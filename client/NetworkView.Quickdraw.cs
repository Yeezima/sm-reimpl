using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// feel/grenades-quickdraw (the owner's requests of 2026-11-06; client presentation of the simulation's <see cref="SimConfig.QuickdrawSwitch"/> and quicker throw):
/// <list type="bullet">
/// <item>The quickdraw: the weapon a switch takes is up when the switch ends (the simulation keeps <see cref="PlayerState.ReadyTicks"/> through it), and the ready posture is entered
/// over <see cref="QuickdrawFadeSeconds"/> instead of the graph's relaxed-to-ready blend when a switch was under way within <see cref="QuickdrawWindow"/> (every player: the slim
/// record carries the slot and the "up" flag). Ours, an estimate for the owner's "near instant, like a quickdraw".</item>
/// <item>The toss: the clip's wind-up is played over the simulation's shorter release (<see cref="ActionHandoff.TossClipTime"/>), so the hand opens on the release tick; the throw then
/// ends in the simulation and the next pose takes over with the toss's usual blend (the arm blends out).</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    /// <summary>Seconds over which the ready posture is entered right after a switch (ours; the graph's relaxed-to-ready blend is about 0.2 s).</summary>
    const float QuickdrawFadeSeconds = 0.06f;
    /// <summary>A switch under way this recently (seconds) makes the next entry into the ready posture a quickdraw.</summary>
    const double QuickdrawWindow = 0.3;

    /// <summary>Notes a switch under way (or a slot change seen between two frames) for <see cref="QuickdrawEntering"/>.</summary>
    void QuickdrawNote(PlayerState s, HipTrack t)
    {
        if (s.SwitchLeft > 0f || s.Slot != t.LastSlot) t.SwitchSeenAt = _connectedFor;
        t.LastSlot = s.Slot;
    }

    /// <summary>Whether the ready posture entered now follows a switch (the quickdraw blend), with the owner's quickdraw switch on.</summary>
    bool QuickdrawEntering(HipTrack t) => _config is { QuickdrawSwitch: true } && _connectedFor - t.SwitchSeenAt <= QuickdrawWindow;

    /// <summary>The toss clip's time for a throw <paramref name="throwTime"/> seconds in (the wind-up fitted to the simulation's release).</summary>
    float TossClipTime(float throwTime) =>
        _config is null ? throwTime : ActionHandoff.TossClipTime(throwTime, _config.ThrowRelease, _classes?.Throw?.Release ?? _config.ThrowRelease);

    /// <summary>The toss clip's end (the toss graph's <c>BRANCH_UseEquipment_End</c>; the weights' tail), whatever the simulation's end.</summary>
    float TossClipEnd => _classes?.Throw?.Duration ?? _config?.ThrowDuration ?? 0.667f;
}
