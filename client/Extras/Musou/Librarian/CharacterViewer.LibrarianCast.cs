using System;
using Godot;

namespace SmClient;

/// <summary>
/// What a Librarian's character shows of his chain lightning cast this frame (Musou Mode only; set by the networked view, <c>NetworkView.TfwLibrarian.cs</c>, read by
/// <see cref="CharacterViewer.ExtrasPoseOverlay"/>). Null on every other character, so the pose overlay returns at once for them.
/// </summary>
public sealed class LibrarianCastState
{
    /// <summary>The cast pose's blend weight, 0 (none) to 1 (full), eased by the caller.</summary>
    public float Weight;
    /// <summary>What the palms aim at: the first struck enemy's chest, or the bolt's end when nothing is in reach (Godot world space).</summary>
    public Vector3 Target;
    /// <summary>The shudder's strength, 0 to 1 (the cast's build-up, the targets struck, the Psychic Stress).</summary>
    public float Intensity;
    /// <summary>Seconds since the cast began (the release thrust plays in its first fraction of a second).</summary>
    public float CastAge = 99f;
    /// <summary>Written by the pose: the centres of the two palms (Godot world space) and whether they are valid this frame.</summary>
    public Vector3 PalmLeft, PalmRight;
    public bool PalmsValid;
}

/// <summary>
/// The Librarian's chain lightning cast pose (owner, 2026-11-10: "add a firing animation where the librarian holds his hands up in an aggressive pose - hands pointed palms forward towards his target -
/// and shudders with power as he casts"). The install's multiplayer marine rig has no psyker, force or push clip (docs/discovery.md "Librarian cast pose"), so the pose is procedural (**ours**) and laid over
/// whatever the stances, clips and pose layers gave, on the upper body only (the legs keep the movement):
/// <list type="bullet">
/// <item>The chest turns to the target from planted legs (<see cref="TwistDegrees"/>; a standing marine's legs stay while his aim turns), the spine leans toward it (<see cref="LeanDegrees"/>) and both
/// clavicles come up and forward (<see cref="ShrugDegrees"/>): an aggressive, braced stance. The body's axes are read from its shoulders each frame (the clips do not face the skeleton's +Y).</item>
/// <item>Each arm reaches by a two-bone solve (upper arm, forearm) to a wrist point in front of the chest, the hands <see cref="HandSpread"/> apart, elbows out and down; the forearm takes half of the
/// wrist's twist so the wrist never wrings.</item>
/// <item>Each hand turns its palm to the target with the fingers up and splayed outward (<see cref="FingerSplayDegrees"/>), fingers open (their bind pose).</item>
/// <item>A shudder of incommensurate sines (no allocation, no random state) shakes the spine, clavicles, wrist points, hands and fingers, scaled by <see cref="LibrarianCastState.Intensity"/>; a short
/// forward thrust of the hands marks the release.</item>
/// </list>
/// Everything is blended in by the weight (each bone's local rotation slerped from the pose under it), so it fades in and out without a pop. Drawn the same on the own Librarian and on everyone else's.
/// Restore switch: <c>--old-librarian-cast</c> (no cast pose).
/// </summary>
public partial class CharacterViewer
{
    /// <summary>Spine lean toward the target and the clavicle shrug (degrees, ours).</summary>
    public const float LeanDegrees = 9f, ShrugDegrees = 9f, ProtractDegrees = 12f;
    /// <summary>The most the chest turns from the planted legs toward the target (degrees, ours; the arms reach a further 50).</summary>
    public const float TwistDegrees = 80f;
    /// <summary>Wrist distance in front of the shoulders, the hands' lateral spread from the centre line and their drop below shoulder height (metres, ours).</summary>
    public const float Reach = 0.62f, HandSpread = 0.15f, HandDrop = 0.24f;
    /// <summary>An arm reaches at most this share of its length, so the elbows never lock straight (ours).</summary>
    public const float MaxExtension = 0.9f;
    /// <summary>The fingers point up and splay outward by this much (degrees, ours).</summary>
    public const float FingerSplayDegrees = 18f;
    /// <summary>The release thrust: the hands push forward this far over this long at the cast's start (ours).</summary>
    public const float ThrustMetres = 0.07f, ThrustSeconds = 0.16f;

    /// <summary>The Librarian's cast pose, or null (every character that is not a casting Librarian).</summary>
    public LibrarianCastState? LibrarianCast { get; set; }

    CastBones? _castBones;

    /// <summary>The bones the cast moves and the hands' palm frames, resolved once from the rig's bind pose.</summary>
    sealed class CastBones
    {
        public int SpineA, SpineD;
        public readonly Arm L = new(), R = new();
        public bool Ok;
    }

    sealed class Arm
    {
        public int Clavicle = -1, Upper = -1, Fore = -1, Hand = -1;
        public int[] Fingers = Array.Empty<int>();
        /// <summary>In the hand bone's own frame: the direction the fingers point and the palm's outward normal.</summary>
        public Vector3 FingerAxis, PalmAxis;
        /// <summary>The open hand: per finger root (thumb, index, ring) the bind rotation turned so its length points along <see cref="Spread"/> (hand frame); the tips straight on them.</summary>
        public Quaternion[] OpenRoot = Array.Empty<Quaternion>();
        public float Side;   // -1 left, +1 right (the character's right is skeleton +X)
    }

    static readonly bool OldCast = OS.GetCmdlineUserArgs().Contains("--old-librarian-cast");

    partial void ExtrasPoseOverlay()
    {
        if (LibrarianCast is not { } cast) return;   // not a Librarian: nothing (every character outside Musou Mode)
        cast.PalmsValid = false;
        if (_skeleton is null || OldCast) return;
        _castBones ??= ResolveCastBones(_skeleton);
        if (!_castBones.Ok) return;
        float w = Math.Clamp(cast.Weight, 0f, 1f);
        if (w <= 1e-3f) return;
        w = w * w * (3f - 2f * w);   // smoothstep: no kink at either end of the fade
        ApplyCast(_castBones, cast, w);
        _layersDirty = true;   // a held clip is applied again next frame, so the shudder moves and the fade-out is seen
    }

    static CastBones ResolveCastBones(Skeleton3D s)
    {
        var b = new CastBones { SpineA = s.FindBone("RIG_SpineA"), SpineD = s.FindBone("RIG_SpineD") };
        bool ok = b.SpineA >= 0 && b.SpineD >= 0;
        foreach (var (arm, p, side) in new[] { (b.L, "RIG_L_", -1f), (b.R, "RIG_R_", 1f) })
        {
            arm.Side = side;
            arm.Clavicle = s.FindBone(p + "Clavicle"); arm.Upper = s.FindBone(p + "UpperArm"); arm.Fore = s.FindBone(p + "Forearm"); arm.Hand = s.FindBone(p + "Hand");
            int f0 = s.FindBone(p + "Finger0"), f1 = s.FindBone(p + "Finger1"), f2 = s.FindBone(p + "Finger2");
            arm.Fingers = new[] { f0, s.FindBone(p + "Finger01"), f1, s.FindBone(p + "Finger11"), f2, s.FindBone(p + "Finger21") };
            ok &= arm.Clavicle >= 0 && arm.Upper >= 0 && arm.Fore >= 0 && arm.Hand >= 0 && f0 >= 0 && f1 >= 0 && f2 >= 0;
            if (!ok) continue;
            // The hand's frame from the bind pose: the fingers along the mean of the index and ring roots; the palm on the side the bind pose's fingers curl toward (a hand closes toward its palm).
            var i1 = s.GetBoneRest(f1).Origin; var i2 = s.GetBoneRest(f2).Origin;
            var finger = ((i1 + i2) * 0.5f).Normalized();
            var across = (i1 - i2);
            across = (across - finger * across.Dot(finger)).Normalized();
            var palm = finger.Cross(across).Normalized();
            var curl = Vector3.Zero;
            foreach (var (root, tip) in new[] { (f1, arm.Fingers[3]), (f2, arm.Fingers[5]) })
            {
                if (tip < 0) continue;
                var rootRest = s.GetBoneRest(root);
                var run = rootRest.Origin.Normalized();
                var d = (rootRest.Basis * s.GetBoneRest(tip).Origin).Normalized();   // the tip's run in the hand frame
                curl += d - run * d.Dot(run);
            }
            if (curl.LengthSquared() > 1e-6f && palm.Dot(curl) < 0f) palm = -palm;
            else if (curl.LengthSquared() <= 1e-6f && palm.Dot(s.GetBoneRest(f0).Origin) < 0f) palm = -palm;   // no curl to read: the thumb's side
            arm.FingerAxis = finger; arm.PalmAxis = palm;
            // The open hand: each root's length (its +X, toward its tip) along the finger axis, the index a little to its side, the ring to the other, the thumb out to the side and up off the palm.
            arm.OpenRoot = new Quaternion[3];
            var roots = new[] { f0, f1, f2 };
            var wants = new[] { (finger * 0.55f + across * 0.75f - palm * 0.2f).Normalized(), (finger + across * 0.14f).Normalized(), (finger - across * 0.14f).Normalized() };
            for (int k = 0; k < 3; k++)
            {
                var rest = s.GetBoneRest(roots[k]).Basis.Orthonormalized().GetRotationQuaternion();
                var length = rest * new Vector3(1f, 0f, 0f);
                arm.OpenRoot[k] = (Arc(length, wants[k]) * rest).Normalized();
            }
        }
        b.Ok = ok;
        return b;
    }

    // ---- the pose ----

    static readonly Vector3 Up = new(0f, 0f, 1f), Forward = new(0f, 1f, 0f), Right = new(1f, 0f, 0f);   // skeleton space: Z up (the body's front and right are read from the shoulders each frame)

    /// <summary>A smooth shudder value in about [-1, 1]: three incommensurate sines (Hz) with a per-channel phase.</summary>
    static float Shudder(float t, float seed, float fast = 1f) =>
        0.5f * MathF.Sin(t * 23.7f * fast + seed * 1.7f) + 0.3f * MathF.Sin(t * 31.3f * fast + seed * 3.1f) + 0.2f * MathF.Sin(t * 13.1f * fast + seed * 5.3f);

    void ApplyCast(CastBones b, LibrarianCastState cast, float w)
    {
        var s = _skeleton!;
        float t = (float)(Time.GetTicksUsec() * 1e-6 % 1000.0);
        float shake = Math.Clamp(cast.Intensity, 0f, 1.5f);
        var toSkeleton = s.GlobalTransform.AffineInverse();
        var target = toSkeleton * cast.Target;
        // The body's own frame from the pose under the cast: its right from the left shoulder to the right one (flat), its front square to that. The clips do not face the skeleton's +Y (measured: the
        // shoulders lie along its Y axis), so no axis is assumed.
        BodyFrame(b, out var bodyRight, out var bodyFront);
        float deg = MathF.PI / 180f;

        // 0. A standing marine's legs stay planted while his aim turns (up to 135 degrees, the turn in place): the chest turns to the target, up to TwistDegrees, split over the two spine bones.
        var toTarget = target - (s.GetBoneGlobalPose(b.L.Upper).Origin + s.GetBoneGlobalPose(b.R.Upper).Origin) * 0.5f;
        float off = MathF.Atan2(toTarget.Dot(bodyRight), toTarget.Dot(bodyFront));
        float twist = -Math.Clamp(off, -TwistDegrees * deg, TwistDegrees * deg) * w;   // about Up, negative turns the front toward the right
        if (MathF.Abs(twist) > 1e-4f)
        {
            GlobalTurn(b.SpineA, new Quaternion(Up, twist * 0.4f));
            GlobalTurn(b.SpineD, new Quaternion(Up, twist * 0.6f));
            BodyFrame(b, out bodyRight, out bodyFront);
        }

        // 1. The spine leans toward the target, with a tremor.
        float lean = (LeanDegrees + 1.2f * shake * Shudder(t, 1f)) * deg * w;
        float roll = 1.0f * shake * Shudder(t, 2f) * deg * w, yawShake = 0.8f * shake * Shudder(t, 3f) * deg * w;
        GlobalTurn(b.SpineA, new Quaternion(bodyRight, -lean * 0.35f));   // about the right axis, negative: the top tips toward the front
        GlobalTurn(b.SpineD, (new Quaternion(bodyRight, -lean * 0.65f) * new Quaternion(bodyFront, roll) * new Quaternion(Up, yawShake)).Normalized());

        // 2. The clavicles come up and forward.
        foreach (var arm in new[] { b.L, b.R })
        {
            var c = s.GetBoneGlobalPose(arm.Clavicle);
            var u = s.GetBoneGlobalPose(arm.Upper).Origin;
            var dir = (u - c.Origin).Normalized();
            float shrug = (ShrugDegrees + 2.5f * shake * Shudder(t, 4f + arm.Side)) * deg * w, protract = ProtractDegrees * deg * w;
            var want = (dir + Up * MathF.Tan(shrug) + bodyFront * MathF.Tan(protract)).Normalized();
            GlobalTurn(arm.Clavicle, Arc(dir, want));
        }

        // 3. Where the hands go: in front of the chest, toward the target (clamped to a cone in front of the body).
        var shoulderL = s.GetBoneGlobalPose(b.L.Upper).Origin; var shoulderR = s.GetBoneGlobalPose(b.R.Upper).Origin;
        var chest = (shoulderL + shoulderR) * 0.5f;
        var aim = ClampAim(target - chest, bodyFront, bodyRight);
        var lateral = aim.Cross(Up).Normalized();   // the character's right, across the aim
        var upAim = lateral.Cross(aim).Normalized();
        float thrust = cast.CastAge < ThrustSeconds ? ThrustMetres * MathF.Sin(MathF.PI * cast.CastAge / ThrustSeconds) : 0f;
        var centre = chest + aim * (Reach + thrust) - upAim * HandDrop;
        centre += (lateral * Shudder(t, 6f, 1.3f) + upAim * Shudder(t, 7f, 1.3f) + aim * Shudder(t, 8f)) * (0.012f * shake);

        foreach (var arm in new[] { b.L, b.R })
        {
            var wrist = centre + lateral * (HandSpread * arm.Side) + upAim * (0.008f * shake * Shudder(t, 9f + arm.Side * 3f, 1.6f));
            var pole = lateral * arm.Side - upAim * 0.6f - aim * 0.3f;   // elbows out, down and a little back
            // The palm faces the target; the fingers point up and splay outward.
            var palm = (target - wrist).Normalized();
            if (palm.LengthSquared() < 0.5f) palm = aim;
            var fingersUp = (upAim - palm * upAim.Dot(palm)).Normalized();
            var outward = (lateral * arm.Side - palm * (lateral * arm.Side).Dot(palm)).Normalized();
            float splay = (FingerSplayDegrees + 3f * shake * Shudder(t, 10f + arm.Side)) * deg;
            var fingers = (fingersUp * MathF.Cos(splay) + outward * MathF.Sin(splay)).Normalized();
            ReachArm(arm, wrist, pole, palm, fingers, w, shake, t);
        }

        // 4. The palms' centres for the bolts (on the palm, a hand's breadth out from the wrist).
        var handL = s.GetBoneGlobalPose(b.L.Hand); var handR = s.GetBoneGlobalPose(b.R.Hand);
        cast.PalmLeft = s.GlobalTransform * PalmCentre(handL, b.L);
        cast.PalmRight = s.GlobalTransform * PalmCentre(handR, b.R);
        cast.PalmsValid = true;
        if (CastLog && Time.GetTicksMsec() - _castLoggedAt > 500)
        {
            _castLoggedAt = Time.GetTicksMsec();
            foreach (var arm in new[] { b.L, b.R })
            {
                var S = s.GetBoneGlobalPose(arm.Upper).Origin; var E = s.GetBoneGlobalPose(arm.Fore).Origin; var H = s.GetBoneGlobalPose(arm.Hand);
                float bend = MathF.Acos(Math.Clamp((E - S).Normalized().Dot((H.Origin - E).Normalized()), -1f, 1f)) / deg;
                var palmDir = (H.Basis * arm.PalmAxis).Normalized(); var fingerDir = (H.Basis * arm.FingerAxis).Normalized();
                var flat = new Vector3(target.X - S.X, target.Y - S.Y, 0f).Normalized();
                GD.Print($"LIBCAST {(arm.Side < 0 ? "L" : "R")} w {w:F2} front {bodyFront.X:F2},{bodyFront.Y:F2} target off the front {MathF.Atan2(flat.Dot(bodyRight), flat.Dot(bodyFront)) / deg:F0} deg; shoulder {S.X:F2},{S.Y:F2},{S.Z:F2} elbow {E.X:F2},{E.Y:F2},{E.Z:F2} wrist {H.Origin.X:F2},{H.Origin.Y:F2},{H.Origin.Z:F2} bend {bend:F0} deg; " +
                    $"palm {palmDir.X:F2},{palmDir.Y:F2},{palmDir.Z:F2} (to target {palmDir.Dot((target - H.Origin).Normalized()):F2}) fingers {fingerDir.X:F2},{fingerDir.Y:F2},{fingerDir.Z:F2}; target {target.X:F1},{target.Y:F1},{target.Z:F1}");
            }
        }
    }

    static readonly bool CastLog = OS.GetCmdlineUserArgs().Contains("--librarian-cast-log");
    ulong _castLoggedAt;

    /// <summary>The upper body's flat frame now: its right from the left shoulder to the right one, its front square to that (Z up).</summary>
    void BodyFrame(CastBones b, out Vector3 right, out Vector3 front)
    {
        var across = _skeleton!.GetBoneGlobalPose(b.R.Upper).Origin - _skeleton.GetBoneGlobalPose(b.L.Upper).Origin;
        across.Z = 0f;
        right = across.LengthSquared() > 1e-6f ? across.Normalized() : Right;
        front = Up.Cross(right).Normalized();
    }

    static Vector3 PalmCentre(Transform3D hand, Arm arm) => hand.Origin + hand.Basis * (arm.FingerAxis * 0.085f + arm.PalmAxis * 0.05f);

    /// <summary>The aim direction from the chest, kept within 50 degrees of the body's front (<paramref name="front"/>, <paramref name="right"/>: the body's flat axes) and between 40 degrees down and 35 up.</summary>
    static Vector3 ClampAim(Vector3 v, Vector3 front, Vector3 right)
    {
        if (v.LengthSquared() < 1e-6f) return front;
        v = v.Normalized();
        float yaw = MathF.Atan2(v.Dot(right), v.Dot(front)), pitch = MathF.Asin(Math.Clamp(v.Z, -1f, 1f));
        yaw = Math.Clamp(yaw, -50f * MathF.PI / 180f, 50f * MathF.PI / 180f);
        pitch = Math.Clamp(pitch, -40f * MathF.PI / 180f, 35f * MathF.PI / 180f);
        float cp = MathF.Cos(pitch);
        return right * (MathF.Sin(yaw) * cp) + front * (MathF.Cos(yaw) * cp) + Up * MathF.Sin(pitch);
    }

    /// <summary>The shortest rotation taking unit vector <paramref name="from"/> to <paramref name="to"/>.</summary>
    static Quaternion Arc(Vector3 from, Vector3 to)
    {
        float d = Math.Clamp(from.Dot(to), -1f, 1f);
        if (d > 0.99999f) return Quaternion.Identity;
        var axis = from.Cross(to);
        if (axis.LengthSquared() < 1e-10f) axis = MathF.Abs(from.X) < 0.9f ? from.Cross(Right) : from.Cross(Forward);
        return new Quaternion(axis.Normalized(), MathF.Acos(d));
    }

    /// <summary>Turns a bone by a rotation given in skeleton space, about its own origin (its parent's pose is already final).</summary>
    void GlobalTurn(int bone, Quaternion turn)
    {
        var s = _skeleton!;
        int parent = s.GetBoneParent(bone);
        var parentRot = parent >= 0 ? s.GetBoneGlobalPose(parent).Basis.Orthonormalized().GetRotationQuaternion() : Quaternion.Identity;
        s.SetBonePoseRotation(bone, (parentRot.Inverse() * turn * parentRot * s.GetBonePoseRotation(bone)).Normalized());
    }

    static Transform3D LocalOf(Skeleton3D s, int bone) => new(new Basis(s.GetBonePoseRotation(bone)).Scaled(s.GetBonePoseScale(bone)), s.GetBonePosePosition(bone));

    /// <summary>
    /// One arm to <paramref name="wrist"/> (skeleton space) by the two-bone solve, elbow toward <paramref name="pole"/>, the hand's palm along <paramref name="palm"/> and fingers along
    /// <paramref name="fingers"/>; every changed bone's local rotation is slerped from its pose under the cast by <paramref name="w"/>.
    /// </summary>
    void ReachArm(Arm arm, Vector3 wrist, Vector3 pole, Vector3 palm, Vector3 fingers, float w, float shake, float t)
    {
        var s = _skeleton!;
        var clav = s.GetBoneGlobalPose(arm.Clavicle);
        var upperL = LocalOf(s, arm.Upper); var foreL = LocalOf(s, arm.Fore); var handL = LocalOf(s, arm.Hand);
        var upperG = clav * upperL; var foreG = upperG * foreL; var handG = foreG * handL;
        var S = upperG.Origin; var E = foreG.Origin; var W = handG.Origin;
        float a = S.DistanceTo(E), bLen = E.DistanceTo(W);
        if (a < 1e-3f || bLen < 1e-3f) return;
        var toT = wrist - S;
        float dist = toT.Length();
        if (dist < 1e-4f) return;
        var dir = toT / dist;
        float d = Math.Clamp(dist, MathF.Abs(a - bLen) + 1e-3f, (a + bLen) * MaxExtension);   // farther: the hand stops short on the same line
        var p = pole - dir * pole.Dot(dir);
        p = p.LengthSquared() > 1e-8f ? p.Normalized() : (MathF.Abs(dir.Z) < 0.9f ? -Up : Forward);
        float x = (a * a - bLen * bLen + d * d) / (2f * d);
        float h = MathF.Sqrt(MathF.Max(0f, a * a - x * x));
        var elbow = S + dir * x + p * h;
        var hand = S + dir * d;

        // The upper arm aims at the new elbow, the forearm at the wrist.
        var upperBasis = new Basis(Arc((E - S) / a, (elbow - S).Normalized())) * upperG.Basis;
        var upperNew = new Transform3D(upperBasis, S);
        var foreNew = upperNew * foreL;
        var handNow = (foreNew * handL).Origin;
        var foreBasis = new Basis(Arc((handNow - foreNew.Origin).Normalized(), (hand - foreNew.Origin).Normalized())) * foreNew.Basis;

        // The hand's wanted frame: (finger axis, palm axis) of the bind pose onto (fingers, palm), with a tremor.
        var palmUnit = (palm - fingers * palm.Dot(fingers)).Normalized();
        var wantG = new Basis(fingers, palmUnit, fingers.Cross(palmUnit));
        var fa = arm.FingerAxis; var pa = (arm.PalmAxis - fa * arm.PalmAxis.Dot(fa)).Normalized();
        var localFrame = new Basis(fa, pa, fa.Cross(pa));
        float deg = MathF.PI / 180f;
        var tremor = new Quaternion(palmUnit, 3f * shake * Shudder(t, 11f + arm.Side, 1.4f) * deg) * new Quaternion(fingers.Cross(palmUnit), 3f * shake * Shudder(t, 12f + arm.Side, 1.4f) * deg);
        var handWant = (new Basis(tremor.Normalized()) * wantG * localFrame.Transposed()).Orthonormalized();

        // The forearm takes half of the wrist's twist about its own axis (the hand stays where it is), so the wrist does not wring.
        var foreRot = foreBasis.Orthonormalized().GetRotationQuaternion();
        var rel = (foreRot.Inverse() * handWant.GetRotationQuaternion()).Normalized();
        var axisLocal = handL.Origin.Normalized();
        var proj = axisLocal * new Vector3(rel.X, rel.Y, rel.Z).Dot(axisLocal);
        var twist = new Quaternion(proj.X, proj.Y, proj.Z, rel.W);
        if (twist.LengthSquared() > 1e-8f)
        {
            twist = twist.Normalized();
            foreRot = (foreRot * Quaternion.Identity.Slerp(twist, 0.5f)).Normalized();
        }

        // Back to local rotations, slerped from the pose under the cast.
        var clavRot = clav.Basis.Orthonormalized().GetRotationQuaternion();
        var upperRot = upperBasis.Orthonormalized().GetRotationQuaternion();
        Blend(arm.Upper, (clavRot.Inverse() * upperRot).Normalized(), w);
        Blend(arm.Fore, (upperRot.Inverse() * foreRot).Normalized(), w);
        Blend(arm.Hand, (foreRot.Inverse() * handWant.GetRotationQuaternion()).Normalized(), w);

        // Open, spread fingers, quivering: each root along its open direction, each tip straight on its root.
        for (int k = 0; k < arm.Fingers.Length; k++)
        {
            int f = arm.Fingers[k];
            if (f < 0) continue;
            var quiver = new Quaternion(new Vector3(0f, 0f, 1f), 5f * shake * Shudder(t, 20f + k + arm.Side * 7f, 1.8f) * deg);
            var open = k % 2 == 0 ? arm.OpenRoot[k / 2] : Quaternion.Identity;
            Blend(f, (open * quiver).Normalized(), w);
        }
    }

    void Blend(int bone, Quaternion target, float w)
    {
        var s = _skeleton!;
        var now = s.GetBonePoseRotation(bone);
        if (now.Dot(target) < 0f) target = -target;
        s.SetBonePoseRotation(bone, now.Slerp(target, w).Normalized());
    }
}
