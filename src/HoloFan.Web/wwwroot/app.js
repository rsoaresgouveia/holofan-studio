"use strict";

// --- State ------------------------------------------------------------------
const state = {
  uploadId: null,
  srcW: 0,
  srcH: 0,
  duration: 0,
  fps: 0,
  frame: null,          // HTMLImageElement of the current backdrop frame
  crop: { x: 0, y: 0, side: 0 },
  drag: null,           // { startX, startY, cropX, cropY }
};

const $ = (id) => document.getElementById(id);
const editorCanvas = $("editorCanvas");
const ectx = editorCanvas.getContext("2d");
const previewCanvas = $("previewCanvas");
const pctx = previewCanvas.getContext("2d");

// --- Helpers ----------------------------------------------------------------
function toast(msg, isError = false) {
  const el = $("toast");
  el.textContent = msg;
  el.classList.toggle("error", isError);
  el.classList.remove("hidden");
  clearTimeout(toast._t);
  toast._t = setTimeout(() => el.classList.add("hidden"), 4000);
}

async function api(url, opts) {
  const res = await fetch(url, opts);
  const isJson = (res.headers.get("content-type") || "").includes("application/json");
  const body = isJson ? await res.json() : await res.blob();
  if (!res.ok) {
    const msg = (body && body.error) ? body.error : `Request failed (${res.status})`;
    const details = body && body.details ? `: ${body.details.join("; ")}` : "";
    throw new Error(msg + details);
  }
  return body;
}

const maxSide = () => Math.min(state.srcW, state.srcH);
const minSide = () => Math.max(16, Math.round(maxSide() * 0.15));

// Fit the source frame into the (square) canvas, returning the draw rectangle.
function fitRect() {
  const cw = editorCanvas.width, ch = editorCanvas.height;
  const scale = Math.min(cw / state.srcW, ch / state.srcH);
  const dispW = state.srcW * scale, dispH = state.srcH * scale;
  return { scale, x: (cw - dispW) / 2, y: (ch - dispH) / 2, w: dispW, h: dispH };
}

function clampCrop() {
  const c = state.crop;
  c.side = Math.min(Math.max(c.side, minSide()), maxSide());
  c.x = Math.min(Math.max(0, c.x), state.srcW - c.side);
  c.y = Math.min(Math.max(0, c.y), state.srcH - c.side);
}

// --- Rendering --------------------------------------------------------------
function render() {
  if (!state.frame) return;
  const r = fitRect();
  ectx.clearRect(0, 0, editorCanvas.width, editorCanvas.height);
  ectx.drawImage(state.frame, r.x, r.y, r.w, r.h);

  const c = state.crop;
  const cx = r.x + c.x * r.scale, cy = r.y + c.y * r.scale, cs = c.side * r.scale;

  // Dim everything outside the crop square.
  ectx.save();
  ectx.fillStyle = "rgba(3,5,11,0.72)";
  ectx.beginPath();
  ectx.rect(0, 0, editorCanvas.width, editorCanvas.height);
  ectx.rect(cx, cy, cs, cs);
  ectx.fill("evenodd");
  ectx.restore();

  // Crop square outline.
  ectx.strokeStyle = "#5eead4";
  ectx.lineWidth = 2;
  ectx.strokeRect(cx, cy, cs, cs);

  // Inscribed circle = the fan's visible area.
  if ($("circularMask").checked) {
    ectx.beginPath();
    ectx.arc(cx + cs / 2, cy + cs / 2, cs / 2, 0, Math.PI * 2);
    ectx.strokeStyle = "#a78bfa";
    ectx.setLineDash([6, 6]);
    ectx.stroke();
    ectx.setLineDash([]);
  }

  renderPreview();
}

function renderPreview() {
  if (!state.frame) return;
  const size = previewCanvas.width;
  const c = state.crop;
  const bg = $("background").value;
  const masked = $("circularMask").checked;

  pctx.clearRect(0, 0, size, size);
  pctx.fillStyle = bg;
  pctx.fillRect(0, 0, size, size);

  pctx.save();
  if (masked) {
    pctx.beginPath();
    pctx.arc(size / 2, size / 2, size / 2, 0, Math.PI * 2);
    pctx.clip();
  }
  const b = 1 + parseFloat($("brightness").value);
  const contrast = parseFloat($("contrast").value);
  const sat = parseFloat($("saturation").value);
  pctx.filter = `brightness(${b}) contrast(${contrast}) saturate(${sat})`;
  pctx.drawImage(state.frame, c.x, c.y, c.side, c.side, 0, 0, size, size);
  pctx.restore();
}

// --- Canvas interaction -----------------------------------------------------
function canvasPoint(ev) {
  const rect = editorCanvas.getBoundingClientRect();
  const sx = editorCanvas.width / rect.width, sy = editorCanvas.height / rect.height;
  return { x: (ev.clientX - rect.left) * sx, y: (ev.clientY - rect.top) * sy };
}

editorCanvas.addEventListener("pointerdown", (ev) => {
  if (!state.frame) return;
  editorCanvas.setPointerCapture(ev.pointerId);
  const p = canvasPoint(ev);
  state.drag = { startX: p.x, startY: p.y, cropX: state.crop.x, cropY: state.crop.y };
});
editorCanvas.addEventListener("pointermove", (ev) => {
  if (!state.drag) return;
  const p = canvasPoint(ev);
  const r = fitRect();
  state.crop.x = state.drag.cropX - (p.x - state.drag.startX) / r.scale;
  state.crop.y = state.drag.cropY - (p.y - state.drag.startY) / r.scale;
  clampCrop();
  render();
});
const endDrag = () => (state.drag = null);
editorCanvas.addEventListener("pointerup", endDrag);
editorCanvas.addEventListener("pointercancel", endDrag);

editorCanvas.addEventListener("wheel", (ev) => {
  if (!state.frame) return;
  ev.preventDefault();
  const delta = ev.deltaY > 0 ? 1 : -1;
  const cur = parseFloat($("zoom").value);
  $("zoom").value = Math.min(100, Math.max(0, cur + delta * 4));
  applyZoom();
}, { passive: false });

function applyZoom() {
  const f = parseFloat($("zoom").value) / 100;
  const newSide = maxSide() - f * (maxSide() - minSide());
  const c = state.crop;
  const centerX = c.x + c.side / 2, centerY = c.y + c.side / 2;
  c.side = newSide;
  c.x = centerX - newSide / 2;
  c.y = centerY - newSide / 2;
  clampCrop();
  $("zoomVal").textContent = `${Math.round(f * 100)}%`;
  render();
}

// --- Upload -----------------------------------------------------------------
async function handleFile(file) {
  if (!file) return;
  toast("Uploading & analysing…");
  const fd = new FormData();
  fd.append("file", file);
  try {
    const info = await api("/api/uploads", { method: "POST", body: fd });
    state.uploadId = info.uploadId;
    state.srcW = info.width;
    state.srcH = info.height;
    state.duration = info.duration || 0;
    state.fps = info.fps || 0;

    // Default framing: the largest centred square.
    state.crop.side = maxSide();
    state.crop.x = (state.srcW - state.crop.side) / 2;
    state.crop.y = (state.srcH - state.crop.side) / 2;

    $("scrub").max = Math.max(0, state.duration).toFixed(1);
    if (state.fps > 0) $("fps").value = Math.min(120, Math.round(state.fps));

    $("sourceMeta").innerHTML = `
      <dt>Resolution</dt><dd>${state.srcW}×${state.srcH}</dd>
      <dt>Duration</dt><dd>${state.duration.toFixed(1)}s</dd>
      <dt>Source fps</dt><dd>${state.fps ? state.fps.toFixed(1) : "—"}</dd>`;

    $("dropzone").classList.add("hidden");
    $("editor").classList.remove("hidden");
    await loadFrame(0);
    toast("Ready — frame the circle and convert.");
  } catch (e) {
    toast(e.message, true);
  }
}

function loadFrame(t) {
  return new Promise((resolve) => {
    const img = new Image();
    img.onload = () => { state.frame = img; render(); resolve(); };
    img.onerror = () => { toast("Could not load a preview frame.", true); resolve(); };
    img.src = `/api/uploads/${state.uploadId}/frame?t=${encodeURIComponent(t)}`;
  });
}

// --- Convert ----------------------------------------------------------------
function buildRequest() {
  const c = state.crop;
  const trimStart = $("trimStart").value === "" ? null : parseFloat($("trimStart").value);
  const trimEnd = $("trimEnd").value === "" ? null : parseFloat($("trimEnd").value);
  let outputSize = parseInt($("outputSize").value, 10);
  if (outputSize % 2 !== 0) outputSize += 1;
  return {
    cropX: Math.round(c.x),
    cropY: Math.round(c.y),
    cropSide: Math.round(c.side),
    outputSize,
    fps: parseInt($("fps").value, 10),
    speed: parseFloat($("speed").value),
    circularMask: $("circularMask").checked,
    background: $("background").value,
    trimStart,
    trimEnd,
    brightness: parseFloat($("brightness").value),
    contrast: parseFloat($("contrast").value),
    saturation: parseFloat($("saturation").value),
    deviceModelId: "42-F2",
  };
}

async function convert() {
  const btn = $("convertBtn");
  const format = $("outputFormat").value;
  btn.disabled = true;
  $("result").classList.remove("hidden");
  $("resultReady").classList.add("hidden");
  setProgress(0, "Queued…");
  try {
    // ".bin" is the fan's own container — packed here, no vendor software needed.
    const endpoint = format === "bin" ? "bin" : "convert";
    const { jobId } = await api(`/api/uploads/${state.uploadId}/${endpoint}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(buildRequest()),
    });
    await pollJob(jobId, format);
  } catch (e) {
    toast(e.message, true);
    setProgress(0, "Failed.");
  } finally {
    btn.disabled = false;
  }
}

function setProgress(frac, label) {
  $("progressFill").style.width = `${Math.round(frac * 100)}%`;
  $("progressLabel").textContent = label ?? `${Math.round(frac * 100)}%`;
}

function pollJob(jobId, format = "mp4") {
  return new Promise((resolve, reject) => {
    const tick = async () => {
      try {
        const job = await api(`/api/jobs/${jobId}`);
        if (job.status === "running" || job.status === "queued") {
          setProgress(job.progress || 0, `Rendering… ${Math.round((job.progress || 0) * 100)}%`);
          setTimeout(tick, 500);
        } else if (job.status === "completed") {
          setProgress(1, "Done ✨");
          const url = `/api/jobs/${jobId}/download`;
          const isBin = format === "bin";
          // A .bin is device bytes — nothing a browser can play, so only offer the download.
          $("resultVideo").classList.toggle("hidden", isBin);
          if (isBin) $("resultVideo").removeAttribute("src");
          else $("resultVideo").src = url;
          $("downloadLink").textContent = isBin ? "Download .bin (copy to the fan's SD card)" : "Download MP4";
          $("downloadLink").href = url;
          // A .bin can also be pushed over WiFi if the fan is connected.
          state.lastBinJob = isBin ? jobId : null;
          const canSend = isBin && $("fanDot").classList.contains("on");
          $("sendToFan").classList.toggle("hidden", !canSend);
          $("resultReady").classList.remove("hidden");
          resolve();
        } else {
          reject(new Error(job.error || "Conversion failed."));
        }
      } catch (e) { reject(e); }
    };
    tick();
  });
}

// --- Wiring -----------------------------------------------------------------
async function loadPresets() {
  try {
    const presets = await api("/api/presets");
    const sel = $("preset");
    sel.innerHTML = `<option value="">Custom</option>` +
      presets.map((p) => `<option value="${p.id}" data-size="${p.resolution}" data-fps="${p.fps}">${p.name} · ${p.resolution}px</option>`).join("");
    sel.addEventListener("change", () => {
      const opt = sel.selectedOptions[0];
      if (!opt.value) return;
      $("outputSize").value = opt.dataset.size;
      $("fps").value = opt.dataset.fps;
    });
  } catch { /* presets are non-critical */ }
}

function bindControls() {
  $("browseBtn").addEventListener("click", () => $("fileInput").click());
  $("dropzone").addEventListener("click", (e) => { if (e.target.id !== "browseBtn") $("fileInput").click(); });
  $("fileInput").addEventListener("change", (e) => handleFile(e.target.files[0]));

  const dz = $("dropzone");
  ["dragover", "dragenter"].forEach((ev) => dz.addEventListener(ev, (e) => { e.preventDefault(); dz.classList.add("drag"); }));
  ["dragleave", "drop"].forEach((ev) => dz.addEventListener(ev, () => dz.classList.remove("drag")));
  dz.addEventListener("drop", (e) => { e.preventDefault(); handleFile(e.dataTransfer.files[0]); });

  $("zoom").addEventListener("input", applyZoom);
  $("centerBtn").addEventListener("click", () => {
    state.crop.x = (state.srcW - state.crop.side) / 2;
    state.crop.y = (state.srcH - state.crop.side) / 2;
    clampCrop(); render();
  });
  $("fitBtn").addEventListener("click", () => {
    $("zoom").value = 0; applyZoom();
    state.crop.x = (state.srcW - state.crop.side) / 2;
    state.crop.y = (state.srcH - state.crop.side) / 2;
    clampCrop(); render();
  });

  $("circularMask").addEventListener("change", render);
  $("background").addEventListener("input", renderPreview);
  ["brightness", "contrast", "saturation"].forEach((id) => {
    $(id).addEventListener("input", () => {
      $(`${id}Val`).textContent = id === "brightness"
        ? parseFloat($(id).value).toFixed(2)
        : parseFloat($(id).value).toFixed(1);
      renderPreview();
    });
  });
  $("speed").addEventListener("input", () => $("speedVal").textContent = `${parseFloat($("speed").value).toFixed(1)}×`);

  let scrubTimer;
  $("scrub").addEventListener("input", () => {
    const t = parseFloat($("scrub").value);
    $("scrubVal").textContent = t.toFixed(1);
    clearTimeout(scrubTimer);
    scrubTimer = setTimeout(() => loadFrame(t), 150);
  });

  $("convertBtn").addEventListener("click", convert);
  $("resetBtn").addEventListener("click", () => location.reload());
}

// --- Modal + destructive-action security ------------------------------------
// A tiny promise-free modal: fields become inputs; onOk returns true to keep it open.
function showModal({ title, message, fields, okLabel, danger, onOk }) {
  $("modalTitle").textContent = title;
  $("modalMsg").textContent = message || "";
  const box = $("modalFields");
  box.innerHTML = "";
  const inputs = {};
  (fields || []).forEach((f) => {
    const label = document.createElement("label");
    label.textContent = f.label;
    const input = document.createElement("input");
    input.type = f.type || "password";
    if (f.placeholder) input.placeholder = f.placeholder;
    label.appendChild(input);
    box.appendChild(label);
    inputs[f.key] = input;
  });
  const ok = $("modalOk");
  ok.textContent = okLabel || "Confirm";
  ok.classList.toggle("danger-btn", !!danger);
  const err = $("modalError");
  err.classList.add("hidden");
  err.textContent = "";
  $("modal").classList.remove("hidden");
  const first = box.querySelector("input");
  if (first) setTimeout(() => first.focus(), 40);

  // Errors show INSIDE the modal (not as a toast hidden behind it).
  const fail = (msg) => { err.textContent = msg; err.classList.remove("hidden"); };
  const close = () => { $("modal").classList.add("hidden"); ok.onclick = null; $("modalCancel").onclick = null; };
  $("modalCancel").onclick = close;
  ok.onclick = async () => {
    err.classList.add("hidden");
    const values = {};
    Object.entries(inputs).forEach(([k, el]) => (values[k] = el.value));
    const keepOpen = await onOk(values, fail);
    if (!keepOpen) close();
  };
  // Enter submits.
  box.querySelectorAll("input").forEach((el) =>
    el.addEventListener("keydown", (e) => { if (e.key === "Enter") ok.click(); }));
}

async function confirmDestructive(cmd, label) {
  let status;
  try { status = await api("/api/security/status"); } catch { status = { passphraseSet: false }; }

  if (!status.passphraseSet) {
    showModal({
      title: "🔒 Set an admin passphrase",
      message: "Destructive actions (Format disk, Clear cache) need an admin passphrase. It is stored only on the device (hashed), never in the cloud or on GitHub.",
      fields: [
        { key: "new", label: "New passphrase" },
        { key: "confirm", label: "Confirm passphrase" },
      ],
      okLabel: "Set passphrase",
      onOk: async (v, fail) => {
        if (!v.new || v.new !== v.confirm) { fail("Passphrases don't match."); return true; }
        try {
          await api("/api/security/passphrase", {
            method: "POST", headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ current: null, newPassphrase: v.new }),
          });
          toast("Passphrase set. Click the action again to confirm.");
          return false;
        } catch (e) { fail(e.message); return true; }
      },
    });
    return;
  }

  showModal({
    title: `⚠ ${label}`,
    message: cmd === "FormatDisk"
      ? "This ERASES every clip on the fan's card and cannot be undone. Type the admin passphrase to confirm."
      : "This deletes files the device considers junk. Type the admin passphrase to confirm.",
    fields: [{ key: "pass", label: "Admin passphrase" }],
    okLabel: `Yes, ${label}`,
    danger: true,
    onOk: async (v, fail) => {
      try {
        await api("/api/fan/command", {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ command: cmd, confirmDestructive: true, passphrase: v.pass }),
        });
        toast(`Sent: ${label}`);
        refreshPlaylist();
        return false;
      } catch (e) { fail(e.message); return true; }  // wrong passphrase → keep open (error in modal)
    },
  });
}

async function managePassphrase() {
  let status;
  try { status = await api("/api/security/status"); } catch { status = { passphraseSet: false }; }
  const set = status.passphraseSet;
  showModal({
    title: set ? "🔒 Change admin passphrase" : "🔒 Set admin passphrase",
    message: "Protects Format disk / Clear cache. Stored hashed on the device only — never in the cloud or GitHub.",
    fields: [
      ...(set ? [{ key: "current", label: "Current passphrase" }] : []),
      { key: "new", label: "New passphrase" },
      { key: "confirm", label: "Confirm passphrase" },
    ],
    okLabel: "Save",
    onOk: async (v, fail) => {
      if (!v.new || v.new !== v.confirm) { fail("Passphrases don't match."); return true; }
      try {
        await api("/api/security/passphrase", {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ current: v.current || null, newPassphrase: v.new }),
        });
        toast("Passphrase saved.");
        return false;
      } catch (e) { fail(e.message); return true; }
    },
  });
}

// --- Fan remote -------------------------------------------------------------
function setFanState(connected, label) {
  $("fanDot").classList.toggle("on", connected);
  $("fanState").textContent = label ?? (connected ? "Connected" : "Unconnected");
  $("fanConnect").textContent = connected ? "Disconnect" : "Connect";
  document.querySelectorAll(".fan-btn").forEach((b) => (b.disabled = !connected));
  document.querySelectorAll("#clockEnabled, #clockNeedle, #clockDial, #duration, #applyDuration")
    .forEach((s) => (s.disabled = !connected));
  $("fanStatus").classList.toggle("hidden", !connected);
  if (!connected) $("powerBadge").classList.add("hidden");
}

function setPower(poweredOn) {
  const b = $("powerBadge");
  if (poweredOn === null || poweredOn === undefined) { b.classList.add("hidden"); return; }
  b.classList.remove("hidden");
  b.classList.toggle("on", poweredOn);
  b.classList.toggle("off", !poweredOn);
  b.textContent = poweredOn ? "⏻ On" : "⏻ Standby";
}

function renderPlaylist(files, poweredOn) {
  setPower(poweredOn);
  $("clipCount").textContent = files && files.length ? `(${files.length})` : "";
  const ul = $("playlist");
  if (!files || !files.length) {
    ul.innerHTML = `<li class="empty">no clips</li>`;
    return;
  }
  ul.innerHTML = files
    .map((f, i) => `<li><span class="idx">${i + 1}</span><span>${escapeHtml(f)}</span></li>`)
    .join("");
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

// Passive render from the server's cached state (connect-time reply).
async function loadPlaylist() {
  if (!$("fanDot").classList.contains("on")) return;
  try {
    const p = await api("/api/fan/playlist");
    renderPlaylist(p.files, p.poweredOn);
  } catch { /* transient */ }
}

// Reconnect first so power/list reflect the current device state, then render.
async function refreshPlaylist() {
  if (!$("fanDot").classList.contains("on")) return;
  try { await api("/api/fan/connect", { method: "POST" }); } catch { /* keep going */ }
  await loadPlaylist();
}

async function refreshFan() {
  try {
    const s = await api("/api/fan/status");
    setFanState(s.connected);
    if (s.connected) { setPower(s.poweredOn); loadPlaylist(); }
  } catch { setFanState(false); }
}

function bindFan() {
  $("fanConnect").addEventListener("click", async () => {
    const connected = $("fanDot").classList.contains("on");
    $("fanConnect").disabled = true;
    try {
      if (connected) {
        await api("/api/fan/disconnect", { method: "POST" });
        setFanState(false);
        toast("Disconnected from the fan.");
      } else {
        setFanState(false, "Connecting…");
        const r = await api("/api/fan/connect", { method: "POST" });
        setFanState(true);
        toast(`Connected to the fan at ${r.host}:${r.port}.`);
        loadPlaylist();
      }
    } catch (e) {
      setFanState(false);
      toast(e.message, true);
    } finally { $("fanConnect").disabled = false; }
  });

  document.querySelectorAll(".fan-btn").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const cmd = btn.dataset.cmd;
      // Format disk / clear cache wipe content — gate them behind the admin passphrase.
      if (btn.dataset.destructive) { confirmDestructive(cmd, btn.textContent.trim()); return; }
      try {
        await api("/api/fan/command", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ command: cmd, confirmDestructive: false }),
        });
        toast(`Sent: ${btn.textContent.trim()}`);
        if (cmd === "Power") refreshPlaylist();   // power state changed → refresh badge
      } catch (e) { toast(e.message, true); }
    });
  });

  $("managePass").addEventListener("click", managePassphrase);
  $("refreshPlaylist").addEventListener("click", refreshPlaylist);

  $("duration").addEventListener("input", () => $("durVal").textContent = `${$("duration").value} s`);
  $("applyDuration").addEventListener("click", async () => {
    try {
      const r = await api("/api/fan/duration", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ seconds: parseInt($("duration").value, 10) }),
      });
      toast(`Picture duration set to ${r.seconds}s.`);
    } catch (e) { toast(e.message, true); }
  });

  $("sendFanBtn").addEventListener("click", async () => {
    if (!state.lastBinJob) return;
    const btn = $("sendFanBtn");
    btn.disabled = true;
    const original = btn.textContent;
    btn.textContent = "Uploading…";
    try {
      const r = await api("/api/fan/upload", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ jobId: state.lastBinJob, name: $("fanClipName").value || "HOLOFAN" }),
      });
      toast(`Sent "${r.uploaded}" to the fan (${(r.bytes / 1024).toFixed(0)} KB). Now on the device.`);
    } catch (e) { toast(e.message, true); }
    finally { btn.disabled = false; btn.textContent = original; }
  });

  const clock = { clockEnabled: "Enabled", clockNeedle: "NeedleColour", clockDial: "DialStyle" };
  Object.entries(clock).forEach(([id, setting]) => {
    $(id).addEventListener("change", async () => {
      try {
        await api("/api/fan/clock", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ setting, value: parseInt($(id).value, 10) }),
        });
        toast(`Clock: ${setting} → ${$(id).selectedOptions[0].textContent}`);
      } catch (e) { toast(e.message, true); }
    });
  });
}

loadPresets();
bindControls();
bindFan();
refreshFan();
