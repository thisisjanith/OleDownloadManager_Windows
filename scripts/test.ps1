<#
.SYNOPSIS
    Runs the automated test suites.
.DESCRIPTION
    Unit, integration and UI tests use only local resources (an in-process HTTP server, temporary folders,
    a private pipe name). The browser end-to-end test launches Chrome or Edge with a temporary profile and
    temporarily registers the native messaging host for the current user; it is opt-in with -Browser.
.EXAMPLE
    ./scripts/test.ps1                     # unit + integration + UI tests
.EXAMPLE
    ./scripts/test.ps1 -Suite Unit
.EXAMPLE
    ./scripts/test.ps1 -Browser chrome     # also run the browser end-to-end test
#>
param(
    [ValidateSet('All', 'Unit', 'Integration', 'UI')]
    [string]$Suite = 'All',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('', 'chrome', 'edge')]
    [string]$Browser = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$projects = switch ($Suite) {
    'Unit' { @('tests\OleDownloadManager.UnitTests') }
    'Integration' { @('tests\OleDownloadManager.IntegrationTests') }
    'UI' { @('tests\OleDownloadManager.UITests') }
    default { @('tests\OleDownloadManager.UnitTests', 'tests\OleDownloadManager.IntegrationTests', 'tests\OleDownloadManager.UITests') }
}

$failed = @()
foreach ($project in $projects) {
    Write-Host "`n=== $project ===" -ForegroundColor Cyan
    dotnet test --project (Join-Path $repoRoot $project) -c $Configuration
    if ($LASTEXITCODE -ne 0) { $failed += $project }
}

if ($Browser) {
    Write-Host "`n=== Browser end-to-end ($Browser) ===" -ForegroundColor Cyan
    dotnet build (Join-Path $repoRoot 'OleDownloadManager.sln') -c $Configuration | Out-Null
    node (Join-Path $repoRoot 'tests\BrowserE2E\run-chrome-e2e.mjs') --browser $Browser --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { $failed += "browser-e2e-$Browser" }
}

if ($failed.Count -gt 0) {
    throw "Failed: $($failed -join ', ')"
}

Write-Host "`nAll selected tests passed." -ForegroundColor Green
