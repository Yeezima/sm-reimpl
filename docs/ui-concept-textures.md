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
