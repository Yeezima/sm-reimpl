<#
.SYNOPSIS
  Mapmaker "Play test" in the tester install (docs/mapmaker.md "Play test", docs/releasing.md "The Mapmaker"): from a .smmap file to playing it with bots. Added content.
.DESCRIPTION
  The Mapmaker's Play test button runs this when it has no repository checkout (tools/mapmaker-playtest.ps1 is the developer's version). It runs from
  <install>\mapmaker-project\ (the launcher copies it there with the rest of the shipped mapmaker\ folder) and uses the prebuilt game of the active version:
  <install>\versions\<tag>\server\SmServer.exe and \game\sm-reimpl.exe (with sm-reimpl.console.exe for the bots). Nothing is built, nothing is downloaded.

  playtest.ps1 -Map <file.smmap> [-Mode annihilation|seize] [-Bots 3] [-Side marines|chaos] [-Class tactical] [-Port n]
  playtest.ps1 -Stop                         stops only the processes the last run recorded (by id and start time), and leaves a stop marker
                                             (stop.flag) that a run still starting checks between its steps, so it ends as "stopped"
  -RunTag <letters, digits . _ ->            written into run.json (the launcher tags its runs, to recognise its own leftovers); with -Stop, only
                                             a run recorded under that tag is stopped
  A run replaced by another play test (run.json gone or naming another run) ends at its next step check as "replaced" (exit code 3)
  and writes neither status.txt nor run.json again.
  -SafeMode                                  the game window on the Compatibility renderer with low settings, as the launcher's "Old GPU / safe mode"
                                             (the bots stay headless); the launcher's PLAY TEST MAP passes it when that box is ticked
  playtest.ps1 -Locate [-VersionDir <dir>]   prints where the server and the game were found (or why not), starts nothing
  -Plan                                      prints the command lines it would run (PLAN server|bot|client ...), starts nothing
  -Headless [-Seconds 30]                    a test aid: the player's client is a hidden bot that stays -Seconds, instead of a game window

  The launcher's PLAY TEST MAP button (owner, 2026-11-28) runs this script from <install>\versions\<tag>\mapmaker\ with no editor open, passing -VersionDir.

  Where the build is found, in order: -VersionDir, the SM_MAPMAKER_VERSION_DIR variable (the launcher sets it when it opens the editor), the build whose
  mapmaker\ folder holds this script, then the install's current.json beside mapmaker-project\.
  SM_PLAYTEST_STATE_DIR (test aid; a full path, read by this script and the launcher only, not by the Mapmaker's plugin) moves status.txt, run.json and
  stop.flag out of <data>\tmp-build\mapmaker-playtest.
  A game window that closes with an error within 8 s of starting (the launcher's SafeModePolicy.EarlyExitWindow) is reported as a failure with the safe-mode hint.
  Starts a dedicated server on 127.0.0.1 only (a free port in 27701..27760) with --custom-map, -Bots bots on each side (hidden, no window), then the game
  client connected to it in a normal window. When the game closes the server and the bots it started are stopped, by process id. Nothing else is touched.
  The editor reads <data>\tmp-build\mapmaker-playtest\status.txt ("stage|text"). The last output line is `PLAYTEST ok` or `PLAYTEST failed: <reason>`.
  Never binds every interface; never writes to the game install; the live servers' ports are never used.
#>
param(
    [string]$Map = '', [ValidateSet('annihilation', 'seize')][string]$Mode = 'annihilation', [int]$Bots = 3, [ValidateSet('marines', 'chaos')][string]$Side = 'marines', [string]$Class = 'tactical',
    [ValidateScript({ $_ -eq 0 -or ($_ -ge 1024 -and $_ -le 65535) })][int]$Port = 0, [switch]$Stop, [switch]$Locate, [switch]$Plan, [string]$VersionDir = '', [switch]$Headless, [int]$Seconds = 30,
    [string]$ExpectHash = '', [switch]$SafeMode, [ValidatePattern('^[A-Za-z0-9._-]{0,64}$')][string]$RunTag = ''
)
$ErrorActionPreference = 'Stop'
$Data = if ($env:SM_DATA_DIR) { $env:SM_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'sm-reimpl' }
# SM_PLAYTEST_STATE_DIR (test aid): another folder (a full path) for status.txt, run.json and stop.flag, so a test run never meets (or stops) a real play test's record
$StateOverride = "$env:SM_PLAYTEST_STATE_DIR".Trim()
$State = if ($StateOverride -and [IO.Path]::IsPathRooted($StateOverride)) { $StateOverride } else { Join-Path $Data 'tmp-build\mapmaker-playtest' }
$RunFile = Join-Path $State 'run.json'
$StopFlag = Join-Path $State 'stop.flag'
# The launcher's SafeModePolicy: its engine options (before "--") and the game's option (after it), and how soon a failing game counts as "closed right away".
$SafeEngineArgs = @('--rendering-method', 'gl_compatibility', '--rendering-driver', 'opengl3')
$SafeGameArg = '--safe-mode'
$EarlyExitSeconds = 8
function Say([string]$stage, [string]$text) { try { Set-Content -LiteralPath (Join-Path $State 'status.txt') -Value "$stage|$text" -Encoding utf8 } catch { } Write-Host $text }

# The version folder of the build to run: the argument, the variable the launcher sets, the build this script came with (it sits in that build's own
# <version>\mapmaker\ folder when the launcher's PLAY TEST MAP button runs it), or the install's current.json (this script sits in <install>\mapmaker-project).
function Find-VersionDir([string]$explicit) {
    if ($explicit) { return $explicit }
    if ($env:SM_MAPMAKER_VERSION_DIR) { return $env:SM_MAPMAKER_VERSION_DIR }
    $installRoot = Split-Path -Parent $PSScriptRoot
    if ((Split-Path -Leaf $PSScriptRoot) -eq 'mapmaker' -and (Test-Path -LiteralPath (Join-Path $installRoot 'server\SmServer.exe'))) { return $installRoot }
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
    $hint = 'Start the play test from the launcher (PLAY TEST MAP, or open the Mapmaker with the MAPMAKER button) so it knows the installed game; if a file is missing, use Check files in the launcher.'
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
# Clients and bots get the exact file too (--custom-map): looking a map up by its name could pick another file with the same name and a different hash.
function Get-BotArgs([int]$port, [string]$side, [string]$cls, [string]$log) { @('--headless', '--log-file', $log, '--', '--connect', "127.0.0.1:$port", '--custom-map', $Map, '--bot', '--no-sound', '--no-hud', '--side', $side, '--class', $cls) }
# The player's game window only: with -SafeMode the Compatibility renderer before "--" and --safe-mode after it, as the launcher's PLAY does (bots stay --headless).
function Get-ClientArgs([int]$port) {
    $a = @('--', '--connect', "127.0.0.1:$port", '--custom-map', $Map, '--side', $Side, '--class', $Class)
    if ($SafeMode) { $a = $SafeEngineArgs + $a + @($SafeGameArg) }
    $a
}
function Get-BotClass([string]$side, [int]$i) {
    $role = @('tactical', 'assault', 'devastator')[$i % 3]
    if ($side -eq 'chaos') { @{ tactical = 'chaos tactical'; assault = 'raptor'; devastator = 'havoc' }[$role] } else { $role }
}

# How a game window that closed ended: '' (fine), or the failure for one that closed with an error within $EarlyExitSeconds (the launcher's SafeModePolicy).
function Get-EarlyExitProblem($exitCode, [double]$ranFor, [bool]$safe) {
    if ($null -eq $exitCode -or $exitCode -eq 0 -or $ranFor -gt $EarlyExitSeconds) { return '' }
    # as the launcher's PLAY: a game that dies at once usually does not run on this graphics card with its normal settings
    $hint = if ($safe) { 'This happened even in Old GPU / safe mode: update your graphics driver, then report it with the logs.' } else { 'Tick "Old GPU / safe mode" in the launcher and press PLAY TEST MAP again.' }
    return "the game closed right after it started (exit code $exitCode after $([int]$ranFor) s). $hint"
}
# test aid: SM_PLAYTEST_EARLY_EXIT_CHECK=<exit code>,<seconds> prints that classification ("EARLY <problem or nothing>") and exits; it starts and touches nothing
if ($env:SM_PLAYTEST_EARLY_EXIT_CHECK) {
    $parts = $env:SM_PLAYTEST_EARLY_EXIT_CHECK -split ','
    Write-Host ('EARLY ' + (Get-EarlyExitProblem ([int]$parts[0]) ([double]::Parse($parts[1], [Globalization.CultureInfo]::InvariantCulture)) ([bool]$SafeMode)))
    exit 0
}

if ($Locate) {
    try { $g = Find-Game $VersionDir; Write-Host "LOCATE ok|$($g.Dir)|$($g.Server)|$($g.Game)|$($g.Console)"; exit 0 }
    catch { Write-Host "LOCATE failed: $($_.Exception.Message)"; exit 1 }
}

New-Item -ItemType Directory -Force -Path $State | Out-Null
# The recorded run's processes, each with its whole tree (/T: the dev server is a supervisor with the server as its child; the tests pin it).
# With a tag, only a run still recorded under it, read here once more right before anything is stopped: -1 when run.json is gone, unreadable or names another run
# (a Mapmaker run that started after -Stop first checked the tag), and nothing is removed or stopped.
function Stop-Recorded([string]$tag = '') {
    if (-not (Test-Path -LiteralPath $RunFile)) { if ($tag) { return -1 } else { return 0 } }
    $n = 0
    try { $run = Get-Content -LiteralPath $RunFile -Raw | ConvertFrom-Json }
    catch {
        if ($tag) { return -1 }      # being written by another run right now: not ours to remove
        Remove-Item -LiteralPath $RunFile -Force -ErrorAction SilentlyContinue; return 0
    }
    if ($tag -and [string]$run.tag -ne $tag) { return -1 }
    # the record goes first, then its processes: a run that is replaced sees its record gone as soon as its game or server dies, and ends as "replaced"
    Remove-Item -LiteralPath $RunFile -Force -ErrorAction SilentlyContinue
    foreach ($rec in @($run.pids)) {
        try {
            $p = Get-Process -Id ([int]$rec.pid) -ErrorAction Stop
            # the same process only: same start time as recorded
            if ($p.StartTime.ToFileTimeUtc() -eq [int64]$rec.start) { & taskkill.exe /PID $p.Id /T /F 2>$null | Out-Null; $n++ }
        } catch { }
    }
    return $n
}
# The marker first: a run still starting (before it recorded anything, or between its steps) sees it and ends as "stopped" instead of starting the next step.
# With -RunTag, only the run recorded under that tag is stopped (the launcher cleaning up its own run's leftovers); another run (the Mapmaker's) is left alone.
# A tagged Stop checks the tag, leaves the marker, and Stop-Recorded checks the tag again right before stopping: a Mapmaker run that started in between (it clears
# the marker and records itself) is then left alone, and the marker this Stop left is taken back (only if it is still this Stop's own), so that run does not end as "stopped".
if ($Stop) {
    if ($RunTag) {
        $recorded = ''
        try { if (Test-Path -LiteralPath $RunFile) { $recorded = [string](Get-Content -LiteralPath $RunFile -Raw | ConvertFrom-Json).tag } } catch { }
        if ($recorded -ne $RunTag) { Write-Host "Not stopped: the recorded play test is another run (tag '$recorded', not '$RunTag')."; exit 0 }
    }
    $mark = (Get-Date).ToUniversalTime().ToString('o') + ' ' + [guid]::NewGuid().ToString('N')
    try { Set-Content -LiteralPath $StopFlag -Value $mark -Encoding utf8 } catch { }
    $n = Stop-Recorded $RunTag
    if ($n -lt 0) {
        try { if ((Get-Content -LiteralPath $StopFlag -Raw -ErrorAction Stop).Trim() -eq $mark) { Remove-Item -LiteralPath $StopFlag -Force -ErrorAction SilentlyContinue } } catch { }
        Write-Host "Not stopped: the recorded play test is another run (it started after this Stop checked the tag '$RunTag')."; exit 0
    }
    Write-Host "Stopped $n process(es) of the last play test."; exit 0
}
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
Remove-Item -LiteralPath $StopFlag -Force -ErrorAction SilentlyContinue      # a marker left for an earlier run


$result = 'PLAYTEST failed: unknown'
$records = [System.Collections.Generic.List[object]]::new()
$Replaced = $false
# This run was replaced by another play test: it has recorded processes, but run.json is gone or names another run (a Play test started from the other button
# stops this run's recorded processes and records its own). Never with -Stop pending: that removes run.json too, and is checked first.
function Test-Replaced {
    if ($records.Count -eq 0) { return $false }
    # a read that cannot be parsed (run.json being written right now) is tried once more a moment later before deciding; still unreadable: not decided on this read
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        if (-not (Test-Path -LiteralPath $RunFile)) { return $true }
        try {
            $first = @((Get-Content -LiteralPath $RunFile -Raw | ConvertFrom-Json).pids)[0]
            return -not ($null -ne $first -and [int]$first.pid -eq $records[0].pid -and [int64]$first.start -eq $records[0].start)
        } catch { if ($attempt -eq 0) { Start-Sleep -Milliseconds 200 } }
    }
    return $false
}
# run.json: this run's processes (pid and start time) and its -RunTag (the launcher's: processes still recorded under it after the script ended are its leftovers).
# A replaced run never writes it again (it would overwrite the new run's record); the process just started is in $records, so Stop-Own ends it.
function Record($proc) {
    $stopped = Test-StopAsked                                  # -Stop removes run.json too: a Stop is never read as "replaced"
    $replaced = (-not $stopped) -and (Test-Replaced)
    $records.Add([pscustomobject]@{ pid = $proc.Id; start = $proc.StartTime.ToFileTimeUtc() })
    if ($stopped) { throw [OperationCanceledException]::new('stopped') }
    if ($replaced) { $script:Replaced = $true; throw [OperationCanceledException]::new('replaced') }
    @{ pids = $records; tag = $RunTag } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $RunFile -Encoding utf8
}
# Stops only what THIS run started (its own list, by id and start time), and removes run.json only while it still names this run: a Play test started meanwhile
# from the other button replaced this run's record with its own, and its processes are not this run's to stop.
function Stop-Own {
    foreach ($rec in $records) {
        try {
            $p = Get-Process -Id ([int]$rec.pid) -ErrorAction Stop
            if ($p.StartTime.ToFileTimeUtc() -eq [int64]$rec.start) { & taskkill.exe /PID $p.Id /T /F 2>$null | Out-Null }
        } catch { }
    }
    if ($records.Count -gt 0 -and (Test-Path -LiteralPath $RunFile)) {
        try {
            $first = @((Get-Content -LiteralPath $RunFile -Raw | ConvertFrom-Json).pids)[0]
            if ([int]$first.pid -eq $records[0].pid -and [int64]$first.start -eq $records[0].start) { Remove-Item -LiteralPath $RunFile -Force -ErrorAction SilentlyContinue }
        } catch { }
    }
}
function Test-StopAsked { Test-Path -LiteralPath $StopFlag }
# Between steps: Stop was asked (stop.flag), or another play test replaced this one: end now instead of starting the next step.
function Assert-NotStopped {
    if (Test-StopAsked) { throw [OperationCanceledException]::new('stopped') }
    if (Test-Replaced) { $script:Replaced = $true; throw [OperationCanceledException]::new('replaced') }
}

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
        Assert-NotStopped
        if ((Get-Content -LiteralPath $slog -ErrorAction SilentlyContinue) -match 'smserver on UDP') { $listening = $true; break }
        Start-Sleep -Milliseconds 400
    }
    Assert-NotStopped
    if (-not $listening) {
        $tail = @((Get-Content -LiteralPath $slog -ErrorAction SilentlyContinue) + (Get-Content -LiteralPath (Join-Path $logDir 'server.err.log') -ErrorAction SilentlyContinue)) | Where-Object { $_ } | Select-Object -Last 4
        throw "the server did not start: $($tail -join ' | ')"
    }
    # The server must be playing exactly this file: its log says which custom map it loaded and that map's hash (the editor passes the file's own as -ExpectHash).
    # No "custom map" line, or another hash, means the default arena or another file: stop here, loudly, instead of opening a game on the wrong map.
    $served = (Get-Content -LiteralPath $slog -ErrorAction SilentlyContinue) -join "`n"
    $cm = [regex]::Match($served, 'custom map (cm_\w+) [^\n]*?map hash ([0-9A-Fa-f]{16})')
    if (-not $cm.Success) { throw "the server is not playing a custom map (it would have loaded the default arena). Its log has no 'custom map' line; see $slog" }
    if ($ExpectHash -and $cm.Groups[2].Value.ToUpperInvariant() -ne $ExpectHash.ToUpperInvariant()) {
        throw "the server loaded another map than '$([IO.Path]::GetFileName($Map))' (map hash $($cm.Groups[2].Value), this file's is $ExpectHash). Not starting the game; see $slog"
    }
    Write-Host "server map: $($cm.Groups[1].Value), hash $($cm.Groups[2].Value) (this file)"

    if ($Bots -gt 0) { Say 'bots' "Starting $($Bots * 2) bots..." }
    foreach ($s in 'marines', 'chaos') {
        for ($i = 0; $i -lt $Bots; $i++) {
            Assert-NotStopped
            $cls = Get-BotClass $s $i
            $bargs = Get-BotArgs $Port $s $cls (Join-Path $logDir "bot-$s-$i.godot.log")
            $bp = Start-Process -FilePath $g.Console -ArgumentList (Quote $bargs) -WorkingDirectory (Split-Path -Parent $g.Console) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logDir "bot-$s-$i.log") -RedirectStandardError (Join-Path $logDir "bot-$s-$i.err.log")
            Record $bp
            Start-Sleep -Milliseconds 800
        }
    }

    Assert-NotStopped
    $name = [IO.Path]::GetFileNameWithoutExtension($Map)
    if ($Headless) {
        # test aid: the player's client is a hidden bot, so a run needs no window; it stays -Seconds
        $cargs = Get-BotArgs $Port $Side $Class (Join-Path $logDir 'client.godot.log')
        $client = Start-Process -FilePath $g.Console -ArgumentList (Quote $cargs) -WorkingDirectory (Split-Path -Parent $g.Console) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logDir 'client.log') -RedirectStandardError (Join-Path $logDir 'client.err.log')
        Record $client
        Say 'playing' "Headless play test of $name ($Mode) on 127.0.0.1:$Port for $Seconds s."
        $until = (Get-Date).AddSeconds($Seconds)
        while ((Get-Date) -lt $until -and -not $client.WaitForExit(500)) { Assert-NotStopped }
        Assert-NotStopped
        Write-Host "logs in $logDir"
        $result = "PLAYTEST ok (headless, port $Port)"
    } else {
        Say 'playing' "Playing $name ($Mode) on 127.0.0.1:$Port. F6 opens the dev tools (add or remove bots, invincible, teleport). Close the game window to stop."
        $cargs = Get-ClientArgs $Port
        $client = Start-Process -FilePath $g.Game -ArgumentList (Quote $cargs) -WorkingDirectory (Split-Path -Parent $g.Game) -PassThru
        $null = $client.Handle        # held from the start, so the exit code can be read after it ends
        $clientStarted = Get-Date
        Record $client
        # polled, so a Stop or a play test that replaced this one ends the wait at once (and a killed game is never read as "closed right away")
        while (-not $client.WaitForExit(500)) { Assert-NotStopped }
        $ranFor = ((Get-Date) - $clientStarted).TotalSeconds
        Assert-NotStopped
        $early = Get-EarlyExitProblem $client.ExitCode $ranFor ([bool]$SafeMode)
        if ($early) { throw $early }
        $result = 'PLAYTEST ok (the game was closed; server and bots stopped)'
    }
    Say 'done' 'Play test over (the server and the bots are stopped).'
}
catch {
    # replaced: status.txt, run.json and stop.flag now belong to the new play test, so this run writes none of them
    if ($Replaced) { $result = 'PLAYTEST replaced by another play test' }
    elseif ($_.Exception -is [OperationCanceledException] -or (Test-StopAsked)) { $result = 'PLAYTEST stopped'; Say 'stopped' 'Play test stopped (its server, bots and game are closed).' }
    else { $result = "PLAYTEST failed: $($_.Exception.Message)"; Say 'failed' $_.Exception.Message }
}
finally {
    $ErrorActionPreference = 'Continue'
    Stop-Own
}
if ($result -like 'PLAYTEST ok*' -and (Test-StopAsked)) { $result = 'PLAYTEST stopped'; Say 'stopped' 'Play test stopped (its server, bots and game are closed).' }
if (-not $Replaced) { Remove-Item -LiteralPath $StopFlag -Force -ErrorAction SilentlyContinue }
Write-Host $result
# exit codes: 0 ok or stopped, 1 failed, 3 replaced by another play test (the launcher says so instead of reading the new run's status as this one's)
exit $(if ($result -like 'PLAYTEST ok*' -or $result -eq 'PLAYTEST stopped') { 0 } elseif ($Replaced) { 3 } else { 1 })
