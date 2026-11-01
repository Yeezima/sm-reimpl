using SmContent;
using SmSim.Extras.Musou;
using Xunit;

namespace SmNet.Tests.Extras.Musou;

/// <summary>The crowd's voice budget and how a type's sounds are read from its files (<see cref="MusouSoundSet"/>).</summary>
public class MusouEnemySoundTests
{
    static SoundBinding Bind(string trigger, string evt, string ability = "") => new("", trigger, ability, "", [], evt, "", false, false, false, 0f, "", new Dictionary<string, float>());
    static SpeechBinding Say(string trigger, string type) => new("", trigger, "", [], SpeechKind.Exertion, "autodetect", type, "", "");

    static SoundTriggers Triggers(string character, params SoundFile[] files) => new()
    {
        Files = files.ToDictionary(f => f.Name, f => f, StringComparer.OrdinalIgnoreCase),
        ContainerSounds = new Dictionary<string, string>(),
        ClipTags = new Dictionary<string, IReadOnlyList<(string Tag, float At)>>(),
        Notes = [],
        BlueprintCharacters = files.ToDictionary(f => f.Name, _ => character, StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void A_group_posts_up_to_its_burst_then_refills_at_its_rate()
    {
        var budget = new MusouSoundBudget();
        var limit = MusouSoundRules.LimitOf(MusouSoundGroup.Gun);
        for (int i = 0; i < (int)limit.Burst; i++) Assert.Equal(MusouSoundVerdict.Post, budget.Take(MusouSoundGroup.Gun, 5f, 0, 0));
        Assert.Equal(MusouSoundVerdict.OverRate, budget.Take(MusouSoundGroup.Gun, 5f, 0, 0));
        budget.Refill(1f / limit.PerSecond);
        Assert.Equal(MusouSoundVerdict.Post, budget.Take(MusouSoundGroup.Gun, 5f, 0, 0));
        Assert.Equal((long)limit.Burst + 1, budget.Count(MusouSoundGroup.Gun, MusouSoundVerdict.Post));
        Assert.Equal(1, budget.Count(MusouSoundGroup.Gun, MusouSoundVerdict.OverRate));
    }

    [Fact]
    public void Beyond_a_groups_range_nothing_posts_and_no_token_is_spent()
    {
        var budget = new MusouSoundBudget();
        float range = MusouSoundRules.LimitOf(MusouSoundGroup.Footstep).Range;
        for (int i = 0; i < 100; i++) Assert.Equal(MusouSoundVerdict.TooFar, budget.Take(MusouSoundGroup.Footstep, range + 0.1f, 0, 0));
        Assert.Equal(MusouSoundVerdict.Post, budget.Take(MusouSoundGroup.Footstep, range, 0, 0));
    }

    [Fact]
    public void The_caps_hold_the_crowd_and_lines_count_only_for_voice_groups()
    {
        var budget = new MusouSoundBudget();
        Assert.Equal(MusouSoundVerdict.VoiceCap, budget.Take(MusouSoundGroup.Swing, 5f, MusouSoundRules.MaxVoices, 0));
        Assert.Equal(MusouSoundVerdict.SpeechCap, budget.Take(MusouSoundGroup.Grunt, 5f, 0, MusouSoundRules.MaxSpeech));
        Assert.Equal(MusouSoundVerdict.Post, budget.Take(MusouSoundGroup.Swing, 5f, 0, MusouSoundRules.MaxSpeech));
    }

    [Fact]
    public void The_boss_is_never_held_by_the_caps()
    {
        var budget = new MusouSoundBudget();
        Assert.Equal(MusouSoundVerdict.Post, budget.Take(MusouSoundGroup.Boss, 60f, MusouSoundRules.MaxVoices + 10, MusouSoundRules.MaxSpeech + 10));
    }

    [Fact]
    public void The_crowd_leaves_more_than_half_the_pool_to_the_players()
    {
        Assert.True(MusouSoundRules.MaxVoices * 2 < 48);
        Assert.True(MusouSoundRules.Stepping <= MusouSoundRules.Slots);
    }

    [Fact]
    public void A_weapon_that_whooshes_at_flare_and_swing_sounds_once()
    {
        var body = new SoundFile("ork_coop_nob", [Bind("evt_left_foot", "step")]);
        var axe = new SoundFile("ork_coop_nob_uge_axe", [Bind("weaponflare", "whoosh"), Bind("actn_huge_swing", "whoosh")]);
        var set = MusouSoundSet.Build(new MusouSoundFiles(MusouEnemyType.Nob, "ork_coop_nob", "ork_coop_nob_uge_axe"), Triggers("ork_nob", body, axe), _ => true);
        Assert.True(set.WindUp.Empty);
        Assert.Equal("whoosh", Assert.Single(set.Swing.Options).Events[0].StartEvent);
    }

    [Fact]
    public void An_event_no_bank_holds_is_reported_and_never_played()
    {
        var body = new SoundFile("boy", [Bind("actn_snd_vox_nav", "play_run"), Bind("actn_snd_vox_taunt_m_s", "play_gone")]);
        var set = MusouSoundSet.Build(new MusouSoundFiles(MusouEnemyType.Boy, "boy", "none"), Triggers("ork_boy_2", body), e => e != "play_gone");
        Assert.Contains("play_gone", set.Missing);
        Assert.True(set.Bark.Empty);
        Assert.False(set.Grunt.Empty);
    }

    [Fact]
    public void A_type_speaks_with_its_voice_blueprints_lines_and_character()
    {
        var body = new SoundFile("boss", [Bind("evt_left_foot", "boss_step")]);
        var voice = new SoundFile("nob", []) { Speech = [Say("actn_snd_vox_roar_h_l", "roar")] };
        var triggers = new SoundTriggers
        {
            Files = new Dictionary<string, SoundFile> { ["boss"] = body, ["nob"] = voice }, ContainerSounds = new Dictionary<string, string>(),
            ClipTags = new Dictionary<string, IReadOnlyList<(string Tag, float At)>>(), Notes = [],
            BlueprintCharacters = new Dictionary<string, string> { ["boss"] = "warboss", ["nob"] = "ork_nob" },
        };
        var set = MusouSoundSet.Build(new MusouSoundFiles(MusouEnemyType.Warboss, "boss", "none", VoiceBlueprint: "nob"), triggers, _ => true);
        Assert.Equal("ork_nob", set.Character);
        Assert.Equal("roar", Assert.Single(set.Roar.Options).Lines[0].Type);
        Assert.Equal("boss_step", Assert.Single(set.StepLeft.Options).Events[0].StartEvent);
    }

    [Fact]
    public void A_character_less_type_keeps_its_sounds_but_says_nothing()
    {
        var body = new SoundFile("thing", [Bind("evt_left_foot", "step")]) { Speech = [Say("speech_celebrate", "celebration")] };
        var triggers = new SoundTriggers { Files = new Dictionary<string, SoundFile> { ["thing"] = body }, ContainerSounds = new Dictionary<string, string>(), ClipTags = new Dictionary<string, IReadOnlyList<(string Tag, float At)>>(), Notes = [] };
        var set = MusouSoundSet.Build(new MusouSoundFiles(MusouEnemyType.Boy, "thing", "none"), triggers, _ => true);
        Assert.Null(set.Character);
        Assert.False(set.StepLeft.Empty);
        Assert.True(set.Celebrate.Empty);
    }

    [Fact]
    public void Every_enemy_type_has_files()
    {
        foreach (var t in Enum.GetValues<MusouEnemyType>()) Assert.Contains(MusouSoundSet.Files, f => f.Type == t);
    }
}
