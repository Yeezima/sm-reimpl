using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// Executions X4 (Warfront): the Orks held for an execution (design state/design-executions-1125.md "Warfront"; the plan's X4 section). The World holds the Ork its executor's run took
/// (<see cref="ExecutionsHold"/>) from the run's start to its end and releases it for any reason (<see cref="ExecutionsRelease"/>). A held Ork does nothing of its own: the AI's per-enemy step
/// (<c>MusouSim.Ai.cs</c>, <see cref="ExecutionsHeldStep"/>) keeps it standing at the pair's yaw with no move, push, wind-up, strike, burst, charge, drive or attack token, in the existing
/// <see cref="MusouEnemyState.Seek"/> state (no new state: the Musou wire is unchanged). The kill frame (<see cref="ExecutionsKill"/>) goes through <see cref="Damage"/>, so the normal kill,
/// XP, assist and drop path runs.
/// <list type="bullet">
/// <item>A slot reused by a new spawn, or a pool cleared, drops its hold (<see cref="ExecutionsSpawned"/>, <see cref="ExecutionsReset"/>: hook lines in <c>MusouSim.Ai.cs</c>'s
/// <c>SpawnedAi</c> and <c>ResetAi</c>, the paths every <c>Spawn</c> and <c>Clear</c> take), so a run whose Ork's slot was freed and reused ends Invalid (the World's check).</item>
/// <item>Nothing is held outside a Warfront execution: the arrays are allocated by the first hold, and every hook returns at once while nothing is held (Musou Mode and the 5-wave run are untouched).</item>
/// <item>X4 phase 2 (after Chaos C2): the gates in <c>Damage</c>, <c>Stagger</c> and <c>KnockDown</c> call <see cref="ExecutionsBlocksHit"/> (every other hit refused until the kill frame).</item>
/// </list>
/// </summary>
public sealed partial class MusouSim
{
    bool[]? _execHeld;
    float[]? _execYaw;
    int _execHeldCount;
    ushort _execKilling;   // the id whose kill frame is being applied (its hit is let through), else 0

    /// <summary>How many Orks are held now.</summary>
    public int ExecutionsHeldCount => _execHeldCount;

    /// <summary>Holds enemy <paramref name="id"/> (in use and not dying) facing <paramref name="yaw"/> for an execution; false, and nothing held, otherwise.</summary>
    public bool ExecutionsHold(ushort id, float yaw)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i] || _state[i] == MusouEnemyState.Dying || !float.IsFinite(yaw)) return false;
        _execHeld ??= new bool[Capacity];
        _execYaw ??= new float[Capacity];
        if (!_execHeld[i]) _execHeldCount++;
        _execHeld[i] = true;
        _execYaw[i] = yaw;
        _token[i] = 0;   // X4 review: an attack token it held is given back now, before the next step's tally counts it for its target
        return true;
    }

    /// <summary>Releases enemy <paramref name="id"/>'s hold (no-op when it has none).</summary>
    public void ExecutionsRelease(ushort id)
    {
        int i = id - 1;
        if (_execHeld is null || i < 0 || i >= Capacity || !_execHeld[i]) return;
        _execHeld[i] = false;
        _execHeldCount--;
    }

    /// <summary>Whether enemy <paramref name="id"/> is held (in use, its slot not reused since the hold).</summary>
    public bool ExecutionsIsHeld(ushort id)
    {
        int i = id - 1;
        return _execHeld is not null && i >= 0 && i < Capacity && _used[i] && _execHeld[i];
    }

    /// <summary>
    /// Whether a hit on enemy <paramref name="id"/> is refused because it is held (every hit but its own kill frame's; X4 phase 2's gates in <c>Damage</c>, <c>Stagger</c> and <c>KnockDown</c>).
    /// </summary>
    public bool ExecutionsBlocksHit(ushort id) => _execHeldCount > 0 && _execKilling != id && ExecutionsIsHeld(id);

    /// <summary>
    /// The hit points the kill frame deals (<paramref name="health"/> x 8, at least 10 000; ours): far over any health, so the attacker's powerup scale (<c>AttackerScale</c>, which can be under 1)
    /// or a stage's health scale cannot leave the Ork alive.
    /// </summary>
    public static float ExecutionsKillAmount(float health) => MathF.Max(float.IsFinite(health) ? health * 8f : 0f, 10000f);

    /// <summary>
    /// The kill frame of held enemy <paramref name="id"/> by player <paramref name="executor"/>: <see cref="Damage"/> with <see cref="ExecutionsKillAmount"/>, no push, the melee marker and cause
    /// (the normal kill, XP, assist and drop path). False when the hit did not land (an unknown or dying enemy); <paramref name="killed"/> as <see cref="Damage"/>'s.
    /// </summary>
    public bool ExecutionsKill(ushort id, int executor, out bool killed)
    {
        killed = false;
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i]) return false;
        _execKilling = id;
        try { return Damage(id, executor, ExecutionsKillAmount(_health[i]), Vector3.Zero, out killed, marker: 3, cause: MusouDeathKind.Melee); }
        finally { _execKilling = 0; }
    }

    // ---- the hook lines' implementations (MusouSim.Ai.cs, // EXECUTIONS-HOOK) ----

    /// <summary>The per-enemy AI step of slot <paramref name="i"/>: a held Ork stands (no move, push, wind-up, strike, burst, charge, drive or token) at the pair's yaw; <paramref name="held"/> skips its step.</summary>
    partial void ExecutionsHeldStep(int i, ref bool held)
    {
        if (_execHeldCount == 0 || !_execHeld![i]) return;
        held = true;
        _velocity[i] = Vector2.Zero;
        _token[i] = 0; _burst[i] = 0; _aim[i] = -1;
        _drive[i] = Vector2.Zero; _driveTicks[i] = 0;
        _charging[i] = false;
        if (_state[i] != MusouEnemyState.Seek) SetState(i, MusouEnemyState.Seek);   // a wind-up, a strike, a stagger or a fall is cancelled; Seek is an existing state (no wire change)
        _yaw[i] = _execYaw![i];
        _faceWant[i] = Vector2.Zero;
        _headwayTick[i] = Tick; _anchor[i] = _position[i];   // the failsafe never takes a held Ork for stuck
    }

    /// <summary>Slot <paramref name="i"/> was taken by a new spawn: any hold of the slot's earlier enemy is dropped.</summary>
    partial void ExecutionsSpawned(int i)
    {
        if (_execHeld is null || !_execHeld[i]) return;
        _execHeld[i] = false;
        _execHeldCount--;
    }

    // ---- X4 phase 2 (series-37 integration): the hit gates (MusouSim.cs Damage, MusouCombat.cs Stagger, Campaign/MusouSim.Knock.cs KnockDown; // EXECUTIONS-HOOK) ----

    /// <summary>A hit on a held Ork is refused (its own kill frame excepted).</summary>
    partial void ExecutionsDamageHeld(ushort id, ref bool held) { if (ExecutionsBlocksHit(id)) held = true; }

    /// <summary>A held Ork is not staggered.</summary>
    partial void ExecutionsStaggerHeld(ushort id, ref bool held) { if (ExecutionsBlocksHit(id)) held = true; }

    /// <summary>A held Ork is not knocked down.</summary>
    partial void ExecutionsKnockHeld(ushort id, ref bool held) { if (ExecutionsBlocksHit(id)) held = true; }

    /// <summary>The pool was cleared (a new round): every hold is dropped.</summary>
    partial void ExecutionsReset()
    {
        if (_execHeld is not null) Array.Clear(_execHeld);
        _execHeldCount = 0;
        _execKilling = 0;
    }
}
