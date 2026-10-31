using System.Numerics;
using SmFormats.Havok;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>
/// The camera's place relative to the character for one pitch, in metres in the character's frame (the game's: X right,
/// Y forward, Z up; the character stands at the origin and faces +Y): the camera (<see cref="Back"/> behind the feet,
/// <see cref="Side"/> to the right, <see cref="Up"/> above them), the pivot it is pulled toward when something is in the way
/// (interpretation: the inner camera bone), and <see cref="LookPitch"/>, the camera's elevation in radians (positive up; the
/// simulation's convention), from the outer bone's rotation.
/// </summary>
public readonly record struct CameraOffset(float Back, float Side, float Up, float PivotBack, float PivotSide, float PivotUp, float LookPitch)
{
    /// <summary>The camera position in the character's frame (X right, Y forward, Z up).</summary>
    public Vector3 Camera => new(Side, -Back, Up);
    /// <summary>The pivot position in the character's frame.</summary>
    public Vector3 Pivot => new(PivotSide, -PivotBack, PivotUp);
}

/// <summary>
/// The original third-person camera rig (<c>spacemarine_camera__hkxx</c> in <c>data/packages/spacemarine_camera.oppc</c>;
/// docs/discovery.md, "Camera rig"): a three-bone skeleton (<c>RIG_Root</c>, <c>RIG_AimInner_Camera</c>,
/// <c>RIG_AimOuter_Camera</c>) and one animation per camera curve (<c>navigationSet_standing</c>, <c>aimSet_standing</c>,
/// <c>pistol_aimSet_standing</c>, ...; the <c>animation_curve</c> of a <see cref="CameraSet"/> names one, case aside).
/// Each animation is sampled by pitch: <b>supported by evidence</b> — the 6 s clips have 181 frames at 30 fps, one per degree
/// from 0 (looking straight down) through 90 (level) to 180 (straight up), and the camera bones' rotation about X follows
/// that angle exactly on the standing sets; <see cref="CameraSet.PitchMinimum"/> and <see cref="CameraSet.PitchMaximum"/>
/// are in the same degrees. That the outer bone is the camera and the inner bone its collision pivot is an
/// <b>interpretation</b> (the outer bone always lies behind the inner one along the view).
/// </summary>
public sealed class CameraRig
{
    public const string Package = "data/packages/spacemarine_camera.oppc";
    public const string Entry = "spacemarine_camera__hkxx";
    public const string InnerBone = "RIG_AimInner_Camera";
    public const string OuterBone = "RIG_AimOuter_Camera";

    readonly HavokSkeleton _skeleton;
    readonly int _inner, _outer;
    readonly Dictionary<string, HavokBoundAnimation> _curves = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The animation (curve) names, in file order.</summary>
    public IReadOnlyList<string> CurveNames { get; }

    CameraRig(HavokCharacterFile file)
    {
        _skeleton = file.Skeleton;
        _inner = IndexOf(InnerBone);
        _outer = IndexOf(OuterBone);
        var names = new List<string>();
        foreach (var name in file.AnimationNames)
        {
            var bound = file.LoadAnimation(name);
            if (bound.Animation.Duration <= 0f || !float.IsFinite(bound.Animation.Duration)) continue;
            _curves[name] = bound;
            names.Add(name);
        }
        CurveNames = names;

        int IndexOf(string bone)
        {
            for (int i = 0; i < _skeleton.BoneNames.Count; i++)
                if (string.Equals(_skeleton.BoneNames[i], bone, StringComparison.OrdinalIgnoreCase)) return i;
            throw new InvalidDataException($"Camera rig has no bone '{bone}'.");
        }
    }

    /// <summary>Reads the rig from a camera packfile (the bytes of <see cref="Entry"/>).</summary>
    public static CameraRig Read(byte[] packfile) => new(new HavokCharacterFile(packfile));

    /// <summary>Reads the rig from the install (read-only).</summary>
    public static CameraRig Load(string gameDir)
    {
        byte[]? bytes = null;
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, Package)))
            archive.ReadEntries(e => e.FileName == Entry, (_, d) => bytes = d.ToArray());
        return Read(bytes ?? throw new FileNotFoundException($"'{Entry}' not found in {Package}."));
    }

    /// <summary>Whether the rig has the named curve (case ignored).</summary>
    public bool Has(string curveName) => _curves.ContainsKey(curveName);

    /// <summary>
    /// The camera for curve <paramref name="curveName"/> at <paramref name="pitchDegrees"/> in the files' convention (0 straight
    /// down, 90 level, 180 straight up; clamped to 0..180): the clip is sampled at pitch / 180 of its length. Null when the rig
    /// has no such curve. Clips shorter than 6 s (<c>meleeSet_CombatKnife</c>, <c>SyncKillSet_Large</c>: 3 s) are sampled the
    /// same way (provisional: their angle per frame is not established).
    /// </summary>
    public CameraOffset? Offset(string curveName, float pitchDegrees)
    {
        if (!_curves.TryGetValue(curveName, out var curve) || !float.IsFinite(pitchDegrees)) return null;
        var animation = curve.Animation;
        float time = Math.Clamp(pitchDegrees, 0f, 180f) / 180f * animation.Duration;
        var local = _skeleton.ReferencePose.ToArray();
        var pose = animation.Sample(time);
        for (int t = 0; t < pose.Length; t++) local[curve.TrackToBone[t]] = pose[t];
        var model = _skeleton.ModelSpace(local);
        var inner = model[_inner].Translation;
        var outer = model[_outer].Translation;
        var forward = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, model[_outer]));
        float look = MathF.Atan2(forward.Z, new Vector2(forward.X, forward.Y).Length());
        return new CameraOffset(-outer.Y, outer.X, outer.Z, -inner.Y, inner.X, inner.Z, look);
    }

    /// <summary>As <see cref="Offset"/> for a pitch in the simulation's convention (radians, 0 level, positive up).</summary>
    public CameraOffset? OffsetAtPitch(string curveName, float pitchRadians) => Offset(curveName, FileDegrees(pitchRadians));

    /// <summary>
    /// The pitch at which a temporary set (an evade's) is looked up for a view at <paramref name="viewPitch"/> (simulation radians): the view's own, only capped at the set's
    /// upper limit (above it the camera keeps that place and only turns, as the exploration and aim sets do). No lower cap: the evade sets stop at 80 file degrees (10 below level) but
    /// their curves orbit the character down to 0, and with a lower cap a view looking at the floor kept the place of a nearly level view, about 3 m behind the character and
    /// 2.5 m lower, so the character left the screen for the length of every hop (camera/hop-and-walls).
    /// </summary>
    public static float SetPlacePitch(float viewPitch, CameraSet set) => MathF.Min(viewPitch, SimRadians(set.PitchMaximum));

    /// <summary>A simulation pitch (radians, 0 level, positive up) in the files' degrees (0 down, 90 level, 180 up).</summary>
    public static float FileDegrees(float pitchRadians) => 90f + pitchRadians * (180f / MathF.PI);

    /// <summary>A pitch in the files' degrees as a simulation pitch (radians).</summary>
    public static float SimRadians(float fileDegrees) => (fileDegrees - 90f) * (MathF.PI / 180f);
}
