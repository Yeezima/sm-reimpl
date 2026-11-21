using System;
using System.Threading.Tasks;
using Godot;
using SmClient.Extras.Weather;
using SmNet.Extras.Weather;
using SmSim.Extras.Weather;

namespace SmClient;

/// <summary>
/// Weather W1 without a server (headless: <c>godot.cmd res://Extras/Weather/WeatherSelfTest.tscn -- --weather-selftest</c>): the weather's look through <see cref="LightingGradeView"/> on a real Godot environment and sun
/// (Rain, Low Fog and Clear change the values, None and a cleared model write every base back exactly) and <see cref="WeatherRig"/> (rain particles and the synthesised hiss start and stop, nothing is left behind).
/// Prints one SELFTEST PASS or FAIL per check and WEATHER SELFTEST RESULT; quits non-zero on a failure.
/// </summary>
public sealed partial class WeatherSelfTest : Node
{
    int _passed, _failed;

    public override void _Ready() => _ = RunAsync();

    void Check(string name, bool ok, string detail = "")
    {
        if (ok) _passed++; else _failed++;
        GD.Print($"SELFTEST {(ok ? "PASS" : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : " - " + detail)}");
    }

    static byte[] Section(WeatherId id, double seconds)
    {
        var d = new WeatherDirector(id); d.Advance(seconds);
        return WeatherWire.Encode(WeatherWire.Of(d));
    }

    async Task RunAsync()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var env = new Godot.Environment { FogEnabled = true, FogMode = Godot.Environment.FogModeEnum.Exponential, FogDensity = 0.0036f, TonemapExposure = 0.9f, AmbientLightEnergy = 2.4f, AdjustmentEnabled = true, AdjustmentSaturation = 0.58f };
        var sun = new DirectionalLight3D { LightEnergy = 0.62f, LightColor = new Color(1f, 0.9f, 0.8f) };
        AddChild(sun);
        float fog0 = env.FogDensity, exp0 = env.TonemapExposure, amb0 = env.AmbientLightEnergy, sat0 = env.AdjustmentSaturation, sun0 = sun.LightEnergy;
        var grade = new LightingGradeView();
        var model = new WeatherClientModel();
        WeatherClientLink.Model = model;

        grade.Frame(env, sun);
        Check("no weather: nothing is touched", env.FogDensity == fog0 && sun.LightEnergy == sun0 && env.TonemapExposure == exp0);

        model.Receive(Section(WeatherId.LowFog, 200));   // the peak
        grade.Frame(env, sun);
        Check("low fog peak: the haze is thicker, within 3.5 times", env.FogDensity > fog0 * 3f && env.FogDensity <= fog0 * 3.5f + 1e-6f, env.FogDensity.ToString());
        Check("low fog peak: the shade is never darker", env.AmbientLightEnergy >= amb0);

        model.Receive(Section(WeatherId.Rain, 130));
        grade.Frame(env, sun);
        Check("rain peak: the sun is weaker and the colour paler", sun.LightEnergy < sun0 * 0.6f && env.AdjustmentSaturation < sat0, $"{sun.LightEnergy} {env.AdjustmentSaturation}");

        model.Receive(Section(WeatherId.Clear, 200));
        grade.Frame(env, sun);
        Check("clear peak: the sun is brighter and the haze thinner", sun.LightEnergy > sun0 * 1.4f && env.FogDensity < fog0 * 0.6f, $"{sun.LightEnergy} {env.FogDensity}");

        model.Receive(Section(WeatherId.Thunderstorm, 200));   // a stub
        grade.Frame(env, sun);
        Check("a stub preset draws the default look", env.FogDensity == fog0 && sun.LightEnergy == sun0 && env.AmbientLightEnergy == amb0);

        model.Receive(Section(WeatherId.Rain, 130));
        grade.Frame(env, sun);
        model.Clear();   // the connection ended or None
        grade.Frame(env, sun);
        Check("teardown: every value is back to the baseline", env.FogDensity == fog0 && sun.LightEnergy == sun0 && env.TonemapExposure == exp0 && env.AmbientLightEnergy == amb0 && env.AdjustmentSaturation == sat0,
            $"{env.FogDensity} {sun.LightEnergy} {env.TonemapExposure} {env.AmbientLightEnergy} {env.AdjustmentSaturation}");

        // a view that leaves the tree lets go of the static link: a later Classic or Musou view keeps no grade and no F6 section
        var viewA = new WeatherClientModel(); var viewB = new WeatherClientModel();
        viewA.Receive(Section(WeatherId.Rain, 130));
        WeatherClientLink.Model = viewA; WeatherClientLink.Send = _ => true;
        Check("link: set while a Warfront view is up", WeatherClientLink.Available);
        WeatherClientLink.Release(viewB);
        Check("link: another view cannot clear it", WeatherClientLink.Available);
        WeatherClientLink.Release(viewA); viewA.Clear();
        grade.Frame(env, sun);
        Check("link: after the view is gone the section is off and the grade is the identity", !WeatherClientLink.Available && WeatherClientLink.Model is null && sun.LightEnergy == sun0 && env.FogDensity == fog0, $"{sun.LightEnergy} {env.FogDensity}");

        var anchor = new Node3D();
        AddChild(anchor);
        var rig = new WeatherRig();
        anchor.AddChild(rig);
        rig.Update(WeatherId.Rain, 1f, new Vector3(3f, 2f, 1f));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var rain = rig.GetNodeOrNull<CpuParticles3D>("Rain");
        var hiss = rig.GetNodeOrNull<AudioStreamPlayer>("RainHiss");
        Check("rain: particles are emitting and follow the camera", rig.Raining && rain is { Emitting: true } && rig.GlobalPosition.IsEqualApprox(new Vector3(3f, 2f, 1f)));
        Check("rain: the hiss plays at most -22 dB", hiss is { Playing: true } && hiss.VolumeDb <= WeatherRig.LoudestDb + 0.01f, hiss?.VolumeDb.ToString() ?? "no player");
        Check("rain: the particle count follows the graphics settings", rain is not null && rain.Amount >= 60 && rain.Amount <= (int)(WeatherRig.RainCount * 1.2f), rain?.Amount.ToString() ?? "");
        rig.Update(WeatherId.None, 0f, Vector3.Zero);
        Check("none: the rain and the hiss stop at once", !rig.Raining && hiss is { Playing: false });
        rig.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Check("the rig is freed", !IsInstanceValid(rig));

        WeatherClientLink.Model = null;
        GD.Print($"WEATHER SELFTEST RESULT {(_failed == 0 ? "PASS" : "FAIL")} ({_passed} passed, {_failed} failed)");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }
}
