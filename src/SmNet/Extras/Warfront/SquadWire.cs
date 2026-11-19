using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace SmNet.Extras.Warfront;

/// <summary>What a squad event reports (tag 34).</summary>
public enum SquadEventKind : byte { Downed = 1, Revived = 2, Out = 3, Recruited = 4, Pound = 5 }

/// <summary>The kinds of the squad's client commands (message kind 17, tag 41): the first payload byte.</summary>
public enum SquadCommandKind : byte { Rename = 1, JumpPound = 2, SquadClass = 3 }

/// <summary>
/// One Marine of a SquadState section as it travels (W10): the state is 1 Standing, 2 Downed (free and out Marines are not sent). <see cref="Aux"/> is the downed seconds left (tenths, 0..200) while
/// <see cref="BeingRevived"/> is clear, and the revive progress (0..200) while it is set (one byte holds one of them: the client keeps counting the window down by itself while a revive is under way).
/// </summary>
public readonly record struct SquadMarineWire(byte Index, byte State, bool Firing, bool Melee, bool BeingRevived, byte Order, bool Airborne, Vector3 Position, float Yaw, byte Health, byte Aux, ushort Target)
{
    public const ushort NoTarget = 0xFFFF;
}

/// <summary>A decoded SquadState section: the sender's phase counter, the first index in it and its Marines.</summary>
public sealed record SquadStateSection(byte Phase, byte FirstIndex, IReadOnlyList<SquadMarineWire> Marines);

/// <summary>One roster Marine as it travels (tag 33): slot, the battle index (255: not in this battle), name, class, the three weapon ids, whether it wears the owner's own armour pieces, the service record.</summary>
public sealed record SquadRosterWire(byte Slot, byte Index, string Name, byte Class, ushort Primary, ushort Secondary, ushort Melee, bool OwnPieces, IReadOnlyList<ushort> Record)
{
    public const byte NotInBattle = 255;
}

/// <summary>A decoded SquadRoster section: one owner's Marines.</summary>
public sealed record SquadRosterSection(ulong Handle, byte OwnerPlayer, bool Held, IReadOnlyList<SquadRosterWire> Marines)
{
    public const byte NoPlayer = 255;
}

/// <summary>One squad event (tag 34): <see cref="By"/> is a player id, 200 + a Marine index, or 255; a pound carries the point it hit.</summary>
public readonly record struct SquadEventWire(SquadEventKind Kind, byte Index, byte By, Vector3 Point = default);

/// <summary>A decoded squad command (tag 41).</summary>
public readonly record struct SquadCommandWire(SquadCommandKind Kind, byte Slot, string Name, Vector3 Point, byte Option);

/// <summary>
/// The Warfront squad on the wire (slice W10; ours; no existing section is changed). Little-endian. The server's sections are envelope tags (message kind 16): 32 the Marines (10 Hz, every client), 33 a roster
/// section per owner (on change and in rotation), 34 the events; the client's commands are message kind 17 under tag 41. Every decoder is <c>TryDecode</c>: it returns false and never throws.
/// <list type="bullet">
/// <item>Tag 32: <c>[u8 phase][u8 first index][u8 count]</c> then <see cref="MarineBytes"/> bytes a Marine <c>[u8 index 0..35][u8 flags: bits 0-1 state, 2 firing, 3 melee, 4 being revived, 5-6 order, 7 airborne]
/// [i16 x][i16 y at 5 cm][i16 z at 2 cm][u8 yaw][u8 health][u8 aux][u16 target or 0xFFFF]</c>; positions reach 1638 m (x, y) and 655 m (z) from the origin, yaw is 1.4 degrees a step, health a 255th.</item>
/// <item>Tag 33: <c>[u64 handle][u8 owner player or 255][u8 held][u8 count]</c> then per Marine <c>[u8 slot][u8 index or 255][u8 len][name, UTF-8, at most 24 bytes][u8 class][u16 primary][u16 secondary][u16 melee]
/// [u8 flags: bit 0 own pieces][u16 x 8 record]</c>.</item>
/// <item>Tag 34: <c>[u8 count]</c> then <c>[u8 kind][u8 index][u8 by]</c>, a pound then <c>[i16 x][i16 y][i16 z]</c> in decimetres.</item>
/// <item>Tag 41 (command): <c>[u8 kind]</c>, Rename <c>[u8 slot][u8 len][name]</c>, JumpPound <c>[i16 x][i16 y][i16 z]</c> in decimetres, SquadClass <c>[u8]</c>.</item>
/// </list>
/// </summary>
public static class SquadWire
{
    public const byte StateTag = 32, RosterTag = 33, EventsTag = 34, CommandTag = 41;
    public const int StateHeaderBytes = 3, MarineBytes = 13, MaxMarines = 36, RosterHeaderBytes = 11, RecordFigures = 8, NameMaxBytes = 24;
    /// <summary>The most bytes a roster Marine takes: slot, index, length, a full name, class, three weapons, flags and eight figures.</summary>
    public const int RosterMarineMaxBytes = 3 + NameMaxBytes + 1 + 6 + 1 + RecordFigures * 2;
    public const int MaxRosterMarines = 9;
    public const int EventBytes = 3, PoundEventBytes = 9;
    /// <summary>The longest state section (all 36 Marines) and roster section (nine full ones): both far inside <see cref="Protocol.MaxExtrasPayloadBytes"/>.</summary>
    public const int MaxStateBytes = StateHeaderBytes + MaxMarines * MarineBytes, MaxRosterBytes = RosterHeaderBytes + MaxRosterMarines * RosterMarineMaxBytes;
    public const int MaxEvents = (Protocol.MaxExtrasPayloadBytes - 1) / PoundEventBytes;
    public const float XyStep = 0.05f, ZStep = 0.02f, PointStep = 0.1f, MaxDownedSeconds = 20f;
    public const byte MaxAux = 200;

    // ---- quantising

    static short Q(float v, float step) => (short)Math.Clamp((int)MathF.Round(v / step), short.MinValue, short.MaxValue);
    static float Finite(float v) => float.IsFinite(v) ? v : 0f;
    public static byte YawByte(float radians)
    {
        float t = Finite(radians) / (MathF.PI * 2f);
        t -= MathF.Floor(t);
        return (byte)((int)MathF.Round(t * 256f) & 255);
    }
    public static float YawOf(byte b) => b * (MathF.PI * 2f / 256f);
    public static byte HealthByte(float share) => (byte)Math.Clamp((int)MathF.Round(Finite(share) * 255f), 0, 255);
    public static float HealthOf(byte b) => b / 255f;
    /// <summary>The aux byte of a downed Marine: tenths of a second left (at most 20 s).</summary>
    public static byte DownedAux(float seconds) => (byte)Math.Clamp((int)MathF.Round(Finite(seconds) * 10f), 0, MaxAux);
    /// <summary>The aux byte of a revive under way: the share 0..1 in 200 steps.</summary>
    public static byte ReviveAux(float share) => (byte)Math.Clamp((int)MathF.Round(Finite(share) * MaxAux), 0, MaxAux);

    /// <summary>A Marine's wire record from its simulation values.</summary>
    public static SquadMarineWire Make(int index, byte state, bool firing, bool melee, bool beingRevived, byte order, bool airborne, Vector3 position, float yaw, float healthShare,
        float downedLeft, float reviveShare, int target) =>
        new((byte)index, state, firing, melee, beingRevived, (byte)(order & 3), airborne, position, yaw, HealthByte(healthShare),
            beingRevived ? ReviveAux(reviveShare) : DownedAux(downedLeft), target is >= 0 and < ushort.MaxValue ? (ushort)target : SquadMarineWire.NoTarget);

    /// <summary>The place as the wire carries it (what a client reads back).</summary>
    public static Vector3 Quantised(Vector3 p) => new(Q(p.X, XyStep) * XyStep, Q(p.Y, XyStep) * XyStep, Q(p.Z, ZStep) * ZStep);

    // ---- tag 32

    /// <summary>One SquadState section for <paramref name="marines"/> (ascending index, at most <see cref="MaxMarines"/>).</summary>
    public static byte[] EncodeState(byte phase, IReadOnlyList<SquadMarineWire> marines)
    {
        int n = Math.Min(marines.Count, MaxMarines);
        var b = new byte[StateHeaderBytes + n * MarineBytes];
        b[0] = phase; b[1] = n > 0 ? marines[0].Index : (byte)0; b[2] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            var m = marines[i];
            var s = b.AsSpan(StateHeaderBytes + i * MarineBytes, MarineBytes);
            s[0] = m.Index;
            s[1] = (byte)((m.State & 3) | (m.Firing ? 4 : 0) | (m.Melee ? 8 : 0) | (m.BeingRevived ? 16 : 0) | ((m.Order & 3) << 5) | (m.Airborne ? 128 : 0));
            BinaryPrimitives.WriteInt16LittleEndian(s[2..], Q(Finite(m.Position.X), XyStep));
            BinaryPrimitives.WriteInt16LittleEndian(s[4..], Q(Finite(m.Position.Y), XyStep));
            BinaryPrimitives.WriteInt16LittleEndian(s[6..], Q(Finite(m.Position.Z), ZStep));
            s[8] = YawByte(m.Yaw); s[9] = m.Health; s[10] = Math.Min(m.Aux, MaxAux);
            BinaryPrimitives.WriteUInt16LittleEndian(s[11..], m.Target);
        }
        return b;
    }

    /// <summary>
    /// The section to send this tick: the whole list when it fits <paramref name="maxPayload"/>, else only one half, by <paramref name="phase"/> (even: the lower indices; odd: the rest), so two sends
    /// of alternating phase cover all of them (plan risk 4).
    /// </summary>
    public static byte[] EncodeStateFitting(byte phase, IReadOnlyList<SquadMarineWire> marines, int maxPayload, out bool half)
    {
        half = false;
        if (StateHeaderBytes + marines.Count * MarineBytes <= maxPayload) return EncodeState(phase, marines);
        half = true;
        int mid = marines.Count / 2;
        var part = (phase & 1) == 0 ? marines.Take(mid).ToList() : marines.Skip(mid).ToList();
        int fit = Math.Max(0, (maxPayload - StateHeaderBytes) / MarineBytes);
        if (part.Count > fit) part = part.Take(fit).ToList();
        return EncodeState(phase, part);
    }

    public static bool TryDecodeState(ReadOnlySpan<byte> p, out SquadStateSection section)
    {
        section = null!;
        if (p.Length < StateHeaderBytes) return false;
        int n = p[2];
        if (n > MaxMarines || p.Length != StateHeaderBytes + n * MarineBytes) return false;
        var list = new List<SquadMarineWire>(n);
        int last = -1;
        for (int i = 0; i < n; i++)
        {
            var s = p.Slice(StateHeaderBytes + i * MarineBytes, MarineBytes);
            if (s[0] >= MaxMarines || s[0] <= last) return false;
            last = s[0];
            int state = s[1] & 3;
            if (state is 0 or 3 || s[10] > MaxAux) return false;
            list.Add(new SquadMarineWire(s[0], (byte)state, (s[1] & 4) != 0, (s[1] & 8) != 0, (s[1] & 16) != 0, (byte)((s[1] >> 5) & 3), (s[1] & 128) != 0,
                new Vector3(BinaryPrimitives.ReadInt16LittleEndian(s[2..]) * XyStep, BinaryPrimitives.ReadInt16LittleEndian(s[4..]) * XyStep, BinaryPrimitives.ReadInt16LittleEndian(s[6..]) * ZStep),
                YawOf(s[8]), s[9], s[10], BinaryPrimitives.ReadUInt16LittleEndian(s[11..])));
        }
        if (n > 0 && p[1] != list[0].Index) return false;
        section = new SquadStateSection(p[0], p[1], list);
        return true;
    }

    // ---- tag 33

    static byte[] NameBytes(string? name)
    {
        var bytes = Encoding.UTF8.GetBytes(name ?? "");
        if (bytes.Length <= NameMaxBytes) return bytes;
        int cut = NameMaxBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;   // not in the middle of a character
        return bytes[..cut];
    }

    public static byte[] EncodeRoster(SquadRosterSection s)
    {
        int n = Math.Min(s.Marines.Count, MaxRosterMarines);
        using var ms = new MemoryStream();
        Span<byte> t = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(t, s.Handle);
        ms.Write(t);
        ms.WriteByte(s.OwnerPlayer); ms.WriteByte(s.Held ? (byte)1 : (byte)0); ms.WriteByte((byte)n);
        for (int i = 0; i < n; i++)
        {
            var m = s.Marines[i];
            var name = NameBytes(m.Name);
            ms.WriteByte(m.Slot); ms.WriteByte(m.Index); ms.WriteByte((byte)name.Length); ms.Write(name);
            ms.WriteByte(m.Class);
            foreach (ushort w in new[] { m.Primary, m.Secondary, m.Melee }) { BinaryPrimitives.WriteUInt16LittleEndian(t, w); ms.Write(t[..2]); }
            ms.WriteByte(m.OwnPieces ? (byte)1 : (byte)0);
            for (int f = 0; f < RecordFigures; f++) { BinaryPrimitives.WriteUInt16LittleEndian(t, f < m.Record.Count ? m.Record[f] : (ushort)0); ms.Write(t[..2]); }
        }
        return ms.ToArray();
    }

    public static bool TryDecodeRoster(ReadOnlySpan<byte> p, out SquadRosterSection section)
    {
        section = null!;
        if (p.Length < RosterHeaderBytes) return false;
        ulong handle = BinaryPrimitives.ReadUInt64LittleEndian(p);
        byte owner = p[8], held = p[9];
        int n = p[10];
        if (held > 1 || n > MaxRosterMarines) return false;
        int at = RosterHeaderBytes;
        var list = new List<SquadRosterWire>(n);
        var seen = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            if (p.Length - at < 3) return false;
            byte slot = p[at], index = p[at + 1];
            int len = p[at + 2];
            at += 3;
            if (len > NameMaxBytes || slot >= MaxRosterMarines || !seen.Add(slot) || (index != SquadRosterWire.NotInBattle && index >= MaxMarines)) return false;
            if (p.Length - at < len + 1 + 6 + 1 + RecordFigures * 2) return false;
            string name;
            try { name = new UTF8Encoding(false, true).GetString(p.Slice(at, len)); } catch (ArgumentException) { return false; }
            at += len;
            byte cls = p[at++];
            ushort a = BinaryPrimitives.ReadUInt16LittleEndian(p[at..]), b = BinaryPrimitives.ReadUInt16LittleEndian(p[(at + 2)..]), c = BinaryPrimitives.ReadUInt16LittleEndian(p[(at + 4)..]);
            at += 6;
            byte flags = p[at++];
            if ((flags & ~1) != 0) return false;
            var rec = new ushort[RecordFigures];
            for (int f = 0; f < RecordFigures; f++, at += 2) rec[f] = BinaryPrimitives.ReadUInt16LittleEndian(p[at..]);
            list.Add(new SquadRosterWire(slot, index, name, cls, a, b, c, (flags & 1) != 0, rec));
        }
        if (at != p.Length) return false;
        section = new SquadRosterSection(handle, owner, held == 1, list);
        return true;
    }

    // ---- tag 34

    public static byte[] EncodeEvents(IReadOnlyList<SquadEventWire> events)
    {
        int n = Math.Min(Math.Min(events.Count, MaxEvents), 255);
        using var ms = new MemoryStream();
        ms.WriteByte((byte)n);
        Span<byte> t = stackalloc byte[2];
        for (int i = 0; i < n; i++)
        {
            var e = events[i];
            ms.WriteByte((byte)e.Kind); ms.WriteByte(e.Index); ms.WriteByte(e.By);
            if (e.Kind == SquadEventKind.Pound)
                foreach (float v in new[] { e.Point.X, e.Point.Y, e.Point.Z }) { BinaryPrimitives.WriteInt16LittleEndian(t, Q(Finite(v), PointStep)); ms.Write(t); }
        }
        return ms.ToArray();
    }

    public static bool TryDecodeEvents(ReadOnlySpan<byte> p, out IReadOnlyList<SquadEventWire> events)
    {
        events = [];
        if (p.Length < 1) return false;
        int n = p[0], at = 1;
        var list = new List<SquadEventWire>(Math.Min(n, MaxEvents));
        for (int i = 0; i < n; i++)
        {
            if (p.Length - at < EventBytes) return false;
            byte kind = p[at], index = p[at + 1], by = p[at + 2];
            at += EventBytes;
            if (kind is < (byte)SquadEventKind.Downed or > (byte)SquadEventKind.Pound || index >= MaxMarines) return false;
            var point = default(Vector3);
            if (kind == (byte)SquadEventKind.Pound)
            {
                if (p.Length - at < 6) return false;
                point = new Vector3(BinaryPrimitives.ReadInt16LittleEndian(p[at..]) * PointStep, BinaryPrimitives.ReadInt16LittleEndian(p[(at + 2)..]) * PointStep, BinaryPrimitives.ReadInt16LittleEndian(p[(at + 4)..]) * PointStep);
                at += 6;
            }
            list.Add(new SquadEventWire((SquadEventKind)kind, index, by, point));
        }
        if (at != p.Length) return false;
        events = list;
        return true;
    }

    // ---- tag 41

    /// <summary>The Rename command; null for a name that is empty after trimming or over <see cref="NameMaxBytes"/> UTF-8 bytes.</summary>
    public static byte[]? EncodeRename(byte slot, string name)
    {
        name = (name ?? "").Trim();
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length is 0 or > NameMaxBytes) return null;
        var b = new byte[3 + bytes.Length];
        b[0] = (byte)SquadCommandKind.Rename; b[1] = slot; b[2] = (byte)bytes.Length;
        bytes.CopyTo(b, 3);
        return b;
    }

    public static byte[] EncodeJumpPound(Vector3 mark)
    {
        var b = new byte[7];
        b[0] = (byte)SquadCommandKind.JumpPound;
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(1), Q(Finite(mark.X), PointStep));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(3), Q(Finite(mark.Y), PointStep));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(5), Q(Finite(mark.Z), PointStep));
        return b;
    }

    public static byte[] EncodeSquadClass(byte option) => [(byte)SquadCommandKind.SquadClass, option];

    public static bool TryDecodeCommand(ReadOnlySpan<byte> p, out SquadCommandWire command)
    {
        command = default;
        if (p.Length < 1) return false;
        switch ((SquadCommandKind)p[0])
        {
            case SquadCommandKind.Rename:
            {
                if (p.Length < 3 || p[2] == 0 || p[2] > NameMaxBytes || p.Length != 3 + p[2] || p[1] >= MaxRosterMarines) return false;
                string name;
                try { name = new UTF8Encoding(false, true).GetString(p.Slice(3, p[2])); } catch (ArgumentException) { return false; }
                command = new SquadCommandWire(SquadCommandKind.Rename, p[1], name, default, 0);
                return true;
            }
            case SquadCommandKind.JumpPound:
                if (p.Length != 7) return false;
                command = new SquadCommandWire(SquadCommandKind.JumpPound, 0, "", new Vector3(BinaryPrimitives.ReadInt16LittleEndian(p[1..]) * PointStep, BinaryPrimitives.ReadInt16LittleEndian(p[3..]) * PointStep, BinaryPrimitives.ReadInt16LittleEndian(p[5..]) * PointStep), 0);
                return true;
            case SquadCommandKind.SquadClass:
                if (p.Length != 2 || p[1] > 2) return false;
                command = new SquadCommandWire(SquadCommandKind.SquadClass, 0, "", default, p[1]);
                return true;
            default: return false;
        }
    }
}
