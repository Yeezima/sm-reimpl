# New weapons and equipment for Musou Mode: lightning claw, power spear, autocannon, demolition charge (design, 2026-11-10)

**Status:** approved by the owner as written (2026-11-10); implementer briefs in section 11. No product code yet. Branch `claude/new-weapons-design`.

**Owner's request (2026-11-10, verbatim):** "What cool new gameplay content can we add? Lightning claws for assault marine, Autocannon for devastator
class ranged weapon that functions like the one in the campaign, new equipments. Start work on lightning claws, power spear for assault marine, auto
cannon for devastator. Ask me questions to help with the design"

## Decided by the owner (2026-11-10, his answers to the first questions, through the lead)

| # | Question | His answer | What it fixes in this design |
|---|---|---|---|
| D1 | Where | "Musou Mode only" | Only Musou's customizer pages and Musou battles. Annihilation and Seize Ground stay untouched: no Classic wargear, loadout or rule changes. The code lives inside the Musou feature (section 3). |
| D2 | Lightning claws | "One claw plus the pistol" | One claw in the Assault Marine's melee slot; the bolt pistol stays (section 5). |
| D3 | Power spear | "Impaling dive plus wide sweeping swings" (his own answer, combining the two options) | The jump-pack dive lands with an impaling strike; the ground attacks are wide, sweeping polearm swings (section 6). |
| D4 | Equipment | "Demolition charge" | The install's unused `eq_demolition_charge` (remote detonators) becomes Musou equipment. Not the auspex (section 7). |
| D5 | Autocannon | no further question | Based on the install's own `exotic_auto_cannon` data, and the campaign's where that differs, for the Musou Devastator (section 4). |

**The demolition charge, round 2 (2026-11-10, his answers through the lead):**

| # | Question | His answer | What it fixes |
|---|---|---|---|
| D6 | Trigger | "Short fuse on its own" | It goes off by itself a few seconds after it lands; no second press. The fuse length is ours (an estimate). |
| D7 | Placing | "Thrown, sticks where it hits" | Thrown like a grenade. It sticks to walls, the floor, contraptions or an Ork (Bosses too) and moves with what it is stuck to. |
| D8 | Blast | "Big blast, throws Orks" | Wider and stronger than a frag: Orks around it fly into the air, Bosses take heavy damage. Compared with the install's own numbers in section 7. |
| D9 | Count | "1, back every 30 s" | One charge, a new one every 30 s in Musou (grenades come back every 15 s); Favor of the Armory gives 2, as the game file has it. |

**Rounds 3 to 5 (2026-11-10, his answers through the lead):**

| # | Question | His answer | What it fixes |
|---|---|---|---|
| D10 | Autocannon handling | "Loadout weapon only" | A Musou Devastator loadout primary; no tripod pickups, now or later (section 4). |
| D11 | Autocannon brace | default: no | No brace; R does nothing. |
| D12 | Autocannon feel | "the autocannon should fire slower but do more damage than the heavy bolter and have 2x the HE explosion size of the bolter and bolt pistol" | 0.4 s between rounds, 50 a round, a 2.5 m bolt-style blast at every Ork struck (replaces the campaign's 4 m splash). |
| D13 | Claw tempo | "About 25% faster" | The sword's swings at 1.25×, lighter hits (section 5). |
| D14 | Spear tempo and reach | "slower than hammer but much wider reach" | The hammer's swings at 0.85×, a 12 m, 240° sweep, at most 12 Orks (section 6). |
| D15 | Models | "I'll send reference images" (three, described by the lead) | The claw (piston-backed gauntlet, four curved steel talons) and the spear (black shaft, bronze fitting, blue single-edged blade), built in code from them. |
| D16 | Claw's edge | "No extra edge" | No bonus against big Orks, no arcs. |
| D17 | Spear and the jump pack | default: yes | The hammer's jump-pack rules. |
| D18 | Impaling dive | default: the proposed rule | 600 to the enemy nearest the landing within 2 m. |
| D19 | Charge blast and fuse | "600 damage at the centre, 40m reach, bosses take 2x, up to 80 orks thrown flying, survivors knocked back up to 10m, 5s fuse after it sticks" | Section 7's centre blast and ring are fitted to these. |
| D20 | Charge carriers | "Every class + Librarian" | Every Musou class and the Librarian (his list with his session). |
| D21 | Charge name and model | "Remote Detonators" on the install's melta bomb model | The install's own name, though it runs on a fuse. |
| D22 | Ring spots | "Spots stack" | An Ork caught by several ring explosions takes each one's damage. |
| D23 | Centre blast | "Centre blast, then ring" | The charge goes off at its centre first, then the ring rolls. |
| D24 | Master-Crafted Wargear | default: the data's ratio | Damage ×1.25, reach ×1.5 on the owner's numbers. |
| — | Claw and spear perks, the claw's crackle | defaults | No heal-on-hit perks at first; a faint blue crackle on strikes with an install lightning snap on hits. |

Section 10 lists what is left: nothing for approval, only the estimates to check in a playtest.

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
| Extra campaign melee clips | campaign rig: `SM_MEL_CK_XATK_HVY_*`, `SM_MEL_CS_YATK_Combo5_*`, `SM_MEL_FURY_ACTIVATE` | Not in the MP rig. Same skeleton, so usable later for a claw finisher. |
| The hands | spec "In-hand mount": pistols on `rig_prop_lhand` (left hand), one-handed melee on `rig_prop_rhand` (right hand) | **One claw on the right hand and the pistol in the left is exactly SM1's layout** for the Assault's sword and pistol. |
| Claw-like effects | `power_claw_strike_00` (power axe hit container), `power_blade_dmg_00`, `amb_chaos_lightning_whip`, `amb_chaos_lightning_wrapping` | The power-field hit and a lightning look around the blades. |
| Lightning sounds | `play_capacitor_lightning` (resolves) | The Librarian's pass already uses install lightning events, decoded at run time. |
| Power klaw sounds | `play_power_klaw_antic`, `_swing`, `_smash` | Warboss klaw: heavier, Ork-flavoured. |
| Unused text | locale 103270 "Power Fist", 103311 | Not referenced by any wargear. |

### 1.3 Power spear: not in the install; its dive and sweeps can borrow the hammer's

**Not found:**
- No wargear, model, clip, effect, HUD sprite or text names a spear, lance, halberd, glaive, pike or trident. The only "lance" text is "Thunderhawk Lance of Jove".
- Same searches as the claws, plus `*pole*`, `*staff*` and `*spear*` clips.
- No thrust (stab) clip exists for a two-handed weapon. The knife's stabs are one-handed.

What can be reused (Verified-local):

| Asset | Where | Use |
|---|---|---|
| Force staff model | `forcestaff_basic.oppc`. **Loads with our reader:** one part, 1,360 triangles, about 2.76 m long, locators `RIG_R_Weapon` and `fx_swing` | A marine-scale pole for the spear shaft. Its only user is `sm_wp1_thunder_hammer_fake`, which has no abilities. Locale 103272 "Force Staff" and 103313 "Two-handed Force Staff." are unused. |
| Two-handed move set | `MP_SM_MEL_ThunderHammer` (MP rig) with `SM_TH_NAV`, `SM_TH_NuSprint`, the 21 `SM_2HM_NAV_TH_*` clips | **The only two-handed melee set** (both hands on a shaft): walk, sprint, three light swings, the interrupt, the slam. Its two heavy strikes X,Y and X,X,Y are the graph's "Sweeping Attack" (`SequencerGenerator18`, `23`). |
| The hammer's wide swings | MP rig clips `SM_MEL_TH_XATK_Combo2`, `_Combo3`, `SM_MEL_TH_HeavyATK01` / `02`, `SM_MEL_TH_XATK_180_MP` (3 s), `SM_MEL_TH_XATK_180_combo2`, `SM_MEL_TH_YATK_Displace2` | Big horizontal swings; the `_180` clips turn half a circle. The "wide sweeping swings" the owner chose, already in the data. |
| The hammer's dive | MP rig clips `SM_JMP_TH_GP_Loop_01` (1.767 s, the dive), `SM_JMP_TH_GP_Impact_01` (1.067 s, the landing), `SM_JMP_TH_GP_ReturnToIdle` (0.867 s) | A two-handed weapon driven down into the ground on landing: with the spear's point turned down, this is the impaling dive. |
| The pack's dive itself | spec "Pound impact", "Pound stun", "Pound target marker" | The dive steers to a marked point and lands with a blast: r 7, 25 + 50 shield, everyone in it stunned (owner's rule M44). |
| Other pole models | `weirdboy_staff.oppc`, `nob_waaagh_banner.oppc` | Ork-styled; not for a marine. |
| Librarian | `ebps` / `librarian` | No weapon at all (armour, backpack, revive). No staff graph exists. |

### 1.4 Demolition charge: complete in the data, never offered

| Finding | Where (Verified-local) | What it means |
|---|---|---|
| The item | `wargear.oppc` / `sm_mp_eq_demolition_charge` (no Chaos twin) | Slot `equipment`, weapon type `weapon_1hand`, animation set `grenade` (the same toss as the frag), `load_type` `mp_weapon`, level 1, `visible_in_customizer` False. No class's `startup_wargear` lists it (they list frag, krak, stimpack and melta bomb). |
| Texts | locale 105800, 105801, 120190, 120191 | "Remote Detonators". "Explosives linked to a remote activator." "%ABILITY_USEEQUIPMENT% to Throw or Detonate". Icon name `remotedetonation_mp` (not looked up in the HUD files). |
| How many | its ammunition `remote_det_charge` | 1 carried, starting full (`initial_clip_amount` −1); 2 with Favor of the Armory (`mp_perk_equipment_double`). |
| One out at a time | a second ammunition `det_pack_deployed` (1) | A throw needs it and spends it. It comes back when the thrown charge is destroyed (`on_tracked_projectile_destroyed_actions`). So a second charge cannot go out until the first has gone off. |
| Throw | `ability_utility` | Semi auto, cooldown 0.5 s, hand `rig_r_hand`, sound `play_sm_grenade_toss`. Its throw effect is named `test_xyz`: a placeholder. |
| Detonate | `detonate_projectile`, `input_override` `useequipment`, delay 0.01 s | **The equipment key again sets it off.** The grenade launcher's remote detonation uses the same ability on the reload key; that one is already simulated (spec "Remote detonation"). |
| The charge in flight and at rest | `ebps.oppc` / `ebps\projectiles\pp_grenade_remote_detonation` | Speed 25, no lifetime (it waits until set off), rigid body, 1 bounce then stops, **sticky**, gravity ×1 at 1 m and ×2 from 2 m, 35 health with `detonate_on_death` (it can be shot off), `on_launcher_death_policy` "die". Fuse effect `ork_frag_grenade_fuse` at `fx_trail`, trail sound `play_frag_grenade_trail_mp`. |
| The blast | same blueprint, two blasts | Each r 10, line of sight, 100 explosive damage, no shield part, fall-off 0 → 1, 4 → 1, 6 → 0.75, 10 → 0.3. The first throws bodies (force medium); the second staggers at full intensity. **200 at the centre**, the frag grenade's damage. Effect `squig_explosion` ×1.5, sounds `play_sm_grenade_explode` and `play_big_damage_grenade`, a camera shake. |
| Upgraded | `pp_grenade_remote_detonation_upgraded`, with Master-Crafted Wargear (`mp_perk_equipment_effectiveness`) | 150 + 100, r 15 (fall-off 6 → 1, 9 → 0.75, 15 → 0.3), `max_active_projectiles` 0. |
| Refill | `pickup_mp_drop_grenades` | A grenade drop gives +1 `remote_det_charge` (spec "Grenade amounts"). |
| Model | the blueprint names `stikka_bomb_proj` / `geo_stikkbomb_lodh` | **The Ork stikkbomb**, a stand-in on a marine item. Marine-made alternatives in the install: `melta_bomb.oppc`, `frag_grenade.oppc` (not yet loaded with our reader). |

The demolition charge is a finished SM1 item with stand-in art (an Ork model, a placeholder throw effect), left out of every class.

---

## 2. How a weapon is built here (the parts a new one touches)

- **Simulation numbers.**
  - A gun is a `WeaponSpec` in `SimConfig.Weapons`, read from its wargear by `WeaponTuning.Read`.
  - A melee weapon is a `MeleeCombo` (light steps, heavy steps, charge) in a class's `MeleeChoices`. It is read by `MeleeTuning.From` from a graph map (`MpClasses.MeleeMaps`: graph and sequencers) plus the wargear's abilities (damage).
  - An equipment item is a `ThrownSpec` in `SimConfig.Thrown`, read by `WeaponTuning`; a class's `EquipmentChoices` index that list.
  - Classes hold `PrimaryChoices`, `PistolChoices`, `MeleeChoices` and `EquipmentChoices`.
- **Wire.** The Hello carries choices as indices into the class's lists. They are resolved by wargear name (`LoadoutIndices`), sent as U8, and the profile stores names. Appending an item at the end of a list changes no existing index and no wire format.
- **Config hash.** It covers every weapon, combo and thrown item, so items appended only in the Musou profile change only the Musou profile's hash.
- **The added-content load point.** It already exists: `GameplayTuning.ExtrasLoad`, whose dispatcher `src/SmContent/Extras/GameplayTuning.Extras.cs` runs Musou's `MusouLoadout.Prepare` under the Musou flag. Prepare copies the class choice lists. Items appended in the same dispatcher just before Prepare therefore reach Musou's drops (`MusouDropRules` picks from the class's choices), reward cards (`TfwRewards` takes equipment from the class's list), extra slots 3 to 5 and the Musou customizer, without touching those Musou files.
- **Customizer.**
  - `PerkCatalog` lists each class's items from the wargear's `ui_data`; Musou's Standard Marines page reads the same catalogue.
  - A new item needs a catalogue entry (name, slot, title, description).
  - The Hello index comes from the simulation's list by name, so the two cannot drift.
- **Client presentation.**
  - The weapon model comes from the wargear's `visual_data` (`WeaponModels`).
  - Animation families are chosen by wargear-name rules: `HipFireAnimations.FamilyOf` ("heavy_bolter" → heavy), `LocomotionAnimations.SprintFamilyOf`, `ReloadAnimations`, `FallAnimations` and the flinch rules.
  - The melee clips come from `MpClasses.MeleeMapFor(wargear)`.
  - Hands: `WeaponHands` (a melee weapon is in hand only while a strike is shown, or always for a class that always holds it; two-handed items move to the chest prop).
- **Heavy weapons.**
  - Brace (heavy bolter only, from its `ability_ranged_braced`) and vent (plasma) live in `Brace.cs` / `Vent.cs`.
  - A Devastator primary is one clip for a life (400 for the heavy bolter), refilled by ammo pickups.
- **Assault jump pack.** Dash attack, ground pound and air slash come from the pack, not the weapon. The only weapon-specific rules are the hammer's (`Loadouts.WieldsHammer`, matched by wargear name: no pound delay, hop swing, steered slam).
- **Throws and remote detonation.** A Grenade press starts a throw in the shared step (`Combat.StepPlayer`; predicted). Remote detonation exists for the grenade launcher only, on the reload key (`World.DetonateRemotely`, `PlayerState.ProjectilesOut`). Sticky projectiles are simulated.
- **Musou's own combat.** Musou applies the players' hits to the Orks in its own code (`World.MusouCombat.cs`: the cleave, projectiles against Orks, charges), with existing hooks for blasts (`ExtrasBlast`), jump events (`ExtrasJump`: Launch, Dive, Land, Impact, SlashLand), the frag's 2× reach (`ExtrasThrownBlastScale`) and melee (`ExtrasMelee`). A sticky projectile that meets an Ork stops at the point it struck; it does not ride on him.
- **Musou's supply and blasts.** Musou gives one grenade back every 15 s while a player carries fewer than the item's most (`World.TfwSupply.cs`, `TfwSupplyRules.GrenadeRefillSeconds`; one period for every item). A blast's kills fly as corpses, at most 20 per throw (the owner's limit, `BlastThrownMax`); the rest are torn apart. Survivors are knocked down, pushed back up to 2 m (`BlastKnockback`, scaled by the intensity; Nobs half) and staggered from intensity 0.25. The Scrap Mek's contraptions are Musou entities with 75 health.
- **Hit-scan splash is not simulated.** `WeaponTuning.Read` reads a projectile's or a cone's blasts, not a hit-scan shot's `hit_sim` `area_of_effect`. Musou's bolt blasts (a blast at every Ork a bolt round strikes, `TfwCombatRules.BoltBlast*`, 1.25 m) are the path the autocannon's explosive round reuses (D12).

---

## 3. Where it lives (decided: Musou Mode only)

None of the four items was offered to an SM1 multiplayer player, so all four are **added content**. The owner's answer D1 puts them inside the existing Musou feature, not a new one:
- **Gate:** `ContentExtras.Musou`. No new flag. Faithful and extended builds never load them; Annihilation and Seize Ground are untouched, including the extended profile's config hash (W0 pins it byte for byte).
- **Folders:** `src/SmContent/Extras/Musou/Arsenal/`, `src/SmSim/Extras/Musou/Arsenal/`, `client/Extras/Musou/Arsenal/`, `tests/*/Extras/Musou/Arsenal/`. Namespaces `<Project>.Extras.Musou.Arsenal`.
- **Core code** is reached only through `Extras*` hooks (section 8, W0), each a no-op without the Musou flag.
- **Numbers** stay **data** wherever a file gives them (the autocannon's and the demolition charge's), with the source named. Everything else is tagged estimate or ours and goes into the refine register.
- `docs/changes-from-original.md` section 0 lists the four items as added content.

Protocol and versions:
- No protocol change and no extras-wire change is expected. The loadout items are appended indices; projectiles, their stuck and attached flags and their detonations are already on the wire.
- Musou's rules version (`MusouSim.Version`) goes up with the new rules; renumbered at integration as usual.
- Musou runs on the player's PC; the VM's Classic server needs nothing.

Classic PvP later only on the owner's word. It would need Chaos twins, a hit-scan splash rule in the core shot path, and balance against the six classes.

---

## 4. Autocannon (decided: a Musou Devastator loadout primary)

**Decided.** A Devastator loadout primary in Musou, picked like the heavy bolter, lascannon and plasma cannon; no tripod pickups, now or later (D10). Full auto (D5, Q9). The owner (D12): "the autocannon should fire slower but do more damage than the heavy bolter and have 2x the HE explosion size of the bolter and bolt pistol". No brace (D11).

**Role.** The heavy, slow gun of the Devastator: fewer, harder rounds than the heavy bolter, each one bursting in a bigger explosion at every Ork it passes through.

**Controls.**
- Fire (LMB) shoots, full auto. Aim (RMB) uses `aim_sm_heavy_weapon_mp`.
- Reload (R) does nothing: neither file has a reload ability or a brace.
- Melee is the Devastator's kick and stomp; the campaign's autocannon strike plays the same kick clip. Hop, sprint and switch follow the heavy bolter's rules.

**Numbers: the install's own MP file where it gives one, with the campaign's full auto.**

| Parameter | Autocannon | Heavy bolter (for scale) | Kind / source |
|---|---|---|---|
| Trigger | full auto | full auto | data: campaign `auto_cannon` (the MP file fires 2-round bursts) |
| Damage per round | **50** (more than the heavy bolter's) | 35 | data: MP file `csm_mp_exotic_auto_cannon` |
| Between rounds | **0.4 s** (slower: 2.5 rounds a second) | 0.233 s from the hip, 0.1 braced | data: the MP file's shot cooldown, used between single rounds |
| Damage a second, one body | 125 | 150 from the hip, 350 braced | data, derived |
| Head multiplier | 1 | 1 | data: MP file |
| Fall-off | ×0.75 from 45 to 70 m | ×0.8 from 50 to 75 m | data: MP file |
| Heat | 1 per round of 15; cools 4.5/s after 0.41 s without a shot: **15 rounds, 6 s of fire** to overheat, 3.3 s to cool from full | 3 per round of 100: 33 rounds, 7.7 s from the hip | data: MP file ("overheats easily", its description). No retune needed: at 0.4 s a round the gap stays under the 0.41 s cooling delay, so it cools only when the trigger rests |
| Clip | 400 (Musou's ammunition is endless anyway) | 400 | data: campaign |
| Pierce | through 5 Orks, bursting on the 6th | 5 | Musou's existing rule (`TfwCombatRules.Pierced` already names `auto_cannon`) |
| **Explosive round** | a blast at **every Ork a round strikes**: radius **2.5 m**, full to 1.0 m, 30 % at the edge, 15 damage | the bolt blast: 1.25 m, full to 0.5 m, 30 % at the edge, 15 | owner's (D12: 2× the size of the bolt blast, `TfwCombatRules.BoltBlast*`); the damage kept at the bolt blast's 15 (the owner asked for the size: estimate) |

The explosive round **uses the bolt blast's rules**, recommended for one consistent look:
- A kill is torn apart where he stands (`MusouDeathKind.BoltBlast`), never thrown.
- A fodder Ork it hurts falls where he stands; any other staggers with no knockback.
- It posts no frag detonation sound (the owner turned that off for bolt blasts).
- It counts toward the owner's per-throw limit of flying Orks as the bolt blasts do (none fly).

The campaign's 4 m hit-scan splash is dropped: this blast replaces it (D12).

**Presentation.**

| Piece | Plan | Source |
|---|---|---|
| Model | `turret_aa`, held at `rig_prop_m_chest` like the heavy bolter, stowed at `mkr_stow_bolter` | data (every autocannon wargear) |
| Walk, sprint, hop, fall | the heavy bolter's families (`SM_TB_NAV`, heavy sprint, `SM_EVD_TB_*`) | data: the campaign graph's nav is `SM_TB_NAV` |
| Shot | additive `SM_RNG_CN_FireIdle_S_01` from the campaign rig (same skeleton); fallback the heavy bolter's fire | data (campaign rig) |
| Effects and sound | `autocannon_muzzle`, `heavy_bolter_tracer`, `shell_casing`, `play_autocannon_turret_fire`; the blast: the bolt blast's effect at twice its size | data: campaign file; the blast's look ours |
| HUD | reticle `autocannon_mc`, ammo `ammo_autocannon_mc` | data (`Hud.gfx`) |
| Name, description | 104314 "Autocannon", 104315 | data |
| Loyalist name | `sm_mp_exotic_auto_cannon` (ours: the Chaos file's twin, built in code, never written to disk) | ours |

**Missing:** a loyalist MP file (built in code from the Chaos one).

---

## 5. Lightning claw (decided: one claw, 25 % faster, no extra edge)

**Decided.** One claw on the right hand plus the bolt pistol (D2); the sword's swings about 25 % faster (D13); no extra edge: no bonus against big Orks, no arcs (D16). The look follows the owner's reference images (D15).

**Role.** The Assault's fastest melee weapon: quicker strings, lighter hits, the same damage a second as the power sword.

**Hands.** The claw is worn on the right hand, where the sword strikes; the bolt pistol stays in the left hand, as with every Assault melee weapon (SM1's own layout). A claw cannot be put away, so it is always worn (ours). The client's existing "always holds its melee weapon" rule does that, applied to the claw.

**Move set.** The power sword's graph and chain (`MP_SM_MEL_PowerSword`) played **1.25× faster**:
- the light string X1..X4;
- the heavy strikes Y, X,Y, X,X,Y, X,X,X,Y and the Y,Y stomp;
- the sword's charge;
- the pack's dash attack, ground pound and air slash, as the sword's.

| Parameter | Value | Kind / source |
|---|---|---|
| Clip speed and step timings | the sword's clips at 1.25×, every step timing and window ÷1.25 | owner (D13: "about 25 % faster"); the factor ours |
| X1..X4 damage | 52 / 56 / 58 / 66 (the sword's 65 / 70 / 73 / 82 ×0.8: the same damage a second) | data, scaled |
| Heavy strikes, stomp, charge | the power sword's ×0.8, played 1.25× faster | data, scaled |
| Musou's melee rules | as every weapon: the cleave, the heal and armour per enemy struck, the stims | Musou's existing rules |
| Perks | none of the sword's perks count for the claw at first | ours (the default) |

**Look (owner's reference images, built in code).**
- **Gauntlet:** a bulky armoured steel gauntlet on the right forearm, with pistons or springs and brass bands on the back of the hand and the wrist. Parented to `RIG_R_Hand`, painted with the armour's metal.
- **Talons:** four long steel talons from the knuckles, silver, curving slightly downward like a raptor's, each about 1.2× the forearm's length, tapering to points.
- **Glow:** the references show none. Ours: a faint blue power crackle on the talons during strikes only (`amb_chaos_lightning_wrapping` recoloured blue).
- **Hits:** `power_claw_strike_00` and `power_blade_dmg_00`; the power sword's swing and hit sounds, with an install lightning snap (`play_capacitor_lightning`) on a hit (the default).
- **Name:** "Lightning Claw" (ours, English).
- Shown to the owner in the first still, before the rest of W2.

**Missing:** any claw model or clip in the install (built in code; borrowed clips).

---

## 6. Power spear (decided: impaling dive, wide sweeping swings, slower than the hammer with a much longer reach)

**Decided.** The jump-pack dive lands with an impaling strike; the ground attacks are wide two-handed sweeps (D3). The owner (D14): "slower than hammer but much wider reach". The hammer's jump-pack rules (D17). The look follows his reference image (D15).

**Role.** A two-handed polearm: slow, very long sweeps that clear the crowd around the player, and a dive that kills one big Ork from the air.

**Grip.** Two-handed on the thunder hammer's set, the only two-handed set in the data; the pistol is kept, as with the hammer.

### 6.1 The sweeps

| Parameter | Spear | Hammer in Musou (for scale) | Kind / source |
|---|---|---|---|
| Tempo | the hammer's clips at **0.85×**, every step timing ÷0.85 (X1..X3 2.67 / 2.33 / 2.33 s → 3.14 / 2.75 / 2.75 s; hits 0.60 / 0.57 / 0.70 s → 0.71 / 0.67 / 0.82 s; the owner's string reset and recovery cut stretched the same) | 1.0 | owner (D14: slower than the hammer); 0.85 an estimate |
| Reach of a sweep (Musou's cleave) | **12 m** from the feet | 8.25 m: (4 + 0.5 + 1) × 1.5 | owner (D14: much wider reach); 12 m an estimate |
| Width | **240°** (±120° of the facing) | 180° (the core's ±60° × 1.5) | estimate |
| Orks one swing hits | at most **12**, nearest first | 8 | estimate (the crowd cap) |
| Damage per light swing | **150** | 200 | estimate |
| Heavy strikes | the hammer's interrupt and two sweeping attacks (stun) at 0.85×, the same reach, width and cap, damage ×0.75 | the hammer's | data, scaled |
| Y,Y finisher | the hammer's slam at 0.85×, steered as the hammer's; its blast 7 m, 100 + 125 | the same | data, slowed |
| Charge | the hammer's bull rush (75 + 25, stun) | the same | data |

**Fair against crowds** (estimate, light swings, every swing full):
- Spear: 150 × 12 Orks every 2.75 s, about **655** a second.
- Hammer: 200 × 8 every 2.33 s, about **690** a second.

So the wider sweep reaches more Orks without killing faster. Against one target the spear is weaker (55 a second against 86). A Boy (85) dies to one swing; an 'Ard Boy (170) to two.

### 6.2 The impaling dive (D18)

| Parameter | Value | Kind / source |
|---|---|---|
| The dive | the pack's own: marker, steering, speed, the 7 m stun blast of 25 + 50 on landing | data and the owner's M44 rule |
| The impale | on landing, the enemy nearest the impact point within 2 m takes **600** (a Nob's whole health: a Nob or anything smaller dies outright) | estimate |
| A survivor | a Boss Ork takes the 600 and is pinned (staggered) for 1 s; the Warboss is stagger-immune and only takes the damage | estimate; the Warboss rule is Musou's |
| Nothing within 2 m | only the pack's blast | ours |
| The air slash's landing | not an impale | ours |
| Jump-pack rules | the hammer's: a hop swing, no pound delay | owner default (D17) |
| Animation | the hammer's dive and landing clips (`SM_JMP_TH_GP_Loop_01`, `_Impact_01`), the spear turned point-down from the dive's start until the get-up | data clips; the turn ours |

The impale is Musou code on the existing `ExtrasJump` hook (`JumpEventKind.Impact`). The hammer's jump rules need one hook in `Loadouts.WieldsHammer` (W0).

### 6.3 Look (owner's reference image, built in code)

- **Shaft:** black, about 1.3× the marine's height (about 2.8 m).
- **Fitting:** near the top, an ornate bronze or gold collar with a small bracket or crossguard and a ring.
- **Blade:** long, straight and single-edged, about a third of the shaft's length (about 0.9 m), with an angled chisel tip. It glows with a translucent blue power field, deeper blue at the base, lighter toward the edge.
- **In hand:** held at the chest prop like the hammer. At rest the reference holds it upright at the side, butt on the ground; in Musou the hammer's carry is used (ours).
- **The force staff:** the install's force staff (`forcestaff_basic`, 2.76 m) is the shaft only if a still shows it matches the black shaft and bronze fitting; otherwise the shaft is built in code too.
- **Effects and sounds:** the hammer's power-field effects along the blade; the power axe's swing and hit sounds.
- **Name:** "Power Spear" (ours).
- Shown to the owner in the first still, before the rest of W3.

**Missing:** any spear model, thrust animation or spear sound in the install.

---

## 7. Remote Detonators (decided: Musou equipment on a short fuse, a rolling ring of explosions)

**Decided.**
- **Item and look:** the install's unused `sm_mp_eq_demolition_charge`, named **"Remote Detonators"** (its own text) on the install's **melta bomb model**, though it now runs on a fuse (D4, D21).
- **Carriers:** every Musou class and the Librarian (D20).
- **Fuse and sticking:** a fuse of **5 s after it sticks**, with no second press. It is thrown and sticks to walls, floor, contraptions and Orks (Bosses too), moving with what it is stuck to (D6, D7, D19).
- **Count:** 1 charge, a new one every 30 s; 2 with Favor of the Armory (D9).
- **Blast (the owner's own numbers, D19):** "600 damage at the centre, 40m reach, bosses take 2x, up to 80 orks thrown flying, survivors knocked back up to 10m". It is delivered as **a centre blast, then a rolling ring** of explosions that **stack** (D22, D23).
- **Master-Crafted Wargear:** the data's own upgrade ratio on his numbers (D24).

**Throwing and sticking.**
- The equipment key (G) throws it with the frag's toss (data: animation set `grenade`, cooldown 0.5 s). Its flight is the data's: speed 25, gravity ×1 at 1 m and ×2 from 2 m.
- It sticks to the first thing it hits:
  - walls and the floor (data: `sticky`);
  - a Scrap Mek contraption, an Ork, a Boss Ork or the Warboss, moving with them (D7; ours in code).
- It passes marines (ours).
- If what it is stuck to dies or is destroyed first, it drops where that was and the fuse runs on. An Ork thrown by another blast carries it along.
- **Fuse:** 5 s after it sticks (D19). **One that sticks to nothing** (thrown off the map, never landing) goes off 5 s after the throw, where it is (ours). It shows the data's fuse effect `ork_frag_grenade_fuse` while it burns.

**Count and refill.**

| Parameter | Value | Kind / source |
|---|---|---|
| Carried | 1 | data (`remote_det_charge`, starts full) |
| Favor of the Armory | 2, both may be out at once (the data's one-out rule served the remote trigger, which is gone) | data; the rule ours |
| Back in Musou | one every 30 s while short (grenades: 15 s) | owner (D9) |
| At a respawn | full | the core's rule |

**The blast. The install's numbers first.**

| | Damage at the centre | Radius | Fall-off (distance → share) |
|---|---|---|---|
| Frag grenade, data | 200 | 10 m | 2.5 → 1, 2.6 → 0.5, 5 → 0.5, 5.1 → 0.3, 7 → 0.3, 10 → 0.05 |
| Frag in Musou today (×2 reach) | 200 | 20 m | the same distances ×2 |
| Charge, data (`pp_grenade_remote_detonation`) | 200 (two blasts of 100) | 10 m | 4 → 1, 6 → 0.75, 10 → 0.3 |
| Charge, data, Master-Crafted | 250 (150 + 100) | 15 m | 6 → 1, 9 → 0.75, 15 → 0.3 |
| **The owner's charge (D19)** | **600** | **40 m** | the data's curve stretched ×4: **16 → 1, 24 → 0.75, 40 → 0.3** (the target the parts below are fitted to) |

**Ours: the centre blast and the ring** (every value an estimate, fitted so the whole charge follows the owner's 600 and 40 m).

| Part | Value |
|---|---|
| Centre blast | When the fuse runs out, at the charge: **600**, r **16 m**, the data's curve stretched ×1.6 (6.4 → 1, 9.6 → 0.75, 16 → 0.3). |
| Ring | **16 spots** on a circle of **24 m** around the point where the charge went off, 22.5° apart (9.4 m between neighbours); the ring stays there even if the charge rode an Ork. |
| Order and pace | One at a time, clockwise seen from above, from a start angle fixed by the charge's projectile id (id × 137.5°), so the server and every client agree without a message. The first spot **0.1 s** after the centre blast (3 ticks), then one every **0.067 s** (2 ticks at 30 Hz): the 16th at **1.1 s**. |
| Each spot | **180**, r **16 m**, the same curve as the centre blast, line of sight from the spot. |
| Stacking | **Each spot hurts on its own, and they stack** (D22): an Ork in the reach of three spots and the centre takes all four. |
| Where a spot sits | The ring point at the charge's height, dropped to the ground below it (a ray down, at most 6 m). If a wall stands between the charge and the ring point, the spot goes off at the wall's face. With no ground within 6 m it goes off in the air. |
| Bosses | Boss Orks and the Warboss take **×2** from the centre and from every spot (D19): 1,200 at the centre. |
| Thrown Orks | Kills fly away from the centre or the spot that killed them, so the Orks are thrown outward in a wave that runs round the ring: up to **80** per charge (D19); the whole charge counts as one throw for the limit. Survivors are knocked down and thrown back up to **10 m** (D19), scaled by the strongest hit's share, once per charge; Nobs half; Boss Orks stagger; the Warboss is stagger-immune. |
| The circle at the end | Each spot's effect plays 1.0 s (ours), so the first is still burning when the last goes off: for a moment the full circle of explosions stands round the charge. |
| Master-Crafted Wargear | The data's ratio (damage ×1.25, reach ×1.5) on every part: centre 750 within 24 m, the ring at 36 m with spots of 225 within 24 m: **60 m** reach (D24; a very large blast: noted for the playtest). |

**What it does, from the fit** (the owner's target beside it; stacking included; "spot line" is a line from the charge through a spot, "between" halfway between two spots):

| Distance | Spot line | Between | Owner's target |
|---|---|---|---|
| 0 to 5 m | 600 | 600 | 600 |
| 10 m | 639 | 584 | 600 |
| 15 m | 584 | 642 | 600 |
| 20 m | 455 | 535 | 525 |
| 24 m | 457 | 520 | 450 |
| 30 m | 388 | 316 | 349 |
| 35 m | 231 | 200 | 264 |
| 40 m | 54 | 0 | 180 (the data's curve ends at 0.3) |

**Who survives** (body health from `MusouTypes`; Boss Orks and the Warboss take ×2):

| | Centre | 20 m | 40 m |
|---|---|---|---|
| Boy (85) | dies | dies | survives the 54 on a spot line; untouched between spots |
| 'Ard Boy (170) | dies | dies | survives |
| Nob (600) | dies (exactly his 600) | survives with 65 to 145 left | survives |
| Smallest Boss Ork (3,000) | loses 1,200 (40 %) | loses 910 to 1,070 (30 to 36 %) | loses up to 108 |
| Warboss (15,000) | loses 1,200 (8 %) | loses up to 1,070 | loses up to 108 |

From the fit: Boyz die out to about 37 to 39 m, 'Ard Boyz to about 35 to 36 m, Nobs to about 14 to 16 m (the first number on a spot line, the second between spots).

**Effects and sounds (install).**

| Piece | Choice | Source |
|---|---|---|
| Centre blast | the data's own: `squig_explosion` (scaled to the 16 m), `play_sm_grenade_explode`, the camera shake | `pp_grenade_remote_detonation` |
| Each spot | the frag's explosion `frag_grenade_explosion` (data: scale 0.6, length 0.6; played at length 1.0 for the closing circle) | `pp_grenade_multiplayer` |
| Each spot's sound | `play_sm_grenade_explode_mp`, on every fourth spot (4 of 16) and none on the rest, so 17 explosions in about a second do not swamp the mix (the owner's sound-overload concern; ours) | `pp_grenade_multiplayer` |
| Fuse, toss, trail | `ork_frag_grenade_fuse`, `play_sm_grenade_toss`, `play_frag_grenade_trail_mp`; the placeholder throw effect `test_xyz` is not played | data |
| Not used | `krak_grenade_explosion` (a blinding flash), the melta bomb's `emp_grenade` and lingering `grenadier_sticky_tar` (a slow burn) | |

**Network: no new event.**
- The charge's detonation is already on the wire (the projectile record's detonated flag, kept 3 ticks). The client starts the ring from it, the item index and the projectile id, places the spots with its own collision world and plays them on the server's tick schedule.
- Damage is server-side; the Orks' hits, deaths and throws arrive through Musou's existing entity updates.
- Because the spots stack, an Ork near the ring takes several hits: more hit events than one blast, bounded by 17 blasts × the Orks in reach.

**W4 must measure, and report to the lead before anything is cut** (80 flying corpses are four times the frag's limit of 20):
- client frame time;
- server tick time;
- snapshot bytes;
- hit and death event counts.

It is measured with a charge going off in a Musou crowd of 1,000+ Orks, in the war's fixed-seed fight. Nothing is lowered without the owner's word.

**Customizer row and HUD:** the data's name and description (105800, 105801; the action text "to Throw or Detonate" does not fit a fuse and is not shown), icon `remotedetonation_mp` if the HUD files hold it, else a placeholder.

**Code.**
- The throw, the flight and sticking to the level are the core's existing thrown-item and sticky rules; the item's `ThrownSpec` is built in code from the data's file with the numbers above and appended only in the Musou profile. **No core hook is needed.**
- Musou code (W4):
  - sticking to and riding on Orks and contraptions, and the fuse;
  - the centre blast and the ring's 16 stacking spots on their tick schedule, the ×2 on Bosses, the 80-corpse limit and the 10 m knockback;
  - the client's ring;
  - the 30 s refill (one per-item period in `World.TfwSupply.cs`, which W0 adds).
- The Librarian's equipment list belongs to his session (local_0217dfc2): W4 coordinates the charge's place in it at implementation time.

---

## 8. Milestones and file ownership

| Milestone | Owner | Files (one writer each) | Done when |
|---|---|---|---|
| **W0: the Musou arsenal base** (first; small) | one implementer | `src/SmContent/Extras/Musou/Arsenal/Arsenal.cs` (the registry: four entries, each a stub that adds nothing); one line in the dispatcher `src/SmContent/Extras/GameplayTuning.Extras.cs` (before `MusouLoadout.Prepare`); the core hooks below; the Musou call sites below; `ExtrasBoundaryTests` pins; the docs' section headings | Faithful and extended config hashes byte for byte as before; a Musou config unchanged while the stubs add nothing. Both builds 0 errors; the boundary, Musou isolation and Musou skirmish pin tests pass. |
| **W1: autocannon** | implementer A | `…/Musou/Arsenal/Autocannon.cs` (SmContent: the loyalist twin and its numbers; SmSim: its blast rule), `client/Extras/Musou/Arsenal/Autocannon/*`, `tests/*/Extras/Musou/Arsenal/Autocannon*` | Picked in the Musou loadout, fired at 0.4 s, overheated after 15 rounds, a 2.5 m bolt-style blast at every Ork struck; player and third-person stills; targeted tests. |
| **W2: lightning claw** | implementer B | `…/Musou/Arsenal/LightningClaw.cs`, `client/Extras/Musou/Arsenal/Claw/*`, `tests/*/Extras/Musou/Arsenal/Claw*` | The claw from the reference description shown in a still first; a string, the heavies, a charge and a dive at 1.25×; the speed-up checked in a clip against the simulation's hits. |
| **W3: power spear** | implementer C | `…/Musou/Arsenal/PowerSpear.cs`, `client/Extras/Musou/Arsenal/Spear/*`, `tests/*/Extras/Musou/Arsenal/Spear*` | The spear from the reference description shown in a still first; the sweeps at 0.85× reaching 12 m over 240° and at most 12 Orks; the impaling dive kills a Nob outright; the hammer's jump rules; stills and a clip. |
| **W4: Remote Detonators** | implementer D | `…/Musou/Arsenal/DemolitionCharge.cs`, `client/Extras/Musou/Arsenal/Charge/*`, `tests/*/Extras/Musou/Arsenal/Charge*` | Throw; stick to the level, a contraption, an Ork, a Boss Ork and the Warboss, riding along; the 5 s fuse; the centre blast and the 16-spot stacking ring matching the table above; 80 corpses and the 10 m knockback; 1 carried (2 with Favor of the Armory), one back every 30 s; the measurement above reported to the lead; stills and a clip. |

No tripod milestone (D10).

**Core hooks W0 adds.** Each is a partial `Extras*` method, pinned and marked `// EXTRAS-HOOK`, a no-op without the Musou flag. The names are proposals.
1. `MpClasses.ExtrasMeleeMap(string wargear, ref WeaponMap?)`: the graph map of an added melee name; the client's `MeleeMapFor` uses it too.
2. `Loadouts.WieldsHammer` → `ExtrasWieldsHammer(..., ref bool)`: the spear takes the hammer's jump-pack rules.
3. `PerkCatalog.ExtrasItems(ref list)`: the customizer rows. They appear in Musou's own loadout slots (claude/musou-loadout-slots stores names checked against the catalogue); W0 keeps them out of Classic's slots and lists.
4. Client `WeaponModels` / `WeaponRig`: `ExtrasWeaponModel(string wargear, ref model)`, a model built in code or loaded from another package, with an always-worn flag (the claw is never put away; the rig's "always holds its melee weapon" rule applied to one weapon).
5. Client `ExtrasAnimationAlias(ref string wargear)`: maps an added name to the family it borrows (autocannon → heavy bolter, claw → power sword, spear → thunder hammer). It is applied in the name rules of `HipFireAnimations`, `LocomotionAnimations`, `ReloadAnimations`, `FallAnimations` and the flinch rule.
6. A clip-speed factor (claw 1.25, spear 0.85) that both the simulation's steps and the client's clip playback read, like the Fast Stomp perk's clip speed (`MeleeTuning.ReadSpeedUpgrades`); W0 checks how the client applies that today.

**Musou call sites W0 adds.** Each is one line calling the registry, in Musou files other sessions also edit; the lead sequences W0 after the Musou sessions in flight.
- the cleave's reach, width, target cap and damage per weapon (`World.MusouCombat.cs`);
- a weapon's own blast at every enemy a round strikes (the bolt blast's path, with the weapon's radius and fall-off);
- the pound impact (`ExtrasJump`, Impact) for the impale;
- a sticky projectile meeting an Ork or a contraption (stick and ride, and the fuse from that moment);
- a thrown item's own detonation (the centre blast and the ring, the Boss multiplier, the corpse limit and the knockback);
- a per-item refill period in `World.TfwSupply.cs` (15 s for every item as now; 30 s for the charge).

**Shared files.** `spec/behavior.md`, `docs/changes-from-original.md`, `docs/milestones.md`, `docs/commands.md` and the generated `docs/refine-register.md`:
- Each milestone writes only under its own heading, which W0 creates.
- The lead regenerates the register at integration.

W1 to W4 share no source file after W0 and can run in parallel.

Process:
- Every milestone keeps Musou changes away from Annihilation and Seize Ground (MusouIsolation tests).
- Every milestone needs a UI screenshot gate for new visuals: player view and third person, in the media previews folder.
- Every milestone gets an independent review.

---

## 9. Risks and checks

| Risk | Check |
|---|---|
| Campaign clips on the MP marine (the autocannon recoil) | The skeletons are identical (checked). W1 plays the recoil in a still before relying on it; fallback the heavy bolter's fire. |
| `turret_aa` is 2.8 m long and was made for a tripod and a carry | W1's first still: the grip at `RIG_Prop_M_Chest`, clipping, camera. |
| Clips at other speeds (claw 1.25×, spear 0.85×) desynchronise the simulation and the client | W0 checks the Fast Stomp path; W2 and W3 compare the simulated hit tick with the clip's contact in a clip. |
| A 12 m, 240° sweep reads as hitting Orks the spear never touches | W3's clip shows the sweep against a crowd; the reach and width are estimates for the playtest. |
| The claw and spear built in code from descriptions of the owner's pictures | Each shown to the owner in a still before the rest of its milestone. |
| The charge's 80 flying corpses and 17 explosions in about a second | W4's measurement in a 1,000+ Ork crowd, reported to the lead; nothing cut without the owner. |
| The stacking ring overshoots the owner's target between spots (up to about +15 % at 24 m) or falls short at the very edge | The fit's table above; tuned in the playtest. |
| The client's ring drifting from the server's | Both place the spots from the same collision world (the map's collision hash is checked at join); W4 compares the spot positions of a server and a client run. The ring is drawn only; its damage comes from the server. |
| A charge riding an Ork that dies, is thrown by another blast, or a contraption destroyed under it | W4: it drops where its carrier was and the fuse runs on; a test for each case. |
| Master-Crafted makes the charge reach 60 m | Noted for the playtest (the owner's numbers with the data's ratio). |
| Other Musou sessions edit the same Musou files | W0's call sites go in sequenced by the lead; W1 to W4 then touch only their own files. |

---

## 10. Open questions (owner)

None left for the approval of this design: every question is decided (D1 to D24) or has the lead's default. Items to check in the playtest (all estimates, in the refine register once built):
- the autocannon's blast damage (15, the bolt blast's: the owner asked for the size only);
- the claw's ×0.8 damage;
- the spear's 0.85× tempo, 12 m reach, 240° width, 12-Ork cap and 150 damage;
- the impale's 600;
- the charge's ring (24 m, 16 spots, 1.1 s), its fit to the owner's 600 and 40 m, and the 5 s fallback for a charge that sticks to nothing.

---

## 11. Implementer briefs (W1 to W4; to hand out once W0 is in main)

**Approved** by the owner, 2026-11-10: "i approve the design as written - don't use a fable agent yet until i actually approvate creating the custom animations".
- The claw uses the sword's moves at 1.25× and the spear the hammer's at 0.85×.
- No custom animations and no Fable agent until he approves them separately. **Changed 2026-11-10** (the owner, through the lead): "Just custom animations first for claw and spear - I'll approve of those". A Fable animator agent authors custom clips for the claw and the spear as preview clips for his approval, to the design's hit times exactly (claw: the sword's hit times ÷1.25; spear: the hammer's ÷0.85), so the simulation is unchanged. All other animation work is blending, timing and transitions only; no new clips.
- No downloaded or generated models: models are built in code from the reference descriptions in section 5 (Look) and section 6.3.

**Rules for every implementer:**
- Never open `config/local.json`.
- The install is read-only; no original bytes in the repository.
- Start Godot only through `godot.cmd` (headless; `--windowed` only for a needed still).
- Servers use `--bind 127.0.0.1`.
- Kill only your own processes.
- No full `dotnet test` without the lead's word; targeted tests are fine.
- Touch only the files your brief lists. W0's call sites and hooks are the way into core and Musou code; a missing hook goes back to the lead, not into a core file.
- Musou Mode only: Annihilation and Seize Ground unchanged. W0's config-hash pin and the MusouIsolation tests must keep passing.
- Every number gets a `spec/behavior.md` row (value, units, timing basis, source, kind: data / owner's / estimate / ours). Every "ours" rule gets a `docs/changes-from-original.md` row under section 0. Write only under your milestone's heading, which W0 makes, and add a paragraph to `docs/milestones.md`. Run `smtool refine-register --check`.
- New visuals pass the UI screenshot gate: player view and third person, in the media previews folder.
- Each milestone gets an independent review. Report the branch tip to the lead.

The hook names below are W0's proposals; use the names W0 lands with.

**W0 as landed** (branch `claude/arsenal-w0`). Each item implements only its own partial methods, in its own files; nothing in a W0 file needs an edit.

| | Content (`Arsenal`, SmContent) | Simulation | Client |
|---|---|---|---|
| W1 autocannon | `AppendAutocannon`, `AutocannonItems`, `AutocannonAlias` | `World.ArsenalShotBlast` | `WeaponRig.AutocannonModel` |
| W2 claw | `AppendLightningClaw`, `LightningClawMap` (with `WeaponMap.ClipSpeed`), `LightningClawItems`, `LightningClawAlias` | none (the sword's cleave) | `WeaponRig.LightningClawModel` (sets `alwaysHeld`) |
| W3 spear | `AppendPowerSpear`, `PowerSpearMap`, `PowerSpearItems`, `PowerSpearAlias` | `ArsenalRules.SpearWieldsHammer`, `World.ArsenalCleave`, `World.ArsenalJump` (Impact) | `WeaponRig.PowerSpearModel` |
| W4 charge | `AppendRemoteDetonators`, `RemoteDetonatorsItems` | `World.ArsenalProjectileMeets`, `World.ArsenalStep` (the fuse, and the detonation and ring from the tick's detonations), `World.ArsenalRefillSeconds` | `NetworkView.RemoteDetonatorsVisual` (the thrown model: hook `ExtrasThrownVisual`, not `ExtrasWeaponModel`) |

Changed from the proposals:
- The charge's model in flight is its own hook, `ExtrasThrownVisual`.
- The catalogue hook comes with `ProfileViews.ExtrasSetCatalog`, so Musou's loadout set is read with Musou's catalogue.
- The clip-speed factor is the `WeaponMap.ClipSpeed` field, not a hook; the client's single-clip strikes read it (`NetworkView.MeleeClipSpeed`).
- The animation alias is read by the hip-fire, sprint, reload and flinch rules and the hammer stance (`AnimationAlias.Of`).
- **The animation seam** (added for the custom clips): the claw and the spear play the sword's and the hammer's clips as stand-ins: the borrowed maps' clips, and their light and heavy clip lists through `AnimationAlias`. One client hook, `NetworkView.ExtrasMeleeClip`, gives a strike (weapon, light or heavy, step) a clip of its own and its rate (1: authored to the simulation's timing), posed alone. Each weapon's table is a partial in its own milestone's files: `LightningClawClip` (W2) and `PowerSpearClip` (W3). The simulation never reads it.
- Not in W0: a source for clips that are not in the install. The rig's clips are read from its packfile only, and `HavokSplineAnimation` has no other constructor. That source comes with the approved clips (the lead decides where), and it changes no mechanics.
- Not reached by W0: the jump pack's own moves (dash attack, ground pound, air slash), which the clip-speed factor does not touch; and the other name-based presentation rules (hit sounds, power flash, the sword's glow and idle sound). Raise either with the lead if needed.

### W1: Autocannon (section 4)

- **Branch:** `claude/arsenal-w1-autocannon` from main.
- **Files you own:**
  - `src/SmContent/Extras/Musou/Arsenal/Autocannon.cs`: the loyalist twin of `csm_mp_exotic_auto_cannon`, built in code with the numbers of section 4, and its registry entry (a Devastator primary, Musou only).
  - `src/SmSim/Extras/Musou/Arsenal/AutocannonBlast.cs`: its blast, 2.5 m at every Ork a round strikes, with the bolt blast's rules.
  - `client/Extras/Musou/Arsenal/Autocannon/*`: model, stow, recoil, effects, sound, HUD.
  - `tests/SmFormats.Tests/Extras/Musou/Arsenal/Autocannon*.cs` and, for the simulation, `tests/SmNet.Tests/Extras/Musou/Arsenal/Autocannon*.cs`.
- **Hooks used:** the registry (`Arsenal.cs`); `PerkCatalog.ExtrasItems` (the customizer row: 104314, 104315); `ExtrasWeaponModel` (`turret_aa` at `rig_prop_m_chest`, stowed at `mkr_stow_bolter`); `ExtrasAnimationAlias` (→ heavy bolter); the weapon-blast call site.
- **First, the customizer's config (W0 review, 2026-11-10).**
  - The front end's customizer loads one config for every page: `CustomizerServices.Load` uses `GameplayTuning.LoadForMatch(GameDir, ContentExtras.Orks)`, which has no Musou and so no arsenal items.
  - From W1 on, Musou's catalogue lists the autocannon, but the Join from the Musou page (`PlanFor` → `JoinPlan.Build`, then `ContentGate.ForJoin` in `JoinOptions`) resolves names against that config. It would send the default primary. The item statuses (`PerkActivities`) would not know the gun either.
  - The in-match picker is fine: `SendLoadout` builds with the match's own config.
  - **Fix:** make `CustomizerServices.Config` (and `PerkActivities`) the config of the content profile in effect, as W0 did for `Catalog`.
    - Every profile but Musou: the data's config, unchanged.
    - Musou: its own config, `LoadForMatch(GameDir, ContentExtras.Musou)`, which runs `Arsenal.Append`. Load it once on a worker the first time a Musou page needs it; until it is ready, the Musou page's PLAY and JOIN wait for it or say so.
  - This is a core change outside W1's files: one new pinned hook (`CustomizerServices.ExtrasConfig(ContentExtras, ref SimConfig)`, its Musou dispatcher loading and caching). Agree with the lead who makes it (W1 with the lead's word, or a small W0 follow-up) before the Join criterion below.
  - Test: the Musou page's `PlanFor` of a Devastator slot with the autocannon asks for its index in Musou's config; a Classic page's plan is unchanged.
- **Done when:**
  - The Devastator picks it in a Musou loadout and the Musou customizer, and a Musou slot stores it; a Join from the Musou page sends it.
  - It fires full auto at 0.4 s, 50 a round, and overheats after 15 rounds.
  - A 2.5 m blast (full to 1.0 m, 30 % at the edge, 15) goes off at every Ork struck, through 5 pierced.
  - Kills are torn apart, hurt fodder knocked down, others staggered, and there is no frag sound.
  - Stills check the grip, clipping and the campaign recoil (fallback: the heavy bolter's fire).
  - Tests cover the spec values, the blast and the Musou-only loadout; builds show 0 errors.

### W2: Lightning claw (section 5)

- **Branch:** `claude/arsenal-w2-claw` from main.
- **Files you own:**
  - `src/SmContent/Extras/Musou/Arsenal/LightningClaw.cs`: the `MeleeCombo` from the power sword's map at clip speed 1.25 (every step timing ÷1.25) with every damage ×0.8, appended to the Assault's melee choices in Musou, and its registry entry.
  - `client/Extras/Musou/Arsenal/Claw/*`: the gauntlet and four talons built in code (section 5's look), always worn on the right hand, the blue crackle on strikes, hit effects and sounds.
  - `tests/*/Extras/Musou/Arsenal/Claw*.cs`.
- **Hooks used:** `MpClasses.ExtrasMeleeMap`; the clip-speed factor; `ExtrasWeaponModel` (with its always-worn flag); `ExtrasAnimationAlias` (→ power sword); `PerkCatalog.ExtrasItems` ("Lightning Claw"); the cleave call site (none needed beyond the sword's).
- **Animations:**
  - Build the claw on the sword's clips as stand-ins: the sword's map at clip speed 1.25, and the alias to the power sword (which gives the stand-in clip lists). Leave `LightningClawClip` (the animation seam) empty.
  - The custom clips the owner approves drop in there later, one entry per strike (light or heavy, step) with its rate, without touching the combo, the timings or the tests of the mechanics.
  - Make no clips yourself: the animator agent authors them. Your animation work is blending, timing and transitions only.
- **Done when:**
  - **First:** a player-view and a third-person still of the claw goes to the lead for the owner. Nothing else is built until he answers.
  - Then: the string X1..X4, the heavies, the stomp, the charge, the dash attack, the ground pound and the air slash with the claw at 1.25×.
  - A clip compares the simulated hit tick with the clip's contact.
  - Damage is the sword's ×0.8, with no edge; the pistol stays in the left hand.
  - Tests; builds show 0 errors.

### W3: Power spear (section 6)

- **Branch:** `claude/arsenal-w3-spear` from main.
- **Files you own:**
  - `src/SmContent/Extras/Musou/Arsenal/PowerSpear.cs`: the `MeleeCombo` from the hammer's map at clip speed 0.85, with section 6's damage, its cleave numbers (12 m, 240°, 12 Orks), the impale rule and its registry entry.
  - `src/SmSim/Extras/Musou/Arsenal/SpearImpale.cs`: the impale on the pound impact, 600 to the enemy nearest within 2 m, a Boss Ork pinned 1 s.
  - `client/Extras/Musou/Arsenal/Spear/*`: the shaft, fitting and blade built in code (section 6's look; the force staff's shaft only if a still shows it matches), and the spear point-down during the dive.
  - `tests/*/Extras/Musou/Arsenal/Spear*.cs`.
- **Hooks used:** `MpClasses.ExtrasMeleeMap`; the clip-speed factor; `ExtrasWieldsHammer` (the hammer's jump-pack rules); the cleave call site (reach, width, target cap, damage); the pound-impact call site (`ExtrasJump`, Impact); `ExtrasWeaponModel`; `ExtrasAnimationAlias` (→ thunder hammer); `PerkCatalog.ExtrasItems` ("Power Spear").
- **Animations:** as W2.
  - The spear plays the hammer's clips as stand-ins (its map at 0.85, the alias to the thunder hammer). Leave `PowerSpearClip` (the animation seam) empty for the approved custom clips.
  - The point-down turn during the dive is a pose on the stand-in, not a new clip.
  - Your animation work is blending, timing and transitions only; no new clips.
- **Done when:**
  - **First:** a still of the spear goes to the lead for the owner, as W2.
  - Then: the sweeps at 0.85× reach 12 m over 240° and hit at most 12 Orks; the heavies and the slam work.
  - The impaling dive kills a Nob outright and pins a Boss Ork.
  - The hop swing and no pound delay work.
  - A clip compares the hit tick with the contact, and a clip shows the sweep against a crowd.
  - Tests; builds show 0 errors.

### W4: Remote Detonators (section 7)

- **Branch:** `claude/arsenal-w4-charge` from main.
- **Files you own:**
  - `src/SmContent/Extras/Musou/Arsenal/DemolitionCharge.cs`: the `ThrownSpec` from `sm_mp_eq_demolition_charge` with section 7's numbers, appended to every Musou class's equipment, the customizer row ("Remote Detonators", 105800 / 105801), and its registry entry.
  - `src/SmSim/Extras/Musou/Arsenal/DemolitionChargeRules.cs` and `World.Charge.cs` (a partial in the same folder):
    - sticking to and riding on Orks, Bosses and contraptions;
    - the 5 s fuse after it sticks (5 s after the throw for one that never sticks);
    - the centre blast and the 16 stacking spots on their schedule;
    - Bosses ×2, up to 80 thrown corpses, the 10 m knockback;
    - the 30 s refill period.
  - `client/Extras/Musou/Arsenal/Charge/*`: the melta bomb model, the fuse effect, and the ring drawn from the detonation record (spot placement, effects, a sound on every fourth spot).
  - `tests/*/Extras/Musou/Arsenal/Charge*.cs`.
- **Hooks used:** the registry; `PerkCatalog.ExtrasItems`; `ExtrasWeaponModel` (its thrown model); and these call sites: a sticky projectile meeting an Ork or a contraption; a thrown item's own detonation; the per-item refill period.
- **Coordinate:** the Librarian's equipment list belongs to his session (local_0217dfc2). Agree on the charge's place in it before touching anything of his.
- **Done when:**
  - It is thrown and sticks to the level, a contraption, an Ork, a Boss Ork and the Warboss, riding along; the fuse is 5 s.
  - The damage follows section 7's table at the centre, 20 m and 40 m (a test); spots stack; the centre blast comes first.
  - Up to 80 corpses fly, survivors are thrown back up to 10 m, and Bosses take ×2.
  - 1 carried (2 with Favor of the Armory), one back every 30 s.
  - The client's ring matches the server's spots.
  - **The measurement:** frame time, tick time, snapshot bytes and event counts with a charge in a 1,000+ Ork crowd, reported to the lead before anything is cut.
  - Stills and a clip; tests; builds show 0 errors.
