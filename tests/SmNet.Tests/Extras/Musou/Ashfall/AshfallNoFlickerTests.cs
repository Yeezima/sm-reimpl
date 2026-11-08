using System.Text.RegularExpressions;
using SmSim.Extras.Musou.Ashfall;
using Xunit;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// ashfall-no-flicker (2026-11-08): the two causes of the flicker in the owner's Ashfall recording, kept fixed. (1) The boundary's block variant was hashed from floor() of the interpolated plane coordinate;
/// the boundary's faces lie on whole metres, so that floor flipped between two neighbours from frame to frame and whole blocks switched variant. The plane is now a per-vertex rounded, flat varying.
/// (2) The kit models' grime overlay and its multiply pass (the overlay's next pass) tied in the transparent sort, so their order flipped per instance and per frame; they now have fixed priorities.
/// </summary>
public class AshfallNoFlickerTests
{
    static string Root()
    {
        var up = new DirectoryInfo(AppContext.BaseDirectory);
        while (up is not null && !File.Exists(Path.Combine(up.FullName, "SmReimpl.slnx"))) up = up.Parent;
        return up?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx not found above the test assembly");
    }

    static string Client(string file) => File.ReadAllText(Path.Combine(Root(), "client", "Extras", "Musou", "Ashfall", file));

    [Fact]
    public void Every_boundary_face_lies_on_a_whole_metre_so_a_rounded_vertex_plane_is_one_value_per_face()
    {
        var level = AshfallMap.Build();
        var walls = level.Parts.Where(p => p.Kind == AshfallKind.Boundary).ToList();
        Assert.NotEmpty(walls);
        foreach (var w in walls)
            foreach (var c in w.Corners)
            {
                // the faces are the box's x and y extremes: whole metres, the case where floor() of an interpolated value at the face flips between two integers
                Assert.Equal(MathF.Round(c.X), c.X);
                Assert.Equal(MathF.Round(c.Y), c.Y);
            }
        // the shader's per-vertex id, round(plane) (floor(plane + 0.5)), is the same for every corner of a face even after a float transform error of a few ulps
        foreach (var w in walls)
            foreach (var plane in w.Corners.Select(c => c.X).Distinct())
                foreach (var eps in new[] { -1e-4f, 0f, 1e-4f })
                    Assert.Equal(MathF.Floor(plane + 0.5f), MathF.Floor(plane + eps + 0.5f));
    }

    [Fact]
    public void The_masonry_block_hash_takes_a_flat_per_vertex_plane_not_floor_of_the_interpolated_one()
    {
        string text = Client("AshfallKitView.cs");
        Assert.Contains("varying flat int plane_id;", text);
        Assert.Contains("const bool OLD_PLANE = false;", text);
        Assert.Contains("plane_id = int(floor((vx ? wpos.x : wpos.z) + 0.5))", text);
        Assert.Contains("blockhash(int(bidx), course, pid, seed)", text);
        Assert.DoesNotContain("blockhash(int(bidx), course, int(floor(plane))", text);
    }

    [Fact]
    public void The_grime_overlay_draws_before_its_multiply_and_both_between_the_cut_outs_and_the_ground_decals()
    {
        var m = Regex.Match(Client("AshfallGrime.cs"), @"public const int OverlayPriority = (-?\d+), DarkenPriority = (-?\d+);");
        Assert.True(m.Success, "AshfallGrime.OverlayPriority / DarkenPriority not found");
        int overlay = int.Parse(m.Groups[1].Value), darken = int.Parse(m.Groups[2].Value);
        Assert.True(overlay < darken, "the multiply (next pass) must sort after the overlay: a tie lets their order flip per frame");
        Assert.True(overlay > -100, "after the alpha-to-coverage cut-outs (RenderQuality.CutOutPriority = -100)");
        Assert.True(darken < -10, "before the gore ground decals (-10), the impact glow (-1) and the effects (0)");
        string grime = Client("AshfallGrime.cs");
        Assert.Contains("RenderPriority = OrderOld ? 0 : OverlayPriority", grime);
        Assert.Contains("RenderPriority = OrderOld ? 0 : DarkenPriority", grime);
    }
}
