using Overwatch;
using Overwatch.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddHttpClient("camera", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient<AzureVisionClient>();
builder.Services.AddSingleton<DetectionStore>();

// Mock mode when no Vision key is configured — full pipeline runs with fake detections.
var hasVisionKey = !string.IsNullOrWhiteSpace(builder.Configuration["Vision:Key"]);
if (hasVisionKey)
    builder.Services.AddSingleton<IVisionClient>(sp => sp.GetRequiredService<AzureVisionClient>());
else
    builder.Services.AddSingleton<IVisionClient, MockVisionClient>();

builder.Services.AddHostedService<CameraPollingService>();

var app = builder.Build();

app.Logger.LogInformation("Vision mode: {Mode}", hasVisionKey ? "Azure AI Vision" : "MOCK (no Vision:Key configured)");

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/cameras", (DetectionStore store) => store.GetStatuses());

app.MapGet("/api/frames/{cameraId}", (string cameraId, DetectionStore store) =>
    store.GetLatest(cameraId) is { } frame ? Results.Ok(frame) : Results.NotFound());

app.MapGet("/api/stats", (DetectionStore store, int? minutes) =>
    store.GetStats(TimeSpan.FromMinutes(minutes is > 0 and <= 30 ? minutes.Value : 10)));

app.MapGet("/api/mode", () => new { mock = !hasVisionKey });

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
