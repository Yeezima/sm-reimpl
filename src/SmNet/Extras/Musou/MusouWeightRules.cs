using System;
using System.Collections.Generic;
using Num = System.Numerics;

namespace SmNet.Extras.Musou;

/// <summary>The body parts whose ground contacts the Musou weight pass shows (MW1): the order of <see cref="MusouWeightRules.Parts"/> and of the bone samples.</summary>
public enum MusouContactPart : byte { LeftFoot, RightFoot, LeftKnee, RightKnee, LeftHand, RightHand, Pelvis, LeftShoulder, RightShoulder }

/// <summary>What a ground contact is: a footfall from a clip's foot tag, a plant (a stop, a melee step), a landing, a body part meeting the ground, or a slide along it.</summary>
public enum MusouContactKind : byte { Step, Plant, Landing, Touch, Skid }

/// <summary>One ground contact of a marine (MW1): the part, the kind, where (the client's world space), how strong (0 to 1) and the part's speed along the ground (m/s).</summary>
public readonly record struct MusouGroundContact(int PlayerId, bool Own, MusouContactPart Part, MusouContactKind Kind, Num.Vector3 At, float Strength, float GroundSpeed);

/// <summary>One bone sample of a frame: where the bone is (the client's world space, Y up) and how high above the ground under the character (m).</summary>
public readonly record struct MusouBoneSample(Num.Vector3 At, float Height);

/// <summary>
/// The Musou weight and impact pass (owner's brief of 2026-11-10, plan approved that day: docs/musou-weight-impact-plan.md), MW1: when a marine's feet or body meet the
/// ground. Two sources: the clips' own foot tags (run, sprint, stops, the melee combos' steps; <see cref="FromFootTag"/>) and, for clips without tags (rolls, landings,
/// knockdowns, get-ups), the bones against the ground under the character (<see cref="MusouContactTracker"/>). Every number here is ours (refine register), presentation
/// only: nothing reaches the simulation.
/// </summary>
public static class MusouWeightRules
{
    /// <summary>The rig bones sampled, in <see cref="MusouContactPart"/> order (the toe, the knee as the calf's root, the hand, the pelvis, the shoulder as the upper arm's root).</summary>
    public static readonly string[] Bones = ["RIG_L_Toe", "RIG_R_Toe", "RIG_L_Calf", "RIG_R_Calf", "RIG_L_Hand", "RIG_R_Hand", "RIG_Pelvis", "RIG_L_UpperArm", "RIG_R_UpperArm"];

    public const int Parts = 9;

    /// <summary>How far above the surface each bone's root sits when that part rests on it (m; ours, from the rig's proportions): toe, knee, hand, pelvis, shoulder.</summary>
    public static float Radius(MusouContactPart p) => p switch
    {
        MusouContactPart.LeftFoot or MusouContactPart.RightFoot => 0.03f,
        MusouContactPart.LeftKnee or MusouContactPart.RightKnee => 0.10f,
        MusouContactPart.LeftHand or MusouContactPart.RightHand => 0.06f,
        MusouContactPart.Pelvis => 0.18f,
        _ => 0.16f,
    };

    /// <summary>A part touches when its bone comes within this of its resting height (m; ours).</summary>
    public const float EnterMargin = 0.06f;
    /// <summary>A part has left the ground (and may touch again) once its bone is this far above its resting height (m; ours).</summary>
    public const float ExitMargin = 0.12f;
    /// <summary>A touch needs the part to come down at least this fast (m/s; ours): a bone resting near the ground is not a contact.</summary>
    public const float TouchSpeed = 0.25f;
    /// <summary>A touching part sliding along the ground faster than this makes a skid (m/s; the plan's 2 m/s).</summary>
    public const float SkidSpeed = 2f;
    /// <summary>A skid trail's spacing (s; the plan's 0.08 s).</summary>
    public const float SkidEvery = 0.08f;
    /// <summary>The same part touches at most once in this (s; the plan's per-foot 0.12 s).</summary>
    public const float PartRate = 0.12f;
    /// <summary>A landing needs this long in the air (s; ours): the ground's steps and the jump's first tick are not landings.</summary>
    public const float LandingAirSeconds = 0.15f;
    /// <summary>After a landing the bones' own touches wait this long (s; ours): the landing's puff stands for them.</summary>
    public const float AfterLandingQuiet = 0.25f;
    /// <summary>The fall speed at which a landing is at full strength (m/s; ours: the pack's full jump lands at 30.6 m/s, its dash at 9.8 m/s).</summary>
    public const float LandingFullSpeed = 14f;
    /// <summary>The most marines whose contacts are tracked: the own and the seven nearest (ours; Musou has at most 4 players and the AI Marines).</summary>
    public const int MaxMarines = 8;
    /// <summary>Nothing is tracked for a marine farther than this from the camera (m; the plan's 45 m).</summary>
    public const float Range = 45f;

    /// <summary>Whether a clip tag is a foot tag (<c>EVT_Left_Foot</c>, <c>EVT_Right_Foot</c>, their <c>_Rush</c> forms, <c>EVT_Bullrush_Stop_*</c>).</summary>
    public static bool IsFootTag(string tag) =>
        tag.StartsWith("evt_", StringComparison.OrdinalIgnoreCase)
        && (tag.Contains("_foot", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("evt_bullrush_stop", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A foot tag's contact (ours): which foot, the kind and the strength. A run step 0.35, a sprint step (<c>_Rush</c>) 0.55, a melee clip's step (<c>SM_MEL_*</c>) a plant
    /// at 0.6, a sprint stop (<c>EVT_Bullrush_Stop_*</c>) a plant at 0.8. Null for a tag that is not a foot tag.
    /// </summary>
    public static (MusouContactPart Part, MusouContactKind Kind, float Strength)? FromFootTag(string tag, string? clip)
    {
        if (!IsFootTag(tag)) return null;
        var part = tag.Contains("right", StringComparison.OrdinalIgnoreCase) ? MusouContactPart.RightFoot : MusouContactPart.LeftFoot;
        if (tag.StartsWith("evt_bullrush_stop", StringComparison.OrdinalIgnoreCase)) return (part, MusouContactKind.Plant, 0.8f);
        if (clip is not null && clip.StartsWith("SM_MEL", StringComparison.OrdinalIgnoreCase)) return (part, MusouContactKind.Plant, 0.6f);
        if (tag.EndsWith("_rush", StringComparison.OrdinalIgnoreCase)) return (part, MusouContactKind.Step, 0.55f);
        return (part, MusouContactKind.Step, 0.35f);
    }

    /// <summary>A landing's strength from the fall speed (m/s): a quarter at the least, full at <see cref="LandingFullSpeed"/> and above.</summary>
    public static float LandingStrength(float fallSpeed) => Math.Clamp(Math.Abs(fallSpeed) / LandingFullSpeed, 0.25f, 1f);

    /// <summary>A body part's touch strength from its downward speed (m/s): knees and hands up to 0.8, the pelvis and shoulders a fifth more (a body hitting the ground).</summary>
    public static float TouchStrength(MusouContactPart p, float downSpeed)
    {
        float s = Math.Clamp(Math.Abs(downSpeed) / 4f, 0.2f, 0.8f);
        return p is MusouContactPart.Pelvis or MusouContactPart.LeftShoulder or MusouContactPart.RightShoulder ? Math.Min(1f, s * 1.2f) : s;
    }

    /// <summary>A skid's strength from the part's speed along the ground (m/s).</summary>
    public static float SkidStrength(float groundSpeed) => 0.1f + 0.25f * Math.Clamp(groundSpeed / 6f, 0f, 1f);

    public static bool IsFoot(MusouContactPart p) => p is MusouContactPart.LeftFoot or MusouContactPart.RightFoot;

    // ------------------------------------------------------------------ MW3: the body's settle

    /// <summary>
    /// MW3, the settle's envelope (0 to 1): a smoothstep down over <paramref name="down"/> s, held for no time, then a smoothstep back over <paramref name="back"/> s; 0 after.
    /// The plan's landing: 0.06 s down, 0.25 s back.
    /// </summary>
    public static float SettleWeight(float age, float down, float back)
    {
        if (age < 0f || age >= down + back) return 0f;
        if (age < down) { float u = down > 1e-4f ? age / down : 1f; return u * u * (3f - 2f * u); }
        float v = back > 1e-4f ? (age - down) / back : 1f;
        return 1f - v * v * (3f - 2f * v);
    }

    /// <summary>A settle: the pelvis's drop (m), the spine's lean back (degrees; below 0 forward), the times down and back (s).</summary>
    public readonly record struct Settle(float Depth, float LeanDegrees, float Down, float Back);

    /// <summary>The roll's end settle (ours): 5 cm, no lean.</summary>
    public static readonly Settle RollEnd = new(0.05f, 0f, 0.06f, 0.25f);

    /// <summary>
    /// MW3 (ours): the settle a contact starts, or null. A landing: the pelvis drops 6 cm plus 8 cm at full strength (the plan's 6 to 14 cm), 0.06 s down and 0.25 s back (one a
    /// landing: the left foot's report). A sprint stop's plant (strength 0.8): 5 cm and a 7-degree lean back against the momentum, 0.08 s down, 0.3 s back. A melee step's plant:
    /// 3 cm and 3 degrees forward into the blow, 0.05 s down, 0.2 s back. Allies at half.
    /// </summary>
    public static Settle? SettleOf(in MusouGroundContact c)
    {
        float k = c.Own ? 1f : 0.5f;
        float s = Math.Clamp(c.Strength, 0f, 1f);
        return c.Kind switch
        {
            MusouContactKind.Landing when c.Part == MusouContactPart.LeftFoot => new Settle((0.06f + 0.08f * s) * k, 0f, 0.06f, 0.25f),
            MusouContactKind.Plant when s >= 0.75f => new Settle(0.05f * k, 7f * k, 0.08f, 0.3f),
            MusouContactKind.Plant => new Settle(0.03f * k, -3f * k, 0.05f, 0.2f),
            _ => null,
        };
    }
}

/// <summary>
/// One marine's bone contacts (MW1): per frame the bones' heights above the ground under the character; a part touches when its bone comes down to its resting height,
/// slides when it moves along the ground while touching, and the whole body lands when the character comes to the ground after <see cref="MusouWeightRules.LandingAirSeconds"/>.
/// The feet take part only when the caller says the pose has no foot tags (a clip with tags reports its own steps).
/// </summary>
public sealed class MusouContactTracker
{
    readonly bool[] _touching = new bool[MusouWeightRules.Parts];
    readonly double[] _touchedAt = new double[MusouWeightRules.Parts];
    readonly double[] _skidAt = new double[MusouWeightRules.Parts];
    readonly Num.Vector3[] _last = new Num.Vector3[MusouWeightRules.Parts];
    readonly float[] _lastHeight = new float[MusouWeightRules.Parts];
    bool _has, _grounded = true;
    double _lastTime, _landedAt = double.NegativeInfinity;
    float _airTime, _fallSpeed;

    public MusouContactTracker() { Array.Fill(_touchedAt, double.NegativeInfinity); Array.Fill(_skidAt, double.NegativeInfinity); }

    /// <summary>Forgets the history (a respawn, a long gap): the next frame only sets the state.</summary>
    public void Reset() { _has = false; _grounded = true; _airTime = 0f; _fallSpeed = 0f; }

    /// <summary>
    /// One frame: <paramref name="bones"/> in <see cref="MusouContactPart"/> order, whether the character stands on the ground and its vertical speed (m/s, up positive),
    /// whether the feet are tracked from the bones, the time (s). New contacts go to <paramref name="into"/>.
    /// </summary>
    public void Step(int playerId, bool own, ReadOnlySpan<MusouBoneSample> bones, bool grounded, float verticalSpeed, bool feetFromBones, double now, List<MusouGroundContact> into)
    {
        float dt = (float)(now - _lastTime);
        if (!_has || dt <= 1e-4f || dt > 0.5f)
        {
            for (int i = 0; i < MusouWeightRules.Parts && i < bones.Length; i++)
            {
                _last[i] = bones[i].At; _lastHeight[i] = bones[i].Height;
                _touching[i] = bones[i].Height < MusouWeightRules.Radius((MusouContactPart)i) + MusouWeightRules.EnterMargin;
            }
            _has = true; _lastTime = now; _grounded = grounded; _airTime = 0f;
            return;
        }
        _lastTime = now;
        if (!grounded) { _airTime += dt; _fallSpeed = Math.Min(_fallSpeed, verticalSpeed); }
        bool landed = grounded && !_grounded && _airTime >= MusouWeightRules.LandingAirSeconds;
        if (grounded && !_grounded)
        {
            if (landed)
            {
                float strength = MusouWeightRules.LandingStrength(_fallSpeed);
                for (int f = 0; f < 2 && f < bones.Length; f++)
                {
                    into.Add(new MusouGroundContact(playerId, own, (MusouContactPart)f, MusouContactKind.Landing, bones[f].At with { Y = bones[f].At.Y - bones[f].Height }, strength, 0f));
                    _touchedAt[f] = now; _touching[f] = true;
                }
                _landedAt = now;
            }
            _airTime = 0f; _fallSpeed = 0f;
        }
        _grounded = grounded;
        bool quiet = now - _landedAt < MusouWeightRules.AfterLandingQuiet;
        for (int i = 0; i < MusouWeightRules.Parts && i < bones.Length; i++)
        {
            var part = (MusouContactPart)i;
            var b = bones[i];
            float r = MusouWeightRules.Radius(part);
            var v = (b.At - _last[i]) / dt;
            float down = (b.Height - _lastHeight[i]) / dt;
            float along = new Num.Vector2(v.X, v.Z).Length();
            _last[i] = b.At; _lastHeight[i] = b.Height;
            bool tracked = grounded && (feetFromBones || !MusouWeightRules.IsFoot(part));
            if (!tracked) { if (b.Height > r + MusouWeightRules.ExitMargin) _touching[i] = false; continue; }
            var ground = b.At with { Y = b.At.Y - b.Height };
            if (_touching[i])
            {
                if (b.Height > r + MusouWeightRules.ExitMargin) { _touching[i] = false; continue; }
                if (!quiet && along > MusouWeightRules.SkidSpeed && now - _skidAt[i] >= MusouWeightRules.SkidEvery)
                {
                    _skidAt[i] = now;
                    into.Add(new MusouGroundContact(playerId, own, part, MusouContactKind.Skid, ground, MusouWeightRules.SkidStrength(along), along));
                }
                continue;
            }
            if (b.Height < r + MusouWeightRules.EnterMargin)
            {
                _touching[i] = true;
                if (quiet || down > -MusouWeightRules.TouchSpeed || now - _touchedAt[i] < MusouWeightRules.PartRate) continue;
                _touchedAt[i] = now;
                into.Add(new MusouGroundContact(playerId, own, part, MusouContactKind.Touch, ground, MusouWeightRules.TouchStrength(part, down), along));
            }
        }
    }
}

/// <summary>One install effect a contact plays (MW2): its name, scale and cull priority (0 is dropped first).</summary>
public readonly record struct MusouPuff(string Effect, float Scale, int Priority);

/// <summary>
/// MW2, the owner's dust, dirt and debris (2026-11-10: "these puffs of debris only appear when his feet or some part of him touches the ground"; small dust allowed as an
/// exception to the no-smoke rule): which install effects a ground contact plays. The dust is <c>knockback_kick_footslide</c> (a light and a dark <c>smoke_puff2</c> ring, the
/// data's kicked-up foot dust), the grit and stones <c>jumppack_land_light</c> (its stone chunks and low cloud) scaled down. Allies at half size and every other step and
/// skid; the CPU effect path (old GPUs, the Compatibility renderer) every other step and skid too. Every number ours.
/// </summary>
public static class MusouPuffRules
{
    public const string Dust = "knockback_kick_footslide";
    public const string Grit = "jumppack_land_light";
    /// <summary>An ally's puffs are this size (the owner's "allies half").</summary>
    public const float AllyScale = 0.5f;
    /// <summary>The same marine's skids make a puff at most this often (s): a roll reports about 18 a second.</summary>
    public const float SkidPuffEvery = 0.1f;

    /// <summary>How much dust a surface throws (0 to 1): dirt, sand, gravel and the level's default in full, stone 0.8, metal 0.4, none off bodies, shields and air.</summary>
    public static float SurfaceDust(string surface) => surface switch
    {
        "dirt" or "sand" or "gravel" or "default" => 1f,
        "stone" => 0.8f,
        "metal" => 0.4f,
        "flesh" or "default_body" or "armour" or "ceramite_armour" or "broken_ceramite" or "energy_shield" or "air" => 0f,
        _ => 0.8f,
    };

    /// <summary>
    /// The puffs of one contact into <paramref name="into"/> (at most 2); <paramref name="sequence"/> counts the marine's steps and skids, so the halved cases keep every other one.
    /// Returns how many.
    /// </summary>
    public static int Of(in MusouGroundContact c, string surface, bool gpu, int sequence, Span<MusouPuff> into)
    {
        float dust = SurfaceDust(surface);
        if (dust <= 0f || into.Length < 2) return 0;
        bool light = c.Kind is MusouContactKind.Step or MusouContactKind.Skid;
        if (light && (!c.Own || !gpu) && (sequence & 1) == 1) return 0;
        float size = c.Own ? 1f : AllyScale;
        float s = Math.Clamp(c.Strength, 0f, 1f);
        int n = 0;
        switch (c.Kind)
        {
            case MusouContactKind.Step:
                into[n++] = new MusouPuff(Dust, (0.2f + 0.25f * s) * dust * size, 0);
                break;
            case MusouContactKind.Skid:
                into[n++] = new MusouPuff(Dust, (0.12f + 0.2f * s) * dust * size, 0);
                break;
            case MusouContactKind.Plant:
                into[n++] = new MusouPuff(Dust, (0.25f + 0.3f * s) * dust * size, 0);
                into[n++] = new MusouPuff(Grit, (0.06f + 0.08f * s) * size, 0);
                break;
            case MusouContactKind.Touch:
                into[n++] = new MusouPuff(Dust, (0.2f + 0.3f * s) * dust * size, 0);
                if (c.Part is MusouContactPart.Pelvis or MusouContactPart.LeftShoulder or MusouContactPart.RightShoulder)
                    into[n++] = new MusouPuff(Grit, (0.05f + 0.08f * s) * size, 0);
                break;
            case MusouContactKind.Landing:
                into[n++] = new MusouPuff(Dust, (0.3f + 0.25f * s) * dust * size, 1);
                if (c.Part == MusouContactPart.LeftFoot) into[n++] = new MusouPuff(Grit, (0.1f + 0.3f * s) * size, 1);   // one grit burst a landing (both feet report it)
                break;
        }
        return n;
    }
}

/// <summary>
/// MW4, attack punch (the owner's answers of 2026-11-10: "Yes, retime" with the hit and end times kept, no gameplay change; "Pose hold + camera push" on heavy strikes and kills,
/// no freeze and no shake). Client presentation only: the simulation's attack, its hit and its end are untouched; only the clip time drawn for a marine's strike is remapped.
/// Every number ours.
/// </summary>
public static class MusouPunchRules
{
    /// <summary>The wind-up plays at this rate (light, heavy): the plan's 0.85x, heavier swings more.</summary>
    public const float WindRateLight = 0.85f, WindRateHeavy = 0.8f;
    /// <summary>The clip's last this many seconds before the hit are the strike (light, heavy): played faster so the hit still lands at its time.</summary>
    public const float StrikeLight = 0.15f, StrikeHeavy = 0.18f;
    /// <summary>The strike never gets less than this much real time (s).</summary>
    public const float MinStrike = 0.04f;
    /// <summary>The own pose hold on a heavy strike or a combo's last step that meets an Ork (s; the owner's 40 to 60 ms).</summary>
    public const float Hold = 0.05f;
    /// <summary>The camera push along the swing: forward and sideways (m, light and heavy), its rise and its return (s).</summary>
    public const float PushForward = 0.045f, PushForwardHeavy = 0.07f, PushSide = 0.025f, PushIn = 0.04f, PushOut = 0.18f;

    /// <summary>
    /// The clip time to draw at attack time <paramref name="t"/> (s) for a strike that hits at <paramref name="hit"/> and ends at <paramref name="end"/>: the wind-up slower, the
    /// strike faster, the hit at its own time; then, with <paramref name="hold"/> above 0, the pose held at the hit for that long and the recovery played a little faster so it
    /// ends at <paramref name="end"/>. Continuous, never decreasing, and equal to <paramref name="t"/> at 0, at the hit (without a hold) and from the end on.
    /// </summary>
    public static float ClipTime(float t, float hit, float end, bool heavy, float hold)
    {
        if (!(t > 0f) || !(hit > 0f) || !(end > hit)) return Math.Max(t, 0f);
        float rate = heavy ? WindRateHeavy : WindRateLight;
        float a = Math.Max(0f, hit - (heavy ? StrikeHeavy : StrikeLight));   // clip time where the strike begins
        float aReal = Math.Min(a / rate, hit - MinStrike);                    // real time it begins
        if (aReal <= a) { aReal = a; }                                         // a strike too short to remap: the wind-up as it is
        if (t < aReal) return aReal > 1e-5f ? t * (a / aReal) : t;
        if (t < hit) return a + (t - aReal) * ((hit - a) / Math.Max(hit - aReal, 1e-5f));
        hold = Math.Clamp(hold, 0f, (end - hit) * 0.5f);
        if (t >= end) return t;
        if (t < hit + hold) return hit;
        return hit + (t - hit - hold) * ((end - hit) / (end - hit - hold));
    }

    /// <summary>Whether a strike earns the hold and the push (the owner's "heavy strikes and kills"): a heavy strike, or a combo's last step; only when it meets an Ork.</summary>
    public static bool Accented(bool heavy, int step, int steps, int orksMet) => orksMet > 0 && (heavy || (steps > 0 && step >= steps));

    /// <summary>The push's weight at <paramref name="age"/> s (0 to 1): up over <see cref="PushIn"/>, back over <see cref="PushOut"/>.</summary>
    public static float PushWeight(float age) => MusouWeightRules.SettleWeight(age, PushIn, PushOut);

    /// <summary>How far a melee blow pushes a hit Ork of the rank and file (m; the plan: a hammer 0.35, a knife 0.12; the pilot's 0.2 for the rest).</summary>
    public static float OrkPush(string? wargear)
    {
        if (wargear is not { Length: > 0 } w) return 0.2f;
        if (w.Contains("hammer", StringComparison.OrdinalIgnoreCase) || w.Contains("maul", StringComparison.OrdinalIgnoreCase) || w.Contains("mace", StringComparison.OrdinalIgnoreCase)) return 0.35f;
        if (w.Contains("axe", StringComparison.OrdinalIgnoreCase)) return 0.28f;
        if (w.Contains("knife", StringComparison.OrdinalIgnoreCase)) return 0.12f;
        return 0.2f;
    }
}

/// <summary>
/// MW5, transitions (the owner's "add more transition animations and sync everything up to appear like a smoother, more powerful visual animation experience"): the pose
/// changes that snapped in the baseline (ready, idle, melee and sprint switches 50 to 110 cm/frame², the body turning 106 degrees in a frame from moving to ready) get a
/// longer stance cross-fade. Every number ours.
/// </summary>
public static class MusouTransitionRules
{
    /// <summary>A stance change cross-fades over at least this (s; the core's floor 0.15).</summary>
    public const float StanceFloor = 0.25f;
    /// <summary>
    /// The floored cross-fade: stance changes only (an instant change stays instant). A floor on clip entries too (0.15 s) was measured and dropped: with it the idle-to-ready
    /// switch's jerk p90 rose from 64 to 109 cm/frame² (probe runs of 2026-11-10).
    /// </summary>
    public static float Blend(float seconds, bool stance) => stance && seconds > 1e-3f ? Math.Max(seconds, StanceFloor) : seconds;
}

/// <summary>
/// MW6, camera and sound at contacts (owner 2026-11-10: "Thin the jolts"). Every number ours.
/// </summary>
public static class MusouImpactRules
{
    /// <summary>Only blasts this close shake and jolt the camera (m; was 25).</summary>
    public const float BlastRange = 12f;
    /// <summary>At most one blast jolt in this long (s).</summary>
    public const float BlastEvery = 0.25f;
    /// <summary>The own roll's end dips the camera this much (degrees; a landing's dip for a 2 m drop is about 0.4).</summary>
    public const float RollEndDipDegrees = 0.4f;
    /// <summary>A body contact sounds when at least this strong (a knee, hand, pelvis or shoulder meeting the ground).</summary>
    public const float TouchSoundStrength = 0.35f;
    /// <summary>The same marine's contact sounds at most once in this long (s).</summary>
    public const float SoundEvery = 0.15f;
    /// <summary>Contact sounds only within this of the camera (m).</summary>
    public const float SoundRange = 25f;
    /// <summary>The landing entry a jump-pack dash's landing plays (a drop of this many metres: the pack's lightest landing sound).</summary>
    public const float DashLandingDrop = 2f;

    /// <summary>Whether a blast <paramref name="distance"/> m away jolts the camera, given the last jolt's time; updates it when it does.</summary>
    public static bool BlastFeels(float distance, double now, ref double lastJolt)
    {
        if (!(distance <= BlastRange) || now - lastJolt < BlastEvery) return false;
        lastJolt = now;
        return true;
    }
}

