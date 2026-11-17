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
        run.Allies = false;   // the default (nothing that skips the server's landing gets the pair); the server sets it at each landing by WarfrontAlliedPair (W13d part 3). W13c pass 2 (designer review of W13c, ruling b; ours until the owner confirms): no allied AI Marine pair in Warfront on any map: a player has his own squad, and two free Marines held the HQ gate alone (Musou's own public switch; Musou and Classic keep it on)
        run.ReviveWindow = _ => false;   // no puppet revives a fallen human (the allied pair never counts): a revive is WarfrontRevive's, for a teammate or a squad's retainer
        enemies.Warfront = rules;
    }

    /// <summary>Whether a Warfront run gets Musou's two allied AI Marines (W13d part 3; owner 2026-11-17: "Two extra machines only for local singleplayer"; an interpretation): only when the server's human cap is one (the local campaign starts its server with <c>--warfront-max-humans 1</c>, as does a server started by hand with it); co-op and hosted runs get none.</summary>
    public static bool WarfrontAlliedPair(int maxHumans, bool laneBattle) => maxHumans == 1 && laneBattle;   // owner 2026-11-17: "Keep them as the free pair, but only in lane battles, not in the HQ defence or the fortress"

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
