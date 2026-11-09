using System.Numerics;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// net/responsive probe (engine-free; runs only with <c>SM_NET_PROBE=1</c>, or with a path in <c>SM_NET_PROBE_OUT</c> to append its lines to): a server and two clients on
/// the loopback network, the clients updated at 60 frames a second (16/17 ms, each its own phase), the server every millisecond. The shooter strafes in short runs (an
/// input edge at each start and stop) and fires full auto at the target as it draws it; the target strafes. Every stage of <see cref="NetLatencyReadout"/> is read from the
/// shooter, the own drawn position as the Godot client draws it (<see cref="GameClient.DrawPair"/> and <see cref="ViewSmoothing.Interpolate"/>). Links: 127.0.0.1 (none),
/// 60 ms and 80 ms round trips with jitter, and a handshake made during a 400 ms round trip that drops to 60 ms (the warm-up a map load gives).
/// </summary>
public class NetLatencyProbe(ITestOutputHelper output)
{
    public sealed class ProbeFactAttribute : FactAttribute
    {
        public ProbeFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SM_NET_PROBE") != "1" && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SM_NET_PROBE_OUT")))
                Skip = "net/responsive probe: set SM_NET_PROBE=1 (or SM_NET_PROBE_OUT=<file>)";
        }
    }

    /// <summary>A named link: one-way latency and jitter (ms) and loss; <paramref name="HandshakeOneWay"/> &gt; 0: the link until the client is connected.</summary>
    public sealed record Link(string Name, double OneWay, double Jitter, double Loss, double HandshakeOneWay = 0);

    public static readonly Link[] Links =
    [
        new("local", 0, 0, 0),
        new("rtt60", 30, 5, 0),
        new("rtt80", 40, 10, 0.01),
        new("warmup400-rtt60", 30, 5, 0, HandshakeOneWay: 200),
    ];

    /// <summary>The result of one run: the shooter's readout and the server's bytes per client per second.</summary>
    public sealed record Run(NetLatencyReadout Readout, double KBytesPerClient, double HandshakeMs, List<(double At, int Lead)> Leads, int Corrections, double CorrectionP95);

    /// <summary>
    /// One probe run. <paramref name="clientOptions"/> and <paramref name="serverOptions"/> choose the behaviour (the old switches or the new defaults).
    /// </summary>
    public static Run Measure(Link link, double seconds, Func<ClientOptions, ClientOptions>? clientOptions = null, ServerOptions? serverOptions = null, int seed = 11)
    {
        var config = new SimConfig { HealthMax = 1e6f, ShieldMax = 1e6f, ClipSize = 100_000, ReserveSize = 100_000 };
        var clock = new ManualClock();
        LinkConditions Of(double oneWay) => new(TimeSpan.FromMilliseconds(oneWay), TimeSpan.FromMilliseconds(link.Jitter), link.Loss);
        var net = new LoopbackNetwork(clock, link.HandshakeOneWay > 0 ? Of(link.HandshakeOneWay) with { Jitter = TimeSpan.Zero } : Of(link.OneWay), seed);
        var counter = new CountingServer(net.Server);
        var server = new GameServer(config, Harness.Fingerprint, counter, clock, serverOptions);

        var baseOptions = new ClientOptions { LatencyReadout = 1e9 };
        var shooterOptions = (clientOptions?.Invoke(baseOptions) ?? baseOptions);
        var shooter = new GameClient(config, Harness.Fingerprint, net.Connect(), clock, shooterOptions, nonce: 100);
        var target = new GameClient(config, Harness.Fingerprint, net.Connect(), clock, clientOptions?.Invoke(new ClientOptions()) ?? new ClientOptions(), nonce: 101);
        var clients = new[] { shooter, target };
        double[] nextFrame = [3.0, 9.0];
        int[] frameIndex = [0, 0];
        bool linkSwitched = link.HandshakeOneWay <= 0;
        var leads = new List<(double, int)>();
        // The shooter strafes: 0.62 s right, 0.47 s still, 0.58 s left, 0.51 s still (lengths off the 33 ms tick and the 16.7 ms frame), firing throughout.
        double[] phases = [0.62, 0.47, 0.58, 0.51];
        float[] moves = [1f, 0f, -1f, 0f];
        float ShooterMove(double t)
        {
            double cycle = phases.Sum(), u = t % cycle;
            for (int i = 0; i < phases.Length; i++) { if (u < phases[i]) return moves[i]; u -= phases[i]; }
            return 0f;
        }
        float TargetMove(double t) => (int)(t / 1.3) % 2 == 0 ? 0.6f : -0.6f;
        float lastMove = float.NaN;
        double startCountAt = 3.0, total = seconds + startCountAt;
        long countedFrom = 0;
        for (int ms = 1; ms <= total * 1000; ms++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            server.Update();
            double now = clock.Now.TotalMilliseconds;
            if (!linkSwitched && shooter.Status == ClientStatus.Connected && target.Status == ClientStatus.Connected) { net.Conditions = Of(link.OneWay); linkSwitched = true; }
            if (ms == (int)(startCountAt * 1000)) countedFrom = counter.Bytes;
            for (int c = 0; c < clients.Length; c++)
            {
                if (now < nextFrame[c]) continue;
                nextFrame[c] += frameIndex[c]++ % 3 == 2 ? 16 : 17;   // 60 fps: 17, 17, 16
                var client = clients[c];
                double t = now / 1000.0;
                if (c == 0)
                {
                    float move = ShooterMove(t);
                    bool edge = !float.IsNaN(lastMove) && move != lastMove;
                    lastMove = move;
                    client.Update(tick => AimAt(client, config, tick, move), tickNow: edge);
                    if (client.Status == ClientStatus.Connected && client.Predicted.Alive)
                    {
                        var (from, to, fraction) = client.DrawPair();
                        client.NoteOwnDrawn(ViewSmoothing.Interpolate(from.Position, to.Position, fraction) + client.DrawBlendOffset());
                    }
                }
                else client.Update(tick => new PlayerInput(tick, TargetMove(t), 0, MathF.PI, InputButtons.None));
            }
            if (shooter.Status == ClientStatus.Connected && ms % 100 == 0 && shooter.Latency?.All("lead") is { Count: > 0 } l) leads.Add((now / 1000.0, (int)l[^1]));
            if (shooter.TakeHits() is { } hits && hits.Hits > 0) shooter.NoteHitMarker(predicted: false);
        }
        double kb = (counter.Bytes - countedFrom) / 1000.0 / seconds / 2;
        var errors = shooter.CorrectionErrors;
        return new Run(shooter.Latency!, kb, shooter.HandshakeRoundTrip.TotalMilliseconds, leads, errors.Count(e => e > 1e-3f), errors.Count == 0 ? 0 : Harness.Percentile(errors, 0.95));
    }

    /// <summary>The shooter's input: the strafe, Fire held, aimed at the target's chest as drawn (the view of this client).</summary>
    static PlayerInput AimAt(GameClient client, SimConfig config, uint tick, float move)
    {
        var me = client.Predicted;
        var (_, others) = client.RemotePlayers();
        if (others.Count == 0 || !me.Alive) return new PlayerInput(tick, move, 0, 0f, InputButtons.None);
        var eye = Combat.EyePosition(me, config);
        var chest = others[0].Position + new Vector3(0, 0, 1.2f);
        var d = chest - eye;
        float yaw = MathF.Atan2(-d.X, d.Y), pitch = MathF.Atan2(d.Z, new Vector2(d.X, d.Y).Length());
        return new PlayerInput(tick, move, 0, yaw, InputButtons.Fire, pitch).WithShotDirection(d);
    }

    sealed class CountingServer(IServerTransport inner) : IServerTransport
    {
        public long Bytes { get; private set; }
        public void Send(int connection, byte[] packet) { Bytes += packet.Length + 28; inner.Send(connection, packet); }   // + IPv4 and UDP headers
        public bool TryReceive(out int connection, out byte[] packet) => inner.TryReceive(out connection, out packet);
        public void Disconnect(int connection) => inner.Disconnect(connection);
        public void Pin(int connection) => inner.Pin(connection);
    }

    /// <summary>The lines of one mode over every link.</summary>
    public static IEnumerable<string> Report(string mode, Func<ClientOptions, ClientOptions>? clientOptions, ServerOptions? serverOptions, double seconds = 30)
    {
        foreach (var link in Links)
        {
            var run = Measure(link, seconds, clientOptions, serverOptions);
            var r = run.Readout;
            string Stage(string s) => $"{s}: {NetLatencyReadout.Stats(r.All(s))}";
            yield return $"[{mode}] {link.Name}: handshake {run.HandshakeMs:F0} ms; server->client {run.KBytesPerClient:F1} KB/s; corrections > 1 mm {run.Corrections} (p95 {run.CorrectionP95 * 1000:F1} mm)";
            foreach (var s in NetLatencyReadout.Stages) yield return $"    {Stage(s)}";
            if (link.HandshakeOneWay > 0)
                yield return "    lead after connect (s: ticks): " + string.Join(", ", run.Leads.Where((_, i) => i % 5 == 0).Take(24).Select(x => $"{x.At:F1}: {x.Lead}"));
        }
    }

    void Emit(IEnumerable<string> lines)
    {
        string? path = Environment.GetEnvironmentVariable("SM_NET_PROBE_OUT");
        foreach (var line in lines)
        {
            output.WriteLine(line);
            if (!string.IsNullOrEmpty(path)) File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    public static double Median(IEnumerable<double> values) => NetLatencyReadout.Quantile(values.ToList(), 0.5);

    /// <summary>Every net/responsive switch back to the behaviour before it (client and server).</summary>
    public static ClientOptions OldNet(ClientOptions o) => o with { SmoothInputLead = false, DrawOwnAhead = false, AdaptiveInterpolation = false, MaxExtrapolationTicks = 0 };

    /// <summary>net/responsive's first step (the integer lead at 2 ticks, two snapshot intervals of interpolation, no extrapolation): the midpoint for the report.</summary>
    public static ClientOptions FirstStep(ClientOptions o) => o with { PreciseLead = false, MinInterpolationDelay = TimeSpan.FromSeconds(2.0 / 30.0), MaxExtrapolationTicks = 0 };
    public static readonly ServerOptions OldServer = new() { SnapshotRate = 20 };

    [ProbeFact]
    public void Current_defaults() => Emit(Report(Environment.GetEnvironmentVariable("SM_NET_PROBE_LABEL") ?? "defaults", null, null));

    [ProbeFact]
    public void Old_switches() => Emit(Report("old switches", OldNet, OldServer));

    [ProbeFact]
    public void First_step() => Emit(Report("first step (2-tick lead, 67 ms)", FirstStep, null));

    /// <summary>Classic 8v8 bandwidth: 16 clients running and firing full auto (scripted), server to client bytes a second per client (IPv4 + UDP headers counted).</summary>
    public static double Bandwidth(int players, ServerOptions? serverOptions, double seconds = 10, int seed = 3)
    {
        var config = new SimConfig { HealthMax = 1e6f, ShieldMax = 1e6f, ClipSize = 100_000, ReserveSize = 100_000 };
        var clock = new ManualClock();
        var net = new LoopbackNetwork(clock, new LinkConditions(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(3), 0), seed);
        var counter = new CountingServer(net.Server);
        var server = new GameServer(config, Harness.Fingerprint, counter, clock, serverOptions);
        var clients = Enumerable.Range(0, players).Select(i => new GameClient(config, Harness.Fingerprint, net.Connect(), clock, new ClientOptions(), nonce: (uint)(200 + i))).ToList();
        long from = 0;
        int total = (int)((seconds + 3) * 1000);
        for (int ms = 1; ms <= total; ms++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            server.Update();
            if (ms == 3000) from = counter.Bytes;
            if (ms % 16 != 0) continue;
            for (int i = 0; i < clients.Count; i++)
            {
                int index = i;
                clients[i].Update(t => Harness.Scripted(index, t) with { Buttons = Harness.Scripted(index, t).Buttons | InputButtons.Fire });
            }
        }
        Assert.All(clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        return (counter.Bytes - from) / 1000.0 / seconds / players;
    }

    [ProbeFact]
    public void Classic_8v8_bandwidth() => Emit([
        $"[bandwidth] 16 players running and firing: 20 Hz snapshots {Bandwidth(16, OldServer):F1} KB/s per client; 30 Hz {Bandwidth(16, null):F1} KB/s per client (IPv4 + UDP headers counted)"]);
}
