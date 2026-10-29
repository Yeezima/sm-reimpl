# Architecture

## Stack (selected 2026-10-26; revisit only on a concrete blocker)

| Part | Choice | Exact version |
|---|---|---|
| Format readers, content pipeline, simulation, networking, dedicated server | C# on .NET | .NET 10 LTS, SDK 10.0.4xx (`global.json`), supported to 2028-11-14 |
| Client (rendering, input, audio, animation playback) | Godot, .NET build | 4.7.2-stable (2026-08-18) |
| Shared collision | Own static-mesh BVH and capsule controller in `SmSim` (no physics library) | decided in M7: both sides run the same code on the same triangles, loaded from each install; the handshake checks a hash of the source files |

### Why, from the evidence

- **No engine can import these formats.** OBPK, BOD, DCM, and BAF have no importer in any engine.
  So every option needs our own readers, and reuse favours none. Writing them as plain .NET libraries keeps them engine-independent.
- **Runtime construction of skinned meshes and animation.** Assets must load at runtime from the
  player's install, not through an editor import step.
  Godot builds `ArrayMesh`, `Skeleton3D`, and `Animation` resources from code at runtime.
  Unity can create meshes at runtime but builds Mecanim animation clips only in the editor (runtime `AnimationClip.SetCurve` targets the legacy system).
  Unreal's skeletal-mesh creation is an editor workflow; runtime generation needs plugins or custom code.
- **Dedicated server.** The authoritative simulation is plain .NET, so the server is a console app with no engine.
  It is headless by construction, small, and easy to host. Godot's `--headless` mode stays available as a fallback.
- **Licensing and distribution.** Godot is MIT and .NET is MIT. We ship our code; players supply content. No royalties or runtime fees.
- **Reversibility.** The engine sits only at the presentation edge, so it could be swapped later without touching formats, simulation, or networking.

Trade-off accepted: Unreal has better out-of-the-box rendering. A 2011 renderer's look is reproduced through
custom shaders in any engine, and Unreal's runtime-asset and server-build costs outweigh that advantage here.

**Unresolved; settle with small prototypes:**
1. ~~Whether Godot 4.7 .NET projects can target `net10.0`~~ Settled in M2: Godot 4.7.2 .NET builds the client with `net10.0`.
2. Whether shared collision (Bepu) or Godot physics plus a server-side equivalent gives acceptable prediction parity.
   *Decided in M7*: neither. Players only need static triangle meshes, a capsule and raycasts, so `SmSim.Collision` does
   this in plain deterministic C# on both server and client. Godot physics is not used for gameplay. The client and server
   load the map from their own installs; the server names the map in its Challenge and refuses a client whose map
   hash (SHA-256 of the collision and spawn files, not of computed floats) differs. Prediction stays exact against walls under 150 ms RTT and 5% loss (tests/SmNet.Tests/MapTests.cs).
   Open: bit equality between different CPUs or runtimes (all tests run on one machine).
3. ~~Decoded-content cache strategy~~ Settled in M1: the largest archive decompresses in about 240 ms, so archives are
   decompressed on demand at load time with no persistent decoded cache (see `docs/discovery.md`).

## Layers

```
original install (read-only) ──► SmFormats ──► SmContent ──► private cache (%LOCALAPPDATA%\sm-reimpl\cache)
                                 (byte-level     (engine-neutral meshes,
                                  readers)        skeletons, clips, tuning)
                                                      │
              ┌───────────────────────────────────────┴──────────────┐
              ▼                                                      ▼
      SmSim (authoritative simulation,                         Godot client
      fixed tick, no engine types)  ◄── SmNet (protocol) ──►   (render, input sampling, audio,
              ▲                                                 prediction via SmSim, interpolation)
              │
      SmServer (.NET console; also hosted in-process for local matches and tests)
```

`SmFormats`, `SmContent`, `SmSim`, `SmNet`, `SmServer` and the Godot client (`client/`) exist. The Godot client does not
use the network yet; `SmNet.GameClient` is the engine-free client it will drive.

### Content boundary
- Readers open files `FileAccess.Read` and `FileShare.ReadWrite`, and nothing ever writes under the game folder.
- Converted data goes only to the private cache outside the repo. It is never packaged.
- Tests use generated synthetic fixtures. No original bytes are committed.

## Simulation ownership and networking direction

- **Server-authoritative.** Clients send input commands (sequence-numbered, per tick) and requests such as "execute target X".
  The server owns positions, health and armour, ammunition, cooldowns, and fury, and validates every request.
- **Local play and tests** (a private match, Exterminatus with friends on one machine, automated tests) run the same `SmSim` + `SmServer`
  in-process over a loopback transport. There is no second gameplay implementation. The singleplayer campaign is out of scope.
- **Timing is separated:** fixed simulation tick, variable render rate, input sampled per tick,
  snapshots sent at their own rate. All rates are **provisional and configurable**. Starting values for
  the prototype: 30 Hz sim, 20 Hz snapshots, 100 ms interpolation delay. The original game's rates are unknown.
- **Prediction and reconciliation** for the local player's movement and suitable local actions.
  The client replays unacknowledged inputs after each authoritative snapshot.
- **Interpolation** for remote entities from buffered snapshots.
- **Lag compensation, evaluated per attack type:**

  | Attack | Starting approach | Open question |
  |---|---|---|
  | Hitscan ranged | Server rewinds hitboxes to the shooter's view time, capped | Cap size vs fairness |
  | Projectiles (e.g. rockets, plasma) | Server-simulated; client spawns a cosmetic predicted copy | Spawn-time rewind or none |
  | Melee | Server resolves with a short rewind window | Animation-driven hit windows need event data |
  | Executions | Request, server validates, both participants locked into a synced state | No rewind; latency hidden by the start-up animation |

- **Determinism** is not required. State-sync with reconciliation tolerates float differences.
- **Session compatibility.** The handshake carries the protocol version, a content fingerprint (archive table metadata: names and sizes,
  cheap to compute), and a hash of the gameplay-tuning config. A mismatch is refused, never silently joined.
- **Handshake and sessions (M4).** Hello → Challenge (stateless keyed cookie) → Hello with cookie → Welcome with a random
  session token that every input must carry. Players time out after 5 s of silence. This stops spoofed joins and blind
  input injection; it is not encryption. Real authentication is out of scope until there is a reason for it.
- **Transport.** An `ITransport` abstraction with UDP and in-process loopback implementations. Loopback has configurable
  latency, jitter, and loss for tests. **Decided in M4:** no LiteNetLib for now. Every message fits one datagram,
  inputs are repeated in each packet (last 8), snapshots are newest-wins, and the handshake is resent until answered,
  so no reliable channel is needed yet. Revisit when large or must-arrive messages appear (chat, loadouts, match state).
- **Must-arrive and large data without a reliable channel (decided for the customizer, protocol 22).** Loadouts and appearances must
  arrive and a custom emblem is 16,384 bytes, but there is still no reliable-channel library. Every message still fits one datagram
  (at most 1200 bytes): (1) **idempotent resends**: the client resends a `Setup` with the same sequence every 250 ms until the matching
  `SetupAck`; the server answers a repeated sequence with the same ack and never applies it twice; (2) **state by version, pulled by
  the receiver**: appearances never ride in snapshots; Scoreboard rows carry a u16 appearance version, and a client asks for the
  versions it lacks (`AppearanceQuery` → `Appearance`); a lost answer is asked for again on a later scoreboard; (3)
  **content-addressed blobs, pulled chunk by chunk** (`SmNet.BlobTransfer`, `BlobPull`): an emblem is named by its SHA-256 (first 16
  bytes); the receiver requests 16 chunks of 1024 bytes (4 outstanding, re-requested after 250 ms) and verifies the whole blob before use,
  so a lost, repeated or forged chunk costs nothing but a retry; the server pulls an emblem from the client that names it, verifies,
  caches and serves it to the others, with per-peer budgets. Engine-free and I/O-free: the same code runs in tests on the loopback
  network (150 ms round trip, 10 % loss). Rules and numbers: `spec/behavior.md`, "Custom classes".
- **Protocol versions of the customizer series.** 22: the Hello carries perks and both races' `AppearanceCode`s (62 bytes each); new
  `Setup` / `SetupAck`, `AppearanceQuery` / `Appearance`, `BlobRequest` / `BlobChunk`; scoreboard rows carry a u16 appearance version; shot
  records and the hit block carry the armour-or-flesh bit; snapshots are unchanged. 23 (perks, M43): the recipient's own record carries its two
  perks and a burst counter (a 2-byte word), kills carry the perk cause; others' perks are not sent. The server never sees a look as pixels: it
  validates codes against the install's catalogue and relays them.
- **Where the client turns appearances into models (T7).** `NetworkView.Appearance.cs` is the one place: per player it resolves the code
  (`AppearanceResolver`, the same call as the customizer's preview), asks `AppearanceAssets` for the model without waiting
  (`Prefetch` on workers, `TryBuild` within a per-frame budget) and swaps the character's armour in place (`CharacterViewer.SetLook`) when it is
  ready and the player is alive. The rules that need no engine (model keys, swap decisions, the build budget, the own look's promotion at the
  respawn, the hitch meter, the picker's texts) live in `client/Looks/LookLogic.cs` and are tested in `tests/SmNet.Tests`. The in-match
  loadout picker (`NetworkView.Loadout.cs`) embeds the front end's `CreatorView` and sends the same plan a Join does with `GameClient.ChangeSetup`.
