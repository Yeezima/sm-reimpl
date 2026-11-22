using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>Codebase fix 5 (Codex review 2026-11-21): the snapshot rate never drops below the rate at which every detonation reaches a snapshot.</summary>
public class SnapshotRateFloorTests
{
    [Theory]
    [InlineData("10", true)]
    [InlineData("20", true)]
    [InlineData("30", true)]
    [InlineData("9", false)]
    [InlineData("5", false)]
    [InlineData("1", false)]
    [InlineData("31", false)]
    public void The_snapshot_rate_option_takes_10_to_30(string value, bool ok)
    {
        bool parsed = ServerCommandLine.TryParse(["--snapshot-rate", value], out var a, out var error);
        Assert.Equal(ok, parsed);
        if (ok) Assert.Equal(int.Parse(value), a.SnapshotRate);
        else Assert.Contains("10 to 30", error);
    }

    [Fact]
    public void The_floor_keeps_a_snapshot_within_every_detonations_memory()
    {
        // at the lowest rate a snapshot goes out at least every DetonationMemoryTicks ticks, so no detonation is pruned before one carries it
        int ticksPerSecond = new SimConfig().TickRate;
        Assert.True((double)ticksPerSecond / ServerOptions.MinSnapshotRate <= World.DetonationMemoryTicks);
    }
    [Theory]
    [InlineData(10)]
    [InlineData(5)]    // programmatic options below the floor are clamped to it
    [InlineData(1)]
    public void Snapshots_never_leave_a_detonations_memory_without_a_send(int rate)
    {
        var h = new Harness(LinkConditions.Perfect, 1, 1, serverOptions: new ServerOptions { SnapshotRate = rate });
        h.Run(TimeSpan.FromSeconds(1));
        var ticks = new List<uint>();
        h.Run(TimeSpan.FromSeconds(3), null, () => { if (ticks.Count == 0 || ticks[^1] != h.Clients[0].LatestSnapshotTick) ticks.Add(h.Clients[0].LatestSnapshotTick); });
        Assert.InRange(ticks.Count, 28, 32);   // 10 a second at the floor
        for (int k = 1; k < ticks.Count; k++) Assert.True(ticks[k] - ticks[k - 1] <= World.DetonationMemoryTicks, $"gap of {ticks[k] - ticks[k - 1]} ticks after tick {ticks[k - 1]}");
    }
}
