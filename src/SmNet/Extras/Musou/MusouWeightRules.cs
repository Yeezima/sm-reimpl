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
