const BRIDGE_BASE = "http://127.0.0.1:47821";
const DEFAULT_SETTINGS = {
  autoCapture: true,
  minSizeMB: 1
};

let fastPollTimer = null;
let hadActiveDownloads = false;
let clearDoneBadgeTimer = null;
let lastRenderedKey = "";
const handledDownloadIds = new Set();

const DEFAULT_ICON_PATHS = {
  16: "icons/icon16.png",
  48: "icons/icon48.png",
  128: "icons/icon128.png"
};

function drawProgressRingIcon(size, progressPercent, isDone = false) {
  const canvas = new OffscreenCanvas(size, size);
  const ctx = canvas.getContext("2d");
  const cx = size / 2;
  const cy = size / 2;
  const ringWidth = Math.max(2.2, size * 0.125);
  const ringRadius = cx - ringWidth / 2 - 0.5;
  const innerRadius = Math.max(1, ringRadius - ringWidth / 2 - 0.8);

  ctx.clearRect(0, 0, size, size);

  // Inner circle background
  const grad = ctx.createLinearGradient(0, 0, size, size);
  if (isDone) {
    grad.addColorStop(0, "#16a34a");
    grad.addColorStop(1, "#15803d");
  } else {
    grad.addColorStop(0, "#0f172a");
    grad.addColorStop(1, "#1e3a8a");
  }
  ctx.beginPath();
  ctx.arc(cx, cy, innerRadius, 0, Math.PI * 2);
  ctx.fillStyle = grad;
  ctx.fill();

  // Outer track ring
  ctx.beginPath();
  ctx.arc(cx, cy, ringRadius, 0, Math.PI * 2);
  ctx.strokeStyle = isDone ? "#22c55e" : "rgba(148, 163, 184, 0.35)";
  ctx.lineWidth = ringWidth;
  ctx.stroke();

  // Active circular progress arc (starts from 12 o'clock)
  const pct = Math.max(0, Math.min(100, progressPercent));
  if (!isDone && pct > 0) {
    const startAngle = -Math.PI / 2;
    const endAngle = startAngle + (pct / 100) * Math.PI * 2;
    ctx.beginPath();
    ctx.arc(cx, cy, ringRadius, startAngle, endAngle);
    ctx.strokeStyle = "#38bdf8";
    ctx.lineWidth = ringWidth;
    ctx.lineCap = "round";
    ctx.stroke();
  }

  // White lightning bolt centered inside the ring
  const scale = innerRadius * 1.45;
  const ox = cx - scale / 2;
  const oy = cy - scale / 2;
  ctx.beginPath();
  ctx.moveTo(ox + scale * 0.57, oy + scale * 0.12);
  ctx.lineTo(ox + scale * 0.25, oy + scale * 0.55);
  ctx.lineTo(ox + scale * 0.49, oy + scale * 0.55);
  ctx.lineTo(ox + scale * 0.41, oy + scale * 0.88);
  ctx.lineTo(ox + scale * 0.75, oy + scale * 0.43);
  ctx.lineTo(ox + scale * 0.51, oy + scale * 0.43);
  ctx.closePath();
  ctx.fillStyle = "#ffffff";
  ctx.fill();

  return ctx.getImageData(0, 0, size, size);
}

function setCircularProgressIcon(progressPercent, isDone = false) {
  const bucket = isDone ? "done" : `p_${Math.round(progressPercent)}`;
  if (lastRenderedKey === bucket) return;
  lastRenderedKey = bucket;

  try {
    chrome.action.setBadgeText({ text: "" });
    chrome.action.setIcon({
      imageData: {
        16: drawProgressRingIcon(16, progressPercent, isDone),
        32: drawProgressRingIcon(32, progressPercent, isDone)
      }
    });
  } catch {
    // Fallback ignored
  }
}

function resetDefaultIcon() {
  if (lastRenderedKey === "default") return;
  lastRenderedKey = "default";
  chrome.action.setBadgeText({ text: "" });
  chrome.action.setIcon({ path: DEFAULT_ICON_PATHS });
}

// Setup context menus and background alarm
function createContextMenus() {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: "turboget-download-link",
      title: "⚡ Tải bằng TurboGet",
      contexts: ["link", "video", "audio", "image"]
    });
  });
}

function setupAlarms() {
  chrome.alarms.create("turboget-badge-sync", { periodInMinutes: 0.5 });
}

chrome.runtime.onInstalled.addListener(() => {
  chrome.storage.sync.get(DEFAULT_SETTINGS, (items) => {
    chrome.storage.sync.set({
      autoCapture: items.autoCapture ?? DEFAULT_SETTINGS.autoCapture,
      minSizeMB: items.minSizeMB ?? DEFAULT_SETTINGS.minSizeMB
    });
  });
  createContextMenus();
  setupAlarms();
  pollDownloadsForBadge();
});

chrome.runtime.onStartup.addListener(() => {
  createContextMenus();
  setupAlarms();
  pollDownloadsForBadge();
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === "turboget-badge-sync") {
    pollDownloadsForBadge();
  }
});

// Helper: get settings
function getSettings() {
  return new Promise((resolve) => {
    chrome.storage.sync.get(DEFAULT_SETTINGS, (items) => resolve(items));
  });
}

// Helper: fetch with timeout
async function fetchJson(path, options = {}, timeoutMs = 1000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const res = await fetch(`${BRIDGE_BASE}${path}`, {
      ...options,
      signal: controller.signal
    });
    if (!res.ok) return null;
    return await res.json();
  } catch {
    return null;
  } finally {
    clearTimeout(timer);
  }
}

async function pingTurboGet(timeoutMs = 800) {
  return await fetchJson("/api/ping", { method: "GET" }, timeoutMs);
}

async function getDownloadsSnapshot(timeoutMs = 1000) {
  return await fetchJson("/api/downloads", { method: "GET" }, timeoutMs);
}

// Update Chrome/Edge toolbar icon with circular progress ring (no badge text covering the icon)
async function pollDownloadsForBadge() {
  const data = await getDownloadsSnapshot(900);

  if (!data || !data.ok) {
    stopFastPolling();
    hadActiveDownloads = false;
    resetDefaultIcon();
    chrome.action.setTitle({ title: "TurboGet — Chưa kết nối (hãy mở TurboGet.exe)" });
    return;
  }

  const activeCount = data.activeCount || 0;
  if (activeCount > 0) {
    hadActiveDownloads = true;
    if (clearDoneBadgeTimer) {
      clearTimeout(clearDoneBadgeTimer);
      clearDoneBadgeTimer = null;
    }

    const pct = Math.max(0, Math.min(100, Math.round(data.averageProgress || 0)));
    setCircularProgressIcon(pct, false);
    chrome.action.setTitle({
      title: `TurboGet — Đang tải ${activeCount} file (${pct}% • ${data.totalSpeedText || "0 B/s"})`
    });
    startFastPolling();
  } else {
    stopFastPolling();
    if (hadActiveDownloads) {
      hadActiveDownloads = false;
      setCircularProgressIcon(100, true);
      chrome.action.setTitle({ title: "TurboGet — Tải hoàn thành!" });
      clearDoneBadgeTimer = setTimeout(() => {
        resetDefaultIcon();
        chrome.action.setTitle({ title: "TurboGet — Sẵn sàng bắt link" });
      }, 5000);
    } else {
      resetDefaultIcon();
      chrome.action.setTitle({ title: "TurboGet — Sẵn sàng bắt link" });
    }
  }
}

function startFastPolling() {
  if (fastPollTimer) return;
  fastPollTimer = setInterval(() => {
    pollDownloadsForBadge();
  }, 1200);
}

function stopFastPolling() {
  if (fastPollTimer) {
    clearInterval(fastPollTimer);
    fastPollTimer = null;
  }
}

// Helper: collect cookies for authenticated downloads
async function getCookieHeader(url) {
  if (!url || (!url.startsWith("http://") && !url.startsWith("https://"))) {
    return "";
  }
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join("; ");
  } catch {
    return "";
  }
}

// Helper: send download request to TurboGet
async function sendToTurboGet({ url, filename = "", referer = "", mime = "", type = "auto" }) {
  const cookie = await getCookieHeader(url);
  const res = await fetch(`${BRIDGE_BASE}/api/download`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      url,
      filename,
      referer,
      cookie,
      userAgent: navigator.userAgent,
      mime,
      type
    })
  });
  if (!res.ok) {
    throw new Error(`HTTP ${res.status}`);
  }
  const result = await res.json();
  pollDownloadsForBadge();
  return result;
}

function notify(title, message) {
  try {
    chrome.notifications.create({
      type: "basic",
      iconUrl: "icons/icon128.png",
      title,
      message
    });
  } catch {
    // Ignore notification errors
  }
}

// Context Menu click handler
chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId !== "turboget-download-link") return;

  const targetUrl = info.linkUrl || info.srcUrl;
  if (!targetUrl) return;

  const ping = await pingTurboGet(1000);
  if (!ping || !ping.ok) {
    notify(
      "TurboGet chưa được mở",
      "Hãy mở phần mềm TurboGet.exe trên máy trước khi gửi link tải."
    );
    return;
  }

  try {
    const result = await sendToTurboGet({
      url: targetUrl,
      referer: info.pageUrl || tab?.url || ""
    });
    notify("Đã gửi sang TurboGet ⚡", result.name || targetUrl);
  } catch (err) {
    notify("Không thể gửi sang TurboGet", String(err));
  }
});

function isTorrentDownload(url, filename, mime) {
  const lowerUrl = (url || "").split("?")[0].toLowerCase();
  const lowerName = (filename || "").toLowerCase();
  const lowerMime = (mime || "").toLowerCase();
  return (
    lowerUrl.endsWith(".torrent") ||
    lowerName.endsWith(".torrent") ||
    lowerMime === "application/x-bittorrent"
  );
}

async function tryInterceptDownload(downloadItem, knownFilename = "") {
  try {
    // Edge fires onCreated for historical downloads when loading history / waking up.
    // Only intercept genuinely new, in-progress downloads started just now.
    if (!downloadItem || downloadItem.state !== "in_progress") return;
    if (downloadItem.endTime) return;
    if (downloadItem.byExtensionId) return;

    if (downloadItem.startTime) {
      const startMs = Date.parse(downloadItem.startTime);
      if (!Number.isNaN(startMs) && Math.abs(Date.now() - startMs) > 15000) {
        return;
      }
    }

    if (handledDownloadIds.has(downloadItem.id)) return;

    const settings = await getSettings();
    if (!settings.autoCapture) return;

    const url = downloadItem.finalUrl || downloadItem.url;
    if (!url) return;
    if (!url.startsWith("http://") && !url.startsWith("https://")) return;

    const filename = knownFilename || downloadItem.filename || "";
    const mime = downloadItem.mime || "";
    const isTorrent = isTorrentDownload(url, filename, mime);

    // Always capture .torrent files regardless of minSizeMB!
    // For normal files, wait until filename/size is known or check minSizeMB.
    if (!isTorrent) {
      const minBytes = (Number(settings.minSizeMB) || 0) * 1024 * 1024;
      if (minBytes > 0 && downloadItem.fileSize > 0 && downloadItem.fileSize < minBytes) {
        return;
      }
    }

    const ping = await pingTurboGet(700);
    if (!ping || !ping.ok) return;

    handledDownloadIds.add(downloadItem.id);
    if (handledDownloadIds.size > 200) {
      const firstId = handledDownloadIds.values().next().value;
      handledDownloadIds.delete(firstId);
    }

    try {
      await chrome.downloads.pause(downloadItem.id);
    } catch {
      // Ignore
    }

    const result = await sendToTurboGet({
      url,
      filename,
      referer: downloadItem.referrer || "",
      mime,
      type: isTorrent ? "torrent" : "auto"
    });

    try {
      await chrome.downloads.cancel(downloadItem.id);
      await chrome.downloads.erase({ id: downloadItem.id });
    } catch {
      // Ignore
    }

    notify(
      isTorrent ? "TurboGet đang tải Torrent 🧲" : "TurboGet đang tải ⚡",
      result.name || filename || url
    );
  } catch {
    try {
      await chrome.downloads.resume(downloadItem.id);
    } catch {
      // Ignore
    }
  }
}

// Intercept in onDeterminingFilename so real filename (e.g. from Content-Disposition like *.torrent),
// MIME type, and fileSize are already resolved by Chrome/Edge.
chrome.downloads.onDeterminingFilename.addListener((downloadItem, suggest) => {
  suggest();
  if (!handledDownloadIds.has(downloadItem.id)) {
    tryInterceptDownload(downloadItem, downloadItem.filename || "");
  }
});

// Handle messages from popup.js
chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.action === "ping") {
    pingTurboGet(1000).then((data) => sendResponse({ connected: Boolean(data?.ok), data }));
    return true;
  }

  if (msg?.action === "getDownloads") {
    getDownloadsSnapshot(1000).then((data) => {
      if (data?.ok) {
        pollDownloadsForBadge();
        sendResponse({ connected: true, data });
      } else {
        sendResponse({ connected: false, data: null });
      }
    });
    return true;
  }

  if (msg?.action === "downloadAction") {
    fetchJson(
      "/api/action",
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ id: msg.id, action: msg.cmd })
      },
      1200
    ).then((res) => {
      pollDownloadsForBadge();
      sendResponse({ ok: Boolean(res?.ok) });
    });
    return true;
  }

  if (msg?.action === "sendUrl") {
    sendToTurboGet({ url: msg.url })
      .then((data) => sendResponse({ ok: true, data }))
      .catch((err) => sendResponse({ ok: false, error: String(err) }));
    return true;
  }
});
