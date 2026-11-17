using Godot;

namespace SmClient;

/// <summary>
/// Warfront's client performance settings (perf pass, branch claude/warfront-perf; all ours, spec "Warfront: client crowd levels of detail"). A Warfront fight draws up to 1800 Orks through the vertex-animation
/// tier, every one with the full mesh; the windowed fortress plaza showed 20 to 36 million primitives a frame. The far mesh now has index-only levels of detail, picked per batch by distance. Musou Mode and PvP
/// keep their old look: nothing here runs there.
/// </summary>
public partial class NetworkView
{
    /// <summary>Pixels of error a level of detail may show before the finer one is used (Godot's own default is 1; the viewport's <c>MeshLodThreshold</c>). Test aid <c>--crowd-lod-threshold &lt;px&gt;</c>.</summary>
    public const float CrowdLodThreshold = 2f;

    partial void WarfrontCrowdPerf()
    {
        if (!WarfrontOn || CmdFlag("no-crowd-lod")) return;
        SmClient.MusouVat.Lods = true;
        var args = OS.GetCmdlineUserArgs();
        int at = System.Array.IndexOf(args, "--crowd-lod-threshold");
        float px = at >= 0 && at + 1 < args.Length && float.TryParse(args[at + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : CrowdLodThreshold;
        GetViewport().MeshLodThreshold = px;
        GD.Print($"warfront: crowd levels of detail on, threshold {px:0.#} px");
    }
}
