# Discovery: installation, formats, tools

Status key: **Verified-local** (checked against the user's install) · **Documented** (two or more
independent sources agree) · **Reported** (one source or community claim) · **Hypothesis**.

## 1. Installation inventory

**Status: collected 2026-10-26 on the user's PC** (`discover.ps1` under Windows PowerShell 5.1, Steam auto-detection,
then `smtool m1-report`). Full reports stay private under `%LOCALAPPDATA%\sm-reimpl\discovery\<timestamp>\`;
only this summary is committed.

What the tool records (all read-only, relative paths, Steam account IDs omitted):

- Steam app manifests for **55150** (original 2011 game, now sold as "Anniversary Edition") and
  **3169520** (Master Crafted Edition, 2025 remaster, a separate product). The original is the target.
  If only 3169520 is installed, formats must be re-verified rather than assumed identical.
- `buildid`, depot manifest IDs, language, beta branch, executable file/product versions. Together these identify the build.
- Counts and sizes by extension and top-level folder, the 25 largest files.
- 16-byte signatures of the three smallest files per extension.
- Every OBPK archive: header fields, groups, entry counts, consistency warnings, a per-archive CSV
  listing (kept private), and signatures of the first three entries of each inner type.
- Candidate gameplay-data files by name pattern (attr, weapon, wargear, anim, event, camera, ucs…).

### Inventory summary

**Edition and build — Verified-local**

| Item | Value |
|---|---|
| Steam app | **55150**, "Warhammer 40,000: Space Marine - Anniversary Edition" (the original 2011 game). 3169520 not installed. |
| Build | buildid **7412325**, depot 55152 manifest 7732626, branch: public, language: english, last updated 2025-08-05 |
| Executable | `SpaceMarine.exe` 25,153,592 bytes, file/product version **1.0.165.0** (32-bit PE). Ships BugSplat crash reporting and `steam_api.dll`. |
| Size | 3,248 files, 11,498,132,543 bytes |

**Layout — Verified-local**

| Folder | Files | Bytes | Contents |
|---|---:|---:|---|
| `data/` | 2,796 | 5.58 GB | 2,736 OBPK `.oppc` archives (`data/chunks/` levels and sets, `data/packages/` per-object packages), config, locale, videos, a few loose map files |
| `streamingaudio/` | 230 | 3.80 GB | `.pck` audio packages; magic `AKPK` = **Wwise file package** (now Verified-local, was a Hypothesis) |
| `Extras/` | 54 | 1.98 GB | Bonus videos, wallpapers, strategy guide PDF. Not game content. |
| `redist/` | 158 | 0.11 GB | DirectX / VC++ redistributables |

Other loose files of interest (structure by magic only):

- `data/videos/*.usm`: CRI Sofdec (`CRID`), as reported.
- `data/locale/<lang>/ucs.pc`: starts with a u32 (decompressed size?) then a zlib stream (`78 DA`). So it is compressed,
  not the plain text described in XeNTaX 7333. **Hypothesis**: same "u32 size + zlib" framing as the OBPK data block.
- `data/config/*.xml` plain XML, including `aibehaviour/behaviourtrees.xml` (272 KB), a direct source for AI (later milestones).
  `graphicsdb.xml` and the `.wmem` archive entries use a text `<Object Class = ...>` syntax.
- `data/config/*.zbin` / `*.xml.bin`: compressed/binary forms of `packages.xml` and `chunkconfig`. `chunkconfig` maps chunk names.
- `data/config/*.sig` (256 bytes) and `*.chk` (32 bytes) next to two online-tuned XML files; `signatures.2048exp65537.dat` in the root.
  These look like RSA-2048 signatures and SHA-256-size hashes. **Hypothesis.** We never need to produce or bypass them: we only read content.
- `data/packages/scripts.obsp` (`OBSP` magic, 5 KB): unknown.
- Loose `.lua` files are Lua 5.1 bytecode (`1B 4C 75 61 51`), not source.

**OBPK archives — Verified-local.** 2,736 archives, 108,246 entries, 5.22 GB compressed, 9.64 GB decompressed.
`smtool m1-report` (2026-10-26): zero parse errors, entry counts identical to `discover.ps1` for every archive,
decompressed bytes = header size = data-block size = entry sum for every archive, **zero warnings** after the
name-table fix below. Largest archive (`data/chunks/ebp_sets__mp_coop_set_grid_test.oppc`, 67.5 MB on disk,
125.7 MB decompressed) fully decompresses in about 240 ms (about 520 MB/s).

**Multiplayer content map — Verified-local by archive names** (contents per archive not yet checked). The project is
multiplayer only (PvP and Exterminatus), so this decides what gets analyzed.

| Use | Archives |
|---|---|
| PvP maps | 15 maps in 26 `data/chunks/mp_*` archives: `mp_tdm_01/09/11/17/18`, `mp_dlc1_map1–2`, `mp_dlc2_map1–4`, `mp_dlc3_map1–4` |
| Exterminatus arenas | `data/chunks/cp_maps__*` (10) and `cp_map_02/03/60day__*` (10); arena names such as `cp_arena_desert_a`, `cp_arena_factory_c` |
| Blueprint sets | `ebp_sets__mp_versus_set`, `ebp_sets__mp_coop_set*` (7), `ebp_sets__mp_coop_default_player`, `player_set`, `ui_fe_player_set`; AI enemies for Exterminatus: `ork_core_units`, `chaos_core_units`, `mixed_core_units` |
| Wargear and tuning | `wargear__mp_sm`, `wargear__mp_csm` (also `wargear__sm`, `__csm`, `__fixed`); packages `*_mp_*` weapons with `_tsf/_tsh/_tst/_v` variants, `auto_aim_info`, `camera_*` |
| Player models | 2,414 `data/packages`, mostly `sm_*` (1,098) and `csm_*` (975) armour pieces; `custom_paint_colour/decal/scheme` |
| Singleplayer only (out of scope) | `part_01`–`part_05` chunks (203 archives), `ebp_sets__s0*`/`seq*`/`campaign_001`/`entourage_*`, `cinematic*` packages |

**Warnings from the first run, and their explanation.** The first real run reported 1,140 archives with
"Name table has N names; group layout implies M". All were caused by our reader following GameExtractor's model of the
name table (one string per group plus one per file). The real table is a **string pool without duplicates** (section 2).
Archives that repeat a file name (e.g. a material and a mesh with the same name, or the same name twice in a group)
have fewer strings than that model expects. The same mistake made later groups take file names as their type
(507 "inner types" instead of 21). Entry data was always read correctly because entries store their own names.
After the fix, all 2,736 archives match the pool model exactly and produce no warnings.

**Inner types (all archives) — names Verified-local, roles by magic or name only.** Every archive uses the same 21 group types.

| Type | Entries | Decompressed bytes | Archives | Leading bytes | Role (status) |
|---|---:|---:|---:|---|---|
| `dds` | 45,841 | 6.05 GB | 2,320 | `DDS ` | Textures. Standard DirectDraw Surface (Verified-local on one sample) |
| `dcm` | 15,082 | 2.88 GB | 1,112 | none; u32 header length | Mesh geometry (Hypothesis, strong; see sample below) |
| `hkxx` | 622 | 391 MB | 215 | `57 E0 E0 57 10 C0 C0 10` | Havok binary packfile. Physics/collision for level geometry (names like `havok_<mesh>__hkxx`) (Hypothesis) |
| `hkt` | 174 | 173 MB | 174 | Havok packfile | One per level/set; likely Havok animation or physics data for the chunk (Hypothesis) |
| `bmat` | 18,404 | 29 MB | 1,113 | `BOD\xFD` | Materials. BOD format (smbod README) |
| `attr_pc` | 3,086 | 22.7 MB | 33 | `BAF ` | Attribute/tuning data (Reported, magic confirmed) |
| `PSystem` | 1,323 | 19.9 MB | 29 | BOD | Particle systems (name) |
| `O3d` | 15,178 | 19.8 MB | 1,114 | BOD | Per-object definitions; one per mesh name (Reported) |
| `Layer` | 2,816 | 19.7 MB | 231 | BOD | Level layers (`a_audio`, `d_movementblockers`…) |
| `ppk` | 1,970 | 11.3 MB | 229 | u32 0x66 … | Physics shape meshes named by `CShapeMesh.MeshName` (`<name>__d` 1,202, `<name>__s` 768); single-part ones read since M39 (section "Physics shape meshes") |
| `lua` | 308 | 6.1 MB | 165 | `\x1BLuaQ` | Lua 5.1 bytecode scripts |
| `nav` | 230 | 5.6 MB | 230 | `NAVARRY` | Navigation mesh, one per chunk |
| `gfx` | 54 | 3.8 MB | 5 | `GFX` | Scaleform GFx UI movies |
| `ssdecal` | 1,331 | 1.1 MB | 143 | BOD | Screen-space decals (name) |
| `object-manifest` | 1,473 | 0.8 MB | 850 | BOD | Object package manifests |
| `region` | 246 | 0.3 MB | 24 | BOD | Level regions |
| `wmem` | 28 | 0.2 MB | 25 | `<Object Class =` text | Memory budgets (name) |
| `tfnt` | 8 | 29 KB | 1 | `\xFF\x04\x00TFNT` | Fonts |
| `tpage` | 31 | 18 KB | 28 | unknown | Texture page / atlas index? (Hypothesis) |
| `world` | 24 | 14 KB | 24 | BOD | Per-chunk world definition |
| `flowdistance` | 17 | 14 KB | 17 | `Chunks =` text | Streaming/flow distances between chunks |

Criterion 4 role mapping: meshes = `dcm`; textures = `dds`; materials = `bmat`; attributes = `attr_pc`;
levels = `world`, `region`, `Layer`, `nav`, plus `hkxx` collision. **Skeletons and animations: not identified yet.**
Candidates are the Havok `hkt` files and the `animation_*` packages. Settle this in M3.

**Samples extracted to the private cache (criterion 5).** Both come from `data/chunks/cp_maps__airlocks_shared.oppc`.

- Texture `gothic_floortiles_dif.dds` (174,904 bytes): standard DDS header (124-byte header, `DXT1`, 512×512, 10 mips,
  caps `0x401008`). 128 + full DXT1 mip chain = 174,904 exactly. **Supported by evidence**: textures are plain DDS that the
  client can upload without conversion. The OBPK 7-byte extra for this entry is `0F 0A 01 00 02 00 02`. Its second byte
  equals the mip count (10). The rest is unknown.
- Mesh `ig_ammocrate01.dcm` (63,737 bytes): one geometry block, 990 vertices, 2,112 indices, material `IG_AmmoCrate`.
  It was the starting point for the `dcm` layout, which is now specified in section 2 ("DCM static meshes").

## 2. Format findings

### OPPC / OBPK archives — **Verified-local** (all 2,736 archives, 2026-10-26)

The layout below was first documented from two independent implementations, then checked against every archive in the
install. Where the real files contradicted a source, the table says so.
aluigi's QuickBMS script `warhammer_40000_oppc.bms` v0.2 (mirrored in
[Stankye/QuickBMS](https://github.com/Stankye/QuickBMS/blob/HEAD/scripts/warhammer_40000_oppc.bms)), and
[GameExtractor `Plugin_OPPC_OBPK.java`](https://github.com/wattostudios/GameExtractor/blob/HEAD/src/org/watto/ge/plugin/archive/Plugin_OPPC_OBPK.java)
(GPL-2.0+). We read both as format evidence. No code was copied; `src/SmFormats/Obpk` is an independent implementation.

All integers are little-endian u32 unless noted.

| Offset | Field | Notes |
|---|---|---|
| 0 | `"OBPK\0"` (5 bytes) | magic |
| 5 | version | 10 in both sources |
| 9 | 0 | |
| 13 | file count | |
| 17 | unknown | 6–53 across archives; not the ID-list or group count |
| 21 | table offset | absolute |
| 25 | data offset, relative to table offset | |
| 29 | decompressed data size | |
| 33, 37 | 0, 0 | |
| 41, 45 | unknown | 10000 / 50000 in 2,710 archives; 0 / 0 in 26 |
| 49… | 288-byte blob | Santos (2011): "looks like complete gibberish… might be encrypted". Not needed for reading. Possibly an RSA-2048 signature (the install ships `signatures.2048exp65537.dat`). **Hypothesis**. |

Table (at table offset):
1. `idListCount`, then each list: `n`, `n × u32`. List lengths 1–20 observed. What the lists mean is unknown.
2. `nameCount`, then `nameCount × (len, ASCII)`: a **string pool without duplicates**, in first-use order over
   "group type name, then that group's file names", group by group. A name already in the pool is not repeated.
   **Verified-local** on all 2,736 archives (exact match, ordinal comparison). GameExtractor's model (one string per group
   and per file, no deduplication) is wrong for 1,140 of them.
3. `groupCount`, then each: 8-byte hash, `fileCount`. The hash is **constant per type name across all archives**
   (e.g. `dds` = `4FF1B0B24DA38373`, `dcm` = `6F8268FC176C9ACB`). It is presumably a hash of the type name. The function is unknown.
4. Per file, in group order: name string, folder string, `size`, `idListIndex`, `size` again, 8 zero bytes,
   `u8 extraLen`, `extraLen` bytes.
   - Folder: backslash-separated with a trailing backslash; `\` is the root (104,578 entries). Others look like
     `cp\cp_maps\airlocks_shared\`. Not `/` as the sources say.
   - `idListIndex`: an index into the ID lists of item 1, or `0xFFFFFFFF` for none. True for every one of the 108,246 entries.
   - `extra`: 4 bytes = u32 copy of `size` for every non-texture, non-mesh type (47,323 of 47,323).
     8 bytes for `dcm` = the entry's first 8 data bytes (header and body size). 7 or 9 bytes for `dds`. These look like
     texture metadata (byte 1 = mip count in the checked sample). Not needed to read the data.
   - Folder + name + type is **not unique**: 195 archives contain 2,652 repeated paths (e.g. two `hazzard.bmat` with
     different `idListIndex`). Address entries by index; `smtool extract` accepts `#<index>`.
5. At `tableOffset + dataOffset` (0x1000-aligned table end, confirmed): `u32 decompressedSize`, then one zlib
   stream running to the end of the file. Files are stored contiguously in table order.

Implications: listing needs no decompression, but reading an entry needs a decompress up to its offset.
Measured in M1: the largest archive decompresses fully in about 240 ms (about 520 MB/s, single thread).
**Decision (criterion 6):** decompress whole archives on demand at load time and keep the decoded entries in memory
for that load. Do not keep a persistent decoded cache or index restart points. Revisit only if level load profiling
shows decompression dominating.

Related: the same container appears in Darksiders (THQ, 2010). A PS3 variant is big-endian
([XeNTaX archive thread 9172](https://github.com/XeNTaXBackup/XeNTaXBackup.github.io)). Do not assume those match the PC build.

### DCM static meshes — **supported by evidence** (M2, 2026-10-26)

Reader: `src/SmFormats/Dcm/DcmMesh.cs`. All integers are little-endian; `f32` is IEEE single.

| Where | Field | Evidence |
|---|---|---|
| 0 | `u32 headerSize`, `u32 bodySize` | Sum to the entry size in 3,000 of 3,000 sampled meshes. The OBPK 8-byte extra repeats them. |
| 8 | `u32 blockCount` | Then `blockCount` geometry blocks, each 58 bytes plus its material records |
| block +0 | `u16 flags` | 0 on every static mesh |
| block +2 | `u32 vertexCount`, `u32 stride` (60), `u32 indexCount`, `u32 bodyOffset`, `u32 indexSize` (2), `u32 materialCount` | Body offsets chain block to block |
| block +26 | `6 × f32` bounding box (min xyz, max xyz) | Every position lies inside its block's box (measured on the 2,490 walked sample meshes) |
| block +50 | `u32 positionsOffset` (= bodyOffset + 2 × indexCount), `u32 attributesOffset` (= positionsOffset + 12 × vertexCount) | Holds in every walked mesh |
| block +58 | `materialCount ×` { `u8 0xFF`, `u16 len`, name, `u32 materialIndex`, `u32 0`, `u32 vertexCount`, `u32 firstVertex`, `u32 indexCount`, `u32 startIndex`, `u32 0` } | Ranges are contiguous and cover all indices in 1,328 of 1,328 multi-material blocks; indices are absolute and inside the stated vertex range in 5,550 of 5,550 ranges |
| body + bodyOffset | `indexCount × u16` triangle list, then `vertexCount × float3` positions, then `vertexCount × 48` attribute bytes | |

Attribute record (48 bytes), checked on all 4.4 million vertices of the multiplayer maps:
`float3 normal` (unit length 100%), `float3 tangent` (unit 99.99%, normal⊥tangent 99.96%), `f32` handedness (±1 in 99.99%),
`float2 uv0`, `float2 uv1`, `u32 colour` (often `FFFFFFFF` or grey; bytes B, G, R, A: see "Second layers" below). uv0 is the
diffuse UV (renders correctly); uv1's role (lightmap or blend) is not settled.

Conventions: **Z is up** (terrain and walkways are thinnest in Z; props sit on z = 0). Triangles are **clockwise seen from
the front** (99.9% of triangles oppose the stored normal under counter-clockwise math), as in Direct3D. Godot also treats
clockwise as front-facing, so the client maps (x, y, z) → (x, z, −y), a pure rotation, and keeps the index order.
Winding and normals alone fit two mirror-image readings (right-handed with clockwise fronts, or left-handed with
counter-clockwise fronts). The texture mapping settles it: the purity-seal scroll `acc_scroll_01` (`mp_tdm_01`) shows its
script left-aligned with a ragged right edge under the client's reading, so the image is not mirrored; and 71.4% of all
multiplayer-map triangles map their texture unmirrored under that reading (48.4% by UV area, where large tiled surfaces
dominate). **Supported by evidence**: right-handed, Z up, clockwise = front.

Coverage on the multiplayer maps: 1,457 static meshes read; 4 skinned meshes rejected as unsupported (stride ≠ 60);
2 fail (`banner_mech`, `banner_mech_sm`: 8 extra header bytes, possibly cloth data). Skinned meshes (character and armour
parts, about 17% of all dcm) use another header and are next (M3).

### DCM skinned meshes — **supported by evidence** (M3, 2026-10-26)

Each geometry block starts with a type byte: `00` static (the layout above, whose first two bytes are the `u16 flags`),
`01` skinned, `02` partly decoded (M24, below; 22 package meshes). Meshes can mix types (e.g. a chainsword: skinned teeth,
static blade). With type dispatch, 821 of 844 package meshes read; 22 are type 2 and 1 (`guardsman_cinematic`, singleplayer)
fails. Since M24, 827 of 844 read: the 6 heavy-weapon meshes with the type-2 variant below are added.

**Type 2 (M24, supported by evidence for one variant).** The heavy weapons' cable or ammunition belt (`heavybolter_mp_basic`
block 4 `GEO_belt`, `lascannon_mp_basic` block 3 `GEO_cable`, `plasmacannon_mp_basic` block 4 `GEO_cable`, each with a `*_sim`
sibling node in the `.O3d`) is a static block whose `u16 flags` is 2, followed after its material records by a 16-byte trailer
`u32 0`, `u32 1`, `u32 offset`, `u32 size`: a further body range that starts at the next 16-byte boundary after the attribute
stream and ends the block (body coverage then matches `bodySize` exactly). Found on all 21 such blocks in the whole install
(packages, `_v` packages, `wargear__fixed*`, the co-op sets); `DcmMesh` exposes it as `DcmBlock.Simulation`, its content not
decoded (**interpretation**: rope/cloth simulation data, from the `_sim` nodes).

**Type 2, the armour's short trailer (fix/armour-pieces, 2026-10-29; supported by evidence).** The armour's belts, tabards, cloth and fur
(`sm_armour_mp_veteran_belt`, `_relic_belt`, `_damage_belt`, `sm_armour_blacktemp_belt`, `sm_armour_mp_spacewolves_cloth`, block 2 `GEO_cloth` of the
Salamanders Veteran torso `sm_armour_mp_dlc2_set2_torso`, and the same layout in other archives) follow the same rule as the cables, read
right: the single material record's last u32 is a count n, n u32 follow it, then `u32 offset`, `u32 size`. The cables have n = 2 (entries 0, 1:
the 16-byte trailer above, whose record field M24 left unread), the armour's cloth n = 1 (entry 0: 12 bytes). The range again starts at the next
16-byte boundary after the attribute stream and ends the block, and the next block's body offset is `offset + size` (e.g. the Salamanders torso:
attributes end at 83,076, range 83,088 + 38,880, the skinned block 3 starts at 121,968). One reading covers both, so the variants cannot overlap;
`DcmSimulationData.Entries` / `TrailerSize` keep the entries and the size (37 blocks of the install have n = 1, 21 n = 2; every type-2 block that
reads has one material record). The entries' meaning and the trailing range are not decoded. In the armour these blocks' object-tree nodes (`GEO_cloth`, `GEO_front_cloth`, `GEO_back_cloth`) sit at
the tree's root with an identity transform beside a `GEO_cloth_sim` / `GEO_sim` node, so their vertices are in model space; the client draws them
rigid on `RIG_Pelvis` (`CharacterModel.ClothBone`, interpretation; spec "Free-hanging cloth"). Install-wide `dcm` coverage after the change: 15,053 of
15,082 entries read (29 fail, 13 names: the banners `banner_mech*`, `mechanicus_*` and `ork_flag_a` / `ork_ribbon_*` with yet other type-2 trailers,
and the singleplayer-style `sm_armour_basic_upres_belt` / `_shldrl` / `_shldrr`, whose static strides read as 19 to 109: another layout; none is an
armour piece the customizer offers).

Skinned block (61 bytes, then records):

| Offset | Field | Evidence |
|---|---|---|
| +0 | `u8 1` | |
| +1 | `u32 vertexCount`, `u32` (40 in every sample, meaning open), `u32 indexCount`, `u32 bodyOffset`, `u32 materialCount`, `u32 boneCount` | |
| +25 | `6 × f32` bounding box | |
| +49 | `u32` offsets of the three vertex streams: 2 × indexCount after bodyOffset, then +32 × vertexCount, then +28 × vertexCount | Chain exactly in 518/518 blocks |
| +61 | `boneCount ×` { `u8 0xFF`, `u16 len`, bone name, `16 × f32` inverse bind matrix (row-vector), `u32` per-mesh bone number } | |
| | `materialCount ×` { material record (same 28-byte draw range as static, whose last `u32` is the palette length `n`), then `n × u32` **bone palette** } | Ork boy meshes (Ork O1, below): header consumed to the last byte in 15,025 of 15,025 readable meshes |

A block with a single material has `n = boneCount` and the identity palette `0..n-1`, which is why the first reading ("`boneCount × u32`
after the materials, meaning open") held for all 291 skinned package meshes: that list *is* the one palette. A block with 2 or more
materials has one palette per material and no further list. See "Bone palettes (Ork O1)" below.

Streams per vertex: **32 bytes** `float3 position`, `4 × f32 weights`, `4 × u8 bone indices` (**slots of the vertex's draw range's bone palette**, not
indices of the block's bone list; identical for single-material blocks);
**28 bytes** `float3 normal`, `float3 tangent`, `f32 handedness`; **84 bytes** = 20-byte record (`float2 uv0`, `float2 uv1`,
`u32 colour`), a 44-byte record (`u32` single bone, then position, normal, tangent, handedness repeated; positions equal the
first stream in 100% of 454,064 vertices, likely a rigid fallback), and 20 more bytes (role open).
Checked on all 454,064 skinned vertices of the packages: weights sum to 1 (100%), every weighted bone index is inside the
block's bone list (100%).

**Bone palettes (Ork O1, 2026-10-28) — supported by evidence.** Skinned blocks with 2 or more materials (174 of the 15,082 `dcm` entries
of the install, 23 distinct names repeated across archives: the Ork `boy_melee`, `boy_ranged`, `boy_melee_pro`, `boy_ranged_pro`, `nob_melee`,
`nob_ranged` and their `_pro` variants, `guardsman_cinematic`, `ork_turret_*`, `servitors_animated`, `titan_scale`, `s16_weapon_*`, and a few set
pieces) were unreadable because the reader treated the last field of each material
record as 0. It is the length `n` of a bone palette of `n × u32` that follows the record; entries are indices into the block's bone
records (all `< boneCount`, 0 violations). Example `boy_melee` block 3 (31 bones): material `Standard_3` (213 vertices) has the palette
`0..15`; material `Ork_Melee1` (785 vertices) has 30 entries `0 13 10 2 1 5 4 16 17 3 18 …`; the header ends exactly after the last
palette, with no further bone list (block 5 ends at the header's last byte).
Vertex bone indices index **their draw range's palette** (the range = `firstVertex .. firstVertex + vertexCount` of the material record):
- In every range of all 174 meshes the highest weighted slot is `< n` (checked by the reader: any weighted slot `>= n` is an error; 0 errors).
- The head vertices (highest Z) of every head block sit on `RIG_Head` / `RIG_Face_Brows` through the palette (`boy_melee` blocks 0-3, `boy_ranged`
  blocks 1-3, `nob_ranged` blocks 1 and 5), and the torso-stump blocks of the dismemberment set (blocks 4 and 5 of `boy_melee`) on `RIG_Spine3` /
  the shoulders.
- Distance of each vertex from its dominant bone's origin in bind space (`vertex × inverse bind`; sane means well under a metre) over the
  multi-material ranges: `boy_melee` mean 0.24 m through the palette against 0.49 m as block indices (100% against 88% within 1 m);
  `boy_melee_pro` 0.24 m against 0.75 m (100% against 69%); `nob_melee_pro` 0.40 m against 1.26 m (99.5% against 30.5%). 48 to 93% of the
  dominant bones in those ranges differ between the two readings, so the mapping matters.
- All vertex weights sum to 1 (0.9998 to 1.0000), models stand on z = 0 and are 2.15 m (boy) to 3.0-3.8 m (nob) tall.
`DcmMaterialRange.BonePalette` holds the palette (null on static blocks); the reader maps every vertex's slots through its range's palette,
so `DcmSkin.BoneIndices` are block-level indices as before and downstream code (character model, skinning) is unchanged. All 14,851
meshes that loaded before load with the same vertex, index, weight, bone-index and material data (hash of all streams equal). Palettes that
differ from the identity occur only in multi-material blocks: of the install's 2,799 single-material palettes none differs from the identity
and none is shorter than the bone list, of the 993 palettes of multi-material blocks 428 are not the identity and 662 are shorter than the
block's bone list (counted per archive entry, duplicates included). Unweighted
slots past a range's palette stay as stored (weight 0; `DcmSkin.BoneIndices` are block-level indices for weighted slots only).
Coverage checks over the install (all multi-material skinned blocks, 374 per archive entry): each material's index span `StartIndex ..
+IndexCount` draws only vertices of that material's `FirstVertex .. +VertexCount` (0 violations in 2,757,096 indices), every vertex lies in
some range (0 uncovered, so 0 weighted uncovered), and the reader's check that ranges sharing a vertex have equal palettes raised 0 errors. The reader therefore rejects, on blocks with 2 or more
materials, an index outside its material's vertex range and a weighted vertex in no range (`InvalidDataException`). Single-material blocks
(2,799) satisfy both as well but are not checked, which keeps their behaviour as before. Confidence: high for the layout and the slot-to-palette mapping; the reason for
the palettes (a per-material limit on bones, as in Direct3D skinned-draw splitting) is an interpretation. The 57 `dcm` entries that still failed then were
type-2 blocks with layouts not decoded (cloth, banners); since fix/armour-pieces 29 (see "Type 2, the armour's short trailer").

Binding: bones bind **by name** (`RIG_Pelvis`…); the per-mesh number differs between meshes. For the multiplayer Space Marine,
each mesh's inverse bind matrix times the Havok skeleton's model-space reference pose is the identity for all 37 bones checked
(maximum error 0.0000), which confirms the row-vector matrix convention and that meshes and skeleton share one space.

**Character pieces.** The Space Marine armour sets are packages, one per piece: `sm_armour_basic_001_{torso, legl, legr, arml,
armr, helmet, shldrl, shldrr, backpack}`. Each holds the `dcm`, `_dif` / `_nrm` / `_spc` / `_lp` textures, a `bmat`, an `O3d`
(compressed BOD object tree) and an `object-manifest`. Six pieces are skinned (`SkinMeshVisual`); the backpack and shoulder pads
are rigid (`StaticMeshVisual`), stored in the bind pose, and their `O3d` names exactly one skeleton bone as the parent node
(`RIG_SpineD`, `RIG_L_ShoulderPad`, `RIG_R_ShoulderPad`). **Provisional**: the client reads that bone from the `O3d` string
table; written before the `O3d` value tree could be parsed (M7), and not yet switched to the tree.

### BOD compression (M3)

Flag byte 1: `u32` payload size, then one zlib stream with exactly what follows the header in the uncompressed form (string
table, then value tree). The "unknown types" reported here in M3 (`0x53`, `0x1F`) were misreadings caused by the then
undecoded `07`/`09` layout; since M7 every `O3d` and `object-manifest` parses fully (section "BOD binary object data").

### BAF attributes — **supported by evidence** (M5, 2026-10-26)

Reader: `src/SmFormats/Baf/BafDocument.cs`. Read on all 3,086 `.attr_pc` files of the install with no failure.
They live in `data/packages` (not the `wargear__*` chunks): `ebps` (824 entity blueprints), `wargear` (811), and smaller
packages (`mp_armor_set`, `multiplayer_map`, `stat`, `global`, `camera_input_tuning`, `auto_aim_info`, …).

| Offset | Field |
|---|---|
| 0 | `"BAF "`, `u32` version (100), `u32` hash, `u32` 0, `u8` 0 |
| 17 | `u32` table section offset (45), table count, entry section offset, entry count, string count, string-offset table offset, string pool offset |
| tables | `table count ×` { `i32` entry count, `i32` first entry (−1 when empty) }; table 0 is the root |
| entries | `entry count ×` { `u32` type, `u32` name string index, `u32` value }; types 0 bool, 1 f32, 2 i32, 3 string index, 4 table index |
| strings | `u32` offsets into the pool, then NUL-terminated ASCII |

Evidence: the counts place each section exactly (e.g. `sm_space_marine_player`: 1,081 tables × 8 bytes end at the entry
section, 4,920 entries × 12 bytes end at the string offsets); every child table is referenced exactly once (1,080 type-4
entries + the root); values read as sensible floats (speeds 6.5 / 6 / 4.5 / 10). `$ref` entries name the template a table
derives from (e.g. `_base_templates\speed_tuning`); values appear resolved in the files, so templates need not be applied.
Multiplayer movement and health values are listed in `spec/behavior.md`.

### Havok packfiles — **supported by evidence** (M3)

Readers: `src/SmFormats/Havok`. No Havok SDK code. Provenance: the container layout and the class field offsets were
established on the game's own files (every value checked above). The two rotation encodings (POLAR32, THREECOMP40) follow
the community decoder in [PredatorCZ/HavokLib](https://github.com/PredatorCZ/HavokLib) (GPL-3.0), reimplemented from memory
of it rather than copied; the THREECOMP40 sign bit was then chosen on the game's data. The repository is GPL-3.0
(decided by the user, 2026-10-26), which is compatible with that source.
`hk_2010.2.0-r1`, 32-bit pointers, little-endian. Header 0x40 bytes (magic `57E0E057 10C0C010`, layout bytes at 0x10,
section count at 0x14, version string at 0x28), then 0x30-byte section headers: tag (19 bytes + 0xFF), absolute offset,
then local fixups, global fixups, virtual fixups, exports, imports, end (relative). Class names: `u32` signature, `0x09`,
NUL-terminated name, padded with 0xFF (a signature may itself start with 0xFF). Local fixups (`src`, `dst`) are pointers;
virtual fixups (`src`, section, name offset) give each object's class. On `havok_sm_base_mp__hkxx` all 15,116 objects and every
pointer resolve.

Each character has one packfile with everything: skeleton, ragdoll skeleton, all animations, the behavior graphs and character
data. Multiplayer-relevant files: `havok_sm_base_mp__hkxx` (Space Marine; in `ebp_sets__mp_versus_set` and the co-op sets),
`havok_chaosspacemarine__hkxx`, and the Exterminatus enemies (`havok_boy_coop`, `nob_coop`, `ard_boy_coop`, `gretchin_coop`,
`chaosspacemarine_coop`, `bloodletter_coop`, …).

- **Skeleton** (`hkaSkeleton`: name @8, parent indices @12, bones @24, reference pose @36 as `hkQsTransform` = translation,
  rotation xyzw, scale; 48 bytes). Space Marine: `RIG_Root` with 50 bones, parents always precede children, head at z ≈ 2.15.
  A 17-bone `Ragdoll_RIG_*` skeleton follows.
- **Animation names**: the `hkCombinerTOC` lists every source file (40-byte entries: name, type, hash, count, 0). Walking it
  against the root container's variants (after the TOC itself) assigns each `.hkxa` its `hkaAnimationContainer`; on the
  Space Marine 665 entries cover all 668 variants with no type mismatch: 613 animations (`.hkxa`), 46 behavior graphs (`.hkxg`,
  e.g. `MP_SM_LasCannon_Fire`, `MP_SM_MEL_ChainSword`), 3 character data (`.hkxc`), 2 rigs (`.hkxr`), 1 project (`.hkxp`).
- **Binding** (`hkaAnimationBinding`): skeleton name @8, animation @12, track → bone `hkInt16[]` @16.
- **Animation** (`hkaSplineCompressedAnimation`): duration @12, transform tracks @16, frames @40, blocks @44, max frames per
  block @48, mask size @52, block and frame durations @56/@64, block offsets @68, data @116. All 613 Space Marine animations:
  30 fps, 50 tracks, no float tracks, no scale data. Per block: a 4-byte mask per track (quantization; position, rotation and
  scale flags: low bits static, high bits spline), then per track position, rotation, scale: absent, static, or a B-spline
  (`u16` items, `u8` degree, `items + degree + 2` byte knots, then control points), each part padded to 4 bytes.
  Positions: per-axis min/max and 8- or 16-bit control values. Rotations: **POLAR32** (4 bytes) or **THREECOMP40** (5 bytes:
  three 12-bit components, 2-bit index of the dropped largest one, its sign in bit 38). Evidence: all 613 decode and end
  within each block's 16-byte padding; with the sign in bit 38 only 0.08% of consecutive control points jump more than 120°
  (1.96% for the alternatives); poses render as a correct run cycle; loops close.
  Prop bones (`RIG_Prop_M_Lhand`…) switch between fixed orientations frame to frame; that is data, not a decoding error.
- **Events**: no animation has annotation tracks. Timing data lives in the behavior graphs instead: `hkbEventSequenceTag` (324),
  `hkbAttackAlignmentSequenceTag` (190), `hkbAttackDistanceSequenceTag` (70), `hkbActionSequenceTag` (776) and events such as
  `ACTN_Hit_Primary_Target`, `Input_Window_Open`, `SYNC_Left_Foot`, `Start_Action_FootStep_L`, `BRANCH_Melee_Combo`.
- **Behavior graphs (M9) — supported by evidence for sequencer timing.** Reader: `src/SmFormats/Havok/HavokBehavior.cs`.
  The packfile has no embedded class reflection, so layouts are inferred from the data. Each graph's objects are
  contiguous, from its `hkbBehaviorGraph` (name @32, root generator @76, data @80) through `hkbBehaviorGraphData` and
  `hkbBehaviorGraphStringData` (event names `hkArray<char*>` @8, variable names @32). Relic's `hkbSequencerGenerator`
  (433 on the marine): name @32, tags (pointer array) @40, generator @52: an `hkbClipGenerator` (name @32, animation
  file @40, playback speed @60) or an `hkbBlenderGenerator` (children @64; `hkbBlenderGeneratorChild` → generator @32).
  Tags (`hkb…SequenceTag`): window start @32 and end @36 as fractions of the clip; `Event`/`Branch` an event index @52,
  `Action` a name string @52, `AttackDistance` a distance @56. Evidence: all 46 graphs, 433 sequencers and 3,431 tags
  read; every tag window lies in 0–1 with start ≤ end; every animation a sequencer names exists in the packfile; 10
  event indices fall outside their graph's names. Many fractions fall on whole frames of the clip (e.g.
  `SM_MEL_CS_XATK_Combo1`, 46 frames: hit 0.2 = frame 9 of 45, combo window 0.2889–0.6667 = frames 13–30), but not all:
  on the knife's `CK 1` (44 frames) most tags fit its 43 intervals, while its multiplayer hit (0.1957) fits 46 and its
  combo close (0.5366) 41, so some tags were evidently authored on other clips and copied. Since tags are fractions of
  whatever clip plays, seconds = fraction × the playing clip's length ÷ its playback speed (1 for the knife's clips;
  950 of the marine's 985 clip generators play at 1).
- **Melee timing** (`src/SmContent/MeleeTuning.cs`): the combat knife's graph `MP_SM_MEL_CombatKnife` has sequencers
  `CK 1/2/3 Sequence`, each a blend of distance variants (`SM_MEL_CK_XATK_1_1m/3.5m/7m`, equal lengths), matching the
  wargear abilities `ability_melee_ck1..3` (40 damage, 20 to secondary targets, `max_range` 4). Multiplayer uses its own
  hit event `ACTN_Hit_Targets_Multiplayer`. Combo windows are `BRANCH_Melee_Combo`, cancelling into shooting
  `BRANCH_ToRanged`, moving again `BRANCH_Melee_Draw_Run`. The combo order is taken from the names (state machine
  transitions are readable since M27, below, but not used for the knife).

### Behaviour graph structure and hip fire — **supported by evidence for the layouts; marked items are interpretations** (M27, 2026-10-28)

Reader: `src/SmFormats/Havok/HavokBehaviorDetail.cs` (`HavokBehaviorDetailReader`, read-only, on top of `HavokBehavior`); blend
hint: `HavokCharacterFile.LoadBlendHint`; content: `src/SmContent/HipFireAnimations.cs`; tool: `smtool graph-dump` (`--list`,
`<graph|pattern*> [--clips <pattern*>] [--no-tree] [--raw <class> [--zeros]]`, `--animations`, `--hipfire`, `--raw-any`). Values:
spec/behavior.md "Hip fire (M27)". Layouts were established on `havok_sm_base_mp__hkxx` with `--raw` (annotated words of every
object of a class) and checked against names, counts and cross-references in the same files.

- **Classes in the marine rig** (`graph-dump --list`): 214 `hkbStateMachine`, 981 `hkbStateMachineStateInfo`, 523
  `hkbStateMachineTransitionInfoArray`, 400 `hkbBlenderGenerator`, 1,109 `hkbBlenderGeneratorChild`, 125 `hkbBoneWeightArray`,
  251 `hkbBlendingTransitionEffect`, 985 `hkbClipGenerator`, 94 `hkbBehaviorReferenceGenerator`, 317 `hkbModifierGenerator`,
  1,090 `hkbVariableBindingSet`, conditions (`hkbBoolCondition` 237, `hkbThresholdCondition` 149, `hkbMultiCondition` 113,
  `hkbNegateCondition` 45, `hkbPieWedgeCondition` 37, `hkbExpressionCondition` 5), `hkbConditionChooser` 63. There is **no**
  manual selector, layer generator or partial-blend generator: partial (upper/lower body) blends are two-child
  `hkbBlenderGenerator`s whose children carry bone masks.
- **Common base**: `hkbBindable`: variable binding set @8 (cached bindables array @12). `hkbNode`: name @32.
- **`hkbVariableBindingSet`**: bindings @8, 32 bytes each: member path @0 (e.g. `blendParameter`, `playbackSpeed`,
  `alignVector`, `twistAngle`), variable index @20 (into the graph's variable names, `hkbBehaviorGraphStringData` @32).
  Evidence: every blend's bound variable is a plausible one (`FLT_Aim_Vertical_Deg_DAMPED` on the Down/Forward/Up blends,
  `FLT_Nav_Motion_Angle_DAMPED` on the eight-direction blends, `FLT_Input_Nav_Speed_DAMPED` on idle/walk/run blends).
- **`hkbBlenderGenerator`**: reference-pose threshold @40, blend parameter @44, cyclic min/max @48/@52, sync master child
  (int16) @56, flags (int16) @58 (0x11 on parametric blends, 0x31 on the cyclic direction blends, 0 on fixed layer blends),
  subtract-last-child byte @60, children (pointer array) @64. **`hkbBlenderGeneratorChild`**: generator @32, bone weights @36,
  weight @40, world-from-model weight @44. On parametric blends the child weights are the parameter values of each child
  (directions −180…180°, speeds 0/2/4.5/6.5, pitches −90/−80…80/90°).
- **`hkbBoneWeightArray`**: weights (float array) @28, by skeleton bone index. Masks come in pairs: the lower layer lists
  48–51 entries with 1 on `RIG_Root`, `RIG_Interaction_Root`, `RIG_Interaction`, `RIG_Pelvis` and the leg bones (some also
  `RIG_SpineA`) and 0 elsewhere; the upper layer lists only the first 13–15 bones, all 0. **Interpretation**: bones past the
  end weigh 1 (read as 0, the upper body would have no weight in either layer). Masks appear on turns, uneven-terrain
  layers, reloads and weapon switches, **not** on shots.
- **`hkbStateMachine`**: start state chooser @52, start state id @56, states (pointer array) @92, wildcard transitions @104.
  **`hkbStateMachineStateInfo`**: transitions @48, generator @52, name @56, id @60, probability @64, enable byte @68.
  **`hkbStateMachineTransitionInfoArray`**: entries @8, **44 bytes** each (object sizes for 1–4 entries: 80/128/176/208 bytes):
  transition effect @0, condition @4, event id @8, to-state id @12, priority (int16) @24, **branch event** (int16) @26.
  Evidence for the branch event: event-less transitions name exactly the branch their source sequencer opens
  (`Idle Relaxed To Run Relaxed` → `BRANCH_Idle_To_Run_End`, turns → `BRANCH_Turn_End`, the recoil's raise state →
  `BRANCH_to_Fire`); a transition with both an event and a branch fires on the event inside the branch window (sprint →
  fire on `BTN_Ranged` in `BRANCH_Sprint_Evade`).
- **`hkbBlendingTransitionEffect`**: name @32, duration @64 (seconds: 0.1, 0.133, 0.167, 0.267, 0.333, 0.5 = 3, 4, 5, 8, 10, 15
  frames at 30 fps, matching names like "Default 3 Frame Transition", "5 Frame Blend", "15 Frame Blend"), flags (int16) @72.
  A transition without an effect switches at once (Havok behaviour; e.g. the bolt pistol's navigation ↔ fire).
- **Conditions**: `hkbExpressionCondition` expression string @8 (e.g. "BOOL_IsAiming == 1"); `hkbThresholdCondition` min @8,
  max @12, variable @20 (e.g. `FLT_Fall_Speed` in [−99999, −20]); `hkbBoolCondition` (Relic) two int arrays of variable
  indices @8 and @20, read as "all true" and "all false" (**interpretation**: the stand-to-fall condition lists
  `BOOL_IsSupported` in the second); `hkbNegateCondition` condition @8; `hkbMultiCondition` kind @8, conditions @12: **0 = all,
  1 = any** (evidence: idle → run is "speed ≥ 0.5 and not (wall and close)", its reverse "speed < 0.5 or (wall and close)");
  `hkbPieWedgeCondition` min/max angle @16/@20 (not used here). **`hkbConditionChooser`**: entries @8, each
  `hkbConditionChooserConditionEntry` with state id @8 and condition @12 (start state by condition, e.g. the recoil starts in
  `Recoil` if `BOOL_Can_Fire`, else in `Set Weapon Ready`).
- **Other nodes**: `hkbClipGenerator` animation @40, playback speed @60, binding index (int16) @72, mode byte @74 (1 on the
  looping movement clips, 0 on recoil and transition clips: Havok's single-play/looping). `hkbModifierGenerator` modifier
  @40, generator @44. `hkbModifierList` modifiers @44. `hkbBehaviorReferenceGenerator` behaviour file @40 (e.g.
  `Behaviors\SM_Bolter_NAV.hkxg`): the base graph `SM_Base_MP` composes the weapon graphs by reference.
  `hkbSetBoolModifier` variable @44, value byte @48 (e.g. "Set Can Fire Bool" sets `BOOL_Can_Fire` to 1, "Set Can't Fire
  Bool" to 0).
- **Binding blend hint** (`hkaAnimationBinding` byte @40, after the float-track array @28): 28 of the 613 marine clips are
  additive (1), the rest normal (0): every ranged recoil (`SM_RNG_FireIdle_S_01`, `_Combi_`, `_LC_`, `_MG_`, `_TB_`,
  `SM_1HR_RNG_FireIdle_S_01`, `SM_1HR_RNG_MP_FireIdle_01`), the charge clips, `SM_RNG_TB_Brace_Fire`,
  `SM_RNG_OverHeat_Vent_02` and the 14 flinches (`SM_REC_Flinch_*`). The grenade launcher's `SM_RNG_GL_FireIdle_01` is normal.
- **Hip fire in the original** (`graph-dump --hipfire`): `SM_Base_MP`'s weapon state machines (`BolterBehavior`,
  `BoltPistol Behavior`, `Behavior_RangedHeavyBolter`, …) switch from navigation to a fire state on `BTN_Ranged`; the fire
  state is a blend with weight 1 on the navigation graph and weight 1 on the recoil graph, **no bone mask** (e.g.
  `Bolter_FireBlend` = `SM_Bolter_NAV` + `SM_UB_RangedRecoil`). The navigation graph keeps running in whatever posture it is
  in; firing puts it in the ready posture (`Stand Ready` / `Run Ready`, condition `BOOL_IsReady`, which no graph modifier writes: set by code; the recoil
  graph's `Set Weapon Ready` state sets `BOOL_Set_Ready`). `Run Ready` is a cyclic eight-direction blend
  (`FLT_Nav_Motion_Angle_DAMPED`: B −180, BL −135, L −90, FL −45, F 0, FR 45, R 90, BR 135, B again at 180) of speed blends
  (`FLT_Input_Nav_Speed_DAMPED`: 0 walk-on-spot, 2 walk, 4.5 run on B/BL/L/R/BR, 6.5 on F/FL/FR, matching the 6.5 run speed) of
  aim-pitch blends (`FLT_Aim_Vertical_Deg(_DAMPED)`: −80 / 0 / (+40) / +80, walk-on-spot ±90). The heavy bolter's `Heavy Run`
  has four directions (B, L, F, R); its run bands sit at 4.5 (B), 4 (L, R) and 5.5 (F). The recoil is an additive single-play clip; the shot state hands
  back to navigation at its `BRANCH_Shot_Finished` (`BRANCH_Fire_End` on the heavy bolter).
- **`SM_NAV_BO_*`**: the rifle's ready walk/run clips for the B, L and R directions (FL, F, FR, BL, BR use the plain
  `SM_NAV_Walk/Run_<dir>_Aim*` clips); relaxed movement uses `SM_NAV_Run_<dir>_01`. They carry no tags of their own (the
  `Run Ready` sequencer has only footstep actions at 0.375 and 0.875). **Interpretation**: "BO" names the bolter family, as
  "CS" the chainsword (`SM_TRN_CS_180Turn_to_Sprint` beside `SM_TRN_BO_180Turn_to_Sprint`); the `SM_TRN_BO_*` transition clips
  carry `BRANCH_Idle_To_Run_End` at 1.0 (idle-aim to run, played to the end, 0.7 s) and `BRANCH_Sprint_Evade` from 0.4286 (180°
  turn into sprint). Nothing in them implies a hold time.
- **Sprint and shooting**: every sprint state has a transition to its fire state on `BTN_Ranged` (rifles and pistols within
  `BRANCH_Sprint_Evade`, which the sprint loop keeps open over its whole clip; the heavy bolter without a window); there is no
  sprint-fire state, a shot ends the sprint.
- **Wargear** (`common_data/fire_mode_primary/entry/animation_set`): `bolter` (bolter, gold bolter), `plasma`, `melta`,
  `grenade_launcher`, `storm_bolter`, `stalker_pattern` (sniper rifle), same for the Chaos versions; pistols and heavy weapons
  have none. `basic_weapon_tuning/entry/wait_for_weapon_ready` is true on the primary fire. `kick_back_and_scatter_values`
  has separate `standing` / `crouching` / `aiming` × `idle` / `walking` / `running` scatter sets (`climb_sway_scatter\…`);
  applied since weapons/spread (section "Weapon scatter, climb and sway" below; spec/behavior.md "Weapon spread"). No camera set is specific to firing: `camera_data` names exploration, aim, crouch, jump and
  melee cameras only; `hit_sim/actions/camera_effect` (shake 0.1 at 12 Hz for 0.2 s, attenuated 8–20 m) is an on-hit effect.
- **Not established**: Havok's priority order among simultaneous transitions (the pistol recoil graph's entry state offers
  `Shoot` at priority 2 and `Bolter Shoot` at 1; `Shoot`, with the multiplayer clip `SM_1HR_RNG_MP_FireIdle_01` and the
  ability modifier, is taken: **interpretation**); how long code keeps `BOOL_IsReady` after a shot; the meaning of
  `hkbPieWedgeCondition` fields beyond the angles.

### Camera rig and camera attributes — **supported by evidence for the rig's pitch mapping; bone roles interpreted** (M20, 2026-10-27)

Reader: `src/SmContent/CameraRig.cs` (on the existing `HavokCharacterFile`); attributes: `src/SmContent/CameraTuning.cs`.
Values: spec/behavior.md, "Camera, aim mode, shot convergence and hit confirmation".

- **Packfile**: `spacemarine_camera.oppc` holds one entry, `spacemarine_camera__hkxx` (72,400 bytes, hk_2010.2.0-r1, same
  container as the character files): a root container with a combiner TOC, 31 `hkaAnimationContainer` /
  `hkaSplineCompressedAnimation` / `hkaAnimationBinding` triples, one behavior graph (`SpaceMarine_Camera_BehaviorGraph`: a
  state machine of 28 clip generators, no events), `hkbCharacterData` and one 3-bone `hkaSkeleton`: `RIG_Root`,
  `RIG_AimInner_Camera`, `RIG_AimOuter_Camera` (both children of the root). `HavokCharacterFile` reads it unchanged.
- **Animations** (names = the `animation_curve` values of `camera_curve_settings`, case aside): `navigationSet_standing`,
  `_standing_far`, `_crouch`, `_evade`, `_evade_JP`, `_evade_TB`, `_jumppack`, `_lean`, `_bullrush`, `aimSet_standing`, `_heavy`,
  `_zoom`, `_crouch`, `_Turret`, `_ValkyrieTurret`, `pistol_navigationSet_standing`, `pistol_aimSet_standing`,
  `Heavy_navigationSet_standing`, `meleeSet_default`, `meleeSet_CombatKnife`, `SyncKillSet_Default`, `_Large`, and singleplayer
  vehicle/dreadnought/ork sets. All three tracks are animated; the root track stays at the origin with identity rotation.
- **Pitch mapping (evidence)**: 29 clips last 6 s with **181 frames at 30 fps: one frame per degree**. On the standing sets the
  camera bones' rotation is a pure rotation about X whose angle is frame − 90° (−90° at frame 0, 0 at frame 90, +90° at frame
  180; within 0.3° when sampled every 5–45°), and at frame 0 the outer bone is above the character looking down, at frame 180
  below it looking up. So a clip is sampled at pitch / 180 of its length, with pitch in degrees from straight down (0) through
  level (90) to straight up (180); the camera sets' `pitch_minimum` / `pitch_maximum` (e.g. 20–160 exploration, 5–175 rifle aim)
  are in these degrees. Crouch, jump-pack and melee sets bend the look angle away from the input (authored, not an error);
  `meleeSet_CombatKnife` and `SyncKillSet_Large` are 3 s / 91 frames and their mapping is not established.
- **Frame**: the animations are in the character's frame (X right, Y forward, Z up, feet at the origin), consistent with
  models facing +Y: the camera sits to the right (+0.64 m) and behind (−Y), i.e. over the right shoulder.
- **Bone roles (interpretation)**: the outer bone is the camera (its rotation is the view; on the standing sets it lies behind
  the inner bone along the view, at level 1.26 m (rifle aim) to 4.06 m (pistol navigation), shrinking as the view tilts up); the
  inner bone (at level 0.5–0.7 m behind the feet, 2.16–2.23 m up) is the pivot the camera is pulled toward when geometry is in
  the way. At level: navigation camera 3.38 m back, 0.64 m right, 2.38 m up;
  rifle aim camera 1.78 / 0.645 / 2.16; pistol aim 1.65 / 0.46 / 2.23; heavy aim 3.00 / 0.645 / 2.14; sniper zoom 0.17 m in
  front of the feet, 0.35 right, 2.07 up (scope view).
- **Camera attributes**: `camera_curve_settings.oppc` (one BAF per camera state: `field_of_view`, `animation_curve`,
  `pitch_minimum` / `_maximum`, `blend_in_time`, `relax_delay`, `relax_speed`, `aim_blend_speed_scaler`, `blend_in_distance`,
  `target_lock_on_seconds`, `lens_properties` depth of field, melee pan and pitch fields); `camera_input_tuning.oppc`
  (`speed_mouse`, `speed_horizontal`, `speed_vertical`, stick `input_mapping` / `input_mapping_ps3` curves, `request_damping`);
  `auto_aim_info.oppc` (adhesion and magnetism per weapon, `auto_aim_angles`, `camera_input_tuning_set`). Each wargear file has
  `camera_data/entry` (by required upgrade) / `camera_data` naming `auto_aim`, `auto_aim_zoom`, `aim_camera`,
  `exploration_camera`, `melee_camera`, `exploration_crouch_camera`, `aim_crouch_camera`, `jump_camera`, `mounted_camera`,
  `aim_jump_camera` as `camera_curve_settings\<name>` / `auto_aim_info\multiplayer\<name>` references; the aim-assist set names
  the input tuning. Evidence: all 55 multiplayer wargear files with camera sets (weapons, melee weapons, equipment) resolve them; 6 ability files
  (jump pack, sprint, evades) have empty camera data.
- **Aim key**: the data has a held aim mode (locale "Hold %AIM_MODE% to enter aim mode with ranged weapons") but no PC key;
  Left Ctrl is from the user's knowledge of the original.

### Ragdoll and hit shapes — **supported by evidence for the shapes; their use as hit volumes is an interpretation** (M20, 2026-10-27)

Reader: `src/SmFormats/Havok/HavokRagdoll.cs`; hit volumes: `src/SmContent/HitVolumeTuning.cs` → `SimConfig.HitVolumes`
(`src/SmSim/HitVolumes.cs`). Values: spec/behavior.md ("Hit volumes (M20)").

- **Objects**: the marine rig `havok_sm_base_mp__hkxx` (shared by Chaos marines) holds one `hkaRagdollInstance` (rigid bodies
  pointer array @8, constraints @20, bone-to-body map @32, ragdoll skeleton @44: 17 bones `Ragdoll_RIG_Pelvis`, `_Chest`,
  `_L/R_Thigh`, `_L/R_Calf`, `_L/R_Foot`, `_L/R_ShoulderPad`, `_Head`, `_L/R_UpperArm`, `_L/R_Forearm`, `_L/R_Hand`) and 17
  `hkpRigidBody`. Shapes: 3 `hkpSphereShape`, 8 `hkpCapsuleShape`, 2 `hkpBoxShape`, 9 `hkpConvexVerticesShape`,
  `hkpConvexTranslateShape` wrappers, one `hkpMoppBvTreeShape` over an `hkpListShape` (the chest: torso, backpack, four
  exhaust stacks).
- **Rigid body** (32-bit): shape @16, name @120 (`Ragdoll_RIG_*`), rest transform as an `hkTransform` in model space: three
  rotation columns @240/@256/@272 (their w lanes hold other data) and translation @288. Evidence: every body's translation is
  its skeleton bone's model-space position (e.g. head 2.025 m = `RIG_Head`), the columns are orthonormal with determinant 1, and
  the centre of mass @304 agrees with the shapes; @336 holds the same rotation as a quaternion.
- **Shapes**: convex radius @16 for all; sphere; capsule vertices @32 and @48; box half extents @32; convex translate child @24,
  translation @32; convex vertices: stored bounding box half extents @32 and centre @48, vertices @64 as blocks of four
  transposed points (48 bytes: x×4, y×4, z×4), vertex count @76, planes @88 (`hkVector4`: unit normal, offset). The planes are
  outward with the convex radius included: for every vertex, max over planes of n·v + w equals −radius exactly (9 of 9 hulls);
  list children @24 (16-byte entries, shape pointer first); MOPP child @52.
- **The marine at rest** (model space = character frame: X right, Y forward, Z up, feet at z ≈ 0): head sphere radius 0.178
  centred (0, −0.038, 2.133) — top 2.311 m; torso hull 1.475–2.096 m; backpack hull 1.298–2.593 m behind (y −0.62…−0.26);
  four exhaust hulls up to 2.456 m behind it; shoulder-pad hulls 1.59–2.14 m out to x ±0.67; pelvis hull 1.047–1.518 m;
  thigh capsules r 0.183, calf r 0.230, upper arm r 0.127, forearm r 0.117; hand spheres r 0.119; foot boxes. Level rays from
  the front at x = 0 hit the head from 2.05 to 2.30 m, the torso below, the backpack from 2.35 to 2.60 m, nothing above.
- **Interpretations**: that these ragdoll bodies are the hit volumes (the original's shot test is in code; the behavior graphs
  have `hkbEntityPropertyTargetableModeData`), that the rest pose stands for every pose (the arms hang at the sides; no
  animation), and that only the head body takes headshots. The previous placeholders (head sphere 1.95 m r 0.25, body capsule
  to 1.7 m) ended below the drawn helmet top, missed the outer shoulder pads and counted the inner shoulders as head.

**Physics ragdoll (M23, 2026-10-27) — supported by evidence.** Reader `HavokRagdoll.ReadPhysics`
(`src/SmFormats/Havok/HavokRagdollPhysics.cs`), content `src/SmContent/RagdollModel.cs`; values in spec/behavior.md ("Death and
ragdoll (M23)"). Established on the install's `havok_sm_base_mp__hkxx` (buildid 7412325); field names follow the Havok 2010
class layouts they fit, no SDK code.

- **Instance**: constraints @20 (16 `hkpConstraintInstance` pointers), bone-to-body map @32 (17 ints, identity), skeleton @44
  (the 17-bone `Ragdoll_RIG_*` `hkaSkeleton`; its reference pose in model space equals the bodies' rest transforms to 2e-6).
  Ragdoll bones' parents: pelvis root; chest, thighs → pelvis; head, shoulder pads → chest; calves → thighs; feet → calves;
  upper arms → shoulder pads; forearms → upper arms; hands → forearms.
- **Rigid body physics** (576-byte objects): collision filter info @44 (group filter: layer bits 0–4 = 0, subsystem id bits
  5–9 one per body, "don't collide with" bits 10–14 = the parent's id, system group bits 16–31 = 1); material @140 (response
  type byte 1), friction @144 and restitution @148 (both 0.8, so their order rests on the declaration order); damage
  multiplier @156 = 1. Motion @224: type byte @232 (3 box inertia, 2 sphere inertia), the motion state's transform @240 (the rest
  transform above), swept transform @304 (centre of mass in model space @304/@320, rotation quaternion @336/@352, centre of
  mass in the body frame @368: rotated and offset it equals @304), object radius @400, linear and angular damping as half
  floats @404/@406 (0 and 0x3D4C = 0.0498), max velocities and deactivation class @410–@412, inverse inertia xyz and inverse
  mass w @416 (e.g. pelvis 0.0303/0.0395/0.0384, 0.00833 → 120 kg), velocities @432/@448 (0), gravity factor half @510 (1.0).
- **Constraint instance** (96 bytes): data @12, entity A @20 = the child body, entity B @24 = the parent, priority @28 (1),
  name @40 (= the child body's name). Evidence: transforming each side's pivot by its body's rest transform gives the same
  point to 4 µm for all 16.
- **`hkpRagdollConstraintData`** (352 bytes, 12 joints): atoms from @16, each starting with a u16 type: set-local-transforms (2)
  @16 with frame A (rotation columns @32/@48/@64, pivot @80; A's pivot is always the child's origin) and frame B (@96/@112/@128,
  pivot @144); setup-stabilization (23) @160; ragdoll motor (19) @176 (disabled; 3 motor pointers @240–@248 to one
  `hkpPositionConstraintMotor`: min/max force −1e6/100 @12/@16, tau 0.8, damping 1, recovery velocities 5/0.2); angular
  friction (17) @256 (enabled @258, 3 axes @260, max torque @264); twist limit (15) @268 (enabled @270, twist axis @271 = 0,
  reference axis @272 = 1, min/max @276/@280, tau @284); cone limit (16) @288 (axes @291/@292 = 0/0, measurement mode @293 = 0
  "zero when aligned", min @296 = −100 i.e. none, max @300 = the cone angle, tau @304); plane limit — a second cone atom (16)
  @308 (axes 0/1, mode 1 "zero when perpendicular", min/max @316/@320, tau @324); ball-and-socket (5) @328.
- **`hkpLimitedHingeConstraintData`** (256 bytes, 4 joints: feet, hands): transforms (2) @16 as above; stabilization (23) @160;
  angular motor (18) @176 (disabled); angular friction (17) @196 (1 axis, max torque @204); angular limit (14) @208 (axis @211 =
  0, min/max @212/@216, tau @220); 2D angular (12) @224 (free axis @226 = 0: the hinge axis is each frame's column 0);
  ball-and-socket (5) @228.
- **`hkaSkeletonMapper`** ×2 (data from @16): skeleton A @16, B @20, simple mappings @24 (64 bytes: bone A i16, bone B i16,
  aFromB `hkQsTransform` @16), chain mappings @36 (112 bytes: start/end A, start/end B i16, two transforms @16/@64), unmapped
  B bones @48 (i16), extracted-motion transform @64, keep-unmapped-local @112 (1), mapping type @116 (0). One maps the animation
  skeleton (A, 50 bones) onto the ragdoll (B; 18 simple, 2 chains); the other the ragdoll (A) onto the animation skeleton (B;
  19 simple, 2 chains, 30 unmapped). Evidence for the composition: animation bone model = aFromB × ragdoll bone model (row
  vectors) reproduces the animation skeleton's reference pose for all 19 to 3e-6 (the other order misses by up to 3.9).
- **Use in the Godot client** (provisional approximation; spec/behavior.md "Death and ragdoll (M23)"): each joint becomes a
  `Generic6DofJoint3D` whose zero is the pose at death. The start offset is the child constraint frame relative to the parent's,
  `(parentFrame⁻¹ · childFrame)` as Godot Euler angles in order XYZ (Godot's `EulerOrder.Xyz`, right-handed, positive = counter-
  clockwise looking down the axis); each Havok range [min, max] on axis k becomes [min(min − startₖ, 0), max(max − startₖ, 0)],
  i.e. shifted by the start and widened to contain it. Subtracting per-axis Euler angles is exact only for a single-axis
  offset. Restitution 0.8 is set as Godot's `PhysicsMaterial.Bounce`; Havok and Godot combine two bodies' materials by different
  rules, so whether corpses bounce as in the original is **unverified**.
- **Rigs**: every PvP character uses this rig. Other ragdolls in the install (bodies): `havok_chaosspacemarine_coop` 19 (the
  Exterminatus enemy), `havok_boy_coop` 15, `havok_nob_coop` 16, `havok_ard_boy_coop` 16, `havok_gretchin_coop` 14,
  `havok_guardsman_a_coop` 16, `havok_psyker` 16 (not read yet).

### Multiplayer classes and loadouts — **supported by evidence** (M10)

Readers: `src/SmContent/MpClasses.cs`, `WeaponTuning.cs`. The class table is in `data/packages/global.oppc`, entry
`global` (BAF), at `extensions/multiplayer_settings/ingame_progression/classes`: six classes, `sm - assault marine`,
`sm - tactical`, `sm - devastator`, `csm - raptor`, `csm - tactical`, `csm - havoc`. Each has `ebp` (the race's single
player blueprint; both races also use the same animation package `havok_sm_base_mp`), `mp_class` ("assault",
"tactical", "heavy"), `race`, `base_wargear` (always carried: abilities such as sprint, evade or jump pack, and for
the Tactical the combat knife and bolt pistol), `startup_wargear` (the loadout choices: equipment, primaries, pistols,
melee), `class_perks`, `default_perk`, `unlocks/class_unlocks` (`xp_unlock` entries with `xp_required` and
`wargear_unlocks`), `override_wargear` (e.g. Devastator backpacks per heavy weapon), `default_armor_set`, `display_name`
(string ids) and `display_icon`. Profile levels: `extensions/profile_progression_info/level_table_info` (experience per
level). The player blueprints carry no loadout (`wargear_ext/dynamic_wargear` = True).

Weapon files (`wargear.oppc`): `common_data/slot` ("ranged", "pistol", "melee", "equipment"); the primary ability's
trigger policy template `ability_ranged_primary/trigger_data/trigger/$ref` (`wargear\trigger_policy\full_auto` for the
bolter, storm bolter, heavy bolter; `semi_auto` for the bolt pistol, Stalker bolter); the delivery type
`delivery/ranged_delivery_type/entry/type/$ref` (`…\raycast` for the bolter, plasma gun, lascannon, Stalker;
`…\projectile` for the grenade launcher; none for the melta, which uses a cone); heat (`fire_sim/…/apply_heat`).
Chainsword: graph `MP_SM_MEL_ChainSword`, sequencers `Sequencer_X1..X4`, abilities `ability_melee_x1..x4`.

Heat (M11): the pool is `common_data/heat/wargear\heat_info` (`heat_name`, `overheat_amount`, `high_heat_amount`,
`recovery_amount`, `cool_down_upgrade_list/upgradeable/{cool_down_rate, cool_down_delay_after_heat_gain,
cool_down_delay_after_overheat}`, `actions/high_heat` and `actions/overheat` effects); each shot's heat is the primary's
`delivery/fire_sim/…/apply_heat/heat_generated/entry/heat_to_generate` (default entry; perks select others). Charge
weapons have a separate ability of type `charge_ranged` (below).

Charged fire, projectiles and splash (M12, `WeaponTuning`; values in `spec/behavior.md`). An ability's type is its
`$ref` (`wargear\ability_types\standard_ranged`, `charge_ranged`, `heat_vent`, `detonate_projectile`,
`switch_fire_mode`, `reload`). The charge ability is `ability_ranged_secondary` on the lascannon, plasma gun and plasma
cannon but `ability_ranged_primary` on the plasma pistol, whose basic shot is its `standard_ranged` secondary.
`charge_states/<n>.charge_<name>` lists stage slots: `1.charge_light` (`minimum_charge_time` 0, no delivery type: fires
nothing), empty slots (`2.charge_dead_zone`, `$ref` ""), and one firing stage (`3.charge_medium` or `4.charge_heavy`)
with `minimum_charge_time`, `heat_applied_per_second`, `charging_action` (`apply_modifiers` `speed_maximum` ×0.5 on the
plasma cannon) and a full `delivery` (same layout as a primary's: `ranged_delivery_type`, `hit_sim`, `fire_sim` with
`apply_heat` either directly or in an `actions/actions_by_upgrade/actions/entry` command and a `delayed_action/delay`,
`ammo_used/_base_templates\ammo_use/amount`, `basic_weapon_tuning`, `max_range`). Ability-level: `charge_upgrade_list/
upgradeable/{max_charge_time, min_charge_time_multiplier}` (perks lower the multiplier), `reload_button_cancels_charging`,
`trigger_data/ammunition/…/required_to_start`, `charge_abort_action` (rumble only). `common_data/fire_mode_primary|
secondary/entry/upgrade_granted` names an upgrade per fire mode (e.g. `lascannon_secondary_fire_mode`); the lascannon's
charged-shot cooldown entry requires it, so the reader selects entries by that upgrade (interpretation).
A projectile delivery's `ranged_delivery_type/entry/type` adds `ebp` (`ebps\projectiles\<name>`, found in
`ebps.oppc` by file name) and `max_active_projectiles`. The blueprint's `extensions/projectile_ext` has `speed`,
`lifetime`, `collision_type` (raycast, rigidbody), `raycast_shape/radius`, `gravity_factor_over_distance/entry/
{distance, gravity_factor}`, `detonate_on_collision`, `detonate_when_lifetime_over`, `sticky`, `max_bounce_count`,
`stop_at_max_bounce`, `ground_lifetime`, `extra_aim_angle`, `rigid_info` (bounce reductions), `homing_info` (present,
unused values) and `detonate_action/commands`: `area_of_effect` (`shape` `…\sphere` `radius`, `fall_off/fall_off_entry/
{distance, intensity_value}`, `line_of_sight`, `ignore_self`, `target_filter`, `hit_actions/commands/do_damage` with
`damage_general`, `damage_armourshield`, `armourshield_breaker` and the material multipliers) and sometimes a direct
`do_damage` (plasma cannon charged shot). Some blasts have no `do_damage` (the frag grenade's second, reaction-only
blast); the reader takes the first damaging one. Grenades are equipment (`common_data/slot` "equipment") thrown by
`ability_utility` with the same projectile delivery; their default ammunition entry is keyed `wargear\ammunition_info`
(perk entries `ammunition_info`). The grenade launcher's grenades are detonated by `ability_utility` of type
`detonate_projectile` (`detonation_delays/time`, `input_override` reload). The melta has no ranged delivery type (a cone
`area_of_effect`; not read). *(Corrected in M13, below: the melta's delivery sits inside a pump state.)*

Thrown equipment and the melta cone (M13, `WeaponTuning`, `MpClasses`; values in `spec/behavior.md`; probes printed on the
install 2026-10-27, buildid 7412325). **Classes:** every MP class's `startup_wargear` lists four equipment items in this
order: `<race>_mp_eq_frag_grenade`, `<race>_mp_eq_krak_grenade`, `sm_mp_eq_stimpack`, `sm_mp_eq_melta_bomb` (Chaos
classes use `csm_` frag and krak but the `sm_` stimpack and melta bomb); no class has equipment in `base_wargear`. The
stimpack's only ability is `ability_stimpack` (`wargear\ability_types\stimpack`, no delivery). The melta bomb has
`ui_data/visible_in_customizer` false (every other item true) and its ammunition is named `plasma_grenade`; the krak
grenade's `ammunition_type` is `blind_grenade` and its `level_required` 5. **Throw:** `ability_utility` (`$ref`
`standard_ranged`), `trigger_data/trigger` `semi_auto`, `delivery/basic_weapon_tuning/entry/shot_cooldown` (frag and krak
1, melta bomb 0.5), `instant_fire` false, `warmup_time` 0, `allowed_fire_cone` 180, `muzzle_marker` `rig_r_hand`,
`ammo_used/entry/amount` 1; `common_data/fire_mode_primary/entry/animation_set` "grenade". Its timing is in the marine rig's
behavior graph `SM_Bolter_GrenadeToss` (one sequencer, `SM_RNG_GrenadeToss_01`, 1.367 s = 41 frames): tag `Event`
`Ability_Ranged_Fire` at 0.1463 (frame 6: 0.2 s), `Branch` `BRANCH_UseEquipment_End` from 0.4878 (frame 20: 0.667 s),
`Branch` `BRANCH_Evade` from 0.268; the jump-pack graph `MP_SM_NAV_JP_Sim` has the same toss with
`BRANCH_GrenadeToss_End`. **Rigid-body grenades** (`collision_type` rigidbody; frag, krak, melta bomb): `projectile_ext/
rigid_info/{ground_hit_horizontal_reduction 0.3, ground_hit_vertical_reduction 0.4, entity_hit_horizontal_reduction 0.05,
entity_hit_vertical_reduction 0.05, wall_hit_horizontal_reduction and wall_hit_vertical_reduction (`_base_templates\
angle_reduction_map`, `listing/map/{angle, reduction}`: 0 → 1, 90 → 0.2), delay_before_collision 0.1,
delay_between_collision 0.1, ground_angle (65 frag, 54 krak and melta bomb)}`, `stop_at_max_bounce` true, `min_distance` 5,
`detonate_action_on_ground` (empty) with `detonate_action_on_ground_max_height` 1, `post_detonate_lifetime` (melta bomb 10),
and `extensions/rigid_body_options_ext/{friction_override 2, restituation_override 0.65, mass_override 0.5,
impact_rotation_multiplier 0.5}` (Havok body settings; not used). **Blasts:** the krak grenade's first `area_of_effect`
(radius 8) has only `do_blindness` (3.5) and `apply_stun_damage` (70), its second (radius 30) `do_damage` 10 with
`do_blindness` 2 and `apply_stun_damage` 700; the melta bomb's first (radius 8) has `lifetime` 10,
`apply_every_this_many_seconds` 0.2 and `lifetime_limit_to_parent` true, its second (radius 6) is a single burst.
**Melta** (`<race>_mp_ranged_melta`): `ability_ranged_primary` has `$ref` `wargear\ability_types\pump_ranged` and its
delivery under `pump_states/wargear\ranged_pump_state/delivery` (`minimum_pump_amount` 1, `pump_ready_action`);
`ability_ranged_primary_old` and `ability_reload_old` are leftovers (other cones; no reload ability is current). The
delivery type is `_base_templates\ranged_delivery_types\shapecast` with `shape` `_base_templates\shape\cone`
(`shape_length` 20, `angle` 10, `start_length` 20, `shape_offset` −20) and `camera_aim_distance` 18; `hit_sim/actions/
do_damage` (200, `damage_armourshield` 25; an `actions_by_upgrade` entry needing `is_in_coop`: 100 + 300);
`damage_fall_off` (6.5 → ×1, 10 → ×0.5, 20 → ×0.15), `coverage_fall_off/fall_off_entry/{coverage, damage_multiplier}`
(1 → 1, 0.36 → 1, 0.35 → 0.75, 0.15 → 0.2), `ammo_used` amount −1, `allowed_fire_cone` 15, `basic_weapon_tuning` 0.33
(0.095 with `fury_aim_mode`), `instant_fire` true. Its ammunition entry has `capacity_clip` 1, `capacity_reserve` 12,
`recharge_delay` 0.5, `ammo_recharged_per_second` 0.5 and a `recharging_action` of five `delayed_action`s at
0.334–1.668 s (pump sounds; perks raise the rate to 1 and the reserve to 20). A survey of every `shape\cone` in
`wargear.oppc` and `ebps.oppc` always has `shape_offset` = −`start_length`; angles range 1–45 on weapons and 135 on a
building blueprint (read as the full apex angle). The bolter, sniper rifle and plasma pistol carry a small cone (10 long, 3°;
purpose not established, not read).

The jump pack (M14, `JumpPackTuning`, `MpClasses`; values in `spec/behavior.md`; probes printed on the install 2026-10-27,
buildid 7412325). **Classes:** `sm - assault marine` and `csm - raptor` carry `sm_mp_abil_jumppack` / `csm_mp_abil_jumppack`
in `base_wargear` (with `sm_mp_abil_sprint` and `sm_mp_core_abil_jump_evade`; the latter's `lifetime_actions` apply the
upgrade string `mp_fall_protection`, whose consumer is not found: no fall-damage value exists in the player blueprints or
`global`). Their four class perks are `mp_perk_jump_pack_{explodes_on_death, take_off_damage, ground_pound_damage,
reduced_cooldown}`. The two packs' gameplay values are identical (only sound event names differ). **Wargear:** slot
`coreability`; `ability_jumppack` (`$ref` `wargear\ability_types\jump_pack`, `trigger_data/trigger` `full_auto`, camera
curve `jump_pack_default`) holds `jump_info/_base_templates\jump_info` twice: the default entry (empty required list) and
one needing `mp_perk_jump_pack_reduced_cooldown` (recharge 15, `smash_available_delay` 2). A `jump_info` has
`jump_height_map/entry/{time, height}` (default, file order: 0.1 → 4, 0.1 → 4, 0.25 → 13, 0.4 → 18, 0.41 → 0, 0 → 0;
perk: 0 → 4, 0.1 → 4, …, 0.41 → 0), `power_settings/{max_power 100, recharge_rate 10, power_used/entry/{time, power_cost}:
0.1 → 35, 0.25 → 35, 0.4 → 55}` (the same `time` keys: read as the hold time of the button), `in_air_drift_gain` 0.01,
`in_air_move_gain` 0.02, `jumping_gravity` 20, `falling_gravity` 30, `jump_hang_gravity` 12, `jump_hang_time` 0.5,
`jump_forward_speed` 16, `smash_gravity` 75, `smash_can_be_used` true, `smash_initial_speed_boost` 50, `smash_limit_angle`
50, `smash_available_delay` 0, and actions: `action_ascending`, `action_apex` (an `aim_magnetism_strength` ×1.5 modifier),
`action_descending_regular`, `action_descending_smash`, `action_smash_target_point` (effects), `action_idle_by_power`
(entries `min_power_level` 55, 40, 0, 30 applying upgrade strings `mp_jumppack_min_power` / `mp_jumppack_low_power`;
consumer not found), `action_land_regular_by_height/landing_action_entry/{distance_from_apex_to_land, action}` (15: an
`area_of_effect` r 4, `do_damage` 15 melee, fall-off 3 → 1, 4 → 0.1, `trigger_reaction` flinch; 0: r 4, 5 damage, no fall-off
points; both `line_of_sight` and `ignore_self` true, `target_all`) and `action_land_smash_by_height` (0 and 8: empty).
`ability_groundpound` (`$ref` `wargear\ability_types\ground_pound`, trigger `semi_auto`, camera curve
`jump_pack_ground_pound_mp`) has no values of its own. `action_data/triggered_actions` holds `actn_gp_hit` (the pound's
`area_of_effect` in `commands/actions_by_upgrade/actions/entry`: default r 7, `do_damage` 25 explosive +
`damage_armourshield` 50, fall-off 0 → 1, 4.5 → 1, 7 → 0.05, `line_of_sight` true, `ignore_self` true, `target_all`,
`reaction_by_intensity` 0.81 → `reaction_mp_stun`, 0.8 → `reaction_mp_melee` with all `reaction_distance` values 0; the
perk entry 75 damage), `actn_take_off` (only a perk entry: r 4, 25 + 50 shield), `actn_jumpdeath_*`, and the assault
melee lunges `swing_jumpdash`, `antic_jumpdash`, `actn_dash_*`, `swing_rushattack` with abilities `ability_dash_attack`,
`ability_melee_dash`, `ability_hammer_dash` (not read). **Behavior graph** `MP_SM_NAV_JP_Sim` (tag times × clip length):
the take-off sequencer `Sequencer00` (`SM_JMP_Curve_F_Lo_Far_01`, 4.333 s) has `JumpPack_StartJumping` 0–0.267 s,
`ACTN_Take_Off` at 0.2 s and `BRANCH_To_Jump` from 0.333 s; the in-jump aim blend (`SM_JMP_Zero*_<dir>_Aim_<U|F|D>`: stick
directions F/B/L/R/up, aim up/level/down) has `BRANCH_Jump_To_Dash` and `BRANCH_In_Jump` but no jump-to-jump; landings:
`Non-Hammer Run Land Sequence` (`SM_JMP_Landing_LowRun`, 0.9 s: `BRANCH_Jump_To_Jump` 0.132 s, `BRANCH_Run` 0.333 s) and
`Non-Hammer Idle Land Sequence` (`SM_JMP_Landing_Low`, 1.2 s: 0.277 s, 0.462 s; `BRANCH_Jump_To_Idle` 1.033 s); the pound:
`SequencerGenerator05` (`SM_JMP_GP_Loop90Degrees_01` / `Loop30Degrees_01`, 1.767 s), `GP Impack Sequencer`
(`SM_JMP_GP_Impact_01`, 1.067 s: `StartAction` 0.1 s, `BRANCH_to_Getup` 0.356 s, `BRANCH_To_Idle_Getup` 0.853 s), then
`GP to Idle` / `GP to Run`; thunder-hammer variants (`SM_JMP_TH_*`) exist alongside. **Root motion:** the take-off clip's
root track (track 0) moves under 0.3 m over its 4.333 s and the impact clip's under 0.7 m, so the arc is not in the
animation: the jump is driven by `jump_info` (the simulation's model is an interpretation of it).

### Evade (Tactical roll, heavy hop): wargear, sequencers, extracted motion and poses — **supported by evidence for the data; the rules are interpretations** (M21, 2026-10-27)

Read-only probes of the install (buildid 7412325) with a scratch tool on `SmFormats`/`SmContent`; the reader is
`SmContent.EvadeTuning`.

- **Wargear.** `wargear.oppc` has `sm_mp_core_abil_evade`, `sm_mp_core_abil_heavy_evade` and `sm_mp_core_abil_jump_evade`
  (slot `coreability`, `load_type` `mp_accessory`), named in the classes' `base_wargear` (Tactical / Chaos Tactical, Devastator /
  Havoc, Assault / Raptor). `ability_data/ability_definitions/ability_evade` is a `wargear\ability_types\sprint` ability:
  `activation_type` "hold", trigger `semi_auto` with `input_queue_time` 0, `camera_curve` `evade_default_mp` /
  `evade_heavy_default_mp` / `evade_jumppack_default_mp`, `max_turn_rate` 40, and `special_nav_ability`
  (`min_time_between_activations` 0.8, `max_horz_angle_for_wall_hit` 25, time, distance and hit limits −1).
  `common_data/fire_mode_primary/entry/animation_set` is "evade" or "heavy_evade". `lifetime_actions` applies the upgrade
  string `mp_fall_protection`. The heavy file also has `ability_displace_kick` (melee: 30 primary / 10 secondary damage,
  `reaction_mp_stagger` 5 m back over 0.3 s, `apply_stun_damage` 25, `max_range` 4) and `ability_displace_slam`
  (`min_time_between_activations` 2, upgrade `mp_reaction_immunity`). No evade speed, distance or duration exists in the
  wargear or the player blueprints (`ebps`: only the AI's `dodge_*`/`evade_*` entries).
- **Cooldown (sim/evade-cooldowns).** `min_time_between_activations` 0.8 is the only gate on the three MP evades (`sm_mp_core_abil_evade`, `_heavy_evade`, `_jump_evade`). Their live abilities have empty
  `ammunition` and `heat` and zero recovery times; `autoshot` (1 round, 0.6/s) on the jump evade serves only its `ability_evade_disabled`. `SM_Base_MP` enters the evades on `BTN_Nav_Evade` (`if BOOL_IsSupported`) with
  no timer variable. Self-transitions such as `Bolter Evade --on BTN_Nav_Evade branch BRANCH_Evade--> Bolter Evade` start an evade inside the clip, so the cooldown cannot run from the move's end. 0.66 in `sm_abil_evade` and the
  non-MP `heavy_bolter` / `auto_cannon` / `plasma_cannon`; `generic_metadata/min_recovery_time` 2 in `sm_coop_evade` / `chaos_coop_evade` / `evade` is AI metadata. Reproduce: `smtool baf-dump data/packages/wargear.oppc --entry evade --grep min_time_between`.
- **Sequencers.** `SM_Base_MP` (the base multiplayer graph) holds "Ranged Evade Fwd/Back/Left/Right Sequencer" with the
  clips `SM_EVD_R2R_Roll_F_02_8m`, `_B_01_8m`, `_L_01_8m`, `_R_01_8m` (1.1667 s each), and two heavy sets of 0.9667 s clips:
  `SM_EVD_TB_Evade_*` (also in `MP_SM_HB_Brace`) and `SM_EVD_LC_Evade_*` (also in `MP_SM_LasCannon`). `MP_SM_PlasmaPistol`
  has `SM_EVD_CS_Roll_*`. Melee graphs and the jump-pack graphs reach a dash (`SM_JMP_Dash_B`, 4.333 s, `ACTN_Dash_Takeoff`,
  `ACTN_Dash_Duration`, `BRANCH_Evade_to_Jump`) and the `Evade Land …` sequencers. Tags of the ranged roll (fractions of
  1.1667 s): `FX_Evade_Start` 0, `FX_Evade_Roll` 0.2128–0.383, `FX_Evade_End` 0.5532 with `AbilityCamera_BlendOut`,
  `BRANCH_Run` and `BRANCH_ToRanged` 0.5714, `BRANCH_To_Weapon_Switch` 0.6596, `BRANCH_Melee_Combo` 0.6857,
  `BRANCH_toPistolRanged` 0.8571, `BRANCH_Evade` and `BRANCH_Evade_End` 1.0. Heavy (TB, of 0.9667 s): `BRANCH_Run` and
  `BRANCH_ToRanged` 0.4681, `BRANCH_Melee_Combo` 0.6552 (back 0.7241), forward `BRANCH_Evade` 0.70, end 1.0.
- **Tag objects (new reads).** Branch tags also hold a float @28 below their start (0.4571 for the roll's `BRANCH_Run`):
  probably an input window opening early; not read. Modifier tags point @52 at the modifier (name @32): the forward roll has
  an `hkbAlignCharacterModifier` "Align To Nav Direction01" (0–0.1, a field 180) and an `hkbCacheVariableModifier`.
  EntityProperty tags point @52 at property data with a value @28: `hkbEntityPropertyTargetableModeData` 1 (0–0.5957) and
  `hkbEntityPropertyCollisionModeData` 1 (0–0.4681) on the roll; meanings not established.
- **Extracted motion.** `hkaAnimation` @24 points at an `hkaDefaultAnimatedReferenceFrame` (612 in the rig, one
  `hkaCompressedAnimatedReferenceFrame`): up axis @16 (0, 0, 1), forward @32 (0, 1, 0), duration @48, samples @52 (array of
  4 floats: X, Y, Z, W; W read as the turn about up, 0 in every evade clip), one per frame including both ends (36 for
  35 frames). The rolls' root bone (`RIG_Root`) tracks stay in place (the pelvis rolls about 1 m over the ground), so the
  roll's travel is this motion: forward 8.924 m, back 9.174, left 9.240, right 9.640 (the clip names say "8m"); heavy TB
  6.03 / 5.98 / 6.00 / 5.98, LC 6.22 / 6.00 / 5.98 / 5.95. The R2R forward roll covers 7.11 m by `BRANCH_Run` (0.667 s);
  speed peaks about 18 m/s in the first 0.1 s (0.09 → 0.69 → 1.25 m).
- **Roll and hop; hyper armour (from the user's knowledge of the original).** The Tactical classes roll; Devastator and
  Havoc do a small hop (their heavy evade); Assault and Raptor jump with the pack. The `TargetableMode` and `CollisionMode`
  windows are moments of hyper armour that negate stun effects, not invulnerability; the data do not say which property is
  which, so both are recorded as hyper armour.
- **The hop in the data.** The TB clips' extracted motion has Z 0 in every sample, so the original's root motion does not
  lift the character. In the poses (`RIG_Root`/`RIG_Pelvis` model height, standing 1.093 m) the forward hop crouches to
  0.73 m at 0.067 s, rises to 1.186 m at 0.37 s (0.09 m above standing) and lands in a crouch (0.873 m at 0.73 s); both
  feet leave the ground: the lowest of `RIG_L/R_Foot` and `RIG_L/R_Toe` is above its first-frame height (0.006 m) by 0.18 m
  at 0.167 s, 0.30 at 0.300 s and back under 2 cm by 0.400 s. Peak clearances: forward 0.29 m, back 0.14, left 0.26, right
  0.24; spans 0.133–0.400, 0.133–0.367, 0.133–0.500, 0.167–0.533 s. The LC clips have the same pelvis curve. Whether the
  original's controller lifts the capsule during this or the hop is visual only is not decoded; the simulation lifts it
  (spec/behavior.md).
- **Locale.** String 110693 "Evade / Jump Pack" (the Space binding; M20).
- Not decoded: the state machines' transitions (which set plays for which weapon in hand), the alignment modifier's other
  fields, the property modes' meanings, what `collision_wall_actions` (empty) and the wall-hit angle do.

### Hit reactions, heavy strikes and blindness — **supported by evidence for the data; which clip set a reaction type plays and the rules are interpretations** (M28, 2026-10-27)

Read-only probes of the install (buildid 7412325) with a scratch tool on `SmFormats`/`SmContent` (BAF dumps of
`wargear.oppc`, `ebps.oppc`, `global.oppc`, `character_reaction_mapper.oppc`, `hit_screen_effects.oppc`; behavior-graph
sequencers and state machines of `havok_sm_base_mp__hkxx`; the English `ucs.pc` searched in memory). Readers:
`SmContent.ReactionTuning`, `MeleeTuning` (heavy strikes), `WeaponTuning` / `JumpPackTuning` (blast reactions). Values in
spec/behavior.md "Hit reactions".

- **Reaction types reaching players.** A survey of every `trigger_reaction` in the `_mp_` wargear and the multiplayer projectile
  blueprints finds three multiplayer types: `reaction_mp_melee` (122 uses), `reaction_mp_stun` (125 + 34 in `ebps`) and
  `reaction_mp_stagger` (94 + 12). Every other type is a flinch (`reaction_flinch`, `reaction_ranged_light`/`_heavy`,
  `rng_*`) or a campaign/co-op name (`ck_x`, `cs_2xy`, `pa_3xy`, `cs_y`, …; these sit in `delayed_action`s with delay 0, next
  to the multiplayer reaction). `reaction_by_intensity/entry` holds `intensity`, `reaction_type` and `reaction_distance/
  {distance_right, distance_backward, distance_up, duration}`; entries are listed by descending intensity.
- **The mapping.** `sm_space_marine_player` and `csm_chaos_marine_player` name `animated_character_ext/character_reaction_mapping`
  = `character_reaction_mapper\multiplayer_reaction_mapping`: entries `reaction_mapping/character_reaction_mapping/{source_reaction,
  source_hit_zone, destination_reaction}`; with an empty hit zone the melee weapons' `ax_default_*`, `cs_default_*` (including
  `_dir180`, `_dir180_finisher`, `_draw_attack`) and `th_default_*` become `reaction_mp_melee`; the leg hit zones become
  `rng_leg_hit`. The knife's light attacks name `cs_default_draw_attack` and `cs_default_2` (mapped) and `reaction_mp_melee`.
- **Stun damage is inert.** `health_ext/stun_threshold` 1,000,000, `stun_damage_decay` 1000, `stun_damage_decay_delay` 0,
  `stun_default_length` 0, a `stunned_action` (sound `play_player_stunned`, `scatter_angle` ×2, `can_revive_others`), and
  `min_hit_reaction_time` 1. `apply_stun_damage` values (25 per knife hit, 100–300 per heavy strike, 70 / 700 or 1000 per
  krak blast) cannot reach the threshold, so the "stun" of multiplayer is the `reaction_mp_stun` reaction.
- **Heavy strikes.** Locale: 110692 "Stun / Alt Strike" (`%ABILITY_MELEE_HEAVY%`); combat-controls rows 120689–121053, e.g.
  "Backhand" / "Light stun/interrupt (single target)", "Kick" / "Medium stun (wedge)", "Shoulder Bash" / "Heavy stun (large
  wedge)", "Foot Stomp" / "Super Heavy stun (radial)"; the chainsword's description (121288) lists Y, X Y, X X Y, X X X Y and
  X X X X Y. The knife graph `MP_SM_MEL_CombatKnife` has `CK Y1..Y4 Sequence` (Y1–Y3 play `SM_MEL_CS_YATK_Combo1..3`, Y4
  `SM_MEL_CS_YATK_Combo4_3.5m` with the action tag `DISPLACE_FootStomp` at 0.4426 and `DISPLACE_FootStomp_Coop`); its wargear has
  `ability_melee_y`, `_y2`, `_y3` (no `_y4`) and `action_data/triggered_actions/displace_footstomp` (two `actions_by_upgrade` entries:
  the default r 7, 25 + 50, and `mp_perk_better_stomp` r 10, 35 + 75). The chainsword graph names its Y sequencers generically
  ("CS Y Atk Sequencer", "Sequencer22", "Sequencer21", "Sequencer20" = `…_DSP_Stomp`, tag 0.46). Y strikes carry
  `BRANCH_Melee_Y_Combo` windows (not used).
- **The reaction graph `MP_SM_Reactions`.** Events include `PORT_Reaction_MP_Stun`, `PORT_Reaction_MP_Stagger`, `Incapacitated`,
  `BRANCH_HitReact`, `BRANCH_Reaction_End`, `BRANCH_Evade`, `BRANCH_Run`, `BRANCH_Melee_Combo`. States (hkbStateMachineStateInfo:
  name @56, state id @60, generator @52, transitions @48; hkbStateMachine states array @92; hkbStateMachineTransitionInfoArray @8
  with 44-byte transitions, event id @8, target state @12 — layout read off this graph, **hypothesis** beyond it): "Incapacitated"
  (a reference to `SM_REC_Incapacitated`: `SM_REC_Idle2Incap_D_01`, a 2 s loop, `SM_REC_Stun2Idle_D_01`; the co-op downed
  state), "Ranged Reactions" → "Grenade Reactions" (`SM_MP_REC_Grenade_F/R/B/L_01`, pie-wedge conditions) or "Extended
  Reactions" (`SM_REC_Stagger_F/B_01`), chosen by bool conditions, and "CSM Reactions" (`SM_REC_Light_Itrpt_FHL/BHR_01`). The
  stagger and grenade sequencers carry `ReactionMovement` tags (0.07–0.27 of the clip) and ragdoll modifiers. Reading the ports as
  the stun → grenade set and the stagger → stagger set, and `reaction_mp_melee` → the light interrupt, is an **interpretation**:
  the transitions that consume the ports were not decoded (their event ids read as −1 with this layout).
- **Attack windows without `BRANCH_HitReact`.** Every melee sequencer carries `BRANCH_HitReact` windows, e.g. knife CK1 0–0.1778
  and 0.2444–1 (a gap around its hit), CK3 0–0.1724 only, Y4 0–0.3167 and 0.7–1; the reaction clips carry them too (light
  interrupts from 0.32–0.4; the stun and stagger clips none).
- **Blindness.** `do_blindness/blindness_duration` on the krak blasts (3.5 and 2 s; the upgraded blueprints 6 and 3);
  locale 111408 warns blind grenades affect teammates. The player blueprint's `take_*_damage_action` camera effects (0.1 s,
  desaturation 0.1, no shake amplitude) and `hit_screen_effects.oppc` (14 directional hit overlays) were not used.
- Not decoded: the displace kick's trigger (heavy evade and knife files, graph `MP_SM_MEL_Displace`), the bull rush and dash
  attacks, the camera effect of a reaction, the original PC key of the heavy strike.

### Level collision (`<region>.hkt`) — **supported by evidence** (M7, 2026-10-26)

Reader: `src/SmFormats/Havok/HavokWorldCollision.cs`; map loader: `src/SmContent/MapContent.cs`. Read without error on all
11 multiplayer maps that have a region archive (`data/chunks/<map>__wrld.oppc` or `__r000_main.oppc`, entry
`wrld.hkt` / `r000_main.hkt`); the four `mp_dlc3_*` maps have only a `.world` file and are not read yet.

- hk_2010.2.0-r1 packfile. The root container has one variant per collision layer, named by number: "2" and "8" on every
  map (32,000–120,000 triangles each), "7", "12" and "3" on some. Each is an `hkpMoppBvTreeShape` (child pointer at +52),
  usually over an `hkpListShape` (child-info array at +24, 16 bytes per child) over another MOPP tree over one
  `hkpStorageExtendedMeshShape`. The MOPP code is not read.
- `hkpStorageExtendedMeshShape`: triangle subparts at +184 (112 bytes each), mesh storage pointers at +240.
- Triangle subpart: +20 triangle count, +28 vertex count, +36 vertex striding (16), +40 triangle offset (running total),
  +44 index striding (u16, 8), +46 striding type (2 = 16-bit indices; the only one seen), +48 extrusion vector,
  +64 `hkQsTransform` (translation, rotation xyzw, scale; applied scale, then rotation, then translation).
- **Instancing**: there are far fewer storages than subparts (mp_dlc2_map4 layer "2": 106 storages for 1,972 subparts), and the vertex and index
  pointers are null in the file. The storage a subpart instances is the **w component of its extrusion vector**, as a
  float. Evidence: on all 11 maps every such index is an integer in range, its storage has exactly the subpart's vertex
  and triangle counts, and every storage is used (mp_dlc2_map4: 3,907 subparts across its three layers). Other checks
  that hold on all 11 maps: no convex shape subparts (array at +196 empty; the reader refuses them), and the welding
  array at +208 has exactly one entry per triangle (the reader uses it as a cross-check).
- Storage (`MeshSubpartStorage`): vertices at +8 (`hkVector4`), 16-bit indices at +32 (four per triangle, the fourth 0).
- Placement check: a ray down from each of mp_dlc2_map4's 20 TDM spawn markers meets layer "2" and "8" ground within
  0.1 m of the marker.
- Layer meaning is an **interpretation**: "2" (with low step and ramp helpers) for characters, "8" (without them, with
  some extra thin panels) for shots. Neither contains the other; inside the play area both cover similar area.
- **Surface materials (M25a)**: the subpart's `userData` at +16 (i32) is its triangles' surface id; a subpart with
  `userData` −1 has material index striding type 1 (8-bit indices) at +1, material index striding 1 at +8 (u16) and
  `numMaterials` at +10 (u16), and its storage holds per-triangle `materialIndices` at +56 (`hkArray<u8>`, one per
  triangle) and `materials` at +68 (12 bytes each: filter info, restitution and friction as halves, `userData` i32 at
  +8). Values over the 11 maps: 0, 1, 2, 3, 4, 7 (Manufactorum layer "8": 0 ×7,227, 3 ×646, 4 ×20,643; per-triangle
  storages carry materials {4, 3}). The same ids appear as `CShapeMesh.MaterialID` in object definitions (−1, 0, 3, 4 on
  Manufactorum). Read as an index into the surface order of the material tables (docs below, "Effects"): an
  **interpretation**. Reader: `HavokCollisionLayer.Materials`; the simulation: `StaticMesh.MaterialOf`.

### Physics shape meshes (`ppk`) and movable props — **supported by evidence** (M39, 2026-10-28)

Reader: `src/SmFormats/Ppk/PpkMesh.cs`; props: `src/SmContent/MapProps.cs`. Survey: a read-only probe over every `ppk` entry of
the install (1,970; private notes in `%LOCALAPPDATA%\sm-reimpl\cache\m39\`).
- Every entry starts with u32 0x66; +0x04 is a part count (1 to 34). The **single-part** layout (910 entries; every one of them
  passes every check below): +0x08 u32 0x50 (data start), +0x10 u32 vertex count, +0x18 u32 triangle count, +0x1C u32
  triangle offset from the data start (the vertex block's end, 16-byte aligned), +0x2C u32 MOPP block size (padded), +0x30 u32
  MOPP offset from the data start (the triangle block's end, aligned: 908 of 910), +0x34 float4 MOPP code info (offset xyz at
  or below the vertex minimum, scale w); vertices are 3 floats, triangles 3 u16; file size = 0x50 + MOPP offset + MOPP size
  (910 of 910). Multi-part entries have a longer header and are not read. The MOPP code is not read.
- **Which shapes are not in the level's baked collision.** Manufactorum (`mp_tdm_09__wrld.oppc`) has two `ppk` entries,
  `ammo_crate_A__d` (8 vertices, 12 triangles: a box −0.583..0.583 × −1.509..1.509 × 0..1.467 m) and
  `accessories_doorbutton__d` (30 vertices, 56 triangles). The definitions that name them give their bodies `MotionType` 2,
  `Mass` 70, `AutoActivate` false (`ammo_crate_A`: `BodyType` 3, `Restitution` 0.1; the door button: two bodies, `BodyType` 2
  and 8); static bodies (e.g. `ammo_crate_black`) have `MotionType` 0, `Mass` 0 and a `__s` shape that is not an entry (baked into
  `wrld.hkt`, whose layers are named "2", "7", "8", "12": `BodyType` 2 and 8 match the character and weapon layers' names,
  an observation). Counting the collision triangles inside each placement's visual bounds: all 49 `ammo_crate_A` placements
  (layer `e_cover`, material `MAT_CrateGreen`: **the green crates**) have none in any layer, while all 73 `ammo_crate_black`
  have layer "2" and "8" triangles. So the green crates are moving rigid bodies whose collision exists only as their `ppk`
  shape; the simulation now loads such shapes as static props (spec/behavior.md M39).
- The 49 green crates stand in 11 named stacks (`ammo_crate_a_29` … `_47`), stacked two high (bases z −0.25 and 1.15, the
  boxes overlapping by 7 cm), a few tilted or on end.

### Weapon penetration (`delivery/penetration`) — **supported by evidence for the values; rules are interpretations** (M39)

`penetration/entry` (by required upgrade): `points`, `material_penetration_costs` (keys are simulation materials: `default`,
`default_body`, `tough_body`, `default_head`, `tough_head`, `default_explosive`, `default_light_armor`, `default_heavy_armor`,
`tough_light_armor`, `tough_heavy_armor`, `default_shield`, `tough_shield`), optionally `max_viz_material_thickness` (keys are
the surface list: default, dirt, …; the bolter's perk entry: stone 0.5, metal 0.5, armour 0.3, energy_shield 0.15), and
`exit_environment_action` (a material effect `…_exit` and an `area_of_effect`: a cone `shape` with `angle`, `shape_length`;
`hit_actions/commands/do_damage`; `target_filter`; `line_of_sight` true). Multiplayer values (Space Marine and Chaos files
alike): lascannon default entry 10 points, `default` 1, bodies and heads 1, light and heavy armour 20, exit cone 15 m, 1°,
50 damage, target_all; its charged stage 10 points, every cost 0, exit 25 m, 1°, 50 damage (head ×3), enemies and neutrals;
stalker bolter 10 points, `default` 5, body 4, head 3, light armour 9, heavy armour and shields 0, exit 10 m, 3°, 20 damage,
target_all; bolter only an entry needing `mp_perk_weap_bolter_kraken_rounds` (2 points, `default` 1, heavy armour 2, exit 10 m,
3°, 16 damage); heavy bolter 0 points, every cost 0; the other weapons an empty `penetration`. Not established: what a cost of
0 means (free, or not penetrable), whether costs apply per object or per surface, and which surfaces count as "environment". Players: `sm_space_marine_player` `extensions/entity_ext` `material_simulation` `default_body`, overridden per hit zone (`head` → `default_head`; body, arms, legs → `default_body`), so a player costs the lascannon 1, the stalker 4 (body) or 3 (head), Kraken Bolts 1 (M39 reads these).

### Layer files (`.Layer`) — **supported by evidence for marker layers** (M7)

Compressed BOD (below). A `RegionLayer` holds `NavMesh` (a `NavMeshChunk`) and `Root` (a `WorldGroup`) whose `Objects`
list holds placed objects. TDM spawns are `base/missionmarkersphere` objects named `tdm_mkr_spawn_NN` with `Position`
(metres), `Rotation` (degrees; facing is +Y rotated by the Z angle: on 7 maps that direction points toward the spawn
area's centre, mean cosine 0.36–0.84, while +X gives about 0) and `Scale`. `d_blockers.Layer` holds
`base/MissionMovementBlocker` boxes (not used yet).

### Teams and Annihilation rules — **supported by evidence for the sides; rules partly in scripts** (M15, 2026-10-27)

Reader: `src/SmContent/GameModes.cs` (teams, script check); class sides from `MpClasses` (`race`). Searched with read-only
probes over every non-singleplayer archive for `annihilation`, `score_limit`/`scorelimit`, `friendly_fire`,
`respawn_time`, `team_kill`, `time_limit`/`timelimit`, `suicide`, `spawn_protection`, `game_mode`, `tdm`.

- **Teams** (attribute data): `global` (`data/packages/global.oppc`) `extensions/multiplayer_settings/teams` has two
  entries, `space marine team` (`ebp` `ebps\multiplayer\race_marine\sm_space_marine_player`) and `chaos team`
  (`ebps\multiplayer\race_chaos\csm_chaos_marine_player`), both `$ref _base_templates\multiplayer_team`, no other fields.
  The six classes' `race` (`space_marine`, `chaos`) gives each class its side. The other `multiplayer_settings` siblings are
  `ingame_progression` (classes), `xp_settings_pvp` / `xp_settings_coop` (experience rewards: kill streaks, multi-kills
  within `multi_kill_interval` 2 s, headshot 35, revenge, avenger, round win 250, assists by `damage_ratio` 0.25/0.5/0.75
  for 25/50/75 % — progression, not match scoring), `challenge_groups`, `coop_campaigns`, `coop_skinning_weights`. No score
  limit, time limit, respawn time, friendly fire or spawn protection value exists in `global`, the other attribute
  packages, or `ebps`.
- **Mode scripts** (`data/packages/util.oppc`, `util/mp/...`): compiled Lua 5.1 (`1B 4C 75 61 51`, format byte 6,
  4-byte floats). **Constant pools only**: a scan listed each function's string and number constants in file order; the
  instruction stream was not read or decoded and nothing was executed. Constants present:
  `util/mp/scripts/data/objectives.lua`: `TeamDeathmatchMarine`, `TeamDeathmatchChaos`, `Deaths`, `group`, `Team`,
  `TEAM_ORK`, `TEAM_MARINE`, `isDeathMatch`, `TeamDeathmatchTimer`, `timerComplete`, `timerShowMinutes`,
  `TeamDeathmatchEnd`, `RandomizeTeamSpawning`, `triggerOnMissionStart`, `randomPerSpawn`, `locations`,
  `tdm_mkr_spawn_01`; `objectives/deaths.lua`: `scoreLimit`, `Network_GetScoreLimit`, `isDeathMatch`, `DeathsDeathEvent`,
  `Event_AddEntityDeathWithData`; `objective.lua` (one function's pool): `timer`, `Network_GetTimeLimit`, −1, `timerEnd`,
  `Game_GetNetworkTime`, 60; `team.lua`: `TEAM_MARINE`, `TEAM_CHAOS`, `TEAM_ORK`, `Network_SetMinPlayerCountPerTeam`, 0,
  `EmptyTeamGameOver`, `GetRandomAreaAwayFrom`. **The relationships are an interpretation** of which constants occur
  together: each deathmatch objective counts deaths of the opposing team's group and completes at the host's score limit;
  the timer runs `Network_GetTimeLimit` minutes (−1 none); a team that empties ends the game. `util/mp/rules_tdm.lua`
  (constants `TDM_MarineDied`, `TDM_OrkDied`, `g_team_points` 20) looks like an older marines-vs-orks prototype, not the
  shipped mode. `smserver` checks that the eight rule names it relies on are present (8/8 on this install).
- **Host options** (front end `data/packages/ui_fe.oppc` `HostOptions.gfx`, Scaleform): buttons `tdm_score_limit_btn`,
  `time_limit_btn`, `friendly_fire_btn`, `spawn_cadence_btn`, `max_shield_btn`, `shield_regen_btn`, `allow_perks_btn`;
  the value lists come from the executable (`Native_SetTdmScoreLimitArray`, `Native_SetTimeLimitArray`, …,
  `Native_SetDefaults`), so the defaults are not in the data. `Hud.gfx` and `PlayerDeathScreen.gfx` show a respawn time;
  the perk `mp_perk_respawn_timer` only applies an upgrade string of that name (its effect is in code).
- **Spawns**: on mp_dlc2_map4 `d_tdm.Layer` has 20 shared `tdm_mkr_spawn_01..20` markers (no team field); team-specific
  markers are in `d_mission_objects.Layer`: start areas `mkr_start_marine` / `mkr_Start_Ork` (18 × 10 m boxes, used by
  other modes), `mkr_marine_respawn`, objective areas, and per-team `MissionMovementBlocker`s with shield effects
  (`marine_blocker_*` `BlockRace` 4, `chaos_blocker_*` `BlockRace` 3) that the scripts switch per mode. Annihilation spawns
  therefore pick among the shared markers by script (`RandomizeTeamSpawning`), rule not decoded.
- `data/config/w40k_sm_quickmatch_tuning_file.xml` (loose file) has matchmaking tuning only (`PlayersToStart` 4,
  `PlayersToGame` 6, map lists), no match rules.

### Chaos Space Marine multiplayer models — **supported; the M17 class-to-set mapping is superseded by the data (owner, 2026-10-28)** (M17, 2026-10-27)
- **Rig:** there is no separate Chaos rig. The Chaos armour pieces bind, by bone name, to the same Havok skeleton
  `havok_sm_base_mp__hkxx` (50 bones, in `data/chunks/ebp_sets__mp_versus_set.oppc`); no "bone not in the skeleton" note was
  produced for `havoc_01` or `raptor_01`. Chaos players therefore play the same clip names (613 clips).
- **Armour:** `data/packages/csm_armour_mp_<set>_<part>.oppc`, parts `torso legl legr arml armr helmet shldrl shldrr backpack`
  (raptor sets have no backpack; the pack is wargear). Sets: `basic`, `havoc_01..03`, `raptor_01..03`, `berserk_01..03`
  (`_bp` instead of `_backpack`), `children`, `ironwarr`, `dlc1_set1`, `dlc2_set1`, `dlc3_set1/2`.
  M17 had not read the class data and followed the class name (**interpretation**: `csm - tactical` → `basic`, `csm - havoc` → `havoc_01`,
  `csm - raptor` → `raptor_01`). **Superseded (owner's decision 2026-10-28: "havoc default armor yes")**: the classes name their default armour in
  `default_armor_set` (`chaos_default`: the `basic` pieces) with `override_wargear` for the Raptor's head and legs and the Havoc's backpack, and the
  client's default look is that, resolved by `AppearanceResolver` (spec "Default look"). The mapping function is removed.
- **Textures are in sibling packages**, not the mesh's own: `<piece>.oppc` holds the `.dcm`, one `standard_2.bmat` (and a `_nrm`),
  `<piece>_wg.oppc` holds `_dif/_spc/_lp/_pnt` at full size, `_tt1`/`_tt2` the same at lower resolutions, `_v` repeats the mesh
  package, `_tsf/_tsh/_tst` hold only `_nrm`.
- **Material:** shader `character_wargear`; its `Parameters` list holds `WargearTextureParameter` structs (one per texture type)
  whose fields `DiffuseMap`, `SpecularMap`, `LightPropMap`, `PaintMask` name the textures and `AdditionalPackageName` the package;
  also `DirtColour` and `TransScaleParams`. `BmatMaterial` reads the first struct's names; the client uses the diffuse and specular maps and the piece's `_nrm` as normal map (see "Map lighting and normal maps").
  Chapter/legion colours come from the paint mask (`_pnt`) and the player's paint scheme: see "Armour paint" below.
- **Rigid sub-meshes (interpretation, checked by eye on `basic`, `havoc_01`, `raptor_01`):** each mesh block has a
  `StaticMeshVisual` in the `.O3d` (`MeshID` = block index, `RefNode` = a `GEO_*` node). The node hangs under a `RIG_*` node (a skeleton
  bone) with its own local position/rotation (degrees, X then Y then Z). An un-skinned block is placed as vertex x GEO local x the
  bone's model-space reference pose and bound fully to that bone; this fixed the raptor pads, torso and helmet. Blocks whose
  `RefNode` contains `_lod` are not drawn (the same piece has a lower-detail twin). The Space Marine armour keeps the older
  one-bone-per-piece rule (unchanged).
- **Texture precedence** `_wg` > `_tt1` > `_tt2` assumes the packages hold equivalent same-named textures; only the first
  `WargearTextureParameter` of a material is read.
- **Gaps:** berserk and other sets unused; the raptor's jump pack is not drawn (see "Armour paint" for the pack's colours); Space Marine
  classes all use one armour set (now `sm_armour_mp_basic`, see below).

### Armour paint: chapter and legion colours — **supported by evidence for the data; the shading maths is a provisional approximation** (2026-10-27)
Reader: `src/SmContent/PaintSchemes.cs` (`PaintSchemes`, `PaintScheme`, `PaintSet`, `PaintColour`); shader `client/MapMaterials.Paint.cs`.
Findings on the install (app 55150):
- **The multiplayer Space Marine armour is `sm_armour_mp_<set>_<part>`** (`basic`, `veteran`, `mk4`, `relic`, ... ; `bp` instead of `backpack` in
  the DLC sets), built exactly like the Chaos sets: material `character_wargear`, textures in a sibling `_wg` package (`_dif` unpainted grey
  shading, `_pnt` paint mask DXT5, `_lp`, `_spc`; `_tt1/_tt2` lower resolutions), mesh blocks placed through the `.O3d`. The M3 set
  `sm_armour_basic_001` (singleplayer-style, colours baked into the diffuse: blue plates, gold eagle) has no mask. `LoadMarine` now uses
  `sm_armour_mp_basic` when all nine parts exist, else the old set (`legacyArmour` forces it). Which MP set is a class's default is not
  found in the data read (the unlockable sets appear in `global`'s progression as `wargear\wargear\multiplayer\race_marine\armour\sm_veteran_torso` and similar);
  `basic` is the **interpretation**, as for Chaos.
- **Paint mask channels (Verified-local, each channel viewed on `sm_armour_mp_basic_torso_pnt` and `csm_armour_mp_basic_torso_pnt`):**
  R = primary colour region (main plates), G = secondary (accent plates, trim), B = tertiary (ornaments: the eagle's wings and the skull on
  the Space Marine torso, studs and rings on Chaos), A = dirt. Regions barely overlap; where all three are 0 the diffuse is drawn as is.
  The jump packs and backpack weapons use the same convention: their material `mp_custompaint` (`pack_mat_copied_realtime`,
  `backpack_onthefly_composite`) names `PaintMask` and holds sample `DiffusePaint1..3`, `SpecularPaint1..3`, `DirtColour` parameters
  (template values, not the player's: that confirms the three-colour model). `sm_plasmacannon/lascannon/hbolter/generic_mp_bp_001` use `standard_2`.
- **Colours:** `custom_paint_colour.oppc` (65 entries, no folders; `ultramarines_blue` occurs twice: the customizer-visible one, which the
  schemes' `citadel_paints\...` paths name, is used) has `diffuse` and `specular` RGB tables (0..1) and a `visible_in_customizer` flag.
  `custom_paint_scheme.oppc` (80) has `paints/<slot>` (head, leftshoulder, rightshoulder, leftarm, rightarm, torso, leftleg, rightleg,
  backpack, and empty weapon slots) with `primary_colour`, `secondary_colour`, `tertiary_colour`, `decals/decal`, `pattern_enabled`; an empty
  slot uses `default_paint`. Colour and decal references are paths whose last segment is the entry name.
- **Defaults (Verified-local):** the player blueprints in `ebps` name them: `sm_space_marine_player` -> `extensions/wargear_ext/default_custom_paint`
  = `sm_ultramarines` (blue `ultramarines_blue` 0.166/0.321/0.451 on all plates; gold `burnished_gold` 0.568/0.396/0.176 as the shoulder
  pads' secondary and everywhere tertiary; left pad decal `sm_ultramarines_orig`, right pad `sm_rank_tactical` in the `ultramarines` variant),
  `csm_chaos_marine_player` -> `chaos_multiplayer` (`blood_raven_red` 0.331/0.101/0.011 primary and secondary, gold tertiary; shoulders red,
  gold, gold, no pattern; other slots empty -> `default_paint`). Other presets exist for `--paint`: `sm_<chapter>` (blood_angels, dark_angels,
  space_wolves, ...), `csm_<legion>` (alpha_legion, black_legion, ...), `color_test_*`, `dlc_*`. Singleplayer co-op blueprints name other schemes
  (`ultramarines_veteran`, `chaos_undivided_temp`).
- **Dirt alpha (checked on 14 masks: Space Marine and Chaos torso/helmet/legs/backpack, the jump packs and the four backpack weapons):**
  alpha is sparse everywhere (mean 0 to 25 of 255; above 128 on at most 1.3 % of pixels) and exactly 0 on the heavy bolter backpack and the Chaos
  jump pack masks, so the dirt step is a no-op there. The client still turns dirt off for a mask whose alpha is constant and high.
- **Material flags:** all 316 materials with a `PaintMask` (`character_wargear` and `mp_custompaint`; names `standard_2/3`, `sm_torso`, `backpack`,
  `pack_mat_copied_realtime`, ...) have `BlendMode` 255, `CullMode` 255 and no `EmissiveMap`. The painted shader still honours `BlendMode` 8
  (scissor) and `CullMode` 0 like `Build`, and notes an `EmissiveMap` it does not draw.
- **Fallbacks and cost:** `PaintSchemes.Load` reads colours, schemes and the two player blueprints each on its own; a missing or unreadable package
  or entry leaves the rest and the built-in defaults (`sm_ultramarines`, `chaos_multiplayer`) and adds a note. The client reads it lazily, once
  (a failure is cached); if the Space Marine has no usable scheme (read failed or `--paint` names none) it wears the pre-painted `sm_armour_basic_001`
  with one note instead of grey armour. Measured on the install: 80 schemes and both defaults in 18 to 24 ms,
  so no further sharing with `GameplayTuning`'s read of `ebps` was done.
- **Not applied / gaps:** `pattern_enabled` and the class badges (see "Armour decals" below); the specular colour's alpha (0 in the data);
  the original's colour space for the stored colours. `LightPropMap`: decoded and used since M40-T13 (see "Light property maps" at the end).
- **Provisional approximation (client):** shading = luminance of the diffuse x 3.1 (`SM_PAINT_GAIN`; 2.5 by eye before M40-T13, 3.1 measured
  against the pre-painted set, see "Light property maps"); albedo = mix(diffuse, shading x
  colour, mask R+G+B) with the three colours weighted by their channels, then mixed toward `DirtColour` x shading by 0.6 x mask alpha;
  the colours are read as sRGB (Godot converts; `SM_PAINT_LINEAR=1` reads them as linear, far too light) because on `sm_ultramarines` that
  matches the baked blue of the old set; specular scale in painted regions = luminance of the paint's specular colour x 0.8 (0.25-0.8);
  roughness from the spec map as for other materials. `--paint <scheme>` (viewer) tries another scheme of the install.

### Armour decals: the chapter badge and legion icon on the shoulder pads — **supported by evidence for the data; the placement is an interpretation** (M30, 2026-10-28)
Reader: `PaintSchemes.Decal`, `DecalFor`, `ReadDecalTexture`, `HasDecalUv` (`src/SmContent/PaintSchemes.cs`); shader `client/MapMaterials.Paint.cs`.
- **Data (Verified-local).** A scheme's slot has `decals/decal`, a path whose last segment names an entry of `custom_paint_decal.oppc` (122 entries, e.g.
  `sm_ultramarines_orig`, `csm_alpha_legion`; colour variants `_orig`, `_bw`, `_black`, `_white`, `_inverted`). An entry has `decal` (texture), `decal_package`
  (always the same name in the install; five use `blank`, which has no package), `visible_in_customizer`, `race`. The texture is a 128x128 DXT5 `.dds`
  without mips in `data/packages/<decal_package>.oppc` (e.g. `sm_ultramarines_dif`, `csm_alphalegion`): grey-white or coloured artwork, the alpha channel is
  the shape (with scratches). No decal entry holds a position, scale or locator.
- **Slots.** Only `leftshoulder` (70 of 80 schemes) and `rightshoulder` (10) name decals. The defaults: `sm_ultramarines` names `sm_ultramarines_orig` (a white
  Ultramarines "U") on the left pad and none on the right; `chaos_multiplayer` names none (its `pattern_enabled` is false), so the default Chaos look has
  no decals.
- **Where it goes (interpretation).** The armour materials carry `TransScaleParams` / `TransScaleParams2` = (0, 0, 1, 1) and their object trees flag the
  visual `DecalReceiver: True` (also set on every other piece, so it is no placement). The pad meshes have a second UV set (`Uv1`) that is a single island
  covering the unit square over the pad's outer dome (range about -0.08..1.08), unlike the first set (an atlas shared with the pad's trim). Mapping the
  decal 1:1 onto `Uv1` puts the badge on the dome, large and centred, upright and unmirrored for the symmetric badges checked (Ultramarines "U" and
  chaos star: tabs up). Pieces whose `Uv1` is degenerate (all zero: the Chaos `basic` right pad, every Havok and Raptor pad) take no decal. `HasDecalUv` only checks that the set
  is not degenerate, thin or tiled (range over 0.5 in both axes, inside -0.5..1.5); other layouts of that size pass it too (the Space Marine torso's and
  helmet's, up to 1.25), so the gate is the scheme slot: decals exist on `leftshoulder` and `rightshoulder` only, and the client asks only for those pieces. The engine's own decal code is not read; whether the original scales or offsets the badge (the identity
  `TransScaleParams` suggest a per-instance override) is unknown.
- **Other shoulder pieces (checked over all 86 `*_shldrl/_shldrr` packages).** A second UV set over the unit square (so `HasDecalUv`) is on the Space Marine
  pads of `mp_basic`, `mp_dlc3_set1`, `mp_mk4`, `mp_master_001`, `mp_damage` (left), `mp_veteran` (left), `mp_bloodravens` (right), `mp_mk6` (right), the old
  `black_temp` (right) and the singleplayer `basic_001`, `blood_ravens`, `dark_angels` (left; range -0.19..1.47 x -0.12..0.83, a different layout) and, among
  the Chaos sets, only `basic` (left). The client loads only `sm_armour_mp_basic` and the `csm_armour_mp_*` sets, so today only the `mp_basic` pads and the
  basic Chaos left pad can carry a decal; the other Space Marine sets are not reachable (no set choice yet) and the singleplayer pieces have no paint mask.
  If more sets are loaded later, their pads should be checked by eye first.
- **Right pad and missing packages (checked by eye).** The right pad's `Uv1` gives an unmirrored image as seen from outside, as the left pad's does: the
  asymmetric `sm_raven_guard_orig` raven (head to the left in the texture) shows its head to the left on both pads, and `splorge`'s right-pad
  `sm_darkangels` winged sword stands upright (`--paint splorge`, side view; the raven check forced the decal onto both pads with a temporary patch).
  Of the right-pad decals in the 10 schemes that have one, `sm_rank_tactical_dif` and `sm_darkangel3rdtactical_dif` are not in the install (a note, nothing
  drawn), `sm_commandsquad_dif` is 256x256 but its schemes (`color_test_*`) leave `primary_colour` empty on that slot, which `ParseScheme` treats as no
  paint set for the slot (the scheme's `default_paint` applies and so does its decal, none): a gap in the test schemes only.
- **Drawing (provisional approximation).** albedo = mix(painted albedo, decal colour, decal alpha) inside 0..1, clamped; no decal normal, specular or
  dirt. The 128x128 decal has no mipmaps in the install; the client builds them (linear mipmap sampling) so the badge does not shimmer at a distance.
  A `blank` decal (five entries) means no decal and is skipped without a note. Checked on screen: Ultramarines (default), `sm_blood_angels`, `csm_alpha_legion`, `csm_black_legion`, `csm_word_bearers` on the basic Chaos set.
  Test aid `SM_DECAL_ORIENT` (0..7: flip U, flip V, swap) turns the decal.
- **Not applied.** `pattern_enabled` (no relation to decals: it is true and false with a decal, and true on 38 right pads without one; meaning unknown),
  class and rank badges (`class_badge_enabled` is a scheme flag; `sm_identifier_tactical/assault/devastator`, `sm_rank_*` decals exist but nothing maps
  a class to one), decals on slots other than the shoulders, decals on the Havok and Raptor pads.

### Weapon models: wargear attachments, mounts, stow markers and muzzles — **supported by evidence; stow scale pivot and fallbacks are interpretations** (M24, 2026-10-27)
Reader: `src/SmContent/WeaponModels.cs` (`WeaponModels`, `WeaponModel`), `ObjectTree.cs`. Findings on the install (app 55150):
- **Which model.** Each wargear file (`data/packages/wargear.oppc`, BAF) has `visual_data/attachments/<key>` with `visual_package`
  and `visual_name` (identical for every multiplayer weapon): the package `data/packages/<visual_package>.oppc` holds
  `<visual_name>.dcm`, `.O3d`, the `.bmat` materials and the full-size `_dif/_nrm/_spc/_lp` textures (the `_tsf/_tsh/_tst`
  siblings hold texture copies, `_v` the mesh again). Every class weapon's package exists (38 packages) and all their diffuse
  textures resolve. Equipment (grenades, stimpack) and the evade/sprint abilities have no attachment; the thrown and launched
  projectiles' models are the projectile blueprints' `extensions/renderable_ext` `visual_package` / `visual` in `ebps.oppc`
  (`pp_grenade_multiplayer` → `frag_grenade/geo_frag_grenade`, krak → `grenade_guardsman/pp_grenade_lodh`, launcher →
  `grenade/pp_grenade_lodh`, plasma bolts → `invisible_projectile`, an object tree without a mesh).
- **Attachment fields:** `wield_info_by_upgrade` (list of `entry` with `required_upgrade_name/required_list`, `handle_node`,
  `body_node`, `scale`; every multiplayer weapon has one entry, without requirement), `stow_info` (`handle_node`, `body_node`,
  `scale`, `hide_on_stow`), `pickup_node`/`dropped_node` (empty), `upgrades/upgrades` (`entry`: `visuals_to_show`/`visuals_to_hide`
  lists of `name` = object-tree node, `required_upgrade_name` = perk), `hide_on_death`, `bone_match_list` (empty). Worn wargear
  (`sm_mp_abil_jumppack` → `sm_jumppack_mp_basic_001`, `csm_mp_abil_jumppack` → `csm_jumpack_mp_basic`, the Devastator pistol's
  second attachment `backpack` → `sm_lascannon_backpack_001`) has empty handle and body nodes; its object tree holds a copy of the
  rig with the geometry under `RIG_SpineD`.
- **In hand** (the weapon's handle node is put on the character's body node; both are rig prop nodes): rifles, heavy weapons, the
  thunder hammer and the power mace `rig_prop_m_chest`; pistols (including the Assault/Raptor "pistol" bolters) `rig_prop_lhand`;
  one-handed melee `rig_prop_rhand`. These are skeleton bones of `havok_sm_base_mp__hkxx` (`RIG_Prop_M_Chest` under `RIG_SpineD`,
  `RIG_Prop_LHand`/`RIG_Prop_RHand` under the hands), so the animations carry the weapon. Checked by pose: in
  `SM_NAV_IdleL_S_Aim_F_01` every rifle's muzzle ends 0.8–1.9 m in front of the character pointing forward (+Y, cosine at least
  0.99); in `SM_1HR_NAV_IdleL_S_Aim_F_01` the pistol sits in the extended left hand and points forward.
- **Weapon object trees** are exported with the character's hierarchy: the handle node (e.g. `RIG_Prop_M_Chest` at the root,
  rotated -90° about Y; pistols and swords in the character's bind pose with a copy of the rig) and the `GEO_*` geometry nodes
  under it. The handle node's frame matches the skeleton bone's frame (same axes in the bind pose), so
  weapon root → bone = inverse(handle node in root space) × scale × body node's offset from its bone.
- **Blocks:** each `StaticMeshVisual` (`MeshID` = block, `RefNode` = node) is drawn as vertex × node's root-space transform, as for
  the Chaos armour (**supported by evidence**: with that rule the parts of every readable weapon fit together, e.g. the bolter's
  body, bolt, grip and barrel upgrade span the same -0.36..0.46 m; taken as stored they do not). `SkinMeshVisual` blocks (the
  chainsword's and chain axe's teeth, skinned to the weapon's own `RIG_teeth*` nodes) are already in root space in the bind
  pose. `LODLevel` is a bit mask as on the maps (31 or -1: all, 15: near, 16: the `_LODL` twins); bit 0 is drawn.
  Upgrade geometry (`GEO_upg_*` / `geo_upgrade*`: every node any upgrade shows, 46 visuals) is hidden by default; nodes that
  upgrades hide stay visible. The hidden set is gathered over all wargear drawing the same model (**interpretation**: the
  Devastator bolt pistol lists no upgrades but uses the Tactical pistol's model).
- **Stowed** (`stow_info`): rifles and heavy weapons → `mkr_stow_bolter`; pistols → `mkr_stow_pistol`; chainswords, power swords,
  thunder hammer, power mace → `mkr_stow_chainsword`; power axe, chain axe → `mkr_stow_poweraxe`; combat knives →
  `mkr_stow_combatknife` with `hide_on_stow` true. Stowed swords and axes are scaled 0.8–0.9. The markers are nodes of the
  character's object tree `spacemarine_body_default.O3d` (in `ebp_sets__mp_versus_set.oppc`; both `sm_space_marine_player` and
  `csm_chaos_marine_player` name it as `renderable_ext/visual`), each under a bone: `MKR_Stow_Bolter` under `RIG_SpineA` (on the
  back), `MKR_Stow_Pistol` under `RIG_L_Thigh`, `MKR_Stow_Chainsword` and `MKR_Stow_Poweraxe` under `RIG_Pelvis` (left hip),
  `MKR_Stow_CombatKnife` and `MKR_Stow_Powersword` under `RIG_SpineD`. The body tree's nodes match the Havok skeleton's bind pose
  (e.g. `RIG_Prop_M_Chest` at (0, 0.374, 1.666) with the same axes).
- **Muzzle:** every gun's object tree has `fx_Muzzle` (`fx_muzzle` on the heavy bolters), the marker the wargear's `fire_fx` and
  `hit_fx` effects name; the storm bolters add `fx_Muzzle01` (second barrel). Its **+Y axis points out of the barrel** on all 23
  gun models (cosine with the centre→muzzle direction 0.94–1.00). Other locators: `fx_shells_eject`, `fx_muzzleflash`,
  `fx_shellcasings`, `fx_Muzzle_plasma`, `fx_clip`, vents, `marker_collision`; melee weapons have `fx_swing`, `fx_chainsword_spray`.
- **Interpretations and gaps:** the stow `scale` is applied uniformly about the handle node; the thunder hammer and power mace
  name `rig_prop_rhand` as their stow handle but their trees have no such node (only `RIG_Prop_M_Chest`), so the model root is
  used; cables and belts (type-2 blocks) and the chain teeth are drawn rigid in the bind pose (the original simulates or animates
  them); when the original switches between in hand and stowed, and what `hide_on_stow` hides exactly, come from the behaviour
  graphs and are not decoded here; backpack weapons and jump packs are painted (see "Armour paint").

### Seize Ground and Manufactorum — **supported by evidence for the layout and times; rules partly in scripts and code** (M16, 2026-10-27)

Readers: `src/SmContent/MapContent.cs` (points, start areas), `src/SmContent/GameModes.cs` (script name check). Searched with
read-only probes for `capture`, `capturepoint`, `capture_point`, `domination`, `mkr_objective`, `seize`, `objective`.

- **Manufactorum is `mp_tdm_09`.** `data/packages/multiplayer_map.oppc` `sm_factory.attr_pc`: `map_name` `mp_tdm_09`,
  `display_name` 111921, `image_name` `mapvs9`, `loading_screen` `sm_factory_loading`. In `data/locale/english/ucs.pc` (a
  4-byte length, then zlib; UTF-16 lines of `id<TAB>text`; read in memory only), string 111921 is the map's name,
  Manufactorum. The other maps' `display_name` ids resolve the same way (not recorded here). Its region archive is
  `mp_tdm_09__wrld.oppc`, which M7/M8 already read (M7 notes open edges where players can walk off; M18 closes them, section "Map edges").
- **Seize Ground is the `domination` game type.** Each map's `map_objectives` lists `objective_game_type`
  (`teamdeathmatch`, `domination`, `ctf`, `koth`, `dreadnought`). The domination entry's `display_name` 121643 is
  the mode's name, Seize Ground; 121644 is its one-line description (not reproduced).
- **Points** (layer data): the region's `d_mission_objects.Layer` (`d_mission.Layer` on the `r000_main` maps) holds
  cylinder markers (`Shape` 3, `MarkerShape` 3) `mkr_objective_N` (or `mkr_cp_N`). Manufactorum has `mkr_objective_1` at
  (110, −785, 4) with scale 30 × 30 × 20, `_2` at (200, −800, 0) with 27 × 27 × 20, and `_3` at (290, −815, 4) with
  30 × 30 × 20. `_4` and `_5` there are 1 m spheres. The region script `mp/<map>/<region>/<region>.lua` is compiled Lua. Its
  constant pool lists the domination markers right after `GetDefaultVersusObjectives`: on Manufactorum
  `mkr_objective_1`, `_2`, `_3`, but not `_4` or `_5`. On mp_tdm_11 the list leaves out `mkr_cp_02`, a 1 m sphere. So the
  script's list is what names the points, and the loader takes the point names from the script's bytes in order.
  **Interpretations:** the cylinder's radius is half its X scale. Box markers' scale is their full size: `mkr_start_*`
  18 × 10 has `NavMeshSurfaceArea` 180. Its height is the Z scale centred on the position: on mp_dlc2_map4, mp_tdm_17 and
  mp_tdm_18 the floor under each point lies inside centre ± Z/2, and a base-at-position reading would put the floor below
  the base on mp_tdm_18. Letters A, B, C follow the script's order. The other maps have 3 or 4 points. They are noted but
  not developed (alpha scope: Manufactorum only).
- **Start areas:** `mkr_start_marine` and `mkr_Start_Ork` (the Chaos side, named after the Ork prototype). On Manufactorum
  both are 10 × 8 × 5 boxes: marines at (245, −937.4, 5.5) facing 0°, Chaos at (154.5, −664.4, 5.55) facing 180°, each
  facing the points. `util/mp/scripts/multiplayer.lua`'s constants include `SetSpawnLocation`, `mkr_start_marine`, 2,
  `mkr_start_ork`, and, under `Network_GetGameMode` `domination`, the blocker and shield marker lists
  (`MovementBlocker_SetGroupAndRaceCategories`, `blockerMarkersMarine/Chaos`). **Interpretation:** outside Annihilation's
  `RandomizeTeamSpawning`, each side spawns in its start area, and per-race movement blockers keep enemies out of it.
- **Spawn rooms on Manufactorum (M22, 2026-10-27; read-only probes of every `.Layer` in `mp_tdm_09__wrld.oppc` and the
  `util.oppc` constant pools).** Each side's room is the raised platform holding its start area plus an L-shaped corridor
  (about 8 m wide, floor z 0), sealed by that side's three `base/MissionMovementBlocker` boxes (`BlockGroup` 3): marines
  `marine_blocker_01` (260, −910.3, 12.1; 10 × 1 × 20), `_02` (230.4, −920.4, 7.2; 1 × 11 × 15), `_03` (193.6, −885.9, 2.3;
  1 × 11 × 15, turned −90°), `BlockRace` 4; Chaos `chaos_blocker_01` (140, −687.5, 10.5; 10 × 5 × 10), `_02` (170.7, −680, 11.3;
  1 × 16 × 25), `_03` (208.4, −713.4, 4.9; 1 × 16 × 25, turned 90°), `BlockRace` 3. Evidence: a 1 m flood fill over the
  character collision (steps ≤ 0.9 m, no wall at 1.0 or 1.8 m) from each start area, with the side's blockers as walls,
  reaches 1,410 cells (marines: x 190..265, y −944..−887) and 1,328 (Chaos: x 136..210, y −712..−656); without them it
  reaches 25,769 cells and point B. `e_occluders.Layer` has `base/planeoccluder`s named `planeoccluder_spawn1..3` (Chaos
  corridor) and `planeoccluder_spawn_04..06` (marine corridor) along those corridors. The region script lists the blockers
  as `blockerMarkersMarine`/`Chaos` and the shields `marine_shield_01..03` / `chaos_shield_01..03` (`base/FXObject`,
  `prop_mp_enemy_shield`); `multiplayer.lua` enables them for `domination` (`MovementBlocker_SetGroupAndRaceCategories`,
  `COLLISION_ENTITY_RACE_CATEGORY_CHAOS` / `_SM`) and opens them (`_ALL` / `_NONE`) for `koth` and `dreadnought`.
  `mkr_marine_respawn` (185, −701, 0; cylinder 7.5 m, `MarkerType` 11) lies outside both rooms, near the Chaos corridor's exit;
  its use is unknown. **Who spawns where (scripts, constant pools only):** `multiplayer.lua` OnStart calls `SetSpawnLocation`
  with `mkr_start_marine` and `mkr_start_ork` (→ `Mission_SetSpawnMarker(team, markerName)`) in every mode. Annihilation's
  objectives `TeamDeathmatchMarine`/`Chaos` then run `RandomizeTeamSpawning` (`triggerOnMissionStart`, `randomPerSpawn`,
  `locations` `tdm_mkr_spawn_01` for both teams); `Actions.lua` turns a string location into `Marker_FindList(...)`, then
  `ResetSpawnLocations` and `AddSpawnLocation` (→ `Mission_AddSpawnMarker`) per marker. CTF uses per-team lists
  (`ctf_` .. team .. `_mkr_spawn_01`); dreadnought switches between the start markers and `tdm_mkr_spawn_01`. So in the data,
  Annihilation re-spawns both sides over the same 18 `tdm_mkr_spawn_*` markers (none is inside a room: `_04` and `_03` lie
  5.5–5.8 m outside the corridors, `_05` and `_06` just outside a blocker; 07..18 are mid-map), and only Seize Ground keeps
  each side in its start area. **M22 decision (user's request):** every spawn in both modes is in the side's start area,
  facing out of the room toward its exit, the nearest blocker by walking (user's decision; spec/behavior.md; first M22 rule: the
  map's middle); the Annihilation part departs from the scripts.
- **Spawn-room blockers as walls (2026-10-27; read-only probes: the blocker objects' fields in `d_mission_objects.Layer`,
  the constant pools of `multiplayer.lua`, `cp_multiplayer.lua` and the region script, and the install's character
  collision).** Every spawn-room blocker has `BlockType` 0, `BlockGroup` 3, `BlockRace` 4 (marines) or 3 (Chaos),
  `MarkerShape` 2, `Shape` 1 (box) and no other filter field. `multiplayer.lua`'s constants under `domination` run
  `MovementBlocker_SetGroupAndRaceCategories`, `blockerMarkersMarine`, `COLLISION_ENTITY_GROUP_CATEGORY_NPC`,
  `COLLISION_ENTITY_RACE_CATEGORY_CHAOS`, `blockerMarkersChaos`, `COLLISION_ENTITY_RACE_CATEGORY_SM`, then (for `koth`,
  `dreadnought`) `COLLISION_ENTITY_GROUP_CATEGORY_ALL`, `COLLISION_ENTITY_RACE_CATEGORY_NONE`; its helper uses
  `MovementBlocker_SetGroupCategory` / `MovementBlocker_SetRaceCategory` per marker. `FXObject_SetFXNames` with
  `shieldMarkersMarine`, `prop_mp_allies_shield`, `shieldMarkersChaos`, `prop_mp_enemy_shield`, `Player_GetTeam`,
  `Player_GetLocal` picks the shield effect per local player. The shields are `base/FXObject`s `marine_shield_01..03` /
  `chaos_shield_01..03` (`prop_mp_enemy_shield` in the layer, `master_fx`, `play_weirdboy_shield_loop_mp`) next to the
  boxes. Nothing names `teamdeathmatch`. **Interpretation (simulated):** a side's boxes stop the other race's characters
  only (constants' order), in Seize Ground; in Annihilation ours (spawns are in the rooms there too). Character
  collision on Manufactorum around the boxes: several boxes span walkable surfaces inside their height (about 10–15 m in
  `chaos_blocker_01`, `_02`, `_03`, `marine_blocker_01`, `_02`); above `marine_blocker_02` (top 14.7 m) the roof is 2.6 m
  clear and above `marine_blocker_03` (top 9.8 m) 0.6–6 m, so a jump pack's ~17.5 m apex may pass over them (recorded as a
  risk in spec/behavior.md). The `d_blockers` spheres are not simulated.
- **Map middle:** the map is point-symmetric about (200, −800): point B's centre; the start areas' midpoint (199.7, −800.9);
  the tdm marker pairs 01/02 (190, −877.5 / 210, −722.5), 03/04 and 05/06 all have their midpoint there.
- **Rules** (`util.oppc`, compiled Lua 5.1; **constant pools only**: string and number constants listed in file order; the
  instruction stream was not read, decoded or run; no script text is copied into the repository).
  - `util/mp/scripts/data/objectives.lua`: the domination preset `Domination` of type `Dominate` (requirement `GameMode`
    `domination`, `ebp` `ebps\props\waaagh_banner_objective`) sits with `captureDuration` 20, `decapDuration` 10,
    `updateScoreFromDelta`, `doNotAllowZeroDelta`, `scoreSpeed` 2, `randomizeSpawnPos`, `teams` 1. Other modes repeat the
    pattern, for example dreadnought with `captureDuration` 90. `presetObjectives` names `Domination` and
    `DominationTimer`, plus `DominationEnd`; the `TeamDeathmatch*` `Deaths` objectives are separate.
  - `objectives/dominate.lua`: defaults `captureDuration` 20, `decapDuration` 10, `scoreSpeed` 1.
    `Network_AddCapturePoint`, `Event_AddPointCaptured`, `DominatePointCaptured`, `owningTeam`, 100, `DominateUpdate`.
    Actions `defend`, `capture`, `neutral`, `Network_GetScoreLimit`, `capturedPoints`, `sort`, 2,
    `Hud_ShowObjectiveDominationScore`, `Complete`, `Draw`.
  - **Interpretations:** a side alone on a neutral point owns it after `captureDuration` s. A side alone on the other
    side's point makes it neutral after `decapDuration` s. The presence and contest rules run in the executable
    (`Network_AddCapturePoint`), so per-player rates, decay and contest handling are not in the data. Each owned point
    scores `scoreSpeed` per update. The update period is not established: the 100 near the timer call is not used.
    `updateScoreFromDelta` and `doNotAllowZeroDelta` hold boolean values, which a constant pool does not hold, so their
    effect is unknown. Deaths do not score team points, because the preset has no `Deaths` objective.
  - `smserver` checks that 10 names are present: 6 in `objectives.lua` and 4 in `dominate.lua`. 10/10 on this install.
- **Point entity:** `ebps.oppc` `waaagh_banner_objective` (domination's `ebp`) has `decorator_type`
  `domination_point_decorator`, visual `wc_capture_point`, and actions `capture` (fx `mp_defend`), `neutral` (`mp_neutral`)
  and `defend` (`mp_captured`), all at scale 2.5. It has no radius or timing.
  `waaagh_banner_interactable_hold` (`progress_while_button_held` 0.3, hold to capture) and `coop_capturepoint` belong to
  other modes.
- **Host options:** `HostOptions.gfx` has `domination_score_limit_btn` and `Native_SetDominationScoreLimitArray`. The
  values come from the executable, so the score limit is provisional.
- `stat.oppc` `mp_pvp_deaths_while_capping` (domination) is a statistic, not a rule.

### Map edges: blocker panels and the kill region — **supported by evidence for the data; roles are interpretations** (M18, 2026-10-27)

Readers: `src/SmContent/MapContent.cs` (`CharacterLayers`, `KillRegions`); simulation: `CollisionWorld.KillsPlayer`,
`World.Step`. Searched every `.Layer` of `mp_tdm_09__wrld.oppc` (15 layers; `d_navmesh_a` does not parse yet, BOD value
type 0xFF) with a read-only probe for struct classes, and walked a simulated player (sprint, 30 s) from the 18 spawns,
2 start areas and 3 points in 24 directions each (552 runs).

- **Open edges come from layer "2" alone.** With characters on layer "2", 82 of 552 runs fall below z −9.5: off the
  outer edges (e.g. x ≈ 105–130 west of point A) and into the pits round point B and to the south (floors at −10, −13.4
  and holes). At 72 of those 82 places a horizontal ray meets a **layer "8" panel that layer "2" lacks**: axis-aligned,
  vertical, thin, e.g. x = 115.0 from y −747.5 to −698.4 and z 4 to 100 (outer edge), x = 219.8 / 180.0 and
  y = −809.7 / −829.9, z 0 to 3 (railings round the central pits). Layers "7" (2,006 triangles) and "12" (33,311) add
  none at those places (12 meets 22, all also in 8). With characters on "2" + "8", 0 of 552 runs fall, and the number
  of runs that end within 3 m of their start barely changes (48 → 50; mean distance 28 → 34 m).
  **Interpretation:** characters collide with layers "2" and "8" together; shots keep layer "8". Layer "8" was read as
  the shot layer in M7; that it also holds the character blockers is what the edges show, not a decoded filter.
- **Kill region:** `d_mission_objects` holds one `base/KillRegion` (`Name` `KillRegion`, `Position` (200, −800, −10),
  `Scale` 250 × 250 × 1, `Shape` 0, `Enable` true, `Exclude` false, `OneShot` false, `BodyType` 11). It lies under the
  whole play area just below the pit floors, so (interpretation, from its position) falling into a pit kills in the original. Read as a box whose scale is
  its full size (as the M16 boxes); a living player dies when the feet's path in a tick, or the body's span at its end,
  meets it. Jump-pack players can still clear the 3 m railings and die in it. `SimConfig.KillDepth` stays as the
  fallback.
- Other blockers, not used: `d_blockers` has two `base/MissionMovementBlocker` spheres (`Shape` 2, 9.3 m, `BlockGroup` 2,
  `BlockRace` 1) at z ≈ 15 over the middle; `d_mission_objects` has the per-race start-area blockers (`marine_blocker_01..03`
  `BlockRace` 4, `chaos_blocker_01..03` `BlockRace` 3; boxes, `BlockGroup` 3) that the script switches under
  `domination`. Neither is at an edge; which race each blocks is not established. `d_ceiling` is empty.
- The map hash covers it all (collision and mission-layer bytes, the role string, `LoaderVersion` 3).

### Map placements and object definitions (`StaticObject`, `.O3d`) — **supported by evidence** (M8)

Reader: `src/SmContent/MapScene.cs`.
- The region's `e_*` layers (and some `d_*`) hold `StaticObject`s: `ObjectName` (an `.O3d` definition, matched ignoring
  case), `PackageName`, `Position`, `Rotation` (degrees), `Scale`, `Disabled`, `ChildObjects`, collision switches
  (`DisableCollision`, `DisableBulletCollision`, …). mp_dlc2_map4: 2,199 enabled placements of 149 definitions. Every
  definition a placement names is in the region archive itself on 10 of the 11 maps (on mp_dlc2_map1, 8 placements name
  definitions that are not there). Drawable placements: 756 of 792 (mp_tdm_18) to all (mp_dlc2_map3, mp_tdm_01, mp_tdm_17).
- **Rotation order**: degrees about X, then Y, then Z, about fixed axes (quaternion qz·qy·qx). Established on two
  placements with tilted rotations whose collision pieces store exact quaternions, (180, −90, −180) and (−90, 0, 90);
  of the six orders only this one fits both.
- `.O3d` (`Object3D`): `Skeleton` (a `Skeleton3D` tree of `Node3D`s, each with `Position`/`Rotation`/`Scale` in the same
  convention), `Visuals` (`StaticMeshVisual`: `MeshName` = a `.dcm`, `MeshID` = index into its blocks, `RefNode` = the
  node it hangs on, `LODLevel` bit mask, `CastShadows`, `Renderable`), and
  `Bodies` (`RigidBody` with `NodeName` and a `CShapeMesh` naming the collision mesh).
- **Placement check against the level collision**: composing a rigid body's node transform with its placement gives the
  translation of a collision piece within 1 cm, and then always its rotation too (|dot| > 0.999): mp_dlc2_map4 1,987 of
  1,990 bodies, mp_dlc1_map1 1,496 of 1,585, mp_dlc2_map1 1,656 of 1,660; lower on mp_tdm_09 (1,312 of 1,946) and
  mp_tdm_18 (374 of 604), where the collision was presumably baked from other or merged data (not examined).
- **LOD masks** (1,457 definitions on the 11 maps): most have one mask (31 ×855, −1 ×216, 15 ×145). Where a definition
  splits detail levels into complementary masks, the high-detail node has the low bits ({15 LODH, 16 LODL} ×21,
  {7 LODH, 24 lod} ×6), so bit 0 is the nearest LOD. Others overlap: {31 LODH, 15 LODL} ×41 and {31 LODH, 7 LODL} ×79
  both include bit 0 in the LODL part. The client draws bit-0 parts and drops low-LOD-named nodes when a high one
  exists (**provisional approximation**; how the engine resolves overlaps is unknown). With this rule no placed
  definition on the 11 maps is left with nothing to draw; up to 49 definitions draw more than one block of the same
  mesh up close with "lod" in a node name (an upper bound for overlapping LODs; many are separate detail parts). A
  visual without a `LODLevel` counts as every LOD.
- **Scale and composition**: rigid-body node transforms composed with placements (scale, then rotation, then
  translation) also reproduce the collision pieces' scale vectors, including for non-uniformly scaled placements
  (e.g. mp_dlc2_map4: 498 of 500 non-uniform, 1,487 of 1,490 uniform). No multiplayer map nests objects in
  `ChildObjects`; placing children relative to their parent's full transform is an **assumption**, untested on data.
  The lower match rates on mp_tdm_09 and mp_tdm_18 come from top-level objects (not examined further).
- No multiplayer map has a mirrored placement (negative determinant).
- `MeshID` is within the `.dcm`'s block count for every visual of all 11 maps. A few meshes use the undecoded block
  type 2 (e.g. banners, mechanicus props) and are not drawn.

### Effects: PSystems, the atlas page, effect commands and material effect containers — **supported by evidence for the layouts; renderer semantics interpreted** (M25a, 2026-10-27)

Readers: `src/SmFormats/Fx/FxSystem.cs`, `FxTexturePage.cs`; catalogue: `src/SmContent/EffectCatalogue.cs` (`FxBinding`,
`WeaponEffects`, `ShotEffects`, `ThrownEffects`, `ProjectileEffects`, `MaterialEffectTable`, `Surfaces`, `CharacterSurface`,
`EffectCatalogue`, `FxLibrary`, `MapEffects`). Gameplay-facing
values: spec/behavior.md "Weapon effects and shot events".

- **`master_fx.oppc`** (`data/packages`): 1,176 `PSystem` entries, 55 `dds` (the atlas `master_fx` 4096 × 4096 DXT5 and 54
  loose decal/aux textures), one `tpage`, the `shell_casing` and `light_alarm` meshes. Every PSystem parses with
  `BodDocument` (uncompressed BOD v2) and `FxSystem.Read`: 0 failures, no description class outside the nine below, the
  deepest description tree 11 levels (the reader stops at 16 and notes it).
- **PSystem tree**: `PSystem { Name, BackgroundColor[3], RefObject, RefAnimation, Emitters, SortOverride, GlobalSortOverride }`;
  `Emitters` is a keyless list of `07` references to `FXEmitter { Name, Hide, Screenspace, ScreenspaceX/YPosition,
  LODDistanceMedium/Far, SmoothMoveDistance, RefNode, FXDescriptions }`. Descriptions (counts over the 1,176):
  `FXRingDescription` 6,893 (sprite quad), `FXSprayDescription` 3,515 (emitter; its `FXDescriptions` are each particle's
  look; `BurstList` of `FXSprayBurst { NumParticles, BurstTime }`), `FXComboDescription` 3,208 (group; `OneAtRandom`),
  `FXBeamDescription` 1,952 (ribbon), `FXLightDescription` 566, `FXAudioDescription` 325, `FXStreamerDescription` 512,
  `FXScreenSpaceDecalDescription` 221, `FXMeshDescription` 23. Shared fields: `Name`, `Duration`, `DurationEpsilon`,
  `IsLooping`, `FXLOD`, `Hide`, `Environment` (list of `FXSimpleEnvironment { Gravity, Wind, Attraction, Drag, Bounce,
  Position, Orientation, CollisionResponse }`), `FXCollisionDescriptions`. Decals spell their height `Height` or `height`.
  **Collision descriptions (typed in M41, `FxElement.OnCollision`)**: 58 systems have a non-empty `FXCollisionDescriptions` (a list of the same description
  structs as `FXDescriptions`) on an element whose particle has a `CollisionResponse` (1, 3 or 6 on the install: 3 blood and body effects, 6 the "dropper"). The ground
  effects `jumppack_land_heavy` / `_normal` / `_extreme_mp` / `_death_mp`, the frag and krak grenade explosions, `warboss_pound` and the jump pack thrusts are one spray
  "dropper - use debug geo" (1 particle, gravity 100, response 6) with one child ring "collider aligns with ground" whose collision descriptions are the whole impact
  (decals, flash, dust, debris, ground sprites, light). The same-depth reading still finds no description class outside the nine (0 systems with notes of 1,176).
- **Curves**: every `Frame*` field (and `Length`, `Colour`, `Scale`, `Noise`, `Arc*`, `EmitFrame*`, `Particle*Frames`) is a
  `09` list with flag 0 whose items are plain `0B` arrays (no `07` reference): `[f32 time, f32 value]`, or for colours
  `[f32 time, [0D, 0D, 0D, 0D]]` with components 0..255 as 8-byte integers. One key is a constant. Reading the time as a
  fraction of the element's duration and interpolating linearly is an **interpretation** (`FxCurve.Evaluate`).
- **Materials**: `MaterialName` is a BOD string; its 64-bit hash (`BodString.Hash`) is the atlas key. `AdditiveBlend` 0/1/2
  (2: distortion materials, interpretation), `SoftParticle`, `TileX`/`TileY` sprite-sheet cells, `RandomTile`, `UAddressMode`.
- **`master_fx.tpage`** (7,368 bytes): `u32 count` (230), `u32` 0, then `count ×` 32 bytes: `u64` name hash, `f32` width,
  height, x, y (fractions of the atlas; top-left origin), `u32` flags (bit 0: stored rotated 90°; 63 rects; the direction
  of the turn is not established), `u32` 0. 229 of 230 hashes are found in the PSystems' string tables; 4 names the
  PSystems use have no rect (`power_shield`, `rifle_tracer_00`, `sky_beam_thick_alpha`, `sticky_tar_dif`; loose `dds`
  entries exist for the last, so non-atlas materials may load a texture of that name: interpretation). The atlas texture
  is the `dds` entry with the page's name.
- **Effect commands** (BAF, `wargear.oppc`, `ebps.oppc`): tables whose `$ref` is `action_commands\play_fx` (`fx_package`,
  `fx_name`, `scale`, `length`, `attach_to_marker`, `trigger_point`, `marker/marker`, `trigger_point_target`,
  `marker_target/marker`, `rotation_source`, `trigger_direction`, `scale_if_from_player`, `on_stop_policy`, `lifetime`,
  `apply_only_if_on_player`) or `action_commands\trigger_material_effect` (`effect_container` path, `material_source`).
  They sit in event tables (`$ref wargear\fx_action_filter`: `actions/<command>` and
  `actions_by_upgrade/entry/{required_upgrade_name/required_list, action/commands/<command>}`), in `*_action/commands`
  tables (`lifetime_action`, `detonate_action`, `charging_action`, `recharging_action`), and in heat and vent actions.
  The basic shot's delivery is the ability's `delivery`, or for the melta its `pump_states/<state>/delivery`; charge
  stages have `charge_states/<n.name>/delivery`; the lascannon's tracers are in `delivery/penetration/entry/
  initial_tracer_fx` and `subsequent_tracer_fx`. Projectile blueprints (`ranged_delivery_type/entry/type/ebp`) carry
  `extensions/entity_actions_ext/lifetime_action` (trail) and `extensions/projectile_ext/detonate_action`.
- **`material_effect_container.oppc`** (104 BAF files, base names only; 12 names repeat as Space Marine / Chaos pairs):
  `sfx` (`post_sound_event`: `start_event_name`, `use_terrain_material`) and `fx/<surface>/<damage class>/play_fx…`.
  Surfaces appear in one order in every table, also in global's `extensions/fx_environment_manager_ext/collision_fx/
  collision_small/material_table/fx/default`: default, dirt, gravel, stone, metal, armour, flesh, air, energy_shield, sand,
  ceramite_armour, broken_ceramite (the level ids index this order: interpretation, see "Level collision").
- **Player surface**: ebps `sm_space_marine_player` `extensions/entity_ext`: `material_audio_visual` "broken_ceramite",
  `material_simulation` "default_body", `material_overrides/entry { hit_zone, material_simulation, material_audio_visual }`
  (head: default_head; body, arms, legs: default_body).
- **Map fx objects**: `base/fxobject { FXName, FXPackage, FXLength, FXScale, FXTargetMarker, FXAudioEventName, FXAutoTrigger,
  FXPreComputeTime, Actions, Marker*, Position, Rotation (degrees, X then Y then Z), Scale, Shape, ObjectID, Name,
  EventGroupID, LightGroup, Disabled, ChildObjects }` in the region's layers (Manufactorum: `l_lights`, `e_fx`,
  `e_adm_dressing`, `d_mission_objects`; 163 in all; `d_navmesh_a` does not parse, type `FF`).

### BOD binary object data — **supported by evidence for materials** (M2)

Reader: `src/SmFormats/Bod/BodDocument.cs`; material view: `BmatMaterial`. Verified on 1,688 of 1,688 `.bmat` files in the
multiplayer maps (parsed to the last byte).

- Header: `"BOD" 0xFD`, `u8 version` (2), `u8 compressed` (0 for `bmat`; 1 for `O3d`, `Layer`, `world`, `region`), `u32` (1).
- Uncompressed body: `u32 stringCount`, `u32` (stringCount − 1 in samples), then `stringCount ×` { `u64 hash`, `u32 len`, ASCII }.
  Everything after refers to strings by hash; hash 0 is the empty string. The hash function is unknown and not needed.
- Value tree, each value starting with a type byte: `01` struct (`u64 class`, `u32 n`, then `n ×` { `u64 name`, typed value });
  `02` i32; `03` f32; `0F` string hash; `05` key (string hash); `0B` array (`u32 n`, `n` typed values);
  `07` reference (`u32` position, then a typed value); `09` list (`u32 n`, `u8` flag, then `n` items: with flag 1 a key
  and a `07` reference, with flag 0 just the reference). M7 added `00` (no payload), `04` (one byte, bool) and `0D`
  (eight bytes, identifiers). With these, every `bmat`, `O3d`, `object-manifest`, `PSystem`, `region` and `ssdecal`
  file and 2,612 of 2,816 `Layer` files in the install parse to the last byte; the rest (navmesh and elevator layers)
  and the 24 `world` files use types `FE`/`FF`, not decoded yet.
- A material is `struct Material { Name, ShaderName, LightingModel, BlendMode, CullMode, DrawOrder, SortBias, Parameters }`.
  `Parameters` lists `TextureParameter { Value = texture name, PackageName, U/V/WAddressMode, Name }`, `FloatParameter`,
  and `Vector4Parameter` items.
- Links: every material a multiplayer-map mesh names exists as a `bmat` in the same archive (5,550 of 5,550), and every
  `DiffuseMap` names a `dds` in the same archive (1,661 of 1,661).
- Shaders are named by feature, e.g. `diff_vtxtint_norm_drvspec` (406), `diff_norm_drvspec` (258),
  `dif2_advvtxbld_vtxtint_norm2_drvspec` (235). The client does not reproduce them yet.

### Audio (Wwise) — **supported by evidence for the readers and the decoder** (M26a, 2026-10-27)

Readers: `src/SmFormats/Audio/` (`AkpkPackage`, `SoundBank` + `Hirc.cs`, `WwiseLibrary`, `Fnv`, `WwiseVorbis`,
`OggWriter`). Tool: `smtool audio-list | audio-event | audio-decode | audio-verify`. Research and the event mapping
(BAF `post_sound_event` → event names) are in the M25 audio research; this section records the layouts as verified by
our readers. Field names follow wwiser's documentation (no licence: names only, no code); every layout was re-checked
on the install.

- **Packages** (`streamingaudio/*.pck`, magic `AKPK`): `u32 headerSize` (bytes after offset 8), `u32 version` (1),
  `u32 languageMapSize`, `u32 bankLutSize`, `u32 streamLutSize` (no external LUT in this game). Language map:
  `u32 count`, `{u32 nameOffset (from the map start), u32 id}`, UTF-16LE names (sfx 0, english(us) 1, …). Each LUT:
  `u32 count`, entries of `(size − 4) / count` bytes; here **24 bytes** `{u32 fileId, u32 blockSize, u64 fileSize,
  u32 startBlock, u32 languageId}`, offset = startBlock × blockSize (the reader also accepts the older 20-byte entry).
  All 230 packages (root and language folders) open; each root package holds one bank whose ID is FNV-1 of the
  package name.
- **Banks**: chunks `{tag, u32 size}`; `BKHD {u32 version = 56, u32 bankId, u32 languageId, u32 feedbackInBank, …}`;
  `DIDX` 12-byte `{wemId, offset in DATA, size}`; `DATA`; `STID {u32 type, u32 count, {u32 bankId, u8 len, name}}`;
  `HIRC {u32 count, {u8 type, u32 size (includes the ID), u32 id, payload}}`. The reader refuses other versions.
- **HIRC objects decoded** (each must consume exactly its size or it is kept raw with the reason): Event, Action, Sound,
  Random/Sequence, Switch, Layer, Actor-mixer. **All 214,097 such objects in the 230 packages parse exactly, 0
  failures** (`smtool audio-verify`). Kept raw for later: State, Bus, music objects, Attenuation, DialogueEvent,
  feedback objects, FxShareSet, FxCustom.
  - Event: `u32 n, n × actionId`.
  - Action: `u32 actionType, u32 targetId, s32 delay, s32 delayMin, s32 delayMax, u32 subsectionSize`, then
    type-specific parameters (kept raw; Play's end with a bank ID: in In_Game_Default 2,145 of 3,055 Play actions name
    that bank, the rest were not checked). Types seen (all banks): `0x4011` Play ×25,160, `0x1010/0x1011` Stop ×2,941,
    `0xA010/0xA011` SetVolume, `0xB01x` ResetVolume, `0x8010/0x8011` SetPitch, `0x9011` ResetPitch, `0x12020` SetState,
    and smaller counts of `0x2xxx`/`0x3xxx` (pause/resume, interpretation), `0x6xxx`, `0x7xxx`, `0xCxxx`–`0xFxxx`.
    The high bits give the kind, the low byte the scope (interpretation). Delays are milliseconds (interpretation; the
    bolter's 0/100/200 fit).
  - Sound: `u32 pluginId (0x00040001 Vorbis), u32 streamType (0 bank, 1 streamed, 2 prefetch), u32 sourceId (= wem ID),
    u32 fileId, [u32 offset, u32 size unless streamed], u8 sourceBits, [u32 size + params for plug-in types 2 and 5],
    node base, s16 loop, s16 loopMin, s16 loopMax`.
  - Node base: `u8 overrideFx, u8 numFx, [u8 bypass, numFx × {u8 index, u32 fxId, u8 shareSet, u8 rendered}],
    u32 outputBus, u32 parentId, u8 priority, u8 priorityOverride, u8 priorityApplyDistance, s8 distanceOffset,
    12 × f32 (volume, LFE, pitch in cents, LPF; each value/min/max)`, positioning `u8 override; if set: s32 centre %,
    f32 panRL, f32 panFR, u8 is3D; 2D: u8 pannerEnabled; 3D: u32 positionType, u32 attenuationId, u8 spatialized, then
    for type 2 a path block (u32 mode, u8 loop, s32 transition, u8 followOrientation, u32 n × 16-byte vertices,
    u32 m × 8-byte items, m × 8-byte ranges), for type 3 u8 dynamic`, advanced settings (9 bytes: virtual queue, kill
    newest, use virtual, u16 max instances, global limit, below threshold, 2 override flags), state groups
    `u32 n, {u32 group, u8 sync, u16 m, m × 8 bytes}`, RTPCs `u16 n, {u32 rtpc, u32 param, u32 curve, u8 scaling,
    u16 points, points × 12 bytes}`, and when BKHD's feedback flag is set `u32 feedbackBus [+ 6 × f32 if non-zero]`.
  - Random/Sequence: node base, `u16 loop, 3 × f32 transition time, u16 avoidRepeat, u8 transitionMode,
    u8 randomMode, u8 mode (0 random), 5 × u8 flags`, children `u32 n, ids`, playlist `u16 n, {u32 id, u8 weight}`.
  - Switch: node base, **`u32 groupType`** (the research read a byte; the u32 makes every object parse exactly),
    `u32 groupId, u32 defaultSwitch, u8 continuous`, children, `u32 n, {u32 switchId, u32 m, m × nodeId}`,
    `u32 p, p × 18 bytes {u32 node, u8 firstOnly, u8 continuePlayback, u32 onSwitchMode, s32 fadeOut, s32 fadeIn}`.
  - Layer: node base, children, `u32 n, {u32 layerId, RTPC block, u32 rtpcId, f32 crossfadeDefault,
    u32 m, {u32 childId, u32 points, points × 12 bytes}}`. Actor-mixer: node base, children.
- **Event resolution** (`WwiseLibrary`): objects deduplicated by ID across the loaded banks (first loaded wins; copies
  are identical apart from media placement), Play targets followed through every switch case. Example:
  `play_sm_bolter_fire` → 6 actions (delays 0, 200, 200, 200, 0, 100) → 44 unique wems over both cases of switch group
  `0x0d500bea`; `play_bolter_hits` 66 wems; `play_sm_footfalls_normal` 60; `play_sm_jump_pack_launch` 4.
  Containers are shared between switch cases, so a resolve memoises finished subtrees (the chain is a DAG) and caps
  the nodes it creates (`MaxChainNodes` 20,000) besides the depth limit (32) and cycle check.
- **Memory**: `WwiseLibrary` loads banks without their DATA chunk and reads media from the package on demand. For the
  default multiplayer set (15 packages + 19 weapon packages) that is 16 MB managed / 59 MB peak working set, against
  168 MB / 200 MB with the media in memory (measured 2026-10-27).
- **Prefetch media**: a bank's DIDX copy of a prefetch sound holds only the first bytes (its RIFF size exceeds the
  copy); the full file is in the package's stream table. `WwiseLibrary` uses the stream in that case.
- **Wwise Vorbis** (`.wem`): RIFF, `fmt ` size 0x42 (codec 0xFFFF, extra 0x30) with the vorb fields at fmt + 0x18:
  `u32 sampleCount, u32 modSignal, …, u32 setupOffset (+0x10), u32 firstAudioOffset (+0x14), …, u32 uid (+0x24),
  u8 blocksize0Exp, u8 blocksize1Exp`; 2-byte packet headers (size only); modified packets (no type bit, no window
  flags). Setup packets name external codebooks (10-bit IDs) from ww2ogg's standard `packed_codebooks.bin` (not the
  aoTuV one): **confirmed by decoding** — see the counts below. That library is **not distributed** (its codebooks
  come from Wwise; licence caution): `SmContent.AudioCache` downloads it on first use from ww2ogg's repository at
  commit `4b4ca8e` (tag 0.24) into `%LOCALAPPDATA%\sm-reimpl\cache\audio-codebooks\` and keeps it only with SHA-256
  `442e9442…5fd592`; offline or on a mismatch the `audio-*` commands stop with a clear message.
  `WwiseVorbis` is a port of ww2ogg 0.24 (BSD-3, `THIRD_PARTY_NOTICES.md`); `OggWriter` pages the packets with granule
  positions computed from the block sizes (libvorbis convention: each packet after the first adds
  prevBlock/4 + curBlock/4; the last non-empty packet, and empty packets after it, are clamped to the header's sample
  count when that cut is smaller than both that increment and half the last block, so every decoder can apply it;
  otherwise the stream keeps a few extra tail samples). NVorbis loops forever on an end cut larger than its own count
  for the last packet (seen on one install wem before this rule). A full setup (ww2ogg's option, not used by this
  game) takes granules from 6-byte packet headers and is refused with 2-byte ones. Block-size exponents must be
  6..13 with blocksize0 ≤ blocksize1.
- **Decoding check**: every wem of the 205 root packages (27,361 files, 2.3 GB: complete embedded copies plus all
  streamed files; prefetch stubs skipped) converts with the standard codebook library, **0 failures**, all with
  codec 0xFFFF, fmt 0x42 and modified packets. Every 25th file per package (1,189) was fully decoded with NVorbis:
  channels and rate match, and the decoded length equals the header's sample count within the tolerance below.
  `smtool audio-decode` of `play_sm_bolter_fire`, `play_bolter_hits`, `play_sm_footfalls_normal` and
  `play_sm_jump_pack_launch` (174 wems) gives exactly the header lengths; a bolter shot's level envelope rises at once
  and decays to silence over 0.8 s, as a gunshot should (listening by the owner pending). Language folders not scanned.
  NVorbis 0.10.5 (used only for the check and WAV output) keeps up to (2048 − 256)/4 = 448 extra samples at the end
  when the first audio page ends on a long block followed by a short one (it places its start position at the end of
  that packet's valid region instead of the window centre); `smtool audio-decode` cuts the PCM at the header's sample
  count in that case. Godot 4 decodes Ogg Vorbis with libvorbis, which follows the window-centre convention (from
  Godot's third-party sources; playback in the client is not tested yet).

### Audio playback evidence (M26b, 2026-10-27)

- **Attenuation** (HIRC 14): `u8 coneEnabled` (+ 4 × f32 with a cone), `5 × s8 curveToUse` (dry volume, wet volume, LFE,
  LPF, spread; −1 none), `u8 curveCount`, curves `{u8 scaling, u16 n, n × {f32 x, f32 y, u32 interpolation}}`, then RTPCs
  (not read). All 231 attenuation objects of the MP banks parse with this layout (`WwiseAttenuation`); the research's
  "313 of 1,539" counted every bank and required the RTPC tail to fit.
- **Bus** (HIRC 8, kept raw): read as `u32 parentBus, f32 a, f32 b, …`: 164 of the 165 buses name another bus as parent, one
  (the master) has none; volumes −34 … 0 dB. **M33 correction**: the master has the same layout (its −7 dB sits in `b`, not
  `a`, which is why it looked different): 84 buses use `a`, 3 use `b` (the master −7, a group −9.3 and one −6), never both;
  read as voice volume and bus volume and added (`AudioCatalog.BusChain`). The `0x3F` byte at offset 25 of every bus, the
  zero floats at 12 and 16 and the effect/RTPC tail are not read.
- **Action parameters**: SetState (0x12020) ends with `u32 stateGroup, u32 state` (`set_state_impact_armor` names group
  0x6f0f7f7c). 0xE010 / 0xF010 (the region events `play_mp_tdm_09_rolloff` / `rolloffreset` and the bed's third action) are
  Set/Reset LPF (M33: done; see the M33 notes below).
- **Switch group 0x0d500bea** (name unknown): under its 207 switch containers the 0x0d500bea case reaches 1,558 sound objects,
  81% of them non-positioned; the `default` case 1,261, 98.5% positioned with attenuation. Read as the local player's
  variant (2D, richer) versus everyone else's (3D): interpretation supported by evidence (`smtool audio-coverage`).
- **Continuous containers**: transition times are milliseconds (ambience: trigger rate 4500 ± 3000; emitters: power
  cross-fade 250–500).
- **Map audio** (`mp_tdm_09__wrld`): `base/FXObject` with `FXAudioEventName` (48, some with names that are no event: `er`, `rer`),
  `base/AudioEventTriggerRegion` (`EnterRegionEvent`, `ExitRegionEvent`, Scale as box size), `base/AudioEnvironmentRegion`
  (`EnvironmentEffectName` = an Init FxShareSet, `EnvironmentEffectAmount` 0.3).
- **Trigger coverage**: 238 named events from the MP files and layers; 197 reach media, 27 are stop/state/LPF-only, 14 are not
  in the MP banks (list in spec/behavior.md, M26b). MP voice barks: none by event name; found in M33 as speech commands (below).
- **QA follow-up (fix/sound-tags, 2026-10-28)**: the behaviour tags crossed without a sound (`ACTN_Take_Off`, `ANTIC_RushAttack`,
  `ANTIC_Y3`, `ACTN_SND_CK_Draw`) are keys of the MP files' `triggered_actions` whose commands hold no `post_sound_event`;
  `EVT_Gear_Move` is bound only in the singleplayer blueprints (`vanguard`, `vanguard_fe` → `play_sm_gear_move_01`); `fx_jump`
  is named by no file. The map strings `er` / `rer` are the layer's own. The map effects `oob_smoke_plume` (layer `e_fx`) and
  `amb_fire_small_dark` (`amb_fire_small_dark_02`, package `master_fx`) exist in no archive of the install under that name
  (searched every `.oppc` entry; nearest: `oob_smoke_plume_b`, `oob_smoke_plume_dark_warm`, `amb_fire_small`), so they are
  not shown; no substitute is chosen.

### Inner formats — first evidence (M1)

| Type | What we know | Status | Source |
|---|---|---|---|
| `.dcm` | Static and skinned block layouts decoded (sections above); one type-2 variant (M24); other type-2 layouts and some field roles open | Supported by evidence (types 0 and 1, type 2 with the 16-byte trailer) | [Steam thread "Model Ripping"](https://steamcommunity.com/app/55150/discussions/0/405693392914771644/) |
| `.o3d` | per-object file "needed in-game"; one claim it is encrypted | Reported, conflicting | same thread |
| BOD (`.bmat`, `.o3d`, `.object-manifest`) | "Binary object data" with a hashed string table. Both forms decoded (sections above); every `.bmat`, `.O3d`, `.object-manifest` and most `.Layer` files parse fully | Supported by evidence (parsing); meaning per file type only where noted | [smaugmented/smbod README](https://github.com/smaugmented/smbod) |
| `attr_pc` (BAF) | attribute/tuning tree; decoded (section above) | Supported by evidence | [Cope's BAF-XML Converter (ModDB)](https://www.moddb.com/games/warhammer-40000-space-marine/downloads/copes-baf-xml-converter), [Steam thread](https://steamcommunity.com/app/55150/discussions/0/3003299213523098889/) |
| `ucs.pc` | localized text "id\ttext". The installed file is zlib-compressed after a u32 (section 1) | Reported; compression Verified-local | [XeNTaX thread 7333](https://github.com/XeNTaXBackup/XeNTaXBackup.github.io) |
| `.usm` | video (CRI Sofdec) | Reported | same |
| `.pck` | audio packages; `AKPK` magic = Wwise file package; banks, HIRC and Wwise Vorbis decoded (section "Audio (Wwise)") | Supported by evidence (M26a) | same |
| textures | `dds` entries are standard DDS (DXT1 sample: header and size match exactly) | Verified-local (one sample; all 45,841 start with `DDS `) | Steam thread |
| skeletons, animations, animation events | Havok 2010 packfiles per character: skeleton, spline animations, behavior graphs (section above) | Supported by evidence (skeleton, animation); sequencer timing tags decoded (M9); state machines, transitions, blends, bone masks, blend hints (M27) | M3, M9, M27 |
| levels | `world`, `region`, `Layer` (BOD), `nav` (`NAVARRY`), `hkxx` collision, Lua 5.1 bytecode scripts | Verified-local (types and magics only) | M1 report |

`attr_pc` holds entity blueprints and wargear: movement and health are read since M5. Weapon files (`wargear.oppc`,
`sm_mp_*` / `csm_mp_*`) are fully expanded trees: `ability_data/ability_definitions/<ability>/delivery/...` holds
damage, cooldowns, range, fall-off and spread references; `common_data` holds slot, ammunition and unlock level;
perks select alternative entries by upgrade name. Values are summarised in `spec/behavior.md`.

## 3. Tool compatibility

| Tool | Supports this game? | Reads or documents? | Asset types | License | Verified locally? |
|---|---|---|---|---|---|
| aluigi QuickBMS script v0.2 | Yes, written for it | Documents archive layout | OPPC only | QuickBMS is GPL-2.0; script license unstated | No |
| GameExtractor plugin OPPC_OBPK | Yes (claims) | Readable source | OPPC only | GPL-2.0+ | Layout checked against real files: name-table model is wrong for 1,140 of 2,736 archives (no deduplication), the rest matches |
| Santos SMTools / SMOPPC v0.2.0, 3ds Max DCM scripts | Yes | Extract only; MaxScript importers may document DCM | OPPC, DCM | Unstated; not downloaded | No |
| Cope's BAF-XML Converter | Yes; reported working on Anniversary Edition | Converts; format not documented | attr_pc | Unstated | No |
| smaugmented/smbod | Yes | Python wrapper around **decompiled** Santos tool | BOD | No license | Do not reuse code |
| Relic-Tool-SGA (ModernMAK) | **No**. SGA archives (DoW/CoH); this game does not use SGA | — | — | GPL-3.0 | n/a |
| PredatorCZ/HavokLib, HavokMax | Generic Havok readers (not game-specific) | Source available | Havok packfiles, skeletons, animations | GPL-3.0 | Not run. Its rotation encodings were recalled when writing our decoder (see "Havok packfiles") |
| hcs64/ww2ogg 0.24 | Generic Wwise Vorbis converter | Source | `.wem` (Vorbis) | BSD-3-Clause (codebook library: licence unclear, downloaded on use, not distributed) | Ported to C# (`WwiseVorbis`); notices in `THIRD_PARTY_NOTICES.md`; checked by decoding the install's wems (M26a) |
| NVorbis 0.10.5 | Generic Vorbis decoder | Library (NuGet) | Ogg Vorbis | MIT | Used by smtool and tests to check our Ogg output |
| bnnm/wwiser | Generic Wwise bank parser | Documents HIRC layouts | `.bnk`, `.pck` | **No licence** | Field names consulted only; no code used |

Conclusion: archive access is well documented. The game's own mesh (`dcm`), material (BOD) and level formats have
**no open documentation**; we decoded them from the bytes (M2, M3). Skeletons and animations are Havok 2010 packfiles, a
generic engine format with community readers; our reader was checked against the game's files.

## 4. Reproducing these findings

- Research downloads were kept outside the repo. Re-fetch the references above to re-check.
- `m1.cmd` runs everything below in one go and writes `m1-report.md` next to discover's `report.md`.
- `smtool verify <archive>` parses, decompresses, and cross-checks sizes. `discover.ps1` contains a second,
  independent OBPK table reader so the two can be compared on real files.
- Synthetic-fixture tests: `dotnet test` (fixtures are generated in code; no original data).

### Map lighting and normal maps (mp_tdm_09) — **values supported by evidence; Godot conversions provisional**
The M18 search missed it: the sun, ambient, fog and bloom live in `l_lights.Layer` itself (`RegionLayer.Root` is a
`WorldGroup` whose `Objects` hold the entries below; `MapLighting` reads them, `MapScene.Lighting` exposes them).
Object classes in the 226 entries: `base/sunlight` (1), `base/environmentregion` (3), `base/omnilight` (111),
`base/spotlight` (5), `base/fxobject` (68, god rays, not read), `StaticObject` (32 light fixtures, drawn as meshes),
`WorldGroup` (6). `d_tdm`, `e_art` and the other layers hold no lights.
- **`base/sunlight`**: `Rotation` degrees (-45, 45, 0), `Color` (1, 0.863, 0.678), `Power` 3, `NumSunSplits` 4 (cascaded
  shadows), `FarSunDistance` 400, `Position` (only a reference).
- **`base/environmentregion`**: `Position`, `Scale` (box size, full extent), `Priority`, `Enabled`, `EnvironmentDescs`
  (five structs). Manufactorum: `_globalEnv_M09` (priority 1, 700 x 700 x 300 m around (200, -800, 0)) and two priority 2
  boxes (54 x 176 x 68 m, at the two ends) named `_globalEnv_M9_bloom[_01]` that raise sun `Power` to 4, darken
  `LowerAmbientColor` and double bloom. The client blends the boxes in by camera position (pass 2).
  - `FogDesc`: `OutscatterColour` = two gradient stops `[position, [r, g, b, a]]` with **bytes** (0-255) stored as
    eight-byte values: (60, 60, 69, 0) to (190, 163, 138, 69); `OutscatterRange` 250 m; `GroundFogColour` all zero,
    `GroundFogHeight` 0, `GroundFogRange` 18.
  - `AmbientDesc`: `LowerAmbientColor` (0.188, 0.204, 0.376), `UpperAmbientColor` (0.514, 0.533, 0.776),
    `AmbientSaturation` 0.8, `SSAO_Multiplier` 1, `SSAO_DisableSSAO` false, plus world-occlusion and darken fields not read.
  - `SunDesc` repeats the sun (`Colour`, `Power`, `Rotation`, ...); `HDRDesc`: `BrightThresholdMin/Max` 0.4/0.8,
    `BloomIntensity` 1, `BloomPersistence` 0.25; `FXEnvironmentDesc`: gravity (0, 0, -9.8), wind (0, 2, 0).
- **`base/omnilight`** (117 in the file with nested groups): `Position`, `Color`, `Power` (0.5 to 3), `AttenStart`,
  `AttenEnd` (3 to 20 m; `Scale` repeats `AttenEnd`), `CastShadows` (false in every one), `Static` (false), `Enabled`,
  `MaskName`/`MaskPackage` (empty). Names: `Sun_Bounce_nn` (warm), `Cool_Fill_nn` (cool). **`base/spotlight`** (5): the same
  plus `FOV` (55 or 75), `ShadowBias`, and a `Rotation` of a few degrees about X or Y (plus 90 about Z) at height 17-26 m
  with range 15-20 m.
- **Light direction (provisional approximation):** lights point along local -Z under the object rotation order (X, Y, Z in
  degrees). Evidence: the spots hang about 17 m up with 15 m range and tilts of 5-6 degrees; a horizontal beam would not
  fit; the sun then has 30 degrees elevation, travelling toward -X, -Y.
- **Not decoded:** how the engine turns `Power` into radiance, the falloff between `AttenStart` and `AttenEnd` (assumed
  full strength to the start, nothing past the end), the exact meaning of the fog alpha (read as the opacity reached at
  `OutscatterRange`), the `MaskName` cookie textures (all empty here).

The client (`client/MapLightingView.cs`) turns these into a Godot sun (energy = Power x 0.55), a gradient sky for ambient
and reflections, depth fog (pass 2: opacity 0 to alpha over the range, far colour), a little bloom, and 117 `OmniLight3D` and 5
`SpotLight3D` (energy = Power x 1.2, range = `AttenEnd`, fading out from 70 m, at most 6 shadowed): all **provisional
approximation**; the data values are *supported by evidence*, the factors were tuned by eye.

**Normal, specular and emissive maps.** A `bmat` lists texture parameters beyond `DiffuseMap` (counts over the 144 materials
of `mp_tdm_09__wrld`): `NormalMap` 133, `SpecularMap` 19, `EmissiveMap` 9 (with `EmissiveColor`), `DiffuseMap2`/`NormalMap2`
53 (second layer, drawn in pass 2), `AlphaMaskMap` 48 (blend mask, drawn in pass 2), `YCoCg`/`BloomControlGrayscale` (sky). Constants:
`SpecularPower` and `SpecularColor` 122, `DerivedSpecParams` 103 ("drvspec" shaders derive specular from the diffuse; four
values, meaning unknown), `DrvSpecDesatAmount`, `TileU/V`, `VtxBlendParams`. All normal, specular and emissive textures are
DXT1 (no DXT5nm swizzle, no BC5): **x in R, y in G, z in B**. `BmatMaterial.Floats` / `.Vectors` expose the constants.
- **Orientation (supported by evidence):** on a raised rivet in `adept_mech_skull_cog_large_nrm` the top edge has high G and
  the right edge high R, so the map is *green up, red right* (the OpenGL convention Godot expects). In the meshes the stored
  tangent equals dP/dU in 210,800 of 210,804 triangles and `cross(normal, tangent) * w` equals -dP/dV (image up) in 210,722 of
  210,804, so Godot's tangent frame matches without flipping anything.
- Conversions to `StandardMaterial3D` (`client/MapMaterials.cs`, **provisional approximation**): `SpecularPower` n to
  roughness (2 / (n + 2))^(1/4); `SpecularColor` luminance x 3 (0.05-0.6) as the specular scale; a `SpecularMap` becomes a
  roughness texture of 1 - 0.7 x luminance; `EmissiveMap` x `EmissiveColor`. The Space Marine armour's material names its own
  `NormalMap` and `SpecularMap` (shader `diff_norm_spec_lp_lgchar`; its `LightPropMap` is not used on this legacy set). The Chaos wargear
  material (`character_wargear`) names `NormalMap` as a plain `TextureParameter` and `DiffuseMap`, `SpecularMap`, `LightPropMap`
  in `WargearTextureParameter` structs (`PaintMask` too on some pieces): the normal, specular and paint maps are used (see "Armour paint"),
  and since M40-T13 the `LightPropMap` of painted pieces too (see "Light property maps"). The `<piece>_nrm` name inference in `StaticModel` is only a fallback.
  Checked on screen (viewer, `--chaos basic|havoc_01|raptor_01`, and live): none of the armour materials has `BlendMode` 8, so no
  visor or cloth is cut out; roughness stays high (no spec power, bright spec maps only gloss the metal), nothing over-shiny.

#### Second layers, alpha cut-outs, bloom regions (lighting pass 2) — **provisional approximation on evidence**
- **Vertex colour (supported by evidence):** the DCM `u32 colour` is bytes **B, G, R, A** (D3DCOLOR order; a rust-coloured junk pile
  reads warm only this way). In `mp_tdm_09` RGB is nearly always one grey value (a tint; white = none) and A varies 0-255 in every
  two-layer material (default 255). For most of them the mean A is 150-250, so **A = 255 is layer 1** and layer 2 (dirt, rust,
  damage) is the sparse overlay: weight of layer 2 = 1 - A. (With the weight = A reading the roofs and pipes came out mostly rust.)
- **Two-layer materials (53):** shaders `dif2_advvtxbld[eg]_vtxtint_norm2_drvspec` (38), `diff2_vtxblend_...` (5),
  `diff2_mskblend...` (10). `DiffuseMap2`/`NormalMap2` are the second layer (flat normal when missing), `SpecularPower2`/`SpecularColor2`
  its specular constants, and `TileU`/`TileV` (present only on these 53) its UV tiling. `AlphaMaskMap` is **not** an opacity mask: on
  the advanced vertex-blend shaders it is a noise texture (`mask_crunchy_noise_msk`, `mask_rusty_noise_msk`, or a diffuse such as
  `conc_cracked_dif` on the "eg" variants) and on `mskblend` shaders a real blend mask (`mask_metal_dif`, ...). `VtxBlendParams` (x, y, z, w)
  = (4, 1, 20-25, 0-1) on most: read as contrast, noise amplitude, noise tiling over UV 0 and an unused flag; the rubble's (100, -10.6, 38.4)
  is clamped. Open: the exact blend formula, the flag, what "eg" means, the derived-spec parameters, and the `uv1` role.
  Client formula (`MapMaterials.LayeredShader`): vtxblend f = 1 - A; advvtxbld f = clamp(((1 - A + (noise - 0.5) y) - 0.5) x + 0.5);
  mskblend f = mask; diffuse, normal, roughness and specular mix by f; the vertex RGB tint multiplies the result (all `vtxtint` shaders).
- **Alpha cut-out:** `AlphaMaskMap` is not it. The material's own `BlendMode` field is 255 (default) on 133 materials and **8** on 11
  (barbed wire, floor grate `GrateTransparentPR`, banners and seals, fans, trims, rubble pile); `CullMode` 0 (two-sided) on the grate
  and the barbed wire. All BlendMode 8 diffuse textures are DXT5 with alpha except `trimPR` (DXT1); the grate's alpha is exactly
  0 or 255, the others have gradients. The client reads 8 as alpha scissor at 0.5 (shadows follow the cut-out) and 0 as two-sided;
  whether 8 means alpha test or blend is not known. `BmatMaterial.BlendMode` / `.CullMode` expose them.
- **Bloom regions:** `MapEnvironment.Weight` (smoothstep over 8 m from each box face) blends the two bloom boxes into the global
  region by the camera's game-space position (sun power 3 to 4, darker lower ambient, bloom 1 to 2). The original fades by time
  (`BlendDuration` 0-3 s per description), not by distance; that is not reproduced.
- **Fog re-read:** the gradient's alpha is 0 at the near stop and 69/255 at the far stop, over `OutscatterRange` = 250 m. Pass 1 treated
  the far alpha as the opacity at 250 m of exponential fog, which kept growing with distance (hazy overview). Pass 2 reads it as a ramp
  from 0 to 27% over 250 m that then stays (Godot depth fog, `FogDensity` = 0.27): more detail in the far hall, still hazy in the
  overview because it is 200-400 m away. Both are readings of the same data; the engine's fog function is not known.
- **Vertex tint colour space (provisional):** D3DCOLOR is often gamma-encoded, so the vertex RGB may need sRGB-to-linear before
  it multiplies the diffuse. Compared on screen (no tint / linear / sRGB, `SM_NO_TINT=1`, `SM_TINT_SRGB=1`): the tint is subtle
  either way; the sRGB reading darkens shaded walls and stairs a lot (a stored 41/255 becomes 2%), the linear reading about
  halves them less. Linear is the default; the data cannot settle it. Cut-out materials are not tinted in single-layer form (a
  `StandardMaterial3D` would also multiply their alpha by the vertex alpha); none of Manufactorum's 11 `BlendMode` 8 materials
  is a single-layer `vtxtint` shader. The unpack and weight rules live in `SmContent/VertexColour.cs` (tested).
- The client preloads the three Chaos armour sets (`basic`, `havoc_01`, `raptor_01`; about 0.2 s) when a map loads.
- Frame time of the Manufactorum client with all of this (1 windowed client, vsync-capped on a high-refresh monitor): mean 4.2-4.9 ms, renderer
  GPU 0.8-1.5 ms, CPU 0.6-2.0 ms.
- Not applied: `base/fxobject` god rays (effects system, M25), `DerivedSpecParams` (decoded in M40-T13, "Light property maps"), per-layer
  specular maps on two-layer materials (the constants are used there).


### Audio follow-ups (M33, 2026-10-28)

Names below were found by hashing (FNV-1, lower-cased) candidate strings and every ASCII token of `data/packages/*.oppc`
(575k tokens; the scan is a throw-away probe, not committed) against the IDs in the MP banks: 1,231 of the 2,155 MP-bank events
are named that way, and the RTPC, argument and dialogue-event names below.

- **Action parameter block** (33 bytes for set/reset pitch, volume, LPF, bus volume and mute; 37 for Play; 49 and 57 with an
  exception list), after the header the old reader already splits (type, target, delay, delay range, subsection size):
  `u32 transition ms` (0; Play: fade-in, Stop: fade-out, property actions: the glide), `u8 fade curve` (12), `u8 value
  meaning` (13: 1 independent, 2 offset: the reset actions all say 1), `f32 value` (17). Action types: high bits 0x6 mute,
  0x7 unmute, 0x8 set pitch, 0x9 reset pitch, 0xA set volume, 0xB reset volume, 0xC set bus volume, 0xD reset bus volume,
  0xE set LPF, 0xF reset LPF, 0x12 set state, 0x13 set game parameter, 0x60 set switch (type 0x60001, parameters `u32 group,
  u32 value`: `play_primary_switch` / `play_secondary_switch` set group d6e86601 to `primary` / `Secondary`); low byte 0x10 / 0x11
  (0x11 = this game object only, interpretation: buses always use 0x10), 0x20, 0x40 (with exceptions). Counts over the MP banks:
  SetLPF 172 (values 20 … 80, e.g. stun 45, grenade deafening 60 … 80, sniper zoom filter 20 … 70 over 750 ms), ResetLPF 125,
  SetPitch 149, ResetPitch 149, SetVolume 350, ResetVolume 275.
- **Node LPF**: the node base's LPF triple is real data (491 of 17,839 MP-bank nodes non-zero, 374 with a random range); added up the
  parent chain like volume and pitch (interpretation).
- **RTPC bindings** (node base tail): `u16 n, {u32 rtpcId, u32 propertyId, u32 curveId, u8 scaling, u16 points, points × 12 bytes
  {f32 x, f32 y, u32 shape}}`; 122 nodes, 14 (parameter, property) pairs. RTPC IDs by name: `distance` 49f32248 (x in m, 0 … 500),
  `speed` 26341ede (x in m/s, 0 … 100), `fast_volume_random` 15e4776a, `height` 4c47d5c0, `altitude_wind_buffet` aa8c327f,
  `wind_buffet_volume` 3813ae07, 6eef6938 (no name found). Property numbering 0 volume, 2 pitch, 3 LPF (1 LFE, 4: a tiny
  −0.85 … 0 on `distance`, unknown). Used by triggered MP events: `speed` by footsteps (volume 0 → +19.8 dB between 0 and 5 m/s,
  pitch −500 … 0, LPF 10 … 2), landings, reload and gear foley (volume −96.3 dB below 3 m/s), evades; `distance` by grenade trails
  (pitch, volume), explosions (`play_sm_grenade_explode` LPF), jump pack ground pounds and charge sounds (pitch).
- **Attenuation low-pass**: 203 of 232 attenuation objects use the fourth curve slot for a distance → LPF curve (scaling 0, values
  0 … 100); added to the voice's LPF.
- **Dialogue events** (HIRC 15) in `speech_01.pck` (English; 17,802 objects, 6,649 streamed wems, the campaign's scripted lines
  among them): `u32 depth, depth × u32 argument group IDs, u32 treeSize, u16 100, treeSize bytes` of 12-byte nodes `{u32 key,
  u32 (u16 firstChild, u16 count) | audio node ID, u16 weight, u16 probability}`; all six events parse to the last byte.
  `speech_exertion` (3b0a3e6a): `character`, `exertion_type` (stunned, damage_big, damage_small, damage_long, death,
  effort_short_stab, effort_big_pull, effort_build, celebration, roar; 251 nodes). `speech_action` (351c27f0): `character`,
  `action_type` (alt_fire, activate, reload, summon, … and 8 unnamed ones), `action_state` (start, end, 00000000 = any), threat
  (`high`, `Low`, any); 336 nodes. The other four events (33c89dfa danger/grenades, 44ef64c4 health/stunned/revived, eaa138d3 weapon
  classes, 1a963e2d) are not named by any MP command. A key of 0 is the "any" case. Characters present: `tactical_marine`,
  `assault_marine`, `devastator`, `chaos_space_marine`, `chaos_havoc`, `chaos_raptor` (the MP classes) besides the campaign's and
  the enemies'. Leaves name random containers of lines (2 … 8 wems each for the MP exertions); their parents are actor-mixers with
  max instances 1, 3D with attenuation 344cfb06 / 0dfd6958 and bus 20d69852 (volume −0.2 … −3).
- **Speech commands in the MP data** (`SpeechBinding`): `post_speech_exertion_event {1_character, 2_exertion_type}` and
  `post_speech_action_event {1_character, 2_action_type, 3_action_state, 4_threat_level}`; `1_character` is `autodetect` in
  every MP file, resolved by the class's `speech_arg_character` (global `multiplayer_settings/ingame_progression/classes/<class>`).
  118 commands in 35 files; the exertion types used: effort_short_stab (melee abilities, stomps, hits on the ground,
  charge), effort_big_pull (thunder hammer / mace x1, x2, 180, dash; jump pack dash attack and ground pound hits), effort_build
  (the Chaos power sword's 180 swing, heavy bolter / autocannon `evt_soundstart`). The action commands (reload, grenade throws) have an empty
  action type and no leaf under it for any of the six MP characters (the devastator has lines for the action type `reload`, which the MP commands do not name),
  so they produce no sound. The blueprints `sm_space_marine_player` / `csm_chaos_marine_player` bind no speech at all (the
  AI's and the singleplayer `vanguard*` blueprints do: `actn_snd_vox_*`, `post_speech_*`).
- **Not done** (data read but unused or not understood): the nodes' `max instances` for sounds other than footsteps (2,945 nodes; applied to footsteps, see the Footsteps section); State objects (1,098) and the nodes' state-group links (19 nodes); Play fade-in and Stop fade-out (the
  transition time of Play/Stop); Pause / Resume (about 10 actions); Set Game Parameter (2 actions, target de26482a); the
  `0x90011` action (2); HDR; the RTPC property 1 and 4; bus RTPCs and effects (the bus objects are kept raw); music objects.

## Footsteps (the event graph and what was wrong, sound/footsteps, 2026-10-28)

The owner's report: the footsteps "sound nothing like the original". Tools added (all read-only on the install; decoded audio goes to the private cache):
`smtool audio-event <event>` (now prints each node's volume, pitch, LPF, attenuation, RTPC curves, `max` instance limits and the below-threshold byte, and a layer's
crossfade curves), `audio-mix <event> [--local] [--surface s] [--speed v] [--distance d]` (every voice's resolved mix: wem, 2D/3D, dB, cents, LPF, bus chain),
`audio-levels` (the loudest voice of every sound event the MP triggers name, as the local player hears it), `audio-tags [--tag re] [--clip re]` (the rig's
Action tags per clip), `audio-surfaces` (the footstep surface switch over the map's walkable cells), `audio-rtpcs [--hot dB] [--limits]` (every RTPC binding by
parameter and property; node-limit statistics), `audio-node <id> [--hex] [--events]`, `audio-objects <type>`.

**The event graph** (`play_sm_footfalls_normal`, 60 wems, one post = three Play actions + one Stop of the jump pack's descent loop `262226f6`; the Chaos
`play_ch_marine_footstep` has the same shape with 48 wems). Every action is a switch container on group `0x0d500bea` (local case: non-positioned layers; `default`:
positioned, attenuation `1dd40fef`) over a `material_type` switch (dirt, gravel, metal, stone/armour/default, sand; flesh, air, energy_shield, ceramite_armour: silent):

1. the body: stone/armour/default = a sub-bass boom (`13044990`, centroid 57 Hz) + 8 thuds; metal = 8 thuds + 5 metal rings; dirt, gravel, sand variants;
2. the sole: five short samples per surface (0.28 to 0.63 s, 300 to 1000 Hz);
3. the armour foley: three random containers of rattle samples (the only ones with high-frequency content, centroid 1.5 to 2.2 kHz).

Every sample is normalised to about -0 dBFS; the level is entirely the node volumes, the output bus chain (-32 dB for the footstep bus incl. the master) and the speed
RTPCs (spec "Footsteps"). The thuds and rings are 80 to 92 % below 150 Hz (centroid 115 to 165 Hz), the sole samples 300 to 900 Hz.

**What was wrong (found by comparing the data with the client's log, `--log-sound footstep`):**

1. *Limit sound instances not applied.* The layers of every action set a limit of 1 to 4 instances with kill-oldest (the other players' main layer: 1; the local
   sole layer: 1; the local main layer: 2; the foley: 3 and 4 global). In the original each step cuts the tail of the previous one; the client let every step's
   0.75 s thud ring out over the next (a running marine at 2.3 steps a second, a sprinting one at 4). 2,945 of 17,875 nodes carry a limit; the flags next to it
   (kill-newest, global, ignore-parent) are now read (`HircNodeBase`).
2. *Voice pool starved by inaudible voices.* A running local player posts six voices per step; three of them (the foley layers) end at -60 to -100 dB. The volume
   threshold only applied to positioned voices, so these took 2D player slots (16 of them) and the player pool stole voices: 25 steals in 40 s of a bot walking
   (live count: 18 voices playing at the 10 s mark, peak 24). The nodes' below-threshold byte is 1 (kill) on every footstep node (and on 14,109 of 17,875
   nodes), so such voices should not play at all. After the fix: steals 0, voices playing 8, 124 voices culled in 40 s.

**The advanced settings' last two bytes** (`TrailingA/B`, `smtool audio-rtpcs --limits` prints the cross-tables): (0,0) 16,924 nodes, (1,0) 414, (1,1) 515, (0,1) 22. A = 1 goes with a limit that differs from the parent's (12 % equal, 42 % for A = 0) and with parents that have none (53 % against 22 %); B = 1 goes with a below-threshold byte that differs from the parent's (10 % equal against 65 %). That looks like override flags but is not clean: 2,162 nodes with A = 0 carry limits of their own, including the footsteps' `Random 127aa7b3` (limit 2) under a layer with A = 1. Inheritance is therefore not implemented (each node's own values are used, all ancestors' limits apply); low confidence, listed in the spec.

**Checked and found right:** the event per tag (spec "Footsteps"); the tag times in the clips; the surface switch (Manufactorum: stone 58 %, metal 38 %, default 4 %,
the id order from M25a; nothing reads `air` on walkable floor); the local/other variant; the speed value (4.5 to 6.5 m/s while running, 10 m/s sprinting; the 6.44 knee of
the sole curve is the run speed 6.5); the sample decode (smooth decays, broad spectra elsewhere: not a decoder artefact); the pitch, LPF and volume sums.

**Open: the level.** On the data as read, a running local marine's sole clank is -7.8 dB and the thud -19 dB while the bolter's loudest voice is -24 dB, a grenade
explosion -23 dB, and the same thud sample as a landing -28 dB: the footsteps would be the loudest sound of the mix by 11 to 16 dB, and the clank (A-weighted) about
10 dB above the thud, which would make a running marine sound like "tk tk tk" rather than thuds. Either the speed RTPC is not a plain +20 to +32 dB offset (the
curves reach +32 dB, `distance` ones +89 dB, beyond what a plain voice volume would be), the bus chain does not sum as read in M33 (the buses are kept raw; the bus objects also
carry what is not read: the master bus holds a Compressor (FxShareSet `37fc0fd3`: threshold -20.2 dB, ratio 1.5, as the 33-byte parameter block reads) and a Peak Limiter
(`3e23be10`: threshold -4.6 dB, ratio 50), the SFX bus `15163f91` two more effects, and the footstep bus a state group whose two states each lower it by 6 dB), or the
footsteps really were that hot. Nothing here can be heard; it needs the owner's ear (`--sound-gain` is
global; `smtool audio-mix` prints the numbers). **Answered (our reading) by the second pass below.**

## Footsteps, second pass (sound/footsteps-2, 2026-10-29)

The owner, after the first fix: the footsteps "still sound atrocious ... like hard boots banging on loose metal flooring"; wanted: the original's sound, slightly quieter.
What was compared (all read-only; decoded samples only in the private cache):

- **Levels.** The cause is the open level question above. On the data as read the running footsteps' voices came out *above* 0 dB at the voice (before the
  buses): the body switch `33fc8aae` (−3 dB) carries a speed curve to +19.81 dB, the sole switch `2267bf67` (0 dB) one to +30.23 dB (others' `1989adf7`: +21.25), so
  the own sole clank sat at +24 dB voice level, −7.8 dB with the −32 dB bus chain: 16 dB over the bolter's loudest voice and 11 dB over the thud. The decoded samples
  (`smtool audio-decode`, then a probe: peak, loudest-100-ms RMS, A-weighted RMS, spectral centroid; not committed) give the intrinsic loudness: thuds RMS −4.5 dBFS,
  −17 dB(A), centroid 184 Hz; the boom 13044990 −22 dB(A), 69 Hz; the metal rings −14 dB(A), 285 Hz; the metal soles −14.5 dBFS RMS, −18 dB(A), 607 Hz; the stone
  soles −23 dB(A), 283 Hz; the foley layers 164 to 1,563 Hz. At those levels the ear gets the sole clank about 11 dB(A) over the thud and the rings level with it:
  "boots banging on loose metal".
- **Why the offsets cannot be literal.** Of the 29,264 nodes (sounds and containers) of the MP banks, **none** has an own volume above 0 dB (max exactly 0; only
  random ranges reach +6 on 93 sounds, so 0 dB is a convention of the banks, not a hard limit of the format). Ten RTPC volume curves go positive: the six footstep
  switches above (both sides, bull rush), two tiny ones (+0.93 Dreadnought, +1.08 Ork nob footstep, both staying under 0), and the Ork grenade fuse `07d5afc8`
  (`play_or_grenade_fuse`): own −12.3 dB, a `distance` curve **+89.48 dB at 0 m** down to 0 at 40.5 m. Literally that fuse would be +77 dB at the voice (it ranked
  +38 dB in `smtool audio-levels`, about 60 dB over every gun). A designer tuning by ear would not leave such values unless the engine saturated them. The weapons' voices sit
  at exactly 0 dB on their chains (the bolter's loudest −24 dB = its buses), so the mix is built as "voices at most 0 dB, buses set the balance". The bus chains therefore
  decide how loud footsteps can ever be: the footsteps' chain is 8 dB under the weapons' (own −32 against −24; others' −27), whatever the curves do.
- **The rule adopted (interpretation; chosen by review, 2026-10-29): `peak`.** A volume curve that goes above 0 dB is shifted down by its highest point
  (`VoiceMixer.PeakNormalised`, applied to every sound: only these ten nodes change, pinned by the install-gated `RtpcCapInstallPinTests`). The node then plays at its own
  volume at the curve's loudest point and quieter below it, so the curve's shape stays: the body thud is 19.8 dB quieter at a standstill than from 5 m/s on, the sole
  32 dB quieter at a standstill and 2 dB under its top at a run (+30.23 against the +32.22 at 100), the fuse full at 0 m and fading by 89 dB to 40.5 m. First tried and
  kept as an aid: `node`, a node with its offsets at most at 0 dB (`VoiceMixer.CapRtpcVolume`; flattens every speed above about 0.8 m/s to one level, and the soles' curves
  then change nothing above 0 m/s). Also kept: `voice` (that bound on the voice's chain sum: every layer of a step at the same level, the nodes' −4 / −8 balance lost) and
  `off` (the old behaviour; the master bus's compressor and peak limiter, not reproduced, would then have held the peaks). Set/Reset volume changes are added after the
  cap in every mode (a lowering change still lowers). Pitch and low-pass curves are never touched.
- **Levels (peak, owner's −3 dB included), metal, own / others at 5 m:** walk 2.5 m/s thud −52 / −50, ring −56 / −56, sole −62 / −61, armour foley −35 / −38 (the
  foley's own curve peaks at 2.5 m/s, so a walking marine is mostly armour rattle); run 6.5 thud −42 / −40, ring −46 / −46, sole −43 / −48, foley −63 / −66; sprint 10 as
  the run, plus the bull-rush event. Node mode: thud −39 / −37, ring −43, sole −41 / −44 at every speed. Bolter −24 own, −25.5 at 5 m: a running marine's loudest step
  is 18 dB under it own and 15 dB at 5 m (node 15 and 12). The Ork grenade fuse at 3 m: −43 dB (node −32.5, off +38).
- **Owner's level:** −3 dB on every foot-tag footstep post (`FootstepLevel`), landings excluded; a deliberate change (docs/changes-from-original.md).
- **Checked and unchanged:** surface (`material_type` from the weapon-layer material; the first bot of the run stood on metal for 194 of 194 steps, the other on stone 175,
  metal 30); layers (no crossfade curves on any layer of the event; no weights or probabilities on the random containers: uniform with avoid-repeat 1 to 4); positioning
  and attenuation; the bus routing (own steps through the layer's override `23814d2c` −8 > `15163f91` −12 > ... −32 dB; others' through the top switch's `2eaf28b4` 0 >
  `0b2fe5d6` −15 > ... −27 dB); pitch (+280 cents on the body switch, −303 on the metal thuds, the rings stay +280) and LPF; the step triggering (one post per foot tag,
  the sprint's `_rush` tags post the bull-rush and the normal events as the data says; median run interval 0.48 s) **except one double, fixed:** at a run-to-sprint
  change the run blend's foot tag and the sprint clip's `_Rush` tag (clip time) fired one footfall twice 0.04 to 0.09 s apart (3 times in 75 s); one foot now posts at
  most once per 0.25 s (`FootstepLevel.AdmitFootfall`, ours), measured after: shortest interval 0.24 s, 2 doubles dropped in 75 s; the `speed` value
  (m/s: the Dreadnought's curves are anchored at 6.5, the run speed). The footstep bus and the SFX bus hold a `combat` state group (`a4c2f6bd`, found by hashing; footstep
  bus −6 dB in both of its states, SFX bus +4 / +2) set only by the music events (`SetState` ×4, state `99e77f78`), so not applied. The bull-rush event's second action
  is an LFE thump (`46029939`, 56 Hz, 98 % of its energy under 150 Hz) whose LFE send follows `speed` (−48 dB below 5.5 m/s, 0 from 6): the LFE property is not
  reproduced, so it plays full-range on each own sprint step (−23.3 dB on bus `17097555`, −26.3 with the owner's level).
- **Open (not changed here):** the bus objects' second float (`b`, read as a second volume since M33) might be the bus's LFE volume: only three buses set it, the master
  (−7), `17097555` (−9.3, the bus of the LFE thumps of the sprint and the bolter) and one more (−6). If so the master is 0 dB, every sound 7 dB louder and those LFE
  sounds quieter; the footsteps' balance against the weapons does not change.

## Impacts and the hit confirm (sound/impacts, 2026-10-29)

The owner: bullet hit sounds "play as if they were landing right on top of my character even if I'm shooting at something far away ... very high pitched and jarring".
Read-only investigation (`smtool audio-event|audio-mix|audio-decode`, a throw-away spectral probe; decoded samples only in the private cache):

- **The impacts at the destination are right.** Own ray impacts and others' shot records post the weapon's hit event (`play_bolter_hits` for every bolter, bolt pistol,
  storm bolter and heavy bolter; `play_stalker_hits`) on a one-shot emitter at the hit point; every action of the event is positioned with attenuation `12c68ad7`.
  Loudest voice on metal: −27 dB at 5 m, −40 at 20 m, −65.5 at 50 m (bot run: own impacts at 16 to 48 m logged at −37 to −64 dB). Its layers are bright: the metal and
  armour ricochets (`2f089dcd` / `202447d8`: centroid 2.5 kHz, half the energy over 2 kHz), `342a846d` (3.5 kHz), action 2 at +600 cents and action 3 at +200.
- **The sound "on top of" the shooter is the hit confirm.** The weapons bind `player_hit_something_action` (trigger point `self`) to `play_bolter_smack`
  (`play_stalker_smack`, `play_plasma_smack`, `play_grenade_launcher_smack`): action 1 a non-positioned layer of two random containers over the same 12 "smack" samples
  (0.3 s, centroid 1.35 kHz, 66 % under 150 Hz) at −32 dB; action 2 a non-positioned switch on `material_type` (flesh: −450 cents body + a +700-cent layer; metal and
  armour: the metal ricochets at +250; ceramite_armour: `2d16527e` ±200 and the +700 layer, −24 dB; energy_shield, stone, default: nothing); action 3 (30 ms later) a
  positioned switch on state group `0x47e2171c` (none, alive: nothing; `death_flesh` `c8871eac`, `death_armored` `07fb5fa0` = the default: two layers, one with a 4.8 kHz
  centroid); action 4 sets the group to `alive` after 50 ms. The death states are set by `play_set_state_death_armored` / `_flesh` (found by name); only the Ork Boy's
  `death_action` posts one (`_flesh`). The client posted this event on the shooter's own emitter, whose `material_type` is the floor under the feet (set for
  footsteps): on Manufactorum's metal floors every hit played a metal ricochet at +250 cents, −27 dB, non-positioned, louder than the impact 5 m away; and the kill layer
  played on the first hit of a session because an unset state group fell to the container's default case. Fixed (faithful reading): the material is the victim's
  (`ceramite_armour` when the hit struck armour, else `flesh`) and an unset state group is Wwise's "None" (of the MP banks' 2 state switch containers only this kill
  layer changes).
- **Owner's changes (ours, docs/changes-from-original.md):** a soft confirm by default (only action 1, −10 dB, −500 cents armour / −700 flesh, low-pass +45, 0.15 s
  cooldown; a kill −7 dB with the data's kill layer) and the bolter family's and the stalker's impacts 400 cents lower with a low-pass +40 at the same level.
  The Call of Duty: World at War reference is only the inspiration for the character (a soft, dull thud); no CoD audio is used.

## Shield sounds (sound/shield-regen, 2026-10-29)

The owner: "the shield regenerating starting sound is playing constantly on a loop. am i hearing other players armor regenerate or is my own looping?" It was the own player's
broken-shield warning. The blueprints' `armourshield_broken` posts `play_armor_break`, whose second action is a switch on `0x0d500bea` with a case only for the local player: a
continuous random container (`3acd4df5`: loop count 0, transition mode 5 = trigger rate, 700 ms, one sample at −10 dB and −600 cents; read from its payload) that pulses every
0.7 s. Nothing stops it but `play_armor_restore` (the `armourshield_regen` start), whose fifth action stops `38d36d37`. A player who died with the shield down never regenerated and
spawned full, so the warning went on through death and the next life (bot run: broken at 45.4 s, dead at 47, pulsing until a regeneration at 83 s). Fixed: a death or spawn ends the
player emitter's loops (`SoundSystem.StopLoops`), and the regeneration start is a span (`ShieldSoundTracker`: once per regeneration, at any level, ended by full or damage, which
posts the binding's stop `stop_armor_restore_early`). Other players' shield sounds are not played (the data's bindings are `apply_only_if_on_player`; the warning has no case for
other players).

## Helmet eye lenses (customizer T6a, 2026-10-28)

Read-only investigation for the eye-lens colour option (our addition; the original has no lens colour setting).
- No separate lens material or surface: each helmet has one `.bmat` (shader `character_wargear`, no emissive map;
  textures `_nrm`, `_dif`, `_spc`, `_lp`, `_pnt`). The lenses are texels of the helmet's `_dif` atlas.
- Lens rule (12 of 13 customizable lens helmets clean): inside a per-helmet UV rectangle, diffuse saturation
  `(max-min)/max > 0.4` with max channel > 30 (sRGB-coded values), and paint-mask R+G+B < ~110 (lens texels 0–4; armour
  ~100/100/0). `_spc` is lens-coloured too; `_lp.G` is 20–63 at the lens in 11 of 13 sets (M40-T13: the self-illumination mask, see "Light property maps").
- UV convention: column = u, DDS row = 1 + v (mesh v is in −1..0), i.e. fract(UV) as the painted shader samples.
- Lens rectangles in a 512x512 `_dif`, `(x0,y0,x1,y1)` exclusive, "+x" = the `fx_lefteye` side:

| Helmet package | Rectangles | Lens hue |
|---|---|---|
| `sm_armour_mp_basic_helmet` | (51,305,110,334) (144,305,203,334) | red |
| `…_mk4` | (253,70,323,96) (371,69,443,96) | red |
| `…_mk6` | (142,47,164,106) (142,146,163,198) | red (dark texels, max 68); dome lamp (126,236,150,261) is not a lens |
| `…_master_001` | (426,457,504,480) (427,423,506,446) | red |
| `…_veteran` | (45,291,112,316) (154,291,223,317) | red |
| `…_relic` | (38,97,98,121) red; (256,100,280,122) green (augmetic) | side lamp is not a lens |
| `…_damage` | (50,304,111,335) (142,304,203,335) | red |
| `csm_armour_mp_basic_helmet` | (171,280,216,314) (106,402,141,437) (442,214,468,239) | red, three lenses |
| `…_berserk_01`, `…_berserk_02` | none (black visor slit) | glow only via effects |
| `…_berserk_03` | (206,150,237,169) (278,150,308,170) | yellow |
| `…_havoc_01` | (337,40,379,82) (170,370,234,411) (282,382,342,408) | green |
| `…_havoc_02` | (292,69,358,92) (396,69,463,93) | green |
| `…_havoc_03` | (181,388,222,413) (266,386,286,408) | red (mask alpha 76–78 there; RGB still ~0) |
| `…_raptor_01` | (206,346,254,373) (260,210,278,226) | red |

- Markers: Space Marine helmets have none. Every Chaos helmet has `fx_lefteye` / `fx_righteye` under `RIG_Head`
  (local about (0.11–0.15, 0.13–0.16, ±0.05) in the Havok head frame, X up).
- Glow: Space Marine lenses have no eye marker or effect (their `_lp` G marks the lens faintly, 20–28 of 255: M40-T13). Chaos eyes glow through effects: each weapon's
  `while_deployed` actions play `csm_mp_eye_*` (`master_fx`; meleehammer, bolter_heavy, lascannon, plasmacannon)
  at the eye markers — additive "colour glow" (r 0.15 m) and "point centre" (r 0.02 m) sprites. Played by the client since
  M40-T13 (see "Light property maps").
- Recommendation: a shader rule (lens colour where the rule holds inside the helmet's rectangles; null = original look);
  for Chaos also tint the eye effects once they play (the only route for `berserk_01/02`); a material-only tint is not
  possible (one surface per helmet).
- Implemented (customizer T6, 2026-10-28): the table is `SmContent.Customization.LensRegions` (numbers only), the rule is in the painted
  shader (`MapMaterials.PaintShaderCode`, `use_lens`). Checked by eye on all 13 helmets with lens texels (magenta lens, head close-ups): every
  rectangle lands on the lens or lenses, none on armour; the Relic's small side lamp and the Mk6 dome lamp stay as they were, the Relic's green
  augmetic lens takes the colour (it is in the table); Chaos `berserk_01` / `berserk_02` do not change.

## Match flow: the countdown, the end splash and their sounds (fidelity gap 4)

Where the data is and how it was read (nothing here is stored in the repository; the install is read-only):

- **Texts** (`smtool locale <id>`): 119813 "MATCH BEGINS:", 110550 "VICTORY", 106136 "DEFEAT", 120706 "Draw" (the three splash words; 106133 "Victory!",
  106134 "Defeat" and 106135 "Match Results" are the end-of-arena screen's), 104299 "%1TEAM% is victorious!", 104300 "%1TEAM% has been defeated!",
  111880 "NEXT MAP - %1TIMER%", team names 104333 / 104334.
- **HudText.gfx** (Scaleform, uncompressed `GFX` v8, 1280x720 stage, 30 fps; tags walked with a small script: sprites, placements, edit-text
  fields; the ActionScript was only read for its constant pools and the call sequences around `winlose_mc` / `countdown_mc`). `countdown_mc`
  (sprite 205, root at 619,351) holds two text fields, `match_type_text` (gold 24 pt, centred, default text "Game Mode Name") and
  `match_countdown_text` (white 40 pt, default "00:00"), plus a half-transparent shape. `winlose_mc` (sprite 135) holds `WinLose_ChaosWins_Mc`
  (97), `WinLose_MarinesWin_mc` (101), `WinLose_Draw_mc` (104), `WinLose_Failure_mc` (111), `WinLose_XP_Numbers_mc` (128) and `WinLose_LevelUp_mc`
  (134); each of the four result clips has 195 frames (6.5 s) and ends with a call to `Proceed` / `ProceedToScoreboard` (frame 194). The result
  words are text fields of 72 pt (x1.67): `$110550` in #ffcc00 (Marines) and #ff9900 (Chaos), `$106136` in #ff3300 (failure), `$120706` in #ffcc00
  (draw). The timeline of clip 101 is in spec/behavior.md ("Match flow"). The engine sets the texts and plays the clips (`Native_*` entry
  points); no length of the countdown is in the movie or the scripts (`multiplayer.lua` names `OnMultiplayerStartRound`, `GameOver`,
  `Network_EndRound`, `winningTeam`, `Post2DAudioEvent`, `Sound_PlayObjectiveCompleteNoise`; `Deaths:SuddenDeath` exists in `deaths.lua`).
- **Wwise** (names are FNV-1 hashes of lower-cased names, so unknown ones were found by hashing candidate names against the banks' event IDs):
  `play_mp_countdown` / `stop_mp_countdown` (In_Game_Default; one embedded 2.6 s Vorbis in a -4 dB layer; the stop event also resets two pitch
  properties), and four music segments played by `play_mp_sm_victory`, `play_mp_sm_loss`, `play_mp_chaos_victory`, `play_mp_chaos_loss`
  (In_Game_Default, one streamed track each: wems 0x386a0430, 0x2df525cc, 0x366769c8, 0x308e8d35, 462 KB to 730 KB; the segments' length fields
  read 8.92, 12.69, 10.35 and 13.54 s). No draw event or match-start stinger was found by name; `play_mp_bonus`, `play_mp_point_captured`,
  `play_level_up` and `play_alarm_siren` exist too (not used here). The HUD movie itself posts `Play_MP_Points_Total` / `Stop_MP_Points_Total`
  (the XP tally) and `Play_MP_Level_Up`.
- **Wwise music objects** (bank v56): a MusicSegment's payload is a node base, then the child track IDs, then tempo, markers and the length (f64, ms);
  a MusicTrack's payload starts with its source count and sources (as a Sound's), followed by the playlist and its own node base (not read).
  `AudioCatalog` plays a segment's tracks' sources once with the segment's volume and bus.

## End-of-match screens: the score count-up and the summary (fidelity gap 4, rest)

Read-only look at two Scaleform movies of the install (`GFX` v8, 1280x720 stage, 30 fps): `EndOfArena.gfx` (`ui_ingame.oppc`, 60 KB) and
`SummaryScreen.gfx` (`ui_fe.oppc`, 106 KB), with the locale (`smtool locale`). A small tag walker listed each movie's sprites, frame counts, frame
labels, placements (names and matrices) and edit-text fields (bounds, size, colour, alignment, default text); the ActionScript was read only as
constant pools (names and strings), never interpreted, so **no script value is known**: the stage lengths and counting speeds are not in the data.
Nothing from the movies is stored in the repository (texts are read from the install at runtime).

**`EndOfArena.gfx`** (the in-match "end of arena" tally; its root script names `EndOfArena`, `Native_OnInit`, `Native_OnActivateScreen`, `Native_SetExperience`,
`Native_ShowReady`, `Native_OnPlayerDead`). The main timeline is 50 frames (1.67 s): the number block `team_1_mc` (sprite 89) rises from y 265 to y 185
(ease out, about 3 px a frame at first), with `xp_bar` (sprite 122: `current_level_txt`, `next_level_txt`, `max_level_txt`, `player_name_txt`; 101 frames),
`unlock_mc` (sprite 131: `unlockicon_mc`, `title_txt`; 105 frames, labels `play` 1 and `repeat` 50) and `nextarena_mc` (sprite 137: the arrows and the text
"DEPLOY TO NEXT ARENA", locale 122390) placed with it. The number block (sprite 89) has **76 frames with the labels `teamscore` (0), `playerscore` (25) and
`xpearned` (51)** and a frame action of 2 bytes on frames 0, 24, 50 and 75 (the size of a single `stop` action; the action bytes were measured by size and not read, so the stops are inferred): three stages, each a number block that the script counts up and then releases to the next label (interpretation).
Blocks: `teamscore_mc` (sprite 37: label "Team Score" 122315 in 22 pt, number `teamscore_txt` 36 pt, both #ffcc00), `playerscore_mc` (sprite 34: "Your Score" 122314,
`individualscore_txt` 36 pt white), `playerxp_mc` (sprite 31: "XP Earned" 122313, `individualxp_txt` 36 pt #99ff00). Below them four player rows
`player_1_mc` .. `player_4_mc` (sprite 81, 30.5 px apart: `level_txt`, `name_txt`, `stat1_txt` / `stat2_txt` / `stat3_txt` at x 469 / 594 / 720 for score, kills and deaths,
plus headset, network, party and health indicators and the three role icons), a header (sprite 87: "LEVEL" 106140, "PLAYER" 110548, "SCORE" 106096, "KILLS" 106098, "DEATHS" 106100,
18 pt #c0c0c0) and `arena_name_txt` (28 pt gold, "Name of the Arena"). Strings: "Level Up" 122075, "Victory!" 106133, "Defeat" 106134, "Match Results" 106135.
Script names (constant pool): `g_teamTargetScore`, `g_teamCurScore`, `g_teamIncScore`, `g_playerTargetScore`, `g_playerCurScore`, `g_playerIncScore`, `g_scoreStartDelay`,
`g_scoreTimeDelay`, `g_scoreTimeInterval` (the count runs on `setInterval`), `g_targetXP`, `g_curXP`, `g_incXP`, `g_xpBarTimeInterval`, `g_unlockStartDelay`, `g_unlockTimeDelay`,
`g_unlockRepeatInterval`; functions `StartUpdatingTeamScore`, `StartUpdatingPlayerScore`, `StartUpdatingXP`, `UpdateTeamScore`, `UpdatePlayerScore`, `UpdateXPEarned`, `StartShowingUnlocks`,
`ShowUnlock`, `PlayUnlock`. Sounds it posts (names): `Play_MP_Points_Total` / `Stop_MP_Points_Total` (the tally), `Play_Objective_Complete`, `Play_Unlock_Class`, `Play_Menu_*`.
Wwise: `play_mp_points_total` is a 4.5 KB embedded sound in `In_Game_Default` (one random-container voice, -20 dB, pitch -600 cents, a low-pass filter), `stop_mp_points_total` stops it
and resets the filter.

**`SummaryScreen.gfx`** (front end; root `summary_mc`, sprite 440 at 640,360; the screen after a match, before the next map). Its tabs: `scoreboard_btn` "SCOREBOARD" (106117),
`battle_record_btn` "BATTLE RECORD" (106116), `career_btn`, "UNLOCKS" (111979), "LEADERBOARDS" (119891), "FRIENDS" (111380); `g_activeTab`, `g_tabs`, `g_isCoop` (a co-op variant:
`Player_COOP_mc`, "RETRIES" 122401). `timer_mc` / `timer_txt` default text "NEXT MAP - 00:00" (111880; the same text as the in-match timer), `timerStr`, `useSeconds`.
*Scoreboard tab*: two team blocks `team_1_mc` and `team_2_mc` (sprite 321, scale 0.9; `team_colors_mc` with frames `friendly` / `enemy`), each with a team icon `icon_mc`
(frames `imperial_friendly`, `chaos_friendly`, `chaos_enemy`, `imperial_enemy`; "SCORE" 106096 at 32 pt over the team score at 50 pt in #f7c500 or #ff3300; team names 104042 "Space Marines" /
104043 "Chaos Space Marines"), a column header `player_header_mc` (sprite 320) **with one frame per mode**, and eight player rows `player_1_mc` .. `player_8_mc` (sprite 264, 27.55 px apart: `level_txt`,
`name_txt`, `stat1_txt` .. `stat5_txt` at x 445 / 518 / 603 / 688 / 773, network bars, headset, party icon, a `dead` health indicator, three role icons, `enemy_mc`). Header frames: `tdm`
(LEVEL 106140, XP 106097, KILLS 106098, DEATHS 106100, RATIO 106101, ASSISTS 106099), `domination` (LEVEL, XP, KILLS, DEATHS, **SEIZED 120334, DEFENDED 120335**), `ctf` (... CAPTURED 122513,
RETURNED 122514), `koth`, `dreadnought` (122572 "DREADNOUGHT"). The row fields in the constant pool: `userLevel`, `userName`, `userRank`, `userScore`, `userScorePerMinute`, `userKills`, `userDeaths`,
`userAssists`, `userCaptures`, `userUncaptures`, texts `kills_txt`, `deaths_txt`, `assists_txt`, `ratio_txt`, `captures_txt`, `uncaptures_txt`, `seized_txt`, `defended_txt`, `kill_death_ratio_txt`,
`score_per_minute_txt`. *XP side*: `base_xp_title_txt` / `bonus_xp_title_txt` / `challenge_xp_title_txt` / total (BASE XP 106081, BONUS XP 106082, CHALLENGE XP 106083, TOTAL XP 106084), `xp_bar_mc`,
`next_unlock_mc` ("NEXT UNLOCK:" 121203), and 24 reward holders (`holder1_btn` .. `holder24_btn`, sprite 147 with `count_txt` "x9999") whose icon frames are the 21 reward tracks: `mp_bonus_kill`,
`_assist`, `_capping`, `_uncapping`, `_avenger`, `_headshot`, `_killstreak_3`, `_multikill`, `_variety`, `_longshot`, `_victory_in_death`, `_reinforcement`, `_won_round`, and `player_track_deathwhileabove`,
`_grudge`, `_doublekill`, `_quadkill`, `_megakill`, `_revenge`, `_saviour`, `_deathfromabove` (the medals of fidelity gap 2). *Battle record*: `battle_record_mc`, `nemesis_mc` (sprite 362: "NEMESIS" 106094,
`enemy_name_txt` 24 pt #ff3300, `enemy_lvl_txt`, "KILL RATIO" 106089 over `enemy_kill_ratio_txt` 40 pt #ff3300, `nemesis_icon_mc` frames `imperial` / `chaos`).
The locale has the record's row labels next to those ids: 106085 "BEST CLASS", 106086 "BEST WEAPON", 106087 "ENEMY KILLS", 106088 "PLAYER DEATHS", 106089 "KILL RATIO", 106090 "TEAM ASSISTS", 106091 "BEST KILLSTREAK",
106092 "HEADSHOTS", 106093 "NEXT UNLOCK", 106094 "NEMESIS", 106095 "SUMMARY SCREEN" (110552 "MATCH RESULTS" is the title of the end-of-match results). 106085 to 106088, 106090 to 106092 are in **no GFX
constant pool** (the rows are probably built by script from numbers), so which movie shows them is an interpretation: the user's description of the original's summary (kills, deaths, assists, captures,
best weapon, best streak) agrees with them.

**What is not known**: every length and speed of the two screens (script values), the order and timing of the in-match tally against the summary, which players the four tally rows list, what the nemesis'
"kill ratio" divides, the scoring formula behind the per-player score and XP, and whether the summary opens on the scoreboard or the record. Our choices are in spec/behavior.md ("Match flow: the score
count-up and the summary") and the gaps in docs/scoreboard-stats-design.md.

## Light property maps (`_lp`), the derived-specular constants and the Chaos eye glow (M40-T13, 2026-10-28)

Read-only investigation of the install (app 55150): every `.bmat` of `data/packages` and of the `mp_*` / `cp_*` chunks parsed for shader
and parameter names (2,206 materials with `DerivedSpecParams`, 938 package materials with a `LightPropMap`), and the 518 distinct `_lp`
textures decoded channel by channel and compared with the same piece's `_dif`, `_nrm`, `_spc` and `_pnt` (per-channel images, histograms,
correlations; throw-away probe, outputs only in the private cache). No executable or compiled shader was read.

**The `_lp` texture.** DXT1, usually half the diffuse's size (256x256 for a 512x512 piece), alpha always 255. Every shader whose name contains
`_lp` (`diff_norm_spec_lp[_lgchar]`, `diff_emis_norm_spec_lp[_lgchar]`, `diff_norm_spec_reflect_lp[_lgchar]`, `diff_spec_lp`) names one, and so
do the wargear shaders `character_wargear` (as the `WargearTextureParameter` with `TextureType` 2: 0 = diffuse, 1 = specular, 2 = light
property) and `mp_custompaint`. The name reads as "light properties".

| Channel | Reading | Evidence | Confidence |
|---|---|---|---|
| R | A per-material gloss (specular power) property | Near constant inside a material: 0.87-0.97 on the basic armour sets (plates 0.93-0.95, raised metal ornaments 0.87, cables and undersuit 0.96; a whole region at one value, not darker in creases, so not ambient occlusion); unused texture space is either the same value or 0. None of the 938 `_lp` materials in the packages has a `SpecularPower` constant, while every `_spec` shader without `_lp` has one (`diff_norm_spec` 15 of 15, `diff_spec`, `diff_tint_norm_spec`, `hair_test2`), so the map most likely carries the power; one exception, the two-layer `diff2_vtxblenduv01_norm_spec_lp` (8 Ork materials), has both. The mapping from R to a power and its direction are not known. | Low to medium (meaning); the values are supported by evidence |
| G | Self-illumination (emission) mask | 0 nearly everywhere; non-zero only on things that glow: helmet eye lenses (Space Marine MP helmets 20-28 of 255, Chaos `basic` 28, `havoc_01` up to 121 with a soft halo, `berserk_03` 89), plasma coils, lascannon, melta and power-sword parts. The `diff_emis_*` shaders name an `EmissiveColor` (1,1,1 on all but one, `sm_armour_mp_dlc2_set1_helmet` 0,0.93,0) and no `EmissiveMap`: 25 of their 30 `_lp_lgchar` maps have G set. Whether `character_wargear` (no "emis" in its name, no `EmissiveColor`) applies it is not known. A few non-emissive cloth and grenade `_lp` maps are plain white placeholders (G = B = 255 everywhere). | Medium-high (mask); low (that the wargear shader uses it) |
| B | Reflection (environment map) mask | Marks the gold ornaments: on the armour it follows the paint mask's tertiary channel (correlation 0.77-0.92 with `_pnt` B, e.g. the Space Marine torso's eagle wings and skull, the Chaos studs and stars) with its own shading inside. All 43 `diff_norm_spec_reflect_lp_lgchar` maps have B set, and those materials name `ReflectionMap` = `sm_gold_ref` and a `ReflectionColor`. | Medium-high |
| A | Unused | 255 everywhere. | High |

**Other material fields.** `character_wargear`: `LightingModel` 255, `DrawOrder` 128, `SortBias` 0 (defaults); `TransScaleParams` and
`TransScaleParams2` (0,0,1,1) on 888 of 894 structs (six have (0,0,1.111,1)): a UV translate (x, y) and scale (z, w), identity in practice;
`DirtColour` (0.282, 0.216, 0.137) on almost all. `mp_custompaint` (the jump packs, backpack weapons and Raptor helmet and legs, names
`*_realtime_composite`, `*_onthefly_composite`): `DiffusePaint1..3`, `SpecularPaint1..3` (RGBA; alpha 0.78-0.96, meaning unknown) and a fixed
three-light rig (`LightColor0..2`, `LightPosition0..2` at +-10,000, `LightPower0..2` 1): read as a compositing pass that bakes paint into a texture
under its own lights (interpretation). `character_custom_preview` (one map material): `Colour1..3`, `SpecColour1..3` (alpha 0.5),
`SpecularPower` 40, `Rotation`, `ScaleX/Y`, `TranslateX/Y`. The paint colours' `specular` alpha is 0 in `custom_paint_colour` (unused); their
`colour_type` (`metallics` for the golds) is not used by the client.

**`DerivedSpecParams` (x, y, z, w)** on the `*_drvspec` shaders (specular derived from the diffuse; they have no `SpecularMap`): in all
2,206 materials x < y, both in 0..1, and **z = 1 / (y - x)** in 1,785 of them (to 1%); the other 421 are all one vector, (0, 0.6, 0.6, 1.75),
an untouched default whose z was never recomputed (28 others hold the recomputed (0, 0.6, 1.667, 1.75)). So x and y are a luminance window
and z its precomputed reciprocal: the derived specular is most likely `pow(saturate((L - x) * z), w)` of the diffuse's luminance L (after
`DrvSpecDesatAmount`, 0..1, desaturates the diffuse colour), times `SpecularColor` (w from 0.1 to 10: an exponent; it could also be a gain).
Confidence: the window and reciprocal high, w medium. Not applied yet (map materials, `client/MapMaterials.cs`).

**Paint gain.** The pre-painted `sm_armour_basic_001` set has the Ultramarine blue baked into its diffuse. On the primary plates (paint mask R
over 200) the median texel of the baked set is, in linear light, 3.1 x (the `sm_armour_mp_basic` grey's luminance x `ultramarines_blue`) in the
green and blue channels on the torso and helmet, 3.8-4.3 on the legs and pads (sRGB ratios 1.5-1.9; red is skewed by its small value). The client's
gain was 2.5 (by eye) and is 3.1 since M40-T13 (provisional; the two sets differ in layout and authoring). The singleplayer `csm_armour_basic` set is
painted in a different scheme and is no reference for the Chaos colours.

**Chaos eye glow.** No material carries it: each Chaos MP weapon that has one plays `csm_mp_eye_<kind>` (`master_fx`; `bolter_heavy`,
`meleehammer`, `lascannon`, `plasmacannon`) twice, attached at `fx_lefteye` and `fx_righteye`, in `action_data/while_deployed_actions` (stop
policy `stop_emitting`; some also replay it in `on_stow_actions`; the co-op and singleplayer thunder hammers and cannons use `lifetime_actions`).
In the 12 Chaos MP wargear files of the alpha's loadouts: bolter (and gold), storm bolter, sniper rifle, grenade launcher, melta, plasma gun,
heavy bolter, lascannon, plasma cannon, chainsword (and gold). The bolt pistol and every Space Marine weapon bind none, and Space Marine helmets
have no eye markers. The effects are two small additive rings, a "colour glow" (r about 0.15 m) and a "point centre" (0.02 m); their
streamer ("smoke") and beam are hidden (`Hide` True). Their colours are not the lenses' red: `FrameColour` (R, G, B) of the glow is
(33, 110, 255) for `bolter_heavy`, (24, 53, 255) for `meleehammer` (blue), (50, 15, 1) for `lascannon` (red-orange) and (50, 1, 105) for
`plasmacannon` (purple). The channel order is checked on effects of known colour read the same way (fire orange, blood dark red; spec
"Eye glow colours"). Implemented in M40-T13:
`SmContent.EyeGlowData` (bindings, helmet markers), `client/EyeGlow.cs` (per character), `client/NetworkView.EyeGlow.cs` (the match); the lens
colour tints them (ours, `FxEffect.Tint`).

## Weapon scatter, climb and sway (weapons/spread, 2026-10-28)

- **Where (Verified-local, buildid 7412325):** each firing ability's delivery (`ability_ranged_primary`, `_secondary`, a charge ability's
  `charge_states/<n>/delivery`, a pump ability's `pump_states/<state>/delivery`, the heavy bolter's `ability_ranged_braced`) carries
  `kick_back_and_scatter_values/{standing|aiming|crouching}/{idle|walking|running}`; each slot is an entry list (`_base_templates\climb_sway_scatter`)
  with `required_upgrade_name/required_list` and a `tuning_set` string `climb_sway_scatter\<name>`. Default entries are named `entry`; the stalker
  bolter's Burst Fire entry is named by its template, not `entry` (the reader takes any name). `crouching` is filled for most weapons but MP has no crouch.
- **The sets** live in `data/packages/climb_sway_scatter.oppc` (242 BAF entries, `attr_pc`), one per name, three blocks: `scatter`
  (`initial_horizontal_angle`, `initial_vertical_angle`, `increase_per_shot`, `max_horizontal_angle`, `max_vertical_angle`, `decrease_per_second`,
  `decrease_delay`, and `target_speed_to_scatter_horizontal_angle` / `_vertical_angle` tables, empty in every MP set), `climb`
  (`permanent_climb_angle_per_shot`, `temporary_climb_angle_per_shot`, `max_temporary_climb_angle`, `climb_rate`, `climb_recovery_rate`; `climb_rate`
  is stored as an int in some sets) and `sway` (`min/max_temporary_sway_angle_per_shot`, `sway_rate`, `sway_recovery_rate`, `max_temporary_sway_angle`,
  `min/max_permanent_sway_angle_per_shot`, `temporary_sway_increase_per_shot`, `temporary_sway_decrease_per_second`, `temporary_sway_decrease_delay`).
  No units are given; degrees fit the sizes (**interpretation**). Values per weapon: spec/behavior.md "Weapon spread".
- **Perk entries:** `mp_perk_weap_bolter_accuracy` (Bolter Targeter: `*_ranged_bolter_accuracy_perk` hip and aim), `mp_perk_weap_storm_bolter_hip_fire`
  (Improved Twin-Link: hip only), `mp_perk_weap_stalker_zoom_modes` (Burst Fire: `*_sniper_rifle_burst`, `_burst_moving`), `mp_perk_weap_las_cannon_zoom`
  (Lascannon Stabilizers: `*_las_cannon_handling_perk`, `_handling_perk_moving`: the scatter blocks equal the plain sets, only climb and sway differ).
  `mp_perk_weap_heavy_bolter_scatter` and `mp_perk_weap_bolt_pistol_scatter` select `_accuracy_perk` sets but no wargear or blueprint grants them
  (searched in `wargear` and `ebps`): unused here.
- **Oddities:** the MP melta's pump state names the singleplayer set `sp_sm_melta` (its unused `ability_ranged_primary_old` names `aim_sm_mp_ranged_melta`);
  several weapons' `crouching` slots name another weapon's set (plasma gun and plasma pistol: `sm_mp_ranged_bolter`); the grenade launcher's sets have no
  increase or decrease (a constant 1.3 × 0.65, aimed 1 × 0.5); the Chaos weapons name the Space Marine sets.
- **The original HUD:** `Hud.gfx` (ui_ingame) carries the names `g_reticules`, `CrosshairTypes`, `Reticule_Ingame_0_mc`, per-weapon idle/target reticule
  clips (`plasmaidle_mc`, `plasmatarget_mc`, `heavybidle_mc`, `stalker_idle_zoom_mc` ...) and `min_aim` / `max_aim`: evidence that the reticule had a
  size range; its script was not read (names only, from the decompressed movie's strings, in the private cache).

## In-game HUD art (M44, 2026-10-28)

- `data/packages/ui_ingame.oppc` (219 entries) holds the in-game Scaleform movies (`Hud.gfx` 143 KB, `HudText.gfx`, `PlayerDeathScreen.gfx`,
  `EndOfArena.gfx`, `PlayerList.gfx`, `ClassSelectionMenu.gfx`, `sharedhuditems.gfx`, `minimap.gfx`, `HudEventLog.gfx` ...) and their bitmaps as
  separate DDS (DXT5): the exporter's atlases named `<movie>_i<hex id>` (`hud_i2d`, `hud_i5d`, `hud_i7`, `hud_ie`, `hud_i4` 256 x 256 / 248;
  `hud_i176` 256 x 184; the bar pieces `hud_i203` 404 x 28, `hud_i215` 412 x 36, `hud_i20a` 404 x 28 (a yellow outline, use unknown),
  `hud_i223` 376 x 16, `hud_i1fd` 308 x 36) and named pictures: one 96 x 64 picture per weapon and item (`bolter`, `bolter_chaos`, `lascannon`,
  `vengance_cannon`, `frag_grenade`, `jumppack` ... 45), 96 x 96 perk and class icons (also in `ui_fe.oppc`), `grenadeactive` 28 x 32.
- What the atlases hold (by eye): the health bar's background, cyan glow and red fill; the medallion (cyan star); Chaos star, aquila,
  Ultramarines symbols; glowing skulls; medal icons (skull rows); clip-tick strips (white, red, black); score-bar shapes; the orange card frame;
  chevrons; the infinity sign; the lightning charge mark. The rectangles the client uses are measured by us (`SmContent.HudSprites`); the movie that
  places them is not read (its shapes and ActionScript are left alone).
- Fonts: `data/config/fonts.xml` names "XBourgeoix OT Medium" / "Bold Alternate" for `$Bourgeois` (English). The glyphs exist only as the font
  movie `ui_common_pc.oppc` `fonts_en.gfx` with its glyph atlas `fonts_en_f0.dds` (1024 x 512); the `.tfnt` files in `ui_common.oppc` are Exocet,
  Tahoma, Arial. Not used: the client draws Bahnschrift.
- Wargear `ui_data`: `icon_path` (a placeholder "weapon_bolt_pistol" on most weapons), `name` / `description` (locale ids), `reticule_type`
  (`bolter`, `bolt_pistol`, `lascannon`, `plasma`, `stalker`, `melta`, `grenade_launcher`, `heavy_bolter`, `storm_bolter`, `plasma_cannon` ...),
  `weapon_type` and `killed_by_weapon_type` (`laz_cannon`, `grenade_launcher`, `stalker`, `jumppack`, `stomp`, `bullrush` ...). The HUD maps
  `weapon_type` onto the picture names (ours).

## HUD indicators: world decorators and the kill feed (fidelity gap 10, 2026-10-28)

How it was read (the install is read-only; nothing from it is stored in the repository): the movies' tags were walked with a small throw-away
script (sprites, placements, frame labels, colour transforms, edit-text fields, DefineShape bitmap fills, the Scaleform sub-image records that
place each shape's bitmap on its atlas); the ActionScript was read only for its constant pools (names), never decompiled. The atlases are DDS
(DXT5) in the same packages. The tag walker is a throw-away script outside the repository (it printed names, frame labels, placements and rectangles only); the rectangles and labels it found are the tables in `SmContent/UiArt.cs` (`FeedIcons`, `DecoratorSprites`, `DecoratorRules`), which tests pin.

- **Where the movies are:** `data/packages/ui_common.oppc` holds `Decorator.gfx` (43 KB, 1280 x 720, 30 fps) with its atlases `decorator_i1`,
  `decorator_i3`, `decorator_i10` (244 x 256, 244 x 256, 232 x 236) and `decorator_i21` (336 x 136: the exporter's checkerboard placeholder, used only
  as a mask shape, never visible). `data/packages/ui_ingame.oppc` holds `HudEventLog.gfx` (4 KB: the kill feed) with the atlas `hudeventlog_i3`
  (248 x 248), `Hud.gfx`, `HudText.gfx`, `minimap.gfx`. The exported symbols of `Decorator.gfx`: `PlayerDecorator`, `PlayerDecoratorCoop`,
  `DominationPoint`, `DreadnoughtPoint`, `FlagDecorator`, `DeadPlayer`, `CapturePoint`, `AmmoPickup`, `GrenadePickup`, `Missile`, `CoopEnemy`, `CoopOrk`,
  `InteractiveObject`. The engine drives them through `Native_SetPlayerNameText`, `Native_SetDominationPointDecoratorText`, `Native_UpdateFlagDecorator`,
  `Native_SetupCoopDecorator`, `Native_SetupCoopEnemyDecorator`, `Native_RemoveDecorator` ... and the clips' own script reads parameters named
  `playerName`, `showInfo`, `healthPct` / `shieldPct`, `showRaceIcon`, `isSM`, `hasFlag`, `isInCombat`, `isLowHealth`, `isLowAmmo`, `mpClass`, `pointNumber`,
  `pointLetter`, `timeRemaining`.
- **Which entities have a decorator** (`ebps.oppc`, `entity_ext/decorator_type` and `decorator_occlusion_distance`, 824 blueprints): the player blueprints
  (`sm_space_marine_player`, `csm_chaos_marine_player`) have `none` and occlusion distance 75: the multiplayer player decorators are therefore not an
  entity decorator but added by the engine for players (the front end's host option "Player Decorators", locale 111765, is the button
  `show_hud_indicators_btn` of `HostOptions.gfx`; its values come from the executable). Entity decorators: `domination_point_decorator` (the Seize Ground
  banners `waaagh_banner_objective`, occlusion distance **1000**), `ammo_pickup_decorator`, `grenade_pickup_decorator` (75), `coop_enemy_decorator`
  (32 co-op enemies, 75), `flag_decorator`, `drop_pod_decorator`, `missile_decorator`, `explosive_object_decorator`, `interactive_object_decorator`,
  `tower_decorator`, `fighter_decorator` (1500), `dreadnought_point`. Every ordinary blueprint of the install carries 75 m (738 with `none`).
- **`PlayerDecorator`** (sprite 84): `player_name_mc` (a 100-frame clip labelled `close to camera` at frame 1 and `far from camera` at frame 100, but
  with no placement changes between them: the distance scaling is not in the timeline), whose `player_name_txt` is a 32 pt `$BourgeoisBoldAlt` field,
  default colour (252, 186, 31) = gold, 798 pt wide, centred on the bar below; `info_mc`: a dark double-pointed bar of 354 x 24 (`decorator_i10` 0,212,176x24
  mirrored) with `health_mc` on its left half (red fill pointing left, `decorator_i1` 52,224,176x24) and `armor_mc` on the right (yellow-white fill pointing
  right, `decorator_i10` 0,186,176x24); each is a 101-frame clip whose mask slides left by 1.8 pixels a frame (frame 1 full, frame 100 empty), with a 10-frame
  spark sprite at the fill's edge; `icon_mc` (labels `imperial` frame 1, `chaos` frame 2: the gold eagle 131 x 70 `decorator_i1` 0,152 and the gold Chaos star
  `decorator_i10` 0,0) at 1.3x; `flag_mc` (the CTF flag marker). The clip's script names `showInfo`, `healthVisible`, `iconVisible`, `showRaceIcon`.
- **`DominationPoint`** (sprite 158): `anim_mc` (27 frames, one label per state): `neutral`, `enemy_chaos`, `enemy_sm`, `ally_chaos`, `ally_sm`,
  `ally_sm_campaign`, `neutral_flash_ally`, `neutral_flash_enemy`, `enemy_chaos_flash`, `enemy_sm_flash`, `ally_chaos_flash`, `ally_sm_flash`,
  `ally_sm_combat_flash`, `enemy_chaos_combat_flash`, `neutral_flash_ally_combat`, `neutral_flash_enemy_combat`, `enemy_sm_combat_flash`,
  `ally_chaos_combat_flash`, three campaign variants, `coop_objective`, `coop_objective_optional`, `neutral_ctf`, `*_ctf`; `number_mc` (the point's number or
  letter, 35 pt black); `bar_capping_mc` and `bar_uncapping_mc` (each a 100-frame `bar_mc` with a 72 x 16.5 sliding fill at 30 % alpha and a 10-frame edge
  sprite, 0.57 x 0.67 scaled; the bar's own art is not in the data). The four owner icons are the same pictures as the player decorator's: gold eagle (ally
  Space Marines), gold Chaos star (ally Chaos), **red** eagle (`decorator_i10` 0,72) and **red** Chaos star (`decorator_i1` 90,78) for the enemy, each over
  a pointer triangle (`decorator_i3`: grey neutral 164,138, yellow ally 82,138, red enemy 0,138, 80 x 80). Text: "CAPTURE" (locale 106075, white, 18 pt)
  on the neutral frame and "DEFEND" (106076, #ffc000) on the ally Chaos frame. **Flash:** the `*_flash` frames are a 30-frame loop (1 s) that places the
  icon over the state's own icon at alpha 256, 239, 223, 208 ... 29, 27, 26 of 256 (a fast-then-slow fade to 10 %), the `*_combat_flash` frames a 10-frame
  loop (0.33 s) with alpha 256, 195, 143, 101, 68, 45, 31, 26; the steady icon sits at 128 / 256. `DreadnoughtPoint` has `neutral`, `ally_capping`,
  `enemy_capping` (the Dreadnought mode's own marker; not used here). The game's loading tip 122000: "If a capture point you own is blinking, that means an
  enemy is uncapping it" (the meaning of `ally_*_flash`); the score panel's objective markers (`HudText.gfx` `mp_score_mc`, `objective_marker1..4`) use the same
  state labels.
- **Other decorators** of the movie, not built: `DeadPlayer` (a 150-frame skull marker), `PlayerDecoratorCoop` (class icons with `neutral`, `combat`,
  `neardeath`, `dead` frames, an ammo warning), `CoopEnemy` (name-less health bar), `AmmoPickup` / `GrenadePickup` (the pickup icons), `FlagDecorator`
  (`attack`, `defend`, `escort`, `kill`, `return`: CTF / Dreadnought modes), `InteractiveObject`, `Position` (campaign).
- **The perk "Situational Awareness"** (locale 111546 / 111547): "Enemies nearby in LOS have a threat indicator in the gutter and their decorators always
  appear normally": enemy decorators are therefore conditional in the original (the condition is not in the data) and the perk lifts it; the gutter
  indicator would be the `Hud.gfx` compass side marker (not built).
- **Footage** (`hud-reference/video1`, t105): an enemy's name floats red over its head in the world ("LW Chakax", with the clan tag in the player's name), with
  no health bar and no icon seen; own-side objective markers carry the gold star / eagle, enemy ones the red eagle, with "CAPTURE" / "DEFEND" above.
- **`HudEventLog.gfx`** (the kill feed): three entries `logentry0..2_mc` (a movie clip placed at 849.5, 61.3 / 98.7 / 136.2 on the stage, scaled 0.864) with
  `killer_name_txt` (right-aligned, `$BourgeoisBoldAlt` 18 pt), `weapon_icon_mc` and `victim_name_txt` (left-aligned), and a `message_txt` (centred 22 pt, the
  "joined" line). `weapon_icon_mc` has 25 frames (frames 1 to 22 carry one label and one picture each; frame 23 places the dreadnought picture and frame 25 carries its label, so 23 labels and 23 pictures, of which the krak grenade reuses the frag grenade's bitmap mirrored: 22 atlas cells) labelled with the wargear `ui_data/weapon_type` values (`bolter`, `heavy_bolter`, `laz_cannon`,
  `plasma_cannon`, `plasma_gun`, `plasma_pistol`, `stalker`, `power_axe`, `thunder_hammer`, `knife`, `chainsword`, `storm_bolter`, `bolt_pistol`, `grenade`,
  `krakgrenades`, `melta`, `grenade_launcher`, `powersword`, `reactiveblast`, `jumppack`, `bullrush`, `stomp`, `dreadnought`): white 48 x 48 silhouettes with blood
  splatter, a 5 x 5 grid of 50-pixel cells on `hudeventlog_i3`; the knife is drawn flipped vertically and the krak grenade as the frag grenade flipped
  horizontally. In the wargear files `killed_by_weapon_type` is empty on every ordinary weapon and names the feed type on the perk blast files
  (`mp_perk_jump_pack_*` -> `jumppack`, `mp_perk_shield_break_damage` -> `reactiveblast`, `mp_perk_better_stomp` -> `stomp`, `mp_perk_friendly_spawn_target` ->
  `bullrush`) and the dreadnought weapons; `sm_mp_core_abil_heavy_evade` has `weapon_type` `stomp` and `sm_mp_core_abil_jump_evade` `bullrush` (the charge).
- **Not found:** the compass or minimap in versus (`Hud.gfx` has `compass_mc`, `minimap.gfx` waypoint / player icons: whether the versus HUD shows them is
  unconfirmed); the values of the "Player Decorators" host option; the distance rule, the occlusion test and the enemy condition of the player decorators
  (executable code); the capture bar's art and colours (a runtime texture).

## HUD movies read in full (ui/hud-fidelity, 2026-10-29)

`SmFormats.Gfx.GfxMovie` now also reads text fields (DefineEditText: rectangle, font id and height, colour, alignment, leading, initial text), font names
(DefineFont2/3 and ImportAssets: the HUD movies import `$Bourgeois` and `$BourgeoisBoldAlt`) and which tag defined each character id; `GfxRaster.DrawMasked`
applies clip layers and draws images placed directly as characters (tag 1009 ids on a display list); `GfxRaster.Bounds` gives a frame's drawn box.
`smtool gfx-dump <game-dir|auto> [--movie Hud|HudText|HudEventLog|Decorator] [--package common] [--grep word] [--frames sprite] [--path a/b --scale s --frame n [--top-frame] --out dir]`
prints every sprite's frames, labels and named placements (text fields with their font and size) and renders a placement into PNG files outside the repository.

- **`Hud.gfx`** (1280 x 720): the root places `Reticule_Ingame_0_mc` (639.2, 356.85: every reticle inside it is centred 0.8 to 1.0, 3.1 to 3.5 px below and right of its
  origin, i.e. on the stage centre) holding one sprite per weapon family and state (`boltpistol_mc` / `_active_mc`, `idle_mc` / `enemy_mc` for the bolter,
  `heavybidle_mc` / `heavybtarget_mc`, `plasmaidle_mc` / `plasmatarget_mc`, `plasmapistol_mc`, `plasma_cannon_mc`, `PlasmaCharge_mc`, `stalker_idle_mc` / `_active_mc`
  and the two `_zoom_mc` scopes, `grenade_neutral_mc` / `_active_mc` with `grenade_rounds_mc`, `melta_mc`, `storm_bolter_mc`, `autocannon_mc`, `lascannon_scope_mc`,
  `reticule_friendly_mc`, `default_mc`, `hit_mc`). The bolt pistol's, bolter's and storm bolter's rings hold a nested `crosshair` clip of four ticks that walk outward
  over 25 frames (labels `min_aim` 0, `min_normal` 2 / 1, `max_normal` 24 / 14, `max_aim` 24 on the bolter); the ring itself is a separate shape at 35 % alpha. The
  heavy bolter's and plasma reticles carry `alt_fire_fade` / `alt_fire` / `normal_fade` clips. `hit_mc` is one shape faded over 10 frames.
  `weaponInfo_Slide` (1054.45, 522; 12-frame slide-in) holds the weapon box: `frame_gren_mc` / `frame_nogren_mc`, `loadout_mc` (the grenade column `grenade00` .. `04`
  with `blind_grenade_mc` / `stimpack_mc` / `grenade_mc` each, the empty `gun_mc` holder the game loads the weapon picture into), `ammo_mc` (`ammo_count_mc`,
  `ammo_count_bolt_pistol_mc`, `_16_mc`, `ammo_count_heavy_mc`: masked tick strips over 100 frames, frame 0 full; the `ammo_txt` count), and one display per family
  (`ammo_plasmagun_mc` with a purple Chaos twin, `ammo_lascannon_mc` tinted red-orange, `ammo_meltagun_mc`, `ammo_stormbolter_mc`, `ammo_autocannon_mc`, `Ammo_Stalker_mc`
  with ten rounds, `loadout_grenaderounddisplay_mc`, `infinity_mc`). `score_mc` (549, 153) holds four reward lines (`xp_event_1_mc` .. `4_mc`, 32.65 px apart: the
  experience, four reward icons from a 30-frame sprite labelled `mp_bonus_*` / `player_track_*`, the name; their clips stamp them in from 27 x and white).
  `unifiedhealth_mc` (463.05, 65.75 at 0.9) holds the armour and health bars as 110- and 100-frame masked fills (frame 0 full) and the Iron Halo's flash; drawn from the
  movie (ui/hud-2): `ironhalo_mc` (100 frames) is the cyan armour bar with the starburst medallion (frames 0 to 14 the flash, 15 the whole bar, 16 to 99 the cut bar), `lightdamagebar_mc` (110 frames, a `fade` label at 99) the same bar in gold, `healthbar_mc` (100 frames, frame 0 full) the red health bar; the placement is flipped on its y axis and sits where the footage has it. The scope's `crosshair` clip (sprite 135, 40 frames) is a **DefineMorphShape2** (tag 84) walked by placement ratios: the reader draws morph shapes (`GfxMorph`). `context_button_mc` places two imported button symbols (not in the movie).
- **`HudText.gfx`**: `mp_score_mc` (20.65, 538.95) is the versus score panel (text fields `marine_kills` 20 px gold 255, 204, 0, `chaos_kills` red 255, 51, 0,
  `kill_number` 35 px, `mp_timer_txt` 24 px gold; bars `sm_bar_mc` / `chaos_bar_mc` 105-frame masked fills; `objective_marker1` .. `4` with the 17 point-state labels;
  side emblems `team_1` (hidden) / `team_2` that place whole images); `Prompt_mc` (385.25, 604.1) the interaction prompt (a band, `Objective_txt` 24 px centred, an icon
  clip with `equip_ammo` / `equip_grenades` labels, an imported button; it slides up 40 px and fades in over 19 frames); `countdown_mc`, `winlose_mc`, `Event_mc`.
- **`HudEventLog.gfx`**: three entries (`logentry0_mc` .. `2_mc` at 849.5, 61.25 / 98.7 / 136.2, 0.864): `killer_name_txt` right-aligned 18 px, `weapon_icon_mc`
  (added), `victim_name_txt` left-aligned, `message_txt` 22 px centred.
- **`Decorator.gfx`**: the point decorator places `anim_mc` (27 labelled frames: the grey, gold or red pointer triangle, the gold or red eagle or Chaos star, campaign and
  CTF states; added, at 0.5), `number_mc` (`number_txt` black 35 px) and the capture bars; drawn 1:1 it is 40 stage px wide, the footage's size.
- **Reference footage** (a 640 x 360 copy of a 2011 match video; frames in the private cache only): the reticles' ring radius (36 px of 1280) and the hit marker's
  place match the movie; the kill feed's newest line enters at the top and lines live about 4 s; the "+50 [skull] Kill" line, the score panel, the weapon box
  (1.12 times the movie's, a perspective the movie does not have) and the prompt were compared side by side (spec "HUD from the movies (ui/hud-fidelity)").

## Ork sounds and the menu and Seize Ground events (Ork O8, fidelity gap 9; 2026-10-28)

Read-only investigation of the install (app 55150); nothing stored in the repository. `smtool ork-sounds <game-dir|auto>` prints the Ork half.

- **The boy's blueprint** `ork_coop_boy` (`data/packages/ebps.oppc`): `audio_ext/character = "ork_boy_2"` (`unit_type "ork_boy"`); 58 `post_sound_event` and 12 speech commands under `entity_actions_ext/triggered_actions`
  (81 tags), `health_ext/death_action` (`play_or_horde_bodyfall`, `stop_or_boy_02_ambient_speech`, `play_set_state_death_flesh`), `health_ext/stunned_action` (exertion `stunned`) and
  `biped_locomotion_ext/finish_falling_actions` (`play_or_land`). Many tags are the campaign AI's cinematic hit sequences (`bolter_hit_*`, `cin_bolter_hit_*`: `play_bg_bolter_hit_sequence`, no voices
  in the loaded banks). The boy rig (`havok_boy_coop__hkxx`, 25 behaviour graphs) carries 47 action tags on 137 clips: `EVT_Left_Foot` (204 clips), `EVT_Right_Foot` (200), `ACTN_SND_Vox_Nav` (112),
  `ACTN_SND_Vox_Roar_M_S` (23), `ACTN_SND_Vox_Grunt_M_S` (11) and the others in `smtool ork-sounds`.
- **The boy's wargear**: shoota `play_or_shoota_fire` and six weapon-handling tags (`play_ork_gun_*`), slugga `play_or_slugga_fire`, choppa `play_or_choppa_swing` / `play_or_hand_swing` / `play_or_gear_move_medium` and an
  `effort_short_stab` grunt at the start of every melee ability, stikkbomb a throw line only (speech action `start`/`high`: no line in the speech tree), the dodge hop nothing. The choppa's swing is bound twice: `start_audio`
  of `ability_melee_light` and the `actn_fx_swing` tag.
- **Speech**: the `speech_exertion` dialogue tree of `speech_01` has a line for `ork_boy_2` for celebration, roar, effort_short_stab, death, damage_small, damage_big, damage_long, stunned and effort_big_pull
  (0.48 to 3.18 s); the speech actions of the reload and the stikkbomb have none.
- **Seize Ground**: `play_mp_point_being_captured` (wem 43040138, 36 ms, 2 channels, 44.1 kHz) is a click, not a loop, with `stop_mp_point_being_captured`; `Play_Objective_Complete` is one streamed 2.78 s sound;
  `Play_MP_Points_Total` an 88 ms tick at -20 dB with a low-pass (the HUD movie posts it with `Stop_MP_Points_Total` for the XP tally); `play_mp_point_captured` and `play_mp_bonus` exist and share a wem. The scripts that post them
  (`multiplayer.lua`: `Sound_PlayObjectiveCompleteNoise`) are compiled; the moments are not in the data.
- **Menus**: the front-end movies in `ui_fe.oppc` (`MainMenu.gfx`, `OnlineMenu.gfx`, `MultiplayerLobby.gfx`, `LobbyCustomizer.gfx`, `TeamSelectMenu.gfx`, `HostOptions.gfx`, `SearchSelection.gfx`, `OptionsMenu.gfx`)
  hold `funcPlaySound` / `PostSoundEvent` and the strings `Play_Menu_Move`, `Play_Menu_Select`, `Play_Menu_Back` (all of them), `Play_Menu_Expand_Main`, `Play_Multiplayer_Select`, `Play_Cooperative_Select`,
  `Play_Network_Select`, `Play_Campaign_Select` (a few). Move, Select and Back are in `In_Game_Default` with embedded media (-12, -9 and -6 dB, 4, 7 and 3 KB, "max 1 global"); `Play_Multiplayer_Select` plays a 281 KB streamed
  sound of `FE_UI`. The `FE_UI` bank has 38 events, 33 of them with unknown names (music and ambience); `ui_fe.pck` is empty.

## Death effects (F-D1, 2026-10-29)

Question: which effects does the original multiplayer data itself bind to a marine's or Chaos marine's death (faithful content; severing limbs would be the gore extras)? Read with `smtool baf-dump` (`--entry`, `--under`, `--grep`; the grep matches
string values, not table names) and `smtool graph-dump SM_Base_MP`, the effect structures with a throw-away reader of `FxLibrary` (kept out of the repository). Rows are Verified-local (this install, buildid 7412325).

| Effect | Bound where | Condition |
|---|---|---|
| `plasma_limb_vanish` x12, `plasma_dmg_burning_body` x1 | ebps `sm_space_marine_player` and `csm_chaos_marine_player`, `extensions/entity_actions_ext/triggered_actions/actn_plasma_death/commands` (two `play_fx` sets of six, one inside a `delayed_action` 1.7 s; markers `rig_l_hand`, `rig_r_hand`, `rig_l_calf`, `rig_r_thigh`, `rig_spinea`, `rig_head`; the burning body attached at `rig_spinea`; a `remove_wargear` of the ranged slot inside a `delayed_action` 1.2 s) | a death by a plasma weapon (see "The trigger") |
| `meltagun_dmg_vaporize_limb` x7 | same files, `actn_melta_death/commands` (markers `rig_head`, `rig_chest`, `rig_spine1`, `rig_rthigh`, `rig_lcalf`, `rig_rhand`, `rig_lhand`) | a death by the melta gun |
| `jumppack_death_mp`, `jumppack_dmg_burn_limb` x4, sound `play_jump_pack_explode_sm`; then `jumppack_land_death_mp`, sound `play_jump_pack_deathfall_impact` | wargear `sm_mp_abil_jumppack` / `csm_mp_abil_jumppack` (`cs_` effects, `play_jump_pack_explode_ch`), `action_data/triggered_actions/actn_jumpdeath_start` and `actn_jumpdeath_impact` | a death in a jump (the graph's `BOOL_JumpIsActive`): the start at the death, the impact when the fall reaches the floor |
| `jumppack_predeath` (immediately, sound `play_final_vengeance_timer`); after 2.5 s `jumppack_death_mp`, `jumppack_dmg_burn_limb` x4, `jumppack_land_death_mp`, sound `play_final_vengeance` and the blast's two `area_of_effect`s | ebps player blueprints, `extensions/health_ext/death_action/commands/actions_by_upgrade/actions/entry` requiring `mp_perk_jump_pack_explodes_on_death` | the perk Final Vengeance (the simulation's M43 death blast, `PlayerUpgrade.DeathBlastDelay` 2.5 s) |
| (pickups, sounds) | the same `death_action`: `random_actions` (the ammunition and grenade drops: fidelity gap 1), `play_kill_shot_mp`, the health-stage and shield-warning stops (sound system) | every death |
| none | `health_ext/ragdoll_death` = True (a boolean, `DeathTuning`); `death_actions_special` is empty on the players (on the Ork boy it holds a head-shot entry) | — |
| none found | `decal_stain_blood_*`: not a string in ebps, wargear or `material_effect_container`; the blood decals of the MP hit containers are `blood_exit_wound_decal`, `plasma_dmg_body_exit_wound_decal` (`blood_splats_dif`), already played by M38. `body_death`, `blood_death`, `body_dismemberment*`, `bloodletter_limb_vanish` belong to the Ork and Chaos AI blueprints only | — |

Weapon files bind none of this: the plasma gun's `do_damage` has `dismemberment_type = ""`, and a `trigger_reaction` with `reaction_type` `rng_plasma_gun_weak` (`strong` for the charged shot); the melta's are `rng_melta_short`, `_med`, `_long`.

**The trigger.** No file names `actn_plasma_death` or the others as a value: the engine runs triggered actions by name. The player behaviour graph `SM_Base_MP` (`graph-dump SM_Base_MP`) has the answer for the engine's side: its
`DEATH` state ("Behavior Death") holds the sequencers Default Death (bolter clips `SM_REC_MP_BolterDeath_F_01` and `_B_01`), Headshot, Sprinting, Bullrush, Ranged Left / Right / Back, **Plasma Death** (`SM_REC_MP_PlasmaDeath_01`, 2.267 s, tag `Event StartAction` at 0.0147),
**Jumppack Death** (start `SM_REC_MP_MidAirDeath_Hit_01` 0.6 s with `StartAction` at 0.0333 and `BRANCH_Jump_Death_Start` at 0.9412; fall `SM_REC_MP_MidAirDeath_Fall_01` with a `FloorCollisionTest` tag and `ACTN_JumpDeath_Fall`; hit floor `SM_REC_MP_MidAirImpact_01` with `StartAction` at 0.04),
Lascannon Deaths and **Melta Death** (four `SM_REC_MP_LasDeath_*` clips, `StartAction` at 0.05 and 0.0588 on the front and back). The transitions from ROOT into DEATH carry `BOOL_IsDead` and a branch: `BRANCH_Headshot` (priority 9), `BRANCH_Las_Death` (8), `BRANCH_Melta_Death` (7), `BRANCH_Plasma_Death` (5), `BRANCH_Death_Ranged_Left` / `_Right` / `_Back` (4, 3, 2), the sprint (6) and `BOOL_JumpIsActive` (10). The branch tags sit on the hit-reaction sequencers of each weapon's reaction
(`PORT_rng_plasma_gun_weak` and the other plasma ports carry `BRANCH_Plasma_Death`, the melta's `BRANCH_Melta_Death`). Each death clip ends with `Powered Ragdoll Controls Modifier` tags (0.45 to 0.9 of the clip): the original plays a short death animation and then powers the ragdoll.
Binding the `StartAction` events to the triggered actions of the same name is our reading (**interpretation**; nothing in the files states it). The tag times all come to 1/30 s after the state starts (0.02 s for the jump start).

**The effects themselves** (read through `FxLibrary`): `plasma_limb_vanish` is a lightning spray, a bloom and bolt ring, crawling electricity, a flash ring, ash sprays (`paper_texture_page_00`), sparks, smoke, a light and a fire dropper; `meltagun_dmg_vaporize_limb`
is a flash, a fire ring, ash and spark sprays, smoke, a light and a fire dropper (durations 0.1 to 7 s); `jumppack_death_mp` is sparks, a flash, a lit skirt of smoke and a body of billowing smoke; `jumppack_dmg_burn_limb` is a streamer, flames and smoke; `jumppack_land_death_mp`
is a dropper whose collision content holds a crack and a crater decal, smoke and debris (like the pound's). None has a mesh element or a hide of anything: they are particle effects. **The elements of `plasma_limb_vanish`, `jumppack_dmg_burn_limb`, `jumppack_death_mp` and
`jumppack_predeath` all have `Duration` 0**, which the effect player reads as one frame (1 ms clamp): a flash that is not seen. Their `play_fx` names `length` 1 (0.6 for `jumppack_death_mp`); reading a duration of 0 as the play's length makes them look as meant (blue lightning, ash and smoke for the plasma vanish; checked in `FxTest --fx-length`).

**Markers.** The data's names are the marine rig's bones lowercased (`rig_l_hand` = `RIG_L_Hand`, `rig_spinea` = `RIG_SpineA`, `rig_pelvis`, `rig_head`, `rig_l_upperarm`, `rig_root`), except in the melta death: `rig_lhand`, `rig_rhand`, `rig_lcalf`, `rig_rthigh` (underscores missing) and `rig_chest`, `rig_spine1` (not in the rig at all; `smtool graph-dump --bones`).
`trigger_point` is `other` for the plasma, melta, burning body and pre-death effects and `self` for the jump deaths; for the Chaos Bloodletter's `actn_death_vanish` (`bloodletter_limb_vanish`, a demon that dissolves) it is `other` too, so `other` is read as the dying body's own bones.

**Left open.** Whether the plasma and melta deaths visibly removed a limb or the body in the original (the graph's death clips are short and the corpse is a powered ragdoll); `remove_wargear` suggests the corpse's gun vanished. The sprint, headshot, lascannon and bullrush deaths bind no effect.

## Guardsman (Musou Mode dependency, 2026-11-01)

Musou Mode (added content) wants Imperial Guardsmen allies drawn with the original model and clips. **Original assets loaded**
(`client.cmd --guard-test`, a windowed still per angle; `client/GuardsmanTestView.cs`). The usable Guardsman is **not singleplayer content**: it is the
Exterminatus co-op Guardsman, in the co-op sets `data/chunks/ebp_sets__mp_coop_set_sm.oppc` and `ebp_sets__mp_coop_set_factory.oppc` (same entries). The
campaign has its own copies and variants (`ebp_sets__guardsman`, `ebp_sets__mixed_core_units` with the renegade guard and `chaos_lasgun`, the
`guardsman_cinematic*` / `guardsman_ingame_*` head packages and the 13 KB rig `havok_guardsman_cinematic`); Musou does not need them.

| Piece | Entry (co-op set) | Status |
|---|---|---|
| Blueprint | `ebps` `ig_coop_guardsman` (also `_shotgun`, `_chainsword`, `_grenade_launcher`): visual `guardsman_a`, animation package `havok_guardsman_a_coop` (project `guardsman_a`), wargear `guardsman_basic_head`, `ig_coop_lasgun`, `sm_coop_evade`; `health_max` 85, run 6.25, sprint 9, walk 3.6 | read (`smtool baf-dump data/packages/ebps.oppc --entry ig_coop_guardsman`) |
| Body | `guardsman_a.dcm` (2 skinned blocks of material `ig_body`, 33 bones; block 1 is the far level of detail by the object tree) | decodes, drawn |
| Head | `guardsman_head_basic_001.dcm` (1 skinned block, `ig_head`, bones `RIG_Head`, `RIG_Neck`, `RIG_Chest`); `guardsman_ingame_d`, `guardsman_cinematic_d` (`IGHead_D`) are the other heads in the set | decode, `_001` drawn |
| Other bodies | `melta_guard_body` (4 blocks: static, 2 skinned `ig_body`, 1 skinned `Body_Mira`), `storm_trooper_body` / `_head`, `psyker` | `melta_guard_body` decodes; the others not checked |
| Rig | `havok_guardsman_a_coop__hkxx` (1.66 MB): 47 bones (`RIG_Root` ... `RIG_Prop_RHand`, `RIG_Prop_LHand`, `RIG_Head`), 148 clips, 6 behaviour graphs (`IG_Base_Coop`, `IG_RANGED_REACTIONS(_CS_Coop)`, `IG_Import_REACTIONS(_CS_Coop)`, `IG_IMPORT_FLINCH`), a ragdoll instance and 2 skeleton mappers. Every bone of the body and head meshes is in the skeleton | read (`smtool graph-dump --list|--animations|--bones --archive data/chunks/ebp_sets__mp_coop_set_sm.oppc --entry havok_guardsman_a_coop__hkxx`) |
| Lasgun | `lasgun.dcm` (1 static block, material `Standard_20`, textures `lasgun_*`), wargear `ig_coop_lasgun` (visual and package `lasgun`; raycast, damage 1, clip 40, `weapon_2hand_range_light`, effects `lasgun_tracer` / `lasgun_muzzle` in `master_fx`) | decodes; the wield entry puts the model's `rig_prop_m_chest` node on the rig bone `RIG_Prop_M_Chest`, read by `GuardLasgun` (TF7a) and `--guard-test`: in both hands, pointing forward; `fx_muzzle` at (-0.327, 0.394, 1.518) in the model's root |
| Materials | `ig_body.bmat`, `ig_head.bmat`, `IGHead_D.bmat`, `Standard_20.bmat` and their `_dif` / `_nrm` / `_spc` / `_lp` textures, all in the set | 3/3 drawn surfaces with the original diffuse |

The multi-material `guardsman_cinematic` that M24 recorded as the one failing package mesh reads since the bone-palette fix (Ork O1): 1 block,
`GuardsmanHead_003` (palette 38) and `ig_head` (palette 2). No reader change was needed for the Guardsman.

Clips (lengths from the rig; all bind except the six additive flinches): idle `IG_NAV_Idle_01` 3 s (also `_02` 8.33 s, `_03` 3 s, `_Tense_01` 4 s,
`_Relax_02..04`, kneeling aims); aim idles `IG_NAV_Idle_AimDown|AimForward|AimUp` 1.5 s; walk `IG_NAV_Walk_F|B|L|R_01` and the aimed `_AimDown|Forward|Up`
1.33 s; run `IG_NAV_Run_F|B|L|R_01` and the aimed sets 0.867 s; sprint only in the chainsword (`CS`) set `IG_CS_NAV_Sprint_F_01` 2 s; shoot
`IG_RNG_Fire_S_01` 0.167 s (additive recoil); reload `IG_RNG_Reload_01` 3.2 s; grenade `IG_RNG_GrenadeToss` 2.33 s; evades `IG_NAV_Evade_L|R_3M|6M_01`
1.87-2.3 s; hit reactions `IG_REC_Light_*` 2 s (8 directions), `IG_REC_HVY_*` 2-2.47 s (8), `IG_REC_BlownAway_F|B_6M|15M` 1.4-2 s, get-ups 2.7 / 3.2 s;
flinches `IG_REC_Flinch_High|Mid|Low_L|R_01` (additive: **not bound** by our reader, "track 47 maps to bone 47 of 47", one track more than bones);
deaths `IG_DIE_Basic_01` 0.4 s, `_03` 0.6 s, `IG_DIE_FallForward_01` 0.467 s, `IG_DIE_Headshot_01` 0.533 s, `IG_DIE_Explode_01` 3 s, `IG_REC_Death_02`
2.233 s; melee `IG_MEL_Attack_F_01` 2 s and the chainsword guardsman's `IG_CS_MEL_*`.

`lasgun_tracer` (`smtool fx-dump auto lasgun_tracer`): two beams are not hidden. `mycore` (additive, `laser_beam`, 0.5 s, width 0.039 to 0.059) goes pink (244,92,105), red (238,29,4) by 5% of its life and almost black (2,12,2) by 36%. `mycore_alpha` (alpha, `laser_beam_alpha`, 0.4 s, width 0.079 falling to 0) peaks pink (218,85,94, alpha 180) at 8%, turns teal (11,115,103, alpha 134) at 17%, dark blue (8,6,81, alpha 86) at 30% and fades. So a shot is pink-red first and a thin blue-teal thread after about 0.1 s. Both looks in our stills are the data's curves at different ages (read with keys as fractions of the duration, the renderer's provisional reading), not a bug. `lasgun_muzzle`'s light is (255,115,168), the pink glow on the floor.

Done in TF7a: `GuardRig` (profile, clip table), `GuardLasgun` (the mount from the data, no table of ours), the crowd (`GuardCrowd`). Left: the rig's graphs are not read, so the clip choices are ours;
the ragdoll (`havok_guardsman_a_coop`, 16 bodies, not read yet); the six additive flinches' extra track.

## Musou Mode pass 2: asset spike S0 (2026-11-01)

Read with `smtool list`, `fx-dump`, `audio-list`, `audio-event`, `baf-dump`, `nav-stats`, and `DcmMesh.Read` and `MapContent.Load` from a throwaway program outside the repository. Nothing was drawn or played: "decodes" means the reader took it, not that it looks right in the client. No sound was decoded (`audio-decode` may fetch ww2ogg's codebooks); "resolves" means `audio-event` reached .wem ids.

**The Stormbird is the Thunderhawk.** The data has no "stormbird" in any entry name; the owner confirmed (2026-11-01) that the Thunderhawk stands in for it.

**Singleplayer-content dependency (scope note, CLAUDE.md):** item 13's aircraft needs campaign content, loaded at runtime from the player's install. `thunderhawk.dcm` is in no multiplayer archive: there is no `data/packages/thunderhawk.oppc`, and the mesh sits only in campaign chunks (`ebp_sets__s002-001`, three `part_05__*` chunks). `play_thunderhawk_pass` plays only with a campaign region bank loaded. The rest of the table is multiplayer or shared content.

| Need | Asset | Where | State |
|---|---|---|---|
| Aircraft mesh | `thunderhawk.dcm`: 15 blocks (1 skinned), 15,048 triangles, bounds 34.1 x 35.5 x 19.0 (m, as read); materials `thunderhawk*`; rig `havok_thunderhawk__hkxx` | chunk `ebp_sets__s002-001` (singleplayer) | decodes; materials and rig not loaded in the client |
| Aircraft textures | `mtl_thunderhawk_{detail_dif,nrml_nrm,spec_spc}` also at a larger size | `mp_tdm_17__r000_main` (multiplayer; no mesh there) | present, not read |
| Blueprints | `thunderhawk` (`visual_package` thunderhawk, `graph_package` havok_thunderhawk), `sm_vehicle_thunderhawk_player`, `sm_turret_thunderhawk_{front,wings}` | `ebps` | read |
| Side guns | `hbolter_thunderhawk_front.dcm` (732 tris), `hbolter_thunderhawk_wings.dcm` (580 tris) | `data/packages` | decode |
| Engine FX | `thunderhawk_jet`, `thunderhawk_jet_distortion`, `valkyrie_jet` | `master_fx` | load (`fx-dump`) |
| Tracer FX | `heavy_bolter_thunderhawk_tracer` (1.2 s) | `master_fx` | loads |
| Explosion FX | `artillery_explosion` (0.3 s), `oob_explosions_artillery(_less)`, `oob_explosion_huge` (the last three looping: the client must stop them) | `master_fx` | load |
| Ambient FX | `amb_anti_aircraft_fire_valkyries` (looping) | `master_fx` | loads |
| Gore FX | `blood_death`, `body_dismemberment{,_head,_limb}`, `blood_hvy_bltr_dmg`, `blood_exit_wound_decal` | `master_fx` | load |
| Decal textures | `metal_crater_dif`, `blood_splats_dif` | `master_fx` | present, not read |
| Drops | `pickup_mp_drop_ammo` and `pickup_mp_drop_grenades` both draw `ammocase_bolter_small.dcm` (0.90 x 0.66 x 0.39, 324 tris) | `ebp_sets__mp_versus_set` (multiplayer) | decodes |
| Crates | `ammocase_bolter_healthy.dcm` (1.37 x 1.97 x 1.96, 2,318 tris) | `ebp_sets__mp_versus_set` | decodes; the nearest fit, see below |
| Capture points | A `mkr_objective_1` (110, -785, 4), B `mkr_objective_2` (200, -800, 0), C `mkr_objective_3` (290, -815, 4); cylinders, r 15, 13.5, 15 (scale 30, 27, 30), height 20 | `MapContent.ControlPoints` on `mp_tdm_09` | read; agrees with the design |
| Start areas | marines (245, -937.4, 5.5), Chaos (154.5, -664.4, 5.6) | same | read |
| Navigation | our `NavGraph`: 104,929 cells (Marines), 105,071 (Chaos), the bots' graph, built or read by `nav-stats`; two `mp_tdm_09-musou-*.nav` in the private cache | private cache | exists; not rebuilt with the hub seeds M1 needs |

The weapon crate proper, `case_weapon.dcm` (blueprints `pickup_weapons`, `pickup_weapons_new`), is only in campaign chunks (`part_0*`), so a crate uses the multiplayer `ammocase_bolter_healthy` unless another singleplayer dependency is accepted. `weapon_drop` was not found as a mesh in any archive. The grenade box `ammocase_grenade_healthy` is campaign-only too, but multiplayer draws its grenade drops with the small case anyway.

**Sounds.** `in_game_default` (in `AudioCatalog.BasePackages`) names many events whose sounds live in other banks; the list below says which bank made each resolve.

| Need | Event | Banks | State |
|---|---|---|---|
| Flyby | `play_thunderhawk_pass` (the blueprint's `flybysound`; 3 wems) | sounds in `rgn_s27_r500` (singleplayer) or `animation_test_grid`; not in the default set | resolves only with one of those banks |
| Flyby, no extra bank | `play_ig_valkyrie_flyby`, `play_valkyrie_s13_flyby` | `in_game_default`, `vehicles` | resolve in the default set; the nearest alternative |
| Engine | `play_sm_thunderhawk_engine` / `stop_` (the blueprint's `thunderhawk_flying`) | event in `in_game_default`; sounds not in the default set, `ambiences_ram` or `vehicles`; also listed in `rgn_s27_r500`, `rgn_s28_r000`, `animation_test_grid` | unresolved in the default set; `rgn_s27_r500` not tried for it |
| Heavy bolter | `play_sm_heavy_bolter_fire`, `_single` | `in_game_default` (bank `weapon_heavy_bolter` holds the weapon's sounds) | resolve in the default set |
| Rounds hitting | `play_heavybolter_valkyrie_hits` | `in_game_default` | resolves in part (2 of its targets missing) |
| Explosion | `play_big_damage_large_explosion`, `play_sm_missile_explosion`, `play_powersource_explode_distant` | `in_game_default`, `explosions` | resolve |
| Far explosion | `play_ork_rokk_distant_explosion`, `play_impact_distant` | `in_game_default`, `explosions` | resolve |
| Near explosion | `play_huge_explosion_near_stereo` | event in `in_game_default`; its 3 targets not found in the default set, `explosions`, `vehicles` or `rgn_s27_r500` | **missing**; use `play_big_damage_large_explosion` |
| Artillery | none: no named event says artillery, mortar or bombard | | **missing**; the explosions above stand in |
| Whistle | none: no named event says whistle or shell in flight (`play_shell_*` are the campaign's cannon loader) | | **missing**; nearest `play_droppod_incoming` (`vehicles`, 3 wems), else the design's falling-pitch treatment of a bank sound, tagged |
| Ambient war | none named: the 82 named `play_*` of `ambiences` are machines and set pieces (`play_titan_footstep` the only distant-war candidate); `mp_tdm_09` has 9 events, no ambience; land vehicles en masse have no event | | **missing**; build it from the far explosions, the Valkyrie flybys, `play_sm_heavy_bolter_fire` and the anti-aircraft FX, low-passed |

Unnamed events remain (`in_game_default` 1,232 of 2,129, `ambiences` 375 of 493), so "none" means none among the names known: a whistle or a war bed may sit unnamed. The region packages `air_rgn_*` hold sounds with no events and were not opened.

## Musou Mode pass 2: the hub, the Thunderhawk's interior (2026-11-01)

Owner's item 29: the hub is the inside of the campaign Thunderhawk. **Singleplayer-content dependency (CLAUDE.md scope note):** the model is in a campaign chunk, read from the player's install at run time; an install without it draws the generated shell instead (`TfwMapView`). The chunk has no collision, so `TfwShip` generates it from measures.

| Piece | Where | State |
|---|---|---|
| Model | `thunderhawk_interior.dcm` (blueprint `thunderhawk_interior`, object tree of the same name) in `data/chunks/part_01__s_002_r001_new_greybox_1.oppc` (12 MB, loads in about 0.3 s with its textures) | **original asset loaded**: 15 static blocks (no skinned one), 28 surfaces, every diffuse map present (43 KB to 1.4 MB DDS) |
| Placement | the object tree's nodes: every block sits at the origin of its node except `GEO_monitor` (to (0, 19.58, 5.27), hanging from the roof) and `GEO_rearGate_releaseHandle` (to (2.16, 21.84, 3.53)); `ModelSpace(node)` applies them | read |
| Door | block 0 `valk_Int_rearDoor`, node `GEO_hawkInt_rear_door` under bone `RIG_Frt_Dr` (hinge (0, 22.162, 1.942)); closed in the mesh (a slab from the hinge up 4.5 m and back 3.4 m); a further -54 degrees about X lies it flat, its top at z 2.03 to 2.12, reaching y 27.74 (at -50 its far end is 0.2 m above the floor, at -58 it dips below) | **supported by evidence** (measured); the `idle` clip's end pose was not read |
| Corridor | block 3 `GEO_hawkInt_roof_floor_front` (floor, deck, ceiling), block 11 stairs, blocks 7 and 13 the hull's side walls (inner faces at x 2.3 and -2.2), blocks 6 and 9 the tech panels (faces at -1.67 and 1.715, y 5.38 to 20.5, up to z 5.3), blocks 8 and 12 low railings inside them, block 10 a bulkhead frame at y 2.1 to 3.4, block 14 roof pipes, block 5 pipework | read |
| Heights | the low floor is z 2.03 for y 4.6 to 22.0; the deck at the cockpit end is z 3.35 for y -4.9 to 3.0; five stair treads 0.2 m apart in height and 0.32 in depth join them (about 30 degrees); the roof is 5.75 at the front, 6.3 at y 7.5 and 5.45 at y 24.9 | **supported by evidence**: the measures `TfwShip` holds |
| Props for the stations | `Monitor_Hub_01` (block 2), the tech panels (blocks 6, 9), the release panel `GEO_rearGate_releasePanel` (block 4, x 2.08 to 2.39, y 21.4 to 22.3) | read |
| Lights | the object tree names 55 `fx_light_*` nodes (red and blue point markers along the walls) and `fx_light_sun` at (-5.8, 36.9, 1.6); none is used: the look's lights are ours | read, unused |

Placed in the world turned half a circle about Z (`TfwShip.ToWorld`) so the ramp looks south: the first captures, with the ramp toward the level, showed Manufactorum's skyline through the door. Outside is `TfwHubSky` (a sky shader on the camera's own environment while aboard) with the owner's planet picture at `client/assets/musou/planet.jpg` (git-ignored, left out of exports, `.gdignore`; read from the project or beside the game; `planet.png` and `planet.webp` are accepted too), else a procedural brown planet. Checks: `TfwMapTests` (closed from every place, the measured corridor, walks, jumps, the door's pose) and `--tfw-test`'s hub tour (docs/commands.md).