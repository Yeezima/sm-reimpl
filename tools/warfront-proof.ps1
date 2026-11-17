<#
.SYNOPSIS
  Warfront proof runs (W17): one switch per run, each bounded by a time limit, every server and client it starts recorded by PID and
  stopped by PID in a finally. Test aid, never shipped. Binds 127.0.0.1 only; starts Godot only through tools\godot.ps1 (headless unless
  -Windowed, which uses the private hidden desktop); kills nothing it did not start.
.DESCRIPTION
  warfront-proof.ps1 -Run r1|r2|r3|r4|r5|r6|r7|r8|r9 [-Port p] [-Seconds s] [-Seed n] [-Out dir] [-Windowed] [-Tag name]

  Every run writes into <Out>\<run>\ : server.log, one log per client, run.json (a copy of the run folder's run.json at the end), the private
  run folder (wf\) and summary.txt (the lines the report cites). Defaults: Out = D:\sm-reimpl\media\warfront\w17 (never in the repo).
  Aids used are named in each run's header below and printed in summary.txt as "AIDED:" lines; nothing aided is reported as played.

  r1  solo win: one bot client (--bot --warfront-bot), server --warfront-max-humans 1, SM_TFW_WIN_TEST (enemies near the human fall, so the
      bot's walk and the capture rules stay real). Three nodes of lane A, then the fortress, the Warboss, the scoreboard, New campaign.
      Seed forced by SM_WF_SEED; the lane rolls are the real (hashed) ones.
  Further runs are added by the later switches of this file.
.NOTES
  Bounded: each run ends when its pass line appears, its failure line appears or -Seconds elapse. Nothing is retried into a pass.
#>
param(
    [Parameter(Mandatory)][ValidateSet('r1', 'r1new', 'r2', 'slots', 'r3', 'r4', 'r5', 'r6', 'r7', 'r8', 'r9', 'fortress-stuck', 'lane-solo')][string]$Run,
    [int]$Port = 27431,
    [int]$Seconds = 900,
    [string]$Seed = '12345',
    [string]$Out = 'D:\sm-reimpl\media\warfront\w17',
    [switch]$Windowed,
    [string]$Tag = '',
    [string]$ServerEnv = '',      # K=V;K=V added to the server's environment
    [string]$ClientArgs = ''      # added to every client
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'quiet-launch.ps1')

$dir = Join-Path $Out ($Run + $(if ($Tag) { "-$Tag" } else { '' }))
Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$wf = Join-Path $dir 'wf'
$serverLog = Join-Path $dir 'server.log'
$serverDll = Join-Path $RepoRoot 'src\SmServer\bin\Release\net10.0\SmServer.dll'
if (-not (Test-Path $serverDll)) { throw "build the server first: dotnet build src/SmServer -c Release ($serverDll missing)" }

$script:servers = @(); $script:clients = @{}   # name -> pid
$aided = [Collections.Generic.List[string]]::new()

function Start-WfServer([string[]]$extra, [hashtable]$env = @{}, [string]$logName = 'server.log') {
    $script:serverLog = Join-Path $dir $logName
    $env['SM_WF_DIR'] = $wf; $env['SM_WF_SEED'] = $Seed; $env['SM_TICK_STATS'] = '10'
    foreach ($kv in ($ServerEnv -split ';' | Where-Object { $_ -match '=' })) { $k, $v = $kv -split '=', 2; $env[$k] = $v }
    foreach ($k in $env.Keys) { Set-Item -Path "Env:$k" -Value $env[$k]; if ($k -like 'SM_*' -and $k -notin 'SM_WF_DIR', 'SM_WF_SEED', 'SM_TICK_STATS') { $aided.Add("$k=$($env[$k])") } }
    $a = @($serverDll, '--content', 'warfront', '--bind', '127.0.0.1', '--port', "$Port") + $extra
    $p = Start-QuietProcess -FilePath (Get-Command dotnet).Source -ArgumentList $a -WorkingDirectory $RepoRoot -StdOut $serverLog
    $script:servers += $p.Id; $script:serverProc = $p
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline -and -not $p.HasExited) {
        if ((Get-Content -LiteralPath $serverLog -ErrorAction SilentlyContinue) -match 'listening|Listening|bound|port') { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2
    if ($p.HasExited) { throw "the server did not start on 127.0.0.1:$Port (see $serverLog)" }
    return $p
}

function Start-WfClient([string]$name, [string[]]$extra, [switch]$win) {
    $log = Join-Path $dir "client-$name.log"
    $g = @(); if ($win) { $g += '--windowed' }
    $g += @('--background', '--log', $log, '--', '--connect', "127.0.0.1:$Port", '--content', 'warfront', '--no-sound',
            '--warfront-key-file', (Join-Path $dir "key-$name.json"), '--quit-after', "$($Seconds + 60)") + $extra
    if ($ClientArgs) { $g += ($ClientArgs -split ' ' | Where-Object { $_ }) }
    $o = & (Join-Path $PSScriptRoot 'godot.ps1') @g 6>&1
    $m = [regex]::Match(($o -join ' '), 'started pid (\d+)')
    if (-not $m.Success) { throw "client $name did not start: $o" }
    $script:clients[$name] = [int]$m.Groups[1].Value
    return $log
}

function Stop-WfClient([string]$name) {
    if ($script:clients.ContainsKey($name)) { cmd.exe /c "taskkill.exe /PID $($script:clients[$name]) /T /F >nul 2>&1"; $script:clients.Remove($name) }
}

function Stop-WfServer {
    foreach ($id in $script:servers) { cmd.exe /c "taskkill.exe /PID $id /T /F >nul 2>&1" }
    $script:servers = @()
    Start-Sleep -Seconds 2
}

function Set-WfRun([scriptblock]$edit) {
    $f = Join-Path $wf 'run.json'
    $j = Get-Content -LiteralPath $f -Raw | ConvertFrom-Json
    & $edit $j
    $j | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $f -Encoding utf8
}

function Stop-All {
    foreach ($n in @($script:clients.Keys)) { Stop-WfClient $n }
    foreach ($id in $script:servers) { cmd.exe /c "taskkill.exe /PID $id /T /F >nul 2>&1" }
    $script:servers = @()
}

function Wait-Log([string]$file, [string]$pattern, [int]$timeout, [string]$failPattern = '') {
    $until = (Get-Date).AddSeconds($timeout)
    while ((Get-Date) -lt $until) {
        $t = Get-Content -LiteralPath $file -Raw -ErrorAction SilentlyContinue
        if ($t -and $t -match $pattern) { return $true }
        if ($failPattern -and $t -and $t -match $failPattern) { return $false }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Write-Summary([string[]]$lines) {
    $sum = @("run $Run at $(Get-Date -Format s) seed $Seed port $Port limit $Seconds s")
    foreach ($a in $aided) { $sum += "AIDED: $a" }
    $sum += $lines
    $sum | Set-Content -LiteralPath (Join-Path $dir 'summary.txt') -Encoding utf8
    foreach ($f in 'run.json', 'squads.json') { $src = Join-Path $wf $f; if (Test-Path $src) { Copy-Item $src (Join-Path $dir "$f.copy") -Force } }
    $sum | Write-Host
}

function Grep-Log([string]$file, [string]$pattern, [int]$max = 400) {
    if (-not (Test-Path $file)) { return @() }
    return @(Select-String -LiteralPath $file -Pattern $pattern | Select-Object -First $max | ForEach-Object { $_.Line })
}

try {
    switch ($Run) {
        'r1' {
            $null = Start-WfServer @('--warfront-max-humans', '1', '--warfront-new') @{ SM_TFW_WIN_TEST = '80'; SM_TFW_KEEP_HUMAN = '1'; SM_WF_LANE_WALK = '6'; SM_WF_FORTRESS_WALK = '8' }
            $log = Start-WfClient 'solo' @('--bot', '--warfront-bot', '--warfront-bot-pick', 'last')
            $ok = Wait-Log $serverLog 'the run is over|archived as' $Seconds
            $lines = @("passed-marker-seen: $ok")
            $lines += Grep-Log $serverLog 'warfront:' 600
            Write-Summary $lines
        }
        'r2' {
            # four players, a wipe on a normal node (the wipe is aided: SM_WF_RESOLVE=wipe ends the battle 5 s after it began); the other lanes' rolls are forced L then W
            $null = Start-WfServer @('--warfront-max-humans', '4', '--warfront-new') @{ SM_WF_RESOLVE = 'wipe'; SM_WF_START = 'a2,b2,c2'; SM_WF_ROLLS = 'LW' }
            foreach ($n in 'p1', 'p2', 'p3', 'p4') { $null = Start-WfClient $n @('--bot', '--warfront-bot', '--warfront-deploy-after', '25'); Start-Sleep -Seconds 4 }
            $ok = Wait-Log $serverLog 'ends as a wipe' $Seconds
            Start-Sleep -Seconds 15
            $lines = @("wipe-seen: $ok")
            $lines += Grep-Log $serverLog 'warfront:.*(identified|deploy|landing on|battle begins|wipe|roll|front|lane|ship)' 120
            Write-Summary $lines
        }
        'slots' {
            # four clients join every slot: deployments in order a1 a2 a3 b1 b2 b3 c1 c2 c3 (pick first; every battle won by SM_WF_RESOLVE=win, every roll held)
            $null = Start-WfServer @('--warfront-max-humans', '4', '--warfront-new') @{ SM_WF_RESOLVE = 'win'; SM_WF_ROLLS = ('W' * 40) }
            foreach ($n in 'p1', 'p2', 'p3', 'p4') { $null = Start-WfClient $n @('--bot', '--warfront-bot', '--warfront-deploy-after', '25'); Start-Sleep -Seconds 4 }
            $ok = Wait-Log $serverLog 'landing on wf_c3' $Seconds
            Start-Sleep -Seconds 30
            $lines = @("wf_c3-reached: $ok")
            $lines += Grep-Log $serverLog 'warfront:.*(landing on|identified|FAULT|tick error)' 200
            foreach ($n in 'p1', 'p2', 'p3', 'p4') { $lines += Grep-Log (Join-Path $dir "client-$n.log") 'warm wf_|read from|generated|mismatch|refus|travel window|frame wf_' 60 }
            Write-Summary $lines
        }
        { $_ -in 'r3', 'r4' } {
            # HQ defence. Phase 1 registers four players on the ship (nobody deploys); the run is then put into the breakthrough state by editing run.json (AIDED: every lane advanced and at node 0, HQ mandatory),
            # phase 2 starts the server again and the leader's bot deploys the only target. r3: survive 8 minutes (SM_TFW_KEEP_HUMAN keeps the humans standing: AIDED); r4: SM_WF_RESOLVE=wipe ends it 5 s in (AIDED).
            $null = Start-WfServer @('--warfront-max-humans', '4', '--warfront-new') @{} 'server-1.log'
            foreach ($n in 'p1', 'p2', 'p3', 'p4') { $null = Start-WfClient $n @('--bot', '--warfront-bot', '--warfront-deploy-after', '9999'); Start-Sleep -Seconds 4 }
            $ok1 = Wait-Log $serverLog '(?s)identified.*identified.*identified.*identified' 120
            Stop-All; Start-Sleep -Seconds 3
            Set-WfRun { param($j) $j.HqMandatory = $true; $j.Fronts = @(0, 0, 0); $j.Advanced = @($true, $true, $true) }
            $aided.Add('run.json edited: Fronts 0,0,0, Advanced true x3, HqMandatory true (the breakthrough state)')
            $env2 = @{}; if ($Run -eq 'r3') { $env2['SM_TFW_KEEP_HUMAN'] = '1' } else { $env2['SM_WF_RESOLVE'] = 'wipe' }
            $null = Start-WfServer @('--warfront-max-humans', '4') $env2 'server-2.log'
            foreach ($n in 'p1', 'p2', 'p3', 'p4') { $null = Start-WfClient $n @('--bot', '--warfront-bot'); Start-Sleep -Seconds 4 }
            $ok = Wait-Log $serverLog 'battle \d+ ended|the run is over' $Seconds
            Start-Sleep -Seconds 20
            $lines = @("four-identified: $ok1", "battle-ended-seen: $ok")
            $lines += Grep-Log (Join-Path $dir 'server-2.log') 'warfront:.*(identified|landing|battle|HQ|run is over|archived|exported|FAULT|tick error)' 150
            Write-Summary $lines
            foreach ($f in Get-ChildItem (Join-Path $wf 'records') -ErrorAction SilentlyContinue) { Copy-Item $f.FullName (Join-Path $dir $f.Name) }
            foreach ($f in Get-ChildItem (Join-Path $wf 'exports') -ErrorAction SilentlyContinue) { Copy-Item $f.FullName (Join-Path $dir $f.Name) }
        }
        'r5' {
            # leadership: four clients, p1 first. p1 quits on the ship and returns; p1 quits in a battle (SM_TFW_KEEP_HUMAN, no battle aid: the battle stays open) and returns.
            $null = Start-WfServer @('--warfront-max-humans', '4', '--warfront-new') @{ SM_TFW_KEEP_HUMAN = '1' }
            foreach ($n in 'p1', 'p2', 'p3', 'p4') { $null = Start-WfClient $n @('--bot', '--warfront-bot', '--warfront-deploy-after', '75'); Start-Sleep -Seconds 4 }
            $r = [ordered]@{}
            $r.four = Wait-Log $serverLog '(?s)identified.*identified.*identified.*identified' 120
            Start-Sleep -Seconds 10
            Stop-WfClient 'p1'                                   # the leader quits on the ship
            $r.shipLeft = Wait-Log $serverLog 'left; leader is now' 30
            Start-Sleep -Seconds 12
            $null = Start-WfClient 'p1' @('--bot', '--warfront-bot', '--warfront-deploy-after', '75')
            $r.shipBack = Wait-Log $serverLog 'identified as a returning player' 60
            $r.battle = Wait-Log $serverLog 'battle begins' 240
            Start-Sleep -Seconds 20
            Stop-WfClient 'p1'                                   # the leader quits in the battle
            Start-Sleep -Seconds 15
            $null = Start-WfClient 'p1' @('--bot', '--warfront-bot', '--warfront-deploy-after', '75')
            Start-Sleep -Seconds 40
            $lines = @($r.GetEnumerator() | ForEach-Object { "$($_.Key): $($_.Value)" })
            $lines += Grep-Log $serverLog 'warfront:.*(identified|left;|deploy|landing on|battle begins|returns|flown|refused|leader)' 150
            Write-Summary $lines
        }
        'r6' {
            # new joiner: p1..p3 fight a lane battle (aided walk and win radius so it ends), p4 (a key the run has never seen) joins 60 s into the battle
            $null = Start-WfServer @('--warfront-max-humans', '4', '--warfront-new') @{ SM_TFW_WIN_TEST = '170'; SM_TFW_KEEP_HUMAN = '1'; SM_WF_LANE_WALK = '6'; SM_WF_START = 'a1,b1,c1' }
            foreach ($n in 'p1', 'p2', 'p3') { $null = Start-WfClient $n @('--bot', '--warfront-bot', '--warfront-deploy-after', '25'); Start-Sleep -Seconds 4 }
            $r = [ordered]@{}
            $r.battle = Wait-Log $serverLog 'battle begins' 240
            Start-Sleep -Seconds 60
            $null = Start-WfClient 'p4' @('--bot', '--warfront-bot', '--warfront-deploy-after', '25')
            $r.won = Wait-Log $serverLog 'battle 1 ended' $Seconds
            Start-Sleep -Seconds 60
            Stop-WfClient 'p4'                                   # leave and rejoin aboard: nothing granted twice
            Start-Sleep -Seconds 8
            $null = Start-WfClient 'p4' @('--bot', '--warfront-bot', '--warfront-deploy-after', '25')
            Start-Sleep -Seconds 45
            $lines = @($r.GetEnumerator() | ForEach-Object { "$($_.Key): $($_.Value)" })
            $lines += Grep-Log $serverLog 'warfront:.*(identified|joiner|spectat|median|catch|customiz|ConfirmClass|offer|grant|deploy|battle|returns)' 200
            Write-Summary $lines
        }
        'r7' {
            # returning player: p1 and p2 fight; p2 quits in the battle and on the ship and rejoins; run.json copies are taken before and after each quit
            $null = Start-WfServer @('--warfront-max-humans', '2', '--warfront-new') @{ SM_TFW_WIN_TEST = '170'; SM_TFW_KEEP_HUMAN = '1'; SM_WF_LANE_WALK = '6'; SM_WF_START = 'a1,b1,c1' }
            foreach ($n in 'p1', 'p2') { $null = Start-WfClient $n @('--bot', '--warfront-bot', '--warfront-deploy-after', '25'); Start-Sleep -Seconds 4 }
            $r = [ordered]@{}
            $r.battle = Wait-Log $serverLog 'battle begins' 240
            Start-Sleep -Seconds 50
            Copy-Item (Join-Path $wf 'run.json') (Join-Path $dir 'run-before-battle-quit.json') -ErrorAction SilentlyContinue
            Stop-WfClient 'p2'; Start-Sleep -Seconds 6
            Copy-Item (Join-Path $wf 'run.json') (Join-Path $dir 'run-after-battle-quit.json') -ErrorAction SilentlyContinue
            $null = Start-WfClient 'p2' @('--bot', '--warfront-bot', '--warfront-deploy-after', '25')
            $r.backInBattle = Wait-Log $serverLog 'identified as a returning player' 90
            $r.won = Wait-Log $serverLog 'battle 1 ended' $Seconds
            Start-Sleep -Seconds 20
            Copy-Item (Join-Path $wf 'run.json') (Join-Path $dir 'run-before-ship-quit.json') -ErrorAction SilentlyContinue
            Stop-WfClient 'p2'; Start-Sleep -Seconds 6
            $null = Start-WfClient 'p2' @('--bot', '--warfront-bot', '--warfront-deploy-after', '300')
            Start-Sleep -Seconds 25
            Copy-Item (Join-Path $wf 'run.json') (Join-Path $dir 'run-after-ship-rejoin.json') -ErrorAction SilentlyContinue
            $lines = @($r.GetEnumerator() | ForEach-Object { "$($_.Key): $($_.Value)" })
            $lines += Grep-Log $serverLog 'warfront:.*(identified|left;|returns|flown|squads|joiner|grant|battle)' 150
            Write-Summary $lines
        }
        'r8' {
            # save and resume: the whole server (supervisor and its stage child, by the supervisor's PID) is killed on the ship and in a battle and started again on the same run folder (a stage child killed alone
            # ends the supervisor by design: the restart is the server's); clients are started again with their own key files
            $null = Start-WfServer @('--warfront-max-humans', '1', '--warfront-new') @{ SM_TFW_KEEP_HUMAN = '1' } 'server-1.log'
            $null = Start-WfClient 'p1' @('--bot', '--warfront-bot', '--warfront-deploy-after', '9999')
            $r = [ordered]@{}
            $r.ship = Wait-Log $serverLog 'identified as a new player' 120
            Start-Sleep -Seconds 8
            Copy-Item (Join-Path $wf 'run.json') (Join-Path $dir 'run-1-ship-before-kill.json')
            Stop-WfClient 'p1'; Stop-WfServer                                  # the kill on the ship
            $null = Start-WfServer @('--warfront-max-humans', '1') @{ SM_TFW_KEEP_HUMAN = '1' } 'server-2.log'
            $null = Start-WfClient 'p1' @('--bot', '--warfront-bot', '--warfront-deploy-after', '25')
            $r.shipResumed = Wait-Log $serverLog 'run loaded: Ship, deployment 0' 60
            $r.shipIdentified = Wait-Log $serverLog 'identified as a returning player' 60
            Copy-Item (Join-Path $wf 'run.json') (Join-Path $dir 'run-2-ship-after-restart.json')
            $r.battle = Wait-Log $serverLog 'battle begins' 240
            Start-Sleep -Seconds 15
            Copy-Item (Join-Path $wf 'run.json') (Join-Path $dir 'run-3-battle-before-kill.json')
            Stop-WfClient 'p1'; Stop-WfServer                                  # the kill in the battle
            $null = Start-WfServer @('--warfront-max-humans', '1') @{ SM_TFW_KEEP_HUMAN = '1' } 'server-3.log'
            $null = Start-WfClient 'p1' @('--bot', '--warfront-bot', '--warfront-deploy-after', '9999')
            $r.battleResumedOnShip = Wait-Log $serverLog 'run loaded: Ship, deployment 0' 60
            $r.battleIdentified = Wait-Log $serverLog 'identified as a returning player' 60
            Copy-Item (Join-Path $wf 'run.json') (Join-Path $dir 'run-4-after-battle-kill.json')
            $lines = @($r.GetEnumerator() | ForEach-Object { "$($_.Key): $($_.Value)" })
            foreach ($n in 1, 2, 3) { $lines += Grep-Log (Join-Path $dir "server-$n.log") 'warfront:.*(run loaded|stage|battle|resum|roll|ended|arrival|identified|squads)' 40 }
            Write-Summary $lines
        }
        'r9' {
            # fallen human (nine retainers by SM_WF_ROSTERS, so some stand): SM_WF_FALL_TEST makes the leader fall far from his squad 20 s into a battle; the squad's Marines revive only within 30 m, so nobody reaches him in the 25 s window
            $null = Start-WfServer @('--warfront-max-humans', '1', '--warfront-new') @{ SM_WF_FALL_TEST = '20'; SM_WF_ROSTERS = '9' }
            $null = Start-WfClient 'p1' @('--bot', '--warfront-bot', '--warfront-deploy-after', '8')
            $r = [ordered]@{}
            $r.fell = Wait-Log $serverLog 'SM_WF_FALL_TEST: P\d+ falls' 240
            $r.spectates = Wait-Log $serverLog 'spectates: default watch' 60
            $r.back = Wait-Log $serverLog 'is back in the fight' 150
            $lines = @($r.GetEnumerator() | ForEach-Object { "$($_.Key): $($_.Value)" })
            $lines += Grep-Log $serverLog 'warfront:.*(FALL_TEST|spectat|back in the fight|down|revive)' 80
            Write-Summary $lines
        }
        'r1new' {
            # after r1's win: the front end's NEW CAMPAIGN path (the server started with --warfront-new on the same folder, whose records\ holds the won run) and the same client key
            $src = Join-Path $Out 'r1'
            Copy-Item -LiteralPath (Join-Path $src 'wf') -Destination $wf -Recurse
            $null = Start-WfServer @('--warfront-max-humans', '1', '--warfront-new') @{}
            Copy-Item (Join-Path $src 'key-solo.json') (Join-Path $dir 'key-solo.json')
            $null = Start-WfClient 'solo' @('--bot', '--warfront-bot', '--warfront-deploy-after', '9999')
            $ok = Wait-Log $serverLog 'identified as a (new|returning) player' 90
            Start-Sleep -Seconds 5
            $lines = @("identified: $ok")
            $lines += Grep-Log $serverLog 'warfront:.*(new run|run loaded|identified|squads|records|archived)' 20
            Write-Summary $lines
        }
        default { throw "run $Run is not in this version of the script" }
    }
}
finally { Stop-All }
