using System.Numerics;
using SmNet.Extras.Musou;
using SmServer;
using SmSim;
using SmSim.Extras.Musou;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou" (MU2): the server's sends. Interest (<see cref="MusouInterest"/>): near enemies every tick, mid every 2nd, far every 5th, a changed
/// state at once, the boss and the Nobs first; at most 4 messages a tick, every payload within one envelope section and every message within 1200 bytes; each
/// event in 3 messages (<see cref="MusouEventWindow"/>); the director first, on a change and every 10 ticks. Over loopback with 300 enemies (the
/// <c>SM_MUSOU_DUMMIES</c> test aid's walkers, converging on the player) a client gets at most 60 KB/s.
/// </summary>
public class MusouSendTests(ITestOutputHelper output)
{
    static readonly MusouBox Box = new(new Vector3(-100f, -100f, -10f), new Vector3(100f, 100f, 30f));
    static readonly byte[] Director = MusouWire.WriteDirector(new MusouDirectorView(1, 5, MusouPhase.Wave, 0, 80, 80, 0, 0f, 0f, 3, Box));

    static MusouEnemy Enemy(ushort id, Vector3 at, MusouEnemyType type = MusouEnemyType.Boy, MusouEnemyState state = MusouEnemyState.Seek) =>
        new(id, type, state, at, default, 0f, 85f, -1, 0, 0, 0);

    static IEnumerable<MusouEntity> EntitiesOf(IEnumerable<(byte Tag, byte[] Payload)[]> messages) =>
        messages.SelectMany(m => m).Where(s => s.Tag == MusouWire.EntitiesTag).SelectMany(s => MusouWire.ReadEntities(s.Payload, Box)!);

    [Fact]
    public void Near_enemies_go_every_tick_mid_every_second_and_far_every_fifth()
    {
        var interest = new MusouInterest();
        MusouEnemy[] enemies = [Enemy(1, new Vector3(10f, 0f, 0f)), Enemy(2, new Vector3(30f, 0f, 0f)), Enemy(3, new Vector3(0f, 70f, 0f))];
        var sent = new Dictionary<ushort, List<uint>> { [1] = new(), [2] = new(), [3] = new() };
        for (uint tick = 1; tick <= 40; tick++)
            foreach (var e in EntitiesOf(interest.Plan(tick, 30, enemies, Vector3.Zero, Box, Director, [])))
                sent[e.Id].Add(tick);
        // The first tick sends every enemy (new to the client); then the bands' rates.
        Assert.Equal(Enumerable.Range(1, 40).Select(t => (uint)t), sent[1]);
        Assert.All(sent[2].Skip(1).Zip(sent[2].Skip(2)), p => Assert.Equal(2u, p.Second - p.First));
        Assert.All(sent[3].Skip(1).Zip(sent[3].Skip(2)), p => Assert.Equal(5u, p.Second - p.First));
        Assert.Equal(1u, sent[2][0]);
        Assert.Equal(1u, sent[3][0]);
    }

    [Fact]
    public void A_changed_state_goes_at_once_and_the_dead_are_not_sent()
    {
        var interest = new MusouInterest();
        var far = Enemy(7, new Vector3(0f, 90f, 0f));
        interest.Plan(1, 30, new[] { far }, Vector3.Zero, Box, Director, []);
        Assert.Empty(EntitiesOf(interest.Plan(2, 30, new[] { far }, Vector3.Zero, Box, Director, [])));   // 0.2 of 1.0
        var winding = far with { State = MusouEnemyState.WindUp };
        Assert.Single(EntitiesOf(interest.Plan(3, 30, new[] { winding }, Vector3.Zero, Box, Director, [])));
        var dying = far with { State = MusouEnemyState.Dying, Health = 0f };
        for (uint t = 4; t < 20; t++) Assert.Empty(EntitiesOf(interest.Plan(t, 30, new[] { dying }, Vector3.Zero, Box, Director, [])));
    }

    [Fact]
    public void The_boss_and_the_nobs_go_first_when_the_budget_is_short()
    {
        var interest = new MusouInterest();
        var rng = new Random(5);
        var enemies = Enumerable.Range(1, 500).Select(i => Enemy((ushort)i, new Vector3(rng.NextSingle() * 10f, rng.NextSingle() * 10f, 0f),
            i == 333 ? MusouEnemyType.Warboss : i % 50 == 0 ? MusouEnemyType.Nob : MusouEnemyType.Boy)).ToArray();
        // The first tick: 500 are due, the budget holds fewer: the boss and the 10 Nobs are among those sent.
        var first = EntitiesOf(interest.Plan(1, 30, enemies, Vector3.Zero, Box, Director, [])).ToList();
        Assert.True(first.Count < 500, $"{first.Count} sent");
        Assert.Equal(MusouEnemyType.Warboss, first[0].Type);
        Assert.Equal(11, first.Take(11).Count(e => MusouSendRules.IsLeader(e.Type)));
        Assert.True(interest.RecordsHeld > 0);
        // Over a few ticks every enemy is sent (the held ones keep their priority and climb).
        var seen = first.Select(e => e.Id).ToHashSet();
        for (uint t = 2; t <= 10; t++) foreach (var e in EntitiesOf(interest.Plan(t, 30, enemies, Vector3.Zero, Box, Director, []))) seen.Add(e.Id);
        Assert.Equal(500, seen.Count);
    }

    [Fact]
    public void Every_message_fits_the_envelope_and_a_tick_sends_at_most_four()
    {
        var interest = new MusouInterest();
        var window = new MusouEventWindow();
        var rng = new Random(11);
        var types = Enum.GetValues<MusouEnemyType>();
        var kinds = Enum.GetValues<MusouEventKind>();
        for (uint tick = 1; tick <= 300; tick++)
        {
            var enemies = Enumerable.Range(1, 400).Select(i => Enemy((ushort)i,
                new Vector3(rng.NextSingle() * 200f - 100f, rng.NextSingle() * 200f - 100f, rng.NextSingle() * 10f), types[i % types.Length],
                (MusouEnemyState)rng.Next(0, 6))).ToArray();
            var events = Enumerable.Range(0, rng.Next(0, 120)).Select(k => new MusouEvent(kinds[k % kinds.Length], tick, (ushort)rng.Next(1, 401), 1, 1,
                new Vector3(rng.NextSingle(), rng.NextSingle(), 0f))).ToList();
            var messages = interest.Plan(tick, 30, enemies, new Vector3(rng.NextSingle() * 50f, 0f, 0f), Box, Director, window.Step(tick, events));
            Assert.InRange(messages.Count, 1, MusouSendRules.MaxMessagesPerTick);
            foreach (var m in messages)
            {
                Assert.All(m, s => Assert.InRange(s.Payload.Length, 1, Protocol.MaxExtrasPayloadBytes));
                var update = new ExtrasUpdate(tick, m);
                Assert.True(Protocol.EnvelopeBytes(update) <= Protocol.MaxExtrasMessageBytes, $"{Protocol.EnvelopeBytes(update)} bytes");
                Assert.Equal(Protocol.EnvelopeBytes(update), Protocol.Write(update).Length);
            }
        }
    }

    [Fact]
    public void Each_event_goes_in_three_messages_deaths_first()
    {
        var window = new MusouEventWindow();
        var hit = new MusouEvent(MusouEventKind.Hit, 10, 4, 1, 1, Vector3.Zero);
        var death = new MusouEvent(MusouEventKind.Death, 10, 4, 1, 0, Vector3.UnitX);
        Assert.Equal([death, hit], window.Step(10, [hit, death]));
        Assert.Equal([death, hit], window.Step(11, []));
        Assert.Equal([death, hit], window.Step(12, []));
        Assert.Empty(window.Step(13, []));
        // Over the cap the oldest hits go first; deaths stay.
        var many = Enumerable.Range(1, 200).Select(i => new MusouEvent(MusouEventKind.Hit, 20, (ushort)i, 1, 1, Vector3.Zero)).Append(death with { Tick = 20 }).ToList();
        var chosen = window.Step(20, many);
        Assert.Equal(MusouSendRules.MaxEventsPerTick, chosen.Count);
        Assert.Equal(MusouEventKind.Death, chosen[0].Kind);
        Assert.True(window.CopiesDropped > 0);
    }

    [Fact]
    public void The_director_goes_first_on_a_change_and_every_ten_ticks()
    {
        var interest = new MusouInterest();
        MusouEnemy[] none = [];
        var at = new List<uint>();
        var other = MusouWire.WriteDirector(new MusouDirectorView(2, 5, MusouPhase.Break, 600, 0, 0, 0, 0f, 0f, 3, Box));
        for (uint tick = 1; tick <= 35; tick++)
        {
            var messages = interest.Plan(tick, 30, none, Vector3.Zero, Box, tick >= 15 ? other : Director, []);
            if (messages.Count > 0 && messages[0][0].Tag == MusouWire.DirectorTag) at.Add(tick);
        }
        Assert.Equal([1u, 11u, 15u, 25u, 35u], at);
    }

    [Fact]
    public void Three_hundred_dummies_cost_a_client_at_most_60_kilobytes_a_second()
    {
        var h = MusouHarness(out var config);
        h.Server.SetMusouDummies(300);
        h.AddClient(nonce: 1);
        h.Run(TimeSpan.FromSeconds(8));   // join, countdown, the first releases (6 a tick)
        Assert.Equal(MatchState.Playing, h.Server.World.State);
        Assert.True(h.Server.World.Musou!.Alive >= 290, $"{h.Server.World.Musou.Alive} alive");
        long bytes0 = h.Server.ExtrasBytesSent;
        int messages0 = h.Server.ExtrasMessagesSent;
        long records0 = h.Server.MusouRecordsSent;
        const int seconds = 20;
        var client = h.Clients[0];
        var state = new MusouClientState();
        h.Run(TimeSpan.FromSeconds(seconds), after: () =>
        {
            foreach (var s in client.TakeExtras()) state.Apply(s, h.Clock.Now.TotalSeconds);
        });
        double perSecond = (h.Server.ExtrasBytesSent - bytes0) / (double)seconds;
        double messagesPerTick = (h.Server.ExtrasMessagesSent - messages0) / (double)(seconds * config.TickRate);
        output.WriteLine($"300 dummies, 1 client: {perSecond / 1000.0:F1} KB/s, {messagesPerTick:F2} messages a tick, {(h.Server.MusouRecordsSent - records0) / (double)seconds:F0} records/s, " +
                         $"held {h.Server.MusouRecordsHeld}, largest {h.Server.LargestExtrasMessage} B, deaths {h.Server.MusouDummiesKilled}, client knows {state.Count}, events {state.EventsPlayed} (+{state.EventCopies} copies)");
        Assert.True(perSecond <= 60_000, $"{perSecond:F0} B/s");
        Assert.True(h.Server.LargestExtrasMessage <= Protocol.MaxExtrasMessageBytes);
        Assert.True(messagesPerTick <= MusouSendRules.MaxMessagesPerTick);
        Assert.Equal(0, state.SectionsMalformed);
        Assert.True(h.Server.MusouDummiesKilled > 0);
        Assert.True(state.EventsPlayed >= h.Server.MusouDummiesKilled);   // every death reached the client (perfect link)
        Assert.InRange(state.Count, 250, 300);
    }

    internal static Harness MusouHarness(out SimConfig config)
    {
        var rifle = new WeaponSpec("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
        var light = new MeleeStep(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
        ClassLoadout Class(string name, Team side) => new(name, 0, -1, [light]) { PrimaryChoices = [0], Side = side, Role = "tactical" };
        config = new SimConfig { Weapons = [rifle], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = ContentExtras.Musou };
        var options = new ServerOptions { Match = new MatchRules { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        return new Harness(LinkConditions.Perfect, 7, 0, config, options);
    }
}
