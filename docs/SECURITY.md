# Security

OleDownloadManager downloads untrusted content from untrusted servers, accepts input from browsers and other
processes, and writes files to user-chosen locations. This document describes what it protects against and how.

## Threat model

| Actor / input | Assumed capability | Main risks |
|---|---|---|
| Remote server | Fully malicious: arbitrary headers, bodies, redirects, timing | Path traversal via file names, overwriting files, protocol downgrade, credential leakage, resource exhaustion, mixing data from different files |
| Web page in the browser | Can trigger downloads and present links | Getting the app to fetch unexpected schemes or local resources |
| Other browser extensions / local processes of other users | Can try to talk to the native host or pipe | Injecting downloads, reading data |
| Local malware running as the same user | Out of scope — it already has the user's privileges |

The app makes no claim that downloaded content is safe. It never executes downloaded files on its own.

## Protections

### Network
- Only `http` and `https` URLs are accepted (user input, browser messages, redirects). URLs with embedded
  credentials are rejected; credentials go in the Authentication fields.
- TLS certificate validation is always on; there is no option to disable it.
- Redirects are followed manually: at most 10 hops, only to HTTP(S), and **HTTPS → HTTP downgrades are refused**.
- Credentials (Basic auth) and sensitive headers (`Authorization`, `Cookie`, anything containing token/auth/key/
  secret/session/password/signature) are sent **only to the origin the user entered**, never to redirect targets on
  other hosts. The `Referer` is reduced to scheme/host/path and never sent from HTTPS to HTTP.
- Engine-controlled headers (`Host`, `Range`, `If-Range`, `Accept-Encoding`, …) cannot be overridden.
- Every `206` response is validated against the requested range and the recorded representation (size, strong
  ETag via `If-Range`), so data from different versions of a file is never combined.
- Timeouts on connect, response headers and stalled reads; responses larger than announced are rejected.

### Files
- File names from URLs, `Content-Disposition`, browsers and users are sanitized: path components removed, invalid
  characters replaced, Windows reserved names (`CON`, `COM1`, …) prefixed, trailing dots/spaces trimmed, bidi
  override characters removed (prevents `invoice<U+202E>fdp.exe` showing as `invoiceexe.pdf`), length limited.
- The final path is verified to stay inside the destination folder.
- Existing files are never overwritten silently: the default is to rename; *Replace* requires explicit confirmation.
- Partial data lives in a hidden `.oledm-partial\<id>` folder; deletion is restricted to such folders.
- Finished files are moved into place atomically and tagged with **Mark-of-the-Web** (`Zone.Identifier`, Internet
  zone, query strings removed) so SmartScreen and Office Protected View apply. The app never removes MOTW.
  FAT32/exFAT drives cannot store MOTW.
- SHA-256 can be verified against a user-supplied value; a locally computed hash alone does not prove authenticity.

### Secrets and local data
- Passwords and sensitive headers are kept in memory only, unless the user opts in to store them in **Windows
  Credential Manager** (per-user, local machine). They are never written to SQLite, settings or logs; a test checks
  the database file does not contain them.
- Logs redact query strings, user info, `Authorization`/`Cookie` values and bearer tokens.
- User data is stored under `%LOCALAPPDATA%\OleDownloadManager`, separate from program files.

### Browser messaging and IPC
- No local HTTP server is opened. The extension talks to a native messaging host that the browser launches.
- The host is registered under HKCU for **specific extension IDs only** (no wildcards) and additionally checks the
  caller ID passed by the browser, exiting immediately for unknown callers.
- Messages are size-limited (64 KiB), schema-validated (known types, protocol version, request-ID format, URL
  scheme), and only `hello`, `getStatus` and `addDownload` are accepted from the browser.
- stdout carries protocol messages only; diagnostics go to a log file.
- The host talks to the app over a named pipe created with `PipeOptions.CurrentUserOnly` (other users cannot
  connect, and the client verifies the server belongs to the same user). The pipe name is derived from the user
  identity.
- The extension does not read page content, cookies or history. It sees only links the user sends and downloads the
  browser starts (capture is limited to listed file types and can be turned off). Captured browser downloads are
  paused first and only canceled after the app accepted them.
- Forwarding browser cookies or session tokens for authenticated downloads is **not implemented** by design; any
  future feature must be explicit, limited in scope and separately reviewed.

### Privileges and execution
- The app and installer run without administrator rights (`asInvoker` manifest, per-user MSI).
- No downloaded file is opened unless the user chooses *Open*; Windows then applies its own checks.
- The optional yt-dlp integration runs only a user-installed executable, with `--ignore-config` (so user config
  files cannot add `--exec` commands), arguments passed as an argument list (no shell), and the output path verified
  to be inside the expected folder. DRM circumvention is not supported.
- No secrets or signing keys are stored in the repository. The browser extension's manifest contains only a public
  key (to fix the development extension ID).

## Reporting

Report security issues privately to the project maintainers rather than in public issue trackers.
