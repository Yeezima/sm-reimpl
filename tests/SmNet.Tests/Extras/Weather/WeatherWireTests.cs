using SmClient.Customizer.Model;
using SmClient.Extras.Weather;
using SmNet.Extras.Weather;
using SmSim.Extras.Weather;

namespace SmNet.Tests.Extras.Weather;

/// <summary>Weather W1: the weather state section and command, the per-slot forecast in the hub section, and the client's Godot-free model and look.</summary>
public class WeatherWireTests
{
    // ---- the state section ----

    [Fact]
    public void The_state_round_trips_and_the_phase_must_match_the_clock()
    {
        var d = new WeatherDirector(WeatherId.Rain);
        d.Advance(100);
        var wire = WeatherWire.Of(d);
        var bytes = WeatherWire.Encode(wire);
        Assert.Equal(WeatherWire.StateBytes, bytes.Length);
        Assert.True(WeatherWire.TryDecode(bytes, out WeatherStateWire back));
        Assert.Equal(wire, back);
        Assert.Equal(WeatherPhase.Buildup, back.Phase);
        var lie = (byte[])bytes.Clone(); lie[1] = (byte)WeatherPhase.Peak;
        Assert.False(WeatherWire.TryDecode(lie, out WeatherStateWire _));
    }

    [Fact]
    public void A_state_never_decodes_from_garbage()
    {
        var good = WeatherWire.Encode(WeatherWire.Of(new WeatherDirector(WeatherId.Clear)));
        for (int len = 0; len < good.Length; len++) Assert.False(WeatherWire.TryDecode(good.AsSpan(0, len), out WeatherStateWire _));
        Assert.False(WeatherWire.TryDecode(good.Append((byte)0).ToArray(), out WeatherStateWire _));
        var bad = (byte[])good.Clone(); bad[0] = 99;
        Assert.False(WeatherWire.TryDecode(bad, out WeatherStateWire _));
        bad = (byte[])good.Clone(); bad[6] = 2;
        Assert.False(WeatherWire.TryDecode(bad, out WeatherStateWire _));
        var rng = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            var b = new byte[rng.Next(0, 12)]; rng.NextBytes(b);
            WeatherWire.TryDecode(b, out WeatherStateWire _);   // never throws
            WeatherWire.TryDecode(b, out WeatherCommand _);
        }
    }

    [Fact]
    public void Every_preset_and_phase_survives_the_wire_to_the_millisecond()
    {
        foreach (var def in WeatherCatalog.All)
            for (double t = 0; t < 900; t += 0.37)
            {
                var d = new WeatherDirector(def.Id); d.Advance(t);
                Assert.True(WeatherWire.TryDecode(WeatherWire.Encode(WeatherWire.Of(d)), out WeatherStateWire back), $"{def.Key} at {t}");
                Assert.Equal(WeatherClock.At(def.Id, back.ElapsedMs / 1000.0).Phase, back.Phase);
            }
    }

    [Fact]
    public void The_overridden_flag_and_serial_travel()
    {
        var d = new WeatherDirector(WeatherId.Clear);
        d.SetOverride(WeatherId.LowFog);
        Assert.True(WeatherWire.TryDecode(WeatherWire.Encode(WeatherWire.Of(d)), out WeatherStateWire s));
        Assert.True(s.Overridden);
        Assert.Equal(d.Serial, s.Serial);
        d.FollowMatch();
        Assert.True(WeatherWire.TryDecode(WeatherWire.Encode(WeatherWire.Of(d)), out s));
        Assert.False(s.Overridden);
    }

    // ---- the command ----

    [Fact]
    public void The_command_round_trips_and_refuses_nonsense()
    {
        foreach (var c in new[] { new WeatherCommand(WeatherCommandKind.Set, WeatherId.Rain), new WeatherCommand(WeatherCommandKind.Set, WeatherId.None), new WeatherCommand(WeatherCommandKind.Follow) })
        {
            var bytes = WeatherWire.Encode(c);
            Assert.InRange(bytes.Length, 1, Protocol.MaxExtrasCommandPayload);
            Assert.True(WeatherWire.TryDecode(bytes, out WeatherCommand back));
            Assert.Equal(c, back);
        }
        Assert.False(WeatherWire.TryDecode(new byte[] { 1, 99 }, out WeatherCommand _));
        Assert.False(WeatherWire.TryDecode(new byte[] { 2, 1 }, out WeatherCommand _));
        Assert.False(WeatherWire.TryDecode(new byte[] { 3 }, out WeatherCommand _));
        Assert.False(WeatherWire.TryDecode(ReadOnlySpan<byte>.Empty, out WeatherCommand _));
    }

    // ---- the client's model and look ----

    static WeatherStateWire At(WeatherId id, double seconds, bool overridden = false, byte serial = 0)
    {
        var d = new WeatherDirector(id); d.Advance(seconds);
        var w = WeatherWire.Of(d);
        return w with { Overridden = overridden, Serial = serial };
    }

    [Fact]
    public void The_client_model_follows_the_server_state_and_runs_its_clock_between_sections()
    {
        var m = new WeatherClientModel();
        Assert.False(m.Has);
        Assert.Equal((WeatherId.None, 0f), (m.State.Id, m.EffectiveIntensity));
        Assert.True(m.Receive(WeatherWire.Encode(At(WeatherId.Rain, 100))));
        Assert.Equal((WeatherId.Rain, WeatherPhase.Buildup), (m.State.Id, m.State.Phase));
        m.Advance(30);   // 130 s into rain: the peak starts at 105 s
        Assert.Equal(WeatherPhase.Peak, m.State.Phase);
        Assert.Equal(1f, m.EffectiveIntensity);
        Assert.False(m.Receive(new byte[] { 1, 2, 3 }));
        Assert.Equal(1, m.Dropped);
    }

    [Fact]
    public void A_reconnecting_client_gets_the_current_phase_and_a_cleared_one_is_the_default_look()
    {
        var server = new WeatherDirector(WeatherId.LowFog);
        server.Advance(150);   // a peak
        var m = new WeatherClientModel();
        m.Receive(WeatherWire.Encode(WeatherWire.Of(server)));
        Assert.Equal(server.Current.Phase, m.State.Phase);
        Assert.Equal(server.Current.Intensity, m.EffectiveIntensity, 3);
        m.Clear();
        Assert.True(m.Grade.IsIdentity);
        Assert.Equal(0f, m.EffectiveIntensity);
    }

    [Fact]
    public void A_stub_preset_draws_the_default_look_until_its_milestone()
    {
        var m = new WeatherClientModel();
        foreach (var id in new[] { WeatherId.AshSquall, WeatherId.Thunderstorm, WeatherId.EmberWind })
        {
            m.Receive(WeatherWire.Encode(At(id, 200)));
            Assert.Equal(id, m.State.Id);
            Assert.True(m.Grade.IsIdentity, id + " must not change the look yet");
        }
    }

    [Fact]
    public void None_and_zero_intensity_are_exactly_the_identity_so_teardown_returns_to_the_baseline()
    {
        Assert.True(WeatherGrade.For(WeatherId.None, 1f).IsIdentity);
        foreach (var id in new[] { WeatherId.Clear, WeatherId.LowFog, WeatherId.Rain })
        {
            Assert.True(WeatherGrade.For(id, 0f).IsIdentity);
            Assert.False(WeatherGrade.For(id, 1f).IsIdentity);
        }
        var user = LightingPresets.For(LightingPresetChoice.PaleAshfall);
        Assert.Equal(user, WeatherGrade.Combine(user, LightingGrade.Identity));   // no weather: the player's preset alone
    }

    [Theory]
    [InlineData(WeatherId.Clear)]
    [InlineData(WeatherId.LowFog)]
    [InlineData(WeatherId.Rain)]
    public void No_weather_hides_players_the_shade_is_never_darker_and_the_fog_stays_within_its_limit(WeatherId id)
    {
        for (float i = 0; i <= 1f; i += 0.05f)
        {
            var g = WeatherGrade.Combine(LightingGrade.Identity, WeatherGrade.For(id, i));
            Assert.InRange(g.Exposure, WeatherGrade.MinExposure, WeatherGrade.MaxExposure);
            Assert.True(g.Ambient >= 1f);
            Assert.True(g.FogDensity <= WeatherGrade.MaxFogFactor);
            Assert.True(g.SunEnergy >= 0.4f, $"{id} at {i}: sun {g.SunEnergy}");
        }
        // stacked on the strongest lighting preset the limits still hold
        foreach (var choice in Enum.GetValues<LightingPresetChoice>())
        {
            var g = WeatherGrade.Combine(LightingPresets.For(choice), WeatherGrade.For(id, 1f));
            Assert.InRange(g.Exposure, WeatherGrade.MinExposure, WeatherGrade.MaxExposure);
            Assert.True(g.FogDensity <= WeatherGrade.MaxFogFactor && g.Ambient >= 1f);
        }
    }

    [Fact]
    public void Low_fog_thickens_the_haze_clear_thins_it_and_rain_dims_the_sun()
    {
        Assert.True(WeatherGrade.For(WeatherId.LowFog, 1f).FogDensity > 3f);
        Assert.True(WeatherGrade.For(WeatherId.Clear, 1f).FogDensity < 0.6f);
        Assert.True(WeatherGrade.For(WeatherId.Clear, 1f).SunEnergy > 1.4f);
        Assert.True(WeatherGrade.For(WeatherId.Rain, 1f).SunEnergy < 0.5f);
    }
}
