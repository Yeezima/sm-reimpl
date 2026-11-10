# Polish pass: audit, baseline and plan (2026-11-10)

The owner's request (verbatim): "make a pass improving all animations, visual effects, weapon behavior and sound and visual effects,
netcode optimizations, Performance optimizations, applying the gpu particles, anything else you can think of that will make experiencing
the downloadable playtest release feel more polished and responsive, beautiful and tight feeling."

His rules for this pass (2026-11-10, through the lead):

- **Classic PvP** (Annihilation, Seize Ground) stays faithful to the 2011 original: only netcode, performance, smoothness and bug fixes,
  plus changes that bring it closer to the original. No new look or feel.
- **Musou Mode** (added content, `Extras/`, behind the Musou gate) gets full creative polish.
- Animations: new clips only for the claw and spear (another agent). Everything else: blending, timing and transitions only.
- Feel: Space Marine 2011 systems and animations with a bit of DOOM Eternal (snappy, readable); Space Marine 2 crowds and effects (Musou).
- Four priorities, equal: responsiveness, smooth frame rate, impact and feel, visual beauty.
- Hardware: push the look on a recent PC (the owner's is the baseline); Compatibility mode (`SafeMode`, the old-GPU renderer) stays smooth
  on GTX 9xx and newer; a steady 60 fps in a 1,000+ Ork Musou fight on a mid-range PC, effects scaling down before the frame rate drops.
- Sound: SM1's own sounds, a punchier and clearer mix, less clutter in big fights; code-made layers only where something is missing.
- Cadence: small reviewed steps, merged as they land; frequent playtest builds.

Phase 1 (this document): audit, measure, plan. No product code; one test aid (`tools/polish-pvp-baseline.ps1`).

Labels: **measured** = numbers from the runs in section 1; **read** = from the code or the install's data; **expected** = reasoning only.

## 1. Baseline (measured, main 4f4b63f5)

### Conditions

- PC: the owner's desktop (Ryzen 5 7600X, 6 cores; RTX 4070 Ti SUPER; Forward+, Vulkan). **Not quiet**: other sessions' builds and test
  sweeps held the CPU at 65 to 95 % during the Musou runs and the first PvP minute; the numbers are an upper bound of a quiet PC.
- Client: the editor build (Debug C#, as `godot.cmd` runs it; the shipped `--export-release` build is faster on the CPU side), windowed
  1600 x 900 on the private test desktop, vsync off, MSAA 4x + SMAA, lighting medium, `--no-sound`. Server: Release, `--bind 127.0.0.1`,
  `SM_TICK_STATS=5`.
- Classic scene (`tools/polish-pvp-baseline.ps1 -Tag polish-base2`): Annihilation on Manufactorum, 6 + 6 headless bots
  (`bots.cmd`'s per-side limit) plus one full-load bot client that measures: **13 players**, not 16 (an 8v8 needs two more bots than the
  aid starts; the headless bots alone put a 6-core PC near saturation). 120 s on Forward+, then 60 s on the Compatibility renderer in
  safe mode (same server and bots).
- Musou scenes (`tools/musou-perf.ps1`): `clash1000` (a garrison of 1,000 and a wave of 1,000 on Ashfall on top of the battle's own
  Orks, 90 s, camera on the armies) and `burst` (200 Orks killed by one blast every 4 s, 15 bursts).
- Logs: `%LOCALAPPDATA%\sm-reimpl\perf\polish-pass\polish-base2\pvp\` and `%LOCALAPPDATA%\sm-reimpl\perf\musou-perf\polish-base\`.
  No captures were taken in this phase (`<media folder>\previews\polish-pass\` is reserved for the implementers' before / after clips).

### Client frame time (after a 20 s warm-up)

| Scene | mean / p50 / p99 / p99.9 / worst (ms) | frames over 33 ms / over 16.7 ms | renderer CPU / GPU (ms) | draw calls | alloc / frame, GC gen0 per 100 s |
|---|---|---|---|---|---|
| Classic 13 players, Forward+ | 11.5 / 11.8 / 18.6 / 25.2 / 90.6 | 2 / 118 (of 8,964) | 2.80 / 1.98 | 1,925 | 192 KB, +104 (gen2 +3) |
| Classic 13 players, Compatibility + safe mode | 13.2 / 12.3 / 24.1 / 50.4 / 156.6 | 7 / 461 (of 3,268) | 6.05 / 4.38 | 1,693 | 279 KB, +45 (gen2 +3) |
| Musou clash 1,000 v 1,000 (loaded PC) | 10.3 / 8.2 / 17.3 / 75.8 / (landing) | 45 / 73 (of 12,976) | 1.06 / 3.04 | 800 to 1,162 | 180 to 230 KB, +115 (gen2 +1) |
| Musou burst (200 deaths every 4 s) | 18.1 / 10.9 / 47.5 / 187.8 / (landing) | 118 / 999 (of 4,882) | 1.62 / 4.29 | 880 | 305 KB, +44 (gen2 +4) |

Where the Classic frame goes (Forward+, phase means): `between frames` (the engine: rendering, physics, the other nodes) **7.1 ms**
(worst 83 ms), `effects` **2.56 ms** (worst 35.7 ms), `remote players: place` 0.44, `camera` 0.18, everything else under 0.15 ms. The
engine's share holds render CPU 2.8 ms at 1,925 draw calls (the map's 1,957 objects drawn one by one) and a physics step of 1.65 ms with
**0 active bodies**. In safe mode the renderer's CPU side doubles (6.05 ms): the Compatibility renderer pays per draw call.

Musou burst (`MUSOUSPIKE`): the worst burst frame **50.4 ms** median over 14 warm bursts (most 56.9; the cold burst 53.4), of which the
Musou code itself is **7.3 ms** (deaths 0.7 to 2.1, crowd 5.8, ragdolls 0.8) and **44 ms is "the rest of the frame"**: the effect
player's update (`effects: update avg 2.99 ms, max 40.2 ms`) and the GPU sprays' uploads. The GPU spray path reports **30,333 sprays
dropped by a full alpha pool** in 60 s, 153,641 sprays started, 200 MiB of record uploads and 210 MiB of frame uploads a minute, each
through `Image.CreateFromData` + `Texture2DArray.UpdateLayer` (32 KB a layer). This matches the gpu-particles review (63.6 vs 53.4 ms).

### Load and first frames

| What | Measured |
|---|---|
| Classic map load (Manufactorum), one frame | **3.5 s** on Forward+ (collision and content 1.47 s, visuals 1.07 s, looks 0.92 s; GC pause 146 ms, gen2 +6), 2.9 s in safe mode. The loading screen is a still picture during it (the main thread is blocked; the keep-alive thread covers the server timeout) |
| Classic first frames after the join | 229 ms (effects 83, own model 33, own weapons 26), 124 ms, 55, 35, 41, 59 ms within the first 1.2 s; safe mode: 95, 158 ms, then **348 and 353 ms in "between frames"** (first-use shader compiles on the OpenGL renderer), then 65 to 58 ms |
| Musou join (Ashfall) | **27.5 to 28 s in one frame**: `ashfall: City layout generated in 22,989 ms` (deterministic, hash 1E3B60672DF1809B, "nothing read from the install", on the main thread, every join; the server generates it too), ragdoll world 925 ms, the rest 3 s. The watchdog reports the stall every 2 s; a tester sees LOADING for half a minute |
| Musou landing frames | 743 ms (effects 260, crowd 151), **400 ms** (`tfw: orders, stations, hud, markers`, known: "ship now, fix after"), 106 ms (ragdolls 92), 99 (crowd 44), 99 (`tfw: fire, clouds` 49), 83, 73, 55 ms; about 20 frames over 33 ms in the landing's first 3 s |
| Weapon models, looks, collision warm-ups | 10 default-loadout models 203 ms, 7 looks 99 ms, pickups 129 ms (one ammo case 127 ms), collision queries 1.6 ms: the M40-T9 warm-ups work |

### Netcode (loopback, 13 players, `NETLAT` and `TICKSTATS`)

| Stage | Measured |
|---|---|
| Input slack on the server (ms an input waited before its tick) | mean 43 to 50 (p90 46 to 55), the lead at exactly 1 tick: protocol 65's steering sits at its target |
| `press-apply` (a sampled input to the tick that applies it: half RTT + lead) | **53 to 59 ms** mean (p90 57 to 65) on loopback; RTT 15 to 24 ms (an upper bound: it includes the server's and the client's waits) |
| `shot-hit` / `marker` (own shot to the server's hit report / the shown marker) | 52 to 64 ms / 57 to 68 ms (the predicted marker shows on the shot's frame for hit-scan weapons; these samples are a bot's, which reports the server's) |
| `interp` (remote delay) / `remote` (age of what is seen) | 50.0 to 55 ms / **58 to 67 ms** mean on a clean link; 85 to 100 / 96 to 106 when the loaded PC jittered the snapshots |
| `edge-draw` (key edge to the own drawn marine moving) | **not measured**: bots make no key edges; needs a human client (`--net-latency`, `--latency-probe`) |
| Snapshot bytes | 735 to 870 B a snapshot at 13 players, **22 to 25 KB/s a client** (16 players, read: up to 1,200 B, about 36 KB/s); kind 5 (snapshots) is 99 % of it. Musou clash: **111 to 120 KB/s a client**, tag 3 (the crowd) 66 KB/s, tag 4 15, core 10.6, Guardsmen 8.6 |
| Server tick | Classic: **0.56 to 0.73 ms** mean, p99 0.9 to 5.4, worst 9.9 ms; Musou clash: 5.7 to 6.9 ms mean (Ork AI 3.4), p99 7.3 to 14.2, worst 19.9 in the fight; the landing **602 ms** (51 ticks skipped) |
| Server pacing | p50 33.3 to 33.6 ms between ticks; during the 13 joins p99 160 to 222 ms (max 349) and `TICKSPIKE tick 187: 64.9 ms` (world step 47.6: first-use JIT); one 1 s stall of the whole PC near 105 s while the second Godot started (the measuring client's lead went to 24 to 41 ticks and recovered) |

Interpretation (read): with a 30 Hz tick the client can do little more for its own inputs (edges run an early tick in their frame; the own
marine is drawn ahead); what is left is the tick's sampling (0 to 33 ms for held movement), one tick of lead and the remote interpolation
delay of 1.5 snapshot intervals. Snapshots are full state every tick, not deltas (`Protocol.cs`: `MaxSnapshotBytes` 1,200 with 13
projectile and 4 to 24 shot records, culled by distance per recipient); inputs carry 8 redundant copies of `F32` fields (about 7 KB/s up).

## 2. Audit: where the polish gaps are

### 2.1 Performance (both modes unless marked)

- **Musou join of 28 s** (measured): the Ashfall layout is generated on the main thread at every join and on the server at start; it is
  deterministic by hash, so it can be cached on disk or at least generated on a worker behind a loading screen that moves.
- **Classic join of 3.5 s in one frame** (measured): collision, content, visuals and looks are built synchronously. The loading screen
  cannot animate, the watchdog and keep-alive exist only to survive it.
- **First frames** (measured): the first effects frame, own model and weapon rigs still cost 83 + 33 + 26 ms; in safe mode the
  OpenGL shader compiles cost two 350 ms frames at the first sight of each material (Forward+ precompiles pipelines, Compatibility does
  not).
- **Draw calls** (measured 1,925; render CPU 2.8 ms, 6.05 ms on Compatibility): the map's objects are drawn one by one; the same
  meshes and textures merged by material would cut the CPU side of rendering by two thirds, most on the GTX 9xx class.
- **Effects update 2.56 ms mean, 36 to 40 ms worst** (measured, both modes): the CPU effect path evaluates every particle on the main
  thread each frame; particles are pure functions of age (`FxPlayer` class doc), so the evaluation parallelises.
- **Physics step 1.65 ms with no active bodies** (measured, Classic): the engine's physics world ticks at 60 Hz for ragdolls that are
  not there.
- **GC**: 192 to 305 KB allocated a frame; gen0 every second, gen2 a few a minute; GC pauses of 18 to 54 ms inside hitch frames
  (`profile ... gc this frame ... pause +N ms`). The top allocating phases are the remote players' model and look updates (24 to 27
  KB/frame), the network update (16.5) and effects (16).
- **GPU sprays** (measured, Musou): the burst's extra 40 ms are the upload path (an `Image` per dirty 32 KB layer per frame) and the
  CPU side of 200 sprays starting at once; the alpha pool overflows (30 k dropped sprays a minute, which is missing blood). The review's
  finding stands: the GPU path is not yet a frame-time win in bursts.
- **Server** (measured): the landing tick 602 ms and the first-join spike 65 ms; `workstation GC, Interactive` latency mode; the loop
  runs unoptimised code until tiering kicks in (no ReadyToRun). The tick itself is cheap (0.6 ms Classic, 6 ms Musou).
- **Camera casts** (read, from the perf pass): 2 ms a frame on Ashfall (sphere casts against the big generated mesh), 0.18 on
  Manufactorum.

### 2.2 Netcode

- Protocol 65 holds its target: lead 1 tick, slack 43 to 50 ms, remote age 58 to 67 ms, extrapolation seldom (read + measured).
- Full snapshots every tick; no delta against the last acknowledged state; shot and projectile records capped per packet (4 shots
  guaranteed, 13 projectiles) and culled by distance in busy fights (read: `GameServer.ShotsFor`, `ProjectilesFor`). A delta scheme
  would halve the bytes and lift the caps.
- The Musou crowd section sent 66 KB/s in the 1,000 v 1,000 test against the 58 KB/s budget the perf pass set (measured; the budget
  rule may not count the `SM_TFW_ARMY_TEST` wave: to check).
- Missing for testers: no ping / loss / interpolation readout in the HUD (the scoreboard shows ping only).
- A 60 Hz tick would cut `press-apply` by about 25 ms but changes every tick-quantised timing and the config hash: not faithful-safe,
  an owner decision only (section 6).

### 2.3 Animation blending, timing and transitions (read; data from `smtool graph-dump --melee-flow` / `--locomotion`)

- The graphs' entry blends are **0.1 s** (first heavy strike, the axe's lights) or **0.167 s** (first light strike); the client floors
  every smooth entry at **0.1875 s** (`CharacterViewer.Transitions.cs:44`, `SmoothMinEntryBlendSeconds`) and strikes and reactions at
  **0.125 s** (`WeightFeel.StrikeEntryFloor`). Every 0.1 s entry plays 25 to 88 % longer than the original's.
- Chain steps the graph marks as no blend (the knife's chain, the axe's light 1 to 2 and 2 to 3, many heavy to heavy) get `MeleeChain`
  0.1 s (`ClipBlends.cs:40`) and then the floor.
- Exits: the data gives 0.333 s (chainsword, power sword), 0.4 / 0.1 s (axe), 0.333 / 0.1 s (hammer); the client uses one
  `ActionExitBlend` 0.2 s (`NetworkView.SprintEdges.cs:189`) and "hit reactions leave over 0.2 s" (ours).
- Gait time constants (`IdleBlendSpeed` 1.0 m/s, `WosIdleBlendSpeed` 0.35, smoothing 0.05 / 0.06 s) are ours; the data gives the speed
  bands (0, 2, 4.5 / 5.5 / 6.5 m/s) and a walk-on-the-spot clip for the zero band.
- Transition clips the install has and the client never plays: `SM_TRN_*_180Turn_to_Sprint`, `SM_NAV_Bullrush2Idle_F_HalfSpeed_01`, four
  of the six hit-reaction directions, `SM_REC_Sprint_Stagger`, `SM_REC_Stun2Idle_D_01` (the musou-weight plan lists them for Musou; for
  Classic they are the original's own transitions where the graph wires them).
- Pose pops at weapon-ready, idle, melee and sprint switches were measured by the musou-weight pass (jerk p90 49 to 108 cm/frame², a 106
  degrees a frame body turn at move to ready). The Classic fix is the data's blend times above, not new smoothing.

### 2.4 Visual effects

- Classic: by the owner's rule nothing new; the effect caps (160 live, 48 + 24 decals, 6 lights, 3,072 quads a batch) are ours and
  were never hit in the 13-player run (peak live shot effects reported 0/160 in the summary line; the batch dropped 0).
- Musou: the GPU path's upload and pool limits above; no load-aware scaling exists (the owner wants effects to scale down before the
  frame rate does); the Ashfall storm draws 4,400 ash + 1,680 flakes + 720 embers + debris and dust around the camera, 90 fires with 99
  lights and 9 plumes at a fixed density; far corpses already vanish (perf pass).

### 2.5 Weapon behaviour and feedback (Classic: toward the original only)

- Predicted hit markers and confirmation sounds exist for hit-scan shots (protocol 65); melee contact in Classic still waits for the
  server (55 to 65 ms on loopback, more online): the slash flash, hit sound and power flash play on the report. Musou predicts its
  contact (`--musou-old-contact` restores). The original was host-authoritative, so the host felt its hits at once: a predicted
  cue (presentation only, the server still decides) is a feel change the owner must rule on.
- The estimates a playtest must settle are listed in the refine register; the ones most felt: the 10 s ready hold, vent 2.1 vs 6 s,
  weapon switch to first shot 0.1 s, the heavy weapons' camera kicks (ours, low), the body recoil scale (ours), tracer width x0.42 and
  trail x0.3 (ours). Each has its switch; this pass changes none without the owner.

### 2.6 Sound

- The data's per-node instance limits (2,945 nodes carry one) are applied to **footsteps only** (`SoundSystem.cs:99`,
  `InstanceLimits.Footsteps`; `--sound-limits all` exists). The original suppresses stacked identical grenades, blasts and melee hits
  with these; we play them all: the clutter the owner hears in big fights.
- `PriorityDistanceOffset` and `PriorityApplyDistanceFactor` are read (`Hirc.cs:135-138`) but not used in the steal order; at the 48 + 16
  voice cap a far sound can steal from a near one.
- Play fade-in / Stop fade-out times and curves are parsed but unused; Wwise's virtual-voice behaviour (resume / from start / kill) is not
  modelled; state groups and RTPC-driven ducking are not applied (the owner's data has no HDR).
- Ours on top of the data: +9 dB on every voice, every range x2.5, a 20 % floor, fire and hit +4 dB, melee hit +6 dB, master x1.1 and a
  limiter at -0.3 dB. A melee hit sits about +19 dB over the data and a shot +13 dB, audible 2.5x farther: punchy, but it is also why
  everything in an 8v8 is heard at once. `--loudness-original` is the Classic fidelity switch (the owner's call: the +9 dB was his
  request of 2026-11-08).
- Known missing sounds (fidelity gap 9's leftovers, the chain axe hum `Sound.Melee.cs:24`, the Ork shot grunt gap `Sound.Ork.cs:135`).

### 2.7 What a tester meets in the downloadable build

- Options (read: `OptionsView.cs`): resolution, window mode, vsync, FPS cap, upscaling, render scale, shadows, anti-aliasing, FOV,
  brightness, lighting preset, reduce motion, camera shake, screen flashes, emblems; master / music / effects / voice volumes; mouse,
  aim and gamepad sensitivities, invert Y, keybinds. **Missing**: an FPS counter and a network readout (ping, loss, interpolation), HUD
  scale, controller dead zone, aim-mode FOV, subtitles.
- Loading: a still LOADING picture with a 2.5 s blink wait; no progress text; a Musou join shows it for 28 s; no `LOADTIME` summary
  line in the log (the pieces are logged separately).
- Logs and crashes: Godot keeps 10 logs in `user://logs`; the launcher keeps `launcher.log`, rolls itself back after its own crash and
  has "Open folder"; there is no log bundle, no in-game report path, and no client-side unhandled-exception handler (a crash leaves the
  tester with a closed window and a folder to find).
- First launch: the install is found through Steam; audio decodes to the cache on first use; the nav graph is cached; nothing else
  (collision, map scene, the Ashfall layout, shader pipelines on Compatibility) is cached between launches.

## 3. Items, prioritised

Columns: mode (C = Classic PvP, M = Musou, B = both); **faithful-safe** says whether Classic may take it under the owner's rule (yes =
no change to what is simulated or shown, or a step toward the original's data); gain and cost are expected unless marked measured;
every item has a restore switch and a verification; files are listed so items can go to separate implementers without touching the
same file (an item that shares a file with another names it).

| # | Item | Area | Mode | Faithful-safe | Expected gain | Cost | Restore switch | Verify | Files (owner of the file for the milestone) |
|---|---|---|---|---|---|---|---|---|---|
| 1 | **Ashfall layout cache**: serialize the generated city layout by its hash to `%LOCALAPPDATA%\sm-reimpl\cache\ashfall\<hash>-<draft version>.bin` (a derived file, nothing from the install), read it on the next join; the server the same; generate on a worker when the cache misses, with the loading screen moving | load | M | n/a | Musou join **28 s -> about 4 s** (measured 23 s is the layout) | medium | `--no-ashfall-cache` | the `ashfall: City layout ... ms` line; `--frame-profile` warm-up worst; `TFWTEST PASS` unchanged | `src/SmSim/Extras/Musou/Ashfall/*` (the generator's entry and a writer / reader), `client/Extras/Musou/Ashfall/NetworkView.TfwAshfall.cs`, `src/SmServer/Extras/Musou/*` (the server's load), `src/SmContent/Extras/Musou/AshfallCache.cs` (new) |
| 2 | **Map load off the main thread** (Classic): `MapContent.Load`, `ToCollisionWorld`, `MapScene.Load` and the look warm-up on a worker; Godot nodes built on the main thread in 8 ms slices behind the loading screen; the Hello sent when collision is ready | load | B | yes (presentation; the same world) | no 3.5 s frozen frame; the loading screen animates; no keep-alive dependence | medium to high | `--sync-map-load` | `--frame-profile` warm-up worst under 100 ms; `--test-freeze-at` tests still pass; `LOADTIME` line | `client/NetworkView.cs` (`LoadMap`), `client/NetworkView.Loading.cs`, `client/MapView.cs` (sliced build), `client/MapLightingView.cs` |
| 3 | **Load-time line, loading progress and an FPS / network readout**: one `LOADTIME map <name>: content a ms, collision b, visuals c, looks d, total e` line; the loading screen's caption steps (Reading the map, Building collision, Lighting, Looks, Joining); Options > Graphics "Show performance" (fps, frame ms, ping, loss, interp, lead; from `RttEstimator`, `NetLatencyReadout`, `PerfProbe`'s counters) | tester | B | yes (HUD aid, off by default) | testers' reports carry numbers; a 28 s load no longer looks hung | low | the option itself; `--no-overlay` | `--ui-selftest` fits; the line appears in every log | `client/NetworkView.Loading.cs`, `client/Hud/HudStats.cs` (new), `client/FrontEnd/OptionsView.cs`, `client/Customizer/Model/GameOptions.cs`, `client/NetworkView.cs` (one `LOADTIME` print) |
| 4 | **Instance limits for every sound**: the data's per-node max instances (`InstanceLimits.All`) as the default in both modes, after an A/B recording (**P1: the default since the owner's decision of 2026-11-10** after the A/B clips in `<media folder>\previews\polish-pass\sound-limits\`: "yes lets apply the cap to all for sound A/B"; the flag bytes' order stays an interpretation, so testers report missing hit, kill or voice sounds) | sound | B | yes (the data's own rule) | stacked grenades, blasts and melee hits no longer pile up: the clutter in big fights (the owner's complaint) | low | `--sound-limits footsteps` | `--log-sound mixab` and `--musou-record-mix` before / after; `smtool audio-mix` of the busiest events; `SoundLimitTests` | `client/SoundSystem.cs` (the default), `client/NetworkView.Sound.Log.cs` |
| 5 | **Priority distance offset and stop fades from the data**: fold `PriorityDistanceOffset` / `ApplyDistanceFactor` into the steal order; use the parsed Play fade-in / Stop fade-out times and curves | sound | B | yes (data) | near sounds keep their voice at the cap; no clicks when a voice is killed | low to medium | `--old-steal-order`, `--no-data-fades` | `--log-sound` steal lines; unit tests on `InstanceLimiter` / a new `VoicePriority` class | `client/SoundSystem.cs` (shares with 4: same milestone, one owner), `src/SmContent/AudioCatalog.cs` |
| 6 | **Musou effect governor**: a controller that watches the last 1 s of frame time and the GPU spray pool and lowers Musou's effect tiers before the frame passes 16.7 ms (blood sprays per kill, far gore, ash and flake density, death effects a frame, fire lights, GPU records per spray), restores with headroom; Options > Graphics "Effects: auto / high / medium / low" | effects | M | n/a | a steady 60 fps in a 1,000+ fight on a mid-range PC (the owner's target); bursts no longer drop 30 k sprays | medium | `--no-fx-governor`, the option at `high` | `clash1000` frames over 16.7 ms (73 now) and `burst` worst (50 ms now); the governor's own log line each 5 s | `client/Extras/Musou/MusouEffectGovernor.cs` (new), `client/Extras/Musou/FxPlayer.MusouGpu.cs` (a tier input), `client/Extras/Musou/Ashfall/AshfallStorm.cs` (density input), `client/Extras/Musou/NetworkView.MusouDeathSpread.cs` (deaths a frame input) |
| 7 | **GPU sprays: upload path and pool**: replace the per-layer `Image.CreateFromData` + `UpdateLayer` with one storage buffer updated by dirty byte ranges (`RenderingDevice.BufferUpdate`; the shader reads it instead of the texture array), or at least pooled `Image` objects and one update a layer a frame; a second alpha pool or a per-burst spray budget so a 200-death burst drops none | effects | M | n/a | burst worst **50 -> about 30 ms** (the 44 ms "rest of the frame" is mostly here, measured); zero dropped sprays in a burst | medium to high | `--fx-gpu-images` (the old upload path), `--fx-cpu` | `burst` MUSOUSPIKE summary; the `effects gpu:` line's dropped count 0 | `client/Extras/Musou/FxGpuSprays.cs`, `src/SmContent/Extras/Musou/FxGpuLayout.cs` (the only two; 6 reads them through one property) |
| 8 | **Landing spread** (Musou): build the Musou HUD, stations and markers in the Stormbird before the landing; the first effects and ragdolls of the landing over frames; the server spawns the battle's crowds and Guard squads over 10 ticks | perf | M | n/a | no 400 to 740 ms frames at the landing; no 602 ms tick and 51 skipped ticks | medium | `--musou-old-landing` | `clash1000` hitches in the landing's 3 s; `TICKSPIKE` at the landing | `client/Extras/Musou/MusouHud.cs`, `client/Extras/Musou/NetworkView.Musou.cs` (the hub's warm-up), `client/Extras/Musou/MusouRagdolls.cs`; server `src/SmSim/Extras/Musou/TfwRun.cs` (`Land`) |
| 9 | **Classic blend times from the graphs**: entries 0.1 / 0.167 s as the graph says (the 0.1875 s floor only where `--transition-probe` shows a bone-rate snap), chain steps 0 or 0.1 s per the dump, per-weapon exits (0.333 / 0.4 / 0.1 s) instead of 0.2 s | animation | C (Musou keeps MW's values) | yes (toward the data) | strikes and reactions start and end as the original's; fewer visible pops at the weapon-ready changes | medium | `--old-blend-floors` | `--transition-probe` jerk and the `smtool graph-dump` values side by side; captures of knife, chainsword, axe, hammer combos before / after into `<media folder>\previews\polish-pass\blends\` | `client/ClipBlends.cs`, `client/CharacterViewer.Transitions.cs`, `src/SmNet/WeightFeel.cs` (`StrikeEntryFloor`), `client/NetworkView.SprintEdges.cs` (`ActionExitBlend`), `client/NetworkView.MeleeFlow.cs` |
| 10 | **Transitions the install has** (approved, answer 2): wire `180Turn_to_Sprint`, `Bullrush2Idle_HalfSpeed`, the four other hit directions, `Sprint_Stagger`, `Stun2Idle` where the graph names them (Classic and Musou; the musou-weight pass does the Musou-only puffs and settles) | animation | B | yes where the graph wires them | the original's own pivots, half-speed stops and side hits | medium | `--no-extra-transitions` | `smtool graph-dump` shows the graph edge; `--transition-sweep` captures | `client/NetworkView.SprintEdges.cs` (shares with 9: same owner or after 9), `client/NetworkView.Reactions.cs`, `client/NetworkView.RunStart.cs` |
| 11 | **Server runtime**: ReadyToRun compile of the server in the release zip (`PublishReadyToRun`, or `TieredCompilation` quick-jit off for the loop assemblies), `GCSettings.LatencyMode = SustainedLowLatency`, concurrent GC on | perf | B | yes (no behaviour change) | no 48 to 65 ms first-use ticks; fewer GC pauses in ticks | low | `SM_OLD_SERVER_RUNTIME=1` | `TICKSPIKE` in the first 10 s of a match; `TICKPACE` GC pauses | `src/SmServer/SmServer.csproj`, `src/SmServer/Program.cs`, `tools/release/*` (the publish step) |
| 12 | **Physics tick**: the map's physics world at 30 Hz, or stepped only while a ragdoll or prop is active | perf | B | yes | about 1 ms a frame (measured 1.65 ms with 0 active bodies) | low | `--physics-60` | `--perf-probe` physics step; ragdoll tests and `--ragdoll` captures unchanged | `client/project.godot`, `client/Ragdoll.cs` |
| 13 | **Map draw-call merge**: at load, merge Manufactorum's static surfaces by material (same meshes, textures and shader parameters; `MultiMesh` for repeated meshes, merged `ArrayMesh` for unique ones), keeping cut-outs, two-layer and emissive surfaces as they are | perf | B | yes (same pixels; check with before / after captures) | render CPU 2.8 -> about 1 ms Forward+, **6 -> about 2 ms on Compatibility** (the GTX 9xx case) | medium | `--no-map-merge` | `--perf-probe` draw calls (1,925 now) and render CPU; pixel-diff captures of 6 views in both renderers | `client/MapView.cs`, `client/MapMaterials.cs` |
| 14 | **Effects evaluation on workers**: the CPU effect path's per-particle evaluation in `Parallel.For` over live effects (pure functions of age), quads written to per-thread buffers and merged | perf | B | yes (same output) | effects phase 2.56 -> about 1 ms mean, worst 36 -> under 10 ms; Musou bursts too | medium | `--fx-single-thread` | `--frame-profile` effects phase; `--fx` test view pixel-identical | `client/FxPlayer.cs`, `client/FxBatch.cs` (shares with 7 nothing: the GPU path is in its own files) |
| 15 | **Allocation diet**: the per-frame allocations of the remote players' model and team-look updates (24 to 27 KB/frame), the network update (16.5) and effects (16); reuse buffers, avoid LINQ in the per-frame paths | perf | B | yes | gen0 every 2 to 3 s instead of every second; the 18 to 54 ms GC pauses inside hitch frames gone | medium, scattered | none (pure refactor; `--frame-profile` alloc columns are the check) | `--frame-profile` alloc under 60 KB/frame; gen0 count per 100 s | `client/NetworkView.Appearance.cs`, `client/NetworkView.Bodies.cs`, `src/SmNet/GameClient.cs` (allocation sites only; coordinate with 17) |
| 16 | **First-frame prewarm, Compatibility shader warm-up**: build the own model and the first effects frame during the load; on the Compatibility renderer draw each map and character material once off-screen while the loading screen is up | perf | B | yes | the 229 / 124 ms first frames and the 348 / 353 ms OpenGL compiles gone | medium | `--no-hitch-fixes` (exists) | `--frame-profile` first 2 s after the join in both renderers | `client/NetworkView.Prewarm.cs`, `client/SafeMode.cs`, `client/FxPlayer.cs` (`Preload`; shares with 14: sequence after it) |
| 17 | **Delta snapshots**: player records written as change bits against the last snapshot the client acknowledged (the input batch carries the ack), full records every 2 s and on request; lifts the shot and projectile record caps | netcode | B | yes (wire only; the simulation unchanged) | bytes a client -50 to -70 % (36 -> about 12 KB/s at 16 players); room for every shot and projectile record in a firefight; Musou's crowd keeps more of its budget | high (protocol bump, tests, the demo format) | server `--full-snapshots` | `TICKBYTES`; `SmNet.Tests` round trips; a recorded demo replays | `src/SmNet/Protocol.cs`, `src/SmServer/GameServer.cs` (snapshot send), `src/SmNet/GameClient.cs` (apply; coordinate with 15) |
| 18 | **Musou crowd budget check**: why tag 3 sent 66 KB/s against the 58 KB/s budget in the clash test | netcode | M | n/a | the budget holds in the biggest fights | low | n/a | `TICKBYTES` in `clash1000` under 60 KB/s total | `src/SmServer/Extras/Musou/*` (the send buckets) |
| 19 | **Human latency measurements**: a 10-minute human run with `--net-latency 5` and `--latency-probe` on loopback and against the VM; decide afterwards whether the interpolation base (1.5 intervals) can go to 1.25 on clean links and whether held-movement edges deserve an early tick | netcode | B | yes (measurement; any change gated on it) | knows the real edge-draw and press-apply before touching the lead | low | existing switches (`--interp-delay`, `--old-input-lead`) | the `NETLAT` summary lines | none (a run) |
| 20 | **Camera casts on Ashfall**: cache the sphere casts' broad phase per cell, cast every frame only while the camera moves | perf | M (shared code, same result) | yes | about 1.5 of the 2 ms camera phase on Ashfall | low to medium | `--old-camera-casts` | `--frame-profile` camera phase on Ashfall and Manufactorum; `--camera-test` unchanged | `client/NetworkView.Camera.cs`, `src/SmContent/StaticMesh.cs` (`SweepSphere`) |
| 21 | **Musou sound: own-weapon side-chain**: the own shots, strikes and hits duck the crowd and blast buses by 3 to 6 dB for 150 ms (a code-made layer; the data has none) and the data's instance limits (item 4) on the Ork voices | sound | M | n/a | the player's own weapon reads over 1,000 Orks; less "drowned out" | low | `--musou-sound-off duck` (exists) | `--musou-record-mix` A/B, `--log-sound mixab` | `client/Extras/Musou/NetworkView.MusouAudio.cs` (after 4 and 5 land in `SoundSystem.cs`) |
| 22 | **Crash handler and log bundle** (**deferred**, the owner 2026-11-10: "Cancel this for now. Save it for later"; when it comes back it goes in the in-game **Esc menu only**, not the launcher or the main menu; nothing of it is built until then): the client catches unhandled .NET exceptions, writes `crash-<time>.txt` beside the logs and shows a one-line message with the folder; the launcher gets "Save a report (zip)" (launcher log, the newest client and server logs, the renderer line, the build version) | tester | B | yes | a tester can report a crash in one step | low to medium | n/a | kill the client with `--test-crash` (new aid); the zip's contents listed by a launcher UI test | `client/ModelViewer.cs` (the handler), `src/SmLauncher/MainForm.cs`, `src/SmLauncher.Core/ReportBundle.cs` (new) |
| 23 | **Safe-mode defaults**: Ashfall grime FAST, the effect governor's `medium` tier and FSR off as the Compatibility defaults | effects | M | n/a | GTX 9xx keeps 60 fps on Ashfall | low | the options themselves | `clash1000 -ClientArgs "--safe-mode"` with `--rendering-method gl_compatibility` | `client/SafeMode.cs`, `client/FrontEnd/OptionsHost.cs` |
| 24 | **Predicted melee contact cue** (Classic; approved, answer 3): the slash flash, hit sound and power flash on the strike's frame when the melee lock's presentation guess has a victim in reach, the server's report then confirms or stays silent (as Musou's `--musou-old-contact` pair) | weapons | C | the owner's decision (answer 3; a feel change argued as closer to the host's feel in the original) | own melee reads 55 to 65 ms sooner on loopback, more online | medium | `--no-predicted-melee` | `--feel-probe`-style line in Classic; `--log-melee-fx` | `client/NetworkView.MeleeFx.cs`, `client/NetworkView.MeleeHitSound.cs`, `src/SmNet/MeleePresentation.cs` |
| 25 | **Classic mix level** (approved, answer 1): either keep the +9 dB / x2.5 stack, or a master gain of +6 dB over the data's own levels and ranges (`--loudness-original` plus master) | sound | C | yes if toward the data | an 8v8 that is loud but not everything at once | low | `--loudness-gain`, `--loudness-range` (exist) | the owner's A/B with `--musou-record-mix`-style captures in Classic | `src/SmContent/SoundLoudness.cs` |
| 26 | **Options the original lacked** (dropped, answer 7): HUD scale, controller dead zone, aim-mode FOV | tester | B | no (ours) | comfort on 1440p / 4K and pads | low each | the option's default | `--ui-selftest` | `client/FrontEnd/OptionsView.cs`, `client/Hud.cs` (after 3) |

Not in the list on purpose: new effects or camera work in Classic (the owner's rule), new clips (the claw and spear agent), the Musou
weight items (MW1 to MW7), a 60 Hz tick (question 4), FSR 2 as a default (question 8).

## 4. Milestones (small, one build each, ordered for frequent playtest builds)

| Milestone | Items | Why in this order | Build check |
|---|---|---|---|
| **P1 quick wins** | 3 (LOADTIME, loading captions, FPS / net readout), 4 (instance limits all), 11 (server runtime), 12 (physics tick), 23 (safe-mode defaults) | low cost, no shared files, every later milestone measures through 3 | `polish-pvp-baseline.ps1` and `musou-perf.ps1 clash1000` rerun; the readout visible in a windowed capture |
| **P2 loads** | 1 (Ashfall cache), 2 (map load off the main thread) | the largest tester-facing numbers (28 s and 3.5 s); 1 and 2 touch different files | Musou join under 5 s; Classic warm-up worst under 100 ms |
| **P3 first frames and the landing** | 16 (prewarm, Compatibility shader warm-up), 29 (sound-phase stalls), 2's remainder (the ragdoll shape and the Ashfall view off the main thread; the 668 ms engine hitch at 2 s of a Classic join), 8 (landing spread) | after P2 the join is the only hitch left | first 3 s after join / landing: no frame over 50 ms |
| **P4 Classic animation toward the data** | 9, then 10 (after owner question 2) | one owner for `SprintEdges.cs` and `Transitions.cs`; sequence around MW4 (section 5) | `--transition-probe` before / after; captures in `<media folder>\previews\polish-pass\blends\` |
| **P5 Musou effects** | 7 (GPU sprays), 6 (governor), 20 (camera casts) | 7 before 6 (the governor reads the pool state 7 defines); the autocannon's blasts (W2) should land on top of the governor | `burst` worst under 35 ms, 0 dropped sprays; `clash1000` frames over 16.7 ms under 20 |
| **P6 sound** | 5, 21, 25 (if the owner says so) | after 4 settled the default; one owner for `SoundSystem.cs` | mix captures A/B; the owner's listen |
| **P7 render and allocations** | 13 (draw-call merge), 14 (effects on workers), 27 (eviction order), 30 (HUD show-set leak), 15 (allocation diet) | independent files; the biggest Compatibility gains | render CPU under 1.5 / 3 ms (Forward+ / Compatibility); alloc under 60 KB/frame |
| **P8 netcode** | 18 (budget check), 19 (human measurements), 28 (the 60 Hz measurement), then 17 (delta snapshots, a protocol bump) | 17 waits for 19's numbers and a quiet window (it bumps the protocol: VM server update) | `TICKBYTES` halved; demo replay |
| **P9 tester surface** | 24 (predicted melee contact, approved); 22 deferred by the owner (saved for later, Esc menu only when it returns) | 26 dropped by the owner | launcher UI tests; `--ui-selftest` |

Every milestone: builds, the full `dotnet test` (ask normal vs capped first), `smtool refine-register --check`, independent review,
merge into `main` on completion (the merge-gate rule), a `release/NEXT.md` bullet in tester words.

## 5. Overlap with the in-flight work and how to sequence

| In flight | Touches | Overlap here | Sequence |
|---|---|---|---|
| Musou weight and impact pass (`claude/musou-weight`, MW1 to MW7: puffs, foot skate, landings, transitions, hit accent, camera, audio) | `CharacterViewer.Transitions.cs` (pose overlay hook), `ClipBlends.cs` (Musou values), `NetworkView.CameraImpulse.cs`, `FxPlayer` (puffs through the GPU path), `NetworkView.MusouAudio.cs` | item 9 (Classic blend times) edits the same blend files; item 7 changes the GPU path MW-A spawns into; item 21 shares `MusouAudio.cs` | 9 after MW4 lands, with the Classic values gated so MW's Musou values stay (`ExtrasBlendTimes` hook); 7 lands first or in parallel with 7's owner holding `FxGpuSprays.cs` (MW-A only calls `FxPlayer`'s API); 21 after MW7 |
| New Musou weapons (`claude/new-weapons-design`, W0 being built: claw, spear, autocannon, Remote Detonators) | Musou customizer, wargear, new effects (2.5 m blasts per Ork struck, a 40 m charge blast) | none in files; the new blasts add effect load the governor (6) must know | 6 before W2 (autocannon) and W3 (charge) ship; W0 and W1 unaffected |
| GPU particles on main (review: bursts 63.6 vs 53.4 ms, 34 k sprays dropped) | `FxGpuSprays.cs`, `FxPlayer.MusouGpu.cs` | items 7 and 6 are its follow-up | this plan owns the follow-up |
| Net responsiveness (shipped, protocol 65) | `GameClient.Responsive.cs`, `InterpolationDelay.cs` | items 17 and 19 build on it | 19 first (measure), 17 later (bump) |
| The landing hitch ("ship now, fix after") | `MusouHud.cs`, `TfwRun.Land` | item 8 | P3 |

File ownership rule for the implementers: one writer per file per milestone; `SoundSystem.cs` (4, 5), `SprintEdges.cs` (9, 10),
`FxPlayer.cs` (14, 16) and `GameClient.cs` (15, 17) are the shared ones and are serialised above.

## 6. The owner's answers (2026-11-10, through the lead; the first draft's questions are in the history)

| # | Question | Answer | What it fixes in the plan |
|---|---|---|---|
| 1 | Classic mix level (item 25) | **"Toward the original."** Classic's balance between sounds goes back to the game files'; a master gain keeps the overall loudness. Musou keeps its own punchier mix | item 25 is approved for P6: the data's levels and ranges in Classic (`--loudness-original` plus a master gain), the Musou mix unchanged |
| 2 | Classic transitions (item 10) | **"Yes, where the original wires them."** | item 10 is approved for P4: the install's unused transition clips where the animation graph names them |
| 3 | Predicted melee contact in Classic (item 24) | **"Yes, predicted contact."** The sound and flash show when the swing lands on screen; the server still decides damage | item 24 is approved (P9 or earlier) |
| 4 | Tick rate | **"60 Hz server test + add delta snapshots."** Measure a 60 Hz server (CPU, bandwidth, input delay) and build delta snapshots; any switch to 60 Hz goes back to the owner with the numbers | item 17 approved; new item 28 (the 60 Hz measurement) in P8 |
| 5 | The mid-range target (item 6) | **"GTX 1060 / RX 580"** is the steady-60 target in big Musou fights | item 6's tiers are tuned for that class; a test card is still to find |
| 6 | Ashfall layout cache (item 1) | the lead's decision: **yes**, in the private cache (`%LOCALAPPDATA%\sm-reimpl\cache`, never the repo), keyed by its hash | item 1 approved for P2 |
| 7 | Options the original lacked (item 26) | **"No, keep the original's options."** No HUD scale, dead zone or aim-mode FOV in Classic | item 26 dropped. Item 3's performance readout is not an Options row: F3 cycles it (a tester aid, off by default) |
| 8 | FSR 2 | **"Yes, FSR before cutting effects."** FSR is a governor tier ahead of thinning effects and crowds | item 6: the governor's first tiers are FSR 2 Quality, then Balanced, then the effect tiers |
| 9 | Report zip (item 22) | first yes to a one-click "send report" for testers, then on the details **"Cancel this for now. Save it for later"**; when it returns, **"Esc menu only"** (not the launcher, not the main menu) | item 22 deferred whole, nothing built; its row carries both notes |

How to build (the lead, 2026-11-10): from current `origin/main`, merging it as it moves; one milestone per branch (`claude/polish-p1`, `-p2`, ...);
the weight pass owns Musou animation and feel and the weapons own their W files; after each milestone both builds, targeted tests,
`refine-register --check`, spec rows (kind and confidence), a milestone paragraph with its restore switch, before / after numbers from the
baseline harness, then the branch tip to the lead for the independent review, the capped full test and the merge.

### Items added after the first draft

| # | Item | Area | Mode | Faithful-safe | Expected gain | Cost | Restore switch | Verify | Files |
|---|---|---|---|---|---|---|---|---|---|
| 27 | **Effect eviction order** (found by the weight pass): `FxPlayer.Play`'s victim pick (`FxPlayer.cs:298`) takes "lower priority, or older", so an older effect at priority 0 can be evicted ahead of a younger one at -1; compare the priority first and the age only within one priority | effects | B | yes (our own cap system, not the original's; changes the eviction only when the cap is hit, toward the intended order) | the intended "lowest priority first" when the cap is hit | trivial | none needed (a bug fix; `--fx-cap` shows it) | a unit test on the pick with mixed priorities | `client/FxPlayer.cs` (with item 14 in P7, one owner) |
| 28 | **60 Hz server measurement**: `--tick-rate 60` as a server test aid (the config hash changes with it: a test server only), the baseline harness run at 60 Hz: tick CPU, bandwidth (with and without item 17), `press-apply` and `remote` from `NETLAT`; the numbers to the owner, no default change | netcode | B | no (a measurement; the switch itself changes every tick-quantised timing) | the owner's decision on 60 Hz rests on numbers | low to medium (every tick-based constant must scale or the aid refuses) | the aid is opt-in | the harness's `NETLAT` and `TICKSTATS` at 30 and 60 | `src/SmSim/Simulation.cs` (`SimConfig.TickRate`), `src/SmServer/GameServer.CommandLine.cs`, `tools/polish-pvp-baseline.ps1` |

| 29 | **Sound-phase stalls** (found by P1's runs with sound on): the `sound` phase held the main thread 174 to 181 ms at the join and 416 ms at 50 s into a 13-player match (47 to 67 ms more times); the likely culprits are a late voice's `AudioStreamOggVorbis.LoadFromBuffer` or a bank's first use on the main thread (`SoundSystem`, the decode is on workers but the stream object is made on the main thread) | perf | B | yes (same sounds, later or earlier work) | no 50 to 400 ms frames from the sound system | low to medium | `--no-sound-prewarm` | `--frame-profile` with sound on: the `sound` phase's worst under 10 ms after the warm-up | `client/SoundSystem.cs` (shares with items 4 and 5: after P6's owner, or P3 with that owner's leave) |

| 30 | **HUD weapon-box show-set cache leak** (found by the W0b review, confirmed by reading): `Hud.Fidelity.cs:329` keys `ShowSets` by the identity of `spec.Show` (`ReferenceEqualityComparer`), but `WeaponBoxRules.For` (`src/SmContent/WeaponBoxRules.cs:31`) builds its `Show` list as a fresh collection expression on every call, so the cache never hits and gains one `HashSet` per HUD frame (Classic too; a few hundred bytes a frame, megabytes an hour). Fix: cache the spec per (type, chaos) in `WeaponBoxRules.For`, or key `ShowSets` by value (`string.Join`) | perf | B | yes (same pixels) | no per-frame growth; fewer gen0 collections | trivial | none (a bug fix) | count `ShowSets` after a minute of play (a `--frame-profile` alloc column for the HUD phase) | `client/Hud/Hud.Fidelity.cs`, `src/SmContent/WeaponBoxRules.cs` (P7, with item 15's owner) |

Note for the Musou effects milestone (P5, from the weight pass): the weight pass's dust clouds do not draw on the Compatibility renderer's CPU
effect path (grit and stones do; the cause is unknown; turning off the soft ground fade did not help). Item 6 / 7's owner should find the cause
before the governor's tiers rely on the CPU path in safe mode.

### Milestone log

- **P1** (branch `claude/polish-p1`, 2026-11-10): items 3 (the `LOADTIME` line and the performance readout; the loading captions wait for item 2,
  since the main thread is blocked during the load and a caption set before it would not draw), 4 (the data's instance limits for every sound),
  11 (the server's GC latency mode and ReadyToRun for published servers), 12 as a measurement aid (`--physics-ticks`, no default change) and 23
  (the grime folded in safe mode). Measured: 322 of 2,475 instance kills on nodes the footsteps default never limited (13 players, 90 s); after the review
  `all` is the default again on the owner's word after the A/B (`LIMITNODE` lines and clips recorded: Classic and a Musou fight), revived loops resume their position, F3 is a reserved key, the safe-mode grime
  flag lives in the core; the physics aid inconclusive (noise), default 60 kept; new item 29 (sound-phase stalls up to 416 ms). ReadyToRun: a published server is
  compiled ahead; note that the VM's `vm-build-server.sh` publishes on the live server box, so it downloads the crossgen2 package there once and compiles there (a
  minute or two of CPU at update time, `-p:SmReadyToRun=false` skips it). Numbers and switches: `docs/milestones.md`, "Polish pass P1".
- **P2** (branch `claude/polish-p2`, 2026-11-10): items 1 (the Ashfall layout cache, `AshfallLayoutCache`, keyed by the generator versions and the build) and 2 (the map load off
  the main thread: `BeginLoadMap` / `IMapLoad` in the client, `Stage1` on a worker, the Godot build in one frame, the warm-ups one a frame, loading captions). Measured (human client): Classic's frozen frame 3.5 s -> 0.80 s (the reading on a worker behind the moving loading screen, the warm-ups one a frame); Musou's join 28 s -> 6.8 s warm (one frame of 2.75 s left: the ragdoll body's shape 1.2 s and the Ashfall view 1.6 s, for P3), the cache read in under 0.2 s; `--sync-map-load` and `SM_NO_ASHFALL_CACHE=1` / `--no-ashfall-cache` restore.
  Numbers and switches: `docs/milestones.md`, "Polish pass P2".

## 7. Things the owner may be missing

- The Musou join takes 28 s on his own PC, 23 s of it the city layout; testers on slower CPUs wait longer and see a still picture.
- The GPU particle path is not yet a frame-time win: in a 200-death burst it is slower than the CPU path and drops 30 k blood sprays a
  minute; the gain comes with the upload path and the governor (items 7 and 6), not with "applying" it more widely.
- The Compatibility renderer pays per draw call: Manufactorum's 1,925 draw calls cost 6 ms of CPU there; the merge (item 13) is the one
  change that matters most for the GTX 9xx promise.
- The sound data already holds the anti-clutter rules (instance limits, priorities with distance, fades); the client applies a tenth of
  them. Applying them is faithful and free of design decisions.
- Classic's melee entries play up to 88 % longer than the graphs say (0.1875 s floor over 0.1 s); the "pops" the weight pass measured
  are partly this floor's doing, and the fix is the data, not more smoothing.
- The server itself is cheap (0.6 ms a tick with 13 players); what testers feel as "server lag" on a hosted PC is the host PC's
  saturation and the first-use JIT, which items 11 and 2 address.
- Nothing tells a tester what a frame or the network is doing (item 3), and a crash leaves no report (item 22).
- The `bots.cmd` aid caps at 6 a side, so an 8v8 with bots cannot be started from the documented commands; the harness here used 6v6 + 1.

## 8. Not measured, and caveats

- Input-to-visible latency with a human (`edge-draw`): bots produce no key edges; `--latency-probe` was not run. Item 19.
- A quiet PC: other sessions loaded the CPU during the runs (65 to 95 %); the Musou numbers in particular carry that noise.
- An exported Release client: the editor Debug build was measured (GPU numbers are unaffected; the CPU side is pessimistic).
- GTX 9xx hardware: the Compatibility renderer was measured on the RTX 4070 Ti (it shows the path's CPU cost, not an old card's GPU).
- A 16-player match: 13 players (6 + 6 bots and the measuring client).
- Shader-compile hitches on a cold Godot shader cache in Forward+: the worktree's first windowed run was the Musou clash, whose landing
  hitches mix compiles with the landing's own work; the Classic runs came after.
- The GPU spray record-upload cost was read from the `effects gpu:` line and the `MUSOUSPIKE` split ("the rest of a frame" 44 ms), not
  from a GPU timeline capture.

## 9. How to re-measure

`powershell tools\polish-pvp-baseline.ps1 -Tag <t>` (Classic, Forward+ then Compatibility) and `powershell tools\musou-perf.ps1 -Scene
clash1000 -Tag <t> -Seconds 90` / `-Scene burst -Tag <t>` (Musou); summaries under `%LOCALAPPDATA%\sm-reimpl\perf\`. Build the server in
Release first. Pitfall found while measuring: `godot.ps1`'s `--` separator must be passed by splatting an array from PowerShell (a
literal `--` is eaten by PowerShell, every client argument then becomes an engine argument and the client silently opens the front end
and quits).
