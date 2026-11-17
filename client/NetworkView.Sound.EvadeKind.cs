using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// sound/editor-roll-hop: the mix editor's "Tactical roll" and "Devastator / Havoc hop" entries (<see cref="SoundMixCatalog.TacticalRollKey"/>, <see cref="SoundMixCatalog.HeavyHopKey"/>). The two evades post
/// the same events, so the sound system tells them apart by the evade kind the player's emitter carries; this sets it when a ground evade starts. Any other evade (a jump-pack dash, the Ork's side hop)
/// clears it, and so does a death, so a later evade of another class never answers to a stale entry (review F1). The kind outlives the evade itself, so its end voices still answer to the entry.
/// </summary>
public partial class NetworkView
{
    void EvadeMixKind(SoundTrack t, PlayerState s)
    {
        if (!s.Alive) { t.Emitter.EvadeKind = null; return; }
        if (!Evade.Evading(s) || _config is null) return;
        if (Loadouts.Evade(_config, s) is not { } spec || spec.Dash) { t.Emitter.EvadeKind = null; return; }
        if (spec.Name.EndsWith("heavy_evade", System.StringComparison.OrdinalIgnoreCase)) t.Emitter.EvadeKind = SoundMixCatalog.EvadeKindHop;
        else if (!spec.Forward.Hops && !spec.Left.Hops && !spec.SideOnly) t.Emitter.EvadeKind = SoundMixCatalog.EvadeKindRoll;
        else t.Emitter.EvadeKind = null;
    }
}
