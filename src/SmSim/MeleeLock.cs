using System.Numerics;

namespace SmSim;

/// <summary>
/// feel/melee-lock: one distance variant of a melee attack's clip (the graph blends <c>…_1m</c>, <c>…_3.5m</c>, <c>…_7m</c> on
/// <c>FLT_MeleeTargetDistance</c>): the blend weight it sits at (metres; 0 for an attack that plays one clip) and its extracted root
/// motion along the character's facing, evenly spaced over the attack's <see cref="MeleeStep.Duration"/> (the first sample at 0), and the clip's name (the client poses it).
/// </summary>
public sealed record MeleeVariant(float Distance, IReadOnlyList<float> Forward, string Clip = "")
{
    /// <summary>Forward root motion (metres) <paramref name="time"/> seconds into an attack of <paramref name="duration"/> seconds (linear between samples, clamped).</summary>
    public float At(float time, float duration)
    {
        int n = Forward.Count;
        if (n == 0) return 0f;
        if (n == 1 || !(duration > 0f)) return Forward[0];
        float u = Math.Clamp(time / duration, 0f, 1f) * (n - 1);
        int i = Math.Min((int)u, n - 2);
        return Forward[i] + (Forward[i + 1] - Forward[i]) * (u - i);
    }
}

/// <summary>
/// feel/melee-lock: what an attack's sequencer gives for reaching its target (<c>SmContent.MeleeTuning</c> from the behaviour graph):
/// the <c>AttackAlignment</c> window (seconds; the attacker turns to its locked target), the <c>AttackDistance</c> window and value, the
/// graph's starting value of <c>FLT_MeleeTargetDistance</c> (the blend without a target) and the distance variants with their root motion.
/// Rules in <see cref="MeleeLock"/>; the windows' meanings are an **interpretation** of their names. <paramref name="Variants"/> are in ascending distance.
/// </summary>
public sealed record MeleeReach(float AlignFrom, float AlignTo, float DistanceFrom, float DistanceTo, float Distance, float DefaultDistance,
    IReadOnlyList<MeleeVariant> Variants)
{
    /// <summary>The variants' blend weights' range (both 0 for a single clip).</summary>
    public (float Min, float Max) Range
    {
        get
        {
            // A plain loop (no allocation: called every tick of a lunge); not cached, so a record copied with other variants stays right.
            if (Variants.Count == 0) return (0f, 0f);
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < Variants.Count; i++) { min = MathF.Min(min, Variants[i].Distance); max = MathF.Max(max, Variants[i].Distance); }
            return (min, max);
        }
    }

    /// <summary>
    /// The blended forward root motion at <paramref name="time"/> for the blend parameter <paramref name="distance"/>: linear between the two
    /// variants around it (Havok's blender between child weights), clamped to the outer ones.
    /// </summary>
    public float ForwardAt(float time, float duration, float distance)
    {
        var (low, high, w) = Blend(distance);
        if (low is null) return 0f;
        float a = low.At(time, duration);
        return high is null ? a : a + (high.At(time, duration) - a) * w;
    }

    /// <summary>
    /// The two variants around blend value <paramref name="distance"/> and the second's weight (Havok's blender between neighbouring child weights;
    /// clamped to the outer ones: then the second is null). Low is null only without variants.
    /// </summary>
    public (MeleeVariant? Low, MeleeVariant? High, float Weight) Blend(float distance)
    {
        if (Variants.Count == 0) return (null, null, 0f);
        if (Variants.Count == 1 || !float.IsFinite(distance)) return (Variants[0], null, 0f);
        MeleeVariant? below = null, above = null;
        foreach (var v in Variants)
        {
            if (v.Distance <= distance && (below is null || v.Distance > below.Distance)) below = v;
            if (v.Distance >= distance && (above is null || v.Distance < above.Distance)) above = v;
        }
        if (below is null) return (above, null, 0f);
        if (above is null || above.Distance <= below.Distance) return (below, null, 0f);
        return (below, above, (distance - below.Distance) / (above.Distance - below.Distance));
    }

    /// <summary>
    /// The blend value an attack began at, seen <paramref name="time"/> into it with its target <paramref name="distanceNow"/> away: the attacker has closed the
    /// blended clip's root motion to that time, so D0 = distanceNow + F(D0, time), solved exactly on the segment between the two variants around it (the blend
    /// is linear in D0 there) while that is well-conditioned, else by fixed-point steps from below; clamped to the variants (a target beyond the farthest one
    /// gets the farthest clip). Used by the lunge and by the client's pose.
    /// </summary>
    public float StartDistance(float time, float duration, float distanceNow)
    {
        var (low, high) = Range;
        if (!float.IsFinite(distanceNow)) return Math.Clamp(DefaultDistance, low, high);
        if (Variants.Count < 2) return Math.Clamp(distanceNow, low, high);
        for (int i = 0; i + 1 < Variants.Count; i++)
        {
            float di = Variants[i].Distance, dj = Variants[i + 1].Distance;
            if (!(dj > di)) continue;
            float fi = Variants[i].At(time, duration), fj = Variants[i + 1].At(time, duration);
            float b = (fj - fi) / (dj - di), a = fi - b * di;
            // Near the hit the clips converge on one strike distance (b → 1) and D0 is ill-conditioned: solved only while 1 − b ≥ 0.05 (there a wrong D0
            // changes this tick's step by little: the variants' steps differ by (b(to) − b(from)) per metre).
            if (1f - b < 0.05f) continue;
            float solved = (distanceNow + a) / (1f - b);
            if (solved >= di - 1e-4f && solved <= dj + 1e-4f) return Math.Clamp(solved, low, high);
        }
        // Ill-conditioned (or no segment holds the solution): the fixed-point steps from the distance now, which rise monotonically toward D0 and never
        // pass it (the lunge then errs short, never long).
        float x = Math.Clamp(distanceNow, low, high);
        for (int n = 0; n < 16; n++) x = Math.Clamp(distanceNow + ForwardAt(time, duration, x), low, high);
        return x;
    }

    internal void Write(BinaryWriter w)
    {
        foreach (float f in new[] { AlignFrom, AlignTo, DistanceFrom, DistanceTo, Distance, DefaultDistance }) w.Write(f);
        w.Write(Variants.Count);
        foreach (var v in Variants) { w.Write(v.Distance); w.Write(v.Forward.Count); foreach (float f in v.Forward) w.Write(f); w.Write(v.Clip); }
    }
}

/// <summary>
/// feel/melee-lock: the melee target lock's tuning. Acquisition values are the install's global <c>directional_targeting_ext</c>
/// (<c>half_cone_angle_for_melee</c> 45, <c>max_melee_target_distance</c> 7, <c>max_melee_target_vertical_distance</c> 1.5,
/// <c>distance_band_size</c> 1, <c>angle_band_size</c> 6, <c>fan_start_distance</c> 1.5, <c>fan_set_back_distance</c> 1,
/// <c>sprint_or_rush_tuning</c> 7 m / 45°), read by <c>SmContent.GameplayTuning</c>; how they combine is an **interpretation**.
/// The rest is ours (spec/behavior.md "Melee target lock").
/// </summary>
public sealed record MeleeLockSpec
{
    public static readonly MeleeLockSpec Default = new();

    /// <summary>Half-angle of the targeting cone around the stick (or view) direction, degrees.</summary>
    public float HalfAngle { get; init; } = 45f;
    /// <summary>Farthest target (feet, horizontal), metres.</summary>
    public float Range { get; init; } = 7f;
    /// <summary>Largest difference in feet height, metres.</summary>
    public float Vertical { get; init; } = 1.5f;
    /// <summary>Candidates rank by distance in bands of this many metres, then by angle in bands of <see cref="AngleBand"/> degrees.</summary>
    public float DistanceBand { get; init; } = 1f;
    public float AngleBand { get; init; } = 6f;
    /// <summary>The cone's apex lies this far behind the attacker (the "fan" set back; interpretation).</summary>
    public float FanSetBack { get; init; } = 1f;
    /// <summary>Targets this close count in the whole front half (the fan starts here; interpretation).</summary>
    public float FanStart { get; init; } = 1.5f;
    /// <summary>The charge's cone and reach (<c>sprint_or_rush_tuning</c>).</summary>
    public float RushHalfAngle { get; init; } = 45f;
    public float RushRange { get; init; } = 7f;
    /// <summary>Ours: a held lock lasts while its target is within <see cref="Range"/> plus this (metres).</summary>
    public float Leash { get; init; } = 1f;
    /// <summary>Ours: the alignment turn's cap, degrees a second (the window itself completes the turn when it can).</summary>
    public float MaxAlignRate { get; init; } = 1080f;
    /// <summary>Ours: how fast the charge steers to its target, degrees a second (only while the target is inside the charge's cone).</summary>
    public float RushSteerRate { get; init; } = 180f;
    /// <summary>Ours: the lunge never brings the attacker nearer its target than this (metres, feet to feet: two capsules and a little).</summary>
    public float Separation { get; init; } = 1f;
    /// <summary>sim/melee-lunge (ours): a locked lunge's burst speed cap, m/s: the bull rush clip's slowest opening burst (spec "Airborne charge: no cap": 28 to 41 m/s), so no teleport.</summary>
    public float BurstSpeed { get; init; } = 28f;
    /// <summary>sim/melee-lunge (ours): the share of the way to the hit by which the locked burst has closed the gap (it settles there; the hit stays at its hit time).</summary>
    public float BurstFraction { get; init; } = 0.6f;
    /// <summary>
    /// feel/melee-charge-2 (protocol 42; the owner's request of 2026-10-29: "melee and stun lock-ons feel janky, make them fluid"; ours): the alignment turn
    /// eases in and out over the window (<see cref="MeleeLock.Align"/>: a smoothstep share of the turn left) instead of turning at a constant rate that starts
    /// and stops in one tick; false: the M32 rule (tests and before / after numbers).
    /// </summary>
    public bool AlignEase { get; init; } = true;
    /// <summary>
    /// feel/melee-charge-2 (ours): a held lock (an attack or charge under way, or the client's recent lock at a new attack's start) is kept while its target
    /// lies within the cone's half-angle plus this many degrees, so it does not flip at the cone's edge (acquisition keeps the plain cone).
    /// </summary>
    public float Hysteresis { get; init; } = 15f;
    /// <summary>
    /// feel/melee-charge-2 (ours): with <see cref="AlignEase"/>, an alignment window shorter than this (seconds) is widened to it (<see cref="MeleeLock.AlignWindow"/>):
    /// later up to the hit, then earlier; 0.2 s.
    /// </summary>
    public float MinAlignSpan { get; init; } = 0.2f;

    internal void Write(BinaryWriter w)
    {
        foreach (float f in new[] { HalfAngle, Range, Vertical, DistanceBand, AngleBand, FanSetBack, FanStart, RushHalfAngle, RushRange, Leash, MaxAlignRate, RushSteerRate, Separation })
            w.Write(f);
        if (BurstSpeed != 28f || BurstFraction != 0.6f) { w.Write((byte)0xE3); w.Write(BurstSpeed); w.Write(BurstFraction); }
        // feel/melee-charge-2: only when not the M32 rule, so a spec from before hashes as it did when both are off.
        if (AlignEase || Hysteresis != 0f) { w.Write((byte)0xE2); w.Write(AlignEase); w.Write(Hysteresis); w.Write(MinAlignSpan); }
    }
}

public sealed partial record SimConfig
{
    /// <summary>feel/melee-lock: the melee target lock (<see cref="MeleeLock"/>); hashed only when it differs from <see cref="MeleeLockSpec.Default"/>.</summary>
    public MeleeLockSpec MeleeLock { get; init; } = MeleeLockSpec.Default;
    /// <summary>
    /// feel/melee-charge-2 (protocol 42; the owner's request of 2026-10-29: "make all melee step forwards step forward more"; a **deliberate change**, the value an
    /// **estimate (owner's request)** to review in a playtest): the factor on every melee attack's forward root motion (<see cref="MeleeLock.LungeStep"/>).
    /// Backward motion, the charge, the dash attack and the slash are unchanged. With a locked target the blend is still solved from the distance now (<see cref="MeleeReach.StartDistance"/>, unscaled), so the approach still
    /// aims at the clips' strike distance (it gets there sooner) and the extra shows in the follow-through after the hit, never nearer than
    /// <see cref="MeleeLockSpec.Separation"/>. 1 (the default, synthetic configs): the clips' motion. The content loader sets 1.625 (1.3 from 2026-10-29, +25% on 2026-10-31; <c>GameplayTuning.OwnerLungeScale</c>). Hashed when not 1.
    /// </summary>
    public float MeleeLungeScale { get; init; } = 1f;
    /// <summary><see cref="MeleeLungeScale"/> as applied (a scale that is not finite or not above 0 counts as 1).</summary>
    public float LungeScale => float.IsFinite(MeleeLungeScale) && MeleeLungeScale > 0f ? MeleeLungeScale : 1f;
}

/// <summary>
/// feel/melee-lock (protocol 32; the owner's knowledge of the original, 2026-10-29: "when initiating a melee attack or charge or f stun strike,
/// if the character model is moving in the general direction of an enemy player model they 'stick' to it and line up to keep delivering more
/// melee attacks"; "you should step forward when swinging"). The target lock, the alignment turn and the lunge.
/// <para>
/// Lock: the client picks it (<see cref="Choose"/>) from its view of the others at its input's view tick and sends its id in the input
/// (<see cref="PlayerInput.MeleeTarget"/>); the server and the client's prediction keep it only while <see cref="Target"/> accepts it in the
/// same view (the server's: the lag-compensated players, <c>World.SeenBy</c>). A combo keeps its target from attack to attack.
/// </para>
/// <para>
/// Alignment: in each tick that overlaps the attack's <see cref="MeleeReach.AlignFrom"/>..<see cref="MeleeReach.AlignTo"/> the attack's heading
/// (<see cref="PlayerState.Yaw"/>) turns to the target so that it would face it by the window's end, at most <see cref="MeleeLockSpec.MaxAlignRate"/>;
/// without a target it follows the view there. Outside the window the heading holds until the attack ends or the stick moves it after
/// <see cref="MeleeStep.MoveAt"/>.
/// </para>
/// <para>
/// Lunge (<see cref="LungeStep"/>): the body follows the attack's root motion along its heading. Without a target: the variant at the graph's
/// starting blend value (the knife's 1 m clip). With one: the blend the target's distance at the attack's start picks (<see cref="MeleeReach.StartDistance"/>,
/// re-solved every tick from the distance now, so a moving target is chased), whose clips end where they were made to strike from; never nearer the
/// target than <see cref="MeleeLockSpec.Separation"/>.
/// </para>
/// </summary>
public static class MeleeLock
{
    const float Eps = 1e-5f;
    /// <summary>
    /// feel/melee-charge-2 (ours): seconds the client remembers its last lock after an attack or charge ends, and offers it again at the next start
    /// (<see cref="Choose"/>'s previous), so a quick follow-up attack keeps its target instead of re-searching and flipping to a neighbour.
    /// </summary>
    public const float LockMemory = 1.0f;

    /// <summary>Whether <paramref name="other"/> is someone <paramref name="self"/> locks onto: another living player of the other side (anyone without sides).</summary>
    public static bool IsEnemy(in PlayerState self, in PlayerState other) =>
        other.PlayerId != self.PlayerId && other.Alive && (self.Team == Team.None || other.Team == Team.None || self.Team != other.Team);

    /// <summary>The flat heading (unit) of yaw <paramref name="yaw"/> (yaw 0 faces +Y).</summary>
    public static Vector2 Forward(float yaw) => new(-MathF.Sin(yaw), MathF.Cos(yaw));

    /// <summary>The yaw facing along <paramref name="direction"/>.</summary>
    public static float YawOf(Vector2 direction) => MathF.Atan2(-direction.X, direction.Y);

    /// <summary>The signed shortest turn from <paramref name="from"/> to <paramref name="to"/>, radians in (−π, π].</summary>
    public static float Delta(float from, float to)
    {
        float d = (to - from) % (2f * MathF.PI);
        if (d > MathF.PI) d -= 2f * MathF.PI; else if (d <= -MathF.PI) d += 2f * MathF.PI;
        return d;
    }

    /// <summary><paramref name="from"/> turned toward <paramref name="to"/> by at most <paramref name="maxStep"/> radians.</summary>
    public static float TurnToward(float from, float to, float maxStep)
    {
        float d = Delta(from, to);
        return MathF.Abs(d) <= maxStep ? from + d : from + MathF.Sign(d) * MathF.Max(0f, maxStep);
    }

    /// <summary>The direction the attack aims a search: the stick's, in the view's frame, when it is pushed; else the view's heading.</summary>
    public static Vector2 SearchDirection(PlayerInput input)
    {
        input = input.Sanitized();
        var forward = Forward(input.Yaw);
        var right = new Vector2(forward.Y, -forward.X);
        var stick = right * input.MoveX + forward * input.MoveY;
        return stick.LengthSquared() > 0.01f ? Vector2.Normalize(stick) : forward;
    }

    /// <summary>
    /// Whether <paramref name="self"/> may lock <paramref name="other"/> through the level: the eye sees the chest through weapon collision (as melee hits),
    /// and no spawn-room blocker that stops <paramref name="self"/>'s side stands between them (the line from waist to waist, sampled every 0.25 m; the
    /// lunge could not pass it). Always true without a level.
    /// </summary>
    public static bool Sees(in PlayerState self, in PlayerState other, SimConfig c, CollisionWorld? world)
    {
        if (world is null) return true;
        var eye = Combat.EyePosition(self, c);
        var chest = other.Position + new Vector3(0, 0, HitVolumes.CentreHeight(c.For(other.Class)));
        var to = chest - eye;
        float length = to.Length();
        if (length > 1e-4f && world.Weapons.Raycast(eye, to / length, length) is { } wall && wall.Distance < length) return false;
        if (world.Blockers.Count == 0) return true;
        var from = self.Position + new Vector3(0, 0, 1f);
        var path = other.Position + new Vector3(0, 0, 1f) - from;
        int samples = Math.Clamp((int)MathF.Ceiling(path.Length() / 0.25f), 1, 64);
        foreach (var b in world.Blockers)
        {
            if (!b.Blocks(self.Team)) continue;
            for (int i = 0; i <= samples; i++)
                if (b.Contains(from + path * (i / (float)samples))) return false;
        }
        return true;
    }

    /// <summary>
    /// Whether <paramref name="target"/> (feet) lies in the targeting cone along <paramref name="direction"/> (unit) from <paramref name="self"/>: within
    /// <paramref name="halfAngleDegrees"/> as seen from the apex <see cref="MeleeLockSpec.FanSetBack"/> behind the attacker, or nearer than
    /// <see cref="MeleeLockSpec.FanStart"/> and in front. Outputs the angle from the apex (degrees).
    /// </summary>
    public static bool InCone(in PlayerState self, Vector3 target, Vector2 direction, float halfAngleDegrees, SimConfig c, out float angle)
    {
        var spec = c.MeleeLock;
        var at = new Vector2(self.Position.X, self.Position.Y);
        var p = new Vector2(target.X, target.Y);
        var flat = p - at;
        float distance = flat.Length();
        var fromApex = p - (at - direction * MathF.Max(0f, spec.FanSetBack));
        float apexLength = fromApex.Length();
        float cos = apexLength > 1e-4f ? Vector2.Dot(fromApex / apexLength, direction) : 1f;
        angle = MathF.Acos(Math.Clamp(cos, -1f, 1f)) * 180f / MathF.PI;
        if (cos >= MathF.Cos(Math.Clamp(halfAngleDegrees, 0f, 180f) * MathF.PI / 180f)) return true;
        return distance <= spec.FanStart && (distance < 1e-4f || Vector2.Dot(flat / distance, direction) >= 0f);
    }

    /// <summary>
    /// The best target for a search along <paramref name="direction"/> (unit): living enemies within <paramref name="range"/> (feet, horizontal)
    /// and the vertical reach, inside the cone (<see cref="InCone"/>), seen and not behind a spawn-room blocker (<see cref="Sees"/>); ranked by distance
    /// band, then angle band, then distance, then id. 0 when none.
    /// </summary>
    public static int Acquire(in PlayerState self, Vector2 direction, IReadOnlyDictionary<int, PlayerState> others, SimConfig c, CollisionWorld? world,
        float range, float halfAngleDegrees)
    {
        var spec = c.MeleeLock;
        (int Band, int AngleBand, float Distance, int Id)? best = null;
        foreach (var (id, other) in others)
        {
            if (!IsEnemy(self, other) || MathF.Abs(other.Position.Z - self.Position.Z) > spec.Vertical) continue;
            float distance = Offset(self, other).Length();
            if (distance > range || !InCone(self, other.Position, direction, halfAngleDegrees, c, out float angle)) continue;
            if (!Sees(self, other, c, world)) continue;
            var key = ((int)(distance / MathF.Max(spec.DistanceBand, 1e-3f)), (int)(angle / MathF.Max(spec.AngleBand, 1e-3f)), distance, id);
            if (best is null || Compare(key, best.Value) < 0) best = key;
        }
        return best?.Id ?? 0;

        static int Compare((int, int, float, int) a, (int, int, float, int) b) =>
            a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2 != b.Item2 ? a.Item2.CompareTo(b.Item2) : a.Item3 != b.Item3 ? a.Item3.CompareTo(b.Item3) : a.Item4.CompareTo(b.Item4);
    }

    /// <summary>
    /// The tick a lock is checked in: an attack starting from rest (<see cref="MeleeStart"/>), one under way (<see cref="Melee"/>), the charge's
    /// first tick (<see cref="RushStart"/>) and the charge under way (<see cref="Rush"/>).
    /// </summary>
    public enum Phase { MeleeStart, Melee, RushStart, Rush }

    /// <summary>
    /// The locked target as <paramref name="others"/> shows it when the server accepts the lock in this <paramref name="phase"/> (the client's prediction
    /// decides the same from the same view): <paramref name="id"/> names a living enemy within the vertical reach, seen and not behind a spawn-room
    /// blocker (<see cref="Sees"/>), and
    /// <list type="bullet">
    /// <item>an attack's start: within the melee range and the melee cone along the stick (or the view: <see cref="SearchDirection"/>);</item>
    /// <item>an attack under way: within the range plus the leash and in the melee cone along the attack's heading or along the stick / view (a
    /// switch to another enemy must lie there too);</item>
    /// <item>the charge's first tick: within the charge's range and cone along the view; under way: its range plus the leash and its cone along its heading.</item>
    /// </list>
    /// feel/melee-charge-2 (protocol 42): every cone but the charge's first tick is widened by <see cref="MeleeLockSpec.Hysteresis"/> (a lock the client
    /// already holds; its own searches use the plain cones).
    /// Null otherwise (or without a view or an id). Stateless: the server checks every tick and no lock state travels (the snapshot budget is full).
    /// </summary>
    public static PlayerState? Target(in PlayerState self, PlayerInput input, int id, IReadOnlyDictionary<int, PlayerState>? others, SimConfig c,
        CollisionWorld? world, Phase phase)
    {
        if (id <= 0 || others is null || !others.TryGetValue(id, out var other) || !IsEnemy(self, other)) return null;
        var spec = c.MeleeLock;
        if (MathF.Abs(other.Position.Z - self.Position.Z) > spec.Vertical) return null;
        float distance = Offset(self, other).Length();
        // feel/melee-charge-2: a held lock keeps a wider cone (the hysteresis) so it does not flip at the edge; a new search (Acquire) keeps the plain one.
        float keep = float.IsFinite(spec.Hysteresis) ? Math.Clamp(spec.Hysteresis, 0f, 90f) : 0f;
        bool inCone = phase switch
        {
            Phase.MeleeStart => distance <= spec.Range && InCone(self, other.Position, SearchDirection(input), spec.HalfAngle + keep, c, out _),
            Phase.Melee => distance <= spec.Range + spec.Leash && (InCone(self, other.Position, Forward(self.Yaw), spec.HalfAngle + keep, c, out _)
                || InCone(self, other.Position, SearchDirection(input), spec.HalfAngle + keep, c, out _)),
            Phase.RushStart => distance <= spec.RushRange && InCone(self, other.Position, Forward(input.Sanitized().Yaw), spec.RushHalfAngle, c, out _),
            _ => distance <= spec.RushRange + spec.Leash && InCone(self, other.Position, Forward(self.Yaw), spec.RushHalfAngle + keep, c, out _),
        };
        return inCone && Sees(self, other, c, world) ? other : null;
    }

    /// <summary>Whether this tick's input with <paramref name="s"/> is (or may start) an attack that locks: a melee attack or the charge under way, or a melee or heavy press.</summary>
    public static bool Wants(in PlayerState s, PlayerInput input)
    {
        if (!s.Alive) return false;
        if (s.MeleeStep > 0 || s.Rush == RushPhase.Rush) return true;
        bool melee = (input.Buttons & InputButtons.Melee) != 0 && !s.MeleeHeld;
        bool heavy = (input.Buttons & InputButtons.MeleeHeavy) != 0 && !s.MeleeHeavyHeld;
        return melee || heavy;
    }

    /// <summary>
    /// The phase this tick's lock will be checked in, as far as the client can tell before the tick: the charge under way, a heavy press that starts a
    /// charge (the simulation counts the charge's spacing down before it may start one, so a spacing of 1 ends in this tick), an attack under way,
    /// else an attack's start.
    /// </summary>
    public static Phase PhaseOf(in PlayerState s, PlayerInput input, SimConfig c)
    {
        if (s.Rush == RushPhase.Rush) return Phase.Rush;
        bool heavy = (input.Buttons & InputButtons.MeleeHeavy) != 0 && !s.MeleeHeavyHeld;
        if (heavy && s.Rush == RushPhase.None && s.RushLock <= 1 && Loadouts.Rush(c, s) is { } rush && Rush.CanStart(s, input, rush, c)) return Phase.RushStart;
        return s.MeleeStep > 0 ? Phase.Melee : Phase.MeleeStart;
    }

    /// <summary>
    /// The client's lock for its next input (sent as <see cref="PlayerInput.MeleeTarget"/>): 0 unless <see cref="Wants"/>; the
    /// <paramref name="previous"/> lock while <see cref="Target"/> accepts it in this tick's phase (<see cref="PhaseOf"/>: a combo sticks to its target);
    /// else a new search: the charge's cone (along the view at its start, along its heading under way), else the melee cone along the stick (or the view).
    /// </summary>
    public static int Choose(in PlayerState s, PlayerInput input, IReadOnlyDictionary<int, PlayerState>? others, SimConfig c, CollisionWorld? world, int previous)
    {
        if (others is null || !Wants(s, input)) return 0;
        var phase = PhaseOf(s, input, c);
        if (previous > 0 && Target(s, input, previous, others, c, world, phase) is not null) return previous;
        var spec = c.MeleeLock;
        return phase switch
        {
            Phase.RushStart => Acquire(s, Forward(input.Sanitized().Yaw), others, c, world, spec.RushRange, spec.RushHalfAngle),
            Phase.Rush => Acquire(s, Forward(s.Yaw), others, c, world, spec.RushRange, spec.RushHalfAngle),
            _ => Acquire(s, SearchDirection(input), others, c, world, spec.Range, spec.HalfAngle),
        };
    }

    /// <summary>The flat offset from <paramref name="self"/> to <paramref name="target"/>.</summary>
    public static Vector2 Offset(in PlayerState self, in PlayerState target) => new(target.Position.X - self.Position.X, target.Position.Y - self.Position.Y);

    /// <summary>Seconds the tick interval (<paramref name="from"/>, <paramref name="to"/>] shares with the window (<paramref name="open"/>, <paramref name="close"/>].</summary>
    public static float Overlap(float from, float to, float open, float close) => MathF.Max(0f, MathF.Min(to, close) - MathF.Max(from, open));

    /// <summary>
    /// The attack's heading after a tick covering (<paramref name="from"/>, <paramref name="to"/>] of step <paramref name="m"/>: inside the
    /// alignment window turned toward the target (<paramref name="toTarget"/>: the flat offset; null: none) so that it would face it at the
    /// window's end, capped by <see cref="MeleeLockSpec.MaxAlignRate"/>; without a target the view's yaw there; outside the window unchanged.
    /// A step without reach data or without an alignment window follows the view (the rule before protocol 32). feel/melee-charge-2
    /// (<see cref="MeleeLockSpec.AlignEase"/>): the turn eases in and out (<see cref="Ease"/>), toward the target or, without one, toward the view.
    /// </summary>
    public static float Align(float heading, float viewYaw, MeleeStep m, float from, float to, Vector2? toTarget, SimConfig c)
    {
        if (m.Reach is not { } reach || reach.AlignTo <= reach.AlignFrom) return viewYaw;   // no window: the old rule (the view turns the attack)
        var (open, close) = AlignWindow(m, c);
        float overlap = Overlap(from, to, open, close);
        if (overlap <= 0f) return heading;
        bool target = toTarget is { } offset && offset.LengthSquared() >= 1e-8f;
        if (!target && !c.MeleeLock.AlignEase) return viewYaw;   // the M32 rule: the view, at once
        float wanted = target ? YawOf(toTarget!.Value) : viewYaw;
        float share;
        if (c.MeleeLock.AlignEase)
        {
            // feel/melee-charge-2 (ours): the share of the turn left that this tick covers follows a smoothstep over the window, so the heading accelerates from
            // rest at the window's start and settles at its end (re-planned every tick: a moving target or view is followed); a window of one tick turns at once.
            float span = close - open;
            float u1 = Math.Clamp((MathF.Max(from, open) - open) / span, 0f, 1f), u2 = Math.Clamp((MathF.Min(to, close) - open) / span, 0f, 1f);
            float s1 = Ease(u1), s2 = Ease(u2);
            share = s1 >= 1f - 1e-6f ? 1f : Math.Clamp((s2 - s1) / (1f - s1), 0f, 1f);
        }
        else
        {
            float remaining = MathF.Max(close - MathF.Max(from, open), 1e-6f);
            share = Math.Clamp(overlap / remaining, 0f, 1f);
        }
        float cap = c.MeleeLock.MaxAlignRate * MathF.PI / 180f * MathF.Max(overlap, c.TickSeconds);
        return TurnToward(heading, wanted, MathF.Min(MathF.Abs(Delta(heading, wanted)) * share, cap));
    }

    /// <summary>
    /// The alignment window <see cref="Align"/> turns in (seconds into the attack): the data's <see cref="MeleeReach.AlignFrom"/>..<see cref="MeleeReach.AlignTo"/>;
    /// feel/melee-charge-2 (<see cref="MeleeLockSpec.AlignEase"/>, ours): one shorter than <see cref="MeleeLockSpec.MinAlignSpan"/> is widened to it, first later up
    /// to the attack's hit (the heading still faces the target when it strikes), then earlier down to the attack's start, so the knife's 2-tick heavy windows no
    /// longer turn 60 degrees in 0.06 s.
    /// </summary>
    public static (float Open, float Close) AlignWindow(MeleeStep m, SimConfig c)
    {
        if (m.Reach is not { } r) return (0f, 0f);
        float open = r.AlignFrom, close = r.AlignTo, min = c.MeleeLock.MinAlignSpan;
        if (!c.MeleeLock.AlignEase || !(min > 0f) || !float.IsFinite(min) || close <= open || close - open >= min) return (open, close);
        close = MathF.Max(close, MathF.Min(open + min, MathF.Max(m.HitTime, close)));
        open = MathF.Max(0f, MathF.Min(open, close - min));
        return (open, close);
    }

    /// <summary>The smoothstep 3u² − 2u³ (0 at 0, 1 at 1, flat at both ends): the alignment turn's ease (feel/melee-charge-2).</summary>
    public static float Ease(float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return u * u * (3f - 2f * u);
    }

    /// <summary>
    /// The bull rush's heading after one tick of steering to its target (<paramref name="toTarget"/>: the flat offset): turned toward it by at
    /// most <see cref="MeleeLockSpec.RushSteerRate"/>, only while the target lies within <see cref="MeleeLockSpec.RushHalfAngle"/> of the heading.
    /// </summary>
    public static float Steer(float heading, Vector2 toTarget, SimConfig c)
    {
        if (toTarget.LengthSquared() < 1e-8f) return heading;
        float wanted = YawOf(toTarget);
        if (MathF.Abs(Delta(heading, wanted)) > c.MeleeLock.RushHalfAngle * MathF.PI / 180f + Eps) return heading;
        return TurnToward(heading, wanted, c.MeleeLock.RushSteerRate * MathF.PI / 180f * c.TickSeconds);
    }

    /// <summary>
    /// The lunge: metres the attack moves the body along <paramref name="heading"/> over the tick (<paramref name="from"/>, <paramref name="to"/>]
    /// of step <paramref name="m"/> (negative: back), see the class summary. <paramref name="toTarget"/>: the flat offset to the locked target, or null.
    /// feel/melee-charge-2: forward motion × <see cref="SimConfig.MeleeLungeScale"/> (backward motion as the clip), still never nearer the target than the separation.
    /// </summary>
    public static float LungeStep(MeleeStep m, float from, float to, float heading, Vector2? toTarget, SimConfig c)
    {
        if (m.Reach is not { } reach || reach.Variants.Count == 0 || !(to > from)) return 0f;
        float duration = m.Duration;
        var (low, high) = reach.Range;
        float k = c.LungeScale;   // feel/melee-charge-2: forward motion × the lunge scale (an estimate, owner's request)
        if (toTarget is not { } offset)
        {
            float d0 = Math.Clamp(reach.DefaultDistance, low, high);
            float free = reach.ForwardAt(to, duration, d0) - reach.ForwardAt(from, duration, d0);
            return free > 0f ? free * k : free;
        }
        // The blend this attack has followed: the target's distance when it began, solved from its distance now and the clip's motion so far (a moving
        // target re-solves it every tick, so the lunge chases or eases). The clips at that blend end where they were made to strike from.
        // feel/melee-charge-2: solved unscaled (self-correcting: a scaled approach that is ahead picks a nearer blend), the step then × the lunge scale.
        float start = reach.StartDistance(from, duration, offset.Length());
        float step = reach.ForwardAt(to, duration, start) - reach.ForwardAt(from, duration, start);
        if (step > 0f) step *= k;
        // sim/melee-lunge (owner 2026-10-31: a locked step covers the gap quicker and more forcefully; the further the target, the bigger and faster; ours): a front-loaded
        // burst closes the remaining gap to the separation by BurstFraction of the way to the hit, the remaining gap falling quadratically (fast, then settling), at most
        // BurstSpeed (the bull rush clip's slowest opening burst, 28 m/s). The hit stays at its hit time; the clip's own (scaled) step still applies if it is larger.
        if (from < m.HitTime - Eps)
        {
            float finish = m.HitTime * c.MeleeLock.BurstFraction;
            float remaining = MathF.Max(0f, offset.Length() - c.MeleeLock.Separation);
            float share = to >= finish - Eps ? 1f : 1f - MathF.Pow((finish - to) / (finish - from), 2f);
            float burst = MathF.Min(remaining * share, c.MeleeLock.BurstSpeed * (to - from));
            step = MathF.Max(step, burst);
        }
        // Never nearer the target than the separation (only forward motion is held back).
        var spec = c.MeleeLock;
        var forward = Forward(heading);
        float along = Vector2.Dot(offset, forward);
        float lateral = MathF.Abs(offset.X * forward.Y - offset.Y * forward.X);
        if (step > 0f && lateral < spec.Separation)
        {
            float limit = along - MathF.Sqrt(MathF.Max(0f, spec.Separation * spec.Separation - lateral * lateral));
            step = MathF.Min(step, MathF.Max(0f, limit));
        }
        return step;
    }
}
