using System.Numerics;
using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// Added content "Musou": the golden weapon drops in the World (owner, 2026-10-31; <see cref="MusouDropRules"/>). A KO of a dropping enemy (the Nobs and Chaos Marines) in
/// play makes one drop spot where it fell, on the floor (its walking-graph node's height); every living player gets a drop of its own there, an independent
/// roll from its class's pool (<see cref="MusouDropRules.Roll"/>). Each player sees and takes only its own: an interact press (E) within
/// <see cref="MusouDropRules.ReachMetres"/> takes the nearest and equips it (<see cref="MusouGiveItem"/>); that press then collects no ammunition box (the
/// drop goes first; a press with no drop in reach, or one that cannot be given now, still does). A drop lies for <see cref="MusouDropRules.LifetimeSeconds"/> or until its player dies (drops are tied to a life);
/// a new round clears them. Server side; the clients learn of their own drops from wire tag 6.
/// </summary>
public sealed partial class World
{
    readonly List<MusouDrop> _musouDrops = new();
    readonly Dictionary<int, uint> _musouDropVersions = new();
    readonly List<int> _musouDropPlayers = new();
    ushort _musouNextDrop = 1;

    /// <summary>Every Musou drop listed now (live, and ended ones for <see cref="MusouDropRules.EndedListedSeconds"/>), oldest first; empty outside Musou.</summary>
    public IReadOnlyList<MusouDrop> MusouDrops => _musouDrops;

    /// <summary>A number that changes whenever <paramref name="playerId"/>'s list of drops changes (the server sends the list on a change).</summary>
    public uint MusouDropVersion(int playerId) => _musouDropVersions.GetValueOrDefault(playerId);

    /// <summary>Player <paramref name="playerId"/>'s listed drops (oldest first) into <paramref name="into"/> (cleared first); the number.</summary>
    public int MusouDropsOf(int playerId, List<MusouDrop> into)
    {
        into.Clear();
        foreach (var d in _musouDrops) if (d.Player == playerId) into.Add(d);
        return into.Count;
    }

    /// <summary>
    /// A drop of <paramref name="item"/> for <paramref name="playerId"/> at <paramref name="at"/> (taken as on the floor), lying from this tick; null
    /// outside Musou, for an unknown player or a point that is not finite. Used by the KOs, the server's test aid and the tests. One over
    /// <see cref="MusouDropRules.MaxLivePerPlayer"/> expires the player's oldest.
    /// </summary>
    public MusouDrop? MusouSpawnDrop(int playerId, Vector3 at, ushort item, ushort enemy = 0)
    {
        if (_musou is null || !_players.ContainsKey(playerId) || !float.IsFinite(at.X + at.Y + at.Z)) return null;
        uint life = (uint)MathF.Max(1f, MathF.Round(MusouDropRules.LifetimeSeconds * Config.TickRate));
        var drop = new MusouDrop(MusouDropId(), playerId, at, item, MusouDropState.Live, Tick, Tick + life, enemy);
        _musouDrops.Add(drop);
        MusouDropChanged(playerId);
        int live = 0;
        for (int i = _musouDrops.Count - 1; i >= 0; i--)
        {
            var d = _musouDrops[i];
            if (d.Player != playerId || d.State != MusouDropState.Live) continue;
            if (++live > MusouDropRules.MaxLivePerPlayer) MusouEndDrop(i, MusouDropState.Expired);
        }
        return drop;
    }

    /// <summary>
    /// The server's test aid (<c>SM_MUSOU_DROP_TEST</c>): a drop rolled from <paramref name="playerId"/>'s pool <paramref name="ahead"/> m in front of it and 1.5 m right
    /// (on the floor below when there is one); null when it has no live drop to roll or is not alive.
    /// </summary>
    public MusouDrop? MusouSpawnTestDrop(int playerId, float ahead = 3f)
    {
        if (_musou is null || !_players.TryGetValue(playerId, out var p) || !p.Alive) return null;
        var from = !_musouArmed.Contains(playerId) && MusouLoadout.Prepared(Config) ? MusouLoadout.Stripped(Config, p) : p;
        if (MusouDropRules.Roll(Config, from, Tick, 0, playerId) is not { } item) return null;
        var forward = new Vector3(-MathF.Sin(p.Yaw), MathF.Cos(p.Yaw), 0f);
        var right = new Vector3(MathF.Cos(p.Yaw), MathF.Sin(p.Yaw), 0f);
        var at = p.Position + forward * ahead + right * 1.5f;   // a little to the right: the third-person camera sees past the player
        if (Collision is not null && GroundUnder(at + new Vector3(0f, 0f, 1f), 3f) is { } ground) at = ground;
        return MusouSpawnDrop(playerId, at, item);
    }

    ushort MusouDropId()
    {
        for (int k = 0; k < ushort.MaxValue; k++)
        {
            ushort id = _musouNextDrop;
            _musouNextDrop = _musouNextDrop >= ushort.MaxValue ? (ushort)1 : (ushort)(_musouNextDrop + 1);
            if (!_musouDrops.Exists(d => d.Id == id)) return id;
        }
        throw new InvalidOperationException("No free Musou drop id.");
    }

    void MusouDropChanged(int playerId) => _musouDropVersions[playerId] = _musouDropVersions.GetValueOrDefault(playerId) + 1;

    void MusouEndDrop(int index, MusouDropState end)
    {
        var d = _musouDrops[index];
        _musouDrops[index] = d with { State = end, EndTick = Tick };
        MusouDropChanged(d.Player);
    }

    /// <summary>A KO (from <see cref="MusouCreditKills"/>, in play): one drop for every living player when the enemy is one that drops.</summary>
    void MusouDropFor(MusouKill kill)
    {
        if (!MusouDropRules.Drops(kill.Type) || kill.Cause == MusouDeathKind.World) return;   // a stranded enemy removed by the failsafe drops nothing
        if (!_musou!.TryGet(kill.Enemy, out var enemy)) return;
        var at = enemy.Position;
        // On the floor: the height of the enemy's walking-graph node (the flat ground without a graph).
        if (_musou.Walk is { } walk && enemy.Node >= 0 && enemy.Node < walk.NodeCount) at.Z = walk.Z(enemy.Node);
        else if (_musou.Walk is null) at.Z = 0f;
        _musouDropPlayers.Clear();
        foreach (int id in _players.Keys) _musouDropPlayers.Add(id);
        _musouDropPlayers.Sort();
        foreach (int id in _musouDropPlayers)
        {
            var p = _players[id];
            if (!p.Alive) continue;
            if (MusouDropRules.Roll(Config, p, Tick, kill.Enemy, id) is { } item) MusouSpawnDrop(id, at, item, kill.Enemy);
        }
    }

    /// <summary>
    /// Once a tick (the end of the Musou step): in play, each living player whose interact press is this tick's takes its nearest own live drop in reach
    /// (equipped; that press then collects no ammunition box); then expiry, the ended drops' listing time, and the drops of players who left.
    /// </summary>
    void MusouDropsStep()
    {
        // Leavers' change counters go (the server forgets a leaver's peer too; an id that joins again starts a new count).
        if (_musouDropVersions.Count > _players.Count)
        {
            _musouDropPlayers.Clear();
            foreach (int id in _musouDropVersions.Keys) if (!_players.ContainsKey(id) && !MusouHasDrops(id)) _musouDropPlayers.Add(id);
            foreach (int id in _musouDropPlayers) _musouDropVersions.Remove(id);
        }
        if (_musouDrops.Count == 0) return;
        if (_state == MatchState.Playing && _interactPresses.Count > 0)
        {
            _musouDropPlayers.Clear();
            foreach (int id in _interactPresses) if (!_musouDropPlayers.Contains(id)) _musouDropPlayers.Add(id);
            _musouDropPlayers.Sort();
            foreach (int id in _musouDropPlayers)
            {
                if (!_players.TryGetValue(id, out var p) || !p.Alive) continue;
                int best = -1;
                float bestD = float.MaxValue;
                for (int i = 0; i < _musouDrops.Count; i++)
                {
                    var d = _musouDrops[i];
                    if (d.Player != id || d.State != MusouDropState.Live || !MusouInReach(d.Position, p.Position)) continue;
                    float d2 = Vector3.DistanceSquared(d.Position, p.Position);
                    if (d2 < bestD) { bestD = d2; best = i; }
                }
                if (best < 0 || !MusouGiveItem(id, _musouDrops[best].Item)) continue;
                MusouEndDrop(best, MusouDropState.Picked);
                for (int k = _interactPresses.Count - 1; k >= 0; k--)   // the drop went first: this press collects no ammunition box (Pickups.cs)
                    if (_interactPresses[k] == id) _interactPresses.RemoveAt(k);
            }
        }
        uint keep = (uint)MathF.Max(1f, MathF.Round(MusouDropRules.EndedListedSeconds * Config.TickRate));
        for (int i = _musouDrops.Count - 1; i >= 0; i--)
        {
            var d = _musouDrops[i];
            if (!_players.TryGetValue(d.Player, out var owner)) { _musouDrops.RemoveAt(i); MusouDropChanged(d.Player); continue; }
            // Drops are tied to a life (the lead, 2026-10-31): a dead player's live drops end (sent as expired).
            if (d.State == MusouDropState.Live && (d.EndTick <= Tick || !owner.Alive)) MusouEndDrop(i, MusouDropState.Expired);
            else if (d.State != MusouDropState.Live && Tick - d.EndTick >= keep) { _musouDrops.RemoveAt(i); MusouDropChanged(d.Player); }
        }
    }

    /// <summary>Whether feet at <paramref name="feet"/> reach a drop at <paramref name="drop"/> (<see cref="MusouDropRules.ReachMetres"/> flat, <see cref="MusouDropRules.ReachHeight"/> up or down).</summary>
    public static bool MusouInReach(Vector3 drop, Vector3 feet)
    {
        float dx = drop.X - feet.X, dy = drop.Y - feet.Y;
        return dx * dx + dy * dy <= MusouDropRules.ReachMetres * MusouDropRules.ReachMetres && MathF.Abs(drop.Z - feet.Z) <= MusouDropRules.ReachHeight;
    }

    bool MusouHasDrops(int playerId)
    {
        foreach (var d in _musouDrops) if (d.Player == playerId) return true;
        return false;
    }

    /// <summary>A new round: every drop goes (each player's list changes to empty).</summary>
    void MusouClearDrops()
    {
        foreach (var d in _musouDrops) MusouDropChanged(d.Player);
        _musouDrops.Clear();
    }
}
