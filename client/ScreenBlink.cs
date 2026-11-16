using System;
using Godot;

namespace SmClient;

/// <summary>
/// The blink between screens (the owner, 2026-11-10: "transition between main menu and load screen and game to make it feel smoother. like blinking."): a black layer over
/// everything closes over <see cref="CloseSeconds"/>, stays black for <see cref="BlackFramesBefore"/> drawn frames (so a change that blocks the main thread, such as the match's
/// synchronous load, freezes a black window and not a half-dimmed menu), then the change runs, the black holds at least <see cref="HoldSeconds"/> and until the new screen has
/// settled (<see cref="SettleFrames"/> frames, none longer than <see cref="LongFrameSeconds"/>, and the caller's own readiness), and opens over <see cref="OpenSeconds"/>.
/// The black never takes input (mouse filter Ignore), and a readiness that does not come gives up after <see cref="MaxHoldSeconds"/> of real time, so an error under it shows.
/// A long frame advances the fades by at most <see cref="MaxStepSeconds"/>, so a load frame does not skip them. Ours (spec "Screen blink"); <c>--no-screen-blink</c>: no layer,
/// every change at once as before. Made by <see cref="ModelViewer"/> (never for bots); the networked view finds it through <see cref="Current"/>.
/// </summary>
public partial class ScreenBlink : CanvasLayer
{
    /// <summary>Seconds of the close (clear to black) and of the open (black to clear), eased in and out; the least time at full black after the change (ours, tuned by eye).</summary>
    public const double CloseSeconds = 0.18, OpenSeconds = 0.26, HoldSeconds = 0.08;

    /// <summary>The longest the black waits for the new screen after the change, in real seconds; then it opens anyway.</summary>
    public const double MaxHoldSeconds = 1.5;

    /// <summary>Frames drawn at full black before the change runs; frames after it before the open may start; a frame this long or longer is not settled yet.</summary>
    public const int BlackFramesBefore = 2, SettleFrames = 3;
    public const double LongFrameSeconds = 0.1;

    /// <summary>The most a single frame advances a fade or the hold.</summary>
    public const double MaxStepSeconds = 1.0 / 30.0;

    /// <summary>Above every other layer of the client (the menus' focus glow 110, the brightness overlay 120).</summary>
    public const int BlinkLayer = 127;

    /// <summary>The switch that turns the blink off.</summary>
    public const string OffSwitch = "no-screen-blink";

    /// <summary>The blink in the tree (null: none, the changes happen at once).</summary>
    public static ScreenBlink? Current { get; private set; }

    enum Phase { Clear, Closing, Black, Opening }

    readonly ColorRect _black;
    Phase _phase = Phase.Clear;
    double _progress;   // of the close or the open, 0 to 1
    Action? _atBlack;
    Func<bool>? _ready;
    bool _changed;   // the change of this blink has run
    int _blackFrames, _framesSince;
    double _held;
    ulong _changedAtMs;

    public ScreenBlink()
    {
        Name = "ScreenBlink";
        Layer = BlinkLayer;
        ProcessMode = ProcessModeEnum.Always;
        _black = new ColorRect { Name = "Black", Color = new Color(0f, 0f, 0f, 0f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _black.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_black);
    }

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
    }

    /// <summary>A blink is under way (closing, black or opening).</summary>
    public bool Busy => _phase != Phase.Clear;

    /// <summary>The black's opacity now, 0 (clear) to 1.</summary>
    public float Alpha => _black.Color.A;

    /// <summary>
    /// Closes, runs <paramref name="atBlack"/> at full black, holds until the new screen has settled and <paramref name="ready"/> (if given) says so, then opens.
    /// Asked while one runs: while closing, both changes run together at black; while black, this change runs now (after the first one) and the hold waits for both
    /// readinesses; while opening, the black closes again from where it is.
    /// </summary>
    public void Run(Action atBlack, Func<bool>? ready = null)
    {
        switch (_phase)
        {
            case Phase.Clear:
                _atBlack = atBlack; _ready = ready; _progress = 0; _phase = Phase.Closing;
                break;
            case Phase.Closing:
                _atBlack += atBlack; _ready = Both(_ready, ready);
                break;
            case Phase.Black when _changed:
                _ready = Both(_ready, ready);
                RunChanges(atBlack);
                break;
            case Phase.Black:
                _atBlack += atBlack; _ready = Both(_ready, ready);
                break;
            case Phase.Opening:
                // The open's alpha is 1 - ease(p) = ease(1 - p): the close continues from the same darkness.
                _atBlack = atBlack; _ready = ready; _progress = 1 - _progress; _phase = Phase.Closing;
                break;
        }
        Draw();
    }

    static Func<bool>? Both(Func<bool>? a, Func<bool>? b) => a is null ? b : b is null ? a : () => a() && b();

    public override void _Process(double delta)
    {
        if (_phase == Phase.Clear) return;
        double step = Math.Min(Math.Max(delta, 0.0), MaxStepSeconds);
        switch (_phase)
        {
            case Phase.Closing:
                _progress += step / CloseSeconds;
                if (_progress >= 1) { _progress = 1; _phase = Phase.Black; _blackFrames = 0; _changed = false; }
                break;
            case Phase.Black when !_changed:
                if (++_blackFrames < BlackFramesBefore) break;   // the black is on screen before a change that may block
                var change = _atBlack;
                _atBlack = null;
                RunChanges(change);
                break;
            case Phase.Black:
                _held += step;
                _framesSince++;
                bool settled = _held >= HoldSeconds && _framesSince >= SettleFrames && delta < LongFrameSeconds && Ready();
                bool waitedLong = (Time.GetTicksMsec() - _changedAtMs) / 1000.0 >= MaxHoldSeconds;
                if (settled || waitedLong)
                {
                    GD.Print(settled ? $"screen blink: opening, {(Time.GetTicksMsec() - _changedAtMs) / 1000.0:0.00} s at black after the change ({_framesSince} frames)"
                                     : $"screen blink: opening after {MaxHoldSeconds:0.0} s without the new screen settling");
                    _ready = null; _progress = 0; _phase = Phase.Opening;
                }
                break;
            case Phase.Opening:
                _progress += step / OpenSeconds;
                if (_progress >= 1) { _progress = 0; _phase = Phase.Clear; }
                break;
        }
        Draw();
    }

    /// <summary>Runs the changes one by one (one that throws is logged and does not stop the others or the open), then starts the hold.</summary>
    void RunChanges(Action? changes)
    {
        _changed = true;
        if (changes is not null)
            foreach (var d in changes.GetInvocationList())
            {
                try { ((Action)d)(); }
                catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { GD.PrintErr("screen blink: a screen change failed: " + e); }
            }
        _held = 0; _framesSince = 0; _changedAtMs = Time.GetTicksMsec();
    }

    bool Ready()
    {
        if (_ready is null) return true;
        try { return _ready(); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { GD.PrintErr("screen blink: readiness check failed: " + e.Message); return true; }
    }

    /// <summary>The black's opacity for the phase: smoothstep ease in and out on both fades.</summary>
    public static float AlphaAt(bool closing, double progress)
    {
        double p = Math.Clamp(progress, 0.0, 1.0);
        double eased = p * p * (3 - 2 * p);
        return (float)(closing ? eased : 1 - eased);
    }

    void Draw()
    {
        float a = _phase switch
        {
            Phase.Closing => AlphaAt(true, _progress),
            Phase.Black => 1f,
            Phase.Opening => AlphaAt(false, _progress),
            _ => 0f,
        };
        _black.Color = new Color(0f, 0f, 0f, a);
        bool visible = a > 0f;
        if (_black.Visible != visible) _black.Visible = visible;
    }
}
