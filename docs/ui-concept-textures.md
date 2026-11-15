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

## Pass 2f: the main menu's video framing (owner, 2026-11-15)

`MenuVideoBackground.FramedRect`: while the overlay shows, the video is enlarged to 1.6x its covering size (less on windows wider than 16:9, so the same part of the picture stays in view) and panned so its focus point (u 0.49, v 0.565: the middle of the Marine and the tower with its god rays, measured on the video's frames) sits at 72 % of the window's width and 50 % of its height, centred in the part right of the overlay; clamped so the picture always covers the window. The video is 1920 x 1080, so at 1.6x it is softer than before.

## Pass 2g (owner, 2026-11-15)

* The nameplate lettering is painted out of the frame's bottom bar (`PaintOutNameplate`; the plate stays as clean steel).
* The wordmark's final L is kept whole: the matte uses a lower threshold for the L's lighter blue (x > 436 crop px), so it has the same height, stroke and bevel as the other letters.
* The left-edge stone is a ruined wall built by the builder from the video's own masonry (`ReplaceWall`): course by course, blocks of random size, each filled with a soft patch of a still of the owner's video, tinted, with mortar, a chipped bevel, chipped and broken corners, cracks, soot and a warm light from the right, and an irregular broken right edge; seeded (same wall every run). Source still: `menu-background-video.mp4` at 2 s, full frame 2560 x 1440, the tower's masonry cut at (1300, 537, 255 x 195) (`ffmpeg -ss 2 -i menu-background-video.mp4 -frames:v 1 video-t2.png`, then `-vf crop=255:195:1300:537`); a Real-ESRGAN x4 of it was tried and left out (it adds hatching), the raw cut is used:

```
godot.cmd -- --build-ui-textures concept-4panel-x4.png --owner-crop owner-ref-main-menu-crop.png --upscaled owner-crop-x4.png --masonry masonry-tower.png
```

## Pass 2h/2i (owner, 2026-11-15)

* The wall is the concept's own painted stone again (`GradeWall`), graded to the video's masonry: per-channel mean and spread moved to those of `masonry-tower.png`, chroma reduced to a grey-brown, a high-pass of the video's masonry laid over as weathering, soot toward the base, a weak warm rim, and a smooth irregular outer edge (slow waves plus a few shallow chips, seeded). Behind and under the wordmark the wall continues from the rows above (two mirrored phases blended), so no concept sky box remains there.
* The sky band between the wordmark and the frame's top is removed completely; the skull and wings are matted from the 4x upscaled crop (`CrestMatte`: warm or bright metal against the dark backing, closed over the eye sockets, kept only if connected to the skull), at the concept's size and place, graded like the frame.
* The overlay's right edge follows the frame's steel (polygon points moved in), and the contact shadow is offset down only (2 crop px right), so no grey strip shows beside the steel.

## Phase 3: a 1:1 main menu (owner, 2026-11-15: "a perfect 1:1 rendition ... until your own screenshot review comparison approves it")

Rebuild (no `--masonry`; passing it restores the graded pass 2h look):

```
godot.cmd -- --build-ui-textures <concept-4panel-x4.png> --owner-crop <owner-ref-main-menu-crop.png> --upscaled <owner-crop-x4.png>
```

* The overlay is the concept's own pixels: no grade, no regraded wall, no shadow. The x4 upscale is first made consistent with the crop by iterative back-projection (`LoadBackProjected`: the upscaler's detail is kept, its k x k block averages are forced to the owner's pixels), which lifted the whole-region SSIM from 0.85 to 0.92. Alpha: the owner's line with a 3 px feather, the wordmark's letters, outline and dark shadow, the crest matte, the frame's steel inside `FramePolygon` (the old sky key had eaten the top bar) and the concept's own dark notch behind the crest (`FaithfulSeat`).
* The five plates are painted out of the recess and come back as the live buttons' own styleboxes: `btn-<id>-dark` / `-gold` (`FaithfulButtons`), the concept's own plates with the concept's own painted labels. The labels that swap rows (MUSOU MODE on a dark plate, CLASSIC PVP on the gold plate) and the gold versions of ARMOURY, OPTIONS and EXIT are the concept's label pixels (alpha from the blue channel against the blank plate, the white of the destination plate) laid on the blank plate. `ConceptMainPage.PaintButtons` applies them; the buttons keep their text (transparent) for the tree, the tests and accessibility; focus and pressed draw the gold texture, hover the warmed dark one. Rows are placed on the concept's pitch (42.85 crop px, plate 35.3 high, x 94 to 430); with other row counts (STOP MUSOU SERVER, a build without Musou) the block is centred and tighter, and unknown rows keep the blank plate and live text. The focus glow is off for these buttons (`FocusGlow.NoGlowMeta`); the scroll list no longer clips (the gold plate reaches past its row).
* Comparison tool: `godot.cmd -- --ui-compare <crop> --ours <1920x1080 capture> [--out dir] [--tag name]` (`UiCompare`). The capture (overlay at the left edge, scale height / 606) is area-averaged onto the crop's grid, then per region SSIM (7 x 7, luminance), dE76 of the regions' mean colours and mean per-pixel dE76, a heat map and a side-by-side (target | ours | heat map). Masks leave out the ordered differences: the two swapped labels, the motto, the final L, the background around the overlay (and the letters' soft drop shadow, which lies on the concept's grey ruins). Capture: `godot.cmd --windowed --resolution 1920x1080 -- --menu --screenshot <png> --profile <scratch> --no-sound --no-screen-blink --quit-after 30` (`--menu-stop-row-demo` shows the STOP MUSOU SERVER row, `--glow-focus <ROW> --glow-at 3` with `--screenshot-every 2` shows a focused row).
* Final numbers (1920 x 1080): wall 0.984, wordmark 0.993, crest 0.979, frame 0.982, recess 0.964, gold plate 0.982, plates 0.943 to 0.995, labels 0.917 to 0.972, FULL 0.977; mean-colour dE at most 0.56 everywhere, mean per-pixel dE at most 1.3 outside the labels (4.4 to 4.6 on the label glyph edges). Every threshold met.

## Phase 4a: Options as panel 04 (owner, 2026-11-15: "do options, armoury, and in game hud in one pass; same method")

Rebuild (adds `--concept1x` and `--panels options`; the other arguments are phase 3's, so the main menu's pieces come out unchanged):

```
godot.cmd -- --build-ui-textures <concept-4panel-x4.png> --owner-crop <owner-ref-main-menu-crop.png> --upscaled <owner-crop-x4.png> --concept1x <concept-4panel.jpg> --panels options
```

* `UiTextureBuilder.Panels.cs`: `PanelRegion` cuts a panel's region from the x4 upscale and back-projects it onto the 1x JPEG's pixels (`BackProject`, the phase 3 technique). `OptionsTextures` makes `overlay-options.png` (736 x 400 panel pixels at x4: the frame, title plate, crest, stone and eagle pieces, the dark interior with its rows, tabs and APPLY / REVERT painted out; alpha = a generous outline polygon minus a gradient-limited flood from its border, with protected zones inside the steel, the title body and the two stone pieces) and the plates: `opt-tab-<graphics|sound|controls|interface>-<dark|gold>`, `opt-apply-<gold|dark>`, `opt-revert-<dark|gold>`, `opt-back-dark`, `opt-toast` (the concept's painted SETTINGS APPLIED chip) and `opt-toast-blank`. Labels move between plates as in phase 3 (`LayLabel`, alpha from luminance against the blanked plate, light ink on dark plates, dark ink on gold). SOUND has no painted label (the concept says AUDIO): live Oswald at the painted labels' size on the blank plate.
* `PaintedButtons` (shared by the next screens) applies a dark / gold texture pair as the button's styleboxes with expand margins (rest, hover warmed, pressed gold, focus gold or a gold ring, disabled grey); the live text stays, transparent where painted. It repaints when the window's scale changes (the first layout pass runs at a tiny size).
* `OptionsView` (concept layout, when `overlay-options` exists): the page maps panel 04's own coordinates (the concept's 780 x 440 screen) onto the window at scale min(h / 440, w / 780), centred; the tabs, the scrolling rows area (the old rows, controls and handlers unchanged; two columns above 1100 px, one below), APPLY, REVERT, BACK (bottom right, as in the concept) and the toast stand on the overlay. Background and `--options-toast` (screenshot aid: shows the SETTINGS APPLIED chip) as before.
* Comparison: `godot.cmd -- --ui-compare-panel <panel screen crop> --ours <capture> --screen options --alpha <overlay png> --alpha-origin 56,52 [--out dir --tag name]` (`UiComparePanel`): the capture is area-averaged onto the concept's 780 x 440 screen; regions are the painted parts, limited to where our overlay is opaque (the coverage column shows how much of each polygon that is); rows, values and the background are left out. INTERFACE tab, 1920 x 1080: title and crest 0.976, frame 0.969, stones 0.952 / 0.948, tabs 0.922 / 0.923 / 0.920 / 0.938, APPLY 0.972, REVERT 0.978, BACK 0.990, interior bands 0.953, FULL 0.962; mean-colour dE at most 0.61.

## Phase 4b: Options rows and the owner's text-uniformity rule (owner, 2026-11-15)

Rebuild: the same command as phase 4a (`--concept1x ... --panels options`); it now also writes `opt-pill`, `opt-switch-off`, `opt-switch-on`, `opt-slider-track`, `opt-slider-fill`, `opt-slider-knob` (`UiTextureBuilder.OptionsControls.cs`, cut from the 4x back-projected panel: the text of the pill and the switches painted out, the slider's gold fill replaced by the empty channel, the fill and knob cut apart, the knob with a rounded alpha) and the blank plates `opt-plate-blank`, `opt-plate-gold-blank`, `opt-back-blank`.

* **One lettering per level (standing rule for every menu, owner 2026-11-15: "all text in each category/level needs to match").** The four tabs, APPLY, REVERT and BACK are now BLANK painted plates; their words are live text in one face (`UiKit.ConceptFont()`: Oswald, 84 % width, embolden 0.32, tracking -1), one size per level (tabs 13.4, action plates 15, in concept pixels times the window's scale), light on a dark plate and dark ink on gold. The concept's painted labels (and the labels moved between plates) are no longer used anywhere on this page. Row labels and row values use the same face (label 14.3, value 12.6).
* Rows: `MenuKit.ConceptRows.cs` (`MenuChoice.UseConcept`, `MenuSlider.UseConcept`) draw the concept's pill, OFF / ON switch (the ON art is 79 x 24, the OFF art 76 x 23) and slider (track, gold fill, knob; the track's corners are not stretched: `ConceptScaled` resamples the 4x art to the window's scale, because a nine-patch does not scale its corners). `OptionsView.Rows.cs` lays the rows on the concept's grid (row pitch 31.2, content from x 128, columns 284 apart, label column 104 or the widest label of the column, two columns when every label leaves room for its control, else one; the page scrolls inside the frame as before). Sliders hang 3 px below the row's centre, as the concept's do.
* Frame (owner: "no grey boxes ... crisp and SYMMETRIC ... one clean half mirrored"): the stone pieces use a hand-measured silhouette (`StoneLeft`) instead of the flood's guess; the left half of the overlay (up to the panel's axis, x 400) is mirrored onto the right, pixels and alpha, except the title plate and crest; hairline haze (alpha under 0.2) goes, the dark fringe of the scene's shadow is eaten from the outside in, and the alpha is opened (erode then dilate, 3 px at 4x) so no crumbs or hairlines remain. The line above APPLY and REVERT is painted out. BACK is cut exactly to its plate with a chamfered hard-edged alpha (no grey box).
* Measuring: `--ui-compare-panel ... --dump-rows <file>` (client option written by `OptionsView`: each control's rectangle in concept pixels) and `--rows <file>` (`UiComparePanel.ControlRows`): the first control of each kind is compared segment by segment (left cap, middle, knob and right cap; pill arrows and rims; whole switches), anchored at the control's left, right or middle end, with a registration search of +-1.5 px reported. Labels are masked out (live text).
* New reference (owner-ref-panel04-v2.png, 825 x 465): it is the old screen at 0.9333 scale placed at (5, 2) (found by correlation, 0.949); `target-v2-p04.png` is that placement on the old target's 780 x 440 grid.
