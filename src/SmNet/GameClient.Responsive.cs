using SmSim;

namespace SmNet;

/// <summary>
/// net/responsive (ours; client only, no wire change; the owner: Classic PvP "feels delayed, not responsive, slow. its smooth, but we're missing something").
/// Each part has its switch back to the behaviour before it (<see cref="ClientOptions"/>; the Godot client's flags in docs/commands.md):
/// <list type="bullet">
/// <item><b>Input lead</b> (<see cref="ClientOptions.SmoothInputLead"/>): the lead the server reports is filtered (<see cref="LeadFilterSeconds"/>) and kept at
/// <see cref="ClientOptions.TargetInputLead"/> by a clock slew: the client's ticks run <see cref="ClientOptions.LeadSlew"/> slower while the filtered lead is
/// <see cref="ClientOptions.LeadBand"/> or more above the target, as much faster while it is that far below, until it is back within a quarter tick. Whole-tick
/// jumps stay for what a slew is too slow for (the stall and overshoot protections): a report below 0 (an input came late; the server repeated one) runs ticks at once
/// up to a lead of 1 (at most <see cref="MaxLeadJump"/>), a filtered lead at or above target + 3 pauses toward the target (at most <see cref="MaxLeadPause"/> ticks a time), then waits for their feedback for the round trip measured from
/// snapshot echoes (<see cref="EchoRoundTripMs"/>) rather than the handshake's, which a map load can inflate. Before: one whole tick per handshake round trip,
/// only below target − 1 or above target + 3, so the lead sat at 3 to 5. A jitter guard (<see cref="JitterGuard"/>) raises the target by a tick after a report of
/// 0 or less (an input that arrived just in time or late), up to two, and lowers it a tick for every <see cref="JitterGuardSeconds"/> without one: a jittery link keeps
/// the margin it needs, a clean one stays at the target.</item>
/// <item><b>Own marine ahead</b> (<see cref="ClientOptions.DrawOwnAhead"/>): <see cref="DrawPair"/> draws the newest predicted tick carried forward through the
/// current tick toward a speculative next tick (<see cref="Ahead"/>: the last input stepped once more, as the server repeats an input it lacks), instead of between
/// the previous tick and the newest. The drawn position and camera show an input a tick sooner; after an early tick the pair before it keeps the timeline, as before.
/// The step a tick makes in that drawing (the new input against the speculative one, a lead catch-up) is blended out over <see cref="DrawBlendTicks"/> ticks
/// (<see cref="OwnDrawnPosition"/>).</item>
/// <item><b>Adaptive interpolation</b> (<see cref="ClientOptions.AdaptiveInterpolation"/>): the remote players' delay follows the snapshots
/// (<see cref="InterpolationDelayEstimator"/>), within [<see cref="InterpolationFloor"/>, <see cref="ClientOptions.MaxInterpolationDelay"/>]; before: a fixed 100 ms.</item>
/// </list>
/// </summary>
public sealed partial class GameClient
{
    /// <summary>Seconds of the filter on the reported lead (exponential time constant; ours).</summary>
    public const double LeadFilterSeconds = 0.25;
    /// <summary>The most ticks one whole-tick lead jump runs or pauses (a pause of 10 ticks is 333 ms, under the server's 500 ms neutral-input wait).</summary>
    public const int MaxLeadJump = 10;
    /// <summary>The most ticks one pause takes out (ours): a pause stands the drawn marine still, so a large excess goes two ticks a feedback wait, with the slew between.</summary>
    public const int MaxLeadPause = 2;

    double _leadFiltered = double.NaN;
    TimeSpan _leadSampledAt;
    /// <summary>The slew in force: +1 the ticks run slower (the lead is too high), -1 faster, 0 none.</summary>
    int _leadSlew;

    /// <summary>The filtered input lead (ticks; NaN before the first report or right after a jump).</summary>
    public double FilteredInputLead => _leadFiltered;
    /// <summary>The slew in force: +1 slower ticks, -1 faster, 0 none (diagnostics, tests).</summary>
    public int LeadSlew => _leadSlew;
    /// <summary>Whole-tick lead jumps so far (run or paused ticks; diagnostics, tests).</summary>
    public int LeadJumps { get; private set; }
    /// <summary>Snapshots whose tick the server ran without this client's input (it repeated the last one: an input came late or every copy was lost).</summary>
    public int MissedInputs { get; private set; }
    uint _firstInputTick;
    /// <summary>Ticks the jitter guard adds to the target lead now (0 to <see cref="JitterGuardTicks"/>).</summary>
    public int JitterGuard { get; private set; }
    /// <summary>The most ticks the jitter guard adds (ours).</summary>
    public const int JitterGuardTicks = 2;
    /// <summary>Seconds without a report at or below 0 before the jitter guard lowers the target by a tick (ours).</summary>
    public const double JitterGuardSeconds = 10;
    TimeSpan _jitterRaisedAt = TimeSpan.MinValue / 2, _jitterSeenAt = TimeSpan.MinValue / 2;

    /// <summary>The length of the client's tick now (seconds): the simulation's, stretched or shortened by the lead slew.</summary>
    public double CurrentTickSeconds => _config.TickSeconds * (1.0 + _leadSlew * _options.LeadSlew);

    /// <summary>The length the newest tick was scheduled with (seconds): the drawn fraction runs over it, so a slew that starts or ends moves nothing drawn.</summary>
    double ScheduledTickSeconds => _scheduledTick > TimeSpan.Zero ? _scheduledTick.TotalSeconds : _config.TickSeconds;
    TimeSpan _scheduledTick;

    /// <summary>From <see cref="OnSnapshot"/> (the own full record): steers the lead (see the class summary).</summary>
    void SteerLead(Snapshot s, TimeSpan now)
    {
        if (s.InputLead == Protocol.NoLead || now < _leadHoldUntil) return;
        var tickLength = TimeSpan.FromSeconds(_config.TickSeconds);
        int target = _options.TargetInputLead;
        if (!_options.SmoothInputLead)
        {
            // Before net/responsive: one extra tick now if inputs arrive late, one paused if far too early; a change shows in the feedback one round trip later.
            if (s.InputLead < target - 1) { _nextTickAt -= tickLength; _leadHoldUntil = now + HandshakeRoundTrip; }
            else if (s.InputLead > target + 3) { _nextTickAt += tickLength; _leadHoldUntil = now + HandshakeRoundTrip; }
            return;
        }
        int lead = s.InputLead;
        // The jitter guard: a report at or below 0 (the next input had not arrived when the server ran its tick), or a snapshot whose tick ran without this
        // client's input (its LastInputTick is older: the server repeated the last one), raises the target by a tick, at most one a second and JitterGuardTicks
        // in all; each JitterGuardSeconds without either lowers it again by a tick.
        bool missed = s.LastInputTick != 0 && s.LastInputTick < s.ServerTick && _history.Count > 0 && s.ServerTick > _firstInputTick + 2;
        if (missed) MissedInputs++;
        if (lead <= 0 || missed)
        {
            if (JitterGuard < JitterGuardTicks && now - _jitterRaisedAt >= TimeSpan.FromSeconds(1)) { JitterGuard++; _jitterRaisedAt = now; }
            _jitterSeenAt = now;
        }
        else if (JitterGuard > 0 && now - _jitterSeenAt >= TimeSpan.FromSeconds(JitterGuardSeconds)) { JitterGuard--; _jitterSeenAt = now; }
        target += JitterGuard;
        double dt = (now - _leadSampledAt).TotalSeconds;
        _leadSampledAt = now;
        _leadFiltered = double.IsNaN(_leadFiltered) ? lead : _leadFiltered + (lead - _leadFiltered) * (1 - Math.Exp(-Math.Max(0, dt) / LeadFilterSeconds));
        // Late (a report below 0: the server already repeated an input): run ticks at once up to a lead of 1, the slew does the rest. Far too early (the filtered
        // lead, so one jittery report does not pause the marine): pause down to the target.
        bool late = lead < 0, early = _leadFiltered >= target + 3;
        if (late || early)
        {
            int jump = Math.Clamp(late ? 1 - lead : target - (int)Math.Round(_leadFiltered), -MaxLeadPause, MaxLeadJump);
            _nextTickAt -= tickLength * jump;   // positive: run the missing ticks at once; negative: pause
            _leadSlew = 0;
            _leadFiltered = (late ? lead : _leadFiltered) + jump;   // what the reports should read once the jump shows
            LeadJumps++;
            _leadHoldUntil = now + FeedbackWait();
            return;
        }
        double error = _leadFiltered - target;
        if (error >= _options.LeadBand) _leadSlew = 1;
        else if (error <= -_options.LeadBand) _leadSlew = -1;
        else if ((_leadSlew > 0 && error <= 0.25) || (_leadSlew < 0 && error >= -0.25)) _leadSlew = 0;
    }

    /// <summary>How long a lead jump takes to show in the reports: the echo round trip (or the handshake's, at most 0.5 s, before one is measured), the filter, two ticks.</summary>
    TimeSpan FeedbackWait()
    {
        double rtt = _echoRtt.Ms >= 0 ? _echoRtt.Ms / 1000.0 : Math.Min(HandshakeRoundTrip.TotalSeconds, 0.5);
        return TimeSpan.FromSeconds(rtt + LeadFilterSeconds + 2 * _config.TickSeconds);
    }

    // ---- the own marine one tick ahead ----
    PlayerState _ahead;
    (uint Tick, int Serial) _aheadKey = (uint.MaxValue, -1);

    /// <summary>
    /// The speculative state one tick after <see cref="Predicted"/>: the newest input stepped once more (its buttons held, so no new press; the server repeats an input it
    /// lacks the same way), with the same collision, bodies and charge contact as the prediction; presentation only (<see cref="DrawPair"/>), never sent or replayed.
    /// <see cref="Predicted"/> itself while dead, before the first input, or for a spectator. Computed once per tick and correction.
    /// </summary>
    public PlayerState Ahead()
    {
        if (_aheadKey == (Tick, CorrectionSerial)) return _ahead;
        _aheadKey = (Tick, CorrectionSerial);
        if (_options.Observer || !Predicted.Alive || !_history.TryGetValue(Tick, out var last)) return _ahead = Predicted;
        var input = last.Input with { Tick = Tick + 1 };
        var bodies = BodiesFor(input);
        PlayerState next;
        using (PlayerCollision.Use(ClashBodies(Predicted, input, bodies)))
            next = Combat.StepSelf(Predicted, Match.Predicted(_latestSnapshotTick, input), _config, out _, out _, Collision,
                input.MeleeTarget > 0 ? LockViewAt(input.ViewTick, Predicted) : null);
        next = PredictChargeContact(Predicted, next, input, null, bodies);
        return _ahead = next;
    }

    /// <summary>
    /// The clip lead in ticks (net/responsive): 1 while the own marine is drawn a tick ahead (<see cref="ClientOptions.DrawOwnAhead"/>, not after an early tick before its
    /// scheduled time), else 0. A clip's drawn time is its predicted time − (1 − <see cref="ClipFraction"/> − lead) ticks (<see cref="OwnClipClock"/>), so it stays with the position.
    /// </summary>
    public float ClipLead => _options.DrawOwnAhead && !EarlyTick.DrawsBehind(_earlyRanAt is not null, _clock.Now.TotalSeconds, _nextTickAt.TotalSeconds, ScheduledTickSeconds) ? 1f : 0f;

    // ---- the drawn own position: continuous across ticks ----
    /// <summary>Ticks over which a step of the drawn own position at a tick is blended out (ours; ahead drawing only).</summary>
    public const double DrawBlendTicks = 3;
    System.Numerics.Vector3 _drawBlend;
    TimeSpan _drawBlendAt;

    /// <summary>
    /// The own marine's drawn position now (game space; no reconciliation fade, which the renderer adds): <see cref="DrawPair"/> interpolated, plus, while drawn ahead, what
    /// is left of the last tick's step (<see cref="DrawBlendOffset"/>).
    /// </summary>
    public System.Numerics.Vector3 OwnDrawnPosition()
    {
        var (from, to, f) = DrawPair();
        return ViewSmoothing.Interpolate(from.Position, to.Position, f) + DrawBlendOffset();
    }

    /// <summary>
    /// While drawn ahead: the step a tick made in the drawn position (a new input's first tick toward the speculative one, a lead catch-up's ticks run at once), fading
    /// linearly to nothing over <see cref="DrawBlendTicks"/> ticks, so the drawn marine turns onto its new path at once but never jumps. Zero in the old draw.
    /// </summary>
    public System.Numerics.Vector3 DrawBlendOffset()
    {
        if (_drawBlend == default) return default;
        double left = 1.0 - (_clock.Now - _drawBlendAt).TotalSeconds / (DrawBlendTicks * _config.TickSeconds);
        if (left <= 0) { _drawBlend = default; return default; }
        return _drawBlend * (float)left;
    }

    /// <summary>Before this update's ticks: where the drawn position is on the current pair's own course now (its fraction not clamped at 1); null when not drawn ahead.</summary>
    System.Numerics.Vector3? DrawnOnCourse(TimeSpan now)
    {
        if (!_options.DrawOwnAhead || !Predicted.Alive) return null;
        double tick = ScheduledTickSeconds, next = _nextTickAt.TotalSeconds;
        bool behind = EarlyTick.DrawsBehind(_earlyRanAt is not null, now.TotalSeconds, next, tick);
        var (from, to, _) = DrawPair();
        if (!from.Alive || from.PlayerId != to.PlayerId || System.Numerics.Vector3.Distance(from.Position, to.Position) > ViewSmoothing.TeleportDistance) return null;
        double f = Math.Clamp(1.0 - ((behind ? next - tick : next) - now.TotalSeconds) / tick, 0.0, 3.0);
        return from.Position + (to.Position - from.Position) * (float)f + DrawBlendOffset();
    }

    /// <summary>
    /// After this update's ticks or lead jump: the step between the course before the update and the drawn position now becomes the blend (none past a teleport). A
    /// reconciliation in the same update is left out of it (<see cref="LastCorrection"/>: the renderer fades that one itself).
    /// </summary>
    void BlendDrawnStep(System.Numerics.Vector3? before, TimeSpan now, int correctionBefore)
    {
        if (before is not { } b) return;
        var (from, to, _) = DrawPair();
        if (!from.Alive || !to.Alive) { _drawBlend = default; return; }
        var step = b - ViewSmoothing.Interpolate(from.Position, to.Position, DrawFraction());
        if (CorrectionSerial != correctionBefore) step += LastCorrection;
        if (!float.IsFinite(step.X + step.Y + step.Z) || step.Length() > ViewSmoothing.TeleportDistance) { _drawBlend = default; return; }
        _drawBlend = step; _drawBlendAt = now;
    }

    // ---- adaptive interpolation ----
    readonly InterpolationDelayEstimator _interpolation = new();

    /// <summary>
    /// The lowest interpolation delay the adaptive estimate may use (null: <see cref="ClientOptions.MinInterpolationDelay"/>). Added content that draws more than the players
    /// at the render tick may raise it (Musou Mode keeps the old 100 ms for its crowd: <c>client/Extras/Musou</c>).
    /// </summary>
    public TimeSpan? InterpolationFloor { get; set; }

    /// <summary>From <see cref="OnSnapshot"/>: a snapshot was taken; the adaptive delay notes its arrival.</summary>
    void NoteInterpolation(uint serverTick, TimeSpan now)
    {
        if (!_options.AdaptiveInterpolation) return;
        double floor = (InterpolationFloor ?? _options.MinInterpolationDelay).TotalSeconds;
        _interpolation.Note(serverTick, now.TotalSeconds, _config.TickSeconds, floor, _options.MaxInterpolationDelay.TotalSeconds, _options.InterpolationDelay.TotalSeconds);
    }
}
