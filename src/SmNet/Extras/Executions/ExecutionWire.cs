using System.Buffers.Binary;
using System.Numerics;

namespace SmNet.Extras.Executions;

/// <summary>What an execution's target is: a player (Classic PvP's corpse) or one of Warfront's AI enemies (an Ork, by its enemy id).</summary>
public enum ExecutionTargetKind : byte { Player = 0, Enemy = 1 }

/// <summary>Why an execution ended (the client's clean-up and the executor's camera: a death goes to the death view).</summary>
public enum ExecutionEndReason : byte { Done = 0, Killed = 1, Invalid = 2, Disconnect = 3, MatchEnd = 4 }

/// <summary>
/// An execution started (server to every client): the server <paramref name="Tick"/> the lock began, the <paramref name="Executor"/>'s player id, the target, the catalogue clip
/// (<paramref name="Clip"/>, an index into X2's catalogue), the alignment origin (game space, metres) and the executor's yaw there (radians, carried in 1/65536 turns).
/// </summary>
public readonly record struct ExecutionStart(uint Tick, ushort Executor, ExecutionTargetKind TargetKind, ushort Target, ushort Clip, Vector3 Origin, float Yaw);

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
/// <item>Start (kind 1, <see cref="StartBytes"/> = 26): <c>[u8 1][u32 tick][u16 executor][u8 target kind][u16 target][u16 clip][f32 x][f32 y][f32 z][u16 yaw]</c>. The server sends it in
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
    public const int StartBytes = 26, EndBytes = 8, OfferBytes = 8;
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

    /// <summary>A yaw in radians as 1/65536 turns (wrapped into [0, 2 pi)).</summary>
    public static ushort YawToWire(float yaw)
    {
        if (!float.IsFinite(yaw)) return 0;
        double turns = yaw / (2 * Math.PI);
        turns -= Math.Floor(turns);
        return (ushort)((int)Math.Round(turns * 65536.0) & 0xFFFF);
    }

    /// <summary>The yaw a wire value stands for (radians in [0, 2 pi)).</summary>
    public static float YawFromWire(ushort value) => (float)(value / 65536.0 * 2 * Math.PI);

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
        return b;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out ExecutionStart start)
    {
        start = default;
        if (data.Length != StartBytes || data[0] != StartKind || data[7] > (byte)ExecutionTargetKind.Enemy) return false;
        var origin = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(data[12..]), BinaryPrimitives.ReadSingleLittleEndian(data[16..]), BinaryPrimitives.ReadSingleLittleEndian(data[20..]));
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z)) return false;
        start = new ExecutionStart(BinaryPrimitives.ReadUInt32LittleEndian(data[1..]), BinaryPrimitives.ReadUInt16LittleEndian(data[5..]), (ExecutionTargetKind)data[7],
            BinaryPrimitives.ReadUInt16LittleEndian(data[8..]), BinaryPrimitives.ReadUInt16LittleEndian(data[10..]), origin, YawFromWire(BinaryPrimitives.ReadUInt16LittleEndian(data[24..])));
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
