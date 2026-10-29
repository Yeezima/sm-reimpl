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

# The two checks a ship needs, exactly as the ship preflight runs them. Returns @{ Test = <exit>; Refine = <exit> } (refine-register is not run when
# dotnet test failed: Refine = $null).
function Invoke-ReleaseTests([string]$repoRoot) {
    $result = @{ Test = $null; Refine = $null }
    Push-Location $repoRoot
    try {
        & dotnet test -nologo -v quiet | Out-Host
        $result.Test = $LASTEXITCODE
        if ($result.Test -ne 0) { return $result }
        & dotnet run --project (Join-Path $repoRoot 'src\SmTool\SmTool.csproj') -c Release -- refine-register --check --spec (Join-Path $repoRoot 'spec\behavior.md') --changes (Join-Path $repoRoot 'docs\changes-from-original.md') --out (Join-Path $repoRoot 'docs\refine-register.md') | Out-Host
        $result.Refine = $LASTEXITCODE
        return $result
    } finally { Pop-Location }
}
