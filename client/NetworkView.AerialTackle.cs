using Godot;
using SmSim;

namespace SmClient;

public partial class NetworkView
{
    /// <summary>
    /// musou/no-aerial-tackle restore switch (client <c>--musou-aerial-tackle</c>; the server needs the same: config hash): the Assault's and Raptor's aerial tackle works in Musou Mode too.
    /// Without it Musou Mode has none and a melee press in a hop is the light attack at once (owner 2026-11-12, temporary).
    /// </summary>
    static SimConfig WithAerialTackleRestoredArg(SimConfig c) =>
        System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--musou-aerial-tackle") >= 0 ? c with { AerialTackleRestored = true } : c;

    /// <summary>musou/ork-swing-commit restore switch (client <c>--musou-ork-swing-tracking</c>; the server needs the same: config hash): a melee Ork's wind-up faces its target to the blow, as before 2026-11-12.</summary>
    static SimConfig WithOrkSwingTrackingArg(SimConfig c) =>
        System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--musou-ork-swing-tracking") >= 0 ? c with { MusouOrkSwingTracking = true } : c;
}
