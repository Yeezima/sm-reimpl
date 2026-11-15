<#
.SYNOPSIS
  classic-maps-perf measuring harness (2026-11-16), Classic PvP, one map per run. Test aid, never shipped.
.DESCRIPTION
  classic-maps-perf.ps1 -Map mp_tdm_09 [-Mode annihilation|seize] [-Tag t] [-Port p] [-Marines 8] [-Chaos 8] [-SoakSeconds 150] [-NoClient] [-ClientArgs '...']

  One Release server on 127.0.0.1 (SM_TICK_STATS=5), Marines + Chaos headless bots started through tools\godot.ps1 --background (their PIDs kept in pids.txt;
  only those are stopped), then one windowed observer client (--observe --observe-tour <places> --perf-probe --perf-tour --frame-profile) that walks the map's
  places (both spawn rooms, every control point, three long open views), 5 s each, so every place gets its own frame times, draw calls and lights. The server
  keeps ticking with the full lobby until -SoakSeconds have passed since the bots started; its working set is sampled every 15 s.
  Logs and summary under %LOCALAPPDATA%\sm-reimpl\perf\classic-maps-perf\<tag>\<map>-<mode>\ (places.txt names the places in order; tools\classic-maps-perf-table.py
  turns the logs into the table). Bounded by --quit-after. The machine's load is sampled before and after the client run and written to summary.txt.
#>
param([Parameter(Mandatory)][string]$Map, [string]$Mode = 'annihilation', [int]$Port = 27631, [int]$Marines = 8, [int]$Chaos = 8, [int]$SoakSeconds = 150, [string]$Tag = 'base', [string]$ClientArgs = '', [switch]$NoClient)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $RepoRoot 'tools\quiet-launch.ps1')
# Spawn-start centres (Marines m, Chaos c) and control points p per map, from `smtool maps-audit` (game metres, Z up).
$maps = @{
 'mp_tdm_09'    = @{ m = @(245,-937.4,5.5); c = @(154.5,-664.4,5.6); p = @(@(110,-785,4), @(200,-800,0), @(290,-815,4)) }
 'mp_tdm_01'    = @{ m = @(40,-78.9,1); c = @(-21,159,1); p = @(@(-35,-10,1), @(10,40,5), @(55,90,1)) }
 'mp_tdm_11'    = @{ m = @(90.4,-124.4,8.9); c = @(-60,106.2,8.8); p = @(@(-65,0,7), @(95,-20,7), @(15,70,13), @(15,-90,13)) }
 'mp_tdm_17'    = @{ m = @(0,181,2); c = @(0,-181,2); p = @(@(-15.3,-87.2,5), @(0,0,4), @(15,87,5)) }
 'mp_tdm_18'    = @{ m = @(464.6,210.9,-35); c = @(464.5,478.8,-35); p = @(@(367.5,345,-41), @(450,425,-34.5), @(450,265,-34.5), @(557.5,345,-41.5)) }
 'mp_dlc1_map1' = @{ m = @(465,12.5,7); c = @(335,-242.5,7); p = @(@(438.5,-115,12), @(361.5,-115,12), @(362.5,-35,13.5), @(437.5,-195,13)) }
 'mp_dlc1_map2' = @{ m = @(-56.8,236.2,38); c = @(206.9,235.7,38); p = @(@(80,185,41), @(126,280,39.5), @(34,280,39.5)) }
 'mp_dlc2_map1' = @{ m = @(-122.5,10,2); c = @(120,-10,2); p = @(@(0,0,1), @(55,50,3), @(-55,-50,3)) }
 'mp_dlc2_map2' = @{ m = @(-105.2,49.8,3); c = @(109.8,-47.3,3); p = @(@(0,0,5.3), @(-10,90,4.1), @(10,-90,3.7)) }
 'mp_dlc2_map3' = @{ m = @(25,-88,2); c = @(30,198.5,1); p = @(@(-5,5,2.5), @(113.6,55,4), @(-5,105,2)) }
 'mp_dlc2_map4' = @{ m = @(40,122.5,5); c = @(-40,-123,5); p = @(@(-30,70,6.5), @(0,0,1.7), @(30,-70,7.5)) }
}
if (-not $maps.ContainsKey($Map)) { throw "unknown map $Map" }
$d = $maps[$Map]
function Pose($from, $to, [double]$up, [double]$pitch) {
    $dx = $to[0] - $from[0]; $dy = $to[1] - $from[1]
    $yaw = [math]::Atan2(-$dx, $dy) * 180 / [math]::PI   # game yaw 0 faces +Y; positive turns toward -X
    ('{0:F1},{1:F1},{2:F1},{3:F0},{4:F0}' -f $from[0], $from[1], ($from[2] + $up), $yaw, $pitch)
}
$mid = @((($d.m[0] + $d.c[0]) / 2), (($d.m[1] + $d.c[1]) / 2), (($d.m[2] + $d.c[2]) / 2))
$places = [System.Collections.Generic.List[string]]::new(); $names = [System.Collections.Generic.List[string]]::new()
function Add-Place([string]$name, [string]$pose) { $script:names.Add($name); $script:places.Add($pose) }
Add-Place 'settle' (Pose $d.m $d.c 2.5 0)                 # the first window also carries the camera's arrival; dropped from the table
Add-Place 'spawn Marines' (Pose $d.m $d.c 2.5 0)
Add-Place 'spawn Chaos' (Pose $d.c $d.m 2.5 0)
$i = 0
foreach ($p in $d.p) {
    $i++
    $to = $mid; if ([math]::Abs($p[0] - $mid[0]) + [math]::Abs($p[1] - $mid[1]) -lt 3) { $to = $d.c }
    Add-Place "point $i" (Pose $p $to 2.5 0)
}
Add-Place 'open mid to Chaos' (Pose @($mid[0], $mid[1], ($mid[2] + 18)) $d.c 0 -8)
Add-Place 'open mid to Marines' (Pose @($mid[0], $mid[1], ($mid[2] + 18)) $d.m 0 -8)
Add-Place 'open Marine end down map' (Pose @($d.m[0], $d.m[1], ($d.m[2] + 14)) $d.c 0 -6)
$tour = ($places -join ';')

$out = Join-Path $env:LOCALAPPDATA "sm-reimpl\perf\classic-maps-perf\$Tag\$Map-$Mode"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path $out | Out-Null
($names -join "`n") | Set-Content (Join-Path $out 'places.txt')
$pids = @()
$serverDll = Join-Path $RepoRoot 'src\SmServer\bin\Release\net10.0\SmServer.dll'
$env:SM_TICK_STATS = '5'
function Load-Sample {
    $c = (Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average
    $q = (Get-CimInstance Win32_PerfFormattedData_PerfOS_System).ProcessorQueueLength
    "cpu $c %, run queue $q, godot processes $(@(Get-Process -Name 'Godot*' -ErrorAction SilentlyContinue).Count)"
}
$server = Start-QuietProcess -FilePath (Get-Command dotnet).Source -ArgumentList @($serverDll, '--bind', '127.0.0.1', '--port', "$Port", '--map', $Map, '--mode', $Mode, '--honor-side-requests') -WorkingDirectory $RepoRoot -StdOut (Join-Path $out 'server.log')
"server $($server.Id)" | Add-Content (Join-Path $out 'pids.txt')
$mem = @(); $load0 = ''; $load1 = ''
$t0 = Get-Date
try {
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline -and -not $server.HasExited) {
        if ((Get-Content -LiteralPath (Join-Path $out 'server.log') -ErrorAction SilentlyContinue) -match 'smserver on UDP') { break }
        Start-Sleep -Milliseconds 500
    }
    if ($server.HasExited) { throw 'server did not start' }
    $mem += "server up after $([int]((Get-Date) - $t0).TotalSeconds) s (map read, nav): working set $([int]((Get-Process -Id $server.Id).WorkingSet64 / 1MB)) MB"
    Start-Sleep -Seconds 2
    $classes = @('tactical', 'assault', 'devastator')
    $total = $SoakSeconds + 200
    $botStart = Get-Date
    foreach ($side in @(@('marines', $Marines), @('chaos', $Chaos))) {
        for ($i = 0; $i -lt $side[1]; $i++) {
            $cls = $classes[$i % 3]
            $log = Join-Path $out ("bot-{0}-{1}.log" -f $side[0], $i)
            $ga = @('--background', '--log', $log, '--'); $ca = @('--connect', "127.0.0.1:$Port", '--bot', '--no-sound', '--side', $side[0], '--class', $cls, '--quit-after', "$total")
            $txt = & (Join-Path $RepoRoot 'tools\godot.ps1') @ga @ca *>&1 | Out-String
            if ($txt -match 'started pid (\d+)') { $pids += [int]$Matches[1]; "bot $($Matches[1])" | Add-Content (Join-Path $out 'pids.txt') }
            Start-Sleep -Milliseconds 1200
        }
    }
    Write-Host "bots: $($pids.Count)"
    Start-Sleep -Seconds 15
    $mem += "after bots joined: server working set $([int]((Get-Process -Id $server.Id).WorkingSet64 / 1MB)) MB"
    $load0 = Load-Sample
    if (-not $NoClient) {
        $clientSecs = [int](5 * ($places.Count + 1) + 25)
        $clientLog = Join-Path $out 'client.log'
        $extra = @(); if ($ClientArgs) { $extra = @($ClientArgs -split ' ' | Where-Object { $_ }) }
        $ga = @('--windowed', '--disable-vsync', '--log', $clientLog, '--'); $ca = @('--connect', "127.0.0.1:$Port", '--observe', '--observe-tour', $tour, '--no-sound', '--frame-profile', '--frame-profile-warmup', '5', '--perf-probe', '--perf-tour', '--net-latency', '5', '--quit-after', "$clientSecs") + $extra
        & (Join-Path $RepoRoot 'tools\godot.ps1') @ga @ca *> $null
        Write-Host "client exited $LASTEXITCODE"
        $load1 = Load-Sample
    }
    while (((Get-Date) - $botStart).TotalSeconds -lt $SoakSeconds -and -not $server.HasExited) {
        Start-Sleep -Seconds 15
        $mem += "t+$([int]((Get-Date) - $botStart).TotalSeconds) s: server working set $([int]((Get-Process -Id $server.Id).WorkingSet64 / 1MB)) MB"
    }
    $mem += "end: server working set $([int]((Get-Process -Id $server.Id).WorkingSet64 / 1MB)) MB, server cpu seconds $([int](Get-Process -Id $server.Id).TotalProcessorTime.TotalSeconds)"
}
finally {
    foreach ($p in $pids) { & taskkill.exe /PID $p /T /F 2>$null | Out-Null }
    if (-not $server.HasExited) { & taskkill.exe /PID $server.Id /T /F | Out-Null }
    Remove-Item Env:SM_TICK_STATS -ErrorAction SilentlyContinue
}
$sum = @("classic-maps-perf $Map $Mode tag $Tag at $(Get-Date -Format s): $Marines v $Chaos headless bots + 1 observer client", "machine load before the client run: $load0", "machine load after: $load1")
$sum += $mem
$sum += (Get-Content -LiteralPath (Join-Path $out 'server.log') | Select-String -Pattern 'TICKSTATS|TICKPACE|TICKBYTES|TICKSPIKE' | ForEach-Object { $_.Line })
$cl = Join-Path $out 'client.log'
if (Test-Path -LiteralPath $cl) { $sum += (Get-Content -LiteralPath $cl | Select-String -Pattern '^perf:|profile .*SUMMARY|profile .*hitch|NETLAT|renderer:|warmed|read in|loaded|jit prewarm|LOADTIME|physics:|observe tour' | ForEach-Object { $_.Line }) }
$sum | Set-Content -LiteralPath (Join-Path $out 'summary.txt') -Encoding utf8
Write-Host "summary: $(Join-Path $out 'summary.txt')"
