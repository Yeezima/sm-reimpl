using System;
using System.Threading.Tasks;
using Godot;
using SmClient.Extras.Weather;
using SmSim;
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

    static float AlphaOf(CpuParticles3D? p) => (p?.Mesh as PrimitiveMesh)?.Material switch
    {
        StandardMaterial3D m => m.AlbedoColor.A,
        ShaderMaterial sm => sm.GetShaderParameter("tint").AsColor().A,   // the weather finish: the rain streaks are a shader
        _ => 0f,
    };

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
        Check("low fog peak: a light depth haze only (the ground fog is the rig's), within 3.5 times", env.FogDensity > fog0 * 1.3f && env.FogDensity <= fog0 * 2f + 1e-6f, env.FogDensity.ToString());
        Check("low fog peak: the shade is never darker", env.AmbientLightEnergy >= amb0);

        model.Receive(Section(WeatherId.Rain, 130));
        grade.Frame(env, sun);
        Check("rain peak: the sun is weaker and the colour paler", sun.LightEnergy < sun0 * 0.6f && env.AdjustmentSaturation < sat0, $"{sun.LightEnergy} {env.AdjustmentSaturation}");

        model.Receive(Section(WeatherId.Clear, 200));
        grade.Frame(env, sun);
        Check("clear, entered from rain: every value is the map's own, exactly (no grade)", env.FogDensity == fog0 && sun.LightEnergy == sun0 && env.TonemapExposure == exp0 && env.AmbientLightEnergy == amb0 && env.AdjustmentSaturation == sat0, $"{sun.LightEnergy} {env.FogDensity}");
        model.Clear(); grade.Frame(env, sun);
        model.Receive(Section(WeatherId.Clear, 0));
        grade.Frame(env, sun);
        Check("clear, from a clean start: every value is the map's own, exactly", env.FogDensity == fog0 && sun.LightEnergy == sun0 && env.TonemapExposure == exp0 && env.AmbientLightEnergy == amb0 && env.AdjustmentSaturation == sat0, $"{sun.LightEnergy} {env.FogDensity}");

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

        // W3, Classic PvP: a cycle fitted to a 600 s match. Held before play: the map as it is; mid-match: the rain's peak; the end: eased.
        WeatherClientLink.Model = model;
        var fitted = new WeatherDirector(WeatherId.Rain, 600) { Held = true };
        model.Receive(WeatherWire.Encode(WeatherWire.Of(fitted)));
        grade.Frame(env, sun);
        Check("classic held before play: the chosen weather shows at full at once (the weather finish), its clock still", model.Held && model.State.Intensity > 0.99f && sun.LightEnergy < sun0 * 0.6f, $"{sun.LightEnergy} {env.FogDensity}");
        fitted.Held = false; fitted.Advance(300);
        model.Receive(WeatherWire.Encode(WeatherWire.Of(fitted)));
        grade.Frame(env, sun);
        Check("classic fitted: mid-match is the rain's peak (the sun weaker, the colour paler)", model.State.Phase == WeatherPhase.Peak && sun.LightEnergy < sun0 * 0.6f && env.AdjustmentSaturation < sat0, $"{model.State.Phase} {sun.LightEnergy}");
        fitted.Advance(290);
        model.Receive(WeatherWire.Encode(WeatherWire.Of(fitted)));
        grade.Frame(env, sun);
        Check("classic fitted: near the end it has eased (the sun between the peak and the map's own)", model.State.Phase == WeatherPhase.Recovery && sun.LightEnergy > sun0 * 0.6f && sun.LightEnergy < sun0, $"{model.State.Phase} {sun.LightEnergy}");
        model.Receive(WeatherWire.Encode(WeatherWire.Of(new WeatherDirector(WeatherId.None, 600))));   // a weather capable server with the preset None
        grade.Frame(env, sun);
        Check("classic None: every value is the map's own, exactly", env.FogDensity == fog0 && sun.LightEnergy == sun0 && env.TonemapExposure == exp0 && env.AmbientLightEnergy == amb0 && env.AdjustmentSaturation == sat0, $"{env.FogDensity} {sun.LightEnergy}");
        model.Clear();
        grade.Frame(env, sun);

        // W3: the Host panel's WEATHER row (the extended profile only), its choice and what it hands the launch.
        {
            ContentExtras content = ContentExtras.All;
            var host = new SmClient.FrontEnd.HostPanel("SmServer.exe", "", new System.Collections.Generic.Dictionary<string, string?>(), _ => { }, (_, _) => { }, () => content, null, () => { });
            AddChild(host);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var row = host.WeatherRowState;
            Check("host: the Classic panel has a WEATHER row on the extended profile, NONE by default, nothing to send", row is { Exists: true, Applies: true, Key: "none", Value: "NONE" } && host.WeatherChosen is null, $"{row}");
            host.ChooseWeatherForTest("thunderstorm");
            Check("host: choosing a preset is what the launch gets", host.WeatherChosen == "thunderstorm" && host.WeatherRowState.Value == "THUNDERSTORM", host.WeatherRowState.ToString());
            host.ChooseWeatherForTest("random");
            Check("host: RANDOM is the last choice and goes to the server as it is", host.WeatherChosen == "random" && host.WeatherRowState.Value == "RANDOM");
            host.ChooseWeatherForTest("none");
            Check("host: NONE sends nothing again", host.WeatherChosen is null);
            host.ChooseWeatherForTest("rain");
            content = ContentExtras.None;   // the lobby's profile goes to Faithful: the row is gone and nothing is sent, whatever was picked
            Check("host: Faithful has no weather row and sends nothing", !host.WeatherRowState.Applies && host.WeatherChosen is null);
            content = ContentProfiles.WarfrontProfile;
            Check("host: Warfront has no Classic weather row either", !host.WeatherRowState.Applies && host.WeatherChosen is null);
            host.QueueFree();
        }

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
        Check("rain: the particle count follows the graphics settings", rain is not null && rain.Amount >= 120 && rain.Amount <= (int)(WeatherRig.RainNearCount * 1.2f), rain?.Amount.ToString() ?? "");
        var farRain = rig.GetNodeOrNull<CpuParticles3D>("RainFar"); var splash = rig.GetNodeOrNull<CpuParticles3D>("Splash"); var mist = rig.GetNodeOrNull<CpuParticles3D>("RainMist");
        int total = (rain?.Amount ?? 0) + (farRain?.Amount ?? 0) + (splash?.Amount ?? 0) + (mist?.Amount ?? 0);
        Check("rain: a far layer, ground splashes and low mist beside the near layer, in a sane total", farRain is { Emitting: true } && splash is { Emitting: true } && mist is { Emitting: true } && total <= (int)((WeatherRig.RainNearCount + WeatherRig.RainFarCount + WeatherRig.SplashCount + WeatherRig.MistCount) * 1.2f) + 4, $"total {total}: {rain?.Amount} + {farRain?.Amount} + {splash?.Amount} + {mist?.Amount}");
        Check("rain: the near layer is denser than the far one and the splashes lie on the ground under the camera", rain is not null && farRain is not null && rain.Amount > farRain.Amount && splash is not null && Math.Abs(splash.GlobalPosition.Y - (2f - 1.8f)) < 0.2f, splash?.GlobalPosition.ToString() ?? "");
        rig.Update(WeatherClock.At(WeatherId.None, 0), 0f, Vector3.Zero);
        Check("none: the rain stops at once; the hiss fades out (repair 2: a 0.5 s fade, never a cut)", !rig.Raining && (hiss is { Playing: false } || rig.BedsFading));
        for (int f = 0; f < 90 && hiss is { Playing: true }; f++) { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); rig.Update(WeatherClock.At(WeatherId.None, 0), 0f, Vector3.Zero); }
        Check("none: the hiss has stopped after its fade", hiss is { Playing: false } && !rig.BedsFading);
        // the W2 looks: thunderstorm (rain from the late buildup, wind, the sky glow only at a strike, thunder), ash squall (warning flecks, the crosswind's ash, the warning text), ember wind (sparse embers)
        var storm = new WeatherRig(); anchor.AddChild(storm);
        double strikeAt = WeatherPlan.StrikesIn(120, 400)[0].Time;
        storm.Update(WeatherClock.At(WeatherId.Thunderstorm, strikeAt), 1f, Vector3.Zero);
        Check("thunderstorm peak: rain falls, the wind blows and the sky glows faintly (never above its alpha limit)", storm.Raining && storm.WindBlowing && storm.GlowAlpha > 0f && storm.GlowAlpha <= WeatherPlan.GlowMaxAlpha + 1e-6f, $"{storm.GlowAlpha}");
        double quiet = 120;
        while (WeatherPlan.CloudGlow(quiet) > 0f) quiet += 0.5;
        storm.Update(WeatherClock.At(WeatherId.Thunderstorm, quiet), 1f, Vector3.Zero);
        Check("thunderstorm between strikes: no glow", storm.GlowAlpha == 0f, $"{storm.GlowAlpha}");
        // full flash: the glow pulse is brighter, still capped
        storm.FullFlash = true;
        storm.Update(WeatherClock.At(WeatherId.Thunderstorm, strikeAt), 1f, Vector3.Zero);
        Check("full flash: the sky glow is brighter than the reduced one and within its cap", storm.GlowAlpha > WeatherPlan.GlowMaxAlpha * 2f && storm.GlowAlpha <= WeatherPlan.FullGlowMaxAlpha + 1e-6f, $"{storm.GlowAlpha}");
        storm.FullFlash = false;
        storm.Update(WeatherClock.At(WeatherId.Thunderstorm, strikeAt), 1f, Vector3.Zero);
        Check("reduced flash again: the glow is back to the faint one", storm.GlowAlpha <= WeatherPlan.GlowMaxAlpha + 1e-6f, $"{storm.GlowAlpha}");
        // the lighting grade: reduced never touches the exposure at a strike; full lifts it a little
        var mm = new WeatherClientModel(); WeatherClientLink.Model = mm; mm.Receive(Section(WeatherId.Thunderstorm, strikeAt));
        grade.Frame(env, sun); float expReduced = env.TonemapExposure;
        mm.FullFlash = true; grade.Frame(env, sun); float expFull = env.TonemapExposure;
        Check("lightning in the grade: the full setting lifts the exposure a little, the reduced one does not", expFull > expReduced && expFull - expReduced <= WeatherPlan.FlashExposure * exp0 + 1e-3f, $"{expReduced} {expFull}");
        mm.Clear(); WeatherClientLink.Model = null; grade.Frame(env, sun);
        storm.PlayRumble(1, false); storm.PlayRumble(2, true);
        var players = 0; foreach (var c in storm.GetChildren()) if (c is AudioStreamPlayer p && p.Name.ToString().StartsWith("Thunder")) { players++; if (p.VolumeDb > WeatherRig.NearThunderDb + 0.01f) players += 100; }
        Check("thunder: a far and a near rumble play, never louder than the near limit", players == 2, players.ToString());
        var stormStart = WeatherClock.At(WeatherId.Thunderstorm, 0.2);
        storm.Update(stormStart, stormStart.Intensity, Vector3.Zero);   // the weather finish: the storm is full the moment it starts
        Check("thunderstorm start: full at once (rain, wind, its sky blending in)", stormStart.Intensity > 0.99f && storm.Raining && storm.WindBlowing && storm.SkyLook == WeatherId.Thunderstorm, $"{stormStart.Intensity}");

        var squall = new WeatherRig(); anchor.AddChild(squall);
        var sqStart = WeatherClock.At(WeatherId.AshSquall, 0.2);
        squall.Update(sqStart, sqStart.Intensity, Vector3.Zero);   // the weather finish: no near-empty opening
        var dense = squall.GetNodeOrNull<CpuParticles3D>("AshSquall");
        Check("ash squall start: the dense ash blows at once along the wind, the wind blows, no text yet", dense is { Emitting: true } && dense.Direction.X > 0.3f && squall.WindBlowing && !squall.WarningShown && WeatherPlan.AshShare(sqStart) > 0.99f, dense?.Direction.ToString() ?? "no ash");
        var sqWarn = WeatherClock.At(WeatherId.AshSquall, 60 + 45 - 20);   // 20 s before the buildup ends
        squall.Update(sqWarn, sqWarn.Intensity, Vector3.Zero);
        Check("ash squall late buildup: the ash still falls and the intensifying text shows", squall.AshFalling && squall.WarningShown);
        var sqPeak = WeatherClock.At(WeatherId.AshSquall, 60 + 45 + 40);
        squall.Update(sqPeak, sqPeak.Intensity, Vector3.Zero, 0f);
        var dustNode = squall.GetNodeOrNull<CpuParticles3D>("AshDust");
        Check("ash squall peak (repair 1): wind-driven dust sheets blow along the wind at street level, faded near the camera, with dense pale ash", squall.Dusting && dustNode is not null && dustNode.Direction.X > 0.3f
            && squall.DustAlpha >= WeatherRig.DustAlphaFull * 0.99f && dustNode.Mesh is QuadMesh { Material: StandardMaterial3D { DistanceFadeMode: BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha } }
            && dense is { Emitting: true } && dense.Amount >= (int)(WeatherRig.AshCount * 0.8f * 0.25f), $"{squall.DustAlpha} {dense?.Amount}");
        squall.SetShelter(1f, false); squall.Update(sqPeak, sqPeak.Intensity, Vector3.Zero, 0f);
        Check("ash squall: no dust under a roof", !squall.Dusting);
        squall.SetShelter(0f, false);
        var rec = WeatherClock.At(WeatherId.AshSquall, 60 + 45 + 90 + 59);
        squall.Update(rec, rec.Intensity, Vector3.Zero);
        Check("ash squall recovery: the ash eases but never stops (at least half)", dense is { Emitting: true } && WeatherPlan.AshShare(rec) >= 0.5f, WeatherPlan.AshShare(rec).ToString());

        var embers = new WeatherRig(); anchor.AddChild(embers);
        var ew = WeatherClock.At(WeatherId.EmberWind, 150);
        embers.Update(ew, ew.Intensity, Vector3.Zero);
        var em = embers.GetNodeOrNull<CpuParticles3D>("Embers");
        Check("ember wind: sparse embers rise on the wind (at most the sparse count), nothing else", embers.Embering && em is { Amount: <= WeatherRig.EmberCount * 2 } && !embers.Raining && !embers.AshFalling && embers.GlowAlpha == 0f, em?.Amount.ToString() ?? "");
        embers.Update(WeatherClock.At(WeatherId.None, 0), 0f, Vector3.Zero);
        squall.Update(WeatherClock.At(WeatherId.None, 0), 0f, Vector3.Zero);
        storm.Update(WeatherClock.At(WeatherId.None, 0), 0f, Vector3.Zero);
        Check("none: every W2 effect stops at once (the wind bed fades out over 0.5 s)", !embers.Embering && !squall.AshFalling && !squall.WarningShown && (!squall.WindBlowing || squall.BedsFading) && !storm.Raining && storm.GlowAlpha == 0f);

        var wind = WeatherSynth.Wind();
        var far = WeatherSynth.Thunder(5, false); var near = WeatherSynth.Thunder(5, true);
        Check("synth: wind loops, thunder does not, none is silent or clipping", wind.LoopMode == AudioStreamWav.LoopModeEnum.Forward && far.LoopMode == AudioStreamWav.LoopModeEnum.Disabled && Peak(wind) > 400 && Peak(wind) < 30000 && Peak(far) > 400 && Peak(far) < 32767 && Peak(near) > 400 && Peak(near) < 32767,
            $"wind {Peak(wind)} far {Peak(far)} near {Peak(near)}");

        // repair 1: the --dev-script words survive stray quotes from nested quoting
        Check("dev script: stray quotes are dropped, every word kept", string.Join("|", DevToolsPanel.SplitScript("\"weather:rain, wxslide:20:3 ,weather:clear\"")) == "weather:rain|wxslide:20:3|weather:clear", string.Join("|", DevToolsPanel.SplitScript("\"weather:rain, wxslide:20:3 ,weather:clear\"")));
        Check("skies: drawn in this renderer only when it is not Compatibility", WeatherRig.SkyDrawn == (RenderingServer.GetCurrentRenderingMethod() != "gl_compatibility"));

        // the weather finish: rain coverage at speed (the emitters lead the camera), Low Fog's ground fog, the preset skies and Clear's removal
        {
            var fast = new WeatherRig(); anchor.AddChild(fast);
            fast.ForceLeadVelocity(new Vector3(20f, 0f, 0f));
            fast.Update(WeatherClock.At(WeatherId.Rain, 130), 1f, Vector3.Zero);
            var off = fast.RainEmitterOffset;
            Check("rain at 20 m/s sideways: the near emitter leads the camera by the drop's fall time", Math.Abs(off.X - 20f * WeatherRig.NearLeadSeconds) < 0.01f && Math.Abs(off.Y - WeatherRig.NearHeightM) < 0.01f, off.ToString());
            fast.ForceLeadVelocity(new Vector3(100f, 0f, 0f));
            fast.Update(WeatherClock.At(WeatherId.Rain, 130), 1f, Vector3.Zero);
            Check("rain lead is capped", fast.RainEmitterOffset.X <= WeatherRig.LeadMaxSpeed * WeatherRig.NearLeadSeconds + 0.01f, fast.RainEmitterOffset.ToString());
            fast.ForceLeadVelocity(null);
            float alphaFull = fast.RainAlpha;
            var rainLull = WeatherClock.At(WeatherId.Rain, 45);   // the opening's end: the lull
            fast.Update(rainLull, rainLull.Intensity, Vector3.Zero);
            Check("rain lull: still raining, fainter than full, never near none", fast.Raining && fast.RainAlpha < alphaFull && fast.RainAlpha > alphaFull * 0.5f && rainLull.Intensity >= 0.5f, $"{alphaFull} -> {fast.RainAlpha} at {rainLull.Intensity}");
            var hissP = fast.GetNodeOrNull<AudioStreamPlayer>("RainHiss");
            Check("rain lull: the hiss follows the rain's amplitude (within 7 dB of full)", hissP is { Playing: true } && hissP.VolumeDb > WeatherRig.LoudestDb - 7f, hissP?.VolumeDb.ToString() ?? "");
            fast.Update(WeatherClock.At(WeatherId.Rain, 130), 1f, Vector3.Zero);
            Check("rain: an overcast sky of its own blends in (not all at once)", fast.SkyLook == WeatherId.Rain && fast.SkyCover > 0f && fast.SkyCover < WeatherRig.RainSkyFull, fast.SkyCover.ToString());
            fast.Update(WeatherClock.At(WeatherId.Clear, 10), 1f, Vector3.Zero);
            Check("clear, entered from rain: no rain, no splashes, the hiss fading (0.5 s), the sky fading out", !fast.Raining && (hissP is { Playing: false } || fast.BedsFading) && !fast.Splashing && WeatherRig.SkyTarget(WeatherId.Clear, 1f) == 0f);
            fast.QueueFree();

            var fog = new WeatherRig(); anchor.AddChild(fog);
            var fogStart = WeatherClock.At(WeatherId.LowFog, 0.2);
            fog.Update(fogStart, fogStart.Intensity, new Vector3(0f, 6f, 0f), 2f);
            var cards = fog.GetNodeOrNull<CpuParticles3D>("LowFog");
            Check("low fog: ground cards at once, on the ground under the player, fading near the camera, within the sparse count", fog.Fogging && cards is not null && Math.Abs(fog.FogBase - 2f) < 0.01f && Math.Abs(cards.GlobalPosition.Y - (2f + WeatherRig.FogBandM * 0.5f)) < 0.01f
                && cards.Amount <= (int)(WeatherRig.FogCount * 1.2f) && cards.Mesh is QuadMesh { Material: StandardMaterial3D { DistanceFadeMode: BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha } }, $"{fog.FogBase} {cards?.GlobalPosition}");
            Check("low fog: no sky of its own, no rain", fog.SkyLook == WeatherId.None && !fog.Raining);
            fog.Update(fogStart, fogStart.Intensity, new Vector3(0f, 12f, 0f), 8f);   // up on a balcony: the fog's base climbs slowly
            Check("low fog: on a balcony the fog stays lower for a while", fog.FogBase < 3f, fog.FogBase.ToString());
            fog.SetShelter(1f, false); fog.Update(fogStart, fogStart.Intensity, Vector3.Zero, 0f);
            Check("low fog: none under a roof", !fog.Fogging);
            fog.SetShelter(0f, false);
            fog.Update(WeatherClock.At(WeatherId.Clear, 1), 1f, Vector3.Zero);
            Check("clear, entered from low fog: the ground fog stops at once", !fog.Fogging);
            fog.QueueFree();
            Check("skies: rain overcast, the storm heavier, none for the quiet presets", WeatherRig.SkyTarget(WeatherId.Thunderstorm, 0.5f) > WeatherRig.SkyTarget(WeatherId.Rain, 0.5f) && WeatherRig.SkyTarget(WeatherId.Rain, 1f) > 0f
                && WeatherRig.SkyTarget(WeatherId.LowFog, 1f) == 0f && WeatherRig.SkyTarget(WeatherId.AshSquall, 1f) == 0f && WeatherRig.SkyTarget(WeatherId.EmberWind, 1f) == 0f && WeatherRig.SkyTarget(WeatherId.None, 1f) == 0f);
        }

        // W4: shelter. Under cover the near layers stop and fade, the far layer rains outside a hole, the weather bus is low-passed, the sounds are cut (never louder); back in the open everything returns.
        var sh = new WeatherRig(); anchor.AddChild(sh);
        var shStorm = WeatherClock.At(WeatherId.Thunderstorm, 135);
        sh.Update(shStorm, 1f, Vector3.Zero);
        var shRain = sh.GetNodeOrNull<CpuParticles3D>("Rain"); var shFar = sh.GetNodeOrNull<CpuParticles3D>("RainFar"); var shSplash = sh.GetNodeOrNull<CpuParticles3D>("Splash"); var shMist = sh.GetNodeOrNull<CpuParticles3D>("RainMist");
        var shHiss = sh.GetNodeOrNull<AudioStreamPlayer>("RainHiss"); var shWind = sh.GetNodeOrNull<AudioStreamPlayer>("WindBed");
        float hissOpen = shHiss?.VolumeDb ?? 0f, windOpen = shWind?.VolumeDb ?? 0f;
        var nearAlphaOpen = AlphaOf(shRain);
        Check("shelter, in the open: all four rain layers fall, the bus is not filtering, no hole in the far layer", shRain is { Emitting: true } && shFar is { Emitting: true } && shSplash is { Emitting: true } && shMist is { Emitting: true } && sh.BusCutoffHz >= WeatherShelterMix.OpenHz - 1f && !sh.FarHoleOn, $"{sh.BusCutoffHz} {sh.FarHoleOn}");
        sh.SetShelter(0.5f, false);
        sh.Update(shStorm, 1f, Vector3.Zero);
        var nearAlphaHalf = AlphaOf(shRain);
        Check("shelter halfway: the near rain is fainter and the bus half-way muffled, still falling", shRain is { Emitting: true } && nearAlphaHalf < nearAlphaOpen * 0.7f && sh.BusCutoffHz < WeatherShelterMix.OpenHz * 0.5f && sh.BusCutoffHz > WeatherShelterMix.MuffledHz, $"{nearAlphaOpen} -> {nearAlphaHalf}, {sh.BusCutoffHz} Hz");
        sh.SetShelter(1f, false);
        sh.Update(shStorm, 1f, Vector3.Zero);
        Check("sheltered: the near rain, splashes and mist have stopped", shRain is { Emitting: false } && shSplash is { Emitting: false } && shMist is { Emitting: false } && !sh.Splashing);
        Check("sheltered: the far layer keeps raining outside a hole round the player, fainter", shFar is { Emitting: true, EmissionShape: CpuParticles3D.EmissionShapeEnum.Ring } && sh.FarHoleOn && Math.Abs(shFar.EmissionRingInnerRadius - WeatherShelterMix.FarHoleRadiusM) < 0.01f);
        Check("sheltered: the weather bus is low-passed to the muffled cutoff", Math.Abs(sh.BusCutoffHz - WeatherShelterMix.MuffledHz) < 30f && AudioServer.GetBusIndex(WeatherRig.BusName) >= 0, $"{sh.BusCutoffHz}");
        Check("sheltered: rain and wind are quieter than in the open, never above their caps; the hiss and wind play on the weather bus",
            shHiss is { Playing: true } && shHiss.VolumeDb < hissOpen - 1f && shHiss.VolumeDb <= WeatherRig.LoudestDb && shHiss.Bus == WeatherRig.BusName && shWind is { Playing: true } && shWind.VolumeDb < windOpen - 1f && shWind.VolumeDb <= WeatherRig.WindLoudestDb && shWind.Bus == WeatherRig.BusName, $"{hissOpen}->{shHiss?.VolumeDb} {windOpen}->{shWind?.VolumeDb}");
        sh.PlayRumble(3, false); sh.PlayRumble(4, true);
        bool thunderOk = true; int thunders = 0;
        foreach (var c in sh.GetChildren()) if (c is AudioStreamPlayer tp && tp.Name.ToString().StartsWith("Thunder")) { thunders++; thunderOk &= tp.Bus == WeatherRig.BusName && tp.VolumeDb <= WeatherRig.NearThunderDb && tp.VolumeDb > -30f; }
        Check("sheltered: thunder still plays (duller on the muffled bus, a little quieter, never above its cap)", thunders == 2 && thunderOk, $"{thunders}");
        sh.SetShelter(0f, false);
        sh.Update(shStorm, 1f, Vector3.Zero);
        Check("back in the open: the near layers fall again, the hole closes, the bus is unfiltered, the sounds are back",
            shRain is { Emitting: true } && shSplash is { Emitting: true } && !sh.FarHoleOn && shFar is { EmissionShape: CpuParticles3D.EmissionShapeEnum.Box } && sh.BusCutoffHz >= WeatherShelterMix.OpenHz - 1f && Math.Abs((shHiss?.VolumeDb ?? 0f) - hissOpen) < 0.01f, $"{sh.BusCutoffHz}");
        // ash and embers: none under cover
        var shAsh = new WeatherRig(); anchor.AddChild(shAsh);
        var ashPeak = WeatherClock.At(WeatherId.AshSquall, 60 + 45 + 40);
        shAsh.Update(ashPeak, ashPeak.Intensity, Vector3.Zero);
        var shAshMain = shAsh.GetNodeOrNull<CpuParticles3D>("AshSquall");
        bool ashOpen = shAshMain is { Emitting: true };
        shAsh.SetShelter(1f, true);
        shAsh.Update(ashPeak, ashPeak.Intensity, Vector3.Zero);
        Check("shelter: the ash stops under cover and returns outside; the warning text is a readability cue and stays", ashOpen && shAshMain is { Emitting: false } && !shAsh.AshFalling, $"{ashOpen}");
        var shAshWarn = WeatherClock.At(WeatherId.AshSquall, 60 + 45 - 20);
        shAsh.Update(shAshWarn, shAshWarn.Intensity, Vector3.Zero);
        Check("shelter: the ash warning text still shows under cover", shAsh.WarningShown);
        shAsh.SetShelter(0f, false);
        shAsh.Update(ashPeak, ashPeak.Intensity, Vector3.Zero);
        Check("shelter: the ash is back when the cover ends", shAsh.AshFalling);
        var shEmber = new WeatherRig(); anchor.AddChild(shEmber);
        var ewState = WeatherClock.At(WeatherId.EmberWind, 150);
        shEmber.SetShelter(1f, false);
        shEmber.Update(ewState, ewState.Intensity, Vector3.Zero);
        Check("shelter: no embers under cover", !shEmber.Embering);
        shEmber.SetShelter(0f, false);
        shEmber.Update(ewState, ewState.Intensity, Vector3.Zero);
        Check("shelter: embers back outside", shEmber.Embering);

        // W4: trim. The counts step down on long frames, stay at or above every floor and at or below the preset's own count, and come back.
        var tr = new WeatherRig(); anchor.AddChild(tr);
        tr.Update(shStorm, 1f, Vector3.Zero);
        var trRain = tr.GetNodeOrNull<CpuParticles3D>("Rain"); var trFar = tr.GetNodeOrNull<CpuParticles3D>("RainFar"); var trSplash = tr.GetNodeOrNull<CpuParticles3D>("Splash"); var trMist = tr.GetNodeOrNull<CpuParticles3D>("RainMist");
        int full = tr.ParticleTotal; int changes = 0; tr.TrimChanged += (_, _) => changes++;
        for (int f = 0; f < 600 && tr.Trim.Level < WeatherTrim.MaxLevel; f++) tr.StepTrim(0.05);   // 50 ms frames
        Check("trim: long frames step the counts down to the lowest level, one change per step", tr.Trim.Level == WeatherTrim.MaxLevel && changes == WeatherTrim.MaxLevel && tr.ParticleTotal < full * 0.6f, $"{full} -> {tr.ParticleTotal} in {changes} steps");
        Check("trim: no layer goes below its floor", trRain!.Amount >= WeatherRig.NearFloor && trFar!.Amount >= WeatherRig.FarFloor && trSplash!.Amount >= WeatherRig.SplashFloor && trMist!.Amount >= WeatherRig.MistFloor, $"{trRain.Amount} {trFar.Amount} {trSplash.Amount} {trMist.Amount}");
        Check("trim: the readability cues are unchanged (the glow limit and the ash warning text stand)", WeatherPlan.GlowMaxAlpha <= 0.06f + 1e-6f && (tr.GlowAlpha == 0f || tr.GlowAlpha <= WeatherPlan.GlowMaxAlpha + 1e-6f));
        for (int f = 0; f < 60 * 60 && tr.Trim.Level > 0; f++) tr.StepTrim(1.0 / 60);   // then calm 60 fps frames
        Check("trim: with headroom the counts return to the preset's own", tr.Trim.Level == 0 && tr.ParticleTotal == full && trRain.Amount == (int)Math.Max(WeatherRig.NearFloor, WeatherRig.RainNearCount * WeatherRig.QualityScale()), $"{tr.ParticleTotal} vs {full}");
        int before = changes; for (int f = 0; f < 60 * 30; f++) tr.StepTrim(1.0 / 60);
        Check("trim: healthy frames cause no more changes (no oscillation)", changes == before);
        tr.QueueFree(); sh.QueueFree(); shAsh.QueueFree(); shEmber.QueueFree();

        rig.QueueFree(); storm.QueueFree(); squall.QueueFree(); embers.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Check("the rigs are freed", !IsInstanceValid(rig) && !IsInstanceValid(storm) && !IsInstanceValid(squall) && !IsInstanceValid(embers));

        Check("the weather bus goes with the last rig", AudioServer.GetBusIndex(WeatherRig.BusName) < 0);

        WeatherClientLink.Model = null;
        GD.Print($"WEATHER SELFTEST RESULT {(_failed == 0 ? "PASS" : "FAIL")} ({_passed} passed, {_failed} failed)");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }
}
