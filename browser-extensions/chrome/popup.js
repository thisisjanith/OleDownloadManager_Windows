const api = globalThis.browser ?? globalThis.chrome;

const DEFAULTS = {
  captureEnabled: true,
  captureExtensions: "exe msi msix zip 7z rar tar gz tgz bz2 xz iso img dmg apk mp4 mkv mov webm avi mp3 flac wav m4a pdf",
  minimumSizeMb: 0,
};

const STATUS_TEXT = {
  "connected": (s) => `Connected to OleDownloadManager ${s.appVersion || ""}`.trim(),
  "app-not-running": () => "Installed. The app will start when you send a download.",
  "host-missing": () => "Not connected. Install OleDownloadManager or register browser integration in its Settings.",
};

async function refreshStatus() {
  const status = await api.runtime.sendMessage({ type: "status" });
  document.getElementById("statusText").textContent = (STATUS_TEXT[status.state] || (() => status.state))(status);
  document.getElementById("dot").className = "dot " + status.state;
}

async function load() {
  const settings = { ...DEFAULTS, ...(await api.storage.local.get(DEFAULTS)) };
  document.getElementById("capture").checked = !!settings.captureEnabled;
  document.getElementById("extensions").value = settings.captureExtensions;
}

async function save() {
  const extensions = document.getElementById("extensions").value
    .toLowerCase()
    .split(/[\s,;]+/)
    .map((e) => e.replace(/^\./, ""))
    .filter((e) => /^[a-z0-9]{1,10}$/.test(e));
  await api.storage.local.set({
    captureEnabled: document.getElementById("capture").checked,
    captureExtensions: [...new Set(extensions)].join(" "),
  });
  document.getElementById("saved").textContent = "Saved";
  setTimeout(() => (document.getElementById("saved").textContent = ""), 1500);
}

document.getElementById("save").addEventListener("click", save);
load();
refreshStatus();
