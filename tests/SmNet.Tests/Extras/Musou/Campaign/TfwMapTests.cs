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

    const ulong PinnedHash = 6980734556804054000UL;

    // ---------------------------------------------------------------- the solids

    /// <summary>A point inside the corridor, 1.5 m up, at the model's <paramref name="x"/> and <paramref name="y"/>.</summary>
    static Vector3 Inside(float x, float y, float up = 1.5f) => TfwTestArena.Hub.OnFloor(new Vector2(x, y)) + new Vector3(0f, 0f, up);

    [Fact]
    public void The_hub_is_closed_on_every_side_from_every_place_inside()
    {
        // A sweep of rays from a grid of places in the corridor, on the ramp and above the stairs: every one meets a surface well within the ship (no gap between solids, no way out).
        var directions = new List<Vector3>();
        for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++) if (x != 0 || y != 0 || z != 0) directions.Add(Vector3.Normalize(new Vector3(x, y, z)));
        for (int k = 0; k < 24; k++) directions.Add(new Vector3(MathF.Cos(k * MathF.Tau / 24f), MathF.Sin(k * MathF.Tau / 24f), 0.1f));
        int rays = 0;
        foreach (float y in new[] { -4.5f, -2f, 2f, 4f, 6f, 9f, 12f, 16f, 20f, 21.5f, 24f, 26f, 27f })
            foreach (float x in new[] { -1.4f, 0f, 1.4f })
                foreach (float up in new[] { 1.5f, 3.2f })
                {
                    float baseZ = y < 3f ? TfwShip.DeckZ - TfwShip.FloorZ : 0f;
                    var from = Inside(x, y, baseZ + up);
                    foreach (var d in directions)
                    {
                        var hit = World.Character.Raycast(from, d, 80f);
                        Assert.True(hit is RayHit, $"a ray from model ({x}, {y}, up {up}) along {d} leaves the ship");
                        rays++;
                    }
                }
        Assert.True(rays > 3000);
    }

    [Fact]
    public void The_corridor_is_as_measured_and_lies_clear_of_the_level()
    {
        var hub = TfwTestArena.Hub;
        var mid = Inside(0f, TfwShip.MidY, 1f);
        Assert.Equal(TfwShip.RightPanel - TfwShip.LeftPanel, Distance(mid, Vector3.UnitX) + Distance(mid, -Vector3.UnitX), 3);   // between the tech panels
        Assert.Equal(TfwShip.EndWallY - TfwShip.MidY, Distance(mid, -Vector3.UnitY), 3);                                         // to the end wall
        Assert.Equal(TfwShip.MidY - TfwShip.FrontY, Distance(mid + new Vector3(0f, 0f, 2f), Vector3.UnitY), 3);               // to the cockpit end, above the deck
        Assert.Equal(1f, Distance(mid, -Vector3.UnitZ), 3);                                                                       // the floor is at the centre's height
        Assert.InRange(Distance(mid, Vector3.UnitZ), 2.5f, 3.5f);                                                                 // the roof, sloping toward the ramp
        // Apart from the level: a walker cannot cross, and the north end of the ship is the clearance short of the level's edge.
        Assert.Equal(-TfwTestArena.ArenaHalf - TfwMap.WallThickness - TfwMap.HubClearance, hub.Centre.Y + MathF.Ceiling(TfwShip.Fore));
        Assert.All(TfwTestArena.Spawns, s => Assert.True(hub.Contains(s.Position) && MathF.Abs(s.Position.X - hub.Centre.X) + TfwShip.HalfWidth - 2f < 3f, "a spawn is aboard, clear of the walls"));
        Assert.True(hub.Contains(hub.DestinationStation) && hub.Contains(hub.EquipmentStation));
        Assert.False(hub.Contains(hub.Centre + new Vector3(0f, TfwShip.Fore + 0.5f, 0f)));
        Assert.False(hub.Contains(hub.Centre + new Vector3(TfwShip.HalfWidth + 0.5f, 0f, 0f)));
    }

    [Fact]
    public void Every_triangle_faces_out_of_its_solid()
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
        var prism = TfwShip.Prism(-1f, 1f, [(0f, 0f), (2f, 0.5f), (2f, 3f), (0f, 3f)]);
        var centre = prism.Aggregate(Vector3.Zero, (a, b) => a + b) / prism.Length;
        for (int t = 0; t < prism.Length; t += 3)
            Assert.True(Vector3.Dot(Vector3.Cross(prism[t + 1] - prism[t], prism[t + 2] - prism[t]), prism[t] - centre) > 0f, $"prism triangle {t / 3}");
    }

    [Fact]
    public void The_open_door_lies_flat_at_the_floor_and_reaches_about_four_and_a_half_metres_past_it()
    {
        // The measured corners of the closed door (block 0: the hinge's line at the floor, the top edge 4.5 m up and 3.4 m back): posed open, the top edge lies in the floor's plane.
        var hinge = TfwShip.DoorHinge;
        var topEdge = new Vector3(0f, 25.6f, 6.3f);
        var open = Vector3.Transform(topEdge, TfwShip.DoorOpen);
        Assert.Equal(hinge.Z, open.Z, 0.25f);
        Assert.InRange(open.Y - hinge.Y, 5f, 6f);
        Assert.Equal(topEdge.X, open.X, 4);
        Assert.True(Vector3.Distance(hinge, Vector3.Transform(hinge, TfwShip.DoorOpen)) < 1e-3f);   // the hinge stays
        Assert.InRange(TfwShip.RampEndY - TfwShip.HingeY, 5f, 5.6f);
    }

    // ---------------------------------------------------------------- walking in the hub

    static PlayerState Walk(PlayerState s, float forward, float side, int ticks, Action<PlayerState>? each = null, InputButtons buttons = 0, float yaw = TfwShip.RampYaw)
    {
        for (int k = 0; k < ticks; k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, side, forward, yaw, buttons, 0f), Raptor, out _, World);
            each?.Invoke(s);
        }
        return s;
    }

    /// <summary>The model's Y of a world position (the ship is turned half a circle).</summary>
    static float ModelY(Vector3 p) => TfwTestArena.Hub.Centre.Y + TfwShip.MidY - p.Y;

    static PlayerState StartAt(Vector3 p) => Combat.Respawned(new PlayerState(1, p, default, 0f, true) { Class = Assault }, Raptor);

    [Fact]
    public void A_walker_goes_to_the_end_wall_and_no_further()
    {
        var hub = TfwTestArena.Hub;
        var s = Walk(StartAt(hub.Spawns[0].Position), 1f, 0f, 900, p => Assert.True(hub.Contains(p.Position), $"{p.Position}"));
        Assert.InRange(ModelY(s.Position), TfwShip.EndWallY - 1f, TfwShip.EndWallY);   // stopped by the end wall at the ramp's top
        Assert.InRange(s.Position.Z - hub.Centre.Z, -0.05f, 0.1f);   // still on the corridor's floor, never on the ramp
    }

    [Fact]
    public void The_stairs_can_be_walked_down_from_the_deck_and_up_again()
    {
        var hub = TfwTestArena.Hub;
        float deck = TfwShip.DeckZ - TfwShip.FloorZ;
        var s = StartAt(hub.OnFloor(new Vector2(0f, -3f)) + new Vector3(0f, 0f, deck));
        for (int k = 0; k < 100 && ModelY(s.Position) < TfwShip.StairsEndY + 1f; k++) s = Walk(s, 1f, 0f, 5);
        Assert.Equal(0f, s.Position.Z - hub.Centre.Z, 0.05f);   // down on the corridor's floor
        Assert.True(ModelY(s.Position) > TfwShip.StairsEndY, $"{s.Position}");
        s = Walk(s, -1f, 0f, 300);   // backwards, up the stairs
        Assert.Equal(deck, s.Position.Z - hub.Centre.Z, 0.05f);   // and up to the deck again
        Assert.True(ModelY(s.Position) < TfwShip.DeckEndY, $"{s.Position}");
    }

    [Fact]
    public void No_scripted_walk_leaves_the_ship_or_gets_under_the_floor_or_over_the_cap()
    {
        var hub = TfwTestArena.Hub;
        var rng = new Random(4);
        int steps = 0;
        foreach (var start in new[] { hub.Spawns[0].Position, hub.Spawns[3].Position, hub.DestinationStation, hub.EquipmentStation })
        {
            for (int trial = 0; trial < 12; trial++)
            {
                var s = StartAt(start);
                for (int leg = 0; leg < 14; leg++)
                {
                    float yaw = (float)(rng.NextDouble() * MathF.Tau);
                    var buttons = (rng.Next(3) == 0 ? InputButtons.Sprint : 0) | (rng.Next(4) == 0 ? InputButtons.Jump : 0);
                    s = Walk(s, (float)rng.NextDouble() * 2f - 0.5f, (float)rng.NextDouble() * 2f - 1f, 40 + rng.Next(80), p =>
                    {
                        steps++;
                        Assert.True(hub.Contains(p.Position), $"left the ship at {p.Position}");
                        Assert.InRange(p.Position.Z - hub.Centre.Z, -0.05f, TfwShip.Height);
                    }, buttons, yaw);
                }
            }
        }
        Assert.True(steps > 20000);
    }

    [Fact]
    public void A_raptors_full_jump_in_the_corridor_stays_under_the_roof_and_one_at_the_end_wall_never_reaches_the_ramp()
    {
        var hub = TfwTestArena.Hub;
        var roof = FullJump(World, hub.Centre, new Vector2(0f, -1f));
        Assert.All(roof, s => Assert.True(s.Position.Z - hub.Centre.Z + Raptor.CapsuleHeight <= 4.1f && hub.Contains(s.Position), $"{s.Position}"));
        // Jump packs and charges at the end wall (the ramp's top), the stick both ways: none passes it, and one of the two does press against it.
        float nearest = float.MaxValue;
        foreach (float dir in new[] { -1f, 1f })
            foreach (float y in new[] { 20.5f, 21.5f })
            {
                var jump = FullJump(World, hub.OnFloor(new Vector2(0f, y)), new Vector2(0f, dir));
                Assert.All(jump, s => Assert.True(ModelY(s.Position) < TfwShip.EndWallY && hub.Contains(s.Position), $"{s.Position}"));
                nearest = MathF.Min(nearest, TfwShip.EndWallY - jump.Max(s => ModelY(s.Position)));
            }
        Assert.InRange(nearest, 0f, 1.2f);
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
            var range = Assert.IsType<TfwRange>(TfwTestArena.Field.Range);   // the Firing Range is the graph's second, separate part
            int arenaNodes = 0;
            for (int n = 0; n < nav.NodeCount; n++) if (!range.Contains(nav.Position(n))) arenaNodes++;
            Assert.True(arenaNodes * cell * cell >= 0.98f * (2f * Half) * (2f * Half), $"{arenaNodes} nodes");
            Assert.True(nav.NodeCount - arenaNodes > 1000, "the range has its own nodes");
            for (int n = 0; n < nav.NodeCount; n++)
            {
                var p = nav.Position(n);
                if (range.Contains(p)) continue;
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
}
