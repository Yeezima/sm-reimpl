using System;
using System.Collections.Generic;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// anim/hammer-swing capture aid <c>--swing-test stand|walk[,heavy]</c> (with <c>--bot</c>; not original behaviour): the bot swings its melee weapon's light chain (X, X, X: each press
/// as soon as the strike's combo window is open) with nobody in reach, every <c>--swing-cycle</c> seconds (default 9), standing (<c>stand</c>) or holding W through the chain and
/// after it (<c>walk</c>), then turns round. It has the human follow camera (<c>--swing-pitch</c>, default -0.2 rad) and logs one <c>SWINGTEST</c> line per press and per cycle.
/// <c>--swing-shots &lt;dir&gt;</c> (a windowed client; a spectator with <c>--observe --observe-follow</c> works) saves a frame every <c>--swing-shot-every</c> seconds (default 0.1) from the first strike
/// of a chain for <c>--swing-shot-for</c> seconds (default 4.5) as <c>swing&lt;set&gt;_&lt;ms&gt;.png</c>, for at most <c>--swing-shots-max</c> chains (default 1).
/// </summary>
public partial class NetworkView
{
    static readonly string? SwingTestMode = CmdArg("swing-test");
    static bool SwingTest => SwingTestMode is { Length: > 0 };
    static readonly float SwingPitch = float.TryParse(CmdArg("swing-pitch"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var swp) && float.IsFinite(swp) ? swp : -0.2f;
    static readonly string? SwingShotsDir = CmdArg("swing-shots");
    static readonly float SwingCycle = Math.Max(4f, CmdFloat("--swing-cycle", 9f));
    static readonly double SwingShotEvery = Math.Max(0.02f, CmdFloat("--swing-shot-every", 0.1f)), SwingShotFor = CmdFloat("--swing-shot-for", 4.5f);
    static readonly int SwingShotsMax = (int)CmdFloat("--swing-shots-max", 1f);
    double _swingStart = -1, _swingCycleStart = double.NaN, _swingShotLast = double.NegativeInfinity;
    float _swingYaw;
    int _swingCycle = -1, _swingPressedStep = -1, _swingShotSet;
    bool _swingPressedFirst;

    PlayerInput? SwingTestInput(PlayerState me, uint tick)
    {
        if (!SwingTest || !me.Alive || _config is null) { _swingStart = -1; return null; }
        if (_swingStart < 0) { _swingStart = _connectedFor + 1.5; _swingYaw = _yaw; }
        double t = _connectedFor - _swingStart;
        if (t < 0) return new PlayerInput(tick, 0f, 0f, _swingYaw, InputButtons.None, SwingPitch);
        int cycle = (int)(t / SwingCycle);
        double phase = t - cycle * SwingCycle;
        if (cycle != _swingCycle)
        {
            if (_swingCycle >= 0) _swingYaw = ViewSmoothing.WrapPi(_swingYaw + MathF.PI);   // back the way it came
            _swingCycle = cycle; _swingPressedStep = -1; _swingPressedFirst = false;
        }
        _yaw = _swingYaw; _pitch = SwingPitch;
        bool walk = SwingTestMode!.StartsWith("w", StringComparison.OrdinalIgnoreCase);
        float forward = walk && phase >= 0.05 ? 1f : 0f;
        var buttons = InputButtons.None;
        if (!_swingPressedFirst && phase >= 0.3 && me.MeleeStep == 0 && !me.MeleeHeld)
        {
            buttons = InputButtons.Melee; _swingPressedFirst = true;
            GD.Print($"SWINGTEST t {_connectedFor:F2} cycle {cycle}: press 1 at ({me.Position.X:F1}, {me.Position.Y:F1}) yaw {_swingYaw:F2}");
        }
        else if (_swingPressedFirst && me.MeleeStep > 0 && !me.MeleeHeavy && me.MeleeStep != _swingPressedStep && !me.MeleeHeld && !me.MeleeQueued
            && Combat.MeleeStepOf(_config, me) is { } step && me.MeleeStep < 3 && me.MeleeTime >= step.ComboOpen + 0.03f)
        {
            buttons = InputButtons.Melee; _swingPressedStep = me.MeleeStep;
            GD.Print($"SWINGTEST t {_connectedFor:F2} cycle {cycle}: press {me.MeleeStep + 1} at strike time {me.MeleeTime:F2}");
        }
        if (SwingTestMode.Contains("heavy", StringComparison.OrdinalIgnoreCase) && me.MeleeStep == 2 && !me.MeleeHeavy && !me.MeleeHeld) buttons = InputButtons.MeleeHeavy;
        return new PlayerInput(tick, 0f, forward, _swingYaw, buttons, SwingPitch);
    }

    /// <summary>
    /// Per frame after drawing (any client with <c>--swing-shots</c>, a spectator too): the frame strip of the first melee strike a drawn player starts (the chain's first press): a frame every
    /// <c>--swing-shot-every</c> seconds for <c>--swing-shot-for</c> seconds; the next strip waits until nobody has struck for 1.5 s.
    /// </summary>
    void SwingTestFrame(IReadOnlyList<PlayerState> all)
    {
        if (SwingShotsDir is null || _swingShotSet >= SwingShotsMax) return;
        bool striking = false;
        foreach (var p in all) if (p.Alive && (p.MeleeStep > 0 || p.JumpSlash)) { striking = true; break; }
        if (striking) _swingLastStrike = _connectedFor;
        if (!double.IsFinite(_swingCycleStart))
        {
            if (!striking || _connectedFor - _swingShotArmedAt < 1.5) return;
            _swingCycleStart = _connectedFor; _swingShotLast = double.NegativeInfinity;
        }
        double since = _connectedFor - _swingCycleStart;
        if (since > SwingShotFor)
        {
            // Saving a frame takes longer than the frame: the strip is kept in memory while it runs and written when it is over.
            System.IO.Directory.CreateDirectory(SwingShotsDir);
            foreach (var (ms, image) in _swingFrames)
            {
                string path = System.IO.Path.Combine(SwingShotsDir, $"swing{_swingShotSet}_{ms:D4}.png");
                var err = image.SavePng(path);
                if (err != Error.Ok) GD.Print($"swing shot failed: {err}");
            }
            GD.Print($"swing shots -> {SwingShotsDir}: {_swingFrames.Count} frames of strip {_swingShotSet}");
            _swingFrames.Clear();
            _swingShotSet++; _swingCycleStart = double.NaN; _swingShotArmedAt = _swingLastStrike; return;
        }
        if (_swingShotLast > double.NegativeInfinity && since - _swingShotLast < SwingShotEvery - 1e-6) return;
        _swingFrames.Add(((int)Math.Round(since * 1000), GetViewport().GetTexture().GetImage()));
        _swingShotLast = since;
    }
    readonly List<(int Ms, Image Image)> _swingFrames = new();
    double _swingLastStrike = double.NegativeInfinity, _swingShotArmedAt = double.NegativeInfinity;
}
