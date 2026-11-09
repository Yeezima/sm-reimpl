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
        // The boss column's distances (MusouHud.BossColumn.cs): each boss's from the own player, from the crowd as shown.
        hud.Data.Distance.Clear();
        ushort commander = _musouState.Director?.BossId ?? 0;
        if ((_tfwBossOrks.Bars.Count > 0 || commander != 0) && _client is { } client && _tfwShown is { } shown)
        {
            var me = client.Predicted.Position;
            foreach (var s in shown)
            {
                if (s.Id != commander) { bool boss = false; foreach (var b in _tfwBossOrks.Bars) if (b.Id == s.Id) { boss = true; break; } if (!boss) continue; }
                hud.Data.Distance[s.Id] = System.Numerics.Vector3.Distance(s.Position, me);
            }
        }
        if (MusouHudDemoCampaign && hud.Data.BossOrks.Count == 0)   // --musou-hud-demo campaign: four made-up Boss Orks, the Big Mek being hit by the own player (the edge layout's screenshots)
        {
            hud.Data.BossOrks.Add(new TfwBossOrkBar(60001, "BOSS NOB", 0.72f));
            hud.Data.BossOrks.Add(new TfwBossOrkBar(60002, "BOSS BIG MEK", 0.35f));
            hud.Data.BossOrks.Add(new TfwBossOrkBar(60003, "BOSS 'ARD BOY", 0.9f));
            hud.Data.BossOrks.Add(new TfwBossOrkBar(60004, "BOSS SHOOTA BOY", 1f));
            hud.Data.Distance[60001] = 40f; hud.Data.Distance[60002] = 12f; hud.Data.Distance[60003] = 25f; hud.Data.Distance[60004] = 70f;
            hud.Data.HitAt[60002] = _connectedFor;
        }
    }

    /// <summary>Test aid <c>--musou-hud-demo campaign</c>: Musou Mode's HUD with every element on, in a real battle (MusouFx.cs MusouDemo).</summary>
    bool MusouHudDemoCampaign => _musouDemo == "campaign";
}
