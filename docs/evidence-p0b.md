# Evidence pass P0, round 2 (2026-11-16)

Round 2 of the owner-approved evidence plan ("keep mining"): the installed game's data, read-only, for the Classic PvP areas round 1 (docs/evidence-p0.md) did not cover: hit reactions, weapons, perks, modes, movement and the jump pack, camera and aim assist, damage. Branch `claude/evidence-p0b`. **No code, constant, protocol or config-hash change was made**; the spec rows that moved are listed in section 2 and every difference between the data and our behaviour is in section 4 ("Owner questions"), together with round 1's held differences, for the lead to put to the owner in one go. Musou Mode values are untouched.

Sources read (archive-relative; install buildid 7412325):

- S1: behaviour graphs through `smtool graph-dump`: `MP_SM_Reactions` (choosers, wedge conditions and their raw objects, reaction sequencers' tags, extracted root motion of the reaction clips), `MP_SM_NAV_JP_Sim` (the jump pack's transitions and landing sequencers), `MP_SM_Bolter_NuSprint`, and `SM_Base_MP` (the jump pack's entry and exit transitions, the plasma vent states).
- S2: BAF attributes through `smtool baf-dump`: `global` (`biped_player_controller_ext`, `directional_targeting_ext`, `combat_ext`, `weight_and_strength`, `multiplayer_settings`), the player blueprints `sm_space_marine_player` and `csm_chaos_marine_player` in full, `camera_input_tuning`, `auto_aim_info`, the plasma gun's wargear; and the constant pools of the `util/mp` Lua scripts (`multiplayer`, `objectives/dominate`, `objectives/deaths`, `spawnmanager`, `data/*`; string and number constants only, nothing decompiled or executed; the layout is in docs/discovery.md "Evidence pass P0", one correction in "Evidence pass P0b").

Method as in round 1: start from the refine register's Part A rows of kind estimate, ours or interpretation (or confidence low or medium) in the seven areas, look for a field, tag, transition or constant that settles or narrows each, and say plainly when none does. The register had a very large Movement and Core simulation share; those rows are mostly the owner's recollections or our own rules, with the data already read in M20 to M45, so most of this pass is negative evidence.

## 1. Result in numbers

| Outcome | Rows | Which |
|---|---:|---|
| Settled (left the refine register) | 1 | Fall damage (`R-71FF1CE8`): none, now high |
| Narrowed (data added, reading sharper, still tagged) | 6 | Hit-reaction clips and sectors, Reactions turned to the attacker, Respawn time, Time limit (Annihilation), Time limit (Seize Ground), Signum Link |
| New fact rows | 1 | Character collision tuning (`global` `weight_and_strength`; not used) |
| Examined, not settled (no row change) | 16 | section 3 |
| Owner questions (data differs from ours) | 10 | section 4 (5 new in this round, 5 held from round 1) |

The register count change is in the report's check section (section 5).

## 2. Per row

### 2.1 Hit reactions: wedge order and reference (narrowed; the largest finding)

Spec "Hit reactions, heavy strikes, knockback and blindness (M28)": row Clips (`R-E6D7626D`), and "Hit reactions turned to the attacker" (`R-199E0479`, `C-283346F3`).

What the graph file says, read from the raw objects (`smtool graph-dump MP_SM_Reactions --raw hkbPieWedgeCondition`, `hkbConditionChooser`, `hkbConditionChooserConditionEntry`, `hkbAttackAlignmentSequenceTag`):

- A "wedge" is an `hkbPieWedgeCondition` with a range (distance) float @12 (10 for the melee choosers, 1000 for the grenade and stagger machines: read as the attacker's maximum distance), the angle range @16 and @20, a flag word @24 (1 on the BACK entry of every front/back chooser, absent on FRONT), and an int @36 (2 on the entries tested against the whole front half, -1 on the side wedges).
- Front/back choosers (the light interrupt "CSM MA1", the Blood Letter reactions): both states carry -90..90; the BACK entry (listed first) has the flag @24. So FRONT is a hit within 90 degrees of the front and BACK is the other half; ours (halves by the dot product) agrees.
- The grenade (stun) machine "Behavior06" lists B -90..90 (priority 4), L -135..-45 (3), R 45..135 (2) and F with no condition (1); Havok tries the highest priority first. The stagger machine "Behavior12" lists B -90..90 (4) and F (1). So, with angle 0 = the source in front: **B for any hit within 90 degrees of the front, L for 90 to 135 degrees on one side, R for 90 to 135 on the other, F for the rear 90 degrees** (the sectors overlap in the file; the priority order cuts B's 180 degrees first). Stagger: B in the front half, F in the rear half.
- **The clip letters of the stun and stagger clips name the direction the body is thrown, not the side of the source.** Extracted root motion (forward = +y, right = +x in the right-handed rig): `SM_MP_REC_Grenade_F_01` +2.0 m forward, `_B_01` -2.1 m, `_R_01` +1.9 m (right), `_L_01` -2.0 m; `SM_REC_Stagger_F_01` +4.0 m, `_B_01` -4.1 m. The interrupt clips are the other way round: `SM_REC_Light_Itrpt_FHL_01` ("front hit") moves the body back 1.15 m and `_BHR_01` forward 1.0 m.
- The reaction sequencers carry `AttackAlignment` windows that rotate the reacting body (`hkbAttackAlignmentSequenceTag`, end @36, the window's start @32 and a lead-in start @28, a target angle @88 on the first window, a flag @80 = 1 on the first window of the stun and stagger clips). Interrupt BACK 0 to 0.24 of its 1.0 s clip with target 180 and FRONT 0 to 0.28 of 1.167 s with target 0; grenade F 0.133 to 0.333 (target 180), R 0.1 to 0.5 (-90), B 0.1 to 0.5 (0), L 0.1 to 0.5 (+90), each followed by a second window (F from 0.433, R and B from 0.6, L 0.6 to 0.9667, to 1.0) with no target; stagger F 0.14 to 0.33 (180) then 0.35 to 1, stagger B 0.1 to 0.5 (0) then 0.6 to 1. The targets are the clip's own orientation to the source: after the first window the B clip has the source dead ahead and the F clip dead behind (a body is thrown away from the source), R and L at 90 degrees. This is the data answer to "whether SM1 aligned victims" (`C-283346F3`): it does, in the first window, by whole angles; the rate and the second window's meaning are not decoded.

Reading and sign: that the angle is counted with 0 = the source in front follows from FRONT/BACK (the same tag, with and without the flag) and from the alignment targets (B 0, F 180). Which of R and L belongs to which side rests on the rig's counter-clockwise-positive convention (the R clip's wedge is +45..+135 and it moves to the right, away from a source on the left): medium.

Our behaviour (`Reaction.DirectionOf`, `ReactionTuning`): four 90 degree sectors; a source in front plays the `_F` stun or stagger clip and one on the right the `_R` clip, so the body is shown thrown towards the source, with the clip's own length and branch times (`ReactAt`/`EndAt`): the front stagger ends at 0.567 s (data: `_B`, 1.067 s), the front stun at 0.533 s (data `_B`, 0.667 s); the rear cases are the other way round; the side sectors are 90 degrees wide each in ours and 45 degrees in the data, and give 0.8 s either way. See question 1 and 2.

New confidence: the clip letters and the wedge test are read from the file (high for F and B, medium for L and R); the spec cell keeps `interpretation` for kind-to-clip-set (the file names ports by weapon type, not by reaction kind) and marks the sectors as differing from the data.

### 2.2 Weapons

Per-weapon times, heat and the grenades were read in M11 to M13, M27 and M35 from the wargear, ability and graph data. Looked at again this round (no change to a row):

- Plasma vent states in `SM_Base_MP`: the plasma weapons enter their Vent on `BTN_Reload` (press, or held), and by themselves on `BOOL_Wargear_Overheated` from navigation; the vent leaves for navigation on `BOOL_Wargear_Vent_NoHeat` (the highest priority), for an evade on `BTN_Nav_Evade`, for a sprint on `BTN_Sprint`, and the cannon's also for a melee press; there is no fire transition (a no-trigger fallback to Fire, priority 4, is not decoded). The wargear's `vent_stages` name the heat fractions of the vent states (`no_heat` 0, `low_heat` 0.01, `medium_heat` 0, `high_heat` 0.5, `overheat`). Both agree with the spec's "Brace and vent" rows; the one place the spec already overrides the graph (sprint stops the vent in the graph, not in ours) is the owner's recollection.
- The heat rows (`R-BE6E6375`, `R-C0955EA4`, `R-9E2CC527`): values are data; the rules (the shot that reaches the overheat still fires, the recovery threshold) are code. No field changes.

### 2.3 Perks

- `snap_to_increase` Signum Link (`R-980B7116`; narrowed): the aim-assist sets `sm_auto_aim_<weapon>_mp__snap_upgrade` and `_zoom_mp__snap_upgrade` exist for three weapons, not two: heavy bolter, **lascannon** and plasma cannon (the earlier note missed the lascannon's), which are exactly the Devastator's weapons, the perk's class. Each differs from its base set in a single field, `max_angle_for_ranged_aim_snap_to`, 0 to 12 (adhesion, magnetism, aim angles and the camera input set are identical, checked for all six pairs). No file names the sets and none reads the perk string, so the link is an inference by name and weapon: the perk's effect is very probably an aim snap of 12 degrees. Aim assist is not simulated (whether the PC mouse used it is unknown), so the perk stays inert (question 5).
- `respawn_timer` Rapid Deployment, `enemy_info` Tactical Readout, `secondary_weapon` Weapon Versatility, `friendly_spawn_target` Teleport Homer: no new data. The `util/mp` constant pools (50 scripts, again) and every BAF package hold no number for them; the rows stay "owner's recollection" (3 s, bars, third slot, homer).

### 2.4 Modes: Annihilation and Seize Ground

- Respawn time (`R-BED2640F`; narrowed): the player blueprints' `health_ext/death_time` is 5 (both races; the Dreadnought's is 10). Our 5 s agrees with it, but what the blueprint field times (the corpse or the respawn wait) is in code and no mode script sets a respawn time (`Network_SetRespawnDelay` takes `team.respawnTime`, which no file sets for versus). Placeholder, now "agrees with `death_time`" (interpretation, low).
- Timed tie, Annihilation (`R-22568AED`; narrowed): `objectives/deaths.lua` has a `SuddenDeath` function whose constants include a `scoreLimit` and the value 1, the HUD text "SUDDEN DEATH" (locale 104179), the score format (104167, 104174) and no draw call; its caller is not in any script. So the original very probably plays a sudden death when a timed Annihilation match ends level, where ours is a draw (question 3). The rest of the Deaths objective (`isDeathMatch`, the score limit from `Network_GetScoreLimit`, a death of a group member scoring for the other team) is as the spec has it.
- Timed tie, Seize Ground (`R-101F7D06`; narrowed): the end function of `objectives/dominate.lua` compares the teams' scores and its constants include `Complete` and `Draw`: a draw on equal scores is in the data's script (ours is a draw). `captureDuration` 20, `decapDuration` 10 and `scoreSpeed` 1 as the spec has; the update timer's interval is not a readable constant.
- Not in any data, still code or ours: score limit values (41, 1000), time limit (10 min), the 1 s score interval, spawn protection (none found), friendly fire default, the match countdown. The spawn-manager data in `util/mp` is Exterminatus (co-op) only.

### 2.5 Movement and the jump pack

- Fall damage (`R-71FF1CE8`; **settled**): the player blueprints' `biped_locomotion_ext/finish_falling_actions` holds the landing sound `play_sm_land_normal`, a 0.1 s camera effect (shake 0.1 and 0.15 amplitude) and the `jump_land` effect, nothing else; `start_falling_actions` is empty; no `do_damage`, reaction or modifier anywhere in the landing action. `health_ext/take_damage_multiplier` has `world` 0.2 (melee, ranged, explosive 1; the Dreadnought 1 for all four), a damage kind the install never feeds from a height. So there is no fall damage; the row is now "none, high".
- Jump launch (`MP_SM_NAV_JP_Sim` "Jumppack Behavior"): the graph's launch needs `!BOOL_JumpIsActive`, `FLT_Distance_Above_Ground` in [0, 0.5] and `FLT_Nav_Speed` in [0, 6.5] (an animation condition: the "Launch" curve clip is chosen when the marine is slower than the run speed); a melee press in the air goes to the pound at 8 m and above and to the dash attack below (already row `R-E7B9EBEC`).
- Landing sequencers: the GP impact clip (1.067 s) has `BRANCH_to_Getup` from 0.333 (0.356 s: the spec's pound recovery of 11 ticks, now seen to be exactly this tag), `BRANCH_To_Idle_Getup` 0.8 (0.853 s). The non-hammer run and idle landings have **no `BRANCH_Jump_To_Melee` tag** (the thunder hammer's do: 0.2647 and 0.3462, 0.3 s and 0.415 s), and `SM_Base_MP`'s "Exotic - JumpPack" state leaves for a melee attack only through that branch, so the graph does not let a chainsword, sword or axe attack out of a landing before the nav exit (`BRANCH_Run` with the stick at 0.333 s run landing and 0.462 s idle landing, `BRANCH_Jump_To_Idle` at 1.033 s of an idle landing). The spec says melee is allowed in a landing (`R-3F38F314`, interpretation): question 4. One unnamed branch tag (0.2963 of the run landing) could not be named by the reader, so this is not settled.
- Character-to-character collision: the new fact row in the spec's Player collision section (global `character_collision_tuning` and the masses; two marines weigh 500, above the 400 below which a body may be ignored). Not used.
- `rotation_max_degrees_per_second` 1080, `rotation_time_till_max_speed` 0.15, `flip_around_*`, `max_walkable_slope` 60 are in the spec as read (some "not used yet"); no new reading.

### 2.6 Camera and aim assist

- `directional_targeting_ext`, `combat_ext` (`safe_time` 5, the intensity block), `biped_player_controller_ext` (`crosshair_acceleration` 6, `crosshair_max_speed` 3, `combat_duration` 5): no new meaning found; `camera_input_tuning` (`speed_mouse` per set, stick curves) is in the spec already. Whether the PC mouse used the aim-assist sets stays unknown (the sets carry `camera_input_tuning_set` and a stick-based magnetism; `use_auto_aim` is per wargear delivery, with no device field).

### 2.7 Damage

- Fall damage: settled above. Head and armour multipliers and the per-weapon values are in the spec (combat pass, protocol 45) with the install as source; no row of kind interpretation is left there that the data could settle. `take_damage_multiplier` `world` 0.2 is noted in the fall damage row.

## 3. Examined and not settled

| Row (spec section) | What was looked at | Why it stays |
|---|---|---|
| Respawn time, Rapid Deployment 3 s, Teleport Homer rules, Tactical Readout, Weapon Versatility | Lua constant pools, BAF, locale | the effects and numbers are code; round 1 and this round found no field |
| Spawn protection | `util/mp` scripts, blueprint `health_ext` (`invulnerable_health` 0), graph | none in any file |
| Score limit (41, 1000), time limit (10 min), the Seize Ground score interval (1 s), friendly fire default | scripts, `multiplayer_settings` | the host option lists are in the executable |
| Seize Ground update timer interval | `dominate.lua` constants (a 100.0 and a 1.0 appear around the timer call) | the compiled instructions would have to be read to tell which is the interval; excluded by the "constant pools only" rule |
| Hit reaction: who reacts, during a reaction, ending, knockback | transitions and tags | values are data, rules code |
| Hit reaction: second alignment window | `AttackAlignment` second windows (no target, lead-in @28) | the field meaning (probably a return of turning to the player) is not decoded |
| Hit reaction: sign of the hit angle | wedges, alignment targets | the sign of the engine's angle is read only through the counter-clockwise rig convention |
| Flinches (`min_hit_reaction_time` 1) | blueprint | not simulated; unchanged |
| Jump pack: height by hold, power costs, gravity by phase, air control | wargear `jump_info`, graph | the physics are code; only the clips' times are in the graph (landing, take-off, pound recovery) and are already in the spec |
| Jump pack: melee out of a landing | landing tags | one unnamed tag, see 2.5 and question 4 |
| Plasma heat unit "per second" | `heat_vented`, `cool_down_rate` | no field states the unit |
| Ready hold (10 s) | round 1 | unchanged |
| Aim assist on the PC | `auto_aim_info` | no device field |
| Grenade fall-off between points (steps or linear) | ability tables | no field |
| Reaction-by-intensity step (0.31 / 0.51) | blast data | the table is data; the choice of the entry for a given intensity is code |
| Fall-off and head multiplier unit questions | wargear | as in the spec |

## 4. Owner questions

One list for the lead to put to the owner. Every item is a difference between the data and what we do now; **none is applied**. "Fix scope": value = one data or config value; logic = simulation or client code change with tests; protocol = the wire or the config hash changes, so a protocol bump and a release note.

New in this round:

1. **Stun and stagger clips are chosen against the source's side (hit reactions: clip direction and sectors).**
   - Original: the grenade (stun) and stagger machines test the angle to the source with 0 = in front, priority highest first: B for within 90 degrees of the front, then L (-135..-45) and R (45..135), F by default (the rear 90 degrees); stagger B in the front half, F behind. The letters name the direction the body is thrown (`Grenade_F` +2.0 m forward, `_B` -2.1 m; `Stagger_F` +4.0 m, `_B` -4.1 m). Source: `MP_SM_Reactions` "Behavior06", "Behavior12" and the clips' extracted root motion (section 2.1).
   - Ours: four 90 degree sectors; a source in front plays the `_F` clip and one on the right the `_R` clip (`Reaction.DirectionOf`, `ReactionTuning`).
   - Size: a frontal stagger locks the victim 1.067 s in the data and 0.567 s in ours (0.5 s longer), a rear stagger 0.567 s against our 1.067 s; a frontal stun 0.667 s against 0.533 s; the side clips (0.8 s each) are chosen over 45 degrees in the data, 90 in ours. The shown clip is also reversed (the body is drawn flying toward a blast in front of it).
   - Fix scope: logic (the sector rule and the clip table, `ReactionDirection` kept) with a config-hash/protocol bump because reaction lock lengths are simulated.
2. **The original turns the reacting body (reaction alignment).**
   - Original: every reaction sequencer has `AttackAlignment` windows: interrupt 0 to 0.24 (back) or 0.28 (front) of the clip, stun and stagger 0.1 to 0.5 of the clip (stagger front 0.14 to 0.33), turning the body by whole angles to a per-clip target (front 0, back 180; grenade F 180, B 0, R -90, L +90), then a second window to the end. Source: section 2.1.
   - Ours: the client eases the reacting body at 25 degrees per second by at most 45 degrees so that the clip's side faces the attacker (`C-283346F3`).
   - Size: the data's turn goes all the way to the clip's target within the window (up to 90 degrees for a hit at the edge of the front half, up to 45 for the side and rear wedges), ours at most 45 degrees at 25 degrees a second; with question 1 fixed, the pairing of clip and orientation changes (a frontal blast: faces the source and flies backward).
   - Fix scope: logic (client presentation, plus the sim's facing if the owner wants the turn to be real), no protocol if only the client.
3. **A tied timed match ends in sudden death in the original (Annihilation).**
   - Original: `deaths.lua` has a `SuddenDeath` function with the text "SUDDEN DEATH" and a score limit of 1; the Deaths objective has no draw; Seize Ground's end has `Draw`. Source: section 2.4. The caller (the timer's end) is in code, so the trigger is inferred.
   - Ours: equal scores at the time limit are a draw.
   - Size: affects only tied timed matches.
   - Fix scope: logic in `MatchRules` plus a HUD banner and a protocol bump if the next point decides the match.
4. **Melee out of a jump-pack landing (non-hammer weapons).**
   - Original: the landing clips for the non-hammer weapons carry no `BRANCH_Jump_To_Melee` tag (the thunder hammer's do, at 0.30 s and 0.415 s); the jump-pack state leaves for a melee attack only through that branch, so a chainsword, sword or axe attack waits for the nav exit: `BRANCH_Run` at 0.333 s (run landing) or 0.462 s (idle landing) with the stick pushed, else `BRANCH_Jump_To_Idle` at 1.033 s of an idle landing. Source: `MP_SM_NAV_JP_Sim` landing sequencers, `SM_Base_MP` "Exotic - JumpPack" (section 2.5).
   - Ours: melee, firing and switching are allowed in a landing (`R-3F38F314`).
   - Size: 0.33 to 1.03 s of no melee after a landing for the assault classes. One unnamed branch tag in the run landing could change this, so the owner's memory decides ("could you chainsword right out of a landing?").
   - Fix scope: logic, probably protocol (it is simulated).
5. **Signum Link's aim snap.**
   - Original: the Devastator's weapons (heavy bolter, lascannon, plasma cannon) each have a `__snap_upgrade` aim-assist set, hip and zoom, identical to the base set except `max_angle_for_ranged_aim_snap_to` 0 to 12 degrees. Source: section 2.3.
   - Ours: the perk does nothing (aim assist is not simulated).
   - Size: a 12 degree aim snap for three weapons, if the PC used aim assist at all.
   - Fix scope: logic (a new aim-snap rule) and protocol; only worth doing if the owner remembers an aim snap on the PC.

Held from round 1 (docs/evidence-p0.md section 3), unchanged:

6. **Charge distance with no target.** Original: the rush's "Smash Blendspace" on `FLT_MeleeTargetDistance` (initial value 1, below 2.5) plays the 2.5 m clip: 12.4 m in all and 3.83 m at the hit time 0.233 s; ours plays the 5 m clip: 14.9 m and 6.32 m. A charge at a target 2.5 to 5 m away blends in between. Size: 2.5 m shorter at every moment. Fix scope: logic plus protocol (the rush picks its variant from the lock's distance).
7. **Charge steering window.** Original: `AttackAlignment` over the first 0.12 of the 1.667 s clip (0.2 s) only; ours steers toward the target at 180 degrees a second while it connects, up to 0.667 s. Size: up to 0.47 s of homing, a turn of up to 85 degrees at the cap. The owner's reference video ("the last 0.3 s turns about 30 to 40 degrees") may contradict the reading. Fix scope: logic plus protocol.
8. **Weapon switch times.** Original: 0.467 s (rifle class, weapon changes at 0.233 s) and 0.667 s (bolt pistol), no `BTN_Ranged` exit, so a shot waits for the end; the heavy bolter and lascannon have no swap state. Ours: 0.6 s default, 0.1 s with the owner's quickdraw (2026-11-06, deliberate). Size: the quickdraw is 0.37 to 0.57 s faster than the original. Fix scope: value (`SimConfig.WeaponSwitchTime`), config hash.
9. **Evade ends on Aim and Grenade.** Original: the evade's exits are the stick, Fire, Reload and Sprint (and a new evade); no aim or grenade transition. Ours: an Aim press or a grenade also ends the evade at 0.667 s. Fix scope: logic, protocol.
10. **Back-diagonal evade.** Original: four direction sets only (forward, back, left, right in 90 degree sectors); no clip or wedge for S+A or S+D, and the forward diagonals fit the forward roll's alignment modifier. Ours: eight directions (owner's recollection, M22) with turned clips for the back diagonals. Fix scope: value or logic, depending on whether the owner keeps the recollection.

## 5. Checks run

Author's own checks (not independent QA): see the report's final message for the exact commands and results: `dotnet build SmReimpl.slnx` and `dotnet build client/SmClient.csproj`, `smtool refine-register` regenerated and `--check` passing, and filtered `dotnet test` capped at `-m:4` (`PublicText`, `RefineRegister`, `Spec`). The full suite was not run. The probes used only the repository's `smtool` readers (`graph-dump`, `baf-dump`, `locale`) and a scratch Lua constant-pool lister (not committed); extracted files stayed in the private cache.
