# Ashfall layout brief (owner, 2026-11-09)

The owner's map-generation principles for Ashfall (Musou Mode), given verbatim on 2026-11-09 with "here's the context data about the way i want you to change the map generation system". The two-approach target and the formation tests are the owner's proposed Ashfall-specific rules, not universal numbers. Answers to the follow-up questions and the plan built from this brief go in docs/musou-ashfall-graybox.md.

---

reviewed both level-design references and Ashfall's existing generator. The key principle for this map is:
Generate a battlefield of broad, readable combat spaces connected by army-capable routes, not a decorated floor that merely has a valid path through it.
Ashfall already has 24 m avenues, 16 m perimeter roads, objective clear zones and anti-overlap rules. The important gap is that a passage wide enough for one Ork, or even the existing 2.4 m prop-gap rule, isn't necessarily wide enough for a formation. The rules below build on what exists rather than replacing it.

Prompt for Claude

Review the current Ashfall procedural generator and implement a configurable map-layout ruleset. Preserve existing authored geometry, deterministic generation, client/server agreement, Classic PvP isolation, and approved asset-placement rules. Inspect current code first and extend existing systems rather than duplicating them.

Battlefield structure

* Plan bases, objectives, assembly areas, major army routes, clash areas and retreat routes before placing decorative structures. Reserve these as protected areas.
* Keep objectives and their approach areas open enough for large fights. Place standalone, floor-supported buildings and debris logically around the battlefield, not stacked or connected into accidental clutter. Preserve explicitly authored stronghold and bridge exceptions.
* Create distinct districts and recognizable landmarks so players can orient themselves. Arrange major spaces as a believable ruined battlefield: assembly areas, broad avenues, open clash spaces and quieter transition areas, rather than a uniform scatter of assets.
* Prefer at least two meaningfully distinct, formation-capable approaches to ordinary contested objectives. They should not immediately converge into one small doorway. Treat strongholds as authored exceptions; don't add gates or change fortress identity without asking me.

Movement and mass combat

* Separate individual-agent navigation from army-route capacity. Expose a minimum usable formation width based on actual actor dimensions, lateral spacing and edge clearance. Check the narrowest point along the entire route after final collision geometry is placed.
* Keep primary routes, base exits, objective approaches, assembly areas and retreat paths clear of decoration. Stairways and rooftops may be player-scale, but must not be counted as army-capable routes unless they actually support formations.
* Validate paths from each base to relevant objectives and back, using the largest intended ground units. Test terrain, collision, overhead clearance and steps with the real movement rules.
* Test the intended 200-vs-200 average clash, 500-vs-500 larger clashes and rare 1,000-vs-1,000 target. Measure frontage, queue length, stuck units, arrival spread and wave throughput. A valid individual path is not a pass if the army forms a long single-file stream. Investigate formation and routing AI as well as geometry; don't assume the map alone causes funneling.
* Preserve the approved wave, retreat and reinforcement behavior. Do not silently rebalance near/far combat.

Sightlines, height and pacing

* Allow players to see or hear approaching waves and recognize objectives from useful decision points. Preserve scouting; don't automatically reveal wave destinations.
* Provide meaningful cover and flanking options without filling the field with obstacles. Place large sightline blockers mainly at edges or as intentional islands, without blocking army routes.
* Give elevated positions useful views and accessible approaches, but avoid one position dominating every objective. Protect stair feet and landings. Keep firing-platform edges free of railings as previously approved.
* Measure player rotation and army reinforcement times along actual routes, not just straight-line distances. Tune them with the existing wave director so the battlefield supports the intended peaks, clashes and lulls.

Generator controls and validation

* Expose tunable parameters for formation clearance, approach count, objective and assembly footprints, route travel times, obstacle density by zone, landmark visibility and high-ground coverage. Derive initial values from actual game metrics and playtests; don't present arbitrary numbers as universal level-design rules.
* Keep layout, dressing and surface-variation seeds separate where practical, so changing visual variety doesn't unexpectedly alter collision or routes. Maintain deterministic hashes and networking compatibility.
* Extend the existing `smtool ashfall-plan` output with overlays for protected areas, routes, width pinch points, assembly zones, objective approaches, sightlines and travel times. Report failed constraints clearly instead of silently producing a bad map.
* Provide a top-down overview, clearance and height/sightline views, a travel-time table, and short clips of waves assembling, advancing, clashing and retreating. Compare against the current map and explain any tradeoffs before changing the authored stronghold.

Success: Ashfall remains open and recognizable, armies deploy in broad fronts rather than narrow lines, objectives are readable and defensible without becoming choke points, and the player can navigate and influence a large battle.

References behind the rules

* [Level Design Book: Metrics](https://book.leveldesignbook.com/process/blockout/metrics) explains why dimensions should come from the game's actual characters and movement, then be tested in a blockout.
* [Circulation](https://book.leveldesignbook.com/process/layout/flow/circulation) illustrates route hierarchy and flow. For Ashfall, apply the principle to broad battlefield routes, not narrow arena lanes.
* [Wayfinding](https://book.leveldesignbook.com/process/blockout/wayfinding) covers landmarks, paths and districts that help players understand where they are.
* [Encounter design](https://book.leveldesignbook.com/process/combat/encounter) covers sightlines and giving players ways to assess and enter a fight.
* [Valve's Left 4 Dead AI presentation](https://cdn.cloudflare.steamstatic.com/apps/valve/2009/ai_systems_of_l4d_mike_booth.pdf) is useful for crowd movement and dramatic pacing. It's inspiration, not a replacement for your wave rules.
* [Godot navigation meshes](https://docs.godotengine.org/en/stable/tutorials/navigation/navigation_using_navigationmeshes.html) explains why navigation clearance must account for actor size.

The two-approach target and formation tests in the prompt are my proposed Ashfall-specific rules, not universal numbers prescribed by those sources.

---

## The owner's answers (2026-11-09, in the Claude chat)
- **Frame:** "full redesign - but keep the current version as a path to easily roll back to. basically, i expect to not like what comes of this possible so we will roll it back if needed". The draft-10 layout stays selectable.
- **Army route width at its narrowest point:** about 20 m.
- **Stronghold keep (AC-044, 3 flights ending at no floor):** keep it; those flights stay drawn and unwalkable.
- **Delivery:** one big draft (the whole ruleset, then all views, tables and clips together).
- **Map size:** whatever the rules need.
- **Capture points:** three (A, B, C); their positions may move.
- **Districts:** cathedral quarter, manufactorum district, hab-block ruins, open no-man's-land.
- **Openness:** "mostly open, ruin islands except there are some clusters of multiple buildings near each other."
- **Stair access (same day):** "don't use asset buildings that have stairs with no access point. replace them with properly scaled ones that do."
