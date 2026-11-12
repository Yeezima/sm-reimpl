# Transition clips of the multiplayer content: the inventory (2026-11-12)

The owner (2026-11-12): "start the work to add the transition animations that the original game had but we haven't deployed to sm-reimpl yet" (standing rule: wire them "where the original wires them").
The first pass (polish item 10, `docs/milestones.md` "Install transitions the client did not play") started from a short list; this is the full inventory it lacked. Names and numbers only; no original bytes.

**How it was made.** `smtool graph-dump --list`, `--animations` (the marine rig's 576 clips with lengths) and `smtool graph-dump '*'` (every state, clip and edge of the 46 behaviour graphs), then, per clip, the
graph state that plays it, its edge condition, and whether the client plays it. **The client plays many clips through names the graphs supply at run time** (`TurnAnimations`, `FallAnimations`, `SprintTransitions`,
`ReloadAnimations`, `EvadeTuning` and the like read state names and take the clips from them), so a plain search of the client's source for a clip name under-counts: "client" below was decided from the reading
code (the graph state it reads), checked with the name search and, for the new rows, a live log. The ork rig (Exterminatus and Musou enemies) has its own graphs and is out of this table's scope except where noted.
The rig has no crouch, cover, holster or aim-raise/aim-lower clip at all (name search: nothing; `SM_NAV_*_AimUp/Down` are aim-pitch poses of the blend grids, not transitions), so those categories are empty by data.

Status: **wired** (the client plays it where the graph does), **wired now** (this pass), **gap, client only** (the sim already has the trigger; a candidate batch), **blocked** (needs a simulation or protocol
change, or an owner decision), **out of scope** (singleplayer, Exterminatus or enemy-only).

## Locomotion: starts, stops, turns

| Clip(s) | Graph / state | Condition | Client | Status |
|---|---|---|---|---|
| `SM_TRN_BO_IdleAim_to_RunF` (0.7 s) | `SM_Bolter_NAV` "Idle Relaxed/Ready To Run" | standing, requested speed 3 or more, forward | `NetworkView.RunStart` (M40-T5) | wired |
| `SM_NAV_AimTurn_Left/_Right` (0.833 s) | `SM_Bolter_NAV`, `SM_Pistol_NAV` "Idle Turn Left/Right" | aim more than 90 (75 pistol) degrees off the standing body | `NetworkView.TurnInPlace` | wired |
| `SM_NAV_TB_AimTurn_Left/_Right` (0.833 s) | `SM_TB_NAV` "Idle Turn Left/Right" | heavy bolter, aim more than 75 degrees off | `TurnAnimations` (heavy posture), same machinery | wired |
| `SM_RNG_TB_Brace_TurnLeft/_Right` (1.467 s, pelvis and legs) | `MP_SM_HB_Brace` "Turn Left/Right" (Brace Idle Behavior) | braced and `FLT_Aim_Horizontal_Deg_DAMPED` in [-180, -60] / [77, 180]; repeats at `BRANCH_TurnComplete` 0.977 while the aim stays out | no: the brace pose's horizontal aim blends and turn clips are "not used" (`BraceTuning` doc) | **gap, client only** (batch 3) |
| `SM_NAV_Bullrush_Start_F_01`, `SM_NAV_Bullrush2Idle_F_01` | sprint graphs, start and "Sprint to Idle" | sprint begin / stop | `SprintEdges` | wired |
| `SM_NAV_Bullrush2Idle_F_HalfSpeed_01` | every sprint graph "Transition To Idle on Interrupt" | stop inside the start's window | `NetworkView.SprintEdges` (item 10) | wired |
| `SM_NAV_Bullrush2Idle_F_Collision_01` | sprint graph "Knocked Back"; chainsword/power-weapon dash attack "Knocked Back" | the charge's wall stop | `NetworkView.DashAttack` | wired |
| `SM_TRN_BO/CS/HB/TH_180Turn_to_Sprint` (1.233 s) | sprint graphs' "180 Idle to Sprint" (the cannons' has none) | body more than 100 degrees off the aim, then a sprint | `NetworkView.SprintEdges` (item 10) | wired |
| `SM_NAV_Sprint_to_Idle_F` | `SM_TH_NuSprint` "Sprint to Idle" | the hammer's sprint stop | item 10 | wired |
| `SM_1HR_NAV_Sprint_to_Idle`, `SM_NAV_LC_Sprint_to_Idle`, `SM_NAV_TB_Sprint_to_Idle` | the pistol, cannon and heavy sprint graphs | sprint stop | `SprintEdges` | wired |
| the sprint loop's wall edge into the half-speed stop | sprint graphs, `BOOL_IsWallInMotionDir` | a sprinter near a wall | no | **blocked** (the sim does not brake a sprinter before a wall) |
| `SM_NAV_IdleL_S_Relaxed_SI` | `SM_Bolter_NAV` "Idle Relaxed Point - Ally > Relaxed Idle 02" | the point-at-ally gesture state | no | out of scope (co-op / singleplayer gesture) |

## Jump, jump pack, falls

| Clip(s) | Graph / state | Condition | Client | Status |
|---|---|---|---|---|
| `SM_JMP_Landing_Low`, `_LowRun`, `_MedRun`, `SM_JMP_GP_Impact_01`, `SM_JMP_GP_ReturnToIdle` | `MP_SM_NAV_JP_Sim` "Idle/Run Landing", "Non-Hammer GP" | a jump-pack landing, run or idle; the ground pound | `NetworkView.Jump` | wired |
| `SM_JMP_TH_Landing_Low` (1.2 s), `SM_JMP_TH_Landing_LowRun` (1.133 s) | `MP_SM_NAV_JP_Sim` "Hammer Idle Land", "Hammer Run Land" (`BOOL_Thunderhammer`) | a hammer marine's jump-pack landing | the plain pair (0.9 s run landing) | **wired now** |
| `SM_JMP_TH_Landing_MedRun`, `SM_JMP_TH_GP_ReturnToIdle`, `SM_JMP_TH_GP_Impact_01`, `SM_JMP_TH_GP_Loop_01` | `MP_SM_NAV_JP_Sim` "Hammer GP" | the hammer's ground pound | Classic: the hammer's jump attack is the air slash (`HammerSlash`), not a pound; Musou's spear dive reads the dive loop and the impact | n/a in Classic (the slash replaces the pound), the spear's are wired |
| `SM_JMP_Dash_B/F/L/R`, `SM_JMP_Dash_Landing_Shallow_*` | every jump-pack melee graph, "Jumppack Evade", "Jump Dash Land" | the air dash | `EvadeTuning` | wired |
| `SM_JMP_TH_Dash_B/F/L/R` (and `SM_JMP_TH_Dash_Landing_B/F/L/R`, 1.133 s) | `MP_SM_MEL_ThunderHammer` "Thunderhammer Jumppack Evade", "Jump TH Dash Land" | a hammer marine's air dash and its landing | the plain dash clips for the hammer | **gap, client only** (batch 2: `EvadeTuning` reads one graph per family) |
| `SM_JMP_DashAtk_F`, `SM_MEL_PA_XATK_Combo3FIN`, `SM_JMP_TH_DashAtk_F` | the jump dash attack (lunge) | the lunge | `DashAttack` (the hammer's own clip is a known gap there) | wired (the hammer's lunge clip: gap, noted in `NetworkView.DashAttack`) |
| `SM_ACT_B_button_drop_fall_01`, `_land_01` | `SM_Bolter_NAV` "Fall Behaviors" | a fall | `NetworkView.Fall` | wired |
| `SM_NAV_TB_Dropdown`, `SM_NAV_TB_Landing` | `SM_TB_NAV` "Heavy Drop Down" | the heavy bolter's fall from 1 m | `NetworkView.Fall` | wired |
| `SM_2HM_NAV_TH_Dropdown`, `SM_2HM_NAV_TH_Landing` | `SM_TH_NAV` "Drop Down"; `SM_Pistol_NAV` "Drop Down with Thunderhammer" | a hammer marine falls 1 m or more (the landing from 0.5 m, whatever the speed) | the rifle's fall | **wired now** (`FallFamily.Hammer`) |
| `SM_NAV_CS_Dropdown`, `SM_NAV_CS_Landing` | `SM_Pistol_NAV` "Drop Down with Chainsword" (`BOOL_Chainsword`) | a chainsword marine falls 1 m or more | the rifle's fall | **wired now** (`FallFamily.Chainsword`) |
| `SM_JMP_Zero*`, `SM_JMP_Curve_F_Lo_Far_01`, `SM_JMP_Blend_90_Pos_Fast` | flight poses | in the air | `NetworkView.Jump` | wired (poses, not transitions) |

## Weapon changes

| Clip(s) | Graph / state | Condition | Client | Status |
|---|---|---|---|---|
| `SM_Swap_Rifle_to_Rifle_01`, `SM_Swap_Rifle_to_Melee_01` | `SM_Base_MP` "Bolter Weapon Switch", "BoltPistol Weapon Switch" | weapon switch | `NetworkView.UpperBody` | wired |
| `SM_1HR_NAV_Pistol2TH` | `MP_SM_MEL_ThunderHammer` "ThunderHammer From Pistol" | the pistol comes down, the hammer stance begins | `NetworkView.MeleeFlow` (HammerDrawPose) | wired |
| `SM_RNG_PC_Transition_01` (1.233 s), `SM_RNG_PR_Transition_01` (0.933 s), `SM_1HR_RNG_PP_Transition_01` (0.833 s) | plasma cannon "Change Fire Mode"; lascannon "Switch Mode", grenade launcher "Detonate"; plasma pistol "Change Fire Mode" | the fire-mode button | no | **blocked**: our simulation has no fire-mode switch input for these weapons (no protocol button); a simulation and protocol change for the owner |
| `SM_RNG_TB_Idle_to_BraceIdle` (1.5 s), `SM_RNG_TB_BraceIdle_to_idle` (1.367 s) | `MP_SM_HB_Brace` | the brace in and out | `NetworkView.Brace` | wired |
| `SM_RNG_Idle_Vent_Heat_*`, `SM_RNG_OverHeat_Vent_02` | `SM_Plasma_Vent` | venting | `NetworkView.Brace` (vent) | wired (states, not transitions) |
| `SM_RNG_Idle_Charge_Up_S_01`, `_Cycle_S_01`, `SM_1HR_RNG_Charge_01`, `_ChargeShot_01` | charge weapons | the charge | `NetworkView.UpperBody` | wired |

## Melee

| Clip(s) | Graph / state | Condition | Client | Status |
|---|---|---|---|---|
| each weapon's entry, chain and exit blends | the melee graphs | strikes | `MeleeFlowBlends` (anim/melee-flow) | wired |
| `SM_MEL_TH_XATK180_to_XATK2` (2.333 s) | `MP_SM_MEL_ThunderHammer` "TH Default 2 (X, X) From 180 MP" | the hammer's second X strike after the 180 sweep | the plain second strike | **gap, client only** if the previous step is on the wire (batch 4; to check) |
| `SM_MEL_CS_XATK_Draw2_*` | the claw / sword Y backhand (`..._3.5m`) | Musou's claw | `ClawSpearClips` | wired (Musou) |
| `SM_JMP_TH_DashAtk_F` | the hammer's jump dash attack state | the lunge | the air slash clip is used instead | known gap (`NetworkView.DashAttack`), owner's hammer-swing pass |

## Reactions and recoveries

| Clip(s) | Graph / state | Condition | Client | Status |
|---|---|---|---|---|
| `SM_REC_Flinch_*` (additive, 0.2 to 0.367 s) | `SM_Base_MP`, additive flinches | a hit | `PoseSlots.Flinch` | wired |
| `SM_REC_Light_Itrpt_FHL_01`, `_BHR_01`; the stun `SM_MP_REC_Grenade_*`; `SM_REC_Stagger_F/B_01` | `MP_SM_Reactions` "CSM Reactions" | interrupt, stun, stagger | `NetworkView.Reactions` | wired |
| `SM_REC_Light_Itrpt_FHR_01`, `_BHL_01` | `MP_SM_Reactions` "BloodLetter MA2" | the enemy Bloodletter's blows | no | out of scope (enemy-only state; Exterminatus AI) |
| `SM_REC_Light_Itrpt_FLL_01` | "Nob Reactions" | the Nob's ranged hits | no | out of scope (Exterminatus) |
| `SM_REC_Hvy_Itrpt_FHL_01` | "Counters" (`BRANCH_Counter`) | the counter window | no | blocked: the entry edge has no trigger in the data, the attacker kind is not on the wire |
| `SM_REC_HVYUNIT_01/02/03` | "Nob Reactions" | Nob blows | no | out of scope (Exterminatus) |
| `SM_REC_Sprint_Stagger` | `SM_Base_MP` "Sprinting Death" | death while sprinting | `MarineDeathKind.Sprint` | wired |
| `SM_REC_MP_*Death*`, `SM_REC_ChaosDeath_01`, `SM_MP_REC_Grenade_*` death | `SM_Base_MP` DEATH | death | `UpperBodyAnimations.DeathRules` | wired |
| `SM_REC_Idle2Incap_D_01`, `_Incap_D_01`, `_Incap2Death_D_01`, `SM_REC_Stun2Idle_D_01` | `SM_REC_Incapacitated` | the co-op downed state and its revive | no | out of scope (Exterminatus: no downed state) |
| roll recoveries `SM_EVD_*` | `SM_Base_MP`, weapon graphs "Evade" | the ground evade | `NetworkView.Evade`, `HeavyHold` | wired |

## Out of scope by content

Singleplayer-only and Exterminatus-only clips: the ork rig's graphs, `SM_REC_HVYUNIT_*`, the Incapacitated machine, the Bloodletter and Nob reaction states, `SM_NAV_IdleL_S_Relaxed_SI`, the interaction clip (`SM_Interact`,
a use-object action the multiplayer content does not call).

## Ranking of the remaining gaps (visible first)

1. **Brace turn (heavy bolter)** (client only): a braced Devastator turning its aim sees the body slide round rigidly; the original plays a 1.467 s turn on the legs. Needs the braced marine inside the planted-body
   machinery (`BodyYawFor`) with the brace graph's turn thresholds (-60 / +77 degrees). Batch 3.
2. **The hammer's air dash and its landing** (client only): Assault with the thunder hammer dashes with the plain dash clips. Batch 2.
3. **The hammer's second strike after the 180 sweep** (client only, if the step's history is available). Batch 4.
4. **Blocked, for the owner**: the plasma, lascannon and plasma-pistol fire-mode change clips (need a fire-mode input and rules in the simulation and the protocol); the sprinter's wall stop (needs the simulation to brake).
5. Not worth wiring: the other hit directions (enemy states), the incapacitated clips (no downed state).

Done in this pass (batch 1): the hammer's and the chainsword's drop and landing, the hammer's jump-pack landings. See `docs/milestones.md` "Install transitions, second batch".
