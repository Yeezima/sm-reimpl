using System.Numerics;
using SmSim.Extras.Musou.Ashfall;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// The kit catalogue's Meshy expansion (draft 6, 36 models of 2026-11-05; docs/musou-ashfall-graybox.md "Draft 6: Meshy expansion (36 models)"): ids, files, measured bounds, design scales and the QA's flags.
/// Numbers only: no model bytes are read here.
/// </summary>
public class AshfallKitCatalogueTests
{
    static readonly string[] SourceIds =
        ["01", "02", "03", "04", "05", "06", "07", "08H", "08L", "08M", "09", "10", "11", "12LA", "12LB", "12LC", "12SA", "12SB", "12SC", "13A", "13B",
         "14A", "14B", "14C", "14D", "14E", "14F", "15A", "15B", "16C", "16E", "16I", "18A", "18B", "18C", "20"];

    static List<AshfallKitModel> Expansion => [.. AshfallKit.Models.Where(m => m.File.StartsWith(AshfallKit.ExpansionFolder + "/", StringComparison.Ordinal))];

    static AshfallKitModel BySource(string s) => Expansion.Single(m => m.SourceId == s);

    [Fact]
    public void Ids_are_unique_and_the_36_models_continue_the_AC_series()
    {
        Assert.Equal(AshfallKit.Models.Count, AshfallKit.Models.Select(m => m.Id).Distinct().Count());
        Assert.Equal(AshfallKit.Models.Count, AshfallKit.Models.Select(m => m.File).Distinct().Count());
        var ex = Expansion;
        Assert.Equal(36, ex.Count);
        Assert.Equal(Enumerable.Range(41, 36).Select(i => $"AC-0{i}"), ex.Select(m => m.Id));
        Assert.Equal(SourceIds, ex.Select(m => m.SourceId));
        foreach (var m in ex)
        {
            Assert.StartsWith(AshfallKit.ExpansionFolder + "/Models/" + m.SourceId + "-", m.File);
            Assert.EndsWith(".glb", m.File);
            Assert.DoesNotContain('\\', m.File);
            Assert.False(m.Review, m.Id);
        }
    }

    [Fact]
    public void The_rejected_previews_are_not_catalogued()
    {
        Assert.Equal(["08H-R1", "03-R1"], AshfallKit.RejectedPreviews);
        foreach (var r in AshfallKit.RejectedPreviews)
            Assert.DoesNotContain(AshfallKit.Models, m => m.SourceId == r || m.File.Contains(r, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Bounds_are_measured_centred_and_normalised_to_about_1_9_units()
    {
        foreach (var m in Expansion)
        {
            var s = m.RawSize;
            Assert.True(s.X > 0f && s.Y > 0f && s.Z > 0f, $"{m.Id}: inverted bounds");
            float longest = MathF.Max(s.X, MathF.Max(s.Y, s.Z));
            Assert.InRange(longest, 1.88f, 1.91f);
            Assert.True(Vector3.Abs(m.RawMin + m.RawMax).Length() < 0.05f, $"{m.Id}: not centred");
            Assert.InRange(m.Triangles, 20_000, 32_000);
        }
    }

    [Fact]
    public void Every_model_has_a_design_scale_and_its_source_is_tagged()
    {
        foreach (var m in Expansion)
        {
            Assert.True(m.DesignScale is > 0.5f and < 20f, $"{m.Id}: design scale {m.DesignScale}");
            Assert.True(m.ScaleSource.StartsWith("catalogue's ", StringComparison.Ordinal) || m.ScaleSource.StartsWith("ours, estimate: ", StringComparison.Ordinal), $"{m.Id}: untagged scale source");
            Assert.False(string.IsNullOrWhiteSpace(m.Family), m.Id);
            Assert.False(string.IsNullOrWhiteSpace(m.Purpose), m.Id);
            if (m.RawRiser > 0f) Assert.True(m.RawRiser * m.DesignScale <= AshfallKitFit.StepHeight, $"{m.Id}: riser {m.RawRiser * m.DesignScale} m at the design scale");
        }
    }

    [Fact]
    public void Design_sizes_meet_the_catalogue_s_dimensions()
    {
        foreach (var s in new[] { "12LA", "12LB", "12LC" }) Assert.Equal(4f, BySource(s).DesignSize.X, 0.02f);
        foreach (var s in new[] { "12SA", "12SB", "12SC" }) Assert.Equal(2f, BySource(s).DesignSize.X, 0.02f);
        // Low cover stays under the level's 1.3 m cover rule; high cover above the 1.6 m eye.
        foreach (var m in Expansion.Where(m => m.SourceId.StartsWith("12"))) Assert.InRange(m.DesignSize.Y, 0.7f, 1.3f);
        foreach (var m in Expansion.Where(m => m.SourceId.StartsWith("13"))) Assert.Equal(2.2f, m.DesignSize.Y, 0.02f);
        foreach (var m in Expansion.Where(m => m.SourceId.StartsWith("14"))) { Assert.Equal(8f, m.DesignSize.X, 0.02f); Assert.Equal(8f, m.DesignSize.Z, 0.02f); }
        Assert.Equal(3f, BySource("07").DesignSize.Y, 0.02f);
        Assert.Equal(6f, BySource("06").RawRiser * 24 * BySource("06").DesignScale, 0.05f);   // 24 risers of 0.25 m to the landing
    }

    [Fact]
    public void The_QA_flags_are_recorded()
    {
        var q = AshfallKitFlags.TraversalQuarantined;
        Assert.Equal(["03", "08H"], Expansion.Where(m => m.Flags.HasFlag(q)).Select(m => m.SourceId));
        Assert.All(Expansion.Where(m => m.Flags.HasFlag(q)), m => Assert.DoesNotContain(m.Id, AshfallKitFit.WalkableIds));
        Assert.Equal(["02", "10"], Expansion.Where(m => m.Flags.HasFlag(AshfallKitFlags.RoofAccessUnverified)).Select(m => m.SourceId));
        Assert.Equal(["01"], Expansion.Where(m => m.Flags.HasFlag(AshfallKitFlags.IntactEnds)).Select(m => m.SourceId));
        Assert.Equal(["12LA", "12LB", "12LC", "12SA", "12SB", "12SC"], Expansion.Where(m => m.Flags.HasFlag(AshfallKitFlags.CoverRatioNotPreserved)).Select(m => m.SourceId));
        Assert.Equal(["08H", "08L", "08M"], Expansion.Where(m => m.Flags.HasFlag(AshfallKitFlags.BareEdges)).Select(m => m.SourceId));
        Assert.Equal(["18C"], Expansion.Where(m => m.Flags.HasFlag(AshfallKitFlags.TerminationOnly)).Select(m => m.SourceId));
        Assert.All(Expansion.Where(m => m.Category is AshfallKitCategory.Ground or AshfallKitCategory.Boundary), m => Assert.True(m.Flags.HasFlag(AshfallKitFlags.SeamsUnverified), m.Id));
        Assert.Equal(["16E"], Expansion.Where(m => m.Flags.HasFlag(AshfallKitFlags.OrientationCorrected)).Select(m => m.SourceId));
    }

    [Fact]
    public void The_plaza_edge_lies_flat_like_the_inner_and_corner_tiles()
    {
        var e = BySource("16E");
        Assert.Equal(AshfallKitImportFix.LayFlat, e.ImportFix);
        Assert.Equal(new Vector3(1f, 3f, -2f), e.FileToModel(new Vector3(1f, 2f, 3f)));   // a quarter turn about X: (x, y, z) to (x, z, -y)
        Assert.Equal(Vector3.One * 2f, BySource("16I").FileToModel(Vector3.One * 2f));    // no fix: unchanged
        foreach (var m in new[] { e, BySource("16I"), BySource("16C") })
            Assert.True(m.RawSize.Y < 0.2f * MathF.Min(m.RawSize.X, m.RawSize.Z), $"{m.SourceId}: not flat ({m.RawSize})");
        Assert.All(Expansion.Where(m => m.SourceId != "16E"), m => Assert.Equal(AshfallKitImportFix.None, m.ImportFix));
    }

    [Fact]
    public void Walkable_expansion_models_are_traversal_pieces()
    {
        string[] families = ["wall run", "stronghold gate", "keep", "bridge", "stair", "ramp", "shooting platform", "ruin shell", "tall building", "rubble", "objective dais"];
        foreach (var m in Expansion.Where(m => AshfallKitFit.WalkableIds.Contains(m.Id))) Assert.Contains(m.Family, families);
        Assert.Equal(12, Expansion.Count(m => AshfallKitFit.WalkableIds.Contains(m.Id)));
    }
}
