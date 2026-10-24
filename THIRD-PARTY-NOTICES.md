# Third-party notices

OleDownloadManager redistributes the following components in its self-contained release.

| Component | Use | License |
|---|---|---|
| .NET runtime and Windows Desktop runtime (WPF, Windows Forms) | Application runtime | MIT |
| Microsoft.Data.Sqlite | SQLite access | MIT |
| SQLitePCLRaw (bundle_e_sqlite3) | Native SQLite bindings | Apache-2.0 |
| SQLite (e_sqlite3) | Embedded database engine | Public domain |
| Microsoft.Extensions.DependencyInjection / Logging | Composition and logging | MIT |

Build-time only (not redistributed): WiX Toolset 5 (MS-RL), xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT).

Optional, user-installed, not redistributed: yt-dlp (Unlicense) and ffmpeg (LGPL/GPL), used only when the
user enables the Media/Playlist feature and installs those tools themselves.
