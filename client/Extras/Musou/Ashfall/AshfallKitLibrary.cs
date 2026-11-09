using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using SmSim.Extras.Musou.Ashfall;

namespace SmClient;

/// <summary>
/// Runtime loader of the Ashfall kit's models (the owner's generated library, private folder <c>%LOCALAPPDATA%\sm-reimpl\ashfall-kit\source\</c>; never in the repository).
/// Each GLB is read with Godot's <see cref="GltfDocument"/> on a worker thread (no editor import), its meshes get LODs (<see cref="ImporterMesh.GenerateLods"/>) and its textures
/// are shrunk to <see cref="TextureMax"/> and block-compressed, then the result is cached once per asset id. A missing folder or file, or a file that fails, makes that
/// asset <c>Failed</c> (one log line each) and never throws. **Placeholder demonstration** (the models are the owner's generated draft assets).
/// </summary>
public sealed class AshfallKitLibrary
{
    /// <summary>One mesh of a model with its transform inside the glTF file's own frame (Y up).</summary>
    public sealed record Piece(Mesh Mesh, Transform3D Local, int Lods);

    /// <summary>A loaded model: its pieces and what loading cost.</summary>
    public sealed class Asset
    {
        public required string Id { get; init; }
        public Piece[] Pieces { get; init; } = [];
        public string? Error { get; init; }
        public bool Ok => Error is null && Pieces.Length > 0;
        public double Ms { get; init; }
        public int Triangles { get; init; }
        public int LodSurfaces { get; init; }
        public long TextureBytes { get; init; }
        public string TextureNote { get; init; } = "";
        /// <summary>Mean luminance of the model's base colour (texture mean times colour factor, per surface, averaged); 0 when unmeasured.</summary>
        public float Luma { get; init; }
    }

    /// <summary>The kit folder a tester build carries next to its executable (<c>game\ashfall-kit\</c>, the owner's own generated models, bundled by
    /// tools/release/release.ps1 since 2026-11-06 at the owner's request); it may not exist.</summary>
    public static string BundledRoot => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Godot.OS.GetExecutablePath()) ?? "", AshfallKit.PrivateFolder);

    /// <summary>The private library's root (<c>%LOCALAPPDATA%\sm-reimpl\ashfall-kit\source\</c>).</summary>
    public static string PrivateRoot => System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", AshfallKit.PrivateFolder, "source");

    /// <summary>The library's root: the bundled folder when the build carries one, else the private folder; the folder may not exist.</summary>
    public static string Root => System.IO.Directory.Exists(BundledRoot) ? BundledRoot : PrivateRoot;

    /// <summary>The weathered copies of the kit (<c>%LOCALAPPDATA%\sm-reimpl\ashfall-kit\processed\</c>, baked by tools/blender/ashfall_bake.py; same relative paths as the source). A bundled folder
    /// (<see cref="BundledRoot"/>) is used as-is: whatever the release copied there is what loads.</summary>
    public static string ProcessedRoot => System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", AshfallKit.PrivateFolder, "processed");

    /// <summary>Whether processed copies are preferred: <c>--ashfall-kit-files processed|source</c> (default processed; owner's decision 2026-11-06).</summary>
    public static bool UseProcessed
    {
        get
        {
            if (_useProcessed is { } v) return v;
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--ashfall-kit-files");
            _useProcessed = !(i >= 0 && i + 1 < args.Length && args[i + 1] == "source");
            return _useProcessed.Value;
        }
    }
    static bool? _useProcessed;
    static int _processedFound;
    static int _sourceUsed;
    static bool _rootLogged;

    /// <summary>The file to read for a catalogue path: the processed copy when enabled and present, else the source file (or the bundled one).</summary>
    static string ResolvePath(string file)
    {
        string rel = file.Replace('/', System.IO.Path.DirectorySeparatorChar);
        string path = System.IO.Path.Combine(Root, rel);
        if (UseProcessed && !System.IO.Directory.Exists(BundledRoot))
        {
            string processed = System.IO.Path.Combine(ProcessedRoot, rel);
            if (System.IO.File.Exists(processed)) { Interlocked.Increment(ref _processedFound); LogRootOnce(); return processed; }
        }
        Interlocked.Increment(ref _sourceUsed);
        LogRootOnce();
        return path;
    }

    static int CountProcessed()
    {
        try { return System.IO.Directory.Exists(ProcessedRoot) ? System.IO.Directory.EnumerateFiles(ProcessedRoot, "*.glb", System.IO.SearchOption.AllDirectories).Count() : 0; }
        catch (Exception) { return 0; }
    }

    static void LogRootOnce()
    {
        if (_rootLogged) return;
        _rootLogged = true;
        GD.Print($"ashfall kit: files from {(UseProcessed && !System.IO.Directory.Exists(BundledRoot) ? ProcessedRoot + " (processed copies, falling back to " + Root + ")" : Root)}; switch --ashfall-kit-files processed|source; processed copies on disk: {CountProcessed()}");
    }

    /// <summary>How many models were read from processed copies and from the source folder so far (for the load summary).</summary>
    public static (int Processed, int Source) FileCounts => (Volatile.Read(ref _processedFound), Volatile.Read(ref _sourceUsed));

    /// <summary>Whether the private folder exists at all (the look defaults to the graybox when it does not).</summary>
    public static bool Available => System.IO.Directory.Exists(Root);

    /// <summary>Largest texture side kept (0: as delivered). <c>--ashfall-tex N</c>; ours.</summary>
    public int TextureMax { get; set; } = 1024;
    /// <summary>Whether LODs are generated at load. <c>--ashfall-lods off</c> turns it off.</summary>
    public bool GenerateLods { get; set; } = true;
    /// <summary>The models drawn inside the battle arena (kit-hi-res, owner 2026-11-09: "all the 3d assets used in the main play map are high res"): their textures keep <see cref="HighTextureMax"/>.
    /// Fill before <see cref="Request"/>; empty, every model gets <see cref="TextureMax"/>.</summary>
    public HashSet<string> HighDetail { get; } = [];
    /// <summary>Largest texture side kept for <see cref="HighDetail"/> models (ours: the source files' 2048).</summary>
    public int HighTextureMax { get; set; } = AshfallKitDetail.ArenaTextureMax;

    readonly ConcurrentDictionary<string, Asset> _done = new();
    readonly ConcurrentQueue<string> _ready = new();
    static readonly List<string> NoneReady = [];
    int _pending;
    long _startTicks;
    long _endTicks;

    /// <summary>Assets requested and not finished yet.</summary>
    public int Pending => Volatile.Read(ref _pending);
    public int Loaded => _done.Count(a => a.Value.Ok);
    public int Failed => _done.Count(a => !a.Value.Ok);
    public IEnumerable<Asset> All => _done.Values;
    /// <summary>Wall time from the first request to the last finish, in ms (0 while nothing was requested).</summary>
    public double WallMs => _endTicks == 0 ? 0 : (_endTicks - _startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    public Asset? Get(string id) => _done.TryGetValue(id, out var a) ? a : null;

    /// <summary>Ids that finished since the last call (loaded or failed), oldest first.</summary>
    public List<string> TakeReady()
    {
        if (_ready.IsEmpty) return NoneReady;   // no allocation on the frames with nothing new (review)
        var list = new List<string>();
        while (_ready.TryDequeue(out var id)) list.Add(id);
        return list;
    }

    /// <summary>Starts loading the ids not asked for before, on at most <paramref name="workers"/> background threads; returns at once.</summary>
    public void Request(IEnumerable<string> ids, int workers = 3)
    {
        var todo = ids.Distinct().Where(id => !_done.ContainsKey(id) && !_requested.Contains(id)).ToList();
        if (todo.Count == 0) return;
        foreach (var id in todo) _requested.Add(id);
        if (Interlocked.CompareExchange(ref _startTicks, System.Diagnostics.Stopwatch.GetTimestamp(), 0) != 0) { /* a later batch keeps the first start */ }
        Interlocked.Add(ref _pending, todo.Count);
        Interlocked.Exchange(ref _endTicks, 0);
        var queue = new ConcurrentQueue<string>(todo);
        int n = Math.Clamp(workers, 1, todo.Count);
        for (int i = 0; i < n; i++)
            Task.Run(() =>
            {
                while (queue.TryDequeue(out var id))
                {
                    Asset asset;
                    try { asset = LoadOne(id); }
                    catch (Exception e) { asset = new Asset { Id = id, Error = e.GetType().Name }; }   // LoadOne lets OutOfMemory through: still count the asset as done, so Pending reaches 0
                    _done[id] = asset;
                    _ready.Enqueue(id);
                    if (Interlocked.Decrement(ref _pending) == 0) Interlocked.Exchange(ref _endTicks, System.Diagnostics.Stopwatch.GetTimestamp());
                }
            });
    }

    /// <summary>Ids asked for so far; <see cref="Request"/> is called from the main thread only.</summary>
    readonly HashSet<string> _requested = [];

    /// <summary>Loads one model on the calling thread (what the workers run); never throws.</summary>
    public Asset LoadOne(string id)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var model = AshfallKit.Models.FirstOrDefault(m => m.Id == id);
            if (model is null) return Fail(id, "not in the catalogue", watch);
            string path = ResolvePath(model.File);
            if (!System.IO.File.Exists(path)) return Fail(id, "file missing: " + model.File, watch);
            var doc = new GltfDocument();
            var state = new GltfState();
            var err = doc.AppendFromFile(path, state);
            if (err != Error.Ok) return Fail(id, "glTF read failed: " + err, watch);
            var nodes = state.GetNodes();
            var meshes = state.GetMeshes();
            var pieces = new List<Piece>();
            int triangles = 0, lodSurfaces = 0;
            foreach (var (node, index) in nodes.Select((n, i) => (n, i)))
            {
                if (node.Mesh < 0 || node.Mesh >= meshes.Count) continue;
                var importer = meshes[node.Mesh].Mesh;
                if (importer is null) continue;
                if (GenerateLods) importer.GenerateLods(60f, 25f, []);
                var mesh = importer.GetMesh();
                if (mesh is null) continue;
                int lods = 0;
                for (int s = 0; s < importer.GetSurfaceCount(); s++)
                {
                    triangles += importer.GetSurfaceArrays(s)[(int)Mesh.ArrayType.Index].AsInt32Array().Length / 3;
                    lods = Math.Max(lods, importer.GetSurfaceLodCount(s));
                    lodSurfaces += importer.GetSurfaceLodCount(s);
                }
                pieces.Add(new Piece(mesh, WorldOf(nodes, index), lods));
            }
            if (pieces.Count == 0) return Fail(id, "no mesh in the file", watch);
            float luma = MeasureLuma(pieces);
            var (bytes, note) = ShrinkTextures(pieces, HighDetail.Contains(id) ? HighTextureMax : TextureMax);
            return new Asset { Id = id, Pieces = [.. pieces], Ms = watch.Elapsed.TotalMilliseconds, Triangles = triangles, LodSurfaces = lodSurfaces, TextureBytes = bytes, TextureNote = note, Luma = luma };
        }
        catch (Exception e) when (e is not (StackOverflowException or AccessViolationException or OutOfMemoryException))
        {
            return Fail(id, e.GetType().Name + ": " + e.Message, watch);
        }
    }

    Asset Fail(string id, string why, System.Diagnostics.Stopwatch watch)
    {
        GD.Print($"ashfall kit: {id}: {why}; the graybox is drawn for it");
        return new Asset { Id = id, Error = why, Ms = watch.Elapsed.TotalMilliseconds };
    }

    static Transform3D WorldOf(Godot.Collections.Array<GltfNode> nodes, int index)
    {
        var t = Transform3D.Identity;
        for (int i = index, guard = 0; i >= 0 && guard < 64; i = nodes[i].Parent, guard++) t = nodes[i].Xform * t;
        return t;
    }

    static readonly BaseMaterial3D.TextureParam[] Slots =
    [
        BaseMaterial3D.TextureParam.Albedo, BaseMaterial3D.TextureParam.Normal, BaseMaterial3D.TextureParam.Metallic, BaseMaterial3D.TextureParam.Roughness,
        BaseMaterial3D.TextureParam.AmbientOcclusion, BaseMaterial3D.TextureParam.Emission,
    ];

    /// <summary>Mean base-colour luminance (0..1, linear-ish sRGB values) over the model's surfaces: a 16 x 16 downsample of each albedo texture times the material's colour.</summary>
    static float MeasureLuma(List<Piece> pieces)
    {
        double sum = 0; int n = 0;
        var cache = new Dictionary<Texture2D, float>();
        foreach (var piece in pieces)
            for (int s = 0; s < piece.Mesh.GetSurfaceCount(); s++)
            {
                if (piece.Mesh.SurfaceGetMaterial(s) is not BaseMaterial3D m) continue;
                var c = m.AlbedoColor;
                float colour = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
                float tex = 1f;
                if (m.GetTexture(BaseMaterial3D.TextureParam.Albedo) is { } t)
                {
                    if (!cache.TryGetValue(t, out tex))
                    {
                        tex = 0.5f;
                        try
                        {
                            var img = t.GetImage();
                            if (img is not null)
                            {
                                if (img.IsCompressed()) img.Decompress();
                                img.Resize(16, 16, Image.Interpolation.Bilinear);
                                double a = 0;
                                for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) { var p = img.GetPixel(x, y); a += 0.299 * p.R + 0.587 * p.G + 0.114 * p.B; }
                                tex = (float)(a / 256);
                            }
                        }
                        catch (Exception) { }
                        cache[t] = tex;
                    }
                }
                else colour *= 0.49f / 1f;   // the no-albedo stone tint below
                sum += tex * colour; n++;
            }
        return n == 0 ? 0f : (float)(sum / n);
    }

    /// <summary>Resizes each material's textures to <paramref name="textureMax"/> and block-compresses them; returns the VRAM estimate and a note.</summary>
    (long Bytes, string Note) ShrinkTextures(List<Piece> pieces, int textureMax)
    {
        long bytes = 0;
        var replaced = new Dictionary<Texture2D, Texture2D>();
        var sizes = new List<string>();
        foreach (var piece in pieces)
            for (int s = 0; s < piece.Mesh.GetSurfaceCount(); s++)
            {
                if (piece.Mesh.SurfaceGetMaterial(s) is not BaseMaterial3D m) continue;
                if (m.GetTexture(BaseMaterial3D.TextureParam.Albedo) is null)
                {
                    // the source model carries no base colour texture (it would draw white): a stone and ash tint instead (ours)
                    sizes.Add("NO-ALBEDO(tinted)");
                    m.AlbedoColor = new Color(m.AlbedoColor.R * 0.52f, m.AlbedoColor.G * 0.49f, m.AlbedoColor.B * 0.46f, m.AlbedoColor.A);
                }
                foreach (var slot in Slots)
                {
                    if (m.GetTexture(slot) is not { } tex) continue;
                    if (!replaced.TryGetValue(tex, out var other))
                    {
                        other = tex;
                        var img = tex.GetImage();
                        if (img is not null && textureMax > 0)
                        {
                            string before = $"{img.GetWidth()}x{img.GetHeight()}";
                            bool normal = slot == BaseMaterial3D.TextureParam.Normal;
                            int max = Math.Max(img.GetWidth(), img.GetHeight());
                            if (img.IsCompressed()) img.Decompress();   // Resize fails on a compressed image
                            if (max > textureMax) { float k = (float)textureMax / max; img.Resize(Math.Max(1, (int)(img.GetWidth() * k)), Math.Max(1, (int)(img.GetHeight() * k)), Image.Interpolation.Lanczos); }
                            if (img.IsCompressed()) img.Decompress();
                            img.GenerateMipmaps(normal);
                            img.Compress(Image.CompressMode.S3Tc, normal ? Image.CompressSource.Normal : Image.CompressSource.Generic);
                            other = ImageTexture.CreateFromImage(img);
                            sizes.Add($"{before}->{img.GetWidth()}x{img.GetHeight()}");
                            bytes += img.GetData().Length;
                        }
                        replaced[tex] = other;
                    }
                    if (!ReferenceEquals(other, tex)) m.SetTexture(slot, other);
                }
            }
        return (bytes, string.Join(" ", sizes.Distinct()));
    }
}
