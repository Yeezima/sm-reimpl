using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SmClient.ImpactRules;
using SmSim;
using Xunit;
using Xunit.Abstractions;
using static SmClient.ImpactRules.GoreStreakRules;

namespace SmNet.Tests;

/// <summary>
/// gore-streak (owner 2026-11-25; ribbon rebuild 2026-11-26): the engine-free rules of the blood streak - the per-frame table, the ribbons (band layout, geometry, timing, the
/// fragment's filament texture and erosion), who draws it, its size, its plane, its colour and Warfront's budget.
/// </summary>
public class GoreStreakRulesTests(ITestOutputHelper output)
{
    static readonly byte[] Tex = FilamentTexels();
    static readonly Vector3 Up = Vector3.UnitY;

    // ---- the table ----

    [Fact]
    public void The_table_has_eleven_frames_at_the_reference_key_times()
    {
        float[] t = [0f, 0.05f, 0.10f, 0.18f, 0.27f, 0.37f, 0.48f, 0.60f, 0.72f, 0.85f, 0.98f];
        Assert.Equal(11, Keys.Length);
        for (int i = 0; i < t.Length; i++) Assert.Equal(t[i], Keys[i].T, 3);
        Assert.True(LifeSeconds > Keys[^1].T && LifeSeconds <= 1.2f);
    }

    [Fact]
    public void Frame_zero_is_one_thin_swipe_short_on_the_entry_side_then_the_bands_grow_and_spread_through_frame_two()
    {
        Assert.True(Keys[0].Entry < Keys[0].Exit);
        Assert.Equal(1f, Keys[0].Branches);                           // one swipe: no side band yet
        Assert.True(Keys[0].Exit <= 1.65f);                            // frame 0: a short swipe (its tapered, eroded tip reads about 1.25 H)
        Assert.True(Keys[1].Exit > Keys[0].Exit && Keys[2].Exit >= Keys[1].Exit);
        Assert.True(Keys[2].Width > 1.8f * Keys[0].Width);           // a narrow swipe, then wide bands
        Assert.True(Keys[2].Fan > Keys[0].Fan && Keys[2].Fan is >= 8f and <= 30f);   // the upper band rising off the main (12 to 15 degrees on screen at the reference's oblique view; ours 20 in the plane)
        Assert.True(Grow(Keys[2]) >= 1f && Grow(Keys[0]) == 0f);
        for (int i = 0; i < Keys.Length; i++) Assert.True(Keys[i].Entry <= 0.56f * Keys[i].Exit, $"frame {i}: entry at most about 0.55 of the exit");
    }

    [Fact]
    public void From_frame_three_the_outer_parts_fade_four_grows_again_while_fading_then_the_bands_break_into_patches_and_fade_out()
    {
        Assert.True(Keys[3].Outer > 0f && Keys[2].Outer == 0f);
        Assert.True(Keys[4].Exit > Keys[3].Exit && Keys[4].Width > Keys[3].Width && Keys[4].Fan > Keys[3].Fan);
        Assert.True(Keys[4].Opacity < Keys[3].Opacity);
        for (int i = 1; i < Keys.Length; i++)
        {
            Assert.True(Keys[i].Opacity <= Keys[i - 1].Opacity, $"frame {i}: opacity never rises");
            Assert.True(Keys[i].Fill <= Keys[i - 1].Fill, $"frame {i}: the erosion never heals");
            Assert.True(Keys[i].Exit >= Keys[i - 1].Exit - 1e-4f, $"frame {i}: the blood hangs in place, the reach never shrinks");
        }
        Assert.True(Keys[5].Fill > 0.6f && Keys[6].Fill < 0.6f);    // mostly whole bands through frame 5 (a few large tears), patches from frame 6
        Assert.True(Keys[^1].Fill < 0.12f);
        Assert.Equal(0f, Sample(LifeSeconds).Opacity);
        Assert.Equal(0f, Sample(-0.01f).Opacity);
        Assert.True(Sample(1.05f).Opacity < Keys[^1].Opacity);
    }

    [Fact]
    public void Sample_hits_the_keys_and_interpolates_between_them()
    {
        for (int i = 0; i < Keys.Length; i++) Assert.Equal(Keys[i].Exit, Sample(Keys[i].T).Exit, 4);
        var mid = Sample(0.075f);
        Assert.Equal((Keys[1].Width + Keys[2].Width) / 2f, mid.Width, 4);
    }

    // ---- the ribbons ----

    [Fact]
    public void Each_band_has_its_own_angle_length_and_width_main_longest_upper_rising_entry_behind_and_short()
    {
        for (int seed = 1; seed < 40; seed++)
            foreach (int f in new[] { 2, 4, 5 })
            {
                var k = Keys[f];
                var main = BandOf(Band.Main, k, seed, 0f);
                var upper = BandOf(Band.Upper, k, seed, 0f);
                var entry = BandOf(Band.Entry, k, seed, 0f);
                var lower = BandOf(Band.Lower, k, seed, 0f);
                Assert.True(main.Length > upper.Length && main.Length > entry.Length && main.Length > lower.Length, $"seed {seed} f{f}: the main band is the longest");
                Assert.InRange(upper.Length / main.Length, 0.45f, 0.75f);                    // about 0.6 of the main
                Assert.True(entry.Length <= 0.6f * main.Length, $"seed {seed} f{f}: entry {entry.Length:F2} vs main {main.Length:F2}");
                float up = (upper.Angle - main.Angle) * 180f / MathF.PI;
                Assert.InRange(up, 8f, 60f);                                                 // the fan's top (the half-fan the scoring tools measure is about half of it)
                Assert.True(MathF.Abs(main.Angle) * 180f / MathF.PI < 14f);                 // the main band on the attack axis (it leans a little away from the upper)
                var mid = BandOf(Band.Mid, k, seed, 0f);
                var low = BandOf(Band.LowMid, k, seed, 0f);
                Assert.True(mid.Angle > main.Angle && mid.Angle < upper.Angle && low.Angle < main.Angle, $"seed {seed} f{f}: a bundle between the upper band and below the main");
                Assert.True(mid.Length < main.Length && low.Length < main.Length && mid.Length > upper.Length);
                Assert.InRange(MathF.Abs(entry.Angle) * 180f / MathF.PI, 172f, 188f);        // behind the wound
                Assert.True(lower.Angle < main.Angle);                                         // the fourth band below
                Assert.True(upper.HalfWidth < main.HalfWidth && entry.HalfWidth <= 1.4f * main.HalfWidth);   // the entry: a short, broad stub
                Assert.True(upper.Root > 0f && entry.Root == 0f);                             // the upper band leaves the main just past the wound
            }
    }

    [Fact]
    public void Frame_zero_draws_only_the_swipe_and_the_side_bands_grow_in_by_frame_two()
    {
        var f0u = BandOf(Band.Upper, Keys[0], 3, 0f);
        Assert.Equal(0f, f0u.Length);
        Assert.Equal(0f, f0u.Alpha);
        var f1u = BandOf(Band.Upper, Keys[1], 3, 0f);
        var f2u = BandOf(Band.Upper, Keys[2], 3, 0f);
        Assert.True(f1u.Length > 0f && f2u.Length > f1u.Length && f2u.HalfWidth > f1u.HalfWidth);
        // the upper band fades ahead of the main between frames 2 and 4; the entry band is gone by frame 9
        Assert.True(BandOf(Band.Upper, Keys[4], 3, 0f).Alpha < BandOf(Band.Main, Keys[4], 3, 0f).Alpha);
        Assert.True(BandOf(Band.Entry, Keys[9], 3, 0f).Alpha < 0.2f);
        Assert.Equal(1f, BandOf(Band.Entry, Keys[5], 3, 0f).Alpha);
    }

    [Fact]
    public void A_ribbon_runs_from_its_root_along_its_angle_tapers_at_both_ends_and_bends_with_the_arc()
    {
        var b = BandOf(Band.Upper, Keys[4], 5, 0f);
        var (x0, y0) = RibbonPoint(b, 0f, 0f);
        Assert.Equal(b.Root, x0, 4); Assert.Equal(b.Across, y0, 4);
        Assert.True(b.Across > 0f);   // the bundle: the upper band's root sits above the axis
        var (x1, y1) = RibbonPoint(b, 1f, 0f);
        Assert.Equal(b.Length, MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)), 3);
        Assert.Equal(b.Angle, MathF.Atan2(y1 - y0, x1 - x0), 3);
        float W(float u) { var (ax, ay) = RibbonPoint(b, u, -1f); var (bx, by) = RibbonPoint(b, u, 1f); return MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)); }
        Assert.True(W(0f) < W(0.65f) && W(1f) < W(0.65f));
        Assert.Equal(2f * b.HalfWidth * EdgePad, W(0.65f), 3);
        // a bent band's middle leaves the straight line
        var bent = BandOf(Band.Main, Keys[4], 5, 0.4f);
        var (mx, my) = RibbonPoint(bent, 0.5f, 0f);
        var straight = BandOf(Band.Main, Keys[4], 5, 0f);
        var (sx, sy) = RibbonPoint(straight, 0.5f, 0f);
        Assert.True(MathF.Abs(my - sy) + MathF.Abs(mx - sx) > 0.05f);
    }

    [Fact]
    public void The_drawn_streak_follows_the_reference_area_and_fan_curves_and_kills_are_bigger_and_longer()
    {
        // face-on area in H^2 (the scoring tools' reference: 0.07 at frame 0, 0.25 to 0.37 in frames 1 to 5 peaking at 2, dipping at 3, regrowing at 4, about 0.006 at frame 10);
        // half-fan (reference 5 at frame 0, 17 at 1, 22 to 31 in frames 2 to 8; on screen the plane's slant narrows it a little)
        var area = new float[Keys.Length];
        var fan = new float[Keys.Length];
        for (int f = 0; f < Keys.Length; f++)
        {
            float sum = 0f, ex = 0f, en = 0f, al = 0f, hf = 0f, fl = 0f, op = 0f; int pa = 0;
            for (int seed = 1; seed <= 4; seed++)
            {
                var m = Measure(Keys[f].T, seed, 0f, false, Tex, FilamentSize);
                sum += m.Area; ex += m.ExitReach; en += m.EntryReach; pa += m.MainPatches; al += m.MeanAlpha; hf += m.HalfFan; fl += m.Fill; op += m.Opaque;
            }
            area[f] = sum / 4f; fan[f] = hf / 4f;
            output.WriteLine($"f{f}: area {area[f]:F3} exit {ex / 4f:F2} entry {en / 4f:F2} ratio {en / MathF.Max(ex, 1e-3f):F2} half-fan {fan[f]:F1} fill {fl / 4f:F2} main patches {pa / 4f:F1} mean alpha {al / 4f:F2} opaque {op / 4f:F2}");
        }
        Assert.InRange(area[0], 0.03f, 0.13f);
        for (int f = 1; f <= 5; f++) Assert.InRange(area[f], 0.18f, 0.55f);   // face-on; the plane's slant shows about 0.8 of it
        Assert.True(area[2] > area[1] && area[3] < area[2] && area[4] > area[3], "peak at 2, dip at 3, regrow at 4");
        for (int f = 6; f < Keys.Length; f++) Assert.True(area[f] < area[f - 1], $"frame {f}: falling");
        Assert.True(area[10] < 0.03f);
        Assert.True(fan[0] < 10f);
        for (int f = 2; f <= 8; f++) Assert.InRange(fan[f], 20f, 34f);
        var m0 = Measure(Keys[0].T, 2, 0f, false, Tex, FilamentSize);
        Assert.InRange(m0.ExitReach, 1.0f, 1.7f);   // geometric; on screen the tapered tip reads about 1.2 to 1.3 H
        Assert.True(m0.EntryReach < 0.6f * m0.ExitReach);
        // the exit holds near its reach through frame 9 (the tip hold), then falls
        var m9 = Measure(Keys[9].T, 2, 0f, false, Tex, FilamentSize);
        Assert.True(m9.ExitReach > 0.6f * Keys[9].Exit, $"frame 9 exit {m9.ExitReach:F2}");
        // from frame 6 the main band breaks into patches
        int patches = 0;
        for (int seed = 1; seed <= 6; seed++) patches += Measure(Keys[6].T, seed, 0f, false, Tex, FilamentSize).MainPatches;
        Assert.InRange(patches / 6f, 2f, 9f);
        // a kill: a sixth band and a bigger area in table units; its unit is 1.4 times longer (Streak.Unit), so it is bigger and longer in metres
        var kill = Measure(Keys[4].T, 2, 0f, true, Tex, FilamentSize, new Streak(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, 2f, Scale(Weight.Normal, true), 2, 0f, 0f, true).WidthMul);
        var hit = Measure(Keys[4].T, 2, 0f, false, Tex, FilamentSize);
        Assert.True(kill.Area > hit.Area);
        // gore round 4: bigger on a kill, but not the 2.2 times the reference's area gate 4 measured (metres squared: table area x unit squared)
        float killUnit = new Streak(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, 2f, Scale(Weight.Normal, true), 2, 0f, 0f, true).Unit, hitUnit = 2f;
        float ratio = kill.Area * killUnit * killUnit / (hit.Area * hitUnit * hitUnit);
        output.WriteLine($"kill area ratio {ratio:F2}");
        Assert.InRange(ratio, 1.2f, 1.9f);
        // translucent throughout (gate 4: 64 to 89 % of the streak's pixels fully opaque in frames 0 to 5; the reference translucent)
        for (int f = 0; f <= 5; f++) Assert.True(Measure(Keys[f].T, 3, 0f, false, Tex, FilamentSize).Opaque < 0.3f, $"frame {f}: mostly translucent");
        Assert.True(new Streak(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, 2f, Scale(Weight.Normal, true), 2, 0f, 0f, true).Unit >= 1.15f * new Streak(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, 2f, 1f, 2, 0f, 0f, false).Unit);
    }

    [Fact]
    public void The_spray_is_a_soft_translucent_body_with_a_ragged_edge_and_large_jagged_tears()
    {
        // gore round 4 (owner 2026-11-26: translucent allowed; "more spread out instead of locked into straight hard lines"; "large jagged gaps the way the reference does")
        var k = Keys[2];
        var b = BandOf(Band.Main, k, 4, 0f);
        float centre = 0f, edge = 0f; int n = 0, capped = 0, shown = 0;
        for (float u = 0.35f; u < 0.75f; u += 0.005f)
        {
            var c = TexelAt(k, b, Band.Main, u, 0.05f, 4, Tex, FilamentSize);
            var e = TexelAt(k, b, Band.Main, u, 0.85f, 4, Tex, FilamentSize);
            centre += c.Alpha; edge += e.Alpha; n++;
            if (c.Alpha >= 0.98f * AlphaMax) capped++;
            if (Shows(c.Alpha)) shown++;
            Assert.True(c.Alpha <= AlphaMax + 1e-5f, "translucent: the ground always shows through");
        }
        output.WriteLine($"centre {centre / n:F2} edge {edge / n:F2} capped {capped}/{n} shown {shown}/{n}");
        Assert.True(centre / n > 1.6f * edge / n, $"a soft body: thicker in the middle ({centre / n:F2}) than at the edge ({edge / n:F2})");
        Assert.InRange(centre / n, 0.3f, 0.75f);                         // translucent, not a solid strip
        Assert.True(capped < 0.35f * n, $"mostly below the cap ({capped}/{n})");
        Assert.True(shown > 0.6f * n, "the body shows along its middle (the tears take the rest)");
        Assert.Equal(0f, TexelAt(k, b, Band.Main, 0.5f, EdgePad, 4, Tex, FilamentSize).Alpha, 2);   // past the ragged edge: nothing
        Assert.True(MeanderWidth > 0f && Meander(b, 0f) == 0f && MathF.Abs(Meander(b, 1f)) < 1e-4f);  // the middle line waves, not at the root or the tip

        // the ragged edge: where the body ends moves along the band (not a straight line)
        var ends = new List<float>();
        for (float u = 0.3f; u < 0.8f; u += 0.01f)
        {
            float last = 0f;
            for (float v = 0f; v < EdgePad; v += 0.02f) if (TexelAt(k with { Fill = 1f }, b, Band.Main, u, v, 4, Tex, FilamentSize).Body > 0.25f) last = v;
            ends.Add(last);
        }
        float lo = ends.Min(), hi = ends.Max();
        output.WriteLine($"edge {lo:F2}..{hi:F2}");
        Assert.True(hi - lo > 0.3f, $"a ragged, lumpy edge ({lo:F2}..{hi:F2})");

        // large jagged tears, not pinholes: at frame 5 the main band's middle line breaks into a few long runs with long gaps between
        var k5 = Keys[5];
        int runs = 0, gaps = 0, longestGap = 0, gap = 0; bool on = true;
        int samples = 0;
        for (int seed = 1; seed <= 6; seed++)
        {
            var b5 = BandOf(Band.Main, k5, seed, 0f);
            on = true; gap = 0;
            for (int i = 0; i < 200; i++)
            {
                float u = 0.32f + 0.6f * i / 200f;
                bool s = TexelAt(k5, b5, Band.Main, u, 0f, seed, Tex, FilamentSize).Keep > 0.5f;
                samples++;
                if (s && !on) { runs++; longestGap = Math.Max(longestGap, gap); gap = 0; }
                if (!s) { if (on) gaps++; gap++; }
                on = s;
            }
        }
        output.WriteLine($"frame 5 tears: {gaps} gaps over 6 seeds, longest {longestGap} of 200 samples");
        Assert.InRange(gaps / 6f, 0.5f, 6f);                             // a few tears per band, not dozens of pinholes
        Assert.True(longestGap >= 12, $"a large gap ({longestGap} samples, about {longestGap * 0.6f * k5.Exit / 200f:F2} H)");
    }

    [Fact]
    public void The_filament_texture_is_deterministic_tileable_and_spread()
    {
        Assert.Equal(Tex, FilamentTexels());
        Assert.Equal(FilamentSize * FilamentSize * 4, Tex.Length);
        var (a0, b0, c0) = TexSample(Tex, FilamentSize, 0.001f, 0.37f);
        var (a1, b1, c1) = TexSample(Tex, FilamentSize, 1.001f, 0.37f);
        Assert.Equal(a0, a1, 4); Assert.Equal(b0, b1, 4); Assert.Equal(c0, c1, 4);
        for (int ch = 0; ch < 3; ch++)
        {
            int lo = 0, hi = 0;
            for (int i = ch; i < Tex.Length; i += 4) { if (Tex[i] < 64) lo++; if (Tex[i] > 192) hi++; }
            Assert.True(lo > Tex.Length / 4 / 20 && hi > Tex.Length / 4 / 40, $"channel {ch} spread: {lo} low, {hi} high");
        }
        int seam = 0, inside = 0;
        for (int y = 0; y < FilamentSize; y++)
        {
            seam += Math.Abs(Tex[(y * FilamentSize + FilamentSize - 1) * 4] - Tex[(y * FilamentSize) * 4]);
            inside += Math.Abs(Tex[(y * FilamentSize + 100) * 4] - Tex[(y * FilamentSize + 101) * 4]);
        }
        Assert.True(seam < inside * 2 + FilamentSize * 4, $"seam {seam} vs inside {inside}");
        // gore round 4: soft flow streaks several times longer than wide (never beads or cells), tears far larger than the grain
        Assert.InRange(FlowAlongH / FlowAcrossH, 4f, 9f);
        Assert.True(GapAlongH / 12f >= 0.3f && GapAcrossH / 12f >= 0.08f, "tear blotches about 0.4 H by 0.12 H");
        Assert.True(GrainAlongH < GapAlongH / 4f && GrainAcrossH < GapAcrossH / 4f);
    }

    [Fact]
    public void The_shader_carries_the_table_and_the_band_layouts()
    {
        // the shader source is built from these tables (GoreStreakView.ShaderCode); a spot check that the numbers the tests rely on are the ones it draws with
        Assert.Equal(6, Bands.Length);
        Assert.Equal(MaxBranches, Bands.Length);
        Assert.True(Segments >= 8);
        Assert.Equal(5, BandCount(false));
        Assert.Equal(6, BandCount(true));
        Assert.False(BandOn(Band.Lower, false));
        Assert.True(BandOn(Band.Lower, true) && BandOn(Band.Mid, false) && BandOn(Band.LowMid, false));

    }

    // ---- who draws it ----

    [Fact]
    public void Warfront_and_extended_draw_it_faithful_and_musou_alone_never_and_the_switches_turn_it_off()
    {
        Assert.False(Draws(ContentExtras.None, false));
        Assert.True(Draws(ContentExtras.Orks | ContentExtras.Gore | ContentExtras.LoyalistBarks | ContentExtras.Autocannon | ContentExtras.Weather, false));
        Assert.True(Draws(ContentExtras.Gore, false));
        Assert.False(Draws(ContentExtras.Orks, false));
        Assert.False(Draws(ContentExtras.Musou, false));
        Assert.True(Draws(ContentExtras.Musou | ContentExtras.Warfront, false));
        Assert.False(Draws(ContentExtras.Musou | ContentExtras.Warfront, oldLook: true));
        Assert.False(Draws(ContentExtras.Gore, oldLook: false, off: true));
    }

    [Theory]
    [InlineData("sm_mp_ranged_plasma_gun", null, true)]
    [InlineData("csm_mp_pistol_plasma_pistol", null, true)]
    [InlineData("plasma_cannon", null, true)]
    [InlineData("sm_mp_ranged_melta", null, true)]
    [InlineData("some_flamer", null, true)]
    [InlineData("anything", DeliveryKind.Cone, true)]
    [InlineData("sm_mp_ranged_bolter", DeliveryKind.Raycast, false)]
    [InlineData("sm_mp_ranged_lascannon", null, false)]
    [InlineData("sm_mp_melee_chainsword", null, false)]
    [InlineData(null, null, false)]
    public void Plasma_melta_and_cone_weapons_play_no_streak(string? wargear, DeliveryKind? delivery, bool excluded) =>
        Assert.Equal(excluded, Excluded(wargear, delivery));

    // ---- size ----

    [Fact]
    public void Same_shape_scaled_by_weapon_and_bigger_on_a_kill()
    {
        Assert.Equal(0.75f, Scale(Weight.Light, false));
        Assert.Equal(1f, Scale(Weight.Normal, false));
        Assert.Equal(1.35f, Scale(Weight.Heavy, false));
        foreach (var w in Enum.GetValues<Weight>()) Assert.True(Scale(w, true) > Scale(w, false));
        Assert.True(Scale(Weight.Heavy, true) <= MaxScale);
        Assert.Equal(LethalScale, Scale(Weight.Normal, true), 4);
        Assert.InRange(LethalScale, 1.15f, 1.35f);   // gore round 4: bigger on a kill, not twice the area
        // gore round 4: an explosion's fan narrower (frame 1 about 17 deg like the reference, not 29), gunfire's a little; melee the whole fan
        Assert.True(FanShareOf(Source.Explosion) < FanShareOf(Source.Gunfire) && FanShareOf(Source.Gunfire) < FanShareOf(Source.Sweep));
        Assert.Equal(1f, FanShareOf(Source.Sweep));
        Assert.True(BandOf(Band.Upper, Keys[2], 3, 0f, 1f, 1f, ExplosionFan).Angle < BandOf(Band.Upper, Keys[2], 3, 0f).Angle);
    }

    [Theory]
    [InlineData(Source.Gunfire, "sm_mp_ranged_lascannon", false, 0, Weight.Heavy)]
    [InlineData(Source.Gunfire, "sm_mp_exotic_las_cannon", false, 0, Weight.Heavy)]   // the live data name (repair round 3: it was drawn Normal)
    [InlineData(Source.Gunfire, "sm_mp_ranged_heavy_bolter", false, 0, Weight.Heavy)]
    [InlineData(Source.Gunfire, "autocannon", false, 0, Weight.Heavy)]
    [InlineData(Source.Gunfire, "sm_mp_pistol_bolt_pistol", false, 0, Weight.Light)]
    [InlineData(Source.Gunfire, "sm_mp_ranged_bolter", false, 0, Weight.Normal)]
    [InlineData(Source.Gunfire, "csm_mp_ranged_sniper_rifle", false, 0, Weight.Heavy)]
    [InlineData(Source.Gunfire, "sm_mp_ranged_sniper_rifle", false, 0, Weight.Heavy)]
    [InlineData(Source.Explosion, null, false, 0, Weight.Heavy)]
    [InlineData(Source.Charge, null, false, 0, Weight.Heavy)]
    [InlineData(Source.Sweep, "sm_mp_melee_thunder_hammer", false, 2, Weight.Heavy)]
    [InlineData(Source.Sweep, "sm_mp_melee_power_axe", false, 2, Weight.Heavy)]
    [InlineData(Source.Sweep, "csm_mp_melee_chain_axe", false, 2, Weight.Normal)]
    [InlineData(Source.Sweep, "sm_mp_melee_chainsword", true, 2, Weight.Heavy)]
    [InlineData(Source.Sweep, "sm_mp_melee_chainsword", false, 1, Weight.Light)]
    [InlineData(Source.Thrust, "sm_mp_melee_combat_knife", false, 3, Weight.Light)]
    [InlineData(Source.Sweep, "sm_mp_melee_chainsword", false, 2, Weight.Normal)]
    public void Weight_classes(Source src, string? wargear, bool heavy, int step, Weight want) => Assert.Equal(want, WeightOf(src, wargear, heavy, step));

    // ---- the plane ----

    static void Perpendicular(Vector3 n, Vector3 axis)
    {
        Assert.Equal(1f, n.Length(), 3);
        Assert.True(MathF.Abs(Vector3.Dot(n, Vector3.Normalize(axis))) < 1e-3f, $"normal {n} not perpendicular to {axis}");
    }

    [Fact]
    public void A_sweep_lies_in_its_swing_plane()
    {
        var axis = Vector3.UnitX;
        var before = Vector3.Normalize(new Vector3(1f, 0.3f, -0.6f)); var after = Vector3.Normalize(new Vector3(1f, -0.3f, 0.6f));
        var n = Plane(Source.Sweep, axis, Up, 1, before, after);
        Perpendicular(n, axis);
        Assert.True(MathF.Abs(Vector3.Dot(n, Vector3.Normalize(Vector3.Cross(before, after)))) > 0.9f);
        // a tilted swing gives a tilted plane, not the level or the vertical
        Assert.True(MathF.Abs(n.Y) > 0.2f && MathF.Abs(n.Y) < 0.98f);
        // no tangents: the vertical plane through the axis
        Perpendicular(Plane(Source.Sweep, axis, Up, 1), axis);
        Assert.Equal(0f, Plane(Source.Sweep, axis, Up, 1).Y, 3);
    }

    [Fact]
    public void Shots_thrusts_and_blasts_are_rolled_by_their_seed_never_exactly_vertical_and_stable()
    {
        var axis = Vector3.Normalize(new Vector3(1f, 0.1f, 0.4f));
        for (int seed = 1; seed < 50; seed++)
            foreach (var src in new[] { Source.Gunfire, Source.Thrust, Source.Explosion, Source.Charge })
            {
                var n = Plane(src, axis, Up, seed);
                Perpendicular(n, axis);
                var vertical = Vector3.Normalize(Vector3.Cross(axis, Up));
                float ang = MathF.Acos(Math.Clamp(MathF.Abs(Vector3.Dot(n, vertical)), 0f, 1f)) * 180f / MathF.PI;
                Assert.InRange(ang, RollDegrees / 3f - 0.1f, RollDegrees + 0.1f);
                Assert.Equal(n, Plane(src, axis, Up, seed));
            }
    }

    [Fact]
    public void An_explosion_sprays_outward_from_the_blast_lifted_fifteen_degrees()
    {
        var a = ExplosionAxis(new Vector3(3f, 1.2f, 0f), new Vector3(0f, 0.2f, 0f), Up, 1);
        Assert.Equal(1f, a.Length(), 3);
        Assert.True(a.X > 0.9f);
        Assert.Equal(MathF.Sin(ExplosionLiftDegrees * MathF.PI / 180f), a.Y, 3);
        var under = ExplosionAxis(new Vector3(0f, 1.2f, 0f), new Vector3(0f, 0f, 0f), Up, 7);   // a blast at his feet: a seeded level direction, lifted
        Assert.Equal(MathF.Sin(ExplosionLiftDegrees * MathF.PI / 180f), under.Y, 3);
    }

    [Fact]
    public void The_camera_bias_keeps_the_plane_through_the_axis_and_never_edge_on()
    {
        var axis = Vector3.UnitX;
        var edgeOn = Vector3.UnitY;                        // a level plane seen from the side: edge-on
        var toCam = Vector3.Normalize(new Vector3(0.2f, 0.1f, 1f));
        var n = CameraBias(edgeOn, axis, toCam);
        Perpendicular(n, axis);
        Assert.True(MathF.Abs(Vector3.Dot(n, toCam)) >= MinFacing - 1e-3f);
        var facing = CameraBias(Vector3.UnitZ, axis, toCam);
        Assert.True(Vector3.Dot(facing, toCam) > 0.9f);
        Assert.Equal(edgeOn, CameraBias(edgeOn, axis, axis));   // looking straight down the axis: unchanged
    }


    // ---- colour ----

    [Theory]
    [InlineData(1f, 1f, 1f)]          // white
    [InlineData(0.85f, 0.85f, 0.85f)] // light grey
    [InlineData(0.55f, 0.75f, 1f)]    // light blue armour
    [InlineData(1f, 0.9f, 0.6f)]      // a bright warm light / a power weapon's flash on screen
    [InlineData(0.57f, 0.565f, 0.55f)]// the grey backdrop as drawn
    [InlineData(0.3f, 0.45f, 0.25f)]  // an Ork's green
    [InlineData(0.05f, 0.05f, 0.05f)] // dark
    public void Inside_a_streak_the_colour_is_red_or_pink_never_white(float r, float g, float b)
    {
        // gore round 4 (owner 2026-11-26: "Allow translucent and opaque that is pink"): a translucent red over any ground stays red or pink - its red above its green and blue by a
        // visible margin wherever the blood shows (from the mask's opacity up), its blue never above its green by much (red to salmon, not purple), never white; dimly and brightly lit
        // a ground drawn bright on screen is brightly lit, so is the blood in front of it; a dark one may be dim; the density follows the opacity (the dense streaks are the opaque ones)
        float[] lights = MathF.Max(r, MathF.Max(g, b)) > 0.5f ? [SceneLight, 1.6f * SceneLight] : [0.5f * SceneLight, SceneLight, 1.6f * SceneLight];
        foreach (float light in lights)
            foreach (float a in new[] { MaskAlpha, 0.3f, 0.55f, AlphaMax })
                foreach (float dens in new[] { 0f, a / AlphaMax })
                {
                    var f = Composite((r, g, b), a, dens, light);
                    var under = (r, g, b);
                    string at = $"alpha {a:F2} dens {dens} light {light}: ({f.R:F3},{f.G:F3},{f.B:F3}) over ({r},{g},{b})";
                    // redder than the ground: the red's lead over green and blue grows with the opacity
                    float lead = f.R - MathF.Max(f.G, f.B), leadUnder = r - MathF.Max(g, b);
                    bool neutral = MathF.Abs(r - g) < 0.05f && MathF.Abs(g - b) < 0.05f;   // white, grey or black: the blood alone makes the hue
                    if (neutral) Assert.True(lead > leadUnder + (a >= 0.45f ? 0.04f : 0.01f), "redder than the ground: " + at);
                    else Assert.True(f.G / MathF.Max(f.R, 1e-3f) < g / r - (a >= 0.3f ? 0.02f : 0.005f), "redder than the coloured ground: " + at);
                    if (neutral && a >= 0.3f)
                    {
                        Assert.True(lead >= 0.035f, "never white or grey (a faint edge over white reads a pale pink): " + at);
                        Assert.True(MathF.Min(f.G, f.B) <= 0.9f || lead >= 0.08f, "never near white: " + at);
                        Assert.True(MathF.Max(f.G, f.B) <= (a >= 0.55f ? 0.93f : 0.96f) * f.R, "red or pink: " + at);
                    }
                    Assert.True(ToLinear(f.B) - ToLinear(f.G) <= MathF.Max(0f, ToLinear(b) - ToLinear(g)) * (1f - a) + 0.005f, "red to salmon, not purple: " + at);
                }
    }

    [Fact]
    public void Over_the_grey_backdrop_the_blood_matches_the_references_translucent_salmon_red()
    {
        // the reference's streak pixels over its grey (scoring tools' red mask): median about (158, 113, 110), the 10th and 90th percentiles about (110, 72, 70) and (185, 122, 120);
        // green over red about 0.7 (ours: a translucent red over the ground). The model leaves out the tonemapper; the captures check the drawn colour.
        var grey = (0.57f, 0.565f, 0.55f);
        var thin = Composite(grey, 0.45f, 0.1f, SceneLight);
        var dense = Composite(grey, 0.75f, 0.9f, SceneLight);
        output.WriteLine($"thin ({thin.R * 255:F0},{thin.G * 255:F0},{thin.B * 255:F0}) dense ({dense.R * 255:F0},{dense.G * 255:F0},{dense.B * 255:F0})");
        Assert.InRange(thin.R * 255f, 135f, 200f);
        Assert.InRange(thin.G / thin.R, 0.45f, 0.8f);
        Assert.InRange(dense.R * 255f, 70f, 150f);
        Assert.InRange(dense.G / dense.R, 0.3f, 0.75f);
        Assert.True(dense.R < thin.R, "the dense streaks deeper than the thin spray");
        // the ground shows through: the same texel over white is lighter (pink), over black darker; never the same solid red over everything
        var white = Composite((1f, 1f, 1f), 0.45f, 0.1f, SceneLight);
        var black = Composite((0.02f, 0.02f, 0.02f), 0.45f, 0.1f, SceneLight);
        Assert.True(white.G > thin.G + 0.1f && black.G < thin.G - 0.1f);
        var none = Composite(grey, 0f, 0.5f);                                // nothing drawn: the ground as it was
        Assert.True(MathF.Abs(none.R - grey.Item1) < 1e-4f && MathF.Abs(none.G - grey.Item2) < 1e-4f && MathF.Abs(none.B - grey.Item3) < 1e-4f);
    }

    [Fact]
    public void The_blood_colour_is_a_red_from_salmon_to_deep_and_lit_by_the_scene()
    {
        foreach (float d in new[] { 0f, 0.5f, 1f })
        {
            var c = Colour(d);
            Assert.True(c.G <= 0.3f * c.R && c.B <= c.G, $"a red albedo ({c.R:F2},{c.G:F2},{c.B:F2})");
            Assert.True(c.R * SceneLight <= 1f, "never brighter than the light on it (no glow of its own)");
        }
        Assert.True(Colour(1f).R < Colour(0f).R);
        Assert.InRange(LightWrap, 0.2f, 0.8f);
        // the measured scene light (gore round 4 fix, gate 5: a lit Warfront level measured 0.35 while its sun was hidden): floored at 0.8, at most 3, eased over about a second
        Assert.Equal(SceneLightMin, LightOf(0.35f, 0f));
        Assert.InRange(SceneLightMin, 0.7f, 0.9f);
        Assert.InRange(LightOf(0.66f, 1.62f), 1.85f, 1.9f);   // the capture map's ambient and sun
        Assert.Equal(SceneLightMax, LightOf(5f, 5f));
        float e = 0.8f; for (int i = 0; i < 60; i++) e = EaseLight(e, 1.8f, 1f / 60f);   // one second at 60 fps
        Assert.InRange(e, 1.35f, 1.55f);                                                  // most of the way, not a jump
        Assert.True(EaseLight(0.8f, 1.8f, 1f / 60f) < 0.83f, "no popping in one frame");
        Assert.Equal(1.8f, EaseLight(0.8f, 1.8f, 30f), 3);
        // lit: a dim level draws it darker (exposure-aware: no glow on a dark map)
        var grey = (0.3f, 0.3f, 0.3f);
        Assert.True(Composite(grey, 0.6f, 0.3f, 0.3f * SceneLight).R < Composite(grey, 0.6f, 0.3f, SceneLight).R - 0.1f);
    }

    // ---- pool and budget ----

    [Fact]
    public void The_pool_is_a_ring_and_a_streak_lives_its_life()
    {
        Assert.Equal(0, PoolSlot(0));
        Assert.Equal(PoolSize - 1, PoolSlot(PoolSize - 1));
        Assert.Equal(0, PoolSlot(PoolSize));
        Assert.True(Live(10.0, 10.5));
        Assert.False(Live(10.0, 10.0 + LifeSeconds));
        Assert.True(Live(10.2, 10.0));   // waiting for a tracer: still pending
        Assert.Equal(108f, ProjectedPixels(1.8f, 10f, 2f * MathF.Atan(0.5f) * 180f / MathF.PI, 600f), 1);
    }

    [Fact]
    public void One_streak_per_victim_bursts_hold_it_heavier_hits_and_kills_restart_it_bigger()
    {
        // nothing showing: a new streak
        Assert.Equal(Join.New, JoinOf(double.NegativeInfinity, double.NegativeInfinity, false, 10.0, false));
        Assert.Equal(Join.New, JoinOf(10.0, 10.9, false, 10.0 + LifeSeconds, false));   // the old one is spent
        // a hit within a quarter second of the last joins the running streak (no restart)
        Assert.Equal(Join.Merge, JoinOf(10.0, 10.0, false, 10.1, false, 1f, 1f));
        Assert.Equal(Join.Merge, JoinOf(10.0, 10.6, false, 10.8, false, 1f, 1f));          // sustained fire: still the first streak
        // gore round 4 (gate-4 finding): hits 0.25 to 1.1 s apart never stack several streaks on one victim: held while the streak is young, else replaced in its slot
        Assert.Equal(Join.Merge, JoinOf(10.0, 10.0, false, 10.0 + MergeSeconds, false, 1f, 1f));
        Assert.Equal(Join.Merge, JoinOf(10.0, 10.0, false, 10.0 + HoldUntilAge - 0.01, false, 1f, 1f));
        Assert.Equal(Join.Upgrade, JoinOf(10.0, 10.0, false, 10.0 + HoldUntilAge + 0.05, false, 1f, 1f));
        Assert.Equal(Join.Upgrade, JoinOf(10.0, 10.3, false, 11.0, false, 1f, 1f));
        // a kill on a running non-killing streak takes it over, bigger; any hit after a kill joins the kill's streak
        Assert.Equal(Join.Upgrade, JoinOf(10.0, 10.0, false, 10.1, true, 1f, 1.25f));
        Assert.Equal(Join.Merge, JoinOf(10.0, 10.0, true, 10.1, false, 1.25f, 1f));
        Assert.Equal(Join.Merge, JoinOf(10.0, 10.0, true, 10.1, true, 1.25f, 1.25f));
        Assert.Equal(Join.Ignore, JoinOf(10.0, 10.0, true, 10.9, false, 1.25f, 1.35f));    // a kill's streak is never replaced, even by a heavier hit
        // gate-5 finding: fire into a corpse holds the kill's streak only for KillHoldSeconds after the kill (its last hit stays the kill's time), then it fades
        Assert.Equal(Join.Merge, JoinOf(10.0, 10.0, true, 10.0 + KillHoldSeconds - 0.01, false, 1.25f, 1f));
        Assert.Equal(Join.Ignore, JoinOf(10.0, 10.0, true, 10.0 + KillHoldSeconds + 0.01, false, 1.25f, 1f));
        Assert.InRange(KillHoldSeconds, 0.3, 0.7);
        double ks = 10.0;
        for (double t = 10.1; t < 11.0; t += 0.1)
        {
            var j = JoinOf(ks, 10.0, true, t, false, 1.25f, 1f);
            if (j == Join.Merge) ks = HoldStart(ks, t);
        }
        Assert.True(11.0 - ks > HoldAge + 0.3, "after the hold the kill's streak ages and fades");
        // a heavier joining hit restarts it at its own, bigger size (as a kill does); a lighter or equal one merges
        Assert.Equal(Join.Upgrade, JoinOf(10.0, 10.0, false, 10.1, false, 1f, HeavyScale));
        Assert.Equal(Join.Merge, JoinOf(10.0, 10.0, false, 10.1, false, HeavyScale, 1f));
        // a merged streak is held back to frame 4 (it grows again while fading) and never made younger than it is
        Assert.Equal(10.5 - HoldAge, HoldStart(10.0, 10.5), 6);
        Assert.Equal(10.4, HoldStart(10.4, 10.5), 6);
        Assert.Equal(10.6, HoldStart(10.6, 10.5), 6);                                     // a tracer still in flight keeps its start
        // a burst at 0.1 s spacing over 2 s: one streak, its age never past frame 5
        double start = 0.0, last = 0.0;
        for (double t = 0.1; t < 2.0; t += 0.1)
        {
            Assert.Equal(Join.Merge, JoinOf(start, last, false, t, false, 1f, 1f));
            last = t; start = HoldStart(start, t);
            Assert.True(t - start <= HoldAge + 1e-9);
        }
        // hits every 0.4 s for 3 s on one victim: at most one streak showing at any time (each hit holds or replaces the one slot)
        double s0 = double.NegativeInfinity, l0 = double.NegativeInfinity; int slots = 0;
        for (double t = 0.0; t < 3.0; t += 0.4)
        {
            var j = JoinOf(s0, l0, false, t, false, 1f, 1f);
            if (j == Join.New) slots++;
            s0 = j == Join.Merge ? HoldStart(s0, t) : t; l0 = t;
        }
        Assert.Equal(1, slots);
    }

    [Fact]
    public void A_spent_slot_is_retired_and_heavy_hits_grow_in_width_not_length()
    {
        Assert.False(Spent(double.NegativeInfinity, 100.0));
        Assert.False(Spent(10.0, 10.5));
        Assert.True(Spent(10.0, 10.0 + LifeSeconds));
        Assert.Equal(1.5f, MaxScale);
        Assert.Equal(MaxScale, Scale(Weight.Heavy, true));
        // a heavy hit: length within 1.1, the rest of the scale in width
        var heavy = new Streak(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, 2f, Scale(Weight.Heavy, false), 1, 0f, 0f, false, Heavy: true);
        Assert.Equal(2f * LengthCap, heavy.Unit, 4);
        Assert.Equal(HeavyScale / LengthCap, heavy.WidthMul, 4);
        Assert.True(heavy.Fourth);
        Assert.True(Keys[0].Exit * heavy.LenScale <= 1.75f && Keys[8].Exit * heavy.LenScale <= 3.3f);   // geometric reach (the tapered, eroded tips read about 0.8 of it)
        // a kill: the same layout scaled up to 1.4 in length, a fourth band
        var kill = new Streak(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, 2f, Scale(Weight.Normal, true), 1, 0f, 0f, true);
        Assert.Equal(2f * LethalLengthCap, kill.Unit, 4);
        Assert.Equal(LethalScale / LethalLengthCap, kill.WidthMul, 4);
        Assert.True(kill.Fourth);
        // a normal hit; the unit caps (Classic 3 m, Warfront 2.2 m)
        var normal = new Streak(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, 2f, 1f, 1, 0f, 0f, false);
        Assert.Equal(2f, normal.Unit);
        Assert.False(normal.Fourth);
        var bigOrk = new Streak(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, 3.4f, Scale(Weight.Heavy, true), 1, 0f, 0f, true, UnitCap: WarfrontUnitMetres);
        Assert.Equal(WarfrontUnitMetres, bigOrk.Unit);
        Assert.Equal(MaxUnitMetres, new Streak(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, 3f, 1.4f, 1, 0f, 0f, true).Unit);
    }

    [Fact]
    public void Warfronts_bucket_refills_per_second_up_to_its_burst_and_an_enemy_waits_between_streaks()
    {
        var b = new Bucket(WarfrontPerSecond, WarfrontBurst);
        int taken = 0;
        for (int i = 0; i < 100; i++) if (b.Take(0.0)) taken++;
        Assert.Equal((int)WarfrontBurst, taken);
        Assert.False(b.Take(0.0));
        Assert.False(b.Has(0.0));
        Assert.True(b.Take(1.0 / WarfrontPerSecond + 1e-6));
        Assert.False(EnemyReady(1.0, 1.1));
        Assert.True(EnemyReady(1.0, 1.0 + WarfrontEnemySeconds));
        Assert.Equal(64, WarfrontLiveMax);
        Assert.Equal(48, WarfrontOwnLiveMax);
    }

    [Fact]
    public void Warfront_streaks_admit_own_hits_first_and_nearest_first_within_the_own_window_and_the_live_caps()
    {
        // 12 own hits and 30 others in one frame, the others nearer
        var asks = new List<StreakAsk>();
        for (int i = 0; i < 12; i++) asks.Add(new StreakAsk(true, false, 10f + i, i));
        for (int i = 0; i < 30; i++) asks.Add(new StreakAsk(false, false, 1f + i * 0.1f, 100 + i));
        var bucket = new Bucket(WarfrontPerSecond, WarfrontBurst);
        var got = AdmitStreaks(asks, 0, 0, 0, bucket, 0.0);
        var own = got.Where(i => i < 100).ToList();
        Assert.Equal(WarfrontOwnPerWindow, own.Count);
        Assert.Equal(Enumerable.Range(0, WarfrontOwnPerWindow), own);                    // the nearest own ones, first
        Assert.Equal(got.Take(WarfrontOwnPerWindow), own);                                // own before the others
        Assert.Equal((int)WarfrontBurst, got.Count - own.Count);                          // the others through the bucket
        // a killing own hit wins over a nearer non-killing one
        var lethal = new List<StreakAsk> { new(true, false, 2f, 1), new(true, true, 30f, 2) };
        Assert.Equal(2, AdmitStreaks(lethal, 0, 0, 0, new Bucket(1f, 1f), 0.0)[0]);
        // the caps: the own window spent, the own live cap, the total live cap
        Assert.Empty(AdmitStreaks([new(true, false, 1f, 1)], WarfrontOwnPerWindow, 0, 0, new Bucket(1f, 1f), 0.0));
        Assert.Empty(AdmitStreaks([new(true, false, 1f, 1)], 0, WarfrontOwnLiveMax, 0, new Bucket(1f, 1f), 0.0));
        Assert.Empty(AdmitStreaks([new(true, false, 1f, 1), new(false, false, 1f, 2)], 0, 0, WarfrontLiveMax, new Bucket(1f, 1f), 0.0));
        // the bucket's token is taken last: an own ask over the own cap costs no token, a dropped other ask none either
        var b2 = new Bucket(1f, 1f);
        AdmitStreaks([new(true, false, 1f, 1)], WarfrontOwnPerWindow, 0, 0, b2, 0.0);
        Assert.True(b2.Has(0.0));
        AdmitStreaks([new(false, false, 1f, 1)], 0, 0, WarfrontLiveMax, b2, 0.0);
        Assert.True(b2.Has(0.0));
        // a killing hit is always admitted (repair round 3): past the own window, the own and total live caps, and with the bucket empty; it costs no token
        var empty = new Bucket(1f, 1f); empty.Take(0.0);
        Assert.Equal([5], AdmitStreaks([new(true, true, 1f, 5)], WarfrontOwnPerWindow, WarfrontOwnLiveMax, WarfrontLiveMax, empty, 0.0));
        Assert.Equal([6], AdmitStreaks([new(false, true, 1f, 6)], 0, 0, WarfrontLiveMax, empty, 0.0));
        Assert.False(empty.Has(0.0));
        // the frame's list is reused (no allocation a frame)
        var into = new List<int> { 99 };
        AdmitStreaks([new(false, true, 1f, 7)], 0, 0, 0, new Bucket(1f, 1f), 0.0, into);
        Assert.Equal([7], into);
    }
}
