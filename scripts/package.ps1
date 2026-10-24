<#
.SYNOPSIS
    Builds the release artifacts: MSI installer, portable zip, browser extension packages and checksums.
.DESCRIPTION
    1. Publishes a self-contained build (scripts/publish.ps1) unless -SkipPublish.
    2. Builds the per-user MSI with WiX Toolset 5 (restored from NuGet).
    3. Creates a portable zip and browser extension zips.
    4. Optionally Authenticode-signs binaries and the MSI when -CertificateThumbprint is given
       (certificate must be in the current user's store). Without it, artifacts are unsigned.
    Output: artifacts/
.EXAMPLE
    ./scripts/package.ps1
#>
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$SkipPublish,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts'
$publishDir = Join-Path $artifacts "publish\$Runtime"
$platform = $Runtime.Split('-')[1]

[xml]$props = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'Version not found in Directory.Build.props' }

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'publish.ps1') -Runtime $Runtime
}
if (-not (Test-Path (Join-Path $publishDir 'OleDownloadManager.exe'))) { throw "No published build at $publishDir" }

function Get-SignTool {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    Get-ChildItem -Path $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*\x64\*' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}

function Invoke-Sign([string[]]$Files) {
    if (-not $CertificateThumbprint) { return }
    $signtool = Get-SignTool
    if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK).' }
    & $signtool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 @Files
    if ($LASTEXITCODE -ne 0) { throw "Signing failed ($LASTEXITCODE)" }
}

Invoke-Sign (Get-ChildItem $publishDir -Filter 'OleDownloadManager*.exe' | ForEach-Object FullName)

Write-Host "Building MSI ($version, $platform)..."
$installerOut = Join-Path $artifacts 'installer'
dotnet build (Join-Path $repoRoot 'installer\OleDownloadManager.Installer.wixproj') -c Release `
    "-p:PublishDir=$publishDir" "-p:ProductVersion=$version" "-p:InstallerPlatform=$platform" "-p:Platform=$platform" `
    -o $installerOut
if ($LASTEXITCODE -ne 0) { throw "MSI build failed ($LASTEXITCODE)" }
$msi = Join-Path $installerOut "OleDownloadManager-$version-$platform.msi"
if (-not (Test-Path $msi)) { throw "MSI not found at $msi" }
Invoke-Sign @($msi)

Write-Host 'Creating portable zip...'
$zip = Join-Path $artifacts "OleDownloadManager-$version-$Runtime-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip -CompressionLevel Optimal

Write-Host 'Packaging browser extensions...'
$extOut = Join-Path $artifacts 'extensions'
New-Item -ItemType Directory -Force -Path $extOut | Out-Null
$chromeZip = Join-Path $extOut "OleDownloadManager-chrome-$version.zip"
$firefoxZip = Join-Path $extOut "OleDownloadManager-firefox-$version.zip"
Remove-Item $chromeZip, $firefoxZip -ErrorAction SilentlyContinue
# Store uploads must not contain the development "key"; the store assigns the published ID.
$storeStage = Join-Path $env:TEMP "oledm-chrome-store-$([guid]::NewGuid().ToString('N'))"
Copy-Item -Recurse (Join-Path $publishDir 'browser-extensions\chrome') $storeStage
$manifestPath = Join-Path $storeStage 'manifest.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$manifest.PSObject.Properties.Remove('key')
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding $false))
Compress-Archive -Path (Join-Path $storeStage '*') -DestinationPath $chromeZip
Remove-Item -Recurse -Force $storeStage
Compress-Archive -Path (Join-Path $publishDir 'browser-extensions\firefox\*') -DestinationPath $firefoxZip

$checksums = Join-Path $artifacts 'SHA256SUMS.txt'
Get-ChildItem -Path $msi, $zip, $chromeZip, $firefoxZip | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content $checksums -Encoding ASCII

Write-Host "`nArtifacts:" -ForegroundColor Green
Get-ChildItem -Path $msi, $zip, $chromeZip, $firefoxZip, $checksums | ForEach-Object {
    Write-Host ('  {0,-55} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB))
}
if (-not $CertificateThumbprint) {
    Write-Host "`nNote: artifacts are NOT code-signed. Windows SmartScreen may warn when they are first run." -ForegroundColor Yellow
}
