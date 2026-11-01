<#
.SYNOPSIS
  Builds the tester release of sm-reimpl into %LOCALAPPDATA%\sm-reimpl\release\<version>\ (never inside the repository or the game install):
    SM-Reimpl-Playtest-<date>-<ver>-Windows.zip   THE tester download (the only asset of a release unless -ServerAssets), laid out for testers:
        0 - SM Reimpl Launcher.exe                  the launcher and updater (src/SmLauncher; self-contained single file): installs into its own
                                                    folder, keeps the build up to date from this repository's releases, starts Play / Host / Join
        1 - READ ME FIRST.txt
        2 - Play.cmd                                the front end (Join screen, HOST A MATCH, CUSTOMIZE)
        3 - Host a match on this PC.cmd             the bundled server, this PC only
        4 - Join a server by address.cmd            asks for host:port and joins
        5 - Host a match for friends (LAN).cmd      the bundled server on every interface
        game\     the Godot Windows export (self-contained .NET) and Launcher.ps1
        server\   the win64 dedicated server (self-contained)
        source\   sm-reimpl-source-<ver>.zip (the GPL source offer)
        notes\    VERSION.txt, LICENSE.txt, THIRD_PARTY_NOTICES.md, licenses\, changes-from-original.md, refine-register.md, playtest\, CONTENTS-SHA256.txt
    sm-reimpl-server-win64-<ver>.zip              dedicated server for server hosts, self-contained (published only with -ServerAssets)
    sm-reimpl-server-linux-x64-<ver>.tar.gz       the same for linux-x64 (server.sh; a tar.gz keeps the 0755 mode bits that a zip may lose); only with -Linux or -ServerAssets
    sm-reimpl-source-<ver>.zip                    git archive of the commit (also inside the playtest zip)
    SHA256SUMS.txt                                over all of them (local; the release description shows the hashes instead)
    RELEASE-NOTES.md                              the release description for testers (tools/release/package/RELEASE-NOTES.md, tokens filled)
  The game and the servers are built from a clean `git archive` copy of the commit (HEAD, or -Commit), so ignored local files under client/ can never
  reach the embedded pack. The content guard (smtool scan-package, built from this checkout) runs over every package, including the source zip inside
  the playtest zip, and deletes them all if it finds game data, private files, user paths, webhooks, tokens, personal e-mail addresses or a file type
  that does not belong; it fails closed (no game install indexed, no config/local.json, a secrets file that cannot be read, or a private folder that
  exists but cannot be read is an error; the private cache folder may be absent only when it truly does not exist). Any exit code of the scanner other
  than 0 is a refusal. Nothing is published unless you pass -Publish (see below). Godot is only ever run --headless; the test servers of -Smoke bind
  127.0.0.1 only.

  First use downloads (official godotengine GitHub release, SHA-512 checked against SHA512-SUMS.txt and a pinned value):
    Godot 4.7.2-stable .NET (about 117 MB, same pin as client.cmd) and the export templates (about 1.2 GB; only the Windows ones are
    kept) into %LOCALAPPDATA%\sm-reimpl\tools; the templates are installed where Godot looks (%APPDATA%\Godot\export_templates\4.7.2.stable.mono).
    The linux-x64 server needs the linux-x64 .NET runtime pack from NuGet on the first build.
.PARAMETER Commit        build the programs from this commit (any git revision) instead of HEAD; the release tooling (scripts, readmes, notes, the content
                         guard) still comes from this checkout. Used to re-package an already published build in a newer layout. The working tree's
                         state does not matter then (no -AllowDirty needed).
.PARAMETER AllowDirty    build from a tree with uncommitted changes (the version says -dirty; -Publish refuses such a version)
.PARAMETER PrepareOnly   only download and install Godot and the export templates, then stop
.PARAMETER SkipClient    do not build the playtest zip
.PARAMETER SkipServer    do not build the stand-alone server packages (the playtest zip always bundles its own win64 server)
.PARAMETER Linux         also build the linux-x64 server package (sm-reimpl-server-linux-x64-<ver>.tar.gz). Off by default: the playtest zip and its smoke
                         test need only the win64 server and the source zip, so a hotfix skips the Linux publish. -ServerAssets turns it on by itself.
.PARAMETER SkipBuild     do not rebuild: re-scan and (with -Publish) publish the packages already in the output folder of this version
.PARAMETER Smoke         after building: unpack the playtest zip and run it as a tester would, headless, on 127.0.0.1 only (ports 27141 to 27199):
                         "0 - SM Reimpl Launcher.exe --selftest" must check the unpacked build against its file list (no network, nothing written),
                         and its window must start (--offline --screenshot, drawn off-screen into the smoke folder),
                         "3 - Host a match on this PC.cmd" starts server\SmServer.exe, game\sm-reimpl.console.exe joins it as a bot, and
                         "2 - Play.cmd" starts game\sm-reimpl.exe, whose HOST A MATCH (auto) must find server\ and join it
.PARAMETER GameDir       the game install, for the guard (files identical to the install are refused) and the smoke test; default: found as client.cmd does
.PARAMETER Publish       OWNER ONLY. After a clean re-scan: gh release create <tag> --repo Yeezima/sm-reimpl-releases --draft --prerelease with the playtest
                         zip as the only asset and RELEASE-NOTES.md as the description (the zip's SHA-256 is in it).
                         <tag> is v<git describe> when git describe finds a tag, else tester-<commit date>-<short sha> (a bare hash).
                         Nothing is uploaded without this switch.
.PARAMETER ServerAssets  with -Publish: also upload the dedicated server packages and the source zip (their SHA-256 are added to the description)
.PARAMETER AllowOtherRepo  with -Publish: allow a -Repo other than Yeezima/sm-reimpl-releases (publishing is pinned to that repository otherwise)
.PARAMETER NoDraft       with -Publish: publish the release directly instead of a draft (it stays marked as a pre-release)
.PARAMETER DryRun        with -Publish (alias -WhatIf): run everything but print the gh command instead of running it.
                         With -Ship: run the preflight's read-only checks (failures are reported, not fatal), build nothing, and print the release
                         description (with a placeholder hash) and every gh and git command the ship would run.
.PARAMETER Ship          OWNER ONLY (ship.cmd). The whole release in one command: preflight (a clean main equal to origin/main, dotnet test and
                         refine-register --check unless -SkipTests, release/NEXT.md has tester-facing bullets, the version is not released yet),
                         build, content guard and smoke test, then gh release create as a published pre-release (-NotPrerelease: a normal release,
                         marked Latest) with "What's new" from release/NEXT.md at the top of the description; then release/NEXT.md is archived to
                         release/history/<version>.md and emptied in one commit on top of the shipped commit, pushed to origin/main only while
                         origin/main is still the shipped commit (-NoPush keeps it local). See docs/releasing.md.
.PARAMETER SkipTests     with -Ship: skip dotnet test and refine-register --check. Without it they are also skipped (and the ship says so) when both
                         passed on exactly this commit within the last 24 hours, as recorded by test-and-mark.cmd (or an earlier ship) in
                         %LOCALAPPDATA%\sm-reimpl\tmp-build\tested\<commit sha>; otherwise they run, and a green run writes that marker.
.PARAMETER Draft         with -Ship: create the release as a draft for the owner to check. NEXT.md is kept and -Supersede waits: after publishing the
                         draft on GitHub, run ship.cmd -ArchiveOnly [-Supersede].
.PARAMETER ArchiveOnly   with -Ship: no build; for a release of this version that is already published (a draft the owner published by hand), archive
                         release/NEXT.md (and with -Supersede point the previous release at it)
.PARAMETER Supersede     with -Ship: put a "newer build" line with the new release's link at the top of the previous published release's description
.PARAMETER KeepPrevious  with -Ship: leave earlier published releases visible. By default (the owner's rule, 2026-10-29) publishing a build turns every
                         other published release of the repository back into a draft, so testers only see the current build (nothing is deleted).
.PARAMETER NotPrerelease with -Ship: publish as a normal release instead of a pre-release. GitHub's releases/latest link only ever points at a published
                         release that is NOT a pre-release, so this is the only way for that link to move (the owner's call; pre-release is the default).
.PARAMETER NoPush        with -Ship: commit the archived release/NEXT.md but do not push it
.PARAMETER AllowEmptyNotes  with -Ship: ship although release/NEXT.md holds no bullets (the description then has no "What's new" section)
.PARAMETER Playtest      after the build (with -Ship: after publishing): restart the owner's local playtest server on this build: the zip's server\ in
                         %LOCALAPPDATA%\sm-reimpl\tmp-build\playtest-srv-<version>, 127.0.0.1:27025 only, --honor-side-requests --bot-damage-scale 0.34,
                         and 7 headless bots from this checkout's client (marines: tactical, devastator, assault; Chaos: tactical, havoc, raptor, tactical).
                         Only the playtest server and bots this script (or the earlier manual playtest runs) recorded are stopped first.
.EXAMPLE
  release.cmd                     (build all packages, scan, write SHA256SUMS.txt and RELEASE-NOTES.md)
  release.cmd -Smoke              (and run the playtest zip's launchers, game and server headless)
  release.cmd -Smoke -Linux       (the same with the linux-x64 server package too)
  release.cmd -Commit 4fa4d46 -Smoke   (the programs of commit 4fa4d46 in this tooling's layout)
  release.cmd -SkipBuild -Publish -DryRun   (re-scan the finished packages and show the gh command)
  ship.cmd                        (= release.ps1 -Ship: preflight, build, guard, smoke, publish, archive the What's new)
  ship.cmd -DryRun                (the checks and the commands, nothing built or published)
#>
[CmdletBinding()]
param(
    [string]$Commit,
    [switch]$AllowDirty, [switch]$PrepareOnly, [switch]$SkipClient, [switch]$SkipServer, [switch]$SkipBuild, [switch]$Smoke,
    [string]$GameDir,
    [switch]$Publish, [switch]$ServerAssets, [switch]$NoDraft, [Alias('WhatIf')][switch]$DryRun,
    [switch]$Ship, [switch]$SkipTests, [switch]$Draft, [switch]$Supersede, [switch]$NotPrerelease, [switch]$NoPush, [switch]$AllowEmptyNotes, [switch]$Playtest, [switch]$ArchiveOnly, [switch]$KeepPrevious,
    [switch]$Linux,
    [string]$Repo = 'Yeezima/sm-reimpl-releases', [switch]$AllowOtherRepo
)
# Step timings: every step's seconds and the total, printed as a table at the end (also when a step fails).
$script:RunClock = [Diagnostics.Stopwatch]::StartNew()
$script:StepTimes = [ordered]@{}
$script:StepName = $null
$script:StepClock = New-Object Diagnostics.Stopwatch
$script:TimingsShown = $false
function Time-Step([string]$name) { Stop-TimeStep; $script:StepName = $name; $script:StepClock.Restart() }
function Stop-TimeStep {
    if (-not $script:StepName) { return }
    $s = $script:StepClock.Elapsed.TotalSeconds
    if ($script:StepTimes.Contains($script:StepName)) { $script:StepTimes[$script:StepName] += $s } else { $script:StepTimes[$script:StepName] = $s }
    $script:StepName = $null
}
function Show-Timings([string]$outcome = 'done') {
    Stop-TimeStep
    if ($script:TimingsShown) { return }
    $script:TimingsShown = $true
    Write-Host ''
    Write-Host "Timings ($outcome), seconds:" -ForegroundColor Cyan
    foreach ($k in $script:StepTimes.Keys) { Write-Host ('  {0,-18} {1,6:0}' -f $k, $script:StepTimes[$k]) }
    Write-Host ('  {0,-18} {1,6:0}' -f 'total', $script:RunClock.Elapsed.TotalSeconds)
}
# A failure anywhere still prints the timings of the steps so far, then stops the script as before.
trap { if (Get-Command Show-Timings -ErrorAction SilentlyContinue) { Show-Timings 'failed' }; break }
Time-Step 'preflight'
if ($Repo -ne 'Yeezima/sm-reimpl-releases' -and -not $AllowOtherRepo) {
    throw "Publishing is pinned to Yeezima/sm-reimpl-releases; '$Repo' needs the explicit -AllowOtherRepo switch."
}
if ($Ship) {
    if ($Commit -or $Publish -or $SkipBuild -or $SkipClient -or $AllowDirty) { throw '-Ship builds and publishes HEAD of a clean main by itself; do not combine it with -Commit, -Publish, -SkipBuild, -SkipClient or -AllowDirty.' }
    $Smoke = $true             # a ship always runs the smoke test
}
if ($ServerAssets) { $Linux = $true }      # the server assets include the Linux package
foreach ($pair in @(@('SkipTests', $SkipTests), @('Draft', $Draft), @('Supersede', $Supersede), @('NotPrerelease', $NotPrerelease), @('NoPush', $NoPush), @('AllowEmptyNotes', $AllowEmptyNotes), @('ArchiveOnly', $ArchiveOnly), @('KeepPrevious', $KeepPrevious))) {
    if ($pair[1] -and -not $Ship) { throw "-$($pair[0]) belongs to -Ship (ship.cmd)." }
}
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Template = Join-Path $PSScriptRoot 'package'
$LocalApp = $env:LOCALAPPDATA
$Tools = Join-Path $LocalApp 'sm-reimpl\tools'
$GodotVersion = '4.7.2-stable'
$GodotName = "Godot_v$($GodotVersion)_mono_win64"
# Same pin as tools/client.ps1 (keep both in sync when Godot is bumped).
$GodotSha512 = '79229FD112B0C9CBEAB82363A4EF7BE18EA70F1CAF86BF912789335B136FBE7E01DB0053A33461438C5DA1C680C17BBB10040BD09BEDC51221CD4423D0367757'
$GodotConsole = Join-Path $Tools "godot\$GodotName\$($GodotName)_console.exe"
# tools/no-focus-steal: every process the build, the smoke test and the playtest start runs with no console window and no focus change
# (Start-QuietProcess); the launcher's window test runs on the private desktop the windowed Godot test clients use.
. (Join-Path $RepoRoot 'tools\quiet-launch.ps1')
$TestDesktop = 'sm-reimpl-test'
$GodotMarker = Join-Path $Tools "godot\$GodotName.complete"
$TemplatesName = "Godot_v$($GodotVersion)_mono_export_templates.tpz"
# SHA-512 of the official templates archive, pinned after the first verified download (empty = checked against SHA512-SUMS.txt only).
$TemplatesSha512 = 'BB5C41D72370ED743660361F6228006F808AB04CA33ABDC545D740B044F3FE057F32AE8CB7873A1BC86DDCD82AE683B9F6DFDFE4179852F2C0F1ACDE2FF6BD5A'   # downloaded, verified against SHA512-SUMS.txt, 2026-10-28
$TemplatesDir = Join-Path $env:APPDATA 'Godot\export_templates\4.7.2.stable.mono'
$Preset = 'Windows Desktop'
# Godot's own licence texts for the playtest zip (notes/licenses/godot/), fetched from the engine's repository at the release tag, SHA-256 pinned.
$GodotLicenceFiles = [ordered]@{
    'COPYRIGHT.txt' = 'CB1980C88089573BCACD7221D777C689BB8BBD778799F24C27FCA0FE5F774D6D'
    'LICENSE.txt'   = 'B0435E3B3E4E55238F05F4B306F30524A1B2E20147810D436EAA554FA6855C80'
}
# Paths (git pathspecs, relative to the repository root) left out of the source zip only, to keep it small.
# The source zip holds what builds and plays the game (the libraries, the server, the client and their run scripts); project notes, specs,
# tests, the developer tools (smtool, discovery, M1, movie, bots) and this release pipeline are left out of it. Every shipped binary
# (SmServer and the Godot client, with SmSim, SmNet, SmContent and SmFormats) still builds from what remains (the GPL source offer).)
$SourceExclude = @(
    'CLAUDE.md', 'docs', 'spec', 'tests', '.github', 'config',
    'src/SmTool', 'smtool.cmd', 'smtool.sh', 'SmReimpl.slnx', 'faithful-build.cmd',
    'tools/discover.ps1', 'discover.cmd', 'tools/m1.ps1', 'm1.cmd', 'tools/movie.ps1', 'movie.cmd', 'tools/bots.ps1', 'bots.cmd',
    'tools/release', 'release.cmd', 'ship.cmd', 'test-and-mark.cmd', 'release'
)
# The ship's "What's new" file and its archive (repository paths). Feature branches add tester-facing bullets to NEXT.md as they merge.
$NextFile = Join-Path $RepoRoot 'release\NEXT.md'
$HistoryDir = Join-Path $RepoRoot 'release\history'
# The owner's local playtest server (-Playtest): this PC only, a fixed port, the owner's settings, 7 bots.
$PlaytestPort = 27025
$PlaytestServerArgs = @('--bind', '127.0.0.1', '--port', "$PlaytestPort", '--honor-side-requests', '--bot-damage-scale', '0.34')
$PlaytestBots = @(
    @{ Side = 'marines'; Class = 'tactical' }, @{ Side = 'marines'; Class = 'devastator' }, @{ Side = 'marines'; Class = 'assault' },
    @{ Side = 'chaos'; Class = 'chaos tactical' }, @{ Side = 'chaos'; Class = 'havoc' }, @{ Side = 'chaos'; Class = 'raptor' }, @{ Side = 'chaos'; Class = 'chaos tactical' }
)
# The playtest zip's entry points, in the order a tester uses them (template in tools/release/package -> name at the top of the zip).
# The readmes, the notes and the scripts refer to each other through the tokens (@README@, @PLAY@, ...), so a rename here renames them everywhere.
$TopFiles = [ordered]@{
    README  = @{ Name = '1 - READ ME FIRST.txt';                   Template = 'README-FIRST.client.txt' }
    PLAY    = @{ Name = '2 - Play.cmd';                            Template = 'Play.cmd' }
    HOST    = @{ Name = '3 - Host a match on this PC.cmd';         Template = 'Host.cmd' }
    JOIN    = @{ Name = '4 - Join a server by address.cmd';        Template = 'Join.cmd' }
    HOSTLAN = @{ Name = '5 - Host a match for friends (LAN).cmd';  Template = 'HostLan.cmd' }
}
# The launcher (src/SmLauncher, a self-contained single-file exe) sits above them, numbered 0 so it is the first thing a tester sees. It keeps the
# tester's install up to date from the releases of $Repo (docs/releasing.md, "The launcher"). Token @LAUNCHER@ in the templates.
$LauncherTop = '0 - SM Reimpl Launcher.exe'

. (Join-Path $PSScriptRoot 'tested-marker.ps1')      # the "tests passed on this commit" marker (Read-TestedMarker, Write-TestedMarker, Invoke-ReleaseTests)
function Step([string]$text) { Write-Host "== $text" -ForegroundColor Cyan }
function Fail([string]$text) { throw $text }
function Run-Native([string]$what, [scriptblock]$block) {
    & $block
    if ($LASTEXITCODE -ne 0) { Fail "$what failed (exit $LASTEXITCODE)" }
}
function Read-Utf8([string]$path) { [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8) }
function Write-Utf8([string]$path, [string]$text) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false)))
}
# Runs Godot headless with its output in files (Windows PowerShell 5.1 turns redirected native stderr into errors); fails on a non-zero exit
# or any "ERROR:" line (an export that only logs an error still writes a broken package).
function Invoke-Godot([string[]]$arguments, [string]$what, [switch]$Lenient) {
    $logDir = Join-Path ([IO.Path]::GetTempPath()) ('sm-release-godot-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $o = Join-Path $logDir 'out.txt'; $e = Join-Path $logDir 'err.txt'
    $p = Start-QuietProcess -FilePath $GodotConsole -ArgumentList $arguments -StdOut $o -StdErr $e
    $p.WaitForExit()
    $text = ((Get-Content $o -ErrorAction SilentlyContinue) + (Get-Content $e -ErrorAction SilentlyContinue)) -join "`n"
    Remove-Item -LiteralPath $logDir -Recurse -Force -ErrorAction SilentlyContinue
    if ($p.ExitCode -ne 0 -or (-not $Lenient -and $text -match '(?m)^\s*ERROR:')) {
        Write-Host (($text -split "`n" | Select-Object -Last 25) -join "`n")
        Fail "Godot $what failed (exit $($p.ExitCode))"
    }
    return $text
}
function Is-Inside([string]$path, [string]$folder) {
    $p = [IO.Path]::GetFullPath($path).TrimEnd('\') + '\'
    $f = [IO.Path]::GetFullPath($folder).TrimEnd('\') + '\'
    return $p.StartsWith($f, [StringComparison]::OrdinalIgnoreCase)
}

# ---- Godot and export templates -------------------------------------------------------------------------------------
function Get-Sums([string]$base) {
    $c = (Invoke-WebRequest -UseBasicParsing -Uri "$base/SHA512-SUMS.txt").Content
    if ($c -is [byte[]]) { return [Text.Encoding]::UTF8.GetString($c) }
    return [string]$c
}
function Expected-Hash([string]$sums, [string]$file) {
    $line = $sums -split "`n" | Where-Object { $_ -match ('\s' + [regex]::Escape($file) + '\s*$') } | Select-Object -First 1
    if (-not $line) { Fail "SHA512-SUMS.txt has no entry for $file" }
    return ($line -split '\s+' | Select-Object -First 1).ToUpperInvariant()
}

function Ensure-Godot {
    if (Test-Path -LiteralPath $GodotMarker) { return }
    Step "Downloading Godot $GodotVersion (.NET, about 117 MB)"
    $dl = Join-Path $Tools 'download'
    New-Item -ItemType Directory -Force -Path $dl | Out-Null
    $base = "https://github.com/godotengine/godot/releases/download/$GodotVersion"
    $zip = Join-Path $dl "$GodotName.zip"
    Invoke-WebRequest -UseBasicParsing -Uri "$base/$GodotName.zip" -OutFile $zip
    $expected = Expected-Hash (Get-Sums $base) "$GodotName.zip"
    $actual = (Get-FileHash -Algorithm SHA512 -LiteralPath $zip).Hash
    if ($actual -ne $expected -or $actual -ne $GodotSha512) { Remove-Item -LiteralPath $zip; Fail 'The Godot download failed its SHA-512 check.' }
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $Tools 'godot') -Force
    Set-Content -LiteralPath $GodotMarker -Value $GodotVersion -Encoding ascii
    Remove-Item -LiteralPath $zip
}

function Ensure-Templates {
    $marker = Join-Path $TemplatesDir 'windows_release_x86_64.exe'
    if ((Test-Path -LiteralPath $marker) -and (Test-Path -LiteralPath (Join-Path $TemplatesDir 'version.txt'))) { return }
    $dl = Join-Path $Tools 'download'
    New-Item -ItemType Directory -Force -Path $dl | Out-Null
    $tpz = Join-Path $dl $TemplatesName
    $base = "https://github.com/godotengine/godot/releases/download/$GodotVersion"
    $expected = Expected-Hash (Get-Sums $base) $TemplatesName
    if ($TemplatesSha512 -and $expected -ne $TemplatesSha512.ToUpperInvariant()) { Fail 'The published templates hash differs from the pinned one; refusing.' }
    $have = (Test-Path -LiteralPath $tpz) -and ((Get-FileHash -Algorithm SHA512 -LiteralPath $tpz).Hash -eq $expected)
    if (-not $have) {
        Step "Downloading the Godot export templates ($TemplatesName, about 1.2 GB)"
        Invoke-WebRequest -UseBasicParsing -Uri "$base/$TemplatesName" -OutFile $tpz
        if ((Get-FileHash -Algorithm SHA512 -LiteralPath $tpz).Hash -ne $expected) { Remove-Item -LiteralPath $tpz; Fail 'The export templates download failed its SHA-512 check.' }
    }
    Step "Installing the Windows export templates into $TemplatesDir"
    New-Item -ItemType Directory -Force -Path $TemplatesDir | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($tpz)
    try {
        foreach ($e in $zip.Entries) {
            if ($e.FullName -notmatch '^templates/(windows_.*|version\.txt)$') { continue }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, (Join-Path $TemplatesDir ($e.FullName -replace '^templates/', '')), $true)
        }
    } finally { $zip.Dispose() }
    if (-not (Test-Path -LiteralPath $marker)) { Fail 'The templates archive has no windows_release_x86_64.exe.' }
    Remove-Item -LiteralPath $tpz
}

function Ensure-GodotLicences {
    $dir = Join-Path $Tools 'godot-licences'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    foreach ($name in $GodotLicenceFiles.Keys) {
        $path = Join-Path $dir $name
        $ok = (Test-Path -LiteralPath $path) -and ((Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash -eq $GodotLicenceFiles[$name])
        if ($ok) { continue }
        Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/godotengine/godot/$GodotVersion/$name" -OutFile $path
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash -ne $GodotLicenceFiles[$name]) { Remove-Item -LiteralPath $path; Fail "Godot's $name failed its pinned SHA-256 check." }
    }
    return $dir
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail 'git is required.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'The .NET 10 SDK is required: run m1.cmd once, or winget install Microsoft.DotNet.SDK.10' }

if ($PrepareOnly) {
    Ensure-Godot
    Ensure-Templates
    Write-Host "Godot and the export templates are ready ($TemplatesDir)." -ForegroundColor Green
    exit 0
}

# ---- version ---------------------------------------------------------------------------------------------------------
# The programs come from $BuildRef: HEAD, or the commit named by -Commit (the tooling stays this checkout's).
if ($Commit) {
    $CommitSha = (git -C $RepoRoot rev-parse --verify --quiet "$Commit^{commit}" | Out-String).Trim()
    if (-not $CommitSha) { Fail "-Commit '$Commit' is not a commit of this repository." }
    $dirty = $false
    $describe = (git -C $RepoRoot describe --tags --always $CommitSha).Trim()
    Write-Warning "Building the programs from commit $($CommitSha.Substring(0, 7)) with the release tooling of this checkout."
} else {
    $status = (git -C $RepoRoot status --porcelain) -join "`n"
    $dirty = [bool]$status.Trim()
    if ($dirty -and -not $AllowDirty -and -not ($Ship -and $DryRun)) { Fail "The working tree has uncommitted changes. Commit them, or pass -AllowDirty (the version then says -dirty and the source zip is HEAD only).`n$status" }
    $describe = (git -C $RepoRoot describe --tags --always --dirty).Trim()
    if ($dirty -and $describe -notmatch 'dirty$') { $describe += '-dirty' }
    $CommitSha = (git -C $RepoRoot rev-parse HEAD).Trim()
}
$Version = $describe -replace '[^A-Za-z0-9._-]', '_'
$protocolText = (git -C $RepoRoot show "$($CommitSha):src/SmNet/Protocol.cs" | Out-String)
$m = [regex]::Match($protocolText, 'public const ushort Version\s*=\s*(\d+);')
if (-not $m.Success) { Fail 'Could not read the protocol version from src/SmNet/Protocol.cs of the build commit.' }
$Protocol = $m.Groups[1].Value
# The build date is the commit's date (not today's), so a later -SkipBuild -Publish gives the same tag and "Built" line as the build did.
$BuildDate = (git -C $RepoRoot show -s --format=%cd --date=format:%Y-%m-%d $CommitSha).Trim()
if ($BuildDate -notmatch '^\d{4}-\d{2}-\d{2}$') { Fail "Could not read the commit date of $CommitSha ('$BuildDate')." }
$Info = "$Version|$BuildDate"
# The launcher's file version: <year>.<month*100+day>.<minute of the day> of the commit time (UTC). A launcher offers to replace itself with the one inside
# a newer build when that one's version is higher, so it must grow with every build (two builds in the same minute would compare equal: no offer).
$commitTime = [DateTimeOffset]::FromUnixTimeSeconds([int64]((git -C $RepoRoot show -s --format=%ct $CommitSha | Out-String).Trim())).UtcDateTime
$LauncherVersion = '{0}.{1}.{2}' -f $commitTime.Year, ($commitTime.Month * 100 + $commitTime.Day), ($commitTime.Hour * 60 + $commitTime.Minute)
# git describe found a tag: v<describe> (no second v if the tag already starts with v and a digit). A bare commit hash: tester-<date>-<short sha>.
$Tag = if ($Version -match '^[0-9a-f]{7,40}$') { "tester-$BuildDate-$($CommitSha.Substring(0, 7))" } elseif ($Version -match '^v\d') { $Version } else { "v$Version" }

# ---- ship helpers ------------------------------------------------------------------------------------------------------
$script:ShipProblems = 0
# A native command's exit code with its output discarded (Windows PowerShell 5.1 turns a native program's stderr into errors under 'Stop').
function Quiet-Exit([scriptblock]$b) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $b *> $null; return $LASTEXITCODE } finally { $ErrorActionPreference = $old }
}
function Ship-Check([bool]$ok, [string]$what, [string]$problem) {
    if ($ok) { Write-Host "  ok     $what" -ForegroundColor Green; return }
    if ($DryRun) { Write-Host "  WOULD REFUSE  $($what): $problem" -ForegroundColor Yellow; $script:ShipProblems++ }
    else { Fail "Ship refused. $($what): $problem" }
}
$dataRoot = if ($env:SM_DATA_DIR) { $env:SM_DATA_DIR } else { Join-Path $LocalApp 'sm-reimpl' }
$OutDir = Join-Path $dataRoot "release\$Version"
if (Is-Inside $OutDir $RepoRoot) { Fail 'The output folder must lie outside the repository.' }

# The game install: only for the guard and the smoke test. Read-only.
if (-not $GameDir) {
    $found = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Template 'Launcher.ps1') -FindGame
    if ($found) { $GameDir = ($found | Select-Object -First 1).Trim() }
    if (-not $GameDir) {
        $disc = Join-Path $dataRoot 'discovery'
        if (Test-Path -LiteralPath $disc) {
            $run = Get-ChildItem -LiteralPath $disc -Directory | Sort-Object Name -Descending | Where-Object { Test-Path (Join-Path $_.FullName 'game-dir.txt') } | Select-Object -First 1
            if ($run) { $GameDir = (Get-Content -LiteralPath (Join-Path $run.FullName 'game-dir.txt') -Raw).Trim() }
        }
    }
}
if ($GameDir -and (Is-Inside $OutDir $GameDir)) { Fail 'The output folder must lie outside the game install.' }

# Secrets sources whose values are searched for in every package, never printed: config/local.json of this checkout and of the main checkout
# (a worktree has none), and <data root>/settings.json (the game folder the front end remembers). The SMREIMPL_DISCORD_* environment variables
# are added by the scanner itself (--secrets-env).
$secretFiles = @()
$commonDir = [IO.Path]::GetFullPath([IO.Path]::Combine($RepoRoot, (git -C $RepoRoot rev-parse --git-common-dir).Trim()))
foreach ($c in @((Join-Path $RepoRoot 'config\local.json'), (Join-Path (Split-Path -Parent $commonDir) 'config\local.json'), (Join-Path $dataRoot 'settings.json'))) {
    if ((Test-Path -LiteralPath $c) -and ($secretFiles -notcontains $c)) { $secretFiles += $c }
}
if (-not ($secretFiles | Where-Object { (Split-Path -Leaf $_) -eq 'local.json' })) {
    Fail 'No config/local.json found (this checkout or the main checkout). The guard needs it to know the secrets it must keep out of the packages; create it (config/local.example.json) and run again.'
}
if ($dirty) { Write-Warning 'AllowDirty: EVERY package is built from HEAD (git archive), not from your uncommitted changes.' }
$script:Src = $RepoRoot       # the tree the packages are built from (the clean archive copy during a build)
$script:RuntimeVersions = @{}

$Stage = Join-Path $OutDir '_stage'
$PlaytestName = "SM-Reimpl-Playtest-$BuildDate-$Version-Windows"
$Zips = [ordered]@{
    playtest = Join-Path $OutDir "$PlaytestName.zip"
    serverWin = Join-Path $OutDir "sm-reimpl-server-win64-$Version.zip"
    serverLinux = Join-Path $OutDir "sm-reimpl-server-linux-x64-$Version.tar.gz"
    source = Join-Path $OutDir "sm-reimpl-source-$Version.zip"
}
$SourceZipName = Split-Path -Leaf $Zips.source
$Sums = Join-Path $OutDir 'SHA256SUMS.txt'
$Notes = Join-Path $OutDir 'RELEASE-NOTES.md'
$Tokens = @{ VERSION = $Version; PROTOCOL = $Protocol; DATE = $BuildDate; COMMIT = $CommitSha; SOURCEZIP = $SourceZipName }
foreach ($k in $TopFiles.Keys) { $Tokens[$k] = $TopFiles[$k].Name }
$Tokens.LAUNCHER = $LauncherTop
Write-Host "sm-reimpl release $Version (protocol $Protocol, $BuildDate, commit $($CommitSha.Substring(0, 7))) -> $OutDir" -ForegroundColor Green

# ---- packaging helpers -----------------------------------------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Expand-Tokens([string]$text, [hashtable]$tokens) {
    foreach ($k in $tokens.Keys) { $text = $text.Replace("@$k@", [string]$tokens[$k]) }
    $left = [regex]::Match($text, '@[A-Z]{3,12}@')
    if ($left.Success) { Fail "A template still holds the unknown token $($left.Value)." }
    return $text
}
# Copies a text template: tokens replaced, line endings CRLF (cmd, ps1, txt, md) or LF (sh), UTF-8 without BOM.
function Copy-Text([string]$src, [string]$dst, [hashtable]$tokens = @{}) {
    $text = Expand-Tokens (Read-Utf8 $src) $tokens
    $text = $text -replace "`r`n", "`n"
    if ([IO.Path]::GetExtension($dst) -ne '.sh') { $text = $text -replace "`n", "`r`n" }
    Write-Utf8 $dst $text
}

function Notices-Text([string]$kind) {
    $notices = (Read-Utf8 (Join-Path $script:Src 'THIRD_PARTY_NOTICES.md')).TrimEnd() + (Read-Utf8 (Join-Path $Template 'THIRD_PARTY_BINARIES.md'))
    if ($kind -eq 'source') { $notices = Read-Utf8 (Join-Path $script:Src 'THIRD_PARTY_NOTICES.md') }
    return (($notices -replace "`r`n", "`n") -replace "`n", "`r`n")
}
function Version-Text([string]$kind) {
    $lines = @("sm-reimpl $Version", "commit: $CommitSha", "protocol: $Protocol", "built: $BuildDate (UTC)", "package: $kind")
    if ($kind -like 'playtest*') { $lines += "godot: $GodotVersion (.NET), Windows Desktop export, ExportRelease" }
    if ($kind -ne 'source') { $rid = if ($kind -like '*linux*') { 'linux-x64' } else { 'win-x64' }; $lines += "dotnet runtime: $($script:RuntimeVersions[$rid]) ($rid, self-contained)" }
    return (($lines -join "`r`n") + "`r`n")
}
# LICENSE, THIRD_PARTY_NOTICES.md, README-FIRST.txt and VERSION.txt at the root of a server or source package.
function Common-Files([string]$dir, [string]$readmeTemplate, [string]$kind, [hashtable]$tokens) {
    Copy-Item -LiteralPath (Join-Path $script:Src 'LICENSE') -Destination (Join-Path $dir 'LICENSE')
    Write-Utf8 (Join-Path $dir 'THIRD_PARTY_NOTICES.md') (Notices-Text $kind)
    Copy-Text (Join-Path $Template $readmeTemplate) (Join-Path $dir 'README-FIRST.txt') $tokens
    Write-Utf8 (Join-Path $dir 'VERSION.txt') (Version-Text $kind)
}

# The licence files of the exact runtime pack that was published (microsoft.netcore.app.runtime.<rid>/<version> in the NuGet cache). Missing = fatal:
# a self-contained package without the runtime's licence and third-party notices must not ship.
function Add-DotnetLicences([string]$dir, [string]$rid) {
    $version = $script:RuntimeVersions[$rid]
    if (-not $version) { Fail "The runtime version of the $rid publish is unknown." }
    $packs = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $pack = Join-Path $packs "microsoft.netcore.app.runtime.$rid\$version"
    foreach ($f in 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') {
        if (-not (Test-Path -LiteralPath (Join-Path $pack $f))) { Fail "$f of the .NET runtime pack $rid $version was not found ($pack). Refusing to build a package without it." }
    }
    $target = Join-Path $dir 'licenses\dotnet'
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -LiteralPath (Join-Path $pack 'LICENSE.TXT') -Destination $target
    Copy-Item -LiteralPath (Join-Path $pack 'THIRD-PARTY-NOTICES.TXT') -Destination $target
}

# Zip with forward-slash entry names, under one top folder ($prefix) or none (empty $prefix: the playtest zip, whose top level is the numbered files);
# unix mode 755 for the listed files (Compress-Archive of Windows PowerShell 5.1 writes backslashes).
function New-Zip([string]$folder, [string]$zipPath, [string]$prefix, [string[]]$executables = @()) {
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath }
    $root = (Resolve-Path -LiteralPath $folder).ProviderPath.TrimEnd('\')
    $lead = if ($prefix) { "$prefix/" } else { '' }
    $fs = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
    $zip = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in (Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName)) {
            $rel = $f.FullName.Substring($root.Length + 1).Replace('\', '/')
            $entry = $zip.CreateEntry("$lead$rel", [IO.Compression.CompressionLevel]::Optimal)
            $mode = if ($executables -contains $rel) { 33261 } else { 33188 }      # 0100755 / 0100644
            $entry.ExternalAttributes = [BitConverter]::ToInt32([BitConverter]::GetBytes([uint32]($mode * 65536)), 0)
            $entry.LastWriteTime = [DateTimeOffset]$f.LastWriteTimeUtc
            $src = [IO.File]::OpenRead($f.FullName)
            try { $dst = $entry.Open(); try { $src.CopyTo($dst) } finally { $dst.Dispose() } } finally { $src.Dispose() }
        }
    } finally { $zip.Dispose(); $fs.Dispose() }
}

function Size-Text([string]$path) { '{0:N0} MB' -f ((Get-Item -LiteralPath $path).Length / 1MB) }
function Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

# The content guard. smtool is always built first (never a stale scanner). Fails closed: the game install must be indexed, the private cache is
# indexed when it exists, and every secrets source that exists is loaded and must yield values. Exit 0 = clean; anything else = refuse.
# The binary packages also get the file-type allowlist; the playtest zip must hold a verified Godot pack, and its source\ zip is scanned entry by
# entry without the allowlist (as the stand-alone source zip is). Loose documents get the content rules only.
function Invoke-Scan([string[]]$paths, [switch]$Rehash) {
    Run-Native 'dotnet build smtool' { dotnet build (Join-Path $RepoRoot 'src\SmTool\SmTool.csproj') -c Release -nologo -v quiet | Out-Null }
    $cache = Join-Path $dataRoot 'cache'
    $common = @('run', '--project', (Join-Path $RepoRoot 'src\SmTool\SmTool.csproj'), '-c', 'Release', '--no-build', '--', 'scan-package', '--require-install', '--require-secrets', '--secrets-env')
    # The comparison trees' fingerprints (path, size, write time, SHA-256; the install's also by Steam build id) are kept between runs in cache\guard, so
    # unchanged files are not hashed again (a stored hash is used for at most 7 days); the guard's answer is the same with or without them (smtool
    # ignores an index folder inside the install or a comparison tree, other than the private cache's own guard folder). -Rehash (the last scan before an
    # upload): no stored hash is used, every needed file is hashed again, and the index is still saved.
    $common += @('--index-cache', (Join-Path $cache 'guard'))
    if ($Rehash) { $common += '--rehash' }
    if (Test-Path -LiteralPath $cache) { $common += @('--against', $cache) }
    $common += @('--against-install', $(if ($GameDir) { $GameDir } else { 'auto' }))
    foreach ($c in $secretFiles) { $common += @('--secrets', $c) }
    # Profiles by the file's leaf name (never the folder).
    $leaf = { param($p) Split-Path -Leaf $p }
    $playtest = @($paths | Where-Object { (& $leaf $_) -match '^SM-Reimpl-Playtest-' })
    $server = @($paths | Where-Object { (& $leaf $_) -match '^sm-reimpl-server-' })
    $other = @($paths | Where-Object { (& $leaf $_) -notmatch '^(SM-Reimpl-Playtest-|sm-reimpl-server-)' })
    # Anything but a clean exit 0 is a refusal: a crashed process exits with a large or negative code (0xE0434352), or leaves no code at all.
    # The first real non-zero code is returned (-1 when there was no code), so the failure message can show it.
    $result = 0
    foreach ($run in @(@{ Paths = $playtest; Extra = @('--profile', 'binary', '--require-pack', '--source-archive', "source/$SourceZipName") }, @{ Paths = $server; Extra = @('--profile', 'binary') }, @{ Paths = $other; Extra = @() })) {
        if ($run.Paths.Count -eq 0) { continue }
        $global:LASTEXITCODE = 1                       # so that a command that never ran cannot look like success
        & dotnet @($common + $run.Extra + $run.Paths) | Out-Host
        if ($null -eq $LASTEXITCODE) { if ($result -eq 0) { $result = -1 } }
        elseif ($LASTEXITCODE -ne 0 -and $result -eq 0) { $result = $LASTEXITCODE }
    }
    return $result      # Out-Host above: the function's output must be the exit code only
}

# SHA256SUMS.txt must list exactly the packages in the folder and every hash must match; -Require names the packages that must be there.
function Test-Sums([string[]]$Require = @()) {
    if (-not (Test-Path -LiteralPath $Sums)) { Fail 'SHA256SUMS.txt is missing.' }
    $listed = @()
    foreach ($line in (Get-Content -LiteralPath $Sums)) {
        if (-not $line.Trim()) { continue }
        $parts = $line -split '\s+', 2
        $name = $parts[1].Trim().TrimStart('*')
        $listed += $name
        $file = Join-Path $OutDir $name
        if (-not (Test-Path -LiteralPath $file)) { Fail "SHA256SUMS.txt names $name which is missing." }
        if ((Sha256 $file) -ne $parts[0].ToLowerInvariant()) { Fail "SHA-256 mismatch for $name." }
    }
    $present = @($Zips.Values | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Split-Path -Leaf $_ })
    foreach ($name in @($present) + @($Require | ForEach-Object { Split-Path -Leaf $_ })) { if ($listed -notcontains $name) { Fail "SHA256SUMS.txt does not list $name." } }
    foreach ($name in $listed) { if ($present -notcontains $name) { Fail "SHA256SUMS.txt lists $name which is not one of the packages." } }
}

# The release description (tools/release/package/RELEASE-NOTES.md) with the playtest zip's name, size and SHA-256; with -ServerAssets also the server
# packages and the source zip with their hashes. Written next to the packages on every build, so the owner can read it before publishing.
# "What's new" (release/NEXT.md) goes at the top. -Placeholder (ship -DryRun): nothing is built yet, so the size and the hash are placeholders.
function Write-ReleaseNotes([switch]$WithServerAssets, [switch]$Placeholder) {
    if (-not $Placeholder -and -not (Test-Path -LiteralPath $Zips.playtest)) { return }
    $t = $Tokens.Clone()
    $t.ZIP = Split-Path -Leaf $Zips.playtest
    $t.ZIPSIZE = if ($Placeholder) { '<size after the build>' } else { Size-Text $Zips.playtest }
    $t.SHA256 = if ($Placeholder) { '<SHA-256 after the build>' } else { Sha256 $Zips.playtest }
    # The comment marks the end of the What's new for the launcher, which shows that part (invisible on GitHub).
    $t.WHATSNEW = if ($WhatsNew) { "## What's new in this build`n`n$WhatsNew`n`n<!-- whats-new-end -->`n`n" } else { '' }
    $t.SERVERASSETS = ''
    if ($WithServerAssets -and -not $Placeholder) {
        foreach ($k in 'serverWin', 'serverLinux', 'source') { if (-not (Test-Path -LiteralPath $Zips[$k])) { Fail "-ServerAssets needs $(Split-Path -Leaf $Zips[$k]); build with -ServerAssets (or -Linux) and without -SkipServer." } }
        $rows = foreach ($k in 'serverWin', 'serverLinux', 'source') { '| `{0}` | `{1}` |' -f (Split-Path -Leaf $Zips[$k]), (Sha256 $Zips[$k]) }
        $t.SERVERASSETS = "`n## Dedicated servers (for server hosts)`n`nTesters do not need these. Stand-alone servers for a Windows or Linux machine, and the source code as a separate download:`n`n| File | SHA-256 |`n|---|---|`n" + ($rows -join "`n") + "`n"
    }
    Copy-Text (Join-Path $Template 'RELEASE-NOTES.md') $Notes $t
}

# ---- What's new (release/NEXT.md) ------------------------------------------------------------------------------------
# smtool whats-new drops NEXT.md's rules comment and refuses text that is not tester-facing (PR numbers, repository paths, hashes, git talk; tested in
# WhatsNewTests). The file's SHA-256 is kept: the archive step only empties NEXT.md if nobody changed it since it was read.
function Get-WhatsNew {
    $result = @{ Text = ''; Problems = @(); Hash = '' }
    if (-not (Test-Path -LiteralPath $NextFile)) { return $result }
    $result.Hash = Sha256 $NextFile
    Run-Native 'dotnet build smtool' { dotnet build (Join-Path $RepoRoot 'src\SmTool\SmTool.csproj') -c Release -nologo -v quiet | Out-Null }
    $out = Join-Path ([IO.Path]::GetTempPath()) ('sm-whatsnew-' + [Guid]::NewGuid().ToString('N') + '.md')
    try {
        $lines = @(& dotnet run --project (Join-Path $RepoRoot 'src\SmTool\SmTool.csproj') -c Release --no-build -- whats-new $NextFile --out $out)
        if ($LASTEXITCODE -eq 0) { $result.Text = (Read-Utf8 $out).Trim() }
        elseif ($LASTEXITCODE -eq 1) { $result.Problems = $lines }
        else { Fail "smtool whats-new failed (exit $LASTEXITCODE)." }
    } finally { Remove-Item -LiteralPath $out -ErrorAction SilentlyContinue }
    return $result
}
$NextInfo = if ($Commit) { @{ Text = ''; Problems = @(); Hash = '' } } else { Get-WhatsNew }      # a re-packaged old build has no "What's new" of its own
$WhatsNew = $NextInfo.Text
if (-not $Ship -and $NextInfo.Problems.Count -gt 0) { Write-Warning "release/NEXT.md is not tester-facing yet, so the description has no What's new:`n  $($NextInfo.Problems -join "`n  ")" }

# ---- ship: the publish commands, the previous release, the archive -----------------------------------------------------
function Format-Command([string]$exe, [string[]]$a) { "$exe " + (($a | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' ') }
# gh release create for the ship. GitHub's releases/latest only ever points at a published release that is not a pre-release, so --latest is passed
# only with -NotPrerelease (with a pre-release it could not move the link).
function Ship-CreateArgs {
    $a = @('release', 'create', $Tag, '--repo', $Repo, '--title', "sm-reimpl tester build $Version", '--notes-file', $Notes)
    if ($Draft) { $a += '--draft' }
    if ($NotPrerelease) { if (-not $Draft) { $a += '--latest' } } else { $a += '--prerelease' }
    return ,($a + @($Zips.playtest))
}
# The newest published (not draft) release other than this one: the one -Supersede points at the new build. Empty when there is none or gh fails.
function Previous-Release {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $tags = @(& gh api "repos/$Repo/releases?per_page=30" --jq '.[] | select(.draft == false) | .tag_name' 2>$null)
        if ($LASTEXITCODE -ne 0) { return $null }
        return @($tags | Where-Object { $_ -and $_ -ne $Tag } | Select-Object -First 1)[0]
    } finally { $ErrorActionPreference = $old }
}
$SupersedeMark = '<!-- superseded -->'
# Puts a "newer build" line on top of the previous release's description. Refuses (throws) when its description cannot be read or is empty: an edit
# from an empty read would wipe it.
function Supersede-Release([string]$previous, [string]$newUrl) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $body = (& gh release view $previous --repo $Repo --json body --jq .body 2>$null | Out-String)
        if ($LASTEXITCODE -ne 0) { Fail "gh release view $previous failed (exit $LASTEXITCODE)" }
        if (-not $body.Trim()) { Fail "the description of $previous came back empty" }
        if ($body -match [regex]::Escape($SupersedeMark)) { $body = [regex]::Replace($body, '(?s)^.*?' + [regex]::Escape($SupersedeMark) + '\s*', '') }
        $banner = "> **A newer playtest build is out: [$Tag]($newUrl).** Please use that one; this build stays here for reference.`n$SupersedeMark`n`n"
        $file = Join-Path $OutDir 'SUPERSEDED-NOTES.md'
        Write-Utf8 $file ($banner + $body.TrimStart())
        & gh release edit $previous --repo $Repo --notes-file $file
        if ($LASTEXITCODE -ne 0) { Fail "gh release edit $previous failed (exit $LASTEXITCODE)" }
    } finally { $ErrorActionPreference = $old }
}
function Invoke-Supersede([string]$url) {
    try {
        $prev = Previous-Release
        if ($prev) { Supersede-Release $prev $url; Write-Host "  $prev now points at $Tag." -ForegroundColor Green }
        else { Write-Host '  -Supersede: no earlier published release found.' }
    } catch { Write-Warning "-Supersede did not edit the previous release ($($_.Exception.Message)). Edit it on GitHub if wanted (docs/releasing.md)." }
}

# Every other published (not draft) release of the repository: the ones a new build hides. Throws when gh fails.
function Published-Others {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $tags = @(& gh api --paginate "repos/$Repo/releases?per_page=100" --jq '.[] | select(.draft == false) | .tag_name' 2>$null)
        if ($LASTEXITCODE -ne 0) { Fail "gh api repos/$Repo/releases failed (exit $LASTEXITCODE)" }
        return @($tags | Where-Object { $_ -and $_ -cne $Tag })
    } finally { $ErrorActionPreference = $old }
}
# Hides the earlier builds (back to drafts: invisible to testers, nothing deleted) once $Tag is published. Never fails the ship: a warning per problem.
function Hide-PreviousReleases {
    if ($KeepPrevious) { Write-Host '  -KeepPrevious: earlier releases stay visible.'; return }
    if ($Supersede) { Write-Host '  note: -Supersede edited the previous release, which is hidden now; use -Supersede with -KeepPrevious.' -ForegroundColor Yellow }
    try {
        $state = (& gh release view $Tag --repo $Repo --json isDraft | ConvertFrom-Json)
        if (-not $state -or $state.isDraft) { Write-Warning "$Tag is not published; earlier releases stay visible."; return }
        $others = @(Published-Others)
        if (-not $others) { Write-Host '  No earlier published release to hide.'; return }
        foreach ($t in $others) {
            & gh release edit $t --repo $Repo --draft=true | Out-Null
            if ($LASTEXITCODE -eq 0) { Write-Host "  hid $t (now a draft)" -ForegroundColor Green } else { Write-Warning "could not hide $t (gh exit $LASTEXITCODE); hide it on GitHub if wanted." }
        }
    } catch { Write-Warning "Earlier releases were not hidden ($($_.Exception.Message)). Hide them on GitHub if wanted (docs/releasing.md)." }
}

# The archive: release/NEXT.md -> release/history/<version>.md and NEXT.md back to its rules comment, in ONE commit directly on top of the shipped commit,
# pushed as exactly that commit (<archive>:main) and only while origin/main is still the shipped commit. Anything unexpected (another branch, HEAD moved,
# NEXT.md edited since it was read, a dirty tree, origin/main moved) skips the step with instructions; the release itself is already out.
function Archive-Manual([string]$why) {
    Write-Warning "The What's new was NOT archived: $why"
    Write-Host "  To finish by hand on main: move the bullets of release/NEXT.md into release/history/$Version.md (a heading '# Playtest build $Version ($BuildDate)'" -ForegroundColor Yellow
    Write-Host '  and the release link), leave only the rules comment in NEXT.md, commit both files and push them with a pull request or git push.' -ForegroundColor Yellow
}
function Archive-WhatsNew([string]$url) {
    $git = { param([string[]]$a) $o = $ErrorActionPreference; $ErrorActionPreference = 'Continue'; try { $out = @(& git -C $RepoRoot @a 2>&1); return @{ Code = $LASTEXITCODE; Out = ($out -join "`n") } } finally { $ErrorActionPreference = $o } }
    $branch = (& $git @('rev-parse', '--abbrev-ref', 'HEAD')).Out.Trim()
    $head = (& $git @('rev-parse', 'HEAD')).Out.Trim()
    $status = (& $git @('status', '--porcelain')).Out.Trim()
    $nowHash = if (Test-Path -LiteralPath $NextFile) { Sha256 $NextFile } else { '' }
    if ($branch -ne 'main') { Archive-Manual "the checkout is on '$branch', not main."; return }
    if ($head -ne $CommitSha) { Archive-Manual "HEAD moved from the shipped commit $($CommitSha.Substring(0, 7)) to $($head.Substring(0, [Math]::Min(7, $head.Length)))."; return }
    if ($status) { Archive-Manual 'the working tree has changes.'; return }
    if ($nowHash -ne $NextInfo.Hash) { Archive-Manual 'release/NEXT.md changed since the ship read it.'; return }
    New-Item -ItemType Directory -Force -Path $HistoryDir | Out-Null
    $hist = "release/history/$Version.md"
    Write-Utf8 (Join-Path $HistoryDir "$Version.md") ("# Playtest build $Version ($BuildDate)`n`nProtocol $Protocol. Release: $url`n`n$WhatsNew`n")
    $rules = [regex]::Match((Read-Utf8 $NextFile), '(?s)^\s*<!--.*?-->').Value
    Write-Utf8 $NextFile ($rules.Trim() + "`n")
    foreach ($step in @(@('add', '--', 'release/NEXT.md', $hist), @('commit', '-q', '-m', "release: shipped playtest build $Version ($Tag); What's new archived", '--', 'release/NEXT.md', $hist))) {
        Write-Host "  $(Format-Command 'git' $step)"
        $r = & $git $step
        if ($r.Code -ne 0) { Archive-Manual "git $($step[0]) failed (exit $($r.Code)): $($r.Out)"; return }
    }
    $archive = (& $git @('rev-parse', 'HEAD')).Out.Trim()
    $parent = (& $git @('rev-parse', 'HEAD^')).Out.Trim()
    if ($parent -ne $CommitSha) { Archive-Manual "the archive commit's parent is not the shipped commit."; return }
    if ($NoPush) { Write-Host "  -NoPush: committed $($archive.Substring(0, 7)); push it with: git push origin $($archive):main" -ForegroundColor Yellow; return }
    $f = & $git @('fetch', '--quiet', 'origin', 'main')
    $origin = (& $git @('rev-parse', '--verify', '--quiet', 'origin/main')).Out.Trim()
    if ($f.Code -ne 0 -or $origin -ne $CommitSha) {
        Write-Warning "Not pushing: origin/main is $(if ($origin) { $origin.Substring(0, 7) } else { 'unreadable' }), no longer the shipped commit $($CommitSha.Substring(0, 7)). The archive commit $($archive.Substring(0, 7)) stays local: rebase it on origin/main and push it with a pull request."
        return
    }
    Write-Host "  $(Format-Command 'git' @('push', 'origin', "$($archive):main"))"
    $r = & $git @('push', 'origin', "$($archive):main")
    if ($r.Code -ne 0) { Write-Warning "git push failed (exit $($r.Code)): $($r.Out). The archive commit $($archive.Substring(0, 7)) is local: push it with a pull request." }
    else { Write-Host "  pushed the archive commit $($archive.Substring(0, 7)) to origin/main" -ForegroundColor Green }
}

# ---- the owner's local playtest server (-Playtest) ---------------------------------------------------------------------
# Stops only what an earlier playtest run recorded (pids.txt: "pid" or "pid start label" lines; server.txt), each checked again before it is stopped:
# a bot is a Godot process whose command line joins 127.0.0.1:<port> with --bot, the server an SmServer.exe from a tmp-build\playtest-srv-* folder on
# that port; when a start time was recorded, the process must have started then. Anything else holding the port is never touched: the run refuses.
$PlaytestState = Join-Path $dataRoot "tmp-build\playtest-$PlaytestPort-bots"
function Read-PlaytestRecords {
    $records = @{}
    foreach ($f in 'pids.txt', 'server.txt') {
        $path = Join-Path $PlaytestState $f
        if (-not (Test-Path -LiteralPath $path)) { continue }
        foreach ($line in (Get-Content -LiteralPath $path)) {
            $parts = @($line.Trim() -split '\s+')
            if ($parts[0] -notmatch '^\d+$') { continue }
            $start = if ($parts.Count -gt 1 -and $parts[1] -match '^\d{15,}$') { [int64]$parts[1] } else { $null }
            $records[[int]$parts[0]] = $start
        }
    }
    return $records
}
function Same-Start($proc, $start) {
    if ($null -eq $start) { return $true }                      # an older bare-PID record: the command-line check alone decides
    if (-not $proc.CreationDate) { return $false }
    return [Math]::Abs($proc.CreationDate.ToFileTimeUtc() - $start) -lt 10000000      # within a second (CIM and Process round differently)
}
function Playtest-Procs {
    $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    $records = Read-PlaytestRecords
    $srvRoot = Join-Path $dataRoot 'tmp-build'
    $isOurServer = { param($p) $p.Name -eq 'SmServer.exe' -and $p.ExecutablePath -and (Is-Inside $p.ExecutablePath $srvRoot) -and ((Split-Path -Leaf (Split-Path -Parent $p.ExecutablePath)) -like 'playtest-srv*') -and $p.CommandLine -match "--port $PlaytestPort(\s|$)" }
    $isOurBot = { param($p) $p.Name -like 'Godot*' -and $p.CommandLine -match "--connect 127\.0\.0\.1:$PlaytestPort(\s|$)" -and $p.CommandLine -match '--bot(\s|$)' }
    $bots = @($all | Where-Object { $records.ContainsKey([int]$_.ProcessId) -and (& $isOurBot $_) -and (Same-Start $_ $records[[int]$_.ProcessId]) })
    # Servers: recorded ones (with their start time), and earlier manual playtest servers, which are only ever playtest-srv-* copies on the playtest port.
    $servers = @($all | Where-Object { (& $isOurServer $_) -and (-not $records.ContainsKey([int]$_.ProcessId) -or (Same-Start $_ $records[[int]$_.ProcessId])) })
    return @{ Bots = $bots; Servers = $servers }
}
# Who holds the playtest port: $null when it is free, else the owning PIDs and whether every one of them is a playtest server this script may stop.
function Playtest-PortHolders {
    $pids = @()
    try { $pids = @(Get-NetUDPEndpoint -LocalPort $PlaytestPort -ErrorAction Stop | ForEach-Object { [int]$_.OwningProcess } | Select-Object -Unique) } catch { $pids = @() }
    if ($pids.Count -eq 0) {
        $listening = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveUdpListeners() | Where-Object { $_.Port -eq $PlaytestPort }
        if (-not $listening) { return $null }
        return @{ Pids = @(); Ours = $false }                    # held, owner unknown: never ours
    }
    $ours = @((Playtest-Procs).Servers | ForEach-Object { [int]$_.ProcessId })
    return @{ Pids = $pids; Ours = (@($pids | Where-Object { $ours -notcontains $_ }).Count -eq 0) }
}
function Start-Playtest {
    $ErrorActionPreference = 'Continue'      # taskkill and the builds write to stderr; failures are checked by exit code
    $zip = $Zips.playtest
    if (-not (Test-Path -LiteralPath $zip)) { Fail "-Playtest needs the playtest zip of $Version." }
    if ($Commit) { Fail '-Playtest runs the bots from this checkout''s client; it cannot be combined with -Commit.' }
    $client = Join-Path $RepoRoot 'client'
    $holders = Playtest-PortHolders
    if ($holders -and -not $holders.Ours) { Fail "UDP $PlaytestPort is held by a process this script did not start (pid $($holders.Pids -join ', ')); stop it yourself, then run again." }
    Step "Playtest: restarting the local playtest server on $Version (127.0.0.1:$PlaytestPort)"
    $old = Playtest-Procs
    foreach ($p in $old.Bots) { Write-Host "  stopping bot pid $($p.ProcessId)"; & taskkill.exe /PID $p.ProcessId /T /F *> $null }
    foreach ($p in $old.Servers) { Write-Host "  stopping playtest server pid $($p.ProcessId)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
    if (Playtest-PortHolders) { Fail "UDP $PlaytestPort is still in use after stopping the recorded playtest processes; stop the holder yourself, then run again." }
    # The server: the zip's own server\ (exactly the shipped binary). Entries that would land outside the folder (zip slip) are refused.
    $srvDir = Join-Path $dataRoot "tmp-build\playtest-srv-$Version"
    if (Test-Path -LiteralPath $srvDir) { Remove-Item -LiteralPath $srvDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $srvDir | Out-Null
    $z = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($e in $z.Entries) {
            if ($e.FullName -notmatch '^server/(.+)$' -or $e.FullName.EndsWith('/')) { continue }
            $rel = $Matches[1]
            if ($rel -match '(^|[\\/])\.\.([\\/]|$)' -or [IO.Path]::IsPathRooted($rel) -or $rel.Contains(':')) { Fail "The playtest zip holds an unsafe path ($($e.FullName)); refusing to extract it." }
            $dst = [IO.Path]::GetFullPath((Join-Path $srvDir ($rel -replace '/', '\')))
            if (-not (Is-Inside $dst $srvDir)) { Fail "The playtest zip holds a path outside server\ ($($e.FullName)); refusing to extract it." }
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dst, $true)
        }
    } finally { $z.Dispose() }
    New-Item -ItemType Directory -Force -Path $PlaytestState | Out-Null
    $slog = Join-Path $PlaytestState 'server.log'
    foreach ($f in $slog, (Join-Path $PlaytestState 'server.err')) { Remove-Item -LiteralPath $f -ErrorAction SilentlyContinue }
    if ($GameDir) { $env:SM_GAME_DIR = $GameDir }
    $sp = Start-QuietProcess -FilePath (Join-Path $srvDir 'SmServer.exe') -ArgumentList $PlaytestServerArgs -WorkingDirectory $srvDir -StdOut $slog -StdErr (Join-Path $PlaytestState 'server.err')
    Set-Content -LiteralPath (Join-Path $PlaytestState 'server.txt') -Value ("{0} {1} server" -f $sp.Id, $sp.StartTime.ToFileTimeUtc()) -Encoding ascii
    $ready = $false
    for ($i = 0; $i -lt 180 -and -not $sp.HasExited; $i++) { Start-Sleep -Seconds 1; if ((Test-Path $slog) -and (Select-String -LiteralPath $slog -Pattern '^smserver on UDP' -Quiet)) { $ready = $true; break } }
    $localOnly = $ready -and (Select-String -LiteralPath $slog -Pattern "^listening on 127\.0\.0\.1 only" -Quiet)
    if (-not $ready -or -not $localOnly) {
        if (-not $sp.HasExited) { Stop-Process -Id $sp.Id -Force -ErrorAction SilentlyContinue }      # the process started just above
        Remove-Item Env:\SM_GAME_DIR -ErrorAction SilentlyContinue
        Fail "The playtest server $(if (-not $ready) { 'did not print its ready line' } else { 'did not report the 127.0.0.1-only bind' }); it was stopped (log: $slog)."
    }
    Write-Host "  server: pid $($sp.Id), 127.0.0.1:$PlaytestPort only, $srvDir" -ForegroundColor Green
    # The bots: this checkout's client (the shipped commit when shipping), headless.
    Ensure-Godot
    Run-Native 'dotnet build client' { dotnet build (Join-Path $client 'SmClient.csproj') -nologo -v quiet | Out-Null }
    if (-not (Test-Path -LiteralPath (Join-Path $client '.godot'))) { Invoke-Godot @('--headless', '--path', $client, '--import') 'import' -Lenient | Out-Null }
    $pidFile = Join-Path $PlaytestState 'pids.txt'
    Set-Content -LiteralPath $pidFile -Value '' -Encoding ascii
    $n = 0
    foreach ($b in $PlaytestBots) {
        $n++
        $bargs = @('--headless', '--path', $client, '--', '--connect', "127.0.0.1:$PlaytestPort", '--bot', '--no-sound', '--class', $b.Class, '--side', $b.Side)
        $bp = Start-QuietProcess -FilePath $GodotConsole -ArgumentList $bargs -WorkingDirectory $RepoRoot -StdOut (Join-Path $PlaytestState "bot$n.log") -StdErr (Join-Path $PlaytestState "bot$n.err")
        Add-Content -LiteralPath $pidFile -Value ("{0} {1} bot{2}-{3}-{4}" -f $bp.Id, $bp.StartTime.ToFileTimeUtc(), $n, $b.Side, ($b.Class -replace ' ', '_')) -Encoding ascii
        Write-Host "  bot $n (pid $($bp.Id)): $($b.Side), $($b.Class)"
        Start-Sleep -Milliseconds 1500
    }
    Remove-Item Env:\SM_GAME_DIR -ErrorAction SilentlyContinue
    Write-Host "Playtest server ready: join 127.0.0.1:$PlaytestPort (this PC only). PIDs: $pidFile" -ForegroundColor Green
}

# ---- ship preflight ----------------------------------------------------------------------------------------------------
if ($Ship) {
    Step "Ship preflight: $Version (protocol $Protocol, commit $CommitSha, built $BuildDate) -> $Tag in $Repo"
    $branch = (git -C $RepoRoot rev-parse --abbrev-ref HEAD).Trim()
    Ship-Check ($branch -eq 'main') 'on main' "the checkout is on '$branch'. Ship from the main checkout: git switch main; git pull"
    Ship-Check (-not $dirty) 'clean working tree' 'commit or stash the changes first (git status)'
    $fetchCode = Quiet-Exit { git -C $RepoRoot fetch --quiet origin main }
    Ship-Check ($fetchCode -eq 0) 'git fetch origin main' "failed (exit $fetchCode): check the network and the remote"
    $originMain = (git -C $RepoRoot rev-parse --verify --quiet origin/main | Out-String).Trim()
    Ship-Check ($fetchCode -eq 0 -and $originMain -eq $CommitSha) 'up to date with origin/main' "HEAD is $($CommitSha.Substring(0, 7)), origin/main is $(if ($originMain) { $originMain.Substring(0, 7) } else { 'unknown' }): git pull (or push the local commits) first"
    $ghOk = [bool](Get-Command gh -ErrorAction SilentlyContinue)
    Ship-Check $ghOk 'GitHub CLI (gh)' 'install it (winget install GitHub.cli) and run gh auth login'
    if ($ghOk) {
        Ship-Check ((Quiet-Exit { gh auth status }) -eq 0) 'gh is signed in' 'run gh auth login'
        $exists = (Quiet-Exit { gh release view $Tag --repo $Repo }) -eq 0
        if ($ArchiveOnly) { Ship-Check $exists "the release $Tag exists (-ArchiveOnly)" "$Tag is not in $Repo" }
        else { Ship-Check (-not $exists) "the version is not released yet ($Tag)" "$Tag already exists in $Repo; merge the next change first (a new commit is a new version)" }
    }
    Ship-Check ($NextInfo.Problems.Count -eq 0) 'release/NEXT.md is tester-facing' ($NextInfo.Problems -join '; ')
    Ship-Check ([bool]$WhatsNew -or $AllowEmptyNotes) 'release/NEXT.md has bullets for this build' 'add the tester-facing changes (see the comment in the file), or pass -AllowEmptyNotes'
    if ($Playtest) {
        $holders = Playtest-PortHolders
        Ship-Check (-not $holders -or $holders.Ours) "UDP $PlaytestPort is free or held by the earlier playtest server (-Playtest)" "it is held by another process (pid $(if ($holders) { $holders.Pids -join ', ' })); stop it first"
    }
    # The tests: skipped by -SkipTests, or when both passed on exactly this commit within 24 hours (test-and-mark.cmd or an earlier ship wrote the marker;
    # the tree is clean here, so the commit is the code). Otherwise they run, and a green run writes the marker.
    $testedAt = if ($SkipTests -or $ArchiveOnly) { $null } else { Read-TestedMarker $dataRoot $CommitSha }
    if ($SkipTests -or $ArchiveOnly) { Write-Host '  skip   dotnet test and refine-register --check' -ForegroundColor Yellow }
    elseif ($testedAt -and -not $dirty) {
        $ago = [DateTime]::UtcNow - $testedAt
        Write-Host ("  skip   dotnet test and refine-register --check: both passed on {0} at {1:yyyy-MM-dd HH:mm} UTC ({2:0.0} h ago; {3})" -f $CommitSha.Substring(0, 7), $testedAt, $ago.TotalHours, (Get-TestedMarkerPath $dataRoot $CommitSha)) -ForegroundColor Green
    }
    elseif ($DryRun) { Write-Host "  would  run dotnet test and smtool refine-register --check (no marker of a green run on $($CommitSha.Substring(0, 7)) within $TestedMaxAgeHours h)" }
    else {
        Time-Step 'tests'
        Step 'Ship preflight: dotnet test'
        $r = Invoke-ReleaseTests $RepoRoot
        Ship-Check ($r.Test -eq 0) 'dotnet test' "failed (exit $($r.Test))"
        Ship-Check ($r.Refine -eq 0) 'refine-register --check' 'the register is stale or a spec row lacks kind/source/confidence: run smtool refine-register, commit, merge'
        # Only a run on a tree that stayed clean at the shipped commit throughout is recorded (the ship goes on either way: the checks passed).
        $why = ''
        $marker = Write-TestedMarkerIfStable $dataRoot $CommitSha $r ([ref]$why)
        if ($marker) { Write-Host "  tests recorded as passed on $($CommitSha.Substring(0, 7)) ($marker)" }
        else { Write-Warning "The tests passed but were not recorded: $why. Check the checkout before shipping (git status)." }
        Time-Step 'preflight'
    }
}

# -ArchiveOnly: the release was published by hand from a draft; archive NEXT.md for it (and point the previous release at it).
if ($Ship -and $ArchiveOnly) {
    $state = if ((Quiet-Exit { gh release view $Tag --repo $Repo }) -eq 0) { (& gh release view $Tag --repo $Repo --json isDraft,url | ConvertFrom-Json) } else { $null }
    if ($DryRun) {
        Write-Host "  would archive release/NEXT.md for $Tag$(if ($Supersede) { ' and point the previous release at it' })$(if (-not $KeepPrevious) { ' and hide the earlier builds' })"
        if ($script:ShipProblems -gt 0) { Write-Host "Preflight: $($script:ShipProblems) check(s) would refuse." -ForegroundColor Yellow; Show-Timings; exit 1 }
        Show-Timings; exit 0
    }
    if (-not $state -or $state.isDraft) { Fail "$Tag is not published yet: publish the draft on GitHub first." }
    $ErrorActionPreference = 'Continue'
    if ($Supersede) { Invoke-Supersede $state.url }
    Step 'Hiding the earlier builds'; Hide-PreviousReleases
    if ($WhatsNew) { Step "Archiving release/NEXT.md to release/history/$Version.md"; Archive-WhatsNew $state.url } else { Write-Host 'release/NEXT.md has nothing to archive.' }
    Show-Timings; exit 0
}

if ($Ship -and $DryRun) {
    Step "Ship DRY RUN: nothing is built, published, committed or stopped"
    Write-Host "  would build: $PlaytestName.zip, the win64 server package$(if ($Linux) { ', the linux-x64 server package' }) and the source zip; content guard; smoke test (127.0.0.1, ports 27141-27199)"
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $Notes = Join-Path $OutDir 'RELEASE-NOTES.dry-run.md'
    Write-ReleaseNotes -Placeholder
    Write-Host "  release description (preview): $Notes"
    Write-Host '  ---- "What''s new" ----'
    if ($WhatsNew) { $WhatsNew -split "`n" | ForEach-Object { Write-Host "  $_" } } else { Write-Host '  (none)' }
    Write-Host '  ----'
    Write-Host '  the commands:' -ForegroundColor Cyan
    Write-Host "  $(Format-Command 'gh' (Ship-CreateArgs))"
    if (-not $NotPrerelease) { Write-Host '    (a pre-release: GitHub''s releases/latest link does not move to it; -NotPrerelease publishes a normal release marked latest)' -ForegroundColor Yellow }
    if ($Draft) { Write-Host '  -Draft: no archive and no -Supersede now; after publishing the draft on GitHub run: ship.cmd -ArchiveOnly [-Supersede]' }
    else {
        if ($Supersede) {
            $prev = if (Get-Command gh -ErrorAction SilentlyContinue) { Previous-Release } else { $null }
            if ($prev) { Write-Host "  $(Format-Command 'gh' @('release', 'edit', $prev, '--repo', $Repo, '--notes-file', (Join-Path $OutDir 'SUPERSEDED-NOTES.md')))   (a 'newer build' line on top of $prev)" }
            else { Write-Host '  (-Supersede: no earlier published release)' }
        }
        if ($KeepPrevious) { Write-Host '  -KeepPrevious: earlier releases stay visible' }
        else {
            $hide = if (Get-Command gh -ErrorAction SilentlyContinue) { try { @(Published-Others) } catch { @() } } else { @() }
            if ($hide) { foreach ($t in $hide) { Write-Host "  $(Format-Command 'gh' @('release', 'edit', $t, '--repo', $Repo, '--draft=true'))   (hide the earlier build)" } }
            else { Write-Host '  (no earlier published release to hide)' }
        }
        if ($WhatsNew) {
            Write-Host "  if still on main at $($CommitSha.Substring(0, 7)) with release/NEXT.md unchanged: write release/history/$Version.md, empty release/NEXT.md, then:"
            Write-Host "  $(Format-Command 'git' @('-C', $RepoRoot, 'add', '--', 'release/NEXT.md', "release/history/$Version.md"))"
            Write-Host "  $(Format-Command 'git' @('-C', $RepoRoot, 'commit', '-q', '-m', "release: shipped playtest build $Version ($Tag); What's new archived", '--', 'release/NEXT.md', "release/history/$Version.md"))"
            if ($NoPush) { Write-Host '  (-NoPush: the commit stays local)' }
            else { Write-Host "  $(Format-Command 'git' @('-C', $RepoRoot, 'push', 'origin', '<archive commit>:main'))   (only while origin/main is still $($CommitSha.Substring(0, 7)))" }
        }
    }
    if ($Playtest) {
        $old = Playtest-Procs
        Write-Host "  -Playtest: would stop $($old.Bots.Count) recorded bot(s) and $($old.Servers.Count) playtest server(s), start server\SmServer.exe of the zip from $(Join-Path $dataRoot "tmp-build\playtest-srv-$Version") with $($PlaytestServerArgs -join ' '), and $($PlaytestBots.Count) headless bots from $(Join-Path $RepoRoot 'client')"
    }
    if ($script:ShipProblems -gt 0) { Write-Host "Preflight: $($script:ShipProblems) check(s) would refuse the ship." -ForegroundColor Yellow; Show-Timings; exit 1 }
    Write-Host 'Preflight: every check passed.' -ForegroundColor Green
    Show-Timings; exit 0
}

if (-not $SkipBuild) {
    Time-Step 'clean copy'
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    if (Test-Path -LiteralPath $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Stage | Out-Null
    foreach ($z in $Zips.Values) { if (Test-Path -LiteralPath $z) { Remove-Item -LiteralPath $z } }
    # Packages of an earlier layout of this version (the client zip) would be mistaken for this build's.
    Get-ChildItem -LiteralPath $OutDir -File | Where-Object { $_.Name -match '^(sm-reimpl-client-|SM-Reimpl-Playtest-)' } | Remove-Item -Force
    foreach ($f in $Sums, $Notes) { if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f } }

    # The scanner is part of the build, not an afterthought (Invoke-Scan builds it again right before every scan).
    Step 'Building the content guard (smtool)'
    Run-Native 'dotnet build smtool' { dotnet build (Join-Path $RepoRoot 'src\SmTool\SmTool.csproj') -c Release -nologo -v quiet }

    # Everything shipped is built from a clean `git archive` copy of the commit: files that git ignores (screenshots, captures, caches, stray textures,
    # stale .godot imports under client/) are not in it, so they can never end up in a package or in the embedded Godot pack.
    Step "Extracting a clean copy of $($CommitSha.Substring(0, 7)) (git archive)"
    $SrcCopy = Join-Path $Stage 'src'
    New-Item -ItemType Directory -Force -Path $SrcCopy | Out-Null
    $archiveTar = Join-Path $Stage 'head.tar'
    Run-Native 'git archive' { git -C $RepoRoot archive --format=tar -o $archiveTar $CommitSha }
    Run-Native 'tar -x' { tar -xf $archiveTar -C $SrcCopy }
    Remove-Item -LiteralPath $archiveTar
    $script:Src = $SrcCopy

    # Reproducible, path-free builds: no debug symbols (their paths hold the build machine's user folder) and the source path mapped away.
    $pubProps = @('-p:DebugType=none', '-p:DebugSymbols=false', '-p:SatelliteResourceLanguages=en', "-p:InformationalVersion=$Info", '-p:IncludeSourceRevisionInInformationalVersion=false', "-p:PathMap=$SrcCopy=/sm-reimpl")

    function Publish-Server([string]$rid, [string]$dest) {
        Step "Publishing SmServer ($rid, self-contained)"
        Run-Native "dotnet publish $rid" { dotnet publish (Join-Path $SrcCopy 'src\SmServer\SmServer.csproj') -c Release -r $rid --self-contained true -o $dest -nologo -v quiet @pubProps }
        $deps = Get-ChildItem -LiteralPath $dest -Filter 'SmServer.deps.json' | Select-Object -First 1
        $rm = if ($deps) { [regex]::Match((Read-Utf8 $deps.FullName), 'runtimepack\.Microsoft\.NETCore\.App\.Runtime\.' + [regex]::Escape($rid) + '/([\d.]+)') } else { $null }
        if (-not $rm -or -not $rm.Success) { Fail "Could not read the runtime pack version of the $rid publish (SmServer.deps.json)." }
        $script:RuntimeVersions[$rid] = $rm.Groups[1].Value
    }

    # The launcher: self-contained, single file, compressed (WinForms cannot be trimmed safely, so it is not trimmed). Its version comes from the commit time.
    function Publish-Launcher([string]$dest) {
        Step "Publishing the launcher (win-x64, self-contained single file, version $LauncherVersion)"
        Run-Native 'dotnet publish launcher' { dotnet publish (Join-Path $SrcCopy 'src\SmLauncher\SmLauncher.csproj') -c Release -r win-x64 -o $dest -nologo -v quiet @pubProps "-p:Version=$LauncherVersion" }
        $exe = Join-Path $dest 'SM Reimpl Launcher.exe'
        if (-not (Test-Path -LiteralPath $exe)) { Fail 'The launcher publish produced no "SM Reimpl Launcher.exe".' }
        return $exe
    }
    # The Windows Desktop runtime (WinForms) the launcher carries: its licence next to the .NET runtime's. Missing = fatal, as for the runtime itself.
    function Add-WindowsDesktopLicence([string]$dir) {
        $version = $script:RuntimeVersions['win-x64']
        $packs = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
        $lic = Join-Path $packs "microsoft.windowsdesktop.app.runtime.win-x64\$version\LICENSE"
        if (-not (Test-Path -LiteralPath $lic)) { Fail "The licence of the Windows Desktop runtime pack $version was not found ($lic). Refusing to build a package without it." }
        $target = Join-Path $dir 'licenses\dotnet-windowsdesktop'
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item -LiteralPath $lic -Destination (Join-Path $target 'LICENSE.TXT')
    }

    # ---- server packages ----
    # The win64 server is always built (the playtest zip bundles it as server\); its stand-alone zip unless -SkipServer. The linux-x64 package only with
    # -Linux or -ServerAssets: nothing in the playtest zip or its smoke test needs it.
    Time-Step 'server builds'
    $serverWinDir = Join-Path $Stage 'server-win64'
    $serverLinuxDir = Join-Path $Stage 'server-linux-x64'
    if (-not $SkipServer -or -not $SkipClient) {
        Publish-Server 'win-x64' $serverWinDir
    }
    if ($SkipServer -and $Linux) { Write-Warning '-SkipServer wins over -Linux: no server packages are built.' }
    elseif (-not $SkipServer -and -not $Linux) { Write-Host '  linux-x64 server package: skipped (pass -Linux, or -ServerAssets, to build it)' }
    if (-not $SkipServer) {
        # win64
        $d = Join-Path $Stage 'pkg-server-win64'
        Copy-Item -LiteralPath $serverWinDir -Destination $d -Recurse
        Copy-Text (Join-Path $Template 'Launcher.ps1') (Join-Path $d 'Launcher.ps1')
        Copy-Text (Join-Path $Template 'Server.cmd') (Join-Path $d 'Server.cmd')
        Add-DotnetLicences $d 'win-x64'
        Common-Files $d 'README-FIRST.server.txt' 'server-win64' ($Tokens + @{ PLATFORM = 'Windows 64-bit'; REQUIREMENTS = 'Windows 10/11 64-bit. No .NET installation is needed (self-contained).'
            RUN = 'Server.cmd (finds the Steam install itself; add options after it, for example: Server.cmd --map mp_tdm_09 --mode seize)'; DATADIR = '%LOCALAPPDATA%\sm-reimpl' })
        New-Zip $d $Zips.serverWin "sm-reimpl-server-win64-$Version" @()
    }
    if (-not $SkipServer -and $Linux) {
        Publish-Server 'linux-x64' $serverLinuxDir
        # linux-x64
        $d = Join-Path $Stage 'pkg-server-linux-x64'
        Copy-Item -LiteralPath $serverLinuxDir -Destination $d -Recurse
        Copy-Text (Join-Path $Template 'server.sh') (Join-Path $d 'server.sh')
        Add-DotnetLicences $d 'linux-x64'
        Common-Files $d 'README-FIRST.server.txt' 'server-linux-x64' ($Tokens + @{ PLATFORM = 'Linux x64'; REQUIREMENTS = 'A 64-bit Linux (glibc) with ICU and OpenSSL libraries as .NET needs them. No .NET installation is needed (self-contained).'
            RUN = "tar xzf sm-reimpl-server-linux-x64-$Version.tar.gz (keeps the executable bits), then in the new folder: export SM_GAME_DIR=/path/to/the/game/install ; ./server.sh   (add options after it, for example: ./server.sh --map mp_tdm_09 --mode seize). Paths are case-sensitive; the install folders are lower-case."; DATADIR = '~/.local/share/sm-reimpl' })
        # A tar.gz, not a zip: the mode bits (0755 for server.sh, SmServer and createdump) survive any unpacker.
        Run-Native 'smtool make-tar' { dotnet run --project (Join-Path $RepoRoot 'src\SmTool\SmTool.csproj') -c Release --no-build -- make-tar $d $Zips.serverLinux --prefix "sm-reimpl-server-linux-x64-$Version" --exec 'server.sh,SmServer,createdump' | Out-Host }
    }

    # ---- source (before the playtest zip, which carries it) ----
    Time-Step 'zips'
    Step "Archiving the source (git archive $($CommitSha.Substring(0, 7)))"
    $srcDir = Join-Path $Stage 'pkg-source'
    New-Item -ItemType Directory -Force -Path $srcDir | Out-Null
    Common-Files $srcDir 'README-FIRST.source.txt' 'source' $Tokens
    $prefix = "sm-reimpl-source-$Version"
    $pathspecs = @('.') + @($SourceExclude | ForEach-Object { ":(exclude)$_" })       # $SourceExclude: the one list of paths kept out of the source zip
    if ($SourceExclude.Count -gt 0) { Write-Host "  source zip leaves out: $($SourceExclude -join ', ')" }
    Run-Native 'git archive' { git -C $RepoRoot archive --format=zip --prefix="$prefix/" -o $Zips.source $CommitSha -- @pathspecs }
    # git archive already holds LICENSE and THIRD_PARTY_NOTICES.md; add README-FIRST.txt and VERSION.txt.
    $fs = [IO.File]::Open($Zips.source, [IO.FileMode]::Open)
    $zip = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($f in 'README-FIRST.txt', 'VERSION.txt') {
            $e = $zip.GetEntry("$prefix/$f"); if ($e) { $e.Delete() }
            $e = $zip.CreateEntry("$prefix/$f", [IO.Compression.CompressionLevel]::Optimal)
            $dst = $e.Open(); try { $b = [IO.File]::ReadAllBytes((Join-Path $srcDir $f)); $dst.Write($b, 0, $b.Length) } finally { $dst.Dispose() }
        }
    } finally { $zip.Dispose(); $fs.Dispose() }

    # ---- the playtest zip ----
    if (-not $SkipClient) {
        Time-Step 'launcher publish'
        $launcherExe = Publish-Launcher (Join-Path $Stage 'launcher')
        Time-Step 'client export'
        Ensure-Godot
        Ensure-Templates
        $exportDir = Join-Path $Stage 'client-export'
        New-Item -ItemType Directory -Force -Path $exportDir | Out-Null
        $client = Join-Path $SrcCopy 'client'
        Step 'Building the game (Release) from the clean copy and exporting it with Godot (headless)'
        # MSBuild reads these from the environment, so Godot's own dotnet publish picks them up too.
        $env:InformationalVersion = $Info; $env:DebugType = 'none'; $env:DebugSymbols = 'false'; $env:IncludeSourceRevisionInInformationalVersion = 'false'; $env:PathMap = "$SrcCopy=/sm-reimpl"
        # Godot's C# export wants a classic solution file next to the project; the repo has none (client.cmd builds the csproj), so one is made in the copy.
        $sln = Join-Path $client 'SmClient.sln'
        $madeSln = $false
        try {
            if (-not (Test-Path -LiteralPath $sln)) {
                Run-Native 'dotnet new sln' { dotnet new sln --format sln -n SmClient -o $client --force | Out-Null }
                Run-Native 'dotnet sln add' { dotnet sln $sln add (Join-Path $client 'SmClient.csproj') | Out-Null }
                $madeSln = $true
            }
            Run-Native 'dotnet build client' { dotnet build (Join-Path $client 'SmClient.csproj') -c Release -nologo -v quiet }
            $exe = Join-Path $exportDir 'sm-reimpl.exe'
            # Always import in the clean copy: `dotnet build` already created .godot (mono), so its presence says nothing about the imported resources.
            Invoke-Godot @('--headless', '--path', $client, '--import') 'import' -Lenient | Out-Null
            Invoke-Godot @('--headless', '--quiet', '--path', $client, '--export-release', $Preset, $exe) 'export' | Out-Null
        } finally {
            Remove-Item Env:\InformationalVersion, Env:\DebugType, Env:\DebugSymbols, Env:\IncludeSourceRevisionInInformationalVersion, Env:\PathMap -ErrorAction SilentlyContinue
            if ($madeSln -and (Test-Path -LiteralPath $sln)) { Remove-Item -LiteralPath $sln -Force }
        }
        if (-not (Test-Path -LiteralPath (Join-Path $exportDir 'sm-reimpl.exe'))) { Fail 'The export produced no sm-reimpl.exe.' }
        Get-ChildItem -LiteralPath $exportDir -Recurse -File | Where-Object { $_.Extension -in '.pdb', '.xml' } | Remove-Item -Force

        Time-Step 'zips'
        Step "Assembling $PlaytestName.zip"
        $d = Join-Path $Stage 'pkg-playtest'
        New-Item -ItemType Directory -Force -Path $d | Out-Null
        # The numbered entry points at the top, nothing else: the launcher first, then the scripts.
        Copy-Item -LiteralPath $launcherExe -Destination (Join-Path $d $LauncherTop)
        foreach ($k in $TopFiles.Keys) { Copy-Text (Join-Path $Template $TopFiles[$k].Template) (Join-Path $d $TopFiles[$k].Name) $Tokens }
        # game\: the Godot export and the launchers' helper.
        $game = Join-Path $d 'game'
        Copy-Item -LiteralPath $exportDir -Destination $game -Recurse
        Copy-Text (Join-Path $Template 'Launcher.ps1') (Join-Path $game 'Launcher.ps1')
        # server\: the bundled win64 server (HOST A MATCH and the Host scripts start it).
        Copy-Item -LiteralPath $serverWinDir -Destination (Join-Path $d 'server') -Recurse
        # source\: the GPL source offer of exactly this build.
        New-Item -ItemType Directory -Force -Path (Join-Path $d 'source') | Out-Null
        Copy-Item -LiteralPath $Zips.source -Destination (Join-Path $d 'source')
        # notes\: version, licences and the tester documents, all from the clean copy of the commit.
        $notesDir = Join-Path $d 'notes'
        New-Item -ItemType Directory -Force -Path $notesDir | Out-Null
        Write-Utf8 (Join-Path $notesDir 'VERSION.txt') (Version-Text 'playtest-win64')
        Copy-Item -LiteralPath (Join-Path $SrcCopy 'LICENSE') -Destination (Join-Path $notesDir 'LICENSE.txt')
        Write-Utf8 (Join-Path $notesDir 'THIRD_PARTY_NOTICES.md') (Notices-Text 'playtest')
        Add-DotnetLicences $notesDir 'win-x64'
        Add-WindowsDesktopLicence $notesDir
        # Godot's own licence and third-party copyright texts (the engine is MIT; its COPYRIGHT.txt lists the components inside it)
        $godotLic = Join-Path $notesDir 'licenses\godot'
        New-Item -ItemType Directory -Force -Path $godotLic | Out-Null
        $licSrc = Ensure-GodotLicences
        foreach ($f in $GodotLicenceFiles.Keys) { Copy-Item -LiteralPath (Join-Path $licSrc $f) -Destination $godotLic }
        foreach ($f in 'refine-register.md', 'changes-from-original.md') {
            $p = Join-Path $SrcCopy "docs\$f"
            if (-not (Test-Path -LiteralPath $p)) { Fail "docs/$f is missing from the build commit." }
            Copy-Item -LiteralPath $p -Destination $notesDir
        }
        $kit = Join-Path $SrcCopy 'docs\playtest'
        if (-not (Test-Path -LiteralPath $kit)) { Fail 'docs/playtest (the tester kit) is missing from the build commit.' }
        Copy-Item -LiteralPath $kit -Destination (Join-Path $notesDir 'playtest') -Recurse
        # CONTENTS-SHA256.txt: every file of the extracted folder (but itself), to check an unpacked copy.
        $root = (Resolve-Path -LiteralPath $d).ProviderPath.TrimEnd('\')
        $lines = foreach ($f in (Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName)) { '{0}  {1}' -f (Sha256 $f.FullName), $f.FullName.Substring($root.Length + 1) }
        Write-Utf8 (Join-Path $notesDir 'CONTENTS-SHA256.txt') (($lines -join "`r`n") + "`r`n")
        # Top level: exactly the numbered files and the four folders.
        $top = @(Get-ChildItem -LiteralPath $d | ForEach-Object Name | Sort-Object)
        $want = @(@($LauncherTop) + @($TopFiles.Values | ForEach-Object { $_.Name }) + @('game', 'notes', 'server', 'source') | Sort-Object)
        if (($top -join '|') -ne ($want -join '|')) { Fail "The playtest zip's top level is not what testers are told: $($top -join ', ')" }
        New-Zip $d $Zips.playtest '' @()
    }
}

# ---- guard, sums, notes ----------------------------------------------------------------------------------------------
$built = @($Zips.Values | Where-Object { Test-Path -LiteralPath $_ })
if ($built.Count -eq 0) { Fail "No packages in $OutDir." }
Time-Step 'content guard'
Step 'Content guard (smtool scan-package)'
$scanCode = Invoke-Scan $built
if ($scanCode -ne 0) {
    foreach ($z in $built) { Remove-Item -LiteralPath $z -Force }
    foreach ($f in $Sums, $Notes) { if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f } }
    Fail "The content guard refused the packages (exit $scanCode). They were deleted. Nothing may be distributed."
}

if (-not $SkipBuild) {
    $lines = foreach ($z in $built) { '{0}  {1}' -f (Sha256 $z), (Split-Path -Leaf $z) }
    Write-Utf8 $Sums (($lines -join "`n") + "`n")
    if (Test-Path -LiteralPath $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
}
Test-Sums
Write-ReleaseNotes -WithServerAssets:$ServerAssets

Write-Host ''
Write-Host "Packages in $OutDir" -ForegroundColor Green
foreach ($z in $built) { Write-Host ('  {0,-60} {1,10}' -f (Split-Path -Leaf $z), (Size-Text $z)) }
Write-Host '  SHA256SUMS.txt'
if (Test-Path -LiteralPath $Notes) { Write-Host '  RELEASE-NOTES.md (the release description)' }

# ---- smoke tests -----------------------------------------------------------------------------------------------------
# Only processes started here are stopped: each is found by its executable lying under the smoke folder.
function Stop-SmokeProcesses([string]$under) {
    foreach ($p in (Get-CimInstance Win32_Process -Filter "Name = 'SmServer.exe' OR Name = 'sm-reimpl.exe' OR Name = 'sm-reimpl.console.exe' OR Name = '0 - SM Reimpl Launcher.exe'" -ErrorAction SilentlyContinue)) {
        if ($p.ExecutablePath -and (Is-Inside $p.ExecutablePath $under)) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    }
}
function Free-Port([int]$from, [int[]]$skip = @()) {
    $used = @([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveUdpListeners() | ForEach-Object { $_.Port }) + $skip
    $port = $from
    while ($used -contains $port -and $port -lt 27199) { $port++ }
    if ($used -contains $port) { Fail 'No free UDP port in 27141..27199 for the smoke test.' }
    return $port
}
function Wait-Line([string]$file, [string]$pattern, [int]$seconds, $process = $null) {
    for ($i = 0; $i -lt $seconds; $i++) {
        if ((Test-Path -LiteralPath $file) -and (Select-String -LiteralPath $file -Pattern $pattern -Quiet)) { return $true }
        if ($process -and $process.HasExited) { Start-Sleep -Milliseconds 500; return ((Test-Path -LiteralPath $file) -and (Select-String -LiteralPath $file -Pattern $pattern -Quiet)) }
        Start-Sleep -Seconds 1
    }
    return $false
}

if ($Smoke) {
    Time-Step 'smoke test'
    if (-not (Test-Path -LiteralPath $Zips.playtest)) { Fail 'The smoke test needs the playtest zip (do not combine -Smoke with -SkipClient on a fresh folder).' }
    $smokeDir = Join-Path $OutDir '_smoke'
    if (Test-Path -LiteralPath $smokeDir) { Remove-Item -LiteralPath $smokeDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $smokeDir | Out-Null
    $pkg = Join-Path $smokeDir 'Playtest folder'          # a space in the path, as most testers' folders have
    Expand-Archive -LiteralPath $Zips.playtest -DestinationPath $pkg
    Remove-Item Env:\SM_HOST_LAN -ErrorAction SilentlyContinue      # the smoke test hosts this PC only, whatever the calling shell has set
    $env:SM_HOST_BOTS = '0'                                          # the smoke test checks hosting, not bots (bots are not what this step checks)
    if ($GameDir) { $env:SM_GAME_DIR = $GameDir }
    $profileFile = Join-Path $smokeDir 'profile\customization.json'      # the front end's profile and settings stay in the smoke folder
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $profileFile) | Out-Null
    $fail = $false
    try {
        # 1. The top level is the numbered files and the four folders, and the launchers are where they say.
        $top = @(Get-ChildItem -LiteralPath $pkg | ForEach-Object Name | Sort-Object)
        Write-Host "  top level: $($top -join ' | ')"
        foreach ($p in $LauncherTop, 'game\sm-reimpl.exe', 'game\sm-reimpl.console.exe', 'game\Launcher.ps1', 'server\SmServer.exe', "source\$SourceZipName", 'notes\VERSION.txt', 'notes\LICENSE.txt') {
            if (-not (Test-Path -LiteralPath (Join-Path $pkg $p))) { $fail = $true; Write-Host "  missing: $p" -ForegroundColor Red }
        }
        # 1b. The launcher starts headless and checks the unpacked build against notes\CONTENTS-SHA256.txt (no network, writes nothing).
        $lexe = Join-Path $pkg $LauncherTop
        if (Test-Path -LiteralPath $lexe) {
            Step "Smoke: '$LauncherTop' --selftest"
            $lout = Join-Path $smokeDir 'launcher.out.txt'; $lerr = Join-Path $smokeDir 'launcher.err.txt'
            $lp = Start-QuietProcess -FilePath $lexe -ArgumentList '--selftest' -WorkingDirectory $pkg -StdOut $lout -StdErr $lerr
            if (-not $lp.WaitForExit(120000)) { Stop-Process -Id $lp.Id -Force; $lcode = -1 } else { $lcode = $lp.ExitCode }     # only the process started above
            $ltext = ((Get-Content $lout -ErrorAction SilentlyContinue) + (Get-Content $lerr -ErrorAction SilentlyContinue)) -join "`n"
            Write-Host "    $ltext"
            if ($lcode -ne 0 -or $ltext -notmatch '^launcher selftest: ok') { $fail = $true; Write-Host "  launcher: FAILED (exit $lcode)" -ForegroundColor Red }
            else { Write-Host '  launcher: started headless and verified the unpacked build' -ForegroundColor Green }
            # 1c. The window itself (Windows Forms starts, the first-run panel is drawn): off-screen, no update check (--offline), no shortcuts, its
            #     install folder in the smoke folder; it draws itself into a PNG and closes.
            $lroot = Join-Path $smokeDir 'launcher-root'; $lpng = Join-Path $smokeDir 'launcher-window.png'
            #     On the private test desktop (tools/no-focus-steal: an off-screen window still took the foreground now and then).
            $wp = Start-QuietProcess -FilePath $lexe -ArgumentList '--root', $lroot, '--offline', '--no-shortcuts', '--screenshot', $lpng -WorkingDirectory $pkg `
                -StdOut (Join-Path $smokeDir 'launcher-window.out.txt') -Desktop $TestDesktop
            if (-not $wp.WaitForExit(120000)) { Stop-Process -Id $wp.Id -Force; $wcode = -1 } else { $wcode = $wp.ExitCode }     # only the process started above
            $pngOk = (Test-Path -LiteralPath $lpng) -and ((Get-Item -LiteralPath $lpng).Length -gt 10000)
            if ($wcode -ne 0 -or -not $pngOk) { $fail = $true; Write-Host "  launcher window: FAILED (exit $wcode, picture drawn: $pngOk)" -ForegroundColor Red }
            else { Write-Host "  launcher window: Windows Forms started and drew the first-run panel ($lpng)" -ForegroundColor Green }
        }
        $found = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $pkg 'game\Launcher.ps1') -FindGame
        Write-Host "  game\Launcher.ps1 -FindGame: $(if ($found) { 'found the install' } else { 'nothing (SM_GAME_DIR is used)' })"

        # 2. The Host launcher starts server\SmServer.exe on this PC only (its default), on a free test port.
        $port = Free-Port 27141
        Step "Smoke: '$($TopFiles.HOST.Name)' --port $port (server\, 127.0.0.1 only)"
        $hostCmd = Join-Path $pkg $TopFiles.HOST.Name
        $log = Join-Path $smokeDir 'host.out.txt'; $err = Join-Path $smokeDir 'host.err.txt'
        $hostProc = Start-QuietProcess -FilePath "$env:ComSpec" -RawArguments "/d /c `"`"$hostCmd`" --port $port`"" -WorkingDirectory $pkg -StdOut $log -StdErr $err
        $ready = Wait-Line $log '^smserver on UDP' 120 $hostProc
        if ($ready) {
            Write-Host '  server: started from server\ by the Host launcher' -ForegroundColor Green
            Get-Content -LiteralPath $log | Select-String '^smserver on UDP|^listening on' | ForEach-Object { Write-Host "    $($_.Line)" }
            if (-not (Select-String -LiteralPath $log -Pattern '^listening on 127\.0\.0\.1 only' -Quiet)) { $fail = $true; Write-Host '  server: it did not report the 127.0.0.1 bind' -ForegroundColor Red }
        } else { $fail = $true; Write-Host '  server: did not start' -ForegroundColor Red; foreach ($f in $log, $err) { if (Test-Path $f) { Get-Content $f -Tail 15 } } }

        # 3. The game from game\ joins it as a headless bot.
        if ($ready) {
            Step 'Smoke: game\sm-reimpl.console.exe, headless bot, joins it'
            $cout = Join-Path $smokeDir 'client.out.txt'; $cerr = Join-Path $smokeDir 'client.err.txt'
            $cp = Start-QuietProcess -FilePath (Join-Path $pkg 'game\sm-reimpl.console.exe') -ArgumentList '--headless', '--', '--connect', "127.0.0.1:$port", '--bot', '--bot-full-load', '--quit-after', '45' `
                -WorkingDirectory (Join-Path $pkg 'game') -StdOut $cout -StdErr $cerr
            if (-not $cp.WaitForExit(180000)) { Stop-Process -Id $cp.Id -Force; $timedOut = $true } else { $timedOut = $false }     # only the process started above
            $code = if ($timedOut) { -1 } else { $cp.ExitCode }
            $out = ((Get-Content $cout -ErrorAction SilentlyContinue) + (Get-Content $cerr -ErrorAction SilentlyContinue)) -join "`n"
            Write-Host (($out -split "`n" | Select-Object -Last 8) -join "`n")
            $joined = Select-String -LiteralPath $log -Pattern '^player \d+ (\([^)]*\) )?joined' -Quiet
            # case-sensitive: the runtime's and the engine's own messages; our status lines may say "(0 failed to load)"
            if ($code -ne 0 -or -not $joined -or $out -cmatch 'Unhandled exception|FileNotFoundException|Could not load file or assembly|Failed to load') { $fail = $true; Write-Host "  game: FAILED (exit $code, the server saw a player join: $joined)" -ForegroundColor Red }
            else { Write-Host "  game: exit $code, no load errors, the server saw the bot join" -ForegroundColor Green }
        }
        # 3b. The same exported game as a LEAN bot (the default for hosted bots): it must say it is lean and still join.
        if ($ready) {
            Step 'Smoke: game\sm-reimpl.console.exe, lean headless bot (the hosted-bot default), joins it'
            $lout = Join-Path $smokeDir 'lean.out.txt'; $lerr = Join-Path $smokeDir 'lean.err.txt'
            $before = @(Select-String -LiteralPath $log -Pattern '^player \d+ (\([^)]*\) )?joined').Count
            $lp = Start-QuietProcess -FilePath (Join-Path $pkg 'game\sm-reimpl.console.exe') -ArgumentList '--headless', '--', '--connect', "127.0.0.1:$port", '--bot', '--quit-after', '25' `
                -WorkingDirectory (Join-Path $pkg 'game') -StdOut $lout -StdErr $lerr
            if (-not $lp.WaitForExit(120000)) { Stop-Process -Id $lp.Id -Force; $lcode = -1 } else { $lcode = $lp.ExitCode }     # only the process started above
            $lo = ((Get-Content $lout -ErrorAction SilentlyContinue) + (Get-Content $lerr -ErrorAction SilentlyContinue)) -join "`n"
            $lean = $lo -match 'lean bot'
            $ljoined = @(Select-String -LiteralPath $log -Pattern '^player \d+ (\([^)]*\) )?joined').Count -gt $before
            if ($lcode -ne 0 -or -not $lean -or -not $ljoined -or $lo -cmatch 'Unhandled exception|FileNotFoundException|Could not load file or assembly|Failed to load') { $fail = $true; Write-Host "  lean bot: FAILED (exit $lcode, lean line: $lean, joined: $ljoined)" -ForegroundColor Red }
            else { Write-Host "  lean bot: exit $lcode, lean mode on, the server saw it join" -ForegroundColor Green }
        }
        Stop-SmokeProcesses $pkg
        if ($hostProc -and -not $hostProc.WaitForExit(15000)) { Stop-Process -Id $hostProc.Id -Force -ErrorAction SilentlyContinue }

        # 4. The Play launcher starts game\sm-reimpl.exe (the front end, headless); its HOST A MATCH (--auto-host, this PC only) must find server\ and join it.
        $port2 = Free-Port ($port + 1) @($port)
        Step "Smoke: '$($TopFiles.PLAY.Name)' (front end, headless), HOST A MATCH on 127.0.0.1:$port2"
        $feLog = Join-Path $smokeDir 'frontend.log'
        $env:SM_ENGINE_ARGS = "--headless --log-file `"$feLog`""
        $env:SM_NO_WATCH = '1'      # tools/no-focus-steal: no `start /min powershell` watcher from Play.cmd (its own console could flash)
        $playCmd = Join-Path $pkg $TopFiles.PLAY.Name
        $pp = Start-QuietProcess -FilePath "$env:ComSpec" -RawArguments "/d /c `"`"$playCmd`" --profile `"$profileFile`" --auto-host --host-bots none --host-port $port2 --quit-after 40`"" -WorkingDirectory $pkg `
            -StdOut (Join-Path $smokeDir 'play.out.txt')
        $null = $pp.WaitForExit(30000)
        Remove-Item Env:\SM_ENGINE_ARGS -ErrorAction SilentlyContinue
        Remove-Item Env:\SM_NO_WATCH -ErrorAction SilentlyContinue
        # The evidence: an SmServer.exe from the zip's server\ folder, started by the game with this port and the this-PC-only bind (an exported game
        # flushes its log file only at exit, so the process table is read, not the log).
        $serverDir = Join-Path $pkg 'server'
        $hostedProc = $null
        for ($i = 0; $i -lt 120 -and -not $hostedProc; $i++) {
            $hostedProc = Get-CimInstance Win32_Process -Filter "Name = 'SmServer.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.ExecutablePath -and (Is-Inside $_.ExecutablePath $serverDir) -and $_.CommandLine -match "--port $port2(\s|$)" } | Select-Object -First 1
            if (-not $hostedProc) { Start-Sleep -Seconds 1 }
        }
        $hosted = [bool]$hostedProc
        $localOnly = $hosted -and ($hostedProc.CommandLine -match '--bind 127\.0\.0\.1')
        $game = Get-CimInstance Win32_Process -Filter "Name = 'sm-reimpl.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.ExecutablePath -and (Is-Inside $_.ExecutablePath $pkg) } | Select-Object -First 1
        $gameFromGame = [bool]($game -and (Is-Inside $game.ExecutablePath (Join-Path $pkg 'game')))
        if ($game) { Wait-Process -Id $game.ProcessId -Timeout 90 -ErrorAction SilentlyContinue }     # --quit-after 40
        if (Test-Path -LiteralPath $feLog) { Get-Content -LiteralPath $feLog | Select-String '^host:|game folder' | Select-Object -First 6 | ForEach-Object { Write-Host "    $($_.Line)" } }
        $feText = if (Test-Path -LiteralPath $feLog) { Get-Content -LiteralPath $feLog -Raw } else { '' }
        if (-not $hosted -or -not $localOnly -or $feText -cmatch 'Unhandled exception|FileNotFoundException|Could not load file or assembly') {
            $fail = $true; Write-Host "  front end: FAILED (game\sm-reimpl.exe running: $gameFromGame; HOST A MATCH started server\SmServer.exe: $hosted; this PC only: $localOnly)" -ForegroundColor Red
        } else { Write-Host '  front end: game\sm-reimpl.exe started by the Play launcher; HOST A MATCH found server\ and started it on 127.0.0.1 only' -ForegroundColor Green }
        Stop-SmokeProcesses $pkg

        # 5. MUSOU MODE (fix/musou-local-server): --auto-musou presses the main menu's button; the shipped server\ must start with --content musou (it is built with the
        # added content) on 127.0.0.1 only, and the game must be able to join it (the player joins as soon as the server listens).
        $port3 = Free-Port ($port2 + 1) @($port, $port2)
        Step "Smoke: MUSOU MODE (front end, headless) on 127.0.0.1:$port3"
        $env:SM_ENGINE_ARGS = "--headless --log-file `"$(Join-Path $smokeDir 'musou.log')`""
        $env:SM_NO_WATCH = '1'
        $mp = Start-QuietProcess -FilePath "$env:ComSpec" -RawArguments "/d /c `"`"$playCmd`" --profile `"$profileFile`" --auto-musou --host-port $port3 --quit-after 40`"" -WorkingDirectory $pkg `
            -StdOut (Join-Path $smokeDir 'musou.out.txt')
        $null = $mp.WaitForExit(30000)
        Remove-Item Env:\SM_ENGINE_ARGS -ErrorAction SilentlyContinue
        Remove-Item Env:\SM_NO_WATCH -ErrorAction SilentlyContinue
        $musouProc = $null
        for ($i = 0; $i -lt 120 -and -not $musouProc; $i++) {
            $musouProc = Get-CimInstance Win32_Process -Filter "Name = 'SmServer.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.ExecutablePath -and (Is-Inside $_.ExecutablePath $serverDir) -and $_.CommandLine -match "--port $port3(\s|$)" } | Select-Object -First 1
            if (-not $musouProc) { Start-Sleep -Seconds 1 }
        }
        $musouOk = $musouProc -and ($musouProc.CommandLine -match '--bind 127\.0\.0\.1') -and ($musouProc.CommandLine -match '--content musou')
        $musouGame = Get-CimInstance Win32_Process -Filter "Name = 'sm-reimpl.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.ExecutablePath -and (Is-Inside $_.ExecutablePath $pkg) } | Select-Object -First 1
        if ($musouGame) { Wait-Process -Id $musouGame.ProcessId -Timeout 90 -ErrorAction SilentlyContinue }
        if (-not $musouOk) { $fail = $true; Write-Host "  musou: FAILED (the shipped server did not start with --content musou on 127.0.0.1 after --auto-musou)" -ForegroundColor Red }
        else { Write-Host '  musou: MUSOU MODE started server\SmServer.exe with --content musou on 127.0.0.1 only' -ForegroundColor Green }
    } finally {
        Remove-Item Env:\SM_ENGINE_ARGS -ErrorAction SilentlyContinue
        Stop-SmokeProcesses $pkg
        Remove-Item Env:\SM_GAME_DIR -ErrorAction SilentlyContinue
    }
    if ($fail) { Fail "Smoke test failed (logs kept in $smokeDir)." }
    Remove-Item -LiteralPath $smokeDir -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- publish (owner only) --------------------------------------------------------------------------------------------
if ($Publish) {
    Time-Step 'publish'
    Step "Publish to $Repo"
    if ($Version -match 'dirty') { Fail 'Refusing to publish a -dirty version.' }
    $need = @($Zips.playtest)
    if ($ServerAssets) { $need += @($Zips.serverWin, $Zips.serverLinux, $Zips.source) }
    foreach ($z in $need) { if (-not (Test-Path -LiteralPath $z)) { Fail "Publishing needs $(Split-Path -Leaf $z). Build without -SkipClient / -SkipServer (the Linux package: with -ServerAssets or -Linux)." } }
    # git describe found a tag: v<describe> (no second v if the tag already starts with v and a digit). A bare commit hash: tester-<date>-<short sha>.
    $tag = if ($Version -match '^[0-9a-f]{7,40}$') { "tester-$BuildDate-$($CommitSha.Substring(0, 7))" } elseif ($Version -match '^v\d') { $Version } else { "v$Version" }
    # Re-scan right before the upload; the packages may have been touched since the build.
    $again = Invoke-Scan $need -Rehash          # the last scan before an upload trusts no stored hash
    if ($again -ne 0) { Fail "The content guard refused the packages just before publishing (exit $again). Nothing was uploaded." }
    Test-Sums -Require $need
    Write-ReleaseNotes -WithServerAssets:$ServerAssets
    $again = Invoke-Scan @($Notes) -Rehash          # the description is public too
    if ($again -ne 0) { Fail "The content guard refused RELEASE-NOTES.md (exit $again). Nothing was uploaded." }
    $ghArgs = @('release', 'create', $tag, '--repo', $Repo)
    if (-not $NoDraft) { $ghArgs += '--draft' }
    $ghArgs += '--prerelease'          # tester builds are always marked as a pre-release (owner's decision)
    $ghArgs += @('--title', "sm-reimpl tester build $Version", '--notes-file', $Notes)
    $ghArgs += $need
    if ($DryRun) {
        Write-Host 'DRY RUN, nothing uploaded. The command would be:' -ForegroundColor Yellow
        Write-Host ('gh ' + (($ghArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '))
    } else {
        if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Fail 'The GitHub CLI (gh) is required for -Publish.' }
        & gh @ghArgs
        if ($LASTEXITCODE -ne 0) { Fail "gh release create failed (exit $LASTEXITCODE)" }
        Write-Host "Created $(if ($NoDraft) { 'release' } else { 'DRAFT release' }) $tag in $Repo." -ForegroundColor Green
    }
}

# ---- ship: publish, supersede, archive (the build, the guard and the smoke test above have passed) -------------------------------
if ($Ship) {
    $ErrorActionPreference = 'Continue'      # gh and git report progress on stderr; every step checks its exit code and Fail still throws
    Time-Step 'publish'
    Step "Ship: publishing $Tag to $Repo"
    # Re-scan right before the upload, the description too (it is public).
    Write-ReleaseNotes
    $again = Invoke-Scan @($Zips.playtest, $Notes) -Rehash      # the last scan before an upload trusts no stored hash
    if ($again -ne 0) { Fail "The content guard refused the packages or the description just before publishing (exit $again). Nothing was uploaded." }
    Test-Sums -Require @($Zips.playtest)
    if ((Quiet-Exit { gh release view $Tag --repo $Repo }) -eq 0) { Fail "$Tag appeared in $Repo while building; nothing was uploaded." }
    $create = Ship-CreateArgs
    Write-Host "  $(Format-Command 'gh' $create)"
    & gh @create
    if ($LASTEXITCODE -ne 0) { Fail "gh release create failed (exit $LASTEXITCODE). Check $Repo for a half-made release (gh release delete $Tag --repo $Repo --cleanup-tag) and run ship.cmd again." }
    $url = (& gh release view $Tag --repo $Repo --json url --jq .url | Out-String).Trim()
    Write-Host "Shipped $(if ($Draft) { 'DRAFT ' })$Tag$(if (-not $NotPrerelease) { ' (pre-release)' }): $url" -ForegroundColor Green
    if (-not $NotPrerelease) { Write-Host "  A pre-release: GitHub's releases/latest link does not point at it (only -NotPrerelease releases can be latest)." -ForegroundColor Yellow }
    if ($Draft) {
        Write-Host '  A draft: release/NEXT.md is kept and no other release was edited. After publishing the draft on GitHub run: ship.cmd -ArchiveOnly [-Supersede]' -ForegroundColor Yellow
    } else {
        if ($Supersede) { Invoke-Supersede $url }
        Step 'Ship: hiding the earlier builds'; Hide-PreviousReleases
        if ($WhatsNew) { Time-Step 'archive'; Step "Ship: archiving release/NEXT.md to release/history/$Version.md"; Archive-WhatsNew $url }
    }
}
if ($Playtest) { Time-Step 'playtest'; Start-Playtest }
Show-Timings
exit 0
