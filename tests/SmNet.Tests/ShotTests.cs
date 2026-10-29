using System.Numerics;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Shot events for effects (M25a, protocol 16): where a ray or cone shot went in the world (end point, hit kind, surface
/// material), the shot records in snapshots (round trip, refusals, the packet budget), the server's choice per recipient and
/// delivery to other clients. Synthetic data only.
/// </summary>
public class ShotTests(ITestOutputHelper output)
{
    /// <summary>
    /// <paramref name="count"/> shot records with the widest values: shooters cycle through <paramref name="players"/>, every
    /// hit kind, weapon 254, stage 7, age 3, an end 1000 m away (within the ±1024 m range).
    /// </summary>
    internal static ShotView[] ManyShots(PlayerState[] players, int count) => [.. Enumerable.Range(0, count).Select(k =>
    {
        var hit = (ShotHit)(k % 4);
        return new ShotView(players[k % players.Length].PlayerId, 254, 7, hit, hit == ShotHit.World ? 14 : -1, players[k % players.Length].Position + new Vector3(1000f, -1000f, 999f), 3);
    })];

    static PlayerState Player(int id, Vector3 at) => new(id, at, Vector3.Zero, 0f, true) { Alive = true };

    // ---------------------------------------------------------------- the wire

    [Fact]
    public void Shot_records_round_trip_relative_to_their_shooter()
    {
        Assert.Equal(42, Protocol.Version);   // protocol 16 brought the shot records; 17 (M28) left them as they are
        Assert.Equal(9, Protocol.ShotBytes);
        PlayerState[] players = [Player(3, new Vector3(100.3f, -812.7f, 4.1f)), Player(65535, new Vector3(-5f, 7f, 0f))];
        ShotView[] shots =
        [
            new(3, 0, 0, ShotHit.World, 4, new Vector3(120.01f, -800.02f, 3.3f), 0),
            new(65535, 2, 3, ShotHit.Head, -1, new Vector3(-4f, 30f, 1.7f), 2),
            new(3, 254, 7, ShotHit.None, -1, players[0].Position + new Vector3(0f, 250f, 0f), 1),
            new(65535, 1, 0, ShotHit.Body, -1, players[1].Position, 3),
            new(3, 1, 0, ShotHit.World, -1, new Vector3(101f, -812f, 4f), 0),   // unknown surface
        ];
        var bytes = Protocol.Write(new Snapshot(5, 4, 0, players, Shots: shots));
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal(shots.Select(s => Protocol.Quantize(s, players.First(p => p.PlayerId == s.Shooter).Position)), back.Shots!);
        for (int k = 0; k < shots.Length; k++)
        {
            Assert.True(Vector3.Distance(shots[k].End, back.Shots![k].End) <= 0.5f / Protocol.ShotEndScale * MathF.Sqrt(3f) + 1e-4f, $"shot {k}");
            Assert.Equal((shots[k].Shooter, shots[k].Weapon, shots[k].Stage, shots[k].Hit, shots[k].Age), (back.Shots![k].Shooter, back.Shots[k].Weapon, back.Shots[k].Stage, back.Shots[k].Hit, back.Shots[k].Age));
        }
        Assert.Equal((4, -1, -1), (back.Shots![0].Surface, back.Shots[1].Surface, back.Shots[4].Surface));
        // The block is last: a count byte and 9 bytes per record.
        Assert.Equal(1 + shots.Length * Protocol.ShotBytes, bytes.Length - Protocol.Write(new Snapshot(5, 4, 0, players)).Length + 1);
        // Normalised on the way out: a surface on a non-world hit or out of range is dropped, the age is clamped, an end beyond
        // ±1024 m is shortened along its direction.
        var odd = Protocol.Quantize(new ShotView(3, 0, 0, ShotHit.Body, 4, players[0].Position + new Vector3(4000f, 0f, -2000f), 9), players[0].Position);
        Assert.Equal((-1, 3), (odd.Surface, odd.Age));
        var offset = odd.End - players[0].Position;
        Assert.True(MathF.Abs(offset.X - 1024f) < 0.05f && MathF.Abs(offset.Z + 512f) < 0.05f, $"{offset}");
        Assert.Equal(-1, Protocol.Quantize(new ShotView(3, 0, 0, ShotHit.World, 15, Vector3.Zero), Vector3.Zero).Surface);
        var wire = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(5, 4, 0, players, Shots: [new ShotView(3, 0, 0, ShotHit.Body, 4, players[0].Position + new Vector3(4000f, 0f, -2000f), 9)]))));
        Assert.Equal(odd, wire.Shots![0]);
        // Left out by the writer: a shooter without a record, no weapon, a stage above 7, a non-finite end, an unknown hit kind.
        ShotView good = new(3, 0, 0, ShotHit.None, -1, Vector3.One);
        foreach (var bad in new[] { good with { Shooter = 4 }, good with { Weapon = -1 }, good with { Weapon = 255 }, good with { Stage = 8 }, good with { End = new Vector3(float.NaN, 0, 0) }, good with { Hit = (ShotHit)4 } })
            Assert.Empty(Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(5, 4, 0, players, Shots: [bad, good])))).Shots!.Skip(1));
        Assert.Empty(Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(5, 4, 0, players)))).Shots!);
    }

    [Fact]
    public void Malformed_shot_records_are_refused()
    {
        PlayerState[] players = [Player(3, Vector3.Zero), Player(4, Vector3.One)];
        var bytes = Protocol.Write(new Snapshot(5, 4, 0, players, Shots: [new ShotView(4, 2, 1, ShotHit.World, 3, new Vector3(5, 6, 7), 2)]));
        int record = bytes.Length - Protocol.ShotBytes;
        Assert.Equal(1, bytes[record - 1]);   // the count
        Assert.Equal(1 | ((int)ShotHit.World << 4) | (2 << 6), bytes[record]);
        Assert.Equal((2, 1 | (3 << 3)), ((int)bytes[record + 1], (int)bytes[record + 2]));
        static byte[] Patch(byte[] b, int at, int value) { var copy = (byte[])b.Clone(); copy[at] = (byte)value; return copy; }
        Assert.NotNull(Protocol.Read(Patch(bytes, record, 0 | ((int)ShotHit.World << 4))));          // control: the first player
        Assert.Null(Protocol.Read(Patch(bytes, record, 2 | ((int)ShotHit.World << 4))));             // no third player record
        Assert.Null(Protocol.Read(Patch(bytes, record + 1, 255)));                                    // no weapon
        Assert.Null(Protocol.Read(Patch(bytes, record + 2, 1 | (3 << 3) | 128)));                     // an unknown bit
        Assert.Null(Protocol.Read(Patch(bytes, record, 1 | ((int)ShotHit.Body << 4))));               // a player hit naming a surface
        Assert.NotNull(Protocol.Read(Patch(Patch(bytes, record, 1 | ((int)ShotHit.Body << 4)), record + 2, 1 | (15 << 3))));   // control: unknown surface
        Assert.Null(Protocol.Read(Patch(bytes, record - 1, 2)));                                      // a count whose records are missing
        Assert.Null(Protocol.Read(Patch(bytes, record - 1, Protocol.MaxSnapshotShots + 1)));
        for (int n = 0; n < bytes.Length; n++) Assert.Null(Protocol.Read(bytes.AsSpan(0, n)));
        Assert.Null(Protocol.Read([.. bytes, 0]));                                                   // trailing bytes
    }

    [Fact]
    public void Shot_records_fill_the_packet_up_to_the_budget_and_never_beyond()
    {
        var players = Enumerable.Range(1, Protocol.MaxPlayers).Select(i => Player(i, new Vector3(i, -i, 2f)) with { Class = 63 }).ToArray();
        var shots = ManyShots(players, 40);
        // The fullest snapshot of every other block still carries the guaranteed shot records, 6 bytes under the budget (a fifth would not fit).
        var projectiles = Enumerable.Range(0, Protocol.MaxSnapshotProjectiles).Select(k => new ProjectileView(60000 + k, 254, 255, new Vector3(k), ProjectileFlags.Detonated)).ToArray();
        var full = Protocol.Write(new Snapshot(1, 1, 1, players, Self: 7, Projectiles: projectiles, Match: new MatchView(MatchState.Playing, Team.None, 1, 2, 3),
            Kills: [.. Enumerable.Range(0, Protocol.MaxSnapshotKills).Select(k => new KillView(k, 1, 2, KillCause.Weapon, 0, Vector3.UnitX))],
            Points: [.. Enumerable.Repeat(new ControlPointView(Team.Chaos, Team.Chaos, 1f, true), Protocol.MaxSnapshotPoints)], Shots: shots));
        output.WriteLine($"fullest snapshot: {full.Length} bytes, {Protocol.MinSnapshotShots} shot records");
        Assert.Equal((Protocol.MaxSnapshotBytes, 1200), (full.Length, full.Length));   // protocol 23: 1200 (21, 22: 1198) (20: 1197); protocol 17: 4 guaranteed shot records (16: 5, 1199 bytes)
        Assert.Equal(Protocol.MinSnapshotShots, Assert.IsType<Snapshot>(Protocol.Read(full)).Shots!.Length);
        // With fewer projectile records the freed bytes carry more shots, never past the budget; the first ones are kept.
        for (int p = 0; p <= Protocol.MaxSnapshotProjectiles; p++)
        {
            var bytes = Protocol.Write(new Snapshot(1, 1, 1, players, Self: 7, Projectiles: projectiles[..p], Points: [.. Enumerable.Repeat(new ControlPointView(Team.Chaos, Team.Chaos, 1f, true), Protocol.MaxSnapshotPoints)], Shots: shots));
            Assert.True(bytes.Length <= Protocol.SnapshotBudget && bytes.Length <= UdpServerTransport.MaxPacket, $"{p} projectiles: {bytes.Length} bytes");
            var back = Assert.IsType<Snapshot>(Protocol.Read(bytes))!;
            Assert.True(back.Shots!.Length >= Protocol.MinSnapshotShots);
            Assert.True(bytes.Length + Protocol.ShotBytes > Protocol.SnapshotBudget || back.Shots.Length == Protocol.MaxSnapshotShots, $"{p}: room for one more");
            Assert.Equal(shots.Take(back.Shots.Length).Select(s => s.Shooter), back.Shots.Select(s => s.Shooter));
        }
        // A small snapshot carries at most MaxSnapshotShots.
        var small = Protocol.Write(new Snapshot(1, 1, 1, players[..2], Shots: ManyShots(players[..2], 40)));
        Assert.Equal(Protocol.MaxSnapshotShots, Assert.IsType<Snapshot>(Protocol.Read(small)).Shots!.Length);
    }

    // ---------------------------------------------------------------- the simulation

    /// <summary>
    /// A wall across +Y at <paramref name="y"/> m whose lower part (to 1.5 m) has surface 3 and upper part surface 4, standing on
    /// a floor (surface 0) at z = 0 that players stand on.
    /// </summary>
    static CollisionWorld Wall(float y)
    {
        var v = new[]
        {
            new Vector3(-20, y, -1), new Vector3(20, y, -1), new Vector3(20, y, 1.5f), new Vector3(-20, y, 1.5f),
            new Vector3(20, y, 10), new Vector3(-20, y, 10),
            new Vector3(-50, -50, 0), new Vector3(50, -50, 0), new Vector3(50, 50, 0), new Vector3(-50, 50, 0),
        };
        int[] indices = [0, 1, 2, 0, 2, 3, 3, 2, 4, 3, 4, 5, 6, 7, 8, 6, 8, 9];
        var mesh = new StaticMesh(v, indices, [3, 3, 4, 4, 0, 0]);
        return new CollisionWorld(mesh, mesh);
    }

    static PlayerInput FireAt(World world, int shooter, Vector3 point)
    {
        var eye = Combat.EyePosition(world.Players[shooter], world.Config);
        return new PlayerInput(0, 0, 0, 0f, InputButtons.Fire).WithShotDirection(point - eye);
    }

    [Fact]
    public void A_ray_shot_reports_its_end_what_it_hit_and_the_surface()
    {
        var c = new SimConfig { HealthMax = 1e6f };
        Assert.True(Wall(10f).Weapons.HasMaterials);
        Assert.Equal((3, 4, 0, -1, -1), (Wall(10f).Weapons.MaterialOf(0), Wall(10f).Weapons.MaterialOf(3), Wall(10f).Weapons.MaterialOf(4), Wall(10f).Weapons.MaterialOf(6), Wall(10f).Weapons.MaterialOf(-1)));
        Assert.False(new StaticMesh([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2]).HasMaterials);
        Assert.Throws<ArgumentException>(() => new StaticMesh([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2], [1, 2]));

        // Into the wall's upper part: a world hit on surface 4, ending on the wall.
        var world = new World(c, Wall(10f), [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(30f, 0f, 0f), 0f)]);
        world.Spawn(1); world.Spawn(2);
        world.Step(id => id == 1 ? FireAt(world, 1, new Vector3(0f, 10f, 3f)) : new PlayerInput(0, 0, 0, 0f, InputButtons.None));
        var shot = Assert.Single(world.LastShots);
        Assert.Equal((1, 0, ShotHit.World, 4, 0), (shot.Shooter, shot.Projectile, shot.Hit, shot.Surface, shot.Target));
        Assert.Equal(10f, shot.End.Y, 3);
        Assert.Equal(Combat.EyePosition(world.Players[1], c), shot.Origin);
        Assert.True(Vector3.Distance(shot.End, new Vector3(0f, 10f, 3f)) < 1e-3f, $"{shot.End}");

        // Into the lower part: surface 3.
        for (int i = 0; i < c.TickRate; i++) world.Step(_ => new PlayerInput(0, 0, 0, 0f, InputButtons.None));
        world.Step(id => id == 1 ? FireAt(world, 1, new Vector3(2f, 10f, 0.5f)) : new PlayerInput(0, 0, 0, 0f, InputButtons.None));
        Assert.Equal((ShotHit.World, 3), (Assert.Single(world.LastShots).Hit, world.LastShots[0].Surface));

        // Away from the wall: nothing within range; the end is at the weapon's range along the shot.
        for (int i = 0; i < c.TickRate; i++) world.Step(_ => new PlayerInput(0, 0, 0, 0f, InputButtons.None));
        var eye = Combat.EyePosition(world.Players[1], c);
        world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, MathF.PI, InputButtons.Fire, 0f) : new PlayerInput(0, 0, 0, 0f, InputButtons.None));
        var miss = Assert.Single(world.LastShots);
        Assert.Equal((ShotHit.None, -1, 0), (miss.Hit, miss.Surface, miss.Target));
        Assert.Equal(Loadouts.InHand(c, world.Players[1])!.MaxRange, Vector3.Distance(miss.Origin, miss.End), 2);
        Assert.True(miss.End.Y < eye.Y - 100f);
    }

    [Fact]
    public void A_ray_shot_into_a_player_reports_the_zone_and_the_target()
    {
        var c = new SimConfig { HealthMax = 1e6f, ShieldMax = 1e6f };
        var target = new Vector3(0f, 8f, 0f);
        var world = new World(c, Wall(30f), [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(target, MathF.PI)]);
        world.Spawn(1); world.Spawn(2);
        float chest = HitVolumes.CentreHeight(c);
        world.Step(id => id == 1 ? FireAt(world, 1, target + new Vector3(0, 0, chest)) : new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None));
        var shot = Assert.Single(world.LastShots);
        var hit = Assert.Single(world.LastHits);
        Assert.Equal((ShotHit.Body, 2, -1), (shot.Hit, shot.Target, shot.Surface));
        Assert.Equal(hit.Distance, Vector3.Distance(shot.Origin, shot.End), 3);   // the end is where the ray met the hit volume
        for (int i = 0; i < c.TickRate; i++) world.Step(_ => new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None));
        world.Step(id => id == 1 ? FireAt(world, 1, target + new Vector3(0, 0, c.HeadHeight)) : new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None));
        Assert.Equal((ShotHit.Head, 2), (Assert.Single(world.LastShots).Hit, world.LastShots[0].Target));
        Assert.Equal(HitZone.Head, Assert.Single(world.LastHits).Zone);
    }

    [Fact]
    public void A_cone_shot_ends_at_the_wall_or_its_length_and_names_the_nearest_victim()
    {
        var config = new SimConfig { Weapons = [MeltaTests.Melta], Classes = [new ClassLoadout("test - melta", 0, -1, []), new ClassLoadout("test - target", -1, -1, [])], HealthMax = 1e6f, ShieldMax = 1e6f };
        PlayerInput Ahead(World w) => new(0, 0, 0, 0f, InputButtons.Fire, 0f);
        // A wall at 5 m (within the 20 m cone), no target: a world hit on the wall.
        var walled = new World(config, Wall(5f), [new SpawnPoint(Vector3.Zero, 0f)]);
        walled.Spawn(1, 0);
        walled.Step(id => Ahead(walled));
        var shot = Assert.Single(walled.LastShots);
        Assert.True(shot.End.Z > 1.5f);   // level from the eye: the wall's upper part
        Assert.Equal((ShotHit.World, 4), (shot.Hit, shot.Surface));
        Assert.Equal(5f, shot.End.Y, 3);
        // No wall: the end is at the cone's length; a target inside it is the hit.
        var open = new World(config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0f, 6f, 0f), MathF.PI)]);
        open.Spawn(1, 0); open.Spawn(2, 1);
        open.Step(id => id == 1 ? Ahead(open) : new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None));
        shot = Assert.Single(open.LastShots);
        Assert.NotEmpty(open.LastHits);
        Assert.Equal((ShotHit.Body, 2, -1), (shot.Hit, shot.Target, shot.Surface));
        Assert.Equal(MeltaTests.Cone.Length, Vector3.Distance(shot.Origin, shot.End), 3);
    }

    [Fact]
    public void Projectile_launches_keep_no_end_point()
    {
        // Projectiles travel as projectile records (and their detonations); their ShotEvent has no end and the server sends no shot record for them.
        var e = new ShotEvent(5, 1, 0, 0, 77);
        Assert.Equal((Vector3.Zero, ShotHit.None, -1, 0), (e.End, e.Hit, e.Surface, e.Target));
        Assert.Empty(GameServer.ShotsFor([e], 2, Vector3.Zero, 5));
        // A ray event that never resolved (a stage firing nothing hit-scan) has no end either and is not sent.
        Assert.Empty(GameServer.ShotsFor([e with { Projectile = 0 }], 2, Vector3.Zero, 5));
        Assert.Single(GameServer.ShotsFor([e with { Projectile = 0, End = Vector3.UnitY }], 2, Vector3.Zero, 5));
    }

    // ---------------------------------------------------------------- the server and clients

    [Fact]
    public void The_server_sends_each_recipient_the_nearest_other_shots_first()
    {
        ShotEvent At(uint tick, int shooter, Vector3 from, Vector3 to) => new(tick, shooter, 0, 0, 0, from, to, ShotHit.None);
        var shots = new List<ShotEvent>
        {
            At(10, 1, new Vector3(0, 0, 0), new Vector3(0, 100, 0)),     // passes the viewer at 5 m
            At(11, 2, new Vector3(50, 0, 0), new Vector3(50, 100, 0)),   // 45 m away
            At(9, 3, new Vector3(10, 0, 0), new Vector3(10, 100, 0)),    // 5 m, older
            At(11, 4, new Vector3(5, 50, 0), new Vector3(5, 60, 0)),     // the recipient's own
        };
        var views = GameServer.ShotsFor(shots, 4, new Vector3(5, 50, 0), 11);
        Assert.Equal([1, 3, 2], views.Select(v => v.Shooter));   // equal distance: the newer first
        Assert.Equal([1, 2, 0], views.Select(v => v.Age));
        Assert.Equal(new Vector3(0, 100, 0), views[0].End);
        // Ages are capped; at most MaxSnapshotShots go to the writer.
        Assert.Equal(Protocol.MaxShotAge, GameServer.ShotsFor([At(1, 1, Vector3.Zero, Vector3.One)], 2, Vector3.Zero, 100)[0].Age);
        var many = Enumerable.Range(0, 60).Select(k => At(5, 1 + k % 3, new Vector3(k, 0, 0), new Vector3(k, 1, 0))).ToList();
        Assert.Equal(Protocol.MaxSnapshotShots, GameServer.ShotsFor(many, 9, Vector3.Zero, 5).Length);
    }

    [Fact]
    public void Clients_receive_other_players_shots_once_and_never_their_own()
    {
        var c = new SimConfig { HealthMax = 1e6f, ShieldMax = 1e6f, ClipSize = 1000, ReserveSize = 1000 };
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(20), TimeSpan.Zero, 0), 3, 2, c);
        var received = new List<ShotView>[] { new(), new() };
        h.Run(TimeSpan.FromSeconds(1));
        Assert.All(h.Clients, x => Assert.Equal(ClientStatus.Connected, x.Status));
        int shooter = h.Clients[0].PlayerId;
        int fired = 0;
        h.Server.Stepped += w => fired += w.LastShots.Count(s => s.Shooter == shooter);
        // Client 0 fires straight ahead for two seconds (full auto); client 1 stands still.
        h.Run(TimeSpan.FromSeconds(2), (i, t) => new PlayerInput(t, 0, 0, 0f, i == 0 ? InputButtons.Fire : InputButtons.None, 0.05f),
            () => { for (int i = 0; i < 2; i++) received[i].AddRange(h.Clients[i].TakeShots()); });
        h.Run(TimeSpan.FromMilliseconds(500), null, () => { for (int i = 0; i < 2; i++) received[i].AddRange(h.Clients[i].TakeShots()); });
        output.WriteLine($"fired {fired}, client 1 received {received[1].Count}, client 0 received {received[0].Count}");
        Assert.True(fired > 5, $"{fired} shots");
        Assert.Empty(received[0]);                       // the shooter's own shots are not sent back
        Assert.Equal(fired, received[1].Count);          // every shot once (a perfect link, few shooters)
        Assert.All(received[1], s => Assert.Equal((shooter, ShotHit.None), (s.Shooter, s.Hit)));
        var from = h.Server.World.Players[shooter].Position;
        Assert.All(received[1], s => Assert.True(s.End.Y - from.Y > 100f, $"{s.End}"));   // to the weapon's range, ahead (+Y)
    }

    [Fact]
    public void Own_predicted_shots_are_reported_once_as_events()
    {
        // M26b: the client hands out its prediction's events (for sounds), each tick's once, matching the server's count closely.
        var c = new SimConfig { HealthMax = 1e6f, ShieldMax = 1e6f, ClipSize = 1000, ReserveSize = 1000 };
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(20), TimeSpan.Zero, 0), 5, 1, c);
        h.Run(TimeSpan.FromSeconds(1));
        h.Clients[0].TakeOwnEvents();
        int shooter = h.Clients[0].PlayerId, fired = 0;
        var events = new List<(uint Tick, SelfEvents Events)>();
        h.Server.Stepped += w => fired += w.LastShots.Count(x => x.Shooter == shooter);
        h.Run(TimeSpan.FromSeconds(2), (i, t) => new PlayerInput(t, 0, 0, 0f, InputButtons.Fire, 0f), () => events.AddRange(h.Clients[0].TakeOwnEvents()));
        h.Run(TimeSpan.FromMilliseconds(300), null, () => events.AddRange(h.Clients[0].TakeOwnEvents()));
        int predicted = events.Count(e => e.Events.Fired);
        output.WriteLine($"server shots {fired}, predicted shot events {predicted}");
        Assert.True(predicted > 5);
        Assert.Equal(events.Count, events.Select(e => e.Tick).Distinct().Count());   // each tick once
        Assert.InRange(predicted, fired - 2, fired + 2);
        Assert.Empty(h.Clients[0].TakeOwnEvents());
    }
}
