namespace Overwatch.Services;

/// <summary>
/// Polls each configured camera on an interval, runs detection, records results.
/// Polling (vs streaming) is a deliberate choice: public DOT cameras only expose
/// still JPEGs, and the Vision free tier is rate-limited — see README.
/// </summary>
public class CameraPollingService(
    IHttpClientFactory httpFactory,
    IVisionClient vision,
    DetectionStore store,
    IConfiguration config,
    ILogger<CameraPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var cameras = config.GetSection("Cameras").Get<List<CameraConfig>>() ?? [];
        var interval = TimeSpan.FromSeconds(config.GetValue("PollIntervalSeconds", 15));
        logger.LogInformation("Polling {Count} cameras every {Interval}s", cameras.Count, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);
        do
        {
            // Cameras are independent; one dead feed must never block the others.
            await Task.WhenAll(cameras.Select(cam => PollOneAsync(cam, ct)));
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task PollOneAsync(CameraConfig cam, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient("camera");
            var bytes = await http.GetByteArrayAsync(cam.ImageUrl, ct);
            var detections = await vision.DetectObjectsAsync(bytes, ct);
            store.RecordSuccess(cam, new FrameResult(
                cam.Id, DateTimeOffset.UtcNow, Convert.ToBase64String(bytes), detections));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Camera {Id} poll failed: {Error}", cam.Id, ex.Message);
            store.RecordFailure(cam, ex.Message);
        }
    }
}
