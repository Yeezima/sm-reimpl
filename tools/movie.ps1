<#
.SYNOPSIS
  Records the Godot client with Movie Maker at a chosen size (M34 capture aid), without editing client\project.godot.
  Movie Maker takes its frame size from the project's viewport size before any script runs (--resolution is ignored, and a
  window resized later is only scaled into the first size), so this script runs Godot on a throw-away copy of the client
  project whose project.godot has the wanted viewport size. The copy lives in a per-run folder under
  %LOCALAPPDATA%\sm-reimpl\cache\movie-project (removed afterwards) and shares the compiled client through a junction to the
  repository's git-ignored client\.godot cache, which Godot may update. Nothing else is written into the repository, and
  nothing into the game install.
.EXAMPLE
  movie.cmd --movie-size 1080x1920 --write-movie "%LOCALAPPDATA%\sm-reimpl\cache\clip.avi" --fixed-fps 30 --connect 127.0.0.1:27961 --no-markers --no-hud --quit-after 5
  Everything but --movie-size (default 1920x1080), --write-movie (required), --fixed-fps (default 30) and --position (Godot's window position, e.g. -4000,-4000; test runs also pass --test-window to the client) goes to the client.
#>
$ErrorActionPreference = 'Stop'
$Version = '4.7.2-stable'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Tools = Join-Path $env:LOCALAPPDATA 'sm-reimpl\tools'
$Name = "Godot_v$($Version)_mono_win64"
$Godot = Join-Path $Tools "godot\$Name\$($Name)_console.exe"
if (-not (Test-Path -LiteralPath $Godot)) { throw 'Godot is not installed yet: run client.cmd once.' }

# True when $Path is $Folder or below it (a sibling such as <folder>-feed is not).
function Test-Within([string]$Path, [string]$Folder) {
    if (-not $Folder) { return $false }
    $f = [IO.Path]::GetFullPath($Folder).TrimEnd('\') + '\'
    $p = [IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    return $p.StartsWith($f, [StringComparison]::OrdinalIgnoreCase)
}

$size = '1920x1080'; $out = $null; $fps = 30; $position = $null; $rest = @()
for ($i = 0; $i -lt $args.Count; $i++) {
    switch ($args[$i]) {
        '--movie-size' { $size = $args[++$i] }
        '--write-movie' { $out = $args[++$i] }
        '--fixed-fps' { $fps = [int]$args[++$i] }
        '--position' { $position = $args[++$i] }   # Godot's window position (before the --): -4000,-4000 keeps a test window off the owner's screen
        default { $rest += $args[$i] }
    }
}
if ($size -notmatch '^(\d{2,4})x(\d{2,4})$' -or [int]$Matches[1] -lt 64 -or [int]$Matches[2] -lt 64 -or [int]$Matches[1] -gt 8192 -or [int]$Matches[2] -gt 8192) { throw "--movie-size '$size': expected WxH between 64 and 8192, e.g. 1080x1920" }
$w = [int]$Matches[1]; $h = [int]$Matches[2]
if (-not $out) { throw '--write-movie <file.avi> is required' }
$out = [IO.Path]::GetFullPath($out)

# The game folder, as the client finds it (SM_GAME_DIR, then config\local.json), for the output guards and the copy.
$gameDir = $env:SM_GAME_DIR
if (-not $gameDir) {
    $local = Join-Path $RepoRoot 'config\local.json'
    if (Test-Path -LiteralPath $local) {
        try { $gameDir = (Get-Content -LiteralPath $local -Raw | ConvertFrom-Json).gameDir }
        catch { throw 'config\local.json could not be read as JSON (its contents are not shown); fix it or set SM_GAME_DIR' }
    }
}
if ($gameDir) { $env:SM_GAME_DIR = $gameDir }

# Output guards: neither the movie nor a --screenshot may land in the repository or the game install.
$targets = [System.Collections.Generic.List[object]]::new()
$targets.Add([pscustomobject]@{ Flag = '--write-movie'; Path = $out })
for ($j = 0; $j -lt $rest.Count - 1; $j++) {
    if ($rest[$j] -eq '--screenshot' -and [IO.Path]::IsPathRooted($rest[$j + 1])) { $targets.Add([pscustomobject]@{ Flag = '--screenshot'; Path = $rest[$j + 1] }) }
}
foreach ($t in $targets) {
    if (Test-Within $t.Path $RepoRoot) { throw "$($t.Flag) $($t.Path) is inside the repository; write under %LOCALAPPDATA%\sm-reimpl\cache" }
    if (Test-Within $t.Path $gameDir) { throw "$($t.Flag) $($t.Path) is inside the game install, which is read-only" }
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $out) | Out-Null

$client = Join-Path $RepoRoot 'client'
dotnet build (Join-Path $client 'SmClient.csproj') -nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not (Test-Path -LiteralPath (Join-Path $client '.godot'))) { & $Godot --headless --path $client --import | Out-Null }

# The copy: sources and scenes copied, the build/import cache shared. A folder per run (so two recordings never meet), whose
# parent holds nothing else: the client's "repository root" (the folder above res://) is that folder, not the real repository.
$runDir = Join-Path $env:LOCALAPPDATA ('sm-reimpl\cache\movie-project\' + [guid]::NewGuid().ToString('N'))
$shadow = Join-Path $runDir 'client'
$junction = Join-Path $shadow '.godot'
$exit = 1
try {
    New-Item -ItemType Directory -Force -Path $shadow | Out-Null
    Get-ChildItem -LiteralPath $client -File | Where-Object { $_.Name -ne 'project.godot' } | Copy-Item -Destination $shadow
    New-Item -ItemType Junction -Path $junction -Target (Join-Path $client '.godot') | Out-Null
    $project = (Get-Content -LiteralPath (Join-Path $client 'project.godot') -Raw) -replace 'viewport_width=\d+', "viewport_width=$w" -replace 'viewport_height=\d+', "viewport_height=$h"
    Set-Content -LiteralPath (Join-Path $shadow 'project.godot') -Value $project -Encoding ascii -NoNewline

    Write-Host "Recording ${w}x${h} @ $fps FPS -> $out" -ForegroundColor Yellow
    $positionArgs = if ($position) { @('--position', $position) } else { @() }
    if ($rest -contains '--test-window') {
        # tools/no-focus-steal: a test recording runs on the private test desktop (never on the owner's screen, never takes the focus).
        . (Join-Path $PSScriptRoot 'quiet-launch.ps1')
        $mlog = Join-Path $runDir 'movie.log'
        $mp = Start-QuietProcess -FilePath $Godot -ArgumentList (@('--path', $shadow, '--write-movie', $out, '--fixed-fps', "$fps") + $positionArgs + @('--') + $rest) `
            -WorkingDirectory $RepoRoot -StdOut $mlog -Desktop 'sm-reimpl-test'
        $exit = Wait-QuietProcess $mp -Log $mlog
    } else {
        & $Godot --path $shadow --write-movie $out --fixed-fps $fps @positionArgs '--' @rest
        $exit = $LASTEXITCODE
    }
}
finally {
    # Remove the link itself first (non-recursive), and prove it is gone before any recursive delete: Remove-Item -Recurse on
    # Windows PowerShell 5.1 could otherwise follow the junction and delete the repository's own client\.godot.
    if (Test-Path -LiteralPath $junction) {
        [IO.Directory]::Delete($junction)
        if (Test-Path -LiteralPath $junction) { throw "Could not remove the junction $junction; the run folder $runDir was left in place (do not delete it recursively)." }
    }
    if (Test-Path -LiteralPath $runDir) { Remove-Item -LiteralPath $runDir -Recurse -Force -ErrorAction Stop }
}
exit $exit
