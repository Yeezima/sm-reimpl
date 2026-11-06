# tools/blender

Headless Blender scripts that prepare the owner's Ashfall kit models. They are our code (GPL-3.0, like the rest of the repository).
The models themselves are the owner's private files: the scripts only read `%LOCALAPPDATA%\sm-reimpl\ashfall-kit\source\` and write
to `%LOCALAPPDATA%\sm-reimpl\ashfall-kit\processed\`. No output ever goes into the repository.

- `ashfall_bake.py` weathers each Meshy glb that has a base colour texture: a new 1024 px base colour (cooler, greyer, darker; cavity/AO soot,
  soot at the wall base, vertical streaks, blotches, grit, lighter worn edges, a small per-asset variation) is baked on the model's own UV map
  with Cycles, the glb is exported to the same relative path under `processed\`. Recipe numbers are constants at the top of the file.
- `ashfall_rubble.py` makes four small rubble props `processed\rubble\AR-001..AR-004.glb`, textured from a kit wall's stone through the same recipe.

Run them only without a window, in bounded batches (the baker takes `--only` to limit the list):

```
blender.exe --background --factory-startup --python tools/blender/ashfall_bake.py -- --src <source> --out <processed> [--only AC-001,AC-002] [--size 1024] [--samples 16] [--preview <dir>]
blender.exe --background --factory-startup --python tools/blender/ashfall_rubble.py -- --src <source> --out <processed>
```

The client prefers the processed copies when they exist; `--ashfall-kit-files source` switches back to the originals.
Blender (GPL) is used as a separate tool; nothing of it is shipped.
