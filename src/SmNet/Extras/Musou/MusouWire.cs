using System.Buffers.Binary;
using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Extras.Musou;

/// <summary>An enemy as it travels (tag 3): id, position, facing, kind and state; positions and facing quantised (see <see cref="MusouWire"/>).</summary>
public readonly record struct MusouEntity(ushort Id, Vector3 Position, float Yaw, MusouEnemyType Type, MusouEnemyState State);

/// <summary>One weapon drop as it travels (tag 6): id, position (quantised over the director's box), item (<see cref="MusouDropItem"/>) and state.</summary>
public readonly record struct MusouDropRecord(ushort Id, Vector3 Position, ushort Item, MusouDropState State, bool Crate = false);

/// <summary>One of the drops' player's active powerups as it travels after tag 6's drops (owner, 2026-11-10): its kind and the ticks it still works from the section's tick.</summary>
public readonly record struct MusouBuffRecord(MusouPowerup Kind, ushort TicksLeft);

/// <summary>
/// Added content "Musou" on the wire (design section C): three sections of the extras envelope (message kind 16), no protocol bump, sent only by a server whose
/// match runs <see cref="ContentExtras.Musou"/> (whose config hashes differently from every other profile). Values are little-endian.
/// <list type="bullet">
/// <item>Tag 3, entities: <c>[u8 n]</c> then n records of 11 bytes <c>[u16 id][u16 x][u16 y][u16 z][u8 yaw][u8 state][u8 type]</c> (the type a full byte since
/// Musou version 13, the state's high nibble spare); x, y, z quantised to 0..65535 over the director's <see cref="MusouBox"/>, yaw to 256 steps of a turn; at most <see cref="MaxEntities"/>
/// (one full payload).</item>
/// <item>Tag 4, events: <c>[u8 n]</c> then n records of 16 bytes <c>[u8 kind][u32 tick][u16 enemy][u16 player][u8 value][3 x 16-bit vector]</c>; the vector
/// is a direction (signed, 1/32767) for <see cref="MusouEventKind.Death"/>'s push and a position (quantised over the box, as the entities) for every other
/// kind; at most <see cref="MaxEvents"/>. Each event is repeated in 3 messages; a client plays each (kind, tick, enemy, player) once.</item>
/// <item>Tag 5, director: one record of <see cref="DirectorBytes"/> bytes <c>[u8 wave][u8 waves][u8 phase][u16 break ticks][u16 left][u16 alive][u16 boss id]
/// [f32 boss health][f32 boss max][u8 lives][f32 x6 box min, max]</c>.</item>
/// <item>Tag 6, drops (MU-D; sent to the drops' own player only): <c>[u8 n]</c> then n records of 11 bytes <c>[u16 id][u16 x][u16 y][u16 z][u16 item][u8 state]</c>,
/// the player's whole list (live drops, and ended ones for a second: picked or expired); a client drops what a newer list leaves out. Positions as tag 3;
/// at most <see cref="MaxDrops"/>. Since Musou version 38 (the powerups) the drops may be followed by the player's active buffs: <c>[u8 m]</c> then m records of 3 bytes <c>[u8 kind][u16 ticks left]</c>
/// (at most <see cref="MaxBuffs"/>, each kind once); without any the section ends after the drops, as before.</item>
/// </list>
/// MU0 fixes the layouts and their round trips; the server's sends (interest, priorities, resends) come in MU2.
/// </summary>
public static class MusouWire
{
    /// <summary>The Musou version (its wire sections and its simulation): <see cref="MusouSim.Version"/>, hashed into the config under the flag.</summary>
    public const uint Version = MusouSim.Version;

    /// <summary>The envelope tags (gore 1, loyalist barks 2).</summary>
    public const byte EntitiesTag = 3, EventsTag = 4, DirectorTag = 5, DropsTag = 6;
    public const int EntityBytes = 11, EventBytes = 16, DirectorBytes = 44, DropBytes = 11, BuffBytes = 3, MaxBuffs = 4;
    /// <summary>The state byte's top bit in tag 6: a Musou Mode weapon crate (M6).</summary>
    const int DropCrateBit = 0x80;
    /// <summary>Most drops in one section: (1191 - 1) / 11 = 108.</summary>
    public const int MaxDrops = (Protocol.MaxExtrasPayloadBytes - 1) / DropBytes;
    /// <summary>Most entities in one section: (1191 - 1) / 11 = 108 (one section alone in a full message).</summary>
    public const int MaxEntities = (Protocol.MaxExtrasPayloadBytes - 1) / EntityBytes;
    /// <summary>Most events in one section: (1191 - 1) / 16 = 74.</summary>
    public const int MaxEvents = (Protocol.MaxExtrasPayloadBytes - 1) / EventBytes;

    /// <summary>Whether an envelope tag is one of Musou's (3 to 6).</summary>
    public static bool Owns(byte tag) => tag is EntitiesTag or EventsTag or DirectorTag or DropsTag;

    // ---------------------------------------------------------------- quantisation

    // musou/boss-collision: one quantisation for the wire and the server's record of the big Orks' bodies (MusouSolids.Quantised), so the two agree to the bit.
    static ushort Quantise(float v, float min, float max) => MusouSolids.Quantise(v, min, max);

    static float Dequantise(ushort q, float min, float max) => MusouSolids.Dequantise(q, min, max);

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

    static short Unit(float v) => float.IsFinite(v) ? (short)MathF.Round(Math.Clamp(v, -1f, 1f) * short.MaxValue) : (short)0;

    /// <summary>A facing in 256 steps of a turn (any angle; wraps).</summary>
    public static byte QuantiseYaw(float yaw)
    {
        if (!float.IsFinite(yaw)) return 0;
        float turns = yaw / (2f * MathF.PI);
        return (byte)((int)MathF.Round((turns - MathF.Floor(turns)) * 256f) & 0xFF);
    }

    public static float DequantiseYaw(byte q) => q * (2f * MathF.PI / 256f);

    /// <summary>The largest error of a quantised position on each axis over <paramref name="box"/> (half a step).</summary>
    public static Vector3 PositionStep(MusouBox box) => (box.Max - box.Min) / ushort.MaxValue;

    // ---------------------------------------------------------------- tag 3: entities

    /// <summary>
    /// The entities payload of <paramref name="entities"/> (the first <see cref="MaxEntities"/>; split larger sets over several sections). Throws
    /// ArgumentException for a state that does not fit 4 bits (the type is a full byte).
    /// </summary>
    public static byte[] WriteEntities(IReadOnlyList<MusouEntity> entities, MusouBox box)
    {
        int n = Math.Min(entities.Count, MaxEntities);
        var bytes = new byte[1 + n * EntityBytes];
        bytes[0] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            var e = entities[i];
            if ((byte)e.State > 15) throw new ArgumentException($"Musou entity {e.Id}: state {e.State} does not fit 4 bits.");
            var s = bytes.AsSpan(1 + i * EntityBytes, EntityBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(s, e.Id);
            WritePosition(s[2..], e.Position, box);
            s[8] = QuantiseYaw(e.Yaw);
            s[9] = (byte)e.State;
            s[10] = (byte)e.Type;
        }
        return bytes;
    }

    /// <summary>The entities of a payload; null for a malformed one (wrong length, id 0 or above the pool, an id twice, an unknown type or state).</summary>
    public static MusouEntity[]? ReadEntities(ReadOnlySpan<byte> payload, MusouBox box)
    {
        if (payload.Length < 1) return null;
        int n = payload[0];
        if (n > MaxEntities || payload.Length != 1 + n * EntityBytes) return null;
        var list = new MusouEntity[n];
        Span<ulong> seen = stackalloc ulong[MusouSim.Capacity / 64];
        seen.Clear();
        for (int i = 0; i < n; i++)
        {
            var s = payload.Slice(1 + i * EntityBytes, EntityBytes);
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(s);
            var type = (MusouEnemyType)s[10];
            var state = (MusouEnemyState)s[9];
            if (id == 0 || id > MusouSim.Capacity || !Enum.IsDefined(type) || !Enum.IsDefined(state)) return null;
            int bit = id - 1;
            if ((seen[bit >> 6] & (1UL << (bit & 63))) != 0) return null;
            seen[bit >> 6] |= 1UL << (bit & 63);
            list[i] = new MusouEntity(id, ReadPosition(s[2..], box), DequantiseYaw(s[8]), type, state);
        }
        return list;
    }

    // ---------------------------------------------------------------- tag 4: events

    /// <summary>The events payload of <paramref name="events"/> (the first <see cref="MaxEvents"/>).</summary>
    public static byte[] WriteEvents(IReadOnlyList<MusouEvent> events, MusouBox box)
    {
        int n = Math.Min(events.Count, MaxEvents);
        var bytes = new byte[1 + n * EventBytes];
        bytes[0] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            var e = events[i];
            var s = bytes.AsSpan(1 + i * EventBytes, EventBytes);
            s[0] = (byte)e.Kind;
            BinaryPrimitives.WriteUInt32LittleEndian(s[1..], e.Tick);
            BinaryPrimitives.WriteUInt16LittleEndian(s[5..], e.Enemy);
            BinaryPrimitives.WriteUInt16LittleEndian(s[7..], e.Player);
            s[9] = e.Value;
            if (e.Kind == MusouEventKind.Death)
            {
                BinaryPrimitives.WriteInt16LittleEndian(s[10..], Unit(e.Vector.X));
                BinaryPrimitives.WriteInt16LittleEndian(s[12..], Unit(e.Vector.Y));
                BinaryPrimitives.WriteInt16LittleEndian(s[14..], Unit(e.Vector.Z));
            }
            else WritePosition(s[10..], e.Vector, box);
        }
        return bytes;
    }

    /// <summary>The events of a payload; null for a malformed one (wrong length, an unknown kind).</summary>
    public static MusouEvent[]? ReadEvents(ReadOnlySpan<byte> payload, MusouBox box)
    {
        if (payload.Length < 1) return null;
        int n = payload[0];
        if (n > MaxEvents || payload.Length != 1 + n * EventBytes) return null;
        var list = new MusouEvent[n];
        for (int i = 0; i < n; i++)
        {
            var s = payload.Slice(1 + i * EventBytes, EventBytes);
            var kind = (MusouEventKind)s[0];
            if (!Enum.IsDefined(kind)) return null;
            var vector = kind == MusouEventKind.Death
                ? new Vector3(BinaryPrimitives.ReadInt16LittleEndian(s[10..]), BinaryPrimitives.ReadInt16LittleEndian(s[12..]), BinaryPrimitives.ReadInt16LittleEndian(s[14..])) / short.MaxValue
                : ReadPosition(s[10..], box);
            list[i] = new MusouEvent(kind, BinaryPrimitives.ReadUInt32LittleEndian(s[1..]), BinaryPrimitives.ReadUInt16LittleEndian(s[5..]),
                BinaryPrimitives.ReadUInt16LittleEndian(s[7..]), s[9], vector);
        }
        return list;
    }

    // ---------------------------------------------------------------- tag 5: the director

    public static byte[] WriteDirector(MusouDirectorView d)
    {
        var bytes = new byte[DirectorBytes];
        var s = bytes.AsSpan();
        s[0] = d.Wave; s[1] = d.Waves; s[2] = (byte)d.Phase;
        BinaryPrimitives.WriteUInt16LittleEndian(s[3..], d.BreakTicksLeft);
        BinaryPrimitives.WriteUInt16LittleEndian(s[5..], d.EnemiesLeft);
        BinaryPrimitives.WriteUInt16LittleEndian(s[7..], d.EnemiesAlive);
        BinaryPrimitives.WriteUInt16LittleEndian(s[9..], d.BossId);
        BinaryPrimitives.WriteSingleLittleEndian(s[11..], Finite(d.BossHealth));
        BinaryPrimitives.WriteSingleLittleEndian(s[15..], Finite(d.BossMaxHealth));
        s[19] = d.Lives;
        var box = d.Box;
        float[] corners = [box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z];
        for (int i = 0; i < 6; i++) BinaryPrimitives.WriteSingleLittleEndian(s[(20 + 4 * i)..], Finite(corners[i]));
        return bytes;
    }

    /// <summary>A director's box corner is at most this far from the origin on any axis (ours: far beyond any map), and each axis at least this wide.</summary>
    public const float MaxBoxCoordinate = 100_000f, MinBoxSpan = 0.5f;

    /// <summary>The director of a payload; null for a malformed one (wrong length, an unknown phase, a non-finite number, an inverted, flat or huge box).</summary>
    public static MusouDirectorView? ReadDirector(ReadOnlySpan<byte> s)
    {
        if (s.Length != DirectorBytes) return null;
        var phase = (MusouPhase)s[2];
        if (!Enum.IsDefined(phase)) return null;
        Span<float> f = stackalloc float[8];
        f[0] = BinaryPrimitives.ReadSingleLittleEndian(s[11..]);
        f[1] = BinaryPrimitives.ReadSingleLittleEndian(s[15..]);
        for (int i = 0; i < 6; i++) f[2 + i] = BinaryPrimitives.ReadSingleLittleEndian(s[(20 + 4 * i)..]);
        foreach (float v in f) if (!float.IsFinite(v)) return null;
        // The box: each corner within MaxBoxCoordinate and each axis at least MinBoxSpan wide (no inverted, flat or huge box that dequantises to inf or NaN).
        for (int i = 0; i < 3; i++)
            if (MathF.Abs(f[2 + i]) > MaxBoxCoordinate || MathF.Abs(f[5 + i]) > MaxBoxCoordinate || !(f[5 + i] - f[2 + i] >= MinBoxSpan)) return null;
        return new MusouDirectorView(s[0], s[1], phase, BinaryPrimitives.ReadUInt16LittleEndian(s[3..]), BinaryPrimitives.ReadUInt16LittleEndian(s[5..]),
            BinaryPrimitives.ReadUInt16LittleEndian(s[7..]), BinaryPrimitives.ReadUInt16LittleEndian(s[9..]), f[0], f[1], s[19],
            new MusouBox(new Vector3(f[2], f[3], f[4]), new Vector3(f[5], f[6], f[7])));
    }

    static float Finite(float v) => float.IsFinite(v) ? v : 0f;

    // ---------------------------------------------------------------- tag 6: drops

    /// <summary>The drops payload of <paramref name="drops"/> (the first <see cref="MaxDrops"/>).</summary>
    public static byte[] WriteDrops(IReadOnlyList<MusouDropRecord> drops, MusouBox box) => WriteDrops(drops, box, Array.Empty<MusouBuffRecord>());

    /// <summary>The drops payload of <paramref name="drops"/> followed by the player's <paramref name="buffs"/> (none: the drops alone; the drops are cut to leave the buffs room).</summary>
    public static byte[] WriteDrops(IReadOnlyList<MusouDropRecord> drops, MusouBox box, IReadOnlyList<MusouBuffRecord> buffs)
    {
        int m = Math.Min(buffs.Count, MaxBuffs);
        int trailer = m > 0 ? 1 + m * BuffBytes : 0;
        int n = Math.Min(drops.Count, Math.Min(MaxDrops, (Protocol.MaxExtrasPayloadBytes - 1 - trailer) / DropBytes));
        var bytes = new byte[1 + n * DropBytes + trailer];
        bytes[0] = (byte)n;
        if (m > 0)
        {
            int at = 1 + n * DropBytes;
            bytes[at] = (byte)m;
            for (int k = 0; k < m; k++)
            {
                bytes[at + 1 + k * BuffBytes] = (byte)buffs[k].Kind;
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 2 + k * BuffBytes), buffs[k].TicksLeft);
            }
        }
        for (int i = 0; i < n; i++)
        {
            var d = drops[i];
            var s = bytes.AsSpan(1 + i * DropBytes, DropBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(s, d.Id);
            WritePosition(s[2..], d.Position, box);
            BinaryPrimitives.WriteUInt16LittleEndian(s[8..], d.Item);
            s[10] = (byte)((byte)d.State | (d.Crate ? DropCrateBit : 0));
        }
        return bytes;
    }

    /// <summary>The drops of a payload; null for a malformed one (wrong length, id 0, an id twice, an unknown state, a malformed buff list).</summary>
    public static MusouDropRecord[]? ReadDrops(ReadOnlySpan<byte> payload, MusouBox box) => ReadDrops(payload, box, out _);

    /// <summary>The drops of a payload and the buffs after them (empty when the section has none); null for a malformed one.</summary>
    public static MusouDropRecord[]? ReadDrops(ReadOnlySpan<byte> payload, MusouBox box, out MusouBuffRecord[] buffs)
    {
        buffs = Array.Empty<MusouBuffRecord>();
        if (payload.Length < 1) return null;
        int n = payload[0];
        int end = 1 + n * DropBytes;
        if (n > MaxDrops || payload.Length < end) return null;
        if (payload.Length > end)
        {
            int m = payload[end];
            if (m == 0 || m > MaxBuffs || payload.Length != end + 1 + m * BuffBytes) return null;
            var read = new MusouBuffRecord[m];
            for (int k = 0; k < m; k++)
            {
                var kind = (MusouPowerup)payload[end + 1 + k * BuffBytes];
                if (kind == MusouPowerup.None || !Enum.IsDefined(kind)) return null;
                for (int j = 0; j < k; j++) if (read[j].Kind == kind) return null;
                read[k] = new MusouBuffRecord(kind, BinaryPrimitives.ReadUInt16LittleEndian(payload[(end + 2 + k * BuffBytes)..]));
            }
            buffs = read;
        }
        var list = new MusouDropRecord[n];
        for (int i = 0; i < n; i++)
        {
            var s = payload.Slice(1 + i * DropBytes, DropBytes);
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(s);
            var state = (MusouDropState)(s[10] & ~DropCrateBit);
            if (id == 0 || !Enum.IsDefined(state)) return null;
            for (int k = 0; k < i; k++) if (list[k].Id == id) return null;
            list[i] = new MusouDropRecord(id, ReadPosition(s[2..], box), BinaryPrimitives.ReadUInt16LittleEndian(s[8..]), state, (s[10] & DropCrateBit) != 0);
        }
        return list;
    }
}
