# Mapmaker (added content): architecture and contract
Owner's direction (2026-11-05): Phase 1 is a Godot editor plugin; Phase 2 an in-game Forge-style editor; both run on **one shared core**
("Both editors must share underlying functionality rather than becoming two separate mapmaking systems"). Agentic use is a first-class
requirement: every edit is an operation an agent can run headless through `smtool map`, with the same validation as the editor.
The Mapmaker is **added content** (`docs/added-content.md`): `faithful-build.cmd` builds and ships none of it.

## How to make a map in 2 minutes (play-test flow, 2026-11-17)
The Mapmaker now goes from "I want a map" to "I am playing it with bots" in two presses. Everything below is in the dock's **Start** tab (the first tab; the same text is its quick-start panel).
1. **Open the editor**: `mapmaker.cmd` (builds, opens Godot on `mapmaker/`; the dock is on the right).
2. **Start tab > New map.** Type a name if you like, pick a layout (Two-base arena is the default), press **New map**. The map appears, is saved to your maps folder at once and is checked.
3. **Play test.** Choose a mode and how many bots a side, press **PLAY TEST**. It saves, builds, starts a server on this PC only (127.0.0.1, with the F6 dev tools), starts the bots and opens the game on your map. Close the game window to stop everything. In a checkout it builds first (`tools/mapmaker-playtest.ps1`); in the tester install it runs the installed build's server and game (`mapmaker\playtest.ps1`, no building; the launcher's MAPMAKER button tells the editor which build, and a missing game file is named in the status line).
That is **2 presses** from a started editor (3 with a name typed). To change the map first:
- **Build tab**: click a piece (Floor, Wall, Cover block, Ramp, Column, Invisible wall, Marine or Chaos spawn, spawn door, Capture point, Kill zone), then click in the 3D view. It lands where you click, on the floor or on top of a block (stacking), snapped to the grid, with a sensible default size; spawn areas and doors face the middle of the map. Right click or Esc stops placing.
- Move with the editor's gizmo (W move, E turn, R size) or type numbers in the **Object** tab (Scale is the size in metres; for a piece, Position is the middle of its footprint on the ground; Rotation Z turns it). Ctrl+D duplicates, Del deletes, Ctrl+Z undoes (every step).
- **Check list** (Start tab, updates after every edit, in plain words): what is missing or broken, with a **Fix it** button for the common ones (no spawn for a side, no capture point, no door, no floor) and **Select** to find the object. The bots' route is checked on the real walking graph in the background.
- **Game modes this map is for**: tick Annihilation, Seize Ground, or both; any combination, at least one (the file keeps names this version does not know, for future modes). Each ticked mode is checked: Seize Ground needs a capture point; Annihilation needs a spawn area for each side.
- **Share**: the `.smmap` file is the whole map. Send it; the other person puts it in their maps folder (`%LOCALAPPDATA%\sm-reimpl\mapmaker\maps`, or any folder named by `SM_MAPS_DIR`) and plays it. Share > Show file / Copy path.
Agents and tests: `smtool map new f.smmap --template arena`, `smtool map check f.smmap --text`, `tools/mapmaker-playtest.ps1 -Map f.smmap -Mode seize -Test -OutDir <dir>`.

## Pieces (gameplay)
Objects with `"piece": {"kind": ..., "side": ...}` instead of an asset or prefab (a document has any mix). Position = the middle of the footprint on the ground, scale = size in metres, rotation Z = facing in degrees (0 faces +Y, positive turns toward -X; the same number as an install marker's rotation). Kinds: `box` (solid), `ramp` (solid, rises toward +Y), `column` (solid), `collision-box` (solid, not drawn in the game: put it round decoration), `spawn-area` (side marines or chaos: the start area, spawn spots on a 2 m grid on the floor inside it, players face the area's +Y unless a door gives the room an exit), `blocker` (side: a spawn door, a box the other side cannot cross), `capture-point` (a cylinder: diameter, diameter, height; points are lettered A, B, C in document order), `kill-zone` (anyone inside dies). Pieces are never grouped into prefabs. Operations: `place {piece, ...}`, `set-piece {id, side}`, `set-modes {modes}`.
Layouts (`smtool map templates`): `arena` (22 solid pieces, two walled spawn rooms with doors, a raised middle with ramps, three points), `open-field`, `blank`.
Not in v1 (said so in the editor): pickups (Classic PvP has none to place), SM1 pieces and imported models as **solid** (they are drawn in the editor only; put an Invisible wall round them), lights and sky (the game draws custom maps in a plain daylight look).

## The check (`MapCheck`)
Run on every edit and by `smtool map check`. Errors stop a mode, warnings do not: no ground; no spawn area for a side; spawn area with no floor, or overlapping the other side, or inside a kill zone; no capture point (Seize Ground only); more than 8 points; (warnings) a spawn area under 6 spots, no door, a point with no floor under it, a huge or tiny map. With the install (always in the editor): the bots' walking graph is built (or read from the cache `%LOCALAPPDATA%\sm-reimpl\cache\nav`) and a route is looked for from each side's spawn to the other and to every point (`no-route` is an error, `point-unreachable` a warning). Falling more than 6 m below the lowest solid piece kills (the map adds a kill floor), so open edges are safe.

## Playing a map (server, client, hash)
- Server: `server.cmd --bind 127.0.0.1 --custom-map f.smmap --mode annihilation|seize` (added content; not with `--map`, `--maps` or Musou). A name that came from the network is never used as a path: it must be `cm_` and plain letters, digits and `_`, and only the search folders are looked in. Files are limited to 8 MB, 20000 objects, 5000 pieces, positions within 100 km and sizes within 10 km (`too-big`). The map's name on the wire is `cm_<name>` (at most 24 letters, digits, `_`).
- Client: told only that name by the server, it finds the file in `SM_MAPS_DIR`, the library's `maps` folder, `maps` beside the program or in the working folder, or is given it with `--custom-map f.smmap`; a missing file gives a plain error naming the folder to put it in. Bots find it the same way.
- The server and every client compile the same file with the same code (`SmMapmaker.CustomMap`): collision (solid pieces, one mesh for characters and shots), start areas, doors, points, kill zones. The handshake's map hash is the first 8 bytes of `SHA-256(canonical JSON of the document + compiler version)`, so a client with a different copy is refused; the collision's own content hash is part of the world's as for every map. **No protocol change, no config-hash change**; original maps are untouched (`BotNavigation.NewMapRules` only adds `cm_` to the maps that get the new-map bot rules).
- Bots: no separate file; each bot builds the walking graph from the compiled collision like on any map (cached by content).

## Pieces (the editor's architecture)
| Piece | Path | Depends on |
|---|---|---|
| Core (no Godot) | `src/SmMapmaker/` (`SmMapmaker.csproj`, net10.0) | SmContent (SM1 readers), SmFormats |
| CLI | `src/SmTool/Extras/MapCommand*.cs` (`smtool map ...`) | SmMapmaker |
| Tests | `tests/SmMapmaker.Tests/` (synthetic fixtures only, never original bytes) | SmMapmaker |
| Godot plugin | `mapmaker/` (own Godot 4.7.2 .NET project; `addons/sm_mapmaker/`), `mapmaker.cmd` | SmMapmaker, SmContent |
The game client (`client/`) is not changed. The plugin may compile client view sources as linked files (read-only reuse).

## Rules
- **Original assets never touch disk.** SM1 meshes, textures and materials are streamed from the install in memory. `.smmap`, `.smprefab`,
  `.tscn` and library files hold only references (map, definition and object names) and transforms. Tests check this.
- **Library** (private, outside the repo): default `%LOCALAPPDATA%\sm-reimpl\mapmaker\` (`maps/`, `library/prefabs/<id>.smprefab`,
  `library/assets/<id>/` with the imported file + `asset.json`, `library/index.json`). Override: `--library <dir>` / plugin setting.
- **Determinism:** canonical JSON (UTF-8, LF, 2-space indent, properties in a fixed declared order, floats as shortest round-trip
  `R` text, invariant culture). The same operations on the same document give byte-identical files, CLI or editor.
- **Stable ids:** objects `o<N>`, from the document's `nextId` counter; never reused. Prefabs and assets: lowercase slug ids chosen at creation.
- **No transform drift:** transforms are stored as components (position m, rotation Euler degrees X/Y/Z in `MapScene.Transform` order,
  scale), never as composed matrices. Edits set components; deltas apply to components. SM1 placements decompose once at extraction.
- **Frame:** documents use the SM1 source frame as `MapScene` produces it, so extracted pieces keep their exact relationship. Views apply the
  same conversion the client's `MapView` applies.

## Document model (`.smmap`, formatVersion 1)
```json
{ "format": "smmap", "formatVersion": 1, "name": "My map", "nextId": 4,
  "objects": [ { "id": "o1", "name": "crate", "parent": null, "layer": "default", "visible": true,
                 "transform": { "position": [0,0,0], "rotation": [0,90,0], "scale": [1,1,1] },
                 "asset": { "source": "sm1", "map": "mp_tdm_09", "definition": "<O3d name>" } },
               { "id": "o2", "name": "bay", "prefab": "manufactorum-bay", "overrides": [], "transform": {...} },
               { "id": "o3", "name": "statue", "asset": { "source": "custom", "id": "my-statue" }, "transform": {...} } ] }
```
An object has exactly one of `asset` or `prefab` (or neither: an empty group). `parent` nests objects; transforms are local to the parent.
An SM1 `asset` may add `"part": N` (index into the definition's nearest-LOD parts) to draw one part only. SM1 bakes much of a map into a few
composite placements (Manufactorum's `tarmacgroundplane` holds the floor, factories, gates and generator buildings in 48 parts), so the editor
picks such placements part by part (ours: 8 or more parts, or wider than 40 m) and names a part by its SM1 node (e.g. `CFactory_Gate`).
The part index follows `ObjectDefinition.NearestLod()`, itself a provisional rule: if that rule changes, saved part indices may point at other parts
(the prefab part's name keeps the node name, so a mismatch can be spotted).

## Prefab (`.smprefab`, formatVersion 1)
`{ format, formatVersion, id, name, tags[], origin:[x,y,z], collision: "mesh"|"original-cut", parts:[{ id:"p1", name, asset, transform }],
provenance:{ source:"sm1"|"custom"|"mixed", map?, objects:[names] }, bounds:{min,max} }` (bounds when known).
Prefab parts can be SM1 or custom assets, or other prefabs (`prefab` instead of `asset`; cycles rejected).
Linked instances: an object with `prefab` draws the master; `overrides` (Phase 1: per-part `visible` and `transform`) survive master edits
while the part id still exists; overrides of removed parts are dropped with a warning.

## Custom assets
`import-asset` copies a glTF 2.0 file (`.glb`, or `.gltf` with its buffers and images) into `library/assets/<id>/` and writes `asset.json`
`{ id, name, file, bounds, sha256, importedFrom }` (bounds from the POSITION accessors' min/max with node transforms). `import-prefab` copies
a `.smprefab` into the library after validation (unknown asset ids are errors).

## Operations (one JSON object each; a batch is one transaction: all apply or none)
`place {asset|prefab, name?, parent?, layer?, transform?}` -> new id Â· `transform {id, position?, rotation?, scale?}` (set) or
`{id, move?, rotate?, scaleBy?}` (delta) Â· `duplicate {ids, offset?}` -> new ids Â· `delete {ids}` (children too) Â·
`rename {id, name}` Â· `set-parent {id, parent}` (keeps world position) Â· `set-visible {id, visible}` Â·
`group-to-prefab {ids, prefab, name, origin: "bounds-bottom-center"|"first"|[x,y,z], collision?, replace?}` (replace: swaps the selection for one
linked instance at the same place) Â· `prefab-edit {prefab, origin?, addParts?, removeParts?, setPart?}` Â· `delete-prefab {prefab}` (refused with `prefab-in-use` while this map or another prefab uses it; undo restores the file) Â· `instance-override {id, part, visible?, transform?}` Â·
`import-asset {path, id?, name?}` Â· `import-prefab {path}` Â· `extract-sm1 {map, objects:[names | {index, part?}], prefab, name, origin?, collision?}` (provenance keeps `indices`, and `parts` when a part was taken).
Snapping is applied by the caller (`Snap.Grid`, `Snap.Angle` helpers in the core) so a typed value is never re-snapped.
Results: `{ ok, created:[ids], warnings:[...], errors:[{ code, message, op, id? }] }`. Error codes are stable strings (`unknown-id`,
`unknown-asset`, `unknown-prefab`, `prefab-cycle`, `bad-transform`, `missing-install`, `bad-file`, ...).
Undo/redo: the session keeps a journal of applied batches with their inverse; one batch = one undo step.

## CLI (`smtool map`; JSON on stdout, exit 0 ok / 1 validation errors / 2 bad arguments)
`new <file> [--name n]` Â· `apply <file> <ops.json|-> [--dry-run]` Â· `query <file> objects|prefabs|assets` Â·
`query-sm1 <map> [--filter text]` (placed objects and definitions from the install) Â· `validate <file>` Â· `preview <file> [--out svg]` (top-down plan) Â·
`import-asset <path> [--id]` Â· `import-prefab <path>` Â· all take `--library <dir>`.

## Godot plugin (Phase 1)
Own project `mapmaker/`; opened with `mapmaker.cmd` (the owner's editor window; agents use `godot.cmd` rules: headless, or `--windowed` on the
hidden desktop for screenshots). Dock: library browser (SM1 definitions of a chosen map, custom assets, prefabs) with thumbnails; open
`.smmap`; "Open SM1 map" view (Manufactorum) for picking; multi-select -> "Extract to prefab"; prefab panel (mesh list, pivot, collision mode);
drag-drop placement; Godot's move/rotate/scale gizmo plus a numeric transform panel (world/local), grid and angle snap; duplicate, delete;
undo via `EditorUndoRedoManager`, each step applying core operations. Godot nodes are a **view** of the core document: every change goes
through the core, and the scene file never holds SM1 meshes.

