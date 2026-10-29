using System.Numerics;
using SmFormats.Havok;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>
/// fix/hitboxes (owner, 2026-10-29: "it feels difficult to shoot enemy assault marines or raptors while they're flying. check on their hitboxes"):
/// the rig's hit volumes (<see cref="HitVolumeTuning"/>, the ragdoll bodies at rest) moved with their bones into an animated pose, so the volumes
/// stand where the drawn body is. Each volume belongs to its ragdoll body, each body to the animation bone the ragdoll-to-animation mapper gives it
/// (<see cref="RagdollModelBody.Bone"/>); a posed volume is its rest volume carried by that bone's motion from the reference pose to the pose
/// (rigid: posed bone · inverse(reference bone)). Engine-free; the client draws the same bones (the skeleton's model space is the character frame).
/// </summary>
public sealed class PosedHitVolumes
{
    /// <summary>The rest volumes (the server's <see cref="SimConfig.HitVolumes"/>, in the same order).</summary>
    public IReadOnlyList<HitVolume> Rest { get; }
    /// <summary>The animation bone each rest volume follows (index into the skeleton).</summary>
    public IReadOnlyList<int> BoneOf { get; }
    public HavokCharacterFile Character { get; }
    readonly Matrix4x4[] _reference;
    readonly Matrix4x4[] _inverseReference;

    PosedHitVolumes(IReadOnlyList<HitVolume> rest, IReadOnlyList<int> boneOf, HavokCharacterFile character)
    {
        Rest = rest; BoneOf = boneOf; Character = character;
        _reference = character.Skeleton.ModelSpace(character.Skeleton.ReferencePose);
        _inverseReference = _reference.Select(m => Matrix4x4.Invert(m, out var inv) ? inv : Matrix4x4.Identity).ToArray();
    }

    /// <summary>The marine rig's volumes and bones from the install (read-only); null with the reason in <paramref name="notes"/>.</summary>
    public static PosedHitVolumes? Load(string gameDir, List<string> notes) => Load(gameDir, RigProfiles.Marine, notes);

    /// <summary>A profile's volumes and bones (<paramref name="rest"/>: the rest volumes already read from the same profile; null: read them).</summary>
    public static PosedHitVolumes? Load(string gameDir, RigProfile profile, List<string> notes, IReadOnlyList<HitVolume>? rest = null)
    {
        rest ??= HitVolumeTuning.Load(gameDir, profile, notes);
        var ragdoll = RagdollModel.Load(gameDir, profile, notes);
        if (rest is null || ragdoll is null) return null;
        byte[]? bytes = null;
        using (var archive = ObpkArchive.Open(profile.ArchivePath(gameDir)))
            archive.ReadEntries(e => e.FileName == profile.Entry && bytes is null, (_, d) => bytes = d.ToArray());
        if (bytes is null) { notes.Add($"posed hit volumes: '{profile.Entry}' not found"); return null; }
        return From(rest, ragdoll, new HavokCharacterFile(bytes), notes);
    }

    /// <summary>Binds each rest volume (named after its body, "#n" for further primitives) to its body's animation bone; null when one has none.</summary>
    public static PosedHitVolumes? From(IReadOnlyList<HitVolume> rest, RagdollModel ragdoll, HavokCharacterFile character, List<string> notes)
    {
        var bones = character.Skeleton.BoneNames;
        int BoneIndex(string? name)
        {
            if (name is null) return -1;
            for (int i = 0; i < bones.Count; i++) if (string.Equals(bones[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
        var boneOf = new int[rest.Count];
        for (int i = 0; i < rest.Count; i++)
        {
            string name = rest[i].Name;
            int hash = name.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) name = name[..hash];
            var body = ragdoll.Bodies.FirstOrDefault(b => string.Equals(Strip(b.Name), name, StringComparison.OrdinalIgnoreCase));
            int bone = BoneIndex(body?.Bone);
            if (bone < 0) { notes.Add($"posed hit volumes: volume '{rest[i].Name}' has no animation bone"); return null; }
            boneOf[i] = bone;
        }
        return new PosedHitVolumes(rest, boneOf, character);

        static string Strip(string n) => n.StartsWith("Ragdoll_RIG_", StringComparison.OrdinalIgnoreCase) ? n["Ragdoll_RIG_".Length..] : n;
    }

    /// <summary>The skeleton's model-space bones for clip <paramref name="clip"/> at <paramref name="fraction"/> (0..1) of its length; untracked bones keep the reference pose.</summary>
    public Matrix4x4[] ClipPose(string clip, float fraction) => ClipPoseAt(clip, fraction, seconds: false);

    /// <summary>The model-space bones for clip <paramref name="clip"/> at <paramref name="time"/>: seconds (clamped to the clip) or, with <paramref name="seconds"/> false, a fraction of its length.</summary>
    public Matrix4x4[] ClipPoseAt(string clip, float time, bool seconds = true)
    {
        var sk = Character.Skeleton;
        var bound = Character.LoadAnimation(clip);
        var local = sk.ReferencePose.ToArray();
        float duration = bound.Animation.Duration;
        float end = MathF.Max(0f, duration - 1e-4f);
        var sampled = bound.Animation.Sample(seconds ? Math.Clamp(time, 0f, end) : Math.Clamp(time, 0f, 1f) * end);
        for (int t = 0; t < sampled.Length && t < bound.TrackToBone.Length; t++)
            if ((uint)bound.TrackToBone[t] < (uint)local.Length) local[bound.TrackToBone[t]] = sampled[t];
        return sk.ModelSpace(local);
    }

    /// <summary>The volumes moved with their bones from the reference pose into <paramref name="model"/> (model-space bones of the skeleton).</summary>
    public HitVolume[] Pose(Matrix4x4[] model)
    {
        var result = new HitVolume[Rest.Count];
        for (int i = 0; i < Rest.Count; i++)
        {
            int b = BoneOf[i];
            result[i] = Transform(Rest[i], _inverseReference[b] * model[b]);   // row vectors: back to the bone's frame at rest, then out with the posed bone
        }
        return result;
    }

    /// <summary>A volume moved by the rigid transform <paramref name="m"/> (row-vector convention; scale is dropped).</summary>
    public static HitVolume Transform(HitVolume v, Matrix4x4 m)
    {
        Matrix4x4.Decompose(m, out _, out var q, out var t);
        q = Quaternion.Normalize(q);
        Vector3 P(Vector3 p) => Vector3.Transform(p, q) + t;
        return v.Kind switch
        {
            HitVolumeKind.Sphere => v with { A = P(v.A), B = P(v.A) },
            HitVolumeKind.Capsule => v with { A = P(v.A), B = P(v.B) },
            HitVolumeKind.Box => v with { A = P(v.A), Rotation = Quaternion.Normalize(q * v.Rotation) },
            _ => HitVolume.Hull(v.Name, v.Zone,
                v.Planes!.Select(p => { var n = Vector3.Transform(new Vector3(p.X, p.Y, p.Z), q); return new Vector4(n, p.W - Vector3.Dot(n, t)); }).ToArray(),
                v.Vertices!.Select(P).ToArray()),
        };
    }

    /// <summary>The centre of a volume (sphere centre, capsule middle, box centre, hull vertex centroid; character frame).</summary>
    public static Vector3 Centre(HitVolume v) => v.Kind == HitVolumeKind.Capsule ? (v.A + v.B) * 0.5f : v.A;
}
