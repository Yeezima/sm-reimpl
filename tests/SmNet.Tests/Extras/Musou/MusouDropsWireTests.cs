using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou" (MU-D): the drops on the wire. Tag 6 carries a player's whole drop list in 11-byte records (round trip, malformed payloads refused),
/// and the server sends each client only its own player's drops: a client never receives another player's drop; a pick-up reaches the picker as a picked
/// record. The join is stripped at once on the server (the review's join gap).
/// </summary>
public class MusouDropsWireTests
{
    static readonly MusouBox Box = new(new Vector3(-100f, -100f, -10f), new Vector3(100f, 100f, 30f));

    /// <summary>MU2's Musou harness (<see cref="MusouSendTests"/>), its config changed by <paramref name="change"/>.</summary>
    static Harness DropHarness(Func<SimConfig, SimConfig>? change = null)
    {
        var rifle = new WeaponSpec("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
        var light = new MeleeStep(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
        ClassLoadout Class(string name, Team side) => new(name, 0, -1, [light]) { PrimaryChoices = [0], Side = side, Role = "tactical" };
        var config = new SimConfig { Weapons = [rifle], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = ContentExtras.Musou };
        if (change is not null) config = change(config);
        var options = new SmServer.ServerOptions { Match = new MatchRules { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        var h = new Harness(LinkConditions.Perfect, 7, 0, config, options);
        h.Server.World.MusouDirector!.Hold = true;
        h.Server.World.Musou!.Frozen = true;
        return h;
    }

    [Fact]
    public void Drops_round_trip_and_malformed_payloads_are_refused()
    {
        Assert.Equal(6, MusouWire.DropsTag);
        Assert.Equal(108, MusouWire.MaxDrops);
        MusouDropRecord[] drops =
        [
            new(1, new Vector3(12.5f, -40.25f, 3f), 4, MusouDropState.Live),
            new(65535, new Vector3(-99f, 99f, 29f), MusouDropItem.Melee(2), MusouDropState.Picked),
            new(7, Vector3.Zero, 0, MusouDropState.Expired),
        ];
        var bytes = MusouWire.WriteDrops(drops, Box);
        Assert.Equal(1 + 3 * MusouWire.DropBytes, bytes.Length);
        var back = MusouWire.ReadDrops(bytes, Box)!;
        Assert.Equal(3, back.Length);
        var step = MusouWire.PositionStep(Box);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal((drops[i].Id, drops[i].Item, drops[i].State), (back[i].Id, back[i].Item, back[i].State));
            var d = Vector3.Abs(back[i].Position - drops[i].Position);
            Assert.True(d.X <= step.X && d.Y <= step.Y && d.Z <= step.Z, $"{back[i].Position} vs {drops[i].Position}");
        }
        Assert.Empty(MusouWire.ReadDrops(MusouWire.WriteDrops([], Box), Box)!);
        // Malformed: a short or long payload, id 0, an id twice, an unknown state, too many.
        Assert.Null(MusouWire.ReadDrops(bytes.AsSpan(0, bytes.Length - 1), Box));
        Assert.Null(MusouWire.ReadDrops([.. bytes, 0], Box));
        Assert.Null(MusouWire.ReadDrops([], Box));
        Assert.Null(MusouWire.ReadDrops(MusouWire.WriteDrops([drops[0] with { Id = 0 }], Box), Box));
        Assert.Null(MusouWire.ReadDrops(MusouWire.WriteDrops([drops[0], drops[0]], Box), Box));
        var bad = MusouWire.WriteDrops([drops[0]], Box);
        bad[11] = 3;
        Assert.Null(MusouWire.ReadDrops(bad, Box));
        var many = MusouWire.WriteDrops(Enumerable.Range(1, 200).Select(i => new MusouDropRecord((ushort)i, Vector3.Zero, 0, MusouDropState.Live)).ToList(), Box);
        Assert.Equal(MusouWire.MaxDrops, many[0]);
        Assert.True(Protocol.EnvelopeBytes(new ExtrasUpdate(1, [(MusouWire.DropsTag, many)])) <= Protocol.MaxExtrasMessageBytes);
    }

    [Fact]
    public void Each_client_gets_only_its_own_players_drops()
    {
        var h = DropHarness();
        h.AddClient(nonce: 1);
        h.AddClient(nonce: 2);
        h.Run(TimeSpan.FromSeconds(3));
        Assert.Equal(MatchState.Playing, h.Server.World.State);
        int a = h.Clients[0].PlayerId, b = h.Clients[1].PlayerId;
        Assert.True(a > 0 && b > 0 && a != b);
        foreach (var c in h.Clients) c.TakeExtras();

        var w = h.Server.World;
        var mine = w.MusouSpawnDrop(a, w.Players[a].Position + new Vector3(1f, 0f, 0f), 0)!.Value;
        var other = w.MusouSpawnDrop(b, w.Players[b].Position + new Vector3(30f, 0f, 0f), 0)!.Value;
        var got = new Dictionary<int, List<MusouDropRecord[]>> { [a] = new(), [b] = new() };
        void Read()
        {
            foreach (var (c, id) in new[] { (h.Clients[0], a), (h.Clients[1], b) })
                foreach (var s in c.TakeExtras())
                    if (s.Tag == MusouWire.DropsTag) got[id].Add(MusouWire.ReadDrops(s.Payload, w.Musou!.Director.Box)!);
        }
        h.Run(TimeSpan.FromSeconds(1), after: Read);
        Assert.NotEmpty(got[a]);
        Assert.NotEmpty(got[b]);
        Assert.All(got[a], list => Assert.Equal([mine.Id], list.Select(d => d.Id)));
        Assert.All(got[b], list => Assert.Equal([other.Id], list.Select(d => d.Id)));
        Assert.All(got[a].SelectMany(l => l), d => Assert.Equal((MusouDropState.Live, (ushort)0), (d.State, d.Item)));
        // Sent on the change and refreshed (every 15 ticks), not every tick.
        Assert.InRange(got[a].Count, 2, 6);

        // Player a takes its drop (E; the harness config has no strip, the drop's bolter is the class's primary): a picked record reaches a only.
        got[a].Clear(); got[b].Clear();
        h.Run(TimeSpan.FromSeconds(0.5), input: (client, tick) => client == 0 && tick % 2 == 0 ? new PlayerInput(tick, 0f, 0f, 0f, InputButtons.Interact) : new PlayerInput(tick, 0f, 0f, 0f, InputButtons.None), after: Read);
        Assert.Contains(got[a].SelectMany(l => l), d => d.Id == mine.Id && d.State == MusouDropState.Picked);
        Assert.DoesNotContain(got[b].SelectMany(l => l), d => d.Id == mine.Id);
        Assert.Equal(MusouDropState.Live, w.MusouDrops.Single(d => d.Id == other.Id).State);
        Assert.True(h.Server.MusouDropSections > 0);
        Assert.True(h.Server.LargestExtrasMessage <= Protocol.MaxExtrasMessageBytes);
    }

    [Fact]
    public void A_server_join_is_stripped_to_the_pistol_before_any_tick()
    {
        var pistol = new WeaponSpec("bolt_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
        var h = DropHarness(c => MusouLoadout.Prepare(c with
        {
            Weapons = [.. c.Weapons, pistol],
            Classes = c.Classes.Select(k => k with { Pistol = c.Weapons.Count }).ToList(),
        }));
        PlayerState? joined = null;
        h.Server.PlayerJoined += p => joined = h.Server.World.Players[p.PlayerId];   // after the Musou handler (subscribed at creation)
        h.AddClient(nonce: 1);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.NotNull(joined);
        var c = h.Server.World.Config;
        Assert.True(MusouLoadout.Prepared(c));
        Assert.Equal(WeaponSlot.Pistol, joined!.Value.Slot);
        Assert.Equal(-1, Loadouts.WeaponIndex(c, joined.Value, WeaponSlot.Ranged));
        Assert.Equal("bolt_pistol", Loadouts.InHand(c, joined.Value)!.Name);
    }
}
