using System.Numerics;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Musou;

/// <summary>
/// The fall of Musou Mode's fodder (pass 2, item 7; design section C): a Marine's shove, charge or melee hit puts a fodder Ork in <see cref="MusouEnemyState.Knocked"/> for
/// <see cref="TfwCombatRules.KnockedSeconds"/> (no moves of his own, no attacks), and sometimes kills him. Only under <see cref="War"/>, so the 5-wave run never sees the state. Elites stagger as before.
/// </summary>
public sealed partial class MusouSim
{
    /// <summary>The war's melee and firepower rules; null (the 5-wave run) leaves every one of them as it was. Set only by <see cref="World.StartTfw"/>.</summary>
    public TfwCombatRules? War { get; set; }
    /// <summary>For the fight's report: Orks thrown down, and those the throw killed, since the start.</summary>
    public int KnockedDown { get; private set; }
    public int KnockKills { get; private set; }

    /// <summary>Whether a type falls instead of staggering: the small Orks; 'Ard Boyz, Nobz, Chaos Marines and the Warboss do not.</summary>
    public static bool Fodder(MusouEnemyType type) => type is MusouEnemyType.Boy or MusouEnemyType.ShootaBoy or MusouEnemyType.Gretchin;

    /// <summary>
    /// A fodder Ork alive is thrown <paramref name="distance"/> along <paramref name="push"/> and falls; with <paramref name="deathChance"/> (a roll of the tick and the id) he dies instead, the KO
    /// <paramref name="attacker"/>'s. False, and nothing changed, outside a war, for any other type, for one already down, or for an unknown or dying one.
    /// </summary>
    public bool KnockDown(ushort id, Vector2 push, float distance, int attacker, float deathChance)
    {
        int i = id - 1;
        if (War is null || i < 0 || i >= Capacity || !_used[i] || !Fodder(_type[i]) || _state[i] is MusouEnemyState.Dying or MusouEnemyState.Knocked) return false;
        if (Unit(Mix(Tick, (uint)id * 0x9E3779B1u + 0x51ED270Bu)) < deathChance)
        {
            KnockKills++;
            return Damage(id, attacker, _health[i], new Vector3(push, 0.3f), out _, marker: 0, cause: MusouDeathKind.Melee);
        }
        KnockedDown++;
        if (_group[i] != 0) ReleaseGroup(_group[i]);   // a shoved defender wakes its group, as a hurt one does
        SetState(i, MusouEnemyState.Knocked);
        _token[i] = 0; _burst[i] = 0;
        Knock(i, push, distance);
        return true;
    }

    /// <summary>One tick on the ground: he stands when his time is up; a throw still under way and the crowd move him, nothing he does.</summary>
    void StepKnocked(int i, ReadOnlySpan<MusouTarget> targets, Span<int> near)
    {
        if (_stateTicks[i] >= Ticks(War?.KnockedSeconds ?? 0f)) SetState(i, MusouEnemyState.Seek);
        _headwayTick[i] = Tick; _anchor[i] = _position[i];
        Move(i, Rules.Of(_type[i]), Vector2.Zero, targets, near);
    }
}
