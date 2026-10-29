using System;
using Godot;
using SmFormats.Havok;
using SmNet;

namespace SmClient;

/// <summary>
/// Foot pins for a marine that is stopping (M40-T5; the M40 audit's D9: "no start / stop clips; the run cycle continues through the fade"; ours).
/// <para>
/// When a marine stops, the run (or sprint, or any moving pose) cross-fades into the idle over the graph's blend (0.333 s relaxed, 0.15 s ready), and the blend space's idle share
/// takes over in the last metre a second of speed. The cross-fade blends the joints, so the foot that is on the ground slides from where it landed to where the idle pose puts it
/// (up to 70 cm in 0.1 s at the end of the run-down), and the foot in the air lands somewhere else and slides the last few centimetres: measured on the install, 22 to 27 cm of
/// planted-foot slide in the first 1.2 s after a stop. A sprint stop plays its own clip since animation pass 3 (<c>NetworkView.SprintEdges.cs</c>, posed by the body's speed over the
/// simulation's sprint stop); the pins cover the run's stops and the stop clip's hand-over to the idle.
/// </para>
/// <para>
/// Instead <see cref="BeginSettle"/> pins each foot that is on the ground at that moment (toe lower than <see cref="PlantedToe"/> above the marine's feet) at its world
/// position and orientation: two-bone IK (<see cref="SolveLeg"/>) holds the ankle there while the pose cross-fades to the idle. The foot in the air is pinned where it lands
/// (toe lower than <see cref="LandedToe"/>). After the hold a pinned foot steps to where the pose puts it (an arc, <see cref="StepLift"/>, so it is off the ground while it
/// moves) and its pin ends. A pin ends at once when the marine moves again (<see cref="CancelSettle"/>) or another pose takes over. Hold and step times come from the
/// caller (the graph's blend into the idle and a step duration of ours).
/// </para>
/// </summary>
public partial class CharacterViewer
{
    enum PinPhase : byte { Off, Pinned }

    struct FootPinState
    {
        public PinPhase Phase;
        public Vector3 World;          // the pinned ankle, world space
        public Quaternion WorldRotation;   // the pinned foot's orientation, world space (the sole stays as flat as it was)
        public float StepAt, StepLength;   // clock time the step starts and how long it takes
        public bool NoLift;                // animation pass 3: a release (ReleaseSettle) slides the foot onto the moving pose without the step's arc
    }

    readonly FootPinState[] _pins = new FootPinState[2];
    readonly bool[] _pinned = new bool[2];   // the foot has been pinned in this settle (it is not pinned again once its step is over)
    int[] _toeBones = [-1, -1];
    float _settleClock, _holdSeconds, _stepSeconds;
    bool _settleActive;

    /// <summary>A foot is on the ground when its toe is lower than this above the marine's feet when the settle begins (m; the probe's flat-foot test uses 0.02 to enter and 0.03 to leave; ours, a little looser so a foot just lifting is held).</summary>
    public const float PlantedToe = 0.045f;
    /// <summary>A foot in the air is pinned where it lands: its toe lower than this (m; the probe's entry height).</summary>
    public const float LandedToe = 0.02f;
    /// <summary>How far above the ground the stepping foot's ankle is raised at the middle of its step (m; ours).</summary>
    public const float StepLift = 0.07f;
    /// <summary><c>--no-foot-pins</c> (test aid, M40-T5): the stop is the plain cross-fade, as before.</summary>
    public static readonly bool NoFootPins = OS.GetCmdlineUserArgs().Contains("--no-foot-pins");

    /// <summary>Test aid counters (<c>--anim-probe</c>): settles begun, feet pinned.</summary>
    public static int SettlesBegun, FeetPinned, PinsDroppedReach, PinsDroppedTeleport, PinsSteppedEarly;

    Vector3 _lastSkeletonOrigin;
    bool _haveSkeletonOrigin;

    /// <summary>True while a settle is under way (a foot is pinned or may still land).</summary>
    public bool Settling => _settleActive;

    /// <summary>
    /// Starts a settle from the pose shown last: pins the feet that are on the ground now and schedules their steps for <paramref name="holdSeconds"/> from now (the one farther from
    /// the marine's feet position first, the other <paramref name="stepSeconds"/> x 0.8 later); each step takes <paramref name="stepSeconds"/>. A foot that is in the air is pinned when it lands.
    /// Returns the number of feet pinned now (a settle with none still starts when a foot is in the air; 0 and no settle when pins are off, the legs are not resolved or the stances
    /// are not driving the pose).
    /// </summary>
    public int BeginSettle(float holdSeconds, float stepSeconds)
    {
        CancelSettle();
        if (NoFootPins || NoLegIk || _skeleton is null || !_stanceMode) return 0;
        ResolveLegs();
        if (_legs is null || _legs.Length != 2) return 0;
        _toeBones = [_skeleton.FindBone("RIG_L_Toe"), _skeleton.FindBone("RIG_R_Toe")];
        if (_toeBones[0] < 0 || _toeBones[1] < 0) return 0;
        _holdSeconds = holdSeconds; _stepSeconds = stepSeconds;
        var heights = new float[2];
        var horizontal = new float[2];
        for (int i = 0; i < 2; i++)
        {
            heights[i] = _skeleton.GetBoneGlobalPose(_toeBones[i]).Origin.Z;   // skeleton space: Z up, the feet's plane at 0
            var ankle = _skeleton.GetBoneGlobalPose(_legs[i].Foot).Origin;
            horizontal[i] = new Vector2(ankle.X, ankle.Y).Length();
        }
        bool[] planted = [heights[0] < PlantedToe, heights[1] < PlantedToe];
        if (!planted[0] && !planted[1])
        {
            int lower = heights[0] <= heights[1] ? 0 : 1;   // neither is flat: the lower one if it is nearly down
            if (heights[lower] < PlantedToe * 3f) planted[lower] = true;
        }
        // The foot farther from the feet's centre steps first.
        int first = planted[0] && planted[1] ? (horizontal[0] >= horizontal[1] ? 0 : 1) : planted[0] ? 0 : 1;
        int count = 0;
        for (int i = 0; i < 2; i++)
        {
            if (!planted[i]) continue;
            Pin(i, holdSeconds + (i == first ? 0f : stepSeconds * 0.8f));
            count++;
        }
        _settleActive = true; _settleClock = 0f;
        SettlesBegun++; FeetPinned += count;
        return count;
    }

    /// <summary>Pins foot <paramref name="i"/> at its place in the pose shown now, to step at <paramref name="stepAt"/> seconds on the settle's clock.</summary>
    void Pin(int i, float stepAt)
    {
        var footPose = _skeleton!.GetBoneGlobalPose(_legs![i].Foot);
        _pins[i] = new FootPinState
        {
            Phase = PinPhase.Pinned, StepLength = _stepSeconds, StepAt = stepAt,
            World = _skeleton.GlobalTransform * footPose.Origin,
            WorldRotation = _skeleton.GlobalTransform.Basis.GetRotationQuaternion() * footPose.Basis.GetRotationQuaternion(),
        };
        _pinned[i] = true;
    }

    /// <summary>
    /// Animation pass 3: lets the pins go over <paramref name="seconds"/> instead of at once (the marine moves on while its feet are held: dropping the pins snapped a foot to the
    /// moving pose in one frame, 40 to 95 cm per frame per frame in the transition probe). Each held foot starts its step now (no lift: the moving pose carries the foot itself) and
    /// no foot is pinned again in this settle. <c>--no-p3</c>: <see cref="CancelSettle"/>.
    /// </summary>
    public void ReleaseSettle(float seconds)
    {
        if (NoP3 || !_settleActive) { CancelSettle(); return; }
        for (int i = 0; i < 2; i++)
        {
            _pinned[i] = true;   // no new pin for a foot that lands from now on
            ref var pin = ref _pins[i];
            if (pin.Phase == PinPhase.Off) continue;
            if (_settleClock < pin.StepAt) { pin.StepAt = _settleClock; pin.StepLength = seconds; pin.NoLift = true; }   // a step under way goes on as it was
        }
    }

    /// <summary>Drops every pin (the marine moves again, or another pose takes over).</summary>
    public void CancelSettle()
    {
        if (!_settleActive && _pins[0].Phase == PinPhase.Off && _pins[1].Phase == PinPhase.Off) return;
        _pins[0] = default; _pins[1] = default;
        _pinned[0] = _pinned[1] = false;
        _settleActive = false;
    }

    void StepSettle(float dt)
    {
        if (!_settleActive) return;
        _settleClock += dt;
        for (int i = 0; i < 2; i++)
            if (_pins[i].Phase != PinPhase.Off && _settleClock >= _pins[i].StepAt + _pins[i].StepLength) _pins[i] = default;   // the step is over: the pose's own foot
        // Over when both feet have been pinned and have stepped, or when nothing can land any more (the hold and two steps have passed).
        bool anyPin = _pins[0].Phase != PinPhase.Off || _pins[1].Phase != PinPhase.Off;
        if (!anyPin && ((_pinned[0] && _pinned[1]) || _settleClock > _holdSeconds + 2f * _stepSeconds)) { _settleActive = false; _pinned[0] = _pinned[1] = false; }
    }

    bool _entryFootLock;

    /// <summary>The thigh, calf, foot and toe bones of both legs (not the pelvis): what a locked clip entry takes from the clip without a fade.</summary>
    bool IsLegBelowPelvis(int bone)
    {
        if (_legs is null) ResolveLegs();
        if (_legs is null) return false;
        foreach (var leg in _legs)
        {
            if (bone == leg.Thigh || bone == leg.Calf || bone == leg.Foot) return true;
            if (_skeleton is not null) for (int b = bone; b >= 0; b = _skeleton.GetBoneParent(b)) if (b == leg.Foot) return true;   // toe, knee pad
        }
        return false;
    }

    /// <summary>A clip foot with its toe lower than this (m, model space) is planted in the clip (the probe's entry height with a little margin).</summary>
    public const float ClipPlantedToe = 0.03f;

    /// <summary>
    /// While a clip that was entered with <c>lockFeet</c> fades in from the pose shown before (<see cref="Pose"/>), the feet that are planted in the clip itself go to the
    /// clip's own ankle positions and orientations at once instead of fading from the old pose: a cross-fade between poses on a body that is already moving drags a planted foot
    /// along with the body for the length of the fade (measured: 25 to 40 cm at the start of a run). Used for the run start, whose first pose is the idle stance (the clip's feet are
    /// where the idle's were), so the feet do not jump. <paramref name="local"/> is the clip's sampled pose.
    /// </summary>
    void EntryFootLock(HavokTransform[] local)
    {
        if (NoFootPins || NoLegIk || _animation is null || _skeleton is null || _assets is null) return;
        ResolveLegs();
        if (_legs is null || _legs.Length != 2) return;
        var sk = _assets.Model.Rig.Skeleton;
        if (!TrackOfBone.TryGetValue(_animation, out var trackOf))
        {
            trackOf = new int[sk.BoneNames.Count];
            Array.Fill(trackOf, -1);
            for (int t = 0; t < _animation.TrackToBone.Length; t++) if ((uint)_animation.TrackToBone[t] < (uint)trackOf.Length) trackOf[_animation.TrackToBone[t]] = t;
            TrackOfBone.Add(_animation, trackOf);
        }
        if (_toeBones[0] < 0 || _toeBones[1] < 0) _toeBones = [_skeleton.FindBone("RIG_L_Toe"), _skeleton.FindBone("RIG_R_Toe")];
        for (int l = 0; l < 2; l++)
        {
            System.Numerics.Matrix4x4 model = System.Numerics.Matrix4x4.Identity;
            foreach (int b in _legs[l].Chain)
            {
                var transform = trackOf[b] >= 0 ? local[trackOf[b]] : sk.ReferencePose[b];
                model = transform.ToMatrix() * model;
            }
            int toe = _toeBones[l];
            if (toe < 0) continue;
            var toeTransform = trackOf[toe] >= 0 ? local[trackOf[toe]] : sk.ReferencePose[toe];
            float toeHeight = (toeTransform.ToMatrix() * model).M43;
            if (toeHeight > ClipPlantedToe) continue;
            SolveLeg(_legs[l], new Vector3(model.M41, model.M42, model.M43));
            var q = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.CreateFromRotationMatrix(model));
            var footRotation = new Quaternion(q.X, q.Y, q.Z, q.W);
            var calfRotation = _skeleton.GetBoneGlobalPose(_legs[l].Calf).Basis.GetRotationQuaternion();
            _skeleton.SetBonePoseRotation(_legs[l].Foot, (calfRotation.Inverse() * footRotation).Normalized());
        }
    }

    /// <summary>Holds or steps the pinned ankles after the pose is built (stance layers only); pins a foot in the air once it lands.</summary>
    void ApplyFootPins()
    {
        if (_skeleton is null) return;
        // Review S2: a body that jumps more than a metre in a frame (respawn, round restart, any alive-to-alive teleport) drops every pin: the held world positions are of the old place.
        var origin = _skeleton.GlobalTransform.Origin;
        bool jumped = _haveSkeletonOrigin && FootPinRules.Teleported(origin.X - _lastSkeletonOrigin.X, origin.Y - _lastSkeletonOrigin.Y, origin.Z - _lastSkeletonOrigin.Z);
        _lastSkeletonOrigin = origin; _haveSkeletonOrigin = true;
        if (jumped && _settleActive) { CancelSettle(); PinsDroppedTeleport++; }
        if (!_settleActive || _legs is null) return;
        var toSkeleton = _skeleton.GlobalTransform.AffineInverse();
        var skeletonRotationInverse = _skeleton.GlobalTransform.Basis.GetRotationQuaternion().Inverse();
        for (int i = 0; i < 2; i++)
        {
            if (_pins[i].Phase == PinPhase.Off && !_pinned[i] && _toeBones[i] >= 0 && _skeleton.GetBoneGlobalPose(_toeBones[i]).Origin.Z < LandedToe)
            {
                // The foot that was in the air has landed: it stays where it landed and steps to the pose after the other one.
                bool otherPinned = _pins[1 - i].Phase != PinPhase.Off;
                Pin(i, MathF.Max(_settleClock + 0.01f, _holdSeconds + (otherPinned ? _stepSeconds * 0.8f : 0f)));
                FeetPinned++;
            }
            ref var pin = ref _pins[i];
            if (pin.Phase == PinPhase.Off) continue;
            var held = toSkeleton * pin.World;
            var heldRotation = skeletonRotationInverse * pin.WorldRotation;
            var ownPose = _skeleton.GetBoneGlobalPose(_legs[i].Foot);   // where the pose puts the ankle, and how it turns the foot
            Vector3 wanted;
            Quaternion wantedRotation;
            if (_settleClock < pin.StepAt) { wanted = held; wantedRotation = heldRotation; }
            else
            {
                float u = Math.Clamp((_settleClock - pin.StepAt) / MathF.Max(pin.StepLength, 1e-3f), 0f, 1f);
                float ease = u * u * (3f - 2f * u);
                wanted = held.Lerp(ownPose.Origin, ease) + new Vector3(0f, 0f, pin.NoLift ? 0f : StepLift * MathF.Sin(MathF.PI * u));
                wantedRotation = heldRotation.Slerp(ownPose.Basis.GetRotationQuaternion(), ease);
            }
            // Review S1: a pin the leg cannot reach (aim-walk, a push, anything the teleport test missed) is dropped, not stretched to (the foot is not pinned again in this settle).
            var hip = _skeleton.GetBoneGlobalPose(_legs[i].Thigh).Origin;
            var knee = _skeleton.GetBoneGlobalPose(_legs[i].Calf).Origin;
            float legLength = hip.DistanceTo(knee) + knee.DistanceTo(ownPose.Origin);
            if (FootPinRules.OutOfReach(hip.DistanceTo(wanted), legLength))
            {
                // Animation pass 3: dropping the pin snapped the foot to the pose in one frame (the transition probe: 35 to 45 cm at the end of the sprint stop). Now a pin the leg
                // cannot reach starts its step at once and the ankle is held on the edge of the leg's reach until the step brings it within (ours). --no-p3: dropped, as before.
                if (NoP3) { pin = default; PinsDroppedReach++; continue; }
                if (_settleClock < pin.StepAt) { pin.StepAt = _settleClock; PinsSteppedEarly++; }
                var reach = wanted - hip;
                if (reach.LengthSquared() > 1e-8f) wanted = hip + reach.Normalized() * legLength * 0.999f;
            }
            SolveLeg(_legs[i], wanted);
            // The foot keeps its pinned orientation too: a foot whose heel lifts while the ankle is held swings the toe along the ground.
            var calfRotation = _skeleton.GetBoneGlobalPose(_legs[i].Calf).Basis.GetRotationQuaternion();
            _skeleton.SetBonePoseRotation(_legs[i].Foot, (calfRotation.Inverse() * wantedRotation).Normalized());
        }
    }
}
