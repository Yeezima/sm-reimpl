using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The powerups' part of the enemy simulation (owner, 2026-11-10; <see cref="MusouPowerupRules"/>), set by the World once a tick (<c>World.MusouPowerups.cs</c>):
/// <list type="bullet">
/// <item>Fear: an Ork within <see cref="MusouPowerupRules.FearRadius"/> of a feared player drops its target and token and runs from the nearest such player, up the flow field where it leads away (as the
/// Scrap Mek runs, <see cref="FleeWay"/>), <see cref="MusouPowerupRules.FearSpeedScale"/> times its speed. The Boss Orks and the Warboss stand their ground (ours: a boss fight stays a fight), and so do
/// a Bomb Squig (it runs at you by nature), an Ork the boss's ability drives and one in the air.</item>
/// <item>Damage: a player's damage to any enemy is scaled (<see cref="SetAttackerScale"/>): Double Damage on all of it, Supercharged Melee on its melee hits (both together never above
/// <see cref="MusouPowerupRules.Cap"/>, x2: the owner's "x2 cap"; the World also holds a stim's melee multiplier under it, <c>World.MusouMeleeDealt</c>).</item>
/// </list>
/// </summary>
public sealed partial class MusouSim
{
    readonly List<int> _fearOf = new();
    readonly Dictionary<int, (float Melee, float Other)> _attackerScale = new();

    /// <summary>Enemies that ran from a feared player on the last step (the log and the tests).</summary>
    public int Fleeing { get; private set; }
    int _fleeingCount;

    /// <summary>The players whose Fear works now (their ids; the World sets the list each tick).</summary>
    public void SetFear(IEnumerable<int> players)
    {
        _fearOf.Clear();
        _fearOf.AddRange(players);
    }

    /// <summary>The players' damage scales now (the World sets them each tick; a player not listed deals 1x).</summary>
    public void SetAttackerScale(int player, float melee, float other)
    {
        if (melee == 1f && other == 1f) _attackerScale.Remove(player);
        else _attackerScale[player] = (melee, other);
    }

    public void ClearAttackerScales() => _attackerScale.Clear();

    /// <summary>The scale on <paramref name="attacker"/>'s damage of <paramref name="cause"/> (1: none).</summary>
    float AttackerScale(int attacker, MusouDeathKind cause) =>
        attacker > 0 && _attackerScale.TryGetValue(attacker, out var s) ? (cause == MusouDeathKind.Melee ? s.Melee : s.Other) : 1f;

    /// <summary>The scale this step puts on <paramref name="attacker"/>'s melee hits (1: none); the World holds a stim's melee under <see cref="MusouPowerupRules.Cap"/> with it.</summary>
    public float MeleeScaleOf(int attacker) => AttackerScale(attacker, MusouDeathKind.Melee);

    /// <summary>Whether Fear moves an enemy of <paramref name="type"/> at all (ours: never a boss, a Squig or the Scrap Mek, who runs anyway).</summary>
    public static bool Fears(MusouEnemyType type) => !MusouPowerupRules.Boss(type) && type is not (MusouEnemyType.BombSquig or MusouEnemyType.ScrapMek);

    /// <summary>One step of an enemy in flight from a feared player; false (and nothing done) when no feared player is near, or it does not fear.</summary>
    bool StepFeared(int i, ReadOnlySpan<MusouTarget> targets, Span<int> near)
    {
        if (_fearOf.Count == 0 || _scripted[i] || !Fears(_type[i])) return false;
        var p = _position[i];
        float nearest = MusouPowerupRules.FearRadius;
        var away = Vector2.Zero;
        foreach (var t in targets)
        {
            if (!t.Alive || t.Goal || t.Id <= 0 || !_fearOf.Contains(t.Id)) continue;
            var d = p - t.Position;
            float reach = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
            if (reach >= nearest || MathF.Abs(d.Z) > 6f) continue;
            nearest = reach;
            var flat = new Vector2(d.X, d.Y);
            away = flat.LengthSquared() > 1e-6f ? Vector2.Normalize(flat) : Forward(_yaw[i] + MathF.PI);
        }
        if (away == Vector2.Zero) return false;
        var spec = Rules.Of(_type[i]);
        _target[i] = 0; _token[i] = 0; _burst[i] = 0; _aim[i] = -1; _charging[i] = false;
        _headwayTick[i] = Tick; _anchor[i] = _position[i];   // running on purpose: never stranded
        if (_state[i] != MusouEnemyState.Seek && _state[i] != MusouEnemyState.Stagger) SetState(i, MusouEnemyState.Seek);
        if (_state[i] == MusouEnemyState.Stagger && _stateTicks[i] < Ticks(Rules.Combat.StaggerSeconds)) { Move(i, spec, Vector2.Zero, targets, near); _fleeingCount++; return true; }
        if (_state[i] == MusouEnemyState.Stagger) SetState(i, MusouEnemyState.Seek);
        Move(i, spec, FleeWay(i, away) * spec.Speed * MusouPowerupRules.FearSpeedScale, targets, near);
        _fleeingCount++;
        return true;
    }

    /// <summary>The count of this step's fleeing enemies becomes <see cref="Fleeing"/> (called at the step's end).</summary>
    void EndFearCount() { Fleeing = _fleeingCount; _fleeingCount = 0; }
}
