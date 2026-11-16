using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The Musou powerups on the network (owner, 2026-11-10; the review of 2026-11-10): the server sends a player its buffs after its drops in wire tag 6, refreshes them while they last and, after any change
/// (a new round's empty list included), sends the section again for a second so that one lost message cannot leave the client with old buffs; Rapid Fire's time is carried into the client's reconcile
/// through <see cref="GameClient.ExtrasCarry"/>, so its prediction fires as the server does and spends nothing.
/// </summary>
public class MusouPowerupsWireTests
{
    /// <summary>MU2's Musou harness (as <see cref="MusouDropsWireTests"/>): the enemies held and frozen.</summary>
    static Harness MusouHarness(LinkConditions link)
    {
        var rifle = new WeaponSpec("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
        var light = new MeleeStep(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
        ClassLoadout Class(string name, Team side) => new(name, 0, -1, [light]) { PrimaryChoices = [0], Side = side, Role = "tactical" };
        var config = new SimConfig { Weapons = [rifle], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = ContentExtras.Musou };
        var options = new SmServer.ServerOptions { Match = new MatchRules { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        var h = new Harness(link, 7, 0, config, options);
        h.Server.World.MusouDirector!.Hold = true;
        h.Server.World.Musou!.Frozen = true;
        return h;
    }

    [Fact]
    public void The_server_sends_the_buffs_refreshes_them_and_resends_a_change_for_a_second()
    {
        var h = MusouHarness(LinkConditions.Perfect);
        h.AddClient(nonce: 1);
        h.Run(TimeSpan.FromSeconds(3));
        var w = h.Server.World;
        Assert.Equal(MatchState.Playing, w.State);
        int a = h.Clients[0].PlayerId;
        h.Clients[0].TakeExtras();
        var lists = new List<(uint Tick, MusouBuffRecord[] Buffs, int Drops)>();
        void Read()
        {
            foreach (var s in h.Clients[0].TakeExtras())
                if (s.Tag == MusouWire.DropsTag && MusouWire.ReadDrops(s.Payload, w.Musou!.Director.Box, out var buffs) is { } drops) lists.Add((s.ServerTick, buffs, drops.Length));
        }

        // A buff: on the change, again every 5 ticks for a second, then the usual refresh every 15 while it lasts. Each copy counts its time down from its own tick.
        uint given = w.Tick;
        Assert.True(w.MusouGiveBuff(a, MusouPowerup.DoubleDamage));
        h.Run(TimeSpan.FromSeconds(1), after: Read);
        int settling = lists.Count;
        Assert.InRange(settling, 5, 8);
        h.Run(TimeSpan.FromSeconds(1), after: Read);
        Assert.InRange(lists.Count - settling, 1, 3);
        uint full = (uint)(MusouPowerupRules.Seconds(MusouPowerup.DoubleDamage) * w.Config.TickRate);
        foreach (var (tick, buffs, drops) in lists)
        {
            Assert.Equal(0, drops);
            var b = Assert.Single(buffs);
            Assert.Equal(MusouPowerup.DoubleDamage, b.Kind);
            Assert.Equal(given + full, tick + b.TicksLeft);
        }

        // A new round ends it: the empty list comes on the change and again for a second (one lost copy no longer leaves the old buff), then nothing (no drops, no buffs).
        lists.Clear();
        typeof(World).GetMethod("MusouNewRound", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, null);
        h.Run(TimeSpan.FromSeconds(1.2), after: Read);
        Assert.InRange(lists.Count, 5, 8);
        Assert.All(lists, l => Assert.Empty(l.Buffs));
        int after = lists.Count;
        h.Run(TimeSpan.FromSeconds(1.5), after: Read);
        Assert.Equal(after, lists.Count);
    }

    /// <summary>
    /// The most the client's predicted clip falls below the full clip while it fires under Rapid Fire (the server spends nothing) over 60 ms each way; <paramref name="carry"/>: the client hands its
    /// buff's end to the reconcile as the networked view does (<c>NetworkView.MusouPowerups.cs</c>), else nothing. The ticks the carry was called with go to <paramref name="calls"/>.
    /// </summary>
    static (int Deficit, int ServerSpent, float Boost) FireUnderRapidFire(bool carry, List<(uint At, uint Latest)> calls)
    {
        var h = MusouHarness(new LinkConditions(TimeSpan.FromMilliseconds(60), TimeSpan.Zero, 0));
        var client = h.AddClient(nonce: 1);
        h.Run(TimeSpan.FromSeconds(3));
        var w = h.Server.World;
        Assert.Equal(MatchState.Playing, w.State);
        int a = client.PlayerId;
        uint end = 0;
        void Read()
        {
            foreach (var s in client.TakeExtras())
                if (s.Tag == MusouWire.DropsTag && MusouWire.ReadDrops(s.Payload, w.Musou!.Director.Box, out var buffs) is not null)
                    end = buffs.FirstOrDefault(b => b.Kind == MusouPowerup.RapidFire) is { Kind: MusouPowerup.RapidFire } r ? s.ServerTick + r.TicksLeft : 0;
        }
        if (carry)
            client.ExtrasCarry = (record, at) =>
            {
                calls.Add((at, client.LatestSnapshotTick));
                return record with { FireBoostLeft = record.Alive && end > at ? (end - at) / (float)w.Config.TickRate : 0f };
            };
        Assert.True(w.MusouGiveBuff(a, MusouPowerup.RapidFire));
        h.Run(TimeSpan.FromSeconds(0.5), after: Read);   // the buff reaches the client before it fires
        int full = w.Players[a].Clip, deficit = 0, shots = 0;
        uint seen = w.Tick;
        float boost = 0f;
        h.Run(TimeSpan.FromSeconds(1.5), input: (_, t) => new PlayerInput(t, 0f, 0f, 0f, InputButtons.Fire), after: () =>
        {
            Read();
            if (w.Tick != seen) { seen = w.Tick; shots += w.LastShots.Count; }
            if (client.Predicted.Alive) deficit = Math.Max(deficit, full - client.Predicted.Clip);
            boost = MathF.Max(boost, client.Predicted.FireBoostLeft);
        });
        Assert.True(shots >= 15, $"the server fired {shots} shots");   // 1.5 s at x2 of 0.12 s, less the raise
        return (deficit, full - w.Players[a].Clip, boost);
    }

    [Fact]
    public void Rapid_fire_is_predicted_through_the_extras_carry()
    {
        var calls = new List<(uint At, uint Latest)>();
        var carried = FireUnderRapidFire(true, calls);
        Assert.Equal(0, carried.ServerSpent);   // the server: no round spent
        Assert.Equal(0, carried.Deficit);       // the prediction: none either
        Assert.InRange(carried.Boost, 15f, 20f);
        // The reconcile hands the carry the own record with the snapshot's tick (never past the newest snapshot).
        Assert.True(calls.Count > 20, $"{calls.Count} calls");
        Assert.All(calls, c => Assert.True(c.At <= c.Latest, $"carry at {c.At}, newest snapshot {c.Latest}"));
        // Without the carry each reconcile drops the boost (not on the wire) and the replay spends rounds the server never spends.
        var bare = FireUnderRapidFire(false, new List<(uint, uint)>());
        Assert.Equal(0, bare.ServerSpent);
        Assert.True(bare.Deficit > 0, $"deficit {bare.Deficit}");
    }
}
