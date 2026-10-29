<#
.SYNOPSIS
  One-shot M1 run: discover.ps1, then `smtool m1-report` with the cross-check and sample extraction.
  Everything is read-only against the install. Output goes to %LOCALAPPDATA%\sm-reimpl\.
#>
param([string]$GameDir)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Root = Join-Path $env:LOCALAPPDATA 'sm-reimpl'
$OutDir = Join-Path $Root ('discovery/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

$discoverArgs = @{ OutDir = $OutDir }
if ($GameDir) { $discoverArgs.GameDir = $GameDir }
& (Join-Path $PSScriptRoot 'discover.ps1') @discoverArgs
$GameDir = (Get-Content -LiteralPath (Join-Path $OutDir 'game-dir.txt') -TotalCount 1).Trim()

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (dotnet --list-sdks | Select-String '^10\.')) {
    Write-Host 'Installing the .NET 10 SDK (winget)...' -ForegroundColor Yellow
    winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

$report = Join-Path $OutDir 'm1-report.md'
dotnet run --project (Join-Path $RepoRoot 'src/SmTool') -c Release -- m1-report $GameDir `
    --discovery (Join-Path $OutDir 'report.json') --out $report --extract (Join-Path $Root 'cache')
$code = $LASTEXITCODE
Write-Host ''
Write-Host "M1 report: $report" -ForegroundColor $(if ($code -eq 0) { 'Green' } else { 'Yellow' })
Write-Host 'Share m1-report.md and report.md with Claude to fill in docs/discovery.md.'
exit $code
