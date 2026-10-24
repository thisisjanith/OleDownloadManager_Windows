// End-to-end browser integration test (Chrome or Edge).
//
// Proves the full chain: extension -> browser native messaging -> OleDownloadManager.BrowserHost.exe
// -> per-user named pipe -> running app -> Add Download dialog -> real HTTP download to disk.
//
// Everything is isolated: a temporary browser profile, a temporary app data folder, a private pipe name
// (OLEDM_PIPE_SUFFIX) and a local HTTP server. The per-user native-messaging registry keys are created
// for the test and restored afterwards.
//
// Usage:  node tests/BrowserE2E/run-chrome-e2e.mjs [--browser chrome|edge] [--configuration Debug|Release] [--headed]

import { spawn, execFileSync } from "node:child_process";
import { createServer } from "node:http";
import { mkdtempSync, rmSync, existsSync, readFileSync, readdirSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";

const args = process.argv.slice(2);
const option = (name, fallback) => {
  const index = args.indexOf(`--${name}`);
  return index >= 0 ? args[index + 1] : fallback;
};
const browser = option("browser", "chrome");
const configuration = option("configuration", "Debug");
const headed = args.includes("--headed");
// --installed: use the MSI-installed app and its own native messaging registration.
const installed = args.includes("--installed");
const installDir = join(process.env.LOCALAPPDATA, "Programs", "OleDownloadManager");

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const browserPath = browser === "edge"
  ? "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe"
  : "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
const registryParent = browser === "edge"
  ? "HKCU\\Software\\Microsoft\\Edge\\NativeMessagingHosts"
  : "HKCU\\Software\\Google\\Chrome\\NativeMessagingHosts";
const hostName = "com.oledm.downloadmanager";
const expectedExtensionId = "fjlipfmbkmopdjkjnfmpocodmcecbacf";
const appExe = installed
  ? join(installDir, "OleDownloadManager.exe")
  : join(repo, `src\\OleDownloadManager.App\\bin\\${configuration}\\net10.0-windows\\OleDownloadManager.exe`);
const hostExe = installed
  ? join(installDir, "OleDownloadManager.BrowserHost.exe")
  : join(repo, `src\\OleDownloadManager.BrowserHost\\bin\\${configuration}\\net10.0-windows\\OleDownloadManager.BrowserHost.exe`);
const extensionDir = installed ? join(installDir, "browser-extensions", "chrome") : join(repo, "browser-extensions", "chrome");
const uiaScript = join(repo, "tests", "BrowserE2E", "uia.ps1");

const work = mkdtempSync(join(tmpdir(), "oledm-e2e-"));
const dataDir = join(work, "data");
const downloadDir = join(work, "downloads");
const browserDownloads = join(work, "browser-downloads");
const profileDir = join(work, "profile");
for (const dir of [dataDir, downloadDir, browserDownloads, profileDir]) mkdirSync(dir, { recursive: true });
const pipeSuffix = "e2e-" + Date.now();

const log = (...m) => console.log("[e2e]", ...m);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function waitFor(predicate, timeoutMs, what) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const value = await predicate();
    if (value) return value;
    await sleep(200);
  }
  throw new Error(`Timed out waiting for ${what}`);
}

// ---------------------------------------------------------------- local HTTP server
const content = (size) => Buffer.from(Array.from({ length: size }, (_, i) => i % 251));
const server = createServer((req, res) => {
  const match = req.url.match(/^\/files\/(\d+)\/([\w.-]+)$/);
  if (!match) { res.writeHead(404); return res.end(); }
  const body = content(Number(match[1]));
  res.writeHead(200, {
    "Content-Type": "application/octet-stream",
    "Content-Length": body.length,
    "Content-Disposition": `attachment; filename="${match[2]}"`,
  });
  res.end(body);
});
await new Promise((r) => server.listen(0, "127.0.0.1", r));
const base = `http://127.0.0.1:${server.address().port}`;
log("test server", base);

// ---------------------------------------------------------------- registry
function regQuery(key) {
  try { return execFileSync("reg", ["query", key, "/ve"], { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }); } catch { return null; }
}
const previousRegistration = regQuery(`${registryParent}\\${hostName}`);
const previousValue = previousRegistration?.match(/REG_SZ\s+(.+)/)?.[1]?.trim();

function registerHost() {
  execFileSync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", join(repo, "scripts", "install-browser-host.ps1"),
    "-HostPath", hostExe, "-ManifestDirectory", join(work, "native-messaging")], { stdio: "inherit" });
}

function restoreRegistry() {
  for (const parent of ["HKCU\\Software\\Google\\Chrome\\NativeMessagingHosts", "HKCU\\Software\\Microsoft\\Edge\\NativeMessagingHosts", "HKCU\\Software\\Mozilla\\NativeMessagingHosts"]) {
    try { execFileSync("reg", ["delete", `${parent}\\${hostName}`, "/f"], { stdio: "ignore" }); } catch { /* not present */ }
  }
  if (previousValue) {
    execFileSync("reg", ["add", `${registryParent}\\${hostName}`, "/ve", "/d", previousValue, "/f"], { stdio: "ignore" });
  }
}

// ---------------------------------------------------------------- CDP over --remote-debugging-pipe
class Cdp {
  constructor(child) {
    this.out = child.stdio[3];
    this.in = child.stdio[4];
    this.nextId = 1;
    this.pending = new Map();
    this.listeners = [];
    let buffer = "";
    this.in.on("data", (chunk) => {
      buffer += chunk.toString("utf8");
      let index;
      while ((index = buffer.indexOf("\0")) >= 0) {
        const message = JSON.parse(buffer.slice(0, index));
        buffer = buffer.slice(index + 1);
        if (message.id && this.pending.has(message.id)) {
          const { resolve, reject } = this.pending.get(message.id);
          this.pending.delete(message.id);
          message.error ? reject(new Error(JSON.stringify(message.error))) : resolve(message.result);
        } else {
          for (const listener of this.listeners) listener(message);
        }
      }
    });
  }

  send(method, params = {}, sessionId) {
    const id = this.nextId++;
    const payload = { id, method, params };
    if (sessionId) payload.sessionId = sessionId;
    this.out.write(JSON.stringify(payload) + "\0");
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      setTimeout(() => {
        if (this.pending.delete(id)) reject(new Error(`CDP timeout: ${method}`));
      }, 30000);
    });
  }

  on(listener) { this.listeners.push(listener); }
}

// ---------------------------------------------------------------- run
let app;
let chrome;
let failed = false;
try {
  for (const file of [appExe, hostExe, browserPath]) assert.ok(existsSync(file), `missing ${file}`);
  if (installed) {
    assert.ok(regQuery(`${registryParent}\\${hostName}`), "the installer should have registered the native host");
  } else {
    registerHost();
  }

  const env = { ...process.env, OLEDM_DATA_DIR: dataDir, OLEDM_PIPE_SUFFIX: pipeSuffix };
  app = spawn(appExe, [], { env, stdio: "ignore" });
  log("app pid", app.pid);
  await sleep(3000);

  const browserArgs = [
    `--user-data-dir=${profileDir}`, "--remote-debugging-pipe", "--enable-unsafe-extension-debugging",
    "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-search-engine-choice-screen",
  ];
  if (!headed) browserArgs.push("--headless=new");
  browserArgs.push("about:blank");
  chrome = spawn(browserPath, browserArgs, { env, stdio: ["ignore", "ignore", "ignore", "pipe", "pipe"] });
  const cdp = new Cdp(chrome);

  const { id: extensionId } = await cdp.send("Extensions.loadUnpacked", { path: extensionDir });
  log("extension loaded", extensionId);
  assert.equal(extensionId, expectedExtensionId, "extension ID must match the key in manifest.json");

  await cdp.send("Browser.setDownloadBehavior", { behavior: "allow", downloadPath: browserDownloads, eventsEnabled: true });

  const worker = await waitFor(async () => {
    const { targetInfos } = await cdp.send("Target.getTargets");
    return targetInfos.find((t) => t.type === "service_worker" && t.url.startsWith(`chrome-extension://${extensionId}/`));
  }, 20000, "extension service worker");
  const { sessionId } = await cdp.send("Target.attachToTarget", { targetId: worker.targetId, flatten: true });
  const evaluate = async (expression) => {
    const result = await cdp.send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true }, sessionId);
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
  };

  // 1. Connection status through native messaging.
  const status = await evaluate("oledm.getConnectionStatus()");
  log("status", JSON.stringify(status));
  assert.equal(status.state, "connected");

  // 2. Context-menu path ("Download with OleDownloadManager").
  const accepted = await evaluate(`oledm.handleContextClick({ linkUrl: "${base}/files/150000/report.pdf", pageUrl: "${base}/page" })`);
  log("context menu result", JSON.stringify(accepted));
  assert.equal(accepted.accepted, true);

  const uia = (action, id, value) => execFileSync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", uiaScript,
    "-ProcessId", String(app.pid), "-Action", action, "-AutomationId", id, ...(value ? ["-Value", value] : [])], { encoding: "utf8" });

  uia("wait", "AddDownloadWindow");
  uia("set", "DestinationBox", downloadDir);
  await sleep(1500);
  uia("click", "StartNowButton");
  await waitFor(() => existsSync(join(downloadDir, "report.pdf")), 30000, "report.pdf");
  assert.deepEqual(readFileSync(join(downloadDir, "report.pdf")), content(150000));
  log("context-menu download verified");

  // 3. Duplicate submissions within a few seconds are ignored by the extension.
  const duplicate = await evaluate(`oledm.sendDownload("${base}/files/150000/report.pdf")`);
  assert.equal(duplicate.accepted, false);

  // 4. Automatic capture of a browser download (paused, handed over, then canceled in the browser).
  await cdp.send("Target.createTarget", { url: `${base}/files/300000/setup.zip` });
  uia("wait", "AddDownloadWindow");
  uia("set", "DestinationBox", downloadDir);
  await sleep(1500);
  uia("click", "StartNowButton");
  await waitFor(() => existsSync(join(downloadDir, "setup.zip")), 30000, "setup.zip");
  assert.deepEqual(readFileSync(join(downloadDir, "setup.zip")), content(300000));
  await sleep(1500);
  const leftInBrowser = readdirSync(browserDownloads).filter((f) => f.startsWith("setup"));
  assert.deepEqual(leftInBrowser, [], "the browser copy must be canceled after hand-off");
  log("captured download verified; browser copy canceled");

  // 5. Non-HTTP URLs are refused before reaching the host.
  const refused = await evaluate(`oledm.sendDownload("file:///C:/Windows/win.ini")`);
  assert.equal(refused.accepted, false);

  log("PASS");
} catch (error) {
  failed = true;
  console.error("[e2e] FAIL", error);
} finally {
  try { chrome?.kill(); } catch { /* already gone */ }
  if (app) {
    try { execFileSync("taskkill", ["/PID", String(app.pid)], { stdio: "ignore" }); } catch { /* exited */ }
    await sleep(4000);
    try { execFileSync("taskkill", ["/PID", String(app.pid), "/F"], { stdio: "ignore" }); } catch { /* exited */ }
  }
  if (!installed) restoreRegistry();
  server.close();
  await sleep(500);
  try { rmSync(work, { recursive: true, force: true }); } catch { /* files may still be locked briefly */ }
  process.exit(failed ? 1 : 0);
}
