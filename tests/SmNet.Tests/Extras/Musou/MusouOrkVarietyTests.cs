using System;
using System.Linq;
using SmNet.Extras.Musou;
using Xunit;
using Num = System.Numerics;

namespace SmNet.Tests.Extras.Musou;

/// <summary>Musou Ork variety (owner 2026-11-16): each Ork's flinch and death throw differ from its neighbours', repeatably from the entity id, client presentation only (<see cref="MusouOrkVariety"/>).</summary>
public class MusouOrkVarietyTests
{
    [Fact]
    public void Strength_duration_and_delay_of_neighbouring_ids_differ_by_at_least_30_percent_of_the_nominal()
    {
        for (int id = 1; id < 5000; id++)
        {
            var a = MusouOrkVariety.Flinch(id);
            var b = MusouOrkVariety.Flinch(id + 1);
            Assert.True(MathF.Abs(a.Strength - b.Strength) >= 0.30f, $"strength {id}: {a.Strength} {b.Strength}");   // nominal strength is 1
            Assert.True(MathF.Abs(a.Duration - b.Duration) >= 0.30f, $"duration {id}: {a.Duration} {b.Duration}");   // nominal duration is 1
            Assert.True(MathF.Abs(a.Delay - b.Delay) >= 0.30f * MusouOrkVariety.DelayMax - 1e-4f, $"delay {id}: {a.Delay} {b.Delay}");   // 30 % of its range
        }
    }

    [Fact]
    public void The_variants_stay_inside_their_ranges_and_a_wave_of_ids_spreads_wide()
    {
        var all = Enumerable.Range(1, 2000).Select(MusouOrkVariety.Flinch).ToList();
        Assert.All(all, v =>
        {
            Assert.InRange(v.Strength, MusouOrkVariety.StrengthMin - 1e-4f, MusouOrkVariety.StrengthMax + 1e-4f);
            Assert.InRange(v.Duration, MusouOrkVariety.DurationMin - 1e-4f, MusouOrkVariety.DurationMax + 1e-4f);
            Assert.InRange(v.Delay, 0f, MusouOrkVariety.DelayMax + 1e-4f);
        });
        Assert.True(all.Max(v => v.Strength) - all.Min(v => v.Strength) > 0.6f);
        Assert.True(all.Max(v => v.Duration) - all.Min(v => v.Duration) > 0.7f);
    }

    [Fact]
    public void A_variant_is_the_same_every_time_for_the_same_id()
    {
        for (int id = 0; id < 500; id++)
        {
            Assert.Equal(MusouOrkVariety.Flinch(id), MusouOrkVariety.Flinch(id));
            Assert.Equal(MusouOrkVariety.ThrowDelay(id), MusouOrkVariety.ThrowDelay(id));
            var v = new Num.Vector3(3f, -4f, 8f);
            Assert.Equal(MusouOrkVariety.Throw(id, v), MusouOrkVariety.Throw(id, v));
            Assert.Equal(MusouOrkVariety.DeathClip(id, 3), MusouOrkVariety.DeathClip(id, 3));
        }
    }

    [Fact]
    public void A_fifth_of_the_Orks_flinch_sideways_or_back_and_forth_and_a_side_flinch_turns_a_quarter()
    {
        var all = Enumerable.Range(1, 5000).Select(MusouOrkVariety.Flinch).ToList();
        double share = all.Count(v => v.Mode != MusouFlinchMode.Away) / (double)all.Count;
        Assert.InRange(share, 0.17, 0.29);   // 0.14 + 0.09 = 0.23
        Assert.Contains(all, v => v.Mode == MusouFlinchMode.SideLeft);
        Assert.Contains(all, v => v.Mode == MusouFlinchMode.SideRight);
        Assert.Contains(all, v => v.Mode == MusouFlinchMode.BackAndForth);
        var away = new Num.Vector2(0f, 1f);
        var left = MusouOrkVariety.FlinchDirection(new MusouFlinchVariant(1f, 1f, 0f, MusouFlinchMode.SideLeft), away);
        var right = MusouOrkVariety.FlinchDirection(new MusouFlinchVariant(1f, 1f, 0f, MusouFlinchMode.SideRight), away);
        Assert.Equal(0f, Num.Vector2.Dot(left, away), 3);
        Assert.Equal(-1f, left.X, 3);
        Assert.Equal(1f, right.X, 3);
        Assert.Equal(away, MusouOrkVariety.FlinchDirection(new MusouFlinchVariant(1f, 1f, 0f, MusouFlinchMode.BackAndForth), away));
    }

    [Fact]
    public void A_back_and_forth_flinch_swings_out_then_back_and_never_past_the_kicks_size()
    {
        var v = new MusouFlinchVariant(1f, 1f, 0f, MusouFlinchMode.BackAndForth);
        var track = new MusouOrkFlinchTrack();
        track.Kick(new Num.Vector2(0f, 1f), 1f, v);
        float most = 0f, least = 0f;
        while (track.Step(1f / 240f)) { float y = track.Current().Y; most = MathF.Max(most, y); least = MathF.Min(least, y); }
        Assert.InRange(most, 0.98f, 1.0001f);
        Assert.InRange(least, -0.6f, -0.2f);   // the rebound
        Assert.Equal(Num.Vector2.Zero, track.Current());
    }

    [Fact]
    public void A_delayed_and_stretched_flinch_starts_late_and_ends_late_and_the_unvaried_one_is_the_original()
    {
        var plain = new MusouOrkFlinchTrack(); plain.Kick(new Num.Vector2(1f, 0f), 1f);
        var none = new MusouOrkFlinchTrack(); none.Kick(new Num.Vector2(1f, 0f), 1f, MusouFlinchVariant.None);
        for (int i = 0; i < 90; i++) { bool a = plain.Step(1f / 240f), b = none.Step(1f / 240f); Assert.Equal(a, b); Assert.Equal(plain.Current(), none.Current()); }

        var v = new MusouFlinchVariant(1f, 1.4f, 0.06f, MusouFlinchMode.Away);
        var late = new MusouOrkFlinchTrack(); late.Kick(new Num.Vector2(1f, 0f), 1f, v);
        for (int i = 0; i < 12; i++) late.Step(1f / 240f);   // 50 ms: still in its delay
        Assert.Equal(Num.Vector2.Zero, late.Current());
        float t = 0.05f; while (late.Step(1f / 240f)) t += 1f / 240f;
        Assert.InRange(t, 0.06f + MusouOrkFlinchRules.Seconds * 1.4f - 0.01f, 0.06f + MusouOrkFlinchRules.Seconds * 1.4f + 0.01f);
    }

    [Fact]
    public void A_throw_keeps_the_blasts_direction_within_five_degrees_and_its_impulse_within_30_percent()
    {
        var wave = new Num.Vector3(6f, 3f, 9f);
        float baseAngle = MathF.Atan2(wave.Y, wave.X);
        float lowest = 9f, highest = 0f;
        for (int id = 1; id <= 4000; id++)
        {
            var t = MusouOrkVariety.Throw(id, wave);
            float turn = MathF.Abs(MathF.Atan2(t.Y, t.X) - baseAngle) * 180f / MathF.PI;
            Assert.True(turn <= MusouOrkVariety.ThrowTurnDegrees + 0.01f, $"id {id} turned {turn}");
            float k = t.Z / wave.Z;
            Assert.InRange(k, 0.7f - 1e-4f, 1.3f + 1e-4f);
            Assert.Equal(k, new Num.Vector2(t.X, t.Y).Length() / new Num.Vector2(wave.X, wave.Y).Length(), 3);   // the flat and the up part scale together
            lowest = MathF.Min(lowest, k); highest = MathF.Max(highest, k);
        }
        Assert.True(lowest < 0.75f && highest > 1.25f);   // the whole band is used
    }

    [Fact]
    public void The_throws_start_times_spread_over_up_to_100_ms()
    {
        var delays = Enumerable.Range(1, 2000).Select(MusouOrkVariety.ThrowDelay).ToList();
        Assert.All(delays, d => Assert.InRange(d, 0f, 0.1f));
        Assert.True(delays.Max() > 0.09f && delays.Min() < 0.01f);
        Assert.True(MusouOrkVariety.ThrowDelayMax <= 0.1f);
    }

    [Fact]
    public void Death_clips_are_picked_by_id_among_those_the_rig_has_and_all_are_used()
    {
        Assert.Equal(0, MusouOrkVariety.DeathClip(77, 1));
        Assert.Equal(0, MusouOrkVariety.DeathClip(77, 0));
        var counts = new int[3];
        for (int id = 1; id <= 3000; id++) counts[MusouOrkVariety.DeathClip(id, 3)]++;
        Assert.All(counts, c => Assert.InRange(c, 800, 1200));
    }

    [Fact]
    public void The_unvaried_variant_is_the_original_flinch_and_Classic_never_reaches_this_code()
    {
        // Classic PvP (Annihilation, Seize Ground) never reaches it: the type lives in Extras/Musou, reached from the Musou crowd and death hand-out only.
        Assert.Equal(MusouFlinchMode.Away, MusouFlinchVariant.None.Mode);
        Assert.Equal(1f, MusouFlinchVariant.None.Strength);
        Assert.Equal(1f, MusouFlinchVariant.None.Duration);
        Assert.Equal(0f, MusouFlinchVariant.None.Delay);
        Assert.Equal("SmNet.Extras.Musou", typeof(MusouOrkVariety).Namespace);
    }
}
