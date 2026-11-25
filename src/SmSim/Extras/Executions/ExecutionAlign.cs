using System.Numerics;

namespace SmSim.Extras.Executions;

/// <summary>Why a variant (or every variant) of an execution did not align (<see cref="ExecutionAlign"/>).</summary>
public enum ExecutionAlignFailure : byte
{
    None = 0,
    /// <summary>No variant was given (no row for this attacker, weapon and victim).</summary>
    NoVariant = 1,
    /// <summary>The attacker would slide further than <see cref="ExecutionAlign.MaxSlide"/> toward the victim (or <see cref="ExecutionAlign.MaxBackSlide"/> away from it).</summary>
    TooFar = 2,
    /// <summary>A wall or prop stands in the slide or in a clip's root path.</summary>
    Blocked = 3,
    /// <summary>No ground within reach below the slide or a root path (a gap, a ledge, the slide fell).</summary>
    Gap = 4,
    /// <summary>A rise or drop higher than the step height between two probes, or between the pair.</summary>
    Step = 5,
    /// <summary>Ground steeper than the walkable slope.</summary>
    Slope = 6,
    /// <summary>The slide or a root path touches a kill volume.</summary>
    KillVolume = 7,
    /// <summary>The aligned attacker does not see the victim through the level (<see cref="MeleeLock.Sees"/>).</summary>
    NoSight = 8,
    /// <summary>A spawn-room blocker that stops the character's side stands on the slide or a root path.</summary>
    SpawnBlocker = 9,
    /// <summary>The attacker is not on the ground (in a jump, a fall or a hop).</summary>
    NotGrounded = 10,
    /// <summary>Another character stands on the slide (the slide never pushes through or past a bystander).</summary>
    Bystander = 11,
}

/// <summary>
/// The outcome of <see cref="ExecutionAlign.Align"/>: on success the move chosen (<see cref="Variant"/>, index into the list given, and its <see cref="Row"/>),
/// where the attacker stands after its slide (<see cref="Attacker"/>, its yaw the pair's), the forward distance its blend plays at (<see cref="Distance"/>;
/// <see cref="ExecutionRow.Blend"/> gives the variants and weight) and the victim's place and yaw for the clip (<see cref="VictimAt"/>, <see cref="VictimYaw"/>;
/// its feet stay where they were). <see cref="Tried"/> is every variant's outcome in order (<see cref="ExecutionAlignFailure.None"/> for those that fit). On
/// failure <see cref="Failure"/> is the first variant's reason and the characters are untouched (<see cref="Attacker"/> is the attacker as given).
/// </summary>
public sealed record ExecutionAlignResult(bool Ok, ExecutionAlignFailure Failure, int Variant, ExecutionRow? Row, PlayerState Attacker, Vector3 VictimAt, float VictimYaw,
    float Distance, float Slide, int SlideTicks, IReadOnlyList<ExecutionAlignFailure> Tried)
{
    /// <summary>Classic: the Chaos Marine rows play on a loyalist victim with the races swapped (<see cref="ExecutionTargets.ClassicRaceSwap"/>; ours).</summary>
    public bool RaceSwapped { get; init; }
}

/// <summary>
/// Executions X2 (design "Simulation &gt; Alignment", inventory docs/executions-coverage.md 1.7 and 4.5; the numbers here are <b>ours</b>, spec/behavior.md
/// "Executions: alignment"): whether a paired execution can be played where the two characters stand, and from where. As in the original, the victim's feet never
/// move (a corpse or an Ork is never dragged; only its yaw turns to the pair's) and the attacker's distance variants cover the gap: the attacker turns so the
/// row's victim offset points at the victim, and the forward distance is clamped to the row's <see cref="ExecutionRow.DistanceBand"/>. Only when the distance
/// lies outside the band does the attacker slide along that line by <see cref="Movement.Glide"/> (the capsule controller, against the level), at most
/// <see cref="MaxSlide"/> toward the victim and at most <see cref="MaxBackSlide"/> away from it (a victim nearer than the band's shortest gap). Then the slide and both clips' root paths (the attacker's blended at the distance and placed from the aligned attacker, the victim's
/// from the victim) are probed every <see cref="ProbeSpacing"/>: downward for ground (a miss is a gap; a rise or drop above the step height between probes is a
/// step; ground steeper than the walkable slope fails), two capsule-radius spheres swept between probes (just above the step height and just under the capsule's
/// top, so together they cover the body from the step height up) for walls, low walls and overhangs, the kill volumes along the path and the
/// spawn-room blockers of the character's side; and the attacker must see the victim (<see cref="MeleeLock.Sees"/>). Every variant is checked in the order
/// given; among those that pass a seeded pick (<see cref="ExecutionChoice.Seed"/>) chooses, else the first; none: a clean failure and nothing moves. Pure.
/// </summary>
public static class ExecutionAlign
{
    /// <summary>Raised when the rules or numbers below change (for X1's hashed Executions block).</summary>
    public const int RulesVersion = 2;
    /// <summary>Longest slide of the attacker toward the victim into the row's distance band (metres, horizontal; ours, design "at most 1 m").</summary>
    public const float MaxSlide = 1f;
    /// <summary>
    /// Longest slide of the attacker away from the victim, when the victim is nearer than the row's shortest variant gap (metres, horizontal; ours, lead
    /// decision 2026-11-25, X7 review): without it a row whose shortest gap is over 1 m (the Bloodletter slam's 2.80 m, the tongue grab's 2.50 m, the axe
    /// hack's 3.37 m) could start only in a ring just inside <see cref="ExecutionChoice.WarfrontRange"/>, and the prompt would vanish as the player walked in.
    /// The backward slide covers up to the row's needed gap, capped here; the slide is checked as any other (capsule glide, probes, bystanders).
    /// </summary>
    public const float MaxBackSlide = 2.5f;
    /// <summary>Speed of the slide (m/s; ours: 1 m in 5 ticks at 30 Hz) and how near the place the slide must end (metres).</summary>
    public const float SlideSpeed = 6f, SlideTolerance = 0.02f;
    /// <summary>Distance between probes along the slide and the root paths (metres; ours, design "every 0.25 m").</summary>
    public const float ProbeSpacing = 0.25f;
    /// <summary>The downward probe starts this far above the previous ground (metres; ours: above the step height, so a rise up to it is seen as ground).</summary>
    public const float ProbeLift = 1f;
    /// <summary>
    /// The wall sweeps: spheres of the capsule radius (<see cref="SimConfig.CapsuleRadius"/>, 0.45 m) centred <see cref="SimConfig.StepHeight"/> + r and
    /// <see cref="SimConfig.CapsuleHeight"/> − r above the probed ground (0.85 and 1.45 m: from 0.4 m to the capsule's 1.9 m top; ours, review of 476eb485).
    /// </summary>
    public static (float Radius, float Low, float High) Sweeps(SimConfig c)
    {
        float r = MathF.Max(c.CapsuleRadius, 0.05f);
        float low = c.StepHeight + r, high = MathF.Max(low, c.CapsuleHeight - r);
        return (r, low, high);
    }
    /// <summary>Most probes along one path (a bound for corrupt root paths; 1 m of slide and a few metres of root motion need far fewer).</summary>
    public const int MaxProbes = 512;

    /// <summary>
    /// Checks <paramref name="variants"/> in order for <paramref name="attacker"/> executing <paramref name="victim"/> (a player or a corpse; an Ork through
    /// <see cref="ExecutionChoice.StandIn"/>) against <paramref name="world"/> (null: no level, the slide limit and the height check alone), and picks among those
    /// that fit with <paramref name="seed"/> (<see cref="ExecutionChoice.Seed"/>; null: the first that fits). <paramref name="bystanders"/>: the feet of every
    /// other living character (not the pair); the slide fails when it would come within two capsule radii of one (null: none checked). The attacker must be
    /// grounded. <see cref="PlayerCollision.LastPush"/> is left as it was. <paramref name="maxSlide"/>: the forward slide's cap (null: <see cref="MaxSlide"/>; Classic passes <see cref="ExecutionRules.ClassicMaxSlide"/>, X3).
    /// </summary>
    public static ExecutionAlignResult Align(in PlayerState attacker, in PlayerState victim, IReadOnlyList<ExecutionRow> variants, SimConfig c, CollisionWorld? world,
        ulong? seed = null, IReadOnlyList<Vector3>? bystanders = null, float? maxSlide = null) =>
        Check(attacker, victim, variants, c, world, seed, bystanders, firstFit: false, maxSlide);

    /// <summary>
    /// <see cref="Align"/>; with <paramref name="firstFit"/> (and no seed) it stops at the first variant that fits: the same result, with
    /// <see cref="ExecutionAlignResult.Tried"/> cut after it (the prompt's mode, <see cref="ExecutionChoice.ChooseAlignedOrk"/>).
    /// </summary>
    internal static ExecutionAlignResult Check(in PlayerState attacker, in PlayerState victim, IReadOnlyList<ExecutionRow> variants, SimConfig c, CollisionWorld? world,
        ulong? seed, IReadOnlyList<Vector3>? bystanders, bool firstFit, float? maxSlide = null)
    {
        firstFit &= seed is null;
        var tried = new ExecutionAlignFailure[variants.Count];
        var passed = new List<ExecutionAlignResult>();
        var push = PlayerCollision.LastPush;
        for (int i = 0; i < variants.Count; i++)
        {
            var r = Try(attacker, victim, variants[i], c, world, bystanders, maxSlide ?? MaxSlide);
            PlayerCollision.LastPush = push;
            tried[i] = r.Failure;
            if (r.Ok)
            {
                passed.Add(r with { Variant = i });
                if (firstFit) { Array.Resize(ref tried, i + 1); break; }
            }
        }
        if (passed.Count > 0) return passed[seed is { } s ? (int)(s % (ulong)passed.Count) : 0] with { Tried = tried };
        return new ExecutionAlignResult(false, tried.Length == 0 ? ExecutionAlignFailure.NoVariant : tried[0], -1, null, attacker, victim.Position, victim.Yaw, 0f, 0f, 0, tried);
    }

    /// <summary>The yaw at which the point <paramref name="local"/> (attacker frame) points from <paramref name="attacker"/> at <paramref name="victim"/>.</summary>
    public static float PairYaw(Vector3 attacker, Vector3 victim, Vector2 local, float fallbackYaw)
    {
        var to = new Vector2(victim.X - attacker.X, victim.Y - attacker.Y);
        if (local.LengthSquared() < 1e-8f || to.LengthSquared() < 1e-8f) return fallbackYaw;
        return Wrap(MathF.Atan2(to.Y, to.X) - MathF.Atan2(local.Y, local.X));
    }

    /// <summary>A point given in a character's frame (X right, Y forward) at <paramref name="yaw"/> in the world (yaw 0 faces +Y, positive turns left).</summary>
    public static Vector2 ToWorld(Vector2 local, float yaw)
    {
        float cs = MathF.Cos(yaw), sn = MathF.Sin(yaw);
        return new Vector2(local.X * cs - local.Y * sn, local.X * sn + local.Y * cs);
    }

    /// <summary>The angle in (-π, π].</summary>
    public static float Wrap(float a)
    {
        if (!float.IsFinite(a)) return 0f;
        a = MathF.IEEERemainder(a, 2f * MathF.PI);
        return a <= -MathF.PI ? a + 2f * MathF.PI : a;
    }

    static ExecutionAlignResult Fail(ExecutionAlignFailure why, in PlayerState attacker, in PlayerState victim) =>
        new(false, why, -1, null, attacker, victim.Position, victim.Yaw, 0f, 0f, 0, []);

    static ExecutionAlignResult Try(in PlayerState attacker, in PlayerState victim, ExecutionRow row, SimConfig c, CollisionWorld? world, IReadOnlyList<Vector3>? bystanders, float maxSlide)
    {
        if (!row.IsValid) return Fail(ExecutionAlignFailure.NoVariant, attacker, victim);
        if (!attacker.Grounded) return Fail(ExecutionAlignFailure.NotGrounded, attacker, victim);
        // The forward distance the pair stands at once the attacker faces along the row's offset, clamped to the band its variants cover.
        float side = row.Offset.X;
        float gap = new Vector2(victim.Position.X - attacker.Position.X, victim.Position.Y - attacker.Position.Y).Length();
        var (min, max) = row.DistanceBand;
        float distance = Math.Clamp(MathF.Sqrt(MathF.Max(gap * gap - side * side, 0f)), min, max);
        var local = new Vector2(side, distance);
        float yaw = PairYaw(attacker.Position, victim.Position, local, attacker.Yaw);
        var place = new Vector2(victim.Position.X, victim.Position.Y) - ToWorld(local, yaw);
        var start = new Vector2(attacker.Position.X, attacker.Position.Y);
        float slide = Vector2.Distance(place, start);
        bool back = local.Length() > gap;   // the attacker steps away from a victim nearer than the band
        if (!float.IsFinite(slide) || slide > (back ? MaxBackSlide : maxSlide) + 1e-4f) return Fail(ExecutionAlignFailure.TooFar, attacker, victim);
        if (MathF.Abs(victim.Position.Z - attacker.Position.Z) > c.StepHeight + 1e-3f) return Fail(ExecutionAlignFailure.Step, attacker, victim);

        // The slide, tick by tick, with the capsule controller (other players ignored, as the charge lock's glide).
        var s = attacker with { Yaw = yaw, Velocity = Vector3.Zero };
        int ticks = 0, most = (int)MathF.Ceiling(MathF.Max(maxSlide, MaxBackSlide) / (SlideSpeed * c.TickSeconds)) + 2;
        using (PlayerCollision.Use(null))
            while (Vector2.Distance(new Vector2(s.Position.X, s.Position.Y), place) > SlideTolerance && ticks < most)
            {
                var prev = s;
                var remaining = place - new Vector2(s.Position.X, s.Position.Y);
                float want = remaining.Length() / c.TickSeconds;
                var v = want > SlideSpeed ? remaining / remaining.Length() * SlideSpeed : remaining / c.TickSeconds;
                s = Movement.Glide(s, yaw, v, c, world) with { Velocity = Vector3.Zero };
                ticks++;
                if (world is not null && world.KillsPlayer(prev.Position, s.Position, c.CapsuleHeight)) return Fail(ExecutionAlignFailure.KillVolume, attacker, victim);
                if (!s.Grounded) return Fail(ExecutionAlignFailure.Gap, attacker, victim);
                if (MathF.Abs(s.Position.Z - prev.Position.Z) > c.StepHeight + 1e-3f) return Fail(ExecutionAlignFailure.Step, attacker, victim);
                if (Vector2.Distance(new Vector2(s.Position.X, s.Position.Y), new Vector2(prev.Position.X, prev.Position.Y)) < 1e-4f) break;
            }
        if (Vector2.Distance(new Vector2(s.Position.X, s.Position.Y), place) > SlideTolerance) return Fail(ExecutionAlignFailure.Blocked, attacker, victim);
        s = s with { Yaw = yaw };
        if (MathF.Abs(victim.Position.Z - s.Position.Z) > c.StepHeight + 1e-3f) return Fail(ExecutionAlignFailure.Step, attacker, victim);   // the pair once slid
        if (bystanders is not null && ticks > 0)
        {
            float clear = 2f * MathF.Max(c.CapsuleRadius, 0.05f);
            var a = new Vector2(attacker.Position.X, attacker.Position.Y);
            var ab = new Vector2(s.Position.X, s.Position.Y) - a;
            float len2 = ab.LengthSquared();
            foreach (var b in bystanders)
            {
                if (MathF.Abs(b.Z - s.Position.Z) > c.CapsuleHeight) continue;
                var p = new Vector2(b.X, b.Y);
                float t = len2 > 1e-8f ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
                float nearest = Vector2.Distance(a + ab * t, p);   // a bystander the slide closes on (one already beside the start and left behind does not count)
                if (nearest < clear && nearest < Vector2.Distance(a, p) - 1e-4f) return Fail(ExecutionAlignFailure.Bystander, attacker, victim);
            }
        }

        float victimYaw = Wrap(yaw + row.RelativeYaw);
        if (world is not null)
        {
            var why = Probe([attacker.Position, s.Position], attacker.Team, c, world);
            if (why == ExecutionAlignFailure.None) why = Probe(RootPath(s.Position, yaw, row.AttackerRootAt(distance)), attacker.Team, c, world);
            if (why == ExecutionAlignFailure.None) why = Probe(RootPath(victim.Position, victimYaw, row.VictimRoot), victim.Team, c, world);
            if (why != ExecutionAlignFailure.None) return Fail(why, attacker, victim);
        }
        if (!MeleeLock.Sees(s, victim, c, world)) return Fail(ExecutionAlignFailure.NoSight, attacker, victim);
        return new ExecutionAlignResult(true, ExecutionAlignFailure.None, 0, row, s, victim.Position, victimYaw, distance, slide, ticks, []);
    }

    /// <summary>A clip's root path placed in the world from <paramref name="origin"/> (feet) at <paramref name="yaw"/>: the origin, then each sample (heights from the origin's).</summary>
    public static Vector3[] RootPath(Vector3 origin, float yaw, IReadOnlyList<Vector4> root)
    {
        var path = new Vector3[root.Count + 1];
        path[0] = origin;
        for (int i = 0; i < root.Count; i++)
        {
            var w = ToWorld(new Vector2(root[i].X, root[i].Y), yaw);
            path[i + 1] = new Vector3(origin.X + w.X, origin.Y + w.Y, origin.Z + root[i].Z);
        }
        return path;
    }

    /// <summary>
    /// Probes the feet's path <paramref name="path"/> (polyline) for a character of <paramref name="side"/>: every <see cref="ProbeSpacing"/>, ground below
    /// (from <see cref="ProbeLift"/> above the previous ground, at most the step height below it), the slope there, the two wall sweeps from the previous probe (<see cref="Sweeps"/>), the
    /// kill volumes and the spawn-room blockers. <see cref="ExecutionAlignFailure.None"/> when clear.
    /// </summary>
    public static ExecutionAlignFailure Probe(IReadOnlyList<Vector3> path, Team side, SimConfig c, CollisionWorld world)
    {
        if (path.Count == 0) return ExecutionAlignFailure.None;
        var mesh = world.CharacterFor(side);
        float minNormalZ = MathF.Cos(Math.Clamp(c.MaxWalkableSlope, 0f, 90f) * MathF.PI / 180f) - 1e-4f;
        var sweep = Sweeps(c);
        Span<float> sweepHeights = [sweep.Low, sweep.High];
        float ground = path[0].Z;
        Vector3? last = null;
        int probes = 0;
        for (int k = 0; k < path.Count; k++)
        {
            var a = k == 0 ? path[0] : path[k - 1];
            var b = path[k];
            float length = new Vector2(b.X - a.X, b.Y - a.Y).Length();
            int n = k == 0 ? 1 : Math.Max(1, (int)MathF.Ceiling(length / ProbeSpacing));
            for (int j = k == 0 ? 0 : 1; j <= n; j++)
            {
                if (++probes > MaxProbes) return ExecutionAlignFailure.Blocked;
                var p = k == 0 ? a : Vector3.Lerp(a, b, j / (float)n);
                var from = new Vector3(p.X, p.Y, ground + ProbeLift);
                if (mesh.Raycast(from, -Vector3.UnitZ, ProbeLift + c.StepHeight + 1e-3f) is not { } hit) return ExecutionAlignFailure.Gap;
                float z = from.Z - hit.Distance;
                if (MathF.Abs(z - ground) > c.StepHeight + 1e-3f) return ExecutionAlignFailure.Step;
                if (MathF.Abs(hit.Normal.Z) < minNormalZ) return ExecutionAlignFailure.Slope;
                var feet = new Vector3(p.X, p.Y, z);
                if (last is { } prev)
                {
                    foreach (float h in sweepHeights)
                    {
                        var w0 = prev + new Vector3(0f, 0f, h);
                        var d = feet + new Vector3(0f, 0f, h) - w0;
                        float len = d.Length();
                        if (len > 1e-4f && mesh.SweepSphere(w0, d / len, sweep.Radius, len) is { } wall && wall.Distance < len) return ExecutionAlignFailure.Blocked;
                    }
                    if (world.KillsPlayer(prev, feet, c.CapsuleHeight)) return ExecutionAlignFailure.KillVolume;
                }
                else if (world.KillsPlayer(feet, feet, c.CapsuleHeight)) return ExecutionAlignFailure.KillVolume;
                foreach (var blocker in world.Blockers)
                    if (blocker.Blocks(side) && blocker.Contains(feet + new Vector3(0f, 0f, 1f))) return ExecutionAlignFailure.SpawnBlocker;
                ground = z;
                last = feet;
            }
        }
        return ExecutionAlignFailure.None;
    }
}
