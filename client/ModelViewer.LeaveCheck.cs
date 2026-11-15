using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmNet;
using SmClient.FrontEnd;

namespace SmClient;

/// <summary>
/// fix/leave-cleanup test aid <c>--leave-check [seconds]</c> (with <c>--connect</c>): after the match has been connected that long (default 4 s) it leaves the way the Esc menu's LEAVE MATCH
/// does (without the blink), waits past the client's snapshot timeout (5 s) plus a margin, then checks that nothing of the match is left and prints "leave-check:" lines and a verdict.
/// <c>--leave-check-wait &lt;s&gt;</c> changes the wait (default 8). <c>--leave-check-double</c> first starts a second view over the live one (the orphan a double join would make), then leaves:
/// the sweep must find it ("outlived its reference") and no networked view may remain. <c>--leave-check-twice</c> asks to leave twice in the same frame. Exit code 0 passes, 1 fails.
/// A server stopped during the wait (the hosted server's shutdown) must not change the result.
/// </summary>
public partial class ModelViewer
{
    double? _leaveCheckAfter, _leftAtElapsed;
    double _leaveCheckWait = 8.0;
    NetworkView? _leaveCheckView;
    bool _leaveCheckDouble, _leaveCheckTwice, _leaveCheckEarly;
    int _leaveCheckCycles = 1, _leaveCheckCycle;   // --leave-check-cycles <n>: leave and join again n times
    readonly List<string> _cycleProblems = new();
    string? _leaveCheckShot;
    string _leaveCheckAtLeave = "";   // problems seen right at the leave, while the dropped view could still be read

    void LeaveCheckSetup(Dictionary<string, string?> args)
    {
        if (!args.ContainsKey("leave-check")) return;
        _leaveCheckAfter = double.TryParse(args.GetValueOrDefault("leave-check"), System.Globalization.CultureInfo.InvariantCulture, out var after) && after > 0 ? after : 4.0;
        if (double.TryParse(args.GetValueOrDefault("leave-check-wait"), System.Globalization.CultureInfo.InvariantCulture, out var wait) && wait > 0) _leaveCheckWait = wait;
        _leaveCheckShot = args.GetValueOrDefault("leave-check-shot");
        _leaveCheckEarly = args.ContainsKey("leave-check-early");   // leave as soon as the view exists: while connecting, before the first snapshot
        if (int.TryParse(args.GetValueOrDefault("leave-check-cycles"), out var cycles) && cycles > 0) _leaveCheckCycles = cycles;
        _leaveCheckDouble = args.ContainsKey("leave-check-double");
        _leaveCheckTwice = args.ContainsKey("leave-check-twice");
    }

    static int SoundsOutside(Node root, Node? frontEndLayerParent) =>
        root.GetChildren().Where(c => c is not UiSounds && c != frontEndLayerParent && c.Name != "FrontEndLayer").Sum(NetworkView.PlayingSounds);

    void LeaveCheckStep()
    {
        if (_leaveCheckAfter is not { } after) return;
        if (_leftAtElapsed is null)
        {
            if (_network is { } view && (_leaveCheckEarly || view.ConnectedSeconds >= after))
            {
                GD.Print($"leave-check: leaving after {view.ConnectedSeconds:F1} s connected (hosted server {(HostedServer.Running ? "running" : "not mine")})");
                if (_leaveCheckDouble && _launchArgs is { } launch)
                {
                    GD.Print("leave-check: starting a second view over the live one (the double-join orphan)");
                    StartNetwork(new Dictionary<string, string?>(launch, StringComparer.OrdinalIgnoreCase), _gameDir, keepOldForTest: true);
                    var children = GetChildren().OfType<NetworkView>().Count();
                    GD.Print($"leave-check: {children} networked views in the tree");
                    view = _network!;
                }
                _leaveCheckView = view;
                var atLeave = new List<string>();
                LeaveMatchNow();
                if (_leaveCheckTwice) LeaveMatchNow();   // the second ask finds nothing to leave
                if (IsInstanceValid(view))
                {
                    if (view.StatusTextNow.Length > 0) atLeave.Add("status text after the leave: " + view.StatusTextNow);
                    if (view.AnyLayerVisible) atLeave.Add("a layer of the view is still visible");
                    if (view.IsInsideTree()) atLeave.Add("the view is still in the tree");
                    if (view.ClientStatusNow == ClientStatus.Connected) atLeave.Add("the connection is still open");
                }
                _leaveCheckAtLeave = string.Join("; ", atLeave);
                _leftAtElapsed = _elapsed;
            }
            return;
        }
        if (_elapsed - _leftAtElapsed < _leaveCheckWait) return;
        var problems = new List<string>();
        if (Input.MouseMode != Input.MouseModeEnum.Visible) problems.Add($"mouse mode after the leave is {Input.MouseMode}, not Visible");
        if (_leaveCheckAtLeave.Length > 0) problems.Add(_leaveCheckAtLeave);
        var live = GetChildren().OfType<NetworkView>().Count();
        if (live != 0) problems.Add($"{live} networked view(s) still in the tree");
        if (_network is not null) problems.Add("the view reference is still set");
        if (_leaveCheckDouble && StrayDropped < 1) problems.Add("the orphan was not swept (no 'outlived its reference' drop)");
        int huds = CountType<Hud>(GetTree().Root);
        if (huds != 0) problems.Add($"{huds} HUD node(s) still in the tree");
        int sounds = SoundsOutside(this, _frontEnd?.GetParent());   // the front end's subtree and the menus' own sounds (UiSounds) are excluded
        if (sounds != 0) problems.Add($"{sounds} audio player(s) still playing");
        if (_leaveCheckView is { } v && IsInstanceValid(v) && v.IsInsideTree()) problems.Add("the view is still in the tree (late)");
        if (_leaveCheckView is null) problems.Add("no view was ever left");
        if (_frontEnd is null) problems.Add("the front end is not showing");
        if (++_leaveCheckCycle < _leaveCheckCycles)
        {
            GD.Print($"leave-check: cycle {_leaveCheckCycle}/{_leaveCheckCycles}: {(problems.Count == 0 ? "clean" : string.Join("; ", problems))}");
            _cycleProblems.AddRange(problems.Select(x => $"cycle {_leaveCheckCycle}: {x}"));
            if (_frontEnd is { } front) { front.Release(); front.GetParent()?.QueueFree(); _frontEnd = null; }
            _leaveCheckView = null; _leaveCheckAtLeave = ""; _leftAtElapsed = null; _leaveCheckDouble = false;
            StartNetwork(new Dictionary<string, string?>(_launchArgs, StringComparer.OrdinalIgnoreCase), _gameDir);   // the same join again
            return;
        }
        problems.InsertRange(0, _cycleProblems);
        if (_leaveCheckShot is { Length: > 0 } shot && !_forbiddenScreenshotDirs.Any(dir => SmFormats.InstallPaths.IsWithin(System.IO.Path.GetFullPath(shot), dir)))   // windowed run only: the menu as it stands after the wait
        {
            var err = GetViewport().GetTexture().GetImage().SavePng(shot);
            GD.Print($"leave-check: screenshot {(err == Error.Ok ? "saved" : "failed " + err)}");
        }
        GD.Print($"leave-check: {(problems.Count == 0 ? "PASS (no networked view, no HUD, no sound, connection closed, status empty, front end showing" + (_leaveCheckDouble ? ", orphan swept" : "") + ")" : "FAIL: " + string.Join("; ", problems))}");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    static int CountType<T>(Node n) where T : Node
    {
        int count = n is T ? 1 : 0;
        foreach (var c in n.GetChildren()) count += CountType<T>(c);
        return count;
    }
}
