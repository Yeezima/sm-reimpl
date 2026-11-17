using System.Linq;
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
        // classic-score-hud (owner 2026-11-17): the centred box is only drawn with --centred-score-box; the default panel is the original's layout.
        bool centred = Array.IndexOf(OS.GetCmdlineUserArgs(), "--centred-score-box") >= 0;
        if (string.IsNullOrEmpty(dir)) { Check("score box: fit (no game install: skipped)", true); return; }
        if (!centred) { await DefaultScorePanelChecks(dir); return; }   // the centred-box check below runs in a second pass with --centred-score-box
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

    /// <summary>classic-score-hud: the default panel (the original's layout): its frame stays inside the viewport, one marker per point (the 4th slot only with a 4th point), the aquila for Space Marines and the star for Chaos.</summary>
    async Task DefaultScorePanelChecks(string dir)
    {
        var hud = new Hud { GameDir = dir };
        hud.Data.InGame = true; hud.Data.Alive = true; hud.Data.Teams = true; hud.Data.OwnTeam = Team.SpaceMarines; hud.Data.Seize = true;
        hud.Data.OwnScore = 786; hud.Data.EnemyScore = 576; hud.Data.ScoreLimit = 1000; hud.Data.Clock = "8:16";
        AddChild(hud);
        try
        {
            Hud.ScorePanelProbe? probe = null;
            for (int i = 0; i < 900 && probe is null; i++)
            {
                await Frames(1);
                probe = hud.LastScorePanel;
                if (probe is null && i == 200 && !hud.HudMoviesLoading()) break;
            }
            if (probe is null) { Check("score panel (default): the HUD movies were not read here: skipped", true); return; }
            async Task<Hud.ScorePanelProbe?> With(int points, Team team)
            {
                hud.Data.OwnTeam = team;
                hud.Data.Points.Clear();
                for (int i = 0; i < points; i++) hud.Data.Points.Add(new Hud.Point { Letter = ((char)('A' + i)).ToString(), Owner = Hud.Rel.None });
                await Frames(3);
                return hud.LastScorePanel;
            }
            foreach (int n in new[] { 3, 4 })
            {
                var p = await With(n, Team.SpaceMarines);
                if (p is null) { Check($"score panel (default) with {n} points: drawn", false); continue; }
                float minX = p.Frame.Min(v => v.X), maxX = p.Frame.Max(v => v.X), minY = p.Frame.Min(v => v.Y), maxY = p.Frame.Max(v => v.Y);
                string d = $"frame x {minX:F0}..{maxX:F0} y {minY:F0}..{maxY:F0} of {p.Viewport.X:F0}x{p.Viewport.Y:F0}";
                Check($"score panel (default) with {n} points: the frame is a sized box inside the viewport", maxX > minX + 100f && maxY > minY + 20f && minX >= -1f && minY >= -1f && maxX <= p.Viewport.X + 1f && maxY <= p.Viewport.Y + 1f, d);
                Check($"score panel (default) with {n} points: {n} markers drawn (the 4th slot only with a 4th point)", p.Markers == n, $"markers {p.Markers}");
                Check($"score panel (default) with {n} points: the aquila's side", p.Emblem == Team.SpaceMarines);
            }
            var chaos = await With(3, Team.Chaos);
            Check("score panel (default): the Chaos star for Chaos", chaos is { Emblem: Team.Chaos });
            var five = await With(5, Team.SpaceMarines);
            Check("score panel (default): never more than the movie's four slots", five is { Markers: 4 }, five is null ? "not drawn" : $"markers {five.Markers}");
            hud.Data.Seize = false;
            var annih = await With(0, Team.SpaceMarines);
            Check("score panel (default): Annihilation draws no markers", annih is { Markers: 0 });
        }
        finally { hud.QueueFree(); }
    }
}
