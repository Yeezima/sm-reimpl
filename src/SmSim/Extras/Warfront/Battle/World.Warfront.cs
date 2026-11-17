using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;

namespace SmSim;

/// <summary>Warfront's part of the World (slice W3): attaches the battle rules and routes an Ork's remembered damage multiplier and the squad orders. Each returns at once unless the run's Warfront object is set.</summary>
public sealed partial class World
{
    partial void WarfrontStarted()
    {
        if (!Config.Extras.HasFlag(ContentExtras.Warfront) || _tfw is not { } run || _musou is not { } enemies || run.Warfront is not null) return;
        var rules = new WarfrontBattleRules();
        run.Warfront = rules;
        run.Allies = false;   // W13c pass 2 (designer review of W13c, ruling b; ours until the owner confirms): no allied AI Marine pair in Warfront on any map: a player has his own squad, and two free Marines held the HQ gate alone (Musou's own public switch; Musou and Classic keep it on)
        run.ReviveWindow = _ => false;   // no puppet revives a fallen human (the allied pair never counts): a revive is WarfrontRevive's, for a teammate or a squad's retainer
        enemies.Warfront = rules;
    }

    partial void WarfrontOrkDamage(ushort enemy, ref float damage)
    {
        if (_tfw?.Warfront is not { } rules || enemy == 0 || enemy >= rules.DamageScale.Length) return;
        float scale = rules.DamageScale[enemy];
        if (scale != 1f) damage *= scale;
    }

    partial void WarfrontOrder(int player, TfwCommandRecord command, ref bool handled)
    {
        if (_tfw?.Warfront is not { } rules) return;
        rules.OrderSink?.Invoke(player, command);
        handled = true;
    }
}
