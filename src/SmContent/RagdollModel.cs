using System.Numerics;
using SmFormats.Havok;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>
/// A ragdoll body for a physics engine (M23): its name (<c>Ragdoll_RIG_*</c>), the animation bone it drives
/// (<see cref="Bone"/>: the ragdoll-to-animation mapper's mapping whose animation bone has the body's name without
/// <c>Ragdoll_</c>, else its first; null without one), its parent body (-1 for the root), the rest transform in model space
/// (the character frame: X right, Y forward, Z up, feet at the origin; the origin is the body's bone), and the physics in the
/// body's frame: mass (kg), diagonal inertia, centre of mass, material, damping, Havok's collision filter word, and its
/// primitives (<see cref="Shapes"/>, body frame, same conventions as <see cref="RagdollShape"/>).
/// </summary>
public sealed record RagdollModelBody(string Name, string? Bone, int Parent, Vector3 RestPosition, Quaternion RestRotation, IReadOnlyList<RagdollShape> Shapes,
    RagdollBodyPhysics Physics)
{
    public float Mass => Physics.Mass;
    public Vector3 Inertia => Physics.Inertia;
    public Vector3 CenterOfMass => Physics.CenterOfMass;
}

/// <summary>
/// The multiplayer marine's ragdoll as a physics engine needs it (M23; original rig data, <b>supported by evidence</b> for the
/// values, layouts in docs/discovery.md "Ragdoll and hit shapes"): 17 bodies and 16 joints from <c>havok_sm_base_mp__hkxx</c>,
/// which both Space Marine and Chaos players use (the versus set has no other rig; the Exterminatus enemy
/// <c>havok_chaosspacemarine_coop</c> has its own 19-body ragdoll, out of the alpha's scope). <see cref="Joints"/>: body A is the
/// child, B the parent, frames in each body's frame (<see cref="RagdollJoint"/>). <see cref="ToAnimation"/> and
/// <see cref="Chains"/> drive the animation skeleton from the ragdoll; bones in <see cref="UnmappedAnimationBones"/> keep their
/// local (animated) transform (the mapper's keep-unmapped-local flag is set). The ragdoll is client-side cosmetics: nothing of it
/// is simulated or sent by the server.
/// </summary>
public sealed record RagdollModel(IReadOnlyList<RagdollModelBody> Bodies, IReadOnlyList<RagdollJoint> Joints, IReadOnlyList<RagdollBoneMapping> ToAnimation,
    IReadOnlyList<RagdollChainMapping> Chains, IReadOnlyList<string> UnmappedAnimationBones, IReadOnlyList<string> SkeletonBones)
{
    /// <summary>Total mass (kg): 780 for the marine.</summary>
    public float TotalMass => Bodies.Sum(b => b.Mass);

    /// <summary>The index of the body named <paramref name="name"/> (ordinal, ignoring case), -1 if none.</summary>
    public int IndexOf(string name)
    {
        for (int i = 0; i < Bodies.Count; i++) if (string.Equals(Bodies[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Reads the multiplayer marine rig's ragdoll from the install (read-only). Space Marines and Chaos use the same one.
    /// Null, with the reason in <paramref name="notes"/>, when the rig is missing or its ragdoll cannot be read.
    /// </summary>
    public static RagdollModel? Load(string gameDir, List<string>? notes = null) => Load(gameDir, RigProfiles.Marine, notes);

    /// <summary>A profile's ragdoll (Ork O2: the boy has 15 bodies, 14 joints, 271 kg); null with the reason in <paramref name="notes"/>.</summary>
    public static RagdollModel? Load(string gameDir, RigProfile profile, List<string>? notes = null)
    {
        notes ??= [];
        byte[]? rig = null;
        try
        {
            using (var archive = ObpkArchive.Open(profile.ArchivePath(gameDir)))
                archive.ReadEntries(e => e.FileName == profile.Entry && rig is null, (_, d) => rig = d.ToArray());
            if (rig is null) { notes.Add($"ragdoll: '{profile.Entry}' not found"); return null; }
            return FromPackfile(new HavokPackfile(rig), notes);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException)
        {
            notes.Add($"ragdoll: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Builds the model from a character packfile (null with a note when it has no ragdoll). Shapes are turned from model
    /// space into each body's frame. Throws <see cref="InvalidDataException"/> on a malformed ragdoll.
    /// </summary>
    public static RagdollModel? FromPackfile(HavokPackfile pack, List<string>? notes = null)
    {
        notes ??= [];
        var shapes = HavokRagdoll.Read(pack, notes);
        var physics = HavokRagdoll.ReadPhysics(pack, notes);
        if (physics is null || shapes.Count == 0) { notes.Add("ragdoll: the packfile has none"); return null; }
        if (physics.Bodies.Count != shapes.Count) throw new InvalidDataException("Ragdoll: shape and physics body counts differ.");

        var bodies = new List<RagdollModelBody>(shapes.Count);
        for (int b = 0; b < shapes.Count; b++)
        {
            var body = shapes[b];
            // The body's bone and its parent bone's body.
            int bone = -1;
            for (int i = 0; i < physics.BoneToBody.Count; i++) if (physics.BoneToBody[i] == b) { bone = i; break; }
            int parent = -1;
            for (int p = bone >= 0 ? physics.SkeletonParents[bone] : -1; p >= 0 && parent < 0; p = physics.SkeletonParents[p]) parent = physics.BoneToBody[p];
            bodies.Add(new RagdollModelBody(body.Name, AnimationBone(physics, bone, body.Name), parent, body.Origin, body.Rotation,
                body.Shapes.Select(s => ToBody(s, body.Origin, body.Rotation)).ToArray(), physics.Bodies[b]));
        }
        return new RagdollModel(bodies, physics.Joints, physics.ToAnimation, physics.Chains, physics.UnmappedAnimationBones, physics.SkeletonBones);
    }

    static string? AnimationBone(RagdollPhysics physics, int bone, string bodyName)
    {
        if (bone < 0) return null;
        string plain = bodyName.StartsWith("Ragdoll_", StringComparison.OrdinalIgnoreCase) ? bodyName["Ragdoll_".Length..] : bodyName;
        var mine = physics.ToAnimation.Where(m => m.RagdollBone == bone).ToList();
        return mine.FirstOrDefault(m => string.Equals(m.AnimationBone, plain, StringComparison.OrdinalIgnoreCase))?.AnimationBone ?? mine.FirstOrDefault()?.AnimationBone;
    }

    /// <summary>A model-space primitive in the frame of a body at <paramref name="origin"/> turned by <paramref name="rotation"/>.</summary>
    public static RagdollShape ToBody(RagdollShape s, Vector3 origin, Quaternion rotation)
    {
        var inverse = Quaternion.Conjugate(rotation);
        Vector3 Point(Vector3 p) => Vector3.Transform(p - origin, inverse);
        return s.Kind switch
        {
            RagdollShapeKind.Box => s with { A = Point(s.A), Rotation = Quaternion.Normalize(inverse * s.Rotation) },
            RagdollShapeKind.Hull => s with
            {
                A = Point(s.A), Rotation = Quaternion.Normalize(inverse * s.Rotation),
                Vertices = s.Vertices!.Select(Point).ToArray(),
                // n·p + w ≤ 0 in model space; with p = R q + o: (Rᵀn)·q + (w + n·o) ≤ 0.
                Planes = s.Planes!.Select(p => { var n = new Vector3(p.X, p.Y, p.Z); return new Vector4(Vector3.Transform(n, inverse), p.W + Vector3.Dot(n, origin)); }).ToArray(),
            },
            _ => s with { A = Point(s.A), B = Point(s.B), Rotation = Quaternion.Normalize(inverse * s.Rotation) },
        };
    }
}
