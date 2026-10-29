# Milestone history

What each milestone and fix branch delivered, oldest first: formats, systems, decisions and the switches that restore old behaviour.
Moved out of CLAUDE.md to keep it short. Search it for a system's name before changing that system.

## Current milestone: the Manufactorum alpha (M16 Seize Ground done 2026-10-27)
M1 is done (2026-10-26): every `.oppc` in the user's install (app 55150, buildid 7412325) parses with zero errors and
zero warnings. The OBPK name table is a deduplicated string pool, not GameExtractor's model; see `docs/discovery.md`.
Claude sessions on the PC can run `smtool` directly against the install (the game folder is in the M1 report's
`game-dir.txt`). Running `.ps1` files needs the user (`m1.cmd`), because execution-policy bypass is not auto-approved.

M2 is done (2026-10-26): `client.cmd --archive ... --mesh ...` renders a statue from a multiplayer map with its original textures (static `dcm`,
`bmat` BOD materials, DDS; formats in `docs/discovery.md` section 2). Godot 4.7.2 .NET lives in
`%LOCALAPPDATA%\sm-reimpl\tools\godot` (downloaded by `client.cmd` on first use). Claude can run it directly:
`<godot>_console.exe --path client -- --archive <path> --mesh <name> --screenshot <png> --quit`.

M3 is done: the client's default view is the multiplayer Space Marine rig (Havok packfile `havok_sm_base_mp__hkxx`) wearing
the `sm_armour_basic_001_*` pieces and playing original animations (`--anim <name>`, `--time <s>`, `--bones`).
Skinned `dcm`, Havok skeleton/animation/TOC are in `docs/discovery.md` section 2.

M4 is done: `SmSim`, `SmNet`, `SmServer` with prediction, reconciliation, interpolation and a refusing handshake;
acceptance tests in `tests/SmNet.Tests`.

BAF attributes are decoded (`SmFormats.Baf`); `SmContent.GameplayTuning` reads the marines' movement from
`data/packages/ebps.oppc` and the server uses it. Still provisional: tick rate, gravity, the movement model's blending,
snapshot rate, interpolation delay, spawn points, and the silent-player rule (all listed in `spec/behavior.md`).

The Godot client joins the server with `--connect` (`client/NetworkView.cs`), one animated marine per player.

Multiplayer weapon values are recorded in `spec/behavior.md` (from `data/packages/wargear.oppc`; layout notes there).

M6 is done: `SmSim.Combat` simulates the original bolter (read by `GameplayTuning.WithWeapon` from
`sm_mp_ranged_bolter`, default entries) and health/armour shield (`health_ext`); the server resolves hits with lag
compensation (`World` history, `MaxRewindTicks` 15); protocol version 2; the client has a HUD, fire/reload/pitch and a
fighting `--bot`. Placeholders: hit volumes, eye height, respawn time, rewind cap, and the shield-then-health damage rule.

M7 is done: `HavokWorldCollision` reads each map region's collision (`<region>.hkt`; instanced pieces name their mesh
storage in extrusion.w), `MapContent` adds TDM spawns from `d_tdm.Layer`, `SmSim.Collision` (BVH, raycasts, capsule
controller) runs on both server and client, protocol version 3 names the map and checks a hash of its source files. The client
draws collision as grey placeholder geometry. Layer roles ("2" characters, "8" shots) are an interpretation.

M8 is done: `SmContent.MapScene` reads a map's placed objects, `.O3d` definitions, meshes, materials and textures;
`client/MapView.cs` draws the map with its original meshes and diffuse textures (lighting and shaders: placeholder;
`--show-collision` adds the grey collision). Rotation order: degrees about X, then Y, then Z (docs/discovery.md).

M9 is done: `HavokBehavior` reads the behavior graphs' sequencers and timing tags; `SmContent.MeleeTuning` builds the
combat knife's combo from its graph and wargear; melee is in `Combat.StepSelf` and `World.ResolveMelee` (protocol 4);
the client binds V / middle mouse and poses the original attack clips (`--bot-melee` for knife-only bot fights).

M10 is done: `SmContent.MpClasses` reads the six classes from `global` (`multiplayer_settings/ingame_progression/
classes`) and builds default loadouts; `WeaponTuning` reads hit-scan weapons; the simulation has per-player classes,
primary/pistol switching, semi-auto triggers and per-class melee; protocol 5 (class in Hello, per-recipient
snapshots). Client: `--class <name>`, X / mouse wheel switches weapons.

M11 is done: weapon heat (`HeatSpec` from `common_data/heat` and `apply_heat`; heavy bolter, plasma gun) with overheat
lock, cooling and switching; every supported class wargear is a loadout choice (`--primary/--pistol/--melee`, by index in
the Hello; unlock levels not applied); protocol 6; heat on the placeholder HUD.

M12 is done: charged fire (`ChargeSpec`; Fire = primary ability, AltFire = secondary), server-side projectiles from the
`ebps` blueprints with splash (`ProjectileSpec`, `SplashSpec`; `SmSim/Projectiles.cs`), the grenade launcher's remote
detonation on Reload; protocol 7 (projectiles in snapshots, not predicted); placeholder projectile and blast drawing.

M13 is done: thrown frag and krak grenades (`ThrownSpec`, rigid bounce, ground fuse, toss timing from
`SM_Bolter_GrenadeToss`), the melta cone (`ConeSpec`, `SmSim/Cone.cs`, self-recharging clip); protocol 8; client G to
throw, `--equipment`.

M14 is done: the jump pack (`JumpPackSpec` from `jump_info`, `SmSim/JumpPack.cs`, `JumpWorld.cs`; timing from
`MP_SM_NAV_JP_Sim`), ground pound and landing blasts; protocol 9 (snapshot budget now exactly 1200 bytes, no headroom);
client Space to jump, original jump clips.

M15 is done: teams (Space Marines, Chaos from `multiplayer_settings/teams` and class races) and Annihilation
(`SmSim/Teams.cs`, `SmContent/GameModes.cs`): side balance, friendly fire, scoring, score/time limits (provisional:
host options live in the executable), restart; protocol 10 with a match block, kill feed and scoreboard message.
Mode scripts in `util.oppc` are compiled Lua: only their constant pools are read for rule names; never decompile them.

M16 is done: Seize Ground (`SmSim/SeizeGround.cs`) on Manufactorum: three points from `d_mission_objects.Layer`
(`mkr_objective_1..3`), capture 20 s, neutralise 10 s, 2 per point per interval (`objectives.lua` constant pool),
per-side start areas; protocol 11; client point markers and HUD.

M17 is done: Chaos players use the original Chaos MP armour (`csm_armour_mp_<set>_<part>`) on the shared rig. Its class → set by class name
(interpretation) was superseded on 2026-10-28 (owner: "havoc default armor yes"): every class wears its data default, `default_armor_set` plus class
overrides, through `AppearanceResolver` (the mapping function is gone).

Paint is done (client/paint): the Space Marine wears `sm_armour_mp_basic` and both sides are painted from the install's paint schemes
(`SmContent/PaintSchemes.cs`; defaults `sm_ultramarines` and `chaos_multiplayer` from the player blueprints; paint mask R/G/B =
primary/secondary/tertiary, A = dirt; backpack weapons and jump packs too); `--paint <scheme>` test aid (shoulder decals: M30 below);
the shading maths is a provisional approximation.

M18 is done: characters collide with layers "2" + "8" (edge walls, railings), the map's KillRegion kills in pits;
provisional warm lighting.

M19 is done: fixes from the first bot playtest: follow cameras stop in front of walls (cast from the head, immediate
pull-in) and hide the own model when too close; test bots fight only visible enemies within 25 m and otherwise chase (M44a: now up to the weapon's range, with aim error and a spawn-door wait; spec "Test bots").

M20 is done: the camera, aiming and HUD after the original. `CameraTuning` (camera sets, mouse speeds, weapon→set) and
`CameraRig` (the Havok camera rig: offset by pitch) drive the client camera; Left Ctrl holds aim mode; shots converge on
the crosshair (protocol 12: shot offsets in the input, 30° server cone (ours), a per-recipient hit block for hit
markers); hit volumes come from the marine rig's ragdoll (`HavokRagdoll`, `HitVolumes`). HUD layout after the original
(placeholder drawing), info text behind F1. PC bindings (Left Ctrl aim, RMB melee, F charge) are from the user's
knowledge of the original.

M21 is done: evades by class (Space; Tactical rolls, Devastator/Havoc hop, Assault/Raptor keep the jump pack; `SmSim/Evade.cs`, `SmContent/EvadeTuning.cs`):
classes' evade abilities from `wargear`, roll clips, branch times and root motion (`hkaDefaultAnimatedReferenceFrame`)
from the behavior graphs; 0.8 s spacing; protocol 13. Players show aim poses (original Focus/aim clips blended by
pitch; choice: interpretation) and roll/hop animations. The evade's TargetableMode/CollisionMode windows are hyper armour
that negates stuns (user's knowledge; `Evade.HyperArmour`; checked by the M28 reactions).

M22 is done: from the user's M21 playtest: aim mode stands still on the original idle-aim clips (slow aim-walk only when
moving); every weapon but the stalker bolter and lascannon shares the rifle's camera sets (FOV 40 / 20); keys 1 = pistol,
2 = primary (`InputButtons.SlotPistol/SlotPrimary`); every spawn in the side's spawn room (`mkr_start_marine` /
`mkr_Start_Ork`, sealed by per-race movement blockers), facing out toward the room's exit;
diagonal Tactical rolls (8 directions); protocol 14. Spawn-room blockers (after M23): each side's three
`MissionMovementBlocker` boxes are solid for the other side's players in the shared movement step
(`CollisionWorld.CharacterFor`; every way of moving; shots pass; Annihilation too: ours), map loader version 5,
translucent placeholder shield planes in the client (M36: hidden unless `--show-markers`).

M23 is done: the death screen (user's description): a dead player's input is ignored, the body becomes a physics ragdoll
built from the original rig (`HavokRagdollPhysics`, `RagdollModel`: 17 bodies, 16 joints, masses; Godot joints approximate
the limits), pushed along the killing blow (`KillView.Direction`, protocol 15); a death camera and a strong vignette.
Client aids: `--bot-idle`, `--ragdoll-rest <s>`, `--ragdoll-debug`.

M24 is done: weapon models: every MP weapon's original mesh, materials and mounts (`WeaponModels`, `ObjectTree`, DCM type 2;
in hand on the rig's prop bones, stowed on the `MKR_Stow_*` markers, worn jump packs and backpack); drawn for every player
and kept on corpses; `MuzzleWorld(playerId)` gives the `fx_Muzzle` locator for M25. Hand/stow timing: interpretation.

Lighting (PR #30): Manufactorum's placed lights from `l_lights.Layer` (`SmContent.MapLighting`), normal and specular
maps, two-layer vertex-blended materials and cut-outs (`client/MapMaterials.cs`, `MapLightingView.cs`); god rays are
effects (M25). Sun, fog and exposure mapping: provisional.

M25 is done: the original weapon effects. M25a reads the effects (`SmFormats.Fx` PSystems, `master_fx` atlas page,
`EffectCatalogue`, `FxLibrary`, `MapEffects`), the weapon-layer surface material per triangle (`StaticMesh.MaterialOf`;
`MapContent.LoaderVersion` 6) and sends shot events (protocol 16: origin, end, hit kind, surface, victim). M25b plays them
(`client/FxPlayer.cs`, `FxBatch.cs`, `NetworkView.Fx.cs`): muzzle flashes and shells at the weapon's markers, tracers to the
hit, impacts by surface or armour, decals, projectile trails, explosions, the map's god rays and steam. How the renderer
reads the elements' fields (curve timing, `USpeed`, rotated atlas sprites) is interpretation; camera kick, blast shake and
the krak flash are placeholders (spec/behavior.md, M25b). `--no-fx`, `--placeholder-fx`, `--fx-slow`, `--spectate shot`.
Effects and sound read one per-frame list of others' shots (`GameClient.TakeShots` drains its queue).

M26a is done: Wwise readers (`SmFormats.Audio`: AKPK packages, banks v56, HIRC, a port of ww2ogg's Wwise Vorbis
rebuild, BSD-3); the codebook file is downloaded into the private cache with a hash check, never committed.

M26b is done: the original sounds in the networked client. `SmContent.AudioCatalog` loads the MP Wwise packages (read-only)
and resolves events into voices (switches, states, random/sequence/continuous containers, layers, delays, volume/pitch up
the parent chain plus output bus volumes, attenuation curves); `.wem` → Ogg on demand into
`%LOCALAPPDATA%\sm-reimpl\cache\audio\ogg\`. `SoundTriggers` reads every `post_sound_event` of the MP blueprints, wargear,
projectiles and hit containers; behaviour tags of the shown clip trigger footsteps, swings and rolls; `MapAudio` places
the map's emitters and regions. Client: `SoundSystem` (pooled players, virtual voices, limiter, region reverb),
`NetworkView.Sound.cs`, `--no-sound`; own shots from `GameClient.TakeOwnEvents`. Switch group 0x0d500bea read as the
local player's variant (evidence in docs/discovery.md). Mixing is provisional; `smtool audio-coverage` reports coverage.

M33 is done (branch `m33/sound-follow-ups`): sound follow-ups. The master bus is applied (-7 dB: buses hold two volume properties, both
summed up to and including the master, so every voice is 7 dB quieter than before; `--sound-gain <dB>`, ours, offsets it); low-pass
filtering (node LPF values, the attenuation's LPF curve, RTPCs, Set/Reset LPF actions such as stun, grenade deafening and the
map's rolloff regions) through 20 filter buses (value to cutoff mapping ours); RTPC curves for `distance` and `speed` (found by name);
Set/Reset pitch, volume, mute and SetSwitch actions with fades (`SoundModifiers`, `VoiceMixer` in `SmContent/AudioMix.cs`); and
character voices: the MP wargear binds `post_speech_exertion_event` / `post_speech_action_event`, resolved through the speech bank's
dialogue events (`speech_exertion`, `speech_action`) by the class's `speech_arg_character`; melee swings, the charge and tagged clips
bark (one line at a time per character, ours); reload and grenade action commands have no MP line. Evidence and what is not done:
`spec/behavior.md` (sound rows) and `docs/discovery.md` ("Audio follow-ups (M33)").

Footsteps fix is done (branch `sound/footsteps`, owner: "footsteps sound nothing like the original"; client and tools only, no protocol change): the event graph, tag times, surface
switch, local/other variant and speed values were checked against the data and are right; two real faults fixed: the nodes' **limit sound instances** (the footstep layers hold 1 to 4,
kill-oldest: each step cuts the tail of the last; `InstanceLimiter`, `HircNodeBase.MaxInstances/KillNewest/GlobalLimit/...`; applied to footstep and landing posts only;
`--sound-limits none` = exactly the old behaviour, `footsteps` (default) = limits + 2D threshold culling for footstep and landing posts, `all` = for every post) and the **voice pool**, which the inaudible foley layers filled (25 steals in 40 s; the volume threshold now also culls 2D voices,
the nodes' below-threshold byte is 1 = kill). **Open, needs the owner's ear:** on the data as read the footsteps are the loudest sound of the mix by 11 to 16 dB (the sole clank -7.8 dB
against the bolter -24 dB); `--footstep-gain <dB>` is the listening aid, `--log-sound footstep` prints the event, variant, surface, speed and each voice's mix per step.
Evidence: docs/discovery.md "Footsteps", spec "Footsteps (sound/footsteps fix)", docs/changes-from-original.md.

Footsteps second pass is done (branch `sound/footsteps-2`; owner 2026-10-29: "like hard boots banging on loose metal", wanted the original's sound slightly quieter; client and content only): the level question answered by
our reading. By convention no node of the 29,264 in the MP banks has an own volume above 0 dB, but the footsteps' speed curves add +19.8 / +32.2 dB (and the Ork grenade fuse's distance curve +89.5):
a volume curve above 0 dB is now shifted down by its peak (`VoiceMixer.PeakNormalised`, cap mode `peak`, chosen by review: the node is at its own volume at the curve's loudest point, quieter
below, walk-to-run dynamics kept; every sound, only 10 nodes change, pinned by `RtpcCapInstallPinTests`; interpretation). Own steps at a run on metal: sole −7.8 → −40, thud −19 → −39,
ring −23 → −43 dB (bolter −24); at a walk 10 to 20 dB softer. The owner's −3 dB on every foot-tag footstep (`FootstepLevel`, landings excluded) is a deliberate change. Aids: `--rtpc-volume-cap
peak|node|voice|off` (`off` = before), `smtool audio-mix --cap ... --step`. Surface, layers, triggering, buses checked unchanged. Spec "Footsteps, second pass (sound/footsteps-2)", docs/discovery.md same title.

Impacts and the hit confirm are done (branch `sound/impacts`; owner 2026-10-29: hit sounds "as if they were landing right on top of my character ... very high pitched"; client and content
only). Cause: the weapon's hit confirm (`player_hit_something_action` → `play_bolter_smack`, trigger point self) was posted on the shooter's emitter, whose `material_type` is the floor:
on metal every hit played a metal ricochet at +250 cents, non-positioned, −27 dB; and an unset state group fell to the default case, so its kill layer played on the first hit.
Fixed (faithful reading): the victim's material (armour or flesh) and unset state groups = "None" (`AudioCatalog.NoneState`). Destination impacts were already 3D at the hit point
(bolter on metal −27 / −40 / −65.5 dB at 5 / 20 / 50 m). Owner's changes (deliberate): `--hit-confirm soft` (default; the event's smack layer only, −10 dB, −500 / −700 cents armour / flesh,
low-pass, 0.15 s cooldown, kills heavier with the data's kill layer; `data`, `off`) and `--impact-treatment bolters` (default; bolter family and stalker impacts −400 cents, low-pass +40;
`ballistic`, `off`; cone shots are not ballistic). The death state is set only for the confirm post and put back after it (`ImpactSounds.StateBeforeConfirm` / `StateAfterConfirm`);
`--hit-confirm data` skips the cooldown and sets no kill state itself. Pins: `StateSwitchInstallPinTests` (only the confirms' kill layer changes with the None state). Post offsets
(`VoiceOffset`) on `SoundSystem.Post`. Spec "Impacts and the shooter's hit confirm (sound/impacts)", docs/discovery.md "Impacts and the hit confirm".

Shield sounds are fixed (branch `sound/shield-regen`; owner 2026-10-29: "the shield regenerating starting sound is playing constantly on a loop ... other players or my own?": the own player's): `play_armor_break`'s local-only warning (continuous container, every 0.7 s) is stopped only by `play_armor_restore` (a regeneration start); a player who died with the shield down kept it pulsing through death and the next life. A death or spawn now ends the emitter's loops (`SoundSystem.StopLoops`); the regen start is a span (`ShieldSoundTracker`: once per regeneration, any level, ended by full or damage). `--log-sound armour`. Spec "Shield sounds (sound/shield-regen)".

M28 is done (PR #36): hit reactions from the data (`reaction_mp_melee` interrupt, `_stun`, `_stagger`; clips and
branch times from `MP_SM_Reactions`; `SmSim/Reactions.cs`, `SmContent/ReactionTuning.cs`): light hits interrupt, heavy strikes
("Stun / Alt Strike", knife and chainsword Y chain with the foot stomp) stun and stagger, frag / krak / ground-pound blasts react
by intensity, krak blindness (M37: the krak is the blind grenade and only blinds); immunity in the evade's hyper armour and jump-pack flight; knockback through the capsule;
protocol 17 (main's 16 plus reactions; 4 guaranteed shot records). Stun damage is inert (`stun_threshold` 1e6). Client: F stun strike (user's knowledge; alt fire moved to middle mouse), reaction clips,
placeholder shake and blindness.

M29 is done (PR #38): the charge: W + F while running (user's knowledge) rushes along the facing
on the sprint graph's bull-rush clip (`SmSim/Rush.cs`, `SmContent/RushTuning.cs`); an enemy it connects with takes the
weapon's `ability_bullrush_01` hit (75 + 25 shield) and a stun (hyper armour negates it), an ally only stops it, walls stop it;
protocol 18. Remote players' reaction, charge and attack clips play at the frame rate between snapshots (`SmNet/ClipClock.cs`,
`client/NetworkView.ClipSmoothing.cs`: a local clock re-synced to each snapshot value; client only, own prediction untouched).

M30 is done: armour decals. A scheme slot's `decals/decal` (shoulders only) names a `custom_paint_decal` entry whose 128x128 DXT5
texture (`decal_package`) is laid over the pad through its second UV set (`Uv1`, one island over the unit square on the Space Marine
`mp_basic` pads and the Chaos `basic` left pad; degenerate on Havok, Raptor and the Chaos right pad: no decal there) in the painted shader.
Default Space Marine look: the white Ultramarines "U" on the left pad; `chaos_multiplayer` has no decals. Placement is an
interpretation; `pattern_enabled` and class badges (`sm_identifier_*`, `sm_rank_*`) are not applied.

M27 is done: hip fire. (M27's "accuracy hip = aimed, no scatter" was reversed by the owner on 2026-10-28: weapons/spread below.) Simulation: the trigger held with a ranged
weapon in hand ends the sprint (`Combat.StepPlayer`, `shooting`; no sprint-fire state in the behaviour graphs). Client
(`NetworkView.HipFire.cs`, `HipFireAnimations` from the graphs): after a shot the marine shows the ready run (the family's
direction x speed x pitch clips; standing: the idle aim pose) while the simulation holds the weapon up (M31; 1.0 s, ours), blended with the graphs' relaxed /
ready times, and each shot restarts the original additive recoil clip as a whole-body pose layer
(`CharacterViewer.SetPoseLayer`; grenade launcher: partial upper-body overlay). Own events and others' shots are drained once per
frame in `HipFireBegin` (sound and effects read `_frameOwnEvents` / `_frameShots`). `--no-hip-fire`. The raise delay
before the first shot: applied since M31. Muzzle check (barrel off the aim while shooting, mean degrees, weapon up): rifle 3.3 to 3.9, pistol 6.2, heavy
bolter 2.5 on the move; aim mode 0.6 to 4.6; the first 0.3 s of the ready posture (the raise, shot not delayed) 17 to 18 for rifle and pistol. Pistol sideways error (QA 2026-10-28, 11 to 19 degrees) is the original's recoil climb (+5 to +10 pitch) over stance clips that aim within 3 to 4 degrees; `--hip-probe` (bot) prints the clip and blend check. Protocol 19 (no layout change: the sprint rule changes the movement, so client and
server must agree). M31: the charge needs the sprint, so W + F with the trigger held is the stun strike (superseded in protocol 39: W + F always charges).

M31 is done (the owner's rules of 2026-10-28; protocol 20): the charge starts only from a sprint (superseded in protocol 39, feel/charge-directions: W held is enough) (Sprint
applied that tick, stick forward, faster than run + 0.25 x (sprint - run) = 7.375 m/s; `Rush.CanStart`); Assault and Raptor charge
from the ground only. The first shot waits for the weapon to come up (`WeaponSpec.RaiseDelay` from the hip-fire graphs: rifle
0.2 s, pistol 0.167 s; heavy bolter, grenade launcher, melta, lascannon, plasma cannon always ready; `PlayerState.ReadyTicks` /
`RaiseTicks`; held up after a shot for `SimConfig.ReadyHold` (1.0 s in M31; 10 s since M32); a sprint, switch, melee, throw, evade, jump, reaction or
charge lowers it; aim mode counts as up); the client's ready posture follows it (`--hip-hold` removed). Fire drives every
charge weapon: tap = normal shot, hold = charged shot (AltFire and middle mouse gone). Spawn-room blockers reach 25 m above
the level's top (**change from the original**, owner's decision: jump packs cannot fly over; map loader 7, nav jump sim 2;
the client draws the placed height). Bullet tracers cross in at most 0.1 s with the impact; the lascannon is instant (client).
Deliberate changes and our choices where the original is unknown: `docs/changes-from-original.md`.
Armour stays silent while moving (no MP gear-rattle binding). Snapshot worst case 1197 bytes.

M35 is done (branch `m35/reload-animations`): reload animations (owner's request). `SmContent.ReloadAnimations` reads each weapon family's reload
clip, tags, bone mask and blend times from the behaviour graphs (`smtool graph-dump --reload`): bolter, bolt pistol, storm bolter,
stalker bolter, grenade launcher, lascannon (heavy bolter, plasma gun / cannon and melta have no reload: heat and pump). The clip is played
over exactly the weapon's reload time (`enforcedDuration` = `FLT_WeaponReloadTime`) as a masked pose layer over the movement
(`ReloadPlayer`, `NetworkView.Reload.cs`): own player from the simulation's `ReloadLeft`; other players' reloads are **inferred from the shots
seen** (ammunition is not on the wire; interpretation, spec "Reload animations and magazine drops"); sounds at the clip's tags (own: M26b; others: scheduled
from the inferred start). The weapon model's magazine (`WeaponModel.Magazine`, the Chaos bolter and bolt pistol: `EmbeddedMagazine`, shells of the body
block, `MeshShells`) leaves at the remove tag, falls as a `RigidBody3D` copy of that mesh on the ragdolls' level (`MagazineDrops`: ours, 10 s, at most 24)
and returns at the load tag or when the hand is back (`ReloadHandProbe`, measured on the clip). `--no-reload`; test aids `--reload-test <wargear>
[--reload-shots t,..] [--reload-probe]` (standalone view) and `--reload-capture <dir>` (networked client); `smtool weapon-parts [wargear] [--components]`.
No magazine mesh: lascannon (animation and sounds only), the grenade launcher keeps its drum. No sound of its own for the magazine hitting the floor (none found).

M32 is done (the owner's answers of 2026-10-28; protocol 21): the charger has hyper armour for the rush's first 0.333 s
(`RushSpec.HyperArmourUntil`, `Rush.HyperArmour` in `Reaction.CanReact`: stuns and other reactions negated, damage applies, the
charge runs on; later, and in the collision stop, a stun ends it). User's knowledge ("wears off roughly halfway"); the graph has no
window for it (the rush clip's `CollisionMode` spans the whole clip), so half of the attack part up to `BRANCH_Attack2Idle` (ours).
Charge stops are applied after all contacts of a tick (head-on chargers alike). The weapon stays up 10 s after a shot
(`SimConfig.ReadyHold`: the owner's rough estimate, **unverified, to check in the original**); ready ticks are a u16 in the own
record (snapshot worst case 1198 bytes). Confirmed and pinned by tests: the first shot after a switch waits for the raise; the
always-up weapons never wait, also after a sprint or a roll.

M34 is done: capture aids for footage (client only; no protocol change). `--no-markers` hides the test overlays and
placeholder markers that are not the original look (side rings, name tags, charging glow, blast spheres and discs, ground-pound and
landing rings, melta cone flash, spawn-room shield planes, Seize Ground point columns, the debug text); the HUD (which still shows
the points) stays, `--no-hud` hides it. Movie Maker fixes its frame size from the project's viewport size before any script runs
(`--resolution` ignored; a later window resize is only scaled into that size), so `movie.cmd --movie-size 1080x1920 --write-movie f.avi`
records from a throw-away per-run copy of the client project (it writes only the ignored `client/.godot` cache, through a junction;
the script refuses outputs inside the repository or the game install); checked: AVI header and frames 1080x1920. `--spectate inspect` orbits slowly
around the own weapon in hand.

M37 is done: the files' "krak" grenade is the **blind grenade** (owner's knowledge; ammunition `blind_grenade`, UI "Blind Grenades"):
no damage, stun, knockback or hit; it blinds every living player whose eye it sees (weapon collision) within its blasts (data: 3.5 s
to 8 m, 2 s to 30 m), allies and the thrower too (`SplashSpec.BlindOnly`, `World.BlindBlast`); players see "blind grenade"
(`ThrownSpec.PlayerName`). The ground pound and landings match the data (spec "Ground pound damage check (M37)"). Protocol 21 unchanged.

M38 is done (the owner's playtest, 2026-10-28; client only, no protocol change; branch `m38/hit-feedback`): hit feedback. The own player's screen edges
flash yellow for shield damage and red for health damage (the data's `screen_dmg_*` / `_ar` screen effects are not drawn; ours: colours, 0.45 s envelope
after their bloom keys, direction toward the shooter). Impact sounds on a player combine the armour impact (`ceramite_armour`) and the flesh layer (the broken-armour
impact state, set per post: ours) always, the owner's direction (a deliberate change; level impacts get the armour state explicitly). The impact effects use the victim's armour
state only where known (own player, `--hit-armour`; others' shots: the protocol-22 armour bit; own hits: the last hit report on that victim; `ArmourUpForHit`), else the armour variant, with the blueprint's
`armourshield_hit` / `armourshield_inactive_hit` victim effects. Decals: every weapon's own (bolter, melta, lascannon, plasma gun glow, plasma cannon craters) with
own clocks, additive glow quads, a sheet animation, detonations on the probed surface, and players/weapons/ragdolls on render layer 2 so decals skip them. The map is silent
(`--map-sounds` restores it; the ringing was the spawn-room shield hum). Rules in `SmContent/HitFeedbackRules.cs` (tested). Spec "Hit feedback, per-weapon decals and the
silent map (M38)"; `docs/changes-from-original.md`.

M39 is done (owner's request 2026-10-28, user's knowledge of the original; branch `m39/cover-collision`): cover collision and penetration.
Manufactorum's 49 green `ammo_crate_A` (and 4 wall-high door buttons) are moving rigid bodies whose shapes live only in `ppk` physics shape
meshes (`SmFormats.Ppk.PpkMesh`, single-part layout); `SmContent.MapProps` loads them as static props (the owner: they never move) that
`CollisionWorld` adds to both meshes: solid for every movement mode, shot, cone, projectile and thrown item (`MapContent.LoaderVersion` 8).
Hit-scan penetration from the wargear (`PierceSpec`, `Pierces`, `CollisionWorld.TraceShot` per-prop spans, `World.ResolvePierce`): the
lascannon passes 10 players (props, bodies, heads 1; charged: free), the stalker bolter 2 (props 5, bodies 4, heads 3), the bolter's Kraken
Bolts 2 (its perk entry, applied when the player's perks give its upgrade string: `Pierces.HasUpgrade` over `Loadouts.UpgradeStrings`); players on the ray take the full shot, past a crate
only the exit cone (lascannon 50 / 15 m, stalker 20 / 10 m). The own tracer and crosshair follow it (`client/NetworkView.Pierce.cs`);
`--show-collision` draws props in green. No protocol layout change on top of 22 (the config hash changes).

M41 is done (the owner's request, 2026-10-28; client only, no simulation or protocol change; branch `m41/ground-pound-impact`): the ground pound's impact. The wargear's
`actn_gp_hit` plays `jumppack_land_heavy` (read by `SmContent.JumpPackEffects`): a 4 m crater decal, a 6 m crack decal, flash, dust and debris sprays and ground shockwave
sprites, all inside the effect's collision descriptions (`FxElement.OnCollision`, now typed; played by `FxPlayer` for effects that opt in with `FxEffect.Collisions`). Ours: a thin dark
scorch ring at the simulation's pound radius (`PoundRing`, 10 s), the light landing's dust added at 1.6 x, 6 to 12 physics ground chunks (`GroundShards`, level collision only,
render layer 2) and a small screen shake. The impact sound (`play_sm_jump_pack_smash`) now plays at the impact, not 0.36 s later. Landings play the data's landing effect only.
Spec "Ground pound impact and landing effects (M41)"; test aid `FxTest --pound-test`.

M36 is done (the owner's playtest, 2026-10-28; client only, no protocol change): the sprint and the backpedal have their own clips
(`SmContent.LocomotionAnimations`, `smtool graph-dump --locomotion`; `client/NetworkView.Locomotion.cs`). Sprint: the sprint graphs' three-way
blend over the movement angle (`SM_MP_1HR_NAV_Sprint_*` for rifles, pistols and the rest, `SM_NAV_TB_Sprint_*` heavy bolter,
`SM_NAV_LC_Sprint_*` lascannon and plasma cannon), shown for a marine moving forward faster than run + 0.25 x (sprint - run) (own predicted
state and remote players alike; only while grounded and not rolling, charging, reacting or in a jump phase, since those also exceed run speed); relaxed run: eight directions with walk and
run clips, back included (`SM_NAV_Run_B_01`, pistol `SM_1HR_NAV_Run_B_01`); the ready posture and aim mode already had backward clips. Clip
choice, thresholds and playback rates: interpretation. The own model and the camera no longer step at the 30 Hz tick: the yaw and pitch are
drawn at the view's, the position between the last two predicted ticks by the fraction of a tick elapsed (one tick late; corrections fade
out; `GameClient.PreviousPredicted` / `TickFraction`, rules in `SmNet.ViewSmoothing`, tested; the simulation, collision, shots and camera wall
casts keep the simulation position). Measured headless at 60 fps: the model stood still in about half of the frames before, 0% (yaw) and
3 to 8% (position) after (`NetworkView.Turning.cs`, `--pan-rate`, `--yaw-probe`, `--no-view-smoothing`). The Seize
Ground point columns, the ground-pound and landing rings and the spawn-room shield planes are hidden unless `--show-markers` (test aid;
`--no-markers` still hides everything else and wins). Test aids: `--bot-gait` (bot walks every gait in turn), `--no-locomotion`.

Customizer core is done (protocol 22; branch `cust/t2-sim-wire`, tasks T1 to T5 of `docs/customizer-architecture.md`; T6 to T8 to come): the
customizer's engine-free core. Catalogue (`SmContent.CustomizationCatalog`, `PerkCatalog`; `LocaleTable` in `SmFormats`): armour pieces and sets
per race, the visible palette, badges and presets, perks with their upgrade strings, unlock levels (all unlocked: `UnlockMode.All`). Profile
(`SmContent/Customization/`: `ProfileStore` JSON with repairs, emblems as `<hash>.bc3`; `ProfileWire` names to wire indices; `AppearanceResolver`
what to draw) and the BC3 codec (`SmFormats/Dds/Bc3.cs`). Simulation: perks are chosen (two slots, the True Grit rule for pistol-slot bolters;
no perk effects yet), loadout changes apply at the next respawn (`World.SetNextLoadout`). Protocol 22: the Hello carries perks and both races'
62-byte `AppearanceCode`s; `Setup`/`SetupAck`, `AppearanceQuery`/`Appearance`, `BlobRequest`/`BlobChunk` (custom emblems: uploaded, verified,
served; `BlobTransfer`), appearance versions in the scoreboard, the armour-or-flesh bit in shot records and the hit block (wired into the M38 hit
effects); snapshots unchanged (1198). Server: `--no-custom-emblems`, `--unlocks all`, limits from `CustomizationCatalog.LoadLimits`.
`GameClient` has the API the UI uses (`ChangeSetup`, `AppearanceOf`, `Emblem`, `LastSetupAck`, `TakeAppearanceChanges`, `TakeEmblemArrivals`).
Done since: rendering of appearances (T6, PR #64), the front end and customizer UI (T8, PR #68) and the networked view (T7, below).
Transport decision in `docs/architecture.md`; rules in `spec/behavior.md` "Custom classes".

Customizer T7 is done (branch `cust/t7-network`; client only, no protocol change on top of 23): the networked view draws every player from
their resolved appearance (`client/NetworkView.Appearance.cs`: own look from the profile, others' from `GameClient.AppearanceOf`, default look
until it arrives; never waits: `BuildScheduler` 5 ms/frame over `AppearanceAssets.TryBuild`, placeholder marine, swap only while alive via
`CharacterViewer.SetLook`, corpses keep their model; emblems fall back to the badge until pulled or when hidden; default and own looks warmed at
map load; rules and tests in `client/Looks/LookLogic.cs`, `tests/SmNet.Tests/NetworkLookTests.cs`). C on the death screen opens the class
creator and appearance editor in the match (`NetworkView.Loadout.cs`); Deploy sends the same plan as a Join with `ChangeSetup`, applied at the
next respawn; Esc closes, the respawn closes it. Measured: 8 players changing looks every 2 s, look work under ~15 ms per frame. Owner's
decisions (2026-10-28): Chaos Havoc/Raptor default armour follow the data (`chaos_default` plus class parts); the Chaos default left pad
follows `pattern_enabled` (red). Test aids: `--look-churn`, `--look-closeup`, `--look-log`, `--picker-demo`, `--picker-on-death`,
`--picker-deploy`, `--bot-change-loadout`. Gaps: backpack weapons keep the side's paint (the worn jump pack follows the chest colours since 2026-10-29, below); others' perks are not on the wire; the real C key and
mouse release need a human check. Spec: "Custom classes, end to end (T9 summary) and in the match (T7)".

M40-T0/T2 are done (the M40 audit's capture and render-clock tasks): demos (`SmNet/Demo.cs`,
`DemoPlayback.cs`, format 1, `Demo.InputFields` pinned to `PlayerInput`) record a real-time client's packets, inputs and view, and replay
them on Godot time with no server, so Movie Maker footage runs at true speed (60 s demo = 1800 frames at 30 fps; match clock within 0.1%).
Corrupt files are refused or trimmed, recorder I/O errors stop only the recording. The smoothed render clock (`SmNet/RenderClock.cs`,
provisional constants) advances by frame time with at most a 5% slew, never backwards, and trails the raw estimate by at most 3 ticks so
`ViewTick` stays inside the server's lag-compensation floor (`ViewTickBoundTests`); remote runners' per-frame step variation 25-40% → about 3%.

M40-T6 is done (client only): cameras never put the near plane inside collision (level layers and the M39 crates): a sphere cast (centre
plus 8 rays), damped pull-in with a hard clamp, depenetration, a continuous per-player push out of bodies, speed-limited springs and
wall-clamped lookahead for spectator views, the death orbit cast from the focus (`client/NetworkView.Spectate.cs`). Spectate "action" worst
jump 41 m → a few metres at hitch frames; the hard clamp can still move the camera instantly when an unforeseen wall enters its line (spec
"Cameras that never clip or cut (M40-T6)", probe numbers there).

M40-T9 is done (client only, no protocol change; branch `m40/t9-hitches`): hitch removal. Heavy first builds are spread (new remote characters, weapon rigs and corpses
within a 6 ms budget per frame, `NetworkView.Prewarm.cs`), the client's methods are compiled on a background thread (`JitPrewarm.cs`), the default loadouts' weapon models
and the crosshair's collision queries are warmed during the map load, all rig animations are decoded on a worker (`MarineAssets.Animation` reads one table per rig), one hidden
ragdoll is built and freed 2 s after joining (the first death: 21.8 ms before, 4.3 ms after), and the per-frame sets and lists of the remote players and projectiles are reused.
Replay of an 8-player demo (`--play-demo`, windowed off-screen): join worst frame 224 to 272 ms before, 55 to 68 ms after; after the join the client's own code never exceeds
33 ms (worst 24 to 27 ms, p99.9 about 11 ms); the whole-frame figures after the join are dominated by the engine's share on a loaded PC (spec "Frame hitches (M40-T9)").
Still in the first join frames and owned elsewhere: the first effects frame, HUD, match UI and own character (about 15 to 20 ms each), the first projectile, the first remote jump-pack
landing or pound (25 to 35 ms). Review fixes: a queued death is dropped when its player is alive again or gone, the own death is never deferred, one id never gets two corpses; the JIT thread skips the client's types with static constructors and stops on any exception. Test aids: `--frame-profile`, `--no-hitch-fixes`. Release builds (`<Optimize>`) of the client project are untried: client.cmd builds Debug.

M40-T4 is done (client only, no protocol change): the locomotion blend space (audit D4). The relaxed and ready runs blend the two
neighbouring directions and speed bands of the original clips by the damped ground velocity (`SmContent.ClipGait`, `GaitSpace`, `GaitPhase`,
`VelocityDamper`, `GaitLibrary`; engine-free, tested): weights and cadence from each clip's measured planted-foot stride, phase-synced by
the feet's plant times (the graphs' `SYNC_L1`/`SYNC_R1` are carried by no clip, so derived), the idle pose below 1 m/s (ours).
`CharacterViewer.BlendStance` (multi-clip layer) and `CharacterViewer.LegIk` (two-bone IK to the clips' mean ankle); footsteps on the gait
phase. The sprint keeps the M36 blend (`--gait-sprint` measured worse). Same demo, rifle: worst planted-foot speed 1.24 → 0.28 m/s, steps
37 → 12 cm; heavy bolter and pistol improved but over the limits; aim-walk still nearest-clip. Owner's playtest fix: from a melee step's
`MoveAt` and during a throw, while the body moves, the legs follow the locomotion and the action clip stays on the upper body
(`NetworkView.Handoff.cs`, rules in `SmNet.ActionHandoff`): stun-strike slide 6.49 → 0.57 m/s. Aids: `--no-gait-blend`, `--no-leg-ik`,
`--no-handoff`, `--log-footsteps`, `--anim-probe`, bots `--bot-gait --gait-sweep` / `--action-sweep`. Spec "Locomotion blend space (M40-T4)".

M40-T5 is done (branch m40/t5-turns; client only, no protocol change): turn in place, aim-offset idle, stops and starts (audit D8, D9). `SmContent.TurnAnimations`
reads the graphs' standing states (`smtool graph-dump --turns`): the rifle's aim-offset idle (left / ahead / right x pitch clips, `SmNet.AimOffsetGrid`), the
turn threshold (90 degrees; 75 pistol and heavy bolter), `SM_NAV_AimTurn_*` with their root rotation (135 degrees over 0.833 s, ended by `BRANCH_Turn_End` at 0.92) and
leg masks, the stop blends and the run-start clip `SM_TRN_BO_IdleAim_to_RunF`. A standing marine keeps its lower body planted, the upper body follows the aim, and past the
threshold the turn clip turns the body (`SmNet.BodyYaw`; follow rate 720 degrees a second and twist cap 135 degrees are ours; the shown body now lags the view: owner's decision
pending). Stops: the feet on the ground are pinned by IK while the run cross-fades to the idle, then step (`CharacterViewer.FootPin.cs`; the original's sprint-stop clip needs 5.8 m of
root motion the simulation does not give, so it is not played). Starts: the start clip, driven by the distance the body has travelled (its root motion matches the simulation's),
legs and hand-over matched to the run, is **off by default** (review M1: `--run-start` opts in; the code stays). Probe on the same `--bot-turn-sweep` demos (headless 60 fps), before / after: body yaw per frame max 180 / 12 degrees (frames above 15: 36 / 0);
planted-foot slide in the 1.2 s after a stop (worse foot, median) 22 to 37 / 1 to 2 cm (the whole toe path, steps included, is longer: 31 to 32 / 38 to 76 cm); planted toe speed while
turning 100 to 200 degrees a second 1.48 / 0.16 m/s; forward starts 50 / 26 cm (relaxed), 12 / 15 cm (ready). Gaps: the rigid spin at fast turns, slope idles and the can't-fire flag are not applied; beyond 90 degrees of twist the spine twist (to the 135 degree cap) keeps the barrel within a median 5 to 7 degrees of the aim; the start clip's entry turns the weapon-hand bone 25 to 28 degrees a frame; no start for the pistol and heavy bolter (no data). Review round (2026-10-28): the run start is off by default (`--run-start`), pins drop out of reach, on a teleport (> 1 m a frame) and with the run-start reset or a client-timed jump view, engine-free `SmNet.FootPinRules` / `RunStartRules` (tested); 16-player pose cost unchanged (1.5 ms median). Aids: `--no-turns`, `--no-foot-pins`,
`--run-start` (opt-in) / `--no-run-start`, `--bot-turn-sweep [--turn-sweep-fire]`, `--anim-probe` (TurnProbe), `--run-start-log`. Spec "Turn in place, aim-offset idle, stop foot pins and run start (M40-T5)".

M43 is done (branch m43/perks): the perks' effects from the data. Every reader's entry choice follows `SmContent.UpgradeScope`; `MpClasses`
reads each weapon, thrown item, jump pack and melee combo again with each perk string (and pair) its file requires, and keeps the reads that
differ as `UpgradeVariant`s; `SmContent.PlayerUpgrades` reads the player blueprint's perk entries (received damage by type, regeneration,
Iron Halo, death and armour-break blasts). The simulation picks each player's variant from its two perks (`Loadouts.Upgrades`, `Body`);
bursts (Burst Fire), heal on hit, take-off, death and armour-break blasts (`SmSim/PerkWorld.cs`). Class default perks now act. Protocol
23: the own record's perk word (perks and a burst's rounds left; worst-case snapshot 1200 = the budget). 34 perks implemented (4 partly),
14 inert ("NOT ACTIVE YET": `Perks.StatusOf`); table in spec/behavior.md "Perks"; `smtool perks <game-dir|auto>` lists them. Perk kills are named in the kill feed (kill cause 5);
a round restart drops waiting perk blasts; `SimConfig.MaxPerks` 127 (7-bit wire field).

Weapons C is done (branch `weapons/c-melee`; no protocol change): the missing melee. The power sword, power axe / Chaos chain axe and thunder hammer /
Chaos daemon maul (`MeleeTuning.PowerSword`, `.PowerAxe`, `.ThunderHammer`; both races' twins share one map) and the Devastator's and Havoc's
kick and stomp from their core ability `sm_mp_core_abil_heavy_evade` (`MeleeTuning.Displace`, graph `MP_SM_MEL_TB_Displace`; `MpClasses.CoreMeleeMaps`,
used only by a class without a melee weapon). New step rules (`MeleeStep`, `MeleeChain`): `damage_armourshield` off the shield first (the axe's 250),
string ends and light-to-heavy targets read off the graph's transitions, the Y,Y finishers on a heavy press in a heavy strike's
`BRANCH_Melee_Y_Combo` (hammer `BRANCH_To_Slam`) window, blast strikes from any `DISPLACE_*` action with a triggered blast (quick stomp, AX slam,
hammer slam), `ability_displace_slam`'s reaction immunity; knife and chainsword unchanged (new fields hash only when set). Fast Stomp decoded
(`MeleeTuning.ReadSpeedUpgrades`: the graph's `hkbSetFloatModifier` sets the stomp clip's speed 1.5); Axeman's Zeal, Power Swordsman's Zeal, Killing
Blow, Furious Stomp and Fast Stomp are active. Client: kick / stomp label, bot heavy chains. Spec "Power sword, power axe, thunder hammer and the
heavy classes' kick and stomp (Weapons C)"; ours in `docs/changes-from-original.md`.

M45 is done (branch `m45/jump-height`; protocol 27): the jump pack's middle jump (the owner's knowledge of the original: "let go of Space mid-jump
to stop rising"). The data (`jump_info`, `MP_SM_NAV_JP_Sim`) has no release field, tag or branch. Jump up on any ascent tick of a committed jump (from
the lift-off on) cuts the rise, latched to the apex (`PlayerState.JumpCut`, `JumpPackSpec.ReleaseCut`, set by `JumpPackTuning` for every pack read):
the hold stops growing, the rise goes on 0.1 s (ours), then decays with a 0.05 s time constant (ours) to the apex; hang and fall as before. Held to
the apex it is the M14 full jump exactly; a tap is still the dash; power is the cost of the hold used. Apexes by release tick (press = 0): 6 (the
lift-off) 2.0 m, 10 5.3, 15 9.0, 20 11.8, 30 15.7, 40 17.4, full 17.5. The own record's jump byte carries the cut (bit 6, no size change). Nav jump
holds 1, 14, 19, 60 (`NavJumpFlight.SimulationVersion` 4); bots hold up to `JumpPack.FullRiseTicks`. The flight clips are posed by the vertical angle of motion as the graph binds them (no falling clip exists: the fall is the far end of
the same clips; `SmContent.JumpFlightPose`, damped 0.15 s, low or high set by the height above the ground; `--loop-flight` restores the M14 loop), so after
the cut and the apex the marine visibly falls; the own player's apex sound and effect come at the release. The cut's shape is the owner's decision; the
0.1 s / 0.05 s values are ours. Spec "The middle jump (M45)"; `JumpHeightTests`, `JumpFlightPoseTests`; `smtool graph-dump --jump-flight`.

Match flow is done (branch fidelity/match-flow, fidelity gap 4; protocol 25: no layout change, the lock rule must agree): `MatchState.Countdown` (`MatchRules.CountdownTime`, server default 5 s, `--countdown <s>`, ours) between warm-up and play: everyone respawns, movement and looking only, Fire/melee/charge/grenade dropped from the input of each countdown tick (server steps S+1..S+L of a snapshot at S with L ticks left; the client sends raw buttons and predicts with `MatchFlowWire.Locks` by tick, in the step and the reconcile replay), nobody hurt, no capture; sent as `Warmup` with ticks left above 0 (`MatchFlowWire`). Client (`MatchFlowView`, `NetworkView.MatchFlow.cs`): "MATCH BEGINS:" over 00:05..00:01 (`HudText.gfx` `countdown_mc`); at the end a 6.5 s victory/defeat/draw splash for the viewer's side on the clips' own timeline (195 frames at 30 fps; bands, word fly-in, fade; band look and word position approximate), then the scoreboard with "NEXT MAP - m:ss" counting the 15 s restart (the old top banner is hidden during the splash). Texts from the install's locale at runtime; sounds from the install: `play_mp_countdown` (riser, started 1.15 s before play: ours) and the music stingers `play_mp_sm_victory` / `_sm_loss` / `_chaos_victory` / `_chaos_loss` (a draw has none): music segments and tracks are read and played as one-shot 2D tracks (`AudioCatalog.AddMusic`; no tempo, transitions or music states). Not done: next-map vote. Spec "Match flow", `docs/discovery.md` "Match flow", `docs/changes-from-original.md`.

Match summary is done (branch fidelity/match-summary, fidelity gap 4 rest; client only, no protocol change): after the splash the end runs on the server's restart clock (`SmNet/MatchSummary.cs` `MatchEndTimeline`, re-synced by every snapshot so it agrees with NEXT MAP): splash 6.5 s, a score count-up 3.0 s (`EndOfArena.gfx` stages: Team Score, then the viewer's kills over the side's best four players; the original's third stage, XP, and its per-player score do not exist here), a battle record 3.0 s (BEST CLASS / BEST WEAPON / ENEMY KILLS / PLAYER DEATHS / KILL RATIO / TEAM ASSISTS (dash) / BEST KILLSTREAK / NEMESIS, plus SEIZED for the side in Seize Ground; labels are the install's locale 106085-106091, 106094, tab names `SummaryScreen.gfx`), then the scoreboard with NEXT MAP for the remaining 2.5 s or more of the 15 s restart; a held Tab shows the scoreboard over them. The split of the 15 s, the counting curve and the layout are ours (the movies' timers are script values). Statistics are built from the kill feed every client receives (`MatchStats`: kills, deaths, streaks, weapons, classes, nemesis; the totals shown are the scoreboard's; a late joiner sees partial ones) and from control points changing owner. The Tab scoreboard now has the original's columns (RATIO computed; ASSISTS, SEIZED, DEFENDED, level and score are dashes because the wire does not carry them): what the server would send for those (7 bytes a row, protocol 31 or later) is in `docs/scoreboard-stats-design.md`. Sound: `play_mp_points_total` at the count-up, stopped at the record. Test: `--wait-match-end --screenshot-every 1` captures the sequence; logs "match flow: score count-up begins / battle record / scoreboard". Spec "Match flow: the score count-up and the summary", `docs/discovery.md` "End-of-match screens".

Fidelity gap 1 is done (branch `fidelity/pickups`; protocol 26): ammunition and grenade drops. Every death drops `pickup_mp_drop_ammo` or
`pickup_mp_drop_grenades` (the player blueprints' `death_action` `random_actions`, weights 3:1; the draw a hash of tick and victim, ours) on the ground
below the death point; one Interact press (`InputButtons.Interact`, bit 12; PC key E, ours; keyboard turning moved to the arrow keys) within the data's
4 m collects it (anyone; contested: nearest presser, ours); amounts by the weapons' ammunition names (bolter +60, storm bolter +125, plasma gun +40,
heavy bolter +100, launcher +6, stalker +15, melta +4, plasma cannon +50, lascannon +8, bolt pistol +20, plasma pistol +35; grenades +1) into the
reserve (clip-only weapons: the clip), capped (ours); 25 s lifetime (`SmSim/Pickups.cs`, `SmContent/PickupTuning.cs`). Pickups travel outside the
snapshots in `PickupUpdate` (kind 15; the live list plus the last second's collections and expiries, redundant sends; snapshots unchanged). Client
(`NetworkView.Pickups.cs`, `PickupView.cs`): the original `ammocase_bolter_small` (in `data/chunks/ebp_sets__mp_versus_set.oppc`), the locale prompt
("Pick up Ammo" / "Pick up Equipment"), `pickup_ammo_take` and `play_pickup_ammo/grenades` on collection, the grenade drop's `pickup_grenades` while
it lies there. Not done: HUD world icons; the pick-up clip is not in the MP rig. Spec "Ammunition and grenade drops"; `docs/changes-from-original.md`;
controls in `docs/playtest/CONTROLS.md`.

Ork O4 is done (branch `ork/o4-sim`; no protocol change; off by default): the Ork Boy in content and simulation (docs/ork-faction-design.md
sections 1 and 2). `GameplayTuning.Load(gameDir, OrkBalanceMode.Parity | Data)` appends `ork - boy` as class 6 (`SmContent.OrkClass`: the
Exterminatus boy's blueprint, slugga, shoota, choppa with its sprint attack as the charge, stikkbomb, side-only dodge hop, the boy rig's hit volumes;
the Tactical's perks; no side). `SmContent.OrkBalance`: Parity (the owner's Tactical parity: 100 + 100, shoota 22, slugga 75, the knife's hits, the
bull rush's 75 + 25, the frag's stikkbomb, the hop timed like the roll; a deliberate change) or Data (the data's values); `smtool ork-balance
<game-dir|auto>` prints the rows. Simulation: class bodies (`SimConfig.Bodies`, `ClassLoadout.Body`, `SimConfig.For(class)`: speeds, health,
shield, regeneration, hit volumes; every hit test uses the target's), `World.AssignSide` / `Spawn(.., team)` (an Ork joins the side it asks for
under the balance rule), `EvadeSpec.SideOnly`. Configs without the Ork hash exactly as before (pinned; install `BD42193EA8C48334`). O4b (owner's
decision 2026-10-28): the Ork keeps its own hitbox (the boy rig's ragdoll; the campaign boy's is identical) and in Parity every movement speed,
aiming included, is the Tactical's. Next: O5 (wire, server side).

Ork O5 is done (branch `ork/o5-wire`; protocol 28): the Ork plays. `smserver` and the networked client load `GameplayTuning.LoadForMatch` (the Ork Boy as
class 6 in Parity; `--ork-data` on both: Data mode, a test aid), so the config hashes agree (install, Parity: `28070D2ECC8C9C09`); marine and Chaos classes
hash as before (a class's race is hashed only when it has one). `SmSim.Race` (1 Space Marines, 2 Chaos, 3 Orks; `ClassLoadout.Race`, `Races.Of`: the class's
race, else the side's) replaces `Team` as an appearance's race (`Appearance` message, `RemoteAppearance`, `IAppearanceRules.Apply`). The Hello (176 bytes) and
Setup (139) end with the Ork look (0..4 = `OrkAssets.Looks`, 255 automatic); an Ork's code is `Races.OrkCode(look)`, rebuilt by `OrkAppearanceRules` (no
pieces, colours, decals, lens or emblem: no emblem pull for an Ork). `GameServer.OnHello` passes `World.AssignSide` to `World.Spawn(.., team:)`; a class
change to or from the Ork changes the race at the next respawn (`AppearanceState.PendingRace`). The client's hit report fills the victim bar from the
victim class's body (`GameClient.VictimBody`). Snapshots unchanged (worst case 1200). Headless check: an Ork bot (`--class ork --side chaos`) and a
Tactical bot fought on 127.0.0.1 with no errors (stikkbomb and slugga kills). Review follow-ups: a failed Ork read (any non-fatal exception, or
values the config refuses) means "no Ork, note added" and keeps the six classes and their hash (`MpClasses.WithOrk`); the server then prints a loud
WARNING; the ConfigMismatch refusal names `--ork-data`; the HUD and kill feed say shoota, slugga, choppa, stikkbomb; docs/playtest/KNOWN-ISSUES.md
lists the placeholder model against the Ork's own smaller hit area. Gaps: the networked Ork was still drawn as the default marine (done in O7a below); no
customizer faction step or look choice yet, the client sends "automatic" (O9). Spec "The Ork Boy on the wire and in the server (Ork O5)".

Ork O7a is done (branch `ork/o7a-model`, based on `ork/o5-wire`; client only, no protocol change): the networked Ork is drawn as an Ork (docs/ork-faction-design.md
sections 4, 5 and 8 row O7a). `client/NetworkView.Ork.cs`: a player whose class has the Ork race wears the boy's original skinned model on the boy's own rig (`OrkAssets`; the
look from its appearance, "automatic" by player id mod 4: `SmContent.OrkLookRules`, tested; never waits: the default marine stands in until the look is built, then the
character is replaced, not re-skinned), the boy's weapons on its prop bones (the shoota, slugga, choppa; the stikkbomb in hand from the start of a throw to its release;
`WeaponAssets` of the Ork rig, its catalogue read on a worker: `WeaponAssets.FromCatalogue`) and moves with the boy's own clips (`SmContent.OrkLocomotionPlan`, built from the
rig's hip-fire and locomotion data: the 2HR aim run of four directions with aim-down / forward / up clips for the shoota, the 1HM run for the slugga, one 1HM sprint, the idle
with additive aim offsets standing; blended by the M40-T4 blend space, `GaitPlace`, with the strides measured on the boy's own feet: `FootBones`, `GaitLibrary.From(.., rigKey,
feet)`; leg IK knows the boy's leg bones; `smtool graph-dump --gaits | --bones --rig ork`). The corpse is the boy's own ragdoll (`NetworkView.OrkRagdollDefinition`; the death push keeps the marine mass for parity: the Ork's own 271 kg would throw it 2.9 times as fast, a playtest item). Every client reads the Ork rig and starts the Ork locomotion and weapon reads on workers while the map loads (`WarmOrk`, one 3 s deadline with the marines' warm-up); an Ork player also builds its look(s). The aimed-enemy bar's maxima are the hit report's (`SmNet.HitBars`: the class body, not a perk's). A race change at the respawn drops the old rig for the placeholder at once. **Left for O7b / O7c** (the Ork shows its
movement pose meanwhile, never a T-pose): hit reactions, the charge, melee swings, the dodge hop, the death clips, turn-in-place and run-start rules, the shot recoil, reload,
the throw's release mapping, sounds (O8). `--no-ork-model` draws the old placeholder marine (before / after runs). Checked headless on 127.0.0.1: two Ork bots on opposite
sides and a Chaos marine bot fought with no exception (stikkbomb, slugga, shoota and melee kills, Ork ragdolls); a windowed off-screen client saw four Orks run, idle and
fight (screenshots in the private cache `ork-o7a`); four Orks on screen cost a steady 5.9 ms a frame (168 fps; the same scene with placeholder marines 7.2 ms). Spec "The networked
Ork: model and locomotion (Ork O7a)".

Ork O7b / O7c are done (branch `ork/o7bc-actions`, on `ork/o7a-model`; client only, no protocol change; docs/ork-faction-design.md rows O7b and O7c): the networked Ork's actions
and reactions in the boy's own clips, built from its behaviour graphs and laid over the times the simulation runs (`client/NetworkView.OrkActions.cs`, `OrkActionPose` in the marines'
order; engine-free `SmContent/OrkActions.cs`: `OrkActionPlan` read with the locomotion on the same worker, `OrkActionRules`, tests `OrkActionsTests`; `smtool ork-actions
<game-dir|auto>` prints every clip, timing and rate). Shot: the boy's additive recoil `BOY_2HR_RNG_Fire_S_01` per shot seen (slugga too). Reload: `BOY_2HR_RNG_Reload_S_01` with the
graph's upper-body mask over the weapon's reload time through the M35 machinery (the shoota 2.05x, the slugga 5.5x: the clip is 4.1 s; the Ork guns have no magazine mesh, so none
drops). Throw: `BOY_2HR_RNG_GrenadeToss` warped so its release (0.711 s) is the simulation's 0.2 s and its `BRANCH_Run` the 0.667 s end. Reactions: `BOY_REACTIONS_Coop` light / heavy
front and back, each clip to its `BRANCH_Reaction_End` over the marine time the simulation holds the player (a stun 3.3x: the owner may want the Ork's own reaction timing later).
Melee: the choppa's combo and heavy clips the steps were read from, with the marines' leg hand-off while the Ork moves. Charge: the sprint attack; the collision stop plays on from its
hit. Hop: `Evade4m_L/R` over the hop's 1.167 s (about 2x), the body lowered by the hop height. Death: the boy's death clip for the blow (front / behind / melee side) until the graph's
`Pre-Powered Ragdoll` window ends (0.4 to 0.93 s), then the Ork ragdoll starts from that pose (`OrkDeathHold`; the powered ragdoll is not modelled). The boy has no relaxed stance,
turn-in-place, run-start or side-hit clips (so hip fire and the aim pose are the O7a run; those rules are absent); stretching a clip to the simulation's time is ours and tagged. The
marine and Chaos paths are unchanged (the marine hooks only skip the Ork class's clips). `--no-ork-actions`, `--ork-capture <dir>` (windowed screenshots per action). Checked headless
on 127.0.0.1 (ports 27290 to 27299): Ork bots on opposite sides with `--bot-melee` and the default fight used every action (shots, reloads, stikkbomb throws, interrupts and stuns,
choppa swings, charges, hops, ten Ork deaths by shot, blast and melee) with no exception; off-screen screenshots in the private cache `ork-o7bc`. Spec "The networked Ork: actions and
reactions (Ork O7b / O7c)".
Ork O8 and fidelity gap 9 are done (branch `sound/ork-and-match`, on `ork/o7a-model`; client only, no protocol change): the Ork's sounds and barks, and the missing match and menu sounds.
**Ork**: a player of the Ork race (`IsOrkVoice`, independent of the model) sounds as the Exterminatus boy: `SoundTriggers.PlayerBlueprints` gains `ork_coop_boy`, the boy rig's behaviour graphs
add their clip tags to the marine rig's table (`OrkSounds.WithOrk`, read on the sound worker), the Ork class's speech character is the blueprint's `audio_ext/character` = `ork_boy_2` (the speech
bank has a line for every exertion his files name). Played from the data: footsteps on `EVT_Left/Right_Foot` (`play_or_boy_footstep` on the terrain surface), the landing, the death action, the
shoota's, slugga's and choppa's sounds, and every `ACTN_SND_Vox_*` tag a shown clip crosses (the run vox `play_or_bo_2_run` about every 1.2 s: data, a playtest item). While the boy's reaction, melee and death
clips are not drawn (O7c) the simulation's events post his lines (**ours**, `OrkSounds` rules tested): the death yell (cuts off a grunt still playing: `SoundSystem.StopSpeech`), a small or big grunt for damage taken
(25 and over; the own Ork from its own record, a remote Ork from the shot records that hit it: health is not on the wire for others; melee and blast hits on a remote Ork are silent), the stun line, the dodge hop's landing and effort, the choppa swing at the start of a light attack (its swing tag within 0.45 s is skipped), and the silly options (`--no-ork-silly` off): a roar at a charge or
sprint start and a taunt 0.6 s after a kill by an Ork standing still (`OrkKillSound`, one line in `NetworkView.Match.cs`). `smtool ork-sounds` lists every command resolved against the banks and the boy's clip tags;
`smtool audio-coverage` / `audio-tags` now include the Ork; `--log-sound ork|bark|match|ui` print each post with its voices. **Match sounds** (`SmNet/SeizeSounds.cs`, tested): the capture sound
`play_mp_point_being_captured` turned out to be a 36 ms click, not a loop (its stop event exists): one tick a second while the local player stands on a moving point, `Play_Objective_Complete` when a
point becomes the local side's, a quiet `Play_MP_Points_Total` tick when the local score rises (the events are the data's, the moments ours). **Menus** (`client/UiSounds.cs`, `SmContent/UiSoundRules.cs`): the front-end
movies' `Play_Menu_Move` / `_Select` / `_Back` on every button under an attached root, the front end's layer and the in-match picker's (`UiSounds.Attach`; own two-package catalogue opened when the first root is attached, disposed when a match starts and handed to its sound system;
`AudioCatalog.OpenUi`). The reload-sound scan is per rig (`SoundTriggers.OrkOnlyClips`). Checked headless on 127.0.0.1 (ports 27300 to 27309): Ork bots on both sides in Seize Ground resolved every post to voices in the logs, front-end self-test buttons and the redirect into a match
posted menu sounds; nothing was listened to. Gaps: the boy's clips' own tags once O7c shows them, the run vox's loudness with many Orks, the music, the menu-opening and multiplayer-select events. Spec "Ork sounds and barks (Ork O8)"
and "Match sounds and menu sounds (fidelity gap 9)"; docs/discovery.md "Ork sounds and the menu and Seize Ground events".

Owner's decisions of 2026-10-28 (perks, bolters, blind grenade; branch `owner/perks-bolters-blind`; protocol 27 unchanged: all server-side, the
config hash changes; install `7425AC9F54C25F89`, with the Ork in parity `FD508E105A086546` (since O5, which hashes the Ork's race: `28070D2ECC8C9C09`), data values `1F039647D5490FDC`): everything unlocked
(`UnlockMode.All`), any perk of the class in either slot and pistol-slot bolters only with True Grit already held and are pinned (`OwnerDecisionTests`).
**Deliberate changes from the original** (docs/changes-from-original.md): `SmContent.OwnerBalance` (one override table, applied in `MpClasses.Load`
before the Ork, so the shoota's parity reference is the buffed bolter: shoota 25.4) raises the standard bolter to 25.4 per hit (data 22) and the
True Grit bolter to 39 (data 22), so against a full marine at 15 m the bolter matches the bolt pistol and the True Grit bolter is 25 % above it (ours),
burst and sustained (on health alone the data's bolter already beat the pistol; its 0.505 shield multiplier made it the slower killer); each Kraken
Bolts variant is measured against the same target on its own (bolter + Kraken 23.2, True Grit + Kraken 29.4); 30 m changes nothing (fall-off starts
at 30 / 45 m). `smtool weapon-dps <game-dir|auto> [--data] [--range m]` prints the table; `GameplayTuning.Load(ownerBalance: false)` the data. The
comparison basis (vs a full marine) is pending the owner's confirmation. The blind grenade blinds only players facing the blast
(`SimConfig.BlindViewHalfAngle` 60 degrees, ours; `World.Faces`; line of sight and the 3.5 s / 2 s bands unchanged; 180 = SM1's distance-only
rule; the server's startup listing says so). Spec "Owner's bolter balance", "Blind grenade (M37)" row "Facing".

Weapons task A (owner's knowledge, 2026-10-28; branch `weapons/a-race-parity`; protocol unchanged, the config hash changes): "weapons are identical in stats
and behaviors between chaos and loyalists space marines ... the plasma of the chaos marines is purple while the marines is blue." `SmContent.RaceParity`
(24 `csm_mp_*` / `sm_mp_*` pairs, two renamed melee weapons, two files with no twin; `Apply` in `MpClasses.Load` before `OwnerBalance`) copies each loyalist
twin's numbers and behaviour (weapon, grenade, jump pack, melee combo, perk variants, charged-shot fire delays) onto the Chaos spec and keeps the Chaos
`Name` / `Wargear` and every projectile `Blueprint`, so models, effects and sounds stay Chaos. On the install the data already agreed except one value (the
Chaos frag grenade's `ground_angle` 54, loyalist 65); the "cosmetic delays" in the Chaos files are the melta's recharge effect pips (not simulated), the
simulated fire delays are equal; the Chaos classes' defaults were already the loyalist twins (pinned). Plasma: the Chaos files play their own purple
effects but share some loyalist-named blue ones (charged blast, impacts), which `PlasmaColour.TintFor` recolours purple through `FxEffect.Tint` when a Chaos
plasma weapon plays them (ours, RGB 0.85 0.36 1.0); the placeholder ball follows the side. `smtool race-parity <game-dir|auto> [--raw] [--fx-colours [word]]`
prints the pairs, the differences before and after, the class defaults, the config hashes and the effect colours; `FxTest --fx-wargear <csm_...>` shows the
tint. Spec "Chaos weapons follow their loyalist twins (owner)", `docs/changes-from-original.md` (two rows).
Weapons B is done (fidelity gap 7; branch `weapons/b-brace-vent`; protocol 29): the heavy bolter's brace and the plasma vent, both on Reload (the weapons' locale
texts "to Change Stance" / "to Vent Heat" and the graph's `BTN_Reload` transitions), with the owner's recollection of 2026-10-28 applied as estimates to review in a
playtest (spec "Brace and vent", kind "estimate (owner's recollection)"). `SmSim/Brace.cs`: Reload enters the stance (1.433 s, `SM_RNG_TB_Idle_to_BraceIdle` to
`BRANCH_Idle_to_Brace`; cannot be cancelled), braced fire is the wargear's `ability_ranged_braced` (0.1 s against 0.233: the owner's "moderately high" rate; 1.8 heat; his "increased accuracy" waits for spread: the `_braced` scatter sets), the press tick stops the body (ours),
braced the player only turns on the spot, Reload exits (1.300 s at the entry's speed; Heavy Bolter Expertise halves entry and exit); no reaction knocks a player out of
any brace phase (`Brace.ReactionImmune` in `Reaction.CanReact`; transitions: ours); exiting he walks, a Jump that starts an evade ends the exit. `SmSim/Vent.cs`: Reload
vents 45 heat/s (`heat_vented`) on top of the passive cooling, an overheat vents by itself (cannon ×0.6), everything but standing or walking stops it (standing: ours);
the plasma cannon's overheat halves its carrier's speed for 1.5 s. **Vent rate:** the owner chose the data's rate (2026-10-28): a full gun cools in about 2.1 s while venting; his recollection of
about 6 s is a refine-register item to check in a later playtest. Timings from the graphs (`SmContent.BraceTuning`, `VentAnimations`; `smtool graph-dump --brace`). Perks active: Heavy Bolter
Expertise and Plasma Heat Sink (pistol vent 70/s via `mp_plasma_pistol_upgraded_vent` and the pistol graph's "Upgraded Vent State"). Wire: slot byte bits 4..5 brace
phase, 6 vent; the own record's Reload byte carries a transition's ticks (6 bits); snapshot worst case still 1200. Client `NetworkView.Brace.cs`: brace and vent poses,
sounds, vent effects, HUD words; `--no-brace-vent`; bot test aid `--bot-brace`.
Weapon spread is done (branch `weapons/spread`; the owner's decision of 2026-10-28, reversing M27's "hip = aimed"; no protocol change, the config
hash changes): `SmContent.SpreadTuning` reads each firing ability's `kick_back_and_scatter_values` (standing / aiming × idle / walking / running; perk
entries through `UpgradeScope`) and the named sets in `data/packages/climb_sway_scatter.oppc` (`scatter`, `climb`, `sway`) into `WeaponSpec.Spread`
(also per charge stage). `SmSim.Spread` (hooked after `Combat.StepPlayer`; `Combat.ShotAim(input, c, shooter)`): a bloom per player (+increase per shot,
capped, decaying after the delay; cleared by a switch or death), each shot turned by a point uniform in the ellipse of the current half-widths
(degrees: interpretation), seeded by player id and input tick so the prediction reproduces it; inside the 30° cone; hit-scan, bursts, charged shots,
projectiles and the melta's axis (not throws). The bloom is not on the wire: the client keeps its predicted value at reconcile (`Spread.Carry`).
Bolter Targeter, Improved Twin-Link and Burst Fire switch scatter sets; Lascannon Stabilizers only climb and sway. Client (`NetworkView.Spread.cs`):
the own tracer and impact use the predicted scatter; the crosshair's ticks open with it (`Hud.gfx` names `min_aim` / `max_aim`; ours); the data's
climb and sway kick the own camera (humans only, interpretation; `--no-handling`). Perks: 36 implemented (2 partly), 12 inert. Test bots (owner's
request): default `--bot-accuracy` 0.03 and a trigger discipline (`SmNet.BotFireDiscipline`: 0.6 s reaction on a new target, 0.8 s bursts, 0.9 s
pauses; ours, off with perfect aim or `--bot-no-discipline`). Spec "Weapon spread", "Test bots"; `SpreadTests`, `SpreadTuningTests`.

Responsive controls are done (branch `feel/controls`; client only, no protocol change; the owner's request of 2026-10-29): measured with `--latency-probe`
(headless human client, scripted events through Godot's input queue). Taps shorter than a tick are no longer lost (`SmNet.InputLatch`: one-frame clicks
1 → 14 of 16, the 2 others in the automatic reload; melee taps 0 → 16 of 16); a press or release of a game button or movement key runs the next client tick at
once (`GameClient.Update(.., tickNow)`, `SmNet.EarlyTick`: same tick number, schedule kept, prediction equals the server tick for tick; fire, melee, stun, roll,
jump, grenade, reload, switch, aim: mean 5 to 25 ms → under 1 ms to the predicted reaction and its muzzle flash, sound or clip); the own position stays on the
schedule (`GameClient.DrawPair`); the converged shot uses the view of the moment (`SmNet.FreshAim`: 5.1 → 0 degrees behind the crosshair at 300 degrees a
second, 60 fps); own attack, stun, throw, reaction and charge clips at the frame rate (`SmNet.OwnClipClock`: still in 50 / 76 % of frames at 60 / 144 fps → 0).
`--old-controls` (or `--no-tap-latch`, `--no-early-tick`, `--no-fresh-aim`, `--no-own-clip-smoothing`) for before / after. Spec "Responsive controls (feel/controls)".
Open: the owner's hands-on feel check; vsync and the swap chain are not changed.

Visible bullets on the damaging path are done (branch `feel/bullets`; client only, no protocol change; the owner's request of 2026-10-29): the tracer was a straight
line muzzle to end, 0.33 to 0.75 m (mean; worst 1.9 m) off the shot's true ray at 5 to 40 m; the bullet (the weapon's tracer effect) now leaves the muzzle, joins the true ray 3 m on
(ours) and flies along it to the end (pierce and scatter included; one tracer effect per shot from the join point plus a pooled muzzle-leg streak, so the effect pool is pressed no harder than before and 96.5 % of shots in a 6v6 bot fight are on the true ray (feel/bullets-2); lascannon beam instant; a weapon with no tracer gets a pooled streak). Own shots use the input
actually sent through the server's `Combat.ShotOrigin` / `ShotDirection`; other players' rays are rebuilt from the shooter's camera rig (the records carry only the end: origin error mean 0.075 m vs 0.91 m
for the eye). Head speed unchanged (M31, data `USpeed`, at most 0.1 s). `client/Bullets/BulletPath.cs` (engine-free, tested), `NetworkView.Bullets.cs`; aids `--no-bullets`, `--show-shot-rays`,
`--bullet-speed`, `--bullet-streaks`, `--bullet-probe`. Spec "Visible bullets" rows.

Weapon smoke reined in is done (branch `feel/muzzle-smoke`; client only; the owner's request of 2026-10-29: the smoke of firing is "too spread out from the exact line of travel"): checked first,
nothing misread (barrel axis, radian deviations, no inherited velocity); the spread is the data's own elements (tracer smoke beams up to 1.5 m wide, ignition puffballs, the heavy bolter's hemisphere puff,
the stop-fire streamers). For the effects a weapon plays when it fires, smoke elements keep 0.55 of their width, radius and sideways speed (`FxEffect.SmokeRein`, `NetworkView.Smoke.cs`; the owner's change, the 0.55
ours); flashes, shells, tracers and impacts untouched. `--old-smoke`, `--smoke-rein f`, `--fx-rein f`. Also fixed: an empty immediate-mesh surface in the bullet streak/debug drawer (one engine error per frame). Spec "Weapon smoke" rows.

Scope overlay and lascannon are done (branch `feel/scope-lascannon`; client only; the owner's request of 2026-10-29): (1) the stalker bolter's and lascannon's zoom now shows the original HUD movie's scope art:
`SmFormats.Gfx` reads `Hud.gfx` (Scaleform GFX: sprites, shapes, placements, colour transforms, the exporter's sub-image records; ActionScript skipped), `GfxRaster` draws it (anti-aliased fills, gradients, bitmaps from
the movie's own `hud_i*` atlases, lines), `SmContent.ScopeArt` picks `stalker_idle_zoom_mc` / `stalker_active_zoom_mc` / `lascannon_scope_mc` / `lascannon_scope_active_mc` (mask at alpha 0.6 with a lens opening, rings, ticks; the stalker's
25-frame fade-in clip; the active variant with an enemy in the sights), `client/Hud/ScopeView.cs` shows it under the HUD while aiming and `Hud.Frame.Scoped` drops the normal crosshair (`--no-scope`). Nothing is extracted;
the container's place (stage centre) is an interpretation. (2) The lascannon's beam is the owner's hard red core with a white-hot line and thin glow, at least 2.2 px wide, in world space (`NetworkView.Laser.cs`;
`--old-laser`; the data's beam is a hazy stack of soft layers, nothing misread). (3) Effects the data attaches to the weapon at a shot follow the marker for 0.08 s only, then stay (`FxEffect.FollowFor`; `--old-follow`).
Test aids `--laser-test`, `--no-scope`. Spec "Scope overlay" and "Lascannon beam" rows; tests `GfxTests`, `ScopeKindTests`.

Added content E1 is done (branch `extras/e1-switch`; no protocol change, the config hash refuses mixed profiles): the content switch of the
added-content plan. `SmSim.ContentExtras` flags (`Orks` 1, `Gore` 2; `ContentExtras.WireVersion` 1 (2 since the loyalist barks), `ContentExtras.Compiled`), profiles
(`ContentProfiles`: `faithful`, `extended`, lists; default `extended`), `SimConfig.Extras` hashed only when not None (a faithful config hashes as
before: pinned), the Ork gated in `GameplayTuning.LoadForMatch(gameDir, extras, orkData)` alone, `--content` on the server (startup line, refusal
naming the profile) and the networked client, `Directory.Build.props`/`.targets` (`SmExtras`, `SM_EXTRAS`, `Extras/**` removed when false),
`faithful-build.cmd`, `ContentProfileTests`, `ExtrasBoundaryTests`. Not yet: the envelope message (E3), the Ork's move into `Extras/`
(O-S1..4). Spec "Added content: the content switch (E1)"; `docs/added-content.md`.

Added content E2 is done (branch `extras/e2-ui`; no protocol change; owner's decision 2026-10-29: default profile `extended`, faithful-only one switch away): the
profile in the front end. The Join screen's CONTENT row (Faithful / Extended, remembered in `frontend.json` next to the profile, `--content` seeds it; the Host
panel starts its server with it); `SmSim.ContentGate` (the Ork class, race and look allowed only with the Orks): the customizer's factions
(`CustomizerServices.Factions`), `JoinPlan.Build` / `FrontEndLaunch.Apply` (a Join never sends an Ork class or look; a saved Ork loadout is kept, shown
unavailable and joined as the side's default: `LoadoutAvailability`), the server (`ClassFor`, `World.SetNextLoadout`, `PlayableRace`: default class,
Ork look ignored, no race 3) and the client (drops race 3). `ContentGateTests`, model tests, `--ui-selftest` content checks (52 checks). The customizer has
no Ork step yet (O9): it will take its factions from the same list. Spec "Added content: the content switch (E1)" rows marked E2.
Falls and jump-pack attacks are done (branch `sim/jump-pack-and-falls`; protocol 30; the owner's descriptions and answers of 2026-10-28): the **walk-off drop**
(`Movement.Step`: a body in the air outside a jump-pack flight is not steered and, from its second tick in the air, has no horizontal velocity: it drops straight;
only over a real drop, the feet more than `SimConfig.StraightDropAbove` 1 m above the ground (ours, the review's probe: zeroing at any height stopped runners dead on
every curb); an evade off a real drop stops travelling and drops while its pose plays on; knockback keeps pushing); the **airborne charge** (`Rush.Held`: a bull rush that
leaves the ground keeps exactly its speed, uncapped (owner, 2026-10-29), the clip and its armour and contact windows wait, no end by time or input until it lands, contacts
and wall stops go on, then the clip resumes); the Assault / Raptor **dash attack** (`RushPhase.DashAttack/DashHit/DashStop/DashCollision`, `DashAttackSpec` read by
`SmContent.JumpPackAttackTuning` from `ability_dash_attack` and `SM_Base_MP`: a melee press from the dash flight's `BRANCH_Dash_Attack` 0.333 s within 3 m of
the ground lunges 10.2 m along the view, 75 + 25 and a **stun** (owner's estimate against the data's interrupt, to review), Furious Charge 100 + 50, 30 to others,
then the hit state, wall stop or run-out); the **sword slash** (`SlashSpec`, `JumpPackSpec.Slash`: a melee press in a jump below 8 m above the ground at the press
dives like the pound and 0.133 s after landing hits everyone in the melee cone with `ability_melee_dash`'s 60 and the light interrupt, no stun, knockback or pound
blast; the regular landing blast applies; recovery 0.638 s; from 8 m up the pound). Wire: the melee byte's charge phase in bits 0..2, the slot byte's bit 7 = the
slash (snapshot sizes unchanged, worst case 1200); `NavJumpFlight.SimulationVersion` 5. Client (`NetworkView.DashAttack.cs`, `NetworkView.Jump.cs`): the clips
posed through the charge and jump paths, the pound marker only from 8 m; test aids `--bot-dash-attack`, `--log-falls`. Gaps: the hammer uses the sword's clips and
ability. Spec "Falls, the airborne charge, the dash attack and the sword slash (protocol 30)"; `JumpPackAttackTests`, `JumpPackAttackTuningTests`.
Jump-pack feel is done (branch `feel/jump-pack`; protocol 31: no layout change; the owner's requests of 2026-10-29): fuel **300** (`JumpPackTuning.OwnerCapacity`; data 100,
M44 110; a perk entry keeps the data's ratio; 5 full jumps from full, 30 s to refill); **air control** in the ascent, hang and fall (`JumpPack.AirSteer`: toward stick x max(8 m/s,
speed) at 15 m/s², diagonals the same speed, no height, never faster than max(8, the speed before); released: the data's drift; ours); the **air slash** (`SlashSpec.Snappy`): its own
straight 40 m/s dive toward the point under the aim (at least 40 degrees down; `JumpPack.SlashDirection`), a 6 m/s landing lunge for 0.2 s, free 0.3 s after the landing (data
0.638 s), a melee press in the take-off is buffered to the tick after the lift-off, no pound delay; press to free 0.40 to 0.57 s (was 0.70 to 0.83); client: the clip holds its first frame through the dive, the 15 m landing's
dust and sound, the hit cone's mark on the ground (`SlashMarkTexture`), dust and chunks at the hit (`GroundPoundFx.PlaySlash`); heavier pound effects; ground effects' dust soft against
the scene (`FxEffect.SoftGround`, `--no-ground-soft`); a charge held in the air shows the fall pose (`ChargeHeldInAir`). Test aid `--slash-test`. Spec "Jump-pack feel";
`JumpPackFeelTests`. A parked branch (`extras/e3-envelope`) also claims protocol 31: renumber it.
Input buffering and slower movement are done (branch `feel/buffer-speed`; protocol 33, after `feel/melee-lock`'s 32; a replayed melee press picks and keeps its lock like a fresh one (`InputBuffer.Replayed` in `GameClient`); the owner's requests
of 2026-10-29, deliberate changes, rules and values ours): **input buffering** (`SmSim.InputBuffer`, wired in `Combat.StepPlayer`): a press of Jump, melee, heavy melee, Grenade,
Switch / slot key or Reload that a running action or recovery blocks (`InputBuffer.Locked`) is kept and replayed as a fresh press until its action starts, for 0.3 s
(`SimConfig.InputBufferWindow`, `GameplayTuning.OwnerInputBuffer`; 9 ticks) and beyond that while its button stays held (letting go after the window cancels it); one at a
time, a newer press replaces it; the hold rule only for Jump and melee, never in a hit reaction or for Jump in the air without a pack (review); Fire never; Jump in a jump-pack take-off or ascent is the hold, never buffered; death clears it. Space held through the air slash's landing
starts the take-off on the recovery's first free tick (0 ticks; lift-off 6 ticks later; before: none while held), measured on the install and with live headless bots
(`--slash-test --slash-jump`, Assault and Raptor). Own record: the thrown-equipment flag byte's bits 3..7 and the raise byte's bits 6..7 (raise ticks now at most 63);
worst case still 1200. **Slower movement** × 0.9 (`SmContent.MovementScale`, `SimConfig.MoveSpeedScale` in the hash): ground speeds and the acceleration cap (run 5.85, sprint 9;
same 6 ticks to full speed), bodies, the jump pack's forward speed 14.4 and air control 7.2 m/s / 13.5 m/s², the dash's and the heavy hop's travel; not the charge, dash attack,
slash, pound dive, melee lunges or the Tactical roll; the charge's sprint gate follows (6.6375 m/s). `GameplayTuning.Load(ownerFeel: false)`: the data's speeds, no buffer.
Spec "Input buffering and slower movement"; `InputBufferTests`, `MovementScaleTests`.

Shorter locks and the sprint stop are done (branch `feel/locks-sprint-stop`; protocol 36, other open branches have 34 and 35: the integrator renumbers; the owner's requests
of 2026-10-29, deliberate changes): **lock scale × 0.7** (`SimConfig.ActionLockScale`, `LockTime`, `GameplayTuning.OwnerLockScale`; an **estimate (owner's request)** to review,
in the hash) on every move / jump / evade gate of an action: melee `MoveAt` (`Combat.MoveAtOf`; the client's leg hand-over follows), the roll's and hop's stick and next-evade
branches, the dash landing's, the evade spacing 0.8 → 0.56 s, the reactions' stick and Jump branches where the data has them before the clip's end (stun and stagger lengths stay the data's; never before the knockback ends; `Reaction.EscapeAt`), the bull rush's and
its collision stop's, a throw's evade gate (`Combat.ThrowHolds`, never before the release); not hit or damage times, lengths, combo / fire / switch / melee branches, the dash
attack, the brace, nor (on that branch) the jump pack's landing locks (feel/jump-pack-2 scales those and the dash attack's exits too). **Jump out of a melee attack**: each attack's `BRANCH_Evade`
(`MeleeStep.EvadeAt`, read by `MeleeTuning`; knife light 0.48 / 0.47 / 0.60 s) lets a Jump press end it and roll, hop or take off (before: not until the attack's end), scaled
but not before the hit when the data opens it after the hit (`Combat.MeleeEvadeOpen`, `MeleeGateAt`). **Sprint stop** (`SmSim.SprintStop`, `PlayerState.SprintStop`): every movement
key let go above the sprint gate (6.64 m/s) slows the body along its velocity (from at most sprint speed) with v² falling at a constant rate over `SimConfig.SprintStopTime` = the stop clip's
`BRANCH_Sprint_Finished` 0.833 s (`SprintStopTuning`; `SM_NAV_Bullrush2Idle_F_01`, 5.79 m root motion): 5.0 m from 9 m/s (before: 0.16 to 0.25 s); a movement key, Jump,
Fire or Aim cancels it into the normal movement; every record's melee byte carries it (64 with step 0, time 0); `SprintStop.ClipTime(speed)` for the client's pose (not posed yet).
Snapshots unchanged (1200). `smtool graph-dump --root-motion [clip..]`. Spec "Action lock scale and the sprint stop"; `LockScaleSprintStopTests` (prints the install's before / after table).

Melee lock is done (branch `feel/melee-lock`; protocol 32, another branch has 31: the integrator renumbers; the owner's knowledge of SM1, 2026-10-29: melee, the
stun strike and the charge "stick" to an enemy and "line up"; every swing steps forward). `SmSim/MeleeLock.cs`: the client picks a lock (global
`directional_targeting_ext`: 45° cone along the stick or view, apex 1 m back, 7 m, 1.5 m high, 1 m distance / 6° angle bands; line of sight; enemies only) from
its view at the input's view tick (`GameClient.ViewAt`) and sends it in the input (u16; 38 bytes an input); the server keeps it while it holds in the rewound
world (`World.SeenBy`) and in that tick's cone (`MeleeLock.Phase`: start along the stick or view, under way along the heading or stick; not through
the other side's spawn blocker), a combo keeps it (1 m leash, ours); lock ticks view the snapshot the client holds nearest their view tick, so the prediction
is exact (tests on flat ground, in a room with a crate, the steered charge). Each attack's `MeleeStep.Reach` (`SmContent.MeleeMotion`, `smtool graph-dump --melee`): its
`AttackAlignment` window (the heading turns to the target, at most 1080°/s, then holds), its distance clips (blend weights 1 / 3.5 / 7 on `FLT_MeleeTargetDistance`)
with their extracted root motion: the body follows it on the ground until `MoveAt` (after it while the stick is idle); without a target the graph's start blend
(the 1 m clip; raised where it backs off), with one the blend at the target's start distance (re-solved each tick), never nearer than 1 m; the bull rush steers to its
lock (180°/s within 45°). Client (`NetworkView.MeleeLock.cs`, rules in `SmNet.MeleePresentation`): distance clips blended at the target's start distance,
root-driven legs (no hand-over), camera assist toward the lock; the hit-stop 0.07 s and hit shakes are OFF by default since 2026-10-29 (owner; opt-in `--hit-stop`); `--no-melee-lock`, `--no-melee-assist`,
`--no-melee-variants`; `--anim-probe` reports the melee clips' foot slide. Snapshots unchanged (1200); install config `1290249E470ACC44` (after the merge with main ce23c26). Spec "Melee target lock,
alignment and lunge"; `MeleeLockTests`, `MeleePresentationTests`, `MeleeLockInstallTests`.

Jump pack paint is done (branch fix/jump-pack-paint, the owner's request 2026-10-29; client and content only, no protocol change): the worn jump pack (Assault, Raptor)
takes the **chest (torso) paint of the look** (`SmContent/Customization/WornPackPaint.cs`, owner's decision; the data's own slot would be the hidden `backpack` entry, which
both default schemes give the same colours as the torso, so the default look is unchanged): `AppearanceAssets.Build` records it in `MarineAssets.PackPaint`,
`WeaponRig.SetPackPaint` puts per-look surface overrides on the pack's meshes (the baked mesh keeps the side's default; materials shared in `WeaponAssets.PackMaterial`),
called every frame by the match (`SyncWeapons`: own look, others' from their codes, `CloneTo` for corpses) and the customizer's preview. Test: `PackPaintTests` (the rule),
`--ui-selftest` (the preview's pack material colour follows the chest swatch), bot log "jump pack is drawn in ...". Backpack weapons are unchanged. Spec "Worn jump pack paint".

Animation pass 3 is done (branch `anim/p3-transitions`; client only, no protocol change; the owner's playtest of 2026-10-29): transitions and connecting clips. Cross-fades start from a
moving copy of the shown pose (dead blending) on a smooth curve with the graphs' blends where read (charge 0.267 s, out of the sprint 0.333 / 0.1 s, out of evades 0.333 s); the sprint graphs'
stop and start clips (`SmContent.SprintTransitions`, `smtool graph-dump --sprint-edges`; `NetworkView.SprintEdges.cs`), posed by the body's speed / distance; the heavy classes sprint on their
weapon's clips (TB / LC) and the cannons hop on the LC set; the pistol's relaxed idle `SM_NAV_IdleL_CS_01` (data; pose-identical to the old idle for the MP weapons' props, so the Assault / Raptor hold the owner flagged is still open: whether the chainsword is drawn at rest); a charge off a ledge falls at once; foot pins let go softly. Probe
`--transition-probe` (jerk per switch), bot `--transition-sweep`, `--ledge-charge`; `--no-p3` for before / after. Integration point for the simulation's sprint stop: `NetworkView.SimSprintStopping`.
Spec "Transitions, connecting clips, the heavy sprint and the pistol idle (animation pass 3)".

The camera hide and the melee hit presentation are done (branch `feel/camera-hitstop`; client only, no protocol change; the owner's requests of 2026-10-29): the whole own character
(model, weapons, worn packs, the jump pack's thrust / idle / dash effects via `FxEffect.Hidden`, the eye glow) is hidden while the follow camera is within 1.4 m of the head and
shown again at 1.61 m (`SmNet.CameraHideRules`, `NetworkView.CameraHide.cs`; ours: before, 1.0 m hid the model only and left the pack effects; a wall holding the camera 1.29 m out showed
the pauldron in the lens; the camera is not shifted: the rig data has no collision offset); the melee hit-stop and the hit shakes (the attacker's 0.5 degrees and the victim's reaction
shake) are off by default, opt-in `--hit-stop` (`--no-hit-stop` is gone); the camera assist toward the lock stays. Test aids: `--bot-backup [--bot-backup-yaw d]` (with `--aim-test`),
`--camera-hide-log`, `--camera-hide-distance`, `--old-camera-hide`. Spec "The own character hides when the camera is close", `docs/changes-from-original.md`.

Charge directions are done (branch `feel/charge-directions`; protocol 39; the owner's playtest of 2026-10-29, user's knowledge and decision): the bull rush starts on F
with **W held**, no sprint and no speed (`Rush.CanStart`, `Rush.WHeld`; supersedes M31's sprint gate, `Rush.SprintThreshold` and `RushSpec.SprintMargin` are gone; aim mode and the
held trigger no longer make it the stun strike; without W, F is the stun strike; the jump-pack classes still charge from the ground only); A or D held with W turns the charge
**25 degrees** to that side (`RushSpec.SideSteer` in the config hash, `Rush.StartYaw`; a stick in between in proportion to its angle beyond the dead zone; ours, an estimate to review; W + F inside the 1 s spacing, or a mashed F replayed when a charge ends, is the stun strike), except that a
charge locking onto a target starts along the view and steers to it as before (`Combat.StepPlayerCore`); the Assault / Raptor dash attack lunges the stick's way of eight around
the view at the melee press (`Rush.DashYaw`; no stick: the view), the body turned to it playing the data's only clip `SM_JMP_DashAtk_F` (ours). Client: the body takes the
simulation's heading at once through every charge phase (`BodyYawFor` snaps instead of the 720 degree a second follow); the camera stays on the view. Snapshots unchanged (1200).
Tests `ChargeDirectionsTests` (W / W + A / W + D charges and eight dash attacks under 150 ms RTT and 5% loss: 0 mismatches); test aids `--bot-charge-dirs`, `--bot-dash-dirs`
(with `--bot-dash-attack`). Spec "Charge directions", `docs/changes-from-original.md`.

Owner's playtest fixes of 2026-10-29 (branch `fix/hitboxes-grenades-bots`; protocol 37): **jump hitboxes**: a jump-pack player is hit with the ragdoll's volumes carried
by their bones into the clip the client draws (take-off clip to 1/3 s after the press, then 27 flight clips x 9 fractions; `SimConfig.FlightHitVolumes`, `SmContent.PosedHitVolumes`);
one engine-free rule for client and server (`SmSim.FlightPoseRules`: damped fraction, low/high hysteresis, clip keys, take-off window), the server keeping each player's view per tick
(`World.FlightViewOf`); the config hash covers the clip names and counts, not the posed floats (Linux server); the client's crosshair, tracer and pierce picture test the same sets.
**View fraction**: inputs carry how far past their view tick the client drew the others (1/256, 39-byte inputs); shots and cones rewind between two ticks (`World.SeenAt`); melee,
the lock and the charge round to the nearest tick (a lock tick snaps to a held snapshot and sends 0). Shots at the drawn body (`FlyingHitboxProbe`, 80 ms): 68.4 % before, 97.5 % now.
**Grenades** (owner's change; the data's reading is right): a ground bounce first loses speed to the blueprint's Havok restitution 0.65 and friction 2 (`RigidSpec.GroundMaterial`):
frag 4.6-12.3 m after landing -> 0-1.1 m, the fuse unchanged. **Test aid**: `smserver --bot-damage-scale f` (0..1; bots mark themselves with the Hello's flags byte, 177 bytes).
`SimConfig.Hash` is cached per instance. Spec "Flying hitboxes, the view fraction, the grenade bounce and the bots' damage scale", `docs/changes-from-original.md`.
The three jumps, the dash attack's walls and the air slash's swing are done (branch `feel/jump-pack-2`; protocol 38, 37 is fix/hitboxes-grenades-bots'; the owner's
requests of 2026-10-29): **jump by the hold** (`JumpPackSpec.HoldTiers`, `JumpPackTuning.ThreeJumps`; owner's decision, "revert to the old jump behavior"): Space let go by
tick 5 after the press (the dash's 0.167 s decision) = the dash; down on tick 5 and let go by tick 12 = the **small jump**, one apex for every medium hold (7.70 m: the hold at
the decision tick; a release while rising decelerates the rise to it, `JumpPack.TierCutGravity`; costs 35); down on tick 12 (the height map's 0.4 s) = the **full jump**
(17.54 m, M14 tick for tick; 55). M45's release cut is off (`ReleaseStopsRise` false; the rule stays as an option). **Dash attack walls**: the lunge meets walls from its first
tick and within 60 degrees of head-on (`JumpPackAttackTuning.DashWallsFromStart`, `DashWallHitAngle`; was from 0.3 s and 25 degrees) into the data's wall state
(`SM_NAV_Bullrush2Idle_F_Collision_01`, the bull rush's connect clip); own camera knock; `--dash-wall-test`. **Air slash**: the graph plays its clip cropped (read now from
the clip generators: `BehaviorSequencer.Crops`, `SlashSpec.FlightClipEnd` / `ImpactClipStart` 0.4667 s): the dive plays the wind-up fitted to the dive, the landing the
overhead strike (`SmNet.SlashPose`; the client had held frame 0 and restarted at 0, showing the wind-up as a backwards swing); `--slash-trace`. **Locks**: the jump-pack
landings' move / jump locks (`JumpPack.MoveLock` / `JumpLock`) and the dash attack's hit, wall stop and run-out exits (`Rush.Begin`) × `SimConfig.LockTime` (0.7, an estimate).
Review fixes: a buffered Jump held past the decision becomes `BufferedAction.JumpLong` (own record value 7) and takes off committed to the small jump (`JumpPack.LongJump`); a charge whose move ends in its wall stop still tests that tick's contacts and the hit wins (`Rush.WallStoppedFrom`, `World.StopRushes`). `NavJumpFlight.SimulationVersion` 8. Spec "The three jumps, the dash attack's walls and the air slash's swing"; `JumpTierTests`, `DashAttackWallTests`, `SlashPoseTests`.

Loyalist barks are done (added content; branch `extras/loyalist-barks`; no protocol bump, `ContentExtras.WireVersion` 2; the owner's request of 2026-10-29): flag
`ContentExtras.LoyalistBarks` (4, `--content loyalist-barks`, in `extended`). Loyalist Space Marines only bark the owner's own ElevenLabs voice pack (32 OGG takes, 8 phrases;
`client/Extras/LoyalistBarks/assets/`, `.gdignore`d, embedded in the client only with added content; provenance in THIRD_PARTY_NOTICES.md) at the charge, kills, melee starts,
bolt-fire starts and the own player's first damage after a quiet spell (chances, 8 s per speaker, 20 s per phrase, no take twice in a row, SM1 speech first; all ours), and **T**
shouts FOR THE EMPEROR at any time (2 s cooldown, input bit 13 held 4 ticks and never read by the simulation; relayed in E3's extras envelope, now on this branch: message kind 16,
section tag 2 with the player's shout sequence, so every client hears the same take). A server without the flag drops bit 13; the bit alone is not play for the idle kick.
Code: `SmContent/Extras/LoyalistBarks`, `SmNet/Extras/LoyalistBarks`, `SmServer/Extras/LoyalistBarks`, `client/Extras/LoyalistBarks`; hooks `GameServer.ExtrasSend`,
`NetworkView.ExtrasInput` / `ExtrasFrame` / `ExtrasKill`. Aids `--no-loyalist-barks`, `--log-sound loyalist`, `--loyalist-gain`, `--bot-shout <s>`. Needs the owner's ear (level,
-2 dB on the sound bus). docs/added-content.md section 7; spec "Added content: loyalist barks". Owner's decision (2026-10-29): the automatic barks are off by default (`--loyalist-auto-barks` turns them on); the release plays only the T key's FOR THE EMPEROR.

Combat stims are done (branch `perks/inert-2`; protocol 40: scoreboard rows end with each player's equipment item; the config hash changes; install, extended: `97325DE6449F96B2`): `sm_mp_eq_stimpack` is the classes' third
equipment choice. `SmContent.StimTuning` reads the count by upgrade, `min_time_between_activations` (6 s, as the cooldown), `actn_stims_activated`'s modifiers and
`mp_reaction_immunity` by upgrade and the player graph's stim sequencer (effect at 0.3 s, free at 0.9 s) into a `ThrownSpec` with a `StimSpec` (`ThrownSpec.NoProjectile`);
the use runs as a throw that launches nothing and sets `PlayerState.StimLeft` (5 s; Master-Crafted 7 s): health-only damage taken ranged x0.75 / melee x0.5 / explosive x0.75
(0.6 / 0.35 / 0.6), melee dealt x1.35 (x2), no hit reactions (`SmSim/Stims.cs`). Not on the wire: the client derives it from the record's item cooldown, past it an agreeing prediction (`Stims.Carry`); others' uses are told from throws by the scoreboard's equipment byte (`GameClient.EquipmentOf`). Client: the stim clip over the
upper body, the action's sound and effects, HUD "STIM n.n" (`client/NetworkView.Stims.cs`); test aid `--perks a,b`. Perks: 43 active, 0 partial, 5 inert (Rapid Deployment, Tactical
Readout, Weapon Versatility, Teleport Homer, Signum Link: no value or consumer in the data). Spec "Combat stims"; `StimTests`, `StimTuningTests`.

No smoke is done (branch `fx/no-smoke`; client only, no protocol change; the owner's request of 2026-10-29, a deliberate change): smoke, dust, mist and haze sprites are not drawn in any
effect (impacts keep flash, sparks, debris and decal; explosions their fireball and flash; god rays, lights, blood, tracer streaks, shells and the physics chunks stay). The rule is engine-free and
data-driven (`SmContent.SmokeRules`: blend, texture and element names, the puff shape; `SmokeRulesTests`), applied at compile time (`CNode.Smoke` / `SmokeOnly`, skipped in `FxPlayer.DrawNode`);
`--smoke on` restores the data's look; `smtool fx-smoke auto` lists the 149 of 228 MP effects touched. The "lines on the floor through smoke" were no draw-order fault: a camera-facing card is a plane
through its centre, so the floor in front of it drew over it; alpha-blended camera billboards are now depth-tested at a plane pulled toward the camera (half the half size, at most 1 m; additive ones not;
the near fade keeps the card's own depth; `FxBatch.Pull*`, `--no-fx-pull`). `SmokeInstallPinTests` pins the fight cues' kept sprites. Same 8-player demo: effects phase 3.04 → 1.95 ms a frame, sprites 1158 → 608 (alpha 673 → 151). `--frame-stats` now prints the renderer's mean GPU time. Spec "No smoke" rows.

Performance pass 1 is done (branch `perf/pass-1`; build, client and release launcher; no protocol change; the owner's report "the performance in this build is abysmal"):
the exported game had compiled SmSim, SmNet, SmContent and SmFormats **without JIT optimisation** (Godot's `ExportRelease` configuration is not one Microsoft.NET.Sdk
optimises; `Directory.Build.props` now optimises every project of an export; collision rays 6 to 8.6x faster, the follow camera 1.8 → 0.2 ms a frame; client.cmd still builds
Debug); lean bots (merged with perf/lean-bots on integrate/2026-10-29: one mode, no flag: every headless `--bot` without `--bot-full-load` or a presentation aid, so the menu's
hosted bots, `bots.cmd` and `Launcher.ps1 -StartBots` bots; `SmNet.LeanBot`, `client/BotLean.cs`) skip every presentation-only load and also run at most 30 frames a second
(`--bot-fps n`), 30 physics steps and no effects: 3 hosted bots on 127.0.0.1 measured 254 to 278 MB working set and 0.03 to 0.06 of a core each (the same bot uncapped 0.14); `HitVolumes.Bounds` no longer recomputes a hull's margin per corner (20 to 45 ms crosshair frames while jump-pack players flew); the drops' models load
during the map load (a 100 ms frame at the first drop); human clients use the GC's SustainedLowLatency mode (`--gc-default` off). Replay of an 8-player demo with 7 bots on
the same PC: mean 12.0 → 6.4 ms, frames over 33 ms 24 → 1. Test aid `--perf-probe` (renderer CPU/GPU, draw calls, memory, GC per 5 s). Report and recommendations
(server-side bots, draw calls, allocations, vsync): spec "Performance pass 1".

Playtester launcher is done (branch `launcher/updater`; tools and release only): `src/SmLauncher.Core` (logic, tested) and `src/SmLauncher` (WinForms, `SM Reimpl Launcher.exe`, self-contained single file), shipped in every playtest zip as `0 - SM Reimpl Launcher.exe`. It installs and updates the newest published build of Yeezima/sm-reimpl-releases into `%LOCALAPPDATA%\SM Reimpl` (`versions\<tag>\`, `current.json`, the last two kept for rollback): HTTPS to GitHub hosts only, Range-resumed downloads with retries, SHA-256 against the API digest and the description hash before anything is unpacked, zip-slip-safe staging checked against `notes\CONTENTS-SHA256.txt`, atomic switch, recovery of every interrupted phase (`update.json`), disk-space checks, adopt of an unpacked folder, Check files, Roll back, Clear caches (data root `cache` only), and a self-update through a verified helper that puts the old launcher back on any failure. Player data in `%APPDATA%\sm-reimpl` is never touched. Headless aids: `--selftest`, `--status`, `--check`, `--install-latest`, `--verify`, `--rollback`, `--adopt`; the release smoke runs `--selftest` and the window off-screen. `docs/releasing.md` "The launcher".

Customizer pose and the Ork in the menus is done (branch `ui/customizer-ork`; client and profile only, no protocol change; the owner's requests of 2026-10-29, Ork O9): every marine and Chaos class in the class creator, the appearance editor and the in-match picker stands in the bolt pistol's relaxed idle (`RigClipTable.PistolRelaxedIdle` = `SM_NAV_IdleL_CS_01`, the clip `SM_Pistol_NAV` "Idle Relaxed" binds; `PreviewPoseInstallPinTests`) with the pistol in the hand and the primary stowed (`PreviewStage.HoldsPistol`; the jump-pack classes keep the melee weapon in hand), turned back 35 degrees for the front-quarter view (ours); the turntable is gone (only drag, Left / Right and the right stick turn the figure). In Extended the Ork Boy is in the menus: the creator's faction row adds Orks and a fourth class tab "Ork Boy" (`client/Customizer/Model/OrkLoadout.cs`: fixed shoota, slugga, choppa, stikkbombs and the Tactical's perks), the Join screen an Ork DEFAULTS chip, custom Ork loadouts (role `ork`, kept by `ProfileStore` in every profile), a fifth step LOOK (automatic or one of five boy models, `orkLook` in `customization.json`) instead of the armour and colour editors, the preview on his own rig in his own idle (`PreviewAssets.TryBuildOrk` / `OrkWeapons`), and a Join, Deploy or close of the in-match picker sends his class and look (`JoinPlan.OrkLook`, `LoadoutKey.OrkLook`). Faithful offers none of it: a saved Ork loadout stays, shows "unavailable here", never opens in the editor and joins as the side's default class. The customizer reads the Ork's class data too (63 to 194 ms more on a warm run, on its worker). Checks: `--ui-selftest` (pose, held pistol, no spin, drag, the Ork in Extended and none in Faithful, fit at 7 sizes with the Ork's LOOK step), `CustomizerModelTests` Ork rows. Spec "The customizer's preview pose and the Ork Boy in the menus".

The impact pass is done (branch `fx/impact-pass`; client and content only, no simulation or protocol change; the owner's requests of 2026-10-29): **stomps and slams** (every melee step with a blast, `MeleeStep.Stomp`) play the wargear's `displace_*` effect (`SmContent.MeleeEffects`, entry matched by radius and damage) on the ground under its marker plus ours sized to the blast radius (`client/StompFx.cs`: a ground shockwave of the data's `rings_outer` sprite and a flash light, the crack decal at 0.85 of the diameter, the pound's thin ring, 2 to 3 chunks per metre; no dust, smoke or shake), once per attack keyed on its start tick (`client/ImpactRules`, `MeleeHitGate`: a reconcile rewind never fires twice, a player first seen mid-attack never fires); the **power sword** (both races) plays the data's `powersword_idle` while in hand, a blue blade glow (`client/PowerBlade.cs`, an additive overlay on the blade band measured on the model) and on a hit `powersword_dmg` plus our flare and light at the contact point (`NetworkView.MeleeFx.cs`); **jump-pack thrust**: trails along the last third of their length in metres of the nozzle's path, the `rocket` flame a third as long, every thrust at the nozzle exit (`SmContent.NozzleExit`: Chaos 0.28 m out of its inside-centre markers); **lighting**: the `remaster` look is the default (`--look current` = the old default), its SSR only at medium and high (never low or off), Manufactorum's god rays removed (`--god-rays`). Test aids `--no-stomp-fx`, `--no-sword-fx`, `--old-pack-trail`, `--stomp-test`; tools `smtool fx-dump`, `weapon-parts --profile/--exhaust`. Spec "Stomp impacts, the power sword and the jump-pack thrust (fx/impact-pass)"; `docs/changes-from-original.md` rows.

HUD fidelity is done (branch `ui/hud-fidelity`; client and readers only, no protocol change; the owner's requests of 2026-10-29): the reticles, hit marker, kill feed, reward lines,
score panel, weapon box, Seize Ground markers and pick-up prompt are drawn from the install's HUD movies (`SmContent.HudMovies`, `ReticleRules`, `WeaponBoxRules`; `GfxMovie` reads
text fields, fonts and defining tags, `GfxRaster.DrawMasked` applies clip layers and images placed as characters; client `Hud/HudMovieArt.cs`, `Hud.Movie.cs`, `Hud.Fidelity.cs`): every
family's own reticle with its spread frame, the active (orange) one over an enemy, none for the stalker bolter and lascannon outside the scope (owner); placements measured on the
reference footage where the original projects its panels; the weapon box's heat bar drains as the weapon heats. The armour bar stays M44's (the movie renders it gold, the footage
cyan). Any failure to read the movies falls back to the M44 drawing (`--hud-movies-fail` test aid); `--old-hud` restores M44. **Camera lag (temporary, owner: likely to be
removed):** the follow camera trails the view by a critically damped 50 ms (`SmNet.CameraLag`, tested), reset after death, respawns and jumps; the aim's ray starts where an
unlagged rig puts the eye and runs along the view (probe: 0.000 degrees and 0.000 m against the unlagged aim), the crosshair is drawn where it lands; `--camera-lag ms` (0 off); all in
`client/NetworkView.CameraLag.cs` plus one-line hooks. The creator's class tabs shrink their font to show the whole label (`UiKit.ShrinkToFit`; the self-test checks it).
`smtool gfx-dump` prints and renders the movies. Spec "HUD from the movies (ui/hud-fidelity)"; docs/discovery.md "HUD movies read in full".
