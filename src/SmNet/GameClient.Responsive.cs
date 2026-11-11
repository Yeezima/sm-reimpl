using SmSim;

namespace SmNet;

/// <summary>
/// net/responsive (ours; client only, no wire change; the owner: Classic PvP "feels delayed, not responsive, slow. its smooth, but we're missing something").
/// Each part has its switch back to the behaviour before it (<see cref="ClientOptions"/>; the Godot client's flags in docs/commands.md):
/// <list type="bullet">
/// <item><b>Input lead, precise</b> (protocol 65, <see cref="ClientOptions.PreciseLead"/>; the owner: "buffering as low as it can go while still playing well"): the
/// server reports the input slack (ms an input waited before its tick ran); the client aims it at <see cref="ClientOptions.LeadLossTicks"/> tick (one lost packet
/// survived) plus the jitter guard's ticks plus the measured jitter (<see cref="SlackJitter"/>) plus <see cref="ClientOptions.LeadMarginMs"/>, by a proportional slew
/// (<see cref="SlackGainMs"/>, at most <see cref="ClientOptions.LeadSlew"/>); jumps and the guard as below. With it off, the integer steering:</item>
/// <item><b>Input lead</b> (<see cref="ClientOptions.SmoothInputLead"/>): the lead the server reports is filtered (<see cref="LeadFilterSeconds"/>) and kept at
/// <see cref="ClientOptions.TargetInputLead"/> by a clock slew: the client's ticks run <see cref="ClientOptions.LeadSlew"/> slower while the filtered lead is
/// <see cref="ClientOptions.LeadBand"/> or more above the target, as much faster while it is that far below, until it is back within a quarter tick. Whole-tick
/// jumps stay for what a slew is too slow for (the stall and overshoot protections): a report below 0 (an input came late; the server repeated one) runs ticks at once
/// up to a lead of 1 (at most <see cref="MaxLeadJump"/>), a filtered lead at or above target + 3 pauses toward the target (at most <see cref="MaxLeadPause"/> ticks a time), then waits for their feedback for the round trip measured from
/// snapshot echoes (<see cref="EchoRoundTripMs"/>) rather than the handshake's, which a map load can inflate. Before: one whole tick per handshake round trip,
/// only below target − 1 or above target + 3, so the lead sat at 3 to 5. A jitter guard (<see cref="JitterGuard"/>) raises the target by a tick after a report of
/// 0 or less (an input that arrived just in time or late; not within a feedback wait of this client's own stall, <see cref="StallGap"/>), up to two, and lowers it a tick for every <see cref="JitterGuardSeconds"/> without one: a jittery link keeps
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

    double _leadFiltered = double.NaN, _slackFiltered = double.NaN;
    TimeSpan _leadSampledAt;
    /// <summary>The slew in force, as a fraction of the tick: positive the ticks run slower (the lead is too high), negative faster, 0 none.</summary>
    double _tickStretch;
    readonly Queue<(TimeSpan At, int Slack)> _slackWindow = new();

    /// <summary>The filtered input lead (ticks; NaN before the first report or right after a jump).</summary>
    public double FilteredInputLead => _leadFiltered;
    /// <summary>The filtered input slack (ms; protocol 65; NaN before the first report or right after a jump).</summary>
    public double FilteredInputSlack => _slackFiltered;
    /// <summary>The slack the steering aims for now (ms): <see cref="ClientOptions.LeadLossTicks"/> and the jitter guard's ticks, the measured jitter, the margin.</summary>
    public double TargetInputSlack { get; private set; } = double.NaN;
    /// <summary>The measured jitter of the input slack (ms): its median less its 2nd percentile over <see cref="SlackWindowSeconds"/>.</summary>
    public double SlackJitter { get; private set; }
    /// <summary>The slew in force: +1 slower ticks, -1 faster, 0 none (diagnostics, tests).</summary>
    public int LeadSlew => Math.Sign(_tickStretch);
    /// <summary>Whole-tick lead jumps so far (run or paused ticks; diagnostics, tests).</summary>
    public int LeadJumps { get; private set; }
    /// <summary>Snapshots whose tick the server ran without this client's input (it repeated the last one: an input came late or every copy was lost).</summary>
    public int MissedInputs { get; private set; }
    uint _firstInputTick;
    /// <summary>Ticks the jitter guard adds to the target lead now (0 to <see cref="JitterGuardTicks"/>).</summary>
    public int JitterGuard { get; private set; }
    /// <summary>The most ticks the jitter guard adds (ours).</summary>
    public const int JitterGuardTicks = 2;
    /// <summary>Seconds without a late input before the jitter guard lowers the target by a tick (ours).</summary>
    public const double JitterGuardSeconds = 10;
    /// <summary>The window the snapshot loss is measured over (seconds; ours).</summary>
    public const double LossWindowSeconds = 3.0;
    /// <summary>The precise steering keeps enough ticks that this many batches lost in a row is rarer than this per tick (ours: 3 in 10,000: 1 % loss keeps 2 ticks, 5 % keeps 3).</summary>
    public const double LossRunRisk = 3e-4;
    /// <summary>The most ticks the loss term asks for (ours).</summary>
    public const int MaxLossTicks = 4;
    readonly Queue<(TimeSpan At, uint Tick)> _lossWindow = new();
    /// <summary>The measured snapshot loss (0 to 1) over <see cref="LossWindowSeconds"/>; 0 unless the server sends a snapshot every tick (the uplink is taken to lose as much).</summary>
    public double MeasuredLoss { get; private set; }
    /// <summary>Ticks the precise steering keeps for lost batches now: the smallest count whose run of losses is rarer than <see cref="LossRunRisk"/>, at least <see cref="ClientOptions.LeadLossTicks"/>.</summary>
    public int LossTicks { get; private set; } = 1;

    /// <summary>From <see cref="OnSnapshot"/>: the snapshot loss estimate (net/responsive).</summary>
    void NoteSnapshotLoss(uint tick, TimeSpan now)
    {
        _lossWindow.Enqueue((now, tick));
        while (_lossWindow.Count > 2 && now - _lossWindow.Peek().At > TimeSpan.FromSeconds(LossWindowSeconds)) _lossWindow.Dequeue();
        double loss = 0;
        if (_lossWindow.Count >= 20)
        {
            var ticks = _lossWindow.Select(x => x.Tick).ToArray();
            var gaps = ticks.Zip(ticks.Skip(1), (a, b) => (long)b - a).Where(g => g > 0).OrderBy(g => g).ToArray();
            long span = (long)ticks.Max() - ticks.Min() + 1;
            if (gaps.Length > 0 && gaps[gaps.Length / 2] == 1 && span > 0) loss = Math.Clamp(1.0 - (double)ticks.Distinct().Count() / span, 0, 0.9);   // only a snapshot every tick tells its losses
        }
        MeasuredLoss = loss;
        int n = 1;
        while (n < MaxLossTicks && Math.Pow(loss, n) >= LossRunRisk) n++;
        LossTicks = Math.Max(n, (int)Math.Ceiling(_options.LeadLossTicks));
    }

    /// <summary>The window the slack's jitter is measured over (seconds; ours).</summary>
    public const double SlackWindowSeconds = 2.0;
    /// <summary>ms of slack error per 1 % of slew in the precise steering (ours: 4 % at 40 ms off and above; at most <see cref="ClientOptions.LeadSlew"/>).</summary>
    public const double SlackGainMs = 10.0;
    /// <summary>The precise steering leaves the slack alone within this many ms of its target (ours).</summary>
    public const double SlackDeadbandMs = 2.0;
    TimeSpan _jitterRaisedAt = TimeSpan.MinValue / 2, _jitterSeenAt = TimeSpan.MinValue / 2;
    /// <summary>An update gap longer than this is this client's own stall (ours: 3 ticks).</summary>
    public static readonly TimeSpan StallGap = TimeSpan.FromSeconds(0.1);
    TimeSpan _lastUpdateAt = TimeSpan.MinValue, _stallQuietUntil = TimeSpan.MinValue;

    /// <summary>From <see cref="Update"/>: notes a stall of this client (the jitter guard ignores the reports it causes).</summary>
    void NoteUpdateGap(TimeSpan now)
    {
        if (_lastUpdateAt != TimeSpan.MinValue && now - _lastUpdateAt > StallGap) _stallQuietUntil = now + FeedbackWait();
        _lastUpdateAt = now;
    }

    /// <summary>The length of the client's tick now (seconds): the simulation's, stretched or shortened by the lead slew.</summary>
    public double CurrentTickSeconds => _config.TickSeconds * (1.0 + _tickStretch);

    /// <summary>The length the newest tick was scheduled with (seconds): the drawn fraction runs over it, so a slew that starts or ends moves nothing drawn.</summary>
    double ScheduledTickSeconds => _scheduledTick > TimeSpan.Zero ? _scheduledTick.TotalSeconds : _config.TickSeconds;
    TimeSpan _scheduledTick;

    /// <summary>From <see cref="OnSnapshot"/> (the own full record): steers the lead (see the class summary).</summary>
    void SteerLead(Snapshot s, TimeSpan now)
    {
        if (s.InputLead == Protocol.NoLead || now < _leadHoldUntil) return;
        var tickLength = TimeSpan.FromSeconds(_config.TickSeconds);
        double tickMs = _config.TickSeconds * 1000.0;
        int target = _options.TargetInputLead;
        if (!_options.SmoothInputLead)
        {
            // Before net/responsive: one extra tick now if inputs arrive late, one paused if far too early; a change shows in the feedback one round trip later.
            if (s.InputLead < target - 1) { _nextTickAt -= tickLength; _leadHoldUntil = now + HandshakeRoundTrip; }
            else if (s.InputLead > target + 3) { _nextTickAt += tickLength; _leadHoldUntil = now + HandshakeRoundTrip; }
            return;
        }
        int lead = s.InputLead;
        bool precise = _options.PreciseLead && s.InputSlack != Protocol.NoSlack;
        // The jitter guard: a snapshot whose tick ran without this client's input (its LastInputTick is older, or a negative slack: the server repeated the last
        // one) or, in the integer steering, a lead report at or below 0, raises the target by a tick, at most one a second and JitterGuardTicks in all; each
        // JitterGuardSeconds without one lowers it again. A report soon after this client's own stall (an update gap) says nothing about the link and raises nothing.
        bool missed = s.LastInputTick != 0 && s.LastInputTick < s.ServerTick && _history.Count > 0 && s.ServerTick > _firstInputTick + 2;
        if (precise && s.InputSlack < 0 && s.ServerTick > _firstInputTick + 2) missed = true;
        if (missed) MissedInputs++;
        if ((missed || (!precise && lead <= 0)) && now >= _stallQuietUntil)
        {
            if (JitterGuard < JitterGuardTicks && now - _jitterRaisedAt >= TimeSpan.FromSeconds(1)) { JitterGuard++; _jitterRaisedAt = now; }
            _jitterSeenAt = now;
        }
        else if (JitterGuard > 0 && now - _jitterSeenAt >= TimeSpan.FromSeconds(JitterGuardSeconds)) { JitterGuard--; _jitterSeenAt = now; }
        double dt = (now - _leadSampledAt).TotalSeconds;
        _leadSampledAt = now;
        double alpha = 1 - Math.Exp(-Math.Max(0, dt) / LeadFilterSeconds);
        _leadFiltered = double.IsNaN(_leadFiltered) ? lead : _leadFiltered + (lead - _leadFiltered) * alpha;
        if (precise)
        {
            // Protocol 65: the slack (ms an input waited on the server) is aimed at a tick for each loss to survive (LeadLossTicks plus the guard's) plus its
            // measured jitter plus a margin; a proportional slew (1 % for every SlackGainMs off, at most LeadSlew) brings it there.
            _slackWindow.Enqueue((now, s.InputSlack));
            while (_slackWindow.Count > 2 && now - _slackWindow.Peek().At > TimeSpan.FromSeconds(SlackWindowSeconds)) _slackWindow.Dequeue();
            var window = _slackWindow.Where(x => x.Slack >= 0).Select(x => (double)x.Slack).ToList();
            SlackJitter = window.Count >= 8 ? Math.Max(0, NetLatencyReadout.Quantile(window, 0.5) - NetLatencyReadout.Quantile(window, 0.02)) : tickMs / 2;
            TargetInputSlack = tickMs * (Math.Max(_options.LeadLossTicks, LossTicks) + JitterGuard) + SlackJitter + _options.LeadMarginMs;
            if (s.InputSlack >= 0) _slackFiltered = double.IsNaN(_slackFiltered) ? s.InputSlack : _slackFiltered + (s.InputSlack - _slackFiltered) * alpha;
            // After this client's own stall its tick loop runs the missed ticks itself: a late report then is no reason to run more (they would pile up as lead).
            // Far short of the target (a loss term or the guard just rose by two ticks or more): run the missing ticks at once rather than slew them for seconds.
            bool lateNow = lead < 0 && now >= _stallQuietUntil, earlyNow = _slackFiltered >= TargetInputSlack + 3 * tickMs,
                shortNow = !lateNow && _slackFiltered <= TargetInputSlack - 2 * tickMs && now >= _stallQuietUntil;
            if (lateNow || earlyNow || shortNow)
            {
                int jump = Math.Clamp(lateNow ? 1 - lead : -(int)Math.Round((_slackFiltered - TargetInputSlack) / tickMs), -MaxLeadPause, MaxLeadJump);
                _nextTickAt -= tickLength * jump;
                _tickStretch = 0;
                _slackFiltered = lateNow ? double.NaN : _slackFiltered + jump * tickMs;
                _leadFiltered = (lateNow ? lead : _leadFiltered) + jump;
                LeadJumps++;
                _leadHoldUntil = now + FeedbackWait();
                return;
            }
            double off = _slackFiltered - TargetInputSlack;
            _tickStretch = double.IsNaN(off) || Math.Abs(off) <= SlackDeadbandMs ? 0
                : Math.Clamp((off - Math.Sign(off) * SlackDeadbandMs) / SlackGainMs * 0.01, -_options.LeadSlew, _options.LeadSlew);
            return;
        }
        target += JitterGuard;
        // Late (a report below 0: the server already repeated an input): run ticks at once up to a lead of 1, the slew does the rest. Far too early (the filtered
        // lead, so one jittery report does not pause the marine): pause down to the target.
        bool late = lead < 0 && now >= _stallQuietUntil, early = _leadFiltered >= target + 3;   // no catch-up after this client's own stall (its tick loop catches up)
        if (late || early)
        {
            int jump = Math.Clamp(late ? 1 - lead : target - (int)Math.Round(_leadFiltered), -MaxLeadPause, MaxLeadJump);
            _nextTickAt -= tickLength * jump;   // positive: run the missing ticks at once; negative: pause
            _tickStretch = 0;
            _leadFiltered = (late ? lead : _leadFiltered) + jump;   // what the reports should read once the jump shows
            LeadJumps++;
            _leadHoldUntil = now + FeedbackWait();
            return;
        }
        double error = _leadFiltered - target;
        if (error >= _options.LeadBand) _tickStretch = _options.LeadSlew;
        else if (error <= -_options.LeadBand) _tickStretch = -_options.LeadSlew;
        else if ((_tickStretch > 0 && error <= 0.25) || (_tickStretch < 0 && error >= -0.25)) _tickStretch = 0;
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
        // Presentation only: the charge contact this step may predict is not kept (the real prediction of the tick decides it; review of the Rush tests: a kept
        // speculative contact took the victim's body out of the next real step).
        var (contact, clash, checks, noView, predicted, bumps) = (LastChargeContact, LastChargeClash, ChargeContactChecks, ChargeContactNoView, ChargeContactsPredicted, ChargeBumpsPredicted);
        PlayerState next;
        using (PlayerCollision.Use(ClashBodies(Predicted, input, bodies)))
            next = Combat.StepSelf(Predicted, Match.Predicted(_latestSnapshotTick, input), _config, out _, out _, Collision,
                input.MeleeTarget > 0 ? LockViewAt(input.ViewTick, Predicted) : null);
        next = PredictChargeContact(Predicted, next, input, null, bodies);
        (LastChargeContact, LastChargeClash, ChargeContactChecks, ChargeContactNoView, ChargeContactsPredicted, ChargeBumpsPredicted) = (contact, clash, checks, noView, predicted, bumps);
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
