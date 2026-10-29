using System.Net;
using System.Numerics;
using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Server hardening for a public dedicated server (branch server/hardening): per-address player cap and join rate, idle kick,
/// same-nonce Hellos that do not count as play, re-joins that wait out a dead player's respawn, crash isolation, the tick
/// catch-up cap, bans and content refusals. Loopback endpoints with synthetic addresses on a manual clock.
/// </summary>
public class ServerHardeningTests
{
    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0);

    static IPEndPoint At(string ip, int port) => new(IPAddress.Parse(ip), port);

    static GameClient AddressedClient(Harness h, IPEndPoint endPoint, uint nonce, SimConfig? config = null, ulong? fingerprint = null)
    {
        var c = new GameClient(config ?? h.Config, fingerprint ?? Harness.Fingerprint, h.Net.Connect(endPoint), h.Clock, nonce: nonce);
        h.Clients.Add(c);
        return c;
    }

    /// <summary>What a raw handshake got: a Welcome, a Reject, nothing at all, or only Challenges.</summary>
    sealed record JoinResult(Welcome? Welcome, Reject? Reject, bool AnyReply);

    /// <summary>Runs a raw client's handshake for up to <paramref name="max"/> (Hello resent every 250 ms, as the real client does).</summary>
    static JoinResult JoinRaw(Harness h, IClientTransport raw, uint nonce, TimeSpan max, Func<int, uint, PlayerInput>? input = null)
    {
        ulong cookie = 0;
        bool any = false;
        var nextHello = TimeSpan.Zero;
        var end = h.Clock.Now + max;
        while (h.Clock.Now < end)
        {
            if (h.Clock.Now >= nextHello)
            {
                raw.Send(Protocol.Write(new Hello(Protocol.Version, Harness.Fingerprint, h.Config.Hash(), nonce, 0, cookie)));
                nextHello = h.Clock.Now + TimeSpan.FromMilliseconds(250);
            }
            h.Step(input);
            while (raw.TryReceive(out var p))
            {
                any = true;
                switch (Protocol.Read(p))
                {
                    case Challenge c when c.Nonce == nonce && cookie == 0: cookie = c.Cookie; nextHello = TimeSpan.Zero; break;
                    case Welcome w when w.Nonce == nonce: return new JoinResult(w, null, true);
                    case Reject r when r.Nonce == nonce: return new JoinResult(null, r, true);
                }
            }
        }
        return new JoinResult(null, null, any);
    }

    // ---- M2: per-address cap, join rate, idle kick, same-nonce Hellos ----

    [Fact]
    public void Per_address_cap_refuses_the_extra_player_and_other_addresses_still_join()
    {
        var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new ServerOptions { MaxPlayersPerAddress = 2, JoinInterval = TimeSpan.Zero });
        var a = AddressedClient(h, At("10.0.0.1", 1001), 1);
        var b = AddressedClient(h, At("10.0.0.1", 1002), 2);
        h.Run(TimeSpan.FromSeconds(1));
        var c = AddressedClient(h, At("10.0.0.1", 1003), 3);
        var other = AddressedClient(h, At("10.0.0.2", 1001), 4);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Connected, a.Status);
        Assert.Equal(ClientStatus.Connected, b.Status);
        Assert.Equal(ClientStatus.Rejected, c.Status);
        Assert.Equal(RejectReason.ServerFull, c.RejectedBecause);
        Assert.Contains("at most 2 players from one address", c.RejectDetail);
        Assert.Equal(ClientStatus.Connected, other.Status);
        Assert.Equal(3, h.Server.World.Players.Count);
    }

    [Fact]
    public void Limited_loopback_counts_like_any_other_address_with_the_default_cap_of_four()
    {
        var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new ServerOptions { JoinInterval = TimeSpan.Zero, LimitLoopback = true });
        var clients = Enumerable.Range(0, 5).Select(i => AddressedClient(h, At("127.0.0.1", 50000 + i), (uint)(10 + i))).ToList();
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(4, clients.Count(c => c.Status == ClientStatus.Connected));
        Assert.Equal(1, clients.Count(c => c.Status == ClientStatus.Rejected));
    }

    [Fact]
    public void Loopback_is_exempt_from_the_cap_the_join_rate_and_the_idle_kick_by_default()
    {
        // The owner's playtests: up to 12 bot clients on 127.0.0.1 at once, some of them idle; default options.
        var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new ServerOptions { IdleKick = TimeSpan.FromSeconds(5) });
        var bots = Enumerable.Range(0, 12).Select(i => AddressedClient(h, At(i % 2 == 0 ? "127.0.0.1" : "::1", 50000 + i), (uint)(10 + i))).ToList();
        var remote = AddressedClient(h, At("10.0.0.5", 1000), 99);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.All(bots, b => Assert.Equal(ClientStatus.Connected, b.Status));
        Assert.Equal(0, h.Server.JoinsDeferred);
        h.Run(TimeSpan.FromSeconds(7));   // nobody moves
        Assert.Equal(12, bots.Count(b => h.Server.World.Players.ContainsKey(b.PlayerId)));
        Assert.False(h.Server.World.Players.ContainsKey(remote.PlayerId));   // a remote address still gets the idle kick
        Assert.Equal(1, h.Server.IdleKicked);
    }

    [Fact]
    public void Bans_apply_to_loopback_too()
    {
        var h = new Harness(Link, seed: 1, clients: 0);
        h.Server.Bans = BanList.Parse(["127.0.0.0/8"]);
        var raw = h.Net.Connect(At("127.0.0.1", 6000));
        Assert.False(JoinRaw(h, raw, 1, TimeSpan.FromSeconds(1)).AnyReply);
    }

    [Fact]
    public void Join_rate_defers_the_second_join_from_an_address_until_the_window_has_passed()
    {
        var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new ServerOptions { JoinInterval = TimeSpan.FromSeconds(5) });
        var first = AddressedClient(h, At("10.0.0.1", 1001), 1);
        h.Run(TimeSpan.FromMilliseconds(500));
        Assert.Equal(ClientStatus.Connected, first.Status);
        var second = AddressedClient(h, At("10.0.0.1", 1002), 2);
        var elsewhere = AddressedClient(h, At("10.0.0.3", 1002), 3);
        h.Run(TimeSpan.FromSeconds(4));
        Assert.Equal(ClientStatus.Connecting, second.Status);   // no answer: the client keeps resending its Hello
        Assert.Equal(ClientStatus.Connected, elsewhere.Status);
        Assert.True(h.Server.JoinsDeferred > 0);
        h.Run(TimeSpan.FromSeconds(1.5));
        Assert.Equal(ClientStatus.Connected, second.Status);
    }

    [Fact]
    public void Idle_player_is_dropped_and_an_active_one_stays()
    {
        var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new ServerOptions { IdleKick = TimeSpan.FromSeconds(10) });
        AddressedClient(h, At("10.0.0.1", 1000), 1);
        AddressedClient(h, At("10.0.0.2", 1000), 2);
        h.Run(TimeSpan.FromSeconds(1));
        int idleId = h.Clients[0].PlayerId, activeId = h.Clients[1].PlayerId;
        var input = (int i, uint t) => i == 0
            ? new PlayerInput(t, 0, 0, MathF.Sin(t * 0.1f), InputButtons.None)   // turning alone is not play
            : new PlayerInput(t, (t / 60) % 2 == 0 ? 1 : -1, 0, 0, InputButtons.None);
        h.Run(TimeSpan.FromSeconds(8), input);
        Assert.True(h.Server.World.Players.ContainsKey(idleId));
        h.Run(TimeSpan.FromSeconds(3), input);
        Assert.False(h.Server.World.Players.ContainsKey(idleId));
        Assert.True(h.Server.World.Players.ContainsKey(activeId));
        Assert.Equal(1, h.Server.IdleKicked);
    }

    [Fact]
    public void Idle_time_does_not_count_during_warm_up()
    {
        var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new ServerOptions { IdleKick = TimeSpan.FromSeconds(2), Match = new MatchRules() });
        AddressedClient(h, At("10.0.0.1", 1000), 1);
        h.Run(TimeSpan.FromSeconds(6));   // alone: warm-up until the other side has a player
        Assert.Equal(MatchState.Warmup, h.Server.World.State);
        Assert.Single(h.Server.World.Players);
        Assert.Equal(0, h.Server.IdleKicked);
    }

    [Fact]
    public void Real_input_is_movement_or_a_button()
    {
        Assert.False(GameServer.IsRealInput(new PlayerInput(1, 0, 0, 2f, InputButtons.None, 0.5f)));
        Assert.True(GameServer.IsRealInput(new PlayerInput(1, 0.2f, 0, 0, InputButtons.None)));
        Assert.True(GameServer.IsRealInput(new PlayerInput(1, 0, -1, 0, InputButtons.None)));
        Assert.True(GameServer.IsRealInput(new PlayerInput(1, 0, 0, 0, InputButtons.Fire)));
        Assert.False(GameServer.IsRealInput(new PlayerInput(1, float.NaN, 0, 0, InputButtons.None)));
    }

    [Fact]
    public void Repeated_same_nonce_hellos_do_not_keep_a_silent_player_alive()
    {
        var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new ServerOptions { PeerTimeout = TimeSpan.FromSeconds(2) });
        var raw = h.Net.Connect(At("10.0.0.7", 7000));
        ulong cookie = 0;
        Welcome? first = null;
        for (int i = 0; i < 1000 && first is null; i++)
        {
            raw.Send(Protocol.Write(new Hello(Protocol.Version, Harness.Fingerprint, h.Config.Hash(), 5, 0, cookie)));
            h.Step();
            while (raw.TryReceive(out var p))
                if (Protocol.Read(p) is Challenge c) cookie = c.Cookie;
                else if (Protocol.Read(p) is Welcome w) first = w;
        }
        Assert.NotNull(first);
        // The same client keeps asking every 250 ms (as if every Welcome were lost) and sends no input: still timed out.
        int welcomes = 0;
        for (int ms = 0; ms < 3000; ms++)
        {
            if (ms % 250 == 0) raw.Send(Protocol.Write(new Hello(Protocol.Version, Harness.Fingerprint, h.Config.Hash(), 5, 0, cookie)));
            h.Step();
            while (raw.TryReceive(out var p)) if (Protocol.Read(p) is Welcome w && w.PlayerId == first.PlayerId) welcomes++;
            if (h.Server.TimedOut > 0) break;
        }
        Assert.True(welcomes > 0, "answered with the same player");
        Assert.Equal(1, h.Server.TimedOut);
    }

    // ---- S3: a re-join is never a faster respawn ----

    static SimConfig Bolter() => new SimConfig() with
    {
        Damage = 22f, ShotCooldown = 0.133f, ClipSize = 30, ReserveSize = 270, ReloadTime = 1.3f, MaxRange = 250f, HealthMax = 100f, ShieldMax = 100f,
        RespawnTime = 5f,
    };

    static (float Yaw, float Pitch) AimAt(Vector3 from, Vector3 to)
    {
        var d = to - from;
        return (MathF.Atan2(-d.X, d.Y), MathF.Atan2(d.Z, new Vector2(d.X, d.Y).Length()));
    }

    /// <summary>Client 0 shoots the raw player <paramref name="target"/> until it dies; returns the server time of the death.</summary>
    static TimeSpan ShootDead(Harness h, int target)
    {
        var shooter = h.Clients[0];
        var feet = h.Server.World.Players[target].Position;
        var end = h.Clock.Now + TimeSpan.FromSeconds(10);
        while (h.Clock.Now < end)
        {
            h.Step((_, t) =>
            {
                var (yaw, pitch) = AimAt(shooter.Predicted.Position + new Vector3(0, 0, h.Config.EyeHeight), feet + new Vector3(0, 0, 1f));
                return new PlayerInput(t, 0, 0, yaw, InputButtons.Fire, pitch);
            });
            if (!h.Server.World.Players[target].Alive) return h.Clock.Now;
        }
        throw new Xunit.Sdk.XunitException("the target did not die");
    }

    [Fact]
    public void A_new_client_on_a_dead_players_address_waits_out_the_respawn_time()
    {
        var h = new Harness(Link, seed: 1, clients: 1, Bolter(), new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(1), JoinInterval = TimeSpan.Zero });
        h.Run(TimeSpan.FromSeconds(1));
        var raw = h.Net.Connect(At("10.0.0.9", 5000));
        var first = JoinRaw(h, raw, 1, TimeSpan.FromSeconds(1));
        int target = first.Welcome!.PlayerId;
        var died = ShootDead(h, target);

        // A new client (new nonce) on the same address while the old player is dead: no answer until it would have respawned.
        var idle = (int _, uint t) => new PlayerInput(t, 0, 0, 0, InputButtons.None);
        var early = JoinRaw(h, raw, 2, TimeSpan.FromSeconds(3), idle);
        Assert.Null(early.Welcome);
        Assert.True(h.Server.JoinsDeferred > 0);
        Assert.True(h.Server.World.Players.ContainsKey(target));
        var late = JoinRaw(h, raw, 2, TimeSpan.FromSeconds(4), idle);
        Assert.NotNull(late.Welcome);
        Assert.True(h.Clock.Now - died >= TimeSpan.FromSeconds(h.Config.RespawnTime - 0.1));
        Assert.False(h.Server.World.Players.ContainsKey(target));   // replaced
    }

    [Fact]
    public void Leaving_while_dead_holds_new_joins_from_that_address_for_the_respawn_time()
    {
        var h = new Harness(Link, seed: 1, clients: 1, Bolter(), new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(1), JoinInterval = TimeSpan.Zero });
        h.Run(TimeSpan.FromSeconds(1));
        var raw = h.Net.Connect(At("10.0.0.9", 5000));
        var first = JoinRaw(h, raw, 1, TimeSpan.FromSeconds(1));
        var died = ShootDead(h, first.Welcome!.PlayerId);
        for (int i = 0; i < 3; i++) raw.Send(Protocol.Write(new Leave(first.Welcome.Session)));
        var idle = (int _, uint t) => new PlayerInput(t, 0, 0, 0, InputButtons.None);
        var other = h.Net.Connect(At("10.0.0.9", 5001));   // same address, new port: a restarted client
        var early = JoinRaw(h, other, 3, TimeSpan.FromSeconds(3), idle);
        Assert.Null(early.Welcome);
        var late = JoinRaw(h, other, 3, TimeSpan.FromSeconds(4), idle);
        Assert.NotNull(late.Welcome);
        Assert.True(h.Clock.Now - died >= TimeSpan.FromSeconds(h.Config.RespawnTime - 0.1));
    }

    // ---- M3: crash isolation ----

    [Fact]
    public void A_throwing_packet_handler_drops_only_that_peer_and_the_loop_goes_on()
    {
        var h = new Harness(Link, seed: 1, clients: 2);
        h.Run(TimeSpan.FromSeconds(1));
        int bad = h.Clients[0].PlayerId, good = h.Clients[1].PlayerId;
        var notes = new List<string>();
        h.Server.OperatorLog += notes.Add;
        h.Server.PacketHookForTests = (connection, message) =>
        {
            if (connection == 0 && message is InputBatch) throw new InvalidOperationException("handler bug");
        };
        var start = h.Server.World.Players[good].Position;
        uint tick = h.Server.World.Tick;
        h.Run(TimeSpan.FromSeconds(1), (i, t) => new PlayerInput(t, 0, 1, 0, InputButtons.None));
        Assert.False(h.Server.World.Players.ContainsKey(bad));
        Assert.True(Vector3.Distance(start, h.Server.World.Players[good].Position) > 1f, "the other player kept playing");
        Assert.True(h.Server.World.Tick > tick + 20);
        Assert.True(h.Server.Faults >= 1);
        Assert.Contains(notes, n => n.Contains("handler bug"));
        Assert.Contains(notes, n => n.Contains($"player {bad} left") && n.Contains("packet failed"));
    }

    [Fact]
    public void A_throwing_tick_is_logged_and_the_server_keeps_ticking_and_sending()
    {
        var h = new Harness(Link, seed: 1, clients: 1);
        h.Run(TimeSpan.FromSeconds(1));
        h.Server.Stepped += _ => throw new InvalidOperationException("tick bug");
        uint tick = h.Server.World.Tick;
        int snapshots = h.Clients[0].CorrectionErrors.Count;
        h.Run(TimeSpan.FromSeconds(1));
        Assert.True(h.Server.World.Tick >= tick + 25);
        Assert.True(h.Server.Faults >= 25);
        Assert.Equal(ClientStatus.Connected, h.Clients[0].Status);
        Assert.True(h.Clients[0].CorrectionErrors.Count > snapshots + 10, "snapshots still went out");
    }

    [Fact]
    public void A_throwing_log_sink_does_not_stop_joins()
    {
        var h = new Harness(Link, seed: 1, clients: 0);
        h.Server.OperatorLog += _ => throw new IOException("stdout closed");
        var c = h.AddClient(nonce: 1);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Connected, c.Status);
        Assert.Equal(0, h.Server.Faults);
    }

    // ---- S1: tick catch-up cap ----

    [Fact]
    public void A_long_stall_runs_at_most_five_ticks_then_resyncs()
    {
        var h = new Harness(Link, seed: 1, clients: 0);
        h.Run(TimeSpan.FromSeconds(1));
        var notes = new List<string>();
        h.Server.OperatorLog += notes.Add;
        uint before = h.Server.World.Tick;
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        h.Server.Update();
        Assert.Equal(before + 5, h.Server.World.Tick);
        Assert.InRange(h.Server.SkippedTicks, 290, 300);
        Assert.Contains(notes, n => n.Contains("skipped"));
        // Back to one tick per tick length afterwards.
        h.Clock.Advance(TimeSpan.FromSeconds(h.Config.TickSeconds));
        h.Server.Update();
        Assert.InRange(h.Server.World.Tick, before + 6, before + 7);
        uint resumed = h.Server.World.Tick;
        h.Run(TimeSpan.FromSeconds(1));
        Assert.InRange(h.Server.World.Tick, resumed + 29, resumed + 31);
    }

    // ---- S5: bans ----

    [Fact]
    public void Ban_list_matches_addresses_and_cidr_ranges()
    {
        var errors = new List<string>();
        var bans = BanList.Parse(["# comment", "", "203.0.113.7", "198.51.100.0/24  # a range", "2001:db8::/32", "::ffff:192.0.2.0/120", "nonsense", "10.0.0.0/33", "12345", "167772160/8"], errors);
        Assert.Equal(4, bans.Count);
        Assert.Equal(4, errors.Count);   // bare numbers are not taken as addresses
        Assert.False(bans.Contains(IPAddress.Parse("0.0.48.57")));
        Assert.True(bans.Contains(IPAddress.Parse("203.0.113.7")));
        Assert.False(bans.Contains(IPAddress.Parse("203.0.113.8")));
        Assert.True(bans.Contains(IPAddress.Parse("198.51.100.200")));
        Assert.False(bans.Contains(IPAddress.Parse("198.51.101.1")));
        Assert.True(bans.Contains(IPAddress.Parse("2001:db8:1::5")));
        Assert.True(bans.Contains(IPAddress.Parse("192.0.2.77")));
        Assert.True(bans.Contains(IPAddress.Parse("::ffff:203.0.113.7")));   // an IPv4-mapped source on a dual-mode socket
        Assert.False(BanList.Empty.Contains(IPAddress.Loopback));
    }

    [Fact]
    public void Banned_sources_get_no_reply_and_the_file_is_read_again_when_it_changes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-reimpl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "bans.txt");
        try
        {
            File.WriteAllText(file, "10.1.0.0/16\n");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
            var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new ServerOptions { BanFile = file, BanFileCheckInterval = TimeSpan.FromMilliseconds(100), JoinInterval = TimeSpan.Zero });
            var banned = h.Net.Connect(At("10.1.2.3", 4000));
            var result = JoinRaw(h, banned, 1, TimeSpan.FromSeconds(1));
            Assert.False(result.AnyReply);   // not even a Challenge
            Assert.True(h.Server.BannedPackets > 0);
            var fine = AddressedClient(h, At("10.2.0.1", 4000), 2);
            h.Run(TimeSpan.FromSeconds(1));
            Assert.Equal(ClientStatus.Connected, fine.Status);

            // The operator bans the connected player and lifts the old ban: it is dropped, the other address gets in.
            File.WriteAllText(file, "# new list\n10.2.0.1\n");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-0.5));
            h.Run(TimeSpan.FromMilliseconds(300));
            Assert.False(h.Server.World.Players.ContainsKey(fine.PlayerId));
            var now = JoinRaw(h, banned, 3, TimeSpan.FromSeconds(1));
            Assert.NotNull(now.Welcome);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_missing_ban_file_bans_no_one_and_is_picked_up_when_it_appears()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-reimpl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "bans.txt");
        try
        {
            var file = new BanFile(path);
            Assert.True(file.Refresh());
            Assert.False(file.Exists);
            Assert.Equal(0, file.Bans.Count);
            Assert.False(file.Refresh());   // unchanged
            File.WriteAllText(path, "192.0.2.1\nbad line\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
            Assert.True(file.Refresh());
            Assert.True(file.Exists);
            Assert.True(file.Bans.Contains(IPAddress.Parse("192.0.2.1")));
            Assert.Single(file.Errors);
            Assert.False(file.Refresh());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- S7: content refusal ----

    [Fact]
    public void Content_refusal_names_the_servers_build_and_the_log_names_both_fingerprints()
    {
        var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new ServerOptions { BuildHint = "Steam build 1234567" });
        var notes = new List<string>();
        h.Server.OperatorLog += notes.Add;
        var c = h.AddClient(fingerprint: 0xABCDEF0123456789UL, nonce: 1);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Rejected, c.Status);
        Assert.Equal(RejectReason.ContentMismatch, c.RejectedBecause);
        Assert.Contains("Steam build 1234567", c.RejectDetail);
        Assert.Contains($"{Harness.Fingerprint:X16}", c.RejectDetail);
        Assert.Contains(notes, n => n.Contains("ABCDEF0123456789") && n.Contains($"{Harness.Fingerprint:X16}") && n.Contains("ContentMismatch"));
    }

    [Fact]
    public void Build_hint_comes_from_the_steam_manifest_next_to_the_install()
    {
        string root = Path.Combine(Path.GetTempPath(), "sm-reimpl-tests", Guid.NewGuid().ToString("N"));
        string install = Path.Combine(root, "steamapps", "common", "Game");
        Directory.CreateDirectory(install);
        try
        {
            Assert.Null(ServerCommandLine.BuildHint(install));
            File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_55150.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"55150\"\n\t\"buildid\"\t\t\"7412325\"\n}\n");
            Assert.Equal("Steam build 7412325", ServerCommandLine.BuildHint(install));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---- review follow-ups ----

    [Fact]
    public void An_ipv4_mapped_loopback_address_is_exempt_too()
    {
        var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new ServerOptions { MaxPlayersPerAddress = 1, IdleKick = TimeSpan.FromSeconds(2) });
        var a = AddressedClient(h, At("::ffff:127.0.0.1", 40000), 1);
        var b = AddressedClient(h, At("::ffff:127.0.0.1", 40001), 2);
        h.Run(TimeSpan.FromSeconds(4));
        Assert.Equal(ClientStatus.Connected, a.Status);
        Assert.Equal(ClientStatus.Connected, b.Status);
        Assert.Equal(2, h.Server.World.Players.Count);
        Assert.Equal(0, h.Server.IdleKicked);
    }

    [Fact]
    public void Connections_without_an_address_are_not_idle_kicked()
    {
        var h = new Harness(Link, seed: 1, clients: 1, serverOptions: new ServerOptions { IdleKick = TimeSpan.FromSeconds(2) });
        h.Run(TimeSpan.FromSeconds(5));
        Assert.Single(h.Server.World.Players);
        Assert.Equal(0, h.Server.IdleKicked);
    }

    [Fact]
    public void Customizer_use_counts_as_activity_for_the_idle_kick()
    {
        var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new ServerOptions { IdleKick = TimeSpan.FromSeconds(3), PeerTimeout = TimeSpan.FromMinutes(1) });
        var raw = h.Net.Connect(At("10.0.0.4", 4000));
        var joined = JoinRaw(h, raw, 1, TimeSpan.FromSeconds(1)).Welcome!;
        // A player in the class creator: no movement, a Setup (Deploy) every 1.5 s.
        ushort sequence = 1;
        for (int ms = 0; ms < 7000; ms++)
        {
            if (ms % 1500 == 0) raw.Send(Protocol.Write(new Setup(joined.Session, sequence++, LoadoutChoice.Default, AppearanceCode.Default, AppearanceCode.Default)));
            h.Step();
            while (raw.TryReceive(out _)) { }
        }
        Assert.True(h.Server.World.Players.ContainsKey(joined.PlayerId));
        h.Run(TimeSpan.FromSeconds(4));   // the customizer closed, still nothing: dropped
        Assert.False(h.Server.World.Players.ContainsKey(joined.PlayerId));
        Assert.Equal(1, h.Server.IdleKicked);
    }

    [Fact]
    public void A_hello_without_a_valid_cookie_never_touches_the_join_limits()
    {
        var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new ServerOptions { MaxPlayersPerAddress = 1, JoinInterval = TimeSpan.FromSeconds(5) });
        // A spoofer sends Hellos from 10.0.0.8 (it never sees the Challenges): no cookie, then a forged one.
        for (int i = 0; i < 50; i++)
        {
            var spoofed = h.Net.Connect(At("10.0.0.8", 20000 + i));
            spoofed.Send(Protocol.Write(new Hello(Protocol.Version, Harness.Fingerprint, h.Config.Hash(), (uint)(500 + i), 0, i == 0 ? 0UL : 0xDEADBEEFUL + (ulong)i)));
        }
        h.Run(TimeSpan.FromMilliseconds(200));
        Assert.Equal((0, 0), (h.Server.JoinsDeferred, h.Server.Rejected));
        // The real player at that address gets in at once: neither the cap nor the join window was used up.
        var real = AddressedClient(h, At("10.0.0.8", 30000), 1);
        h.Run(TimeSpan.FromMilliseconds(300));
        Assert.Equal(ClientStatus.Connected, real.Status);
        Assert.Equal(0, h.Server.JoinsDeferred);
    }

    [Fact]
    public void A_failing_snapshot_drops_only_its_recipient_and_the_shots_still_clear()
    {
        var h = new Harness(Link, seed: 1, clients: 3);
        h.Run(TimeSpan.FromSeconds(1));
        int bad = h.Clients[1].PlayerId;
        h.Server.SnapshotHookForTests = id => { if (id == bad) throw new InvalidOperationException("snapshot bug"); };
        var counts = h.Clients.Select(c => c.CorrectionErrors.Count).ToArray();
        h.Run(TimeSpan.FromSeconds(1));
        Assert.False(h.Server.World.Players.ContainsKey(bad));
        Assert.True(h.Clients[0].CorrectionErrors.Count > counts[0] + 10, "the others still got snapshots");
        Assert.True(h.Clients[2].CorrectionErrors.Count > counts[2] + 10, "the others still got snapshots");
        Assert.True(h.Server.Faults >= 1);
        Assert.Null(h.Server.FatalError);
    }

    [Fact]
    public void Ticks_failing_two_seconds_in_a_row_are_fatal_and_one_success_resets_the_count()
    {
        var h = new Harness(Link, seed: 1, clients: 1);
        h.Run(TimeSpan.FromSeconds(1));
        var notes = new List<string>();
        h.Server.OperatorLog += notes.Add;
        bool failing = true;
        int ticks = 0;
        h.Server.Stepped += _ => { ticks++; if (failing && ticks % 50 != 0) throw new InvalidOperationException("tick bug"); };
        h.Run(TimeSpan.FromSeconds(3));   // 49 failures, then one good tick, again and again: never 60 in a row
        Assert.Null(h.Server.FatalError);
        h.Server.Stepped += _ => throw new InvalidOperationException("broken for good");
        h.Run(TimeSpan.FromSeconds(2.2));
        Assert.NotNull(h.Server.FatalError);
        Assert.Contains("60 ticks in a row", h.Server.FatalError);
        Assert.Contains(notes, n => n.StartsWith("FATAL: "));
    }

    // ---- flag parsing ----

    [Fact]
    public void Command_line_defaults_and_values()
    {
        Assert.True(ServerCommandLine.TryParse([], out var d, out _));
        Assert.Equal((27015, 4, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(300), (string?)null), (d.Port, d.MaxPerIp, d.JoinInterval, d.IdleKick, d.BanFile));
        Assert.Equal(((float?)null, 0f, false), (d.CountdownSeconds, d.PickupTestSeconds, d.HonorSideRequests));
        Assert.True(ServerCommandLine.TryParse(["--countdown", "0", "--pickup-test", "2.5", "--honor-side-requests"], out var flow, out var flowError), flowError);
        Assert.Equal(((float?)0f, 2.5f, true), (flow.CountdownSeconds, flow.PickupTestSeconds, flow.HonorSideRequests));
        Assert.False(ServerCommandLine.TryParse(["--countdown", "601"], out _, out _));
        Assert.False(ServerCommandLine.TryParse(["--pickup-test", "0"], out _, out _));
        Assert.Equal(IPAddress.Any, d.Bind);
        Assert.True(ServerCommandLine.TryParse(
            ["--port", "27161", "--bind", "127.0.0.1", "--map", "none", "--mode", "seize", "--score-limit", "300", "--time-limit", "15", "--friendly-fire", "on",
             "--no-custom-emblems", "--max-per-ip", "0", "--join-interval", "2.5", "--idle-kick", "0", "--ban-file", "bans.txt", "--unlocks", "all"], out var a, out var error), error);
        Assert.Equal((27161, "none", GameMode.SeizeGround, 300, 15f, true, false), (a.Port, a.Map, a.Mode, a.ScoreLimit, a.TimeLimitMinutes, a.FriendlyFire, a.CustomEmblems));
        Assert.Equal((0, TimeSpan.FromSeconds(2.5), TimeSpan.Zero, "bans.txt"), (a.MaxPerIp, a.JoinInterval, a.IdleKick, a.BanFile));
        Assert.Equal(IPAddress.Loopback, a.Bind);
        Assert.True(ServerCommandLine.TryParse(["--mode", "ffa"], out var ffa, out _));
        Assert.False(ffa.Teams);
        Assert.False(d.LimitLoopback);
        Assert.True(ServerCommandLine.TryParse(["--limit-loopback", "--port", "27162"], out var limited, out _));
        Assert.True(limited.LimitLoopback);
        Assert.Equal(27162, limited.Port);
        Assert.Contains("--limit-loopback", ServerCommandLine.Usage);
        Assert.True(ServerCommandLine.TryParse(["--help"], out var help, out _));
        Assert.True(help.Help);
        Assert.Contains("--ban-file", ServerCommandLine.Usage);
        Assert.Contains("--idle-kick", ServerCommandLine.Usage);
    }

    [Theory]
    [InlineData(new[] { "--prot", "27015" }, "unknown option '--prot'")]
    [InlineData(new[] { "--port" }, "--port needs a value")]
    [InlineData(new[] { "--port", "--map", "none" }, "--port needs a value")]
    [InlineData(new[] { "--port", "abc" }, "--port 'abc'")]
    [InlineData(new[] { "--port", "70000" }, "--port '70000'")]
    [InlineData(new[] { "--bind", "localhost" }, "--bind 'localhost'")]
    [InlineData(new[] { "--mode", "ctf" }, "--mode 'ctf'")]
    [InlineData(new[] { "--score-limit", "0" }, "--score-limit '0'")]
    [InlineData(new[] { "--time-limit", "-1" }, "--time-limit '-1'")]
    [InlineData(new[] { "--friendly-fire", "yes" }, "--friendly-fire 'yes'")]
    [InlineData(new[] { "--unlocks", "none" }, "--unlocks 'none'")]
    [InlineData(new[] { "--max-per-ip", "-1" }, "--max-per-ip '-1'")]
    [InlineData(new[] { "--idle-kick", "NaN" }, "--idle-kick 'NaN'")]
    [InlineData(new[] { "--join-interval", "soon" }, "--join-interval 'soon'")]
    [InlineData(new[] { "--ban-file" }, "--ban-file needs a value")]
    [InlineData(new[] { "27015" }, "unexpected argument '27015'")]
    public void Command_line_errors_are_refused(string[] args, string expected)
    {
        Assert.False(ServerCommandLine.TryParse(args, out _, out var error));
        Assert.Contains(expected, error);
        Assert.Equal(2, ServerCommandLine.UsageError);
    }
}
