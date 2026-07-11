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
