# Visual overhaul proposal (pending the owner's approval)

The owner's request (2026-11-06): "Initiate a game wide visual overhaul pass informed to you by ChatGPT. Propose changes and give me previews
for approval". Only the task name ("whole-game lighting and aesthetic overhaul") came from ChatGPT; no design came with it, so this proposal is
our own analysis. Branch `proposal/visual-overhaul`; nothing merges until the previews are approved.

**Switch:** `--visual-overhaul on|off` (client, default **off**: main's look is unchanged). Code: `client/VisualOverhaul.cs`; every constant can
be tuned with `--look-set key=value` like the M42 ones. Spec: "Visual overhaul proposal"; docs/changes-from-original.md has the row.

## Where the look stands (survey)

The client already has a full lighting stack: the map's own `l_lights` data (sun, hemisphere ambient, depth fog, bloom, 117 omni + 5 spot lamps,
3 environment regions), M40-T7's tone mapping and sky, M42's presets (`--lighting off|low|medium|high`, default medium: soft 4-split sun
shadows, SSAO, SSIL, volumetric smog with shafts, a colour grade; SSR and PCSS at high), the remaster look as default (`--look`), MSAA 4x +
SMAA with a roughness limiter, and a Compatibility-renderer safe mode for old GPUs. The yards already read close to the original's warm,
smoky forge world. What the BEFORE captures still show:

1. **Interiors and the spawn rooms are dim and flat** (03, 08, 09): one fixed exposure tuned for the sunlit yards; inside, the cool fill lamps
   and the sky ambient give an even blue-grey wash with little depth.
2. **Contact shading is short-range**: SSAO 1.4 m and SSIL 6 m darken seams but not the space under containers, consoles and in corners.
3. **Near sun shadows are soft and coarse** for a third-person game (first cascade 20 m, blur 1.2).
4. **Ground textures blur at grazing angles** (05): 4x anisotropic filtering on large floor planes.
5. **The sky is a flat gradient** (07): the original's textured sky is not decoded; M42's fog covers most of the background.
6. Minor: banding in the fog and sky gradients, specular sparkle on normal-mapped grates, blocky smog shafts at medium.

## The proposal: an enhancement of how the original content is lit, not a restyle

The map's sun, ambient, fog and lamp **colours stay the data's**; the colour grade, sun angle, materials' colours, effects and HUD are
untouched. Each change below is in the switch. The per-item costs are **estimates** for a mid-range GPU; only the total was measured (see "Cost").

| # | Change | Why | Cost | Risk | Compatibility renderer |
|---|---|---|---|---|---|
| V1 | **Eye adaptation**: a narrow auto exposure; target mean luminance 0.11 (the yards' own, so they keep the chosen exposure), at most x1.5 brighter and x1.25 darker, about 1 s to adapt | Interiors, spawn rooms and deep shade lift (03, 08, 09) without touching the yards; SM1's HDR pipeline (`HDRDesc` in the data) implies adapted exposure, whose parameters we have not decoded | ~0.05 ms (luminance reduction) | a dark room never gets brighter than x1.5, so PvP readability does not depend on adaptation; a short "breathing" when walking out of a door | not available: off |
| V2 | **Wider contact shading and bounce**: SSAO radius 2.4 m, power 1.8, some effect on direct light; SSIL radius 9 m, intensity 2.6 | Depth under containers, consoles and in corners; warm sunlit floors fill the shade | ~0.2-0.4 ms | slightly darker crevices; at a doorway a dark halo can appear (SSAO is screen space) | SSAO/SSIL not available: unchanged |
| V3 | **Sharper near sun shadows**: cascades 3 / 10 / 35 % of the data's 400 m (first 12 m), blur 0.8 | Players, rails and grates cast crisp shadows near the camera | ~0.1 ms | far cascades a little coarser | applies (it has sun shadows at low) |
| V4 | **16x anisotropic filtering** | Floors and walkways stay sharp at grazing angles (05) | <0.1 ms on any card of the last decade | none known | applies |
| V5 | **Debanding** | Smooth fog and sky gradients | ~0.05 ms | none known | applies |
| V6 | **Stronger specular anti-aliasing**: roughness limiter 0.4 / 0.28 (was 0.25 / 0.18) | Less sparkle on grates and armour edges in motion | ~0 | metal very slightly less sharp in highlights | applies when AA is on |
| V7 | **Smog sky**: static smoke banks in the sky background (not in the ambient cubemap, so the lighting is unchanged), coloured from the data's haze, lit toward the sun; the depth fog and smog cover less of the sky (0.15 / 0.04 instead of 0.35 / 0.1) | The background reads as an industrial sky instead of a flat gradient (07); the original has a textured sky | ~0.05 ms (sky pixels only, static) | an interpretation of the original sky (not decoded) | applies |
| V8 | **Bicubic glow upscale** | Smoother bloom around lamps and muzzle flashes | ~0.05 ms | none known | applies where glow runs |
| V9 | **Finer volumetric smog grid** at medium: 128 x 80 froxels (was 96 x 64; high keeps 160 x 96) | Less blockiness and flicker on the light shafts | ~0.3 ms | VRAM +~6 MB | not available: off |

**Measured total** (see "Cost" below): +0.05 to +0.11 ms GPU per frame at medium on the development PC (a fast GPU); the estimates add up to about +0.6-1 ms on a mid-range card. The Compatibility renderer gets only the cheap items (V3-V8).

Considered and **not** proposed (yet):

- SDFGI / VoxelGI: SDFGI hides whole walls of Manufactorum in Godot 4.7.2 (M42); VoxelGI needs a bake per map and its memory on old GPUs.
- TAA or FSR as default: ghosting on fast third-person turns and on the effect cards; the current MSAA + SMAA stays.
- A new colour grade, sun angle or tint: that is the art direction (and the Graphics presets' job, below), not the renderer.
- Rim or fill lights on players: a readability aid, but a restyle of the original's characters.
- `drvspec` materials (103 of 144 Manufactorum materials derive their specular from the diffuse; the four `DerivedSpecParams` values are not
  understood): a specular-from-diffuse term would cut the "wet" sheen on dark surfaces, but needs the parameters decoded first. Phase 2.

## Previews

All captures are in the private cache, never in the repository (they show original assets):
`%LOCALAPPDATA%\sm-reimpl\cache\previews\visual-overhaul\`

- `contact_sheet.jpg`: every pair on one sheet (left BEFORE = main's look, right AFTER = `--visual-overhaul on`), same build, same views.
- `side-by-side\<view>.jpg`: one pair per view; the raw frames are in `before\` and `after\`.

| View | How | What to look at |
|---|---|---|
| 01_yard_cathedral | observer pose 205,-840,3,0,-3 | the yard keeps its exposure; sky over the cathedral |
| 02_yard_reverse | 205,-800,3,180,-3 | sky, shade under the containers |
| 03_interior | 247,-926,9,0,-8 | interior lift (V1), corners (V2) |
| 04_shade_alley | 230,-760,6,200,-8 | shade, cast shadows (V3) |
| 05_ground_grazing | 200,-806,2.2,30,-25 | floor sharpness at grazing angles (V4) |
| 06_overview_high | 205,-870,32,0,-22 | the whole yard: contact shading, haze |
| 07_skyline | 205,-840,3,0,12 | smog sky (V7) |
| 08_marine_portrait / _fx | `--observe --bolt-fx-demo` on an idle Tactical (spawn room) | the marine in a dark room; bolter impact and bloom (V8) |
| 09_chaos_portrait / _fx | the same on an idle Chaos Tactical | the Chaos marine; impact |
| 10_musou | `--content musou` observer on a Musou server | Musou's war map is still the placeholder slab (no lighting data), so only V1/V4-V6 show |
| 11_menu | `--menu-screen lobby` | the front end is 2D: unchanged by design |

The FX frames are the brightest frame of each 0.2 s series (the shot timing is not frame-exact between runs), so the pair can differ in the
moment of the impact.

## How the overhaul combines with the Graphics presets

A separate branch (`ui/lighting-presets-focus-glow`) adds ten lighting presets to Options > Graphics; its default "Faithful Remaster" is the
current look. The two layers are meant to stack: **the overhaul is the base rendering** (exposure range, shading range, shadows, filtering,
sky), **the presets are grades on top** (colour, contrast, fog thickness, sun). In code the overhaul's constants sit between `--look` and
`--look-set` (`MapLightingView.MergeOverrides`); when the presets land, a preset's grade constants should apply after the overhaul's, and a
preset that sets one of the overhaul's keys (`ssao.*`, `ssil.*`, `sh.*`, `fog.sky`, `vf.sky`) wins. If approved, the switch should become a
Graphics option ("Enhanced rendering: on/off", on by default on Forward+), owned by the presets work.

## Shared files touched (one line each)

`client/MapLightingView.cs` (the overrides layer), `client/ModelViewer.cs` (viewport settings at start), `client/FrontEnd/OptionsHost.cs`
(re-apply after an AA change), `client/NetworkView.cs` (map hook after the lighting); plus `spec/behavior.md`,
`docs/changes-from-original.md`, `docs/refine-register.md` (regenerated). Nothing in gameplay, protocol, server, HUD or effects.

## Cost

`--frame-stats`, observer at a fixed pose, hidden windowed client (`godot.cmd --windowed`), 1600x900, lighting medium, MSAA 4x + SMAA, no
players; the mean of the renderer's own GPU time over the last three 5 s periods of a 32 s run, same build, switch off and on:

| View | GPU off | GPU on | Difference |
|---|---|---|---|
| 01 yard (205,-840,3,0,-3) | 2.18 ms | 2.19 ms | +0.01 ms |
| 03 interior (247,-926,9,0,-8) | 2.12 ms | 2.24 ms | +0.12 ms |

The development PC's GPU is fast (2.2 ms a frame), so these numbers mostly say "no hitch"; on an older card the froxel grid (V9) and the wider
SSAO/SSIL (V2) are the items to watch, and both are off on the Compatibility renderer and follow `--lighting low`. Not measured: a full match
with 16 players, and an old GPU. Render CPU time did not rise.

## What stays unchanged

The data's colours (sun, ambient, fog, lamps), the M42 preset ladder and its defaults, the remaster look's grade and sun, materials and
textures, effect cards, HUD, menus, anti-aliasing mode, Musou's hub sky, and everything with the switch off.
