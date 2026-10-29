using System.Buffers.Binary;
using System.Text;

namespace SmFormats.Bod;

/// <summary>
/// Read-only reader for uncompressed "BOD" binary object data (Space Marine 2011, PC): a string table keyed by 64-bit
/// hashes, then one typed value tree that refers to strings by hash. Layout per docs/discovery.md ("BOD");
/// every material, object definition and manifest in the install, and 2,612 of 2,816 layers, parse to the last byte.
/// Unknown value types (seen: 0xFE/0xFF in navmesh layers and world files) are rejected with their offset.
/// The zlib-compressed variant (flag 1, used by ".O3d", ".Layer" and others) is decompressed first.
/// </summary>
public sealed class BodDocument
{
    static readonly byte[] Magic = [0x42, 0x4F, 0x44, 0xFD];
    const int HeaderSize = 10;
    const int MaxDepth = 64;
    /// <summary>Values per document; the largest file in the install has far fewer. Bounds memory for corrupt input.</summary>
    const int MaxValues = 4_000_000;

    public byte Version { get; }
    public uint HeaderUnknown { get; }
    /// <summary>u32 after the string count; one less than the count in material samples, other values in object trees. Meaning unknown.</summary>
    public uint TableUnknown { get; }
    public IReadOnlyDictionary<ulong, string> Strings { get; }
    public BodValue Root { get; }

    BodDocument(byte version, uint headerUnknown, uint tableUnknown, Dictionary<ulong, string> strings, BodValue root)
    {
        Version = version; HeaderUnknown = headerUnknown; TableUnknown = tableUnknown; Strings = strings; Root = root;
    }

    public static bool HasMagic(ReadOnlySpan<byte> head) => head.StartsWith(Magic);

    /// <summary>
    /// Only the string table (cheap; used where a name is enough, e.g. the bone an ".O3d" armour piece hangs on).
    /// </summary>
    public static IReadOnlyList<string> ReadStrings(ReadOnlySpan<byte> d)
    {
        if (d.Length < HeaderSize + 8 || !HasMagic(d)) throw new InvalidDataException("Missing BOD magic.");
        if (d[5] > 1) throw new NotSupportedException($"BOD compression flag {d[5]} is not supported.");
        byte[] payload;
        int start;
        if (d[5] == 1)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(d[HeaderSize..]);
            if (size < 8 || size > 64 << 20) throw new InvalidDataException($"Implausible decompressed BOD size {size}.");
            payload = new byte[size];
            using var z = new System.IO.Compression.ZLibStream(new MemoryStream(d[(HeaderSize + 4)..].ToArray()), System.IO.Compression.CompressionMode.Decompress);
            try { z.ReadExactly(payload); }
            catch (EndOfStreamException) { throw new InvalidDataException("Compressed BOD payload is shorter than its stated size."); }
            start = 0;
        }
        else { payload = d.ToArray(); start = HeaderSize; }
        var r = new Reader(payload, start);
        int count = r.Count("string count");
        r.U32();
        var strings = new List<string>(Math.Min(count, (payload.Length - r.Pos) / 12));
        for (int i = 0; i < count; i++)
        {
            r.U64();
            strings.Add(Encoding.ASCII.GetString(r.Bytes(r.Count("string length"))));
        }
        return strings;
    }

    public static BodDocument Read(ReadOnlySpan<byte> d)
    {
        if (d.Length < HeaderSize + 8 || !HasMagic(d)) throw new InvalidDataException("Missing BOD magic.");
        byte version = d[4], compressed = d[5];
        uint headerUnknown = BinaryPrimitives.ReadUInt32LittleEndian(d[6..]);
        if (compressed > 1) throw new NotSupportedException($"BOD compression flag {compressed} is not supported.");
        if (compressed == 1)
        {
            // u32 payload size, then one zlib stream holding what follows the header in the uncompressed form.
            int size = BinaryPrimitives.ReadInt32LittleEndian(d[HeaderSize..]);
            if (size < 8 || size > 64 << 20) throw new InvalidDataException($"Implausible decompressed BOD size {size}.");
            var payload = new byte[size];
            using (var z = new System.IO.Compression.ZLibStream(new MemoryStream(d[(HeaderSize + 4)..].ToArray()), System.IO.Compression.CompressionMode.Decompress))
            {
                try { z.ReadExactly(payload); }
                catch (EndOfStreamException) { throw new InvalidDataException("Compressed BOD payload is shorter than its stated size."); }
            }
            return Parse(payload, 0, version, headerUnknown);
        }
        return Parse(d, HeaderSize, version, headerUnknown);
    }

    static BodDocument Parse(ReadOnlySpan<byte> d, int start, byte version, uint headerUnknown)
    {
        var r = new Reader(d, start);
        int count = r.Count("string count");
        uint tableUnknown = r.U32();
        var strings = new Dictionary<ulong, string>(Math.Min(count, (d.Length - r.Pos) / 12));   // each entry takes at least 12 bytes
        for (int i = 0; i < count; i++)
        {
            ulong hash = r.U64();
            int len = r.Count("string length");
            strings[hash] = Encoding.ASCII.GetString(r.Bytes(len));
        }
        var root = r.Value(r.U8(), strings, 0);
        if (r.Pos != d.Length) throw new InvalidDataException($"Value tree ends at {r.Pos}; file is {d.Length} bytes.");
        return new BodDocument(version, headerUnknown, tableUnknown, strings, root);
    }

    ref struct Reader(ReadOnlySpan<byte> d, int pos)
    {
        readonly ReadOnlySpan<byte> _d = d;
        public int Pos = pos;
        int _values;

        void Need(int n, string what)
        {
            if (n < 0 || (long)Pos + n > _d.Length) throw new InvalidDataException($"Truncated {what} at offset {Pos}.");
        }
        public byte U8() { Need(1, "type byte"); return _d[Pos++]; }
        public uint U32() { Need(4, "u32"); var v = BinaryPrimitives.ReadUInt32LittleEndian(_d[Pos..]); Pos += 4; return v; }
        public ulong U64() { Need(8, "hash"); var v = BinaryPrimitives.ReadUInt64LittleEndian(_d[Pos..]); Pos += 8; return v; }
        public ReadOnlySpan<byte> Bytes(int n) { Need(n, "bytes"); var s = _d.Slice(Pos, n); Pos += n; return s; }
        public int Count(string what)
        {
            uint v = U32();
            if (v > _d.Length) throw new InvalidDataException($"Implausible {what} {v} at offset {Pos - 4}.");
            return (int)v;
        }

        public BodValue Value(byte type, Dictionary<ulong, string> strings, int depth)
        {
            if (depth > MaxDepth) throw new InvalidDataException($"Value tree deeper than {MaxDepth} at offset {Pos}.");
            if (++_values > MaxValues) throw new InvalidDataException($"More than {MaxValues:N0} values at offset {Pos}.");
            switch (type)
            {
                case 0x01:
                {
                    var cls = Str(U64(), strings);
                    int n = Count("field count");
                    var fields = new List<BodField>(Math.Min(n, 1024));
                    for (int i = 0; i < n; i++)
                    {
                        var name = Str(U64(), strings);
                        fields.Add(new BodField(name, Value(U8(), strings, depth + 1)));
                    }
                    return new BodStruct(cls, fields);
                }
                case 0x00: return BodNull.Instance;
                case 0x02: return new BodInt((int)U32());
                case 0x04: return new BodBool(U8() != 0);
                case 0x0D: return new BodUInt64(U64());
                case 0x03: return new BodFloat(BitConverter.UInt32BitsToSingle(U32()));
                case 0x05: return Str(U64(), strings) with { IsKey = true };
                case 0x0F: return Str(U64(), strings);
                case 0x07: { uint index = U32(); return new BodRef(index, Value(U8(), strings, depth + 1)); }
                case 0x0B:
                {
                    int n = Count("array length");
                    var items = new List<BodValue>(Math.Min(n, 1024));
                    for (int i = 0; i < n; i++) items.Add(Value(U8(), strings, depth + 1));
                    return new BodArray(items);
                }
                case 0x09:
                {
                    int n = Count("list length");
                    byte flag = U8();   // 1: each item has a key before its value (materials); 0: values only (layers)
                    if (flag > 1) throw new InvalidDataException($"BOD list flag {flag} at offset {Pos - 1}.");
                    var items = new List<BodListItem>(Math.Min(n, 1024));
                    for (int i = 0; i < n; i++)
                    {
                        var key = flag == 1 ? Value(U8(), strings, depth + 1) : BodNull.Instance;
                        // Items are 0x07 references (position, value) in every sample; anything else stands alone.
                        var item = Value(U8(), strings, depth + 1);
                        items.Add(item is BodRef r ? new BodListItem(key, new BodIndex(r.Index), r.Value) : new BodListItem(key, BodNull.Instance, item));
                    }
                    return new BodList(flag, items);
                }
                default: throw new NotSupportedException($"BOD value type 0x{type:X2} at offset {Pos - 1} is not supported yet.");
            }
        }

        static BodString Str(ulong hash, Dictionary<ulong, string> strings) =>
            new(hash, hash == 0 ? null : strings.TryGetValue(hash, out var s) ? s : null);
    }
}

public abstract record BodValue;
/// <summary>Type 0x01: a named class with ordered fields.</summary>
public sealed record BodStruct(BodString Class, IReadOnlyList<BodField> Fields) : BodValue
{
    public BodValue? this[string name] => Fields.FirstOrDefault(f => f.Name.Text == name)?.Value;
}
public sealed record BodField(BodString Name, BodValue Value);
/// <summary>Types 0x0F (string) and 0x05 (list key): a hash into the string table. Hash 0 is the empty/null string.</summary>
public sealed record BodString(ulong Hash, string? Text, bool IsKey = false) : BodValue
{
    public override string ToString() => Text ?? (Hash == 0 ? "" : $"#{Hash:X16}");
}
public sealed record BodInt(int Value) : BodValue;
/// <summary>Type 0x00: no value and no payload.</summary>
public sealed record BodNull : BodValue { public static readonly BodNull Instance = new(); }
public sealed record BodFloat(float Value) : BodValue;
/// <summary>The u32 position carried by a 0x07 value.</summary>
public sealed record BodIndex(uint Value) : BodValue;
/// <summary>Type 0x07: a u32 position (list index, or 0) followed by a nested value.</summary>
public sealed record BodRef(uint Index, BodValue Value) : BodValue;
/// <summary>Type 0x04: one byte.</summary>
public sealed record BodBool(bool Value) : BodValue;
/// <summary>Type 0x0D: eight bytes (object or resource identifiers in layer files).</summary>
public sealed record BodUInt64(ulong Value) : BodValue;
/// <summary>Type 0x0B: typed values, e.g. a 4-float vector.</summary>
public sealed record BodArray(IReadOnlyList<BodValue> Items) : BodValue;
/// <summary>Type 0x09: items, each a 0x07 reference (position and value), preceded by a key when the flag is 1.</summary>
public sealed record BodList(byte Flag, IReadOnlyList<BodListItem> Items) : BodValue;
public sealed record BodListItem(BodValue Key, BodValue Index, BodValue Value);
