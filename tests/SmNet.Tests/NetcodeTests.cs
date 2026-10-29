using System.Numerics;
using SmNet;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>M4 acceptance: prediction, reconciliation, interpolation and determinism under simulated network conditions.</summary>
public class NetcodeTests(ITestOutputHelper output)
{
    static readonly LinkConditions Rtt150 = new(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);

    [Theory]
    [InlineData(42)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(2026)]
    public void TwoClients_150msRtt_5pctLoss_PredictionErrorWithinThreshold(int seed)
    {
        var h = new Harness(Rtt150, seed: seed, clients: 2);
        var positionsFinite = true;
        h.Run(TimeSpan.FromSeconds(22), Harness.Scripted, () =>
        {
            foreach (var p in h.Server.World.Players.Values) positionsFinite &= Harness.Finite(p.Position);
        });

        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        Assert.True(positionsFinite);
        Assert.True(h.Server.World.Tick >= 20 * h.Config.TickRate, $"server only reached tick {h.Server.World.Tick}");

        for (int i = 0; i < h.Clients.Count; i++)
        {
            var c = h.Clients[i];
            var e = c.CorrectionErrors;
            double p95 = Harness.Percentile(e, 0.95), max = e.Count == 0 ? double.NaN : e.Max();
            int nonZero = e.Count(x => x > 1e-4f);
            output.WriteLine($"client {i} (player {c.PlayerId}): handshake RTT {c.HandshakeRoundTrip.TotalMilliseconds:F0} ms, " +
                $"{e.Count} corrections, p95 {p95:F4} m, max {max:F4} m, mean {e.Average():F5} m, {nonZero} > 0.1 mm");
            Assert.True(e.Count >= 300, $"client {i}: only {e.Count} correction samples");
            Assert.True(p95 < 0.05, $"client {i}: p95 correction {p95:F4} m >= 0.05 m");
            Assert.True(max < 0.5, $"client {i}: max correction {max:F4} m >= 0.5 m");
        }
        output.WriteLine($"packets sent {h.Net.PacketsSent}, dropped {h.Net.PacketsDropped} ({100.0 * h.Net.PacketsDropped / h.Net.PacketsSent:F1}%)");
        // The players actually moved (a trivially static run would pass the error thresholds).
        foreach (var p in h.Server.World.Players.Values)
            output.WriteLine($"server player {p.PlayerId} final position {p.Position}");
        Assert.All(h.Server.World.Players.Values, p => Assert.True(new Vector2(p.Position.X, p.Position.Y).Length() > 1f));
    }

    /// <summary>
    /// Negative control: a 600 ms total outage of client-to-server traffic makes the server repeat stale inputs, so the
    /// client must record non-zero correction errors and then converge again. Shows the metric is not vacuous.
    /// </summary>
    [Fact]
    public void CorrectionMetric_DetectsMispredictionDuringOutage_ThenRecovers()
    {
        var h = new Harness(Rtt150, seed: 5, clients: 2);
        h.Run(TimeSpan.FromSeconds(5), Harness.Scripted);
        int before = h.Clients[0].CorrectionErrors.Count;
        h.Net.Conditions = h.Net.Conditions with { Loss = 1.0 };
        h.Run(TimeSpan.FromMilliseconds(600), Harness.Scripted);
        h.Net.Conditions = Rtt150;
        h.Run(TimeSpan.FromSeconds(5), Harness.Scripted);
        var e = h.Clients[0].CorrectionErrors;
        float duringMax = e.Skip(before).Take(20).Max();
        var tail = e.Skip(e.Count - 40).ToList();
        output.WriteLine($"outage: max error right after outage {duringMax:F4} m; last 40 max {tail.Max():F4} m");
        Assert.True(duringMax > 0.01f, "an input outage should cause visible corrections");
        Assert.True(tail.Max() < 0.05f, "corrections should converge after the outage");
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
    }

    [Fact]
    public void RemoteInterpolation_TracksServerTruth()
    {
        var h = new Harness(Rtt150, seed: 7, clients: 2);
        var truth = new Dictionary<uint, Vector3>();   // server tick -> position of client B's player
        var errors = new List<float>();
        int clampedSamples = 0;
        uint lastRecorded = uint.MaxValue;
        int stepIndex = 0;

        h.Run(TimeSpan.FromSeconds(22), Harness.Scripted, () =>
        {
            stepIndex++;
            var a = h.Clients[0]; var b = h.Clients[1];
            if (b.PlayerId > 0 && h.Server.World.Players.TryGetValue(b.PlayerId, out var sb) && h.Server.World.Tick != lastRecorded)
            {
                lastRecorded = h.Server.World.Tick;
                truth[lastRecorded] = sb.Position;
            }
            if (stepIndex < 3000 || a.Status != ClientStatus.Connected || stepIndex % 5 != 0) return;
            var (renderTick, players) = a.RemotePlayers();
            var seen = players.Where(p => p.PlayerId == b.PlayerId).ToList();
            Assert.Single(seen);
            uint t0 = (uint)Math.Floor(renderTick);
            if (!truth.TryGetValue(t0, out var p0) || !truth.TryGetValue(t0 + 1, out var p1)) { clampedSamples++; return; }
            var expected = Vector3.Lerp(p0, p1, (float)(renderTick - t0));
            errors.Add(Vector3.Distance(expected, seen[0].Position));
        });

        double p95 = Harness.Percentile(errors, 0.95), p99 = Harness.Percentile(errors, 0.99), max = errors.Max();
        output.WriteLine($"interpolation: {errors.Count} samples, p95 {p95:F4} m, p99 {p99:F4} m, max {max:F4} m, mean {errors.Average():F4} m; {clampedSamples} samples without truth");
        Assert.True(errors.Count > 3000);
        Assert.True(p95 < 0.10, $"interpolation p95 {p95:F4} m >= 0.10 m");
    }

    [Fact]
    public void SameSeedAndInputs_GiveIdenticalServerPositions()
    {
        static Dictionary<int, Vector3> RunOnce()
        {
            var h = new Harness(Rtt150, seed: 1234, clients: 2);
            h.Run(TimeSpan.FromSeconds(10), Harness.Scripted);
            return h.Server.World.Players.ToDictionary(p => p.Key, p => p.Value.Position);
        }
        var first = RunOnce();
        var second = RunOnce();
        Assert.Equal(2, first.Count);
        foreach (var (id, pos) in first)
        {
            output.WriteLine($"player {id}: {pos} vs {second[id]}");
            Assert.Equal(pos, second[id]);   // exact, bit for bit
        }
    }

    [Fact]
    public void HarshNetwork_200msRtt_40msJitter_10pctLoss_StaysConnected()
    {
        var link = new LinkConditions(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(40), 0.10);
        var h = new Harness(link, seed: 99, clients: 2);
        bool finite = true;
        h.Run(TimeSpan.FromSeconds(22), Harness.Scripted, () =>
        {
            foreach (var p in h.Server.World.Players.Values) finite &= Harness.Finite(p.Position);
            foreach (var c in h.Clients) finite &= Harness.Finite(c.Predicted.Position);
        });
        for (int i = 0; i < h.Clients.Count; i++)
        {
            var e = h.Clients[i].CorrectionErrors;
            output.WriteLine($"[harsh, reported only] client {i}: {e.Count} corrections, p95 {Harness.Percentile(e, 0.95):F4} m, " +
                $"max {(e.Count > 0 ? e.Max() : float.NaN):F4} m, mean {(e.Count > 0 ? e.Average() : float.NaN):F5} m");
        }
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        Assert.Equal(2, h.Server.Players.Count);
        Assert.True(finite);
    }
}
