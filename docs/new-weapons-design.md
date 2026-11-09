# New weapons: lightning claws, power spear, autocannon (design, 2026-11-10)

**Status:** design for the owner's approval. No product code yet. Branch `claude/new-weapons-design`.

**Owner's request (2026-11-10, verbatim):** "What cool new gameplay content can we add? Lightning claws for assault marine, Autocannon for devastator
class ranged weapon that functions like the one in the campaign, new equipments. Start work on lightning claws, power spear for assault marine, auto
cannon for devastator. Ask me questions to help with the design"

"New equipments" is out of this document. Section 9 lists the questions only the owner can answer. Each choice in sections 3 to 6 has a recommendation.

Labels used here:
- **Verified-local:** read from the install by archive and entry name.
- **Not found:** searched and absent.

Every number carries a kind:
- **data**: read from a named file.
- **data, scaled**: a data value converted by a stated ratio.
- **estimate**: ours, to check in a playtest.
- **ours**: a rule we choose.

---

## 1. What the install has

### 1.1 Autocannon: the weapon exists twice in the data, and the campaign version has its own animations

| Finding | Where (Verified-local) | What it means |
|---|---|---|
| Campaign carried autocannon | `wargear.oppc` / `auto_cannon` (the larger of two entries of that name, 43,389 bytes) | Slot `exotic`, weapon type `weapon_turret`, animation set `exotic_auto_cannon`, `disabled_slots` evade. Has rip-off sounds (`actn_snd_turret_ripoff`, `play_turret_pull`, `play_turret_break`), drop sounds and a melee strike (`ability_melee_light`, 25). Name 104441 "Autocannon". |
| Campaign turret autocannon | `wargear.oppc` / `auto_cannon` (second entry, 26,463 bytes) | Animation project `test_turret`, package `hbolter_turret`, animation set `auto_cannon`. 15 damage, 0.3 s, 1000 rounds, a projectile (`special_auto_cannon_chaos`, 500 m/s). Probably the gun as an enemy mans it; not established. |
| Campaign pickups | `ebps.oppc` / `pickup_auto_cannon`, `pickup_tripod_autocannon`, `turret_tripod_auto_cannon` | A tripod gun. Interaction states idle → manned (mount, `infinite_ammo` while mounted) → ripout (pick up the gun, `pickup_animation` `btn_exo_autocannon`, effect `turret_ripout`). They name the wargear `race_marine\weapons\2hand_ranged\exotic\auto_cannon`. Which of the two entries that path is was not established. |
| Campaign texts | locale 1038100, 1083490, 105677, 119883, 1847345 | "The Orks have taken the nearby landing zone and are defending it with an autocannon." "I will take care of the autocannon." "Pick up Autocannon". "Man Autocannon". "Autocannon! We need cover!" |
| MP autocannon | `wargear.oppc` / `csm_mp_exotic_auto_cannon` | Chaos only, no loyalist twin. Slot `ranged`, weapon type `weapon_2hand_range_light`, `load_type` `mp_weapon`, level 1. `visible_in_customizer` False, and no class lists it. Animation set `auto_cannon`, which no MP graph state uses. Name 104314 "Autocannon"; description 104315 says it has "enormously damaging shells" but "overheats easily". |
| Cut MP perks | locale 105900, 105902, 104318 | "Autocannon - Shot Timing Adjustments (DISABLED)", "Autocannon - Improved Rifling (DISABLED)", "Autocannon w/ Frags". The MP autocannon was planned and cut. |
| Gun model | `data/packages/turret_aa.oppc` | Parts `gun`, `ammobarrel`, `Recoiling_Barrel` (1,883 triangles). Locators `RIG_Prop_M_Chest`, `FX_muzzle`, `FX_Shellcasings`. About 2.8 m long, 2.1 m from the grip locator to the muzzle. **Our weapon-model reader loads it** (`smtool weapon-parts --model turret_aa/turret_aa`). Every autocannon wargear names this model. |
| Tripod model | `turret_tripod_base.oppc` | The tripod. The `turret_aa_base` and `test_turret_base` packages the pickup blueprint names are **not in the install**. |
| Campaign carry graph | `ebp_sets__player_set.oppc` / `havok_spacemarine__hkxx` (the campaign marine rig) / graph `SM_EXO_AutoCannon` | Nav = `SM_TB_NAV` (the heavy bolter's walk). Shoot = additive recoil `SM_RNG_CN_FireIdle_S_01` (0.5 s, `BRANCH_Refire` at 0.2). Evade = the heavy hop `SM_EVD_TB_Evade_*`. Melee = the graph `SM_TB_Attack_F_D_01`, which plays the heavy kick `SM_MEL_TB_YATK_Kick_*`: the same clip as the MP Devastator's kick. |
| Campaign pickup and drop | same rig / `SM_Base` states "Pickup - Exotic Auto Cannon", "Drop - Exotic Auto Cannon" | From the turret: `SM_TB_StowtoIdle_S_01_v2` (2.6 s). From the ground: `SM_NAV_Pick_up_gun_01` (1.3 s). Drop: `SM_TB_ThrowtoStow_S_01_v2` (0.933 s, on `BTN_EXO_Drop`). |
| Campaign turret clips | same rig | `SM_NAV_Turret_Aim*` (9 directions), `SM_NAV_Turret_Fire`, `SM_NAV_Turret_SyncOn` / `SyncOff`: a marine manning a turret. |
| The two marine skeletons | `graph-dump --bones` on both rigs | **Identical:** 50 bones, same names, same parents. So the campaign clips can play on the MP marine. Supported by evidence; playback untested. |
| The MP rig | `ebp_sets__mp_versus_set.oppc` / `havok_sm_base_mp__hkxx` / `SM_Base_MP` | Declares `BOOL_Exotic_Auto_Cannon` and `BTN_EXO_AutoCannon`, but no state uses them. None of the autocannon clips above is in the MP rig (613 clips). |
| Effects | `master_fx` | Campaign / loyalist: `autocannon_muzzle`, `autocannon_trail`, `autocannon_dmg*`, the hit container `weapon_hits\space_marines\ranged_weapons\autocannon`, `heavy_bolter_tracer` (the campaign file's tracer), `shell_casing`, `turret_ripout`. Chaos: `chaos_autocannon_muzzle`, `_tracer`, `_smoke`, `_trail`, `_dmg`, the hit container `weapon_hits\chaos\ranged_weapons\autocannon`. |
| Sounds | MP banks (`In_Game_Default`) | `play_autocannon_turret_fire` (the campaign file's) and `play_ch_autocannon_fire` (the MP file's) both resolve. Also `play_turret_pull`, `play_turret_break`, `play_turret_drop_impact`. `play_sm_autocannon_pull` is not in the loaded banks. |
| HUD | `Hud.gfx` | Reticle `autocannon_mc` / `autocannon_active_mc`, ammo display `ammo_autocannon_mc` (already noted in docs/discovery.md). |
| Cameras | spec "Camera sets" | `exploration_heavyweapon` (the MP file's exploration camera, not the `_mp` set), aim `aim_sm_heavy_weapon_mp`. The campaign uses `aim_autocannon`. |
| Spread | `climb_sway_scatter\csm_mp_exotic_auto_cannon`, `_accuracy_perk`, `aim_csm_mp_exotic_auto_cannon` (named by the MP file) | Scatter sets for the MP gun. |

**The numbers.** These were read through the simulation's own reader (`WeaponTuning.Read`, default entries) with a scratch script outside the repository. The MP heavy bolter is listed for scale.

| | MP file `csm_mp_exotic_auto_cannon` | Campaign carried `auto_cannon` | MP heavy bolter `sm_mp_exotic_heavy_bolter` | Campaign heavy bolter `heavy_bolter` |
|---|---|---|---|---|
| Trigger | semi auto, **2-round burst**, 0.2 s apart | full auto | full auto | full auto |
| Damage per round | 50 | 100 | 35 | 75 |
| Shot cooldown | 0.4 s | 0.22 s | 0.233 s (braced 0.1) | 0.1 s |
| Clip / reserve | 120 / 0 (ammo pickups refill the clip) | 400 / 0 (low ammo at 40) | 400 / 0 | 300 / 0 |
| Range, fall-off | 150 m; ×0.75 from 45 to 70 m | 150 m; none | 250 m; ×0.8 from 50 to 75 m | 150 m; none |
| Head / body (shield head / body) | 1 / 1 (1 / 1) | 1.5 / 1 (1 / 1) | 1 / 1 (0.7 / 0.7) | 1.5 / 1 |
| Heat | 1 per round, overheat 15, high 9, cools 4.5/s after 0.41 s | 3 per round, overheat 100, high 50, cools 50/s after 0.5 s | 3 per round, overheat 100, cools 25/s after 0.3 s | 1.25 per round, overheat 100 |
| Splash | none | radius 4 m: ×1 up to 2 m, ×0.1 at 4 m, 100 damage, line of sight | none | none |
| Delivery | hit-scan | hit-scan | hit-scan | hit-scan |
| Dismemberment | `heavy_bolter` | `explosive_heavy` | — | — |

Scale. The campaign and MP heavy bolters show how the original converted campaign numbers to MP: damage ×0.467 (35/75) and fire rate ÷2.33 (0.1 → 0.233 s). Converted that way, the campaign autocannon's 100 is 47 per round, close to the MP file's 50. So the MP file's damage is the campaign gun's at MP scale (an inference, kind data, scaled).

### 1.2 Lightning claws: not in the install

**Not found** as a weapon:
- No wargear, model, graph, clip, effect, HUD sprite or text names a lightning claw, falchion, gauntlet or twin claws.
- Searched: wargear, every package and chunk name, `baf-dump` of `data/packages`, both marine rigs' graphs and clips, `fx-dump`, `gfx-dump`, and the English locale for claw, klaw, talon, lightning, falchion, gauntlet, glaive, fist, dual, twin and offhand.

What can be reused (all Verified-local):

| Asset | Where | Use |
|---|---|---|
| Power sword move set | `MP_SM_MEL_PowerSword` (MP rig), `sm_mp_melee_powersword` | A one-handed, right-hand set: X1..X4, Y, X,Y, X,X,Y, X,X,X,Y, the Y,Y stomp. The best fit for claw slashes. |
| Combat knife and chainsword sets | `MP_SM_MEL_CombatKnife`, `MP_SM_MEL_ChainSword` | Alternatives: stabbing knife strikes, chainsword cuts. |
| Extra campaign melee clips | campaign rig: `SM_MEL_CK_XATK_HVY_*`, `SM_MEL_CS_YATK_Combo5_*`, the 180-degree attacks `*_DIR180_*`, `SM_MEL_FURY_ACTIVATE` | Not in the MP rig. Same skeleton, so usable later for a claw finisher. |
| Left-hand attachment point | `rig_prop_lhand`, used by `bolter_offhand`, `storm_bolter_offhand`, `choppa_lefthanded` | Where a second claw hangs. Only the Ork Boy Pro pairs two melee weapons, on the Ork rig. **No marine graph has a left-hand strike.** |
| Claw-like effects | `power_claw_strike_00` (power axe hit container), `power_blade_dmg_00`, `amb_chaos_lightning_whip`, `amb_chaos_lightning_wrapping` | The power-field hit and a lightning look around the blades. |
| Lightning sounds | `play_capacitor_lightning` (resolves) | The Librarian's pass already uses install lightning events, decoded at run time. |
| Power klaw sounds | `play_power_klaw_antic`, `_swing`, `_smash` | Warboss klaw: heavier, Ork-flavoured. |
| Unused text | locale 103270 "Power Fist", 103311 | Not referenced by any wargear. |

### 1.3 Power spear: not in the install

**Not found:**
- No wargear, model, clip, effect, HUD sprite or text names a spear, lance, halberd, glaive, pike or trident. The only "lance" text is "Thunderhawk Lance of Jove".
- Same searches as the claws, plus `*pole*`, `*staff*` and `*spear*` clips.

What can be reused (Verified-local):

| Asset | Where | Use |
|---|---|---|
| Force staff model | `forcestaff_basic.oppc`. **Loads with our reader:** one part, 1,360 triangles, about 2.76 m long, locators `RIG_R_Weapon` and `fx_swing` | A marine-scale pole for the spear shaft. Its only user is `sm_wp1_thunder_hammer_fake`, which has no abilities. Locale 103272 "Force Staff" and 103313 "Two-handed Force Staff." are unused. |
| Two-handed move set | `MP_SM_MEL_ThunderHammer` (MP rig) with `SM_TH_NAV`, `SM_TH_NuSprint`, the 21 `SM_2HM_NAV_TH_*` clips | **The only two-handed melee set** (both hands on a shaft): walk, sprint, three swings, the interrupt, two sweeps, the slam. |
| Other pole models | `weirdboy_staff.oppc`, `nob_waaagh_banner.oppc` | Ork-styled; not for a marine. |
| Librarian | `ebps` / `librarian` | No weapon at all (armour, backpack, revive). No staff graph exists. |

No thrust (stab) animation exists for a two-handed weapon. The knife's stabs are one-handed.

---

## 2. How a weapon is built here (the parts a new one touches)

- **Simulation numbers.**
  - A gun is a `WeaponSpec` in `SimConfig.Weapons`, read from its wargear by `WeaponTuning.Read`.
  - A melee weapon is a `MeleeCombo` (light steps, heavy steps, charge) in a class's `MeleeChoices`. It is read by `MeleeTuning.From` from a graph map (`MpClasses.MeleeMaps`: graph and sequencers) plus the wargear's abilities (damage).
  - Classes hold `PrimaryChoices`, `PistolChoices`, `MeleeChoices` and `EquipmentChoices`.
- **Wire.** The Hello carries choices as indices into the class's lists. They are resolved by wargear name (`LoadoutIndices`), sent as U8, and the profile stores names. Appending an item at the end of a list changes no existing index and no wire format.
- **Config hash.** It covers every weapon and combo, so appended weapons change the hash only of the profiles that carry them.
- **The added-content load point.** It already exists: `GameplayTuning.ExtrasLoad`, whose dispatcher `src/SmContent/Extras/GameplayTuning.Extras.cs` runs Musou's `MusouLoadout.Prepare`. Prepare copies the class choice lists. A weapon appended in the same dispatcher just before Prepare therefore reaches Musou's drops (`MusouDropRules` picks from the class's choices), reward cards and extra slots 3 to 5 without touching Musou code.
- **Customizer.**
  - `PerkCatalog` lists each class's items from the wargear's `ui_data`; Musou's Standard Marines page reads the same catalogue.
  - A new item needs a catalogue entry (name, slot, title, description).
  - The Hello index comes from the simulation's list by name, so the two cannot drift.
- **Client presentation.**
  - The weapon model comes from the wargear's `visual_data` (`WeaponModels`).
  - Animation families are chosen by wargear-name rules: `HipFireAnimations.FamilyOf` ("heavy_bolter" → heavy), `LocomotionAnimations.SprintFamilyOf`, `ReloadAnimations`, `FallAnimations` and the flinch rules.
  - The melee clips come from `MpClasses.MeleeMapFor(wargear)`.
  - Hands: `WeaponHands` (a melee weapon is in hand only while a strike is shown; two-handed items move to the chest prop).
- **Heavy weapons.**
  - Brace (heavy bolter only, from its `ability_ranged_braced`) and vent (plasma) live in `Brace.cs` / `Vent.cs`.
  - A Devastator primary is one clip for a life (400 for the heavy bolter), refilled by ammo pickups.
- **Assault jump pack.** Dash attack, ground pound and air slash come from the pack, not the weapon. The only weapon-specific rules are the hammer's (`Loadouts.WieldsHammer`: no pound delay, hop swing, steered slam).
- **Hit-scan splash is not simulated.** `WeaponTuning.Read` reads a projectile's or a cone's blasts, not a hit-scan shot's `hit_sim` `area_of_effect`. The campaign autocannon's splash needs a new rule.

---

## 3. Where the weapons live

None of the three was offered to an SM1 multiplayer player. The claws and the spear have no data at all. The autocannon is cut SM1 data: invisible in the customizer, no class lists it, no MP animation state, its perks marked DISABLED.

So all three are **added content** (docs/added-content.md: "a whole system, faction, mode or mechanic SM1 multiplayer did not have, removable in one piece"). They go in `Extras/Arsenal/` folders, with namespaces `<Project>.Extras.Arsenal` and `Extras*` hooks, and are off in a faithful build.

The autocannon is a special case. The weapon is added content, but its numbers stay **data** wherever the files give them (kind `data`, source the wargear file). It is recorded so in `docs/changes-from-original.md` section 0. It is not an owner's change to something SM1 had, so it is switched, not just tagged.

| Option | Gate | What changes |
|---|---|---|
| **A. Musou only (recommended first)** | `ContentExtras.Musou` (the Musou profile takes no other flags) | Musou players get the weapons in the loadout, drops, rewards and the Musou customizer. Annihilation and Seize Ground are untouched, including the extended profile's config hash. Code in `Extras/Arsenal/`, read only when the Musou flag is set. |
| B. Musou and Classic PvP | a new flag `Arsenal` (next free bit, 16), in `extended`; the Musou code also accepts `Musou` | Classic extended matches offer the weapons on both sides. Needs Chaos twins (section 9, question 2). The extended config hash changes, so builds refuse each other across the change, as with any balance change. The VM server needs the new build. Every Classic number then needs balance against the six classes. |

The recommendation is A first, then B if the owner wants them in PvP after a Musou playtest. Option A costs no Classic risk. The code is the same either way: under B only the gate and the Chaos twins are added.

Protocol:
- No protocol or extras-wire change is needed for the loadout weapons.
- Musou's rules version goes up with any Musou number.
- The tripod pickups (milestone W4) need an interaction message only if mounting is added. That is a Musou wire addition, bumping the extras wire version.

---

## 4. Autocannon (Devastator primary)

**Role.** A heavy, slow, hard-hitting gun that kills groups (splash) and overheats quickly. It sits between the heavy bolter (sustained fire, brace) and the lascannon (single big shots).

**What "like the one in the campaign" means in the data.**
- In the campaign the Orks hold a landing zone with a tripod autocannon. Titus can man it (infinite ammo, turret aim clips) or rip the gun off and carry it.
- Carried, it fires full auto from the hip with a cannon recoil. It carries 400 rounds with no reserve, so it is dropped when empty (`SM_TB_ThrowtoStow_S_01_v2`). It walks like the heavy bolter.

Two handling choices follow (question 8).

| Choice | What the Devastator does |
|---|---|
| **W1: a loadout primary (recommended now)** | Picked in the loadout like the heavy bolter, lascannon and plasma cannon. Always carried; one clip a life, refilled by ammo. |
| W4: campaign-style pickups (later, Musou only) | Tripod autocannons on Musou maps. Use to man (infinite ammo, turret clips). Hold Use to rip off and carry until empty, then drop. Any class can do it, as in the campaign. Needs map placement, an interaction and the pickup and drop clips. |

**Controls (W1).**
- Fire (LMB) shoots. Aim (RMB) uses `aim_sm_heavy_weapon_mp`.
- Reload (R) does nothing: there is no reload ability and no brace in either file (ours: no brace; question 10).
- Melee is the Devastator's kick and stomp; the campaign's autocannon strike plays the same kick clip. Hop, sprint and switch follow the heavy bolter's rules. The campaign disables the evade while carrying; the MP Devastator keeps its hop (ours).

**Numbers: two sets, one per mode (question 9).**

| Parameter | Musou (campaign handling; recommended) | Classic, if chosen (MP data) | Kind / source |
|---|---|---|---|
| Trigger | full auto | semi auto, 2-round burst 0.2 s apart | data: `auto_cannon` / `csm_mp_exotic_auto_cannon` |
| Damage per round | 50 | 50 | data: MP file (equals the campaign's 100 scaled ×0.467) |
| Shot cooldown | 0.22 s | 0.4 s per burst | data: campaign / MP file |
| Clip | 400 | 120 | data: campaign / MP file |
| Head multiplier | 1.5 | 1 | data |
| Fall-off | none | ×0.75 from 45 to 70 m | data |
| Heat | 3 per round of 100, cools 50/s after 0.5 s: about 33 rounds, 7.3 s of fire to overheat | 1 per round of 15, cools 4.5/s after 0.41 s: 15 rounds, about 3 s | data |
| Splash | radius 4 m, ×1 to 2 m, ×0.1 at 4 m, line of sight, 50 at the centre | none | data shape; the damage scaled ×0.5 like the round (data, scaled) |
| Scatter | none (Musou has no bullet spread) | the MP file's sets | data |

For scale (estimate, body shots, no splash), Musou is about 227 DPS for 7 s; the heavy bolter is 150 from the hip and 350 braced.

**Presentation.**

| Piece | Recommendation | Source |
|---|---|---|
| Model | `turret_aa`, held at `rig_prop_m_chest` like the heavy bolter, stowed at `mkr_stow_bolter` | data (every autocannon wargear) |
| Walk, sprint, hop, fall | the heavy bolter's families (`SM_TB_NAV`, heavy sprint, `SM_EVD_TB_*`) | data: the campaign graph's nav is `SM_TB_NAV` |
| Shot | additive `SM_RNG_CN_FireIdle_S_01` from the campaign rig (same skeleton); fallback the heavy bolter's fire | data (campaign rig) |
| Loyalist effects and sound | `autocannon_muzzle`, `heavy_bolter_tracer`, `shell_casing`, the space-marine autocannon hit container, `play_autocannon_turret_fire` | data: campaign file |
| Chaos effects and sound (Classic) | `chaos_autocannon_*`, `play_ch_autocannon_fire` | data: MP file |
| HUD | reticle `autocannon_mc`, ammo `ammo_autocannon_mc`, the round count as the heavy bolter's | data (`Hud.gfx`); the count rule is the owner's heavy-bolter rule |
| Name, description | 104314 "Autocannon", 104315 | data |
| Loyalist name | `sm_mp_exotic_auto_cannon` (ours; the twin of the Chaos file, built in code, never written to disk) | ours |

**Missing:**
- A loyalist MP file.
- The tripod's own animation graph and base packages, needed for W4 only; the tripod base model exists.

---

## 5. Lightning claws (Assault melee choice)

**Role.** A fast, shredding close-combat weapon: quicker strings than the sword, lighter hits, more armour damage.

**One claw or a pair (question 3).** Every SM1 melee set strikes with the right hand only, and the Assault keeps its bolt pistol with every melee weapon.

| Choice | Look | Cost |
|---|---|---|
| **Pair, pistol kept (recommended)** | A claw on each gauntlet. The blades are out only during a strike and fold back into the gauntlet otherwise, so the right hand can hold the pistol (ours; SM1 also shows the melee weapon only for a strike). The left claw rides along: the data has no left-hand strikes. | One more model attachment (`rig_prop_lhand`). |
| Single claw (right) and pistol | The sword's place exactly. Simplest. | Less iconic. |
| Pair, no pistol | Both claws always out; Fire does nothing, or fires nothing. | Removes the Assault's gun. Not recommended. |

**Move set (question 4).** Recommended: the power sword's graph, timings and chain unchanged, so all the timing data stays as read:
- X1..X4.
- The heavy strikes Y, X,Y, X,X,Y, X,X,X,Y and the Y,Y stomp.
- The sword's charge (bull rush).
- The pack's dash attack, ground pound and air slash, as the sword's.

The claws differ in numbers. The alternative is a faster string: the same clips played 1.25× faster, with the step timings divided by 1.25 (ours). That is checked first in one still and one clip, because the client must play the clips at the new speed in step with the simulation.

| Parameter | Recommended | Kind / source |
|---|---|---|
| X1..X4 damage | 50 / 55 / 55 / 85 (the pair's X4 is both claws); secondary 25 / 25 / 35 / 45 | estimate (245 a string against the sword's 290 from 65 / 70 / 73 / 82, made up by the armour damage below) |
| Armour-shield damage per light hit | 100 | estimate (the axe has 250; the sword none) |
| Heavy strikes, stomp, charge | the power sword's | data: `sm_mp_melee_powersword`, `MP_SM_MEL_PowerSword` |
| Perks | none of the sword's heal-on-hit perk at first | ours (question 13) |

**Presentation.**
- Model: none exists (question 11). Recommended: our own mesh built in code. That is a gauntlet plate and four curved blades per hand, steel with a blue power-field glow.
  - Parented to `RIG_R_Hand` / `RIG_L_Hand`.
  - The blades scale out over 0.1 s when a strike starts and back after it (ours).
  - Fallback placeholder: three combat-knife blades per hand, from the install at run time.
- Effects: the power sword's field effects on the blades, `power_claw_strike_00` and `power_blade_dmg_00` on a hit, and a thin lightning crackle (`amb_chaos_lightning_wrapping`, recoloured blue) while the blades are out.
- Sounds: the power sword's swing and hit events, with `play_capacitor_lightning` layered on a hit (install events; no downloads, as the owner asked for the Librarian).
- Name: "Lightning Claws" (ours, English).
- Chaos twin (Classic only): the Raptor's (40k Raptors carry claws), same numbers (race parity).

**Missing:** the model, any left-hand strike, any claw clip.

---

## 6. Power spear (Assault melee choice)

**Role (question 5).** Reach and the charge: a two-handed weapon that hits from farther than the sword and is deadliest on a charge or a dive. It is slower than the claws and faster than the hammer.

**Grip and tempo (question 6).**

| Choice | Animation | Feel |
|---|---|---|
| **Two-handed on the hammer's set, played faster (recommended)** | `MP_SM_MEL_ThunderHammer` and `SM_TH_NAV` / sprint at 1.4× speed (the step timings ÷1.4; ours) | Wide two-handed swings and sweeps; both hands on the shaft. The only two-handed set in the data. |
| Two-handed on the hammer's set at its own speed | the same at 1.0× | A heavy halberd; very close to the hammer. |
| One-handed on the sword's set | `MP_SM_MEL_PowerSword` | Quick, but a 2.5 m pole in one hand clips through the body. Not recommended. |

No thrust clip exists. A real stab needs a new animation (ours, procedural, later) or one supplied by the owner.

| Parameter | Recommended | Kind / source |
|---|---|---|
| X1..X3 lengths | 1.905 / 1.667 / 1.667 s; hits 0.431 / 0.405 / 0.500 s | data, scaled (the hammer's ÷1.4) |
| X1..X3 damage | 120 / 120 / 140; secondary 60 | estimate (the hammer's 200 ×0.6–0.7) |
| Reach | light hits land up to 1 m farther than the hammer's | estimate |
| Heavy strikes | the hammer's interrupt and sweeps (stun), sped up the same | data, scaled |
| Y,Y finisher | the hammer's slam at 1.4×; its blast radius 5 m, 75 + 75 | estimate (the hammer's 7 m, 100 + 125) |
| Charge | the bull rush at ×1.5 damage (112 + 38), stagger | estimate (the data's 75 + 25, stun) |
| Pistol | kept, as the hammer | data (the hammer keeps it) |
| Jump pack | the hammer's rules (hop swing, no pound delay) | ours (question 7) |

**Presentation.**
- Model: none exists (question 11). Recommended: the install's force staff as the shaft (loaded at run time), with a blade head built in code (ours), held at the chest prop like the hammer.
- Effects: the hammer's power-field effects along the blade.
- Sounds: the power axe's swing and hit events.
- Name: "Power Spear" (ours).

**Missing:** the model, any thrust animation, any spear sound.

---

## 7. Milestones and file ownership

| Milestone | Owner | Files (one writer each) | Done when |
|---|---|---|---|
| **W0: the arsenal base** (first; small) | one implementer | the gate; `src/SmContent/Extras/Arsenal/Arsenal.cs` (the registry: three entries, each a stub that returns nothing); one line in the dispatcher `src/SmContent/Extras/GameplayTuning.Extras.cs` (before Musou's Prepare); the core hooks below; `ExtrasBoundaryTests` pins; the docs' section headings | A faithful and an extended config hash byte for byte as before. A Musou config unchanged while the stubs return nothing. Both builds 0 errors; the boundary and isolation tests pass. |
| **W1: autocannon** | implementer A | `src/SmContent/Extras/Arsenal/Autocannon.cs` (the loyalist twin from the Chaos file, the Musou numbers), a new file in `src/SmSim/Extras/Musou/` for splash on hit-scan shots against Orks, `client/Extras/Arsenal/Autocannon/*`, `tests/*/Extras/Arsenal/Autocannon*`. Calling the splash file needs one line in Musou's shot code, which other Musou sessions also edit: the lead sequences that line. | Picked in the loadout, fired, overheated, splash on Orks; player and third-person stills; targeted tests. |
| **W2: lightning claws** | implementer B | `src/SmContent/Extras/Arsenal/LightningClaws.cs`, `client/Extras/Arsenal/Claws/*`, `tests/*/Extras/Arsenal/Claws*` | A string, the heavies, a charge and a dive with claws; blades out and in; stills. |
| **W3: power spear** | implementer C | `src/SmContent/Extras/Arsenal/PowerSpear.cs`, `client/Extras/Arsenal/Spear/*`, `tests/*/Extras/Arsenal/Spear*` | As W2, with the speed-up checked in a clip against the simulation's hits. |
| W4 (later, Musou): tripod autocannons | after W1 | Musou map placement, interaction, carried state, campaign pickup / drop / turret clips | Man, rip off, carry, drop. |

**Core hooks W0 adds.** Each is a partial `Extras*` method, pinned and marked `// EXTRAS-HOOK`. The names are proposals.
1. `MpClasses.ExtrasMeleeMap(string wargear, ref WeaponMap?)`: the graph map of an added melee name. The client's `MeleeMapFor` uses it too.
2. `PerkCatalog.ExtrasItems(ref list)`: the customizer rows.
3. Client `WeaponModels` / `WeaponRig`: `ExtrasWeaponModel(string wargear, ref model)`, a model built in code or loaded from another package.
4. One animation-family alias, `ExtrasAnimationAlias(ref string wargear)`. It maps an added name to the family it borrows (autocannon → heavy bolter, claws → power sword, spear → thunder hammer). It is applied in the name rules of `HipFireAnimations`, `LocomotionAnimations`, `ReloadAnimations`, `FallAnimations` and the flinch rule, so W1 to W3 touch none of them.
5. If melee speed-ups are approved: a clip-speed factor the simulation's steps and the client's clip playback both read. It works like the Fast Stomp perk's clip speed (`MeleeTuning.ReadSpeedUpgrades`); W0 checks how the client applies that today.

Under option B, add the Chaos twins and a hit-scan splash rule in the core shot path (a hook in `World`). Under A, the splash lives in Musou's own combat code.

**Shared files.** `spec/behavior.md`, `docs/changes-from-original.md`, `docs/milestones.md`, `docs/commands.md` and the generated `docs/refine-register.md`:
- Each milestone writes only under its own heading, which W0 creates.
- The lead regenerates the register at integration.

W1, W2 and W3 share no source file after W0 and can run in parallel.

Process:
- Every milestone keeps Musou changes away from Annihilation and Seize Ground (MusouIsolation tests).
- Every milestone needs a UI screenshot gate for new visuals: player view and third person, in the media previews folder.
- Every milestone gets an independent review.

---

## 8. Risks and checks

| Risk | Check |
|---|---|
| Campaign clips on the MP marine (the autocannon recoil, later the pickup) | The skeletons are identical (checked). W1 plays the recoil in a still before relying on it; fallback the heavy bolter's fire. |
| `turret_aa` is 2.8 m long and was made for a tripod and a carry | W1's first still: the grip at `RIG_Prop_M_Chest`, clipping, camera. |
| Speeding up melee clips desynchronises sim and client | W0 checks the Fast Stomp path; W2 / W3 compare the simulated hit tick with the clip's contact in a clip. |
| Claw blades that fold away are ours | Shown to the owner in the first still. |
| No left-hand strikes or thrusts in the data | Stated as missing; new animation only on the owner's word. |
| Musou damage scale | Musou applies its own combat rules to every weapon; the new numbers are set against the Musou Orks in a playtest, not only against marines. |
| Musou rules version and the VM | Musou runs on the player's PC; a Classic option B ship needs the VM update. |

---

## 9. Questions only the owner can answer

1. **Where:** Musou Mode only for now, or Classic PvP too (as added content: on in the extended profile, off in faithful)? Recommended: Musou first, Classic after a playtest.
2. If Classic: should Chaos get them too? The Raptor would get claws, the Havoc the data's own Chaos autocannon, and someone the spear. Or loyalists only?
3. **Claws, one or two:** a pair with the bolt pistol kept, the blades out only for strikes (recommended)? Or one claw and the pistol, or a pair without the pistol?
4. **Claw tempo:** the power sword's swings at their own speed with different numbers (lighter hits, armour damage; recommended)? Or the same swings played about 25 % faster?
5. **Spear's job:** what should it be best at: reach, the charge, the jump-pack dive, or something else? Recommended: reach and the charge.
6. **Spear's grip and speed:** two-handed on the thunder hammer's swings played about 40 % faster (recommended)? Or at the hammer's own speed, or one-handed? A real thrust (stab) needs a new animation; do you want one later?
7. Should the spear follow the hammer's jump-pack rules (swing during a hop, no pound delay)? Recommended: yes.
8. **Autocannon handling:** a Devastator loadout primary carried like the heavy bolter (recommended now)? Or the campaign's pickup (man a tripod gun, rip it off, carry it until empty, then drop it)? Or both, with tripods later on Musou maps?
9. **Autocannon fire:** full auto at the campaign's pace with splash (recommended for Musou)? Or the MP file's two-round bursts, its 120 rounds and its quick overheat (recommended for Classic, if Classic)?
10. Should the autocannon brace on R like the heavy bolter? The data gives it no brace; recommended: no.
11. **Models:** the install has no claw or spear model. Do you have models or reference images to give us? Otherwise we build simple ones in code: claws with four blades and a blue glow, and a spear from the install's force staff with a blade head we make.
12. How should the claws' lightning look and sound? A thin blue crackle on the blades and a lightning snap on hits, from the install's sounds (recommended), or quieter?
13. Should the power sword's perks (heal on hit) also count for the claws or the spear? Recommended: no, at first.
14. Any number you remember from other 40k games that the claws, spear or autocannon should match (damage feel, speed)? It would be labelled as your recollection and checked in a playtest.
