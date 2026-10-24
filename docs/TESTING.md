# Testing

All automated tests run locally. They use an in-process Kestrel HTTP server, temporary folders, an isolated data
directory (`OLEDM_DATA_DIR`) and a private named-pipe suffix (`OLEDM_PIPE_SUFFIX`); none depend on public servers.

## Commands

```powershell
./scripts/test.ps1                      # unit + integration + UI
./scripts/test.ps1 -Suite Unit          # or Integration / UI
./scripts/test.ps1 -Browser chrome      # also the real-browser end-to-end test (or: edge)

dotnet test --project tests/OleDownloadManager.UnitTests
dotnet test --project tests/OleDownloadManager.IntegrationTests -- --filter-class "*ResumeAndQueueTests"
node tests/BrowserE2E/run-chrome-e2e.mjs --browser edge
node tests/BrowserE2E/run-chrome-e2e.mjs --installed   # against the MSI-installed app and its registration
```

The test projects use xUnit v3 on Microsoft.Testing.Platform (`global.json` opts in), so `dotnet test` needs
`--project`.

## Suites

### Unit tests (`tests/OleDownloadManager.UnitTests`)

| Area | What is covered |
|---|---|
| URL validation | HTTP/HTTPS only, embedded credentials rejected, whitespace/control characters, length limit, scheme guessing |
| File names | Traversal (`../`, `C:\`), invalid characters, reserved names, trailing dots/spaces, bidi overrides, truncation without splitting surrogate pairs, URL-derived names, counters (`.tar.gz`) |
| Headers | Parsing, forbidden and sensitive headers |
| State machine | Allowed/forbidden transitions, explicit-only restarts |
| Segment calculation | Exact coverage without gaps/overlap (fixed sizes up to 10 GiB, 2 000 randomized cases), small files, split arithmetic, layout validation |
| Content-Range | Valid forms, unsatisfied form, 13 malformed variants, matching |
| Progress and formatting | Fractions with unknown/zero sizes, ETA overflow/NaN, size/speed/duration formatting |
| Speed and throttling | Rolling window, averages; token bucket burst/debt, enforced rate with two concurrent consumers (±tolerance) |
| Retry policy | Exponential backoff, jitter bounds, `Retry-After` (delta/date, capped), transient vs. permanent |
| Queue scheduler | Free slots, manual > priority > position ordering, stopped queue, scheduled items, trash, daily time crossing (incl. midnight) |
| Error classification | HTTP status codes, socket errors, TLS, file-system HRESULTs (disk full, access denied, device removed, network share lost), incomplete responses |
| Resume manager | Partial-file reconciliation (matching, shorter, missing files), interrupted/active states, unavailable volumes, non-resumable downloads |
| Persistence | Migrations (fresh, from v1, refusing newer schema), round trip incl. dates/offsets/validators/headers, durable-bytes-only persistence, segment replacement, concurrent saves, delete cascade, settings round trip |
| File system | Conflict policies, reserved names, traversal-safe combine, MAX_PATH fitting, writability/free space, disk-full and missing-drive detection, disk-space margin, guarded temp deletion, no-overwrite finalization, Mark-of-the-Web |
| Browser protocol | Message validation (types, version, request IDs, URLs, sizes, nesting), framing edge cases, pipe round trip, manifests without wildcards |
| Media tool | Playlist/single-video parsing, unsafe entry URLs dropped, multi-stream progress accumulation |
| Launch at sign-in | Shortcut creation (temporary folder) |
| Logging | URL redaction and free-text secret scrubbing |

### Integration tests (`tests/OleDownloadManager.IntegrationTests`)

Real coordinator + engine + SQLite + file system against the local test server:

- Small file, 6 MiB segmented file (≥ 4 segments, parallel requests) byte-identical to a single-connection download
- Server ignoring `Range` (probe response reused), invalid `Content-Range` (rejected, single-connection fallback)
- Unknown length (chunked), redirects, redirect loops, 404 (no retries), 503 (retries then fails), flaky 500s (retried)
- Connections aborted mid-transfer (resumed from the correct offset, no corruption)
- `Content-Disposition` incl. traversal attempts and RFC 5987 Unicode names; gzip content coding
- Conflicts: never overwrite, Skip rejects, Replace replaces; 12 concurrent downloads claiming the same name (deadlock and race regression)
- Zero-length files; SHA-256 match and mismatch (file not saved)
- `429` + `Retry-After` with automatic connection reduction
- Segment-layout invariants on every progress snapshot
- Source changed mid-download and while paused (restart; versions never mixed)
- Pause/resume: persisted state matches files on disk, resumed requests start after offset 0, output byte-exact
- Non-resumable pause restarts from zero with a notice
- App restart: history, partial progress and queue state restored; auto-resume on/off; missing partial files are not treated as resumable
- Cancel removes partial data; trash/restore/permanent delete keeps completed files
- Queue concurrency limit never exceeded; priority order; scheduled start; global and per-download speed limits
- Unavailable destination drive (simulated with `subst`): *Waiting for Destination*, notification, automatic resume
- Failed state consistent after reload; sensitive headers absent from the database file (incl. WAL)
- Native messaging host executable: forwarding, multiple messages, app not running, invalid messages, unknown callers, Firefox arguments, oversized frames
- yt-dlp integration (skipped if yt-dlp is not installed): inspection, progress, output path, completion through the coordinator
- 512 MiB download: memory growth stays bounded (measured: 9 MiB growth, ~350 MiB/s locally)

### UI tests (`tests/OleDownloadManager.UITests`)

- **View-model scenarios** (on an STA dispatcher thread, real engine): add URL with probe (name, size, category),
  invalid URLs, changing the destination, pause/resume via commands and the Space toggle, cancel with
  confirmation, retry, search and sidebar filtering, open file / reveal / copy path via the shell service, trash and
  empty trash, paste from clipboard, sorting, saving and validating settings, history restored after restart.
- **Real application** via Windows UI Automation: launch with a URL, confirm the Add Download dialog, file
  downloaded and listed, search filters the real list, window closed and app relaunched with history restored;
  second instance hands its URL to the running instance.

### Browser end-to-end (`tests/BrowserE2E/run-chrome-e2e.mjs`)

Launches Chrome or Edge headless with a temporary profile, loads the unpacked extension through the DevTools protocol
(`Extensions.loadUnpacked`), and verifies: extension ID, native-messaging connection status, context-menu send →
host → pipe → app dialog (confirmed via UI Automation) → byte-exact file, duplicate suppression, automatic capture of
a browser download with the browser copy canceled, and refusal of non-HTTP URLs. The native-messaging registry key is
created for the run and restored afterwards (`--installed` uses the MSI's registration instead).

## Results on the development machine (Windows 11 26200, .NET 10.0.300)

| Suite | Result |
|---|---|
| Unit | 239 passed |
| Integration | 53 passed (yt-dlp 2026.07.04 installed, so media tests ran) |
| UI | 16 passed |
| Browser E2E | Chrome 155: pass · Edge 155: pass · installed MSI build with Chrome: pass |
| Firefox | not run (Firefox not installed) |

## Manual verification performed

- Published Release build: real HTTPS downloads from public servers — a 100 MiB file over multiple connections
  (SHA-256 identical to `curl`), a server ignoring ranges (single-connection fallback), and a 1 GiB file
  (≈ 8 MB/s, working set flat at ≈ 208 MB, UI responsive; the server throttled parallel connections and the
  engine reduced them automatically).
- MSI: silent per-user install with all features, files/shortcuts/registry verified, installed native host
  auto-started the app, Chrome E2E against the installed build, silent uninstall removed program files, shortcuts and
  registry entries and left user data untouched.

## Not covered by automation

Physical USB-drive removal during a transfer, sign-in auto-start, Windows notification display, high-DPI rendering at
every scale factor, ARM64, Firefox, and code signing. These are listed as known limitations in the README.
