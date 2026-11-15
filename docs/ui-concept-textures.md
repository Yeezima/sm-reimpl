# UI concept textures: source, licence and how to rebuild them

The painted pieces of the redesigned UI (`client/assets/ui/*.png`, embedded in the client assembly as `Ui/<name>.png`, read through `ConceptArt`) are cut from **one source: the
owner's own four-panel concept image** (`concept-4panel.jpg`, 1600 x 960, made by the owner for this project, 2026-11-15). It is owner-made art, like the owner's images in
`client/assets/menu`; the owner asked for the UI to match it "exactly". The image itself is not in the repository (it is outside it, in the owner's media folder); only the cut pieces are.

* No original game texture or UI file is used, nothing was made by a paid image service, and no whole screen or panel is a texture: every piece is a small cut of one element.
* Licence of the pieces: the owner's, committed with his permission for this project (GPL-3.0 for the repository's own code; the owner's art keeps the same footing as `client/assets/menu`).
* Fonts: none bundled by this step. The text is live Godot text in the existing bundled DejaVu Sans Bold (`client/assets/fonts/LICENSE-DejaVu.txt`), squeezed to 84 % width with 2 px tighter spacing (`Steel.CondensedFont`) to come near the concept's condensed lettering. A closer free face (an OFL condensed bold such as Oswald or Teko) would have to be
  downloaded; that needs the owner's permission (file name, source and size are in the phase 2a report), and its licence would be recorded beside the DejaVu one.

## Rebuilding

```
godot.cmd -- --build-ui-textures <path to concept-4panel.jpg> [--out <dir>]      (default dir: client/assets/ui)
```

`UiTextureBuilder` (client/Customizer/UiTextureBuilder.cs) holds every rectangle (in the concept's own pixels) and every rule; running it gives the same PNGs. Each piece is cut,
upscaled with Lanczos by 1.74 (the concept's frames are 2.46x at 1080p; ours are drawn smaller to fit) and sharpened with a 3 x 3 unsharp mask. Frames are cut as one corner plus edge strips
and mirrored into a nine-patch with a transparent hole; labels are removed by tiling a blank stretch of the same plate over them (matched to the plate's light) so live text goes on top.

| Piece | Cut from (concept px: x, y, w, h) | Alpha | Used for |
|---|---|---|---|
| `frame-main` | panel 01 frame: corner 47,200,63x60; top strip 122,200,20x60; side strip 47,300,63x30; ring 27 / 24 | chamfer outside, hole inside | the main menu's frame (amber recess drawn behind it) |
| `frame-options` | panel 04 frame: corner 1436,582,66x53 (top right, mirrored); strips 1380,582 and 1436,670; ring 15 / 14 | same | the Options frame |
| `button-dark`, `button-gold` | panel 01 plates CLASSIC PVP (96,266,239x27) and MUSOU MODE (96,234,239x29), label removed | pointed ends | every steel button (dark, hover warm, gold = focus, pressed, primary) |
| `tab-dark`, `tab-gold` | panel 04 tabs AUDIO (1080,610,118x22) and INTERFACE (1324,609,132x24), label removed | pointed ends | Options tabs |
| `pill` | panel 04 choice pill (1060,684,116x20), label removed, arrows kept | pointed ends | value pills of the option rows |
| `nameplate` | panel 01 (114,414,200x24), text baked (fixed "ONE SOLDIER. MANY BATTLEFIELDS.") | pointed ends | the main menu's nameplate |
| `crest` | panel 04 (1127,537,146x31) | flood key of the flat grey, plate edge cleared | the winged skull over the main menu and Options |
| `title-plate` | panel 04 (1090,566,220x37), text baked (fixed "OPTIONS") | flood key | the OPTIONS plate |
| `wordmark` | panel 01 (45,110,350x80), text baked (fixed "SM-REIMPL") | saturation key plus dark outline | the main menu's lettering |
| `stone-main` | panel 01 (10,240,37x100) | none | the main menu's stone pillar (tiled) |
| `stone-options` | panel 04 (1496,600,42x150) | flood key | Options' stone side pieces (the left one mirrored) |

Known limits (the concept is 780 x 440 per panel, JPEG): the pieces are soft at 4K, the frame's top, bottom and side strips are single samples stretched along the edge, and the wordmark's edge is
slightly rough. Not yet cut from the concept: the slider track, fill and knob, the OFF / ON switch (they stay code-drawn in the steel palette), the HUD frames and the Armoury cards (phase 2b).

## Pass 2b: the main menu from the owner's sharper crop (2026-11-15)

Rebuild with `godot.cmd -- --build-ui-textures <concept-4panel.jpg> --owner-crop <owner-ref-main-menu-crop.png>` (the crop is the owner's 522 x 606 cut of panel 01, outside the repository). With the crop the builder also makes:

| Piece | Cut from the crop (x, y, w, h) | Use |
|---|---|---|
| `frame-wall` | 0,236,495,345; the amber recess (68,275,394x228) transparent, the right-hand outside transparent, the stone at the left kept opaque | the main menu's frame mounted on the wall, with the nameplate inset in its thick bottom bar (unique steel on every side, scaled 1.2, no mirrored corner) |
| `wall-main` | 0,300,24,250 | the stone column down the left edge (tiled, every second tile upside down) |
| `wall-cap` | 0,0,76,112 | the stone block at the top left |
| `wordmark` | 30,122,478x94, matte: saturation key, holes closed, dark outline kept, alpha softened and eroded | SM-REIMPL |

Without `--owner-crop` the 4-panel pieces of the first pass are made as before (`frame-main`, `stone-main`, `wordmark`).

## Pass 2c: the main menu as one overlay (owner, 2026-11-15)

The owner: "simply paste in the concept art overlaid on the left side of the screen, then fix the buttons". The main menu's left side is now ONE picture, `overlay-main.png` (1044 x 1212), cut from the owner's 522 x 606 crop of panel 01 (the wall, the wordmark, the crest, the window and its nameplate), with its alpha following the owner's orange line (feathered); this replaces `frame-wall`, `wall-main`, `wordmark` and the crest for the main menu only (they stay for Options and the fallback). The concept's button plates are painted out of the recess (its amber with scan lines); the live buttons stand on their places (the recess area `UiTextureBuilder.OverlayRows`), spread evenly, tighter when STOP MUSOU SERVER shows. Behind the wordmark and the crest only the letters (outline, shadow) and the crest are kept, so no box of the concept's sky shows.

Upscale stage: without `--upscaled` the crop is enlarged 2x with Lanczos limited to the local range of the source (no ringing) and an unsharp mask. `--upscaled <png>` replaces that stage with any already upscaled picture of the same framing (the builder scales its coordinates to the picture's size), so an AI-upscaled crop can be dropped in with no code change:

```
godot.cmd -- --build-ui-textures <concept-4panel.jpg> --owner-crop <owner-ref-main-menu-crop.png> [--upscaled <crop upscaled.png>]
```

While the overlay shows, the video behind the menu is enlarged 1.3x from its left edge (`MenuVideoBackground.MainMenuFraming`) so the Marine and the battlefield stand clear of the overlay, which spans the full height and about 48 % of the width at 16:9.

## Pass 2d: Real-ESRGAN upscale and Oswald (owner, 2026-11-15)

Both were downloaded by the lead with the owner's permission and stay outside the repository (Real-ESRGAN ncnn Vulkan v0.2.5.0, model `realesrgan-x4plus`; run only on the owner's concept files and our cut pieces, never on game files). The exact steps:

```
realesrgan-ncnn-vulkan.exe -i owner-ref-main-menu-crop.png -o owner-crop-x4.png      -n realesrgan-x4plus -s 4     (522 x 606  -> 2088 x 2424)
realesrgan-ncnn-vulkan.exe -i concept-4panel.jpg            -o concept-4panel-x4.png -n realesrgan-x4plus -s 4     (1600 x 960 -> 6400 x 3840)
godot.cmd -- --build-ui-textures concept-4panel-x4.png --owner-crop owner-ref-main-menu-crop.png --upscaled owner-crop-x4.png
```

The builder takes the upscaled concept as its first argument (its factor F = width / 1600 scales every rectangle, thickness and chamfer; the pieces keep the same output size as before, now downsampled from the 4x detail, so they are crisper) and the upscaled crop as the overlay's source (`overlay-main.png` is 2088 x 2424, drawn at about 930 x 1080, so it holds at 4K). The plate, tab, pill, frame and title pieces stay at their 1:1 UI size (about 1.74x the concept); they are sharp at 1080p and soft at 4K.

Font: Oswald (SIL OFL 1.1, variable weight, `client/assets/fonts/Oswald-wght.ttf`, licence `LICENSE-Oswald-OFL.txt`, noted in THIRD_PARTY_NOTICES.md), weight 600, for the kit's labels, tabs and buttons (`Steel.CondensedFont`); the kit's text sizes were raised to suit it (27 buttons, 23 tabs, 21 rows, 16 small). The squeezed DejaVu is the fallback if the file cannot be read.

## Pass 2e: the overlay graded into the video's scene (owner, 2026-11-15)

`UiTextureBuilder.FinishOverlay` (fixed parameters, so the builder gives the same picture): the concept's flat grey daylight is graded toward the video's dark, warm sunset (contrast up, haze down, a warm tint), the far left of the wall and the lower part are darkened, a warm rim light runs along the edges that face the light (right and top), the sunset's haze is mixed into the outer edge, and one soft contact shadow (blurred alpha, shifted down and right) sits under the wall, frame, wordmark and crest; the feather where the overlay meets the video is 11 crop pixels wide. The haze at the frame's top-right corner and the strip down its right side are flood-keyed out. The amber recess is a smooth glow (no scan lines; its rim and sides are smoothed vertically to remove the upscale's dark streaks). The live buttons and text are untouched.
