using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmContent.Customization;
using SmFormats.Havok;
using Num = System.Numerics;

namespace SmClient;

/// <summary>One armour piece of a character: a shared mesh and skin, and this character's paint as surface override materials (null: the mesh's own material).</summary>
public sealed record AssetPiece(string Name, ArrayMesh Mesh, Skin Skin, IReadOnlyList<Material?> Overrides);

/// <summary>
/// What a <see cref="CharacterViewer"/> shows: the shared multiplayer rig and animations (<see cref="RigAssets"/>) plus the armour pieces of one look.
/// Many characters share one instance, and instances of different looks share the rig, the piece meshes and the painted materials
/// (<see cref="AppearanceAssets"/> builds them for any <see cref="ResolvedAppearance"/>; <see cref="Load"/> is the single-set path the networked view still
/// uses: the side's default paint scheme on its default armour). A mesh is a resource, so many characters can use it with their own skeletons.
/// </summary>
public sealed class MarineAssets : IDisposable
{
    public RigAssets Rig { get; }
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, HavokBoundAnimation?> _table;
    /// <summary>The rig (<c>Model.Rig</c>); <c>Model.Pieces</c> is empty, the pieces are in <see cref="Pieces"/>.</summary>
    public CharacterModel Model { get; }
    public IReadOnlyList<AssetPiece> Pieces { get; private set; }
    public int TexturedSurfaces { get; }
    public int Surfaces { get; }
    public List<string> Notes { get; }
    /// <summary>
    /// The paint the worn jump pack takes in this look (the backpack's: <see cref="SmContent.Customization.WornPackPaint"/>, the owner's rule), or null (the pack keeps the
    /// side's default paint: the single-set path and the placeholder). Read by <see cref="WeaponRig.SetPackPaint"/>.
    /// </summary>
    public SmContent.Customization.ResolvedPaint? PackPaint { get; internal init; }

    internal MarineAssets(RigAssets rig, IReadOnlyList<AssetPiece> pieces, int textured, int surfaces, List<string> notes)
    {
        Rig = rig; _table = TableOf(rig); Model = new CharacterModel(rig.Rig, []); Pieces = pieces; TexturedSurfaces = textured; Surfaces = surfaces; Notes = notes;
    }

    /// <summary>Drops the piece list (the meshes and materials are freed with the last character that uses them; the shared caches keep theirs until <see cref="AppearanceAssets.Dispose"/>).</summary>
    public void Dispose() => Pieces = [];

    /// <summary>Test aid (<c>--paint &lt;scheme&gt;</c>): a paint scheme from the install used instead of each side's default.</summary>
    public static string? PaintOverride { get; set; }
    static PaintSchemes? _schemes;
    static bool _schemesRead;
    static readonly Dictionary<string, PaintScheme?> ResolvedSchemes = new(StringComparer.OrdinalIgnoreCase);
    static readonly object SchemesLock = new();

    /// <summary>
    /// The install's paint schemes, read once, lazily (the first character or backpack weapon that needs paint; the read goes through the whole
    /// of <c>ebps</c> for the two player blueprints, its time is noted). A failure is cached too, as null: nothing is read again per surface.
    /// </summary>
    public static PaintSchemes? Schemes(string gameDir, List<string> notes)
    {
        lock (SchemesLock)
        {
            if (_schemesRead) return _schemes;
            _schemesRead = true;
            try
            {
                _schemes = PaintSchemes.Load(gameDir, notes);
                notes.Add($"paint: {_schemes.Names.Count()} schemes read in {_schemes.LoadTime.TotalMilliseconds:F0} ms (marines {_schemes.MarineDefault}, Chaos {_schemes.ChaosDefault})");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                notes.Add($"paint schemes unreadable ({ex.Message}); armour is drawn unpainted");
            }
            return _schemes;
        }
    }

    /// <summary>
    /// The paint scheme of a side: <paramref name="name"/>, else <see cref="PaintOverride"/>, else the side's default. Resolved once per name
    /// (also when absent: null, one note).
    /// </summary>
    public static PaintScheme? SchemeFor(string gameDir, List<string> notes, bool chaos, string? name = null)
    {
        lock (SchemesLock)
        {
            if (Schemes(gameDir, notes) is not { } schemes) return null;
            name ??= PaintOverride ?? (chaos ? schemes.ChaosDefault : schemes.MarineDefault);
            if (ResolvedSchemes.TryGetValue(name, out var cached)) return cached;
            var scheme = schemes.Scheme(name);
            if (scheme is null) notes.Add($"paint scheme '{name}' not found; armour is drawn unpainted");
            return ResolvedSchemes[name] = scheme;
        }
    }

    /// <summary>
    /// A single armour set in one paint scheme, as the networked view draws its players until it is wired to <see cref="AppearanceAssets"/> (customizer T7).
    /// The rig, piece meshes and painted materials come from the shared <see cref="AppearanceAssets.Shared"/> caches.
    /// </summary>
    /// <param name="chaosSet">null: the Space Marine; else a Chaos armour set named explicitly (a test aid: <c>--chaos &lt;set&gt;</c>, <c>--legacy</c>; a played class's look is resolved from the data by <c>AppearanceResolver</c>).</param>
    /// <param name="paintScheme">A paint scheme name (<see cref="PaintSchemes.Names"/>); null: <see cref="PaintOverride"/>, else the side's
    /// default (<see cref="PaintSchemes.MarineDefault"/> / <see cref="PaintSchemes.ChaosDefault"/>).</param>
    public static MarineAssets Load(string gameDir, List<string>? notes = null, string? chaosSet = null, string? paintScheme = null)
    {
        notes ??= new List<string>();
        var scheme = SchemeFor(gameDir, notes, chaos: chaosSet is not null, paintScheme);
        var schemes = Schemes(gameDir, notes);
        var shared = AppearanceAssets.Shared(gameDir);
        // Without a usable scheme the paintable (grey) Space Marine armour would be drawn unpainted: use the pre-painted old set instead.
        bool legacy = chaosSet is null && scheme is null;
        if (legacy) notes.Add("no usable paint scheme for the Space Marine: the pre-painted set sm_armour_basic_001 is drawn");
        if (legacy) return shared.BuildLegacy(notes);
        var files = chaosSet is null ? CharacterModel.MarineArmourFiles(gameDir, false, out _) : CharacterModel.ChaosArmourFiles(gameDir, chaosSet);
        if (chaosSet is not null && files.Count == 0) throw new System.IO.FileNotFoundException($"No Chaos armour pieces for set '{chaosSet}' in the install.");
        var packages = files.Select(System.IO.Path.GetFileNameWithoutExtension).Select(n => n!).ToList();
        var built = shared.Build(SingleSetLook(packages, chaosSet is not null, scheme, schemes));
        notes.AddRange(built.Notes);
        return built;
    }

    /// <summary>The look of a single armour set in one scheme (the scheme's own colours per slot, its pattern flags ignored as before the customizer, its shoulder decals).</summary>
    static ResolvedAppearance SingleSetLook(IReadOnlyList<string> packages, bool chaos, PaintScheme? scheme, PaintSchemes? schemes)
    {
        var pieces = packages.Select(p => new ResolvedPiece(PaintSchemes.SlotOf(p) ?? "default", p, [p], false)).ToList();
        var paint = new Dictionary<string, ResolvedPaint>(StringComparer.Ordinal);
        if (scheme is not null)
        {
            foreach (var slot in PaintSchemes.Slots)
                if (scheme.For(slot) is { } set) paint[slot] = Resolved(set);
            if (scheme.Default is { } d) paint["default"] = Resolved(d);
        }
        ResolvedDecal Decal(string pad)
        {
            string? piece = packages.FirstOrDefault(p => PaintSchemes.SlotOf(p) == pad);
            return piece is not null && scheme is not null && schemes?.DecalFor(scheme, piece) is { IsBlank: false } d ? new ResolvedDecal(DecalKind.Badge, d, SmSim.EmblemHash.None) : ResolvedDecal.None;
        }
        return new ResolvedAppearance(chaos ? CustomizationCatalog.Chaos : CustomizationCatalog.SpaceMarine, "", null, pieces, paint, Decal("leftshoulder"), Decal("rightshoulder"),
            null, new HashSet<string>(), new HashSet<string>(), "", []);
    }

    static ResolvedPaint Resolved(PaintSet set) =>
        new(ResolvedColour.Of(set.Primary), ResolvedColour.Of(set.Secondary), ResolvedColour.Of(set.Tertiary), true);

    /// <summary>
    /// The named animation, decoded once; null (with a note) when it is missing or cannot be decoded. M40-T9: every caller goes through one table per rig
    /// (<see cref="TableOf"/>), which <see cref="PrefetchAnimations"/> fills on a worker thread, so a clip's first use does not decode it on the main thread
    /// (a decode took up to 30 ms); a name asked for before its prefetch is decoded on the spot as before. A name always gives the same instance.
    /// </summary>
    public HavokBoundAnimation? Animation(string name)
    {
        if (_table.TryGetValue(name, out var decoded)) return decoded;
        var animation = Rig.Animation(name);
        return _table.GetOrAdd(name, animation);
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RigAssets, System.Collections.Concurrent.ConcurrentDictionary<string, HavokBoundAnimation?>> Tables = new();

    static System.Collections.Concurrent.ConcurrentDictionary<string, HavokBoundAnimation?> TableOf(RigAssets rig) =>
        Tables.GetValue(rig, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, HavokBoundAnimation?>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Decodes the rig's animations on a worker thread (below normal priority), most likely ones first (the multiplayer clips), into the table
    /// <see cref="Animation"/> reads. The packfile is read-only after loading, and the worker only calls <see cref="SmFormats.Havok.HavokCharacterFile.LoadAnimation"/>,
    /// never <see cref="RigAssets.Animation"/> (whose dictionary and notes belong to the main thread). A clip that cannot be decoded is left to the main thread's
    /// call, which notes it. Returns the task (its result: the number decoded).
    /// </summary>
    public System.Threading.Tasks.Task<int> PrefetchAnimations(System.Threading.CancellationToken cancel = default)
    {
        var rig = Rig;
        var table = TableOf(rig);
        var file = rig.Rig;
        // anim/melee-flow: the hammer's own navigation, sprint, charge and draw clips are multiplayer clips without "MP" in their names: with the MP clips.
        static bool Hammer(string n) => n.Contains("_TH_", StringComparison.OrdinalIgnoreCase) || n.Contains("_2HM_", StringComparison.OrdinalIgnoreCase) || n.Contains("Pistol2TH", StringComparison.OrdinalIgnoreCase);
        var names = file.AnimationNames.OrderBy(n => n.StartsWith("MP_", StringComparison.OrdinalIgnoreCase) || n.Contains("_MP_", StringComparison.OrdinalIgnoreCase) || Hammer(n) ? 0 : 1).ToArray();
        var completion = new System.Threading.Tasks.TaskCompletionSource<int>();
        new System.Threading.Thread(() =>
        {
            int count = 0;
            try
            {
                foreach (var name in names)
                {
                    if (cancel.IsCancellationRequested) break;
                    if (table.ContainsKey(name)) continue;
                    try { table.TryAdd(name, file.LoadAnimation(name)); count++; }
                    catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or System.IO.InvalidDataException) { }   // the main thread's call notes it
                }
                completion.SetResult(count);
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "AnimationPrefetch", Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
        return completion.Task;
    }

    /// <summary>The first animation name that exists among the candidates, else the first containing <paramref name="fallbackContains"/>.</summary>
    public string? FindAnimation(IEnumerable<string> candidates, string fallbackContains) => Rig.FindAnimation(candidates, fallbackContains);

    internal static Transform3D ToGodot(HavokTransform t) => new(
        new Basis(new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W).Normalized()) * Basis.FromScale(new Vector3(t.Scale.X, t.Scale.Y, t.Scale.Z)),
        new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z));

    /// System.Numerics matrices are row-vector (p' = p·M): rows 1–3 are the images of the axes, row 4 the translation.
    internal static Transform3D ToGodot(Num.Matrix4x4 m) => new(
        new Basis(new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23), new Vector3(m.M31, m.M32, m.M33)),
        new Vector3(m.M41, m.M42, m.M43));
}
