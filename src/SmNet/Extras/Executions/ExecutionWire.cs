using System.Buffers.Binary;
using System.Numerics;
using SmSim.Extras.Executions;

namespace SmNet.Extras.Executions;

// ExecutionTargetKind and ExecutionEndReason live in SmSim.Extras.Executions (ExecutionRun.cs) since X3: the state machine and the wire share one definition.


/// <summary>
/// An execution started (server to every client): the server <paramref name="Tick"/> the run started (<c>ExecutionRun.StartTick</c>; the first pinned tick is the next), the
/// <paramref name="Executor"/>'s player id, the target, the row (<paramref name="Clip"/>: its wire index, <c>ExecutionRowSet.WireIndexOf</c>, never a filtered list's or the kept rows' place;
/// resolve with <c>ExecutionRowSet.AtWire</c>, null: draw nothing), the alignment origin (the aligned attacker's feet after the slide, game space, metres) and the pair's yaw there (radians,
/// carried in 1/65536 turns). X3's extension: the blend <paramref name="Distance"/> (metres, carried in millimetres), the <paramref name="SlideTicks"/>, <paramref name="RaceSwapped"/>
/// (Classic's Chaos Marine rows on a loyalist) and <paramref name="SlideOffset"/> (the slide's start minus the origin, metres, carried in millimetres; zero: no slide).
/// <see cref="ToRun"/> gives the <c>ExecutionRun</c> <c>ExecutionTrack</c> poses.
/// </summary>
public readonly record struct ExecutionStart(uint Tick, ushort Executor, ExecutionTargetKind TargetKind, ushort Target, ushort Clip, Vector3 Origin, float Yaw,
    float Distance, byte SlideTicks, bool RaceSwapped, Vector3 SlideOffset = default)
{
    /// <summary>The run this Start describes (the victim fields left at their defaults: the client reads the corpse or the Ork from its own view).</summary>
    public ExecutionRun ToRun() => new(Executor, TargetKind, Target, Clip, Distance, Origin + SlideOffset, Origin, Yaw, SlideTicks, Tick, RaceSwapped);

    /// <summary>The Start of <paramref name="run"/> (a run the server started through <c>ExecutionTrack.Quantize</c>, so <see cref="ToRun"/> of its decode gives it back).</summary>
    public static ExecutionStart Of(in ExecutionRun run) => new(run.StartTick, (ushort)run.Executor, run.TargetKind, (ushort)run.Target, (ushort)Math.Max(0, run.Wire), run.Origin, run.Yaw,
        run.Distance, (byte)Math.Clamp(run.SlideTicks, 0, 255), run.RaceSwapped, run.SlideFrom - run.Origin);
}

/// <summary>An execution ended at server <paramref name="Tick"/> (server to every client).</summary>
public readonly record struct ExecutionEnd(uint Tick, ushort Executor, ExecutionEndReason Reason);

/// <summary>
/// Classic PvP Extended, to the killer alone: the kill of <paramref name="Victim"/> at server <paramref name="KillTick"/> may be finished; the window is <paramref name="WindowTicks"/> ticks
/// (the HUD prompt; the server decides on its own clock whatever the client shows).
/// </summary>
public readonly record struct ExecutionOffer(ushort Victim, uint KillTick, byte WindowTicks);

/// <summary>Warfront: one player's overshield as a fraction of its cap (<paramref name="Fraction"/> 1..255 of 255; a player without one is left out of the list).</summary>
public readonly record struct OvershieldEntry(ushort Player, byte Fraction);

/// <summary>
/// The executions wire (executions X1; docs/added-content.md section 13): envelope tag <see cref="Tag"/> (50), sent only by a server whose match runs <c>ContentExtras.Executions</c>, so a
/// faithful or Musou server never sends it and <c>Protocol.Version</c> does not move (the executions block of the config hash does, <c>ExecutionRules.Version</c>). Every payload starts
/// with a message kind byte. Little-endian. Decoders return false on anything malformed (wrong length, unknown kind, target kind or reason, a non-finite origin, a list that does
/// not fill its payload, a zero fraction) and never throw.
/// <list type="bullet">
/// <item>Start (kind 1, <see cref="StartBytes"/> = 36; 26 before X3): <c>[u8 1][u32 tick][u16 executor][u8 target kind][u16 target][u16 clip][f32 x][f32 y][f32 z][u16 yaw][u16 distance mm][u8 slide ticks][u8 flags: bit 0 race swapped][i16 slide dx mm][i16 dy][i16 dz]</c>; the decoder refuses a distance of 0 or above 20 000 mm, a flag bit above bit 0 and a slide axis beyond 4 000 mm. The server sends it in
/// three consecutive messages and repeats the live ones twice a second for late joiners (X3).</item>
/// <item>End (kind 2, <see cref="EndBytes"/> = 8): <c>[u8 2][u32 tick][u16 executor][u8 reason]</c>.</item>
/// <item>Classic offer (kind 3, <see cref="OfferBytes"/> = 8), to the killer only: <c>[u8 3][u16 victim][u32 kill tick][u8 window ticks]</c>.</item>
/// <item>Warfront overshield list (kind 4, 2 + 3 n bytes, at most <see cref="MaxOvershieldEntries"/>): <c>[u8 4][u8 n]{[u16 player][u8 fraction]}</c>, on a change and once a second. The own
/// HUD needs none of it (the own record carries <c>Shield</c> as f32).</item>
/// </list>
/// </summary>
public static class ExecutionWire
{
    /// <summary>The envelope tag (the next free one after Warfront's 49).</summary>
    public const byte Tag = 50;
    public const byte StartKind = 1, EndKind = 2, OfferKind = 3, OvershieldKind = 4;
    public const int StartBytes = 36, EndBytes = 8, OfferBytes = 8;
    /// <summary>Most entries in one overshield list (ours: Warfront's co-op has at most 4 humans; retainers never execute).</summary>
    public const int MaxOvershieldEntries = 16;
    /// <summary>The server sends each Start in this many consecutive messages (a lost datagram is covered).</summary>
    public const int StartRepeats = 3;
    /// <summary>The live executions are resent this often (Hz) for clients that joined during one.</summary>
    public const double LiveListHz = 2.0;
    /// <summary>The overshield list is resent this often (Hz) besides on every change.</summary>
    public const double OvershieldHz = 1.0;

    /// <summary>The message kind of a payload (0 when empty or unknown).</summary>
    public static byte KindOf(ReadOnlySpan<byte> data) => data.Length > 0 && data[0] is >= StartKind and <= OvershieldKind ? data[0] : (byte)0;

    /// <summary>A yaw in radians as 1/65536 turns (wrapped into [0, 2 pi); <see cref="ExecutionTrack.YawToWire"/>, the server's own run uses the same).</summary>
    public static ushort YawToWire(float yaw) => ExecutionTrack.YawToWire(yaw);

    /// <summary>The yaw a wire value stands for (radians in [0, 2 pi)).</summary>
    public static float YawFromWire(ushort value) => ExecutionTrack.YawFromWire(value);

    public static byte[] Encode(ExecutionStart s)
    {
        var b = new byte[StartBytes];
        b[0] = StartKind;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), s.Tick);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(5), s.Executor);
        b[7] = (byte)s.TargetKind;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), s.Target);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), s.Clip);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12), s.Origin.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(16), s.Origin.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(20), s.Origin.Z);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(24), YawToWire(s.Yaw));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), ExecutionTrack.DistanceToWire(s.Distance));
        b[28] = s.SlideTicks;
        b[29] = s.RaceSwapped ? RaceSwappedFlag : (byte)0;
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(30), ExecutionTrack.SlideToWire(s.SlideOffset.X));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(32), ExecutionTrack.SlideToWire(s.SlideOffset.Y));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(34), ExecutionTrack.SlideToWire(s.SlideOffset.Z));
        return b;
    }

    /// <summary>The Start's flag byte: bit 0, the races are swapped (every other bit refused).</summary>
    public const byte RaceSwappedFlag = 1;

    public static bool TryDecode(ReadOnlySpan<byte> data, out ExecutionStart start)
    {
        start = default;
        if (data.Length != StartBytes || data[0] != StartKind || data[7] > (byte)ExecutionTargetKind.Enemy) return false;
        var origin = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(data[12..]), BinaryPrimitives.ReadSingleLittleEndian(data[16..]), BinaryPrimitives.ReadSingleLittleEndian(data[20..]));
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z)) return false;
        ushort distance = BinaryPrimitives.ReadUInt16LittleEndian(data[26..]);
        if (distance == 0 || distance > ExecutionTrack.MaxDistanceMm || (data[29] & ~RaceSwappedFlag) != 0) return false;
        short dx = BinaryPrimitives.ReadInt16LittleEndian(data[30..]), dy = BinaryPrimitives.ReadInt16LittleEndian(data[32..]), dz = BinaryPrimitives.ReadInt16LittleEndian(data[34..]);
        if (Math.Abs((int)dx) > ExecutionTrack.MaxSlideMm || Math.Abs((int)dy) > ExecutionTrack.MaxSlideMm || Math.Abs((int)dz) > ExecutionTrack.MaxSlideMm) return false;
        start = new ExecutionStart(BinaryPrimitives.ReadUInt32LittleEndian(data[1..]), BinaryPrimitives.ReadUInt16LittleEndian(data[5..]), (ExecutionTargetKind)data[7],
            BinaryPrimitives.ReadUInt16LittleEndian(data[8..]), BinaryPrimitives.ReadUInt16LittleEndian(data[10..]), origin, YawFromWire(BinaryPrimitives.ReadUInt16LittleEndian(data[24..])),
            ExecutionTrack.DistanceFromWire(distance), data[28], (data[29] & RaceSwappedFlag) != 0,
            new Vector3(ExecutionTrack.SlideFromWire(dx), ExecutionTrack.SlideFromWire(dy), ExecutionTrack.SlideFromWire(dz)));
        return true;
    }

    public static byte[] Encode(ExecutionEnd e)
    {
        var b = new byte[EndBytes];
        b[0] = EndKind;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), e.Tick);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(5), e.Executor);
        b[7] = (byte)e.Reason;
        return b;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out ExecutionEnd end)
    {
        end = default;
        if (data.Length != EndBytes || data[0] != EndKind || data[7] > (byte)ExecutionEndReason.MatchEnd) return false;
        end = new ExecutionEnd(BinaryPrimitives.ReadUInt32LittleEndian(data[1..]), BinaryPrimitives.ReadUInt16LittleEndian(data[5..]), (ExecutionEndReason)data[7]);
        return true;
    }

    public static byte[] Encode(ExecutionOffer o)
    {
        var b = new byte[OfferBytes];
        b[0] = OfferKind;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), o.Victim);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(3), o.KillTick);
        b[7] = o.WindowTicks;
        return b;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out ExecutionOffer offer)
    {
        offer = default;
        if (data.Length != OfferBytes || data[0] != OfferKind || data[7] == 0) return false;
        offer = new ExecutionOffer(BinaryPrimitives.ReadUInt16LittleEndian(data[1..]), BinaryPrimitives.ReadUInt32LittleEndian(data[3..]), data[7]);
        return true;
    }

    /// <summary>The overshield list (at most <see cref="MaxOvershieldEntries"/> entries: ArgumentException beyond).</summary>
    public static byte[] Encode(IReadOnlyList<OvershieldEntry> entries)
    {
        if (entries.Count > MaxOvershieldEntries) throw new ArgumentException($"At most {MaxOvershieldEntries} overshield entries in one list.");
        var b = new byte[2 + 3 * entries.Count];
        b[0] = OvershieldKind;
        b[1] = (byte)entries.Count;
        for (int i = 0; i < entries.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2 + 3 * i), entries[i].Player);
            b[4 + 3 * i] = entries[i].Fraction;
        }
        return b;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out OvershieldEntry[] entries)
    {
        entries = [];
        if (data.Length < 2 || data[0] != OvershieldKind || data[1] > MaxOvershieldEntries || data.Length != 2 + 3 * data[1]) return false;
        var list = new OvershieldEntry[data[1]];
        for (int i = 0; i < list.Length; i++)
        {
            byte fraction = data[4 + 3 * i];
            if (fraction == 0) return false;
            list[i] = new OvershieldEntry(BinaryPrimitives.ReadUInt16LittleEndian(data[(2 + 3 * i)..]), fraction);
        }
        entries = list;
        return true;
    }

    /// <summary>An overshield as a wire fraction of its cap: 0 for none, else 1..255 (a sliver is never sent as none).</summary>
    public static byte Fraction(float overshield, float cap) =>
        !(overshield > 0f) || !(cap > 0f) ? (byte)0 : (byte)Math.Clamp((int)MathF.Ceiling(overshield / cap * 255f), 1, 255);
}
