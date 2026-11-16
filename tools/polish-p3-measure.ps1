# Polish pass P3 measurement harness (docs/polish-pass-plan.md; test aid, never shipped). Everything through tools\godot.ps1 --windowed (the hidden desktop) or --background (headless); servers 127.0.0.1; own PIDs only.
# P3 measurements (before and after): Musou human join cold + warm with a deploy (the landing), Classic human join on the Compatibility renderer (first-use shader frames),
# Classic 13 players with sound (the sound-phase stalls). Bounded; 127.0.0.1 only; own PIDs only.
# -Before adds P3's restore switch to every measuring client (--no-shader-sweep): the numbers before P3.
param([string]$Tag = 'p3-before', [switch]$Before)
$restore = if ($Before) { @('--no-shader-sweep') } else { @() }
$ErrorActionPreference = 'Continue'
$sp = Join-Path $env:LOCALAPPDATA 'sm-reimpl\perf\polish-pass\logs'; New-Item -ItemType Directory -Force -Path $sp | Out-Null
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $env:LOCALAPPDATA "sm-reimpl\perf\polish-pass\$Tag"
New-Item -ItemType Directory -Force -Path $out | Out-Null
Set-Location $repo
. "$repo\tools\quiet-launch.ps1"
$env:SM_TICK_STATS = '5'
$cache = Join-Path $env:LOCALAPPDATA 'sm-reimpl\cache\ashfall'

function Run-Join([string]$label, [string]$port, [string[]]$serverMore, [string[]]$engineMore, [string[]]$clientMore, [int]$seconds) {
    $srv = Start-QuietProcess -FilePath (Get-Command dotnet).Source -ArgumentList (@("$repo\src\SmServer\bin\Release\net10.0\SmServer.dll", '--bind', '127.0.0.1', '--port', $port, '--honor-side-requests') + $serverMore) -WorkingDirectory $repo -StdOut "$out\$label-server.log"
    "server $label pid $($srv.Id) at $(Get-Date -Format s)"
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline -and -not $srv.HasExited -and -not ((Get-Content "$out\$label-server.log" -ErrorAction SilentlyContinue) -match 'smserver on UDP')) { Start-Sleep -Milliseconds 500 }
    Start-Sleep 2
    $botPid = 0
    if ($label -like 'classic*') {
        $ga = @('--background', '--log', "$out\$label-bot.log", '--'); $ca = @('--connect', "127.0.0.1:$port", '--bot', '--no-sound', '--content', 'extended', '--side', 'chaos', '--class', 'raptor', '--quit-after', "$($seconds + 40)")
        $txt = & "$repo\tools\godot.ps1" @ga @ca *>&1 | Out-String; if ($txt -match 'started pid (\d+)') { $botPid = [int]$Matches[1] }; Start-Sleep 5
    }
    $ga = @('--windowed', '--disable-vsync') + $engineMore + @('--log', "$out\$label-client.log", '--')
    $ca = @('--connect', "127.0.0.1:$port", '--no-sound', '--frame-profile', '--frame-profile-warmup', '30', '--quit-after', "$seconds") + $clientMore + $restore
    & "$repo\tools\godot.ps1" @ga @ca *> $null
    "client $label exited $LASTEXITCODE at $(Get-Date -Format s)"
    if ($botPid) { & taskkill.exe /PID $botPid /T /F | Out-Null }
    if (-not $srv.HasExited) { & taskkill.exe /PID $srv.Id /T /F | Out-Null }
}

if (Test-Path $cache) { Get-ChildItem $cache -Filter '*.bin' | Remove-Item -Force }
Run-Join 'musou-cold' '27485' @('--content', 'musou') @() @('--content', 'musou') 45
Run-Join 'musou-warm-deploy' '27484' @('--content', 'musou') @() @('--content', 'musou', '--tfw-deploy', 'b') 75
Run-Join 'classic-compat' '27483' @('--mode', 'annihilation') @('--rendering-method', 'gl_compatibility', '--rendering-driver', 'opengl3') @('--content', 'extended', '--class', 'tactical', '--side', 'marines', '--safe-mode') 40
Run-Join 'classic-forward' '27482' @('--mode', 'annihilation') @() @('--content', 'extended', '--class', 'tactical', '--side', 'marines') 40
& "$repo\tools\polish-pvp-baseline.ps1" -Tag "$Tag-sound" -Seconds 90 -SafeSeconds 0 -Sound -ClientArgs ($restore -join ' ') *>> "$out\sound-run.log"
Remove-Item Env:SM_TICK_STATS -ErrorAction SilentlyContinue
foreach ($f in 'musou-cold-client.log', 'musou-warm-deploy-client.log', 'classic-compat-client.log', 'classic-forward-client.log') {
    "==== $f"
    Get-Content (Join-Path $out $f) | Select-String 'LOADTIME|ashfall: .*(layout|view|kit|storm)|SUMMARY warm-up|hitch .* at [0-9.]+ s|ERROR|only allowed' | Select-Object -First 24 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(300, $_.Line.Length)) }
}
"==== sound"
Get-Content (Join-Path $env:LOCALAPPDATA "sm-reimpl\perf\polish-pass\$Tag-sound\pvp\client-forward.log") | Select-String 'hitch .*; sound \d|SUMMARY after|sound at exit' | ForEach-Object { $_.Line.Substring(0, [Math]::Min(260, $_.Line.Length)) }
'P3DONE' | Out-File "$sp\p3measure-$Tag.done"
