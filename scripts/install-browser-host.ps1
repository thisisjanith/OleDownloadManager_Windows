<#
.SYNOPSIS
    Registers the OleDownloadManager native messaging host for Chrome, Edge and Firefox (current user only).
.DESCRIPTION
    Writes host manifests and the HKCU registry keys browsers use to find them. No administrator rights are
    needed. Only the listed extension IDs may connect; wildcard origins are never written.
    The MSI installer performs the same registration automatically; use this script for development builds.
.EXAMPLE
    ./scripts/install-browser-host.ps1
.EXAMPLE
    ./scripts/install-browser-host.ps1 -ChromeExtensionId abcdefghijklmnopabcdefghijklmnop
#>
param(
    [string]$HostPath,
    [string[]]$ChromeExtensionId = @('fjlipfmbkmopdjkjnfmpocodmcecbacf'),
    [string[]]$FirefoxExtensionId = @('oledownloadmanager@oledm.local'),
    [string]$ManifestDirectory = (Join-Path $env:LOCALAPPDATA 'OleDownloadManager\native-messaging'),
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

# Browsers parse these manifests as JSON; write UTF-8 without a byte-order mark.
function Write-Utf8NoBom([string]$Path, [Parameter(ValueFromPipeline)][string]$Text) {
    process { [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding $false)) }
}
$hostName = 'com.oledm.downloadmanager'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $HostPath) {
    $HostPath = Join-Path $repoRoot "src\OleDownloadManager.BrowserHost\bin\$Configuration\net10.0-windows\OleDownloadManager.BrowserHost.exe"
}
$HostPath = [System.IO.Path]::GetFullPath($HostPath)
if (-not (Test-Path -LiteralPath $HostPath)) {
    throw "Native host not found at $HostPath. Build the solution first (scripts/build.ps1)."
}

foreach ($id in $ChromeExtensionId) {
    if ($id -notmatch '^[a-p]{32}$') { throw "Invalid Chrome/Edge extension ID: $id" }
}
foreach ($id in $FirefoxExtensionId) {
    if ([string]::IsNullOrWhiteSpace($id) -or $id.Contains('*')) { throw "Invalid Firefox extension ID: $id" }
}

New-Item -ItemType Directory -Force -Path $ManifestDirectory | Out-Null
$chromeManifest = Join-Path $ManifestDirectory "$hostName.chrome.json"
$firefoxManifest = Join-Path $ManifestDirectory "$hostName.firefox.json"

[ordered]@{
    name            = $hostName
    description     = 'OleDownloadManager browser integration'
    path            = $HostPath
    type            = 'stdio'
    allowed_origins = @($ChromeExtensionId | ForEach-Object { "chrome-extension://$_/" })
} | ConvertTo-Json | Write-Utf8NoBom $chromeManifest

[ordered]@{
    name               = $hostName
    description        = 'OleDownloadManager browser integration'
    path               = $HostPath
    type               = 'stdio'
    allowed_extensions = @($FirefoxExtensionId)
} | ConvertTo-Json | Write-Utf8NoBom $firefoxManifest

# The host also checks the caller itself; tell it about any non-default IDs.
$defaultChrome = @('fjlipfmbkmopdjkjnfmpocodmcecbacf')
$defaultFirefox = @('oledownloadmanager@oledm.local')
$extraChrome = @($ChromeExtensionId | Where-Object { $defaultChrome -notcontains $_ })
$extraFirefox = @($FirefoxExtensionId | Where-Object { $defaultFirefox -notcontains $_ })
$allowedFile = Join-Path (Split-Path -Parent $HostPath) 'browserhost.allowed.json'
if ($extraChrome.Count -gt 0 -or $extraFirefox.Count -gt 0) {
    [ordered]@{ chromium = $extraChrome; firefox = $extraFirefox } | ConvertTo-Json | Write-Utf8NoBom $allowedFile
}

$keys = @{
    'HKCU:\Software\Google\Chrome\NativeMessagingHosts'  = $chromeManifest
    'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts' = $chromeManifest
    'HKCU:\Software\Mozilla\NativeMessagingHosts'        = $firefoxManifest
}
foreach ($parent in $keys.Keys) {
    $key = Join-Path $parent $hostName
    New-Item -Path $key -Force | Out-Null
    Set-Item -Path $key -Value $keys[$parent]
    Write-Host "Registered $key -> $($keys[$parent])"
}

Write-Host "Native messaging host registered for the current user."
