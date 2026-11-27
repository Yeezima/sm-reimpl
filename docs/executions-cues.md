# Executions: sound, contact effects and gore (cue sheet, 2026-11-27)

Brief: the owner's brief on execution sound, gore and crossovers (coordination `decisions/2026-11-27-executions-sound-gore-crossovers.md`; card "Exec sound": original events first).
Scope: Warfront and Classic PvP Extended only (the executions flag, `ContentExtras.Executions`). Classic Faithful and standalone Musou never run this code. Eligibility, combat balance,
Classic's one kill and respawn timing, and Warfront's one reward are unchanged. The cues are client presentation only. The one simulation change is the crossover rows in section 3.

## 1. Where the cues come from (supported by evidence)

The original binds each execution's sounds and effects through its animation tags. Nothing in the data is specific to executions. Each sync-kill sequencer, the attacker's and the
victim's, carries Action tags at fixed fractions of the move. `smtool sync-kills --all-tags` lists them; the inventory used to hide the `SFX_*` ones. Each tag names a
`triggered_actions` entry of the entity that plays it:
- the attacker's blueprint (the campaign player `vanguard`);
- its weapon (the campaign `chainsword_new`, `poweraxe`, `thunderhammer_new` or `combat_knife`);
- the victim's blueprint (`boy`, `gretchin`, `chaos_marine_melee`, `chaos_bloodletter` or `chaos_renegade_ig`, then their co-op twins).

Those entries hold three kinds of command:
- `post_sound_event`: start and stop events;
- `post_speech_exertion_event`: the grunts, roars and pain lines;
- `play_fx`: blood sprays, sparks, the decap geyser, and swing trails at weapon or body markers.

The client builds 866 cues over 39 row and weapon lists (720 original bindings, 146 ours). Every sound but 2 is in the banks the match client already loads. The 2 exceptions: `play_sm_sk01_throatgrab` is in no bank of the install, and
`play_sm_chain_sword_grind_pre` (the chainsword's O4) is in none of the client's banks. 29 of the 30 effects compile.

Tools (read-only):
- `smtool exec-cues [--row id] [--all-banks] [--weapon-file f]` prints each row's tags and every file's reaction to them.
- `smtool exec-cues --sheet` prints section 5 the way the client builds it (`ExecutionCueLoader`).

Runtime code: `src/SmContent/Extras/Executions/ExecutionCues.cs` (loader, rules, clock) and `client/Extras/Executions/NetworkView.ExecutionCues.cs` (playback).
Nothing from the install is committed. Like the catalogue rows, this file holds names and times only.

## 2. Playback rules

**Original.** These follow the data's binding; the places marked interpretation are our reading of it.
- Each tag's commands fire when the shared execution clock passes the tag's start. This is the clock the paired clips play on (`ExecutionPresentMath.Pose`). The victim's tags run on
  the same clock, because the victim's clip starts with the attacker's.
- A windowed tag's stop event is posted at the end of its window. An instant tag's stop waits for the end of the move, where the run's loops are ended (interpretation: the action
  ends with the state).
- The data's explicit stop events, such as `stop_sm_sk_chainsword_grind_armor` on `fx_NOBSync_Grind_End`, stop each grind on the emitter that started it.
- `autodetect` speech uses the speaker's own character. That is the attacker's class voice, the victim player's class voice, or an enemy's blueprint voice (`ork_boy_2` for the Boys,
  `chaos_space_marine` for the Chaos marines). A race-swapped loyalist therefore screams in his own voice.
- Effects whose names end in `xx` (`chainsword_blood_swipexx`) are not drawn (interpretation: their authors switched them off).

**Ours.** Each rule has a row in docs/changes-from-original.md and in spec/behavior.md "Added content: execution cues".
- **Three emitters per run.** One sits on the attacker (chest) and one on the victim (its proxy's chest). The third is the **wound**: the victim's chest, or its head for a head, neck,
  throat or decap cue, moved 0.3 m toward the attacker. Impacts, hits, slams, stomps, kicks, grinds, cuts, stabs and the arm break play at the wound. The original posts them on the
  attacker.
- **Speech moved to the victim.** Two kinds of attacker line play on the victim, in the victim's voice: a line in a named character (the grind's `ork_boy_2` effort), and a
  `damage_*` line with no character.
- **No echoed lines.** A line moved from an attacker file is dropped when the victim's own line of the same type plays within 0.1 s (the knife's 0.700 s `damage_big` beside the boy's 0.733 s).
- **Speech fallbacks.** When a character has no recording of a line, the nearest one plays instead. `roar` becomes `effort_big_pull` (the MP marines have no roar). `damage_big` and
  `damage_long` become `damage_small`.
- **Borrowed blades.** This covers the power sword on the chainsword's rows (owner 2026-11-25), the knife on them (owner 2026-11-26), and the knife's arm-break crossover.
  - The wielded weapon's own campaign file is read first. The knife's file has its own cut sounds.
  - Where that file has nothing for a tag, the chainsword's file is used without its motor. Each chainsword-named sound becomes the wielder's own sound:
    - a swing plays the MP wargear's `swing_x1`, else `actn_snd_ck_swing`;
    - a hit or a grind plays the light strike's hit events, repeated every 0.4 s along a grind;
    - a rev or wind-up plays nothing.
  - The same motor rule applies to the wielded weapon's own file on a borrowed move (the knife file keeps a leftover chainsword cut bed, `play_sm_chainsword_sk_cut_start`, and trail).
  - A whoosh is a swing; a blade drawn out (`remove`, `release`) and a wind-up before contact (a `_pre` suffix, O4's `play_sm_chain_sword_grind_pre`) add no hit. Swings play on the attacker, hits and grinds at the wound.
  - The knife's hit is the campaign knife's own `play_combat_knife_stab`, not the MP light strike's `play_sm_fist_hit`.
  - A blade-borne effect whose marker the blade lacks is dropped when it falls more than 0.1 s before the move's first contact (O4's `blood_pre_grind` at 0.300 s): at the wound it would show blood on the victim before anything touches him. From the first contact on it plays at the wound. Dropping was chosen over moving it to the contact: the cut's own blood already plays there.
  - The chainsword's teeth trails are not drawn. Its blood effects stay, placed at the wound when the blade lacks the chainsword's marker.
  - A power sword on a chainsword move therefore sounds like a power sword (`play_sm_power_sword_hit`).
- **Blood.** Blood effects and one gore streak at the kill frame draw only where the match's gore allows: Warfront, or Classic with the Extended gore
  (`ExecutionCueRules.BloodAllowed`). The streak is the current one, reused with its decals (`SpawnStreak`, source sweep), and keeps its own switches (`--old-gore-streak`,
  `--no-gore-streak`). `--no-execution-gore` turns both off for executions. The general blood redesign is untouched.
- **Once everywhere, cancelled on interruption** (`ExecutionCueTimeline`).
  - Each client fires each cue of a run once, whether it is the attacker's, the victim's or an observer's client.
  - On any advance (a run first seen late, or a clock jump mid-move), cues more than 0.25 s behind the clock are skipped, never fired in a burst; stops always apply. The kill streak follows the same rule. A clock that steps back fires nothing again. A closed run revived by the live list plays nothing again.
  - A lost connection interrupts every run (the core's `ExtrasConnectionGone`). The emitters are one-shot, so the sound system forgets them once their voices end.
  - Any end other than Done (Killed, Invalid, Disconnect, MatchEnd), or a run that vanishes, stops all three emitters at once: grinds, motor loops and pending voices. The run's
    effects end with it.
  - A run that ends Done plays its remaining cues out (the victim's half runs longer), then its loops end.
- **Test aids.** `--log-execution-cues` logs each cue. `--no-execution-cues` restores the shipped silent moves. `--no-execution-gore` drops the blood.

## 3. Crossovers

Candidates come from three sources: the shipped coverage matrix (docs/executions-coverage.md 6.1), the brief's first trial, and the Support chat's worksheet (lead 42, 2026-11-27).
The worksheet's candidates are a list only; nobody has watched them.

| Candidate | Source | Status | Reason |
|---|---|---|---|
| O4 arm break with the combat knife, on the Ork Boy kinds | the brief's first trial; Support worksheet (medium) | **accepted** (`O4.combat_knife`, wire 25; lead 42's Fable review of 80b49e09) | Played in the raw-clip test view beside O4 with the chainsword, at normal and quarter speed. Grip: the knife sits in the right hand where the chainsword was; the left hand does the grab, the wrestle and the overhand hit (`ACTN_Ork_Sync_OverhandHit`, `body_smash` at `rig_prop_lhand`). Clearance: the raised blade never meets the Ork. Pose, scale and contact: the same victim half; the slam to the ground reads the same. The chainsword's cut window (0.70-1.40 s) shows no visible blade contact with either weapon, so with the knife its blood plays at the wound (the knife has no `fx_chainsword_spray`). Terrain: the arm break's own footprint and checks, unchanged. |
| O8 stab with the chainsword (and so the power sword), on the Ork Boy kinds | Support worksheet (medium); lead 42 "do try" | **accepted** (`O8.chainsword`, wire 26; lead 42's Fable review of 80b49e09, on condition of the motor fix) | Played in the test view at normal and quarter speed. The weapon hand drives the blade into the base of the Ork's skull (`ACTN_Sync_Head_Stab` at 0.567 s) and draws it out at the kill (`ACTN_Sync_Head_Remove`, 1.200 s); the Ork then staggers away on its own clip. Grip: the chainsword in the right hand as in its own moves. Contact holds; the longer blade stays buried about 0.6 s with no motor under load (the knife file has none to borrow; noted for the owner's ear). Sounds: the knife graph's whoosh plays as the wielder's swing, the stab as its hit, the draw-out as nothing (one hit, not two; Fable review 1). Scale and terrain: unchanged from the knife's O8. |
| O6 axe hack with the chainsword (and so the power sword), on the Ork Boy kinds | Support worksheet (medium-low); lead 42 "do try" | **rejected** (weapon clearance), row removed | Played in the test view at normal and quarter speed. The chop buries the weapon in the Ork's chest and holds it there while turning him (2.6-3.6 s). The axe's short head stays inside the body; the chainsword's long blade sticks out of the Ork's back for most of that hold. Clips: `testview-O6-chainsword-*` beside `testview-O6-power_axe-*`. |
| O3, O9, R1 for the Devastator / Havoc | Support worksheet (high) | rejected (scope) | It would give a finisher to a class that has none today. That changes eligibility, which is outside this brief. It would also need the heavy weapon stowed. |
| O1/O2 grinds, O5 decap, O7 head pop, C1-C3, B1-B3 on other families | Support worksheet, "not recommended" | rejected (desk check) | Each motion depends on its own weapon: grinding teeth, the axe, the two-handed hammer. |

Distinct playable moves, before and after, on the Boy, Shoota Boy, Rokkit Boy, 'Ard Boy and grounded Stormboy (Warfront; also a Classic Ork-player victim):
- the knife goes from 2 (O3, O8) to 3 (O3, O8, O4);
- the chainsword, gold chainsword and power sword go from 4 (O1-O4) to 5 (O1-O4, O8).

No other matchup changes: the axe, hammer and maul, the Gretchin, and every Chaos kind keep their moves. The server's selection and fallback rules are unchanged. A press still makes a seeded pick among the rows that align. The prompt still takes the first row that
fits by priority; the new row has priority 0, after O3 and O8.

## 4. Known gaps (for the owner's playtest)

- **Head cut (2026-11-27, section 6).** C1 and O5 now cut the head at their cut tags; B1, O7, the 'Ard Boy and the Stormboy are left with the geyser and blood only.
- **Silent victim tags.** `ACTN_FX_Boy_Sync_Hit_Ground`, `ACTN_FX_Knee_Ground_Hit`, `ACTN_FX_Death_Ground_Hit`, `ACTN_SND_Gear_Movement` (Chaos marine) and `ACTN_SND_Arm_Break`
  have no command in any file. The original was silent there too (supported by evidence). Nothing of ours is added.
- **Chaos Lord alignment.** The scaled Lord (x1.35) keeps the documented alignment limit: the wound cues follow his scaled proxy's bones, but the attacker's blade does not
  (docs/executions-coverage.md 6.1).
- **Chaos attacker footsteps.** A Chaos attacker's footsteps are the campaign marine's (`play_sm_footfalls_normal`, the data's `vanguard` binding).
- **Untuned mix.** The original sync-kill events play at the data's levels against our louder MP mix (sound/louder-farther). The owner has not heard the result yet.

## 5. The cue sheet (generated: `smtool exec-cues --sheet`)

Columns:
- **s**: time on the shared execution clock, in seconds; a span for a windowed tag.
- **side**: whose tag sets the cue off.
- **kind**: sound, stop, speech, effect, or motor (a borrowed blade's motor sound, replaced by the wielder's).
- **place**: where it plays.
- **tag**, **event / effect** (with the effect's marker), and **file**: the triggered action that holds the cue.
- The last column notes events missing from the client's banks.

"(ours)" marks a cue moved to the wound or to the victim, or a motor replacement. Each row's heading gives its kill time, the primary hit. The contacts before and after it are the
`hit`, `impact`, `grind` and `cut` lines around that time.

#### B1.chainsword (kill 3.100 s, end 3.933 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.106 | attacker | sound | attacker | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4` | chainsword_new |  |
| 0.133 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.159-0.505 | attacker | sound | attacker | `ACTN_Sync_Chainsword_Swing` | `play_sm_chainsword_swing_x4` | chainsword_new |  |
| 0.159 | attacker | speech | attacker | `ACTN_Sync_Chainsword_Swing` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 0.159-0.505 | attacker | effect | attacker | `ACTN_Sync_Chainsword_Swing` | `chainsword_swing_heavy at fx_swing` | chainsword_new |  |
| 0.292-0.558 | attacker | sound | attacker | `fx_BL_Sync_SwingHit` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 0.292-0.558 | attacker | sound (ours) | wound | `fx_BL_Sync_SwingHit` | `play_sm_sk_chainsword_impact` | chainsword_new |  |
| 0.292 | attacker | speech (ours) | victim | `fx_BL_Sync_SwingHit` | `speech  damage_long` | chainsword_new |  |
| 0.292-0.558 | attacker | effect | attacker | `fx_BL_Sync_SwingHit` | `chainsword_swing_blood at fx_swing (blood)` | chainsword_new |  |
| 0.292-0.558 | attacker | effect | attacker | `fx_BL_Sync_SwingHit` | `chainsword_dmg at fx_swing` | chainsword_new |  |
| 0.292-0.558 | attacker | effect | attacker | `fx_BL_Sync_SwingHit` | `chainsword_blood_swipe at fx_chainsword_spray (blood)` | chainsword_new |  |
| 0.478 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.505 | attacker | stop | attacker | `ACTN_Sync_Chainsword_Swing` | `stop_sm_chainsword_swing` | chainsword_new |  |
| 0.967 | victim | sound | victim | `ACTN_SND_Left_Hand_Hit_Ground` | `play_ch_bloodletter_footstep` | chaos_bloodletter |  |
| 1.100 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.500 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.800 | attacker | sound | attacker | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4` | chainsword_new |  |
| 2.206-3.163 | attacker | sound (ours) | wound | `ACTN_Bloodletter_Sync_SwordGrind` | `play_sm_sk_chainsword_hit_flesh` | chainsword_new |  |
| 2.206-3.163 | attacker | sound (ours) | wound | `ACTN_Bloodletter_Sync_SwordGrind` | `play_sm_sk_chainsword_grind_flesh` | chainsword_new |  |
| 2.206 | attacker | speech | attacker | `ACTN_Bloodletter_Sync_SwordGrind` | `speech autodetect effort_build` | chainsword_new |  |
| 2.206-3.163 | attacker | effect | attacker | `ACTN_Bloodletter_Sync_SwordGrind` | `chainsword_grind_fire at fx_chainsword_spray` | chainsword_new |  |
| 2.206-3.163 | attacker | effect | attacker | `ACTN_Bloodletter_Sync_SwordGrind` | `chainsword_blood_grind_long at fx_chainsword_spray (blood)` | chainsword_new |  |
| 3.083-3.163 | attacker | stop (ours) | wound | `ACTN_Bloodletter_Sync_HeadSawOff` | `stop_sm_sk_chainsword_grind_flesh` | chainsword_new |  |
| 3.083 | attacker | speech | attacker | `ACTN_Bloodletter_Sync_HeadSawOff` | `speech autodetect roar` | chainsword_new |  |
| 3.083-3.163 | attacker | effect | attacker | `ACTN_Bloodletter_Sync_HeadSawOff` | `chainsword_swipe_fire at fx_swing` | chainsword_new |  |
| 3.083-3.163 | attacker | effect | attacker | `ACTN_Bloodletter_Sync_HeadSawOff` | `chainsword_swing_heavy at fx_swing` | chainsword_new |  |
| 3.100 | victim | sound | victim | `ACTN_Death_Vanish` | `play_ch_bloodletter_death` | chaos_bloodletter |  |
| 3.100 | victim | effect | victim | `ACTN_Death_Vanish` | `bloodletter_limb_vanish at rig_lhand` | chaos_bloodletter |  |
| 3.100 | victim | effect | victim | `ACTN_Death_Vanish` | `bloodletter_limb_vanish_disable at rig_lcalf` | chaos_bloodletter |  |
| 3.109-3.614 | victim | effect | victim | `ACTN_Death_Start` | `bloodletter_limb_burn at rig_head` | chaos_bloodletter |  |
| 3.267 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 3.400 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 3.833 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### B1.chainsword with the power sword (kill 3.100 s, end 3.933 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.106 | attacker | motor (ours) | wound | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4 -> the wielder's rev` | chainsword_new |  |
| 0.133 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.159 | attacker | speech | attacker | `ACTN_Sync_Chainsword_Swing` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 0.159-0.505 | attacker | motor (ours) | attacker | `ACTN_Sync_Chainsword_Swing` | `play_sm_chainsword_swing_x4 -> the wielder's swing` | chainsword_new |  |
| 0.292-0.558 | attacker | sound | attacker | `fx_BL_Sync_SwingHit` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 0.292 | attacker | speech (ours) | victim | `fx_BL_Sync_SwingHit` | `speech  damage_long` | chainsword_new |  |
| 0.292-0.558 | attacker | effect | attacker | `fx_BL_Sync_SwingHit` | `chainsword_swing_blood at fx_swing (blood)` | chainsword_new |  |
| 0.292-0.558 | attacker | effect | attacker | `fx_BL_Sync_SwingHit` | `chainsword_blood_swipe at fx_chainsword_spray (blood)` | chainsword_new |  |
| 0.292-0.558 | attacker | motor (ours) | wound | `fx_BL_Sync_SwingHit` | `play_sm_sk_chainsword_impact -> the wielder's hit` | chainsword_new |  |
| 0.478 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.967 | victim | sound | victim | `ACTN_SND_Left_Hand_Hit_Ground` | `play_ch_bloodletter_footstep` | chaos_bloodletter |  |
| 1.100 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.500 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.800 | attacker | motor (ours) | wound | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4 -> the wielder's rev` | chainsword_new |  |
| 2.206 | attacker | speech | attacker | `ACTN_Bloodletter_Sync_SwordGrind` | `speech autodetect effort_build` | chainsword_new |  |
| 2.206-3.163 | attacker | effect | attacker | `ACTN_Bloodletter_Sync_SwordGrind` | `chainsword_blood_grind_long at fx_chainsword_spray (blood)` | chainsword_new |  |
| 2.206-3.163 | attacker | motor (ours) | wound | `ACTN_Bloodletter_Sync_SwordGrind` | `play_sm_sk_chainsword_hit_flesh -> the wielder's hit` | chainsword_new |  |
| 2.206-3.163 | attacker | motor (ours) | wound | `ACTN_Bloodletter_Sync_SwordGrind` | `play_sm_sk_chainsword_grind_flesh -> the wielder's grind` | chainsword_new |  |
| 3.083 | attacker | speech | attacker | `ACTN_Bloodletter_Sync_HeadSawOff` | `speech autodetect roar` | chainsword_new |  |
| 3.100 | victim | sound | victim | `ACTN_Death_Vanish` | `play_ch_bloodletter_death` | chaos_bloodletter |  |
| 3.100 | victim | effect | victim | `ACTN_Death_Vanish` | `bloodletter_limb_vanish at rig_lhand` | chaos_bloodletter |  |
| 3.100 | victim | effect | victim | `ACTN_Death_Vanish` | `bloodletter_limb_vanish_disable at rig_lcalf` | chaos_bloodletter |  |
| 3.109-3.614 | victim | effect | victim | `ACTN_Death_Start` | `bloodletter_limb_burn at rig_head` | chaos_bloodletter |  |
| 3.267 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 3.400 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 3.833 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### B1.chainsword with the combat knife (kill 3.100 s, end 3.933 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.106 | attacker | motor (ours) | wound | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4 -> the wielder's rev` | chainsword_new |  |
| 0.133 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.159 | attacker | speech | attacker | `ACTN_Sync_Chainsword_Swing` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 0.159-0.505 | attacker | motor (ours) | attacker | `ACTN_Sync_Chainsword_Swing` | `play_sm_chainsword_swing_x4 -> the wielder's swing` | chainsword_new |  |
| 0.292-0.558 | attacker | sound | attacker | `fx_BL_Sync_SwingHit` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 0.292 | attacker | speech (ours) | victim | `fx_BL_Sync_SwingHit` | `speech  damage_long` | chainsword_new |  |
| 0.292-0.558 | attacker | effect | attacker | `fx_BL_Sync_SwingHit` | `chainsword_swing_blood at fx_swing (blood)` | chainsword_new |  |
| 0.292-0.558 | attacker | effect | attacker | `fx_BL_Sync_SwingHit` | `chainsword_blood_swipe at fx_chainsword_spray (blood)` | chainsword_new |  |
| 0.292-0.558 | attacker | motor (ours) | wound | `fx_BL_Sync_SwingHit` | `play_sm_sk_chainsword_impact -> the wielder's hit` | chainsword_new |  |
| 0.478 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.967 | victim | sound | victim | `ACTN_SND_Left_Hand_Hit_Ground` | `play_ch_bloodletter_footstep` | chaos_bloodletter |  |
| 1.100 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.500 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.800 | attacker | motor (ours) | wound | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4 -> the wielder's rev` | chainsword_new |  |
| 2.206 | attacker | speech | attacker | `ACTN_Bloodletter_Sync_SwordGrind` | `speech autodetect effort_build` | chainsword_new |  |
| 2.206-3.163 | attacker | effect | attacker | `ACTN_Bloodletter_Sync_SwordGrind` | `chainsword_blood_grind_long at fx_chainsword_spray (blood)` | chainsword_new |  |
| 2.206-3.163 | attacker | motor (ours) | wound | `ACTN_Bloodletter_Sync_SwordGrind` | `play_sm_sk_chainsword_hit_flesh -> the wielder's hit` | chainsword_new |  |
| 2.206-3.163 | attacker | motor (ours) | wound | `ACTN_Bloodletter_Sync_SwordGrind` | `play_sm_sk_chainsword_grind_flesh -> the wielder's grind` | chainsword_new |  |
| 3.083 | attacker | speech | attacker | `ACTN_Bloodletter_Sync_HeadSawOff` | `speech autodetect roar` | chainsword_new |  |
| 3.100 | victim | sound | victim | `ACTN_Death_Vanish` | `play_ch_bloodletter_death` | chaos_bloodletter |  |
| 3.100 | victim | effect | victim | `ACTN_Death_Vanish` | `bloodletter_limb_vanish at rig_lhand` | chaos_bloodletter |  |
| 3.100 | victim | effect | victim | `ACTN_Death_Vanish` | `bloodletter_limb_vanish_disable at rig_lcalf` | chaos_bloodletter |  |
| 3.109-3.614 | victim | effect | victim | `ACTN_Death_Start` | `bloodletter_limb_burn at rig_head` | chaos_bloodletter |  |
| 3.267 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 3.400 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 3.833 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### B2.power_axe (kill 2.074 s, end 3.600 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.430 | attacker | sound | attacker | `ACTN_Bloodletter_Sync_TongueGrab` | `play_sync_grab` | poweraxe |  |
| 0.430 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.430 | attacker | speech | attacker | `ACTN_Bloodletter_Sync_TongueGrab` | `speech autodetect roar` | poweraxe |  |
| 1.252 | attacker | sound (ours) | wound | `ACTN_Bloodletter_Sync_ThrowDown` | `play_sync_slam_bloodletter` | poweraxe |  |
| 1.252 | attacker | speech | attacker | `ACTN_Bloodletter_Sync_ThrowDown` | `speech autodetect roar` | poweraxe |  |
| 1.643 | attacker | sound | attacker | `ACTN_Sync_Poweraxe_Antic` | `play_sm_power_axe_antic` | poweraxe |  |
| 1.643 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_antic_light at fx_swing` | poweraxe |  |
| 1.643 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 1.976-2.598 | victim | effect | victim | `ACTN_Death_Start` | `bloodletter_limb_burn at rig_head` | chaos_bloodletter |  |
| 2.100-2.200 | attacker | sound | attacker | `ACTN_Sync_ Poweraxe _Swing` | `play_sm_power_axe_swing` | poweraxe |  |
| 2.100 | attacker | speech | attacker | `ACTN_Sync_ Poweraxe _Swing` | `speech autodetect effort_short_stab` | poweraxe |  |
| 2.100-2.200 | attacker | effect | attacker | `ACTN_Sync_ Poweraxe _Swing` | `poweraxe_swing_light at fx_swing` | poweraxe |  |
| 2.100-2.200 | attacker | effect | attacker | `ACTN_Sync_ Poweraxe _Swing` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 2.200 | attacker | sound (ours) | wound | `ACTN_Bloodletter_Sync_AxHit` | `play_sm_sk_power_axe_hit_flesh` | poweraxe |  |
| 2.200 | attacker | stop | attacker | `ACTN_Sync_ Poweraxe _Swing` | `stop_sm_power_axe_swing` | poweraxe |  |
| 2.200 | attacker | effect | attacker | `ACTN_Bloodletter_Sync_AxHit` | `chainsword_blood_rip_out at fx_swing (blood)` | poweraxe |  |
| 2.200 | attacker | effect | attacker | `ACTN_Bloodletter_Sync_AxHit` | `poweraxe_dmg_body at fx_swing` | poweraxe |  |
| 2.562 | victim | sound | victim | `ACTN_Death_Vanish` | `play_ch_bloodletter_death` | chaos_bloodletter |  |
| 2.562 | victim | effect | victim | `ACTN_Death_Vanish` | `bloodletter_limb_vanish at rig_lhand` | chaos_bloodletter |  |
| 2.562 | victim | effect | victim | `ACTN_Death_Vanish` | `bloodletter_limb_vanish_disable at rig_lcalf` | chaos_bloodletter |  |
| 2.896-3.287 | attacker | sound (ours) | wound | `ACTN_Bloodletter_Sync_AxRelease` | `play_sm_sk_power_axe_release_flesh` | poweraxe |  |
| 2.896-3.287 | attacker | sound | attacker | `ACTN_Bloodletter_Sync_AxRelease` | `play_sm_power_axe_swing` | poweraxe |  |
| 2.896 | attacker | speech | attacker | `ACTN_Bloodletter_Sync_AxRelease` | `speech autodetect effort_short_stab` | poweraxe |  |
| 2.896-3.287 | attacker | effect | attacker | `ACTN_Bloodletter_Sync_AxRelease` | `chainsword_blood_swipe at fx_swing (blood)` | poweraxe |  |
| 2.974 | attacker | sound | attacker | `ACTN_Sync_Poweraxe_Antic` | `play_sm_power_axe_antic` | poweraxe |  |
| 2.974 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_antic_light at fx_swing` | poweraxe |  |
| 2.974 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 3.287 | attacker | stop | attacker | `ACTN_Bloodletter_Sync_AxRelease` | `stop_sm_power_axe_swing` | poweraxe |  |
| 3.483 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### B3.thunder_hammer (kill 2.267 s, end 3.667 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.043 | attacker | sound | attacker | `ACTN_Sync_Thunderhammer_Antic` | `play_sm_thunder_hammer_antic_sync` | thunderhammer_new |  |
| 0.043 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Antic` | `thunderhammer_antic_light_face at fx_face01` | thunderhammer_new |  |
| 0.043 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Antic` | `thunderhammer_antic_light_side at fx_side01` | thunderhammer_new |  |
| 0.043 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Antic` | `thunderhammer_antic_light_center at fx_swing` | thunderhammer_new |  |
| 0.129 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 0.440-2.102 | victim | effect | victim | `ACTN_Death_Start` | `bloodletter_limb_burn at rig_head` | chaos_bloodletter |  |
| 0.700 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.733 | attacker | sound (ours) | wound | `ACTN_Bloodletter_Sync_HammerImpact` | `play_sm_sk_thunderhammer_hit_flesh` | thunderhammer_new |  |
| 0.733 | attacker | effect | attacker | `ACTN_Bloodletter_Sync_HammerImpact` | `thunderhammer_dmg_body_secondary at fx_swing` | thunderhammer_new |  |
| 0.733 | attacker | effect | attacker | `ACTN_Bloodletter_Sync_HammerImpact` | `thunderhammer_dmg_body at fx_swing` | thunderhammer_new |  |
| 1.510 | attacker | sound | attacker | `ACTN_Sync_Thunderhammer_Antic` | `play_sm_thunder_hammer_antic_sync` | thunderhammer_new |  |
| 1.510 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Antic` | `thunderhammer_antic_light_face at fx_face01` | thunderhammer_new |  |
| 1.510 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Antic` | `thunderhammer_antic_light_side at fx_side01` | thunderhammer_new |  |
| 1.510 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Antic` | `thunderhammer_antic_light_center at fx_swing` | thunderhammer_new |  |
| 1.769-1.941 | attacker | sound | attacker | `ACTN_Sync_Thunderhammer_Swing` | `play_sm_thunder_hammer_swing_heavy` | thunderhammer_new |  |
| 1.769 | attacker | speech | attacker | `ACTN_Sync_Thunderhammer_Swing` | `speech autodetect roar` | thunderhammer_new |  |
| 1.769-1.941 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Swing` | `thunderhammer_swing_light at fx_swing` | thunderhammer_new |  |
| 1.941 | attacker | stop | attacker | `ACTN_Sync_Thunderhammer_Swing` | `stop_sm_thunder_hammer_swing_heavy` | thunderhammer_new |  |
| 2.167 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 2.300 | attacker | sound (ours) | wound | `ACTN_Bloodletter_Sync_HammerImpact` | `play_sm_sk_thunderhammer_hit_flesh` | thunderhammer_new |  |
| 2.300 | attacker | effect | attacker | `ACTN_Bloodletter_Sync_HammerImpact` | `thunderhammer_dmg_body_secondary at fx_swing` | thunderhammer_new |  |
| 2.300 | attacker | effect | attacker | `ACTN_Bloodletter_Sync_HammerImpact` | `thunderhammer_dmg_body at fx_swing` | thunderhammer_new |  |
| 2.433 | victim | sound | victim | `ACTN_Death_Vanish` | `play_ch_bloodletter_death` | chaos_bloodletter |  |
| 2.433 | victim | effect | victim | `ACTN_Death_Vanish` | `bloodletter_limb_vanish at rig_lhand` | chaos_bloodletter |  |
| 2.433 | victim | effect | victim | `ACTN_Death_Vanish` | `bloodletter_limb_vanish_disable at rig_lcalf` | chaos_bloodletter |  |
| 3.167 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### C1.chainsword (kill 4.367 s, end 6.333 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.071 | attacker | sound | attacker | `ACTN_Sync_GearMove` | `play_sm_sk_foley_movement` | vanguard |  |
| 0.533-0.700 | attacker | sound | attacker | `ACTN_Sync_Chainsword_Swing` | `play_sm_chainsword_swing_x4` | chainsword_new |  |
| 0.533 | attacker | speech | attacker | `ACTN_Sync_Chainsword_Swing` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 0.533-0.700 | attacker | effect | attacker | `ACTN_Sync_Chainsword_Swing` | `chainsword_swing_heavy at fx_swing` | chainsword_new |  |
| 0.676 | attacker | sound | attacker | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4` | chainsword_new |  |
| 0.700 | attacker | sound (ours) | wound | `ACTN_CSM_Sync_SwordHit` | `play_sm_sk_chainsword_hit_armor` | chainsword_new |  |
| 0.700 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.700 | attacker | stop | attacker | `ACTN_Sync_Chainsword_Swing` | `stop_sm_chainsword_swing` | chainsword_new |  |
| 0.700 | attacker | effect | attacker | `ACTN_CSM_Sync_Sword_Impact_Chaos` | `cl_talon_stab at fx_chainsword_spray` | chainsword_new |  |
| 0.700 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | chaos_marine_melee |  |
| 0.867-1.500 | attacker | sound (ours) | wound | `ACTN_CSM_Sync_SwordGrind` | `play_sm_sk_chainsword_grind_armor` | chainsword_new |  |
| 0.867 | attacker | speech | attacker | `ACTN_CSM_Sync_SwordGrind` | `speech autodetect effort_build` | chainsword_new |  |
| 0.867 | attacker | speech | attacker | `ACTN_CSM_Sync_Sword_Grind_Chaos` | `speech autodetect roar` | chainsword_new |  |
| 0.867-1.400 | attacker | effect | attacker | `ACTN_CSM_Sync_Sword_Grind_Chaos` | `chainsword_blood_grind_long at fx_chainsword_spray (blood)` | chainsword_new |  |
| 0.867-1.400 | attacker | effect | attacker | `ACTN_CSM_Sync_Sword_Grind_Chaos` | `chainsword_armor_swipe at fx_chainsword_spray` | chainsword_new |  |
| 0.867-1.400 | attacker | effect | attacker | `ACTN_CSM_Sync_Sword_Grind_Chaos` | `chainsword_grind_chaos at fx_chainsword_spray` | chainsword_new |  |
| 1.167 | victim | sound | victim | `EVT_Left_Foot` | `play_ch_marine_footstep` | chaos_marine_melee |  |
| 1.367 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.433-1.533 | attacker | stop (ours) | wound | `fx_NOBSync_Grind_End` | `stop_sm_sk_chainsword_grind_flesh` | chainsword_new |  |
| 1.433-1.533 | attacker | stop (ours) | wound | `fx_NOBSync_Grind_End` | `stop_sm_sk_chainsword_grind_armor` | chainsword_new |  |
| 1.433 | attacker | speech (ours) | victim | `fx_NOBSync_Grind_End` | `speech  damage_small` | chainsword_new |  |
| 1.433-1.533 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_blood_swipe at fx_chainsword_spray (blood)` | chainsword_new |  |
| 1.433-1.533 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_blood_rip_out at fx_swing (blood)` | chainsword_new |  |
| 1.433-1.533 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_swing_blood at fx_swing (blood)` | chainsword_new |  |
| 1.733 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.933 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 2.000 | attacker | sound | attacker | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4` | chainsword_new |  |
| 2.267-2.533 | attacker | sound | attacker | `ACTN_Sync_Chainsword_Swing` | `play_sm_chainsword_swing_x4` | chainsword_new |  |
| 2.267 | attacker | speech | attacker | `ACTN_Sync_Chainsword_Swing` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 2.267-2.533 | attacker | effect | attacker | `ACTN_Sync_Chainsword_Swing` | `chainsword_swing_heavy at fx_swing` | chainsword_new |  |
| 2.533 | attacker | sound (ours) | wound | `ACTN_CSM_Sync_Sword_Impact_Chaos` | `play_sm_sk_chainsword_hit_armor` | chainsword_new |  |
| 2.533 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 2.533 | attacker | stop | attacker | `ACTN_Sync_Chainsword_Swing` | `stop_sm_chainsword_swing` | chainsword_new |  |
| 2.533 | attacker | effect | attacker | `ACTN_CSM_Sync_Sword_Impact_Chaos` | `cl_talon_stab at fx_chainsword_spray` | chainsword_new |  |
| 2.533 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | chaos_marine_melee |  |
| 2.567 | attacker | sound (ours) | wound | `ACTN_CSM_Sync_SwordHit` | `play_sm_sk_chainsword_hit_armor` | chainsword_new |  |
| 2.600-3.333 | attacker | sound (ours) | wound | `ACTN_CSM_Sync_SwordGrind` | `play_sm_sk_chainsword_grind_armor` | chainsword_new |  |
| 2.600 | attacker | speech | attacker | `ACTN_CSM_Sync_SwordGrind` | `speech autodetect effort_build` | chainsword_new |  |
| 2.600 | attacker | speech | attacker | `ACTN_CSM_Sync_Sword_Grind_Chaos` | `speech autodetect roar` | chainsword_new |  |
| 2.600-4.233 | attacker | effect | attacker | `ACTN_CSM_Sync_Sword_Grind_Chaos` | `chainsword_blood_grind_long at fx_chainsword_spray (blood)` | chainsword_new |  |
| 2.600-4.233 | attacker | effect | attacker | `ACTN_CSM_Sync_Sword_Grind_Chaos` | `chainsword_armor_swipe at fx_chainsword_spray` | chainsword_new |  |
| 2.600-4.233 | attacker | effect | attacker | `ACTN_CSM_Sync_Sword_Grind_Chaos` | `chainsword_grind_chaos at fx_chainsword_spray` | chainsword_new |  |
| 3.901 | attacker | sound | attacker | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4` | chainsword_new |  |
| 4.233-4.767 | attacker | stop (ours) | wound | `fx_NOBSync_Grind_End` | `stop_sm_sk_chainsword_grind_flesh` | chainsword_new |  |
| 4.233-4.767 | attacker | stop (ours) | wound | `fx_NOBSync_Grind_End` | `stop_sm_sk_chainsword_grind_armor` | chainsword_new |  |
| 4.233 | attacker | speech (ours) | victim | `fx_NOBSync_Grind_End` | `speech  damage_small` | chainsword_new |  |
| 4.233-4.767 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_blood_swipe at fx_chainsword_spray (blood)` | chainsword_new |  |
| 4.233-4.767 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_blood_rip_out at fx_swing (blood)` | chainsword_new |  |
| 4.233-4.767 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_swing_blood at fx_swing (blood)` | chainsword_new |  |
| 4.267 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 4.267 | attacker | stop (ours) | wound | `ACTN_CSM_Sync_GrindFinish` | `stop_sm_sk_chainsword_grind_armor` | chainsword_new |  |
| 4.300 | victim | effect | victim | `ACTN_FX_CS_Sync_Blood_Geyser` | `chainsword_grind_chaos at fx_decap_geyser` | chaos_marine_melee |  |
| 4.300 | victim | effect | victim | `ACTN_FX_CS_Sync_Blood_Geyser` | `chainsword_blood_rip_out at fx_decap_geyser (blood)` | chaos_marine_melee |  |
| 4.367 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 5.533 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### C1.chainsword with the power sword (kill 4.367 s, end 6.333 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.071 | attacker | sound | attacker | `ACTN_Sync_GearMove` | `play_sm_sk_foley_movement` | vanguard |  |
| 0.533 | attacker | speech | attacker | `ACTN_Sync_Chainsword_Swing` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 0.533-0.700 | attacker | motor (ours) | attacker | `ACTN_Sync_Chainsword_Swing` | `play_sm_chainsword_swing_x4 -> the wielder's swing` | chainsword_new |  |
| 0.676 | attacker | motor (ours) | wound | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4 -> the wielder's rev` | chainsword_new |  |
| 0.700 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.700 | attacker | effect | attacker | `ACTN_CSM_Sync_Sword_Impact_Chaos` | `cl_talon_stab at fx_chainsword_spray` | chainsword_new |  |
| 0.700 | attacker | motor (ours) | wound | `ACTN_CSM_Sync_SwordHit` | `play_sm_sk_chainsword_hit_armor -> the wielder's hit` | chainsword_new |  |
| 0.700 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | chaos_marine_melee |  |
| 0.867 | attacker | speech | attacker | `ACTN_CSM_Sync_SwordGrind` | `speech autodetect effort_build` | chainsword_new |  |
| 0.867 | attacker | speech | attacker | `ACTN_CSM_Sync_Sword_Grind_Chaos` | `speech autodetect roar` | chainsword_new |  |
| 0.867-1.400 | attacker | effect | attacker | `ACTN_CSM_Sync_Sword_Grind_Chaos` | `chainsword_blood_grind_long at fx_chainsword_spray (blood)` | chainsword_new |  |
| 0.867-1.500 | attacker | motor (ours) | wound | `ACTN_CSM_Sync_SwordGrind` | `play_sm_sk_chainsword_grind_armor -> the wielder's grind` | chainsword_new |  |
| 1.167 | victim | sound | victim | `EVT_Left_Foot` | `play_ch_marine_footstep` | chaos_marine_melee |  |
| 1.367 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.433 | attacker | speech (ours) | victim | `fx_NOBSync_Grind_End` | `speech  damage_small` | chainsword_new |  |
| 1.433-1.533 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_blood_swipe at fx_chainsword_spray (blood)` | chainsword_new |  |
| 1.433-1.533 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_blood_rip_out at fx_swing (blood)` | chainsword_new |  |
| 1.433-1.533 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_swing_blood at fx_swing (blood)` | chainsword_new |  |
| 1.733 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.933 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 2.000 | attacker | motor (ours) | wound | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4 -> the wielder's rev` | chainsword_new |  |
| 2.267 | attacker | speech | attacker | `ACTN_Sync_Chainsword_Swing` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 2.267-2.533 | attacker | motor (ours) | attacker | `ACTN_Sync_Chainsword_Swing` | `play_sm_chainsword_swing_x4 -> the wielder's swing` | chainsword_new |  |
| 2.533 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 2.533 | attacker | effect | attacker | `ACTN_CSM_Sync_Sword_Impact_Chaos` | `cl_talon_stab at fx_chainsword_spray` | chainsword_new |  |
| 2.533 | attacker | motor (ours) | wound | `ACTN_CSM_Sync_Sword_Impact_Chaos` | `play_sm_sk_chainsword_hit_armor -> the wielder's hit` | chainsword_new |  |
| 2.533 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | chaos_marine_melee |  |
| 2.567 | attacker | motor (ours) | wound | `ACTN_CSM_Sync_SwordHit` | `play_sm_sk_chainsword_hit_armor -> the wielder's hit` | chainsword_new |  |
| 2.600 | attacker | speech | attacker | `ACTN_CSM_Sync_SwordGrind` | `speech autodetect effort_build` | chainsword_new |  |
| 2.600 | attacker | speech | attacker | `ACTN_CSM_Sync_Sword_Grind_Chaos` | `speech autodetect roar` | chainsword_new |  |
| 2.600-4.233 | attacker | effect | attacker | `ACTN_CSM_Sync_Sword_Grind_Chaos` | `chainsword_blood_grind_long at fx_chainsword_spray (blood)` | chainsword_new |  |
| 2.600-3.333 | attacker | motor (ours) | wound | `ACTN_CSM_Sync_SwordGrind` | `play_sm_sk_chainsword_grind_armor -> the wielder's grind` | chainsword_new |  |
| 3.901 | attacker | motor (ours) | wound | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4 -> the wielder's rev` | chainsword_new |  |
| 4.233 | attacker | speech (ours) | victim | `fx_NOBSync_Grind_End` | `speech  damage_small` | chainsword_new |  |
| 4.233-4.767 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_blood_swipe at fx_chainsword_spray (blood)` | chainsword_new |  |
| 4.233-4.767 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_blood_rip_out at fx_swing (blood)` | chainsword_new |  |
| 4.233-4.767 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_swing_blood at fx_swing (blood)` | chainsword_new |  |
| 4.267 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 4.300 | victim | effect | victim | `ACTN_FX_CS_Sync_Blood_Geyser` | `chainsword_grind_chaos at fx_decap_geyser` | chaos_marine_melee |  |
| 4.300 | victim | effect | victim | `ACTN_FX_CS_Sync_Blood_Geyser` | `chainsword_blood_rip_out at fx_decap_geyser (blood)` | chaos_marine_melee |  |
| 4.367 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 5.533 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### C1.chainsword with the combat knife (kill 4.367 s, end 6.333 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.071 | attacker | sound | attacker | `ACTN_Sync_GearMove` | `play_sm_sk_foley_movement` | vanguard |  |
| 0.533 | attacker | speech | attacker | `ACTN_Sync_Chainsword_Swing` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 0.533-0.700 | attacker | motor (ours) | attacker | `ACTN_Sync_Chainsword_Swing` | `play_sm_chainsword_swing_x4 -> the wielder's swing` | chainsword_new |  |
| 0.676 | attacker | motor (ours) | wound | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4 -> the wielder's rev` | chainsword_new |  |
| 0.700 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.700 | attacker | effect | attacker | `ACTN_CSM_Sync_Sword_Impact_Chaos` | `cl_talon_stab at fx_chainsword_spray` | chainsword_new |  |
| 0.700 | attacker | motor (ours) | wound | `ACTN_CSM_Sync_SwordHit` | `play_sm_sk_chainsword_hit_armor -> the wielder's hit` | chainsword_new |  |
| 0.700 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | chaos_marine_melee |  |
| 0.867 | attacker | speech | attacker | `ACTN_CSM_Sync_SwordGrind` | `speech autodetect effort_build` | chainsword_new |  |
| 0.867 | attacker | speech | attacker | `ACTN_CSM_Sync_Sword_Grind_Chaos` | `speech autodetect roar` | chainsword_new |  |
| 0.867-1.400 | attacker | effect | attacker | `ACTN_CSM_Sync_Sword_Grind_Chaos` | `chainsword_blood_grind_long at fx_chainsword_spray (blood)` | chainsword_new |  |
| 0.867-1.500 | attacker | motor (ours) | wound | `ACTN_CSM_Sync_SwordGrind` | `play_sm_sk_chainsword_grind_armor -> the wielder's grind` | chainsword_new |  |
| 1.167 | victim | sound | victim | `EVT_Left_Foot` | `play_ch_marine_footstep` | chaos_marine_melee |  |
| 1.367 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.433 | attacker | speech (ours) | victim | `fx_NOBSync_Grind_End` | `speech  damage_small` | chainsword_new |  |
| 1.433-1.533 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_blood_swipe at fx_chainsword_spray (blood)` | chainsword_new |  |
| 1.433-1.533 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_blood_rip_out at fx_swing (blood)` | chainsword_new |  |
| 1.433-1.533 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_swing_blood at fx_swing (blood)` | chainsword_new |  |
| 1.733 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.933 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 2.000 | attacker | motor (ours) | wound | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4 -> the wielder's rev` | chainsword_new |  |
| 2.267 | attacker | speech | attacker | `ACTN_Sync_Chainsword_Swing` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 2.267-2.533 | attacker | motor (ours) | attacker | `ACTN_Sync_Chainsword_Swing` | `play_sm_chainsword_swing_x4 -> the wielder's swing` | chainsword_new |  |
| 2.533 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 2.533 | attacker | effect | attacker | `ACTN_CSM_Sync_Sword_Impact_Chaos` | `cl_talon_stab at fx_chainsword_spray` | chainsword_new |  |
| 2.533 | attacker | motor (ours) | wound | `ACTN_CSM_Sync_Sword_Impact_Chaos` | `play_sm_sk_chainsword_hit_armor -> the wielder's hit` | chainsword_new |  |
| 2.533 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | chaos_marine_melee |  |
| 2.567 | attacker | motor (ours) | wound | `ACTN_CSM_Sync_SwordHit` | `play_sm_sk_chainsword_hit_armor -> the wielder's hit` | chainsword_new |  |
| 2.600 | attacker | speech | attacker | `ACTN_CSM_Sync_SwordGrind` | `speech autodetect effort_build` | chainsword_new |  |
| 2.600 | attacker | speech | attacker | `ACTN_CSM_Sync_Sword_Grind_Chaos` | `speech autodetect roar` | chainsword_new |  |
| 2.600-4.233 | attacker | effect | attacker | `ACTN_CSM_Sync_Sword_Grind_Chaos` | `chainsword_blood_grind_long at fx_chainsword_spray (blood)` | chainsword_new |  |
| 2.600-3.333 | attacker | motor (ours) | wound | `ACTN_CSM_Sync_SwordGrind` | `play_sm_sk_chainsword_grind_armor -> the wielder's grind` | chainsword_new |  |
| 3.901 | attacker | motor (ours) | wound | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4 -> the wielder's rev` | chainsword_new |  |
| 4.233 | attacker | speech (ours) | victim | `fx_NOBSync_Grind_End` | `speech  damage_small` | chainsword_new |  |
| 4.233-4.767 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_blood_swipe at fx_chainsword_spray (blood)` | chainsword_new |  |
| 4.233-4.767 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_blood_rip_out at fx_swing (blood)` | chainsword_new |  |
| 4.233-4.767 | attacker | effect | attacker | `fx_NOBSync_Grind_End` | `chainsword_swing_blood at fx_swing (blood)` | chainsword_new |  |
| 4.267 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 4.300 | victim | effect | victim | `ACTN_FX_CS_Sync_Blood_Geyser` | `chainsword_grind_chaos at fx_decap_geyser` | chaos_marine_melee |  |
| 4.300 | victim | effect | victim | `ACTN_FX_CS_Sync_Blood_Geyser` | `chainsword_blood_rip_out at fx_decap_geyser (blood)` | chaos_marine_melee |  |
| 4.367 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 5.533 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### C2.power_axe (kill 2.933 s, end 4.000 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.314 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.467 | victim | sound | victim | `EVT_Left_Foot` | `play_ch_marine_footstep` | chaos_marine_melee |  |
| 0.471 | attacker | sound | attacker | `ACTN_Sync_Poweraxe_Antic` | `play_sm_power_axe_antic` | poweraxe |  |
| 0.471 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_antic_light at fx_swing` | poweraxe |  |
| 0.471 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 0.500 | victim | sound | victim | `EVT_Right_Foot` | `play_ch_marine_footstep` | chaos_marine_melee |  |
| 0.734-1.028 | attacker | sound | attacker | `ACTN_Sync_ Poweraxe _Swing` | `play_sm_power_axe_swing` | poweraxe |  |
| 0.734 | attacker | speech | attacker | `ACTN_Sync_ Poweraxe _Swing` | `speech autodetect effort_short_stab` | poweraxe |  |
| 0.734-1.028 | attacker | effect | attacker | `ACTN_Sync_ Poweraxe _Swing` | `poweraxe_swing_light at fx_swing` | poweraxe |  |
| 0.734-1.028 | attacker | effect | attacker | `ACTN_Sync_ Poweraxe _Swing` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 0.900 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | chaos_marine_melee |  |
| 0.933 | attacker | sound (ours) | wound | `ACTN_CSM_Sync_AxHit` | `play_sm_sk_power_axe_hit_armor` | poweraxe |  |
| 0.933 | attacker | effect | attacker | `ACTN_CSM_Sync_AxHit` | `poweraxe_dmg_body at fx_edge_blood_1` | poweraxe |  |
| 0.933 | attacker | effect | attacker | `ACTN_CSM_Sync_AxHit` | `chainsword_blood_rip_out at fx_edge_blood_1 (blood)` | poweraxe |  |
| 0.980 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.028 | attacker | stop | attacker | `ACTN_Sync_ Poweraxe _Swing` | `stop_sm_power_axe_swing` | poweraxe |  |
| 1.100 | victim | sound | victim | `EVT_Left_Foot` | `play_ch_marine_footstep` | chaos_marine_melee |  |
| 1.200 | victim | sound | victim | `EVT_Right_Foot` | `play_ch_marine_footstep` | chaos_marine_melee |  |
| 1.431-1.766 | attacker | sound (ours) | wound | `ACTN_Nob_Sync_RemoveAx` | `play_sm_sk_power_axe_release_flesh` | poweraxe |  |
| 1.431 | attacker | speech | attacker | `ACTN_Nob_Sync_RemoveAx` | `speech autodetect effort_big_pull` | poweraxe |  |
| 1.431-1.766 | attacker | effect | attacker | `ACTN_Nob_Sync_RemoveAx` | `chainsword_blood_swipe at fx_swing (blood)` | poweraxe |  |
| 1.600 | victim | sound | victim | `EVT_Left_Foot` | `play_ch_marine_footstep` | chaos_marine_melee |  |
| 1.635 | attacker | sound | attacker | `ACTN_Sync_Poweraxe_Antic` | `play_sm_power_axe_antic` | poweraxe |  |
| 1.635 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_antic_light at fx_swing` | poweraxe |  |
| 1.635 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 1.725 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 2.018-2.202 | attacker | sound | attacker | `ACTN_Sync_ Poweraxe _Swing` | `play_sm_power_axe_swing` | poweraxe |  |
| 2.018 | attacker | speech | attacker | `ACTN_Sync_ Poweraxe _Swing` | `speech autodetect effort_short_stab` | poweraxe |  |
| 2.018-2.202 | attacker | effect | attacker | `ACTN_Sync_ Poweraxe _Swing` | `poweraxe_swing_light at fx_swing` | poweraxe |  |
| 2.018-2.202 | attacker | effect | attacker | `ACTN_Sync_ Poweraxe _Swing` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 2.100 | attacker | sound (ours) | wound | `ACTN_CSM_Sync_AxHit` | `play_sm_sk_power_axe_hit_armor` | poweraxe |  |
| 2.100 | attacker | effect | attacker | `ACTN_CSM_Sync_AxHit` | `poweraxe_dmg_body at fx_edge_blood_1` | poweraxe |  |
| 2.100 | attacker | effect | attacker | `ACTN_CSM_Sync_AxHit` | `chainsword_blood_rip_out at fx_edge_blood_1 (blood)` | poweraxe |  |
| 2.202 | attacker | stop | attacker | `ACTN_Sync_ Poweraxe _Swing` | `stop_sm_power_axe_swing` | poweraxe |  |
| 2.972-3.413 | attacker | sound (ours) | wound | `ACTN_CSM_Sync_KickHit` | `play_sm_sk_kick_hit_armored` | poweraxe |  |
| 2.972-3.413 | attacker | effect | attacker | `ACTN_CSM_Sync_KickHit` | `chainsword_blood_swipe at fx_swing (blood)` | poweraxe |  |
| 3.216 | attacker | sound | attacker | `ACTN_Sync_Poweraxe_Antic` | `play_sm_power_axe_antic` | poweraxe |  |
| 3.216 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_antic_light at fx_swing` | poweraxe |  |
| 3.216 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 3.843 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### C3.thunder_hammer (kill 2.533 s, end 4.000 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.533 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.667-1.267 | attacker | sound | attacker | `ACTN_CSM_Sync_SlamDown` | `play_sm_sk_foley_movement` | thunderhammer_new |  |
| 0.667 | attacker | speech | attacker | `ACTN_CSM_Sync_SlamDown` | `speech autodetect roar` | thunderhammer_new |  |
| 1.233 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.267 | attacker | sound (ours) | wound | `ACTN_CSM_Sync_SlamImpact` | `play_sync_slam_armored` | thunderhammer_new |  |
| 1.300 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Small` | `speech autodetect damage_small` | chaos_marine_melee |  |
| 1.767 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 2.467-2.600 | attacker | sound | attacker | `ACTN_Sync_Thunderhammer_Swing` | `play_sm_thunder_hammer_swing_heavy` | thunderhammer_new |  |
| 2.467 | attacker | speech | attacker | `ACTN_Sync_Thunderhammer_Swing` | `speech autodetect roar` | thunderhammer_new |  |
| 2.467-2.600 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Swing` | `thunderhammer_swing_light at fx_swing` | thunderhammer_new |  |
| 2.533 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 2.600 | attacker | sound (ours) | wound | `ACTN_CSM_Sync_HammerHit` | `play_sm_sk_thunderhammer_hit_armor` | thunderhammer_new |  |
| 2.600 | attacker | stop | attacker | `ACTN_Sync_Thunderhammer_Swing` | `stop_sm_thunder_hammer_swing_heavy` | thunderhammer_new |  |
| 2.600 | attacker | effect | attacker | `ACTN_CSM_Sync_HammerHit` | `thunderhammer_dmg_body at fx_swing` | thunderhammer_new |  |
| 3.267 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O1.chainsword (kill 2.200 s, end 3.294 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.067 | attacker | sound | attacker | `ACTN_Sync_GearMove` | `play_sm_sk_foley_movement` | vanguard |  |
| 0.247 | attacker | sound | attacker | `SFX_SM_SK01_ThroatGrab` | `play_sync_grab` | chainsword_new |  |
| 0.247 | attacker | sound | attacker | `SFX_SM_SK01_ThroatGrab` | `play_sm_gear_move` | chainsword_new |  |
| 0.247 | attacker | speech | attacker | `SFX_SM_SK01_ThroatGrab` | `speech autodetect roar` | chainsword_new |  |
| 0.323-1.077 | victim | sound | victim | `ACTN_SND_Vox_Roar_H_S` | `play_or_bo_2_roar` | boy |  |
| 0.400 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.500 | attacker | sound | attacker | `ACTN_Ork_Sync_ThroatGrab` | `play_sync_grab` | vanguard |  |
| 0.967 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.000-2.167 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 1.000-2.167 | attacker | sound (ours) | wound | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_chainsword_grind_flesh` | chainsword_new |  |
| 1.000 | attacker | speech | attacker | `SFX_SM_SK_Cut_Start` | `speech autodetect effort_build` | chainsword_new |  |
| 1.000-2.167 | attacker | effect | attacker | `SFX_SM_SK_Cut_Duration` | `chainsword_swing_heavy at fx_swing` | chainsword_new |  |
| 1.000-1.167 | attacker | effect | attacker | `SFX_SM_SK_Cut_Start` | `chainsword_blood_grind_long at fx_chainsword_spray (blood)` | chainsword_new |  |
| 1.000-1.167 | attacker | effect | attacker | `SFX_SM_SK_Cut_Start` | `chainsword_armor_swipe at fx_chainsword_spray` | chainsword_new |  |
| 1.300 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | boy |  |
| 2.167 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 2.167 | attacker | stop | wound | `SFX_SM_SK_Cut_Duration` | `stop_sm_sk_chainsword_grind_flesh` | chainsword_new |  |
| 2.182 | attacker | speech | attacker | `SFX_SM_SK_Cut_End` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 2.267 | attacker | sound (ours) | wound | `ACTN_Ork_Sync_Gory_SFX` | `play_sync_stomp` | chainsword_new |  |
| 2.633 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O1.chainsword with the power sword (kill 2.200 s, end 3.294 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.067 | attacker | sound | attacker | `ACTN_Sync_GearMove` | `play_sm_sk_foley_movement` | vanguard |  |
| 0.247 | attacker | sound | attacker | `SFX_SM_SK01_ThroatGrab` | `play_sync_grab` | chainsword_new |  |
| 0.247 | attacker | sound | attacker | `SFX_SM_SK01_ThroatGrab` | `play_sm_gear_move` | chainsword_new |  |
| 0.247 | attacker | speech | attacker | `SFX_SM_SK01_ThroatGrab` | `speech autodetect roar` | chainsword_new |  |
| 0.323-1.077 | victim | sound | victim | `ACTN_SND_Vox_Roar_H_S` | `play_or_bo_2_roar` | boy |  |
| 0.400 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.500 | attacker | sound | attacker | `ACTN_Ork_Sync_ThroatGrab` | `play_sync_grab` | vanguard |  |
| 0.967 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.000-2.167 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 1.000 | attacker | speech | attacker | `SFX_SM_SK_Cut_Start` | `speech autodetect effort_build` | chainsword_new |  |
| 1.000-1.167 | attacker | effect | attacker | `SFX_SM_SK_Cut_Start` | `chainsword_blood_grind_long at fx_chainsword_spray (blood)` | chainsword_new |  |
| 1.000-2.167 | attacker | motor (ours) | wound | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_chainsword_grind_flesh -> the wielder's grind` | chainsword_new |  |
| 1.300 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | boy |  |
| 2.167 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 2.182 | attacker | speech | attacker | `SFX_SM_SK_Cut_End` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 2.267 | attacker | sound (ours) | wound | `ACTN_Ork_Sync_Gory_SFX` | `play_sync_stomp` | chainsword_new |  |
| 2.633 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O2.chainsword (kill 3.933 s, end 4.583 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.302 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 0.393 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 0.430 | attacker | sound | attacker | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4` | chainsword_new |  |
| 0.433-0.633 | attacker | sound | attacker | `ACTN_Sync_Chainsword_Swing` | `play_sm_chainsword_swing_x4` | chainsword_new |  |
| 0.433 | attacker | speech | attacker | `ACTN_Sync_Chainsword_Swing` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 0.433-0.633 | attacker | effect | attacker | `ACTN_Sync_Chainsword_Swing` | `chainsword_swing_heavy at fx_swing` | chainsword_new |  |
| 0.567 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.633 | attacker | stop | attacker | `ACTN_Sync_Chainsword_Swing` | `stop_sm_chainsword_swing` | chainsword_new |  |
| 0.633 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | boy |  |
| 0.667-2.900 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 0.667-2.900 | attacker | sound (ours) | wound | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_chainsword_grind_flesh` | chainsword_new |  |
| 0.667 | attacker | speech | attacker | `SFX_SM_SK_Cut_Start` | `speech autodetect effort_build` | chainsword_new |  |
| 0.667-2.900 | attacker | effect | attacker | `SFX_SM_SK_Cut_Duration` | `chainsword_swing_heavy at fx_swing` | chainsword_new |  |
| 0.667-0.785 | attacker | effect | attacker | `SFX_SM_SK_Cut_Start` | `chainsword_blood_grind_long at fx_chainsword_spray (blood)` | chainsword_new |  |
| 0.667-0.785 | attacker | effect | attacker | `SFX_SM_SK_Cut_Start` | `chainsword_armor_swipe at fx_chainsword_spray` | chainsword_new |  |
| 0.933 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.967 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 1.027 | attacker | sound | attacker | `ACTN_Sync_GearMove` | `play_sm_sk_foley_movement` | vanguard |  |
| 1.208 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 1.329 | attacker | sound | attacker | `ACTN_Sync_GearMove` | `play_sm_sk_foley_movement` | vanguard |  |
| 1.333 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.500 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.541 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 1.833 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.933-2.779 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 1.933-2.779 | attacker | sound (ours) | wound | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_chainsword_grind_flesh` | chainsword_new |  |
| 1.933-2.779 | attacker | effect | attacker | `SFX_SM_SK_Cut_Duration` | `chainsword_swing_heavy at fx_swing` | chainsword_new |  |
| 2.000 | attacker | sound | attacker | `ACTN_Ork_Sync_Grind_Lift` | `play_sm_sk_chainsword_rev_up` | chainsword_new |  |
| 2.000 | attacker | sound | attacker | `ACTN_Ork_Sync_Grind_Lift` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 2.779 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 2.779 | attacker | stop | wound | `SFX_SM_SK_Cut_Duration` | `stop_sm_sk_chainsword_grind_flesh` | chainsword_new |  |
| 2.800 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 2.900 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 2.900-3.300 | attacker | sound | attacker | `ACTN_Sync_Chainsword_Swing` | `play_sm_chainsword_swing_x4` | chainsword_new |  |
| 2.900 | attacker | stop | wound | `SFX_SM_SK_Cut_Duration` | `stop_sm_sk_chainsword_grind_flesh` | chainsword_new |  |
| 2.900 | attacker | speech (ours) | victim | `ACTN_Ork_GrindSync_BloodPullout` | `speech ork_boy_2 effort_short_stab` | chainsword_new |  |
| 2.900 | attacker | speech | attacker | `ACTN_Sync_Chainsword_Swing` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 2.900-3.300 | attacker | effect | attacker | `ACTN_Ork_GrindSync_BloodPullout` | `chainsword_swing_blood at fx_swing (blood)` | chainsword_new |  |
| 2.900-3.300 | attacker | effect | attacker | `ACTN_Ork_GrindSync_BloodPullout` | `chainsword_blood_swipe at fx_chainsword_spray (blood)` | chainsword_new |  |
| 2.900-3.300 | attacker | effect | attacker | `ACTN_Sync_Chainsword_Swing` | `chainsword_swing_heavy at fx_swing` | chainsword_new |  |
| 3.000 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 3.300 | attacker | stop | attacker | `ACTN_Sync_Chainsword_Swing` | `stop_sm_chainsword_swing` | chainsword_new |  |
| 3.933 | attacker | sound (ours) | wound | `ACTN_Ork_Sync_Kick_Hit` | `play_sm_sk_kick_hit` | chainsword_new |  |
| 3.933 | attacker | sound | attacker | `ACTN_Sync_GearMove` | `play_sm_sk_foley_movement` | vanguard |  |
| 4.333 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 4.733 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O2.chainsword with the power sword (kill 3.933 s, end 4.583 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.302 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 0.393 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 0.430 | attacker | motor (ours) | wound | `ACTN_Sync_Chainsword_Antic` | `play_sm_chainsword_rev_x4 -> the wielder's rev` | chainsword_new |  |
| 0.433 | attacker | speech | attacker | `ACTN_Sync_Chainsword_Swing` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 0.433-0.633 | attacker | motor (ours) | attacker | `ACTN_Sync_Chainsword_Swing` | `play_sm_chainsword_swing_x4 -> the wielder's swing` | chainsword_new |  |
| 0.567 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.633 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | boy |  |
| 0.667-2.900 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 0.667 | attacker | speech | attacker | `SFX_SM_SK_Cut_Start` | `speech autodetect effort_build` | chainsword_new |  |
| 0.667-0.785 | attacker | effect | attacker | `SFX_SM_SK_Cut_Start` | `chainsword_blood_grind_long at fx_chainsword_spray (blood)` | chainsword_new |  |
| 0.667-2.900 | attacker | motor (ours) | wound | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_chainsword_grind_flesh -> the wielder's grind` | chainsword_new |  |
| 0.933 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.967 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 1.027 | attacker | sound | attacker | `ACTN_Sync_GearMove` | `play_sm_sk_foley_movement` | vanguard |  |
| 1.208 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 1.329 | attacker | sound | attacker | `ACTN_Sync_GearMove` | `play_sm_sk_foley_movement` | vanguard |  |
| 1.333 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.500 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.541 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 1.833 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.933-2.779 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 1.933-2.779 | attacker | motor (ours) | wound | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_chainsword_grind_flesh -> the wielder's grind` | chainsword_new |  |
| 2.000 | attacker | sound | attacker | `ACTN_Ork_Sync_Grind_Lift` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 2.000 | attacker | motor (ours) | wound | `ACTN_Ork_Sync_Grind_Lift` | `play_sm_sk_chainsword_rev_up -> the wielder's rev` | chainsword_new |  |
| 2.779 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 2.800 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 2.900 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 2.900 | attacker | speech (ours) | victim | `ACTN_Ork_GrindSync_BloodPullout` | `speech ork_boy_2 effort_short_stab` | chainsword_new |  |
| 2.900 | attacker | speech | attacker | `ACTN_Sync_Chainsword_Swing` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 2.900-3.300 | attacker | effect | attacker | `ACTN_Ork_GrindSync_BloodPullout` | `chainsword_swing_blood at fx_swing (blood)` | chainsword_new |  |
| 2.900-3.300 | attacker | effect | attacker | `ACTN_Ork_GrindSync_BloodPullout` | `chainsword_blood_swipe at fx_chainsword_spray (blood)` | chainsword_new |  |
| 2.900-3.300 | attacker | motor (ours) | attacker | `ACTN_Sync_Chainsword_Swing` | `play_sm_chainsword_swing_x4 -> the wielder's swing` | chainsword_new |  |
| 3.000 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 3.933 | attacker | sound (ours) | wound | `ACTN_Ork_Sync_Kick_Hit` | `play_sm_sk_kick_hit` | chainsword_new |  |
| 3.933 | attacker | sound | attacker | `ACTN_Sync_GearMove` | `play_sm_sk_foley_movement` | vanguard |  |
| 4.333 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 4.733 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O3.chainsword (kill 2.433 s, end 3.381 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.163 | attacker | sound | attacker | `SFX_SM_SK01_ThroatGrab` | `play_sync_grab` | chainsword_new |  |
| 0.163 | attacker | sound | attacker | `SFX_SM_SK01_ThroatGrab` | `play_sm_gear_move` | chainsword_new |  |
| 0.163 | attacker | speech | attacker | `SFX_SM_SK01_ThroatGrab` | `speech autodetect roar` | chainsword_new |  |
| 1.385-1.793 | attacker | sound (ours) | wound | `ACTN_ArdBoy_Sync_BodySlam` | `play_sync_slam` | vanguard |  |
| 1.385 | attacker | speech | attacker | `ACTN_ArdBoy_Sync_BodySlam` | `speech autodetect roar` | vanguard |  |
| 1.385-1.793 | attacker | effect | attacker | `ACTN_ArdBoy_Sync_BodySlam` | `jumppack_land_light at rig_l_hand` | vanguard |  |
| 1.433 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Small` | `speech autodetect damage_small` | boy |  |
| 2.467-3.000 | attacker | sound (ours) | wound | `ACTN_ArdBoy_Sync_Stomp` | `play_sync_stomp` | vanguard |  |
| 2.467 | attacker | speech | attacker | `ACTN_ArdBoy_Sync_Stomp` | `speech autodetect roar` | vanguard |  |
| 2.467-3.000 | attacker | effect | attacker | `ACTN_ArdBoy_Sync_Stomp` | `jumppack_land_light at fx_l_foot` | vanguard |  |

#### O3.chainsword with the power sword (kill 2.433 s, end 3.381 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.163 | attacker | sound | attacker | `SFX_SM_SK01_ThroatGrab` | `play_sync_grab` | chainsword_new |  |
| 0.163 | attacker | sound | attacker | `SFX_SM_SK01_ThroatGrab` | `play_sm_gear_move` | chainsword_new |  |
| 0.163 | attacker | speech | attacker | `SFX_SM_SK01_ThroatGrab` | `speech autodetect roar` | chainsword_new |  |
| 1.385-1.793 | attacker | sound (ours) | wound | `ACTN_ArdBoy_Sync_BodySlam` | `play_sync_slam` | vanguard |  |
| 1.385 | attacker | speech | attacker | `ACTN_ArdBoy_Sync_BodySlam` | `speech autodetect roar` | vanguard |  |
| 1.385-1.793 | attacker | effect | attacker | `ACTN_ArdBoy_Sync_BodySlam` | `jumppack_land_light at rig_l_hand` | vanguard |  |
| 1.433 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Small` | `speech autodetect damage_small` | boy |  |
| 2.467-3.000 | attacker | sound (ours) | wound | `ACTN_ArdBoy_Sync_Stomp` | `play_sync_stomp` | vanguard |  |
| 2.467 | attacker | speech | attacker | `ACTN_ArdBoy_Sync_Stomp` | `speech autodetect roar` | vanguard |  |
| 2.467-3.000 | attacker | effect | attacker | `ACTN_ArdBoy_Sync_Stomp` | `jumppack_land_light at fx_l_foot` | vanguard |  |

#### O3.combat_knife (kill 2.433 s, end 3.381 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 1.385-1.793 | attacker | sound (ours) | wound | `ACTN_ArdBoy_Sync_BodySlam` | `play_sync_slam` | vanguard |  |
| 1.385 | attacker | speech | attacker | `ACTN_ArdBoy_Sync_BodySlam` | `speech autodetect roar` | vanguard |  |
| 1.385-1.793 | attacker | effect | attacker | `ACTN_ArdBoy_Sync_BodySlam` | `jumppack_land_light at rig_l_hand` | vanguard |  |
| 1.433 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Small` | `speech autodetect damage_small` | boy |  |
| 2.467-3.000 | attacker | sound (ours) | wound | `ACTN_ArdBoy_Sync_Stomp` | `play_sync_stomp` | vanguard |  |
| 2.467 | attacker | speech | attacker | `ACTN_ArdBoy_Sync_Stomp` | `speech autodetect roar` | vanguard |  |
| 2.467-3.000 | attacker | effect | attacker | `ACTN_ArdBoy_Sync_Stomp` | `jumppack_land_light at fx_l_foot` | vanguard |  |

#### O3.power_axe (kill 2.433 s, end 3.381 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 1.385-1.793 | attacker | sound (ours) | wound | `ACTN_ArdBoy_Sync_BodySlam` | `play_sync_slam` | vanguard |  |
| 1.385 | attacker | speech | attacker | `ACTN_ArdBoy_Sync_BodySlam` | `speech autodetect roar` | vanguard |  |
| 1.385-1.793 | attacker | effect | attacker | `ACTN_ArdBoy_Sync_BodySlam` | `jumppack_land_light at rig_l_hand` | vanguard |  |
| 1.433 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Small` | `speech autodetect damage_small` | boy |  |
| 2.467-3.000 | attacker | sound (ours) | wound | `ACTN_ArdBoy_Sync_Stomp` | `play_sync_stomp` | vanguard |  |
| 2.467 | attacker | speech | attacker | `ACTN_ArdBoy_Sync_Stomp` | `speech autodetect roar` | vanguard |  |
| 2.467-3.000 | attacker | effect | attacker | `ACTN_ArdBoy_Sync_Stomp` | `jumppack_land_light at fx_l_foot` | vanguard |  |

#### O3.thunder_hammer (kill 2.433 s, end 3.381 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.163 | attacker | sound | attacker | `SFX_SM_SK01_ThroatGrab` | `play_sm_sk01_throatgrab` | thunderhammer_new | not in the client's banks |
| 0.163 | attacker | sound | attacker | `SFX_SM_SK01_ThroatGrab` | `play_sm_gear_move` | thunderhammer_new |  |
| 0.163 | attacker | speech | attacker | `SFX_SM_SK01_ThroatGrab` | `speech autodetect roar` | thunderhammer_new |  |
| 1.385-1.793 | attacker | sound (ours) | wound | `ACTN_ArdBoy_Sync_BodySlam` | `play_sync_slam` | vanguard |  |
| 1.385 | attacker | speech | attacker | `ACTN_ArdBoy_Sync_BodySlam` | `speech autodetect roar` | vanguard |  |
| 1.385-1.793 | attacker | effect | attacker | `ACTN_ArdBoy_Sync_BodySlam` | `jumppack_land_light at rig_l_hand` | vanguard |  |
| 1.433 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Small` | `speech autodetect damage_small` | boy |  |
| 2.467-3.000 | attacker | sound (ours) | wound | `ACTN_ArdBoy_Sync_Stomp` | `play_sync_stomp` | vanguard |  |
| 2.467 | attacker | speech | attacker | `ACTN_ArdBoy_Sync_Stomp` | `speech autodetect roar` | vanguard |  |
| 2.467-3.000 | attacker | effect | attacker | `ACTN_ArdBoy_Sync_Stomp` | `jumppack_land_light at fx_l_foot` | vanguard |  |

#### O4.chainsword (kill 1.433 s, end 2.824 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.057 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 0.300-0.433 | attacker | sound (ours) | wound | `EVT_GrindStart` | `play_sm_chain_sword_grind_pre` | chainsword_new | not in the client's banks |
| 0.300-0.433 | attacker | effect | attacker | `EVT_GrindStart` | `blood_pre_grind at fx_chainsword_spray (blood)` | chainsword_new |  |
| 0.450 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 0.500-0.700 | attacker | sound | attacker | `ACTN_Sync_Chainsword_Swing` | `play_sm_chainsword_swing_x4` | chainsword_new |  |
| 0.500 | attacker | speech | attacker | `ACTN_Sync_Chainsword_Swing` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 0.500-0.700 | attacker | effect | attacker | `ACTN_Sync_Chainsword_Swing` | `chainsword_swing_heavy at fx_swing` | chainsword_new |  |
| 0.540 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 0.633 | attacker | speech | attacker | `SFX_SM_SK_Cut_Start` | `speech autodetect effort_build` | chainsword_new |  |
| 0.633-0.700 | attacker | effect | attacker | `SFX_SM_SK_Cut_Start` | `chainsword_blood_grind_long at fx_chainsword_spray (blood)` | chainsword_new |  |
| 0.633-0.700 | attacker | effect | attacker | `SFX_SM_SK_Cut_Start` | `chainsword_armor_swipe at fx_chainsword_spray` | chainsword_new |  |
| 0.700 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.700-1.400 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 0.700-1.400 | attacker | sound (ours) | wound | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_chainsword_grind_flesh` | chainsword_new |  |
| 0.700 | attacker | stop | attacker | `ACTN_Sync_Chainsword_Swing` | `stop_sm_chainsword_swing` | chainsword_new |  |
| 0.700-1.400 | attacker | effect | attacker | `SFX_SM_SK_Cut_Duration` | `chainsword_swing_heavy at fx_swing` | chainsword_new |  |
| 0.733 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | boy |  |
| 1.400 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 1.400 | attacker | stop | wound | `SFX_SM_SK_Cut_Duration` | `stop_sm_sk_chainsword_grind_flesh` | chainsword_new |  |
| 1.400 | attacker | speech | attacker | `SFX_SM_SK_Cut_End` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 1.433 | attacker | sound (ours) | wound | `ACTN_Ork_Sync_OverhandHit` | `play_sm_sk_fist_hit` | vanguard |  |
| 1.433 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.433 | attacker | effect | attacker | `ACTN_Ork_Sync_OverhandHit` | `body_smash at rig_prop_lhand` | vanguard |  |
| 2.000 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 2.767 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O4.chainsword with the power sword (kill 1.433 s, end 2.824 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.057 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 0.300 | attacker | speech | attacker | `EVT_GrindStart` | `speech autodetect roar` | powersword_new |  |
| 0.300-0.433 | attacker | effect | attacker | `EVT_GrindStart` | `blood_pre_grind at fx_chainsword_spray (blood)` | powersword_new |  |
| 0.300-0.433 | attacker | motor (ours) | wound | `EVT_GrindStart` | `play_sm_chain_sword_grind_pre -> the wielder's rev` | powersword_new |  |
| 0.450 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 0.500 | attacker | speech | attacker | `ACTN_Sync_Chainsword_Swing` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 0.500-0.700 | attacker | motor (ours) | attacker | `ACTN_Sync_Chainsword_Swing` | `play_sm_chainsword_swing_x4 -> the wielder's swing` | chainsword_new |  |
| 0.540 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 0.633 | attacker | speech | attacker | `SFX_SM_SK_Cut_Start` | `speech autodetect effort_build` | chainsword_new |  |
| 0.633-0.700 | attacker | effect | attacker | `SFX_SM_SK_Cut_Start` | `chainsword_blood_grind_long at fx_chainsword_spray (blood)` | chainsword_new |  |
| 0.700 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.700-1.400 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 0.700-1.400 | attacker | motor (ours) | wound | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_chainsword_grind_flesh -> the wielder's grind` | chainsword_new |  |
| 0.733 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | boy |  |
| 1.400 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 1.400 | attacker | speech | attacker | `SFX_SM_SK_Cut_End` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 1.433 | attacker | sound (ours) | wound | `ACTN_Ork_Sync_OverhandHit` | `play_sm_sk_fist_hit` | vanguard |  |
| 1.433 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.433 | attacker | effect | attacker | `ACTN_Ork_Sync_OverhandHit` | `body_smash at rig_prop_lhand` | vanguard |  |
| 2.000 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 2.767 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O4.combat_knife (kill 1.433 s, end 2.824 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.057 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 0.300-0.433 | attacker | effect | attacker | `EVT_GrindStart` | `body_smash at rig_r_hand` | combat_knife |  |
| 0.450 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 0.500 | attacker | speech | attacker | `ACTN_Sync_Chainsword_Swing` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 0.500-0.700 | attacker | motor (ours) | attacker | `ACTN_Sync_Chainsword_Swing` | `play_sm_chainsword_swing_x4 -> the wielder's swing` | chainsword_new |  |
| 0.540 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 0.633 | attacker | speech | attacker | `SFX_SM_SK_Cut_Start` | `speech autodetect effort_build` | chainsword_new |  |
| 0.633-0.700 | attacker | effect | attacker | `SFX_SM_SK_Cut_Start` | `chainsword_blood_grind_long at fx_chainsword_spray (blood)` | chainsword_new |  |
| 0.700 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.700-1.400 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_gear_move` | combat_knife |  |
| 0.700 | attacker | speech | attacker | `SFX_SM_SK_Cut_Duration` | `speech autodetect roar` | combat_knife |  |
| 0.700-1.400 | attacker | motor (ours) | wound | `SFX_SM_SK_Cut_Duration` | `play_sm_chainsword_sk_cut_start -> the wielder's grind` | combat_knife |  |
| 0.733 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | boy |  |
| 1.400 | attacker | sound | attacker | `SFX_SM_SK_Cut_Duration` | `play_sm_gear_move` | combat_knife |  |
| 1.400 | attacker | speech | attacker | `SFX_SM_SK_Cut_End` | `speech autodetect effort_short_stab` | chainsword_new |  |
| 1.433 | attacker | sound (ours) | wound | `ACTN_Ork_Sync_OverhandHit` | `play_sm_sk_fist_hit` | vanguard |  |
| 1.433 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.433 | attacker | effect | attacker | `ACTN_Ork_Sync_OverhandHit` | `body_smash at rig_prop_lhand` | vanguard |  |
| 2.000 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 2.767 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O4.power_axe (kill 1.433 s, end 2.824 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.057 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 0.333 | attacker | sound | attacker | `ACTN_Sync_Poweraxe_Antic` | `play_sm_power_axe_antic` | poweraxe |  |
| 0.333 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_antic_light at fx_swing` | poweraxe |  |
| 0.333 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 0.450 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 0.540 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 0.567-0.733 | attacker | sound | attacker | `ACTN_Sync_ Poweraxe _Swing` | `play_sm_power_axe_swing` | poweraxe |  |
| 0.567 | attacker | speech | attacker | `ACTN_Sync_ Poweraxe _Swing` | `speech autodetect effort_short_stab` | poweraxe |  |
| 0.567-0.733 | attacker | effect | attacker | `ACTN_Sync_ Poweraxe _Swing` | `poweraxe_swing_light at fx_swing` | poweraxe |  |
| 0.567-0.733 | attacker | effect | attacker | `ACTN_Sync_ Poweraxe _Swing` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 0.700 | attacker | sound (ours) | wound | `ACTN_Ork_Decap_AxBackHit` | `play_sm_sk_power_axe_hit_flesh` | poweraxe |  |
| 0.700 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.700 | attacker | speech | attacker | `ACTN_Ork_Decap_AxBackHit` | `speech autodetect roar` | poweraxe |  |
| 0.700 | attacker | effect | attacker | `ACTN_Ork_Decap_AxBackHit` | `chainsword_blood_rip_out at fx_edge_blood_1 (blood)` | poweraxe |  |
| 0.700 | attacker | effect | attacker | `ACTN_Ork_Decap_AxBackHit` | `poweraxe_dmg_body at fx_edge_blood_1` | poweraxe |  |
| 0.733 | attacker | stop | attacker | `ACTN_Sync_ Poweraxe _Swing` | `stop_sm_power_axe_swing` | poweraxe |  |
| 0.733 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | boy |  |
| 1.400 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.433 | attacker | sound (ours) | wound | `ACTN_Ork_Sync_OverhandHit` | `play_sm_sk_fist_hit` | vanguard |  |
| 1.433 | attacker | sound (ours) | wound | `ACTN_Ork_Sync_OverhandHit` | `play_sm_sk_power_axe_hit_flesh` | poweraxe |  |
| 1.433 | attacker | effect | attacker | `ACTN_Ork_Sync_OverhandHit` | `body_smash at rig_prop_lhand` | vanguard |  |
| 1.433 | attacker | effect | attacker | `ACTN_Ork_Sync_OverhandHit` | `chainsword_blood_rip_out at rig_prop_lhand (blood)` | poweraxe |  |
| 1.567 | attacker | sound | attacker | `ACTN_Sync_Poweraxe_Antic` | `play_sm_power_axe_antic` | poweraxe |  |
| 1.567 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_antic_light at fx_swing` | poweraxe |  |
| 1.567 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 2.000 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 2.633 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O5.power_axe (kill 1.867 s, end 2.833 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.100 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 0.200 | attacker | sound | attacker | `ACTN_Sync_Poweraxe_Antic` | `play_sm_power_axe_antic` | poweraxe |  |
| 0.200 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_antic_light at fx_swing` | poweraxe |  |
| 0.200 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 0.400-0.533 | attacker | sound | attacker | `ACTN_Sync_ Poweraxe _Swing` | `play_sm_power_axe_swing` | poweraxe |  |
| 0.400 | attacker | speech | attacker | `ACTN_Sync_ Poweraxe _Swing` | `speech autodetect effort_short_stab` | poweraxe |  |
| 0.400-0.533 | attacker | effect | attacker | `ACTN_Sync_ Poweraxe _Swing` | `poweraxe_swing_light at fx_swing` | poweraxe |  |
| 0.400-0.533 | attacker | effect | attacker | `ACTN_Sync_ Poweraxe _Swing` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 0.433 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.500 | attacker | sound (ours) | wound | `ACTN_Ork_Decap_AxBackHit` | `play_sm_sk_power_axe_hit_flesh` | poweraxe |  |
| 0.500 | attacker | speech (ours) | victim | `ACTN_Ork_Decap_AxBackHit` | `speech ork_boy_2 damage_big` | poweraxe |  |
| 0.500 | attacker | speech | attacker | `ACTN_Ork_Decap_AxBackHit` | `speech autodetect roar` | poweraxe |  |
| 0.500 | attacker | effect | attacker | `ACTN_Ork_Decap_AxBackHit` | `chainsword_blood_rip_out at fx_edge_blood_1 (blood)` | poweraxe |  |
| 0.500 | attacker | effect | attacker | `ACTN_Ork_Decap_AxBackHit` | `poweraxe_dmg_body at fx_edge_blood_1` | poweraxe |  |
| 0.500 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Small` | `speech autodetect damage_small` | boy |  |
| 0.533 | attacker | stop | attacker | `ACTN_Sync_ Poweraxe _Swing` | `stop_sm_power_axe_swing` | poweraxe |  |
| 0.900 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.044 | attacker | sound | attacker | `ACTN_Sync_Poweraxe_Antic` | `play_sm_power_axe_antic` | poweraxe |  |
| 1.044 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_antic_light at fx_swing` | poweraxe |  |
| 1.044 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 1.133-1.333 | attacker | sound (ours) | wound | `ACTN_Nob_Sync_RemoveAx` | `play_sm_sk_power_axe_release_flesh` | poweraxe |  |
| 1.133 | attacker | speech | attacker | `ACTN_Nob_Sync_RemoveAx` | `speech autodetect effort_big_pull` | poweraxe |  |
| 1.133-1.333 | attacker | effect | attacker | `ACTN_Nob_Sync_RemoveAx` | `chainsword_blood_swipe at fx_swing (blood)` | poweraxe |  |
| 1.433 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.528 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 1.533 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 1.567 | attacker | sound | attacker | `ACTN_Sync_Poweraxe_Antic` | `play_sm_power_axe_antic` | poweraxe |  |
| 1.567 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_antic_light at fx_swing` | poweraxe |  |
| 1.567 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 1.633-1.800 | attacker | sound | attacker | `ACTN_Sync_ Poweraxe _Swing` | `play_sm_power_axe_swing` | poweraxe |  |
| 1.633 | attacker | speech | attacker | `ACTN_Sync_ Poweraxe _Swing` | `speech autodetect effort_short_stab` | poweraxe |  |
| 1.633-1.800 | attacker | effect | attacker | `ACTN_Sync_ Poweraxe _Swing` | `poweraxe_swing_light at fx_swing` | poweraxe |  |
| 1.633-1.800 | attacker | effect | attacker | `ACTN_Sync_ Poweraxe _Swing` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 1.667 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.715 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 1.800-2.067 | attacker | sound (ours) | wound | `ACTN_Nob_Sync_RemoveAx` | `play_sm_sk_power_axe_release_flesh` | poweraxe |  |
| 1.800 | attacker | stop | attacker | `ACTN_Sync_ Poweraxe _Swing` | `stop_sm_power_axe_swing` | poweraxe |  |
| 1.800 | attacker | speech | attacker | `ACTN_Nob_Sync_RemoveAx` | `speech autodetect effort_big_pull` | poweraxe |  |
| 1.800-2.067 | attacker | effect | attacker | `ACTN_Nob_Sync_RemoveAx` | `chainsword_blood_swipe at fx_swing (blood)` | poweraxe |  |
| 1.867 | attacker | sound (ours) | wound | `ACTN_OrkBoy_Sync_HeadDecapHit` | `play_sm_sk_power_axe_hit_flesh` | poweraxe |  |
| 1.867 | attacker | speech | attacker | `ACTN_OrkBoy_Sync_HeadDecapHit` | `speech autodetect roar` | poweraxe |  |
| 1.900 | attacker | sound | attacker | `ACTN_Sync_Poweraxe_Antic` | `play_sm_power_axe_antic` | poweraxe |  |
| 1.900 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_antic_light at fx_swing` | poweraxe |  |
| 1.900 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 1.938 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 2.133 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 2.162 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |

#### O6.power_axe (kill 2.600 s, end 3.667 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.175 | attacker | sound | attacker | `ACTN_Sync_Poweraxe_Antic` | `play_sm_power_axe_antic` | poweraxe |  |
| 0.175 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_antic_light at fx_swing` | poweraxe |  |
| 0.175 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 0.524-0.629 | attacker | sound | attacker | `ACTN_Sync_ Poweraxe _Swing` | `play_sm_power_axe_swing` | poweraxe |  |
| 0.524 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.524 | attacker | speech | attacker | `ACTN_Sync_ Poweraxe _Swing` | `speech autodetect effort_short_stab` | poweraxe |  |
| 0.524-0.629 | attacker | effect | attacker | `ACTN_Sync_ Poweraxe _Swing` | `poweraxe_swing_light at fx_swing` | poweraxe |  |
| 0.524-0.629 | attacker | effect | attacker | `ACTN_Sync_ Poweraxe _Swing` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 0.567 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | boy |  |
| 0.594 | attacker | sound (ours) | wound | `ACTN_Ork_Decap_AxBackHit` | `play_sm_sk_power_axe_hit_flesh` | poweraxe |  |
| 0.594 | attacker | speech | attacker | `ACTN_Ork_Decap_AxBackHit` | `speech autodetect roar` | poweraxe |  |
| 0.594 | attacker | effect | attacker | `ACTN_Ork_Decap_AxBackHit` | `chainsword_blood_rip_out at fx_edge_blood_1 (blood)` | poweraxe |  |
| 0.594 | attacker | effect | attacker | `ACTN_Ork_Decap_AxBackHit` | `poweraxe_dmg_body at fx_edge_blood_1` | poweraxe |  |
| 0.629 | attacker | stop | attacker | `ACTN_Sync_ Poweraxe _Swing` | `stop_sm_power_axe_swing` | poweraxe |  |
| 0.700 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 0.833 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 1.167-1.400 | attacker | sound (ours) | wound | `ACTN_Nob_Sync_RemoveAx` | `play_sm_sk_power_axe_release_flesh` | poweraxe |  |
| 1.167 | attacker | speech | attacker | `ACTN_Nob_Sync_RemoveAx` | `speech autodetect effort_big_pull` | poweraxe |  |
| 1.167-1.400 | attacker | effect | attacker | `ACTN_Nob_Sync_RemoveAx` | `chainsword_blood_swipe at fx_swing (blood)` | poweraxe |  |
| 1.222 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.400 | attacker | sound | attacker | `ACTN_Sync_Poweraxe_Antic` | `play_sm_power_axe_antic` | poweraxe |  |
| 1.400 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_antic_light at fx_swing` | poweraxe |  |
| 1.400 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 1.600-1.833 | attacker | sound | attacker | `ACTN_Sync_ Poweraxe _Swing` | `play_sm_power_axe_swing` | poweraxe |  |
| 1.600 | attacker | speech | attacker | `ACTN_Sync_ Poweraxe _Swing` | `speech autodetect effort_short_stab` | poweraxe |  |
| 1.600-1.833 | attacker | effect | attacker | `ACTN_Sync_ Poweraxe _Swing` | `poweraxe_swing_light at fx_swing` | poweraxe |  |
| 1.600-1.833 | attacker | effect | attacker | `ACTN_Sync_ Poweraxe _Swing` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 1.746 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.767 | attacker | sound (ours) | wound | `ACTN_Ork_Decap_AxBackHit` | `play_sm_sk_power_axe_hit_flesh` | poweraxe |  |
| 1.767 | attacker | speech (ours) | victim | `ACTN_Ork_Decap_AxBackHit` | `speech ork_boy_2 damage_big` | poweraxe |  |
| 1.767 | attacker | speech | attacker | `ACTN_Ork_Decap_AxBackHit` | `speech autodetect roar` | poweraxe |  |
| 1.767 | attacker | effect | attacker | `ACTN_Ork_Decap_AxBackHit` | `chainsword_blood_rip_out at fx_edge_blood_1 (blood)` | poweraxe |  |
| 1.767 | attacker | effect | attacker | `ACTN_Ork_Decap_AxBackHit` | `poweraxe_dmg_body at fx_edge_blood_1` | poweraxe |  |
| 1.833 | attacker | stop | attacker | `ACTN_Sync_ Poweraxe _Swing` | `stop_sm_power_axe_swing` | poweraxe |  |
| 1.851 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.867 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 2.600-2.933 | attacker | sound (ours) | wound | `ACTN_Nob_Sync_RemoveAx` | `play_sm_sk_power_axe_release_flesh` | poweraxe |  |
| 2.600 | attacker | speech | attacker | `ACTN_Nob_Sync_RemoveAx` | `speech autodetect effort_big_pull` | poweraxe |  |
| 2.600-2.933 | attacker | effect | attacker | `ACTN_Nob_Sync_RemoveAx` | `chainsword_blood_swipe at fx_swing (blood)` | poweraxe |  |
| 2.700 | attacker | sound | attacker | `ACTN_Sync_Poweraxe_Antic` | `play_sm_power_axe_antic` | poweraxe |  |
| 2.700 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_antic_light at fx_swing` | poweraxe |  |
| 2.700 | attacker | effect | attacker | `ACTN_Sync_Poweraxe_Antic` | `poweraxe_face_glow_charge at fx_face` | poweraxe |  |
| 3.492 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O7.thunder_hammer (kill 2.033 s, end 3.333 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.400 | attacker | sound | attacker | `ACTN_Sync_Thunderhammer_Antic` | `play_sm_thunder_hammer_antic_sync` | thunderhammer_new |  |
| 0.400 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Antic` | `thunderhammer_antic_light_face at fx_face01` | thunderhammer_new |  |
| 0.400 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Antic` | `thunderhammer_antic_light_side at fx_side01` | thunderhammer_new |  |
| 0.400 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Antic` | `thunderhammer_antic_light_center at fx_swing` | thunderhammer_new |  |
| 0.633-0.933 | attacker | sound | attacker | `ACTN_Sync_Thunderhammer_Swing` | `play_sm_thunder_hammer_swing_heavy` | thunderhammer_new |  |
| 0.633 | attacker | speech | attacker | `ACTN_Sync_Thunderhammer_Swing` | `speech autodetect roar` | thunderhammer_new |  |
| 0.633-0.933 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Swing` | `thunderhammer_swing_light at fx_swing` | thunderhammer_new |  |
| 0.767 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Big` | `speech autodetect damage_big` | boy |  |
| 0.833-1.000 | attacker | sound (ours) | wound | `ACTN_Ork_Sync_HammerImpact` | `play_sm_sk_thunderhammer_hit_flesh` | thunderhammer_new |  |
| 0.867 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.933 | attacker | stop | attacker | `ACTN_Sync_Thunderhammer_Swing` | `stop_sm_thunder_hammer_swing_heavy` | thunderhammer_new |  |
| 1.111 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.167 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.467-1.533 | attacker | sound | attacker | `ACTN_Sync_Thunderhammer_Antic` | `play_sm_thunder_hammer_antic_sync` | thunderhammer_new |  |
| 1.467-1.533 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Antic` | `thunderhammer_antic_light_face at fx_face01` | thunderhammer_new |  |
| 1.467-1.533 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Antic` | `thunderhammer_antic_light_side at fx_side01` | thunderhammer_new |  |
| 1.467-1.533 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Antic` | `thunderhammer_antic_light_center at fx_swing` | thunderhammer_new |  |
| 1.500 | attacker | sound | attacker | `ACTN_Sync_Thunderhammer_Antic` | `play_sm_thunder_hammer_antic_sync` | thunderhammer_new |  |
| 1.500 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Antic` | `thunderhammer_antic_light_face at fx_face01` | thunderhammer_new |  |
| 1.500 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Antic` | `thunderhammer_antic_light_side at fx_side01` | thunderhammer_new |  |
| 1.500 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Antic` | `thunderhammer_antic_light_center at fx_swing` | thunderhammer_new |  |
| 1.533 | attacker | stop | attacker | `ACTN_Sync_Thunderhammer_Antic` | `stop_sm_thunder_hammer_antic_sync` | thunderhammer_new |  |
| 1.867-2.033 | attacker | sound | attacker | `ACTN_Sync_Thunderhammer_Swing` | `play_sm_thunder_hammer_swing_heavy` | thunderhammer_new |  |
| 1.867 | attacker | speech | attacker | `ACTN_Sync_Thunderhammer_Swing` | `speech autodetect roar` | thunderhammer_new |  |
| 1.867-2.033 | attacker | effect | attacker | `ACTN_Sync_Thunderhammer_Swing` | `thunderhammer_swing_light at fx_swing` | thunderhammer_new |  |
| 1.967 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 2.033-2.267 | attacker | sound (ours) | wound | `ACTN_Ork_Sync_GroundImpact` | `play_sm_sk_thunderhammer_slam` | thunderhammer_new |  |
| 2.033 | attacker | stop | attacker | `ACTN_Sync_Thunderhammer_Swing` | `stop_sm_thunder_hammer_swing_heavy` | thunderhammer_new |  |
| 2.033-2.267 | attacker | effect | attacker | `ACTN_Ork_Sync_GroundImpact` | `thunderhammer_dmg_displace at fx_swing` | thunderhammer_new |  |
| 2.033-2.267 | attacker | effect | attacker | `ACTN_Ork_Sync_GroundImpact` | `autocannon_dmg_body at fx_swing` | thunderhammer_new |  |
| 2.267 | attacker | stop | wound | `ACTN_Ork_Sync_GroundImpact` | `stop_sm_thunder_hammer_swing_heavy` | thunderhammer_new |  |
| 3.100 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O8.chainsword (kill 1.200 s, end 2.800 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.097 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 0.367 | attacker | speech | attacker | `ACTN_SND_CK_Swing` | `speech autodetect effort_short_stab` | combat_knife |  |
| 0.367-0.567 | attacker | effect | attacker | `ACTN_SND_CK_Swing` | `choppa_swing at fx_swing` | combat_knife |  |
| 0.367-0.567 | attacker | motor (ours) | attacker | `ACTN_SND_CK_Swing` | `play_combat_knife_whoosh -> the wielder's swing` | combat_knife |  |
| 0.467 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.483 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 0.567 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Small` | `speech autodetect damage_small` | boy |  |
| 0.567 | attacker | effect | attacker | `ACTN_Sync_Head_Stab` | `chainsword_swing_blood at fx_swing (blood)` | combat_knife |  |
| 0.567 | attacker | effect | attacker | `ACTN_Sync_Head_Stab` | `chainsword_dmg at fx_swing` | combat_knife |  |
| 0.567 | attacker | effect | attacker | `ACTN_Sync_Head_Stab` | `chainsword_blood_swipe at fx_swing (blood)` | combat_knife |  |
| 0.567 | attacker | motor (ours) | wound | `ACTN_Sync_Head_Stab` | `play_combat_knife_stab -> the wielder's hit` | combat_knife |  |
| 0.709 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 1.167 | attacker | speech | attacker | `ACTN_SND_CK_Swing` | `speech autodetect effort_short_stab` | combat_knife |  |
| 1.167-1.467 | attacker | effect | attacker | `ACTN_SND_CK_Swing` | `choppa_swing at fx_swing` | combat_knife |  |
| 1.167-1.467 | attacker | motor (ours) | attacker | `ACTN_SND_CK_Swing` | `play_combat_knife_whoosh -> the wielder's swing` | combat_knife |  |
| 1.200 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.200 | attacker | motor (ours) | wound | `ACTN_Sync_Head_Remove` | `play_combat_knife_remove -> the wielder's rev` | combat_knife |  |
| 1.224-1.804 | victim | sound | victim | `ACTN_SND_Vox_Roar_H_M` | `play_or_bo_2_roar` | boy |  |
| 1.257 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 1.450 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 1.948 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O8.chainsword with the power sword (kill 1.200 s, end 2.800 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.097 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 0.367 | attacker | speech | attacker | `ACTN_SND_CK_Swing` | `speech autodetect effort_short_stab` | combat_knife |  |
| 0.367-0.567 | attacker | effect | attacker | `ACTN_SND_CK_Swing` | `choppa_swing at fx_swing` | combat_knife |  |
| 0.367-0.567 | attacker | motor (ours) | attacker | `ACTN_SND_CK_Swing` | `play_combat_knife_whoosh -> the wielder's swing` | combat_knife |  |
| 0.467 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.483 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 0.567 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Small` | `speech autodetect damage_small` | boy |  |
| 0.567 | attacker | effect | attacker | `ACTN_Sync_Head_Stab` | `chainsword_swing_blood at fx_swing (blood)` | combat_knife |  |
| 0.567 | attacker | effect | attacker | `ACTN_Sync_Head_Stab` | `chainsword_dmg at fx_swing` | combat_knife |  |
| 0.567 | attacker | effect | attacker | `ACTN_Sync_Head_Stab` | `chainsword_blood_swipe at fx_swing (blood)` | combat_knife |  |
| 0.567 | attacker | motor (ours) | wound | `ACTN_Sync_Head_Stab` | `play_combat_knife_stab -> the wielder's hit` | combat_knife |  |
| 0.709 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 1.167 | attacker | speech | attacker | `ACTN_SND_CK_Swing` | `speech autodetect effort_short_stab` | combat_knife |  |
| 1.167-1.467 | attacker | effect | attacker | `ACTN_SND_CK_Swing` | `choppa_swing at fx_swing` | combat_knife |  |
| 1.167-1.467 | attacker | motor (ours) | attacker | `ACTN_SND_CK_Swing` | `play_combat_knife_whoosh -> the wielder's swing` | combat_knife |  |
| 1.200 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.200 | attacker | motor (ours) | wound | `ACTN_Sync_Head_Remove` | `play_combat_knife_remove -> the wielder's rev` | combat_knife |  |
| 1.224-1.804 | victim | sound | victim | `ACTN_SND_Vox_Roar_H_M` | `play_or_bo_2_roar` | boy |  |
| 1.257 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 1.450 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 1.948 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O8.combat_knife (kill 1.200 s, end 2.800 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.097 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 0.367-0.567 | attacker | sound | attacker | `ACTN_SND_CK_Swing` | `play_combat_knife_whoosh` | combat_knife |  |
| 0.367 | attacker | speech | attacker | `ACTN_SND_CK_Swing` | `speech autodetect effort_short_stab` | combat_knife |  |
| 0.367-0.567 | attacker | effect | attacker | `ACTN_SND_CK_Swing` | `choppa_swing at fx_swing` | combat_knife |  |
| 0.467 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.483 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 0.567 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Small` | `speech autodetect damage_small` | boy |  |
| 0.567 | attacker | sound (ours) | wound | `ACTN_Sync_Head_Stab` | `play_combat_knife_stab` | combat_knife |  |
| 0.567 | attacker | effect | attacker | `ACTN_Sync_Head_Stab` | `chainsword_swing_blood at fx_swing (blood)` | combat_knife |  |
| 0.567 | attacker | effect | attacker | `ACTN_Sync_Head_Stab` | `chainsword_dmg at fx_swing` | combat_knife |  |
| 0.567 | attacker | effect | attacker | `ACTN_Sync_Head_Stab` | `chainsword_blood_swipe at fx_swing (blood)` | combat_knife |  |
| 0.709 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 1.167-1.467 | attacker | sound | attacker | `ACTN_SND_CK_Swing` | `play_combat_knife_whoosh` | combat_knife |  |
| 1.167 | attacker | speech | attacker | `ACTN_SND_CK_Swing` | `speech autodetect effort_short_stab` | combat_knife |  |
| 1.167-1.467 | attacker | effect | attacker | `ACTN_SND_CK_Swing` | `choppa_swing at fx_swing` | combat_knife |  |
| 1.200 | attacker | sound | attacker | `ACTN_Sync_Head_Remove` | `play_combat_knife_remove` | combat_knife |  |
| 1.200 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.224-1.804 | victim | sound | victim | `ACTN_SND_Vox_Roar_H_M` | `play_or_bo_2_roar` | boy |  |
| 1.257 | victim | sound | victim | `EVT_Right_Foot` | `play_or_boy_footstep` | boy |  |
| 1.450 | victim | sound | victim | `EVT_Left_Foot` | `play_or_boy_footstep` | boy |  |
| 1.948 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O9.chainsword (kill 0.333 s, end 0.833 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.367 | attacker | sound (ours) | wound | `ACTN_Gretchin_Sync_Punch_Hit` | `play_sm_sk_fist_hit` | vanguard |  |
| 0.367 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.367 | attacker | effect | attacker | `ACTN_Gretchin_Sync_Punch_Hit` | `body_smash at rig_prop_lhand` | vanguard |  |
| 0.967 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O9.chainsword with the power sword (kill 0.333 s, end 0.833 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.367 | attacker | sound (ours) | wound | `ACTN_Gretchin_Sync_Punch_Hit` | `play_sm_sk_fist_hit` | vanguard |  |
| 0.367 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.367 | attacker | effect | attacker | `ACTN_Gretchin_Sync_Punch_Hit` | `body_smash at rig_prop_lhand` | vanguard |  |
| 0.967 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O9.combat_knife (kill 0.333 s, end 0.833 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.367 | attacker | sound (ours) | wound | `ACTN_Gretchin_Sync_Punch_Hit` | `play_sm_sk_fist_hit` | vanguard |  |
| 0.367 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.367 | attacker | effect | attacker | `ACTN_Gretchin_Sync_Punch_Hit` | `body_smash at rig_prop_lhand` | vanguard |  |
| 0.967 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O9.power_axe (kill 0.333 s, end 0.833 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.367 | attacker | sound (ours) | wound | `ACTN_Gretchin_Sync_Punch_Hit` | `play_sm_sk_fist_hit` | vanguard |  |
| 0.367 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.367 | attacker | effect | attacker | `ACTN_Gretchin_Sync_Punch_Hit` | `body_smash at rig_prop_lhand` | vanguard |  |
| 0.967 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### O9.thunder_hammer (kill 0.333 s, end 0.833 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.367 | attacker | sound (ours) | wound | `ACTN_Gretchin_Sync_Punch_Hit` | `play_sm_sk_fist_hit` | vanguard |  |
| 0.367 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.367 | attacker | effect | attacker | `ACTN_Gretchin_Sync_Punch_Hit` | `body_smash at rig_prop_lhand` | vanguard |  |
| 0.967 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### R1.chainsword (kill 1.233 s, end 2.067 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.067 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 0.300 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Small` | `speech chaos_renegade damage_small` | chaos_renegade_ig |  |
| 0.333 | attacker | sound (ours) | wound | `ACTN_Renegade_Sync_Backfist` | `play_sync_hit_light` | vanguard |  |
| 0.333 | attacker | speech | attacker | `ACTN_Renegade_Sync_Backfist` | `speech autodetect effort_short_stab` | vanguard |  |
| 0.567 | attacker | sound (ours) | wound | `ACTN_Renegade_Sync_LeftArm_Gear_Move` | `play_sync_slam` | vanguard |  |
| 0.567 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.600 | victim | sound | victim | `EVT_Right_Foot` | `play_ch_renegade_footstep` | chaos_renegade_ig |  |
| 1.000 | victim | sound | victim | `EVT_Left_Foot` | `play_ch_renegade_footstep` | chaos_renegade_ig |  |
| 1.133 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.229 | attacker | sound | attacker | `sfx_SM_gear_movement_long` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 1.233 | attacker | sound | attacker | `ACTN_Renegade_Sync_Punch` | `play_sm_sk_foley_movement` | vanguard |  |
| 1.233 | attacker | sound (ours) | wound | `ACTN_Renegade_Sync_Punch` | `play_sm_sk_fist_hit` | vanguard |  |
| 1.233 | attacker | speech | attacker | `ACTN_Renegade_Sync_Punch` | `speech autodetect effort_short_stab` | vanguard |  |
| 2.400 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### R1.chainsword with the power sword (kill 1.233 s, end 2.067 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.067 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 0.300 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Small` | `speech chaos_renegade damage_small` | chaos_renegade_ig |  |
| 0.333 | attacker | sound (ours) | wound | `ACTN_Renegade_Sync_Backfist` | `play_sync_hit_light` | vanguard |  |
| 0.333 | attacker | speech | attacker | `ACTN_Renegade_Sync_Backfist` | `speech autodetect effort_short_stab` | vanguard |  |
| 0.567 | attacker | sound (ours) | wound | `ACTN_Renegade_Sync_LeftArm_Gear_Move` | `play_sync_slam` | vanguard |  |
| 0.567 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.600 | victim | sound | victim | `EVT_Right_Foot` | `play_ch_renegade_footstep` | chaos_renegade_ig |  |
| 1.000 | victim | sound | victim | `EVT_Left_Foot` | `play_ch_renegade_footstep` | chaos_renegade_ig |  |
| 1.133 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.229 | attacker | sound | attacker | `sfx_SM_gear_movement_long` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 1.233 | attacker | sound | attacker | `ACTN_Renegade_Sync_Punch` | `play_sm_sk_foley_movement` | vanguard |  |
| 1.233 | attacker | sound (ours) | wound | `ACTN_Renegade_Sync_Punch` | `play_sm_sk_fist_hit` | vanguard |  |
| 1.233 | attacker | speech | attacker | `ACTN_Renegade_Sync_Punch` | `speech autodetect effort_short_stab` | vanguard |  |
| 2.400 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### R1.chainsword with the combat knife (kill 1.233 s, end 2.067 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.067 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 0.300 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Small` | `speech chaos_renegade damage_small` | chaos_renegade_ig |  |
| 0.333 | attacker | sound (ours) | wound | `ACTN_Renegade_Sync_Backfist` | `play_sync_hit_light` | vanguard |  |
| 0.333 | attacker | speech | attacker | `ACTN_Renegade_Sync_Backfist` | `speech autodetect effort_short_stab` | vanguard |  |
| 0.567 | attacker | sound (ours) | wound | `ACTN_Renegade_Sync_LeftArm_Gear_Move` | `play_sync_slam` | vanguard |  |
| 0.567 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.600 | victim | sound | victim | `EVT_Right_Foot` | `play_ch_renegade_footstep` | chaos_renegade_ig |  |
| 1.000 | victim | sound | victim | `EVT_Left_Foot` | `play_ch_renegade_footstep` | chaos_renegade_ig |  |
| 1.133 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.229 | attacker | sound | attacker | `sfx_SM_gear_movement_long` | `play_sm_sk_foley_movement` | chainsword_new |  |
| 1.233 | attacker | sound | attacker | `ACTN_Renegade_Sync_Punch` | `play_sm_sk_foley_movement` | vanguard |  |
| 1.233 | attacker | sound (ours) | wound | `ACTN_Renegade_Sync_Punch` | `play_sm_sk_fist_hit` | vanguard |  |
| 1.233 | attacker | speech | attacker | `ACTN_Renegade_Sync_Punch` | `speech autodetect effort_short_stab` | vanguard |  |
| 2.400 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### R1.power_axe (kill 1.233 s, end 2.067 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.067 | attacker | sound | attacker | `ACTN_Sync_GearMove` | `play_sm_sk_foley_movement` | vanguard |  |
| 0.300 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Small` | `speech chaos_renegade damage_small` | chaos_renegade_ig |  |
| 0.333 | attacker | sound (ours) | wound | `ACTN_Renegade_Sync_Backfist` | `play_sync_hit_light` | vanguard |  |
| 0.333 | attacker | speech | attacker | `ACTN_Renegade_Sync_Backfist` | `speech autodetect effort_short_stab` | vanguard |  |
| 0.567 | attacker | sound (ours) | wound | `ACTN_Renegade_Sync_LeftArm_Gear_Move` | `play_sync_slam` | vanguard |  |
| 0.567 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.600 | victim | sound | victim | `EVT_Right_Foot` | `play_ch_renegade_footstep` | chaos_renegade_ig |  |
| 1.000 | victim | sound | victim | `EVT_Left_Foot` | `play_ch_renegade_footstep` | chaos_renegade_ig |  |
| 1.133 | attacker | sound | attacker | `ACTN_Sync_GearMove` | `play_sm_sk_foley_movement` | vanguard |  |
| 1.133 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.233 | attacker | sound | attacker | `ACTN_Renegade_Sync_Punch` | `play_sm_sk_foley_movement` | vanguard |  |
| 1.233 | attacker | sound (ours) | wound | `ACTN_Renegade_Sync_Punch` | `play_sm_sk_fist_hit` | vanguard |  |
| 1.233 | attacker | speech | attacker | `ACTN_Renegade_Sync_Punch` | `speech autodetect effort_short_stab` | vanguard |  |
| 2.400 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

#### R1.thunder_hammer (kill 1.233 s, end 2.067 s)

| s | side | kind | place | tag | event / effect | file | |
|---|---|---|---|---|---|---|---|
| 0.067 | attacker | sound | attacker | `EVT_Gear_Move` | `play_sm_gear_move_01` | vanguard |  |
| 0.300 | victim | speech | victim | `ACTN_SND_Vox_Sync_Damage_Small` | `speech chaos_renegade damage_small` | chaos_renegade_ig |  |
| 0.333 | attacker | sound (ours) | wound | `ACTN_Renegade_Sync_Backfist` | `play_sync_hit_light` | vanguard |  |
| 0.333 | attacker | speech | attacker | `ACTN_Renegade_Sync_Backfist` | `speech autodetect effort_short_stab` | vanguard |  |
| 0.567 | attacker | sound (ours) | wound | `ACTN_Renegade_Sync_LeftArm_Gear_Move` | `play_sync_slam` | vanguard |  |
| 0.567 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 0.600 | victim | sound | victim | `EVT_Right_Foot` | `play_ch_renegade_footstep` | chaos_renegade_ig |  |
| 1.000 | victim | sound | victim | `EVT_Left_Foot` | `play_ch_renegade_footstep` | chaos_renegade_ig |  |
| 1.133 | attacker | sound | attacker | `EVT_Right_Foot` | `play_sm_footfalls_normal` | vanguard |  |
| 1.229 | attacker | sound | attacker | `sfx_SM_gear_movement_long` | `play_sm_sk_foley_movement` | thunderhammer_new |  |
| 1.233 | attacker | sound | attacker | `ACTN_Renegade_Sync_Punch` | `play_sm_sk_foley_movement` | vanguard |  |
| 1.233 | attacker | sound (ours) | wound | `ACTN_Renegade_Sync_Punch` | `play_sm_sk_fist_hit` | vanguard |  |
| 1.233 | attacker | speech | attacker | `ACTN_Renegade_Sync_Punch` | `speech autodetect effort_short_stab` | vanguard |  |
| 2.400 | attacker | sound | attacker | `EVT_Left_Foot` | `play_sm_footfalls_normal` | vanguard |  |

Counts: 39 row-wield lists, 866 cues: 720 original bindings, 146 ours; 2 sound posts not in the client's banks.

## 6. The head cut (ours, 2026-11-27; owner card "Decap": "Search, then build ours")

Search (read-only): no head gib or severed-head model exists in the install for any race. The Ork Boy meshes `boy_melee`, `boy_ranged`, `boy_melee_pro` and `boy_ranged_pro` carry a headless body block (`GEO_Wounded_Neck`; the data's head dismemberment points name `geo_wounded_neck`); `boy_ardboy` has none. Marines have no stump geometry; the head is its own armour piece.

| Row | Victim | Cut | What happens |
|---|---|---|---|
| C1 (all wields) | Chaos Marine, race-swapped loyalist, the Chaos run's marine kinds | `ACTN_FX_CS_Sync_Blood_Geyser`, 4.300 s | head pieces hidden, neck cap (ours), loose head from the head pieces |
| O5 | Boy, Shoota Boy, Rokkit Boy (Classic Ork players too) | `ACTN_OrkBoy_Sync_HeadDecapHit`, 1.867 s | the install's headless body, loose head cut from the body's head triangles |
| O5 | 'Ard Boy, grounded Stormboy | - | left as before (no headless body) |
| B1 | Bloodletter | `HeadSawOff` 3.083 s | left as before: the data vanishes the daemon at 3.100 s |
| O7 | Ork Boy kinds | - | left as before: the head pop crushes, it does not sever |

The cut is one more cue (`ExecutionDecapRules.WithCut`, kind Decap), so the clock's rules in section 2 hold for it. The loose head is a rigid body tossed along the blade's exit, with the data's head-point effects at the neck; it lies and sinks with the corpse. It draws only where execution gore draws. Values: spec "Added content: execution head cut".
