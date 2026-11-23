using SmSim.Extras.Weather;

namespace SmNet.Tests.Extras.Weather;

/// <summary>Weather W2: the Thunderstorm, Ash Squall and Ember Wind timelines (pure functions of the preset's clock), the reduced-flash limits, and the coherent wind.</summary>
public class WeatherPlanTests
{
    static double Start(WeatherId id, WeatherPhase phase)
    {
        var d = WeatherCatalog.Of(id);
        double t = 0;
        for (var p = WeatherPhase.Opening; p < phase; p++) t += d.Duration(p);
        return t;
    }

    [Fact]
    public void Thunderstorm_is_full_at_once_then_rains_winds_and_thunders_between_its_lull_and_its_peak()
    {
        // the weather finish (2026-11-23): no dry, calm opening any more; the storm's rain, wind and strikes follow its active level (0.5 to 1)
        var id = WeatherId.Thunderstorm;
        double opening = Start(id, WeatherPhase.Buildup), peak = Start(id, WeatherPhase.Peak), recovery = Start(id, WeatherPhase.Recovery), end = recovery + WeatherCatalog.Of(id).Duration(WeatherPhase.Recovery);
        Assert.Equal(1f, WeatherPlan.RainShare(id, WeatherClock.At(id, 0).Intensity));
        Assert.True(WeatherPlan.Wind(id, WeatherClock.At(id, 0).Intensity, 0) > 0.8f);
        Assert.NotEmpty(WeatherPlan.StrikesIn(0, opening + 200));
        // the buildup: the wind rises and never falls
        float last = 0f;
        for (double t = opening; t <= peak; t += 1)
        {
            float w = WeatherPlan.Wind(id, WeatherClock.At(id, t).Intensity, 0);
            Assert.True(w >= last - 1e-6f, $"the wind fell at {t}");
            last = w;
        }
        foreach (var s in WeatherPlan.StrikesIn(0, end + 200)) Assert.True(WeatherClock.At(id, s.Time).Intensity >= WeatherPlan.StrikeFromIntensity);
        // the peak: full rain, the strikes come
        Assert.Equal(1f, WeatherPlan.RainShare(id, 1f));
        Assert.NotEmpty(WeatherPlan.StrikesIn(peak, recovery));
        // the lull (the recovery's end): lighter, never dry or still
        float lullRain = WeatherPlan.RainShare(id, WeatherClock.At(id, end - 0.01).Intensity);
        Assert.InRange(lullRain, 0.5f, 0.7f);
        Assert.True(WeatherPlan.Wind(id, WeatherClock.At(id, end - 0.01).Intensity, 0) > 0.4f);
        // nobody else rains
        foreach (var other in new[] { WeatherId.AshSquall, WeatherId.EmberWind, WeatherId.Clear, WeatherId.LowFog, WeatherId.None }) Assert.Equal(0f, WeatherPlan.RainShare(other, 1f));
    }

    [Fact]
    public void Follow_db_is_the_amplitude_of_the_share_and_never_below_silence()
    {
        Assert.Equal(-22f, WeatherPlan.FollowDb(1f, -22f, -60f), 3);
        Assert.Equal(-22f - 6f, WeatherPlan.FollowDb(0.5f, -22f, -60f), 3);   // repair 2: -6 dB at the active floor
        foreach (var id in new[] { WeatherId.Rain, WeatherId.Thunderstorm, WeatherId.AshSquall })
        {
            float lull = WeatherClock.Lull(WeatherCatalog.Of(id));
            Assert.InRange(-22f - WeatherPlan.FollowDb(lull, -22f, -60f), 4f, 6f);   // each lull 4 to 6 dB down
        }
        Assert.Equal(-60f, WeatherPlan.FollowDb(0f, -22f, -60f));
        Assert.Equal(-60f, WeatherPlan.FollowDb(0.0001f, -22f, -60f));
        Assert.Equal(-60f, WeatherPlan.FollowDb(float.NaN, -22f, -60f));
        Assert.Equal(-22f, WeatherPlan.FollowDb(5f, -22f, -60f), 3);
        float prev = -60f;
        for (float v = 0.01f; v <= 1f; v += 0.01f) { float db = WeatherPlan.FollowDb(v, -22f, -60f); Assert.True(db >= prev - 1e-4f); prev = db; }
    }

    [Fact]
    public void The_cloud_glow_is_reduced_flash_small_smooth_rare_and_in_the_sky_only()
    {
        // the glow's own limits (spec "Weather (W2)": reduced flash is the only mode)
        Assert.True(WeatherPlan.GlowMaxAlpha <= 0.06f);
        Assert.True(WeatherPlan.GlowRise >= 0.3 && WeatherPlan.GlowFall >= 1.0);
        Assert.True(WeatherPlan.MinStrikeGap >= 6.5);
        double step = 1.0 / 60;
        float prev = 0f, maxStep = 0f, maxGlow = 0f;
        for (double t = 0; t < 1200; t += step)
        {
            float g = WeatherPlan.CloudGlow(t);
            maxGlow = Math.Max(maxGlow, g);
            maxStep = Math.Max(maxStep, Math.Abs(g - prev));
            prev = g;
        }
        Assert.InRange(maxGlow, 0.99f, 1f);   // it does reach its fullest, and never goes above
        Assert.True(maxStep * WeatherPlan.GlowMaxAlpha < 0.006f, $"the glow moves {maxStep * WeatherPlan.GlowMaxAlpha} alpha in one 60 Hz frame");   // under 0.6 % of the sky's brightness a frame
        // the strikes: never two within the gap, never more than one a second (far below the 3 flashes a second photosensitivity limit), at most two in any ten seconds
        var strikes = WeatherPlan.StrikesIn(0, 2400);
        Assert.True(strikes.Count > 20);
        for (int i = 1; i < strikes.Count; i++) Assert.True(strikes[i].Time - strikes[i - 1].Time >= WeatherPlan.MinStrikeGap - 1e-9, $"strikes {i - 1} and {i} are {strikes[i].Time - strikes[i - 1].Time} s apart");
        for (int i = 0; i + 2 < strikes.Count; i++) Assert.True(strikes[i + 2].Time - strikes[i].Time > 10, "three strikes within ten seconds");
    }

    [Fact]
    public void The_glow_is_not_a_screen_effect_so_the_grade_never_depends_on_a_strike()
    {
        // WeatherGrade.For takes the preset and its intensity only (client/Extras/Weather/WeatherGrade.cs); a strike has no input there. The plan's side: the strikes and the glow are functions of the clock alone.
        Assert.Equal(WeatherPlan.StrikesIn(0, 900), WeatherPlan.StrikesIn(0, 900));
        Assert.Equal(WeatherPlan.CloudGlow(321.7), WeatherPlan.CloudGlow(321.7));
    }

    [Fact]
    public void Every_strike_is_seen_and_heard_exactly_once_whatever_the_frame_rate_and_a_late_joiner_sees_the_same_storm()
    {
        var all = WeatherPlan.StrikesIn(0, 600);
        int heard = 0, seen = 0;
        double prev = 0;
        for (double t = 0.037; t <= 606; t += 0.037) { heard += WeatherPlan.ThunderIn(prev, t).Count; seen += WeatherPlan.StrikesIn(prev, t).Count; prev = t; }
        Assert.Equal(all.Count, seen);
        Assert.Equal(all.Count(s => s.SoundTime <= prev), heard);
        // a client that joined at 300 s derives the same strikes after it
        Assert.Equal(all.Where(s => s.Time > 300), WeatherPlan.StrikesIn(300, 600));
        // thunder follows its flash: a far one 1.5 to 6.5 s later, a near one 0.6 to 1.2 s later
        Assert.All(all, s => Assert.InRange(s.SoundDelay, s.Near ? WeatherPlan.NearDelayMin : WeatherPlan.FarDelayMin, s.Near ? WeatherPlan.NearDelayMax : WeatherPlan.FarDelayMax));
        Assert.Contains(all, s => s.Near);
        Assert.Contains(all, s => !s.Near);
    }

    [Fact]
    public void Ash_squall_blows_at_once_eases_and_returns_and_cues_its_intensifying_in_the_buildup()
    {
        var id = WeatherId.AshSquall;
        double build = Start(id, WeatherPhase.Buildup), peak = Start(id, WeatherPhase.Peak), rec = Start(id, WeatherPhase.Recovery), end = rec + WeatherCatalog.Of(id).Duration(WeatherPhase.Recovery);
        WeatherState At(double t) => WeatherClock.At(id, t);
        // the intensifying cue: the last 30 s of the buildup only
        Assert.False(WeatherPlan.Warning(At(build + 1)));
        Assert.False(WeatherPlan.Warning(At(peak - WeatherPlan.WarningSeconds - 1)));
        Assert.True(WeatherPlan.Warning(At(peak - WeatherPlan.WarningSeconds + 1)));
        Assert.True(WeatherPlan.Warning(At(peak - 0.5)));
        Assert.False(WeatherPlan.Warning(At(peak + 1)));
        Assert.False(WeatherPlan.Warning(WeatherClock.At(WeatherId.Rain, peak - 1)));
        // the ash (the weather finish): all of it at once, then never below half, full at the peak, no step anywhere
        Assert.Equal(1f, WeatherPlan.AshShare(At(0)));
        Assert.Equal(1f, WeatherPlan.AshShare(At(peak + 10)));
        float last = WeatherPlan.AshShare(At(0)), maxStep = 0f;
        for (double t = 0; t < end + 100; t += 1.0 / 30)
        {
            float a = WeatherPlan.AshShare(At(t));
            Assert.InRange(a, 0.5f, 1f);
            maxStep = Math.Max(maxStep, Math.Abs(a - last)); last = a;
        }
        Assert.True(maxStep < 0.01f, $"one frame moved the ash by {maxStep}");
        // the crosswind follows the ash: strongest at the peak, lighter (never still) at the lull
        float windLull = WeatherPlan.Wind(id, At(end - 0.01).Intensity, 0), windPeak = WeatherPlan.Wind(id, 1f, 0);
        Assert.True(windPeak > 0.8f && windLull > 0.4f && windLull < windPeak, $"{windLull} {windPeak}");
        // others have no ash
        foreach (var other in new[] { WeatherId.Thunderstorm, WeatherId.Rain, WeatherId.EmberWind }) Assert.Equal(0f, WeatherPlan.AshShare(WeatherClock.At(other, peak + 10)));
    }

    [Fact]
    public void Full_flash_is_one_brighter_pulse_per_strike_capped_fast_and_never_a_strobe()
    {
        Assert.True(WeatherPlan.FlashExposure <= 0.10f && WeatherPlan.FlashSun <= 1.0f && WeatherPlan.FlashAmbient <= 0.25f && WeatherPlan.FullGlowMaxAlpha <= 0.35f);
        double step = 1.0 / 60;
        float prev = 0f, maxStep = 0f, maxFlash = 0f;
        int pulses = 0; bool inPulse = false;
        for (double t = 0; t < 2400; t += step)
        {
            float f = WeatherPlan.FullFlash(t);
            maxFlash = Math.Max(maxFlash, f); maxStep = Math.Max(maxStep, Math.Abs(f - prev));
            if (f > 0.05f && !inPulse) { pulses++; inPulse = true; } else if (f <= 0.05f) inPulse = false;
            prev = f;
        }
        Assert.InRange(maxFlash, 0.99f, 1f);
        Assert.True(maxStep <= 0.35f, $"one 60 Hz frame moved the flash by {maxStep}");   // a quick rise but no jump
        // one pulse per strike (no second flicker), the strikes' own spacing (at least 6.5 s), so never three in ten seconds
        Assert.Equal(WeatherPlan.StrikesIn(0, 2400).Count, pulses);
        // the pulse is over before the next strike can come: its whole length is under half the gap
        Assert.True(WeatherPlan.FlashRise + WeatherPlan.FlashFall < WeatherPlan.MinStrikeGap / 2);
        Assert.Equal(0f, WeatherPlan.FullFlash(double.NaN));
    }

    [Fact]
    public void The_reduced_setting_is_the_default_and_full_flash_only_lifts_a_thunderstorm_within_the_readability_limits()
    {
        var m = new SmClient.Extras.Weather.WeatherClientModel();
        double strike = WeatherPlan.StrikesIn(120, 400)[0].Time;
        var d = new WeatherDirector(WeatherId.Thunderstorm); d.Advance(strike);
        m.Receive(SmNet.Extras.Weather.WeatherWire.Encode(SmNet.Extras.Weather.WeatherWire.Of(d)));
        Assert.False(m.FullFlash);
        var reduced = m.Grade;
        m.FullFlash = true;
        var full = m.Grade;
        Assert.True(full.Exposure > reduced.Exposure && full.SunEnergy > reduced.SunEnergy && full.Ambient > reduced.Ambient);
        Assert.True(full.Exposure - reduced.Exposure <= WeatherPlan.FlashExposure + 1e-5f);
        var combined = SmClient.Extras.Weather.WeatherGrade.Combine(SmClient.Customizer.Model.LightingGrade.Identity, full);
        Assert.InRange(combined.Exposure, SmClient.Extras.Weather.WeatherGrade.MinExposure, SmClient.Extras.Weather.WeatherGrade.MaxExposure);
        // between strikes, and for any other preset, the setting changes nothing
        double quiet = 120; while (WeatherPlan.FullFlash(quiet) > 0f) quiet += 0.5;
        var d2 = new WeatherDirector(WeatherId.Thunderstorm); d2.Advance(quiet);
        m.Receive(SmNet.Extras.Weather.WeatherWire.Encode(SmNet.Extras.Weather.WeatherWire.Of(d2)));
        m.FullFlash = false; var a = m.Grade; m.FullFlash = true;
        Assert.Equal(a, m.Grade);
        var d3 = new WeatherDirector(WeatherId.Rain); d3.Advance(130);
        m.Receive(SmNet.Extras.Weather.WeatherWire.Encode(SmNet.Extras.Weather.WeatherWire.Of(d3)));
        m.FullFlash = false; var r = m.Grade; m.FullFlash = true;
        Assert.Equal(r, m.Grade);
    }

    [Fact]
    public void The_thunder_tracker_plays_each_strike_once_in_order_across_the_two_second_clock_snaps()
    {
        var all = WeatherPlan.ThunderIn(200, 600);
        var tracker = new WeatherThunderTracker();
        tracker.Step(200);
        var heard = new List<WeatherStrike>();
        double now = 200; var rng = new Random(5);
        while (now < 600)
        {
            now += 1.0 / 60;
            double shown = now;
            // every two seconds the client's clock snaps to the server's by a few tens of milliseconds, either way
            if ((int)(now * 60) % 120 == 0) shown = now + (rng.NextDouble() * 0.12 - 0.06);
            heard.AddRange(tracker.Step(shown));
            if (shown < now) heard.AddRange(tracker.Step(now));   // and the clock goes on from where it was
        }
        Assert.Equal(all.Select(s => s.Seed).Where(seed => all.First(x => x.Seed == seed).SoundTime <= now - 0.2).ToList(), heard.Select(s => s.Seed).Where(seed => all.First(x => x.Seed == seed).SoundTime <= now - 0.2).ToList());
        Assert.Equal(heard.Count, heard.Select(s => s.Seed).Distinct().Count());   // none twice
        for (int i = 1; i < heard.Count; i++) Assert.True(heard[i].SoundTime > heard[i - 1].SoundTime);   // strictly later each time
        // a jump forward (a join or a stall) replays nothing it skipped; a step back never lowers the mark
        var t2 = new WeatherThunderTracker();
        t2.Step(100); Assert.Empty(t2.Step(100 + 30));
        double s0 = WeatherPlan.ThunderIn(130, 400)[0].SoundTime;
        t2.Step(s0 + 1); Assert.Empty(t2.Step(s0 - 0.5)); Assert.Empty(t2.Step(s0 + 0.5));
        Assert.Empty(new WeatherThunderTracker().Step(double.NaN));
        // a small step back then forward across a strike's sound plays it once
        var t3 = new WeatherThunderTracker();
        t3.Step(s0 - 1.0); t3.Step(s0 - 0.2); Assert.Empty(t3.Step(s0 - 0.25));
        Assert.Single(t3.Step(s0 + 0.2)); Assert.Empty(t3.Step(s0 + 0.15)); Assert.Empty(t3.Step(s0 + 0.3));
        // a restarted clock (the same preset set again) hears its strikes again from the start
        var t4 = new WeatherThunderTracker();
        t4.Step(s0 - 1.0); Assert.Single(t4.Step(s0 + 0.5));
        t4.Step(s0 + 200); t4.Step(s0 - 1.0);   // the clock restarts far behind the mark
        Assert.Single(t4.Step(s0 + 0.5));
    }

    [Fact]
    public void The_full_flash_setting_defaults_to_reduced_survives_a_restart_and_reads_a_damaged_file_as_the_default()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-weather-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "weather-settings.json");
        try
        {
            SmClient.Extras.Weather.WeatherSettings.PathOverride = file;
            SmClient.Extras.Weather.WeatherSettings.Reset();
            Assert.False(SmClient.Extras.Weather.WeatherSettings.FullFlash);   // no file: reduced
            Assert.False(File.Exists(file));                                   // and nothing written until the player chooses
            SmClient.Extras.Weather.WeatherSettings.FullFlash = true;
            SmClient.Extras.Weather.WeatherSettings.Reset();                   // a restart
            Assert.True(SmClient.Extras.Weather.WeatherSettings.FullFlash);
            File.WriteAllText(file, "{ not json");
            SmClient.Extras.Weather.WeatherSettings.Reset();
            Assert.False(SmClient.Extras.Weather.WeatherSettings.FullFlash);
            File.WriteAllText(file, new string('x', 10000));
            SmClient.Extras.Weather.WeatherSettings.Reset();
            Assert.False(SmClient.Extras.Weather.WeatherSettings.FullFlash);
        }
        finally { SmClient.Extras.Weather.WeatherSettings.PathOverride = null; SmClient.Extras.Weather.WeatherSettings.Reset(); try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Ember_wind_is_sparse_steady_and_on_one_coherent_wind_direction()
    {
        var id = WeatherId.EmberWind;
        Assert.Equal(0f, WeatherPlan.EmberDensity(WeatherId.Rain, 1f));
        for (float i = 0.05f; i <= 1f; i += 0.05f) Assert.InRange(WeatherPlan.EmberDensity(id, i), 0.5f, 1f);
        Assert.Equal(0f, WeatherPlan.EmberDensity(id, 0f) - 0.5f);   // even at the opening's start the embers are the half-density kind: sparse, never a storm
        // one heading for the whole preset (the embers, the ash and the rain's lean read the same), at all times
        Assert.Equal(WeatherPlan.WindYaw(id), WeatherPlan.WindYaw(id));
        Assert.Equal(WeatherPlan.WindYaw(WeatherId.AshSquall), WeatherPlan.WindYaw(id));
        // the gusts keep within 15 % of the phase's own level
        for (double t = 0; t < 120; t += 0.3)
        {
            float level = WeatherPlan.Wind(id, 0.6f, t), flat = 0.25f + 0.25f * 0.6f;
            Assert.InRange(level, flat * 0.85f - 1e-4f, flat * 1.15f + 1e-4f);
        }
        // no weather without a wind: Rain, Clear, Low Fog, None have none
        foreach (var other in new[] { WeatherId.Rain, WeatherId.Clear, WeatherId.LowFog, WeatherId.None }) Assert.Equal(0f, WeatherPlan.Wind(other, 1f, 5));
    }

    [Fact]
    public void Hostile_inputs_never_throw_or_go_out_of_range()
    {
        Assert.Empty(WeatherPlan.StrikesIn(double.NaN, 10));
        Assert.Empty(WeatherPlan.StrikesIn(10, 5));
        Assert.Empty(WeatherPlan.ThunderIn(0, double.PositiveInfinity));
        Assert.Equal(0f, WeatherPlan.CloudGlow(double.NaN));
        Assert.Equal(0f, WeatherPlan.CloudGlow(-5));
        Assert.InRange(WeatherPlan.Wind(WeatherId.Thunderstorm, float.NaN, double.NaN), 0f, 1f);
        Assert.InRange(WeatherPlan.RainShare(WeatherId.Thunderstorm, 7f), 0f, 1f);
    }
}
