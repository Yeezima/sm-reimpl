using System.Numerics;
using SmServer;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou" (MU0, the switch and contracts): the World's Musou guard is set only under the flag (<see cref="World.StartMusou"/>); with it every
/// join is a Space Marine without balance and the one-sided match reaches play and stays there (no warm-up wait, no instant end); a Musou server builds
/// its mode at the constructor and over loopback its clients join as Space Marines and get no Musou section yet (no enemies). The enemy pool's contract
/// (<see cref="MusouSim"/>: spawn, damage, query, a late id reuse).
/// </summary>
public class MusouSwitchTests
{
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static ClassLoadout Class(string name, Team side) => new(name, 0, -1, [Light]) { PrimaryChoices = [0], Side = side, Role = "tactical" };
    static readonly SimConfig Base = new() { Weapons = [Rifle], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)] };
    static readonly SimConfig MusouConfig = Base with { Extras = ContentExtras.Musou };
    static MatchRules Rules => new() { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 };

    [Fact]
    public void The_guard_is_set_only_under_the_flag_and_with_rules()
    {
        Assert.False(new World(Base) { Rules = Rules }.StartMusou(null));
        Assert.False(new World(Base with { Extras = ContentExtras.All }) { Rules = Rules }.StartMusou(null));
        Assert.False(new World(MusouConfig).StartMusou(null));   // no team rules (free-for-all)
        var w = new World(MusouConfig) { Rules = Rules };
        Assert.Null(w.Musou);
        Assert.True(w.StartMusou(null));
        var first = w.Musou;
        Assert.NotNull(first);
        Assert.True(w.StartMusou(null));
        Assert.Same(first, w.Musou);
    }

    [Fact]
    public void Without_the_guard_a_musou_config_plays_the_core_match_flow()
    {
        var w = new World(MusouConfig) { Rules = Rules };
        w.Spawn(1, w.AssignClass(1, Team.Chaos));
        Assert.Equal(Team.Chaos, w.Players[1].Team);
        for (int k = 0; k < 90; k++) w.Step(_ => default);
        Assert.Equal(MatchState.Warmup, w.State);   // one side: the core waits for the other
    }

    [Fact]
    public void Every_join_is_a_space_marine_and_the_one_sided_match_plays_on()
    {
        var w = new World(MusouConfig) { Rules = Rules };
        Assert.True(w.StartMusou(null));
        w.MusouDirector!.Hold = true;   // MU1: no releases here (the director's own tests run the waves)
        for (int id = 1; id <= 10; id++)
        {
            int cls = w.AssignClass(id % 2, id % 3 == 0 ? Team.Chaos : Team.None);
            Assert.Equal(Team.SpaceMarines, w.AssignSide(cls, Team.Chaos));
            w.Spawn(id, cls, team: w.AssignSide(cls, Team.Chaos));
            Assert.Equal(Team.SpaceMarines, w.Players[id].Team);
        }
        Assert.Equal(10, w.CountSide(Team.SpaceMarines));   // no per-side limit, no balance
        Assert.Equal(MatchState.Warmup, w.State);
        w.Step(_ => default);
        Assert.Equal(MatchState.Countdown, w.State);
        for (int k = 0; k < 40; k++) w.Step(_ => default);
        Assert.Equal(MatchState.Playing, w.State);
        Assert.Equal(uint.MaxValue, w.TicksLeft);   // no time limit
        for (int k = 0; k < 3000; k++) w.Step(_ => default);
        Assert.Equal(MatchState.Playing, w.State);   // no limit ends it (only the director's win or loss)
        Assert.Equal(0, w.Musou!.Count);

        // Every player gone: back to warm-up; a new join starts again.
        for (int id = 1; id <= 10; id++) w.Remove(id);
        w.Step(_ => default);
        Assert.Equal(MatchState.Warmup, w.State);
        w.Spawn(20, w.AssignClass(1));
        Assert.Equal(Team.SpaceMarines, w.Players[20].Team);
        w.Step(_ => default);
        Assert.Equal(MatchState.Countdown, w.State);
    }

    [Fact]
    public void A_musou_server_starts_the_mode_and_its_clients_join_as_space_marines_without_sections()
    {
        var options = new ServerOptions { Match = Rules };
        var h = new Harness(LinkConditions.Perfect, 5, 0, MusouConfig, options);
        Assert.NotNull(h.Server.World.Musou);
        h.Server.World.MusouDirector!.Hold = true;   // MU1: no releases here (the enemies' sections are MU2's own tests)
        Assert.Contains("musou: the mode runs", h.Server.MusouNote);
        Assert.Contains("no nav graph", h.Server.MusouNote);   // the flat ground: no map
        h.AddClient(nonce: 1, options: new ClientOptions { RequestedSide = Team.Chaos });
        h.AddClient(nonce: 2, options: new ClientOptions { RequestedClass = 1 });
        var log = new List<string>();
        h.Server.Log += log.Add;
        h.Run(TimeSpan.FromSeconds(4));
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        Assert.All(h.Server.World.Players.Values, p => Assert.Equal(Team.SpaceMarines, p.Team));
        Assert.Equal(MatchState.Playing, h.Server.World.State);
        // With the director held there is no enemy: the clients get the director's state alone (MU2 sends it once play starts), no entities or events.
        Assert.All(h.Clients, c => Assert.All(c.TakeExtras(), s => Assert.Equal(SmNet.Extras.Musou.MusouWire.DirectorTag, s.Tag)));
        Assert.Equal(0, h.Server.World.Musou!.Count);
        Assert.Contains(log, l => l.StartsWith("musou:", StringComparison.Ordinal));

        // A faithful or extended server never builds the mode.
        Assert.Null(new Harness(LinkConditions.Perfect, 6, 0, Base, options).Server.World.Musou);
        Assert.Null(new Harness(LinkConditions.Perfect, 6, 0, Base with { Extras = ContentExtras.All }, options).Server.World.Musou);
    }

    // ---------------------------------------------------------------- the pool's contract

    [Fact]
    public void The_pool_spawns_damages_queries_and_reuses_ids_late()
    {
        var sim = new MusouSim(null);
        sim.BeginTick(10);
        ushort a = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 2f, 0f), 0f, 85f);
        ushort b = sim.Spawn(MusouEnemyType.Nob, new Vector3(0f, 1f, 0f), 0f, 600f);
        ushort c = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 9f, 0f), 0f, 85f);
        Assert.Equal((1, 2, 3), (a, b, c));
        Assert.Equal((3, 3), (sim.Count, sim.Alive));

        Span<ushort> found = stackalloc ushort[8];
        int n = sim.Query(Vector3.Zero, 3f, found);
        Assert.Equal([b, a], found[..n].ToArray());   // nearest first; c is beyond 3 m
        Assert.Equal(1, sim.Query(Vector3.Zero, 3f, found[..1]));
        Assert.Equal(b, found[0]);

        Assert.True(sim.Damage(a, 4, 50f, Vector3.UnitY * 3f, out bool killed));
        Assert.False(killed);
        Assert.True(sim.Damage(a, 4, 50f, Vector3.UnitY * 3f, out killed));
        Assert.True(killed);
        Assert.True(sim.TryGet(a, out var dead));
        Assert.Equal((MusouEnemyState.Dying, 0f), (dead.State, dead.Health));
        Assert.False(dead.Alive);
        Assert.False(sim.Damage(a, 4, 10f, Vector3.Zero, out _));   // a dying enemy takes no more
        Assert.Equal((3, 2), (sim.Count, sim.Alive));
        Assert.Equal([MusouEventKind.Hit, MusouEventKind.Hit, MusouEventKind.Death], sim.Events.Select(e => e.Kind));
        var death = sim.Events[^1];
        Assert.Equal((10u, a, (ushort)4), (death.Tick, death.Enemy, death.Player));
        Assert.Equal(1f, death.Vector.Length(), 4);   // the push as a direction
        sim.BeginTick(11);
        Assert.Empty(sim.Events);

        // A freed id waits behind every other free slot.
        Assert.True(sim.Remove(a));
        Assert.False(sim.Remove(a));
        Assert.Equal(4, sim.Spawn(MusouEnemyType.Boy, Vector3.Zero, 0f, 85f));
        Assert.False(sim.TryGet(a, out _));
        int spawned = 0;
        while (sim.Spawn(MusouEnemyType.Gretchin, Vector3.Zero, 0f, 30f) != 0) spawned++;
        Assert.Equal(MusouSim.Capacity, sim.Count);
        Assert.Equal(MusouSim.Capacity - 4 + 1, spawned);   // slots 5..512, then id 1 again last
        Assert.True(sim.TryGet(a, out var reused));
        Assert.Equal(MusouEnemyType.Gretchin, reused.Type);
        sim.Clear();
        Assert.Equal((0, 0), (sim.Count, sim.Alive));
    }
}
