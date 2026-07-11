namespace Overwatch;

public record CameraConfig(string Id, string Name, string ImageUrl);

public enum FeedHealth { Up, Stale, Down }

public record Detection(
    string Label,
    double Confidence,
    // Bounding box normalized to 0..1 so the frontend can scale to any render size
    double X, double Y, double W, double H);

public record FrameResult(
    string CameraId,
    DateTimeOffset CapturedAt,
    string ImageBase64,          // last frame as JPEG base64 (kept in memory only)
    IReadOnlyList<Detection> Detections);

public record CameraStatus(
    string Id,
    string Name,
    FeedHealth Health,
    DateTimeOffset? LastSuccess,
    string? LastError);
