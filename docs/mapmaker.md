# Mapmaker (added content): architecture and contract
Owner's direction (2026-11-05): Phase 1 is a Godot editor plugin; Phase 2 an in-game Forge-style editor; both run on **one shared core**
("Both editors must share underlying functionality rather than becoming two separate mapmaking systems"). Agentic use is a first-class
requirement: every edit is an operation an agent can run headless through `smtool map`, with the same validation as the editor.
The Mapmaker is **added content** (`docs/added-content.md`): `faithful-build.cmd` builds and ships none of it.

## Pieces
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
`place {asset|prefab, name?, parent?, layer?, transform?}` -> new id · `transform {id, position?, rotation?, scale?}` (set) or
`{id, move?, rotate?, scaleBy?}` (delta) · `duplicate {ids, offset?}` -> new ids · `delete {ids}` (children too) ·
`rename {id, name}` · `set-parent {id, parent}` (keeps world position) · `set-visible {id, visible}` ·
`group-to-prefab {ids, prefab, name, origin: "bounds-bottom-center"|"first"|[x,y,z], collision?, replace?}` (replace: swaps the selection for one
linked instance at the same place) · `prefab-edit {prefab, origin?, addParts?, removeParts?, setPart?}` · `instance-override {id, part, visible?, transform?}` ·
`import-asset {path, id?, name?}` · `import-prefab {path}` · `extract-sm1 {map, objects:[names | {index, part?}], prefab, name, origin?, collision?}` (provenance keeps `indices`, and `parts` when a part was taken).
Snapping is applied by the caller (`Snap.Grid`, `Snap.Angle` helpers in the core) so a typed value is never re-snapped.
Results: `{ ok, created:[ids], warnings:[...], errors:[{ code, message, op, id? }] }`. Error codes are stable strings (`unknown-id`,
`unknown-asset`, `unknown-prefab`, `prefab-cycle`, `bad-transform`, `missing-install`, `bad-file`, ...).
Undo/redo: the session keeps a journal of applied batches with their inverse; one batch = one undo step.

## CLI (`smtool map`; JSON on stdout, exit 0 ok / 1 validation errors / 2 bad arguments)
`new <file> [--name n]` · `apply <file> <ops.json|-> [--dry-run]` · `query <file> objects|prefabs|assets` ·
`query-sm1 <map> [--filter text]` (placed objects and definitions from the install) · `validate <file>` · `preview <file> [--out svg]` (top-down plan) ·
`import-asset <path> [--id]` · `import-prefab <path>` · all take `--library <dir>`.

## Godot plugin (Phase 1)
Own project `mapmaker/`; opened with `mapmaker.cmd` (the owner's editor window; agents use `godot.cmd` rules: headless, or `--windowed` on the
hidden desktop for screenshots). Dock: library browser (SM1 definitions of a chosen map, custom assets, prefabs) with thumbnails; open
`.smmap`; "Open SM1 map" view (Manufactorum) for picking; multi-select -> "Extract to prefab"; prefab panel (mesh list, pivot, collision mode);
drag-drop placement; Godot's move/rotate/scale gizmo plus a numeric transform panel (world/local), grid and angle snap; duplicate, delete;
undo via `EditorUndoRedoManager`, each step applying core operations. Godot nodes are a **view** of the core document: every change goes
through the core, and the scene file never holds SM1 meshes.
