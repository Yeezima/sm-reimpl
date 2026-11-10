# Lightning claw and power spear: strike animations (preview, 2026-11-10)

**Status:** animation and presentation only; no gameplay, timing or rule change. Musou Mode only (added content, `docs/new-weapons-design.md` sections 5
and 6). The spear's clips (approved, sixth pass) play in a Musou match since 2026-11-10 over W3's simulated spear (section 4b); the claw keeps the power
sword's clips in a match (the owner's choice), so its clips run on the preview stage only.
**Owner's review of the first previews (2026-11-10):** spear approved as the base, with the rest pose "upright at his side" (done: section 3) and
the impale's release kept at 0.5 s; claws "redo: too stiff / small motions, reads like a sword; keep the diagonal rake" (done: the second pass of
section 3, every claw strike re-authored with the whole upper body driving it, the off hand bracing, and claw moves: rake, gut, stab, rip, overhead rake).
**Second review (2026-11-10):** "no part of the character's body should remain still through this.. he should be putting his entire body into the
strokes", "still too stiff / small", "add pelvis now", "keep the stab-and-rip pair" (done: the third pass, section 1's Move and Foot channels and the
leg solve, section 3: the pelvis turns, drops and shifts on every strike, a lead foot steps and replants, planted feet pinned by the solve).
**Spear, fifth pass (2026-11-10, "redo again" with a reference clip of a halberd's "Wrath hew from Field Guard", kept out of the repository):**
"each light attack combo swing should be a different hewing cut, and the F + F stun strike combo should end in a massive stab similar to the hammer
swing overhead from the stun strike combo F + F"; "while running the character model should grip higher up on the spear shaft towards the blade,
then slide the spear inside his hand to hold it lower towards the butt end of the spear for melee combat" (done: section 3's hews, stun and stab, the
`GripPoint` track and the slide, section 1).
**Spear, sixth pass (2026-11-10, "redo again"):** "doesn't match the reference, still too stiff / small, carry or grip looks wrong, the legs becoming
disconnected from the body would be a good thing to fix, the torso should remain intact and not bend much - straight back the entire time, the blade
needs to face forward at rest". The leg bug and its fix: section 1 (the carried spine); the straight back: `ClawSpearClips.SpineLimit*`, pinned by
`The_spear_keeps_a_straight_back_and_leans_and_turns_from_the_hips`; the hews re-authored to the reference's shapes (section 3); the blade's edge forward
at rest and the carry 10 degrees from upright (section 1's grip row).
**Spear in a match, no-clipping pass (owner's review of the wired stills, 2026-11-10):** "the blade is facing away from the character model at idle and when running, and the spear
should be held out away from the body at idle and when running. Make a pass at all the animations to ensure the spear isn't going through the player models body at any time (clipping)";
the impale "Point-down". Done: the carry held out wide in the stance's frame with the edge out (section 1's grip row), every clip measured against the body's capsules and re-posed where it
passed through (section 3b), the impale point-down and held (section 3).
**Claws approved ("approve, refine later"). Spear, fourth pass (2026-11-10, superseded by the fifth):** "carried 1 handed in the right hand when running or idling" /
"right hand carries, both swing"; sweeps "wider and longer" (300 degrees, 15 m); "the pelvis and legs should be greatly active in facilitating a
powerful swing animation" (done: the Grip track, section 1; the sweeps of section 3 re-authored with the claw's full-body treatment, 300 degrees of
blade travel and a step into each; the impale's release stays at 0.5 s).
**Label:** ours (keyframes authored by us), laid over original clips read at run time; stand-in weapon models are placeholder demonstrations.

Previews: `<media folder>\previews\claw-spear-anims\` (stills per view and moment, one clip per weapon and view, a README with the table below).

## 1. Approach: our keys over the install's stances, through the existing pose hook

The install's clips are Havok spline animations decoded at run time (`HavokSplineAnimation`, sealed, built only from the packfile), so a new
clip cannot be injected into the viewer's clip pipeline without copying original data or changing the format library. Instead the strikes are
**our own keyframe data on the marine skeleton** (`src/SmContent/Extras/Musou/ClawSpearClips.cs`, Godot-free), **laid over an install clip as a
procedural pose** through the hook Musou's Librarian cast pose already uses, `CharacterViewer.ExtrasPoseOverlay` (its one implementation in
`client/Extras/Musou/Librarian/CharacterViewer.LibrarianCast.cs` now calls the Librarian's pose, then `WeaponPoseOverlay()` in
`client/Extras/Musou/Weapons/CharacterViewer.WeaponPose.cs`). No new hook, so `ExtrasBoundaryTests.Hooks` is unchanged; the only core-file edit is the
test view's name in `ModelViewer.FrontEndExcluded`.

The alternative, additive layers over existing clips (`CharacterViewer.SetLayer` with Havok additive clips), was not taken: the install has no
claw or spear clips to layer, and a hand-authored additive clip would still have to be a `HavokSplineAnimation`.

How a clip is defined (`WeaponPoseClip`):

| Piece | What |
|---|---|
| Base clip | the install clip the keys are laid over: the one-handed melee idle `SM_NAV_IdleL_CS_01` for the claw (pistol in the left hand, the right arm hanging low and back), the hammer's two-handed idle `SM_2HM_NAV_TH_IdleL_S_01` for the spear, the hammer's dive loop and landing `SM_JMP_TH_GP_Loop_01` / `SM_JMP_TH_GP_Impact_01` for the dive. |
| Turn channel | a bone turned about its own origin by yaw / pitch / roll (degrees) in the stance's frame (up, the body's right and front read from the hips, which no clip moves): the spine, head, clavicles, hands. Zero = the base pose. |
| Aim channel | where a limb segment points (elevation above the horizontal, azimuth from the front toward the right), reached from the base pose by the shortest turn, as far as the key's weight (0 = base, 1 = aimed): the upper arms and forearms. Absolute, so a pose means the same over any stance; the numbers were read off the base poses with the stage's `--pose-log`. |
| Keys and easing | each key eases in from the one before: `In` (the wind-up gathers), `Hold` (the claw's cocked apex sits 0.035 s, two frames, so it reads before the arc), `Snap` (97 % of the travel in the first half of the segment: the strike), `Linear` (a blade passing the front of a sweep without pausing), `Out`, `Over` (a follow-through that passes by a tenth and comes back), `Smooth` (settles). |
| Spear carry and the two grip tracks | `spear_rest`, a held pose over the hammer's idle: the spear in the right hand alone at the right side, the left arm hanging free (the owner: "right hand carries, both swing"). Two tracks on a clip place the spear: **`Grip`** (0 to 1, `WeaponPoseClip.GripAt`) says how far the left hand is on the shaft (0: carried one-handed, 1: the shaft on the line through both hands); **`GripPoint`** (`GripPointAt`) is the grip contract: where the right hand holds the shaft in **metres from the butt** (`CarryGrip` **0.95** at rest and running, `LowGrip` **0.5** for the hews, `StabGrip` **0.3** for the stab). A model puts the hand at exactly that point of its own shaft: `SpearHold.Place` (SmContent, pinned by `ClawSpearClipsTests`) places the stand-in and, in a match, W3's model (`WeaponRig.PlaceSpearOnHands`); W3's own axis is `SpearHold.AxisAt` (its model's origin 1.05 m up the shaft: 0.95 m is -0.10, and W3's own run grip 1.34 m is its `SpearGripHigh` 0.29), so the meaning does not change when a model's grip constants do (review of 2026-11-10: the earlier `GripSlideAt`, a 0..1 slide onto W3's constants, is gone). At the carry grip (since the no-clipping pass, 2026-11-10) the right hand is held out wide of the hip (the upper arm -30 degrees elevation, 85 azimuth; the forearm -15, 55) and the shaft leans from it to `SpearHold.CarryElevation` 55 degrees at `CarryAzimuth` -15 (forward, a little in at the top), in the stance's frame, so the blade stands forward of the shoulder pad and the butt hangs behind and outside the leg, clear of the trailing leg's kick in the strafes and sprints (0.107 m at the least over every hammer stance clip; the butt at least 0.56 m off the ground, where it was 0.14 m running: the owner's "just above the ground" gave way to the legs, for him to judge). Before, the shaft leaned 80 degrees toward the front from a hand hanging at the side and rose beside the head through the pad. one-handed at the low grip the shaft stands upright. The carried blade's edge faces away from the body (owner, 2026-11-10: "the blade is facing away from the character model"): `SpearHold.EdgeAzimuth` 60 degrees from the front toward his right, forward and out, squared to the shaft; once both hands are on the shaft the edge faces the body's front (the strikes as approved), turning up only when the shaft itself points near it; W3's model mounts its blade with the edge on that axis. A strike begun from the carry slides the shaft through the hand to the low grip over `GripSlide` 0.25 s while the left hand joins (the shaft moving in the hand, not a pop); chained strikes keep the low grip; the string's last strike slides it back up over `GripSlideBack` 0.4 s as the left hand lets go. While a clip holds the hand in a match, W3's own slide (`WeaponRig.GripSlide`) is held at `SpearHold.SlideAt` of the clip's point, so W3's mount carries on from there when the clip stops. The stage shows the carry in every spear idle. |
| Move channel | a shift of a bone's origin in the stance's frame (metres forward, right, up): the pelvis drops, drives forward and shifts its weight (third pass). Applied in the parent's frame after the bone's turn. |
| Foot channel and the leg solve | a foot's offset from where the stance plants it (metres; up > 0 lifts it for a step, a new forward / right offset with up 0 replants it there). Whenever a clip is laid, each leg is solved (the core's legs, `ResolveLegs`, so the Ork rig's `RIG_LLeg_*` as well as the marine's; two bones: the thigh aims at the new knee, the calf at the foot, the knee bending the way the stance bends it, the foot keeping the stance's orientation) so the foot stands at its planted spot plus the offset; a foot with no channel is held on its spot. So the pelvis and spine move freely and a planted foot never slides; the thighs, calves and toes are never keyed, and the root never moves (the simulation's position; `ClawSpearClipsTests` pin all of this). Chained strikes start with the feet where the one before planted them. |
| The carried spine (the leg bug) | The marine rig hangs `RIG_SpineA` **beside** `RIG_Pelvis` under `RIG_Root`, not under the pelvis (`smtool graph-dump --bones`), while the thighs are the pelvis's children. So a pelvis `Move` or `Turn` on its own moved the hips and legs and left the torso where the root put it: the body's halves parted at the waist ("the legs becoming disconnected from the body", the owner's review of the fifth set). The overlay now carries the torso with the pelvis as one rigid piece (`CharacterViewer.WeaponPose.RigidWith`: `RIG_SpineA` takes the pelvis's turn about the pelvis's origin and its shift, before its own keys are laid), a carried bone is ordered right after its carrier whatever their indices, and no bone is ever scaled or stretched (turns and shifts only; the leg solve rotates the thigh and calf about their lengths). Every clip gets this, the approved claws included: their keys are unchanged, their torso now stays on the hips. The Musou weight pass's settle (MW3: a landing's or a stop's pelvis drop) carries the torso down with the pelvis the same way (`RigidCarriedWith`). |
| Hand-over | a chained strike fades from the previous clip's last pose over 0.14 s (`WeaponPoseState.From`), turns, shifts and foot offsets alike, so the overlay never pops at a combo step. |
| Stand-ins | `ClawSpearStandIns.cs`: the claw follows the right forearm and hand (gauntlet, pistons, brass bands, four talons 1.2 forearms long curving toward the palm); the spear follows the line through both hands (2.8 m black shaft, bronze collar, crossguard and ring, 0.9 m single-edged blade glowing blue, deeper at the base; the right hand is the rear hand 0.5 m from the butt, the blade beyond the left hand). The glow rises round the hit. |

**The hit time is the data's.** Every clip's length and hit time is the borrowed strike's divided by the design's tempo (`ClawTempo` 1.25,
`SpearTempo` 0.85), and the keys are built from those two numbers (anticipation = hit − wind-up, follow-through = hit + x). In a match the spear's
clip is shown at the strike's drawn time (section 4b), on the hit the simulation strikes at; nothing in the simulation changes for the animation.

## 2. Timing table (seconds from the strike's start)

| Clip | Borrowed strike (data) | Length | Hit (design) | Wind-up apex | Follow-through | Settle | Chains at (preview) |
|---|---|---|---|---|---|---|---|
| `claw_x1` diagonal rake | sword X1 `SM_MEL_CS_ATK_F_01_D_3.5m` 1.600 s, hit 0.320 | 1.280 | **0.256** | 0.170 (held to 0.205) | 0.381 | 1.150 | 0.694 (combo 0.867 ÷ 1.25) |
| `claw_x2` gutting rake | sword X2 `SM_MEL_PA_XATK_Combo2MsA_3.5m` 1.433, hit 0.287 | 1.146 | **0.230** | 0.150 (to 0.185) | 0.350 | 1.100 | 0.614 |
| `claw_x3` punching stab | sword X3 `SM_MEL_CS_XATK_Combo3_3.5` 1.767, hit 0.433 | 1.414 | **0.346** | 0.206 (to 0.241) | 0.461, held buried to 0.62 | 1.350 | 0.586 |
| `claw_x4` tearing rip | sword X4 `SM_MEL_CS_XATK_Combo4_3.5m` 1.667, hit 0.533 | 1.334 | **0.426** | 0.266 (to 0.301) | 0.566 | 1.250 | string ends |
| `claw_heavy` overhead rake | sword Y `SM_MEL_CS_XATK_Draw2_3.5m` 1.5, hit 0.33 | 1.200 | **0.264** | 0.164 (to 0.199) | 0.424 | 1.200 | - |
| `spear_rest` the high one-handed carry | hammer idle `SM_2HM_NAV_TH_IdleL_S_01` 1.5 | held | - | - | - | - | into any strike |
| `spear_hew1` wrath hew | hammer X1 `SM_MEL_TH_XATK_Combo2` 2.667, hit 0.604 | 3.138 | **0.711** | 0.521 (the steer 0.331) | 1.001 | 2.400 | 1.098 (combo 0.933 ÷ 0.85) |
| `spear_hew2` rising hew | hammer X2 `SM_MEL_TH_XATK_Combo3` 2.333, hit 0.567 | 2.745 | **0.667** | 0.487 | 0.947 | 2.300 | 1.138 |
| `spear_hew3` reverse hew | hammer X3 `SM_MEL_TH_HeavyATK02` 2.333, hit 0.700 | 2.745 | **0.824** | 0.624 | 1.004 | 2.400 | string ends |
| `spear_stun` snap cut (F) | hammer Y interrupt `SM_MEL_TH_Ytap_ATK` 1.667, hit 0.433 | 1.961 | **0.509** | 0.369 | 0.629 | 1.700 | 0.627 (`BRANCH_To_Slam` 0.533 ÷ 0.85) |
| `spear_stab` massive stab (F, F) | hammer Y,Y slam `SM_MEL_TH_YATK_Displace2` 3.0, hit 1.1 | 3.529 | **1.294** | 1.094 | 1.494 (held to 1.9) | 3.000 | chain ends |
| `spear_dive` point-down | hammer dive loop `SM_JMP_TH_GP_Loop_01` 1.767 | held | - | - | - | - | into the landing |
| `spear_impale` | hammer landing `SM_JMP_TH_GP_Impact_01` 1.067 | 1.067 | **0.000** (the landing) | - | 0.080 (drive-down) | 1.000 | - |

The design's hit times (section 5: the sword's ÷ 1.25; section 6.1: 0.71 / 0.67 / 0.82 s; section 6.2: the impale on landing) are met exactly;
`ClawSpearClipsTests` pins them. Hew 3's flat cut passes the front at a constant rate through its hit; every spear cut's power is the hips' turn, drop
and step with the back straight (section 3), the arms at full reach.

## 3. What each strike does (third pass of the claw, 2026-11-10)

The claw's first pass was judged too stiff and sword-like, the second still too stiff and small, with the body below the waist still. In the
third pass the whole body is in every stroke: the pelvis turns (to about 20 degrees), drops (to 0.26 m) and shifts its weight; the spine and
shoulder add their turn (chest yaw to about 90 degrees in all, lean to 40); a lead foot lifts, steps and replants into the strike while the rear
leg drives, the legs solved onto the feet so a planted foot never slides; the arm arcs reach far behind the body and far past the target, the
cocked apex is held two frames, and the off hand (the pistol) braces, shoves or counter-swings; the head follows the target throughout. The five
moves are a claw's, not a sword's; the string's steps carry the feet forward (X1 the left foot 0.40 m, X2 the right 0.35 m, X3 the left lunging to
0.75 m, X4 the right stamping back) and X4's long recovery brings them back under him. The root never moves: the simulation's lunge moves the body.

- **Claw X1, diagonal rake** (kept, as the owner asked): hips and chest coil hard right over the rear leg, the claw drawn up behind the right shoulder, the left foot lifting; the left foot stamps down ahead and the body whips left and down over it as the talons rake across to past the left hip; the pistol arm swings back as a counterweight.
- **Claw X2, gutting rake**: from X1's end the body sinks further left over the bent lead leg with the claw low, then the right foot steps through and the hips and chest uncoil up and right as the talons tear upward through the belly to above the right shoulder; the pistol arm shoves forward low as a brace.
- **Claw X3, punching stab**: the weight sinks onto the rear leg, the chest coils far right with the claw drawn back beside the ribs, talons forward, the pistol arm out as a guard; the left foot lunges a long step ahead and the hips drive the whole body forward behind the arm as the talons punch into the chest. The claw stays buried, the body leaning in, until the next strike (the combo opens at 0.586 s; alone, it pulls out by 1.0 s).
- **Claw X4, tearing rip** (string end): with the talons buried, the body leans in further and twists the claw deeper, the pistol hand planted on the target, the rear foot lifting; the right foot stamps back, the hips wrench back and right and the talons tear out and up past the right shoulder while the off hand shoves the target away. A long recovery as the feet come back under him.
- **Claw heavy, overhead downward rake**: the body rears back onto the rear leg with the claw raised straight up and cocked behind the head, the pistol arm thrown back, the left foot lifting; the left foot stamps down ahead and hips, chest and shoulder fold forward over it together as the talons rake down through the target to the ground.
Sixth pass: the back is straight throughout (the spine's own keys within 8 degrees of pitch, 5 of roll, 16 of yaw); every lean and turn below is the
pelvis's, which carries the whole torso as one piece; the stance is the reference's, wide and deep (the pelvis down 0.26 to 0.32 m at the cuts, steps
of 0.6 to 0.85 m), and the arms reach their full length at every cut.

- **Spear carry**: the spear in the right hand alone, the arm held out wide of the hip with the fist at chest height, gripped 0.95 m from the butt, the shaft leaning forward 35 degrees from upright (and a little in at the top), the blade forward of the shoulder pad with its edge facing out and forward, the butt behind and outside the leg; the left arm hanging free with the pistol.
- **Spear hew 1, the wrath hew** (the reference's technique): the shaft slides low through the right hand as the left hand takes it and both hands rise straight up into the steer (0.33 s: the haft upright overhead, the knees sinking); the haft swings around over the head until it lies behind the right shoulder as the hips coil 34 degrees right and the left foot lifts (0.52 s); the left foot stamps 0.65 m ahead and the hips (turning 60 degrees to the left, dropping 0.30 m, leaning 16) drive the cut down hard and diagonally from the right shoulder to low left (0.711 s), both arms long, ending in the deep wide field guard (the right hand at the right hip, the left forward and low, the blade forward and a little down; held to 1.6 s).
- **Spear hew 2, the rising hew**: from the field guard the hips turn 30 degrees left and sink to 0.30 m as the blade drops low and left behind the hip; the right foot steps 0.60 m through and the hips uncoil 58 degrees to the right as the blade is torn up and across to high over the right shoulder, the arms long.
- **Spear hew 3, the reverse hew** (string end): from high right the haft loops over the head to the left as the hips coil 36 degrees left and the left foot lifts; the left foot stamps 0.60 m forward-left and the hips pivot 48 degrees right as a flat hew at waist height carries the blade from the left across the front (at a constant rate at the hit) and on to the right, both arms low and long; it settles into the field guard, the feet come back under him and the shaft slides back up to the carry.
- **Spear stun (F)**: the shaft slides low as the left hand takes it and the haft is whipped up behind the right shoulder with a 28 degree coil of the hips; a fast flat snap cut at head height from right to left with a half step of the left foot; the hips stay coiled forward for the stab. A second F in the window (0.627 s, the hammer's `BRANCH_To_Slam` at the spear's tempo) chains to the stab.
- **Spear stab (F, F)**: from the stun's end the spear is drawn far back along the right hip, blade levelled at the target and both hands at the butt (the grip at 0.3 m), the hips coiling 40 degrees back and sinking onto the rear leg with the left foot lifting and the head fixed on the target; the left foot stamps a 0.85 m stride ahead and the hips drive the whole body, back straight, behind the blade in one lunging thrust (0.45 m forward, 0.30 m down, the torso leaning 16 from the hips), the left arm fully extended and the right hand at the chest; the blade is held in the target to 1.9 s, then wrenched out and the shaft slid back up to the carry.
- **Spear dive**: the spear turned point-down and forward, the right hand before the face and the left lower and forward on the shaft (no-clipping pass: before, the right hand was straight overhead and the shaft between the hands met the jump pack as the dive leans the body forward).
- **Spear impale** (owner, 2026-11-10: "Point-down"): on landing the point-down spear is driven down into the impaled Ork at his feet, home by 0.14 s (`ClawSpearClips.ImpaleDrive`; the hand-over from the dive ends with it), the hands sliding up the shaft to 1.5 m from the butt (`ImpaleGrip`) so the tip reaches the Ork about 0.7 to 0.9 m ahead from the landing's deep crouch; the hips lift the landing's fold back to a 35 to 45 degree lean; held point-down until 0.85 s (`ImpaleHeld`: "until the pound ends"), wrenched free by 0.95 s, and the clip ends on the carry's arms with the left hand off at the low grip, so the carry after the pound takes over smoothly. Before: the held keys laid the shaft flat at knee height toward his left and released at 0.5 s.

The pistol stays in the left hand for every claw strike (design D2); the left arm braces and counter-swings and never aims the pistol.

## 3b. No clipping (2026-11-10)

The owner's review of the wired stills: "Make a pass at all the animations to ensure the spear isn't going through the player models body at any time". **The check** (`SpearClearance`,
SmContent, Godot-free): the marine's body as capsules on its bones, fitted to the install's basic multiplayer armour and the Assault's jump pack in the bind pose (each part's vertices,
skinned mostly to a bone or rigid on it, split into clusters; each capsule covers 88 % of its cluster at the least volume; head, torso, waist, pelvis, jump pack, shoulder pads, upper arms,
thighs, knee pads, shins, feet; the forearms and hands hold the spear and are left out), a core capsule along the spine, and the spear as W3's model (shaft, collar, crossguard, the blade's
spine and edge). `SpearClearanceTests` (install only; skipped in CI) samples every spear clip every 1/60 s on the install's rig through `WeaponPoseMirror`, the overlay's arithmetic in
System.Numerics (the strikes over the hammer idle at six loop phases, the carry over all twenty hammer stance clips, the pound over its dive loop and landing) and the hand-overs between them
(the carry into each strike, the chains, each strike and the impale back to the carry, the dive into the impale), and allows no pose 0.02 m into a capsule (one allowance, below). The
fitted capsules are pinned to the fit (`Body_capsules_fit_the_armour`). The mirror was compared with the client's own overlay on the preview stage (`--claw-spear-test --clearance`): 0.01 to
0.02 m apart on every strike of the clips before the pass.

**Before**: up to 0.34 m (hew 3's overhead loop through the jump pack); every strike 0.18 to 0.34 m (the snap cut and the stab through the chest and the pads, the hews through the waist and
the pack); the carry 0.23 m into the shins; the impale 0.18 m into the pack. **After**: every clip and hand-over within the 0.02 m, the carry 0.107 m clear.

**What changed**: the carry (section 1); the strikes keep their key times, hits, eases, hips, steps and grip tracks (the timing and the weight pass's retime as approved) and only the arms'
keys moved: where the hips' turn carries the chest 40 to 90 degrees off the stance's front (the aims are absolute in the stance's frame), the right hand had fallen behind the turned torso
and the shaft between the hands ran through it; those keys are now placed in the chest's frame, the hands in front of it (two-bone IK), the shaft before the torso, below the pads and in
front of or above the jump pack. Hew 1: the left arm's swing-around routed through the front (it had swung behind the head into the pack); hew 2: the rising cut with the right hand before
the right hip and the left rising before the chest; hew 3: the loop over the head held in front of and above the pack, the flat hew and its follow-through before the hips; the snap cut
below the pads; the stab's start, drawn-back apex and thrust (the butt end had risen into the right pad through the hold); the shared recovery brings the left hand forward and the right
hand out before the left lets go. The dive and the impale: section 3.

**The allowance** (`SpearClearanceTests.LandingHandOverBudget`, 0.10 m): the dive's pose fading into the impale over the landing's first frames, while the landing clip slams the body about a
metre down in 0.07 s, meets the right arm and leg for one frame (0.085 m at 0.033 s; every dive pose tried gave 0.07 to 0.09 m). Open, for a refinement.

**On the preview stage**, `--clearance` prints the least clearance per clip with the client's own overlay; its run-through shows two things the match does not: the stage poses its base clip
through the viewer, whose loop wrap of the hammer idle cross-fades the pose (hew 2 and hew 3 read 0.06 to 0.09 m right after a wrap; held at the same clip time they read clear), and its
dive lasts 1.1 s before the impale. The grip tracks now fade from the clip before on the stage as in a match.

## 4. Preview stage and restore

```powershell
.\godot.cmd --windowed -- --claw-spear-test --clip claw|spear|all|<name,name> --view tp|side|front [--stills] [--out <dir outside the repository>]
.\godot.cmd --windowed --write-movie <dir>\claw-tp.avi --fixed-fps 30 -- --claw-spear-test --clip claw --view tp
# a phone-friendly copy (Movie Maker's MJPEG AVI does not play on phones): ffmpeg -i claw-tp.avi -vf scale=960:-2 -c:v libx264 -pix_fmt yuv420p -crf 20 claw-tp.mp4
.\godot.cmd -- --claw-spear-test --hold spear_hew3 --time 0.82 --pose-log 30 --quit-after 1.5     # headless: the arm directions the keys are authored against
```

`--view tp` is the game's third-person camera behind the right shoulder; `side` the right side; `front` the front. `--stills` saves a PNG at each
strike's wind-up apex, hit, follow-through and settle; stills and recordings default to `%LOCALAPPDATA%\sm-reimpl\tmp-build\claw-spear-anims`
and are refused inside the repository or the install. `--clearance` measures the spear against the body (section 3b). `--no-weapon-pose` shows the stances alone (the overlay off; in a match it also turns the spear's posing off, `SpearPosesOn`); the stand-in models are
only ever created by the preview stage (`ClawSpearTestView`). Removing the Musou weapons removes the `Weapons/` folder, `ClawSpearClips.cs`, `SpearHold.cs`
and the spear's `NetworkView.SpearPose.cs`; the hook implementation in the Librarian file then drops its one call.

### 4b. The spear in a match (2026-11-10)

W3 simulates the spear on the thunder hammer's combo at 0.85 and keeps posing the hammer's clips for its strikes (`client/Extras/Musou/Arsenal/Spear/NetworkView.SpearPose.cs`):
their sound and footstep tags, the hand-over to the legs from the step's `MoveAt` and every timing stay the hammer's. Over them each spear player's
character shows the approved clip through `CharacterViewer.WeaponPose`:

| What the player does | What shows | Driven by |
|---|---|---|
| a strike (light X1..X3, the Y interrupt, the X,Y and X,X,Y sweeps, the Y,Y slam) | hew 1..3; the snap cut for the interrupt and both sweeps; the stab for the slam's two sides (`ClawSpearClips.SpearStrike`) | the attack pose's drawn strike time (`ExtrasAttackTime`: the simulation's attack time, predicted for the own player and from the snapshots for the others, after the weight pass's retime), through `WeaponPoseClip.TimeAt`: as authored where the clip's hit is the strike's (all but the two sweeps), else the wind-up fitted so the hits meet |
| the jump pack's pound | the point-down dive, then the impale from the landing | the pound's dive loop and impact clips as posed |
| standing, walking, running, sprinting with the spear (the hammer stance: the pistol not up, no other action) | the one-handed carry | the stance, every frame |
| anything else (the pistol up, a reaction, a roll, a jump, a charge, the air slash) | nothing (faded out over 0.15 s); W3's mounts | - |

A strike's and the pound's keys lie over the clip they were authored on (the hammer's idle, its dive loop and landing; `WeaponPoseState.Base`, laid
while the viewer is posed with a clip, a bone it has no track for at its rest pose as on the stage), so they show exactly as on the stage; the carry lies over the stance itself. The clip foot lock is off for a
spear strike (the clip's own leg solve plants the feet). While a clip shows, the spear's model is put on the hands as the stage puts its stand-in
(`WeaponRig.PlaceSpearOnHands`, from W3's mount by the overlay's weight). Since the no-clipping pass the carry leans and turns its edge in the stance's frame (the hips' front), as on the stage; W3b had leaned it toward the way the player faces (the skeleton's +Y), which across the hammer idle's side-on stance (its hips 71 degrees right of the facing, measured) put the shaft and the edge across the body. In Musou a light press starts a random swing of the light chain (`MeleeChain.MusouSwing`), so hew 2 or 3 often
starts from the carry rather than from the hew before; the 0.14 s hand-over then takes the hands from the carry to the clip's first pose. Restore:
`--musou-spear-stand-in` (the hammer's clips and W3's mounts alone).
Stills: `--tfw-spear-capture` (docs/commands.md); `<media folder>\previews\spear-anims-wired\`; after the no-clipping pass `<media folder>\previews\spear-anims-pass2\` (`spear-*` in a match, `stage-*` on the preview stage).

## 5. Open points for the owner

Answered 2026-11-10: keep X1's diagonal rake; the spear rests upright at the side; the impale releases at 0.5 s; keep the stab-and-rip pair; key the pelvis (done, with the leg solve).

1. The steps are presentation: the feet move 0.35 to 0.85 m ahead of the root while the simulation's own lunge moves the body. In a match (the spear since 2026-11-10) the two add up: the body lunges and the lead foot reaches further still, and the planted feet travel with the body through the lunge (the clip's leg solve plants them in the body's frame; the clip foot lock is off). If that reads as too much ground covered or as sliding, the step lengths are one number each (`X1LeftF` ... for the claw, `H1LeftF` ... for the spear); to check in a playtest.
2. The reference's hand release and regrip is implied, not shown: the stand-in's left hand stays on the shaft through the overhead swing of hew 1 and hew 3 (the `Grip` track at 1), while the real technique lets the leading hand go and regrips lower. Showing it needs a per-hand grip-point track on the model (W3) and is a refinement.
3. No side-by-side stills against the reference were made: the reference is third-party material kept out of every published folder. The README names the reference frames (4, 12, 19, 30, 36) that hew 1's moments correspond to.
4. Stand-in sizes: the talons are 1.2 forearms (about 0.45 m); the blade 0.9 m on a 2.8 m spear. The real models replace them in W2 / W3.
5. Answered by the no-clipping pass (2026-11-10): the carry and the impale (sections 1 and 3). New: the carried butt now rides at least 0.56 m off the ground (it was 0.14 m running), the price of clearing the legs' kick; the carried blade crosses the third-person crosshair's column in the idle and the run (as the W3b carry did beside it); the landing's one-frame allowance (section 3b). For the owner's next review.
