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
        // the gate repair's fade (lead 41): opacity about 0.65 / 0.5 / 0.4 / 0.25 / 0.12 / 0.05 over frames 5 to 10, the area eroding with it
        float[] fade = [0.65f, 0.5f, 0.4f, 0.25f, 0.12f, 0.05f];
        for (int i = 0; i < fade.Length; i++) Assert.Equal(fade[i], Keys[5 + i].Opacity, 2);
        Assert.True(Keys[^1].Fill <= 0.15f);
    }

    [Fact]
    public void The_entry_stub_stays_under_about_half_the_exit_and_hangs_in_place()
    {
        for (int i = 0; i < Keys.Length; i++) Assert.True(Keys[i].Entry <= 0.56f * Keys[i].Exit, $"frame {i}: entry {Keys[i].Entry} vs exit {Keys[i].Exit}");
        for (int i = 3; i < 8; i++) Assert.True(MathF.Abs(Keys[i].Entry - Keys[2].Entry) <= 0.1f, $"frame {i}: the stub does not slide outward");
    }

    [Fact]
    public void Frames_one_to_five_fan_out_widely_with_an_upward_branch_and_the_root_holds_through_frame_four()
    {
        for (int i = 1; i <= 5; i++) Assert.InRange(Keys[i].Fan * BranchShares[0].Angle, 17f, 45f);
        Assert.True(BranchShares[0].Angle > 0f && BranchShares[2].Angle < 0f, "branches on both sides of the axis");
        for (int i = 0; i <= 3; i++) Assert.Equal(1f, Keys[i].Root);
        Assert.True(Keys[^1].Root == 0f);
    }

    [Fact]
    public void Every_branch_leaves_the_wound_as_one_solid_mass_in_frames_zero_to_four()
    {
        // along each exit branch and the stub, the first fifth of its reach is covered (the root term), whatever the grit
        for (int f = 0; f <= 4; f++)
            for (int seed = 1; seed <= 4; seed++)
            {
                var k = Keys[f];
                for (int b = 0; b < 3; b++)
                {
                    if (Math.Clamp(k.Branches - b, 0f, 1f) < 0.99f) continue;
                    var (ang, reach, _) = Branch(b, seed, k.Fan, 0f);
                    for (float u = 0.03f; u <= 0.2f; u += 0.04f)
                    {
                        float s = u * k.Exit * reach;
                        var (cov, _, _) = Coverage(k, s * MathF.Cos(ang), s * MathF.Sin(ang), seed, 0f, 0f, 3, GritTex, GritSize);
                        Assert.True(cov > 0.8f, $"frame {f} seed {seed} branch {b} at {u:F2}: cov {cov:F2}");
                    }
                }
            }
    }

    [Fact]
    public void Edges_are_feathered_and_a_soft_halo_lies_under_the_cores()
    {
        Assert.True(Feather >= 0.03f);
        var k = Keys[2];
        var p = Field(k, 0.8f, 0.8f * MathF.Tan(Branch(0, 1, k.Fan, 0f).Angle) + k.Width * 1.2f, 1, 0f, 0f, 3);
        Assert.True(p.Halo > 0f, "the halo reaches past the core's edge");
        Assert.True(HaloAlpha * k.Opacity < 0.3f, "the halo is low alpha");
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
        // no covered or halo texel outside the quad at any key (sampled just outside its edges), and the quad no larger than it needs (finding M2)
        foreach (var k in Keys)
            for (int seed = 1; seed <= 3; seed++)
                for (float t = -1f; t <= 1f; t += 0.05f)
                {
                    foreach (var (x, y) in new[] { (QuadFwd + 0.01f, t * QuadSide), (-QuadBack - 0.01f, t * QuadSide), (t * QuadFwd, QuadSide + 0.01f), (t * QuadFwd, -QuadSide - 0.01f) })
                    {
                        var p = Field(k, x, y, seed, 0f, 0f, 4);
                        Assert.True(p.Field <= 0f && p.Halo <= 0.01f, $"key {k.T}: blood at ({x:F2},{y:F2})");
                    }
                }
        Assert.True((QuadFwd + QuadBack) * 2f * QuadSide < 13f, $"quad {QuadFwd + QuadBack:F2} x {2f * QuadSide:F2}");
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
        foreach (float shade in new[] { CompatibilityShade, ForwardShade })
            foreach (float dens in new[] { 0.45f, 0.8f, 1f })
            {
                // a dense covered texel: only the blood's own green and blue
                var c = Composite((r, g, b), 0.95f, 1f, dens, shade);
                Assert.True(c.G <= 0.15f * c.R + 1e-4f && c.B <= 0.15f * c.R + 1e-4f, $"({c.R:F3},{c.G:F3},{c.B:F3}) over ({r},{g},{b}) dens {dens}");
                // a soft texel (the halo, a fading core, 0.3 opacity and up): stripped toward red, never pink (the scoring tools' pink: min(G,B)/R over 0.7)
                foreach (float alpha in new[] { 0.3f, 0.5f })
                {
                    var f = Composite((r, g, b), alpha, 0.2f, dens, shade);
                    Assert.True(MathF.Min(f.G, f.B) <= 0.7f * f.R + 1e-4f || f.R < 0.02f, $"alpha {alpha}: ({f.R:F3},{f.G:F3},{f.B:F3}) over ({r},{g},{b})");
                }
            }
    }

    [Fact]
    public void The_strip_fades_with_the_opacity_so_a_faint_texel_never_goes_near_black()
    {
        Assert.Equal(0f, Strip(0f, 1f));
        Assert.Equal(1f, Strip(0.5f, 1f));
        Assert.True(Strip(0.1f, 1f) < 0.55f);
        var grey = Composite((0.5f, 0.5f, 0.5f), 0.1f, 1f, 0.8f);
        Assert.True(grey.R > 0.25f, $"a faint texel over grey keeps most of the grey's level: {grey.R:F3}");
    }

    [Fact]
    public void The_blood_colour_is_our_darker_red_at_the_same_level_on_both_renderers()
    {
        foreach (float d in new[] { 0f, 0.5f, 1f })
        {
            var c = Colour(d);
            Assert.True(ClassicBloodRules.IsBloodOnly(c, ClassicBloodRules.BloodRgb.R));
            Assert.True(c.R <= 0.56f);
        }
        // the absolute level (gate repair 8, owner: keep our darker red, about (150, 0, 0) on screen): Compatibility shows the albedo as is, Forward+ through linear-to-display
        // (its tonemapper and exposure lifted the earlier albedo to about (229, 20, 0); ForwardShade brings it back)
        float compat = Colour(0.6f, CompatibilityShade).R * 255f, forward = ToSrgb(Colour(0.6f, ForwardShade).R) * 255f;
        Assert.InRange(compat, 90f, 170f);
        Assert.InRange(forward, 90f, 170f);
        Assert.True(MathF.Abs(compat - forward) < 40f, $"compat {compat:F0} vs forward {forward:F0}");
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
    public void A_spent_slot_is_retired_and_the_unit_is_capped()
    {
        Assert.False(Spent(double.NegativeInfinity, 100.0));
        Assert.False(Spent(10.0, 10.5));
        Assert.True(Spent(10.0, 10.0 + LifeSeconds));
        var big = new Streak(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, 2.33f, Scale(Weight.Heavy, true), 1, 0f, 0f, true);
        Assert.Equal(MaxUnitMetres, big.Unit);
        var normal = new Streak(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, 2f, 1f, 1, 0f, 0f, false);
        Assert.Equal(2f, normal.Unit);
    }

    [Fact]
    public void Warfronts_bucket_refills_per_second_up_to_its_burst_and_an_enemy_waits_between_streaks()
    {
        var b = new Bucket(WarfrontPerSecond, WarfrontBurst);
        int taken = 0;
        for (int i = 0; i < 100; i++) if (b.Take(0.0)) taken++;
        Assert.Equal((int)WarfrontBurst, taken);
        Assert.False(b.Take(0.0));
        Assert.True(b.Take(1.0 / WarfrontPerSecond + 1e-6));
        int second = 0;
        for (double t = 2.0; t < 3.0; t += 1.0 / 240.0) if (b.Take(t)) second++;
        Assert.InRange(second, (int)WarfrontPerSecond - 2, (int)(WarfrontPerSecond + WarfrontBurst) + 1);
        Assert.False(EnemyReady(1.0, 1.1));
        Assert.True(EnemyReady(1.0, 1.0 + WarfrontEnemySeconds));
        Assert.True(WarfrontLiveMax <= 150);
    }
}
