using System;
using Godot;

namespace SmClient;

// fix/bolter-hud-last-round: screenshot aid for the weapon box's clip display (the bolter's tick strip).

public sealed partial class Hud
{
    /// <summary><c>--hud-clip-demo &lt;prefix&gt;</c> (screenshot aid): in a match, shows the own weapon's clip full, then 3, 2, 1 and 0 rounds left (the reserve and the weapon unchanged),
    /// saving the window as <c>&lt;prefix&gt;_N.png</c> for each, then quits.</summary>
    static readonly string? ClipDemo = ReadClipDemo();
    double _clipDemoAt;
    int _clipDemoStep;

    static string? ReadClipDemo()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--hud-clip-demo");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    void RunClipDemo()
    {
        if (ClipDemo is null || !Data.InGame || Data.ClipMax <= 0) return;
        var d = Data;
        int[] steps = [d.ClipMax, 3, 2, 1, 0];
        _clipDemoAt += GetProcessDeltaTime();
        if (_clipDemoStep >= steps.Length) { GetTree().Quit(); return; }
        d.Alive = true; d.Clip = steps[_clipDemoStep];
        if (_clipDemoAt < 3.0) return;
        GD.Print(GetViewport().GetTexture().GetImage().SavePng($"{ClipDemo}_{_clipDemoStep}.png") == Error.Ok ? $"clip shot {_clipDemoStep} (clip {d.Clip}/{d.ClipMax}) -> {ClipDemo}_{_clipDemoStep}.png" : "clip shot failed");
        _clipDemoStep++; _clipDemoAt = 2.0;
    }
}
