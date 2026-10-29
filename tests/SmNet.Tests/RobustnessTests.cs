using System.Numerics;
using SmContent;
using SmNet;
using SmSim;

namespace SmNet.Tests;

/// <summary>M4 acceptance: malformed packets, hostile inputs, protocol round trips, content fingerprint.</summary>
public class RobustnessTests
{
    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0);

    static Message[] Samples() =>
    [
        new Hello(Protocol.Version, 0xDEADBEEFUL, 0x1234UL, 9, 123456, 0xC00C1E, 0xAB0DE),
        new Challenge(9, 0xC00C1E, 123456, "mp_dlc2_map4"),
        new Leave(0x5E55),
        new Welcome(3, 1000, 30, 9, 123456, 0x5E55),
        new Reject(RejectReason.ConfigMismatch, "gameplay tuning differs", 9),
        new InputBatch([new PlayerInput(5, 0.5f, -1f, 1.25f, InputButtons.Jump | InputButtons.Sprint | InputButtons.Fire | InputButtons.Melee, -0.3f, 3), new PlayerInput(4, 0, 0, 0, InputButtons.Reload)], 0x5E55),
        new Snapshot(77, 75, 2, [new PlayerState(1, new Vector3(1, 2, 3), new Vector3(-1, 0, 0.5f), 0.7f, true)
        {
            Alive = true, Health = 77.5f, Shield = 12.25f, Clip = 29, Reserve = 240, Cooldown = 0.1f, ReloadLeft = 0.5f, SinceDamage = 3f, Pitch = 0.2f,
            MeleeStep = 2, MeleeTime = 0.4f, MeleeQueued = true, MeleeHeld = true,
            Class = 2, Slot = WeaponSlot.Pistol, OtherClip = 30, OtherReserve = 270, SwitchLeft = 0.25f, FireHeld = true, SwitchHeld = true,
        }, new PlayerState(2, Vector3.Zero, Vector3.One, -3f, false) { RespawnLeft = 4.5f, MeleeHeld = true, Class = 1, Slot = WeaponSlot.Pistol, MeleeStep = 1, MeleeTime = 0.1f }], Self: 1),
        // Protocol 22: every new message, and the Hello with a full setup.
        new Hello(Protocol.Version, 0xDEADBEEFUL, 0x1234UL, 9, 123456, 0xC00C1E, 0xAB0DE, 1, 2, 3, 4, 5, 2, 6, 7,
            AppearanceCode.Default.WithPiece(ArmourSlot.Head, 3).WithDecals(1, AppearanceCode.DecalEmblem, 4).WithEmblem(new EmblemHash(7, 9)), AppearanceCode.Default.WithLens((1, 2, 3))),
        new Setup(0x5E55, 4, new LoadoutChoice(1, 0, -1, 2, 1, 0, 3), AppearanceCode.Default.WithPredefinedSet(2), AppearanceCode.Default),
        new SetupAck(4, new LoadoutChoice(1, 0, -1, 2, 1, 0, -1), SetupFlags.Pending | SetupFlags.Defaulted, 77),
        new AppearanceQuery(0x5E55, [1, 2, 65535]),
        new Appearance(2, 77, Race.Chaos, AppearanceCode.Default.WithPalette(ArmourSlot.Torso, 1, 2, 3)),
        new BlobRequest(0x5E55, new EmblemHash(7, 9), 15),
        new BlobChunk(new EmblemHash(7, 9), 3, 16, [.. Enumerable.Range(0, 1024).Select(i => (byte)i)]),
        new Scoreboard(9, true, 41, 600f, false, [new ScoreEntry(1, Team.SpaceMarines, 0, 1, 2, 77), new ScoreEntry(2, Team.Chaos, 2, 3, 4)]),
    ];

    [Fact]
    public void Largest_snapshot_and_input_batch_fit_one_udp_packet()
    {
        var players = Enumerable.Range(1, Protocol.MaxPlayers).Select(i => new PlayerState(i, Vector3.One, Vector3.One, 1f, true) { Alive = true, Health = 1, Shield = 1, Clip = 1, Reserve = 1, MeleeStep = 3, MeleeTime = 1, MeleeQueued = true, MeleeHeld = true }).ToArray();
        Assert.True(Protocol.Write(new Snapshot(1, 1, 1, players)).Length <= UdpServerTransport.MaxPacket);
        var inputs = Enumerable.Range(1, Protocol.MaxInputsPerBatch).Select(i => new PlayerInput((uint)i, 1, 1, 1, InputButtons.Fire, 1, 1)).ToArray();
        Assert.True(Protocol.Write(new InputBatch(inputs, 1, 1)).Length <= UdpServerTransport.MaxPacket);
    }

    [Fact]
    public void Snapshots_with_two_records_for_one_player_are_rejected()
    {
        var a = new PlayerState(3, Vector3.Zero, Vector3.Zero, 0f, true) { Alive = true };
        var b = new PlayerState(4, Vector3.One, Vector3.Zero, 0f, true) { Alive = true };
        Assert.NotNull(Protocol.Read(Protocol.Write(new Snapshot(1, 0, 0, [a, b]))));   // control: distinct ids parse
        Assert.Null(Protocol.Read(Protocol.Write(new Snapshot(1, 0, 0, [a, a with { Position = Vector3.One }]))));
    }

    [Fact]
    public void Protocol_RoundTripsEveryMessage()
    {
        foreach (var m in Samples())
        {
            var back = Protocol.Read(Protocol.Write(m));
            Assert.NotNull(back);
            Assert.Equal(m.GetType(), back!.GetType());
            switch (m)
            {
                case InputBatch b: Assert.Equal(b.Inputs, ((InputBatch)back).Inputs); Assert.Equal(b.Session, ((InputBatch)back).Session); break;
                case Snapshot s:
                    var sb = (Snapshot)back;
                    Assert.Equal((s.ServerTick, s.LastInputTick, s.InputLead), (sb.ServerTick, sb.LastInputTick, sb.InputLead));
                    // The recipient's own player (Self) in full, the others slim.
                    Assert.Equal(s.Self, sb.Self);
                    Assert.Equal(s.Players.Select(p => p.PlayerId == s.Self ? p : Protocol.Slim(p)), sb.Players); break;
                case AppearanceQuery q: Assert.Equal((q.Session, q.PlayerIds), (((AppearanceQuery)back).Session, ((AppearanceQuery)back).PlayerIds), TupleOfArrays.Instance); break;
                case BlobChunk c: Assert.Equal((c.Hash, c.Index, c.Count), (((BlobChunk)back).Hash, ((BlobChunk)back).Index, ((BlobChunk)back).Count)); Assert.Equal(c.Data, ((BlobChunk)back).Data); break;
                case Scoreboard b: Assert.Equal(b.Players, ((Scoreboard)back).Players); Assert.Equal((b.ServerTick, b.ScoreLimit), (((Scoreboard)back).ServerTick, ((Scoreboard)back).ScoreLimit)); break;
                default: Assert.Equal(m, back); break;
            }
        }
    }

    [Fact]
    public void Protocol_Read_NeverThrowsOnRandomOrTruncatedPackets()
    {
        var rng = new Random(20261026);
        int parsed = 0, total = 0;
        // Pure random bytes, biased so the first byte is often a valid message kind.
        for (int i = 0; i < 5000; i++)
        {
            var bytes = new byte[rng.Next(0, 200)];
            rng.NextBytes(bytes);
            if (bytes.Length > 0 && rng.Next(2) == 0) bytes[0] = (byte)rng.Next(1, 15);   // every kind (14 since protocol 22)
            total++;
            if (Protocol.Read(bytes) is not null) parsed++;
        }
        // Every truncation of every valid message, and seeded single-byte corruptions.
        foreach (var m in Samples())
        {
            var good = Protocol.Write(m);
            for (int len = 0; len < good.Length; len++) { total++; Assert.Null(Protocol.Read(good.AsSpan(0, len))); }
            total++; Assert.Null(Protocol.Read([.. good, 0]));   // trailing garbage
            for (int i = 0; i < 1000; i++)
            {
                var bad = (byte[])good.Clone();
                bad[rng.Next(bad.Length)] = (byte)rng.Next(256);
                total++;
                _ = Protocol.Read(bad);
            }
        }
        // Counts beyond the limits.
        // Kind, session, snapshot tick, count, then complete inputs (36 bytes each since protocol 12): only the count check can refuse it.
        const int InputBytes = Protocol.InputBytes;
        byte[] tooMany = [4, 0, 0, 0, 0, 0, 0, 0, 0, Protocol.MaxInputsPerBatch + 1, .. new byte[InputBytes * (Protocol.MaxInputsPerBatch + 1)]];
        Assert.Null(Protocol.Read(tooMany));
        tooMany[9] = Protocol.MaxInputsPerBatch;   // control: the same packet at the limit parses
        Assert.NotNull(Protocol.Read(tooMany.AsSpan(0, tooMany.Length - InputBytes)));
        Assert.Null(Protocol.Read([5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Protocol.MaxPlayers + 1]));
        Assert.Null(Protocol.Read([3, 1, 0, 0, 0, 0, 0xFF, 0xFF]));   // reject detail longer than the packet
        Assert.True(total > 10000);
    }

    [Fact]
    public void Sanitized_ClampsNonFiniteAndOutOfRange()
    {
        var s = new PlayerInput(1, float.NaN, float.PositiveInfinity, float.NegativeInfinity, (InputButtons)0xFF).Sanitized();
        Assert.Equal((0f, 0f, 0f), (s.MoveX, s.MoveY, s.Yaw));
        var big = new PlayerInput(1, 1e30f, -1e30f, 1e30f, InputButtons.None).Sanitized();
        Assert.Equal(1f, big.MoveX); Assert.Equal(-1f, big.MoveY);
        Assert.True(float.IsFinite(big.Yaw) && MathF.Abs(big.Yaw) <= 2 * MathF.PI);
    }

    [Fact]
    public void Movement_WithHostileInput_StaysFiniteAndBelowSprintSpeed()
    {
        var c = new SimConfig();
        var state = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0, true);
        var hostile = new[]
        {
            new PlayerInput(0, float.NaN, float.NaN, float.NaN, (InputButtons)0xFF),
            new PlayerInput(0, 1e30f, 1e30f, float.MaxValue, InputButtons.Sprint),
            new PlayerInput(0, float.NegativeInfinity, float.PositiveInfinity, float.Epsilon, InputButtons.Jump),
        };
        for (int i = 0; i < 300; i++)
        {
            state = Movement.Step(state, hostile[i % hostile.Length], c);
            Assert.True(Harness.Finite(state.Position) && Harness.Finite(state.Velocity));
            Assert.True(new Vector2(state.Velocity.X, state.Velocity.Y).Length() <= c.SprintSpeed + 1e-3f);
        }
    }

    /// <summary>Connects a raw endpoint by hand (Hello, Challenge, Hello with cookie) and returns its player id and session.</summary>
    internal static (int Id, uint Session) ConnectRaw(Harness h, IClientTransport raw, uint nonce = 1)
    {
        raw.Send(Protocol.Write(new Hello(Protocol.Version, Harness.Fingerprint, h.Config.Hash(), nonce)));
        for (int i = 0; i < 500; i++)
        {
            h.Step();
            while (raw.TryReceive(out var p))
                switch (Protocol.Read(p))
                {
                    case Challenge c: raw.Send(Protocol.Write(new Hello(Protocol.Version, Harness.Fingerprint, h.Config.Hash(), nonce, 0, c.Cookie))); break;
                    case Welcome w: return (w.PlayerId, w.Session);
                }
        }
        throw new Xunit.Sdk.XunitException("raw client got no Welcome");
    }

    [Fact]
    public void Server_IgnoresNonFiniteAndOutOfRangeInputValues()
    {
        var h = new Harness(Link, seed: 1, clients: 0);
        var raw = h.Net.Connect();
        var (id, session) = ConnectRaw(h, raw);
        uint baseTick = h.Server.World.Tick;
        var inputs = new List<PlayerInput>();
        for (uint t = 3; t <= 14; t++)
        {
            inputs.Add((t % 4) switch
            {
                0 => new PlayerInput(baseTick + t, float.NaN, float.PositiveInfinity, float.NaN, (InputButtons)0xFF),
                1 => new PlayerInput(baseTick + t, 1e30f, -1e30f, 1e30f, InputButtons.Sprint),
                2 => new PlayerInput(baseTick + t, float.NegativeInfinity, float.MaxValue, float.PositiveInfinity, InputButtons.Jump),
                _ => new PlayerInput(baseTick + t, -float.MaxValue, float.NaN, -float.MaxValue, InputButtons.Sprint | InputButtons.Jump),
            });
        }
        raw.Send(Protocol.Write(new InputBatch(inputs.Take(Protocol.MaxInputsPerBatch).ToArray(), session)));
        var c = h.Config;
        for (int i = 0; i < 3000; i++)
        {
            h.Step();
            if (i % 1000 == 999) raw.Send(Protocol.Write(new InputBatch([], session)));   // keep-alive (peer timeout is 5 s)
            var s = h.Server.World.Players[id];
            Assert.True(Harness.Finite(s.Position) && Harness.Finite(s.Velocity), $"non-finite state at tick {h.Server.World.Tick}: {s}");
            Assert.True(new Vector2(s.Velocity.X, s.Velocity.Y).Length() <= c.SprintSpeed + 1e-3f);
            Assert.True(float.IsFinite(s.Yaw));
        }
    }

    [Fact]
    public void Server_IgnoresInputsFarInTheFuture_ButAppliesNearOnes()
    {
        // This test stays silent for 10 s, longer than the default peer timeout.
        var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new SmServer.ServerOptions { PeerTimeout = TimeSpan.FromMinutes(1) });
        var raw = h.Net.Connect();
        var (id, session) = ConnectRaw(h, raw);
        var spawn = h.Server.World.Players[id].Position;

        // 200 ticks ahead (beyond ServerOptions.MaxInputLead = 120): must be dropped, even after that tick passes.
        uint far = h.Server.World.Tick + 200;
        raw.Send(Protocol.Write(new InputBatch([new PlayerInput(far, 0, 1, 0, InputButtons.Sprint)], session)));
        h.Run(TimeSpan.FromSeconds(10));
        Assert.True(h.Server.World.Tick > far + 30);
        Assert.Equal(spawn, h.Server.World.Players[id].Position);

        // Control: a near-future input is applied (and repeated), so the player moves.
        uint near = h.Server.World.Tick + 5;
        raw.Send(Protocol.Write(new InputBatch([new PlayerInput(near, 0, 1, 0, InputButtons.None)], session)));
        h.Run(TimeSpan.FromSeconds(1));
        Assert.True(Vector3.Distance(spawn, h.Server.World.Players[id].Position) > 1f);
    }

    [Fact]
    public void Server_IgnoresInputsFromUnknownConnections()
    {
        var h = new Harness(Link, seed: 1, clients: 0);
        var stranger = h.Net.Connect();
        stranger.Send(Protocol.Write(new InputBatch([new PlayerInput(5, 1, 1, 0, InputButtons.Sprint)])));
        stranger.Send([0xFF, 1, 2, 3]);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Empty(h.Server.World.Players);
    }

    [Fact]
    public void SimConfigHash_IsStableAndSensitive()
    {
        Assert.Equal(new SimConfig().Hash(), new SimConfig().Hash());
        Assert.NotEqual(new SimConfig().Hash(), new SimConfig { TickRate = 60 }.Hash());
        Assert.NotEqual(new SimConfig().Hash(), new SimConfig { JumpSpeed = 6.0001f }.Hash());
        Assert.NotEqual(new SimConfig().Hash(), new SimConfig { Gravity = 9.81f }.Hash());
    }

    [Fact]
    public void ContentFingerprint_PathSizeOverload()
    {
        (string, long)[] a = [("data/a.oppc", 10), ("data/b.oppc", 20)];
        Assert.Equal(ContentFingerprint.Compute(a), ContentFingerprint.Compute(a.ToArray()));
        Assert.NotEqual(ContentFingerprint.Compute(a), ContentFingerprint.Compute([("data/a.oppc", 10), ("data/b.oppc", 21)]));
        Assert.NotEqual(ContentFingerprint.Compute(a), ContentFingerprint.Compute([("data/a.oppc", 10), ("data/c.oppc", 20)]));
        Assert.NotEqual(ContentFingerprint.Compute(a), ContentFingerprint.Compute([("data/a.oppc", 10)]));
        // Path/size boundary is unambiguous: "ab"+size vs "a"+"b..." cannot collide via the separator.
        Assert.NotEqual(ContentFingerprint.Compute([("data/ab", 1)]), ContentFingerprint.Compute([("data/a", 1), ("b", 1)]));
    }

    [Fact]
    public void ContentFingerprint_DirectoryMatchesPathSizeList()
    {
        string root = Path.Combine(Path.GetTempPath(), "smnet-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "data", "Chunks"));
            File.WriteAllBytes(Path.Combine(root, "data", "Zeta.oppc"), new byte[7]);
            File.WriteAllBytes(Path.Combine(root, "data", "Chunks", "mp_test.oppc"), new byte[13]);
            File.WriteAllBytes(Path.Combine(root, "data", "readme.txt"), new byte[99]);   // not an archive: ignored
            File.WriteAllBytes(Path.Combine(root, "outside.oppc"), new byte[5]);          // outside data/: ignored

            ulong fromDir = ContentFingerprint.Compute(root);
            ulong expected = ContentFingerprint.Compute([("data/chunks/mp_test.oppc", 13), ("data/zeta.oppc", 7)]);
            Assert.Equal(expected, fromDir);

            File.WriteAllBytes(Path.Combine(root, "data", "Zeta.oppc"), new byte[8]);
            Assert.NotEqual(fromDir, ContentFingerprint.Compute(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>Compares (session, ids) tuples by the ids' contents.</summary>
sealed class TupleOfArrays : IEqualityComparer<(uint, int[])>
{
    public static readonly TupleOfArrays Instance = new();
    public bool Equals((uint, int[]) a, (uint, int[]) b) => a.Item1 == b.Item1 && a.Item2.SequenceEqual(b.Item2);
    public int GetHashCode((uint, int[]) t) => t.Item1.GetHashCode();
}
