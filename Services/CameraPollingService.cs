using Microsoft.AspNetCore.SignalR;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

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
            byte[] bytes;
            try
            {
                bytes = await FetchFrameAsync(cam.ImageUrl, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && cam.FallbackUrl is not null)
            {
                // Primary sensor denied or dead — fail over to the alternate source
                // rather than taking the feed dark.
                logger.LogWarning("Camera {Id} primary source failed ({Error}); using fallback source",
                    cam.Id, ex.Message);
                bytes = await FetchFrameAsync(cam.FallbackUrl, ct);
            }
            var detections = await vision.DetectObjectsAsync(bytes, ct);
            // Detection runs on the full-resolution frame; the stored copy is for
            // display only, so downscale it — a 1080p frame is ~375KB of base64
            // that every dashboard would otherwise re-download each refresh.
            var frame = new FrameResult(
                cam.Id, DateTimeOffset.UtcNow, Convert.ToBase64String(ToDisplayJpeg(bytes)), detections);
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

    private static byte[] ToDisplayJpeg(byte[] original)
    {
        using var image = SixLabors.ImageSharp.Image.Load(original);
        if (image.Width <= 960) return original;
        image.Mutate(x => x.Resize(960, 0));
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 74 });
        return ms.ToArray();
    }

    /// <summary>
    /// Fetches one frame from a source URL: YouTube live pages resolve to their
    /// time-limited HLS manifest first (cache dropped on failure so the next
    /// poll re-resolves), .m3u8 streams go through ffmpeg, anything else is a
    /// plain HTTP image GET.
    /// </summary>
    private async Task<byte[]> FetchFrameAsync(string url, CancellationToken ct)
    {
        var isYoutube = IsYoutube(url);
        var sourceUrl = isYoutube ? await ResolveYoutubeStreamAsync(url, ct) : url;
        try
        {
            return isYoutube || sourceUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                ? await GrabHlsFrameAsync(sourceUrl, ct)
                : await httpFactory.CreateClient("camera").GetByteArrayAsync(sourceUrl, ct);
        }
        catch when (isYoutube)
        {
            _resolvedStreams.TryRemove(url, out _);
            throw;
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Url, DateTimeOffset At)> _resolvedStreams = new();
    private static readonly TimeSpan ResolveTtl = TimeSpan.FromHours(4);

    private static bool IsYoutube(string url) =>
        url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a YouTube live URL to its current HLS manifest via yt-dlp.
    /// Manifests expire after ~6h, so results are cached for 4 and invalidated
    /// early if a frame grab fails.
    /// </summary>
    private async Task<string> ResolveYoutubeStreamAsync(string url, CancellationToken ct)
    {
        if (_resolvedStreams.TryGetValue(url, out var hit) && DateTimeOffset.UtcNow - hit.At < ResolveTtl)
            return hit.Url;

        var ytdlp = config["YtDlp:Path"] ?? "yt-dlp";
        var psi = new System.Diagnostics.ProcessStartInfo(ytdlp, $"-g --no-warnings \"{url}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start yt-dlp at '{ytdlp}'");
        try
        {
            var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
            await proc.WaitForExitAsync(timeout.Token);
            var resolved = (await stdout).Split('\n').FirstOrDefault()?.Trim();
            if (proc.ExitCode != 0 || string.IsNullOrEmpty(resolved))
                throw new InvalidOperationException(
                    $"yt-dlp exit {proc.ExitCode}: {(await stderr).Split('\n').FirstOrDefault()?.Trim()}");
            _resolvedStreams[url] = (resolved, DateTimeOffset.UtcNow);
            return resolved;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException("YouTube stream resolution timed out after 45s");
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
