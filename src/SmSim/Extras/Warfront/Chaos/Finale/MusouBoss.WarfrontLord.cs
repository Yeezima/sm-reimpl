using System.Numerics;
using SmSim.Extras.Warfront.Chaos.Finale;

namespace SmSim.Extras.Musou;

/// <summary>
/// The Chaos Lord's duel kit on the Warboss's boss code (Chaos Warfront C5; design "The Lord's AI"): his rules come from <see cref="MusouSim.WarfrontLordRules"/> (no Waaagh!, the slam's telegraph and
/// the recoveries x1.5); while the duel is on (<see cref="MusouSim.WarfrontLordDuelOn"/>) he also
/// <list type="bullet">
/// <item>leaps: the duelist beyond <see cref="WarfrontDuelRules.LeapBeyond"/> m for <see cref="WarfrontDuelRules.LeapAfterSeconds"/> s, a marker at the duelist's feet for <see cref="WarfrontDuelRules.LeapMarkerSeconds"/> s
/// (he winds up in place), then he lands there with a slam of <see cref="WarfrontDuelRules.LeapRadius"/> m (the slam's damage) and recovers as from a slam; <see cref="WarfrontDuelRules.LeapCooldownSeconds"/> s to the next;</item>
/// <item>fires his bolt beyond <see cref="WarfrontDuelRules.BoltBeyond"/> m: the Chaos Marine bolter row's volley (its rounds, its period, its near and far hit chances, a hash roll), each round's damage x the bosses' danger;</item>
/// <item>raises a telegraph (the wire's kind 17) for each slam, charge, bolt and leap.</item>
/// </list>
/// The slam and the charge stay the Warboss's own (their choice in <see cref="Step"/>). Outside the duel nothing here acts.
/// </summary>
public sealed partial class MusouBoss
{
    bool _leaping;
    Vector3 _leapAt;
    int _leapTicks, _farTicks, _leapCooldown, _boltCooldown;
    MusouBossAction _toldAction;

    /// <summary>Whether the Lord is in his leap's wind-up (tests).</summary>
    public bool Leaping => _leaping;
    /// <summary>Ticks until he may leap again (tests).</summary>
    public int LeapCooldown => _leapCooldown;
    /// <summary>The rules this boss fights by (tests: the Lord's).</summary>
    public MusouBossRules Rules => _rules;

    partial void WarfrontBossRules(ushort id, ref MusouBossRules rules) => _sim.WarfrontLordRules(id, ref rules);

    /// <summary>The handoff's clean slate (C5): the ability under way dropped, every cooldown back to its first, the leap and bolt forgotten. Health is not the boss code's to touch.</summary>
    public void WarfrontLordSlate()
    {
        Action = MusouBossAction.None; ActionTicks = 0;
        _charged.Clear();
        SlamCooldown = Ticks(_rules.SlamFirst);
        ChargeCooldown = Ticks(_rules.ChargeFirst);
        _leaping = false; _leapTicks = 0; _farTicks = 0; _leapCooldown = 0; _boltCooldown = 0;
        _toldAction = MusouBossAction.None;
        _relocations = _sim.Relocations(Id);
    }

    partial void WarfrontBossStep(MusouEnemy boss, ReadOnlySpan<MusouTarget> targets, List<MusouBossStrike> strikes, ref bool handled)
    {
        if (!_sim.WarfrontLordDuelOn(Id, out int duelist)) { _leaping = false; _farTicks = 0; _toldAction = Action; return; }
        if (_leapCooldown > 0) _leapCooldown--;
        if (_boltCooldown > 0) _boltCooldown--;
        int t = -1;
        for (int k = 0; k < targets.Length; k++) if (targets[k].Id == duelist && targets[k].Alive) { t = k; break; }
        float distance = float.PositiveInfinity;
        if (t >= 0) distance = new Vector2(targets[t].Position.X - boss.Position.X, targets[t].Position.Y - boss.Position.Y).Length();
        _farTicks = distance > WarfrontDuelRules.LeapBeyond && t >= 0 ? _farTicks + 1 : 0;

        // The Warboss's own slam and charge, told once at their start (their wind-up began last tick).
        if (Action != _toldAction)
        {
            if (Action == MusouBossAction.SlamWindUp) _sim.AddLordTelegraph(new(_sim.Tick, WarfrontLordTelegraph.Slam, boss.Position, _rules.SlamWindUp));
            else if (Action == MusouBossAction.ChargeWindUp) _sim.AddLordTelegraph(new(_sim.Tick, WarfrontLordTelegraph.Charge, boss.Position + new Vector3(_line * _rules.ChargeLength, 0f), _rules.ChargeWindUp));
            _toldAction = Action;
        }

        if (_leaping)
        {
            handled = true;
            if (++_leapTicks < Ticks(WarfrontDuelRules.LeapMarkerSeconds)) return;
            _leaping = false;
            if (_sim.Relocate(Id, _leapAt)) _relocations = _sim.Relocations(Id);
            _sim.TryGet(Id, out var landed);
            float r2 = WarfrontDuelRules.LeapRadius * WarfrontDuelRules.LeapRadius;
            foreach (var target in targets)
            {
                if (!target.Alive) continue;
                var d = target.Position - landed.Position;
                if (MathF.Abs(d.Z) > _rules.VerticalReach || d.X * d.X + d.Y * d.Y > r2) continue;
                var push = new Vector2(d.X, d.Y);
                strikes.Add(new MusouBossStrike(target.Id, _rules.SlamDamage, landed.Position, push.LengthSquared() > 1e-8f ? Vector2.Normalize(push) : Vector2.UnitY, 0f, MusouBossAbility.GroundSlam));
                _sim.AddEvent(new MusouEvent(MusouEventKind.Strike, _sim.Tick, Id, Clamp(target.Id), (byte)MusouBossAbility.GroundSlam, target.Position));
            }
            _leapCooldown = Ticks(WarfrontDuelRules.LeapCooldownSeconds);
            Recover(_rules.SlamRecover);
            _toldAction = Action;
            return;
        }

        if (Action != MusouBossAction.None || boss.State != MusouEnemyState.Seek || t < 0) return;
        if (_leapCooldown == 0 && _farTicks >= Ticks(WarfrontDuelRules.LeapAfterSeconds))
        {
            _leaping = true; _leapTicks = 0; _farTicks = 0;
            _leapAt = targets[t].Position;
            _sim.Script(Id, true, MusouEnemyState.WindUp);
            _sim.Turn(Id, new Vector2(_leapAt.X - boss.Position.X, _leapAt.Y - boss.Position.Y));
            _sim.AddLordTelegraph(new(_sim.Tick, WarfrontLordTelegraph.Leap, _leapAt, WarfrontDuelRules.LeapMarkerSeconds));
            handled = true;
            return;
        }
        if (_boltCooldown == 0 && distance > WarfrontDuelRules.BoltBeyond) Bolt(boss, targets[t]);
    }

    /// <summary>One volley of the bolt at the duelist: the Chaos Marine row's rounds, each hitting by its near-to-far chance over the row's band (a roll of the tick, the round and the Lord).</summary>
    void Bolt(MusouEnemy boss, MusouTarget target)
    {
        var row = _sim.Rules.Of(MusouEnemyType.ChaosMarine);
        _boltCooldown = Ticks(MathF.Max(0.1f, row.RangedPeriod));
        float d = Vector3.Distance(boss.Position, target.Position);
        float span = MathF.Max(1e-3f, row.RangedMax - row.RangedMin);
        float share = Math.Clamp((d - row.RangedMin) / span, 0f, 1f);
        float chance = row.RangedHitNear + (row.RangedHitFar - row.RangedHitNear) * share;
        var from = MusouSim.Chest(boss.Position, _sim.Body(boss.Type).Height);
        _sim.AddLordTelegraph(new(_sim.Tick, WarfrontLordTelegraph.Bolt, from, 0f));
        for (int r = 0; r < Math.Max(1, row.RangedRounds); r++)
        {
            float roll = MusouSim.Unit(MusouSim.Mix(MusouSim.Mix(_sim.Tick, (uint)Id * 7919u), 0xB017_0000u + (uint)r));
            if (roll < chance) _sim.AddLordBolt(new WarfrontLordBolt(Id, target.Id, row.RangedDamage * WarfrontDuelRules.BoltDamageScale, from));
        }
    }
}
