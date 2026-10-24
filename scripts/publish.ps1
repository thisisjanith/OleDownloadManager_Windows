<#
.SYNOPSIS
    Produces a self-contained release folder (no .NET installation required on the target PC).
.DESCRIPTION
    Publishes the WPF app and the native messaging host into one folder, adds the browser extensions
    and the native messaging manifests (with paths relative to the manifest, so the folder can live
    anywhere). Output: artifacts/publish/<runtime>.

    Single-file publishing is deliberately not used: WPF and the native SQLite library would be extracted
    to a temporary folder at every start, which is slower and trips some antivirus products.
.EXAMPLE
    ./scripts/publish.ps1
.EXAMPLE
    ./scripts/publish.ps1 -Runtime win-arm64
#>
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

# Browsers parse these manifests as JSON; write UTF-8 without a byte-order mark.
function Write-Utf8NoBom([string]$Path, [Parameter(ValueFromPipeline)][string]$Text) {
    process { [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding $false)) }
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repoRoot "artifacts\publish\$Runtime"

if (Test-Path $output) { Remove-Item -Recurse -Force $output }
New-Item -ItemType Directory -Force -Path $output | Out-Null

$common = @('-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '-o', $output,
    '-p:DebugType=none', '-p:DebugSymbols=false', '-p:SatelliteResourceLanguages=en')

Write-Host "Publishing app ($Runtime, $Configuration)..."
dotnet publish (Join-Path $repoRoot 'src\OleDownloadManager.App\OleDownloadManager.App.csproj') @common
if ($LASTEXITCODE -ne 0) { throw "Publishing the app failed ($LASTEXITCODE)" }

Write-Host "Publishing native messaging host..."
dotnet publish (Join-Path $repoRoot 'src\OleDownloadManager.BrowserHost\OleDownloadManager.BrowserHost.csproj') @common
if ($LASTEXITCODE -ne 0) { throw "Publishing the native host failed ($LASTEXITCODE)" }

# Browser extensions: Chrome/Edge as-is; Firefox = shared files + Firefox manifest.
$extensions = Join-Path $output 'browser-extensions'
$chromeSource = Join-Path $repoRoot 'browser-extensions\chrome'
Copy-Item -Recurse $chromeSource (Join-Path $extensions 'chrome')
$firefoxOut = Join-Path $extensions 'firefox'
Copy-Item -Recurse $chromeSource $firefoxOut
Copy-Item -Force (Join-Path $repoRoot 'browser-extensions\firefox\manifest.json') (Join-Path $firefoxOut 'manifest.json')

# Native messaging manifests. Browsers resolve a relative "path" against the manifest's folder.
$hostName = 'com.oledm.downloadmanager'
[ordered]@{
    name            = $hostName
    description     = 'OleDownloadManager browser integration'
    path            = 'OleDownloadManager.BrowserHost.exe'
    type            = 'stdio'
    allowed_origins = @('chrome-extension://fjlipfmbkmopdjkjnfmpocodmcecbacf/')
} | ConvertTo-Json | Write-Utf8NoBom (Join-Path $output "$hostName.chrome.json")
[ordered]@{
    name               = $hostName
    description        = 'OleDownloadManager browser integration'
    path               = 'OleDownloadManager.BrowserHost.exe'
    type               = 'stdio'
    allowed_extensions = @('oledownloadmanager@oledm.local')
} | ConvertTo-Json | Write-Utf8NoBom (Join-Path $output "$hostName.firefox.json")

Copy-Item (Join-Path $repoRoot 'LICENSE') $output
Copy-Item (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') $output

foreach ($required in 'OleDownloadManager.exe', 'OleDownloadManager.BrowserHost.exe', 'e_sqlite3.dll') {
    if (-not (Get-ChildItem -Path $output -Recurse -Filter $required)) { throw "Publish output is missing $required" }
}

$size = (Get-ChildItem -Recurse $output | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("Published to {0} ({1:N1} MB)" -f $output, $size)
