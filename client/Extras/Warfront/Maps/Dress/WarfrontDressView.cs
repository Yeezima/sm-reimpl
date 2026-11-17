using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent.Extras.Warfront.Maps;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Fortress;
using SmSim.Extras.Warfront.Hq;
using SmSim.Extras.Warfront.Maps;

namespace SmClient;

/// <summary>
/// A self-view of the Warfront dressing (slice "dress", part 1; test aid, client only): builds the same drawing the client builds (<see cref="WarfrontFortressDress.Steps"/>, <see cref="WarfrontFarGoal.Apply"/>) in a plain world with no server, runs the
/// steps one a frame with the real-time frame length of each, waits for the kit's models, then saves player-view and third-person stills from fixed poses and the frame time of a still view. Windowed run (the hidden desktop):
/// <c>godot.cmd --windowed --resolution 1600x900 res://Extras/Warfront/Maps/Dress/WarfrontDressView.tscn -- --dress-set fortress|far --dress-shots &lt;dir&gt; [--dress off] [--dress-band 0..2] [--dress-perf]</c>.
/// <c>--dress off</c> draws the before (the W14b graybox, the far ground without the massing). Not part of play.
/// </summary>
public sealed partial class WarfrontDressView : Node
{
    Node3D _game = null!;
    Camera3D _cam = null!;
    DirectionalLight3D _sun = null!;
    Godot.Environment _env = null!;
    Node3D? _standIn;
    string? _dir;
    string _tag = "";

    static string? Arg(string name) { var a = OS.GetCmdlineUserArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    static bool Flag(string name) => Array.IndexOf(OS.GetCmdlineUserArgs(), name) >= 0;
    static Vector3 G(float x, float y, float z) => new(x, z, -y);

    public override void _Ready() => _ = RunAsync();

    async Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }

    void World()
    {
        _game = new Node3D { Name = "GameSpace", Basis = new Basis(new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0)) };
        AddChild(_game);
        _cam = new Camera3D { Fov = 65f, Near = 0.05f, Far = 3000f, Current = true };
        AddChild(_cam);
        _sun = new DirectionalLight3D { ShadowEnabled = true, LightEnergy = 1.4f, LightColor = new Color(1.0f, 0.86f, 0.68f), DirectionalShadowMaxDistance = 120f };
        AddChild(_sun);
        _sun.RotationDegrees = new Vector3(-50f, 35f, 0f);
        AddChild(new WorldEnvironment
        {
            Environment = _env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.24f, 0.2f, 0.17f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.62f, 0.6f, 0.58f), AmbientLightEnergy = 1.1f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic, TonemapExposure = 1.15f,
                FogEnabled = true, FogLightColor = new Color(0.36f, 0.3f, 0.25f), FogDensity = 0.004f, SsaoEnabled = true,
            },
        });
        // a stand-in for the player in the third-person stills (a marine-sized capsule; there is no model in this view)
        _standIn = new Node3D { Name = "StandIn", Visible = false };
        _standIn.AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.45f, Height = 1.9f }, Position = new Vector3(0, 0.95f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.2f, 0.55f), Roughness = 0.6f } });
        AddChild(_standIn);
    }

    /// <summary>One still: a player-view and a third-person picture from the same pose. <paramref name="feet"/> is where the player stands, <paramref name="look"/> what he looks at (game space).</summary>
    async Task Shoot(string name, Vector3 feet, Vector3 look)
    {
        var dirXY = new Vector3(look.X - feet.X, look.Y - feet.Y, 0f).Normalized();
        foreach (bool third in new[] { false, true })
        {
            Vector3 eye = third ? feet - dirXY * 5.5f + new Vector3(0, 0, 2.9f) : feet + new Vector3(0, 0, 1.7f);
            var aim = third ? feet + dirXY * 12f + new Vector3(0, 0, 1.2f + (look.Z - 1.2f) * 0.35f) : look;
            _cam.Position = G(eye.X, eye.Y, eye.Z);
            _cam.LookAt(G(aim.X, aim.Y, aim.Z), Vector3.Up);
            _standIn!.Visible = third;
            _standIn.Position = G(feet.X, feet.Y, feet.Z);
            _standIn.LookAt(G(feet.X + dirXY.X, feet.Y + dirXY.Y, feet.Z), Vector3.Up);
            await Frames(8);
            if (_dir is null) continue;
            var img = GetViewport().GetTexture()?.GetImage();
            if (img is null || img.IsEmpty()) { GD.Print("DRESSVIEW no image for " + name); continue; }
            string path = System.IO.Path.Combine(_dir, $"{_tag}{name}_{(third ? "tp" : "pv")}_{img.GetWidth()}x{img.GetHeight()}.png");
            img.SavePng(path);
            GD.Print("DRESSVIEW SHOT " + path);
        }
    }

    /// <summary>Average and worst real frame time over <paramref name="frames"/> frames at a still pose.</summary>
    async Task Perf(string name, Vector3 feet, Vector3 look, int frames = 180)
    {
        _cam.Position = G(feet.X, feet.Y, feet.Z + 1.7f);
        _cam.LookAt(G(look.X, look.Y, look.Z), Vector3.Up);
        _standIn!.Visible = false;
        var vp = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(vp, true);
        await Frames(10);
        var times = new List<double>(); var gpu = new List<double>(); var cpu = new List<double>();
        long last = (long)Time.GetTicksUsec();
        for (int i = 0; i < frames; i++)
        {
            await Frames(1);
            long now = (long)Time.GetTicksUsec();
            times.Add((now - last) / 1000.0); last = now;
            gpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(vp)); cpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp));
        }
        times.Sort();
        GD.Print($"DRESSVIEW RENDER {_tag}{name}: measured render time, GPU mean {gpu.Average():F2} ms (max {gpu.Max():F2}), CPU mean {cpu.Average():F2} ms (max {cpu.Max():F2}); frames are capped by the display's refresh ({times[times.Count / 2]:F2} ms)");
        GD.Print($"DRESSVIEW PERF {_tag}{name}: {frames} frames, mean {times.Average():F2} ms, median {times[times.Count / 2]:F2} ms, p95 {times[(int)(times.Count * 0.95)]:F2} ms, worst {times[^1]:F2} ms; draw calls {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):F0}, primitives {Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame):F0}, static memory {OS.GetStaticMemoryUsage() / 1048576.0:F0} MiB");
    }

    async Task RunSteps(List<(string Name, Action Run)> steps, string what)
    {
        long start = (long)Time.GetTicksUsec(); double longest = 0; string longestName = "";
        long last = start;
        foreach (var (name, run) in steps)
        {
            await Frames(2);
            long t0 = (long)Time.GetTicksUsec();
            run();
            long t1 = (long)Time.GetTicksUsec();
            await Frames(1);
            long t2 = (long)Time.GetTicksUsec();   // the frame that draws what the step built: its real length
            double frame = (t2 - t1) / 1000.0, stepMs = (t1 - t0) / 1000.0;
            if (frame + stepMs > longest) { longest = frame + stepMs; longestName = name; }
            GD.Print($"DRESSVIEW {what} step \"{name}\": step {stepMs:F0} ms, the frame after it {frame:F0} ms (real time)");
        }
        GD.Print($"DRESSVIEW {what} steps done in {((long)Time.GetTicksUsec() - start) / 1000} ms; the longest main-thread frame a step caused (step plus the frame after it): {longest:F0} ms (\"{longestName}\")");
    }

    async Task WaitKit(AshfallKitView? kit)
    {
        if (kit is null) return;
        long t0 = (long)Time.GetTicksMsec();
        while (AshfallKitView.Report.Length == 0 && (long)Time.GetTicksMsec() - t0 < 90000) await Frames(5);
        GD.Print($"DRESSVIEW kit models: {AshfallKitView.Report} (waited {(long)Time.GetTicksMsec() - t0} ms)");
    }

    async Task RunAsync()
    {
        World();
        _dir = Arg("--dress-shots");
        if (_dir is not null) System.IO.Directory.CreateDirectory(_dir);
        bool off = WarfrontFarMassing.Off;
        _tag = off ? "before_" : "after_";
        GD.Print($"DRESSVIEW window {GetViewport().GetVisibleRect().Size}, set {Arg("--dress-set") ?? "fortress"}, {(off ? "before (--dress off)" : "after")}");
        double mem0 = OS.GetStaticMemoryUsage() / 1048576.0;
        try
        {
            switch (Arg("--dress-set") ?? "fortress") { case "far": await RunFar(); break; case "hq": await RunHq(); break; default: await RunFortress(); break; }
        }
        catch (Exception e) { GD.Print($"DRESSVIEW ERROR {e}"); }
        GD.Print($"DRESSVIEW memory: static {mem0:F0} MiB before the build, {OS.GetStaticMemoryUsage() / 1048576.0:F0} MiB after; texture memory {Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / 1048576.0:F0} MiB, video memory {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0:F0} MiB");
        GD.Print("DRESSVIEW DONE");
        GetTree().Quit();
    }

    async Task RunFortress()
    {
        var build = (WarfrontFortressBuild)WarfrontSlots.Fortress;
        var node = new Node3D { Name = "WarfrontFortress" };
        _game.AddChild(node);
        var view = AshfallView.Build(build.Level);
        node.AddChild(view);
        AshfallView.ApplyLook(_sun, _env);
        if (!WarfrontFarMassing.Off)
        {
            await RunSteps(WarfrontFortressDress.Steps(node, build, view, _sun, _env), "fortress");
            await WaitKit(node.GetNodeOrNull<AshfallKitView>("KitLook"));
            GD.Print("DRESSVIEW " + WarfrontFortressDress.Report);
        }
        await Frames(30);
        var P = (float x, float y, float z) => new Vector3(x, y, z);
        await Shoot("1-landing-north", P(0, -150, 0), P(0, -100, 8));
        await Shoot("2-gate-from-outside", P(0, -122, 0), P(0, -96, 9));
        await Shoot("3-breach", P(-64, -118, 0), P(-64, -96, 5));
        await Shoot("4-wall", P(34, -122, 0), P(34, -100, 9));
        await Shoot("5-street-elite-post", P(0, -86, 0), P(-22, -52, 6));
        await Shoot("6-lane-elite-post", P(-75, 6, 0), P(-75, 40, 6));
        await Shoot("7-plaza-from-gate", P(0, -8, 0), P(0, 50, 3));
        await Shoot("8-plaza-north-spire", P(0, 62, 0), P(0, 215, 95));
        await Shoot("9-parapet", P(-40, -96, WarfrontFortressMap.WallTop), P(0, -40, 6));
        await Shoot("10-plaza-corner", P(-36, 12, 0), P(10, 60, 4));
        if (Flag("--dress-perf"))
        {
            await Perf("perf-plaza", P(0, 6, 0), P(0, 60, 4));
            await Perf("perf-gate", P(0, -122, 0), P(0, -96, 9));
            await Perf("perf-landing", P(0, -150, 0), P(0, -100, 8));
        }
    }

    async Task RunHq()
    {
        var build = WarfrontHqLayout.Build;
        var node = new Node3D { Name = "WarfrontHq" };
        _game.AddChild(node);
        var view = AshfallView.Build(build.Level);
        node.AddChild(view);
        AshfallView.ApplyLook(_sun, _env);
        if (!WarfrontFarMassing.Off)
        {
            await RunSteps(WarfrontHqDress.Steps(node, build, view, _sun, _env), "hq");
            await WaitKit(node.GetNodeOrNull<AshfallKitView>("KitLook"));
            GD.Print("DRESSVIEW " + WarfrontHqDress.Report);
        }
        await Frames(30);
        float Ramp(float y) => (y - WarfrontHqMap.RampSouth) * WarfrontHqMap.RampRise / (WarfrontHqMap.RampNorth - WarfrontHqMap.RampSouth);
        var P = (float x, float y, float z) => new Vector3(x, y, z);
        await Shoot("1-assembly-up-the-ramp", P(0, -175, 0), P(0, -46, 18));
        await Shoot("2-ramp-to-the-gate", P(0, -92, Ramp(-92)), P(0, -46, 17));
        await Shoot("3-ramp-side-wrecks", P(40, -104, 0), P(30, -70, 3));
        await Shoot("4-gate-close", P(0, -62, Ramp(-62)), P(0, -46, 17));
        await Shoot("5-parapet-looking-down", P(-30, -43, WarfrontHqMap.WallTop), P(0, -10, 12));
        await Shoot("6-courtyard", P(0, -30, WarfrontHqMap.PlateauZ), P(0, 38, 16));
        await Shoot("7-courtyard-corner", P(-72, -32, WarfrontHqMap.PlateauZ), P(50, 20, 13));
        await Shoot("8-wall-from-the-west", P(-120, -120, 0), P(-60, -46, 14));
        await Shoot("9-north-spire", P(0, -5, WarfrontHqMap.PlateauZ), P(0, 430, 110));
        await Shoot("10-south-wasteland", P(0, -43, WarfrontHqMap.WallTop), P(0, -700, 60));
        if (Flag("--dress-perf"))
        {
            await Perf("perf-courtyard", P(0, -30, WarfrontHqMap.PlateauZ), P(0, 38, 16));
            await Perf("perf-ramp", P(0, -92, Ramp(-92)), P(0, -46, 17));
            await Perf("perf-assembly", P(0, -175, 0), P(0, -46, 18));
        }
    }

    async Task RunFar()
    {
        int band = int.TryParse(Arg("--dress-band"), out int b) ? Math.Clamp(b, 0, AshfallFront.BandCount - 1) : 0;
        _tag = $"a{band + 1}_{_tag}";
        var held = new Dictionary<int, (AshfallLevel Level, AshfallDressing Dressing)>();
        foreach (int k in new[] { band - 1, band, band + 1 })
            if (k >= 0 && k < AshfallFront.BandCount) { held[k] = WarfrontLayoutCache.Build(WarfrontSlots.Of(TfwLane.A, k + 1), out string note); GD.Print($"DRESSVIEW layout a{k + 1}: {note.Split(';')[0]}"); }
        var (level, dressing) = held[band];
        var node = new Node3D { Name = $"WarfrontBand{band}" };
        _game.AddChild(node);
        var drawn = AshfallMap.OpenFrontEdges(level);
        var gv = AshfallView.Build(drawn);
        node.AddChild(gv);
        AshfallKitView? kit = null;
        if (AshfallKitLibrary.Available) { kit = new AshfallKitView(drawn, dressing, gv); node.AddChild(kit); kit.SetKit(true); }
        var empty = new AshfallDressing([], [], dressing.Version);
        var bands = new (AshfallLevel Level, AshfallDressing Dressing)[AshfallFront.BandCount];
        for (int k = 0; k < bands.Length; k++) bands[k] = (level, empty);
        foreach (var (k, v) in held) bands[k] = v;
        long t0 = (long)Time.GetTicksUsec();
        var scenery = AshfallFrontScenery.Build(band, bands);
        string goal = WarfrontFarGoal.Apply(scenery, band);
        node.AddChild(scenery);
        GD.Print($"DRESSVIEW scenery step {((long)Time.GetTicksUsec() - t0) / 1000} ms: {goal}");
        await Frames(2);
        AshfallView.ApplyLook(_sun, _env);
        var host = new Node3D { Name = "WarfrontStorm" };
        node.AddChild(host);
        if (!AshfallStorm.Off) AshfallStorm.Attach(host, level, dressing, _sun, _env);
        await WaitKit(kit);
        await Frames(30);
        var spawn = level.Spawns.Count > 0 ? level.Spawns[0] : System.Numerics.Vector3.Zero;
        float spire = WarfrontFarGoal.SpireDistance(band);
        var feet = new Vector3(spawn.X, spawn.Y, spawn.Z);
        await Shoot("landing-north", feet, new Vector3(0, spawn.Y + 1000f, 90f));
        await Shoot("landing-north-wide", feet, new Vector3(spawn.X + 400f, spawn.Y + 1400f, 120f));
        // from the avenue's north edge of the band's square: the bare stretch seen from where it matters (the neighbour copy, then the far ground)
        await Shoot("avenue-edge-north", new Vector3(0, 235f, AshfallMap.BlockTop), new Vector3(0, 235f + 1500f, 100f));
        if (Flag("--dress-perf")) await Perf("perf-landing-north", feet, new Vector3(0, spawn.Y + 1000f, 90f));
    }
}
