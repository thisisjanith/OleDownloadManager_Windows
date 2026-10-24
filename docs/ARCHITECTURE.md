# Architecture

OleDownloadManager is split into projects with one-way dependencies. The download engine has no WPF
dependency and is tested without a UI.

```
OleDownloadManager.App (WPF, MVVM) ──┐
OleDownloadManager.BrowserHost ──────┤
                                     ▼
              OleDownloadManager.Infrastructure (SQLite, file system, Windows APIs, yt-dlp)
                                     ▼
              OleDownloadManager.Engine (HTTP, segments, queue, retries, throttling)
                                     ▼
              OleDownloadManager.Core (models, state machine, validation, protocol)
```

| Project | Target | Responsibility |
|---|---|---|
| `Core` | `net10.0` | Domain model (`DownloadItem`, `DownloadSegment`, immutable `DownloadSnapshot`), `DownloadStateMachine`, validation (`UrlValidator`, `FileNameSanitizer`, `HeaderValidator`, `LogRedactor`), `ContentRange` parsing, `SegmentCalculator`, `CategoryClassifier`, settings model, service interfaces, browser/IPC protocol and named-pipe transport. |
| `Engine` | `net10.0` | `DownloadCoordinator` (queue, concurrency, lifecycle), `DownloadJob` (one run of one download), `HttpDownloadEngine` (probe + single connection), `SegmentedDownloadEngine` (parallel ranges + assembly), `HttpRequestSender` (redirect policy), `ResumeManager`, `RetryPolicy`, `SpeedMonitor`, `BandwidthLimiter`, `QueueScheduler`, `DownloadErrorClassifier`. |
| `Infrastructure` | `net10.0-windows` | SQLite persistence with migrations, settings store, `FileSystemService` (destinations, conflicts, temp data, finalization, Mark-of-the-Web), Windows Credential Manager, rolling file logger with redaction, native-messaging registration, Startup-folder shortcut, yt-dlp integration. |
| `App` | `net10.0-windows` (WPF) | Views, view models, theming, tray icon/notifications, dialogs, clipboard watcher, single instance, local pipe server, composition root (`App.xaml.cs`). |
| `BrowserHost` | `net10.0-windows` (console) | Native messaging host started by the browser; validates and forwards messages to the app over the pipe. |

## A download's life

1. **Add** — `AddDownloadViewModel` (or the browser host) builds a `DownloadRequest`. The coordinator validates the
   URL, sanitizes the name, resolves the destination (category folders if enabled) and conflicts, separates
   sensitive headers (kept in memory / Credential Manager), persists the record and asks the queue to pump.
2. **Schedule** — `QueueScheduler.SelectToStart` picks waiting items: manual starts first, then priority, queue
   position and creation time; scheduled items when their time arrives; never more than the global limit.
   All scheduling decisions run under one async gate, so concurrent requests cannot exceed the limit.
3. **Probe** — `HttpDownloadEngine.ProbeAsync` sends `Range: bytes=0-0`. Range support is reported only for a `206`
   whose `Content-Range` is exactly `bytes 0-0/<size>`. A `200` response is kept and reused as the single-connection
   transfer, so non-range servers are contacted once.
4. **Resume check** — if segments exist, size and validators (ETag/Last-Modified) must match; otherwise the partial
   data is discarded and the user sees why. Segment files are reconciled with recorded progress.
5. **Transfer** — segmented: `SegmentCalculator` creates contiguous ranges; workers request each remaining range with
   `If-Range`, validate the `206` (start offset, length, total size, ETag, no content coding) and append to that
   segment's own file. Idle workers split the largest remaining segment (the split point stays two buffers ahead
   of the active writer). Throttling responses reduce the connection count. Single connection: the response body is
   streamed (content coding decoded) and its length checked against `Content-Length`.
6. **Checkpoint** — every few seconds each worker flushes its file to the device (`Flush(true)`) and only then
   records the byte count as *durable*. The database stores durable counts only; on resume, any bytes beyond them
   are truncated. A crash or power loss can therefore lose seconds of progress but never corrupt the file.
7. **Verify and finalize** — segment lengths and layout are verified, segments are appended onto the first segment
   file (no full-file copy in memory), the final size is checked, SHA-256 computed if requested, the conflict policy
   applied again, and the file moved atomically into place (same volume). Mark-of-the-Web is written, temp data
   removed, and only then is the item marked Completed.

Failures are classified (`DownloadErrorClassifier`) into transient (retried with exponential backoff and jitter,
honouring `Retry-After`) and permanent errors. Special cases: changed source → restart from scratch (max twice);
invalid ranges → single connection; expired redirect target (403/404 on a CDN URL) → re-resolve the original URL
once; unavailable destination → *Waiting for Destination*, rechecked every few seconds.

## State machine

`DownloadStateMachine` defines allowed transitions, e.g. Queued → Connecting → Downloading → Verifying → Completed;
Downloading → Paused / Interrupted / Failed / Retrying / Waiting for Destination; Failed → Queued (retry).
Completed and Canceled downloads can only return to Queued through an explicit user action ("Download Again").

## Threading

- The engine is fully asynchronous; each job runs on the thread pool. `DownloadItem` mutations happen under the
  item's `SyncRoot`; consumers only see immutable `DownloadSnapshot`s.
- The coordinator raises events on background threads. `MainViewModel` stores the latest snapshot per download and
  applies them on the UI thread every 250 ms, so a fast download cannot flood the dispatcher.
- SQLite access runs on the thread pool behind a write lock; each save snapshots the item inside the lock so the
  newest state is always written last.

## Persistence

SQLite database (`oledm.db`, WAL mode). Schema versions are tracked with `PRAGMA user_version`; each migration runs
in a transaction, and a database from a newer app version is refused rather than modified.

| Table | Contents |
|---|---|
| `Downloads` | One row per download: URLs, names, folders, status, sizes, validators, options, queue position, errors. Non-sensitive custom headers as JSON. |
| `Segments` | Segment map per download: offsets, durable byte count, state, retries. |
| `Settings` | Application settings as one JSON document. |
| `DownloadEvents` | History of lifecycle events (added, started, paused, completed, failed …). |

Startup order: open database → migrate → load settings → load downloads → reconcile partial files → restore queue →
apply missed-schedule policy → resume eligible downloads.

## Browser integration

```
Extension ──native messaging (stdin/stdout, length-prefixed JSON)──▶ BrowserHost.exe
BrowserHost.exe ──named pipe (current user only, length-prefixed JSON)──▶ App (BrowserRequestHandler)
```

The host checks the caller's extension ID, validates every message (types, version, sizes, URLs), starts the app
if needed for `addDownload`, and relays the reply. See `BROWSER-INTEGRATION.md`.

## UI

MVVM without a framework: `ObservableObject`, `RelayCommand`, `AsyncRelayCommand` (no re-entrancy, errors routed to a
handler). Views contain only view plumbing (selection forwarding, drag-and-drop, window placement). Styles live in
`Resources/Styles.xaml`; colours in `Theme.Light.xaml` / `Theme.Dark.xaml`, swapped at runtime by `ThemeService`
(which also follows the Windows app theme and darkens title bars). The list uses a virtualized `ListView` with live
filtering/sorting; the details panel draws the segment map with a lightweight custom control (`SegmentBar`).

## Optional media module

`YtDlpMediaTool` (Infrastructure) implements `IMediaTool`. Media downloads are ordinary queue items with
`Kind = Media`; `DownloadJob` hands them to the tool (output template inside the partial folder, `--ignore-config`,
progress via `--progress-template`, final path via `--print after_move:filepath`) and then finalizes them like any
other download. Pausing stops the tool; yt-dlp continues from its `.part` file next time.
