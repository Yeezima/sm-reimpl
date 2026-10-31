using System.Drawing.Imaging;

namespace SmLauncher;

/// <summary>
/// <c>--layout-check &lt;folder&gt;</c>: draws the window off-screen in each of its states (idle, checking, offered, downloading, error, setup) at 100, 125,
/// 150 and 200 % (simulated: fonts and pixel sizes scaled as Windows does), saves a PNG of each into the folder and fails when two visible controls overlap
/// or one reaches outside its container. No install folder, no network, nothing on the desktop (the windows sit at -4000,-4000).
/// </summary>
static class LayoutCheck
{
    public static readonly double[] Scales = [1.0, 1.25, 1.5, 2.0];

    public static int Run(string? folder)
    {
        folder ??= Path.Combine(Path.GetTempPath(), "sm-launcher-layout");
        Directory.CreateDirectory(folder);
        ApplicationConfiguration.Initialize();
        int failures = 0, shots = 0;
        foreach (double scale in Scales)
            foreach (string state in MainForm.DemoStates)
            {
                Theme.SimScale = 1.0;
                var args = Args.Parse(["--screenshot", "--ui-state", state, "--ui-scale", scale.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                using var form = new MainForm(args);
                form.Show();
                for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(20); }
                string name = $"launcher-{state}-{(int)Math.Round(scale * 100)}";
                using (var bmp = form.Snapshot()) bmp.Save(Path.Combine(folder, name + ".png"), ImageFormat.Png);
                shots++;
                var problems = Problems(form);
                foreach (var p in problems) Console.WriteLine($"{name}: {p}");
                if (problems.Count > 0) failures++;
                form.Close();
            }
        // small and large work areas (screen minus taskbar): the window must fit inside, centred, at the scale of that display
        (string Name, double Scale, int W, int H)[] screens = [("1366x768", 1.0, 1366, 728), ("1920x1080", 1.5, 1920, 1040), ("2560x1440", 2.0, 2560, 1400)];
        foreach (var (sname, scale, wa, ha) in screens)
            foreach (string state in MainForm.DemoStates)
            {
                Theme.SimScale = 1.0;
                var args = Args.Parse(["--screenshot", "--ui-state", state, "--ui-scale", scale.ToString(System.Globalization.CultureInfo.InvariantCulture), "--ui-workarea", $"{wa}x{ha}"]);
                using var form = new MainForm(args);
                form.Show();
                for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(20); }
                string name = $"launcher-{state}-screen-{sname}";
                using (var bmp = form.Snapshot()) bmp.Save(Path.Combine(folder, name + ".png"), ImageFormat.Png);
                shots++;
                var problems = Problems(form);
                if (form.Width > wa || form.Height > ha) problems.Add($"the window {form.Width}x{form.Height} is larger than the work area {wa}x{ha}");
                foreach (var p in problems) Console.WriteLine($"{name}: {p}");
                if (problems.Count > 0) failures++;
                form.Close();
            }
        // tab order: PLAY is the first stop of the main view
        {
            Theme.SimScale = 1.0;
            using var form = new MainForm(Args.Parse(["--screenshot", "--ui-state", "idle"]));
            form.Show();
            for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(20); }
            var order = new List<string>();
            for (Control? c = form.GetNextControl(null!, true); c is not null && order.Count < 40; c = form.GetNextControl(c, true))
                if (c.TabStop && c.Visible && c.Enabled && !(c is Label and not LinkLabel)) order.Add(Describe(c));
            Console.WriteLine("tab order: " + string.Join(" > ", order));
            if (order.Count == 0 || !order[0].StartsWith("Button[PLAY", StringComparison.Ordinal)) { Console.WriteLine("tab order: FAILED, PLAY is not first"); failures++; }
            form.Close();
        }
        Theme.SimScale = 1.0;
        Console.WriteLine(failures == 0 ? $"layout check: ok ({shots} states, no overlaps, nothing outside its container)" : $"layout check: FAILED ({failures} of {shots} states)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Overlaps between visible leaf controls, and visible controls that stick out of their parent's client area (clipped text, cut-off buttons).</summary>
    public static List<string> Problems(Form form)
    {
        var leaves = new List<(Control C, Rectangle R)>();
        var problems = new List<string>();
        void Walk(Control c)
        {
            foreach (Control k in c.Controls)
            {
                if (!k.Visible) continue;
                var r = form.RectangleToClient(c.RectangleToScreen(k.Bounds));
                var parentArea = form.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
                bool scrolls = c is ScrollableControl { AutoScroll: true } sc && sc.AutoScrollMinSize.Height > sc.ClientSize.Height;        // may be taller than its view: it scrolls
                if (!scrolls && !parentArea.Contains(r)) problems.Add($"{Describe(k)} {r} sticks out of {Describe(c)} {parentArea}");
                if (k.Controls.Cast<Control>().Any(x => x.Visible)) Walk(k);
                else
                {
                    var shown = r;                                                    // what is actually visible: clipped by every ancestor (a scrolled panel hides the rest)
                    for (Control? up = k.Parent; up is not null && up != form; up = up.Parent)
                        shown = Rectangle.Intersect(shown, form.RectangleToClient(up.RectangleToScreen(up.ClientRectangle)));
                    if (shown.Width > 0 && shown.Height > 0) leaves.Add((k, shown));
                }
            }
        }
        Walk(form);
        for (int i = 0; i < leaves.Count; i++)
            for (int j = i + 1; j < leaves.Count; j++)
                if (leaves[i].R.IntersectsWith(leaves[j].R))
                    problems.Add($"{Describe(leaves[i].C)} {leaves[i].R} overlaps {Describe(leaves[j].C)} {leaves[j].R}");
        return problems;
    }

    static string Describe(Control c) => $"{c.GetType().Name}[{(c.Text.Length > 24 ? c.Text[..24] + "..." : c.Text)}]";
}
