using System;
using System.Threading.Tasks;
using Godot;
using SmClient.Extras.Weather;
using SmNet.Extras.Weather;
using SmSim.Extras.Weather;

namespace SmClient;

/// <summary>
/// Weather W1 and W2 without a server (headless: <c>godot.cmd res://Extras/Weather/WeatherSelfTest.tscn -- --weather-selftest</c>): the weather's look through <see cref="LightingGradeView"/> on a real Godot environment and sun
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

    static int Peak(AudioStreamWav w)
    {
        int peak = 0;
        var d = w.Data;
        for (int i = 0; i + 1 < d.Length; i += 2) peak = Math.Max(peak, Math.Abs((short)(d[i] | d[i + 1] << 8)));
        return peak;
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

        model.Receive(Section(WeatherId.Thunderstorm, 200));   // the peak (45 + 75 = 120 s)
        grade.Frame(env, sun);
        Check("thunderstorm peak: darker sun, thicker haze (within 3.5 times), the shade never darker, exposure never below its floor",
            sun.LightEnergy < sun0 * 0.55f && env.FogDensity > fog0 * 2f && env.FogDensity <= fog0 * 3.5f + 1e-6f && env.AmbientLightEnergy >= amb0 && env.TonemapExposure >= exp0 * 0.94f - 1e-4f, $"{sun.LightEnergy} {env.FogDensity} {env.TonemapExposure}");

        model.Receive(Section(WeatherId.AshSquall, 160));   // the peak (60 + 45 = 105 s)
        grade.Frame(env, sun);
        Check("ash squall peak: a dim ochre sun and a thick warm haze", sun.LightEnergy < sun0 * 0.7f && env.FogDensity > fog0 * 2f && env.FogDensity <= fog0 * 3.5f + 1e-6f, $"{sun.LightEnergy} {env.FogDensity}");

        model.Receive(Section(WeatherId.EmberWind, 200));
        grade.Frame(env, sun);
        Check("ember wind: a faint warm change only", env.FogDensity < fog0 * 1.5f && sun.LightEnergy > sun0 * 0.8f, $"{sun.LightEnergy} {env.FogDensity}");

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
        rig.Update(WeatherClock.At(WeatherId.Rain, 130), 1f, new Vector3(3f, 2f, 1f));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var rain = rig.GetNodeOrNull<CpuParticles3D>("Rain");
        var hiss = rig.GetNodeOrNull<AudioStreamPlayer>("RainHiss");
        Check("rain: particles are emitting and follow the camera", rig.Raining && rain is { Emitting: true } && rig.GlobalPosition.IsEqualApprox(new Vector3(3f, 2f, 1f)));
        Check("rain: the hiss plays at most -22 dB", hiss is { Playing: true } && hiss.VolumeDb <= WeatherRig.LoudestDb + 0.01f, hiss?.VolumeDb.ToString() ?? "no player");
        Check("rain: the particle count follows the graphics settings", rain is not null && rain.Amount >= 60 && rain.Amount <= (int)(WeatherRig.RainCount * 1.2f), rain?.Amount.ToString() ?? "");
        rig.Update(WeatherClock.At(WeatherId.None, 0), 0f, Vector3.Zero);
        Check("none: the rain and the hiss stop at once", !rig.Raining && hiss is { Playing: false });
        // the W2 looks: thunderstorm (rain from the late buildup, wind, the sky glow only at a strike, thunder), ash squall (warning flecks, the crosswind's ash, the warning text), ember wind (sparse embers)
        var storm = new WeatherRig(); anchor.AddChild(storm);
        double strikeAt = WeatherPlan.StrikesIn(120, 400)[0].Time;
        storm.Update(WeatherClock.At(WeatherId.Thunderstorm, strikeAt), 1f, Vector3.Zero);
        Check("thunderstorm peak: rain falls, the wind blows and the sky glows faintly (never above its alpha limit)", storm.Raining && storm.WindBlowing && storm.GlowAlpha > 0f && storm.GlowAlpha <= WeatherPlan.GlowMaxAlpha + 1e-6f, $"{storm.GlowAlpha}");
        double quiet = 120;
        while (WeatherPlan.CloudGlow(quiet) > 0f) quiet += 0.5;
        storm.Update(WeatherClock.At(WeatherId.Thunderstorm, quiet), 1f, Vector3.Zero);
        Check("thunderstorm between strikes: no glow", storm.GlowAlpha == 0f, $"{storm.GlowAlpha}");
        storm.PlayRumble(1, false); storm.PlayRumble(2, true);
        var players = 0; foreach (var c in storm.GetChildren()) if (c is AudioStreamPlayer p && p.Name.ToString().StartsWith("Thunder")) { players++; if (p.VolumeDb > WeatherRig.NearThunderDb + 0.01f) players += 100; }
        Check("thunder: a far and a near rumble play, never louder than the near limit", players == 2, players.ToString());
        storm.Update(WeatherClock.At(WeatherId.Thunderstorm, 10), 0.1f, Vector3.Zero);   // the opening: nothing but a little wind
        Check("thunderstorm opening: no rain", !storm.Raining);

        var squall = new WeatherRig(); anchor.AddChild(squall);
        squall.Update(WeatherClock.At(WeatherId.AshSquall, 60 + 45 - 20), WeatherClock.At(WeatherId.AshSquall, 60 + 45 - 20).Intensity, Vector3.Zero);   // 20 s before the buildup ends
        var warnAsh = squall.GetNodeOrNull<CpuParticles3D>("AshWarning");
        Check("ash squall warning: the first flecks drift, the wind rises, the text shows, the dense ash is not there yet", squall.AshFalling && warnAsh is { Emitting: true } && squall.WarningShown && squall.GetNodeOrNull<CpuParticles3D>("AshSquall") is null);
        var peak = WeatherClock.At(WeatherId.AshSquall, 60 + 45 + 20);
        squall.Update(peak, peak.Intensity, Vector3.Zero);
        var dense = squall.GetNodeOrNull<CpuParticles3D>("AshSquall");
        Check("ash squall peak: the dense ash blows across (leaning on the wind), the warning text is gone", dense is { Emitting: true } && dense.Direction.X > 0.3f && !squall.WarningShown && squall.WindBlowing, dense?.Direction.ToString() ?? "no ash");
        var rec = WeatherClock.At(WeatherId.AshSquall, 60 + 45 + 90 + 59);
        squall.Update(rec, rec.Intensity, Vector3.Zero);
        Check("ash squall recovery: the dense ash stops", dense is { Emitting: false });

        var embers = new WeatherRig(); anchor.AddChild(embers);
        var ew = WeatherClock.At(WeatherId.EmberWind, 150);
        embers.Update(ew, ew.Intensity, Vector3.Zero);
        var em = embers.GetNodeOrNull<CpuParticles3D>("Embers");
        Check("ember wind: sparse embers rise on the wind (at most the sparse count), nothing else", embers.Embering && em is { Amount: <= WeatherRig.EmberCount * 2 } && !embers.Raining && !embers.AshFalling && embers.GlowAlpha == 0f, em?.Amount.ToString() ?? "");
        embers.Update(WeatherClock.At(WeatherId.None, 0), 0f, Vector3.Zero);
        squall.Update(WeatherClock.At(WeatherId.None, 0), 0f, Vector3.Zero);
        storm.Update(WeatherClock.At(WeatherId.None, 0), 0f, Vector3.Zero);
        Check("none: every W2 effect stops at once", !embers.Embering && !squall.AshFalling && !squall.WarningShown && !squall.WindBlowing && !storm.Raining && storm.GlowAlpha == 0f);

        var wind = WeatherSynth.Wind();
        var far = WeatherSynth.Thunder(5, false); var near = WeatherSynth.Thunder(5, true);
        Check("synth: wind loops, thunder does not, none is silent or clipping", wind.LoopMode == AudioStreamWav.LoopModeEnum.Forward && far.LoopMode == AudioStreamWav.LoopModeEnum.Disabled && Peak(wind) > 400 && Peak(wind) < 30000 && Peak(far) > 400 && Peak(far) < 32767 && Peak(near) > 400 && Peak(near) < 32767,
            $"wind {Peak(wind)} far {Peak(far)} near {Peak(near)}");

        rig.QueueFree(); storm.QueueFree(); squall.QueueFree(); embers.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Check("the rigs are freed", !IsInstanceValid(rig) && !IsInstanceValid(storm) && !IsInstanceValid(squall) && !IsInstanceValid(embers));

        WeatherClientLink.Model = null;
        GD.Print($"WEATHER SELFTEST RESULT {(_failed == 0 ? "PASS" : "FAIL")} ({_passed} passed, {_failed} failed)");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }
}
