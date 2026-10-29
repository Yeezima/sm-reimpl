using System.Numerics;

namespace SmFormats.Havok;

/// <summary>How Havok integrates a ragdoll body (the motion's type byte): 2 sphere inertia, 3 box inertia.</summary>
public enum RagdollMotionKind : byte { Invalid = 0, Dynamic = 1, SphereInertia = 2, BoxInertia = 3, Keyframed = 4, Fixed = 5, ThinBoxInertia = 6, Character = 7 }

/// <summary>
/// A ragdoll rigid body's physics (M23), in the body's own frame: the frame of its rest transform (<see cref="RagdollBody.Origin"/>,
/// <see cref="RagdollBody.Rotation"/>), whose origin is the body's ragdoll bone. <see cref="Inertia"/> is the diagonal inertia
/// tensor in that frame (kg·m² if the mass is in kg; the file stores the inverses), <see cref="CenterOfMass"/> the centre of
/// mass in that frame. Friction and restitution are the body's material; damping and the gravity factor are Havok half floats.
/// <see cref="CollisionFilterInfo"/> is Havok's group-filter word: layer = bits 0–4, subsystem id = bits 5–9, the subsystem it
/// does not collide with = bits 10–14, system group = bits 16–31 (the marine's bodies do not collide with their parent body).
/// </summary>
public sealed record RagdollBodyPhysics(string Name, RagdollMotionKind Motion, float Mass, Vector3 Inertia, Vector3 CenterOfMass,
    float Friction, float Restitution, float LinearDamping, float AngularDamping, float GravityFactor, uint CollisionFilterInfo)
{
    public int Layer => (int)(CollisionFilterInfo & 31);
    public int SubSystemId => (int)((CollisionFilterInfo >> 5) & 31);
    public int NoCollideWith => (int)((CollisionFilterInfo >> 10) & 31);
    public int SystemGroup => (int)(CollisionFilterInfo >> 16);
}

/// <summary>The constraint classes the reader knows: <c>hkpRagdollConstraintData</c> and <c>hkpLimitedHingeConstraintData</c>.</summary>
public enum RagdollJointKind : byte { Ragdoll, LimitedHinge }

/// <summary>
/// A ragdoll constraint (M23): <see cref="BodyA"/> is the child body (index into the ragdoll's bodies; Havok's entity A),
/// <see cref="BodyB"/> its parent. Each side has a constraint frame in that body's own frame: a pivot and a rotation whose
/// local X axis (<see cref="AxisA"/>/<see cref="AxisB"/>) is the twist axis of a ragdoll joint or the hinge axis, and whose
/// local Y axis (<see cref="PlaneAxisA"/>/<see cref="PlaneAxisB"/>) is a ragdoll joint's plane axis or the hinge's zero-angle
/// reference (Havok's atoms: twist limit twist axis 0, reference axis 1; cone limit axes 0/0 measured from alignment; plane
/// limit axes 0/1 measured from perpendicular; hinge free axis 0, angle limit axis 0). Angles in radians.
/// Ragdoll joint: <see cref="MinAngle"/>..<see cref="MaxAngle"/> is the twist range about the twist axis, <see cref="ConeAngle"/>
/// the largest angle between A's twist axis and B's, and <see cref="PlaneMin"/>..<see cref="PlaneMax"/> the range of the angle
/// between A's twist axis and B's plane, zero when they are perpendicular (Havok's cone atom with the plane axis as reference).
/// Limited hinge: <see cref="MinAngle"/>..<see cref="MaxAngle"/> is the hinge angle; cone and plane are 0.
/// <see cref="MaxFrictionTorque"/>: the joint's angular friction (all three axes of a ragdoll joint, the hinge axis of a hinge);
/// <see cref="LimitTau"/>: the limits' stiffness factor (Havok's angular limits tau). The motors (position motors for powered
/// ragdolls) are present but disabled in the file and are not read.
/// </summary>
public sealed record RagdollJoint(string? Name, RagdollJointKind Kind, int BodyA, int BodyB, Vector3 PivotA, Quaternion FrameA, Vector3 PivotB, Quaternion FrameB,
    float MinAngle, float MaxAngle, float ConeAngle, float PlaneMin, float PlaneMax, float MaxFrictionTorque, float LimitTau)
{
    public Vector3 AxisA => Vector3.Transform(Vector3.UnitX, FrameA);
    public Vector3 AxisB => Vector3.Transform(Vector3.UnitX, FrameB);
    public Vector3 PlaneAxisA => Vector3.Transform(Vector3.UnitY, FrameA);
    public Vector3 PlaneAxisB => Vector3.Transform(Vector3.UnitY, FrameB);
}

/// <summary>
/// One simple mapping of the ragdoll-to-animation skeleton mapper (the <c>hkaSkeletonMapper</c> whose skeleton A is the
/// ragdoll's): the animation bone <see cref="AnimationBone"/> follows ragdoll bone <see cref="RagdollBone"/> (index into
/// <see cref="RagdollPhysics.SkeletonBones"/>) with <see cref="Translation"/>/<see cref="Rotation"/>, the animation bone's
/// transform in the ragdoll bone's frame (Havok's aFromBTransform): animation bone model transform = this × ragdoll bone model
/// transform (System.Numerics row vectors). Checked on the install: every mapping reproduces the animation skeleton's reference
/// pose from the ragdoll's to 3e-6.
/// </summary>
public sealed record RagdollBoneMapping(int RagdollBone, string AnimationBone, Vector3 Translation, Quaternion Rotation);

/// <summary>A chain mapping: the animation bones from <see cref="AnimationStart"/> down to <see cref="AnimationEnd"/> follow the ragdoll chain <see cref="RagdollStart"/>..<see cref="RagdollEnd"/> (Havok spreads them along it).</summary>
public sealed record RagdollChainMapping(int RagdollStart, int RagdollEnd, string AnimationStart, string AnimationEnd,
    Vector3 StartTranslation, Quaternion StartRotation, Vector3 EndTranslation, Quaternion EndRotation);

/// <summary>
/// What a physics ragdoll needs beyond the shapes (<see cref="HavokRagdoll.Read"/>): per body (same order) its physics, the
/// ragdoll skeleton (bone names, parents; <see cref="BoneToBody"/>: ragdoll bone → body index, -1 none; a body's rest transform
/// is its bone's model-space reference pose), the joints, and the mapping onto the animation skeleton
/// (<see cref="AnimationSkeleton"/> names it). Mapping lists are empty when the file has no mapper from the ragdoll skeleton.
/// </summary>
public sealed record RagdollPhysics(IReadOnlyList<RagdollBodyPhysics> Bodies, IReadOnlyList<RagdollJoint> Joints, IReadOnlyList<string> SkeletonBones,
    IReadOnlyList<short> SkeletonParents, IReadOnlyList<int> BoneToBody, IReadOnlyList<RagdollBoneMapping> ToAnimation, IReadOnlyList<RagdollChainMapping> Chains,
    IReadOnlyList<string> UnmappedAnimationBones, string? AnimationSkeleton);

public static partial class HavokRagdoll
{
    const int MaxJoints = 256, MaxMappings = 1024;

    /// <summary>
    /// Reads the ragdoll's physics (null when the packfile has no <c>hkaRagdollInstance</c>). Layout per docs/discovery.md
    /// ("Ragdoll and hit shapes"), established on the install's own marine rig (hk_2010.2.0-r1, 32-bit); no Havok SDK code.
    /// Ragdoll instance: rigid bodies @8, constraints @20 (pointer arrays), bone-to-body map @32 (int array), skeleton @44.
    /// Rigid body: collision filter info @44, friction @144, restitution @148; its motion @224: type @232, centre of mass in the
    /// body frame @368, linear and angular damping (half floats) @404/@406, inverse inertia (xyz) and inverse mass (w) @416,
    /// gravity factor (half) @510. Constraint instance: data @12, entities A @20 and B @24, name @40. Constraint data:
    /// transforms atom @16 (frame A rotation columns @32/@48/@64, pivot @80; frame B @96/@112/@128, pivot @144); ragdoll data:
    /// friction atom @256 (max torque @264), twist limit @268 (min/max @276/@280, tau @284), cone limit @288 (max @300), plane
    /// limit @308 (min/max @316/@320); limited hinge: friction atom @196 (max torque @204), angle limit @208 (min/max
    /// @212/@216, tau @220), 2D angular atom @224. Atom types and axes are checked; another constraint class is skipped with a
    /// note. Skeleton mapper: skeleton A @16, B @20, simple mappings @24 (64 bytes: bone A, bone B, transform @16), chains @36
    /// (112 bytes: start/end A, start/end B, transforms @16/@64), unmapped B bones @48.
    /// </summary>
    public static RagdollPhysics? ReadPhysics(HavokPackfile pack, List<string>? notes = null)
    {
        notes ??= [];
        var instance = pack.OfClass("hkaRagdollInstance").FirstOrDefault();
        if (instance is null) return null;
        var classes = pack.Objects.ToDictionary(o => o.Offset, o => o.ClassName);
        var (bodiesAt, count) = pack.Array(instance.Offset + 8, 4);
        if (count > MaxBodies) throw new InvalidDataException($"Implausible ragdoll body count {count}.");
        var bodyOffsets = new int[count];
        var bodies = new RagdollBodyPhysics[count];
        for (int b = 0; b < count; b++)
        {
            int body = bodyOffsets[b] = pack.Pointer(bodiesAt!.Value + 4 * b) ?? throw new InvalidDataException($"Ragdoll body {b}: null pointer.");
            if (!classes.TryGetValue(body, out var cls) || cls != "hkpRigidBody") throw new InvalidDataException($"Ragdoll body {b} is {cls ?? "no object"}, not hkpRigidBody.");
            bodies[b] = BodyPhysics(pack, body, pack.String(body + 120) ?? $"body{b}");
        }

        // The ragdoll skeleton and which body each of its bones is.
        int skeletonAt = pack.Pointer(instance.Offset + 44) ?? throw new InvalidDataException("Ragdoll instance without a skeleton.");
        if (!classes.TryGetValue(skeletonAt, out var skeletonClass) || skeletonClass != "hkaSkeleton") throw new InvalidDataException("Ragdoll skeleton is not an hkaSkeleton.");
        var skeleton = HavokSkeleton.Read(pack, skeletonAt);
        var (mapAt, mapCount) = pack.Array(instance.Offset + 32, 4);
        if (mapCount != skeleton.BoneNames.Count) throw new InvalidDataException($"Ragdoll: {mapCount} bone-to-body entries for {skeleton.BoneNames.Count} bones.");
        var boneToBody = new int[mapCount];
        for (int i = 0; i < mapCount; i++)
        {
            int v = pack.ReadInt32(mapAt!.Value + 4 * i);
            boneToBody[i] = v >= -1 && v < count ? v : throw new InvalidDataException($"Ragdoll bone {i} maps to body {v} of {count}.");
        }

        // Joints.
        var (jointsAt, jointCount) = pack.Array(instance.Offset + 20, 4);
        if (jointCount > MaxJoints) throw new InvalidDataException($"Implausible ragdoll constraint count {jointCount}.");
        var joints = new List<RagdollJoint>(jointCount);
        for (int j = 0; j < jointCount; j++)
        {
            int c = pack.Pointer(jointsAt!.Value + 4 * j) ?? throw new InvalidDataException($"Ragdoll constraint {j}: null pointer.");
            if (!classes.TryGetValue(c, out var cls) || cls != "hkpConstraintInstance") throw new InvalidDataException($"Ragdoll constraint {j} is {cls ?? "no object"}, not hkpConstraintInstance.");
            int data = pack.Pointer(c + 12) ?? throw new InvalidDataException($"Ragdoll constraint {j}: no data.");
            int a = Array.IndexOf(bodyOffsets, pack.Pointer(c + 20) ?? -1), b = Array.IndexOf(bodyOffsets, pack.Pointer(c + 24) ?? -1);
            if (a < 0 || b < 0 || a == b) throw new InvalidDataException($"Ragdoll constraint {j}: its bodies are not two of the ragdoll's.");
            string name = pack.String(c + 40) ?? $"joint{j}";
            string kind = classes.TryGetValue(data, out var k) ? k : "?";
            switch (kind)
            {
                case "hkpRagdollConstraintData": joints.Add(RagdollData(pack, data, name, a, b)); break;
                case "hkpLimitedHingeConstraintData": joints.Add(HingeData(pack, data, name, a, b)); break;
                default: notes.Add($"ragdoll constraint '{name}': {kind} not read"); break;
            }
        }

        // The mapper from the ragdoll skeleton onto another (the animation skeleton).
        var mappings = new List<RagdollBoneMapping>();
        var chains = new List<RagdollChainMapping>();
        var unmapped = new List<string>();
        string? animationSkeleton = null;
        var mapper = pack.OfClass("hkaSkeletonMapper").FirstOrDefault(m => pack.Pointer(m.Offset + 16) == skeletonAt && pack.Pointer(m.Offset + 20) is { } other && other != skeletonAt);
        if (mapper is null) notes.Add("ragdoll: no skeleton mapper from the ragdoll skeleton");
        else
        {
            int targetAt = pack.Pointer(mapper.Offset + 20)!.Value;
            if (!classes.TryGetValue(targetAt, out var targetClass) || targetClass != "hkaSkeleton") throw new InvalidDataException("Ragdoll mapper: skeleton B is not an hkaSkeleton.");
            var target = HavokSkeleton.Read(pack, targetAt);
            animationSkeleton = target.Name;
            int bones = skeleton.BoneNames.Count, targetBones = target.BoneNames.Count;
            var (simpleAt, simpleCount) = pack.Array(mapper.Offset + 24, 64);
            var (chainAt, chainCount) = pack.Array(mapper.Offset + 36, 112);
            var (unmappedAt, unmappedCount) = pack.Array(mapper.Offset + 48, 2);
            if (simpleCount > MaxMappings || chainCount > MaxMappings || unmappedCount > MaxMappings) throw new InvalidDataException("Ragdoll mapper: implausible sizes.");
            for (int i = 0; i < simpleCount; i++)
            {
                int e = simpleAt!.Value + 64 * i;
                int boneA = pack.ReadInt16(e), boneB = pack.ReadInt16(e + 2);
                if (boneA < 0 || boneA >= bones || boneB < 0 || boneB >= targetBones) throw new InvalidDataException($"Ragdoll mapper: mapping {i} names bones {boneA}/{boneB}.");
                var t = Transform(pack, e + 16, $"mapping {i}");
                mappings.Add(new RagdollBoneMapping(boneA, target.BoneNames[boneB], t.Translation, t.Rotation));
            }
            for (int i = 0; i < chainCount; i++)
            {
                int e = chainAt!.Value + 112 * i;
                int sa = pack.ReadInt16(e), ea = pack.ReadInt16(e + 2), sb = pack.ReadInt16(e + 4), eb = pack.ReadInt16(e + 6);
                if (sa < 0 || sa >= bones || ea < 0 || ea >= bones || sb < 0 || sb >= targetBones || eb < 0 || eb >= targetBones)
                    throw new InvalidDataException($"Ragdoll mapper: chain {i} names bones out of range.");
                var t0 = Transform(pack, e + 16, $"chain {i}"); var t1 = Transform(pack, e + 64, $"chain {i}");
                chains.Add(new RagdollChainMapping(sa, ea, target.BoneNames[sb], target.BoneNames[eb], t0.Translation, t0.Rotation, t1.Translation, t1.Rotation));
            }
            for (int i = 0; i < unmappedCount; i++)
            {
                int bone = pack.ReadInt16(unmappedAt!.Value + 2 * i);
                unmapped.Add(bone >= 0 && bone < targetBones ? target.BoneNames[bone] : throw new InvalidDataException($"Ragdoll mapper: unmapped bone {bone}."));
            }
        }
        return new RagdollPhysics(bodies, joints, skeleton.BoneNames, skeleton.Parents, boneToBody, mappings, chains, unmapped, animationSkeleton);
    }

    static RagdollBodyPhysics BodyPhysics(HavokPackfile pack, int body, string name)
    {
        var motion = (RagdollMotionKind)pack.ReadByte(body + 232);
        var inverse = Vector(pack, body + 416);
        float inverseMass = pack.ReadSingle(body + 428);
        if (!(float.IsFinite(inverseMass) && inverseMass > 0f) || !Finite(inverse) || inverse.X <= 0f || inverse.Y <= 0f || inverse.Z <= 0f)
            throw new InvalidDataException($"Ragdoll body '{name}': no mass or inertia (inverse mass {inverseMass}, inverse inertia {inverse}).");
        var com = Vector(pack, body + 368);
        float friction = pack.ReadSingle(body + 144), restitution = pack.ReadSingle(body + 148);
        float linear = Half(pack, body + 404), angular = Half(pack, body + 406), gravity = Half(pack, body + 510);
        if (!Finite(com) || !NonNegative(friction) || !NonNegative(restitution) || !NonNegative(linear) || !NonNegative(angular) || !float.IsFinite(gravity))
            throw new InvalidDataException($"Ragdoll body '{name}': implausible centre of mass, material or damping.");
        return new RagdollBodyPhysics(name, motion, 1f / inverseMass, new Vector3(1f / inverse.X, 1f / inverse.Y, 1f / inverse.Z), com,
            friction, restitution, linear, angular, gravity, pack.ReadUInt32(body + 44));
    }

    static RagdollJoint RagdollData(HavokPackfile pack, int d, string name, int a, int b)
    {
        // Atoms: transforms 2, stabilization 23, ragdoll motor 19, angular friction 17, twist 15, cone 16, plane (cone) 16, ball-and-socket 5.
        CheckAtoms(pack, d, name, (16, 2), (160, 23), (176, 19), (256, 17), (268, 15), (288, 16), (308, 16), (328, 5));
        if (pack.ReadByte(d + 271) != 0 || pack.ReadByte(d + 272) != 1 || pack.ReadByte(d + 291) != 0 || pack.ReadByte(d + 292) != 0 || pack.ReadByte(d + 293) != 0
            || pack.ReadByte(d + 311) != 0 || pack.ReadByte(d + 312) != 1 || pack.ReadByte(d + 313) != 1)
            throw new InvalidDataException($"Ragdoll constraint '{name}': twist, cone or plane atom axes are not the documented ones.");
        var (pivotA, frameA, pivotB, frameB) = Frames(pack, d, name);
        float twistMin = pack.ReadSingle(d + 276), twistMax = pack.ReadSingle(d + 280), cone = pack.ReadSingle(d + 300);
        float planeMin = pack.ReadSingle(d + 316), planeMax = pack.ReadSingle(d + 320);
        float friction = pack.ReadSingle(d + 264), tau = pack.ReadSingle(d + 284);
        if (!Angles(twistMin, twistMax) || !Angles(planeMin, planeMax) || !(cone >= 0f && cone <= MathF.PI) || !NonNegative(friction) || !NonNegative(tau))
            throw new InvalidDataException($"Ragdoll constraint '{name}': implausible limits.");
        return new RagdollJoint(name, RagdollJointKind.Ragdoll, a, b, pivotA, frameA, pivotB, frameB, twistMin, twistMax, cone, planeMin, planeMax, friction, tau);
    }

    static RagdollJoint HingeData(HavokPackfile pack, int d, string name, int a, int b)
    {
        // Atoms: transforms 2, stabilization 23, angular motor 18, angular friction 17, angular limit 14, 2D angular 12, ball-and-socket 5.
        CheckAtoms(pack, d, name, (16, 2), (160, 23), (176, 18), (196, 17), (208, 14), (224, 12), (228, 5));
        if (pack.ReadByte(d + 211) != 0 || pack.ReadByte(d + 226) != 0)
            throw new InvalidDataException($"Ragdoll constraint '{name}': hinge axis atoms are not the documented ones.");
        var (pivotA, frameA, pivotB, frameB) = Frames(pack, d, name);
        float min = pack.ReadSingle(d + 212), max = pack.ReadSingle(d + 216), friction = pack.ReadSingle(d + 204), tau = pack.ReadSingle(d + 220);
        if (!Angles(min, max) || !NonNegative(friction) || !NonNegative(tau)) throw new InvalidDataException($"Ragdoll constraint '{name}': implausible limits.");
        return new RagdollJoint(name, RagdollJointKind.LimitedHinge, a, b, pivotA, frameA, pivotB, frameB, min, max, 0f, 0f, 0f, friction, tau);
    }

    static void CheckAtoms(HavokPackfile pack, int d, string name, params (int At, short Type)[] atoms)
    {
        foreach (var (at, type) in atoms)
            if (pack.ReadInt16(d + at) != type)
                throw new InvalidDataException($"Ragdoll constraint '{name}': atom type {pack.ReadInt16(d + at)} at {at}, expected {type}.");
    }

    static (Vector3 PivotA, Quaternion FrameA, Vector3 PivotB, Quaternion FrameB) Frames(HavokPackfile pack, int d, string name)
    {
        var pivotA = Vector(pack, d + 80); var pivotB = Vector(pack, d + 144);
        if (!Finite(pivotA) || !Finite(pivotB)) throw new InvalidDataException($"Ragdoll constraint '{name}': a pivot is not finite.");
        return (pivotA, Rotation(pack, d + 32, name), pivotB, Rotation(pack, d + 96, name));
    }

    /// <summary>Three column vectors (16 bytes apart) of a rotation, checked to be orthonormal and right-handed.</summary>
    static Quaternion Rotation(HavokPackfile pack, int at, string name)
    {
        var c0 = Vector(pack, at); var c1 = Vector(pack, at + 16); var c2 = Vector(pack, at + 32);
        var m = new Matrix4x4(c0.X, c0.Y, c0.Z, 0, c1.X, c1.Y, c1.Z, 0, c2.X, c2.Y, c2.Z, 0, 0, 0, 0, 1);
        if (!Finite(c0) || !Finite(c1) || !Finite(c2) || MathF.Abs(m.GetDeterminant() - 1f) > 1e-3f
            || MathF.Abs(Vector3.Dot(c0, c1)) > 1e-3f || MathF.Abs(Vector3.Dot(c0, c2)) > 1e-3f || MathF.Abs(Vector3.Dot(c1, c2)) > 1e-3f)
            throw new InvalidDataException($"Ragdoll constraint '{name}': a frame is not a rotation.");
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    static HavokTransform Transform(HavokPackfile pack, int at, string what)
    {
        var t = HavokTransform.Read(pack, at);
        float length = t.Rotation.Length();
        if (!Finite(t.Translation) || !float.IsFinite(length) || MathF.Abs(length - 1f) > 1e-3f)
            throw new InvalidDataException($"Ragdoll mapper: {what} has no usable transform.");
        return t with { Rotation = Quaternion.Normalize(t.Rotation) };
    }

    /// <summary>A Havok half float (hkHalf): the top 16 bits of a single.</summary>
    static float Half(HavokPackfile pack, int at) => BitConverter.Int32BitsToSingle(pack.ReadInt16(at) << 16);
    static bool NonNegative(float v) => float.IsFinite(v) && v >= 0f;
    /// <summary>A limit range: finite, min ≤ max, within ±2π.</summary>
    static bool Angles(float min, float max) => float.IsFinite(min) && float.IsFinite(max) && min <= max && min >= -2 * MathF.PI && max <= 2 * MathF.PI;
}
