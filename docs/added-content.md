# Added content: the boundary

The owner (2026-10-28, verbatim): "i want to retain the ability to separate the 'added content' work from the rest of the project at any
time. the first 'meta' milestone for the project is still a '95% or more faithful reproduction of sm1 with a new game engine'."

**Added content** is a whole system, faction, mode or mechanic SM1 multiplayer did not have, removable in one piece: the Ork Boy, gore, future
executions. The owner's changes to things SM1 had (the bolter balance, the blind grenade by facing, the spawn-room blockers above jump height,
...) are not added content: they are tagged in `docs/changes-from-original.md`, not switched. Customizer additions (lens colour, imported
emblems, 10 slots, the Host panel) also stay outside the switch.

The plan this follows: the added-content plan of 2026-10-28, tasks E1 to E3, G1 to G6, D1
and O-S1 to O-S4. E1 (this switch) is done; the rest is listed at the end.

## 1. The runtime switch

| Piece | What |
|---|---|
| Flags | `SmSim.ContentExtras` (`[Flags]`, u32): `None` 0 (the faithful game), `Orks` 1, `Gore` 2, `LoyalistBarks` 4 (section 7; `--content loyalist-barks`). A new added content takes the next free bit. |
| Profiles | `--content faithful` (no added content), `--content extended` (every flag this build compiled), or a list: `--content orks`, `--content orks,gore`. Same flag on `smserver` and on the networked client (`client.cmd --connect ... --content faithful`). `ContentProfiles.TryParse`, `Name`, `Describe`. |
| Default | `ContentProfiles.DefaultName` = `extended` (owner, 2026-10-29: "Default to the extended profile (Ork and gore on); faithful-only stays one switch away."). A tester or release build that ships the faithful game changes that one line. A build without extras always runs `faithful`. |
| In the match | `SimConfig.Extras` holds the flags. `GameplayTuning.LoadForMatch(gameDir, extras, orkData)` is the one place the Ork is gated (`extras.HasFlag(Orks)`) and sets `Extras`; the server and the client both call it. |
| Config hash | `SimConfig.Hash` writes an extras block (the string `extras`, the flags, `ContentExtras.WireVersion`, then the `ExtrasHash` hook) **only when the flags are not None**, so a faithful config hashes byte for byte as before the switch (pinned in `ContentProfileTests` against hashes captured on main before E1). A client with another profile is refused (`ConfigMismatch`); the refusal names the server's profile, flags and extras wire version. |
| Extras wire version | `ContentExtras.WireVersion` (2 since the loyalist barks: the envelope, message kind 16, and the action key, input bit 13; 1 before). Raise it for an extras-only change (a wire section, a simulation rule of an extra); `Protocol.Version` stays the faithful wire's version. |
| Server log | `smserver` prints `content: <profile>` at startup and which `--content` its clients need. |
| Refused clients | The client prints `rejected by the server: ...` with the server's profile; a `--bot` then ends with exit code 4 (so `bots.cmd` runs against a server with another profile do not idle). |
| Host panel | The front end's "Host a match" starts the server with the client's own `--content` (`HostOptions.Content`, `ServerLaunch.Arguments`), so the client that joins it matches. |

**Running a faithful match** (on the usual build, nothing to rebuild):

```powershell
.\server.cmd --content faithful --bind 127.0.0.1          # prints "content: faithful (SM1 multiplayer alone ...)"
.\client.cmd --connect 127.0.0.1:27015 --content faithful  # or the front end started with --content faithful (its Host panel passes it on)
```

### The profile in the front end and the customizer (E2)

- **Join screen:** a CONTENT row (Faithful / Extended; Extended is disabled in a build without added content) with a one-line note. The choice is
  remembered in `frontend.json` next to the profile (outside the repository; `FrontEndSettings.Content`, default `extended`), `--content` seeds it for
  that run, and Join and "Host a match" use it (the hosted server is started with `--content <choice>`, `127.0.0.1` unless "LAN / Internet" is chosen; the row
  is fixed while that server runs). A match started from the front end plays the Join screen's choice, and after a match the front end keeps the
  player's in-session choice (the `--content` seed is used once). A `--connect` with a profile (`--profile` or `--loadout`) plays `--content` when given,
  else the saved Join choice; a `--connect` without a profile plays `--content` or the default. An invalid `--content` is refused with a message and exit
  code 2 on every path (as the server's).
- **Faithful hides the Ork:** the faction step and the class creator offer the Space Marines and Chaos only (`CustomizerServices.Factions`, over
  `ContentGate.Factions`); the in-match picker (C on the death screen) uses the match's own flags; a Join or loadout change never carries an Ork class or an Ork
  look (`ContentGate.ForJoin` / `OrkLook`, applied in `JoinPlan.Build` and `FrontEndLaunch.Apply`). The customizer's Ork step (O9, ui/customizer-ork) takes its
  factions and class tabs from the same list (`CustomizerServices.Factions` / `Roles` / `OffersOrks`), so the Orks button, the Ork Boy tab, his DEFAULTS chip
  and his LOOK step show only with the flag; in Faithful a saved Ork loadout (custom or the Ork DEFAULTS) is never opened in the editor.
- **A saved Ork loadout** (role `ork`, saved by the O9 creator; `ProfileStore` keeps it and the profile's `orkLook` in every profile) stays in the profile, shows "unavailable here" on its chip and joins as the Tactical defaults of the
  side in a profile without the Orks (`LoadoutAvailability`).
- **A faithful server** gives an Ork class request the default class, ignores the Hello's and Setup's Ork look, never draws anyone as race 3
  (`GameServer.PlayableRace`), and a faithful client drops an Appearance of race 3; all of it holds for a config that has an Ork class without the flag
  (`ContentGate`, tests in `ContentGateTests`).
- **Checks:** `ContentGateTests` (server, client and the rules) and the model tests are the real coverage of what a profile hides. `--ui-selftest --profile <file
  outside the repo>` (headless) ends with content checks on the real screens: the picker and its saved choice (restored at the end), the factions the
  customizer lists, the unavailable
  saved slot and the Hello without the Ork class and look; then the Ork checks (`UiSelfTest.Ork.cs`): in Extended the Orks button, tab, DEFAULTS chip,
  preview, fixed gear, perks, LOOK step, a saved Ork loadout and a Hello with his class and look; in Faithful none of it, and no way into the Ork editor.
  It passes on a fresh profile and on a repeat run.


## 2. The build switch

- The root `Directory.Build.props` sets `SmExtras` (default `true`) and, when true, defines `SM_EXTRAS`. The root `Directory.Build.targets`
  applies `<Compile Remove="Extras/**" />` when it is false (in the props file the removal would run before the SDK's default Compile glob
  and remove nothing). Both apply to every SDK project and to the Godot client (`client/SmClient.csproj`).
- `ContentExtras.Compiled` is true when the build compiled the extras. A build without them refuses any profile but `faithful` (the server
  exits with code 2 and "this build has no added content").
- `faithful-build.cmd` is **the compile-time gate**: it builds `SmReimpl.slnx` and `client/SmClient.csproj` with `-p:SmExtras=false` and runs
  `dotnet test` with it, so any core file that needs an `Extras/` folder fails there. Its outputs go to the ignored `.faithful-build/` folder
  of the repository: the SDK projects through `--artifacts-path` (inside the repository because several tests find it from their output
  folder), the Godot client through `Directory.Build.props` (`.faithful-build/godot`: the Godot SDK sets its own output folders and ignores the
  artifacts path, so without that a faithful build would overwrite `client/.godot/mono/temp`, the client Godot runs; checked untouched). The
  usual `bin/`, `obj/` and `.godot` builds stay as they are. Extra arguments go to `dotnet test` (e.g. `--filter`). It is a check, not a way
  to play: a faithful match runs on the usual build with `--content faithful` (section 1). Its server
  (`.faithful-build/bin/SmServer/debug/SmServer.exe`) does run, and refuses any profile but faithful.
- `#if SM_EXTRAS` appears only in `src/SmSim/ContentExtras.cs` today. The composition roots (`src/SmServer/Program.cs`, `src/SmTool/Program.cs`)
  are allowed to use it should they need to; nothing else is. Everything else asks `ContentExtras.Compiled` or the match's `SimConfig.Extras`.

## 3. Folders, namespaces and hooks

- Added content lives in `Extras/<Feature>/` folders in every project: `src/SmSim/Extras/Gore/`, `src/SmContent/Extras/Gore/`,
  `src/SmNet/Extras/Gore/`, `src/SmServer/Extras/Gore/`, `src/SmTool/Extras/<Feature>/`, `client/Extras/<Feature>/`, `tests/*/Extras/<Feature>/`.
- New types go in the namespace `<Project>.Extras.<Feature>`. Partial extensions of core types keep the core namespace; their files are
  named `<Type>.<Feature>.cs` and live in the feature's `Extras/` folder.
- Core code reaches added content only through **hooks**: partial methods named `Extras*` (void, results through `ref`), declared in an
  allow-listed core file and implemented in an `Extras/` folder. Unimplemented, they compile away. Each call site carries the comment
  `// EXTRAS-HOOK`; each implementation returns at once unless its flag is on (`Config.Extras.HasFlag(ContentExtras.X)`).
- Hooks today (E1; the envelope's and the loyalist barks' below): `SimConfig.ExtrasHash(BinaryWriter)` (declared in `src/SmSim/ContentExtras.cs`, called in `SimConfig.Hash`) and
  `static GameplayTuning.ExtrasLoad(ref SimConfig, string gameDir, List<string> notes)` (declared and called in
  `src/SmContent/GameplayTuning.cs`). Planned with gore (G1b, E3): `World.ExtrasOnKill`, `GameServer.ExtrasSend`, `GameServer.ExtrasOption`,
  `NetworkView.ExtrasRagdollStarted`, `NetworkView.ExtrasFrame`, `NetworkView.ExtrasImpact`.
- Hooks added with the loyalist barks (section 7): `GameServer.ExtrasSend()` (E3's envelope, `src/SmServer/GameServer.Envelope.cs`, once a server tick after the pickups, only with added content), `NetworkView.ExtrasInput(ref PlayerInput)` and `NetworkView.ExtrasFrame(PlayerState, IReadOnlyList<PlayerState>, double)` (`client/NetworkView.cs`: every live sampled input; once a frame after the sound) and `NetworkView.ExtrasKill(KillView)` (`client/NetworkView.Match.cs`: each kill-feed entry once). A C# partial method has one implementation: when a second feature needs one of these, the first feature's implementation becomes a small dispatcher in `Extras/` that calls each feature (the client's `ExtrasFrame` also drains the envelope queue, `GameClient.TakeExtras`, and must hand each tag to its owner).
- Added content's own files (sounds, data) live in `Extras/<Feature>/assets/` with a `.gdignore` (Godot never imports or exports them) and are embedded in the client assembly only when `SmExtras` is true (`client/SmClient.csproj`; `Directory.Build.targets` removes `Extras/**` embedded resources otherwise), so a faithful build and its Godot export carry none of them.
- `tests/SmNet.Tests/ExtrasBoundaryTests.cs` checks this as text: no file outside an `Extras/` folder names an extras namespace
  (`<Project>.Extras...`, a bare `Extras.<Feature>....`, or `using` / `namespace` of one; member access such as `Config.Extras.HasFlag` is
  fine); `Extras*` partial declarations only as the pinned (file, hook name) pairs of its `Hooks` list, every hook call marked; `SM_EXTRAS` in
  a preprocessor condition only in its `SymbolFiles` list. It is an early text guard; `faithful-build.cmd` is the real gate.

## 4. The rule for future added content

1. It lives in `Extras/<Feature>/` folders and the `<Project>.Extras.<Feature>` namespaces.
2. It owns a `ContentExtras` flag, is on in `extended`, and does nothing unless its flag is set in the match's config.
3. It adds no field to snapshots, kill entries or the Hello: what it sends goes in its own section of the extras envelope (E3: message kind
   16, `[16][u32 tick][u8 n]{[u8 tag][u16 len][payload]}`, unknown tags skipped; a faithful server never sends it).
4. Every new hook is a partial `Extras*` method in a core file, added as (file, hook name) to `Hooks` in `ExtrasBoundaryTests` with a note,
   its call marked `// EXTRAS-HOOK`.
7. `faithful-build.cmd` passes before it merges.
5. Its spec rows go under a heading `Added content: <Feature> — ...`; its rows in `docs/changes-from-original.md` go in section 0 (D1).
6. An extras-only change to the wire or the simulation raises `ContentExtras.WireVersion`, not `Protocol.Version`.

The client-to-server direction has one message of its own, kind 17 (`ExtrasCommandMessage`, Musou Mode TF0, no protocol bump as the envelope and the action key: `[17][u32 session][u8 tag][u16 len][payload, at most 64 bytes]`): a client sends it only under `ContentExtras.CommandUsers` (`GameClient.SendExtrasCommand`), and any other server drops it unread and sends nothing back (`GameServer.ExtrasCommandsDropped`; pinned by `ExtrasCommandIsolationTests`, which runs in the faithful build too). Accepted commands (8 a second per client) reach the hook `GameServer.ExtrasCommand(int player, byte tag, byte[] payload)` on the packet path: the feature queues them for its next tick and treats the bytes as untrusted. Tags here are their own namespace, not the envelope's.

## 5. How to split

- **A faithful build at any time:** `faithful-build.cmd`, or any `dotnet build -p:SmExtras=false`.
- **A faithful tree:** `git rm -r '**/Extras/**'` (the hooks compile away) and set `SmExtras` to `false` in `Directory.Build.props`, so
  `ContentExtras.Compiled` is false and only `faithful` runs. Until O-S1 to O-S4 the Ork's code is still in core files (gated at runtime only).
- **A separate repository for the added content:** `git filter-repo --path-glob '**/Extras/**'` (on a copy) keeps only the extras' history;
  that repository is used as an overlay on the faithful tree (or through a future `SmExtrasDir` property).
- **A faithful match on an extended build:** `smserver --content faithful` and `client.cmd --connect ... --content faithful`.

## 6. State (E1) and what is left

Done in E1: the flags and profiles, the config-hash block, the Ork runtime gate, `--content` on the server and the networked client, the Host
panel passing it on, the refusal text, the build property, `faithful-build.cmd`, the boundary and profile tests. The default (extended) match's
config hash changed with E1 (the extras block), so builds from before and after E1 refuse each other. The Ork code itself has not moved yet: it still lives in core
files and is only gated at runtime (its move into `Extras/Orks` is O-S1 to O-S4).

E2 (the profile in the front end and the customizer, the server's Ork refusal) is done on branch `extras/e2-ui`. E3's envelope (message kind 16, `ExtrasUpdate`, `GameServer.SendExtras`, `GameClient.TakeExtras`) came in with the loyalist barks (branch `extras/loyalist-barks`), without a protocol version of its own: `ContentExtras.WireVersion` 2 (the parked `extras/e3-envelope` had numbered it protocol 31; its tests are kept). Left (other tasks): G1a/G1b
gore on the boundary; D1 the docs (changes-from-original section 0, spec headings, the register's "Added content" part).

## 7. Loyalist barks (`LoyalistBarks`, flag 4)

The owner (2026-10-29, verbatim): "use these to create a voice line bark system for the loyalist marines only. add a keybind for the player to play the
voiceline 'FOR THE EMPEROR' at any time. it shouldn't interrupt any action or play any animation." "These" is the maintainer's own voice pack (released with the project under the GPL-3.0; see `THIRD_PARTY_NOTICES.md`): 32 takes
(8 phrases x 4) synthesized with ElevenLabs (model eleven_v4, voice "SM-Reimpl Loyalist - Voice 1") with a helmet filter baked in, not extracted from any game
(provenance in `THIRD_PARTY_NOTICES.md`).

| Piece | What |
|---|---|
| Assets | `client/Extras/LoyalistBarks/assets/`: the 32 OGG takes (`audio/`) and a slimmed manifest (`loyalist-barks.json`: id, file, text, phrase, the pack's event, take, duration, SHA-256; the pack's WAV masters, dry takes, previews and generation records are not in the repository). A `.gdignore` keeps Godot from importing or exporting them; they are embedded in the client assembly with added content only. Checked by `LoyalistBarksTests` (manifest = files, hashes) and `LoyalistBarkAudioTests` (every take decodes mono at 48 kHz, its length, headroom). |
| Who | loyalist Space Marines only (`Races.Of` = Space Marines): never Chaos, never an Ork, on either side. The client sends the key's bit only for a living loyalist; the server refuses anyone else's press. |
| When (ours) | **off by default since the owner's decision of 2026-10-29 (only the key plays; `--loyalist-auto-barks` turns these on):** the charge: a battle cry (chance 0.6); a kill (the kill feed, an enemy): an attack line or a battle cry (0.5); a melee attack's start: an attack line (0.15); the start of bolt-weapon fire after 2 s without a shot: "Eat boltgun!" (0.1); the own player's first damage after 5 s without any: "Taking fire!" (0.35; only the own client knows it, so only the own player hears it). Never per bullet or per damage tick. Rolls and takes are seeded by the event, so clients that see it mostly agree. `LoyalistBarkRules`. |
| Limits (the pack's suggestions; ours) | one line at a time per speaker; 8 s between a speaker's random barks; 20 s before a speaker says the same phrase again; never the speaker's last take of a phrase; random barks start at most every 1.5 s anywhere. |
| SM1's own speech | yields: a random bark is not started while M33's exertion or action line of that character plays, and stops when one starts. The key's shout cuts the character's SM1 line (the player asked for it). |
| The key | T (no other binding uses it): a FOR THE EMPEROR take at any time, at most every 2 s (60 ticks, counted in ticks by the server and the shouting client alike). A press sends `InputButtons.ExtraAction` (input bit 13) for 4 ticks (`LoyalistShoutWire.HoldTicks`), so one lost or late input does not lose it; the simulation never reads the bit (no action is interrupted, no animation plays, no state changes: pinned by a test). The server accepts the press edge of a living loyalist and tells every client in the envelope's section tag 2 (`LoyalistShoutWire`: `[u8 n]{[u16 player][u32 tick][u8 kind][u8 sequence]}`, repeated in 3 ticks' messages, played once per player and tick, not when older than 1 s against the latest snapshot's tick). The take is a function of the player and its shout sequence alone (`LoyalistBarkRules.KeyTake`: a per-player order of the four takes, one step a shout): every client hears the same take, never the same one twice in a row; the shouter hears it at the press from its predicted next sequence (one the server refused would shift its own take until the next echo). |
| Loading | the 32 takes are read from the assembly on a worker when the match starts and their streams built on the main thread, two a frame (never on a key press; a malformed or missing manifest turns the feature off); a take that fails to load is kept as failed and never retried. Until the sound system and the takes are ready nothing is decided, so no cooldown is spent on a bark nobody hears (`--no-sound`: the key still travels, nothing plays). |
| Playback | others in 3D from their characters (`AudioStreamPlayer3D`, full level within 8 m, inverse-distance, silent beyond 70 m; ours), the own player without position; on the sound bus with the master bus volume and `--sound-gain`, base level -2 dB (ours). No second helmet filter. |
| Wire | no protocol bump: the envelope (kind 16) and bit 13 are sent only by an extended client or server, whose configs hash differently from a faithful one; `ContentExtras.WireVersion` 1 → 2. Snapshots unchanged (worst case 1200). A server whose match runs no added content that reads the bit (`ContentExtras.ActionKeyUsers`) drops it from every input it stores and sends no envelope; the bit alone never counts as play for the idle kick (`GameServer.IsRealInput`). |
| Test aids | `--no-loyalist-barks`, `--log-sound loyalist` (every decision: said, chance, cooldown, spacing, phrase cooldown, speaking, SM1 speech), `--loyalist-gain <dB>`, `--bot-shout <s>` (a bot presses T every s seconds); bots print a summary every 10 s; the server logs each accepted shout. |
| Code | `src/SmContent/Extras/LoyalistBarks/` (`LoyalistBarkPack`, `LoyalistBarkRules`), `src/SmNet/Extras/LoyalistBarks/LoyalistShoutWire.cs`, `src/SmServer/Extras/LoyalistBarks/GameServer.LoyalistBarks.cs`, `client/Extras/LoyalistBarks/NetworkView.LoyalistBarks.cs`; tests in `tests/*/Extras/LoyalistBarks/`. |

## 8. Musou (`Musou`, flag 8; experimental, MU0 of MU0–MU4)

The owner (2026-10-31): a separate experimental game mode, "labeled Musou", toggled in the menu like Faithful and Extended, which must not
change the existing modes at all: local play on Manufactorum, massive simple swarm enemies (Orks, Chaos) in about five waves converging
on the player, an Ork Warboss with a few abilities at the end, melee cleave in this mode only. Design: the lead's Musou design note
(milestones MU0 switch, MU1 sim and director, MU2 network and crowd, MU3 combat, boss and HUD, MU4 tuning).

| Piece | What |
|---|---|
| Switch | `--content musou` (`ContentProfiles.Musou`): a profile of its own, never combined with another flag and **not** in `ContentExtras.All`, so `extended` and its config hash are unchanged (`ContentExtras.Known` = All + Musou). Its own version (`MusouSim.Version`) is hashed in its `ExtrasHash` block; `ContentExtras.WireVersion` stays 2 and the protocol is unchanged by Musou (it is 50 since the scoreboard ping, net/scoreboard-ping). No `--mode musou`: the server refuses any mode but Annihilation under Musou (exit 2), with no score or time limit. |
| Hooks (MU0) | `World.ExtrasStep` (after the perk blasts), `ExtrasMelee` (end of `ResolveMelee`), `ExtrasBlast` (end of `ApplyBlast`, also on its blind-only return), `ExtrasStepMatch(ref handled)` (top of `StepMatch`), `ExtrasJoinSide(ref want, ref forced)` (`AssignClass`, `AssignSide`), `GameServer.ExtrasCreated` (end of the constructor). Each is a no-op unless `World.StartMusou` ran, which only a Musou profile does. |
| Dispatchers | one implementation per partial method: `GameServer.Extras.cs` (`ExtrasSend`: barks, then Musou; `ExtrasCreated`), `client/Extras/NetworkView.Extras.cs` (`ExtrasFrame` drains the envelope queue once and routes by tag: 2 barks, 3–5 Musou), `SimConfig.Extras.cs` (`ExtrasHash`), `GameplayTuning.Extras.cs` (`ExtrasLoad`). |
| Wire | envelope tags 3 (enemies, 10-byte records quantised over the nav box), 4 (events, 16 bytes), 5 (director, 44 bytes), 6 (a player's own weapon drops, 11-byte records, sent only to that player); `MusouWire`. |
| Front end | CONTENT in the lobby menu reads Faithful / Extended; MUSOU MODE on the main menu (the owner, 2026-11-01) starts Musou on this PC with `--content musou` and leaves the remembered profile alone; a Musou remembered by an earlier build falls back to Extended; with Musou the Host panel is This PC only, Annihilation, no bots, START MUSOU, and gives back the earlier mode and reach when Musou is deselected. |
| Isolation | `MusouIsolationTests` (core) pins the faithful and extended config hashes and a 900-tick replay hash captured on the code before the hooks; a loopback test shows faithful and extended servers never send tags 3–5. |
| Code | `src/*/Extras/Musou/`, `client/Extras/Musou/`, `tests/SmNet.Tests/Extras/Musou/`. |
| Musou Mode (TF0) | The owner's decision (2026-11-01): Musou Mode is this war. `--content musou` runs the war on the map `tfw_war` (`TfwMap`; an empty arena and a Stormbird room from TF1, Manufactorum and a Stormbird since pass 2's M1), and there is no Host choice; `--map mp_tdm_09` keeps the 5-wave run as a test aid (`MusouSkirmishPinTests` pins it), `--map tfw_war` on another profile exits 2. Hooks: `World.ExtrasPlayerInput` (before `Step` asks for an input: players without a peer), `GameServer.ExtrasGeneratedMap` (static, through `GameServer.GeneratedMap`, which `Program.cs` calls), `GameServer.ExtrasCommand` (kind 17, section 4), `GameServer.ExtrasPlayerName` (`ScoreboardOf`), `NetworkView.ExtrasMapFile` and `ExtrasMapCollision` (in `LoadMap`). The dispatchers call `CreateTfw`, `SendTfw`, `OnTfwCommand`, `TfwPlayerName`, `TfwGeneratedMap`, `TfwFrame`, `TfwMapFile`, `TfwMapCollision`; the World's Musou hooks go to `World.Tfw.cs` when `World.Tfw` is set. Envelope tags 7 to 10 (`TfwWire`), `MusouPhase.Campaign` 6, `TfwRules.Version` in Musou's hash block. Code: the `Campaign/` folders under `src/*/Extras/Musou/`, `client/Extras/Musou/` and `tests/SmNet.Tests/Extras/Musou/`. |
| Test aids (TF9) | `GameServer.TfwWinTest.cs` (`SM_TFW_WIN_TEST`, `SM_TFW_STRESS`) sits behind the feature's own `SendTfw`; `GameServer.TickStats.cs` (`SM_TICK_STATS`) is core, not added content: two calls in `Update` and a counting transport, nothing without the variable. |
| The name | The owner (2026-11-01): "From now on three-front war is called Musou Mode." `Tfw` in code means Musou Mode: identifiers, files, flags, wire names and the map name (`Tfw*`, `tfw_war`, `--tfw-*`) keep it to avoid churn. The older 5-wave run (`--map mp_tdm_09`) is a test aid and is not Musou Mode, though its code stays under `Extras/Musou/`. |
| Musou Mode (TF4) | `TfwBattle`, `TfwRun` and `World.Tfw.cs` run the war: the run is stepped from `TfwStep`, `TfwStepMatch` replaces the Musou flow, `World.TfwCreditKills` wraps the KO credit, `GameServer.FeedTfw` fills TF6's feed. Spec "the battle and the run". |
| Musou Mode (TF0) | The owner's decision (2026-11-01): Musou Mode is this war. `--content musou` runs the war on the generated map `tfw_war` (`TfwMap`; a stub flat slab until TF1), and there is no Host choice; `--map mp_tdm_09` keeps the 5-wave run as a test aid (`MusouSkirmishPinTests` pins it), `--map tfw_war` on another profile exits 2. Hooks: `World.ExtrasPlayerInput` (before `Step` asks for an input: players without a peer), `GameServer.ExtrasGeneratedMap` (static, through `GameServer.GeneratedMap`, which `Program.cs` calls), `GameServer.ExtrasCommand` (kind 17, section 4), `GameServer.ExtrasPlayerName` (`ScoreboardOf`), `NetworkView.ExtrasMapFile` and `ExtrasMapCollision` (in `LoadMap`). The dispatchers call `CreateTfw`, `SendTfw`, `OnTfwCommand`, `TfwPlayerName`, `TfwGeneratedMap`, `TfwFrame`, `TfwMapFile`, `TfwMapCollision`; the World's Musou hooks go to `World.Tfw.cs` when `World.Tfw` is set. Envelope tags 7 to 10 (`TfwWire`), `MusouPhase.Campaign` 6, `TfwRules.Version` in Musou's hash block. Code: the `Campaign/` folders under `src/*/Extras/Musou/`, `client/Extras/Musou/` and `tests/SmNet.Tests/Extras/Musou/`. |
| The war's HUD (TF7b) | `client/Extras/Musou/Tfw/`: `NetworkView.Tfw.cs` (the frame, beside TF7a's `TfwGuardsFrame`, which reads the sections into `_tfwState` and sends the commands), `TfwStations.cs` (prompts, E, the M overlay), `WarMapPanel.cs`, `TfwOrders.cs` (Z, X, V, P), `TfwHud.cs`, `TfwMarkers.cs`, `NetworkView.TfwTest.cs` (test aids). Engine-free words and key rules: `TfwHudModel`. Tag 7 gained the own block, `TfwOwnView` (`World.TfwOwn`), 14 bytes per client: `TfwRules.Version` 4. No new hook: the open war map stands in the picker's place (`_pickerLayer`), so the core's rules for a menu hold unchanged. Order keys X and V give up Next weapon and Melee for the match, in the InputMap only (`TfwOrderKeysFree`). |
| The war's wire (TF6) | `TfwWire` holds the layouts of tags 7 (war, 71 bytes; 85 since TF7b), 8 (objectives, 10-byte records), 9 (Guardsmen, 10-byte records) and 10 (Guard events, 15 bytes). `GameServer.SendTfw` sends them only under `World.Tfw`, after `SendMusou`, from `GameServer.TfwFeed`, which the run fills; tags 8 to 10 wait for a director (its box). A client's commands are 4 bytes, 10 for Hold and Attack (their place); `TfwCommandSender` resends, `GameServer.OnTfwCommand` takes each sequence once. `TfwClientState` is the client's engine-free reader. |
| The war's wire (TF6) | `TfwWire` holds the layouts of tags 7 (war, 71 bytes), 8 (objectives, 10-byte records), 9 (Guardsmen, 10-byte records) and 10 (Guard events, 15 bytes). `GameServer.SendTfw` sends them only under `World.Tfw`, after `SendMusou`, from `GameServer.TfwFeed`, which the run fills; tags 8 to 10 wait for a director (its box). A client's commands are 4 bytes, 10 for Hold and Attack (their place); `TfwCommandSender` resends, `GameServer.OnTfwCommand` takes each sequence once. `TfwClientState` is the client's engine-free reader. |
| The Guardsmen as drawn (TF7a) | `client/Extras/Musou/CrowdView.cs` holds what the two crowds share (the skinned pool, the vertex-animation tier, the bodies); `MusouCrowd` and `GuardCrowd` say what to draw, `MusouVat.Start` takes the look's own loop rule. `GuardLooks` builds the original Guardsman on a worker and the main thread, as a `MusouLooks.Look`. `TfwFrame` calls `TfwGuardsFrame` (`Tfw/NetworkView.TfwGuards.cs`), one call so TF7b adds the HUD beside it. No new hook; `GuardRig` (rig profile, clip table, `GuardLasgun`) is in `src/SmContent/Extras/Musou/`. |
| Pass 2's contracts (M0) | Envelope tags 11 fire, 12 score, 13 bars, 14 reward (`TfwWire`; 11 to 14 need no box; nothing sends them yet), commands ChangeClass and ChooseReward, `MusouDeathKind` Barrage and Strafe, `Knocked` in both state enums, `TfwRules.Version` 6. `TfwClientState` reads the tags; `client/Extras/Musou/Tfw/NetworkView.TfwPass2.cs` holds the empty frames. No new hook. |
| Pass 2's rewards and score (M6, M6b) | The owner's items 3, 9, 11, 17, 25, 26. `World.TfwArsenal.cs` arms each life with the full loadout (the 5-wave strip stays for `--map mp_tdm_09`), keeps extra slots 3 to 5 beside an unchanged `PlayerState` (an extra stands in the primary's or pistol's field, the own weapon waits in a bank) and drops the core's boxes from Orks; `TfwRewards` holds the kits and offers, `TfwScore` the score, `TfwBest` the local best. Tag 12 and 14 now go out, tag 15 is new (`TfwArsenalView`), tag 6's state byte has a crate bit, ChooseReward names its offer, SelectSlot is new, `TfwRules.Version` 8. Core change: one extract, `World.DropPickup` in `src/SmSim/Pickups.cs`, no behaviour change; no new hook. |
| Pass 2's map (M1) | The owner's item 4: every battle on Manufactorum. `tfw_war` keeps its name and is one world: `SmContent.Extras.Musou.TfwWorld` loads `mp_tdm_09`, `TfwMap.Compose` adds the Stormbird placed from the map's bounds (`TfwHub.Beyond`), drops the spawn-room blockers and hashes `collision ^ salt ^ TfwMap.Version` (3). Hooks: `GameServer.ExtrasGeneratedMap` gains the install directory; `NetworkView.ExtrasMapFile(ref file)` (the install's map behind a name, for the map, its effects and sounds) and `NetworkView.ExtrasMapCollision(name, ref world, ref hash)` (after the level's collision) replace `ExtrasLoadMap`. `GameMap.AddedContent` carries the battlefield to `CreateTfw` (core field, never sent or hashed); `CollisionWorld.WithSolids` is an extras partial over one stored mesh. Places: `TfwSites` (hash-seeded), tag 8 gains each radius and the sites, `TfwRules.Version` 7. An install without the map: the server exits 2 with `TfwWorld.Missing`'s message, a client's map load fails with it. Tests use `TfwTestArena` and `TfwWorldTests`, synthetic levels. |
