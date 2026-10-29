using System.Buffers.Binary;
using System.Numerics;

namespace SmFormats.Fx;

/// <summary>
/// One sprite's place in the effects atlas: the BOD string hash of its material name, its rectangle as fractions of the
/// atlas (<see cref="X"/>, <see cref="Y"/> top-left; <see cref="Width"/>, <see cref="Height"/>), and flags (bit 0: the sprite is
/// stored turned 90 degrees, <see cref="Rotated"/>; which way is not established). <see cref="Name"/> is filled when the
/// hash is found in a string table (<see cref="FxTexturePage.WithNames"/>), else empty.
/// </summary>
public sealed record FxAtlasRect(ulong Hash, float X, float Y, float Width, float Height, uint Flags, string Name = "")
{
    public bool Rotated => (Flags & 1) != 0;
    /// <summary>Top-left and bottom-right in atlas UV (0..1).</summary>
    public (Vector2 Min, Vector2 Max) Uv => (new Vector2(X, Y), new Vector2(X + Width, Y + Height));
}

/// <summary>
/// The effects atlas index (<c>master_fx.tpage</c>, M25a; layout in docs/discovery.md): a u32 count, a u32 (0 in the install),
/// then per sprite 32 bytes: u64 name hash, f32 width, height, x, y (fractions of the atlas), u32 flags, u32 (0). The keys are
/// the same 64-bit hashes BOD string tables carry, so a PSystem's <see cref="FxMaterial.Hash"/> finds its rect without the
/// hash function. The atlas texture is the archive's <c>dds</c> entry of the same name (<see cref="AtlasTexture"/>), loaded at
/// runtime from the install.
/// </summary>
public sealed class FxTexturePage
{
    const int EntryBytes = 32;
    /// <summary>A bound for corrupt files (the install's page has 230 rects).</summary>
    public const int MaxRects = 65536;

    readonly Dictionary<ulong, FxAtlasRect> _byHash;
    public IReadOnlyList<FxAtlasRect> Rects { get; }
    /// <summary>The u32 after the count (0 in the install; meaning unknown).</summary>
    public uint Reserved { get; }
    /// <summary>The atlas texture's entry name (the page's own name; <c>master_fx</c> in the install).</summary>
    public string AtlasTexture { get; }

    FxTexturePage(IReadOnlyList<FxAtlasRect> rects, uint reserved, string atlas)
    {
        Rects = rects; Reserved = reserved; AtlasTexture = atlas;
        _byHash = new Dictionary<ulong, FxAtlasRect>();
        foreach (var r in rects) _byHash.TryAdd(r.Hash, r);   // the first rect wins for a repeated hash
    }

    public static FxTexturePage Read(ReadOnlySpan<byte> d, string atlasTexture = "master_fx")
    {
        if (d.Length < 8) throw new InvalidDataException("Texture page shorter than its header.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(d);
        if (count > MaxRects || d.Length != 8 + (long)count * EntryBytes)
            throw new InvalidDataException($"Texture page of {d.Length} bytes does not hold {count} rects of {EntryBytes} bytes.");
        var rects = new FxAtlasRect[count];
        for (int i = 0; i < count; i++)
        {
            var e = d.Slice(8 + i * EntryBytes, EntryBytes);
            float w = F(e, 8), h = F(e, 12), x = F(e, 16), y = F(e, 20);
            if (!(float.IsFinite(w) && float.IsFinite(h) && float.IsFinite(x) && float.IsFinite(y)))
                throw new InvalidDataException($"Texture page rect {i} is not finite.");
            rects[i] = new FxAtlasRect(BinaryPrimitives.ReadUInt64LittleEndian(e), x, y, w, h, BinaryPrimitives.ReadUInt32LittleEndian(e[24..]));
        }
        return new FxTexturePage(rects, BinaryPrimitives.ReadUInt32LittleEndian(d[4..]), atlasTexture);
        static float F(ReadOnlySpan<byte> e, int at) => BinaryPrimitives.ReadSingleLittleEndian(e[at..]);
    }

    /// <summary>The rect for a material name's hash, or null (a name the page lacks, e.g. one whose case differs: hashes are case-sensitive).</summary>
    public FxAtlasRect? Find(ulong hash) => _byHash.GetValueOrDefault(hash);

    /// <summary>The rect for a material, or null for an empty or unknown one.</summary>
    public FxAtlasRect? Find(FxMaterial material) => material.IsEmpty ? null : Find(material.Hash);

    /// <summary>This page with names from string tables (hash → text, e.g. the PSystems' <c>BodDocument.Strings</c>).</summary>
    public FxTexturePage WithNames(IReadOnlyDictionary<ulong, string> strings) =>
        new(Rects.Select(r => strings.TryGetValue(r.Hash, out var n) ? r with { Name = n } : r).ToList(), Reserved, AtlasTexture);
}
