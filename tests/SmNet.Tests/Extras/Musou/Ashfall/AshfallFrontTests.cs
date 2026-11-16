using System.Numerics;
using SmContent.Extras.Musou;
using SmNet;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// The Musou front pilot (docs/musou-ashfall-graybox.md "Front pilot"; a test map beside draft 13): lane B's three positions as three bands (wasteland, outskirts, urban core), each its own 480 m battlefield built from a band
/// profile after its routes and capture points passed gate A on the empty frame, and gate B on the final collision; the three composed side by side in one world (<c>tfw_war_front</c>). From the generator alone (no asset bytes,
/// no install). The slow cases (every band over five seeds through the kit pass) take a few minutes.
/// </summary>
public class AshfallFrontTests(ITestOutputHelper output)
{
    /// <summary>The bands' level pins (front version 1, the default seed). After a change of the generator or a refit of the kit's collision, take the new values from this test's output.</summary>
    public static readonly ulong[] PinnedBandHashes = [0x1C0E74F030BD9527UL, 0xC301BB5479451A7AUL, 0xDC77FE44619EF92DUL];

    public static readonly TheoryData<uint> Seeds = new() { AshfallFront.DefaultSeed, 1u, 2u, 3u, 4u };

    static bool Through(AshfallFieldStructure s) => s.Name.StartsWith("through ", StringComparison.Ordinal);

    // ---------------------------------------------------------------- the profiles

    [Fact]
    public void The_band_profiles_carry_the_plans_density_rules()
    {
        var (w, o, c) = (AshfallBandProfile.Wasteland, AshfallBandProfile.Outskirts, AshfallBandProfile.Core);
        Assert.Equal([w, o, c], AshfallBandProfile.Front);
        Assert.True(w.Fabric == AshfallFabric.Islands && o.Fabric == AshfallFabric.Islands && c.Fabric == AshfallFabric.Rows);
        Assert.Equal((0.05f, 0.08f), (w.CoverageMin, w.CoverageMax));
        Assert.Equal((0.20f, 0.24f), (o.CoverageMin, o.CoverageMax));
        Assert.Equal((0.34f, 0.38f), (c.CoverageMin, c.CoverageMax));
        Assert.Equal(0f, w.UpperFloorShare); Assert.Equal(0.10f, o.UpperFloorShare);
        Assert.True(w.LowMin == 1.2f && w.LowMax == 2.2f && w.HeightMax <= 8f);
        Assert.True(o.HeightMin == 10f && o.HeightMax == 13f && o.SideStreet == 12f);
        Assert.True(c.HeightMin == 13f && c.HeightMax == 26f && c.TallHeight == 44f && c.Plazas == 3 && c.BreachMin == 30f && c.BreachMax == 50f);
        foreach (var p in AshfallBandProfile.Front)
            Assert.True(p.CorridorWidth == 24f && p.MinArmyWidth == 20f && p.ObjectiveClear == 16f && p.Approaches == 2 && p.ApproachSeparation == 60f && p.Landmarks.Length > 0);
        Assert.Same(o, w.Next); Assert.Same(c, o.Next); Assert.Same(c, c.Next);
        Assert.Equal(0f, AshfallBandProfile.BlendAt(0f)); Assert.Equal(0f, AshfallBandProfile.BlendAt(144f)); Assert.Equal(1f, AshfallBandProfile.BlendAt(240f));
        Assert.Equal(0, AshfallFront.BandForNode(1)); Assert.Equal(1, AshfallFront.BandForNode(2)); Assert.Equal(2, AshfallFront.BandForNode(3)); Assert.Equal(0, AshfallFront.BandForNode(0));
    }

    // ---------------------------------------------------------------- gate A and the fabric

    [Theory, MemberData(nameof(Seeds))]
    public void Every_band_passes_gate_A_and_its_fabric_keeps_off_the_routes_points_and_protected_areas(uint seed)
    {
        for (int band = 0; band < AshfallFront.BandCount; band++)
        {
            var plan = seed == AshfallFront.DefaultSeed ? AshfallFront.Default(band) : AshfallFront.Plan(band, seed);
            var prof = AshfallBandProfile.Front[band];
            output.WriteLine($"seed {seed} band {band} ({prof.Name}): {plan.Notes[0]}");
            output.WriteLine($"  {plan.Structures.Count} structures, cover {plan.Coverage:P1} of {plan.FreeLand / 10000f:0.0} ha (target {prof.CoverageMin:P0}-{prof.CoverageMax:P0})");
            Assert.Equal(band, plan.Band);
            Assert.Equal(AshfallFront.LayoutOf(band), plan.Layout);
            Assert.All(plan.GateA, r => Assert.True(r.Passed, r.ToString()));
            Assert.Contains(plan.GateA, r => r.Name.StartsWith("main avenue at the south edge", StringComparison.Ordinal));
            Assert.Contains(plan.GateA, r => r.Name.StartsWith("frame: through road", StringComparison.Ordinal));
            Assert.Equal(3, plan.Points.Count);
            // the main avenue: 24 m at x = 0 from edge to edge
            Assert.Contains(plan.Corridors, k => k.Name == "main avenue through the base" && k.A.X == 0f && k.Box().Cy - k.Box().Length / 2f <= -238f);
            Assert.Contains(plan.Corridors, k => k.Name == "main avenue through the stronghold" && k.A.X == 0f && k.Box().Cy + k.Box().Length / 2f >= 238f);
            // nothing of the fabric meets a corridor, a point's clear zone or a protected area (the through pieces are the authored exception)
            foreach (var s in plan.Structures.Where(s => !Through(s)))
            {
                var r = AshfallField.Footprint(s.Centre, s.Size, s.YawStep);
                Assert.DoesNotContain(plan.Corridors, k => AshfallField.RectMeetsBox(r, k.Box()));
                foreach (var (name, c, rad) in plan.ClearZones)
                {
                    float dx = MathF.Max(MathF.Max(r.X - c.X, c.X - r.Z), 0f), dy = MathF.Max(MathF.Max(r.Y - c.Y, c.Y - r.W), 0f);
                    Assert.True(dx * dx + dy * dy >= rad * rad, $"{s.Name} in point {name}'s clear zone");
                }
                foreach (var (name, z) in plan.Protected) Assert.False(r.X < z.Z && r.Z > z.X && r.Y < z.W && r.W > z.Y, $"{s.Name} in {name}");
                Assert.True(r.X >= -226f && r.Z <= 226f && r.Y >= -226f && r.W <= 226f, s.Name);
            }
            Assert.True(plan.Coverage > prof.CoverageMin * 0.6f && plan.Coverage <= prof.CoverageMax + 0.02f, $"cover {plan.Coverage:P1}");
            Assert.Equal(7, plan.Structures.Count(Through));
            if (band == 2) Assert.Contains(plan.Structures, s => s.Name.Contains(" breached block ", StringComparison.Ordinal));
            // deterministic
            var again = AshfallFront.Plan(band, seed);
            Assert.Equal(plan.Structures, again.Structures);
            Assert.Equal(plan.Points, again.Points);
        }
    }

    [Fact]
    public void Gate_A_names_the_rule_a_bad_frame_breaks()
    {
        var prof = AshfallBandProfile.Wasteland;
        var bad = new AshfallFront.Skeleton(0, 0, 150f, -96f, 40f, -20f, 84f);   // B's clear zone runs into the Ork assembly area
        var frame = AshfallFront.FramePlan(prof, bad, AshfallFront.DefaultSeed);
        var rules = AshfallFront.GateA(frame, AshfallMap.BuildFrontFrame(frame));
        var failed = rules.Where(r => !r.Passed).ToList();
        foreach (var f in failed) output.WriteLine(f.ToString());
        Assert.Contains(failed, r => r.Name == "point B: clear zone off the frame and the protected areas" && r.Value.Contains("Ork assembly", StringComparison.Ordinal));
        var outside = AshfallFront.FramePlan(prof, bad with { CrossY = 0f, FlankX = 215f }, AshfallFront.DefaultSeed);
        Assert.Contains(AshfallFront.GateA(outside, AshfallMap.BuildFrontFrame(outside)), r => !r.Passed && r.Name == "point A: clear zone off the frame and the protected areas" && r.Value == "outside the square");
    }

    // ---------------------------------------------------------------- gate B (the final collision)

    [Theory, MemberData(nameof(Seeds))]
    public void Every_band_passes_gate_B_on_its_final_collision(uint seed)
    {
        for (int band = 0; band < AshfallFront.BandCount; band++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var d = seed == AshfallFront.DefaultSeed ? AshfallMap.BuildFrontBand(band) : AshfallMap.RebuildFront(band, seed);
            var gate = d.Dressing.Flags.Single(f => f.StartsWith("front gate B (", StringComparison.Ordinal));
            output.WriteLine($"seed {seed} band {band}: {watch.ElapsedMilliseconds} ms, hash {d.Level.Hash:X16}, {d.Level.SolidCorners().Length / 3} collision triangles, {d.Dressing.Items.Count(i => i.Role == AshfallDressRole.Prop)} props; {gate}");
            Assert.Contains(": all pass;", gate);
            var rules = d.Dressing.Flags.Where(f => f.StartsWith("front gate B rule ", StringComparison.Ordinal)).ToList();
            Assert.True(rules.Count >= 45);
            Assert.All(rules, r => Assert.StartsWith("front gate B rule [pass]", r));
            Assert.Contains(rules, r => r.Contains("through road (x = 0, edge to edge)", StringComparison.Ordinal));
            // the through variants: draft 10's keep and the ring's gate are gone, the keep's two wings stand
            Assert.DoesNotContain(d.Placements, p => p.Item >= 0 && p.Group is "keep" or "ring gate" or "compound north wall west" or "compound south wall");
            Assert.Contains(d.Placements, p => p.Item >= 0 && p.Group == "through keep wing west");
            Assert.Contains(d.Placements, p => p.Item >= 0 && p.Group == "through keep wing east");
            Assert.Equal(AshfallFront.LayoutOf(band), d.Level.Layout);
        }
    }

    // ---------------------------------------------------------------- pins, determinism, today's maps untouched

    [Fact]
    public void The_default_bands_are_deterministic_and_pinned_and_draft_13_stays_the_default()
    {
        for (int band = 0; band < AshfallFront.BandCount; band++)
        {
            var d = AshfallMap.BuildFrontBand(band);
            output.WriteLine($"band {band} hash 0x{d.Level.Hash:X16}UL");
            Assert.Equal(d.Level.Hash, AshfallMap.RebuildFront(band, AshfallFront.DefaultSeed).Level.Hash);
            if (PinnedBandHashes[band] != 0UL) Assert.Equal(PinnedBandHashes[band], d.Level.Hash);
            Assert.NotEqual(AshfallFront.HashVersionOf(band), AshfallHeights.HashVersion);
        }
        Assert.Equal(AshfallLayout.Heights, TfwMap.LayoutOf(TfwMap.Name));
        Assert.Equal(AshfallDraft13Tests.PinnedHeightsHash, AshfallMap.BuildDraft5(AshfallLayout.Heights).Level.Hash);
        Assert.Equal(AshfallDraft12Tests.PinnedCityHash, AshfallMap.BuildDraft5(AshfallLayout.City).Level.Hash);
    }
}
