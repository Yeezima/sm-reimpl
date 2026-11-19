using System;
using Godot;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>
/// Replaces a SpinBox's arrow behaviour (owner 2026-11-19): a right click on an arrow steps by <see cref="SpinSteps.RightClickStep"/> (Godot's own jumps
/// to the minimum or maximum), and a held left arrow repeats slowly at first, then faster (<see cref="SpinSteps.HoldMultiplier"/>). The spin box's
/// <c>gui_input</c> signal fires before its own handling, so an arrow press is taken here and marked handled; everything else (typing, dragging) is left alone.
/// </summary>
public sealed partial class SpinArrows : Node
{
    SpinBox _spin = null!;
    SpinKind _kind;
    int _dir;           // +1 / -1 while an arrow is held, 0 otherwise
    double _held;

    public SpinArrows() { }

    public static void Attach(SpinBox spin, SpinKind kind)
    {
        var a = new SpinArrows { _spin = spin, _kind = kind, Name = "SpinArrows" };
        spin.AddChild(a);
        spin.GuiInput += a.OnGuiInput;
        a.SetProcess(false);
    }

    /// <summary>The arrows sit at the right edge, the upper half is up. Measured from the theme's icon so a scaled editor still lands.</summary>
    bool OnArrows(Vector2 p, out int dir)
    {
        dir = 0;
        float w = _spin.GetThemeIcon("updown", "SpinBox")?.GetWidth() ?? 14;
        if (p.X < _spin.Size.X - w - 6 || p.X > _spin.Size.X || p.Y < 0 || p.Y > _spin.Size.Y) return false;
        dir = p.Y < _spin.Size.Y / 2 ? 1 : -1;
        return true;
    }

    void Add(double amount) { _spin.Value = Math.Round((_spin.Value + amount) / _spin.Step) * _spin.Step; }

    void OnGuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton b) return;
        if (!b.Pressed)
        {
            if (b.ButtonIndex == MouseButton.Left && _dir != 0) { _dir = 0; SetProcess(false); }
            return;
        }
        if (!OnArrows(b.Position, out int dir)) return;
        if (b.ButtonIndex == MouseButton.Right) { Add(dir * SpinSteps.RightClickStep(_kind)); _spin.AcceptEvent(); }
        else if (b.ButtonIndex == MouseButton.Left)
        {
            Add(dir * _spin.Step);
            _dir = dir; _held = 0; SetProcess(true);
            _spin.AcceptEvent();
        }
    }

    public override void _Process(double delta)
    {
        if (_dir == 0 || !Input.IsMouseButtonPressed(MouseButton.Left)) { _dir = 0; SetProcess(false); return; }
        double before = _held; _held += delta;
        int n = SpinSteps.RepeatsBetween(before, _held);
        if (n > 0) Add(_dir * n * _spin.Step * SpinSteps.HoldMultiplier(_held));
    }
}
