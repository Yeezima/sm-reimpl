using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmFormats.Tests;

/// <summary>Ork O8 and fidelity gap 9: the Ork Boy's sound bindings, the rules for when his lines play, the menu sound rules; on synthetic data only.</summary>
public class OrkSoundTests
{
    static (string, object) Sound(string evt, bool footstep = false, bool terrain = false) =>
        ("post_sound_event", new (string, object)[]
        {
            ("$ref", "action_commands\\post_sound_event"), ("start_event_name", evt), ("stop_event_name", ""), ("trigger_point", "hitpoint"),
            ("use_terrain_material", terrain), ("time_delay", 0), ("is_footstep_sound", footstep), ("apply_only_if_on_player", false),
        });

    static (string, object) Exertion(string type) =>
        ("post_speech_exertion_event", new (string, object)[] { ("$ref", "action_commands\\post_speech_exertion_event"), ("1_character", "autodetect"), ("2_exertion_type", type) });

    /// <summary>A blueprint shaped like <c>ork_coop_boy</c>: audio_ext character, footsteps, a vox tag, a death action, a stun action and a landing.</summary>
    static BafDocument Boy() => BafDocument.Read(SyntheticBafWriter.Build(
        ("extensions", new (string, object)[]
        {
            ("audio_ext", new (string, object)[] { ("$ref", "entity_extensions\\audio_ext"), ("character", "ork_boy_2"), ("unit_type", "ork_boy") }),
            ("biped_locomotion_ext", new (string, object)[]
            {
                ("finish_falling_actions", new (string, object)[] { ("$ref", "action"), ("commands", new (string, object)[] { Sound("play_or_land") }) }),
            }),
            ("entity_actions_ext", new (string, object)[]
            {
                ("triggered_actions", new (string, object)[]
                {
                    ("evt_left_foot", new (string, object)[] { ("commands", new (string, object)[] { Sound("play_or_boy_footstep", footstep: true, terrain: true) }) }),
                    ("actn_snd_vox_death_yell_m_s", new (string, object)[] { ("commands", new (string, object)[] { Exertion("death") }) }),
                    ("actn_snd_vox_taunt_m_s", new (string, object)[] { ("commands", new (string, object)[] { Sound("play_or_bo_2_barks") }) }),
                    ("speech_celebrate", new (string, object)[] { ("commands", new (string, object)[] { Exertion("celebration") }) }),
                }),
            }),
            ("health_ext", new (string, object)[]
            {
                ("death_action", new (string, object)[] { ("$ref", "action"), ("commands", new (string, object)[] { Sound("play_or_horde_bodyfall") }) }),
                ("stunned_action", new (string, object)[] { ("$ref", "action"), ("commands", new (string, object)[] { Exertion("stunned") }) }),
            }),
        })));

    [Fact]
    public void The_speech_character_is_the_blueprints_audio_character()
    {
        Assert.Equal("ork_boy_2", OrkSounds.SpeechCharacter(Boy()));
        var none = BafDocument.Read(SyntheticBafWriter.Build(("extensions", new (string, object)[] { ("health_ext", new (string, object)[] { ("health", 1) }) })));
        Assert.Null(OrkSounds.SpeechCharacter(none));
    }

    [Fact]
    public void The_boys_blueprint_binds_footsteps_landing_death_stun_and_vox_tags()
    {
        var triggers = SoundTriggers.Build(new Dictionary<string, BafDocument> { [OrkSounds.Blueprint] = Boy() }, new Dictionary<string, string>(), null);
        var file = triggers.File("ORK_COOP_BOY")!;
        var step = Assert.Single(file.Tag("EVT_Left_Foot"));
        Assert.Equal(("play_or_boy_footstep", true, true), (step.StartEvent, step.IsFootstep, step.UseTerrainMaterial));
        Assert.Equal("play_or_land", Assert.Single(file.Of(OrkSounds.LandTrigger)).StartEvent);
        Assert.Equal("play_or_horde_bodyfall", Assert.Single(file.Of("death_action")).StartEvent);
        Assert.Equal("play_or_bo_2_barks", Assert.Single(file.Tag(OrkSounds.BarkTag)).StartEvent);
        // The voice lines the rules use sit where the client looks for them.
        Assert.Equal("death", Assert.Single(file.SpeechTag(OrkSounds.DeathTag)).Type);
        Assert.Equal("celebration", Assert.Single(file.SpeechTag(OrkSounds.TauntTag)).Type);
        Assert.Equal("stunned", Assert.Single(file.SpeechOf(OrkSounds.StunTrigger)).Type);
        Assert.Empty(file.SpeechTag(OrkSounds.RoarTag));                                   // absent in this synthetic file: nothing invented
    }

    [Fact]
    public void The_ork_blueprint_is_one_of_the_player_blueprints_the_triggers_load()
    {
        Assert.Contains(OrkSounds.Blueprint, SoundTriggers.PlayerBlueprints);
        Assert.Equal("ork_coop_boy", OrkSounds.Blueprint);
        Assert.Equal(2, SoundTriggers.PlayerBlueprints.Count(n => n != OrkSounds.Blueprint));   // the marine and the Chaos blueprint are still there
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(0.5f, false)]
    [InlineData(1f, true)]
    [InlineData(24f, true)]
    [InlineData(-30f, false)]
    public void Only_real_damage_is_a_hit(float lost, bool hit) => Assert.Equal(hit, OrkSounds.IsHit(lost));

    [Fact]
    public void Big_hits_get_the_big_grunt_and_small_ones_the_small()
    {
        Assert.Equal(OrkSounds.HitSmallTag, OrkSounds.HitTagFor(1f));
        Assert.Equal(OrkSounds.HitSmallTag, OrkSounds.HitTagFor(OrkSounds.BigHit - 0.01f));
        Assert.Equal(OrkSounds.HitBigTag, OrkSounds.HitTagFor(OrkSounds.BigHit));
        Assert.Equal(OrkSounds.HitBigTag, OrkSounds.HitTagFor(200f));
    }

    [Fact]
    public void A_taunt_needs_a_standing_ork_and_spacing()
    {
        Assert.True(OrkSounds.Taunts(0f, 10.0, double.NegativeInfinity));
        Assert.True(OrkSounds.Taunts(OrkSounds.TauntMaxSpeed, 10.0, double.NegativeInfinity));
        Assert.False(OrkSounds.Taunts(OrkSounds.TauntMaxSpeed + 0.1f, 10.0, double.NegativeInfinity));   // running: no taunt
        Assert.False(OrkSounds.Taunts(0f, 10.0, 10.0 - OrkSounds.TauntSpacing + 0.1));                  // taunted a moment ago
        Assert.True(OrkSounds.Taunts(0f, 10.0, 10.0 - OrkSounds.TauntSpacing));
    }

    [Fact]
    public void A_roar_waits_out_its_spacing()
    {
        Assert.True(OrkSounds.Roars(0.0, double.NegativeInfinity));
        Assert.False(OrkSounds.Roars(3.0, 0.0));
        Assert.True(OrkSounds.Roars(OrkSounds.RoarSpacing, 0.0));
    }

    [Fact]
    public void An_ability_start_sound_is_remembered_for_its_window_per_emitter_and_event()
    {
        var posts = new RecentPosts();
        Assert.False(posts.Recent(1, "play_or_choppa_swing", 5.0, OrkSounds.DuplicateWindow));
        posts.Note(1, "play_or_choppa_swing", 5.0);
        Assert.True(posts.Recent(1, "play_or_choppa_swing", 5.0 + OrkSounds.DuplicateWindow - 0.01, OrkSounds.DuplicateWindow));
        Assert.False(posts.Recent(1, "play_or_choppa_swing", 5.0 + OrkSounds.DuplicateWindow, OrkSounds.DuplicateWindow));   // later: the next swing sounds
        Assert.False(posts.Recent(2, "play_or_choppa_swing", 5.1, OrkSounds.DuplicateWindow));                                // another Ork
        Assert.False(posts.Recent(1, "play_or_gear_move_medium", 5.1, OrkSounds.DuplicateWindow));                             // another event
        Assert.False(posts.Recent(1, "play_or_choppa_swing", 4.0, OrkSounds.DuplicateWindow));                                // before the note (a clock reset)
    }

    [Theory]
    [InlineData(ShotHit.Body, true, true, false, 10f, "actn_snd_vox_sync_damage_small")]
    [InlineData(ShotHit.Head, true, true, false, 40f, "actn_snd_vox_sync_damage_big")]
    [InlineData(ShotHit.Body, true, true, false, 25f, "actn_snd_vox_sync_damage_big")]
    [InlineData(ShotHit.World, true, true, false, 40f, null)]       // a wall is no hit on the Ork
    [InlineData(ShotHit.None, true, true, false, 40f, null)]
    [InlineData(ShotHit.Body, false, true, false, 40f, null)]       // a marine does not grunt like an Ork
    [InlineData(ShotHit.Body, true, false, false, 40f, null)]       // a corpse does not grunt
    [InlineData(ShotHit.Body, true, true, true, 40f, null)]         // the own Ork's damage is seen in its own record
    [InlineData(ShotHit.Body, true, true, false, 0.5f, null)]       // no damage
    public void A_shot_that_hits_a_remote_ork_sets_off_a_grunt_sized_by_the_weapon(ShotHit hit, bool ork, bool alive, bool own, float damage, string? tag) =>
        Assert.Equal(tag, OrkSounds.ShotGruntTag(hit, ork, alive, own, damage));

    static BehaviorGraph Graph(string clip, params string[] tags) =>
        new("g_" + clip, [], [new BehaviorSequencer("s_" + clip, [clip], tags.Select((t, i) => new BehaviorTag("Action", 0.1f * (i + 1), 0.1f * (i + 1), t, null)).ToList(), [1f])]);

    static BafDocument WeaponWithTags(params string[] tags) => BafDocument.Read(SyntheticBafWriter.Build(
        ("action_data", new (string, object)[]
        {
            ("triggered_actions", tags.Select(t => (t, (object)new (string, object)[] { ("commands", new (string, object)[] { Sound("play_" + t) }) })).ToArray()),
        })));

    [Fact]
    public void The_reload_scan_of_a_marine_weapon_is_the_same_with_and_without_the_ork_rigs_clips()
    {
        // The Ork's reload clip carries more of the weapon's tags than the marine's: scanned together it would win for the marine's weapon.
        var marine = new[] { Graph("SM_RNG_Bolter_Reload_01", "actn_clip_out", "actn_clip_in") };
        var ork = new[] { Graph("BOY_2HR_RNG_Reload_S_01", "actn_clip_out", "actn_clip_in", "actn_cock") };
        var docs = new Dictionary<string, BafDocument> { ["bolter"] = WeaponWithTags("actn_clip_out", "actn_clip_in", "actn_cock"), ["shoota"] = WeaponWithTags("actn_clip_out", "actn_clip_in", "actn_cock") };
        var none = new Dictionary<string, string>();

        var alone = SoundTriggers.Build(docs, none, marine);
        var both = SoundTriggers.Build(docs, none, marine, null, ork);

        Assert.Equal(alone.ReloadTags(alone.File("bolter")!), both.ReloadTags(both.File("bolter")!));
        Assert.Equal(["actn_clip_out", "actn_clip_in"], both.ReloadTags(both.File("bolter")!).Select(t => t.Tag));
        // The Ork's own scan finds its clip, and the tags of both rigs are in the one table.
        Assert.Equal(["actn_clip_out", "actn_clip_in", "actn_cock"], both.ReloadTags(both.File("shoota")!, orkRig: true).Select(t => t.Tag));
        Assert.Equal(["BOY_2HR_RNG_Reload_S_01"], both.OrkOnlyClips);
        Assert.Equal(2, both.ClipTags["SM_RNG_Bolter_Reload_01"].Count);
        Assert.Equal(3, both.ClipTags["BOY_2HR_RNG_Reload_S_01"].Count);
        Assert.Empty(alone.OrkOnlyClips);
        // Without the Ork rig the Ork scan finds nothing; clips both rigs name are the marine's.
        Assert.Empty(alone.ReloadTags(alone.File("shoota")!, orkRig: true));
        var shared = SoundTriggers.Build(docs, none, marine, null, [Graph("SM_RNG_Bolter_Reload_01", "actn_x")]);
        Assert.Empty(shared.OrkOnlyClips);
    }

    [Theory]
    [InlineData("BACK (Esc)", true)]
    [InlineData("CANCEL (Esc)", true)]
    [InlineData("RESUME (Esc)", true)]
    [InlineData("Cancel", true)]
    [InlineData("  close", true)]
    [InlineData("JOIN", false)]
    [InlineData("CUSTOMIZE", false)]
    [InlineData("Use the chapter's colour", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Buttons_that_leave_a_screen_play_back(string? text, bool back)
    {
        Assert.Equal(back, UiSoundRules.IsBack(text));
        Assert.Equal(back ? UiSoundRules.Back : UiSoundRules.Select, UiSoundRules.ForPress(text));
    }

    [Fact]
    public void The_menu_events_are_the_ones_the_front_end_movies_post()
    {
        Assert.Equal("Play_Menu_Move", UiSoundRules.Move);
        Assert.Equal("Play_Menu_Select", UiSoundRules.Select);
        Assert.Equal("Play_Menu_Back", UiSoundRules.Back);
        Assert.Equal(new[] { "init", "in_game_default" }, AudioCatalog.UiPackages);
    }
}
