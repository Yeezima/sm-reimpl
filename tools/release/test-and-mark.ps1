<#
.SYNOPSIS
  Runs the ship's checks (dotnet test, then smtool refine-register --check) on this checkout and, when both pass on a clean tree, records it for the
  HEAD commit in %LOCALAPPDATA%\sm-reimpl\tmp-build\tested\<commit sha>. A ship (ship.cmd) of the same commit within 24 hours then skips its own
  test run and says so. With uncommitted or untracked changes the checks still run, but nothing is recorded (the tested code is not the commit's).
  Exit 0 = both passed; 1 = a check failed.
.PARAMETER Check   only report whether HEAD has a valid marker (exit 0 yes, 1 no); runs nothing
#>
[CmdletBinding()]
param([switch]$Check)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tested-marker.ps1')
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dataRoot = Get-TestedDataRoot
$sha = (git -C $RepoRoot rev-parse HEAD).Trim()
if ($Check) {
    $when = Read-TestedMarker $dataRoot $sha
    if ($when) { Write-Host "HEAD $($sha.Substring(0, 7)): tests passed at $($when.ToString('yyyy-MM-dd HH:mm')) UTC (within $TestedMaxAgeHours h): a ship skips them." -ForegroundColor Green; exit 0 }
    Write-Host "HEAD $($sha.Substring(0, 7)): no test marker within $TestedMaxAgeHours h: a ship runs the tests." -ForegroundColor Yellow
    exit 1
}
$clean = -not ((git -C $RepoRoot status --porcelain --untracked-files=normal) -join "`n").Trim()
$clock = [Diagnostics.Stopwatch]::StartNew()
Write-Host "== dotnet test and refine-register --check on $($sha.Substring(0, 7))$(if (-not $clean) { ' (uncommitted changes: no marker will be written)' })" -ForegroundColor Cyan
$r = Invoke-ReleaseTests $RepoRoot
# The tree must still be the same clean commit afterwards (a branch switch or an edit during the run would make the marker lie).
$after = (git -C $RepoRoot rev-parse HEAD).Trim()
$cleanAfter = -not ((git -C $RepoRoot status --porcelain --untracked-files=normal) -join "`n").Trim()
$secs = '{0:0}' -f $clock.Elapsed.TotalSeconds
if ($r.Test -ne 0) { Write-Host "dotnet test failed (exit $($r.Test)) after $secs s; nothing recorded." -ForegroundColor Red; exit 1 }
if ($r.Refine -ne 0) { Write-Host "refine-register --check failed (exit $($r.Refine)) after $secs s: run smtool refine-register and commit; nothing recorded." -ForegroundColor Red; exit 1 }
if (-not $clean -or -not $cleanAfter -or $after -ne $sha) { Write-Host "Both passed in $secs s, but the tree was not the clean commit $($sha.Substring(0, 7)) throughout; nothing recorded." -ForegroundColor Yellow; exit 0 }
$path = Write-TestedMarker $dataRoot $sha
Write-Host "Both passed in $secs s; recorded for $($sha.Substring(0, 7)) ($path). A ship of this commit within $TestedMaxAgeHours h skips the tests." -ForegroundColor Green
exit 0
