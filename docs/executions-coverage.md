# Executions: the original's sync kills and their coverage (inventory, 2026-11-25)

Brief: the owner-approved executions brief (coordination `specifications/2026-11-24-original-sm1-executions.md`, approval `decisions/2026-11-25-original-sm1-executions.md`).
This document inventories every execution ("sync kill") the install carries and maps each one to the SM-Reimpl modes the brief names: Warfront
(human players against REGULAR Orks) and Classic PvP Extended (the lethal-melee finisher). It contains names, timings and counts only; no original bytes.
Nothing here changes gameplay. The implementation still needs its own design.

**How it was made.** `smtool sync-kills` (new, read-only; `src/SmTool/SyncKillsCommand.cs`, core in `src/SmTool/Dump/SyncKillInventory.cs`, tests
`tests/SmFormats.Tests/SyncKillInventoryTests.cs`) reads each rig's behaviour graphs from the install. For each rig it lists the sync states (state path,
clips with lengths and distance weights, timing tags in seconds, interaction payloads, alignment tags, transitions in), then the clips' root motion and
`RIG_Interaction` bone, and finally a pair table that matches each attacker clip to its victim clip by name and aligns the two. I also used
`smtool graph-dump --bones/--animations` (skeleton comparisons) and `smtool baf-dump` on `ebps`, `wargear` and `global` (`--grep "sync|execut"`).
Default run: `smtool.cmd sync-kills` (about 3 s; 8,000 lines). Labels follow CLAUDE.md: **supported by evidence** (read from the data),
**interpretation**, **ours** (a proposal).

## 1. Findings in short

1. **The original multiplayer had no executions** (supported by evidence). The multiplayer melee wargear (`sm_mp_melee_*`, `csm_mp_melee_*`) defines no
   `ability_melee_sync`. Only the campaign weapons define it: `chainsword_new`, `poweraxe`, `thunderhammer_new`, `combat_knife`, `fists` and
   `demon_prince_fists`. The multiplayer rig `havok_sm_base_mp` (`ebp_sets__mp_versus_set`) still declares the campaign's variables and events
   (`HNDL_Ork_Sync_Kill`, `HNDL_Nob_Sync_Kill`, `HNDL_CSM_Sync_Kill` and the like, `BTN_Melee_Sync`), and `BRANCH_To_Sync` windows remain on five
   power-axe and six thunder-hammer sequencers. No transition reads any of them, and the rig has no sync state. Its six `SYN`-named sequencers are
   ordinary strikes: the power axe's combo end 3 "Shoulder Attack" (`SM_SYN_PA_HVY_Shldr_1m/3.5m/7m`, 1.667 s) and "PA Default 4"
   (`SM_SYN_PA_HVY_UprcutSPN_*`, 1.6 s). Their interaction tags send a hit reaction (`PORT_PA_2XY` at 0.37 s, for example), not a paired kill.
2. **Exterminatus had none either** (supported by evidence). The co-op player rig (`ebp_sets__mp_coop_default_player`) has the same 46 graphs and
   the same six strikes. The co-op marine weapons (`sm_coop_chainsword`, `sm_coop_poweraxe`, `sm_coop_thunderhammer`) define no sync ability. None of
   the co-op enemy rigs has a sync clip: `havok_boy_coop`, `havok_nob_coop`, `havok_ard_boy_coop`, `havok_gretchin_coop`,
   `havok_chaosspacemarine_coop`, `havok_bloodletter_coop` and `havok_psyker`. The one paired state left in the MP rig is the marine as the
   *victim* of a Bloodletter's rend (`MP_SM_Reactions` "BloodLetter Sync"; see 2.6). The co-op Bloodletter rig has no matching clip, so that state
   cannot be reached with the shipped rigs (interpretation).
3. **Every execution is a campaign sync kill** and lives in the campaign player rig `havok_spacemarine` (114 sync sequencers in 58 graphs). That rig is
   in `ebp_sets__player_set`, the shared set (also in `campaign_001`, `ui_fe_player_set`, `s025-001` and `s028-001`). The weapon graphs that carry
   sync kills are `SM_MEL_ChainSword`, `SM_MEL_PowerAxe`, `SM_MEL_ThunderHammer` and `SM_MEL_CombatKnife`. Each victim half is a separate clip on the
   victim's own rig.
4. **The attacker rig matches the multiplayer rig** (supported by evidence). `havok_spacemarine`, `havok_sm_base_mp`, `havok_chaosspacemarine` and
   `havok_chaosspacemarine_coop` have the same 50-bone skeleton: same names, order and parents, including `RIG_Interaction_Root` and
   `RIG_Interaction`. Every attacker clip therefore plays on our players as they are, and every Chaos Marine victim clip plays on a PvP player.
5. **The Ork rigs match each other.** `havok_boy` (campaign, `ebp_sets__ork_core_units`) has the same 51-bone skeleton as `havok_boy_coop` (our
   Warfront and Musou Ork), `havok_ard_boy`, `havok_ard_boy_coop`, `havok_elite_boy` and `havok_stormboy`. `havok_gretchin` has the same 38-bone
   skeleton as `havok_gretchin_coop`. The Nob rigs differ: they add three prop bones and reorder the face bones. **The Ork victim clips are only in the
   campaign-set rigs** (`BOY_SYN_*` in `havok_boy` of `ork_core_units`, `GRE_SYN_*` in `havok_gretchin`). The co-op rigs we load have none, so the
   client must take those clips from the `ork_core_units` packfile and play them on the co-op skeleton (same bones, no retargeting). The marine and
   Ork skeletons differ (50 and 51 bones, different names and order), so no clip crosses between the two species. None needs to: each side plays its
   own rig's half.
6. **How the original triggered a sync kill** (supported by evidence for the graph; interpretation for the engine's part). The attacker's weapon graph
   enters "<Weapon> Sync Kill" on `BTN_Melee_Sync`, from navigation, sprint, the draw, or an attack inside its `BRANCH_To_Sync` window. It plays
   "SYNC Kill Attempt" (`SM_MEL_SYNC_MISS`, 1.333 s), whose interaction tags send `PORT_CS_Sync` (or `PORT_AX_Sync`, `PORT_TH_Sync`, `PORT_CK_Sync`)
   to the target at 0.0 and 0.326 s. The victim enters "SYNC KILL" and goes on to its weapon-family state "CS/AX/TH/CK Sync Kill" **only if
   `BOOL_Stunned`**. Otherwise it plays "Sync Kill Fail", a counter swing that sends `PORT_Ork_Sync_Fail` at 0.867 s. The attacker then passes
   "CONDUIT - <target type> Sync" (Ork, Nob, Ard Boy, Gretchin, Chaos Marine, Bloodletter, WeirdBoy, Renegade, Tainted Psyker) into one move. **The
   conduit exits have no event and a disabled condition (flag 0x100), only priorities.** The executable picks the target type and the move. The data
   holds no selection rule; the priorities are listed in section 2.
7. **Alignment is authored in the clips** (supported by evidence for the numbers; interpretation for the rule). Each attacker move blends three
   distance variants (1 m, 3.5 m, 7 m; the arm break uses 2, 4 and 7 m) on `FLT_MeleeTargetDistance`. The victim plays one clip, authored at
   3.5 m. In each clip the `RIG_Interaction` bone holds one fixed world point. When both clips' points meet at the start, with the bones' headings
   equal, the victim stands 3.37 to 4.00 m ahead of the 3.5 m attacker variant, facing him (yaw 180 degrees), and 0.0 to 0.1 m to the side. The
   original therefore **covers a varying distance with the attacker's blend and does not move the victim**. Distances outside the authored band
   (1 to 7 m; 2 to 7 m for the arm break; 3.5 m only for the axe hack and the psyker stomp) have no variant.
8. **The moves travel far.** On the 3.5 m variant the attacker's root moves up to 6.9 m forward (the slam; 10.4 m on its 7 m variant), and victims are
   carried up to 4.2 m (the axe decap) or knocked 4.7 m sideways (the hammer's Chaos kill). Every pair needs its whole path free of walls and drops.
   The brief forbids pulling anyone through terrain or across gaps, so a pair whose path is blocked must not start (section 3).
9. **The original rewarded health.** Every finishing move carries `ACTN_give_health_50` at, or just after, its hit frame. The brief's Warfront reward
   (full health, full armour, a one-bar overshield) and Classic's "no reward" are ours, by the brief.
10. **Coverage.** For Warfront's regular Orks, 9 Ork Boy moves and 1 Gretchin move have an original paired victim clip (section 2.1). For Classic PvP
    Extended, 3 finishers have an original victim clip on the shared skeleton: chainsword decap, power-axe hack and thunder-hammer kill, all
    marine-on-Chaos-Marine (section 2.3). Gaps: the power sword, the heavies' kick, the Dreadnought, the lightning claw, the power spear, the bomb
    squig, and a loyalist victim (section 4).

## 2. Inventory of the install's sync kills

Times are seconds into the clip (3.5 m variant), read from the sequencer tags, which are fractions of the clip in the data.
- **Hit**: `ACTN_Hit_Primary_Target`, the contact and kill frame (interpretation: the same event marks the hit frame of ordinary strikes).
- **Heal**: `ACTN_give_health_50`.
- **Free**: the start of `BRANCH_Melee_End`; `BRANCH_Melee_Run` opens slightly earlier.
- **Align**: the `AttackAlignment` window.
- **Victim dead**: the start of the victim's `BRANCH_Boy_Dead` or `BRANCH_CSM_Dead` window.

Positions are in the attacker's starting frame (X right, Y forward, metres), from the pair table of `smtool sync-kills`.

### 2.1 Space Marine on Ork Boy and Gretchin (campaign; the Warfront candidates)

Attacker rig `havok_spacemarine` (`ebp_sets__player_set`). Victim rig `havok_boy` (`ebp_sets__ork_core_units`, also `ard_boy` for the slam) and
`havok_gretchin` (`ork_core_units`). Victim states sit under the boy's "SYNC KILL / <family> Sync Kill" in `Base_MELEE`, `Base_MELEE_Pro` and
`Base_RANGED`, so melee, melee-pro and shoota boys alike take them.

| # | Move (attacker state) | Weapon graph(s) and conduit priority | Attacker clip(s), length | Victim clip, length | Align | Hit | Heal | Free | Victim dead | Victim start / yaw | Attacker end | Victim end |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| O1 | Ork Raised Grind | ChainSword (4) | `SM_SYN_ATT_F_ORKGRIND_01_1M/3.5M/7M`, 3.5 | `BOY_SYN_ATT_F_ORKGRIND_01_3.5M`, 3.5 | 0.167-0.200 | 2.200 | 2.267 | 3.294 | 1.267 | (0.07, 3.48) / 180 | (1.18, 3.36) | (2.48, 5.96) |
| O2 | Ork Grind | ChainSword (3) | `SM_SYN_SwordGrind_1m/3.5m/7m`, 4.833 | `BOY_SYN_SwordGrind_3.5m`, 4.833 | 0.200-0.467 | 3.933 | 4.033 | 4.583 | 0.400 | (0.09, 3.65) / 180 | (0.07, 5.95) | (0.67, 6.46) |
| O3 | Ork Slam | ChainSword (2), CombatKnife (2), PowerAxe (2), ThunderHammer (1) | `SM_SYN_ATT_F_ORKSLAM_1M/3.5M/7M`, 3.667 | `BOY_SYN_ATT_F_ORKSLAM_3.5M`, 3.667 | 0.133-0.233 | 2.433 | 2.467 | 3.381 | 0.733 | (0.07, 3.50) / 180 | (0.00, 6.89) | (-0.69, 6.01) |
| O4 | Ork Arm Break | ChainSword (1), PowerAxe (3) | `SM_SYN_ARMBREAK_01_2m/4m/7m`, 3.0 | `BOY_SYN_ARMBREAK_01_4m`, 3.0 | 0.143-0.171 | 1.433 | 1.433 | 2.824 | 0.500 | (0.07, 4.00) / 180 | (-0.42, 2.36) | (0.26, 3.54) |
| O5 | Ork Decap | PowerAxe (4) | `SM_SYN_PA_Boy_Decap_1m/3.5m/7m`, 2.833 | `BOY_SYN_PA_Boy_Decap_3.5m`, 3.467 | 0.069-0.115 | 1.867 | 1.867 | 2.833 | 0.400 | (0.00, 3.50) / 180 | (-0.07, 3.86) | (-0.36, 7.69) |
| O6 | Ork Axe Hack | PowerAxe (1) | `SM_SYN_PA_Boy_AxeHack_3.5m` only, 3.667 (attacker turns 45 degrees) | `BOY_SYN_PA_Boy_AxeHack_3.5m`, 3.667 | 0.200-0.333 | 2.600, 2.724 | 2.600 | 3.667 | 0.400 | (0.00, 3.37) / 180 | (0.62, 2.05) | (-1.35, 3.72) |
| O7 | Ork Boy Sync - Head Pop | ThunderHammer (2) | `SM_SYN_TH_OrkBoy_headPOP1M`, `_headPOP`, `_headPOP7M`, 3.333 | `BOY_SYN_TH_OrkBoy_headPOP`, 3.333 | 0.200-0.333 | 2.033 | 2.033 | 3.333 | 0.633 | (0.07, 3.51) / 180 | (-0.91, 5.08) | (-0.91, 3.81) |
| O8 | Ork Stab | CombatKnife (1) | `SM_MEL_CK_XATK_HVY_1m/3.5m/7m`, 2.8 | `BOY_MEL_CK_XATK_HVY_3.5m`, 1.933 (the boy's "CK Stab"; not a `SYN` name) | 0.000-0.426 | 1.200 | 1.200 | (clip end) | (no dead branch; `ork_suicide_slam` at 1.167) | (-0.08, 3.36) / 180 | (-0.23, 1.59) | (-0.08, 6.70) |
| O9 | Gretchin Punch Sync | ChainSword, CombatKnife (3), PowerAxe, ThunderHammer | `SM_SYN_GretchinPunch_1m/3.5M/7m`, 1.567 | `GRE_SYN_GretchinPunch_3.5M`, 1.567 (`GROT_REACTIONS` "SYNC / Sync - Chainsword") | 0.167-0.300 | 0.333 | 0.367 | 0.833 | (death state) | (-0.01, 3.50) / 180 | (-0.01, 2.99) | (-0.01, 3.50) |
| - | Ork Boy Sync Fail ( React ) | every weapon (no priority) | the stagger `SM_REC_Stagger_B_01`, 1.433 | the boy's counter `BOY_1HM_MEL_Atk_Forward_01_2M/4M/6M`, 2.833 (sends `PORT_Ork_Sync_Fail` at 0.867) | - | - | - | - | - | - | - | - |

All nine Ork moves (O1 to O9) list their victim clip's own rig as `havok_boy`, `havok_ard_boy` or `havok_gretchin`. The Weirdboy kill (2.4) uses the
`havok_elite_boy` rig, which is an elite in our Warfront.

### 2.2 Space Marine on Nob and 'Ard Boy (campaign; elites, not eligible in this version)

Most Nob kills are the winning end of a struggle: a clash (0.7 or 1.433 s), then the loop `SM_SYN_Struggle_Loop` / `_NobR_Loop` (4.667 s, time
bound to `FLT_StruggleScore`, the button-mash game), then a win or loss.
- **Wins** (marine clip / Nob clip, length; hit, heal; victim start):
  - ChainSword: "Grind Face" `SM_SYN_CS_Nob_GrindFace` / `NOB_SYN_CS_Nob_GrindFace` 5.967 (hit 3.567, heal 3.600; 1.86 m). "Face Punch" (Pro) and
    "Face Punch - Ranged" `SM_SYN_CS_Nob_FacePunch_01` / `NOB_SYN_CS_Nob_FacePunch_01` 3.767 / 3.533 and `SM_SYN_CS_NobR_FacePunch_01` (no Nob half
    by that name) (hit 2.733, heal 2.767; 1.86 m).
  - PowerAxe: "Axe in Head" `SM_SYN_PA_Nob_AxeHead_01` / `NOB_SYN_PA_Nob_AxeHead_01` 4.067 (hit 3.133, heal 3.200) and the ranged Nob's
    `..._NobR_AxeHead_01`. "Jaw Break" `SM_SYN_PA_Nob_JawBreak_01` / `NOB_SYN_PA_Nob_JawBreak_01` 5.033 (hit 3.333, heal 3.400); the Nob rig also has
    `NOB_SYN_PA_NobR_JawBreak_01` 4.833.
  - ThunderHammer: "Nob Sync Success (Struggle)" `SM_SYN_Nob_Baseball_1M/_/7M` / `NOB_SYN_Nob_Baseball` 5.0 (hit 2.667, heal 2.700; 3.51 m; the
    attacker travels to (-1.72, 6.69)).
  - ChainSword "Nob_Sync_Stun": `SM_SYN_Struggle_Nob_Kill_01` 5.667, with no Nob half by that name.
- **Losses**: `SM_SYN_Struggle_SM_Break_01` / `NOB_SYN_Struggle_SM_Break_01` 5.467 (the marine is thrown 6.3 m back). The marine rig's
  `SM_Base` "Slam Death" and `SM_Struggles` "Nob_Def_Slam" reuse it as a death; the hammer's loss is `SM_SYN_TH_Struggle_Nob_Win` 5.667.
- **'Ard Boy** (`havok_ard_boy`, campaign `s008-001` and others). CS/PA/TH "Ard Boy Sync - Tombstone": `SM_SYN_Ardboy_TombStone_1m/3.5m/7m` /
  `BOY_SYN_Ardboy_TombStone_3.5m` 4.333 (hit 2.797, heal 2.758; 3.50 m; both turn -35 degrees). "Ard Boy Sync - Slam" = O3. "Ard Boy Sync Fail" =
  stagger. In SM-Reimpl the 'Ard Boy is an elite (`SquadEnemyClass.Elite`), so these are not eligible.

### 2.3 Space Marine on Chaos Space Marine (campaign; the Classic PvP Extended candidates)

Victim rig `havok_chaosspacemarine` (`ebp_sets__chaos_core_units`), on the marine skeleton. Every Chaos Marine kill is a struggle win. The Chaos
Marine's "SYNC" machine enters "Success" if `BOOL_Stunned`, else "FAIL" (a clash and a push back), split melee or ranged by `BOOL_Chainsword`. The
attacker enters "Chaos Marine Sync Success (Struggle)", in plain, RANGED, FURY and RANGED FURY variants (conduit priorities 4, 3, 2, 1). Structure:
clash `SM_SYN_Strgle_CSM_Clash_*` 0.667 s (ranged `SM_SYN_Strgle_C_R_Clash_*` 0.767; hammer `SM_2HM_SYN_Str_CSM_Clash_*` 0.767), then the loop
`SM_SYN_Strgle_CSM_loop` / `SM_SYN_Str_C_R_Loop` 4.667 s (`FLT_StruggleScore`), then a win or loss.

| # | Weapon graph | Attacker win clip, length | Victim clip (`havok_chaosspacemarine`), length | Align | Hit | Heal | Free | Victim dead | Victim start / yaw | Attacker end | Victim end |
|---|---|---|---|---|---|---|---|---|---|---|---|
| C1 | ChainSword "3b - Struggle Win ( CSM )" | `SM_SYN_CS_StrglWin_ChaosDecap`, 6.333 | `CSM_SYN_CS_StrglWin_ChaosDecap`, 6.333 | 0.168-1.090 | 4.367 | 4.400 | 6.333 | 0.833 (`BRANCH_Struggle_Death` 6.190) | (0.00, 1.32) / 180 | (0.00, 0.31) | (-0.60, 1.78) |
| C2 | PowerAxe "3b - Struggle Win ( CSM )" | `SM_SYN_PA_CSM_AxeHack_01`, 4.0 | `CSM_SYN_PA_CSM_AxeHack_01`, 4.0 | 0.106-0.689 | 2.933 | 2.967 | 4.000 | 0.833 | (0.00, 1.46) / 180 | (0.62, 1.97) | (-1.35, 3.64) |
| C3 | ThunderHammer "Struggle Win ( CSM )" | `SM_SYN_Chaos_hamburger`, 4.0 (the attacker turns 89 degrees) | `SM2_SYN_Chaos_hamburger`, 4.0 ("2 - TH CSM Loses") | 0.167-0.300 | 2.533 | 2.600 | 4.000 | 0.433 | (-0.32, 1.11) / 180 | (-2.49, 1.05) | (-4.35, 1.71) |

The marine's losses are `SM_SYN_Chaos_Success` / `SM2_SYN_Chaos_Success` 2.0: the Chaos Marine hits him at 0.833 and throws him 5.3 m back; it is
not lethal. The fails are `SM_SYN_Chaos_Pushback` / `CSM_SYN_Chaos_Pushback` 1.667 and the stagger. These are not kills.

### 2.4 Other campaign sync kills (no SM-Reimpl target)

All are marine attacker, victim clip on its own rig.
- **Bloodletter** (`havok_bloodletter`, 61 bones). CS `SM_SYN_BloodLtr_Cutthroat_*` / `BL_SYN_BloodLtr_Cutthroat_3.5m` 3.933 (hit 3.100, heal 3.083).
  PA `SM_SYN_PA_BL_TongueGrab_*` 3.6 / `BL_SYN_PA_BL_TongueGrab_3.5m` 3.367 (hit 2.074; the attacker turns 109 degrees). TH
  `SM_SYN_TH_Bloodletter_slam*` / `BL_SYN_TH_Bloodletter_slam` 3.667 (hit 2.267).
- **WeirdBoy**, CS/PA/TH. `SM_SYN_Weirdboy_1m/3.5m/7m` 3.333 / `EB_SYN_Weirdboy_3.5m` 3.333 on `havok_elite_boy` (hit 3.300, heal 1.867; 3.48 m).
  Our Musou and Warfront Weirdboy is an elite.
- **Renegade guardsman**, CS/PA/TH. `SM_SYN_IG_BackHand_Punch_1m/3.5m/7m` 2.867 / `IG_SYN_IG_BackHand_Punch_3.5m` on `havok_guardsman_a` (47 bones)
  (hit 1.233; the victim is thrown to 8.7 m).
- **Tainted Psyker**, CS/PA/TH. `SM_SYN_Psyker_Stomp_3.5m` only, 3.967 / `PSY_SYN_Psyker_Stomp_3.5m` (54 bones) (hit 2.467; victim yaw -90; the
  attacker turns -172 degrees).

### 2.5 Fury variants and the attempt

- "SYNC Kill Attempt Fury" / the FURY struggle variants are chosen by `BOOL_Upgrade_Fury_Mode` (the campaign's Fury). The attempt `SM_MEL_SYNC_MISS`
  is 1.333 s; its interaction tags send `PORT_<CS|AX|TH|CK>_Sync` at 0.0 and 0.326 s and `PORT_CS_Counter` at 0.059 s.
- Camera: `ability_melee_sync` names `camera_curve_settings\sync_kill_default` (the axe: `sync_kill_large`; the camera rig's `SyncKillSet_Default`
  and `_Large`). The brief keeps our normal orbit camera instead (ours, by the brief).

### 2.6 Enemy on marine (paired, the marine is the victim)

- **Bloodletter rend**. `MP_SM_Reactions` and campaign `SM_Reactions` "BloodLetter Sync": `SM_SYNC_Rending_4m` 3.067 and `_Reverse` (from behind),
  hammer `SM_2HM_SYNC_TH_Rending_4m` and `SM_2HM_SYNC_TH_Rend_4m_Reverse` 3.333. Partner: `BL_SYNC_Rending_4m` 4.0 and `BL_SYNC_Rending_Miss` 0.967 on
  the campaign Bloodletter only. The marine is thrown 2.7 m back and survives (no death tag).
- **Nob slam** (2.2, the struggle losses).

## 3. Mapping to SM-Reimpl

**Owner answers since this inventory (2026-11-25; decision record in the coordination repository).** They supersede the open points below. Section 6
lists the rows that implement them.
- The power sword plays the chainsword's moves in every mode with executions (an owner override of "no lookalike substitutes", for this weapon only).
- Grounded Stormboys take the Boy's moves. The 'Ard Boy is eligible on the Boy's moves (`ExecutionTargets.Warfront`), the Gretchin on the punch. The
  Bomb Squig never is (squigs are removed from Warfront for now).
- Chaos runs: Traitor Guard and Cultists take the renegade backhand. Chaos Tactical, Havoc and Champion marines take C1 to C3, and Bloodletters their
  three moves. The Chosen and the Lord never.
- Classic: a Chaos killer on a loyalist plays C1 to C3 with the races swapped (ours). Lead decision: an Ork-player victim takes the Ork Boy rows, and
  Ork players cannot execute.

The original paired clips are on the same skeletons as our rigs, so no move needs a lookalike. The open design points are which move plays (the
original executable chose), the target condition (the original required a stunned target), and the terrain check.

### 3.1 Warfront (human Space Marine players on regular Orks)

Regular means `SquadEnemyClass.Fodder`: Boy (choppa), Shoota Boy, Gretchin, Rokkit Boy, Stormboy, Bomb Squig. Elites and bosses are excluded by the
brief: 'Ard Boy, Nob, Chaos Marine, Weirdboy, Big Shoota Nob, Scrap Mek, the Warboss and the Boss Orks.

| Equipped melee (SM-Reimpl) | Our graph | Original moves on a Boy / Shoota Boy / Rokkit Boy | On a Gretchin | Notes |
|---|---|---|---|---|
| Chainsword, gold chainsword | `MP_SM_MEL_ChainSword` | O1 Raised Grind, O2 Grind, O3 Slam, O4 Arm Break | O9 | campaign `SM_MEL_ChainSword` conduit priorities 4 / 3 / 2 / 1 |
| Power axe; chain axe (Chaos twin, same graph since the original MP) | `MP_SM_MEL_PowerAxe` | O5 Decap, O4 Arm Break, O3 Slam, O6 Axe Hack | O9 | priorities 4 / 3 / 2 / 1; O6 has only the 3.5 m variant |
| Thunder hammer; daemon maul (same graph) | `MP_SM_MEL_ThunderHammer` | O7 Head Pop, O3 Slam | O9 | priorities 2 / 1 |
| Combat knife (Tactical base wargear) | `MP_SM_MEL_CombatKnife` | O3 Slam, O8 Stab | O9 | priorities 2 / 1 |
| Power sword | `MP_SM_MEL_PowerSword` | **none** | **none** | the campaign rig has no power-sword graph; `powersword_new` defines no sync ability |
| Devastator / Havoc kick (`ability_displace_kick`, `MP_SM_MEL_TB_Displace`) | displace | **none** | **none** | no paired clip; the kick only counts toward the campaign's sync-kill achievement stat |
| Librarian (Musou / Warfront class) | per the melee weapon he carries | as the row of that weapon | as that row | same rig; no Librarian-specific sync move exists |
| Dreadnought | `havok_sm_dreadnought_basic` (54 bones, 1 graph) | **none** | **none** | no sync state or clip on the Dreadnought rig |
| Lightning claw, power spear (added content, Musou only) | the claw draws on the sword's moves, the spear on the hammer's | **none** (not SM1 items) | **none** | listed only; not re-enabled |

Per Ork type:
- **Boy, Shoota Boy, Rokkit Boy.** The campaign boy's `Base_MELEE`, `Base_MELEE_Pro` and `Base_RANGED` all have the sync states. Our types run on
  the co-op boy skeleton, so the clips play there (the Rokkit Boy is an interpretation: he uses the boy rig).
- **Gretchin.** `GRE_SYN_GretchinPunch_3.5M` from `havok_gretchin`, which has the same skeleton as `havok_gretchin_coop`.
- **Stormboy.** The rig has the same skeleton as the boy but no sync clip or state of its own. Playing the boy's clips on him would be an extension
  (owner decision). The jump pack would also need hiding or a check that it fits.
- **Bomb Squig.** None (`havok_squig`, no sync).

Proposals for the remaining choices (**ours**; the data holds none of them):
- **Which move plays.** Recommended: a uniform random pick among the weapon's eligible moves that pass the terrain check. The priority order is a
  fallback if the owner prefers it deterministic.
- **Target.** Any regular Ork at any health, by the brief. The original required `BOOL_Stunned` (a change from the original: record it in
  `docs/changes-from-original.md` when built).
- **Distance band.** Execute only when the Ork is between the move's shortest and longest variant (1 to 7 m; 2 to 7 m for the arm break; about
  3.4 m for the axe hack, or drop O6 when the distance does not match). Blend the attacker's variants by the real distance and keep the victim where
  he stands, as the original did.
- **Reward moment.** The brief rewards at completion: the clip end, or `BRANCH_Melee_End`. The original healed at the heal tag.
- **Invulnerability.** Covers the clip by the brief; the cooldown starts at completion.

### 3.2 Classic PvP Extended (Space Marines vs Chaos, the lethal-melee finisher)

Victims are players on `havok_sm_base_mp`, the marine skeleton. Only the Chaos Marine victim halves C1 to C3 fit.

| Killer's melee | Original finisher | Victim clip | Notes |
|---|---|---|---|
| Chainsword, gold chainsword, Chaos chainsword | C1 decap (6.333 s, hit 4.367) | `CSM_SYN_CS_StrglWin_ChaosDecap` | starts from the struggle pose: needs a blend from the kill pose (ours) |
| Power axe, chain axe | C2 axe hack (4.0 s, hit 2.933) | `CSM_SYN_PA_CSM_AxeHack_01` | same |
| Thunder hammer, daemon maul | C3 (4.0 s, hit 2.533) | `SM2_SYN_Chaos_hamburger` | the victim is carried 4.7 m to the side: the biggest terrain risk |
| Combat knife | **none** | - | the campaign knife has no Chaos Marine move |
| Power sword | **none** | - | no campaign power-sword graph |
| Heavy kick / stomp | **none** | - | no paired clip |
| Lightning claw, power spear | not in Classic | - | Musou-only added items |

Proposals and decisions for the owner:
- **A Chaos killer and a loyalist victim (owner decision).** The original only has the loyalist killing a Chaos Marine. In the original MP both races
  share one rig (`havok_sm_base_mp`), and in our game both PvP races do too. The same clip pair can therefore play with the races swapped: the killer
  plays the `SM_` half, the victim plays the `CSM_` / `SM2_` half. This uses the exact clips and timing with the costumes reversed. Recommended:
  allow it, labelled as ours. Otherwise Chaos killers have no finisher.
- **The struggle is skipped (ours).** The brief's finisher starts after a confirmed lethal hit, with no button-mash. Recommended: play only the win
  clip, from a short blend.
- **Timing.** The victim is already dead and respawns on the normal clock (brief). The finisher runs 4.0 to 6.3 s, which may outlast a short respawn
  timer. The brief keeps the victim's respawn and camera off the animation, so the body plays the victim half to other clients while the victim's
  own view stays normal.

## 4. Gaps, risks and dependencies

1. **No loyalist-marine victim clip for a marine killer.** The original has no loyalist-dies-to-a-marine pair. See the race-swap proposal in 3.2.
2. **No pairing data field** (interpretation). Victim halves are matched by clip name (`SyncKillInventory.Stem`), and the move is chosen by the
   executable. Two names need care: `SM_SYN_CS_NobR_FacePunch_01` and `SM_SYN_Struggle_Nob_Kill_01` have no victim of the same name. The knife stab's
   victim (`BOY_MEL_CK_XATK_HVY_3.5m`) is identified by its state ("CK Sync Kill / CK Stab"), not its name.
3. **Rig mismatches.** There is none for the nine Ork moves and the Gretchin move: the co-op skeletons equal the campaign ones. The Nob rigs differ
   from the boy rig (prop bones), but Nobs are elites. No marine clip can play on an Ork or the reverse, and none needs to.
4. **Disabled or added equipment.** The lightning claw and the power spear are Musou-only added items. SM1 had no such weapons and no sync clips for
   them, so they stay unavailable and nothing re-enables them. The power sword has no original sync kill. The heavies' kick has no paired clip. The
   Dreadnought has no sync rig.
5. **Terrain and alignment.** Each pair has a footprint: the attacker's path up to 6.9 m forward (3.5 m variant; 10.4 m for the 7 m slam), and the
   victim's path up to 4.2 m forward (decap) or 4.7 m sideways (C3). Both must stay on walkable ground with no step, wall or drop. Recommended:
   test both roots' paths against the walking graph or collision at the start, and keep the normal kill (Classic) or refuse the execution (Warfront)
   when either path fails. The original's exact checks are executable code (not established).
6. **The victim clip's length can differ from the attacker's.** The decap victim runs 3.467 s against the attacker's 2.833; the knife stab 1.933
   against 2.8; the tongue grab 3.367 against 3.6. The victim's tags end the pair, through the dead branch or the clip end.
7. **The interaction bone is authored two ways** (supported by evidence). In most clips it marks the victim's start. In the slam, renegade and
   weirdboy attacker clips it marks the attacker's end spot. The alignment rule in 1.7 (meet the two bones) handles both and gives 3.37 to 4.00 m for
   every Ork pair.
8. **Selection and conditions are executable code.** These are not established: the target-type test, the move choice, the stun requirement's
   source, the minimum health (`min_sync_kill_health` = 30 on every blueprint, including the players'; meaning not established) and the abort
   (`actn_sync_kill_abort`).
9. **Campaign content dependencies** (to note the way CLAUDE.md notes Musou's):
   - Attacker clips come from `havok_spacemarine` in `ebp_sets__player_set`, a shared set.
   - The Ork Boy and Gretchin victim clips come from `havok_boy` and `havok_gretchin` in `ebp_sets__ork_core_units`. CLAUDE.md files that set under
     Exterminatus; it is also the campaign's Ork set.
   - The Chaos Marine victim clips (Classic Extended) come from `havok_chaosspacemarine` in `ebp_sets__chaos_core_units`, the same kind of set.
   - No singleplayer-only archive (`part_0*`, `ebp_sets__s0*`, `seq*`, `campaign_*`) is needed for the eligible moves. The 'Ard Boy rig
     (`ebp_sets__s008-001`) would be, but it is an elite.
   - When this is built, add a line to CLAUDE.md's dependency list: "Executions draw the campaign player rig's sync clips (`player_set`
     `havok_spacemarine`) and the victim halves from `ork_core_units` (`havok_boy`, `havok_gretchin`) and `chaos_core_units`
     (`havok_chaosspacemarine`)".
10. **Spec rows.** None was added in this pass (an inventory, no gameplay parameter). The implementation must add the clips, timings and every
    "ours" choice above to `spec/behavior.md` and `docs/changes-from-original.md`.

## 5. Counts

- **Sync sequencers.** `havok_spacemarine` 114. `havok_sm_base_mp` 6 (ordinary strikes, no executions); the co-op player rig the same. The MP
  Dreadnought 0.
- **Victim sync clips.** `havok_boy` 7, `havok_gretchin` 1, `havok_ard_boy` 2, `havok_nob` 20, `havok_elite_boy` 1, `havok_chaosspacemarine` 13,
  `havok_bloodletter` 5, `havok_tainted_psyker` 1, `havok_guardsman_a` 1. Every co-op rig 0. `havok_stormboy` 0.
- **Kill moves by target.** Ork Boy 8 (O1 to O8), Gretchin 1, Nob 8 wins, 'Ard Boy 2, Chaos Marine 3, Bloodletter 3, WeirdBoy 1, Renegade 1, Tainted
  Psyker 1.
- **Covered.** Warfront: 4 melee weapon families (chainsword, axe, hammer, knife) times regular Boys and Gretchin: 9 + 1 moves. Classic PvP Extended:
  3 weapon families, 3 finishers.
- **Not covered.** Power sword, heavy kick, Dreadnought, claw, spear (both modes); knife (Classic); Stormboy and Bomb Squig (Warfront); a loyalist
  victim (Classic, pending the owner's race-swap decision).

## 6. Catalogue rows (executions X7)

The rows are data in `src/SmSim/Extras/Executions/ExecutionRows.cs` (`ExecutionRows.All`, wire order; names, timings and offsets only).
`ExecutionCatalogueLoader.Load` (`src/SmContent/Extras/Executions/`) reads every row's clips from the install at runtime (read-only). It takes the lengths,
root paths and variant paths from the install and the event times from the row's own sequencer tags. A row whose clip is missing is left out with a
note, never replaced. On the owner's install every row loads, and the lengths and event times match the data (no note;
`ExecutionRowsTests.The_install_resolves_every_rows_clips`). All rows are "*" class: no class has a move of its own.

Row events: sync at the `AttackAlignment` start; contact and kill at `ACTN_Hit_Primary_Target` (interpretation); the heal tag and the victim's dead
branch as "other" (not played); end at `BRANCH_Melee_End`. Each variant's distance is the authored victim gap measured for that clip (the
interaction bones met), not the distance in its name. For example the hammer's Bloodletter slam variants are 2.80, 3.46 and 8.84 m.

| Row id | Kind | Family (also) | Priority | Reference clip pair | Offset (m) | Variant gaps (m) | Kill / end (s) |
|---|---|---|---|---|---|---|---|
| O1.chainsword | ork_boy | chainsword (power sword) | 4 | `SM_SYN_ATT_F_ORKGRIND_01_3.5M` / `BOY_…` | (0.07, 3.48) | 0.97, 3.48, 6.98 | 2.200 / 3.294 |
| O2.chainsword | ork_boy | chainsword | 3 | `SM_SYN_SwordGrind_3.5m` / `BOY_…` | (0.09, 3.65) | 1.15, 3.65, 7.16 | 3.933 / 4.583 |
| O3.chainsword | ork_boy | chainsword | 2 | `SM_SYN_ATT_F_ORKSLAM_3.5M` / `BOY_…` | (0.07, 3.50) | 1.00, 3.50, 7.01 | 2.433 / 3.381 |
| O4.chainsword | ork_boy | chainsword | 1 | `SM_SYN_ARMBREAK_01_4m` / `BOY_…` | (0.07, 4.00) | 2.00, 4.00, 7.00 | 1.433 / 2.824 |
| O5.power_axe | ork_boy | power axe (chain axe) | 4 | `SM_SYN_PA_Boy_Decap_3.5m` / `BOY_…` | (0.00, 3.50) | 1.70, 3.50, 7.00 | 1.867 / 2.833 |
| O4.power_axe | ork_boy | power axe | 3 | as O4 | (0.07, 4.00) | 2.00, 4.00, 7.00 | 1.433 / 2.824 |
| O3.power_axe | ork_boy | power axe | 2 | as O3 | (0.07, 3.50) | 1.00, 3.50, 7.01 | 2.433 / 3.381 |
| O6.power_axe | ork_boy | power axe | 1 | `SM_SYN_PA_Boy_AxeHack_3.5m` / `BOY_…` | (0.00, 3.37) | 3.37 only | 2.600 / 3.667 |
| O7.thunder_hammer | ork_boy | hammer (daemon maul) | 2 | `SM_SYN_TH_OrkBoy_headPOP` / `BOY_…` | (0.07, 3.51) | 1.00, 3.51, 7.00 | 2.033 / 3.333 |
| O3.thunder_hammer | ork_boy | hammer | 1 | as O3 | (0.07, 3.50) | 1.00, 3.50, 7.01 | 2.433 / 3.381 |
| O3.combat_knife | ork_boy | knife | 2 | as O3 | (0.07, 3.50) | 1.00, 3.50, 7.01 | 2.433 / 3.381 |
| O8.combat_knife | ork_boy | knife | 1 | `SM_MEL_CK_XATK_HVY_3.5m` / `BOY_MEL_CK_XATK_HVY_3.5m` | (-0.08, 3.36) | 1.87, 3.36, 6.86 | 1.200 / 2.800 |
| O9.(family), 4 rows | gretchin | each family | 0 (knife 3) | `SM_SYN_GretchinPunch_3.5M` / `GRE_…` | (-0.01, 3.50) | 1.43, 3.50, 7.00 | 0.333 / 0.833 |
| C1.chainsword | chaos_marine | chainsword (power sword) | 4 | `SM_SYN_CS_StrglWin_ChaosDecap` / `CSM_…` | (0.00, 1.32) | 1.32 only | 4.367 / 6.333 |
| C2.power_axe | chaos_marine | power axe (chain axe) | 4 | `SM_SYN_PA_CSM_AxeHack_01` / `CSM_…` | (0.00, 1.46) | 1.46 only | 2.933 / 4.000 |
| C3.thunder_hammer | chaos_marine | hammer (daemon maul) | 4 | `SM_SYN_Chaos_hamburger` / `SM2_…` | (-0.32, 1.11) | 1.15 only | 2.533 / 4.000 |
| B1.chainsword | bloodletter | chainsword | 0 | `SM_SYN_BloodLtr_Cutthroat_3.5m` / `BL_…` | (0.10, 3.50) | 1.52, 3.51, 7.00 | 3.100 / 3.933 |
| B2.power_axe | bloodletter | power axe | 0 | `SM_SYN_PA_BL_TongueGrab_3.5m` / `BL_…` | (0.00, 3.46) | 2.50, 3.46, 6.96 | 2.074 / 3.600 |
| B3.thunder_hammer | bloodletter | hammer | 0 | `SM_SYN_TH_Bloodletter_slam` / `BL_…` | (0.00, 3.46) | 2.80, 3.46, 8.84 | 2.267 / 3.667 |
| R1.(family), 3 rows | renegade | chainsword, power axe, hammer | 0 | `SM_SYN_IG_BackHand_Punch_3.5m` / `IG_…` | (0.00, 3.91) | 1.43, 3.91, 7.43 | 1.233 / 2.067 |

Counts: 25 rows. By kind: ork_boy 12, gretchin 4, chaos_marine 3, bloodletter 3, renegade 3. By family: chainsword 8, power axe 8, thunder
hammer 6, combat knife 3. Victim yaw is 180 degrees in every row. Priority 0 means the data names none; the kind then has one row per family, so
the priority does not matter.

### 6.1 Coverage matrix

| Mode | Victim (roster) | Kind | Chainsword, power sword | Power axe, chain axe | Hammer, daemon maul | Knife | Label |
|---|---|---|---|---|---|---|---|
| Warfront | Boy, Shoota Boy, Rokkit Boy | ork_boy | O1-O4 | O5, O4, O3, O6 | O7, O3 | O3, O8 | original; the Rokkit Boy on the boy rig is an interpretation |
| Warfront | 'Ard Boy, grounded Stormboy | ork_boy | as above | as above | as above | as above | ours (owner): no own clips; same skeleton |
| Warfront | Gretchin | gretchin | O9 | O9 | O9 | O9 | original |
| Warfront | Scrap Mek, Bomb Squig, elites, bosses | - | none | none | none | none | not eligible |
| Chaos run | Chaos Tactical, Havoc, Champion | chaos_marine | C1 | C2 | C3 | none | original pairs; struggle skipped (ours) |
| Chaos run | Bloodletter | bloodletter | B1 | B2 | B3 | none | original |
| Chaos run | Traitor Guard, Cultist | renegade | R1 | R1 | R1 | none | original guardsman clip on our rosters (ours: the mapping) |
| Chaos run | Chosen, Chaos Lord | - | none | none | none | none | never (owner) |
| Classic Extended | Chaos player | chaos_marine | C1 | C2 | C3 | none | original pairs |
| Classic Extended | loyalist player | chaos_marine, races swapped | C1 | C2 | C3 | none | ours (owner): `ExecutionTargets.ClassicRaceSwap` |
| Classic Extended | Ork player | ork_boy | O1-O4 | O5, O4, O3, O6 | O7, O3 | O3, O8 | ours (lead): Ork players cannot execute |

The power sword taking the chainsword's rows is an owner override, labelled ours. Nothing covers the heavies' kick, the Dreadnought, the lightning
claw, the power spear or a loyalist victim of his own race (section 4). The renegade and Bloodletter rows load from the install and pass the tests,
but the test view (6.3) cannot show them yet because it has no Gretchin, Bloodletter or guardsman model.

### 6.2 Campaign content dependencies

The rows need no singleplayer-only archive. They draw campaign clips from three shared sets:
- `ebp_sets__player_set` (`havok_spacemarine`): every attacker clip.
- `ebp_sets__ork_core_units` (`havok_boy`, `havok_gretchin`): the Ork victim halves. The co-op rigs we load have none.
- `ebp_sets__chaos_core_units` (`havok_chaosspacemarine`, `havok_bloodletter`, `havok_guardsman_a`): the Chaos victim halves.

Proposed line for CLAUDE.md's "Noted dependencies" (not applied; the lead owns that file): "Executions (Warfront and Classic PvP Extended) play the
campaign player rig's sync clips (`ebp_sets__player_set` `havok_spacemarine`) and the victim halves from `ebp_sets__ork_core_units` (`havok_boy`,
`havok_gretchin`) and `ebp_sets__chaos_core_units` (`havok_chaosspacemarine`, `havok_bloodletter`, `havok_guardsman_a`); docs/executions-coverage.md 6.2."

### 6.3 Raw-clip test view

`client.cmd --execution-test <row id>` (`client/Extras/Executions/ExecutionTestView.cs`, reached through the `ModelViewer.ExtrasExecutionTest` hook)
plays one row's reference attacker and victim clips on one clock. The attacker is the MP marine holding the row's weapon. The victim is the co-op Ork
Boy or the MP Chaos Marine, standing at the row's offset and facing him. With `--race-swap` on a C row, a Chaos killer plays it on a loyalist. There is
no alignment, blending or terrain check: it is the baseline for comparing against the original side by side. Options: `--list`,
`--times t0,t1,..` with `--out`/`--name` (stills, then quit; needs `godot.cmd --windowed`), `--view side|front|back|three`, `--look`, `--rate`,
`--quit`. Labels: original asset loaded (clips, meshes, textures) on a placeholder demonstration stage.
