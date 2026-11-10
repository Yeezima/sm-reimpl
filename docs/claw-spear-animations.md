# Lightning claw and power spear: strike animations (preview, 2026-11-10)

**Status:** animation and presentation only, for the owner's approval; no gameplay, timing or rule change. Musou Mode only (added content,
`docs/new-weapons-design.md` sections 5 and 6). No weapon is simulated yet: the clips run on a preview stage and wait for W2 / W3.
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
| Keys and easing | each key eases in from the one before: `In` (the wind-up gathers), `Snap` (97 % of the travel in the first half of the segment: the strike), `Linear` (a blade passing the front of a sweep without pausing), `Out`, `Over` (a follow-through that passes by a tenth and comes back), `Smooth` (settles). |
| Hand-over | a chained strike fades from the previous clip's last pose over 0.14 s (`WeaponPoseState.From`), so the overlay never pops at a combo step. |
| Feet | no clip names the root, pelvis or a leg bone (pinned by `ClawSpearClipsTests`), so the feet stay where the stance puts them and the body's weight shift is carried by the spine. |
| Stand-ins | `ClawSpearStandIns.cs`: the claw follows the right forearm and hand (gauntlet, pistons, brass bands, four talons 1.2 forearms long curving toward the palm); the spear follows the line through both hands (2.8 m black shaft, bronze collar, crossguard and ring, 0.9 m single-edged blade glowing blue, deeper at the base; the right hand is the rear hand 0.5 m from the butt, the blade beyond the left hand). The glow rises round the hit. |

**The hit time is the data's.** Every clip's length and hit time is the borrowed strike's divided by the design's tempo (`ClawTempo` 1.25,
`SpearTempo` 0.85), and the keys are built from those two numbers (anticipation = hit − wind-up, follow-through = hit + x). When the weapons are
simulated (W2 / W3), the networked view will show the clip at the simulation's attack time exactly as it shows the sword's and hammer's clips
today; nothing in the simulation changes for the animation.

## 2. Timing table (seconds from the strike's start)

| Clip | Borrowed strike (data) | Length | Hit (design) | Wind-up apex | Follow-through | Settle | Chains at (preview) |
|---|---|---|---|---|---|---|---|
| `claw_x1` cross slash | sword X1 `SM_MEL_CS_ATK_F_01_D_3.5m` 1.600 s, hit 0.320 | 1.280 | **0.256** | 0.170 | 0.371 | 1.150 | 0.694 (combo 0.867 ÷ 1.25) |
| `claw_x2` backhand rake | sword X2 `SM_MEL_PA_XATK_Combo2MsA_3.5m` 1.433, hit 0.287 | 1.146 | **0.230** | 0.150 | 0.340 | 1.100 | 0.614 |
| `claw_x3` rising rake | sword X3 `SM_MEL_CS_XATK_Combo3_3.5` 1.767, hit 0.433 | 1.414 | **0.346** | 0.240 | 0.461 | 1.300 | 0.586 |
| `claw_x4` overhead stab | sword X4 `SM_MEL_CS_XATK_Combo4_3.5m` 1.667, hit 0.533 | 1.334 | **0.426** | 0.300 | 0.556 | 1.250 | string ends |
| `claw_heavy` heavy backhand | sword Y `SM_MEL_CS_XATK_Draw2_3.5m` 1.5, hit 0.33 | 1.200 | **0.264** | 0.164 | 0.404 | 1.200 | - |
| `spear_forehand` | hammer X1 `SM_MEL_TH_XATK_Combo2` 2.667, hit 0.604 | 3.138 | **0.711** | 0.521 | 1.001 | 2.400 | 1.098 (combo 0.933 ÷ 0.85) |
| `spear_backhand` | hammer X2 `SM_MEL_TH_XATK_Combo3` 2.333, hit 0.567 | 2.745 | **0.667** | 0.487 | 0.947 | 2.300 | 1.138 |
| `spear_spin` 240 degree sweep | hammer X3 `SM_MEL_TH_HeavyATK02` 2.333, hit 0.700 | 2.745 | **0.824** | 0.624 | 1.004 | 2.400 | string ends |
| `spear_dive` point-down | hammer dive loop `SM_JMP_TH_GP_Loop_01` 1.767 | held | - | - | - | - | into the landing |
| `spear_impale` | hammer landing `SM_JMP_TH_GP_Impact_01` 1.067 | 1.067 | **0.000** (the landing) | - | 0.080 (drive-down) | 1.000 | - |

The design's hit times (section 5: the sword's ÷ 1.25; section 6.1: 0.71 / 0.67 / 0.82 s; section 6.2: the impale on landing) are met exactly;
`ClawSpearClipsTests` pins them. The spin's blade passes the front at the hit on a constant rate and runs on for 0.18 s; the chest winds about
50 degrees and the arms carry the rest, about 250 degrees of blade travel in all, the feet planted.

## 3. What each strike does

- **Claw X1**: the body coils right, the claw cocked high by the ear; a diagonal rake down across to the left, the chest leaning into it.
- **Claw X2**: from X1's end the claw is drawn across the chest and whipped out flat to the right.
- **Claw X3**: a crouch with the claw drawn back behind the hip; the talons tear upward through the target's chest to above the head.
- **Claw X4** (string end): the claw straight up and cocked behind the head, then driven down into the target with the whole upper body; a long recovery.
- **Claw heavy**: wound far across the left shoulder, chest turned with it; the whole upper body unwinds into a wide flat sweep to the right.
- **Spear forehand**: a long wind-up with the blade forward-right and the rear hand at the left hip; a flat two-handed sweep across the front to the left, the rear hand swinging behind the right hip.
- **Spear backhand**: from the forehand's end the blade is gathered left-back and swept across the front to the right.
- **Spear spin**: the deepest wind-up, the blade behind the right shoulder and the eyes on the target; one unbroken sweep through the front and on past the left shoulder.
- **Spear dive**: the spear turned point-down and forward over the head, both hands high on the shaft (the blade hand lower).
- **Spear impale**: on landing the spear is driven into the ground at the feet (snap over 0.08 s), held pinned to 0.5 s, wrenched free by 0.75 s.

The pistol stays in the left hand for every claw strike (design D2); the left arm moves a little for balance and never aims the pistol.

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

1. The claw's cross slash (X1) is authored as a rake from high right down to low left; the sword's X1 is a forward slash. Keep the diagonal, or match the sword's line?
2. The spear's rest grip is the hammer's carry (blade across the body to the left); the reference holds it upright at the side. The sweeps start from the carry either way.
3. The impale's 0.5 s pin before the pull-out: the design pins a surviving Boss Ork for 1 s; the pose releases earlier so the get-up matches the landing clip's end. Hold the pin for the whole clip instead?
4. Stand-in sizes: the talons are 1.2 forearms (about 0.45 m); the blade 0.9 m on a 2.8 m spear. The real models replace them in W2 / W3.
