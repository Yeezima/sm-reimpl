# sm-reimpl

An independent reimplementation of the **multiplayer** of *Warhammer 40,000: Space Marine* (2011, PC):
PvP (Annihilation, Seize Ground), with Exterminatus co-op planned. The singleplayer campaign is out of scope.
It loads content from your own legal installation at runtime. The one intentional change is
**server-authoritative multiplayer with dedicated servers**. Everything else aims to match the original.

> **Unofficial fan project.** sm-reimpl is an unofficial, free fan project. It is not affiliated with, endorsed by or
> connected to Relic Entertainment, SEGA or Games Workshop. "Warhammer 40,000" and "Space Marine" are trademarks of their
> respective owners and are used here only to identify the game this project works with. The project ships no game files:
> you need your own copy of *Space Marine* (2011) on Steam (app 55150), which it only reads (read-only, never written to).

This repository contains only new code and documentation. It ships no game files and never modifies your installation.

Testing a playable build? Start with the tester kit: [docs/playtest/README.md](docs/playtest/README.md).
Bug reports go to the Issues tab of [Yeezima/sm-reimpl-releases](https://github.com/Yeezima/sm-reimpl-releases/issues).

## Status

A playable **pre-alpha**. Testers play it from a release zip with a launcher that keeps it up to date.

| Area | State |
|---|---|
| Content reading | Every `.oppc` archive of the Steam build (app 55150) parses; meshes, materials, textures, Havok rigs, animations and behaviour graphs, gameplay data (BAF), effects and Wwise audio are read from your install at runtime |
| Client and server | Godot 4 client (`client.cmd`) with a front end; headless dedicated UDP server (`server.cmd`, Windows and Linux) with client prediction, reconciliation and lag compensation |
| Modes and maps | Annihilation and Seize Ground, Space Marines against Chaos, up to 8 against 8, on Manufactorum (`mp_tdm_09`) |
| Classes and combat | All six multiplayer classes with their weapons, grenades, melee, jump packs, evades, hit reactions and the charge; the original effects and sounds |
| Customizer and perks | Custom-class creator (10 slots) and armour customizer; perks act from the data (36 of 48 so far, 2 of them partly); everything unlocked, no progression yet |
| Playtester builds | One zip with the SM Reimpl Launcher (installs and updates the build), the client, a bundled server and the GPL source |
| Not yet | Other maps and modes, Exterminatus and its AI, levels and unlocks. How close each feature is to the original: [docs/changes-from-original.md](docs/changes-from-original.md) and [docs/refine-register.md](docs/refine-register.md) |

## Prerequisites

- Windows 10/11 with the game installed (Steam app 55150). The Master Crafted Edition (app 3169520) is detected but not supported.
- For `discover.cmd`: nothing extra (built-in Windows PowerShell).
- For `smtool.cmd` and tests: .NET 10 SDK, installed with `winget install Microsoft.DotNet.SDK.10`.
- Later milestones: Godot 4.7.2-stable, .NET build.

## Run

```bat
:: The front end: the Join screen (server, side, loadouts), the custom-class creator and the armour customizer.
:: First run downloads Godot 4.7.2 .NET (official release, SHA-512 checked) into %LOCALAPPDATA%\sm-reimpl\tools.
client.cmd
:: Show the multiplayer Space Marine in original armour, running an original animation (the old default view).
client.cmd --viewer
client.cmd --anim SM_NAV_IdleL_S_01 --bones
:: Multiplayer: start a dedicated server, then connect one or more clients
:: (WASD, Shift to sprint, mouse or the Left/Right arrows to turn and aim, E to pick up a dropped ammo or grenade box, left mouse to fire, R to reload,
::  hold Left Ctrl for aim mode (the original's aim camera and slower look), F for the stun (heavy) strike (with W held: the charge; W + A / W + D: a bit to that side),
::  on the plasma gun, plasma cannon, lascannon and plasma pistol tap left mouse for the normal shot and hold it for the charged shot,
::  the first shot from a lowered weapon waits for it to come up (0.2 s rifles, 0.167 s pistols),
::  R also detonates launcher grenades,
::  Space to evade in the stick direction (Tactical rolls, Devastator and Havoc hop); with a jump pack (Assault, Raptor)
::  Space jumps instead (hold for height; melee in the air pounds),
::  right mouse (or V) for melee, G to throw a grenade (--equipment frag|blind; the files call the blind grenade krak, which is accepted too), 1 pistol, 2 primary, X or the mouse wheel to toggle weapons; --class tactical|assault|devastator|raptor|havoc;
::  --primary/--pistol/--melee <name> pick from the class's choices, e.g. --primary "plasma gun"; the HUD shows heat).
::  F1 shows or hides the info overlay (status, controls, provenance notes; --show-info starts with it shown).
::  The key bindings are from knowledge of the original's PC defaults; they are not in the data read.
:: Teams: --side marines|chaos (the server may balance you onto the other side), Tab shows the scoreboard.
:: Server modes: server.cmd --mode annihilation|ffa --score-limit 41 --time-limit 10 --friendly-fire off
:: Test bots: --bot, --bot-fire (fire without a target), --bot-loadout random, --aim-test (bot holds aim mode every other 5 s), --bot-slot pistol|primary (bot presses that slot key once after joining), --bot-stand (with --aim-test: stand, walk and strafe while aiming).
::  On a map the bots walk A* paths on a navigation grid built from the map's collision (cached in %LOCALAPPDATA%\sm-reimpl\cache\nav);
::  --bot-goal <point> walks to one point and holds it, --bot-route <p1,p2,...> loops through points (x,y pairs or A, B, C, marines, chaos, centre, spawnN);
::  bot logs get a "nav:" line every 5 s (path length, progress, plans, stuck count).
server.cmd --port 27015 --map mp_dlc2_map4
client.cmd --connect 127.0.0.1:27015
:: Zone indicators (the Seize Ground point columns, ground-pound and landing rings and spawn-room shield planes) are hidden by default since they are not in the original; --show-markers shows them for testing.
:: Movement aids: --bot-gait (a bot that walks every gait in turn), --no-locomotion (the old single run clip), --pan-rate <deg/s>, --yaw-probe and --no-view-smoothing (measure and compare the own model and camera smoothness: yaw, position).
:: Capture aids for footage: --no-markers hides the test rings, name tags and placeholder blasts (and wins over --show-markers) (the HUD stays; --no-hud hides it),
::  --spectate inspect orbits the own weapon (also overview|action|point:<letter>|shot|cam:x,y,z,tx,ty,tz).
:: Movie Maker at a chosen size (a copy of the client project gets that viewport size; default 1920x1080; the client args follow):
movie.cmd --movie-size 1080x1920 --write-movie "%LOCALAPPDATA%\sm-reimpl\cache\clip.avi" --fixed-fps 30 --connect 127.0.0.1:27015 --no-markers --no-hud --quit-after 5
:: One static model from a multiplayer map instead:
client.cmd --archive data/chunks/mp_dlc2_map4__wrld.oppc --mesh Geo_Statue_LODH

:: 0. Shortcut: runs steps 1-2 plus sample extraction and writes m1-report.md.
m1.cmd

:: 1. Read-only inventory. Finds the game through Steam automatically.
discover.cmd
::    If it can't find the game:  discover.cmd -GameDir "D:\path\to\game"
::    Report goes to %LOCALAPPDATA%\sm-reimpl\discovery\<timestamp>\report.md

:: 2. Verify every archive (parse, full decompress, size cross-checks, timings)
smtool.cmd verify-all "D:\path\to\game"

:: 3. Other archive commands
smtool.cmd list "<archive>.oppc"
smtool.cmd extract "<archive>.oppc" "<entry path>" "%LOCALAPPDATA%\sm-reimpl\cache"

:: 4. Sound (Wwise packages in streamingaudio\; decoded audio goes to %LOCALAPPDATA%\sm-reimpl\cache\audio\;
::    the first decode downloads ww2ogg's codebook library, hash-checked, to ...\cache\audio-codebooks\)
smtool.cmd audio-list "D:\path\to\game\streamingaudio\weapon_bolter.pck"
smtool.cmd audio-event play_sm_bolter_fire
smtool.cmd audio-decode play_sm_bolter_fire

:: 5. Behaviour graphs of the marine rig (read-only; game folder found as for the client): list, one graph's tree,
::    transitions, bone masks and sequencers, the hip-fire data per weapon family
smtool.cmd graph-dump --list
smtool.cmd graph-dump SM_Bolter_NAV --clips "*Run_F*"
smtool.cmd graph-dump --hipfire

:: Tests (synthetic fixtures only)
dotnet test
```

`extract` refuses to write anywhere that overlaps the game folder (found by walking up from the archive to the folder
holding `SpaceMarine.exe` or `steam_appid.txt`). Keep the cache outside this repository.
Entry paths can repeat inside an archive; pass `#<index>` (the first column of `list --csv`) to pick one exactly.

## Linux and containers (dedicated server, preparation)

The dedicated server and `smtool` are plain .NET 10 and run on Linux (the Godot client and the `.cmd` / `.ps1` tools are Windows-first).
The server still needs a player's own install of the game, mounted or copied read-only; it is never part of this repository or an image.

```sh
export SM_GAME_DIR=/path/to/the/game/install     # the folder that holds "data"; read only, never written
./server.sh --bind 127.0.0.1 --port 27961 --map none          # same options as server.cmd
./smtool.sh list "$SM_GAME_DIR/data/chunks/mp_tdm_09__wrld.oppc"
```

- `server.sh` and `smtool.sh` are POSIX twins of `server.cmd` and `smtool.cmd` (`dotnet run --project ... -c Release -- "$@"`).
- `SM_DATA_DIR` moves the private data and cache root (default `%LOCALAPPDATA%\sm-reimpl` on Windows, `~/.local/share/sm-reimpl` on Linux). It must lie outside `SM_GAME_DIR` (and outside the repository); the output guards refuse anything inside the install.
- Linux paths are case-sensitive; the install's folders are lower-case, as the code expects.
- Settings come from the environment (`SM_GAME_DIR`, `SM_DATA_DIR`, `SM_STEAM_DIR`) or the git-ignored `config/local.json`.
  All variables: `config/local.example.json`.
- `Dockerfile`: `docker build -t smserver .`, then `docker run --rm -p 27015:27015/udp -v <install>:/game:ro smserver --map mp_tdm_09`.
  The image has no game files and no config. Not yet built or deployed anywhere.

## Building a tester release

The owner ships a playtest build with one command, `ship.cmd` (preflight, build, guard, smoke test, publish, the What's new from
`release/NEXT.md`); `ship.cmd -DryRun` shows every check and command first. The whole process, its options and the rollback: [docs/releasing.md](docs/releasing.md).
The pieces underneath:

```bat
release.cmd                  :: builds the playtest zip, the win64 server zip and the source zip locally; publishes nothing; prints each step's seconds
release.cmd -Smoke           :: ... and runs the playtest zip as a tester would, headless, on 127.0.0.1 only (ports 27141-27199)
release.cmd -Linux           :: ... and the linux-x64 server package too (-ServerAssets implies it)
test-and-mark.cmd            :: dotnet test and refine-register --check; green on a clean tree = ship.cmd of this commit skips them for 24 hours
release.cmd -Commit <rev>    :: the programs of an older commit in this tooling's layout (re-packaging a published build)
release.cmd -PrepareOnly     :: only fetch Godot and its export templates
```

Output: `%LOCALAPPDATA%\sm-reimpl\release\<version>\` (never inside the repository or the game install). Testers get **one zip**,
`SM-Reimpl-Playtest-<date>-<ver>-Windows.zip`, whose top level holds only the numbered entry points
(`0 - SM Reimpl Launcher.exe`, the launcher and updater that keeps testers up to date (`src/SmLauncher`; not code-signed, so Windows SmartScreen warns
once; [docs/releasing.md](docs/releasing.md), "The launcher"), `1 - READ ME FIRST.txt`, `2 - Play.cmd` (the front end), `3 - Host a match on this PC.cmd`, `4 - Join a server by address.cmd`,
`5 - Host a match for friends (LAN).cmd`) and four folders: `game\` (the Godot Windows export, self-contained .NET, and `Launcher.ps1`),
`server\` (the win64 SmServer; the front end's HOST A MATCH finds it beside `game\`), `source\` (`sm-reimpl-source-<ver>.zip`, the GPL source offer)
and `notes\` (`VERSION.txt`, `LICENSE.txt`, `THIRD_PARTY_NOTICES.md`, `licenses\`, `changes-from-original.md`, `refine-register.md`, the tester kit
`playtest\` and `CONTENTS-SHA256.txt`). The names live in `$TopFiles` in `tools/release/release.ps1`; the templates (`tools/release/package/`) refer to them
by token (`@PLAY@`, `@HOST@`, ...). Next to it, for server hosts: `sm-reimpl-server-win64-<ver>.zip`, `sm-reimpl-server-linux-x64-<ver>.tar.gz`
(only with `-Linux` or `-ServerAssets`; self-contained `dotnet publish` of `src/SmServer`, `server.sh`; a tar.gz so the executable bits survive) and `sm-reimpl-source-<ver>.zip`, each with
`LICENSE`, `THIRD_PARTY_NOTICES.md`, `README-FIRST.txt` and `VERSION.txt`; `SHA256SUMS.txt` over all of them; and `RELEASE-NOTES.md`, the release
description for testers (`tools/release/package/RELEASE-NOTES.md`: install steps, the controls table, known issues, the zip's SHA-256).

- The version is `git describe --tags --always --dirty` of the build commit, plus the protocol version and the commit's date. A dirty tree is refused
  unless `-AllowDirty`. The client reads it from `client/BuildInfo.cs` (assembly informational version; nothing displays it yet).
- `-Commit <rev>` builds the programs, the source zip and the shipped docs from that commit, and everything else (scripts, readmes, the release
  description, the content guard) from the current checkout: that is how a published build is re-packaged in a newer layout.
- First use downloads Godot's export templates (about 1.2 GB, SHA-512 checked against the official `SHA512-SUMS.txt`) and installs
  the Windows ones into `%APPDATA%\Godot\export_templates\4.7.2.stable.mono`; Godot only ever runs `--headless`.
  The Linux server needs the linux-x64 .NET runtime pack from NuGet the first time.
- The client and the servers are built from a clean `git archive` copy of the commit under the release folder, so files that git ignores (screenshots,
  captures, caches, stray textures, stale `.godot` imports under `client/`) can never reach a package or the embedded Godot pack.
- The content guard (`smtool scan-package`, `src/SmTool/PackageScanner.cs`, tested with synthetic files) runs over every package and
  deletes them all when it finds game-content extensions (`.oppc .bnk .wem .pck .hkt .hkx .dcm .bmat .dds .fx .baf .o3d .Layer .ppk .bin`),
  game file magic (OBPK, AKPK, BKHD, DDS, Havok), such files listed inside the embedded Godot pack, a file identical to one of the install or
  the private cache, `config/local.json`, absolute user paths, webhook URLs and tokens, the exact secret values of `config/local.json`,
  `<data root>/settings.json` and the webhook URLs and tokens set in the environment, personal e-mail addresses, or (client and servers) a file type outside
  a short allowlist. Zips inside a package are scanned entry by entry (`outer.zip!/inner/path`); in the playtest zip, `--source-archive source/` lets the
  source zip through the allowlist and scans its files as the stand-alone source zip is scanned. It fails closed: the game install must be indexed
  (`--require-install`), the cache is indexed when it exists, an unreadable secrets file, forbidden file or nested zip is an error (exit 2), not an empty comparison.
  The fingerprints of the install's and the cache's files (path, size, write time, SHA-256; the install's also by Steam build id) are kept in
  `%LOCALAPPDATA%\sm-reimpl\cache\guard\` (`--index-cache`), so an unchanged file is not hashed twice; the answer is the same without them.
- `-Smoke` unpacks the playtest zip into a folder with a space in its name and runs it as a tester would: `3 - Host a match on this PC.cmd --port <p>`
  must start `server\SmServer.exe` bound to 127.0.0.1, `game\sm-reimpl.console.exe --headless` must join it as a bot, and `2 - Play.cmd` (with
  `SM_ENGINE_ARGS=--headless --log-file ...` and `--auto-host`) must start `game\sm-reimpl.exe`, whose HOST A MATCH finds `server\` and starts it on 127.0.0.1.
  Only processes whose executable lies in the smoke folder are stopped.
- `-Publish` is for the owner only: after a fresh scan it runs `gh release create <tag> --repo Yeezima/sm-reimpl-releases --draft --prerelease`
  (`<tag>` = `v<git describe>` when a tag exists, else `tester-<commit date>-<short sha>`; the date is the commit's, so a later `-SkipBuild -Publish` gives the
  same tag; other repositories need `-AllowOtherRepo`) with the playtest zip as the only asset and `RELEASE-NOTES.md` as the description (`-ServerAssets` adds
  the two server packages and the source zip, with their SHA-256 in the description; `-NoDraft` publishes directly, still as a pre-release; `-DryRun` /
  `-WhatIf` only prints the command). Nothing is uploaded without it. To re-package a published release: build it with `-Commit`, then
  `gh release upload <tag> <zip> --clobber`, `gh release delete-asset` for the old assets and `gh release edit <tag> --notes-file RELEASE-NOTES.md`.
- `$SourceExclude` at the top of `tools/release/release.ps1` lists the paths left out of the source zip to keep it small.

## Layout

| Path | Contents |
|---|---|
| `src/SmFormats` | Format readers (engine-independent .NET library) |
| `src/SmTool` | Command-line archive inspector |
| `tests/` | Parser tests with generated synthetic fixtures |
| `tools/discover.ps1` | Installation inventory |
| `docs/discovery.md` | Inventory, format findings, tool compatibility, sources |
| `docs/architecture.md` | Stack, content boundary, simulation and networking design |
| `docs/roadmap.md` | Milestones and acceptance criteria |
| `spec/behavior.md` | Gameplay parameters and their provenance |
| `docs/changes-from-original.md` | How this reimplementation differs from the original |

## License

Copyright (C) 2026 Luna Studios. The code, documentation and the maintainer's loyalist voice pack in this repository are licensed under the GNU General Public License v3.0 (see `LICENSE`), except the third-party parts named in `THIRD_PARTY_NOTICES.md`, which keep their own licences.
This license grants nothing for the game's content, which stays with its owners.

## Legal boundary

No original assets, extracted data, executable code, or proprietary source belong in this repository or in any build.
Users supply their own copy of the game.
