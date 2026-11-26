using SmSim;
using SmSim.Extras.Executions;
using SmNet.Tests.Extras.Executions;

namespace SmNet.Tests.CrossExtras;

/// <summary>
/// Executions X4b's rows and readings for the tests in the Warfront folders (synthetic only: <see cref="ExecutionTestKit.Row"/>). It lives in the executions folder and in a namespace that names
/// neither extra, so a Warfront test reaches the executions' types through it without naming their namespace (the boundary scans of <c>ExecutionsBoundaryTests</c> and <c>WarfrontBoundaryTests</c>).
/// </summary>
public static class ChaosExecutionKit
{
    /// <summary>The Chaos kinds' chainsword rows (C1, R1, B1) and the Ork Boy's (O1), synthetic, in the wire order of <see cref="ExecutionRows.All"/>: 2 s, kill at 1 s, end at 1.8 s, gap 1.3 m.</summary>
    public static ExecutionRowSet Rows() => ExecutionRowSet.OfAll(
    [
        ExecutionTestKit.Row("C1.chainsword", ExecutionTargets.ChaosMarine), ExecutionTestKit.Row("R1.chainsword", ExecutionTargets.Renegade),
        ExecutionTestKit.Row("B1.chainsword", ExecutionTargets.Bloodletter), ExecutionTestKit.Row("O1.chainsword", ExecutionTargets.OrkBoy),
    ]);

    /// <summary><paramref name="c"/> with <see cref="Rows"/> and every class's melee choice a chainsword of its side.</summary>
    public static SimConfig WithRows(SimConfig c) => c with
    {
        Executions = Rows(),
        Classes = [.. c.Classes.Select(k => k with { MeleeChoices = [new MeleeCombo(k.Side == Team.Chaos ? "csm_mp_melee_chainsword" : "sm_mp_melee_chainsword", k.Melee)] })],
    };

    /// <summary>The row id of player <paramref name="id"/>'s run started in the last step, or null.</summary>
    public static string? StartedRow(World w, int id = 1)
    {
        foreach (var r in w.ExecutionsStartedThisTick) if (r.Executor == id) return w.Config.Executions.AtWire(r.Wire)?.Id;
        return null;
    }

    /// <summary>The enemy id player <paramref name="id"/>'s live run holds, or 0.</summary>
    public static int LiveTarget(World w, int id = 1) => w.ExecutionOf(id) is { } r ? r.Target : 0;

    /// <summary>Why player <paramref name="id"/>'s run ended in the last step ("Done", "Invalid" ...), or null.</summary>
    public static string? EndedWhy(World w, int id = 1)
    {
        foreach (var e in w.ExecutionsEndedThisTick) if (e.Run.Executor == id) return e.Reason.ToString();
        return null;
    }

    /// <summary>Whether player <paramref name="id"/>'s live run has passed its kill frame.</summary>
    public static bool KillDone(World w, int id = 1) => w.ExecutionOf(id) is { KillDone: true };

    /// <summary>Why player <paramref name="id"/>'s press did nothing in the last step ("NoTarget", "Refused" ...), or null.</summary>
    public static string? RefusedWhy(World w, int id = 1)
    {
        foreach (var (pid, why) in w.ExecutionsRefusedThisTick) if (pid == id) return why.ToString();
        return null;
    }

    /// <summary>The armour a successful Warfront execution leaves on a body with <paramref name="shieldMax"/>: the bar plus a one-bar overshield.</summary>
    public static float RewardShield(float shieldMax) => ExecutionRules.RewardShield(shieldMax);

    /// <summary>The armour of the rewards given in the last step to player <paramref name="id"/> (none: empty).</summary>
    public static IEnumerable<float> RewardedShields(World w, int id = 1) => w.ExecutionsRewardedThisTick.Where(r => r.Player == id).Select(r => r.Shield);

    /// <summary>Puts player <paramref name="id"/> <paramref name="gap"/> m from <paramref name="target"/> (flat, on the line from where he stands), facing it, standing still.</summary>
    public static void PlaceFacing(World w, int id, System.Numerics.Vector3 target, float gap)
    {
        var p = w.Players[id];
        var d = new System.Numerics.Vector2(p.Position.X - target.X, p.Position.Y - target.Y);
        if (d.LengthSquared() < 1e-6f) d = new System.Numerics.Vector2(0f, -1f);
        d = System.Numerics.Vector2.Normalize(d);
        var at = new System.Numerics.Vector3(target.X + d.X * gap, target.Y + d.Y * gap, target.Z);
        float yaw = MathF.Atan2(-(target.X - at.X), target.Y - at.Y);   // yaw 0 faces +Y, positive turns left
        w.ExecutionsPlaceForTests(id, at, yaw);
    }

    /// <summary>One tick: player <paramref name="tapper"/> taps Execute, everyone else stands still (each facing its yaw).</summary>
    public static void Tap(World w, int tapper = 1) => w.Step(id => Hold(w, id, id == tapper ? InputButtons.Execute : InputButtons.None));

    /// <summary>One tick with nobody pressing anything.</summary>
    public static void Idle(World w) => w.Step(id => Hold(w, id, InputButtons.None));

    static PlayerInput Hold(World w, int id, InputButtons buttons) => new(0, 0f, 0f, w.Players.TryGetValue(id, out var p) ? p.Yaw : 0f, buttons, 0f, w.Tick);
}
