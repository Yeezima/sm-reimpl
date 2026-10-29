using SmContent;
using SmFormats.Audio;
using static SmFormats.Audio.SyntheticWwiseWriter;

namespace SmFormats.Tests;

/// <summary>
/// M33: the sound follow-ups on synthetic banks only (no original bytes): the master and second bus volumes, node and
/// attenuation low-pass values, RTPC curves, Set/Reset pitch, volume, LPF and mute actions with fades (<see cref="SoundModifiers"/>),
/// SetSwitch, and the dialogue event tree.
/// </summary>
public class AudioMixTests
{
    const uint Bank1 = 0xB1;
    const uint Wem1 = 101, Wem2 = 102;

    static AudioCatalog Catalog(IReadOnlyList<Obj> objects)
    {
        var media = new[] { Wem1, Wem2 }.Select(w => new Item(w, [1, 2, 3, 4])).ToList();
        var lib = new WwiseLibrary();
        lib.AddPackage(AkpkPackage.Read(new MemoryStream(Package([new Item(Bank1, Bank(Bank1, "Synthetic", media, objects))], []))), "synthetic");
        return new AudioCatalog(lib, null, null, 7);
    }

    static SoundVoice Voice(IReadOnlyList<Obj> objects, uint ev = 0xE1)
    {
        using var c = Catalog(objects);
        return Assert.Single(c.Post(ev, SoundSwitches.None).Voices);
    }

    [Fact]
    public void Bus_volumes_add_both_properties_and_the_master_counts()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x11, 0, Bank1), Sound(0x11, Wem1, new Node(0, Bus: 0xB3)),
            Event(0xE2, 0xA2), Play(0xA2, 0x12, 0, Bank1), Sound(0x12, Wem2, new Node(0)),        // no bus: the master alone
            Bus(0xB1, 0, 0f, busVolume: -7f), Bus(0xB2, 0xB1, 0f, busVolume: -9.3f), Bus(0xB3, 0xB2, -4f),
        };
        using var c = Catalog(objects);
        var v = Assert.Single(c.Post(0xE1, SoundSwitches.None).Voices);
        Assert.Equal(-20.3f, v.BusVolumeDb, 3);                                // -4 voice volume, -9.3 and -7 bus volumes
        Assert.Equal(new[] { 0xB3u, 0xB2u, 0xB1u }, v.Buses.Select(b => b.Id));
        Assert.Equal(-7f, Assert.Single(c.Post(0xE2, SoundSwitches.None).Voices).BusVolumeDb, 3);
        Assert.Equal(0xB1u, c.MasterBusId());
        Assert.Equal(-7f, c.MasterBusVolume, 3);
    }

    [Fact]
    public void Node_low_pass_adds_up_the_chain_with_its_draw_and_is_clamped()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            Random(0x10, new Node(0, Lpf: 30, LpfMin: 0, LpfMax: 0), [0x11]), Sound(0x11, Wem1, new Node(0x10, Lpf: 20)),
            Event(0xE2, 0xA2), Play(0xA2, 0x20, 0, Bank1),
            Random(0x20, new Node(0, Lpf: 90), [0x21]), Sound(0x21, Wem2, new Node(0x20, Lpf: 40)),
        };
        using var c = Catalog(objects);
        Assert.Equal(50f, Assert.Single(c.Post(0xE1, SoundSwitches.None).Voices).LpfBase, 3);
        Assert.Equal(100f, Assert.Single(c.Post(0xE2, SoundSwitches.None).Voices).LpfBase, 3);
    }

    [Fact]
    public void Rtpc_curves_are_read_and_applied_for_the_parameters_the_client_gives()
    {
        uint distance = VoiceMixer.Distance, speed = VoiceMixer.Speed;
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x11, 0, Bank1),
            Sound(0x11, Wem1, new Node(0, Volume: -2f, RtpcCurves:
            [
                new RtpcSpec(speed, 0, [(0f, -96.3f, 9), (3f, -96.3f, 4), (6f, 1f, 4), (100f, 0f, 4)]),
                new RtpcSpec(distance, 3, [(0f, 0f, 4), (100f, 40f, 4)]),
                new RtpcSpec(speed, 2, [(0f, -400f, 9), (2f, 0f, 4), (100f, 0f, 4)]),
                new RtpcSpec(speed, 1, [(0f, 55f, 4)]),                          // LFE: not applied
            ])),
        };
        using var c = Catalog(objects);
        var v = Assert.Single(c.Post(0xE1, SoundSwitches.None).Voices);
        var rtpcs = ((HircNode)c.Library.Find(0x11)!).Base.Rtpcs;
        Assert.Equal(4, rtpcs.Count);
        Assert.Equal((speed, 0u), (rtpcs[0].RtpcId, rtpcs[0].ParamId));
        Assert.Equal(1f, rtpcs[0].Evaluate(6f), 4);
        Assert.Equal(-47.65f, rtpcs[0].Evaluate(4.5f), 2);                      // between (3, -96.3) and (6, 1)

        // Without parameters only the data's own values; with speed 0 the footstep layer is silent; faster, it is there (curves as read: cap off).
        var none = VoiceMixer.Evaluate(v, null, 1, 0, null, null);
        Assert.Equal((-2f, 0f, 0f), (none.VolumeDb, none.PitchCents, none.Lpf));
        var slow = VoiceMixer.Evaluate(v, null, 1, 0, 50f, new Dictionary<uint, float> { [speed] = 0f }, RtpcVolumeCap.Off);
        Assert.Equal(-98.3f, slow.VolumeDb, 3);
        Assert.Equal(-400f, slow.PitchCents, 3);
        Assert.Equal(20f, slow.Lpf, 3);                                        // the distance RTPC at 50 m
        var fast = VoiceMixer.Evaluate(v, null, 1, 0, 100f, new Dictionary<uint, float> { [speed] = 8f }, RtpcVolumeCap.Off);
        Assert.Equal(-2f + (1f - 2f / 94f), fast.VolumeDb, 3);
        Assert.Equal(0f, fast.PitchCents, 3);
        Assert.Equal(40f, fast.Lpf, 3);
        // The default (peak): this curve peaks at +1 dB, so it is shifted down by 1; pitch and low-pass are not touched.
        var peak = VoiceMixer.Evaluate(v, null, 1, 0, 100f, new Dictionary<uint, float> { [speed] = 8f });
        Assert.Equal(-2f + (1f - 2f / 94f) - 1f, peak.VolumeDb, 3);
        Assert.Equal((0f, 40f), (peak.PitchCents, peak.Lpf));
    }

    [Theory]
    [InlineData(0f, 30.23f, 0f)]          // the local sole layer's switch at a run: no node goes above 0 dB
    [InlineData(-3f, 19.81f, 0f)]         // the thud's switch: lifted from -3 to 0, not to +16.8
    [InlineData(-12.3f, 89.48f, 0f)]      // the Ork grenade fuse at 0 m
    [InlineData(-6f, 2f, -4f)]            // under the ceiling: added as it is
    [InlineData(-4f, 1.08f, -2.92f)]
    [InlineData(-2f, -10f, -12f)]         // negative offsets are never touched
    [InlineData(0f, -28.12f, -28.12f)]
    [InlineData(1f, 5f, 1f)]              // an own value above the ceiling (none on the install) keeps itself
    public void A_positive_rtpc_volume_offset_lifts_a_node_at_most_to_0_dB(float own, float rtpc, float expected) =>
        Assert.Equal(expected, VoiceMixer.CapRtpcVolume(own, rtpc), 4);

    [Fact]
    public void The_rtpc_volume_cap_is_peak_normalised_by_default_and_the_other_modes_can_be_chosen()
    {
        // The footsteps' shape on synthetic data: a parent at -3 dB whose speed curve reaches +19.81 dB at 5 m/s, over a child at -4 dB, on a -32 dB bus.
        uint speed = VoiceMixer.Speed;
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            Random(0x10, new Node(0, Volume: -3f, Bus: 0xB2, RtpcCurves: [new RtpcSpec(speed, 0, [(0f, 0f, 4), (5f, 19.81f, 9), (100f, 19.81f, 4)])]), [0x20]),
            Random(0x20, new Node(0x10, Volume: -4f), [0x11]), Sound(0x11, Wem1, new Node(0x20)),
            Bus(0xB1, 0, 0f, busVolume: -7f), Bus(0xB2, 0xB1, -25f),
        };
        var v = Voice(objects);
        var run = new Dictionary<uint, float> { [speed] = 6.5f };
        Assert.Equal(-32f - 7f, VoiceMixer.Evaluate(v, null, 1, 0, null, run).VolumeDb, 3);                            // default peak: own volumes at the curve's top
        Assert.Equal(-32f - 7f, VoiceMixer.Evaluate(v, null, 1, 0, null, run, RtpcVolumeCap.Peak).VolumeDb, 3);
        Assert.Equal(-32f - 4f, VoiceMixer.Evaluate(v, null, 1, 0, null, run, RtpcVolumeCap.Node).VolumeDb, 3);         // the parent lifted to 0
        Assert.Equal(-32f, VoiceMixer.Evaluate(v, null, 1, 0, null, run, RtpcVolumeCap.Voice).VolumeDb, 3);             // the chain's sum capped
        Assert.Equal(-32f - 7f + 19.81f, VoiceMixer.Evaluate(v, null, 1, 0, null, run, RtpcVolumeCap.Off).VolumeDb, 3);  // before the branch
        // Standing still the curve adds nothing in node, voice and off; peak keeps the curve's 19.81 dB range below its top.
        var still = new Dictionary<uint, float> { [speed] = 0f };
        foreach (var cap in new[] { RtpcVolumeCap.Node, RtpcVolumeCap.Voice, RtpcVolumeCap.Off })
            Assert.Equal(-39f, VoiceMixer.Evaluate(v, null, 1, 0, null, still, cap).VolumeDb, 3);
        Assert.Equal(-39f - 19.81f, VoiceMixer.Evaluate(v, null, 1, 0, null, still).VolumeDb, 3);
        // Between: 1 m/s gives +3.96 dB, which lifts the -3 dB parent to +0.96 -> capped at 0 per node; peak is 15.85 dB under its top.
        var walk = new Dictionary<uint, float> { [speed] = 1f };
        Assert.Equal(-36f, VoiceMixer.Evaluate(v, null, 1, 0, null, walk, RtpcVolumeCap.Node).VolumeDb, 3);
        Assert.Equal(-39f + 3.962f - 19.81f, VoiceMixer.Evaluate(v, null, 1, 0, null, walk).VolumeDb, 3);
        Assert.Equal(-32f - 7f + 1.981f, VoiceMixer.Evaluate(v, null, 1, 0, null, new Dictionary<uint, float> { [speed] = 0.5f }, RtpcVolumeCap.Node).VolumeDb, 3);
    }

    [Fact]
    public void A_set_volume_change_still_lowers_a_node_under_the_cap()
    {
        // The node is bounded on its own volume and the Set/Reset delta is added after: an independent -20 dB on the -3 dB parent lowers the voice by 17 dB in
        // every mode (before the review fix the node cap took -20 + 19.81 = -0.19 and the change was lost).
        uint speed = VoiceMixer.Speed;
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            Random(0x10, new Node(0, Volume: -3f, Bus: 0xB2, RtpcCurves: [new RtpcSpec(speed, 0, [(0f, 0f, 4), (5f, 19.81f, 9), (100f, 19.81f, 4)])]), [0x20]),
            Random(0x20, new Node(0x10, Volume: -4f), [0x11]), Sound(0x11, Wem1, new Node(0x20)),
            Bus(0xB1, 0, 0f, busVolume: -7f), Bus(0xB2, 0xB1, -25f),
        };
        var v = Voice(objects);
        var run = new Dictionary<uint, float> { [speed] = 6.5f };
        var m = new SoundModifiers();
        var unchanged = Enum.GetValues<RtpcVolumeCap>().ToDictionary(c => c, c => VoiceMixer.Evaluate(v, m, 1, 0, null, run, c).VolumeDb);
        m.Set(SoundProperty.Volume, 0x10, 0, -20f, offset: false, now: 0, fadeSeconds: 0);
        foreach (var cap in Enum.GetValues<RtpcVolumeCap>())
            Assert.True(Math.Abs(unchanged[cap] - 17f - VoiceMixer.Evaluate(v, m, 1, 0, null, run, cap).VolumeDb) < 1e-3, $"{cap}: a -20 dB set must lower the voice by 17 dB");
        Assert.Equal(-32f - 17f - 4f, VoiceMixer.Evaluate(v, m, 1, 0, null, run, RtpcVolumeCap.Node).VolumeDb, 3);
        // A mute on the bounded node mutes it too.
        m.Apply(new SoundPropertyAction(SoundProperty.Mute, 0x10, false, 0, false, 0, 0, false), 1, 0);
        Assert.Equal(-32f - 17f - 4f + VoiceMixer.MuteDb, VoiceMixer.Evaluate(v, m, 1, 0, null, run, RtpcVolumeCap.Node).VolumeDb, 3);
    }

    [Theory]
    [InlineData("peak", true, RtpcVolumeCap.Peak)]
    [InlineData("NODE", true, RtpcVolumeCap.Node)]
    [InlineData("voice", true, RtpcVolumeCap.Voice)]
    [InlineData("off", true, RtpcVolumeCap.Off)]
    [InlineData("bad", false, RtpcVolumeCap.Peak)]
    [InlineData("1", false, RtpcVolumeCap.Peak)]
    [InlineData(null, false, RtpcVolumeCap.Peak)]          // the flag given last, without a value
    [InlineData("--log-sound", false, RtpcVolumeCap.Peak)] // the next flag is not a value
    public void The_cap_flag_takes_names_only_and_needs_a_value(string? text, bool ok, RtpcVolumeCap expected)
    {
        Assert.Equal(ok, RtpcVolumeCaps.TryParse(text, out var cap, out string? error));
        Assert.Equal(expected, cap);
        Assert.Equal(ok, error is null);
    }

    [Fact]
    public void The_owners_footstep_level_applies_to_foot_tags_only()
    {
        static SoundBinding B(string trigger, bool footstep, string evt = "play_sm_footfalls_normal") =>
            new($"extensions/x/{trigger}/commands/post_sound_event", trigger, "", "", [], evt, "", false, footstep, false, 0f, "", new Dictionary<string, float>());
        Assert.Equal(-3f, FootstepLevel.OwnerGainDb);
        foreach (var t in new[] { "evt_left_foot", "evt_right_foot", "evt_left_foot_rush", "EVT_Right_Foot_Rush", "evt_bullrush_stop_small_1", "evt_bullrush_stop_big" })
            Assert.Equal(-3f, FootstepLevel.GainDb(B(t, true)));
        Assert.Equal(0f, FootstepLevel.GainDb(B("finish_falling_actions", true, "play_sm_land_normal")));          // landings keep the data's level
        Assert.Equal(0f, FootstepLevel.GainDb(B("action_land_regular_by_height", true, "play_sm_jump_pack_land")));
        Assert.Equal(0f, FootstepLevel.GainDb(B("evt_left_foot", false)));                                          // not an is_footstep_sound command
        Assert.Equal(0f, FootstepLevel.GainDb(B("fx_evade_roll", false, "play_evade_roll")));
    }

    [Theory]
    [InlineData("EVT_Left_Foot", "evt_left_foot")]
    [InlineData("EVT_Left_Foot_Rush", "evt_left_foot")]     // the sprint's version of the same footfall
    [InlineData("evt_right_foot_rush", "evt_right_foot")]
    [InlineData("EVT_Right_Foot", "evt_right_foot")]
    [InlineData("evt_bullrush_stop_small_1", "evt_bullrush_stop_small_1")]
    [InlineData("actn_snd_swing", "actn_snd_swing")]
    public void A_foot_tag_and_its_sprint_version_are_spaced_as_one_footfall(string tag, string key) =>
        Assert.Equal(key, FootstepLevel.SpacingKey(tag));

    [Fact]
    public void One_footfall_posts_once_across_the_run_blend_and_the_sprint_clip()
    {
        var last = new Dictionary<string, double>();
        Assert.True(FootstepLevel.AdmitFootfall(last, "EVT_Left_Foot", 21.102));           // the run blend's tag
        Assert.False(FootstepLevel.AdmitFootfall(last, "EVT_Left_Foot_Rush", 21.150));     // the sprint clip's tag for the same footfall (the bot run's case)
        Assert.True(FootstepLevel.AdmitFootfall(last, "EVT_Right_Foot_Rush", 21.40));      // the other foot is its own
        Assert.True(FootstepLevel.AdmitFootfall(last, "EVT_Left_Foot_Rush", 21.70));       // the next left step (0.6 s later)
        Assert.False(FootstepLevel.AdmitFootfall(last, "evt_left_foot", 21.70 + FootstepLevel.SameFootMinSeconds - 0.001));
        Assert.True(FootstepLevel.AdmitFootfall(last, "evt_left_foot", 21.70 + FootstepLevel.SameFootMinSeconds));
        // Other tags are never held back nor recorded.
        Assert.True(FootstepLevel.AdmitFootfall(last, "evt_bullrush_stop_small_1", 30.0));
        Assert.True(FootstepLevel.AdmitFootfall(last, "evt_bullrush_stop_small_1", 30.01));
        Assert.True(FootstepLevel.AdmitFootfall(last, "actn_snd_swing", 30.0));
        Assert.Equal(2, last.Count);
        // A clock that went back (a reconnect) does not hold a foot back.
        Assert.True(FootstepLevel.AdmitFootfall(last, "evt_right_foot", 1.0));
    }

    [Fact]
    public void Attenuation_adds_its_volume_and_its_low_pass_at_the_listeners_distance()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x11, 0, Bank1), Sound(0x11, Wem1, new Node(0, AttenuationId: 0xAA)),
            LowPassAttenuation(0xAA),
        };
        var v = Voice(objects);
        Assert.NotNull(v.Attenuation!.LowPass);
        var near = VoiceMixer.Evaluate(v, null, 1, 0, 50f, null);
        Assert.Equal(-30f, near.VolumeDb, 3);
        Assert.Equal(0f, near.Lpf, 3);                                          // constant 0 up to 100 m
        var far = VoiceMixer.Evaluate(v, null, 1, 0, 150f, null);
        Assert.Equal(-78f, far.VolumeDb, 3);
        Assert.Equal(30f, far.Lpf, 3);                                          // halfway from 0 to 60
        Assert.Equal(60f, VoiceMixer.Evaluate(v, null, 1, 0, 400f, null).Lpf, 3);
    }

    /// <summary>An attenuation object with a dry volume curve (0 dB at 0, -60 at 100, -96 at 200) and a low-pass curve (0 up to 100 m, 60 at 200 m).</summary>
    static Obj LowPassAttenuation(uint id) =>
        new(HircType.Attenuation, id, Build(w =>
        {
            w.Write((byte)0);
            w.Write((sbyte)0); w.Write((sbyte)-1); w.Write((sbyte)-1); w.Write((sbyte)1); w.Write((sbyte)-1);
            w.Write((byte)2);
            w.Write((byte)2); w.Write((ushort)3);
            foreach (var (x, y, i) in new[] { (0f, 0f, 4u), (100f, -60f, 4u), (200f, -96f, 4u) }) { w.Write(x); w.Write(y); w.Write(i); }
            w.Write((byte)0); w.Write((ushort)3);
            foreach (var (x, y, i) in new[] { (0f, 0f, 4u), (100f, 0f, 4u), (200f, 60f, 4u) }) { w.Write(x); w.Write(y); w.Write(i); }
            w.Write((ushort)0);
        }));

    static byte[] Build(Action<BinaryWriter> body)
    {
        var ms = new MemoryStream();
        body(new BinaryWriter(ms));
        return ms.ToArray();
    }

    [Fact]
    public void Property_actions_are_read_with_their_fade_value_meaning_and_scope()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7),
            PropertyAction(0xA1, 0xE010, 0x40, 45f, transitionMs: 750),                       // set LPF 45 on 0x40 (independent), global
            PropertyAction(0xA2, 0x8011, 0x41, -300f, transitionMs: 2000, meaning: 2, delay: 100), // set pitch -300 offset, emitter scoped
            PropertyAction(0xA3, 0xF010, 0x40, 0f, transitionMs: 4000),                       // reset LPF
            PropertyAction(0xA4, 0xA010, 0x42, -9f, meaning: 2),                              // set volume
            PropertyAction(0xA5, 0xB011, 0x42, 0f),                                           // reset volume
            PropertyAction(0xA6, 0x6010, 0x43, 0f),                                           // mute
            PropertyAction(0xA7, 0x7010, 0x43, 0f),                                           // unmute
            Event(0xE2, 0xA8), SetSwitch(0xA8, 0xD6E86601, 0x6F8AE299),
        };
        using var c = Catalog(objects);
        var play = c.Post(0xE1, SoundSwitches.None);
        Assert.Equal(0, play.UnsupportedActions);
        Assert.Equal(new[]
        {
            (SoundProperty.Lpf, 0x40u, false), (SoundProperty.Pitch, 0x41u, false), (SoundProperty.Lpf, 0x40u, true), (SoundProperty.Volume, 0x42u, false),
            (SoundProperty.Volume, 0x42u, true), (SoundProperty.Mute, 0x43u, false), (SoundProperty.Mute, 0x43u, true),
        }, play.Properties.Select(a => (a.Property, a.Target, a.Reset)));
        var lpf = play.Properties[0];
        Assert.Equal((45f, 0.75f, false, false), (lpf.Value, lpf.Fade, lpf.Offset, lpf.EmitterScope));
        var pitch = play.Properties[1];
        Assert.Equal((-300f, 2f, 0.1f, true, true), (pitch.Value, pitch.Fade, pitch.Delay, pitch.Offset, pitch.EmitterScope));

        var sw = new SoundSwitches();
        var set = c.Post(0xE2, sw);
        Assert.Equal((0xD6E86601u, 0x6F8AE299u), Assert.Single(set.SwitchSets));
        Assert.Equal(0x6F8AE299u, sw.Get(0xD6E86601u));
    }

    [Fact]
    public void Modifiers_fade_to_the_set_value_and_back_and_are_scoped()
    {
        var m = new SoundModifiers();
        // Independent LPF 45 over 1 s on a node whose own value is 10.
        m.Set(SoundProperty.Lpf, 5, 0, 45f, offset: false, now: 10, fadeSeconds: 1);
        Assert.Equal(10f, m.Apply(SoundProperty.Lpf, 5, 1, 10f, 10), 3);
        Assert.Equal(27.5f, m.Apply(SoundProperty.Lpf, 5, 1, 10f, 10.5), 3);
        Assert.Equal(45f, m.Apply(SoundProperty.Lpf, 5, 1, 10f, 12), 3);
        Assert.Equal(10f, m.Apply(SoundProperty.Lpf, 6, 1, 10f, 12), 3);        // another target
        // Reset over 2 s from the full effect.
        m.Reset(SoundProperty.Lpf, 5, 0, now: 12, fadeSeconds: 2);
        Assert.Equal(27.5f, m.Apply(SoundProperty.Lpf, 5, 1, 10f, 13), 3);
        Assert.Equal(10f, m.Apply(SoundProperty.Lpf, 5, 1, 10f, 14), 3);
        m.Prune(14);
        Assert.Equal(0, m.Count);

        // Offset pitch -300 over 2 s, scoped to emitter 7: others are not affected.
        m.Set(SoundProperty.Pitch, 5, 7, -300f, offset: true, now: 0, fadeSeconds: 2);
        Assert.Equal(-150f, m.Apply(SoundProperty.Pitch, 5, 7, 0f, 1), 3);
        Assert.Equal(-300f + 25f, m.Apply(SoundProperty.Pitch, 5, 7, 25f, 2), 3);
        Assert.Equal(25f, m.Apply(SoundProperty.Pitch, 5, 8, 25f, 2), 3);
        // A reset without a fade drops the entry at once.
        m.Reset(SoundProperty.Pitch, 5, 7, 3, 0);
        Assert.Equal(25f, m.Apply(SoundProperty.Pitch, 5, 7, 25f, 3), 3);

        // An emitter that is gone takes its scoped entries with it; global ones stay.
        m.Set(SoundProperty.Lpf, 5, 9, 60f, offset: false, now: 0, fadeSeconds: 0);
        m.Set(SoundProperty.Lpf, 5, 0, 30f, offset: false, now: 0, fadeSeconds: 0);
        m.ForgetEmitter(9);
        Assert.Equal(1, m.Count);
        Assert.Equal(30f, m.Apply(SoundProperty.Lpf, 5, 9, 0f, 1), 3);
    }

    [Fact]
    public void Voice_mix_follows_modifiers_on_its_nodes_and_buses_and_mutes()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            Random(0x10, new Node(0, Volume: -3f, Pitch: 100f), [0x11]), Sound(0x11, Wem1, new Node(0x10, Bus: 0xB2)),
            Bus(0xB1, 0, 0f, busVolume: -7f), Bus(0xB2, 0xB1, -5f),
        };
        var v = Voice(objects);
        var m = new SoundModifiers();
        Assert.Equal(-15f, VoiceMixer.Evaluate(v, m, 1, 0, null, null).VolumeDb, 3);
        m.Set(SoundProperty.Lpf, 0xB2, 0, 70f, false, 0, 0);                    // a bus LPF reaches the voice through the chain
        Assert.Equal(70f, VoiceMixer.Evaluate(v, m, 1, 0, null, null).Lpf, 3);
        m.Set(SoundProperty.Pitch, 0x10, 0, -1200f, true, 0, 0);                // offset on the container: 100 - 1200
        Assert.Equal(-1100f, VoiceMixer.Evaluate(v, m, 1, 0, null, null).PitchCents, 3);
        m.Set(SoundProperty.Volume, 0x10, 0, -20f, false, 0, 0);                // independent: replaces the container's -3
        Assert.Equal(-32f, VoiceMixer.Evaluate(v, m, 1, 0, null, null).VolumeDb, 3);
        m.Apply(new SoundPropertyAction(SoundProperty.Mute, 0x11, false, 0, false, 0, 0, false), 1, 0);
        Assert.Equal(-32f + VoiceMixer.MuteDb, VoiceMixer.Evaluate(v, m, 1, 0, null, null).VolumeDb, 3);
        m.Apply(new SoundPropertyAction(SoundProperty.Mute, 0x11, true, 0, false, 0, 0, false), 1, 0);
        Assert.Equal(-32f, VoiceMixer.Evaluate(v, m, 1, 0, null, null).VolumeDb, 3);
    }

    [Fact]
    public void Dialogue_events_resolve_argument_paths_with_an_any_case()
    {
        uint character = 0xC1, kind = 0xC2;
        uint tactical = Fnv.Hash32("tactical_marine"), chaos = Fnv.Hash32("chaos_space_marine"), stab = Fnv.Hash32("effort_short_stab"), death = Fnv.Hash32("death");
        var objects = new List<Obj>
        {
            Dialogue(0xD1, [character, kind],
                ([tactical, stab], 0x501), ([tactical, death], 0x502), ([chaos, stab], 0x503), ([chaos, 0], 0x504), ([0, stab], 0x505)),
            Sound(0x501, Wem1, new Node(0)),
        };
        var bank = SoundBank.Read(Bank(Bank1, "Synthetic", [new Item(Wem1, [1, 2, 3, 4])], objects));
        Assert.Equal(0, bank.ParseFailures);
        var d = Assert.IsType<HircDialogueEvent>(bank.Find(0xD1));
        Assert.Equal(new[] { character, kind }, d.ArgumentIds);
        Assert.Equal(0x501u, d.Resolve([tactical, stab]));
        Assert.Equal(0x502u, d.Resolve([tactical, death]));
        Assert.Equal(0x503u, d.Resolve([chaos, stab]));
        Assert.Equal(0x504u, d.Resolve([chaos, Fnv.Hash32("anything")]));       // the character's "any" case
        Assert.Equal(0x505u, d.Resolve([Fnv.Hash32("unknown"), stab]));         // the "any" character
        Assert.Null(d.Resolve([tactical, Fnv.Hash32("taunt")]));                 // no "any" under tactical_marine
        Assert.Null(d.Resolve([tactical]));                                      // too few values
        Assert.Contains(stab, d.KeysAt(2));
        Assert.Equal(3, d.KeysAt(1).Count());
    }

    [Fact]
    public void Dialogue_events_that_do_not_fit_are_kept_raw()
    {
        var good = Dialogue(0xD1, [1], ([5u], 0x501));
        var odd = new Obj(HircType.DialogueEvent, 0xD2, [.. good.Payload, 0xEE]);                 // a stray byte
        var bank = SoundBank.Read(Bank(Bank1, null, [], [good, odd]));
        Assert.IsType<HircDialogueEvent>(bank.Find(0xD1));
        Assert.IsType<HircRaw>(bank.Find(0xD2));
        Assert.Equal(1, bank.ParseFailures);
    }
}
