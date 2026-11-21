using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using SmClient.Extras.Weather;
using SmNet.Extras.Weather;
using SmSim.Extras.Weather;

namespace SmClient;

/// <summary>
/// Weather W2 stills aid (windowed, on the private hidden desktop: <c>godot.cmd --windowed res://Extras/Weather/WeatherStills.tscn -- --weather-stills &lt;dir&gt;</c>): a **placeholder demonstration**, not a Warfront battle. A plain test
/// stage (a street of boxes, stand-in marines, a sun, fog and a sky) lit by <see cref="LightingGradeView"/> with the real <see cref="WeatherGrade"/> and drawn with the real <see cref="WeatherRig"/> at chosen moments of each
/// preset: a player's view and a third-person view of each. Saves one PNG per view and prints one STILL line per file. Test aid only (client only, nothing shipped depends on it).
/// </summary>
public sealed partial class WeatherStills : Node
{
    static readonly (string Name, WeatherId Id, double At)[] Moments =
    [
        ("none", WeatherId.None, 0),
        ("thunderstorm-buildup", WeatherId.Thunderstorm, 100),
        ("thunderstorm-peak-glow", WeatherId.Thunderstorm, -1),   // the first strike's flash at the peak
        ("thunderstorm-peak-between", WeatherId.Thunderstorm, -2),   // a moment between strikes
        ("squall-warning", WeatherId.AshSquall, 60 + 45 - 20),
        ("squall-peak", WeatherId.AshSquall, 60 + 45 + 40),
        ("squall-recovery", WeatherId.AshSquall, 60 + 45 + 90 + 55),
        ("embers", WeatherId.EmberWind, 150),
    ];

    public override void _Ready() => _ = RunAsync();

    async Task RunAsync()
    {
        string dir = "";
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--weather-stills") dir = args[i + 1];
        if (dir.Length == 0) { GD.Print("WEATHER STILLS: no output folder (--weather-stills <dir>)"); GetTree().Quit(1); return; }
        Directory.CreateDirectory(dir);
        GetWindow().Size = new Vector2I(960, 540);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial { SkyTopColor = new Color(0.30f, 0.34f, 0.42f), SkyHorizonColor = new Color(0.55f, 0.50f, 0.45f), GroundHorizonColor = new Color(0.30f, 0.27f, 0.24f), GroundBottomColor = new Color(0.12f, 0.11f, 0.10f) } },
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.52f, 0.50f), AmbientLightEnergy = 1.1f,
            FogEnabled = true, FogMode = Godot.Environment.FogModeEnum.Exponential, FogDensity = 0.0036f, FogLightColor = new Color(0.56f, 0.50f, 0.44f),
            TonemapMode = Godot.Environment.ToneMapper.Aces, TonemapExposure = 0.95f, AdjustmentEnabled = true, AdjustmentSaturation = 0.85f, GlowEnabled = true,
        };
        AddChild(new WorldEnvironment { Environment = env });
        var sun = new DirectionalLight3D { LightEnergy = 0.9f, LightColor = new Color(1f, 0.88f, 0.74f), Rotation = new Vector3(-0.9f, 0.6f, 0f), ShadowEnabled = true };
        AddChild(sun);
        BuildStage();
        var cam = new Camera3D { Current = true, Fov = 75f, Far = 600f };
        AddChild(cam);

        var grade = new LightingGradeView();
        var model = new WeatherClientModel();
        WeatherClientLink.Model = model;
        WeatherRig? rig = null;

        double strike = WeatherPlan.StrikesIn(120, 400)[0].Time;
        double between = 120; while (WeatherPlan.CloudGlow(between) > 0f) between += 0.5;
        float fog0 = env.FogDensity, exp0 = env.TonemapExposure, amb0 = env.AmbientLightEnergy, sun0 = sun.LightEnergy;
        foreach (var (name, id, at0) in Moments)
        {
            double at = at0 == -1 ? strike : at0 == -2 ? between : at0;
            rig?.QueueFree();   // a fresh rig for each moment: nothing of the last look is left in the air
            rig = new WeatherRig();
            AddChild(rig);
            var d = new WeatherDirector(id); d.Advance(at);
            model.Receive(WeatherWire.Encode(WeatherWire.Of(d)));
            // two views: a player's (1.7 m, looking down the street) and a third person (behind and above the marine)
            foreach (var (view, pos, look) in new[] { ("player", new Vector3(0f, 1.7f, 18f), new Vector3(0f, 1.6f, -20f)), ("third", new Vector3(2.2f, 2.6f, 24f), new Vector3(0f, 1.2f, 16f)) })
            {
                cam.GlobalPosition = pos; cam.LookAt(look, Vector3.Up);
                // let the particles fill the air (real time) with the clock running at the chosen moment
                var st = model.State;
                for (int f = 0; f < 150; f++)
                {
                    grade.Frame(env, sun);
                    rig!.Update(WeatherClock.At(st.Id, st.Elapsed), model.EffectiveIntensity, cam.GlobalPosition);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }
                grade.Frame(env, sun);
                rig!.Update(WeatherClock.At(st.Id, st.Elapsed), model.EffectiveIntensity, cam.GlobalPosition);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var img = GetViewport().GetTexture().GetImage();
                string file = Path.Combine(dir, $"{name}-{view}.png");
                var err = img.SavePng(file);
                GD.Print($"STILL {name} {view} {st.Id} {st.Phase} t={st.Elapsed:F1}s intensity {st.Intensity:F2} glow {rig!.GlowAlpha:F3} fog x{env.FogDensity / fog0:F2} sun x{sun.LightEnergy / sun0:F2} exposure x{env.TonemapExposure / exp0:F2} ambient x{env.AmbientLightEnergy / amb0:F2} -> {file} ({err})");
            }
        }
        WeatherClientLink.Model = null;
        GD.Print("WEATHER STILLS DONE");
        GetTree().Quit(0);
    }

    void BuildStage()
    {
        var ground = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(400f, 400f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.29f, 0.26f), Roughness = 1f } };
        AddChild(ground);
        var wall = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.38f, 0.34f), Roughness = 0.95f };
        for (int i = 0; i < 12; i++)
        {
            float z = 30f - i * 14f, h = 9f + (i % 3) * 4f;
            AddBox(new Vector3(-11f, h / 2, z), new Vector3(8f, h, 11f), wall);
            AddBox(new Vector3(11f, h / 2, z - 3f), new Vector3(8f, h + 2f, 11f), wall);
        }
        // stand-in marines (capsules): friendly blue, the objective beacon amber
        var blue = new StandardMaterial3D { AlbedoColor = new Color(0.15f, 0.30f, 0.75f), Roughness = 0.6f };
        foreach (var p in new[] { new Vector3(0f, 1f, 16f), new Vector3(-2f, 1f, 5f), new Vector3(2.5f, 1f, -4f) })
            AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.45f, Height = 1.9f }, MaterialOverride = blue, Position = p });
        AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.15f, BottomRadius = 0.15f, Height = 30f }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.7f, 0.1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded }, Position = new Vector3(0f, 15f, -30f) });
    }

    void AddBox(Vector3 at, Vector3 size, Material m) => AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = m, Position = at });
}
