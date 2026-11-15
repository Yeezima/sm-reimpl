# Effect priority and the live-effect cap (fx-cap-priority, 2026-11-15)

The owner, 2026-11-15: "we need to make a change to visual effects. can we raise the cap even more and audit priority?"

`FxPlayer.Play` keeps at most a **cap** of live shot, hit and blast effects (ambient map effects are counted apart: 200). When the cap is full a new effect takes the place of
an old one, or is refused. This page is the inventory of what goes through that cap, the priority tiers, and how the victim is chosen.

## How a victim is picked (`SmContent.FxEviction.Victim`, tested in `FxEvictionTests`)

Among the live effects that are not ambient and whose priority is at or below the new effect's:

1. the **lowest priority** goes first;
2. within one priority the **farthest from the camera** goes (distance in steps of 2 m, so a burst's sprays that share a place are not told apart by a centimetre);
3. within one distance step the **oldest** goes (before 2026-11-15: the oldest, with no distance).

When the pick has the new effect's own priority and the new effect is farther from the camera than the pick, the new one is the one that would go first: it is **refused**
and nothing nearer is dropped for it. An effect of higher priority than every live one is never refused. The counters `Evicted` / `Refused` (by tier, slots -1 to 4) are in the
`effects:` log line.

## Tiers (`SmContent.FxPriority`), high to low

| Tier | Value | What |
|---|---|---|
| Own | 4 | the player's own weapon effects: muzzle flash, tracer, impact (Classic PvP and Musou). Not done: an own projectile trail (the projectile view carries no owner) stays at 1 |
| Hit | 3 | hits on the player and by the player (armour and flesh hit glows on a player, Musou's own blood and hit effects), a blast within 15 m of the camera, power-weapon flashes |
| Special | 2 | bosses, elites, abilities and specials: the Weirdboy (was 1), the drop pod (was 3), blasts (rokkit, squig, orbital, thrown grenades), the Meks, stomps, melee flares, death effects, jump-pack landings |
| Ally | 1 | other players' and allies' muzzles and tracers, held effects of a fight (melee glows, jump trails, eye glow), Musou allies' hits |
| Crowd | 0 | distant crowd gunfire (Ork and Guardsmen tracers and muzzles), other players' impacts, decals' sparks, Musou's other hits, stop-fire barrel smoke |
| Puff | -1 | the weight pass's small puffs |
| (ambient) | n/a | the map's looping effects: their own cap of 200, never evicted by a shot |

## Inventory: what goes through the cap

Path: CPU = drawn by the effect player's own sprite batch; GPU = Musou Mode's spray particles (the one-shot sprays of an effect are written once and computed by a shader; the ring
children and everything that is not a spray stay on the CPU). Classic PvP is always the CPU path (cap 160). "Reach-capped" effects end their particles within 4 m of the body and stay on the CPU path.
Typical counts are from the 2026-11-15 runs (a 60-army clash: **224** live effects at most; the 200-death burst: about 150).

| Source | Where | Tier now (before) | Path | Capped | Typical in a big fight |
|---|---|---|---|---|---|
| Own muzzle flash, fire effects | `NetworkView.Fx.cs` FxShot | Own 4 (1) | CPU + GPU sprays | yes | 1 to 3 a shot |
| Own stalker crater, own stop-fire smoke, own autocannon terrain flash | FxImpact, FxStopFireSmoke, `ExtrasTerrainBlast` | Own 4 (0) | CPU | yes | |
| Own tracer | `NetworkView.Bullets.cs` PlayTracer | Own 4 (1) | CPU (beams) | yes | 1 a shot |
| Own impact (world and body) | FxShot / FxImpact | Own 4 (0) | CPU + GPU sprays | yes | 1 to 3 a shot |
| Other player's muzzle and tracer | same, `own` false | Ally 1 (1) | CPU + GPU | yes | Classic: up to 16 players |
| Other player's impact | FxImpact | Crowd 0 (0) | CPU + GPU | yes | |
| Hit on the player (armour glow, sparks, flesh) | `NetworkView.HitFeedback.cs` | Hit 3 (0), reach-capped | CPU | yes | |
| Blasts (grenade, rokkit, plasma, bolt blast, autocannon blast) | `FxDetonation` | Special 2, Hit 3 within 15 m (2) | CPU + GPU | yes | Musou: up to 6 bolt blasts a frame |
| Musou own blood and hits | `MusouGorePriority.FxPriority` | Hit 3 (2) | GPU sprays | yes | dozens a second |
| Musou allies' blood and hits | same | Ally 1 (1) | GPU | yes | |
| Musou other blood and hits | same | Crowd 0 (0) | GPU | yes | 10 a second |
| Musou crowd gunfire (Ork and Guardsmen tracers, muzzles) | `NetworkView.MusouGuns.cs`, `TfwGuards.cs` | Crowd 0 (0) | CPU (beams) | yes | hundreds in a clash |
| Death effects and gore bursts | `NetworkView.DeathFx.cs` | Special 2 (2) | CPU + GPU | yes | one per kill |
| Weirdboy staff, shield, burst | `NetworkView.MusouWeirdboy.cs` | Special 2 (1; the burst 2) | CPU + GPU | yes | |
| Drop pod trail, landing | `TfwDropPod.cs` | Special 2 (3) | CPU + GPU | yes | rare |
| Meks, squig, rokkit, melta burns, stormboy landings | `Musou*.cs` | Special 2 (2) | CPU | yes | |
| Thunderhawk jets, shells, tracers | `TfwFireView.cs` | 2 / 1 (unchanged) | CPU | yes | |
| Melee flares and power flashes, stomps, ground pounds | `MeleeFx.cs`, `PowerFlash.cs`, `GroundPoundFx.cs` | 3 / 2 / 1 (unchanged) | CPU + GPU | yes (the flashes are limited by their own swing budget) | |
| Dreadnought footfall dust (his muzzle effect is gone from main) | `NetworkView.Dreadnought.cs` | Special 2 (0) | CPU | yes | |
| Pickups' idle glows | `NetworkView.Pickups.cs` | 0 (unchanged) | CPU | yes (held, repeating) | |
| Map ambient effects | `NetworkView.Fx.cs` | ambient, own cap 200 | CPU | apart | |

## Cap

See the spec row "Live-effect cap with the GPU sprays (Musou)" and `docs/milestones.md` (fx-cap-priority) for the measurements and the chosen values.
