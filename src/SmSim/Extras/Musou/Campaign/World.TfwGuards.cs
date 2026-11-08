using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// The fight of Musou Mode in the World (TF3): the enemies' pool against the players and the Guard squads, and the Guardsmen against the enemies. Runs from
/// <see cref="TfwStep"/> and only while anyone is in the pool or the squads, so a war without a battle costs nothing.
/// </summary>
public sealed partial class World
{
    GuardSim? _tfwGuards;

    /// <summary>The Guardsmen of this war (null outside one). A new battle clears them with <see cref="GuardSim.Clear"/>: a new round does not.</summary>
    public GuardSim? TfwGuards => _tfw is null ? null : _tfwGuards ??= new GuardSim(_musou!) { Sight = Collision };

    /// <summary>One tick of the fight: the players' shots, the enemies against players and squads (players in id order, then squads), their attacks, the Guardsmen, the KOs.</summary>
    void TfwGuardsStep()
    {
        var musou = _musou!;
        var guards = TfwGuards!;
        guards.BeginTick(Tick);
        if (musou.Count == 0 && guards.Count == 0) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool playing = _state == MatchState.Playing;

        int size = _players.Count + GuardSim.MaxSquads + WarRules.Positions;
        if (_musouTargets.Length < size) _musouTargets = new MusouTarget[size * 2];
        _musouIds.Clear();
        foreach (int id in _players.Keys) _musouIds.Add(id);
        _musouIds.Sort();
        int n = 0;
        foreach (int id in _musouIds) { var p = _players[id]; _musouTargets[n++] = new MusouTarget(id, p.Position, p.Alive); }
        n += guards.AppendTargets(_musouTargets.AsSpan(n));
        if (_tfw!.Battle is { } battle) n += battle.AppendGoals(_musouTargets.AsSpan(n));

        if (playing) MusouPlayerShots();
        TfwCreditKills();
        _musouAttacks.Clear();
        long section = SimProfile.Begin();   // musou/perf-pass: SM_TICK_STATS's sections
        musou.Step(_musouTargets.AsSpan(0, n), Collision, playing, _musouAttacks);
        SimProfile.End("tfw: ork ai", section);
        section = SimProfile.Begin();
        foreach (var attack in _musouAttacks) MusouHit(attack);
        SimProfile.End("tfw: ork hits", section);
        MusouStormboyCrashes();   // World.MusouStormboy.cs: shot-down Stormboys that struck the ground (expansion S3)
        if (playing) TfwShoves();   // World.TfwMelee.cs
        section = SimProfile.Begin();
        guards.Step(playing);
        SimProfile.End("tfw: guards", section);
        TfwCreditKills();
        musou.LastStepMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>
    /// An enemy's attack on a Guard squad: the squad's man nearest the attacker takes it (no player multipliers). A Boss Ork's blow cleaves (owner, 2026-11-06): it strikes every man in its arc, any
    /// squad's, as the players' cleave strikes Orks (<see cref="TfwGuardCleave"/>).
    /// </summary>
    void TfwGuardsHit(MusouAttack attack)
    {
        if (TfwGuards is not { } guards) return;
        if (attack.Melee && _musou!.TryGet(attack.Enemy, out var e) && e.Alive && MusouBossOrks.Is(e.Type) && TfwGuardCleave(guards, e, attack.Damage) > 0) return;
        guards.Hit(TfwContracts.SquadOf(attack.Player), attack.Damage, attack.From);
    }

    /// <summary>
    /// A Boss Ork's cleave on the Guardsmen, by the players' cleave's rules (<see cref="MusouCleave"/>): its reach is its own melee reach plus its body, a Guardsman's and
    /// <see cref="MusouCombatRules.CleaveExtraRange"/>; its arc the core's melee cone times <see cref="MusouCombatRules.CleaveConeScale"/>; at most <see cref="TfwCombatRules.CleaveTargets"/>, nearest first;
    /// each falls or dies as a fodder Ork the players strike (<see cref="GuardSim.Cleave"/>). How many it struck (0: none in its arc, and the blow lands as any other).
    /// </summary>
    int TfwGuardCleave(GuardSim guards, in MusouEnemy boss, float damage)
    {
        var musou = _musou!;
        var spec = musou.Rules.Of(boss.Type);
        var combat = musou.Rules.Combat;
        var war = musou.War ?? TfwCombatRules.Default;
        float reach = spec.MeleeRange + spec.Radius + guards.Rules.Radius + combat.CleaveExtraRange;
        float minCos = MathF.Cos(MathF.Min(180f, Config.MeleeConeDegrees * combat.CleaveConeScale) * MathF.PI / 180f);
        var forward = new System.Numerics.Vector2(-MathF.Sin(boss.Yaw), MathF.Cos(boss.Yaw));
        int struck = guards.Cleave(boss.Position, forward, reach, minCos, war.CleaveTargets, damage, war);
        TfwBestGuardCleave = Math.Max(TfwBestGuardCleave, struck);
        return struck;
    }

    /// <summary>The most Guardsmen one Boss Ork blow has struck (the server's log, tests).</summary>
    public int TfwBestGuardCleave { get; private set; }
}
