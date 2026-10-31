<#
.SYNOPSIS
  The one way for test runs, agents and scripts to start the Godot client (tools/no-focus-steal, owner's request of 2026-10-31): nothing
  ever appears on the owner's desktop or takes his focus. Test aid; read-only against the game install; never starts a server.
.DESCRIPTION
  godot.cmd [--windowed] [--background] [--log <file>] [--exe <path>] [--build] [<Godot engine options>] [-- <client options>]

  Headless (the default): adds --headless and runs Godot's console wrapper with no console window at all (CREATE_NO_WINDOW; its real
  Godot child inherits the window-less console), stdin from NUL, stdout and stderr into the log file.
      godot.cmd -- --connect 127.0.0.1:27181 --bot --no-sound --quit-after 30
      godot.cmd -- --ui-selftest --profile "%LOCALAPPDATA%\sm-reimpl\tmp-build\p.json"
  --windowed: a real window that renders (screenshots, recordings, the perk-card shot), started on the private desktop
  "sm-reimpl-test" of this window station: it is never on the owner's screen, never gets the foreground, never gets his mouse or
  keyboard. --test-window is added to the client options when missing.
      godot.cmd --windowed -- --connect 127.0.0.1:27181 --bot --screenshot "%LOCALAPPDATA%\sm-reimpl\cache\shots\a.png" --quit-after 20
      godot.cmd --windowed --write-movie "%LOCALAPPDATA%\sm-reimpl\cache\m.avi" --fixed-fps 30 -- --connect ... --quit-after 20
  --path <repo>\client is added unless --path or --main-pack is given or --exe names an exported game. Without --background the
  log is copied to the console as it grows and the exit code is Godot's. --background prints the process id and the log and returns
  (stop it with: taskkill /PID <id> /T /F, only processes you started). --exe runs another executable the same way (e.g. an exported
  game's sm-reimpl.console.exe). --build builds client\SmClient.csproj first. Default log: %LOCALAPPDATA%\sm-reimpl\tmp-build\godot\.
#>
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'quiet-launch.ps1')
$Name = 'Godot_v4.7.2-stable_mono_win64'
$Editor = Join-Path $env:LOCALAPPDATA "sm-reimpl\tools\godot\$Name\$($Name)_console.exe"
$TestDesktop = 'sm-reimpl-test'

$windowed = $false; $background = $false; $log = $null; $exe = $null; $build = $false
$engine = [Collections.Generic.List[string]]::new(); $user = $null
$all = @($args)
for ($i = 0; $i -lt $all.Count; $i++) {
    $a = [string]$all[$i]
    if ($null -ne $user) { $user.Add($a); continue }
    switch -Regex ($a) {
        '^--$'           { $user = [Collections.Generic.List[string]]::new() }
        '^--windowed$'   { $windowed = $true }
        '^--background$' { $background = $true }
        '^--build$'      { $build = $true }
        '^--log$'        { if ($i + 1 -ge $all.Count) { throw '--log needs a file' }; $log = [string]$all[++$i] }
        '^--exe$'        { if ($i + 1 -ge $all.Count) { throw '--exe needs a path' }; $exe = [string]$all[++$i] }
        '^(--help|-h|/\?)$' { (Get-Help $PSCommandPath).Description.Text | Write-Host; exit 0 }
        default          { $engine.Add($a) }
    }
}
if ($null -eq $user) { $user = [Collections.Generic.List[string]]::new() }

$editorRun = -not $exe
if (-not $exe) { $exe = $Editor }
if (-not (Test-Path -LiteralPath $exe)) { throw "$exe was not found$(if ($editorRun) { ': run client.cmd once (it downloads Godot 4.7.2)' })." }
$client = Join-Path $RepoRoot 'client'
if ($editorRun -and -not ($engine -contains '--path') -and -not ($engine -contains '--main-pack')) { $engine.InsertRange(0, [string[]]@('--path', $client)) }
if ($windowed) {
    if ($engine -contains '--headless') { throw '--windowed and --headless together: drop one' }
    if (-not ($user -contains '--test-window')) { $user.Add('--test-window') }
} elseif (-not ($engine -contains '--headless')) { $engine.Insert(0, '--headless') }

if ($build) {
    dotnet build (Join-Path $client 'SmClient.csproj') -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$state = Join-Path $env:LOCALAPPDATA 'sm-reimpl\tmp-build\godot'
New-Item -ItemType Directory -Force -Path $state | Out-Null
if ($editorRun -and -not (Test-Path -LiteralPath (Join-Path $client '.godot'))) {
    $ip = Start-QuietProcess -FilePath $exe -ArgumentList @('--headless', '--path', $client, '--import') -WorkingDirectory $RepoRoot -StdOut (Join-Path $state 'import.log')
    $null = Wait-QuietProcess $ip
}
if (-not $log) { $log = Join-Path $state ("run-{0}-{1}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID) }

$argv = @($engine) + @('--') + @($user)
$p = Start-QuietProcess -FilePath $exe -ArgumentList $argv -WorkingDirectory $(if ($editorRun) { $RepoRoot } else { Split-Path -Parent $exe }) `
    -StdOut $log -Desktop $(if ($windowed) { $TestDesktop } else { '' })
if ($background) {
    Write-Host ("started pid {0} ({1}); log {2}" -f $p.Id, $(if ($windowed) { "windowed on the private desktop $TestDesktop" } else { 'headless, no window' }), $log)
    Write-Host "stop it with: taskkill /PID $($p.Id) /T /F"
    exit 0
}
$code = Wait-QuietProcess $p -Log $log
exit $code
