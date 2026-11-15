# Evidence pass P0 (2026-11-16)

Owner-approved phase P0 of the evidence plan: mine the installed game's data, read-only, for the open interpretations of Classic PvP melee, charge, hit reactions, evade, raise and ready timings, and weapon and perk values. Branch `claude/evidence-p0`. Musou Mode values are untouched. **No code, constant, protocol or config-hash change was made**: every gameplay-visible difference found is listed in section 3 for the owner's decision.

Sources read (archive-relative; install buildid 7412325):

- S1: the marine rig's behaviour graphs in `data/packages/havok_sm_base_mp__hkxx` (`SM_Base_MP`, `MP_SM_Bolter_NuSprint`, `MP_SM_Reactions`, `SM_Plasma_Vent`, the melee graphs), through `smtool graph-dump` (sequencer tags, transitions, state choosers, extracted root motion).
- S2: BAF attributes in `data/packages/wargear.oppc`, `global.oppc`, `ebps.oppc` (`smtool baf-dump`, `smtool locale`) and the constant pools of the 50 compiled Lua scripts under `util/mp` in `data/packages/util.oppc` (string and number constants only; nothing decompiled or executed; the layout is in docs/discovery.md "Evidence pass P0").

Method: started from the spec rows (and their refine-register rows) of kind estimate, ours or interpretation, or confidence low or medium, in the areas above; for each, looked for a data field, tag, transition or constant that settles or narrows it. Time bases below are the clip's own seconds (tag fraction times clip length). Confidence words follow the spec.

## 1. Result in numbers

| Outcome | Rows | Which |
|---|---:|---|
| Settled (left the refine register) | 1 | Evade direction (`R-3D3F6639`) |
| Narrowed (data added, value or reading sharper, still tagged) | 11 | Control returns, Switch, Next evade, Weapon switch time, Ready hold, Vent duration, Charge lock and steering, Rush motion, Contact, Wall, Homing |
| Examined, not settled (no change to the row text) | 9 | section 4 |
| New facts recorded in docs/discovery.md, no row | 4 | compiled Lua layout; `directional_targeting_ext` extra fields; plasma heat comparison; the swap clips' tags |

The refine register went from 2442 to 2441 tagged rows (`smtool refine-register --check` passes). It is a modest count on purpose: M21, M27, M28, M29 and M39 had already read most of what the files hold.

## 2. Per row

### 2.1 Settled

**Evade: Direction** (`R-3D3F6639`, spec "Evade: the Tactical roll and the heavy hop")
- Old: the stick's nearest axis, `|y| >= |x|` forward or back, so exact diagonals go forward or back; "interpretation (sectors ours)", low.
- Data: every evade state machine in `SM_Base_MP` (the rifle roll, the melee-weapon roll, the pistol's heavy and jump-pack evades) starts in a state chosen by `FLT_LStick_Angle_Char`: Forward in [-45, 45], Back in [135, -135] through 180, Left in [-135, -45], Right in [45, 135], tried in that order (graph-dump "start in ... if" lines of the "Behavior00", "Chainsword Evade", "Chainsword Evade00" and "Chainsword Evade01" choosers).
- New: unchanged rule, now data: 90 degree sectors, the inclusive boundaries go to Forward (45 and -45) and Back (135 and -135). `Evade.DirectionOf` already behaves so (checked by reading the code against the wedges).
- Confidence: high for the sectors. Left ours: the 0.1 dead zone for "no stick" (the graph's angle variable is undefined then). The 8-direction diagonals (spec "Diagonal rolls (M22)", owner's recollection) have **no** clip or wedge in the data (only the four sets); forward diagonals are plausible through the forward roll's `Align To Nav Direction` modifier (field 180) and stay as they are; the back-diagonal turned clips have no data basis.

### 2.2 Narrowed

**Evade: Control returns** (`R-1E265688`)
- Old: the mapping of inputs to branches (stick, Fire, Aim, Grenade) was an interpretation, low.
- Data: transitions out of "Bolter Evade" (`SM_Base_MP`): `FLT_Input_Nav_Speed` in [0.5, 100] at `BRANCH_Run` to navigation (0.5714 of the 1.1667 s roll); `BTN_Ranged`, `BTN_Reload` and `BTN_Sprint` at `BRANCH_ToRanged` (also 0.5714); `BTN_Nav_Evade` at `BRANCH_Evade`; `BRANCH_Evade_End` at the end. The heavy pistol evade has the same exits. There is **no** aim or grenade transition out of an evade state (aim is the variable `BOOL_IsAiming`; the grenade, `BTN_UseEquipment`, is taken by the parent "FROM Ranged (Interruptible)" machine).
- New: the stick, Fire, Reload and Sprint mapping is data (spec cell edited); Aim, Grenade and the pistol branch stay an interpretation. Confidence medium (stick, Fire, Reload, Sprint) and low (rest).

**Evade: Switch (`BRANCH_To_Weapon_Switch`)** (`R-4B05E3CC`): data: the branch is consumed by the parent machine's `if BOOL_<weapon> == 0 branch BRANCH_To_Weapon_Switch --> TO Ranged Weapon Switch` on every weapon state (the evade states have no switch exit of their own). Low to medium.

**Evade: Next evade (`BRANCH_Evade`)** (`R-F6CCFF23`): data: `Bolter Evade --on BTN_Nav_Evade branch BRANCH_Evade--> Bolter Evade` (a self-transition with a 0.1 s blend). Medium.

**Weapon switch time** (`R-8813BF01`, spec "Classes and default loadouts")
- Old: 0.6 s default "placeholder (the original's switch animation timing not read)", 0.1 s with the owner's quickdraw (since 2026-11-06), 0.48 s before.
- Data: the rifle-class behaviours' "Weapon Switch" states (bolter, plasma, melta, storm bolter, stalker, grenade launcher, plasma cannon) play `SM_Swap_Rifle_to_Rifle_01`, **0.467 s**, with the `Weapons - Melee Weapon` modifier over 0 to 0.5 and `Weapons - Ranged Weapon` over 0.5 to 1 (the weapon changes at 0.233 s), a `BranchIn` tag at 0.45 and `BRANCH_Run` at 1.0. The bolt pistol's state plays `SM_Swap_Rifle_to_Melee_01`, **0.667 s**. The heavy bolter and lascannon have no swap state. The swap state's exits are `BRANCH_Run` (the end), `BTN_Nav_Evade`, `BTN_Sprint`, `BTN_Reload`: **no `BTN_Ranged`**, so a shot waits for the clip's end (then a 0.167 s blend to navigation).
- New value: 0.467 s (rifle class), 0.667 s (pistol), shot not before the end. The earlier 0.48 s estimate (0.6 x 0.8) was within 3 % of the data. Confidence high for the clip times. The row stays tagged because the simulation does not use them: 0.6 s is the config default and 0.1 s is the owner's deliberate quickdraw (not changed; see 3.3).

**Ready hold after a shot** (`R-C64B6FB4`): data: the graph has the events `EVT_Ranged_Nav_Timeout` (ranged states return to the melee weapon) and `EVT_Melee_Nav_Timeout` (the pistol's melee state returns to ranged navigation) raised by code; the attribute packages hold no numeric field with `ready`, `timeout`, `hold`, `lower` or `holster` in its name (`wargear`, `ebps`, `global`; only the bool `wait_for_weapon_ready`) and the multiplayer script constants hold none. So the length (the 10 s estimate) is a code constant: **not settleable from the files**, but now known to exist as a timeout. Low.

**Vent duration** (`R-99C06BDF`): data: `overheat_delay` and `overheat_recover_time` are 0 on every plasma weapon; the vent pose graph `SM_Plasma_Vent` follows `BOOL_Wargear_Overheated` with no timer; the multiplayer plasma gun's passive `cool_down_rate` is 4 (delay 0.35 after a gain, 1 after an overheat), the campaign gun's 5 / 2 and the campaign pistol's 0.75, co-op cannons 1000; `overheat_heat_venting_multiplier` 1 (multiplayer). So no field holds a vent duration: only the vent's 45 heat/s and the passive rate decide it (about 2.1 s, the owner's 6 s recollection stays unreconciled). Low.

**Charge: Rush motion** (`R-A7D09F49`)
- Old: the 5 m variant always; "the 2.5 m variant (blended by target distance in the original) is not used".
- Data: `MP_SM_Bolter_NuSprint` "Sprint Attack" is a "Smash Blendspace" on `FLT_MeleeTargetDistance` with `SM_BUL_Hit_M_2.5m` at weight 2.5 and `SM_BUL_Hit_M_5m` at 5; the variable's initial value is **1** (`smtool graph-dump --melee`), below the first weight, so with no target (or a target nearer than 2.5 m) the original plays the **2.5 m clip**. Extracted motion: 2.5 m clip 3.83 m at the hit time 0.233 s, 9.36 m at 0.667 s, 11.86 m at 1.2 s, 12.38 m in all; 5 m clip 6.32, 11.85, 14.34 and 14.87 m. Between 2.5 and 5 m of target distance they blend.
- New: the original's charge with no target is **about 2.5 m shorter than ours** (12.38 against 14.87 m), and 2.5 m shorter at every moment (the clips differ by about 2.5 m from 0.2 s on). Medium (the values), low (that code feeds the variable the distance to the lock). Not applied (3.1).

**Charge: Contact** (`R-7038C9DD`): data from the rush sequencer's tags: `ACTN_Hit_Primary_Target` and `ACTN_Hit_Targets_Multiplayer` at 0.14 (0.233 s), `ACTN_Hit_Secondary_Targets` 0.14 to 0.24 (0.233 to 0.4 s), `Interaction` 0.12 to 0.14 (0.2 to 0.233 s), `CollisionMode` 1 over the whole clip, `AttackDistance` 0 to 0.1406 with d=5, and the transition `Sprint Attack --if BOOL_Bullrush_Hit--> Knocked Back`. The window "until `BRANCH_Attack2Idle` (0.667 s)", the 0.45 + 0.3 m reach and the 45 degree cone stay ours; the secondary-target window (0.233 to 0.4 s) is a candidate for the 30-damage cone's timing. Low.

**Charge: Wall** (`R-255D55A9`): data: `ability_bullrush_01` has no `special_nav_ability` of its own (only `min_time_between_activations` 1, `max_num_primary_hits` -1, `world_hit_*`); the rush is the sprint graph's "Sprint Attack" state, and `sm_mp_abil_sprint` holds `max_horz_angle_for_wall_hit` 25 and `max_turn_rate` 80; the clip's `WallCollisionTest` window is 0.2 to 1. So the sprint ability's 25 is the only wall angle in the data. Low to medium.

**Charge: Homing** (`R-956FF538`) and **Charge lock and steering** (`R-4974D4FD`): data: the rush sequencer's `AttackAlignment` window is 0 to 0.12 (0.2 s) only; `sprint_or_rush_tuning` (7 m, 45 degrees) holds no rate; the sprint ability's `max_turn_rate` is 80. The 180 degrees per second stays an estimate; that the original steers toward the target only in the first 0.2 s is a reading (3.2). Low.

### 2.3 Facts without a row change

- **Compiled Lua constants**: the install's Lua variant (12-byte header with float32 numbers, a type table, then functions) parses exactly; all 50 `util/mp` scripts read. The rules hold no respawn time, spawn protection, friendly fire or spawn delay for versus (`Network_SetRespawnDelay` takes `team.respawnTime`, which no file sets; 999999 is the co-op fail rule), so the 5 s respawn and Rapid Deployment's 3 s remain code-side.
- `global` `directional_targeting_ext`: `half_cone_angle_for_ranged` 25, `max_ranged_target_distance` 30, `max_knocked_out_target_distance` 5 (not simulated); `stuned_enemy_targeting_bonus` 1 (not applied; a multiplier of 1 would do nothing).

## 3. Gameplay-visible differences found (none applied; for the lead to put to the owner)

1. **Charge distance with no target** (Rush motion): original about 12.4 m, ours 14.9 m; a charge at a target 2.5 to 5 m away blends in between. A fix needs the rush to pick the variant from the lock's distance (`RushTuning`, `Rush.Move`, the client's rush pose) and a protocol or config-hash decision, so it is not a one-line data value. The owner's open item "the charge's reach" (CLAUDE.md, Next action 3) gets a hard number from this.
2. **Charge steering window**: the data's alignment window is the first 0.2 s; ours steers to the target at 180 degrees a second while it connects (up to 0.667 s). Cutting it to 0.2 s would shorten homing (the owner's reference video agrees with a turn late in the charge: "the video's last 0.3 s turns about 30 to 40 degrees", so this reading may be wrong).
3. **Weapon switch**: data 0.467 s (rifle class) and 0.667 s (pistol), shot at the end; the owner's quickdraw is 0.1 s (decided 2026-11-06). Reported only so the owner knows the quickdraw is 0.37 to 0.57 s faster than the original.
4. **Evade ends on Aim and Grenade at `BRANCH_Run`**: the graph's evade exits are the stick, Fire, Reload and Sprint only (2.2); our evade also ends on an Aim press or a grenade at 0.667 s (spec "Control returns"). The Tactical roll's Fire exit is already off by the owner's rule.
5. **Back-diagonal evade**: no clip or wedge in the data for S+A, S+D (owner's recollection keeps the 8 directions; the forward diagonals fit the forward roll's alignment modifier).

## 4. Examined and not settled

| Row (spec section) | What was looked at | Why it stays |
|---|---|---|
| Hit reactions: Clips, sectors, kind to clip set (`MP_SM_Reactions`) | the choosers now print: grenade B -90..90 (priority 4), L -135..-45, R 45..135, F default; stagger B -90..90, F default; the light interrupt's FRONT and BACK both -90..90 | the wedge variable's meaning and the priority order are not decoded; the ports' conditions still print "(no trigger)" |
| Hit reactions: Who reacts, During a reaction, Ending, Knockback | transitions, `reaction_by_intensity` | values are data, the rules are code; no new field |
| Evade: Melee (`BRANCH_Melee_Combo`) | tags 0.8 s and 0.6333 s | the consumers are in the melee graphs; not traced |
| Evade: Start conditions | navigation enters an evade `if BOOL_IsSupported`; Fire, Reload and the switch state enter it with no ground condition | "no melee attack in progress" and the cancel rules are code |
| Evade: Facing | `Align To Nav Direction` field @68 = 180 (Facing03: 60) | a rate (degrees a second) or a limit; not established |
| Plasma heat unit (`WeaponSpec.Heat`, low) | MP gun 4, campaign gun 5, campaign pistol 0.75, co-op 1000 | the unit "per second" is plausible (co-op 1000 = instant) but no field proves it |
| Respawn time 5 s, Rapid Deployment 3 s | Lua constants, BAF (`mp_perk_respawn_timer` holds upgrade strings only), locale 111525 ("Reduced time before returning to battle") | code-side; no number anywhere |
| Charge side steer 25 degrees, the clash lock distance, the 1 s mashed-F rules | the rush ability and graph | no angle or distance field; the 25 in the data is the wall angle |
| Weapon-switch HUD and the third-slot order (Weapon Versatility) | n/a | owner rules, no data |

## 5. Checks run

Author's own checks (not independent QA): `dotnet build SmReimpl.slnx` and `dotnet build client/SmClient.csproj` 0 errors; `smtool refine-register --check` ok (3329 rows, 2441 tagged); filtered `dotnet test` (`PublicText`, `RefineRegister`, `Evade`, `Spec`, capped `-m:4`): SmFormats.Tests 160 passed (includes `PublicTextGuardTests` and `RefineRegisterTests`), SmNet.Tests 143 passed (includes `EvadeTests`), 0 failed. The full suite was not run.
