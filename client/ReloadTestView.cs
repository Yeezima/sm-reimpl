using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// M35 test aid (<c>--reload-test &lt;wargear&gt;</c>, e.g. <c>sm_mp_ranged_bolter</c>, <c>sm_mp_pistol_bolt_pistol</c>): a marine on a plain floor reloads the
/// weapon over and over with the same code the networked view uses (<see cref="ReloadPlayer"/>, <see cref="MagazineDrops"/>, <see cref="WeaponRig"/>).
/// <c>--reload-time s</c> sets the reload time (default: the weapon's, from the table in the spec), <c>--reload-shots t,t,...</c> saves screenshots
/// <c>t</c> seconds into the reload to <c>--reload-out</c> (default <c>%LOCALAPPDATA%\sm-reimpl\cache\m35-reload</c>, never inside the repository or the install)
/// and quits afterwards, <c>--reload-cam x,y,z</c> and <c>--reload-look x,y,z</c> place the camera (Godot space, the marine faces -Z), <c>--reload-pose clip</c> the base pose
/// (default the relaxed idle), <c>--reload-cancel t</c> cancels the reload at that time.
/// </summary>
public partial class ReloadTestView : Node3D
{
    readonly string _gameDir;
    readonly Dictionary<string, string?> _args;
    CharacterViewer? _character;
    WeaponRig? _rig;
    ReloadPlayer _player = new();
    MagazineDrops? _drops;
    ReloadClip? _clip;
    float _total = 1.3f, _clock;
    double _since;
    float[] _shots = [];
    int _nextShot;
    string _wargear = "";
    string _outDir = "";
    float? _cancelAt;

    public ReloadTestView(string gameDir, Dictionary<string, string?> args) { _gameDir = gameDir; _args = args; }
    public ReloadTestView() : this("", new Dictionary<string, string?>()) { }

    static float F(string? text, float fallback) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    static Vector3 V3(string? text, Vector3 fallback)
    {
        var p = text?.Split(',');
        return p is { Length: 3 } ? new Vector3(F(p[0], fallback.X), F(p[1], fallback.Y), F(p[2], fallback.Z)) : fallback;
    }

    /// <summary>The weapons' reload times (spec/behavior.md weapon table; the simulation reads them from the wargear).</summary>
    static float DefaultReloadTime(string wargear) =>
        wargear.Contains("bolt_pistol") ? 0.75f : wargear.Contains("storm") ? 2.0f : wargear.Contains("sniper") ? 2.5f : wargear.Contains("grenade") ? 0.6f : wargear.Contains("las") ? 2.25f : 1.3f;

    public override void _Ready()
    {
        _wargear = _args.GetValueOrDefault("reload-test") ?? "sm_mp_ranged_bolter";
        _total = F(_args.GetValueOrDefault("reload-time"), DefaultReloadTime(_wargear));
        _shots = (_args.GetValueOrDefault("reload-shots") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => F(x, 0f)).OrderBy(x => x).ToArray();
        _cancelAt = _args.TryGetValue("reload-cancel", out var c) ? F(c, 0.5f) : null;
        _outDir = _args.GetValueOrDefault("reload-out") ?? System.IO.Path.Combine(GameInstall.DataRoot, "cache", "m35-reload");
        var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
        if (SmFormats.InstallPaths.IsWithin(System.IO.Path.GetFullPath(_outDir), repoRoot) || SmFormats.InstallPaths.IsWithin(System.IO.Path.GetFullPath(_outDir), _gameDir))
        { GD.PrintErr($"reload test: {_outDir} is inside the repository or the game folder; screenshots refused"); _shots = []; }

        // The room: a floor (the static world the magazines bounce on) and light.
        var checker = Image.CreateEmpty(2, 2, false, Image.Format.Rgb8);
        checker.SetPixel(0, 0, new Color(0.34f, 0.35f, 0.37f)); checker.SetPixel(1, 1, new Color(0.34f, 0.35f, 0.37f));
        checker.SetPixel(1, 0, new Color(0.22f, 0.23f, 0.25f)); checker.SetPixel(0, 1, new Color(0.22f, 0.23f, 0.25f));
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(20, 20) },
            MaterialOverride = new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(checker), TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Uv1Scale = new Vector3(20, 20, 1) },
        });
        var floor = new StaticBody3D { Name = "Floor", CollisionLayer = Ragdoll.WorldLayer, CollisionMask = 0 };
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D { Plane = new Plane(Vector3.Up, 0f) } });
        AddChild(floor);
        AddChild(new DirectionalLight3D { LightEnergy = 1.2f, ShadowEnabled = true, RotationDegrees = new Vector3(-45, 25, 0) });
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.12f, 0.12f, 0.14f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.55f, 0.58f), AmbientLightEnergy = 0.9f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        });
        var camera = new Camera3D { Current = true, Fov = 50f, Near = 0.05f, Far = 100f };
        AddChild(camera);
        camera.Position = V3(_args.GetValueOrDefault("reload-cam"), new Vector3(1.5f, 1.45f, -2.7f));
        camera.LookAt(V3(_args.GetValueOrDefault("reload-look"), new Vector3(0, 0.9f, 0)), Vector3.Up);

        // The marine in game space (Z up) under a rotated parent, as in the networked view; it faces game +Y.
        var space = new Node3D { Name = "GameSpace", Basis = new Basis(new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0)) };
        AddChild(space);
        var notes = new List<string>();
        try
        {
            var assets = _assets = MarineAssets.Load(_gameDir, notes);
            string idle = _args.GetValueOrDefault("reload-pose") ?? "SM_NAV_IdleL_S_Relaxed_LE";
            _character = new CharacterViewer(assets, idle, gameSpaceRoot: false) { Name = "Marine" };
            space.AddChild(_character);
            var weaponAssets = WeaponAssets.Load(_gameDir, assets, [_wargear], notes);
            _rig = new WeaponRig(_character.Skeleton ?? throw new InvalidOperationException("no skeleton"), weaponAssets);
            bool pistol = _wargear.Contains("_pistol_", StringComparison.Ordinal);
            _rig.Configure((0, 0, 0, "viewer", null), pistol ? null : _wargear, pistol ? _wargear : null, null, [], false);
            _rig.Update(pistol ? WeaponSlot.Pistol : WeaponSlot.Ranged, false, true, 0f, 0.6f);
            var data = ReloadAnimations.Load(_gameDir);
            _clip = ReloadAnimations.FamilyOf(_wargear) is { } family ? data.For(family) : null;
            GD.Print($"reload test: {_wargear}: " + (_clip is null ? "no reload clip for this weapon"
                : $"{_clip.Clip} ({_clip.ClipLength:F2} s) stretched to {_total:F2} s (rate {_clip.PlaybackRate(_total):F2}), magazine out {_clip.MagazineOut?.ToString("F3") ?? "-"} in {_clip.MagazineIn?.ToString("F3") ?? "-"}; " +
                  $"magazine part: {weaponAssets.Get(weaponAssets.Catalogue.Held(_wargear)!)?.Magazine?.Node ?? "none"}"));
        }
        catch (Exception ex) { GD.PrintErr("reload test: " + ex); }
        foreach (var n in notes) GD.Print("  note: " + n);
        _drops = new MagazineDrops { Name = "MagazineDrops" };
        AddChild(_drops);
        _since = -0.5;   // the idle pose first
    }

    /// <summary>
    /// <c>--reload-probe</c>: poses the reload clip at 50 evenly spaced fractions of the clip (no physics, the magazine stays in the weapon) and prints how far the
    /// weapon's magazine is from each hand bone and from where it was at the start: the evidence for when the hand takes the magazine out and the new one is loaded.
    /// </summary>
    void Probe()
    {
        var skeleton = _character!.Skeleton!;
        var hands = Enumerable.Range(0, skeleton.GetBoneCount()).Where(b => skeleton.GetBoneName(b).Contains("hand", StringComparison.OrdinalIgnoreCase)).ToArray();
        GD.Print("probe: hand bones " + string.Join(", ", hands.Select(b => skeleton.GetBoneName(b))));
        if (!_rig!.TryMagazine(out var mag, out _)) { GD.Print("probe: no magazine"); return; }
        Vector3? start = null;
        for (int i = 0; i <= 50; i++)
        {
            float f = i / 50f;
            _character.ClearPoseLayer();
            _character.SetPoseLayer(_clip!.Clip, f * _clip.ClipLength, 1f, false, _clip.BoneMask);
            _character.ForcePose();
            _rig.TryMagazine(out _, out var world);
            var at = world.Origin;
            start ??= at;
            var parts = hands.Select(b => $"{skeleton.GetBoneName(b)} {(skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(b)).Origin.DistanceTo(at):F3}");
            GD.Print($"probe f {f:F2}: magazine moved {at.DistanceTo(start.Value):F3} m; to " + string.Join(", ", parts));
        }
    }

    public override void _Process(double delta)
    {
        if (_character is null || _rig is null || _clip is null) return;
        if (_args.ContainsKey("reload-probe") && !_probed) { _probed = true; Probe(); GetTree().Quit(0); return; }
        _since += delta;
        if (_since < 0) return;
        float dt = (float)delta;
        if (!_started)
        {
            _started = true; _clock = 0f;
            _player.Begin(_clip, _total, 0f, _rig, _clip.MagazineIn is null ? null : _hands.For(this, _assets!, _rig, _clip.Clip));
            GD.Print($"reload test: begins at {_since:F2} s");
        }
        _clock += dt;
        if (_player.Active)
        {
            if (_cancelAt is { } cancel && _clock >= cancel) { _player.Finish(false); GD.Print("reload test: cancelled"); }
            else if (_clock >= _total) _player.Finish(true);
        }
        _player.Advance(dt, null, 1f / 30f);
        _character.ClearPoseLayer();
        _player.ApplyPose(_character);
        if (_player.ApplyMagazine(_rig, _drops, Vector3.Zero)) GD.Print($"reload test: magazine dropped at {_clock:F3} s (clip fraction {_player.ClipFraction:F3}), live {_drops!.Live}");
        // Screenshots at the given times into the reload; quit two seconds after the last (the magazine lies on the floor by then).
        if (_nextShot < _shots.Length && _clock >= _shots[_nextShot] - 1e-4f)
        {
            string name = $"{_wargear}_{_shots[_nextShot]:0.00}s.png";
            System.IO.Directory.CreateDirectory(_outDir);
            string path = System.IO.Path.Combine(_outDir, name);
            var err = GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print(err == Error.Ok ? $"screenshot -> {path} (clock {_clock:F3} s, layer {_player.Weight:F2}, clip fraction {_player.ClipFraction:F3}, {_drops!.Live} magazines live)" : $"screenshot failed: {err}");
            _nextShot++;
        }
        if (_shots.Length > 0 && _nextShot >= _shots.Length) { _quitIn ??= 0.4; _quitIn -= delta; if (_quitIn <= 0) GetTree().Quit(0); }
        // Without screenshots it repeats every few seconds.
        if (_shots.Length == 0 && _clock > _total + 3f) { _started = false; _drops!.Clear(); _rig.ShowMagazines(); }
    }

    bool _started, _probed;
    readonly ReloadHandTimes _hands = new();
    MarineAssets? _assets;
    double? _quitIn;
}
