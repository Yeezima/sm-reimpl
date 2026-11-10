<#
.SYNOPSIS
  Polish pass baseline harness, Classic PvP (docs/polish-pass-plan.md, 2026-11-10). Test aid, never shipped.
.DESCRIPTION
  polish-pvp-baseline.ps1 [-Tag t] [-Port p] [-Marines 6] [-Chaos 6] [-Seconds 120] [-SafeSeconds 60]

  One Release server on 127.0.0.1 (build it first: dotnet build src/SmServer -c Release; SM_TICK_STATS=5), N + M headless bots started here
  through tools\godot.ps1 --background (their PIDs kept in pids.txt; only those are stopped), then one windowed measuring client on Forward+
  (--bot --bot-full-load --frame-profile --perf-probe --net-latency 5) and a second one on the Compatibility renderer in safe mode.
  Bounded by every client's --quit-after. Logs and summary.txt under %LOCALAPPDATA%\sm-reimpl\perf\polish-pass\<tag>\pvp\.
  The Musou scenes are tools\musou-perf.ps1 (clash1000, burst). Note: godot.ps1's "--" separator must be passed by splatting an array
  (as below); a literal -- on a PowerShell command line is eaten by PowerShell and every client argument then becomes an engine argument.
#>
param([int]$Port = 27411, [int]$Marines = 6, [int]$Chaos = 6, [int]$Seconds = 120, [int]$SafeSeconds = 60, [string]$Tag = 'polish-base')
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $RepoRoot 'tools\quiet-launch.ps1')
$out = Join-Path $env:LOCALAPPDATA "sm-reimpl\perf\polish-pass\$Tag\pvp"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$pids = @()
$serverDll = Join-Path $RepoRoot 'src\SmServer\bin\Release\net10.0\SmServer.dll'
$env:SM_TICK_STATS = '5'
$server = Start-QuietProcess -FilePath (Get-Command dotnet).Source -ArgumentList @($serverDll, '--bind', '127.0.0.1', '--port', "$Port", '--mode', 'annihilation', '--honor-side-requests') -WorkingDirectory $RepoRoot -StdOut (Join-Path $out 'server.log')
"server $($server.Id)" | Add-Content (Join-Path $out 'pids.txt')
Write-Host "server pid $($server.Id)"
try {
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline -and -not $server.HasExited) {
        if ((Get-Content -LiteralPath (Join-Path $out 'server.log') -ErrorAction SilentlyContinue) -match 'smserver on UDP') { break }
        Start-Sleep -Milliseconds 500
    }
    if ($server.HasExited) { throw 'server did not start' }
    Start-Sleep -Seconds 2
    $classes = @('tactical', 'assault', 'devastator')
    $total = $Seconds + $SafeSeconds + 90
    $n = 0
    foreach ($side in @(@('marines', $Marines), @('chaos', $Chaos))) {
        for ($i = 0; $i -lt $side[1]; $i++) {
            $cls = $classes[$i % 3]
            $log = Join-Path $out ("bot-{0}-{1}.log" -f $side[0], $i)
            $ga = @('--background', '--log', $log, '--'); $ca = @('--connect', "127.0.0.1:$Port", '--bot', '--no-sound', '--content', 'extended', '--side', $side[0], '--class', $cls, '--quit-after', "$total")
            $txt = & (Join-Path $RepoRoot 'tools\godot.ps1') @ga @ca 2>&1 | Out-String
            if ($txt -match 'started pid (\d+)') { $pids += [int]$Matches[1]; "bot $($Matches[1])" | Add-Content (Join-Path $out 'pids.txt') }
            $n++
            Start-Sleep -Milliseconds 1500
        }
    }
    Write-Host "bots: $($pids -join ' ')"
    Start-Sleep -Seconds 15
    # Forward+ measuring client (a full-load bot: it fights, so its frames carry the fight's effects and sounds off).
    $clientLog = Join-Path $out 'client-forward.log'
    $ga = @('--windowed', '--disable-vsync', '--log', $clientLog, '--'); $ca = @('--connect', "127.0.0.1:$Port", '--no-sound', '--content', 'extended', '--bot', '--bot-full-load', '--side', 'marines', '--class', 'tactical', '--frame-profile', '--frame-profile-warmup', '20', '--perf-probe', '--net-latency', '5', '--quit-after', "$Seconds")
    & (Join-Path $RepoRoot 'tools\godot.ps1') @ga @ca *> $null
    Write-Host "forward client exited $LASTEXITCODE"
    # Compatibility renderer (safe mode) client.
    $safeLog = Join-Path $out 'client-safe.log'
    $ga = @('--windowed', '--disable-vsync', '--rendering-method', 'gl_compatibility', '--rendering-driver', 'opengl3', '--log', $safeLog, '--'); $ca = @('--connect', "127.0.0.1:$Port", '--no-sound', '--content', 'extended', '--bot', '--bot-full-load', '--side', 'marines', '--class', 'tactical', '--safe-mode', '--frame-profile', '--frame-profile-warmup', '20', '--perf-probe', '--net-latency', '5', '--quit-after', "$SafeSeconds")
    & (Join-Path $RepoRoot 'tools\godot.ps1') @ga @ca *> $null
    Write-Host "safe client exited $LASTEXITCODE"
}
finally {
    foreach ($p in $pids) { & taskkill.exe /PID $p /T /F 2>$null | Out-Null }
    if (-not $server.HasExited) { & taskkill.exe /PID $server.Id /T /F | Out-Null }
    Remove-Item Env:SM_TICK_STATS -ErrorAction SilentlyContinue
}
$sum = @("pvp baseline tag $Tag at $(Get-Date -Format s): $Marines v $Chaos bots + 1 measuring client")
$sum += (Get-Content -LiteralPath (Join-Path $out 'server.log') | Select-String -Pattern 'TICKSTATS|TICKPACE|TICKBYTES|TICKSPIKE' | ForEach-Object { $_.Line })
foreach ($f in @('client-forward.log', 'client-safe.log')) {
    $sum += "---- $f"
    $sum += (Get-Content -LiteralPath (Join-Path $out $f) | Select-String -Pattern '^perf:|profile .*SUMMARY|profile .*hitch|NETLAT|renderer:|warmed|read in|loaded|jit prewarm' | ForEach-Object { $_.Line })
}
$sum | Set-Content -LiteralPath (Join-Path $out 'summary.txt') -Encoding utf8
Write-Host "summary: $(Join-Path $out 'summary.txt')"
