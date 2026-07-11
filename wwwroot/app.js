const REFRESH_MS = 5000;
const MAX_LOG_ENTRIES = 60;
const cards = new Map();          // cameraId -> DOM refs
const lastSeen = new Map();       // cameraId -> capturedAt of last logged frame
const lastHealth = new Map();     // cameraId -> last health string

async function fetchJson(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${url} -> ${res.status}`);
  return res.json();
}

/* ---------- Event log ---------- */

function logEvent(text, cls = "") {
  const list = document.getElementById("log-entries");
  const li = document.createElement("li");
  if (cls) li.className = cls;
  const time = new Date().toISOString().slice(11, 19);
  li.innerHTML = `<span class="t">${time}Z</span>${text}`;
  list.prepend(li);
  while (list.children.length > MAX_LOG_ENTRIES) list.lastChild.remove();
}

/* ---------- Camera cards ---------- */

function getCard(cam) {
  if (cards.has(cam.id)) return cards.get(cam.id);
  const tpl = document.getElementById("camera-template");
  const node = tpl.content.firstElementChild.cloneNode(true);
  node.querySelector(".cam-name").textContent = cam.name;
  document.getElementById("cameras").appendChild(node);
  const refs = {
    root: node,
    dot: node.querySelector(".health-dot"),
    age: node.querySelector(".cam-age"),
    img: node.querySelector(".frame"),
    overlay: node.querySelector(".overlay"),
    noSignal: node.querySelector(".no-signal"),
    detections: node.querySelector(".cam-detections"),
  };
  cards.set(cam.id, refs);
  return refs;
}

function drawBoxes(overlay, detections) {
  overlay.innerHTML = "";
  const ns = "http://www.w3.org/2000/svg";
  for (const d of detections) {
    const rect = document.createElementNS(ns, "rect");
    rect.setAttribute("x", d.x);
    rect.setAttribute("y", d.y);
    rect.setAttribute("width", d.w);
    rect.setAttribute("height", d.h);
    overlay.appendChild(rect);

    const text = document.createElementNS(ns, "text");
    text.setAttribute("x", d.x + 0.004);
    text.setAttribute("y", Math.max(d.y - 0.01, 0.03));
    text.textContent = `${d.label} ${(d.confidence * 100).toFixed(0)}%`;
    overlay.appendChild(text);
  }
}

function ageString(iso) {
  if (!iso) return "never";
  const secs = Math.round((Date.now() - new Date(iso)) / 1000);
  return secs < 60 ? `${secs}s ago` : `${Math.round(secs / 60)}m ago`;
}

function trackHealthChange(cam) {
  const prev = lastHealth.get(cam.id);
  if (prev && prev !== cam.health) {
    const cls = cam.health === "Up" ? "detection" : "warn";
    logEvent(`${cam.name}: ${prev} → ${cam.health}`, cls);
  }
  lastHealth.set(cam.id, cam.health);
}

async function updateCamera(cam) {
  const card = getCard(cam);
  card.dot.className = "health-dot " + cam.health.toLowerCase();
  card.age.textContent = ageString(cam.lastSuccess);
  card.noSignal.classList.toggle("hidden", cam.health !== "Down");
  trackHealthChange(cam);

  if (cam.health === "Down") {
    card.detections.textContent = cam.lastError ?? "feed unreachable";
    return;
  }
  try {
    const frame = await fetchJson(`/api/frames/${cam.id}`);
    card.img.src = `data:image/jpeg;base64,${frame.imageBase64}`;
    drawBoxes(card.overlay, frame.detections);
    card.detections.textContent = frame.detections.length
      ? frame.detections.map(d => `${d.label} ${(d.confidence * 100).toFixed(0)}%`).join("  ·  ")
      : "no objects detected";

    // Log each new frame's detections once
    if (lastSeen.get(cam.id) !== frame.capturedAt) {
      lastSeen.set(cam.id, frame.capturedAt);
      if (frame.detections.length) {
        const summary = Object.entries(
          frame.detections.reduce((m, d) => ((m[d.label] = (m[d.label] ?? 0) + 1), m), {})
        ).map(([l, n]) => (n > 1 ? `${n}× ${l}` : l)).join(", ");
        logEvent(`${cam.name}: ${summary}`, "detection");
      }
    }
  } catch {
    /* no frame yet — first poll hasn't completed */
  }
}

/* ---------- Live device camera ---------- */

const LIVE_INTERVAL_MS = 2500;
let liveStream = null;
let liveTimer = null;
let liveFacing = "environment"; // back camera by default; "user" = front

async function openStream() {
  // iOS needs the previous stream fully released before switching cameras.
  liveStream?.getTracks().forEach(t => t.stop());
  liveStream = await navigator.mediaDevices.getUserMedia({
    video: { facingMode: liveFacing, width: { ideal: 1280 } },
    audio: false,
  });
  document.getElementById("live-video").srcObject = liveStream;
}

async function startLive() {
  const status = document.getElementById("live-status");
  if (!navigator.mediaDevices?.getUserMedia) {
    logEvent("live camera unavailable — needs HTTPS (or localhost)", "warn");
    alert("Camera access requires HTTPS or localhost. Open this page via the https:// URL.");
    return;
  }
  try {
    await openStream();
  } catch (err) {
    logEvent(`camera permission denied: ${err.name}`, "warn");
    return;
  }
  document.getElementById("live-panel").classList.remove("hidden");
  document.getElementById("live-cta").classList.add("hidden");
  logEvent("live device camera online", "detection");
  status.textContent = "analyzing…";
  liveTimer = setInterval(analyzeLiveFrame, LIVE_INTERVAL_MS);
}

async function flipLive() {
  if (!liveStream) return;
  liveFacing = liveFacing === "environment" ? "user" : "environment";
  try {
    await openStream();
    document.getElementById("live-overlay").innerHTML = "";
    logEvent(`camera switched to ${liveFacing === "user" ? "front" : "back"}`);
  } catch (err) {
    // Laptops usually have one camera — flip back rather than dying.
    liveFacing = liveFacing === "environment" ? "user" : "environment";
    try { await openStream(); } catch { /* original also gone; STOP still works */ }
    logEvent(`camera switch failed: ${err.name}`, "warn");
  }
}

function stopLive() {
  clearInterval(liveTimer);
  liveStream?.getTracks().forEach(t => t.stop());
  liveStream = null;
  document.getElementById("live-panel").classList.add("hidden");
  document.getElementById("live-cta").classList.remove("hidden");
  logEvent("live device camera stopped");
}

async function analyzeLiveFrame() {
  const video = document.getElementById("live-video");
  if (!liveStream || video.videoWidth === 0) return;

  // Downscale before upload: detection doesn't need full resolution,
  // and it keeps mobile-network round trips fast.
  const scale = Math.min(1, 960 / video.videoWidth);
  const canvas = document.createElement("canvas");
  canvas.width = Math.round(video.videoWidth * scale);
  canvas.height = Math.round(video.videoHeight * scale);
  canvas.getContext("2d").drawImage(video, 0, 0, canvas.width, canvas.height);

  const blob = await new Promise(r => canvas.toBlob(r, "image/jpeg", 0.7));
  const status = document.getElementById("live-status");
  try {
    const res = await fetch("/api/analyze?device=browser", {
      method: "POST",
      headers: { "Content-Type": "application/octet-stream" },
      body: blob,
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const detections = await res.json();
    drawBoxes(document.getElementById("live-overlay"), detections);
    document.getElementById("live-detections").textContent = detections.length
      ? detections.map(d => `${d.label} ${(d.confidence * 100).toFixed(0)}%`).join("  ·  ")
      : "no objects detected";
    status.textContent = `live · ${canvas.width}×${canvas.height}`;
  } catch (err) {
    status.textContent = "analyze failed";
    logEvent(`live analyze failed: ${err.message}`, "warn");
  }
}

/* ---------- Stats + refresh loop ---------- */

async function refresh() {
  try {
    const [cams, stats] = await Promise.all([
      fetchJson("/api/cameras"),
      fetchJson("/api/stats?minutes=10"),
    ]);

    document.getElementById("stat-total").textContent = stats.total;
    document.getElementById("stat-cams-up").textContent =
      `${cams.filter(c => c.health === "Up").length}/${cams.length}`;
    document.getElementById("stat-labels").innerHTML = Object.entries(stats.byLabel)
      .sort((a, b) => b[1] - a[1])
      .map(([label, n]) => `<span class="chip">${label} ${n}</span>`)
      .join("") || `<span class="chip">none yet</span>`;

    await Promise.all(cams.map(updateCamera));
  } catch (err) {
    console.error("refresh failed", err);
  }
}

function tickClock() {
  document.getElementById("clock").textContent =
    new Date().toISOString().slice(11, 19) + " UTC";
}

async function init() {
  document.getElementById("live-btn").addEventListener("click", startLive);
  document.getElementById("live-flip").addEventListener("click", flipLive);
  document.getElementById("live-stop").addEventListener("click", stopLive);
  tickClock();
  setInterval(tickClock, 1000);
  try {
    const mode = await fetchJson("/api/mode");
    const badge = document.getElementById("mode-badge");
    badge.textContent = mode.mock ? "MOCK DETECTIONS" : "AZURE AI VISION";
    badge.classList.add(mode.mock ? "mock" : "live");
    logEvent(`pipeline online — ${mode.mock ? "mock" : "Azure AI Vision"} mode`, "detection");
  } catch { /* non-fatal */ }
  await refresh();
  setInterval(refresh, REFRESH_MS);
}

init();
