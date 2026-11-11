<#
.SYNOPSIS
  Graphify (code graph / change impact) for sm-reimpl: setup, index, serve (MCP over stdio) and pass-through queries.
  Dev tool only: nothing here is shipped. See docs/commands.md, "Graphify (code graph)".
.DESCRIPTION
  graphify.cmd setup                      create or repair the pinned venv under %LOCALAPPDATA%\sm-reimpl\tools\graphify-venv; prints the version
  graphify.cmd index                      incremental code-only update of this checkout into graphify-out\ (no clustering); prints the timing
  graphify.cmd serve                      stdio MCP server (stdout is the MCP channel; warnings go to stderr)
  graphify.cmd explain|affected|path|query ...   pass-through to the graphify CLI against this checkout's index

  Every graphify process runs with a CLEAN environment (PATH to the venv and system32, SYSTEMROOT, USERPROFILE, TEMP, TMP,
  LOCALAPPDATA) plus GRAPHIFY_BACKEND=none, so no LLM backend or key from the user's environment can ever be picked up.
  SM_GRAPHIFY_VENV overrides the venv folder (tests of the failure path only).
#>
param([Parameter(Position = 0)][string]$Command = '', [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest)
$ErrorActionPreference = 'Stop'
$Pin = '0.9.106'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$venv = $env:SM_GRAPHIFY_VENV
if (-not $venv) { $venv = Join-Path $env:LOCALAPPDATA 'sm-reimpl\tools\graphify-venv' }
$py = Join-Path $venv 'Scripts\python.exe'
$gexe = Join-Path $venv 'Scripts\graphify.exe'
$scripts = Join-Path $venv 'Scripts'
$outDir = Join-Path $repo 'graphify-out'

function Err([string]$m) { [Console]::Error.WriteLine($m) }

function Invoke-Clean([string]$exe, [string[]]$arguments) {
  # Inherited stdio (no redirection), so MCP bytes pass through untouched.
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $exe
  $psi.Arguments = ($arguments | ForEach-Object { if ($_ -match '[\s"]' -or $_ -eq '') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
  $psi.WorkingDirectory = $repo
  $psi.UseShellExecute = $false
  $psi.Environment.Clear()
  $sys = [Environment]::GetFolderPath('System')
  $psi.Environment['PATH'] = "$scripts;$sys"
  $psi.Environment['SYSTEMROOT'] = $env:SystemRoot
  $psi.Environment['USERPROFILE'] = $env:USERPROFILE
  $psi.Environment['TEMP'] = $env:TEMP
  $psi.Environment['TMP'] = $env:TEMP
  $psi.Environment['LOCALAPPDATA'] = $env:LOCALAPPDATA
  $psi.Environment['GRAPHIFY_BACKEND'] = 'none'
  $psi.Environment['GRAPHIFY_QUERY_LOG_DISABLE'] = '1'
  $psi.Environment['PYTHONIOENCODING'] = 'utf-8'
  $p = [System.Diagnostics.Process]::Start($psi)
  $p.WaitForExit()
  return $p.ExitCode
}

function Git-Out([string[]]$a) {
  $o = & git -C $repo @a 2>$null
  if ($LASTEXITCODE -ne 0) { return $null }
  return ($o | Select-Object -First 1)
}

function Need-Venv {
  if (-not (Test-Path $gexe) -or -not (Test-Path $py)) {
    Err "graphify: venv not found at '$venv'. Run: graphify.cmd setup"
    exit 2
  }
}

# The index to use: this checkout's, else the main checkout's (a worktree's common dir sits in the main checkout).
function Find-Graph {
  $own = Join-Path $outDir 'graph.json'
  if (Test-Path $own) { return $own }
  $common = Git-Out @('rev-parse', '--path-format=absolute', '--git-common-dir')
  if ($common) {
    $main = Split-Path $common -Parent
    $g = Join-Path $main 'graphify-out\graph.json'
    if (Test-Path $g) { return $g }
  }
  return $null
}

switch -Regex ($Command) {
  '^setup$' {
    if (-not (Test-Path $py)) {
      $base = $null
      foreach ($c in @('py', 'python')) { if (Get-Command $c -ErrorAction SilentlyContinue) { $base = $c; break } }
      if (-not $base) { Err 'graphify setup: no Python 3 found (install Python 3.11+ and retry).'; exit 2 }
      New-Item -ItemType Directory -Force (Split-Path $venv -Parent) | Out-Null
      $a = @(); if ($base -eq 'py') { $a += '-3' }
      & $base @a -m venv $venv
      if ($LASTEXITCODE -ne 0) { Err 'graphify setup: venv creation failed.'; exit 2 }
    }
    $have = $null
    if (Test-Path $gexe) {
      $have = & $py -c "import importlib.metadata as m; print(m.version('graphifyy'))" 2>$null
    }
    if ($have -ne $Pin) {
      $rc = Invoke-Clean $py @('-m', 'pip', 'install', '--disable-pip-version-check', "graphifyy[mcp]==$Pin")
      if ($rc -ne 0) { Err 'graphify setup: pip install failed.'; exit $rc }
      $have = & $py -c "import importlib.metadata as m; print(m.version('graphifyy'))"
    }
    Write-Output "graphifyy $have (pinned $Pin) in $venv"
    if ($have -ne $Pin) { exit 1 }
  }
  '^index$' {
    Need-Venv
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $rc = Invoke-Clean $gexe @('update', '.', '--no-cluster')
    $sw.Stop()
    if ($rc -ne 0) { Err "graphify index failed (exit $rc)."; exit $rc }
    $head = Git-Out @('rev-parse', 'HEAD')
    New-Item -ItemType Directory -Force $outDir | Out-Null
    Set-Content -Path (Join-Path $outDir 'index-head.txt') -Value $head -Encoding ascii
    Write-Output ("graphify index: {0:N1} s, HEAD {1}" -f $sw.Elapsed.TotalSeconds, $head)
  }
  '^serve$' {
    Need-Venv
    $graph = Find-Graph
    if (-not $graph) { Err "graphify serve: no graphify-out\graph.json here or in the main checkout. Run: graphify.cmd index"; exit 2 }
    $stamp = Join-Path (Split-Path $graph -Parent) 'index-head.txt'
    $idx = if (Test-Path $stamp) { (Get-Content $stamp -TotalCount 1).Trim() } else { '' }
    $cur = Git-Out @('rev-parse', 'HEAD')
    if (-not $idx) { Err "graphify: index has no HEAD stamp (not made with graphify.cmd index); it may be stale. Index: $graph" }
    elseif ($cur -and $idx -ne $cur) { Err ("graphify: index is from {0} but this checkout is at {1}; run graphify.cmd index for current results. Index: {2}" -f $idx.Substring(0, [Math]::Min(8, $idx.Length)), $cur.Substring(0, 8), $graph) }
    exit (Invoke-Clean $py @('-m', 'graphify.serve', $graph))
  }
  '^(explain|affected|path|query)$' {
    Need-Venv
    $graph = Find-Graph
    if (-not $graph) { Err 'graphify: no index yet. Run: graphify.cmd index'; exit 2 }
    $a = @($Command) + $Rest
    if ($a -notcontains '--graph') { $a += @('--graph', $graph) }
    exit (Invoke-Clean $gexe $a)
  }
  default {
    Err 'usage: graphify.cmd setup | index | serve | explain|affected|path|query ...'
    exit 64
  }
}
