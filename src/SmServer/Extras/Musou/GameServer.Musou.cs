using SmContent;
using SmNet;
using SmSim;

namespace SmServer;

/// <summary>
/// Added content "Musou" on the server (design sections A and C), only when the match runs <see cref="ContentExtras.Musou"/>. At the end of the constructor
/// (<see cref="CreateMusou"/>, from the <c>ExtrasCreated</c> hook) the server builds or reads the walking graph of the map (the test bots' graph setup and
/// cache, <see cref="BotNavigation"/>, around the map's spawns, points and start areas, on the Space Marines' collision: the side the players are on) and
/// makes the world a Musou match (<see cref="World.StartMusou"/>). Once a tick (<see cref="SendMusou"/>, from the envelope hook) it will send the enemies,
/// their events and the director (<c>MusouWire</c> tags 3 to 5; MU2). MU0 sends nothing: there are no enemies yet.
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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                navNote = $"nav graph failed ({e.GetType().Name}: {e.Message})";
            }
        }
        bool started = World.StartMusou(nav);
        _musouNote = started
            ? $"musou: the mode runs (Space Marines only, no limits; no enemies yet, MU0); {navNote}{(nav is null ? "" : $", {nav.NodeCount} nodes")}"
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

    /// <summary>This feature's part of the envelope hook (called by the dispatcher <c>GameServer.Extras.cs</c>).</summary>
    void SendMusou()
    {
        if (World.Musou is null) return;
        if (!_musouLogged) { _musouLogged = true; Log?.Invoke(_musouNote); }
        // MU2: entities (tag 3) by interest, events (tag 4, each in 3 messages) and the director (tag 5, every 10 ticks and on a change) through SendExtras.
    }
}
