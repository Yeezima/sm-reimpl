using System.Numerics;
using SmServer;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The war's generated map (TF1): the arena and the Stormbird as solids, the hash both ends compare, the enemies' graph over the arena alone, the wire's box, and a Raptor's full jump
/// staying inside. The pack is the install-shaped synthetic one (<see cref="JumpPackDashTests"/>) with the owner's hold tiers and air control; no original bytes.
/// </summary>
public class TfwMapTests
{
    const int Assault = 0;
    const float Half = TfwMap.ArenaHalf;

    /// <summary>The Raptor's rules as the install's: the hold tiers and the owner's 8 m/s, 15 m/s² air control (spec "Jump-pack feel").</summary>
    static readonly SimConfig Raptor = JumpPackDashTests.Config with
    {
        JumpPacks = [JumpPackDashTests.Config.JumpPacks[0] with { HoldTiers = true, AirSpeed = 8f, AirAcceleration = 15f }],
    };

    static readonly TfwMapData Map = TfwMap.Build();
    static CollisionWorld World => Map.Collision;
    static GameMap ServerMap(out ulong hash)
    {
        string? name = null;
        var map = Assert.IsType<GameMap>(GameServer.GeneratedMap(ContentExtras.Musou, ref name, out _));
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
        ulong client = TfwMap.Build().Hash;   // what NetworkView.TfwLoadMap answers with
        Assert.Equal(server, client);
        Assert.Equal(PinnedHash, client);   // a geometry change: raise TfwMap.Version and take the new value
        Assert.Equal(World.Character.ContentHash, World.Weapons.ContentHash);   // shots meet what walkers meet
    }

    const ulong PinnedHash = 5922244319279579540UL;

    // ---------------------------------------------------------------- the solids

    [Fact]
    public void The_arena_is_an_empty_square_as_large_as_manufactorum_with_a_floor_at_zero()
    {
        var eye = new Vector3(0f, 0f, 1f);
        Assert.Equal(Half, Distance(eye, Vector3.UnitX), 3);
        Assert.Equal(Half, Distance(eye, -Vector3.UnitX), 3);
        Assert.Equal(Half, Distance(eye, Vector3.UnitY), 3);
        Assert.Equal(Half, Distance(eye, -Vector3.UnitY), 3);
        Assert.Equal(1f, Distance(eye, -Vector3.UnitZ), 3);
        Assert.Null(World.Character.Raycast(eye, Vector3.UnitZ, 400f));   // open above
        float area = (2f * Half) * (2f * Half);
        Assert.InRange(area / (210f * 280f), 0.95f, 1.05f);   // Manufactorum's footprint, about 210 x 280 m
        foreach (var solid in TfwMap.Solids.Where(s => !s.Stormbird))
            Assert.True(solid.Max.Z <= TfwMap.WallHeight);
    }

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
        var inside = TfwMap.StormbirdCentre + new Vector3(0f, 0f, 2f);
        Assert.InRange(Distance(inside, Vector3.Normalize(new Vector3(x, y, z))), 0.1f, 9f);
    }

    [Fact]
    public void The_stormbird_interior_is_10_by_10_by_5()
    {
        var c = TfwMap.StormbirdCentre;
        var inside = c + new Vector3(0f, 0f, 2f);
        Assert.Equal(10f, Distance(inside, Vector3.UnitX) + Distance(inside, -Vector3.UnitX), 3);
        Assert.Equal(10f, Distance(inside, Vector3.UnitY) + Distance(inside, -Vector3.UnitY), 3);
        Assert.Equal(5f, Distance(inside, Vector3.UnitZ) + Distance(inside, -Vector3.UnitZ), 3);
        Assert.All(TfwMap.Spawns, s => Assert.True(MathF.Abs(s.Position.X - c.X) + 1f < 5f && MathF.Abs(s.Position.Y - c.Y) + 1f < 5f, "a capsule fits between a spawn and the walls"));
        Assert.Equal(-TfwMap.ArenaHalf - 74f, c.Y + TfwMap.StormbirdSize.Y / 2f + TfwMap.WallThickness);   // apart from the arena's wall: a walker cannot cross
    }

    [Fact]
    public void Every_triangle_faces_out_of_its_box()
    {
        foreach (var solid in TfwMap.Solids)
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
            var nav = GameServer.BuildTfwNav(map, config, out _, dir);
            float cell = nav.Settings.CellSize;
            Assert.True(nav.NodeCount * cell * cell >= 0.98f * (2f * Half) * (2f * Half), $"{nav.NodeCount} nodes");
            for (int n = 0; n < nav.NodeCount; n++)
            {
                var p = nav.Position(n);
                Assert.True(MathF.Abs(p.X) < Half && MathF.Abs(p.Y) < Half, $"node {n} at {p}");
                Assert.Equal(0f, p.Z, 3);
            }
            foreach (var at in new[] { TfwMap.Position1, TfwMap.Position2, TfwMap.Position3, TfwMap.Hq }.Concat(TfwMap.Sources))
            {
                var path = nav.FindPath(TfwMap.LandingZone, at);
                Assert.False(Assert.IsType<NavPath>(path).Partial, $"to {at}");
            }
            Assert.Single(Directory.GetFiles(dir, $"{TfwMap.Name}{GameServer.MusouNavCacheSuffix}-*.nav"));
            var again = GameServer.BuildTfwNav(map, config, out _, dir);
            Assert.Equal(nav.NodeCount, again.NodeCount);
            Assert.Equal(0, again.BuildMilliseconds);   // read, not built
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_box_holds_the_stormbird_and_every_place_of_the_battle()
    {
        var sim = new MusouSim(null);
        sim.WidenBox(TfwMap.WireBox);
        var places = new[] { TfwMap.LandingZone, TfwMap.Position1, TfwMap.Position2, TfwMap.Position3, TfwMap.Hq, TfwMap.StormbirdCentre, TfwMap.DestinationStation, TfwMap.EquipmentStation }
            .Concat(TfwMap.Sources).Concat(TfwMap.Spawns.Select(s => s.Position));
        foreach (var p in places)
            Assert.True(Vector3.Min(p, sim.Box.Min) == sim.Box.Min && Vector3.Max(p, sim.Box.Max) == sim.Box.Max, $"{p} outside {sim.Box}");
        Assert.True(sim.Box.Max.Z >= TfwMap.WallHeight);
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
        h.AddClient(nonce: 1, options: new ClientOptions { LoadMap = _ => { var c = TfwMap.Build(); return new LoadedMap(c.Collision, c.Hash); } });
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Connected, h.Clients[0].Status);   // the client's own build of the map passes the hash check
        var sim = Assert.IsType<MusouSim>(h.Server.World.Musou);
        Assert.NotNull(sim.Nav);
        Assert.True(sim.Box.Min.Y < TfwMap.StormbirdCentre.Y - 5f);
        Assert.Contains("nodes", h.Server.MusouNote);
        var p = Assert.Single(h.Server.World.Players.Values).Position;
        Assert.True(MathF.Abs(p.X) < 5f && MathF.Abs(p.Y - TfwMap.StormbirdCentre.Y) < 5f, $"spawned at {p}");
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

    /// <summary>The map with its walls cut down to <paramref name="height"/>.</summary>
    static CollisionWorld WallsOf(float height)
    {
        var corners = TfwMap.Solids.Select(s => s with { Max = s.Max with { Z = MathF.Min(s.Max.Z, height) } }).SelectMany(TfwMap.Triangles).ToArray();
        var mesh = new StaticMesh(corners, Enumerable.Range(0, corners.Length).ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    [Fact]
    public void A_full_jump_clears_the_designs_12_m_wall_which_is_why_the_wall_is_higher()
    {
        float apex = FullJump(null, Vector3.Zero, Vector2.Zero).Max(s => s.Position.Z);
        Assert.InRange(apex, 17f, 18f);
        Assert.True(TfwMap.WallHeight > apex + 2f, "the wall stands clear above the highest feet");
        var over = FullJump(WallsOf(12f), new Vector3(118f, 0f, 0f), new Vector2(1f, 0f));
        Assert.Contains(over, s => s.Position.X > Half + TfwMap.WallThickness || s.Position.Z >= 12f && s.Grounded);   // over it, or on top of it
    }

    [Theory]
    [InlineData(118f, 0f, 1f, 0f)]
    [InlineData(-118f, 0f, -1f, 0f)]
    [InlineData(0f, 118f, 0f, 1f)]
    [InlineData(0f, -118f, 0f, -1f)]
    [InlineData(118f, 118f, 0.7071f, 0.7071f)]
    [InlineData(-118f, -118f, -0.7071f, -0.7071f)]
    public void A_full_jump_at_a_wall_stays_in_the_arena(float x, float y, float stickX, float stickY)
    {
        var states = FullJump(World, new Vector3(x, y, 0f), new Vector2(stickX, stickY));
        Assert.Contains(states, s => s.Position.Z > 15f);   // it flew
        Assert.All(states, s => Assert.True(MathF.Abs(s.Position.X) < Half && MathF.Abs(s.Position.Y) < Half && s.Position.Z < TfwMap.WallHeight, $"{s.Position}"));
        Assert.Equal(0f, states[^1].Position.Z, 2);   // back on the arena's floor
    }

    [Fact]
    public void A_full_jump_in_the_stormbird_stays_under_its_ceiling()
    {
        var c = TfwMap.StormbirdCentre;
        var states = FullJump(World, c, new Vector2(0f, 1f));
        Assert.All(states, s => Assert.True(s.Position.Z + Raptor.CapsuleHeight <= TfwMap.StormbirdSize.Z + 0.05f && s.Position.Y < c.Y + 5f, $"{s.Position}"));
    }
}
