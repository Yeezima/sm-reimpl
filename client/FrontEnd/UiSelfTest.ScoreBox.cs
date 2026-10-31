using System;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmSim;

namespace SmClient.FrontEnd;

// ui/score-box-center: the HUD's opaque score box holds its block (scores, bars, timer over the limit) centred, with room on every side.
public sealed partial class UiSelfTest
{
    /// <summary>The scenarios: own score, enemy score, limit, clock (a first kill, a normal match, three-digit scores and limit with a timer past ten minutes, a long game).</summary>
    static readonly (int Own, int Enemy, int Limit, string Clock)[] ScoreBoxCases =
        [(2, 0, 41, "8:16"), (0, 0, 25, "0:05"), (37, 12, 50, "12:30"), (123, 99, 250, "10:00"), (250, 249, 999, "59:59"), (7, 100, 100, "9:59")];

    async Task ScoreBoxChecks()
    {
        string? dir = _services.GameDir;
        if (string.IsNullOrEmpty(dir)) { Check("score box: fit (no game install: skipped)", true); return; }
        var hud = new Hud { GameDir = dir };
        hud.Data.Teams = true; hud.Data.OwnTeam = Team.SpaceMarines;
        AddChild(hud);
        try
        {
            // The movie's pieces are read and built in the background: wait for the first measurement.
            (float Left, float Right, float Top, float Bottom, float LeftMiddle)? gaps = null;
            for (int i = 0; i < 900 && gaps is null; i++)
            {
                await Frames(1);
                gaps = hud.ScoreBoxGaps();
                if (gaps is null && i == 200 && !hud.HudMoviesLoading()) break;
            }
            if (gaps is null) { Check("score box: fit (the HUD movies were not read here: skipped)", true); return; }
            foreach (var (own, enemy, limit, clock) in ScoreBoxCases)
            {
                hud.Data.OwnScore = own; hud.Data.EnemyScore = enemy; hud.Data.ScoreLimit = limit; hud.Data.Clock = clock;
                await Frames(2);
                var g = hud.ScoreBoxGaps();
                string name = $"score box {own}/{enemy} of {limit} at {clock}";
                if (g is not { } v) { Check($"{name}: measured", false); continue; }
                string d = $"gaps L {v.Left:F1} (middle {v.LeftMiddle:F1}) R {v.Right:F1} T {v.Top:F1} B {v.Bottom:F1}";
                Check($"{name}: nothing touches the box's edges (every gap at least 4 px)", Math.Min(Math.Min(v.Left, v.Right), Math.Min(v.Top, v.Bottom)) >= 4f, d);
                Check($"{name}: centred (left = right and top = bottom within 1 px)", Math.Abs(v.LeftMiddle - v.Right) <= 1f && Math.Abs(v.Top - v.Bottom) <= 1f, d);
            }
        }
        finally { hud.QueueFree(); }
    }
}
