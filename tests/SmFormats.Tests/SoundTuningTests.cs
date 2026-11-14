using SmContent;

namespace SmFormats.Tests;

/// <summary>sound/tuning-tool and sound/classic-autocannon: the group mapping, the per-group fall-off, the overrides file and the autocannon body rule. Synthetic data only.</summary>
public class SoundTuningTests
{
    [Theory]
    [InlineData("play_sm_heavy_bolter_fire", true, false, SoundTuningGroups.OwnGun)]
    [InlineData("play_sm_heavy_bolter_fire", false, false, SoundTuningGroups.GunHeavy)]
    [InlineData("play_ch_autocannon_fire", false, false, SoundTuningGroups.GunHeavy)]
    [InlineData("play_sm_bolter_fire", false, false, SoundTuningGroups.GunBolter)]
    [InlineData("play_sm_plasma_rifle_fire", false, false, SoundTuningGroups.GunPlasma)]
    [InlineData("play_melta_gun_fire", false, false, SoundTuningGroups.GunMelta)]
    [InlineData("play_ch_lascannon_fire", false, false, SoundTuningGroups.GunLas)]
    [InlineData("play_sm_grenade_launcher_fire", false, false, SoundTuningGroups.GunOther)]
    [InlineData("play_sm_footsteps", true, true, SoundTuningGroups.OwnSteps)]
    [InlineData("play_sm_footsteps", false, true, SoundTuningGroups.StepsOthers)]
    [InlineData("play_sm_jump_pack_land", true, false, SoundTuningGroups.OwnJump)]
    [InlineData("play_sm_jump_pack_land", false, false, SoundTuningGroups.JumpOthers)]
    [InlineData("play_ch_frag_grenade_explode", false, false, SoundTuningGroups.Explosions)]
    [InlineData("play_sm_thunder_hammer_hit", false, false, SoundTuningGroups.MeleeImpacts)]
    [InlineData("play_sm_thunder_hammer_swing", true, false, SoundTuningGroups.OwnMelee)]
    [InlineData("play_sm_thunder_hammer_swing", false, false, SoundTuningGroups.MeleeOthers)]
    [InlineData("speech/x", true, false, SoundTuningGroups.OwnVoice)]
    [InlineData("speech/x", false, false, SoundTuningGroups.VoiceOthers)]
    [InlineData("play_menu_select", false, false, SoundTuningGroups.Ui)]
    [InlineData("something_unknown", false, false, SoundTuningGroups.Other)]
    [InlineData("something_unknown", true, false, SoundTuningGroups.OwnOther)]
    public void Events_land_in_their_logical_group(string evt, bool own, bool footstep, string group) =>
        Assert.Equal(group, SoundTuningGroups.Resolve(evt, own, footstep));

    [Fact]
    public void Musou_flags_win_over_the_event_and_every_group_is_listed_once()
    {
        Assert.Equal(SoundTuningGroups.MusouWar, SoundTuningGroups.Resolve("play_sm_bolter_fire", false, false, background: true));
        Assert.Equal(SoundTuningGroups.MusouThreat, SoundTuningGroups.Resolve("play_sm_bolter_fire", false, false, threat: true));
        foreach (var g in SoundTuningGroups.All) Assert.True(SoundTuningGroups.IndexOf(g.Id) >= 0);
        Assert.Equal(SoundTuningGroups.All.Count, SoundTuningGroups.All.Select(g => g.Id).Distinct().Count());
        Assert.Equal(0, SoundTuningGroups.IndexOf(SoundTuningGroups.Master));
    }

    [Fact]
    public void Defaults_change_nothing()
    {
        var t = new SoundTuning();
        Assert.True(t.IsDefault);
        for (int i = 0; i < SoundTuningGroups.All.Count; i++) Assert.Equal(0f, t.GainDb(i));
        Assert.Equal(0f, GroupTuning.Default.FalloffDb(500f));   // the data's curve: no custom change
    }

    [Fact]
    public void A_group_gain_adds_to_the_masters_and_is_clamped()
    {
        var t = new SoundTuning();
        t.Set(SoundTuningGroups.Master, new GroupTuning(VolumeDb: -3f));
        t.Set(SoundTuningGroups.OwnGun, new GroupTuning(VolumeDb: 5f));
        Assert.Equal(2f, t.GainDb(SoundTuningGroups.IndexOf(SoundTuningGroups.OwnGun)));
        t.Set(SoundTuningGroups.OwnGun, new GroupTuning(VolumeDb: 999f));
        Assert.Equal(GroupTuning.MaxVolumeDb, t.Of(SoundTuningGroups.OwnGun).VolumeDb);
        t.Set(SoundTuningGroups.OwnGun, new GroupTuning(VolumeDb: float.NaN));
        Assert.Equal(0f, t.Of(SoundTuningGroups.OwnGun).VolumeDb);
    }

    [Fact]
    public void The_custom_falloff_is_full_inside_the_minimum_then_loses_the_roll_off_per_doubling_then_goes_silent()
    {
        var g = new GroupTuning(0f, true, MinDistance: 4f, MaxDistance: 100f, RolloffDb: 6f);
        Assert.Equal(0f, g.FalloffDb(2f));
        Assert.Equal(0f, g.FalloffDb(4f));
        Assert.Equal(-6f, g.FalloffDb(8f), 3);
        Assert.Equal(-12f, g.FalloffDb(16f), 3);
        Assert.True(g.FalloffDb(95f) < g.FalloffDb(60f));
        Assert.Equal(-80f, g.FalloffDb(100f));
        Assert.Equal(0f, new GroupTuning(0f, true, 4f, 100f, 0f).FalloffDb(80f));   // no roll-off: flat until the fade
    }

    [Fact]
    public void Overrides_round_trip_and_only_changed_groups_are_written()
    {
        var defaults = new SoundTuning();
        var t = new SoundTuning { ReverbScale = 0.5f };
        t.Set(SoundTuningGroups.OwnGun, new GroupTuning(VolumeDb: 4.5f));
        t.Set(SoundTuningGroups.StepsOthers, new GroupTuning(-2f, true, 5f, 60f, 9f));
        string json = SoundTuningFile.ToJson(t, defaults);
        Assert.Contains("own_gun", json);
        Assert.Contains("steps_others", json);
        Assert.DoesNotContain("gun_melta", json);
        var back = defaults.Clone();
        Assert.True(SoundTuningFile.TryApply(json, back));
        Assert.Equal(0.5f, back.ReverbScale);
        Assert.Equal(4.5f, back.Of(SoundTuningGroups.OwnGun).VolumeDb);
        Assert.Equal(new GroupTuning(-2f, true, 5f, 60f, 9f), back.Of(SoundTuningGroups.StepsOthers));
        Assert.Equal(default, back.Of(SoundTuningGroups.GunMelta));
    }

    [Fact]
    public void Garbage_and_unknown_entries_are_skipped_not_fatal()
    {
        var t = new SoundTuning();
        Assert.False(SoundTuningFile.TryApply("not json", t));
        Assert.False(SoundTuningFile.TryApply("[1,2]", t));
        Assert.True(SoundTuningFile.TryApply("{\"groups\":{\"nope\":{\"volumeDb\":3},\"own_gun\":{\"volumeDb\":\"loud\"},\"gun_las\":{\"volumeDb\":7}},\"reverbScale\":9}", t));
        Assert.Equal(0f, t.Of(SoundTuningGroups.OwnGun).VolumeDb);
        Assert.Equal(7f, t.Of(SoundTuningGroups.GunLas).VolumeDb);
        Assert.Equal(3f, t.ReverbScale);   // clamped
    }

    [Fact]
    public void Save_load_and_export_use_files_with_the_overrides_on_top_of_the_shipped_defaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-tuning-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string defaultsPath = Path.Combine(dir, "defaults.json"), overridesPath = Path.Combine(dir, "sub", "overrides.json");
            var shipped = new SoundTuning();
            shipped.Set(SoundTuningGroups.GunHeavy, new GroupTuning(VolumeDb: 3f));
            SoundTuningFile.ExportDefaults(defaultsPath, shipped);
            Assert.Contains("gun_melta", File.ReadAllText(defaultsPath));   // the export holds every group

            var (defaults, current, note) = SoundTuningFile.Load(defaultsPath, overridesPath);
            Assert.Equal(3f, current.Of(SoundTuningGroups.GunHeavy).VolumeDb);
            Assert.Contains("shipped", note);

            current.Set(SoundTuningGroups.OwnSteps, new GroupTuning(VolumeDb: 6f));
            SoundTuningFile.SaveOverrides(overridesPath, current, defaults);
            var text = File.ReadAllText(overridesPath);
            Assert.Contains("own_steps", text);
            Assert.DoesNotContain("gun_heavy", text);   // unchanged from the shipped defaults: not an override

            var (_, again, note2) = SoundTuningFile.Load(defaultsPath, overridesPath);
            Assert.Equal(6f, again.Of(SoundTuningGroups.OwnSteps).VolumeDb);
            Assert.Equal(3f, again.Of(SoundTuningGroups.GunHeavy).VolumeDb);
            Assert.Contains("overrides", note2);

            var (_, none, note3) = SoundTuningFile.Load(Path.Combine(dir, "missing.json"), Path.Combine(dir, "missing2.json"));
            Assert.True(none.IsDefault);
            Assert.Contains("built-in", note3);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_overrides_file_lives_under_local_app_data_not_the_repo()
    {
        string path = SoundTuningFile.OverridesPath();
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), path);
        Assert.EndsWith(Path.Combine("sm-reimpl", "sound-tuning.json"), path);
    }

    static SoundVoice Voice(params uint[] path) => new(1, 1, 0f, -10f, 0f, 1, true, null, 50, path);

    [Fact]
    public void The_autocannons_footstep_body_alone_is_dropped()
    {
        Assert.True(ClassicAutocannonSound.IsBody(ClassicAutocannonSound.Event, Voice(1, ClassicAutocannonSound.BodyNode, 2)));
        Assert.False(ClassicAutocannonSound.IsBody(ClassicAutocannonSound.Event, Voice(1, 2)));   // the shot's own layers (tail, click) stay as the data has them
        Assert.False(ClassicAutocannonSound.IsBody("play_sm_heavy_bolter_fire", Voice(ClassicAutocannonSound.BodyNode)));   // no other weapon
        // A simulated post of the event: the tail, the body (the footstep samples), the click. Classic keeps the tail and the click only; the old mix keeps all three.
        var post = new[] { Voice(11, 12), Voice(21, ClassicAutocannonSound.BodyNode, 22), Voice(31) };
        var played = post.Where(v => !ClassicAutocannonSound.Applies(true, ClassicAutocannonSound.Event, v)).ToList();
        Assert.Equal([post[0], post[2]], played);
        Assert.Equal(3, post.Count(v => !ClassicAutocannonSound.Applies(false, ClassicAutocannonSound.Event, v)));
        Assert.Equal("play_ch_autocannon_fire", ClassicAutocannonSound.Event);   // the one event the weapon files post: no dreadnought or footstep event joins it
    }

    [Fact]
    public void The_classic_gate_leaves_the_old_mix_musou_and_other_weapons_alone()
    {
        var body = Voice(ClassicAutocannonSound.BodyNode);
        Assert.True(ClassicAutocannonSound.Applies(true, ClassicAutocannonSound.Event, body));
        Assert.False(ClassicAutocannonSound.Applies(false, ClassicAutocannonSound.Event, body));   // --old-classic-mix, and the mix Musou Mode runs in
        Assert.False(ClassicAutocannonSound.Applies(true, "play_sm_bolter_stalker_fire", body));    // Musou's autocannon plays the Stalker's event
        Assert.False(ClassicAutocannonSound.Applies(true, "play_sm_heavy_bolter_fire", body));
        Assert.False(ClassicAutocannonSound.Applies(true, ClassicAutocannonSound.Event, Voice(5)));
    }

    [Fact]
    public void A_group_gain_lifts_a_quiet_voice_over_the_cull_threshold_and_a_custom_curve_ends_where_the_owner_set_it()
    {
        // The client passes the voice's level plus the group's gain, and 0 (none) as the curve's end for a group with its own fall-off.
        float quiet = VoiceAudibility.VolumeThreshold - 5f;
        Assert.False(VoiceAudibility.IsAudible(true, true, 100f, 50f, quiet, 0f, 1, true, false));
        Assert.True(VoiceAudibility.IsAudible(true, true, 100f, 50f, quiet + 10f, 0f, 1, true, false));   // +10 dB group gain
        Assert.False(VoiceAudibility.IsAudible(true, true, 100f, 150f, -20f, 0f, 1, true, false));         // the data's curve ends at 100 m
        Assert.True(VoiceAudibility.IsAudible(true, true, 0f, 150f, -20f, 0f, 1, true, false));            // custom fall-off: the data's end is not tested
        var g = new GroupTuning(0f, true, 3f, 200f, 6f);
        Assert.True(g.FalloffDb(150f) > -80f);   // still sounding at 150 m with a 200 m maximum
        Assert.Equal(-80f, g.FalloffDb(200f));   // and gone at the maximum
    }
}
