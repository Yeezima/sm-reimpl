<#
.SYNOPSIS
  bots-new-maps-r2 measuring harness (2026-11-16), Classic PvP, one map and mode per run. Test aid, never shipped.
.DESCRIPTION
  bots-measure.ps1 -Map mp_tdm_17 [-Mode annihilation|seize] [-Tag t] [-Marines 6] [-Chaos 6] [-Seconds 300] [-Client '--extra args']

  One Release server on 127.0.0.1 (SM_BOT_STATS=30), headless bots through tools\godot.ps1 --background (their PIDs kept; only those are stopped), run for -Seconds after the last
  bot joined. Writes server.log and bot logs under %LOCALAPPDATA%\sm-reimpl\perf\bots-new-maps-r2\<tag>\<map>-<mode>\ and a one-line result.txt:
  stuck detections (the bots' "nav stuck" lines), "unreachable" samples, deaths by the world, then the server's last BOTSTATS line.
#>
param([Parameter(Mandatory)][string]$Map, [string]$Mode = 'annihilation', [string]$Tag = 'base', [int]$Marines = 6, [int]$Chaos = 6, [int]$Seconds = 300, [int]$Port = 0, [string]$Client = '')
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $RepoRoot 'tools\quiet-launch.ps1')
$out = Join-Path $env:LOCALAPPDATA "sm-reimpl\perf\bots-new-maps-r2\$Tag\$Map-$Mode"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path $out | Out-Null
if ($Port -eq 0) {
    foreach ($try in 27701..27760) {
        try { $l = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $try)); $l.Close(); $Port = $try; break } catch { }
    }
    if ($Port -eq 0) { throw 'no free UDP port in 27701..27760' }
}
$pids = @()
$serverDll = Join-Path $RepoRoot 'src\SmServer\bin\Release\net10.0\SmServer.dll'
$env:SM_BOT_STATS = '30'
$server = Start-QuietProcess -FilePath (Get-Command dotnet).Source -ArgumentList @($serverDll, '--bind', '127.0.0.1', '--port', "$Port", '--map', $Map, '--mode', $Mode, '--honor-side-requests') -WorkingDirectory $RepoRoot -StdOut (Join-Path $out 'server.log')
Remove-Item Env:SM_BOT_STATS -ErrorAction SilentlyContinue
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
            $txt = & (Join-Path $RepoRoot 'tools\godot.ps1') @ga @ca *>&1 | Out-String
            if ($txt -match 'started pid (\d+)') { $pids += [int]$Matches[1]; "bot $($Matches[1])" | Add-Content (Join-Path $out 'pids.txt') }
            else { throw "a bot did not report its pid: $txt" }
            Start-Sleep -Milliseconds 1200
        }
    }
    $t0 = Get-Date
    while (((Get-Date) - $t0).TotalSeconds -lt $Seconds -and -not $server.HasExited) { Start-Sleep -Seconds 5 }
}
finally {
    foreach ($p in $pids) { & taskkill.exe /PID $p /T /F 2>$null | Out-Null }
    if (-not $server.HasExited) { & taskkill.exe /PID $server.Id /T /F | Out-Null }
}
$sl = @(Get-Content -LiteralPath (Join-Path $out 'server.log') | Where-Object { $_ -match '^BOTSTATS' })
$last = if ($sl.Count) { $sl[-1] } else { 'no BOTSTATS' }
$stuck = 0; $unr = 0; $esc = 0; $pit = 0; $retreats = 0; $hunts = 0
foreach ($f in Get-ChildItem $out -Filter 'bot-*.log') {
    $c = Get-Content -LiteralPath $f.FullName
    $stuck += @($c | Where-Object { $_ -match 'nav stuck #' }).Count
    $nl = $c | Where-Object { $_ -match 'escapes \d+, drops given up \d+, pit jumps \d+' } | Select-Object -Last 1
    if ($nl -match 'escapes (\d+), drops given up \d+, pit jumps (\d+)') { $esc += [int]$Matches[1]; $pit += [int]$Matches[2] }
    $unr += @($c | Where-Object { $_ -match 'unreachable' }).Count
    $retreats += @($c | Where-Object { $_ -match ' retreat \d+ to cover' }).Count
    $hunts += @($c | Where-Object { $_ -match ' hunt \d+ on P' }).Count
}
$falls = @(Get-Content -LiteralPath (Join-Path $out 'server.log') | Where-Object { $_ -match '^BOTFALL' }).Count
$caps = @(Get-Content -LiteralPath (Join-Path $out 'server.log') | Where-Object { $_ -match 'point . .* (captured|neutralised) by' }).Count
$res = "$Map $Mode tag $Tag : stuck $stuck, unreachable $unr, retreats $retreats, hunts $hunts, escapes $esc, pit jumps $pit, falls $falls, point events $caps | $last"
$res | Set-Content (Join-Path $out 'result.txt')
Write-Host $res
