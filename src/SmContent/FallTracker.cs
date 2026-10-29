namespace SmContent;

/// <summary>What <see cref="FallTracker.Observe"/> saw this frame.</summary>
public enum FallEvent
{
    /// <summary>Nothing began or ended.</summary>
    None,
    /// <summary>The marine has been unsupported for the debounce time: the fall pose starts.</summary>
    Began,
    /// <summary>A fall that had begun ended on the ground (alive, eligible): the landing.</summary>
    Landed,
    /// <summary>A fall that had begun ended without a landing (died, or a roll, charge, reaction or the jump pack took over).</summary>
    Aborted,
}

/// <summary>
/// The per-marine state machine of the fall pose (animation pass 1; engine-free so it can be tested, as <c>ActionHandoff</c> and <c>FootPinRules</c>): fed once per drawn frame with
/// whether the marine is <b>eligible</b> to fall (alive, no jump-pack phase, not rolling, charging or reacting, no client jump view: the caller's rule), whether it is supported by the
/// ground and its vertical speed, it reports when a fall begins (unsupported for <see cref="FallRules.DebounceSeconds"/>, so one or two ticks of a stair crest are not a fall), what the
/// worst downward speed of the spell was, when it lands or is aborted, and resets itself after a gap in the frames (the player was not shown, was respawned). Also the heavy bolter's
/// two states (the drop clip from <see cref="FallSet.EnterHeight"/>, the landing from <see cref="FallSet.LandHeight"/>) and the landing clip's window.
/// </summary>
public sealed class FallTracker
{
    /// <summary>A gap longer than this between two frames (seconds) forgets the fall (ours).</summary>
    public const double GapSeconds = 0.25;

    public bool InAir { get; private set; }
    public bool Falling { get; private set; }
    /// <summary>When the unsupported spell began, and when the fall pose did (render clock, seconds).</summary>
    public double AirSince { get; private set; } = double.NegativeInfinity;
    public double FallSince { get; private set; } = double.NegativeInfinity;
    /// <summary>The largest downward speed of this spell in the air, m/s (a falling body only speeds up; the interpolated vertical speed of a remote player dips toward 0 at the touchdown frame).</summary>
    public float PeakDown { get; private set; }
    /// <summary>The air time of the fall that just landed or was aborted (valid after <see cref="FallEvent.Landed"/> / <see cref="FallEvent.Aborted"/>).</summary>
    public double LastAirTime { get; private set; }
    /// <summary>True after an <see cref="Observe"/> that found a gap and forgot a fall (the caller drops what hangs on it, e.g. a fading layer).</summary>
    public bool WasReset { get; private set; }

    // Heavy bolter.
    public bool DropStarted { get; private set; }
    public double DropSince { get; private set; } = double.NegativeInfinity;
    public bool HeavyLanded { get; private set; }

    // Landing clip window.
    public bool LandHard { get; private set; }
    public double LandAt { get; private set; } = double.NegativeInfinity;

    double _seenAt = double.NegativeInfinity;

    /// <summary>
    /// One frame. <paramref name="eligible"/>: the marine may fall (see the class comment); <paramref name="grounded"/>: it is supported; <paramref name="verticalSpeed"/>: m/s, up positive.
    /// </summary>
    /// <param name="immediate">The fall begins on the first unsupported frame, without the debounce (animation pass 3: a charge held in the air off a ledge).</param>
    public FallEvent Observe(double now, bool eligible, bool grounded, float verticalSpeed, bool immediate = false)
    {
        WasReset = false;
        bool gap = !(now - _seenAt <= GapSeconds);
        _seenAt = now;
        if (gap)
        {
            WasReset = Falling || LandHard;
            Falling = false; InAir = false; LandHard = false;
        }
        if (eligible && !grounded)
        {
            float down = float.IsFinite(verticalSpeed) ? MathF.Max(0f, -verticalSpeed) : 0f;
            if (!InAir) { InAir = true; AirSince = now; PeakDown = 0f; }
            PeakDown = MathF.Max(PeakDown, down);
            if (!Falling && (immediate || now - AirSince >= FallRules.DebounceSeconds - 1e-6))
            {
                Falling = true; FallSince = now;
                LandHard = false; DropStarted = false; HeavyLanded = false;
                return FallEvent.Began;
            }
            return FallEvent.None;
        }
        var result = FallEvent.None;
        if (Falling)
        {
            LastAirTime = now - AirSince;
            result = eligible && grounded ? FallEvent.Landed : FallEvent.Aborted;
        }
        InAir = false;
        Falling = false;
        return result;
    }

    /// <summary>
    /// The heavy bolter's states for a falling marine <paramref name="height"/> metres above the ground: the drop clip starts when the height reaches <paramref name="enterHeight"/>, the landing
    /// clip when it then falls to <paramref name="landHeight"/>. Returns true on the frame the landing clip begins (it can begin before the touchdown; the touchdown then only ends the fall).
    /// </summary>
    public bool HeavyStep(double now, float height, float enterHeight, float landHeight)
    {
        if (!Falling || HeavyLanded) return false;
        if (!DropStarted && height >= enterHeight) { DropStarted = true; DropSince = now; }
        if (DropStarted && height <= landHeight) { HeavyLanded = true; return true; }
        return false;
    }

    /// <summary>Starts the landing at <paramref name="now"/>; <paramref name="hard"/>: the landing clip plays (else the legs just fade out). The heavy bolter's early landing is <see cref="HeavyStep"/>'s.</summary>
    public void BeginLanding(double now, bool hard)
    {
        LandAt = now;
        LandHard = hard;
    }

    /// <summary>Marks the heavy landing as done at the touchdown when it had not begun above the ground (a drop that never reached the entry height).</summary>
    public void MarkHeavyDone() => HeavyLanded = true;

    /// <summary>Whether the landing clip is still showing <paramref name="now"/> (hard landing, before its exit and blend have run out).</summary>
    public bool LandingActive(double now, FallExit exit) => LandHard && !FallRules.LandOver((float)(now - LandAt), exit);

    /// <summary>Ends the landing clip (its window is over).</summary>
    public void EndLanding() => LandHard = false;
}
