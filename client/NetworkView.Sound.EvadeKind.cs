using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// sound/editor-roll-hop: the mix editor's "Tactical roll" and "Devastator / Havoc hop" entries (<see cref="SoundMixCatalog.TacticalRollKey"/>, <see cref="SoundMixCatalog.HeavyHopKey"/>). The two evades post
/// the same events, so the sound system tells them apart by the evade kind the player's emitter carries; this sets it when a ground evade starts. A jump-pack dash sets nothing (its own sound is a
/// different event) and an evade that is neither (the Ork's side hop) leaves the kind as it was.
/// </summary>
public partial class NetworkView
{
    void EvadeMixKind(SoundTrack t, PlayerState s)
    {
        if (!s.Alive || !Evade.Evading(s) || _config is null || Loadouts.Evade(_config, s) is not { } spec || spec.Dash) return;
        if (spec.Name.EndsWith("heavy_evade", System.StringComparison.OrdinalIgnoreCase)) t.Emitter.EvadeKind = SoundMixCatalog.EvadeKindHop;
        else if (!spec.Forward.Hops && !spec.Left.Hops && !spec.SideOnly) t.Emitter.EvadeKind = SoundMixCatalog.EvadeKindRoll;
    }
}
