# Ashfall Citadel graybox (Musou Mode's battlefield, G1)

Status: generator, server, content, tool and tests done (see Results); the client work is separate. On branch `musou/ashfall-graybox`. Owner's start: 2026-11-04 (agent-coordination `decisions/2026-11-04-ashfall-graybox-start.md`).
Merge gate: the owner playtests a branch build. Everything here is **ours** (placeholder demonstration) unless it says otherwise; every number is an estimate
for the owner's playtest and goes to the refine register.

Design sources: the owner's overhead reference image of 2026-11-04 (a 3 x 3 block city, not to scale), the living spec
(agent-coordination `specifications/musou-mode/ashfall-citadel.md`, change register MUS-001..014) and the kit standard
(https://claude.ai/artifact/7kAJ5yEvwQwVK6kt6LumbS: 4 m module, 6 m storey, measured movement values).

## What changes
- Musou Mode's battlefield becomes Ashfall Citadel, a level generated in code (primitive geometry only, no install files, no art).
  Manufactorum stays for classic PvP, untouched. The Stormbird hub and the Firing Range are composed beside it as today (`TfwMap.Compose`).
- The war's rules, nodes, enemies, commander and HUD are unchanged: the generated level supplies what Manufactorum supplied
  (collision, three capture points A/B/C, a start area for each side, spawn markers that seed the enemies' graph).
- Restore switch: the old composition stays reachable as map `tfw_war_m09` (Manufactorum + Stormbird + range, `TfwMap.Version` 7 behaviour);
  `tfw_war` is Ashfall. Both names need `--content musou`. The client follows the map name it is sent.
- No boss gate, no new progression rules (separate proposal later). The commander stands in the Chaos start area, which is now the stronghold's courtyard.

## Frame and units
Simulation frame: X east, Y north, Z up, metres. Map centre (0, 0), ground at z = 0. Kit standard: horizontal positions on the 4 m grid (1 m for trim,
0.5 m for rubble), every walkable top on a 0.5 m level, storeys 6 m (walkable at 0, 6, 12), structural walls 1 m thick (graybox: up to 4 m for ramparts).

## Footprint (MUS-005: larger than 400 x 400)
- Playable area 480 x 480 m: x, y in [-240, 240]. Sprint 9 m/s: spawn to B about 18 s, spawn to A or C about 26 s, A to C about 36 s.
- Boundary: a ruined skyline wall 4 m thick and **48 m** tall on the outside of the square (a full jump from a 16 m perch tops out near 34 m; nobody leaves).
- Backdrop (drawn, solid, unreachable): cathedral blocks outside the boundary along the west and east edges and the north corners, 20 x 20 x 40..70 m, as in the image's edges.

## Streets and blocks (the image's 3 x 3 grid)
- Perimeter road 16 m wide inside the boundary; two north-south and two east-west avenues 24 m wide.
- Columns: west x [-224, -104], centre x [-80, 80], east x [104, 224]. Rows: south y [-224, -104], middle y [-80, 80], north y [104, 224].
- Blocks are raised 0.25 m above the roads (a curb under the 0.4 m step: walkable, but reads as a kerb).
- Paved road from the spawn compound's north gate (0, -160) north to the plaza's south edge (0, -60), 12 m wide, flush (drawn as road).

## Blocks (west to east, north to south)
| Block | Image | Content (graybox) |
|---|---|---|
| NW [-224,-104] x [104,224] | monument on a stepped plinth | **Objective A** at (-164, 164). Plinth 16 x 16 x 3 m centred on A with a box-step stair 8 m wide up its south face (steps 0.25 m rise, 0.5 m tread). Broken obelisk 4 x 4 m on the plinth to 15 m (jump-only perch). Two rubble clusters and two low walls at 28-40 m from A (cover outside the zone, MUS-009). |
| N centre [-80,80] x [104,224] | stronghold and two L walls | **Boss stronghold** (Chaos start area: the courtyard). Two L-shaped walls 4 m thick, 12 m tall, walkable tops: west L from (-72, 112) east to (-12, 112) and from (-72, 112) north to (-72, 196); east L mirrored; the 24 m gap between them at x [-12, 12] is the gate (open; no trigger). Keep 64 x 48 m (x [-32, 32], y [164, 212]), 18 m tall, a 12 x 10 m entrance in its south face, solid inside for the graybox. Courtyard y [116, 160]: the Chaos start area centred (0, 138), 40 x 36 m. One ramp of box steps up the inner face of each L wall to its top (6 m wide). |
| NE [104,224] x [104,224] | ruined chapel facade | **Objective C** at (164, 150). Large ruin 24 x 16 m north of C (x [152, 176], y [172, 188]): a north facade 24 m wide, 18 m tall with two breaches 4 x 6 m at 6 m; east and west walls 12 m tall with ground breaches 4 x 4.5 m; first floor at 6 m over the north half with a box-step stair (4 m wide, two flights and a landing); roof slab at 12 m over the west third (jump-only). Rubble and low walls 28-40 m from C. |
| W middle [-224,-104] x [-80,80] | crater field | **Crater field** (MUS-010): about 12 craters in the ground heightfield, radius 6-14 m, depth 1.5-2.5 m, rims raised 0.5 m, slopes under 35 deg. Low walls 1.2 m and collapsed-wall pieces 2.5 m between craters; deeper cover than elsewhere. |
| between W middle and the plaza, (-92, 0) | small monument | Plinth 10 x 10 x 2 m with box steps on the east side; obelisk 3 x 3 m to 12 m (jump-only perch). Stands on the avenue's west half. |
| centre [-80,80] x [-80,80] | plaza with a fallen statue | **Objective B** at (0, 0). Paved plaza x, y [-60, 60] flush with the block (drawn as paving). Fallen statue: an oriented box 24 x 6 x 4 m turned 30 deg, centred (6, 4) (low cover inside the zone, as in the image). Six rubble piles 2-3 m tall at 32-48 m from B; the zone itself otherwise open (MUS-009). |
| on the east avenue, (92, 0) | triumphal arch | Two piers x [80, 84] and [100, 104], y [-3, 3], to 10 m; lintel x [80, 104] from 10 to 14 m, walkable top (jump-only perch). Passage north-south 16 m wide. |
| E middle [104,224] x [-80,80] | ruin cluster | Large two-storey ruin 24 x 24 m at (164, 10): walls 1 m thick with breaches on three sides (ground 4 x 4.5, upper 2 x 2.5 at 7 m sill), first floor at 6 m over the east half, stair (4 m wide), roof at 12 m over the north-east quarter (jump-only). Small ruin 12 x 12 x 12 m at (136, -48), one stair to a 6 m floor. |
| SW [-224,-104] x [-224,-104] | artillery wreck | Gun platform 12 x 12 x 3 m at (-164, -164) with box steps on its north side; barrel an oriented box 4 x 22 x 3 m, pitched 20 deg up toward the north-east from the platform's top (a walkable ramp to a perch at about 10 m). Wall fragments and rubble. |
| S centre [-80,80] x [-224,-104] | walled compound, road north | **Space Marine start area / spawn compound** x [-36, 36], y [-216, -160]: low ruined walls 2.5 m tall, 1 m thick, a 16 m gate in the north wall at x [-8, 8]. Marine start area centred (0, -188), 64 x 48 m. Short wall fragments (1.2 m) flanking the road north. |
| SE [104,224] x [-224,-104] | bunker ruin | Bunker 24 x 20 m at (164, -164): walls 3 m tall on three sides, a roofed part 12 x 20 m with its roof at 6 m (walkable) reached by box steps on its west side. Rubble. |

Capture points: radius 14 m, half height 8 m (a plinth top 3 m up still counts). B: radius 16 m. Spawn markers (the enemies' graph seeds, never solid):
each objective, the plaza's four corners, each block centre, the compound gate, the courtyard.

Rubble and fragments: a fixed list generated by a small integer hash (no `System.Random`, no floating-point accumulation across machines): oriented boxes,
yaw in 15 deg steps, heights either up to 0.35 m (walk over) or 0.9..2.5 m (blocking); never 0.4..0.9 m (the kit's dead band). Gaps between solids
either 0 or at least 1.2 m.

## Geometry rules for the generator
- Primitives: axis-aligned box, oriented box (yaw, optional pitch), box-step stair (risers 0.25 m), heightfield ground (2 m cells over the playable
  square, flat 0 except craters and rims). Every solid's triangles wind outward (as `TfwMap.Triangles`).
- Each primitive carries a kind for drawing: Ground, Road, Paving, Curb, Wall, Cover, Rubble, Floor, Stair, Perch (jump-only tops), Objective (a floor
  marker disc, not solid), Stronghold, Boundary, Backdrop.
- Collision: character mesh = every solid; weapon mesh = the same triangles. No kill volumes. No props.
- Budgets (kit standard, section 6): whole level under 200k collision triangles; report the count.
- Deterministic: the same triangles in the same order on every machine; the level hash is an FNV over the vertices and a generator version.

## Checks (automated, no bot gameplay)
1. Determinism: two builds give the same hash; the hash changes with the generator version.
2. Navigation (the enemies' graph builder, `NavGraph.Build` with the player's settings): on foot from the Marine start area, A, B, C, the courtyard, the
   plaza corners, and every ground-access upper floor (A plinth top, C first floor, E ruin first floor, small ruin floor, bunker roof, artillery barrel top, wall tops of the L walls) are one connected graph; the jump-only perches (obelisks, arch top, ruin roofs, keep top) are not reached on foot. Report the build time and memory.
3. No spawn marker, start area centre or capture centre inside a solid; each stands on walkable ground.
4. Triangle counts per kind and total under budget.
5. Isolation: PvP config hashes (faithful and extended) and the Manufactorum map hash unchanged; `tfw_war_m09` composes exactly as `TfwMap` version 7 did.
6. A top-down plan rendered from the generator (`smtool ashfall-plan --out <file.svg>`): every solid by kind, the grid, the objectives, start areas, Stormbird and range, a 50 m scale bar.

## Known gaps and risks
- The enemies' graph and flow field over about 5 times Manufactorum's area: build time, cache size and crowd cost are measured, not known.
- The Musou wire box (positions quantised within a box) grows with the map: check its precision.
- Square box steps and box rubble give a rough sense of the final ruins only.
- Lighting and sky: the client's provisional look (warm sun, ash-grey fog); no map effects or ambient sound on the generated level.

## Results (generator, server, content, tool and tests)
Generator `AshfallMap.Build` (version 1; `AshfallMap.Build.cs` holds the layout, `AshfallBuilder.cs` the primitives), all ours:
- **Triangles** (collision, character mesh = weapon mesh): total 16,940 of the 200,000 budget, in 569 parts. Per kind: Ground 10,208 (5 parts: four slabs and the crater heightfield, 2 m cells over the west-middle block, with skirts), Road 36, Paving 12,
  Curb 96, Wall 684, Cover 192, Rubble 2,556 (213 pieces), Floor 108, Stair 2,700 (225 box steps), Perch 60, Stronghold 48, Boundary 48, Backdrop 192; the three objective discs (not solid) add 72.
  `smtool ashfall-plan` prints the same table.
- **Enemies' graph** (`NavGraph.Build` as the server builds it, `NavSettings.From` of a normal `SimConfig`, the war's walking area: the level's markers and the Firing Range's seed): 800,782 nodes; **cold build about 56 s** on the
  development machine (Manufactorum's much smaller graph takes seconds), about 67 MiB retained, **cache file about 68 MiB**, loaded from the cache in 0.3 s. So a server or a client test aid builds it once per machine and cache folder; the
  first start of a new server copy (a fresh VM) waits about a minute. The test is marked `[Trait("Speed", "Slow")]` and takes about a minute.
- **Wire box** (`TfwUseWireBox`; 16 bits an axis over the graph's box widened by the Stormbird and the range): span about 660 x 583 x 57 m, so a step of 1.01 / 0.89 / 0.09 cm and a worst position error of about 0.5 cm. Well under 5 cm: no change needed.
- **Checks, all automated** (tests/SmNet.Tests/Extras/Musou/Ashfall): two builds bit-equal and the hash follows the version; every box winds outward; spawns, start areas and capture centres stand on walkable ground outside every solid; budgets;
  rubble 0.35 m or 0.9..2.5 m high with gaps 0 or at least 1.2 m (separated pairs measured on the footprints); the graph connects Marine start, A, B, C, the courtyard, the plaza corners, A's plinth, the C, E-ruin, small-ruin and bunker floors,
  the artillery platform and barrel top, both L walls' tops (ramps) and the small monument's plinth on foot, and reaches none of the six jump-only perches (A and small obelisks, arch top, keep top, C and E roofs); the classic config
  hashes are pinned; `TfwMap.WithHub(level, manufactorum: true)` keeps the old pinned hash and `tfw_war_m09` composes as version 7 did.

Deviations from the design, with reasons:
- **Statue** centred (12, 8), not (6, 4): at (6, 4) with a 30 degree turn the box covers B's centre.
- **A's centre is at z = 3** (the plinth top; its zone, half height 8, still covers the ground); the A disc is drawn on the ground, under the plinth. The other points and the start areas stand at the block surface (0.25 m; the plaza 0.27 m).
- **Obelisk** at the plinth's north-west part, not its middle, so A's centre is open ground.
- **Road and paving** are 2 cm overlays (kind Road, Paving) above the surface, not flush, so they never share a plane with the ground (no z-fighting; a 2 cm lip is walked over).
- **Stairs inset**: a stair's boxes stop 0.25 m inside its nominal width and its treads start 0.25 m early (the last step still meets the platform). The graph samples a 0.5 m grid, and where a box edge lies exactly on a grid line it found the
  ground under the edge instead of the step, cutting stairs off from the ground beside them (found by the graph check; the L-wall ramps and two others were unreachable). Edges between grid lines walk. The graph builder itself is unchanged.
- **Small ruin and bunker stairs**: the small ruin's stair runs outside along its south wall to a doorway whose sill is at 6 m (24 steps do not fit in 10 m inside); the bunker's runs inside its open west half to the roof.
- **C ruin**: two straight flights and a landing, then a connector slab to the north half's floor; the flights end where the floor begins (a thin slab over the climb would block it).
- **Walls and pieces on the grid**: ruins' breaches are made from box pieces (`HoleWall`), so a breach's sill is solid.
- **Rubble** is placed by a fixed integer hash on the 0.5 m grid with yaw in 15 degree steps, keeping 1.2 m from every other piece, structure and marker; a cluster may place fewer pieces than its count. Hand-placed low walls and collapsed pieces between the
  craters are kept only on flat ground: in version 1 two of the eleven (the low wall at (-110, 40) and the collapsed piece at (-176, 5)) fall on a rim or a slope and are dropped, so the crater field has five low walls and four collapsed pieces.
- **Hash** is FNV over the solid corners' bits salted with `AshfallMap.Version` (as designed); the map hash the handshake compares (`TfwMap.Version` 8) is the composed world's.
- **Restore switch**: `TfwMap.ManufactorumName` (`tfw_war_m09`, `--map tfw_war_m09`); `TfwMap.WithHub(level, manufactorum)` has no one-argument form any more (callers state which battlefield); `TfwMap.Compose` takes `manufactorum` as a last optional argument (default true, the old behaviour).
- `smtool ashfall-plan` draws every solid by kind (shade: higher is darker; F/P labels with the top height on upper floors and perches), the grid, objectives with radii, both start areas, the Stormbird and the range, a scale bar and a north arrow. Not yet looked at by a person.

Unfinished or open: the first cold start of a server builds the graph for about a minute (a shipped or pre-built cache is a later decision); the plan SVG and the whole layout await the owner's eyes; no nav jump links (`.navjump`) were measured.
