# Custom-class creator and character customizer: architecture

Design (2026-10-28, from main 30dc69e) for the owner's request. UX: `docs/customizer-design.md`. Data:
`docs/customizer-and-perks-data.md`. Our defaults for the open questions are marked **(our default)** until the owner
answers; they are recorded in `spec/behavior.md` and `docs/changes-from-original.md` when implemented.

## 1. Starting point
- `MpClasses` builds `ClassLoadout` lists in data order and keeps only simulated items (melee: knife and chainsword
  combos only — power sword, power axe, chain axe, thunder hammer, daemon maul dropped; equipment: thrown items only,
  stims skipped, melta bomb hidden). `MpClass.Perks` / `DefaultPerk` are read but unused.
- The wire carries choices as indices into the class lists (`Hello.RequestedClass/Primary/Pistol/Melee/Equipment/Side`,
  255 = default). `GameServer.OnHello` spawns once (loadout fixed for the session). `World.Spawn` / `Loadouts.Choose`
  replace invalid choices with defaults. Respawns: `World.Step` and match restart (`Teams.cs`) — the hooks for a pending
  loadout. Side balance maps a class to the other side's class of the same role; choice indices carry over by position.
- Transport: unreliable datagrams, `MaxPacket` 1200; `LoopbackNetwork` throws on oversize (a free size test). The kill
  feed's ack pattern is the model to copy. Snapshot worst case 1198/1200 (2 bytes free); the 1 Hz Scoreboard (≤ ~330
  bytes) has room.
- Rendering: `MarineAssets` bakes paint per (side, set, scheme); `NetworkView` caches assets by a model key and swaps via
  `EnsureModel` — the seam for per-player appearance. `CharacterModel.Load` takes any piece-package list. Paint shader has
  primary/secondary/tertiary, `decal_tex` on UV2, `use_decal`; `LoadDecal` handles 128x128 DXT5; `pattern_enabled` read but
  not rendered; `PaintSchemes.HasDecalUv` rejects degenerate pads.
- `LocaleTable` lives in `src/SmTool/Dump/` (client cannot reference it): move it to `src/SmFormats/Locale/`.
- Godot code has no unit tests: all logic goes into engine-free projects with synthetic fixtures.

## 2. Decisions
1. Persistence stores **names**; the wire sends **indices** (both ends derive identical lists from the same install,
   guarded by the content fingerprint and config hash).
2. A custom class is **per role and shared by both sides** (the original's role tabs); names stored race-neutral
   (`sm_`/`csm_` stripped); where the sides' lists differ the other side's item is taken by position (interpretation).
   **(our default)**
3. Appearance profiles are **per race** in a library (up to 10 per race, ours); each custom slot points to one Space
   Marine and one Chaos profile; the client sends both (the server may balance it to the other side).
4. **No reliable-channel library**: idempotent resends plus receiver-pulled, content-addressed blobs; every message fits
   one datagram (update `docs/architecture.md`).
5. **Custom emblem = 128x128 BC3/DXT5, exactly 16,384 bytes** (the original badge format; existing `LoadDecal` path;
   server validates length and hash only). Our own BC3 encoder in SmFormats (Godot's `Compress(S3tc)` emits DXT1 without
   alpha).
6. Loadout changes apply at the **next respawn**; appearance in the Hello applies at once; later appearance changes at the
   next respawn (no mid-life armour swaps). **(our default)**
7. Appearance never rides in snapshots: Scoreboard rows carry a u16 appearance version; clients query unknown versions.
8. Unlocks: one switch, `UnlockMode.All` (the only working mode); every level gate stays in the catalogue (items, perks,
   second perk slot at level 15, slots at 4/11/17/23/29/35, classes at 2 and 3).

## 3. Data model and persistence
Profile: `user://customization.json` (atomic temp+rename); emblems `user://emblems/<sha256-128 hex>.bc3`. The client
project uses a custom user dir so this lands in `%APPDATA%\sm-reimpl\` **(our default)**. Not cache, not original
content, never in the repo.
```json
{ "schema": 1,
  "selected": { "kind": "custom", "slot": 3 },
  "classes": [ { "slot": 1, "name": "CUSTOM LOADOUT 1", "role": "tactical",
      "primary": "mp_ranged_bolter", "pistol": null, "melee": null, "equipment": "mp_eq_frag_grenade",
      "perks": ["mp_perk_friendly_spawn_target", null],
      "appearance": { "space_marine": "sm-1", "chaos": "csm-1" } } ],
  "appearances": [ { "id": "sm-1", "race": "space_marine", "name": "Appearance 1",
      "predefinedSet": null,
      "pieces": { "head": "...", "torso": "...", "...": "..." },
      "paint": { "head": { "primary": "...", "pattern": "...", "detail": "...", "patternOn": false } },
      "presetApplied": "sm_ultramarines",
      "decals": { "leftshoulder": { "badge": "..." }, "rightshoulder": { "emblem": "<hex>", "fallback": "sm_blank_badge" } },
      "lens": null } ] }
```
Records (engine-free, `src/SmContent/Customization/`): `CustomizationProfile`, `CustomClass`, `AppearanceProfile`,
`SlotPaint`, `ShoulderDecal`, `Selection`. `ProfileStore.Load(path, catalog) → (profile, repairs)`: unknown/wrong-race/
not-offered names → defaults with repair notes; duplicate perk → null; > 10 trimmed; missing emblem → fallback badge;
newer schema → read-only, never overwritten; corrupt JSON renamed `customization.json.bad-<utc>`. Unsimulated items
(stims, three melee weapons) are kept in the profile, shown unavailable and sent as the default.
Defaults: empty slot = the role's class defaults; appearance = the data's `default_armor_set` (`sm_default` Mk7 Aquila /
`chaos_default`), schemes `sm_ultramarines` / `chaos_multiplayer`, lens null; the front end also lists the three
DEFAULTS loadouts.
Equipment: frag, blind (files: krak), combat stims (selectable but "not simulated yet", sent as default); melta bomb not
offered. Armour slots: the data's nine (head, torso incl. belt, leftshoulder, rightshoulder, leftarm, rightarm, leftleg,
rightleg, backpack).
Content types: `CustomizationCatalog` (per race and slot the offered pieces, sets, visible palette 60, visible decals
89/17, presets 31/16, perks as `PerkInfo(Name, UpgradeString?, Kind, Level, ChallengeText)`, unlock levels, customizer
camera framings); `ProfileWire` (`ToLoadout`, `ToCode`, `FromCode`); `AppearanceResolver.Resolve(...) →
ResolvedAppearance` (piece packages with slots, paint per slot, decals, lens, hidden slots; Raptor head/legs forced,
Devastator weapon pack, Havoc generic pack, jump-pack classes hide the backpack, pattern off = secondary equals primary,
predefined sets fill all slots with their scheme).

## 4. Protocol and server (one version bump when merged)
SmSim types: `LoadoutChoice(Class, Primary, Pistol, Melee, Equipment, Perk1, Perk2)` (indices, -1 default),
`AppearanceCode`, `EmblemHash(Lo, Hi)` (first 16 bytes of SHA-256 of the 16,384 BC3 bytes).

AppearanceCode, 62 bytes: flags 1 (bit 0 lens set, bit 1 predefined set) · predefined set 1 (255 none) · piece per slot 9
(255 = default set's piece) · palette index x3 per slot 27 · pattern bits 2 · left/right decal 2 (0..252 badge, 254
emblem, 255 none) · fallback badge 1 · lens RGB 3 · emblem hash 16 (zero none).

| Message | Dir | Layout | Bytes |
|---|---|---|---|
| Hello (changed) | C→S | today's 49 + perk1, perk2 + AppearanceCode SM + Chaos | 175 |
| Setup (9) | C→S | session u32, seq u16, 7 choice bytes, 2 x AppearanceCode | 138 |
| SetupAck (10) | S→C | seq u16, 7 resolved choice bytes, flags (1 pending, 2 emblem uploading, 4 emblem rejected, 8 defaulted), appearance version u16 | 13 |
| AppearanceQuery (11) | C→S | session u32, count ≤ 16, player ids u16 x n | 6–38 |
| Appearance (12) | S→C | player u16, version u16, race u8, AppearanceCode | 68 |
| BlobRequest (13) | both | session u32 (0 from server), hash 16, chunk u8 | 22 |
| BlobChunk (14) | both | hash 16, index, count (16), length u16, ≤ 1024 data | ≤ 1045 |
| Scoreboard row | S→C | + appearance version u16 | +2/row |

Client: the Hello carries the full setup; `ChangeSetup` resends every 250 ms until the matching `SetupAck`; on each
Scoreboard, one batched `AppearanceQuery` for unknown versions (0 = default look), each id at most every 500 ms.
Blobs (`src/SmNet/BlobTransfer.cs`): per hash a 16-bit chunk bitmap, 4 outstanding requests, 250 ms re-request, 3 tries
per chunk, hash verified before use. Upload: the server pulls from the client when a Setup names an unknown hash; the
client serves only hashes in its current profiles. Distribution: clients pull from the server, which serves only
complete, verified blobs referenced by a connected player's current profile. Server cache ≤ 32 blobs (512 KB), in-use
pinned, LRU. Client cache in memory only (others' images never written to disk). Before arrival, or with
`--no-custom-emblems` / the settings toggle, the fallback badge shows.
Abuse limits (per peer, `ServerOptions`): Setup ≤ 1/s (same seq idempotent); ≤ 1 new emblem hash / 10 s; ≤ 64 served
chunks/s (token bucket); ≤ 32 queried ids/s; authenticated session required.
Validation (`OnHello`, `OnSetup`; bad fields defaulted one by one): class exists, mapped to the player's side by role;
today's index rules; perks in the class's `PerkChoices`, no duplicates, perk 2 needs the second slot (allowed under
`UnlockMode.All`); appearance via `IAppearanceRules` (`src/SmServer/AppearanceRules.cs`): race, ranges, visibility,
predefined-set lock, pads without decal UVs. Pistol-slot bolters need the True Grit perk (data-implied; owner question).
Timing: `World.SetNextLoadout(id, LoadoutChoice)` consumed in both respawn paths; `World.LastRespawns` lets the server
promote a pending appearance; a server-wide u16 version counter. The own prediction may show the old class for a moment
at respawn (note in spec).

## 5. Rendering
Split `MarineAssets` into `RigAssets` (rig and animations once; `CharacterModel.Load(HavokCharacterFile rig, ...)`
overload), `PieceCache` (geometry per package, lazy, background file reads), `PaintedMaterialCache` (keyed by bmat, 3
colours, pattern, decal key, lens; uniform-only). Prefer `SetSurfaceOverrideMaterial` on shared meshes. Model key
`race|classKey|appearanceVersion`. Paint slot from the resolver (wargear `slot`), pattern off = secondary equals primary
(also applies presets' `pattern_enabled`). Badges via `LoadDecal`; emblems via `Image.CreateFromData(128,128,false,
Dxt5,...)` then decompress + mipmaps into the same `decal_tex`. Pads failing `HasDecalUv` greyed in the UI.
Lens colour (our addition): investigate first (helmet surfaces/materials; `_pnt`/`_dif`/`_lp` channels of the MP helmets;
eye markers such as `fx_lefteye_csm`). Prefer (A) a lens material tint, else (B) a shader `use_lens`/`lens_colour` where
the paint mask is ~0 inside per-helmet UV rectangles kept in a committed table of numbers (`LensRegions`), else (C)
tinted emissive quads at eye markers. Null lens = original look.
Class overrides from the resolver. Havoc default armour: the data says `chaos_default` (M17 used `havoc_01`; owner
answered "havoc default armor yes" on 2026-10-28: the data wins everywhere, the M17 mapping is removed).

## 6. Front end and UI (Godot C#, built in code)
`client/FrontEnd/FrontEndView.cs` (Play with address, Customizer, Quit); `client/Customizer/CreatorView.cs` (role tabs
showing both sides' names, DEFAULTS + CUSTOM 1..10, Weapons / Equipment / Perks); `AppearanceView.cs` (race toggle,
Chapters, Armor Sets, Armor 9 slots with forced slots locked, Colors with pattern and apply-to-all, Shoulder badges with
custom emblem, Lens labelled as our addition); `PreviewStage.cs` (SubViewport turntable, `CharacterViewer` on shared
`RigAssets`, weapons via `WeaponRig`, the data's camera framing per tab); `EmblemImporter.cs` (native `FileDialog`;
png/jpg/webp/bmp/tga; ≤ 8 MB, ≤ 4096² px; square crop with pan/zoom; Lanczos to 128²; BC3; hash; save); `UiKit.cs`
(shared widgets, `FocusMode.All`, `ui_*` actions).
In a match: the death screen offers "Change loadout [C]"; the picker lists DEFAULTS + custom slots for the current side,
"applies at next respawn", sends `Setup`, shows the `SetupAck` state.
Flags: `--class/--primary/--pistol/--melee/--equipment/--side` keep working (a temporary unsaved setup, skips the front
end); new `--menu`, `--customizer`, `--loadout <default:role|1..10>`, `--profile <json>` (outside the repo),
`--no-custom-emblems`. With `--bot` the profile is never read unless `--loadout`/`--profile` is given. A human
`--connect` without loadout flags uses the profile's selected slot. No-argument default view unchanged.

## 7. Interfaces for the perks milestone (define only)
`PerkSpec(Name, UpgradeString?, Kind, Level)` and `SimConfig.Perks` (hashed); `ClassLoadout.PerkChoices`, `DefaultPerk`;
`PlayerState.Perk1/Perk2`; `Loadouts.UpgradeStrings(SimConfig, PlayerState)`; later "base + upgrade" tuning variants
(`WeaponSpec With(upgrades)`, jump packs, thrown items, blueprint modifiers). Predicting perk effects needs the own perks
in the own record (2 bytes = the remaining headroom) — the perks milestone decides. No effects here.

## 8. Work breakdown
| # | Task | Files | Size | Depends on |
|---|---|---|---|---|
| T1 | Catalogue and locale | `src/SmContent/CustomizationCatalog.cs`, `PerkCatalog.cs`; move `LocaleTable` to `src/SmFormats/Locale/` (+ SmTool usings); `tests/SmFormats.Tests/CustomizationCatalogTests.cs` | M | — |
| T2 | Sim plumbing + types | `src/SmSim/Loadout.cs`, `Simulation.cs`, `Teams.cs`, new `Appearance.cs`; `src/SmContent/MpClasses.cs`, `GameplayTuning.cs`; `tests/SmNet.Tests/LoadoutChangeTests.cs` | M | T1 `PerkCatalog` signature |
| T3a | Wire codec and blobs | `src/SmNet/Protocol.cs`, new `BlobTransfer.cs`; `tests/SmNet.Tests/SetupProtocolTests.cs` | M | T2 types |
| T3b | Server and client integration | `src/SmServer/GameServer.cs`, `Program.cs`, new `AppearanceRules.cs`, `src/SmNet/GameClient.cs`; replication and emblem tests | L | T3a, T1 |
| T4 | Profile model, store, resolver, wire mapping | `src/SmContent/Customization/*.cs`; `tests/SmFormats.Tests/CustomizationProfileTests.cs` | M | T1, T2 types |
| T5 | BC3 codec | `src/SmFormats/Dds/Bc3.cs`; `tests/SmFormats.Tests/Bc3Tests.cs` | S | — |
| T6a | Lens investigation | read-only (findings only) | S | — |
| T6 | Appearance rendering | `client/MarineAssets.cs`, `CharacterViewer.cs`, `MapMaterials.Paint.cs`, `src/SmContent/CharacterModel.cs`, new `client/AppearanceAssets.cs` | L | T4, T6a |
| T7 | Networked view integration | new `client/NetworkView.Appearance.cs`, `NetworkView.Loadout.cs`; `NetworkView.cs`, `NetworkView.Weapons.cs`, `NetworkView.Death.cs`, `ModelViewer.cs` | M | T3b, T6, T8 |
| T8 | Front end and customizer UI | `client/FrontEnd/*.cs`, `client/Customizer/*.cs` | L | T4, T5, T6 (skeleton can start on T4 interfaces) |
| T9 | Docs | `spec/behavior.md`, `docs/changes-from-original.md`, `docs/architecture.md`, `CLAUDE.md` | S | all |

Order: T1, T2 types, T5, T6a in parallel → T4, T3a → T3b, T6, T8 → T7 → test and review. Hotspots: `Protocol.cs`,
`GameServer.cs`, `Simulation.cs` (serialize with any other protocol change; one bump per merged PR); `NetworkView.cs` and
`ModelViewer.cs` belong to T7 only.

Acceptance (synthetic fixtures only): T1 per-slot lists, order, visibility, levels from synthetic BAF/OBPK; T2 a change
while alive applies at the respawn tick (also restart respawn), other-side class maps by role, invalid/duplicate perks
default, hash changes with perks; T3a every message round-trips, malformed input gives null, all ≤ 1200 bytes,
Challenge ≤ Hello; T3b loopback 150 ms RTT 10 % loss: A's appearance reaches B with the same version, emblem uploads,
verifies and is served, tampered chunk rejected (fallback shown), floods stay within caps, Setup idempotent, no session
ignored, snapshots ≤ 1198; T4 round trip, repairs, future schema not overwritten, corrupt JSON renamed, resolver rules;
T5 error bound, exactly 16,384 bytes, deterministic; T6–T8 manual/screenshots (≤ 2 Godot windows, servers on
127.0.0.1): presets on both races, two clients with different `--profile` files see each other's armour, colours,
badge and emblem, C picker then respawn with the new class, keyboard-only customizer.

## 9. Risks
Index-based wire needs identical content (fingerprint covers it); positional side mapping for differing Chaos melee
weapons (test it); user images shown to others cannot be moderated by the server (hide toggles, fallback, rate limits);
join hitch loading many piece sets (background reads, warm the profile's pieces); perks' snapshot budget; unknown lens
region; DLC predefined sets; Havoc default look.

## 10. Owner questions (our defaults in brackets)
1. Perk slots: either slot any perk, or slot 1 class / slot 2 weapon? [either slot, any perk of the class]
2. Pistol-slot bolters only with True Grit? [yes, as the data implies]
3. Unsimulated stims and three melee weapons: greyed or hidden? [greyed, "not simulated yet"]
4. One custom class per role shared by both sides? [yes]
5. Appearance changes at the next respawn? [yes]
6. Other players' custom emblems shown by default? [yes, with a hide toggle]
7. Level gates for slots 7–10 when progression arrives? [open]
8. Predefined/DLC sets: lock paint, offer at all? [offer all, lock paint]
9. Lens colour: free RGB or palette; glow? [palette plus free RGB; the Chaos eye-glow effect follows the lens colour (owner, 2026-10-28)]
10. `C` as the in-match loadout key? [yes]
11. Havoc default armour per the data (`chaos_default`) instead of M17's `havoc_01`? [**yes, owner 2026-10-28**; done]
12. 10 appearance profiles per race? [yes]
13. Profile under `%APPDATA%\sm-reimpl`? [yes]
