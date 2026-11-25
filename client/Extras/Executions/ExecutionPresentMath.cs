using System;
using System.Numerics;
using SmSim.Extras.Executions;

namespace SmClient;

/// <summary>
/// Executions X5: the presentation's engine-free maths (tested in <c>ExecutionPresentMathTests</c>). The drawn pose of an executor comes from X3's <see cref="ExecutionTrack"/> (the
/// server pin's function) at a fractional render tick; the attacker's two clips and their weight from <see cref="ExecutionRow.Blend"/> at the run's distance (never a variant's own
/// distance); every clip's raw time from the row's clock (sequencer seconds) by matched time fractions, which applies the sequencer's playback speed (a row's lengths are after
/// it: <see cref="ExecutionRow.AttackerSeconds"/>, <see cref="ExecutionRow.VictimSeconds"/>); the victim's place from the run and the row (its feet never move).
/// </summary>
public static class ExecutionPresentMath
{
    /// <summary>The executor's feet and yaw at the fractional server tick <paramref name="tick"/>: <see cref="ExecutionTrack.Pose"/> at the ticks either side, interpolated.</summary>
    public static (Vector3 Feet, float Yaw, bool InSlide, float ClipSeconds) Pose(in ExecutionRun run, ExecutionRow row, double tick, int tickRate)
    {
        if (!double.IsFinite(tick) || tick < 0) tick = 0;
        double whole = Math.Floor(tick);
        float f = (float)(tick - whole);
        uint t0 = (uint)Math.Min(whole, uint.MaxValue - 1);
        var a = ExecutionTrack.Pose(run, row, t0, tickRate);
        var b = ExecutionTrack.Pose(run, row, t0 + 1, tickRate);
        float clip = ClipSeconds(run, tick, tickRate);
        return (Vector3.Lerp(a.Feet, b.Feet, f), a.Yaw + ExecutionAlign.Wrap(b.Yaw - a.Yaw) * f, a.InSlide && (b.InSlide || f < 1f), clip);
    }

    /// <summary>The row's clock (sequencer seconds from clip time 0) at the fractional server tick: 0 through the slide.</summary>
    public static float ClipSeconds(in ExecutionRun run, double tick, int tickRate)
    {
        double since = tick - ExecutionTrack.ClipStartTick(run);
        return since <= 0 || !double.IsFinite(since) ? 0f : (float)(since / Math.Max(1, tickRate));
    }

    /// <summary>The forward distance the blend plays at: the run's, or the row's authored gap when the run carries none.</summary>
    public static float DistanceOf(in ExecutionRun run, ExecutionRow row) => float.IsFinite(run.Distance) && run.Distance > 0f ? run.Distance : row.Offset.Y;

    /// <summary>
    /// The attacker's clips to draw: the near clip at full weight and, when the distance falls between two variants, the far clip laid over it at <c>Weight</c>; a row without variants
    /// draws its reference clip alone (<c>Far</c> null).
    /// </summary>
    public static (string Near, string? Far, float Weight) AttackerClips(ExecutionRow row, float distance)
    {
        var (near, far, w) = row.Blend(distance);
        if (near < 0) return (row.AttackerClip, null, 0f);
        string n = row.AttackerVariants[near].Clip;
        if (near == far || w <= 1e-4f) return (n, null, 0f);
        string fc = row.AttackerVariants[far].Clip;
        if (w >= 1f - 1e-4f) return (fc, null, 0f);
        return (n, fc, w);
    }

    /// <summary>
    /// A clip's raw time (seconds of its own animation) for the row's clock <paramref name="seconds"/>: the same fraction of <paramref name="rawDuration"/> as of <paramref name="rowSeconds"/>
    /// (the row's length after the sequencer's speed), clamped to the clip.
    /// </summary>
    public static float RawTime(float seconds, float rowSeconds, float rawDuration)
    {
        if (!(rawDuration > 0f) || !float.IsFinite(rawDuration)) return 0f;
        if (!(rowSeconds > 0f) || !float.IsFinite(rowSeconds) || !float.IsFinite(seconds)) return 0f;
        return Math.Clamp(seconds / rowSeconds, 0f, 1f) * rawDuration;
    }

    /// <summary>The playback speed a row's clock implies for a clip (raw length over the row's length; 1 when unknown).</summary>
    public static float Speed(float rowSeconds, float rawDuration) => rowSeconds > 0f && rawDuration > 0f && float.IsFinite(rowSeconds) && float.IsFinite(rawDuration) ? rawDuration / rowSeconds : 1f;

    /// <summary>
    /// Where the victim stands and faces (game space): the aligned origin plus the row's victim offset (lateral X kept, the run's forward distance) turned by the yaw, at
    /// <paramref name="groundZ"/> (the victim's own feet when known; else the origin's), facing the pair's yaw (<see cref="ExecutionRow.RelativeYaw"/>), as <see cref="ExecutionAlign"/> places it.
    /// </summary>
    public static (Vector3 Feet, float Yaw) VictimPlace(in ExecutionRun run, ExecutionRow row, float? groundZ = null)
    {
        var w = ExecutionAlign.ToWorld(new Vector2(row.Offset.X, DistanceOf(run, row)), run.Yaw);
        return (new Vector3(run.Origin.X + w.X, run.Origin.Y + w.Y, groundZ ?? run.Origin.Z), ExecutionAlign.Wrap(run.Yaw + row.RelativeYaw));
    }

    /// <summary>A root sample of a clip played in place (extracted motion) at its clip fraction, placed from <paramref name="feet"/> at <paramref name="yaw"/>.</summary>
    public static (Vector3 Feet, float Yaw) RootPlace(Vector3 feet, float yaw, System.Collections.Generic.IReadOnlyList<Vector4> root, float fraction)
    {
        if (root.Count == 0) return (feet, yaw);
        var s = ExecutionRow.RootAt(root, fraction);
        var w = ExecutionAlign.ToWorld(new Vector2(s.X, s.Y), yaw);
        return (new Vector3(feet.X + w.X, feet.Y + w.Y, feet.Z + s.Z), yaw + s.W);
    }

    /// <summary>
    /// How long a proxy lives from the end of its move (seconds): a completed move (<paramref name="done"/>) leaves the victim lying for <paramref name="lieSeconds"/> then
    /// sinking for <paramref name="sinkSeconds"/> (Warfront: the crowd's own corpse timing, so the hidden crowd member would have faded by then); any other end
    /// <see cref="ExecutionClientState.EndedKeepSeconds"/>.
    /// </summary>
    public static double AfterEnd(bool done, float lieSeconds, float sinkSeconds) => done ? Math.Max(0f, lieSeconds) + Math.Max(0f, sinkSeconds) : ExecutionClientState.EndedKeepSeconds;

    /// <summary>How deep a proxy corpse has sunk <paramref name="age"/> seconds after its move ended (the crowd's sink: none while it lies, then linear to <paramref name="depth"/>).</summary>
    public static float Sink(double age, float lieSeconds, float sinkSeconds, float depth) =>
        age <= lieSeconds || !(sinkSeconds > 0f) ? 0f : (float)Math.Min(1.0, (age - lieSeconds) / sinkSeconds) * depth;

    /// <summary>Whether a Classic melee victim's death pose is still held <paramref name="sinceDeath"/> seconds after its death (the window, X1's <c>DeathPoseHoldSeconds</c>).</summary>
    public static bool DeathHeld(double sinceDeath, float holdSeconds) => sinceDeath >= 0 && sinceDeath < holdSeconds;
}
