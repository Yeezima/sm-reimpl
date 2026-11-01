using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// Test aid (Three-Front War dependency, docs/discovery.md "Guardsman"): the Exterminatus Guardsman (<c>ig_coop_guardsman</c>) with its original body
/// (<c>guardsman_a</c>), head (<c>guardsman_head_basic_001</c>), rig and clips (<c>havok_guardsman_a_coop</c>) and its lasgun, all read from
/// <c>data/chunks/ebp_sets__mp_coop_set_sm.oppc</c>, on a plain stage without a server. <c>--guard-test</c> with <c>--anim &lt;clip&gt;</c> (default
/// <c>IG_NAV_Idle_01</c>; <c>--list-clips</c> prints the rig's clips), <c>--time &lt;s&gt;</c> (hold the clip there), <c>--head &lt;mesh&gt;</c>,
/// <c>--no-lasgun</c>, <c>--shots 0,180</c> (turntable angles, 0 = front) saved to <c>--out</c> (default <c>%LOCALAPPDATA%\sm-reimpl\cache\guardsman</c>;
/// never inside the repository or the install) as <c>&lt;--name&gt;_&lt;angle&gt;.png</c>, then quit (a capture needs <c>godot.cmd --windowed</c>).
/// The lasgun hangs as its wargear <c>ig_coop_lasgun</c> says: the model's <c>rig_prop_m_chest</c> node on the rig bone of that name (**supported by evidence**).
/// **Placeholder demonstration** around original assets.
/// </summary>
public partial class GuardsmanTestView : Node3D
{
    /// <summary>The co-op set holding the Guardsman's rig, meshes, materials and lasgun (Exterminatus content).</summary>
    public const string Archive = "data/chunks/ebp_sets__mp_coop_set_sm.oppc";
    public const string RigEntry = "havok_guardsman_a_coop__hkxx", Body = "guardsman_a", Head = "guardsman_head_basic_001", Wargear = "ig_coop_lasgun";

    /// <summary>The Guardsman's rig: only its archive, packfile and body visual (the analyzers' graph names stay the marine defaults; nothing here uses them).</summary>
    public static readonly RigProfile Profile = new("guardsman", Archive, RigEntry, Body);

    readonly string _gameDir;
    readonly Dictionary<string, string?> _args;
    CharacterViewer? _viewer;
    float[] _shots = [];
    int _nextShot, _waitFrames;
    string _outDir = "", _name = "guardsman";

    public GuardsmanTestView(string gameDir, Dictionary<string, string?> args) { _gameDir = gameDir; _args = args; }
    public GuardsmanTestView() : this("", new Dictionary<string, string?>()) { }

    /// <summary>The lasgun on the mount its wargear names.</summary>
    CharacterPiece LasgunPiece(RigAssets rig, List<string> notes)
    {
        var catalogue = WeaponModels.Load(_gameDir, Profile, notes, [Wargear], rig.Rig.Skeleton.BoneNames);
        var attachment = catalogue.Held(Wargear) ?? throw new InvalidOperationException($"{Wargear}: no wield entry");
        var wield = attachment.Wield!;
        var weapon = WeaponModel.Load(_gameDir, attachment, notes);
        var toBone = weapon.RootToBone(wield, notes);
        var rigid = weapon.Parts.Where(p => p.Visible).ToDictionary(p => p.BlockIndex, p => new RigidBlock(wield.Bone ?? throw new InvalidOperationException($"{Wargear}: wield on no bone"), p.ToRoot * toBone));
        notes.Add($"{Wargear}: {wield.HandleNode} on {wield.Bone}");
        return new CharacterPiece("ig_lasgun", weapon.Model, null) { Rigid = rigid, Hidden = Enumerable.Range(0, weapon.Model.Mesh.Blocks.Count).Where(b => !rigid.ContainsKey(b)).ToHashSet() };
    }

    /// <summary>One piece of the look: the visuals of the object tree's nearest level of detail, unskinned blocks hung under their tree bone.</summary>
    static CharacterPiece LoadPiece(string archive, string name, RigAssets rig, List<string> notes)
    {
        var model = StaticModel.Load(archive, name, notes);
        var tree = ObjectTree.Load(archive, name, notes);
        var hidden = new HashSet<int>();
        var rigid = new Dictionary<int, RigidBlock>();
        if (tree is not null)
        {
            var drawn = tree.Visuals.Where(v => v.Renderable && v.NearLevel && v.MeshId >= 0 && v.MeshId < model.Mesh.Blocks.Count).ToList();
            if (drawn.Count > 0) hidden = Enumerable.Range(0, model.Mesh.Blocks.Count).Where(b => drawn.All(v => v.MeshId != b)).ToHashSet();
            foreach (var v in drawn.Where(v => model.Mesh.Blocks[v.MeshId].Skin is null))
                if (tree.UnderBone(v.RefNode, rig.BoneNames.Contains) is { } placed) rigid[v.MeshId] = new RigidBlock(placed.Bone, placed.Local);
        }
        foreach (var bone in model.Mesh.Blocks.SelectMany(b => b.Skin?.Bones ?? []).Select(b => b.Name).Distinct())
            if (!rig.BoneNames.Contains(bone)) notes.Add($"{name}: bone '{bone}' is not in the Guardsman skeleton");
        notes.Add($"{name}: {model.Mesh.Blocks.Count} blocks, drawn {model.Mesh.Blocks.Count - hidden.Count} (hidden {string.Join(",", hidden.OrderBy(h => h))}), materials {string.Join(", ", model.Surfaces.Select(s => s.Material?.Name ?? "?").Distinct())}");
        return new CharacterPiece("ig_" + name, model, null) { Rigid = rigid, Hidden = hidden };
    }

    public override void _Ready()
    {
        if (_gameDir.Length == 0) { GD.PrintErr("guardsman test: no game folder"); GetTree().Quit(1); return; }
        var notes = new List<string>();
        _name = _args.GetValueOrDefault("name") ?? "guardsman";
        if (_name.Length == 0 || _name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || _name.Contains("..", StringComparison.Ordinal)) _name = "guardsman";
        _outDir = _args.GetValueOrDefault("out") ?? System.IO.Path.Combine(GameInstall.DataRoot, "cache", "guardsman");
        foreach (var x in (_args.GetValueOrDefault("shots") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) _shots = [.. _shots, v];
        var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));

        try
        {
            string fullOut = System.IO.Path.GetFullPath(_outDir);   // a bad --out throws: reported below
            if (SmFormats.InstallPaths.IsWithin(fullOut, repoRoot) || SmFormats.InstallPaths.IsWithin(fullOut, _gameDir))
            { GD.PrintErr($"guardsman test: {_outDir} is inside the repository or the game folder; screenshots refused"); _shots = []; }
            var rig = RigAssets.Load(_gameDir, Profile, notes);
            GD.Print($"guardsman test: rig {RigEntry}: {rig.Rig.Skeleton.BoneNames.Count} bones, {rig.Rig.AnimationNames.Count} animations");
            if (_args.ContainsKey("list-clips"))
                foreach (var n in rig.Rig.AnimationNames.Order(StringComparer.Ordinal))
                    GD.Print($"  {n,-36} {(rig.Animation(n) is { } a ? $"{a.Animation.Duration:F2} s" : "not bound")}");
            var pieces = new PieceCache(_gameDir, rig, notes);
            string archive = Profile.ArchivePath(_gameDir);
            var parts = new List<CharacterPiece> { LoadPiece(archive, Body, rig, notes), LoadPiece(archive, _args.GetValueOrDefault("head") ?? Head, rig, notes) };
            if (!_args.ContainsKey("no-lasgun")) parts.Add(LasgunPiece(rig, notes));
            var assetsList = new List<AssetPiece>();
            int textured = 0, surfaces = 0;
            foreach (var piece in parts)
            {
                if (pieces.Build(piece.Name, piece, vertexColours: true) is not { } asset) { notes.Add($"{piece.Name}: nothing to draw"); continue; }
                assetsList.Add(new AssetPiece(piece.Name, asset.Mesh, asset.Skin, new Material?[asset.Surfaces.Count]));
                textured += asset.Surfaces.Count(s => s.HasDiffuse); surfaces += asset.Surfaces.Count;
            }
            var assets = new MarineAssets(rig, assetsList, textured, surfaces, pieces.Notes);
            string clip = _args.GetValueOrDefault("anim") ?? "IG_NAV_Idle_01";
            _viewer = new CharacterViewer(assets, clip, gameSpaceRoot: true) { Name = "Guardsman" };
            AddChild(_viewer);
            if (_args.ContainsKey("time")) _viewer.Pose(clip, float.TryParse(_args["time"], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0f, 0f);
            else _viewer.Play(clip, 0f);
            GD.Print($"guardsman test: {assetsList.Count} pieces, {textured}/{surfaces} surfaces with original diffuse; clip '{clip}' " +
                     $"{(_viewer.ClipTime is { } ct ? $"length {ct.Duration:F2} s" : "NOT FOUND")}");
            AddStage(rig);
        }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException))
        {
            GD.PrintErr("guardsman test: " + ex);
            GetTree().Quit(1);
            return;
        }
        foreach (var n in notes.Distinct()) GD.Print("  note: " + n);
        _waitFrames = 12;
        if (_shots.Length > 0) _viewer!.RotationDegrees = new Vector3(0, _shots[0], 0);
    }

    void AddStage(RigAssets rig)
    {
        var skeleton = rig.Rig.Skeleton;
        var model = skeleton.ModelSpace(skeleton.ReferencePose);
        int head = skeleton.BoneNames.ToList().FindIndex(n => n.Equals("RIG_Head", StringComparison.OrdinalIgnoreCase));
        float height = (head < 0 ? 1.7f : model[head].Translation.Z) + 0.25f;
        var target = new Vector3(0, height * 0.5f, 0);
        var camera = new Camera3D { Fov = 35f, Near = 0.05f, Far = 60f, Current = true };
        AddChild(camera);
        camera.Position = target + new Vector3(0, 0.12f, -1f).Normalized() * height * 1.75f;
        camera.LookAt(target, Vector3.Up);
        AddChild(new DirectionalLight3D { LightEnergy = 1.1f, ShadowEnabled = true, RotationDegrees = new Vector3(-40f, 205f, 0f) });
        AddChild(new DirectionalLight3D { LightEnergy = 0.45f, RotationDegrees = new Vector3(-20f, 30f, 0f) });
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.18f, 0.2f, 0.23f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.57f, 0.6f), AmbientLightEnergy = 0.7f,
            },
        });
    }

    public override void _Process(double delta)
    {
        if (_waitFrames > 0) { _waitFrames--; return; }
        if (_shots.Length == 0) { if (_args.ContainsKey("quit")) GetTree().Quit(0); return; }
        string path = System.IO.Path.Combine(_outDir, $"{_name}_{_shots[_nextShot]:0}.png");
        System.IO.Directory.CreateDirectory(_outDir);
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print(err == Error.Ok ? $"screenshot -> {path}" : $"screenshot failed: {err}");
        if (++_nextShot >= _shots.Length) { _shots = []; GetTree().Quit(0); return; }
        _viewer!.RotationDegrees = new Vector3(0, _shots[_nextShot], 0);
        _waitFrames = 3;
    }
}
