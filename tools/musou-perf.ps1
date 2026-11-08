<#
.SYNOPSIS
  Musou performance pass harness (musou/perf-pass, 2026-11-08): the same scenes, camera and workload before and after a change. Test aid, never shipped.
.DESCRIPTION
  musou-perf.ps1 -Scene clash200|clash500|clash1000|burst|pvp [-Tag name] [-Port p] [-Seconds s] [-Headless] [-ClientArgs '...'] [-ServerEnv 'K=V;K=V']

  Starts one server on 127.0.0.1 (Release, already built: dotnet build src/SmServer -c Release) and one client through godot.cmd (windowed on the private
  desktop unless -Headless), waits for the client to quit, stops the server it started (by its own PID only) and writes both logs plus a summary to
  %LOCALAPPDATA%\sm-reimpl\perf\musou-perf\<tag>\<scene>\ (server.log, client.log, summary.txt).
    clashN : SM_TFW_ARMY_TEST=clash:N (a garrison of N at the point nearest the human, a wave of N sent at it), the human held 35 m short and the camera on
             the nearest army; client --perf-probe and --frame-profile; server SM_TICK_STATS=5. 150 s of fight.
    burst  : a plain Musou server; client --musou-death-spike-test 200 (200 Orks 8 m ahead die by one blast every 4 s, 15 bursts; burst 1 is the cold one).
    pvp    : Classic PvP Annihilation on Manufactorum: one client as a full-load bot (--bot --bot-full-load) for -Seconds; its frames and the server's ticks.
  Bounded: every run ends by the client's own timer (--quit-after) and the harness's timeout; nothing binds beyond 127.0.0.1.
#>
param(
    [Parameter(Mandatory)][ValidateSet('clash200', 'clash500', 'clash1000', 'burst', 'pvp')][string]$Scene,
    [string]$Tag = 'run',
    [int]$Port = 27311,
    [int]$Seconds = 150,
    [switch]$Headless,
    [string]$ClientArgs = '',
    [string]$ServerEnv = '',
    [int]$Every = 30
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'quiet-launch.ps1')

$out = Join-Path $env:LOCALAPPDATA "sm-reimpl\perf\musou-perf\$Tag\$Scene"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$serverLog = Join-Path $out 'server.log'; $clientLog = Join-Path $out 'client.log'
$shots = Join-Path $out 'shots'

$serverDll = Join-Path $RepoRoot 'src\SmServer\bin\Release\net10.0\SmServer.dll'
if (-not (Test-Path $serverDll)) { throw "build the server first: dotnet build src/SmServer -c Release ($serverDll missing)" }

$env:SM_TICK_STATS = '5'
$serverArgs = @($serverDll, '--bind', '127.0.0.1', '--port', "$Port")
$client = @('--connect', "127.0.0.1:$Port", '--no-sound', '--perf-probe', '--frame-profile', '--frame-profile-warmup', '20')
switch -Regex ($Scene) {
    '^clash(\d+)$' {
        $env:SM_TFW_ARMY_TEST = "clash:$($Matches[1])"; $env:SM_TFW_KEEP_HUMAN = '1'
        $serverArgs += @('--content', 'musou')
        $client += @('--content', 'musou', '--tfw-test-fight', '--tfw-fight-hold', '--tfw-fight-look-armies', '--tfw-fight-seconds', "$Seconds", '--tfw-fight-every', "$Every")
    }
    '^burst$' {
        Remove-Item Env:SM_TFW_ARMY_TEST -ErrorAction SilentlyContinue; Remove-Item Env:SM_TFW_KEEP_HUMAN -ErrorAction SilentlyContinue
        $serverArgs += @('--content', 'musou')
        $client += @('--content', 'musou', '--musou-death-spike-test', '200', '--quit-after', '180')
    }
    '^pvp$' {
        Remove-Item Env:SM_TFW_ARMY_TEST -ErrorAction SilentlyContinue; Remove-Item Env:SM_TFW_KEEP_HUMAN -ErrorAction SilentlyContinue
        $serverArgs += @('--mode', 'annihilation')
        $client += @('--bot', '--bot-full-load', '--quit-after', "$Seconds")
    }
}
foreach ($kv in ($ServerEnv -split ';' | Where-Object { $_ -match '=' })) { $k, $v = $kv -split '=', 2; Set-Item -Path "Env:$k" -Value $v }
if ($ClientArgs) { $client += ($ClientArgs -split ' ' | Where-Object { $_ }) }
if ($Scene -like 'clash*') { $client += @('--quit-after', "$($Seconds + 300)", '--tfw-test', $shots) }   # bounded even if the fight never ends (a lost server)   # --tfw-test goes last (its argument is the PNG folder)

$server = Start-QuietProcess -FilePath (Get-Command dotnet).Source -ArgumentList $serverArgs -WorkingDirectory $RepoRoot -StdOut $serverLog
Write-Host "server pid $($server.Id) on 127.0.0.1:$Port ($Scene, tag $Tag)"
try {
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline -and -not $server.HasExited) {
        if ((Get-Content -LiteralPath $serverLog -ErrorAction SilentlyContinue) -match 'listening|Listening|bound|port') { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2
    if ($server.HasExited -or ((Get-Content -LiteralPath $serverLog -Raw -ErrorAction SilentlyContinue) -match 'already in use')) { throw "the server did not start on 127.0.0.1:$Port (see $serverLog): is another run still going?" }
    $godot = @(); if (-not $Headless) { $godot += '--windowed' }
    $godot += @('--disable-vsync', '--log', $clientLog, '--')   # vsync off: the frame time is the work, not the display's pacing
    & (Join-Path $RepoRoot 'tools\godot.ps1') @godot @client *> $null
    Write-Host "client exited $LASTEXITCODE"
}
finally {
    if (-not $server.HasExited) { & taskkill.exe /PID $server.Id /T /F | Out-Null }
    Remove-Item Env:SM_TICK_STATS, Env:SM_TFW_ARMY_TEST, Env:SM_TFW_KEEP_HUMAN -ErrorAction SilentlyContinue
}

# Summary: the server's tick lines, the client's perf and profile summaries, the death-spike lines.
$sum = @()
$sum += "scene $Scene tag $Tag at $(Get-Date -Format s)"
$sum += (Get-Content -LiteralPath $serverLog | Select-String -Pattern 'TICKSTATS|TFWARMY|ARMYPROF' | ForEach-Object { $_.Line })
$sum += (Get-Content -LiteralPath $clientLog | Select-String -Pattern '^perf:|profile .*SUMMARY|MUSOUSPIKE|hitch|TFWTEST (PASS|FAIL)' | ForEach-Object { $_.Line })
$sum | Set-Content -LiteralPath (Join-Path $out 'summary.txt') -Encoding utf8
Write-Host "summary: $(Join-Path $out 'summary.txt')"
