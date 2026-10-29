using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmFormats.Havok;
using Num = System.Numerics;

namespace SmClient;

/// <summary>One drawn surface of a <see cref="PieceAsset"/> (a range of the piece's mesh with one material).</summary>
public sealed class PieceSurface
{
    /// <summary>The material for an unpainted look: the diffuse, normal and specular maps as the map's materials (also the surface's own material when it has no paint mask).</summary>
    public required Material Plain { get; init; }
    /// <summary>The paint mask's material parts, or null when the surface is not paintable.</summary>
    public MapMaterials.PaintedSurface? Painted { get; init; }
    /// <summary>Whether the surface's second UV set can carry a decal (<see cref="PaintSchemes.HasDecalUv"/>).</summary>
    public bool DecalUv { get; init; }
    /// <summary>Package, mesh surface index and material name: the base of the painted material's cache key.</summary>
    public required string Key { get; init; }
    public bool HasDiffuse { get; init; }
}

/// <summary>
/// One armour package as drawn: a skinned <see cref="ArrayMesh"/> in the game's Z-up space with its <see cref="Skin"/>, one per package whoever wears it.
/// Characters add it with a mesh instance and put their own paint on the surfaces with surface override materials, so sixteen players share the GPU buffers.
/// </summary>
public sealed class PieceAsset
{
    public required string Package { get; init; }
    public required ArrayMesh Mesh { get; init; }
    public required Skin Skin { get; init; }
    /// <summary>One per surface of <see cref="Mesh"/>, in surface order.</summary>
    public required IReadOnlyList<PieceSurface> Surfaces { get; init; }
    /// <summary>Some surface can carry a decal (a shoulder pad with a decal layout).</summary>
    public bool TakesDecal => Surfaces.Any(s => s.DecalUv);
}

/// <summary>
/// The armour pieces of the install by package name, read lazily (customizer T6): the files are read and parsed on worker threads
/// (<see cref="Prefetch"/>, or on demand), the Godot meshes, skins and textures are built on the main thread, once per package, and kept. Not thread-safe
/// beyond that split: call everything but the background reads from the main thread.
/// </summary>
public sealed class PieceCache
{
    readonly string _gameDir;
    readonly RigAssets _rig;
    readonly Num.Matrix4x4[] _modelSpace;
    readonly IReadOnlyDictionary<string, Num.Vector3> _bonePositions;
    readonly Dictionary<string, PieceAsset?> _built = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Task<Loaded>> _reading = new(StringComparer.OrdinalIgnoreCase);
    readonly object _lock = new();

    // Piece: null when the read failed. Definite: the failure will repeat (file or mesh missing, data not understood), so the null may be cached; a
    // transient failure (the disk) is retried by the next Prefetch, up to MaxTransientTries times. TakesDecal is computed on the worker.
    sealed record Loaded(CharacterPiece? Piece, List<string> Notes, double Milliseconds, bool Definite, bool TakesDecal);

    const int MaxTransientTries = 3, MaxNotes = 400;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _takesDecal = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> _transientFailures = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _noteSet = new(StringComparer.Ordinal);

    /// <summary>The painted-material parts and textures of every built piece; replaced by <see cref="Clear"/> (the old one is freed with its last user).</summary>
    public MapMaterials Materials { get; private set; } = new();
    public List<string> Notes { get; }

    /// <summary>Adds a note once (the same text again is dropped, and at most <see cref="MaxNotes"/> are kept), so a character rebuilt every frame cannot grow the list.</summary>
    public void AddNote(string note)
    {
        if (Notes.Count < MaxNotes && _noteSet.Add(note)) Notes.Add(note);
    }
    /// <summary>Pieces built so far (meshes created).</summary>
    public int Built => _built.Values.Count(p => p is not null);
    /// <summary>Total time spent reading files (summed over worker threads) and building meshes and textures on the main thread, in ms.</summary>
    public double ReadMilliseconds { get; private set; }
    public double BuildMilliseconds { get; private set; }

    public PieceCache(string gameDir, RigAssets rig, List<string>? notes = null)
    {
        _gameDir = gameDir; _rig = rig; Notes = notes ?? new List<string>();
        _modelSpace = rig.Rig.Skeleton.ModelSpace(rig.Rig.Skeleton.ReferencePose);
        _bonePositions = CharacterModel.BindPositions(rig.Rig.Skeleton);
    }

    /// <summary>Starts reading a package's file on a worker thread (no effect when it is built or already being read).</summary>
    public void Prefetch(string package)
    {
        lock (_lock)
        {
            if (_built.ContainsKey(package) || _reading.ContainsKey(package)) return;
            _reading[package] = Task.Run(() => Read(package));
        }
    }

    /// <summary>Whether <see cref="Get"/> would not wait for the file: built, or its read has finished.</summary>
    public bool IsReady(string package)
    {
        lock (_lock) return _built.ContainsKey(package) || (_reading.TryGetValue(package, out var t) && t.IsCompleted);
    }

    /// <summary>
    /// The worker's job: nothing escapes it but an out-of-memory error, so a task never faults. A missing file or package name and data that does not parse are
    /// definite; any other failure (a disk error) is transient.
    /// </summary>
    Loaded Read(string package)
    {
        var watch = Stopwatch.StartNew();
        var notes = new List<string>();
        try
        {
            string? path = CharacterModel.PackagePath(_gameDir, package);
            if (path is null || !System.IO.File.Exists(path))
                return new Loaded(null, [$"armour package '{package}' is not in the install; not drawn"], watch.Elapsed.TotalMilliseconds, true, false);
            var piece = CharacterModel.LoadPiece(_rig.BoneNames, path, notes, CharacterModel.ChaosTexturePackages, objectTreePlacement: true, _bonePositions);
            bool takes = ComputeTakesDecal(piece);
            _takesDecal[package] = takes;
            return new Loaded(piece, notes, watch.Elapsed.TotalMilliseconds, true, takes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            bool definite = ex is not (System.IO.IOException or UnauthorizedAccessException) || ex is System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException;
            notes.Add($"armour package '{package}' unreadable ({ex.GetType().Name}: {ex.Message}); not drawn{(definite ? "" : " (will be tried again)")}");
            return new Loaded(null, notes, watch.Elapsed.TotalMilliseconds, definite, false);
        }
    }

    /// <summary>Whether any paintable surface of the piece has a decal layout (<see cref="PaintSchemes.HasDecalUv"/>): the same rule <see cref="Build"/> applies per surface.</summary>
    static bool ComputeTakesDecal(CharacterPiece piece) =>
        piece.Model.Surfaces.Any(s => piece.Hidden?.Contains(s.BlockIndex) != true && s.Material?.Textures.ContainsKey("PaintMask") == true
                                      && PaintSchemes.HasDecalUv(piece.Model.Mesh.Blocks[s.BlockIndex].Uv1));

    /// <summary>
    /// Whether the package's piece has a pad decal layout, when that is known without waiting (built, or its read has finished); null otherwise (and its read is started).
    /// </summary>
    public bool? TakesDecalIfKnown(string package)
    {
        Prefetch(package);
        if (_built.TryGetValue(package, out var built)) return built?.TakesDecal ?? false;
        return _takesDecal.TryGetValue(package, out var known) ? known : null;
    }

    /// <summary>Whether the piece is built (or known to be unavailable): <see cref="TryGet"/> would return at once.</summary>
    public bool IsBuilt(string package) => _built.ContainsKey(package);

    /// <summary>
    /// The package's piece, built on first use. Waits for the file when its read is still running (use <see cref="Prefetch"/> and <see cref="TryGet"/> to avoid the
    /// wait); null (with a note) when it is missing, unreadable or cannot be built. Definite failures are remembered; a transient one is tried again by the next call.
    /// </summary>
    public PieceAsset? Get(string package)
    {
        if (_built.TryGetValue(package, out var done)) return done;
        Prefetch(package);
        Task<Loaded> task;
        lock (_lock) task = _reading[package];
        Loaded loaded;
        try { loaded = task.GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            loaded = new Loaded(null, [$"armour package '{package}' read failed ({ex.GetType().Name}: {ex.Message}); not drawn"], 0, false, false);
        }
        finally { lock (_lock) { if (_reading.TryGetValue(package, out var t) && ReferenceEquals(t, task)) _reading.Remove(package); } }
        foreach (var n in loaded.Notes) AddNote(n);
        ReadMilliseconds += loaded.Milliseconds;
        if (loaded.Piece is null)
        {
            // A definite failure, or the third transient one, is remembered; otherwise the next Prefetch reads the file again.
            bool remember = loaded.Definite || _transientFailures.GetValueOrDefault(package) + 1 >= MaxTransientTries;
            _transientFailures[package] = _transientFailures.GetValueOrDefault(package) + 1;
            if (remember) _built[package] = null;
            return null;
        }
        _takesDecal[package] = loaded.TakesDecal;
        var watch = Stopwatch.StartNew();
        var asset = Build(package, loaded.Piece);
        BuildMilliseconds += watch.Elapsed.TotalMilliseconds;
        _built[package] = asset;
        return asset;
    }

    /// <summary>
    /// Like <see cref="Get"/> but never waits: true with the piece (null: unavailable) when it is built or its read has finished (then it is built now), false while its read
    /// is still running (the read is started when none is).
    /// </summary>
    public bool TryGet(string package, out PieceAsset? asset)
    {
        asset = null;
        if (_built.TryGetValue(package, out asset)) return true;
        if (!IsReady(package)) { Prefetch(package); return false; }
        asset = Get(package);
        return true;
    }

    /// <summary>
    /// The mesh, skin and surfaces of one piece (main thread): every surface becomes a mesh surface skinned to the rig (rigid blocks are bound to the one
    /// bone they hang from), the raw texture bytes are released once uploaded. Public for the legacy single-set path (<see cref="MarineAssets.Load"/>).
    /// </summary>
    /// <param name="vertexColours">Ork O3: the mesh carries its DCM vertex colours and the two-layer vertex-blend materials are built (the dismemberment blocks); the armour pieces have none.</param>
    public PieceAsset? Build(string package, CharacterPiece piece, bool vertexColours = false)
    {
        var notes = new List<string>();
        try { return BuildCore(package, piece, notes, vertexColours); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // One bad piece (bad indices, a texture that will not decode ...) is left out with a note; the look is drawn without it.
            notes.Add($"armour package '{package}' could not be built ({ex.GetType().Name}: {ex.Message}); not drawn");
            return null;
        }
        finally { foreach (var n in notes) AddNote(n); }
    }

    PieceAsset? BuildCore(string package, CharacterPiece piece, List<string> notes, bool vertexColours)
    {
        var skeleton = _rig.Rig.Skeleton;
        var textures = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
        var mesh = new ArrayMesh();
        var skin = new Skin();
        var surfaces = new List<PieceSurface>();
        foreach (var surface in piece.Model.Surfaces)
        {
            var block = piece.Model.Mesh.Blocks[surface.BlockIndex];
            if (piece.Hidden?.Contains(surface.BlockIndex) == true) continue;
            int n = block.Positions.Length;
            if (n == 0 || surface.Range.IndexCount < 3) continue;
            var bones = new int[4 * n];
            var weights = new float[4 * n];
            if (block.Skin is { } s)
            {
                int bindBase = skin.GetBindCount();
                foreach (var bone in s.Bones) skin.AddNamedBind(bone.Name, MarineAssets.ToGodot(bone.InverseBind));
                for (int v = 0; v < n; v++)
                {
                    // Unweighted slots may hold out-of-range indices in the data; point them at a valid bind.
                    for (int k = 0; k < 4; k++) bones[4 * v + k] = bindBase + (s.BoneIndices[4 * v + k] < s.Bones.Count ? s.BoneIndices[4 * v + k] : 0);
                    (weights[4 * v], weights[4 * v + 1], weights[4 * v + 2], weights[4 * v + 3]) = (s.Weights[v].X, s.Weights[v].Y, s.Weights[v].Z, s.Weights[v].W);
                }
            }
            else if (piece.Rigid is not null && piece.Rigid.TryGetValue(surface.BlockIndex, out var placed))
            {
                // Chaos rigid block: bring the vertices from the GEO node's space into the skeleton's model space (bind pose).
                int bone = skeleton.BoneNames.ToList().IndexOf(placed.Bone);
                if (bone < 0) { notes.Add($"{piece.Name}: bone '{placed.Bone}' not in the skeleton; block {surface.BlockIndex} left out"); continue; }
                var toModel = placed.ModelSpace ? placed.Local : placed.Local * _modelSpace[bone];
                Num.Matrix4x4.Invert(_modelSpace[bone], out var inverse);
                int bind = skin.GetBindCount();
                skin.AddNamedBind(placed.Bone, MarineAssets.ToGodot(inverse));
                for (int v = 0; v < n; v++) { bones[4 * v] = bind; weights[4 * v] = 1f; }
                block = block with
                {
                    Positions = block.Positions.Select(p => Num.Vector3.Transform(p, toModel)).ToArray(),
                    Normals = block.Normals.Select(p => Num.Vector3.Normalize(Num.Vector3.TransformNormal(p, toModel))).ToArray(),
                    Tangents = block.Tangents.Select(t => new Num.Vector4(Num.Vector3.Normalize(Num.Vector3.TransformNormal(new Num.Vector3(t.X, t.Y, t.Z), toModel)), t.W)).ToArray(),
                };
            }
            else if (piece.RigidBone is { } boneName)
            {
                // Rigid pieces are stored in the bind pose: bind them fully to one bone.
                int bone = skeleton.BoneNames.ToList().IndexOf(boneName);
                if (bone < 0) { notes.Add($"{piece.Name}: bone '{boneName}' not in the skeleton; block left out"); continue; }
                Num.Matrix4x4.Invert(_modelSpace[bone], out var inverse);
                int bind = skin.GetBindCount();
                skin.AddNamedBind(boneName, MarineAssets.ToGodot(inverse));
                for (int v = 0; v < n; v++) { bones[4 * v] = bind; weights[4 * v] = 1f; }
            }
            else { notes.Add($"{piece.Name}: not attached to the skeleton; skipped"); continue; }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = block.Positions.Select(p => new Vector3(p.X, p.Y, p.Z)).ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = block.Normals.Select(p => new Vector3(p.X, p.Y, p.Z)).ToArray();
            arrays[(int)Mesh.ArrayType.Tangent] = block.Tangents.SelectMany(t => new[] { t.X, t.Y, t.Z, t.W }).ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = block.Uv0.Select(t => new Vector2(t.X, t.Y)).ToArray();
            arrays[(int)Mesh.ArrayType.TexUV2] = block.Uv1.Select(t => new Vector2(t.X, t.Y)).ToArray();
            if (vertexColours)
                arrays[(int)Mesh.ArrayType.Color] = block.Colors.Select(c => { var v = VertexColour.Unpack(c); return new Color(v.X, v.Y, v.Z, v.W); }).ToArray();
            arrays[(int)Mesh.ArrayType.Bones] = bones;
            arrays[(int)Mesh.ArrayType.Weights] = weights;
            var indices = new int[surface.Range.IndexCount - surface.Range.IndexCount % 3];
            for (int i = 0; i < indices.Length; i++) indices[i] = block.Indices[surface.Range.StartIndex + i];
            arrays[(int)Mesh.ArrayType.Index] = indices;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            MapMaterials.PaintedSurface? painted = surface.Material is { } pmat && pmat.Textures.ContainsKey("PaintMask")
                ? Materials.PreparePainted(pmat, piece.Model.Textures, notes) : null;
            Material plain = (surface.Material is { } bmat ? Materials.Build(bmat, piece.Model.Textures, notes, vertexColours) : null)
                             ?? ModelViewer.MaterialFor(surface, textures, notes);
            int index = mesh.GetSurfaceCount() - 1;
            mesh.SurfaceSetMaterial(index, plain);
            surfaces.Add(new PieceSurface
            {
                Plain = plain, Painted = painted, DecalUv = painted is not null && PaintSchemes.HasDecalUv(block.Uv1),
                Key = $"{package}#{index}:{surface.Material?.Name}", HasDiffuse = surface.DiffuseDds is not null,
            });
        }
        piece.Model.ReleaseTextures();   // uploaded: the raw DDS bytes are not needed again
        return surfaces.Count == 0 ? null : new PieceAsset { Package = package, Mesh = mesh, Skin = skin, Surfaces = surfaces };
    }

    /// <summary>
    /// Forgets every built piece, every piece read that was never built (its raw bytes) and every texture (<see cref="Materials"/> is replaced): the Godot resources
    /// are freed when no character uses them any more. A read still running finishes and its result is dropped.
    /// </summary>
    public void Clear()
    {
        lock (_lock) _reading.Clear();
        _built.Clear();
        _takesDecal.Clear();
        _transientFailures.Clear();
        _noteSet.Clear();
        Materials = new MapMaterials();
    }
}
