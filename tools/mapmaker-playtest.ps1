<#
.SYNOPSIS
  Mapmaker "Play test" (docs/mapmaker.md "Play test"): from a .smmap file to playing it with bots, in one command. Added content.
.DESCRIPTION
  mapmaker-playtest.ps1 -Map <file.smmap> [-Mode annihilation|seize] [-Bots 3] [-Side marines|chaos] [-Class tactical] [-Port n] [-NoBuild]
  mapmaker-playtest.ps1 -Map <file> -Test [-OutDir <folder>] [-Seconds 60]   (agents and tests: the camera is a free observer on the hidden desktop, stills in -OutDir)
  mapmaker-playtest.ps1 -Stop                                            (stops only the processes the last run recorded)

  1. builds the server and the client (skipped with -NoBuild);
  2. checks the map and refuses to start when it cannot be played in that mode (the reasons are printed in plain words);
  3. starts a dedicated server on 127.0.0.1 only (a free port in 27701..27760) with --custom-map, and -Bots test bots on each side (lean, headless, no window);
  4. starts the game client on the map: a normal window for you (-Side, -Class: where you start), or with -Test an observer on the private test desktop taking stills;
  5. when the client closes, stops the server and the bots it started, by process id. Nothing else is touched.
  The last line is `PLAYTEST ok` or `PLAYTEST failed: <reason>`. Bounded: -Test stops after -Seconds; a normal run ends when you close the client (or press Stop in the editor).
  Never binds every interface; never touches the game install; the live ports are never used.
#>
param(
    [string]$Map = '', [ValidateSet('annihilation', 'seize')][string]$Mode = 'annihilation', [int]$Bots = 3, [string]$Side = 'marines', [string]$Class = 'tactical',
    [int]$Port = 0, [switch]$NoBuild, [switch]$Test, [string]$OutDir = '', [int]$Seconds = 60, [switch]$Stop, [double]$Every = 3
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'quiet-launch.ps1')
$State = Join-Path $env:LOCALAPPDATA 'sm-reimpl\tmp-build\mapmaker-playtest'
$GodotConsole = Join-Path $env:LOCALAPPDATA 'sm-reimpl\tools\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
# The editor reads this file to show progress: "stage|text".
function Say([string]$stage, [string]$text) { try { Set-Content -LiteralPath (Join-Path $State 'status.txt') -Value "$stage|$text" -Encoding utf8 } catch { } Write-Host $text }
$RunFile = Join-Path $State 'run.json'
New-Item -ItemType Directory -Force -Path $State | Out-Null

function Stop-Recorded {
    if (-not (Test-Path -LiteralPath $RunFile)) { return 0 }
    $n = 0
    try { $run = Get-Content -LiteralPath $RunFile -Raw | ConvertFrom-Json } catch { Remove-Item -LiteralPath $RunFile -Force -ErrorAction SilentlyContinue; return 0 }
    foreach ($rec in @($run.pids)) {
        try {
            $p = Get-Process -Id ([int]$rec.pid) -ErrorAction Stop
            # the same process only: same start time as recorded
            if ($p.StartTime.ToFileTimeUtc() -eq [int64]$rec.start) { & taskkill.exe /PID $p.Id /T /F 2>$null | Out-Null; $n++ }
        } catch { }
    }
    Remove-Item -LiteralPath $RunFile -Force -ErrorAction SilentlyContinue
    return $n
}
if ($Stop) { $n = Stop-Recorded; Write-Host "Stopped $n process(es) of the last play test."; exit 0 }
if (-not $Map) { throw '-Map <file.smmap> is required (mapmaker-playtest.ps1 -Map my.smmap)' }
if (-not (Test-Path -LiteralPath $Map)) { Write-Host "PLAYTEST failed: the map file '$Map' does not exist."; exit 1 }
$Map = (Resolve-Path -LiteralPath $Map).Path
if ($Bots -lt 0 -or $Bots -gt 6) { throw '-Bots takes 0 to 6 per side' }
$null = Stop-Recorded   # a play test of this script still running from before: only its own recorded processes

$smtool = Join-Path $RepoRoot 'src\SmTool\bin\Debug\net10.0\SmTool.dll'
$serverDll = Join-Path $RepoRoot 'src\SmServer\bin\Release\net10.0\SmServer.dll'
if (-not $NoBuild) {
    Say 'building' 'Building the server, the client and the map tools...'
    dotnet build (Join-Path $RepoRoot 'src\SmTool\SmTool.csproj') -nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host 'PLAYTEST failed: the map tools did not build (dotnet build src\SmTool).'; exit 1 }
    dotnet build (Join-Path $RepoRoot 'src\SmServer\SmServer.csproj') -c Release -nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host 'PLAYTEST failed: the server did not build (dotnet build src\SmServer -c Release).'; exit 1 }
    dotnet build (Join-Path $RepoRoot 'client\SmClient.csproj') -nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host 'PLAYTEST failed: the client did not build (dotnet build client\SmClient.csproj).'; exit 1 }
}

Say 'checking' 'Checking the map...'
# The check in plain words; refuse a map that cannot be played in the chosen mode.
$checkText = & dotnet $smtool map check $Map --text --no-bots
$checkJson = (& dotnet $smtool map check $Map --no-bots) -join "`n"
$check = $checkJson | ConvertFrom-Json
$ready = if ($Mode -eq 'seize') { $check.seize } else { $check.annihilation }
if (-not $ready) {
    Write-Host ($checkText -join "`n")
    Write-Host "PLAYTEST failed: this map cannot be played in $Mode yet (see the list above)."
    exit 1
}

if ($Port -eq 0) {
    foreach ($try in 27701..27760) {
        try { $l = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $try)); $l.Close(); $Port = $try; break } catch { }
    }
    if ($Port -eq 0) { Write-Host 'PLAYTEST failed: no free UDP port in 27701..27760 (pass -Port).'; exit 1 }
}
if (@(27015, 27016, 27020, 27025) -contains $Port) { Write-Host "PLAYTEST failed: port $Port is a live server's port."; exit 1 }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir = Join-Path $State "run-$stamp"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$records = [System.Collections.Generic.List[object]]::new()
function Record($proc) { $records.Add([pscustomobject]@{ pid = $proc.Id; start = $proc.StartTime.ToFileTimeUtc() }); @{ pids = $records } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $RunFile -Encoding utf8 }
# Clients and bots find the map by its name in the folder of the file (SM_MAPS_DIR), so a map saved anywhere plays.
$env:SM_MAPS_DIR = Split-Path -Parent $Map
$result = 'PLAYTEST failed: unknown'
try {
    Say 'server' "Starting the server on 127.0.0.1:$Port (with the F6 dev tools)..."
    $server = Start-QuietProcess -FilePath (Get-Command dotnet).Source -ArgumentList @($serverDll, '--bind', '127.0.0.1', '--port', "$Port", '--mode', $Mode, '--custom-map', $Map, '--dev-tools', '--dev-bot-exe', $GodotConsole, '--dev-bot-project', (Join-Path $RepoRoot 'client')) -WorkingDirectory $RepoRoot -StdOut (Join-Path $logDir 'server.log')
    Record $server
    $deadline = (Get-Date).AddSeconds(120)
    $listening = $false
    while ((Get-Date) -lt $deadline -and -not $server.HasExited) {
        if ((Get-Content -LiteralPath (Join-Path $logDir 'server.log') -ErrorAction SilentlyContinue) -match 'listening on|smserver on UDP') { $listening = $true; break }
        Start-Sleep -Milliseconds 400
    }
    if (-not $listening) {
        $tail = (Get-Content -LiteralPath (Join-Path $logDir 'server.log') -ErrorAction SilentlyContinue | Select-Object -Last 4) -join ' | '
        throw "the server did not start: $tail"
    }
    Say 'bots' "Starting $($Bots * 2) bots..."
    $total = if ($Test) { $Seconds } else { 4 * 3600 }
    foreach ($s in @('marines', 'chaos')) {
        for ($i = 0; $i -lt $Bots; $i++) {
            $role = @('tactical', 'assault', 'devastator')[$i % 3]
            $cls = if ($s -eq 'chaos') { @{ tactical = 'chaos tactical'; assault = 'raptor'; devastator = 'havoc' }[$role] } else { $role }
            # The "--" must travel as an array element: written literally on the command line, PowerShell 5.1 swallows it and the client never sees its arguments.
            $botArgs = @('--background', '--log', (Join-Path $logDir "bot-$s-$i.log"), '--', '--connect', "127.0.0.1:$Port", '--bot', '--no-sound', '--side', $s, '--class', $cls)
            $txt = & (Join-Path $PSScriptRoot 'godot.ps1') @botArgs *>&1 | Out-String
            if ($txt -match 'started pid (\d+)') { $bp = Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue; if ($bp) { Record $bp } }
            else { throw "a bot did not start: $txt" }
            Start-Sleep -Milliseconds 800
        }
    }
    Start-Sleep -Seconds 3
    if ($Test) {
        if (-not $OutDir) { $OutDir = Join-Path $logDir 'shots' }
        New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
        $tour = (& dotnet $smtool map tour $Map --raw) -join ''
        $places = @($tour -split '\|' | Where-Object { $_ })
        $poses = @($places | ForEach-Object { ($_ -split '@')[1] })
        $names = @($places | ForEach-Object { ($_ -split '@')[0] })
        $first = $poses[0]
        $tourArg = (@($first) + $poses) -join ';'
        $list = for ($k = 0; $k -lt $names.Count; $k++) { '{0:000}: {1}' -f ($k + 2), $names[$k] }
        (@('001: first view (settling)') + $list) | Set-Content (Join-Path $OutDir 'places.txt')
        $frames = [int]($Every * ($poses.Count + 3) + 90)
        $ga = @('--windowed', '--log', (Join-Path $logDir 'client.log'), '--')
        $ca = @('--connect', "127.0.0.1:$Port", '--observe', '--observe-tour', $tourArg, '--no-sound', '--no-hud', '--no-markers', '--screenshot', (Join-Path $OutDir 'view.png'), '--screenshot-every', "$Every", '--quit-after', "$frames")
        & (Join-Path $PSScriptRoot 'godot.ps1') @ga @ca *> $null
        $shots = @(Get-ChildItem -LiteralPath $OutDir -Filter '*.png' -ErrorAction SilentlyContinue).Count
        Write-Host "observer exited $LASTEXITCODE; $shots still(s) in $OutDir"
        $result = if ($shots -gt 0) { "PLAYTEST ok (map $($check.wireName), mode $Mode, port $Port, $shots stills in $OutDir)" } else { "PLAYTEST failed: the observer took no stills (see $logDir\client.log)" }
    } else {
        Say 'playing' "Playing $($check.name) ($Mode) on 127.0.0.1:$Port. F6 opens the dev tools (add or remove bots, invincible, teleport). Close the game window to stop."
        Write-Host "Playing $($check.name) ($Mode) with $Bots bots a side on 127.0.0.1:$Port. Close the game window to stop."
        & (Join-Path $PSScriptRoot 'client.ps1') --connect "127.0.0.1:$Port" --side $Side --class $Class
        $result = "PLAYTEST ok (the game was closed; server and bots stopped)"
    }
}
catch { $result = "PLAYTEST failed: $($_.Exception.Message)"; Say 'failed' $_.Exception.Message }
finally {
    $ErrorActionPreference = 'Continue'
    $null = Stop-Recorded
}
Write-Host $result
exit $(if ($result -like 'PLAYTEST ok*') { 0 } else { 1 })



