using System.Numerics;
using SmSim;

namespace SmNet;

/// <summary>
/// The buttons a human player presses between two input samples (feel/controls; client input handling, ours). The simulation samples one
/// input per 30 Hz tick and acts on press edges, so a button that was pressed and let go between two samples (a quick click of Fire, a tap of
/// melee, Space or G) used to reach it as never pressed, and a quick release-and-press of a held button as no edge at all. The client
/// notes every press and release event here and folds them into the next sample (<see cref="Sample"/>):
/// <list type="bullet">
/// <item>a press since the last sample counts as held for that sample, even when the button is up again (a tap is one tick long);</item>
/// <item>a button held at the last sample that was let go since reads as released for this sample, so the next press is an edge; a press that
/// came after that release (and was let go again) is carried to the next sample.</item>
/// </list>
/// Two whole taps of one button within one tick (33 ms) still count as one. Engine-free, so the rules are tested (<c>ControlFeelTests</c>).
/// </summary>
public sealed class InputLatch
{
    InputButtons _pressed, _released, _pressedAfterRelease, _carry, _lastSampled;
    bool _moved;

    /// <summary>A press or release (or a movement key change, <see cref="MoveChanged"/>) happened since the last sample.</summary>
    public bool Pending => (_pressed | _released | _carry) != InputButtons.None || _moved;

    /// <summary>The buttons the last <see cref="Sample"/> reported.</summary>
    public InputButtons LastSampled => _lastSampled;

    /// <summary>A press event of these buttons (not a key repeat).</summary>
    public void Press(InputButtons buttons)
    {
        _pressedAfterRelease |= buttons & _released;
        _pressed |= buttons;
    }

    /// <summary>A release event of these buttons.</summary>
    public void Release(InputButtons buttons) => _released |= buttons;

    /// <summary>A movement key went down or up (no button of its own: the stick is read held; it only makes <see cref="Pending"/> true).</summary>
    public void MoveChanged() => _moved = true;

    /// <summary>
    /// The buttons for this sample from the held ones (<paramref name="held"/>, read from the device now) and the events since the last sample,
    /// by the rules in the class comment. Clears the events.
    /// </summary>
    public InputButtons Sample(InputButtons held)
    {
        var result = held | _pressed | _carry;
        _carry = InputButtons.None;
        // Held at the last sample and let go since: released this sample (so the next press is an edge); a press after the release goes to the next.
        var letGo = _lastSampled & _released;
        if (letGo != InputButtons.None)
        {
            _carry = letGo & _pressedAfterRelease & ~held;
            result &= ~letGo;
        }
        _pressed = _released = _pressedAfterRelease = InputButtons.None;
        _moved = false;
        _lastSampled = result;
        return result;
    }

    /// <summary>Forgets every event and the last sample (death, a menu, the mouse released).</summary>
    public void Reset()
    {
        _pressed = _released = _pressedAfterRelease = _carry = _lastSampled = InputButtons.None;
        _moved = false;
    }
}

/// <summary>
/// The rules of the client's early tick (feel/controls, ours): a press or release of a game button runs the next client tick at once
/// (<see cref="GameClient.Update(System.Func{uint, PlayerInput}, bool)"/>) instead of waiting up to a tick (33 ms) for its scheduled time.
/// The input still belongs to one tick number, the simulation steps exactly one tick for it and the server applies it at that tick, so
/// prediction, reconciliation and the server agree as before; only the moment the client samples it moves. The tick schedule is kept
/// (the tick after an early one is due at its own scheduled time), so the client is never more than one tick ahead of it. The own position
/// is drawn on the schedule (<see cref="DrawsBehind"/>: no step and no stall); the early tick's events, velocity and clip starts show at once.
/// </summary>
public static class EarlyTick
{
    /// <summary>
    /// Whether a tick may run early now: the next one is not due yet (a due tick runs anyway) and is at most one tick away, so the client is
    /// never more than one tick ahead of its schedule. Early ticks can follow one another (one per scheduled tick that passes); the caller also
    /// skips it when a scheduled tick ran in the same update (that tick already sampled the edge).
    /// </summary>
    public static bool Allowed(double now, double nextTickAt, double tickSeconds) =>
        tickSeconds > 0 && double.IsFinite(now) && double.IsFinite(nextTickAt) && now < nextTickAt && nextTickAt - now <= tickSeconds;

    /// <summary>
    /// Whether the own player is drawn one pair behind (the two states before the newest): the newest tick ran early and its scheduled time
    /// has not come yet (the next due tick is more than a tick away). The drawn position then keeps the schedule's timeline.
    /// </summary>
    public static bool DrawsBehind(bool newestRanEarly, double now, double nextTickAt, double tickSeconds) =>
        newestRanEarly && tickSeconds > 0 && nextTickAt - now > tickSeconds;

    /// <summary>
    /// The clip fraction after an early tick: 0 when it ran (<paramref name="ranAt"/>), 1 when the next tick is due (clamped; an empty span
    /// gives 1). A clip the early tick started is drawn from its start at once and reaches the predicted time when the next tick runs.
    /// </summary>
    public static float ClipFraction(double now, double ranAt, double nextTickAt)
    {
        double span = nextTickAt - ranAt;
        if (!(span > 1e-9)) return 1f;
        double f = (now - ranAt) / span;
        return double.IsFinite(f) ? (float)Math.Clamp(f, 0.0, 1.0) : 1f;
    }
}

/// <summary>
/// The own player's clip time drawn at the frame rate (feel/controls, ours). The predicted state's clip timers (melee attack, throw, reaction,
/// charge) change once per tick; posing the clip at them showed each pose for two frames at 60 fps and four or five at 144 fps. The drawn time
/// is the predicted one a tick back plus the fraction of the tick elapsed (as the own position is drawn, M36:
/// <see cref="ViewSmoothing.Interpolate"/>), never before 0, never past the predicted value, and never backwards within one clip.
/// </summary>
public sealed class OwnClipClock
{
    long _key;
    float _lastShown, _lastPredicted;

    /// <summary>True while a clip is being followed.</summary>
    public bool Active { get; private set; }

    /// <summary>The drawn time for a clip whose predicted time is <paramref name="predicted"/> (a tick back plus the fraction).</summary>
    public static float Time(float predicted, float fraction, float tickSeconds)
    {
        if (!float.IsFinite(predicted)) return 0f;
        float f = float.IsFinite(fraction) ? Math.Clamp(fraction, 0f, 1f) : 1f;
        return Math.Clamp(predicted - (1f - f) * MathF.Max(tickSeconds, 0f), 0f, MathF.Max(predicted, 0f));
    }

    /// <summary>The drawn time this frame; <paramref name="key"/> names the clip (a change, or the predicted time going back, restarts it).</summary>
    public float Step(long key, float predicted, float fraction, float tickSeconds, float length = float.MaxValue)
    {
        float shown = Time(predicted, fraction, tickSeconds);
        bool restart = !Active || key != _key || predicted < _lastPredicted - 1e-4f;
        if (!restart) shown = MathF.Max(shown, MathF.Min(_lastShown, MathF.Max(predicted, 0f)));
        shown = Math.Clamp(shown, 0f, MathF.Max(length, 0f));
        Active = true; _key = key; _lastShown = shown; _lastPredicted = predicted;
        return shown;
    }

    /// <summary>The player left the clip.</summary>
    public void Stop() => Active = false;
}

/// <summary>
/// The converged shot's camera ray brought up to the view of this moment (feel/controls, ours). The crosshair ray was read from the camera as
/// the previous frame placed it, while the input's yaw and pitch already carried this frame's mouse movement: the shot left one frame of mouse
/// movement behind the crosshair the frame shows (at 60 fps and a 300 degrees a second flick, 5 degrees). The ray is turned about the vertical
/// through the player's feet by the yaw the view has turned since the camera was placed, and its elevation raised by the pitch change.
/// </summary>
public static class FreshAim
{
    /// <summary>
    /// The ray turned as a rigid whole (camera/hop-and-walls): by <paramref name="deltaYaw"/> about the vertical through <paramref name="pivot"/>, then by <paramref name="deltaPitch"/>
    /// about the horizontal axis to the right of a view at <paramref name="viewYaw"/> (the yaw after the turn). No clamp and no read-back: a ray off the view (<c>CameraBoom.StableLook</c>)
    /// keeps its offset at the pitch limits and past the vertical; the caller's <paramref name="deltaPitch"/> is the view's own, within its limits.
    /// </summary>
    public static (Vector3 Origin, Vector3 Direction) TurnRigid(Vector3 origin, Vector3 direction, Vector3 pivot, float deltaYaw, float deltaPitch, float viewYaw)
    {
        if (!float.IsFinite(deltaYaw) || !float.IsFinite(deltaPitch) || !float.IsFinite(viewYaw) || direction.LengthSquared() < 1e-12f) return (origin, direction);
        float s = MathF.Sin(deltaYaw), c = MathF.Cos(deltaYaw);
        var rel = origin - pivot;
        var o = pivot + new Vector3(rel.X * c - rel.Y * s, rel.X * s + rel.Y * c, rel.Z);
        var d = Vector3.Normalize(direction);
        d = new Vector3(d.X * c - d.Y * s, d.X * s + d.Y * c, d.Z);
        if (deltaPitch != 0f)
        {
            var axis = new Vector3(MathF.Cos(viewYaw), MathF.Sin(viewYaw), 0f);
            float sp = MathF.Sin(deltaPitch), cp = MathF.Cos(deltaPitch);
            d = Vector3.Normalize(d * cp + Vector3.Cross(axis, d) * sp + axis * (Vector3.Dot(axis, d) * (1f - cp)));   // Rodrigues
        }
        return (o, d);
    }

    /// <summary>The ray (origin, unit direction; game space, Z up) turned by <paramref name="deltaYaw"/> about the vertical through <paramref name="pivot"/>, and its pitch raised by <paramref name="deltaPitch"/> (kept within ±<paramref name="maxPitch"/>).</summary>
    public static (Vector3 Origin, Vector3 Direction) Turn(Vector3 origin, Vector3 direction, Vector3 pivot, float deltaYaw, float deltaPitch, float maxPitch) =>
        Turn(origin, direction, pivot, deltaYaw, deltaPitch, -maxPitch, maxPitch);

    /// <summary>
    /// As the symmetric overload with separate limits (sim/roll-fire-pitch: the view goes up to 89.9 degrees but down only to 1.4 rad): the raised pitch is kept
    /// within [<paramref name="minPitch"/>, <paramref name="maxPitch"/>] (radians, positive up).
    /// </summary>
    public static (Vector3 Origin, Vector3 Direction) Turn(Vector3 origin, Vector3 direction, Vector3 pivot, float deltaYaw, float deltaPitch, float minPitch, float maxPitch)
    {
        if (!float.IsFinite(deltaYaw) || !float.IsFinite(deltaPitch) || direction.LengthSquared() < 1e-12f) return (origin, direction);
        float s = MathF.Sin(deltaYaw), c = MathF.Cos(deltaYaw);
        var rel = origin - pivot;
        var o = pivot + new Vector3(rel.X * c - rel.Y * s, rel.X * s + rel.Y * c, rel.Z);
        var d = Vector3.Normalize(direction);
        d = new Vector3(d.X * c - d.Y * s, d.X * s + d.Y * c, d.Z);
        if (deltaPitch != 0f)
        {
            float yaw = MathF.Atan2(-d.X, d.Y);   // Combat.AimDirection: (-sin yaw cos pitch, cos yaw cos pitch, sin pitch)
            float pitch = MathF.Asin(Math.Clamp(d.Z, -1f, 1f));
            d = Combat.AimDirection(yaw, Math.Clamp(pitch + deltaPitch, MathF.Min(minPitch, maxPitch), maxPitch));
        }
        return (o, d);
    }
}
