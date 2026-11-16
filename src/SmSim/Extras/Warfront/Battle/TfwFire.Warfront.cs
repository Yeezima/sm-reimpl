namespace SmSim.Extras.Musou.Campaign;

/// <summary>Warfront's cooldown cards on the support strike and the drop pod (slice W3b): the seconds of the shared cooldown scaled by the caller's cards.</summary>
public sealed partial class TfwFire
{
    /// <summary>The run's Warfront rules (set by the World's step; null without them).</summary>
    internal SmSim.Extras.Warfront.WarfrontBattleRules? Warfront { get; set; }

    partial void WarfrontCooldown(int player, ref float seconds)
    {
        if (Warfront is not { } rules) return;
        float scale = rules.Progression.CooldownScale(player);
        if (scale != 1f) seconds *= scale;
    }
}
