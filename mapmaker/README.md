# Mapmaker, Phase 1 (Godot editor plugin)

Added content (docs/mapmaker.md). Own Godot 4.7.2 .NET project; the plugin is `addons/sm_mapmaker`. It edits `.smmap` documents through the shared
core (`src/SmMapmaker`): the Godot nodes are only a view, every edit is a batch of core operations, and no SM1 mesh, texture or material is ever
saved (SM1 content is streamed from your install into memory; scenes hold only ids and transforms).

## Open it
`mapmaker.cmd` (repo root): builds the plugin, opens the Godot editor on `mapmaker/`. First start imports the project (a few seconds). Needs Godot 4.7.2
(client.cmd downloads it) and your Space Marine install (found like the game does).
The dock is **Mapmaker** in the right-hand dock group next to Inspector (use the small arrows beside the tabs if it is scrolled out of view).
The scene `Maps/Empty.tscn` opens by itself; it is just the view, the map is the `.smmap`.

## Make a map in 2 minutes
Tabs, in work order: Map, Library, Build, Object, Prefab, Test (the mouse tool and Undo above them, Play test below). On the **Map** tab: 1. pick a layout and press **New map**; 2. press **Play test** (a server on this PC only, bots, and the game open on your map). Change things first with the **Build** tab (click a piece, click the 3D view), fix anything red in the **Test** tab's Check list (Fix buttons do it for you), tick the game modes the map is for, and send the `.smmap` file to share it. Full guide: docs/mapmaker.md "How to make a map in 2 minutes".

## Quick tour (the older SM1 piece workflow, Map and Library tabs)
1. Dock > Map > SM1 reference map > **Open SM1 map** (default mp_tdm_09): Manufactorum appears as a grey-textured reference; the tool switches to **Pick SM1**.
2. Click objects in the 3D view (shift/ctrl adds). Yellow boxes mark them. Dock > Prefab > Create > **Extract picked SM1 objects...**: name, origin
   (bounds bottom centre or first object), collision (mesh or original-cut). The prefab goes to the private library, not the repo.
3. Map > **New map**, then Library > Prefabs: click a row, click in the 3D view to place (Esc / right click stops). Rows can be dragged into the view too.
4. Select an object (click it): Godot's move/rotate/scale gizmo works (W/E/R); on release the move is stored in the document, snapped to the grid and angle
   steps of the Map tab (Snap). The **Object** tab edits position/rotation/scale numerically (local or world), name, visibility; Duplicate (Ctrl+D) and Delete (Del)
   over the 3D view, **Group to prefab**.
5. **Prefab** tab: parts list with per-part transform, remove part, hide a part on one instance, pivot (shift origin), collision mode, name.
   Instances are linked: editing the prefab updates every instance; per-instance part overrides survive.
6. Library > **Import asset** (.glb/.gltf) and **Import prefab** (.smprefab). Map > **Save / Save As** write `.smmap` (default folder: the library's `maps/`).
7. Map > **Grid** (after Snap): show, colour, cell size, height (**From selection** = lowest point of the selection) and draw-over-objects for the 3D view's fixed grid. It never
   changes with zoom (Godot's own grid is switched off while a Mapmaker scene is open and given back afterwards), is saved with the map (editor only: no effect in the game or
   its hash) and each change is one undo step. Test aid: `--mm-grid`.
8. Map > **Look** (**Look from SM1 map**: draws the map in play with an original map's lighting and sky; players need that map installed) and, in SM1 reference map, **Refresh from Classic**: compares an imported map with what Classic has now and opens a dialog (Added is inserted automatically; Removed, Changed and your deletions have checkboxes; edited-by-you items are marked); **Apply selected**, **Apply all I haven't edited** or Cancel, one undo step. Build > **Classic walls and cover** has Invisible wall (players only) and Cover (shoot-through); the Object tab sets a wall's shape; the Scene tab greys objects Classic hides and can list only those. Test aid: `--mm-classic`.
Undo/redo is Godot's own (Ctrl+Z / Ctrl+Shift+Z); every step restores the document (and prefab files) as they were.

## Notes
- Rotation values are Euler degrees about X, then Y, then Z in the SM1 frame (Z up); the editor shows Y up and converts (Frame.cs).
- Custom glTF assets are Y up; they are turned to the document frame like the core's bounds.
- Library thumbnails are rendered pictures cached under `%LOCALAPPDATA%\sm-reimpl\mapmaker\thumbs` (outside the repo).
- Library folder default: `%LOCALAPPDATA%\sm-reimpl\mapmaker\`.
- Agents: `godot.cmd --editor --path mapmaker --quit-after 200` (headless) loads the plugin; `godot.cmd --windowed --editor --path mapmaker -- --mm-selftest`
  runs the scripted self test on the hidden desktop and writes screenshots to `%LOCALAPPDATA%\sm-reimpl\cache\previews\mapmaker\`.
