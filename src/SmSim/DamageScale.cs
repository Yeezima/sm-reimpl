namespace SmSim;

public sealed partial class World
{
    readonly Dictionary<int, float> _damageScale = new();
    readonly HashSet<int> _botClients = new();

    /// <summary>
    /// Whether player <paramref name="playerId"/>'s client marked itself a test bot (its Hello's bot flag, as <c>--bot-damage-scale</c> reads it; the server sets it on join). Information only: the core never
    /// reads it, added content may (Musou Mode: a bot client takes a tenth of every hit, as its AI Marines; owner, 2026-11-06). Dropped by <see cref="Remove"/> and a new <see cref="Spawn"/> of the id.
    /// Server-side only: not in the config hash.
    /// </summary>
    public void SetBotClient(int playerId, bool bot)
    {
        if (bot) _botClients.Add(playerId); else _botClients.Remove(playerId);
    }

    /// <summary>Whether <paramref name="playerId"/>'s client is a test bot (<see cref="SetBotClient"/>).</summary>
    public bool IsBotClient(int playerId) => _botClients.Contains(playerId);

    /// <summary>
    /// Test aid (the server's <c>--bot-damage-scale</c>; owner, 2026-10-29: "reduce all bot damage by 66% temporarily for testing purposes"; not the
    /// original): every hit player <paramref name="playerId"/> deals from now on is scaled by <paramref name="scale"/> (its shots, projectiles, cones,
    /// blasts and landings, melee and the charge; the health and the shield part alike; its own blasts on itself too). 1 or more (it only ever lowers damage), not finite or
    /// negative removes it. Kept across respawns; dropped by <see cref="Remove"/> and a new <see cref="Spawn"/> of the id. Server-side only: not in the config hash.
    /// </summary>
    public void SetDamageScale(int playerId, float scale)
    {
        if (!float.IsFinite(scale) || scale < 0f || scale >= 1f) _damageScale.Remove(playerId);   // only ever lowers (a bot's flag is its own word)
        else _damageScale[playerId] = scale;
    }

    /// <summary>The scale on the damage <paramref name="attacker"/> deals (<see cref="SetDamageScale"/>; 1 by default).</summary>
    public float DamageScaleOf(int attacker) => _damageScale.TryGetValue(attacker, out float s) ? s : 1f;

    /// <summary>
    /// The scale on every hit player <paramref name="target"/> takes (shots, projectiles, cones, blasts, melee, the charge, an enemy's attack; the health and the shield part alike): 1 unless added
    /// content changes it (<see cref="ExtrasDamageTaken"/>; Musou Mode: its AI Marines and the bot clients take a tenth). Classic PvP never sets it.
    /// </summary>
    public float DamageTakenScale(int target)
    {
        float scale = 1f;
        ExtrasDamageTaken(target, ref scale);   // EXTRAS-HOOK
        return scale;
    }

    partial void ExtrasDamageTaken(int target, ref float scale);
}
