using System.Text;
using SmSim.Extras.Warfront.Progression;

namespace SmNet.Extras.Warfront;

/// <summary>A decoded progress record: <see cref="Flags"/> (<see cref="WarfrontProgressSaveWire.FlagAbsent"/>, <see cref="WarfrontProgressSaveWire.FlagReset"/>) and the progression (null when absent).</summary>
public sealed record WarfrontProgressRecord(byte Flags, WarfrontProgressFile? State)
{
    public bool Absent => (Flags & WarfrontProgressSaveWire.FlagAbsent) != 0;
    public bool Reset => (Flags & WarfrontProgressSaveWire.FlagReset) != 0;
}

/// <summary>
/// The wire of a player's persistent progression (WarfrontRules.Version 20, <see cref="WarfrontWire.Format"/> 2; no change to <c>Protocol.Version</c> or <c>ContentExtras.WireVersion</c>). Values are little-endian.
/// One record layout, two carriers:
/// <list type="bullet">
/// <item>Record: <c>[u8 flags: 1 absent, 2 reset][u32 xp][u8 picksTaken][u8 nCards][u8 card]*[u8 nExtra]{[u8 len][class][u8 len][name]}</c>. With flag 1 (absent: the sender has no file) the record is the flag byte alone.
/// Caps: <see cref="WarfrontProgressFile.MaxCards"/> cards, <see cref="WarfrontProgressFile.MaxExtras"/> extras, class at most <see cref="WarfrontProgressFile.MaxClassBytes"/> and name at most
/// <see cref="WarfrontProgressFile.MaxNameBytes"/> UTF-8 bytes, so a record is at most <see cref="MaxRecordBytes"/> bytes.</item>
/// <item>Host to client: envelope tag <see cref="EnvelopeTag"/> (49: tag 47 is the Battle section, 48 the weather), the record as the section payload, in one piece.</item>
/// <item>Client to server: command kind <c>Progress</c> (5) of tag <see cref="WarfrontWire.CommandTag"/>. A command holds at most 64 bytes, so the record is cut into chunks:
/// <c>[u8 5][u8 transfer id][u8 index][u8 count][up to <see cref="ChunkData"/> record bytes]</c> (<see cref="CommandChunks"/>); the server collects them with a <see cref="Reassembler"/> per client.</item>
/// </list>
/// Every <c>TryDecode</c> returns false on any malformed, truncated, oversized or out-of-range payload and never throws. The wire carries no key, version or resetAt.
/// </summary>
public static class WarfrontProgressSaveWire
{
    /// <summary>ProgressSave, host to client. 47 is the Battle section (<c>WarfrontBattleWire</c>) and 48 the weather state, so the plan's 47 moved to the next free tag.</summary>
    public const byte EnvelopeTag = 49;
    public const byte FlagAbsent = 1, FlagReset = 2;
    /// <summary>The Progress command's kind byte (<see cref="WarfrontClientCommandKind.Progress"/>).</summary>
    public const byte CommandKind = 5;
    /// <summary>Chunk header inside a command: kind, transfer id, index, count.</summary>
    public const int ChunkHeader = 4;
    public const int ChunkData = Protocol.MaxExtrasCommandPayload - ChunkHeader;
    /// <summary>The longest record: flags, xp, picks, card count, the cards, extra count and the extras at their caps (393 bytes).</summary>
    public const int MaxRecordBytes = 1 + 4 + 1 + 1 + WarfrontProgressFile.MaxCards + 1 + WarfrontProgressFile.MaxExtras * (2 + WarfrontProgressFile.MaxClassBytes + WarfrontProgressFile.MaxNameBytes);
    /// <summary>The most chunks of one record (a record's count byte above this is refused).</summary>
    public const int MaxChunks = (MaxRecordBytes + ChunkData - 1) / ChunkData;

    // ---- record ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>The record bytes. <paramref name="state"/> null (or <see cref="FlagAbsent"/> in <paramref name="flags"/>) makes the one-byte absent record. State is cut to the caps, never throws; sanitize it first for a meaningful record.</summary>
    public static byte[] Encode(WarfrontProgressFile? state, byte flags = 0)
    {
        flags &= FlagAbsent | FlagReset;
        if (state is null || (flags & FlagAbsent) != 0) return new[] { (byte)(flags | FlagAbsent) };
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(flags);
        w.Write((uint)Math.Clamp(state.Xp, 0L, uint.MaxValue));
        var cards = state.Cards.Where(WarfrontProgressFile.IsKnownCard).Take(WarfrontProgressFile.MaxCards).ToArray();
        w.Write((byte)Math.Clamp(state.PicksTaken, 0, 255));
        w.Write((byte)cards.Length);
        foreach (var c in cards) w.Write((byte)c);
        var extras = state.ExtraRetainers.Take(WarfrontProgressFile.MaxExtras).ToArray();
        w.Write((byte)extras.Length);
        foreach (var e in extras) { Str(w, e?.ClassName, WarfrontProgressFile.MaxClassBytes); Str(w, e?.Name, WarfrontProgressFile.MaxNameBytes); }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Decodes a record. False for anything malformed (unknown flag bits, absent with a body, counts past the caps, an unknown card, a name past its cap, trailing bytes).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> d, out WarfrontProgressRecord record)
    {
        record = null!;
        if (d.Length is 0 or > MaxRecordBytes) return false;
        byte flags = d[0];
        if ((flags & ~(FlagAbsent | FlagReset)) != 0) return false;
        if ((flags & FlagAbsent) != 0)
        {
            if (d.Length != 1) return false;
            record = new WarfrontProgressRecord(flags, null);
            return true;
        }
        int at = 1;
        if (d.Length < at + 4 + 3) return false;
        uint xp = (uint)(d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24); at += 4;
        int picks = d[at++], nCards = d[at++];
        if (nCards > WarfrontProgressFile.MaxCards || at + nCards + 1 > d.Length) return false;
        var cards = new List<int>(nCards);
        for (int i = 0; i < nCards; i++) { int c = d[at++]; if (!WarfrontProgressFile.IsKnownCard(c)) return false; cards.Add(c); }
        int nExtra = d[at++];
        if (nExtra > WarfrontProgressFile.MaxExtras) return false;
        var extras = new List<WarfrontExtraRetainer>(nExtra);
        for (int i = 0; i < nExtra; i++)
        {
            if (!Str(d, ref at, WarfrontProgressFile.MaxClassBytes, out var cls) || !Str(d, ref at, WarfrontProgressFile.MaxNameBytes, out var name)) return false;
            extras.Add(new WarfrontExtraRetainer { ClassName = cls, Name = name });
        }
        if (at != d.Length) return false;
        record = new WarfrontProgressRecord(flags, new WarfrontProgressFile { Xp = xp, PicksTaken = picks, Cards = cards, ExtraRetainers = extras });
        return true;
    }

    // ---- the command's chunks (client to server) ------------------------------------------------------------------------------------------

    /// <summary>The record cut into full Progress command payloads (each at most <see cref="Protocol.MaxExtrasCommandPayload"/> bytes), to send under <see cref="WarfrontWire.CommandTag"/>. Send all of them; the server collects by index, so order does not matter.</summary>
    public static IReadOnlyList<byte[]> CommandChunks(byte[] record, byte transferId)
    {
        if (record.Length == 0 || record.Length > MaxRecordBytes) throw new ArgumentException("record size");
        int n = (record.Length + ChunkData - 1) / ChunkData;
        var list = new List<byte[]>(n);
        for (int i = 0; i < n; i++)
        {
            int at = i * ChunkData, len = Math.Min(ChunkData, record.Length - at);
            var c = new byte[ChunkHeader + len];
            c[0] = CommandKind; c[1] = transferId; c[2] = (byte)i; c[3] = (byte)n;
            Buffer.BlockCopy(record, at, c, ChunkHeader, len);
            list.Add(c);
        }
        return list;
    }

    /// <summary>True for a well-formed chunk body (the bytes after the kind byte: transfer id, index, count, data).</summary>
    internal static bool ValidChunk(ReadOnlySpan<byte> body) =>
        body.Length is > 3 and <= 3 + ChunkData && body[2] is > 0 and <= MaxChunks && body[1] < body[2];

    /// <summary>
    /// Collects the chunks of one client's record (one instance per connection). A chunk of another transfer id or count restarts the collection; a malformed chunk is ignored; a record that does not decode is dropped.
    /// </summary>
    public sealed class Reassembler
    {
        byte _id; byte[]?[] _parts = Array.Empty<byte[]?>(); int _have;

        /// <summary>Takes one chunk body (<c>[transfer][index][count][data]</c>, a command's bytes after the kind byte); returns the decoded record when the last missing part arrives, else null.</summary>
        public WarfrontProgressRecord? Add(ReadOnlySpan<byte> body)
        {
            if (!ValidChunk(body)) return null;
            byte id = body[0], index = body[1], count = body[2];
            if (_parts.Length != count || _id != id) { _id = id; _parts = new byte[count][]; _have = 0; }
            if (_parts[index] is null) { _parts[index] = body[3..].ToArray(); _have++; }
            if (_have < _parts.Length) return null;
            var all = _parts.SelectMany(p => p!).ToArray();
            _parts = Array.Empty<byte[]?>(); _have = 0;
            return TryDecode(all, out var rec) ? rec : null;
        }
    }

    // ---- bytes ----------------------------------------------------------------------------------------------------------------------------

    static void Str(BinaryWriter w, string? s, int maxBytes)
    {
        var text = WarfrontProgressFile.CleanText(s, maxBytes);
        var b = Encoding.UTF8.GetBytes(text);
        w.Write((byte)b.Length); w.Write(b);
    }

    static bool Str(ReadOnlySpan<byte> d, ref int at, int maxBytes, out string s)
    {
        s = "";
        if (at >= d.Length) return false;
        int n = d[at++];
        if (n > maxBytes || at + n > d.Length) return false;
        s = Encoding.UTF8.GetString(d.Slice(at, n)); at += n;
        return true;
    }
}
