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
    }

    /// <summary>The extracted library's root (<c>source\</c>); the folder may not exist.</summary>
    public static string Root => System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", AshfallKit.PrivateFolder, "source");

    /// <summary>Whether the private folder exists at all (the look defaults to the graybox when it does not).</summary>
    public static bool Available => System.IO.Directory.Exists(Root);

    /// <summary>Largest texture side kept (0: as delivered). <c>--ashfall-tex N</c>; ours.</summary>
    public int TextureMax { get; set; } = 1024;
    /// <summary>Whether LODs are generated at load. <c>--ashfall-lods off</c> turns it off.</summary>
    public bool GenerateLods { get; set; } = true;

    readonly ConcurrentDictionary<string, Asset> _done = new();
    readonly ConcurrentQueue<string> _ready = new();
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
            string path = System.IO.Path.Combine(Root, model.File.Replace('/', System.IO.Path.DirectorySeparatorChar));
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
            var (bytes, note) = ShrinkTextures(pieces);
            return new Asset { Id = id, Pieces = [.. pieces], Ms = watch.Elapsed.TotalMilliseconds, Triangles = triangles, LodSurfaces = lodSurfaces, TextureBytes = bytes, TextureNote = note };
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

    /// <summary>Resizes each material's textures to <see cref="TextureMax"/> and block-compresses them; returns the VRAM estimate and a note.</summary>
    (long Bytes, string Note) ShrinkTextures(List<Piece> pieces)
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
                        if (img is not null && TextureMax > 0)
                        {
                            string before = $"{img.GetWidth()}x{img.GetHeight()}";
                            bool normal = slot == BaseMaterial3D.TextureParam.Normal;
                            int max = Math.Max(img.GetWidth(), img.GetHeight());
                            if (max > TextureMax) { float k = (float)TextureMax / max; img.Resize(Math.Max(1, (int)(img.GetWidth() * k)), Math.Max(1, (int)(img.GetHeight() * k)), Image.Interpolation.Lanczos); }
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
