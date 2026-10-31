using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou" (MU2): the client's crowd state (<see cref="MusouClientState"/>). Sections wait for the first director's box; each enemy is
/// interpolated between its records at the render tick; an id goes after its death event (once, whatever the copies) or 2 s of silence; a record older
/// than a death cannot raise it; a reused id after the death is a new enemy. Over loopback the client's places match the server's within the quantisation.
/// </summary>
public class MusouClientTests
{
    static readonly MusouBox Box = new(new Vector3(-100f, -100f, -10f), new Vector3(100f, 100f, 30f));
    static readonly byte[] Director = MusouWire.WriteDirector(new MusouDirectorView(1, 5, MusouPhase.Wave, 0, 80, 80, 0, 0f, 0f, 3, Box));

    static ExtrasSection Entities(uint tick, params MusouEntity[] e) => new(tick, MusouWire.EntitiesTag, MusouWire.WriteEntities(e, Box));
    static ExtrasSection Events(uint tick, params MusouEvent[] e) => new(tick, MusouWire.EventsTag, MusouWire.WriteEvents(e, Box));
    static ExtrasSection DirectorSection(uint tick) => new(tick, MusouWire.DirectorTag, Director);
    static MusouEntity At(ushort id, float x, MusouEnemyState state = MusouEnemyState.Seek) => new(id, new Vector3(x, 0f, 0f), 0f, MusouEnemyType.Boy, state);

    [Fact]
    public void Sections_before_the_first_director_wait_for_its_box()
    {
        var s = new MusouClientState();
        Assert.True(s.Apply(Entities(5, At(1, 10f)), 0.0));
        Assert.Equal(0, s.Count);
        Assert.True(s.Apply(DirectorSection(6), 0.0));
        Assert.Equal(1, s.Count);
        Assert.Equal(10f, s.Sample(5, 0.0)[0].Position.X, 0.01f);
        Assert.False(s.Apply(new ExtrasSection(7, MusouWire.EntitiesTag, [3, 1]), 0.0));
        Assert.Equal(1, s.SectionsMalformed);
    }

    [Fact]
    public void An_enemy_is_interpolated_between_its_records_and_held_past_the_newest()
    {
        var s = new MusouClientState();
        s.Apply(DirectorSection(1), 0.0);
        s.Apply(Entities(10, At(1, 0f)), 0.0);
        s.Apply(Entities(20, At(1, 10f)), 0.0);
        s.Apply(Entities(15, At(1, 7f)), 0.0);   // late: sorted in
        Assert.Equal(0f, s.Sample(5, 0.1)[0].Position.X, 0.01f);
        Assert.Equal(3.5f, s.Sample(12.5, 0.1)[0].Position.X, 0.01f);
        Assert.Equal(8.5f, s.Sample(17.5, 0.1)[0].Position.X, 0.01f);
        Assert.Equal(10f, s.Sample(30, 0.1)[0].Position.X, 0.01f);
    }

    [Fact]
    public void A_death_drops_the_id_once_and_a_late_record_cannot_raise_it()
    {
        var s = new MusouClientState();
        s.Apply(DirectorSection(1), 0.0);
        s.Apply(Entities(10, At(3, 5f)), 0.0);
        var death = new MusouEvent(MusouEventKind.Death, 12, 3, 2, (byte)MusouDeathKind.Melee, Vector3.UnitY);
        s.Apply(Events(12, death), 0.1);
        s.Apply(Events(13, death), 0.15);
        s.Apply(Events(14, death), 0.2);
        Assert.Equal(0, s.Count);
        Assert.Single(s.NewDeaths);
        Assert.Equal((ushort)3, s.NewDeaths[0].Id);
        Assert.Equal(5f, s.NewDeaths[0].Position.X, 0.01f);
        Assert.Equal(MusouDeathKind.Melee, s.NewDeaths[0].Cause);
        Assert.Equal(1, s.EventsPlayed);
        Assert.Equal(2, s.EventCopies);
        s.TakeNew();
        s.Apply(Entities(11, At(3, 5f)), 0.25);   // older than the death
        Assert.Equal(0, s.Count);
        Assert.Equal(1, s.StaleRecords);
        s.Apply(Entities(40, At(3, -20f)), 0.3);   // the id reused later: a new enemy
        Assert.Equal(1, s.Count);
        Assert.Equal(-20f, s.Sample(40, 0.3)[0].Position.X, 0.01f);
    }

    [Fact]
    public void Two_seconds_of_silence_drop_an_id()
    {
        var s = new MusouClientState();
        s.Apply(DirectorSection(1), 0.0);
        s.Apply(Entities(10, At(1, 0f), At(2, 1f)), 0.0);
        s.Apply(Entities(50, At(2, 1f)), 1.5);
        Assert.Equal(2, s.Sample(50, 1.9).Count);
        Assert.Single(s.Sample(50, 2.1));
        Assert.Equal(1, s.DroppedSilent);
    }

    [Fact]
    public void The_state_time_counts_from_the_change()
    {
        var s = new MusouClientState();
        s.Apply(DirectorSection(1), 0.0);
        s.Apply(Entities(10, At(1, 0f)), 0.0);
        Assert.Equal(1.0f, s.Sample(10, 1.0)[0].StateSeconds, 0.001f);
        s.Apply(Entities(11, At(1, 0f, MusouEnemyState.WindUp)), 1.0);
        Assert.Equal(MusouEnemyState.WindUp, s.Sample(11, 1.2)[0].State);
        Assert.Equal(0f, s.Sample(11, 1.2)[0].StateSeconds, 0.001f);
        Assert.Equal(0.3f, s.Sample(11, 1.5)[0].StateSeconds, 0.001f);
    }

    [Fact]
    public void Over_loopback_the_client_sees_the_servers_enemies_within_the_quantisation()
    {
        var h = MusouSendTests.MusouHarness(out _);
        h.AddClient(nonce: 1);
        h.Run(TimeSpan.FromSeconds(3));
        var sim = h.Server.World.Musou!;
        var rng = new Random(9);
        var spawned = new Dictionary<ushort, Vector3>();
        for (int i = 0; i < 200; i++)
        {
            var at = new Vector3(rng.NextSingle() * 120f - 60f, rng.NextSingle() * 120f - 60f, 0f);
            spawned[sim.Spawn((MusouEnemyType)(i % 7), at, 0f, 85f)] = at;
        }
        var state = new MusouClientState();
        var client = h.Clients[0];
        h.Run(TimeSpan.FromSeconds(1.5), after: () => { foreach (var s in client.TakeExtras()) state.Apply(s, h.Clock.Now.TotalSeconds); });
        Assert.NotNull(state.Director);
        var box = state.Director!.Value.Box;
        var step = MusouWire.PositionStep(box);
        var shown = state.Sample(state.NewestTick, h.Clock.Now.TotalSeconds);
        Assert.Equal(200, shown.Count);
        foreach (var e in shown)
        {
            var d = Vector3.Abs(e.Position - spawned[e.Id]);
            Assert.True(d.X <= step.X && d.Y <= step.Y && d.Z <= step.Z, $"{e.Id}: {spawned[e.Id]} -> {e.Position}");
            Assert.Equal((MusouEnemyType)((e.Id - 1) % 7), e.Type);
        }
    }

    [Fact]
    public void Two_hits_on_one_enemy_in_one_tick_by_two_players_both_play()
    {
        var s = new MusouClientState();
        s.Apply(DirectorSection(1), 0.0);
        var a = new MusouEvent(MusouEventKind.Hit, 12, 3, 1, 1, Vector3.Zero);
        var b = a with { Player = 2 };
        s.Apply(Events(12, a, b), 0.0);
        s.Apply(Events(13, a, b), 0.0);
        Assert.Equal(2, s.EventsPlayed);
        Assert.Equal(2, s.EventCopies);
    }
}
