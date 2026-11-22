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
}
