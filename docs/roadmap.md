# Roadmap

## Phases (owner, 2026-10-29)

Order: demo → full game → new content → standalone game + engine move as one phase.

### Phase 1: pre-alpha demo (pure sm1)
manufactorum only. annihilation + seize ground. every class, every perk, roughly authentic.

**done when:**
- all 48 perks work. right now 36 work, 2 only partly, 12 do nothing yet
- every class and weapon plays like sm1 within a "feels right" margin
- a first playtest pass over the refine register (`docs/refine-register.md`)
- a tester build installs and joins on a clean pc
- `--content faithful` is the demo build, so no orks in it
- scoreboard stats on the wire (assists, seized, defended; `docs/scoreboard-stats-design.md`)

**not in it:** other maps, exterminatus, progression

### Phase 2: the full game
all of sm1's multiplayer redone.

- every mp map, one at a time. manufactorum is the template
- every pvp mode sm1 shipped with, not just the two from the alpha
- progression and unlocks the way sm1 had them
- exterminatus: wave director, ork and chaos ai, cp maps

exterminatus is the biggest job in here. there's no ai at all yet. maps first so it has somewhere to run.

### Phase 3: new content
orks (already started), gore (paused on `gore/g1-sim`), whatever's next. it all stays behind `--content` in `Extras/`
(`docs/added-content.md`), so the faithful build never breaks.

### Phase 4: a standalone game with original assets, and the engine move
A possible later phase: the project becomes a standalone game with its own original assets, no longer reading SM1's files,
and moves to a new engine (Unreal Engine 5 is the candidate), both done as one phase. Better visuals mostly come from new
assets; UE5 running SM1's 2011 assets gains little. As a standalone game it would carry its own setting and names rather
than the original game's.

**Blockers to settle first:**
- **UE5 and GPL-3.0.** The Unreal EULA and the GPL are widely considered incompatible (to be checked), which would keep GPL code from shipping linked into UE5. The port
  would then need a licence change first, or the client stays on Godot (or another GPL-compatible engine).
- UE5 has no official C#: the simulation and netcode would need a rewrite in C++ or an unofficial C# plugin.

The server, simulation and netcode do not depend on the engine, so they carry over. Only the client is rebuilt.

### Open questions
- does "all perks" mean as they worked at sm1 launch, or after the last patch?
- is any of exterminatus wanted in the demo?
- what licence would the project use for the standalone game (phase 4)?

## Milestone log

Scope: multiplayer only, meaning PvP (Annihilation, Seize Ground) and Exterminatus co-op (decided 2026-10-26; see CLAUDE.md).
The singleplayer campaign is out of scope except where multiplayer depends on it.

Status labels: *original asset loaded* · *placeholder demonstration* · *supported by evidence* ·
*provisional approximation* · *implemented but untested*.

## M0 — Bootstrap (done 2026-10-26)
- Format research. Archive layout documented from two independent sources.
- OBPK reader with bounds checks and 11 synthetic-fixture tests (passing).
- `discover.ps1` inventory tool, dry-run tested on a synthetic install and a synthetic Steam library.
- Stack selected (see architecture.md).
- **Not done:** nothing has touched the real installation yet.

## M1 — Original archive access verified (done 2026-10-26)

Result on the user's install (app 55150, buildid 7412325), details in `docs/discovery.md` section 1:
1. PASS: edition, buildid, depot manifest, and exe version recorded.
2. PASS: 2,736 archives, zero parse errors, entry counts identical between `discover.ps1` and smtool.
3. PASS: sizes agree for every archive. The 1,140 warnings of the first run came from a wrong name-table model;
   the reader was fixed and now reports zero warnings.
4. PASS: 21 inner types with signatures. Meshes `dcm`, textures `dds`, materials `bmat`, attributes `attr_pc`, levels
   `world`/`region`/`Layer`/`nav`. Skeletons and animations are still unknown (Havok `hkt` is the candidate) and move to M3.
5. PASS: one `dcm` and one `dds` extracted; structure hypotheses recorded.
6. PASS: largest archive decompresses in about 240 ms. Decision: decompress on demand, no persistent decoded cache.

Not re-run after the fix: `discover.ps1`'s own reader got the same string-pool fix and a parse check under
PowerShell 5.1, but has not been re-run end to end on the install. The next `m1.cmd` run confirms it.

Original criteria:

Acceptance criteria (all must pass):
1. `discover.cmd` produces a report identifying the app id (55150 expected), buildid, depot manifests, and executable version.
   The summary is copied into `docs/discovery.md`.
2. Every `.oppc` file parses with **zero errors** in both `discover.ps1` and `smtool verify`. Entry counts are identical between the two.
3. For every archive, `smtool verify` confirms: decompressed bytes = header size = data-block size = sum of entry sizes.
   Any warning is explained in `docs/discovery.md`.
4. The inner-type table lists every group with signatures. The groups holding meshes, textures, skeletons, animations,
   attributes, and levels are identified, or marked unknown.
5. One mesh-related entry (`.dcm` or whatever M1 shows) and one texture are extracted with `smtool extract` into the private cache.
   Their structure is recorded as a hypothesis in `docs/discovery.md`.
6. Timings recorded: full decompress time of the largest archive, which decides the cache strategy.

## M2 — First original textured model displayed (done 2026-10-26)

Result: `client.cmd` shows `Geo_Statue_LODH` from `mp_dlc2_map4__wrld.oppc` (a multiplayer map) with all 11 surfaces
using their original diffuse textures. Label: *original asset loaded*. Screenshot saved under
`%LOCALAPPDATA%\sm-reimpl\screenshots\`. Static `dcm`, uncompressed BOD (`bmat`) and the mesh→material→texture links
are specified in `docs/discovery.md`; readers in `SmFormats`, loading in `SmContent`, client in `client/`.
Godot 4.7.2 .NET builds the client with `net10.0` (no multi-targeting needed).
Not reproduced yet: original shaders (normal, specular, vertex blending), alpha, vertex colours. Rendering is diffuse only.

Original scope:
- Byte-level analysis of the mesh, material, and texture formats. Readers in `SmFormats` with bounds checks and synthetic tests.
- Godot 4.7.2 .NET client project. Confirm `net10.0` targeting, or multi-target.
- Acceptance: one original model from a multiplayer map (`data/chunks/mp_*`) renders with its original texture from the user's install, with no manual steps beyond the launcher.
  Screenshot saved locally. Label: *original asset loaded*.

## M3 — Multiplayer Space Marine: skeleton, armour, one animation (done 2026-10-26)

Result: `client.cmd` shows the multiplayer Space Marine rig (`havok_sm_base_mp__hkxx`, 50 bones) wearing the 9 original
`sm_armour_basic_001_*` pieces, playing the original `SM_NAV_Run_F_01` (one of 613 animations); `--bones` draws the skeleton.
Label: *original asset loaded*. Event data is located in the behavior graphs (listed in `docs/discovery.md`), not decoded.
Not done: original shaders and chapter colours, `O3d` value trees (rigid attachment read from the string table, provisional),
type-2 meshes, which armour set the multiplayer default loadout uses.

Original scope:
- MP player models are assembled from armour pieces (`sm_*` / `csm_*` packages, skinned `dcm`), with paint schemes (`custom_paint_*`).
- Acceptance: a default MP Tactical Marine is assembled from its original armour pieces, its skeleton is parsed and drawn, and one animation plays on it. Any event or track data found is listed.

## M4 — Networking prototype (done 2026-10-26)

Result: `SmSim` (fixed 30 Hz tick, shared movement step), `SmNet` (binary protocol, loopback with simulated latency,
jitter and loss, UDP transports, engine-free `GameClient` with prediction, reconciliation and interpolation) and
`SmServer` (in-process `GameServer` and the `server.cmd` dedicated UDP server). Acceptance, measured by an independent
tester (`tests/SmNet.Tests`, 26 tests):
- Two clients, 150 ms RTT, ±10 ms jitter, 5% loss, 22 s, five seeds: correction error p95 and max 0.0000 m (threshold:
  p95 < 0.05 m, max < 0.5 m). A forced 600 ms outage shows the metric works (up to 0.13 m, back to 0 within 5 s).
- Remote interpolation vs the server's true positions: p95 0.012 m (threshold 0.10 m), max 0.14 m.
- Mismatched protocol, content fingerprint, or config hash, and a full server: refused with the right reason; no spawn.
- Robust to early handshake loss and malformed packets; deterministic for a fixed seed.
- After review: a 400 ms round trip (above the Hello resend interval) is measured correctly and keeps prediction exact;
  real UDP on localhost connects, moves, and refuses wrong content.
Security measures (prototype level): a player exists only after the client echoes a stateless challenge cookie, so spoofed
source addresses create nothing; every input carries a random session token; silent players time out after 5 s and silent
servers are detected; refusals are sent only after the cookie round, so nothing larger than a request goes to an
unproven address; the UDP table of unauthenticated addresses is capped, and players' addresses are pinned so a
spoofed-address flood cannot evict them (tested, with a negative control); at most 1,024 packets per server update.
Movement and all rates are **provisional placeholders** (spec/behavior.md). Not done: the Godot client on the network,
level collision, encryption or authentication beyond the cookie and session (an on-path attacker can still read and inject),
rate limiting per address, UDP tested only on localhost, and forward compatibility (a future client whose Hello
layout differs gets no answer instead of a protocol-mismatch refusal; keep the Hello prefix stable when it changes).

Original scope:
- Headless .NET server; two local clients; movement prediction and reconciliation; remote interpolation; configurable simulated latency, jitter, and loss; handshake refuses mismatched protocol, content, or config.
- Acceptance: automated test where two clients move under 150 ms RTT and 5% loss. Predicted position error after reconciliation stays below a stated threshold. A mismatched-config client is refused.
- Out of scope (product features): accounts, matchmaking, a hosted multiplayer service. Hosting our own development and playtest servers (cloud or otherwise) is tooling, not a game feature.

## Later (order set by evidence)
BAF attribute reader into `spec/behavior.md` (MP wargear: `wargear__mp_sm`, `wargear__mp_csm`); MP map geometry and collision;
camera; player locomotion and jump packs; weapons and MP loadouts; melee; executions; PvP modes (Annihilation, Seize Ground)
and their rules; classes, perks, armour customization, progression and unlocks; Exterminatus waves with Ork/Chaos AI;
front end (class select, lobby flow) only as far as needed to start a match.

## M5 — Original gameplay data and a networked client (current)
Done (2026-10-26): BAF attribute reader; the server and clients read the multiplayer marines' movement tuning from the
install (run 6.5, strafe 6, back 4.5, sprint 10, 0.2 s to full speed, acceleration cap 100), replacing the placeholders;
health and regen values recorded. Netcode with the original values: correction error 0 under 150 ms RTT and 5% loss.
Networked client done (2026-10-26): `client.cmd --connect host:port` joins the dedicated server; one animated original
marine per player (local predicted, remote interpolated), run/idle by speed, follow camera, keyboard/mouse or `--bot`;
clients send `Leave` on exit and the server neutralizes the input of a player silent for 0.5 s. Verified with two bot
clients over UDP (each sees the other move; a leaving client disappears at once). Ground, camera and animation choice are
*placeholder demonstration*. Weapon values done (2026-10-26): 53 multiplayer weapon and equipment files read; headline numbers in `spec/behavior.md`.
M5 is complete.

## M6 — Weapons and health (done 2026-10-26)
The original bolter in `SmSim.Combat`: damage 22, 0.133 s between shots, 30 / 270 rounds, 1.3 s reload, range 250,
fall-off (×1 to 45, ×0.8 from 70), head ×2.55, shield ×0.505 body / ×0.5 head, all read from `sm_mp_ranged_bolter`
(default entries). Health 100 and armour shield 100 with the original regeneration delays and rates. Death and respawn.
The server validates every shot: fire rate and ammunition are enforced, and hits are tested against targets rewound
to the tick the shooter was viewing (capped at 15 ticks). Protocol version 2. The client predicts ammunition, cooldown,
reload and regeneration exactly; damage is server-only. Godot client: HUD (health, shield, ammo, reload, death),
crosshair, left mouse fire, R reload, mouse pitch, and a `--bot` that fights.
Acceptance (tests/SmNet.Tests/CombatAcceptanceTests.cs, end to end through server and clients): fire rate and ammunition
hold against forged input floods; shots aimed at the rendered target hit under 150 ms RTT with 5% loss; shield then
health damage, head multipliers, fall-off and range; death, respawn after 5 s, regeneration; prediction of the weapon
state matches the server bit for bit. Two bot clients verified fighting over UDP.
Provisional: hit volumes, aim origin and spread, respawn time, rewind cap, and the damage-combination rule
(`spec/behavior.md`).

## M7 — Level collision and maps (done 2026-10-26)
The multiplayer maps' static collision is read from each region's Havok file (`wrld.hkt` / `r000_main.hkt`): 11 of 15
maps (the `mp_dlc3_*` maps keep theirs elsewhere). The storage behind each instanced piece is the w component of its
extrusion vector (evidence in docs/discovery.md). TDM spawn markers come from `d_tdm.Layer` (BOD reader extended).
`SmSim.Collision`: deterministic BVH, raycasts, and a capsule character controller (walls, slopes up to the original
60°, steps, falling). Shots stop at weapon collision. The server takes `--map` (default `mp_dlc2_map4`), names it in the
handshake, and refuses clients whose map hash (of the source files) differs; the client loads the same map and draws its collision
geometry as grey placeholder geometry. Shared-collision question decided (docs/architecture.md).
Provisional: capsule size, step height, controller rules, which collision layer serves characters and shots, spawn choice,
fall speed cap and out-of-world kill depth.
Real-data soak (16 bots × 3 minutes on each of the 11 maps): deterministic, no NaN, World.Step 0.07–0.25 ms on average for
16 players. Known issues: 5 spots where a bot can still get trapped (mostly gaps narrower than the placeholder capsule);
bots walk off open map edges and are killed by the fallback kill depth (the original's kill volumes are not decoded).

## M8 — Original map visuals (done 2026-10-26)
The client draws each multiplayer map with its original meshes and diffuse textures: `SmContent.MapScene` reads the
region's layers (enabled `StaticObject`s, nested children placed relative to their parent), the `.O3d` definitions
(node tree, `StaticMeshVisual` parts, LOD masks) and the meshes, materials and textures, all from the region archive;
`client/MapView.cs` draws the nearest LOD with one MultiMesh per mesh. The rotation order (degrees about X, then Y,
then Z) was settled against the level collision's quaternions, and placed objects' collision nodes land on the
collision pieces (e.g. 1,987 of 1,990 on mp_dlc2_map4). Live: mp_dlc2_map4 (2,177 objects, 313 of 315 surfaces
textured, 0.9 s to load collision and visuals) and mp_tdm_01 (282 of 282). The nearest-LOD choice is a provisional
approximation (bit 0 of the LOD mask, minus low-LOD-named nodes). Not done: normal, specular and detail maps,
vertex blending, alpha, the original shaders and lighting (`SunDesc`, fog, HDR in the layers), decals, lights, effects,
the far LODs, and meshes with block type 2 (a few banners and props).

## M9 — Melee from the behavior graphs (done 2026-10-27)
The behavior graphs in the character packfiles are read (`HavokBehavior`: Relic's sequencers and their timing tags;
layouts inferred from the data, all 46 marine graphs and 3,431 tags read). The combat knife's three-stab combo is
timed from its graph (hit, combo window, fire-cancel and move-again times per stab, in seconds from the clips'
lengths) and its wargear (40 damage, 20 to secondary targets, reach 4 m, its own shield and health multipliers).
The simulation has a melee button with buffered combos; the server resolves melee hits with lag compensation like
shots; protocol version 4. The client binds V and the middle mouse button, poses the original attack animation at
the attack's time, shows the combo step, and `--bot-melee` makes bots fight with the knife. Verified with two bots
over UDP (combos chain 1→2→3, kills with no shot fired). Provisional: how presses map onto the graph's branch windows,
the cone and vertical reach, no lunge or root motion, free turning during an attack, combo order from names.
Known gaps: the client poses attack clips at simulation time without the playback speed (all knife clips play at 1);
secondary melee targets use the primary hit's multipliers; the 256-generator cap of the behavior reader has no test.

## M10 — Classes and loadouts (done 2026-10-27)
The six multiplayer classes are read from the global file's class table (Assault, Tactical, Devastator and the Chaos
Raptor, Tactical, Havoc), with their base wargear, loadout choices and perks. Each gets a default loadout (provisional:
base wargear, then the first supported choice per slot; on the install that is always the data's first choice):
Tactical bolter + bolt pistol + combat knife, Assault bolt pistol + chainsword, Devastator heavy bolter + bolt pistol.
`WeaponTuning` reads any hit-scan weapon (slot, full or semi auto trigger, raycast delivery, damage, ammunition,
reload, fall-off, multipliers); the chainsword combo comes from its behavior graph like the knife's. The simulation
holds a primary and a pistol per player with switching (placeholder 0.6 s), semi-auto triggers on the press, and the
class's melee. Clients choose a class in the handshake (`--class`); snapshots are per recipient (own player in full,
others slim: no health, ammunition or timers), protocol 5. The client shows the class, the weapon in hand and the
holstered one, binds X / mouse wheel to switch, and plays each class's melee clips. Verified with three bot classes.
Not done: projectile, cone, heat and charge weapons (grenade launcher, plasma cannon, melta, heat, lascannon charge),
equipment and grenades, jump pack, perks, unlocks, class choice after connecting, per-race models.

## M11 — Weapon heat and loadout choice (done 2026-10-27)
`WeaponTuning` reads each weapon's heat pool (`common_data/heat/wargear\heat_info`) and heat per primary shot
(`apply_heat`, default entry): heavy bolter 3 per shot, overheat 100, released at 20, cools 25/s after 0.3 s; plasma gun
6.5, 100, 0.1, 4/s after 0.35 s (1 s after an overheat). The simulation adds heat per shot, locks an overheated weapon
until heat falls to the recovery amount, cools both weapons (the holstered one too) and swaps heat with the weapon on a
switch; prediction stays exact through overheats. Every supported base and startup wargear of a class is now a choice
(unlock levels not applied); the client asks by index in the Hello (`--primary/--pistol/--melee`), the server
validates it. Protocol 6 (the weapon in hand and the melee choice for others; heat for the owner). The client's
placeholder HUD shows heat and OVERHEATED; bots switch to the pistol while overheated. Verified live: heavy bolter
overheats after about 7.7 s of fire and releases 3.2 s later; the plasma gun stays locked about 26 s (rate unit
uncertain, see `spec/behavior.md`).
Not done: charged fire (lascannon, plasma gun secondary), braced heavy bolter, venting, overheat effects.

## M12 — Charged fire, projectiles and splash (done 2026-10-27)
`WeaponTuning` reads charge abilities (`charge_ranged`, stages with times, heat per second, ammo cost, the stage's own
shot), projectile blueprints from `ebps` (`projectile_ext`: speed, lifetime, gravity over distance, sticky, bounces,
detonation) and their blasts (`area_of_effect`: radius, fall-off, line of sight, shield damage), and the grenade
launcher's remote detonation delays. The simulation charges while the button is held (Fire = primary ability,
AltFire = secondary; the plasma pistol taps for its basic shot), fires the reached stage on release, and runs
server-side projectiles that collide with the map and players, stick, bounce and detonate with linear splash fall-off
(owner included). The grenade launcher and plasma cannon are now loadout choices. Protocol 7 adds projectiles to
snapshots (not predicted; interpolated like remote players) and the charge state. The client binds AltFire (right
mouse / F), draws projectiles and blasts as placeholders and shows a charge bar; bots charge and detonate. Verified
live on the flat ground and on `mp_dlc2_map4` (charged lascannon and plasma shots, grenade and plasma blasts dealing
damage and kills, projectiles stopping on the level).
Not done: frag/krak grenades and melta bombs (read only), the melta's cone, `armourshield_breaker`, collision radius of
projectiles, venting, perks; projectile lag compensation (by design for now).

## M13 — Thrown grenades and the melta (done 2026-10-27)
Every class carries frag and krak grenades (`startup_wargear`; the melta bomb is read but not offered: hidden from the
customizer in the data). Throws follow the toss graph (`SM_Bolter_GrenadeToss`: release 0.2 s, end 0.667 s), grenades
are rigid bodies with the blueprint's bounce rules (`rigid_info`) and a 1 s fuse from first ground contact, and every
damaging blast in the blueprint applies (the krak's blinding and stun are not simulated). The melta's primary
(`pump_ranged`, a `shapecast` cone 20 long, 10° apex) hits every target in the cone, rewound like rays, with distance and
coverage fall-off, and refills its single-round clip by itself. Protocol 8 (grenade button, count, cooldown and throw
state; equipment choice in the Hello). The client throws with G (`--equipment frag|krak`), draws grenades, blasts and a
local cone flash as placeholders, poses the original toss clip and shows grenade and recharge state; bots throw and use
the melta. Verified live: frag and krak detonations dealing damage and kills, melta hits falling off with distance.
Not done: blindness and stun, the melta bomb, stimpack, perks, cone shots of other players on the wire; grenade
bounces on level geometry not observed on screen.

## M14 — The jump pack (done 2026-10-27)
The Assault Marine and Raptor read their pack from `sm_mp_abil_jumppack` / `csm_mp_abil_jumppack` (`jump_info`: height
by hold 4–18 m, power 100 recharging 10/s with costs 35–55, rise / hang / fall gravities 20 / 12 / 30, forward speed
16; smash speed 50, limit 50°, gravity 75) and the ground pound blast (`actn_gp_hit`: radius 7, 25 + 50 shield) and
landing blasts by drop height; timing (lift-off 0.2 s, landing and get-up locks) comes from the rig's graph
`MP_SM_NAV_JP_Sim`. Space jumps (hold for height), melee in the air pounds; prediction stays exact on maps. Protocol 9
(jump state for the owner, jumping/pounding flags for others; 16 players + 26 projectiles = 1200 bytes). The client
plays the original jump, flight, dive, impact and landing clips, shows power, and draws placeholder rings; bots jump
and pound. Verified live on the flat ground and `mp_dlc2_map4` (pound impacts dealing damage and kills, landings on
level geometry).
Not done: stun and knockback (not established), fall damage (no value found), perks, the assault melee lunges; remote
pound/landing blasts are inferred on clients (not on the wire). The snapshot budget has no headroom left.

## M15 — Teams and Annihilation (done 2026-10-27)
The two sides come from `global` `multiplayer_settings/teams` (Space Marines, Chaos) and each class's race; the
shipped deathmatch script's constant pool names the rule (enemy deaths, the host's score and time limits), whose values
are host options in the executable, so score limit 41, 10 minutes, friendly fire off and the 5 s respawn are
provisional. The server assigns and balances sides (8 per side), filters friendly fire on every damage path, scores
every death for the opposing side, ends at the score or time limit or an emptied side, and restarts after 15 s;
spawns pick the shared TDM marker farthest from enemies. Protocol 10 (team in the class byte, match block and kill
feed in snapshots, a separate scoreboard message; 20 projectile records per snapshot, worst case 1158 bytes). The client
colours allies and enemies, shows the match banner, kill feed and a Tab scoreboard; bots target enemies. Verified live:
matches ending at the score limit and restarting, no team kills over 57 kills.
Not done: Chaos models, spawn protection, experience and assists, Seize Ground, the lobby after a match.

## Alpha scope (2026-10-27)
The user limited the initial alpha: only Annihilation and Seize Ground, only the map Manufactorum (`mp_tdm_09`), no
customizer yet; Exterminatus and other maps are deferred.

## M16 — Seize Ground on Manufactorum (done 2026-10-27)
Manufactorum is `mp_tdm_09` (`multiplayer_map.oppc` `sm_factory`, display name from the English string table) and is
now the server's default map. Seize Ground is the data's `domination` game type: three cylinder points from the map's
`d_mission_objects.Layer` (named by the map script's constant pool), per-side start areas, capture 20 s / neutralise
10 s / 2 per point (the `Domination` preset's constants); score interval, score limit and time limit provisional.
Protocol 11 (mode bits, point states, point layout in the scoreboard). The client draws the points and a capture HUD;
bots go for points; `--spectate overview|action|point:<letter>`, `--no-hud`, `--screenshot-every` help capture
showcase material. Verified live: points captured by both sides, scores +2 per point per second, the match ending at
the score limit. Known issues: neutralisation not seen live (bots rarely attack enemy points); players fall off
Manufactorum's open edges (about 6 per 170 s with 10 bots); when every player leaves at once the empty-side rule names
a winner; one 10-client run stalled after 8 s (not reproduced with fewer clients).

## M17 — Chaos marine models (done 2026-10-27)
Chaos players are drawn with the original Chaos multiplayer armour (`data/packages/csm_armour_mp_<set>_<part>.oppc`,
diffuse textures from the sibling `_wg`/`_tt1`/`_tt2` packages, `character_wargear` materials) on the shared Space
Marine multiplayer rig, so every animation carries over. Class → set by the class name (an interpretation, **superseded 2026-10-28**: the default look follows
the data, `chaos_default` plus the class overrides, owner's decision): Tactical `basic`, Havoc `havoc_01`, Raptor `raptor_01` as M17 shipped. Rigid armour blocks are placed through their `.O3d` node under a bone.
The red tint remains only as a fallback if a set fails to load. Verified by model renders of all three sets and the
unchanged Space Marine. Legion paint was added later (see "Armour paint" in discovery.md). Not done: the Raptor's jump pack mesh, other Chaos sets.

## M18 — Manufactorum edges and lighting (done 2026-10-27)
Characters now collide with collision layers "2" and "8" together: layer "8" holds Manufactorum's invisible edge walls
and pit railings (interpretation; no collision filter decoded). The map's `KillRegion` marker (a 250 × 250 × 1 m box
under the pit floors) kills players who fall into a pit. A walk-off probe from every spawn, start area and point in 24
directions went from 82 falls to 0 of 552. The server warns at start-up if a spawn, start spot or point lies inside a
kill volume (none on Manufactorum). Lighting is a provisional approximation (warm sun with shadows, dusty fog, SSAO):
the map's own sun and fog values were not found; its placed lights (`l_lights.Layer`) and normal maps are not loaded.
Not done: per-race start-area blockers, the `--show-collision` view still draws layer "2" only.

## M19 — Fixes from the first bot playtest (done 2026-10-27)
A 4-bot playtest of both modes on Manufactorum (report outside the repo) found the follow camera on walls and inside
the own shoulder pad, and Annihilation bots standing at their spawns firing through walls (0 kills in 3 minutes). The
follow cameras (bot, over-the-shoulder, spectator views except the overview) now cast from the player's head (the
spectator views: from their target) against both the shot and the character collision and stop 0.35 m in front of the
first hit; pulling in is immediate, only moving back out is smoothed; the own model hides when the camera is within
1 m of the head. The test bots fight only the nearest enemy they can see within 25 m (on a map; flat ground is
unchanged) and otherwise walk toward the nearest enemy or the map centre, with the stuck-detour helper shared with the
Seize Ground bot. All placeholder / test script; the original camera rig is not decoded. The "winner at 0–0" seen when
the bots quit was the empty-side rule working as stated (the bots left one side first); both sides emptying in one
tick already ends with no winner.

## M20 — Camera, aiming and HUD after the original (done 2026-10-27)
After a human playtest ("aiming and hits feel terrible") and a gameplay video of the original: the camera sets
(`camera_curve_settings`: field of view 40 exploring, 20 aiming a rifle), mouse speeds (`camera_input_tuning`) and the
weapon→set mapping (wargear `camera_data`) are read from the install; the Havok camera rig (`spacemarine_camera`: 181
frames, one per degree of pitch) gives the camera's offset (exploration 3.38 m behind, 0.64 m right, 2.38 m up). Left
Ctrl holds aim mode (zoom, slower mouse, aim-mode walk speeds from the blueprints). Shots converge on the crosshair: the
client sends the shot direction as offsets from the view (protocol 12), the server clamps them to 30° (ours). The
shooter learns its hits from a 12-byte per-recipient block (hit markers). Hit volumes are the marine rig's ragdoll
shapes (head sphere to 2.31 m, shoulder pads, backpack, limbs) instead of placeholders. The HUD is redrawn after the
original's layout (placeholder drawing, no original UI assets; the original HUD is Scaleform) and the info text moved
behind F1. Not done: aim assist (data recorded), remote aim poses, camera relax/blend curves, original HUD art and font.

## M21 — The evade roll and aim poses (done 2026-10-27)
Space evades as in the original's "Evade / Jump Pack": Tactical (and Chaos Tactical) rolls, Devastator / Havoc do a
short hop (the heavy clips' flat root motion plus the feet's clearance, peak about 0.29 m: interpretation), Assault /
Raptor jump with the pack (user's description of the original). The roll's and hop's TargetableMode / CollisionMode
windows are hyper armour that negates stuns (user's knowledge of the original), exposed for the future stun code. From the install: the classes' evade abilities (`sm_mp_core_abil_evade`, `..._heavy_evade`:
0.8 s between activations, 25° wall-hit angle), the behavior graph's evade sequencers (ranged roll 1.167 s, heavy 0.967
s) with their branch times (move, fire, switch, melee), and the clips' extracted root motion (ranged roll about 9 m,
heavy about 6 m). Direction from the stick at the press; firing and melee blocked until their branch times; walls stop
it; predicted like movement (protocol 13). Players in aim mode show the original aim-mode clips by weapon class (rifle
Focus set, pistol, heavy), blended by pitch; rolls are posed from the roll clips. Interpretations: input-to-branch
mapping, direction sectors, one clip set per class, the pitch blend. Not done: the jump-pack classes' dash evade, the
heavy bolter brace, the meaning of the roll's TargetableMode / CollisionMode windows.

## M22 — Aim stance, zoom, slot keys, spawn rooms, diagonal rolls (done 2026-10-27)
From the user's M21 playtest: aim mode now stands on the original idle-aim clips (the "walk on the spot" WOS clips lift
the feet); the aim-walk plays only when moving, slowed to the speed. Every weapon except the stalker bolter and lascannon
uses the rifle's camera sets (user's knowledge of the original; the data gives pistols and heavy weapons their own).
Keys 1 = pistol, 2 = primary turn into the existing switch. Spawns: each side's room is the platform and corridor sealed
by its three `MissionMovementBlocker`s around `mkr_start_marine` / `mkr_Start_Ork`; every spawn uses it (the original's
Annihilation script spawns at 18 shared markers: the user's rule wins, recorded), facing point B. Tactical rolls in 8
directions (diagonals: the forward/back clip turned 45°: interpretation). Protocol 14. The movement blockers followed
(branch `blockers/spawn-rooms`): each side's boxes are solid for the other side in the shared movement step (all ways
of moving; shots pass), map loader version 5, placeholder shield planes in the client.

## M23 — Death screen (done 2026-10-27)
After the user's description of the original: control is lost on death, the body ragdolls and the screen sinks into a
strong vignette until respawn. The ragdoll is the original rig's (17 bodies with masses, inertia and friction; 16 joints:
12 ragdoll constraints and 4 limited hinges with their cone, twist and plane limits; the ragdoll-to-animation skeleton
mapper), simulated in Godot (joint limits approximated) and pushed along the killing blow, which the kill feed now
carries (protocol 15). The death tuning in the blueprints (ragdoll on death, named force levels) is read; its units are
not established. Bodies bake after 3 s at rest; at most 16 at once.

## M24 — Weapon models (done 2026-10-27)
Every multiplayer weapon a class can carry, Space Marine and Chaos, is drawn from the install: each wargear names its
visual package; the weapon's handle node goes onto the rig's prop bones (rifles and heavies at the chest prop, pistols in
the left hand, one-handed melee in the right), unequipped weapons onto the character's `MKR_Stow_*` markers (back, left
thigh, hip), plus the worn jump packs and the Devastator backpack. Every gun has an `fx_Muzzle` locator for M25. Heavy
weapon belts, cables and chain teeth are drawn rigid (placeholder). When a weapon is in hand or stowed is our
interpretation.

## Manufactorum lighting (done 2026-10-28, PR #30)
Placed lights from the map's light layer, normal and specular maps, two-layer vertex-blended materials and cut-outs.
Sun, fog and exposure mapping are provisional approximations; god rays come with the effects (M25).

## M25 — The original weapon effects (done 2026-10-28)
M25a: effects content (PSystems, atlas page, effect commands per weapon and hit container), per-triangle surface materials
from the weapon collision layer, and server shot events (protocol 16). M25b: the client plays them: muzzle flashes, shells,
tracers from the muzzle to the hit, impacts by surface and armour, decals, trails, explosions and the map's ambient
effects. Renderer semantics of the element fields are interpretations; camera kick and blast shake are ours.

## M26 — The original sound profile (done 2026-10-28)
M26a: Wwise package, bank and HIRC readers and a Wwise Vorbis decoder (ww2ogg port, BSD-3; codebooks downloaded to the
private cache). M26b: event resolution (switches, states, containers, attenuation, bus volumes), triggers from the
blueprints, wargear, behaviour tags and map emitters, and a pooled client mixer. Mixing is provisional (follow-ups: M33).

## M33 — Sound follow-ups (done 2026-10-28)
Master bus (-7 dB) and both bus volume properties applied; low-pass through filter buses (node LPF, attenuation LPF curve, RTPCs,
Set/Reset LPF actions, the map's rolloff regions); RTPC curves for `distance` and `speed`; Set/Reset pitch, volume, mute and
SetSwitch actions with fades; MP character voices (exertion lines at melee swings, the charge and tagged clips) through the speech
bank's dialogue events. Cutoff mapping, filter slope, the bark rule and trigger moments are ours or interpretations. Left: per-node
max instances, Play/Stop fades, state-driven properties, pause/resume, game-parameter actions, bus RTPCs (docs/discovery.md).

## M27 — Hip fire (done 2026-10-28, user request)
Every gun fires from the hip while moving with the same accuracy as aimed fire (the shot converges on the crosshair in both
modes; the original's per-stance scatter sets are deliberately not applied). Probe (T0): the original's fire state is the
navigation graph in its ready posture plus an additive whole-body recoil clip, no bone mask, no sprint-fire state.
Simulation (T1): a shot ends the sprint (held trigger; no protocol change). Client (T2, `NetworkView.HipFire.cs`): the ready
run (direction x speed band x pitch blend of the graph's clips) for `HipHold` (1.0 s, ours) after a shot, relaxed/ready blend
times from the graphs, the per-shot additive recoil through `CharacterViewer.SetPoseLayer`, shots seen from the prediction (own),
shot records and new projectiles (others). The raise delay before the first shot is not applied (the ready hold time is in
game code, not in the data). Not done: the +40 degree diagonal clips, the plasma pistol's own graph, grenade launcher recoil as
more than an upper-body overlay, ready posture on other states (sprint start/stop transitions).

## M28 — Hit reactions, heavy strikes, knockback and blindness (done 2026-10-27, branch `sim/stuns`)

The multiplayer reaction types (`reaction_mp_melee` interrupt, `reaction_mp_stun`, `reaction_mp_stagger`) with the original
clips and branch times of `MP_SM_Reactions`; light attacks interrupt; the heavy strikes ("Stun / Alt Strike", Y after n light
attacks: knife and chainsword, with the foot stomp) stun, stagger and knock back; frag, krak (stun, blindness) and ground-pound
blasts react by intensity; the evade's hyper armour, jump-pack flight and attack windows without `BRANCH_HitReact` are immune;
knockback through the capsule controller; predicted exactly (protocol 17, largest snapshot 1194 bytes with 4 guaranteed shot records after the merge with M25). Client: reaction and
heavy clips, a placeholder shake and blindness overlay, F heavy (stun) strike (user's knowledge), alt fire on middle mouse (ours, pending the owner); W + F gives the stun strike until the charge (M29) lands. Not done: the displace kick and slam,
bull rush, perks, flinches, the reaction graph's state machine (kind ↔ clip set by name), remote reaction-time smoothing.

## M29 — The charge (bull rush) (done 2026-10-28, branch `sim/charge`)

W + F while running (user's knowledge of the original): the melee weapon's `ability_bullrush_01` (75 + 25 shield and a stun on
the enemy it connects with, 30 and an interrupt on others in reach, 1 s spacing) with the sprint graph's rush clip and its
extracted motion (14 m), contact while the rush connects, a wall or a player stops it into the collision clip, an ally only
stops it, hyper armour negates the stun; protocol 18 (fullest snapshot 1195 bytes). Not done: the 2.5 m rush variant, perks,
the charger's own `CollisionMode` window.

## M31 — The owner's rules (done 2026-10-28, branch `m31/owner-rules`)

Owner's decisions of 2026-10-28, in the shared simulation (protocol 20, fullest snapshot 1197 bytes): the charge only from a
sprint (the jump-pack classes from the ground only); the first shot waits for the weapon to come up (raise delays from the
hip-fire graphs, always-ready weapons, a 1.0 s hold after a shot that is ours; the client's ready posture follows it); the left
mouse button taps for the normal shot and holds for the charged shot on every charge weapon (AltFire gone); spawn-room blockers
reach 25 m above the level so jump packs cannot fly over them (a change from the original); armour stays silent while moving.

## M32 — The owner's answers (done 2026-10-28, branch `m32/owner-rules-2`)

Protocol 21 (fullest snapshot 1198 bytes). The charger has hyper armour for the first 0.333 s of the rush, half of its attack part
(user's knowledge: "wears off roughly halfway"; no graph window found, the rush clip's `CollisionMode` spans the whole clip): a stun
then is shrugged off (damage applies, the charge runs on), later it ends the charge; charge stops follow all of a tick's contacts.
The weapon stays up 10 s after a shot (the owner's rough estimate, to verify in the original; ready ticks now a u16). The raise
after a switch and the always-up weapons that never wait (also after a sprint or a roll) are confirmed and pinned by tests.

## M37 — The blind grenade and the ground-pound check (done 2026-10-28, branch `m37/blind-grenade-ground-pound`)

Protocol 21 (unchanged; the config hash changes). The owner's knowledge of the original: the files' "krak" grenade is the blind
grenade (ammunition type `blind_grenade`, UI name "Blind Grenades"): no damage, no stun, no knockback, no hit; it blinds every
living player whose eye it sees within its blasts' radii (data: 3.5 s within 8 m, 2 s to 30 m), allies and the thrower included
(`SplashSpec.BlindOnly`, `World.BlindBlast`). Players see it named "blind grenade" (`ThrownSpec.PlayerName`). The ground pound
and the jump landings were checked value by value against both jump-pack files: no mismatch (spec "Ground pound damage check (M37)").

## M39 — Cover collision and penetration (done 2026-10-28, branch `m39/cover-collision`)

Owner's request (user's knowledge of the original): the green boxes on Manufactorum are solid, and only the stalker bolter and the
lascannon shoot through them. The 49 green `ammo_crate_A` (and 4 wall-high door buttons) are moving rigid bodies whose shapes live in
`ppk` physics shape meshes (`PpkMesh`), not in the baked level collision; `MapProps` loads them as static props (the owner: they never
move), solid for every movement mode, shot, cone, projectile and thrown item. Penetration from the wargear: lascannon 10 points (props,
bodies, heads 1; charged: free), stalker 10 (props 5, bodies 4, heads 3), Kraken Bolts 2 (perk; applied when the player's perks give its upgrade string, `Loadouts.UpgradeStrings`): players on the ray take the full shot while points last, past a crate only the exit
cone deals damage (lascannon 50 / 15 m, stalker 20 / 10 m; the owner confirmed a shot through a crate does not keep its full damage).
Map loader version 8; protocol 21 unchanged (the config hash changes). Spec: the "Movable props", "Prop penetration", "Player
penetration" and "Damage past a prop" rows.

## Test-bot navigation (test aid, 2026-10-27)
The test bots stalled on walls and took long to meet. `SmSim.NavGraph` builds a walkable grid (0.5 m cells, several
floors per column) from a given character mesh (so a per-side mesh gets its own graph): cells where the capsule stands
(walkable slope, headroom, 0.4 m clearance at knee, chest and head height, outside kill volumes), walk links to the 8
neighbours within a step or the slope's rise, one-way drop links off ledges that keep clear of kill volumes; only cells
reached from the spawns, points and start areas are kept. A* with string pulling gives the paths; `SmSim.NavAgent`
follows them (re-plans when the goal moves or every second, looks ahead, re-plans and steps back to a cell centre when
stuck). The client builds the graph in the background (one process at a time) and caches it in
`%LOCALAPPDATA%\sm-reimpl\cache\nav`; bots chase, walk to points (a side's bots spread over the wanted points), strafe
and roll only onto walkable cells; `--bot-goal` / `--bot-route` send a bot along points (`x/y/z` gives a point a height).

Round 2 (2026-10-27, test script): **a graph per side** — each bot builds its graph from `CollisionWorld.CharacterFor(its side)`
(`SmContent.BotNavigation`; cache key follows the mesh, so a side's graph is separate), so it never plans through the
enemy's spawn-room blockers (on Manufactorum the one shared graph had 198 / 53 cells inside blockers that stop the
Marines / Chaos, and its start-area-to-start-area route crossed them; the own graphs have none, and the enemy's sealed room
is an unreachable goal the bot stops short of). **Jump-pack links** for Assault and Raptor (`SmSim.NavJumpSet`,
`NavJumpFlight`): for boundary cells on a 1 m grid, every one of 16 headings x holds of 1, 4, 7 and 12 ticks is flown with
the real jump rules (`Combat.StepPlayer` against the side's mesh and kill volumes: the capsule's sweeps along the arc, ceilings
and walls included) from rest on the cell; a flight that lands on a graph cell (standing room, headroom, exits) at least 3 m
away, that walking reaches only far later (more than twice the flight + 12 m, or not at all), and that lands the same from
8 points 0.3 m around the take-off (the agent stops near the cell, not on it) becomes a link (~2,400 per side on Manufactorum,
about 16 s to build, cached next to the graph as `.navjump`). `NavAgent` walks to the take-off cell, stands still, presses
Jump once the power covers the link, replays the checked controls (no stick until the lift-off tick, then fully forward along
the heading; Jump held for the hold) and goes on from the landing (a landing more than 3 m off re-plans). A* uses the links
only for jump classes (cost = flight length + 20 m). `smtool nav-stats` prints sizes per side and the blocker exposure.

## Planned — M40: animation and visual polish (owner's request 2026-10-28)
Recorded footage still looks janky. A milestone entirely about cleaning up animations and the game's visual
appearance, after the original: first an audit (frame-by-frame recordings of bots and the own player: foot sliding,
pose pops between stances and layers, remote players stepping between snapshots, weapon/prop attachment jitter, turn
snapping, ragdoll and death transitions, camera judder; lighting, fog, materials, shadows, anti-aliasing, effects
draw order), ranked by how visible each is in footage, each with its cause and the original's data (blend times,
graph transitions, IK, material parameters); then fixes in small, independently reviewed parts with before/after
recordings. Includes the Chaos eye glow effects (`csm_mp_eye_*`, discovery.md "Helmet eye lenses").

## Planned — Dedicated servers off the development PC (prepare, not started)
The dedicated server already runs on Linux and in a container (README "Linux and containers"). Any machine that runs a
server or the content-dependent tools needs its own legally owned install, read-only as today; the install or anything
extracted from it is never copied into shared storage. Synthetic-fixture tests (`dotnet test`) already run anywhere.

## Immediate next tasks
Batch from the user's M21 playtest (2026-10-27): M22 aim stance standing still, consistent rifle/pistol FOV, slot keys
1 = pistol / 2 = primary, spawns only in the side's spawn room facing the middle; M23 death screen (no control, ragdoll,
vignette); M24 weapon models; M25 tracers, muzzle and impact effects; M26 the original sound profile.
1. M27 hip fire; melee stuns (in flight); then a human playtest of the alpha.
3. Armour decals (chapter badge, legion icon): done (M30). Open: class badges, `pattern_enabled`.
3. Lunge and attack alignment from the graph's tags; spread (`climb_sway_scatter`).
   Netcode follow-ups from the M6 review: send ammunition and reload timers only to their owner (others learn enemy
   reload windows today); predict respawn on the client; derive the camera from the original rig.
4. Later rendering fidelity: chapter colours (`sm_<chapter>_dif`), normal and specular maps, `O3d` value trees.
5. When convenient: double-click `m1.cmd` once more to confirm the patched `discover.ps1` on the real install.
