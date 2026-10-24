<#
.SYNOPSIS
    Restores and builds the OleDownloadManager solution.
.EXAMPLE
    ./scripts/build.ps1
.EXAMPLE
    ./scripts/build.ps1 -Configuration Release
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'OleDownloadManager.sln'

Write-Host "Restoring packages..."
dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed ($LASTEXITCODE)" }

Write-Host "Building ($Configuration)..."
# Warnings are treated as errors so new warnings are caught early.
dotnet build $solution -c $Configuration --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }

Write-Host "Build succeeded."
