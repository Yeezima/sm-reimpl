using System.Numerics;
using SmContent.Extras.Musou;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// The front pilot's client rules that live in the simulation so they can be tested (re-review of 2026-11-16): the drawn openings in the boundary wall (the owner's "a visual opening ... collision stays closed") and which band the client
/// draws (none in the Stormbird: a band's outside scenery would bury it).
/// </summary>
public class AshfallFrontClientRuleTests
{
    [Fact]
    [Trait("Speed", "Slow")]   // builds the default bands (cached in the process)
    public void The_edge_openings_are_drawing_only_and_cover_only_the_avenue()
    {
        for (int band = 0; band < AshfallFront.BandCount; band++)
        {
            var level = AshfallMap.BuildFrontBand(band).Level;
            var drawn = AshfallMap.OpenFrontEdges(level);
            Assert.NotSame(level, drawn);
            Assert.Equal(level.Hash, drawn.Hash);   // the level's hash is carried, never recomputed from the drawn parts
            Assert.Equal(level.Parts.Count, drawn.Parts.Count);   // the same part indices (the dressing's hidden parts stay right)
            Assert.Same(level.Points, drawn.Points);
            int changed = 0;
            for (int i = 0; i < level.Parts.Count; i++)
            {
                if (ReferenceEquals(level.Parts[i], drawn.Parts[i])) continue;
                changed++;
                var before = level.Parts[i]; var after = drawn.Parts[i];
                Assert.Equal(AshfallKind.Boundary, after.Kind);
                Assert.True(Math.Abs(before.Corners.Max(c => c.Y)) >= AshfallMap.Half - 0.01f || Math.Abs(before.Corners.Min(c => c.Y)) >= AshfallMap.Half - 0.01f, "only the north and south faces");
                // the opening: nothing drawn with |x| < 12, everything else of the wall still drawn
                Assert.All(after.Corners, c => Assert.True(Math.Abs(c.X) >= AshfallMap.FrontOpeningHalf - 1e-3f, $"drawn corner {c} inside the opening"));
                Assert.Equal(before.Corners.Min(c => c.X), after.Corners.Min(c => c.X));
                Assert.Equal(before.Corners.Max(c => c.X), after.Corners.Max(c => c.X));
                Assert.Equal(-AshfallMap.FrontOpeningHalf, after.Corners.Where(c => c.X < 0f).Max(c => c.X));
                Assert.Equal(AshfallMap.FrontOpeningHalf, after.Corners.Where(c => c.X > 0f).Min(c => c.X));
            }
            Assert.Equal(2, changed);
        }
        // the collision the server and the client predict with is the composed level's: closed, and its hash is today's
        var composed = AshfallMap.BuildDraft5(AshfallLayout.Front).Level;
        var partsBefore = composed.Parts.ToArray();
        foreach (var (bandLevel, _) in AshfallMap.SplitFront(composed, AshfallMap.BuildDraft5(AshfallLayout.Front).Dressing)) AshfallMap.OpenFrontEdges(bandLevel);
        Assert.Equal(partsBefore.Length, composed.Parts.Count);   // drawing copies only: the composed level keeps every part, the walls closed
        for (int i = 0; i < partsBefore.Length; i++) Assert.Same(partsBefore[i], composed.Parts[i]);   // the very same part objects (a replaced part would carry new corners)
        Assert.Equal(AshfallFrontWorldTests.PinnedComposedHash, composed.Hash);
        var collision = AshfallContent.ToMapContent(composed).ToCollisionWorld();
        for (int band = 0; band < AshfallFront.BandCount; band++)
            foreach (float dir in new[] { 1f, -1f })   // a ray along the avenue out of each edge meets the boundary wall
            {
                var from = new Vector3(AshfallFront.OffsetOf(band), dir * (AshfallMap.Half - 5f), 2f);
                var hit = collision.Character.Raycast(from, new Vector3(0f, dir, 0f), 20f);
                Assert.True(hit is { } h && h.Distance <= 5.5f, $"band {band}: the avenue's {(dir > 0 ? "north" : "south")} exit is open in the collision");
            }
        Assert.Equal(AshfallFrontWorldTests.PinnedComposedHash, AshfallContent.ToMapContent(composed).Hash);   // the map hash the handshake compares is the pinned composed hash
        var gate = new Vector3(0f, AshfallMap.Half + 2f, 2f);   // in the north wall, on the avenue
        Assert.Contains(composed.Parts, p => p.Kind == AshfallKind.Boundary && p.Corners.Min(c => c.X) < 0f && p.Corners.Max(c => c.X) > 0f && p.Corners.Min(c => c.Y) <= gate.Y && p.Corners.Max(c => c.Y) >= gate.Y);
        Assert.True(collision.Character.Max.Y >= AshfallMap.Half + 4f - 1e-3f);
    }

    [Fact]
    public void The_client_draws_the_band_the_player_stands_in_and_none_in_the_stormbird()
    {
        for (int band = 0; band < AshfallFront.BandCount; band++)
        {
            float x = AshfallFront.OffsetOf(band);
            Assert.Equal(band, AshfallFront.BandShown(new Vector3(x, 0f, 1f), cameraInHub: false));
            Assert.Equal(band, AshfallFront.BandShown(new Vector3(x - 230f, 230f, 1f), cameraInHub: false));
            Assert.Equal(-1, AshfallFront.BandShown(new Vector3(x, 0f, 1f), cameraInHub: true));   // the camera aboard wins
        }
        var hub = TfwHub.Beyond(AshfallContent.ToMapContent(AshfallMap.BuildDraft5(AshfallLayout.Front).Level).ToCollisionWorld());
        Assert.Equal(-1, AshfallFront.BandShown(hub.Centre, cameraInHub: false));   // standing in the Stormbird, south of the composed world
        Assert.Equal(-1, AshfallFront.BandShown(new Vector3(300f, 0f, 1f), cameraInHub: false));   // between two bands
        Assert.Equal(-1, AshfallFront.BandShown(new Vector3(2000f, 0f, 1f), cameraInHub: false));   // east of the last band (the Firing Range)
    }
}
