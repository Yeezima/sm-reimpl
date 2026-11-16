using System.Numerics;

namespace SmContent;

// The jump-pack death in mid-air (2026-11-11; client presentation only: no simulation, wire, protocol or config-hash change). Read from SM_Base_MP's "Jumppack Death" (`smtool graph-dump --upper`,
// `graph-dump SM_Base_MP`): a ModifierGenerator with "Apply Gravity" over the machine "Jumppack Death Behavior": "Jumppack Death Start" (SM_REC_MP_MidAirDeath_Hit_01, 0.6 s, StartAction at 0.0333,
// BRANCH_Jump_Death_Start at 0.9412 into the fall), "Jumppack Death Fall" (SM_REC_MP_MidAirDeath_Fall_01 looping at speed 0.65 under a blender at weight 1, FloorCollisionTest 0 to 1; out on
// BOOL_IsSupported, priority 2, or BOOL_Floor_Hit, 1, with no blend effect) and "Jumppack Death Hit Floor" (SM_REC_MP_MidAirImpact_01, 0.833 s, StartAction at 0.04, the powered ragdoll from 0.44).
// Engine-free (the client places and poses the body; tests on synthetic graphs and levels).

/// <summary>The states of the graph's jump death the body is in, then the ragdoll.</summary>
public enum JumpDeathPhase { Hit, Fall, Impact, Ragdoll }

/// <summary>Why the jump death handed the body to the ragdoll: at the floor clip's tag, at the fall's cap (no floor), or cancelled by the client (the kill was a fall out of the level).</summary>
public enum JumpDeathEnd { None, Floor, Cap, Cancelled }

/// <summary>What one <see cref="JumpDeathRun.Step"/> saw happen.</summary>
[Flags]
public enum JumpDeathEvents { None = 0, Fall = 1, Landed = 2, Impact = 4, ImpactAction = 8, Ragdoll = 16, FloorHit = 32 }

/// <summary>The level as the falling body sees it (game space, metres, z up): the distance along the unit <paramref name="direction"/> to the first surface within <paramref name="maxDistance"/>, else null.</summary>
public delegate float? JumpDeathRay(Vector3 origin, Vector3 direction, float maxDistance);

/// <summary>
/// The rules of the jump-pack death in mid-air. Data: the clips, the branch into the fall, the floor tests that end it, the floor clip's powered-ragdoll tag (0.44 of 0.833 s = 0.367 s) and the
/// <c>StartAction</c> tags (the start's at 0.0333 of 0.6 s = 0.02 s: <c>actn_jumpdeath_start</c>; the floor's at 0.04 of 0.833 s = 0.033 s: <c>actn_jumpdeath_impact</c>, docs/discovery.md "Death
/// effects"). Ours: the body's fall (the world gravity, the walls and ceilings, the floor test by a ray), the cap, the slide once on the floor and the floor reached before the branch.
/// </summary>
public static class JumpDeathRules
{
    /// <summary>
    /// The longest seconds from the death to the floor (ours): a body that has not reached a floor by then (killed over a pit or a kill volume, where the fall would never end) goes to the ragdoll where
    /// it is, with its fall's velocity, and no impact plays. The same 4 s the impact effect waited for a ragdoll's floor before (<see cref="DeathEffectRules.JumpImpactLimit"/>); the longest fall on
    /// Manufactorum (its 33 m, under the 20 m/s² gravity, from a rise) takes under 3 s.
    /// </summary>
    public const float FallCap = DeathEffectRules.JumpImpactLimit;
    /// <summary>The floor ray starts this far above the feet (m; the held death's slide's).</summary>
    public const float FootLift = 0.6f;
    /// <summary>The ceiling ray starts this far above the feet (m; ours, a marine's head).</summary>
    public const float BodyTop = 1.8f;
    /// <summary>The heights (m above the feet) of the rays that stop the body's motion along the level (foot, middle and head height, as the held death's slide).</summary>
    public static readonly float[] WallHeights = [0.3f, 0.9f, 1.6f];
    /// <summary>A wall nearer than this past the step (m) stops the motion along the level (the slide's 0.45, about the capsule's radius).</summary>
    public const float WallMargin = 0.45f;
    /// <summary>The time constant (s) of the slide once the body is on the floor (the held death's slide: <c>NetworkView.DeathSlideSeconds</c>).</summary>
    public const float SlideSeconds = 0.2f;
    /// <summary>The longest sub-step (s) of the body's motion and the longest frame it advances by (a hitch counts as this much).</summary>
    public const float MaxStep = 1f / 120f, MaxFrame = 0.25f;

    /// <summary>
    /// Whether a death plays the jump death: the victim was in a jump-pack jump at its last living frame (the graph's <c>BOOL_JumpIsActive</c>: flight or dive, not the take-off), the kill was not a fall
    /// out of the level, and the three clips were read. Else the death goes on as before (a victim on the ground: its graph route; in the air without a jump: the ragdoll at once).
    /// </summary>
    public static bool Applies(bool jumpActive, bool fellOutOfLevel, DeathAnimations? data) =>
        jumpActive && !fellOutOfLevel && data is not null
        && data.Get(MarineDeathKind.JumpStart) is not null && data.Get(MarineDeathKind.JumpFall) is not null && data.Get(MarineDeathKind.JumpImpact) is not null;

    /// <summary>Seconds into the floor clip at which its <c>StartAction</c> fires (the impact's effects and sound): the tag's time, else 1/30 s (where every death state's tag lands).</summary>
    public static float ImpactActionAt(DeathClip impact) => float.IsFinite(impact.ActionAt) && impact.ActionAt >= 0f ? impact.ActionAt : DeathEffectRules.StartActionDelay;
}

/// <summary>
/// One jump-pack death in mid-air: the body plays the hit clip (it falls meanwhile: the state's "Apply Gravity"), the fall loop until it meets a floor, then the floor clip, and the ragdoll takes it at the
/// floor clip's tag; or at <see cref="JumpDeathRules.FallCap"/> when no floor came. The body starts with the victim's last velocity and falls under <paramref name="gravity"/> (the world's: the pack's
/// thrust ends with the death), stopped along the level by walls and upward by ceilings; on the floor it slides on and slows (as a death on the ground). The killing blow does not push it (the data's
/// reactions have no knockback; ours). The fall ends at the first of the graph's two floor tests: <c>BOOL_Floor_Hit</c>, read as the posed body's lowest point reaching the floor (the fall clip carries a
/// <c>FloorCollisionTest</c> tag; the clips hang the hands about 0.5 m below the feet, and the floor clip lifts every bone above them within 0.1 s), and <c>BOOL_IsSupported</c>, the feet on the floor;
/// the body falls on in the floor clip until its feet are on the floor (the state's gravity).
/// </summary>
public sealed class JumpDeathRun
{
    public JumpDeathRun(DeathClip hit, DeathClip fall, DeathClip impact, Vector3 feet, Vector3 velocity, float gravity, float maxFallSpeed, float cap = JumpDeathRules.FallCap)
    {
        Hit = hit; Fall = fall; Impact = impact;
        Feet = feet;
        _startZ = feet.Z;
        Velocity = float.IsFinite(velocity.X) && float.IsFinite(velocity.Y) && float.IsFinite(velocity.Z) ? velocity : Vector3.Zero;
        Gravity = float.IsFinite(gravity) ? MathF.Max(gravity, 0f) : 0f;
        MaxFallSpeed = maxFallSpeed > 0f && float.IsFinite(maxFallSpeed) ? maxFallSpeed : float.PositiveInfinity;
        Cap = float.IsFinite(cap) ? MathF.Max(cap, 0f) : JumpDeathRules.FallCap;
    }

    public DeathClip Hit { get; }
    public DeathClip Fall { get; }
    public DeathClip Impact { get; }
    public float Gravity { get; }
    public float MaxFallSpeed { get; }
    public float Cap { get; }

    public JumpDeathPhase Phase { get; private set; } = JumpDeathPhase.Hit;
    public JumpDeathEnd End { get; private set; }
    /// <summary>Seconds since the death.</summary>
    public float Age { get; private set; }
    /// <summary><see cref="Age"/> at which the current phase began.</summary>
    public float PhaseStart { get; private set; }
    public float PhaseAge => Age - PhaseStart;
    /// <summary>The body's feet (game space).</summary>
    public Vector3 Feet { get; private set; }
    /// <summary>The body's velocity (game space): its fall in the air; on the floor the slide (along the level only).</summary>
    public Vector3 Velocity { get; private set; }
    /// <summary>The body stands on a floor (the graph's <c>BOOL_IsSupported</c>).</summary>
    public bool Supported { get; private set; }
    /// <summary><see cref="Age"/> at which the body reached the floor (NaN while in the air), at which the fall loop began and at which the floor clip began.</summary>
    public float LandedAt { get; private set; } = float.NaN;
    public float FallStartedAt { get; private set; } = float.NaN;
    public float ImpactStartedAt { get; private set; } = float.NaN;
    /// <summary>Seconds the fall loop played (to the floor, or so far).</summary>
    public float FallSeconds => float.IsNaN(FallStartedAt) ? 0f : (float.IsNaN(ImpactStartedAt) ? Age : ImpactStartedAt) - FallStartedAt;
    /// <summary>Whether the floor clip's <c>StartAction</c> (the impact's effects) has fired.</summary>
    public bool ImpactActionFired { get; private set; }
    /// <summary>The metres the body fell from the death to the floor (or so far).</summary>
    public float Dropped => _startZ - Feet.Z;
    /// <summary>The posed body's lowest point met the floor (the graph's <c>BOOL_Floor_Hit</c>), and when (<see cref="Age"/>; NaN before).</summary>
    public bool FloorHit { get; private set; }
    public float FloorHitAt { get; private set; } = float.NaN;
    /// <summary>The first floor test that held: the body's lowest point or its feet on the floor (NaN before).</summary>
    public float TouchedAt => float.IsNaN(FloorHitAt) ? LandedAt : float.IsNaN(LandedAt) ? FloorHitAt : MathF.Min(FloorHitAt, LandedAt);

    readonly float _startZ;
    float _bottom;
    JumpDeathPhase? _shown;

    /// <summary>The most a posed body's lowest point may count below its feet (m; a sanity bound on what the client measures).</summary>
    public const float MaxBottom = 1.5f;

    /// <summary>
    /// Advances the death by <paramref name="dt"/> seconds (at most <see cref="JumpDeathRules.MaxFrame"/>, in sub-steps of <see cref="JumpDeathRules.MaxStep"/>) against the level <paramref name="ray"/> (null:
    /// no level, nothing to land on). <paramref name="bottom"/>: the shown body's lowest point relative to its feet (m, negative below them; the client measures the posed bones), for the floor hit.
    /// </summary>
    public JumpDeathEvents Step(float dt, JumpDeathRay? ray, float bottom = 0f)
    {
        if (Phase == JumpDeathPhase.Ragdoll) return JumpDeathEvents.None;
        _bottom = float.IsFinite(bottom) ? Math.Clamp(bottom, -MaxBottom, 0f) : 0f;
        if (!(dt > 0f) || !float.IsFinite(dt)) return Transitions();
        dt = MathF.Min(dt, JumpDeathRules.MaxFrame);
        var events = JumpDeathEvents.None;
        while (dt > 1e-6f && Phase != JumpDeathPhase.Ragdoll)
        {
            float h = MathF.Min(dt, JumpDeathRules.MaxStep);
            dt -= h;
            events |= Move(h, ray);
            Age += h;
            events |= Transitions();
        }
        return events;
    }

    /// <summary>The client ends the death early (the kill turned out to be a fall out of the level): the ragdoll takes the body now.</summary>
    public void Cancel()
    {
        if (Phase == JumpDeathPhase.Ragdoll) return;
        Phase = JumpDeathPhase.Ragdoll;
        End = JumpDeathEnd.Cancelled;
    }

    /// <summary>
    /// The clip to show and its playback seconds (0 to its <see cref="DeathClip.Length"/>; the fall loops: modulo its length), and whether the phase is shown for the first time (the client blends into it
    /// over the clip's own blend then, and not when the loop wraps).
    /// </summary>
    public (DeathClip Clip, float Playback, bool Entered) Shown()
    {
        var clip = Phase switch { JumpDeathPhase.Hit => Hit, JumpDeathPhase.Fall => Fall, _ => Impact };
        float t = MathF.Max(PhaseAge, 0f);
        t = Phase == JumpDeathPhase.Fall && clip.Length > 1e-3f ? t % clip.Length : MathF.Min(t, MathF.Max(clip.Length, 0f));
        bool entered = _shown != Phase;
        _shown = Phase;
        return (clip, t, entered);
    }

    JumpDeathEvents Transitions()
    {
        var events = JumpDeathEvents.None;
        const float eps = 1e-5f;
        bool touched = Supported || FloorHit;
        // "Jumppack Death Start" --branch BRANCH_Jump_Death_Start--> the fall; a body already on the floor goes on to the floor clip at once (the fall's floor test holds; ours: no frame of the loop).
        if (Phase == JumpDeathPhase.Hit && PhaseAge >= Hit.RagdollAt - eps)
        {
            float at = PhaseStart + MathF.Max(Hit.RagdollAt, 0f);
            if (touched) { Enter(JumpDeathPhase.Impact, MathF.Max(at, TouchedAt)); events |= JumpDeathEvents.Impact; }
            else { Enter(JumpDeathPhase.Fall, at); FallStartedAt = at; events |= JumpDeathEvents.Fall; }
        }
        // "Jumppack Death Fall" --if BOOL_IsSupported / BOOL_Floor_Hit--> "Jumppack Death Hit Floor".
        if (Phase == JumpDeathPhase.Fall && touched) { Enter(JumpDeathPhase.Impact, TouchedAt); events |= JumpDeathEvents.Impact; }
        if (Phase == JumpDeathPhase.Impact)
        {
            if (!ImpactActionFired && (PhaseAge >= JumpDeathRules.ImpactActionAt(Impact) - eps || PhaseAge >= Impact.RagdollAt - eps)) { ImpactActionFired = true; events |= JumpDeathEvents.ImpactAction; }
            if (PhaseAge >= Impact.RagdollAt - eps) { Phase = JumpDeathPhase.Ragdoll; End = JumpDeathEnd.Floor; events |= JumpDeathEvents.Ragdoll; }
        }
        else if (Age >= Cap - eps) { Phase = JumpDeathPhase.Ragdoll; End = JumpDeathEnd.Cap; events |= JumpDeathEvents.Ragdoll; }
        return events;
    }

    void Enter(JumpDeathPhase phase, float at)
    {
        Phase = phase;
        PhaseStart = float.IsFinite(at) ? MathF.Min(at, Age) : Age;
        if (phase == JumpDeathPhase.Impact) ImpactStartedAt = PhaseStart;
    }

    static readonly Vector3 Up = Vector3.UnitZ;

    /// <summary>One sub-step of the body's motion: the fall (gravity, walls, ceiling, the floor test) or, on the floor, the slide.</summary>
    JumpDeathEvents Move(float h, JumpDeathRay? ray)
    {
        if (Supported) { Slide(h, ray); return JumpDeathEvents.None; }
        var v = Velocity;
        v.Z = MathF.Max(v.Z - Gravity * h, -MaxFallSpeed);
        var step = v * h;
        var feet = Feet;
        if (ray is not null)
        {
            var flat = new Vector3(step.X, step.Y, 0f);
            float length = flat.Length();
            if (length > 1e-6f)
            {
                var dir = flat / length;
                foreach (float height in JumpDeathRules.WallHeights)
                    if (ray(feet + Up * height, dir, length + JumpDeathRules.WallMargin) is not null) { v.X = 0f; v.Y = 0f; step.X = 0f; step.Y = 0f; break; }
            }
            if (step.Z > 0f && ray(feet + Up * JumpDeathRules.BodyTop, Up, step.Z) is not null) { v.Z = 0f; step.Z = 0f; }
            if (step.Z <= 0f)
            {
                var origin = new Vector3(feet.X + step.X, feet.Y + step.Y, feet.Z + JumpDeathRules.FootLift);
                if (ray(origin, -Up, JumpDeathRules.FootLift - step.Z - _bottom) is { } d)
                {
                    var events = JumpDeathEvents.None;
                    if (!FloorHit) { FloorHit = true; FloorHitAt = Age + h; events |= JumpDeathEvents.FloorHit; }   // the lowest point (or the feet) at the floor
                    if (d <= JumpDeathRules.FootLift - step.Z)
                    {
                        Feet = origin - Up * d;
                        Velocity = new Vector3(v.X, v.Y, 0f);
                        Supported = true;
                        LandedAt = Age + h;
                        return events | JumpDeathEvents.Landed;
                    }
                    Feet = feet + step;
                    Velocity = v;
                    return events;
                }
            }
        }
        Feet = feet + step;
        Velocity = v;
        return JumpDeathEvents.None;
    }

    /// <summary>On the floor: the body slides on along the level and slows (time constant <see cref="JumpDeathRules.SlideSeconds"/>), stopped by a wall and at an edge (it does not slide over a drop).</summary>
    void Slide(float h, JumpDeathRay? ray)
    {
        var flat = new Vector3(Velocity.X, Velocity.Y, 0f) * MathF.Exp(-h / JumpDeathRules.SlideSeconds);
        if (flat.Length() < 0.05f) { Velocity = Vector3.Zero; return; }
        var step = flat * h;
        if (ray is not null)
        {
            float length = step.Length();
            var dir = step / length;
            foreach (float height in JumpDeathRules.WallHeights)
                if (ray(Feet + Up * height, dir, length + JumpDeathRules.WallMargin) is not null) { Velocity = Vector3.Zero; return; }
            if (ray(new Vector3(Feet.X + step.X, Feet.Y + step.Y, Feet.Z + JumpDeathRules.FootLift), -Up, 2f * JumpDeathRules.FootLift) is not { } down) { Velocity = Vector3.Zero; return; }   // an edge: the body stays
            Feet = new Vector3(Feet.X + step.X, Feet.Y + step.Y, Feet.Z + JumpDeathRules.FootLift - down);
        }
        else Feet += step;
        Velocity = flat;
    }
}
