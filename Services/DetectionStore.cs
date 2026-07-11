using System.Collections.Concurrent;

namespace Overwatch.Services;

/// <summary>
/// In-memory store: latest frame per camera plus a rolling detection history.
/// Swap for Azure Table Storage / Cosmos DB when deploying — the interface is
/// deliberately shaped so that's a drop-in change.
/// </summary>
public class DetectionStore
{
    private readonly ConcurrentDictionary<string, FrameResult> _latest = new();
    private readonly ConcurrentDictionary<string, CameraStatus> _status = new();
    private readonly ConcurrentQueue<(DateTimeOffset At, string CameraId, string Label)> _history = new();
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(30);

    public void RecordSuccess(CameraConfig cam, FrameResult frame)
    {
        _latest[cam.Id] = frame;
        _status[cam.Id] = new CameraStatus(cam.Id, cam.Name, FeedHealth.Up, frame.CapturedAt, null);
        foreach (var d in frame.Detections)
            _history.Enqueue((frame.CapturedAt, cam.Id, d.Label));
        Trim();
    }

    public void RecordFailure(CameraConfig cam, string error)
    {
        var prev = _status.GetValueOrDefault(cam.Id);
        // One failure = Stale (we still show the last frame); repeated failure
        // with no success in 2 minutes = Down.
        var health = prev?.LastSuccess is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(2)
            ? FeedHealth.Stale
            : FeedHealth.Down;
        _status[cam.Id] = new CameraStatus(cam.Id, cam.Name, health, prev?.LastSuccess, error);
    }

    /// <summary>Detections from a live device camera — counted in stats, no stored frame.</summary>
    public void RecordLiveDetections(string deviceId, IReadOnlyList<Detection> detections)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var d in detections)
            _history.Enqueue((now, deviceId, d.Label));
        Trim();
    }

    public FrameResult? GetLatest(string cameraId) => _latest.GetValueOrDefault(cameraId);

    public IReadOnlyList<CameraStatus> GetStatuses() => _status.Values.OrderBy(s => s.Id).ToList();

    public object GetStats(TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        var recent = _history.Where(h => h.At >= cutoff).ToList();
        return new
        {
            windowMinutes = window.TotalMinutes,
            total = recent.Count,
            byLabel = recent.GroupBy(h => h.Label)
                            .ToDictionary(g => g.Key, g => g.Count()),
            byCamera = recent.GroupBy(h => h.CameraId)
                             .ToDictionary(g => g.Key, g => g.Count())
        };
    }

    private void Trim()
    {
        var cutoff = DateTimeOffset.UtcNow - HistoryWindow;
        while (_history.TryPeek(out var head) && head.At < cutoff)
            _history.TryDequeue(out _);
    }
}
