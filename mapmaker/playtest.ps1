<#
.SYNOPSIS
  Mapmaker "Play test" in the tester install (docs/mapmaker.md "Play test", docs/releasing.md "The Mapmaker"): from a .smmap file to playing it with bots. Added content.
.DESCRIPTION
  The Mapmaker's PLAY TEST button runs this when it has no repository checkout (tools/mapmaker-playtest.ps1 is the developer's version). It runs from
  <install>\mapmaker-project\ (the launcher copies it there with the rest of the shipped mapmaker\ folder) and uses the prebuilt game of the active version:
  <install>\versions\<tag>\server\SmServer.exe and \game\sm-reimpl.exe (with sm-reimpl.console.exe for the bots). Nothing is built, nothing is downloaded.

  playtest.ps1 -Map <file.smmap> [-Mode annihilation|seize] [-Bots 3] [-Side marines|chaos] [-Class tactical] [-Port n]
  playtest.ps1 -Stop                         stops only the processes the last run recorded (by id and start time)
  playtest.ps1 -Locate [-VersionDir <dir>]   prints where the server and the game were found (or why not), starts nothing
  -Plan                                      prints the command lines it would run (PLAN server|bot|client ...), starts nothing
  -Headless [-Seconds 30]                    a test aid: the player's client is a hidden bot that stays -Seconds, instead of a game window

  Where the build is found, in order: -VersionDir, the SM_MAPMAKER_VERSION_DIR variable (the launcher sets it when it opens the editor), then the install's
  current.json beside mapmaker-project\.
  Starts a dedicated server on 127.0.0.1 only (a free port in 27701..27760) with --custom-map, -Bots bots on each side (hidden, no window), then the game
  client connected to it in a normal window. When the game closes the server and the bots it started are stopped, by process id. Nothing else is touched.
  The editor reads <data>\tmp-build\mapmaker-playtest\status.txt ("stage|text"). The last output line is `PLAYTEST ok` or `PLAYTEST failed: <reason>`.
  Never binds every interface; never writes to the game install; the live servers' ports are never used.
#>
param(
    [string]$Map = '', [ValidateSet('annihilation', 'seize')][string]$Mode = 'annihilation', [int]$Bots = 3, [ValidateSet('marines', 'chaos')][string]$Side = 'marines', [string]$Class = 'tactical',
    [ValidateScript({ $_ -eq 0 -or ($_ -ge 1024 -and $_ -le 65535) })][int]$Port = 0, [switch]$Stop, [switch]$Locate, [switch]$Plan, [string]$VersionDir = '', [switch]$Headless, [int]$Seconds = 30
)
$ErrorActionPreference = 'Stop'
$Data = if ($env:SM_DATA_DIR) { $env:SM_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'sm-reimpl' }
$State = Join-Path $Data 'tmp-build\mapmaker-playtest'
$RunFile = Join-Path $State 'run.json'
function Say([string]$stage, [string]$text) { try { Set-Content -LiteralPath (Join-Path $State 'status.txt') -Value "$stage|$text" -Encoding utf8 } catch { } Write-Host $text }

# The version folder of the build to run: the argument, the variable the launcher sets, or the install's current.json (this script sits in <install>\mapmaker-project).
function Find-VersionDir([string]$explicit) {
    if ($explicit) { return $explicit }
    if ($env:SM_MAPMAKER_VERSION_DIR) { return $env:SM_MAPMAKER_VERSION_DIR }
    $installRoot = Split-Path -Parent $PSScriptRoot
    $cur = Join-Path $installRoot 'current.json'
    if (Test-Path -LiteralPath $cur) {
        try {
            $tag = [string](Get-Content -LiteralPath $cur -Raw | ConvertFrom-Json).Current
            if ($tag -match '^[A-Za-z0-9._-]+$') { return (Join-Path (Join-Path $installRoot 'versions') $tag) }
        } catch { }
    }
    return ''
}
# Returns the files, or throws a plain sentence for the Mapmaker's status line.
function Find-Game([string]$explicit) {
    $v = Find-VersionDir $explicit
    $hint = 'Open the Mapmaker from the launcher (the MAPMAKER button) so it knows the installed game, then press Play test again.'
    if (-not $v) { throw "game files not found: the install folder is not known. $hint" }
    $server = Join-Path $v 'server\SmServer.exe'; $game = Join-Path $v 'game\sm-reimpl.exe'; $console = Join-Path $v 'game\sm-reimpl.console.exe'
    foreach ($f in $server, $game, $console) {
        if (-not (Test-Path -LiteralPath $f)) { throw "game files not found: $(Split-Path -Leaf $f) is missing from '$v'. $hint" }
    }
    return [pscustomobject]@{ Dir = $v; Server = $server; Game = $game; Console = $console }
}

# Windows PowerShell 5.1 joins a Start-Process argument list without quoting: quote what holds a space (the install folder is "SM Reimpl").
function Quote([string[]]$a) { ($a | ForEach-Object { if ($_ -match '[\s"]' -or $_ -eq '') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' ' }
# The command lines, one place (-Plan prints them; the tests check them). The game reads its own arguments only after "--" (Godot's user arguments), as Play.cmd does.
function Get-ServerArgs($g, [int]$port) { @('--bind', '127.0.0.1', '--port', "$port", '--mode', $Mode, '--custom-map', $Map, '--dev-tools', '--dev-bot-exe', $g.Console) }
function Get-BotArgs([int]$port, [string]$side, [string]$cls, [string]$log) { @('--headless', '--log-file', $log, '--', '--connect', "127.0.0.1:$port", '--bot', '--no-sound', '--no-hud', '--side', $side, '--class', $cls) }
function Get-ClientArgs([int]$port) { @('--', '--connect', "127.0.0.1:$port", '--side', $Side, '--class', $Class) }
function Get-BotClass([string]$side, [int]$i) {
    $role = @('tactical', 'assault', 'devastator')[$i % 3]
    if ($side -eq 'chaos') { @{ tactical = 'chaos tactical'; assault = 'raptor'; devastator = 'havoc' }[$role] } else { $role }
}

if ($Locate) {
    try { $g = Find-Game $VersionDir; Write-Host "LOCATE ok|$($g.Dir)|$($g.Server)|$($g.Game)|$($g.Console)"; exit 0 }
    catch { Write-Host "LOCATE failed: $($_.Exception.Message)"; exit 1 }
}

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
if (-not $Map) { Write-Host 'PLAYTEST failed: -Map <file.smmap> is required.'; exit 1 }
if (-not (Test-Path -LiteralPath $Map)) { Say 'failed' "the map file '$Map' does not exist."; Write-Host "PLAYTEST failed: the map file '$Map' does not exist."; exit 1 }
$Map = (Resolve-Path -LiteralPath $Map).Path
if ($Bots -lt 0 -or $Bots -gt 6) { Say 'failed' '-Bots takes 0 to 6 per side.'; Write-Host 'PLAYTEST failed: -Bots takes 0 to 6 per side.'; exit 1 }
if ($Plan) {
    try {
        $g = Find-Game $VersionDir
        $pp = if ($Port -eq 0) { 27701 } else { $Port }
        Write-Host ('PLAN server: ' + $g.Server + ' ' + (Quote (Get-ServerArgs $g $pp)))
        for ($i = 0; $i -lt [Math]::Min($Bots, 1); $i++) { Write-Host ('PLAN bot: ' + $g.Console + ' ' + (Quote (Get-BotArgs $pp 'marines' (Get-BotClass 'marines' $i) 'bot.log'))) }
        Write-Host ('PLAN client: ' + $g.Game + ' ' + (Quote (Get-ClientArgs $pp)))
        exit 0
    } catch { Write-Host "PLAN failed: $($_.Exception.Message)"; exit 1 }
}
$null = Stop-Recorded   # a play test of this script still running from before: only its own recorded processes


$result = 'PLAYTEST failed: unknown'
$records = [System.Collections.Generic.List[object]]::new()
function Record($proc) { $records.Add([pscustomobject]@{ pid = $proc.Id; start = $proc.StartTime.ToFileTimeUtc() }); @{ pids = $records } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $RunFile -Encoding utf8 }
try {
    $g = Find-Game $VersionDir
    if ($Port -eq 0) {
        foreach ($try in 27701..27760) {
            try { $l = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $try)); $l.Close(); $Port = $try; break } catch { }
        }
        if ($Port -eq 0) { throw 'no free UDP port in 27701..27760 (close another play test).' }
    }
    if (@(27015, 27016, 27020, 27025) -contains $Port) { throw "port $Port is a live server's port." }
    $logDir = Join-Path $State ('run-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    # Clients and bots find the map by its name in the folder of the file (SM_MAPS_DIR), so a map saved anywhere plays.
    $env:SM_MAPS_DIR = Split-Path -Parent $Map

    Say 'server' "Starting the server on 127.0.0.1:$Port..."
    $slog = Join-Path $logDir 'server.log'
    $sargs = Get-ServerArgs $g $Port
    $server = Start-Process -FilePath $g.Server -ArgumentList (Quote $sargs) -WorkingDirectory (Split-Path -Parent $g.Server) -WindowStyle Hidden -PassThru -RedirectStandardOutput $slog -RedirectStandardError (Join-Path $logDir 'server.err.log')
    Record $server
    $deadline = (Get-Date).AddSeconds(60)
    $listening = $false
    while ((Get-Date) -lt $deadline -and -not $server.HasExited) {
        if ((Get-Content -LiteralPath $slog -ErrorAction SilentlyContinue) -match 'smserver on UDP') { $listening = $true; break }
        Start-Sleep -Milliseconds 400
    }
    if (-not $listening) {
        $tail = @((Get-Content -LiteralPath $slog -ErrorAction SilentlyContinue) + (Get-Content -LiteralPath (Join-Path $logDir 'server.err.log') -ErrorAction SilentlyContinue)) | Where-Object { $_ } | Select-Object -Last 4
        throw "the server did not start: $($tail -join ' | ')"
    }

    if ($Bots -gt 0) { Say 'bots' "Starting $($Bots * 2) bots..." }
    foreach ($s in 'marines', 'chaos') {
        for ($i = 0; $i -lt $Bots; $i++) {
            $cls = Get-BotClass $s $i
            $bargs = Get-BotArgs $Port $s $cls (Join-Path $logDir "bot-$s-$i.godot.log")
            $bp = Start-Process -FilePath $g.Console -ArgumentList (Quote $bargs) -WorkingDirectory (Split-Path -Parent $g.Console) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logDir "bot-$s-$i.log") -RedirectStandardError (Join-Path $logDir "bot-$s-$i.err.log")
            Record $bp
            Start-Sleep -Milliseconds 800
        }
    }

    $name = [IO.Path]::GetFileNameWithoutExtension($Map)
    if ($Headless) {
        # test aid: the player's client is a hidden bot, so a run needs no window; it stays -Seconds
        $cargs = Get-BotArgs $Port $Side $Class (Join-Path $logDir 'client.godot.log')
        $client = Start-Process -FilePath $g.Console -ArgumentList (Quote $cargs) -WorkingDirectory (Split-Path -Parent $g.Console) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logDir 'client.log') -RedirectStandardError (Join-Path $logDir 'client.err.log')
        Record $client
        Say 'playing' "Headless play test of $name ($Mode) on 127.0.0.1:$Port for $Seconds s."
        $null = $client.WaitForExit($Seconds * 1000)
        Write-Host "logs in $logDir"
        $result = "PLAYTEST ok (headless, port $Port)"
    } else {
        Say 'playing' "Playing $name ($Mode) on 127.0.0.1:$Port. F6 opens the dev tools (add or remove bots, invincible, teleport). Close the game window to stop."
        $cargs = Get-ClientArgs $Port
        $client = Start-Process -FilePath $g.Game -ArgumentList (Quote $cargs) -WorkingDirectory (Split-Path -Parent $g.Game) -PassThru
        Record $client
        $client.WaitForExit()
        $result = 'PLAYTEST ok (the game was closed; server and bots stopped)'
    }
    Say 'done' 'Play test over (the server and the bots are stopped).'
}
catch { $result = "PLAYTEST failed: $($_.Exception.Message)"; Say 'failed' $_.Exception.Message }
finally {
    $ErrorActionPreference = 'Continue'
    $null = Stop-Recorded
}
Write-Host $result
exit $(if ($result -like 'PLAYTEST ok*') { 0 } else { 1 })
