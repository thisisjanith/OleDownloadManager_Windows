// OleDownloadManager browser extension (Chrome, Edge, Firefox).
//
// Privacy: this extension only sees URLs you explicitly send (context menu) or downloads the browser
// is about to start (when capture is enabled). It never reads page content, cookies or history.

const api = globalThis.browser ?? globalThis.chrome;
const HOST = "com.oledm.downloadmanager";
const PROTOCOL_VERSION = 1;
const MENU_ID = "oledm-download";
const DUPLICATE_WINDOW_MS = 5000;

const DEFAULT_SETTINGS = {
  captureEnabled: true,
  // Only these file types are captured automatically. Everything else downloads normally.
  captureExtensions: "exe msi msix zip 7z rar tar gz tgz bz2 xz iso img dmg apk mp4 mkv mov webm avi mp3 flac wav m4a pdf",
  minimumSizeMb: 0,
};

const recent = new Map();

function isHttpUrl(url) {
  try {
    const parsed = new URL(url);
    return parsed.protocol === "https:" || parsed.protocol === "http:";
  } catch {
    return false;
  }
}

function isDuplicate(url) {
  const now = Date.now();
  for (const [key, time] of recent) {
    if (now - time > DUPLICATE_WINDOW_MS) recent.delete(key);
  }
  if (recent.has(url)) return true;
  recent.set(url, now);
  return false;
}

function requestId() {
  return Math.random().toString(36).slice(2, 12) + Date.now().toString(36);
}

async function getSettings() {
  const stored = await api.storage.local.get(DEFAULT_SETTINGS);
  return { ...DEFAULT_SETTINGS, ...stored };
}

/** Sends one message to the native host. Rejects when the host is not installed. */
async function sendToHost(message) {
  return await api.runtime.sendNativeMessage(HOST, { version: PROTOCOL_VERSION, requestId: requestId(), ...message });
}

/** Returns { state: "connected" | "app-not-running" | "host-missing", appVersion? }. */
async function getConnectionStatus() {
  try {
    const reply = await sendToHost({ type: "hello" });
    if (reply && reply.appRunning) return { state: "connected", appVersion: reply.appVersion };
    return { state: "app-not-running" };
  } catch (error) {
    return { state: "host-missing", error: String(error && error.message || error) };
  }
}

function setBadge(text, color) {
  try {
    api.action.setBadgeText({ text });
    if (color) api.action.setBadgeBackgroundColor({ color });
  } catch {
    // Badges are cosmetic.
  }
}

/** Hands a URL to the desktop app. Returns true when the app accepted it. */
async function sendDownload(url, pageUrl, fileName, fileSize) {
  if (!isHttpUrl(url)) return { accepted: false, reason: "Only HTTP and HTTPS links can be sent." };
  if (isDuplicate(url)) return { accepted: false, reason: "Already sent a moment ago." };
  const message = { type: "addDownload", url };
  if (pageUrl && isHttpUrl(pageUrl)) message.pageUrl = pageUrl;
  if (fileName) message.fileName = fileName;
  if (Number.isFinite(fileSize) && fileSize > 0) message.fileSize = fileSize;
  try {
    const reply = await sendToHost(message);
    if (reply && reply.type === "downloadAccepted") {
      setBadge("", null);
      return { accepted: true };
    }
    recent.delete(url);
    return { accepted: false, reason: (reply && reply.reason) || "The app rejected the download." };
  } catch (error) {
    recent.delete(url);
    setBadge("!", "#C42B1C");
    return { accepted: false, reason: "OleDownloadManager is not installed or its browser integration is not registered." };
  }
}

function fileExtension(name) {
  const clean = (name || "").split(/[?#]/)[0];
  const dot = clean.lastIndexOf(".");
  return dot >= 0 ? clean.slice(dot + 1).toLowerCase() : "";
}

function fileNameFromItem(item) {
  const path = item.filename || "";
  const fromPath = path.split(/[\\/]/).pop();
  if (fromPath) return fromPath;
  try {
    return decodeURIComponent(new URL(item.finalUrl || item.url).pathname.split("/").pop() || "");
  } catch {
    return "";
  }
}

/** Only downloads the browser started moments ago count as new. */
const MAX_CAPTURE_AGE_MS = 60 * 1000;

function isFreshDownload(item, now = Date.now()) {
  // The browser replays its download history through onCreated (at browser start and when the
  // extension loads). Those entries are finished or old and must never be sent to the app.
  if (item.state && item.state !== "in_progress") return false;
  const started = Date.parse(item.startTime);
  if (!Number.isFinite(started)) return false;
  return now - started <= MAX_CAPTURE_AGE_MS && started - now <= MAX_CAPTURE_AGE_MS;
}

async function shouldCapture(item) {
  if (!isFreshDownload(item)) return false;
  const settings = await getSettings();
  if (!settings.captureEnabled) return false;
  if (item.byExtensionId) return false;
  const url = item.finalUrl || item.url;
  if (!isHttpUrl(url)) return false;
  if (item.incognito) return false;
  const allowed = new Set(String(settings.captureExtensions).toLowerCase().split(/[\s,;]+/).filter(Boolean));
  const ext = fileExtension(fileNameFromItem(item)) || fileExtension(url);
  if (!allowed.has(ext)) return false;
  const minBytes = Number(settings.minimumSizeMb || 0) * 1024 * 1024;
  if (minBytes > 0 && item.totalBytes > 0 && item.totalBytes < minBytes) return false;
  return true;
}

// ---- Context menu -----------------------------------------------------------

function createMenu() {
  api.contextMenus.removeAll(() => {
    api.contextMenus.create({
      id: MENU_ID,
      title: "Download with OleDownloadManager",
      contexts: ["link", "image", "video", "audio"],
    });
  });
}

api.runtime.onInstalled.addListener(createMenu);
api.runtime.onStartup?.addListener(createMenu);

async function handleContextClick(info) {
  const url = info.linkUrl || info.srcUrl;
  return await sendDownload(url, info.pageUrl, undefined, undefined);
}

api.contextMenus.onClicked.addListener((info) => {
  if (info.menuItemId === MENU_ID) handleContextClick(info);
});

// ---- Download capture --------------------------------------------------------
// The browser download is paused first and only canceled once the app has accepted it;
// if the app is unavailable the browser download simply resumes.

api.downloads.onCreated.addListener(async (item) => {
  try {
    if (!(await shouldCapture(item))) return;
    await api.downloads.pause(item.id).catch(() => {});
    const result = await sendDownload(item.finalUrl || item.url, item.referrer, fileNameFromItem(item), item.totalBytes);
    if (result.accepted) {
      await api.downloads.cancel(item.id).catch(() => {});
      await api.downloads.erase({ id: item.id }).catch(() => {});
    } else {
      await api.downloads.resume(item.id).catch(() => {});
    }
  } catch {
    api.downloads.resume(item.id).catch(() => {});
  }
});

// ---- Popup messaging -----------------------------------------------------------

api.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (sender.id !== api.runtime.id) return false;
  if (message && message.type === "status") {
    getConnectionStatus().then(sendResponse);
    return true;
  }
  return false;
});

// Exposed for automated end-to-end tests run through the browser's DevTools protocol.
globalThis.oledm = { sendDownload, getConnectionStatus, handleContextClick, shouldCapture, isFreshDownload };
