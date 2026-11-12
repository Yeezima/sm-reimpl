using System.Text.RegularExpressions;
using SmContent;
using SmSim.Extras.Musou;
using Xunit;
using V3 = System.Numerics.Vector3;
using V2 = System.Numerics.Vector2;

namespace SmNet.Tests.Extras.Musou;

/// <summary>musou/soundscape-tuning (owner interview 2026-11-12; Musou Mode only, client only): the rules of the clip guard, the threat cues, the softer explosion duck, the far war's duck and the Waaagh! split.</summary>
public class MusouSoundscapeTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx");
    }

    [Fact]
    public void The_guard_lowers_masters_ceiling_by_the_boost_so_the_peak_leaves_at_the_old_ceiling()
    {
        Assert.Equal(SoundMixPass.MasterCeilingDb, MusouSoundscape.GuardMasterCeilingDb + SoundMixPass.MasterBoostDb, 4);
        Assert.True(MusouSoundscape.SoftCeilingDb <= MusouSoundscape.GuardMasterCeilingDb + 0.2f);
        Assert.True(MusouSoundscape.SoftThresholdDb < MusouSoundscape.SoftCeilingDb);
    }

    [Theory]
    [InlineData(MusouSoundscape.Threat.BossAttack, 14f, false, false, true)]
    [InlineData(MusouSoundscape.Threat.BossAttack, 20f, false, false, false)]
    [InlineData(MusouSoundscape.Threat.BossAttack, 20f, true, false, true)]
    [InlineData(MusouSoundscape.Threat.BossAttack, 30f, true, false, false)]
    [InlineData(MusouSoundscape.Threat.NobSwing, 5f, false, false, true)]
    [InlineData(MusouSoundscape.Threat.NobSwing, 9f, true, false, true)]
    [InlineData(MusouSoundscape.Threat.NobSwing, 9f, false, false, false)]
    [InlineData(MusouSoundscape.Threat.NobSwing, 12f, true, false, false)]
    [InlineData(MusouSoundscape.Threat.MeleeBehind, 4f, true, true, true)]
    [InlineData(MusouSoundscape.Threat.MeleeBehind, 4f, true, false, false)]   // in view: heard as always
    [InlineData(MusouSoundscape.Threat.MeleeBehind, 4f, false, true, false)]   // not at him
    [InlineData(MusouSoundscape.Threat.MeleeBehind, 9f, true, true, false)]    // not close
    [InlineData(MusouSoundscape.Threat.Rokkit, 3f, false, false, true)]
    [InlineData(MusouSoundscape.Threat.Rokkit, 9f, false, false, false)]
    [InlineData(MusouSoundscape.Threat.BigShoota, 30f, true, false, true)]
    [InlineData(MusouSoundscape.Threat.BigShoota, 30f, false, false, false)]
    [InlineData(MusouSoundscape.Threat.BigShoota, 50f, true, false, false)]
    [InlineData(MusouSoundscape.Threat.SquigFuse, 6f, false, false, true)]
    [InlineData(MusouSoundscape.Threat.SquigFuse, 12f, false, false, false)]
    public void Only_close_threats_qualify(MusouSoundscape.Threat kind, float distance, bool aimed, bool outside, bool expected) =>
        Assert.Equal(expected, MusouSoundscape.Qualifies(kind, distance, aimed, outside));

    [Fact]
    public void Facing_and_view_follow_the_game_yaw()
    {
        // game yaw 0 faces +Y: forward is (-sin, cos)
        Assert.True(MusouSoundscape.IsAimedAt(new V3(0, 0, 0), 0f, new V3(0, 5, 0)));
        Assert.False(MusouSoundscape.IsAimedAt(new V3(0, 0, 0), 0f, new V3(0, -5, 0)));
        Assert.True(MusouSoundscape.IsAimedAt(new V3(0, 0, 0), MathF.PI / 2, new V3(-5, 0, 0)));   // yaw +90 faces -X
        Assert.False(MusouSoundscape.IsAimedAt(new V3(0, 0, 0), 0f, new V3(5, 1, 0)));              // 79 degrees off
        Assert.False(MusouSoundscape.IsOutsideView(new V3(0, 0, 0), new V2(0, 1), new V3(1, 5, 0)));
        Assert.True(MusouSoundscape.IsOutsideView(new V3(0, 0, 0), new V2(0, 1), new V3(0, -3, 0)));
        Assert.True(MusouSoundscape.IsOutsideView(new V3(0, 0, 0), new V2(0, 1), new V3(5, -1, 0)));
    }

    [Fact]
    public void The_threat_bucket_gives_a_burst_then_the_rate()
    {
        var b = new MusouThreatBudget();
        int first = 0;
        while (b.Take()) first++;
        Assert.Equal((int)MusouSoundscape.ThreatBurst, first);
        b.Refill(1f);
        int next = 0;
        while (b.Take()) next++;
        Assert.Equal((int)MusouSoundscape.ThreatBurst, next);   // a second refills six tokens, the bucket holds four
    }

    static SoundPropertyAction Act(bool reset, SoundProperty p = SoundProperty.Volume, float value = -6f, float delay = 0f, float fade = 0.2f) =>
        new(p, 0x0f736776, reset, value, true, delay, fade, false);

    [Fact]
    public void A_far_blast_gives_no_cut_and_a_close_one_a_shallower_shorter_one()
    {
        Assert.Null(MusouSoundscape.ShapeDuck(Act(false), MusouSoundscape.DuckRadius + 0.1f));
        var set = MusouSoundscape.ShapeDuck(Act(false, value: -6f, fade: 0.2f), 4f)!;
        Assert.Equal(-6f * MusouSoundscape.DuckDepth, set.Value);
        Assert.Equal(0.2f * MusouSoundscape.DuckTime, set.Fade, 4);
        var lpf = MusouSoundscape.ShapeDuck(Act(false, SoundProperty.Lpf, 60f), 4f)!;
        Assert.Equal(60f * MusouSoundscape.DuckDepth, lpf.Value);
        var reset = MusouSoundscape.ShapeDuck(Act(true, delay: 1.5f, fade: 0.6f), 4f)!;
        Assert.Equal(1.5f * MusouSoundscape.DuckTime, reset.Delay, 4);   // the hold is shorter
        Assert.Equal(0.6f * MusouSoundscape.DuckTime, reset.Fade, 4);
        var pitch = MusouSoundscape.ShapeDuck(Act(false, SoundProperty.Pitch, -300f), 4f)!;
        Assert.Equal(-300f, pitch.Value);   // only volume and low-pass get shallower
    }

    [Fact]
    public void The_far_war_ducks_while_engaged_holds_and_releases()
    {
        var d = new MusouBackgroundDucker();
        for (int i = 0; i < 10; i++) d.Step(0.1f, false);
        Assert.Equal(0f, d.Db);
        float t = 0;
        while (d.Db > MusouSoundscape.BackgroundDuckDb + 1e-4f && t < 5f) { d.Step(0.05f, true); t += 0.05f; }
        Assert.Equal(MusouSoundscape.BackgroundDuckDb, d.Db, 3);
        Assert.InRange(t, MusouSoundscape.BackgroundAttack - 0.06f, MusouSoundscape.BackgroundAttack + 0.06f);
        // engaged ends: it holds, then comes back over the release time, never past 0
        float held = 0;
        while (held < MusouSoundscape.BackgroundHold - 0.1f) { d.Step(0.05f, false); held += 0.05f; }
        Assert.Equal(MusouSoundscape.BackgroundDuckDb, d.Db, 3);
        float back = 0;
        while (d.Db < 0f && back < 10f) { d.Step(0.05f, false); back += 0.05f; }
        Assert.InRange(back, MusouSoundscape.BackgroundRelease - 0.2f, MusouSoundscape.BackgroundRelease + 0.4f);
        d.Step(1f, false);
        Assert.Equal(0f, d.Db);
    }

    [Fact]
    public void Engagement_is_a_wind_up_or_strike_near_or_anyone_in_touch()
    {
        Assert.True(MusouSoundscape.Engages(MusouEnemyState.WindUp, 6f));
        Assert.True(MusouSoundscape.Engages(MusouEnemyState.Strike, 8f));
        Assert.False(MusouSoundscape.Engages(MusouEnemyState.WindUp, 12f));
        Assert.False(MusouSoundscape.Engages(MusouEnemyState.Seek, 6f));
        Assert.True(MusouSoundscape.Engages(MusouEnemyState.Seek, 2f));
    }

    [Fact]
    public void Waaagh_is_on_by_default_and_the_mob_yells_stay_off()
    {
        Assert.True(MusouSoundscape.Waaagh([]));
        Assert.True(MusouSoundscape.Waaagh(["--musou-ambient-voices"]));
        Assert.False(MusouSoundscape.Waaagh(["--no-musou-waaagh"]));
        Assert.False(MusouVoiceRules.AmbientVoices([]));   // the far Ork mobs' yells: still only with the restore switch
    }

    [Fact]
    public void The_soundscape_switches_have_names_and_a_composite()
    {
        Assert.True(MusouSoundAb.TryParse("soundscape", out var all, out _));
        Assert.Equal(MusouSoundOff.Soundscape, all);
        Assert.True(MusouSoundAb.TryParse("clipguard,threat,softduck,bgduck,bed,waaagh", out var each, out _));
        Assert.Equal(MusouSoundOff.Soundscape, each);
        Assert.True(MusouSoundOff.All.HasFlag(MusouSoundOff.Soundscape));
    }

    [Fact]
    public void The_sound_systems_new_hooks_default_to_the_old_behaviour_and_only_musous_files_set_them()
    {
        string sound = File.ReadAllText(Path.Combine(Root(), "client", "SoundSystem.cs"));
        Assert.Matches(@"public Func<SoundPropertyAction, float, SoundPropertyAction\?>\? ShapeBusActions \{ get; set; \}\s*\r?\n", sound);   // null: the data's actions as they are
        Assert.Matches(@"public float BackgroundDb \{ get; set; \}\s*\r?\n", sound);                                                          // 0
        Assert.Matches(@"public bool Threat;\s*\r?\n", sound);
        Assert.Matches(@"public bool Background;\s*\r?\n", sound);
        var setter = new Regex(@"\b(ShapeBusActions|BackgroundDb)\s*=(?!=)|\bSetClipGuard\s*\(", RegexOptions.CultureInvariant);
        string client = Path.Combine(Root(), "client");
        var offenders = Directory.EnumerateFiles(client, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.godot{Path.DirectorySeparatorChar}") && setter.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(client, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("Extras/Musou/", StringComparison.Ordinal) && f != "SoundSystem.cs")
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_bed_uses_the_installs_own_events()
    {
        Assert.Equal("play_world_fire_03", MusouSoundscape.FireEvent);
        Assert.Equal("play_wind_open", MusouSoundscape.WindEvent);
        Assert.Equal("stop_wind_open", MusouSoundscape.WindStopEvent);
    }
}
