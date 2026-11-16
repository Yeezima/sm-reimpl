using System.Numerics;
using SmSim.Extras.Warfront.Fortress;

// INTERIM (slice W14b). Musou's four W13b hooks have ONE answering body each (a partial method has one implementation). W13b's HQ battle implements the same four; until the lead merges W13b this file
// answers them for the fortress alone (keyed on WarfrontBattleRules.Fortress, which only a fortress deployment sets). When W13b merges: delete this file and add to W13b's four bodies
//     if (rules.Fortress is not null) { ...the body below... }
// (the bodies below are four short lines each). Nothing else in W14b depends on this file.

namespace SmSim.Extras.Musou.Campaign;

public sealed partial class TfwBattle
{
    partial void WarfrontStart(ref bool handled)
    {
        if (_enemies.Warfront?.Fortress is not { } setup) return;
        handled = true;
        FortressStart(setup);
    }

    partial void WarfrontRetake(int position, ref bool allowed)
    {
        if (_fortress is not null) allowed = false;   // the waypoints are never retaken (and the final assault never asks)
    }

    partial void WarfrontSourceActive(int source, ref bool active, ref bool handled)
    {
        if (_fortress is null) return;
        handled = true;
        active = FortressSourceActive(source);
    }
}

public sealed partial class TfwRun
{
    partial void WarfrontBattlePlan(World w, TfwLane lane, int node, ref TfwLayout layout, ref bool final, ref float elite)
    {
        if (Warfront?.Fortress is not { } setup) return;
        layout = setup.Plan.Layout;
        final = true;
        elite = WarfrontFortressBattleRules.EliteShare;
    }
}
