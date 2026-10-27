# Browser integration

The OleDownloadManager extension adds **Download with OleDownloadManager** to the context menu of links, images,
video and audio, and can optionally hand over downloads the browser starts (for chosen file types). It talks to the
desktop app through the browser's **native messaging** API — no local web server is opened.

```
Extension ──(stdin/stdout, 32-bit length + UTF-8 JSON)──▶ OleDownloadManager.BrowserHost.exe
          ◀─────────────────────────────────────────────
BrowserHost ──(named pipe, current user only)──▶ OleDownloadManager.exe
```

## Components

| Item | Location |
|---|---|
| Chrome / Edge extension (Manifest V3) | `browser-extensions/chrome` (installed copy: `%LOCALAPPDATA%\Programs\OleDownloadManager\browser-extensions\chrome`) |
| Firefox extension | `browser-extensions/firefox/manifest.json` + the shared files; ready-to-load folder produced by `scripts/publish.ps1` at `artifacts/publish/win-x64/browser-extensions/firefox` |
| Native host | `OleDownloadManager.BrowserHost.exe` (next to the app) |
| Host manifests | Installed: `com.oledm.downloadmanager.chrome.json` / `.firefox.json` in the install folder. Development: `%LOCALAPPDATA%\OleDownloadManager\native-messaging` |
| Registry (per user) | `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.oledm.downloadmanager`, `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\…`, `HKCU\Software\Mozilla\NativeMessagingHosts\…` |

Extension IDs allowed by default:

- Chrome/Edge: `fjlipfmbkmopdjkjnfmpocodmcecbacf` — fixed by the public `key` in `manifest.json`, so the unpacked
  extension always gets this ID. (Only the public key is in the repository; no private key is needed for unpacked
  loading or store publishing.)
- Firefox: `oledownloadmanager@oledm.local`

## Installing

### With the installer

The MSI's *Browser integration* feature (on by default) registers the host for Chrome, Edge and Firefox for the
current user. Then load the extension:

**Chrome / Edge**
1. Open `chrome://extensions` (or `edge://extensions`).
2. Turn on **Developer mode**.
3. Choose **Load unpacked** and select `%LOCALAPPDATA%\Programs\OleDownloadManager\browser-extensions\chrome`.
4. Click the extension icon: it should say *Connected to OleDownloadManager*.

**Firefox** (not tested — Firefox was not available on the build machine)
1. Open `about:debugging#/runtime/this-firefox` → **Load Temporary Add-on…** → select `manifest.json` in
   `…\browser-extensions\firefox`. Temporary add-ons are removed when Firefox restarts.
2. For permanent installation the add-on must be signed by Mozilla (submit the zip from
   `artifacts/extensions/OleDownloadManager-firefox-<version>.zip` to addons.mozilla.org, or use an unbranded/
   Developer Edition build with `xpinstall.signatures.required` set to false).

### For development builds

```powershell
./scripts/build.ps1
./scripts/install-browser-host.ps1            # registers the Debug host for this user
# ... load browser-extensions/chrome as an unpacked extension ...
./scripts/uninstall-browser-host.ps1          # removes the registration
```

The app's **Settings → Browser** page shows the registration status for each browser and can register or remove it.

### Store-published extensions

Chrome Web Store and Edge Add-ons assign their own extension IDs. Register them with
`./scripts/install-browser-host.ps1 -ChromeExtensionId <store id> [-ChromeExtensionId fjlipfmbkmopdjkjnfmpocodmcecbacf]`;
the script also writes `browserhost.allowed.json` next to the host so the host accepts the new ID.
`scripts/package.ps1` produces store-ready zips (the development `key` is stripped from the Chrome package).

## Using

- Right-click a link, image, video or audio element → **Download with OleDownloadManager**.
- With **Capture downloads of these file types** enabled (popup; on by default for common archive, installer,
  disk-image, video, audio and PDF types), downloads the browser starts are paused, offered to the app, and canceled
  in the browser only after the app accepts them. If the app cannot be reached, the browser download resumes.
  Only downloads that are in progress and started within the last minute are captured: Chrome replays its whole
  download history to extensions (seen at browser start), and those past downloads are always ignored.
- As a safety net the app accepts at most 10 links from the browser within 10 seconds and shows their dialogs one
  at a time.
- The app shows the Add Download dialog for browser downloads (Settings → Browser can switch to adding them
  directly). If the app is not running, the host starts it in the notification area.
- The same link sent twice within five seconds is ignored; a link already in the list is rejected by the app.

## Protocol

Every message carries `type`, `version` (currently 1) and an optional `requestId` (≤ 64 characters `[A-Za-z0-9_-]`).

| From extension | Fields | Reply |
|---|---|---|
| `hello` | — | `hello` with `appVersion`, `appRunning` |
| `getStatus` | — | `status` with `appRunning`, `activeDownloads` |
| `addDownload` | `url` (http/https), optional `pageUrl`, `fileName`, `fileSize` | `downloadAccepted` (`downloadId` or a `reason` when waiting for confirmation) or `downloadRejected` (`reason`) |
| anything else / invalid | — | `error` with `reason` |

Limits and validation: messages larger than 64 KiB are refused (the host exits on an oversized frame), JSON depth is
limited, unknown types are rejected, URLs must be absolute HTTP(S) without credentials, file names are sanitized,
invalid page URLs are dropped. `hello`/`getStatus` never start the app.

## Troubleshooting

| Symptom | Check |
|---|---|
| Popup: *Not connected* | Settings → Browser in the app; registry keys above; the manifest's `path` must point at an existing host (relative paths resolve against the manifest's folder). |
| Popup: *Installed. The app will start when you send a download.* | Normal when the app is closed. |
| Nothing happens on send | `%LOCALAPPDATA%\OleDownloadManager\logs\browserhost-*.log` lists rejected callers and protocol errors. |
| Wrong extension ID | Reload the unpacked extension from the folder containing the provided `manifest.json` (its `key` fixes the ID), or register your ID with `install-browser-host.ps1`. |

## Acceptance checklist

Run before a release (automated items are covered by `node tests/BrowserE2E/run-chrome-e2e.mjs`).

| # | Check | Chrome | Edge | Firefox |
|---|---|---|---|---|
| 1 | Unpacked extension loads with ID `fjlipfmbkmopdjkjnfmpocodmcecbacf` | automated ✔ | automated ✔ | — |
| 2 | Popup shows *Connected* while the app runs | automated ✔ (status call) | automated ✔ | manual |
| 3 | Context-menu send opens the Add Download dialog with the right URL and file name | automated ✔ | automated ✔ | manual |
| 4 | Confirming the dialog downloads a byte-exact file | automated ✔ | automated ✔ | manual |
| 5 | Same link sent twice within 5 s is ignored | automated ✔ | automated ✔ | manual |
| 6 | Captured browser download is handed over and canceled in the browser | automated ✔ | automated ✔ | manual |
| 7 | Non-HTTP links are refused | automated ✔ | automated ✔ | manual |
| 8 | With the app closed, sending a link starts it | verified manually with the host (MSI build) | — | manual |
| 9 | Unknown extension IDs are refused by the host | automated ✔ (host tests) | automated ✔ | automated ✔ (host tests) |
| 10 | MSI registration works without the dev script | automated ✔ (`--installed`) | — | manual |
| 11 | Uninstall removes the registry keys | verified manually | verified manually | verified manually |
| 12 | Capture can be turned off in the popup and stays off after a browser restart | manual | manual | manual |
