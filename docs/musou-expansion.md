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
- Psychic Ward: future idea only, not built.

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

## Verification plan
Unit/sim tests per stage (targeting, committed destination, blasts and the trap exception, Squig kick/fuse/chain,
Stormboy crash vs landing, Mek limits and cleanup, Librarian stress ratios, garrison cap, retreat and rally loops,
wire round trips). Monte Carlo clash test (1,000 seeds). Headless profiles 200/500/1,000 with mass deaths. Classic PvP
goldens unchanged. Gameplay captures through `godot.cmd --windowed`.
