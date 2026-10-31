using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using SmSim;

namespace SmNet;

public enum RejectReason : byte { ProtocolMismatch = 1, ContentMismatch = 2, ConfigMismatch = 3, ServerFull = 4, MapMismatch = 5 }

public abstract record Message;
/// <summary>
/// Client → server, resent until answered. Carries everything needed to refuse an incompatible client.
/// <paramref name="SentMs"/> is the client's clock when sent (echoed back for the round trip); <paramref name="Cookie"/> is
/// 0 at first and then the value from the server's <see cref="Challenge"/>, which proves the client receives at its address.
/// <paramref name="MapHash"/> is the collision hash of the map the Challenge named, loaded from the client's install (0 for none).
/// <paramref name="RequestedClass"/> is the class to play (index into <see cref="SimConfig.Classes"/>), or
/// <see cref="Protocol.ServerDefaultClass"/> for the server's default; the server replaces an invalid one with its default.
/// <paramref name="RequestedPrimary"/>, <paramref name="RequestedPistol"/> and <paramref name="RequestedMelee"/> index the
/// class's choice lists (<see cref="Loadouts.Choices"/>, <see cref="ClassLoadout.MeleeChoices"/>); <see cref="Protocol.DefaultChoice"/>
/// or an index out of range gets the class default. <paramref name="RequestedEquipment"/> (protocol 8) indexes the class's
/// <see cref="ClassLoadout.EquipmentChoices"/> the same way. <paramref name="RequestedSide"/> (protocol 10) asks for a side
/// (<see cref="Team"/>: 0 any, 1 Space Marines, 2 Chaos; anything else reads as any): the server moves the player to that
/// side's class of the same role, unless the side is fuller than the other (<see cref="World.AssignClass"/>); a class without a side (the
/// Ork Boy, protocol 28) keeps its class and joins that side under the same rule (<see cref="World.AssignSide"/>; no request: the smaller side).
/// Protocol 22 (custom classes): <paramref name="RequestedPerk1"/> and <paramref name="RequestedPerk2"/> index the class's
/// <see cref="ClassLoadout.PerkChoices"/> (<see cref="Protocol.DefaultChoice"/>: perk 1 the class default perk, perk 2 none), and the
/// player's two appearances (<paramref name="SpaceMarineAppearance"/>, <paramref name="ChaosAppearance"/>; the server uses the one of
/// the side it puts the player on). 175 bytes. Protocol 28 (Ork O5): <paramref name="OrkLook"/>, the look an Ork wears (0..4, <see cref="Races.OrkLooks"/>;
/// <see cref="Races.OrkLookAutomatic"/> or any other value: automatic, by player id), used while the player plays the Ork Boy; 176 bytes.
/// </summary>
public sealed record Hello(ushort ProtocolVersion, ulong ContentFingerprint, ulong ConfigHash, uint Nonce, uint SentMs = 0, ulong Cookie = 0, ulong MapHash = 0,
    byte RequestedClass = Protocol.ServerDefaultClass, byte RequestedPrimary = Protocol.DefaultChoice, byte RequestedPistol = Protocol.DefaultChoice,
    byte RequestedMelee = Protocol.DefaultChoice, byte RequestedEquipment = Protocol.DefaultChoice, byte RequestedSide = 0,
    byte RequestedPerk1 = Protocol.DefaultChoice, byte RequestedPerk2 = Protocol.DefaultChoice,
    AppearanceCode SpaceMarineAppearance = default, AppearanceCode ChaosAppearance = default, byte OrkLook = Races.OrkLookAutomatic, byte Flags = 0,
    byte RequestedSecond = Protocol.DefaultChoice) : Message
{
    /// <summary>Protocol 37 (test aid): the client is a test bot (<c>client.cmd --bot</c>); the server's <c>--bot-damage-scale</c> applies to it. Self-declared:
    /// it can only lower the damage its own sender deals.</summary>
    public const byte BotFlag = 1;
    /// <summary>Whether <see cref="Flags"/> marks a test bot.</summary>
    public bool Bot => (Flags & BotFlag) != 0;

    /// <summary>Protocol 46 (ours; observer tooling, docs/changes-from-original.md): the client joins as a spectator (<c>client.cmd --observe</c>): the server gives it no player,
    /// team, score or body, sends it the snapshots of everyone and lists it as a spectator. Self-declared and harmless: a spectator can neither act nor be acted on.</summary>
    public const byte SpectatorFlag = 2;
    /// <summary>Whether <see cref="Flags"/> marks a spectator (protocol 46).</summary>
    public bool Spectator => (Flags & SpectatorFlag) != 0;

    /// <summary>The requested class and choices as a <see cref="LoadoutChoice"/> (255 bytes as -1, the default).</summary>
    public LoadoutChoice Loadout => new(Protocol.FromChoice(RequestedClass), Protocol.FromChoice(RequestedPrimary), Protocol.FromChoice(RequestedPistol),
        Protocol.FromChoice(RequestedMelee), Protocol.FromChoice(RequestedEquipment), Protocol.FromChoice(RequestedPerk1), Protocol.FromChoice(RequestedPerk2), Protocol.FromChoice(RequestedSecond));

    /// <summary>The appearance for a side (<see cref="Team.Chaos"/>: the Chaos one; anything else the Space Marine one).</summary>
    public AppearanceCode AppearanceFor(Team side) => side == Team.Chaos ? ChaosAppearance : SpaceMarineAppearance;

    /// <summary>The appearance for a race (protocol 28): an Ork's is built from <see cref="OrkLook"/> (<see cref="Races.OrkCode"/>).</summary>
    public AppearanceCode AppearanceFor(Race race) => Protocol.CodeFor(race, SpaceMarineAppearance, ChaosAppearance, OrkLook);
}

/// <summary>
/// Client to server (protocol 22, 138 bytes; protocol 28: 139): change the loadout and appearances from the next respawn. Resent (every 250 ms)
/// with the same <paramref name="Sequence"/> until the matching <see cref="SetupAck"/>; the server treats a repeated sequence as the same
/// request (idempotent). <paramref name="Loadout"/>: indices as in the Hello (-1 default; class -1: keep the current class).
/// <paramref name="OrkLook"/> (protocol 28): as in the Hello. A class change to or from the Ork Boy changes the race at the next respawn.
/// </summary>
public sealed record Setup(uint Session, ushort Sequence, LoadoutChoice Loadout, AppearanceCode SpaceMarineAppearance, AppearanceCode ChaosAppearance,
    byte OrkLook = Races.OrkLookAutomatic) : Message
{
    public AppearanceCode AppearanceFor(Team side) => side == Team.Chaos ? ChaosAppearance : SpaceMarineAppearance;
    /// <summary>The appearance for a race (protocol 28; as <see cref="Hello.AppearanceFor(Race)"/>).</summary>
    public AppearanceCode AppearanceFor(Race race) => Protocol.CodeFor(race, SpaceMarineAppearance, ChaosAppearance, OrkLook);
}

/// <summary>What a <see cref="SetupAck"/> says about the request (protocol 22).</summary>
[Flags]
public enum SetupFlags : byte
{
    None = 0,
    /// <summary>Accepted; applies at the next respawn.</summary>
    Pending = 1,
    /// <summary>The server is pulling the custom emblem from the client (the fallback badge shows until it is verified).</summary>
    EmblemUploading = 2,
    /// <summary>The emblem failed verification or the upload limits; the fallback badge shows.</summary>
    EmblemRejected = 4,
    /// <summary>At least one field was invalid and was set to its default (the resolved choices say which).</summary>
    Defaulted = 8,
    All = Pending | EmblemUploading | EmblemRejected | Defaulted,
}

/// <summary>
/// Server to client (protocol 22, 13 bytes): the answer to the <see cref="Setup"/> with <paramref name="Sequence"/>: the choices as
/// resolved (-1 default; the class mapped to the player's side), flags, and the appearance version the player will have
/// (<see cref="ScoreEntry.AppearanceVersion"/>).
/// </summary>
public sealed record SetupAck(ushort Sequence, LoadoutChoice Loadout, SetupFlags Flags, ushort AppearanceVersion) : Message;

/// <summary>
/// Client to server (protocol 22, 6 to 38 bytes): send me these players' current appearances (at most
/// <see cref="Protocol.MaxAppearanceQuery"/> ids, 1..65535, each once).
/// </summary>
public sealed record AppearanceQuery(uint Session, int[] PlayerIds) : Message;

/// <summary>
/// Server to client (protocol 22, 68 bytes): <paramref name="PlayerId"/>'s appearance <paramref name="Version"/> (a server-wide counter;
/// 0 is the default look) for its race (<see cref="SmSim.Race"/>: 1 Space Marines, 2 Chaos; protocol 28: 3 Orks, whose code names only a look,
/// <see cref="Races.OrkCode"/>). Any other race byte is refused.
/// </summary>
public sealed record Appearance(int PlayerId, ushort Version, Race Race, AppearanceCode Code) : Message;

/// <summary>
/// Either direction (protocol 22, 22 bytes): send me chunk <paramref name="Chunk"/> (0..15) of the blob with this hash
/// (<see cref="BlobTransfer"/>). <paramref name="Session"/> authenticates a client's request; the server sends 0.
/// </summary>
public sealed record BlobRequest(uint Session, EmblemHash Hash, int Chunk) : Message;

/// <summary>
/// Either direction (protocol 22, at most 1045 bytes): chunk <paramref name="Index"/> of <paramref name="Count"/> of the blob with this
/// hash, 1..<see cref="BlobTransfer.ChunkBytes"/> bytes of <paramref name="Data"/>. The receiver verifies the whole blob's hash.
/// </summary>
public sealed record BlobChunk(EmblemHash Hash, int Index, int Count, byte[] Data) : Message;
/// <summary>
/// Server → client: echo this cookie in the next Hello, and load <paramref name="Map"/> ("" for the flat placeholder ground).
/// The server keeps no state until it comes back. Never larger than a Hello (map names are at most <see cref="Protocol.MaxMapName"/> bytes).
/// </summary>
public sealed record Challenge(uint Nonce, ulong Cookie, uint EchoSentMs, string Map = "") : Message;
/// <summary>
/// The map a server runs: its name (sent to clients), collision, spawn points, and <paramref name="Hash"/>, which identifies
/// the map's source data (both sides must agree; see <see cref="LoadedMap"/>).
/// </summary>
/// Protocol 11: <paramref name="ControlPoints"/> and <paramref name="StartAreas"/> for Seize Ground (null: none).
/// M22: <paramref name="Blockers"/>, the spawn rooms' blockers (they set the spawn facing; null: <see cref="CollisionWorld.Blockers"/>).
/// Movement takes them from <paramref name="Collision"/> (solid for the other side's players), which the client loads from its
/// own install like the rest of the level; <paramref name="Hash"/> covers the layers they come from.
public sealed record GameMap(string Name, CollisionWorld Collision, IReadOnlyList<SpawnPoint> Spawns, ulong Hash,
    IReadOnlyList<ControlPoint>? ControlPoints = null, IReadOnlyList<StartArea>? StartAreas = null, IReadOnlyList<SpawnBlocker>? Blockers = null);
/// <summary>What a client's map loader returns: the collision to predict with and the hash the server checks.</summary>
public sealed record LoadedMap(CollisionWorld Collision, ulong Hash);
/// <summary>
/// Server → client. <paramref name="EchoSentMs"/> echoes the Hello's send time, so the client measures the round trip of
/// the Hello actually answered. <paramref name="Session"/> must accompany every input batch.
/// </summary>
public sealed record Welcome(int PlayerId, uint ServerTick, int TickRate, uint Nonce, uint EchoSentMs = 0, uint Session = 0) : Message;
public sealed record Reject(RejectReason Reason, string Detail, uint Nonce) : Message;
/// <summary>Client → server, every tick: the newest inputs, newest first. Older ones repeat so a lost packet costs nothing.</summary>
/// <summary>Inputs plus <paramref name="SnapshotTick"/>, the newest snapshot the client has received (bounds lag compensation).</summary>
public sealed record InputBatch(PlayerInput[] Inputs, uint Session = 0, uint SnapshotTick = 0) : Message;
/// <summary>Client → server when quitting (best effort, sent a few times): remove my player now.</summary>
public sealed record Leave(uint Session) : Message;
/// <summary>
/// Server → client: the world after <paramref name="ServerTick"/>. <paramref name="LastInputTick"/> is the newest input
/// tick from this client the server has applied; <paramref name="InputLead"/> is how many ticks ahead of the server
/// that client's newest received input was (the client steers its lead time with it), or <see cref="Protocol.NoLead"/>
/// before any input has arrived. Snapshots are per recipient: the player whose id is <paramref name="Self"/> (the
/// recipient's own) travels in full; every other player travels slim (<see cref="Protocol.Slim"/>: what is needed to draw
/// and interpolate it, not its health, shield, ammunition or timers). 0: every player slim.
/// <paramref name="Projectiles"/> (protocol 7): projectiles in the world and recent detonations, at most
/// <see cref="Protocol.MaxSnapshotProjectiles"/> (the server picks which); null reads as none.
/// <paramref name="Match"/> (protocol 10): the match state, time and team scores (null is written as no match).
/// <paramref name="Kills"/> (protocol 10): recent deaths for the kill feed, at most <see cref="Protocol.MaxSnapshotKills"/>
/// (a death repeats in the snapshots of <see cref="World.KillMemoryTicks"/> ticks; drop repeats by <see cref="KillView.Sequence"/>).
/// </summary>
/// <paramref name="Points"/> (protocol 11): every control point's state (Seize Ground), at most <see cref="World.MaxControlPoints"/>, in the
/// order of the scoreboard's layout; null reads as none.
/// <paramref name="Hits"/> (protocol 12): the recipient's own hits on others (<see cref="HitView"/>; null is written as all zeros).
/// <paramref name="Shots"/> (protocol 16): other players' hit-scan and cone shots since the previous snapshot, for tracers and
/// impacts (<see cref="ShotView"/>), in priority order: the writer keeps the first ones that fit (at most
/// <see cref="Protocol.MaxSnapshotShots"/>, and only while the snapshot stays within <see cref="Protocol.SnapshotBudget"/>
/// bytes; at least <see cref="Protocol.MinSnapshotShots"/> always fit), and leaves out any whose shooter has no record in
/// <paramref name="Players"/>. Null reads as none.
public sealed record Snapshot(uint ServerTick, uint LastInputTick, int InputLead, PlayerState[] Players, int Self = 0, ProjectileView[]? Projectiles = null,
    MatchView? Match = null, KillView[]? Kills = null, ControlPointView[]? Points = null, HitView? Hits = null, ShotView[]? Shots = null) : Message;

/// <summary>
/// A shot for effects (protocol 16, 9 bytes): who fired (<paramref name="Shooter"/>, a player id that has a record in the same
/// snapshot), which weapon (index into <see cref="SimConfig.Weapons"/>) and stage (<see cref="Shots"/>: 0 basic, k charge
/// stage k − 1; 0..7), what it hit (<see cref="ShotHit"/>), the surface material id of a world hit (0..14; -1 unknown; always -1
/// for other hits), where it ended (<paramref name="End"/>: the hit point, or the point at the weapon's range when nothing was
/// hit) and how many ticks before the snapshot's tick it was fired (<paramref name="Age"/>, 0..3).
/// On the wire: byte 0 = the shooter's index among the snapshot's player records (bits 0..3) | hit (bits 4..5) | age (bits 6..7);
/// byte 1 = weapon; byte 2 = stage (bits 0..2) | surface (bits 3..6; 15 = unknown) | 0; then the end point relative to the
/// shooter's record position as three signed 16-bit integers in 1/32 m (±1024 m; a longer offset is shortened along its
/// direction). Use <see cref="Protocol.Quantize(ShotView, Vector3)"/> to compare with a received one.
/// <paramref name="Armoured"/> (protocol 22, hit feedback; bit 7 of byte 2): a player hit (Body, Head) struck a raised armour shield
/// (<see cref="ShotEvent.Armoured"/>); always false for other hits.
/// </summary>
public readonly record struct ShotView(int Shooter, int Weapon, int Stage, ShotHit Hit, int Surface, Vector3 End, int Age = 0, bool Armoured = false);

/// <summary>
/// What the recipient's last hit was (protocol 12): head zone, it killed, a melee hit, a blast (splash) hit; protocol 22: the victim's
/// armour shield was up when it landed (<see cref="HitEvent.Armoured"/>).
/// </summary>
[Flags]
public enum HitFlags : byte { None = 0, Head = 1, Killed = 2, Melee = 4, Splash = 8, Armoured = 16, All = Head | Killed | Melee | Splash | Armoured }

/// <summary>
/// The recipient's hits on other players (protocol 12, 12 bytes), as running totals since it joined so a lost snapshot loses
/// nothing: hits (mod 65536), shield and health damage dealt in tenths (mod 65536), kills (mod 256); then the latest hit:
/// victim id (0..65535; 0 none), <see cref="HitFlags"/>, and the victim's health and shield after that tick as fractions of its maximum
/// health and shield (<see cref="Loadouts.Body"/>: the config's values, or as its perks change them since M43) in 1/255 steps. Self damage is not counted.
/// The client turns the change between snapshots into a <see cref="HitReport"/> (<see cref="GameClient.TakeHits"/>).
/// </summary>
public readonly record struct HitView(int Hits, int ShieldTenths, int HealthTenths, int Kills, int LastVictim, HitFlags LastFlags, float VictimHealth, float VictimShield);

/// <summary>
/// The match as snapshots carry it (protocol 10, 9 bytes): state, the winner of an ended match (<see cref="Team.None"/>: a
/// draw), <paramref name="TicksLeft"/> (playing: until the time limit, <see cref="uint.MaxValue"/> for none; ended: until the
/// restart) and the two team scores (0..65535). Protocol 11: the mode in bits 4..5 of the state byte.
/// </summary>
public readonly record struct MatchView(MatchState State, Team Winner, uint TicksLeft, int MarineScore, int ChaosScore, GameMode Mode = GameMode.Annihilation);

/// <summary>
/// A control point as snapshots carry it (protocol 11, 2 bytes): owner (None: neutral), the side whose progress counts,
/// whether both sides stand on it, and the progress as a fraction of its phase (owned: hold over the neutralise time;
/// neutral: capture over the capture time) in 1/255 steps.
/// </summary>
public readonly record struct ControlPointView(Team Owner, Team Side, float Progress, bool Contested);

/// <summary>A control point's layout (protocol 11, in the Scoreboard): marker name, centre, radius and half height (metres).</summary>
public readonly record struct ControlPointInfo(string Name, Vector3 Center, float Radius, float HalfHeight);

/// <summary>
/// Seize Ground rules and point layout (protocol 11, in the Scoreboard): capture and neutralise times (s), score per owned
/// point per interval, the interval (s), and the points in snapshot order.
/// </summary>
public sealed record SeizeGroundInfo(float CaptureTime, float NeutraliseTime, int PointScore, float ScoreInterval, ControlPointInfo[] Points);

/// <summary>
/// A kill-feed entry (protocol 10, 8 bytes): <paramref name="Sequence"/> modulo 65536, killer and victim player ids (0..65535;
/// killer 0: none, killer = victim: suicide), the cause and its index (<see cref="KillCause"/>; -1 none).
/// <paramref name="Direction"/> (protocol 15, one more byte): the killing hit's push for the victim's ragdoll
/// (<see cref="KillEvent.Direction"/>; unit, world space, zero: none), as yaw in 256 steps (the simulation's yaw: forward
/// (−sin, cos)) and pitch in 15 steps either side of level (6°), carried in the cause byte's top 5 bits (0: no direction).
/// Use <see cref="Protocol.Quantize(KillView)"/> to compare with a received entry.
/// </summary>
public readonly record struct KillView(int Sequence, int Killer, int Victim, KillCause Cause, int Index, Vector3 Direction = default)
{
    public bool HasDirection => Direction != Vector3.Zero;
}

/// <summary>
/// A scoreboard row: player, side, class, kills and deaths (0..65535); protocol 22: the player's appearance version (0 the default
/// look; a client queries versions it does not have with an <see cref="AppearanceQuery"/>); protocol 40 (perks/inert-2): the player's
/// equipment item (index into <see cref="SimConfig.Thrown"/>, as <see cref="Loadouts.EquipmentIndex(SimConfig, PlayerState)"/> resolves it;
/// -1 none or unknown), so clients know whether another player's use is a throw or combat stims (slim snapshot records carry no equipment).
/// Protocol 41 (names/bot-lore): the player's display name (<see cref="PlayerNames"/>; "" none: shown as "P&lt;id&gt;"), at most
/// <see cref="PlayerNames.MaxLength"/> Latin-1 characters, sanitised on writing and reading (<see cref="PlayerNames.Sanitise"/>). The server names test bots today.
/// </summary>
public readonly record struct ScoreEntry(int PlayerId, Team Team, int Class, int Kills, int Deaths, ushort AppearanceVersion = 0, int Equipment = -1, string Name = "");

/// <summary>A spectator listed in the scoreboard (protocol 46): its id and display name ("" none: shown as "Spectator &lt;id&gt;"); spectators have no team, class or score.</summary>
public readonly record struct SpectatorEntry(int PlayerId, string Name = "");

/// <summary>
/// Server → client about once a second (protocol 10): the match rules (<paramref name="Teams"/> false: no team match; time
/// limit in seconds, 0 none) and every player's score. Unreliable like snapshots; the next one replaces it.
/// <paramref name="Seize"/> (protocol 11): Seize Ground's rules and point layout; null in Annihilation and free-for-all.
/// </summary>
public sealed record Scoreboard(uint ServerTick, bool Teams, int ScoreLimit, float TimeLimit, bool FriendlyFire, ScoreEntry[] Players, SeizeGroundInfo? Seize = null,
    SpectatorEntry[]? Spectators = null) : Message
{
    public GameMode Mode => Seize is null ? GameMode.Annihilation : GameMode.SeizeGround;
}

/// <summary>What a snapshot says about a projectile.</summary>
[Flags]
public enum ProjectileFlags : byte
{
    None = 0,
    /// <summary>Resting where it struck the level.</summary>
    Stuck = 1,
    /// <summary>Stuck to a player.</summary>
    Attached = 2,
    /// <summary>It detonated at <see cref="ProjectileView.Position"/> in the last few ticks (<see cref="World.DetonationMemoryTicks"/>); draw the blast once per id.</summary>
    Detonated = 4,
    /// <summary>A thrown item (protocol 8): <see cref="ProjectileView.Weapon"/> indexes <see cref="SimConfig.Thrown"/> and the stage is 0.</summary>
    Thrown = 16,
    All = Stuck | Attached | Detonated | Thrown,
}

/// <summary>
/// A projectile as snapshots carry it: <paramref name="Id"/> (the world's id modulo 65536), the weapon (index into
/// <see cref="SimConfig.Weapons"/>, or into <see cref="SimConfig.Thrown"/> with <see cref="ProjectileFlags.Thrown"/>) and shot
/// (<see cref="Shots"/>: 0 basic, k charge stage k − 1) that launched it, and its position quantized to 1/64 m
/// (<see cref="Protocol.Quantize"/>). 14 bytes on the wire.
/// </summary>
public readonly record struct ProjectileView(int Id, int Weapon, int Stage, Vector3 Position, ProjectileFlags Flags)
{
    /// <summary>A thrown item's record.</summary>
    public bool Thrown => (Flags & ProjectileFlags.Thrown) != 0;
}

/// <summary>Binary wire format: one type byte, then fixed little-endian fields. Malformed packets are rejected.</summary>
public static partial class Protocol
{
    // 2: combat fields, pitch, view tick, snapshot ack; 3: map name and map hash; 4: melee state;
    // 5: requested class, per-recipient snapshots (own player full, others slim), class, slot, second weapon, switch state;
    // 6: requested loadout, weapon heat, chosen weapons and melee (in-hand weapon and melee choice in slim records too);
    // 7: AltFire input, charge and pending-shot state, grenades out, a charging flag in slim records, projectiles;
    // 8: Grenade input, requested equipment, throw state and grenades in the own record, a throwing flag in slim records, thrown projectiles
    // 9: jump pack: Jump input used, jump state in the own record (9 bytes), jumping and pound flags in slim records
    // 10: teams and Annihilation: requested side in the Hello, the team in the class byte of every record, the match block
    //     and kill feed at the end of the snapshot, the Scoreboard message; 20 projectile records instead of 26
    // 11: Seize Ground: the mode in the match block, control point states at the end of the snapshot (count, 2 bytes each),
    //     the rules and point layout in the Scoreboard
    // 12: aim and convergence: 16-bit buttons (Aim), the shot direction offsets and the shot origin offset in inputs, an aiming flag (bit 7) in every player
    //     record, the recipient's hit totals (12 bytes) at the end of the snapshot
    // 13: evade roll: an evade byte in every player record (direction, ticks up to 63), the exact ticks and the spacing lock
    //     in the own record (2 bytes); 19 projectile records instead of 20
    // 14: weapon slot selection: SlotPistol and SlotPrimary input buttons (bits 9 and 10), their held flags (2 and 4) in the own
    //     record's grenade flag byte; diagonal evades: the evade direction's bit 2 in bit 1 of every record's slot byte; no size change
    // 15: death screen: the killing hit's direction in every kill-feed entry (a yaw byte, pitch in the cause byte's top 5 bits; 9 bytes each)
    // 16: shot events for effects (count, 9 bytes each) at the end of the snapshot, filled up to the packet budget; 16 projectile records instead of 19
    // 17 (M28): the MeleeHeavy input button (bit 11); every record's melee byte carries the
    //     heavy flag (bit 6) or a reaction (bit 7: kind and clip direction, its ticks in the melee time field); the own record
    //     4 more bytes (knockback yaw, distance, ticks; blindness ticks) and the heavy held and buffered flags (bits 6, 7 of its
    //     melee flag byte); 4 guaranteed shot records in the fullest snapshot instead of 5
    // 18 (M29): the charge (bull rush): its phase in every record's melee byte (bit 5; steps now 0..31), its ticks in the melee
    //     time field; the own record 1 more byte (the charge's spacing lock)
    // 19 (M27, hip fire): no layout change. The movement rule changed: while the trigger is held (or a charge or delayed charged shot
    //     runs) with a ranged weapon in hand the Sprint input bit is not applied, so a client and a server must agree on it (prediction)
    // 20 (M31, the owner's rules): the AltFire input bit (6) is gone (Fire charges every charge weapon: tap = basic shot, hold =
    //     charged shot) and so is its held flag in the own record; the weapon raise: "weapon up" in bit 2 of every record's slot byte,
    //     the own record 2 more bytes (ready ticks, raise ticks); the charge needs the sprint; spawn-room blockers reach above the level
    // 21 (M32, the owner's rules): the own record's ready ticks are a u16 (1 byte more: the 10 s hold is 300 ticks at 30 Hz); the
    //     charger's hyper armour (no stun in the rush's first half) changes the simulation
    // 22 (custom classes): the Hello carries two perk choices and two appearance codes (175 bytes); new messages Setup (9),
    //     SetupAck (10), AppearanceQuery (11), Appearance (12), BlobRequest (13), BlobChunk (14); a u16 appearance version in every
    //     Scoreboard row (12 bytes each); the config hash covers the perks. Snapshots unchanged (worst case 1198)
    // 23 (M43, perks): the own record's perk word (2 bytes: perk 1 and 2 in 7 bits each, a burst's rounds left in 2) so the
    //     prediction picks the server's perk variants; the perks' effects change the simulation (variants, player upgrades,
    //     bursts). Snapshot worst case 1200, exactly the budget
    // 24 (M44, the jump pack): the slot byte's bit 3 marks a jump-pack dash's landing part (PlayerState.EvadeLanded); the Assault /
    //     Raptor tap is a dash (an evade), two full jumps fit the pack, the pound stuns in its whole radius (config hash). Snapshot
    //     layout and sizes unchanged (worst case 1200)
    // 25 (match flow, ours): while the match-begins countdown runs (the match block says Warmup with ticks left above 0), the server drops the
    //     attack buttons of each input whose tick falls inside it, and the client's prediction drops the same ones by tick (a client that
    //     does not would predict shots the server never fires, or keep dropping them after play began); the client now sends its raw
    //     buttons. No layout change; the countdown rides in the existing match block (MatchFlowWire)
    // 26 (fidelity gap 1, dropped pickups): the Interact input button (bit 12); a new message PickupUpdate (15) with the pickups in the
    //     world and the recent collections and expiries, sent outside the snapshots (Protocol.Pickups.cs); the config hash covers the drops.
    //     Snapshots unchanged (worst case 1200)
    // 27 (M45, the middle jump; user's knowledge): letting go of Jump while a jump-pack jump rises cuts the rise (the simulation changes, so
    //     client and server must agree); the own record's jump byte carries the cut in bit 6 during the ascent (no size change; worst case 1200)
    // 28 (Ork O5): the Ork Boy plays (class 6 of the install's config, in the config hash; Parity balance unless both sides run --ork-data). The
    //     Hello (176 bytes) and the Setup (139) end with the Ork look byte; the Appearance message's race byte may be 3 (Orks); an appearance's race
    //     follows the class (a class change to or from the Ork applies at the next respawn). Snapshots, scoreboard, kill feed and shot records
    //     unchanged (worst case 1200)
    // 29 (fidelity gap 7, the heavy bolter brace and the plasma vent): every record's slot byte carries the brace phase in bits 4..5 (BracePhase) and the
    //     vent in bit 6; the own record's flag byte (Reload and Detonate held) carries a brace transition's ticks in bits 3..7 (low 5) and bit 0 (bit 5);
    //     braced fire, venting and the plasma cannon's overheat slow change the simulation. Snapshot layout and sizes unchanged (worst case 1200)
    // 30 (sim/jump-pack-and-falls): the walk-off drop (a body in the air outside a jump-pack flight falls straight down), a charge in the air keeps its
    //     speed until it lands, the Assault / Raptor dash attack and the jump pack's sword slash change the simulation. The melee byte's charge phase
    //     takes bits 0..2 (RushPhase 1..6: the dash attack's lunge, hit, run-out and wall stop); the slot byte's bit 7 marks a pound phase as the
    //     sword slash's (with the pounding flag). Snapshot layout and sizes unchanged (worst case 1200)
    // 31 (feel/jump-pack; the owner's requests of 2026-10-29): jump-pack air control (the stick steers the flight), the sword slash's own dive toward
    //     the aim, its landing lunge and 0.3 s recovery, a slash press buffered in the take-off (the slot byte's bit 7 in the take-off and on the lift-off tick;
    //     the reader accepts it with the jumping flag, refuses it in the own record's hang and fall), the capacity 300 (config hash). Snapshot layout and sizes
    //     unchanged (worst case 1200). (A parked branch, extras/e3-envelope, also claims 31: it will be renumbered.)
    // 32 (feel/melee-lock): every input ends with a u16 melee target (0 none; 38 bytes an input): the player the client locks its melee attack or
    //     charge onto (MeleeLock). Melee attacks follow their root motion and turn to the locked target in their alignment window, the bull rush steers
    //     to it (the simulation changes, so client and server must agree; the config hash covers each attack's reach data). Snapshots unchanged (worst case 1200)
    // 33 (feel/buffer-speed; the owner's requests of 2026-10-29): input buffering (a blocked press is
    //     kept for the first tick its action is allowed: InputBuffer) and the movement speeds × 0.9 (config hash). The own record carries the buffer: the
    //     thrown-equipment flag byte's bits 3..5 the action and 6..7 the low 2 bits of its ticks, the raise byte's bits 6..7 the high 2 (raise ticks now
    //     6 bits, at most 63). Snapshot layout and sizes unchanged (worst case 1200)
    // 34, 35: claimed by other open branches (the integrator renumbers).
    // 36 (feel/locks-sprint-stop; the owner's requests of 2026-10-29): the move, jump and evade gates of actions × SimConfig.ActionLockScale (0.7, an estimate
    //     for review), a Jump press from a melee attack's BRANCH_Evade ends it and evades, and the sprint stop (SprintStop: a slowdown when every movement key is
    //     let go after a sprint; config hash). Every record's melee byte carries the stop as 64 (the heavy flag) with step 0 and time 0, a value refused
    //     before. Snapshot layout and sizes unchanged (worst case 1200)
    // 37 (fix/hitboxes-grenades-bots, on top of 36; the owner's playtest of 2026-10-29): every input ends with a u8 view fraction (39 bytes an input: 38
    //     with the melee target, + 1): how far past its view tick toward the next the client drew the others; hit-scan shots and cones rewind targets to
    //     that point (World.SeenAt; melee, the melee lock and the charge stay on whole ticks, and a lock tick snapped to a held snapshot sends 0). Players in
    //     jump-pack flight are hit with volumes posed by the flight clip the client shows (SimConfig.FlightHitVolumes, config hash); thrown grenades bounce
    //     shorter (owner's change; config hash). The Hello ends with a flags byte (177 bytes; bit 0: a test bot, for the server's --bot-damage-scale).
    //     Snapshots unchanged (worst case 1200)
    // 38 (feel/jump-pack-2; the owner's requests of 2026-10-29): the jump pack's three jumps by the hold (tap = the dash, a medium hold = the small
    //     jump with one apex: its cut rides the M45 cut bit, a long hold = the full jump; JumpPackSpec.HoldTiers in the config hash, the M45 cut off),
    //     the dash attack's lunge stopped by walls from its first tick and up to 60 degrees off head-on (config hash); the jump-pack landings' and the dash attack's exits' locks × SimConfig.LockTime (config hash); the own record's buffered action 7
    //     (once refused) is the long Jump press (BufferedAction.JumpLong: replayed, a take-off committed to the small jump); a charge's contact on its wall-stop tick.
    //     Snapshot layout and sizes unchanged (worst case 1200)
    // 39 (feel/charge-directions; the owner's rules of 2026-10-29): the bull rush starts on F with W held (no sprint, no speed: Rush.CanStart), A or D
    //     with W turn its heading up to RushSpec.SideSteer (25 degrees, ours) unless it locks onto a target; the dash attack's lunge goes the stick's way
    //     of eight around the view (Rush.DashYaw). The config hash changes (SideSteer replaces SprintMargin). Snapshot layout and sizes unchanged (worst case 1200)
    // 40 (perks/inert-2): combat stims (config hash); each scoreboard row ends with the player's equipment item (13 bytes a row; 255 none), so a client
    //     draws and sounds another player's stim use as a stim and not as a throw. Snapshot layout and sizes unchanged (worst case 1200). Other open branches
    //     may claim 40 too: the integrator renumbers
    // 41 (names/bot-lore; the owner's request of 2026-10-29): each scoreboard row ends with the player's display name (a u8 length, 0..24, then that many
    //     Latin-1 bytes; 0: no name, shown as "P<id>"): the server names test bots from per-race pools (PlayerNames). 14 to 38 bytes a row (16 rows: at most
    //     608 bytes). Snapshots unchanged (worst case 1200)
    // 42 (feel/melee-charge-2; the owner's requests of 2026-10-29): a bull rush held in the
    //     air ends on a landing after a real fall (Rush.EndsOnLanding, RushSpec.LandEndDrop 0.5 m, ours; the dash attack held at its end on any landing): no
    //     resumed clip, normal movement from the next tick (speed kept, at most sprint speed), the landing tick's contact still counts; the knife's and
    //     chainsword's Y,Y chain (backhand, kick or shoulder → the stomp in BRANCH_Melee_Y_Combo; config hash); melee lunges × SimConfig.MeleeLungeScale (1.3,
    //     estimate) and the weapon switch × 0.8 (0.48 s, estimate; config hash); the melee lock's eased alignment and hysteresis (MeleeLockSpec.AlignEase,
    //     Hysteresis 15 degrees; config hash). Snapshot layout and sizes unchanged (worst case 1200)
    // 43 (sim/player-collision; the owner's request of 2026-10-29: "the models shouldn't get stuck inside each other"): living players' capsules block
    //     each other in the shared movement step (PlayerCollision: slide, push apart, slide off a head). Every input ends with a u8 body lag (40 bytes an
    //     input): how many ticks before its own the snapshot was that its client held when sampling it (PlayerInput.BodyTick; 0 none); server and
    //     prediction collide with the other players as that snapshot had them, moved on by their velocities (World.RecordBodies keeps the same floats).
    //     Snapshots unchanged (worst case 1200)
    // 44 (perks; the owner's decisions of 2026-10-30): Weapon Versatility's third weapon slot (Tactical only): the SlotSecond input button (bit 14), the requested second
    //     primary (one more byte in the Hello, the Setup and the SetupAck), and the own record's 14 more bytes (the third weapon's index, the real slot and flags, the
    //     third store's clip, reserve, heat and heat age; at most 15 projectile records instead of 16, so the worst case stays 1200). Rapid Deployment (3 s respawn) and
    //     Teleport Homer (allies respawn next to a living carrier) change the server's simulation only. Tactical Readout / Signum Link: a new message EnemyBars (18) with the
    //     living enemies' health and armour fractions, sent only to players whose perks show them (Protocol.Bars.cs).
    // 45 (sim/combat-pass; the owner's playtest notes of 2026-10-30): reloads run on while the weapon is not equipped (the own record's 8 more bytes: the first and
    //     second holstered weapons' reload times; at most 14 projectile records, so the worst case is 1194); W + F is always the charge (the spacing's press is lost,
    //     never a stun strike); directional melee (the stick's way of eight at the press sets the strike's facing: 3 bits in the own record's slot byte); the ledge charge's
    //     fall (client animation). Headshot values are the data's (unchanged)
    // 46 (observer/spectator; the owner's request of 2026-10-30; ours: tooling for promotional footage): the Hello's flags byte has a spectator bit (2); the server gives such a
    //     client an id and snapshots of everyone (slim records, no Self record) but no player; the Scoreboard has a flag (8) and a trailing list of the spectators (count,
    //     then id and name; at most MaxSpectators). Snapshots unchanged (worst case 1200)
    // 47 (sim/combat-2; the owner's playtest round 2, 2026-10-30): no wire change, the rules change the simulation: jump-pack air control twice as strong,
    //     W no longer ends the bull rush, light reactions (interrupt, stagger) hold nobody, the bolt pistol's trigger ends a swing at once, a melee lunge steps on the
    //     press tick, no stick or button input during the match-start countdown, a roll keeps its travel off a ledge. Snapshot layout and sizes unchanged (worst case 1194)
    // 48 (sim/no-fire-charging-all; the owner's playtest of 2026-10-31): no wire change. No class fires from the charge's start to the end of its clip (BR-02's Tactical-only
    //     rule for everyone; a fire press is dropped, never buffered), and Fire and Aim no longer end the charge (Aim spam ended it and flipped the aim camera). Worst case 1194
    // 49 (sim/charge-needs-sprint; the owner's rule of 2026-10-31): no wire change. The charge needs Sprint (Shift; the pad's L3 hold) as well as W at the F press; F without Sprint is a stun strike
    //     in the held stick's direction (W + F alone: forward), never a charge. The buffered press follows the same test (Rush.ChargeHeld).
    public const ushort Version = 49;
    /// <summary>Most spectators a server admits and a Scoreboard lists (protocol 46), so the Scoreboard stays within one packet.</summary>
    public const int MaxSpectators = 4;
    /// <summary>Bytes of a Hello (protocol 22: 49 + 2 perk choices + 2 appearance codes; protocol 28: + the Ork look).</summary>
    public const int HelloBytes = 54 + 2 * AppearanceCode.Size;
    /// <summary>Bytes of a Setup message (protocol 28: + the Ork look).</summary>
    public const int SetupBytes = 1 + 4 + 2 + ChoiceBytes + 2 * AppearanceCode.Size + 1;
    /// <summary>Bytes of a SetupAck message.</summary>
    public const int SetupAckBytes = 1 + 2 + ChoiceBytes + 1 + 2;
    /// <summary>Bytes of an Appearance message.</summary>
    public const int AppearanceBytes = 1 + 2 + 2 + 1 + AppearanceCode.Size;
    /// <summary>Bytes of a BlobRequest message.</summary>
    public const int BlobRequestBytes = 1 + 4 + EmblemHash.Bytes + 1;
    /// <summary>Bytes of a BlobChunk message's header (the data follows).</summary>
    public const int BlobChunkHeaderBytes = 1 + EmblemHash.Bytes + 1 + 1 + 2;
    /// <summary>The loadout choices on the wire: class, primary, pistol, melee, equipment, perk 1, perk 2 (one byte each, 255 default).</summary>
    public const int ChoiceBytes = 8;
    /// <summary>Most player ids in one <see cref="AppearanceQuery"/>.</summary>
    public const int MaxAppearanceQuery = 16;
    /// <summary>Bytes of a Scoreboard row without its name (protocol 22: 10 + the appearance version; protocol 40: + the equipment item; protocol 41: + the
    /// name's length byte). The name's bytes follow (at most <see cref="PlayerNames.MaxLength"/>).</summary>
    public const int ScoreRowBytes = 14;
    /// <summary>Shot offsets travel as signed 16-bit integers in 1/10000 rad (±3.2767 rad).</summary>
    public const float ShotOffsetScale = 10000f;
    /// <summary>Shot-origin offsets travel as signed 16-bit integers in centimetres (±327.67 m).</summary>
    public const float ShotOriginScale = 100f;
    /// <summary>Bytes per input in an input batch (protocol 12; 40 since protocol 43: the body lag).</summary>
    public const int InputBytes = 40;
    /// <summary>Bytes of the hit block at the end of every snapshot (protocol 12; <see cref="HitView"/>).</summary>
    public const int HitBlockBytes = 12;
    /// <summary>
    /// Input buttons this protocol knows (bits 0..11 since protocol 17, without bit 6 since protocol 20, bit 12 Interact since 26; bit 13
    /// <see cref="InputButtons.ExtraAction"/>, added content's action key, since extras wire 2: the faithful simulation never reads it); others are
    /// dropped when an input batch is read.
    /// </summary>
    const ushort KnownButtons = 0x7FFF & ~64;   // protocol 44: bit 14 SlotSecond
    /// <summary>Most control point records in one snapshot (every point travels).</summary>
    public const int MaxSnapshotPoints = World.MaxControlPoints;
    /// <summary>
    /// Most projectile records in one snapshot (26 in protocol 9, when the snapshot filled the 1200-byte packet exactly; 20 in
    /// protocols 10 to 12; 19 in 13 to 15, to make room for the evade byte; 16 since protocol 16, to make room for
    /// <see cref="MinSnapshotShots"/> shot records). With 16 players (15 slim records of <see cref="SlimRecordBytes"/> bytes and
    /// the recipient's full one of <see cref="FullRecordBytes"/>) a snapshot is at most
    /// 13 + 1 + 16 × 14 + 1 + 15 × 47 + 144 + 9 (match) + 1 + 4 × 9 (kill feed) + 1 + 8 × 2 (points) + 12 (hits) + 1 + 4 × 9 (shots) = <see cref="MaxSnapshotBytes"/> bytes (full record 138 in protocol 17, 139 in 18 and 19, 141 in 20, 142 in 21 and 22, 144 since protocol 23).
    /// </summary>
    public const int MaxSnapshotProjectiles = 14;   // protocol 45: 14 (the holstered reloads' 8 bytes took one record's room)
    /// <summary>The packet size snapshots must not exceed (bytes, the UDP payload budget).</summary>
    public const int SnapshotBudget = 1200;
    /// <summary>Bytes of a shot record (protocol 16; <see cref="ShotView"/>).</summary>
    public const int ShotBytes = 9;
    /// <summary>Most shot records in one snapshot (protocol 16; the writer also stops at <see cref="SnapshotBudget"/>).</summary>
    public const int MaxSnapshotShots = 24;
    /// <summary>
    /// Shot records that fit even in the largest snapshot: <see cref="MaxSnapshotBytes"/> is computed with these (protocol 16: 5;
    /// protocol 17: 4, the own record's 4 reaction bytes took the fifth's room).
    /// </summary>
    public const int MinSnapshotShots = 4;
    /// <summary>Shot end points travel as signed 16-bit offsets from the shooter in 1/32 m (±1024 m, 3 cm steps).</summary>
    public const float ShotEndScale = 32f;
    /// <summary>The surface field of a shot record meaning "unknown" (and every hit that is not a world hit).</summary>
    public const int UnknownSurface = 15;
    /// <summary>Most ticks a shot record can say it is older than its snapshot.</summary>
    public const int MaxShotAge = 3;
    /// <summary>Bytes of a kill-feed entry (protocol 10: 8; protocol 15: + the direction's yaw byte).</summary>
    public const int KillEntryBytes = 9;
    /// <summary>Bytes of a slim player record (protocol 13: 46 + the evade byte).</summary>
    public const int SlimRecordBytes = 47;
    /// <summary>Bytes of the recipient's full record (protocol 13: the slim part, 85 more, and 2 evade bytes; protocol 17: 4 reaction bytes; 18: the charge lock; 20: ready and raise ticks; 21: ready ticks in 2 bytes; 23: the perk word).</summary>
    public const int FullRecordBytes = SlimRecordBytes + 97 + 14 + 8;   // protocol 44: + the third weapon's 14 bytes (and one projectile record fewer)
    /// <summary>Most kill-feed entries in one snapshot (protocol 10; the server sends the newest).</summary>
    public const int MaxSnapshotKills = 4;
    /// <summary>
    /// The largest snapshot with every other block full: 1200 bytes (protocol 11: 1175; protocol 12: + the 12-byte hit block =
    /// 1187; protocol 13: + 16 evade bytes in the player records + 2 in the own record − one 14-byte projectile record = 1191;
    /// protocol 15: + 4 kill-feed direction bytes = 1195; protocol 16: − three projectile records (42) + the shot count and
    /// five shot records (46) = 1199; protocol 17: + 4 reaction bytes in the own record − one guaranteed shot record (9) = 1194; protocol 18: + 1 charge-lock byte = 1195; protocol 20: + 2 raise bytes = 1197; protocol 21: ready ticks in 2 bytes = 1198; protocol 23: + the 2-byte perk word = 1200; protocol 44: + 14 bytes, - one projectile record = 1200; protocol 45: + 8 bytes, - one projectile record = 1194),
    /// at most the 1200-byte <see cref="SnapshotBudget"/> (no headroom left). A smaller snapshot
    /// carries more shot records, up to <see cref="MaxSnapshotShots"/>, but never more than the budget.
    /// </summary>
    public const int MaxSnapshotBytes = 13 + 1 + MaxSnapshotProjectiles * 14 + 1 + (MaxPlayers - 1) * SlimRecordBytes + FullRecordBytes + 9 + 1 + MaxSnapshotKills * KillEntryBytes
        + 1 + MaxSnapshotPoints * 2 + HitBlockBytes + 1 + MinSnapshotShots * ShotBytes;
    /// <summary>Classes travel in the low 6 bits of the class byte, the team in the top 2 (protocol 10): class indices 0..63.</summary>
    public const int MaxClasses = SimConfig.MaxClasses;
    /// <summary>Projectile positions travel as three signed 24-bit integers in 1/64 m: ±131 km, 1.6 cm steps.</summary>
    public const float PositionScale = 64f;
    const int I24Max = (1 << 23) - 1;
    /// <summary>A <see cref="Hello.RequestedClass"/> asking for the server's default class.</summary>
    public const byte ServerDefaultClass = 255;
    /// <summary>A requested primary, pistol or melee asking for the class default.</summary>
    public const byte DefaultChoice = 255;
    /// <summary>A weapon index or melee choice of "none" on the wire (indices travel as one byte, so at most 255 weapons or choices).</summary>
    public const byte NoIndex = 255;
    public const int MaxPlayers = 16;
    /// <summary>Longest map name a Challenge carries, so a Challenge never outgrows the Hello it answers.</summary>
    public const int MaxMapName = 24;
    public const int MaxInputsPerBatch = 16;
    public const int NoLead = int.MinValue;

    enum Kind : byte
    {
        Hello = 1, Welcome = 2, Reject = 3, InputBatch = 4, Snapshot = 5, Challenge = 6, Leave = 7, Scoreboard = 8,
        Setup = 9, SetupAck = 10, AppearanceQuery = 11, Appearance = 12, BlobRequest = 13, BlobChunk = 14,
        Pickups = 15,
        /// <summary>Protocol 44: the living enemies' health and armour for a player whose perks show them (Protocol.Bars.cs).</summary>
        EnemyBars = 18,
        /// <summary>Added content's envelope (Protocol.Envelope.cs): sent only by a server whose match runs added content; no protocol version of its own (ContentExtras.WireVersion 2).</summary>
        Extras = 16,
    }

    /// <summary>
    /// The version and nonce of a packet that starts like a Hello of any protocol since 3 (kind, version u16, fingerprint u64, config hash u64,
    /// nonce u32), whether or not this protocol can read the rest; null otherwise. Lets a server tell an old client why it is refused.
    /// </summary>
    public static (ushort Version, uint Nonce)? PeekHello(ReadOnlySpan<byte> packet) =>
        packet.Length >= 23 && packet[0] == (byte)Kind.Hello
            ? (BinaryPrimitives.ReadUInt16LittleEndian(packet[1..]), BinaryPrimitives.ReadUInt32LittleEndian(packet[19..]))
            : null;

    /// <summary>A loadout choice as one byte: 0..254, anything else (-1, the default, or too large) <see cref="DefaultChoice"/>.</summary>
    public static byte ToChoice(int choice) => choice is >= 0 and < DefaultChoice ? (byte)choice : DefaultChoice;
    /// <summary>A choice byte as an index: <see cref="DefaultChoice"/> is -1.</summary>
    public static int FromChoice(byte b) => b == DefaultChoice ? -1 : b;

    /// <summary>A loadout choice after a trip through the wire (fields outside 0..254 become -1).</summary>
    public static LoadoutChoice Quantize(LoadoutChoice c) => new(FromChoice(ToChoice(c.Class)), FromChoice(ToChoice(c.Primary)), FromChoice(ToChoice(c.Pistol)),
        FromChoice(ToChoice(c.Melee)), FromChoice(ToChoice(c.Equipment)), FromChoice(ToChoice(c.Perk1)), FromChoice(ToChoice(c.Perk2)), FromChoice(ToChoice(c.Second)));

    static void WriteChoice(Writer w, LoadoutChoice c)
    {
        w.U8(ToChoice(c.Class)); w.U8(ToChoice(c.Primary)); w.U8(ToChoice(c.Pistol)); w.U8(ToChoice(c.Melee)); w.U8(ToChoice(c.Equipment));
        w.U8(ToChoice(c.Perk1)); w.U8(ToChoice(c.Perk2)); w.U8(ToChoice(c.Second));
    }

    static LoadoutChoice ReadChoice(ref Reader r) => new(FromChoice(r.U8()), FromChoice(r.U8()), FromChoice(r.U8()), FromChoice(r.U8()), FromChoice(r.U8()),
        FromChoice(r.U8()), FromChoice(r.U8()), FromChoice(r.U8()));

    static void WriteCode(Writer w, AppearanceCode c) { Span<byte> b = stackalloc byte[AppearanceCode.Size]; c.WriteTo(b); w.Bytes(b); }
    static AppearanceCode ReadCode(ref Reader r) => AppearanceCode.FromBytes(r.Bytes(AppearanceCode.Size));
    static void WriteHash(Writer w, EmblemHash h) { Span<byte> b = stackalloc byte[EmblemHash.Bytes]; h.WriteTo(b); w.Bytes(b); }
    static EmblemHash ReadHash(ref Reader r) => EmblemHash.Read(r.Bytes(EmblemHash.Bytes));
    /// <summary>
    /// The code for <paramref name="race"/> out of a Hello's or Setup's three appearances (protocol 28): Chaos its own, Orks the look's
    /// (<see cref="Races.OrkCode"/>), anything else the Space Marine one.
    /// </summary>
    public static AppearanceCode CodeFor(Race race, AppearanceCode spaceMarine, AppearanceCode chaos, byte orkLook) => race switch
    {
        Race.Chaos => chaos,
        Race.Orks => Races.OrkCode(orkLook),
        _ => spaceMarine,
    };

    /// <summary>A player id a protocol-22 message carries: 1..65535.</summary>
    static bool PlayerId16(int id) => id is > 0 and <= ushort.MaxValue;

    public static byte[] Write(Message m)
    {
        var w = new Writer();
        switch (m)
        {
            case Hello h: w.U8((byte)Kind.Hello); w.U16(h.ProtocolVersion); w.U64(h.ContentFingerprint); w.U64(h.ConfigHash); w.U32(h.Nonce); w.U32(h.SentMs); w.U64(h.Cookie); w.U64(h.MapHash); w.U8(h.RequestedClass);
                w.U8(h.RequestedPrimary); w.U8(h.RequestedPistol); w.U8(h.RequestedMelee); w.U8(h.RequestedEquipment); w.U8(h.RequestedSide);
                w.U8(h.RequestedPerk1); w.U8(h.RequestedPerk2); WriteCode(w, h.SpaceMarineAppearance); WriteCode(w, h.ChaosAppearance); w.U8(h.OrkLook); w.U8(h.Flags); w.U8(h.RequestedSecond); break;
            case Setup x: w.U8((byte)Kind.Setup); w.U32(x.Session); w.U16(x.Sequence); WriteChoice(w, x.Loadout); WriteCode(w, x.SpaceMarineAppearance); WriteCode(w, x.ChaosAppearance);
                w.U8(x.OrkLook); break;
            case SetupAck a: w.U8((byte)Kind.SetupAck); w.U16(a.Sequence); WriteChoice(w, a.Loadout); w.U8((byte)(a.Flags & SetupFlags.All)); w.U16(a.AppearanceVersion); break;
            case AppearanceQuery q:
                if (q.PlayerIds.Length > MaxAppearanceQuery) throw new ArgumentException($"At most {MaxAppearanceQuery} ids in an appearance query.");
                if (!q.PlayerIds.All(PlayerId16) || q.PlayerIds.Distinct().Count() != q.PlayerIds.Length) throw new ArgumentException("Appearance query ids must be 1..65535, each once.");
                w.U8((byte)Kind.AppearanceQuery); w.U32(q.Session); w.U8((byte)q.PlayerIds.Length);
                foreach (int id in q.PlayerIds) w.U16((ushort)id);
                break;
            case Appearance a:
                if (!PlayerId16(a.PlayerId) || !Races.IsKnown(a.Race)) throw new ArgumentException("An appearance names a player 1..65535 and a race (1..3).");
                w.U8((byte)Kind.Appearance); w.U16((ushort)a.PlayerId); w.U16(a.Version); w.U8((byte)a.Race); WriteCode(w, a.Code); break;
            case BlobRequest q:
                if (q.Hash.IsNone || q.Chunk is < 0 or >= BlobTransfer.MaxChunks) throw new ArgumentException($"A blob request names a hash and a chunk 0..{BlobTransfer.MaxChunks - 1}.");
                w.U8((byte)Kind.BlobRequest); w.U32(q.Session); WriteHash(w, q.Hash); w.U8((byte)q.Chunk); break;
            case BlobChunk c:
                if (c.Hash.IsNone || c.Count is < 1 or > BlobTransfer.MaxChunks || c.Index < 0 || c.Index >= c.Count || c.Data is not { Length: >= 1 and <= BlobTransfer.ChunkBytes })
                    throw new ArgumentException($"A blob chunk names a hash, an index below a count of 1..{BlobTransfer.MaxChunks} and carries 1..{BlobTransfer.ChunkBytes} bytes.");
                w.U8((byte)Kind.BlobChunk); WriteHash(w, c.Hash); w.U8((byte)c.Index); w.U8((byte)c.Count); w.U16((ushort)c.Data.Length); w.Bytes(c.Data); break;
            case Leave l: w.U8((byte)Kind.Leave); w.U32(l.Session); break;
            case Challenge c:
            {
                if (!IsMapName(c.Map, allowEmpty: true)) throw new ArgumentException($"'{c.Map}' is not a map name (letters, digits, '_'; at most {MaxMapName}).");
                var map = Encoding.ASCII.GetBytes(c.Map);
                w.U8((byte)Kind.Challenge); w.U32(c.Nonce); w.U64(c.Cookie); w.U32(c.EchoSentMs); w.U8((byte)map.Length); w.Bytes(map); break;
            }
            case Welcome x: w.U8((byte)Kind.Welcome); w.I32(x.PlayerId); w.U32(x.ServerTick); w.I32(x.TickRate); w.U32(x.Nonce); w.U32(x.EchoSentMs); w.U32(x.Session); break;
            case Reject r:
                w.U8((byte)Kind.Reject); w.U8((byte)r.Reason); w.U32(r.Nonce);
                var detail = Encoding.UTF8.GetBytes(r.Detail.Length > 200 ? r.Detail[..200] : r.Detail);
                w.U16((ushort)detail.Length); w.Bytes(detail); break;
            case InputBatch b:
                w.U8((byte)Kind.InputBatch); w.U32(b.Session); w.U32(b.SnapshotTick); w.U8((byte)Math.Min(b.Inputs.Length, MaxInputsPerBatch));
                // 40 bytes each (protocol 12: buttons u16, shot direction offsets i16 in 1/10000 rad, shot origin offset 3 × i16 in cm; protocol 32: the melee target u16; 37: the view fraction u8; 43: the body lag u8).
                foreach (var i in b.Inputs.Take(MaxInputsPerBatch))
                {
                    w.U32(i.Tick); w.F32(i.MoveX); w.F32(i.MoveY); w.F32(i.Yaw); w.U16((ushort)i.Buttons); w.F32(i.Pitch); w.U32(i.ViewTick);
                    w.I16(ShotOffset(i.ShotYawOffset)); w.I16(ShotOffset(i.ShotPitchOffset));
                    w.I16(OriginOffset(i.ShotOriginX)); w.I16(OriginOffset(i.ShotOriginY)); w.I16(OriginOffset(i.ShotOriginZ));
                    w.U16((ushort)(i.MeleeTarget is > 0 and <= ushort.MaxValue ? i.MeleeTarget : 0));
                    w.U8(ViewFractionByte(i.ViewFraction));   // protocol 37
                    w.U8(BodyLag(i));   // protocol 43
                }
                break;
            case Snapshot s:
                w.U8((byte)Kind.Snapshot); w.U32(s.ServerTick); w.U32(s.LastInputTick); w.I32(s.InputLead);
                // Projectiles (14 bytes each): id u16, weapon u8, stage u8, flags u8, position 3 × i24 (1/64 m).
                var projectiles = s.Projectiles ?? [];
                int count = Math.Min(projectiles.Length, MaxSnapshotProjectiles);
                w.U8((byte)count);
                for (int k = 0; k < count; k++)
                {
                    var q = projectiles[k];
                    w.U16((ushort)q.Id); w.U8(Index(q.Weapon)); w.U8((byte)Math.Clamp(q.Stage, 0, byte.MaxValue)); w.U8((byte)(q.Flags & ProjectileFlags.All));
                    w.I24(Fixed(q.Position.X)); w.I24(Fixed(q.Position.Y)); w.I24(Fixed(q.Position.Z));
                }
                w.U8((byte)s.Players.Length);
                foreach (var p in s.Players)
                {
                    bool full = s.Self != 0 && p.PlayerId == s.Self;
                    // Protocol 44: the three weapons by slot; the one in hand, the first holstered (Loadouts.Other) and the second holstered (Loadouts.Third).
                    int WeaponOf(WeaponSlot k) => k == WeaponSlot.Pistol ? p.Pistol : k == WeaponSlot.Second ? p.Second : p.Primary;
                    int inHand = WeaponOf(p.Slot), holstered = WeaponOf(Loadouts.Other(p.Slot)), third = WeaponOf(Loadouts.Third(p.Slot));
                    // Slim part: everything needed to draw and interpolate a player (47 bytes).
                    w.I32(p.PlayerId); w.V3(p.Position); w.V3(p.Velocity); w.F32(p.Yaw); w.F32(p.Pitch);
                    w.U8((byte)((p.Grounded ? 1 : 0) | (p.Alive ? 2 : 0) | (full ? 4 : 0) | (p.Charging ? 8 : 0) | (p.Throwing ? 16 : 0)
                        | (JumpPack.Jumping(p.Jump) ? 32 : 0) | (JumpPack.Pounding(p.Jump) ? 64 : 0) | (p.Aiming ? 128 : 0)));
                    w.U8(ClassAndTeam(p)); w.U8(Index(inHand)); w.U8(Index(p.MeleeChoice)); w.U8(SlotByte(p));
                    var (meleeByte, meleeTime) = MeleeField(p);
                    w.U8(meleeByte); w.F32(meleeTime);   // protocol 17: heavy flag or a reaction (see MeleeField)
                    w.U8(EvadeByte(p));   // protocol 13: direction (bits 0..1) << 6 | ticks (0..63; 0 not evading); bit 2 in the slot byte (14)
                    if (!full) continue;
                    // The recipient's own player: everything its prediction replays from (97 more bytes). Protocol 23 (M43): first the
                    // perk word: perk 1 (bits 0..6), perk 2 (bits 7..13; 127 none) and a burst's rounds left (bits 14..15).
                    w.U16(PerkWord(p));
                    // Protocol 44 (14 bytes): the second holstered weapon's index, the real slot (bits 0..1) with its overheat (2) and the SlotSecond held flag (3), its clip,
                    // reserve, heat and heat age. Right after the perk word, so the fields at the record's end keep their offsets from the end.
                    w.U8(Index(third)); w.U8((byte)(((int)p.Slot & 3) | (p.ThirdOverheated ? 4 : 0) | (p.SlotSecondHeld ? 8 : 0) | (((int)p.MeleeDirection & 7) << 4) | (p.BufferedCharge ? 128 : 0)));   // protocol 45: bits 4..6 the melee attack's direction
                    w.U16(Ammo(p.ThirdClip)); w.U16(Ammo(p.ThirdReserve)); w.F32(p.ThirdHeat); w.F32(p.ThirdSinceHeat);
                    w.F32(p.OtherReloadLeft); w.F32(p.ThirdReloadLeft);   // protocol 45: the holstered weapons' reloads (8 bytes)
                    w.F32(p.Health); w.F32(p.Shield);
                    w.U16(Ammo(p.Clip)); w.U16(Ammo(p.Reserve)); w.U16(Ammo(p.OtherClip)); w.U16(Ammo(p.OtherReserve));
                    w.F32(p.Cooldown); w.F32(p.ReloadLeft); w.F32(p.SinceDamage); w.F32(p.RespawnLeft); w.F32(p.SwitchLeft);
                    w.U8(Index(holstered));
                    w.F32(p.Heat); w.F32(p.SinceHeat); w.F32(p.OtherHeat); w.F32(p.OtherSinceHeat);
                    w.F32(p.ChargeTime); w.F32(p.PendingLeft);
                    w.U8((byte)Math.Clamp(p.PendingShot, 0, byte.MaxValue)); w.U8((byte)Math.Clamp(p.ProjectilesOut, 0, byte.MaxValue));
                    // Jump pack (protocol 9; 9 bytes): phase and flags (8: Jump held, 16: holding, 32: landed moving), ticks, mark, power.
                    // Protocol 24 (M44): while taking off, bits 5..7 carry the latched dash direction (landed-moving is false then); otherwise bit 5 is landed-moving.
                    // Protocol 27 (M45): in the ascent, bit 6 says the rise is cut (Jump let go; the mark is then the cut's tick).
                    w.U8((byte)(((int)p.Jump & 7) | (p.JumpHeld ? 8 : 0) | (p.JumpHolding ? 16 : 0)
                        | (p.Jump == JumpPhase.TakeOff ? ((int)p.JumpDashDirection & 7) << 5 : p.JumpLandMoving ? 32 : 0)
                        | (p.Jump == JumpPhase.Ascent && p.JumpCut ? 64 : 0)));
                    w.U16((ushort)Math.Clamp(p.JumpTicks, 0, JumpPack.MaxTicks)); w.U16((ushort)Math.Clamp(p.JumpMark, 0, JumpPack.MaxTicks)); w.F32(p.JumpPower);
                    // Evade (protocol 13; 2 bytes): the exact ticks and the spacing lock.
                    w.U8((byte)Math.Clamp(p.EvadeTicks, 0, Evade.MaxTicks)); w.U8((byte)Math.Clamp(p.EvadeLock, 0, Evade.MaxTicks));
                    // Thrown equipment (protocol 8; 11 bytes): count, item, cooldown, throw time, flags (1: Grenade held;
                    // protocol 14: 2 SlotPistol held, 4 SlotPrimary held).
                    w.U8((byte)Math.Clamp(p.Grenades, 0, byte.MaxValue)); w.U8(Index(p.Equipment));
                    w.F32(p.GrenadeCooldown); w.F32(p.ThrowTime);
                    // Protocol 33 (feel/buffer-speed): bits 3..5 the buffered action, bits 6..7 the low 2 bits of its ticks (the high 2 in the raise byte).
                    var (bufferedAction, bufferTicks) = BufferFields(p);
                    w.U8((byte)((p.GrenadeHeld ? 1 : 0) | (p.SlotPistolHeld ? 2 : 0) | (p.SlotPrimaryHeld ? 4 : 0) | ((int)bufferedAction << 3) | ((bufferTicks & 3) << 6)));
                    w.U8(ReloadByte(p));   // Reload and Detonate held; protocol 29: a brace transition's ticks in bits 3..7 and 0
                    w.U8((byte)((p.MeleeQueued ? 1 : 0) | (p.MeleeHeld ? 2 : 0) | (p.FireHeld ? 4 : 0) | (p.SwitchHeld ? 8 : 0)
                        | (p.Overheated ? 16 : 0) | (p.OtherOverheated ? 32 : 0) | (p.MeleeHeavyHeld ? 64 : 0) | (p.MeleeQueued && p.MeleeQueuedHeavy ? 128 : 0)));
                    // Reaction (protocol 17; 4 bytes): knockback yaw byte, distance (1/16 m), ticks; blindness ticks.
                    w.U8(Byte(p.PushYaw)); w.U8(Byte(p.PushDistance)); w.U8(Byte(p.PushTicks)); w.U8(Byte(p.BlindTicks));
                    w.U8(Byte(p.RushLock));   // protocol 18: the charge's spacing lock
                    // Protocol 20: the weapon raise (M31); 21: ready ticks as a u16 (M32: a 10 s hold is 300 ticks at 30 Hz).
                    // Protocol 33: the raise ticks in bits 0..5 (at most Combat.MaxRaiseTicks), the buffer ticks' high 2 bits in 6..7.
                    w.U16((ushort)Math.Clamp(p.ReadyTicks, 0, SimConfig.MaxReadyTicks)); w.U8((byte)(Math.Clamp(p.RaiseTicks, 0, Combat.MaxRaiseTicks) | ((bufferTicks >> 2) << 6)));
                }
                // Match (protocol 10; 9 bytes): state | winner << 2, ticks left, team scores.
                var match = s.Match ?? default;
                if (match.State == MatchState.Countdown) throw new ArgumentException("MatchState.Countdown has no wire value: send it through MatchFlowWire.ToWire (Warmup with ticks left).");
                w.U8((byte)(((int)match.State & 3) | (((int)match.Winner & 3) << 2) | (((int)match.Mode & 1) << 4))); w.U32(match.TicksLeft);
                w.U16(Count(match.MarineScore)); w.U16(Count(match.ChaosScore));
                // Kill feed (protocol 10; 9 bytes each since 15): sequence, killer, victim (u16 each), cause | pitch code << 3, index, yaw byte.
                var kills = s.Kills ?? [];
                int killCount = Math.Min(kills.Length, MaxSnapshotKills);
                w.U8((byte)killCount);
                for (int k = 0; k < killCount; k++)
                {
                    var e = kills[k];
                    var (yaw, pitch) = KillDirection(e.Direction);
                    w.U16((ushort)e.Sequence); w.U16(Id16(e.Killer)); w.U16(Id16(e.Victim)); w.U8((byte)(((int)e.Cause & 7) | (pitch << 3))); w.U8(Index(e.Index)); w.U8(yaw);
                }
                // Control points (protocol 11; 2 bytes each): owner | side << 2 | contested << 4, progress in 1/255.
                var points = s.Points ?? [];
                int pointCount = Math.Min(points.Length, MaxSnapshotPoints);
                w.U8((byte)pointCount);
                for (int k = 0; k < pointCount; k++)
                {
                    var (owner, side, progress) = Normalized(points[k]);
                    w.U8((byte)((int)owner | ((int)side << 2) | (points[k].Contested ? 16 : 0))); w.U8(progress);
                }
                // The recipient's hits (protocol 12; 12 bytes): running totals, then the latest hit.
                var hits = s.Hits ?? default;
                w.U16((ushort)hits.Hits); w.U16((ushort)hits.ShieldTenths); w.U16((ushort)hits.HealthTenths); w.U8((byte)hits.Kills);
                w.U16(Id16(hits.LastVictim)); w.U8((byte)(hits.LastFlags & HitFlags.All)); w.U8(Fraction(hits.VictimHealth)); w.U8(Fraction(hits.VictimShield));
                // Shots (protocol 16; 9 bytes each): as many of the first ones as fit the budget (see ShotView).
                var shotRecords = new List<(int Index, ShotView Shot)>();
                foreach (var shot in s.Shots ?? [])
                {
                    int index = Array.FindIndex(s.Players, p => p.PlayerId == shot.Shooter);
                    if (index >= 0 && index < MaxPlayers && Encodable(shot)) shotRecords.Add((index, shot));   // 4-bit index
                }
                int shotCount = Math.Min(Math.Min(shotRecords.Count, MaxSnapshotShots), Math.Max(0, (SnapshotBudget - w.Length - 1) / ShotBytes));
                w.U8((byte)shotCount);
                for (int k = 0; k < shotCount; k++)
                {
                    var (index, shot) = shotRecords[k];
                    var (x, y, z) = ShotOffset(shot.End - s.Players[index].Position);
                    w.U8((byte)(index | ((int)shot.Hit << 4) | (Math.Clamp(shot.Age, 0, MaxShotAge) << 6)));
                    w.U8((byte)shot.Weapon);
                    w.U8((byte)(shot.Stage | (SurfaceCode(shot) << 3) | (ArmouredHit(shot) ? 128 : 0)));   // protocol 22: bit 7 armour hit
                    w.I16(x); w.I16(y); w.I16(z);
                }
                break;
            case Scoreboard b:
            {
                // Scoreboard (protocol 10): tick, flags (1 teams, 2 friendly fire, 4 Seize Ground: protocol 11), score limit,
                // time limit, [Seize Ground block], rows of 12 bytes (protocol 22: + the appearance version; protocol 40: 13, + the equipment item;
                // protocol 41: + the display name, a length byte and 0..24 Latin-1 bytes).
                w.U8((byte)Kind.Scoreboard); w.U32(b.ServerTick); w.U8((byte)((b.Teams ? 1 : 0) | (b.FriendlyFire ? 2 : 0) | (b.Seize is null ? 0 : 4) | (b.Spectators is { Length: > 0 } ? 8 : 0)));
                w.U16(Count(b.ScoreLimit)); w.F32(float.IsFinite(b.TimeLimit) ? Math.Max(0f, b.TimeLimit) : 0f);
                if (b.Seize is { } seize)
                {
                    // Seize Ground (protocol 11): capture time, neutralise time, score per point, interval, then the points
                    // (name length and ASCII name, centre, radius, half height).
                    if (seize.Points.Length > MaxSnapshotPoints) throw new ArgumentException($"At most {MaxSnapshotPoints} control points.");
                    if (!Positive(seize.CaptureTime) || !Positive(seize.NeutraliseTime) || !Positive(seize.ScoreInterval))
                        throw new ArgumentException("Seize Ground times must be finite and positive.");
                    w.F32(seize.CaptureTime); w.F32(seize.NeutraliseTime); w.U16(Count(seize.PointScore)); w.F32(seize.ScoreInterval);
                    w.U8((byte)seize.Points.Length);
                    foreach (var p in seize.Points)
                    {
                        if (!IsMapName(p.Name) || !float.IsFinite(p.Center.X) || !float.IsFinite(p.Center.Y) || !float.IsFinite(p.Center.Z) || !Positive(p.Radius) || !Positive(p.HalfHeight))
                            throw new ArgumentException($"Control point '{p.Name}': the name must be letters, digits or '_' (at most {MaxMapName}), the shape finite and positive.");
                        var name = Encoding.ASCII.GetBytes(p.Name);
                        w.U8((byte)name.Length); w.Bytes(name); w.V3(p.Center); w.F32(p.Radius); w.F32(p.HalfHeight);
                    }
                }
                int rows = Math.Min(b.Players.Length, MaxPlayers);
                w.U8((byte)rows);
                for (int k = 0; k < rows; k++)
                {
                    var e = b.Players[k];
                    w.I32(e.PlayerId); w.U8((byte)(e.Team is Team.SpaceMarines or Team.Chaos ? e.Team : Team.None)); w.U8((byte)Math.Clamp(e.Class, 0, MaxClasses - 1));
                    w.U16(Count(e.Kills)); w.U16(Count(e.Deaths)); w.U16(e.AppearanceVersion);
                    w.U8(e.Equipment is >= 0 and < 255 ? (byte)e.Equipment : (byte)255);   // protocol 40: the equipment item (255: none)
                    var name = Encoding.Latin1.GetBytes(PlayerNames.Sanitise(e.Name));   // protocol 41: the display name (0..24 Latin-1 bytes)
                    w.U8((byte)name.Length); w.Bytes(name);
                }
                if (b.Spectators is { Length: > 0 } spectators)
                {
                    // Protocol 46: the spectators, after the rows: count, then id and display name each.
                    int listed = Math.Min(spectators.Length, MaxSpectators);
                    w.U8((byte)listed);
                    for (int k = 0; k < listed; k++)
                    {
                        w.I32(spectators[k].PlayerId);
                        var name = Encoding.Latin1.GetBytes(PlayerNames.Sanitise(spectators[k].Name));
                        w.U8((byte)name.Length); w.Bytes(name);
                    }
                }
                break;
            }
            case EnemyBars eb: WriteBars(w, eb); break;   // protocol 44 (Protocol.Bars.cs)
            case PickupUpdate p: WritePickups(w, p); break;   // protocol 26 (Protocol.Pickups.cs)
            case ExtrasUpdate x: WriteExtras(w, x); break;   // added content's envelope (Protocol.Envelope.cs; extras wire 2)
            default: throw new ArgumentException($"Unknown message {m.GetType().Name}.");
        }
        return w.ToArray();
    }

    /// <summary>Parses a packet; returns null for anything malformed (wrong length, unknown type, bad counts).</summary>
    public static Message? Read(ReadOnlySpan<byte> packet)
    {
        var r = new Reader(packet);
        try
        {
            Message? m = (Kind)r.U8() switch
            {
                Kind.Hello => new Hello(r.U16(), r.U64(), r.U64(), r.U32(), r.U32(), r.U64(), r.U64(), r.U8(), r.U8(), r.U8(), r.U8(), r.U8(), r.U8(),
                    r.U8(), r.U8(), ReadCode(ref r), ReadCode(ref r), r.U8(), r.U8(), r.U8()),
                Kind.Setup => new Setup(r.U32(), r.U16(), ReadChoice(ref r), ReadCode(ref r), ReadCode(ref r), r.U8()),
                Kind.SetupAck => ReadSetupAck(ref r),
                Kind.AppearanceQuery => ReadAppearanceQuery(ref r),
                Kind.Appearance => ReadAppearance(ref r),
                Kind.BlobRequest => ReadBlobRequest(ref r),
                Kind.BlobChunk => ReadBlobChunk(ref r),
                Kind.Challenge => ReadChallenge(ref r),
                Kind.Leave => new Leave(r.U32()),
                Kind.Welcome => new Welcome(r.I32(), r.U32(), r.I32(), r.U32(), r.U32(), r.U32()),
                Kind.Reject => ReadReject(ref r),
                Kind.InputBatch => ReadInputs(ref r),
                Kind.Snapshot => ReadSnapshot(ref r),
                Kind.Scoreboard => ReadScoreboard(ref r),
                Kind.Pickups => ReadPickups(ref r),
                Kind.EnemyBars => ReadBars(ref r),
                Kind.Extras => packet.Length <= MaxExtrasMessageBytes ? ReadExtras(ref r) : null,   // added content's envelope: oversized is refused
                _ => null,
            };
            return m is not null && r.AtEnd ? m : null;
        }
        catch (IndexOutOfRangeException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    static SetupAck? ReadSetupAck(ref Reader r)
    {
        ushort sequence = r.U16(); var choice = ReadChoice(ref r); int flags = r.U8(); ushort version = r.U16();
        return (flags & ~(int)SetupFlags.All) != 0 ? null : new SetupAck(sequence, choice, (SetupFlags)flags, version);   // unknown flags
    }

    static AppearanceQuery? ReadAppearanceQuery(ref Reader r)
    {
        uint session = r.U32();
        int n = r.U8();
        if (n > MaxAppearanceQuery) return null;
        var ids = new int[n];
        for (int k = 0; k < n; k++)
        {
            ids[k] = r.U16();
            if (ids[k] == 0) return null;   // 0 is "none"
            for (int j = 0; j < k; j++) if (ids[j] == ids[k]) return null;   // each id once
        }
        return new AppearanceQuery(session, ids);
    }

    static Appearance? ReadAppearance(ref Reader r)
    {
        int id = r.U16(); ushort version = r.U16(); int race = r.U8();
        var code = ReadCode(ref r);
        return id == 0 || !Races.IsKnown((Race)race) ? null : new Appearance(id, version, (Race)race, code);
    }

    static BlobRequest? ReadBlobRequest(ref Reader r)
    {
        uint session = r.U32(); var hash = ReadHash(ref r); int chunk = r.U8();
        return hash.IsNone || chunk >= BlobTransfer.MaxChunks ? null : new BlobRequest(session, hash, chunk);
    }

    static BlobChunk? ReadBlobChunk(ref Reader r)
    {
        var hash = ReadHash(ref r); int index = r.U8(), count = r.U8(), length = r.U16();
        if (hash.IsNone || count is < 1 or > BlobTransfer.MaxChunks || index >= count || length is < 1 or > BlobTransfer.ChunkBytes) return null;
        return new BlobChunk(hash, index, count, r.Bytes(length).ToArray());   // a length beyond the packet throws: malformed
    }

    static Challenge? ReadChallenge(ref Reader r)
    {
        uint nonce = r.U32(); ulong cookie = r.U64(); uint echo = r.U32();
        int length = r.U8();
        if (length > MaxMapName) return null;
        var name = Encoding.ASCII.GetString(r.Bytes(length));
        return IsMapName(name, allowEmpty: true) ? new Challenge(nonce, cookie, echo, name) : null;
    }

    /// <summary>Map names travel in Challenges and become file names on the client: ASCII letters, digits and '_' only.</summary>
    public static bool IsMapName(string name, bool allowEmpty = false) =>
        (name.Length > 0 || allowEmpty) && name.Length <= MaxMapName && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    static Reject ReadReject(ref Reader r)
    {
        var reason = (RejectReason)r.U8(); uint nonce = r.U32();
        int length = r.U16();
        return new Reject(reason, Encoding.UTF8.GetString(r.Bytes(length)), nonce);
    }

    static InputBatch? ReadInputs(ref Reader r)
    {
        uint session = r.U32();
        uint snapshotTick = r.U32();
        int n = r.U8();
        if (n > MaxInputsPerBatch) return null;
        var inputs = new PlayerInput[n];
        for (int i = 0; i < n; i++)
        {
            inputs[i] = new PlayerInput(r.U32(), r.F32(), r.F32(), r.F32(), (InputButtons)(r.U16() & KnownButtons), r.F32(), r.U32(), r.I16() / ShotOffsetScale, r.I16() / ShotOffsetScale,
                r.I16() / ShotOriginScale, r.I16() / ShotOriginScale, r.I16() / ShotOriginScale, r.U16(), r.U8() / ViewFractionScale);
            inputs[i] = inputs[i] with { BodyTick = BodyTickOf(inputs[i].Tick, r.U8()) };   // protocol 43
        }
        return new InputBatch(inputs, session, snapshotTick);
    }

    static Snapshot? ReadSnapshot(ref Reader r)
    {
        uint tick = r.U32(), last = r.U32(); int lead = r.I32();
        int m = r.U8();
        if (m > MaxSnapshotProjectiles) return null;
        var projectiles = new ProjectileView[m];
        for (int k = 0; k < m; k++)
        {
            int id = r.U16(), weapon = r.U8(), stage = r.U8(), flags = r.U8();
            if (weapon == NoIndex || (flags & ~(int)ProjectileFlags.All) != 0) return null;   // no weapon, unknown flags
            if ((flags & (int)ProjectileFlags.Thrown) != 0 && stage != 0) return null;         // thrown items have no stages
            var position = new Vector3(r.I24(), r.I24(), r.I24()) / PositionScale;
            for (int j = 0; j < k; j++) if (projectiles[j].Id == id) return null;   // one record per projectile
            projectiles[k] = new ProjectileView(id, weapon, stage, position, (ProjectileFlags)flags);
        }
        int n = r.U8();
        if (n > MaxPlayers) return null;
        var players = new PlayerState[n];
        int self = 0;
        for (int i = 0; i < n; i++)
        {
            int id = r.I32(); var position = r.V3(); var velocity = r.V3(); float yaw = r.F32(), pitch = r.F32();
            int flags = r.U8();
            if ((flags & 96) == 96) return null;   // jumping and pounding at once (bit 7, aiming, since protocol 12)
            int classAndTeam = r.U8(), inHand = FromIndex(r.U8()), meleeChoice = FromIndex(r.U8()), slotByte = r.U8();
            if (classAndTeam >> 6 > (int)Team.Chaos) return null;   // unknown team
            bool slash = (slotByte & 128) != 0;   // protocol 30: the pound phase is the sword slash's
            if (slash && (flags & 96) == 0) return null;   // only with the pounding flag (protocol 31: or the jumping one, a slash press buffered in the take-off)
            int slot = slotByte & 1, diagonal = (slotByte >> 1) & 1;   // protocol 14: the evade direction's bit 2
            bool up = (slotByte & 4) != 0;   // protocol 20: the weapon is up (ready or raising)
            bool landed = (slotByte & 8) != 0;   // protocol 24 (M44): a jump-pack dash's landing part
            var bracePhase = (BracePhase)((slotByte >> 4) & 3);   // protocol 29: the brace (fidelity gap 7)
            bool venting = (slotByte & 64) != 0;   // protocol 29: venting
            int playerClass = classAndTeam & (MaxClasses - 1);
            int meleeByte = r.U8(); float meleeTime = r.F32();
            if (FromMeleeField(meleeByte, meleeTime) is not { } melee) return null;   // unknown bits, kind 0, or ticks not a whole number 0..255
            int evade = r.U8(), evadeTicks = evade & Evade.MaxSlimTicks;
            if (evadeTicks == 0 && (evade != 0 || diagonal != 0 || landed)) return null;   // a direction or a landing without an evade
            var p = new PlayerState(id, position, velocity, yaw, (flags & 1) != 0)
            {
                Pitch = pitch, Alive = (flags & 2) != 0, Charging = (flags & 8) != 0, Throwing = (flags & 16) != 0, Aiming = (flags & 128) != 0,
                Jump = (flags & 32) != 0 ? JumpPhase.Ascent : (flags & 64) != 0 ? JumpPhase.Dive : JumpPhase.None, Class = playerClass, Slot = (WeaponSlot)slot,
                MeleeStep = melee.Step, MeleeTime = melee.Time, MeleeHeavy = melee.Heavy, Reaction = melee.Reaction, ReactionDirection = melee.Direction, ReactionTicks = melee.Ticks,
                Rush = melee.Rush, RushTicks = melee.RushTicks, SprintStop = melee.SprintStop,
                MeleeChoice = meleeChoice, Team = (Team)(classAndTeam >> 6),
                Primary = slot == (int)WeaponSlot.Ranged ? inHand : -1, Pistol = slot == (int)WeaponSlot.Pistol ? inHand : -1,
                EvadeTicks = evadeTicks, EvadeDirection = (EvadeDirection)((evade >> 6) | (diagonal << 2)), EvadeLanded = landed,
                ReadyTicks = up ? 1 : 0,   // slim: a representative value (the own record below has the exact ticks)
                Brace = bracePhase, Venting = venting, JumpSlash = slash,
            };
            if ((flags & 4) != 0)
            {
                if (self != 0 || id == 0) return null;   // at most one full record, and never for id 0 ("none")
                self = id;
                int perks = r.U16();   // protocol 23 (M43): the perk word first
                p = p with { Perk1 = FromPerk(perks & NoPerk), Perk2 = FromPerk((perks >> 7) & NoPerk), BurstLeft = perks >> 14 };
                // Protocol 44: the third weapon's store and the real slot.
                int thirdIndex = FromIndex(r.U8()), ext = r.U8();
                if ((ext & 3) == 3) return null;
                var meleeDirection = (EvadeDirection)((ext >> 4) & 7);   // protocol 45
                if (meleeDirection != EvadeDirection.Forward && p.MeleeStep == 0) return null;   // a direction without an attack
                var realSlot = (WeaponSlot)(ext & 3);
                if ((realSlot == WeaponSlot.Pistol) != (slot == (int)WeaponSlot.Pistol)) return null;   // disagrees with the slim slot bit
                p = p with { MeleeDirection = meleeDirection, ThirdOverheated = (ext & 4) != 0, SlotSecondHeld = (ext & 8) != 0, ThirdClip = r.U16(), ThirdReserve = r.U16(), ThirdHeat = r.F32(), ThirdSinceHeat = r.F32() };
                p = p with { OtherReloadLeft = r.F32(), ThirdReloadLeft = r.F32() };   // protocol 45
                p = p with
                {
                    Health = r.F32(), Shield = r.F32(), Clip = r.U16(), Reserve = r.U16(), OtherClip = r.U16(), OtherReserve = r.U16(),
                    Cooldown = r.F32(), ReloadLeft = r.F32(), SinceDamage = r.F32(), RespawnLeft = r.F32(), SwitchLeft = r.F32(),
                };
                int holstered = FromIndex(r.U8());
                p = slot == (int)WeaponSlot.Pistol ? p with { Primary = holstered } : p with { Pistol = holstered };
                p = p with { Heat = r.F32(), SinceHeat = r.F32(), OtherHeat = r.F32(), OtherSinceHeat = r.F32() };
                p = p with { ChargeTime = r.F32(), PendingLeft = r.F32(), PendingShot = r.U8(), ProjectilesOut = r.U8() };
                int jump = r.U8();
                var phase = (JumpPhase)(jump & 7);
                if (phase != JumpPhase.TakeOff && jump > (phase == JumpPhase.Ascent ? 127 : 63)) return null;   // unknown flags (protocol 24: bits 5..7 are the dash direction while taking off; 27: bit 6 the ascent's cut)
                if (phase == JumpPhase.Ascent && (jump & 64) != 0 && (jump & 16) != 0) return null;   // a cut rise is not holding
                if (JumpPack.Jumping(phase) != ((flags & 32) != 0) || JumpPack.Pounding(phase) != ((flags & 64) != 0)) return null;   // disagrees with the slim flags
                if (slash && phase is JumpPhase.Hang or JumpPhase.Fall) return null;   // protocol 31: the buffered press only in the take-off and on the lift-off tick
                p = p with
                {
                    Jump = phase, JumpHeld = (jump & 8) != 0, JumpHolding = (jump & 16) != 0,
                    JumpLandMoving = phase != JumpPhase.TakeOff && (jump & 32) != 0, JumpDashDirection = phase == JumpPhase.TakeOff ? (EvadeDirection)(jump >> 5) : EvadeDirection.Forward,
                    JumpCut = phase == JumpPhase.Ascent && (jump & 64) != 0,
                    JumpTicks = r.U16(), JumpMark = r.U16(), JumpPower = r.F32(),
                };
                int exactTicks = r.U8(), evadeLock = r.U8();
                if (Math.Min(exactTicks, Evade.MaxSlimTicks) != evadeTicks) return null;   // disagrees with the slim byte
                p = p with { EvadeTicks = exactTicks, EvadeLock = evadeLock };
                p = p with { Grenades = r.U8(), Equipment = FromIndex(r.U8()), GrenadeCooldown = r.F32(), ThrowTime = r.F32() };
                int grenade = r.U8();
                int buffered = (grenade >> 3) & 7, bufferLow = grenade >> 6;
                if (buffered > (int)BufferedAction.JumpLong) return null;   // protocol 33: an unknown buffered action (protocol 38: 7 is the long Jump press)
                p = p with { GrenadeHeld = (grenade & 1) != 0, SlotPistolHeld = (grenade & 2) != 0, SlotPrimaryHeld = (grenade & 4) != 0, Buffered = (BufferedAction)buffered, BufferedCharge = (ext & 128) != 0 };
                if ((ext & 128) != 0 && p.Buffered != BufferedAction.MeleeHeavy) return null;   // protocol 45: a charge press flag without a buffered heavy press
                int charge = r.U8();
                // Protocol 29: a brace transition's ticks (bits 3..7 low, bit 0 high: 0..63), only while entering or exiting (and then at least 1).
                int braceTicks = ((charge >> 3) & 31) | ((charge & 1) << 5);
                if ((braceTicks > 0) != (bracePhase is BracePhase.Entering or BracePhase.Exiting)) return null;
                p = p with { ReloadHeld = (charge & 2) != 0, DetonateHeld = (charge & 4) != 0, BraceTicks = braceTicks };
                int more = r.U8();
                if ((more & 128) != 0 && (more & 1) == 0) return null;   // a heavy buffered press without a buffered press
                p = p with
                {
                    MeleeQueued = (more & 1) != 0, MeleeHeld = (more & 2) != 0, FireHeld = (more & 4) != 0, SwitchHeld = (more & 8) != 0,
                    Overheated = (more & 16) != 0, OtherOverheated = (more & 32) != 0, MeleeHeavyHeld = (more & 64) != 0, MeleeQueuedHeavy = (more & 128) != 0,
                };
                int pushYaw = r.U8(), pushDistance = r.U8(), pushTicks = r.U8(), blind = r.U8();
                if ((pushDistance == 0) != (pushTicks == 0) || (pushDistance == 0 && pushYaw != 0)) return null;   // a knockback has both or neither (and no yaw without one)
                if (pushDistance != 0 && p.Reaction == ReactionKind.None) return null;   // a knockback outside a reaction
                p = p with { PushYaw = pushYaw, PushDistance = pushDistance, PushTicks = pushTicks, BlindTicks = blind, RushLock = r.U8() };
                int ready = r.U16(), raiseByte = r.U8(), raise = raiseByte & Combat.MaxRaiseTicks;
                int bufferTicks = bufferLow | ((raiseByte >> 6) << 2);
                if ((ready > 0 || raise > 0) != up) return null;   // disagrees with the slim "weapon up" bit
                if (buffered == 0 && bufferTicks != 0) return null;   // protocol 33: ticks without a buffered action
                p = p with { ReadyTicks = ready, RaiseTicks = raise, BufferTicks = bufferTicks };
                // Protocol 44: the three weapon indices are placed by the real slot in hand (the third store was read after the perk word).
                var byslot = new int[3];
                byslot[(int)realSlot] = inHand; byslot[(int)Loadouts.Other(realSlot)] = holstered; byslot[(int)Loadouts.Third(realSlot)] = thirdIndex;
                p = p with { Slot = realSlot, Primary = byslot[(int)WeaponSlot.Ranged], Pistol = byslot[(int)WeaponSlot.Pistol], Second = byslot[(int)WeaponSlot.Second] };
            }
            if (players.Take(i).Any(q => q.PlayerId == id)) return null;   // one record per player (a duplicate would break the client)
            players[i] = p;
        }
        int state = r.U8();
        if (state > 31 || ((state >> 2) & 3) > (int)Team.Chaos) return null;   // unknown state bits or mode, or winner 3
        var match = new MatchView((MatchState)(state & 3), (Team)((state >> 2) & 3), r.U32(), r.U16(), r.U16(), (GameMode)(state >> 4));
        int killCount = r.U8();
        if (killCount > MaxSnapshotKills) return null;
        var kills = new KillView[killCount];
        for (int k = 0; k < killCount; k++)
        {
            int sequence = r.U16(), killer = r.U16(), victim = r.U16(), causeByte = r.U8(), index = FromIndex(r.U8()), yaw = r.U8();
            int cause = causeByte & 7, pitch = causeByte >> 3;
            if (cause > (int)KillCause.Perk) return null;   // protocol 23: perk kills (5)
            if (pitch == 0 && yaw != 0) return null;   // no direction travels as zeros
            for (int j = 0; j < k; j++) if (kills[j].Sequence == sequence) return null;   // one entry per death
            kills[k] = new KillView(sequence, killer, victim, (KillCause)cause, index, KillDirection(yaw, pitch));
        }
        int pointCount = r.U8();
        if (pointCount > MaxSnapshotPoints) return null;
        var points = new ControlPointView[pointCount];
        for (int k = 0; k < pointCount; k++)
        {
            int flags = r.U8(), progress = r.U8();
            int owner = flags & 3, side = (flags >> 2) & 3;
            if (flags > 31 || owner > (int)Team.Chaos || side > (int)Team.Chaos) return null;   // unknown bits or sides
            if ((owner != 0 && side != owner) || (side == 0 && progress != 0)) return null;      // an owned point's progress is its owner's; no side, no progress
            points[k] = new ControlPointView((Team)owner, (Team)side, progress / 255f, (flags & 16) != 0);
        }
        int hitCount = r.U16(), shieldTenths = r.U16(), healthTenths = r.U16(), hitKills = r.U8(), lastVictim = r.U16(), hitFlags = r.U8(), victimHealth = r.U8(), victimShield = r.U8();
        if (hitFlags > (int)HitFlags.All) return null;   // unknown flags
        var hits = new HitView(hitCount, shieldTenths, healthTenths, hitKills, lastVictim, (HitFlags)hitFlags, victimHealth / 255f, victimShield / 255f);
        int shotCount = r.U8();
        if (shotCount > MaxSnapshotShots) return null;
        var shots = new ShotView[shotCount];
        for (int k = 0; k < shotCount; k++)
        {
            int head = r.U8(), weapon = r.U8(), info = r.U8();
            var offset = new Vector3(r.I16(), r.I16(), r.I16()) / ShotEndScale;
            int index = head & 15, hit = (head >> 4) & 3, age = head >> 6, stage = info & 7, surface = (info >> 3) & 15;
            bool armoured = (info & 128) != 0;
            if (index >= n || weapon == NoIndex) return null;                                       // no such player record, no weapon
            if ((ShotHit)hit != ShotHit.World && surface != UnknownSurface) return null;           // only world hits name a surface
            if (armoured && (ShotHit)hit is not (ShotHit.Body or ShotHit.Head)) return null;       // only player hits strike armour
            shots[k] = new ShotView(players[index].PlayerId, weapon, stage, (ShotHit)hit, surface == UnknownSurface ? -1 : surface, players[index].Position + offset, age, armoured);
        }
        return new Snapshot(tick, last, lead, players, self, projectiles, match, kills, points, hits, shots);
    }

    /// <summary>Whether a shot can travel: a weapon index 0..254, a stage 0..7, a known hit kind and a finite end point.</summary>
    static bool Encodable(ShotView s) => s.Weapon is >= 0 and < NoIndex && s.Stage is >= 0 and <= 7 && s.Hit is >= ShotHit.None and <= ShotHit.Head
        && float.IsFinite(s.End.X) && float.IsFinite(s.End.Y) && float.IsFinite(s.End.Z);

    /// <summary>The armour bit of a shot record (protocol 22): set only for a player hit that struck a raised shield.</summary>
    static bool ArmouredHit(ShotView s) => s.Armoured && s.Hit is ShotHit.Body or ShotHit.Head;

    /// <summary>The surface field: a world hit's material 0..14, else (and for anything out of range) <see cref="UnknownSurface"/>.</summary>
    static int SurfaceCode(ShotView s) => s.Hit == ShotHit.World && s.Surface is >= 0 and < UnknownSurface ? s.Surface : UnknownSurface;

    /// <summary>An end-point offset in 1/32 m steps; one longer than the range is shortened along its direction (non-finite: zero).</summary>
    static (short X, short Y, short Z) ShotOffset(Vector3 d)
    {
        if (!float.IsFinite(d.X) || !float.IsFinite(d.Y) || !float.IsFinite(d.Z)) return (0, 0, 0);
        const float limit = short.MaxValue / ShotEndScale;
        float largest = MathF.Max(MathF.Abs(d.X), MathF.Max(MathF.Abs(d.Y), MathF.Abs(d.Z)));
        if (largest > limit) d *= limit / largest;
        static short Q(float v) => (short)Math.Clamp(MathF.Round(v * ShotEndScale), -short.MaxValue, short.MaxValue);
        return (Q(d.X), Q(d.Y), Q(d.Z));
    }

    /// <summary>
    /// A shot after a trip through a snapshot whose record for its shooter is at <paramref name="shooterPosition"/>: the end
    /// point snapped to 1/32 m from there (shortened beyond ±1024 m), age clamped to 0..3, the surface -1 unless a world hit
    /// names one of 0..14.
    /// </summary>
    public static ShotView Quantize(ShotView s, Vector3 shooterPosition)
    {
        var (x, y, z) = ShotOffset(s.End - shooterPosition);
        int surface = SurfaceCode(s);
        return s with { End = shooterPosition + new Vector3(x, y, z) / ShotEndScale, Age = Math.Clamp(s.Age, 0, MaxShotAge), Surface = surface == UnknownSurface ? -1 : surface,
            Armoured = ArmouredHit(s) };
    }

    /// <summary>
    /// A kill-feed entry after a trip through a snapshot: sequence modulo 65536, ids 16-bit, index -1..254, the direction
    /// snapped to 256 yaw steps and 31 pitch codes (zero stays zero).
    /// </summary>
    public static KillView Quantize(KillView k)
    {
        var (yaw, pitch) = KillDirection(k.Direction);
        return new KillView(k.Sequence & 0xFFFF, Id16(k.Killer), Id16(k.Victim), (KillCause)((int)k.Cause & 7), FromIndex(Index(k.Index)), KillDirection(yaw, pitch));
    }

    /// <summary>
    /// A kill direction on the wire: yaw byte (2π/256 steps, the simulation's yaw = atan2(−x, y)) and a pitch code 1..31
    /// (code − 16 steps of 6°, ±90°); (0, 0) when the vector is zero or not finite.
    /// Straight up or down (codes 31 and 1) travel with yaw 0 and decode exactly vertical, so re-quantizing a decoded entry is stable.
    /// </summary>
    static (byte Yaw, int Pitch) KillDirection(Vector3 d)
    {
        if (!float.IsFinite(d.X) || !float.IsFinite(d.Y) || !float.IsFinite(d.Z) || d.LengthSquared() < 1e-12f) return (0, 0);
        d = Vector3.Normalize(d);
        float yaw = MathF.Atan2(-d.X, d.Y), pitch = MathF.Asin(Math.Clamp(d.Z, -1f, 1f));
        int y = (int)MathF.Round(yaw / (2 * MathF.PI) * 256f) & 255;
        int p = 16 + Math.Clamp((int)MathF.Round(pitch / (MathF.PI / 2) * 15f), -15, 15);
        return (p is 1 or 31 ? (byte)0 : (byte)y, p);
    }

    /// <summary>The unit vector of a wire kill direction (zero for pitch code 0).</summary>
    static Vector3 KillDirection(int yaw, int pitch)
    {
        if (pitch == 0) return Vector3.Zero;
        if (pitch is 1 or 31) return pitch == 31 ? Vector3.UnitZ : -Vector3.UnitZ;   // vertical: no yaw (cos(±π/2) is not exactly 0 in floats)
        float y = yaw * (2 * MathF.PI / 256f), p = (pitch - 16) * (MathF.PI / 2 / 15f);
        float horizontal = MathF.Cos(p);
        return new Vector3(-MathF.Sin(y) * horizontal, MathF.Cos(y) * horizontal, MathF.Sin(p));
    }

    /// <summary>A hit block after a trip through a snapshot: totals modulo their widths, fractions clamped to 0..1 in 1/255 steps.</summary>
    public static HitView Quantize(HitView h) => new(h.Hits & 0xFFFF, h.ShieldTenths & 0xFFFF, h.HealthTenths & 0xFFFF, h.Kills & 0xFF, Id16(h.LastVictim),
        h.LastFlags & HitFlags.All, Fraction(h.VictimHealth) / 255f, Fraction(h.VictimShield) / 255f);

    static byte Fraction(float f) => float.IsNaN(f) ? (byte)0 : (byte)MathF.Round(Math.Clamp(f, 0f, 1f) * 255f);

    /// <summary>A shot offset as the wire carries it: 1/10000 rad, clamped to ±3.2767 rad (non-finite: 0).</summary>
    static short ShotOffset(float radians) => float.IsFinite(radians) ? (short)Math.Clamp(MathF.Round(radians * ShotOffsetScale), short.MinValue + 1, short.MaxValue) : (short)0;

    /// <summary>
    /// An input after a trip through an input batch: the shot direction offsets in 1/10000 rad steps, the origin offset in
    /// centimetres, unknown buttons dropped. The client predicts with this, so its prediction sees what the server will.
    /// </summary>
    public static PlayerInput Quantize(PlayerInput i) => i with
    {
        Buttons = (InputButtons)((ushort)i.Buttons & KnownButtons),
        ShotYawOffset = ShotOffset(i.ShotYawOffset) / ShotOffsetScale, ShotPitchOffset = ShotOffset(i.ShotPitchOffset) / ShotOffsetScale,
        ShotOriginX = OriginOffset(i.ShotOriginX) / ShotOriginScale, ShotOriginY = OriginOffset(i.ShotOriginY) / ShotOriginScale, ShotOriginZ = OriginOffset(i.ShotOriginZ) / ShotOriginScale,
        ViewFraction = ViewFractionByte(i.ViewFraction) / ViewFractionScale,
        BodyTick = BodyTickOf(i.Tick, BodyLag(i)),
    };

    /// <summary>Protocol 43: an input's body tick as the wire carries it, the ticks back from its own tick (1 to <see cref="PlayerCollision.MaxBodyLagTicks"/>; else 0, none).</summary>
    static byte BodyLag(PlayerInput i) => i.BodyTick > 0 && i.BodyTick < i.Tick && i.Tick - i.BodyTick <= PlayerCollision.MaxBodyLagTicks ? (byte)(i.Tick - i.BodyTick) : (byte)0;

    /// <summary>Protocol 43: the body tick of an input of tick <paramref name="tick"/> whose body lag is <paramref name="lag"/> (0 or out of range: none).</summary>
    static uint BodyTickOf(uint tick, byte lag) => lag is > 0 and <= PlayerCollision.MaxBodyLagTicks && lag < tick ? tick - lag : 0;

    /// <summary>Protocol 37: the view fraction travels as a byte in 1/256 steps (0..255/256).</summary>
    public const float ViewFractionScale = 256f;

    /// <summary>A view fraction as the wire carries it: 0..1 in 1/256 steps, truncated (anything not in [0, 1): 0).</summary>
    static byte ViewFractionByte(float f) => f is > 0f and < 1f ? (byte)Math.Clamp((int)(f * ViewFractionScale), 0, 255) : (byte)0;

    /// <summary>A shot-origin offset as the wire carries it: centimetres, clamped to ±327.67 m (non-finite: 0).</summary>
    static short OriginOffset(float metres) => float.IsFinite(metres) ? (short)Math.Clamp(MathF.Round(metres * ShotOriginScale), short.MinValue + 1, short.MaxValue) : (short)0;

    /// <summary>
    /// A control point as the wire carries it: owner and side outside None/SpaceMarines/Chaos read as None, an owned point's
    /// side is its owner, a point without a side has no progress; progress clamped to 0..1 (NaN: 0) in 1/255 steps.
    /// </summary>
    static (Team Owner, Team Side, byte Progress) Normalized(ControlPointView p)
    {
        var owner = p.Owner is Team.SpaceMarines or Team.Chaos ? p.Owner : Team.None;
        var side = owner != Team.None ? owner : p.Side is Team.SpaceMarines or Team.Chaos ? p.Side : Team.None;
        byte progress = side == Team.None || float.IsNaN(p.Progress) ? (byte)0 : (byte)MathF.Round(Math.Clamp(p.Progress, 0f, 1f) * 255f);
        return (owner, side, progress);
    }

    /// <summary>A control point after a trip through a snapshot.</summary>
    public static ControlPointView Quantize(ControlPointView p)
    {
        var (owner, side, progress) = Normalized(p);
        return new ControlPointView(owner, side, progress / 255f, p.Contested);
    }

    static bool Positive(float v) => float.IsFinite(v) && v > 0f;

    static Scoreboard? ReadScoreboard(ref Reader r)
    {
        uint tick = r.U32();
        int flags = r.U8();
        if (flags > 15) return null;   // unknown flags (protocol 46: bit 3, spectators)
        int limit = r.U16(); float time = r.F32();
        SeizeGroundInfo? seize = null;
        if ((flags & 4) != 0)
        {
            float capture = r.F32(), neutralise = r.F32(); int score = r.U16(); float interval = r.F32();
            int count = r.U8();
            if (count > MaxSnapshotPoints || !Positive(capture) || !Positive(neutralise) || !Positive(interval)) return null;
            var layout = new ControlPointInfo[count];
            for (int k = 0; k < count; k++)
            {
                int length = r.U8();
                if (length > MaxMapName) return null;
                var name = Encoding.ASCII.GetString(r.Bytes(length));
                var center = r.V3(); float radius = r.F32(), halfHeight = r.F32();
                if (!IsMapName(name) || !float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(center.Z) || !Positive(radius) || !Positive(halfHeight)) return null;
                layout[k] = new ControlPointInfo(name, center, radius, halfHeight);
            }
            seize = new SeizeGroundInfo(capture, neutralise, score, interval, layout);
        }
        int n = r.U8();
        if (n > MaxPlayers || !float.IsFinite(time) || time < 0f) return null;
        var rows = new ScoreEntry[n];
        for (int k = 0; k < n; k++)
        {
            int id = r.I32(), team = r.U8(), playerClass = r.U8(), kills = r.U16(), deaths = r.U16(); ushort appearance = r.U16();
            int equipment = r.U8();   // protocol 40
            int nameLength = r.U8();   // protocol 41: the display name
            if (nameLength > PlayerNames.MaxLength) return null;
            string name = PlayerNames.Sanitise(Encoding.Latin1.GetString(r.Bytes(nameLength)));
            if (team > (int)Team.Chaos || playerClass >= MaxClasses) return null;
            for (int j = 0; j < k; j++) if (rows[j].PlayerId == id) return null;   // one row per player
            rows[k] = new ScoreEntry(id, (Team)team, playerClass, kills, deaths, appearance, equipment == 255 ? -1 : equipment, name);
        }
        SpectatorEntry[]? spectators = null;
        if ((flags & 8) != 0)
        {
            int listed = r.U8();
            if (listed is 0 or > MaxSpectators) return null;
            spectators = new SpectatorEntry[listed];
            for (int k = 0; k < listed; k++)
            {
                int id = r.I32(), nameLength = r.U8();
                if (nameLength > PlayerNames.MaxLength) return null;
                spectators[k] = new SpectatorEntry(id, PlayerNames.Sanitise(Encoding.Latin1.GetString(r.Bytes(nameLength))));
            }
        }
        return new Scoreboard(tick, (flags & 1) != 0, limit, time, (flags & 2) != 0, rows, seize, spectators);
    }

    /// <summary>A position as a snapshot carries a projectile's: each axis rounded to 1/64 m and clamped to ±131 km (NaN: 0).</summary>
    public static Vector3 Quantize(Vector3 v) => new Vector3(Fixed(v.X), Fixed(v.Y), Fixed(v.Z)) / PositionScale;

    static int Fixed(float v) => float.IsNaN(v) ? 0 : (int)Math.Clamp(MathF.Round(v * PositionScale), -I24Max, I24Max);

    /// <summary>
    /// A player as a slim snapshot record carries it: id, position, velocity, yaw, pitch, grounded, alive, class, slot,
    /// the weapon in that slot (<see cref="PlayerState.Primary"/> or <see cref="PlayerState.Pistol"/>; the other one is
    /// -1), the melee choice, melee step and time (class and melee step one byte each; indices 0..254, else -1), and
    /// whether it is charging (protocol 7) and whether it is throwing (protocol 8), and its jump as a representative phase (protocol 9: Ascent for any jumping phase, Dive for
    /// the dive and recovery, None otherwise), and its team (protocol 10; the class clamped to 0..63), its evade (protocol 13:
    /// <see cref="PlayerState.EvadeTicks"/> up to <see cref="Evade.MaxSlimTicks"/>, the direction; Forward when not evading), and
    /// (protocol 17) the melee step up to <see cref="MaxMeleeStep"/> with its heavy flag, or the reaction (kind, direction, or (protocol 18) the charge's phase and ticks;
    /// ticks up to 255; a reaction clears the step); every other field is default.
    /// </summary>
    /// <summary>A perk index on the wire (protocol 23): 7 bits, <see cref="NoPerk"/> for none (and for an index the field cannot hold).</summary>
    public const int NoPerk = SimConfig.MaxPerks;
    /// <summary>Most rounds left of a burst the own record carries (2 bits).</summary>
    public const int MaxBurstLeft = WeaponSpec.MaxBurstLength - 1;

    /// <summary>The own record's perk word (protocol 23): perk 1 | perk 2 &lt;&lt; 7 | burst rounds left &lt;&lt; 14.</summary>
    static ushort PerkWord(PlayerState p)
    {
        static int Perk(int i) => i >= 0 && i < NoPerk ? i : NoPerk;
        return (ushort)(Perk(p.Perk1) | (Perk(p.Perk2) << 7) | (Math.Clamp(p.BurstLeft, 0, MaxBurstLeft) << 14));
    }

    static int FromPerk(int v) => v == NoPerk ? -1 : v;

    /// <summary>
    /// The own record's input buffer (protocol 33, feel/buffer-speed; <see cref="InputBuffer"/>): the action (3 bits) and its ticks (4 bits,
    /// at most <see cref="InputBuffer.MaxTicks"/>); none and 0 for an action out of range, and 0 ticks without an action.
    /// </summary>
    static (BufferedAction Action, int Ticks) BufferFields(PlayerState p) =>
        p.Buffered is > BufferedAction.None and <= BufferedAction.JumpLong ?(p.Buffered, Math.Clamp(p.BufferTicks, 0, InputBuffer.MaxTicks)) : (BufferedAction.None, 0);

    public static PlayerState Slim(PlayerState p)
    {
        var (meleeByte, meleeTime) = MeleeField(p);
        var melee = FromMeleeField(meleeByte, meleeTime)!.Value;
        return new(p.PlayerId, p.Position, p.Velocity, p.Yaw, p.Grounded)
        {
            Pitch = p.Pitch, Alive = p.Alive, Charging = p.Charging, Throwing = p.Throwing, Aiming = p.Aiming,
            Jump = JumpPack.Jumping(p.Jump) ? JumpPhase.Ascent : JumpPack.Pounding(p.Jump) ? JumpPhase.Dive : JumpPhase.None,
            Class = Math.Clamp(p.Class, 0, MaxClasses - 1), Slot = p.Slot, Team = p.Team is Team.SpaceMarines or Team.Chaos ? p.Team : Team.None,
            MeleeStep = melee.Step, MeleeTime = melee.Time, MeleeHeavy = melee.Heavy,
            Reaction = melee.Reaction, ReactionDirection = melee.Direction, ReactionTicks = melee.Ticks, Rush = melee.Rush, RushTicks = melee.RushTicks,
            SprintStop = melee.SprintStop,   // protocol 36
            Primary = p.Slot == WeaponSlot.Ranged ? FromIndex(Index(p.Primary)) : -1,
            Pistol = p.Slot == WeaponSlot.Pistol ? FromIndex(Index(p.Pistol)) : -1,
            MeleeChoice = FromIndex(Index(p.MeleeChoice)),
            EvadeTicks = Math.Clamp(p.EvadeTicks, 0, Evade.MaxSlimTicks), EvadeDirection = (EvadeDirection)((EvadeByte(p) >> 6) | (((SlotByte(p) >> 1) & 1) << 2)),
            EvadeLanded = (SlotByte(p) & 8) != 0,
            ReadyTicks = p.ReadyTicks > 0 || p.RaiseTicks > 0 ? 1 : 0,   // protocol 20: "weapon up" only
            Brace = (BracePhase)((SlotByte(p) >> 4) & 3), Venting = p.Venting,   // protocol 29: the phase and the vent (not the transition's ticks)
            JumpSlash = (SlotByte(p) & 128) != 0,   // protocol 30
        };
    }

    /// <summary>Largest melee step a record carries (6 bits in protocol 17, 5 since protocol 18).</summary>
    public const int MaxMeleeStep = SimConfig.MaxMeleeSteps;

    /// <summary>The fields a record's melee byte and time carry (<see cref="MeleeField"/>).</summary>
    readonly record struct MeleeFields(int Step, float Time, bool Heavy, ReactionKind Reaction, ReactionDirection Direction, int Ticks, RushPhase Rush, int RushTicks,
        bool SprintStop = false);

    /// <summary>
    /// The melee byte and time of a player record: during a reaction (protocol 17), bit 7 set, the kind (1..3) in bits 2..3, the
    /// clip direction in bits 0..1 and the reaction ticks (0..255, a whole number) as the time; during a charge (protocol 18),
    /// bit 5 set and the phase in bits 0..1 (1..2; protocol 30: bits 0..2, 1..6 with the dash attack's phases), its ticks as the time; otherwise the melee step (0..31) in bits 0..4, the
    /// heavy flag in bit 6 (only with a step) and the attack time. A reaction ends an attack and a charge, and a charge never
    /// overlaps an attack, so at most one is under way (a reaction wins, then a charge). Protocol 36 (feel/locks-sprint-stop): with no step, reaction or
    /// charge, the sprint stop (<see cref="PlayerState.SprintStop"/>, which none of them overlaps) is bit 6 with step 0 and time 0.
    /// </summary>
    static (byte Byte, float Time) MeleeField(PlayerState p)
    {
        if (p.Reaction is ReactionKind.Interrupt or ReactionKind.Stun or ReactionKind.Stagger)
            return ((byte)(128 | ((int)p.Reaction << 2) | ((int)p.ReactionDirection & 3)), Math.Clamp(p.ReactionTicks, 0, Reaction.MaxTicks));
        if (p.Rush is >= RushPhase.Rush and <= RushPhase.DashCollision)
            return ((byte)(32 | (int)p.Rush), Math.Clamp(p.RushTicks, 0, Rush.MaxTicks));   // protocol 30: the phase in bits 0..2
        int step = Math.Clamp(p.MeleeStep, 0, MaxMeleeStep);
        if (step == 0 && p.SprintStop) return (64, 0f);   // protocol 36: the sprint stop
        return ((byte)(step | (step > 0 && p.MeleeHeavy ? 64 : 0)), p.MeleeTime);
    }

    static bool WholeTicks(float time, int max) => time >= 0f && time <= max && time == MathF.Floor(time);

    /// <summary>The fields of a melee byte and time (<see cref="MeleeField"/>), or null when malformed.</summary>
    static MeleeFields? FromMeleeField(int b, float time)
    {
        if ((b & 128) != 0)
        {
            int kind = (b >> 2) & 3;
            if ((b & 0x70) != 0 || kind == 0 || !WholeTicks(time, Reaction.MaxTicks)) return null;
            return new MeleeFields(0, 0f, false, (ReactionKind)kind, (ReactionDirection)(b & 3), (int)time, RushPhase.None, 0);
        }
        if ((b & 32) != 0)
        {
            int phase = b & 7;   // protocol 30: bits 0..2 (1..6)
            if ((b & 0x58) != 0 || phase is 0 or > (int)RushPhase.DashCollision || !WholeTicks(time, Rush.MaxTicks)) return null;
            return new MeleeFields(0, 0f, false, ReactionKind.None, ReactionDirection.Front, 0, (RushPhase)phase, (int)time);
        }
        int step = b & 31;
        if ((b & 64) != 0 && step == 0)
            return time == 0f ? new MeleeFields(0, 0f, false, ReactionKind.None, ReactionDirection.Front, 0, RushPhase.None, 0, SprintStop: true) : null;   // protocol 36: the sprint stop (with time 0)
        return new MeleeFields(step, time, (b & 64) != 0, ReactionKind.None, ReactionDirection.Front, 0, RushPhase.None, 0);
    }

    /// <summary>A value as one byte, clamped to 0..255.</summary>
    static byte Byte(int v) => (byte)Math.Clamp(v, 0, byte.MaxValue);

    /// <summary>
    /// The slot byte of a player record: the slot in bit 0; since protocol 14 bit 1 is bit 2 of the evade direction (the
    /// diagonals, 4..7; 0 when not evading); since protocol 20 bit 2 says the weapon is up (M31: ready or raising); since protocol 24
    /// bit 3 says a jump-pack dash is in its landing part (M44: <see cref="PlayerState.EvadeLanded"/>; 0 when not evading); since protocol 29 bits 4..5
    /// the brace and bit 6 the vent; since protocol 30 bit 7 says a pound phase (dive, recovery) is the sword slash's (<see cref="PlayerState.JumpSlash"/>); since protocol 31
    /// also, in the take-off and on the lift-off tick, that a slash press is buffered (feel/jump-pack).
    /// </summary>
    static byte SlotByte(PlayerState p) =>
        (byte)(((int)p.Slot & 1) | (Math.Clamp(p.EvadeTicks, 0, Evade.MaxSlimTicks) > 0 && ((int)p.EvadeDirection & 4) != 0 ? 2 : 0)
            | (p.ReadyTicks > 0 || p.RaiseTicks > 0 ? 4 : 0) | (Math.Clamp(p.EvadeTicks, 0, Evade.MaxSlimTicks) > 0 && p.EvadeLanded ? 8 : 0)
            | (BraceOnWire(p) << 4) | (p.Venting ? 64 : 0) | ((JumpPack.Pounding(p.Jump) || p.Jump is JumpPhase.TakeOff or JumpPhase.Ascent) && p.JumpSlash ? 128 : 0));

    /// <summary>
    /// The brace phase a record carries (protocol 29; slot byte bits 4..5): a transition (entering, exiting) only with its ticks (1..<see cref="Brace.MaxTicks"/>),
    /// else as braced when exiting and none when entering (a state the simulation never leaves a tick in; kept readable).
    /// </summary>
    static int BraceOnWire(PlayerState p) => p.Brace switch
    {
        BracePhase.Braced => 2,
        BracePhase.Entering => p.BraceTicks > 0 ? 1 : 0,
        BracePhase.Exiting => p.BraceTicks > 0 ? 3 : 2,
        _ => 0,
    };

    /// <summary>
    /// The own record's Reload byte: Reload held (bit 1), Detonate held (bit 2); protocol 29: a brace transition's ticks (clamped to 1..<see cref="Brace.MaxTicks"/>)
    /// in bits 3..7 (low 5) and bit 0 (bit 5); 0 outside a transition.
    /// </summary>
    static byte ReloadByte(PlayerState p)
    {
        int ticks = BraceOnWire(p) is 1 or 3 ? Math.Clamp(p.BraceTicks, 1, Brace.MaxTicks) : 0;
        return (byte)((p.ReloadHeld ? 2 : 0) | (p.DetonateHeld ? 4 : 0) | ((ticks & 31) << 3) | ((ticks >> 5) & 1));
    }

    /// <summary>The evade byte of a player record: direction bits 0..1 in bits 6..7, ticks clamped to 0..63 below; 0 when not evading.</summary>
    static byte EvadeByte(PlayerState p)
    {
        int ticks = Math.Clamp(p.EvadeTicks, 0, Evade.MaxSlimTicks);
        return (byte)(ticks == 0 ? 0 : ticks | (((int)p.EvadeDirection & 3) << 6));
    }

    static ushort Ammo(int count) => (ushort)Math.Clamp(count, 0, ushort.MaxValue);
    static ushort Count(int count) => (ushort)Math.Clamp(count, 0, ushort.MaxValue);
    /// <summary>A player id in the kill feed: 0..65535 (the server allocates ids in that range); anything else is written as 0 (none).</summary>
    static ushort Id16(int id) => id is >= 0 and <= ushort.MaxValue ? (ushort)id : (ushort)0;
    /// <summary>The class (clamped to 0..63) in the low 6 bits, the team (<see cref="Team"/>; an unknown value is None) in the top 2.</summary>
    static byte ClassAndTeam(PlayerState p) =>
        (byte)(Math.Clamp(p.Class, 0, MaxClasses - 1) | ((p.Team is Team.SpaceMarines or Team.Chaos ? (int)p.Team : 0) << 6));
    /// <summary>An index as one byte: 0..254; anything else (none, or too large to send) is <see cref="NoIndex"/>.</summary>
    static byte Index(int index) => index is >= 0 and < NoIndex ? (byte)index : NoIndex;
    static int FromIndex(byte b) => b == NoIndex ? -1 : b;

    sealed class Writer
    {
        readonly List<byte> _b = new();
        public int Length => _b.Count;
        public void U8(byte v) => _b.Add(v);
        public void U16(ushort v) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(s, v); _b.AddRange(s.ToArray()); }
        public void I16(short v) => U16((ushort)v);
        public void U32(uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); _b.AddRange(s.ToArray()); }
        public void I32(int v) => U32((uint)v);
        public void U64(ulong v) { Span<byte> s = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(s, v); _b.AddRange(s.ToArray()); }
        public void F32(float v) => U32(BitConverter.SingleToUInt32Bits(v));
        public void V3(Vector3 v) { F32(v.X); F32(v.Y); F32(v.Z); }
        public void I24(int v) { _b.Add((byte)v); _b.Add((byte)(v >> 8)); _b.Add((byte)(v >> 16)); }
        public void Bytes(byte[] b) => _b.AddRange(b);
        public void Bytes(ReadOnlySpan<byte> b) { foreach (byte x in b) _b.Add(x); }
        public byte[] ToArray() => _b.ToArray();
    }

    ref struct Reader(ReadOnlySpan<byte> data)
    {
        readonly ReadOnlySpan<byte> _d = data;
        int _p;
        public bool AtEnd => _p == _d.Length;
        public byte U8() => _d[_p++];
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_d.Slice(_p, 2)); _p += 2; return v; }
        public short I16() => (short)U16();
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_d.Slice(_p, 4)); _p += 4; return v; }
        public int I32() => (int)U32();
        public ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(_d.Slice(_p, 8)); _p += 8; return v; }
        public float F32() => BitConverter.UInt32BitsToSingle(U32());
        public Vector3 V3() => new(F32(), F32(), F32());
        public int I24() { int v = _d[_p] | (_d[_p + 1] << 8) | (_d[_p + 2] << 16); _p += 3; return (v << 8) >> 8; }
        public ReadOnlySpan<byte> Bytes(int n) { var s = _d.Slice(_p, n); _p += n; return s; }
    }
}
