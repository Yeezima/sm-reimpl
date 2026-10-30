using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M22: diagonal rolls (user's knowledge of the original: two movement keys roll diagonally). The roll takes eight
/// directions; a diagonal plays the forward or back clip with the body turned 45° (forward diagonals with alignment turn to
/// the stick). The hop keeps four. Distance and timing, walls, prediction exactness and protocol 14.
/// </summary>
public class EvadeDiagonalTests(ITestOutputHelper output)
{
    const int Tactical = 1, Heavy = 3;
    const float Dt = 1f / 30f;
    static readonly EvadeSpec Roll8 = EvadeTests.Roll with { Diagonals = true };
    static readonly SimConfig Config = EvadeTests.HopConfig with { Evades = [Roll8, EvadeTests.Hop] };

    static PlayerState Fresh(int cls = Tactical, SimConfig? c = null) =>
        Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls }, c ?? Config);

    static void Step(ref PlayerState s, InputButtons b, float mx = 0f, float my = 0f, float yaw = 0f, CollisionWorld? world = null, SimConfig? c = null) =>
        s = Combat.StepPlayer(s, new PlayerInput(0, mx, my, yaw, b), c ?? Config, out _, world);

    static Vector2 Flat(Vector3 v) => new(v.X, v.Y);
    /// <summary>A direction in the view's frame (X right, Y forward) at yaw <paramref name="yaw"/> in world space.</summary>
    static Vector2 World(float yaw, Vector2 local) =>
        new Vector2(MathF.Cos(yaw), MathF.Sin(yaw)) * local.X + new Vector2(-MathF.Sin(yaw), MathF.Cos(yaw)) * local.Y;

    // ---------------------------------------------------------------- directions

    [Theory]
    [InlineData(0f, 1f, EvadeDirection.Forward)]         // W
    [InlineData(1f, 1f, EvadeDirection.ForwardRight)]    // W + D
    [InlineData(1f, 0f, EvadeDirection.Right)]           // D
    [InlineData(1f, -1f, EvadeDirection.BackRight)]      // S + D ("roll south-east")
    [InlineData(0f, -1f, EvadeDirection.Back)]           // S
    [InlineData(-1f, -1f, EvadeDirection.BackLeft)]      // S + A
    [InlineData(-1f, 0f, EvadeDirection.Left)]           // A
    [InlineData(-1f, 1f, EvadeDirection.ForwardLeft)]    // W + A
    [InlineData(0.7071f, -0.7071f, EvadeDirection.BackRight)]   // a normalised key pair
    [InlineData(0.05f, -0.05f, EvadeDirection.Forward)]  // the dead zone
    [InlineData(0.38f, 1f, EvadeDirection.Forward)]      // 20.8°: forward's sector (±22.5°)
    [InlineData(0.45f, 1f, EvadeDirection.ForwardRight)] // 24.2°
    public void Key_pairs_give_eight_directions_on_the_roll(float mx, float my, EvadeDirection expected)
    {
        Assert.Equal(expected, Evade.DirectionOf(mx, my, diagonals: true));
        var s = Fresh();
        Step(ref s, InputButtons.Jump, mx, my, 0.3f);
        Assert.Equal((1, expected), (s.EvadeTicks, s.EvadeDirection));
        // The hop keeps four directions (an exact diagonal: forward or back), and so does a roll without diagonals.
        var h = Fresh(Heavy);
        Step(ref h, InputButtons.Jump, mx, my, 0.3f);
        Assert.Equal(Evade.DirectionOf(mx, my), h.EvadeDirection);
        Assert.True((int)h.EvadeDirection < 4);
        Assert.Equal(Evade.DirectionOf(mx, my), Evade.DirectionOf(mx, my, diagonals: false));
    }

    [Fact]
    public void Diagonals_play_the_forward_or_back_clip_turned_45_degrees()
    {
        Assert.Same(Roll8.Back, Roll8.Move(EvadeDirection.BackRight));
        Assert.Same(Roll8.Back, Roll8.Move(EvadeDirection.BackLeft));
        Assert.Same(Roll8.Forward, Roll8.Move(EvadeDirection.ForwardRight));
        Assert.Same(Roll8.Forward, Roll8.Move(EvadeDirection.ForwardLeft));
        Assert.Equal((-MathF.PI / 4, MathF.PI / 4, MathF.PI / 4, -MathF.PI / 4, 0f),
            (Evade.YawOffset(EvadeDirection.ForwardRight), Evade.YawOffset(EvadeDirection.ForwardLeft), Evade.YawOffset(EvadeDirection.BackRight), Evade.YawOffset(EvadeDirection.BackLeft), Evade.YawOffset(EvadeDirection.Back)));
        Assert.True(Evade.IsForward(EvadeDirection.ForwardLeft) && !Evade.IsForward(EvadeDirection.BackLeft));
    }

    // ---------------------------------------------------------------- distance and timing

    [Theory]
    [InlineData(1f, -1f, 1f, -1f)]     // S + D: back-right
    [InlineData(-1f, -1f, -1f, -1f)]   // S + A: back-left
    [InlineData(1f, 1f, 1f, 1f)]       // W + D: forward-right (aligned forward roll)
    [InlineData(-1f, 1f, -1f, 1f)]     // W + A
    public void A_diagonal_roll_goes_its_clip_distance_along_the_diagonal_in_the_clip_time(float mx, float my, float dx, float dy)
    {
        const float view = 0.7f;
        var s = Fresh();
        var straight = Fresh();
        var direction = Vector2.Normalize(World(view, new Vector2(dx, dy)));
        var clip = Roll8.Move(Evade.DirectionOf(mx, my, true));
        int n = 0;
        var last = Vector2.Zero;
        for (; n < 60; n++)
        {
            Step(ref s, n == 0 ? InputButtons.Jump : InputButtons.None, n == 0 ? mx : 0f, n == 0 ? my : 0f, view);
            Step(ref straight, n == 0 ? InputButtons.Jump : InputButtons.None, 0f, n == 0 ? MathF.Sign(my) : 0f, view);
            if (s.EvadeTicks == 0) break;
            Assert.Equal(straight.EvadeTicks, s.EvadeTicks);   // the same timing as the straight clip
            var want = direction * clip.At(s.EvadeTicks * Dt).Length();
            Assert.True(Vector2.Distance(want, Flat(s.Position)) < 1e-3f, $"tick {s.EvadeTicks}: {Flat(s.Position)} vs {want}");
            last = Flat(s.Position);
        }
        Assert.Equal(0, straight.EvadeTicks);   // both end in the same tick
        Assert.Equal(30, n);                     // the clip: 1 s = 30 ticks, ended in the tick after
        Assert.Equal(clip.Distance.Length(), last.Length(), 3);   // 9 m at the clip's last tick
        Assert.True(Vector2.Dot(Vector2.Normalize(last), direction) > 0.99999f);
        output.WriteLine($"stick ({mx}, {my}): {Flat(s.Position)} after {n} ticks, body yaw {s.Yaw:F4} (view {view})");
    }

    [Fact]
    public void Without_alignment_a_forward_diagonal_turns_the_body_45_degrees()
    {
        var unaligned = Config with { Evades = [Roll8 with { AlignForward = false }, EvadeTests.Hop] };
        var s = Fresh(c: unaligned);
        Step(ref s, InputButtons.Jump, 1f, 0.6f, 0.2f, c: unaligned);   // 59°: forward-right's sector
        Assert.Equal(EvadeDirection.ForwardRight, s.EvadeDirection);
        Assert.Equal(0.2f - MathF.PI / 4, s.Yaw, 5);
        // Aligned, the same stick turns the body to the stick (59° right).
        var a = Fresh();
        Step(ref a, InputButtons.Jump, 1f, 0.6f, 0.2f);
        Assert.Equal(0.2f + MathF.Atan2(-1f, 0.6f), a.Yaw, 5);
        // A back diagonal turns by 45° either way: back-right turns left (+), so the back clip travels back-right.
        var b = Fresh();
        Step(ref b, InputButtons.Jump, 1f, -1f, 0.2f);
        Assert.Equal(0.2f + MathF.PI / 4, b.Yaw, 5);
    }

    // ---------------------------------------------------------------- walls

    static CollisionWorld Mesh(params (Vector3 A, Vector3 B, Vector3 C, Vector3 D)[] quads)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        foreach (var (a, b, c, d) in quads) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    static (Vector3, Vector3, Vector3, Vector3) Floor(float z) => (new(-60, -60, z), new(60, -60, z), new(60, 60, z), new(-60, 60, z));
    static (Vector3, Vector3, Vector3, Vector3) Wall(Vector2 a, Vector2 b) => (new(a.X, a.Y, -1f), new(b.X, b.Y, -1f), new(b.X, b.Y, 4f), new(a.X, a.Y, 4f));

    [Fact]
    public void A_wall_across_the_diagonal_stops_the_roll_and_one_along_it_is_slid_past()
    {
        // Back-right from yaw 0 travels along (1, -1)/√2; a wall across it 4 m out.
        var d = Vector2.Normalize(new Vector2(1, -1));
        var perp = new Vector2(d.Y, -d.X);
        var p = d * 4f;
        var across = Mesh(Floor(0f), Wall(p - perp * 30f, p + perp * 30f));
        var s = Fresh();
        Step(ref s, InputButtons.Jump, 1f, -1f, world: across);
        int n = 1;
        while (s.EvadeTicks > 0 && n < 40) { Step(ref s, InputButtons.None, world: across); n++; }
        float reach = Vector2.Dot(Flat(s.Position), d);
        output.WriteLine($"across: ended after {n} ticks, {reach:F3} m along the diagonal");
        Assert.True(n < 30, $"the roll lasted {n} ticks");
        Assert.InRange(reach, 4f - Config.CapsuleRadius - 0.05f, 4f - Config.CapsuleRadius + 0.01f);

        // A wall parallel to the diagonal 1 m to its side does not touch the roll: the clip's whole 9 m.
        var along = Mesh(Floor(0f), Wall(perp * 1.2f - d * 30f, perp * 1.2f + d * 30f));
        var o = Fresh();
        Step(ref o, InputButtons.Jump, 1f, -1f, world: along);
        for (int k = 2; k <= 30; k++) { Step(ref o, InputButtons.None, world: along); Assert.Equal(k, o.EvadeTicks); }
        Assert.Equal(9f, Vector2.Dot(Flat(o.Position), d), 2);
    }

    // ---------------------------------------------------------------- prediction

    static PlayerInput DiagonalInput(int i, uint t)
    {
        var move = Harness.Scripted(i, t);
        var buttons = move.Buttons & ~(InputButtons.Jump | InputButtons.Sprint);
        uint phase = (t + (uint)i * 17) % 50;
        uint roll = (t + (uint)i * 17) / 50;
        if (phase is 0 or 1) buttons |= InputButtons.Jump;
        // The stick at the press cycles through the eight key combinations; later in the roll the script's stick.
        (float X, float Y)[] keys = [(1, -1), (-1, -1), (1, 1), (-1, 1), (0, -1), (1, 0), (0, 1), (-1, 0)];
        var (kx, ky) = keys[(int)(roll % 8)];
        if (phase is 20 && i == 0) buttons |= InputButtons.Fire;
        if (phase is 25 && i == 1) buttons |= InputButtons.SlotPistol;
        if (phase is 40 && i == 1) buttons |= InputButtons.SlotPrimary;
        return move with { Buttons = buttons, MoveX = phase < 18 ? kx : move.MoveX, MoveY = phase < 18 ? ky : move.MoveY, Pitch = -0.2f };
    }

    [Theory]
    [InlineData(231)]
    [InlineData(233)]
    public void Prediction_matches_the_server_with_diagonal_rolls_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var config = EvadeTests.Harmless(Config);
        var level = Mesh(Floor(0f), Wall(new(-40, 14), new(40, 14)), Wall(new(-14, -40), new(-14, 40)));   // walls to roll into
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var map = new GameMap("diag", level, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(4, 0, 0), 0f), new SpawnPoint(new Vector3(-4, 0, 0), 0f)], 77);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) }, map: map);
        int[] classes = [Tactical, Tactical, Heavy];
        for (int i = 0; i < classes.Length; i++) h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = (byte)classes[i], LoadMap = _ => new LoadedMap(level, 77) });
        var seenRemotely = new HashSet<EvadeDirection>();
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = classes.Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        h.Run(TimeSpan.FromSeconds(30), DiagonalInput, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < classes.Length; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
            if (h.Clients[2].Status == ClientStatus.Connected)
                foreach (var p in h.Clients[2].RemotePlayers().Players) if (p.EvadeTicks > 0) seenRemotely.Add(p.EvadeDirection);
        });
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
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            var directions = mine.Where(p => p.EvadeTicks == 1).Select(p => p.EvadeDirection).Distinct().ToList();
            output.WriteLine($"client {i} ({config.Classes[classes[i]].Name}): compared {compared} ticks, {mismatches.Count} mismatches, {c.CorrectionErrors.Count(e => e > 1e-4f)} corrections; directions {string.Join(",", directions.Order())}");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.Empty(mismatches);
            Assert.Equal(0, c.CorrectionErrors.Count(e => e > 1e-4f));
            if (classes[i] == Tactical) Assert.Equal(8, directions.Count);   // all eight seen
            else Assert.True(directions.All(d => (int)d < 4) && directions.Count >= 3);
        }
        // Remote players see the diagonals (slim records carry all eight directions).
        output.WriteLine($"client 2 saw remote directions {string.Join(",", seenRemotely.Order())}");
        Assert.Equal(8, seenRemotely.Count);
    }

    // ---------------------------------------------------------------- protocol 14

    [Fact]
    public void Protocol_14_carries_all_eight_directions_in_slim_and_own_records_and_refuses_malformed_ones()
    {
        Assert.Equal(45, Protocol.Version);
        var players = Enumerable.Range(1, 16).Select(id => new PlayerState(id, new Vector3(id), Vector3.Zero, 0.5f, true)
        {
            Alive = true, Slot = id % 3 == 0 ? WeaponSlot.Pistol : WeaponSlot.Ranged, EvadeTicks = id * 4, EvadeDirection = (EvadeDirection)(id % 8), EvadeLock = 3,
        }).ToArray();
        for (int self = 1; self <= 16; self += 5)
        {
            var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, players, Self: self))));
            Assert.Equal(players.Select(p => p.PlayerId == self ? p : Protocol.Slim(p)), back.Players);
            foreach (var (sent, got) in players.Zip(back.Players))
                Assert.Equal((sent.EvadeDirection, sent.Slot, Math.Min(sent.EvadeTicks, 63)), (got.EvadeDirection, got.Slot, Math.Min(got.EvadeTicks, 63)));
        }
        // Not evading: no direction bits; the slot byte is the slot alone.
        Assert.Equal(EvadeDirection.Forward, Protocol.Slim(players[4] with { EvadeTicks = 0, EvadeDirection = EvadeDirection.BackLeft }).EvadeDirection);
        var idle = players[2] with { EvadeTicks = 0, EvadeDirection = EvadeDirection.BackLeft };
        var idleBytes = Protocol.Write(new Snapshot(1, 1, 1, [idle with { PlayerId = 4 }]));
        Assert.Equal(1, idleBytes[^31]);   // slot byte (pistol): … slot, step, time 4, evade 1, then 24 bytes of blocks
        // The slot byte: unknown bits and a diagonal bit without an evade are refused.
        var me = players[4] with { PlayerId = 4, EvadeTicks = 5, EvadeDirection = EvadeDirection.BackRight };   // 5: bit 2 set
        var slim = Protocol.Write(new Snapshot(1, 1, 1, [me]));
        Assert.Equal((2, 5 | (1 << 6)), ((int)slim[^31], (int)slim[^25]));
        var unknown = (byte[])slim.Clone(); unknown[^31] |= 128;   // protocol 24: bit 3 (8) is the dash's landing part; 29: bits 4..6 the brace and the vent, bit 7 unknown
        Assert.Null(Protocol.Read(unknown));
        var landed = (byte[])slim.Clone(); landed[^31] |= 8;
        Assert.True(Assert.IsType<Snapshot>(Protocol.Read(landed)).Players[0].EvadeLanded);
        var orphan = (byte[])slim.Clone(); orphan[^25] = 0;   // no evade, but the diagonal bit
        Assert.Null(Protocol.Read(orphan));
        var orphanLanding = (byte[])idleBytes.Clone(); orphanLanding[^31] |= 8;   // no evade, but the landing bit
        Assert.Null(Protocol.Read(orphanLanding));
        // The worst case is unchanged (the bit uses a spare one).
        Assert.Equal(1194, Protocol.MaxSnapshotBytes);   // protocol 23: + the perk word (21, 22: 1198; 20: 1197; 17-19: 1195; 16: 1199; 15: 1195)
    }
}
