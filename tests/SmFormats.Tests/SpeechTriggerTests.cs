using SmContent;
using SmFormats.Audio;
using SmFormats.Baf;
using static SmFormats.Audio.SyntheticWwiseWriter;

namespace SmFormats.Tests;

/// <summary>M33: the speech commands of the blueprint and wargear files, the class → speech character table and the dialogue lines, on synthetic data only.</summary>
public class SpeechTriggerTests
{
    static (string, object) Exertion(string type) =>
        ("post_speech_exertion_event", new (string, object)[] { ("$ref", "action_commands\\post_speech_exertion_event"), ("1_character", "autodetect"), ("2_exertion_type", type) });

    static (string, object) Action(string type, string state) =>
        ("post_speech_action_event", new (string, object)[]
        {
            ("$ref", "action_commands\\post_speech_action_event"), ("1_character", "autodetect"), ("2_action_type", type), ("3_action_state", state), ("4_threat_level", "autodetect"),
        });

    [Fact]
    public void Speech_commands_are_collected_with_their_trigger_ability_and_arguments()
    {
        var doc = BafDocument.Read(SyntheticBafWriter.Build(
            ("ability_data", new (string, object)[]
            {
                ("ability_definitions", new (string, object)[]
                {
                    ("ability_melee_x1", new (string, object)[] { ("start_actions", new (string, object)[] { ("start_audio", new (string, object)[] { ("actions", new (string, object)[] { Exertion("effort_big_pull") }) }) }) }),
                    ("ability_melee_x2", new (string, object)[] { ("start_actions", new (string, object)[] { ("start_audio", new (string, object)[] { ("actions", new (string, object)[] { Exertion("effort_short_stab") }) }) }) }),
                    ("ability_reload", new (string, object)[] { ("additional_reload_actions", new (string, object)[] { ("commands", new (string, object)[] { Action("", "start") }) }) }),
                }),
            }),
            ("action_data", new (string, object)[]
            {
                ("triggered_actions", new (string, object)[]
                {
                    ("swing_x1", new (string, object)[] { ("commands", new (string, object)[] { Exertion("effort_short_stab") }) }),
                    ("evt_other", new (string, object)[] { ("commands", new (string, object)[]
                    {
                        ("actions_by_upgrade", new (string, object)[] { ("entry", new (string, object)[]
                        {
                            ("required_upgrade_name", new (string, object)[] { ("required_list", new (string, object)[] { ("1", "perk_y") }) }),
                            ("action", new (string, object)[] { ("commands", new (string, object)[] { Exertion("roar") }) }),
                        }) }),
                    }) }),
                }),
            })));
        var triggers = SoundTriggers.Build(new Dictionary<string, BafDocument> { ["wargear"] = doc }, new Dictionary<string, string>(), null);
        var file = triggers.File("wargear")!;
        Assert.Equal(5, file.Speech.Count);
        Assert.Equal("effort_big_pull", file.AbilityStart("ability_melee_x1")!.Type);
        Assert.Equal("effort_short_stab", file.AbilityStart("ABILITY_MELEE_X2")!.Type);
        Assert.Null(file.AbilityStart("ability_melee_x3"));
        var tag = Assert.Single(file.SpeechTag("SWING_X1"));
        Assert.Equal((SpeechKind.Exertion, "autodetect", "effort_short_stab"), (tag.Kind, tag.Character, tag.Type));
        Assert.Empty(file.SpeechTag("evt_other"));                                     // the perk's variant is not the default
        Assert.Equal(new[] { "perk_y" }, file.Speech.Single(b => b.Type == "roar").RequiredUpgrades);
        var reload = Assert.Single(file.Speech, b => b.Kind == SpeechKind.Action);
        Assert.Equal(("", "start", "autodetect"), (reload.Type, reload.State, reload.Threat));
        Assert.Empty(file.Bindings);                                                   // speech is not a sound binding
    }

    [Fact]
    public void Speech_characters_come_from_the_class_table()
    {
        var global = BafDocument.Read(SyntheticBafWriter.Build(
            ("extensions", new (string, object)[]
            {
                ("multiplayer_settings", new (string, object)[]
                {
                    ("ingame_progression", new (string, object)[]
                    {
                        ("classes", new (string, object)[]
                        {
                            ("sm - tactical", new (string, object)[] { ("speech_arg_character", "tactical_marine") }),
                            ("csm - havoc", new (string, object)[] { ("speech_arg_character", "chaos_havoc") }),
                            ("sm - none", new (string, object)[] { ("display_icon", "x") }),
                        }),
                    }),
                }),
            })));
        var map = SoundTriggers.ReadSpeechCharacters(global);
        Assert.Equal(2, map.Count);
        Assert.Equal("tactical_marine", map["SM - Tactical"]);
        Assert.Equal("chaos_havoc", map["csm - havoc"]);
    }

    [Fact]
    public void Speech_lines_resolve_through_the_speech_banks_dialogue_events()
    {
        uint ch = Fnv.Hash32("character"), kind = Fnv.Hash32("exertion_type");
        uint tactical = Fnv.Hash32("tactical_marine"), stab = Fnv.Hash32("effort_short_stab");
        var objects = new List<Obj>
        {
            Dialogue(Fnv.Hash32("speech_exertion"), [ch, kind], ([tactical, stab], 0x501)),
            Random(0x501, new Node(0), [0x502]), Sound(0x502, 101, new Node(0x501)),
        };
        var media = new[] { new Item(101, [1, 2, 3, 4]) };
        var lib = new WwiseLibrary();
        lib.AddPackage(AkpkPackage.Read(new MemoryStream(Package([new Item(1, Bank(1, "Speech_Synthetic", media, objects))], []))), "synthetic");
        using var c = new AudioCatalog(lib, null, null, 3);
        Assert.True(c.HasSpeech);
        var line = c.PostSpeech(SpeechKind.Exertion, "tactical_marine", "effort_short_stab", "", "", SoundSwitches.None, emitter: 4);
        Assert.True(line.Found);
        Assert.Equal(101u, Assert.Single(line.Voices).WemId);
        var none = c.PostSpeech(SpeechKind.Exertion, "tactical_marine", "roar", "", "", SoundSwitches.None);
        Assert.True(none.Found);
        Assert.Empty(none.Voices);                                                      // no line for that exertion
        Assert.False(c.PostSpeech(SpeechKind.Action, "tactical_marine", "", "start", "high", SoundSwitches.None).Found);   // no action event in this bank
    }
}
