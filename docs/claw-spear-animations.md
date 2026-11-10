# Lightning claw and power spear: strike animations (preview, 2026-11-10)

**Status:** animation and presentation only, for the owner's approval; no gameplay, timing or rule change. Musou Mode only (added content,
`docs/new-weapons-design.md` sections 5 and 6). No weapon is simulated yet: the clips run on a preview stage and wait for W2 / W3.
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
| Spear carry and the two grip tracks | `spear_rest`, a held pose over the hammer's idle: the spear in the right hand alone at the right side, the left arm hanging free (the owner: "right hand carries, both swing"). Two tracks on a clip drive the stand-in and, in a match, W3's rig: **`Grip`** (0 to 1, `WeaponPoseClip.GripAt`) says how far the left hand is on the shaft (0: carried one-handed, 1: the shaft on the line through both hands); **`GripPoint`** (metres from the butt, `GripPointAt`; `GripSlideAt` gives it as W3's `WeaponRig.GripSlide`, 0 = the high carry `CarryGrip` 1.3 m, 1 = the low melee grip `LowGrip` 0.5 m) says where the right hand holds the shaft. At rest and running the hand is at `CarryGrip` **0.95 m from the butt** (the owner: "right at the point where the butt of the spear is just above the ground as the player runs": the hanging hand is about 0.97 m up, the shaft leans 70 degrees, `SpearStandIn.CarryTilt`, so the butt rides about 0.08 m off the ground behind the hand and the blade forward-up over the shoulder; W3's high-grip constant takes the same 0.95 m). A strike begun from the carry slides the shaft through the hand to the low grip over `GripSlide` 0.25 s while the left hand joins (the shaft moving in the hand, not a pop); chained strikes keep the low grip; the string's last strike slides it back up over `GripSlideBack` 0.4 s as the left hand lets go. The stab holds the shaft 0.3 m from the butt for the reach. **For W3 / the match:** set `WeaponRig.GripSlideDriven = true` and feed `GripSlideAt(attackTime)` of the shown clip each frame (the rest's value, 0, while running or idling); the model's mount needs no other parameter, the carry tilt is the arms' pose plus the slide. The stage shows the carry in every spear idle. |
| Move channel | a shift of a bone's origin in the stance's frame (metres forward, right, up): the pelvis drops, drives forward and shifts its weight (third pass). Applied in the parent's frame after the bone's turn. |
| Foot channel and the leg solve | a foot's offset from where the stance plants it (metres; up > 0 lifts it for a step, a new forward / right offset with up 0 replants it there). Whenever a clip is laid, each leg is solved (two bones: the thigh aims at the new knee, the calf at the foot, the knee bending the way the stance bends it, the foot keeping the stance's orientation) so the foot stands at its planted spot plus the offset; a foot with no channel is held on its spot. So the pelvis and spine move freely and a planted foot never slides; the thighs, calves and toes are never keyed, and the root never moves (the simulation's position; `ClawSpearClipsTests` pin all of this). Chained strikes start with the feet where the one before planted them. |
| Hand-over | a chained strike fades from the previous clip's last pose over 0.14 s (`WeaponPoseState.From`), turns, shifts and foot offsets alike, so the overlay never pops at a combo step. |
| Stand-ins | `ClawSpearStandIns.cs`: the claw follows the right forearm and hand (gauntlet, pistons, brass bands, four talons 1.2 forearms long curving toward the palm); the spear follows the line through both hands (2.8 m black shaft, bronze collar, crossguard and ring, 0.9 m single-edged blade glowing blue, deeper at the base; the right hand is the rear hand 0.5 m from the butt, the blade beyond the left hand). The glow rises round the hit. |

**The hit time is the data's.** Every clip's length and hit time is the borrowed strike's divided by the design's tempo (`ClawTempo` 1.25,
`SpearTempo` 0.85), and the keys are built from those two numbers (anticipation = hit − wind-up, follow-through = hit + x). When the weapons are
simulated (W2 / W3), the networked view will show the clip at the simulation's attack time exactly as it shows the sword's and hammer's clips
today; nothing in the simulation changes for the animation.

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
`ClawSpearClipsTests` pins them. The spin's blade passes the front at the hit on a constant rate and runs on for 0.18 s; the chest winds about
50 degrees and the arms carry the rest, about 250 degrees of blade travel in all, the feet planted.

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
- **Spear carry** (fifth pass): the spear in the right hand alone at the right side, gripped 0.95 m from the butt so the butt hangs just above the ground, the shaft leaning forward-up, the left arm hanging free with the pistol.
- **Spear hew 1, the wrath hew** (fifth pass; the reference's technique): the shaft slides low through the right hand as the left hand takes it and both rise into the steer (0.33 s: the pole upright overhead, the knees sinking); the pole swings around over the head and behind the right shoulder as the hips coil right and the left foot lifts (0.52 s); the left foot stamps 0.50 m ahead and the whole body drives the cut down diagonally from the right shoulder to low left (0.711 s), the arms extended, ending wide and low in the field guard (held to 1.6 s).
- **Spear hew 2, the rising hew**: from the field guard the body sinks deeper over the bent lead leg and the blade drops low and left behind the hip; the right foot steps 0.55 m through and hips and chest uncoil as the blade is torn up and across to high over the right shoulder.
- **Spear hew 3, the reverse hew** (string end): from high right the pole loops over the head to the left as the hips coil left and the left foot lifts; the left foot stamps 0.60 m forward-left and the hips pivot right as a flat hew at waist height carries the blade from the left across the front (at a constant rate at the hit) and on to the right, the body folded low over it; it settles into the field guard, the feet come back under him and the shaft slides back up to the carry.
- **Spear stun (F)**: the shaft slides low as the left hand takes it and the pole is whipped up behind the right shoulder with a short coil; a fast flat snap cut at head height from right to left with a half step of the left foot; the body stays coiled forward for the stab. A second F in the window (0.627 s, the hammer's `BRANCH_To_Slam` at the spear's tempo) chains to the stab.
- **Spear stab (F, F)**: from the stun's end the spear is drawn far back along the right hip, blade levelled at the target and both hands at the butt (the grip at 0.3 m), the body coiling back and sinking onto the rear leg with the left foot lifting and the head fixed on the target; the left foot stamps a 0.85 m stride ahead and hips, chest and arms drive the body behind the blade in one lunging thrust (pelvis 0.45 m forward, 0.28 m down), the left arm straight out, the right hand at the chest; the blade is held in the target to 1.9 s, then wrenched out and the shaft slid back up to the carry.
- **Spear dive**: the spear turned point-down and forward over the head, both hands high on the shaft (the blade hand lower).
- **Spear impale**: on landing the spear is driven into the ground at the feet (snap over 0.08 s), held pinned to 0.5 s (the owner kept this), wrenched free by 0.75 s.

The pistol stays in the left hand for every claw strike (design D2); the left arm braces and counter-swings and never aims the pistol.

## 4. Preview stage and restore

```powershell
.\godot.cmd --windowed -- --claw-spear-test --clip claw|spear|all|<name,name> --view tp|side|front [--stills] [--out <dir outside the repository>]
.\godot.cmd --windowed --write-movie <dir>\claw-tp.avi --fixed-fps 30 -- --claw-spear-test --clip claw --view tp
.\godot.cmd -- --claw-spear-test --hold spear_spin --time 0.82 --pose-log 30 --quit-after 1.5     # headless: the arm directions the keys are authored against
```

`--view tp` is the game's third-person camera behind the right shoulder; `side` the right side; `front` the front. `--stills` saves a PNG at each
strike's wind-up apex, hit, follow-through and settle; stills and recordings default to `%LOCALAPPDATA%\sm-reimpl\tmp-build\claw-spear-anims`
and are refused inside the repository or the install. `--no-weapon-pose` shows the stances alone (the overlay off); the stand-in models are
only ever created by the preview stage (`ClawSpearTestView`), so nothing of this is drawn in a match until W2 / W3 wire the state
(`CharacterViewer.WeaponPose`) from the simulation. Removing the Musou weapons removes the `Weapons/` folder and `ClawSpearClips.cs`; the hook
implementation in the Librarian file then drops its one call.

## 5. Open points for the owner

Answered 2026-11-10: keep X1's diagonal rake; the spear rests upright at the side; the impale releases at 0.5 s; keep the stab-and-rip pair; key the pelvis (done, with the leg solve).

1. The steps are presentation: the feet move 0.35 to 0.75 m ahead of the root while the simulation's own lunge moves the body. In a match the two add up (the body lunges and the lead foot reaches further still); if that reads as too much ground covered, the step lengths are one number each (`X1LeftF` ..., `FhLeftF` ...).
2. The reference's hand release and regrip is implied, not shown: the stand-in's left hand stays on the shaft through the overhead swing of hew 1 and hew 3 (the `Grip` track at 1), while the real technique lets the leading hand go and regrips lower. Showing it needs a per-hand grip-point track on the model (W3) and is a refinement.
3. No side-by-side stills against the reference were made: the reference is third-party material kept out of every published folder. The README names the reference frames (4, 12, 19, 30, 36) that hew 1's moments correspond to.
4. Stand-in sizes: the talons are 1.2 forearms (about 0.45 m); the blade 0.9 m on a 2.8 m spear. The real models replace them in W2 / W3.
