using System.Numerics;
using SmSim.Extras.Warfront.Fortress;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// Warfront's fortress battle (slice W14b): Musou's final assault on an arena that comes through <see cref="IWarfrontArena"/>. The three "positions" are waypoints (Secured from the start, never an objective [ours]); the one
/// Warboss stands at the boss spot, leashed until a human enters the plaza; the arena's elite posts each hold a leashed Nob and Big Shoota Nob and a Boss Ork stands on each approach; the Ork sources open by stage
/// (<see cref="WarfrontFortressState"/>). Nothing here names the generated map. The hooks that call these methods are in <c>WarfrontFortressHooks.cs</c>.
/// </summary>
public sealed partial class TfwBattle
{
    WarfrontFortressState? _fortress;

    /// <summary>Whether this is the fortress battle.</summary>
    public bool IsWarfrontFortress => _fortress is not null;
    /// <summary>The fortress's stage flow (null outside the fortress battle).</summary>
    public WarfrontFortressState? WarfrontFortressFlow => _fortress;
    /// <summary>Humans and others whose ids the run set this tick (<see cref="SetPlayerIds"/>): the length of the run's feet list.</summary>
    internal int FortressPlayerCount => _stormIds.Count;
    /// <summary>The id the run set for the <paramref name="k"/>th feet entry this tick.</summary>
    internal int FortressPlayerId(int k) => _stormIds[k];
    /// <summary>The Boss Orks the fortress stood on its approaches, and the elites it stood on its posts (for tests and the report).</summary>
    public int FortressElitesMade { get; private set; }
    public int FortressBossOrksMade { get; private set; }

    /// <summary>The fortress's own opening: Musou's final-assault start with the Warboss leashed at the boss spot and the posts and approaches held.</summary>
    void FortressStart(WarfrontFortressSetup setup)
    {
        _fortress = new WarfrontFortressState(setup);
        for (int k = 0; k < Positions; k++) _objectives[k] = new TfwObjective((byte)k, TfwPositionState.Secured, 1f, _layout.Positions[k], _layout.Radius(k));
        _objectives[CommanderObjective] = new TfwObjective(CommanderObjective, TfwPositionState.Locked, 0f, _layout.Hq, TfwMap.HqRadius);
        int field = Math.Min(Squads, TfwBattleRules.FinalFieldSquads);
        _reserves = Squads - field;
        PlanColumns();
        PlanStormboys();
        PlaceBigShootas();
        PlanArmyRoutes();
        PlanVantage();
        for (int s = 0; s < field; s++) _guards.SpawnSquad(Ground(_layout.LandingZone + SquadOffset(s), _layout.LandingZone), FaceCentre(_layout.LandingZone));
        Secured = Positions;
        _commander = true;
        for (int k = 0; k <= Positions; k++) _tideTicks[k] = Ticks(TfwBattleRules.CrowdFillSeconds);
        SpawnBoss(MusouEnemyType.Warboss, _enemies.Rules.BossHealth * _scale * _bonus.BossHealthFactor, leashed: true);   // the one Warboss of the run, leashed to the plaza until a human enters it
        if (BossId != 0 && _enemies.TryGet(BossId, out var boss)) BossMaxHealth = boss.Health;   // the level and human scaling were applied at the spawn: the bar's full is what he stands with
        FortressPlace(setup.Plan);
    }

    /// <summary>A leashed Nob and Big Shoota Nob on each elite post; a Boss Ork leashed on each approach (all off the crowds' cap, as Musou's big Orks are).</summary>
    void FortressPlace(WarfrontFortressPlan plan)
    {
        int group = WarfrontFortressBattleRules.GroupFirst;
        foreach (var post in plan.ElitePosts)
        {
            for (int j = 0; j < WarfrontFortressBattleRules.ElitePairSize; j++)
            {
                var type = j % 2 == 0 ? MusouEnemyType.Nob : MusouEnemyType.BigShootaNob;
                float angle = j * MathF.PI;
                var at = Ground(post + new Vector3(MathF.Cos(angle) * 1.5f, MathF.Sin(angle) * 1.5f, 0f), post);
                ushort id = _enemies.SpawnLeashed(type, at, FaceCentre(at), Health(type), group, WarfrontFortressBattleRules.Aggro);
                if (id == 0) continue;
                TrackBig(id);
                FortressElitesMade++;
            }
            group++;
        }
        var kinds = MusouBossOrks.Kinds;
        for (int i = 0; i < plan.ApproachPosts.Count; i++)
        {
            var type = kinds[i % kinds.Length];
            var at = Ground(plan.ApproachPosts[i], plan.ApproachPosts[i]);
            ushort id = _enemies.SpawnLeashed(type, at, FaceCentre(at), Health(type), group++, WarfrontFortressBattleRules.Aggro);
            if (id == 0) continue;
            TrackBig(id);
            _bossOrks.Add(id);
            _bossOrkMax[id] = _enemies.TryGet(id, out var e) ? e.Health : Health(type);
            _bossOrkCount++;
            FortressBossOrksMade++;
        }
    }

    /// <summary>One tick of the fortress's stages from the living humans' feet; the plaza's first human frees the Warboss. No-op outside the fortress.</summary>
    public void FortressObserve(ReadOnlySpan<Vector3> humans)
    {
        if (_fortress is not { } state || Won) return;
        if (state.Observe(humans)) _enemies.ReleaseGroup(CommanderGroup);
    }

    /// <summary>Whether source <paramref name="index"/> sends now (<see cref="WarfrontFortressState.SourceOpen"/>); the first three also check their crowd's anchor at the waypoint.</summary>
    bool FortressSourceActive(int index)
    {
        if (_fortress is not { } state || Won) return false;
        if ((uint)index >= (uint)_layout.Sources.Length) return false;
        return state.SourceOpen(index, _layout.Sources[index], index < Positions ? _layout.Positions[index] : null);
    }
}
