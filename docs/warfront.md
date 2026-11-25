# Warfront

Warfront is the third mode of this project, beside Classic PvP (Annihilation, Seize Ground) and Musou Mode: a 30 to 60 minute solo or four-player co-op campaign
built on Musou Mode's battle (its Orks, classes, Dreadnought, Librarian, hub and the Ashfall band generator). It is **added content** (`--content warfront`,
flag 32; `docs/added-content.md` section 11) and not part of the original game: every rule below is the owner's brief or a tagged estimate in
`spec/behavior.md` ("Added content: Warfront ...") and `docs/changes-from-original.md`. Musou and Classic are unchanged (pinned by the tests in
`tests/SmNet.Tests/Extras/Warfront/Pins`). Test evidence: `docs/warfront-test-report.md`. Owner's playtest notes: `docs/warfront-playtest.md`.

## The campaign as built

- **11 slots.** Nine lane nodes `wf_a1 .. wf_c3` (lane A, B, C; node 1 wasteland, node 2 outskirts, node 3 urban core; the pilot's band profiles, one fixed seed
  per slot, listed in `WarfrontSlots`), the Imperial HQ `wf_hq` (Helm's Deep style: approach ramp, wall and gateway W, keep K, caves V) and the Ork fortress
  `wf_fortress` (walled compound, arena plaza, one Warboss). Every lane node has the same objectives: capture A, B and C, then D appears at the north-centre
  site where Musou's Warboss stood; securing D wins the node. The Warboss never spawns on the nine nodes.
- **Fronts.** Each lane starts at node 1 (a floor: until the lane has been won once a loss cannot push it below node 1). A won node moves its lane one node
  up; a wiped node moves it one back and returns the party to the Thunderhawk. At the end of every completed lane deployment each *other* lane rolls once,
  50/50 (deterministic from the run seed, the deployment number and the lane): a win holds, a loss moves it one back. Nothing rolls on the ship, after the
  HQ defence or after a fortress attempt. A lane that falls from node 1 after being advanced is a breakthrough: the next deployment must be the HQ defence.
- **HQ defence.** Survive 8 minutes holding W, then K, then V. A win resets all three fronts to node 2 with no roll and keeps everyone's progression; a loss
  ends the run.
- **Fortress.** Any cleared lane (node 4) unlocks it. Killing the one Warboss wins the campaign at once (no hold, no extraction). A wipe there drops the
  cleared lane back and the others roll.
- **End of a run.** One leaderboard on a win and on a loss (one row per player key, disconnected contributors marked, a player part and a squad part, the
  squad's top named retainer, a transparent score formula in `WarfrontScoreWeights`), a Discord-ready text copy (fixed width; written to the run folder's
  `exports\`, no posting), the record archived under `records\`, then "New campaign" starts empty (level 1, one retainer, no cards; nothing is read from
  `records\`).
- **Allied pair.** Musou's two allied Marines only in a solo campaign (human cap 1) and only in lane battles; none in the HQ defence, the fortress or co-op.

## Co-op, leadership, joining

Up to four humans on a server started by hand. Identity is a random campaign key kept in `%LOCALAPPDATA%\sm-reimpl\warfront\client.json` (or the file
`--warfront-key-file` names). The earliest-joined *connected* player is the leader and chooses the deployment; leadership passes to the next oldest on a
leave and returns to the first when it returns (join order is kept for the run). Only the leader's deploy is accepted. The local campaign (the front end's
Warfront row) starts this PC's server bound to 127.0.0.1 with a cap of one human; when its client quits the server stops (leadership handoff means
something only on a standalone server; no host migration is claimed). A returning player gets their own saved XP, cards, pending picks, roster and stats. A
first-time joiner during a battle spectates (Classic's spectator camera) until the next ship stop; then the customizer must be opened and closed and the
player may deploy. Since codebase fix 1 (2026-11-22) a first-time joiner with no progress file starts at level 1 (the median-XP catch-up and its cards are removed); a player who
brings a progress file from their own PC joins with that level (the host sanitizes it, max level 50, and the better of the two copies wins). Warfront needs this build on both sides:
`WarfrontRules.Version` 21 and `WarfrontWire.Format` 2, so older builds cannot join.

**ONLINE WARFRONT row (slice S5, owner 2026-11-19; shown since the 2026-11-20 ship).** The project's VM is to host one shared, always-on Warfront campaign on UDP 27016 (up to 4 players drop in and out). The
Warfront page gets a row ONLINE WARFRONT after JOIN A FRIEND: it sets the Warfront profile (as the friend join does) and joins the public Classic server's host (`JoinTargets.PublicServer`, never a second
written address) on port 27016 with nothing to type (`WarfrontOnline.Address`, `TryJoin(..., remember: false)`). **The gate is ON since the 2026-11-20 ship** (it was off until the VM's campaign was live): `WarfrontOnline.EnabledByDefault = true` (client/Extras/Warfront/FrontEnd/WarfrontOnline.cs), so every
tester sees the row. Test aids: environment `SM_WF_ONLINE=1` shows the row in a normal client, and the
self-test forces it with `WarfrontOnline.ForceForTest`. A refused join (another protocol, game files or config) shows the handshake's plain-words text on the menu (`ConnectFailure.RejectText`); a server that
never answers shows "The online campaign is not reachable right now. Try again later." (the 10 s connect timeout, no hang).

## XP, cards, scaling, squads

- Personal XP from the player's own and squad kills; shared once per event: elite, boss, captures. XP to the next level 1500 + 500 per level; a pick
  queues until a lull or the ship; the picker makes the player invulnerable and idle without pausing the others; every card on offer (1 to 6 or D-pad; owner 2026-11-18): all
  five stat cards (attack speed, max HP, damage, ability cooldown, max armour), and a sixth, +1 Retainer, only for a squad-capable class with room (the Dreadnought
  sees five); nothing is drawn. A level-up refills health
  and armour. Retainers take one third of their player's stat bonuses, and (retainer AI slice S1, owner 2026-11-19) their damage and health also grow with the player's level on the Orks' curve (damage +5 %, health +8 % per level above 1: level 10 is x1.45 and x1.72).
- Progression persists (codebase fix 1): level, XP, picks, cards and extra retainers belong to the player, not the run, and survive a lost save, an update, a win, a defeat and a new campaign; see "Saves and progress". Max level 50. Because Orks scale with the highest player level, a fresh run with a high-level player starts harder.
- Ork health and damage at spawn scale with the highest player level (HP x (1 + 0.08 (L-1)), damage x (1 + 0.05 (L-1))) and HP x (1 + 0.2 (humans-1)); living
  Orks are never changed. All numbers are estimates. Per-level table with times to kill: `smtool.cmd warfront-tuning` (copied into the test report).
- Each eligible player owns a roster of up to nine retainers (36 at four players), mirroring the player's class, weapons, perks, armour and colours; names
  and service records survive loadout changes. Dreadnought: no active retainers (the roster is held until he takes a squad class; a class change swaps the whole roster in place, names and records kept, and takes
  effect at the next landing). Librarian: a squad class option (Assault/Raptor, Tactical, Devastator/Havoc), and the whole group is ONE random loadout of that class
  (seeded by run seed, key and selection), never a mixed squad. An Assault/Raptor retainer fires its pistol at range, strikes within 2.5 m and uses its real jump pack (the client
  hangs the pack on the Marine). The retainers run the lightweight squad sim, not 36 World puppets (the protocol holds 16 players and the puppet ids 12). Their decisions follow Musou's
  `MarineBrain.PlanRetainer`: Follow keeps the owner within 4.5 to 6.5 m (steps in beyond, backs off inside, sprints beyond 12 m with no enemy), Hold stands within 1 m of its place,
  Attack walks to the marked place until within 8 m, then engages (guns to 12 m, blades to 1.5 m); only Attack chases; the target is the first in sight of the 4 nearest enemies
  (20 m Follow, 25 m Hold, 40 m Attack); a sprint fires nothing. Differences that remain (ours): one aggregate weapon profile per Marine (range, damage, rate and hit chance, no
  recoil or ammunition) instead of the real gun; the aim error is the hit chance, not +-2 degrees; squad pathing (one shared path per squad) instead of each Marine's own way; squad-mates
  revive each other by the squad's rules; Assault and Raptor leap by their packs (follow, pound), which Musou's retainers never do. Orders as in Musou; Assault squads jump and pound; **J** marks a jump-pound for a jump-pack player. A Marine is downed for 20 s and is
  revived by anyone; out for that battle only if not. Strength table (estimates): `tests/.../Wiring/SquadStrengthTable.cs`, copied into the test report.
- A fallen human lies downed 25 s, is revived by a teammate or retainer, otherwise spectates for 60 s more, then returns at the landing zone while a
  teammate or retainer stands (a wipe when nobody does).

## Melee cleave

Owner 2026-11-18, Warfront only, for human players and their retainers (not allied Marines, Guardsmen or contraption swings): every melee weapon cleaves 20 % farther than Musou Mode's war gives it, the Dreadnought 40 % farther (in place of the 20 %),
with no cap on the enemies one swing strikes: an Ork is struck whenever his collision (his radius from the attacker's feet) overlaps the cleave volume. The server
applies it through the `WarfrontCleave` hook of `World.MusouCleave` (`Extras/Warfront/Battle/World.WarfrontCleave.cs`; it acts only while the run's Warfront object
exists); the client's drawn arc and predicted targets read the same scale. Classic PvP and Musou Mode keep their numbers. Numbers: spec "Warfront melee cleave".

## The screens' look (2026-11-20)

Every Warfront screen is in the style of the main menu, Options, Classic PvP and the Armoury (owner's request of 2026-11-20; spec "Warfront UI pass"). The WARFRONT page (`WarfrontPage`, a `ConceptPanelPage`): the shared frame, a status strip (DEPLOYMENTS, FRONTS, LEVEL; dashes with no run; the honor roll's counts are its hover hint), one centred column of plates (NEW CAMPAIGN or STOP SERVER, HOST FOR FRIENDS, JOIN A FRIEND, ONLINE WARFRONT when its gate is on, CUSTOMIZER) and the gold CONTINUE or REJOIN CAMPAIGN plate in the frame's tray; PREPARING THE BATTLEFIELD uses the same frame. In the match and on the ship `WarfrontStyle` is the one look (concept lettering, the size table, the scoreboard's painted pieces); explainer text is a hover hint (`UiTooltip`), while errors, refusals, progress lines and counts stay. The Warfront self-tests run the UI text audit; the HUD readouts and the spectate keys legend (`F: NEXT VIEW ...`, a key legend, not a sentence) stay on screen.

## Guardsmen: twice the numbers, the Orks' favour, defence in depth

Owner 2026-11-20 ("add more guardsmen, make them fight in a 'defense in depth' style where there are grids of squads who cover each other from behind. buff guardsmen-ork balance to be 30-60 ork
favored"; answered: the Orks win about 2 of every 3 even fights, Guardsmen at about half the Orks' strength; double today's numbers; in every Warfront battle that has Guardsmen). Warfront only
(`WarfrontRules.Version` 17); Musou Mode and Classic keep their Guardsmen (the restore switch for a before row is the environment variable `SM_WF_GUARD_OLD=1`). Code:
`Extras/Warfront/Battle/WarfrontGuardRules.cs` (every number), `TfwBattle.WarfrontGuards.cs` (the formation and the hooks), `World.WarfrontGuards.cs`; spec "Warfront Guardsmen".

- **Numbers x2.** Field squads 6 to 12, a battle's squads 4..12 to 8..24 (reserves refill the field), the fortress's field 12 to 24, the HQ defence's posts 2, 1, 1 to 4, 2, 2 squads, the garrison cap
  200 to 400 a point and the reinforcements 60 to 120 every 60 s. The sim's 40 squads and the wire's 448 Guardsman ids are unchanged: the armies' real squads are capped at 40 less the field's own.
- **Balance.** A Guardsman's health and shot damage are Musou's times 0.95 (the Orks are untouched), and the aggregate garrisons' worth in the far clash is the square of that. On the bounded trial
  `WarfrontGuardBalanceTests` (60 Guardsmen on a point against 60 Orks of a crowd's mix, 30 seeded fights, 300 s at most) Musou's Guardsmen hold 14 of 30; Warfront's 9 of 30 (the Orks win 70 %).
  The HQ's hold rule counts a Guardsman for 1 over `NumberScale`, so twice the men weigh what the old squads did.
- **Defence in depth (lane battles and the fortress; the HQ keeps its own posts).** The squads of each secured point stand in rows facing the Orks' approach: up to 4 squads a row 7 m apart, rows 6 m apart,
  the front row at 0.5 of the point's radius toward the approach, every second row shifted half a squad so its men fire between the front's men (a Guardsman's shot is a roll on a line of sight, never stopped by a
  friend: the gaps keep the line open and show the cover). The approach is the Orks within 70 m of the point (at least 6, their centre) and else the bearing of the point's Ork source, turned only past 30 degrees.
  A squad with 3 men or fewer falls back to the rear row and the squad behind it steps up; a fallen squad's place is taken by the next row. A slot is pulled toward the point until it is on the walking graph,
  reachable in a straight line from the point and 2.5 m off walls where the ground allows it. A squad within the radius plus 14 m counts as on its point. Test aid: `smtool.cmd warfront-guards [--slot wf_a1] [--seconds 240] [--frames file.rgb] [--frame-point 0..2]`
  (a lane battle in-process on the real map and graph: Guardsmen, tick cost, raw top-down frames of the rows).

## Faction: Orks or Chaos (Chaos Warfront C1, 2026-11-25)

A run fights one army, chosen once when the run is built and saved in `run.json` (`Faction`, save version 7): wipes, saves, restarts and reconnects keep it, and a save from before it loads as Orks. The setting is `--warfront-faction alternate|orks|chaos` (default `alternate`: Orks and Chaos take turns run by run, Orks first; the other two force the army). It is read only when a new run is built (first start, NEW CAMPAIGN, the dedicated server's own new run, a new run after a damaged save), so changing it affects only the next run. Each process reads its own flag: the supervisor passes it to the first run it builds (`WarfrontSupervisor.Run` -> `PrepareRun` -> `WarfrontRunFiles.NewRun`, also with `--warfront-new`), and its stage children inherit it for NEW CAMPAIGN and the loader's new runs; the Host panel's FACTION row (Warfront profile only; `client/Extras/Warfront/Chaos/HostPanel.WarfrontFaction.cs`) and `WarfrontLocal.ExtraArgs` pass a forced setting and send nothing for ALTERNATE.

The rotation lives in `faction.json` beside `run.json` (`{LastFaction, LastRunSeed}`; an archive moves only `run.json`, so it survives). A new run writes `run.json` first and `faction.json` second; the stage's loader, the supervisor's loop and `--warfront-new` (before it archives) copy the run's army and seed into `faction.json` when it is behind, so a crash between the two writes neither repeats nor skips an army. A stage child records its run's army before the World exists (`WarfrontRunFiles.StageFaction`, logged as `Warfront faction: this stage runs ...`), and a deployment lands on the running stage only when both the map and the army match (`WarfrontStagePlan.CanLandHere`). The Hub section ends with one faction byte (0 Orks, 1 Chaos; `WarfrontWire.Format` 3, `WarfrontRules.Version` 23).

**Rollback note:** a save written by this build is version 7 and is refused by any older build ("a newer build's file"); going back to an older build means starting a new campaign there or restoring an older run.json. The army is read from the `Faction` field whenever it is present (not keyed on the version), so the retainer rules' version 6 (T0) can be reconciled at integration. A `faction.json` without `LastFaction` (or `{}`) counts as no record.

C1 only chooses, stores and sends the army: until C2 builds the Chaos rules table, a Chaos run plays as Orks, and Orks runs are unchanged by construction. The `FinaleLost` outcome (the duel relay ran out of challengers: the campaign is lost) is in the campaign for C5; it loses the campaign only in a Chaos run's fortress battle, anywhere else it is the fortress-wipe rule. Spec: "Warfront faction" in `spec/behavior.md`.

## Where things are stored

`%LOCALAPPDATA%\sm-reimpl\warfront\` (or the folder `SM_WF_DIR` names): `run.json` (the whole campaign, written atomically at every ship arrival, pick and
deploy), `squads.json`, `stage.json`, `records\<utc>-<result>.json` (archive; never read by a new run), `exports\<utc>.txt` (the Discord copy),
`client.json` (this PC's campaign key). Layout caches (generated levels and graphs) are in `%LOCALAPPDATA%\sm-reimpl\cache`, keyed by the build
configuration: **a Debug client and a Release server do not share files**. To start over: the front end's NEW CAMPAIGN (or the results panel's NEW RUN), or
delete `run.json`.

## Starting it

```powershell
.\smtool.cmd warfront-maps --prebuild                       # Release layouts (the dedicated server's)
dotnet run -c Debug --project src/SmTool -- warfront-maps --prebuild   # what the Godot (Debug) client reads
.\client.cmd                                                # main menu: WARFRONT, NEW CAMPAIGN
```

Four players: `.\server.cmd --content warfront --bind 127.0.0.1 --port 27200 --warfront-max-humans 4 --warfront-new` and one
`.\client.cmd --connect 127.0.0.1:27200 --content warfront --warfront-key-file <own file>` per player. Switch and build property: `--content warfront`;
`-p:SmWarfront=false` removes `**/Extras/Warfront/**` from the build (the build then refuses `--content warfront`). `WarfrontRules.Version` is 17.

## Dedicated mode (`--warfront-dedicated`, slices S1 and S2)

The project's server runs one shared, always-on campaign with up to four humans dropping in and out. `server.cmd --warfront-dedicated --bind <addr> --port <p>`
implies `--content warfront` and `--warfront-max-humans 4` (a given cap wins), refuses `--warfront-new` and `--maps`, and is passed on to every stage child
(`WarfrontRunFiles.Dedicated` for server code). S1 adds only the flag and a hardened supervisor; later slices hang the behaviour (a new run when the
campaign ends, default deploy, an empty server) on it. In this mode the supervisor:

- **locks the run folder**: `supervisor.lock` is opened with an exclusive OS lock (`FileShare.None`: flock on Linux) and held for the supervisor's life, so the OS frees it however the supervisor dies (no PID check, no reused-PID or container-PID-1 trap); a second supervisor on the same folder prints why and exits 5; a leftover file nobody holds is simply taken. The PID inside is informational.
- **restarts a crashed stage** from the last save: a stage child that ends with any code except 0 (clean leave), 1 (no install, run or map unreadable), 2 (command line), 3 (port taken) and 12
  (next stage) is started again after 2, 5, 10, 30 s (the last repeats; a stage that ran a minute starts the steps again), each restart logged. Exit 4 (the simulation kept failing) restarts too.
- **stops on SIGTERM** (and Ctrl+C): it writes `stop.request` into the run folder; the stage child sees it at its next once-a-second check (the check that also notices a vanished supervisor), saves and leaves with 0;
  the supervisor waits 20 s (then kills the child) and exits 0. The stage child also handles SIGTERM and Ctrl+C itself (dedicated mode only): it sets the same save-and-leave flag and exits 0 after the save,
  so a `systemd` `KillMode=control-group` that signals the whole group saves too. Recommended unit settings: `Restart=on-failure` (or `always`), `KillMode=mixed` (belt and braces: SIGTERM to the supervisor, then SIGKILL to the rest after the timeout), `TimeoutStopSec=30` or more (above the 20 s grace).
- **logs a crash loop**: five failures in a row, each before the stage ran a minute, log a `CRASH LOOP` line on every further cycle; the retries go on at the 30 s step.
- **timestamps** every supervisor line `[2026-11-19T12:00:00Z] warfront: ...` (the player-hosted supervisor too).

### Operation of the stage (slice S2; estimates, spec "Warfront dedicated server, slice S2")

- **Default deploy**: the leader (earliest-joined connected player) deploys as always. When the ship has been idle for 90 s (on the ship, an identified leader, no preparation, Go, landing or arrival; the clock restarts when any of that stops holding or the leader changes), the server
  deploys for the leader through the normal path: the HQ defence when mandatory, else the node of the lowest-front lane (ties A, B, C), else the fortress; the Stage text says so for 5 s. It never fires with nobody connected, and it passes the co-op gate (a player still choosing a class or cards had the 90 s; otherwise an absent one would hold the ship forever).
- **New run**: the leader's NewRun works as before; otherwise a new run starts 2 minutes after the results stand, and at once when no human is connected.
- **Empty server**: the process and the run stay and the play clock stops; a battle left with nobody is no result (back on the ship, nothing rolled); the next joiner continues the run. An unfinished run never restarts by itself.
- Player-hosted play does none of this. Code: `GameServer.WarfrontDedicated.cs`; tests `WarfrontDedicatedOperationTests`.

### The service (slice S4)

On the project's VM the mode runs as the systemd service `sm-warfront` (UDP 27016), installed by `tools/release/setup-warfront-service.sh` (docs/dedicated-server.md "The Warfront server"):
`SM_WF_DIR=/var/lib/sm-warfront` (the run), `SM_DATA_DIR=/var/lib/sm-warfront/data` (layout caches), `ExecStart=<versioned server folder>/SmServer --port 27016 --game-dir /srv/sm1 --warfront-dedicated --content warfront --ban-file /var/lib/sm-warfront/bans.txt`,
`Restart=on-failure`, `RestartPreventExitStatus=1 2 3 5`, `KillMode=mixed`, `TimeoutStopSec=40`. It replaces the old `sm-musou` service (kept for `--rollback`).

**Map warm-up (built; owner decision 2026-11-20: build every map at start, in the background, players may join meanwhile).** A map's layout and walking graph are built on first use (`WarfrontMaps.Prebuild`, the travel wait's work, 100 to 180 s cold), cached under `SM_DATA_DIR`.
In dedicated mode the supervisor starts one extra child, `SmServer --warfront-dedicated --warfront-prebuild` (the supervisor's own arguments minus `--map`, `--warfront-new`, `--warfront-stage`), once a stage child has run 30 s without ending
(`WarfrontSupervisorTiming.PrebuildAfter`: the stage has loaded the game data and listens). The child loads the game data and the same `SimConfig` a stage has (so the nav cache keys match), binds nothing, and builds the 11 maps (nine lane slots, HQ, fortress) one at a time at
below-normal priority (`ProcessPriorityClass.BelowNormal`, nice on Linux), then logs its time and peak working set and exits 0 (1 when a map failed). Details:
- It runs once per supervisor: a failure or a crash is logged once and it is not restarted (the first deployment to a map it missed builds it as before); a stage restart does not restart it.
- The supervisor's stop (SIGTERM, Ctrl+C, `stop.request`) kills it at once (it has nothing to save), and it ends with the supervisor however the loop ends; the child itself also leaves between maps when the supervisor is gone, SIGTERM arrives or `stop.request` appears.
- A warm map is "skipped" by being read back (both files), not by a pre-check: the nav cache key needs the built map, so a file-name check could call a stale cache warm. Its cost is the load of the game data plus two file reads per map (to measure on the VM).
- Cache safety: the layout cache and the nav cache both write a unique temp file and rename it into place (`File.Move` overwrite), so a reader never sees a partial file and a stage preparing the same map at the same moment simply races to the same bytes (the layout temp name now carries a GUID, not only the PID, so two writers in one process cannot collide either; the nav cache also takes a named mutex per key). Test: `Concurrent_writers_of_one_cache_file_never_leave_a_reader_a_partial_file`.
- Memory: one extra process holding one map's build at a time (hundreds of MB while it runs; the prebuild releases each slot afterwards); the line `map warm-up finished: ... peak working set N MB` in the service log gives the real figure.
Tests: `WarfrontDedicatedWarmupTests` (fake launcher: starts once after a stage has run, never in player-hosted mode, not restarted on failure, killed on stop; the child's loop with an injected build). Spec: "Warfront dedicated server, slice S1" rows "Map warm-up".

Exit codes of the supervisor: 0 stopped, 1 run unreadable or a stage ended with 1, 2 command line, 3 port taken, 5 another supervisor holds the folder. Tests: `tests/SmNet.Tests/Extras/Warfront/Dedicated`
(a fake stage child; SIGTERM delivery itself is Linux-only and not tested on Windows).

## Saves and progress (codebase fix 1, 2026-11-22)

**The campaign save (`run.json`, host).** At start `WarfrontRunLoader` loads the run and tells loading from writing: a campaign that loaded but could not be rewritten keeps playing (SavePending, retried 5 s later and at every save point; the
asynchronous writer sets the same flag). A damaged file is moved to `records\<yyyyMMddTHHmmssZ>-unreadable.json` (with `squads.json` as `...-unreadable-squads.json`; `-2`, `-3` on a clash), a new campaign starts and
the player is told ("the campaign file was damaged; it was set aside and a new campaign began"); if it can be neither moved nor copied the server refuses every join ("the campaign file is damaged and could not be set aside"). A file of a newer build is never
touched ("this campaign needs a newer build"), and a locked or unreadable one (3 reads, 200 ms apart, UnauthorizedAccess too) is left alone ("the campaign file cannot be read"). In those last cases the dedicated server stays up in NoSave refuse mode: no save, no archive, no
new-run command, every Identify refused with the same words; the supervisor no longer exits on a bad run. The loader leaves `notice.json` (`{kind: unreadable|newer|locked, utc, text}`) in the run folder; the Warfront menu reads and deletes it when it opens and shows it as one live-text line (an online host sends the text as a notice). The menu's run state `Newer` shows "this campaign needs a newer build" and turns NEW CAMPAIGN off.

**Progression (`progress.json`, player's PC).** In the Warfront folder next to `client.json` (`<key file>.progress.json` with `--warfront-key-file`): version 1, key, XP, picks taken, cards, extra retainers (class and name), reset time. Written
atomically with a `.bak`; a file of a newer version is never touched; another key's file is kept as `progress-other-key.json`. After Identify the client sends it as the chunked Progress command (kind 5, resent once a second up to 30 times
until the first ProgressSave, tag 49, arrives); the host sanitizes it (max level 50, MaxXp 687499; picks, cards and extras bounded), keeps the better of its run copy and the player's under `Better` (XP, then picks, then extras; a tie takes the host) and
applies it at the Ship phase. The host sends ProgressSave on every pick, level change and retainer gain, and every 10 s while XP changed; the client writes its file only when the host copy is not lower. A key already in the run with no file keeps its run copy, which becomes its first file.
**Reset:** RESET PROGRESSION in OPTIONS, INTERFACE, directly under SCREEN FLASHES (owner 2026-11-25; on the Warfront page before; usable only while no server of this game runs on this PC, otherwise greyed with the reason on hover, as when there is nothing to reset or in the in-match Options; Yes / No card) keeps one `progress-before-reset.json`, writes a level-1 file, deletes the `.bak` and sends flag 2 with the next Progress, which the host honours only in the Ship phase; run, stats and honor roll stay.
**Review fixes (2026-11-22).** A reset the host has not applied yet is never undone: the host's ProgressSave to such a client carries the level-1 copy, the client's file ignores a host copy above level 1 while it is a pending reset, and the reset is resent until a ProgressSave shows level 1. A progress file that exists but cannot be read (locked) is "unreadable", never absent: it is not written, reset or sent, and the menu says so. The reset's safety copy is of the file the load used (the `.bak` when the main is damaged). A record that waited for the ship and wins gets the XP the run's copy earned meanwhile. Every recorded client gets a final ProgressSave at the end of the run and at a stage change. `--warfront-new` on a damaged `run.json` sets it aside (as the loader does) and starts the new run; on a newer or locked one it refuses and leaves it alone. A save whose autosave writer is still busy waits (SavePending) instead of racing it. Once the host has applied a reset in its run, its ProgressSave carries the Reset flag and the client clears the file's pending-reset mark, so a later join never sends the reset again (which would wipe XP earned on that host meanwhile); a queued reset's level-1 copy has no flag and leaves the mark in place.
Spec: "Warfront save safety and persistent progression".

## Hooks, pins, versions

The Musou and core hook list, with the pin tests, is in `docs/added-content.md` section 11 (W3 sixteen, W3b seventeen, W13b four, W5a, W5c, W16, W13e
`WarfrontHeadwayNear`; `WarfrontWallLean` was added and reverted and is not in the tree). Test aids: `docs/commands.md` (`SM_WF_*`, `--warfront-*`).

## Known approximations

See `docs/warfront-test-report.md` ("Unproven") for the current list. In short: the dressing is a first draft; Warboss pace and pinning at the fortress
openings; a short freeze on the travel screen while the new view is built; allied pair on solo; every tuning number, squad strength and difficulty is an
estimate awaiting playtests (`docs/refine-register.md`).


## Retainer combat (slice S1, owner 2026-11-19)

Retainers fight like the Classic PvP bots and stay on the squad leash and orders. Slice S1 gives them the bots' aim (a retainer makes 40 % of the misses of a Classic-aimed shot, 0.87 hit chance at weapon chance 0.7 against 0.68 for a Classic bot, the bots' own hit-chance formula
copied into `SquadAim` and pinned to `BotAimModel` by a test), a target they hold for about a second (threats to the owner or a downed Marine come first, a clearly nearer Ork can still take over: `SquadTargeting`), and the level
scaling above. The old strength cap (a Marine deals 0.08 to 0.25 of a bolter player) is superseded: retainers deal clearly more. Still to come: movement (strafing, spread, avoiding blasts), combat moves (roll, block, melee,
charge, jump, grenade) and the client animations. Spec: "Added content: Warfront: squad combat". `WarfrontRules.Version` 16.

**Developer tools in the local campaign (owner 2026-11-21).** The WARFRONT page's local campaign (`WarfrontLocal.Options`) starts its server with `--dev-tools`, like Classic's "This PC only": F6 opens the developer tools, including the Weather section (preset, None, Follow Match; presentation only, never saved). `ServerLaunch` adds the flag only for a this-PC-only host and the server still obeys it only on a loopback bind and for loopback requests. The Host panel's HOST WARFRONT (`HostOptions` built in `HostPanel`, `HostStartsDevTools` false for the Musou family) is unchanged: its arguments do not carry `--dev-tools`.
