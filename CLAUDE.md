# CLAUDE.md — sm-reimpl

## Goal
Reimplement the **multiplayer** of *Warhammer 40,000: Space Marine* (2011, PC, Steam app 55150) as faithfully as the
evidence allows. The engine reads content from each player's own install. The intentional change is server-authoritative
networking with dedicated servers. Everything else defaults to the original behavior: no redesign, rebalancing, or new
features without a reason tied to that goal. Do not confuse this game with Space Marine 2 (2024), or with the 2025 Master
Crafted Edition (app 3169520).

**Faithful first (owner, 2026-10-28):** the goal for now is a faithful SM1 multiplayer experience on our engine. New features
wait until later (the gore system is paused on branch `gore/g1-sim`); the Ork faction stays as the one sanctioned addition
(`docs/ork-faction-design.md`). Every estimate, guess, interpretation or "ours" value is tagged (spec `kind`/`confidence`) and
collected in the refine register (`docs/refine-register.md`, `smtool refine-register`), the checklist for human playtests that
refine each feature toward the original.

**Added content stays separable (owner, 2026-10-28):** "i want to retain the ability to separate the 'added content' work from the rest of the
project at any time." Added content (the Ork, gore, future extras) runs behind `--content faithful|extended|orks,gore` (`SmSim.ContentExtras`, in
the config hash) and lives in `Extras/<Feature>/` folders reached only through `Extras*` hooks; `faithful-build.cmd` builds without it. Contract and
the rule for new added content: `docs/added-content.md`.

## Scope (decided by the user, 2026-10-26)
- **In:** PvP (Annihilation, Seize Ground; Space Marines vs Chaos, up to 8v8) and **Exterminatus** (online co-op waves,
  which needs Ork/Chaos AI enemies). Plus whatever these need: MP maps, MP/co-op blueprint sets, player classes, wargear,
  weapons, armour customization and paint, progression and unlocks, front-end flow to get into a match.
- **Out:** the singleplayer campaign (chapters, cinematics, story sequences, campaign-only characters and AI). Touch
  singleplayer content only when multiplayer depends on it, and note the dependency.
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
- Out of scope: executable decompilation, binary patching, DRM circumvention, replacing online services.
  Do not reuse code from tools without a usable license (e.g. smbod, which is decompiled).
- Do not push, publish, or spend money unless the user asks.
- Label results accurately: *original asset loaded* / *placeholder demonstration* / *supported by evidence* /
  *provisional approximation* / *implemented but untested*.
- Record every gameplay parameter in `spec/behavior.md` with value, units, timing basis, source, confidence, and kind.
  Keep original units when their meaning is unknown.

## Everyday commands
```bat
dotnet build SmReimpl.slnx && dotnet build client/SmClient.csproj   :: both builds
dotnet test                                                         :: all tests (fails when the refine register is stale)
smtool.cmd refine-register [--check]  :: regenerate docs/refine-register.md after any spec/behavior.md or changes-from-original.md edit
faithful-build.cmd                    :: build and test without added content (-p:SmExtras=false)
client.cmd                            :: the front end;  client.cmd --connect 127.0.0.1:<port> [--bot] [--class c] [--side s]
server.cmd --bind 127.0.0.1 --port <p> [--mode annihilation|seize] [--honor-side-requests] [--bot-damage-scale f]
bots.cmd --connect 127.0.0.1:<port> --marines N --chaos M   ::  bots.cmd --stop
release.cmd -Smoke                    :: build and smoke-test the tester zip locally (PowerShell)
ship.cmd [-DryRun] [-Playtest]        :: owner's one-command playtest release (docs/releasing.md)
```
The full reference of every command, flag and test aid is in `docs/commands.md`.

## Alpha scope (set by the user, 2026-10-27)
Only Annihilation and Seize Ground; no other modes for now (Exterminatus deferred). Customizer: in since 2026-10-28 (owner's
request): a custom-class creator with 10 slots and a character (armour) customizer, matching the original in all but style.
No level progression system yet (owner, 2026-10-28): every player has all weapons, perks and armour from the start.
Only the map Manufactorum (`mp_tdm_09`) is developed further; it is the alpha arena.

## Current state (2026-10-29)
A playable pre-alpha ships to playtesters through the SM Reimpl Launcher (releases repository Yeezima/sm-reimpl-releases): Annihilation and
Seize Ground on Manufactorum, the six classes plus the Ork Boy (added content), customizer, perks, bots, the launcher and updater. Protocol 50 (since 2026-10-31: the scoreboard ping).
The history of every milestone and fix (formats, systems, decisions, test aids, the switches that restore old behaviour) is in
`docs/milestones.md`; search it for a system's name before changing that system. Add a short paragraph there after each milestone.
This repository was published fresh on 2026-10-29 (one root commit); never merge a branch from the older private history.

## Next action (2026-10-29)
1. A playable pre-alpha is shipped to playtesters with the SM Reimpl Launcher. Next: support for older hardware, the default
   lighting look, and making the code repository public. Merge each PR into `main` as soon as it is verified (user's rule, 2026-10-28).
2. Playtests of the pre-alpha (the refine register is the checklist); fixes from them.
3. Owner decisions pending: the charge's reach (spec "The charge", still open; the sprint margin is moot since protocol 39: W + F charges) and the side charge's 25 degrees (spec "Charge directions", an estimate). Weapons B (brace and vent): the owner's recollection is applied as estimates to check in a playtest; the owner kept the data's vent rate (about 2.1 s to cool a full gun); his 6 s recollection is to check in a later playtest. To verify in
   the original: the 10 s ready hold after a shot (the owner's rough estimate). (Decided
   2026-10-28 and done in M31: LMB tap/hold charged shot, the charge's sprint gate and jump-pack classes, the raise delay,
   no gear rattle, blockers above jump height; in M32: the charger's hyper armour (a stun ends a charge only after it), the
   ready hold, the raise after a switch and always-up weapons never waiting.)
4. Open, not blocking: class badges on the right pad (no class-to-decal mapping found), `pattern_enabled`; the 2.5 m
   rush variant; sound gaps left by M33 (per-node max instances, Play/Stop fades, state-driven properties; docs/discovery.md).

## Where things are
- Milestone history: `docs/milestones.md`. Command reference: `docs/commands.md`. Releases: `docs/releasing.md`.
- Formats and sources: `docs/discovery.md`. Design: `docs/architecture.md`. Plan: `docs/roadmap.md`. Behavior: `spec/behavior.md`.
- Differences from the original (deliberate changes, our choices, missing features): `docs/changes-from-original.md`. Add a row there with every deliberate change or "ours" rule.
- Custom classes, customizer and perks (class rules, 48 perks with effects as data, armour sets, paint, unlocks, front-end text): `docs/customizer-and-perks-data.md` (`smtool baf-dump`, `smtool locale`).
- After meaningful progress: update those docs, add a paragraph to `docs/milestones.md`, and keep this file's Current state and Next action short.
- Discord, promo and feed tooling (bot posts, the live tracker, changelog posting, ops notes) live outside this repository.
