using System.Text.RegularExpressions;
using SmContent;

namespace SmFormats.Tests;

/// <summary>sound/mix-expand: the per-sound mix editor's rules (the empty config changes nothing, overrides apply, the file round trip, the event-to-group mapping, the diff in words). Synthetic data only.</summary>
public class SoundMixConfigTests
{
    const string Heavy = "play_sm_heavy_bolter_fire";

    [Theory]
    [InlineData(Heavy, true)]
    [InlineData(Heavy, false)]
    [InlineData("speech/exertion/attack", true)]
    [InlineData("", false)]
    public void An_empty_config_changes_nothing(string evt, bool own)
    {
        var config = new SoundMixConfig();
        Assert.True(config.IsEmpty);
        Assert.Null(config.Resolve(evt, own));   // no effect object at all: the sound system takes its old path
        Assert.Equal(0f, SoundMixEffect.Gain(null));
        Assert.Equal(1.37f, SoundMixEffect.Pitch(null, 1.37f));
        Assert.Equal(42.5f, SoundMixEffect.Distance(null, 42.5f));
        Assert.Equal(0, config.ChangeCount);
    }

    [Fact]
    public void Setting_a_sound_back_to_its_defaults_leaves_the_config_empty_again()
    {
        var config = new SoundMixConfig();
        config.Set(Heavy, true, new SoundOverride(VolumeDb: 3f));
        Assert.False(config.IsEmpty);
        config.Set(Heavy, true, SoundOverride.None);
        Assert.True(config.IsEmpty);
        Assert.Null(config.Resolve(Heavy, true));
    }

    [Fact]
    public void Overrides_apply_to_their_own_side_only()
    {
        var config = new SoundMixConfig();
        int v0 = config.Version;
        config.Set(Heavy, own: true, new SoundOverride(VolumeDb: 3f, PitchSemitones: 12f, DistanceScale: 2f));
        Assert.True(config.Version > v0);
        var own = config.Resolve(Heavy, true)!;
        Assert.Equal(3f, SoundMixEffect.Gain(own));
        Assert.Equal(2f, SoundMixEffect.Pitch(own, 1f), 4);   // an octave up doubles the playback speed
        Assert.Equal(50f, SoundMixEffect.Distance(own, 100f));   // heard twice as far: the listener is read as half as far
        Assert.False(own.Raw);
        Assert.Null(config.Resolve(Heavy, false));   // other players' sound untouched
        Assert.Null(config.Resolve("play_sm_bolter_fire", true));   // another sound untouched
        Assert.Equal(1, config.ChangeCount);
    }

    [Fact]
    public void Values_are_clamped_and_matching_is_case_blind()
    {
        var config = new SoundMixConfig();
        config.Set("PLAY_X", false, new SoundOverride(VolumeDb: 999f, PitchSemitones: -99f, DistanceScale: 100f));
        var got = config.Get("play_x", false);
        Assert.Equal(SoundOverride.MaxVolumeDb, got.VolumeDb);
        Assert.Equal(SoundOverride.MinPitch, got.PitchSemitones);
        Assert.Equal(SoundOverride.MaxScale, got.DistanceScale);
        config.Set("play_nan", true, new SoundOverride(VolumeDb: float.NaN, DistanceScale: float.NaN, Mute: true));
        Assert.Equal(0f, config.Get("play_nan", true).VolumeDb);
        Assert.Equal(1f, config.Get("play_nan", true).DistanceScale);
    }

    [Fact]
    public void Mute_solo_and_raw_resolve_as_asked()
    {
        var config = new SoundMixConfig();
        config.Set(Heavy, true, new SoundOverride(Mute: true));
        Assert.True(config.Resolve(Heavy, true)!.Silenced);
        Assert.Equal(SoundMixEffect.SilenceDb, SoundMixEffect.Gain(config.Resolve(Heavy, true)));
        config.Reset(Heavy, true);
        Assert.True(config.IsEmpty);

        config.SetSolo(Heavy, true, true);
        Assert.False(config.Resolve(Heavy, true)!.Silenced);   // the soloed sound plays
        Assert.True(config.Resolve("play_sm_bolter_fire", true)!.Silenced);   // everything else is silent
        Assert.True(config.Resolve(Heavy, false)!.Silenced);   // the other side of the same sound is not soloed
        Assert.DoesNotContain("solo", SoundMixFile.ToJson(config));   // solo is a listening aid: never in a file
        config.SetSolo(Heavy, true, false);
        Assert.True(config.IsEmpty);

        config.GlobalRaw = true;
        Assert.Same(SoundMixEffect.RawOnly, config.Resolve("anything", false));
        config.GlobalRaw = false;
        config.Set(Heavy, false, new SoundOverride(Raw: true));
        Assert.True(config.Resolve(Heavy, false)!.Raw);
        Assert.Null(config.Resolve(Heavy, true));
    }

    [Fact]
    public void The_file_round_trips_and_writes_only_what_differs()
    {
        var config = new SoundMixConfig { GlobalRaw = true };
        config.Set(Heavy, true, new SoundOverride(VolumeDb: -4.5f, Mute: false));
        config.Set(Heavy, false, new SoundOverride(PitchSemitones: 2f, DistanceScale: 1.5f, Raw: true));
        config.Set("play_menu_select", false, new SoundOverride(Mute: true));
        var tuning = new SoundTuning { ReverbScale = 0.5f };
        tuning.Set(SoundTuningGroups.OwnGun, new GroupTuning(VolumeDb: 2f));
        string json = SoundMixFile.ToJson(config, tuning);
        Assert.Contains("\"own/play_sm_heavy_bolter_fire\"", json);
        Assert.DoesNotContain("pitchSemitones\": 0", json);
        Assert.DoesNotContain("\"mute\": false", json);

        var back = new SoundMixConfig();
        var backTuning = new SoundTuning();
        Assert.True(SoundMixFile.TryApply(json, back, backTuning));
        Assert.True(back.GlobalRaw);
        Assert.Equal(config.Get(Heavy, true), back.Get(Heavy, true));
        Assert.Equal(config.Get(Heavy, false), back.Get(Heavy, false));
        Assert.True(back.Get("play_menu_select", false).Mute);
        Assert.Equal(3, back.Count);
        Assert.Equal(0.5f, backTuning.ReverbScale);
        Assert.Equal(2f, backTuning.Of(SoundTuningGroups.OwnGun).VolumeDb);
        Assert.Equal(json, SoundMixFile.ToJson(back, backTuning));   // stable
    }

    [Fact]
    public void Bad_text_is_refused_and_bad_entries_are_skipped()
    {
        var config = new SoundMixConfig();
        Assert.False(SoundMixFile.TryApply("not json", config));
        Assert.False(SoundMixFile.TryApply("[1,2]", config));
        Assert.False(SoundMixFile.TryApply("{\"hello\": 1}", config));
        Assert.False(SoundMixFile.TryApply("{\"sounds\": 5}", config));
        Assert.False(SoundMixFile.TryApply("{\"version\": 2, \"sounds\": {}}", config));   // a newer file than this client knows
        Assert.False(SoundMixFile.TryApply("{\"sounds\": {}, \"tuning\": []}", config));
        Assert.True(SoundMixFile.TryApply("{\"sounds\": {\"weird/play_x\": {\"volumeDb\": 3}, \"own/\": {\"volumeDb\": 3}, \"own/play_ok\": {\"volumeDb\": \"loud\", \"mute\": true}, \"other/play_y\": 5}}", config));
        Assert.Equal(1, config.Count);
        Assert.True(config.Get("play_ok", true).Mute);
        Assert.Equal(0f, config.Get("play_ok", true).VolumeDb);   // a non-number is ignored, the rest of the entry is kept
    }

    [Fact]
    public void Shipped_defaults_then_the_owners_file_and_nothing_at_all()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-mix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string shipped = Path.Combine(dir, "defaults.json"), mine = Path.Combine(dir, "mine.json"), missing = Path.Combine(dir, "none.json");
            var (d0, c0, n0) = SoundMixFile.Load([missing], missing);
            Assert.True(d0.IsEmpty);
            Assert.True(c0.IsEmpty);   // no file anywhere: the game's own sound
            Assert.Equal("", n0);

            var shipConfig = new SoundMixConfig();
            shipConfig.Set(Heavy, true, new SoundOverride(VolumeDb: 5f));
            var shipTuning = new SoundTuning();
            shipTuning.Set(SoundTuningGroups.Ambience, new GroupTuning(VolumeDb: -3f));
            SoundMixFile.Write(shipped, shipConfig, shipTuning);

            var tuningDefaults = new SoundTuning();
            var (d1, c1, n1) = SoundMixFile.Load([missing, shipped], missing, tuningDefaults);
            Assert.Equal(5f, d1.Get(Heavy, true).VolumeDb);
            Assert.Equal(5f, c1.Get(Heavy, true).VolumeDb);
            Assert.Equal(-3f, tuningDefaults.Of(SoundTuningGroups.Ambience).VolumeDb);
            Assert.Equal("shipped sound mix", n1);

            var owner = shipConfig.Clone();   // the owner reset the shipped sound and set another
            owner.Set(Heavy, true, SoundOverride.None);
            owner.Set("play_sm_bolter_fire", true, new SoundOverride(VolumeDb: -2f));
            SoundMixFile.Write(mine, owner, null, shipConfig);
            Assert.Contains("\"reset\": true", File.ReadAllText(mine));
            var (_, c2, n2) = SoundMixFile.Load([shipped], mine);
            Assert.Equal(0f, c2.Get(Heavy, true).VolumeDb);   // the reset sticks
            Assert.Equal(-2f, c2.Get("play_sm_bolter_fire", true).VolumeDb);
            Assert.Contains("your sound mix", n2);

            // A later shipped default reaches a player who saved before: v2 of the defaults adds a sound and changes the one he never touched.
            var v2 = new SoundMixConfig();
            v2.Set(Heavy, true, new SoundOverride(VolumeDb: 9f));   // he reset this one: it stays his
            v2.Set("play_sm_stimpack", true, new SoundOverride(VolumeDb: 4f));   // new in v2: reaches him
            File.Delete(shipped);
            SoundMixFile.Write(shipped, v2);
            var (_, c2b, _) = SoundMixFile.Load([shipped], mine);
            Assert.Equal(4f, c2b.Get("play_sm_stimpack", true).VolumeDb);
            Assert.Equal(0f, c2b.Get(Heavy, true).VolumeDb);
            Assert.Equal(-2f, c2b.Get("play_sm_bolter_fire", true).VolumeDb);
            SoundMixFile.Write(shipped, shipConfig, shipTuning);

            File.WriteAllText(mine, "{ broken");
            var (_, c3, n3) = SoundMixFile.Load([shipped], mine);
            Assert.Equal(5f, c3.Get(Heavy, true).VolumeDb);   // a broken file falls back to the shipped defaults
            Assert.Contains("not valid", n3);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Exports_go_to_their_own_folder_with_a_timestamp_name_and_never_overwrite()
    {
        var at = new DateTime(2026, 11, 17, 9, 5, 3, DateTimeKind.Utc);
        string path = SoundMixFile.NewExportPath(at);
        Assert.Equal("20261117-090503.json", Path.GetFileName(path));
        Assert.Equal(SoundMixFile.ExportFolder, Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.StartsWith(SoundMixFile.Directory(), path);
    }

    // ---- the mapping ----

    [Theory]
    [InlineData("play_sm_heavy_bolter_fire", SoundMixGroup.Weapons, "Heavy bolter: fire (Marine)")]
    [InlineData("play_ch_lascannon_recharge", SoundMixGroup.Weapons, "Lascannon: recharge (Chaos)")]
    [InlineData("play_sm_plasma_venting_overheat", SoundMixGroup.Weapons, "Plasma weapons: venting overheat (Marine)")]
    [InlineData("play_dry_fire_bolter", SoundMixGroup.Weapons, "Bolter: dry fire")]
    [InlineData("play_sm_chainsword_swing_x2", SoundMixGroup.CombatMoves, "Chainsword: swing x2 (Marine)")]
    [InlineData("play_evade_roll", SoundMixGroup.CombatMoves, "Evade roll: roll")]
    [InlineData("play_sm_thunder_hammer_hit", SoundMixGroup.CombatMoves, "Thunder hammer: hit (Marine)")]
    [InlineData("play_sm_grenade_toss", SoundMixGroup.Equipment, "Grenade: toss (Marine)")]
    [InlineData("play_sm_jump_pack_launch", SoundMixGroup.Equipment, "Jump pack: launch (Marine)")]
    [InlineData("play_sm_stimpack", SoundMixGroup.Equipment, "Stimpack (Marine)")]
    [InlineData("play_mp_spawn_sm", SoundMixGroup.PlayerEvents, "Spawn: mp sm")]
    [InlineData("play_mp_point_being_captured", SoundMixGroup.PlayerEvents, "Capture point: mp")]
    [InlineData("play_sm_footfalls_normal", SoundMixGroup.PlayerEvents, "Footsteps: normal (Marine)")]
    [InlineData("speech/exertion/attack", SoundMixGroup.Voices, "Voice line: exertion attack")]
    [InlineData("play_or_bo_2_barks", SoundMixGroup.Voices, "Ork voice: bo 2 (Ork)")]
    [InlineData("Play_MP_TDM_09_ambs", SoundMixGroup.Ambience, "Map ambience: mp tdm 09")]
    [InlineData("Play_Menu_Select", SoundMixGroup.Ui, "Menu: select")]
    [InlineData("stop_ch_plasma_cannon_charge", SoundMixGroup.Weapons, "Plasma cannon: stop charge (Chaos)")]
    [InlineData("something_nobody_named", SoundMixGroup.Other, "something nobody named")]
    public void Events_get_a_group_and_a_readable_label(string evt, SoundMixGroup group, string label)
    {
        var info = SoundMixCatalog.Classify(evt);
        Assert.Equal(group, info.Group);
        Assert.Equal(label, info.Label, ignoreCase: true);
        Assert.Equal(evt, info.Event);
    }

    [Fact]
    public void Every_name_has_exactly_one_group_and_the_unmapped_go_to_Other()
    {
        string[] names = ["", " ", "er", "rer", "play_", "stop_", "Play_X", "speech/", "speech/a/b", "play_sm_heavy_bolter_fire", "set_state_impact_armor", "play_zzz_unknown", "éè", new string('a', 500)];
        var counts = SoundMixCatalog.Coverage(names);
        Assert.Equal(names.Length, counts.Values.Sum());   // none lost
        Assert.Equal(8, counts.Count);
        foreach (var n in names) Assert.NotNull(SoundMixCatalog.Classify(n).Label);
        Assert.Equal(SoundMixGroup.Other, SoundMixCatalog.Classify("play_zzz_unknown").Group);
        Assert.Equal(SoundMixGroup.Other, SoundMixCatalog.Classify("er").Group);
        Assert.Equal(8, SoundMixCatalog.Groups.Count);
        Assert.Equal(8, SoundMixCatalog.Groups.Select(SoundMixCatalog.GroupLabel).Distinct().Count());
    }

    [Fact]
    public void The_events_the_source_posts_are_all_mapped_and_few_are_Other()
    {
        // The event names the repository's own code mentions (the client's posts, the rules' constants): the mapping covers each of them, and the generic bucket stays small.
        string? root = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null && root is null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) root = d.FullName;
        Assert.NotNull(root);
        var rx = new Regex("\"((?:play|stop|Play|Stop)_[A-Za-z0-9_]+)\"");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { "client", "src" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, dir), "*.cs", SearchOption.AllDirectories).Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(seg => seg is "obj" or "bin")))
                foreach (Match m in rx.Matches(File.ReadAllText(file))) names.Add(m.Groups[1].Value);
        Assert.True(names.Count > 20, $"only {names.Count} event names found in the source");
        var counts = SoundMixCatalog.Coverage(names);
        Assert.Equal(names.Count, counts.Values.Sum());
        Assert.True(counts[SoundMixGroup.Other] * 10 <= names.Count, $"{counts[SoundMixGroup.Other]} of {names.Count} source event names are unmapped: " + string.Join(", ", names.Where(n => SoundMixCatalog.Classify(n).Group == SoundMixGroup.Other)));
    }

    [Fact]
    public void The_known_list_holds_what_was_registered_and_what_is_configured()
    {
        SoundMixCatalog.Register(["play_test_registered_one", "play_test_registered_one", "  "]);
        Assert.Single(SoundMixCatalog.KnownEvents(), n => n == "play_test_registered_one");
        var list = SoundMixCatalog.InGroup(SoundMixGroup.Other, ["play_only_in_a_file"]);
        Assert.Contains(list, i => i.Event == "play_only_in_a_file");   // a name only a file mentions is still listed
    }

    // ---- the diff in words ----

    [Fact]
    public void The_diff_says_in_plain_words_what_changed()
    {
        var before = new SoundMixConfig();
        var after = new SoundMixConfig { GlobalRaw = true };
        after.Set(Heavy, true, new SoundOverride(VolumeDb: 3f, Mute: true));
        after.Set(Heavy, false, new SoundOverride(DistanceScale: 2f, PitchSemitones: -1f));
        var afterTuning = new SoundTuning { ReverbScale = 0.5f };
        afterTuning.Set(SoundTuningGroups.OwnGun, new GroupTuning(VolumeDb: 2f));
        var lines = SoundMixFile.Describe(before, new SoundTuning(), after, afterTuning);
        Assert.Equal(5, lines.Count);
        Assert.Contains(lines, l => l.StartsWith("Raw mode on for every sound"));
        Assert.Contains(lines, l => l.Contains("Heavy bolter: fire (Marine), your own sound") && l.Contains("volume +3 dB") && l.Contains("muted"));
        Assert.Contains(lines, l => l.Contains("other players' sound") && l.Contains("pitch -1 semitones") && l.Contains("heard 2 times as far"));
        Assert.Contains(lines, l => l.Contains("Map reverb send x0.5 (was x1)"));
        Assert.Contains(lines, l => l.Contains("Your actions / Your gun") && l.Contains("volume +2 dB"));
        Assert.All(lines.Where(l => l.StartsWith("[Weapons]")), l => Assert.Contains(Heavy, l));

        Assert.Empty(SoundMixFile.Describe(after, afterTuning, after.Clone(), afterTuning.Clone()));   // nothing differs: nothing said
        var back = SoundMixFile.Describe(after, afterTuning, new SoundMixConfig(), new SoundTuning());
        Assert.Contains(back, l => l.StartsWith("Raw mode off"));
        Assert.Contains(back, l => l.Contains("back to the game's own"));
    }
}
