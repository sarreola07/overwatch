# Overwatch — Cloud Computer Vision over Live Camera Feeds

A miniature ISR-style pipeline built from civilian parts: unreliable public camera
feeds → cloud object detection → operator dashboard with feed-health monitoring
and rolling statistics.

**Stack:** ASP.NET Core 8 (minimal API + BackgroundService) · Azure AI Vision
(Image Analysis v4.0, free tier) · vanilla JS dashboard · deploys to Azure App
Service free tier.

## Architecture

```
[Public camera JPEGs] --poll--> [CameraPollingService] --frame--> [Azure AI Vision]
                                        |                              |
                                        v                              v
                                 [DetectionStore] <---- detections + boxes
                                        |
                                        v
                        [/api/*] ----> [Dashboard: frames, boxes, health, stats]
```

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

**What does this cost at 100 cameras?** At 15s polling: 400 calls/min — past the
free tier (20/min) and past S1 (10/min per TPS default, raisable). The bottleneck
is the Vision API, not compute. Options in order: lower cadence per camera,
motion-gating (only send frames that changed, cheap diff locally), or move
inference in-house (YOLO in a container on Azure Container Apps) which flips the
cost from per-call to per-vCPU.

## Roadmap

- [ ] Persist detection history to Azure Table Storage
- [ ] Operator-configurable alert rules ("notify when > N objects on cam X")
- [ ] Motion-gating to cut Vision API spend
- [ ] Deploy to Azure App Service (free F1) with GitHub Actions CI
