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
| N centre [-80,80] x [104,224] | stronghold and two L walls (reworked in pass 3: see below) | **Boss stronghold** (Chaos start area: the courtyard). Two L-shaped walls 4 m thick, 12 m tall, walkable tops: west L from (-72, 112) east to (-12, 112) and from (-72, 112) north to (-72, 196); east L mirrored; the 24 m gap between them at x [-12, 12] is the gate (open; no trigger). Keep 64 x 48 m (x [-32, 32], y [164, 212]), 18 m tall, a 12 x 10 m entrance in its south face, solid inside for the graybox. Courtyard y [116, 160]: the Chaos start area centred (0, 138), 40 x 36 m. One ramp of box steps up the inner face of each L wall to its top (6 m wide). |
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

Results (version 2; 2b below): 60,848 collision triangles of the 200,000 budget in 4,228 parts (was 16,940 in 569). Per kind: Ground 10,208, Road 36, Paving 12, Curb 96, Wall 20,772, Cover 4,896, Rubble 2,904, Floor 1,572, Stair 19,632, Perch 432, Stronghold 48, Boundary 48, Backdrop 192.
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

### Pass 2b (owner's second notes, 2026-11-05)
The owner's notes (verbatim): "add these things to the map" (two marked screenshots) and "yes the east tower should get those things but vary it in some way". `AshfallMap.Version` is now **4** (level hash `0xA07D05D38EB46CF2`, pinned; version 3 was `0xBB4D50AF4614FD39`). Ours, as before; the client is unchanged (existing kinds only).
New primitives in `AshfallBuilder`: `Convex` (a convex solid from vertices and face cycles, every triangle wound away from the centroid), `Gable` and `Pyramid` on top of it. `WallRun` gained an optional gateway (column, storey): a full-height column with a floor-level 4 m x 4.5 m opening on that storey.
- **Twin towers with a bridge** (west avenue, y 140..160): the west tower at x -124..-108, y 140..156 (16 x 16) in A's block and the east tower at x -84..-72, y 144..160 (12 x 16) in the stronghold block, butted to the west L wall (x -72..-68). Both 4 storeys (24 m), both with the walkable dogleg core, a jump-only roof slab at 24 m (kind Perch).
  The bridge: a deck 6 m wide at 12 m (kind Floor) from x -109 to -83 through a 4 x 4.5 m gateway in each facing wall (y 148..152), parapets 1.2 m (kind Cover) on both sides, open (not roofed). The avenue's 8 m centre corridor is free at ground level (the deck starts at 11.5 m);
  the corridor test skips parts that start at 6 m or higher, for the bridge. The stronghold block's ban for ruins now begins at x -72 (it began at -76) so the tower fits beside the wall. A tower of 12 m on the short side has two hole positions in its floor plates instead of three.
- **Keep stair**: the keep's body is now 17.5 m tall with a Floor slab (kind Floor) as its roof at 18 m, so the roof is no longer a jump-only perch. Three flights of 4 m wide box steps (0.25 / 0.5 m) rise along the east face at x 32..36 from y 164 to 208 with landings at 6 m (y 176..180) and 12 m (y 192..196)
  and a 4 m deck at 18 m (y 208..212); the foot is at (34, 162). The courtyard, the start area and the gate approach are untouched (the stair is 32 m from the east L wall).
- **East tower** (x 200..216, y 48..72, 24 m, cored) with the sketch's features, varied:
  - a gallery on the north face (towards C) on the third floor (18 m): a slab 3 m deep and 8 m wide with 1.2 m parapets on three sides, entered through a gateway from the floor;
  - an outside stair that wraps the south-east corner: flight 1 (3 m) along the south wall, a 4 x 4 m landing on the corner, flight 2 (3 m) along the east wall to a 4 x 4 m deck at 6 m in front of a gateway to the first floor. It is a second way up besides the core; the ground doorways moved away from it (east column 5, south column 2);
  - two pointed roofs of different heights on a roof slab at 24 m: a gable 12 m high (ridge at 36 m, slopes 63 deg) over the south half and a spire 14 m high (apex 38 m, faces 67 deg) over the north half, kind Wall, so nobody stands on them; two bare strips of the slab beside the spire (x 200..202 and 214..216) are the jump-only flat.
  The highest point is 38 m: 38 + 17.54 + 4 stays under the 64 m boundary (checked by the existing boundary test).
Checks added: the roofs are outward wound, closed, steeper than 60 deg, 2 different heights, top at or under 40 m; the keep roof slab, stair and no perch on the keep; the bridge deck (at least 4 m), parapets (1.0..1.2 m) and the gallery (3 m deep, parapets 1.0..1.2 m).
The slow graph test walks to both twin towers' floors (with the other cored towers'), the bridge's west end, middle and east end and the east tower's plate, the keep roof from the stair's foot and the deck, the gallery, the outside stair's landing, top deck and first floor, from the stair's foot to its top and from its top into the first floor,
and shows the gable and spire faces (a downward ray meets a steep face), the two bare roof strips (solid flat tops, with the ray control) and the twin towers' roof slabs are not walked to.
Results of 2b: 65,086 collision triangles in 4,582 parts (Wall 21,638, Stair 22,644, Floor 1,872, Cover 4,932, Perch 456); the enemies' graph has 919,534 nodes, cold build 97 s, about 75 MiB retained, cache file 77 MiB. Deviations: the bridge is railed but not roofed; the twin east tower is 12 x 16 m (the strip between the avenue's lane and the L wall is 12 m wide).

## Pass 3 (stronghold rework, sketch 6)
The owner's sketch 6 (2026-11-05; an overhead shot of the stronghold looking north: the keep, its south entrance, the two L walls, the gate) and his words for the outerwork: "an 'outerwork' defense that is a moderately sized narrow structure outside of the wall which seals off except the gateway the boss compound from the rest of the map. the only ways in are to be an assault marine and jump over the wall, the bridge buildings, or to go through the central gate." Item 7 (the east outerwork): "yes".
`AshfallMap.Version` is now **5** (level hash `0xAE096E323BB5A4D3`, pinned; version 4 was `0xA07D05D38EB46CF2`). Every coordinate and size is ours (placeholder demonstration), to check in the owner's playtest; the client is unchanged (existing kinds only).
New code: `AshfallMap.Stronghold.cs` (the ring, parapets, bridge, outerworks, the three towers); `AshfallMap.Structures.cs` gained `WallRun(noGround)` (a sealed ground storey) and the `TowerDef` fields `Sealed`, `BackGateCol`, `BackGateStorey`.

What it is now (block x -80..80, y 104..224):
- **Ring (the seal).** Walls 4 m thick with walkable tops: west half 12 m (south leg x -72..-12, y 112..116; west leg x -72..-68, y 116..220; **new north wall** x -68..68, y 216..220), east half **raised to 18 m** (south leg x 12..72, y 112..116; east leg x 68..72, y 116..220, **extended north** from 196 to 220). The gate x -12..12 is the one opening on the ground; a test probes the centre line every metre. The north wall closes the block's north side; the keep stays free-standing, 4 m inside it (an alley).
- **1. Rampart parapets.** Cover, 0.5 m thick, 1.2 m tall, on both sides of every wall top, edge to edge (gap 0, so the kit's gap rule holds). They stop only where a tower stands against the wall (its own wall is the barrier), at the twin tower's face (west leg y 144..160) and at the keep bridge's mouth (y 199..205). Walkway between parapets: 3 m.
- **2. Stair towers** replace the two inner ramps (removed). Both stand inside the compound at the courtyard's south corners, 16 x 16 m, with the walkable dogleg core of the tall buildings. SW: x -68..-52, y 116..132, 3 storeys (floors at 6 and 12 m), a gateway (4 m wide, from 4.5 m above the floor) at 12 m through its west wall onto the west rampart (y 124..128). SE: x 52..68, y 116..132, 4 storeys (floors to 18 m), a gateway at 18 m through its east wall onto the east rampart (y 124..128). Ground doors face the courtyard (north and east for the SW tower, north and west for the SE tower); the walls against the ring have none. The rampart is reached only through the tower, which stands inside the ring, so no path opens from outside.
- **3. Keep tower** (the tall tower on the keep's north-west part): 16 x 16 m (x -48..-32, y 196..212), 5 storeys, walls to 30 m (12 m above the keep roof at 18 m), walkable core (floors at 6, 12, 18 and 24 m), a jump-only roof slab at 30 m, a gateway at 12 m through its west wall (y 200..204). It stands against the keep's west face, not on the keep: the keep is solid inside, so a tower on top would have no floor to start from.
- **4. Bridge**: deck 6 m wide (a Floor slab at 12 m, x -68..-48, y 199..205) from the west rampart across the compound to the keep tower's gateway, parapets 1.2 m on both sides.
- **5. East wall raised to 18 m** and extended north (above); its stair tower is the SE tower at its south end.
- **6. Outerwork west** (a narrow terraced bastion outside the south wall's west half): x -68..-20, y 102..108 (6 m deep, 48 m long) in three terraces of 16 m with tops at 2, 4 and 6 m (Wall body, Floor top, 1.0 m Cover parapets on both long sides and an end cap), a box-step stair from the ground at its west end (x -72, 4 m wide) and a stair between the terraces. It stands 4 m from the wall (y 108 against 112) and its highest top is 6 m under the lowest rampart, so it is no foot path onto a wall top and a jump pack clears it; it is 8 m from the gate's approach (x -20 against -12) and clear of the avenue's 8 m lane (y 88..96).
- **7. Outerwork east: a wall, not a ramp.** An outer wall from the gate's east side running south-east in two steps, 6 m tall (kind Wall, no stair, so its top is jump-only like a perch): x 20..36, y 104..108, then x 36..56, y 100..104. Same distances as item 6. I chose the wall (the brief read "an outer wall") over a ramp: it needs no walkable top and so cannot become a way in.
- **The twin tower east is sealed at the ground** (no door, no window on the ground storey): its ground doors would have led through its core up to the new rampart gateway and made the bridge unnecessary. It has a new gateway at 12 m through its back (east) wall, column 1 (y 152..156), onto the west rampart. So the way in over the bridge is: west twin tower's core, bridge deck (12 m), east twin tower's gateway, its 12 m floor, the new gateway, the west rampart, then the SW stair tower (down to the courtyard) or the keep bridge.
- **Seal**: the compound (courtyard, keep, ramparts, stair towers, keep tower, keep bridge) is entered on foot only through the gate or the twin tower bridge; jump-pack classes clear the wall (intended). One quirk found by the seal test: the bridge's parapet tops (0.5 m wide) can be reached by a walk-off from the west tower's upper floor (a drop of about 5 m), so the graph treats the parapets as part of the bridge; a player could do the same, which is the bridge route anyway.

Checks added (all automated): the parapets (heights 1.0..1.2 m, both sides of every wall run, uncovered stretches only as designed); the stair towers inside the compound with their storeys, floors to the rampart heights and gateways (open above the sill, wall under it); the twin tower east's ground storey solid; the keep tower (16 x 16, 5 storeys, 30 m, core, floors) and the bridge (deck 6 m wide, parapets 1.0..1.2 m); the ring closed on the ground except the gate; the outerworks' heights and distances. Slow graph tests: every pass-3 place is walked to from the Marine start (ramparts, both stair towers' floors, the keep tower's gateway and floors, the keep bridge, the terraces); each gateway is passable at its own level (a walk that stays at 12 m from the west tower's bridge end over the bridge, through the east tower and its new gateway onto the west rampart; the stair towers' gateways; the keep bridge into the keep tower); no walk at terrace height leads from the outerwork to the wall. **The seal test** builds four local walking graphs (x -135..105, y 84..232, walk and drop links only, seeded on the avenues, beside the twin towers and in the courtyard): gate and bridge both present, the gate walled shut, the bridge's deck and parapets removed, and both gone. Every compound place is reached from the avenue in the first three and none in the fourth, while the outside places (avenue, the west tower's bridge end, the terraces, the gate approach) stay reached.
Adapted existing checks: the tall-building count may be up to 20 (17 now: the 14 of pass 2b plus the two stair towers and the keep tower); the east L wall's walked top is at 18 m (was 12 m); the pinned level hash and the version. The old ramps were not pinned by a check of their own, so no ramp check changed.
Results (version 5): 69,202 collision triangles of the 200,000 budget in 4,925 parts (version 4: 65,086 in 4,582). Per kind: Ground 10,208, Road 36, Paving 12, Curb 96, Wall 23,294, Cover 5,112, Rubble 2,904, Floor 2,304, Stair 24,480, Perch 468, Stronghold 48, Boundary 48, Backdrop 192. Tall buildings: 17 (13 with a core). Enemies' graph: 922,750 nodes (919,534 before), cold build 101 s inside the parallel test run, about 75 MiB retained, cache file 77.6 MiB. The four local seal graphs have about 143,400 to 144,000 nodes each and build in about 15 s each. `smtool ashfall-plan` writes an SVG only (a copy of this pass's plan is outside the repo, `%LOCALAPPDATA%\sm-reimpl\cache\previews\ashfall-sketches\plan-v5.svg`).
Deviations and uncertain: the keep tower stands against the keep (item 3). The east outer wall has no stair, so it is a barrier only. The north wall is 12 m (the west half's height), so the east leg's 18 m rampart is a separate walk with no stair to it from the north wall. Both outerworks are ours and await the owner's eye. The plan image was not looked at by a person.

## Kit dressing (draft 1)
The owner's decision of 2026-11-05 (agent-coordination `decisions/2026-11-05-ashfall-kit-dressing.md`): dress the map with his generated library (32 Meshy models AC-001..032, 8 ground tiles AC-033..040), keep the 6 m storeys and the layout, v5's graybox stays the collision skeleton, models that add solid mass get their own simple collision proxy.
`AshfallMap.Version` is now **6** (level hash `0x6E3C634B27D892B7`, pinned; version 5 was `0xAE096E323BB5A4D3`), `AshfallMap.DressVersion` **2**. Everything here is ours (placeholder demonstration) and goes to the owner's playtest; the models are not in the repository (the contract is `AshfallKit.cs`).
Code: `AshfallDress.cs` (entry `AshfallMap.BuildWithDressing()`, the box reader `AshfallBox`), `AshfallDress.Skins.cs`, `AshfallDress.Surfaces.cs` (surfaces and decoration), `AshfallDress.Props.cs`, `AshfallExport.cs` (JSON and GLB). `Build()` and `BuildWithFeatures()` return the dressed level's collision (v5 parts + the props' proxies, appended at the end, so every version 5 part keeps its index); `BuildWithDressing()` returns the same level and the items.
Deterministic: integer hashes (`AshfallBuilder.Mix`), 15 degree yaw steps from the table (the yaw is `step x pi/12`), total-order sorts, no random numbers.

Result: 5,457 items (Skin 4,891, Surface 219, Decoration 280, Prop 67), 2,570 hidden parts; 5,002 parts and 70,126 collision triangles (v5: 4,925 parts and 69,202 triangles; the props add 77 parts and 924 triangles). The items' model triangles (catalogue numbers, for the client's budget): Skin 55.2 M, Surface 1.2 M, Decoration 3.9 M, Prop 1.4 M. **The skins are the heavy part**; the client should instance and cull (or the "small piece" threshold below goes up).

### Skins (over graybox boxes)
A part is read as a box (`AshfallBox.Of`): 12 triangles, flat top and bottom, any 15 degree yaw. Anything else (heightfield ground, convex roofs, the pitched barrel, objective discs) gets no skin. Local X is the box's longer horizontal side. A box is tiled exactly (N copies along the length, rows stacked in height), so every item lies inside the part's box (the checks allow 0.15 m, the draft uses 0): what is seen is what is hit. The tiling is the fewest items that keep the three scale factors within 1:2.2 of each other (else 1:3); a model that cannot do that is not used. The model is chosen among the candidates by triangle cost (items x the model's triangles): any candidate within twice the cheapest is eligible and the part's hash picks. A part that has skins is hidden (`HiddenParts`): the tiles cover its four faces and its top, and the openings (windows, doorways, gateways) are separate boxes in the graybox, so they stay open.

| Graybox part | Models (candidates) | Rule |
|---|---|---|
| Wall, thickness at most 4.1 m, height at least 0.45 m | face under 40 m2: AC-031, AC-014, AC-023 first (8-11k triangles); else by height: 10 m and up AC-005, AC-004, AC-006, AC-002, AC-001; 5-10 m AC-004, AC-006, AC-001, AC-005, AC-002; 2.2-5 m AC-001, AC-006, AC-004, AC-003 (AC-003 first for short thick pieces); under 2.2 m AC-001, AC-017, AC-014, AC-011 | 4 m columns, window sills, lintels, the 4 m ring walls (12 and 18 m: several rows); thicker boxes (platform and outerwork bodies) stay graybox |
| Cover, thickness at most 3 m | AC-031, AC-011, AC-014, AC-017; AC-018, AC-017 when 1.8 m or thicker; AC-011, AC-014, AC-031 in the crater field (AC-012 only as a prop) | low walls, staggered walls, rampart and bridge parapets |
| Rubble | up to 0.4 m: AC-020, AC-024, AC-025; taller: AC-019, AC-021, AC-023 | |
| Perch, 4.5 m or less across and 8 m or more tall | AC-010 (AC-030 does not fit) | the two broken obelisks |
| Floor on the ground, 8-17 m across, up to 3 m tall | AC-028 | A's plinth, the small monument's plinth, the gun platform |
| Boundary | AC-031 in 3 rows of about 22 m, 31 pieces a side | tiles the whole 4 m thick wall (stretched about 1:2; the library has no 64 m wall) |
| Stronghold (the keep's four boxes) | AC-006, AC-004, AC-001 | a shell 3 m deep along the four vertical faces |

Left graybox (not hidden): Ground, Road, Paving, Curb, Stair, Floor (plates, decks, platform tops, terraces), Perch plates, the pointed roofs, thick Wall bodies, and **Backdrop** (the cathedral blocks behind the 64 m boundary are not visible from inside except the last few metres of the 70 m ones; no model is dressed on them; a deviation from the brief).

**Facing** (the client developer's rule: the fit is a rotation, not a mirror; with SwapXZ false glTF X is local X, glTF -Z is local Y, so the front, glTF +Z, faces local -Y, south at yaw 0): wall and cover skins face the nearest street (the avenue or perimeter centre line, ours), the boundary's face the play area, the keep's shell faces outward; props and decoration face their hash yaw. Material notes from the client: AC-012, AC-015, AC-018 and AC-028 render nearly white, AC-004, AC-009 and AC-024 light.

### Surfaces (visual only, no collision)
A tile's box is `[surface - raw height, surface]`, so the tile's top (raw Y = 0) lies exactly on the surface (the contract's box-bottom rule; **if the client puts Position.Z at raw Y = 0 instead, the tiles sink 0.3 m: one line in `Tile()` flips it**).
- Avenues (24 m) and the perimeter road (16 m): one row of AC-033 tiles of about 24 m in each stretch between the junctions (3 times wider on an avenue, 2 times on the perimeter); 219 surface items in all, because the ground tiles are draw-call heavy (client note). One in eight straight tiles is the cratered AC-040 (its crater lies below the ground slab, so it shows only if the client lets the slab through).
- Junctions: AC-035 fitted to each of the 4 avenue crossings (24 x 24; its arms are narrower than the 24 m avenue), AC-034 fitted to the 8 avenue/perimeter T junctions; the 4 perimeter corners stay bare (no L tile). The paved road from the compound to the plaza: AC-033, about 14 m a tile, 12 m wide, on the Road parts' tops.
- B's plaza (120 x 120 m, on the Paving top at 0.27 m): AC-038 in the corners, AC-036 on the rim turned outward and, turned by a hash, over the inside (15 m cells: the 8 m tiles scaled 1.9 times, uniform; 8 x 8 cells). **AC-037 (raised 1.2 m centre) is not used**: B's centre must stay flat and walkable, and the library has no plain plaza tile.
- Assumptions of the tiles' orientation, to check in the client (the fit is a rotation: glTF -Z is local Y, a model's front glTF +Z faces local -Y): AC-034's stem points along its local +Y at yaw 0 (towards the map's middle here), AC-036's rim is on its local -Y side, AC-038's corner at local (-X, -Y). The avenue tiles run on under the monument and the arch.

### Decoration (walk-over)
280 items (AC-020 2.6 x 2.4 x 0.30 m, AC-022 3.2 x 3.2 x 0.08, AC-024 1.8 x 1.8 x 0.20, AC-025 2.4 x 1.7 x 0.30): the placer's hash order on a 3 m grid, 5 m apart, at least 0.8 m from every solid, outside the lanes (the placer's bans), the capture zones plus 6 m, the start areas and the spawn markers plus 6 m, on level ground at the ground's height. Height never above 0.35 m.

### Props (70, extra solid placements)
Placed with `AshfallPlacer` after the whole v5 level: the same banned places (lanes, gate approach, start areas, capture zones plus 6 m, B's open centre, spawn markers), plus the stronghold block (x -84..84, y 96..224) for every prop; at least 3 m from every claim (the gaps are far over 1.2 m), 10 m from any other prop and 30 m from its own kind, on level ground inside +-222 m; the owner's red zones first (`AshfallMap.PriorityZones`), each model with its own hash order, round robin so a short supply of room costs every kind a little. Heights keep out of the 0.4-0.9 m band; rubble 0.9-2.5 m.

| Model | Proxy | Size (L x D x H) | Count |
|---|---|---|---|
| AC-027 shattered saint monument | Wall box | 6 x 5 x 7 | 4 |
| AC-032 statue fragments | Rubble box | 4 x 3 x 2.2 | 8 |
| AC-031 relief slab (standing) | Cover box | 3.6 x 0.8 x 1.8 | 12 |
| AC-029 shrine fragment | Wall box | 5 x 3.5 x 6 | 5 |
| AC-030 collapsed buttress | Wall box | 12 x 5 x 6 | 4 |
| AC-002 arch remnant | two Wall piers (2 x 2 x 7) and a lintel at 5.5-7 m; the 4 m x 5.5 m opening stays open (the item's Part is the first pier, the next two parts are the rest) | 8 x 2 x 7 | 5 |
| AC-013 plinth fragment | Cover box | 3 x 3 x 2 | 7 |
| AC-008 elevated firing perch | Perch box (a standable top, reached by a jump only) | 6 x 3 x 4.5 | 6 |
| AC-007 exterior stair ruin | Wall box, **a non-walkable backdrop piece** (no stair of its own; my call) | 6 x 4 x 6 | 3 |
| AC-018 chokepoint debris barrier | Rubble box | 3.2 x 2.4 x 1.8 | 10 |
| AC-012 crater edge cover | Cover box (3 only: the model has no clear flat footprint) | 5 x 2.4 x 1.4 | 3 |

AC-015, AC-016 and AC-026 (flagged `Review`) and AC-009 are not used. The proxies are plain boxes: a monument's or a buttress's silhouette differs from its box, so a shot may stop a little before the stone.

### Exports for the Mapmaker
`smtool ashfall-plan --dressing-json <file>` writes the dressing (`levelVersion`, `dressVersion`, `levelHash`, the items in order with `index`, `asset`, `role`, `part`, `position`, `yaw`, `size`, `swapXZ`, and `hiddenParts`); `--graybox-glb <file>` writes the level's solid parts (the props' proxies included) as one glTF 2.0 binary, a mesh and a node per `AshfallKind`, Y up in metres (game X to glTF X, game Y to glTF -Z, game Z to glTF Y), positions only. `smtool ashfall-plan` also draws every item (outline by role, props filled) and prints the counts per role and per asset; a copy of this pass's plan is outside the repo, `%LOCALAPPDATA%\sm-reimpl\cache\previews\ashfall-kit\plan-v6.svg`.

### Checks (all automated, `AshfallKitDressingTests.cs`)
Two builds give equal items and level; every item names a catalogue model (the flagged ones only as scatter); every skin lies inside its part's box grown by 0.15 m, with scale factors within 1:3; each hidden part has skins that cover it (the volumes add up to the box); surfaces lie on road cells or on a Road/Paving top and never over a block; decoration is at most 0.35 m high and outside the discs, start areas and spawn markers; the props (67) number 40-120, are appended after the v5 parts, stand outside the lanes, capture zones plus 6 m, start areas, stronghold block, gate approach and plaza axes, 10 m apart, half of them in the red zones; the arch keeps its opening; the JSON round-trips counts and values; the GLB has a valid header, chunk lengths, one mesh per kind and the right Y-up bounds. The existing graybox tests (spawns, gaps, bands, corridors, capture cylinders, seal) run unchanged on the dressed level.

### Open and for the owner's eye
All sizes, the model choices, the cheap-model rule for small walls, the prop spacing and the tile orientations are estimates; the draft was not seen in an engine. Skin stretch is visible on thin pieces (a 1 m wall in a 2 x 0.5 model is stretched to twice its natural depth). AC-003 (the corner model) is used only for short thick wall pieces, not for corners as such: the graybox has no corner piece. The 56 M skin triangles are the first thing to cut (instancing, LOD, or a larger small-piece threshold in `Skins`).
## Kit look (client)
The client draws Ashfall Citadel in two looks of the same map `tfw_war`: the graybox (`AshfallView`, unchanged) and the kit look (`AshfallKitView`), which draws `AshfallDressing.Items` with the owner's generated models and leaves out the graybox parts in `HiddenParts`; the remaining graybox parts get a stone and ash material (no checker); the objective discs and letters stay. Both are built once and switched by visibility: **F8** in game, or `--ashfall-look kit|graybox` at start (default kit when `%LOCALAPPDATA%\sm-reimpl\ashfall-kit\source\` exists, else graybox). The models are read at runtime from that private folder (`AshfallKitLibrary`: Godot `GltfDocument`, no editor import, on 3 background threads, one cached mesh per asset id, LODs from `ImporterMesh.GenerateLods`, textures shrunk to 1024 px and S3TC-compressed); nothing is copied into the repository or a build. A missing folder or file logs one line and that item stays graybox (a hidden part stays drawn until every Skin item over it is shown). Fitting follows the `AshfallDressItem` rule; the axis mapping is a rotation, not a mirror: glTF X to local X, glTF -Z to local Y (the model's front, glTF +Z, faces south at yaw 0), glTF Y to Z; swapped: glTF Z to X, glTF X to Y. Measured on the development PC (12 threads, Forward+): all 40 models load in about 6 s of background time (3 workers, 17.7 s summed; 3.2 s with `--ashfall-tex 0`), about 85 MiB of textures; items appear as their model arrives. Per-instance `MeshInstance3D` with mesh LODs is used (a MultiMesh has no culling or per-instance LOD, and a synthetic dense dressing of 2,591 items would put 16 M triangles on screen). The kit look sets the viewport's mesh LOD threshold to 4 (engine default 1; `--ashfall-lod-threshold`): on the dense synthetic map that cut GPU time from 20-28 ms to 2-5 ms per frame. Other aids: `--ashfall-tex N`, `--ashfall-lods off`, `--ashfall-shadows off`. Test aid `godot.cmd --windowed -- --ashfall-kit-gallery [--shots all|overview|AC-001,..] [--out dir]` lays out all 40 models in labelled boxes (previews in `%LOCALAPPDATA%\sm-reimpl\cache\previews\ashfall-kit\`); with `--ashfall-dress` it shows the map with the real dressing, with `--ashfall-dense` a synthetic dressing (a Skin item over every Wall, Cover and Rubble part, test aid only) and prints KITPERF lines (frame time, draw calls, primitives, both looks). **Placeholder demonstration** (draft assets, to compare by eye in the owner's playtest).

### Draft 2 (the lead's fixes from the first screenshots, 2026-11-05; `DressVersion` 2)
- **AC-031 (winged relief slab) is a prop only** (8 on the map): it read as rows of wing-shaped totems as a wall skin, on the boundary and in the cover pool. Cover skins now come from AC-011, AC-014, AC-017 (AC-018 for the thick ones, sparingly: it renders near white).
- **Small or narrow Wall pieces get no skin and are not hidden**: a Wall box needs a face of at least 40 m2 and at least 2 m along the face; the client draws the others as stone graybox.
- **Boundary**: AC-001, AC-004 and AC-005 along the inner face in pieces of about 12 m, stacked in tiers (AC-001 10 m, AC-004 12 m, AC-005 14 m, picked by the hash per tier, a short last tier shared with the one below) up to the wall's 64 m; scale factors within 1:3.
- **Contract change**: skinned Wall, Stronghold, Boundary (and plinth and obelisk) parts are no longer in `HiddenParts`; only Cover and Rubble skins hide their box (the client draws the others as a stone backing inset by 0.15 m, so openings in the models show stone). Doc comments in `AshfallKit.cs` and the hidden-parts test say so.
- Result: 3,931 items (Skin 3,369, Surface 219, Decoration 280, Prop 63), 647 hidden parts, skin model triangles about 72 M (the tiered boundary alone is about 25-30 M: the wall models are 30k triangles each); 4,998 parts, 70,078 collision triangles; level hash `0x6E3C634B27D892B7` (the props moved: AC-031 went from 12 to 8, so the hash changed).
