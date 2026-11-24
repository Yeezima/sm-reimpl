using System.Numerics;
using SmSim;
using SmSim.Extras.Executions;

namespace SmNet.Tests.Extras.Executions;

/// <summary>Executions X3's synthetic rows (no original bytes: ids, names and timings from <see cref="ExecutionRows.All"/> or made up here, root paths generated).</summary>
static class ExecutionTestKit
{
    /// <summary>A straight root path forward over <paramref name="metres"/> in <paramref name="samples"/> evenly spaced samples (the turn <paramref name="turn"/> spread over it).</summary>
    public static Vector4[] Straight(float metres, int samples = 31, float turn = 0f) =>
        Enumerable.Range(0, samples).Select(i => { float f = samples == 1 ? 0f : i / (float)(samples - 1); return new Vector4(0f, metres * f, 0f, turn * f); }).ToArray();

    /// <summary>
    /// A synthetic two-variant row (ids of <see cref="ExecutionRows.All"/> so the wire order resolves it): the reference clip at the authored gap <paramref name="gap"/>
    /// and a near variant at <paramref name="near"/>, each walking its own distance minus 0.9 m forward, 2 s long, kill at 1 s, end at 1.8 s.
    /// </summary>
    public static ExecutionRow Row(string id = "C1.chainsword", string target = ExecutionTargets.ChaosMarine, string weapon = ExecutionWeapons.Chainsword, float gap = 1.3f, float near = 1.0f,
        float seconds = 2f, float kill = 1f, float? end = 1.8f)
    {
        var events = new List<ExecutionEvent> { new(ExecutionEventKind.Sync, 0.1f), new(ExecutionEventKind.Kill, kill) };
        if (end is { } e) events.Add(new(ExecutionEventKind.End, e));
        return new ExecutionRow(id, "*", weapon, target, "ref_clip", "victim_clip", seconds, seconds, events, new Vector3(0f, gap, 0f), MathF.PI, "graph", "seq")
        {
            AttackerVariants = [new("ref_clip", gap, Straight(MathF.Max(0f, gap - 0.9f))), new("near_clip", near, Straight(MathF.Max(0f, near - 0.9f)))],
            AttackerRoot = Straight(MathF.Max(0f, gap - 0.9f)),
            VictimRoot = Straight(0f, 2),
        };
    }

    /// <summary>Rows for every Classic victim kind and the chainsword (Chaos Marine C1, Ork Boy O1) as a set in the wire order of <see cref="ExecutionRows.All"/>.</summary>
    public static ExecutionRowSet ClassicSet(float gap = 1.3f) => ExecutionRowSet.OfAll(
        [Row("C1.chainsword", ExecutionTargets.ChaosMarine, gap: gap), Row("O1.chainsword", ExecutionTargets.OrkBoy, gap: gap)]);
}
