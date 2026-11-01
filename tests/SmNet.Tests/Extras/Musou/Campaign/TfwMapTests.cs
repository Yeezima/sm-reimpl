using System.Numerics;
using SmServer;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The war's composed map on a synthetic level (TF1, M1): the Stormbird's solids, the hash both ends compare, the enemies' graph over the level alone, the wire's box, and a Raptor's full jump
/// staying inside the room. The pack is the install-shaped synthetic one (<see cref="JumpPackDashTests"/>) with the owner's hold tiers and air control; no original bytes.
/// </summary>
public class TfwMapTests
{
    const int Assault = 0;
    const float Half = TfwTestArena.ArenaHalf;

    /// <summary>The Raptor's rules as the install's: the hold tiers and the owner's 8 m/s, 15 m/sÂ² air control (spec "Jump-pack feel").</summary>
    static readonly SimConfig Raptor = JumpPackDashTests.Config with
    {
        JumpPacks = [JumpPackDashTests.Config.JumpPacks[0] with { HoldTiers = true, AirSpeed = 8f, AirAcceleration = 15f }],
    };

    static readonly TfwMapData Map = TfwTestArena.Build();
    static CollisionWorld World => Map.Collision;
    static GameMap ServerMap(out ulong hash)
    {
        var map = TfwTestArena.ServerMap();
        hash = map.Hash;
        return map;
    }

    static float Distance(Vector3 from, Vector3 direction, float reach = 400f) =>
        Assert.IsType<RayHit>(World.Character.Raycast(from, direction, reach)).Distance;

    // ---------------------------------------------------------------- the hash

    [Fact]
    public void The_server_and_the_client_hash_the_same_map_and_a_change_moves_the_pin()
    {
        ServerMap(out ulong server);   // what GameServer.GeneratedMap offers
        ulong client = TfwMap.WithHub(TfwTestArena.Level()).Hash;   // what NetworkView.TfwMapCollision answers with, from the level alone
        Assert.Equal(server, client);
        Assert.Equal(PinnedHash, client);   // a geometry change: raise TfwMap.Version and take the new value
        Assert.Equal(World.Character.ContentHash, World.Weapons.ContentHash);   // shots meet what walkers meet
    }

    const ulong PinnedHash = 16110256361378542445UL;

    // ---------------------------------------------------------------- the solids

    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(-1f, 0f, 0f)]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, -1f, 0f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(0f, 0f, -1f)]
    [InlineData(1f, 1f, 0.3f)]
    [InlineData(-1f, -1f, 0.3f)]
    public void The_stormbird_is_closed_on_every_side(float x, float y, float z)
    {
        var inside = TfwTestArena.Hub.Centre + new Vector3(0f, 0f, 2f);
        Assert.InRange(Distance(inside, Vector3.Normalize(new Vector3(x, y, z))), 0.1f, 9f);
    }

    [Fact]
    public void The_stormbird_interior_is_10_by_10_by_5()
    {
        var c = TfwTestArena.Hub.Centre;
        var inside = c + new Vector3(0f, 0f, 2f);
        Assert.Equal(10f, Distance(inside, Vector3.UnitX) + Distance(inside, -Vector3.UnitX), 3);
        Assert.Equal(10f, Distance(inside, Vector3.UnitY) + Distance(inside, -Vector3.UnitY), 3);
        Assert.Equal(5f, Distance(inside, Vector3.UnitZ) + Distance(inside, -Vector3.UnitZ), 3);
        Assert.All(TfwTestArena.Spawns, s => Assert.True(MathF.Abs(s.Position.X - c.X) + 1f < 5f && MathF.Abs(s.Position.Y - c.Y) + 1f < 5f, "a capsule fits between a spawn and the walls"));
        Assert.Equal(-TfwTestArena.ArenaHalf - TfwMap.WallThickness - TfwMap.HubClearance, c.Y + TfwMap.StormbirdSize.Y / 2f + TfwMap.WallThickness);   // apart from the level: a walker cannot cross
    }

    [Fact]
    public void Every_triangle_faces_out_of_its_box()
    {
        foreach (var solid in TfwTestArena.Hub.Solids)
        {
            var mid = (solid.Min + solid.Max) / 2f;
            var corners = TfwMap.Triangles(solid);
            Assert.Equal(36, corners.Length);
            for (int t = 0; t < 36; t += 3)
            {
                var normal = Vector3.Cross(corners[t + 1] - corners[t], corners[t + 2] - corners[t]);
                Assert.True(Vector3.Dot(normal, corners[t] - mid) > 0f, $"{solid} triangle {t / 3}");
            }
        }
    }

    // ---------------------------------------------------------------- the enemies' graph

    static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-tfw-nav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void The_graph_covers_the_arena_and_nothing_else_and_comes_from_its_cache_file()
    {
        string dir = TempDir();
        try
        {
            var map = ServerMap(out _);
            var config = new SimConfig { Extras = ContentExtras.Musou };
            var nav = GameServer.BuildTfwNav(map, TfwTestArena.Field, config, out _, dir);
            float cell = nav.Settings.CellSize;
            Assert.True(nav.NodeCount * cell * cell >= 0.98f * (2f * Half) * (2f * Half), $"{nav.NodeCount} nodes");
            for (int n = 0; n < nav.NodeCount; n++)
            {
                var p = nav.Position(n);
                Assert.True(MathF.Abs(p.X) < Half && MathF.Abs(p.Y) < Half, $"node {n} at {p}");
                Assert.Equal(0f, p.Z, 3);
            }
            foreach (var at in new[] { TfwTestArena.Position1, TfwTestArena.Position2, TfwTestArena.Position3, TfwTestArena.Hq }.Concat(TfwTestArena.Sources))
            {
                var path = nav.FindPath(TfwTestArena.LandingZone, at);
                Assert.False(Assert.IsType<NavPath>(path).Partial, $"to {at}");
            }
            Assert.Single(Directory.GetFiles(dir, $"{TfwMap.Name}{GameServer.MusouNavCacheSuffix}-*.nav"));
            var again = GameServer.BuildTfwNav(map, TfwTestArena.Field, config, out _, dir);
            Assert.Equal(nav.NodeCount, again.NodeCount);
            Assert.Equal(0, again.BuildMilliseconds);   // read, not built
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_box_holds_the_stormbird_and_every_place_of_the_battle()
    {
        var sim = new MusouSim(null);
        sim.WidenBox(TfwTestArena.Hub.Box);
        sim.WidenBox(TfwTestArena.Field.Bounds);
        var places = new[] { TfwTestArena.LandingZone, TfwTestArena.Position1, TfwTestArena.Position2, TfwTestArena.Position3, TfwTestArena.Hq, TfwTestArena.Hub.Centre, TfwTestArena.Hub.DestinationStation, TfwTestArena.Hub.EquipmentStation }
            .Concat(TfwTestArena.Sources).Concat(TfwTestArena.Spawns.Select(s => s.Position));
        foreach (var p in places)
            Assert.True(Vector3.Min(p, sim.Box.Min) == sim.Box.Min && Vector3.Max(p, sim.Box.Max) == sim.Box.Max, $"{p} outside {sim.Box}");
        Assert.True(sim.Box.Max.Z >= TfwTestArena.WallHeight);   // the level's top, for a jump
    }

    [Fact]
    public void A_server_runs_the_war_on_the_arenas_graph_with_the_wider_box_and_a_join_lands_in_the_stormbird()
    {
        var map = ServerMap(out ulong hash);
        var config = new SimConfig
        {
            Weapons = [new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f)],
            Classes = [new ClassLoadout("sm - tactical", -1, 0, SimConfig.PlaceholderMeleeSteps) { PistolChoices = [0], Side = Team.SpaceMarines, Role = "tactical" }],
            Extras = ContentExtras.Musou,
        };
        var h = new Harness(LinkConditions.Perfect, 5, 0, config, new ServerOptions { Match = new MatchRules { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } }, map: map);
        h.AddClient(nonce: 1, options: new ClientOptions { LoadMap = _ => { var c = TfwTestArena.Build(); return new LoadedMap(c.Collision, c.Hash); } });
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Connected, h.Clients[0].Status);   // the client's own build of the map passes the hash check
        var sim = Assert.IsType<MusouSim>(h.Server.World.Musou);
        Assert.NotNull(sim.Nav);
        Assert.True(sim.Box.Min.Y < TfwTestArena.Hub.Centre.Y - 5f);
        Assert.Contains("nodes", h.Server.MusouNote);
        var p = Assert.Single(h.Server.World.Players.Values).Position;
        Assert.True(MathF.Abs(p.X) < 5f && MathF.Abs(p.Y - TfwTestArena.Hub.Centre.Y) < 5f, $"spawned at {p}");
        Assert.Equal(hash, map.Hash);
    }

    // ---------------------------------------------------------------- a Raptor's full jump

    /// <summary>A full jump from <paramref name="from"/> with the stick toward <paramref name="toward"/>, held until the landing: every state.</summary>
    static List<PlayerState> FullJump(CollisionWorld? world, Vector3 from, Vector2 toward)
    {
        var s = Combat.Respawned(new PlayerState(1, from, default, 0f, true) { Class = Assault }, Raptor);
        var states = new List<PlayerState>();
        // The stick in the view's frame at yaw 0: X right, Y forward.
        for (int k = 0; k < 300; k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, toward.X, toward.Y, 0f, InputButtons.Jump, 0f), Raptor, out _, world);
            states.Add(s);
            if (k > 20 && s.Jump is JumpPhase.Landed or JumpPhase.Recover) break;
        }
        return states;
    }

    [Fact]
    public void A_full_jump_in_the_stormbird_stays_under_its_ceiling()
    {
        var c = TfwTestArena.Hub.Centre;
        var states = FullJump(World, c, new Vector2(0f, 1f));
        Assert.All(states, s => Assert.True(s.Position.Z + Raptor.CapsuleHeight <= TfwMap.StormbirdSize.Z + 0.05f && s.Position.Y < c.Y + 5f, $"{s.Position}"));
    }
}
