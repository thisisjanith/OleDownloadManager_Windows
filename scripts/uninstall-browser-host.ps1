<#
.SYNOPSIS
    Removes the per-user native messaging registration created by install-browser-host.ps1.
#>
param(
    [string]$ManifestDirectory = (Join-Path $env:LOCALAPPDATA 'OleDownloadManager\native-messaging')
)

$ErrorActionPreference = 'Stop'
$hostName = 'com.oledm.downloadmanager'

foreach ($parent in 'HKCU:\Software\Google\Chrome\NativeMessagingHosts', 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts', 'HKCU:\Software\Mozilla\NativeMessagingHosts') {
    $key = Join-Path $parent $hostName
    if (Test-Path $key) {
        Remove-Item -Path $key -Recurse -Force
        Write-Host "Removed $key"
    }
}

foreach ($file in "$hostName.chrome.json", "$hostName.firefox.json") {
    $path = Join-Path $ManifestDirectory $file
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
        Write-Host "Removed $path"
    }
}

Write-Host "Native messaging host unregistered."
