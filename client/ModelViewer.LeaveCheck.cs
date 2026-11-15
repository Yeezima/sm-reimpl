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
/// <c>--leave-check-wait &lt;s&gt;</c> changes the wait (default 8). Exit code 0 passes, 1 fails. A server stopped during the wait (the hosted server's shutdown) must not change the result.
/// </summary>
public partial class ModelViewer
{
    double? _leaveCheckAfter, _leftAtElapsed;
    double _leaveCheckWait = 8.0;
    NetworkView? _leaveCheckView;

    void LeaveCheckSetup(Dictionary<string, string?> args)
    {
        if (!args.ContainsKey("leave-check")) return;
        _leaveCheckAfter = double.TryParse(args.GetValueOrDefault("leave-check"), System.Globalization.CultureInfo.InvariantCulture, out var after) && after > 0 ? after : 4.0;
        if (double.TryParse(args.GetValueOrDefault("leave-check-wait"), System.Globalization.CultureInfo.InvariantCulture, out var wait) && wait > 0) _leaveCheckWait = wait;
    }

    void LeaveCheckStep()
    {
        if (_leaveCheckAfter is not { } after) return;
        if (_leftAtElapsed is null)
        {
            if (_network is { } view && view.ConnectedSeconds >= after)
            {
                _leaveCheckView = view;
                GD.Print($"leave-check: leaving after {view.ConnectedSeconds:F1} s connected (hosted server {(HostedServer.Running ? "running" : "not mine")})");
                LeaveMatchNow();
                _leftAtElapsed = _elapsed;
            }
            return;
        }
        if (_elapsed - _leftAtElapsed < _leaveCheckWait) return;
        var problems = new List<string>();
        var live = GetChildren().OfType<NetworkView>().Count();
        if (live != 0) problems.Add($"{live} networked view(s) still in the tree");
        if (_network is not null) problems.Add("the view reference is still set");
        int sounds = GetChildren().Where(c => c is not UiSounds).Sum(NetworkView.PlayingSounds);   // the menus' own sounds (UiSounds) may play on the front end
        if (sounds != 0) problems.Add($"{sounds} audio player(s) still playing");
        if (_leaveCheckView is { } v)
        {
            if (IsInstanceValid(v))   // still alive: it must at least be out of the tree and marked left
            {
                if (!v.Left) problems.Add("the view was not marked left");
                if (v.ClientStatusNow == ClientStatus.Connected) problems.Add("the connection is still open");
                if (v.IsInsideTree()) problems.Add("the view is still in the tree");
            }
            // else: freed, which closed its connection in its exit
        }
        else problems.Add("no view was ever left");
        if (_frontEnd is null) problems.Add("the front end is not showing");
        GD.Print($"leave-check: {(problems.Count == 0 ? "PASS (no networked view, no sound, connection closed, no error, front end showing)" : "FAIL: " + string.Join("; ", problems))}");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }
}
