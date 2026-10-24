# OleDownloadManager

A download manager for Windows: segmented (multi-connection) HTTP/HTTPS downloads, real pause and
resume, a persistent queue with scheduling and bandwidth limits, browser integration for Chrome, Edge
and Firefox, and an optional media/playlist mode that drives a user-installed yt-dlp.

Built with C# / .NET 10 (LTS) and WPF. The release is self-contained — users do not need to install .NET.

## Supported systems

| | |
|---|---|
| Windows | Windows 10 and Windows 11, x64. Developed and tested on Windows 11 (build 26200). |
| ARM64 | `scripts/publish.ps1 -Runtime win-arm64` produces an ARM64 build. It has **not** been tested on ARM64 hardware. |
| Browsers | Chrome and Edge (tested end-to-end, Chrome 155 / Edge 155). Firefox extension provided but **not tested** (Firefox was not available on the build machine). |

## Features

### Implemented and tested

- **Downloads over HTTP and HTTPS** with streaming writes (large files never load into memory), safe redirect handling
  (HTTPS→HTTP downgrades refused, credentials never forwarded to other hosts), and TLS validation always on.
- **Segmented downloads**: range support is verified with a real `Range: bytes=0-0` probe (a `206` with a matching
  `Content-Range`), not trusted from `Accept-Ranges`. Up to 8 connections per download (default 4), independent
  segment files, validated `206` responses, dynamic splitting of the largest remaining part when a connection
  becomes idle, and automatic step-down when servers throttle parallel requests (`429`/`503`, `Retry-After`).
- **Safe fallback** to a single connection when a server ignores ranges or returns inconsistent `Content-Range` values.
- **Pause and resume** with durable checkpoints (data is flushed to disk before progress is recorded), `If-Range`
  validation with ETag/Last-Modified, and detection of changed source files (the download restarts instead of
  mixing old and new data). Non-resumable servers are detected and the user is warned before pausing.
- **Recovery**: interrupted downloads survive app exit or crash; on startup, partial files are reconciled with the
  database and missing data is never treated as resumable.
- **Queue**: persistent order, priorities, maximum simultaneous downloads, start/pause queue, scheduled downloads,
  daily queue start/stop times, missed-schedule policy, automatic resume on startup (configurable).
- **Bandwidth limits**: global and per-download token-bucket limits that apply across all connections.
- **Files**: filename sanitization (path traversal, reserved names, invalid characters, bidi spoofing, long paths),
  conflict handling (rename / replace with confirmation / skip — never silent overwrite), atomic finalization,
  partial data stored next to the destination (`.oledm-partial`) so it never fills the system drive, disk-space
  checks, Mark-of-the-Web on finished files (NTFS), optional SHA-256 and checksum verification.
- **External drives**: an unavailable destination puts the download in *Waiting for Destination*; it resumes when
  the drive returns (tested with `subst` drives; see Known limitations).
- **Interface**: WPF window with sidebar sections and categories with live counts, sortable/searchable list,
  details panel with a segment map, context menu, keyboard shortcuts, drag-and-drop of links, light/dark/system
  theme, notification-area icon, Windows notifications, window placement memory, single instance.
- **Browser integration** (Chrome/Edge): context-menu "Download with OleDownloadManager", optional capture of
  browser downloads for chosen file types, connection status in the popup, native messaging host with origin
  and schema validation.
- **Installer**: per-user MSI (no administrator rights), Start Menu shortcut, optional desktop shortcut, browser
  integration feature, clean uninstall that keeps the user's history and downloaded files.
- **Media and playlists (optional)**: lists the items of a page or playlist through a user-installed
  [yt-dlp](https://github.com/yt-dlp/yt-dlp) and adds selected items as normal downloads (see below).

### Partially implemented / limitations

- **Notifications** are delivered through the notification-area icon (balloon tips, shown as toasts by Windows 10/11).
  With the icon disabled, notifications appear only in the status bar.
- **Launch at sign-in** creates a Startup-folder shortcut. Shortcut creation is covered by a unit test (against a
  temporary folder); an actual sign-in cycle was not automated.
- **Code signing** is supported by `scripts/package.ps1 -CertificateThumbprint`, but no certificate was available,
  so that path is untested and the produced artifacts are **unsigned** (SmartScreen will warn).
- **Firefox**: extension and host registration are provided; not tested end-to-end.

### Not implemented (planned)

Per-host connection limits, import/export, bandwidth schedules, floating progress window, compact mode, completion
sound, store-published extensions, authenticated-browser (cookie) hand-off — the last one deliberately, see
`docs/SECURITY.md`.

## Media and playlists (yt-dlp)

The **Media** button (Ctrl+M) lists the entries of a playlist or media page using **yt-dlp, which you install
separately** (for example `winget install yt-dlp.yt-dlp`; merging best video+audio also needs ffmpeg).
OleDownloadManager contains no site-specific extraction code; it launches yt-dlp with your user configuration
ignored, shows its progress, and finalizes the file like any other download.

Only download media you have the right to save — your own uploads, content whose licence permits downloading, or
content you have permission to download. Many sites' terms of service restrict downloading. DRM-protected media is
not supported. The dialog shows this notice before first use.

## Building

Prerequisites: Windows 10/11, [.NET SDK 10.0](https://dotnet.microsoft.com/download) (10.0.300 used), PowerShell 5.1+,
Node.js 22+ (only for the browser end-to-end test). Visual Studio is optional. Packages restore from nuget.org
(`nuget.config` is included).

```powershell
./scripts/build.ps1                       # restore + Debug build (warnings are errors)
./scripts/build.ps1 -Configuration Release
./scripts/test.ps1                        # unit + integration + UI tests
./scripts/test.ps1 -Browser chrome        # + real browser end-to-end test (also: edge)
./scripts/publish.ps1                     # self-contained build -> artifacts/publish/win-x64
./scripts/package.ps1                     # MSI + portable zip + extension zips -> artifacts/
```

## Running

- From source: `dotnet run --project src/OleDownloadManager.App` (or run the built `OleDownloadManager.exe`).
- Installed: Start Menu → OleDownloadManager.
- Command line: `OleDownloadManager.exe <url>` opens the Add Download dialog; `--background` starts in the
  notification area. A second launch hands its URL to the running instance.

Keyboard shortcuts: Ctrl+N add, Ctrl+V paste link (outside text fields), Ctrl+M media, Space pause/resume,
Ctrl+R retry, Delete remove, Enter open, Ctrl+F search, F9 details panel, Ctrl+, settings.

## Where things are stored

| What | Location |
|---|---|
| Program (installed) | `%LOCALAPPDATA%\Programs\OleDownloadManager` |
| History database and settings | `%LOCALAPPDATA%\OleDownloadManager\oledm.db` (SQLite) |
| Diagnostic logs (14 days, redacted) | `%LOCALAPPDATA%\OleDownloadManager\logs` |
| Partial downloads | `<destination folder>\.oledm-partial\<id>\` (hidden) |
| Saved credentials (opt-in) | Windows Credential Manager, `OleDownloadManager:download:<id>` |
| Downloads | Windows *Downloads* folder by default; any folder or drive can be chosen per download or in Settings |

Set the environment variable `OLEDM_DATA_DIR` to keep data elsewhere (used by the tests; also works for portable use).

## Browser integration

The installer registers the native messaging host for Chrome, Edge and Firefox (current user). Then load the
extension from `%LOCALAPPDATA%\Programs\OleDownloadManager\browser-extensions\chrome` via
`chrome://extensions` → Developer mode → Load unpacked. Details, Firefox steps and the acceptance checklist are in
[docs/BROWSER-INTEGRATION.md](docs/BROWSER-INTEGRATION.md).

## Installer

`scripts/package.ps1` builds `artifacts/installer/OleDownloadManager-<version>-x64.msi` with
[WiX Toolset 5](https://wixtoolset.org). WiX 5 was chosen because it is restored from NuGet as an MSBuild SDK
(reproducible builds, no machine-wide install), produces standard MSI packages that support per-user installation
and clean uninstall, and is MS-RL licensed. (WiX 6 and 7 require accepting an additional maintenance-fee EULA, which
is a decision for the project owner.) Silent install: `msiexec /i OleDownloadManager-1.0.0-x64.msi /qn`
(add `ADDLOCAL=ALL` for the desktop shortcut). Uninstall from Settings → Apps or `msiexec /x <msi> /qn`.
Uninstalling never deletes `%LOCALAPPDATA%\OleDownloadManager` or downloaded files; remove that folder manually to
erase history.

## Troubleshooting

| Problem | What to try |
|---|---|
| SmartScreen warns about the installer | Builds are unsigned unless you sign them. Choose *More info → Run anyway* only if you built or trust the file. |
| Extension shows "Not connected" | Settings → Browser shows the registration status; *Register for this user* repairs it. Check `%LOCALAPPDATA%\OleDownloadManager\logs\browserhost-*.log`. |
| Download restarts after pausing | The server does not support byte ranges; the app warns before pausing such downloads. |
| "Waiting for Destination" | The target drive or network share is unavailable. Reconnect it; the download continues automatically. |
| Only one connection is used | The server did not confirm range support, the file is small (< 2 MiB), or the server throttled parallel requests. |
| Media button says yt-dlp not found | Install yt-dlp or set its path in Settings → Media. |

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — modules, data flow, threading, persistence
- [docs/SECURITY.md](docs/SECURITY.md) — threat model and protections
- [docs/TESTING.md](docs/TESTING.md) — test suites, commands and coverage
- [docs/BROWSER-INTEGRATION.md](docs/BROWSER-INTEGRATION.md) — extensions, native host, acceptance checklist

## Known limitations

- Physical removal of a USB drive during a transfer was not automated; drive loss is simulated with `subst` and
  covered by error-classification tests. Writes in flight when a drive disappears are discarded and resumed from the
  last durable checkpoint.
- Mark-of-the-Web cannot be stored on FAT32/exFAT drives (a Windows limitation shared by browsers).
- Speeds depend on the server and connection; more connections are not always faster, and many CDNs throttle
  parallel requests (the engine reduces connections automatically).
- Schedules only run while the app is running and the computer is awake.

## License

MIT — see [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
