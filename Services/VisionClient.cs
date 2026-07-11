using System.Text.Json;

namespace Overwatch.Services;

public interface IVisionClient
{
    Task<IReadOnlyList<Detection>> DetectObjectsAsync(byte[] imageBytes, CancellationToken ct);
}

/// <summary>
/// Calls Azure AI Vision v4.0 Image Analysis (objects feature).
/// Free tier (F0): 20 calls/min, 5000/month — plenty for a demo polling cadence.
/// </summary>
public class AzureVisionClient(HttpClient http, IConfiguration config) : IVisionClient
{
    // F0 allows 20 calls/min and throttles bursts. One request at a time with a
    // minimum gap keeps every caller (fixed cameras + live devices) under the
    // limit globally instead of each racing the quota.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _lastCall = DateTimeOffset.MinValue;
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(3.2);

    public async Task<IReadOnlyList<Detection>> DetectObjectsAsync(byte[] imageBytes, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var wait = _lastCall + MinGap - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastCall = DateTimeOffset.UtcNow;
            return await CallVisionAsync(imageBytes, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<IReadOnlyList<Detection>> CallVisionAsync(byte[] imageBytes, CancellationToken ct)
    {
        var endpoint = config["Vision:Endpoint"]!.TrimEnd('/');
        var url = $"{endpoint}/computervision/imageanalysis:analyze?api-version=2024-02-01&features=objects";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("Ocp-Apim-Subscription-Key", config["Vision:Key"]);
        req.Content = new ByteArrayContent(imageBytes);
        req.Content.Headers.ContentType = new("application/octet-stream");

        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var meta = root.GetProperty("metadata");
        double imgW = meta.GetProperty("width").GetInt32();
        double imgH = meta.GetProperty("height").GetInt32();

        var detections = new List<Detection>();
        if (root.TryGetProperty("objectsResult", out var objects))
        {
            foreach (var obj in objects.GetProperty("values").EnumerateArray())
            {
                var box = obj.GetProperty("boundingBox");
                var tag = obj.GetProperty("tags")[0];
                detections.Add(new Detection(
                    tag.GetProperty("name").GetString()!,
                    tag.GetProperty("confidence").GetDouble(),
                    box.GetProperty("x").GetInt32() / imgW,
                    box.GetProperty("y").GetInt32() / imgH,
                    box.GetProperty("w").GetInt32() / imgW,
                    box.GetProperty("h").GetInt32() / imgH));
            }
        }
        return detections;
    }
}

/// <summary>
/// Generates plausible fake detections so the full pipeline and UI can be
/// exercised with no Azure key. Enabled when Vision:Key is not configured.
/// </summary>
public class MockVisionClient : IVisionClient
{
    private static readonly string[] Labels = ["car", "truck", "person", "bus", "bicycle"];
    private readonly Random _rng = new();

    public Task<IReadOnlyList<Detection>> DetectObjectsAsync(byte[] imageBytes, CancellationToken ct)
    {
        var count = _rng.Next(0, 6);
        var list = new List<Detection>(count);
        for (var i = 0; i < count; i++)
        {
            double w = _rng.NextDouble() * 0.15 + 0.05;
            double h = _rng.NextDouble() * 0.15 + 0.05;
            list.Add(new Detection(
                Labels[_rng.Next(Labels.Length)],
                Math.Round(_rng.NextDouble() * 0.45 + 0.5, 2),
                _rng.NextDouble() * (1 - w),
                _rng.NextDouble() * (1 - h),
                w, h));
        }
        return Task.FromResult<IReadOnlyList<Detection>>(list);
    }
}
