# Warfront Chaos roster: what the install carries (asset inventory, 2026-11-25)

Input to the Warfront Chaos campaign (brief `2026-11-24-warfront-chaos-campaign`, rev 1, owner-approved 2026-11-25). Branch `claude/chaos-inventory-1125`, base
game main `d4db1299`. Everything here was read from the install at run time, read-only, with `smtool baf-dump`, `graph-dump --animations|--list|--bones`, `fx-dump`,
`audio-event` and the new `smtool find-entry` (below). Names, counts, sizes and timings only; nothing extracted enters the repository. Nothing was drawn or played:
"present" means the reader found and parsed it, not that it looks right in the client. Labels: **supported by evidence** (read from the data) unless marked
*interpretation*.

## Where Chaos lives

- **Blueprints** are all in `data/packages/ebps.oppc`, **wargear** in `data/packages/wargear.oppc`. Each unit has an Exterminatus (co-op) blueprint `chaos_coop_*`
  (folder `ebps\multiplayer\race_chaos`, AI wargear under `wargear\multiplayer\race_chaos\weapons_ai`) and a campaign twin `chaos_*` (`ebps\race_chaos\units\...`). Use the co-op ones.
- **Models and rigs, co-op/MP first:**
  - `data/chunks/ebp_sets__mp_coop_set_grid_test.oppc` holds the whole co-op Chaos roster in one multiplayer set: `renegade_guard` (mesh, textures, `.ppk`),
    `bloodletter`, `tainted_psyker`, the `csm_armour_basic|havoc|berserker` pieces, `chaos_bolter|lascannon|plasmacannon|powermace|lasgun`, and the rigs
    `havok_chaosspacemarine_coop`, `havok_bloodletter_coop`, `havok_tainted_psyker`, `havok_guardsman_a_coop`.
  - The Exterminatus arena `data/chunks/cp_maps__cp_arena_desert_a.oppc` is the Chaos co-op map. It has the same set except the renegade.
  - `ebp_sets__chaos_core_units` (the content map's Exterminatus core set) holds the campaign rigs (`havok_chaosspacemarine`, `havok_bloodletter`,
    `havok_guardsman_a`) and the renegade again.
  - `havok_chaosspacemarine_coop__hkxx` is also in `ebp_sets__mp_coop_set_sm|factory|waterworks|desert_c|habs_e` and `cp_arena_habs_c`. `havok_bloodletter_coop`
    is also in `ebp_sets__mp_coop_set_habs`.
- **Campaign only:** the Chaos Lord's body, the Daemon Prince, the Chaos drone and the cinematic heads (see the table).

## Per unit

| Brief's unit | Found | Blueprint (ebps) | Model, rig | Wargear (AI) | Animations, AI data | Health / armour (`health_max` / `armourshield_max`, stun threshold) | Sounds | Campaign dependency |
|---|---|---|---|---|---|---|---|---|
| Traitor Guardsmen | **yes**: the renegade guard | `chaos_coop_renegade_ig` (race chaos, character `chaos_renegade`) | `renegade_guard.dcm` (751 KB, mats `RenegadeGuard_Body/_Head`, own textures) on the **Guardsman's co-op rig** `havok_guardsman_a_coop` (graph `ig_base_coop`, 148 clips, the one Musou's Guardsmen use) | `chaos_coop_lasgun` (2 per shot, 0.1 s, AI ideal 20-35 m, max 75), `chaos_coop_frag_grenade` (AI 20-30 m), `chaos_coop_evade` | the Guardsman's full set (run, aim, fire, reload, grenade, evades, light/heavy/blown-away reactions, deaths `IG_DIE_*`), `behaviour_tree` combatant, ranged, sight 80 | 60 / none, 99 | `play_ch_renegade_footstep`, `play_blood_hit`, `play_kill_shot`; no voice set named | none |
| Cultists | **no unit by that name** (no entry contains cultist, heretic or traitor) | nearest: the same renegade guard; also `chaos_coop_renegade_ig_spawn_bl` (below) | as above | as above | as above | as above | as above | none |
| Raptors (jump pack, melee) | **player class only; no AI Raptor anywhere** | none: no `ebps` Chaos jump-pack AI; only the PvP class `csm_chaos_marine_player` (raptor armour + `csm_mp_abil_jumppack`). `csm_abil_jumppack` is the campaign's jump pack (visual `sm_jumppack_basic_001`, AI ranges 15-30 m, max 50, ground pound 50 in 5 m) | the MP marine rig `havok_sm_base_mp` (shared with players), armour `csm_armour_mp_raptor_01..03` | the PvP Raptor's (chainaxe and the rest of the class data) | the player rig's `SM_JMP_*` clips; jump FX `chaos_jumppack_thrust|_dash|_attack_thrust|_decend_thrust`, `mp_chaos_assault_land_reticule` | the PvP class's | the PvP Chaos voice and jump sounds | none |
| Chaos Tactical Marines | **yes** | `chaos_coop_marine_bolter` | `spacemarine_body_default` + `csm_armour_basic` pieces on `havok_chaosspacemarine_coop` (graph `csm_base_v2_coop`, 212 clips). **Its 50 bones are the MP marine rig's, same names, same order**, so player armour binds | `chaos_coop_bolter` (3 + 1 armour per shot, 0.12 s, AI 15-50 m), `chaos_coop_triple_grenade` (AI 20-35 m), `chaos_coop_chainaxe` (25 / 10), `chaos_coop_evade` | 73 `SM_1HR`, 47 `SM_NAV`, `SM_REC`/`CSM_REC`, `CSM_EVD`, melee `CSM_MEL_PAxe1_*`, `SM_MEL_CS_XATK_Combo1-4_*`; 4 graphs incl. `CSM_IMPORT_FLINCHES`, `CSM_RANGED_REACTIONS`; reaction mapping `csm_coop_ranged_reaction_mapping`; combatant, ranged, sight 80; drops `pickup_mp_drop_ammo_coop` / `_grenades_coop` | 500 / 400, 499 | `play_ch_marine_footstep`, `_bullrush_footstep`, `play_armor_break`, `play_armor_restore`, `play_kill_shot_armored`; audio character `chaos_space_marine` | none |
| Havocs | **yes**, two | `chaos_coop_marine_havok_lc` (lascannon), `chaos_coop_marine_havok_pc` (plasma cannon) | `csm_armour_havoc` pieces, same co-op rig | `chaos_coop_lascannon` (25 / 50, AI 10-60 m), `chaos_coop_plasma_cannon` (projectile `plasma_cannon_csm_coop`, 0.33 s, AI 5-50 m), evade | as Tactical; audio character `chaos_havoc` | 100 / 500, 699 | as Tactical | none |
| Daemons | **Bloodletters only** | `chaos_coop_bloodletter` (`unit_size` big) | `bloodletter.dcm` (1.28 MB) on **its own rig** `havok_bloodletter_coop` (61 bones, 66 clips) | `chaos_coop_hellblade` (15 / 15 light, 25 / 50 heavy, AI 1-3 m), `chaos_coop_evade_bloodletter` | locomotion `BL_NAV_Walk|Run_*`, `Sprint_F`; melee `BL_MEL_Att_F_3_*`, `BL_MEL_Lt_Att_F_1_*` at 2-7 m; evades; deaths `BL_DIE_Base_01|04|05`; light/heavy/knockback/stun/get-up reactions; **`BL_NAV_GateSpawn_01` (3.03 s)**, `BL_NAV_Taunt_03`; graphs `Bloodletter_Base_Coop`, `_RANGED_REACTIONS_Coop`, `_IMPORT_FLINCH`; combatant melee | 200 / none, 200 | `play_ch_bloodletter_footstep`, `_evade`, `_vox_taunt`, `_vox_roar_high`, `_vox_roar_death`, `_death`; FX `bloodletter_skin`, `_streak`, `_light`, `_limb_burn`, `_limb_vanish` (4.7 s) | none |
| Elite CSM bodyguard | **yes** | `chaos_coop_marine_melee` (`behaviour_tree` "combatant melee") | `csm_armour_berserker` helmet and shoulders + `iron_halo_for_havoc` on the co-op rig | `chaos_coop_thunderhammer` (visual `chaos_powermace`; anim set thunderhammer; heavy hit `mp_stagger` with 80 armour, blown-away, area 5-8 m) | `CSM_MEL_TH_HeavyATK02`, `_XATK_Combo2`, `_YATK_Displace2`, `SM_MEL_TH_YATK2`, `SM_2HM_NAV_TH_MeleeRun|Walk_*`; melee AI | 1000 / 1000, 999 | as Tactical, plus `play_iron_halo` | none |
| Chaos Lord | **campaign only, not a fighter** | `chaos_lord` (`ebps\race_chaos\...`; character `chaos_lord`) | `chaos_lord_body.dcm` (3.57 MB), own rig `havok_chaos_lord_body` (71 bones) | head `race_chaos\heads\cinematic\chaos_lord`; projectile `plasma_cannon_chaos_lord` | **2 clips only**: `CL_Idle` 2 s, `CL_CL_trans_darkritual` 27 s; 1 graph; no AI | 400 / none, 100 | `play_c_l_swirling_energy`, `play_c_l_twirling_energy_epic_2|3`, `play_chaos_lord_laugh`, `play_chaos_lord_damage_exertion` | **yes**: `ebp_sets__s025-001`, `s029-003`, `part_02_04__s_024_rc_400_tolanding`, `part_03__s_016_r625_airlock`, `_r650_weaponplatform`; head `cinematic_chaos_lord` (13 KB face rig) in the same chunks |
| (Lord option) Daemon Prince | campaign only, quick-time boss | `demon_prince` (`behaviour_tree` turret, `combat_preference` none) | `demon_prince_body.dcm` (3.07 MB), own rig `havok_demon_prince_body` (77 bones) | `demon_prince_fists`, `demon_prince_blasts` | 35 clips, all the scripted duel (`DMP_SM_1st..4th_*` struggles, punches, dodge jumps, `DMP_FF_Fire_01`, `DMP_FF_Idle_01`); no locomotion, no death of the usual kind | 400 / none, 100 | `play_demon_prince_swing`, `_grab`, `_spin`, `_head_crush`, `_death` | **yes**: `ebp_sets__daemon_prince`; head also in `data/packages/cinematic_demon_prince.oppc` |
| (extra) Tainted psyker | yes, co-op | `chaos_coop_tainted_psyker` | `tainted_psyker.dcm`, own rig `havok_tainted_psyker` (54 bones, 27 clips) | `chaos_coop_psyker_weapon` (10 / 5, 0.1 s, AI 5-35 m) | `PSY_NAV_StrafeMove_*`, `PSY_NAV_Teleport_In_01`, `PSY_RNG_Attack_01`, **`PSY_RNG_Summon_Gate_01` (4.83 s)**, `PSY_NAV_Death_01`, light reactions and flinches; actions `summon_portal` (plays `tainted_psyker_cast`), `psyker_suicide` | 200 / 500, 499 | `play_tainted_psyker_loop`, `play_ch_psyker_amb_oneshot`, `play_tainted_pysker_explode` (sic) | none |

Not found: Pink Horrors or any Tzeentch, Nurgle or Slaanesh daemon. The one Khorne name is the Berserker armour (`khorne_berserker_berserker_3`).
`chaos_drone` (`havok_chaos_drone`, turret) and the Hell Talon, Predator and Tower of Skulls are campaign set pieces and outside this brief.

**Daemon from a Traitor (supported by evidence).** `chaos_coop_renegade_ig_spawn_bl` is a renegade with a health action at 0.5 that triggers the animation event
`spawndeath`. Its action `spawn bloodletter` spawns `ebps\multiplayer\race_chaos\chaos_coop_bloodletter`, plays `renegade_death` and removes the renegade. The
campaign twin `spawning_chaos_renegade_ig` does the same. No clip of the Guardsman rig is named for it: the nearest is `IG_DIE_Explode_01` (3 s), an *interpretation*.

## Warp breach assets

All are in `master_fx` (`fx-dump`), and every sound resolves in `In_Game_Default`, the default bank set (`audio-event`).

- **The breach itself:** `chaos_portal` (looping; a 1 s "delay to open", a 5 s build-up core with purple lightning, smoke rings up to about 23 m, a flash and
  an explosion). `chaos_portal_no_psyker` is the same without the caster, so it suits a standing breach. `chaos_portal_lord` and `chaos_portal_sky` are bigger
  variants. `chaos_warp_gate_brewing` and `chaos_warp_gate_open` (both looping) give a two-stage gate. `amb_chaos_portal_clouds`, `_thick` and `_wall` add the sky,
  and `amb_chaos_lightning_whip|_wrapping` and `amb_chaos_energy_patch` add more.
  The data's own carrier is the projectile blueprint `psyker_portal` (invisible, plays `chaos_portal` as its lifetime action).
- **Arrivals:** `BL_NAV_GateSpawn_01` (the Bloodletter stepping out of a gate), `mp_chaos_teleport_in`, `tainted_psyker_teleport_in|_out`, `cl_demon_enter|_exit`.
- **Sounds:** `play_chaos_portal_explode_buildup` (1 wem), `play_chaos_portal_explode` (a layer with 5 random children: suits the closure),
  `play_chaos_portal_lightning` (3), `play_chaos_portal_no_psyker` (5: the hum while it is open), `play_chaos_portal_arcs` (9),
  `play_tainted_psyker_spell_teleport_in|_in_flash|_hit`.
- **Not in the data:** a "closing" effect or sound by that name. A closure would be built from `chaos_portal`'s flash and explode elements, with
  `play_chaos_portal_explode` (*interpretation*). The breach's cadence, cap and close rule are the brief's own, not original data.

## Chaos Lord candidate

The install has **no playable Chaos Lord, Champion or Sorcerer** for a melee duel:
- Nemeroth's `chaos_lord` body has two clips (an idle and a 27 s ritual).
- The Daemon Prince is a scripted quick-time boss (turret behaviour, 35 duel-sequence clips that pair with the player's own `DMP_SM_*`).
- Both are campaign-only and on rigs of their own.

**Recommended:** build the Lord on the co-op Chaos Marine (`havok_chaosspacemarine_coop`, the player skeleton) from the elite `chaos_coop_marine_melee`:
- the thunder hammer (`chaos_powermace`) with its heavy displace and stagger strikes;
- the berserker helmet and iron halo;
- a distinct MP armour set (for example `berserk_03` or a `dlc*_set*`) and a modest scale-up for the silhouette.

This keeps a full locomotion, melee, evade and reaction set, and the player's own melee and reaction systems. It also needs no campaign content. The gap-closer can use the
MP marine rig's bull-rush (`SM_BUL_*` clips are in the co-op rig) or a jump-pack leap from the Raptor class's clips. Optional dressing is the campaign's `cinematic_chaos_lord` head
(face rig, as `WarbossRig.BindHead` did) or the `chaos_portal_lord` effect. Either dressing would add a campaign dependency.

## How Warfront could drive each unit (*interpretation*; for the systems design)

| Unit | Drive | Closest Ork / Musou type | Gaps |
|---|---|---|---|
| Traitor Guardsmen / Cultists | Musou enemy pool (fodder), drawn with the Guardsman crowd's rig (`GuardRig`, `GuardCrowd`) and the renegade mesh and lasgun | Shoota Boy (ranged fodder) or Gretchin (frail, numerous) | new enemy type and look; the renegade mesh's binding to `havok_guardsman_a_coop` is not yet checked; no voice; a Cultist-flavoured melee variant would be ours |
| Raptors | puppet World players on the MP rig (as the allied Marines), or a Musou type on the Stormboy hop code | Stormboy (jump squads) | no AI data at all; the leap and melee AI are ours; a Musou-pool Raptor needs the marine rig in the enemy renderer (Musou's `ChaosMarine` look already does this) |
| Chaos Tactical Marines | Musou enemy (the existing `MusouEnemyType.ChaosMarine`, PvP look) with the co-op numbers | `ChaosMarine` / Shoota Boy (ranged) | the co-op bolter and grenade data, a Warfront row |
| Havocs | Musou enemy, ranged elite | Big Shoota Nob / Rokkit Boy (heavy ranged) | lascannon and plasma cannon projectiles and visuals in the enemy pool |
| Bloodletters (Daemon squads) | Musou enemy pool, own look (own rig, like the Weirdboy's) | Boy / 'Ard Boy (melee rush) | new look and clip table for `havok_bloodletter_coop`; gate-spawn arrival; breach spawner and cap |
| Elite bodyguard | Musou elite melee | Nob (elite) | thunder hammer strikes as an enemy swing; berserker / iron-halo look |
| Chaos Lord | a new duel boss: either a Musou boss (as the Warboss) or a puppet World player with the player's melee stack | Warboss (boss) | everything about the duel; no original boss data fits |

## Findings that matter most for the design

1. Every co-op Chaos unit except Raptors has blueprint, wargear, health, AI ranges and a rig with graphs in a **multiplayer** set
   (`ebp_sets__mp_coop_set_grid_test`, `cp_arena_desert_a`). The army needs no campaign content.
2. The co-op Chaos Marine rig is the MP player skeleton bone for bone. Marines, Havocs, the bodyguard and a marine-based Lord can share the player renderer and armour.
3. **No AI Raptor exists**. Raptors must be built on the PvP Raptor class (puppets) or on the Stormboy code.
4. **No Cultists.** The renegade guard is the only Chaos fodder, on the Guardsman co-op rig that Musou already draws.
5. **Bloodletters are the only daemons**, and they carry a gate-spawn clip. The data also has a "renegade turns into a Bloodletter" spawn chain.
6. The breach has a complete original look and sound (`chaos_portal_no_psyker`, `chaos_warp_gate_*`, `play_chaos_portal_*`), all in multiplayer packages.
7. **No usable Lord model.** A Lord built on the elite melee marine is the faithful-asset path. Nemeroth or the Daemon Prince would add a campaign dependency and get no usable clips.
8. Original durability spread (health / armour): renegade 60 / 0, Bloodletter 200 / 0, Havoc 100 / 500, Tactical 500 / 400, elite 1000 / 1000. Warfront balance is ours,
   but these give the relative weights.
9. The original co-op wave scripts were not found by name: no "wave" or "spawn" entries in the `cp_*` chunks. The Lua is elsewhere and was not read.

## Campaign dependencies (only if chosen)

- the Chaos Lord body and head, in `ebp_sets__s025-001` and others;
- the Daemon Prince, in `ebp_sets__daemon_prince`;
- the campaign `chaos_*` twins in `ebp_sets__chaos_core_units`, which are not needed.

The recommended roster has **no** campaign dependency.

## Tool added

`smtool find-entry <game-dir|auto> <regex>... [--under <folder>]... [--names-only]` (`src/SmTool/FindEntryCommand.cs`). It lists which archives hold an entry whose file
name matches, reading only tables of contents (a full sweep of `data/chunks` and `data/packages` takes about 50 s). Tests: `FindEntryCommandTests` (synthetic archives).
