using System.Drawing.Imaging;

namespace SmLauncher;

/// <summary>
/// <c>--layout-check &lt;folder&gt;</c>: draws the window off-screen in each of its states (idle, checking, offered, downloading, error, setup, and the owner's
/// state of 2026-10-31 in a 735 px wide window) at 100, 125, 150, 175 and 200 % (simulated where this PC's display is not that: fonts and pixel sizes scaled
/// the way Windows Forms scales them on such a display), saves a PNG of each into the folder and fails when two visible controls overlap, one reaches
/// outside its container or a text does not fit its control. No install folder, no network, nothing on the desktop (the windows sit at -4000,-4000).
/// SmLauncher.Tests runs the same checks (LauncherLayoutTests).
/// </summary>
static class LayoutCheck
{
    public static readonly double[] Scales = [1.0, 1.25, 1.5, 1.75, 2.0];

    /// <summary>The owner's window width in the screenshot of 2026-10-31 (outer pixels); the window cannot be narrower than its minimum, which grows with the scale.</summary>
    public const int OwnerWidth = 735;

    /// <summary>Screens (work area = screen minus taskbar) and their display scale. At 1366x768 125 % and 1920x1080 175 % the window is taller than the work
    /// area, so it is clamped and its rows must scroll instead of overlapping.</summary>
    public static readonly (string Name, double Scale, int W, int H)[] WorkAreas =
        [("1366x768", 1.0, 1366, 728), ("1366x768-125", 1.25, 1366, 728), ("1920x1080", 1.5, 1920, 1040), ("1920x1080-175", 1.75, 1920, 1040), ("2560x1440", 2.0, 2560, 1400)];

    public static int Run(string? folder)
    {
        folder ??= Path.Combine(Path.GetTempPath(), "sm-launcher-layout");
        Directory.CreateDirectory(folder);
        ApplicationConfiguration.Initialize();
        int failures = 0, shots = 0;
        void One(string name, string state, double scale, int? width = null, (int W, int H)? workArea = null, bool smallest = false)
        {
            var problems = StateProblems(state, scale, width, workArea, Path.Combine(folder, name + ".png"), smallest);
            shots++;
            foreach (var p in problems) Console.WriteLine($"{name}: {p}");
            if (problems.Count > 0) failures++;
        }
        foreach (double scale in Scales)
        {
            foreach (string state in MainForm.DemoStates) One($"launcher-{state}-{(int)Math.Round(scale * 100)}", state, scale);
            One($"launcher-owner-{OwnerWidth}px-{(int)Math.Round(scale * 100)}", "owner", scale, OwnerWidth);
            foreach (string state in new[] { "error", "setup" }) One($"launcher-{state}-smallest-{(int)Math.Round(scale * 100)}", state, scale, smallest: true);      // the tallest states, dragged as small as allowed
        }
        // small and large work areas (screen minus taskbar): the window must fit inside, centred, at the scale of that display
        foreach (var (sname, scale, wa, ha) in WorkAreas)
            foreach (string state in MainForm.DemoStates) One($"launcher-{state}-screen-{sname}", state, scale, null, (wa, ha));
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
        // the repaint check (the live window against a full repaint after state changes, with the safe-mode box's row): at 100, 150 and 200 %, and the
        // owner's 735 px window at 100 %
        foreach (string sc in new[] { "1", "1.5", "2" })
            if (RowCheck.Run(Path.Combine(folder, "repaint-" + sc), sc, initialize: false) != 0) failures++;
        if (RowCheck.Run(Path.Combine(folder, "repaint-owner"), "1", initialize: false, width: OwnerWidth) != 0) failures++;
        Theme.SimScale = 1.0;
        Console.WriteLine(failures == 0 ? $"layout check: ok ({shots} states, no overlaps, nothing outside its container, every text fits)" : $"layout check: FAILED ({failures} of {shots} states)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>One state of the window at one display scale (1 = 100 %), optionally at an outer width and in a work area: the problems found (none when it is
    /// clean), and a PNG of it when <paramref name="png"/> is given. Also checks that the window's pixel sizes really follow the scale (the PLAY button is 44 px
    /// tall at 100 %): they once stayed at 100 % on a larger display while the fonts grew.</summary>
    public static List<string> StateProblems(string state, double scale, int? width = null, (int W, int H)? workArea = null, string? png = null, bool smallest = false)
    {
        Theme.SimScale = 1.0;
        var list = new List<string> { "--screenshot", "--ui-state", state, "--ui-scale", scale.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (workArea is { } wa0) { list.Add("--ui-workarea"); list.Add($"{wa0.W}x{wa0.H}"); }
        using var form = new MainForm(Args.Parse([.. list]));
        if (width is { } w) form.Width = w;
        form.Show();
        if (smallest) form.Size = form.MinimumSize;       // as small as a user can drag it
        for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(20); }
        if (png is not null) using (var bmp = form.Snapshot()) bmp.Save(png, ImageFormat.Png);
        var problems = Problems(form);
        if (workArea is { } wa && (form.Width > wa.W || form.Height > wa.H)) problems.Add($"the window {form.Width}x{form.Height} is larger than the work area {wa.W}x{wa.H}");
        int playHeight = form.PlayButtonSize().Height, expected = (int)Math.Round(44 * Theme.Scale(form));
        if (Math.Abs(playHeight - expected) > 1) problems.Add($"the PLAY button is {playHeight} px tall at {scale * 100:0} %, not {expected} px: the window's sizes do not follow the display scale");
        form.Close();
        Theme.SimScale = 1.0;
        return problems;
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
                    if (TextDoesNotFit(k) is { } why) problems.Add($"{Describe(k)} {r}: {why}");
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

    /// <summary>Whether a control's text needs more room than its bounds give it (cut-off or overdrawn text); null when it fits. A one-line label that ends in
    /// "..." by design (<see cref="DarkLabel.Ellipsize"/>) fits whatever its width, text boxes scroll.</summary>
    static string? TextDoesNotFit(Control k)
    {
        if (k.Text.Length == 0 || k is TextBox or DarkProgress || k is DarkLabel { Ellipsize: true }) return null;
        const int slack = 2;        // rounding between the control's own measure and this one
        switch (k)
        {
            case DarkCheckBox or DarkRadioButton:
            {
                var need = DarkGlyph.Preferred((ButtonBase)k, new Size(k.Width, 0));
                return need.Width > k.Width + slack || need.Height > k.Height + slack ? $"its text needs {need.Width}x{need.Height}" : null;
            }
            case Button b:
            {
                var t = TextRenderer.MeasureText(b.Text, b.Font, new Size(int.MaxValue, 0), TextFormatFlags.SingleLine);
                int w = b.Width - 2 * b.FlatAppearance.BorderSize, h = b.Height - 2 * b.FlatAppearance.BorderSize;
                return t.Width > w + slack || t.Height > h + slack ? $"its text needs {t.Width}x{t.Height} on one line" : null;
            }
            case Label l:
            {
                int w = l.Width - l.Padding.Horizontal;
                var t = TextRenderer.MeasureText(l.Text, l.Font, new Size(Math.Max(1, w), 0), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                return t.Width > w + slack || t.Height > l.Height - l.Padding.Vertical + slack ? $"its text needs {t.Width}x{t.Height} in {w} px" : null;
            }
        }
        return null;
    }

    static string Describe(Control c) => $"{c.GetType().Name}[{(c.Text.Length > 24 ? c.Text[..24] + "..." : c.Text)}]";
}
