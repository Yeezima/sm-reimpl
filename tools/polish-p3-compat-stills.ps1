# Polish pass P3: Compatibility-renderer stills of the power sword's hit flare and the melta cone (the check of per-instance shader values). Through tools\godot.ps1 --windowed only.
# P3: stills on the Compatibility renderer (--safe-mode) of the power sword's hit flare in a Musou fight and the melta cone in a Classic match. Hidden desktop only (godot.ps1 --windowed).
# -Out: the stills' folder (default under %LOCALAPPDATA%\sm-reimpl\perf\polish-pass; pass a folder of the media drive for the owner).
# -Only musou|classic|sword|librarian runs one part (none: all four); -More 'a b' adds client arguments (a restore switch to tell a fault apart).
param([string]$Tag = 'stills', [string]$Out = '', [string]$Only = '', [string]$More = '')
$ErrorActionPreference = 'Continue'
$sp = Join-Path $env:LOCALAPPDATA 'sm-reimpl\perf\polish-pass\logs'; New-Item -ItemType Directory -Force -Path $sp | Out-Null
$repo = Split-Path -Parent $PSScriptRoot
$media = if ($Out) { $Out } else { Join-Path $env:LOCALAPPDATA "sm-reimpl\perf\polish-pass\compat-uniforms\$Tag" }
$more = @()
if ($More) { $more += $More -split ' ' }
New-Item -ItemType Directory -Force -Path $media | Out-Null
Set-Location $repo
. "$repo\tools\quiet-launch.ps1"
$compat = @('--rendering-method', 'gl_compatibility', '--rendering-driver', 'opengl3')
if (-not $Only -or $Only -eq 'musou') {
# 1. Musou: the scripted melee player with the power sword against a dense crowd, stills from --tfw-test into the folder.
$env:SM_TFW_KEEP_HUMAN = '1'; $env:SM_TFW_STRESS = '300,0'
$srv = Start-QuietProcess -FilePath (Get-Command dotnet).Source -ArgumentList @("$repo\src\SmServer\bin\Release\net10.0\SmServer.dll", '--bind', '127.0.0.1', '--port', '27479', '--content', 'musou') -WorkingDirectory $repo -StdOut "$sp\compat-$Tag-musou-server.log"
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline -and -not ((Get-Content "$sp\compat-$Tag-musou-server.log" -ErrorAction SilentlyContinue) -match 'smserver on UDP')) { Start-Sleep -Milliseconds 500 }
Start-Sleep 2
$ga = @('--windowed') + $compat + @('--log', "$sp\compat-$Tag-musou-client.log", '--')
$ca = @('--connect', '127.0.0.1:27479', '--content', 'musou', '--no-sound', '--safe-mode', '--class', 'assault', '--melee', 'powersword', '--tfw-melee-aim', '--tfw-feel-capture', '--tfw-test', "$media\musou", '--quit-after', '150') + $more
& "$repo\tools\godot.ps1" @ga @ca *> $null
"musou client exited $LASTEXITCODE"
if (-not $srv.HasExited) { & taskkill.exe /PID $srv.Id /T /F | Out-Null }
Remove-Item Env:SM_TFW_KEEP_HUMAN, Env:SM_TFW_STRESS -ErrorAction SilentlyContinue
}
if (-not $Only -or $Only -eq 'classic') {
# 2. Classic: a melta Tactical firing the drawn cone (--melta-cone: the data's blast is the default look) sideways every 3 s (--shot-test), screenshots every 0.25 s.
$srv = Start-QuietProcess -FilePath (Get-Command dotnet).Source -ArgumentList @("$repo\src\SmServer\bin\Release\net10.0\SmServer.dll", '--bind', '127.0.0.1', '--port', '27478', '--mode', 'annihilation', '--honor-side-requests') -WorkingDirectory $repo -StdOut "$sp\compat-$Tag-classic-server.log"
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline -and -not ((Get-Content "$sp\compat-$Tag-classic-server.log" -ErrorAction SilentlyContinue) -match 'smserver on UDP')) { Start-Sleep -Milliseconds 500 }
Start-Sleep 2
$ga = @('--background', '--log', "$sp\compat-$Tag-classic-bot.log", '--'); $ca = @('--connect', '127.0.0.1:27478', '--bot', '--no-sound', '--content', 'extended', '--side', 'chaos', '--class', 'raptor', '--quit-after', '80')
$txt = & "$repo\tools\godot.ps1" @ga @ca *>&1 | Out-String; $botPid = 0; if ($txt -match 'started pid (\d+)') { $botPid = [int]$Matches[1] }
Start-Sleep 6
$ga = @('--windowed') + $compat + @('--log', "$sp\compat-$Tag-classic-client.log", '--')
$ca = @('--connect', '127.0.0.1:27478', '--no-sound', '--safe-mode', '--content', 'extended', '--side', 'marines', '--class', 'tactical', '--primary', 'melta', '--melta-cone', '--shot-test', '--shot-test-turn', '90', '--screenshot', "$media\classic-melta.png", '--screenshot-every', '0.25', '--quit-after', '32') + $more
& "$repo\tools\godot.ps1" @ga @ca *> $null
"classic client exited $LASTEXITCODE"
if ($botPid) { & taskkill.exe /PID $botPid /T /F | Out-Null }
if (-not $srv.HasExited) { & taskkill.exe /PID $srv.Id /T /F | Out-Null }
}
if (-not $Only -or $Only -eq 'sword') {
# 3. Classic: an Assault bot swings the power sword standing, with nobody in reach (--swing-test stand); the first chain's frames into the folder (the blade's glow and swing flare).
$srv = Start-QuietProcess -FilePath (Get-Command dotnet).Source -ArgumentList @("$repo\src\SmServer\bin\Release\net10.0\SmServer.dll", '--bind', '127.0.0.1', '--port', '27477', '--mode', 'annihilation', '--honor-side-requests') -WorkingDirectory $repo -StdOut "$sp\compat-$Tag-sword-server.log"
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline -and -not ((Get-Content "$sp\compat-$Tag-sword-server.log" -ErrorAction SilentlyContinue) -match 'smserver on UDP')) { Start-Sleep -Milliseconds 500 }
Start-Sleep 2
$ga = @('--windowed') + $compat + @('--log', "$sp\compat-$Tag-sword-client.log", '--')
$ca = @('--connect', '127.0.0.1:27477', '--no-sound', '--safe-mode', '--content', 'extended', '--bot', '--bot-full-load', '--side', 'marines', '--class', 'assault', '--melee', 'powersword', '--swing-test', 'stand', '--swing-shots', "$media\sword", '--swing-shot-every', '0.1', '--quit-after', '40') + $more
& "$repo\tools\godot.ps1" @ga @ca *> $null
"sword client exited $LASTEXITCODE"
if (-not $srv.HasExited) { & taskkill.exe /PID $srv.Id /T /F | Out-Null }
}
if (-not $Only -or $Only -eq 'librarian') {
# 4. Musou: the Librarian's scripted demo (the force sword's glow and flare); its frame sequences into the folder.
$srv = Start-QuietProcess -FilePath (Get-Command dotnet).Source -ArgumentList @("$repo\src\SmServer\bin\Release\net10.0\SmServer.dll", '--bind', '127.0.0.1', '--port', '27476', '--content', 'musou') -WorkingDirectory $repo -StdOut "$sp\compat-$Tag-librarian-server.log"
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline -and -not ((Get-Content "$sp\compat-$Tag-librarian-server.log" -ErrorAction SilentlyContinue) -match 'smserver on UDP')) { Start-Sleep -Milliseconds 500 }
Start-Sleep 2
$ga = @('--windowed') + $compat + @('--log', "$sp\compat-$Tag-librarian-client.log", '--')
$ca = @('--connect', '127.0.0.1:27476', '--content', 'musou', '--no-sound', '--safe-mode', '--librarian-demo', "$media\librarian", '--quit-after', '150') + $more
& "$repo\tools\godot.ps1" @ga @ca *> $null
"librarian client exited $LASTEXITCODE"
if (-not $srv.HasExited) { & taskkill.exe /PID $srv.Id /T /F | Out-Null }
}
Get-ChildItem $media -Recurse -File | Measure-Object | Select-Object Count
Get-Content "$sp\compat-$Tag-*-client.log" -ErrorAction SilentlyContinue | Select-String 'renderer:|TFWTEST|melee fx|flare' | Select-Object -First 8 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(200, $_.Line.Length)) }
"COMPATDONE" | Out-File "$sp\compat-$Tag.done"
