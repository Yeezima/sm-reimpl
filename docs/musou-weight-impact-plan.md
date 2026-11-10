# Musou weight and impact pass: plan (claude/musou-weight, 2026-11-10)

The owner's brief (2026-11-10, verbatim): "in general improve the weightness and add accompanying supporting audio and visual
effects. the player model should make a bit of dust/dirt/debris move when he lands, runs, jumps, attacks, rolls, any movement or
action basically. these puffs of debris only appear when his feet or some part of him touches the ground. improve the animations to
make them have the same improved punch and visual feedback and storytelling. add more transition animations and sync everything up
to appear like a smoother, more powerful visual animation experience. overall the game feels floaty, make it feel decisive,
powerful, weighty, impactful." On scope: "Weight and impact pass is Musou only".

Scope: Musou Mode only, added content (`docs/added-content.md`): everything behind the Musou content gate, through `Extras*`
hooks; Annihilation, Seize Ground and the faithful build stay byte-for-byte the same. Client presentation only (the client does
the spectacle): no simulation, wire, protocol, config-hash or rules change, no damage, reach, timing or balance change.
Branch `claude/musou-weight` from `origin/main` 085cdf72. Phase 1 = this plan; no product code yet beyond one test aid.

Labels: **measured** = numbers from the baseline runs below; **expected** = reasoning, not measured; **ours** = our choice, an
estimate for the refine register.

## 1. Baseline

### How it was recorded

- Test aid `--tfw-weight-capture` (new, `client/Extras/Musou/Tfw/NetworkView.TfwWeightCapture.cs`): a scripted Musou run on a
  127.0.0.1 server (`SM_TFW_KEEP_HUMAN=1`): deploy on lane B, walk 3 s, sprint 3 s, stop; three rolls (forward, left, right), or
  with `--tfw-weight-pack` the Assault's pack dash, full jump and landing, ground pound and dash attack; then towards the crowd,
  2.5 s of aimed fire, six swings (light, light, stun strike), 5 s standing to take hits. `TFWWEIGHT mark` lines carry the
  process frame (a Movie Maker clip is one frame a frame); `--tfw-weight-log` adds a line a frame (speed, height, vertical speed,
  grounded, jump phase, evade ticks).
- Clips: Godot Movie Maker through `godot.cmd --windowed` (the private desktop), 24 fps, 1600 x 900, FSR 2 performance (to keep
  the recording near real time), no sound. Measurements: the same aid with `--transition-probe` (pose jerk at state switches),
  `--anim-probe` (planted-foot slide), `--log-weight-feel` (every camera impulse).
- Media (`<media folder>` = the owner's media folder): `<media folder>\previews\musou-weight\baseline\`
  `tactical-player.avi`, `tactical-third.avi` (Tactical: run, sprint, stop, rolls, bolter, knife, hits taken), `assault-player.avi`,
  `assault-third.avi` (Assault with power sword: pack dash, jump and landing, pound, dash attack, pistol, sword, hits taken).

### Measured (Tactical and Assault, 2026-11-10, main 085cdf72)

| What | Measured | What it means |
|---|---|---|
| Run start, 0 to 90 % of 5.85 m/s | 0.38 to 0.39 s | the simulation's acceleration (faithful; not changed) |
| Sprint start, run to 90 % of 9.0 m/s | 0.21 to 0.26 s | as above |
| Sprint stop, 8.4 m/s to half / to under 0.1 m/s | 0.38 s / 0.54 s | as above; the body shows no plant or lean |
| Roll (forward, left, right) | 1.17 s, 9.3 to 10.0 m; speed 18 m/s on the first frames, then 10 to 14, a tail to 0 | the clip is authored for 8 m (`SM_EVD_R2R_Roll_*_8m`) |
| Roll planted-foot slide | median 3.9 to 4.5 m/s, p90 14 to 22 m/s | the feet and knees skate through the roll: the biggest single floaty cue |
| Run planted-foot slide, straight / side headings | median 0.51 / 4.8 m/s (p90 1.5 / 7.7) | straight runs are fine; side and diagonal headings slide |
| Steady frames with a foot planted, diagonal run | 33 to 35 % | the feet rarely read as planted |
| Pose jerk at switches (p90, cm/frame²) | ready to idle 88, idle to ready 108, ready to melee 81, ready to sprint 65, move to sprint 49 | visible pops, most at the weapon-ready changes |
| Body turn at move to ready | median 106 degrees a frame | the body snaps to the aim heading |
| Melee clips, steady | jerk p99 67 cm/frame², turn p99 41 degrees a frame | fast, but the body travels without contact cues |
| Ground contact effects | jump-pack landing and pound only (the data's effects plus ours) | no dust or debris for steps, sprints, stops, rolls, melee steps, hits taken |
| Dust and smoke | not drawn anywhere (the owner's no-smoke rule, 2026-10-29) | a dust puff needs an exception (question 1) |
| Melee contact accent | none: the owner removed the hit-stop and the melee hit shakes on 2026-10-29 | contact reads from the slash and sound alone (question 2) |
| Camera jolts in a Musou fight | up to 16 a second, 0.1 to 0.8 degrees each, from every blast within 25 m | constant small jitter: the own impacts have no contrast left |
| Roll, dash and landing sounds | the rolls carry no sound tags; the footsteps play at -3 dB (owner) | rolls and body contacts are silent |
| Frame log rate | 60 to 100 frames a second | the measurements are per rendered frame |

Jump pack (Assault, measured from the frame log):

| Move | Measured | What it means |
|---|---|---|
| Full jump | apex 17.5 m; falls the last metres at 25 to 30.6 m/s; stops in one tick; `Landed` 0.375 s, then free | the fastest touchdown in the game ends with one camera dip of 1.2 degrees and the data's landing effect |
| Ground pound | dives at 25.7 m/s down and 30.6 m/s forward; on impact 6 m/s forward at once; `Recover` 0.2 s | the one move that reads heavy (shockwave, crater, chunks, shake) |
| Dash | 0.9 s; 19.8 m/s at the start; lands at 9.8 m/s down and 11 m/s forward, sliding on at 8 m/s | lands and slides without a contact cue (no puff, sound or dip) |
| Dash attack | 0.8 s from press to landing | as the dash |

## 2. Why it feels floaty (diagnosis)

1. **Contacts are not shown.** A foot, knee, hand or the whole body meets the ground without dust, grit, sound or camera. The eye
   reads weight from contact: without it the marine glides. The one place it is shown (the pound) is the one move testers call
   heavy.
2. **Feet skate.** The roll covers 9.3 to 10 m with a clip authored for 8 m, under a speed curve that is not the clip's: knees and
   feet slide at 4 m/s median and up to 22 m/s. Side and diagonal runs slide too (median 4.8 m/s).
3. **No anticipation or settle.** Stops, landings and roll ends end in the next stance at once; the body never compresses (knees,
   pelvis) or leans against the momentum. The data's landing crouch is used only above 8 m/s falls and at 0.3 to 0.8 weight.
4. **Pops at state changes.** Ready, idle, melee and sprint switch with jerks of 50 to 110 cm/frame² and a 106 degrees a frame
   body turn: snaps read as weightless (a heavy body cannot change pose in one frame).
5. **Impacts have no contrast.** With 16 small jolts a second from far blasts, the camera is always moving a little, and the own
   hits add nothing to it. The owner removed the melee freeze and shake in October; nothing took their place.
6. **Silence at contacts.** Rolls, body falls and dash landings make no sound; heavy steps sound like light ones.

## 3. The plan, by part

Every part: Musou Mode only (the content gate and `Extras*` hooks), own marine first, allies second (the client does the
spectacle), Orks unchanged except where named. Every number is ours, tagged for the refine register. Every part has a restore
switch and the Camera shake / Screen flashes options keep applying.

### A. Ground-contact puffs (the owner's dust, dirt and debris)

- **When a contact happens.** Two sources, so every action is covered:
  - The clips' own foot tags (`EVT_Left_Foot`, `EVT_Right_Foot`, their `_Rush` sprint forms, `EVT_Bullrush_Stop_*`): run, sprint,
    sprint start and stop, the melee combos' steps (every `SM_MEL_CS_*` combo carries 2 or 3 foot tags), the jump-pack dash attack,
    the low-cover evades. The sound system already reads them (`NetworkView.Sound.cs`); one new `Extras` hook passes each footfall
    with its foot and surface.
  - A bone-contact detector for clips without tags (the rolls, landings, knockdowns, get-ups, deaths, the pack dash): per drawn
    marine, per frame, the toes, knees, hands, pelvis and shoulders against the ground height under the character (one ray a
    character a frame, the sound's surface ray); a contact is a bone entering 6 cm above the ground moving down, or sliding along
    it faster than 2 m/s (a skid trail every 0.08 s). At most 8 marines: cost is a few dozen bone reads a frame.
- **What it looks like.** From the install's own effects: `knockback_kick_footslide` (a light and a dark dust puff, `smoke_puff2`),
  `jump_land`, `jumppack_land_light` / `_normal` / `_heavy`, and the surface's small-arms impact effect (`MaterialEffectTable`: grit,
  chips and debris for dirt, gravel, stone, sand, metal) scaled down; Ashfall's ash tint where the surface is the map's default.
  Strength by the contact: a run step small, a sprint step and a stop plant larger, a melee step with a forward kick of grit, a
  roll's knee and shoulder contacts plus the skid trail, landings by fall speed, hits taken by the knockback's slide.
- **Path.** On the GPU spray path (`musou/gpu-particles`): one effect a contact, a per-marine rate cap (one puff a foot per
  0.12 s), nothing past 45 m from the camera, allies at half size and count.
- **Needs** the owner's word on dust (question 1): the no-smoke rule hides dust sprites everywhere; Musou's blood mist already has
  an exception (`FxPlayer.KeepSmoke`). The plan asks for the same exception for these contact puffs only, small and short-lived.

### B. Weight in the body: landings, stops and rolls

- **Landings** (the pack's full jump, the dash, drops): a procedural settle over the clip through the pose-overlay hook
  (`CharacterViewer.ExtrasPoseOverlay`, `claude/librarian-lightning-ab6ddc`): pelvis drop and knee bend scaled by the fall speed
  (ours: 6 to 14 cm over 0.06 s down, 0.25 s back), the data's landing crouch from 4 m/s falls up (Classic keeps 8 m/s), and a
  puff ring.
- **Sprint stop and hard turns**: a short lean back against the momentum and a planted foot (the stop clip's tag) with its puff;
  the body turn eased over 0.1 s instead of snapping (the 106 degrees a frame at move to ready).
- **Rolls** (MW3 measured, 2026-11-10: dropped. The body follows the clip's own extracted path to within 0.1 m and the clips travel 8.9 to 9.6 m, not 8 m, so the rate is already right; the sliding is the clip's in-place pose against its extracted motion. The roll end settles; the rest stays as it is): the clip's playback rate fitted to the distance the simulation moves (9.3 to 10 m against 8 m) and its root speed to
  the simulation's speed curve, so the knees and feet stop skating (target: planted slide median under 1 m/s); the roll's end
  settles with a crouch and puff; the shoulder, hip and knee contacts make puffs and sound.

### C. Attack punch and storytelling

- **Anticipation and release**, time-remapped inside each melee clip with its start, hit and end times kept on the server's (no
  timing change): wind-up at 0.85 x, strike at 1.25 x, the recovery after the hit eased; heavy strikes more than light.
- **Contact** (question 2): either nothing new (the slash, power flash, sound and Ork reaction from the combat-feel pilot carry it),
  or an attacker-only pose hold of 2 to 3 frames (40 to 60 ms) on heavy strikes and kills, never a screen freeze, Orks moving on,
  plus a camera nudge along the swing (not a shake; capped, the Camera shake option applies).
- **Follow-through**: the melee steps' foot tags plant with puffs and a grit kick; heavy strikes scrape the ground.
- **Orks**: the pilot's push back scaled by the weapon (a hammer pushes 0.35 m, a knife 0.12 m; ours), no 180-degree turns.

### D. Transitions and blends

- The pops at weapon-ready, idle, melee and sprint changes: blend floors and a pose-velocity limiter for the own marine in Musou
  (target: switch jerk p90 under 40 cm/frame²).
- More transitions from the install's own clips, wired only where the simulation's state already says the move happens. The
  install has 613 marine clips (`smtool graph-dump --animations`); ones the client never plays yet that fit this pass:
  - `SM_TRN_*_180Turn_to_Sprint` (1.23 s): the pivot into a sprint on a reversal (read by `SprintTransitions`, never played);
  - `SM_NAV_Bullrush2Idle_F_HalfSpeed_01` (0.9 s): the shorter sprint stop, for stops from below full sprint speed (today every
    stop plays the 1.33 s `Bullrush2Idle`);
  - `SM_REC_Light_Itrpt_FHR_01`, `_BHL_01`, `_FLL_01`, `SM_REC_Hvy_Itrpt_FHL_01`: the other hit directions (two of the six are used),
    so a hit taken turns the body from the side it came from;
  - `SM_REC_Sprint_Stagger` (1.6 s): a hit while sprinting; `SM_REC_Stun2Idle_D_01` (1.3 s): the stun's recovery;
  - `SM_REC_MP_MidAirImpact_01`: a hit in the air (the jump pack);
  - `SM_NAV_CS_Dropdown` / `_Landing`: the drop and landing for the light classes (only the heavy classes' `SM_NAV_TB_*` play now).
  Not used: the hammer jump-pack set (`SM_JMP_TH_*`, hammer classes only; later with the new weapons), the slope clips (need the
  ground's slope; later), the combat-shield rolls (another class's).

### E. Camera

- Musou's blast jolts thinned: only blasts within 12 m (25 m now) and own grenades, at most the strongest one a 0.25 s window
  (from up to 16 a second), so the own impacts read.
- Own impacts as pitch dips and short pushes, not shakes: landings (exists, extended to the dash and roll end), the pound
  (exists), heavy strikes and kills (question 2). All through `CombineShake` (no stacking) and the Camera shake option.

### F. Audio (install sounds only)

- Rolls, body contacts and dash landings: the data's landing and footstep events at the contact, on the surface; heavy steps
  (sprint, stops, melee plants) with the `_Rush` footstep forms.
- No gear rattle (the owner's 2026-10-28 rule stays), nothing louder than the data, every new voice through the instance limiter;
  checked against the sound-overload A/B work (`musou/sound-ab`) before it lands.

### G. Cost

- Puffs only for marines (at most 8), never per Ork; one effect per contact on the GPU path. Expected well under 0.2 ms a frame
  on this PC at 1,000+ Orks (measured in each milestone's capture with `--frame-profile`).
- Old GPUs (Compatibility renderer, no GPU sprays: the CPU path, 160 effects): puffs at half count, the lowest effect priority, so
  a full fight drops them before anything else; no new shaders.

## 4. Milestones

| # | Milestone | Restore switch | Estimate |
|---|---|---|---|
| MW1 | Contact detection (foot tags and the bone detector) with a log and a probe, no visuals; the contact counts per action as evidence | `--no-musou-contacts` | 0.5 day |
| MW2 | Ground-contact puffs on the GPU path, by surface and strength; allies at half; old-GPU halving | `--no-musou-puffs` | 1 day |
| MW3 | Body weight: landing settle, stop lean, eased body turn, roll fit (rate and root speed), roll end settle | `--no-musou-weight-body` | 1.5 days |
| MW4 | Attack punch: time remap, follow-through plants; contact accent per the owner's answer; Ork push by weapon | `--no-musou-punch` | 1 day |
| MW5 | Transitions: blend floors, pose-velocity limiter, install transition clips | `--no-musou-transitions` | 1 day |
| MW6 | Camera thinning and own-impact dips; audio at contacts | `--no-musou-impact-camera`, `--no-musou-contact-sound` | 0.5 day |
| MW7 | Captures (before / after, player and third person, Forward+ and Compatibility), 1,000-Ork cost run, review | - | 0.5 day |

`--musou-old-weight` turns the whole pass off. Each milestone: builds, the Musou tests, a short before / after clip in
`<media folder>\previews\musou-weight\`, refine-register rows (every value ours, low confidence), spec rows, a
`docs/changes-from-original.md` row; the whole pass lands after the owner has played it.

## 5. Questions for the owner

1. **Dust**: may Musou's ground-contact puffs draw dust (small, short-lived), as an exception to the no-smoke rule of
   2026-10-29, like the blood mist? Without it they are grit and debris only.
2. **Contact accent**: you removed the melee "stop hit / screen freeze / shake" on 2026-10-29. For Musou only: no freeze and no
   shake, but may heavy strikes and kills get a 40 to 60 ms hold of the own marine's pose (the Orks keep moving) and a small camera
   push along the swing? Or keep contact to the slash, flash, sound and Ork reaction only?
3. **Whose puffs**: every marine (allies at half), or the own marine only?
4. **Retiming**: may melee clips play their wind-up slower and their strike faster, keeping the hit and end times (no gameplay
   change)?
5. **Camera**: may Musou thin the far-blast jolts (within 12 m only, one per 0.25 s) so your own impacts stand out?
