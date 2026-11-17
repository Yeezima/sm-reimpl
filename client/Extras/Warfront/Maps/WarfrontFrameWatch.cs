using Godot;

namespace SmClient;

/// <summary>
/// The main-thread frame gaps of the travel window (slice W5d; test aid, client only): a node on the scene root, so it survives the rejoin's new view and sees the frames of the connect and the map load too. <see cref="Begin"/> when the Stage
/// section names a lane slot (the warm-up starts), <see cref="End"/> at the landing: one log line with the frames seen, the longest and how many were over a second (the acceptance: none), and a line for every frame over half a second as it happens.
/// A frame's length is the next frame's delta, so a stall shows in the frame after it.
/// </summary>
public partial class WarfrontFrameWatch : Node
{
    static WarfrontFrameWatch? _node;
    static bool _installing;
    bool _active;
    double _max, _window, _longestAt;
    int _frames, _over;

    public static void Begin()
    {
        if (_node is null || !IsInstanceValid(_node))
        {
            if (_installing || Engine.GetMainLoop() is not SceneTree tree) return;
            _installing = true;
            var node = new WarfrontFrameWatch { Name = "WarfrontFrameWatch", ProcessMode = ProcessModeEnum.Always };
            tree.Root.CallDeferred(Node.MethodName.AddChild, node);
            _node = node; _installing = false;
        }
        _node!._active = true; _node._max = 0; _node._window = 0; _node._frames = 0; _node._over = 0;
    }

    public static void End(string where)
    {
        if (_node is null || !IsInstanceValid(_node) || !_node._active) return;
        var n = _node; n._active = false;
        GD.Print($"warfront: travel window main-thread frames ({where}): {n._frames} seen over {n._window:F1} s, longest {n._max * 1000:F0} ms (at {n._longestAt:F1} s), {n._over} over 1 s");
    }

    public override void _Process(double delta)
    {
        if (!_active) return;
        _frames++; _window += delta;
        if (delta > _max) { _max = delta; _longestAt = _window; }
        if (delta > 1.0) _over++;
        if (delta > 0.5) GD.Print($"warfront: long main-thread frame {delta * 1000:F0} ms at {_window:F1} s of the travel window");
    }
}
