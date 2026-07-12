using Microsoft.AspNetCore.SignalR;
using Overwatch;
using Overwatch.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new()
{
    Title = "Overwatch API",
    Version = "v1",
    Description = "Computer-vision pipeline over live camera feeds. REST for state and analysis; " +
                  "SignalR hub at /hubs/detections pushes frame/feedFault/liveDetections events in realtime.",
}));

builder.Services.AddHttpClient("camera", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient<AzureVisionClient>();
builder.Services.AddSingleton<DetectionStore>();

// Vision provider: "yolo" (local ONNX on GPU/CPU), "azure" (cloud API), "mock",
// or "auto" — azure when a key is configured, mock otherwise.
var hasVisionKey = !string.IsNullOrWhiteSpace(builder.Configuration["Vision:Key"]);
var provider = (builder.Configuration["Vision:Provider"] ?? "auto").ToLowerInvariant();
if (provider == "auto") provider = hasVisionKey ? "azure" : "mock";

builder.Services.AddSingleton<IVisionClient>(sp => provider switch
{
    "yolo" => ActivatorUtilities.CreateInstance<YoloVisionClient>(sp),
    "azure" => sp.GetRequiredService<AzureVisionClient>(),
    _ => new MockVisionClient(),
});

builder.Services.AddHostedService<CameraPollingService>();

var app = builder.Build();

app.Logger.LogInformation("Vision provider: {Provider}", provider);

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseSwagger();
app.UseSwaggerUI();

app.MapHub<DetectionHub>("/hubs/detections");

app.MapGet("/api/cameras", (DetectionStore store) => store.GetStatuses())
   .WithSummary("Health and last-success time for every configured feed");

app.MapGet("/api/frames/{cameraId}", (string cameraId, DetectionStore store) =>
    store.GetLatest(cameraId) is { } frame ? Results.Ok(frame) : Results.NotFound())
   .WithSummary("Latest analyzed frame (base64 JPEG) with its detections");

app.MapGet("/api/stats", (DetectionStore store, int? minutes) =>
    store.GetStats(TimeSpan.FromMinutes(minutes is > 0 and <= 30 ? minutes.Value : 10)))
   .WithSummary("Rolling detection counts by label and by feed");

app.MapGet("/api/mode", () => new { provider, mock = provider == "mock" });

// Live device camera: browser captures a frame, POSTs the JPEG here, gets
// detections back. 4 MB cap — a downscaled camera frame is well under that.
app.MapPost("/api/analyze", async (HttpRequest req, IVisionClient vision, DetectionStore store,
    IHubContext<DetectionHub> hub, CancellationToken ct) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms, ct);
    if (ms.Length == 0 || ms.Length > 4_000_000)
        return Results.BadRequest(new { error = "expected image body up to 4 MB" });

    var detections = await vision.DetectObjectsAsync(ms.ToArray(), ct);
    var device = req.Query.TryGetValue("device", out var d) && !string.IsNullOrWhiteSpace(d)
        ? $"live-{d}" : "live-device";
    store.RecordLiveDetections(device, detections);
    if (detections.Count > 0)
        await hub.Clients.All.SendAsync("liveDetections", new { device, detections }, ct);
    return Results.Ok(detections);
})
.WithSummary("Analyze a posted JPEG through the detection pipeline; returns detections and broadcasts them");

// Liveness/readiness probe for App Service, load balancers, or uptime monitors.
// Reports degraded (503) only if every feed is down — one dead camera is an
// expected condition, not an outage.
app.MapGet("/healthz", (DetectionStore store) =>
{
    var statuses = store.GetStatuses();
    var anyUp = statuses.Count == 0 || statuses.Any(s => s.Health != FeedHealth.Down);
    var body = new { status = anyUp ? "ok" : "degraded", feeds = statuses.Count };
    return anyUp ? Results.Ok(body) : Results.Json(body, statusCode: 503);
});

app.Run();
