# Refine register sweep (2026-11-16)

Owner-approved documentation and accuracy pass over the refine register (`docs/refine-register.md`), Classic PvP first. Branch `claude/refine-register-sweep`, cut from `claude/evidence-p0`. **No gameplay value and no code constant was changed.** Musou rows were not touched. Nothing was settled by guess: a row was closed only when the spec itself already says a later row replaced it, or when the row is engineering with nothing to compare with SM1.

## 1. Counts

| | Before (evidence-p0 tip) | After |
|---|---:|---:|
| Spec table rows scanned | 3329 | 3329 |
| Tagged rows (Part A) | 2441 | 2433 |
| Not tagged (read from data, high) | 620 | 616 |
| Appendix (test aids, engineering, superseded) | 268 | 280 |
| Part C rows (changes document) | 738 | 738 |

Net: 9 rows left Part A, 1 came back (`R-3D3F6639`, see 4), 4 untagged rows moved to the appendix.

## 2. Rows closed and why

Two mechanisms, both small and tested (`RefineRegisterTests`):

- **Kind `superseded`** (generator: `RefineRules.IsSuperseded`): a row whose Kind cell starts with "superseded" is history and is listed in the appendix, not in Part A. The spec rows below already said in their own text that a later row replaced them; only their Kind and Confidence cells changed (`superseded (was <old kind>, <old confidence>)`, `—`). The replacement row keeps the live tag. A spec row that is only partly superseded was left alone.
- **Engineering names** (generator: `EngineeringSubject`): rows named exactly `Protocol` or `Server log` whose source or kind is ours are wire or log formats, with nothing to compare with SM1; they go to the appendix like `Protocol 18`.

| R-id | Row | Why closed | Replacement |
|---|---|---|---|
| `R-7EE1950B` | The charge: Binding (sprinting forward) | superseded by protocol 39 and 49 | "The charge needs the sprint", Charge request (protocol 49) |
| `R-98B884B3` | The charge: Start gate (M31), 7.375 m/s sprint speed | superseded by protocol 39; `Rush.SprintThreshold` and `RushSpec.SprintMargin` are gone (the `SprintMargin` left in `SprintStop.cs` is the sprint stop's) | same |
| `R-AD722D2E` | Charge start (protocol 39) | superseded by protocol 49 (Sprint held as well) | same; its 0.7 stick share is restated there |
| `R-AD4F6782` | Weapon switch time, 0.6 x 0.8 = 0.48 s | superseded by the quickdraw (0.1 s, 2026-11-06) | `R-8813BF01` (carries the data's 0.467 s and 0.667 s) |
| `R-CB6E42DC` | Air-Cooled Thrusters pound delay (2 s) | superseded by protocol 73 (no weapon waits for it); `Loadout.cs` comment confirms | "Pound from any weapon in the ascent" |
| `R-ABCCA53F`, `R-BA478E13`, `R-7280DE6D`, `R-595B1816` | `Protocol` (3 rows) and `Server log` | engineering, ours | none needed |

The appendix also now lists three rows that were already untagged superseded rows (`R-ACF31455`, `R-2DF998F2`, `R-79F12026`) so nothing is hidden.

Left on purpose: partly superseded rows that still carry a live interpretation (`R-FEBBDE3E` pound start binding, `R-8DA81460` perk row whose recharge and height-map values are live, `R-27642325` firing ends the sprint in the air, `R-65A4BC36`, `R-E2018D4F` bent bullets behind `--bent-bullets`, `R-80A30BFB` stim glow behind `--old-stim-glow`, `R-5100F4DE`).

## 3. Spec and code disagreements found (neither side changed)

None found in the values I spot-checked (health 100, regen 5 s / 5 per s, shield 100, regen 7 s / 33 per s, respawn 5 s, tick 30, `smash_available_delay` use). Observations to confirm, not claimed as defects:

1. `SimConfig` defaults for speeds are 5 / 5 / 5 / 8 / 3 (run, strafe, back, sprint, walk, `Simulation.cs`), while the spec's extracted rows say 6.5 / 6 / 4.5 / 10 / 3.6. The spec says `GameplayTuning` fills them from the install; I did not trace that override, so I treat the defaults as the synthetic no-install values. Worth one test or comment by whoever owns `SimConfig`. Lead check (2026-11-16): `GameplayTuning.cs` reads `run_speed`, `sprint_speed`, `walk_speed` and the aiming speeds from the install's `biped_locomotion_ext` and falls back to the `SimConfig` defaults only when a field is missing, so there is no disagreement.
2. `Evade.cs` carries a stale comment about the direction rule (already known to the lead; waits for a code pass).

## 4. Items folded in from the evidence-p0 review

1. **Direction sign convention** (`docs/evidence-p0.md` 2.1, spec "Evade: Direction", `R-3D3F6639`): it was not established that negative `FLT_LStick_Angle_Char` is left; the report read it from the graph's state names (the state named Left holds the negative wedge), not from the code that fills the variable, and the angle was not measured. A line saying so was added; confidence is now high for Forward and Back and **medium for Left and Right**, in the report and the spec row. Because of the medium, the row is back in Part A (the evidence-p0 "settled" count is amended in its table).
2. **`docs/playtest/WHAT-TO-TEST.md`** (`R-8813BF01`): the tester text now gives the original clips (0.467 s rifle class, 0.667 s bolt pistol, shot at the end) and says our 0.1 s quickdraw is the owner's decision.

## 5. Rows that need the owner or a playtest (not touched)

- Owner decisions: the charge's reach and the side charge's 25 degrees (spec "The charge", "Charge directions"); the charge steering window (evidence-p0 section 3, item 2); whether the evade should end on Aim and Grenade (item 4); the back-diagonal evade clips (item 5); the quickdraw's 0.37 to 0.57 s lead over the original (item 3).
- Playtest (stopwatch or frame count against SM1): ready hold after a shot (`R-C64B6FB4`, 10 s is the owner's rough estimate), vent duration (`R-99C06BDF`, owner's 6 s against the data's 2.1 s), respawn time 5 s and Rapid Deployment 3 s (no number in any data: placeholder), walk speed use (`R-90435565`, when the original walks is unknown).
- Duplicate candidates, not merged because the second table has no Kind column to carry the closure: `Respawn time` in "Provisional values in use" (`R-72F9868D`) repeats `R-BED2640F` in "Teams and Annihilation" and the Part C row `C-D2C7690E`.
- Contradictory kind and confidence cells I could not settle without a guess: spec row "Respawn and death" under "Evade cooldowns" (kind `interpretation`, confidence `high`, source `ours`); rows reading `ours` with `high` that are tested client rules (responsive controls tap latch, early tick, fresh aim). A tested rule of ours is still ours, so they stay tagged; the owner may prefer a new kind such as "engineering, tested" for them.
- Rows with no Timing basis or Units cell (about 96 outside Musou and Ork, listed by a throwaway script: mostly non-numeric rows such as "Protocol", "Look", "Early release", "Splash flags"): not filled in; inventing a unit would be a guess.

## 6. Grouping for playtesters

The generator already groups Part A by area (critical areas first) and by spec section, with a Compare prompt and a Result box on every row; no grouping change was needed.

## 7. Checks run (author's own, not independent QA)

`dotnet build SmReimpl.slnx` and `dotnet build client/SmClient.csproj`: 0 errors. `smtool refine-register --check`: ok (3329 rows, 2433 tagged). Filtered `dotnet test` capped at `-m:4` (PublicText, RefineRegister, Spec, Evade): SmFormats.Tests 161 passed (includes `PublicTextGuardTests`, `RefineRegisterTests` with the two new cases), SmNet.Tests 143 passed. The full suite was not run.
