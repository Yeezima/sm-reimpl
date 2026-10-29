using System.Numerics;
using System.Runtime.CompilerServices;

namespace SmSim;

/// <summary>
/// The rules by which a jump-pack flight is drawn (M45, <c>NetworkView.Jump</c>), shared by the client (which draws it) and the server (which hits the
/// drawn pose, fix/hitboxes): the flight clip's time fraction (the vertical angle of the motion, damped), the low or high set (height above the ground,
/// with hysteresis), the clip's direction and aim keys in lookup order, and the take-off window. Engine-free. <c>SmContent.JumpFlightPose</c> forwards here.
/// </summary>
public static class FlightPoseRules
{
    /// <summary>The graph's blend weights for the low and high flight sets (metres above the ground; extracted from 'Jump Move Blend').</summary>
    public const float LowWeight = 9f, HighWeight = 18f;
    /// <summary>Where the nearer weight changes (the midpoint of the two weights; ours).</summary>
    public const float LowHighSwitch = (LowWeight + HighWeight) / 2f;
    /// <summary>Metres either side of <see cref="LowHighSwitch"/> before the set changes (ours, so a marine near the midpoint does not flicker).</summary>
    public const float LowHighHysteresis = 1f;
    /// <summary>Time constant (s) of the fraction's damping (ours; the code's damping of the <c>_DAMPED</c> variables is unknown).</summary>
    public const float DampSeconds = 0.15f;
    /// <summary>Pitch (radians) beyond which a flight clip's aim is up or down rather than level (client choice).</summary>
    public const float AimThreshold = 0.35f;
    /// <summary>Horizontal speed (m/s) relative to the facing below which the neutral (U) flight clip is used (client choice).</summary>
    public const float NeutralSpeed = 3f;
    /// <summary>The take-off clip, shown from the press (on the ground and in the air) until <see cref="TakeOffBranch"/>.</summary>
    public const string TakeOffClip = "SM_JMP_Curve_F_Lo_Far_01";
    /// <summary>The take-off clip's <c>BRANCH_To_Jump</c> (s after the press): the flight clips take over from here.</summary>
    public const float TakeOffBranch = 1f / 3f;
    /// <summary>The ground probe: from this high above the feet, at most this far down (metres; the client's <c>AboveGround</c>).</summary>
    public const float GroundLift = 0.5f, GroundReach = 60f;

    /// <summary>
    /// The target time fraction (0..1) for a motion with vertical speed <paramref name="vertical"/> and horizontal speed <paramref name="horizontal"/>
    /// (m/s): 0 straight up, 0.5 level, 1 straight down. No motion at all is level (0.5). Not finite inputs: 0.5.
    /// </summary>
    public static float Target(float vertical, float horizontal)
    {
        if (!float.IsFinite(vertical) || !float.IsFinite(horizontal)) return 0.5f;
        horizontal = MathF.Abs(horizontal);
        if (MathF.Abs(vertical) < 1e-4f && horizontal < 1e-4f) return 0.5f;
        float elevation = MathF.Atan2(vertical, horizontal);   // −π/2 .. π/2
        return Math.Clamp(0.5f - elevation / MathF.PI, 0f, 1f);
    }

    /// <summary>The target fraction of a player's motion (<see cref="Target(float, float)"/> of its vertical and horizontal speed).</summary>
    public static float Target(in PlayerState p) => Target(p.Velocity.Z, MathF.Sqrt(p.Velocity.X * p.Velocity.X + p.Velocity.Y * p.Velocity.Y));

    /// <summary>
    /// <paramref name="current"/> moved toward <paramref name="target"/> over <paramref name="dt"/> seconds with the time constant <paramref name="tau"/>
    /// (exponential; never overshoots). A current that is not a number (a new flight) starts at the target; a tau of 0 or less jumps to it.
    /// </summary>
    public static float Damp(float current, float target, float dt, float tau = DampSeconds)
    {
        if (!float.IsFinite(target)) target = 0.5f;
        if (!float.IsFinite(current)) return target;
        if (!(dt > 0f)) return current;
        if (!(tau > 0f)) return target;
        return current + (target - current) * (1f - MathF.Exp(-dt / tau));
    }

    /// <summary>
    /// Whether the low set (<c>ZeroLo</c>) is shown at <paramref name="aboveGround"/> metres above the ground, given whether it was (<paramref name="wasLow"/>):
    /// below <see cref="LowHighSwitch"/> − <see cref="LowHighHysteresis"/> low, above + it high, in between unchanged. Unknown height (not finite): high.
    /// </summary>
    public static bool Low(float aboveGround, bool wasLow)
    {
        if (!float.IsFinite(aboveGround)) return false;
        if (aboveGround < LowHighSwitch - LowHighHysteresis) return true;
        if (aboveGround > LowHighSwitch + LowHighHysteresis) return false;
        return wasLow;
    }

    /// <summary>The feet's height above the character collision below them (the client's probe); infinity without a level or ground within reach.</summary>
    public static float AboveGround(Vector3 feet, CollisionWorld? world)
    {
        if (world is null) return float.PositiveInfinity;
        return world.Character.Raycast(feet + new Vector3(0f, 0f, GroundLift), -Vector3.UnitZ, GroundReach + GroundLift) is { } hit
            ? MathF.Max(0f, hit.Distance - GroundLift) : float.PositiveInfinity;
    }

    /// <summary>
    /// The flight clip keys (direction F/B/L/R/U, aim U/F/D) to look up for <paramref name="s"/>, in order: its own pair, the level aim, the neutral direction
    /// with its aim, the neutral level clip; a set without any of them shows its first clip. Direction: the horizontal velocity relative to the facing
    /// (neutral below <see cref="NeutralSpeed"/>); aim: the pitch beyond ±<see cref="AimThreshold"/>.
    /// </summary>
    public static ((char Dir, char Aim), (char Dir, char Aim), (char Dir, char Aim), (char Dir, char Aim)) Candidates(in PlayerState s)
    {
        float fx = -MathF.Sin(s.Yaw), fy = MathF.Cos(s.Yaw);
        float forward = s.Velocity.X * fx + s.Velocity.Y * fy, right = s.Velocity.X * fy - s.Velocity.Y * fx;
        char dir = MathF.Max(MathF.Abs(forward), MathF.Abs(right)) < NeutralSpeed ? 'U'
            : MathF.Abs(forward) >= MathF.Abs(right) ? (forward > 0f ? 'F' : 'B') : (right > 0f ? 'R' : 'L');
        char aim = s.Pitch > AimThreshold ? 'U' : s.Pitch < -AimThreshold ? 'D' : 'F';
        return ((dir, aim), (dir, 'F'), ('U', aim), ('U', 'F'));
    }

    /// <summary>
    /// One tick (or frame) of the view a viewer keeps of another player's jump (the client's <c>JumpTrack</c> for a remote player, as the slim record shows
    /// it): jumping on the ground is the take-off, jumping in the air the flight, anything else none. A new view resets the fraction; the take-off's press is
    /// its start (seen in the air first: <paramref name="takeOffDelay"/> before); in the air until <see cref="TakeOffBranch"/> after the press the take-off clip
    /// shows, then the flight clip at the damped fraction and the low or high set (kept across flights). Returns the pose to show.
    /// </summary>
    public static FlightView Step(FlightTrack track, in PlayerState s, float dt, float aboveGround, float takeOffDelay)
    {
        var raw = !s.Alive ? FlightTrack.ViewKind.None
            : JumpPack.Jumping(s.Jump) ? (s.Grounded ? FlightTrack.ViewKind.TakeOff : FlightTrack.ViewKind.Air) : FlightTrack.ViewKind.None;
        if (raw != track.View)
        {
            track.Fraction = float.NaN;
            if (raw == FlightTrack.ViewKind.TakeOff) track.SincePress = 0f;
            else if (raw == FlightTrack.ViewKind.Air && track.View != FlightTrack.ViewKind.TakeOff) track.SincePress = takeOffDelay;
            else if (raw == FlightTrack.ViewKind.Air) track.SincePress += dt;   // the lift-off: the press keeps its time
            track.View = raw;
        }
        else if (raw != FlightTrack.ViewKind.None) track.SincePress += dt;
        switch (raw)
        {
            case FlightTrack.ViewKind.TakeOff:
                return new FlightView(FlightViewPhase.TakeOff, track.SincePress, float.NaN, track.Low);
            case FlightTrack.ViewKind.Air when track.SincePress < TakeOffBranch - 1e-4f:   // 1e-4 s: the branch tick counts as past it (whole ticks sum to 1/3 in float)
                return new FlightView(FlightViewPhase.TakeOff, track.SincePress, float.NaN, track.Low);
            case FlightTrack.ViewKind.Air:
                track.Fraction = Damp(track.Fraction, Target(s), dt);
                track.Low = Low(aboveGround, track.Low);
                return new FlightView(FlightViewPhase.Flight, track.SincePress, track.Fraction, track.Low);
            default:
                return default;
        }
    }
}

/// <summary>A viewer's running view of one player's jump (<see cref="FlightPoseRules.Step"/>).</summary>
public sealed class FlightTrack
{
    public enum ViewKind : byte { None, TakeOff, Air }
    public ViewKind View;
    /// <summary>Seconds since the take-off's press (estimated when the air was seen first).</summary>
    public float SincePress;
    /// <summary>The damped fraction (NaN: a new flight).</summary>
    public float Fraction = float.NaN;
    /// <summary>The low set is shown (kept across flights).</summary>
    public bool Low;
}

/// <summary>What a jump-pack player shows: none (the rest pose), the take-off clip at <see cref="Seconds"/> after the press, or a flight clip.</summary>
public enum FlightViewPhase : byte { None, TakeOff, Flight }

/// <summary>A jump's drawn pose (<see cref="FlightPoseRules.Step"/>): the phase, seconds since the press, the flight clip's damped fraction and the low set.</summary>
public readonly record struct FlightView(FlightViewPhase Phase, float Seconds, float Fraction, bool Low);

/// <summary>
/// fix/hitboxes (protocol 37): one flight clip's posed hit volumes. <see cref="Sets"/>[i] is clip <see cref="Clip"/> at fraction i / (count − 1), each in the
/// order and character frame of <see cref="SimConfig.HitVolumes"/>.
/// </summary>
/// <param name="Dir">F, B, L, R or U.</param>
/// <param name="Aim">U, F or D.</param>
/// <param name="Low">The low set (<c>SM_JMP_ZeroLo_*</c>) rather than the high one.</param>
public sealed record FlightPose(string Clip, char Dir, char Aim, bool Low, IReadOnlyList<IReadOnlyList<HitVolume>> Sets)
{
    public int IndexOf(float fraction)
    {
        if (!float.IsFinite(fraction)) fraction = 0.5f;
        return (int)MathF.Round(Math.Clamp(fraction, 0f, 1f) * (Sets.Count - 1));
    }
}

/// <summary>
/// fix/hitboxes (protocol 37; owner, 2026-10-29: "it feels difficult to shoot enemy assault marines or raptors while they're flying. check on their
/// hitboxes"): the hit volumes of a jump-pack player as the client draws the jump: the take-off clip (<see cref="TakeOff"/>, one set per tick after the
/// press) and the flight clips (<see cref="Poses"/>, in the client's lookup order), chosen by the shared <see cref="FlightPoseRules"/>. Built from the
/// install by <c>SmContent.FlightHitVolumes</c>.
/// </summary>
public sealed record FlightVolumes(IReadOnlyList<FlightPose> Poses)
{
    /// <summary>The take-off clip at 0, 1, 2 … ticks after the press (until <see cref="FlightPoseRules.TakeOffBranch"/>); empty: the rest volumes then.</summary>
    public IReadOnlyList<IReadOnlyList<HitVolume>> TakeOff { get; init; } = [];
    /// <summary>The take-off clip's name (hashed; null without take-off sets).</summary>
    public string? TakeOffClip { get; init; }
    /// <summary>Raised when the rules that build or choose the sets change (hashed instead of the posed values).</summary>
    public const int RulesVersion = 1;

    /// <summary>Whether there is a pose, each with at least two sets, each valid with a head (as <see cref="BodySpec.IsValid"/> asks of volumes).</summary>
    public bool IsValid => Poses is { Count: > 0 } && Poses.All(p => p is not null && "FBLRU".Contains(p.Dir) && "UFD".Contains(p.Aim) && p.Sets is { Count: >= 2 } && p.Sets.All(Valid))
        && TakeOff.All(Valid);

    static bool Valid(IReadOnlyList<HitVolume> s) => s is { Count: > 0 and <= 64 } && s.All(v => v is not null && v.IsValid) && s.Any(v => v.Zone == HitZone.Head);

    /// <summary>
    /// For the config hash (S1 of the review): the rules version, the clips, keys and step counts, never the posed values (decoded with platform maths:
    /// a Linux server and a Windows client may differ in the last bits; the server alone hits with them).
    /// </summary>
    internal void Write(BinaryWriter w)
    {
        w.Write(RulesVersion);
        w.Write(Poses.Count);
        foreach (var p in Poses) { w.Write(p.Clip); w.Write(p.Dir); w.Write(p.Aim); w.Write(p.Low); w.Write(p.Sets.Count); }
        w.Write(TakeOffClip ?? ""); w.Write(TakeOff.Count);
    }

    /// <summary>The pose index the client shows for <paramref name="p"/> with the low set or not (<see cref="FlightPoseRules.Candidates"/>, the set's first clip as the fallback).</summary>
    public int Choose(in PlayerState p, bool low)
    {
        bool set = (low && Poses.Any(x => x.Low)) || !Poses.Any(x => !x.Low);   // only a low set: it is the high one too
        var (a, b, c, d) = FlightPoseRules.Candidates(p);
        foreach (var (dir, aim) in new[] { a, b, c, d })
            for (int i = 0; i < Poses.Count; i++)
                if (Poses[i].Low == set && Poses[i].Dir == dir && Poses[i].Aim == aim) return i;
        for (int i = 0; i < Poses.Count; i++) if (Poses[i].Low == set) return i;
        return 0;
    }

    /// <summary>The take-off set for <paramref name="seconds"/> after the press (the nearest tick's; clamped), or null without take-off sets.</summary>
    public IReadOnlyList<HitVolume>? TakeOffAt(float seconds, float tickSeconds) =>
        TakeOff.Count == 0 ? null : TakeOff[Math.Clamp((int)MathF.Round((float.IsFinite(seconds) ? seconds : 0f) / tickSeconds), 0, TakeOff.Count - 1)];
}

public sealed partial record SimConfig
{
    /// <summary>
    /// fix/hitboxes (protocol 37): the volumes of a jump-pack player as the client draws its take-off and flight; null (tests, placeholders, an install without
    /// the clips): the rest volumes as before. Only for players hit with this config's own volumes (a class body with volumes of its own keeps them).
    /// Hashed only when set, and then by its clips and counts (<see cref="FlightVolumes.Write"/>).
    /// </summary>
    public FlightVolumes? FlightHitVolumes { get; init; }

    static readonly ConditionalWeakTable<SimConfig, FlightCache> FlightCaches = new();

    sealed class FlightCache(int poses)
    {
        public readonly SimConfig?[][] Flight = new SimConfig?[poses][];
        public SimConfig?[]? TakeOff;
    }

    /// <summary>
    /// The config player <paramref name="p"/> is hit with when it shows <paramref name="view"/> (<see cref="FlightPoseRules.Step"/>): its class's
    /// (<see cref="For"/>), with the take-off or flight clip's volumes in place of the rest volumes. Copies are cached per config instance; no raycast.
    /// </summary>
    public SimConfig HitBody(in PlayerState p, FlightView view)
    {
        var body = For(p.Class);
        if (FlightHitVolumes is not { } flight || view.Phase == FlightViewPhase.None || !p.Alive || HitVolumes is null || !ReferenceEquals(body.HitVolumes, HitVolumes)) return body;
        var cache = FlightCaches.GetValue(body, _ => new FlightCache(flight.Poses.Count));
        if (view.Phase == FlightViewPhase.TakeOff)
        {
            if (flight.TakeOff.Count == 0) return body;
            int k = Math.Clamp((int)MathF.Round((float.IsFinite(view.Seconds) ? view.Seconds : 0f) / TickSeconds), 0, flight.TakeOff.Count - 1);
            var row = cache.TakeOff ??= new SimConfig?[flight.TakeOff.Count];
            return row[k] ??= body with { HitVolumes = flight.TakeOff[k] };
        }
        int pose = flight.Choose(p, view.Low);
        var chosen = flight.Poses[pose];
        int index = chosen.IndexOf(view.Fraction);
        var sets = cache.Flight[pose] ??= new SimConfig?[chosen.Sets.Count];
        return sets[index] ??= body with { HitVolumes = chosen.Sets[index] };
    }

    /// <summary>
    /// <see cref="HitBody(in PlayerState, FlightView)"/> from the state alone (no running view: a flight with the undamped fraction and the low set without
    /// hysteresis, nothing for the take-off); for tests and probes. The server and the client use the running view.
    /// </summary>
    public SimConfig HitBody(in PlayerState p, CollisionWorld? world) =>
        HitBody(p, Flying(p) ? new FlightView(FlightViewPhase.Flight, FlightPoseRules.TakeOffBranch, FlightPoseRules.Target(p),
            FlightPoseRules.AboveGround(p.Position, world) < FlightPoseRules.LowHighSwitch) : default);

    /// <summary>Whether a player is in jump-pack flight: alive and off the ground in a jump's ascent, hang or fall.</summary>
    public static bool Flying(in PlayerState p) => p.Alive && !p.Grounded && p.Jump is JumpPhase.Ascent or JumpPhase.Hang or JumpPhase.Fall;
}
