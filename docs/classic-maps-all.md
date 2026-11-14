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

## 4. Per-map results

(filled in as each map is verified; see the checklist below)
