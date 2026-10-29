using SmContent;
using SmFormats.Audio;
using SmFormats.Baf;
using static SmFormats.Audio.SyntheticWwiseWriter;

namespace SmFormats.Tests;

/// <summary>
/// M26b: the sound catalogue (event â†’ voices: switches, random/sequence with avoid-repeat, layers, continuous containers,
/// volume/pitch summing, delays, positioning and attenuation, stops, states), the attenuation reader and the BAF sound
/// triggers, on synthetic banks and files only (no original bytes, no real audio).
/// </summary>
public class AudioCatalogTests
{
    const uint Bank1 = 0xB1;
    const uint Wem1 = 101, Wem2 = 102, Wem3 = 103, Wem4 = 104, WemMetal = 201, WemStone = 202, WemLocal = 301, WemOther = 302;
    static readonly uint Metal = Fnv.Hash32("metal"), Stone = Fnv.Hash32("stone"), Default = Fnv.Hash32("default");

    /// <summary>A catalogue over one synthetic package; every listed wem is embedded (4 placeholder bytes, not audio).</summary>
    static AudioCatalog Catalog(IReadOnlyList<Obj> objects, IEnumerable<uint>? wems = null, int seed = 7, VorbisCodebookLibrary? codebooks = null,
        string? disk = null, IReadOnlyList<Item>? media = null)
    {
        media ??= (wems ?? [Wem1, Wem2, Wem3, Wem4, WemMetal, WemStone, WemLocal, WemOther]).Select(w => new Item(w, [1, 2, 3, 4])).ToList();
        var lib = new WwiseLibrary();
        lib.AddPackage(AkpkPackage.Read(new MemoryStream(Package([new Item(Bank1, Bank(Bank1, "Synthetic", media, objects))], []))), "synthetic");
        return new AudioCatalog(lib, codebooks, disk, seed);
    }

    [Fact]
    public void Switch_containers_follow_the_emitters_value_and_fall_back_to_the_default()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            Switch(0x10, new Node(0), AudioCatalog.MaterialType, Stone, [(Metal, [0x11u]), (Stone, [0x12u])]),
            Sound(0x11, WemMetal, new Node(0x10)), Sound(0x12, WemStone, new Node(0x10)),
            // The player group: the local case and "default".
            Event(0xE2, 0xA2), Play(0xA2, 0x20, 0, Bank1),
            Switch(0x20, new Node(0), AudioCatalog.PlayerGroup, Default, [(AudioCatalog.LocalPlayerValue, [0x21u]), (Default, [0x22u])]),
            Sound(0x21, WemLocal, new Node(0x20)), Sound(0x22, WemOther, new Node(0x20)),
        };
        using var c = Catalog(objects);
        Assert.Equal(WemMetal, Assert.Single(c.Post(0xE1, new SoundSwitches().Set("material_type", "metal")).Voices).WemId);
        Assert.Equal(WemStone, Assert.Single(c.Post(0xE1, new SoundSwitches().Set("material_type", "stone")).Voices).WemId);
        Assert.Equal(WemStone, Assert.Single(c.Post(0xE1, new SoundSwitches().Set("material_type", "sand")).Voices).WemId);   // no case: default
        Assert.Equal(WemStone, Assert.Single(c.Post(0xE1, SoundSwitches.None).Voices).WemId);
        Assert.Equal(WemLocal, Assert.Single(c.Post(0xE2, new SoundSwitches().Set(AudioCatalog.PlayerGroup, AudioCatalog.LocalPlayerValue)).Voices).WemId);
        Assert.Equal(WemOther, Assert.Single(c.Post(0xE2, SoundSwitches.None).Voices).WemId);
        var missing = c.Post(0xEEEE, SoundSwitches.None);
        Assert.False(missing.Found);
        Assert.Empty(missing.Voices);
    }

    [Fact]
    public void Layers_play_every_child_and_actions_keep_their_delays()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1, 0xA2, 0xA3), Play(0xA1, 0x10, 0, Bank1), Play(0xA2, 0x13, 200, Bank1), Play(0xA3, 0x13, 100, Bank1, delayMin: 0, delayMax: 50),
            Layer(0x10, new Node(0), [0x11, 0x12]), Sound(0x11, Wem1, new Node(0x10)), Sound(0x12, Wem2, new Node(0x10)),
            Sound(0x13, Wem3, new Node(0)),
        };
        using var c = Catalog(objects);
        for (int i = 0; i < 20; i++)
        {
            var play = c.Post(0xE1, SoundSwitches.None);
            Assert.Equal(4, play.Voices.Count);
            Assert.Equal(new uint[] { Wem1, Wem2, Wem3, Wem3 }, play.Voices.Select(v => v.WemId));
            Assert.Equal(0f, play.Voices[0].Delay);
            Assert.Equal(0.2f, play.Voices[2].Delay, 4);
            Assert.InRange(play.Voices[3].Delay, 0.1f, 0.15f);              // 100 ms + uniform(0, 50) ms (interpretation)
            Assert.Equal(new uint[] { 0x10, 0x11 }, play.Voices[0].Path);
        }
    }

    [Fact]
    public void Random_containers_avoid_their_last_picks()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            Random(0x10, new Node(0), [0x11, 0x12, 0x13, 0x14], avoidRepeat: 3),
            Sound(0x11, Wem1, new Node(0x10)), Sound(0x12, Wem2, new Node(0x10)), Sound(0x13, Wem3, new Node(0x10)), Sound(0x14, Wem4, new Node(0x10)),
            Event(0xE2, 0xA2), Play(0xA2, 0x20, 0, Bank1),
            Random(0x20, new Node(0), [0x21, 0x22], avoidRepeat: 1),
            Sound(0x21, Wem1, new Node(0x20)), Sound(0x22, Wem2, new Node(0x20)),
        };
        using var c = Catalog(objects);
        var picks = Enumerable.Range(0, 200).Select(_ => Assert.Single(c.Post(0xE1, SoundSwitches.None).Voices).WemId).ToList();
        for (int i = 3; i < picks.Count; i++) Assert.DoesNotContain(picks[i], picks.Skip(i - 3).Take(3));   // 4 children, avoid 3: a fixed rotation
        Assert.Equal(4, picks.Distinct().Count());
        var two = Enumerable.Range(0, 50).Select(_ => Assert.Single(c.Post(0xE2, SoundSwitches.None).Voices).WemId).ToList();
        for (int i = 1; i < two.Count; i++) Assert.NotEqual(two[i - 1], two[i]);

        // Histories are per emitter for a container that is not global.
        var a = c.Post(0xE2, SoundSwitches.None, emitter: 1).Voices[0].WemId;
        var b = c.Post(0xE2, SoundSwitches.None, emitter: 2).Voices[0].WemId;
        Assert.NotEqual(a, c.Post(0xE2, SoundSwitches.None, emitter: 1).Voices[0].WemId);
        Assert.NotEqual(b, c.Post(0xE2, SoundSwitches.None, emitter: 2).Voices[0].WemId);

        // A finished one-shot emitter's histories are dropped (they would otherwise grow with every impact).
        int before = c.ContainerHistories;
        c.ForgetEmitter(1);
        Assert.Equal(before - 1, c.ContainerHistories);
        c.ForgetEmitter(0);   // the shared emitter keeps its history
        Assert.Equal(before - 1, c.ContainerHistories);
    }

    [Fact]
    public void Sequence_shuffle_and_weights_pick_as_documented()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            Random(0x10, new Node(0), [0x11, 0x12, 0x13], sequence: true),
            Sound(0x11, Wem1, new Node(0x10)), Sound(0x12, Wem2, new Node(0x10)), Sound(0x13, Wem3, new Node(0x10)),
            Event(0xE2, 0xA2), Play(0xA2, 0x20, 0, Bank1),
            Random(0x20, new Node(0), [0x21, 0x22], avoidRepeat: 0, weights: [0, 100]),
            Sound(0x21, Wem1, new Node(0x20)), Sound(0x22, Wem2, new Node(0x20)),
            Event(0xE3, 0xA3), Play(0xA3, 0x30, 0, Bank1),
            Random(0x30, new Node(0), [0x31, 0x32, 0x33], avoidRepeat: 0, shuffle: true),
            Sound(0x31, Wem1, new Node(0x30)), Sound(0x32, Wem2, new Node(0x30)), Sound(0x33, Wem3, new Node(0x30)),
        };
        using var c = Catalog(objects);
        Assert.Equal(new uint[] { Wem1, Wem2, Wem3, Wem1, Wem2 }, Enumerable.Range(0, 5).Select(_ => c.Post(0xE1, SoundSwitches.None).Voices[0].WemId));
        Assert.All(Enumerable.Range(0, 30), _ => Assert.Equal(Wem2, c.Post(0xE2, SoundSwitches.None).Voices[0].WemId));
        for (int round = 0; round < 10; round++)
        {
            var bag = Enumerable.Range(0, 3).Select(_ => c.Post(0xE3, SoundSwitches.None).Voices[0].WemId).ToList();
            Assert.Equal(3, bag.Distinct().Count());                          // each child once per round
        }
    }

    [Fact]
    public void Volume_and_pitch_sum_up_the_parent_chain_with_one_draw_per_node()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            ActorMixer(0x01, new Node(0, Volume: -2f, Pitch: 100f), [0x10]),
            Layer(0x10, new Node(0x01, Volume: -3f, VolumeMin: -6f, VolumeMax: 0f, Pitch: -50f, PitchMin: -10f, PitchMax: 10f), [0x11, 0x12]),
            Sound(0x11, Wem1, new Node(0x10, Volume: -1f)), Sound(0x12, Wem2, new Node(0x10, Volume: -4f, Pitch: 1200f)),
        };
        using var c = Catalog(objects);
        for (int i = 0; i < 50; i++)
        {
            var v = c.Post(0xE1, SoundSwitches.None).Voices;
            Assert.InRange(v[0].VolumeDb, -2f - 3f - 6f - 1f, -2f - 3f - 1f);
            Assert.InRange(v[0].PitchCents, 100f - 50f - 10f, 100f - 50f + 10f);
            // The layer's draw is shared by its children: the difference is exactly the sounds' own values.
            Assert.Equal(-3f, v[1].VolumeDb - v[0].VolumeDb, 4);
            Assert.Equal(1200f, v[1].PitchCents - v[0].PitchCents, 3);
            Assert.Equal(2f, v[1].PitchScale / v[0].PitchScale, 4);
        }
    }

    [Fact]
    public void Output_bus_volumes_add_up_to_the_master_bus()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            Layer(0x10, new Node(0, Volume: -1f, Bus: 0xB3), [0x11]), Sound(0x11, Wem1, new Node(0x10)),
            Bus(0xB1, 0, -7f), Bus(0xB2, 0xB1, -3f), Bus(0xB3, 0xB2, -12.5f),
        };
        using var c = Catalog(objects);
        var v = Assert.Single(c.Post(0xE1, SoundSwitches.None).Voices);
        Assert.Equal(-22.5f, v.BusVolumeDb, 4);                            // 12.5 + 3 + the master bus (no parent) 7
        Assert.Equal(-23.5f, v.VolumeDb, 4);
        Assert.Equal(-7f, c.BusVolume(0xDEAD), 4);                          // an unknown bus goes to the master
        Assert.Equal(new uint[] { 0xB3, 0xB2, 0xB1 }, v.Buses.Select(b => b.Id));
        Assert.Equal(-7f, c.MasterBusVolume, 4);
    }

    [Fact]
    public void Positioning_and_attenuation_come_from_the_nearest_overriding_node()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            Random(0x10, new Node(0, AttenuationId: 0xAA), [0x11]), Sound(0x11, Wem1, new Node(0x10)),
            Event(0xE2, 0xA2), Play(0xA2, 0x21, 0, Bank1), Sound(0x21, Wem2, new Node(0)),
            Attenuation(0xAA, (0f, 0f, 4), (100f, -60f, 9), (200f, -96f, 4)),
        };
        using var c = Catalog(objects);
        var v = Assert.Single(c.Post(0xE1, SoundSwitches.None).Voices);
        Assert.True(v.Is3D);
        Assert.NotNull(v.Attenuation);
        Assert.Equal(200f, v.Attenuation!.MaxDistance);
        Assert.Equal(0f, v.Attenuation.VolumeAt(-5f));
        Assert.Equal(-30f, v.Attenuation.VolumeAt(50f), 3);               // linear segment
        Assert.Equal(-60f, v.Attenuation.VolumeAt(150f), 3);              // constant segment (interpolation 9) holds its start
        Assert.Equal(-96f, v.Attenuation.VolumeAt(500f), 3);              // beyond the end: the last value
        var flat = Assert.Single(c.Post(0xE2, SoundSwitches.None).Voices);
        Assert.False(flat.Is3D);                                           // no node overrides positioning: 2D
        Assert.Null(flat.Attenuation);
    }

    [Fact]
    public void Attenuation_reader_refuses_truncated_or_unsorted_curves()
    {
        var good = Attenuation(0x1, (0f, 0f, 4), (10f, -6f, 4));
        Assert.NotNull(WwiseAttenuation.TryRead(new HircRaw(HircType.Attenuation, 1, good.Payload), out var none));
        Assert.Null(none);
        Assert.Null(WwiseAttenuation.TryRead(new HircRaw(HircType.Attenuation, 1, good.Payload[..12]), out var e1));
        Assert.Contains("truncated", e1);
        var unsorted = Attenuation(0x2, (10f, 0f, 4), (5f, -6f, 4));
        Assert.Null(WwiseAttenuation.TryRead(new HircRaw(HircType.Attenuation, 2, unsorted.Payload), out var e2));
        Assert.Contains("sorted", e2);
        Assert.Null(WwiseAttenuation.TryRead(new HircRaw(HircType.Sound, 3, good.Payload), out _));
    }

    [Fact]
    public void Stop_and_set_state_actions_are_reported_and_applied()
    {
        uint group = 0x6F0F7F7C, armor = Fnv.Hash32("armor"), broken = Fnv.Hash32("broken");
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1, 0xA2), Stop(0xA1, 0x10), Play(0xA2, 0x10, 0, Bank1),
            Switch(0x10, new Node(0), group, armor, [(armor, [0x11u]), (broken, [0x12u])], groupType: 1),
            Sound(0x11, Wem1, new Node(0x10)), Sound(0x12, Wem2, new Node(0x10)),
            Event(0xE2, 0xA3), SetState(0xA3, group, broken),
        };
        using var c = Catalog(objects);
        var first = c.Post(0xE1, SoundSwitches.None);
        Assert.Equal((0x10u, 0f), Assert.Single(first.Stops));
        Assert.Equal(Wem1, Assert.Single(first.Voices).WemId);             // state unset: the default
        Assert.Empty(c.Post(0xE2, SoundSwitches.None).Voices);
        Assert.Equal(broken, c.State(group));
        Assert.Equal(Wem2, Assert.Single(c.Post(0xE1, SoundSwitches.None).Voices).WemId);
    }

    [Fact]
    public void Continuous_containers_ask_to_be_continued_at_their_trigger_rate()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            Random(0x10, new Node(0), [0x11, 0x12], avoidRepeat: 1, continuous: true, transitionMode: 5, transitionMs: 4500, loop: 0),
            Sound(0x11, Wem1, new Node(0x10)), Sound(0x12, Wem2, new Node(0x10)),
            Event(0xE2, 0xA2), Play(0xA2, 0x20, 0, Bank1),
            Random(0x20, new Node(0), [0x21], continuous: true, transitionMode: 5, transitionMs: 1000, loop: 2),
            Sound(0x21, Wem3, new Node(0x20)),
        };
        using var c = Catalog(objects);
        var play = c.Post(0xE1, SoundSwitches.None, emitter: 9);
        var first = Assert.Single(play.Voices).WemId;
        var repeat = Assert.Single(play.Repeats);
        Assert.Equal(4.5f, repeat.After, 3);
        Assert.Equal(-1, repeat.PlaysLeft);
        Assert.Equal(9ul, repeat.Emitter);
        var next = c.Continue(repeat, SoundSwitches.None);
        Assert.NotEqual(first, Assert.Single(next.Voices).WemId);          // avoid-repeat across continuations
        Assert.Single(next.Repeats);

        // Loop 2 over one child: two plays in all, so one continuation and then none.
        var finite = c.Post(0xE2, SoundSwitches.None);
        var r = Assert.Single(finite.Repeats);
        Assert.Equal(0, r.PlaysLeft);
        Assert.Empty(c.Continue(r, SoundSwitches.None).Repeats);
    }

    [Fact]
    public void Sounds_without_media_are_counted_not_played()
    {
        var objects = new List<Obj> { Event(0xE1, 0xA1), Play(0xA1, 0x11, 0, Bank1), Sound(0x11, 999, new Node(0)) };
        using var c = Catalog(objects, wems: [Wem1]);
        var play = c.Post(0xE1, SoundSwitches.None);
        Assert.Empty(play.Voices);
        Assert.Equal(1, play.MissingMedia);
        Assert.False(c.HasMedia(999));
        Assert.Equal(new uint[] { 999 }, c.WemsOf(0xE1));
    }

    [Fact]
    public void Ogg_conversion_is_cached_in_memory_and_on_disk()
    {
        var wem = Wem(1, 22050, [false, true, true, false], 1500, inlineCodebook: false);
        var objects = new List<Obj> { Event(0xE1, 0xA1), Play(0xA1, 0x11, 0, Bank1), Sound(0x11, Wem1, new Node(0)) };
        string disk = Path.Combine(Path.GetTempPath(), "sm-reimpl-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var c = Catalog(objects, codebooks: new VorbisCodebookLibrary(CodebookLibrary()), disk: disk, media: [new Item(Wem1, wem)]))
            {
                var ogg = c.Ogg(Wem1, out _);
                Assert.NotNull(ogg);
                Assert.True(ogg!.AsSpan().StartsWith("OggS"u8));
                Assert.Same(ogg, c.Ogg(Wem1, out _));
                Assert.Equal((1, 1), (c.Decoded, c.MemoryHits));
                Assert.True(File.Exists(Path.Combine(disk, $"{Wem1}.ogg")));
                Assert.Equal(1500 / 22050f, c.Duration(Wem1), 4);
                Assert.Null(c.Ogg(12345, out _));
                Assert.Equal(1, c.DecodeFailures);
            }
            using (var again = Catalog(objects, codebooks: null, disk: disk, media: [new Item(Wem1, wem)]))
            {
                Assert.NotNull(again.Ogg(Wem1, out _));                       // from the disk cache, no codebooks needed
                Assert.Equal((0, 1), (again.Decoded, again.DiskHits));
            }
        }
        finally { if (Directory.Exists(disk)) Directory.Delete(disk, recursive: true); }
    }

    [Fact]
    public void Triggered_actions_without_a_sound_are_known_as_keys()
    {
        // Shaped like the jump pack's actn_take_off (area effects, an effect, a rumble) and a melee weapon's antic_y3 whose
        // default upgrade entry (empty required list) holds a sound.
        (string, object) Empty(string name) => (name, new (string, object)[] { ("$ref", "action_commands\\" + name) });
        var doc = BafDocument.Read(SyntheticBafWriter.Build(
            ("action_data", new (string, object)[]
            {
                ("triggered_actions", new (string, object)[]
                {
                    ("actn_take_off", new (string, object)[] { ("commands", new (string, object)[] { Empty("area_of_effect"), Empty("play_fx"), Empty("controller_rumble") }) }),
                    ("antic_y3", new (string, object)[]
                    {
                        ("commands", new (string, object)[]
                        {
                            ("actions_by_upgrade", new (string, object)[]
                            {
                                ("actions", new (string, object)[]
                                {
                                    ("entry", new (string, object)[]
                                    {
                                        ("required_upgrade_name", new (string, object)[] { ("required_list", new (string, object)[] { }) }),
                                        ("action", new (string, object)[] { ("commands", new (string, object)[]
                                        {
                                            ("post_sound_event", new (string, object)[] { ("$ref", "action_commands\\post_sound_event"), ("start_event_name", "play_antic") }),
                                        }) }),
                                    }),
                                }),
                            }),
                        }),
                    }),
                }),
            })));
        Assert.Equal(new[] { "actn_take_off", "antic_y3" }, SoundTriggers.TriggeredActionKeys(doc.Root));
        var triggers = SoundTriggers.Build(new Dictionary<string, BafDocument> { ["pack"] = doc }, new Dictionary<string, string>(), null);
        var file = triggers.File("pack")!;
        Assert.Empty(file.Tag("ACTN_Take_Off"));                            // no sound in its commands
        Assert.Contains("ACTN_Take_Off", file.TriggeredActions);           // but the file reacts to the tag
        Assert.DoesNotContain("fx_jump", file.TriggeredActions);
        Assert.Equal("play_antic", Assert.Single(file.Tag("ANTIC_Y3")).StartEvent);   // an empty required list is the default variant
    }

    [Fact]
    public void Sound_bindings_name_their_trigger_flags_upgrades_and_parameters()
    {
        (string, object) Post(string start, bool terrain = false, bool footstep = false, bool onlyPlayer = false, float delay = 0f, string stop = "") =>
            ("post_sound_event", new (string, object)[]
            {
                ("$ref", "action_commands\\post_sound_event"), ("start_event_name", start), ("stop_event_name", stop), ("trigger_point", "self"),
                ("use_terrain_material", terrain), ("time_delay", delay), ("is_footstep_sound", footstep), ("apply_only_if_on_player", onlyPlayer),
            });
        var doc = BafDocument.Read(SyntheticBafWriter.Build(
            ("extensions", new (string, object)[]
            {
                ("entity_actions_ext", new (string, object)[]
                {
                    ("triggered_actions", new (string, object)[]
                    {
                        ("evt_left_foot", new (string, object)[] { ("commands", new (string, object)[] { Post("play_steps", footstep: true) }) }),
                    }),
                }),
                ("health_ext", new (string, object)[]
                {
                    ("death_action", new (string, object)[] { ("commands", new (string, object)[] { Post("play_death"), Post("play_late", onlyPlayer: true, delay: 500f) }) }),
                }),
            }),
            ("ability_data", new (string, object)[]
            {
                ("ability_definitions", new (string, object)[]
                {
                    ("ability_ranged_primary", new (string, object)[]
                    {
                        ("delivery", new (string, object)[]
                        {
                            ("fire_audio", new (string, object)[]
                            {
                                ("actions", new (string, object)[] { Post("play_fire") }),
                                ("actions_by_upgrade", new (string, object)[]
                                {
                                    ("entry", new (string, object)[]
                                    {
                                        ("required_upgrade_name", new (string, object)[] { ("required_list", new (string, object)[] { ("1", "perk_x") }) }),
                                        ("action", new (string, object)[] { ("commands", new (string, object)[] { Post("play_perk") }) }),
                                    }),
                                }),
                            }),
                        }),
                    }),
                    ("ability_ranged_secondary", new (string, object)[]
                    {
                        ("charge_states", new (string, object)[]
                        {
                            ("2.charge_heavy", new (string, object)[] { ("delivery", new (string, object)[] { ("fire_audio", new (string, object)[] { ("actions", new (string, object)[] { Post("play_charged", stop: "stop_charge") }) }) }) }),
                        }),
                    }),
                }),
            }),
            ("jump_info", new (string, object)[]
            {
                ("action_land_regular_by_height", new (string, object)[]
                {
                    ("landing_action_entry", new (string, object)[] { ("distance_from_apex_to_land", 15f), ("action", new (string, object)[] { ("commands", new (string, object)[] { Post("play_land", terrain: true) }) }) }),
                }),
            })));
        var file = new SoundFile("synthetic", SoundBinding.Collect(doc.Root));
        Assert.Equal(7, file.Bindings.Count);
        var step = Assert.Single(file.Tag("EVT_Left_Foot"));                 // tags match case-insensitively
        Assert.True(step.IsFootstep);
        Assert.Equal("play_steps", step.StartEvent);
        Assert.Equal(new[] { "play_death", "play_late" }, file.Of("death_action").Select(b => b.StartEvent));
        Assert.Equal(0.5f, file.Of("death_action")[1].DelaySeconds, 4);
        Assert.True(file.Of("death_action")[1].OnlyIfOnPlayer);
        Assert.Empty(file.Tag("death_action"));                              // not a behaviour tag
        Assert.Equal("play_fire", Assert.Single(file.Fire(null)).StartEvent);   // the perk's variant is not the default
        Assert.Equal(new[] { "perk_x" }, file.Bindings.Single(b => b.StartEvent == "play_perk").RequiredUpgrades);
        var charged = Assert.Single(file.Fire("2.charge_heavy"));
        Assert.Equal(("ability_ranged_secondary", "stop_charge"), (charged.Ability, charged.StopEvent));
        var land = Assert.Single(file.Of("action_land_regular_by_height"));
        Assert.True(land.UseTerrainMaterial);
        Assert.Equal(15f, land.Parameters["distance_from_apex_to_land"]);

        var triggers = SoundTriggers.Build(new Dictionary<string, BafDocument> { ["synthetic"] = doc },
            new Dictionary<string, string> { ["material_effect_container\\weapon_hits\\bolter"] = "play_hits" }, null);
        Assert.Equal("play_hits", triggers.ContainerSound("material_effect_container\\weapon_hits\\bolter"));
        Assert.Contains("stop_charge", triggers.EventNames());
        Assert.Null(triggers.ContainerSound(null));
    }

    [Fact]
    public void A_music_segment_plays_its_tracks_sources_once_as_2d_voices_with_the_segments_volume()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            MusicSegment(0x10, new Node(0, Volume: -6f), [0x11, 0x12]),
            MusicTrack(0x11, Wem1, HircStreamType.Bank), MusicTrack(0x12, Wem2, HircStreamType.Bank),
        };
        using var c = Catalog(objects);
        var play = c.Post(0xE1, SoundSwitches.None);
        Assert.Equal(new uint[] { Wem1, Wem2 }, play.Voices.Select(v => v.WemId));
        Assert.All(play.Voices, v => Assert.Equal((-6f, false, (short)1), (v.VolumeDb, v.Is3D, v.LoopCount)));
        Assert.Equal(0, play.MissingMedia);
        // A track whose media is not in any loaded package counts as missing media, not as a failure.
        using var missing = Catalog([Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1), MusicSegment(0x10, new Node(0), [0x11]), MusicTrack(0x11, 999, HircStreamType.Bank)]);
        var none = missing.Post(0xE1, SoundSwitches.None);
        Assert.Empty(none.Voices);
        Assert.Equal(1, none.MissingMedia);
        // The reader keeps a segment it cannot read raw, without counting a parse failure.
        var raw = SoundBank.Parse(HircType.MusicSegment, 7, new byte[] { 1, 2, 3 }, false, out var error);
        Assert.IsType<HircRaw>(raw);
        Assert.Null(error);
        var track = Assert.IsType<HircMusicTrack>(SoundBank.Parse(HircType.MusicTrack, 8, objects[4].Payload, false, out var trackError));
        Assert.Null(trackError);
        Assert.Equal(Wem2, Assert.Single(track.Sources).SourceId);
    }

    [Fact]
    public void A_music_track_with_a_streamed_source_finds_its_media_in_the_packages_streams()
    {
        const uint StreamedWem = 777;
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            MusicSegment(0x10, new Node(0), [0x11]), MusicTrack(0x11, StreamedWem, HircStreamType.Streamed, fileId: StreamedWem),
        };
        var lib = new WwiseLibrary();
        lib.AddPackage(AkpkPackage.Read(new MemoryStream(Package([new Item(Bank1, Bank(Bank1, "Synthetic", [], objects))], [new Item(StreamedWem, [1, 2, 3, 4, 5, 6, 7, 8])]))), "synthetic");
        using var c = new AudioCatalog(lib, null, null, 7);
        var play = c.Post(0xE1, SoundSwitches.None);
        var voice = Assert.Single(play.Voices);
        Assert.Equal((StreamedWem, false, 0), (voice.WemId, voice.Is3D, play.MissingMedia));
        // Without the stream the same track is missing media, not a failure.
        var bare = new WwiseLibrary();
        bare.AddPackage(AkpkPackage.Read(new MemoryStream(Package([new Item(Bank1, Bank(Bank1, "Synthetic", [], objects))], []))), "synthetic");
        using var none = new AudioCatalog(bare, null, null, 7);
        Assert.Equal((0, 1), (none.Post(0xE1, SoundSwitches.None).Voices.Count, none.Post(0xE1, SoundSwitches.None).MissingMedia));
    }

    [Fact]
    public void Music_records_with_oversized_counts_stay_raw_without_a_failure_or_a_huge_allocation()
    {
        // A track that claims 4 billion sources.
        var track = new byte[64];
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(track, 0);
        var rawTrack = SoundBank.Parse(HircType.MusicTrack, 1, track, false, out var trackError);
        Assert.IsType<HircRaw>(rawTrack);
        Assert.Null(trackError);
        // A segment whose child count is 4 billion (the count sits after the node base, before the 24-byte tail).
        var segment = MusicSegment(2, new Node(0), []).Payload.ToArray();
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(segment, segment.Length - 28);
        var rawSegment = SoundBank.Parse(HircType.MusicSegment, 2, segment, false, out var segmentError);
        Assert.IsType<HircRaw>(rawSegment);
        Assert.Null(segmentError);
        // Truncated records too.
        foreach (var type in new[] { HircType.MusicTrack, HircType.MusicSegment })
            foreach (int length in new[] { 0, 1, 3 })
                Assert.IsType<HircRaw>(SoundBank.Parse(type, 3, new byte[length], false, out _));
    }
}
