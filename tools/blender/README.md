# tools/blender

Headless Blender scripts that prepare the owner's Ashfall kit models. They are our code (GPL-3.0, like the rest of the repository).
The models themselves are the owner's private files: the scripts only read `%LOCALAPPDATA%\sm-reimpl\ashfall-kit\source\` and write
to `%LOCALAPPDATA%\sm-reimpl\ashfall-kit\processed\`. No output ever goes into the repository.

- `ashfall_bake.py` weathers each Meshy glb that has a base colour texture: a new 1024 px base colour (cooler, greyer, darker; cavity/AO soot,
  soot at the wall base, vertical streaks, blotches, grit, lighter worn edges, a small per-asset variation) is baked on the model's own UV map
  with Cycles, the glb is exported to the same relative path under `processed\`. Recipe numbers are constants at the top of the file.
- **Arena models at arena scale (kit-hi-res, 2026-11-09).** The recipe's numbers are metres, but a Meshy file is normalised to about 2 m and the map draws it 8 to 33 times larger.
  Baked at file scale, the grit, stone tones, streaks, foot band and AO came out building-sized smears that hid the source's masonry (the owner: "some of the 3d asset buildings
  appear very low poly after the texture and other blender work"). The models placed inside the battle arena are therefore baked with `--scales` (from
  `smtool ashfall-kit-bake-scales`: each file's glTF X, Y, Z scale of its largest arena item) at `--size 2048`:
  - the object is scaled to its arena size for the bake only; the mesh is exported as read;
  - patterns finer than 6 texels at the model's texel density (`TEXEL_PERIOD_MIN`) are faded (grit, block tones, thin streaks) and noise octaves above it dropped;
  - stains fade on thinly textured models (`STAIN_FULL_DENSITY`, `STAIN_MIN`);
  - a model without a normal map (the Meshy expansion) gets a tangent-space normal map baked from its source colour's relief (`RELIEF_STRENGTH`, `RELIEF_DISTANCE`).
  Models only placed outside the arena keep the earlier 1024 px file-scale bake.
- `ashfall_rubble.py` makes four small rubble props `processed\rubble\AR-001..AR-004.glb`, textured from a kit wall's stone through the same recipe.

Run them only without a window, in bounded batches (the baker takes `--only` to limit the list):

```
blender.exe --background --factory-startup --python tools/blender/ashfall_bake.py -- --src <source> --out <processed> [--only AC-001,AC-002] [--size 1024] [--samples 16] [--preview <dir>] [--scales <file.json>]
smtool.cmd ashfall-kit-bake-scales --out <scales.json>   :: the arena models' bake scales (then: --only <their stems> --size 2048 --scales <scales.json>)
blender.exe --background --factory-startup --python tools/blender/ashfall_rubble.py -- --src <source> --out <processed>
```

The client prefers the processed copies when they exist; `--ashfall-kit-files source` switches back to the originals.
Blender (GPL) is used as a separate tool; nothing of it is shipped.
