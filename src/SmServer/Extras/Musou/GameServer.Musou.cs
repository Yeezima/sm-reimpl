using System.Numerics;
using SmContent;
using SmNet;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmServer;

/// <summary>
/// Added content "Musou" on the server (design sections A and C), only when the match runs <see cref="ContentExtras.Musou"/>. At the end of the constructor
/// (<see cref="CreateMusou"/>, from the <c>ExtrasCreated</c> hook) the server builds or reads the walking graph of the map (the test bots' graph setup and
/// cache, <see cref="BotNavigation"/>, around the map's spawns, points and start areas, on the Space Marines' collision: the side the players are on) and
/// makes the world a Musou match (<see cref="World.StartMusou"/>). Once a tick (<see cref="SendMusou"/>, from the envelope hook) it sends each client the
/// director, the events and the enemies due by that client's interest (<see cref="MusouInterest"/>: <c>MusouWire</c> tags 3 to 5, at most 4 messages a tick
/// within the client's budget; MU2). Nothing is sent while the pool is empty and the director unset (MU0's state).
/// </summary>
public sealed partial class GameServer
{
    string _musouNote = "";
    bool _musouLogged;

    /// <summary>The Musou set-up's one-line note (the nav graph and the mode), or "" when the match is not Musou.</summary>
    public string MusouNote => _musouNote;

    void CreateMusou()
    {
        if (!World.Config.Extras.HasFlag(ContentExtras.Musou)) return;
        NavGraph? nav = null;
        string navNote;
        if (Map is not { } map) navNote = "no map (flat ground): no nav graph";
        else
        {
            try { nav = BuildMusouNav(map, World.Config, out navNote); }
            catch (Exception e) when (e is not OutOfMemoryException)   // any failure of the build: the mode runs without a graph (noted), the server starts
            {
                navNote = $"nav graph failed ({e.GetType().Name}: {e.Message})";
            }
        }
        bool started = World.StartMusou(nav);
        if (started) PlayerJoined += p => World.MusouJoined(p.PlayerId);   // MU-D fix: a join is stripped to the pistol at once, not a tick later
        _musouNote = started
            ? $"musou: the mode runs (Space Marines only, no limits; enemies by the director, sent by interest); {navNote}{(nav is null ? "" : $", {nav.NodeCount} nodes")}" +
              (MusouDummiesWanted > 0 ? $"; test aid {MusouDummiesVariable}: {MusouDummiesWanted} dummies" : "")
            : "musou: not started (the match has no team rules)";
    }

    /// <summary>The map's walking graph for the enemies (the bots' setup and cache; <see cref="BotNavigation.LoadOrBuildWalk"/> over the map's own spawns, points and start areas).</summary>
    static NavGraph? BuildMusouNav(GameMap map, SimConfig config, out string note)
    {
        var seeds = map.Spawns.Select(s => s.Position)
            .Concat((map.ControlPoints ?? []).Select(p => p.Center))
            .Concat((map.StartAreas ?? []).Select(a => a.Center)).ToList();
        if (seeds.Count == 0) { note = "no spawns or points to build the nav graph around"; return null; }
        var settings = NavSettings.From(config);
        var area = NavArea.Around(seeds, BotNavigation.Margin);
        var mesh = map.Collision.CharacterFor(Team.SpaceMarines);
        ulong key = NavGraph.KeyFor(mesh, map.Collision.KillVolumes, settings, area);
        return NavCache.LoadOrBuild(BotNavigation.CacheDirectory, map.Name, key, settings, () => NavGraph.Build(mesh, map.Collision.KillVolumes, settings, area), out note);
    }

    readonly Dictionary<int, MusouInterest> _musouInterest = new();
    readonly MusouEventWindow _musouEvents = new();
    readonly MusouEnemy[] _musouEnemies = new MusouEnemy[MusouSim.Capacity];
    readonly List<int> _musouGone = new();
    MusouBox? _musouNavBox;
    bool _musouNavBoxRead;
    long _musouRecordsGone, _musouRecordsHeldGone;

    /// <summary>Musou entity records sent (all clients), and records due but held back by a client's budget or message cap (MU2; bandwidth tests).</summary>
    public long MusouRecordsSent => _musouInterest.Values.Sum(i => i.RecordsSent) + _musouRecordsGone;
    public long MusouRecordsHeld => _musouInterest.Values.Sum(i => i.RecordsHeld) + _musouRecordsHeldGone;
    /// <summary>Event copies left out of a tick's sections (over two full sections in the 3-tick window).</summary>
    public int MusouEventCopiesDropped => _musouEvents.CopiesDropped;

    /// <summary>
    /// This feature's part of the envelope hook (called by the dispatcher <c>GameServer.Extras.cs</c>): per client, the director (on a change, else every 10
    /// ticks), the events of the last 3 ticks (each in 3 messages) and the enemies due by that client's interest.
    /// </summary>
    void SendMusou()
    {
        if (World.Musou is not { } sim) return;
        if (!_musouLogged) { _musouLogged = true; Log?.Invoke(_musouNote); }
        StepMusouDummies(sim);   // test aid (SM_MUSOU_DUMMIES; GameServer.MusouDummies.cs)
        int n = sim.CopyTo(_musouEnemies);
        PoseMusouDummies(_musouEnemies.AsSpan(0, n));
        LogMusouDirector(sim.Director);
        var events = _musouEvents.Step(sim.Tick, sim.Events, MusouSendRules.EventsPerTick(World.Config.TickRate));
        if (n == 0 && events.Count == 0 && sim.Director == default) return;   // nothing in play: MU0's state sends nothing

        if (!_musouNavBoxRead) { _musouNavBoxRead = true; _musouNavBox = MusouSend.BoundsOf(sim.Nav); }
        var box = MusouSend.EffectiveBox(sim.Director.Box, _musouNavBox);
        var director = MusouWire.WriteDirector(sim.Director with { Box = box });
        uint tick = World.Tick;
        var enemies = new ReadOnlySpan<MusouEnemy>(_musouEnemies, 0, n);
        Vector3? anyone = null;
        foreach (var p in World.Players.Values) if (p.Alive) { anyone = p.Position; break; }
        foreach (var peer in _peers.Values)
        {
            if (!_musouInterest.TryGetValue(peer.PlayerId, out var interest)) _musouInterest[peer.PlayerId] = interest = new MusouInterest();
            // The client's player, else (a spectator) the first living player: the crowd near the action stays fresh.
            Vector3? viewer = World.Players.TryGetValue(peer.PlayerId, out var me) ? me.Position : anyone;
            var planned = interest.Plan(tick, World.Config.TickRate, enemies, viewer, box, director, events);
            MusouDropSection(peer.PlayerId, tick, box, planned);   // MU-D: the client's own drops (tag 6), into the tick's messages
            foreach (var sections in planned)
                SendExtras(new ExtrasUpdate(tick, sections), peer.PlayerId);
        }
        _musouGone.Clear();
        foreach (int id in _musouInterest.Keys) if (!_peers.Values.Any(p => p.PlayerId == id)) _musouGone.Add(id);
        foreach (int id in _musouGone)
        {
            _musouRecordsGone += _musouInterest[id].RecordsSent;
            _musouRecordsHeldGone += _musouInterest[id].RecordsHeld;
            _musouInterest.Remove(id);
            _musouDropSent.Remove(id);
        }
    }

    /// <summary>Test aid (MU-D): with this set to 1 the server lays a drop 3 m in front of every living player that has none (to see and take one at once).</summary>
    public const string MusouDropTestVariable = "SM_MUSOU_DROP_TEST";
    bool? _musouDropTest;
    readonly Dictionary<int, (uint Version, uint Tick)> _musouDropSent = new();
    readonly List<MusouDrop> _musouDropList = new();
    readonly List<MusouDropRecord> _musouDropRecords = new();
    /// <summary>Drop sections (tag 6) sent, and their records (all clients; tests).</summary>
    public int MusouDropSections { get; private set; }
    public long MusouDropRecordsSent { get; private set; }

    /// <summary>A client's drop list is sent again every this many ticks while it is not empty (ours: a lost message is mended within half a second).</summary>
    const uint MusouDropRefreshTicks = 15;

    /// <summary>
    /// Player <paramref name="playerId"/>'s drops (only its own: the drops are per player) as one tag 6 section, when its list changed since the last one sent
    /// or every <see cref="MusouDropRefreshTicks"/> ticks while not empty; added to the tick's last message when it has room, else as a new message within
    /// the cap of <see cref="MusouSendRules.MaxMessagesPerTick"/>; with no room it waits for the next tick (the change is still unsent).
    /// </summary>
    void MusouDropSection(int playerId, uint tick, MusouBox box, List<(byte Tag, byte[] Payload)[]> messages)
    {
        if (_musouDropTest ??= Environment.GetEnvironmentVariable(MusouDropTestVariable) == "1")
            if (World.State == MatchState.Playing && World.Players.TryGetValue(playerId, out var p) && p.Alive && World.MusouDropsOf(playerId, _musouDropList) == 0 && World.MusouSpawnTestDrop(playerId, 4f) is { } test)
                Log?.Invoke($"musou: test aid {MusouDropTestVariable}: drop {test.Id} for player {playerId} at {test.Position.X:F1},{test.Position.Y:F1},{test.Position.Z:F1}, item {test.Item}");
        uint version = World.MusouDropVersion(playerId);
        int n = World.MusouDropsOf(playerId, _musouDropList);
        bool known = _musouDropSent.TryGetValue(playerId, out var sent);
        bool changed = known ? sent.Version != version : n > 0;
        bool refresh = n > 0 && known && (tick - sent.Tick >= MusouDropRefreshTicks || tick < sent.Tick);
        if (!changed && !refresh) return;
        _musouDropRecords.Clear();
        foreach (var d in _musouDropList) _musouDropRecords.Add(new MusouDropRecord(d.Id, d.Position, d.Item, d.State));
        (byte, byte[]) section = (MusouWire.DropsTag, MusouWire.WriteDrops(_musouDropRecords, box));
        int need = Protocol.ExtrasSectionHeaderBytes + section.Item2.Length;
        if (messages.Count > 0 && messages[^1].Length < Protocol.MaxExtrasSections
            && Protocol.EnvelopeBytes(new ExtrasUpdate(tick, messages[^1])) + need <= Protocol.MaxExtrasMessageBytes)
            messages[^1] = [.. messages[^1], section];
        else if (messages.Count < MusouSendRules.MaxMessagesPerTick) messages.Add([section]);
        else return;   // no room this tick: sent the next
        _musouDropSent[playerId] = (version, tick);
        MusouDropSections++;
        MusouDropRecordsSent += _musouDropRecords.Count;
    }

    MusouDirectorView _musouLoggedDirector;

    /// <summary>The director's milestones in the server log (the director the simulation sets each tick: MU1's view): a wave starts, a break, the boss, a win or a loss.</summary>
    void LogMusouDirector(MusouDirectorView d)
    {
        var was = _musouLoggedDirector;
        _musouLoggedDirector = d;
        if (d.Phase == was.Phase && d.Wave == was.Wave && d.BossId == was.BossId) return;
        string? line = d.Phase switch
        {
            MusouPhase.Wave when d.Wave != was.Wave || was.Phase != MusouPhase.Wave => $"wave {d.Wave}/{d.Waves} starts: {d.EnemiesLeft} enemies, {d.Lives} lives",
            MusouPhase.Break when was.Phase != MusouPhase.Break => $"break after wave {d.Wave}/{d.Waves}: {d.BreakTicksLeft / (float)Math.Max(1, World.Config.TickRate):0.#} s, {d.Lives} lives",
            MusouPhase.Boss when d.BossId != 0 && (was.BossId != d.BossId || was.Phase != MusouPhase.Boss) => $"the boss is in (enemy {d.BossId}, {d.BossMaxHealth:0} health), {d.Lives} lives",
            MusouPhase.Won when was.Phase != MusouPhase.Won => $"won: the boss is dead after wave {d.Wave}/{d.Waves}, {d.Lives} lives left",
            MusouPhase.Lost when was.Phase != MusouPhase.Lost => $"lost in wave {d.Wave}/{d.Waves}: every player dead and no lives left",
            _ => null,
        };
        if (line is not null) Log?.Invoke($"musou: tick {World.Tick}: {line}");
    }
}
