using Microsoft.AspNetCore.SignalR;

namespace Overwatch.Services;

/// <summary>
/// Polls each configured camera on an interval, runs detection, records results,
/// and pushes each result to connected dashboards over SignalR. Polling the
/// sources (vs streaming) is a deliberate choice: public DOT cameras only expose
/// still JPEGs, and the Vision backends are rate-limited — see README.
/// </summary>
public class CameraPollingService(
    IHttpClientFactory httpFactory,
    IVisionClient vision,
    DetectionStore store,
    IHubContext<DetectionHub> hub,
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
            var bytes = cam.ImageUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                ? await GrabHlsFrameAsync(cam.ImageUrl, ct)
                : await httpFactory.CreateClient("camera").GetByteArrayAsync(cam.ImageUrl, ct);
            var detections = await vision.DetectObjectsAsync(bytes, ct);
            var frame = new FrameResult(
                cam.Id, DateTimeOffset.UtcNow, Convert.ToBase64String(bytes), detections);
            store.RecordSuccess(cam, frame);
            await hub.Clients.All.SendAsync("frame",
                new { cameraId = cam.Id, capturedAt = frame.CapturedAt, detections }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Camera {Id} poll failed: {Error}", cam.Id, ex.Message);
            store.RecordFailure(cam, ex.Message);
            await hub.Clients.All.SendAsync("feedFault",
                new { cameraId = cam.Id, error = ex.Message }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Pulls a single keyframe from an HLS video stream as JPEG via ffmpeg.
    /// -skip_frame nokey avoids "no frame" failures when the playlist position
    /// lands mid-GOP. The whole grab is capped at 25s so a stalled stream
    /// degrades the feed instead of wedging the poll loop.
    /// </summary>
    private async Task<byte[]> GrabHlsFrameAsync(string url, CancellationToken ct)
    {
        var ffmpeg = config["Ffmpeg:Path"] ?? "ffmpeg";
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg,
            $"-loglevel error -skip_frame nokey -i \"{url}\" -frames:v 1 -q:v 4 -f image2pipe -vcodec mjpeg -")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start ffmpeg at '{ffmpeg}'");
        try
        {
            using var ms = new MemoryStream();
            var copy = proc.StandardOutput.BaseStream.CopyToAsync(ms, timeout.Token);
            var err = proc.StandardError.ReadToEndAsync(timeout.Token);
            await proc.WaitForExitAsync(timeout.Token);
            await copy;
            if (proc.ExitCode != 0 || ms.Length == 0)
                throw new InvalidOperationException(
                    $"ffmpeg exit {proc.ExitCode}: {(await err).Split('\n').FirstOrDefault()?.Trim()}");
            return ms.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException("HLS frame grab timed out after 25s");
        }
    }
}
