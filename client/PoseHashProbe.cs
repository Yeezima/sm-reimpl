using System;
using System.IO;
using System.Linq;
using Godot;

namespace SmClient;

/// <summary>
/// <c>--pose-hash &lt;file&gt;</c> (animation pass 1 test aid, ours): writes one line per rendered frame and drawn character, <c>frame name hash</c>, where the hash is a 64-bit FNV-1a
/// over every bone's pose (rotation and position rounded to 1e-4) after all poses of the frame are applied. Two builds (or two flag sets) that run the same demo replay
/// (<c>--play-demo</c>, headless, <c>--fixed-fps 60</c>) draw the same poses exactly when their files are identical: the check that a change to the pose pipeline (the layer stack)
/// leaves the layers it did not touch as they were (recoil, hand-off, reload). Free when off (the node is not created); never run with a real-time session. Relative names go next to the
/// working directory, so give an absolute path outside the repository.
/// </summary>
public partial class PoseHashProbe : Node
{
    static readonly string? OutPath = ReadPath();
    static PoseHashProbe? _instance;
    StreamWriter? _writer;
    long _frame;

    public static bool Enabled => OutPath is not null;

    static string? ReadPath()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--pose-hash");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static void EnsureStarted(Node from)
    {
        if (OutPath is null || _instance is not null || !GodotObject.IsInstanceValid(from)) return;
        _instance = new PoseHashProbe { Name = "PoseHashProbe", ProcessPriority = 2000 };   // after every character (-10), the networked view and the animation probe (1000)
        from.GetTree().Root.CallDeferred(Node.MethodName.AddChild, _instance);
        GD.Print($"pose hash probe on: {OutPath}");
    }

    public override void _Ready() => _writer = new StreamWriter(OutPath!, append: false) { AutoFlush = false };

    public override void _ExitTree() { _writer?.Flush(); _writer?.Dispose(); }

    public override void _Process(double delta)
    {
        if (_writer is null) return;
        _frame++;
        foreach (var c in CharacterViewer.Live.OrderBy(c => c.Name.ToString(), StringComparer.Ordinal))
        {
            if (c.Skeleton is not { } skeleton || !c.IsVisibleInTree()) continue;
            ulong h = 14695981039346656037UL;
            static void Mix(ref ulong h, float v) { h = (h ^ (ulong)(long)MathF.Round(v * 1e4f)) * 1099511628211UL; }
            int n = skeleton.GetBoneCount();
            for (int b = 0; b < n; b++)
            {
                var q = skeleton.GetBonePoseRotation(b);
                var p = skeleton.GetBonePosePosition(b);
                Mix(ref h, q.X); Mix(ref h, q.Y); Mix(ref h, q.Z); Mix(ref h, q.W); Mix(ref h, p.X); Mix(ref h, p.Y); Mix(ref h, p.Z);
            }
            _writer.WriteLine($"{_frame} {c.Name} {h:X16}");
        }
    }
}
