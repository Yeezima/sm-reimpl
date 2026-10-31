using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SmLauncher;

/// <summary>
/// <c>--row-check &lt;folder&gt;</c> (also part of <c>--layout-check</c>): the repaint test. One real window (off-screen at -4000,-4000, shown, with a handle) is
/// taken through the state changes a user sees (checking, update offered, downloading, error, set-up and back, the safe-mode box ticked, button hover, a
/// resize, minimise and restore), and after each step what the window really shows (PrintWindow of the live window, which is the painted result of every
/// partial repaint so far) is compared with a fresh full repaint of the same window (every control asked to draw itself again). Any block that differs is a
/// stale or missing repaint; the check reports the rectangle and the controls under it, and fails. The frames go to the folder as PNG files, the safe-mode
/// box's row cropped as <c>row-*.png</c>.
/// </summary>
static class RowCheck
{
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    const uint PW_CLIENTONLY = 1, PW_RENDERFULLCONTENT = 2;

    static void Pump(int ms = 300) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(15); } }

    /// <summary>What the live window shows now: its client area, as composed by Windows from all the repaints so far.</summary>
    static Bitmap Live(Form f)
    {
        GetClientRect(f.Handle, out var c);
        var bmp = new Bitmap(Math.Max(1, c.R), Math.Max(1, c.B), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        try { PrintWindow(f.Handle, hdc, PW_CLIENTONLY | PW_RENDERFULLCONTENT); } finally { g.ReleaseHdc(hdc); }
        return bmp;
    }

    /// <summary>A full fresh repaint: every control draws itself again (WM_PRINT of the whole tree), nothing remembered from before.</summary>
    static Bitmap Fresh(Form f)
    {
        using var w = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppArgb);
        f.DrawToBitmap(w, new Rectangle(0, 0, f.Width, f.Height));
        GetWindowRect(f.Handle, out var wr);
        var p = new POINT(); ClientToScreen(f.Handle, ref p);
        GetClientRect(f.Handle, out var c);
        var r = Rectangle.Intersect(new Rectangle(p.X - wr.L, p.Y - wr.T, c.R, c.B), new Rectangle(0, 0, w.Width, w.Height));
        return w.Clone(r, PixelFormat.Format32bppArgb);
    }

    /// <summary>Blocks where the two frames differ (small anti-aliasing noise is ignored): the bounding box of each connected group of 16 px cells.</summary>
    static List<Rectangle> Differences(Bitmap a, Bitmap b, List<(Rectangle R, bool Ring)> rings)
    {
        var res = new List<Rectangle>();
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        var da = a.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            const int cell = 16;
            int cw = (w + cell - 1) / cell, ch = (h + cell - 1) / cell;
            var bad = new bool[cw * ch];
            var ra = new byte[w * 4]; var rb = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(da.Scan0 + y * da.Stride, ra, 0, w * 4);
                Marshal.Copy(db.Scan0 + y * db.Stride, rb, 0, w * 4);
                for (int x = 0; x < w; x++)
                {
                    int d = Math.Abs(ra[x * 4] - rb[x * 4]) + Math.Abs(ra[x * 4 + 1] - rb[x * 4 + 1]) + Math.Abs(ra[x * 4 + 2] - rb[x * 4 + 2]);
                    if (d > 24 && !rings.Any(r => r.R.Contains(x, y) && (!r.Ring || !Rectangle.Inflate(r.R, -5, -5).Contains(x, y)))) bad[(y / cell) * cw + x / cell] = true;
                }
            }
            var seen = new bool[bad.Length];
            for (int i = 0; i < bad.Length; i++)
            {
                if (!bad[i] || seen[i]) continue;
                int minx = cw, miny = ch, maxx = 0, maxy = 0;
                var stack = new Stack<int>(); stack.Push(i); seen[i] = true;
                while (stack.Count > 0)
                {
                    int k = stack.Pop(), x = k % cw, y = k / cw;
                    minx = Math.Min(minx, x); maxx = Math.Max(maxx, x); miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= cw || ny >= ch) continue;
                            int n = ny * cw + nx;
                            if (bad[n] && !seen[n]) { seen[n] = true; stack.Push(n); }
                        }
                }
                res.Add(Rectangle.Intersect(new Rectangle(minx * cell, miny * cell, (maxx - minx + 1) * cell, (maxy - miny + 1) * cell), new Rectangle(0, 0, w, h)));
            }
        }
        finally { a.UnlockBits(da); b.UnlockBits(db); }
        return res;
    }

    /// <summary>What is not compared: the 3 px frame of every text box (the native edit control draws its own border differently for a live window and for WM_PRINT) and the progress bar's gradient (one row rounds differently when painted in parts).</summary>
    static List<(Rectangle R, bool Ring)> NativeBorders(Form f)
    {
        var res = new List<(Rectangle, bool)>();
        void Walk(Control c)
        {
            foreach (Control k in c.Controls)
            {
                if (!k.Visible) continue;
                if (k is TextBox) { var r = f.RectangleToClient(c.RectangleToScreen(k.Bounds)); res.Add((Rectangle.Inflate(r, 2, 2), true)); }
                else if (k is DarkProgress) res.Add((f.RectangleToClient(c.RectangleToScreen(k.Bounds)), false));        // its gradient rounds one row differently when painted in pieces
                else Walk(k);
            }
        }
        Walk(f);
        return res;
    }

    static string Under(Form f, Rectangle r)
    {
        var names = new List<string>();
        void Walk(Control c)
        {
            foreach (Control k in c.Controls)
            {
                if (!k.Visible) continue;
                var kr = f.RectangleToClient(c.RectangleToScreen(k.Bounds));
                if (!kr.IntersectsWith(r)) continue;
                if (k.Controls.Count == 0) names.Add($"{k.GetType().Name}[{(k.Text.Length > 20 ? k.Text[..20] : k.Text)}]");
                else Walk(k);
            }
        }
        Walk(f);
        return string.Join(", ", names);
    }

    public static int Run(string folder, string? scaleText = null, bool initialize = true)
    {
        Directory.CreateDirectory(folder);
        if (initialize) ApplicationConfiguration.Initialize();
        Theme.SimScale = 1.0;
        var list = new List<string> { "--screenshot", "--ui-state", "idle" };
        if (scaleText is not null) { list.Add("--ui-scale"); list.Add(scaleText); }
        using var form = new MainForm(Args.Parse([.. list]));
        form.Show();
        Pump(600);
        int failures = 0, step = 0;
        void Check(string what)
        {
            Pump(250);
            using var live = Live(form);
            using var fresh = Fresh(form);
            string name = $"{++step:00}-{what}";
            live.Save(Path.Combine(folder, "live-" + name + ".png"), ImageFormat.Png);
            var diffs = Differences(live, fresh, NativeBorders(form));
            var row = form.SafeModeRowBounds();
            var crop = Rectangle.Intersect(Rectangle.Inflate(row, 0, 40), new Rectangle(0, 0, live.Width, live.Height));
            if (crop.Width > 0 && crop.Height > 0) { using var c = live.Clone(crop, PixelFormat.Format32bppArgb); c.Save(Path.Combine(folder, "row-" + name + ".png"), ImageFormat.Png); }
            if (diffs.Count > 0)
            {
                failures++;
                foreach (var d in diffs) Console.WriteLine($"repaint-diff {name}: stale or missing pixels at {d} (under: {Under(form, d)}){(d.IntersectsWith(Rectangle.Inflate(row, 0, 20)) ? " [IN THE SAFE-MODE ROW]" : "")}");
                fresh.Save(Path.Combine(folder, "fresh-" + name + ".png"), ImageFormat.Png);
            }
            foreach (var p in LayoutCheck.Problems(form)) { Console.WriteLine($"{name}: {p}"); failures++; }
        }
        Check("idle");
        foreach (var s in new[] { "checking", "offered", "downloading", "error", "idle", "setup", "idle", "error", "checking", "downloading", "idle" })
        {
            form.ApplyDemoState(s);
            Check("state-" + s);
        }
        form.ToggleSafeModeForTest(); Check("checkbox-toggled");
        form.ToggleSafeModeForTest(); Check("checkbox-toggled-back");
        form.FocusSafeModeForTest(true); Check("checkbox-focused");
        form.HoverForTest(true); Check("buttons-hovered");
        form.HoverForTest(false); Check("buttons-unhovered");
        form.Size = new Size(form.Width + 120, form.Height - 40); Check("resized-smaller");
        form.Size = new Size(form.Width - 80, form.Height + 60); Check("resized-again");
        form.ApplyDemoState("offered"); Check("offered-after-resize");
        form.WindowState = FormWindowState.Minimized; Pump(300);
        form.ApplyDemoState("downloading");                                        // changed while minimised
        form.WindowState = FormWindowState.Normal; Check("restored");
        form.ApplyDemoState("idle"); Check("idle-end");
        form.Close();
        Theme.SimScale = 1.0;
        Console.WriteLine(failures == 0 ? $"repaint check{(scaleText is null ? "" : " at " + scaleText + "x")}: ok ({step} frames, the live window equals a full repaint every time)" : $"repaint check{(scaleText is null ? "" : " at " + scaleText + "x")}: FAILED ({failures} problems in {step} frames)");
        return failures == 0 ? 0 : 1;
    }
}
