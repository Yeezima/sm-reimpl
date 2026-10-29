using System.Buffers.Binary;
using System.Text;

namespace SmFormats.Havok;

/// <summary>
/// Read-only reader for Havok binary packfiles as used by Space Marine (hk_2010.2.0-r1, 32-bit pointers, little-endian):
/// the "hkxx" / "hkt" archive entries. Layout per docs/discovery.md ("Havok packfiles"), written from the documented
/// container structure; no Havok SDK code is used. It resolves pointers through the local and global fixup tables and
/// identifies objects through the virtual fixup table, so class readers can follow pointers by offset.
/// </summary>
public sealed class HavokPackfile
{
    static readonly byte[] Magic = [0x57, 0xE0, 0xE0, 0x57, 0x10, 0xC0, 0xC0, 0x10];
    const int HeaderSize = 0x40, SectionHeaderSize = 0x30;

    readonly byte[] _file;
    readonly Dictionary<int, (int Section, int Offset)> _pointers = new();

    public string ContentsVersion { get; }
    public IReadOnlyList<HavokSection> Sections { get; }
    /// <summary>Objects in the data section, in file order: offset within that section and class name.</summary>
    public IReadOnlyList<HavokObject> Objects { get; }
    public int DataSectionIndex { get; }

    public static bool HasMagic(ReadOnlySpan<byte> head) => head.StartsWith(Magic);

    public HavokPackfile(byte[] file)
    {
        _file = file;
        if (file.Length < HeaderSize || !HasMagic(file)) throw new InvalidDataException("Missing Havok packfile magic.");
        byte pointerSize = file[0x10], littleEndian = file[0x11];
        if (pointerSize != 4 || littleEndian != 1)
            throw new NotSupportedException($"Packfile layout (pointer size {pointerSize}, little-endian {littleEndian}) is not supported; only 32-bit little-endian.");
        int sectionCount = I32(0x14);
        if (sectionCount is < 1 or > 16) throw new InvalidDataException($"Implausible section count {sectionCount}.");
        ContentsVersion = CString(0x28, 16);

        var sections = new List<HavokSection>(sectionCount);
        for (int s = 0; s < sectionCount; s++)
        {
            int h = HeaderSize + s * SectionHeaderSize;
            Need(h, SectionHeaderSize);
            string tag = CString(h, 19);
            int abs = I32(h + 20);
            // local, global, virtual, exports, imports, end: offsets relative to abs, non-decreasing
            int[] rel = new int[6];
            for (int k = 0; k < 6; k++) rel[k] = I32(h + 24 + 4 * k);
            for (int k = 1; k < 6; k++)
                if (rel[k - 1] > rel[k]) throw new InvalidDataException($"Section '{tag}': fixup table offsets are out of order.");
            if (abs < 0 || rel[0] < 0 || (long)abs + rel[5] > file.Length) throw new InvalidDataException($"Section '{tag}' extends past the file.");
            sections.Add(new HavokSection(tag, abs, rel[0], rel[1], rel[2], rel[3], rel[5]));
        }
        Sections = sections;
        DataSectionIndex = sections.FindIndex(s => s.Tag == "__data__");
        int classSection = sections.FindIndex(s => s.Tag == "__classnames__");
        if (DataSectionIndex < 0 || classSection < 0) throw new InvalidDataException("Packfile has no __data__ or __classnames__ section.");

        var classNames = ReadClassNames(sections[classSection]);
        var data = sections[DataSectionIndex];

        for (int o = data.LocalFixups; o + 8 <= data.GlobalFixups; o += 8)
        {
            int src = I32(data.Absolute + o), dst = I32(data.Absolute + o + 4);
            if (src == -1) continue;   // padding
            CheckInside(data, src, "local fixup source"); CheckInside(data, dst, "local fixup target");
            _pointers[src] = (DataSectionIndex, dst);
        }
        for (int o = data.GlobalFixups; o + 12 <= data.VirtualFixups; o += 12)
        {
            int src = I32(data.Absolute + o), section = I32(data.Absolute + o + 4), dst = I32(data.Absolute + o + 8);
            if (src == -1) continue;
            if (section < 0 || section >= sections.Count) throw new InvalidDataException($"Global fixup to missing section {section}.");
            CheckInside(data, src, "global fixup source"); CheckInside(sections[section], dst, "global fixup target");
            _pointers[src] = (section, dst);
        }
        var objects = new List<HavokObject>();
        for (int o = data.VirtualFixups; o + 12 <= data.Exports; o += 12)
        {
            int src = I32(data.Absolute + o), section = I32(data.Absolute + o + 4), nameOffset = I32(data.Absolute + o + 8);
            if (src == -1) continue;
            if (section != classSection || !classNames.TryGetValue(nameOffset, out var cls))
                throw new InvalidDataException($"Virtual fixup at {src} names an unknown class (section {section}, offset {nameOffset}).");
            CheckInside(data, src, "object");
            objects.Add(new HavokObject(src, cls));
        }
        Objects = objects;
    }

    Dictionary<int, string> ReadClassNames(HavokSection s)
    {
        // Entries: u32 signature, u8 0x09, null-terminated name. The table is padded with 0xFF bytes; a signature may
        // itself start with 0xFF, so the end is where the 0x09 marker is missing.
        var names = new Dictionary<int, string>();
        int p = 0, end = s.LocalFixups;
        while (p + 6 <= end && _file[s.Absolute + p + 4] == 0x09)
        {
            int start = p + 5, z = start;
            while (z < end && _file[s.Absolute + z] != 0) z++;
            if (z >= end) throw new InvalidDataException("Class name table: unterminated name.");
            names[start] = Encoding.ASCII.GetString(_file, s.Absolute + start, z - start);
            p = z + 1;
        }
        return names;
    }

    public IEnumerable<HavokObject> OfClass(string className) => Objects.Where(o => o.ClassName == className);

    // ---- reading inside the data section (offsets are relative to the section start) ----

    HavokSection Data => Sections[DataSectionIndex];

    public int ReadInt32(int offset) { CheckInside(Data, offset + 3, "read"); return I32(Data.Absolute + offset); }
    public uint ReadUInt32(int offset) => (uint)ReadInt32(offset);
    public short ReadInt16(int offset) { CheckInside(Data, offset + 1, "read"); return BinaryPrimitives.ReadInt16LittleEndian(_file.AsSpan(Data.Absolute + offset)); }
    public byte ReadByte(int offset) { CheckInside(Data, offset, "read"); return _file[Data.Absolute + offset]; }
    public float ReadSingle(int offset) { CheckInside(Data, offset + 3, "read"); return BinaryPrimitives.ReadSingleLittleEndian(_file.AsSpan(Data.Absolute + offset)); }
    public ReadOnlySpan<byte> ReadBytes(int offset, int count)
    {
        if (count < 0) throw new InvalidDataException("Negative byte count.");
        if (count > 0) CheckInside(Data, offset + count - 1, "read");
        return _file.AsSpan(Data.Absolute + offset, count);
    }

    /// <summary>The data-section offset a pointer field points to, or null for a null pointer.</summary>
    public int? Pointer(int fieldOffset)
    {
        if (!_pointers.TryGetValue(fieldOffset, out var target)) return null;
        if (target.Section != DataSectionIndex) throw new NotSupportedException("Pointer into a section other than __data__.");
        return target.Offset;
    }

    /// <summary>hkArray: pointer, u32 size, u32 capacity and flags (12 bytes on 32-bit). The items must fit in the section.</summary>
    public (int? Data, int Count) Array(int fieldOffset, int elementSize = 1)
    {
        int count = ReadInt32(fieldOffset + 4);
        if (count < 0 || count > Data.Size) throw new InvalidDataException($"Implausible array size {count} at {fieldOffset}.");
        int? data = Pointer(fieldOffset);
        if (count > 0 && data is null) throw new InvalidDataException($"Array at {fieldOffset} has {count} items but no data pointer.");
        if (data is { } at && (long)at + (long)count * elementSize > Data.Size)
            throw new InvalidDataException($"Array at {fieldOffset}: {count} items of {elementSize} bytes run past the section.");
        return (data, count);
    }

    /// <summary>hkStringPtr / char*: pointer to a null-terminated string.</summary>
    public string? String(int fieldOffset)
    {
        if (Pointer(fieldOffset) is not { } p) return null;
        int z = p;
        while (true) { CheckInside(Data, z, "string"); if (_file[Data.Absolute + z] == 0) break; z++; }
        return Encoding.ASCII.GetString(_file, Data.Absolute + p, z - p);
    }

    int I32(int abs) { Need(abs, 4); return BinaryPrimitives.ReadInt32LittleEndian(_file.AsSpan(abs)); }
    string CString(int abs, int max)
    {
        Need(abs, max);
        int n = System.Array.IndexOf(_file, (byte)0, abs, max);
        return Encoding.ASCII.GetString(_file, abs, (n < 0 ? abs + max : n) - abs);
    }
    void Need(int abs, int count) { if (abs < 0 || (long)abs + count > _file.Length) throw new InvalidDataException($"Truncated packfile at {abs}."); }
    static void CheckInside(HavokSection s, int offset, string what)
    {
        if (offset < 0 || offset >= s.Size) throw new InvalidDataException($"{what} offset {offset} outside section '{s.Tag}' ({s.Size} bytes).");
    }
}

/// <param name="Absolute">File offset of the section.</param>
/// <param name="LocalFixups">Offsets relative to <paramref name="Absolute"/>; the section's object data is [0, LocalFixups).</param>
public sealed record HavokSection(string Tag, int Absolute, int LocalFixups, int GlobalFixups, int VirtualFixups, int Exports, int End)
{
    public int Size => LocalFixups;
}

public sealed record HavokObject(int Offset, string ClassName);
