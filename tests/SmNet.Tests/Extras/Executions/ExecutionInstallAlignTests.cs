using System.Numerics;
using SmContent;
using SmContent.Extras.Executions;
using SmSim;
using SmSim.Extras.Executions;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Executions;

/// <summary>
/// Executions X3 (lead 39, item 3): every row the install loads aligns on an open floor at its reference gap, its real root paths probed (the probe's cap, ExecutionAlign.MaxProbes 512, is checked
/// against the real paths: the longest at 2026-11-25 has 191 samples, so no row is refused for its sample count). An install fact (skipped without one; never in CI): no original bytes are read into the repository.
/// </summary>
public class ExecutionInstallAlignTests(ITestOutputHelper output)
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    [ContentProfileTests.InstallFact]
    public void Every_loaded_row_aligns_on_an_open_floor_at_its_reference_gap()
    {
        var notes = new List<string>();
        var catalogue = ExecutionCatalogueLoader.Load(Game.Value!, notes);
        Assert.NotEmpty(catalogue.Rows);
        var c = new SimConfig();
        var floor = ExecutionTestKit.Flat();
        var failed = new List<string>();
        foreach (var row in catalogue.Rows)
        {
            var attacker = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Alive = true };
            var victim = new PlayerState(2, new Vector3(row.Offset.X, row.Offset.Y, 0f), Vector3.Zero, MathF.PI, true) { Alive = true, Team = Team.Chaos };
            var r = ExecutionAlign.Align(attacker, victim, [row], c, floor);
            int samples = Math.Max(row.AttackerRoot.Count, row.AttackerVariants.Count == 0 ? 0 : row.AttackerVariants.Max(v => v.Root.Count));
            output.WriteLine($"{row.Id}: {(r.Ok ? "ok" : r.Failure.ToString())}, distance {r.Distance:0.00} m, slide {r.Slide:0.00} m, attacker root {samples} samples, victim root {row.VictimRoot.Count}");
            if (!r.Ok) failed.Add($"{row.Id}: {r.Failure}");
        }
        Assert.True(failed.Count == 0, "rows that do not align on an open floor:\n" + string.Join("\n", failed));
    }
}
