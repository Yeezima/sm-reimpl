# Ork faction: design (2026-10-28)

A playable Ork Boy as a third faction. **A deliberate change from the original** (its MP was Space Marines vs Chaos only; hints that
Orks were once planned are in the content survey).

## Owner decisions (2026-10-28)
- The faction is chosen first in the customizer: Space Marines, Chaos, Orks. Orks have one class, **Ork Boy**, with the Tactical's perks.
- Weapons from the game files: slugga (pistol), shoota `ork_coop_boy_2hand_shoota_rapid` (gun), choppa `ork_coop_boy_choppa` (melee);
  stikkbombs `ork_coop_boy_stikkbomb` only. A small dodge hop instead of the Tactical roll. Original Ork grunts and sounds. Weapons on the
  Ork's own prop bones. Deliberately a bit silly.
- **Orks play on either team**: two teams stay; the faction is per player (no third team or spawn room).
- **Balance: Tactical parity** (health, shield, weapon damage scaled from the AI-tuned data; table in section 2).

## 0. Starting point
1. `MpClasses.Load` reads every class from `global` with the marine rig's graphs; `ClassLoadout` has `Side` and `Role`, no race.
2. Body stats (speeds, health/shield, eye height, `HitVolumes`, throw timing, reactions) sit on `SimConfig`; `Loadouts.Body` (M43) and
   `Movement.Step` already take the player.
3. `World.Spawn` sets the team with `TeamFor(playerClass)`; a class with `Side == Team.None` goes to the smaller side.
4. Snapshots carry the class in 6 bits (`SimConfig.MaxClasses` 64): a seventh class fits; worst case stays 1200 bytes.
5. Race is carried as `Team` in appearance messages; `Protocol.cs` rejects races other than 1 or 2. `AppearanceState.Race` is fixed at
   the join, so a class change to or from an Ork would send the wrong look (fix in O5).
6. Content readers open the marine rig directly (`CharacterModel.MarineRigArchive`) in `HitVolumeTuning`, `RagdollModel`,
   `LocomotionAnimations`, `HipFireAnimations`, `ReloadAnimations`, `MeleeTuning`, `WeaponModels`, `MpClasses`; most already split
   "read" (`Load`) from "work on read data" (`From`).
7. The client hard-codes marine clip names (`NetworkView.cs` idle and toss, `NetworkView.Aim.cs` aim sets, `CharacterViewer.DefaultAnimation`,
   `NetworkView.HipFire.cs`); `ModelKey` picks by side.
8. `DcmMesh.ReadMaterials` reads a 28-byte record per material; on Ork meshes its last field (`Unknown6`) is the length of a bone palette
   that follows the record.

## 1. Simulation and content
### 1.1 The Ork class
- New `src/SmContent/OrkClass.cs`: a synthetic `MpClass("ork - boy", Role: "ork", Race: "ork")` (ours). Wargear: `slugga`,
  `ork_coop_boy_2hand_shoota_rapid`, `ork_coop_boy_choppa`, `ork_coop_boy_stikkbomb`, evade `ork_coop_boy_evade_ranged`; one choice per
  slot; perks and default perk copied from `sm - tactical`; slots from an explicit table.
- `MpClasses.Build` becomes per-rig (`RigData`: graphs, animation length, extracted motion, lowest foot, hip fire); `MpClass.Rig`
  (default `RigProfiles.Marine`). `Load` reads `havok_boy_coop__hkxx` (`data/chunks/ebp_sets__mp_coop_set.oppc`) and appends the Ork
  as class 6 (0–5 unchanged).
- Choppa: a `MeleeTuning.WeaponMap` for `Base_MELEE_Coop` (sequencers `BOY_1HM_MEL_Cmbo_01/02_*`). The choppa's sprint attack is the
  Ork's charge if `RushTuning` can read it; otherwise no charge (F with W = heavy strike). Since protocol 39 the charge needs only W held at the F press, no sprint.
### 1.2 Race and side
- `enum Race : byte { SpaceMarines = 1, Chaos = 2, Orks = 3 }` (`src/SmSim/Appearance.cs`; 1 and 2 match `Team`). `ClassLoadout.Race`;
  the Ork's `Side` is `Team.None`.
- `World.AssignSide(class, requested)`: classes with a side keep today's rules; an Ork gets the requested side unless it has more
  players or is full (then the other side); no request = the smaller side. `World.Spawn(..., Team? team)`; `GameServer.OnHello` uses it.
- Class changes keep the team (`ClassForSide` maps by role).
### 1.3 Per-class body
- `SimConfig.Bodies` (`BodySpec`: speeds, health/shield and regen, `HitVolumes`; hashed) and `ClassLoadout.Body` (-1 = marine).
  `SimConfig.For(playerClass)` returns a cached `this with {…}` (never hashed); call sites in `Movement.Step`, `Combat.Respawned`,
  `Loadouts.Body` and every hit test (`Combat`, `Cone`, `Projectiles`, `RushWorld`, `JumpWorld`, `PerkWorld`, `Props`).
- Same for every class (ours, parity): capsule, step height, eye height, camera sets, reaction and throw timings.
### 1.4 Hit volumes and ragdoll
`HitVolumeTuning.Load(gameDir, RigProfile)` (Ork ragdoll 15 bodies; head zone = the body named "Head"), `RagdollModel.Load(gameDir,
RigProfile)` (271 kg); check `client/Ragdoll.cs`'s hard-coded bone name.
### 1.5 Evade (dodge hop)
`EvadeSpec.SideOnly`: `2HR_NAV_Evade4m_L/R`; forward/back presses hop toward the stick's X sign, else the last strafe side, default
right (ours); no diagonals; hop height from the clip's lowest foot; hyper armour from the clip's windows, else the Tactical's (ours).
`EvadeTuning` takes the graph name from the `RigProfile`.
### 1.6 Perks
The Tactical's eight class perks; no Ork weapon names an MP perk string. Larraman's Blessing works unchanged; Rapid Deployment, Teleport
Homer, Tactical Readout, Weapon Versatility stay inert as for the Tactical. Mirrored by `OrkBalance.MirrorVariants` (ours): Serrated
Combat Blade (choppa light ×1.625, secondary ×1.6), Favor of the Armory (stikk 2→4, initial 1→2), Master-Crafted Wargear (stikk 200→300,
radius 10→15).

## 2. Balance: Tactical parity
`src/SmContent/OrkBalance.cs`: one row per stat `(Stat, DataPath, Reference, Rule, Factor, Override?)`; factors computed at load
(`reference / data`, or `Set` where the data is 0 or missing); `OrkBalance.Mode` = `Parity` (default) or `Data` (all 1.0), in the
config hash; `smtool ork-balance` prints data, reference, factor, result and the sustained-dps check. Cooldowns round up to whole
30 Hz ticks (bolter 0.133 s and shoota 0.11 s both 0.1333 s).

| Stat | Ork data | Tactical reference | Rule | Result |
|---|---|---|---|---|
| Health max | 85 | 100 | ×1.1765 | 100 |
| Health regen delay / rate | none | 5 s / 5 per s | Set | 5 / 5 |
| Shield max | 0 | 100 | Set | 100 |
| Shield regen delay / rate | none | 7 s / 33 per s | Set | 7 / 33 |
| Shoota damage | 2 | bolter 25.4 / 0.1333 s = 190.5 dps (the owner-balanced bolter since 2026-10-28; data 22: 165) | ×12.7 | 25.4 |
| Shoota cooldown, clip, reload | 0.11 s, 40, 2.0 s | — | kept | 7.5 shots/s |
| Shoota reserve | data | bolter (30+270)×25.4 = 7620 damage | Set | 260 |
| Shoota sustained check | — | 143.8 dps | ±10 % | 138.5 (−3.6 %) |
| Shoota range, fall-off, multipliers | 100, data | bolter's | copied | bolter's |
| Slugga damage | 2 | bolt pistol 40 / 0.2667 s = 150 dps | ×37.5 | 75 |
| Slugga cooldown, clip, reload | 0.5 s, data | — | kept | 2 shots/s |
| Slugga reserve | data | (10+80)×40 = 3600 damage | Set | 48 − clip |
| Slugga range, fall-off, multipliers | 100 | bolt pistol's | copied | bolt pistol's |
| Choppa light hit (secondary) | 10 | knife 40 (20) | ×4 | 40 (20) |
| Choppa heavy | 24 | knife Y1 (read at load) | knifeY1/24 | knife Y1, its stun |
| Choppa charge (sprint attack) | 15 | `ability_bullrush_01` 75 + 25 shield | ×5, shield Set | 75 + 25, stun |
| Stikk damage | 30 health / 70 shield-only | frag 200 / none | ×6.667 / ×0 | 200 / 0 |
| Stikk radius and fall-off | 8 | frag 10 and its bands | ×1.25, bands copied | frag's |
| Stikk count / initial / cooldown | 5 | 2 / 1 / 1.0 | Set | 2 / 1 / 1.0 |
| Stikk speed, bounce, fuse, models, effects, sounds | 30, 1, 2 s, data | — | kept (feel) | data |
| Run / sprint / walk | 6.5 / 10 / 3.6 | same | 1 | equal |
| Strafe | 6.5 | 6.0 | ×0.923 (O4b, owner) | 6.0 |
| Back | 6.5 | 4.5 | ×0.692 | 4.5 |
| Aim speeds | 6 / 4 / 4 | 3 / 3 / 2 | the Tactical's (O4b, owner) | 3 / 3 / 2 |
| Raise delay | data if present | marine's | data, else reference | — |

## 3. Protocol 24 (one bump; shipped as protocol 28 in O5)
- `Hello` gains `OrkLook` (u8; 255 = automatic) → 176 bytes; `Setup` the same → 139; the Challenge stays no larger than the Hello.
- `Appearance` accepts race 3; an Ork's code is `AppearanceCode.Default.WithPredefinedSet(look)`; `OrkAppearanceRules` builds it from
  scratch (no pieces, palette, decals or emblem); `Apply` takes `Race`.
- Server: `JoinAppearance` and `OnSetup` take the race from the class; `AppearanceState.PendingRace`, applied in `PromoteAppearances`.
- Snapshots, scoreboard, kill feed, shot records unchanged (worst case 1200). The config hash changes.

## 4. Client model path
- `src/SmContent/RigProfile.cs`: `RigProfile(Name, Archive, Entry, BodyVisual, Graphs…)`, `RigProfiles.Marine`, `RigProfiles.OrkBoy`,
  and the per-rig clip table; `Load(gameDir, RigProfile)` overloads for each reader (the marine overload delegates).
- `RigAssets.Load(gameDir, RigProfile)`; `client/OrkAssets.cs` builds a `MarineAssets` (class name kept) from the boy mesh. Looks (ours):
  `boy_ranged`, `boy_melee`, `boy_ranged_pro`, `boy_melee_pro` (`boy_ardboy` only if it binds to the boy skeleton); dismemberment and LOD
  blocks skipped for now.
- Ork materials (`diff2_vtxblenduv01_norm_spec_lp`) reuse the map's two-layer vertex-blend path, skinned; no paint.
- DCM fix (`src/SmFormats/Dcm/DcmMesh.cs`): for skinned blocks read `Unknown6` u32s after each material record into
  `DcmMaterialRange.BonePalette`; confirm on the install that vertex bone indices index the range's palette and remap per range.
- `ModelKey` = `race|look|…` (`ork:<look>`). Weapons in hand on the wargear's prop bones (shoota `RIG_Prop_M_Chest`, slugga
  `RIG_Prop_LHand`, choppa `RIG_Prop_RHand`); stowed spots from a small table of our offsets (the boy has no `MKR_Stow_*`).

## 5. Animation
`RigClipTable` (in `RigProfile`) holds the names the client hard-codes today; the graph maps feed the existing readers.

| Gap | Default |
|---|---|
| 4-direction run | blend the two neighbouring directions (builds on the M40-T4 blend layer) |
| No relaxed stance | the 2HR aim run always; the raise delay stays (parity) |
| No pistol set | 1HM set (slugga left hand, choppa right, as the AI boy carries them); additive 2HR aim masked to spine and left arm; a small procedural recoil (ours) |
| No 2HR sprint | `BOY_1HM_NAV_Sprint_F_01` for every weapon |
| One reload clip | `BOY_2HR_RNG_Reload_S_01` over the weapon's reload time (as M35); the shoota plays it about 2× (fits "silly") |
| Throw | `BOY_2HR_RNG_GrenadeToss`, release tag mapped to the sim's release |
| Reactions | `BOY_REACTIONS_Coop`, stretched to the sim's marine timings |
| Evade | the side hop (1.5) |
| Charge | the choppa sprint attack if readable, else none |
| Death | Ork death clips, then the Ork ragdoll |

"Silly" options (ours; on by default, `--no-ork-silly`): a taunt with a roar bark after a kill when standing still within 2 s; a
"WAAAGH" roar on charge or sprint start (at most every 6 s); death yells and grunts from the clip tags; the kill feed names "Ork Boy"
and "stikkbomb".

## 6. Sound
`SoundTriggers` adds the `ork_coop_boy` blueprint with `ForBlueprint(name)` lookups; Ork clip tags map through the ebp's
`triggered_actions` to random containers (`play_or_bo_*`, `play_or_boy_footstep`, `play_or_choppa_*`); weapon sounds from the wargear
(`play_or_slugga_fire`, `play_or_shoota_fire`, `play_ork_gun_*`, `play_or_grenade*`); impacts on an Ork use the flesh layer only (ours).
**Done in O8** (what the data binds, what plays and what is ours: spec "Ork sounds and barks (Ork O8)"; the Ork's voice is the blueprint's `audio_ext/character` = `ork_boy_2`,
the speech bank has a line for every exertion his files name; the impacts use the armour and flesh layers together since M38, the owner's direction).

## 7. Customizer
- Faction step first (`client/Customizer/FactionStep.cs`); `Selection.faction` (null = space marine) and `orkLook` in the profile
  (optional fields; schema stays 1).
- Orks: one ORK BOY tab (defaults plus custom slots from the shared 10); slugga, shoota and choppa shown fixed; stikkbomb only; the
  Tactical's perks; a Look row (2–4 variants); no armour, colour or badge tabs; "Fight for: Either / Space Marines / Chaos"
  (→ `RequestedSide`, default Either). Preview on the Ork rig. The in-match picker offers your side's loadouts and the Ork loadouts.

## 8. Work breakdown
| # | Task | Files | Size | Depends on | Acceptance |
|---|---|---|---|---|---|
| O1 | DCM multi-material skinned fix | `src/SmFormats/Dcm/DcmMesh.cs`; a synthetic test | S | — | synthetic 2-material skinned block parses and remaps; `smtool verify-all` still clean; `boy_ranged.dcm` loads |
| O2 | Rig profiles and reader overloads | `src/SmContent/RigProfile.cs`; `CharacterModel`, `HitVolumeTuning`, `RagdollModel`, `LocomotionAnimations`, `HipFireAnimations`, `ReloadAnimations`, `EvadeTuning`, `ReactionTuning`, `WeaponModels`; tests | M | — | Ork-named synthetic graphs give a 4-direction set, a side-only evade, a reload clip; marine output unchanged |
| O3 | Ork model and idle (first visible Ork) | `client/OrkAssets.cs`, `client/OrkTestView.cs`; `RigAssets.cs`; `MapMaterials.cs` | M | O1, O2 | screenshot: idle Ork with original textures, every look |
| O4 | Ork class, bodies, balance, sides | `OrkClass.cs`, `OrkBalance.cs`; `MpClasses`, `GameplayTuning`; `SmSim` (Loadout, Simulation, Teams, Evade, Combat, Cone, Projectiles, RushWorld, JumpWorld, PerkWorld, Props); `smtool ork-balance`; tests | L | O2 | requested side, per-class speeds and hit volumes, Data mode = data, Parity = section 2, side-only hop, hash changes |
| O5 | **Done** (branch `ork/o5-wire`; protocol 28, not 24: main had moved on; Hello 176 / Setup 139 confirmed; worst case 1200; the Ork plays in every match, Parity, `--ork-data` on server and client for Data; spec "The Ork Boy on the wire and in the server (Ork O5)"). Protocol 24 and server | `Protocol.cs`, `Appearances.cs`, `GameClient*.cs`; `GameServer.cs`, `GameServer.Appearance.cs`, `AppearanceRules.cs`; `SmSim/Appearance.cs`; tests | M | O4 | round trips; Hello 176 / Setup 139; worst case 1200; race changes at the respawn; no emblem pull for an Ork. From O4: `GameServer.OnHello` passes `World.AssignSide(class, requested side)` to `World.Spawn(.., team:)` (an Ork otherwise goes to the smaller side); the server and client load the same `OrkBalanceMode` (`GameplayTuning.Load(gameDir, mode)`; in the config hash); `GameClient` scales the victim bar by the victim class's body (`Loadouts.Body(config, victim)`, not `_config.HealthMax` / `ShieldMax`; matters in Data mode) |
| O6 | Weapons on the Ork | `WeaponRig.cs`, `SmContent/WeaponModels.cs` | M | O3 | screenshot: in hand and stowed |
| O7a | **Done** (branch `ork/o7a-model`, on `ork/o5-wire`; client only; spec "The networked Ork: model and locomotion (Ork O7a)"): a networked Ork (own and others) wears `OrkAssets` on the boy rig, look by the wire's byte or player id mod 4 (`OrkLookRules`), the weapons on the boy's prop bones (stikkbomb in hand until its release), the 2HR aim run (four directions, aim down / forward / up) or the 1HM run through the M40-T4 blend space with strides measured on the boy's feet, the 1HM sprint, idle with additive aim offsets, the boy's ragdoll as the corpse; `NetworkView.Ork.cs` plus small hooks (`Place`, `Spawn`, `EnsureModel`, `SyncWeapons`, `ProcessDeaths`, the ragdoll prewarm, `CharacterViewer.LegIk`, `WeaponAssets.FromCatalogue`); engine-free `SmContent/OrkLocomotion.cs` with tests. Left for O7b / O7c: reactions, charge, melee, hop, death clips, turn-in-place, run start, recoil, reload, throw release. Networked Ork: model and locomotion | `client/NetworkView.Ork.cs`; `NetworkView.cs`; `NetworkView.Locomotion.cs`; `CharacterViewer.cs` | L | M40-T4 merged, O3, O5 | two headless Ork bots on opposite sides fight; run blend captured. From O4: the client's own uses of body values take the Ork's (`SimConfig.For(class)`): chest height and volumes for effects, the aimed-enemy bars' maxima (`NetworkView.cs` `AimedHealthMax`), the customizer and in-match previews' stats |
| O7b | **Done** (branch `ork/o7bc-actions`, on `ork/o7a-model`; client only; spec "The networked Ork: actions and reactions (Ork O7b / O7c)"): the boy's additive shot recoil per shot seen (`BOY_2HR_RNG_Fire_S_01`; the slugga uses it too), the reload clip over the weapon's reload time with the graph's upper-body mask through the M35 machinery (`OrkReloadData`; no magazine mesh on the Ork guns, so none drops), the stikkbomb toss warped so its release is the simulation's (`OrkActionRules.ThrowClipTime`; moving: toss on the upper body). Hip fire and the aim pose need nothing of their own: the boy's graphs have no relaxed stance (the 2HR aim run of O7a is the always-ready posture). Hip fire, aim, reload, throw | `NetworkView.OrkActions.cs`; small hooks in `NetworkView.Reload.cs`, `NetworkView.Weapons.cs`; `SmContent/OrkActions.cs`; tests | M | O7a | reload capture, hip probe: headless Ork fights with no exception; screenshots of a shot, a reload, a throw in the private cache `ork-o7bc` |
| O7c | **Done** (same branch): reactions (`BOY_REACTIONS_Coop` light / heavy front and back, the clip to its `BRANCH_Reaction_End` over the marine time the simulation holds the player), the choppa's combo and heavy clips (with the marines' leg hand-off), the charge (the sprint attack; the stop plays on from its hit), the dodge hop (`Evade4m_L/R` over the hop's 1.167 s, the body lowered by the hop height), death clips for the blow (`OrkDeathHold`: until the graph's `Pre-Powered Ragdoll` window ends, then the boy's ragdoll starts from that pose). The boy has no turn-in-place, run-start or side-hit clips. Evade, reactions, melee, death, ragdoll | `NetworkView.OrkActions.cs`; small hooks in `NetworkView.Death.cs`, `NetworkView.ClipSmoothing.cs`, `NetworkView.Reactions.cs`, `NetworkView.cs` | M | O7a | bot melee capture, ragdoll at rest: headless bot fights with every action and no exception; screenshots in `ork-o7bc` |
| O8 | **Done** (branch `sound/ork-and-match`, on `ork/o7a-model`; client only; spec "Ork sounds and barks (Ork O8)"): the Ork blueprint joins `SoundTriggers.PlayerBlueprints`, the boy rig's clip tags join the marine rig's table, the Ork class's voice is the blueprint's `audio_ext/character` (`ork_boy_2`); a player of the Ork race sounds as an Ork (`BlueprintName`): footsteps on `EVT_Left/Right_Foot` (`play_or_boy_footstep`), the landing, death action, the shoota, slugga and choppa sounds, and every `ACTN_SND_Vox_*` tag a shown clip crosses; the simulation's events post the boy's lines while his clips are not all drawn (death yell, damage grunts, stun line, hop landing, choppa swing; ours); the silly options (roar at a charge or sprint start, kill taunt; `--no-ork-silly`). `smtool ork-sounds`, `smtool audio-coverage` and `smtool audio-tags` list the Ork's events and tags. Sound and barks | `SmContent/OrkSounds.cs`, `SoundTriggers.cs`, `client/NetworkView.Sound.Ork.cs`, `NetworkView.Sound.cs` | S–M | O7a | `smtool audio-coverage` lists the Ork tags |
| O9 | **Done** (branch `ui/customizer-ork`; client and profile only, no protocol change; spec "The customizer's preview pose and the Ork Boy in the menus"): in Extended the creator's faction row adds Orks and a fourth class tab "Ork Boy" (fixed gear, the Tactical's perks; `OrkLoadout`), the Join screen an Ork DEFAULTS chip, custom Ork loadouts (role `ork`, kept by `ProfileStore` in every profile), the LOOK step (`orkLook` in the profile, automatic or a look index) instead of the appearance editor, a Join / Deploy sends the Ork class and look; the preview draws the boy on his rig in his idle. Not done: a "Fight for" row (the Join screen's SIDE serves). Customizer faction step | `client/Customizer/*`, `client/FrontEnd/*`, `ProfileModel.cs`, `ProfileWire.cs`, `JoinPlan.cs`; tests | M | O3, O5 | round trip with faction and look; keyboard-only flow; Ork preview |
| O10 | Silly options (the sound half is done in O8: roar and kill taunt) | `NetworkView.Ork.cs` | S | O7a, O8 | capture of a kill taunt |
| O11 | Docs | spec, changes-from-original, architecture, CLAUDE.md | S | all | rows for every ours or parity value |

Order: O1 and O2 in parallel, then O3 (first visible Ork, no server); meanwhile O4 then O5; then O6; after M40-T4 merges, O7a–c, then
O8, O9, O10. Hotspots: `Protocol.cs`, `GameServer.cs`, `Simulation.cs` (O4/O5 only); `NetworkView.cs`, `CharacterViewer.cs` (O7a only).

## 9. Open questions (our defaults, applied unless the owner says otherwise)
1. Orks on both sides look identical → a small side-coloured glyph above Orks and a red enemy crosshair (ours); no texture swap.
2. ~~The Ork's hit volumes are smaller than the marine's~~ **Closed (owner, 2026-10-28): the Ork keeps its own hitbox**, from the Ork boy's data. The
   Exterminatus rig's ragdoll (`havok_boy_coop__hkxx`) is used; the campaign boy's (`havok_boy__hkxx`) is identical to the millimetre (O4b). Projected
   area front 70 %, side 65 % of the marine's; no scaling.
3. ~~Back speed capped at 4.5, strafe kept at 6.5~~ **Closed (owner, 2026-10-28): every movement speed is the Tactical marine's**, aiming speeds included
   (strafe 6.0, aim 3 / 3 / 2; O4b).
4. Stikk timing: keep the stikk's 2 s ground fuse and speed 30 → yes.
5. Shoota and slugga use the marine's 250 m range → yes.
6. Charge from the choppa sprint attack → yes if readable, else none.
7. Ork looks: 4 choices, automatic by player id by default → yes.
8. Silly options on by default → yes (`--no-ork-silly`).
Technical risks: the DCM palette meaning until probed; Ork graph names not yet dumped; Ork clips stretched to marine timings; the
choppa combo's dps vs the knife's; AI wargear lacking MP fields; no Ork armour-hit effects for the parity shield.
