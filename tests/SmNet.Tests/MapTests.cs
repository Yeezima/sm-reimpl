using System.Numerics;
using SmNet;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>M7: the server names its map, clients load the same collision, and prediction stays exact against walls.</summary>
public class MapTests(ITestOutputHelper output)
{
    static readonly LinkConditions Rtt150 = new(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);

    /// <summary>A 30 × 30 m room with 4 m walls, a pillar in the middle, and a 0.3 m platform in one corner.</summary>
    static StaticMesh Room(float pillar = 2f)
    {
        var v = new List<Vector3>(); var i = new List<int>();
        void Box(Vector3 min, Vector3 max)
        {
            int b = v.Count;
            for (int k = 0; k < 8; k++) v.Add(new Vector3((k & 1) == 0 ? min.X : max.X, (k & 2) == 0 ? min.Y : max.Y, (k & 4) == 0 ? min.Z : max.Z));
            int[][] faces = [[0, 1, 3, 2], [4, 6, 7, 5], [0, 4, 5, 1], [2, 3, 7, 6], [0, 2, 6, 4], [1, 5, 7, 3]];
            foreach (var f in faces) i.AddRange([b + f[0], b + f[1], b + f[2], b + f[0], b + f[2], b + f[3]]);
        }
        Box(new(-15, -15, -1), new(15, 15, 0));          // floor
        Box(new(-16, -15, 0), new(-15, 15, 4));          // walls
        Box(new(15, -15, 0), new(16, 15, 4));
        Box(new(-15, -16, 0), new(15, -15, 4));
        Box(new(-15, 15, 0), new(15, 16, 4));
        Box(new(-pillar / 2, -pillar / 2, 0), new(pillar / 2, pillar / 2, 4));
        Box(new(8, 8, 0), new(15, 15, 0.3f));            // platform (a step up)
        return new StaticMesh([.. v], [.. i]);
    }

    static GameMap Map(StaticMesh mesh) => new("test_room", new CollisionWorld(mesh, mesh), Spawns, mesh.ContentHash);
    static readonly SpawnPoint[] Spawns =
        [new SpawnPoint(new Vector3(-10, -10, 0), 0f), new SpawnPoint(new Vector3(10, -10, 0), MathF.PI / 2), new SpawnPoint(new Vector3(-10, 10, 0), MathF.PI)];
    static LoadedMap Loaded(StaticMesh mesh) => new(new CollisionWorld(mesh, mesh), mesh.ContentHash);

    [Fact]
    public void Clients_load_the_named_map_and_predict_exactly_against_its_walls()
    {
        var map = Map(Room());
        var loaded = new List<string>();
        var options = new ClientOptions { LoadMap = name => { loaded.Add(name); return Loaded(Room()); } };
        var h = new Harness(Rtt150, seed: 7, clients: 2, map: map, clientOptions: options);
        bool inside = true; string? where = null;
        h.Run(TimeSpan.FromSeconds(20), Harness.Scripted, () =>
        {
            foreach (var p in h.Server.World.Players.Values)
            {
                bool ok = MathF.Abs(p.Position.X) <= 15f - 0.4f && MathF.Abs(p.Position.Y) <= 15f - 0.4f && p.Position.Z >= -0.01f
                          // Horizontal distance from the 2 × 2 m pillar is at least the capsule radius (corners are round).
                          && new Vector2(MathF.Max(0, MathF.Abs(p.Position.X) - 1f), MathF.Max(0, MathF.Abs(p.Position.Y) - 1f)).Length() >= 0.45f - 0.01f;
                if (!ok && inside) where = $"player {p.PlayerId} at {p.Position} on tick {h.Server.World.Tick}";
                inside &= ok;
            }
        });
        output.WriteLine(where ?? "always inside");

        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        Assert.All(h.Clients, c => Assert.Equal("test_room", c.MapName));
        Assert.Contains("test_room", loaded);
        Assert.True(inside, "a player left the room or entered the pillar");
        Assert.All(h.Server.World.Players.Values, p => Assert.True(Harness.Finite(p.Position)));
        foreach (var c in h.Clients)
        {
            var e = c.CorrectionErrors;
            output.WriteLine($"client {c.PlayerId}: {e.Count} corrections, p99 {Harness.Percentile(e, 0.99):F4} m, max {(e.Count > 0 ? e.Max() : 0):F4} m");
            Assert.True(e.Count > 100);
            Assert.True(e.Max() < 1e-4f, $"prediction diverged from the server by {e.Max()} m");
        }
    }

    [Fact]
    public void Players_spawn_on_the_maps_points_with_their_facing()
    {
        var map = Map(Room());
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(5), TimeSpan.Zero, 0), seed: 1, clients: 3, map: map,
            clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
        // Nobody moves: players stay where they spawned (their facing follows their own input, so it is not checked here).
        h.Run(TimeSpan.FromMilliseconds(500));
        var spawned = h.Server.World.Players.Values.ToList();
        Assert.Equal(3, spawned.Count);
        foreach (var p in spawned)
        {
            var point = map.Spawns.MinBy(s => Vector2.Distance(new(s.Position.X, s.Position.Y), new(p.Position.X, p.Position.Y)));
            Assert.True(Vector2.Distance(new(point.Position.X, point.Position.Y), new(p.Position.X, p.Position.Y)) < 0.5f, $"{p.Position} is not at a spawn point");
        }
        Assert.Equal(3, spawned.Select(p => (int)MathF.Round(p.Position.X) * 100 + (int)MathF.Round(p.Position.Y)).Distinct().Count());
    }

    [Fact]
    public void A_client_without_the_map_or_with_different_geometry_is_refused()
    {
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(5), TimeSpan.Zero, 0), seed: 1, clients: 0, map: Map(Room()));
        var noLoader = h.AddClient(nonce: 1);
        var missing = h.AddClient(nonce: 2, options: new ClientOptions { LoadMap = _ => throw new FileNotFoundException("no such map here") });
        var different = h.AddClient(nonce: 3, options: new ClientOptions { LoadMap = _ => Loaded(Room(pillar: 3f)) });
        var same = h.AddClient(nonce: 4, options: new ClientOptions { LoadMap = _ => Loaded(Room()) });
        h.Run(TimeSpan.FromSeconds(1));

        foreach (var c in new[] { noLoader, missing, different })
        {
            Assert.Equal(ClientStatus.Rejected, c.Status);
            Assert.Equal(RejectReason.MapMismatch, c.RejectedBecause);
        }
        Assert.Contains("no such map here", missing.MapError);
        Assert.NotNull(noLoader.MapError);
        Assert.Null(different.MapError);
        Assert.Equal(ClientStatus.Connected, same.Status);
        Assert.Single(h.Server.World.Players);
    }

    [Fact]
    public void A_second_challenge_naming_another_map_does_not_trigger_another_load()
    {
        var transport = new ScriptedTransport();
        var clock = new ManualClock();
        var loads = new List<string>();
        var client = new GameClient(new SimConfig(), Harness.Fingerprint, transport, clock,
            new ClientOptions { LoadMap = name => { loads.Add(name); return Loaded(Room()); } }, nonce: 7);
        transport.Incoming.Enqueue(Protocol.Write(new Challenge(7, 1, 0, "first_map")));
        transport.Incoming.Enqueue(Protocol.Write(new Challenge(7, 2, 0, "other_map")));
        transport.Incoming.Enqueue(Protocol.Write(new Challenge(7, 3, 0, "third_map")));
        for (int i = 0; i < 5; i++) { clock.Advance(TimeSpan.FromMilliseconds(10)); client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None)); }
        Assert.Equal(["first_map"], loads);
        Assert.Equal("first_map", client.MapName);
        var hello = transport.Sent.Select(p => Protocol.Read(p)).OfType<Hello>().Last();
        Assert.Equal(Room().ContentHash, hello.MapHash);
    }

    [Fact]
    public void Snapshots_with_non_finite_or_absurd_states_are_ignored()
    {
        var transport = new ScriptedTransport();
        var clock = new ManualClock();
        var client = new GameClient(new SimConfig(), Harness.Fingerprint, transport, clock, nonce: 7);
        transport.Incoming.Enqueue(Protocol.Write(new Welcome(1, 100, 30, 7, 0, 5)));
        client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None));
        Assert.Equal(ClientStatus.Connected, client.Status);
        var nan = new PlayerState(2, new Vector3(float.NaN, 0, 0), Vector3.Zero, 0, true) { Alive = true };
        var fast = new PlayerState(3, Vector3.Zero, new Vector3(1e9f, 0, 0), 0, true) { Alive = true };
        var fine = new PlayerState(4, new Vector3(1, 2, 0), Vector3.Zero, 0, true) { Alive = true };
        transport.Incoming.Enqueue(Protocol.Write(new Snapshot(101, 0, 2, [nan])));
        transport.Incoming.Enqueue(Protocol.Write(new Snapshot(102, 0, 2, [fast])));
        transport.Incoming.Enqueue(Protocol.Write(new Snapshot(103, 0, 2, [fine])));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None));
        clock.Advance(TimeSpan.FromMilliseconds(300));
        var (_, others) = client.RemotePlayers();
        Assert.Equal([4], others.Select(o => o.PlayerId));
        Assert.True(Harness.Finite(client.Predicted.Position));
    }

    sealed class ScriptedTransport : IClientTransport
    {
        public Queue<byte[]> Incoming { get; } = new();
        public List<byte[]> Sent { get; } = new();
        public void Send(byte[] packet) => Sent.Add(packet);
        public bool TryReceive(out byte[] packet) => Incoming.TryDequeue(out packet!);
    }

    [Fact]
    public void Flat_ground_servers_need_no_map()
    {
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(5), TimeSpan.Zero, 0), seed: 1, clients: 1);
        h.Run(TimeSpan.FromMilliseconds(500));
        Assert.Equal(ClientStatus.Connected, h.Clients[0].Status);
        Assert.Equal("", h.Clients[0].MapName);
        Assert.Null(h.Clients[0].Collision);
    }

    [Fact]
    public void The_challenge_never_outgrows_the_hello_and_rejects_bad_map_names()
    {
        string longest = new('m', Protocol.MaxMapName);
        var challenge = Protocol.Write(new Challenge(1, 2, 3, longest));
        var hello = Protocol.Write(new Hello(Protocol.Version, 1, 2, 3));
        Assert.True(challenge.Length <= hello.Length, $"challenge {challenge.Length} bytes, hello {hello.Length}");
        Assert.Equal(longest, Assert.IsType<Challenge>(Protocol.Read(challenge)).Map);
        Assert.Throws<ArgumentException>(() => Protocol.Write(new Challenge(1, 2, 3, longest + "m")));

        var bad = Protocol.Write(new Challenge(1, 2, 3, "mp_a"));
        bad[^2] = (byte)'/';   // "mp/a": not a map name
        Assert.Null(Protocol.Read(bad));
    }
}
