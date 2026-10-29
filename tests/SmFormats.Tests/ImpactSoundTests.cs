using SmContent;
using SmFormats.Audio;
using static SmFormats.Audio.SyntheticWwiseWriter;

namespace SmFormats.Tests;

/// <summary>
/// sound/impacts: the unset state group as Wwise's "None", the owner's deeper impacts and soft hit confirm (rules in <see cref="ImpactSounds"/>, <see cref="HitConfirmGate"/>),
/// and a post's <see cref="VoiceOffset"/>. Synthetic banks only (no original bytes).
/// </summary>
public class ImpactSoundTests
{
    const uint Bank1 = 0xB1;
    const uint WemNone = 101, WemDefault = 102, WemAlive = 103;

    static AudioCatalog Catalog(IReadOnlyList<Obj> objects)
    {
        var media = new[] { WemNone, WemDefault, WemAlive }.Select(w => new Item(w, [1, 2, 3, 4])).ToList();
        var lib = new WwiseLibrary();
        lib.AddPackage(AkpkPackage.Read(new MemoryStream(Package([new Item(Bank1, Bank(Bank1, "Synthetic", media, objects))], []))), "synthetic");
        return new AudioCatalog(lib, null, null, 7);
    }

    [Fact]
    public void An_unset_state_group_is_in_the_none_state_not_the_containers_default()
    {
        // The confirm event's kill layer: a state switch whose default case (death_armored) plays something and whose "none" case plays nothing.
        uint group = 0x47e2171c, none = Fnv.Hash32("none"), alive = Fnv.Hash32("alive"), deathArmored = 0x07fb5fa0;
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            Switch(0x10, new Node(0), group, deathArmored, [(none, []), (alive, [0x13]), (deathArmored, [0x12])], groupType: 1),
            Sound(0x12, WemDefault, new Node(0x10)), Sound(0x13, WemAlive, new Node(0x10)),
            Event(0xE2, 0xA2), SetState(0xA2, group, deathArmored),
            Event(0xE3, 0xA3), SetState(0xA3, group, 0x12345678),   // a state the container has no case for: its default
        };
        using var c = Catalog(objects);
        Assert.Equal(none, AudioCatalog.NoneState);
        Assert.Empty(c.Post(0xE1, SoundSwitches.None).Voices);                                  // unset: none -> nothing (before: the default case played)
        c.Post(0xE2, SoundSwitches.None);
        Assert.Equal(WemDefault, Assert.Single(c.Post(0xE1, SoundSwitches.None).Voices).WemId); // set by the data's own event: it plays
        c.SetState(group, alive);
        Assert.Equal(WemAlive, Assert.Single(c.Post(0xE1, SoundSwitches.None).Voices).WemId);
        c.Post(0xE3, SoundSwitches.None);
        Assert.Equal(WemDefault, Assert.Single(c.Post(0xE1, SoundSwitches.None).Voices).WemId); // no case for the value: the default, as before
    }

    [Fact]
    public void An_unset_state_group_without_a_none_case_still_plays_the_default()
    {
        uint group = 0x6f0f7f7c;
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x10, 0, Bank1),
            Switch(0x10, new Node(0), group, 0xA8C11BE7, [(0xA8C11BE7u, [0x12])], groupType: 1), Sound(0x12, WemDefault, new Node(0x10)),
        };
        using var c = Catalog(objects);
        Assert.Equal(WemDefault, Assert.Single(c.Post(0xE1, SoundSwitches.None).Voices).WemId);
    }

    [Theory]
    [InlineData(ImpactTreatmentMode.Bolters, "play_bolter_hits", true, true)]
    [InlineData(ImpactTreatmentMode.Bolters, "PLAY_STALKER_HITS", true, true)]
    [InlineData(ImpactTreatmentMode.Bolters, "play_sm_lascannon_impacts", true, false)]
    [InlineData(ImpactTreatmentMode.Ballistic, "play_sm_lascannon_impacts", true, true)]
    [InlineData(ImpactTreatmentMode.Ballistic, "play_melta_gun_hit", false, false)]
    [InlineData(ImpactTreatmentMode.Ballistic, "play_bolter_hits", false, true)]
    [InlineData(ImpactTreatmentMode.Off, "play_bolter_hits", true, false)]
    public void The_deeper_impact_treatment_applies_by_mode(ImpactTreatmentMode mode, string hitEvent, bool hitScan, bool treated)
    {
        Assert.Equal(treated, ImpactSounds.Treated(mode, hitEvent, hitScan));
        Assert.Equal(treated ? ImpactSounds.ImpactTreatment : VoiceOffset.None, ImpactSounds.ImpactOffset(mode, hitEvent, hitScan));
    }

    [Fact]
    public void The_treatment_is_deeper_and_muddier_and_the_confirm_fainter()
    {
        Assert.True(ImpactSounds.ImpactTreatment.PitchCents is <= -300f and >= -500f);
        Assert.True(ImpactSounds.ImpactTreatment.Lpf > 0f);
        Assert.Equal(0f, ImpactSounds.ImpactTreatment.GainDb);
        var armour = ImpactSounds.HitConfirmOffset(armoured: true, kill: false);
        var flesh = ImpactSounds.HitConfirmOffset(armoured: false, kill: false);
        var kill = ImpactSounds.HitConfirmOffset(armoured: true, kill: true);
        Assert.True(armour.GainDb < 0f && flesh.GainDb < 0f);
        Assert.True(flesh.PitchCents < armour.PitchCents);   // flesh duller and deeper than armour
        Assert.True(kill.GainDb > armour.GainDb);           // a kill a little heavier
        Assert.True(armour.Lpf > 0f);
        Assert.Equal("ceramite_armour", ImpactSounds.ConfirmMaterial(true));
        Assert.Equal("flesh", ImpactSounds.ConfirmMaterial(false));
        Assert.Equal("play_set_state_death_flesh", ImpactSounds.KillStateEvent(false));
    }

    [Fact]
    public void A_voice_offset_adds_to_the_mix_with_the_low_pass_clamped()
    {
        var mix = new VoiceMix(-30f, 600f, 70f).With(new VoiceOffset(-10f, -400f, 40f));
        Assert.Equal((-40f, 200f, 100f), (mix.VolumeDb, mix.PitchCents, mix.Lpf));
        Assert.True(VoiceOffset.None.IsNone);
        Assert.Equal(new VoiceOffset(-3f, -400f, 40f), new VoiceOffset(-3f).Plus(ImpactSounds.ImpactTreatment));
    }

    [Fact]
    public void The_soft_confirm_waits_out_its_cooldown_but_a_kill_always_plays()
    {
        var gate = new HitConfirmGate();
        Assert.True(gate.Admit(10.00, kill: false));
        Assert.False(gate.Admit(10.13, kill: false));                                  // the next bolter round
        Assert.True(gate.Admit(10.14, kill: true));                                    // a kill is never held back
        Assert.False(gate.Admit(10.14 + ImpactSounds.HitConfirmCooldown - 0.001, kill: false));
        Assert.True(gate.Admit(10.14 + ImpactSounds.HitConfirmCooldown, kill: false));
        Assert.True(gate.Admit(1.0, kill: false));                                     // a clock that went back (a reconnect) is not held back
    }

    [Fact]
    public void The_death_state_is_set_only_for_the_confirm_post_and_never_left_behind()
    {
        uint none = AudioCatalog.NoneState, alive = Fnv.Hash32("alive"), flesh = ImpactSounds.DeathFlesh, armoured = ImpactSounds.DeathArmored;
        // Soft: a kill sets the death state for this post, any other hit clears it; afterwards what was there before comes back (a death state never does).
        Assert.Equal(armoured, ImpactSounds.StateBeforeConfirm(HitConfirmMode.Soft, null, kill: true, armoured: true));
        Assert.Equal(flesh, ImpactSounds.StateBeforeConfirm(HitConfirmMode.Soft, alive, kill: true, armoured: false));
        Assert.Equal(none, ImpactSounds.StateBeforeConfirm(HitConfirmMode.Soft, flesh, kill: false, armoured: true));   // a stale Ork death does not play the kill layer
        Assert.Equal(alive, ImpactSounds.StateAfterConfirm(HitConfirmMode.Soft, alive, flesh));
        Assert.Equal(none, ImpactSounds.StateAfterConfirm(HitConfirmMode.Soft, null, alive));
        Assert.Equal(none, ImpactSounds.StateAfterConfirm(HitConfirmMode.Soft, flesh, alive));
        // Data: a death state the data set (the Ork Boy's death_action) plays the kill layer only on a kill; the event's own reset (alive) is kept, a death state left over is cleared.
        Assert.Equal(flesh, ImpactSounds.StateBeforeConfirm(HitConfirmMode.Data, flesh, kill: true, armoured: true));
        Assert.Equal(none, ImpactSounds.StateBeforeConfirm(HitConfirmMode.Data, flesh, kill: false, armoured: true));
        Assert.Equal(none, ImpactSounds.StateBeforeConfirm(HitConfirmMode.Data, null, kill: true, armoured: true));    // MP marines' deaths set none: no kill layer
        Assert.Equal(alive, ImpactSounds.StateAfterConfirm(HitConfirmMode.Data, flesh, alive));                        // play_bolter_smack reset it
        Assert.Equal(none, ImpactSounds.StateAfterConfirm(HitConfirmMode.Data, flesh, flesh));                         // play_grenade_launcher_smack has no reset
        Assert.True(ImpactSounds.IsDeathState(armoured) && !ImpactSounds.IsDeathState(alive) && !ImpactSounds.IsDeathState(null));
    }

    [Theory]
    [InlineData("soft", true, HitConfirmMode.Soft)]
    [InlineData("DATA", true, HitConfirmMode.Data)]
    [InlineData("off", true, HitConfirmMode.Off)]
    [InlineData("loud", false, HitConfirmMode.Soft)]
    [InlineData(null, false, HitConfirmMode.Soft)]
    [InlineData("--log-sound", false, HitConfirmMode.Soft)]
    public void The_flags_take_names_only_and_need_a_value(string? text, bool ok, HitConfirmMode expected)
    {
        Assert.Equal(ok, ImpactSounds.TryParse<HitConfirmMode>(text, out var mode, out string? error));
        Assert.Equal(expected, mode);
        Assert.Equal(ok, error is null);
        Assert.True(ImpactSounds.TryParse<ImpactTreatmentMode>("ballistic", out var t, out _) && t == ImpactTreatmentMode.Ballistic);
    }
}
