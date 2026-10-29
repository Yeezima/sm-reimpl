using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Godot;
using SmContent;
using SmContent.Customization;
using SmSim;

namespace SmClient;

/// <summary>
/// Customizer T6 test aid: a marine in a resolved look on a turntable, for screenshots and as the reference for the customizer UI's preview
/// (<see cref="AppearanceAssets"/> is the API both use). <c>--appearance-test &lt;profile.json|default&gt;</c> reads the appearance of the profile (the first of the race, or
/// <c>--appearance &lt;id&gt;</c>; "default": the race's default look); <c>--race space_marine|chaos</c>, <c>--class tactical|assault|devastator|raptor|havoc</c> (or a class key such
/// as "sm - tactical"). Overrides on top of the profile: <c>--preset &lt;scheme&gt;</c> (a chapter / warband preset), <c>--set &lt;set&gt;</c> (a set's pieces, or a predefined set),
/// <c>--piece slot=wargear,...</c>, <c>--lens r,g,b</c>, <c>--emblem-test</c> (a synthetic 128x128 BC3 emblem on the right pad), <c>--carried a,b</c> (wargear for class overrides).
/// Output: <c>--shots 0,90,180,270</c> (turntable angles, degrees; 0 = front) saved to <c>--out</c> (default <c>%LOCALAPPDATA%\sm-reimpl\cache\cust-t6</c>; never inside the
/// repository or the install) as <c>&lt;--name&gt;_&lt;angle&gt;.png</c>, then quit; <c>--closeup head|shoulder|torso</c> frames a part; <c>--turn &lt;deg/s&gt;</c> turns it live.
/// <c>--try-build</c> builds through the non-blocking <c>TryBuild</c> and reports the call times; <c>--perf &lt;n&gt;</c> builds n different looks (every set with every preset, in turn) and prints the time and the GPU memory of each.
/// </summary>
public partial class AppearanceTestView : Node3D
{
    readonly string _gameDir;
    readonly Dictionary<string, string?> _args;
    CharacterViewer? _character;
    Camera3D? _camera;
    float[] _shots = [];
    int _nextShot, _frames, _waitFrames;
    string _outDir = "", _name = "look";
    float _turn;
    bool _quit;

    public AppearanceTestView(string gameDir, Dictionary<string, string?> args) { _gameDir = gameDir; _args = args; }
    public AppearanceTestView() : this("", new Dictionary<string, string?>()) { }

    static float F(string? text, float fallback) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    static string ClassKey(CustomizationCatalog catalog, string race, string? wanted)
    {
        string prefix = race == CustomizationCatalog.Chaos ? "csm" : "sm";
        wanted ??= "tactical";
        var exact = catalog.Classes.FirstOrDefault(c => c.Key.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Key;
        var found = catalog.Classes.FirstOrDefault(c => c.Race == race && c.Key.Contains(" - " + wanted, StringComparison.OrdinalIgnoreCase));
        if (found is null) GD.PrintErr($"appearance test: no class '{wanted}' for {race} (the catalogue has: {string.Join(", ", catalog.Classes.Select(c => c.Key))}); tactical is used");
        return found?.Key ?? $"{prefix} - tactical";
    }

    /// <summary>A synthetic emblem (a 128 x 128 ring and diagonal bar over transparent) as BC3 bytes: no original data, for the emblem screenshots.</summary>
    static byte[] SyntheticEmblem()
    {
        const int N = EmblemHash.Side;
        var rgba = new byte[N * N * 4];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float dx = x - N / 2f + 0.5f, dy = y - N / 2f + 0.5f, r = MathF.Sqrt(dx * dx + dy * dy);
                bool ring = r is > 44f and < 58f, bar = MathF.Abs(dx - dy) < 9f && r < 50f, hole = r < 14f;
                float t = x / (float)N;
                byte red = (byte)(235 - 60 * t), green = (byte)(180 + 50 * t), blue = (byte)(30 + 200 * t);
                int i = 4 * (y * N + x);
                bool on = ring || bar || hole;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (red, green, blue, on ? (byte)255 : (byte)0);
            }
        return SmFormats.Dds.Bc3.Encode(rgba, N, N);
    }

    AppearanceProfile ProfileLook(CustomizationCatalog catalog, string race, List<string> notes)
    {
        string source = _args.GetValueOrDefault("appearance-test") ?? "default";
        var look = AppearanceProfile.Empty("test", race, "test");
        if (!source.Equals("default", StringComparison.OrdinalIgnoreCase) && !source.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            var result = ProfileStore.ForFile(source).Load(catalog);
            foreach (var r in result.Repairs) notes.Add($"profile: {r.Path}: {r.Message}");
            var wanted = _args.GetValueOrDefault("appearance");
            look = (wanted is null ? result.Profile.AppearancesFor(race).FirstOrDefault() : result.Profile.Appearance(wanted)) ?? look;
        }
        var raceCatalog = catalog.Race(race)!;
        if (_args.GetValueOrDefault("set") is { } setName)
        {
            if (raceCatalog.Set(setName) is not { } set) notes.Add($"--set '{setName}' is not a {race} set");
            else if (!set.Customizable) look = look with { PredefinedSet = set.Name };
            else look = look with { PredefinedSet = null, Pieces = new Dictionary<string, string>(set.Items.Where(kv => CustomizationCatalog.ArmourSlots.Contains(kv.Key))) };
        }
        if (_args.GetValueOrDefault("preset") is { } presetName)
        {
            if (raceCatalog.Presets.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase)) is { } preset) look = ProfileWire.ApplyPreset(look, preset, catalog);
            else notes.Add($"--preset '{presetName}' is not a {race} preset");
        }
        if (_args.GetValueOrDefault("piece") is { } pieceList)
        {
            var pieces = new Dictionary<string, string>(look.Pieces);
            foreach (var item in pieceList.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (item.Split('=') is { Length: 2 } kv) pieces[kv[0]] = kv[1];
            look = look with { Pieces = pieces };
        }
        if (_args.GetValueOrDefault("lens") is { } lens && lens.Split(',') is { Length: 3 } c && byte.TryParse(c[0], out var lr) && byte.TryParse(c[1], out var lg) && byte.TryParse(c[2], out var lb))
            look = look with { Lens = new LensColour(lr, lg, lb) };
        return look;
    }

    public override void _Ready()
    {
        var notes = new List<string>();
        _name = _args.GetValueOrDefault("name") ?? "look";
        _outDir = _args.GetValueOrDefault("out") ?? System.IO.Path.Combine(GameInstall.DataRoot, "cache", "cust-t6");
        _turn = F(_args.GetValueOrDefault("turn"), 0f);
        _quit = _args.ContainsKey("quit");
        _shots = (_args.GetValueOrDefault("shots") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => F(x, 0f)).ToArray();
        var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
        string fullOut = System.IO.Path.GetFullPath(_outDir);
        if (SmFormats.InstallPaths.IsWithin(fullOut, repoRoot) || SmFormats.InstallPaths.IsWithin(fullOut, _gameDir))
        { GD.PrintErr($"appearance test: {_outDir} is inside the repository or the game folder; screenshots refused"); _shots = []; }

        AddEnvironment();
        try
        {
            var total = Stopwatch.StartNew();
            var assets = AppearanceAssets.Shared(_gameDir, notes);
            var watch = Stopwatch.StartNew();
            var catalog = CustomizationCatalog.Load(_gameDir, notes);
            GD.Print($"appearance test: catalogue {watch.ElapsedMilliseconds} ms");
            string race = _args.GetValueOrDefault("race") is { } r ? (r.StartsWith("c", StringComparison.OrdinalIgnoreCase) ? CustomizationCatalog.Chaos : CustomizationCatalog.SpaceMarine)
                : _args.GetValueOrDefault("class") is { } cl && cl.StartsWith("csm", StringComparison.OrdinalIgnoreCase) || _args.GetValueOrDefault("class") is "raptor" or "havoc" ? CustomizationCatalog.Chaos
                : CustomizationCatalog.SpaceMarine;
            string classKey = ClassKey(catalog, race, _args.GetValueOrDefault("class"));
            var cls = catalog.Class(classKey)!;
            race = cls.Race;
            var profileLook = ProfileLook(catalog, race, notes);
            var carried = _args.GetValueOrDefault("carried")?.Split(',', StringSplitOptions.RemoveEmptyEntries);
            watch.Restart();
            var options = _args.ContainsKey("try-build") ? assets.ResolveOptions(carried) : assets.ResolveOptionsExact(carried);
            var look = AppearanceResolver.Resolve(profileLook, classKey, catalog, options);
            GD.Print($"appearance test: resolved '{classKey}' ({race}): {look.Pieces.Count} pieces [{string.Join(", ", look.Pieces.Select(p => p.Slot + "=" + p.Name))}], predefined {look.PredefinedSet ?? "-"}, lens {(look.Lens is { } l ? $"{l.R},{l.G},{l.B}" : "none")}; resolve incl. pad decal check ({(_args.ContainsKey("try-build") ? "non-blocking" : "exact")}) {watch.ElapsedMilliseconds} ms");
            foreach (var n in look.Notes) notes.Add("resolver: " + n);
            if (_args.ContainsKey("emblem-test"))
            {
                var bc3 = SyntheticEmblem();
                var hash = EmblemHash.Of(bc3);
                assets.EmblemSource = h => h == hash ? bc3 : null;
                string pads = _args.GetValueOrDefault("emblem-test") ?? "right";
                if (pads is "right" or "both") look = look with { RightDecal = new ResolvedDecal(DecalKind.Emblem, look.RightDecal.Badge, hash) };
                if (pads is "left" or "both") look = look with { LeftDecal = new ResolvedDecal(DecalKind.Emblem, look.LeftDecal.Badge, hash) };
                GD.Print($"appearance test: synthetic emblem {hash.ToHex()} ({bc3.Length} bytes of BC3) on the {pads} pad");
            }

            if (_args.GetValueOrDefault("perf") is { } perf) { Perf(assets, catalog, race, classKey, (int)F(perf, 8f), options); }

            watch.Restart();
            MarineAssets model;
            if (_args.ContainsKey("try-build"))
            {
                // The in-match path: TryBuild every "frame" (2 ms apart here) until it returns the model; no call may wait for a file.
                int calls = 0; double worst = 0, spent = 0; var each = new List<string>();
                MarineAssets? built = null;
                while (built is null && calls < 5000)
                {
                    var call = Stopwatch.StartNew();
                    built = assets.TryBuild(look);
                    double ms = call.Elapsed.TotalMilliseconds;
                    worst = Math.Max(worst, ms); spent += ms; calls++; each.Add($"{ms:F0}");
                    if (built is null) OS.DelayMsec(2);
                }
                GD.Print($"appearance test: TryBuild took {calls} calls over {watch.ElapsedMilliseconds} ms; the longest call {worst:F1} ms, all calls together {spent:F1} ms; {(built is null ? "NO MODEL" : "model ready")}; per call (ms): {string.Join(" ", each)}");
                model = built ?? assets.Build(look);
            }
            else
            {
                assets.Prefetch(look);
                model = assets.Build(look);
            }
            GD.Print($"appearance test: first build {assets.LastBuildMilliseconds:F1} ms (files read {assets.Pieces.ReadMilliseconds:F0} ms summed over workers, meshes/textures built {assets.Pieces.BuildMilliseconds:F0} ms); {model.Pieces.Count} pieces, {model.TexturedSurfaces}/{model.Surfaces} textured surfaces; materials {assets.Materials.Count} (hits {assets.Materials.Hits})");
            assets.Build(look);
            GD.Print($"appearance test: second build of the same look {assets.LastBuildMilliseconds:F2} ms (materials {assets.Materials.Count}, hits {assets.Materials.Hits})");
            if (_args.ContainsKey("legacy"))
            {
                // The single-set path the networked view uses today (the side's default scheme on one armour set): the reference of the default-look comparison.
                string? chaosSet = race == CustomizationCatalog.Chaos ? _args.GetValueOrDefault("legacy") ?? "basic" : null;
                model = MarineAssets.Load(_gameDir, notes, chaosSet);
                GD.Print($"appearance test: legacy single-set model ({(chaosSet is null ? "Space Marine" : "Chaos " + chaosSet)}): {model.Pieces.Count} pieces");
            }
            _character = new CharacterViewer(model, "SM_NAV_IdleL_S_Relaxed_LE", gameSpaceRoot: true) { Name = "Marine" };
            AddChild(_character);
            _character.Pose("SM_NAV_IdleL_S_Relaxed_LE", 0.4f, 0f);   // held, so every run shows the same pose (pixel comparisons)
            if (_args.ContainsKey("eye-glow")) AddChild(new EyeGlowTest(_gameDir, _character, look.Lens, _args.GetValueOrDefault("eye-glow"), _camera!) { Name = "EyeGlow" });   // M40-T13 test aid
            GD.Print($"appearance test: total {total.ElapsedMilliseconds} ms; GPU memory: textures {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed) / 1048576.0:F1} MB, buffers {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed) / 1048576.0:F1} MB");
        }
        catch (Exception ex) { GD.PrintErr("appearance test: " + ex); _quit = true; _shots = []; }
        foreach (var n in notes.Distinct()) GD.Print("  note: " + n);
        _waitFrames = (int)F(_args.GetValueOrDefault("wait-frames"), 12f);   // M40-T13: --wait-frames n before the first shot (effects that need time)
        if (_shots.Length > 0) _character!.RotationDegrees = new Vector3(0, _shots[0], 0);
    }

    /// <summary>--perf n: builds n looks (every customizable set of the race in turn, with every preset in turn) and prints the build time and the GPU memory.</summary>
    void Perf(AppearanceAssets assets, CustomizationCatalog catalog, string race, string classKey, int count, ResolveOptions options)
    {
        var raceCatalog = catalog.Race(race)!;
        var sets = raceCatalog.CustomizableSets.ToList();
        var presets = raceCatalog.Presets;
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            var set = sets[i % sets.Count];
            var look0 = AppearanceProfile.Empty("p" + i, race, "perf") with { Pieces = new Dictionary<string, string>(set.Items.Where(kv => CustomizationCatalog.ArmourSlots.Contains(kv.Key))) };
            if (presets.Count > 0) look0 = ProfileWire.ApplyPreset(look0, presets[(i * 7) % presets.Count], catalog);
            var resolved = AppearanceResolver.Resolve(look0, classKey, catalog, options);
            long before = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed);
            int materials = assets.Materials.Count, builtPieces = assets.Pieces.Built;
            assets.Prefetch(resolved);
            var model = assets.Build(resolved);
            sum += assets.LastBuildMilliseconds;
            GD.Print($"perf {i + 1}/{count}: set {set.Name}, preset {(look0.PresetApplied ?? "-")}: {assets.LastBuildMilliseconds:F1} ms, +{assets.Pieces.Built - builtPieces} pieces, +{assets.Materials.Count - materials} materials, " +
                     $"texture memory {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed) / 1048576.0:F1} MB ({((long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed) - before) / 1048576.0:+0.0;-0.0} MB), {model.Pieces.Count} pieces");
        }
        GD.Print($"perf: {count} looks, mean build {sum / count:F1} ms; pieces cached {assets.Pieces.Built}, materials {assets.Materials.Count} (hits {assets.Materials.Hits})");
    }

    void AddEnvironment()
    {
        string closeup = _args.GetValueOrDefault("closeup") ?? "";
        // The marine faces -Z in Godot, so the front view is from -Z; its right side is +X. The turntable turns the marine about its own axis, so the pad
        // closeups (which put the camera beside a pad) use shots of 0 only.
        var front = new Vector3(0, 0.12f, -1f);
        var (target, distance, fov, direction) = closeup switch
        {
            "head" => (new Vector3(0, 1.95f, 0), 0.8f, 30f, front),
            "head-right" => (new Vector3(0, 1.95f, 0), 0.8f, 30f, new Vector3(1f, 0.1f, -0.5f)),
            "shoulder" => (new Vector3(0, 1.75f, 0), 1.6f, 30f, front),
            "shoulder-r" => (new Vector3(0.5f, 1.8f, 0), 1.1f, 30f, new Vector3(1f, 0.1f, -0.35f)),
            "shoulder-l" => (new Vector3(-0.5f, 1.8f, 0), 1.1f, 30f, new Vector3(-1f, 0.1f, -0.35f)),
            "torso" => (new Vector3(0, 1.2f, 0), 2.0f, 35f, front),
            _ => (new Vector3(0, 0.95f, 0), 4.2f, 35f, front),
        };
        // --cam-target x,y,z and --cam-dir x,y,z (Godot space, from the target towards the camera) override the framing.
        static Vector3? Triple(string? text) => text?.Split(',') is { Length: 3 } p && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z) ? new Vector3(x, y, z) : null;
        target = Triple(_args.GetValueOrDefault("cam-target")) ?? target;
        direction = Triple(_args.GetValueOrDefault("cam-dir")) ?? direction;
        distance = F(_args.GetValueOrDefault("cam-dist"), distance);
        _camera = new Camera3D { Fov = fov, Near = 0.05f, Far = 50f, Current = true };
        AddChild(_camera);
        _camera.Position = target + direction.Normalized() * distance;
        _camera.LookAt(target, Vector3.Up);
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
        if (_character is null) { if (_quit) GetTree().Quit(1); return; }
        if (_turn != 0f) _character.RotationDegrees += new Vector3(0, _turn * (float)delta, 0);
        if (_waitFrames > 0) { _waitFrames--; return; }
        if (_shots.Length == 0) { if (_quit) GetTree().Quit(0); return; }
        if (_nextShot == 0)
            GD.Print($"appearance test: GPU memory after the first frames: textures {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed) / 1048576.0:F1} MB, " +
                     $"buffers {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed) / 1048576.0:F1} MB, video {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed) / 1048576.0:F1} MB");
        float angle = _shots[_nextShot];
        string path = System.IO.Path.Combine(_outDir, $"{_name}_{angle:0}.png");
        System.IO.Directory.CreateDirectory(_outDir);
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print(err == Error.Ok ? $"screenshot -> {path}" : $"screenshot failed: {err}");
        if (++_nextShot >= _shots.Length) { _shots = []; GetTree().Quit(err == Error.Ok ? 0 : 1); return; }
        _character.RotationDegrees = new Vector3(0, _shots[_nextShot], 0);
        _waitFrames = 3;
    }
}
