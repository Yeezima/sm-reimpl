# Custom classes, character customizer and perks: what the install's data says

Research for the custom-class creator, the character customizer and the perks (owner's request, 2026-10-28). Everything here was read
from the user's install (app 55150, buildid 7412325) with `smtool` (read-only); nothing from the install is copied into the repo
beyond names, numbers and one-line paraphrases. Labels follow CLAUDE.md: **Verified-local** (read from the files), **interpretation**
(our reading), **not in the data** (lives in the executable or the online service; nothing to read).

Where things live (all `data/packages/*.oppc`, BAF attribute files unless noted):

| Package / entry | Holds |
|---|---|
| `global` / `global` | `extensions/multiplayer_settings/ingame_progression/classes` (6 classes), `.../challenge_groups` (unlocks), `profile_progression_info` (levels, feature unlocks), `character_customization` (camera only) |
| `wargear` (811 entries) | weapons, equipment, abilities, **perks** (`mp_perk_*`, slot `perk`), armour pieces (slots `head`, `torso`, `backpack`, `leftshoulder`, `rightshoulder`, `leftarm`, `rightarm`, `leftleg`, `rightleg`, `belt`) |
| `mp_armor_set` (32 entries) | armour sets: item lists, race, `usage` (`customizable` / `predefined`), default paint scheme |
| `custom_paint_scheme` (80), `custom_paint_colour` (65), `custom_paint_decal` (122) | paint presets, the colour palette, shoulder badges |
| `ebps` / `sm_space_marine_player`, `csm_chaos_marine_player` | player blueprints: perk effects on health/armour/damage taken, default paint scheme |
| `data/locale/english/ucs.pc` | all display text (id to string); read in memory only |
| `climb_sway_scatter` | accuracy tuning sets the accuracy perks switch to |

Reproduce: `smtool baf-dump data/packages/global.oppc --locale` (whole file), `... wargear.oppc --entry "^mp_perk_" --locale`,
`... data/packages --grep "mp_perk_" ` (every package), `smtool locale 104200-104300` (text by id range or regex).
These two commands were added with this document (`src/SmTool/DumpCommands.cs`, `Dump/`; tests `tests/SmFormats.Tests/DumpTests.cs`).
Archive entry names drop the folders; wargear names are unique for everything in this document (the 38 duplicated names are
singleplayer or generic: `head`, `leg_left`, `bolter`, `frag_grenade`, ...).

## 0. Counts

| Thing | Count |
|---|---|
| Multiplayer classes | 6 (3 per side) |
| Perks | **48 selectable** (24 class perks, 24 weapon perks) + 1 internal (`mp_perk_iron_halo_for_havoc`) |
| Perk slots | 2 (the second from player level 15); a third slot's label exists but no unlock for it |
| Custom loadout slots | **6** per player, unlocked by level (4, 11, 17, 23, 29, 35); the owner wants 10 |
| Armour sets | 32 = Space Marine 8 customizable + 8 predefined, Chaos 9 customizable + 7 predefined |
| Armour slots | 9 (+ a `belt` piece that rides on some torsos) |
| Paint schemes | 80 (Space Marine 53, Chaos 27); **47 are "chapter/warband" presets** shown in the customizer (31 + 16) |
| Paint colours | 65 entries, 60 visible in the customizer in 8 groups |
| Shoulder badges (decals) | 122 entries: 106 visible (89 Space Marine incl. 1 blank, 17 Chaos); the rest hidden |
| Player levels | 41 |

## 1. Classes and loadout rules (`global`, `.../ingame_progression/classes`, **Verified-local**)

Each class has `base_wargear` (always carried), `startup_wargear` (the choices), `class_perks` (perks it may take), `default_perk`,
`override_wargear` (forced armour parts), `default_armor_set`, `mp_class`, `race`, `display_name`/`display_description` ids.
`unlocks/class_unlocks` has 2 (Assault Marine: 10) empty `xp_unlock` entries (`xp_required` 100, 200, ...) with no `wargear_unlocks`:
vestigial, nothing to apply. Class availability is a profile-level rule (section 1.3).

| Class key | Name | mp_class | Always carried (base_wargear) | Chooses | Perk list | Default perk | Default armour set |
|---|---|---|---|---|---|---|---|
| `sm - tactical` | Tactical Marine | tactical | sprint, **combat knife**, **bolt pistol**, evade (roll) | **1 primary** (7) + 1 equipment (4) | 20 | Teleport Homer | `sm_default` (Mk7 Aquila) |
| `sm - devastator` | Devastator | heavy | sprint, heavy evade (hop/stomp), devastator bolt pistol | **1 heavy weapon** (3) + 1 equipment (4) | 14 | Feel No Pain | `sm_default` |
| `sm - assault marine` | Assault Marine | assault | jump pack, sprint, jump evade | **1 pistol** (4) + **1 melee** (5) + 1 equipment (4); **no primary** | 14 | Furious Charge | `sm_default` |
| `csm - tactical` | Chaos Space Marine | tactical | as Tactical (Chaos knife and pistol) | as Tactical | 20 | Teleport Homer | `chaos_default` |
| `csm - havoc` | Chaos Havoc | heavy | as Devastator | as Devastator | 14 | Feel No Pain | `chaos_default` |
| `csm - raptor` | Chaos Raptor | assault | Chaos jump pack, sprint, jump evade | as Assault, Chaos melee list | 14 | Furious Charge | `chaos_default` |

Answers to the owner's questions:
- **Bolt pistol always equipped?** Yes for Tactical and Devastator/Havoc (it is `base_wargear`). Assault/Raptor are the only class that chooses the sidearm.
- **Tactical and Devastator/Havoc choose the primary**, nothing else (their pistol and melee are fixed; Tactical's melee is the knife; Devastator/Havoc list no melee weapon, only the heavy evade's stomp).
- **Assault may choose** the pistol (bolt pistol, plasma pistol, and two pistol-slot Bolters) and the **melee weapon** (5 Space Marine / 5 Chaos), plus equipment. It has no ranged primary.
- **Grenade slot** = "equipment" (4 offered): frag, **krak = the "Blind Grenades"** (locale: flash grenades that blind or disorient), combat stims, and `melta_bomb` (shown as "Plasma Grenade"; `visible_in_customizer` false, so hidden; `MpClasses` already skips it). Equipment counts: frag 2 (initial 1), blind 2 (initial 1), stims 1, melta bomb 1; the perk Favor of the Armory doubles them (4/2 frag and blind, 2 stims).
- **The two pistol-slot Bolters** (`*_pistol_bolter`, `*_pistol_bolter_gold`, listed for Assault) are the "True Grit" perk's content (the perk: bolter in place of the bolt pistol, one-handed). Whether the front end hides them without the perk is **not in the data** (assume yes).
- `mp_perk_secondary_weapon` ("Weapon Versatility", Tactical): "an additional weapon can be equipped". The extra slot is **not in the data** (no second primary list; the generic slot names `Primary`/`Secondary`/`Pistol`/`Exotic`/`Offhand`... exist in the text). Open question.
- **Forced armour parts** (`override_wargear/gear_and_req`): Devastator's backpack becomes the weapon pack for the chosen weapon (`sm_backpack_heavy_bolter` / `_lascannon` / `_plasma_cannon`; packages `sm_hbolter_mp_bp_001`, ...); Havoc's backpack is the generic pack (`csm_backpack_havoc`, `sm_generic_mp_bp_001`) and it gets the Iron Halo icon item when the Iron Halo perk is chosen; **Chaos Raptor's head and both legs are forced to the Raptor set** (`csm_armour_mp_raptor_01_head/_leg_left/_leg_right`), required wargear empty. Assault Marine has no override: how the jump pack and the armour's backpack piece share the back is **not in the data** (the jump pack is a `coreability` wargear with its own model `sm_jumppack_mp_basic_001` / `csm_jumpack_mp_basic`).

### 1.1 Choices per class (names are the install's; Chaos names in brackets) and unlock levels

Level = `common_data/unlock_info/level_requirement/level_required` of the wargear file (player level, section 1.3). Perk-gated and challenge-gated items are marked.

| Slot | Internal name (`sm_mp_...`; Chaos `csm_mp_...`) | Display name | Level | Classes |
|---|---|---|---|---|
| primary | `ranged_bolter`, `ranged_bolter_gold` | Bolter, Relic Bolter [Possessed Bolter] | 1, 1 | Tactical |
| primary | `ranged_sniper_rifle` | Stalker Bolter [Daemon Eye Bolter] | 4 | Tactical |
| primary | `ranged_plasma_gun` | Plasma Gun | 8 | Tactical |
| primary | `ranged_melta` | Melta Gun | 12 | Tactical |
| primary | `ranged_storm_bolter` | Storm Bolter [Twin Bolter] | 16 | Tactical |
| primary | `ranged_grenade_launcher` | Vengeance Launcher | 22 | Tactical |
| heavy | `exotic_heavy_bolter` | Heavy Bolter | 1 | Devastator/Havoc |
| heavy | `exotic_las_cannon` | Lascannon | 10 | Devastator/Havoc |
| heavy | `exotic_plasma_cannon` | Plasma Cannon | 20 | Devastator/Havoc |
| pistol | `pistol_bolt_pistol` | Bolt Pistol | 1 | Assault (fixed for Tactical; `_devastator` variant fixed for Devastator) |
| pistol | `pistol_plasma_pistol` | Plasma Pistol | 14 | Assault |
| pistol | `pistol_bolter`, `pistol_bolter_gold` | Bolter, Relic Bolter [Possessed Bolter] | 1, 1 (+ True Grit perk, level 25) | Assault |
| melee | `melee_chainsword`, `melee_chainsword_gold` | Chainsword, Relic Chainsword [Possessed Chainsword] | 1, 1 | Assault |
| melee | `melee_powersword` | Power Sword | 1 | Assault |
| melee | `melee_power_axe` [`melee_chain_axe`] | Power Axe [Chain Axe] | 6 | Assault |
| melee | `melee_2hand_thunder_hammer` [`melee_2hand_power_mace`] | Thunder Hammer [Daemon Maul] | 18 | Assault |
| melee | `melee_combat_knife` | Combat Knife | 1 | Tactical (fixed) |
| equipment | `eq_frag_grenade` | Frag Grenades | 1 | all |
| equipment | `eq_krak_grenade` | Blind Grenades | 5 | all |
| equipment | `eq_stimpack` (shared by both sides) | Combat Stimulants | 9 | all |
| equipment | `eq_melta_bomb` (shared) | "Plasma Grenade" (hidden) | 1 | not offered |

The Chaos Raptor melee list is the same five slots with Chaos weapons (chainsword, possessed chainsword, chain axe, daemon maul, power sword).
Not offered by any class but present: `eq_demolition_charge` (remote detonators), `eq_targeter` (auspex, reveals enemies), `exotic_auto_cannon`, `ranged_lasgun_ai`, `abil_iron_halo`/`eq_iron_halo` (all `visible_in_customizer` false).
Weapon unlock levels are identical on both sides. Weapon values (damage, clips) are in `spec/behavior.md` and `WeaponTuning`; clip sizes and perk variants are in section 2.

Core abilities (`coreability` slot, level 1): `abil_sprint`, `core_abil_evade` ("Campaign Evade"), `core_abil_heavy_evade` (text "Close Combat"; the stomp), `core_abil_jump_evade`, `abil_jumppack`, `abil_revive` (MP Revive), `abil_iron_halo`.

### 1.2 Loadout structure in the original (names only)

Text ids 104215-104228 name 14 generic wargear slots (Primary, Secondary, Pistol, Exotic, Offhand, Spells, Core Ability, Primary Perk, Secondary Perk, Perk 3, Special Equipment, Equipment 2, 3, 4); a class uses a few. "Loadout %1NUM%", "CUSTOM LOADOUT %1NUM%", "DEFAULTS"/"CUSTOM" (111797/111798) and per-class tabs "Tactical", "Heavy", "Assault/Raptor" (111793-111795; "Tactical Marine %1%" etc.) show the front end: **per class, a default loadout plus numbered custom loadouts**. The *default* loadouts are **not in the data** (no wargear set for them; `wargear_set` holds only singleplayer sets): `MpClasses`' first-listed-choice default stays an interpretation. "Appearance %1NUM%" (111828) also exists: apparently several saved appearances; how many is not in the data.

### 1.3 Progression: levels, unlocks and the loadout slots (`global`, `profile_progression_info`)

41 levels. Experience at which level n starts (level 1 = 0): 0, 800, 2220, 4260, 7000, 9840, 12780, 15820, 18960, 22200, 25540, 28980, 32520, 36160, 40200, 44640, 49480, 54720, 60360, 66400, 72840, 79680, 86920, 94960, 103800, 113440, 123880, 135120, 147160, 160000, 173640, 189280, 208920, 233560, 263200, 301840, 349480, 407120, 474760, 552400, 671040 (the `unlocks` lists of all 41 levels are empty: unlocks hang on the items, not on the table).
`unlocked_func_info` (each is a `level_requirement`):

| Feature | Level |
|---|---|
| Devastator/Havoc | 2 |
| Assault/Raptor | 3 |
| Customizer, custom appearance, **loadout 1** | 4 |
| Loadout 2, 3, 4, 5, 6 | 11, 17, 23, 29, 35 |
| Second perk slot | 15 |
| Prestige | 41 |

(Tactical has no flag, so it is available from level 1 and the Chaos classes use the same flags: both **interpretation**, no Chaos-specific keys.) XP rewards are in `xp_settings_pvp` (e.g. point capture 200, uncapture 50 base XP) and are outside this document.

### 1.4 The 6 vs 10 slots

The data gives **six** equipment loadouts, gated by level, and a custom appearance gate. Ten slots (the owner's wish) is a deliberate change; the level gates can be dropped or kept.

## 2. Perks (`wargear`, slot `perk`; classes from `global`)

Mechanism (**Verified-local**): a perk file does one thing, `apply_upgrade_string` of a string on its owner (`strings/entry`). The effect is whatever
checks that string: `required_upgrade_name/required_list/upgrade_name` in the player blueprints (`ebps`: health, armour, damage taken, death/armour-break blasts), in weapon and
equipment wargear (`by_upgrade/ammunition_info` clip sizes; `actions_by_upgrade` damage and heal; `reload_upgrade_info`; `charge_upgrade_list`; `kick_back_and_scatter_values` accuracy sets; `ranged_delivery_type` projectile blueprints; `jump_info`), always as "base entry plus entry that needs the upgrade".
So a perk is functional only for the item that checks it (weapon perks do nothing without the weapon). Upgrade strings equal the perk's file name except: `weap_las_cannon_recharge` -> `mp_perk_weap_las_cannon_reload`, `weap_plasma_cannon_alt_fire_charge_time` -> `..._cooldown`, `weap_power_axe_slow_on_hit` -> `..._heal_on_hit` (the file name lies: it heals), `iron_halo_for_havoc` -> `mp_perk_iron_halo`, `secondary_weapon` -> none.
Some weapons also grant internal strings (`mp_perk_weap_heavy_bolter_scatter`, `..._bolt_pistol_scatter`, `fury_mode`, ...), which are not perks.

Perk level = the file's `unlock_info/level_required`. **Weapon perks are level 1 in the file and are unlocked by challenges instead** (section 2.2).
Classes: SA/CR = Assault Marine / Raptor, ST/CT = Tactical, SD/CH = Devastator / Havoc (a perk is on both sides' list of the same role). Values are stored values; units follow the data (seconds, game units; "x" = multiplier).

### 2.1 Class perks (24)

| Perk (`mp_perk_...`) | Display name | Lvl | Classes | Effect as data | Needs (we lack) |
|---|---|---|---|---|---|
| `improved_bullrush` (default SA/CR) | Furious Charge | 1 | SA CR | charge hit (`ability_bullrush_01`, every melee weapon; jump pack `ability_dash_attack`): 75 + 25 armour -> 100 + 50, reaction stun -> stagger | charge exists (M29); needs the upgraded entry |
| `ranged_damage_resistance` | Impenetrable | 7 | SA CR | `received_ranged_damage` x0.7 (player blueprint lifetime action) | damage-taken modifier |
| `jump_pack_explodes_on_death` | Final Vengeance | 13 | SA CR | on death, delayed area blast: `damage_general` 100, explosive | death blast |
| `jump_pack_take_off_damage` | Blast Off | 1 | SA CR | jump take-off (`actn_take_off`) area damage 25, explosive (none without the perk) | take-off blast |
| `jump_pack_ground_pound_damage` | Death From Above | 19 | SA CR | ground-pound hit (`actn_gp_hit`) damage 25 -> 75 | ground pound exists (M14) |
| `jump_pack_reduced_cooldown` | Air-Cooled Thrusters | 31 | SA CR | `jump_info/power_settings/recharge_rate` 10 -> 15 (also other landing tuning: see `sm_mp_abil_jumppack`'s second `jump_info`) | jump pack energy exists |
| `special_y_hold_attack` | Sure Strike | 1 | SA CR | heavy strike (`ability_melee_y*`, "Interrupt attack"), measured on the power axe: stun damage 100 -> 500, hit damage 25 -> 30, reaction `reaction_mp_stun` -> `reaction_mp_stagger`; the later strikes of the chain and the stomp variants (`displace_*`) have upgraded entries too (read them per weapon) | heavy strikes exist (M28) |
| `bolter_in_place_of_pistol` | True Grit | 25 | SA CR | no data consumer; adds the two pistol-slot Bolters to the Assault list (front end) | one-handed bolter in the pistol slot |
| `respawn_timer` | Rapid Deployment | 1 | ST CT | no data consumer (executable): shorter respawn | respawn rule |
| `shield_recharge` | Larraman's Blessing | 25 | ST CT | `health_regeneration` +45, `health_regeneration_delay` -1.5 (base `health_regen_rate` 5, `health_regen_delay` 5, `health_max` 100) | health regen |
| `enemy_info` | Tactical Readout | 1 | ST CT | no data consumer: shows armour/health bars over enemies | HUD |
| `combat_knife_damage` | Serrated Combat Blade | 6 | ST CT | knife attacks: primary target 40 -> 65, secondary targets 20 -> 32 | knife exists |
| `secondary_weapon` | Weapon Versatility | 13 | ST CT | no consumer, no upgrade string (front end / executable) | second weapon slot |
| `equipment_double` | Favor of the Armory | 31 | ST CT | equipment clip: frag, blind 2 -> 4 (initial 1 -> 2); stims, melta bomb, demolition 1 -> 2 | equipment counts |
| `friendly_spawn_target` (default ST/CT) | Teleport Homer | 1 | ST CT | no data consumer; the Dreadnought blueprints carry it by default (`wargear_ext/default_wargear`); allies may respawn at the carrier | spawn rule |
| `equipment_effectiveness` | Master-Crafted Wargear | 19 | ST CT | swaps in "upgraded" projectile blueprints: frag damage 200 -> 300, radius 10 -> 15; blind radius 8 -> 12, blind time 3.5 -> 6 s (direct 2 -> 3), stun 700 -> 1000; stims 5 -> 7 s with received damage x0.75/0.75/0.5/0.75 (world/ranged/melee/explosive, health only) -> x0.75/0.6/0.35/0.6 and melee damage x1.35 -> x2 | stims, grenades (thrown exist M13) |
| `melee_damage_resistance` (default SD/CH) | Feel No Pain | 1 | SD CH | `received_melee_damage` x0.7 and `received_explosive_damage` x0.7 | damage-taken modifier |
| `speed_increase` | Fast Stomp | 13 | SD CH | stomp animation faster: the displace graphs' "Set Stomp Speed Float" modifier (enabled by `BOOL_UPGRADE_mp_perk_speed_increase`) sets the stomp clip's playback speed to 1.5 (Weapons C, decoded; spec "Perks") | animation speed |
| `better_stomp` | Furious Stomp | 1 | SD CH | stomp (`displace_footstomp`, knife and heavy evade) damage 25 -> 35, armour damage 50 -> 75, radius 7 -> 10, falloff 4.5/6/7 -> 6/8/10 | stomp exists (heavy evade) |
| `shield_break_damage` | Reactive Blast | 7 | SD CH | when armour breaks (also by a breaker): area blast r 4.5, 25 + 35 armour, explosive, no fall-off, stun with 2.5 m knockback (M43 check) | armour-break blast |
| `snap_to_increase` | Signum Link | 1 | SD CH | no data consumer: enemies show more clearly; the only candidate data (perks/finish) are the unbound aim-assist sets `*_heavy_bolter_(zoom_)mp__snap_upgrade` and the plasma cannon's, `max_angle_for_ranged_aim_snap_to` 0 -> 12 | HUD or aim assist |
| `iron_halo` (+ `iron_halo_for_havoc`) | Iron Halo / Chaos Icon | 19 | SD CH | `armourshield_max` 100 -> 150 (regen delay 7, rate 33 unchanged); Havoc adds the icon item as forced gear | armour value |
| `shield_regen_delay` | Artificer Armor | 25 | SD CH | `armourshield_regeneration_delay` -5 (7 -> 2) | armour regen |
| `starting_ammo` | Ammunition Stores | 31 | SD CH | heavy bolter clip 400 -> 800, plasma cannon 100 -> 200, lascannon reserve 30 -> 60 | ammo variants |

### 2.2 Weapon perks (24) and how they are unlocked

Unlock = a weapon-mastery challenge group in `global` (`challenge_groups`, type `weapon`): kill counts with that weapon (the "Veteran" step, e.g. 50 kills) or a skill stat
(kill streaks, double kills, headshots, long-range kills). **Challenge targets are lifetime stats of the player's profile.** Display names are the install's.

| Perk (`mp_perk_weap_...`) | Display name | Unlock (stat: target) | Classes | Effect as data (base -> perk) |
|---|---|---|---|---|
| `bolt_pistol_larger_mag` | Bolt Pistol Magazine | pistol kills: 30 | SA CR | clip 10 -> 16 (reserve 80) |
| `plasma_pistol_less_heat` | Plasma Heat Sink | plasma pistol kills: 30 | SA CR | the player blueprint applies the string `mp_plasma_pistol_upgraded_vent`; the pistol graph (`MP_SM_PlasmaPistol`) then runs its "Upgraded Vent State", i.e. the wargear's `ability_utility_upgraded`: vent 45 -> 70 heat/s (found in fidelity gap 7; the string is a graph variable, not a wargear entry) |
| `chainsword_heal_on_hit` | Swordsman's Zeal | chainsword kills: 50 | SA CR | on a hit while below full health: heal 30 per primary target per swing (x1-x4, 180); the 12 entry also needs `is_in_coop` (co-op only, M43 check); jump pack hits heal 30/20 |
| `power_axe_slow_on_hit` | Axeman's Zeal | axe kills: 50 | SA CR | same heal, 30 / 12 (upgrade string `..._heal_on_hit`) |
| `power_sword_heal_on_hit` | Power Swordsman's Zeal | sword kills: 50 | SA CR | heal 20 / 12 per swing |
| `thunder_hammer_aoe_strike` | Killing Blow | hammer kills: 50 | SA CR | direct blow damage 2000 + 2000 armour (kills outright); other targets 100-150 |
| `bolter_accuracy` | Bolter Targeter | bolter kill streaks: 30 | ST CT | scatter sets `sm_mp_ranged_bolter_accuracy_perk` / `aim_...`: initial angle 0.25 -> 0.125 (aimed 0.075 -> 0.03), per-shot increase 0.35 -> 0.21 (aimed 0.3 -> 0.17), decay 2.4 -> 2.6 per s |
| `bolter_kraken_rounds` | Kraken Bolts | bolter kills: 50 | ST CT | bullet becomes an armour breaker (`armourshield_breaker` true) with penetration points 2 (punches through; `default_heavy_armor` cost 2) |
| `storm_bolter_reload` | Rapid-Fire Reload | storm bolter kills: 50 | ST CT | reload 2 s -> 1 s |
| `storm_bolter_hip_fire` | Improved Twin-Link | storm bolter kill streaks: 30 | ST CT | hip scatter set `..._accuracy_perk`: initial 0.45 -> 0.4, max 2.25 -> 1.9, decay 3.3 -> 2, climb per shot 0.75 -> 0.35 |
| `stalker_larger_mag` | Stalker Ammo Reserves | stalker kills: 50 | ST CT | reserve 40 -> 60 (clip 10) |
| `stalker_zoom_modes` | Burst Fire | stalker headshot kills: 15 | ST CT | single shot (cooldown 0.267) -> 2-round burst, 0.1 s apart, cooldown 0.475; burst scatter sets |
| `gren_launcher_automatic` | Vengeance Projectiles | launcher kills: 50 | ST CT | `max_active_projectiles` (live charges) 3 -> 5 |
| `gren_launcher_more_ammo` | Vengeance Payload | launcher double kills: 16 | ST CT | reserve 17 -> 35 |
| `melta_gun_range_increase` | Improved Melta Charge | melta kills: 50 | ST CT | auto-recharge 0.5 -> 1 per s; cone length 20 -> 23, angle 8; reload time 0.5 -> 0.275 |
| `melta_gun_clip_size` | Additional Melta Fuel | melta double kills: 16 | ST CT | reserve 12 -> 20 (initial 1) |
| `plasma_gun_less_heat` | Improved Plasma Charge | plasma gun kills: 50 | ST CT | heavy charge threshold 1.1 s x0.65 (0.715 s) |
| `plasma_gun_alt_fire_cost` | Enlarged Plasma Chamber | plasma gun charged kills: 35 | ST CT | energy cell 125 -> 200 |
| `heavy_bolter_braced_fire_time` | Heavy Bolter Expertise | heavy bolter kills: 50 | SD CH | the brace entry's playback speed `FLT_Brace_Entry_Time` 1 -> 2 (`SM_Base_MP` set-float modifier enabled by `BOOL_UPGRADE_mp_perk_weap_heavy_bolter_braced_fire_time`): entry 1.433 -> 0.717 s, and the exit at the same speed 1.300 -> 0.650 s (owner 2026-10-28: the perk sped up getting into and out of the brace; fidelity gap 7) |
| `heavy_bolter_heat` | Heavy Bolter Coolant | heavy bolter kill streaks: 30 | SD CH | heat per shot 3 -> 1 (braced 1.8 -> 0.7) |
| `las_cannon_recharge` | Lascannon Capacitors | lascannon kills: 50 | SD CH | reload 2.25 s -> 1.1 s |
| `las_cannon_zoom` | Lascannon Stabilizers | lascannon long-range kills: 30 | SD CH | max charge time 1.5 -> 0.8 s (min x0.53) and handling sets `..._handling_perk` (less climb, sway) |
| `plasma_cannon_less_heat` | Plasma Heat Sink | plasma cannon kills: 50 | SD CH | primary heat 21 -> 11 per shot |
| `plasma_cannon_alt_fire_charge_time` | Improved Plasma Charge | plasma cannon double kills: 16 | SD CH | charge thresholds x0.55 (3 s -> 1.65 s) |

### 2.3 Simulation features the perks need that we do not have yet (for the milestone)

Present today (CLAUDE.md): hit-scan and projectile weapons, heat, charge, melee combos, heavy strikes and stuns, the charge, jump pack and ground pound, thrown grenades, health and armour shield.
Missing: a **modifier layer** (`received_*_damage` multipliers; health and armour regeneration deltas; `melee_damage` multiplier for stims); **combat stims** (simulated since perks/inert-2: spec/behavior.md "Combat stims"); **ammunition variants**; **accuracy/scatter and climb** (done since weapons/spread, the owner's decision of 2026-10-28: spec/behavior.md "Weapon spread"), which four perks switch (Bolter Targeter, Improved Twin-Link, Lascannon Stabilizers, Burst Fire); death and armour-break **area blasts**; take-off blast; **heal on hit**; **burst fire**; **reload and charge time variants**; **equipment counts**; spawn rules (Rapid Deployment, Teleport Homer); HUD indicators (Tactical Readout, Signum Link); a **second weapon slot** and the **one-handed Bolter** (Weapon Versatility, True Grit); animation speed (Fast Stomp, Heavy Bolter Expertise). Effects with **no data consumer** (executable side): respawn timer, Teleport Homer, Tactical Readout, Signum Link, Weapon Versatility, True Grit, Fast Stomp, Heavy Bolter Expertise.
Singleplayer dependence: none for perks. The mode scripts' constant pools (read only; `swapwargear`, `spawnmanager`, `Player`, `unit`) contain no perk names, only `loadout`/`wargear` calls.

## 3. Armour (`wargear`, `mp_armor_set`)

- **Slots** (`common_data/slot`): `head`, `torso`, `backpack`, `leftshoulder`, `rightshoulder`, `leftarm`, `rightarm`, `leftleg`, `rightleg`. The customizer text names them "helmet", "left/right shoulder (pad)", "left/right arm" (vambrace), "torso" (breast plate), "left/right leg" (greave), "backpack" (text 104229-104238). A `belt` wargear piece (`sm_armour_*_belt`) is a second visual of the **torso** piece (the torso item's `visual_data/attachments` lists two packages), not a choice.
- **A piece** is a wargear file `race_marine\armour\sm_<set>_<part>` / `race_chaos\armour\csm_armour_mp_<set>_<part>` with `visual_data/attachments/*/visual_package` (the package of mesh and textures, e.g. `sm_armour_mp_basic_helmet`), `ui_data/name` and description, `helmet_override` (`helmet` / `no_helmet` / `no_override`), `load_type` `mp_armor` (or `mp_accessory`), and **level 1 for every piece** (they are unlocked by challenges, below). Package naming and texture layout (`_wg`, `_tt1/_tt2`, `_tsf/_tsh/_tst`, `_v`, paint mask `_pnt`) are in `docs/discovery.md` ("Chaos Space Marine multiplayer models", "Armour paint"). All pieces bind to the one rig `havok_sm_base_mp__hkxx` by bone name (M3/M17). The old `sm_armour_basic_001` set is singleplayer-style and not part of the MP sets.
- **Sets** (`mp_armor_set`): `item_list` of 9 wargear files, `set_name`/`set_description` text ids, `usable_by_race`, `usage`, `custom_paint_scheme` (predefined sets only), `set_id`.
  `customizable` = a **mix-and-match library**: every piece of every customizable set of the race is offered per slot (the sets share parts, e.g. Mk6/Mk8 reuse Mk7 arms). `predefined` = a fixed full set (the DLC sets and the campaign-reward sets) that carries its own paint scheme.
- **Defaults**: `default_armor_set` `sm_default` = **Mk7 Aquila** (`mp_armor_set/mk_7_aquila`, pieces `sm_mk7aquila_*`, packages `sm_armour_mp_basic_*`) and `chaos_default` = "Chaos Armor" (`csm_armour_mp_basic_*`). Default paint: `sm_ultramarines` / `chaos_multiplayer` (player blueprints, M30 notes).

Customizable sets (distinct pieces offered per slot, from all sets of a race):

| Race | Sets | head | torso | backpack | shoulders (each) | arms (each) | legs (each) |
|---|---|---|---|---|---|---|---|
| Space Marine (8 sets) | Mk7 Aquila (default), Mk4 Maximus, Mk6 Corvus, Mk8 Errant, Master-Crafted, Veteran, Relic, Battle-Damaged | 7 | 7 | 7 | 7 | 6 | 8 |
| Chaos (9 sets) | Chaos Armor (default), Armor of Scorn / Hate / Rage (berserker 1-3, packages `berserk_0x`), Black Crusader / Daemon Armor / Spiked Armor (havoc 1-3), Shock Armor, Blood Disciples (raptor 2, 3: torso, shoulders, arms only; the rest `basic`) | 7 | 9 | 7 | 9 | 9 | 7 |

Predefined sets: Space Marine 8 (Black Templars, Space Wolves, Blood Ravens, Legion of the Damned, Blood Angels Veteran, Salamanders Veteran, Iron Hands Veteran = DLC; Ultramarines Champion = campaign reward) and Chaos 7 (Emperor's Children, Iron Warriors, Alpha Legion Champion, Death Guard Champion, two more DLC sets named only `dlc2_set3` / `dlc3_set2` (checked in fix/armour-pieces: their `set_name` 121688 / 121692, descriptions and pieces' `ui_data/name` are those very strings in the English locale, their schemes have name 0, and no other text names them, so the customizer shows the ids as the data does); Chosen of Nemeroth = campaign reward). Their paint schemes: `sm_black_templars`, `sm_space_wolves`, `sm_blood_ravens_armor_set`, `dlc_dlc1_set1`, `dlc_dlc2_set1/2`, `dlc_dlc3_set1`, `sm_ultramarines_armor_set`; `csm_emperors_children_armor_set`, `csm_iron_warriors_armor_set`, `dlc_dlc1_set2`, `dlc_dlc2_set3`, `dlc_dlc3_set2/3`, `csm_chosen_of_nemeroth`. Whether the DLC sets need ownership is **not in the data**.

### 3.1 Armour unlocks (`global`, `challenge_groups`, type `armor`)

Pieces unlock in tiers of a lifetime stat (a tier unlocks several pieces; tier 1 and the last tier award XP only). Each row: group, stat, tier targets: pieces.

| Group (set) | Stat | Tiers (target: pieces) |
|---|---|---|
| Mk8 Errant | wins as Space Marine | 15: legs; 50: torso |
| Battle-Damaged | kills after dying (1), deaths while capturing (3), deaths by explosives (50), deaths by melee (50) | 3: arms+legs; 3: backpack+torso; 50: shoulders; 50: head |
| Veteran | Savior Kills | 25: arms+legs; 50: backpack+torso; 100: shoulders; 200: head |
| Relic | killstreaks as Space Marine | 5: arms+legs; 10: backpack; 25: shoulders; 50: head |
| Master-Crafted | kills as Space Marine | 80: arms+legs; 120: backpack+torso; 250: shoulders; 500: head |
| Mk6 Corvus | assists | 25: legs; 50: backpack+torso; 100: shoulders; 200: head |
| Mk4 Maximus | reach player level 41 | the whole set |
| Ultramarines Champion | complete the singleplayer campaign (**singleplayer dependency**) | the whole set |
| Armor of Scorn / Hate / Rage | revenge kills / grudge kills / killstreaks as Chaos | arms+legs, backpack+torso, shoulders, head at 15/30/50/75, 10/20/40/80, 5/10/25/50 |
| Black Crusader | wins as Chaos | 15: arms+legs; 30: backpack+torso; 50: shoulders; 100: head |
| Daemon Armor | reach player level 41 | the whole set |
| Shock Armor | kills as Chaos | 80: arms; 120: torso; 250: shoulders |
| Blood Disciples | melee kills | 25: arms; 50: torso; 100: shoulders |
| Chosen of Nemeroth | complete the campaign (**singleplayer dependency**) | the whole set |

Free from the start (no challenge): Mk7 Aquila, Chaos Armor, Spiked Armor (no challenge group found: an apparent gap or a DLC; unknown), and the shared pieces inside other sets (Mk6/Mk8 arms and shoulders; Raptor sets' helmet, legs and backpack are `basic`).
Challenge ids resolve to wargear files through `challenge/wargear` (e.g. `sm_mk8neck_torso`); the piece files are the ones listed in `mp_armor_set` items.

## 4. Colours (`custom_paint_*`; mask channels in `docs/discovery.md` "Armour paint")

- **A scheme** (`custom_paint_scheme`, `race`, `name` text id, `visible_in_customizer`, `class_badge_enabled`) has `paints/<slot>` for the 9 armour slots, plus empty weapon slots (`melee`, `ranged`, `exotic`, `offhand`, `spells`, `coreability`, `perk`, `equipment`, `ranged2`, `pistol`, `equipment2-4`; only the developer scheme `splorge` fills them: **weapons are never painted by the presets**), and `default_paint` for empty slots. A slot's paint = `primary_colour`, `secondary_colour`, `tertiary_colour` (paths to palette entries), `decals/decal` (shoulders only), `pattern_enabled`.
- **Colour to mask channel**: mask red = primary, green = secondary, blue = tertiary, alpha = dirt (Verified-local, M30). The customizer's three labels, in order, are "PRIMARY COLOR", "PATTERN COLOR", "DETAIL COLOR" (text 104280-104282, help 104289-104291): **interpretation: primary / secondary / tertiary**, with "PATTERN ON/OFF" toggling `pattern_enabled` (in the presets, slots with the pattern off have secondary = primary; shoulders, the pad with the badge, turn it on and use the accent colour as secondary).
- **Palette** (`custom_paint_colour`; `name` text id, `diffuse` and `specular` RGB 0..1, `colour_type`, `visible_in_customizer`): 60 visible colours in 8 groups, hidden 5 (developer colours and a duplicate of `ultramarines_blue`). Groups and counts: blue 9, cold shades 8 (greys, blacks, white), green 9, metallics 9, purple 6, red 9, warm shades 5 (bone, stone), yellow 5.
  Names, e.g.: Ultramarines Blue, Regal Blue, Ice Blue; Chaos Black, Skull White, Codex Grey; Dark Angels Green, Scorpion Green; Burnished Gold, Boltgun Metal, Brazen Brass; Blood Raven Red, Blazing Orange, Mechrite Red; Bad Moon Yellow, Bleached Bone (full list: `smtool baf-dump data/packages/custom_paint_colour.oppc --locale`).
- **Chapter/warband presets**: 47 visible schemes (31 Space Marine, 16 Chaos), e.g. Ultramarines, Blood Angels, Dark Angels, Space Wolves, Imperial Fists, Iron Hands, Raven Guard, Salamanders, White Scars, Black Templars; Black Legion, Alpha Legion, Word Bearers, Iron Warriors, Death Guard, Emperor's Children, Night Lords (hidden), World Eaters. The 33 hidden schemes are tests, AI, DLC-set schemes and `splorge`. The front end text "Select your colors based on an existing Space Marine Chapter" (104288) is this list.
- **Shoulder badges** (`custom_paint_decal`): a texture name, `decal_package`, `visible_in_customizer`, `race`. Visible: **89 Space Marine** (chapter badges with variants `_orig`, `_white`, `_black`, `_bw`, `_inverted`; `sm_custom_01..05_*` generic badges x black/white; one `sm_blank_badge` = none), **17 Chaos** (legion icons and `csm_chaos_star`). Hidden: 12 Space Marine (class identifiers `sm_identifier_assault/devastator/tactical`, `sm_rank_*`, test and AI badges) and 4 Chaos (all `blank`). Only `leftshoulder` (70 schemes) and `rightshoulder` (10) name a decal; the class identifier badges are not mapped to a class anywhere in the data (open since M30).
- **Eye-lens colour**: **not in the data.** No lens/visor entry in any BAF attribute file, locale or customizer string; the helmet material (`character_wargear`) has only the three paint channels, dirt, and the `LightPropMap` (whose blue channel marks ornaments, meaning unknown). Chaos "eyes" are effects at the `fx_lefteye_csm`/`fx_righteye_csm` markers and weapon glows. So a lens colour is a **new feature**, not original behaviour; if wanted, a fourth colour needs a mask region we have not identified (check the helmet `_pnt`/`_lp` textures by eye first).
- Other customizer items named in text without data: "Squad Number" (105971), "Shoulder Badge" (105970), "Pattern" (105982); the stored profile format is **not in the install**.

## 5. Front-end text and structure (names only, to mirror the flow)

Main menu entries: Campaign, Multiplayer, **Customizer** (103926, 105044, 111840: "Customize your loadouts", 111667; "Customize your Space Marine and Chaos loadouts", 111671), Network. Customizer flow from the text ids:

| Screen / label | Text id (examples) |
|---|---|
| Class tabs (Tactical, Heavy, Assault/Raptor), "Edit Class", "Loadout %1NUM%", "DEFAULTS" / "CUSTOM", "CUSTOM LOADOUT %1NUM%" | 111793-111798, 111832, 104268 |
| Loadout editor: "EDIT WARGEAR", "MODIFY LOADOUT(S)", "SELECT WEAPON", "SELECT EQUIPMENT", tabs "Weapons", "Equipment", "Perks" ("WEAPONS", "WEAPON PERKS", "CLASS PERKS", "CHOOSE PERK", "PERKS") | 104270, 105823, 105845-105850, 105824, 105830, 105831, 105822 |
| Prompts: "Select your primary/secondary weapon", "Select your pistol", "Select your close-combat weapon", "Select your special equipment", "Select a Perk"; slots "Primary Perk", "Secondary Perk", "Perk 3", "Special Equipment", "Equipment 2-4" | 104239-104253, 104222-104228 |
| Item card: "<description>\nRequired Level: %2LEVEL%" | 104298 |
| Appearance: "MODIFY APPEARANCE", "CHANGE APPEARANCE", "Appearance %1NUM%", "EDIT ARMOR", "SELECT COLORS", "Chapters", "Armor", "Armor Colors", "Armor Sets", "Shoulder Badge", "Squad Number" | 105986, 105851, 111827-111828, 105987-105988, 105967-105971, 105978 |
| Appearance submenu help: change chapter/warband, armour set or colours; change individual pieces; change colour and pattern of a piece; change badge | 105972-105975, 105989 |
| Armour piece editor: "CHAPTER", "ARMOR", "ARMOR SET", "PRIMARY COLOR", "PATTERN COLOR", "DETAIL COLOR", "DECAL", "CONFIGURATION", "Toggle Pattern", "PATTERN OFF/ON" | 104271-104283, 104287, 119749-119751 |
| Piece prompts: "Modify your helmet", "Modify left shoulder armor", ..., "Modify armor backpack" | 104229-104238 |
| Accolades / honors: "ACCOLADES", "ACCOLADE 1-3" (three slots), "HONORS" | 104273, 104284-104286, 106115 |
| Loading: "Loading Customizer. Please Wait." | 111823 |
| Piece-unlock captions: "Vambraces", "Greaves", "Breast Plate", "Backpack", "Shoulder Pads", "Helmet" | 120626-120647 |

Customizer camera framing per tab (`global` `character_customization/camera_settings`: `initial`, `weapons`, `equipment`, `perks`, `honors`; distance 4.1, horizontal offsets -0.7, 0.7, 0.7, -1.5, -2): the tab names above are the original's menu sections (initial, weapons, equipment, perks, honors).
`character_customization/usable_wargear` lists 15 singleplayer-name wargear (frag, bolt pistol, bolter, ...); it is a singleplayer-era list, not the multiplayer choices.

## 6. How the appearance reaches other players

**Nothing in the data.** The player blueprints set `wargear_ext/dynamic_wargear = True` (gear is added at spawn) and name a `default_custom_paint`; the mode scripts call `Entity_AddWargear` and pass a `loadout` table (`spawnmanager`, `Player`, `unit` constant pools); nothing mentions paint, armour or badge replication. So the original ships the player's chosen wargear (and presumably the paint) from the profile/online service to the host and on to the other clients at spawn. That wire format is **not in the install** and is ours to design (our server-authoritative join already carries class and loadout choices in the Hello).

## 7. Not found and open questions

1. **Default loadouts and the custom-loadout storage** (profile, online service): not in the install. Defaults stay our interpretation.
2. **Eye-lens colour**: not in the original (section 4). Owner's addition.
3. **Number of appearance slots**, the squad number, the accolade/honor selection (only the labels exist).
4. **Perks without a data consumer** (executable side): Rapid Deployment, Teleport Homer, Tactical Readout, Signum Link, Weapon Versatility, True Grit, plus the animation-timed Fast Stomp and Heavy Bolter Expertise. Their numbers (e.g. respawn time reduction) are unknown; original behaviour must come from the owner's knowledge.
5. **Three singleplayer dependencies** among the unlocks: Ultramarines Champion and Chosen of Nemeroth armour (campaign completion). The scope note says to name such dependencies; suggest treating them as unlocked or tied to a level.
6. **Pistol-slot Bolters** without the True Grit perk; the second (secondary) weapon slot of Weapon Versatility; whether Tactical can pick another sidearm or melee (the data says no).
7. **Backpack vs jump pack** for Assault/Raptor, and whether backpack weapons replace the armour piece's backpack mesh (the Devastator override suggests yes).
8. **DLC sets**: ownership gating unknown. **Spiked Armor** (havoc 3) has no challenge group.
9. **Class-wide unlocks for Chaos classes**: assumed equal to the Space Marine flags.
10. Perk values for the heal, burst, accuracy and heat entries are given as stored; the base weapon tuning they modify is in `WeaponTuning`/`spec/behavior.md` or the wargear files named above and was not re-derived here. Several perk files' display names do not match the effect of the string they apply (plasma gun "less heat" is a charge-time perk; melta "range increase" is a recharge perk): follow the data, not the names.
