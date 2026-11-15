# Refine register sweep (2026-11-16)

Owner-approved documentation and accuracy pass over the refine register (`docs/refine-register.md`), Classic PvP first. Branch `claude/refine-register-sweep`, cut from `claude/evidence-p0`. **No gameplay value and no code constant was changed.** Musou rows were not touched. Nothing was settled by guess: a row was closed only when the spec itself already says a later row replaced it, or when the row is engineering with nothing to compare with SM1.

## 1. Counts

| | Before (evidence-p0 tip) | After |
|---|---:|---:|
| Spec table rows scanned | 3329 | 3329 |
| Tagged rows (Part A) | 2441 | 2433 |
| Not tagged (read from data, high) | 620 | 616 |
| Appendix (test aids, engineering, superseded) | 268 | 280 |
| Part C rows (changes document) | 738 | 738 |

Net: 9 rows left Part A, 1 came back (`R-3D3F6639`, see 4), 4 untagged rows moved to the appendix.

## 2. Rows closed and why

Two mechanisms, both small and tested (`RefineRegisterTests`):

- **Kind `superseded`** (generator: `RefineRules.IsSuperseded`): a row whose Kind cell starts with "superseded" is history and is listed in the appendix, not in Part A. The spec rows below already said in their own text that a later row replaced them; only their Kind and Confidence cells changed (`superseded (was <old kind>, <old confidence>)`, `—`). The replacement row keeps the live tag. A spec row that is only partly superseded was left alone.
- **Engineering names** (generator: `EngineeringSubject`): rows named exactly `Protocol` or `Server log` whose source or kind is ours are wire or log formats, with nothing to compare with SM1; they go to the appendix like `Protocol 18`.

| R-id | Row | Why closed | Replacement |
|---|---|---|---|
| `R-7EE1950B` | The charge: Binding (sprinting forward) | superseded by protocol 39 and 49 | "The charge needs the sprint", Charge request (protocol 49) |
| `R-98B884B3` | The charge: Start gate (M31), 7.375 m/s sprint speed | superseded by protocol 39; `Rush.SprintThreshold` and `RushSpec.SprintMargin` are gone (the `SprintMargin` left in `SprintStop.cs` is the sprint stop's) | same |
| `R-AD722D2E` | Charge start (protocol 39) | superseded by protocol 49 (Sprint held as well) | same; its 0.7 stick share is restated there |
| `R-AD4F6782` | Weapon switch time, 0.6 x 0.8 = 0.48 s | superseded by the quickdraw (0.1 s, 2026-11-06) | `R-8813BF01` (carries the data's 0.467 s and 0.667 s) |
| `R-CB6E42DC` | Air-Cooled Thrusters pound delay (2 s) | superseded by protocol 73 (no weapon waits for it); `Loadout.cs` comment confirms | "Pound from any weapon in the ascent" |
| `R-ABCCA53F`, `R-BA478E13`, `R-7280DE6D`, `R-595B1816` | `Protocol` (3 rows) and `Server log` | engineering, ours | none needed |

The appendix also now lists three rows that were already untagged superseded rows (`R-ACF31455`, `R-2DF998F2`, `R-79F12026`) so nothing is hidden.

Left on purpose: partly superseded rows that still carry a live interpretation (`R-FEBBDE3E` pound start binding, `R-8DA81460` perk row whose recharge and height-map values are live, `R-27642325` firing ends the sprint in the air, `R-65A4BC36`, `R-E2018D4F` bent bullets behind `--bent-bullets`, `R-80A30BFB` stim glow behind `--old-stim-glow`, `R-5100F4DE`).

## 3. Spec and code disagreements found (neither side changed)

None found in the values I spot-checked (health 100, regen 5 s / 5 per s, shield 100, regen 7 s / 33 per s, respawn 5 s, tick 30, `smash_available_delay` use). Observations to confirm, not claimed as defects:

1. `SimConfig` defaults for speeds are 5 / 5 / 5 / 8 / 3 (run, strafe, back, sprint, walk, `Simulation.cs`), while the spec's extracted rows say 6.5 / 6 / 4.5 / 10 / 3.6. The spec says `GameplayTuning` fills them from the install; I did not trace that override, so I treat the defaults as the synthetic no-install values. Worth one test or comment by whoever owns `SimConfig`. Lead check (2026-11-16): `GameplayTuning.cs` reads `run_speed`, `sprint_speed`, `walk_speed` and the aiming speeds from the install's `biped_locomotion_ext` and falls back to the `SimConfig` defaults only when a field is missing, so there is no disagreement.
2. `Evade.cs` carries a stale comment about the direction rule (already known to the lead; waits for a code pass).

## 4. Items folded in from the evidence-p0 review

1. **Direction sign convention** (`docs/evidence-p0.md` 2.1, spec "Evade: Direction", `R-3D3F6639`): it was not established that negative `FLT_LStick_Angle_Char` is left; the report read it from the graph's state names (the state named Left holds the negative wedge), not from the code that fills the variable, and the angle was not measured. A line saying so was added; confidence is now high for Forward and Back and **medium for Left and Right**, in the report and the spec row. Because of the medium, the row is back in Part A (the evidence-p0 "settled" count is amended in its table).
2. **`docs/playtest/WHAT-TO-TEST.md`** (`R-8813BF01`): the tester text now gives the original clips (0.467 s rifle class, 0.667 s bolt pistol, shot at the end) and says our 0.1 s quickdraw is the owner's decision.

## 5. Rows that need the owner or a playtest (not touched)

- Owner decisions: the charge's reach and the side charge's 25 degrees (spec "The charge", "Charge directions"); the charge steering window (evidence-p0 section 3, item 2); whether the evade should end on Aim and Grenade (item 4); the back-diagonal evade clips (item 5); the quickdraw's 0.37 to 0.57 s lead over the original (item 3).
- Playtest (stopwatch or frame count against SM1): ready hold after a shot (`R-C64B6FB4`, 10 s is the owner's rough estimate), vent duration (`R-99C06BDF`, owner's 6 s against the data's 2.1 s), respawn time 5 s and Rapid Deployment 3 s (no number in any data: placeholder), walk speed use (`R-90435565`, when the original walks is unknown).
- Duplicate candidates, not merged because the second table has no Kind column to carry the closure: `Respawn time` in "Provisional values in use" (`R-72F9868D`) repeats `R-BED2640F` in "Teams and Annihilation" and the Part C row `C-D2C7690E`.
- Contradictory kind and confidence cells I could not settle without a guess: spec row "Respawn and death" under "Evade cooldowns" (kind `interpretation`, confidence `high`, source `ours`); rows reading `ours` with `high` that are tested client rules (responsive controls tap latch, early tick, fresh aim). A tested rule of ours is still ours, so they stay tagged; the owner may prefer a new kind such as "engineering, tested" for them.
- Rows with no Timing basis or Units cell (about 96 outside Musou and Ork, listed by a throwaway script: mostly non-numeric rows such as "Protocol", "Look", "Early release", "Splash flags"): not filled in; inventing a unit would be a guess.

## 6. Grouping for playtesters

The generator already groups Part A by area (critical areas first) and by spec section, with a Compare prompt and a Result box on every row; no grouping change was needed.

## 7. Checks run (author's own, not independent QA)

`dotnet build SmReimpl.slnx` and `dotnet build client/SmClient.csproj`: 0 errors. `smtool refine-register --check`: ok (3329 rows, 2433 tagged). Filtered `dotnet test` capped at `-m:4` (PublicText, RefineRegister, Spec, Evade): SmFormats.Tests 161 passed (includes `PublicTextGuardTests`, `RefineRegisterTests` with the two new cases), SmNet.Tests 143 passed. The full suite was not run.

# Pass 2 (branch `claude/refine-register-sweep-2`, 2026-11-16)

Second owner-approved pass over Part A, Classic PvP areas only (Musou and Ork rows untouched: the Ork area stays at 249). **No gameplay value and no code constant was changed**; the spec edits are Kind, Confidence and Source cells and one note. Honest scope: a full read of all 2184 Classic rows against code and install data was not possible in one pass. What was done: every row's kind, confidence and source cells were scanned by script for contradictions; every row whose subject is engineering, measurement or history was found by name, section and value patterns and read before closing; every `extracted`, `data` or `observed` row with a bare "medium" was read against the code and the data it cites. 66 rows closed; the rest need a human against SM1.

## P2.1 Generator changes (`RefineRules`, tested in `RefineRegisterTests`)

- **Kind `engineering measurement`** or **`engineering rule`** (the Kind cell starts with it): a cost, memory or frame-time reading of our own build, or a wire, snapshot, hardening or load-safety rule. Listed in the appendix; nothing in SM1 to compare with. The old kind stays in the cell: `engineering rule (was ours)`.
- **`duplicate of R-XXXXXXXX`** at the start of any cell of a row (the Source or Where cell when the table has no Kind column): the row repeats another register row, which keeps the live tag. Listed in the appendix.

## P2.2 Counts

Part A rows (the generator's "Tagged rows"); before is the pass 1 tip (`730e71ad`):

| Area | Before | After |
|---|---:|---:|
| Movement | 416 | 403 |
| Combat core | 22 | 21 |
| Weapons | 119 | 119 |
| Melee | 158 | 157 |
| Jump pack | 85 | 81 |
| Charge | 59 | 59 |
| Reactions | 25 | 25 |
| Camera | 55 | 55 |
| HUD and hit feedback | 73 | 73 |
| Sound mix | 90 | 89 |
| Effects | 98 | 95 |
| Game modes | 46 | 45 |
| Perks | 59 | 52 |
| Death and ragdoll | 14 | 13 |
| Customizer and paint | 48 | 46 |
| Animation blending | 7 | 7 |
| Lighting and rendering | 31 | 29 |
| Networking and render smoothing | 25 | 17 |
| Core simulation (Classic and Musou, mixed) | 747 | 725 |
| General game design | 7 | 7 |
| Ork faction | 249 | 249 |
| **All areas** | **2433** | **2367** |

Appendix 280 to 343 (+63: 66 rows left Part A; 3 of them, `R-3C5DC868`, `R-39C246EA` and `R-93B0A98E`, became untagged instead, so untagged is 616 to 619). Weapons, Charge, Reactions, Camera and HUD did not move: their open rows are real estimates or owner recollections.

## P2.3 Rows closed

**Closed by data (kind and confidence now say so)**

| R-id | Row | Reason |
|---|---|---|
| `R-93B0A98E` | Max walkable slope | the value (60 degrees) is the install's `max_walkable_slope`, read at load by `GameplayTuning`; the only tag was a note that the controller rules around it are ours (those have their own rows) |
| `R-3C5DC868` | Evade: Duration | confidence medium to high: 1.1667 s and 0.9667 s are the clips' lengths, read at load into `EvadeMove`; the per-tick basis is in its own cell |
| `R-39C246EA` | Sprint stop: time | confidence medium to high: the clip, its branch tag and its length are the data's, read at load; 0.833 s applies only when the graph cannot be read |

**Superseded (kind `superseded`)**: `R-44515378` Hit flinch: slot choice and `R-06EB519B` Death hit: choice, both replaced by the hit reactions of 2026-11-11 (the old rows are kept only behind `--old-hit-reactions`).

**Duplicate**: `R-72F9868D` (Respawn time, "Provisional values in use") duplicates `R-BED2640F` (Teams and Annihilation). The Part C row `C-D2C7690E` is unchanged.

**Engineering measurements (appendix)**: `R-EA55CD79`, `R-5587D429`, `R-49047B63` (animation pose cost), `R-60F22C0C`, `R-EF54CD93` (GPU frame time), `R-498FA589` (no-smoke cost), `R-8075DC70` (scope overlay cost), `R-BF10C7C3` (bot memory), `R-AC4906B9` (sound stream cost), `R-5FBBC333` (menu sound cost), `R-D2E922CC` (customizer look cost), `R-029F8653` (death effect cost), `R-E5EA2A49` (menu video cost). These are readings of our build; SM1's own frame cost is not something to match. Their kind was `measured`, `extracted`, `observed` or `ours`, with confidence medium.

**Engineering rules (appendix)**
- Wire, snapshot and prediction rules: `R-136EE8C9` kill feed, `R-6CD98EDA` death-view data, `R-B3B0DB48` perk kill cause, `R-5228203E` others' equipment, `R-F6252E69` own stim time at a reconcile, `R-981F38FA` emblem bytes, `R-F86598F9` emblem pulls, `R-D66C5B56` setup limits, `R-544497B2` True Grit log, `R-FBF08696` render clock test.
- Server and client hardening: `R-D7526043` join rate, `R-0BFCE5E9` repeated Hello, `R-D9C2F998` tick catch-up, `R-2E8E90AA` crash isolation, `R-82B2FE16` UDP receive, `R-03B89F9B` respawn choice rate, `R-7BAED7ED` connect timeout, `R-F0EEFE25` hosting port check, `R-0F64DB7A` failure isolation of the gait data, `R-D7E578E8` looks built off the frame, `R-EDECAEC7` map load off the main thread.
- Stall and log: `R-D29B5413`, `R-804365BD`, `R-A9B08743`, `R-ECF314B5`, `R-6F965540`, `R-599C739E`, `R-89EEAE92`, `R-96F42DFA`.
- Performance settings: `R-9CEA0140`, `R-C6583162`, `R-96A0A21B` (lean bots), `R-9CBB7590`, `R-900DB710` (GC modes), `R-54E913E5` (ReadyToRun).
- Display-name plumbing: `R-A40BE858` name seed, `R-4DE26DA6` name characters.
- Test bots, which have no SM1 counterpart: `R-F831153D`, `R-25F3C186`, `R-8389442F`, `R-FB826BA2`, `R-261BAA24`, `R-F39784AA`, `R-BB28D290`. Kind `test aid` (their Source cell already said so): `R-FD9EB805`, `R-C10ACB8E`, `R-D196B53C`.

**Kind corrected (still tagged)**: `R-40BF1F5D` "Respawn and death" (under "Evade cooldowns") was kind `interpretation`, confidence high, source ours. It is a rule of ours, tested: kind is now `ours (was interpretation: ...)`. `R-2A21D9CD` "Rotation sign reference" had the same contradiction and the same fix. Neither is closed: a tested rule of ours is still ours.

## P2.4 Spec and code disagreements

None found. Constants compared with `SimConfig`, `MatchRules` and `Loadouts`: tick rate 30, gravity 20, respawn 5 s and Rapid Deployment 3 s, capsule 0.45 / 1.9, step 0.4, fall speed 60, head and body hit volumes, melee reach 4, cone 60 degrees, vertical reach 1.5, Annihilation score limit 41 and time limit 600 s, capture 20 s, neutralise 10 s, Seize score interval 1 s. All agree. The install-read values (`GameplayTuning` overrides them at load) were not compared one by one.

## P2.5 Why the other rows stay

- **`extracted` rows at medium (46 outside Musou and Ork)**: read one by one. The medium is the stated caveat in most: the time unit of `delayed_action`, cooldown and lifetime values is assumed to be seconds, and the heat rate is assumed per second (cell text "seconds assumed" or "unit assumed"; the weapon and projectile rows in "Charged fire, projectiles and splash", "Thrown equipment and the melta cone" and "Blind grenade"). The code does read them at runtime (`WeaponTuning`, from the blueprints), so the values are right; the unit is what is unconfirmed. I did not raise any of them to high: that would assert the unit. One timing per class of value in SM1 would close the group (see P2.6).
- **Units and timing basis**: nothing filled in. Every numeric Classic row already has a unit and a timing basis except `Max walkable slope` (timing basis empty; the data does not say) and `Shading gain` (no timing basis); the empty cells that remain are on non-numeric rule rows.
- **`ours` at high confidence (90 rows)**: tested rules of ours, not contradictions (the owner may prefer a kind such as "ours, tested").
- Owner recollections stay labelled as the owner's (`user's knowledge`, `owner's decision`, `owner recollection`).
- The table "Provisional values in use" has no Kind column, so its engineering row `R-15CA0344` (controller constants) could not take the new kind; extending the generator to read the Source cell would close it and a few more.

## P2.6 For the owner or a playtest, grouped for a playtester

1. **One stopwatch session on time units** (closes about 40 weapon, projectile, grenade, heat and blind rows at once): in SM1 time the frag grenade cooldown (data 1), the melta cooldown (0.33), the plasma gun's charged shot (stage at 1.1 held), the heavy bolter's cooling (25 per second from 0.3 s after the last shot) and the grenade launcher's reload (0.6). If each matches seconds, the unit is confirmed and those rows go to high.
2. **Frame-counted video of the evade and sprint stop**: the roll's end (1.1667 s), control return (0.667 s), the sprint stop clip (0.833 s); this checks the timing basis of the rows just closed at the value.
3. **Owner decisions still open** (carried from pass 1): the charge's reach, the side charge's 25 degrees, the quickdraw's lead over the original, the evade ending on Aim and Grenade, the back-diagonal evade clips.
4. **Gaps no data can close** (play the original): the perks `respawn_timer`, `enemy_info`, `secondary_weapon`, `friendly_spawn_target` and `snap_to_increase` (not in the data; the owner's descriptions stand), spawn protection (`R-956A1C89`), fall damage (`R-71FF1CE8`), players colliding with each other.
5. **Sound, HUD, camera, effects** (about 410 rows, all ours or estimates against the original's look and sound): the Compare prompt of each row is the playtest; no row of these areas could be settled by data.
