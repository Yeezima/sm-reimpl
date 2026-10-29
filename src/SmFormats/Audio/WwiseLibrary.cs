namespace SmFormats.Audio;

/// <summary>A bank loaded from a package (its media stays in the package until read).</summary>
public sealed record LoadedBank(string PackageName, AkpkPackage Package, AkpkEntry Entry, SoundBank Bank);

/// <summary>Where a .wem's bytes live: embedded in a bank (DIDX/DATA) or streamed from a package's stream table.</summary>
public sealed record MediaLocation(uint WemId, string PackageName, string? BankName, bool Streamed, long Size);

/// <summary>One branch of a container in a resolved chain; for switch containers, the switch value it belongs to.</summary>
public sealed record ChainBranch(uint? SwitchValue, bool IsDefault, IReadOnlyList<ChainNode> Nodes);

/// <summary>
/// A node in a resolved chain: the object (null if no loaded bank has it) and its branches. Nodes reached by several
/// paths are shared (the chain is a DAG); <see cref="Truncated"/> marks a cycle, the depth limit or the node budget.
/// </summary>
public sealed record ChainNode(uint Id, HircObject? Object, string? BankName, IReadOnlyList<ChainBranch> Branches, bool Truncated = false);

/// <summary>An action of an event and, for Play, the resolved tree under its target.</summary>
public sealed record ChainAction(HircAction Action, ChainNode? Target);

/// <summary>An event and its actions, as found in the loaded banks.</summary>
public sealed record EventChain(uint EventId, HircEvent? Event, string? BankName, IReadOnlyList<ChainAction> Actions)
{
    /// <summary>Every .wem ID reachable through Play actions, over all switch values, in first-seen order.</summary>
    public IReadOnlyList<uint> WemIds()
    {
        var seen = new HashSet<uint>();
        var visited = new HashSet<ChainNode>(ReferenceEqualityComparer.Instance);
        var list = new List<uint>();
        void Visit(ChainNode n)
        {
            if (!visited.Add(n)) return;                                   // shared subtrees are walked once
            if (n.Object is HircSound s && seen.Add(s.SourceId)) list.Add(s.SourceId);
            foreach (var b in n.Branches) foreach (var c in b.Nodes) Visit(c);
        }
        foreach (var a in Actions) if (a.Target is { } t) Visit(t);
        return list;
    }
}

/// <summary>
/// A set of loaded Wwise packages with their banks indexed together. Banks embed copies of shared structures, so
/// objects are deduplicated by ID (the first loaded bank wins); media is looked up in every bank's DIDX and every
/// package's stream table and read from the package on demand (banks are loaded without their DATA chunk).
/// Packages are opened read-only and owned by the library.
/// </summary>
public sealed class WwiseLibrary : IDisposable
{
    const int MaxDepth = 32;

    /// <summary>Most nodes one resolve creates; beyond it nodes come back truncated.</summary>
    public const int MaxChainNodes = 20_000;

    readonly List<AkpkPackage> _packages = new();
    readonly List<(string Name, AkpkPackage Package)> _named = new();
    readonly List<LoadedBank> _banks = new();
    readonly Dictionary<uint, (HircObject Obj, LoadedBank Bank)> _objects = new();
    readonly Dictionary<uint, LoadedBank> _embedded = new();

    public IReadOnlyList<LoadedBank> Banks => _banks;
    public int ObjectCount => _objects.Count;

    /// <summary>Opens <c>&lt;dir&gt;/&lt;name&gt;.pck</c> for each name that exists; returns the library and the names not found.</summary>
    public static WwiseLibrary Open(string streamingAudioDir, IEnumerable<string> packageNames, out List<string> missing)
    {
        var lib = new WwiseLibrary();
        missing = new List<string>();
        try
        {
            foreach (var name in packageNames)
            {
                string path = Path.Combine(streamingAudioDir, name + ".pck");
                if (File.Exists(path)) lib.AddPackage(path);
                else missing.Add(name);
            }
            return lib;
        }
        catch
        {
            lib.Dispose();
            throw;
        }
    }

    public void AddPackage(string path) => AddPackage(AkpkPackage.Open(path), Path.GetFileNameWithoutExtension(path));

    /// <summary>
    /// Takes ownership of <paramref name="package"/> and loads all of its banks (without media). If a bank does not
    /// parse, the package is disposed and the library is left as it was.
    /// </summary>
    public void AddPackage(AkpkPackage package, string name)
    {
        var loaded = new List<LoadedBank>();
        try
        {
            foreach (var e in package.Banks) loaded.Add(new LoadedBank(name, package, e, package.ReadBank(e, loadMedia: false)));
        }
        catch
        {
            package.Dispose();
            throw;
        }
        _packages.Add(package);
        _named.Add((name, package));
        foreach (var b in loaded)
        {
            _banks.Add(b);
            foreach (var o in b.Bank.Objects) _objects.TryAdd(o.Id, (o, b));
            foreach (var m in b.Bank.Media) _embedded.TryAdd(m.Id, b);
        }
    }

    public HircObject? Find(uint id) => _objects.TryGetValue(id, out var x) ? x.Obj : null;

    public string? BankOf(uint id) => _objects.TryGetValue(id, out var x) ? BankLabel(x.Bank) : null;

    static string BankLabel(LoadedBank b) => b.Bank.Name ?? b.PackageName;

    public IEnumerable<HircEvent> Events => _objects.Values.Select(v => v.Obj).OfType<HircEvent>();

    static byte[] ReadEmbedded(LoadedBank b, uint wemId, int? limit = null)
    {
        b.Bank.TryGetMediaRange(wemId, out long offset, out int size);
        return b.Package.ReadRange(b.Entry.Offset + offset, limit is int l ? Math.Min(l, size) : size);
    }

    bool EmbeddedIsPrefetch(LoadedBank b, uint wemId)
    {
        b.Bank.TryGetMediaRange(wemId, out _, out int size);
        var head = ReadEmbedded(b, wemId, 8);
        if (head.Length < 8) return false;
        uint riff = head.AsSpan().StartsWith("RIFF"u8) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4))
            : head.AsSpan().StartsWith("RIFX"u8) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4)) : 0;
        return riff != 0 && (long)riff + 8 > size;
    }

    /// <summary>
    /// Where a .wem lives. A complete embedded copy wins; a prefetch copy (the bank holds only the start of the file,
    /// so its RIFF size exceeds the embedded bytes) yields to the full streamed file.
    /// </summary>
    public MediaLocation? FindMedia(uint wemId)
    {
        LoadedBank? partial = null;
        if (_embedded.TryGetValue(wemId, out var b))
        {
            b.Bank.TryGetMediaRange(wemId, out _, out int size);
            if (!EmbeddedIsPrefetch(b, wemId)) return new MediaLocation(wemId, b.PackageName, BankLabel(b), false, size);
            partial = b;
        }
        foreach (var (name, p) in _named)
            if (p.FindStream(wemId) is { } e) return new MediaLocation(wemId, name, null, true, e.Size);
        if (partial is null) return null;
        partial.Bank.TryGetMediaRange(wemId, out _, out int partialSize);
        return new MediaLocation(wemId, partial.PackageName, BankLabel(partial), false, partialSize);
    }

    /// <summary>The .wem bytes, embedded or streamed (see <see cref="FindMedia"/>); null if no loaded package has them.</summary>
    public byte[]? ReadMedia(uint wemId)
    {
        LoadedBank? partial = null;
        if (_embedded.TryGetValue(wemId, out var b))
        {
            if (!EmbeddedIsPrefetch(b, wemId)) return ReadEmbedded(b, wemId);
            partial = b;
        }
        foreach (var (_, p) in _named)
            if (p.FindStream(wemId) is { } e) return p.ReadEntry(e);
        return partial is null ? null : ReadEmbedded(partial, wemId);
    }

    /// <summary>True when a RIFF/RIFX header promises more bytes than are present (a prefetch copy).</summary>
    public static bool IsTruncatedRiff(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8) return false;
        uint size = d.StartsWith("RIFF"u8) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(d[4..])
            : d.StartsWith("RIFX"u8) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d[4..]) : 0;
        return size != 0 && (long)size + 8 > d.Length;
    }

    public EventChain ResolveEvent(string name) => ResolveEvent(Fnv.Hash32(name));

    /// <summary>Follows an event through its actions and, for Play actions, down every container to its sounds.</summary>
    public EventChain ResolveEvent(uint eventId)
    {
        if (Find(eventId) is not HircEvent ev) return new EventChain(eventId, null, null, []);
        var resolver = new Resolver(this);
        var actions = new List<ChainAction>();
        foreach (var actionId in ev.ActionIds)
        {
            if (Find(actionId) is not HircAction a) continue;
            ChainNode? target = a.Kind == HircActionKind.Play ? resolver.Node(a.TargetId, 0) : null;
            actions.Add(new ChainAction(a, target));
        }
        return new EventChain(eventId, ev, BankOf(eventId), actions);
    }

    /// <summary>The tree under one sound or container, all switch values included.</summary>
    public ChainNode ResolveNode(uint id) => new Resolver(this).Node(id, 0);

    /// <summary>
    /// One resolve: memoises finished subtrees by ID (containers are shared between switch cases, so the tree is a
    /// DAG), stops cycles and the depth limit, and caps the number of nodes created.
    /// </summary>
    sealed class Resolver(WwiseLibrary lib)
    {
        readonly Dictionary<uint, ChainNode> _done = new();               // finished subtrees without any truncation
        readonly HashSet<uint> _path = new();
        int _created;

        public ChainNode Node(uint id, int depth) => Visit(id, depth, out _);

        ChainNode Visit(uint id, int depth, out bool clean)
        {
            clean = true;
            if (_done.TryGetValue(id, out var memo)) return memo;
            var obj = lib.Find(id);
            string? bank = lib.BankOf(id);
            if (obj is null) return new ChainNode(id, null, null, []);
            if (depth >= MaxDepth || _created >= MaxChainNodes || !_path.Add(id))
            {
                clean = false;
                return new ChainNode(id, obj, bank, [], Truncated: true);
            }
            _created++;
            bool allClean = true;
            List<ChainNode> Children(IEnumerable<uint> ids)
            {
                var list = new List<ChainNode>();
                foreach (var n in ids)
                {
                    list.Add(Visit(n, depth + 1, out bool c));
                    allClean &= c;
                }
                return list;
            }
            var branches = new List<ChainBranch>();
            switch (obj)
            {
                case HircSwitch sw:
                    foreach (var c in sw.Cases) branches.Add(new ChainBranch(c.SwitchId, c.SwitchId == sw.DefaultSwitch, Children(c.NodeIds)));
                    break;
                case HircNode node when node.Children.Count > 0:
                    branches.Add(new ChainBranch(null, false, Children(node.Children)));
                    break;
            }
            _path.Remove(id);
            var result = new ChainNode(id, obj, bank, branches);
            // Only subtrees without a truncation are reused: a cut depends on the path that reached it.
            if (allClean) _done[id] = result;
            clean = allClean;
            return result;
        }
    }

    public void Dispose()
    {
        foreach (var p in _packages) p.Dispose();
        _packages.Clear();
    }
}

/// <summary>Known names for Wwise IDs (hashed with <see cref="Fnv.Hash32"/>); unknown IDs print as hex.</summary>
public sealed class WwiseNames
{
    readonly Dictionary<uint, string> _names = new();

    public int Count => _names.Count;

    public void Add(string name)
    {
        if (!string.IsNullOrWhiteSpace(name)) _names.TryAdd(Fnv.Hash32(name), name);
    }

    public bool TryGet(uint id, out string name) => _names.TryGetValue(id, out name!);

    public string Name(uint id) => _names.TryGetValue(id, out var n) ? n : $"0x{id:x8}";
}
