using System;
using System.Collections.Generic;
using System.Linq;

namespace SmContent.Extras.Musou;

/// <summary>How a key is reached from the key before it (the easing of the segment that ends at the key).</summary>
public enum PoseEase : byte
{
    /// <summary>Smoothstep: eases out of the key before and into this one (recoveries, settles).</summary>
    Smooth,
    /// <summary>Accelerates into the key (an anticipation that gathers: slow start, arrives moving).</summary>
    In,
    /// <summary>Decelerates into the key (leaves fast, settles).</summary>
    Out,
    /// <summary>A constant rate (a blade passing through the front of a sweep without pausing).</summary>
    Linear,
    /// <summary>The strike: nearly all of the travel in the first part of the segment, then it sits (the snap).</summary>
    Snap,
    /// <summary>Passes the key's value by about a tenth and comes back (a follow-through with weight).</summary>
    Over,
    /// <summary>Holds the key before until this key's time, then takes this key's value (a step).</summary>
    Hold,
}

/// <summary>What a channel's keys mean.</summary>
public enum PoseChannelKind : byte
{
    /// <summary>A turn of the bone about its own origin in the body's frame: the key's yaw, pitch and roll (degrees); zero leaves the base pose alone.</summary>
    Turn,
    /// <summary>
    /// Where the bone's segment points (the bone's origin toward <see cref="PoseChannel.Toward"/>'s origin), as an elevation above the horizontal and an azimuth from the body's front toward
    /// its right (degrees), reached from the base pose's direction by the shortest turn, as far as the key's weight (0: the base pose, 1: the aimed direction). Absolute, so a limb's key
    /// means the same over any stance.
    /// </summary>
    Aim,
    /// <summary>
    /// A shift of the bone's origin in the body's frame (the key's fields as metres: forward, right, up; the pelvis drops, shifts and sways), as far as the key's weight. The legs follow the
    /// pelvis, so the planted feet are put back where the stance had them by the leg solve (<see cref="Foot"/>).
    /// </summary>
    Move,
    /// <summary>
    /// A foot's place against the ground: an offset (metres forward, right, up in the body's frame) from where the stance plants it, reached by a two-bone leg solve; zero holds the foot
    /// planted on its spot, an up offset lifts it (a step), a new forward / right offset with up 0 replants it there. A foot with no channel is held on its spot whenever the clip moves the
    /// pelvis, so a planted foot never slides.
    /// </summary>
    Foot,
}

/// <summary>
/// One key at <paramref name="T"/> seconds into the clip. For a <see cref="PoseChannelKind.Turn"/> channel: <paramref name="Yaw"/> about the body's up axis (positive turns toward the
/// character's left), <paramref name="Pitch"/> about the body's right axis (positive tips a standing segment back), <paramref name="Roll"/> about the body's front axis (positive tips a
/// standing segment to the right), degrees, composed yaw, then pitch, then roll and applied about the bone's own origin over the pose under the overlay. For an
/// <see cref="PoseChannelKind.Aim"/> channel: <paramref name="Yaw"/> is the azimuth, <paramref name="Pitch"/> the elevation and <paramref name="Weight"/> how far the segment is turned
/// from the base pose toward them.
/// </summary>
public readonly record struct PoseKey(float T, float Yaw, float Pitch, float Roll, PoseEase Ease = PoseEase.Smooth, float Weight = 1f)
{
    public static PoseKey Zero(float t, PoseEase ease = PoseEase.Smooth) => new(t, 0f, 0f, 0f, ease, 0f);
    /// <summary>A turn key.</summary>
    public static PoseKey Turn(float t, float yaw, float pitch, float roll, PoseEase ease = PoseEase.Smooth) => new(t, yaw, pitch, roll, ease, 1f);
    /// <summary>An aim key: the segment toward <paramref name="elevation"/> degrees above the horizontal and <paramref name="azimuth"/> degrees from the front toward the right.</summary>
    public static PoseKey Aim(float t, float elevation, float azimuth, PoseEase ease = PoseEase.Smooth, float weight = 1f) => new(t, azimuth, elevation, 0f, ease, weight);
    /// <summary>A move or foot key: an offset in metres in the body's frame (<see cref="PoseChannelKind.Move"/>, <see cref="PoseChannelKind.Foot"/>).</summary>
    public static PoseKey Move(float t, float forward, float right, float up, PoseEase ease = PoseEase.Smooth, float weight = 1f) => new(t, forward, right, up, ease, weight);
    /// <summary>A move or foot key's offset (metres).</summary>
    public float Forward => Yaw;
    public float Right => Pitch;
    public float Up => Roll;
    /// <summary>This key's turn scaled (a recovery part-way back toward the base pose); for an aim key the weight is scaled.</summary>
    public PoseKey Scaled(float t, float factor, PoseEase ease = PoseEase.Smooth) => new(t, Yaw * factor, Pitch * factor, Roll * factor, ease, Weight * factor);
    /// <summary>An aim key's elevation and azimuth.</summary>
    public float Elevation => Pitch;
    public float Azimuth => Yaw;
}

/// <summary>A sampled key: the turn (or the aim's azimuth, elevation) and weight at a time.</summary>
public readonly record struct PoseSample(float Yaw, float Pitch, float Roll, float Weight)
{
    public float Elevation => Pitch;
    public float Azimuth => Yaw;
    /// <summary>A move or foot sample's offset (metres).</summary>
    public float Forward => Yaw;
    public float Right => Pitch;
    public float Up => Roll;
    public bool IsZero => MathF.Abs(Yaw) + MathF.Abs(Pitch) + MathF.Abs(Roll) < 1e-3f;
}

/// <summary>A bone's keys, in time order. An <see cref="PoseChannelKind.Aim"/> channel names the child bone the segment runs toward; a <see cref="PoseChannelKind.Foot"/> channel names the foot bone.</summary>
public sealed record PoseChannel(string Bone, PoseKey[] Keys, PoseChannelKind Kind = PoseChannelKind.Turn, string? Toward = null)
{
    /// <summary>The value at <paramref name="t"/> seconds, held at the first key before it and at the last key after it. An aim's azimuth is interpolated the short way round.</summary>
    public PoseSample Sample(float t) => Sample(Keys, Kind, t);

    /// <summary>The value of <paramref name="k"/> (keys of a <paramref name="kind"/> channel) at <paramref name="t"/>, without a channel (no allocation: the grip tracks are sampled each frame).</summary>
    public static PoseSample Sample(PoseKey[] k, PoseChannelKind kind, float t)
    {
        if (k.Length == 0) return default;
        if (t <= k[0].T) return new(k[0].Yaw, k[0].Pitch, k[0].Roll, k[0].Weight);
        for (int i = 1; i < k.Length; i++)
        {
            if (t > k[i].T) continue;
            var a = k[i - 1]; var b = k[i];
            float span = b.T - a.T;
            float u = span <= 1e-6f ? 1f : (t - a.T) / span;
            float e = Ease(b.Ease, u);
            float yaw = kind == PoseChannelKind.Aim ? a.Yaw + ShortWay(b.Yaw - a.Yaw) * e : a.Yaw + (b.Yaw - a.Yaw) * e;
            return new(yaw, a.Pitch + (b.Pitch - a.Pitch) * e, a.Roll + (b.Roll - a.Roll) * e, a.Weight + (b.Weight - a.Weight) * e);
        }
        var last = k[^1];
        return new(last.Yaw, last.Pitch, last.Roll, last.Weight);
    }

    static float ShortWay(float degrees)
    {
        degrees %= 360f;
        if (degrees > 180f) degrees -= 360f;
        if (degrees < -180f) degrees += 360f;
        return degrees;
    }

    /// <summary>The easing curves: each maps 0..1 to a share of the travel (the <see cref="PoseEase.Over"/> curve passes 1).</summary>
    public static float Ease(PoseEase ease, float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        switch (ease)
        {
            case PoseEase.In: return u * u * u;
            case PoseEase.Out: { float v = 1f - u; return 1f - v * v * v; }
            case PoseEase.Linear: return u;
            case PoseEase.Snap: { float v = 1f - u; return 1f - v * v * v * v * v; }   // 97 % of the travel in the first half of the segment
            case PoseEase.Over: { const float c = 1.2f; float v = u - 1f; return 1f + (c + 1f) * v * v * v + c * v * v; }   // back-ease out: overshoots by about a tenth
            case PoseEase.Hold: return u >= 1f ? 1f : 0f;
            default: return u * u * (3f - 2f * u);
        }
    }
}

/// <summary>
/// A keyframed pose overlay for a Musou weapon's strike (added content; <c>docs/claw-spear-animations.md</c>): our own keys on the marine skeleton laid over an install clip
/// (<see cref="BaseClip"/>, the stance the strike starts from and returns to), so no original byte is copied and the hit time stays the design's (<see cref="HitTime"/>, the moment the
/// strike's snap lands). Sampled by the client (<c>CharacterViewer.WeaponPose</c>) at the simulation's attack time; the keys begin and end on the base pose, so the overlay blends out
/// of and back into the stance by itself.
/// </summary>
/// <param name="Name">The clip's name (<c>claw_x1</c> ...).</param>
/// <param name="Weapon"><c>claw</c> or <c>spear</c>.</param>
/// <param name="Duration">The strike's length (s): the borrowed strike's clip length at the design's tempo.</param>
/// <param name="HitTime">When the hit lands (s from the start): the borrowed strike's hit time at the design's tempo; NaN for a held pose.</param>
/// <param name="BaseClip">The install clip the keys are laid over.</param>
/// <param name="Anticipation">When the wind-up is at its apex (s), for the stills.</param>
/// <param name="Follow">When the follow-through is at its farthest (s), for the stills.</param>
/// <param name="Settle">When the pose is back on the stance for the stills (the end of the recovery, not of the clip).</param>
/// <param name="NextAt">Where a chained next strike starts in a preview (s; the borrowed strike's combo window opening at the tempo), or NaN when none chains.</param>
/// <param name="Source">The borrowed strike this takes its timing from, for the table.</param>
/// <param name="Description">What the strike does, in words.</param>
public sealed record WeaponPoseClip(string Name, string Weapon, float Duration, float HitTime, string BaseClip, float Anticipation, float Follow, float Settle, float NextAt, string Source, string Description, PoseChannel[] Channels)
{
    /// <summary>The bones this clip moves.</summary>
    public IEnumerable<string> Bones => Channels.Select(c => c.Bone);

    /// <summary>
    /// The frame the clip's keys (turns, aims, shifts, foot offsets) and its spear placement are read in: false (the default) the stance's, from the hips as the pose under the keys stands; true
    /// the way the player faces (the skeleton's +Y, his right +X), so the pose keeps its place against the camera whichever stance is under it (2026-11-11: the spear's carry, whose blade must
    /// stay out of the crosshair's column standing, walking, running and sprinting, while the hammer stance's hips turn from 71 degrees right of the facing at idle to nearly ahead in the run).
    /// </summary>
    public bool FacingFrame { get; init; }

    /// <summary>The facing frame's front and right in skeleton space (<see cref="FacingFrame"/>): +Y and +X.</summary>
    public static readonly System.Numerics.Vector3 FacingFront = new(0f, 1f, 0f), FacingRight = new(1f, 0f, 0f);

    /// <summary>
    /// For a two-handed weapon carried in one hand (the spear; owner 2026-11-10: "right hand carries, both swing"): how far the off (left) hand is on the shaft over time, the keys' weights
    /// 0 (off, the shaft stands upright from the carrying hand) to 1 (both hands, the shaft on the line through them). Null: both hands throughout.
    /// </summary>
    public PoseKey[]? Grip { get; init; }

    /// <summary>The off hand's hold on the shaft at <paramref name="t"/> (0 to 1; 1 when the clip has no grip track).</summary>
    public float GripAt(float t) => Grip is { Length: > 0 } g ? Math.Clamp(PoseChannel.Sample(g, PoseChannelKind.Turn, t).Weight, 0f, 1f) : 1f;

    /// <summary>
    /// Where the carrying (right) hand holds the shaft, metres from the butt, over time (the keys' first field; owner 2026-11-10: "grip higher up on the spear shaft towards the blade" when
    /// running, "slide the spear inside his hand to hold it lower towards the butt end" for melee). Null: the low melee grip throughout (<see cref="LowGrip"/>).
    /// </summary>
    public PoseKey[]? GripPoint { get; init; }

    /// <summary>The melee grip (m from the butt): the hands low on the shaft for the full reach of the hews.</summary>
    public const float LowGrip = 0.5f;
    /// <summary>
    /// The carry (m from the butt): the hand higher than the melee grip, at the point where the butt hangs just above the ground as he runs (owner, 2026-11-10: "right at the point where the butt
    /// of the spear is just above the ground"). Since the no-clipping pass (2026-11-10) the hand is held out wide and the shaft leans forward to <see cref="SpearHold.CarryElevation"/>, so the
    /// butt rides at least 0.56 m off the ground over the hammer stance clips (0.14 m running before), clear of the legs' kick (a refinement for the owner).
    /// </summary>
    public const float CarryGrip = 0.95f;
    /// <summary>The stab's grip (m from the butt): both hands back at the butt for the lunge's reach.</summary>
    public const float StabGrip = 0.3f;

    /// <summary>
    /// The grip contract (review of 2026-11-10): the carrying hand's place on the shaft at <paramref name="t"/>, **metres from the butt** (<see cref="CarryGrip"/>, <see cref="LowGrip"/>,
    /// <see cref="StabGrip"/>). A model puts the hand at exactly this point of its own shaft (<see cref="SpearHold.Place"/>; W3's model through <see cref="SpearHold.AxisAt"/>), so the meaning
    /// does not change when a model's own grip constants do.
    /// </summary>
    public float GripPointAt(float t) => GripPoint is { Length: > 0 } g ? PoseChannel.Sample(g, PoseChannelKind.Turn, t).Yaw : LowGrip;

    /// <summary>
    /// The clip's time for a strike shown <paramref name="strikeTime"/> s into a simulated step whose hit is at <paramref name="strikeHit"/> s: the clip as authored when its hit is the step's
    /// (every spear clip on the strike it was authored for), else its wind-up stretched or squeezed so its hit lands on the step's and the follow-through at its own rate (a strike shown with
    /// another strike's clip). Never below 0 or past the clip's end.
    /// </summary>
    public float TimeAt(float strikeTime, float strikeHit)
    {
        float t = MathF.Max(0f, strikeTime);
        if (float.IsNaN(HitTime) || !float.IsFinite(strikeHit) || strikeHit <= 1e-4f || MathF.Abs(strikeHit - HitTime) < 1e-3f) return MathF.Min(t, Duration);
        float shown = t <= strikeHit ? t * HitTime / strikeHit : HitTime + (t - strikeHit);
        return MathF.Min(shown, Duration);
    }
}
