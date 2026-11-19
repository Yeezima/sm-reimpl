# Mapmaker (added content): architecture and contract
Owner's direction (2026-11-05): Phase 1 is a Godot editor plugin; Phase 2 an in-game Forge-style editor; both run on **one shared core**
("Both editors must share underlying functionality rather than becoming two separate mapmaking systems"). Agentic use is a first-class
requirement: every edit is an operation an agent can run headless through `smtool map`, with the same validation as the editor.
The Mapmaker is **added content** (`docs/added-content.md`): `faithful-build.cmd` builds and ships none of it.

## How to make a map in 2 minutes (play-test flow, 2026-11-17)
The Mapmaker now goes from "I want a map" to "I am playing it with bots" in two presses. The dock's tabs follow the work, left to right: **Map**, **Library**, **Build**, **Object**, **Prefab**, **Test**; the mouse tool and Undo are in a bar above the tabs and **Play test** is always at the bottom (layout: `docs/mapmaker-layout-proposal.md`). The 2-minute guide is the tooltip of the Map tab's hint line.

**Typed transform entries (Object tab, owner 2026-11-18):** Position, Rotation and Scale typed in the Object tab apply to exactly the selected objects. A selected child turns on its own, its parent untouched. When a selected object has a selected ancestor (a parent with all or some of its children selected), only the topmost selected object gets the entry (`TransformTargets.Roots`) and the others follow through inheritance, keeping their relation to it, the same as Godot's multi-select rotate; nothing is turned twice and nothing unselected is touched. With several objects selected the entry is the change to the first one's values, applied to each target.
1. **Open the editor**: `mapmaker.cmd` (builds, opens Godot on `mapmaker/`; the dock is on the right).
2. **Map tab > New map.** Type a name if you like, pick a layout (Two-base arena is the default), press **New map**. The map appears, is saved to your maps folder at once and is checked.
3. **Play test.** Choose a mode and how many bots a side on the **Test** tab (the defaults are fine), press **Play test** (the button at the bottom of every tab). It saves, builds, starts a server on this PC only (127.0.0.1, with the F6 dev tools), starts the bots and opens the game on your map. Close the game window to stop everything. In a checkout it builds first (`tools/mapmaker-playtest.ps1`); in the tester install it runs the installed build's server and game (`mapmaker\playtest.ps1`, no building; the launcher's MAPMAKER button tells the editor which build, and a missing game file is named in the status line).
That is **2 presses** from a started editor (3 with a name typed). To change the map first:
- **Build tab**: click a piece (Floor, Wall, Cover block, Ramp, Column, Invisible wall, Marine or Chaos spawn, spawn door, Capture point, Kill zone), then click in the 3D view. It lands where you click, on the floor or on top of a block (stacking), snapped to the grid, with a sensible default size; spawn areas and doors face the middle of the map. Right click or Esc stops placing.
- Move with the editor's gizmo (W move, E turn, R size) or type numbers in the **Object** tab (Scale is the size in metres; for a piece, Position is the middle of its footprint on the ground; Rotation Z turns it). Ctrl+D duplicates, Del deletes, Ctrl+Z undoes (every step).
- **Check list** (Test tab, updates after every edit, in plain words): what is missing or broken, with a **Fix it** button for the common ones (no spawn for a side, no capture point, no door, no floor) and **Select** to find the object. The bots' route is checked on the real walking graph in the background.
- **Game modes** (Map tab; the modes this map is for): tick Annihilation, Seize Ground, or both; any combination, at least one (the file keeps names this version does not know, for future modes). Each ticked mode is checked: Seize Ground needs a capture point; Annihilation needs a spawn area for each side.
- **Share**: the `.smmap` file is the whole map. Send it; the other person puts it in their maps folder (`%LOCALAPPDATA%\sm-reimpl\mapmaker\maps`, or any folder named by `SM_MAPS_DIR`) and plays it. Test tab > Share > Show file / Copy path.
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

## Document model (`.smmap`, formatVersion 2; version 1 files are upgraded on open, see "Older and shared map files"; the optional top-level `prefabs` array holds the embedded prefab definitions)
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

**Import from Quixel / Fab (no prompts).** Drag a `.zip`, `.fbx`, `.glb`, `.gltf` or an unzipped folder onto the Library list or the 3D view, or use
"Import asset" / "Import folder" (a map must be open). Core (`ModelImport`, tested without Godot): zip extraction with a zip-slip guard (no `..`, no
rooted or drive paths; at most 20 000 entries and 2 GB unpacked, counted while unpacking, not from the headers) and only models, images and `.bin`
files are unpacked; the model is chosen by lowest LOD (`_LOD0`; a name with no LOD counts as 0), then `.glb` over `.gltf` over `.fbx`, then the
shallowest and first path (variation folders resolve to the first). Plugin (`AssetImporter`): loads FBX with Godot's runtime FBX reader and glTF with
`GltfDocument`, turns Godot's `ImporterMeshInstance3D` nodes into real `MeshInstance3D` nodes (see "Why previews and bounds were empty"), builds a
material from Megascans / Fab / Unreal texture names for surfaces that have no albedo picture (Albedo/BaseColor/Diffuse/BC, Normal/N, Roughness,
Metalness, AO, ORM, Opacity into albedo alpha, Emissive; Displacement/Height ignored; a normal map named DX/DirectX gets its green channel flipped,
GL/OpenGL not, and an unmarked Megascans-style `<id>_4K_Normal` is read as DirectX per the owner's note: **ours, a refine item**; textures above
2048 px are scaled down), guesses the unit (a model whose largest side is over 30 m is read as centimetres, x0.01, or millimetres, x0.001; ours),
measures the bounds from the loaded meshes and writes one self-contained `.glb`, which `import-asset` stores. `import-asset` also takes optional
`bounds` (min/max in the glTF frame; wins over the accessors), `from` (the source name) and `import` (free-form record kept in `asset.json`: source,
format, model, lod, scale, units, textures, normalGreenFlipped, sourceSha256). The same download dropped again is recognised by `sourceSha256` and not
imported twice. Draco and meshopt files are refused at import with a message (`unsupported-compression`); other failures are named in the dock status
and an editor toast ("Imported Rock (scale 0.01 (centimetres), 3 textures, LOD0)").

**Why previews and bounds were empty.** (1) Godot's runtime glTF/FBX reader returns `ImporterMeshInstance3D` nodes, which never draw in a running scene,
so every custom asset loaded to nothing and its box stayed empty; they are now converted. (2) A glTF whose POSITION accessors lack min/max has unknown
bounds, and one such primitive discarded the box of the others; the editor now measures the loaded mesh when the library has no box. (3) Draco and
meshopt files load as nothing. (4) The library list requested pictures only for the first 60 rows and capped SM1 definitions at 400; every row is
now rendered lazily (rows in view first, the rest in the background). (5) A part that failed or an empty model was skipped silently; it now shows a
placeholder tile that names the reason (`no mesh`, `load failed`, `map not loaded`, `no geometry`, `empty prefab`, `missing asset`, `unreadable`) and
is logged once. (6) A picture of a prefab whose SM1 map was not loaded yet was cached with holes; it is a placeholder until the map loads, and the
cache file name now carries the content (asset hash, prefab text plus its assets' hashes) and a render version, so changed content is drawn again and
the old file is deleted. (7) SM1 definitions that Manufactorum's armoury rule hides (`MapDecor.Hidden`) built to nothing; they are drawn for the
library under another name. Definitions with no visual part at all (markers, lights, child-only groups) are counted under the list, not listed.

**Extracted single part (owner's report 2026-11-18).** A prefab made from one picked part of a composite SM1 placement (for example one generator block of `tarmacgroundplane`) stores `asset.part`, and every view of it (thumbnail, ghost, placed instance) now draws that part alone (`ContentFactory.BuildSm1Part`). The old build copied the whole definition with `with { Visuals = ... }`, which kept its cached `HighestLod()` list and so drew every neighbouring part. The core's bounds and the extract origin already used the part's own box. Checked by the selftest ("the part prefab draws only that part") and `--mm-libtest` (1 of 48 instances). The game's custom-map view draws graybox pieces only, so it never drew SM1 parts.

**Rename.** Custom assets and prefabs: F2, double click on the name, or right click "Rename..." in the Library list (Enter confirms, Esc cancels). Only
the display name changes (`asset.json` / the `.smprefab`); the id and folder stay, and maps and prefabs refer to the id, so they keep resolving. The name
is trimmed with inner whitespace made one space, 1 to 80 characters, no control characters; a name another item of the same kind already has is
rejected (`duplicate-name`, compared without case; an asset and a prefab may share a name). Not undoable. SM1 definitions are read-only.

r
**Delete.** Right click a custom asset or prefab in the Library list and choose "Delete..." (SM1 rows are read-only). A dialog asks first and says how many placed copies in the open map go with it. A prefab goes with its placed copies (and whatever is parented under them); a custom asset (`delete-asset {asset}`) goes with the objects that place it, but is refused (`asset-in-use`, nothing changes) while a prefab still uses it: edit that prefab first. Both are one undo step; an asset's folder moves to its own folder `library/trash/assets/<id>-<time>-<n>` (deleting, re-importing and deleting again keeps both copies) and Ctrl+Z moves it back. The dialog also names other saved maps in the maps folder that use the item: they are not changed and show it as missing. Nothing is left pointing at a missing item.

**Group to prefab keeps every part where it was** (owner, 2026-11-18). The prefab's part transforms are the parts' world transforms relative to the chosen origin and the replacing instance sits at that origin with no rotation or scale, so every part's world position, rotation and scale is unchanged (any origin; tested with rotated, scaled and offset parts). The one case that used to shift parts: an instance with part overrides (moved or hidden parts) lost its overrides when grouped, so its parts jumped back to the prefab's own places. Such an instance is now unfolded into its parts as drawn (hidden parts are left out) and a warning says so. SM1 pieces (boxes, ramps, spawn areas) cannot be prefab parts and are not part of a group.
Test aid: `godot.cmd --windowed --editor --path mapmaker -- --mm-libtest --mm-out <dir>` (with `SM_MAPMAKER_CONTROL` set to a scratch folder) builds
synthetic fixtures (an ASCII FBX zip at three LODs with Megascans-named textures, a loose glb, a zip-slip zip, a Draco glTF), imports them by drop,
checks the previews of all three sources, the placeholders, rename and takes screenshots.

## Nesting and groups (owner's pass 3, 2026-11-19)
Two ways to hold objects together, both in the file, both undoable.
**Nesting** is the `parent` field: a child's transform is local to its parent, so it follows when the parent moves. Any object can be the parent of any other, whatever
their kinds (assets, prefab instances, pieces, empty objects); the compiler and the play test read the nested world transforms. The Scene tab's Objects list (drag a row onto
another to nest it, onto empty space to unparent; **Make child of...**, **Unparent**) and a drag in Godot's Scene dock all become `set-parent` operations, which keep the object's world
position and refuse cycles. (The editor's own history keeps its entry for a Scene-dock drag too; undo that step with the Mapmaker step above it first.)
**Groups** are an editing aid with no transform and no effect in the game (the compiler never reads them): a named set of objects and smaller groups, saved in the file as an
optional top-level `"groups": [ { "id": "g1", "name": "Gate", "members": ["o1", "o2", "g2"] } ]` (written only when there are groups; `formatVersion` stays 1 and a file
without the key opens as before; a file that lists a missing object or lists a member twice is repaired on open, not refused). An object or group is in at most one group, so groups
form a tree. Clicking an object selects only it (it moves alone); clicking a group row in the Scene tab selects every object in it, as does a double-click on a member in the 3D view
(again, soon after: the group around it); any mix of selected objects moves together with the gizmo. Actions: New group from selection (whole groups in the selection become one
member, so selecting two groups and grouping nests them), Rename, Add selected, Remove selected, Ungroup, Delete group (objects stay); dragging an object onto a group row adds it.
Test aid: `godot.cmd --windowed --editor --path mapmaker -- --mm-groups --mm-out <dir>`.

## Operations (one JSON object each; a batch is one transaction: all apply or none)
`place {asset|prefab, name?, parent?, layer?, transform?}` -> new id Â· `transform {id, position?, rotation?, scale?}` (set) or
`{id, move?, rotate?, scaleBy?}` (delta) Â· `duplicate {ids, offset?}` -> new ids Â· `delete {ids}` (children too) Â·
`rename {id, name}` Â· `set-parent {id, parent}` (keeps world position) Â· `set-visible {id, visible}` Â·
`group-to-prefab {ids, prefab, name, origin: "bounds-bottom-center"|"first"|[x,y,z], collision?, replace?}` (replace: swaps the selection for one
linked instance at the same place) Â· `prefab-edit {prefab, origin?, addParts?, removeParts?, setPart?}` Â· `delete-asset {asset}` (refused with `asset-in-use` while an object or prefab uses it; undo restores the folder from the library trash) · `delete-prefab {prefab}` (refused with `prefab-in-use` while this map or another prefab uses it; undo restores the file) Â· `instance-override {id, part, visible?, transform?}` Â·
`import-asset {path, id?, name?}` Â· `import-prefab {path}` Â· `extract-sm1 {map, objects:[names | {index, part?}], prefab, name, origin?, collision?}` (provenance keeps `indices`, and `parts` when a part was taken).
`import-sm1-map {map, name?}` -> the group's id (the owner's "edit an original map", 2026-11-18): every drawn placement of the original map becomes an ordinary
object (`asset {source: sm1, map, definition}`, the exact placement decomposed once) under one group named after the map (`name` overrides), all one undo step.
Left out, each said in the result's warnings: placements whose definition draws nothing (markers, lights, empty groups), the armoury stations the game also hides
(editor source only), a sheared matrix, and anything that would take the whole map past 20000 objects or 8 MB (also across a second import; at most 15000 per import, `Sm1Import.MaxObjects`; a full map refuses with `too-big`). Not in the document at
all: decals, lights, terrain, collision, the original's script logic, and composite placements (Manufactorum's `tarmacgroundplane`) come in whole, not part by part.
**Spawns and doors (2026-11-19).** The import also creates the original's start areas as `spawn-area` pieces and its spawn-room blockers as `blocker` ("Spawn door") pieces under the same group, at the places the game server itself reads (`InstalledSm1Source.LoadSpawns`, which runs `MapContent.Load`: the `mkr_start_marine` / `mkr_start_ork` markers and the `marine_blocker_*` / `chaos_blocker_*` boxes; docs/discovery.md "Start areas", "Spawn rooms on Manufactorum"). A marker is a centre and a full size, a piece sits on the floor, so z drops by half the height; the marker's Z rotation becomes the piece's facing. A thin "Invisible wall" floor (0.5 m, top at the area's bottom) goes under each area so the check finds standing room (the original's own floor is collision the document cannot hold). A side the data has no area for (or a map the server loader cannot read) gets a labelled placeholder area and floor and the import warning says so; round start areas become boxes. They are ordinary objects: move, delete, save. The whole map is still not solid: add Invisible walls round the SM1 pieces before Play test (the bots' route check will say where the halves are cut).
The original's Seize Ground capture points come in too, as `capture-point` pieces (the same reader's `ControlPoints`, in script order, so the first is A; a floor placeholder under each; three labelled placeholder points and a warning when the data has none), so an imported map passes the check for both modes.
**SM1 definitions library (2026-11-19).** Library > SM1 definitions lists every Classic PvP map (`MapCatalog`) with no map opened: a heading per map in "All maps", or one map from the dropdown filter above the list. The names come from a background index (`Sm1Catalog` in the plugin, `Sm1Index` in the core) built one map at a time and cached as names-only JSON in `%LOCALAPPDATA%sm-reimplchemapmakersm1-definitions`, renewed when the map's archive size or time changes. Pictures need the map in memory: choosing a map in the filter, or arming a row, loads it in the background (it does not become the backdrop); placing from a map that was never opened works the same way. **Remove SM1 backdrop** (Map tab) takes the opened original map out of the view for good (the Show box only hides it); imported and placed objects stay, Undo brings it back.
Plugin: Map tab > SM1 reference map > **Import SM1 map for editing** (`MapmakerPlugin.ImportSm1ForEditing`) reads the map if needed and always works in a new unsaved map: "Remix of ..." when
none is open, else a copy of the open one (so a plain Save asks for a new file and never overwrites it); imports, hides the read-only backdrop and frames the view; each object is a node in the Scene tree under the group (select, move, delete there or with
the gizmo). **Save as...** stores it as a new map: the file holds references (map, definition, transform) only, no original bytes; the game install is only read.
The imported pieces are not solid in play (see "Not in v1"): add a Floor and Invisible walls (or the Fix it buttons) before Play test.
Snapping is applied by the caller (`Snap.Grid`, `Snap.Angle` helpers in the core) so a typed value is never re-snapped.
Group operations (editing groups, see "Nesting and groups"): `group {ids: [objects and groups], name?}` -> the new group's id (`g<N>`; each member leaves the group that held it, and the
new group sits inside the group that held the first member) - `group-rename {id, name}` - `group-add {group, ids}` (refuses a group inside itself: `group-cycle`) - `group-remove {ids}` (out of
their group, still in the map) - `ungroup {id}` (dissolves the group; members go to the group around it) - `delete-group {id}` (the group and the smaller groups in it; objects stay).
Unknown group: `unknown-group`. Deleting an object takes it out of its group.
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

**Viewport controls (2026-11-18).** *Always box-select:* with the Select tool a left drag from anywhere in the 3D view, also from over a placed
object or an SM1 piece, draws a rectangle and selects what it covers on release (placed objects when their box overlaps it, SM1 pieces when their box
centre is inside it); Shift adds, Ctrl removes; a plain click behaves as before. Objects move only with the gizmo handles (W/E/R); a press within about 70 px
of the selection's centre goes to Godot so the handles keep working. *Keys + stamp:* arming an item (a library row, a Build button, a drop) shows a see-through
preview of the real model at the cursor, snapped like the placed copy; the mouse wheel turns it (15 degrees or the angle snap), Shift + wheel scales it
(10 %), the wheel never zooms the camera while armed; LMB places a copy with the preview's exact position, turn and size and the item stays armed; Esc or
right click removes the preview and disarms; the preview hides while the mouse is outside the view. *Any scene tab:* each scene tab holds its own map (the
session follows the edited scene); placing never switches tabs. A tab without a Mapmaker root is offered one ("Add a Mapmaker map here", a `MapmakerRoot` node
under the scene's root); the SM1 map and the prefab library are shared, so a prefab extracted in one tab is placeable in any other. The flow test
(`--mm-flow`) covers all three.

**Play test cannot load another map any more (2026-11-19, pass 3).** Findings: the owner's maps folder holds several files named "My Map" (the dock's default name); before 11-18 the client looked the map up by that name and took the first file alphabetically (an old copy, often the default layout), and a tester whose launcher never finished syncing `mapmaker-project\` (see docs/releasing.md "The Mapmaker", the sync's failure modes) kept the old editor and the old `playtest.ps1` after updating, so the old behaviour stayed. Now: (1) the editor passes the file's own hash (`-ExpectHash`) and both play-test scripts read the server's `custom map cm_... map hash ...` log line and stop with a plain reason when it is missing (the default arena) or another file's, before the game opens; (2) `--custom-map <file>` that does not exist is an error, never a fallback to a lookup by name; (3) without `--custom-map` a client picks the newest saved file of that name and prints that it guessed (`CustomMaps.LocateAll`); (4) the Test tab status names other files with the same map name (`CustomMaps.SameNameAs`): rename the map to tell them apart. Identity of a play test = the exact file (path) plus its hash; the display name is only a label.
**Older and shared map files (2026-11-19).** `formatVersion` is now 2. `MapUpgrade` (core) holds numbered steps; any older file reads in memory the same way everywhere (one hash for server, client and editor). Opening an older file in the editor first copies the original to `name.smmap.bak-v1` (never replaced; not a `.smmap`, so no list or search finds it), then writes the file in the current format and says so in the status line. A file made by a newer Mapmaker is refused in plain words. 1 to 2 adds `prefabs`: Save map puts the definition of every prefab the map uses (and the prefabs those use) into the file; opening a map adds the ones your library lacks (a library prefab with the same id is never overwritten, and a differing copy is mentioned). Before, a map sent to another PC had "unknown prefab" on every instance. Custom (glTF) assets are still library-only. Note for play: SM1 placements and prefab instances are decoration (not collision); only pieces play, so a map made of them needs a Floor and walls from the Build tab.
**Play test picks a saved file (2026-11-18).** Play test now opens a file dialog (`EditorFileDialog`) and plays exactly the `.smmap` file chosen there; the server, the bots and the game all get that path (`--custom-map <file>`), so the hash always matches. Unsaved edits are never played: press Save map first (the dialog and status say so). Cause of the old "map could not be loaded here, or differs" error: only the server got the file; the client looked it up by the wire name (`cm_my_map`), and an older `my-map.smmap` with the same map name in the same folder (the new map was saved as `my-map-2.smmap`) was found first, with another hash. `CustomMaps.Locate` now also trusts the file's `name` field before its file name.

**Edit controls (2026-11-19).** *Alt box:* with Alt held a left press in the 3D view always starts a selection box (also over objects and on the gizmo) and a release without a drag selects nothing; release Alt for the normal click. *Duplicate* (button, Ctrl+D) puts the copy exactly on the original and selects it. *Spin arrows* (Object and Prefab tabs): right click steps by 10 (position, origin), 45 degrees (rotation), 0.5 (scale); holding an arrow repeats 5 times a second after 0.5 s, one plain step each for the first 3 s, then the step doubles every 1.5 s up to 4096 times (`SpinSteps`). *Group to prefab...* shows a name and id dialog (prefilled "<first object> group") before saving; Cancel changes nothing.
