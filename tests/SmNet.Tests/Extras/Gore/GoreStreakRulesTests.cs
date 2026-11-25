using System;
using System.Linq;
using System.Numerics;
using SmClient.ImpactRules;
using SmSim;
using Xunit;
using static SmClient.ImpactRules.GoreStreakRules;

namespace SmNet.Tests;

/// <summary>gore-streak (owner 2026-11-25): the engine-free rules of the blood streak — the per-frame table, who draws it, its size, its plane, its grit and its colour.</summary>
public class GoreStreakRulesTests
{
    static readonly byte[] GritTex = GritTexels();
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
    public void Frame_zero_is_a_small_swipe_short_on_the_entry_side_and_the_spray_spreads_through_frame_two()
    {
        Assert.True(Keys[0].Entry < Keys[0].Exit);
        Assert.True(Keys[0].Branches < 2f);                          // one swipe (and a hairline)
        Assert.True(Keys[1].Exit > Keys[0].Exit && Keys[2].Exit >= Keys[1].Exit);
        Assert.True(Keys[1].Fan > 10f && Keys[2].Fan > 10f);      // fanned out on the plane
        Assert.True(Keys[2].Width > Keys[0].Width);
        for (int i = 0; i < Keys.Length; i++) Assert.True(Keys[i].Entry < Keys[i].Exit, $"frame {i}: entry shorter than exit");
    }

    [Fact]
    public void From_frame_three_the_outer_parts_fade_it_grows_again_at_four_while_fading_and_then_fades_out()
    {
        Assert.Equal(0f, Keys[2].Outer);
        Assert.True(Keys[3].Outer > 0f);
        Assert.True(Keys[4].Exit >= Keys[3].Exit && Keys[4].Width > Keys[3].Width && Keys[4].Fan >= Keys[3].Fan, "frame 4 grows again");
        Assert.True(Keys[4].Opacity < Keys[3].Opacity, "while still fading");
        for (int i = 4; i < Keys.Length - 1; i++)
        {
            Assert.True(Keys[i + 1].Opacity <= Keys[i].Opacity, $"opacity falls after {i}");
            Assert.True(Keys[i + 1].Fill <= Keys[i].Fill, $"fill falls after {i}");
            Assert.True(Keys[i + 1].Grit >= Keys[i].Grit, $"pinholes grow after {i}");
        }
        // it fades by erosion (fill, the covered area), not by translucency: a covered texel stays opaque enough to read red on every renderer
        // (Compatibility blends in gamma space: a faint texel over the stripped ground read near black and dropped out of the red mask)
        Assert.True(Keys[^1].Fill <= 0.1f);
        Assert.True(Keys.All(k => k.Opacity >= 0.6f));
    }

    [Fact]
    public void Sample_hits_the_keys_interpolates_between_them_and_is_empty_outside_the_life()
    {
        for (int i = 0; i < Keys.Length; i++) Assert.Equal(Keys[i].Exit, Sample(Keys[i].T).Exit, 4);
        var mid = Sample(0.025f);
        Assert.Equal((Keys[0].Exit + Keys[1].Exit) / 2f, mid.Exit, 3);
        Assert.Equal(0f, Sample(-0.01f).Opacity);
        Assert.Equal(0f, Sample(LifeSeconds).Opacity);
        Assert.True(Sample(1.05f).Opacity < Keys[^1].Opacity);
        Assert.Equal(0f, Sample(float.NaN).Opacity);
    }

    // ---- the shape ----

    [Fact]
    public void The_drawn_spray_reaches_farther_on_the_exit_side_and_its_area_rises_to_frame_two_then_dips_at_three_regrows_at_four_and_fades()
    {
        float[] area = new float[Keys.Length], exit = new float[Keys.Length], entry = new float[Keys.Length];
        for (int f = 0; f < Keys.Length; f++)
            for (int seed = 1; seed <= 4; seed++)
            {
                var m = Measure(Keys[f].T + 1e-4f, seed, 0f, 0f, 3, GritTex, GritSize, 0.025f);
                area[f] += m.Area / 4f; exit[f] += m.ExitReach / 4f; entry[f] += m.EntryReach / 4f;
            }
        for (int f = 0; f <= 8; f++) Assert.True(entry[f] < exit[f], $"frame {f}: entry {entry[f]:F2} < exit {exit[f]:F2}");
        Assert.True(area[0] < area[1] && area[1] < area[2], $"grows: {area[0]:F3} {area[1]:F3} {area[2]:F3}");
        Assert.True(area[3] < area[2], $"dips at 3: {area[3]:F3} < {area[2]:F3}");
        Assert.True(area[4] > area[3], $"regrows at 4: {area[4]:F3} > {area[3]:F3}");
        for (int f = 5; f < Keys.Length - 1; f++) Assert.True(area[f + 1] < area[f], $"fades after {f}: {area[f + 1]:F3} < {area[f]:F3}");
        Assert.True(area[10] < 0.1f * area[2]);
    }

    [Fact]
    public void A_heavy_or_lethal_streak_has_a_fourth_branch_and_more_area()
    {
        float three = 0f, four = 0f;
        for (int seed = 1; seed <= 4; seed++)
        {
            three += Measure(0.2f, seed, 0f, 0f, 3, GritTex, GritSize, 0.025f).Area;
            four += Measure(0.2f, seed, 0f, 0f, 4, GritTex, GritSize, 0.025f).Area;
        }
        Assert.True(four > three);
    }

    [Fact]
    public void The_shape_stays_inside_the_quad()
    {
        var k = Keys.OrderByDescending(x => x.Exit).First();
        float maxFan = Keys.Max(x => x.Fan) * 1.15f * MathF.PI / 180f;
        Assert.True(k.Exit * 1.12f <= QuadFwd + 1e-3f);
        Assert.True(Keys.Max(x => x.Entry) * 1.12f <= QuadBack + 1e-3f);
        Assert.True(MathF.Sin(maxFan) * k.Exit + k.Width * 1.2f <= QuadSide);
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
        Assert.Equal(1.4f, Scale(Weight.Normal, true), 4);
    }

    [Theory]
    [InlineData(Source.Gunfire, "sm_mp_ranged_lascannon", false, 0, Weight.Heavy)]
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

    // ---- grit ----

    [Fact]
    public void The_grit_is_deterministic_tileable_and_spread()
    {
        var again = GritTexels();
        Assert.Equal(GritTex, again);
        Assert.Equal(GritSize * GritSize * 4, GritTex.Length);
        for (int ch = 0; ch < 3; ch++)
        {
            // the wrap: the sample just past the right edge equals the left edge's continuation
            var (a0, b0, c0) = GritSample(GritTex, GritSize, 0.001f, 0.37f);
            var (a1, b1, c1) = GritSample(GritTex, GritSize, 1.001f, 0.37f);
            Assert.Equal(a0, a1, 4); Assert.Equal(b0, b1, 4); Assert.Equal(c0, c1, 4);
            int lo = 0, hi = 0;
            for (int i = ch; i < GritTex.Length; i += 4) { if (GritTex[i] < 64) lo++; if (GritTex[i] > 192) hi++; }
            Assert.True(lo > GritTex.Length / 4 / 20 && hi > GritTex.Length / 4 / 20, $"channel {ch} spread: {lo} low, {hi} high");
        }
        // the wrap in the texels themselves: no seam (the neighbour across the edge differs no more than inside)
        int seam = 0, inside = 0;
        for (int y = 0; y < GritSize; y++)
        {
            seam += Math.Abs(GritTex[(y * GritSize + GritSize - 1) * 4] - GritTex[(y * GritSize) * 4]);
            inside += Math.Abs(GritTex[(y * GritSize + 100) * 4] - GritTex[(y * GritSize + 101) * 4]);
        }
        Assert.True(seam < inside * 2 + GritSize * 4, $"seam {seam} vs inside {inside}");
    }

    // ---- colour ----

    [Theory]
    [InlineData(1f, 1f, 1f)]          // white
    [InlineData(0.55f, 0.75f, 1f)]    // light blue armour
    [InlineData(4f, 0.4f, 0.2f)]      // an HDR red glow / a power weapon's flash
    [InlineData(0.5f, 0.5f, 0.5f)]    // the grey backdrop
    [InlineData(0.05f, 0.05f, 0.05f)] // dark
    public void Inside_a_streak_the_colour_is_red_or_darker_never_white_or_pink(float r, float g, float b)
    {
        foreach (float dens in new[] { 0.55f, 0.8f, 1f })
            foreach (float op in new[] { 0.05f, 0.5f, 1f })
            {
                if (op < 0.9f) continue;   // the rule is for covered texels of the dense spray; faint texels blend with the stripped ground
                var c = Composite((r, g, b), 1f, dens, op);
                Assert.True(c.G <= 0.15f * c.R + 1e-4f && c.B <= 0.15f * c.R + 1e-4f, $"({c.R:F3},{c.G:F3},{c.B:F3}) over ({r},{g},{b}) dens {dens}");
                Assert.True(c.R <= MathF.Max(ClassicBloodRules.BloodRgb.R * ClassicBloodRules.RibbonShadeTop, r * ClassicBloodRules.RibbonStripRed) + 1e-4f);
            }
        var faint = Composite((r, g, b), 1f, 0.55f, 0.05f);   // a fading texel: the ground stripped of green and blue shows through, still no pink
        Assert.True(faint.G <= 0.15f * faint.R + 1e-4f || faint.R < 0.02f);
    }

    [Fact]
    public void The_blood_colour_is_the_darker_red()
    {
        foreach (float d in new[] { 0f, 0.5f, 1f })
        {
            var c = Colour(d);
            Assert.True(ClassicBloodRules.IsBloodOnly(c, ClassicBloodRules.BloodRgb.R));
            Assert.True(c.R <= 0.56f);
        }
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
}
