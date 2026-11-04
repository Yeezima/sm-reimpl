# Ashfall Citadel graybox (Musou Mode's battlefield, G1)

Status: done on the branch (generator, server, content, client view, tool, tests; see Results), awaiting the owner's playtest. On branch `musou/ashfall-graybox`. Owner's start: 2026-11-04 (agent-coordination `decisions/2026-11-04-ashfall-graybox-start.md`).
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
Generator `AshfallMap.Build` (version 1 here; see Pass 2 below for version 2; `AshfallMap.Build.cs` holds the layout, `AshfallBuilder.cs` the primitives), all ours:
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

## Integration and review (lead, 2026-11-04)
- Live load check (server `--bind 127.0.0.1 --content musou`, one headless client, no bots, no input): the client generated the level in 32 ms and
  drew 569 parts / 17,012 triangles in 14 ms; map load 581 ms; client and server map hash equal (DDC3549A201A03D6); the war started on Ashfall
  (enemies' graph built in 9.7 s on the server: 800,782 cells, 21 seeds). Level hash v1 4D0658D87AF67B7B (pinned in the tests).
- Independent review: no must-fix. Applied: perch test has a positive control (each perch point stands on a solid top); Ashfall's level hash is
  pinned; the perimeter road's four corners seed the enemies' graph and the war's bounds (before, x > 210 and y > 194 had no nodes and orders
  there were clamped); the score pages name the battlefield (ASHFALL CITADEL, or MANUFACTORUM on `tfw_war_m09`).
- Open: Ashfall and Manufactorum runs may share best-score records (`TfwMapScore` carries no map id; unverified); the cold graph build on a new
  server copy (about 10 to 60 s by machine) delays the war's start once; both war maps share the graph cache's base name; the faithful build's
  `smtool` help lists `ashfall-plan` though the command exists only with added content.

## Pass 2 (owner's playtest notes, 2026-11-05)
The owner's notes (verbatim): "currently there are not enough ruined structures around. simulate them with grey box. add more low walls around the 'roads' of the map. remove the box from the B center point. add more buildings that are many stories high",
"more random shooting platforms that are just small plateus with slopes that are less than 1 storey high", "add stairs all around the A point box" and "add more cover and structures in particular in the areas marked in red".
`AshfallMap.Version` is now **2** (level hash `0x875D9199C67B1894`, pinned in the tests; version 1 was `0x4D0658D87AF67B7B`). All of it is ours (placeholder demonstration); the client is unchanged (it draws the existing kinds).
New code: `AshfallMap.Structures.cs` (the layouts), `AshfallPlacer.cs` (claims, banned places, checks), `AshfallFeatures.cs` (`AshfallMap.BuildWithFeatures()` returns the level and what pass 2 placed, for the checks; `Build()` is unchanged).

What changed:
- **B**: the fallen statue is gone; B's centre and its whole zone are clear (the rubble piles 32-48 m out stay).
- **A's plinth** has a box-step stair (8 m wide, 0.25 m risers, 0.5 m treads, 3 m) on all four sides (north, west and east added to the south one); the obelisk stays as the jump-only perch. A's zone holds no new cover; A's nearby rubble and low walls needed no move (the stairs are claims, so the rubble keeps 1.2 m from them).
- **Ruins** (grey-box shells): 152 placed by rule across the nine blocks (the stronghold block excepted): 1-storey shells (8-16 x 8-12 m), 2-storey shells (12-24 x 12-16 m; those 20 m or longer have a straight 4 m stair to a partial first floor, the others a jump-only partial floor), facade fragments with buttresses,
  freestanding broken walls and L corners. Walls are 1 m thick in 4 m columns with broken tops (0.5 m steps, 1.5-6 m per storey, never in the 0.4-0.9 m band), windows (1 m sill, 3.5 m opening) and doorways 4 m wide x 4.5 m tall; one corner of some shells has collapsed.
- **Tall buildings** (3-5 storeys, 18-30 m): 12, footprints 12 x 12 to 16 x 24 m: NW, NE and SW corner towers (5 storeys), one by A (4), one by C (3), an east tower (4), two on the south loop (3 and 4), a plaza-west tower (3), an east flank shell (3), the shell south-east of the compound (3) and the SE corner shell (4).
  8 have a walkable stair core (a dogleg of 4 m wide flights of 3 m with a 2 m pad, per storey; the floor plates have a staggered 4 x 4 hole and are open over the core) reaching every floor at 6 m steps; the other 4 are shells whose floors (kind Perch) are jump-only. Every storey has outward windows and the ground floor two doorways.
  The highest walkable floor is 24 m (the 5-storey towers; their walls reach 30 m), at least 24 m from the boundary, so a full jump from it peaks near 41.5 m under the boundary's 48 m.
- **Shooting platforms**: 26 plateaus 6-12 m square with tops at 1.5-5.5 m (0.5 m steps, always under one storey), each with one or two box-step ramps (4 m wide, 26.6 deg, solid down to the ground), a Wall-kind body under a Floor-kind top and a 1 m Cover parapet along part of the edge on about half.
  Chosen in hash order from the 4 m grid, red zones first, at least 36 m apart, with the same exclusions as the ruins.
- **Low walls along the roads**: 310 curb segments (3,616 m in all; 8-16 m long, 0.5 or 1.0 m thick, 1.0 or 1.2 m tall, kind Cover) along every block edge and the boundary wall's foot, with openings of 4-8 m between (at least one every 24 m), and 72 staggered cover walls (5.5 m, across half an avenue beside the 8 m lane, alternating sides)
  every 24 m along both avenues in each direction (every 12 m inside the red zones). A segment that would touch anything or stand on a slope is left out, which only widens an opening.
- **Rubble**: the clusters on the avenues moved to the avenues' edges (the lane stays free) and 29 more clusters went into the red zones.

Placement rules (`AshfallPlacer`): candidates on the 4 m grid in a fixed hash order (`AshfallBuilder.Mix`, no `System.Random`); a candidate is accepted when its footprint stays at least 8 m from every claim (everything hand-built and everything placed before), outside the banned places and on level ground (a road or a block's top, never a crater or a rim).
Banned: the 8 m lane (plus 1.2 m) of every avenue and perimeter centreline, the paved road and the plaza's axes, the stronghold gate's approach, both start areas, the stronghold block's interior, each capture zone plus 6 m, the plaza's open centre (B's zone plus 6 m), the spawn markers plus 6 m.
Ruins stay 4 m inside their block; platforms stay inside +-222 m. The tall buildings are hand-listed (a wrong entry fails the build with the reason). Kit rules hold: structures on the 4 m grid, tops on 0.5 m levels, gaps 0 or at least 1.2 m (curb walls and rubble keep 1.2 m from everything).

Red zones (`AshfallMap.PriorityZones`, the lead's reading of the owner's marked screenshot): ruins get a second pass of their own inside each, platforms are chosen there first, the avenue cover is twice as dense and rubble clusters were added. Per zone:
1. South loop (y -120..-60, the whole width): a second ruin pass on the south blocks' north edge and the middle row's south edge, two tall buildings (a 3-storey shell at (-148, -128), a 4-storey tower with a core at (132, -128)), staggered cover every 12 m along the south avenue, platforms first, rubble clusters on both edges.
2. Crater field south-west (x -220..-160, y -90..0): ruins, platforms and rubble on the flat ground between the craters (no building stands on a crater or rim: a wall on a slope would hang).
3. West avenue by the plaza's south-west corner (x -100..-60, y -70..-30): staggered cover every 12 m, ruins in the plaza block's south-west quarter, rubble, a platform first.
4. East avenue south of the arch (x 80..130, y -70..-20): the same.
5. East side of the E block (x 190..236, y -70..0): ruins, platforms, rubble; the east tower (y 48..72) and the SE corner shell frame it.
6. North avenue south of A's block and east of the stronghold (y 80..104): staggered cover every 12 m, small ruins along the edges, rubble; the gate approach x -12..12 stays banned.

Checks added (all automated): the new hash; no solid in any capture cylinder (A: only its plinth, four stairs and obelisk); ruins and tall buildings out of the zones' 6 m ring, the start areas, the plaza's open centre and the perimeter road, with 8 m between any two; each avenue's and the perimeter's 8 m corridor free of solids
(exceptions: version 1's monument and arch); no wall or cover top in the 0.4-0.9 m band; platform heights; tall buildings 3-5 storeys, footprints 12 x 12 to 24 x 16, 6 m x storeys high, 10 m clear of the boundary; A's four stairs. The slow graph test walks to every platform top (26), every ruin floor with a stair (5), every floor of the 8 cored buildings (25) and from each of A's four stair feet to the plinth top,
and confirms with the downward-ray control that the shell towers' floors are solid tops the graph does not reach on foot.

Results: 60,848 collision triangles of the 200,000 budget in 4,228 parts (was 16,940 in 569). Per kind: Ground 10,208, Road 36, Paving 12, Curb 96, Wall 20,772, Cover 4,896, Rubble 2,904, Floor 1,572, Stair 19,632, Perch 432, Stronghold 48, Boundary 48, Backdrop 192.
Enemies' graph: 902,105 nodes (was 800,782); cold build 92 to 138 s on the development machine (the longer figure with the other tests running), about 107 MiB retained, cache file about 76 MiB, loaded from the cache in 0.4 s. The wire box is 696 x 651 x 57 m: a worst position error of about 0.5 cm, unchanged.
Per-block share of the ground covered by ruin and tower footprints: 10 to 15 % in the eight built blocks (walls are thin, so a block reads denser than the share), 2 % in the crater field.

Deviations: the tall buildings' stair core is 8 m long (flight, 2 m pad, flight returning in the other lane): a first version with a landing at the far end could not be walked, as its second flight began on the wall-side edge that the graph's grid cannot step onto. The SW corner tower stands at y -176..-160, not in the corner,
because the block-centre spawn marker (-196, -196) keeps 10 m clear. Platforms are accepted wherever the rules pass (blocks, avenue edges, junction quarters), not on a fixed list. Few ruins have a walkable floor (5), as only shells 20 m or longer have the room for the stair; the rest keep jump-only partial floors.
Unfinished: the plan SVG was not looked at by a person; the red zones follow the lead's reading of the marked screenshot; no live check (the lead runs it).

### Pass 2: integration and review (lead, 2026-11-05)
- Live load check (server `--bind 127.0.0.1 --content musou`, one headless client, no bots): 4,228 parts, 60,920 triangles drawn; map load 1.2 s;
  client and server hash equal; the war started. The enemies' graph built in **18.5 s** on the server (902,105 cells, 25 seeds); the 92-138 s in the
  pass-2 notes above were measured inside the parallel test run.
- Independent review: no must-fix. Applied (version 3, level hash 0xBB4D50AF4614FD39): the boundary-foot walls butt against the boundary (they left a
  1 m slot that trapped the capsule); the boundary is 64 m (`AshfallMap.BoundaryHeight`; the 5-storey towers' 30 m wall columns plus a full jump left
  0.5 m under 48 m), with a test that the highest top plus a full jump and 4 m stays below it; the placers' sorts have a total order (Key, X, Y).
- Open: the cold graph build on a new server copy after each layout change (about 20 s here); ruins' jump-only floors have no reachability check of their own.
