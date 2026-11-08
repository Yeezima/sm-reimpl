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

RESULTS_PLACEHOLDER
