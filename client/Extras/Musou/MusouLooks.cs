using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmFormats.Havok;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// The looks of the Musou crowd (design section D): one per enemy kind, each a <see cref="MarineAssets"/> (mesh, skin and the rig's animations) with its clips
/// for the five things a crowd member does (run, idle, attack, stagger, death) and a scale. The Orks are the boy's original meshes on the Exterminatus boy's rig
/// (<see cref="OrkAssets"/>: the rig, the piece cache and the five player looks; the Nob meshes, which are not player looks, are built here the same way from the
/// same archive, read on a worker); the Chaos Marine is the PvP Chaos model the networked view has already built. Read from the player's install at run time only.
/// <para>
/// Which mesh and clip each kind uses, and the scales, are **ours** (interpretation: the install has no Musou): Boy boy_melee, Shoota Boy boy_ranged, 'Ard Boy
/// boy_ardboy, Gretchin boy_melee at 0.7 (no Gretchin mesh verified), Nob nob_melee (else boy_melee_pro at 1.2), Warboss nob_melee at 1.6 (else boy_melee_pro at 1.9, else the Boy at 2.0; the owner, 2026-11-01: "always a large orc, never small"; 1.6 is the sim's BossScale).
/// </para>
/// </summary>
public sealed class MusouLooks
{
    /// <summary>The clip indices of a look (<see cref="Look.Clips"/>).</summary>
    public const int Run = 0, Idle = 1, Attack = 2, Stagger = 3, Death = 4, ClipCount = 5;
    /// <summary>Whether a clip loops (the run and the idle) or holds its last frame (the attack, stagger and death follow the state's time).</summary>
    public static bool Loops(int clip) => clip is Run or Idle;

    /// <summary>One kind's look.</summary>
    public sealed class Look
    {
        public required string Name;
        public required MarineAssets Assets;
        public required float Scale;
        public required HavokBoundAnimation?[] Clips;
        public MusouVat.Baked? Vat;
        public Task<MusouVat.Baked?>? Baking;
        /// <summary>The bake failed or had nothing to bake: skinned only, never tried again.</summary>
        public bool BakeFailed;
    }

    /// <summary>The Warboss's model scale: the sim's body (<see cref="MusouRules.BossScale"/>), so what is drawn is what is shot.</summary>
    static readonly float BossScale = new MusouRules().BossScale;

    sealed record Wanted(MusouEnemyType Type, string Mesh, float Scale, string? Fallback, float FallbackScale, bool Ranged);

    static readonly Wanted[] Plan =
    [
        new(MusouEnemyType.Boy, "boy_melee", 1f, null, 1f, false),
        new(MusouEnemyType.ShootaBoy, "boy_ranged", 1f, "boy_melee", 1f, true),
        new(MusouEnemyType.ArdBoy, "boy_ardboy", 1f, "boy_melee_pro", 1f, false),
        new(MusouEnemyType.Gretchin, "boy_melee", 0.7f, null, 0.7f, false),
        new(MusouEnemyType.Nob, "nob_melee", 1f, "boy_melee_pro", 1.2f, false),
        new(MusouEnemyType.Warboss, "nob_melee", BossScale, "boy_melee_pro", 1.9f, false),
    ];

    readonly string _gameDir;
    readonly OrkAssets _orks;
    readonly Dictionary<string, Task<(StaticModel? Model, ObjectTree? Tree, Exception? Failure)>> _reads = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, MarineAssets?> _meshes = new(StringComparer.OrdinalIgnoreCase);
    readonly Look?[] _looks = new Look?[16];
    readonly Func<MarineAssets?> _chaos;
    int _next;
    bool _chaosTried;

    public List<string> Notes { get; } = new();
    /// <summary>Every kind has a look (or its fallback, or none to be had).</summary>
    public bool Done { get; private set; }

    /// <param name="chaos">The Chaos Marine's model when the view has built it (null: not yet, or none).</param>
    public MusouLooks(string gameDir, OrkAssets orks, Func<MarineAssets?> chaos)
    {
        _gameDir = gameDir; _orks = orks; _chaos = chaos;
        foreach (var w in Plan) { Start(w.Mesh); if (w.Fallback is not null) Start(w.Fallback); }
    }

    /// <summary>The look of a kind (null: not built yet; the crowd then draws the Boy's, or nothing).</summary>
    public Look? For(MusouEnemyType type) => _looks[(int)type] ?? (type is MusouEnemyType.ChaosMarine or MusouEnemyType.Warboss or MusouEnemyType.Nob ? null : _looks[(int)MusouEnemyType.Boy]);   // the big ones wait for their own body: never a small Boy standing in (owner, 2026-11-01)

    /// <summary>Builds at most one look a call (main thread) once its files are read; returns the one built, or null.</summary>
    public Look? Step()
    {
        if (Done) return null;
        if (_next < Plan.Length)
        {
            var w = Plan[_next];
            var mesh = Mesh(w.Mesh, out bool ready);
            if (!ready) return null;
            float scale = w.Scale;
            if (mesh is null && w.Fallback is not null)
            {
                mesh = Mesh(w.Fallback, out ready);
                if (!ready) return null;
                scale = w.FallbackScale;
                if (mesh is not null) Notes.Add($"musou: {w.Type} drawn as {w.Fallback} x{scale:0.##} ({w.Mesh} not available)");
            }
            _next++;
            if (mesh is null) { Notes.Add($"musou: no look for {w.Type}"); return null; }
            var look = new Look { Name = $"{w.Type}:{(scale == w.Scale ? w.Mesh : w.Fallback)}", Assets = mesh, Scale = scale, Clips = OrkClips(mesh, w.Ranged) };
            return _looks[(int)w.Type] = look;
        }
        if (_looks[(int)MusouEnemyType.Warboss] is null && _looks[(int)MusouEnemyType.Boy] is { } boy)   // no Nob and no pro mesh to be had: the Boy's body, big
            _looks[(int)MusouEnemyType.Warboss] = new Look { Name = "Warboss:boy_melee", Assets = boy.Assets, Scale = 2.0f, Clips = boy.Clips };
        if (!_chaosTried && _chaos() is { } chaos)
        {
            _chaosTried = true;
            Done = true;
            return _looks[(int)MusouEnemyType.ChaosMarine] = new Look { Name = "ChaosMarine", Assets = chaos, Scale = 1f, Clips = MarineClips(chaos) };
        }
        return null;
    }

    /// <summary>Gives up waiting for the Chaos model (the view has none): the Chaos Marine is drawn as a Boy.</summary>
    public void StopWaiting() { if (_next >= Plan.Length && !_chaosTried) { _chaosTried = true; Done = true; Notes.Add("musou: no Chaos model built; Chaos Marines drawn as Boyz"); } }

    void Start(string mesh)
    {
        if (OrkAssets.LookName(mesh) is not null) { _orks.Prefetch(mesh); return; }
        if (_reads.ContainsKey(mesh)) return;
        string archive = RigProfiles.OrkBoy.ArchivePath(_gameDir);
        _reads[mesh] = Task.Run(() =>
        {
            try
            {
                var notes = new List<string>();
                var model = StaticModel.Load(archive, mesh, notes);
                var tree = ObjectTree.Load(archive, mesh, notes) ?? throw new System.IO.FileNotFoundException($"no object tree '{mesh}.O3d'");
                return ((StaticModel?)model, (ObjectTree?)tree, (Exception?)null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return ((StaticModel?)null, (ObjectTree?)null, ex); }
        });
    }

    /// <summary>A mesh's model (built once; null when it cannot be had); <paramref name="ready"/> false while its files are being read.</summary>
    MarineAssets? Mesh(string name, out bool ready)
    {
        ready = true;
        if (_meshes.TryGetValue(name, out var done)) return done;
        MarineAssets? built = null;
        try
        {
            if (OrkAssets.LookName(name) is not null)
            {
                if (!_orks.IsReady(name)) { ready = false; return null; }
                built = _orks.Build(name);
            }
            else
            {
                var read = _reads[name];
                if (!read.IsCompleted) { ready = false; return null; }
                var (model, tree, failure) = read.Result;
                if (failure is not null) throw failure;
                built = BuildOrk(name, model!, tree!);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Notes.Add($"musou: mesh {name}: {ex.GetType().Name}: {ex.Message}");
        }
        return _meshes[name] = built;
    }

    /// <summary>An Ork mesh that is not a player look, built as <see cref="OrkAssets.Build"/> builds a look (near detail, no wounded stumps, rigid blocks under their nearest bone).</summary>
    MarineAssets? BuildOrk(string name, StaticModel model, ObjectTree tree)
    {
        var rig = _orks.Rig;
        var drawn = tree.Visuals.Where(v => v.Renderable && v.NearLevel && v.MeshId >= 0 && v.MeshId < model.Mesh.Blocks.Count
                                            && !v.RefNode.Contains("wounded", StringComparison.OrdinalIgnoreCase)).ToList();
        var hidden = Enumerable.Range(0, model.Mesh.Blocks.Count).Where(b => drawn.All(v => v.MeshId != b)).ToHashSet();
        var rigid = new Dictionary<int, RigidBlock>();
        foreach (var v in drawn.Where(v => !v.IsSkinned && model.Mesh.Blocks[v.MeshId].Skin is null))
            if (tree.UnderBone(v.RefNode, rig.BoneNames.Contains) is { } placed) rigid[v.MeshId] = new RigidBlock(placed.Bone, placed.Local);
        int foreign = model.Mesh.Blocks.SelectMany(b => b.Skin?.Bones ?? []).Count(b => !rig.BoneNames.Contains(b.Name));
        if (foreign > 0) { Notes.Add($"musou: {name} binds {foreign} bones the boy's rig lacks; not used"); return null; }
        var piece = new CharacterPiece("musou_" + name, model, null) { Rigid = rigid, Hidden = hidden };
        var asset = _orks.Pieces.Build(piece.Name, piece, vertexColours: true);
        if (asset is null) return null;
        return new MarineAssets(rig, [new AssetPiece(piece.Name, asset.Mesh, asset.Skin, new Material?[asset.Surfaces.Count])],
            asset.Surfaces.Count(s => s.HasDiffuse), asset.Surfaces.Count, _orks.Pieces.Notes);
    }

    // ---------------------------------------------------------------- clips (ours: the nearest original clips to what a crowd member does)

    static HavokBoundAnimation?[] OrkClips(MarineAssets a, bool ranged)
    {
        HavokBoundAnimation? Find(string[] names, string contains) => a.FindAnimation(names, contains) is { } n ? a.Animation(n) : null;
        var clips = new HavokBoundAnimation?[ClipCount];
        clips[Run] = ranged ? Find(["BOY_2HR_NAV_Run_F_AimForward_02", "BOY_1HM_NAV_Sprint_F_01"], "Run_F") : Find(["BOY_1HM_NAV_Run_F_01", "BOY_1HM_NAV_Sprint_F_01"], "Run_F");
        clips[Idle] = ranged ? Find(["BOY_2HR_NAV_Idle_S_01", "BOY_2HR_RNG_Idle_S_01"], "Idle") : Find(["BOY_1HM_MEL_Idle_Combat_01", "BOY_1HM_MEL_Idle_Combat_03"], "Idle");
        clips[Attack] = ranged ? Find(["BOY_2HR_RNG_Fire_S_01"], "Fire") : Find(["BOY_1HM_MEL_Cmbo_01_Atk01_1m", "BOY_1HM_MEL_Cmbo_01_Hvy01_1m"], "_Atk");
        clips[Stagger] = Find(["BOY_1HM_REC_Hvy_Front_Hi_L_01", "BOY_1HM_REC_Lt_Front_Hi_L_01"], "_REC_");
        clips[Death] = ranged ? Find(["BOY_2HR_DIE_Fall_Forward_01", "BOY_1HM_DIE_Basic_02"], "_DIE") : Find(["BOY_1HM_DIE_Basic_02", "BOY_1HM_DIE_Basic_03"], "_DIE");
        Fill(clips);
        return clips;
    }

    static HavokBoundAnimation?[] MarineClips(MarineAssets a)
    {
        HavokBoundAnimation? Find(string[] names, string contains) => a.FindAnimation(names, contains) is { } n ? a.Animation(n) : null;
        var clips = new HavokBoundAnimation?[ClipCount];
        clips[Run] = Find([CharacterViewer.DefaultAnimation], "Run_F");
        clips[Idle] = Find([], "Idle");
        clips[Attack] = Find([], "_MEL_");
        clips[Stagger] = Find([], "_REC_");
        clips[Death] = Find([], "_DIE");
        Fill(clips);
        return clips;
    }

    /// <summary>A missing clip takes the idle's (or the run's) place, so every state has something to play.</summary>
    static void Fill(HavokBoundAnimation?[] clips)
    {
        var any = clips[Idle] ?? clips[Run];
        for (int i = 0; i < clips.Length; i++) clips[i] ??= any;
    }
}
