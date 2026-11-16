<#
.SYNOPSIS
  mapmaker.cmd --restart: after a fix, builds the Mapmaker plugin and restarts the owner's open editor, or starts one when none is open
  (owner, 2026-11-05: "when you close the map editor on your own to apply a fix, start it again for me automatically").
.DESCRIPTION
  A running editor (its process id in editor.json in the control folder) gets a restart.request file: it saves the open map, notes it and
  the SM1 map, and quits; this script then starts a new editor, which reopens both (mapmaker/addons/sm_mapmaker/EditorControl.cs). Nothing is killed.
  Control folder: %LOCALAPPDATA%\sm-reimpl\mapmaker\editor, or SM_MAPMAKER_CONTROL. -Test starts a missing editor on the hidden test desktop
  through godot.cmd (agents' own checks) instead of on the owner's desktop.
#>
param([switch]$Test, [int]$TimeoutSeconds = 180)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot 'mapmaker'
$Control = if ($env:SM_MAPMAKER_CONTROL) { $env:SM_MAPMAKER_CONTROL } else { Join-Path $env:LOCALAPPDATA 'sm-reimpl\mapmaker\editor' }
$Godot = Join-Path $env:LOCALAPPDATA 'sm-reimpl\tools\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe'

& dotnet build (Join-Path $Project 'Mapmaker.csproj') -nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Host 'Mapmaker build failed; the editor was left as it is.'; exit 1 }

function Get-EditorPid {
    $file = Join-Path $Control 'editor.json'
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    try { $id = [int](Get-Content -LiteralPath $file -Raw | ConvertFrom-Json).pid } catch { return $null }
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($p -and $p.ProcessName -like 'Godot*') { return $id }
    return $null
}

$old = Get-EditorPid
if ($old) {
    New-Item -ItemType Directory -Force -Path $Control | Out-Null
    Set-Content -LiteralPath (Join-Path $Control 'restart.request') -Value (Get-Date).ToUniversalTime().ToString('o') -Encoding utf8
    # The editor saves the map, notes what to reopen and quits; then this script starts the new one.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline -and (Get-Process -Id $old -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
    if (Get-Process -Id $old -ErrorAction SilentlyContinue) {
        Remove-Item -LiteralPath (Join-Path $Control 'restart.request') -ErrorAction SilentlyContinue
        Write-Host "The editor (process $old) did not close within $TimeoutSeconds s (busy, a dialog open, or an older plugin without restart support); it was left running."
        exit 2
    }
    Write-Host "Mapmaker editor (process $old) closed; starting the new one."
}

if ($Test) {
    & (Join-Path $RepoRoot 'godot.cmd') --windowed --background --editor --path $Project
} else {
    Start-Process -FilePath $Godot -ArgumentList @('--editor', '--path', "`"$Project`"")
}
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $now = Get-EditorPid
    if ($now) { Write-Host "Mapmaker editor started (process $now)."; exit 0 }
}
Write-Host "An editor was started but did not register within $TimeoutSeconds s."
exit 2
