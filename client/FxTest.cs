using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// M25b test aid (<c>--fx name[,name...]</c>): plays original effects in a plain room (a floor and a wall at z = 0, the effect at (0, 1.2, 0)) so
/// that their look can be judged without a server. <c>--fx-dir x,y,z</c> sets the effect's axis (default +Y; <c>0,0,1</c> is a hit on the wall),
/// <c>--fx-target d</c> a beam target <c>d</c> metres along the axis (tracers), <c>--fx-scale s</c>, <c>--fx-spacing m</c> (several effects side by side), <c>--fx-age t</c> holds the effects at that age
/// (the screenshot then shows that moment), otherwise they restart every <c>--fx-period</c> seconds (default 2.5); <c>--fx-cam x,y,z</c> and
/// <c>--fx-look x,y,z</c> place the camera; <c>--fx-rot ccw</c> turns rotated atlas sprites counter-clockwise (the default is clockwise: a bolt tracer's bright head then leads);
/// <c>--pound-test</c> (M41) plays the ground pound's impact as the networked client does (<see cref="GroundPoundFx"/>: the effect named first in <c>--fx</c>, default <c>jumppack_land_heavy</c>, with its
/// collisions, the dark ring at <c>--pound-radius</c> metres (default 7, the simulation's pound radius) and the physics chunks on a floor; <c>--fx-pos</c> sets the point, default 0,0,3 on the floor);
/// <c>--stomp-test</c> (fx/impact-pass) plays a stomp's impact as the networked client does (<see cref="StompFx"/>, the first effect named, default <c>interrupt_stomp_dmg</c>, at <c>--pound-radius</c>; <c>--stomp-ring</c> picks the shockwave's sprite);
/// <c>--fx-shots t1,t2,...</c> with <c>--fx-shot-base file.png</c> (M41) saves the view at those seconds after the first effect starts (<c>file_001.png</c>, ...) and then quits;
/// <c>--fx-wargear csm_mp_ranged_plasma_gun</c> plays the effects as that weapon does (a Chaos plasma weapon recolours the loyalist-named plasma effects purple, <see cref="PlasmaColour"/>);
/// <c>--fx-rein f</c> (feel/muzzle-smoke) plays the effects with their smoke elements reined in to that fraction (1: the data's look), <c>--fx-decals</c> (M38) draws only the effects' decals, <c>--fx-head m/s</c> sets a tracer's bright-head speed (what the networked client does for long shots), <c>--fx-sprite name</c> shows one atlas sprite as a big quad (to see how it is stored).
/// </summary>
public partial class FxTestView : Node3D
{
    readonly string _gameDir;
    readonly Dictionary<string, string?> _args;
    FxPlayer? _player;
    Camera3D? _camera;
    string[] _names = [];
    double _since = 1e9;
    double? _holdAge;
    double _period = 2.5;
    readonly List<FxEffect> _live = new();
    int _frames;
    GroundShards? _shards;
    double[] _shotTimes = [];
    int _shotNext;

    public FxTestView(string gameDir, Dictionary<string, string?> args) { _gameDir = gameDir; _args = args; }

    public FxTestView() : this("", new Dictionary<string, string?>()) { }

    static Vector3 V3(string? text, Vector3 fallback)
    {
        if (text is null) return fallback;
        var p = text.Split(',');
        if (p.Length != 3) return fallback;
        return new Vector3(F(p[0], fallback.X), F(p[1], fallback.Y), F(p[2], fallback.Z));
    }

    static float F(string? text, float fallback) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public override void _Ready()
    {
        _names = (_args.GetValueOrDefault("fx") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (_args.ContainsKey("pound-test") && _names.Length == 0) _names = ["jumppack_land_heavy", "jumppack_land_light"];
        // fx/impact-pass: --stomp-test plays a stomp's impact as the networked client does (StompFx): the first effect named, the others compiled for the shockwave's ring sprite.
        if (_args.ContainsKey("stomp-test")) { _args["pound-test"] = null; if (_names.Length == 0) _names = ["interrupt_stomp_dmg", "knockback_stomp_dmg_furied", "thunderhammer_dmg_displace_heavy"]; }
        _holdAge = _args.TryGetValue("fx-age", out var a) ? F(a, 0.1f) : null;
        _period = F(_args.GetValueOrDefault("fx-period"), 2.5f);
        _shotTimes = (_args.GetValueOrDefault("fx-shots") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => (double)F(x, 0f)).ToArray();
        FxBatch.RotatedClockwise = !string.Equals(_args.GetValueOrDefault("fx-rot"), "ccw", StringComparison.OrdinalIgnoreCase);

        var checker = Image.CreateEmpty(2, 2, false, Image.Format.Rgb8);
        checker.SetPixel(0, 0, new Color(0.34f, 0.35f, 0.37f)); checker.SetPixel(1, 1, new Color(0.34f, 0.35f, 0.37f));
        checker.SetPixel(1, 0, new Color(0.22f, 0.23f, 0.25f)); checker.SetPixel(0, 1, new Color(0.22f, 0.23f, 0.25f));
        var floorMaterial = new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(checker), TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Uv1Scale = _args.ContainsKey("pound-test") ? new Vector3(20, 20, 1) : new Vector3(10, 10, 1) };
        AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = _args.ContainsKey("pound-test") ? new Vector2(40, 40) : new Vector2(20, 20) }, MaterialOverride = floorMaterial });
        var wallMaterial = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.43f, 0.4f) };
        AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(20, 8) }, MaterialOverride = wallMaterial, Position = new Vector3(0, 4, _args.ContainsKey("pound-test") ? -19f : -0.02f) });
        AddChild(new DirectionalLight3D { LightEnergy = 1.1f, ShadowEnabled = true, RotationDegrees = new Vector3(-45, 25, 0) });
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.12f, 0.12f, 0.14f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.5f, 0.5f, 0.52f), AmbientLightEnergy = 0.8f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        });
        if (_args.ContainsKey("pound-test"))
        {
            // The level the chunks fall on: the same physics layer the networked client's ragdolls and drops use.
            var ground = new StaticBody3D { Name = "Ground", CollisionLayer = Ragdoll.WorldLayer, CollisionMask = 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D { Plane = new Plane(Vector3.Up, 0f) } });
            AddChild(ground);
            _shards = new GroundShards { Name = "GroundShards" };
            AddChild(_shards);
        }
        _camera = new Camera3D { Current = true, Fov = 60f, Near = 0.05f, Far = 500f };
        AddChild(_camera);
        _camera.Position = V3(_args.GetValueOrDefault("fx-cam"), new Vector3(2.0f, 1.5f, 5.0f));
        _camera.LookAt(V3(_args.GetValueOrDefault("fx-look"), new Vector3(0, 1.2f, 1.5f)), Vector3.Up);

        try
        {
            var library = FxLibrary.Load(_gameDir);
            _player = new FxPlayer { Name = "Effects", DecalsOnly = _args.ContainsKey("fx-decals") };
            AddChild(_player);
            if (!_player.Initialize(library, _gameDir)) GD.PrintErr("fx: " + _player.Status);
            else
            {
                // The player never reads the package itself: the named effects (and their decals) are read here in one pass.
                // --fx-length s (F-D1): compiles the named effects with elements of duration 0 lasting s seconds (the death effects' play length; CSystem.From).
                var zero = _args.ContainsKey("fx-length") ? _names.ToDictionary(n => n, _ => F(_args.GetValueOrDefault("fx-length"), 1f), StringComparer.Ordinal) : null;
                var compiled = NetworkView.CompileSystems(library, _names, new List<string>(), zero);
                var collide = _args.ContainsKey("pound-test") ? _names.ToHashSet(StringComparer.Ordinal) : null;
                _player.Preload(compiled, library.Textures(FxPlayer.DecalNames(compiled.Values, collide)), collide);
                if (_args.ContainsKey("stomp-test"))
                {
                    var shockwave = StompFx.Build(compiled.Values, _args.GetValueOrDefault("stomp-ring"));
                    if (shockwave is not null) _player.Preload(new Dictionary<string, CSystem> { [StompFx.ShockwaveName] = shockwave }, new Dictionary<string, byte[]>());
                    GD.Print($"fx stomp-test: shockwave {(shockwave is null ? "none" : ((CRing)shockwave.Roots[0]).Material.Name)}; compiled {string.Join(", ", compiled.Keys)}");
                }
                GD.Print("fx test: " + _player.Status + "; " + _player.MeshStatus);
                if (_args.GetValueOrDefault("fx-sprite") is { } sprite)
                {
                    var rect = library.Page.Rects.FirstOrDefault(r => r.Name.Equals(sprite, StringComparison.OrdinalIgnoreCase));
                    if (rect is null) GD.PrintErr($"fx: no sprite '{sprite}' in the page");
                    else
                    {
                        GD.Print($"fx sprite {rect.Name}: {rect.Width * 4096:F0} x {rect.Height * 4096:F0} px at {rect.X * 4096:F0}, {rect.Y * 4096:F0}, rotated {rect.Rotated}");
                        var material = new SmFormats.Fx.FxMaterial(rect.Name, rect.Hash, 1, 0, F(_args.GetValueOrDefault("fx-tx"), 1f), F(_args.GetValueOrDefault("fx-ty"), 1f), false, 0);
                        _player.DebugDraw = batch =>
                        {
                            if (!batch.Sprite(material, 0, out var r)) return;
                            float w = F(_args.GetValueOrDefault("fx-scale"), 3f);
                            // Sprite space is the quad: U along +X, V down; its size follows the unrotated shape.
                            float aspect = rect.Rotated ? rect.Width / rect.Height : rect.Height / rect.Width;
                            aspect = rect.Rotated ? (rect.Height / rect.Width) : (rect.Height / rect.Width);
                            batch.Add(true, new Vector3(0, 1.5f, 1.5f), new Vector3(w, 0, 0), new Vector3(0, w * (rect.Rotated ? rect.Width / rect.Height : rect.Height / rect.Width), 0), Colors.White, r);
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr("fx test: " + ex.Message);
        }
    }

    void Spawn()
    {
        if (_player is null) return;
        foreach (var e in _live) e.Life = 0;
        _live.Clear();
        if (_args.ContainsKey("stomp-test"))
        {
            float radius = F(_args.GetValueOrDefault("pound-radius"), 7f);
            PoundRing.Texture(radius, wait: true);
            var stomp = new[] { new SmContent.FxPlay("master_fx", _names[0], F(_args.GetValueOrDefault("fx-scale"), 1f), 1f, false, "self", "", "", "", "world", "up", 0f, "stop_emitting", false, false) };
            int n = StompFx.Play(_player, stomp, V3(_args.GetValueOrDefault("fx-pos"), new Vector3(0, 0, 3f)), Vector3.Up, radius, _args.ContainsKey("no-shards") ? null : _shards, "stone", _frames, near: true);
            GD.Print($"fx stomp-test: {_names[0]} at radius {radius:F1}: {n} effects, {_shards?.Spawned} chunks thrown");
            return;
        }
        if (_args.ContainsKey("pound-test"))
        {
            // The first name at the data's scale (or --fx-scale), any others as the pound's dust (GroundPoundFx.DustScale).
            var plays = _names.Select((n, i) => new SmContent.FxPlay("master_fx", n, i == 0 ? F(_args.GetValueOrDefault("fx-scale"), 1f) : GroundPoundFx.DustScale, 1f, false, "self", "", "", "", "world", "up", 0f, "stop_emitting", false, false)).ToList();
            PoundRing.Texture(F(_args.GetValueOrDefault("pound-radius"), 7f), wait: true);   // the networked client makes it in the background; the test view waits
            var started = GroundPoundFx.Play(_player, plays, V3(_args.GetValueOrDefault("fx-pos"), new Vector3(0, 0, 3f)), Vector3.Up, F(_args.GetValueOrDefault("pound-radius"), 7f), _shards, "stone", _frames);
            foreach (var e in started)
            {
                if (_holdAge is { } holdAge) e.Age = holdAge;
                e.Life = _holdAge is not null ? double.PositiveInfinity : e.Life;
                _live.Add(e);
            }
            GD.Print($"fx pound-test: {string.Join("+", _names)}: {started.Count} effects, {PoundRing.Texture(F(_args.GetValueOrDefault("pound-radius"), 7f), wait: true)?.GetWidth()} px ring, {_shards?.Spawned} chunks thrown");
            return;
        }
        var axis = V3(_args.GetValueOrDefault("fx-dir"), Vector3.Up).Normalized();
        var origin = V3(_args.GetValueOrDefault("fx-pos"), new Vector3(0, 1.2f, 1.5f));
        float scale = F(_args.GetValueOrDefault("fx-scale"), 1f);
        Vector3? target = _args.TryGetValue("fx-target", out var t) ? origin + axis * F(t, 20f) : null;
        var basis = AxesFromY(axis);
        float spacing = F(_args.GetValueOrDefault("fx-spacing"), 0f);
        for (int i = 0; i < _names.Length; i++)
        {
            var name = _names[i];
            var place = origin + new Vector3(spacing * (i - (_names.Length - 1) / 2f), 0, 0);
            // --fx-wargear <file> plays the effects as that weapon would (the Chaos plasma recolouring, SmContent.PlasmaColour).
            var tint = PlasmaColour.TintFor(_args.GetValueOrDefault("fx-wargear") ?? "", name) is { } rgb ? new Color(rgb.R, rgb.G, rgb.B) : (Color?)null;
            var e = _player.Play(name, place, basis, scale, target is { } tg ? tg + (place - origin) : null, null, 0f, 0.0, repeat: false, tint: tint, smokeRein: F(_args.GetValueOrDefault("fx-rein"), 1f));
            if (e is null) { GD.PrintErr($"fx: '{name}' not found"); continue; }
            if (_args.TryGetValue("fx-head", out var head)) e.HeadSpeed = F(head, 0f);
            if (_holdAge is { } age) e.Age = age;
            e.Life = _holdAge is not null ? double.PositiveInfinity : e.Life;
            _live.Add(e);
            GD.Print($"fx: {name} life {e.Life:F2} s, {e.System.Roots.Length} roots, streamer {e.System.HasStreamer}{(tint is { } tt ? $", tinted {tt.ToHtml(false)}" : "")}");
        }
    }

    static Basis AxesFromY(Vector3 y)
    {
        var r = MathF.Abs(y.Y) > 0.99f ? Vector3.Right : Vector3.Up;
        var x = r.Cross(y).Normalized();
        return new Basis(x, y, x.Cross(y));
    }

    public override void _Process(double delta)
    {
        if (_player is not { IsReady: true } || _camera is null) return;
        _frames++;
        _since += delta;
        if (_shotTimes.Length > 0 && _frames > 12)
        {
            if (_shotNext < _shotTimes.Length && _since >= _shotTimes[_shotNext])
            {
                string path = System.IO.Path.GetFullPath(_args.GetValueOrDefault("fx-shot-base") ?? "fx.png");
                path = System.IO.Path.ChangeExtension(path, null) + $"_{++_shotNext:000}" + ".png";
                if (SmFormats.InstallPaths.IsWithin(path, _gameDir)) { GD.Print($"screenshot refused: {path} is inside the game folder"); GetTree().Quit(1); return; }
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                GD.Print(GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok ? $"screenshot -> {path} (t {_since:F2} s, {_shards?.Live ?? 0} chunks live)" : "screenshot failed");
                if (_shotNext >= _shotTimes.Length) { GetTree().Quit(0); return; }
            }
        }
        if (_holdAge is null && _since >= _period) { _since = 0; Spawn(); }
        else if (_holdAge is not null && _live.Count == 0) Spawn();
        // Held: age does not advance. The stats go to the console once.
        _player.Update(_holdAge is null ? delta : 0.0, _camera);
        if (_frames == 30) GD.Print($"fx test: {_player.LastQuads} sprites ({_player.AdditiveQuads} additive), {_player.LastLights} lights, {_player.LiveDecals} decals, dropped {_player.DroppedTotal}; missing: {string.Join(", ", _player.Missing)}");
    }
}
