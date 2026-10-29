using SmContent;
using SmFormats.Audio;
using static SmFormats.Audio.SyntheticWwiseWriter;

namespace SmFormats.Tests;

/// <summary>
/// Footsteps fix: the nodes' "limit sound instances" and "below threshold" settings are read from the banks' advanced settings, and
/// <see cref="InstanceLimiter"/> decides which playing instance a new voice kills. Synthetic banks and chains only.
/// </summary>
public class InstanceLimitTests
{
    const uint Bank1 = 0xB1;

    static AudioCatalog Catalog(IReadOnlyList<Obj> objects)
    {
        var media = new[] { 101u, 102u }.Select(w => new Item(w, [1, 2, 3, 4])).ToList();
        var lib = new WwiseLibrary();
        lib.AddPackage(AkpkPackage.Read(new MemoryStream(Package([new Item(Bank1, Bank(Bank1, "Synthetic", media, objects))], []))), "synthetic");
        return new AudioCatalog(lib, null, null, 7);
    }

    [Fact]
    public void The_advanced_settings_are_read_into_the_node_and_the_voice_chain()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            Layer(0x10, new Node(0, MaxInstances: 1, KillNewest: false), [0x11, 0x12]),
            Sound(0x11, 101, new Node(0x10, MaxInstances: 2, KillNewest: true, GlobalLimit: true, IgnoreParentMax: true, BelowThreshold: 1)),
            Sound(0x12, 102, new Node(0x10, MaxInstances: 0)),
        };
        using var c = Catalog(objects);
        var layer = (HircNode)c.Library.Find(0x10)!;
        Assert.Equal((ushort)1, layer.Base.MaxInstances);
        Assert.False(layer.Base.KillNewest);
        var first = (HircNode)c.Library.Find(0x11)!;
        Assert.Equal(((ushort)2, true, true, true, (byte)1), (first.Base.MaxInstances, first.Base.KillNewest, first.Base.GlobalLimit, first.Base.IgnoreParentMaxInstances, first.Base.BelowThreshold));

        var play = c.Post(0xE1, SoundSwitches.None);
        var a = play.Voices.Single(v => v.SoundId == 0x11);
        Assert.Equal(new InstanceLimit(2, true, true, true), a.Chain[0].Limit);
        Assert.Equal(new InstanceLimit(1, false, false, false), a.Chain[1].Limit);
        Assert.Equal((byte)1, a.Chain[0].BelowThreshold);
        var b = play.Voices.Single(v => v.SoundId == 0x12);
        Assert.Null(b.Chain[0].Limit);                                       // 0 = no limit
    }

    [Fact]
    public void A_node_with_no_limit_keeps_its_ignore_parent_flag()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            Layer(0x10, new Node(0, MaxInstances: 1), [0x11]),
            Sound(0x11, 101, new Node(0x10, MaxInstances: 0, IgnoreParentMax: true)),
        };
        using var c = Catalog(objects);
        var voice = Assert.Single(c.Post(0xE1, SoundSwitches.None).Voices);
        Assert.Equal(new InstanceLimit(0, false, false, true), voice.Chain[0].Limit);
        // ... and the limiter honours it: the parent's limit of 1 does not apply below it.
        var playing = new[] { Voice(1, 7, 1, 0, Node(0x40), Node(0x10, new InstanceLimit(1, false, false, false))) };
        Assert.Equal(LimitDecision.None, InstanceLimiter.Decide(voice.Chain, 7, 2, playing));
    }

    static VoiceNode Node(uint id, InstanceLimit? limit = null) => new(id, 0, 0, 0, []) { Limit = limit };

    static InstanceLimiter.Playing Voice(int handle, ulong emitter, long post, double started, params VoiceNode[] chain) => new(handle, emitter, post, chain);

    [Fact]
    public void A_new_post_kills_the_oldest_instance_of_a_node_at_its_limit_for_the_same_emitter_only()
    {
        var layer = Node(0x10, new InstanceLimit(1, false, false, false));
        var playing = new[]
        {
            Voice(1, emitter: 7, post: 1, started: 1.0, Node(0x20), layer),
            Voice(2, emitter: 8, post: 2, started: 1.1, Node(0x21), layer),    // another emitter's: not counted for emitter 7
        };
        var d = InstanceLimiter.Decide([Node(0x22), layer], emitter: 7, post: 3, playing);
        Assert.False(d.DropNew);
        Assert.Equal(new[] { 1 }, d.Kill);

        // A global limit counts across emitters: the oldest of both is killed first, then the next, until one is left for the new voice.
        var global = Node(0x10, new InstanceLimit(1, false, true, false));
        var d2 = InstanceLimiter.Decide([Node(0x22), global], 7, 3, playing.Select(p => p with { Chain = [p.Chain[0], global] }).ToArray());
        Assert.Equal(new[] { 1, 2 }, d2.Kill);
    }

    [Fact]
    public void Voices_of_one_post_are_one_instance_and_never_kill_each_other()
    {
        var layer = Node(0x10, new InstanceLimit(1, false, false, false));
        var playing = new[]
        {
            Voice(1, 7, post: 5, started: 1.0, Node(0x20), layer),
            Voice(2, 7, post: 5, started: 1.0, Node(0x21), layer),            // the second sound of the same layer post
        };
        // The next sound of that same post (a layer with three children): nothing to kill.
        Assert.Equal(LimitDecision.None, InstanceLimiter.Decide([Node(0x22), layer], 7, post: 5, playing));
        // The next post replaces the whole earlier instance: both voices of post 5 go.
        var d = InstanceLimiter.Decide([Node(0x22), layer], 7, post: 6, playing);
        Assert.Equal(new[] { 1, 2 }, d.Kill);
    }

    [Fact]
    public void A_limit_of_two_keeps_the_newest_instance_and_kill_newest_drops_the_new_voice()
    {
        var two = Node(0x10, new InstanceLimit(2, false, false, false));
        var playing = new[]
        {
            Voice(1, 7, 1, 1.0, Node(0x20), two), Voice(2, 7, 2, 1.4, Node(0x21), two),
        };
        var d = InstanceLimiter.Decide([Node(0x22), two], 7, 3, playing);
        Assert.Equal(new[] { 1 }, d.Kill);                                    // two playing, at the limit: the oldest goes, the new one is the second
        Assert.Equal(LimitDecision.None, InstanceLimiter.Decide([Node(0x22), two], 7, 3, playing.Take(1).ToArray()));   // one playing: room for another

        var newest = Node(0x10, new InstanceLimit(2, true, false, false));
        var dropped = InstanceLimiter.Decide([Node(0x22), newest], 7, 3, playing.Select(p => p with { Chain = [p.Chain[0], newest] }).ToArray());
        Assert.True(dropped.DropNew);
        Assert.Empty(dropped.Kill);
    }

    [Fact]
    public void A_node_that_ignores_its_parents_hides_their_limits_and_every_node_of_the_chain_is_checked()
    {
        var parent = Node(0x30, new InstanceLimit(1, false, false, false));
        var ignoring = Node(0x20, new InstanceLimit(4, false, false, true));
        var playing = new[] { Voice(1, 7, 1, 1.0, Node(0x40), ignoring, parent) };
        // The parent's limit of 1 would kill the playing voice; the node below it ignores that and has room (4).
        Assert.Equal(LimitDecision.None, InstanceLimiter.Decide([Node(0x41), ignoring, parent], 7, 2, playing));
        // Without the ignore flag the parent's limit applies.
        var plain = Node(0x20, new InstanceLimit(4, false, false, false));
        var plainPlaying = new[] { Voice(1, 7, 1, 1.0, Node(0x40), plain, parent) };
        Assert.Equal(new[] { 1 }, InstanceLimiter.Decide([Node(0x41), plain, parent], 7, 2, plainPlaying).Kill);
        // A chain without limits, or nothing playing, decides nothing.
        Assert.Equal(LimitDecision.None, InstanceLimiter.Decide([Node(0x41), Node(0x20)], 7, 2, plainPlaying));
        Assert.Equal(LimitDecision.None, InstanceLimiter.Decide([Node(0x41), parent], 7, 2, []));
    }

    [Fact]
    public void Age_is_the_posts_number_so_a_late_voice_of_an_older_post_is_dropped_not_the_newer_instance_killed()
    {
        var layer = Node(0x10, new InstanceLimit(1, false, false, false));
        var playing = new[] { Voice(1, 7, post: 5, started: 0.1, Node(0x20), layer) };
        // A voice of post 4 (decoded late, so it starts after post 5's) finds post 5 at the limit: it is the older one and goes.
        var d = InstanceLimiter.Decide([Node(0x22), layer], 7, post: 4, playing);
        Assert.True(d.DropNew);
        Assert.Empty(d.Kill);
        // Two instances at a limit of 2: the lowest post number is killed whatever order their voices started in.
        var two = Node(0x10, new InstanceLimit(2, false, false, false));
        var pair = new[] { Voice(1, 7, post: 9, started: 0.1, Node(0x20), two), Voice(2, 7, post: 8, started: 0.2, Node(0x21), two) };
        var killed = InstanceLimiter.Decide([Node(0x22), two], 7, post: 10, pair);
        Assert.Equal(new[] { 2 }, killed.Kill);
        Assert.Equal(new[] { (8L, 0x10u) }, killed.Instances);
    }

    [Fact]
    public void A_drop_cancels_the_kills_chosen_for_the_same_voice()
    {
        var first = Node(0x10, new InstanceLimit(1, false, false, false));
        var second = Node(0x11, new InstanceLimit(1, true, false, false));
        var playing = new[] { Voice(1, 7, 1, 0, Node(0x20), first), Voice(2, 7, 2, 0, Node(0x21), second) };
        var d = InstanceLimiter.Decide([Node(0x22), first, second], 7, 3, playing);
        Assert.True(d.DropNew);
        Assert.Empty(d.Kill);                                                 // the earlier node's kill of voice 1 is not carried out
        Assert.Empty(d.Instances);
    }

    // ---- the audibility rule (client SoundSystem.Audible feeds it) ----

    static bool Audible2D(float levelDb, byte below = 1, bool thresholded = true, float master = -7f) =>
        VoiceAudibility.IsAudible(is3D: false, hasAttenuation: false, 0f, null, levelDb, master, below, hasChain: true, thresholded);

    [Fact]
    public void Quiet_non_positioned_voices_are_culled_only_when_the_post_asked_and_the_sound_is_set_to_be_killed()
    {
        Assert.False(Audible2D(-70f));                                        // -63 without the master bus: under -60
        Assert.True(Audible2D(-60f));                                         // -53 without it
        Assert.True(Audible2D(-70f, thresholded: false));                     // --sound-limits none: exactly the old behaviour, 2D voices always play
        Assert.True(Audible2D(-70f, below: 0));                               // a sound set to keep playing is never culled
        Assert.False(Audible2D(-70f, below: 2));                              // virtual: not played either
        Assert.False(Audible2D(-96f - 7f + 7f, thresholded: true));           // a muted voice carries the mute's -96 dB
        Assert.True(VoiceAudibility.IsAudible(false, false, 0f, null, -80f, -7f, 0, hasChain: false, thresholded2D: false));
    }

    [Fact]
    public void Positioned_voices_keep_their_old_rule_in_every_mode()
    {
        foreach (bool thresholded in new[] { false, true })
        {
            Assert.True(VoiceAudibility.IsAudible(true, true, 100f, 50f, -30f, -7f, 1, true, thresholded));
            Assert.False(VoiceAudibility.IsAudible(true, true, 100f, 150f, -30f, -7f, 1, true, thresholded));      // beyond the curve's end
            Assert.False(VoiceAudibility.IsAudible(true, true, 100f, 50f, -80f, -7f, 1, true, thresholded));       // under the threshold
            Assert.False(VoiceAudibility.IsAudible(true, true, 100f, 50f, -80f, -7f, 0, true, thresholded));       // even a keep-playing sound (as before)
            Assert.True(VoiceAudibility.IsAudible(true, true, 0f, null, -80f, -7f, 1, true, thresholded));         // no listener
            Assert.True(VoiceAudibility.IsAudible(true, false, 0f, 10f, -90f, -7f, 1, true, thresholded));        // positioned without an attenuation: always, as before
        }
    }

    [Fact]
    public void A_voice_that_will_not_start_must_not_evict_an_audible_one()
    {
        // The caller's order (SoundSystem.Start): audibility first, the limiter only for a voice that starts.
        var layer = Node(0x10, new InstanceLimit(1, false, false, false));
        var playing = new List<InstanceLimiter.Playing> { Voice(1, 7, 1, 0, Node(0x20), layer) };
        bool audible = Audible2D(-75f);                                       // the foley layer of the next step
        var decision = audible ? InstanceLimiter.Decide([Node(0x22), layer], 7, 2, playing) : LimitDecision.None;
        Assert.False(audible);
        Assert.Empty(decision.Kill);                                          // (deciding first would have killed voice 1)
        Assert.Equal(new[] { 1 }, InstanceLimiter.Decide([Node(0x22), layer], 7, 2, playing).Kill);
    }
}
