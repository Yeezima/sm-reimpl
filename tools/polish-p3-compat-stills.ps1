# Polish pass P3: Compatibility-renderer stills of the power sword's hit flare and the melta cone (the instance-uniform fix). Through tools\godot.ps1 --windowed only.
# P3: stills on the Compatibility renderer (--safe-mode) of the power sword's hit flare in a Musou fight and the melta cone in a Classic match. Hidden desktop only (godot.ps1 --windowed).
param([string]$Tag = 'before')
$ErrorActionPreference = 'Continue'
$sp = Join-Path $env:LOCALAPPDATA 'sm-reimpl\perf\polish-pass\logs'; New-Item -ItemType Directory -Force -Path $sp | Out-Null
$repo = Split-Path -Parent $PSScriptRoot
$media = "D:\sm-reimpl\media\previews\polish-pass\compat-uniforms\$Tag"
New-Item -ItemType Directory -Force -Path $media | Out-Null
Set-Location $repo
. "$repo\tools\quiet-launch.ps1"
$compat = @('--rendering-method', 'gl_compatibility', '--rendering-driver', 'opengl3')
# 1. Musou: the scripted melee player with the power sword against a dense crowd, stills from --tfw-test into the folder.
$env:SM_TFW_KEEP_HUMAN = '1'; $env:SM_TFW_STRESS = '300,0'
$srv = Start-QuietProcess -FilePath (Get-Command dotnet).Source -ArgumentList @("$repo\src\SmServer\bin\Release\net10.0\SmServer.dll", '--bind', '127.0.0.1', '--port', '27479', '--content', 'musou') -WorkingDirectory $repo -StdOut "$sp\compat-$Tag-musou-server.log"
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline -and -not ((Get-Content "$sp\compat-$Tag-musou-server.log" -ErrorAction SilentlyContinue) -match 'smserver on UDP')) { Start-Sleep -Milliseconds 500 }
Start-Sleep 2
$ga = @('--windowed') + $compat + @('--log', "$sp\compat-$Tag-musou-client.log", '--')
$ca = @('--connect', '127.0.0.1:27479', '--content', 'musou', '--no-sound', '--safe-mode', '--class', 'assault', '--melee', 'powersword', '--tfw-melee-aim', '--tfw-feel-capture', '--tfw-test', "$media\musou", '--quit-after', '150')
& "$repo\tools\godot.ps1" @ga @ca *> $null
"musou client exited $LASTEXITCODE"
if (-not $srv.HasExited) { & taskkill.exe /PID $srv.Id /T /F | Out-Null }
Remove-Item Env:SM_TFW_KEEP_HUMAN, Env:SM_TFW_STRESS -ErrorAction SilentlyContinue
# 2. Classic: a melta Tactical bot against one bot, screenshots every 2 s for 30 s.
$srv = Start-QuietProcess -FilePath (Get-Command dotnet).Source -ArgumentList @("$repo\src\SmServer\bin\Release\net10.0\SmServer.dll", '--bind', '127.0.0.1', '--port', '27478', '--mode', 'annihilation', '--honor-side-requests') -WorkingDirectory $repo -StdOut "$sp\compat-$Tag-classic-server.log"
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline -and -not ((Get-Content "$sp\compat-$Tag-classic-server.log" -ErrorAction SilentlyContinue) -match 'smserver on UDP')) { Start-Sleep -Milliseconds 500 }
Start-Sleep 2
$ga = @('--background', '--log', "$sp\compat-$Tag-classic-bot.log", '--'); $ca = @('--connect', '127.0.0.1:27478', '--bot', '--no-sound', '--content', 'extended', '--side', 'chaos', '--class', 'raptor', '--quit-after', '80')
$txt = & "$repo\tools\godot.ps1" @ga @ca *>&1 | Out-String; $botPid = 0; if ($txt -match 'started pid (\d+)') { $botPid = [int]$Matches[1] }
Start-Sleep 6
$ga = @('--windowed') + $compat + @('--log', "$sp\compat-$Tag-classic-client.log", '--')
$ca = @('--connect', '127.0.0.1:27478', '--no-sound', '--safe-mode', '--content', 'extended', '--bot', '--bot-full-load', '--side', 'marines', '--class', 'tactical', '--primary', 'melta', '--screenshot', "$media\classic-melta.png", '--screenshot-every', '2', '--quit-after', '45')
& "$repo\tools\godot.ps1" @ga @ca *> $null
"classic client exited $LASTEXITCODE"
if ($botPid) { & taskkill.exe /PID $botPid /T /F | Out-Null }
if (-not $srv.HasExited) { & taskkill.exe /PID $srv.Id /T /F | Out-Null }
Get-ChildItem $media -Recurse -File | Measure-Object | Select-Object Count
Get-Content "$sp\compat-$Tag-musou-client.log" | Select-String 'renderer:|TFWTEST|melee fx|flare' | Select-Object -First 8 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(200, $_.Line.Length)) }
"COMPATDONE" | Out-File "$sp\compat-$Tag.done"
