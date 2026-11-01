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
    public GuardSim? TfwGuards => _tfw is null ? null : _tfwGuards ??= new GuardSim(_musou!);

    /// <summary>One tick of the fight: the players' shots, the enemies against players and squads (players in id order, then squads), their attacks, the Guardsmen, the KOs.</summary>
    void TfwGuardsStep()
    {
        var musou = _musou!;
        var guards = TfwGuards!;
        guards.BeginTick(Tick);
        if (musou.Count == 0 && guards.Count == 0) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool playing = _state == MatchState.Playing;

        int size = _players.Count + GuardSim.MaxSquads;
        if (_musouTargets.Length < size) _musouTargets = new MusouTarget[size * 2];
        _musouIds.Clear();
        foreach (int id in _players.Keys) _musouIds.Add(id);
        _musouIds.Sort();
        int n = 0;
        foreach (int id in _musouIds) { var p = _players[id]; _musouTargets[n++] = new MusouTarget(id, p.Position, p.Alive); }
        n += guards.AppendTargets(_musouTargets.AsSpan(n));

        if (playing) MusouPlayerShots();
        TfwCreditKills();
        _musouAttacks.Clear();
        musou.Step(_musouTargets.AsSpan(0, n), Collision, playing, _musouAttacks);
        foreach (var attack in _musouAttacks) MusouHit(attack);
        guards.Step(playing);
        TfwCreditKills();
        musou.LastStepMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>An enemy's attack on a Guard squad: the squad's man nearest the attacker takes it (no player multipliers).</summary>
    void TfwGuardsHit(MusouAttack attack) => TfwGuards?.Hit(TfwContracts.SquadOf(attack.Player), attack.Damage, attack.From);
}
