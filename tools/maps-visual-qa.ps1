<#
.SYNOPSIS
  maps-visual-qa capture aid (2026-11-16), Classic PvP, one map per run. Test aid, never shipped.
.DESCRIPTION
  maps-visual-qa.ps1 -Map mp_tdm_01 [-Tag before|after] [-OutDir dir] [-Every 2.5] [-Bots 2] [-ClientArgs '...']

  One Release server on 127.0.0.1 (a free loopback port), -Bots lean headless bots per side (bodies for the third-person views), and one windowed observer
  client on the hidden desktop (godot.cmd --windowed) with the free camera taking a still at each place of a tour: both spawn rooms in the four directions at
  eye height, each control point at eye height and from 4 m back and up (third person), and three long overviews. Stills: <OutDir>\<map>\<tag>_NNN.png,
  places.txt names them in order. Only the processes it starts are stopped, by PID. Bounded by --quit-after.
#>
param([Parameter(Mandatory)][string]$Map, [string]$Tag = 'before', [string]$OutDir = $env:SM_QA_MEDIA_DIR, [string]$PlaceRange = '', [string]$EngineArgs = '', [double]$Every = 2.5, [int]$Bots = 2, [string]$ClientArgs = '', [int]$Port = 0)
$ErrorActionPreference = 'Stop'
if (-not $OutDir) { throw 'pass -OutDir <folder> (or set SM_QA_MEDIA_DIR): stills show original assets and must stay outside the repository' }
$RepoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $RepoRoot 'tools\quiet-launch.ps1')
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
    $yaw = [math]::Atan2(-$dx, $dy) * 180 / [math]::PI
    ('{0:F1},{1:F1},{2:F1},{3:F0},{4:F0}' -f $from[0], $from[1], ($from[2] + $up), $yaw, $pitch)
}
function PoseYaw($from, [double]$yaw, [double]$up, [double]$pitch) { ('{0:F1},{1:F1},{2:F1},{3:F0},{4:F0}' -f $from[0], $from[1], ($from[2] + $up), $yaw, $pitch) }
function Yaw($from, $to) { [math]::Atan2(-($to[0] - $from[0]), ($to[1] - $from[1])) * 180 / [math]::PI }
$mid = @((($d.m[0] + $d.c[0]) / 2), (($d.m[1] + $d.c[1]) / 2), (($d.m[2] + $d.c[2]) / 2))
$places = [System.Collections.Generic.List[string]]::new(); $names = [System.Collections.Generic.List[string]]::new()
function Add-Place([string]$name, [string]$pose) { $script:names.Add($name); $script:places.Add($pose) }
Add-Place 'settle (dropped)' (PoseYaw $d.m (Yaw $d.m $d.c) 1.7 0)
foreach ($s in @(@('Marines', $d.m, $d.c), @('Chaos', $d.c, $d.m))) {
    $y0 = Yaw $s[1] $s[2]
    foreach ($k in 0..3) { Add-Place ("spawn $($s[0]) dir $k") (PoseYaw $s[1] ($y0 + 90 * $k) 1.7 0) }
}
$i = 0
foreach ($p in $d.p) {
    $i++
    $to = $mid; if ([math]::Abs($p[0] - $mid[0]) + [math]::Abs($p[1] - $mid[1]) -lt 3) { $to = $d.c }
    $y = Yaw $p $to
    Add-Place "point $i eye" (PoseYaw $p $y 1.7 0)
    Add-Place "point $i eye back" (PoseYaw $p ($y + 180) 1.7 0)
    $r = [math]::PI * $y / 180
    $back = @(($p[0] + 4 * [math]::Sin($r)), ($p[1] - 4 * [math]::Cos($r)), $p[2])
    Add-Place "point $i third person" (PoseYaw $back $y 3.2 -18)
}
Add-Place 'open mid to Chaos' (Pose @($mid[0], $mid[1], ($mid[2] + 18)) $d.c 0 -8)
Add-Place 'open mid to Marines' (Pose @($mid[0], $mid[1], ($mid[2] + 18)) $d.m 0 -8)
Add-Place 'open mid down' (Pose @($mid[0], $mid[1], ($mid[2] + 60)) $mid 0 -80)
Add-Place 'open mid up (sky)' (Pose @($mid[0], $mid[1], ($mid[2] + 2)) $d.c 0 55)
if ($PlaceRange -match '^(\d+)-(\d+)$') {   # e.g. 19-22: the settle place plus those places (1-based, as the before/after numbering)
    $a = [int]$Matches[1]; $b = [int]$Matches[2]
    $keep = @(0) + @(($a - 1)..($b - 1) | Where-Object { $_ -lt $places.Count })
    $places = [System.Collections.Generic.List[string]]($keep | ForEach-Object { $places[$_] }); $names = [System.Collections.Generic.List[string]]($keep | ForEach-Object { $names[$_] })
}
$tour = ($places -join ';')
$out = Join-Path $OutDir $Map
New-Item -ItemType Directory -Force -Path $out | Out-Null
$logDir = Join-Path $env:LOCALAPPDATA "sm-reimpl\perf\maps-visual-qa\$Map-$Tag"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$k = 0; $list = foreach ($n in $names) { '{0}_{1:000}: {2}' -f $Tag, (++$k), $n }
$list | Set-Content (Join-Path $out "$Tag-places.txt")
if ($Port -eq 0) {
    foreach ($try in 27631..27690) {
        try { $l = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $try)); $l.Close(); $Port = $try; break } catch { }
    }
    if ($Port -eq 0) { throw 'no free UDP port in 27631..27690: pass -Port' }
}
$pids = @()
$serverDll = Join-Path $RepoRoot (Join-Path "src" "SmServer/bin/Release/net10.0/SmServer.dll")
$runStart = Get-Date
$server = Start-QuietProcess -FilePath (Get-Command dotnet).Source -ArgumentList @($serverDll, '--bind', '127.0.0.1', '--port', "$Port", '--map', $Map, '--mode', 'annihilation', '--honor-side-requests') -WorkingDirectory $RepoRoot -StdOut (Join-Path $logDir 'server.log')
try {
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline -and -not $server.HasExited) {
        if ((Get-Content -LiteralPath (Join-Path $logDir 'server.log') -ErrorAction SilentlyContinue) -match 'smserver on UDP') { break }
        Start-Sleep -Milliseconds 500
    }
    if ($server.HasExited) { throw 'server did not start' }
    $total = [int](120 + $Every * ($places.Count + 2))
    foreach ($side in @('marines', 'chaos')) {
        for ($i = 0; $i -lt $Bots; $i++) {
            $log = Join-Path $logDir ("bot-{0}-{1}.log" -f $side, $i)
            $txt = & (Join-Path $RepoRoot 'tools\godot.ps1') --background --log $log -- --connect "127.0.0.1:$Port" --bot --no-sound --side $side --class tactical --quit-after "$($total + 60)" *>&1 | Out-String
            if ($txt -match 'started pid (\d+)') { $pids += [int]$Matches[1] } else { throw "a bot did not report its pid: $txt" }
            Start-Sleep -Milliseconds 1200
        }
    }
    Start-Sleep -Seconds 5
    $extra = @(); if ($ClientArgs) { $extra = @($ClientArgs -split ' ' | Where-Object { $_ }) }
    $eng = @(); if ($EngineArgs) { $eng = @($EngineArgs -split ' ' | Where-Object { $_ }) }
    $ga = @('--windowed') + $eng + @('--log', (Join-Path $logDir 'client.log'), '--')
    $ca = @('--connect', "127.0.0.1:$Port", '--observe', '--observe-tour', $tour, '--no-sound', '--no-hud', '--no-markers', '--screenshot', (Join-Path $out "$Tag.png"), '--screenshot-every', "$Every", '--quit-after', "$total") + $extra
    & (Join-Path $RepoRoot 'tools\godot.ps1') @ga @ca *> $null
    Write-Host "client exited $LASTEXITCODE; stills in $out"
}
finally {
    $ErrorActionPreference = 'Continue'   # root cause of the leaks: under 'Stop', taskkill's stderr ('not found') raised a terminating error and ended this block before the server was stopped
    foreach ($p in $pids) { & taskkill.exe /PID $p /T /F 2>$null | Out-Null }
    # HasExited proved unreliable for the quiet-launched server (31 leaked): always kill by its own PID, ignore 'not found'.
    & taskkill.exe /PID $server.Id /T /F 2>$null | Out-Null
    Get-Process -Id $server.Id -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    # the recorded PID did not always match the real server (31 leaked): also stop the server this run started, found by its exact command line
    # (this run's port AND map) and only if it was created after this run began, so a server someone else runs on the same port is never touched.
    $mine = [regex]::Escape("SmServer.dll --bind 127.0.0.1 --port $Port --map $Map ")
    Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -match $mine -and $_.CreationDate -ge $runStart.AddSeconds(-1) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
