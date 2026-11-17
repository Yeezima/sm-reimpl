using System.Linq;
using Godot;
using SmClient.Extras.Warfront.Dress;
using SmSim.Extras.Musou.Ashfall;
using V3 = System.Numerics.Vector3;

namespace SmClient;

/// <summary>
/// The far massing's drawing (slice "dress", part 1): <see cref="WarfrontFarMassingPlan"/>'s blocks as one dark mesh under the band's scenery, standing on the outside ground's own height (<see cref="AshfallFrontScenery.Height"/>).
/// Called from <see cref="WarfrontFarGoal.Apply"/>. Client scenery only; no collision, in no hash. <c>--dress off</c> leaves it out (the before of a before/after still).
/// </summary>
public static class WarfrontFarMassing
{
    /// <summary>Test aid <c>--dress off</c>: the drawing before this slice (no far massing, no fortress dressing).</summary>
    public static bool Off
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = System.Array.IndexOf(args, "--dress");
            return i >= 0 && i + 1 < args.Length && args[i + 1] == "off";
        }
    }

    /// <summary>Adds the far massing of band <paramref name="band"/> to <paramref name="scenery"/>; returns a one-line note.</summary>
    public static string Add(Node3D scenery, int band)
    {
        float spire = WarfrontFarGoal.SpireDistance(band);
        var (y0, y1) = WarfrontFarMassingPlan.Range(band + 1 < AshfallFront.BandCount, AshfallFrontScenery.NeighbourOffset, AshfallFrontScenery.LineHalf, spire);
        var blocks = WarfrontFarMassingPlan.Blocks(y0, y1, 0xFA2u + (uint)band * 101u);
        var pieces = blocks.Select(b =>
        {
            float x = b.X, ground = AshfallFrontScenery.Height(x);
            uint h = DressHash.Mix(0x3A1u, (uint)(b.X * 3f + b.Y * 7f + 5000f));
            return new DressPiece(DressShape.Box, DressMat.Stone, new V3(x, b.Y, ground - 1f), new V3(b.SizeX, b.SizeY, b.Height + 1f), b.Yaw, null, (h & 0xFFu) / 255f);
        }).ToList();
        var host = new Node3D { Name = "FarMassing" };
        scenery.AddChild(host);
        int tris = WarfrontDressBake.Attach(host, pieces, "Far", shadows: false);
        return $"far massing {blocks.Count} blocks ({tris:N0} triangles) between {y0:F0} and {y1:F0} m north";
    }
}
