using System;
using System.Globalization;
using System.Linq;
using Godot;

namespace SmClient;

/// <summary>
/// Test aid (hotfix/ashfall-floor-shimmer, 2026-11-08): <c>--ashfall-frames &lt;dir&gt; [--ashfall-frames-at s1,s2,..] [--ashfall-frames-count n]</c> saves n consecutive frames (default 12) at each of the
/// given times (seconds after Ashfall was drawn; default 45) as PNGs in dir, in a real match: the floor shimmer is judged on consecutive frames of the moving game camera. Needs a rendering window
/// (<c>godot.cmd --windowed</c>). Never active without the argument.
/// </summary>
public partial class AshfallFrameBurst : Node
{
    readonly string _dir;
    readonly double[] _at;
    readonly int _count;
    double _t;
    int _burst, _frame;

    AshfallFrameBurst(string dir, double[] at, int count) { _dir = dir; _at = at; _count = count; Name = "AshfallFrameBurst"; }

    /// <summary>The burst node when the command line asks for one, else null.</summary>
    public static AshfallFrameBurst? FromArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        string? Value(string name) { int i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (Value("ashfall-frames") is not { } dir || DisplayServer.GetName() == "headless") return null;
        var at = (Value("ashfall-frames-at") ?? "45").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1).Where(v => v >= 0).OrderBy(v => v).ToArray();
        int count = int.TryParse(Value("ashfall-frames-count"), out var c) && c > 0 ? Math.Min(c, 120) : 12;
        return new AshfallFrameBurst(dir, at, count);
    }

    public override void _Process(double delta)
    {
        _t += delta;
        if (_burst >= _at.Length || _t < _at[_burst]) return;
        System.IO.Directory.CreateDirectory(_dir);
        string path = System.IO.Path.Combine(_dir, $"burst{_burst}_f{_frame:D3}.png");
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        if (_frame == 0) GD.Print($"ashfall frames: burst {_burst} at {_t:F1} s -> {_dir} ({err})");
        if (++_frame >= _count) { _frame = 0; _burst++; }
    }
}
