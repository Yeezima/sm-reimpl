using System.Numerics;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// The spawn-room blockers as walls: a side's blocker is solid for the other side's players (walking, sprinting, sliding,
/// the evade roll and hop, jump-pack flight, the ground pound's dive, falling, any speed) and not for its own side or for
/// players with no side; shots pass through; the side's mesh keeps the level's triangles exact; prediction stays exact over
/// the network with players pressing against blockers. Synthetic level and specs only (no game data).
/// </summary>
public class SpawnBlockerTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;
    static readonly SimConfig Config = EvadeTests.Config;
    static float R => Config.CapsuleRadius;
    const int Assault = 0, Tactical = 1, Heavy = 3;

    /// <summary>A marine blocker across the X axis at x 0: 1 m thick, 24 m wide, 40 m tall (bottom 1 m under the floor).</summary>
    static readonly SpawnBlocker MarineWall = new(Team.SpaceMarines, new Vector3(0, 0, 19), new Vector3(1, 24, 40), 0f);

    static (Vector3, Vector3, Vector3, Vector3) Floor(float z, float x0 = -60, float x1 = 60, float y0 = -60, float y1 = 60) =>
        (new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z));

    static (Vector3, Vector3, Vector3, Vector3) Wall(Vector2 a, Vector2 b, float height = 4f) =>
        (new(a.X, a.Y, -1f), new(b.X, b.Y, -1f), new(b.X, b.Y, height), new(a.X, a.Y, height));

    static StaticMesh Mesh(params (Vector3 A, Vector3 B, Vector3 C, Vector3 D)[] quads)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        foreach (var (a, b, c, d) in quads) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        return new StaticMesh(v.ToArray(), idx.ToArray());
    }

    static CollisionWorld Level(params SpawnBlocker[] blockers)
    {
        var mesh = Mesh(Floor(0f));
        return new CollisionWorld(mesh, mesh, null, blockers);
    }

    static PlayerState Fresh(Team side, int cls, Vector3 at, float yaw, SimConfig? c = null) =>
        Combat.Respawned(new PlayerState(1, at, Vector3.Zero, yaw, true) { Class = cls, Team = side }, c ?? Config);

    /// <summary>Yaw whose forward is the unit vector <paramref name="d"/> (yaw 0 faces +Y; forward (−sin, cos)).</summary>
    static float YawToward(Vector2 d) => MathF.Atan2(-d.X, d.Y);

    /// <summary>How far the capsule's axis is past the blocker's near face along its thin axis (≤ −R when held there).</summary>
    static float Along(SpawnBlocker b, Vector3 p)
    {
        var axis = new Vector2(MathF.Cos(b.Yaw), MathF.Sin(b.Yaw));
        return Vector2.Dot(new Vector2(p.X - b.Center.X, p.Y - b.Center.Y), axis) + b.Size.X / 2f;
    }

    // ---------------------------------------------------------------- walking

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0f, 30f)]
    [InlineData(0f, 60f)]
    [InlineData(45f, 0f)]
    [InlineData(90f, 20f)]
    [InlineData(-135f, 45f)]
    public void An_enemy_walking_or_sprinting_into_a_blocker_is_stopped_and_slides_along_it(float blockerDegrees, float approachDegrees)
    {
        // 100 m wide, so a slide along it never reaches its end.
        var blocker = MarineWall with { Center = new Vector3(3, -2, 19), Size = new Vector3(1, 100, 40), Yaw = blockerDegrees * MathF.PI / 180f };
        var level = Level(blocker);
        var axis = new Vector2(MathF.Cos(blocker.Yaw), MathF.Sin(blocker.Yaw));          // across the blocker, toward its far side
        var side = new Vector2(-axis.Y, axis.X);
        float a = approachDegrees * MathF.PI / 180f;
        var heading = axis * MathF.Cos(a) + side * MathF.Sin(a);
        var start = new Vector3(blocker.Center.X, blocker.Center.Y, 0f) - new Vector3(axis * 5f, 0f);
        float yaw = YawToward(heading);
        foreach (var team in new[] { Team.Chaos, Team.SpaceMarines, Team.None })
        {
            var s = Fresh(team, Tactical, start, yaw);
            float deepest = float.MinValue;
            for (int t = 0; t < 120; t++)
            {
                var buttons = t % 60 < 30 ? InputButtons.Sprint : InputButtons.None;
                s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, yaw, buttons), Config, out _, level);
                deepest = MathF.Max(deepest, Along(blocker, s.Position));
            }
            output.WriteLine($"{team}: end {s.Position}, deepest {deepest:F4} m past the near face");
            if (team == Team.Chaos)
            {
                Assert.True(deepest <= -R + 2e-3f, $"the enemy reached {deepest} m past the blocker's face");
                Assert.True(deepest >= -R - 0.05f, "the enemy never reached the blocker");
                if (approachDegrees > 0f) Assert.True(Vector2.Dot(new Vector2(s.Position.X - start.X, s.Position.Y - start.Y), side) > 5f, "no slide along the blocker");
            }
            else Assert.True(Along(blocker, s.Position) > blocker.Size.X + 2f, $"{team} did not pass through");
        }
    }

    [Fact]
    public void A_blocker_of_each_side_stops_only_the_other_side()
    {
        var chaosWall = new SpawnBlocker(Team.Chaos, new Vector3(0, 0, 19), new Vector3(1, 24, 40), 0f);
        var level = Level(MarineWall with { Center = new Vector3(-10, 0, 19) }, chaosWall);
        float east = YawToward(Vector2.UnitX), west = YawToward(-Vector2.UnitX);
        // Everyone starts between the marine blocker (x −10) and the Chaos blocker (x 0) and sprints east or west.
        var marine = Fresh(Team.SpaceMarines, Tactical, new Vector3(-5, 0, 0), east);
        var chaos = Fresh(Team.Chaos, Tactical, new Vector3(-5, 0, 0), west);
        var marineWest = Fresh(Team.SpaceMarines, Tactical, new Vector3(-5, 0, 0), west);
        var chaosEast = Fresh(Team.Chaos, Tactical, new Vector3(-5, 0, 0), east);
        for (int t = 0; t < 90; t++)
        {
            marine = Combat.StepPlayer(marine, new PlayerInput(0, 0, 1, east, InputButtons.Sprint), Config, out _, level);
            chaos = Combat.StepPlayer(chaos, new PlayerInput(0, 0, 1, west, InputButtons.Sprint), Config, out _, level);
            marineWest = Combat.StepPlayer(marineWest, new PlayerInput(0, 0, 1, west, InputButtons.Sprint), Config, out _, level);
            chaosEast = Combat.StepPlayer(chaosEast, new PlayerInput(0, 0, 1, east, InputButtons.Sprint), Config, out _, level);
        }
        Assert.True(marine.Position.X <= -0.5f - R + 2e-3f, $"marine at {marine.Position}");          // held by the Chaos blocker
        Assert.True(chaos.Position.X >= -9.5f + R - 2e-3f, $"chaos at {chaos.Position}");             // held by the marine blocker
        Assert.True(marineWest.Position.X < -12f, $"marine west at {marineWest.Position}");           // through its own
        Assert.True(chaosEast.Position.X > 2f, $"chaos east at {chaosEast.Position}");                // through its own
    }

    // ---------------------------------------------------------------- fast movement: roll, hop, glide, flight, dive, fall

    [Fact]
    public void Rolls_and_hops_into_a_blocker_stop_at_it_for_the_enemy_and_pass_for_its_own_side()
    {
        var level = Level(MarineWall);
        float east = YawToward(Vector2.UnitX);
        foreach (var (cls, config) in new[] { (Tactical, Config), (Heavy, EvadeTests.HopConfig) })
            foreach (var team in new[] { Team.Chaos, Team.SpaceMarines })
                foreach (float from in new[] { 1f, 2f, 4f, 6f, 8f })   // the roll (9 m) or hop (6 m) starts this far from the face
                    foreach (float slant in new[] { 0f, 0.5f })         // straight at it, or with the stick turned 27° off
                    {
                        float yaw = east;
                        var s = Fresh(team, cls, new Vector3(-0.5f - from, 0, 0), yaw, config);
                        float deepest = float.MinValue;
                        int evading = 0;
                        for (int t = 0; t < 45; t++)
                        {
                            var buttons = t == 0 ? InputButtons.Jump : InputButtons.None;
                            s = Combat.StepPlayer(s, new PlayerInput(0, slant, 1, yaw, buttons), config, out _, level);
                            if (s.EvadeTicks > 0) evading++;
                            deepest = MathF.Max(deepest, Along(MarineWall, s.Position));
                        }
                        output.WriteLine($"{config.Classes[cls].Name} {team} from {from} m, slant {slant}: {evading} evading ticks, deepest {deepest:F4}, end {s.Position}");
                        Assert.True(evading > 0, "no evade");
                        if (team == Team.Chaos) Assert.True(deepest <= -R + 2e-3f, $"{team} reached {deepest}");
                        else if (from <= 4f) Assert.True(deepest > 1.5f, $"{team} did not roll through its own blocker");   // within the move's reach
                        else Assert.True(deepest > -R, $"{team} stopped short of its own blocker");
                    }
    }

    [Theory]
    [InlineData(30f)]
    [InlineData(120f)]
    [InlineData(400f)]   // 13.3 m a tick; the motor sub-steps up to 64 × R/2 = 14.4 m a tick (a ground pound is 50 m/s here)
    public void Gliding_flying_and_falling_fast_do_not_tunnel_through(float speed)
    {
        var level = Level(MarineWall);
        float east = YawToward(Vector2.UnitX);
        foreach (var team in new[] { Team.Chaos, Team.SpaceMarines })
        {
            var glide = Fresh(team, Tactical, new Vector3(-3, 0, 0), east);
            var fly = Fresh(team, Tactical, new Vector3(-3, 0, 10), east) with { Grounded = false };
            var fall = Fresh(team, Tactical, new Vector3(-3, 0, 30), east) with { Grounded = false, Velocity = new Vector3(speed, 0, -speed) };
            for (int t = 0; t < 10; t++)
            {
                glide = Movement.Glide(glide, east, new Vector2(speed, 0f), Config, level);
                fly = Movement.Fly(fly, east, new Vector2(speed, 0f), 0f, 0f, Config, level);
                fall = Movement.Fly(fall, east, new Vector2(fall.Velocity.X, fall.Velocity.Y), fall.Velocity.Z, Config.Gravity, Config, level);
                if (team == Team.Chaos)
                    foreach (var (what, p) in new[] { ("glide", glide.Position), ("flight", fly.Position), ("fall", fall.Position) })
                        Assert.True(Along(MarineWall, p) <= -R + 2e-3f, $"{what} at {speed} m/s reached {p}");
            }
            if (team == Team.SpaceMarines)
            {
                Assert.True(glide.Position.X > 1f && fly.Position.X > 1f, $"own side held: {glide.Position}, {fly.Position}");
            }
        }
    }

    [Fact]
    public void Jump_pack_flights_and_ground_pound_dives_at_a_blocker_stop_at_it_for_the_enemy()
    {
        var level = Level(MarineWall);
        float east = YawToward(Vector2.UnitX);
        foreach (var team in new[] { Team.Chaos, Team.SpaceMarines })
        {
            var s = Fresh(team, Assault, new Vector3(-9, 0, 0), east);
            float deepest = float.MinValue, highest = 0f;
            int dives = 0, airborneAtWall = 0, divingAtWall = 0;
            bool crossed = false;
            for (int t = 0; t < 600; t++)
            {
                int phase = t % 75;
                var buttons = phase < 12 ? InputButtons.Jump : phase == 30 ? InputButtons.Melee : InputButtons.None;   // lift off, then dive
                // Every other flight starts by walking back 6 m, so the dives also come from the open floor.
                float my = (t / 75) % 2 == 1 && phase >= 50 ? -1f : 1f;
                var before = s.Jump;
                s = Combat.StepPlayer(s, new PlayerInput(0, 0, my, east, buttons, Pitch: -0.2f), Config, out _, level);
                if (s.Jump == JumpPhase.Dive && before != JumpPhase.Dive) dives++;
                float along = Along(MarineWall, s.Position);
                deepest = MathF.Max(deepest, along);
                highest = MathF.Max(highest, s.Position.Z);
                if (!s.Grounded && along > -R - 0.05f) { airborneAtWall++; if (s.Jump == JumpPhase.Dive) divingAtWall++; }
                if (s.Position.X > 1f) crossed = true;
            }
            output.WriteLine($"{team}: {dives} dives, {airborneAtWall} airborne ticks at the wall ({divingAtWall} diving), highest {highest:F1} m, deepest {deepest:F4}, end {s.Position}");
            if (team == Team.Chaos)
            {
                Assert.True(dives >= 2, "too few dives");
                Assert.True(airborneAtWall > 10, "the flights never reached the blocker");
                Assert.True(divingAtWall > 0, "no dive met the blocker");
                Assert.True(deepest <= -R + 2e-3f, $"reached {deepest} m past the face");
            }
            else Assert.True(crossed, "the marine never flew through its own blocker");
        }
    }

    [Theory]
    [InlineData(0f, 0f, 1f, 1f)]        // the centre: out across the +X face (ties go positive), then walks on east
    [InlineData(0.3f, 0f, -1f, 1f)]     // nearer the +X face: out there, then held there walking west
    [InlineData(-0.3f, 0f, 1f, -1f)]    // nearer the −X face: out there, then held there walking east
    [InlineData(0.2f, 11.8f, 1f, 2f)]   // nearer the +Y end (0.2 m) than the +X face (0.3 m): out past the end
    public void An_enemy_that_starts_inside_a_box_is_put_out_across_the_nearest_face_and_cannot_walk_back_in(float x, float y, float walkX, float side)
    {
        // Without the push-out the capsule controller keeps a capsule inside a box thicker than its diameter (1 m > 0.9 m):
        // every face it reaches pushes it back in.
        var level = Level(MarineWall);
        float yaw = YawToward(new Vector2(walkX, 0f));
        var s = Fresh(Team.Chaos, Tactical, new Vector3(x, y, 0), yaw);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0, 0, yaw, InputButtons.None), Config, out _, level);
        output.WriteLine($"from {x},{y}: after one tick {s.Position}");
        if (side == 2f) Assert.True(s.Position.Y >= 12f + R, $"not out past the end: {s.Position}");
        else Assert.True(side * s.Position.X >= 0.5f + R, $"not out across the {(side > 0 ? "+" : "−")}X face: {s.Position}");
        Assert.False(MarineWall.HorizontalDistance(s.Position) < R - 1e-3f, "still overlapping the box");
        for (int t = 0; t < 60; t++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0, 1, yaw, InputButtons.None), Config, out _, level);
            Assert.True(MarineWall.HorizontalDistance(s.Position) >= R - 2e-3f, $"tick {t}: back in the box at {s.Position}");
        }
        output.WriteLine($"  after 2 s walking {(walkX > 0 ? "east" : "west")}: {s.Position}");
        // The own side is never moved: a marine inside its own box stays where it is.
        var own = Fresh(Team.SpaceMarines, Tactical, new Vector3(x, y, 0), yaw);
        Assert.Equal(own.Position, level.OutOfBlockers(own, Config).Position);
    }

    // ---------------------------------------------------------------- shots, meshes, hashes

    [Fact]
    public void Shots_pass_through_blockers_and_only_the_other_sides_mesh_has_them()
    {
        var level = Level(MarineWall);
        var from = new Vector3(-5, 0, 1.5f);
        Assert.Null(level.Weapons.Raycast(from, Vector3.UnitX, 20f));
        Assert.Null(level.Character.Raycast(from, Vector3.UnitX, 20f));
        Assert.Null(level.CharacterFor(Team.SpaceMarines).Raycast(from, Vector3.UnitX, 20f));
        Assert.Null(level.CharacterFor(Team.None).Raycast(from, Vector3.UnitX, 20f));
        var hit = level.CharacterFor(Team.Chaos).Raycast(from, Vector3.UnitX, 20f);
        Assert.NotNull(hit);
        Assert.Equal(4.5f, hit!.Value.Distance, 4);
        Assert.Equal(-Vector3.UnitX, hit.Value.Normal);
        Assert.Equal(level.Character.TriangleCount + 12, level.CharacterFor(Team.Chaos).TriangleCount);
        Assert.Same(level.Character, level.CharacterFor(Team.SpaceMarines));   // no Chaos blockers: the level itself

        // A marine in its room shoots a Chaos player outside through the marine blocker: the hit lands.
        var rifle = new WeaponSpec("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 20f, 0.15f, 30, 270, 0.5f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
        var config = new SimConfig
        {
            Weapons = [rifle],
            Classes = [new ClassLoadout("sm", 0, -1, SimConfig.PlaceholderMeleeSteps) { Side = Team.SpaceMarines, Role = "t" }, new ClassLoadout("csm", 0, -1, SimConfig.PlaceholderMeleeSteps) { Side = Team.Chaos, Role = "t" }],
        };
        var world = new World(config, level, [new SpawnPoint(new Vector3(-8, 0, 0), 0f), new SpawnPoint(new Vector3(8, 0, 0), 0f)]) { Rules = new MatchRules() };
        world.Spawn(1, 0); world.Spawn(2, 1);
        Assert.Equal((new Vector3(-8, 0, 0), new Vector3(8, 0, 0)), (world.Players[1].Position, world.Players[2].Position));
        world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, YawToward(Vector2.UnitX), InputButtons.Fire) : new PlayerInput(0, 0, 0, 0, InputButtons.None));
        Assert.Contains(world.LastHits, h => h.Shooter == 1 && h.Target == 2);
    }

    [Fact]
    public void The_sides_meshes_keep_the_level_triangles_exact_and_number_the_boxes_after_them()
    {
        var mesh = Mesh(Floor(0f), Wall(new(-20, 10), new(20, 10)), Wall(new(-20, -10), new(20, -30), 6f), Floor(1.5f, 5, 9, -4, 4));
        var level = new CollisionWorld(mesh, mesh, null, [MarineWall with { Center = new Vector3(200, 200, 19) }]);   // out of the rays' reach
        var chaos = level.CharacterFor(Team.Chaos);
        var random = new Random(5);
        int hits = 0;
        for (int i = 0; i < 2000; i++)
        {
            var o = new Vector3(random.NextSingle() * 50 - 25, random.NextSingle() * 50 - 25, random.NextSingle() * 8);
            var d = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
            var a = mesh.Raycast(o, d, 30f);
            var b = chaos.Raycast(o, d, 30f);
            if (a != b) output.WriteLine($"ray {i} from {o} along {d}: level {a}, side mesh {b}");
            Assert.Equal(a, b);
            if (a is not null) hits++;
        }
        Assert.True(hits > 500, $"{hits} hits");
        var box = chaos.Raycast(new Vector3(190, 200, 1), Vector3.UnitX, 20f);
        Assert.NotNull(box);
        Assert.True(box!.Value.Triangle >= 8, $"box triangle numbered {box.Value.Triangle}");   // after the 8 level triangles
        Assert.NotEqual(mesh.ContentHash, chaos.ContentHash);
    }

    [Fact]
    public void The_level_hash_covers_the_blockers_and_bad_blockers_are_refused()
    {
        var mesh = Mesh(Floor(0f));
        ulong none = new CollisionWorld(mesh, mesh).ContentHash;
        Assert.Equal(none, new CollisionWorld(mesh, mesh, null, []).ContentHash);   // no blockers: as before
        ulong one = new CollisionWorld(mesh, mesh, null, [MarineWall]).ContentHash;
        Assert.NotEqual(none, one);
        Assert.Equal(one, new CollisionWorld(mesh, mesh, null, [MarineWall with { }]).ContentHash);
        Assert.NotEqual(one, new CollisionWorld(mesh, mesh, null, [MarineWall with { Side = Team.Chaos }]).ContentHash);
        Assert.NotEqual(one, new CollisionWorld(mesh, mesh, null, [MarineWall with { Yaw = 1e-3f }]).ContentHash);
        foreach (var bad in new[] { MarineWall with { Size = new Vector3(1, 0, 1) }, MarineWall with { Center = new Vector3(float.NaN, 0, 0) }, MarineWall with { Yaw = float.PositiveInfinity } })
            Assert.Throws<ArgumentException>(() => new CollisionWorld(mesh, mesh, null, [bad]));
        // A world on that level takes the blockers for the spawn facing unless given its own.
        var level = new CollisionWorld(mesh, mesh, null, [MarineWall]);
        Assert.Equal([MarineWall], new World(Config, level).SpawnBlockers);
        Assert.Empty(new World(Config, level) { SpawnBlockers = [] }.SpawnBlockers);
    }

    // ---------------------------------------------------------------- over the network

    static ClassLoadout Side(ClassLoadout k, string name, Team side, string role) => k with { Name = name, Side = side, Role = role };

    /// <summary>Tactical (roll) and assault (jump pack) classes for both sides, harmless weapons.</summary>
    static readonly SimConfig NetConfig = EvadeTests.Harmless(Config with
    {
        Classes =
        [
            Side(Config.Classes[Tactical], "sm - tactical", Team.SpaceMarines, "tactical"), Side(Config.Classes[Assault], "sm - assault", Team.SpaceMarines, "assault"),
            Side(Config.Classes[Tactical], "csm - tactical", Team.Chaos, "tactical"), Side(Config.Classes[Assault], "csm - raptor", Team.Chaos, "assault"),
        ],
    });

    /// <summary>
    /// A 24 × 24 m walled yard (walls 40 m) split in three by a marine blocker at x −4 and a Chaos blocker at x +4 (1 m thick,
    /// 40 m tall); spawns in the middle strip.
    /// </summary>
    static readonly SpawnBlocker NetMarineWall = new(Team.SpaceMarines, new Vector3(-4, 0, 19), new Vector3(1, 24, 40), 0f);
    static readonly SpawnBlocker NetChaosWall = new(Team.Chaos, new Vector3(4, 0, 19), new Vector3(1, 24, 40), 0f);

    static CollisionWorld Yard()
    {
        var mesh = Mesh(Floor(0f, -12, 12, -12, 12), Wall(new(-12, -12), new(12, -12), 40f), Wall(new(12, -12), new(12, 12), 40f),
            Wall(new(12, 12), new(-12, 12), 40f), Wall(new(-12, 12), new(-12, -12), 40f));
        return new CollisionWorld(mesh, mesh, null, [NetMarineWall, NetChaosWall]);
    }

    static PlayerInput YardInput(int i, uint t)
    {
        var move = Harness.Scripted(i, t);
        uint phase = (t + (uint)i * 23) % 75;
        var buttons = move.Buttons & ~(InputButtons.Jump | InputButtons.Melee);
        if (phase is < 10 || phase == 40) buttons |= InputButtons.Jump;     // a pack lift-off held (assault) or a roll (tactical)
        if (phase == 28) buttons |= InputButtons.Melee;                     // the assault class dives
        // Head mostly east or west (along X, across the blockers), swinging slowly.
        float yaw = ((t / 150 + (uint)i) % 2 == 0 ? -MathF.PI / 2 : MathF.PI / 2) + MathF.Sin(t * 0.02f + i) * 0.6f;
        return move with { Buttons = buttons, Yaw = yaw, MoveX = move.MoveX * 0.3f, MoveY = 1f, Pitch = -0.2f };
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    public void Prediction_matches_the_server_with_players_pressing_against_blockers_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var level = Yard();
        var spawns = new[] { new SpawnPoint(new Vector3(0, -6, 0), 0f), new SpawnPoint(new Vector3(0, 6, 0), 1f), new SpawnPoint(new Vector3(-1, 0, 0), 2f), new SpawnPoint(new Vector3(1, 2, 0), 3f) };
        var map = new GameMap("blocker_yard", level, spawns, 81);
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, NetConfig, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5), Match = new MatchRules { TimeLimit = 0f } }, map: map);
        int[] classes = [0, 2, 1, 3];
        for (int i = 0; i < classes.Length; i++)
            h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = (byte)classes[i], LoadMap = _ => new LoadedMap(level, 81) });

        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, classes.Length).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        h.Run(TimeSpan.FromSeconds(30), YardInput, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < classes.Length; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });

        // The server's truth: no enemy ever got past a blocker; enemies pressed against them; each side went through its own.
        int contacts = 0, airborneContacts = 0, ownCrossings = 0;
        foreach (var world in truth.Values)
            foreach (var p in world.Values.Where(p => p.Alive))
            {
                if (p.Team == Team.Chaos)
                {
                    Assert.True(p.Position.X >= -3.5f + R - 2e-3f, $"Chaos player {p.PlayerId} at {p.Position} is past the marine blocker");
                    if (p.Position.X < -3.5f + R + 0.05f) { contacts++; if (!p.Grounded) airborneContacts++; }
                    if (p.Position.X > 4.6f) ownCrossings++;
                }
                else if (p.Team == Team.SpaceMarines)
                {
                    Assert.True(p.Position.X <= 3.5f - R + 2e-3f, $"marine {p.PlayerId} at {p.Position} is past the Chaos blocker");
                    if (p.Position.X > 3.5f - R - 0.05f) { contacts++; if (!p.Grounded) airborneContacts++; }
                    if (p.Position.X < -4.6f) ownCrossings++;
                }
                else Assert.Fail($"player {p.PlayerId} has no side");
            }
        output.WriteLine($"{contacts} player-ticks pressed against an enemy blocker ({airborneContacts} airborne), {ownCrossings} beyond their own");
        Assert.True(contacts > 200, $"only {contacts} player-ticks against an enemy blocker");
        Assert.True(airborneContacts > 10, $"only {airborneContacts} airborne player-ticks against an enemy blocker");
        Assert.True(ownCrossings > 100, $"only {ownCrossings} player-ticks beyond the own blocker");

        for (int i = 0; i < classes.Length; i++)
        {
            var c = h.Clients[i];
            int id = c.PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            int corrections = c.CorrectionErrors.Count(e => e > 1e-4f);
            output.WriteLine($"client {i} ({NetConfig.Classes[classes[i]].Name}, {h.Server.World.Players[id].Team}): compared {compared} ticks, {mismatches.Count} mismatches, {corrections} corrections");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.Empty(mismatches);
            Assert.Equal(0, corrections);
        }
    }
}
