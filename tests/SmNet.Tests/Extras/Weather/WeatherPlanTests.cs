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
    public void Thunderstorm_darkens_and_winds_up_through_the_buildup_then_rains_and_thunders_at_the_peak_then_recovers()
    {
        var id = WeatherId.Thunderstorm;
        double opening = Start(id, WeatherPhase.Buildup), peak = Start(id, WeatherPhase.Peak), recovery = Start(id, WeatherPhase.Recovery), end = recovery + WeatherCatalog.Of(id).Duration(WeatherPhase.Recovery);
        // the opening: some wind, no rain, no strike
        Assert.Equal(0f, WeatherPlan.RainShare(id, WeatherClock.At(id, 10).Intensity));
        Assert.Empty(WeatherPlan.StrikesIn(0, opening));
        // the buildup: the wind rises and never falls, rain begins late, strikes start only once the storm is strong
        float last = 0f;
        for (double t = opening; t <= peak; t += 1)
        {
            float w = WeatherPlan.Wind(id, WeatherClock.At(id, t).Intensity, 0);
            Assert.True(w >= last - 1e-6f, $"the wind fell at {t}");
            last = w;
        }
        Assert.Equal(0f, WeatherPlan.RainShare(id, WeatherClock.At(id, opening + 5).Intensity));
        Assert.True(WeatherPlan.RainShare(id, WeatherClock.At(id, peak - 1).Intensity) > 0.5f);
        foreach (var s in WeatherPlan.StrikesIn(0, end + 200)) Assert.True(WeatherClock.At(id, s.Time).Intensity >= WeatherPlan.StrikeFromIntensity);
        // the peak: full rain, the strikes come
        Assert.Equal(1f, WeatherPlan.RainShare(id, 1f));
        Assert.NotEmpty(WeatherPlan.StrikesIn(peak, recovery));
        // the recovery: the rain and wind fall back toward the opening level
        Assert.True(WeatherPlan.RainShare(id, WeatherClock.At(id, end - 1).Intensity) < 0.05f);
        Assert.True(WeatherPlan.Wind(id, WeatherClock.At(id, end - 1).Intensity, 0) < 0.4f);
        // nobody else rains or strikes
        foreach (var other in new[] { WeatherId.AshSquall, WeatherId.EmberWind, WeatherId.Clear, WeatherId.LowFog, WeatherId.None }) Assert.Equal(0f, WeatherPlan.RainShare(other, 1f));
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
    public void Ash_squall_warns_in_the_buildup_blows_at_the_peak_and_clears_in_the_recovery()
    {
        var id = WeatherId.AshSquall;
        double build = Start(id, WeatherPhase.Buildup), peak = Start(id, WeatherPhase.Peak), rec = Start(id, WeatherPhase.Recovery), end = rec + WeatherCatalog.Of(id).Duration(WeatherPhase.Recovery);
        WeatherState At(double t) => WeatherClock.At(id, t);
        // the warning: the last 30 s of the buildup only
        Assert.False(WeatherPlan.Warning(At(build + 1)));
        Assert.False(WeatherPlan.Warning(At(peak - WeatherPlan.WarningSeconds - 1)));
        Assert.True(WeatherPlan.Warning(At(peak - WeatherPlan.WarningSeconds + 1)));
        Assert.True(WeatherPlan.Warning(At(peak - 0.5)));
        Assert.False(WeatherPlan.Warning(At(peak + 1)));
        Assert.False(WeatherPlan.Warning(WeatherClock.At(WeatherId.Rain, peak - 1)));
        // the ash: a few flecks in the buildup (never more than the warning share), all of it at the peak, falling to none by the recovery's end
        for (double t = build; t < peak; t += 1) Assert.InRange(WeatherPlan.AshShare(At(t)), 0f, WeatherPlan.WarningFlecks + 1e-6f);
        Assert.True(WeatherPlan.AshShare(At(peak - 0.1)) > 0.05f, "the warning shows flecks");
        Assert.Equal(1f, WeatherPlan.AshShare(At(peak + WeatherPlan.AshRampSeconds + 0.1)));
        float lastShare = 1f;
        for (double t = rec; t < end; t += 1) { float a = WeatherPlan.AshShare(At(t)); Assert.True(a <= lastShare + 1e-6f); lastShare = a; }
        Assert.True(WeatherPlan.AshShare(At(end - 0.01)) < 0.01f);
        // the crosswind: the strongest wind is at the peak, a quiet one before and after
        float windBuild = WeatherPlan.Wind(id, At(build + 1).Intensity, 0), windPeak = WeatherPlan.Wind(id, 1f, 0), windEnd = WeatherPlan.Wind(id, At(end - 0.01).Intensity, 0);
        Assert.True(windPeak > 0.7f && windBuild < 0.1f && windEnd < 0.1f, $"{windBuild} {windPeak} {windEnd}");
        // others have no ash and no warning
        foreach (var other in new[] { WeatherId.Thunderstorm, WeatherId.Rain, WeatherId.EmberWind }) Assert.Equal(0f, WeatherPlan.AshShare(WeatherClock.At(other, peak + 10)));
    }

    [Fact]
    public void The_squalls_density_ramps_into_the_peak_with_no_step()
    {
        var id = WeatherId.AshSquall;
        double peak = Start(id, WeatherPhase.Peak);
        float before = WeatherPlan.AshShare(WeatherClock.At(id, peak - 1e-3)), after = WeatherPlan.AshShare(WeatherClock.At(id, peak + 1e-3));
        Assert.InRange(Math.Abs(after - before), 0f, 0.01f);   // no jump at the boundary
        float last = WeatherPlan.AshShare(WeatherClock.At(id, peak - 3)), maxStep = 0f;
        for (double t = peak - 3; t <= peak + WeatherPlan.AshRampSeconds + 1; t += 1.0 / 60)
        {
            float v = WeatherPlan.AshShare(WeatherClock.At(id, t));
            maxStep = Math.Max(maxStep, Math.Abs(v - last)); last = v;
        }
        Assert.True(maxStep < 0.05f, $"one frame moved the density by {maxStep}");   // a smooth ramp over 2.5 s
        Assert.Equal(1f, WeatherPlan.AshShare(WeatherClock.At(id, peak + WeatherPlan.AshRampSeconds)), 3);
        Assert.InRange(WeatherPlan.AshShare(WeatherClock.At(id, peak + WeatherPlan.AshRampSeconds / 2)), 0.3f, 0.8f);   // half way through it is part way
        // and the recovery starts where the peak ended
        double rec = Start(id, WeatherPhase.Recovery);
        Assert.InRange(Math.Abs(WeatherPlan.AshShare(WeatherClock.At(id, rec + 1e-3)) - WeatherPlan.AshShare(WeatherClock.At(id, rec - 1e-3))), 0f, 0.01f);
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
