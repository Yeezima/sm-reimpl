using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The players' attacks on the Musou enemies (MU3; design section B; every value ours, kind: ours, unless the design names it). Distances in metres, times
/// in seconds. Hashed into the config only through <see cref="MusouSim.Version"/>.
/// </summary>
public sealed record MusouCombatRules
{
    /// <summary>The cleave reaches this much past the core's <see cref="SimConfig.MeleeRange"/> (design: 0.5 m) …</summary>
    public float CleaveExtraRange { get; init; } = 0.5f;
    /// <summary>… within the core's <see cref="SimConfig.MeleeConeDegrees"/> times this (design: 1.5) …</summary>
    public float CleaveConeScale { get; init; } = 1.5f;
    /// <summary>… on at most this many enemies, nearest first (design: 8).</summary>
    public int CleaveTargets { get; init; } = 8;
    /// <summary>A heavy hit's knockback (design: 2 m) and the most a blast's gives (at full intensity; scaled by it; ours).</summary>
    public float HeavyKnockback { get; init; } = 2f;
    public float BlastKnockback { get; init; } = 2f;
    /// <summary>Seconds a knockback takes (ours).</summary>
    public float KnockbackSeconds { get; init; } = 0.25f;
    /// <summary>Seconds a stagger holds an enemy (MU1's 0.6 s; ours).</summary>
    public float StaggerSeconds { get; init; } = 0.6f;
    /// <summary>A Nob is knocked back this share of the distance (ours; it still staggers). The Warboss is stagger-immune (design).</summary>
    public float NobKnockbackScale { get; init; } = 0.5f;
    /// <summary>A blast staggers only from this intensity (ours: the outer ring only hurts).</summary>
    public float BlastStaggerIntensity { get; init; } = 0.25f;
    /// <summary>Most enemies one charge hits (ours: as the cleave).</summary>
    public int ChargeTargets { get; init; } = 8;

    public static MusouCombatRules Default { get; } = new();
}

/// <summary>
/// MU3's additions to the enemies (design section B): the stagger (<see cref="Stagger"/>, the Warboss immune), the drive (a knockback or the boss's charge:
/// a velocity on top of the enemy's own move for some ticks, kept on the graph by the move), the script (an enemy whose ability moves it, the Warboss's:
/// its own decisions wait), the Waaagh!'s speed (<see cref="Frenzy"/>) and the shapes the players' cones and blasts are tested against.
/// </summary>
public sealed partial class MusouSim
{
    readonly bool[] _scripted = new bool[Capacity];
    readonly Vector2[] _drive = new Vector2[Capacity];
    readonly ushort[] _driveTicks = new ushort[Capacity];
    int _frenzyTicks;
    float _frenzyScale = 1f;

    /// <summary>Ticks left of the Waaagh!'s speed (every enemy but the Warboss runs <see cref="FrenzyScale"/> times as fast).</summary>
    public int FrenzyTicks => _frenzyTicks;
    public float FrenzyScale => _frenzyTicks > 0 ? _frenzyScale : 1f;

    void SpawnedCombat(int i)
    {
        _scripted[i] = false; _drive[i] = Vector2.Zero; _driveTicks[i] = 0;
    }

    float SpeedScale(int i) => _frenzyTicks > 0 && _type[i] != MusouEnemyType.Warboss ? _frenzyScale : 1f;

    /// <summary>The drive of this tick (and one tick less of it).</summary>
    Vector2 Driven(int i)
    {
        if (_driveTicks[i] == 0) return Vector2.Zero;
        _driveTicks[i]--;
        return _drive[i];
    }

    /// <summary>Every enemy but the Warboss runs <paramref name="scale"/> times as fast for <paramref name="seconds"/> (a later call replaces it).</summary>
    public void Frenzy(float scale, float seconds)
    {
        if (!(scale > 0f) || !float.IsFinite(scale)) return;
        _frenzyScale = scale;
        _frenzyTicks = Ticks(seconds);
    }

    /// <summary>
    /// Staggers an enemy alive (a spawning one too, which ends its stand): <see cref="MusouEnemyState.Stagger"/> for <see cref="MusouCombatRules.StaggerSeconds"/> (a wind-up
    /// or burst is lost, so is its attack token) and a knockback of <paramref name="distance"/> along <paramref name="push"/> (a Nob a share of it). False for
    /// the Warboss (stagger-immune) and anything dying or unknown.
    /// </summary>
    public bool Stagger(ushort id, Vector2 push, float distance)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i] || _state[i] == MusouEnemyState.Dying || _type[i] == MusouEnemyType.Warboss) return false;
        SetState(i, MusouEnemyState.Stagger);
        _token[i] = 0; _burst[i] = 0;
        if (_type[i] == MusouEnemyType.Nob) distance *= Rules.Combat.NobKnockbackScale;
        Knock(i, push, distance);
        return true;
    }

    void Knock(int i, Vector2 push, float distance)
    {
        float length = push.Length();
        if (!(distance > 0f) || !(length > 1e-6f)) return;
        ushort ticks = Ticks(Rules.Combat.KnockbackSeconds);
        Drive((ushort)(i + 1), push / length * (distance / (ticks * Dt)), ticks);
    }

    /// <summary>For <paramref name="ticks"/> ticks the enemy moves <paramref name="velocity"/> on top of its own move (the graph still holds it).</summary>
    public void Drive(ushort id, Vector2 velocity, int ticks)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i] || !float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y)) return;
        _drive[i] = velocity;
        _driveTicks[i] = (ushort)Math.Clamp(ticks, 0, ushort.MaxValue);
    }

    /// <summary>
    /// Puts an enemy under a script (<paramref name="on"/>: its own decisions wait, it still moves by its drive and the crowd) in <paramref name="state"/>, or
    /// gives it back to its own decisions (in <see cref="MusouEnemyState.Seek"/>). The Warboss's abilities (<see cref="MusouBoss"/>).
    /// </summary>
    public void Script(ushort id, bool on, MusouEnemyState state = MusouEnemyState.Seek)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i] || _state[i] == MusouEnemyState.Dying) return;
        _scripted[i] = on;
        if (!on) { _drive[i] = Vector2.Zero; _driveTicks[i] = 0; state = MusouEnemyState.Seek; }
        if (_state[i] != state) SetState(i, state);
    }

    /// <summary>Turns an enemy to face <paramref name="direction"/> (the ground plane).</summary>
    public void Turn(ushort id, Vector2 direction)
    {
        int i = id - 1;
        if (i >= 0 && i < Capacity && _used[i]) Face(i, direction);
    }

    /// <summary>An enemy type's body radius and height as the shots see them (the Warboss scaled by <see cref="MusouRules.BossScale"/>).</summary>
    public (float Radius, float Height) Body(MusouEnemyType type)
    {
        var spec = Rules.Of(type);
        float scale = type == MusouEnemyType.Warboss ? Rules.BossScale : 1f;
        return (spec.Radius * scale, spec.Height * scale);
    }

    /// <summary>The point a blast or a cone measures an enemy's distance to: its chest (0.55 of its height; ours).</summary>
    public static Vector3 Chest(Vector3 feet, float height) => feet + new Vector3(0f, 0f, height * 0.55f);

    /// <summary>
    /// The share of an enemy's body a cone covers (as <see cref="Cones.Coverage"/> for players: 21 fixed points, the head and five rings of four on the
    /// body axis plus the axis; ours).
    /// </summary>
    public static float ConeCoverage(ConeSpec cone, Vector3 origin, Vector3 direction, Vector3 feet, float radius, float height)
    {
        int inside = 0, total = 0;
        float headRadius = 0.22f * height / 1.9f;
        if (Cones.Inside(cone, origin, direction, feet + new Vector3(0f, 0f, height - headRadius))) inside++;
        total++;
        float r = radius * 0.7f, top = height - 2f * headRadius;
        for (int k = 0; k < 5; k++)
        {
            var axis = feet + new Vector3(0f, 0f, top * (k + 0.5f) / 5f);
            Span<Vector3> ring = [axis, axis + new Vector3(r, 0f, 0f), axis - new Vector3(r, 0f, 0f), axis + new Vector3(0f, r, 0f), axis - new Vector3(0f, r, 0f)];
            foreach (var p in ring) { total++; if (Cones.Inside(cone, origin, direction, p)) inside++; }
        }
        return (float)inside / total;
    }

    /// <summary>
    /// Whether a player's melee step is a heavy hit for the enemies (it staggers and knocks back): a heavy strike, a step whose primary reaction is a stun or
    /// a stagger (the light combo's stun strike), or one with a stomp.
    /// </summary>
    public static bool Heavy(PlayerState attacker, MeleeStep step) =>
        attacker.MeleeHeavy || step.Stomp is not null || step.PrimaryReaction.Kind is ReactionKind.Stun or ReactionKind.Stagger;

    /// <summary>The horizontal distance from <paramref name="p"/> to the segment <paramref name="a"/>-<paramref name="b"/>.</summary>
    public static float FlatDistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = new Vector2(b.X - a.X, b.Y - a.Y);
        var ap = new Vector2(p.X - a.X, p.Y - a.Y);
        float l2 = ab.LengthSquared();
        float t = l2 > 1e-9f ? Math.Clamp(Vector2.Dot(ap, ab) / l2, 0f, 1f) : 0f;
        return (ap - ab * t).Length();
    }
}
