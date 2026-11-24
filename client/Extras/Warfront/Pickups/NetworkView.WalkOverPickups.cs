using SmNet.Extras.Musou;
using SmSim.Extras.Warfront;

namespace SmClient;

/// <summary>
/// Warfront's walk-over pickups on the client (owner 2026-11-25; <see cref="WarfrontPickupRules"/>): a powerup, a crate or an ammunition box shows no press or hold prompt, since the server takes it when the
/// player walks over it; the take sound plays when the server's list (a drop) or its pickup event (a box) says it was taken, as for any pickup not predicted here. A weapon drop keeps its E prompt. Each body returns at
/// once unless the match has the Warfront flag.
/// </summary>
public partial class NetworkView
{
    partial void WarfrontDropWalksOver(in MusouDropRecord drop, ref bool walks)
    {
        if (WarfrontOn && WarfrontPickupRules.WalksOver(drop.Item, drop.Crate)) walks = true;
    }

    partial void WarfrontPickupWalkOver(ref bool walks)
    {
        if (WarfrontOn) walks = true;
    }
}
