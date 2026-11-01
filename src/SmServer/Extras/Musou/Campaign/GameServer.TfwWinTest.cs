using System.Globalization;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// Test aids of Musou Mode on a real server (TF9; <c>GameServer.TfwDownTest.cs</c> is their sibling). Off without their variables.
/// <para>
/// <c>SM_TFW_WIN_TEST=&lt;m&gt;</c>: once a second every enemy within that many metres of a human in the field falls, the boss too, credited to that human. The positions, the Guardsmen's securing, the depot, the commander's
/// coming, the win, the free pickup, the front's move and the final assault all stay the run's own: only the fighting is left out, so a scripted player (<c>--tfw-test-win</c>) can walk a whole run.
/// </para>
/// <para>
/// <c>SM_TFW_STRESS=&lt;orks&gt;,&lt;squads&gt;</c>: during a battle the enemies are topped up to <c>orks</c> near the landing zone and the Guard squads to <c>squads</c>, once a second, and a human in the field who falls
/// stands up where it fell, so the load holds for as long as the run is measured (<c>SM_TICK_STATS</c>).
/// </para>
/// </summary>
public sealed partial class GameServer
{
    static readonly float TfwWinRadius = float.TryParse(Environment.GetEnvironmentVariable("SM_TFW_WIN_TEST"), CultureInfo.InvariantCulture, out float m) ? m : -1f;
    static readonly (int Orks, int Squads)? TfwStress = ParseStress(Environment.GetEnvironmentVariable("SM_TFW_STRESS"));

    static (int, int)? ParseStress(string? text)
    {
        var parts = text?.Split(',') ?? [];
        return parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out int orks) && int.TryParse(parts[1], CultureInfo.InvariantCulture, out int squads) ? (orks, squads) : null;
    }

    void TfwTestAids()
    {
        if ((TfwWinRadius < 0f && TfwStress is null) || World.Tfw is not { } run) return;
        int rate = Math.Max(1, World.Config.TickRate);
        if (TickStatsSeconds > 0.0 && World.Tick % (uint)(rate * TickStatsSeconds) == 0)
            Console.WriteLine($"TFWSTATS tick {World.Tick}: {run.Phase}, enemies {World.Musou?.Alive}, guards {World.TfwGuards?.Alive}, marines {run.Marines.Count}, players {World.Players.Count}");
        if (run.Battle is null) return;
        if (TfwStress is { } stress)
            foreach (int human in run.InField)
                if (World.Players.TryGetValue(human, out var p) && !p.Alive) World.TfwRespawnAt(human, p.Position, p.Yaw);
        if (World.Tick % (uint)rate != 0) return;
        if (TfwStress is { } fill) World.TfwFillForTests(fill.Orks, fill.Squads);
        if (TfwWinRadius < 0f) return;
        foreach (int human in run.InField)
            if (World.Players.TryGetValue(human, out var p) && p.Alive) World.TfwSmiteForTests(human, p.Position, TfwWinRadius);
    }
}
