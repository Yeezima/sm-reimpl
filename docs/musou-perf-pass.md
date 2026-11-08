# Musou performance pass (musou/perf-pass, 2026-11-08)

The owner's brief of 2026-11-08 (smooth the first explosion and mass deaths, simplify distant battle decisions, reduce invisible
rendering work, add FSR 2), stage S7 of `docs/musou-expansion.md` (profiling at 200 / 500 / 1,000, captures, Classic PvP
regression) and the owner's answers to S6's balance questions. Branch `musou/perf-pass` from `main` bf5d3319.

Labels: **measured** = numbers from runs on this PC listed below; **expected** = not measured, reasoning only.

## How it was measured

- PC: the owner's desktop (RTX 4070 Ti SUPER, Vulkan Forward+), quiet (no other build, test or Godot process; CPU 6 to 25 %
  from desktop apps). Client: the editor build (**Debug** C#, as `client.cmd` runs it; shipped builds are `--export-release`,
  so the client's CPU numbers here are higher than a player's), one window 1600 x 900 on the private test desktop, **vsync off**
  (`--disable-vsync`), MSAA 4x + SMAA (the default), lighting preset default. Server: Release, `--bind 127.0.0.1`.
- Harness: `tools/musou-perf.ps1` (new). Same scene, camera and workload each time:
  - `clashN`: `SM_TFW_ARMY_TEST=clash:N` on Ashfall (a garrison of N at the point nearest the human, an Ork wave of N sent at
    it, on top of the battle's own 850 to 1,500 Orks, Stormboys, Meks, Weirdboyz and six Defenders), the scripted player held
    35 m short, the camera on the nearest army (`--tfw-test-fight --tfw-fight-hold --tfw-fight-look-armies`), 150 s of fight.
  - `burst`: `--musou-death-spike-test 200` (200 Orks 8 m ahead die by one blast every 4 s, 15 bursts; burst 1 is the cold one).
  - A/B inside one run: `--perf-ab cells|grime|fxcull|fsr|fsr-native` switches one change on and off every 5 s window, so
    both states meet the same battle and load (the most trustworthy comparisons here).
- Client numbers: `--frame-profile` (whole frame on the main thread's wall clock; per-phase means and worst; Musou phases are
  new), `--perf-probe` (renderer CPU and GPU ms, draw calls, primitives). Server: `SM_TICK_STATS=5` with the new
  `TICKSECTIONS` (named sections), `TICKBYTES` (KB/s per client by message kind / envelope tag) and `TICKSPIKE` lines.
- Logs: `%LOCALAPPDATA%\sm-reimpl\perf\musou-perf\<tag>\<scene>\` (`base` = before, `after` = after, `ab-*` = A/B runs).

## Key numbers (measured, before = `main` bf5d3319, after = this branch)

Client frame times are the main thread's wall clock after a 20 s warm-up (join and loads excluded; the landing is inside).
Server ticks are per 5 s window from the clash's start (`mean` range over windows, worst window p99, worst single tick).

| Scene | | Client frame mean / p99 / p99.9 / worst (ms) | Renderer CPU / GPU (ms) | Frames over 33 ms | Server tick mean / p99 / worst (ms) | KB/s a client |
|---|---|---|---|---|---|---|
| 200 v 200 | before | 10.6 / 16.0 / 141.6 / 1,096 | 1.20 / 4.81 | 16 | 4.2-8.2 / 51 / 132 | 45-79 |
| | after | 9.1 / 13.8 / 20.5 / 443 | 0.92 / 1.97 * | 9 | 4.0-8.5 / 49 / 73 | 32-79 |
| 500 v 500 | before | 10.3 / 15.2 / 221.6 / 1,936 | 1.09 / 4.84 | 19 | 4.4-7.5 / 60 / 141 | 44-79 |
| | after | 10.4 / 16.5 / 21.4 / 459 | 1.34 / 4.19 | 8 | 4.7-8.3 / 64 / 110 | 40-79 |
| 1,000 v 1,000 | before | 10.9 / 16.4 / 225.8 / 2,334 | 1.19 / 4.55 | 23 | 3.2-7.0 / 50 / 137 | 44-80 |
| | after | 10.4 / 19.0 / 23.2 / 452 | 1.33 / 4.56 | 9 | 4.5-6.2 / 37 / 119 | 36-79 |
| Burst (200 deaths, 15x) | before | 10.9 / 23.3 / 26.7 / 1,827 | 1.50 / 4.65 | 3 | - | - |
| | after | 9.5 / 21.6 / 25.0 / 28.8 | 1.51 / 4.65 | 0 | - | - |

\* The 200 v 200 after-run's camera spent more of its time facing fewer armies: its renderer numbers are not comparable;
the rendering changes are judged by the A/B runs below, which share one battle. The battles before and after also differ
by design (the owner's balance decisions change who wins near the player), so their tick means are not like for like.

**First explosion / mass death (burst scene, main thread CPU per frame, 200 Orks dying at once):** cold burst 29.4 ms
before, 26.6 ms after (28.4 in a second after-run); warm bursts' worst frame median 26.4 -> 23.5 ms against quiet frames of
8 to 9 ms; worst burst frame 1,560 ms -> 27.3 ms. The previous notes' 72 ms cold first burst does **not** occur any more in
this scene (the night batch's ragdoll dressing and hand-out already removed most of it); what was left of the cold
penalty is about 3 ms.

**A/B in one battle (`--perf-ab`, 5 s windows alternating, 200 or 500 v 500):**

| Change | Off | On | Verdict |
|---|---|---|---|
| Crowd far batches per 32 m cell (500 v 500) | GPU 5.24 ms, 45.9 M primitives, frame 9.44 ms | GPU 4.58 ms, 29.0 M primitives, frame 9.59 ms | kept: -13 % GPU, -37 % primitives; frame time unchanged (CPU-bound PC) |
| Ashfall grime overlay pass (200 v 200, two runs) | GPU 1.76 / 2.93 ms, 652 / 1,021 draws, 4.5 / 12.6 M primitives | GPU 2.26 / 4.12 ms, 844 / 1,601 draws, 8.6 / 23.7 M primitives | not changed (see below) |
| Off-screen effects not evaluated (200 v 200) | frame 8.90, p99 16.7 | frame 9.52, p99 20.0 (noise); ~600 effects/s skipped | no gain shown: left as an opt-in trial (`--fx-cull`) |
| FSR 2 Quality vs MSAA 4x + SMAA (200 v 200, two runs) | GPU 4.21 / 3.73, render CPU 1.48 / 1.12, frame 10.90 / 7.58 | GPU 3.50 / 3.31, render CPU 1.65 / 1.42, frame 11.70 / 9.24 | added, default off: GPU -11 to -17 %, render CPU +0.2 to 0.3 ms |
| FSR 2 Native AA vs MSAA 4x + SMAA (two runs) | GPU 4.27 / 3.89, render CPU 1.45 / 1.28, frame 9.49 / 8.43 | GPU 4.00 / 3.93, render CPU 1.60 / 1.46, frame 10.23 / 9.07 | added, default off: GPU about equal, render CPU +0.15 ms |

## What changed, by item

### 1. First explosion and mass deaths (measured)
- **The real cause of the 1 to 2 s hitches in every Musou battle:** a thrown body's tumble quaternion drifted off unit
  length during its roll-out (`Slerp` without a normalise); Godot's quaternion product then threw an exception out of the
  whole Musou frame. 421 to 767 exceptions per 2.5-minute run, each printing a stack trace; frames of 1.1 to 2.3 s. Fixed
  (`CrowdView.StepFlung`); 0 exceptions after. This is most of the p99.9 and worst-frame gain in the table.
- **Server 138 ms tick a few seconds after every landing:** the walk graph's edge-distance spread was computed lazily in
  the first Scrap Mek's hide search. Now computed on a worker when the graph loads (`MusouNav.PrepareEdges`); same values.
  The worst landing tick fell from 132-141 ms to 73-119 ms (what is left is the landing's own spawning: crowds 26-29 ms,
  Guard squads up to 28 ms, the core step).
- **Client 430 ms frame at the landing:** the Guardsmen's look, crowd and effects were made when the first men came into
  sight. They are now made from the war's first frame in the Stormbird (`TfwGuardsFrame`). Measured after: the landing
  still has one 420-460 ms frame, now shown to be in the HUD, stations and markers' first battle frame (`tfw: orders,
  stations, hud, markers`); not yet split or fixed.
- Prewarm checked and found not needed: effect systems are compiled at load already (FxPlayer.Preload), the ragdoll pool is
  dressed ahead (night batch), the cold burst costs only about 3 ms more than a warm one (26.6 vs 23.5 ms), so no shader,
  material or audio stall large enough to prewarm was found. Sounds decode on workers; their first loads were not a measurable share.
- Not changed: the remaining 15 to 20 ms above a quiet frame in a 200-death burst is the engine side (20 ragdolls in the
  physics, about 1.5 ms a frame on average and more in the burst, the effects' update of 200 simultaneous death effects up to
  18 ms in this Debug build) and the hand-out already spreads deaths over 5 frames nearest first. Reducing it further would
  mean fewer or cheaper death effects or ragdolls: a visible change the brief does not allow without the owner.

### 2. Distant battle decisions (measured)
- The far layer is already cheap: the army sections cost 0.00 to 0.01 ms a tick on average (worst 3 ms in a landing tick)
  at 200, 500 and 1,000 a side; a tick's 4 to 6 ms are the Orks' own AI (~2 to 2.5 ms for 1,100 to 1,500 real Orks), the
  Guardsmen (0.2 ms) and the core step. Co-op proximity is already per human (each human's distance to each army).
- **Route-search stall:** the battle's army and patrol route searches now run side by side on workers (each search's
  scratch is per thread); the ways and the tick they are taken at are unchanged, so a battle is still deterministic.
- **Send rate:** measured by message kind (`TICKBYTES`): the Musou crowd (tag 3) 33-52 KB/s, the Guardsmen (tag 9) 2-16 KB/s,
  the core snapshot (kind 5) 12.8 KB/s, the rest under 5 KB/s. Musou's and the war's sections keep their 58 KB/s budget;
  the 72-80 KB/s total is that budget **plus** the core snapshot of 16 players (four humans' retainers and six Defenders
  are World players), which no budget counts. Not changed (a decision for the owner, below).
- The owner's balance decisions (next section) were built here.

### 3. Invisible rendering work (measured)
- **Crowd batches per cell (kept):** one batch per look covered the whole field, so its box was the field and every far Ork
  was drawn in every view and every shadow cascade. Batches are now per 32 m cell with a box (`CrowdView.AddFar`).
- **Grime (measured, not changed):** the overlay pass costs 0.5 to 1.2 ms of GPU, +190 to +580 draw calls and doubles the
  kit's primitives in a battle view; the paired captures show it carries much of Ashfall's look (the plaza turns near black
  without it), so it cannot simply go. Folding it into a derived material would save most of that, but the frame on this PC is
  bound by the main thread (GPU 2 to 5 ms of a 9 to 11 ms frame), so it shows no frame-time gain here, and the derived
  shader would have to reproduce a lit blend over a lit surface plus an unshaded multiply, which is not exact. Left as it is;
  worth doing for weaker GPUs if a playtest asks.
- **Off-screen effects:** implemented and measured; no gain beyond noise; left off (`--fx-cull` to try).
- **Far and unseen deaths leave no body** (the owner's rule, below): fewer bodies to step and draw away from the player.

### 4. FSR 2 (measured cost; look checked in captures)
- Options > Graphics > **Upscaling**: OFF (default) / FSR 2 NATIVE AA / QUALITY / BALANCED / PERFORMANCE, saved in
  `frontend.json` (`upscaling`). Godot's built-in FSR 2.2, Forward+ only: on the Compatibility renderer the row is disabled
  and its tooltip says why. While on: FSR 2 is the anti-aliasing (MSAA, SMAA and TAA off, roughness limiter on; the
  anti-aliasing row and the render-scale slider rest), the HUD and menus stay at native resolution. `--fsr <mode>` for tests.
- Default off, so Classic PvP and every existing profile look and run as before.

## Owner balance decisions (2026-11-08)

| Decision | What was built | Measured |
|---|---|---|
| Near matches far | army Guard squads made real near a player fight at 0.135 of the field Guardsmen's 4.5x (0.61x the install) | 200 v 200 near: Guardsmen held 4 of 8 bearings (before: 8 of 8, every Ork dead in 13 s, no Guardsman lost); 500 v 500 near: still 8 of 8 (Guardsmen kept 7 to 64 %); far clash: 511 of 1,000. Perf: no tick change (same units) |
| Fight off-screen | a near army's remainder beyond the real caps clashes in the far layer, nothing drawn; the real units give it their fire's share and pay only its share of losses; the remainder's rout no longer sends a whole near army home | tick sections unchanged (clash 0.00 ms mean) |
| Base is a target | with no point held, waves march on the base | first wave on the test arena: left at 144 s, reached the base at 226 s (time for the players to deploy and push out) |
| Far corpses vanish | far (> 90 m) and unseen deaths leave no body, ragdoll or gore; an army aggregate's loss leaves a body only within 90 m | fewer bodies away from the player (not separately timed) |

## Classic PvP regression

Gameplay: the Classic PvP code paths are unchanged (the Musou sim changes live in Musou-only files; the shared changes are
the server's `SM_TICK_STATS` sections, which do nothing without the variable, the `--frame-profile` phase names, the
default-off FSR 2 setting and the default-off `--fx-cull` trial). Full `dotnet test` (MusouIsolationTests, the Classic
config-hash and protocol pins, every PvP test): 5,664 of 5,665 passed in each of two full runs; the one failure each time was a different background-planning navigation test with a wall-clock wait (`NavigationTests.Background_planning_...`, then `NavJumpTests.A_background_plan_...`), both while another session's test sweep held the CPU at 99 %; both test classes pass when run alone (37 of 37), and `src/SmSim/Navigation.cs` is unchanged by this branch.

Frame and tick on Manufactorum were **not** measured: from 12:17 another session ran a test sweep (`GuardHoldTrialTests`
in another worktree, CPU at 99 %), so no further timing on this PC was clean. Expected: no change (the only shared
runtime code that differs is behind default-off switches).

## Captures

Saved outside the repo under `%LOCALAPPDATA%\sm-reimpl\shots\musou-perf\`:
- `fsr-quality\` and `fsr-native\`: pairs from one battle, `NN-before-<state>.png` just before a switch and the next
  `NN+1-after-<state>.png` 0.6 s after it (same view): MSAA 4x + SMAA against FSR 2 Quality / Native AA. Seen: the same
  light, haze, ash and blast; FSR 2 Quality slightly softer on far edges; the HUD equally sharp. Not seen in stills:
  ghosting on fast motion, ash, blood or lightning (needs a short capture or a playtest).
- `grime\`: the same pairs with the grime overlay on and off (it is on in the game).
- `s7-clash200\`, `s7-clash500\`, `s7-clash1000\`: the S7 captures of the after runs (landing, then every 30 s).
- Not usable: an earlier set of unpaired captures overlapped a second run on the same port and was deleted.

## Tradeoffs, blockers, open questions

- The client was measured as a Debug editor build (how `client.cmd` runs it); a shipped `--export-release` build is faster
  on the CPU side. GPU numbers are the same either way.
- FSR 2 costs about 0.15 ms of renderer CPU and saves 0.3 to 0.7 ms of GPU here; on this CPU-bound PC the whole frame does
  not get faster. It helps GPU-bound players (high resolutions, weaker GPUs).
- 500 v 500 near still favours the Guardsmen (their remainder's far fire against real Orks); one strength value cannot even
  out both sizes. Waves of 200 (70 % of waves) are now about even near a player.
- The per-client send of 72-80 KB/s: Musou and the war keep their 58 KB/s budget; the core snapshot of 16 players adds 13
  KB/s. Options: count the snapshot in the budget (far Orks then updated less often), or leave it (600 kbit/s a client).
- After the pair runs, a further 200 v 200 run (`after2`) overlapped another session's test sweep (from 12:17) and is
  not used.
- The grime overlay costs up to 1.2 ms of GPU: folding it into derived kit materials is the largest render saving left,
  for GPU-bound players; not done here (not exact, and no frame gain on this PC).
- The "camera" phase costs about 2 ms a frame on Ashfall (the rig's sphere casts against the large map mesh); shared with
  Classic PvP, so not touched here.
- Landing ticks still reach 73 to 119 ms on the server (spawning the battle); a one-off per battle.

## Follow-up: the owner's answers (2026-11-08)

| Answer | What was built | Measured |
|---|---|---|
| Near balance: "scale with size" (later replaced: see the integration below) | the near Guard share is set by the army's size (`TfwArmyRules.NearGuardStrength(men)`): 0.135 at 200, 0.072 at 500, 0.087 at 1,000, straight between on the log of the size | `NearFarBalanceTests` (8 bearings, 180 s, the rule): 200 v 200 held 4 of 8; 500 v 500 held 5 of 8; 1,000 v 1,000 held 4 of 8 (calibration: 500 at 0.065 / 0.08 gave 3 / 5 of 8; 1,000 at 0.065 / 0.08 / 0.095 gave 2 / 3 / 7 of 8). Far clash 511 of 1,000 at every size |
| Send budget: "count it" | in a Musou match the core messages (snapshots, scoreboards, bars, pickups) come out of the client's Musou send bucket each tick (at least 0.3 of the refill stays); the war's bucket then has what Musou and the core left of its 58 KB/s. Classic PvP counts nothing (`SM_MUSOU_OLD_CORE_BUDGET=1` restores) | 500 v 500 on Ashfall: **59 to 62 KB/s a client** (before 72 to 80): crowd (tag 3) 31 to 33 KB/s (was 33 to 52), core 13 KB/s, the rest under 7 |
| Grime: "option in Graphics" | Options > Graphics > Ashfall grime: EXACT (OVERLAY) default, FAST (FOLDED, WEAK GPUS): derived kit shaders with the grime and the multiply worked into albedo and roughness; surfaces with other features keep the overlay. Close, not exact | 500 v 500 A/B: GPU 4.12 -> 3.78 ms, draw calls 1,632 -> 1,200, primitives 25.0 M -> 16.4 M; frame time unchanged here. Pair: `%LOCALAPPDATA%\sm-reimpl\shots\musou-perf\ashfall-grime-EXACT.png` and `ashfall-grime-FAST.png` (the plaza a little lighter and less blotched with FAST); all pairs in `grime-fold\` |
| Landing hitch: "ship now, fix after" | not worked on | see "Next item" |

From the independent review: the FSR option's tooltip now says cut-out edges (grates, fences) follow the change on the next
map load. **FSR 2 over the vertex-animated crowd needs the owner's check in motion:** the crowd's VAT shader writes no
motion vectors of its own animation, so far Orks could ghost or smear under FSR 2's temporal reconstruction; only stills
were checked. FSR 2 stays off by default.

## Next item

The landing: one 420 to 460 ms client frame in the HUD, stations and markers' first battle frame (`tfw: orders, stations,
hud, markers`), and 73 to 119 ms server ticks while the battle spawns (crowds 26 to 29 ms, Guard squads up to 28 ms, the
core step). The owner: "ship now, fix after".

## Integration with expansion S6's answers and the S4 follow-up (2026-11-08)

Merged `claude/admiring-swanson-fe6c45` (S6: 414766bc, d43978f4) and `claude/elegant-torvalds-092dba` up to 01e56dca (S4
follow-up: traps kill AI Marines, the shield dome stops Guard fire, Ork blasts, rockets and grenades through the new core
hook `ExtrasProjectileStop` in `Projectiles.Fly`, Guardsmen shoot Rokkit turrets). The owner's picks:

| Overlap | Kept | Dropped |
|---|---|---|
| Near balance | S6's: the Guardsmen 0.875x the install, a Guardsman worth an Ork in the far clash (`TfwClashRules.GuardWorth` 1) | this branch's size-scaled near-Guard share (0.135 / 0.072 / 0.087) and its `GuardSim` strength use |
| Far bodies | this branch's: deaths beyond 90 m and out of view leave no body, ragdoll or gore; an aggregate's Ork losses leave bodies only within 90 m | S6's Guard impostor bodies (`GuardCrowd.AddImpostorBody`) |
| Base target | this branch's: waves march on the base when the players hold no point | S6's "only once A, B and C are all Imperial" |
| Remainders fighting near a player | S6's rule (real units a human can see take the aggregates' fire, put out none there), with this branch's rout fixes (a remainder's loss share is the army's; a routed remainder of an army with real men does not turn it home; under one man no remainder) | this branch's fire-share split |
| Route stall | this branch's: searches side by side, taken at the fixed 5 s tick (deterministic) | S6's "taken when done, never waited for" (wall-clock dependent, not reproducible) |
| Waiting waves | S6's creep (after 2.5 s a waiting wave goes on at 0.6 of its pace) | - |

`TfwRules.Version` **40** (38 S4 follow-up, 39 S6's answers, 40 this integration); `MusouSim.Version` untouched (19). The
new core hook is inert outside Musou: its stop stays infinite unless a Musou battle has a dome (`TfwMeks.AnyDome`); the
Classic PvP tests pass.

**Trials after the merge (Release):** S6's `GuardHoldTrialTests` (60 Guardsmen v 60 Orks): held **10 of 20** (S6's
target). This branch's army trials near a player (`NearFarBalanceTests`, a garrison and a wave of N at a point, 8
bearings): 200 v 200 held **8 of 8** (Guardsmen kept 64-70 %), 500 v 500 **8 of 8** (67-79 %), 1,000 v 1,000 **8 of 8**
(65-75 %); far clash 511 of 1,000 at every size. So with S6's balance a near army clash at a point still goes to the
garrison every time (the garrison holds the point and fights from it; the wave's real Orks come in a stream): the owner's
call whether that is wanted. Base target: the first wave left at 144 s and reached the base at 226 s.

## Follow-up: "leave as is" and "make FAST darker" (2026-11-08)

- Near army clashes: the owner, "leave as is" (with S6's balance the garrison holds a point near a player every time; no change).
- Ashfall grime FAST darker: where the grime covers, the folded surface loses its specular (the overlay had none), and floors and tops get a further 0.82 (`grime_floor_dark`). Mean luminance (sRGB, Rec. 709 weights) of the same regions over 19 paired captures, same camera within each pair (0.6 s apart):

| Region | Before the tune: EXACT / FAST | After: EXACT / FAST |
|---|---|---|
| Plaza (floor left and right of the player) | 76.9 / 86.5 (+12.4 %) | 76.9 / 75.3 (-2.1 %) |
| Ruins band | 83.2 / 83.4 (+0.2 %) | 65.1 / 64.6 (-0.6 %) |

  (The before and after runs were separate battles, so their views differ; each comparison is EXACT against FAST in the same view.) Perf kept: 500 v 500 A/B after the tune, GPU 4.18 -> 4.06 ms, draw calls 1,742 -> 1,319, primitives 25.7 M -> 17.8 M (the first run's GPU saving was 0.34 ms; this one 0.12 ms: different views). New pair: `%LOCALAPPDATA%\sm-reimpl\shots\musou-perf\ashfall-grime-EXACT.png` and `ashfall-grime-FAST2.png`; all pairs in `grime-fold2\`.
- The full `dotnet test` was skipped this once at the owner's request; the lead runs it after merging.
