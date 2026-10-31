using System.Numerics;
using System.Security.Cryptography;
using SmNet;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Added content "Musou" (MU0, the switch; design section A): Musou must not change the existing modes at all. Golden values captured on main
/// 35deb39 (protocol 49) BEFORE any Musou hook landed, and pinned here: the config hash of a synthetic config and of the default config in the
/// faithful and the extended profile, and a scripted 900-tick World replay (4 players, 2 a side, on the flat placeholder ground: hit-scan fire,
/// grenades, melee combos and charges) as SHA-256 over every tick's <see cref="Protocol.Write(Message)"/> of the snapshot (once per player as its
/// own full record), the match's state and scores, and the kill log. Core folder (not <c>Extras/</c>), so it also runs in <c>faithful-build.cmd</c>.
/// Synthetic fixtures defined here only (do not change them: the goldens depend on every value).
/// </summary>
public class MusouIsolationTests(ITestOutputHelper output)
{
    // ---------------------------------------------------------------- the fixture (do not change: pinned)

    static readonly WeaponSpec Rifle = new("iso_rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("iso_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static readonly MeleeStep Heavy = new(1.2f, 0.3f, 0f, 0f, 0.8f, 0.9f, 45f, 10f, 3.5f) { PrimaryReaction = new(0f, ReactionKind.Stun) };

    static Vector2[] Line(float metres, int samples) => [.. Enumerable.Range(0, samples + 1).Select(k => new Vector2(0f, metres * k / samples))];
    static readonly RushMove RushClip = new("iso_rush", Line(12f, 12), 1.2f, 1.0f, 0.6f, 0.5f, 1.0f);
    static readonly RushMove StopClip = new("iso_rush_stop", Line(0.5f, 10), 1.0f, 0.9f, 0.9f, 0.6f, 0.3f);
    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    static readonly RushSpec Charge = new("iso_bullrush", RushClip, StopClip, Plain with { Damage = 75f, ShieldDamage = 25f }, new ReactionEntry(0f, ReactionKind.Stun),
        Plain with { Damage = 30f }, new ReactionEntry(0f, ReactionKind.Interrupt), 4f, 1f) { ContactUntil = 0.6f, WallFrom = 0.3f, HyperArmourUntil = 0.3f };

    static readonly RigidSpec Rigid = new(65f, 0.3f, 0.4f, 0.05f, 0.05f, [new(0f, 1f), new(90f, 0.2f)], [new(0f, 1f), new(90f, 0.2f)], 0.1f, 0.1f);
    static readonly DistancePoint[] FragFallOff = [new(0f, 1f), new(2.5f, 1f), new(2.6f, 0.5f), new(5f, 0.5f), new(5.1f, 0.3f), new(7f, 0.3f), new(10f, 0.05f)];
    static readonly ProjectileSpec FragBall = new("ebps\\iso\\frag", 20f, 30f, "rigidbody", 0f, [new(5f, 0f), new(10f, 1f)], false, true, false, 1, 1f, 1.5f, 0, null,
        new SplashSpec(6f, new DamageSpec(120f, 0f, 1f, 1f, 1f, 1f, false), FragFallOff, true, false, "target_all")) { Rigid = Rigid, StopAtMaxBounce = true };
    static readonly ThrownSpec Frag = new("iso_frag", 3, 3, 1f, 10f, FragBall);

    static ClassLoadout Class(string name, Team side) =>
        new(name, 0, 1, [Light, Light, Light]) { PrimaryChoices = [0], PistolChoices = [1], EquipmentChoices = [0], MeleeHeavy = [Heavy], MeleeRush = Charge, Side = side, Role = "tactical" };

    static readonly SimConfig Fixture = new()
    {
        Weapons = [Rifle, Pistol],
        Thrown = [Frag],
        Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)],
        HitVolumes = [HitVolume.Sphere("Head", HitZone.Head, new(0f, 0f, 1.9f), 0.2f), HitVolume.Capsule("Spine", HitZone.Body, new(0f, 0f, 0.4f), new(0f, 0f, 1.5f), 0.35f)],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        HealthMax = 150f, ShieldMax = 50f, RespawnTime = 2f,
    };

    static readonly SimConfig Faithful = Fixture;
    static readonly SimConfig Extended = Fixture with { Extras = ContentExtras.All };

    // ---------------------------------------------------------------- the goldens (captured on main 35deb39, before MU0's hooks)

    const ulong FixtureFaithfulHash = 0x2235C9DFF926583FUL, FixtureExtendedHash = 0x54B88195AFC434B4UL, DefaultFaithfulHash = 0xA57B097C29D78EABUL, DefaultExtendedHash = 0x0CA292E1A0338C00UL;
    const string FaithfulReplay = "05F93BCF2093221D9B4356450A324E43A06B7135AE2CE10E4D53D8A851C08F81", ExtendedReplay = "05F93BCF2093221D9B4356450A324E43A06B7135AE2CE10E4D53D8A851C08F81";

    [Fact]
    public void The_faithful_and_extended_config_hashes_are_unchanged()
    {
        output.WriteLine($"fixture faithful 0x{Faithful.Hash():X16}UL, extended 0x{Extended.Hash():X16}UL; default faithful 0x{new SimConfig().Hash():X16}UL, extended 0x{(new SimConfig() with { Extras = ContentExtras.All }).Hash():X16}UL");
        Assert.Equal(FixtureFaithfulHash, Faithful.Hash());
        Assert.Equal(FixtureExtendedHash, Extended.Hash());
        Assert.Equal(DefaultFaithfulHash, new SimConfig().Hash());
        Assert.Equal(DefaultExtendedHash, (new SimConfig() with { Extras = ContentExtras.All }).Hash());
    }

    [Fact]
    public void The_faithful_replay_is_unchanged() => AssertReplay(Faithful, FaithfulReplay);

    [Fact]
    public void The_extended_replay_is_unchanged() => AssertReplay(Extended, ExtendedReplay);

    void AssertReplay(SimConfig config, string golden)
    {
        var (hash, stats) = Replay(config);
        output.WriteLine($"replay {hash}: {stats}");
        // The script exercises every system it is meant to pin (a retuned fixture that stops shooting, throwing, striking or charging fails here).
        Assert.True(stats.Shots > 0 && stats.Throws > 0 && stats.MeleeHits > 0 && stats.Rushes > 0 && stats.Kills > 0 && stats.Detonations > 0, stats.ToString());
        Assert.Equal(golden, hash);
    }

    // ---------------------------------------------------------------- the wire (added with MU0's hooks)

    /// <summary>Musou's envelope tags (entities, events, director; <c>MusouWire</c>, named here as numbers: this core test runs in the faithful build too).</summary>
    static readonly byte[] MusouTags = [3, 4, 5, 6];

    [Fact]
    public void Musou_changes_neither_the_protocol_version_nor_the_extended_profile()
    {
        Assert.Equal(49u, (uint)Protocol.Version);
        Assert.Equal(2u, ContentExtras.WireVersion);
        Assert.False(ContentExtras.All.HasFlag(ContentExtras.Musou));
        Assert.Equal(ContentExtras.Orks | ContentExtras.Gore | ContentExtras.LoyalistBarks, ContentExtras.All);
        if (ContentExtras.Compiled)
        {
            Assert.True(ContentProfiles.TryParse(ContentProfiles.Extended, out var extended, out _));
            Assert.False(extended.HasFlag(ContentExtras.Musou));
        }
        // Musou hashes apart from both (a Musou client is refused by every other server and the other way round).
        ulong musou = (Fixture with { Extras = ContentExtras.Musou }).Hash();
        Assert.NotEqual(Faithful.Hash(), musou);
        Assert.NotEqual(Extended.Hash(), musou);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_faithful_or_extended_server_never_sends_a_musou_section(bool extended)
    {
        var config = extended ? Extended : Faithful;
        IServerTransport? recorder = null;
        var sent = new List<byte[]>();
        var h = new Harness(LinkConditions.Perfect, 7, 2, config, wrapServer: inner => recorder = new Recording(inner, sent));
        // The loyalist barks' key (input bit 13) every half second for everyone, so an extended server does send envelopes (the check is live).
        h.Run(TimeSpan.FromSeconds(4), (i, t) => Harness.Scripted(i, t) with { Buttons = Harness.Scripted(i, t).Buttons | (t % 15 < 4 ? InputButtons.ExtraAction : InputButtons.None) });
        Assert.NotNull(recorder);
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        var envelopes = sent.Select(p => Protocol.Read(p)).OfType<ExtrasUpdate>().ToList();
        output.WriteLine($"{(extended ? "extended" : "faithful")}: {sent.Count} packets, {envelopes.Count} envelopes, tags {string.Join(",", envelopes.SelectMany(e => e.Sections).Select(s => s.Tag).Distinct())}");
        if (extended && ContentExtras.Compiled) Assert.NotEmpty(envelopes);   // the barks' shouts (a faithful build has no Extras/ code to send them)
        else Assert.Empty(envelopes);
        Assert.DoesNotContain(envelopes.SelectMany(e => e.Sections), s => MusouTags.Contains(s.Tag));
    }

    sealed class Recording(IServerTransport inner, List<byte[]> sent) : IServerTransport
    {
        public void Send(int connection, byte[] packet) { sent.Add(packet); inner.Send(connection, packet); }
        public bool TryReceive(out int connection, out byte[] packet) => inner.TryReceive(out connection, out packet);
        public void Disconnect(int connection) => inner.Disconnect(connection);
        public void Pin(int connection) => inner.Pin(connection);
    }

    internal readonly record struct ReplayStats(int Shots, int Throws, int Detonations, int MeleeHits, int Rushes, int Kills, MatchState End);

    /// <summary>
    /// The scripted 900-tick match: players 1 and 2 Space Marines, 3 and 4 Chaos, 6 m apart across the middle. Each faces its nearest living enemy
    /// and loops (120 ticks, offset per player): fire while strafing, a grenade, a sprint into a charge, then a light combo and a heavy strike.
    /// </summary>
    internal static (string Hash, ReplayStats Stats) Replay(SimConfig config)
    {
        SpawnPoint[] spawns = [new(new(-2f, -3f, 0f), 0f), new(new(2f, -3f, 0f), 0f), new(new(-2f, 3f, 0f), MathF.PI), new(new(2f, 3f, 0f), MathF.PI)];
        var w = new World(config, null, spawns) { Rules = new MatchRules { CountdownTime = 1f, TimeLimit = 0f } };
        w.Spawn(1, 0); w.Spawn(2, 0); w.Spawn(3, 1); w.Spawn(4, 1);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int shots = 0, throws = 0, detonations = 0, meleeHits = 0, rushes = 0, kills = 0, lastKill = 0;
        var tail = new byte[4 + 4 + 4 + 4];
        for (int k = 0; k < 900; k++)
        {
            uint view = Math.Max(1u, w.Tick), body = w.Tick > 1 ? w.Tick - 1 : 0;
            var now = w.Players;
            w.Step(id => Script(k, id, now) with { ViewTick = view, BodyTick = body });
            var players = w.Players.Values.OrderBy(p => p.PlayerId).ToArray();
            foreach (var p in players) sha.AppendData(Protocol.Write(new Snapshot(w.Tick, 0, 0, players, Self: p.PlayerId)));
            BitConverter.TryWriteBytes(tail.AsSpan(0), (int)w.State);
            BitConverter.TryWriteBytes(tail.AsSpan(4), w.TicksLeft);
            BitConverter.TryWriteBytes(tail.AsSpan(8), w.ScoreOf(Team.SpaceMarines));
            BitConverter.TryWriteBytes(tail.AsSpan(12), w.ScoreOf(Team.Chaos));
            sha.AppendData(tail);
            foreach (var e in w.KillLog)
            {
                if (e.Sequence <= lastKill) continue;
                lastKill = e.Sequence;
                var b = new byte[6 * 4 + 3 * 4];
                BitConverter.TryWriteBytes(b.AsSpan(0), e.Sequence); BitConverter.TryWriteBytes(b.AsSpan(4), e.Tick);
                BitConverter.TryWriteBytes(b.AsSpan(8), e.Killer); BitConverter.TryWriteBytes(b.AsSpan(12), e.Victim);
                BitConverter.TryWriteBytes(b.AsSpan(16), (int)e.Cause); BitConverter.TryWriteBytes(b.AsSpan(20), e.Index);
                BitConverter.TryWriteBytes(b.AsSpan(24), e.Direction.X); BitConverter.TryWriteBytes(b.AsSpan(28), e.Direction.Y); BitConverter.TryWriteBytes(b.AsSpan(32), e.Direction.Z);
                sha.AppendData(b);
            }
            shots += w.LastShots.Count;
            throws += w.LastThrows.Count;
            detonations += w.LastDetonations.Count;
            meleeHits += w.LastHits.Count(h => h.Melee);
            rushes += w.LastRushes.Count;
            kills += w.LastKills.Count;
        }
        return (Convert.ToHexString(sha.GetHashAndReset()), new ReplayStats(shots, throws, detonations, meleeHits, rushes, kills, w.State));
    }

    static PlayerInput Script(int k, int id, IReadOnlyDictionary<int, PlayerState> players)
    {
        if (!players.TryGetValue(id, out var me)) return default;
        // Face the nearest living enemy (forward is (-sin yaw, cos yaw)).
        float yaw = me.Yaw, best = float.MaxValue;
        foreach (var (other, p) in players)
        {
            if (other == id || !p.Alive || p.Team == me.Team) continue;
            var d = p.Position - me.Position;
            float distance = d.LengthSquared();
            if (distance < best) { best = distance; yaw = MathF.Atan2(-d.X, d.Y); }
        }
        int t = (k + id * 17) % 120;
        float side = id % 2 == 0 ? 1f : -1f;
        return t switch
        {
            < 36 => new PlayerInput(0, side * (t < 18 ? 1f : -1f), 0f, yaw, InputButtons.Fire),
            36 or 37 => new PlayerInput(0, 0f, 0f, yaw, InputButtons.Grenade),
            < 50 => new PlayerInput(0, 0f, -0.5f, yaw, InputButtons.None),
            < 66 => new PlayerInput(0, 0f, 1f, yaw, InputButtons.Sprint),
            66 or 67 => new PlayerInput(0, 0f, 1f, yaw, InputButtons.Sprint | InputButtons.MeleeHeavy),
            < 84 => new PlayerInput(0, 0f, 0.3f, yaw, InputButtons.None),
            < 104 => new PlayerInput(0, 0f, 0.5f, yaw, t % 6 == 0 ? InputButtons.Melee : InputButtons.None),
            108 => new PlayerInput(0, 0f, 0f, yaw, InputButtons.MeleeHeavy),
            _ => new PlayerInput(0, 0f, 0f, yaw, InputButtons.None),
        };
    }
}
