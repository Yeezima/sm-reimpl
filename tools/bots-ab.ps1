<#
.SYNOPSIS
  bots/a1-perf (2026-11-23) matched bot-vs-bot measuring run, Classic PvP. Test aid, never shipped.
.DESCRIPTION
  bots-ab.ps1 -Map mp_manufactorum [-Mode annihilation|seize] [-Tag base|cand] [-Root <checkout>] [-Marines 4] [-Chaos 4] [-Seconds 300] [-NameSeed 7] [-Client '--extra']

  The same run as bots-measure.ps1, from the build of -Root (default: this checkout), so the base and the candidate run from two checkouts of the same harness:
  one Release server on 127.0.0.1 (SM_BOT_STATS=30, --name-seed), lean headless bots through -Root's tools\godot.ps1 --background with a fixed class order
  (tactical, assault, devastator; the Chaos counterparts by the server), their PIDs kept and only those stopped (by PID). Every 10 s it samples the CPU time and
  the working set of the server and of each bot process tree. Writes under %LOCALAPPDATA%\sm-reimpl\perf\bots-a1\<tag>\<map>-<mode>\: server.log, bot logs,
  samples.csv and result.txt (per-bot mean CPU cores and peak working set, the server's, then the last BOTSTATS and every BOTCOMBAT line).
  -DataDir <dir>: SM_DATA_DIR for the server and the bots, a private data root with its own nav cache (never the owner's cache).
  Bot matches are not bit-repeatable (real time, the network), so base and candidate are compared on totals and rates over equal runs.
#>
param([Parameter(Mandatory)][string]$Map, [string]$Mode = 'annihilation', [string]$Tag = 'base', [string]$Root = '', [int]$Marines = 4, [int]$Chaos = 4,
    [int]$Seconds = 300, [int]$Port = 0, [int]$NameSeed = 7, [string]$Client = '', [string]$DataDir = '')
$ErrorActionPreference = 'Stop'
$Here = Split-Path -Parent $PSScriptRoot
if (-not $Root) { $Root = $Here }
. (Join-Path $Here 'tools\quiet-launch.ps1')
$out = Join-Path $env:LOCALAPPDATA "sm-reimpl\perf\bots-a1\$Tag\$Map-$Mode"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path $out | Out-Null
if ($Port -eq 0) {
    foreach ($try in 27801..27860) {
        try { $l = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $try)); $l.Close(); $Port = $try; break } catch { }
    }
    if ($Port -eq 0) { throw 'no free UDP port in 27801..27860' }
}

function Get-Tree([int]$id) {
    # the process and its children (godot.ps1's console wrapper starts the real Godot as its child)
    $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId)
    $ids = [Collections.Generic.List[int]]::new(); $ids.Add($id)
    for ($k = 0; $k -lt $ids.Count; $k++) { foreach ($p in $all) { if ($p.ParentProcessId -eq $ids[$k] -and -not $ids.Contains([int]$p.ProcessId)) { $ids.Add([int]$p.ProcessId) } } }
    return $ids
}
function Get-Usage([int]$id) {
    $cpu = 0.0; $ws = 0L
    foreach ($t in (Get-Tree $id)) { $p = Get-Process -Id $t -ErrorAction SilentlyContinue; if ($p) { $cpu += $p.TotalProcessorTime.TotalSeconds; $ws += $p.WorkingSet64 } }
    return @($cpu, $ws)
}

$pids = @()
$serverDll = Join-Path $Root 'src\SmServer\bin\Release\net10.0\SmServer.dll'
if (-not (Test-Path $serverDll)) { throw "$serverDll is missing: dotnet build -c Release src\SmServer in $Root" }
if ($DataDir) { New-Item -ItemType Directory -Force -Path $DataDir | Out-Null; $env:SM_DATA_DIR = $DataDir }   # a private data root (its own nav cache), never the owner's
$env:SM_BOT_STATS = '30'
$server = Start-QuietProcess -FilePath (Get-Command dotnet).Source -ArgumentList @($serverDll, '--bind', '127.0.0.1', '--port', "$Port", '--map', $Map, '--mode', $Mode, '--honor-side-requests', '--name-seed', "$NameSeed") -WorkingDirectory $Root -StdOut (Join-Path $out 'server.log') -StdErr (Join-Path $out 'server.err.log')
Remove-Item Env:SM_BOT_STATS -ErrorAction SilentlyContinue
$samples = [Collections.Generic.List[string]]::new(); $samples.Add('t,who,pid,cpu_s,ws_mb')
try {
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline -and -not $server.HasExited) {
        if ((Get-Content -LiteralPath (Join-Path $out 'server.log') -ErrorAction SilentlyContinue) -match 'smserver on UDP') { break }
        Start-Sleep -Milliseconds 500
    }
    if ($server.HasExited) { throw 'server did not start' }
    Start-Sleep -Seconds 2
    $classes = @('tactical', 'assault', 'devastator')
    foreach ($side in @(@('marines', $Marines), @('chaos', $Chaos))) {
        for ($i = 0; $i -lt $side[1]; $i++) {
            $log = Join-Path $out ("bot-{0}-{1}.log" -f $side[0], $i)
            $ga = @('--background', '--log', $log, '--'); $ca = @('--connect', "127.0.0.1:$Port", '--bot', '--no-sound', '--side', $side[0], '--class', $classes[$i % 3], '--quit-after', "$($Seconds + 400)")
            if ($Client) { $ca += @($Client -split ' ' | Where-Object { $_ }) }
            $txt = & (Join-Path $Root 'tools\godot.ps1') @ga @ca *>&1 | Out-String
            if ($txt -match 'started pid (\d+)') { $pids += [int]$Matches[1]; "bot $($Matches[1]) $($side[0]) $($classes[$i % 3])" | Add-Content (Join-Path $out 'pids.txt') }
            else { throw "a bot did not report its pid: $txt" }
            Start-Sleep -Milliseconds 1200
        }
    }
    # the loads (map, collision, graph) are left out of the steady-state numbers: wait until every bot has joined (at most 300 s), then 20 s more
    $joinBy = (Get-Date).AddSeconds(300)
    while ((Get-Date) -lt $joinBy) {
        $joined = @(Get-ChildItem $out -Filter 'bot-*.log' | Where-Object { Select-String -LiteralPath $_.FullName -Pattern 'connected as player' -Quiet }).Count
        if ($joined -ge $pids.Count) { break }
        Start-Sleep -Seconds 5
    }
    "joined $joined of $($pids.Count) bots" | Add-Content (Join-Path $out 'pids.txt')
    Start-Sleep -Seconds 20
    $first = @{}; $peak = @{}
    $who = [Collections.Generic.List[object]]::new(); $who.Add([object[]]@('server', $server.Id)); foreach ($p in $pids) { $who.Add([object[]]@('bot', $p)) }
    $t0 = Get-Date
    while (((Get-Date) - $t0).TotalSeconds -lt $Seconds -and -not $server.HasExited) {
        $t = [int]((Get-Date) - $t0).TotalSeconds
        foreach ($w in $who) {
            $u = Get-Usage $w[1]
            if (-not $first.ContainsKey($w[1])) { $first[$w[1]] = @($t, $u[0]) }
            $peak[$w[1]] = [Math]::Max([double]($peak[$w[1]]), $u[1] / 1MB)
            $samples.Add(('{0},{1},{2},{3:F2},{4:F0}' -f $t, $w[0], $w[1], $u[0], ($u[1] / 1MB)))
            $last = @($t, $u[0]); Set-Variable -Name ("last_" + $w[1]) -Value $last
        }
        Start-Sleep -Seconds 10
    }
}
finally {
    Remove-Item Env:SM_DATA_DIR -ErrorAction SilentlyContinue
    foreach ($p in $pids) { & taskkill.exe /PID $p /T /F 2>$null | Out-Null }
    if (-not $server.HasExited) { & taskkill.exe /PID $server.Id /T /F | Out-Null }
}
$samples | Set-Content (Join-Path $out 'samples.csv')
$lines = [Collections.Generic.List[string]]::new()
$botCores = @(); $botPeaks = @()
foreach ($w in $who) {
    $l = Get-Variable -Name ("last_" + $w[1]) -ValueOnly -ErrorAction SilentlyContinue
    if (-not $l -or -not $first.ContainsKey($w[1]) -or $l[0] -le $first[$w[1]][0]) { continue }
    $cores = ($l[1] - $first[$w[1]][1]) / ($l[0] - $first[$w[1]][0])
    $lines.Add(('{0} {1}: {2:F3} cores, peak {3:F0} MB' -f $w[0], $w[1], $cores, $peak[$w[1]]))
    if ($w[0] -eq 'bot') { $botCores += $cores; $botPeaks += $peak[$w[1]] }
}
if ($botCores.Count) { $lines.Insert(0, ('bots {0}: mean {1:F3} cores, mean peak {2:F0} MB' -f $botCores.Count, ($botCores | Measure-Object -Average).Average, ($botPeaks | Measure-Object -Average).Average)) }
$sl = @(Get-Content -LiteralPath (Join-Path $out 'server.log') | Where-Object { $_ -match '^BOTSTATS' })
$lastStats = if ($sl.Count) { $sl[-1] } else { 'no BOTSTATS' }
$lines.Add($lastStats)
$all = @(Get-Content -LiteralPath (Join-Path $out 'server.log'))
$cut = -1; for ($k = 0; $k -lt $all.Count; $k++) { if ($all[$k] -match '^BOTSTATS') { $cut = $k } }
if ($cut -ge 0) { for ($k = $cut + 1; $k -lt $all.Count; $k++) { if ($all[$k] -match '^BOT(PLAYER|COMBAT) ') { $lines.Add($all[$k]) } } }
$lines.Insert(0, "$Map $Mode tag $Tag root $Root, $Marines v $Chaos, $Seconds s, name seed $NameSeed")
$lines | Set-Content (Join-Path $out 'result.txt')
$lines | Write-Host
