using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmContent;
using SmContent.Extras.Musou;

namespace SmClient;

/// <summary>
/// Test aid (Musou Mode, a singleplayer dependency: docs/discovery.md "Warboss model"): the Musou Warboss exactly as the crowd builds it (<see cref="WarbossLooks"/>: the campaign's
/// <c>warboss_regular</c> body and <c>warboss_head_regular</c> head on <c>havok_warboss_regular__hkxx</c>), on a plain stage without a server, next to a 2 m grey post for scale.
/// <c>--warboss-test</c> with <c>--clip run|idle|attack|stagger|death</c> (default idle), <c>--time &lt;s&gt;</c> (hold the clip there), <c>--scale &lt;f&gt;</c> (default 1, the model's own size),
/// <c>--shots 0,180</c> (turntable angles, 0 = front) saved to <c>--out</c> (default <c>%LOCALAPPDATA%\sm-reimpl\cache\previews\warboss-model</c>; never inside the repository or the install)
/// as <c>&lt;--name&gt;_&lt;angle&gt;.png</c>, then quit (a capture needs <c>godot.cmd --windowed</c>). Prints the look's notes (pieces, blocks drawn and hidden, bones its rig lacks).
/// **Placeholder demonstration** around original assets read at run time.
/// </summary>
public partial class WarbossTestView : Node3D
{
    readonly string _gameDir;
    readonly Dictionary<string, string?> _args;
    WarbossLooks? _looks;
    Node3D? _turntable;
    float[] _shots = [];
    int _nextShot, _waitFrames = -1;
    string _outDir = "", _name = "warboss";

    public WarbossTestView(string gameDir, Dictionary<string, string?> args) { _gameDir = gameDir; _args = args; }
    public WarbossTestView() : this("", new Dictionary<string, string?>()) { }

    public override void _Ready()
    {
        if (_gameDir.Length == 0) { GD.PrintErr("warboss test: no game folder"); GetTree().Quit(1); return; }
        _name = _args.GetValueOrDefault("name") ?? "warboss";
        if (_name.Length == 0 || _name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || _name.Contains("..", StringComparison.Ordinal)) _name = "warboss";
        _outDir = _args.GetValueOrDefault("out") ?? System.IO.Path.Combine(GameInstall.DataRoot, "cache", "previews", "warboss-model");
        foreach (var x in (_args.GetValueOrDefault("shots") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) _shots = [.. _shots, v];
        var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
        try
        {
            string fullOut = System.IO.Path.GetFullPath(_outDir);
            if (SmFormats.InstallPaths.IsWithin(fullOut, repoRoot) || SmFormats.InstallPaths.IsWithin(fullOut, _gameDir))
            { GD.PrintErr($"warboss test: {_outDir} is inside the repository or the game folder; screenshots refused"); _shots = []; }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { GD.PrintErr($"warboss test: bad --out: {ex.Message}"); _shots = []; }
        _looks = new WarbossLooks(_gameDir);
    }

    public override void _Process(double delta)
    {
        if (_looks is null) return;
        if (_turntable is null)
        {
            var look = _looks.Step();
            if (!_looks.Done) return;
            foreach (var n in _looks.Notes.Distinct()) GD.Print("  note: " + n);
            if (look is null) { GD.PrintErr("warboss test: the Warboss model could not be built"); GetTree().Quit(1); return; }
            Build(look);
            return;
        }
        if (_waitFrames > 0) { _waitFrames--; return; }
        if (_shots.Length == 0) { if (_args.ContainsKey("quit")) GetTree().Quit(0); return; }
        string path = System.IO.Path.Combine(_outDir, $"{_name}_{_shots[_nextShot]:0}.png");
        System.IO.Directory.CreateDirectory(_outDir);
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print(err == Error.Ok ? $"screenshot -> {path}" : $"screenshot failed: {err}");
        if (++_nextShot >= _shots.Length) { _shots = []; GetTree().Quit(0); return; }
        _turntable.RotationDegrees = new Vector3(0, _shots[_nextShot], 0);
        _waitFrames = 3;
    }

    void Build(MusouLooks.Look look)
    {
        int clip = (_args.GetValueOrDefault("clip") ?? "idle").ToLowerInvariant() switch
        {
            "run" => MusouLooks.Run, "attack" => MusouLooks.Attack, "stagger" => MusouLooks.Stagger, "death" => MusouLooks.Death, _ => MusouLooks.Idle,
        };
        string? clipName = look.Clips[clip]?.Name;
        float scale = float.TryParse(_args.GetValueOrDefault("scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0.01f ? s : 1f;
        var viewer = new CharacterViewer(look.Assets, clipName, gameSpaceRoot: true) { Name = "Warboss" };
        _turntable = new Node3D { Name = "Turntable", Scale = Vector3.One * scale };
        _turntable.AddChild(viewer);
        AddChild(_turntable);
        if (clipName is not null)
        {
            if (_args.ContainsKey("time")) viewer.Pose(clipName, float.TryParse(_args["time"], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0f, 0f);
            else viewer.Play(clipName, 0f);
        }
        float height = MathF.Max(1f, _looks!.Height * scale);
        GD.Print($"warboss test: {look.Name}, {look.Assets.Pieces.Count} pieces ({string.Join(", ", look.Assets.Pieces.Select(p => p.Name))}), " +
                 $"{look.Assets.TexturedSurfaces}/{look.Assets.Surfaces} surfaces with original diffuse; model height {_looks.Height:0.00} m x{scale:0.##}; clip {clipName ?? "none"}");
        AddStage(height);
        _waitFrames = 12;
        if (_shots.Length > 0) _turntable.RotationDegrees = new Vector3(0, _shots[0], 0);
    }

    void AddStage(float height)
    {
        var post = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.1f, 2f, 0.1f) }, Position = new Vector3(height * 0.45f, 1f, 0f) };   // 2 m for scale
        AddChild(post);
        AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(height * 4f, height * 4f) } });
        var target = new Vector3(0, height * 0.5f, 0);
        var camera = new Camera3D { Fov = 35f, Near = 0.05f, Far = 200f, Current = true };
        AddChild(camera);
        camera.Position = target + new Vector3(0, 0.12f, -1f).Normalized() * height * 1.9f;
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
}
