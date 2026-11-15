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
- **A second artifact class found by the same views: opaque sheets.** The vista cards (`BlendMode` 1, unlit) and Waste Management's `water` surface were drawn as flat opaque tan sheets because the material builder read only `BlendMode` 8. Now alpha cut-out (8, 10) and blend (1), and a see-through water. `smtool map-emissive --blend` lists them per map. The large flat tan sheets behind Shattered Bridge's towers and the pale violet slab overhead were a third cause: the engine's sky boxes (`seq_NN_sky`, shader `sky_ycocg_bloomcontrol`, YCoCg textures, no DiffuseMap; 133 m to 1.2 km boxes, present on every map but Waste Management) got the grey placeholder material and were drawn lit as level geometry. `MapView.BuildMesh` now skips surfaces whose material shader starts with `sky_` (the lighting's own sky shows; a confirming still is in the owner's media folder, `sky_fix`). On Manufactorum the box is placed today (`seq_07_sky` at the origin, about 540 m from its play area) and its drawing is kept exactly as before: the skip is per map (`MapDecorLists.SkipsSkyBoxes`, never `mp_tdm_09`), and `--old-sky-boxes` restores the old drawing everywhere. Not decoded: the YCoCg sky textures themselves. Waste Management's `Sky_Plane` (`diff_emis`, orange noise plane 132 m up) is drawn as before and unverified.
- Not fixed, noted: the whole of Shattered Bridge reads orange (sunset light and fog; unverified against the original), Waste Management's water is a provisional approximation, `BlendMode` 10 is read like 8 (a guess).

## 5. Per-map checklist

Checks run on a dedicated server (`--bind 127.0.0.1`) with two lean bot clients, one per side, 1-minute matches, both modes (summaries kept in the owner's temporary test folder): spawn rooms read (spawn spots per side, exit found), bots leave the rooms and path across the map (nav graph built per map), kills scored, points captured and scored in Seize Ground, the match ends and the restart clock counts. Rotation: `--maps mp_tdm_17,mp_tdm_01` with two bots, the bots rejoined by themselves on the second map. Visual QA: windowed free-camera tours (`--observe-tour`); stills are in the owner's media folder.

Legend: Y = run and passed; Y(a) = Annihilation only; "-" = not run (the PC was too loaded by other work to finish the planned runs; stated, not assumed). "Sets" = `MapDecorLists` set.

| Map (archive) | Modes enabled | Spawns / start rooms | Two-bot run: collision, traversal, scoring, round end (Annihilation / Seize) | Objectives (Seize) | Visual QA | Salmon / yellow check |
|---|---|---|---|---|---|---|
| Shattered Bridge (`mp_tdm_17`) | both | 45 spots per side, exits found | Y / Y with a windowed full-load bot client (marines) and a headless bot client (chaos), 2-minute matches on protocol 79: both sides spawn, move and fight, kills scored, Seize points A, B and C captured and scored, match ended (Annihilation 1-2, Seize 182-262), the restart clock ran and a new match began | 3 points captured and scored | player-view and third-person bot stills, spawn rooms, the bridge, each control point and overviews, plus the Host panel's MAP row with Shattered Bridge (stills in the owner's media folder, `final`); after the sky fix | no emissive board kit, lamps only; the orange look is the map's own lighting data, see below |
| Waste Management (`mp_tdm_01`) | both | 13 per side | Y / Y | 3 points | baseline and station views | salmon pipes, boards, keypads found (same as Manufactorum); set added; after-fix stills not captured (-) |
| Basilica (`mp_tdm_11`) | both | 27 per side | Y / Y | 4 points | baseline tour, console views | dim red console lamps only; no set |
| Hab Center (`mp_tdm_18`) | both | 25 per side | Y / Y | 4 points | baseline tour, panel views | amber window lamps; no set |
| Habs Ablaze (`mp_dlc1_map1`) | both | 55 / 45 | Y / Y | 4 points | final tour (night, blue) | lamps only; no set |
| Station Tertius (`mp_dlc1_map2`) | both | from data (21 markers) | Y(a) / - | - | - | kit consoles in audit; set added, no view (-) |
| Chem Refinery (`mp_dlc2_map1`) | both | from data | - / - | - | - | kit boards in audit; set added, no view (-) |
| Aquila Canyon (`mp_dlc2_map2`) | both | 21 / 45 | server start only | - | baseline tour (desert, sepia) | one `console_dif`; no set |
| Desolation (`mp_dlc2_map3`) | both | 21 / 21 | server start only | - | baseline partial | kit boards (orange tint) in audit; set added, no view (-) |
| Dome Mechanicus (`mp_dlc2_map4`) | both | from data | - / - | - | - | kit boards in audit; set added, no view (-) |
| Manufactorum (`mp_tdm_09`) | both (unchanged) | 15 per side | Y / Y with two headless bots on protocol 79 (Annihilation: a kill scored, match ended; Seize: points A and C captured, match ended) | - | final-build view: no salmon | set unchanged (tests pin it); blend changes do not touch it (it has only blend 8 and 255) |
| `mp_dlc3_map1..4` | none | no data in the install | - | - | - | - |

Rotation: `--maps mp_tdm_17,mp_tdm_01`, two bots: match 1 ended by the time limit, the server exited, the bots rejoined on `mp_tdm_01` and played on (Y).

Not verified: the Host panel row in a window (built, unit-tested pieces only); real clients (every run used lean bots plus one windowed observer); the "after" stills of the kit maps; Seize Ground and the second client on Station Tertius to Dome Mechanicus; the Manufactorum bot re-test; any comparison against the original game's own images (none available here).


### Shattered Bridge's orange look is authored

`smtool maps-audit auto --lighting mp_tdm_17` reads the map's `l_lights` layer: sun colour 1.00/0.75/0.46 at power 4.0 (Manufactorum: 1.00/0.86/0.68 at 3.0), ambient up 0.64/0.46/0.36 and low 0.35/0.35/0.54, and a fog gradient to 0.58/0.48/0.38 at 40 % over 200 m (Manufactorum's far fog is 0.75/0.64/0.54 at 27 %). So the warm orange sun, sky-side ambient and tan fog are in the data; our rendering adds nothing orange of its own. How the engine tonemaps and blends that fog is not known (the fog reading is ours, discovery.md "Fog re-read").

## 6. Performance (classic-maps-perf, 2026-11-16)

**What was measured, and how.** Every map the same way, in Annihilation, by `tools\classic-maps-perf.ps1` (table: `tools\classic-maps-perf-table.py`): one Release server on `127.0.0.1` with 8 + 8 headless bots (a full lobby), and one windowed observer client (debug-configuration build, hidden desktop, vsync off) with the free camera walking the map's places 5 s each (both spawn rooms, every control point, three long open views), `--perf-probe --perf-tour --frame-profile`. The table uses the first 5 s window of each place after the first (settle) place: 8 windows on maps with 3 points, 9 on maps with 4. Seize Ground draws the same geometry, so it was not run separately. **Measured, one run per map, not repeated; the machine was not quiet.** Other agents ran about 58 Godot processes (servers, bots, clients) beside these runs: the CPU read 52 to 100 percent and the run queue 0 to 54 before each client run (each run's `summary.txt` has its own line). Frame times therefore carry a scheduling noise of about 3 ms and spikes of 100 to 150 ms in nearly every run (Manufactorum's too); the renderer's own CPU and GPU times and the draw counts are steadier and are the better comparison. "Worst window p99" is the worst 1% low of any 5 s window; "place N" counts the settle window as 1. (The first version of the table also counted the windows the client kept measuring at the tour's last place; those are dropped here.)

| Map | mean frame ms | worst place mean | worst window p99 | render CPU / GPU ms | draw calls mean/max | objects max | lights (shadowed) | VRAM MB |
|---|---|---|---|---|---|---|---|---|
| Manufactorum (baseline) (`mp_tdm_09`) | 11.39 | 15.50 (place 2) | 23.0 | 3.1 / 1.9 | 2238/2702 | 3624 | 129 (1) | 664 |
| Waste Management (`mp_tdm_01`) | 14.41 | 15.70 (place 6) | 23.8 | 3.2 / 1.1 | 1289/1544 | 2189 | 156 (7) | 540 |
| Basilica (`mp_tdm_11`) | 12.04 | 14.60 (place 7) | 22.2 | 3.4 / 2.0 | 2968/3410 | 4250 | 190 (1) | 675 |
| Shattered Bridge (`mp_tdm_17`) | 15.75 | 17.70 (place 6) | 22.5 | 3.9 / 1.8 | 2144/2548 | 3362 | 105 (1) | 658 |
| Hab Center (`mp_tdm_18`) | 16.19 | 18.40 (place 3) | 23.6 | 4.7 / 1.4 | 2203/2694 | 3566 | 142 (2) | 578 |
| Habs Ablaze (`mp_dlc1_map1`) | 10.43 | 11.60 (place 4) | 14.3 | 2.4 / 1.7 | 2112/2365 | 3136 | 7 (1) | 622 |
| Station Tertius (`mp_dlc1_map2`) | 13.96 | 16.60 (place 2) | 23.2 | 3.2 / 1.8 | 1761/2167 | 3046 | 7 (1) | 636 |
| Chem Refinery (`mp_dlc2_map1`) | 16.88 | 18.50 (place 9) | 116.7 | 3.8 / 1.0 | 2041/2307 | 3017 | 7 (1) | 517 |
| Aquila Canyon (`mp_dlc2_map2`) | 9.19 | 10.60 (place 2) | 14.6 | 2.0 / 1.0 | 1269/1581 | 2397 | 7 (1) | 545 |
| Desolation (`mp_dlc2_map3`) | 9.35 | 10.60 (place 2) | 14.1 | 2.3 / 1.3 | 1815/2079 | 2792 | 47 (1) | 545 |
| Dome Mechanicus (`mp_dlc2_map4`) | 12.36 | 15.50 (place 2) | 22.1 | 2.5 / 1.2 | 1383/1720 | 2382 | 7 (1) | 529 |

Client working set 1.6 to 1.8 GB on every map (a debug build with the other Godot processes beside it). Server, 8 v 8 + 1 observer, 17 clients (`SM_TICK_STATS=5`, about 100 s of play per map): tick mean 0.64 to 1.06 ms (p99 0.99 to 2.34 ms) on every map, Manufactorum 0.64 ms; no skipped ticks; 28 to 30 KB/s per client. The slowest server map is Shattered Bridge (mean 1.06 ms, p99 2.34 ms), about 3 percent of the 33 ms tick. Server working set 133 to 144 MB after the map read, and map read up in 4 s: Manufactorum only (the others were not timed one by one). Client load time and the nav graph were not measured (the observer builds no graph; bots cache theirs).

**Reading.** No new map is clearly worse than Manufactorum on what the renderer reports. Worst remaining: **Hab Center** (render CPU 4.7 ms against Manufactorum's 3.1, 142 lights, about 2,200 draw calls) and **Shattered Bridge** (3.9 ms, 105 lights); Basilica has the most draw calls (2968 mean, 190 lights) but a cheaper frame. Chem Refinery has the highest mean frame time (16.88 ms) with a cheap render (3.8 ms CPU), so that figure is load noise rather than the map. The likely cause, the same on every map and on Manufactorum (read from the code, not isolated by an experiment): the map is drawn from the nearest LOD only, with no draw distance, and one MultiMesh per mesh whose culling box covers every placement, so all of a mesh's placements are drawn whenever one is in view. Few placed lights cast shadows (at most 6, `MaxShadowedLights`; 1 to 7 here) and all fade out at 70 m; the nav graph is built by bots only and cached on disk; the server's tick cost is flat across the maps.

**One fix tried and dropped.** Splitting each mesh's placements into 40 m culling cells on the new maps (same pixels) roughly doubled the draw calls (Waste Management 1289 to 2009, Habs Ablaze 2112 to 4093) and gave no consistent frame gain (mean of the ten new maps 13.1 ms before, 15.0 ms after; single maps moved both ways by more than the noise). It is not in the branch. **Held for the owner's choice** (each changes what a map shows): a draw distance or LOD switch for far placements, and fewer placed lights on Hab Center and Basilica. No map's drawing was changed. Not measured: Seize Ground, a repeat of any run on a quiet machine, a human client's load time, the Compatibility renderer, shader-compile spikes (the first view of most maps shows one worst frame of 100 to 150 ms; Manufactorum too).
