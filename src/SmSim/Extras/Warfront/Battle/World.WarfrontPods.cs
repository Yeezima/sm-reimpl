using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// Warfront's drop pods outside the player limit (Phase B T2b; owner 2026-11-24: "Drop pod marines don't count towards the limit"; spec "Added content: Warfront: drop pod Marines outside the player
/// limit"): a pod's five Marines take ids from <see cref="TfwContracts.PodIdFirst"/> (20, four pods), never the puppet ids the retainers and the pair hold, so a pod lands its full complement at four
/// humans with full squads. They stay real World players with Musou's pod role, look and brain. Returns at once outside Warfront: Musou Mode's pods keep the puppet ids.
/// </summary>
public sealed partial class World
{
    partial void WarfrontPodIds(ref bool own)
    {
        if (_tfw?.Warfront is not null) own = true;
    }
}
