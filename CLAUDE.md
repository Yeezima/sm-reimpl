# CLAUDE.md — sm-reimpl

## Goal
Reimplement the **multiplayer** of *Warhammer 40,000: Space Marine* (2011, PC, Steam app 55150) as faithfully as the
evidence allows. The engine reads content from each player's own install. The intentional change is server-authoritative
networking with dedicated servers. Everything else defaults to the original behavior: no redesign, rebalancing, or new
features without a reason tied to that goal. Do not confuse this game with Space Marine 2 (2024), or with the 2025 Master
Crafted Edition (app 3169520).

**Faithful first.** Classic multiplayer follows the original wherever evidence permits. The October 28 Ork-only addition was a dated scope decision. Use the current owner-approved brief for extended content.
Tag estimates, guesses, interpretations and "ours" values with spec `kind` and `confidence`. Keep the refine register (`docs/refine-register.md`, `smtool refine-register`) as the human playtest checklist.

**Added content stays separable (owner, 2026-10-28):** "i want to retain the ability to separate the 'added content' work from the rest of the
project at any time." Future features must keep SM1-only play selectable through an existing profile or feature switch, or one small configuration change.
On the usual build, `--content faithful` selects `ContentExtras.None`. Choose it on both the server and the networked client. `ContentProfiles.DefaultName` supplies the fallback when no profile is otherwise selected.
Keep optional feature code in `Extras/<Feature>/` behind `Extras*` hooks as described in [the added-content contract](docs/added-content.md).
`faithful-build.cmd` separately requests an extras-disabled build and tests with `-p:SmExtras=false`. Faithful Build work and checks stay deferred. Claim an end-to-end runtime or build pass only with evidence for that candidate.

## Scope (decided by the user, 2026-10-26)
- **In:** PvP (Annihilation, Seize Ground; Space Marines vs Chaos, up to 8v8) and **Exterminatus** (online co-op waves,
  which needs Ork/Chaos AI enemies). Plus whatever these need: MP maps, MP/co-op blueprint sets, player classes, wargear,
  weapons, armour customization and paint, progression and unlocks, front-end flow to get into a match.
- **Out:** the singleplayer campaign (chapters, cinematics, story sequences, campaign-only characters and AI). Touch
  singleplayer content only when multiplayer depends on it, and note the dependency.
  Noted dependencies: Musou Mode's Warboss draws the campaign's `warboss_regular` body and `cinematic_warboss` head
  (`ebp_sets__sequence2_core_units`; docs/discovery.md "Warboss model"); Musou's Stormboy uses the campaign's `boy_stormboy` +
  `havok_stormboy` from `ebp_sets__s002-001` (docs/discovery.md "Exterminatus Ork roster").
  Musou's Ashfall wind bed plays `play_wind_open` from the campaign region bank `rgn_onthetrail_r100_player_01` (no wind in the MP banks; musou/soundscape 2026-11-12).
- Content map (Verified-local by archive names): PvP = `data/chunks/mp_*`, `ebp_sets__mp_versus_set`,
  `wargear__mp_sm`/`wargear__mp_csm`, `data/packages/*_mp_*`; Exterminatus = `data/chunks/cp_maps__*`/`cp_map_*`,
  `ebp_sets__mp_coop_*`, `ork_core_units`/`chaos_core_units`; shared = `player_set`, packages `sm_*`/`csm_*`,
  camera/aim tuning packages. Singleplayer = `part_0*`, `ebp_sets__s0*`/`seq*`/`campaign_*`, `cinematic*`, `entourage_*`.

## Settled decisions (do not reopen without a concrete blocker)
- Independent engine reimplementation that loads the user's installed content at runtime.
- Original files and anything extracted or converted from them never enter the repo or builds.
- License: GPL-3.0 (decided 2026-10-26). Code derived from GPL-3.0 tools is allowed with its source named; code from
  tools without a usable license still is not.
- Stack: .NET 10 LTS for readers, simulation, networking, and dedicated server; Godot 4.7.2-stable .NET for the client.
  Reasons are in `docs/architecture.md`.
- The user wants minimal personal effort. Automate; ask only when blocked.

## Hard rules
- Test runs (agents, bots, playtest scripts) start servers with `--bind 127.0.0.1`: a server listening on every
  interface makes Windows Firewall ask the owner about each new SmServer copy.
- Nothing in tests, tools or test runs binds 0.0.0.0 / every interface (owner, 2026-10-28: Windows Firewall prompts for testhost, Godot
  and SmServer): sockets bind 127.0.0.1, servers `--bind 127.0.0.1`, the Host panel and `Host.cmd` are tested in "This PC only" mode.
- Test runs never disturb the owner's desktop (owner, 2026-10-28 and 2026-10-31: no window may flash or take focus): start Godot only through
  `godot.cmd` (headless by default, no console window); a windowed client (only when a screenshot or recording needs rendering)
  uses `godot.cmd --windowed`, which runs it on a private hidden desktop. Never run Godot or its console exe directly.
- The game install is **read-only**. Open with `FileAccess.Read`. Never write, rename, or patch inside it,
  including saves, config, Steam files, and online services.
- Private cache: `%LOCALAPPDATA%\sm-reimpl\cache` (outside the repo). Never commit or package it.
- No original bytes in tests. Generate synthetic fixtures (`SyntheticObpkWriter`).
- No absolute local paths or account IDs in committed files. The machine path goes in `config/local.json` (git-ignored).
  That covers docs, comments, spec rows and release notes too (owner, 2026-11-10: "Scrub local pc folder paths ands any other sensitive or identifying information from public view"): write `<media folder>\...`, `%LOCALAPPDATA%\sm-reimpl\...` or "the owner's media folder", never an expanded local path (`PublicTextGuardTests` enforces it).
- Out of scope: executable decompilation, binary patching, DRM circumvention, replacing online services.
  Do not reuse code from tools without a usable license (e.g. smbod, which is decompiled).
- Use the approved brief and the coordination lead protocol for branch pushes. The owner merges by default. Only an explicitly authorized lease holder may merge or push game main. Tester publication or deployment requires separate owner ship approval for the exact candidate. Spending requires separate owner authorization.
- Label results accurately: *original asset loaded* / *placeholder demonstration* / *supported by evidence* /
  *provisional approximation* / *implemented but untested*.
- Record every gameplay parameter in `spec/behavior.md` with value, units, timing basis, source, confidence, and kind.
  Keep original units when their meaning is unknown.

## Everyday commands
```bat
dotnet build SmReimpl.slnx && dotnet build client/SmClient.csproj   :: both builds
dotnet test                                                         :: all tests (fails when the refine register is stale)
smtool.cmd refine-register [--check]  :: regenerate docs/refine-register.md after any spec/behavior.md or changes-from-original.md edit
faithful-build.cmd                    :: extras-disabled solution/client builds and tests (-p:SmExtras=false), work and checks deferred
client.cmd                            :: the front end;  client.cmd --connect 127.0.0.1:<port> [--bot] [--class c] [--side s]
server.cmd --bind 127.0.0.1 --port <p> [--mode annihilation|seize] [--honor-side-requests] [--bot-damage-scale f]
bots.cmd --connect 127.0.0.1:<port> --marines N --chaos M   ::  bots.cmd --stop
release.cmd -Smoke                    :: build and smoke-test the tester zip locally (PowerShell)
ship.cmd [-DryRun] [-Playtest]        :: owner's one-command playtest release (docs/releasing.md)
```
The full reference of every command, flag and test aid is in `docs/commands.md`.

For lookups, use source search. For shared simulation or network impact, consult the [coordination tool-choice rule](https://github.com/Yeezima/agent-coordination/blob/main/operations/lead-protocol.md#11-source-search-and-graphify). `Extras*` partial-method hooks are a known graph blind spot. Commands, stale-index handling and the lead's post-fast-forward index maintenance are in [the command guide](docs/commands.md#graphify-code-graph).

## Historical Classic alpha scope
The October 27–28, 2026 alpha targeted Annihilation and Seize Ground on Manufactorum. Exterminatus was deferred. That alpha included the ten-slot custom-class and armour customizer, and all equipment was unlocked. Use the current approved brief for active work.

## Current work and published builds
Read the [coordination lead protocol](https://github.com/Yeezima/agent-coordination/blob/main/operations/lead-protocol.md) for roles, gates and verification. Read the [lease and latest handoff](https://github.com/Yeezima/agent-coordination/blob/main/state/lead.md) and [active batches](https://github.com/Yeezima/agent-coordination/blob/main/state/active-batches.md) at the coordination commit named in the assignment. The owner-approved brief defines current scope.
Identify the last shipment from [published tester releases](https://github.com/Yeezima/sm-reimpl-releases/releases). Record the candidate separately by full game SHA and build ID. Warfront design and evidence are in `docs/warfront.md` and `docs/warfront-test-report.md`.
The [October next-action list](https://github.com/Yeezima/sm-reimpl/blob/84132607baca47456c09e0fcc728c096881eb1cb/CLAUDE.md#L98-L108) preserves the old tuning questions. Consult the refine register and later owner decisions before treating an item as open.
The history of every milestone and fix (formats, systems, decisions, test aids, the switches that restore old behaviour) is in
`docs/milestones.md`; search it for a system's name before changing that system. Add a short paragraph there after each milestone.
This repository was published fresh on 2026-10-29 (one root commit); never merge a branch from the older private history.

## Where things are
- Milestone history: `docs/milestones.md`. Command reference: `docs/commands.md`. Releases: `docs/releasing.md`.
- Formats and sources: `docs/discovery.md`. Design: `docs/architecture.md`. Plan: `docs/roadmap.md`. Behavior: `spec/behavior.md`.
- Differences from the original (deliberate changes, our choices, missing features): `docs/changes-from-original.md`. Add a row there with every deliberate change or "ours" rule.
- Custom classes, customizer and perks (class rules, 48 perks with effects as data, armour sets, paint, unlocks, front-end text): `docs/customizer-and-perks-data.md` (`smtool baf-dump`, `smtool locale`).
- After meaningful progress, update the applicable technical docs and `docs/milestones.md`. The lease holder records live queue, evidence and handoff state in the coordination repository.
- Discord, promo and feed tooling (bot posts, the live tracker, changelog posting, ops notes) live outside this repository.
