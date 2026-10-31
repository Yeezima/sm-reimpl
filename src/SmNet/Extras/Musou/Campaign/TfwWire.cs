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
/// The Three-Front War on the wire (design section H; no protocol bump, Musou's config hash keeps other profiles out). Values are little-endian; positions are quantised to
/// 0..65535 over the director's <see cref="MusouBox"/> (tag 5), as Musou's. The server's sections are envelope tags (message kind 16), sent only under <c>World.Tfw</c>:
/// <list type="bullet">
/// <item>Tag 7, the war: one record of <see cref="WarBytes"/> bytes <c>[u8 phase][u8 battle lane][f32 clock][f32 time scale][u8 transport][u8 supplies][u16 command echo]</c> then per
/// lane <c>[i8 front][f32 progress][f32 imperial][f32 ork][u8 owned mask][f32 gather left][u8 lord state]</c>, then the own block (TF7b, <see cref="TfwOwnView"/>) <c>[u8 travel s][u8 pickup s][u8 revive s]
/// [u8 extraction s][u8 revive tenths][u8 flags: bit 0 battle won][u8 retainer][u8 order][u16 recovery s][u8 guards][u8 squads][u8 reserves][u8 secured]</c>. On a change and once a second; per client
/// (the echo and the own block are the client's).</item>
/// <item>Tag 8, the objectives: <c>[u8 n]</c> then n records of 10 bytes <c>[u8 index][u8 state][u16 progress 0..1][u16 x][u16 y][u16 z]</c>, at most <see cref="MaxObjectives"/>
/// (180 bytes in all), the whole list every 10 ticks; n 0 outside a battle.</item>
/// <item>Tag 9, the Guardsmen: <c>[u8 n]</c> then n records of 10 bytes <c>[u16 id][u16 x][u16 y][u16 z][u8 yaw][u8 state]</c>, at most <see cref="MaxGuards"/>, by interest
/// (<see cref="TfwGuardInterest"/>).</item>
/// <item>Tag 10, the Guard events: <c>[u8 n]</c> then n records of 15 bytes <c>[u8 kind][u32 tick][u16 guard][u16 enemy][u16 x][u16 y][u16 z]</c>, at most <see cref="MaxGuardEvents"/>; a client
/// plays each (kind, tick, guard, enemy) once.</item>
/// </list>
/// A client's commands are message kind 17 under <see cref="CommandTag"/>: <c>[u16 sequence][u8 command][u8 argument]</c>, and for an order with a place (<see cref="HasPoint"/>: Hold and
/// Attack) then <c>[i16 x][i16 y][i16 z]</c> in decimetres (<see cref="PointStep"/>): the point of the client's crosshair, which the server cannot work out itself (it has no camera), in a form that
/// needs no box (a client may order before its first director) and cannot carry a NaN or an infinity. Resent every 250 ms until tag 7 echoes the sequence (<see cref="TfwCommandSender"/>).
/// A faithful or extended server never sends 7 to 10 and drops 17.
/// </summary>
public static class TfwWire
{
    /// <summary>The envelope tags (gore 1, loyalist barks 2, Musou 3 to 6).</summary>
    public const byte WarTag = 7, ObjectivesTag = 8, GuardsTag = 9, GuardEventsTag = 10;
    /// <summary>The tag of the war's commands in message kind 17 (another namespace than the envelope's: client to server).</summary>
    public const byte CommandTag = 7;
    /// <summary>Bytes of a command's payload, and of one with a place (at most <see cref="Protocol.MaxExtrasCommandPayload"/>).</summary>
    public const int CommandBytes = 4, PointCommandBytes = CommandBytes + 6;
    /// <summary>The metres of one step of a command's point: 0.1 m, so a point reaches 3276.7 m from the origin on each axis.</summary>
    public const float PointStep = 0.1f;

    const int WarHeadBytes = 14, LaneBytes = 19, OwnBytes = 14;
    /// <summary>The fullest revive progress on the wire (<see cref="TfwOwnView.ReviveTenths"/>).</summary>
    public const byte MaxReviveTenths = 10;
    public const int WarBytes = WarHeadBytes + 3 * LaneBytes + OwnBytes, ObjectiveBytes = 10, GuardBytes = 10, GuardEventBytes = 15;
    /// <summary>The objectives' section is at most this long (design section H).</summary>
    public const int MaxObjectivesBytes = 180;
    /// <summary>Most objectives in one section: (180 - 1) / 10 = 17.</summary>
    public const int MaxObjectives = (MaxObjectivesBytes - 1) / ObjectiveBytes;
    /// <summary>Most Guardsmen in one section: (1191 - 1) / 10 = 119.</summary>
    public const int MaxGuards = (Protocol.MaxExtrasPayloadBytes - 1) / GuardBytes;
    /// <summary>Most Guard events in one section: (1191 - 1) / 15 = 79.</summary>
    public const int MaxGuardEvents = (Protocol.MaxExtrasPayloadBytes - 1) / GuardEventBytes;
    /// <summary>The highest Guardsman id (design section E's pool: ids are slot + 1).</summary>
    public const int GuardCapacity = 256;

    /// <summary>Whether an envelope tag is the war's (7 to 10).</summary>
    public static bool Owns(byte tag) => tag is WarTag or ObjectivesTag or GuardsTag or GuardEventsTag;

    /// <summary>Whether sequence <paramref name="a"/> is after <paramref name="b"/>, across the 16-bit wrap.</summary>
    public static bool IsNewer(ushort a, ushort b) => (short)(a - b) > 0;

    // ---------------------------------------------------------------- commands (message kind 17)

    /// <summary>Whether a command names a place (the payload is <see cref="PointCommandBytes"/> long).</summary>
    public static bool HasPoint(TfwCommand command) => command is TfwCommand.Hold or TfwCommand.Attack;

    public static byte[] WriteCommand(TfwCommandRecord c)
    {
        var bytes = new byte[HasPoint(c.Command) ? PointCommandBytes : CommandBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, c.Sequence);
        bytes[2] = (byte)c.Command;
        bytes[3] = c.Argument;
        if (HasPoint(c.Command))
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4), Decimetres(c.Point.X));
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(6), Decimetres(c.Point.Y));
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8), Decimetres(c.Point.Z));
        }
        return bytes;
    }

    /// <summary>The command of a payload; null for a malformed one (a length that is not the command's, an unknown command, a Deploy to an unknown lane).</summary>
    public static TfwCommandRecord? ReadCommand(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < CommandBytes) return null;
        var command = (TfwCommand)payload[2];
        if (!Enum.IsDefined(command) || payload.Length != (HasPoint(command) ? PointCommandBytes : CommandBytes)) return null;
        if (command == TfwCommand.Deploy && !Enum.IsDefined((TfwLane)payload[3])) return null;
        var point = HasPoint(command)
            ? new Vector3(BinaryPrimitives.ReadInt16LittleEndian(payload[4..]), BinaryPrimitives.ReadInt16LittleEndian(payload[6..]), BinaryPrimitives.ReadInt16LittleEndian(payload[8..])) * PointStep
            : default;
        return new TfwCommandRecord(BinaryPrimitives.ReadUInt16LittleEndian(payload), command, payload[3], point);
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
        s[5] = (byte)(own.BattleWon ? 1 : 0);
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
        if (s[4] > MaxReviveTenths || s[5] > 1 || !Enum.IsDefined(retainer) || !Enum.IsDefined(order) || s[13] > WarRules.Positions) return null;
        return new TfwOwnView(s[0], s[1], s[2], s[3], s[4], s[5] == 1, retainer, order, BinaryPrimitives.ReadUInt16LittleEndian(s[8..]), s[10], s[11], s[12], s[13]);
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
    public static byte[] WriteObjectives(IReadOnlyList<TfwObjective> objectives, MusouBox box)
    {
        int n = Math.Min(objectives.Count, MaxObjectives);
        var bytes = new byte[1 + n * ObjectiveBytes];
        bytes[0] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            var o = objectives[i];
            var s = bytes.AsSpan(1 + i * ObjectiveBytes, ObjectiveBytes);
            s[0] = o.Index;
            s[1] = (byte)o.State;
            BinaryPrimitives.WriteUInt16LittleEndian(s[2..], Quantise(o.Progress, 0f, 1f));
            WritePosition(s[4..], o.Position, box);
        }
        return bytes;
    }

    /// <summary>The objectives of a payload; null for a malformed one (wrong length, an index twice, an unknown state).</summary>
    public static TfwObjective[]? ReadObjectives(ReadOnlySpan<byte> payload, MusouBox box)
    {
        if (payload.Length < 1) return null;
        int n = payload[0];
        if (n > MaxObjectives || payload.Length != 1 + n * ObjectiveBytes) return null;
        var list = new TfwObjective[n];
        for (int i = 0; i < n; i++)
        {
            var s = payload.Slice(1 + i * ObjectiveBytes, ObjectiveBytes);
            var state = (TfwPositionState)s[1];
            if (!Enum.IsDefined(state)) return null;
            for (int k = 0; k < i; k++) if (list[k].Index == s[0]) return null;
            list[i] = new TfwObjective(s[0], state, Dequantise(BinaryPrimitives.ReadUInt16LittleEndian(s[2..]), 0f, 1f), ReadPosition(s[4..], box));
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
}
