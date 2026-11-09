using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Test aid (Musou Mode, musou-big-ork-deaths 2026-11-09): the real Musou crowd (<see cref="MusouLooks"/>, <see cref="MusouCrowd"/>) on a plain stage without a server. Each kind in
/// <c>--kinds BossNob,Warboss,..</c> (default Nob,BossNob,Warboss) runs across the stage at its simulation speed (<c>--speed</c> m/s to override) for <c>--run</c> s (default 3), then dies where it stands
/// (the crowd's body path, as a death event hands it over) and lies; one kind after the other. <c>--every s</c> (default 0.25) saves frames <c>&lt;--name&gt;-&lt;kind&gt;-NN.png</c> to <c>--out</c>
/// (default <c>%LOCALAPPDATA%\sm-reimpl\cache\previews\musou-crowd</c>; never inside the repository or the install) for <c>--seconds</c> s per kind (default 8), from the side at a distance fitting the
/// body (<c>--cam-dist</c> to override; <c>--eye-height m</c> puts the camera at a player's eye height), then quits (a capture needs <c>godot.cmd --windowed</c>). <c>--far</c> draws it beyond the skinned distance (the vertex-animation tier). Prints a MUSOUCROWDTEST
/// line per kind with the clip rate the crowd chose while it ran. **Placeholder demonstration** around original assets read at run time.
/// </summary>
public partial class MusouCrowdTestView : Node3D
{
    static readonly Basis GameToGodot = new(new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0));

    readonly string _gameDir;
    readonly Dictionary<string, string?> _args;
    MusouLooks? _looks;
    MusouCrowd? _crowd;
    Node3D? _space;
    Camera3D? _camera;
    MusouEnemyType[] _kinds = [];
    int _kind = -1;
    float _t, _nextShot, _every = 0.25f, _run = 3f, _seconds = 8f, _speed;
    int _shot;
    bool _dead, _far;
    string _outDir = "", _name = "crowd";
    ushort _id = 1;
    readonly List<MusouShown> _shown = new();

    public MusouCrowdTestView(string gameDir, Dictionary<string, string?> args) { _gameDir = gameDir; _args = args; }
    public MusouCrowdTestView() : this("", new Dictionary<string, string?>()) { }

    static float Arg(Dictionary<string, string?> a, string key, float fallback) =>
        float.TryParse(a.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public override void _Ready()
    {
        if (_gameDir.Length == 0) { GD.PrintErr("musou crowd test: no game folder"); GetTree().Quit(1); return; }
        _kinds = (_args.GetValueOrDefault("kinds") ?? "Nob,BossNob,Warboss").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => Enum.TryParse<MusouEnemyType>(k, true, out var t) ? t : (MusouEnemyType?)null).Where(t => t is not null).Select(t => t!.Value).ToArray();
        _every = MathF.Max(0.02f, Arg(_args, "every", 0.25f)); _run = Arg(_args, "run", 3f); _seconds = Arg(_args, "seconds", 8f); _speed = Arg(_args, "speed", 0f);
        _far = _args.ContainsKey("far");
        _name = _args.GetValueOrDefault("name") ?? "crowd";
        if (_name.Length == 0 || _name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || _name.Contains("..", StringComparison.Ordinal)) _name = "crowd";
        _outDir = _args.GetValueOrDefault("out") ?? System.IO.Path.Combine(SmContent.GameInstall.DataRoot, "cache", "previews", "musou-crowd");
        var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
        string full = System.IO.Path.GetFullPath(_outDir);
        if (SmFormats.InstallPaths.IsWithin(full, repoRoot) || SmFormats.InstallPaths.IsWithin(full, _gameDir)) { GD.PrintErr($"musou crowd test: {_outDir} is inside the repository or the game folder"); GetTree().Quit(1); return; }
        var orks = OrkAssets.Shared(_gameDir);
        _looks = new MusouLooks(_gameDir, orks, () => null);
        _space = new Node3D { Name = "GameSpace", Basis = GameToGodot };
        AddChild(_space);
        _crowd = new MusouCrowd(_looks);
        _space.AddChild(_crowd);
        AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(400f, 400f) } });
        _camera = new Camera3D { Fov = 40f, Near = 0.1f, Far = 600f, Current = true };
        AddChild(_camera);
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

    float SpeedOf(MusouEnemyType type)
    {
        if (_speed > 0f) return _speed;
        var rules = new MusouRules();
        return (int)type < rules.Types.Count ? rules.Of(type).Speed : 4.4f;
    }

    public override void _Process(double delta)
    {
        if (_looks is null || _crowd is null || _camera is null) return;
        float dt = 1f / 30f;   // a fixed step, so the frames are the same run to run
        if (_kind < 0)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 50 && _looks.Step() is { } built) GD.Print($"musou crowd test: look {built.Name} ready");
            _looks.StopWaiting();
            foreach (var n in _looks.Notes) GD.Print("  note: " + n);
            _looks.Notes.Clear();
            _crowd.Frame(_shown, Vector3.Zero, dt);   // bakes the looks
            if (!_kinds.All(k => _looks.For(k) is { } l && (l.Vat is not null || l.BakeFailed || _crowd.NoVat))) return;
            _kind = 0; Start();
            return;
        }
        var kind = _kinds[_kind];
        var look = _looks.For(kind)!;
        float height = MathF.Max(2f, LookHeight(look));
        float speed = SpeedOf(kind);
        _t += dt;
        _shown.Clear();
        var at = new Vector3(-speed * _run * 0.5f + speed * MathF.Min(_t, _run), 0f, 0f);
        if (_t < _run) _shown.Add(new MusouShown(_id, new System.Numerics.Vector3(at.X, at.Y, at.Z), -MathF.PI / 2f, kind, MusouEnemyState.Seek, _t));
        else if (!_dead)
        {
            _dead = true;
            _crowd.AddBody(new MusouDeathShown(_id, kind, new System.Numerics.Vector3(at.X, at.Y, at.Z), -MathF.PI / 2f, new System.Numerics.Vector3(0f, 1f, 0.3f), MusouDeathKind.Shot, 0, 0), 0f, keepSkeleton: true);
            GD.Print($"MUSOUCROWDTEST {kind}: {look.Name} x{look.Scale:0.##}, height {height:0.0} m, speed {speed:0.0} m/s, death clip {look.Clips[MusouLooks.Death]?.Name ?? "none"} ({look.Clips[MusouLooks.Death]?.Animation.Duration ?? 0f:0.00} s)");
        }
        float dist = Arg(_args, "cam-dist", height * 2.2f) + (_far ? MusouCrowd.NearMetres + 10f : 0f);
        var target = new Vector3(at.X, at.Y, height * 0.45f);
        var eye = target + new Vector3(0f, -dist, height * 0.25f);
        if (_args.ContainsKey("eye-height")) { eye.Z = Arg(_args, "eye-height", 1.9f); target = new Vector3(at.X, at.Y, MathF.Max(eye.Z, height * 0.3f)); }   // a player's eye: low, looking up at the body
        _camera.Position = GameToGodot * eye;
        _camera.LookAt(GameToGodot * target, Vector3.Up);
        _crowd.Frame(_shown, eye, dt);
        if (_t + 1e-4f >= _nextShot)
        {
            System.IO.Directory.CreateDirectory(_outDir);
            string path = System.IO.Path.Combine(_outDir, $"{_name}-{kind}-{_shot:00}.png");
            GetViewport().GetTexture().GetImage().SavePng(path);
            _shot++; _nextShot += _every;
        }
        if (_t >= _seconds)
        {
            if (++_kind >= _kinds.Length) { GD.Print("MUSOUCROWDTEST done"); GetTree().Quit(0); return; }
            Start();
        }
    }

    void Start() { _t = 0f; _nextShot = 0f; _shot = 0; _dead = false; _id++; }

    static float LookHeight(MusouLooks.Look look)
    {
        float top = float.NegativeInfinity, bottom = float.PositiveInfinity;
        foreach (var piece in look.Assets.Pieces) { var box = piece.Mesh.GetAabb(); top = MathF.Max(top, box.End.Z); bottom = MathF.Min(bottom, box.Position.Z); }
        return top > bottom ? (top - bottom) * look.Scale : 0f;
    }
}
