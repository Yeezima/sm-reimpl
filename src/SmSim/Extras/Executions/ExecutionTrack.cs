using System.Numerics;

namespace SmSim.Extras.Executions;

/// <summary>
/// Executions X3: where an executor stands at any tick of a run, as a pure function of the run and its row (no engine types). The server pin (<c>World.ExecutionsPin</c>), the own
/// prediction pin (X6, <c>GameClient.ExtrasPin</c>) and the drawn pose (X5) all call <see cref="Pose"/>, so they cannot drift apart.
/// <list type="bullet">
/// <item>The slide first: for <c>tick - StartTick</c> in <c>[0, SlideTicks)</c> the feet move linearly by tick from <see cref="ExecutionRun.SlideFrom"/> to <see cref="ExecutionRun.Origin"/>,
/// at the pair's yaw (<b>ours</b>: the alignment's capsule glide ends within 0.02 m of the origin, so the straight line is its path to the probe's precision).</item>
/// <item>Then the clip: clip time 0 at <c>StartTick + SlideTicks</c>; the attacker's root path blended at <see cref="ExecutionRun.Distance"/> (<see cref="ExecutionRow.AttackerRootAt"/>, never a
/// variant's own distance) at <c>ClipSeconds / AttackerSeconds</c>, placed from the origin at the yaw (<see cref="ExecutionAlign.RootPath"/>'s placement; the root's W adds to the yaw).</item>
/// <item>Event and end ticks: clip time 0 plus the event's seconds, rounded to the nearest tick (<see cref="EventTick(in ExecutionRun, float, int)"/>, <see cref="EndTick"/>).</item>
/// </list>
/// The wire precision (<see cref="Quantize"/>) is defined here too, so the Start's encoder and the server's own run agree to the bit.
/// </summary>
public static class ExecutionTrack
{
    /// <summary>The executor's feet and yaw at <paramref name="tick"/> (before the start: the slide's start; after the end: the clip's last root sample).</summary>
    public static (Vector3 Feet, float Yaw, bool InSlide, float ClipSeconds) Pose(in ExecutionRun run, ExecutionRow row, uint tick, int tickRate)
    {
        float rate = Math.Max(1, tickRate);
        long since = (long)tick - run.StartTick;
        if (since < run.SlideTicks)
        {
            float f = run.SlideTicks <= 0 || since <= 0 ? 0f : since / (float)run.SlideTicks;
            return (Vector3.Lerp(run.SlideFrom, run.Origin, f), run.Yaw, true, 0f);
        }
        float clip = (since - run.SlideTicks) / rate;
        var root = row.AttackerRootAt(run.Distance);
        float length = row.AttackerSeconds;
        var s = ExecutionRow.RootAt(root, length > 0f ? clip / length : 1f);
        var w = ExecutionAlign.ToWorld(new Vector2(s.X, s.Y), run.Yaw);
        return (new Vector3(run.Origin.X + w.X, run.Origin.Y + w.Y, run.Origin.Z + s.Z), run.Yaw + s.W, false, clip);
    }

    /// <summary>The tick clip time 0 falls on: <c>StartTick + SlideTicks</c>.</summary>
    public static uint ClipStartTick(in ExecutionRun run) => run.StartTick + (uint)Math.Max(0, run.SlideTicks);

    /// <summary>The tick at <paramref name="seconds"/> of clip time (rounded to the nearest tick; negative seconds count as 0).</summary>
    public static uint EventTick(in ExecutionRun run, float seconds, int tickRate) =>
        ClipStartTick(run) + (uint)Math.Max(0, (int)MathF.Round((float.IsFinite(seconds) ? MathF.Max(0f, seconds) : 0f) * Math.Max(1, tickRate)));

    /// <summary>
    /// The tick of <paramref name="row"/>'s first event of <paramref name="kind"/> (<see cref="ExecutionRow.At"/>); <see cref="ExecutionEventKind.Kill"/> falls back as
    /// <see cref="ExecutionRow.KillSeconds"/> and <see cref="ExecutionEventKind.End"/> as <see cref="ExecutionRow.Seconds"/>; another kind the row lacks gives <see cref="EndTick"/>.
    /// </summary>
    public static uint EventTick(in ExecutionRun run, ExecutionRow row, ExecutionEventKind kind, int tickRate) => kind switch
    {
        ExecutionEventKind.Kill => EventTick(run, row.KillSeconds, tickRate),
        ExecutionEventKind.End => EndTick(run, row, tickRate),
        _ => row.At(kind) is { } s ? EventTick(run, s, tickRate) : EndTick(run, row, tickRate),
    };

    /// <summary>The tick the move ends: its end event, else the longer clip (<see cref="ExecutionRow.Seconds"/>), from clip time 0; never before the kill tick.</summary>
    public static uint EndTick(in ExecutionRun run, ExecutionRow row, int tickRate) =>
        Math.Max(EventTick(run, row.Seconds, tickRate), EventTick(run, row.KillSeconds, tickRate));

    // ---- the wire's precision (ExecutionWire's Start, tag 50) ----

    /// <summary>Largest distance the Start carries (millimetres; a u16, and the decoder refuses 0 and anything above).</summary>
    public const int MaxDistanceMm = 20000;
    /// <summary>Largest slide offset per axis the Start carries (millimetres; the alignment's slide is at most 2.5 m back, 1 m forward).</summary>
    public const int MaxSlideMm = 4000;

    /// <summary>A yaw as 1/65536 turns (wrapped into [0, 2 pi)).</summary>
    public static ushort YawToWire(float yaw)
    {
        if (!float.IsFinite(yaw)) return 0;
        double turns = yaw / (2 * Math.PI);
        turns -= Math.Floor(turns);
        return (ushort)((int)Math.Round(turns * 65536.0) & 0xFFFF);
    }

    /// <summary>The yaw a wire value stands for (radians in [0, 2 pi)).</summary>
    public static float YawFromWire(ushort value) => (float)(value / 65536.0 * 2 * Math.PI);

    /// <summary>A distance in millimetres for the wire (1..<see cref="MaxDistanceMm"/>).</summary>
    public static ushort DistanceToWire(float metres) => (ushort)Math.Clamp((int)MathF.Round((float.IsFinite(metres) ? metres : 0f) * 1000f), 1, MaxDistanceMm);

    public static float DistanceFromWire(ushort mm) => mm / 1000f;

    /// <summary>One axis of the slide offset (<c>SlideFrom - Origin</c>) in millimetres for the wire (clamped to ±<see cref="MaxSlideMm"/>).</summary>
    public static short SlideToWire(float metres) => (short)Math.Clamp((int)MathF.Round((float.IsFinite(metres) ? metres : 0f) * 1000f), -MaxSlideMm, MaxSlideMm);

    public static float SlideFromWire(short mm) => mm / 1000f;

    /// <summary>
    /// <paramref name="run"/> at the wire's precision: the yaw to 1/65536 turn, the distance to the millimetre, the slide's start to the millimetre from the origin (the origin itself is
    /// f32 on the wire, exact). The server starts every run through this, so a client's run decoded from the Start equals the server's.
    /// </summary>
    public static ExecutionRun Quantize(in ExecutionRun run)
    {
        var d = run.SlideFrom - run.Origin;
        var slideFrom = run.Origin + new Vector3(SlideFromWire(SlideToWire(d.X)), SlideFromWire(SlideToWire(d.Y)), SlideFromWire(SlideToWire(d.Z)));
        return run with
        {
            Yaw = YawFromWire(YawToWire(run.Yaw)),
            Distance = DistanceFromWire(DistanceToWire(run.Distance)),
            SlideFrom = slideFrom,
            SlideTicks = Math.Clamp(run.SlideTicks, 0, 255),
        };
    }
}
