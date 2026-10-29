<#
.SYNOPSIS
  Starts headless test bots (Godot clients with --bot --no-sound) on a server and stops them again (M44a). Test aid; read-only
  against the game install. Never starts a server: start one yourself with server.cmd (see below).
.DESCRIPTION
  bots.cmd --connect <host:port> [--marines N] [--chaos M] [--classes tactical,assault,devastator] [--accuracy 0..1] [--range <m>]
           [--stagger <s>] [--for <seconds>] [--log-dir <dir>] [--trace-dir <dir>] [--no-build] [--allow-remote] [--extra "<more client args>"]
  bots.cmd --stop            stops only the bots this script started, each with the Godot process it started (the Windows console build starts the
                             real Godot as a child, so the whole process tree is stopped); their PIDs and start times are kept in
                             %LOCALAPPDATA%\sm-reimpl\tmp-build\bots\bots.pids and a PID is stopped only while it is still the same process
  bots.cmd --status          lists them

  N and M are 0 to 6 per side. Each bot asks for its side (--side marines|chaos) and a class from --classes (default: a rotation of
  all three roles; the names are tactical, assault, devastator, or the Chaos names raptor and havoc: assault and raptor are the jump
  pack role, devastator and havoc the heavy role); the Chaos side gets the Chaos class of the role. --accuracy is the fraction
  of shots meant to hit at medium range (default 0.03 with a trigger discipline, ours: bots miss most shots; 1 = perfect aim), --range the farthest distance in metres at
  which a bot opens fire on an enemy it sees (default: its weapon's range).

  The server must keep uneven or full sides as asked, so start it with --honor-side-requests (otherwise its balance moves a bot
  that asks for the larger side):
      server.cmd --bind 127.0.0.1 --port 27141 --honor-side-requests
      bots.cmd --connect 127.0.0.1:27141 --marines 6 --chaos 6
  Use --bind 127.0.0.1 for test servers (no firewall prompt). Each bot's output goes to <log-dir>\bot-<n>-<side>-<class>.log.

  Safety: the host must be loopback (127.x, localhost, ::1) unless --allow-remote; the ports 27015, 27016, 27020 and 27025 (the usual live servers) are refused;
  --log-dir and --trace-dir may not be inside any checkout of this repository (this one, the main checkout, other worktrees) or the game install.
#>
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Tools = Join-Path $env:LOCALAPPDATA 'sm-reimpl\tools'
$Name = 'Godot_v4.7.2-stable_mono_win64'
$Godot = Join-Path $Tools "godot\$Name\$($Name)_console.exe"
$State = Join-Path $env:LOCALAPPDATA 'sm-reimpl\tmp-build\bots'
$PidFile = Join-Path $State 'bots.pids'
$MaxPerSide = 6

function Show-Usage { (Get-Help $PSCommandPath).Description.Text | Write-Host }

# --- argument parsing ($args as typed: --name value) ---
$opt = @{ allowremote = $false; tracedir = $null; connect = $null; marines = 0; chaos = 0; classes = $null; accuracy = $null; range = $null; stagger = 1.5; for = 0; logdir = $null; nobuild = $false; extra = ''; stop = $false; status = $false; help = $false }
$all = @($args)
$inv = [Globalization.CultureInfo]::InvariantCulture
for ($i = 0; $i -lt $all.Count; $i++) {
    $a = [string]$all[$i]
    $next = $null
    if ($a -notmatch '^(--stop|-stop|--status|-status|--no-build|-nobuild|--allow-remote|-allowremote|--help|-h|-help|/\?)$') {
        if ($i + 1 -ge $all.Count) { throw "$a needs a value" }
        $i++; $next = [string]$all[$i]
    }
    switch -Regex ($a) {
        '^(--connect|-connect)$'   { $opt.connect = $next }
        '^(--marines|-marines)$'   { $opt.marines = [int]$next }
        '^(--chaos|-chaos)$'       { $opt.chaos = [int]$next }
        '^(--classes|-classes)$'   { $opt.classes = @($next -split '[,; ]+' | Where-Object { $_ }) }
        '^(--accuracy|-accuracy)$' { $opt.accuracy = [double]::Parse($next, $inv) }
        '^(--range|-range)$'       { $opt.range = [double]::Parse($next, $inv) }
        '^(--stagger|-stagger)$'   { $opt.stagger = [double]::Parse($next, $inv) }
        '^(--for|-for)$'           { $opt.for = [int]$next }
        '^(--log-dir|-logdir)$'    { $opt.logdir = $next }
        '^(--extra|-extra)$'       { $opt.extra = $next }
        '^(--trace-dir|-tracedir)$' { $opt.tracedir = $next }
        '^(--no-build|-nobuild)$'  { $opt.nobuild = $true }
        '^(--allow-remote|-allowremote)$' { $opt.allowremote = $true }
        '^(--stop|-stop)$'         { $opt.stop = $true }
        '^(--status|-status)$'     { $opt.status = $true }
        '^(--help|-h|-help|/\?)$'  { $opt.help = $true }
        default { throw "unknown argument '$a' (bots.cmd --help)" }
    }
}
if ($opt.help) { Show-Usage; exit 0 }

function Read-Pids {
    if (-not (Test-Path -LiteralPath $PidFile)) { return @() }
    @(Get-Content -LiteralPath $PidFile | Where-Object { $_ -match '^\d+\s' } | ForEach-Object {
        $f = $_ -split '\s+', 3
        [pscustomobject]@{ Pid = [int]$f[0]; Start = [int64]$f[1]; Label = $f[2] }
    })
}

# A recorded bot is "alive" only while the same process (same PID and start time) still runs; a process that has exited (or cannot be read) is not.
function Test-Same($rec) {
    try {
        $p = Get-Process -Id $rec.Pid -ErrorAction Stop
        return (-not $p.HasExited -and $p.ProcessName -like 'Godot*' -and $p.StartTime.ToFileTimeUtc() -eq $rec.Start)
    } catch { return $false }
}

# The console build of Godot (Godot_..._console.exe) is a wrapper that starts the real Godot as a child: stop the wrapper's whole tree.
function Stop-Tree($rec) {
    if (-not (Test-Same $rec)) { return $false }
    & taskkill.exe /PID $rec.Pid /T /F *> $null
    return $true
}

if ($opt.status) {
    $recs = @(Read-Pids)
    $alive = @($recs | Where-Object { Test-Same $_ })
    Write-Host "$($alive.Count) of $($recs.Count) recorded bots running."
    $alive | ForEach-Object { Write-Host ("  pid {0}  {1}" -f $_.Pid, $_.Label) }
    exit 0
}

if ($opt.stop) {
    $recs = @(Read-Pids)
    $n = 0
    foreach ($r in $recs) {
        if (Stop-Tree $r) { $n++ }
    }
    if (Test-Path -LiteralPath $PidFile) { Remove-Item -LiteralPath $PidFile -Force }
    Write-Host "Stopped $n bot(s) started by this script ($($recs.Count) recorded)."
    exit 0
}

# --- start ---
if (-not $opt.connect) { Show-Usage; throw '--connect <host:port> is required' }
if ($opt.marines -lt 0 -or $opt.marines -gt $MaxPerSide -or $opt.chaos -lt 0 -or $opt.chaos -gt $MaxPerSide) { throw "--marines and --chaos take 0 to $MaxPerSide" }
if ($opt.marines + $opt.chaos -eq 0) { throw 'nothing to start: give --marines N and/or --chaos M' }
if ($null -ne $opt.accuracy -and ($opt.accuracy -lt 0 -or $opt.accuracy -gt 1)) { throw '--accuracy takes 0 to 1' }
$hostPart = if ($opt.connect -match '^\[(.+)\]:(\d+)$') { $Matches[1], $Matches[2] } elseif ($opt.connect -match '^(.+):(\d+)$') { $Matches[1], $Matches[2] } else { $opt.connect, '27015' }
$hostName = $hostPart[0]; $hostPort = [int]$hostPart[1]
if (@(27015, 27016, 27020, 27025) -contains $hostPort) { throw "port $hostPort is refused: it is a live server's port. Use a test server on another port (27141 to 27149) with --bind 127.0.0.1." }
if (-not $opt.allowremote -and $hostName -notmatch '^(localhost|::1|127\.\d+\.\d+\.\d+)$') { throw "host '$hostName' is not loopback; pass --allow-remote to connect bots to another machine." }

# Where logs and traces may go: not inside this repository or the game install.
function Get-GameDir {
    if ($env:SM_GAME_DIR -and (Test-Path -LiteralPath (Join-Path $env:SM_GAME_DIR 'data'))) { return $env:SM_GAME_DIR }
    $local = Join-Path $RepoRoot 'config\local.json'
    if (Test-Path -LiteralPath $local) { try { $g = (Get-Content -LiteralPath $local -Raw | ConvertFrom-Json).gameDir; if ($g) { return [string]$g } } catch { } }
    $disc = Join-Path $env:LOCALAPPDATA 'sm-reimpl\discovery'
    if (Test-Path -LiteralPath $disc) {
        foreach ($d in (Get-ChildItem -LiteralPath $disc -Directory | Sort-Object Name -Descending)) {
            $f = Join-Path $d.FullName 'game-dir.txt'
            if (Test-Path -LiteralPath $f) { return (Get-Content -LiteralPath $f -Raw).Trim() }
        }
    }
    return $null
}
# Every checkout of this repository: this one, the main checkout that owns the common .git directory (this one may be a worktree) and every listed worktree.
function Get-RepoCheckouts {
    $roots = @($RepoRoot)
    if (Get-Command git.exe -ErrorAction SilentlyContinue) {
        try {
            $common = (& git.exe -C $RepoRoot rev-parse --path-format=absolute --git-common-dir 2>$null)
            if ($LASTEXITCODE -eq 0 -and $common) { $roots += (Split-Path -Parent ([string]$common).Trim().Replace('/', '\')) }
            foreach ($line in (& git.exe -C $RepoRoot worktree list --porcelain 2>$null)) {
                if ($line -like 'worktree *') { $roots += $line.Substring(9).Trim().Replace('/', '\') }
            }
        } catch { }
    }
    return @($roots | Where-Object { $_ } | Select-Object -Unique)
}
function Assert-OutsideProtected($path, $what) {
    if (-not $path) { return }
    $full = [IO.Path]::GetFullPath($path).TrimEnd('\') + '\'
    $protected = @(Get-RepoCheckouts); $g = Get-GameDir; if ($g) { $protected += $g }
    foreach ($p in $protected) {
        $pf = [IO.Path]::GetFullPath($p).TrimEnd('\') + '\'
        if ($full.StartsWith($pf, [StringComparison]::OrdinalIgnoreCase)) { throw "$what '$path' is inside $pf (a checkout of the repository or the game install); use a folder under %LOCALAPPDATA%." }
    }
}
Assert-OutsideProtected $opt.logdir '--log-dir'
Assert-OutsideProtected $opt.tracedir '--trace-dir'

if (-not (Test-Path -LiteralPath $Godot)) { throw "Godot is not installed yet: run client.cmd once (it downloads Godot 4.7.2 into $Tools\godot)." }
if (@(Read-Pids | Where-Object { Test-Same $_ }).Count -gt 0) { throw 'Bots from an earlier run are still running: bots.cmd --stop first (or --status).' }

# Roles and the class name each side's bot asks for (client.cmd --class resolves these names).
$roleNames = @{
    tactical = @{ marines = 'tactical'; chaos = 'chaos tactical' }
    assault  = @{ marines = 'assault';  chaos = 'raptor' }
    raptor   = @{ marines = 'assault';  chaos = 'raptor' }
    devastator = @{ marines = 'devastator'; chaos = 'havoc' }
    havoc    = @{ marines = 'devastator'; chaos = 'havoc' }
    heavy    = @{ marines = 'devastator'; chaos = 'havoc' }
}
$roles = if ($opt.classes) { @($opt.classes | ForEach-Object { $_.ToLowerInvariant() }) } else { @('tactical', 'assault', 'devastator') }
foreach ($r in $roles) { if (-not $roleNames.ContainsKey($r)) { throw "unknown class '$r' (tactical, assault, devastator, raptor, havoc)" } }

$client = Join-Path $RepoRoot 'client'
if (-not $opt.nobuild) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 10 SDK is required: run m1.cmd once, or winget install Microsoft.DotNet.SDK.10' }
    dotnet build (Join-Path $client 'SmClient.csproj') -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath (Join-Path $client '.godot'))) { & $Godot --headless --path $client --import | Out-Null }


$logDir = if ($opt.logdir) { $opt.logdir } else { Join-Path $State ('run-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
New-Item -ItemType Directory -Force -Path $State | Out-Null
Set-Content -LiteralPath $PidFile -Value '' -Encoding ascii

$extra = @(); if ($opt.extra) { $extra = @($opt.extra -split '\s+' | Where-Object { $_ }) }
$plan = @()
for ($k = 0; $k -lt $opt.marines; $k++) { $plan += [pscustomobject]@{ Side = 'marines'; Role = $roles[$k % $roles.Count] } }
for ($k = 0; $k -lt $opt.chaos; $k++)   { $plan += [pscustomobject]@{ Side = 'chaos';   Role = $roles[$k % $roles.Count] } }
# Interleave the sides so a server that balances anyway sees them alternate.
$m = @($plan | Where-Object Side -eq 'marines'); $c = @($plan | Where-Object Side -eq 'chaos')
$order = @(); for ($k = 0; $k -lt [Math]::Max($m.Count, $c.Count); $k++) { if ($k -lt $m.Count) { $order += $m[$k] }; if ($k -lt $c.Count) { $order += $c[$k] } }

$n = 0
foreach ($b in $order) {
    $n++
    $cls = $roleNames[$b.Role][$b.Side]
    $label = "bot-$n-$($b.Side)-$($cls -replace ' ', '_')"
    $cargs = @('--headless', '--path', $client, '--', '--connect', $opt.connect, '--bot', '--no-sound', '--no-hud', '--class', $cls, '--side', $b.Side)
    if ($null -ne $opt.accuracy) { $cargs += @('--bot-accuracy', $opt.accuracy.ToString([Globalization.CultureInfo]::InvariantCulture)) }
    if ($null -ne $opt.range)    { $cargs += @('--bot-range', $opt.range.ToString([Globalization.CultureInfo]::InvariantCulture)) }
    if ($opt.tracedir) { New-Item -ItemType Directory -Force -Path $opt.tracedir | Out-Null; $cargs += @('--bot-trace', (Join-Path $opt.tracedir ("$label.csv"))) }
    $cargs += $extra
    $argLine = ($cargs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $p = Start-Process -FilePath $Godot -ArgumentList $argLine -WorkingDirectory $RepoRoot -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $logDir "$label.log") -RedirectStandardError (Join-Path $logDir "$label.err")
    Add-Content -LiteralPath $PidFile -Value ("{0} {1} {2}" -f $p.Id, $p.StartTime.ToFileTimeUtc(), $label) -Encoding ascii
    Write-Host ("started {0} (pid {1}): --class '{2}' --side {3}" -f $label, $p.Id, $cls, $b.Side)
    if ($opt.stagger -gt 0 -and $n -lt $order.Count) { Start-Sleep -Milliseconds ([int]($opt.stagger * 1000)) }
}
Write-Host "Logs: $logDir"
Write-Host 'Stop them with: bots.cmd --stop   (only the bots started here; PIDs in the file below)'
Write-Host "  $PidFile"
if ($opt.for -gt 0) {
    Write-Host "Running for $($opt.for) s, then stopping..."
    Start-Sleep -Seconds $opt.for
    foreach ($r in Read-Pids) { [void](Stop-Tree $r) }
    Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
    Write-Host 'Stopped.'
}
