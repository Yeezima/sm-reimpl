# Musou expansion: special Orks, the Librarian, mass battles (plan, 2026-11-07)

Owner's brief of 2026-11-07. Musou Mode only (added content, `ContentExtras.Musou`); Classic PvP must not change
(`MusouIsolationTests`, `PvpCombatIsolationTests`). Every value below is **ours / estimate** unless marked, lives in a
rules class (`*Rules`) so it can be tuned, and gets a `spec/behavior.md` row and a refine-register entry.
Branch `musou/expansion` (integration); each stage is its own branch merged into it. Nothing reaches `main` before the
owner's playtest.

## What already exists (reused, not rebuilt)
- One Ork pool `MusouSim` (2048, struct of arrays), type table `MusouTypes.DefaultTypes`, per-type AI hooks
  (`MusouSim.Ai.cs`; the Weirdboy is the template for a special enemy).
- Guardsmen `GuardSim` (squads of 10, never capture: "no offensives" already holds), AI Marines as puppet players
  (`MarineBrain`), positions Locked → Held → Clearing → Securing → Secured (`TfwBattle`), strongholds/HQ, landing zone
  (= Imperial base), hunter patrols (`TfwBattle.Patrols.cs`), Weirdboy slow bolt, `World.MusouBlast`, `MusouSim.Knock`,
  frag (`spec/behavior.md` frag rows), plasma heat (`PlayerState.Heat`), Ork sound budget (`MusouSoundBudget`).
- Ork bullets are rolled hits with no projectile, so Ork shots already never hurt Orks.

## Conflicts and gaps found (flagged, not silently substituted)
1. **Wire type field is 4 bits** (12 of 16 used); five new kinds make 17. Fix: Musou entity record grows a full type
   byte (extras wire, `MusouSim.Version` bump; no core protocol bump). Clients must update (normal for a release).
2. **Scale.** 200 per point and 1,000 v 1,000 exceed the Guard pool (240), the Ork pool (2048) and the 55 KB/s send
   budget. Fix: a new **aggregate far layer** (formations as counts + strength, resolved at 2 Hz); only troops within the
   near radius of a player are materialised as real units. This is the largest and riskiest stage.
3. **Six independent AI Marines respawning after 30 s** conflict with today's retainers (follow the player, max 3 in a
   battle, 180 s recovery) and the 8 puppet ids (4-player co-op). **Owner, 2026-11-07: "Keep both."** The six
   independent Marines come in addition to the retainers. S6 widens the puppet id range from 8 to 12 by lowering
   `PuppetIdFirst` (65516-65527; the point target ids above `PuppetIdLast` stay put) and checks the World's 16-player cap:
   4 humans + 3 retainers + 6 Marines = 13.
4. **Assets.** In the install (co-op set): `squig_bomb`, `boy_pro_rocketlauncher` (Rokkit), `nob_ranged` (Big Shoota).
   **Stormboy** (`boy_stormboy`) is campaign-only: a singleplayer dependency, to be noted in CLAUDE.md like the Warboss.
   **No Mek model and no Librarian/psyker body found**: Mek = a Nob/Boy body with a placeholder look; Librarian = the
   Tactical body with the power sword; both labelled *placeholder*. Contraption models: placeholders.
5. **Three positions are hard-coded** (`WarRules.Positions`, tag 8 size). The expansion keeps three captured points.
6. **Frag reference**: original data frag is 200 damage, radius 10 m with the data fall-off; Musou's player frags use
   ×2 radius. The Squig and crash blasts use the **original** values (radius 10), not Musou's ×2.

## Stages
| Stage | Content | Main files | Depends on |
|---|---|---|---|
| S1 | Foundation: type byte on the wire; target preference by kind (ordinary → Marines/Guards/objectives, elites/bosses → Marines then player); a neutral **Ork blast** (hurts players, Marines, Guards, Orks incl. elites; sets off Squigs; credit "Ork"); friendly-fire table | `MusouEnemy`, `MusouTypes`, `MusouSim.Ai`, `World.MusouCombat`, `MusouWire`, client looks | — |
| S2 | Bomb Squig, Rokkit Boy (collision rocket, one directional warning) | new `MusouSim.Squig.cs`, `MusouSim.Rokkit.cs`, client | S1 |
| S3 | Stormboy squads (replace hunters), Big Shoota Nob | `TfwBattle.Patrols`, new `MusouSim.Stormboy.cs`, `MusouSim.BigShoota.cs` | S1 |
| S4 | Scrap Meks and contraptions (new structure tag) | new `MusouMeks.cs`, `TfwBattle.Meks.cs`, wire tag | S2 (rockets) |
| S5 | Librarian class | `TfwRun.Class`, new `LibrarianClass` (Musou-only), `World.TfwPsyker.cs`, client | — (parallel) |
| S6 | Battle flow: waves, committed destinations, Guard reinforcement and 200 cap, far layer, cohesion, retreat/rally, six Marines, Waaagh! barks | `TfwBattle`, `GuardSim`, new `TfwClash.cs`, `MarineBrain`, client sounds | S1, S3 |
| S7 | Profiling 200/500/1000, captures, Classic regression | tests, `tools` | all |

## Rules (initial values; all tunable)
**Targeting.** Ordinary Orks score targets: Guard squad / AI Marine / objective at weight 1, player at weight 2.5 (so a
player counts only when ~2.5× nearer), any player within 12 m wins outright. Elites (Nob, 'Ard Boy, Big Shoota) and
bosses: AI Marine 1, player 0.8 (player preferred), Guards 3. Stormboys: own rules (S3).

**Ork blast.** Radius / damage per source; hits every side; Squigs within radius detonate next tick (chain allowed,
one per tick per Squig). Kills by an Ork blast credit the player whose action caused it (shot Squig, shot-down Stormboy,
shot trap, kicked Squig within 10 s), otherwise no one.

**Bomb Squig.** Drop: during a battle only, every 60-120 s per player, 12-20 m from a player, off-screen preferred,
fall 1.2 s with a whistle. Speed 7.5 m/s, 30 hp. Fuse starts within 10 m of a player/Marine/Guard: 1.8 s, beeping and
glowing faster. Shot → explodes at once. Kick (player runs into it): flung 12 m along the run, fuse forced to 1.0 s if
not lit. Blast: original frag (200, r 10, data fall-off).

**Rokkit Boy.** 85 hp, holds 25-45 m. Rocket 16 m/s (slow, visible trail), period 5 s, blast r 3.5, 120 damage
(fall-off linear to 25 % at the edge). Explodes on the first Ork or solid in its path. Warning: one screen-edge
"incoming" swish and whine from the nearest threatening rocket only, starting 1.0 s before impact; never stacked.

**Stormboys.** Squads of 8 (2 per battle at once, replacing the 2 hunter columns). 120 hp. Hop: 25 m arcs, 3 s apart.
Order: counterattack the most recently secured Imperial point; when it falls or the squad fails (lost half), hunt the
nearest player (nearest by walk; ties to the player with the fewest hunters). Wiped hunter squad → next squad
attacks a point. All players dead → survivors return to capturing. No target → hold the nearest Ork point. Shot down in
the air (killed while airborne) → ragdoll, crash blast on impact (original frag, scatters Orks). Normal landing: Orks
touched within 1.5 m are shoved aside, no damage.

**Big Shoota Nob.** 600 hp, holds an Ork-held point with a squad of 10. Burst 4 s (12 rounds/s, 4 damage, spread),
then overheats 3.5 s (steam, no fire). Bullets never hurt Orks.

**Scrap Meks.** 5 at once, 250 hp, each respawns 300 s after his death. Roams, flees from players within 20 m, hides
(cover node far from players) and builds: 15 s per contraption, at most 3 alive each, shield generator at most 1 on the
whole map. Death → all his contraptions are destroyed.
- Shield generator: 800 hp, radius 10 m dome; stops shots from outside (walk in to fight).
- Reinforcement beacon: 400 hp; every 30 s brings a drop of 10 Boys, up to 40 alive from it. Destroyed → the drop in
  flight and the queue are cancelled; Orks already on the ground stay and fight as ordinary Orks.
- Rokkit turret: 500 hp, fires the Rokkit Boy's rocket every 4 s, range 50 m.
- Scrap trap: 150 hp, radius 3 m, triggered by a Mek's enemy stepping in; deals 50 % of a player's maximum health to
  players (equivalent flat damage to Marines/Guards); spares Orks. Shot by a player → explodes as an Ork blast (hurts
  Orks). Drawn with a pulsing red light and spikes.

**Librarian** (Musou class, appended only under Musou).
- Psychic Stress 0-100; lightning adds 5/s (20 s to full); Warp Pull +50, Shockwave +50 (each = 10 s of lightning).
- Cooling 12.5/s after 1.0 s without casting (8 s from full); each melee hit −6. At 100 all powers lock until stress
  falls to 0.
- Chain lightning (hold fire): range 30 m, jump radius 8 m, up to 40 targets, 90 dps on the primary, ×0.85 per jump.
- Warp Pull: cone 30°, range 25 m; ordinary Orks pulled to 2 m from the Librarian; elites 35 % of the distance;
  bosses immune. Cooldown 8 s.
- Psychic Shockwave: radius 9 m; ordinary Orks thrown 20-25 m; elites and bosses not moved; 40 damage (kills Gretchin
  30 hp only; Boys 85 survive). Cooldown 12 s.
- Force sword: power sword model and moves, rainbow-iridescent glow (client).
- Psychic Ward: future idea only, not built (nothing reserved on the wire).
- Binding (S5, built): Fire held = chain lightning, Grenade press = Warp Pull, Reload press = Psychic Shockwave; he carries no gun, pistol or grenade. Numbers: `LibrarianRules`. Wire: Musou tag 16. A melee "hit" is a swing that strikes at least one enemy (-6 once a swing); "cone 30" is the half-angle. Second pass (MusouSim 15): the install's MP Librarian torso, belt, right pad and hood backpack over his Tactical look; PLAY AS LIBRARIAN in the Stormbird's Esc menu; original install sounds; a weapon-box-style stress gauge; all 40 links drawn (3-byte jumps); tag 16 in the 5-wave run too.

**Battle flow.**
- Ork waves muster at strongholds; a peak every 3-5 min, lulls of 90-150 s. Wave size: 200 (70 %), 500 (25 %),
  1,000 (5 %).
- Destination on departure: the Imperial-held point farthest from all players (random among the two farthest);
  committed until the wave arrives or routs.
- Guardsmen: reinforcements leave the Imperial base every 60 s, split to the points most under strength; stationed cap
  200 per point; formations in transit may exceed it. Lost destination → nearest other Imperial point → else the base.
- Clash resolution (far layer): Lanchester-style attrition with **cohesion**: a side's fire falls as its cohesion falls,
  cohesion drops with losses and with the enemy's numerical edge, and a side at heavy losses routs. Target: across many
  even clashes win rate 45-55 %, the winner keeps ≥ 25 % of its force, the loser routs before it is wiped out.
- Retreat: Orks after ≥ 50 % losses with no elite/boss within 100 m; Guardsmen after > 50 % losses or when their point
  falls. Survivors go home and join later waves/reinforcements.
- Rally: a player within 15 m of a retreating Guard formation rallies it (once per formation per 60 s); it returns to
  its point, or a nearer Imperial point. A formation that rallied cannot retreat again for 30 s.
- Six AI Marines, independent, respawn 30 s after death (see conflict 3).
- Barks: waves attacking a point shout "Waaagh!" in bursts; at most 3 Waaagh! voices at once, 4-8 s between bursts
  per wave, nearest waves first. No UI warning of a wave's destination.
- Near/far: troops within 90 m of a player are real units; beyond, formation aggregates. Promote/demote with
  hysteresis (enter 90 m, leave 120 m) and a per-tick cap so no frame spawns or kills hundreds at once. Deaths queue
  for the client's death spread (no single-frame gore spike); authoritative damage is never dropped.

## S6 design: the battle flow (2026-11-08)
Everything below is Musou Mode only and **ours / estimate** unless the brief gives the number; values live in
`TfwArmyRules`, `TfwClashRules`, `DefenderRules` (sim) and `WaaaghBarkRules` (client planner, engine-free in SmNet).
What stays as it was: the crowds, the trickle, the musters, the patrols, the Stormboys (S3 orders kept), the Big
Shoota squads, the Boss Orks, the Weirdboyz, the field squads (`StepSquads`), the artillery, the 5-wave run and the
skirmish. The flow is **added** beside them and can be switched off per battle (`TfwBattle.Armies`, tests).

**Armies (the far layer).** `TfwArmy` (new `TfwArmy.cs`) is one formation: side (Ork / Guard), order (Muster, March,
Attack, Hold, Retreat, Rally), goal, a track (the walking graph's way, searched off the tick at the battle's start for
every pair of the battle's places, taken at a fixed tick so a battle is deterministic), its place along it, a centre
and facing, its **count** (aggregate part + real members), its start (losses are counted from it), elites, cohesion and
its timers. Members are real units: Orks of `MusouSim` leashed under the army's own group and walked by a moving home
(the patrols' `MoveHome`), Guard squads of `GuardSim` ordered to the army's goal. `TfwBattle.Armies.cs` steps them.
- **Levels.** An army is *near* while any human (each co-op player, never an AI Marine) is within 90 m of its
  footprint (centre distance less its footprint radius), *far* again past 120 m (hysteresis). Near: its aggregate part
  becomes real units, slot by slot, at most `PromotePerTick` Orks and `PromoteSquadsPerTick` squads a tick over all
  armies; far: its real members fold back in, at most `DemotePerTick` a tick. Count, elites, health share, goal and
  timers belong to the army, so nothing is lost or gained across levels.
- **No popping.** Slots are one shared layout (`TfwArmyLayout`, SmSim, used by the server and the client): rows of
  `Width(count)` abreast behind the centre along the facing. The client draws an army's aggregate part as cheap crowd
  impostors in exactly those slots (Orks through `MusouCrowd`'s vertex-animation tier, Guardsmen through `GuardCrowd`),
  a promotion spawns the real unit in its slot (the impostor of that slot stops), a demotion removes it (its slot's
  impostor appears). A dropping far count becomes bodies at the slots that went (rate-limited, the death spread's way).
- **Pools.** The Ork pool grows 2048 → 2560; every existing spawner keeps its old limit (`TfwBattleRules.CrowdPool`
  2048 less the reserve), so only armies use the new 512 slots, at most `NearOrksMost` 480 real army Orks. GuardSim
  grows 24 → 40 squads (wire capacity 256 → 400); armies own at most 28 of them, the field squads keep theirs.
- **Wire.** Tag 18 (`TfwWire.ArmiesTag`; 17 is left to S4's Meks), boxless like tag 11, 2 Hz and on change, every
  army in one section: `[u8 n]` then 16-byte records `[u8 id][u8 side|order|leader][u16 count][u16 far][u16 elites]
  [i16 x][i16 y][i16 z]` (decimetres) `[u8 yaw][u8 cohesion]`. 40 armies = 641 bytes, about 1.3 KB/s a client.
  No core protocol bump; `TfwRules.Version` and `MusouSim.Version` rise.

**Clash resolution (`TfwClash`, pure).** Lanchester square law with cohesion, stepped at 2 Hz between the aggregate
parts of the armies that meet (at a point, or within `ClashRadius` of each other): each side's losses per step are the
other side's `count × FirePerSecond × (CohesionFloor + (1 − CohesionFloor) × cohesion) × dt` with a seeded ±noise;
cohesion falls by `LossShock × (losses / start)` and by `EdgeShock × max(0, enemy / self − 1) × dt`, and recovers
slowly out of a clash; a side routs when cohesion reaches `RoutCohesion` or its losses reach `RoutLoss`. A side is
symmetric (one Ork in the aggregate is worth one Guardsman, so 200 v 200 is even, the brief's sizes). Real units fight
for real; the aggregate fire of one army only falls on the aggregate part of the other (no real unit dies to a number).
Monte Carlo test (1,000 seeds, 200 v 200, 500 v 500, 1,000 v 1,000): win rate 45-55 %, the winner keeps ≥ 25 %,
the loser routs before it is wiped out.

**Waves.** A stronghold is an Ork-held position (Held) or the HQ's source. The next wave musters (an army in Muster,
growing in the aggregate) at the Ork stronghold farthest from every human and departs at its peak: departures are
`PeakMin`-`PeakMax` (180-300 s) apart, the first after a lull of `LullMin`-`LullMax` (90-150 s); between peaks the
field is quiet for at least a lull. Size by seeded dice: 200 (70 %), 500 (25 %), 1,000-1,200 (5 %), plus the survivors
who came home since (they join it). Elites by the run's elite share. On departure the destination is the
Imperial-held position farthest from every human (random between the two farthest) and is **committed**: players moving
never changes it; only its loss redirects it (then the nearest other Imperial position; none: the wave goes home). With
no Imperial position the muster waits (re-checked every 5 s), so the opening is as before. No UI names the destination.
The army fans out along its way (rows widen with its count). At the point it attacks: real members are let loose as
one pack, the aggregate clashes with the garrison; the point's retake counts the aggregate Orks and Guardsmen exactly as
real ones (`StepPosition`), so a point falls in the same 20 s either way.

**Guardsmen.** Every 60 s a reinforcement leaves the base (the landing zone): `ReinforceMen` (60) plus the men who came
home, split over the Imperial positions by need (200 less stationed and inbound), in whole squads. A garrison (Hold) is
capped at 200 per position; an army arriving merges up to the cap and the rest goes on to the position most short, else
home: moving, reinforcing or retreating armies may exceed the cap while they move. A destination lost in transit →
another Imperial position (nearest), else the base. With no Imperial position nothing leaves (the field squads hold the
landing zone as before).

**Retreats and rallies.** Orks: losses ≥ 50 % of the army's start and no elite or boss within 100 m (its own elites
count, real ones of any army nearby too): it routs home to the nearest Ork stronghold and its survivors join the next
wave there. Killing its leaders therefore breaks a charge once the losses are met. Guardsmen: losses > 50 % of the
army's start (a garrison's start is its highest count since it was last full, rallied or reinforced) or its position
falls: home to the base, survivors join the next reinforcement. A human within 15 m of a retreating Guard army (its
footprint) rallies it, at most once per army per 60 s; it turns back to its position if still Imperial or to a nearer
Imperial position (else on home), its start resets to its count, and for 30 s it cannot retreat. A rallied army whose
goal falls is redirected like any other. Loops are closed by the 60 s rally lock, the 30 s no-retreat window and the
reset start (a new retreat needs half of what it has now to die).

**Six AI Marines (`MarineRole.Defender`).** Puppet ids widen to 12 (`PuppetIdFirst` 65516; `PointTargetId` keeps its
values). At the landing up to six Defenders come after the retainers and lords: `DefendersFor(lords) = min(6, 12 − 4
humans' retainers − 2 × lords)`, so the World never passes 16 players (node battle: 6; final assault with three lords: 2).
Each acts alone (no orders, no owner): it holds the Imperial position the Orks press hardest (an army attacking or
marching on it first, then the Orks near it), else the landing zone; it never adds capture progress. Dead, it respawns
at the landing zone 30 s later. The retainers are unchanged.

**Waaagh! barks (client).** `WaaaghBarkPlanner` (SmNet, engine-free, tested): Ork armies in Attack shout in bursts,
each army 4-8 s apart, nearest armies first, at most 3 Waaagh! voices at once, rotating over the Boy, 'Ard Boy and Nob
roar and taunt lines so two bursts in a row never repeat a voice; posted through `MusouSoundBudget` (a new Waaagh
group with a long range) at the army's front.

**Profiling.** `ArmyProfileTests` (Slow trait, bounded, off unless `SM_ARMY_PROFILE=1`): a World with a human at a
point, 200 v 200, 500 v 500 and 1,000 v 1,000 clashing there, 90 s each, server tick mean / p99 / max and the ticks of
the promotion and of the mass deaths.

## Verification plan
Unit/sim tests per stage (targeting, committed destination, blasts and the trap exception, Squig kick/fuse/chain,
Stormboy crash vs landing, Mek limits and cleanup, Librarian stress ratios, garrison cap, retreat and rally loops,
wire round trips). Monte Carlo clash test (1,000 seeds). Headless profiles 200/500/1,000 with mass deaths. Classic PvP
goldens unchanged. Gameplay captures through `godot.cmd --windowed`.
