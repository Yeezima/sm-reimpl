# New weapons and equipment for Musou Mode: lightning claw, power spear, autocannon, demolition charge (design, 2026-11-10)

**Status:** design, round 4 (the demolition charge to the owner's answers D6 to D9 and his rolling ring). No product code yet. Branch `claude/new-weapons-design`.

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

Section 10 lists the questions still open. Each choice has a recommendation, so "all recommended" is a complete answer.

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
- **Hit-scan splash is not simulated.** `WeaponTuning.Read` reads a projectile's or a cone's blasts, not a hit-scan shot's `hit_sim` `area_of_effect`. The campaign autocannon's splash needs a new rule (in Musou's code).

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

## 4. Autocannon (Musou Devastator primary)

**Role.** A heavy, slow, hard-hitting gun that kills groups (splash) and overheats quickly. It sits between the heavy bolter (sustained fire) and the lascannon (single big shots).

**What "like the one in the campaign" means in the data.**
- In the campaign the Orks hold a landing zone with a tripod autocannon. Titus can man it (infinite ammo, turret aim clips) or rip the gun off and carry it.
- Carried, it fires full auto from the hip with a cannon recoil. It carries 400 rounds with no reserve and walks like the heavy bolter.

**Reading of D5.** The base is the MP file `csm_mp_exotic_auto_cannon`, the gun's only multiplayer data. Where the campaign's carried gun differs (trigger, rate, rounds, head multiplier, heat, splash), the campaign's handling applies: that is what makes it "function like the one in the campaign". The MP file's damage of 50 is the campaign's 100 at MP scale, so the damage scale stays the multiplayer one. Open question 1 confirms this reading.

**Handling.** A Devastator loadout primary in Musou, picked like the heavy bolter, lascannon and plasma cannon. One clip a life, refilled by ammo drops. It also appears in Musou's drops and reward cards, since those read the class's list.

**Controls.**
- Fire (LMB) shoots, full auto. Aim (RMB) uses `aim_sm_heavy_weapon_mp`.
- Reload (R) does nothing: there is no reload ability and no brace in either file (ours: no brace; open question 2).
- Melee is the Devastator's kick and stomp; the campaign's autocannon strike plays the same kick clip. Hop, sprint and switch follow the heavy bolter's rules. The campaign disables the evade while carrying; the Devastator keeps its hop (ours).

**Numbers.**

| Parameter | Value | Kind / source |
|---|---|---|
| Trigger | full auto | data: campaign `auto_cannon` |
| Damage per round | 50 | data: MP file (equals the campaign's 100 scaled ×0.467) |
| Shot cooldown | 0.22 s | data: campaign |
| Clip | 400, no reserve | data: campaign |
| Head multiplier | 1.5 | data: campaign |
| Range, fall-off | 150 m, none | data: campaign |
| Heat | 3 per round of 100, cools 50/s after 0.5 s: about 33 rounds, 7.3 s of fire to overheat | data: campaign |
| Splash | radius 4 m, ×1 to 2 m, ×0.1 at 4 m, line of sight, 50 at the centre; at the hit point, on Orks and on the level | data shape (campaign); damage scaled ×0.5 like the round (data, scaled) |
| Scatter | none (Musou has no bullet spread) | Musou's existing rule |

For scale (estimate, body shots, no splash): about 227 damage a second for 7 s; the heavy bolter is 150 from the hip and 350 braced. A Boy (85 health) falls to two rounds; the splash takes the Boyz around him.

**Presentation.**

| Piece | Plan | Source |
|---|---|---|
| Model | `turret_aa`, held at `rig_prop_m_chest` like the heavy bolter, stowed at `mkr_stow_bolter` | data (every autocannon wargear) |
| Walk, sprint, hop, fall | the heavy bolter's families (`SM_TB_NAV`, heavy sprint, `SM_EVD_TB_*`) | data: the campaign graph's nav is `SM_TB_NAV` |
| Shot | additive `SM_RNG_CN_FireIdle_S_01` from the campaign rig (same skeleton); fallback the heavy bolter's fire | data (campaign rig) |
| Effects and sound | `autocannon_muzzle`, `heavy_bolter_tracer`, `shell_casing`, the space-marine autocannon hit container, `play_autocannon_turret_fire` | data: campaign file |
| HUD | reticle `autocannon_mc`, ammo `ammo_autocannon_mc`, the round count as the heavy bolter's | data (`Hud.gfx`); the count rule is the owner's heavy-bolter rule |
| Name, description | 104314 "Autocannon", 104315 | data |
| Loyalist name | `sm_mp_exotic_auto_cannon` (ours: the Chaos file's twin, built in code, never written to disk) | ours |

**Missing:** a loyalist MP file (built in code from the Chaos one).

**Later, if wanted (open question 3):** tripod autocannons on Musou maps, as in the campaign (man it, rip it off, carry it until empty, drop it). It would need map placement, an interaction message (an extras-wire addition), the campaign's pickup, drop and turret clips, and the tripod's own graph, which is not in the install.

---

## 5. Lightning claw (decided: one claw plus the pistol)

**Role.** The fastest melee weapon of the Assault: quick strings, lighter hits, and a power field that tears through the big Orks.

**Hands.** The claw is worn on the right hand, where the sword strikes; the bolt pistol stays in the left hand, as with every Assault melee weapon. This is SM1's own layout, so nothing else moves. The difference from the sword: a claw cannot be put away, so it is always worn (ours). The client's existing "always holds its melee weapon" rule does this, applied to the claw instead of to a class.

**Move set.** The power sword's graph and chain (`MP_SM_MEL_PowerSword`):
- the light string X1..X4;
- the heavy strikes Y, X,Y, X,X,Y, X,X,X,Y and the Y,Y stomp;
- the sword's charge (bull rush);
- the pack's dash attack, ground pound and air slash, as the sword's.

**Tempo (open question 4).** Recommended: the same clips played 1.25× faster, with every step timing divided by 1.25 and every damage ×0.8, so a claw does the sword's damage per second against Boyz (ours). The alternative is the sword's own speed with its own numbers. The speed-up is checked first in one still and one clip, because the client must play the clips at the new speed in step with the simulation.

**The claw's edge (open question 5).** Recommended: the power field ignores armour, so claw hits do ×1.5 against the big Orks (Nobs, the Boss Orks and the Warboss; estimate). Alternatives: lightning arcs from each hit to 2 Orks within 4 m for a quarter of the hit (estimate; close to the Librarian's chain lightning), or no edge.

| Parameter | Recommended | Kind / source |
|---|---|---|
| X1..X4 damage | 52 / 56 / 58 / 66 (the sword's 65 / 70 / 73 / 82 ×0.8) | data, scaled (×0.8 with the 1.25× tempo; ours) |
| Heavy strikes, stomp, charge | the power sword's, ×0.8, played 1.25× faster | data, scaled |
| Step timings, windows | the sword's ÷1.25 | data, scaled |
| Against the big Orks | ×1.5 | estimate |
| Musou's melee rules | as every weapon: the cleave, the heal and armour per enemy struck, the stims | Musou's existing rules |
| Sword perks | none of the sword's perks count for the claw at first | ours |

**Presentation.**
- Model: none exists. Recommended: our own mesh built in code: a gauntlet plate over the right hand with four long curved blades, steel with a blue power-field glow, parented to `RIG_R_Hand` (open question 6 asks for models or references).
- Effects: the power sword's field effect on the blades, `power_claw_strike_00` and `power_blade_dmg_00` on a hit, and a thin lightning crackle (`amb_chaos_lightning_wrapping`, recoloured blue) on the blades during strikes.
- Sounds: the power sword's swing and hit events, with `play_capacitor_lightning` layered on a hit (install events, decoded at run time; no downloads).
- Name: "Lightning Claw" (ours, English).

**Missing:** the model, any claw clip.

---

## 6. Power spear (decided: impaling dive plus wide sweeping swings)

**Role.** A two-handed polearm that clears crowds on the ground with wide sweeps and kills one big target from the air with an impaling dive. Slower than the claw, faster than the hammer.

**Grip.** Two-handed on the thunder hammer's set, the only two-handed set in the data (both hands on the shaft); the pistol is kept, as with the hammer.

### 6.1 The wide sweeping swings

The hammer's swings are already wide horizontal sweeps, and its two heavy strikes are the graph's own "Sweeping Attack". The spear keeps that chain and makes it wider and quicker.

| Parameter | Recommended | Kind / source |
|---|---|---|
| Tempo | the hammer's clips and step timings at 1.4× (open question 7) | data, scaled |
| X1..X3 lengths | 1.905 / 1.667 / 1.667 s; hits 0.431 / 0.405 / 0.500 s | data, scaled (the hammer's ÷1.4) |
| X1..X3 damage | 120 / 120 / 140 | estimate (the hammer's 200 ×0.6–0.7) |
| Sweep width (Musou cleave) | a half circle in front (180°) in place of Musou's usual cone, reach +1 m, up to 12 Orks a swing in place of 8 (open question 8) | estimate |
| Heavy strikes | the hammer's interrupt and its two sweeping attacks (stun), sped up the same | data, scaled |
| Y,Y finisher | the hammer's slam at 1.4×, steered as the hammer's; blast r 5 m, 75 + 75 | estimate (the hammer's 7 m, 100 + 125) |
| Charge | the bull rush at ×1.5 damage (112 + 38), stagger | estimate (the data's 75 + 25, stun) |
| Optional half-turn swing | `SM_MEL_TH_XATK_180_MP` (a 180° hammer swing in the MP rig) for a strike at Orks behind | data clip; use: ours, later |

### 6.2 The impaling dive

The jump pack's dive (a melee press in the air, steered to the marked point) lands with the spear driven point-first into the target.

| Parameter | Recommended | Kind / source |
|---|---|---|
| The dive | the pack's own: marker, steering, speed, the 7 m stun blast of 25 + 50 on landing | data and the owner's M44 rule |
| The impale | on landing, the enemy nearest the impact point within 2 m takes **600** (a Nob's whole health: a Nob or anything smaller dies outright) | estimate |
| A survivor | a Boss Ork or the Warboss takes the 600 and is pinned (staggered) for 1 s; the Warboss is stagger-immune and only takes the damage | estimate; the Warboss rule is Musou's |
| Nothing within 2 m | only the pack's blast | ours |
| The air slash's landing | not an impale (open question 9 asks) | ours |
| Jump-pack rules | the hammer's: a hop swing, no pound delay (open question 10) | ours |
| Animation | the hammer's dive and landing clips (`SM_JMP_TH_GP_Loop_01`, `_Impact_01`), the spear turned point-down from the dive's start until the get-up (ours, procedural) | data clips; the turn ours |
| Effect and sound | the hammer's ground-pound effects and sound, plus `power_blade_dmg_00` at the impaled target | data |

Code: the impale is Musou code on the existing `ExtrasJump` hook (`JumpEventKind.Impact`), so it needs no new core hook. The hammer's jump rules need one hook in `Loadouts.WieldsHammer` (W0).

### 6.3 Presentation

- Model: none exists. Recommended: the install's force staff as the shaft (loaded at run time), with a leaf-shaped blade head built in code (ours), held at the chest prop like the hammer.
- Effects: the hammer's power-field effects along the blade.
- Sounds: the power axe's swing and hit events.
- Name: "Power Spear" (ours).

**Missing:** the model, any thrust animation, any spear sound.

---

## 7. Demolition charge (decided: Musou equipment, a short fuse, a big blast)

**Role.** A big bomb on a short fuse: stick it on a Nob, a Boss or a choke point, and the Orks around it fly. One charge, back every 30 s.

**Who carries it.** Every Musou class, appended to the end of each class's equipment list (open question 11). The Librarian may use every equipment item other classes use (the owner's rule in the Librarian pass). If his list is built from the other classes' lists, the charge reaches him by itself; otherwise his session adds it, since that session owns his files.

**Throwing and sticking (D7).**
- The equipment key (G) throws it with the frag's toss (data: animation set `grenade`, cooldown 0.5 s).
- Its flight is the data's: speed 25, gravity ×1 at 1 m and ×2 from 2 m.
- It sticks to the first thing it hits:
  - walls and the floor (data: `sticky`);
  - a Scrap Mek contraption, an Ork, a Boss Ork or the Warboss, and then it moves with them (D7; ours in code).
- It does not stick to marines (ours) and passes them.
- If what it is stuck to dies or is destroyed before the fuse runs out, it drops where that was and the fuse runs on (ours). An Ork thrown by another blast carries it along.

**Fuse (D6).** It goes off **3 s after it sticks** (estimate; the frag's ground fuse is 1 s). A charge that sticks to nothing (thrown into the sky) goes off 6 s after the throw (estimate). It shows the data's fuse effect `ork_frag_grenade_fuse` while it burns. There is no second press, so the data's remote trigger (`detonate_projectile` on the equipment key) is not used.

**Count and refill (D9).**

| Parameter | Value | Kind / source |
|---|---|---|
| Carried | 1 | data (`remote_det_charge`, starts full) |
| Favor of the Armory | 2 | data |
| Back in Musou | one every **30 s** while short (grenades: 15 s) | owner (D9) |
| At a respawn | full | the core's rule |
| Both of Favor's charges out at once | allowed | ours (the data's one-out rule, `det_pack_deployed`, served the remote trigger, which is gone) |
| Reward cards | offered from the class's list | Musou's existing rule |

**Blast (D8 and the owner's ring, 2026-11-10). The install's numbers first, then ours.**

The owner, verbatim: "The explosion for the demotion Charge needs to roll a little bit - one explosion at a time in quick succession in a ring around the charge. Forming a circle for explosion spots at the end."

| | Damage at the centre | Radius | Fall-off (distance → share) | Extra |
|---|---|---|---|---|
| Frag grenade, data (`pp_grenade_multiplayer`) | 200 | 10 m | 2.5 → 1, 2.6 → 0.5, 5 → 0.5, 5.1 → 0.3, 7 → 0.3, 10 → 0.05 | — |
| Frag in Musou today (×2 reach) | 200 | 20 m | 5 → 1, 5.2 → 0.5, 10 → 0.5, 10.2 → 0.3, 14 → 0.3, 20 → 0.05 | — |
| **Charge, data** (`pp_grenade_remote_detonation`) | 200 (two blasts of 100) | 10 m | 4 → 1, 6 → 0.75, 10 → 0.3 | the first throws bodies (force medium), the second staggers |
| Charge, data, Master-Crafted (`…_upgraded`) | 250 (150 + 100) | 15 m | 6 → 1, 9 → 0.75, 15 → 0.3 | as above |

At the same 10 m radius, the data's charge is already the stronger blast:
- it deals the frag's 200, but keeps full damage to 4 m against the frag's 2.5 m;
- at 6 m it still does 0.75, against the frag's 0.3.

But Musou doubles the frag's reach, so the data's charge as it stands would reach half as far as a Musou frag. D8 asks for wider and stronger, hence ours.

**Ours: a centre blast, then a rolling ring** (every value an estimate unless named).

| Part | Value |
|---|---|
| Centre blast | When the fuse runs out, at the charge: **400** (the data's two blasts, ×2), r **10 m**, the data's curve (4 → 1, 6 → 0.75, 10 → 0.3). Boss Orks and the Warboss take ×3 (**1,200**): this is the hit a Boss takes when the charge is stuck on him. Open question 16. |
| Ring | **12 spots** on a circle of **12 m** around the point where the charge went off, 30° apart (6.2 m between neighbours). |
| Order | One at a time, going round clockwise seen from above, from a start angle fixed by the charge's projectile id (id × 137.5°), so the server and every client agree without a message. |
| Pace | The first spot **0.1 s** after the centre blast (3 ticks), then one every **0.067 s** (2 ticks at 30 Hz). The 12th goes off at **0.83 s**: the whole roll lasts under a second. |
| Each spot | A blast of **200**, r **7 m**, the data's curve scaled ×0.7 (2.8 → 1, 4.2 → 0.75, 7 → 0.3), line of sight from the spot. Boss Orks and the Warboss ×3 (600). |
| Who a spot hurts | **Each spot hurts on its own, in its own tick, but an enemy is hurt by the ring only once**: by the first spot that reaches him, at that spot's share (open question 15). The centre blast counts separately. |
| Where a spot sits | The ring point at the charge's height, dropped to the ground below it (a ray down, at most 6 m). If a wall stands between the charge and the ring point, the spot goes off at the wall's face. With no ground within 6 m it goes off in the air. The ring stays where the charge went off even if it was riding an Ork. |
| Thrown Orks | The kills fly away **from the spot that killed them**, so the Orks are thrown outward in a wave that runs round the ring. Up to 40 per charge (open question 12): the whole roll counts as one throw for the cap. Survivors are knocked down and thrown back up to 6 m from their spot; Nobs half; Boss Orks stagger; the Warboss is stagger-immune. |
| Master-Crafted Wargear | The data's own upgrade ratio (damage ×1.25, reach ×1.5) on every part: centre 500 within 15 m, the ring at 18 m with spots of 250 within 10.5 m (open question 13). |
| The circle at the end | Each spot's effect is played long enough (1.0 s, ours) that the first spot is still burning when the last goes off: for a moment the full circle of explosions stands round the charge, then they fade together. |

How far it reaches, on a line through a spot (centre blast + the ring's one hit; between two spots it is at most 6 % less):

| Distance from the charge | Musou frag | Data charge | Ours |
|---|---|---|---|
| 5 m | 200 | 175 | 410 (350 + 60) |
| 10 m | 100 | 60 | 320 (120 + 200) |
| 12 m | 60 | — | 200 |
| 15 m | 52 | — | 193 |
| 19 m | 10 | — | 60 |

What ours does to the Musou Orks (body health from `MusouTypes`):
- **Boyz** (Boy and Shoota Boy 85, Gretchin 30): die out to about 18 m.
- **'Ard Boyz** (170): die out to about 15 m.
- **Nobs** (600): survive anywhere, knocked down and thrown.
- **A Boss Ork** (at least 3,000): loses 1,200 with the charge stuck on him (40 % of the smallest), up to 600 more if he stands in the ring.
- **The Warboss** (15,000): loses the same 1,200 (8 %).

**Effects and sounds per spot (install, Verified-local by blueprint).**

| Piece | Choice | Source |
|---|---|---|
| Centre blast | the data's own: `squig_explosion` (×1.5 in the data; scaled to the 10 m, ours), `play_sm_grenade_explode`, the camera shake | `pp_grenade_remote_detonation` |
| Each spot | the frag's explosion `frag_grenade_explosion` (data: scale 0.6, length 0.6; played at length 1.0 for the closing circle, ours) | `pp_grenade_multiplayer` |
| Each spot's sound | `play_sm_grenade_explode_mp`, the frag's, on every third spot (4 of 12) and none on the rest, so 13 explosions in under a second do not swamp the mix (the owner's sound-overload concern; ours) | `pp_grenade_multiplayer` |
| Not used | `krak_grenade_explosion` (the blind grenade's flash: reads as a blinding flash, not a blast); the melta bomb's `emp_grenade` and lingering `grenadier_sticky_tar` (a slow burn, not a roll) | `pp_grenade_krak_multiplayer`, `pp_grenade_meltabomb_multiplayer` |

**Network budget: no new event.**
- The charge's detonation is already on the wire: the projectile record's detonated flag, kept 3 ticks.
- The client starts the ring from that record, the item index and the projectile id. It places the spots with its own collision world and plays them on the same tick schedule as the server.
- Damage is server-side, spot by spot. The Orks' hits, deaths and throws arrive through Musou's existing entity updates.
- With "each enemy once by the ring", one charge sends as many hit events as a single blast would.
- The ring is fully determined by the charge's position, id and the map, so no spot needs its own message.

**Presentation.**
- Model: the data names the Ork stikkbomb, a stand-in. Recommended: the install's melta bomb model (`melta_bomb.oppc`, a marine item), loaded at run time; checked in the first still (open question 14).
- Effects and sounds: the data's toss sound `play_sm_grenade_toss`, trail `play_frag_grenade_trail_mp` and fuse `ork_frag_grenade_fuse` while it burns; the blast's as in the table above. The throw effect `test_xyz` is a placeholder and is not played.
- Customizer row and HUD: the data's name and description (105800, 105801); the action text 120190 ("to Throw or Detonate") does not fit a fused charge and is not shown. Icon `remotedetonation_mp` if the HUD files hold it, else a placeholder. Name: the install's "Remote Detonators" or the owner's "Demolition Charge" (open question 14).

**Code.**
- The throw, the flight and sticking to the level are the core's existing thrown-item and sticky rules. The charge's `ThrownSpec` is built in code from the data's file with our numbers, and appended only in the Musou profile.
- **No core hook is needed**, now that the second press is gone.
- Musou code (W4):
  - sticking to and riding on Orks and contraptions (the server moves it; the wire already carries its position and attached flag);
  - the fuse from the moment it sticks;
  - the centre blast and the ring's twelve spots on their tick schedule (each enemy once by the ring), the bigger thrown-corpse limit, the knockback from each spot and the ×3 against Bosses;
  - the client's ring: the same spots and schedule from the detonation record, with the effects and sounds above;
  - the 30 s refill (one per-item period in `World.TfwSupply.cs`, which W0 adds).

---

## 8. Milestones and file ownership

| Milestone | Owner | Files (one writer each) | Done when |
|---|---|---|---|
| **W0: the Musou arsenal base** (first; small) | one implementer | `src/SmContent/Extras/Musou/Arsenal/Arsenal.cs` (the registry: four entries, each a stub that adds nothing); one line in the dispatcher `src/SmContent/Extras/GameplayTuning.Extras.cs` (before `MusouLoadout.Prepare`); the core hooks below; the Musou call sites below; `ExtrasBoundaryTests` pins; the docs' section headings | Faithful and extended config hashes byte for byte as before; a Musou config unchanged while the stubs add nothing. Both builds 0 errors; the boundary, Musou isolation and Musou skirmish pin tests pass. |
| **W1: autocannon** | implementer A | `…/Musou/Arsenal/Autocannon.cs` (SmContent: the loyalist twin and its numbers; SmSim: the splash on hit-scan shots), `client/Extras/Musou/Arsenal/Autocannon/*`, `tests/*/Extras/Musou/Arsenal/Autocannon*` | Picked in the Musou loadout, fired, overheated, splash on Orks; player and third-person stills; targeted tests. |
| **W2: lightning claw** | implementer B | `…/Musou/Arsenal/LightningClaw.cs`, `client/Extras/Musou/Arsenal/Claw/*`, `tests/*/Extras/Musou/Arsenal/Claw*` | A string, the heavies, a charge and a dive with the claw; the edge against big Orks; stills; the speed-up checked in a clip against the simulation's hits. |
| **W3: power spear** | implementer C | `…/Musou/Arsenal/PowerSpear.cs`, `client/Extras/Musou/Arsenal/Spear/*`, `tests/*/Extras/Musou/Arsenal/Spear*` | The sweeps hit a half circle; the impaling dive kills a Nob outright; the hammer's jump rules; stills and a clip. |
| **W4: demolition charge** | implementer D | `…/Musou/Arsenal/DemolitionCharge.cs`, `client/Extras/Musou/Arsenal/Charge/*`, `tests/*/Extras/Musou/Arsenal/Charge*` | Throw; stick to the level, a contraption, an Ork, a Boss Ork and the Warboss, riding along; the 3 s fuse; the centre blast and the rolling ring (spots, pace, each enemy once, the closing circle, the same spots on the client as on the server, no new event); the thrown corpses and the knockback; 1 carried (2 with Favor of the Armory), one back every 30 s; stills and a clip. |
| W5 (later, if wanted): tripod autocannons | after W1 | Musou map placement, interaction, carried state, the campaign's pickup, drop and turret clips | Man, rip off, carry, drop. |

**Core hooks W0 adds.** Each is a partial `Extras*` method, pinned and marked `// EXTRAS-HOOK`, a no-op without the Musou flag. The names are proposals.
1. `MpClasses.ExtrasMeleeMap(string wargear, ref WeaponMap?)`: the graph map of an added melee name. The client's `MeleeMapFor` uses it too.
2. `Loadouts.WieldsHammer` → `ExtrasWieldsHammer(..., ref bool)`: the spear takes the hammer's jump-pack rules.
3. `PerkCatalog.ExtrasItems(ref list)`: the customizer rows.
4. Client `WeaponModels` / `WeaponRig`: `ExtrasWeaponModel(string wargear, ref model)`, a model built in code or loaded from another package.
5. Client `ExtrasAnimationAlias(ref string wargear)`: maps an added name to the family it borrows (autocannon → heavy bolter, claw → power sword, spear → thunder hammer). It is applied in the name rules of `HipFireAnimations`, `LocomotionAnimations`, `ReloadAnimations`, `FallAnimations` and the flinch rule, so W1 to W4 touch none of them.
6. If the speed-ups are approved: a clip-speed factor that both the simulation's steps and the client's clip playback read. It works like the Fast Stomp perk's clip speed (`MeleeTuning.ReadSpeedUpgrades`); W0 checks how the client applies that today.

**Musou call sites W0 adds** (each one line calling the registry, in Musou files other sessions also edit; the lead sequences W0 after the current ship and the Musou sessions in flight):
- the cleave's width, reach and target count per weapon (`World.MusouCombat.cs`);
- the hit-scan shot's splash against Orks;
- the pound impact (`ExtrasJump`, Impact) for the impale;
- a sticky projectile meeting an Ork or a contraption (stick and ride along, for the charge), and the charge's fuse from that moment;
- a thrown item's blast rules (the thrown-corpse limit, the knockback, the ×3 against Bosses) in Musou's blast path;
- a per-item grenade refill period in `World.TfwSupply.cs` (15 s for every item as now; 30 s for the charge).

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
| Speeding up melee clips desynchronises the simulation and the client | W0 checks the Fast Stomp path; W2 and W3 compare the simulated hit tick with the clip's contact in a clip. |
| The spear turned point-down for the dive is ours | Shown to the owner in the first still. |
| A charge riding an Ork that dies, is thrown by another blast, or a contraption destroyed under it | W4: it drops where its carrier was and the fuse runs on (ours); a test for each case. |
| 40 flying corpses and 13 explosions in under a second | W4 measures the frame time and the sound mix with a charge in a full Musou crowd; the owner's cap of 20 and fewer spot sounds if it costs too much. |
| The client's ring drifting from the server's | Both place the spots from the same collision world (the map's collision hash is checked at join); W4 compares the spot positions of a server and a client run. The ring is drawn only; its damage comes from the server. |
| Musou damage scale | The new numbers are set against the Musou Orks in a playtest, not only against marines. |
| Other Musou sessions edit the same Musou files | W0's call sites go in after the current ship, sequenced by the lead; W1 to W4 then touch only their own files. |

---

## 10. Open questions (owner)

1. **Autocannon handling:** is the reading right: the MP file's damage (50 a round), with the campaign gun's handling (full auto every 0.22 s, 400 rounds, its slower overheat, its 4 m splash)? Or the MP file's own handling: two-round bursts, 120 rounds, a quick overheat, no splash? Recommended: the campaign's handling.
2. **Autocannon brace:** should it brace on R like the heavy bolter? The data gives it none. Recommended: no.
3. **Tripod autocannons:** campaign-style tripod guns on Musou maps (man, rip off, carry, drop), later? Recommended: not now; after a playtest of the carried gun.
4. **Claw tempo:** the sword's swings played 25 % faster with lighter hits (the same damage per second; recommended)? Or the sword's own speed?
5. **Claw's edge:** ×1.5 against the big Orks (Nobs, Boss Orks, the Warboss; recommended)? Or lightning arcing from each hit to two nearby Orks? Or no edge?
6. **Looks:** the claw always worn on the right gauntlet with four blades and a blue crackle on strikes, and the spear made from the install's force staff with a blade head we build (both recommended)? Do you have models or reference images for either?
7. **Spear tempo:** the hammer's swings played 40 % faster (recommended)? Or the hammer's own pace?
8. **Spear sweeps:** each swing hits a half circle in front, 1 m farther, up to 12 Orks (recommended)? Or wider or narrower?
9. **Impaling dive:** on landing, the enemy nearest the point (within 2 m) takes 600, enough to kill a Nob outright, and the pack's normal stun blast still goes off around it (recommended)? Should the air slash's landing impale too? Recommended: no, only the dive.
10. **Spear and the jump pack:** the hammer's rules (swing during a hop, no delay before a dive; recommended)?
11. **Charge carriers:** every Musou class, the Librarian included (recommended)? Or only some classes?
12. **Charge, flying Orks:** up to 40 corpses fly from one charge (recommended), or your frag limit of 20? And the rest as proposed: a 3 s fuse; 12 spots on a 12 m ring, one every 0.067 s, the roll done in 0.83 s; 200 a spot; Boss Orks and the Warboss taking three times the damage; survivors thrown back up to 6 m?
13. **Charge with Master-Crafted Wargear:** the game file's own upgrade ratio, damage ×1.25 and reach ×1.5, on the centre and the ring (centre 500 within 15 m, the ring at 18 m; recommended)? Or damage only?
14. **Charge look and name:** the install's melta bomb model (a marine item; recommended) or the data's Ork stikkbomb? Called "Remote Detonators" (the install's name) or "Demolition Charge"?
15. **Charge ring, damage per spot:** does each spot hurt on its own, with an Ork hurt only by the first spot that reaches him (recommended: the damage follows the explosions round the ring, and a spot never stacks with its neighbours)? Or do the spots stack on an Ork between two of them? Or are the spots only for the eye, with the damage one blast at the centre?
16. **Charge ring, centre blast:** does the charge itself go off first at the centre (400 within 10 m, 1,200 on a Boss it is stuck to; recommended), with the ring rolling out from it? Or is it the ring alone, so a charge stuck on a Boss hurts him only through the ring?
