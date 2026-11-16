# Evidence pass P0, round 3 (2026-11-16)

Round 3 of the owner-approved evidence plan ("keep mining"). Rounds 1 and 2 are docs/evidence-p0.md and docs/evidence-p0b.md; their findings are held for the owner and are not applied here. Branch `claude/evidence-p0c`, cut from `claude/evidence-p0b`. **No code, constant, protocol or config-hash change was made**; only spec rows, docs/discovery.md and this report moved. Musou Mode values are untouched.

Sources read (install buildid 7412325, read-only, extracted files only in the private cache):

- S1: the locale (`smtool locale`, the digit-bearing strings, the host-option, respawn, perk and zoom texts).
- S2: camera attribute files through `smtool baf-dump` (`camera_curve_settings`, `camera_melee_settings`, `camera_marker_settings`, `camera_controlled_flight_settings`, `camera_input_tuning`), the paint packages (`custom_paint_scheme`, `custom_paint_decal`, `custom_paint_colour`) and the 49 `mp_perk_*` wargear files.
- S3: HUD movies through `smtool gfx-dump` (`HudEventLog`, `HudText` as read in earlier rounds).
- S4: the Wwise banks through `smtool audio-event` (nodes, max instances) and a scratch console program over `SmFormats.Audio` that prints the Play and Stop transition times (not committed).
- S5: compiled Lua constant pools of 12 `util.oppc` files outside `util/mp`, with the round 1 layout (a scratch lister, not committed; nothing executed) and the plain config files `audio.cfg`, `w40k_sm_quickmatch_tuning_file.xml`, `graphicsdb.xml`.

Method as before: start from the refine register's Part A rows (camera, movement and jump pack, HUD, sound mix, effects, customizer) and the 16 rows round 2 left unsettled, look for a field, text or file that settles or narrows each, and say plainly when none does. Most of these rows are the owner's recollections or our own presentation rules, with the data already read in M20 to M45, so again most of this pass is negative evidence; the real finds are the Wwise fade times, the host-option texts and a stale note in discovery.

## 1. Result in numbers

| Outcome | Rows | Which |
|---|---:|---|
| Settled (documentation corrected) | 1 | Max instances of non-footstep sounds: the client already applies them to every post (the M33 "not done" note was out of date) |
| Narrowed (data added or a source ruled out, still tagged) | 6 | Respawn time (a host option exists), Score limit (two legacy texts, 15 and 20), Kill feed timing (the movie has no timeline), Death camera and shoulder side (no camera file exists for them), Sound Stop and Play fades (now read), Zoom cycle (perk-gated, pad-flavoured text) |
| New fact rows | 14 | spec "Evidence pass P0c" (host options, legacy modes, death texts, kill feed, unlisted camera sets, non-multiplayer camera files, camera negative result, fades, instance limits, unused perk texts, pad texts, `pattern_enabled`, Lua outside `util/mp`, quick-match file) |
| Examined, not settled (no row change) | 16 round-2 rows (section 4) plus effects, movement and the jump pack (section 3) | |
| Owner questions | 15 | section 6 (10 held unchanged, 5 new) |

The register count change is in section 7.

## 2. Per area

### 2.1 Camera

- The spec's camera table (about two dozen sets) was checked against `camera_curve_settings` (79 sets). The ones it lacks (`bullrush_hit_default` fov 30, pitch 80 to 95; `exploration_far_default` fov 40, pitch 5 to 175; `ledge_lean` fov 35, relax speed 0.5, blend 0.5 s; the sync-kill, combat-knife and heavy melee, Dreadnought and Ork sets) are none named by a multiplayer wargear we read; they are recorded and not used.
- **No file holds a death camera, a spectator camera or a shoulder swap.** Every walking curve puts the camera 0.644 m to the right; there is no left curve and no side setting. The rows "Shoulder side", "Death camera", "Death camera orbit" and "Spectator blend" are therefore ours with nothing in the data to refine; only a playtest can (section 8). The locale text `CHANGE CAMERA` (119476) and `KILLED BY:` (119478) next to the respawn texts are the only traces (question 13).
- `camera_melee_settings`, `camera_marker_settings` and `camera_controlled_flight_settings` are campaign and vehicle cameras (a roaming "cameraman" with radius 10, fov 35, blend 0.5 s each way; the Thunderhawk and Valkyrie marker cameras). Not multiplayer.
- Blend, relax and collision rules: still undecoded (round 2 had said so); no new source.
- Zoom cycle: the texts 105867 and 111535 ("Additional Zoom mode ... (press [L3])") make the second zoom level a perk's content (Stalker Optics, Lascannon Optics) with the pad button named in the PC text; the binding label `Zoom Cycle` is 120988. Narrowed, not applied (question 15).

### 2.2 Movement and the jump pack

No new source. The jump pack's numbers (power, costs, recharge, cooldown, pound) are in the spec from the wargear `power_settings` and `jump_info` and round 2's graph reading; the locale perk texts add nothing numeric beyond what `smtool perks` already prints. Acceleration and turn-rate rows stay "owner's recollection / ours": the only candidate fields (`rotation_max_degrees_per_second` 1080, `rotation_time_till_max_speed` 0.15) were read in round 2. The Lua utilities outside `util/mp` hold no movement number.

### 2.3 HUD timings

- **Kill feed**: `HudEventLog.gfx` is a single-frame movie whose three entries are single-frame clips: no fade-in, hold, slide or fade-out timeline exists in the data, so the feed's timing (ours: 0.4 s in, 4 s, 0.5 s out, taken from footage) cannot be refined from the install. Register row `R-7BBB712E` stays an estimate; its source is footage only.
- **Hit marker, score popups, respawn countdown**: the hit marker's alpha series (9 frames at 30 fps) and the reward-line clips are in the spec from the HUD movies (ui/hud-fidelity); the countdown's length is not in any movie or script (round 2). New: the texts. The original labels the respawn wait "DEPLOY IN" (119475) beside "TACTICAL INSERTION" (119474), "CHANGE CAMERA" (119476) and "KILLED BY:" (119478); ours says "RESPAWN IN n" and has no killer line (question 13). "Respawn Time" (103933) is an option on the create-game screen, so the respawn wait is host-configurable in the original and 5 s (the blueprint's `death_time`) is probably a default (question 12).
- **Damage indicators**: the screen-space effects in `master_fx` (`screen_dmg_*`) and the armour-bar flashes are in the spec; no timing field beyond those.
- Lua outside `util/mp`: `UI_StandardFadeOut` is 4.0 s (a campaign fade to black), `ui.lua`'s other functions only call native HUD functions. No multiplayer HUD number.

### 2.4 Sound mix (the M33 gaps)

- **Play fade-in and Stop fade-out are real and common.** The first `u32` of an action's parameters (M33's layout) is read as a transition time: 559 of 3,055 Play actions fade in (most often 1000 ms, up to 8000 ms) and 1,219 of 1,580 Stop actions fade out (749 of them 2000 ms, up to 10000 ms) over the 2,129 events of the 15 multiplayer packages. Named examples (spec P0c row): the jump-pack descent fades in over 2 s and its Stop, after 2.5 s, over 3 s; the plasma overheat vent's Stop fades over 3 s; the countdown swell fades in over 8 s and its stop event over 1 s; the stun and large-explosion ringing stop after 1 s over 4 s; armour break 1 s; armour restore 0.5 s in and 1 s out. Our client keeps only a Stop's delay (`AudioCatalog.Play` adds `(TargetId, delay)`), so every Stop cuts at once and every Play starts at full level (the only fade we play is a 0.03 s `KillFade` on a voice an instance limit kills, `SoundSystem.cs`) (question 11). The curve shape byte is 4 on zero transitions and 6 or 7 on fades; shapes are not reproduced either way. The field layout is M33's interpretation (medium).
- **Max instances**: the table in the spec row lists the limits of the main events (for example the bolter's fire switch 8 global, grenade and blast layers 3 to 6, melee hits 6 to 8 global, landings 4). The client applies the nodes' limits to every post (`InstanceLimits.All` since polish P1); the M33 "not done" note in docs/discovery.md is out of date. Settled as documentation.
- Still not decoded: state-driven properties (1,098 State objects; the unset state reads as `none`), Pause and Resume, the `0x90011` action, HDR, bus RTPCs and effects. No new source.
- `data/config/audio.cfg` holds only memory-pool and streaming sizes (75.3 MB default pool, 12 streams, 380 ms buffer): no mix value.

### 2.5 Effects

Not re-examined beyond a spot check: `smtool fx-dump auto bolter_muzzle` (a 0.201 s effect: a 0.2 s flash combo, 0.05 s side flashes, a point light of 0.005 s) agrees with the spec's weapon effect rows (extracted, high). The register's effects rows that remain tagged are renderer choices (ours) or footage readings; the data states no further value.

### 2.6 Armour customizer, paint and unlocks

The Part A customizer rows are mostly shader approximations and our own presentation rules. One data fact: `pattern_enabled` appears 599 times across the 80 preset schemes (True 161, False 438) beside a per-scheme `decals` list; the meaning of the flag is not stated (the register row "`pattern_enabled`, class badges" stays a gap). The unlock levels, perk and challenge gating are already in docs/customizer-and-perks-data.md. Five locale perk texts (Quicker Stomp, Mission Specialist, Super Kick, Resourceful, Tenacity) and the `(DISABLED)` texts belong to no `mp_perk_*` file: cut perks, not a difference from ours.

### 2.7 Other sources tried

- `data/config/w40k_sm_quickmatch_tuning_file.xml` (matchmaking and the server coordinator), `graphicsdb.xml` (vendor overrides) and `localStorage.cfg` (empty): no game rule.
- `util.oppc`'s other 74 Lua files: the twelve non-speech utilities were read as constant pools; the 33 `speechdata` tables are dialogue data and the co-op `cp_*` scripts are Exterminatus. No multiplayer number.
- Locale strings that state numbers (1,384 strings with a digit, filtered by units): the only gameplay numbers are perk and ability texts already in the perk data (frag and stim cooldowns, "15% to instantly explode enemies with a kick" of an unused perk, "survive for 5 minutes" in co-op) and two legacy mode descriptions (20 kills and 15 kills, question 14).

## 3. Examined, not settled: movement, jump pack, effects, customizer

| Row group (register area) | What was looked at | Why it stays |
|---|---|---|
| Acceleration, turn rates, air control, jump pack fuel, cooldowns, ground pound timing (Movement, Jump pack) | wargear and blueprint fields (round 2), locale, `util` Lua | physics are code; the data holds only the clips' times and the already recorded fields |
| Camera blend, relax, lookahead, release, pull-in, hide distance, damage dips (Camera) | all `camera_*` packages | ours; no death, spectator or shoulder camera in the data |
| Hit marker, score popups, damage vignette, kill feed (HUD) | HUD movies | art and a few clip timings are in the spec; the feed and the vignette timings are not in the movies |
| Tail and level of sounds, voice cap, distance curves (Sound mix) | banks | the fades are now data (question 11); the loudness and range rules are the owner's decisions |
| Muzzle, impact, blood (Effects) | `fx-dump` spot check | already extracted; the rest is renderer choices |
| Paint shading, decals, eye glow (Customizer) | `custom_paint_*` | shader approximations; the data states colours and slots only |

## 4. The 16 rows round 2 left unsettled: new sources tried

| Row (round 2 section 3) | New source tried | Result |
|---|---|---|
| Respawn time, Rapid Deployment 3 s, Teleport Homer rules, Tactical Readout, Weapon Versatility | locale host-option and perk texts, 12 more Lua constant pools | narrowed: "Respawn Time" (103933) is a create-game option, so the wait is host-set; the perk effects stay code |
| Spawn protection | `util` Lua outside `util/mp`, locale | still none anywhere |
| Score limit (41, 1000), time limit (10 min), Seize Ground score interval, friendly fire default | locale, quick-match XML | narrowed: the host option names and "Unlimited"/"%1% minutes" texts; two legacy descriptions give 20 kills and 15 kills (low); the values are in the executable |
| Seize Ground update timer interval | other Lua pools | unchanged (the compiled instructions would have to be read) |
| Hit reaction: who reacts, during a reaction, ending, knockback | none new | unsettled |
| Hit reaction: second alignment window | none new | unsettled |
| Hit reaction: sign of the hit angle | none new | unsettled |
| Flinches (`min_hit_reaction_time` 1) | none new | unsettled |
| Jump pack: height by hold, power costs, gravity by phase, air control | locale perk texts, scripts | unsettled |
| Jump pack: melee out of a landing | none new | unsettled (question 4) |
| Plasma heat unit "per second" | locale perk text lists "45 heat/s to 70 heat/s" through `smtool perks` (our unit) | unsettled |
| Ready hold (10 s) | none new | unsettled |
| Aim assist on the PC | locale: the zoom texts name a pad button (`[L3]`), so shipped texts are console-flavoured | unsettled (no device field) |
| Grenade fall-off between points | none new | unsettled |
| Reaction-by-intensity step | none new | unsettled |
| Fall-off and head multiplier unit questions | none new | unsettled |

Result: 2 of the 16 narrowed, 14 unsettled, none settled.

## 5. Checks

Author's own checks (not independent QA), run on this branch; the results are in section 7.

## 6. Owner questions

One list for the lead to put to the owner. Every item is a difference between the data and what we do now; **none is applied**. "Fix scope": value = one data or config value; logic = simulation or client code change with tests; protocol = the wire or the config hash changes, so a protocol bump and a release note. Items 1 to 10 are carried forward from docs/evidence-p0b.md section 4 unchanged (1 to 5 new in round 2, 6 to 10 held from round 1); items 11 to 15 are new.

1. **Stun and stagger clips are chosen against the source's side (hit reactions: clip direction and sectors).**
   - Original: the grenade (stun) and stagger machines test the angle to the source with 0 = in front, priority highest first: B for within 90 degrees of the front, then L (-135..-45) and R (45..135), F by default (the rear 90 degrees); stagger B in the front half, F behind. The letters name the direction the body is thrown (`Grenade_F` +2.0 m forward, `_B` -2.1 m; `Stagger_F` +4.0 m, `_B` -4.1 m). Source: `MP_SM_Reactions` "Behavior06", "Behavior12" and the clips' extracted root motion (evidence-p0b section 2.1).
   - Ours: four 90 degree sectors; a source in front plays the `_F` clip and one on the right the `_R` clip (`Reaction.DirectionOf`, `ReactionTuning`).
   - Size: a frontal stagger locks the victim 1.067 s in the data and 0.567 s in ours (0.5 s longer), a rear stagger 0.567 s against our 1.067 s; a frontal stun 0.667 s against 0.533 s; the side clips (0.8 s each) are effectively 45 degrees wide in the data (see evidence-p0b 2.1), 90 in ours. The shown clip is also reversed (the body is drawn flying toward a blast in front of it).
   - Fix scope: logic (the sector rule and the clip table, `ReactionDirection` kept) with a config-hash/protocol bump because reaction lock lengths are simulated.
2. **The original turns the reacting body (reaction alignment).**
   - Original: every reaction sequencer has `AttackAlignment` windows: interrupt 0 to 0.24 (back) or 0.28 (front) of the clip, stun and stagger 0.1 to 0.5 of the clip (stagger front 0.14 to 0.33), turning the body by whole angles to a per-clip target (front 0, back 180; grenade F 180, B 0, R -90, L +90), then a second window to the end. Source: evidence-p0b section 2.1.
   - Ours: the client eases the reacting body at 25 degrees per second by at most 45 degrees so that the clip's side faces the attacker (`C-283346F3`).
   - Size: the data's turn goes all the way to the clip's target within the window (up to 90 degrees for a hit at the edge of the front half, up to 45 for the side and rear wedges), ours at most 45 degrees at 25 degrees a second; with question 1 fixed, the pairing of clip and orientation changes (a frontal blast: faces the source and flies backward).
   - Fix scope: logic (client presentation, plus the sim's facing if the owner wants the turn to be real), no protocol if only the client.
3. **A tied timed match ends in sudden death in the original (Annihilation).**
   - Original: `deaths.lua` has a `SuddenDeath` function with the text "SUDDEN DEATH" and a score limit of 1; the Deaths objective has no draw; Seize Ground's end has `Draw`. Source: evidence-p0b section 2.4. The caller (the timer's end) is in code, so the trigger is inferred.
   - Ours: equal scores at the time limit are a draw.
   - Size: affects only tied timed matches.
   - Fix scope: logic in `MatchRules` plus a HUD banner and a protocol bump if the next point decides the match.
4. **Melee out of a jump-pack landing (non-hammer weapons).**
   - Original: the landing clips for the non-hammer weapons carry no `BRANCH_Jump_To_Melee` tag (the thunder hammer's do, at 0.30 s and 0.415 s); the jump-pack state leaves for a melee attack only through that branch, so a chainsword, sword or axe attack waits for the nav exit: `BRANCH_Run` at 0.333 s (run landing) or 0.462 s (idle landing) with the stick pushed, else `BRANCH_Jump_To_Idle` at 1.033 s of an idle landing. Source: `MP_SM_NAV_JP_Sim` landing sequencers, `SM_Base_MP` "Exotic - JumpPack" (evidence-p0b section 2.5).
   - Ours: melee, firing and switching are allowed in a landing (`R-3F38F314`).
   - Size: 0.33 to 1.03 s of no melee after a landing for the assault classes. One unnamed branch tag in the run landing could change this, so the owner's memory decides ("could you chainsword right out of a landing?").
   - Fix scope: logic, probably protocol (it is simulated).
5. **Signum Link's aim snap.**
   - Original: the Devastator's weapons (heavy bolter, lascannon, plasma cannon) each have a `__snap_upgrade` aim-assist set, hip and zoom, identical to the base set except `max_angle_for_ranged_aim_snap_to` 0 to 12 degrees. Source: evidence-p0b section 2.3.
   - Ours: the perk does nothing (aim assist is not simulated).
   - Size: a 12 degree aim snap for three weapons, if the PC used aim assist at all.
   - Fix scope: logic (a new aim-snap rule) and protocol; only worth doing if the owner remembers an aim snap on the PC.
6. **Charge distance with no target.** Original: the rush's "Smash Blendspace" on `FLT_MeleeTargetDistance` (initial value 1, below 2.5) plays the 2.5 m clip: 12.4 m in all and 3.83 m at the hit time 0.233 s; ours plays the 5 m clip: 14.9 m and 6.32 m. A charge at a target 2.5 to 5 m away blends in between. Size: 2.5 m shorter at every moment. Fix scope: logic plus protocol (the rush picks its variant from the lock's distance).
7. **Charge steering window.** Original: `AttackAlignment` over the first 0.12 of the 1.667 s clip (0.2 s) only; ours steers toward the target at 180 degrees a second while it connects, up to 0.667 s. Size: up to 0.47 s of homing, a turn of up to 85 degrees at the cap. The owner's reference video ("the last 0.3 s turns about 30 to 40 degrees") may contradict the reading. Fix scope: logic plus protocol.
8. **Weapon switch times.** Original: 0.467 s (rifle class, weapon changes at 0.233 s) and 0.667 s (bolt pistol), no `BTN_Ranged` exit, so a shot waits for the end; the heavy bolter and lascannon have no swap state. Ours: 0.6 s default, 0.1 s with the owner's quickdraw (2026-11-06, deliberate). Size: the quickdraw is 0.37 to 0.57 s faster than the original. Fix scope: value (`SimConfig.WeaponSwitchTime`), config hash.
9. **Evade ends on Aim and Grenade.** Original: the evade's exits are the stick, Fire, Reload and Sprint (and a new evade); no aim or grenade transition. Ours: an Aim press or a grenade also ends the evade at 0.667 s. Fix scope: logic, protocol.
10. **Back-diagonal evade.** Original: four direction sets only (forward, back, left, right in 90 degree sectors); no clip or wedge for S+A or S+D, and the forward diagonals fit the forward roll's alignment modifier. Ours: eight directions (owner's recollection, M22) with turned clips for the back diagonals. Fix scope: value or logic, depending on whether the owner keeps the recollection.

New in this round:

11. **Sound fade-ins and fade-outs are not played (Wwise Play and Stop transitions).**
    - Original: 559 of the 3,055 Play actions of the multiplayer banks fade in and 1,219 of the 1,580 Stop actions fade out (749 of them over 2 s). Examples: the jump-pack descent fades in over 2 s and its Stop, after 2.5 s, over 3 s; the plasma overheat vent's Stop over 3 s; the countdown swell fades in over 8 s and `stop_mp_countdown` over 1 s; the stun and large-explosion ringing stop after 1 s over 4 s; armour restore 0.5 s in and 1 s out; the stimpack aura stops after 1.2 s over 0.4 s. Source: `smtool audio-event` plus the action parameters' first `u32` (M33's layout, spec "Evidence pass P0c").
    - Ours: a Play starts at full level and a Stop cuts at once; only the Stop's delay is kept (`AudioCatalog.Play`, `SoundSystem`); property actions (pitch, volume, LPF) already glide over their transition.
    - Size: audible as tails and swells that the original has and ours lacks: the plasma vent and the jump-pack descent end abruptly; the ringing after a big blast ends in 4 s in the data and at its cut here; most affected are loops and long events, not one-shot weapon sounds (0 ms transitions).
    - Fix scope: logic, client only (a fade per voice on Play and Stop; a linear or the curve's shape), no protocol; best compared by ear (checklist C).
12. **Host options: Max. Armor, Armor Regeneration, Allow Perks, Player Decorators, Respawn Time.**
    - Original: the create-game screen lists Time Limit, Score Limit, Max. Armor, Armor Regeneration, Friendly Fire, Allow Perks and Player Decorators (locale 111759 to 111765), plus a "Respawn Time" label (103933); their value lists are in the executable. Source: `smtool locale`.
    - Ours: time limit, score limit and friendly fire (`server.cmd --time-limit --score-limit --friendly-fire`); armour, perks, decorators and respawn are fixed (respawn 5 s, the blueprint's `death_time`).
    - Size: four options missing; nothing differs in a default match.
    - Fix scope: logic plus protocol (match settings on the wire and in the config hash) and the lobby UI; the value ranges are not in the data, so the owner's memory decides them.
13. **Death and respawn screen: "DEPLOY IN", "KILLED BY:", "CHANGE CAMERA".**
    - Original: locale 119474 "TACTICAL INSERTION", 119475 "DEPLOY IN", 119476 "CHANGE CAMERA", 119478 "KILLED BY:", 111808 "respawning..."; the roles are read from the texts' neighbourhood (interpretation).
    - Ours: "RESPAWN IN n" (`client/Hud.cs`), no killer line, no camera change.
    - Size: a missing killer line and a different label; what "CHANGE CAMERA" did (cycling the death view, or a spectator on a teammate) is not in the data.
    - Fix scope: client logic for the text and the killer line (the killer is known from the kill feed), no protocol; the camera change needs the owner's memory first.
14. **Default score limit in the original (legacy mode texts 20 and 15).**
    - Original: locale 103913 "Kill the enemy players 20 times and win" and 103915 "Team Deathmatch: Kill 15 members of the opposing team to win" are fixed texts of an earlier build; the shipped descriptions are templated and the host's value decides (121127, 121128). Source: locale; the value list is in the executable.
    - Ours: 41 points, our own choice (`MatchRules.ScoreLimit`).
    - Size: if the shipped default was 15 or 20 kills, matches here run two to three times longer; only the owner's memory of the default and the option list can say.
    - Fix scope: value (`MatchRules.ScoreLimit` default), config hash.
15. **Zoom cycle is a perk's content.**
    - Original: "Additional Zoom mode for the Stalker Bolter or Daemon Eye Bolter" and "... for the Lascannon" are the texts of the perks Stalker Optics and Lascannon Optics (their pad text names `[L3]`); the weapons' `zoom_levels` list `fov_2` (the lascannon's 2 degrees); a `Zoom Cycle` binding label exists (120988). Source: locale 105867, 111535, 120988; wargear `zoom_levels` (spec zoom rows).
    - Ours: the view blends to `fov_1` only; the level cycle is not implemented (`R-2BE13C83`), and the two perks (`mp_perk_weap_stalker_zoom_modes`, `mp_perk_weap_las_cannon_zoom`) have nothing to switch.
    - Size: the stalker's second level equals the first (10 degrees), so only the lascannon differs (5.75 against 2 degrees); the perk is a half-empty choice for the stalker.
    - Fix scope: logic, client only (a zoom-cycle key, level blend); the PC key is not in the data (owner's memory).

## 7. Checks run

Author's own checks (not independent QA), on branch `claude/evidence-p0c`:

- `dotnet build SmReimpl.slnx` and `dotnet build client/SmClient.csproj`: both 0 errors.
- `smtool.cmd refine-register` regenerated; `--check` ok (3344 spec rows scanned, 2376 tagged; before this pass 3330 and 2367, the difference being the P0c fact rows and the fall-damage row's lower confidence).
- Filtered `dotnet test -m:4` (`PublicText`, `RefineRegister`, `Spec`): SmFormats 149 passed, SmNet 27 passed (the launcher test projects matched no test). The full suite was not run.
- The probes used only the repository's `smtool` readers (`locale`, `baf-dump`, `gfx-dump`, `audio-event`, `audio-rtpcs`, `perks`, `fx-dump`), a scratch console program over `SmFormats.Audio` and a scratch Lua constant-pool lister (not committed); extracted files stayed in the private cache. No server and no Godot was started.

## 8. Playtest checklist

Rows only a human can settle (look, listen, stopwatch), grouped so the owner can do them in one session in the order below (about one hour with a second copy of the original and our build side by side, same class and loadout). Record `same`, `close` or `differs` with the original's value in the register's Result column by ID.

**A. Look (screenshots from the same spot, same FOV)**

1. Third-person camera at rest and while aiming (`R-331520E1` distance 1.3x, `R-EB4F2AD9` FOV shift, `R-2BE13C83`, `R-052D48BC`): the standing distance, height and right-shoulder offset; the lascannon and stalker scopes.
2. Camera when something is close behind (`R-E135A8CB` hide distance, `R-C4739D78` shoulder side, the wall rows of camera/hop-and-walls): does the original hide the marine, and does it ever swap shoulders?
3. The death camera (`R-2FFF8AF7`, `R-8DA108B4`, `R-284D14E4`, `R-231BB64F`): distance, orbit, whether you can look around; what "CHANGE CAMERA" did (question 13).
4. Damage vignette and armour flashes (`R-1D3CB861`, `R-C6C8A576`, `R-850E9625`), hit marker (`R-4B7155DC`), reticles at spread (`R-93B612FC`).
5. Kill feed layout and the reward line (`R-0BD295BE`, `R-7BBB712E`, `R-920F9F9E`), the score panel (`R-C1029531`, `R-EF52CA94`), the death screen text (`R-2E01F836`, question 13).
6. Muzzle, impact and blood looks for the bolter, heavy bolter, plasma and melta (the register's Effects rows).

**B. Listen (A/B at the same distance, headphones)**

7. Tails and swells (question 11): the jump-pack descent, the plasma overheat vent, a grenade's ringing, the countdown, the stimpack aura. This is the first listening check, because a fix is cheap.
8. Mix level of fire, hits and footsteps (`R-2BEC9588`, `R-A08F0BB3`, `R-4F7B3707`, `R-74E86226`, `R-3E8B1D7F`), others' footsteps and jump packs (`R-7898C3A2`, `R-20F471C6`).
9. The first-person loop sounds when a match ends and a life ends (`R-5D1FAF41`), the shield regeneration start (`R-5C497E5C`).

**C. Stopwatch (phone video, frame count)**

10. Respawn wait and the label (`R-BED2640F`, question 12, 13): does it count from death, and is it 5 s?
11. Match length: the default score and time limit of a host-created match and the host options list (questions 12, 14); a tied timed match (question 3).
12. Kill feed hold and fade (`R-7BBB712E`), hit marker length, reward line timing.
13. Reaction locks (question 1: frontal stagger 1.07 s or 0.57 s, grenade stun) and the charge's distance (question 6, the Charge rows) with a stopwatch over a marked floor.
14. Jump pack: fuel per jump, recharge, a second jump, the pound recovery (the Jump pack rows), and chainsword out of a landing (question 4).

**D. Feel (play for a few minutes, then answer same, close or differs)**

15. Mouse sensitivity ratio between exploring and aiming (`camera_input_tuning` 100 : 60) and the aim-mode movement speeds (`R-582B236B`).
16. Weapon switch time and whether a shot can interrupt it (question 8), roll exits (question 9), the eight-direction roll (question 10).
17. Camera blend between hip and aim, release after a wall, hop and landing dips (`R-3ECF189B`, `R-3317BABF`, `R-916F55DB`).
