using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// Expansion S2 in the World (Musou Mode only): the Bomb Squigs' drop director, the players' kicks, and the Ork blasts of the tick (the rockets that burst, then the Squigs queued to burst:
/// <see cref="MusouSquigRules"/>, <see cref="MusouRokkitRules"/>). Runs from <see cref="TfwStep"/> after the fight, in play only; the 5-wave run and the skirmish never get here.
/// </summary>
public sealed partial class World
{
    readonly Dictionary<int, uint> _squigDue = new();   // human player id: the tick his next Squig is due on
    TfwBattle? _squigBattle;
    int _squigDrops;
    readonly List<(ushort Id, int Credit)> _squigQueue = new();
    readonly List<MusouRocketBlast> _rocketQueue = new();

    /// <summary>Bomb Squigs dropped in this war (tests, the server's log).</summary>
    public int TfwSquigsDropped => _squigDrops;
    /// <summary>Test aid: whether the drop director runs (ours: always; tests turn it off to place Squigs themselves).</summary>
    public bool TfwSquigDropsOn { get; set; } = true;

    /// <summary>
    /// Capture aid (ours; the server's <c>SM_MUSOU_S2_DEMO=s</c>): in a battle, every <c>s</c> seconds a Squig lands 11 m in front of the first living human (on screen, so a capture shows its
    /// fall, fuse and burst) and a Rokkit Boy 30 m in front of him fires one rocket that passes beside him. 0: off (every match).
    /// </summary>
    public float TfwS2DemoSeconds { get; set; }
    uint _s2DemoNext;
    ushort _s2DemoRokkit;

    void TfwS2Demo(MusouSim musou)
    {
        if (!(TfwS2DemoSeconds > 0f) || _tfw?.Battle is null || Tick < _s2DemoNext) return;
        _s2DemoNext = Tick + (uint)Math.Max(1, (int)(TfwS2DemoSeconds * Config.TickRate));
        foreach (int id in _musouIds)
        {
            if (TfwContracts.IsPuppet(id) || !_players[id].Alive) continue;
            var p = _players[id];
            var ahead = new Vector3(-MathF.Sin(p.Yaw), MathF.Cos(p.Yaw), 0f);
            var side = new Vector3(ahead.Y, -ahead.X, 0f);
            int node = musou.Walk?.Nearest(p.Position + ahead * 11f, 3f, 6f) ?? -1;
            var at = node >= 0 ? musou.Walk!.Position(node) : p.Position + ahead * 11f;
            ushort squig = musou.Spawn(MusouEnemyType.BombSquig, at, p.Yaw + MathF.PI, musou.Rules.Of(MusouEnemyType.BombSquig).Health, node);
            if (squig != 0) musou.AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, squig, (ushort)id, MusouSquigRules.DropCue, at));
            var from = p.Position + ahead * 30f + side * 6f;
            int rnode = musou.Walk?.Nearest(from, 3f, 6f) ?? -1;
            if (rnode >= 0) from = musou.Walk!.Position(rnode);
            if (!(musou.TryGet(_s2DemoRokkit, out var r) && r.Alive && r.Type == MusouEnemyType.RokkitBoy))
                _s2DemoRokkit = musou.Spawn(MusouEnemyType.RokkitBoy, from, p.Yaw + MathF.PI, musou.Rules.Of(MusouEnemyType.RokkitBoy).Health, rnode);
            if (_s2DemoRokkit != 0) musou.LaunchRocket(_s2DemoRokkit, p.Position + side * 1.5f + new Vector3(0f, 0f, 1.2f));
            return;
        }
    }

    void TfwSquigsStep()
    {
        if (_musou is not { } musou || _state != MatchState.Playing) return;
        MusouBeginTick();
        _musouIds.Clear();
        foreach (int id in _players.Keys) _musouIds.Add(id);
        _musouIds.Sort();
        TfwS2Demo(musou);
        TfwSquigDrops(musou);
        TfwSquigKicks(musou);
        _rocketQueue.Clear();
        musou.TakeRocketBlasts(_rocketQueue);
        foreach (var r in _rocketQueue) MusouOrkBlast(r.Point, MusouRokkitRules.Blast, credit: 0);
        // The Squigs queued before this point burst now (their bursts may queue more: those burst next tick, one Squig a tick).
        _squigQueue.Clear();
        musou.TakeDetonations(_squigQueue);
        foreach (var (id, credit) in _squigQueue)
            if (musou.Detonate(id, credit, out var at)) MusouOrkBlast(at, MusouSquigRules.Blast, credit);
        if (_rocketQueue.Count > 0 || _squigQueue.Count > 0) TfwCreditKills();
    }

    /// <summary>
    /// The drop director: during a battle (not before, between or after them), each living human player not at the Firing Range has a Squig due every <see cref="MusouSquigRules.DropMinSeconds"/>
    /// to <see cref="MusouSquigRules.DropMaxSeconds"/> (a roll of his id and the drop); when due one lands <see cref="MusouSquigRules.DropMin"/> to <see cref="MusouSquigRules.DropMax"/> from him,
    /// behind his view first (off-screen), on the graph where one is.
    /// </summary>
    void TfwSquigDrops(MusouSim musou)
    {
        var battle = _tfw?.Battle;
        if (!ReferenceEquals(battle, _squigBattle)) { _squigBattle = battle; _squigDue.Clear(); }
        if (battle is null || battle.Won || !TfwSquigDropsOn) return;
        int alive = -1;   // counted when a drop is due
        foreach (int id in _musouIds)
        {
            if (TfwContracts.IsPuppet(id)) continue;
            var p = _players[id];
            if (!p.Alive || _tfw!.InRange.Contains(id)) continue;
            if (!_squigDue.TryGetValue(id, out uint due)) { _squigDue[id] = Tick + SquigWait(id); continue; }
            if (Tick < due) continue;
            if (alive < 0) alive = musou.CountAlive(MusouEnemyType.BombSquig);
            if (alive >= MusouSquigRules.MaxAlive || musou.Alive - (_tfw?.Battle?.ArmyOrksAlive ?? 0) >= TfwBattleRules.CrowdPoolRoom || !DropSquig(musou, p))
            {
                _squigDue[id] = Tick + (uint)Math.Max(1, Config.TickRate);   // a second, then again
                continue;
            }
            alive++;
            _squigDue[id] = Tick + SquigWait(id);
        }
    }

    uint SquigWait(int id)
    {
        float roll = MusouSim.Unit(MusouSim.Mix((uint)id * 0x9E3779B1u, (uint)_squigDrops * 0x85EBCA6Bu + Tick));
        return (uint)Math.Max(1, (int)MathF.Round((MusouSquigRules.DropMinSeconds + (MusouSquigRules.DropMaxSeconds - MusouSquigRules.DropMinSeconds) * roll) * Config.TickRate));
    }

    /// <summary>One Squig near <paramref name="p"/>: tried behind his view first, then turning out to his front in 30-degree steps; false when no try lands on the graph.</summary>
    bool DropSquig(MusouSim musou, in PlayerState p)
    {
        float r = MusouSquigRules.DropMin + (MusouSquigRules.DropMax - MusouSquigRules.DropMin) * MusouSim.Unit(MusouSim.Mix((uint)p.PlayerId, Tick ^ 0x5A17u));
        float behind = p.Yaw + MathF.PI;
        ReadOnlySpan<float> turns = [0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f];
        foreach (float degrees in turns)
        {
            float yaw = behind + degrees * MathF.PI / 180f;
            var dir = new Vector2(-MathF.Sin(yaw), MathF.Cos(yaw));
            var spot = p.Position + new Vector3(dir * r, 0f);
            int node = -1;
            if (musou.Walk is { } walk)
            {
                node = walk.Nearest(spot, 3f, 6f);
                if (node < 0 || !musou.Reachable(node)) continue;
                spot = walk.Position(node);
            }
            ushort squig = musou.Spawn(MusouEnemyType.BombSquig, spot, MathF.Atan2(-(p.Position.X - spot.X), p.Position.Y - spot.Y), musou.Rules.Of(MusouEnemyType.BombSquig).Health, node);
            if (squig == 0) return false;
            _squigDrops++;
            musou.AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, squig, (ushort)Math.Clamp(p.PlayerId, 0, ushort.MaxValue), MusouSquigRules.DropCue, spot));
            return true;
        }
        return false;
    }

    /// <summary>A living player (an AI Marine too) running into a Squig kicks it along his run (<see cref="MusouSim.KickSquig"/>; see <see cref="MusouSquigRules"/> for the reach and speed).</summary>
    void TfwSquigKicks(MusouSim musou)
    {
        foreach (int id in _musouIds)
        {
            if (!_players.TryGetValue(id, out var p) || !p.Alive) continue;
            var run = new Vector2(p.Velocity.X, p.Velocity.Y);
            float speed = run.Length();
            if (speed < MusouSquigRules.KickMinSpeed) continue;
            int n = musou.Query(p.Position, MusouSquigRules.KickReach, _musouNear, verticalReach: MusouSquigRules.KickVertical);
            for (int k = 0; k < n; k++)
            {
                if (!musou.TryGet(_musouNear[k], out var e) || e.Type != MusouEnemyType.BombSquig) continue;
                var to = new Vector2(e.Position.X - p.Position.X, e.Position.Y - p.Position.Y);
                if (to.LengthSquared() > 1e-6f && Vector2.Dot(run / speed, Vector2.Normalize(to)) < MusouSquigRules.KickMinCos) continue;
                musou.KickSquig(e.Id, id, run);
            }
        }
    }
}
