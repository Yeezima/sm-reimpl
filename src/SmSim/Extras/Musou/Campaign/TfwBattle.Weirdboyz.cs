using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// When and where the Weirdboyz come in a battle (Musou Mode only; spec "Musou Mode: the Weirdboy"; every value ours). In Exterminatus they teleport in (the data's <c>cin_teleportin</c>,
/// <c>weirdboy_teleport_in</c>), so here they appear near a player rather than walk from a spawn point. A change raises <see cref="TfwRules.Version"/>.
/// </summary>
public static class TfwWeirdboyRules
{
    /// <summary>The first of a battle: this long after the landing, plus up to <see cref="FirstSpread"/> more (ours).</summary>
    public const float FirstSeconds = 45f, FirstSpread = 45f;
    /// <summary>The next: this long after the last one came, plus up to <see cref="NextSpread"/> more (ours).</summary>
    public const float NextSeconds = 60f, NextSpread = 60f;
    /// <summary>Most alive at once (ours); one that is due waits for a free place.</summary>
    public const int MaxAlive = 2;
    /// <summary>He appears this far from the player he comes for (ours: inside his band of 20 to 50 m).</summary>
    public const float ArriveMin = 25f, ArriveMax = 40f;
    /// <summary>Seconds between two tries when one is due and nowhere would do, or the pool is full.</summary>
    public const float RetrySeconds = 1f;
}

public sealed partial class TfwBattle
{
    readonly List<ushort> _weirdboyz = new();
    int _weirdboyDue = -1;   // the battle's tick the next one is due on (-1: not yet drawn)
    int _weirdboyCount;

    /// <summary>Test aid and the server's log: called once for every Weirdboy that comes, with its id and where.</summary>
    public Action<ushort, Vector3>? OnWeirdboy { get; set; }
    /// <summary>Whether Weirdboyz come in this battle (ours: always in a battle, never in the final assault; tests turn it off to count the crowds alone).</summary>
    public bool WeirdboyzOn { get; set; } = true;
    /// <summary>The Weirdboyz alive now, in the order they came.</summary>
    public IReadOnlyList<ushort> Weirdboyz => _weirdboyz;
    /// <summary>Weirdboyz that came in this battle.</summary>
    public int WeirdboyzCome => _weirdboyCount;
    /// <summary>The battle's tick the next Weirdboy is due on (-1 before the first step).</summary>
    public int WeirdboyDueTick => _weirdboyDue;

    /// <summary>
    /// Once a tick from <see cref="Step"/> (a battle, not the final assault): the dead are forgotten; when one is due, fewer than <see cref="TfwWeirdboyRules.MaxAlive"/> live, the pool has room and
    /// someone is in the field, a Weirdboy appears near one of them (a roll picks whom), and the next is drawn.
    /// </summary>
    void StepWeirdboyz(ReadOnlySpan<Vector3> players)
    {
        for (int k = _weirdboyz.Count - 1; k >= 0; k--)
            if (!_enemies.TryGet(_weirdboyz[k], out var e) || !e.Alive || e.Type != MusouEnemyType.Weirdboy) _weirdboyz.RemoveAt(k);
        if (_final || !WeirdboyzOn) return;
        if (_weirdboyDue < 0) _weirdboyDue = _now + Ticks(TfwWeirdboyRules.FirstSeconds + TfwWeirdboyRules.FirstSpread * Roll(0x3E1D_0001u));
        if (_now < _weirdboyDue || _weirdboyz.Count >= TfwWeirdboyRules.MaxAlive || players.IsEmpty) return;
        if (PoolUsed + 1 > TfwBattleRules.CrowdPoolRoom || !SpawnWeirdboy(players))
        {
            _weirdboyDue = _now + Ticks(TfwWeirdboyRules.RetrySeconds);
            return;
        }
        _weirdboyDue = _now + Ticks(TfwWeirdboyRules.NextSeconds + TfwWeirdboyRules.NextSpread * Roll(0x3E1D_1000u + (uint)_weirdboyCount));
    }

    /// <summary>A Weirdboy near a player picked by a roll: <see cref="TfwWeirdboyRules.ArriveMin"/> to <see cref="TfwWeirdboyRules.ArriveMax"/> from it, on the graph, in eight directions from a rolled one.</summary>
    bool SpawnWeirdboy(ReadOnlySpan<Vector3> players)
    {
        var player = players[Math.Min(players.Length - 1, (int)(Roll(0x3E1D_2000u + (uint)_weirdboyCount) * players.Length))];
        float start = Roll(0x3E1D_3000u + (uint)_weirdboyCount) * 2f * MathF.PI;
        float radius = TfwWeirdboyRules.ArriveMin + (TfwWeirdboyRules.ArriveMax - TfwWeirdboyRules.ArriveMin) * Roll(0x3E1D_4000u + (uint)_weirdboyCount);
        for (int k = 0; k < 8; k++)
        {
            float angle = start + k * MathF.PI / 4f;
            var spot = player + new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f);
            int node = -1;
            if (_enemies.Nav is { } nav)
            {
                node = nav.Nearest(spot, 3f, 6f);
                if (node < 0) continue;
                if (_enemies.Walk?.Nearest(nav.Position(node)) is int walk and >= 0 && !_enemies.Reachable(walk)) continue;   // never where nobody can walk to him
                spot = nav.Position(node);
            }
            ushort id = _enemies.Spawn(MusouEnemyType.Weirdboy, spot, YawTo(spot, player), Health(MusouEnemyType.Weirdboy), node);
            if (id == 0) return false;
            _weirdboyz.Add(id);
            _weirdboyCount++;
            OnWeirdboy?.Invoke(id, spot);
            return true;
        }
        return false;
    }
}
