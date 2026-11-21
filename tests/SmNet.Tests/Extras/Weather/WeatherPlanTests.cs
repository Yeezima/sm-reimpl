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
        for (double t = build; t < peak; t += 1) Assert.InRange(WeatherPlan.AshShare(id, At(t).Phase, At(t).Intensity), 0f, WeatherPlan.WarningFlecks + 1e-6f);
        Assert.True(WeatherPlan.AshShare(id, WeatherPhase.Buildup, At(peak - 0.1).Intensity) > 0.05f, "the warning shows flecks");
        Assert.Equal(1f, WeatherPlan.AshShare(id, WeatherPhase.Peak, 1f));
        float lastShare = 1f;
        for (double t = rec; t < end; t += 1) { float a = WeatherPlan.AshShare(id, At(t).Phase, At(t).Intensity); Assert.True(a <= lastShare + 1e-6f); lastShare = a; }
        Assert.True(WeatherPlan.AshShare(id, WeatherPhase.Recovery, At(end - 0.01).Intensity) < 0.01f);
        // the crosswind: the strongest wind is at the peak, a quiet one before and after
        float windBuild = WeatherPlan.Wind(id, At(build + 1).Intensity, 0), windPeak = WeatherPlan.Wind(id, 1f, 0), windEnd = WeatherPlan.Wind(id, At(end - 0.01).Intensity, 0);
        Assert.True(windPeak > 0.7f && windBuild < 0.1f && windEnd < 0.1f, $"{windBuild} {windPeak} {windEnd}");
        // others have no ash and no warning
        foreach (var other in new[] { WeatherId.Thunderstorm, WeatherId.Rain, WeatherId.EmberWind }) Assert.Equal(0f, WeatherPlan.AshShare(other, WeatherPhase.Peak, 1f));
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
