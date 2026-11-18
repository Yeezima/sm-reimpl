using Godot;

namespace SmClient;

/// <summary>
/// Warfront's client performance settings (perf pass, branch claude/warfront-perf; all ours, spec "Warfront: client crowd levels of detail"). A Warfront fight draws up to 1200 Orks (1800 before 2026-11-18) through the vertex-animation
/// tier, every one with the full mesh; the windowed fortress plaza showed 20 to 36 million primitives a frame. The far mesh now has index-only levels of detail, picked per batch by distance. Musou Mode and PvP
/// keep their old look: nothing here runs there.
/// </summary>
public partial class NetworkView
{
    /// <summary>Pixels of error a level of detail may show before the finer one is used (Godot's own default is 1; the viewport's <c>MeshLodThreshold</c>). Test aid <c>--crowd-lod-threshold &lt;px&gt;</c>.</summary>
    public const float CrowdLodThreshold = 4f;

    /// <summary>The viewport's LOD threshold before the first Warfront match changed it (process-wide, like the viewport); null when Warfront has not changed it.</summary>
    static float? _preWarfrontLodThreshold;

    partial void WarfrontCrowdPerf()
    {
        // These are process-wide statics: set every one on each match start so a Musou match after a Warfront match in the same client gets Musou's own values back (review).
        CrowdView.FarNoShadow = WarfrontOn || CrowdView.FarNoShadowFlag;   // owner 2026-11-18: far-tier Orks cast no sun shadows (Warfront only; --crowd-no-shadow anywhere)
        MusouCrowd.VisibleOverride = WarfrontOn ? MusouCrowd.WarfrontVisible : 0;   // owner 2026-11-18: 1200 visible Orks in Warfront, Musou keeps 1800
        SmClient.MusouVat.Lods = false;
        if (!WarfrontOn)
        {
            // The viewport outlives the match: put back the threshold Warfront replaced, so a later Musou match (and its Ashfall kit view, which caches it) starts from it (review).
            if (_preWarfrontLodThreshold is { } before) { GetViewport().MeshLodThreshold = before; _preWarfrontLodThreshold = null; }
            return;
        }
        if (CmdFlag("no-crowd-lod")) return;
        SmClient.MusouVat.Lods = true;
        var args = OS.GetCmdlineUserArgs();
        int at = System.Array.IndexOf(args, "--crowd-lod-threshold");
        float px = at >= 0 && at + 1 < args.Length && float.TryParse(args[at + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : CrowdLodThreshold;
        _preWarfrontLodThreshold ??= GetViewport().MeshLodThreshold;
        GetViewport().MeshLodThreshold = px;
        GD.Print($"warfront: crowd levels of detail on, threshold {px:0.#} px");
    }
}
