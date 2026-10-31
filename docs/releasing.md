# Releasing a playtest build

One command ships the next playtest build to https://github.com/Yeezima/sm-reimpl-releases (owner only). From PowerShell, in the **main checkout**:

```powershell
git switch main; git pull
.\ship.cmd -DryRun      # the checks and every command it would run; builds and publishes nothing
.\ship.cmd              # ship it
```

`ship.cmd` is `tools\release\release.ps1 -Ship`. Every run ends with a table of each step's seconds and the total (also when a step fails), so a slow
step shows itself. Measured on the owner's PC (2026-10-29, warm build caches): `release.cmd -Smoke` about 3.5 minutes, of which the smoke test is
about 2 minutes (its bots and the front end run for fixed times) and the Godot export under half a minute; the ship's `dotnet test` and
`refine-register --check` about 1 minute more.

To ship a hotfix quickly, run the tests first while you write the What's new, then ship the same commit:

```powershell
.\test-and-mark.cmd     # dotnet test + refine-register --check; green on a clean tree = recorded for this commit
.\ship.cmd              # skips the tests it finds recorded for this exact commit within 24 hours (and says so)
```

## Before shipping: release\NEXT.md

`release\NEXT.md` holds the **What's new** of the next build: one `- ` bullet per change a tester can notice, in plain words. Add the bullet in the same
pull request as the change. The text is public, so it is content only: no PR or issue numbers, repository paths or source file names, commit hashes or git talk
(ship refuses such lines; `smtool whats-new release\NEXT.md` shows the text or the problems). Indented continuation lines and `###` headings are fine. Ship puts the bullets at the top of the release description, then moves them to `release\history\<version>.md` and
empties `NEXT.md` in a commit that it pushes to `origin/main`.

The rest of the description (install steps, the file list, the controls table, known issues, the download's SHA-256) comes from one template,
`tools\release\package\RELEASE-NOTES.md`, so every release has it. Change the controls there and in `tools\release\package\README-FIRST.client.txt`
(the zip's `1 - READ ME FIRST.txt`) when a key changes.

## What it does, step by step

| Step | Checks | Stops when |
|---|---|---|
| 1. Preflight | on `main`; clean tree; `HEAD` = `origin/main` (after a fetch); `gh` installed and signed in; the version's tag (`tester-<date>-<short hash>`) not in the releases repo yet; `NEXT.md` has tester-facing bullets; `dotnet test`; `smtool refine-register --check` (both skipped when they passed on exactly this commit within 24 hours: see "Skipping a duplicate test run"). Prints the version, protocol and commit. | any check fails |
| 2. Build | the playtest zip `SM-Reimpl-Playtest-<date>-<version>-Windows.zip` (`0 - SM Reimpl Launcher.exe`, numbered launchers, `game\`, `server\`, `source\`, `notes\`), the win64 server zip and the source zip, from a clean `git archive` of `HEAD`; the linux-x64 server package only with `-Linux` (or `-ServerAssets`) | a build or export error |
| 3. Content guard | `smtool scan-package` over every package (the source zip inside the playtest zip too): no game files, private files, user paths, secrets, personal e-mail addresses or unexpected file types. The install's and the private cache's file fingerprints are kept between runs in `%LOCALAPPDATA%\sm-reimpl\cache\guard\` (below) | any finding: every package is deleted |
| 4. Smoke test | unpacks the zip, `0 - SM Reimpl Launcher.exe --selftest` verifies it, `3 - Host a match on this PC.cmd` starts `server\` on 127.0.0.1 (ports 27141-27199), a headless bot from `game\` joins it, `2 - Play.cmd`'s HOST A MATCH finds `server\` | the server does not start, the bot does not join, a load error |
| 5. Publish | re-scans the zip and the description, then `gh release create` with the zip as the only asset | the guard refuses, the tag appeared meanwhile, `gh` fails |
| 5b. Hide earlier builds | after publishing (not with `-Draft`, not with `-KeepPrevious`): every other published release becomes a draft again, so testers only see this build; nothing is deleted | never stops the ship: a failed hide is a warning |
| 6. Archive | only if the checkout is still `main` at the shipped commit, clean, with `NEXT.md` unchanged since step 1: `NEXT.md` to `release\history\<version>.md` in one commit on top of the shipped commit, pushed as exactly that commit (`git push origin <archive>:main`) only while `origin/main` is still the shipped commit | anything unexpected skips the commit or the push with the steps to finish by hand (the release is out either way) |

## Options

| Option | Effect |
|---|---|
| `-DryRun` | Preflight checks only report ("WOULD REFUSE"), nothing is built, published, committed or stopped; prints the description preview path and every `gh`/`git` command. Exits 1 when a check would refuse. |
| `-Draft` | Creates a draft: check it on GitHub, then press Publish there. `NEXT.md` is kept and no other release is edited; after publishing, run `.\ship.cmd -ArchiveOnly` (add `-Supersede` if wanted) on the same commit. |
| `-ArchiveOnly` | No build: for this version's release, once published, archive `NEXT.md` (and with `-Supersede` point the previous release at it). |
| (default) | Once the new build is published, every other published release is turned back into a draft, so testers only see the current build. Nothing is deleted: publish an old one again on GitHub if needed. `-KeepPrevious` leaves them visible. |
| `-Supersede` | Only useful with `-KeepPrevious` (otherwise the previous release is hidden right after). Puts "A newer playtest build is out" with the new link at the top of the previous published release's description. Older releases are never deleted. |
| `-NotPrerelease` | Publishes a normal release marked **Latest**. GitHub's `releases/latest` link only ever points at a published release that is *not* a pre-release, so without this switch that link does not move (the default stays a pre-release, the owner's earlier decision). |
| `-SkipTests` | Skips `dotnet test` and `refine-register --check` (the guard and the smoke test still run). Without it they are skipped anyway when a green run of both on exactly this commit is recorded from the last 24 hours (below). |
| `-Linux` | Also builds the linux-x64 server package (`sm-reimpl-server-linux-x64-<version>.tar.gz`). Off by default: the playtest zip, its smoke test and a ship need only the win64 server and the source zip. `-ServerAssets` (with `-Publish`) turns it on by itself. Works with `release.cmd` and `ship.cmd`. |
| `-NoPush` | Commits the archived `NEXT.md` but does not push. |
| `-AllowEmptyNotes` | Ships with no What's new section. |
| `-Playtest` | The preflight first checks that UDP 27025 is free or held by the earlier playtest server. After publishing, restarts the local playtest server on the shipped build: the zip's `server\` copied to `%LOCALAPPDATA%\sm-reimpl\tmp-build\playtest-srv-<version>`, `--bind 127.0.0.1 --port 27025 --honor-side-requests --bot-damage-scale 0.34`, and 7 headless bots from this checkout's client (`--headless --bot --no-sound`; marines tactical, devastator, assault; Chaos tactical, havoc, raptor, tactical). Their PIDs go to `tmp-build\playtest-27025-bots\pids.txt` (the server's to `server.txt`). It stops only the bots recorded there (a Godot `--bot` process joining 127.0.0.1:27025, started at the recorded time) and SmServer copies from `tmp-build\playtest-srv-*` on port 27025; if anything else holds 27025 it refuses. The new server must print its ready line and its 127.0.0.1-only line, else it is stopped and the run fails. Also works without shipping: `release.cmd -SkipBuild -Playtest` on a built version. |

Lower-level commands stay available: `release.cmd` (build only), `release.cmd -Smoke`, `release.cmd -Linux`, `release.cmd -SkipBuild -Publish [-NoDraft] [-ServerAssets]`
(`-ServerAssets` needs a build made with `-Linux` or `-ServerAssets`), `release.cmd -Commit <rev>` (re-package an older build). README, "Building a tester release".

## Step timings

Every `release.cmd` and `ship.cmd` run ends with "Timings (done), seconds:", one line per step that ran and the total: preflight, tests, clean copy
(smtool and the `git archive` copy), server builds, launcher publish, client export (the game's build, Godot import and export), zips (the source zip,
assembling and zipping the playtest folder), content guard (with the sums and the description), smoke test, publish (the re-scan, `gh`, hiding the
earlier builds), archive, playtest. A failed run prints "Timings (failed)" with the steps so far before the error. The guard also prints its own seconds
and how many of the install's and the cache's files it had to hash.

## Skipping a duplicate test run

`test-and-mark.cmd` (`tools\release\test-and-mark.ps1`) runs exactly the ship's checks, `dotnet test` and then `smtool refine-register --check`. When both
pass and the tree was clean and at the same `HEAD` commit both before and after the run (no changes, no untracked files; the check lives in
`tools\release\tested-marker.ps1`, `Invoke-ReleaseTests` and `Write-TestedMarkerIfStable`, shared with the ship), it writes
`%LOCALAPPDATA%\sm-reimpl\tmp-build\tested\<full commit sha>` (a small text file: the commit, the UTC time, both checks passed). A ship of that commit within
24 hours prints `skip   dotnet test and refine-register --check: both passed on <sha> at <time> UTC (...)` and goes on. A marker is ignored (the tests run
as before) when it is unreadable, names another commit, lacks a check, is older than 24 hours, or is dated more than 5 minutes in the future (5 minutes of
clock slack are allowed). A ship that runs the tests itself writes the marker after a green run too, under the same rule: when the tree changed or `HEAD`
moved during the run it writes nothing and warns. `test-and-mark.cmd -Check` only reports whether `HEAD` has a valid marker. With uncommitted changes the
checks still run, but nothing is recorded. Delete the folder to force the tests.

## The guard's fingerprint cache

`smtool scan-package --index-cache <dir>` (release.ps1 passes `%LOCALAPPDATA%\sm-reimpl\cache\guard`) keeps, per comparison tree (the game install, the
private cache), each file's relative path, size, last-write time and, once the guard needed it, its SHA-256, in `install-<id>.json` / `private-<id>.json`.
The guard compares a package file with the tree's files of the same size only, so it hashes few of them (36 on the 2026-10-29 build); a stored hash is
reused only when the path, the size and the write time (and for the install the Steam build id from `appmanifest_55150.acf`) are unchanged and the hash
was taken less than 7 days ago (reusing a hash does not renew its date); a changed, new or older file is hashed again, a deleted one is dropped, and a
missing, corrupt, older-format or foreign index file is ignored and rebuilt, so the answer is identical with and without it (tests: `GuardIndexTests`).
The last scans before an upload (`-Publish`, and the ship's re-scan) pass `--rehash`: no stored hash is used there, every needed file is hashed again, and
the index is still saved. An `--index-cache` folder inside the install or inside a comparison tree (other than the private cache's own `guard` folder;
links and junctions resolved to their final path first) is ignored: the guard runs without stored hashes and writes nothing there. A failure to save the
index (a full disk, a file name JSON cannot hold) costs time on the next run, never the guard's answer. Deleting the folder is always safe.

## The launcher (what testers run)

Every playtest zip starts with `0 - SM Reimpl Launcher.exe`: a launcher and updater that installs the build into its own folder, keeps it up to date
from this repository's releases and starts Play, Host and Join. The zip's numbered scripts keep working without it.

- **Built by `release.ps1`** from `src/SmLauncher` (the Windows Forms window) and `src/SmLauncher.Core` (all the logic, engine-free, tested in
  `tests/SmLauncher.Tests` against an in-process fake GitHub and a 127.0.0.1 socket server, never the real API): `dotnet publish -r win-x64`,
  self-contained, single file, compressed, not trimmed (Windows Forms does not support trimming; about 49 MB, the zip grows by about 45 MB). Its file
  version is the build commit's time in UTC, `<year>.<month*100+day>.<minute of the day>`; a local build is 1.0.0. Its runtime licences are in
  `notes\licenses\dotnet\` and `notes\licenses\dotnet-windowsdesktop\`. Not code-signed: SmartScreen warns once ("More info" > "Run anyway").
- **Finding a build:** `GET https://api.github.com/repos/Yeezima/sm-reimpl-releases/releases` without a token (not `/releases/latest`, which skips
  pre-releases): the newest published (non-draft) release by `published_at` with exactly one `SM-Reimpl-Playtest-<date>-<version>-Windows.zip`; other
  assets are ignored; a newer release it cannot offer (two playtest zips, a tag that is no safe folder name: no trailing dot or space, no device name)
  is logged and named in the window. Hiding a build (turning it into a draft) removes it from that list; testers who installed it keep it. When the
  newest published build is older than the installed one, the window says "The current playtest build is ..." and offers to switch to it.
- **Checking it:** before a byte is downloaded the release must state the zip's SHA-256 (GitHub's asset `digest`, the description's "Download check", or
  both, and then both equal: keep that section in `tools/release/package/RELEASE-NOTES.md`); after the download the file must match. A mismatch
  deletes the file and offers a retry. The zip is then unpacked into `versions\<tag>.staging\` (zip-slip safe: an entry that would land outside it, a
  drive, a colon, a device name or a duplicate refuses the whole zip before anything is written), every file is checked against the zip's own
  `notes\CONTENTS-SHA256.txt` (and a program file, `.exe .dll .cmd .bat .ps1 ...`, that the list does not name fails the check), and only then the
  folder is renamed to `versions\<tag>\` and `current.json` replaced (written to a temp file, flushed, renamed over it). Nothing from a zip runs before
  its hash is checked. Free space is checked before the download (the zip's size) and before the unpack (the zip's declared total); a full disk stops
  the update at once with "not enough disk space" (no retries), keeping the previous version and the verified download.
- **The What's new** shown in the window is the top of the description up to the `<!-- whats-new-end -->` comment `release.ps1` writes after it,
  as plain text (older descriptions are cut at their first paragraph).
- **Download:** resumable (`downloads\<zip>.part`, HTTP Range), retried with backoff (2, 4, 8, 16 s; it gives up after 5 failures in a row without a byte
  gained, keeping the part; no answer within 60 s or no data for 30 s counts as a failure), percent, MB, speed, time left and Cancel. Only api.github.com, github.com and *.githubusercontent.com over HTTPS, every
  redirect checked; no token, no telemetry, no port bound, no administrator rights.
- **Dark look** (`SmLauncher/Theme.cs`): warm charcoal #302D2C background with a faint weave drawn as a tile with GDI+ at start-up (no image embedded), light text, flat buttons, amber links and focus, owner-drawn check box, radio button, progress bar and message box (`DarkBox`), and the dark title bar through DWMWA_USE_IMMERSIVE_DARK_MODE. Window screenshots: `--screenshot` with a temp `--root`.
- **Layout** (`SmLauncher/Theme.cs`, `LayoutCheck.cs`): no transparent controls: every label, link, check box and panel paints its own background (the tile or the header's solid colour) and repaints the area it leaves when its text or size changes, so a changing status line cannot leave old glyphs behind. The window is DPI-aware (`AutoScaleMode.Dpi` at 96, PerMonitorV2), wrapping labels take the width of their cell, the install-folder line ends in "..." instead of growing. `"SM Reimpl Launcher.exe" --layout-check <folder>` draws the window off-screen in six states (idle, checking, offered, downloading, error, setup) at 100, 125, 150 and 200 % (simulated), saves the PNGs and fails when two visible controls overlap or one sticks out of its container; no install folder, no network. `--ui-state <s> --ui-scale <f> --ui-check --screenshot <png>` does one. The window is clamped to the screen's work area (above the taskbar) after DPI scaling and centred there; the panels scroll when even that is too small; the check also runs 1366x768 at 100 %, 1920x1080 at 150 % and 2560x1440 at 200 % work areas and the tab order (PLAY first). `--row-check <folder>` (also run by `--layout-check` at 100, 150 and 200 %) is the repaint test: one live window through 22 state changes, each frame compared with a full fresh repaint (PrintWindow against DrawToBitmap), failing on any stale or missing pixels; the safe-mode box's row is saved cropped.
- **Old GPU / safe mode** (hotfix/old-gpu; checkbox under the buttons, saved as `safeMode` in `launcher-settings.json`; rules in `SmLauncher.Core/SafeModePolicy.cs`):
  Godot has no DirectX 9, so an old card (a GTX 960M laptop) runs Godot's Compatibility renderer (OpenGL 3.3). Ticked, PLAY and Join start the game with
  `--rendering-method gl_compatibility --rendering-driver opengl3` before `--` (PLAY passes it through `SM_ENGINE_ARGS`, which `2 - Play.cmd` already reads)
  and `--safe-mode` after it: lighting low, no anti-aliasing, no volumetric fog, SSR, SSIL, SSAO or SDFGI. Automatically: when PLAY's game exits with a
  failure within 8 s the launcher ticks the box itself, says so in plain words and starts the game once more (never a second time); the launcher then
  sets `SM_NO_WATCH=1` so `2 - Play.cmd` skips its own "the game closed" helper. Even without the box the client falls back to the Compatibility renderer
  when Vulkan cannot start (`client/project.godot`), and then switches itself to safe mode. The game's log has one line, `renderer: <method> (<driver>); ...`.
- **Install folder** (default `%LOCALAPPDATA%\SM Reimpl`, the tester may choose another; a non-default one is remembered under
  `HKCU\Software\SM Reimpl Launcher`): `SM Reimpl Launcher.exe`, `current.json` (the active and the previous version), `update.json` (only while an
  update runs: its phase), `versions\<tag>\` (the last two), `downloads\`, `logs\launcher.log` (it holds local paths: testers are told to look it over
  before attaching it). A folder counts as an install only with one of the launcher's own files (`current.json`, `update.json`, `launcher.lock`), and
  the cleanup deletes only the launcher's own names (`<tag>.staging`, `<tag>.replaced`, `<tag>.trash-*`, `<playtest zip>.part`). The first run offers
  to copy in the unpacked zip it was started from (only the files its own list names, then checked; no download) or to download the newest build,
  and creates desktop and Start menu shortcuts.
- **Interrupted updates:** `update.json` records downloading, verifying, extracting or switching. On the next start a partial download resumes, a
  complete one is checked again, a half-unpacked staging folder is deleted and unpacked again, and a switch is finished only after the files are
  hashed again. `current.json` changes only after a full check, so a crash or a power cut leaves the previous version playable; a `current.json`
  that points at a missing or broken version falls back to the previous one.
- **Player data** stays where the game keeps it (`%APPDATA%\sm-reimpl`, caches in `%LOCALAPPDATA%\sm-reimpl`) and is never touched; "Repair: clear
  caches" deletes `<data root>\cache` only (the data root found as the game finds it: `SM_DATA_DIR` when not blank, trimmed, else
  `%LOCALAPPDATA%\sm-reimpl`; refused for a drive root or the user folder, and a `cache` that is a link is not followed), after asking, with the game closed. Files a tester added inside the old version folder come
  along: `game\override.cfg` and small `.cfg`, `.ini` or `.json` files the old build's file list does not name (the new build's own files win). The
  launcher never reads or writes the Space Marine install.
- **Hand-over to the newest launcher** (`SmLauncher.Core/LauncherHandoff.cs`, `Program.TryHandOff`, `MainForm.RelaunchInstalledIfNewer`): a launcher that is not the install folder's `SM Reimpl Launcher.exe` (an old unpacked zip's `0 - SM Reimpl Launcher.exe`) compares its file version with the installed launcher (and the active build's verified one); when one is newer it starts that with the same arguments plus `--handed-off` (the loop guard) and exits. Never with `--selftest`, `--layout-check`, `--root`, `--screenshot`, `--ui-*`, `--offline`, `--auto-update*`, helper flags or `--handed-off` (so the zip's smoke test never hands over). Also after an update installed a newer launcher while this process is an older outside copy: it relaunches the installed one instead of carrying on (before, only the install folder's own launcher restarted; an outside copy kept its old window). Launchers from before this change cannot do it: run any new launcher once.
- **Self-update:** when an installed build carries a launcher that matches the build's file list and is newer (higher file version) than the one in
  the install folder, the window offers to replace it; it never replaces a launcher with an older or equal one (also on the first run). The running
  launcher is never renamed (a single-file program still loads parts of itself from its file: renaming it broke the old one): it starts the verified
  new launcher from the build's folder as a helper (`--install-self <target> --root <root> --wait-pid <pid> --wait-start <start time> --window`) and
  exits (code: `SmLauncher.Core.SelfUpdateHelper`, tested with a fake process host). The helper refuses unless the target is the install folder's
  launcher and it is itself `<root>\versions\<tag>\0 - SM Reimpl Launcher.exe` matching that build's file list; one helper at a time (a named mutex),
  and it holds the install folder's lock for the swap. It waits for the old process (a reused process id, by start time, counts as gone), swaps itself
  in with File.Replace (the old one kept as `.old`; a failed swap is rolled back), runs the new launcher's light `--selftest --rules`, then starts its
  window with `--self-updated <backup>`; that window writes `<exe>.ok` once its first update check is done and removes the backup (only a
  `<exe name>*.old` beside it). A window closed normally (exit 0) before the sign is kept; a crash (non-zero exit, or `<exe>.crashed` from the unhandled
  exception handler) or a failed self-test or swap puts the old launcher back and starts it with a notice. With the window, every way out starts a
  launcher, except when the old process is still running (nothing changed), another helper is at work, or another launcher holds the install folder.
  Checked on a test root both ways; the failure path with the test aid `SM_LAUNCHER_TEST_FAIL_AFTER_UPDATE=1` (the new window exits 3).
- **Buttons and links:** Update / Resume update, PLAY (`2 - Play.cmd`), Host a match on this PC (`3 - ...`), Join a server... (asks for the address and
  starts `game\sm-reimpl.exe -- --connect <address>`), Host for friends (LAN) (`5 - ...`), Check files (hash every file; offers to download a damaged
  build again), Roll back to the previous version (and back again with Update, no download), Open the release page, Report an issue (the Issues tab),
  Open the install folder, Repair: clear caches.
- **Smoke test:** `release.cmd -Smoke` runs `0 - SM Reimpl Launcher.exe --selftest` in the unpacked zip: it must print `launcher selftest: ok` (its rules
  on fixed inputs, then every file of the folder against `notes\CONTENTS-SHA256.txt`; no network, nothing written), then starts the window with
  `--offline --no-shortcuts --screenshot` (install folder and picture in the smoke folder): Windows Forms must start and draw the first-run panel.
- **Support and test commands** (headless, output on the console): `--root <dir>` with `--status`, `--check` (the newest release, its hashes and What's
  new), `--install-latest`, `--resume`, `--verify`, `--rollback`, `--adopt <unpacked folder>`, `--update-launcher` (the active build's launcher into
  the install folder when it is newer; through the helper, with its self-test, when the running launcher is that one). Window test aids: `--screenshot <png>
  [--screenshot-after <s>]` (drawn off-screen, then it closes), `--auto-update`, `--no-shortcuts`, `--offline` (no update check), `--auto-update-launcher` (takes a newer bundled launcher at once; with
  `--screenshot x.png` the new window draws `x.after-update.png`). Use a test root under
  `%LOCALAPPDATA%\sm-reimpl\tmp-build\`, never `%LOCALAPPDATA%\SM Reimpl`.

## When something fails

- **Preflight refuses:** read the line; it says what to do (`git pull`, commit, add bullets, fix a test). Nothing was built.
- **The content guard refuses:** the packages are deleted and nothing was uploaded. The finding names the file and the rule (for example
  `copy-of-private-file`, `absolute-user-path`, `personal-email`). Fix the source of it in the repository (never weaken the guard), merge, ship again.
- **The smoke test fails:** nothing was uploaded. Its logs stay in `%LOCALAPPDATA%\sm-reimpl\release\<version>\_smoke\` (`host.out.txt`,
  `client.out.txt`, `frontend.log`). A port clash is retried on the next free port; a server that does not start usually means the game install
  was not found (set `SM_GAME_DIR`).
- **`gh release create` fails half-way:** look at the releases page; delete a half-made release (below) and run `ship.cmd` again.
- **The push fails** (for example a protected branch): the release is out. Push the archive commit with a pull request instead.

## Rolling back

A hidden earlier build comes back with Publish on its draft on GitHub (or `gh release edit <tag> --repo Yeezima/sm-reimpl-releases --draft=false`).

```powershell
# Hide a release again (back to a draft; the tag and files stay):
gh release edit <tag> --repo Yeezima/sm-reimpl-releases --draft
# Delete it and its tag for good:
gh release delete <tag> --repo Yeezima/sm-reimpl-releases --cleanup-tag --yes
# Remove a "newer build" line that -Supersede added: edit the previous release on GitHub, or
gh release edit <previous tag> --repo Yeezima/sm-reimpl-releases --notes-file <file without the first lines>
```

To put the What's new back for a re-ship: `git revert` the archive commit (it restores `release\NEXT.md` and removes the history file).
