using SmSim.Extras.Weather;
using Xunit;

namespace SmNet.Tests.Extras.Weather;

public class WeatherThunderFadeTests
{
    /// <summary>A model of the weather's voices: each has a level offset; the fade applies to every one it holds, a combat voice is not among them.</summary>
    sealed class Voices
    {
        public readonly System.Collections.Generic.List<string> Weather = [];
        public int Combat = 1;
        public float Db;
        readonly WeatherThunderFade _fade = new();
        WeatherId _prev = WeatherId.Thunderstorm;
        public bool Frame(WeatherId id, double dt)
        {
            if (WeatherThunderFade.Fades(_prev, id)) _fade.Start();
            else if (id != WeatherId.None && _fade.Active) { _fade.Cancel(); Db = 0; }
            _prev = id;
            if (!_fade.Active) return false;
            Db = _fade.Step(dt);
            if (_fade.Done) { Weather.Clear(); return true; }
            return false;
        }
    }

    [Fact]
    public void Only_a_change_to_None_fades()
    {
        Assert.True(WeatherThunderFade.Fades(WeatherId.Thunderstorm, WeatherId.None));
        Assert.True(WeatherThunderFade.Fades(WeatherId.Clear, WeatherId.None));
        Assert.False(WeatherThunderFade.Fades(WeatherId.Thunderstorm, WeatherId.Clear));
        Assert.False(WeatherThunderFade.Fades(WeatherId.Thunderstorm, WeatherId.Rain));
        Assert.False(WeatherThunderFade.Fades(WeatherId.None, WeatherId.None));
        Assert.False(WeatherThunderFade.Fades(WeatherId.None, WeatherId.Thunderstorm));
    }

    [Theory]
    [InlineData("near procedural roll")]
    [InlineData("installed far roll")]
    public void A_switch_to_None_during_a_roll_fades_the_weathers_voices_in_half_a_second_then_stops_them_and_leaves_combat_alone(string roll)
    {
        var v = new Voices(); v.Weather.Add(roll);
        v.Frame(WeatherId.Thunderstorm, 1.0 / 60);
        Assert.Equal(0f, v.Db);
        double t = 0; bool stopped = false; float last = 0f;
        while (t < 2 && !stopped) { stopped = v.Frame(WeatherId.None, 1.0 / 60); t += 1.0 / 60; Assert.True(v.Db <= last + 1e-4f); last = v.Db; }
        Assert.True(stopped); Assert.Equal(WeatherThunderFade.FloorDb, v.Db);
        Assert.InRange(t, 0.49, 0.53);
        Assert.Empty(v.Weather); Assert.Equal(1, v.Combat);
    }

    [Fact]
    public void A_switch_to_another_preset_keeps_the_tails_and_a_return_cancels_a_fade()
    {
        var v = new Voices(); v.Weather.Add("far roll");
        for (int i = 0; i < 600; i++) Assert.False(v.Frame(i < 10 ? WeatherId.Thunderstorm : WeatherId.Clear, 1.0 / 60));
        Assert.Single(v.Weather); Assert.Equal(0f, v.Db);
        var w = new Voices(); w.Weather.Add("near roll");
        w.Frame(WeatherId.None, 0.2);   // a fade begins
        Assert.True(w.Db < 0f);
        w.Frame(WeatherId.Thunderstorm, 0.1);   // the storm is back
        Assert.Equal(0f, w.Db);
        for (int i = 0; i < 300; i++) w.Frame(WeatherId.Thunderstorm, 1.0 / 60);
        Assert.Single(w.Weather);
    }

    [Fact]
    public void The_timer_ignores_bad_frame_times_and_cancel_drops_it()
    {
        var f = new WeatherThunderFade();
        Assert.Equal(0f, f.Step(1)); Assert.False(f.Done);
        f.Start(); Assert.Equal(0f, f.Step(double.NaN)); Assert.Equal(0f, f.Step(-1)); Assert.True(f.Active);
        f.Cancel(); Assert.False(f.Active);
        f.Start(); Assert.Equal(WeatherThunderFade.FloorDb, f.Step(0.6)); Assert.True(f.Done); Assert.False(f.Active);
    }
}
