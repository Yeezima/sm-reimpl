# Lightning claw and power spear: strike animations (preview, 2026-11-10)

**Status:** animation and presentation only, for the owner's approval; no gameplay, timing or rule change. Musou Mode only (added content,
`docs/new-weapons-design.md` sections 5 and 6). No weapon is simulated yet: the clips run on a preview stage and wait for W2 / W3.
**Owner's review of the first previews (2026-11-10):** spear approved as the base, with the rest pose "upright at his side" (done: section 3) and
the impale's release kept at 0.5 s; claws "redo: too stiff / small motions, reads like a sword; keep the diagonal rake" (done: the second pass of
section 3, every claw strike re-authored with the whole upper body driving it, the off hand bracing, and claw moves: rake, gut, stab, rip, overhead rake).
**Second review (2026-11-10):** "no part of the character's body should remain still through this.. he should be putting his entire body into the
strokes", "still too stiff / small", "add pelvis now", "keep the stab-and-rip pair" (done: the third pass, section 1's Move and Foot channels and the
leg solve, section 3: the pelvis turns, drops and shifts on every strike, a lead foot steps and replants, planted feet pinned by the solve).
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
| Spear rest | `spear_rest`, a held pose over the hammer's idle: the spear upright at the right side, butt by the foot and blade above the head, the right hand on the shaft at the hip, the left gripping it at the chest (the owner's reference). Every sweep starts from it and returns to it; the stage shows it in every spear idle. |
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
| `spear_rest` upright at the side | hammer idle `SM_2HM_NAV_TH_IdleL_S_01` 1.5 | held | - | - | - | - | into any sweep |
| `spear_forehand` | hammer X1 `SM_MEL_TH_XATK_Combo2` 2.667, hit 0.604 | 3.138 | **0.711** | 0.521 | 1.001 | 2.400 | 1.098 (combo 0.933 ÷ 0.85) |
| `spear_backhand` | hammer X2 `SM_MEL_TH_XATK_Combo3` 2.333, hit 0.567 | 2.745 | **0.667** | 0.487 | 0.947 | 2.300 | 1.138 |
| `spear_spin` 240 degree sweep | hammer X3 `SM_MEL_TH_HeavyATK02` 2.333, hit 0.700 | 2.745 | **0.824** | 0.624 | 1.004 | 2.400 | string ends |
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
- **Spear rest**: upright at the right side, butt by the foot, blade above the head (the owner's reference); the right hand on the shaft at the hip, the left gripping it at the chest.
- **Spear forehand**: from the rest the spear is levelled and drawn back over the right hip, blade forward-right; a flat two-handed sweep across the front to the left, the rear hand swinging behind the right hip.
- **Spear backhand**: from the forehand's end the blade is gathered left-back and swept across the front to the right.
- **Spear spin**: the deepest wind-up, the blade behind the right shoulder and the eyes on the target; one unbroken sweep through the front and on past the left shoulder, then back to the upright rest.
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

1. The steps are presentation: the feet move 0.35 to 0.75 m ahead of the root while the simulation's own lunge moves the body. In a match the two add up (the body lunges and the lead foot reaches further still); if that reads as too much ground covered, the step lengths are one number each (`X1LeftF` ...).
2. The spear's sweeps were left as approved; the pelvis and foot channels would let them step into the sweeps too (a light touch, on request).
3. Stand-in sizes: the talons are 1.2 forearms (about 0.45 m); the blade 0.9 m on a 2.8 m spear. The real models replace them in W2 / W3.
