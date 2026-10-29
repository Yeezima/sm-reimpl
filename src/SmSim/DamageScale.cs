namespace SmSim;

public sealed partial class World
{
    readonly Dictionary<int, float> _damageScale = new();

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
}
