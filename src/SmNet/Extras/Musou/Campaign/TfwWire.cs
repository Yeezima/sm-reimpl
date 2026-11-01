using System.Buffers.Binary;
using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Extras.Musou.Campaign;

/// <summary>What a Guard event reports (tag 10): a shot (the vector is where the tracer ends), a man hurt, a man dead (the vector is his feet). The numbers are <c>GuardEventKind</c>'s.</summary>
public enum TfwGuardEventKind : byte { Shot = 1, Hurt = 2, Death = 3 }

/// <summary>One Guard event as it travels (tag 10): <see cref="Enemy"/> is the enemy a shot hit (0: a miss).</summary>
public readonly record struct TfwGuardEvent(TfwGuardEventKind Kind, uint Tick, ushort Guard, ushort Enemy, Vector3 Vector);

/// <summary>
/// Musou Mode on the wire (design section H; no protocol bump, Musou's config hash keeps other profiles out). Values are little-endian; positions are quantised to
/// 0..65535 over the director's <see cref="MusouBox"/> (tag 5), as Musou's. The server's sections are envelope tags (message kind 16), sent only under <c>World.Tfw</c>:
/// <list type="bullet">
/// <item>Tag 7, the war: one record of <see cref="WarBytes"/> bytes <c>[u8 phase][u8 battle lane][f32 clock][f32 time scale][u8 transport][u8 supplies][u16 command echo]</c> then per
/// lane <c>[i8 front][f32 progress][f32 imperial][f32 ork][u8 owned mask][f32 gather left][u8 lord state]</c>, then the own block (TF7b, <see cref="TfwOwnView"/>) <c>[u8 travel s][u8 pickup s][u8 revive s]
/// [u8 extraction s][u8 revive tenths][u8 flags: bit 0 battle won, bit 1 deployed][u8 retainer][u8 order][u16 recovery s][u8 guards][u8 squads][u8 reserves][u8 secured]</c>. On a change and once a second; per client
/// (the echo and the own block are the client's).</item>
/// <item>Tag 8, the objectives: <c>[u8 n]</c> then n records of 11 bytes <c>[u8 index][u8 state][u16 progress 0..1][u16 x][u16 y][u16 z][u8 radius in quarters of a metre]</c>, then the sites <c>[u8 m]{[u16 x][u16 y][u16 z]}</c> (the landing zone, then the sources), at most <see cref="MaxObjectives"/>
/// (180 bytes in all), the whole list every 10 ticks; n 0 outside a battle.</item>
/// <item>Tag 9, the Guardsmen: <c>[u8 n]</c> then n records of 10 bytes <c>[u16 id][u16 x][u16 y][u16 z][u8 yaw][u8 state]</c>, at most <see cref="MaxGuards"/>, by interest
/// (<see cref="TfwGuardInterest"/>).</item>
/// <item>Tag 10, the Guard events: <c>[u8 n]</c> then n records of 15 bytes <c>[u8 kind][u32 tick][u16 guard][u16 enemy][u16 x][u16 y][u16 z]</c>, at most <see cref="MaxGuardEvents"/>; a client
/// plays each (kind, tick, guard, enemy) once.</item>
/// <item>Tag 11, fire support (pass 2): <c>[u8 n]</c> then n records of 19 bytes <c>[u8 kind][u32 tick][u16 subject][i16 x][i16 y][i16 z][i16 to x][i16 to y][i16 to z]</c>, points in decimetres as a command's
/// (<see cref="PointStep"/>; an aircraft flies above the box, so no box here), at most <see cref="MaxFire"/>; a client plays each (kind, tick, subject) once.</item>
/// <item>Tag 12, the score (per client): <c>[u8 parts]</c> (bit 0 own, 1 others, 2 history) then each part present, in that order: own <c>[u32 map points][u32 run total][u16 KOs][u16 best chain]</c>; others <c>[u8 n]</c> then
/// n of <c>[u16 player][u32 run total]</c>, humans only, at most <see cref="MaxScorePlayers"/>; history <c>[u8 n]</c> then n of <c>[u8 lane][u8 node][u8 result][u16 KOs][u16 best chain][u32 points]</c>, at most <see cref="MaxScoreMaps"/>.
/// The parts bit is ours: the three go at different rates.</item>
/// <item>Tag 13, elite bars: <c>[u8 n]</c> then n records of 3 bytes <c>[u16 id][u8 health]</c>, at most <see cref="MaxBars"/>.</item>
/// <item>Tag 14, a reward offer (per client): <see cref="RewardBytes"/> bytes <c>[u16 sequence]</c> then 3 cards <c>[u8 kind][u16 item]</c>; repeated every second while it stands.</item>
/// <item>Tag 15, the extra slots (per client; M6): <see cref="ArsenalBytes"/> bytes <c>[i16 key 3][i16 key 4][i16 key 5][u8 key in the primary's field][u8 key in the pistol's field]</c>; weapon indices, -1 empty; keys 0: the player's own weapon.</item>
/// </list>
/// Tags 11 to 15 need no box.
/// A client's commands are message kind 17 under <see cref="CommandTag"/>: <c>[u16 sequence][u8 command][u8 argument]</c>, and for an order with a place (<see cref="HasPoint"/>: Hold and
/// Attack) then <c>[i16 x][i16 y][i16 z]</c> in decimetres (<see cref="PointStep"/>), for ChooseReward then <c>[u16 offer]</c> (M6: a delayed pick cannot land on a newer offer): the point of the client's crosshair, which the server cannot work out itself (it has no camera), in a form that
/// needs no box (a client may order before its first director) and cannot carry a NaN or an infinity. Resent every 250 ms until tag 7 echoes the sequence (<see cref="TfwCommandSender"/>).
/// A faithful or extended server never sends 7 to 15 and drops 17.
/// </summary>
public static class TfwWire
{
    /// <summary>The envelope tags (gore 1, loyalist barks 2, Musou 3 to 6).</summary>
    public const byte WarTag = 7, ObjectivesTag = 8, GuardsTag = 9, GuardEventsTag = 10, FireTag = 11, ScoreTag = 12, BarsTag = 13, RewardTag = 14, ArsenalTag = 15;
    /// <summary>The tag of the war's commands in message kind 17 (another namespace than the envelope's: client to server).</summary>
    public const byte CommandTag = 7;
    /// <summary>Bytes of a command's payload, and of one with a place (at most <see cref="Protocol.MaxExtrasCommandPayload"/>).</summary>
    public const int CommandBytes = 4, PointCommandBytes = CommandBytes + 6, OfferCommandBytes = CommandBytes + 2;
    /// <summary>The metres of one step of a command's point: 0.1 m, so a point reaches 3276.7 m from the origin on each axis.</summary>
    public const float PointStep = 0.1f;

    const int WarHeadBytes = 14, LaneBytes = 19, OwnBytes = 14, OwnWon = 1, OwnDeployed = 2;
    /// <summary>The fullest revive progress on the wire (<see cref="TfwOwnView.ReviveTenths"/>).</summary>
    public const byte MaxReviveTenths = 10;
    public const int WarBytes = WarHeadBytes + 3 * LaneBytes + OwnBytes, ObjectiveBytes = 11, SiteBytes = 6, GuardBytes = 10, GuardEventBytes = 15;
    /// <summary>The objectives' section is at most this long (design section H).</summary>
    public const int MaxObjectivesBytes = 180;
    /// <summary>Most objectives in one section: what the section leaves after its two count bytes and <see cref="MaxSites"/> sites, in records.</summary>
    public const int MaxObjectives = (MaxObjectivesBytes - 2 - MaxSites * SiteBytes) / ObjectiveBytes;
    /// <summary>Sites in tag 8: the landing zone, the three points, the HQ's source: where the Orks come from.</summary>
    public const int MaxSites = 5;
    /// <summary>A radius travels in quarters of a metre.</summary>
    const float RadiusStep = 0.25f;
    /// <summary>Most Guardsmen in one section: (1191 - 1) / 10 = 119.</summary>
    public const int MaxGuards = (Protocol.MaxExtrasPayloadBytes - 1) / GuardBytes;
    /// <summary>Most Guard events in one section: (1191 - 1) / 15 = 79.</summary>
    public const int MaxGuardEvents = (Protocol.MaxExtrasPayloadBytes - 1) / GuardEventBytes;
    /// <summary>The highest Guardsman id (design section E's pool: ids are slot + 1).</summary>
    public const int GuardCapacity = 256;

    public const int FireBytes = 19, ScoreOwnBytes = 12, ScorePlayerBytes = 6, ScoreMapBytes = 11, BarBytes = 3, RewardCards = 3, RewardCardBytes = 3, RewardBytes = 2 + RewardCards * RewardCardBytes, ArsenalBytes = 8;
    /// <summary>Most fire records in one section: (1191 - 1) / 19 = 62.</summary>
    public const int MaxFire = (Protocol.MaxExtrasPayloadBytes - 1) / FireBytes;
    /// <summary>Most elite bars in one section (the count's byte; 766 bytes at most).</summary>
    public const int MaxBars = byte.MaxValue;
    /// <summary>Most other humans in one score section, and most maps in its history (the addendum's 16).</summary>
    public const int MaxScorePlayers = 16, MaxScoreMaps = 16;
    const int ScoreHasOwn = 1, ScoreHasOthers = 2, ScoreHasHistory = 4;

    /// <summary>Whether an envelope tag is the war's (7 to 15).</summary>
    public static bool Owns(byte tag) => tag is >= WarTag and <= ArsenalTag;

    /// <summary>Whether sequence <paramref name="a"/> is after <paramref name="b"/>, across the 16-bit wrap.</summary>
    public static bool IsNewer(ushort a, ushort b) => (short)(a - b) > 0;

    // ---------------------------------------------------------------- commands (message kind 17)

    /// <summary>Whether a command names a place (the payload is <see cref="PointCommandBytes"/> long).</summary>
    public static bool HasPoint(TfwCommand command) => command is TfwCommand.Hold or TfwCommand.Attack;

    /// <summary>The length of a command's payload.</summary>
    static int CommandLength(TfwCommand command) => HasPoint(command) ? PointCommandBytes : command == TfwCommand.ChooseReward ? OfferCommandBytes : CommandBytes;

    public static byte[] WriteCommand(TfwCommandRecord c)
    {
        var bytes = new byte[CommandLength(c.Command)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, c.Sequence);
        bytes[2] = (byte)c.Command;
        bytes[3] = c.Argument;
        if (c.Command == TfwCommand.ChooseReward) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), c.Offer);
        if (HasPoint(c.Command))
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4), Decimetres(c.Point.X));
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(6), Decimetres(c.Point.Y));
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8), Decimetres(c.Point.Z));
        }
        return bytes;
    }

    /// <summary>The command of a payload; null for a malformed one (a length that is not the command's, an unknown command, a Deploy to an unknown lane, a card that does not exist, a pick of offer 0, a weapon key that does not exist).</summary>
    public static TfwCommandRecord? ReadCommand(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < CommandBytes) return null;
        var command = (TfwCommand)payload[2];
        if (!Enum.IsDefined(command) || payload.Length != CommandLength(command)) return null;
        if (command == TfwCommand.Deploy && !Enum.IsDefined((TfwLane)payload[3])) return null;
        ushort offer = command == TfwCommand.ChooseReward ? BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]) : (ushort)0;
        if (command == TfwCommand.ChooseReward && (payload[3] >= RewardCards || offer == 0)) return null;
        if (command == TfwCommand.SelectSlot && payload[3] is < 1 or > 5) return null;
        var point = HasPoint(command)
            ? new Vector3(BinaryPrimitives.ReadInt16LittleEndian(payload[4..]), BinaryPrimitives.ReadInt16LittleEndian(payload[6..]), BinaryPrimitives.ReadInt16LittleEndian(payload[8..])) * PointStep
            : default;
        return new TfwCommandRecord(BinaryPrimitives.ReadUInt16LittleEndian(payload), command, payload[3], point, offer);
    }

    static short Decimetres(float metres) =>
        float.IsFinite(metres) ? (short)MathF.Round(Math.Clamp(metres / PointStep, short.MinValue, short.MaxValue)) : (short)0;

    // ---------------------------------------------------------------- quantisation (as MusouWire's)

    static ushort Quantise(float v, float min, float max)
    {
        float span = max - min;
        if (!(span > 0f) || !float.IsFinite(v)) return 0;
        return (ushort)MathF.Round(Math.Clamp((v - min) / span, 0f, 1f) * ushort.MaxValue);
    }

    static float Dequantise(ushort q, float min, float max) => min + q / (float)ushort.MaxValue * (max - min);

    static void WritePosition(Span<byte> s, Vector3 p, MusouBox box)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(s, Quantise(p.X, box.Min.X, box.Max.X));
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], Quantise(p.Y, box.Min.Y, box.Max.Y));
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], Quantise(p.Z, box.Min.Z, box.Max.Z));
    }

    static Vector3 ReadPosition(ReadOnlySpan<byte> s, MusouBox box) => new(
        Dequantise(BinaryPrimitives.ReadUInt16LittleEndian(s), box.Min.X, box.Max.X),
        Dequantise(BinaryPrimitives.ReadUInt16LittleEndian(s[2..]), box.Min.Y, box.Max.Y),
        Dequantise(BinaryPrimitives.ReadUInt16LittleEndian(s[4..]), box.Min.Z, box.Max.Z));

    static float Finite(float v) => float.IsFinite(v) ? v : 0f;

    // ---------------------------------------------------------------- tag 7: the war

    public static byte[] WriteWar(in TfwWarView war)
    {
        var bytes = new byte[WarBytes];
        var s = bytes.AsSpan();
        s[0] = (byte)war.Phase;
        s[1] = (byte)war.BattleLane;
        BinaryPrimitives.WriteSingleLittleEndian(s[2..], Finite(war.Clock));
        BinaryPrimitives.WriteSingleLittleEndian(s[6..], Finite(war.TimeScale));
        s[10] = war.Transport;
        s[11] = war.Supplies;
        BinaryPrimitives.WriteUInt16LittleEndian(s[12..], war.CommandEcho);
        WriteLane(s[WarHeadBytes..], war.A);
        WriteLane(s[(WarHeadBytes + LaneBytes)..], war.B);
        WriteLane(s[(WarHeadBytes + 2 * LaneBytes)..], war.C);
        WriteOwn(s[(WarHeadBytes + 3 * LaneBytes)..], war.Own);
        return bytes;
    }

    static void WriteOwn(Span<byte> s, in TfwOwnView own)
    {
        s[0] = own.TravelSeconds;
        s[1] = own.PickupSeconds;
        s[2] = own.ReviveSeconds;
        s[3] = own.ExtractionSeconds;
        s[4] = own.ReviveTenths;
        s[5] = (byte)((own.BattleWon ? OwnWon : 0) | (own.Deployed ? OwnDeployed : 0));
        s[6] = (byte)own.Retainer;
        s[7] = (byte)own.Order;
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], own.RecoverySeconds);
        s[10] = own.Guards;
        s[11] = own.Squads;
        s[12] = own.Reserves;
        s[13] = own.Secured;
    }

    static TfwOwnView? ReadOwn(ReadOnlySpan<byte> s)
    {
        var retainer = (TfwRetainerState)s[6];
        var order = (MarineOrder)s[7];
        if (s[4] > MaxReviveTenths || (s[5] & ~(OwnWon | OwnDeployed)) != 0 || !Enum.IsDefined(retainer) || !Enum.IsDefined(order) || s[13] > WarRules.Positions) return null;
        return new TfwOwnView(s[0], s[1], s[2], s[3], s[4], (s[5] & OwnWon) != 0, retainer, order, BinaryPrimitives.ReadUInt16LittleEndian(s[8..]), s[10], s[11], s[12], s[13], (s[5] & OwnDeployed) != 0);
    }

    static void WriteLane(Span<byte> s, in TfwLaneState lane)
    {
        s[0] = (byte)lane.Front;
        BinaryPrimitives.WriteSingleLittleEndian(s[1..], Finite(lane.Progress));
        BinaryPrimitives.WriteSingleLittleEndian(s[5..], Finite(lane.Imperial));
        BinaryPrimitives.WriteSingleLittleEndian(s[9..], Finite(lane.Ork));
        s[13] = lane.OwnedMask;
        BinaryPrimitives.WriteSingleLittleEndian(s[14..], Finite(lane.GatherLeft));
        s[18] = lane.LordState;
    }

    /// <summary>The war of a payload; null for a malformed one (wrong length, an unknown phase or lane, a front out of range, a non-finite number, an unknown mask or lord state, an own block out of range).</summary>
    public static TfwWarView? ReadWar(ReadOnlySpan<byte> s)
    {
        if (s.Length != WarBytes) return null;
        var phase = (TfwRunPhase)s[0];
        var lane = (TfwLane)s[1];
        if (!Enum.IsDefined(phase) || !Enum.IsDefined(lane)) return null;
        float clock = BinaryPrimitives.ReadSingleLittleEndian(s[2..]), scale = BinaryPrimitives.ReadSingleLittleEndian(s[6..]);
        if (!float.IsFinite(clock) || !float.IsFinite(scale)) return null;
        if (ReadLane(s[WarHeadBytes..]) is not { } a || ReadLane(s[(WarHeadBytes + LaneBytes)..]) is not { } b || ReadLane(s[(WarHeadBytes + 2 * LaneBytes)..]) is not { } c) return null;
        if (ReadOwn(s[(WarHeadBytes + 3 * LaneBytes)..]) is not { } own) return null;
        return new TfwWarView(phase, lane, clock, scale, s[10], s[11], BinaryPrimitives.ReadUInt16LittleEndian(s[12..]), a, b, c, own);
    }

    static TfwLaneState? ReadLane(ReadOnlySpan<byte> s)
    {
        var front = (sbyte)s[0];
        float progress = BinaryPrimitives.ReadSingleLittleEndian(s[1..]), imperial = BinaryPrimitives.ReadSingleLittleEndian(s[5..]),
            ork = BinaryPrimitives.ReadSingleLittleEndian(s[9..]), gather = BinaryPrimitives.ReadSingleLittleEndian(s[14..]);
        if (front is < WarRules.FrontBesieged or > WarRules.FrontStronghold || s[13] >= 1 << WarRules.Positions || s[18] > WarRules.LordWounded) return null;
        if (!float.IsFinite(progress) || !float.IsFinite(imperial) || !float.IsFinite(ork) || !float.IsFinite(gather)) return null;
        return new TfwLaneState(front, progress, imperial, ork, s[13], gather, s[18]);
    }

    // ---------------------------------------------------------------- tag 8: the objectives

    /// <summary>The objectives payload of <paramref name="objectives"/> (the first <see cref="MaxObjectives"/>).</summary>
    public static byte[] WriteObjectives(IReadOnlyList<TfwObjective> objectives, MusouBox box, IReadOnlyList<Vector3>? sites = null)
    {
        int n = Math.Min(objectives.Count, MaxObjectives);
        int m = Math.Min(sites?.Count ?? 0, MaxSites);
        var bytes = new byte[2 + n * ObjectiveBytes + m * SiteBytes];
        bytes[0] = (byte)n;
        int after = 1 + n * ObjectiveBytes;
        bytes[after] = (byte)m;
        for (int i = 0; i < m; i++) WritePosition(bytes.AsSpan(after + 1 + i * SiteBytes, SiteBytes), sites![i], box);
        for (int i = 0; i < n; i++)
        {
            var o = objectives[i];
            var s = bytes.AsSpan(1 + i * ObjectiveBytes, ObjectiveBytes);
            s[0] = o.Index;
            s[1] = (byte)o.State;
            BinaryPrimitives.WriteUInt16LittleEndian(s[2..], Quantise(o.Progress, 0f, 1f));
            WritePosition(s[4..], o.Position, box);
            s[10] = (byte)Math.Clamp((int)MathF.Round(o.Radius / RadiusStep), 0, byte.MaxValue);
        }
        return bytes;
    }

    /// <summary>The sites of a payload <see cref="ReadObjectives"/> took: the landing zone, then the sources (none outside a battle).</summary>
    public static Vector3[] ReadSites(ReadOnlySpan<byte> payload, MusouBox box)
    {
        int after = 1 + payload[0] * ObjectiveBytes, m = payload[after];
        var sites = new Vector3[m];
        for (int i = 0; i < m; i++) sites[i] = ReadPosition(payload.Slice(after + 1 + i * SiteBytes, SiteBytes), box);
        return sites;
    }

    /// <summary>The objectives of a payload; null for a malformed one (wrong length, too many sites, an index twice, an unknown state).</summary>
    public static TfwObjective[]? ReadObjectives(ReadOnlySpan<byte> payload, MusouBox box)
    {
        if (payload.Length < 2) return null;
        int n = payload[0];
        if (n > MaxObjectives || payload.Length < 2 + n * ObjectiveBytes) return null;
        int m = payload[1 + n * ObjectiveBytes];
        if (m > MaxSites || payload.Length != 2 + n * ObjectiveBytes + m * SiteBytes) return null;
        var list = new TfwObjective[n];
        for (int i = 0; i < n; i++)
        {
            var s = payload.Slice(1 + i * ObjectiveBytes, ObjectiveBytes);
            var state = (TfwPositionState)s[1];
            if (!Enum.IsDefined(state)) return null;
            for (int k = 0; k < i; k++) if (list[k].Index == s[0]) return null;
            list[i] = new TfwObjective(s[0], state, Dequantise(BinaryPrimitives.ReadUInt16LittleEndian(s[2..]), 0f, 1f), ReadPosition(s[4..], box), s[10] * RadiusStep);
        }
        return list;
    }

    // ---------------------------------------------------------------- tag 9: the Guardsmen

    /// <summary>The Guardsmen payload of <paramref name="guards"/> (the first <see cref="MaxGuards"/>; split larger sets over several sections).</summary>
    public static byte[] WriteGuards(IReadOnlyList<TfwGuard> guards, MusouBox box)
    {
        int n = Math.Min(guards.Count, MaxGuards);
        var bytes = new byte[1 + n * GuardBytes];
        bytes[0] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            var g = guards[i];
            var s = bytes.AsSpan(1 + i * GuardBytes, GuardBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(s, g.Id);
            WritePosition(s[2..], g.Position, box);
            s[8] = MusouWire.QuantiseYaw(g.Yaw);
            s[9] = g.State;
        }
        return bytes;
    }

    /// <summary>The Guardsmen of a payload; null for a malformed one (wrong length, id 0 or above the pool, an id twice).</summary>
    public static TfwGuard[]? ReadGuards(ReadOnlySpan<byte> payload, MusouBox box)
    {
        if (payload.Length < 1) return null;
        int n = payload[0];
        if (n > MaxGuards || payload.Length != 1 + n * GuardBytes) return null;
        var list = new TfwGuard[n];
        Span<ulong> seen = stackalloc ulong[GuardCapacity / 64];
        seen.Clear();
        for (int i = 0; i < n; i++)
        {
            var s = payload.Slice(1 + i * GuardBytes, GuardBytes);
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(s);
            if (id == 0 || id > GuardCapacity) return null;
            int bit = id - 1;
            if ((seen[bit >> 6] & (1UL << (bit & 63))) != 0) return null;
            seen[bit >> 6] |= 1UL << (bit & 63);
            list[i] = new TfwGuard(id, ReadPosition(s[2..], box), MusouWire.DequantiseYaw(s[8]), s[9]);
        }
        return list;
    }

    // ---------------------------------------------------------------- tag 10: the Guard events

    /// <summary>The events payload of <paramref name="events"/> (the first <see cref="MaxGuardEvents"/>).</summary>
    public static byte[] WriteGuardEvents(IReadOnlyList<TfwGuardEvent> events, MusouBox box)
    {
        int n = Math.Min(events.Count, MaxGuardEvents);
        var bytes = new byte[1 + n * GuardEventBytes];
        bytes[0] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            var e = events[i];
            var s = bytes.AsSpan(1 + i * GuardEventBytes, GuardEventBytes);
            s[0] = (byte)e.Kind;
            BinaryPrimitives.WriteUInt32LittleEndian(s[1..], e.Tick);
            BinaryPrimitives.WriteUInt16LittleEndian(s[5..], e.Guard);
            BinaryPrimitives.WriteUInt16LittleEndian(s[7..], e.Enemy);
            WritePosition(s[9..], e.Vector, box);
        }
        return bytes;
    }

    /// <summary>The events of a payload; null for a malformed one (wrong length, an unknown kind).</summary>
    public static TfwGuardEvent[]? ReadGuardEvents(ReadOnlySpan<byte> payload, MusouBox box)
    {
        if (payload.Length < 1) return null;
        int n = payload[0];
        if (n > MaxGuardEvents || payload.Length != 1 + n * GuardEventBytes) return null;
        var list = new TfwGuardEvent[n];
        for (int i = 0; i < n; i++)
        {
            var s = payload.Slice(1 + i * GuardEventBytes, GuardEventBytes);
            var kind = (TfwGuardEventKind)s[0];
            if (!Enum.IsDefined(kind)) return null;
            list[i] = new TfwGuardEvent(kind, BinaryPrimitives.ReadUInt32LittleEndian(s[1..]), BinaryPrimitives.ReadUInt16LittleEndian(s[5..]),
                BinaryPrimitives.ReadUInt16LittleEndian(s[7..]), ReadPosition(s[9..], box));
        }
        return list;
    }

    // ---------------------------------------------------------------- tag 11: fire support

    static void WritePoint(Span<byte> s, Vector3 p)
    {
        BinaryPrimitives.WriteInt16LittleEndian(s, Decimetres(p.X));
        BinaryPrimitives.WriteInt16LittleEndian(s[2..], Decimetres(p.Y));
        BinaryPrimitives.WriteInt16LittleEndian(s[4..], Decimetres(p.Z));
    }

    static Vector3 ReadPoint(ReadOnlySpan<byte> s) =>
        new Vector3(BinaryPrimitives.ReadInt16LittleEndian(s), BinaryPrimitives.ReadInt16LittleEndian(s[2..]), BinaryPrimitives.ReadInt16LittleEndian(s[4..])) * PointStep;

    /// <summary>The fire payload of <paramref name="fire"/> (the first <see cref="MaxFire"/>).</summary>
    public static byte[] WriteFire(IReadOnlyList<TfwFireEvent> fire)
    {
        int n = Math.Min(fire.Count, MaxFire);
        var bytes = new byte[1 + n * FireBytes];
        bytes[0] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            var f = fire[i];
            var s = bytes.AsSpan(1 + i * FireBytes, FireBytes);
            s[0] = (byte)f.Kind;
            BinaryPrimitives.WriteUInt32LittleEndian(s[1..], f.Tick);
            BinaryPrimitives.WriteUInt16LittleEndian(s[5..], f.Subject);
            WritePoint(s[7..], f.At);
            WritePoint(s[13..], f.To);
        }
        return bytes;
    }

    /// <summary>The fire records of a payload; null for a malformed one (wrong length, an unknown kind).</summary>
    public static TfwFireEvent[]? ReadFire(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1) return null;
        int n = payload[0];
        if (n > MaxFire || payload.Length != 1 + n * FireBytes) return null;
        var list = new TfwFireEvent[n];
        for (int i = 0; i < n; i++)
        {
            var s = payload.Slice(1 + i * FireBytes, FireBytes);
            var kind = (TfwFireKind)s[0];
            if (!Enum.IsDefined(kind)) return null;
            list[i] = new TfwFireEvent(kind, BinaryPrimitives.ReadUInt32LittleEndian(s[1..]), BinaryPrimitives.ReadUInt16LittleEndian(s[5..]), ReadPoint(s[7..]), ReadPoint(s[13..]));
        }
        return list;
    }

    // ---------------------------------------------------------------- tag 12: the score

    /// <summary>The score payload of <paramref name="score"/> (the first <see cref="MaxScorePlayers"/> others and <see cref="MaxScoreMaps"/> maps).</summary>
    public static byte[] WriteScore(in TfwScoreUpdate score)
    {
        int others = Math.Min(score.Others?.Count ?? 0, MaxScorePlayers), maps = Math.Min(score.History?.Count ?? 0, MaxScoreMaps);
        var bytes = new byte[1 + (score.Own is null ? 0 : ScoreOwnBytes) + (score.Others is null ? 0 : 1 + others * ScorePlayerBytes) + (score.History is null ? 0 : 1 + maps * ScoreMapBytes)];
        var s = bytes.AsSpan();
        s[0] = (byte)((score.Own is null ? 0 : ScoreHasOwn) | (score.Others is null ? 0 : ScoreHasOthers) | (score.History is null ? 0 : ScoreHasHistory));
        int at = 1;
        if (score.Own is { } own)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[at..], own.MapPoints);
            BinaryPrimitives.WriteUInt32LittleEndian(s[(at + 4)..], own.RunTotal);
            BinaryPrimitives.WriteUInt16LittleEndian(s[(at + 8)..], own.Kos);
            BinaryPrimitives.WriteUInt16LittleEndian(s[(at + 10)..], own.BestChain);
            at += ScoreOwnBytes;
        }
        if (score.Others is { } players)
        {
            s[at++] = (byte)others;
            for (int i = 0; i < others; i++, at += ScorePlayerBytes)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(s[at..], players[i].Player);
                BinaryPrimitives.WriteUInt32LittleEndian(s[(at + 2)..], players[i].RunTotal);
            }
        }
        if (score.History is { } history)
        {
            s[at++] = (byte)maps;
            for (int i = 0; i < maps; i++, at += ScoreMapBytes)
            {
                var m = history[i];
                s[at] = (byte)m.Lane;
                s[at + 1] = m.Node;
                s[at + 2] = (byte)m.Result;
                BinaryPrimitives.WriteUInt16LittleEndian(s[(at + 3)..], m.Kos);
                BinaryPrimitives.WriteUInt16LittleEndian(s[(at + 5)..], m.BestChain);
                BinaryPrimitives.WriteUInt32LittleEndian(s[(at + 7)..], m.Points);
            }
        }
        return bytes;
    }

    /// <summary>The score of a payload; null for a malformed one (no part or an unknown part, a wrong length, too many records, player 0, an AI Marine or a player twice, an unknown lane or result).</summary>
    public static TfwScoreUpdate? ReadScore(ReadOnlySpan<byte> s)
    {
        if (s.Length < 1) return null;
        int parts = s[0];
        if (parts == 0 || (parts & ~(ScoreHasOwn | ScoreHasOthers | ScoreHasHistory)) != 0) return null;
        int at = 1;
        TfwScoreOwn? own = null;
        TfwScorePlayer[]? others = null;
        TfwMapScore[]? history = null;
        if ((parts & ScoreHasOwn) != 0)
        {
            if (s.Length < at + ScoreOwnBytes) return null;
            own = new TfwScoreOwn(BinaryPrimitives.ReadUInt32LittleEndian(s[at..]), BinaryPrimitives.ReadUInt32LittleEndian(s[(at + 4)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(s[(at + 8)..]), BinaryPrimitives.ReadUInt16LittleEndian(s[(at + 10)..]));
            at += ScoreOwnBytes;
        }
        if ((parts & ScoreHasOthers) != 0)
        {
            if (s.Length < at + 1) return null;
            int n = s[at++];
            if (n > MaxScorePlayers || s.Length < at + n * ScorePlayerBytes) return null;
            others = new TfwScorePlayer[n];
            for (int i = 0; i < n; i++, at += ScorePlayerBytes)
            {
                ushort player = BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);
                if (player == 0 || TfwContracts.IsPuppet(player)) return null;
                for (int k = 0; k < i; k++) if (others[k].Player == player) return null;
                others[i] = new TfwScorePlayer(player, BinaryPrimitives.ReadUInt32LittleEndian(s[(at + 2)..]));
            }
        }
        if ((parts & ScoreHasHistory) != 0)
        {
            if (s.Length < at + 1) return null;
            int n = s[at++];
            if (n > MaxScoreMaps || s.Length < at + n * ScoreMapBytes) return null;
            history = new TfwMapScore[n];
            for (int i = 0; i < n; i++, at += ScoreMapBytes)
            {
                var lane = (TfwLane)s[at];
                var result = (TfwMapResult)s[at + 2];
                if (!Enum.IsDefined(lane) || !Enum.IsDefined(result)) return null;
                history[i] = new TfwMapScore(lane, s[at + 1], result, BinaryPrimitives.ReadUInt16LittleEndian(s[(at + 3)..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(s[(at + 5)..]), BinaryPrimitives.ReadUInt32LittleEndian(s[(at + 7)..]));
            }
        }
        return at == s.Length ? new TfwScoreUpdate(own, others, history) : null;
    }

    // ---------------------------------------------------------------- tag 13: elite bars

    /// <summary>The bars payload of <paramref name="bars"/> (the first <see cref="MaxBars"/>).</summary>
    public static byte[] WriteBars(IReadOnlyList<TfwBar> bars)
    {
        int n = Math.Min(bars.Count, MaxBars);
        var bytes = new byte[1 + n * BarBytes];
        bytes[0] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            var s = bytes.AsSpan(1 + i * BarBytes, BarBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(s, bars[i].Enemy);
            s[2] = bars[i].Health;
        }
        return bytes;
    }

    /// <summary>The bars of a payload; null for a malformed one (wrong length, id 0 or above the pool).</summary>
    public static TfwBar[]? ReadBars(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1) return null;
        int n = payload[0];
        if (payload.Length != 1 + n * BarBytes) return null;
        var list = new TfwBar[n];
        for (int i = 0; i < n; i++)
        {
            var s = payload.Slice(1 + i * BarBytes, BarBytes);
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(s);
            if (id == 0 || id > MusouSim.Capacity) return null;
            list[i] = new TfwBar(id, s[2]);
        }
        return list;
    }

    // ---------------------------------------------------------------- tag 14: a reward offer

    static void WriteCard(Span<byte> s, TfwRewardCard card)
    {
        s[0] = (byte)card.Kind;
        BinaryPrimitives.WriteUInt16LittleEndian(s[1..], card.Item);
    }

    static TfwRewardCard? ReadCard(ReadOnlySpan<byte> s)
    {
        var kind = (TfwRewardKind)s[0];
        return Enum.IsDefined(kind) ? new TfwRewardCard(kind, BinaryPrimitives.ReadUInt16LittleEndian(s[1..])) : null;
    }

    public static byte[] WriteReward(in TfwRewardOffer offer)
    {
        var bytes = new byte[RewardBytes];
        var s = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s, offer.Sequence);
        WriteCard(s[2..], offer.First);
        WriteCard(s[(2 + RewardCardBytes)..], offer.Second);
        WriteCard(s[(2 + 2 * RewardCardBytes)..], offer.Third);
        return bytes;
    }

    /// <summary>The offer of a payload; null for a malformed one (wrong length, sequence 0, an unknown kind).</summary>
    public static TfwRewardOffer? ReadReward(ReadOnlySpan<byte> s)
    {
        if (s.Length != RewardBytes) return null;
        ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(s);
        if (sequence == 0 || ReadCard(s[2..]) is not { } first || ReadCard(s[(2 + RewardCardBytes)..]) is not { } second || ReadCard(s[(2 + 2 * RewardCardBytes)..]) is not { } third) return null;
        return new TfwRewardOffer(sequence, first, second, third);
    }

    // ---------------------------------------------------------------- tag 15: the extra slots

    public static byte[] WriteArsenal(in TfwArsenalView a)
    {
        var bytes = new byte[ArsenalBytes];
        var s = bytes.AsSpan();
        BinaryPrimitives.WriteInt16LittleEndian(s, a.Slot3);
        BinaryPrimitives.WriteInt16LittleEndian(s[2..], a.Slot4);
        BinaryPrimitives.WriteInt16LittleEndian(s[4..], a.Slot5);
        s[6] = a.LoadedPrimary;
        s[7] = a.LoadedPistol;
        return bytes;
    }

    /// <summary>The slots of a payload; null for a malformed one (wrong length, a key that is not 0 or 3 to 5).</summary>
    public static TfwArsenalView? ReadArsenal(ReadOnlySpan<byte> s)
    {
        if (s.Length != ArsenalBytes || s[6] is not (0 or >= 3 and <= 5) || s[7] is not (0 or >= 3 and <= 5)) return null;
        return new TfwArsenalView(BinaryPrimitives.ReadInt16LittleEndian(s), BinaryPrimitives.ReadInt16LittleEndian(s[2..]), BinaryPrimitives.ReadInt16LittleEndian(s[4..]), s[6], s[7]);
    }
}
