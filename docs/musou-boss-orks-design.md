# Musou Mode: roaming Boss Orks and the Warboss model (design, 2026-11-06)

Added content, Musou Mode only (`--content musou`); nothing here touches Annihilation or Seize Ground. Status: **approved by the owner on 2026-11-06** with the changes in "The owner's answers" below, which override section 1 to 5 where they differ. Every number is kind: ours (or owner where he named it) and goes to the refine register.

## The owner's request (2026-11-06, verbatim)
"is it possible to add randomly spawning boss orks (they spawn with an elite retinue of elite ork enemies). the bosses have massively
incresased hp, damage, and are scaled up 3x from their usual size." and "the final boss should use a warboss model."

## The owner's answers (2026-11-06, in this session's chat)
- Base body: **any Ork type** (Boy, Shoota Boy, 'Ard Boy, Nob) at 3x, picked at random.
- Health 10x the type's, **at least 3000**; damage 2x. **Own attack only** (no slam or charge); melee reach x1.5 (ours).
- Retinue: **20 elites, fight on** after the boss dies.
- Frequency: **more often, up to 2**: battles only, the first 1 to 2 min after the landing, the next 1.5 to 3 min after the last one came.
- Final Warboss: **15000 HP, 1.5x damage** (swing 60, slam 60, charge 75): "the final boss should have even more health ... noticeably
  stronger than the 3x size elites". Drawn with the Warboss model **as tall as today's** boss.
- The battle commander: "**replace him with the warboss**": the HQ's boss after A, B and C fall is the Warboss (model, size, abilities and
  health as the final one, ours: the literal reading), still leashed at the HQ with his escort.
- Test runs: "skip the headless run as well as screenshots. i'll playtest all changes for this session manually."

## What exists today (read from the code)
| Who | Where | Body | Health | Extras |
|---|---|---|---|---|
| Elite squads | crowds, runs of 20 (`TfwBattle.EliteSquad`) | 'Ard Boy (75 %) or Nob (25 %), 1x | 170 / 600 | none |
| The commander (the "big boss" of a battle) | HQ, after A, B and C have fallen | Nob, 1x | 1500 x players scale | 1 Nob + 5 'Ard Boyz escort, leashed; boss bar |
| The final Warboss | courtyard outside the HQ, final assault | `nob_melee` drawn at `BossScale` 3 (sim radius 0.75, height 2.9, x3) | 4000 x scale | slam, charge, 3 Waaagh!s, enrage (`MusouBoss`); boss bar |

## Install evidence for the Warboss model (*supported by evidence*: archive listings of the owner's install)
- `warboss_regular.O3d` / `.dcm`, `warboss_head_regular`, textures `warboss_regular_dif/nrm/spc/lp`, rig `havok_warboss_regular__hkxx` (766 KB):
  in `data/chunks/ebp_sets__sequence2_core_units.oppc` (also `s008-001`, `s018-001`, `s022-002`). These are campaign sets: a singleplayer
  dependency, noted here and in docs/discovery.md as CLAUDE.md asks.
- `ork_coop_warboss.attr_pc` (+ `_power_klaw`, `_bullrush`, `_ranged_abilities`) in `data/packages/ebps.oppc`: a co-op Warboss blueprint exists.
- `cinematic_warboss` is the cutscene variant: not used.
- Risk: the Warboss has its **own skeleton**, not the boy's rig the Musou crowd uses. It is loaded the way `GuardRig` loads the Guardsman
  (its own `HavokCharacterFile`). If its packfile has too few clips (run, idle, attack, stagger, death), the missing ones fall back to a still
  pose; if the mesh cannot be built at all, today's Nob body at x3 stays as the fallback (a note in the client log).

## Proposed design

### 1. The Boss Ork (new enemy kind `BossOrk`, wire type 7 of 16)
- Base body: a Nob (`nob_melee`) at **3x** its usual size: sim radius 0.6 -> 1.8 m, height 2.3 -> 6.9 m; the client draws exactly that.
- Health: Nob 600 x **10** = 6000 (x the battle's player scale). Damage: the Nob's swing 25 x **2** = 50, still staggering; reach 2.2 x 1.5 = 3.3 m (ours: a 3x arm).
- Attacks: the Nob's swing plus the Warboss's **ground slam and charge** (reuse `MusouBoss` with its own rules: no Waaagh!, no enrage).
- Hyper armour: never staggered, knocked down or shoved by players; a heavy step does nothing; an artillery shell only scratches it (as the bosses today).
- Collision: the 1.8 m body pushes Orks and players aside as the Warboss's does; it walks the same flow field (like the 3x Warboss it can
  look clipped in narrow doors; the stranded failsafe moves it to a source if it is stuck).

### 2. The retinue
- **20 elites** (the owner's "squads of at least 20" rule): the battle's usual elite mix ('Ard Boyz 75 %, Nobs 25 %), normal size and stats.
- Formation: a ring of 3 to 6 m round the boss at the spawn; they follow the boss while it has no target (a leash to the boss, not to a place)
  and attack whatever it attacks. They count in the battle's alive cap and use the pool's reserve so a boss never spawns without them.
- After the boss dies they keep fighting as an ordinary swarm (no rout).

### 3. Spawning
- In battles only, not in the hub, the Firing Range or the final assault (the Warboss is the final assault's boss).
- Timing: deterministic from the battle's seed; the first boss **2 to 4 min** after the landing, then each next one **3 to 5 min** after the
  previous boss died. At most **one** alive at a time.
- Place: a random sending source (a held position's spawn point or the HQ source), at least 40 m from every living player, walked in from there.
- Announcement: the call-out "BOSS ORK INCOMING" (the Waaagh! call-out style) and an Ork roar; the boss gets the top boss bar while no commander
  or Warboss holds it (else a smaller bar under it); a "BOSS" marker on the M map.

### 4. Rewards
- 1000 points (the Nob's 100 x 10) and a guaranteed weapon drop if the drop system has one to give; a "BOSS ORK SLAIN" call-out.

### 5. The final Warboss uses the Warboss model
- The final assault's Warboss is drawn with `warboss_regular` (+ head) on its own rig, scaled so he stands as tall as today's x3 boss (his sim
  body, health and abilities unchanged). Fallback: today's Nob body x3.

## Wire and versions
- `MusouEnemyType.BossOrk = 7` (4 bits: room to 15); bars on tag 13 already cover elites. `MusouSim.Version` and `TfwRules.Version` +1; no
  protocol change expected.

## Tests (phase 2)
- Spawn rule determinism (same seed, same times and places), one-alive rule, no spawn in the final assault or hub.
- Scaling: body, health, damage and reach of the BossOrk; hyper armour (no stagger or knock).
- Retinue: 20 elites spawned with the boss, follow it, fight on after its death; the pool reserve.
- A headless Musou run on 127.0.0.1 with no exception; windowed screenshots in `%LOCALAPPDATA%\sm-reimpl\cache\previews\musou-bosses\`.

## As implemented (2026-11-06)
- Kinds `BossBoy`, `BossShootaBoy`, `BossArdBoy`, `BossNob` (wire types 7 to 10, no wire format change), numbers from the base by `MusouBossOrks.From`; speed capped at 4.4 m/s so the
  retinue keeps pace (ours). The retinue is free from the start and walks with its boss; there is no leash to the boss.
- No Boss Ork comes while nobody is in the field (ours); one due then comes as soon as someone is.
- The weapon drops are off in Musou Mode, so a Boss Ork's sure drop is an ammunition or equipment box.
- No headless run and no screenshots (the owner: "skip the headless run as well as screenshots. i'll playtest all changes for this session manually").
- Values and tags: spec/behavior.md "Musou Mode: Boss Orks and the Warboss model"; the Warboss model's files and clips: docs/discovery.md "Warboss model".
