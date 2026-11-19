using Godot;

namespace SmClient;

/// <summary>
/// The main-thread frame gaps of the travel window (slice W5d; test aid, client only): a node on the scene root, so it survives the rejoin's new view and sees the frames of the connect and the map load too. <see cref="Begin"/> when the Stage
/// section names a lane slot (the warm-up starts), <see cref="End"/> at the landing: one log line with the frames seen, the longest and how many were over a second (the acceptance: none), and a line for every frame over half a second as it happens.
/// The length is REAL time between two <c>_Process</c> calls (<see cref="Time.GetTicksUsec"/>), not Godot's <c>delta</c>, which the engine clamps to about 133 ms (the first pass read 148 ms for frames that took 0.7 to 1 s). A stall shows in the frame after it.
/// A test aid that stays in the product path (a node on the scene root for the life of the client, idle outside the travel window); named so in docs/commands.md.
/// </summary>
public partial class WarfrontFrameWatch : Node
{
    static WarfrontFrameWatch? _node;
    static bool _installing;
    bool _active;
    double _max, _window, _longestAt;
    long _lastUs;
    int _frames, _over;

    static void Install()
    {
        if (_node is not null && IsInstanceValid(_node)) return;
        if (_installing || Engine.GetMainLoop() is not SceneTree tree) return;
        _installing = true;
        var node = new WarfrontFrameWatch { Name = "WarfrontFrameWatch", ProcessMode = ProcessModeEnum.Always };
        tree.Root.CallDeferred(Node.MethodName.AddChild, node);
        _node = node; _installing = false;
    }

    string? _stepNote;

    /// <summary>A band drawing step ran just now: the real length of the NEXT frame (the one that draws what the step built) is logged under its name, inside or outside the travel window.</summary>
    public static void NoteStep(string slot, string step)
    {
        Install();
        if (_node is not null) _node._stepNote = $"{slot} after step \"{step}\"";
    }

    public static void Begin()
    {
        Install();
        if (_node is null) return;
        _node!._active = true; _node._max = 0; _node._window = 0; _node._frames = 0; _node._over = 0;
    }

    public static void End(string where)
    {
        if (_node is null || !IsInstanceValid(_node) || !_node._active) return;
        var n = _node; n._active = false;
        GD.Print($"warfront: travel window main-thread frames, real time between frames ({where}): {n._frames} seen over {n._window:F1} s, longest {n._max * 1000:F0} ms (at {n._longestAt:F1} s), {n._over} over 1 s");
    }

    public override void _Process(double delta)
    {
        long nowUs = (long)Time.GetTicksUsec();
        if (_lastUs == 0) { _lastUs = nowUs; return; }   // the first frame has no predecessor
        delta = (nowUs - _lastUs) / 1e6; _lastUs = nowUs;   // real time between frames, not the engine's clamped delta
        if (_stepNote is not null) { GD.Print($"warfront: frame {_stepNote}: {delta * 1000:F0} ms (real time)"); _stepNote = null; }
        if (!_active) return;
        _frames++; _window += delta;
        if (delta > _max) { _max = delta; _longestAt = _window; }
        if (delta > 1.0) _over++;
        if (delta > 0.5) GD.Print($"warfront: long main-thread frame {delta * 1000:F0} ms at {_window:F1} s of the travel window");
    }
}
