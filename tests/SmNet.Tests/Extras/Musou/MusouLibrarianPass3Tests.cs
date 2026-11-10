using System.Numerics;
using SmNet.Extras.Musou;
using SmServer;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The Librarian, pass 3 (librarian-lightning, owner 2026-11-10: ".1 second max between clicking left mouse button and the chain lighting hitting its target"): the own client's foreseen chain is the server's chain
/// on the same pool (<see cref="LibrarianPrediction"/> against <see cref="LibrarianPsyker"/>), and the server sends tag 16 at once when a cast starts or stops, a lock changes or a Shockwave is cast
/// (<see cref="GameServer.EdgeKey"/>), not at its next 10 Hz slot.
/// </summary>
public class MusouLibrarianPass3Tests
{
    const int Rate = 30;

    static (List<LibrarianLink> Server, List<LibrarianLink> Client, int ServerStruck, int ClientStruck) Both(Action<MusouSim> fill, float yaw = 0f, float pitch = 0f)
    {
        var sim = new MusouSim(null) { TickRate = Rate };
        fill(sim);
        var eye = new Vector3(0f, 0f, 1.6f);
        var psyker = new LibrarianPsyker();
        sim.BeginTick(1);
        var all = new MusouEnemy[4096];
        int n = sim.CopyTo(all);
        var candidates = all.Take(n).Where(e => e.Alive).Select(e => new LibrarianCandidate(e.Id, e.Position, LibrarianPrediction.HeightOf(MusouRules.Default, e.Type))).ToArray();
        var predicted = new List<LibrarianLink>();
        int clientStruck = LibrarianPrediction.Chain(Vector3.Zero, eye, yaw, pitch, candidates, null, predicted, new bool[candidates.Length]);
        psyker.Step(sim, new LibrarianCaster(1, Vector3.Zero, eye, yaw, pitch), new LibrarianPress(true, false), 1f / Rate);
        return (psyker.Links.ToList(), predicted, psyker.LastStruck, clientStruck);
    }

    static void SameChain(List<LibrarianLink> server, List<LibrarianLink> client)
    {
        Assert.Equal(server.Count, client.Count);
        for (int k = 0; k < server.Count; k++)
        {
            Assert.True(Vector3.Distance(server[k].From, client[k].From) < 1e-3f, $"link {k} from {server[k].From} / {client[k].From}");
            Assert.True(Vector3.Distance(server[k].To, client[k].To) < 1e-3f, $"link {k} to {server[k].To} / {client[k].To}");
        }
    }

    [Fact]
    public void The_foreseen_chain_through_a_crowd_is_the_servers()
    {
        var rng = new Random(5);
        var (server, client, s, c) = Both(sim =>
        {
            for (int k = 0; k < 160; k++)
            {
                var type = (k % 7) switch { 0 => MusouEnemyType.Gretchin, 1 => MusouEnemyType.Nob, 2 => MusouEnemyType.ArdBoy, _ => MusouEnemyType.Boy };
                sim.Spawn(type, new Vector3(-25f + 50f * rng.NextSingle(), 4f + 40f * rng.NextSingle(), 0f), 0f, 100000f);
            }
        });
        Assert.True(s > 5, $"the chain struck {s}");
        Assert.Equal(s, c);
        SameChain(server, client);
    }

    [Fact]
    public void The_foreseen_chain_picks_the_same_primary_off_the_centre_and_misses_as_the_server_does()
    {
        var (server, client, s, c) = Both(sim =>
        {
            sim.Spawn(MusouEnemyType.Boy, new Vector3(1.2f, 12f, 0f), 0f, 1000f);   // a few degrees off
            sim.Spawn(MusouEnemyType.Boy, new Vector3(-0.3f, 18f, 0f), 0f, 1000f);  // truer but farther
            sim.Spawn(MusouEnemyType.Warboss, new Vector3(6f, 20f, 0f), 0f, 1000f);
        }, yaw: 0.02f, pitch: 0.05f);
        Assert.Equal(s, c);
        SameChain(server, client);

        var miss = Both(sim => sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, -10f, 0f), 0f, 1000f));   // behind him
        Assert.Equal(0, miss.ServerStruck);
        Assert.Equal(0, miss.ClientStruck);
        SameChain(miss.Server, miss.Client);   // the bolt that crackles out to the full range
    }

    [Fact]
    public void A_cast_command_casts_from_the_next_tick_until_the_press_input_takes_over()
    {
        var w = MusouLibrarianTests.NewWorld(out int cls);
        w.Spawn(1, cls);
        w.Step(_ => default);
        var me = w.Players[1];
        var forward = new Vector3(-MathF.Sin(me.Yaw), MathF.Cos(me.Yaw), 0f);
        var boy = w.Musou!.Spawn(MusouEnemyType.Boy, me.Position + forward * 8f, 0f, 5000f);
        var psyker = w.LibrarianOf(1)!;

        uint press = w.Tick + 4;
        Assert.True(w.LibrarianCastEarly(1, press));
        w.Step(_ => default);   // the inputs of these ticks were sampled before the press: no Fire in them
        Assert.True(psyker.Casting);
        w.Musou.TryGet(boy, out var hit);
        Assert.True(hit.Health < 5000f);
        while (w.Tick + 1 < press) { w.Step(_ => default); Assert.True(psyker.Casting); }
        w.Step(_ => default);   // the press's own tick: the input decides (here: released at once, a tap)
        Assert.False(psyker.Casting);

        // Refused: a press tick not ahead, too far ahead, a player who is not a Librarian, a dead one.
        Assert.False(w.LibrarianCastEarly(1, w.Tick));
        Assert.False(w.LibrarianCastEarly(1, w.Tick + World.MaxEarlyTicks + 1));
        w.Spawn(2, 0);
        w.Step(_ => default);
        Assert.False(w.LibrarianCastEarly(2, w.Tick + 3));
        Assert.Equal(new byte[] { 7, 0, 0, 0 }, LibrarianWire.WriteCast(7));
        Assert.Equal(7u, LibrarianWire.ReadCast(LibrarianWire.WriteCast(7)));
        Assert.Null(LibrarianWire.ReadCast(new byte[3]));
    }

    [Fact]
    public void The_edge_key_changes_with_a_cast_a_lock_a_shockwave_or_a_librarian_but_not_with_stress_or_links()
    {
        LibrarianView View(bool casting = false, bool locked = false, byte shocks = 0, float stress = 10f, int links = 0, ushort player = 3) =>
            new(player, stress, locked, casting, 0f, 0f, 0, shocks, Enumerable.Range(0, links).Select(i => new LibrarianLink(Vector3.Zero, new Vector3(i, 1f, 1f))).ToList());
        ulong idle = GameServer.EdgeKey([View()]);
        Assert.Equal(idle, GameServer.EdgeKey([View(stress: 60f)]));
        Assert.NotEqual(idle, GameServer.EdgeKey([View(casting: true)]));
        Assert.Equal(GameServer.EdgeKey([View(casting: true, links: 1)]), GameServer.EdgeKey([View(casting: true, links: 12, stress: 40f)]));
        Assert.NotEqual(idle, GameServer.EdgeKey([View(locked: true)]));
        Assert.NotEqual(idle, GameServer.EdgeKey([View(shocks: 1)]));
        Assert.NotEqual(idle, GameServer.EdgeKey([View(), View(player: 4)]));
        Assert.NotEqual(idle, GameServer.EdgeKey([]));
        Assert.Equal(20, GameServer.CastingSendsPerSecond);
    }

    /// <summary>Sends <see cref="LibrarianSendPacer"/> lets out over <paramref name="ticks"/> ticks of a section that changes every tick (edges fixed after the first).</summary>
    static int Sends(int ticks, bool casting, bool old, bool changed = true, int tickRate = Rate)
    {
        var pacer = new LibrarianSendPacer();
        int sent = 0;
        for (uint t = 1; t <= ticks; t++)
            if (pacer.Due(t, tickRate, changed || t == 1, 7UL, casting, old)) { pacer.Sent(t, 7UL); sent++; }
        return sent;
    }

    [Fact]
    public void While_casting_the_links_go_out_a_true_20_times_a_second_and_otherwise_as_before()
    {
        Assert.InRange(Sends(30 * 10, casting: true, old: false), 20 * 10 - 1, 20 * 10 + 1);   // every 1 or 2 ticks at 30 Hz (integer division gave every tick: 30 a second)
        Assert.InRange(Sends(60 * 10, casting: true, old: false, tickRate: 60), 20 * 10 - 1, 20 * 10 + 1);
        Assert.InRange(Sends(30 * 10, casting: false, old: false), 10 * 10 - 1, 10 * 10 + 1);   // any other change: 10 a second
        Assert.InRange(Sends(30 * 10, casting: true, old: true), 10 * 10 - 1, 10 * 10 + 1);   // restore switch send: 10 a second while casting too
        Assert.InRange(Sends(30 * 10, casting: true, old: false, changed: false), 10 - 1, 10 + 1);   // nothing changing: once a second

        var pacer = new LibrarianSendPacer();
        Assert.True(pacer.Due(1, Rate, true, 1UL, true, false)); pacer.Sent(1, 1UL);
        Assert.True(pacer.Due(2, Rate, false, 2UL, true, false)); pacer.Sent(2, 2UL);   // a new edge goes at once, credit or not
        Assert.False(pacer.Due(3, Rate, false, 2UL, true, false));
        var oldPacer = new LibrarianSendPacer();
        Assert.True(oldPacer.Due(1, Rate, true, 1UL, true, true)); oldPacer.Sent(1, 1UL);
        Assert.False(oldPacer.Due(2, Rate, false, 2UL, true, true));   // the old cadence: no edge sends
    }

    [Fact]
    public void Casting_lightning_allocates_nothing_per_tick()
    {
        var sim = new MusouSim(null) { TickRate = Rate };
        for (int i = 0; i < 400; i++)   // a crowd ahead (+Y) and around, inside the lightning's 30 m; too tough to die
        {
            float angle = i * 2.399963f, distance = 3f + (i % 26);
            sim.Spawn(MusouEnemyType.Boy, new Vector3(MathF.Sin(angle) * distance * (i % 3 == 0 ? 1f : 0.2f), MathF.Abs(MathF.Cos(angle)) * distance + 2f, 0f), 0f, 1e7f);
        }
        var psyker = new LibrarianPsyker();
        var caster = new LibrarianCaster(1, Vector3.Zero, new Vector3(0f, 0f, 1.6f), 0f, 0f);
        var hold = new LibrarianPress(true, false);
        uint tick = 0;
        for (int k = 0; k < 10; k++) { sim.BeginTick(++tick); psyker.Step(sim, caster, hold, 1f / Rate); }   // warm (JIT, list capacities)
        Assert.True(psyker.Casting && psyker.LastStruck > 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 0; k < 20; k++) { sim.BeginTick(++tick); psyker.Step(sim, caster, hold, 1f / Rate); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(psyker.Casting);
        Assert.True(allocated == 0, $"{allocated} bytes over 20 casting ticks (Query's scratch is MusouSim.Capacity: a larger buffer makes it allocate)");
    }

    [Fact]
    public void A_shockwave_through_a_full_pool_credits_every_kill_though_its_events_overflow()
    {
        var sim = new MusouSim(null) { TickRate = Rate };
        int spawned = 0;
        for (int i = 0; i < MusouSim.Capacity; i++)
        {
            float angle = i * 2.399963f, distance = 2f + 55f * MathF.Sqrt((i + 0.5f) / MusouSim.Capacity);   // spread over the 60 m disc
            if (sim.Spawn(MusouEnemyType.Boy, new Vector3(MathF.Cos(angle) * distance, MathF.Sin(angle) * distance, 0f), 0f, MusouRules.Default.Of(MusouEnemyType.Boy).Health) != 0) spawned++;
        }
        Assert.Equal(MusouSim.Capacity, spawned);
        var psyker = new LibrarianPsyker();
        sim.BeginTick(1);
        psyker.Step(sim, new LibrarianCaster(1, Vector3.Zero, new Vector3(0f, 0f, 1.6f), 0f, 0f), new LibrarianPress(false, true), 1f / Rate);
        var kills = new List<MusouKill>();
        Assert.Equal(spawned, sim.TakeKills(kills));   // the kill credit is its own list: every kill counts
        Assert.All(kills, k => Assert.Equal(1, k.Player));
        Assert.True(sim.EventsDropped > 0, "a whole pool's deaths overflow MaxEventsPerTick (the spec names this: some death events are not sent)");
    }
}
