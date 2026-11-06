using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The roaming Boss Orks on the client (owner, 2026-11-06): their bars under the boss bar and the call-outs, from tag 13's pinned bars and the entity feed's kinds (<see cref="TfwBossOrkHud"/>).
/// Every client logs an arrival and a kill (headless too), so a scripted run can read them.
/// </summary>
public partial class NetworkView
{
    readonly TfwBossOrkHud _tfwBossOrks = new();

    void TfwBossOrksFrame(in TfwWarView war)
    {
        bool fighting = war.Phase is TfwRunPhase.Battle or TfwRunPhase.FinalAssault && !war.Own.BattleWon;
        var (arrived, slain) = _tfwBossOrks.Update(_tfwState.Bars, _musouState.TypeOf, fighting);
        if (arrived > 0) GD.Print($"musou: boss ork incoming ({_tfwBossOrks.Bars.Count} alive)");
        if (slain > 0) GD.Print($"musou: boss ork slain ({_tfwBossOrks.Bars.Count} alive)");
        if (_tfwHud is { } tfw) tfw.Data.BossOrks = _tfwBossOrks.Bars.Count;
        if (_musouHud is not { } hud) return;
        if (arrived > 0) hud.BossOrkIncoming();
        else if (slain > 0) hud.BossOrkSlain();
        hud.Data.BossOrks.Clear();
        hud.Data.BossOrks.AddRange(_tfwBossOrks.Bars);
    }
}
