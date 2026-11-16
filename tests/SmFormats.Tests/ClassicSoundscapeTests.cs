using SmContent;

namespace SmFormats.Tests;

/// <summary>sound/classic-soundscape: the rule's numbers (the owner's interview, 2026-11-13): reach, steady fall-off, protection, kinds, modes. Synthetic mixes only.</summary>
public class ClassicSoundscapeTests
{
    // A footstep-like voice: -27 dB at 0 m, the data's curve and floor -14 dB (so -41 dB, dull) far away.
    static readonly VoiceMix Near = new(-27f, 0f, 0f);
    static VoiceMix Data(float d) => new(-27f - Math.Min(14f, d * 1.2f), 0f, Math.Min(60f, d));

    [Fact]
    public void Movement_is_lifted_over_the_data_inside_the_reach_with_a_steady_fall_off()
    {
        float last = float.MaxValue;
        foreach (float d in new[] { 2f, 10f, 30f, 50f })
        {
            var heard = ClassicSoundscape.Apply(ScapeClass.Movement, Data(d), Near, d);
            Assert.True(heard.VolumeDb >= Data(d).VolumeDb);
            Assert.True(heard.VolumeDb < last);   // steady: always falling
            last = heard.VolumeDb;
        }
        Assert.Equal(-27f + ClassicSoundscape.MovementLiftDb - ClassicSoundscape.MovementSlopeDbPerM * 48f, ClassicSoundscape.Apply(ScapeClass.Movement, Data(50f), Near, 50f).VolumeDb, 3);
    }

    [Fact]
    public void A_loud_movement_sound_is_not_lifted_and_a_quiet_one_at_most_the_lift()
    {
        Assert.Equal(0f, ClassicSoundscape.MovementLift(-8f));
        Assert.Equal(ClassicSoundscape.MovementLiftDb, ClassicSoundscape.MovementLift(-40f));
        Assert.Equal(3f, ClassicSoundscape.MovementLift(-22f), 3);
        var pound = ClassicSoundscape.Apply(ScapeClass.MovementFar, new VoiceMix(-9f, 0f, 0f), new VoiceMix(-8f, 0f, 0f), 2f);
        Assert.InRange(pound.VolumeDb, -9f, -8f);   // already above the readable level: no lift, at most the data's own 0 m level
    }

    [Fact]
    public void Movement_fades_out_past_the_reach()
    {
        var at50 = ClassicSoundscape.Apply(ScapeClass.Movement, Data(50f), Near, 50f).VolumeDb;
        var at60 = ClassicSoundscape.Apply(ScapeClass.Movement, Data(60f), Near, 60f).VolumeDb;
        var at70 = ClassicSoundscape.Apply(ScapeClass.Movement, Data(70f), Near, 70f).VolumeDb;
        Assert.True(at50 - at60 >= 10f);
        Assert.True(at50 - at70 >= 25f);   // on its way to the -60 dB cut
        Assert.True(at70 < at60);
    }

    [Fact]
    public void Movement_keeps_a_clear_top_end()
    {
        Assert.Equal(ClassicSoundscape.MaxLpf(ScapeClass.Movement), ClassicSoundscape.Apply(ScapeClass.Movement, Data(50f), Near, 50f).Lpf);
        Assert.Equal(5f, ClassicSoundscape.Apply(ScapeClass.Movement, new VoiceMix(-30f, 0f, 5f), Near, 5f).Lpf);   // never made duller
    }

    [Fact]
    public void Reload_is_quieter_than_steps_and_reaches_twenty_metres()
    {
        foreach (float d in new[] { 3f, 10f, 20f })
            Assert.True(ClassicSoundscape.Apply(ScapeClass.Reload, Data(d), Near, d).VolumeDb < ClassicSoundscape.Apply(ScapeClass.Movement, Data(d), Near, d).VolumeDb);
        Assert.True(ClassicSoundscape.Apply(ScapeClass.Reload, Data(20f), Near, 20f).VolumeDb >= Data(20f).VolumeDb);
        Assert.True(ClassicSoundscape.Apply(ScapeClass.Reload, Data(35f), Near, 35f).VolumeDb < ClassicSoundscape.Apply(ScapeClass.Reload, Data(20f), Near, 20f).VolumeDb - 15f);
    }

    [Fact]
    public void Far_classes_keep_a_floor_over_the_whole_map_and_never_get_quieter_than_the_data()
    {
        foreach (var c in new[] { ScapeClass.Gunfire, ScapeClass.MovementFar })
            foreach (float d in new[] { 60f, 120f, 300f, 1000f })
            {
                var heard = ClassicSoundscape.Apply(c, Data(d), Near, d);
                Assert.True(heard.VolumeDb >= Data(d).VolumeDb);
                Assert.True(heard.VolumeDb >= -27f - ClassicSoundscape.FarMaxDropDb - 0.01f);
            }
    }

    [Fact]
    public void None_is_untouched_and_the_offset_carries_the_class()
    {
        Assert.Equal(Data(40f), ClassicSoundscape.Apply(ScapeClass.None, Data(40f), Near, 40f));
        Assert.True(VoiceOffset.None.IsNone);
        Assert.False(new VoiceOffset(Scape: ScapeClass.Movement).IsNone);
        Assert.Equal(ScapeClass.Movement, new VoiceOffset(-3f).Plus(new VoiceOffset(Scape: ScapeClass.Movement)).Scape);
        Assert.Equal(ScapeClass.Movement, new VoiceOffset(Scape: ScapeClass.Movement).Plus(new VoiceOffset(2f)).Scape);
    }

    [Theory]
    [InlineData("footstep", ScapeClass.Movement)]
    [InlineData("charge", ScapeClass.Movement)]
    [InlineData("roll", ScapeClass.Movement)]
    [InlineData("bark", ScapeClass.Movement)]
    [InlineData("jump land", ScapeClass.Movement)]
    [InlineData("jump launch", ScapeClass.MovementFar)]
    [InlineData("dash", ScapeClass.MovementFar)]
    [InlineData("ground pound", ScapeClass.MovementFar)]
    [InlineData("fire other", ScapeClass.Gunfire)]
    [InlineData("detonation", ScapeClass.Gunfire)]
    [InlineData("reload other", ScapeClass.Reload)]
    [InlineData("fire own", ScapeClass.None)]
    [InlineData("switch", ScapeClass.None)]
    public void Kinds_map_to_classes(string kind, ScapeClass expected) => Assert.Equal(expected, ClassicSoundscape.ClassOfKind(kind));

    [Fact]
    public void Only_movement_is_protected_and_the_protection_is_bounded()
    {
        Assert.True(ClassicSoundscape.IsProtected(ScapeClass.Movement));
        Assert.True(ClassicSoundscape.IsProtected(ScapeClass.MovementFar));
        Assert.False(ClassicSoundscape.IsProtected(ScapeClass.Gunfire));
        Assert.False(ClassicSoundscape.IsProtected(ScapeClass.Reload));
        Assert.InRange(ClassicSoundscape.ProtectedVoiceCap, 1, 48 / 2);   // 15 others cannot starve the pool of 48
    }

    [Theory]
    [InlineData(ScapeMode.Clear, false, ScapeClass.Movement, 5, true)]
    [InlineData(ScapeMode.Natural, false, ScapeClass.MovementFar, 20, true)]
    [InlineData(ScapeMode.Clear, false, ScapeClass.Movement, 21, false)]   // past the bound: ordinary voices
    [InlineData(ScapeMode.Clear, false, ScapeClass.Gunfire, 0, false)]     // gunfire still competes for voices
    [InlineData(ScapeMode.Clear, false, ScapeClass.Reload, 0, false)]
    [InlineData(ScapeMode.Clear, true, ScapeClass.Movement, 0, false)]     // the own player's paths stay as before
    [InlineData(ScapeMode.Old, false, ScapeClass.Movement, 0, false)]      // old mix and Musou Mode (Old)
    public void Protection_holds_only_for_others_movement_inside_the_bound(ScapeMode mode, bool own, ScapeClass c, int held, bool expected) =>
        Assert.Equal(expected, ClassicSoundscape.Holds(mode, own, c, held));

    [Fact]
    public void Direction_modes_pan_and_darken_only_in_clear_for_others()
    {
        Assert.Equal(ClassicSoundscape.ClearPanningStrength, ClassicSoundscape.PanningStrength(ScapeMode.Clear, own: false));
        Assert.Equal(1f, ClassicSoundscape.PanningStrength(ScapeMode.Clear, own: true));
        Assert.Equal(1f, ClassicSoundscape.PanningStrength(ScapeMode.Natural, own: false));
        Assert.Equal(1f, ClassicSoundscape.PanningStrength(ScapeMode.Old, own: false));
        Assert.Equal(0f, ClassicSoundscape.RearLpfFor(ScapeMode.Clear, false, 1f));
        Assert.Equal(ClassicSoundscape.RearLpf, ClassicSoundscape.RearLpfFor(ScapeMode.Clear, false, -1f));
        Assert.Equal(0f, ClassicSoundscape.RearLpfFor(ScapeMode.Natural, false, -1f));
        Assert.Equal(0f, ClassicSoundscape.RearLpfFor(ScapeMode.Clear, true, -1f));
    }
}
