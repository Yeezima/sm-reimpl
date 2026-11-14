# Classic PvP: every original competitive map (classic-maps-all, 2026-11-15)

Owner's brief (2026-11-15): every remaining original competitive multiplayer map in Classic PvP, Annihilation and Seize Ground only, starting from the
corrected pipeline so the salmon-red and yellow import artifacts removed from Manufactorum do not reach the new maps. Manufactorum, Musou Mode and the
loading screens stay as they are. Extracted assets stay out of Git and out of release packages (everything is read from the player's own install at runtime;
`smtool maps-audit auto` prints the data below).

## 1. Roster, confirmed against the installed Steam build

The install's front-end data (`data/packages/multiplayer_map.oppc`, one entry per map: `map_name`, `display_name` as a locale id, `map_objectives`) and its English
locale give the official names. 14 competitive entries (a `mp_*` map offering `teamdeathmatch` or `domination`); co-op arenas (`cp_*`, `coop_grid_test`) are excluded.
`MapCatalog` (src/SmContent/MapCatalog.cs) reads this list at runtime, so the lobby shows the install's own names.

| Archive | Official name | Region archive | Game types in the data | Status |
|---|---|---|---|---|
| `mp_tdm_09` | Manufactorum | `__wrld` | teamdeathmatch, domination (+ ctf, koth, dreadnought) | unchanged (alpha map) |
| `mp_tdm_17` | Shattered Bridge | `__r000_main` | same | enabled (first end-to-end import) |
| `mp_tdm_01` | Waste Management | `__r000_main` | same | enabled |
| `mp_tdm_11` | Basilica | `__r000_main` | same | enabled |
| `mp_tdm_18` | Hab Center | `__wrld` | same | enabled |
| `mp_dlc1_map1` | Habs Ablaze | `__wrld` | same | enabled |
| `mp_dlc1_map2` | Station Tertius | `__wrld` | same | enabled |
| `mp_dlc2_map1` | Chem Refinery | `__wrld` | same | enabled |
| `mp_dlc2_map2` | Aquila Canyon | `__wrld` | same | enabled |
| `mp_dlc2_map3` | Desolation | `__wrld` | same | enabled |
| `mp_dlc2_map4` | Dome Mechanicus | `__wrld` | same | enabled |
| `mp_dlc3_map1..4` | none (placeholder ids `dlc3_map1`..`4`) | none | map1: coop, koth, dreadnought; map2-4: tdm, domination, koth, dreadnought | **not enabled: no authored data** |

The brief's ten names are all present (Waste Management, Shattered Bridge, Hab Center, Basilica, Habs Ablaze, Station Tertius, Aquila Canyon, Desolation, Dome
Mechanicus, Chem Refinery). The install confirms no other named competitive map. The four `mp_dlc3_*` entries are 5.9 KB stub archives holding a `.world` and a
region description only (no collision, no layers, no spawn or point markers) and their display names are unset locale ids (`dlc3_map2`): the DLC3 content is not in the
install. They are reported, not guessed, and `MapEntry.Playable` is false for them (`MapContent.Available` finds no region archive).

Every enabled map carries, in its region and mission layers: the character and weapon collision layers, `*_spawn_*` markers (16 to 21), two start areas
(`mkr_start_marine`, `mkr_start_ork`), 6 to 8 spawn-room blockers, a kill region (all but `mp_dlc1_map1`) and 3 to 4 control points (`mkr_objective_N`), so
Annihilation and Seize Ground are both supported by the data on every playable map. Capture the Flag, Capture and Control and Dreadnought Assault are in the data
too and are not implemented in this pass.

## 2. The Manufactorum process that is reused

Collision, spawns, start areas, blockers, kill regions and control points are read by the generic `MapContent.Load` (no map is named in it; milestones M7, M16, M18,
M22). Drawing is the generic `MapScene` / `MapView` / `MapMaterials` / `MapLightingView`. Manufactorum-specific parts: `MapDecorLists` (what is not drawn), the bot cover
anchors (`BotCover`) and the Musou map. Per map the new work is therefore: confirm the data loads, serve it, draw it, then look for the artifact causes below.

### Cause of the salmon-red, olive and yellow artifacts (traced through 210e830c, fbe0c4b0, 7c0297f2)

Not a texture or hue error: the placed objects whose materials have an `EmissiveMap` are drawn by `MapMaterials` as emission = `EmissiveColor` x emissive texture x 1.5,
flat and unlit. Manufactorum's materials carry authored tints (`tech_stations` 0.55/0.14/0.12 salmon, `backPanel` 0.27/0.29/0.10 olive, `yellow` 0.43/0.44/0.07,
`green`), so the wall stations, pipe runs and tanks drew as bright flat salmon and olive shapes. The original's own shading of these maps is not known. The fix was
neither a hue shift nor a shader change: the owner asked for the offending surfaces not to be drawn, by object definition and material name
(`MapDecorLists.Always`, `Near`, `Surfaces`; the gate is `MapDecorLists.Hidden` / `HidesSurface`, drawn in `MapView`), found with `smtool map-emissive`, which
lists every emissive material of a map with its tint, whether it is salmon, and the meshes and placements that use it. Red lamps (`mini_env_light_red`), blue,
cyan, white and amber lamps are legitimate and stay.

Two facts decide the treatment of the new maps: the same material names recur (`tech_stations`, `backPanel`, `yellow`, `green`, `mini_env_light_*`) but their
`EmissiveColor` differs per map, and the lists name Manufactorum's own object definitions. So nothing is copied blindly: each map's emissive audit and in-game
views decide. Findings are in section 4.

## 3. Pipeline stages and where the work is

| Stage | Where | New for this pass |
|---|---|---|
| Roster | `MapCatalog` (install's `multiplayer_map`) | new |
| Collision, spawns, points, blockers | `MapContent.Load` | none (generic); per-map numbers audited with `smtool maps-audit` |
| Visual scene | `MapScene`, `MapView`, `MapMaterials` | none (generic); per-map artifact audit with `smtool map-emissive --map <m> --all` |
| Artifact fixes | `MapDecorLists` | made per-map (a table keyed by map) |
| Server | `server.cmd --map m` / `--maps a,b,c` | rotation (`--maps`) |
| Lobby | Host panel | a MAP row (every playable map, or ROTATION) |
| Client | `NetworkView` | follows a rotation: reconnects after the match end |

## 4. Findings that decide the artifact work

- **Waste Management (`mp_tdm_01`) is Manufactorum's art kit.** Its baseline views drew the same salmon pipe runs, boards and keypads (stills in the owner's media folder, `classic-maps\mp_tdm_01\stations_baseline`). The audit lists the same definitions and tints (`tech_stations` 0.55/0.14/0.12, `backPanel` 0.27/0.29/0.10). It gets a set (`MapDecorLists.WasteManagementSet`) built from its own audit: `backpanel_a`, `tech_panel_c/d`, `tech_panel_pipe_a/b`, the salmon surface of `tech_panel_pipe_g`, `tech_interface_a`'s salmon and olive surfaces and `Pipe_Piece_A`'s `console_dif`.
- **Station Tertius, Chem Refinery, Desolation, Dome Mechanicus** place the same kit objects (`backpanel_a/b`, `handle_bar_a`, `tech_panel_a/c/d`, `tech_interface_a`, `tank_a`, `Pipe_detail_A`); on Desolation the `tech_stations` tint is orange (0.94/0.43/0.11), the "yellow" case. Each has its own set listing only what its audit found.
- **Hab Center, Basilica, Habs Ablaze, Shattered Bridge, Aquila Canyon: no set.** Hab Center's `tech_stations` (0.94/0.43/0.11) sits on `Hab_wall_panel_A/Fan`, the small amber window lamps (views show small lit windows, not boards); Basilica's is dark red (0.28/0.02/0.01) on console indicator lamps; Habs Ablaze has only red, blue and amber lamp materials; Shattered Bridge only lamps; Aquila Canyon one `console_dif` on one wall detail. These are legitimate lamps and markings and stay. Red lamps (`mini_env_light_red`) are pure red lamps everywhere and stay.
- **A second artifact class found by the same views: opaque sheets.** The vista cards (`BlendMode` 1, unlit) and Waste Management's `water` surface were drawn as flat opaque tan sheets because the material builder read only `BlendMode` 8. Now alpha cut-out (8, 10) and blend (1), and a see-through water. `smtool map-emissive --blend` lists them per map. The large flat tan sheets behind Shattered Bridge's towers and the pale violet slab overhead were a third cause: the engine's sky boxes (`seq_NN_sky`, shader `sky_ycocg_bloomcontrol`, YCoCg textures, no DiffuseMap; 133 m to 1.2 km boxes, present on every map but Waste Management) got the grey placeholder material and were drawn lit as level geometry. `MapView.BuildMesh` now skips surfaces whose material shader starts with `sky_` (the lighting's own sky shows; a confirming still is in the owner's media folder, `sky_fix`). Manufactorum's box sits at the origin, far from its play area, so its look does not change. Not decoded: the YCoCg sky textures themselves. Waste Management's `Sky_Plane` (`diff_emis`, orange noise plane 132 m up) is drawn as before and unverified.
- Not fixed, noted: the whole of Shattered Bridge reads orange (sunset light and fog; unverified against the original), Waste Management's water is a provisional approximation, `BlendMode` 10 is read like 8 (a guess).

## 5. Per-map checklist

Checks run on a dedicated server (`--bind 127.0.0.1`) with two lean bot clients, one per side, 1-minute matches, both modes (summaries kept in the owner's temporary test folder): spawn rooms read (spawn spots per side, exit found), bots leave the rooms and path across the map (nav graph built per map), kills scored, points captured and scored in Seize Ground, the match ends and the restart clock counts. Rotation: `--maps mp_tdm_17,mp_tdm_01` with two bots, the bots rejoined by themselves on the second map. Visual QA: windowed free-camera tours (`--observe-tour`); stills are in the owner's media folder.

Legend: Y = run and passed; Y(a) = Annihilation only; "-" = not run (the PC was too loaded by other work to finish the planned runs; stated, not assumed). "Sets" = `MapDecorLists` set.

| Map (archive) | Modes enabled | Spawns / start rooms | Two-bot run: collision, traversal, scoring, round end (Annihilation / Seize) | Objectives (Seize) | Visual QA | Salmon / yellow check |
|---|---|---|---|---|---|---|
| Shattered Bridge (`mp_tdm_17`) | both | 45 spots per side, exits found | Y / Y (kills scored, captures, match ended) | 3 points captured, scored | tours before and after the material fix; warm orange light and tan canyon-wall slabs, not emissive | no emissive board kit; lamps only; no set |
| Waste Management (`mp_tdm_01`) | both | 13 per side | Y / Y | 3 points | baseline and station views | salmon pipes, boards, keypads found (same as Manufactorum); set added; after-fix stills not captured (-) |
| Basilica (`mp_tdm_11`) | both | 27 per side | Y / Y | 4 points | baseline tour, console views | dim red console lamps only; no set |
| Hab Center (`mp_tdm_18`) | both | 25 per side | Y / Y | 4 points | baseline tour, panel views | amber window lamps; no set |
| Habs Ablaze (`mp_dlc1_map1`) | both | 55 / 45 | Y / Y | 4 points | final tour (night, blue) | lamps only; no set |
| Station Tertius (`mp_dlc1_map2`) | both | from data (21 markers) | Y(a) / - | - | - | kit consoles in audit; set added, no view (-) |
| Chem Refinery (`mp_dlc2_map1`) | both | from data | - / - | - | - | kit boards in audit; set added, no view (-) |
| Aquila Canyon (`mp_dlc2_map2`) | both | 21 / 45 | server start only | - | baseline tour (desert, sepia) | one `console_dif`; no set |
| Desolation (`mp_dlc2_map3`) | both | 21 / 21 | server start only | - | baseline partial | kit boards (orange tint) in audit; set added, no view (-) |
| Dome Mechanicus (`mp_dlc2_map4`) | both | from data | - / - | - | - | kit boards in audit; set added, no view (-) |
| Manufactorum (`mp_tdm_09`) | both (unchanged) | 15 per side | server start only | - | final-build view: no salmon | set unchanged (tests pin it); blend changes do not touch it (it has only blend 8 and 255) |
| `mp_dlc3_map1..4` | none | no data in the install | - | - | - | - |

Rotation: `--maps mp_tdm_17,mp_tdm_01`, two bots: match 1 ended by the time limit, the server exited, the bots rejoined on `mp_tdm_01` and played on (Y).

Not verified: the Host panel row in a window (built, unit-tested pieces only); real clients (every run used lean bots plus one windowed observer); the "after" stills of the kit maps; Seize Ground and the second client on Station Tertius to Dome Mechanicus; the Manufactorum bot re-test; any comparison against the original game's own images (none available here).

