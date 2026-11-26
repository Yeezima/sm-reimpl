using System;
using Godot;

namespace SmClient;

/// <summary>gore-streak capture aid: a node that calls <see cref="Tick"/> once a frame (the capture runs from the Extras folder without a core per-frame hook).</summary>
public partial class GoreStreakTicker : Node
{
    public Action? Tick;
    public override void _Process(double delta) => Tick?.Invoke();
}
