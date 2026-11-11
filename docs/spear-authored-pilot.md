# Power spear: the authored-animation pilot (hew 1), 2026-11-12

Musou Mode only (added content); presentation only. Branch `claude/spear-authored-pilot` from main `26bfaa20`. Brief: the owner-approved handoff
"Recreate the Musou spear animations without motion capture" (2026-11-11, stages 1 and 2) and the owner's 2026-11-12 direction: original keyframe authoring in Blender
(Python-assisted authoring, cleanup and IK), proved through one representative hew into the existing spear pose system before the set is recreated; **no motion capture and
no video-to-motion**; reference footage is for studying movement only; keep the original baseline and a rollback.

**Label:** *ours* (keyframes authored by us); a pilot, not artistically approved. Nothing here changes a timing, hit window, damage, reach, combo window, the protocol, the config hash
or the jump pack's flight (`ClawSpearClipsTests.The_jump_packs_flight_with_the_spear_is_left_as_it_was` is unchanged and passes).

## 1. Move manifest (what the full set will cover; this pilot touches only hew 1)

Times in seconds from the strike's start at the spear's tempo 0.85 (docs/claw-spear-animations.md section 2). "Routes" are what the client shows for each simulated step
(`ClawSpearClips.SpearStrike`, `NetworkView.SpearPose.cs`). The immutable contract for every row: the clip's length and hit time are the borrowed hammer strike's; the simulation's
damage, hit window, combo window, reach, recovery eligibility and lunge are untouched (a clip never moves the root); both grip sockets `RIG_Prop_RHand` / `RIG_Prop_LHand` hold the shaft;
`RIG_Pelvis` carries `RIG_SpineA`; bone lengths are fixed (aims and two-bone solves only); planted feet stay planted (`FootPin`).

| Action (input) | Current clip | Hit | Next legal at | Routes | Intended key poses | Proof clip |
|---|---|---|---|---|---|---|
| carry (idle, walk, run, sprint) | `spear_rest` | - | any | the hammer stance with no other action | right hand carries, blade outside the aim column | not in the pilot |
| light hew 1 (X, first or random pick) | `spear_hew1` | **0.711** | 1.098 | light step 1 | carry, coil, plant, contact, follow-through, finish hold, recovery | **this pilot** |
| light hew 2 (X, X) | `spear_hew2` | 0.667 | 1.138 | light step 2 | low-left coil, rising cut, high-right finish | next |
| light hew 3 (X, X, X) | `spear_hew3` | 0.824 | string ends | light step 3 | flat hew, right to far left | next |
| stun / snap cut (F) | `spear_stun` | 0.509 | 0.627 (to the slam) | heavy 1; also the X,Y and X,X,Y sweeps (steps 2, 3) | whip up behind the shoulder, flat snap cut | next |
| massive stab (F, F) | `spear_stab` | 1.294 | chain ends | heavy 4, 5 (both slam sides) | drawn back at the hip, lunge, thrust | next |
| ground pound dive | `spear_dive` | - | into the landing | the pack's pound | point-down, both hands on the shaft | next |
| landing on a hit / plain landing | `spear_impale` / `spear_land` | 0 (the landing) | - | server's impale evidence | drive-down and hold / spear up beside him | next (the impale's left hand on the blade fittings is an open defect) |
| jump-pack flight (W after the long jump) | none (protected) | - | - | - | **unchanged** | pinned by test |

Reference: the halberd "Wrath hew from Field Guard" clip **is** in the owner's media folder (`<media folder>\references\spear-reference\`: `spear-reference.mp4`, `frames\f_0001...`, three contact
sheets); the repository still holds none of it. Frames 4, 12, 19, 30 and 36 are guard, steer (hands overhead), swing round behind the head, the hew and the low follow-through. It was
**studied for poses and mechanics only**. This pilot's wind-up is deliberately more compact than the reference's overhead steer (the owner asked for a compact wind-up): the shaft is gathered
back to the right with the hands low rather than lifted overhead, so it does **not** claim to match the reference's steer.

## 2. Why hew 1

It is the swing every string and most random picks start from standing; it is also the one clip with the longest approved recovery to the carry, so the pilot exercises the whole life of a
clip (carry hand-over in, grip slide, coil, plant, contact, follow-through, brief finish hold, the approved recovery, and the hand-over into hew 2 and hew 3 at the combo window).

## 3. The pipeline

```
smtool spear-rig-dump                    install -> %LOCALAPPDATA%\sm-reimpl\spear-authoring\rig.json     PRIVATE: the marine's skeleton and the hammer idle's rest pose
blender -b --python tools/blender/spear_hew_author.py -- --rig rig.json --export src/SmContent/Extras/Musou/Authored/spear_hew1.json [--blend f.blend] [--renders dir]
                                          Blender: F-curves on a control empty -> Python solve -> 60 Hz channel samples   COMMITTED: original data only
AuthoredPoseClip.FromJson / LoadEmbedded  JSON -> WeaponPoseClip (the existing overlay contract); the strike's identity and timing are the template's
ClawSpearClips.SpearHew1                  = authored, or SpearHew1Original with --old-spear-hew
```

- **Authoring (`tools/blender/spear_hew_author.py`, `spear_hew_render.py`).** The marine's proportions and joint positions come from the install's own rig through `smtool spear-rig-dump`
  into the private folder (refused inside the repository or the install); nothing of it is committed. The controls are Blender F-curves (explicit keys, Bezier, linear and ease-in
  interpolation) on one empty: the hips' yaw, pitch, roll and shift, the spine, head and clavicles, each foot's offset, and the arms by where the right palm is and where the shaft points in the
  chest's frame (the hands' spacing, the grip slide and the left hand's take). A Python solve lays them with the runtime overlay's own arithmetic (FK with the pelvis carrying the spine, the
  overlay's two-bone leg solve, a two-bone arm solve onto the palm sockets with the elbow's swivel chosen to keep the upper arm off the shaft). The solve **is** the authoring-side IK; the
  runtime does not re-solve the arms (it lays the exported aims), so no constraint is applied twice. A `--blend` saves the scene for editing in Blender; `--renders` writes Workbench stills.
- **Export (`spear_hew1.json`, format `sm-spear-authored-clip/1`).** Units metres, degrees, seconds; the axes, the base stance (the hammer's idle, `SM_2HM_NAV_TH_IdleL_S_01`, its stance frame read
  from the thighs), the bone map, the grip sockets, 60 Hz sampling (plus exact keys at the wind-up's apex and the hit) and the timing (length 3.138 s, hit 0.711 s) are in the file. The channels
  are the overlay's own kinds (Turn, Aim, Move, Foot, and the two grip tracks), so **WeaponPoseClip loses no motion and no extension was needed**. The file also carries a `check` block (Blender's
  solved joint positions) which the runtime ignores.
- **Adapter (`src/SmContent/Extras/Musou/AuthoredPoseClip.cs`).** 190 lines or fewer, no new runtime concept: the samples become linear keys of the same channel types; the adapter **refuses**
  data whose name, length, hit time or base stance differ from the strike it replaces, so an authored clip cannot move the hit. Deterministic. `SM_AUTHORED_CLIP_DIR` points it at a folder for
  an authoring preview without a rebuild (not for a match).
- **Deterministic regeneration.** The same `rig.json` and script give the same JSON; the committed JSON is the runtime's source. Blender 4.5.15 (`%LOCALAPPDATA%\sm-reimpl\tools\blender-4.5.15-windows-x64`), run only with `-b`.
- **What is authored and what is derived.** Authored: the timing of the coil, the hips' acceleration into the hit, the planting, the finish, the hold, the shaft's route, the foot steps. Derived (runtime
  or solve): the arm bones' directions from the palm targets, the legs from the feet (runtime), the spear's placement on the sockets (`SpearHold.Place`). **Carried over from the previous pass**
  (and named so): the swing's palm and shaft geometry between 0.50 and 0.80 s (measured from the previous clip's cleared keys, `SWING_ARMS`, with the palm held 0.12 m further right around 0.65 s) and the
  recovery's arm aims from 0.95 s (`RECOVERY_AIMS`, the approved recovery and its hand-overs); the hips' recovery keys and the left foot's steps are the previous pass's. They were kept because the
  clearance and hand-over tests cleared them; re-authoring them is the full-set step.

## 4. The hew

Timeline (seconds from the swing's start; hit 0.711 s as before): 0.00 carry; 0.00-0.20 the shaft slides to the melee grip (0.95 to 0.30 m from the butt) and the left hand takes it; 0.20-0.52 the coil:
the hips turn back to the right (to -8 degrees past the stance), the weight drops onto the right leg, the left foot lifts, the shaft is gathered back to the right with the hands low (**apex 0.521 s**,
0.19 s before the hit); 0.52-0.711 the acceleration: the hips' yaw eases in (quadratic: its rate is 0 at the apex and about twice the average at the hit), the weight shifts to the left leg first (0.64 s), the left foot
plants by 0.64 s; the blade passes the target at 0.711 with the hips square to it; 0.711-0.95 the follow-through to low left; **0.95-1.18 the finish hold** (the hips move under 4 degrees); the approved recovery
from 1.18 to the carry. The next light swing may start at 1.098 s as before and the hold yields to it through the 0.14 s hand-over.

## 5. Checks (filtered runs on this branch)

See docs/milestones.md for the counts. `SpearAuthoredHewTests` (adapter identity and timing, refusal, determinism, shape: compact apex, acceleration, finish hold, units and axes in the export, and with the
install: **the in-game overlay's joint positions against Blender's at the same times, worst 0.2 cm over 13 joints and the whole clip**), `SpearClearanceTests` (the body capsules, the palms on the shaft, the planted feet, the
wide arc, the hand-overs into hew 2 and hew 3 and out to the carry), `ClawSpearClipsTests` (timing, flight, the carry, keys), `FootPinTests`.

## 6. Open

- The in-game captures are previews of the pilot; the owner has not approved the look. Open visual questions are in the report that came with this branch and in section 7.
- Still carried from the previous pass: the swing's mid-cut palm geometry and the recovery's arms (section 3). The full set re-authors them.
- Known defects not touched: the impale's left hand riding onto the blade fittings, the right-strafe sprint's narrow column margin, the landing hand-over's one-frame allowance (docs/claw-spear-animations.md).

## 7. Evidence

Written after the captures (see the lead's report): `D:\sm-reimpl\media\spear-pilot\{before,after}\` (MP4s, stills, contact sheets), each named for what produced it (`match-` the real Musou match through
`--tfw-spear-film`, `stage-` the preview stage `--claw-spear-test`, `blender-` the authoring renders).
