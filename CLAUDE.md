# CLAUDE.md — sm-reimpl

## Goal
Reimplement the **multiplayer** of *Warhammer 40,000: Space Marine* (2011, PC, Steam app 55150) as faithfully as the
evidence allows. The engine reads content from each player's own install. The intentional change is server-authoritative
networking with dedicated servers. Everything else defaults to the original behavior: no redesign or rebalancing without a
reason tied to that goal. Not Space Marine 2 (2024) or the 2025 Master Crafted Edition (app 3169520).

Every estimate, guess or "ours" value is tagged (spec `kind`/`confidence`) and collected in the refine register
(`docs/refine-register.md`), the checklist for playtests that refine each feature toward the original.

**Added content stays separable (owner):** added content (Ork faction, Musou Mode — `Tfw` in code — with its map Ashfall,
gore, future extras) runs behind `--content faithful|extended|...` (`SmSim.ContentExtras`) and lives in `Extras/<Feature>/`
folders reached only through `Extras*` hooks. Mark original and added content separately as before; `faithful-build.cmd`
still exists but is no longer a required check (owner, 2026-11-07). Contract: `docs/added-content.md`.
**Musou never changes Classic PvP** (Annihilation, Seize Ground): no Musou change may touch their gameplay.

## Scope
- **In:** PvP (Annihilation, Seize Ground; Space Marines vs Chaos, up to 8v8) and Exterminatus (co-op waves, deferred), plus
  what they need: MP maps, classes, wargear, weapons, armour customization and paint, front-end flow. Added content as above.
- **Out:** the singleplayer campaign. Touch singleplayer content only when multiplayer depends on it, and note the dependency
  (Musou's Warboss uses campaign `warboss_regular` + `cinematic_warboss`; docs/discovery.md "Warboss model").
- Content map: PvP = `data/chunks/mp_*`, `ebp_sets__mp_versus_set`, `wargear__mp_sm`/`wargear__mp_csm`, `data/packages/*_mp_*`;
  Exterminatus = `cp_maps__*`/`cp_map_*`, `ebp_sets__mp_coop_*`; shared = `player_set`, packages `sm_*`/`csm_*`.
- Current alpha: Classic PvP on Manufactorum (`mp_tdm_09`) and Musou Mode on Ashfall. No progression: everyone has all
  weapons, perks and armour.

## Settled decisions (do not reopen without a concrete blocker)
- Independent engine that loads the user's installed content at runtime. License GPL-3.0; code from GPL-3.0 tools allowed
  with its source named, code from tools without a usable license is not (e.g. smbod, decompiled).
- Stack: .NET 10 LTS (readers, sim, networking, dedicated server); Godot 4.7.2-stable .NET client (`docs/architecture.md`).
- The user wants minimal personal effort. Automate; ask only when blocked.

## Hard rules
- Original files and anything extracted from them never enter the repo, tests or builds. Tests use synthetic fixtures
  (`SyntheticObpkWriter`). Private cache: `%LOCALAPPDATA%\sm-reimpl\cache`, never committed or packaged.
- The game install is **read-only** (`FileAccess.Read`); never write, rename or patch inside it.
- Never open `config/local.json` (machine paths and live tokens). No absolute local paths or account IDs in committed files.
- Nothing in tests, tools or test runs binds every interface: sockets bind 127.0.0.1, servers `--bind 127.0.0.1`.
- Test runs never disturb the owner's desktop: start Godot only through `godot.cmd` (headless; `--windowed` runs on a
  hidden desktop). Never run Godot or its console exe directly.
- Out of scope: executable decompilation, binary patching, DRM circumvention, replacing online services.
- Do not push, publish, merge or spend money unless the owner asks. Gameplay merges wait for the owner's playtest, UI merges
  for the owner's screenshot approval; docs merge at once.
- Label results accurately: *original asset loaded* / *placeholder demonstration* / *supported by evidence* /
  *provisional approximation* / *implemented but untested*.
- Record every gameplay parameter in `spec/behavior.md` (value, units, timing basis, source, confidence, kind); add every
  deliberate change or "ours" rule to `docs/changes-from-original.md`.

## Everyday commands
```bat
dotnet build SmReimpl.slnx && dotnet build client/SmClient.csproj   :: both builds
dotnet test                                                         :: all tests (fails when the refine register is stale)
smtool.cmd refine-register            :: regenerate after any spec/behavior.md or changes-from-original.md edit
client.cmd [--connect 127.0.0.1:<port>]
server.cmd --bind 127.0.0.1 --port <p> [--mode annihilation|seize]
bots.cmd --connect 127.0.0.1:<port> --marines N --chaos M
release.cmd -Smoke                    :: build and smoke-test the tester zip
ship.cmd -Linux                       :: playtest release (docs/releasing.md); then update the VM server and bots
```
Full reference: `docs/commands.md`.

## Where things are
- History of every milestone and fix: `docs/milestones.md` (search it before changing a system; add a paragraph after each).
- Formats: `docs/discovery.md`. Design: `docs/architecture.md`. Plan: `docs/roadmap.md`. Behavior: `spec/behavior.md`.
- Customizer and perks: `docs/customizer-and-perks-data.md`. Releases: `docs/releasing.md`.
- Testers get builds through the SM Reimpl Launcher (Yeezima/sm-reimpl-releases). This repo is public from one fresh
  commit (2026-10-29); never merge a branch from the older private history.
