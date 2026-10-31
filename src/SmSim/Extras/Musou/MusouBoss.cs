using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The Warboss's abilities (MU3; design section B: the design's numbers where it names them, the rest ours, kind: ours). Distances in metres, times in
/// seconds, damage in the players' health and shield points (before their received-damage multipliers and <see cref="MusouRules.PlayerDamageScale"/>).
/// </summary>
public sealed record MusouBossRules
{
    /// <summary>Ground slam: the telegraph (design: 0.8 s), the radius it lands in (design: 6 m), its damage (design: 40, and a stagger), its cooldown
    /// (design: 10 s, counted from the telegraph; ours), the reach a player must be within to start it (ours) and the recovery after it lands (ours).</summary>
    public float SlamWindUp { get; init; } = 0.8f;
    public float SlamRadius { get; init; } = 6f;
    public float SlamDamage { get; init; } = 40f;
    public float SlamCooldown { get; init; } = 10f;
    public float SlamTrigger { get; init; } = 4f;
    public float SlamRecover { get; init; } = 0.6f;
    /// <summary>Charge: the wind-up (design: 1 s), the length (design: 15 m) and speed (design: 12 m/s), the width of the path it hits (design: 1.5 m),
    /// its damage (design: 50) and knockback (ours: 3 m over 0.4 s, the core's stagger), its cooldown (design: 12 s, from the wind-up; ours), the nearest a
    /// player must be to start it (ours; the farthest is the length) and the recovery after it (ours).</summary>
    public float ChargeWindUp { get; init; } = 1f;
    public float ChargeLength { get; init; } = 15f;
    public float ChargeSpeed { get; init; } = 12f;
    public float ChargeWidth { get; init; } = 1.5f;
    public float ChargeDamage { get; init; } = 50f;
    public float ChargeKnockback { get; init; } = 3f;
    public float ChargeKnockbackSeconds { get; init; } = 0.4f;
    public float ChargeCooldown { get; init; } = 12f;
    public float ChargeMin { get; init; } = 6f;
    public float ChargeRecover { get; init; } = 0.8f;
    /// <summary>Seconds before the first slam and the first charge may start (ours).</summary>
    public float SlamFirst { get; init; } = 2f;
    public float ChargeFirst { get; init; } = 3f;
    /// <summary>A player counts for the slam and the charge within this height of the boss (ours).</summary>
    public float VerticalReach { get; init; } = 2f;
    /// <summary>Waaagh!: at these shares of the boss's health, each once (design: 75, 50, 25 %), <see cref="WaaaghBoyz"/> Boyz come through the gates
    /// (design: 20; not scaled, ours) and every other enemy runs <see cref="WaaaghSpeed"/> times as fast (design: +30 %) for <see cref="WaaaghSeconds"/>
    /// (design: 8 s).</summary>
    public IReadOnlyList<float> WaaaghAt { get; init; } = [0.75f, 0.5f, 0.25f];
    public int WaaaghBoyz { get; init; } = 20;
    public float WaaaghSpeed { get; init; } = 1.3f;
    public float WaaaghSeconds { get; init; } = 8f;
    /// <summary>Enrage below this share of its health (design: 25 %): the cooldowns times <see cref="EnrageCooldownScale"/> (design: 0.6).</summary>
    public float EnrageBelow { get; init; } = 0.25f;
    public float EnrageCooldownScale { get; init; } = 0.6f;

    public static MusouBossRules Default { get; } = new();
}

/// <summary>What the Warboss is doing (<see cref="MusouBoss.Action"/>).</summary>
public enum MusouBossAction : byte { None = 0, SlamWindUp = 1, ChargeWindUp = 2, Charging = 3, Recover = 4 }

/// <summary>
/// One hit of the Warboss's abilities on a player this tick, for the World to apply: the player, the damage, where it came from, the push's direction, the
/// knockback (0: the slam's stagger without one) and the ability.
/// </summary>
public readonly record struct MusouBossStrike(int Player, float Damage, Vector3 From, Vector2 Push, float Knockback, MusouBossAbility Ability);

/// <summary>
/// The Warboss's abilities (MU3; design section B; timings by ticks, deterministic). Between abilities the boss is an ordinary enemy (MU1's moves and its
/// swing); an ability starts only from <see cref="MusouEnemyState.Seek"/> and puts it under a script (<see cref="MusouSim.Script"/>) until it recovers.
/// <list type="bullet">
/// <item>Ground slam: a player within <see cref="MusouBossRules.SlamTrigger"/> and the cooldown over: the telegraph (a <see cref="MusouEventKind.Telegraph"/>
/// with <see cref="MusouBossAbility.GroundSlam"/> at the boss's feet; the boss winds up in place), and <see cref="MusouBossRules.SlamWindUp"/> later every
/// living player within <see cref="MusouBossRules.SlamRadius"/> takes the damage and the stagger (a <see cref="MusouEventKind.Strike"/> each).</item>
/// <item>Charge: a player between <see cref="MusouBossRules.ChargeMin"/> and the charge's length and the cooldown over: the telegraph (its end point), the
/// line locked at its start; after the wind-up the boss runs the line at the charge's speed (stopped early by a wall), and every living player its path
/// passes within half the width (plus the player's radius) takes the damage and the knockback, each once.</item>
/// <item>Waaagh!: when its health first falls to each share, a telegraph, the summons (<see cref="MusouDirector.Summon"/>) and the horde's speed
/// (<see cref="MusouSim.Frenzy"/>); several shares crossed at once fire each.</item>
/// <item>Enrage: below its share of health, once: a telegraph, and every cooldown started from then on is shorter.</item>
/// </list>
/// Outside play (<c>fight</c> false) nothing starts and an ability under way stops.
/// </summary>
public sealed class MusouBoss
{
    readonly MusouSim _sim;
    readonly MusouDirector? _director;
    readonly MusouBossRules _rules;
    readonly float _playerRadius;
    readonly bool[] _waaagh;
    readonly HashSet<int> _charged = new();
    Vector2 _line;
    Vector3 _last;
    float _travelled;
    int _recoverTicks;

    /// <param name="director">The director the Waaagh!'s summons go through (null: none, the speed still applies).</param>
    /// <param name="maxHealth">The boss's full health (the shares are of it).</param>
    /// <param name="playerRadius">A player's body radius (the charge's path).</param>
    public MusouBoss(MusouSim sim, MusouDirector? director, ushort id, float maxHealth, float playerRadius = 0.45f)
    {
        _sim = sim;
        _director = director;
        _rules = sim.Rules.Boss;
        Id = id;
        MaxHealth = MathF.Max(1f, maxHealth);
        _playerRadius = MathF.Max(0f, playerRadius);
        _waaagh = new bool[_rules.WaaaghAt.Count];
        SlamCooldown = Ticks(_rules.SlamFirst);
        ChargeCooldown = Ticks(_rules.ChargeFirst);
    }

    public ushort Id { get; }
    public float MaxHealth { get; }
    public MusouBossAction Action { get; private set; }
    /// <summary>Ticks in <see cref="Action"/>.</summary>
    public int ActionTicks { get; private set; }
    /// <summary>Ticks until the slam and the charge may start again.</summary>
    public int SlamCooldown { get; private set; }
    public int ChargeCooldown { get; private set; }
    public bool Enraged { get; private set; }
    /// <summary>Waaagh!s so far.</summary>
    public int Waaaghs { get; private set; }
    /// <summary>The charge's line (unit, the ground plane) and the distance run so far.</summary>
    public Vector2 ChargeLine => _line;
    public float ChargeTravelled => _travelled;

    int Ticks(float seconds) => Math.Max(1, (int)MathF.Round(seconds * _sim.TickRate));
    int Cooldown(float seconds) => Ticks(seconds * (Enraged ? _rules.EnrageCooldownScale : 1f));

    /// <summary>One tick, after the enemies' <see cref="MusouSim.Step"/> of the tick: the abilities; their hits on players go into <paramref name="strikes"/>.</summary>
    public void Step(ReadOnlySpan<MusouTarget> targets, bool fight, List<MusouBossStrike> strikes)
    {
        if (!_sim.TryGet(Id, out var boss) || !boss.Alive) return;
        if (SlamCooldown > 0) SlamCooldown--;
        if (ChargeCooldown > 0) ChargeCooldown--;
        if (fight) Thresholds(boss);
        if (!fight)
        {
            if (Action != MusouBossAction.None) { _sim.Script(Id, false); Action = MusouBossAction.None; ActionTicks = 0; }
            return;
        }
        ActionTicks++;
        switch (Action)
        {
            case MusouBossAction.None:
                if (boss.State != MusouEnemyState.Seek) break;
                int target = Nearest(boss.Position, targets, out float distance);
                if (target < 0) break;
                if (SlamCooldown == 0 && distance <= _rules.SlamTrigger) StartSlam(boss, targets[target]);
                else if (ChargeCooldown == 0 && distance >= _rules.ChargeMin && distance <= _rules.ChargeLength) StartCharge(boss, targets[target]);
                break;
            case MusouBossAction.SlamWindUp:
                if (ActionTicks < Ticks(_rules.SlamWindUp)) break;
                foreach (var t in targets)
                {
                    if (!t.Alive) continue;
                    var d = t.Position - boss.Position;
                    if (MathF.Abs(d.Z) > _rules.VerticalReach || d.X * d.X + d.Y * d.Y > _rules.SlamRadius * _rules.SlamRadius) continue;
                    var push = new Vector2(d.X, d.Y);
                    strikes.Add(new MusouBossStrike(t.Id, _rules.SlamDamage, boss.Position, push.LengthSquared() > 1e-8f ? Vector2.Normalize(push) : Vector2.UnitY, 0f, MusouBossAbility.GroundSlam));
                    _sim.AddEvent(new MusouEvent(MusouEventKind.Strike, _sim.Tick, Id, Clamp(t.Id), (byte)MusouBossAbility.GroundSlam, t.Position));
                }
                Recover(_rules.SlamRecover);
                break;
            case MusouBossAction.ChargeWindUp:
                _sim.Turn(Id, _line);
                if (ActionTicks < Ticks(_rules.ChargeWindUp)) break;
                Action = MusouBossAction.Charging; ActionTicks = 0;
                _sim.Script(Id, true, MusouEnemyState.Strike);
                _sim.Drive(Id, _line * _rules.ChargeSpeed, (int)MathF.Ceiling(_rules.ChargeLength / _rules.ChargeSpeed * _sim.TickRate));
                _last = boss.Position; _travelled = 0f;
                _charged.Clear();
                break;
            case MusouBossAction.Charging:
            {
                // The drive moved it in this tick's step: the path it covered, and the players on it.
                var moved = boss.Position - _last;
                float step = new Vector2(moved.X, moved.Y).Length();
                _travelled += step;
                float reach = _rules.ChargeWidth * 0.5f + _playerRadius;
                foreach (var t in targets)
                {
                    if (!t.Alive || _charged.Contains(t.Id) || MathF.Abs(t.Position.Z - boss.Position.Z) > _rules.VerticalReach) continue;
                    if (MusouSim.FlatDistanceToSegment(t.Position, _last, boss.Position) > reach) continue;
                    _charged.Add(t.Id);
                    var side = new Vector2(t.Position.X - boss.Position.X, t.Position.Y - boss.Position.Y);
                    // Thrown forward along the line and off to the side it stood on (ours).
                    var push = _line + (side.LengthSquared() > 1e-8f ? Vector2.Normalize(side) * 0.5f : Vector2.Zero);
                    strikes.Add(new MusouBossStrike(t.Id, _rules.ChargeDamage, boss.Position, Vector2.Normalize(push), _rules.ChargeKnockback, MusouBossAbility.Charge));
                    _sim.AddEvent(new MusouEvent(MusouEventKind.Strike, _sim.Tick, Id, Clamp(t.Id), (byte)MusouBossAbility.Charge, t.Position));
                }
                _last = boss.Position;
                float expected = _rules.ChargeSpeed / _sim.TickRate;
                // The end: the length run, or a wall (it covered under a third of a stride).
                if (_travelled >= _rules.ChargeLength - 1e-3f || (ActionTicks > 1 && step < expected / 3f)) Recover(_rules.ChargeRecover);
                break;
            }
            case MusouBossAction.Recover:
                if (ActionTicks < _recoverTicks) break;
                _sim.Script(Id, false);
                Action = MusouBossAction.None; ActionTicks = 0;
                break;
        }
    }

    void StartSlam(MusouEnemy boss, MusouTarget target)
    {
        Action = MusouBossAction.SlamWindUp; ActionTicks = 0;
        SlamCooldown = Cooldown(_rules.SlamCooldown);
        _sim.Script(Id, true, MusouEnemyState.WindUp);
        var toward = new Vector2(target.Position.X - boss.Position.X, target.Position.Y - boss.Position.Y);
        _sim.Turn(Id, toward);
        _sim.AddEvent(new MusouEvent(MusouEventKind.Telegraph, _sim.Tick, Id, Clamp(target.Id), (byte)MusouBossAbility.GroundSlam, boss.Position));
    }

    void StartCharge(MusouEnemy boss, MusouTarget target)
    {
        var toward = new Vector2(target.Position.X - boss.Position.X, target.Position.Y - boss.Position.Y);
        _line = toward.LengthSquared() > 1e-8f ? Vector2.Normalize(toward) : new Vector2(-MathF.Sin(boss.Yaw), MathF.Cos(boss.Yaw));
        Action = MusouBossAction.ChargeWindUp; ActionTicks = 0;
        ChargeCooldown = Cooldown(_rules.ChargeCooldown);
        _sim.Script(Id, true, MusouEnemyState.WindUp);
        _sim.Turn(Id, _line);
        _sim.AddEvent(new MusouEvent(MusouEventKind.Telegraph, _sim.Tick, Id, Clamp(target.Id), (byte)MusouBossAbility.Charge,
            boss.Position + new Vector3(_line * _rules.ChargeLength, 0f)));
    }

    void Recover(float seconds)
    {
        Action = MusouBossAction.Recover; ActionTicks = 0;
        _recoverTicks = Ticks(seconds);
        _sim.Script(Id, true, MusouEnemyState.Recover);
        _sim.Drive(Id, Vector2.Zero, 0);   // in place (the charge's drive ends here)
    }

    /// <summary>The Waaagh!s and the enrage, each share once, by the boss's health now.</summary>
    void Thresholds(MusouEnemy boss)
    {
        float share = boss.Health / MaxHealth;
        for (int k = 0; k < _waaagh.Length; k++)
        {
            if (_waaagh[k] || share > _rules.WaaaghAt[k]) continue;
            _waaagh[k] = true;
            Waaaghs++;
            _director?.Summon(MusouEnemyType.Boy, _rules.WaaaghBoyz);
            _sim.Frenzy(_rules.WaaaghSpeed, _rules.WaaaghSeconds);
            _sim.AddEvent(new MusouEvent(MusouEventKind.Telegraph, _sim.Tick, Id, 0, (byte)MusouBossAbility.Waaagh, boss.Position));
        }
        if (!Enraged && share < _rules.EnrageBelow)
        {
            Enraged = true;
            _sim.AddEvent(new MusouEvent(MusouEventKind.Telegraph, _sim.Tick, Id, 0, (byte)MusouBossAbility.Enrage, boss.Position));
        }
    }

    int Nearest(Vector3 from, ReadOnlySpan<MusouTarget> targets, out float distance)
    {
        int best = -1; distance = float.MaxValue;
        for (int t = 0; t < targets.Length; t++)
        {
            if (!targets[t].Alive) continue;
            var d = targets[t].Position - from;
            if (MathF.Abs(d.Z) > _rules.VerticalReach) continue;
            float flat = new Vector2(d.X, d.Y).Length();
            if (flat < distance) { distance = flat; best = t; }
        }
        return best;
    }

    static ushort Clamp(int id) => (ushort)Math.Clamp(id, 0, ushort.MaxValue);
}
