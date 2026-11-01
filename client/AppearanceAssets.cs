using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmContent.Customization;
using SmSim;

namespace SmClient;

/// <summary>
/// Dresses the shared rig in any <see cref="ResolvedAppearance"/> (customizer T6, docs/customizer-architecture.md section 5). The expensive parts are shared:
/// one <see cref="RigAssets"/> (rig and animations), one <see cref="PieceCache"/> (mesh, skin and textures per armour package, files read on worker threads),
/// one <see cref="PaintedMaterialCache"/> (a material per surface, colours, decal and lens; bounded). <see cref="Build"/> only picks pieces and materials, so
/// a new look costs a few materials, and sixteen players in the same look share all of them.
/// <para>
/// In a match (no stalls): <c>assets.Prefetch(look)</c> when a look becomes known, then each frame <c>assets.TryBuild(look)</c> until it returns a model (it never
/// waits for a file and builds at most a couple of pieces per call). In the customizer, where one wait is fine: <c>assets.Build(look)</c>. Then
/// <c>new CharacterViewer(model, animation, gameSpaceRoot: false)</c>. The look comes from <see cref="AppearanceResolver"/> (<see cref="ResolveOptions"/> gives its
/// options). Everything here runs on the main thread except the background file reads <see cref="Prefetch"/> starts.
/// </para>
/// </summary>
public sealed class AppearanceAssets : IDisposable
{
    readonly string _gameDir;
    readonly PaintedMaterialCache _materials = new();
    // The raw bytes of badge textures read in the background, until they are decoded (then dropped).
    readonly Dictionary<string, Task<byte[]?>> _badgeBytes = new(StringComparer.Ordinal);

    public RigAssets Rig { get; }
    public PieceCache Pieces { get; }
    public PaintedMaterialCache Materials => _materials;
    /// <summary>
    /// Where a custom emblem's 16,384 BC3 bytes come from (the profile's file for the own look, the blob cache for others'); null or a null result: the
    /// fallback badge is drawn. It is asked only for an emblem whose texture is not cached yet, and its bytes are checked against the hash before they are used.
    /// When bytes arrive later, build the look again (the materials are cached, so it is cheap).
    /// </summary>
    public Func<EmblemHash, byte[]?>? EmblemSource { get; set; }
    /// <summary>Why something is missing, each text once (at most 400): the notes of the shared caches too.</summary>
    public IReadOnlyList<string> Notes => Pieces.Notes;
    /// <summary>Milliseconds the last <see cref="Build"/> took on the main thread (including any wait for a file still being read).</summary>
    public double LastBuildMilliseconds { get; private set; }
    public int BuildCount { get; private set; }

    public AppearanceAssets(string gameDir, RigAssets? rig = null, List<string>? notes = null)
    {
        _gameDir = gameDir;
        Rig = rig ?? RigAssets.Load(gameDir, notes);
        Pieces = new PieceCache(gameDir, Rig, notes);
    }

    static AppearanceAssets? _shared;
    static string? _sharedFor;

    /// <summary>
    /// The caches every character of this process shares, made on first use for <paramref name="gameDir"/> (compared without regard to case; another folder replaces the
    /// old instance). <paramref name="notes"/> is the list the new instance appends its notes to; it is only used when the instance is made, later calls ignore it (read
    /// <see cref="Notes"/>).
    /// </summary>
    public static AppearanceAssets Shared(string gameDir, List<string>? notes = null)
    {
        if (_shared is null || !string.Equals(_sharedFor, gameDir, StringComparison.OrdinalIgnoreCase))
        { _shared?.Dispose(); _shared = new AppearanceAssets(gameDir, notes: notes); _sharedFor = gameDir; }
        return _shared;
    }

    /// <summary>Starts reading, on worker threads, the files of the look's pieces and its badges, so <see cref="TryBuild"/> / <see cref="Build"/> do not wait for the disk.</summary>
    public void Prefetch(ResolvedAppearance look)
    {
        foreach (var piece in look.Pieces)
            foreach (var package in piece.Packages) Pieces.Prefetch(package);
        foreach (var decal in new[] { look.LeftDecal, look.RightDecal })
            if (decal.Kind != DecalKind.None && decal.Badge is { IsBlank: false } badge) PrefetchBadge(badge);
    }

    void PrefetchBadge(PaintDecal badge)
    {
        string key = BadgeKey(badge);
        if (Pieces.Materials.HasDecalTexture(key) || _badgeBytes.ContainsKey(key)) return;
        _badgeBytes[key] = Task.Run(() =>
        {
            try { return PaintSchemes.ReadDecalTexture(_gameDir, badge); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        });
    }

    static string BadgeKey(PaintDecal badge) => "b:" + badge.Texture;

    /// <summary>Whether <see cref="Build"/> would not have to wait for any file: every piece and badge read has finished.</summary>
    public bool IsReady(ResolvedAppearance look) => look.Pieces.SelectMany(p => p.Packages).All(Pieces.IsReady) && BadgesReady(look);

    bool BadgesReady(ResolvedAppearance look)
    {
        foreach (var decal in new[] { look.LeftDecal, look.RightDecal })
            if (decal.Kind != DecalKind.None && decal.Badge is { IsBlank: false } badge)
            {
                string key = BadgeKey(badge);
                if (Pieces.Materials.HasDecalTexture(key)) continue;
                if (!_badgeBytes.TryGetValue(key, out var task)) { PrefetchBadge(badge); return false; }
                if (!task.IsCompleted) return false;
            }
        return true;
    }

    /// <summary>
    /// <see cref="ResolveOptions"/> for <see cref="AppearanceResolver"/> that know which shoulder pieces can take a decal (the piece's mesh has a decal layout:
    /// <see cref="PaintSchemes.HasDecalUv"/>). It never waits for a file: a pad whose package has not been read yet (its read is started) counts as able; drawing
    /// still puts a decal only on surfaces that have the layout. <paramref name="carried"/>: see <see cref="ResolveOptions.Carried"/>.
    /// </summary>
    public ResolveOptions ResolveOptions(IReadOnlyCollection<string>? carried = null) => new()
    {
        Carried = carried,
        PadTakesDecal = piece => piece.Packages.Count > 0 && (Pieces.TakesDecalIfKnown(piece.Packages[0]) ?? true),
    };

    /// <summary>Like <see cref="ResolveOptions"/> but exact: waits for the pads' packages to be read (a test view, the customizer's first resolve).</summary>
    public ResolveOptions ResolveOptionsExact(IReadOnlyCollection<string>? carried = null) => new()
    {
        Carried = carried,
        PadTakesDecal = piece => piece.Packages.Count > 0 && Pieces.Get(piece.Packages[0])?.TakesDecal == true,
    };

    /// <summary>
    /// For use in a match: the character model for <paramref name="look"/> when everything it needs has been read, else null (and the reads are started). It never waits
    /// for a file and builds at most <paramref name="maxNewPieces"/> pieces (meshes and textures, a few milliseconds each) per call, so the work is spread over frames:
    /// call it every frame until it returns a model.
    /// </summary>
    public MarineAssets? TryBuild(ResolvedAppearance look, int maxNewPieces = 2)
    {
        Prefetch(look);
        if (!BadgesReady(look)) return null;
        bool pending = false;
        int budget = maxNewPieces;
        foreach (var package in look.Pieces.SelectMany(p => p.Packages))
        {
            if (Pieces.IsBuilt(package)) continue;
            if (!Pieces.IsReady(package) || budget <= 0) { pending = true; continue; }
            Pieces.Get(package);   // its read has finished: no wait
            budget--;
        }
        return pending ? null : Build(look);
    }

    /// <summary>
    /// A character model for <paramref name="look"/>: the shared rig and animations, one mesh instance per piece with this look's paint as surface override
    /// materials. The class is the one the look was resolved for (<see cref="ResolvedAppearance.ClassKey"/>). **Waits** for any file not yet read (use
    /// <see cref="Prefetch"/> first, or <see cref="TryBuild"/> in a match). The result is cheap (a list of shared meshes and materials) and may be built again
    /// whenever something it shows changed (an emblem arrived).
    /// </summary>
    public MarineAssets Build(ResolvedAppearance look)
    {
        var watch = Stopwatch.StartNew();
        int notesBefore = Pieces.Notes.Count;
        var pieces = new List<AssetPiece>();
        int textured = 0, surfaces = 0;
        var leftDecal = DecalTexture(look.LeftDecal, out string leftKey);
        var rightDecal = DecalTexture(look.RightDecal, out string rightKey);
        bool unpaintedNoted = false;
        foreach (var piece in look.Pieces)
        {
            foreach (var package in piece.Packages)
            {
                if (Pieces.Get(package) is not { } asset) continue;
                var paint = PaintOf(look, piece);
                var overrides = new Material?[asset.Surfaces.Count];
                for (int i = 0; i < asset.Surfaces.Count; i++)
                {
                    var surface = asset.Surfaces[i];
                    surfaces++;
                    if (surface.HasDiffuse) textured++;
                    if (surface.Painted is not { } painted) continue;
                    if (paint is null)
                    {
                        if (!unpaintedNoted) { Pieces.AddNote($"{package}: no paint for slot '{piece.Slot}'; the grey diffuse is drawn"); unpaintedNoted = true; }
                        continue;   // the surface's own (grey, unpainted) material
                    }
                    Texture2D? decal = null; string decalKey = "";
                    if (surface.DecalUv && piece.Slot == "leftshoulder") (decal, decalKey) = (leftDecal, leftKey);
                    else if (surface.DecalUv && piece.Slot == "rightshoulder") (decal, decalKey) = (rightDecal, rightKey);
                    MapMaterials.LensSetting? lens = null;
                    (byte, byte, byte)? lensKey = null;
                    if (piece.Slot == "head" && look.Lens is { } lc && LensRegions.For(package).Count > 0)
                    {
                        lens = new MapMaterials.LensSetting(new Color(lc.R / 255f, lc.G / 255f, lc.B / 255f), LensRegions.Slots(package), LensRegions.Gain(package));
                        lensKey = lc;
                    }
                    string key = PaintedMaterialKey.Of(surface.Key, paint, decalKey, lensKey);
                    var colours = MapMaterials.PaintColours.From(paint);
                    overrides[i] = _materials.Get(key, () => Pieces.Materials.MakePainted(painted, colours, decal, lens));
                }
                pieces.Add(new AssetPiece(package, asset.Mesh, asset.Skin, overrides));
            }
        }
        LastBuildMilliseconds = watch.Elapsed.TotalMilliseconds;
        BuildCount++;
        return new MarineAssets(Rig, pieces, textured, surfaces, Pieces.Notes.Skip(notesBefore).ToList()) { PackPaint = WornPackPaint.Of(look) };
    }

    /// <summary>
    /// The paint of a piece: its slot's (the belt follows its torso; the class's forced extra such as the Iron Halo follows the backpack, an interpretation), or the
    /// "default" paint of a single-set look.
    /// </summary>
    static ResolvedPaint? PaintOf(ResolvedAppearance look, ResolvedPiece piece)
    {
        if (look.Paint.TryGetValue(piece.Slot, out var p)) return p;
        if (piece.Slot == "perk" && look.Paint.TryGetValue("backpack", out var back)) return back;
        return look.Paint.GetValueOrDefault("default");
    }

    /// <summary>
    /// A pad's decal texture (null: none) and its cache key: a badge by its texture, an emblem by its hash once its bytes are here (a cached texture is used without asking
    /// <see cref="EmblemSource"/>; bytes are verified against the hash before decoding), else the fallback badge.
    /// </summary>
    Texture2D? DecalTexture(ResolvedDecal decal, out string key)
    {
        key = "";
        if (decal.Kind == DecalKind.Emblem && !decal.Emblem.IsNone)
        {
            var emblem = Pieces.Materials.CachedEmblem(decal.Emblem);
            if (emblem is null && EmblemSource?.Invoke(decal.Emblem) is { } bytes)
            {
                emblem = Pieces.Materials.LoadEmblem(decal.Emblem, bytes, out string? problem);
                if (problem is not null) Pieces.AddNote(problem);
            }
            if (emblem is not null) { key = PaintedMaterialKey.DecalKeyOf(decal, true); return emblem; }
        }
        if (decal.Kind != DecalKind.None && decal.Badge is { IsBlank: false } badge)
        {
            string badgeKey = BadgeKey(badge);
            var texture = Pieces.Materials.LoadDecal(badgeKey, () => BadgeBytes(badgeKey, badge), Pieces.Notes);
            _badgeBytes.Remove(badgeKey);   // decoded (or known missing): the raw bytes are not needed again
            if (texture is not null) { key = PaintedMaterialKey.DecalKeyOf(decal with { Kind = DecalKind.Badge }, false); return texture; }
        }
        return null;
    }

    /// <summary>The badge's DDS bytes: from the background read (waiting only if it is still running), else read now.</summary>
    byte[]? BadgeBytes(string key, PaintDecal badge)
    {
        if (_badgeBytes.TryGetValue(key, out var task))
        {
            try { return task.GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        }
        return PaintSchemes.ReadDecalTexture(_gameDir, badge);
    }

    /// <summary>The old pre-painted Space Marine set (no paint mask), used only when the install's paint schemes cannot be read.</summary>
    internal MarineAssets BuildLegacy(List<string> notes)
    {
        var model = CharacterModel.LoadMarine(_gameDir, notes, legacyArmour: true);
        var pieces = new List<AssetPiece>();
        int textured = 0, surfaces = 0;
        foreach (var piece in model.Pieces)
        {
            if (Pieces.Build(piece.Name, piece) is not { } asset) continue;
            pieces.Add(new AssetPiece(piece.Name, asset.Mesh, asset.Skin, new Material?[asset.Surfaces.Count]));
            surfaces += asset.Surfaces.Count; textured += asset.Surfaces.Count(s => s.HasDiffuse);
        }
        return new MarineAssets(Rig, pieces, textured, surfaces, notes);
    }

    /// <summary>Frees every cache: pieces, painted materials, textures, and raw bytes read but never used (characters still showing a look keep what they use until freed).</summary>
    public void Clear()
    {
        Pieces.Clear();
        _materials.Clear();
        _badgeBytes.Clear();
    }

    public void Dispose()
    {
        Clear();
        if (ReferenceEquals(_shared, this)) { _shared = null; _sharedFor = null; }
    }
}
