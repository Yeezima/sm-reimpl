<#
.SYNOPSIS
  Helper of the sm-reimpl start scripts (the numbered .cmd files of the playtest zip; Server.cmd of the server package). Writes only the bots' log files (-StartBots) and, if no message box can be shown, a note in %TEMP% (-Watch).
  -FindGame       prints the folder of your Steam install of Space Marine (app 55150), or nothing when it is not found.
  -AskAddress     asks for a server address (host or host:port) and prints it; an empty answer means this PC (127.0.0.1:27015); prints nothing if it looks wrong.
  -ShowAddresses  prints this PC's IPv4 network addresses (what friends on the same network type to join a server hosted here).
  -Watch          the early-close note (fix/hosting-bots-logs): the launcher starts the game itself, then this helper hidden; it finds the game it just
                  started (the process of -Exe, by its full path, started in the last 20 seconds; the newest) and, if it closes within 10 seconds of its start
                  with an exit code other than 0, a window says so and where its log is (%APPDATA%\sm-reimpl\logs\godot.log) to send with a bug report.
                  A blocked or failing PowerShell only loses the note, never the game. With --headless in SM_ENGINE_ARGS it only prints the note.
  -StartBots      (no launcher uses it since fix/hosting-bots-logs: hosting starts no bots; a manual test aid) waits until the server -ServerExe started just now
                  listens on UDP -Port, then starts -Count headless bots (default 0: none; game\sm-reimpl.console.exe --bot --no-sound: lean by themselves, no graphics,
                  effects or sounds, 30 frames a second), 3 s apart, the other side
                  first. Each bot writes its own log file in %APPDATA%\sm-reimpl\logs\bots (the 30 newest are kept), never the player's godot.log. It gives up,
                  starting none, when another program holds the port or the server does not come up within 5 minutes. The bots leave by themselves a few seconds
                  after the server stops.
#>
param([switch]$FindGame, [switch]$AskAddress, [switch]$ShowAddresses, [switch]$StartBots, [switch]$Watch, [string]$Exe = '', [int]$Port = 27015, [string]$ServerExe = '', [int]$Count = 0, [string]$BotExe = '', [string]$BotProject = '')
$ErrorActionPreference = 'SilentlyContinue'

if ($Watch) {
    $began = Get-Date
    $log = Join-Path $env:APPDATA 'sm-reimpl\logs\godot.log'
    $headless = "$env:SM_ENGINE_ARGS" -match '(^|\s)--headless(\s|$)'
    try { $exePath = [IO.Path]::GetFullPath($Exe) } catch { exit 0 }
    $name = [IO.Path]::GetFileNameWithoutExtension($exePath)
    # The game the launcher started just before this helper: by its full path (string compare, so [ ] & and spaces in folder names do not matter), newest first.
    $p = $null
    for ($i = 0; $i -lt 20 -and -not $p; $i++) {
        $p = Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object {
            try { $_.Path -and ([IO.Path]::GetFullPath($_.Path) -ieq $exePath) -and $_.StartTime -ge $began.AddSeconds(-20) } catch { $false }
        } | Sort-Object StartTime -Descending | Select-Object -First 1
        if (-not $p) { Start-Sleep -Milliseconds 250 }
    }
    if (-not $p) { exit 0 }   # not found (already gone or never seen): no note rather than a wrong one
    $null = $p.Handle   # keeps the exit code readable
    $left = 10000 - [int]((Get-Date) - $p.StartTime).TotalMilliseconds
    if ($left -gt 0 -and -not $p.WaitForExit($left)) { exit 0 }
    if (-not $p.HasExited) { exit 0 }
    $code = $null
    try { $code = $p.ExitCode } catch { }
    if ($code -eq 0) { exit 0 }   # a normal quick quit
    $codeText = if ($null -ne $code) { " (exit code $code)" } else { '' }
    $text = "The game closed right after starting$codeText.`n`nIts log is:`n$log`n`nPlease send that file with your bug report (zip it if it is large; the folder also holds the earlier godot*.log files)."
    if ($env:SM_WATCH_LOG) { Add-Content -LiteralPath $env:SM_WATCH_LOG -Value "NOTE: $text" }   # test aid: where a headless test reads the note
    if ($headless) { [Console]::Error.WriteLine($text); exit 1 }
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $answer = [System.Windows.Forms.MessageBox]::Show($text + "`n`nOpen the log folder now?", 'sm-reimpl: the game closed', [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Warning)
        if ($answer -eq [System.Windows.Forms.DialogResult]::Yes) {
            if (Test-Path -LiteralPath $log) { Start-Process -FilePath 'explorer.exe' -ArgumentList "/select,`"$log`"" }
            elseif (Test-Path -LiteralPath (Split-Path -Parent $log)) { Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$(Split-Path -Parent $log)`"" }
        }
    } catch {
        # No message box here: a console window with the same text instead.
        $msg = Join-Path $env:TEMP 'sm-reimpl-closed.txt'
        Set-Content -LiteralPath $msg -Value $text -Encoding UTF8
        Start-Process -FilePath 'cmd.exe' -ArgumentList "/c type `"$msg`" & echo. & pause"
    }
    exit 1
}

if ($StartBots) {
    if (-not $BotExe) { $BotExe = Join-Path $PSScriptRoot 'sm-reimpl.console.exe' }
    if (-not (Test-Path -LiteralPath $BotExe)) { Write-Host "Bots: $BotExe was not found; no bots."; exit 0 }
    $began = Get-Date
    $serverPath = [IO.Path]::GetFullPath($ServerExe)
    $ours = $false
    while (((Get-Date) - $began).TotalMinutes -lt 5) {
        $owner = Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($owner) {
            $p = Get-Process -Id $owner.OwningProcess -ErrorAction SilentlyContinue
            # Only the server started by this launcher (its path, started after this helper less a margin): never bots for another program or an older server.
            if ($p -and $p.Path -and ([IO.Path]::GetFullPath($p.Path) -ieq $serverPath) -and $p.StartTime -ge $began.AddSeconds(-10)) { $ours = $true; break }
            Write-Host "Bots: port $Port is held by another program; no bots."
            exit 0
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ours) { exit 0 }
    Start-Sleep -Seconds 3
    # The rules of the menu's BOTS (HostBots): the other side first, alternating; the classes in the playtest order.
    $marineClasses = @('tactical', 'devastator', 'assault'); $chaosClasses = @('chaos tactical', 'havoc', 'raptor', 'chaos tactical')
    $Count = [Math]::Max(0, [Math]::Min(15, $Count))
    $own = [Math]::Floor($Count / 2); $other = $Count - $own
    $plan = @()
    # The bots' own log files (never the player's godot.log, which Godot would rotate away): the 30 newest are kept.
    $botLogs = Join-Path $env:APPDATA 'sm-reimpl\logs\bots'
    $null = New-Item -ItemType Directory -Force -Path $botLogs
    for ($k = 0; $k -lt [Math]::Max($own, $other); $k++) {
        if ($k -lt $other) { $plan += ,@('chaos', $chaosClasses[$k % $chaosClasses.Count]) }
        if ($k -lt $own) { $plan += ,@('marines', $marineClasses[$k % $marineClasses.Count]) }
    }
    if ($plan.Count -eq 0) { Write-Host 'Bots: none asked for (-Count 0).'; exit 0 }
    Write-Host "Bots: starting $($plan.Count) bots on 127.0.0.1:$Port ($own Space Marines, $other Chaos), 3 seconds apart."
    foreach ($b in $plan) {
        $alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
        if (-not $alive) { break }
        $botArgs = @('--headless')
        if ($BotProject) { $botArgs += @('--path', "`"$BotProject`"") }   # test aid: the Godot editor binary with the repository's client project
        Get-ChildItem -LiteralPath $botLogs -Filter 'bot-*.log' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -Skip 29 | Remove-Item -Force
        $n = $plan.IndexOf($b) + 1
        $botLog = Join-Path $botLogs ("bot-$n-$($b[0])-$((Get-Date).ToString('yyyyMMdd-HHmmss'))-$PID.log")
        $botArgs += @('--log-file', "`"$botLog`"")
        $botArgs += @('--', '--connect', "127.0.0.1:$Port", '--bot', '--no-sound', '--no-hud', '--side', $b[0], '--class', "`"$($b[1])`"")
        # No console window at all (CREATE_NO_WINDOW, as the game's own hosted bots): nothing appears and nothing takes the focus.
        $si = New-Object System.Diagnostics.ProcessStartInfo $BotExe
        $si.Arguments = $botArgs -join ' '; $si.WorkingDirectory = Split-Path -Parent $BotExe; $si.UseShellExecute = $false; $si.CreateNoWindow = $true
        $null = [System.Diagnostics.Process]::Start($si)
        Start-Sleep -Seconds 3
    }
    exit 0
}

if ($FindGame) {
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($k in @(@('HKCU:\Software\Valve\Steam', 'SteamPath'), @('HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath'), @('HKLM:\SOFTWARE\Valve\Steam', 'InstallPath'))) {
        try { $v = (Get-ItemProperty -Path $k[0] -Name $k[1] -ErrorAction Stop).($k[1]); if ($v) { $roots.Add(($v -replace '/', '\')) } } catch { }
    }
    foreach ($d in @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")) { if ($d -and (Test-Path -LiteralPath $d)) { $roots.Add($d) } }
    $libs = New-Object System.Collections.Generic.List[string]
    foreach ($root in ($roots | Select-Object -Unique)) {
        $libs.Add($root)
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs.Add(($m.Groups[1].Value -replace '\\\\', '\')) }
        }
    }
    foreach ($lib in ($libs | Select-Object -Unique)) {
        $acf = Join-Path $lib 'steamapps\appmanifest_55150.acf'
        if (-not (Test-Path -LiteralPath $acf)) { continue }
        $m = [regex]::Match((Get-Content -LiteralPath $acf -Raw), '"installdir"\s+"([^"]*)"')
        if (-not $m.Success) { continue }
        $dir = Join-Path $lib ('steamapps\common\' + $m.Groups[1].Value)
        if (Test-Path -LiteralPath (Join-Path $dir 'data')) { Write-Output $dir; break }
    }
    exit 0
}

if ($AskAddress) {
    [Console]::Error.Write('Server address (host or host:port, e.g. 192.168.1.20:27015; just Enter = this PC, 127.0.0.1:27015): ')
    $line = [Console]::In.ReadLine()
    if ($null -ne $line) { $line = $line.Trim() }
    if ($null -ne $line -and $line -eq '') { $line = '127.0.0.1' }
    $okPort = $true
    if ($line -match ':([0-9]{1,5})$') { $okPort = ([int]$Matches[1] -ge 1 -and [int]$Matches[1] -le 65535) }
    if ($line -and $okPort -and $line -match '^[A-Za-z0-9.\-]{1,253}(:[0-9]{1,5})?$') {
        if ($line -notmatch ':') { $line = $line + ':27015' }
        Write-Output $line
    } elseif ($line -and -not $okPort) {
        [Console]::Error.WriteLine('The port must be a number from 1 to 65535 (the default is 27015).')
    } elseif ($line) {
        [Console]::Error.WriteLine('That does not look like host or host:port.')
    }
    exit 0
}

if ($ShowAddresses) {
    $found = $false
    foreach ($nic in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($nic.OperationalStatus -ne 'Up' -or $nic.NetworkInterfaceType -eq 'Loopback') { continue }
        foreach ($u in $nic.GetIPProperties().UnicastAddresses) {
            $a = $u.Address
            if ($a.AddressFamily -ne 'InterNetwork' -or $a.ToString().StartsWith('169.254.')) { continue }
            Write-Output ('    ' + $a.ToString() + '    (' + $nic.Name + ')')
            $found = $true
        }
    }
    if (-not $found) { Write-Output '    (no network address found: is this PC connected to a network?)' }
    exit 0
}
