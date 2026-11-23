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
    public void The_pin_test_aid_holds_one_clients_clock_inside_a_phase_and_nothing_else()
    {
        var pinned = new WeatherClientModel { PinPhase = WeatherPhase.Peak, PinAllowed = true };
        var plain = new WeatherClientModel();
        pinned.Receive(WeatherWire.Encode(At(WeatherId.Rain, 0)));
        plain.Receive(WeatherWire.Encode(At(WeatherId.Rain, 0)));
        Assert.Equal(WeatherPhase.Opening, plain.State.Phase);
        for (int i = 0; i < 600; i++) { pinned.Advance(1.0); plain.Advance(1.0); Assert.Equal(WeatherPhase.Peak, pinned.State.Phase); }   // 600 s: past the whole cycle, the pin loops over the 180 s peak
        pinned.Receive(WeatherWire.Encode(At(WeatherId.Rain, 3)));   // a server repeat does not move it out
        Assert.Equal(WeatherPhase.Peak, pinned.State.Phase);
        Assert.Equal(1f, pinned.EffectiveIntensity);
        var none = new WeatherClientModel { PinPhase = WeatherPhase.Peak, PinAllowed = true };
        none.Receive(WeatherWire.Encode(At(WeatherId.None, 0)));
        none.Advance(5);
        Assert.Equal(0f, none.EffectiveIntensity);   // None stays None
    }

    [Fact]
    public void Repair_2_the_lull_pin_holds_the_drawn_level_at_the_presets_lull_and_the_words_parse()
    {
        foreach (var id in new[] { WeatherId.Rain, WeatherId.AshSquall, WeatherId.Thunderstorm })
        {
            float lull = WeatherClock.Lull(WeatherCatalog.Of(id));
            foreach (int span in new[] { 0, 600 })
            {
                var m = new WeatherClientModel { PinLull = true, PinAllowed = true };
                var d = new WeatherDirector(id, span); d.Advance(1);
                m.Receive(WeatherWire.Encode(WeatherWire.Of(d)));
                for (int i = 0; i < 30; i++) { m.Advance(1.0); Assert.Equal(lull, m.EffectiveIntensity, 2); }   // held at the lull, not looping back to full
            }
        }
        var gated = new WeatherClientModel { PinLull = true };   // no developer-tools gate: the server's clock
        gated.Receive(WeatherWire.Encode(At(WeatherId.Rain, 0)));
        Assert.Equal(1f, gated.EffectiveIntensity);
        Assert.True(WeatherClientModel.TryParsePinWord("lull", out var p1, out bool l1) && p1 is null && l1);
        Assert.True(WeatherClientModel.TryParsePinWord("FULL", out var p2, out bool l2) && p2 == WeatherPhase.Peak && !l2);
        Assert.True(WeatherClientModel.TryParsePinWord("recovery", out var p3, out _) && p3 == WeatherPhase.Recovery);
        Assert.False(WeatherClientModel.TryParsePinWord("7", out _, out _));
        Assert.False(WeatherClientModel.TryParsePinWord("", out _, out _));
    }

    [Fact]
    public void Repair_3_the_strike_pin_loops_the_drawn_clock_round_a_real_peak_strike_so_its_glow_and_full_flash_recur()
    {
        foreach (int span in new[] { 0, 600 })
        {
            double? at = WeatherClientModel.PinnedStrikeTime(WeatherId.Thunderstorm, span);
            Assert.NotNull(at);
            var m = new WeatherClientModel { PinStrike = true, PinAllowed = true, FullFlash = true };
            var d = new WeatherDirector(WeatherId.Thunderstorm, span); d.Advance(1);
            m.Receive(WeatherWire.Encode(WeatherWire.Of(d)));
            int glowFrames = 0, flashFrames = 0;
            for (int f = 0; f < 600; f++)   // 10 s at 60 Hz: two and a half loops
            {
                m.Advance(1.0 / 60);
                var s = m.State;
                Assert.InRange(s.Elapsed, at!.Value - 1.0 - 1e-6, at.Value + WeatherClientModel.StrikeLoopSeconds);
                if (WeatherPlan.CloudGlow(s.Elapsed, s.Span) > 0f) glowFrames++;
                if (WeatherPlan.FullFlash(s.Elapsed, s.Span) > 0f) flashFrames++;
            }
            Assert.True(glowFrames > 60 && flashFrames > 30, $"span {span}: glow {glowFrames} flash {flashFrames} frames");
        }
        Assert.Null(WeatherClientModel.PinnedStrikeTime(WeatherId.Rain, 0));
        Assert.True(WeatherClientModel.TryParsePinWord("strike", out var p, out bool l) && p is null && !l);
    }

    [Fact]
    public void Repair_2_a_bed_stops_with_a_half_second_fade_to_silence_and_a_wanted_bed_cancels_it()
    {
        var f = new WeatherBedFade();
        f.Start(-25f);
        float a = f.Step(0.1, out bool d1);
        Assert.False(d1); Assert.InRange(a, -32.1f, -31.9f);   // a fifth of the way from -25 to -60
        f.Start(-10f);   // ignored while running
        float b = f.Step(0.3, out bool d2);
        Assert.False(d2); Assert.True(b < a);
        f.Step(0.1, out bool d3);
        Assert.True(d3); Assert.False(f.Active);   // 0.5 s in all: stopped
        f.Start(-20f); f.Cancel();
        Assert.False(f.Active);
        Assert.Equal(WeatherThunderFade.Seconds, WeatherBedFade.Seconds);   // the beds and the thunder go down together
    }

    [Fact]
    public void Repair_3_a_second_fade_in_one_life_starts_with_a_one_frame_step()
    {
        // the rig samples its frame time every update (Thunderstorm -> Rain fades the wind; a second later Rain -> Clear fades the hiss)
        var clock = new WeatherFrameDt();
        var wind = new WeatherBedFade(); var hiss = new WeatherBedFade();
        long ms = 1000; clock.Step(ms);
        wind.Start(-20f);
        bool done = false;
        for (int f = 0; f < 120 && !done; f++) { ms += 16; wind.Step(clock.Step(ms), out done); }
        Assert.True(done);
        for (int f = 0; f < 60; f++) { ms += 16; clock.Step(ms); }   // a second of rain: the clock keeps being sampled
        hiss.Start(-22f);
        ms += 16;
        float first = hiss.Step(clock.Step(ms), out _);
        Assert.InRange(-22f - first, 0f, 2f);   // one 16 ms frame of a 0.5 s fade: about 1.3 dB, not the old 0.25 s jump (19 dB)
        Assert.Equal(0.0, new WeatherFrameDt().Step(5000));   // the first sample is 0
        var c = new WeatherFrameDt(); c.Step(0);
        Assert.Equal(WeatherBedFade.MaxStep, c.Step(10_000));   // a stall is capped
    }

    [Fact]
    public void Weather_fix_2_a_pin_without_the_developer_tools_gate_draws_the_servers_phase()
    {
        var a = new WeatherClientModel { PinPhase = WeatherPhase.Peak };   // not allowed: an ordinary client
        var b = new WeatherClientModel { PinPhase = WeatherPhase.Recovery };
        var server = new WeatherClientModel();
        foreach (var m in new[] { a, b, server }) m.Receive(WeatherWire.Encode(At(WeatherId.Rain, 0)));
        for (int i = 0; i < 40; i++) { a.Advance(1.0); b.Advance(1.0); server.Advance(1.0); }
        Assert.False(a.Pinned);
        Assert.Equal(server.State.Phase, a.State.Phase);
        Assert.Equal(server.State.Phase, b.State.Phase);
        Assert.Equal(server.State.Elapsed, a.State.Elapsed, 6);
    }

    [Fact]
    public void Weather_fix_2_a_gated_client_pins_and_a_lapse_returns_to_the_servers_clock()
    {
        var m = new WeatherClientModel { PinPhase = WeatherPhase.Peak, PinAllowed = true };
        var server = new WeatherClientModel();
        m.Receive(WeatherWire.Encode(At(WeatherId.Rain, 0))); server.Receive(WeatherWire.Encode(At(WeatherId.Rain, 0)));
        for (int i = 0; i < 20; i++) { m.Advance(1.0); server.Advance(1.0); }
        Assert.Equal(WeatherPhase.Peak, m.State.Phase);
        Assert.NotEqual(server.State.Phase, m.State.Phase);
        m.PinAllowed = false;   // the gate lapses
        Assert.Equal(server.State.Phase, m.State.Phase);
        Assert.Equal(server.State.Elapsed, m.State.Elapsed, 6);
        m.Advance(1.0); server.Advance(1.0);
        Assert.Equal(server.State.Elapsed, m.State.Elapsed, 6);
        m.PinAllowed = true;   // and back
        Assert.Equal(WeatherPhase.Peak, m.State.Phase);
    }

    [Theory]
    [InlineData("peak", true, WeatherPhase.Peak)]
    [InlineData("OPENING", true, WeatherPhase.Opening)]
    [InlineData("Buildup", true, WeatherPhase.Buildup)]
    [InlineData("recovery", true, WeatherPhase.Recovery)]
    [InlineData("7", false, WeatherPhase.Opening)]
    [InlineData("-1", false, WeatherPhase.Opening)]
    [InlineData("2", false, WeatherPhase.Opening)]
    [InlineData("foo", false, WeatherPhase.Opening)]
    [InlineData("", false, WeatherPhase.Opening)]
    [InlineData("peak,recovery", false, WeatherPhase.Opening)]
    public void Weather_fix_2_the_pin_takes_only_the_four_phase_names(string text, bool ok, WeatherPhase want)
    {
        Assert.Equal(ok, WeatherClientModel.TryParsePin(text, out var phase));
        if (ok) Assert.Equal(want, phase);
    }

    [Fact]
    public void Weather_fix_2_a_lapse_while_held_returns_to_the_exact_wire_elapsed()
    {
        var m = new WeatherClientModel { PinPhase = WeatherPhase.Peak, PinAllowed = true };
        m.Receive(WeatherWire.Encode(new WeatherStateWire(WeatherId.Rain, WeatherClock.At(WeatherId.Rain, 12.345, 600).Phase, 12_345, false, 0, 600, true)));
        m.Advance(50);   // held: the server's clock stands still, and a pinned client's does not run either
        Assert.Equal(WeatherPhase.Peak, m.State.Phase);
        m.PinAllowed = false;
        Assert.Equal(12.345, m.State.Elapsed, 6);
        Assert.Equal(WeatherClock.At(WeatherId.Rain, 12.345, 600).Phase, m.State.Phase);
        m.Advance(50);
        Assert.Equal(12.345, m.State.Elapsed, 6);
    }

    [Fact]
    public void Weather_fix_2_a_lapse_with_a_span_returns_to_the_servers_fitted_clock()
    {
        var wire = new WeatherStateWire(WeatherId.Rain, WeatherClock.At(WeatherId.Rain, 5.0, 600).Phase, 5_000, false, 0, 600, false);
        var m = new WeatherClientModel { PinPhase = WeatherPhase.Peak, PinAllowed = true };
        var server = new WeatherClientModel();
        m.Receive(WeatherWire.Encode(wire)); server.Receive(WeatherWire.Encode(wire));
        for (int i = 0; i < 25; i++) { m.Advance(1.0); server.Advance(1.0); }
        Assert.Equal(WeatherPhase.Peak, m.State.Phase);
        m.PinAllowed = false;
        Assert.Equal(server.State.Elapsed, m.State.Elapsed, 6);
        Assert.Equal(server.State.Phase, m.State.Phase);
        Assert.Equal(600, m.State.Span);
    }

    [Fact]
    public void Weather_fix_2_clear_resets_the_pin_clock()
    {
        var m = new WeatherClientModel { PinPhase = WeatherPhase.Buildup, PinAllowed = true };
        m.Receive(WeatherWire.Encode(At(WeatherId.Rain, 0)));
        m.Advance(30);
        double early = m.State.PhaseSeconds;
        Assert.True(early >= 29);
        m.Clear();
        m.Receive(WeatherWire.Encode(At(WeatherId.Rain, 0)));
        Assert.True(m.State.PhaseSeconds < 1);   // the loop restarts at the phase's start
    }

    [Fact]
    public void The_w2_presets_change_the_look_at_their_peak_and_start_from_the_maps_own_look()
    {
        var m = new WeatherClientModel();
        foreach (var id in new[] { WeatherId.AshSquall, WeatherId.Thunderstorm, WeatherId.EmberWind })
        {
            m.Receive(WeatherWire.Encode(At(id, 200)));
            Assert.Equal(id, m.State.Id);
            Assert.False(m.Grade.IsIdentity, id + " must draw its own look");
            m.Receive(WeatherWire.Encode(At(id, 0)));
            Assert.False(m.Grade.IsIdentity, id + " is full the moment it starts (the weather finish)");
        }
    }

    [Fact]
    public void None_and_zero_intensity_are_exactly_the_identity_so_teardown_returns_to_the_baseline()
    {
        Assert.True(WeatherGrade.For(WeatherId.None, 1f).IsIdentity);
        foreach (var id in new[] { WeatherId.LowFog, WeatherId.Rain, WeatherId.Thunderstorm, WeatherId.AshSquall, WeatherId.EmberWind })
        {
            Assert.True(WeatherGrade.For(id, 0f).IsIdentity);
            Assert.False(WeatherGrade.For(id, 1f).IsIdentity);
        }
        for (float i = 0f; i <= 1f; i += 0.1f) Assert.True(WeatherGrade.For(WeatherId.Clear, i).IsIdentity);   // the weather finish: Clear is the authored look at every level
        var user = LightingPresets.For(LightingPresetChoice.PaleAshfall);
        Assert.Equal(user, WeatherGrade.Combine(user, LightingGrade.Identity));   // no weather: the player's preset alone
    }

    [Theory]
    [InlineData(WeatherId.Clear)]
    [InlineData(WeatherId.LowFog)]
    [InlineData(WeatherId.Rain)]
    [InlineData(WeatherId.Thunderstorm)]
    [InlineData(WeatherId.AshSquall)]
    [InlineData(WeatherId.EmberWind)]
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
        // stacked on the strongest lighting preset the limits still hold (Clear is the identity since the weather finish: the player's preset alone, under its own limits)
        if (WeatherGrade.For(id, 1f).IsIdentity) return;
        foreach (var choice in Enum.GetValues<LightingPresetChoice>())
        {
            var g = WeatherGrade.Combine(LightingPresets.For(choice), WeatherGrade.For(id, 1f));
            Assert.InRange(g.Exposure, WeatherGrade.MinExposure, WeatherGrade.MaxExposure);
            Assert.True(g.FogDensity <= WeatherGrade.MaxFogFactor && g.Ambient >= 1f);
        }
    }

    [Fact]
    public void Low_fog_adds_a_light_haze_clear_changes_nothing_and_rain_dims_the_sun()
    {
        float fog = WeatherGrade.For(WeatherId.LowFog, 1f).FogDensity;
        Assert.InRange(fog, 1.3f, 2f);   // the ground fog is the rig's cards; the depth haze stays light so long sightlines stay usable
        Assert.True(WeatherGrade.For(WeatherId.Clear, 1f).IsIdentity);
        Assert.True(WeatherGrade.For(WeatherId.Rain, 1f).SunEnergy < 0.5f);
    }
}
