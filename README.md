# Overwatch — Computer Vision Pipeline over Live Camera Feeds

**Live demo: https://overwatch-sarreola.azurewebsites.net** · MIT licensed · [![build](https://github.com/sarreola07/overwatch/actions/workflows/build.yml/badge.svg)](https://github.com/sarreola07/overwatch/actions)

A miniature ISR-style pipeline built from civilian parts: unreliable public camera
feeds → object detection (cloud API or local GPU) → realtime operator dashboard
with feed-health monitoring, rolling statistics, and live device cameras.

**Stack:** ASP.NET Core 8 (minimal API + BackgroundService + SignalR) · pluggable
detection backends (local YOLOv8 via ONNX Runtime/DirectML, Azure AI Vision, mock)
· vanilla JS dashboard · Swagger/OpenAPI · deploys to Azure App Service.

## API

Interactive documentation at **`/swagger`** on any running instance.

| Surface | What it does |
|---|---|
| `GET /api/cameras` | Health + last-success time for every feed |
| `GET /api/frames/{id}` | Latest analyzed frame (base64 JPEG) with detections |
| `GET /api/stats?minutes=10` | Rolling detection counts by label and feed |
| `POST /api/analyze` | Run any posted JPEG through the pipeline |
| `GET /healthz` | Liveness probe (503 only if *every* feed is down) |
| `WS /hubs/detections` | SignalR: `frame`, `feedFault`, `liveDetections` pushed in realtime |

The dashboard is realtime-first with graceful degradation: it renders from
SignalR pushes the moment a frame is analyzed, keeps a slow 30s safety-net poll
while the socket is healthy, and automatically falls back to 5s polling if the
socket drops — the event log announces each transition.

## Architecture

```
[Public camera JPEGs] --poll--> [CameraPollingService] --frame--> [IVisionClient]
[Phone/laptop camera] --POST /api/analyze ------------------------>    |
                                        |                    ┌─────────┴─────────┐
                                        v                    | YoloVisionClient  | local ONNX, GPU
                                 [DetectionStore] <--------- | AzureVisionClient | cloud API
                                        |                    | MockVisionClient  | no dependencies
                                        v                    └───────────────────┘
                        [/api/*] ----> [Dashboard: frames, boxes, health, stats]
```

Set `Vision:Provider` in appsettings.json: `yolo` (local inference), `azure`
(cloud API), `mock`, or `auto` (azure if a key is configured, else mock).

### Local YOLO backend

`Vision:Provider = "yolo"` runs a COCO-pretrained YOLOv8-small ONNX model
in-process via ONNX Runtime, using the GPU through DirectML (CPU fallback is
automatic). Measured ~40ms per frame end-to-end on an RTX 4080 SUPER vs
~500-800ms per Azure API round trip — and no per-call cost or rate limit.

The model file is not committed (43 MB). Download it once:

```powershell
Invoke-WebRequest -Uri "https://huggingface.co/orirdx/yolov8s-coco-onnx/resolve/main/coco_yolov8s.onnx" -OutFile models\yolov8s.onnx
```

The client handles the full inference pipeline in C#: letterbox resize to
640×640, RGB→CHW float tensor, session run, then decoding the [1,84,8400]
output (4 box coords + 80 COCO class scores per anchor), confidence filtering,
and non-max suppression. Both YOLOv8 ([1,84,N]) and transposed/v5-style
([1,N,85]) output layouts are handled.

## Run it

```
cd Overwatch
dotnet run
```

Open the printed localhost URL. With no Azure key configured it runs in **mock
mode** — real polling and real feed-health handling, fake detections — so the
whole system is demoable offline.

### Enable real detection

1. Create an **Azure AI services / Computer Vision** resource (F0 free tier).
2. Set secrets (don't put the key in appsettings.json):
   ```
   dotnet user-secrets init
   dotnet user-secrets set "Vision:Endpoint" "https://<your-resource>.cognitiveservices.azure.com"
   dotnet user-secrets set "Vision:Key" "<your-key>"
   ```
3. Replace the demo camera URLs in `appsettings.json` with real public still-image
   feeds (state DOT traffic cameras publish JPEG endpoints). Keep `cam-3` broken
   on purpose — it demonstrates degraded-feed handling.

## Demo from your phone or laptop (live device camera)

The dashboard has a **GO LIVE** button that streams frames from the visitor's
own camera through the same detection pipeline (browser → `/api/analyze` →
Vision client). Frames are analyzed and discarded, never stored.

To demo on other devices on your Wi-Fi:

1. Run the server bound to all interfaces (the repo's launch config already does):
   ```
   dotnet run --urls "http://0.0.0.0:5170;https://0.0.0.0:5171"
   ```
2. Allow the ports through Windows Firewall once (admin PowerShell):
   ```
   New-NetFirewallRule -DisplayName "Overwatch" -Direction Inbound -Protocol TCP -LocalPort 5170,5171 -Action Allow
   ```
3. On the phone/laptop, open `https://<this-PC's-IP>:5171` (find the IP with
   `ipconfig`). **Camera access requires HTTPS on iOS/Safari** — accept the
   self-signed dev-certificate warning for the demo. A deployed App Service URL
   has real TLS and no warning.

## Design decisions (the questions an engineer would ask)

**Why poll instead of stream?** The sources only expose still JPEGs, and the
Vision free tier allows 20 calls/min. Polling with a `PeriodicTimer` gives
predictable, budgetable API consumption. At 3 cameras / 15s that's 12 calls/min —
inside the limit with headroom.

**What happens when a feed dies?** Nothing else stops. Each camera is polled
independently (`Task.WhenAll` with per-camera try/catch). A failed feed degrades
Up → Stale (last good frame still shown, with its age) → Down after 2 minutes
without a success (NO SIGNAL, error surfaced to the operator). Data age is always
displayed — an operator must never mistake stale data for live data.

**How are low-confidence detections handled?** They're shown *with* their
confidence score rather than silently filtered. In an operator context, hiding
uncertain detections is a product decision, not an engineering default; the
threshold belongs in the UI layer where the operator can see what they're
trading off.

**Why is storage in-memory?** Deliberate weekend scope cut. `DetectionStore` is a
singleton behind a narrow interface; swapping to Azure Table Storage (history) is
a drop-in change and the first thing on the roadmap.

**What does this cost at 100 cameras?** On the Azure backend at 15s polling:
400 calls/min — past both the F0 free tier (20/min) and S1 defaults, at $1 per
1000 calls. That ceiling is why the local YOLO backend exists: same interface,
~40ms per frame on a consumer GPU, zero marginal cost. The project lived this
arc for real — F0 429s → S1 (faster but metered) → local ONNX inference — and
the `IVisionClient` abstraction meant each step was a config change, not a
rewrite. Cloud deployments without a GPU still use the Azure backend; the
tradeoff is per-call cost vs owning inference hardware.

## Roadmap

- [ ] Persist detection history to Azure Table Storage
- [ ] Operator-configurable alert rules ("notify when > N objects on cam X")
- [ ] Motion-gating to cut Vision API spend
- [ ] Deploy to Azure App Service (free F1) with GitHub Actions CI
