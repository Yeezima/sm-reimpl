# The "tests passed on this commit" marker shared by test-and-mark.ps1 and release.ps1 -Ship (dot-sourced by both).
# <data root>\tmp-build\tested\<full commit sha>: written only after `dotnet test` and `smtool refine-register --check` both passed on a clean tree at that
# commit. A ship of the same commit within $TestedMaxAgeHours skips its own test run. The marker is a plain text file:
#   sm-reimpl tests passed
#   commit: <sha>
#   utc: <yyyy-MM-ddTHH:mm:ssZ>
#   dotnet test: passed
#   refine-register --check: passed

$TestedMaxAgeHours = 24

function Get-TestedDataRoot {
    if ($env:SM_DATA_DIR) { return $env:SM_DATA_DIR }
    return (Join-Path $env:LOCALAPPDATA 'sm-reimpl')
}

function Get-TestedMarkerPath([string]$dataRoot, [string]$sha) {
    if ($sha -notmatch '^[0-9a-f]{40}$') { throw "Not a full commit sha: '$sha'" }
    return (Join-Path $dataRoot "tmp-build\tested\$sha")
}

# The UTC time the tests passed on $sha, or $null when there is no usable marker: missing, unreadable, another commit, a check missing, older than
# $TestedMaxAgeHours, or dated more than five minutes in the future.
function Read-TestedMarker([string]$dataRoot, [string]$sha) {
    try {
        $path = Get-TestedMarkerPath $dataRoot $sha
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
        $lines = @(Get-Content -LiteralPath $path -TotalCount 20 -ErrorAction Stop)
        if ($lines -notcontains "commit: $sha" -or $lines -notcontains 'dotnet test: passed' -or $lines -notcontains 'refine-register --check: passed') { return $null }
        $utcLine = [string]($lines | Where-Object { $_ -like 'utc: *' } | Select-Object -First 1)
        if ($utcLine -notmatch '^utc: (\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z)$') { return $null }
        $when = [DateTime]::ParseExact($Matches[1], "yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal)
        $age = [DateTime]::UtcNow - $when
        if ($age.TotalHours -gt $TestedMaxAgeHours -or $age.TotalMinutes -lt -5) { return $null }
        return $when
    } catch { return $null }
}

function Write-TestedMarker([string]$dataRoot, [string]$sha) {
    $path = Get-TestedMarkerPath $dataRoot $sha
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    $text = "sm-reimpl tests passed`ncommit: $sha`nutc: $([DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture))`ndotnet test: passed`nrefine-register --check: passed`n"
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false)))
    return $path
}

# The checkout's state: HEAD's sha and whether the tree is clean (no changes, no untracked files that git does not ignore).
function Get-TreeState([string]$repoRoot) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $sha = (& git -C $repoRoot rev-parse HEAD 2>$null | Out-String).Trim()
        $headOk = $LASTEXITCODE -eq 0
        $status = (& git -C $repoRoot status --porcelain --untracked-files=normal 2>$null | Out-String).Trim()
        $statusOk = $LASTEXITCODE -eq 0
        return @{ Sha = $sha; Clean = ($headOk -and $statusOk -and -not $status) }
    } finally { $ErrorActionPreference = $old }
}

# The two checks a ship needs (the ship preflight and test-and-mark.ps1 both call this). Returns @{ Test = <exit>; Refine = <exit>; Sha = <HEAD before>;
# Stable = <bool> } (refine-register is not run when dotnet test failed: Refine = $null). Stable is true only when the tree was clean and at the same HEAD
# before and after the run: a marker may be written only for a Stable run of exactly the commit it names (Write-TestedMarkerIfStable).
function Invoke-ReleaseTests([string]$repoRoot) {
    $before = Get-TreeState $repoRoot
    $result = @{ Test = $null; Refine = $null; Sha = $before.Sha; Stable = $false }
    Push-Location $repoRoot
    try {
        & dotnet test -nologo -v quiet | Out-Host
        $result.Test = $LASTEXITCODE
        if ($result.Test -ne 0) { return $result }
        # The default paths, resolved from the repository root (the current folder): with absolute --spec/--changes/--out the generator names the files by
        # their leaf ("behavior.md") in the register's text, so the check against the committed register (which says "spec/behavior.md") always failed.
        & dotnet run --project (Join-Path $repoRoot 'src\SmTool\SmTool.csproj') -c Release -- refine-register --check | Out-Host
        $result.Refine = $LASTEXITCODE
        return $result
    } finally {
        Pop-Location
        $after = Get-TreeState $repoRoot
        $result.Stable = $before.Clean -and $after.Clean -and $before.Sha -and ($before.Sha -eq $after.Sha)
    }
}

# Writes the marker only for a run whose two checks passed on a tree that stayed clean at $sha throughout. Returns the marker's path, or $null with
# $why set to the reason nothing was written.
function Write-TestedMarkerIfStable([string]$dataRoot, [string]$sha, $result, [ref]$why) {
    if ($result.Test -ne 0 -or $result.Refine -ne 0) { $why.Value = 'a check failed'; return $null }
    if (-not $result.Stable) { $why.Value = 'the tree was not clean, or HEAD moved, during the run'; return $null }
    if ($result.Sha -ne $sha) { $why.Value = "the tests ran on $($result.Sha), not on $sha"; return $null }
    return (Write-TestedMarker $dataRoot $sha)
}
