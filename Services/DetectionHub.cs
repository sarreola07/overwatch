using Microsoft.AspNetCore.SignalR;

namespace Overwatch.Services;

/// <summary>
/// Server → client realtime channel. The server broadcasts:
///   "frame"          { cameraId, capturedAt, detections } — a fixed feed was analyzed
///   "feedFault"      { cameraId, error }                  — a fixed feed failed a poll
///   "liveDetections" { device, detections }               — a live device camera frame was analyzed
/// Clients render instantly instead of waiting for the next poll; HTTP polling
/// remains as the degraded-mode fallback when the socket is down.
/// </summary>
public class DetectionHub : Hub;
