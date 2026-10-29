using System.Buffers.Binary;
using System.Text;

namespace SmFormats.Baf;

/// <summary>
/// Read-only reader for "BAF" attribute files (".attr_pc"): gameplay tuning as a tree of named tables.
/// Layout per docs/discovery.md ("BAF attributes"), verified on the game's files:
/// header (magic, version 100, hash, counts and offsets), a table section (count, first entry; -1 when empty),
/// an entry section (type, name string index, value), and a string pool with an offset table.
/// Value types: 0 bool, 1 float, 2 int, 3 string (index), 4 table (index).
/// </summary>
public sealed class BafDocument
{
    static readonly byte[] Magic = "BAF "u8.ToArray();
    const int HeaderSize = 45;
    const int MaxDepth = 64;

    readonly int[] _tableCount, _tableFirst;
    readonly BafType[] _type;
    readonly int[] _name, _value;
    readonly string[] _strings;

    public uint Version { get; }
    public uint Hash { get; }
    public BafTable Root => new(this, 0);
    public int TableCount => _tableCount.Length;
    public int EntryCount => _type.Length;

    BafDocument(uint version, uint hash, int[] tableCount, int[] tableFirst, BafType[] type, int[] name, int[] value, string[] strings)
    {
        Version = version; Hash = hash; _tableCount = tableCount; _tableFirst = tableFirst;
        _type = type; _name = name; _value = value; _strings = strings;
    }

    public static bool HasMagic(ReadOnlySpan<byte> head) => head.StartsWith(Magic);

    public static BafDocument Read(ReadOnlySpan<byte> d)
    {
        if (d.Length < HeaderSize || !HasMagic(d)) throw new InvalidDataException("Missing BAF magic.");
        uint version = U32(d, 4), hash = U32(d, 8);
        int tablesAt = I32(d, 17), tableCount = I32(d, 21), entriesAt = I32(d, 25), entryCount = I32(d, 29);
        int stringCount = I32(d, 33), stringOffsetsAt = I32(d, 37), poolAt = I32(d, 41);
        Section(d, tablesAt, tableCount, 8, "table");
        Section(d, entriesAt, entryCount, 12, "entry");
        Section(d, stringOffsetsAt, stringCount, 4, "string offset");
        if (poolAt < 0 || poolAt > d.Length) throw new InvalidDataException($"String pool offset {poolAt} outside the file.");
        if (tableCount < 1) throw new InvalidDataException("BAF has no root table.");

        var strings = new string[stringCount];
        for (int i = 0; i < stringCount; i++)
        {
            long at = (long)poolAt + I32(d, stringOffsetsAt + 4 * i);
            if (at < poolAt || at >= d.Length) throw new InvalidDataException($"String {i} starts outside the pool.");
            int end = d[(int)at..].IndexOf((byte)0);
            if (end < 0) throw new InvalidDataException($"String {i} is not terminated.");
            strings[i] = Encoding.ASCII.GetString(d.Slice((int)at, end));
        }

        var tCount = new int[tableCount]; var tFirst = new int[tableCount];
        for (int i = 0; i < tableCount; i++)
        {
            tCount[i] = I32(d, tablesAt + 8 * i);
            tFirst[i] = I32(d, tablesAt + 8 * i + 4);
            if (tCount[i] < 0 || (tCount[i] > 0 && (tFirst[i] < 0 || (long)tFirst[i] + tCount[i] > entryCount)))
                throw new InvalidDataException($"Table {i}: {tCount[i]} entries from {tFirst[i]} outside {entryCount}.");
        }
        var type = new BafType[entryCount]; var name = new int[entryCount]; var value = new int[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            int o = entriesAt + 12 * i;
            int t = I32(d, o);
            if (t is < 0 or > 4) throw new InvalidDataException($"Entry {i}: unknown value type {t}.");
            type[i] = (BafType)t; name[i] = I32(d, o + 4); value[i] = I32(d, o + 8);
            if (name[i] < 0 || name[i] >= stringCount) throw new InvalidDataException($"Entry {i}: name index {name[i]} of {stringCount}.");
            if (type[i] == BafType.String && (value[i] < 0 || value[i] >= stringCount)) throw new InvalidDataException($"Entry {i}: string index {value[i]} of {stringCount}.");
            if (type[i] == BafType.Table && (value[i] <= 0 || value[i] >= tableCount)) throw new InvalidDataException($"Entry {i}: table index {value[i]} of {tableCount}.");
        }
        var doc = new BafDocument(version, hash, tCount, tFirst, type, name, value, strings);
        doc.CheckTree();
        return doc;
    }

    /// <summary>Every child table is reachable from the root once, so walking the tree terminates.</summary>
    void CheckTree()
    {
        var seen = new bool[_tableCount.Length];
        var stack = new Stack<(int Table, int Depth)>();
        stack.Push((0, 0));
        seen[0] = true;
        while (stack.Count > 0)
        {
            var (table, depth) = stack.Pop();
            if (depth > MaxDepth) throw new InvalidDataException($"Table nesting deeper than {MaxDepth}.");
            for (int k = 0; k < _tableCount[table]; k++)
            {
                int e = _tableFirst[table] + k;
                if (_type[e] != BafType.Table) continue;
                if (seen[_value[e]]) throw new InvalidDataException($"Table {_value[e]} is referenced more than once.");
                seen[_value[e]] = true;
                stack.Push((_value[e], depth + 1));
            }
        }
    }

    internal IEnumerable<BafEntry> Entries(int table)
    {
        for (int k = 0; k < _tableCount[table]; k++)
        {
            int e = _tableFirst[table] + k;
            yield return new BafEntry(this, _strings[_name[e]], _type[e], _value[e]);
        }
    }

    internal string StringAt(int index) => _strings[index];

    static void Section(ReadOnlySpan<byte> d, int at, int count, int size, string what)
    {
        if (at < HeaderSize || count < 0 || (long)at + (long)count * size > d.Length)
            throw new InvalidDataException($"BAF {what} section ({count} × {size} bytes at {at}) outside the file.");
    }
    static int I32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d[o..]);
    static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
}

public enum BafType { Bool = 0, Float = 1, Int = 2, String = 3, Table = 4 }

/// <summary>A named table. Lookups are by entry name; "$ref" names the template a table derives from.</summary>
public readonly struct BafTable(BafDocument doc, int index)
{
    public int Index { get; } = index;
    public IEnumerable<BafEntry> Entries => doc.Entries(Index);
    public BafEntry? this[string name] => Entries.Cast<BafEntry?>().FirstOrDefault(e => e!.Value.Name == name);

    /// <summary>Follows a path of table names, e.g. "extensions/biped_locomotion_ext/speed_standing".</summary>
    public BafTable? Table(string path)
    {
        BafTable? t = this;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (t?[part] is not { Type: BafType.Table } e) return null;
            t = e.AsTable();
        }
        return t;
    }

    public float? Float(string name) => this[name] is { Type: BafType.Float } e ? e.AsFloat() : null;
    public string? String(string name) => this[name] is { Type: BafType.String } e ? e.AsString() : null;
}

public readonly struct BafEntry(BafDocument doc, string name, BafType type, int raw)
{
    public string Name { get; } = name;
    public BafType Type { get; } = type;
    public int Raw { get; } = raw;
    public float AsFloat() => BitConverter.Int32BitsToSingle(Raw);
    public bool AsBool() => Raw != 0;
    public string AsString() => doc.StringAt(Raw);
    public BafTable AsTable() => new(doc, Raw);
    public override string ToString() => Type switch
    {
        BafType.Bool => $"{Name} = {AsBool()}",
        BafType.Float => $"{Name} = {AsFloat()}",
        BafType.Int => $"{Name} = {Raw}",
        BafType.String => $"{Name} = \"{AsString()}\"",
        _ => $"{Name} = <table {Raw}>",
    };
}
