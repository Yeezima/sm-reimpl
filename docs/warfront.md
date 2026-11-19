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
first-time joiner during a battle spectates (Classic's spectator camera) until the next ship stop; then XP = the median of the others, the customizer must be
opened and closed, every catch-up card is picked aboard, then the player may deploy. Nothing is granted twice on a rejoin.

## XP, cards, scaling, squads

- Personal XP from the player's own and squad kills; shared once per event: elite, boss, captures. XP to the next level 1500 + 500 per level; a pick
  queues until a lull or the ship; the picker makes the player invulnerable and idle without pausing the others; every card on offer (1 to 6 or D-pad; owner 2026-11-18): all
  five stat cards (attack speed, max HP, damage, ability cooldown, max armour), and a sixth, +1 Retainer, only for a squad-capable class with room (the Dreadnought
  sees five); nothing is drawn. A level-up refills health
  and armour. Retainers take one third of their player's stat bonuses, and (retainer AI slice S1, owner 2026-11-19) their damage and health also grow with the player's level on the Orks' curve (damage +5 %, health +8 % per level above 1: level 10 is x1.45 and x1.72).
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
`-p:SmWarfront=false` removes `**/Extras/Warfront/**` from the build (the build then refuses `--content warfront`). `WarfrontRules.Version` is 13.

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
