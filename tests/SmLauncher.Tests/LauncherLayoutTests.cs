using System.Windows.Forms;

namespace SmLauncher.Tests;

/// <summary>
/// The launcher window, drawn off-screen (at -4000,-4000, never activated, not in the taskbar): at every display scale no two visible controls overlap,
/// nothing sticks out of its container, every text fits its control (or ends in "..." by design), the pixel sizes follow the scale, and after each state
/// change the live window equals a full fresh repaint (no stale or ghost pixels, the owner's "Old GPU" row of 2026-10-31). Scales other than this PC's own
/// are simulated the way Windows Forms scales a window on such a display (MainForm --ui-scale).
/// </summary>
[Collection(nameof(LauncherLayoutTests))]       // one window at a time: Theme.SimScale is shared
public class LauncherLayoutTests
{
    static readonly object Gate = new();
    static bool _initialized;

    /// <summary>Runs <paramref name="body"/> on a fresh STA thread with the launcher's own Windows Forms set-up (per-monitor DPI awareness, visual styles).</summary>
    static T OnUiThread<T>(Func<T> body)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                lock (Gate)
                {
                    if (!_initialized)          // what the launcher's ApplicationConfiguration.Initialize does (its csproj: ApplicationHighDpiMode PerMonitorV2)
                    {
                        _initialized = true;
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                    }
                    result = body();
                }
            }
            catch (Exception e) { error = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("the launcher window failed: " + error);
        return result;
    }

    public static TheoryData<double> DisplayScales => [1.0, 1.25, 1.5, 1.75];

    [Theory]
    [MemberData(nameof(DisplayScales))]
    public void Every_state_lays_out_without_overlaps_or_cut_text(double scale)
    {
        var problems = OnUiThread(() =>
        {
            var all = new List<string>();
            foreach (string state in MainForm.DemoStates)
                all.AddRange(LayoutCheck.StateProblems(state, scale).Select(p => $"{state}: {p}"));
            all.AddRange(LayoutCheck.StateProblems("owner", scale, LayoutCheck.OwnerWidth).Select(p => $"owner, {LayoutCheck.OwnerWidth} px: {p}"));
            foreach (string state in new[] { "error", "setup" })          // the tallest states in a window dragged as small as it goes: the rows scroll, never overlap
                all.AddRange(LayoutCheck.StateProblems(state, scale, smallest: true).Select(p => $"{state}, smallest window: {p}"));
            return all;
        });
        Assert.True(problems.Count == 0, $"at {scale * 100:0} %:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    [Fact]
    public void A_window_clamped_to_a_small_screen_scrolls_instead_of_overlapping()
    {
        var problems = OnUiThread(() =>
        {
            var all = new List<string>();
            foreach (var (name, scale, w, h) in LayoutCheck.WorkAreas)
                foreach (string state in MainForm.DemoStates)
                    all.AddRange(LayoutCheck.StateProblems(state, scale, null, (w, h)).Select(p => $"{name}, {state}: {p}"));
            return all;
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_window_is_per_monitor_dpi_aware() =>
        Assert.Equal(HighDpiMode.PerMonitorV2, OnUiThread(() => Application.HighDpiMode));

    [Theory]
    [InlineData(1.0, LayoutCheck.OwnerWidth)]      // the owner's screenshot: a 735 px window, the mouse over "Host a match on this PC"
    [InlineData(1.5, null)]
    public void The_live_window_always_equals_a_full_repaint(double scale, int? width)
    {
        var problems = OnUiThread(() => RowCheck.Check(null, scale.ToString(System.Globalization.CultureInfo.InvariantCulture), width, _ => { }, out _));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
