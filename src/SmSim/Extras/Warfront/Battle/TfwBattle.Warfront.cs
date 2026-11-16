using System.Numerics;
using SmSim.Extras.Warfront;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// Warfront's part of the battle (slice W3): the fourth objective D is a capture point with an elite garrison in place of the Warboss, and securing it wins the node. No Warboss is ever spawned and no victory wave
/// starts. Every hook returns at once unless the battle is a Warfront node (<see cref="WarfrontNode"/>, set by the run's hook), or, for the scale, unless the enemies' pool holds the Warfront rules.
/// </summary>
public sealed partial class TfwBattle
{
    /// <summary>Whether this battle is a Warfront node (set before <see cref="Start"/>).</summary>
    public bool WarfrontNode { get; internal set; }

    partial void WarfrontPlayerScale(int humans, ref float scale)
    {
        if (_enemies.Warfront is not { } rules) return;
        rules.PlayerScaleNow = WarfrontBattleRules.PlayerScale(humans);   // applied once, at every spawn (MusouSim.Warfront.cs)
        scale = 1f;   // so Musou's own per-player scale (the bosses' health) is not applied on top
    }

    /// <summary>A, B and C secured at some time: D unlocks (as the commander came, whatever has been retaken since), held by the commander's escort and one Boss Nob, all leashed.</summary>
    partial void WarfrontCommander(ref bool handled)
    {
        if (!WarfrontNode) return;
        handled = true;
        _commander = true;
        _tideTicks[Positions] = Ticks(TfwBattleRules.CrowdFillSeconds);
        _objectives[CommanderObjective] = _objectives[CommanderObjective] with { State = TfwPositionState.Held };
        SpawnEscort();   // 2 Nobz (twins) and 5 'Ard Boyz at D, as the commander's escort; no Warboss
        for (int k = 0; k < WarfrontBattleRules.GarrisonBossNobs; k++)
        {
            float angle = (k + 0.5f) * MathF.Tau / Math.Max(1, WarfrontBattleRules.GarrisonBossNobs);
            var at = Ground(_layout.Hq + new Vector3(MathF.Cos(angle) * 6f, MathF.Sin(angle) * 6f, 0f), _layout.Hq);
            ushort id = _enemies.SpawnLeashed(MusouEnemyType.BossNob, at, FaceCentre(at), Health(MusouEnemyType.BossNob), CommanderGroup, TfwBattleRules.DefenderAggro);
            TrackBig(id);
        }
    }

    partial void WarfrontObjectives(ReadOnlySpan<Vector3> players, ref TfwBattleChange change)
    {
        if (!WarfrontNode) return;
        change &= ~TfwBattleChange.DepotSecured;   // no transport or supplies from the depot in Warfront
        if (_enemies.Warfront is { } rules) rules.PlayerScaleNow = WarfrontBattleRules.PlayerScale(_humansNow);   // the humans of now (a late joiner or a leaver changes the Orks spawned from here on)
        if (!_commander || Won) return;
        var o = _objectives[CommanderObjective];
        var at = _layout.Hq;
        float reach = _layout.Reach(CommanderObjective), r2 = TfwMap.HqRadius * TfwMap.HqRadius;
        bool capturers = false;
        foreach (var p in Capturers(players))
            if (MathF.Abs(p.Z - at.Z) <= reach && new Vector2(p.X - at.X, p.Y - at.Y).LengthSquared() <= r2) { capturers = true; break; }
        if (!capturers)
        {
            _objectives[CommanderObjective] = o with { State = TfwPositionState.Held, Progress = MathF.Max(0f, o.Progress - TfwBattleRules.SecureDecayPerSecond * Dt) };
            return;
        }
        Span<ushort> one = stackalloc ushort[1];
        bool orks = _enemies.Query(at, TfwMap.HqRadius, one, reach) > 0;
        o = o with { State = orks ? TfwPositionState.Clearing : TfwPositionState.Securing, Progress = o.Progress + Dt / TfwBattleRules.SecureSeconds };
        if (o.Progress < 1f) { _objectives[CommanderObjective] = o; return; }
        _objectives[CommanderObjective] = o with { State = TfwPositionState.Secured, Progress = 1f };
        Won = true;
        change |= TfwBattleChange.Won;
    }
}
