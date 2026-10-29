using System.Numerics;

namespace SmFormats.Havok;

/// <summary>
/// hkaSkeleton (hk_2010, 32-bit): bone names, parent indices, and the reference pose in parent space.
/// Field offsets: 8 name, 12 parentIndices (hkInt16[]), 24 bones (hkaBone[], 8 bytes each: name, lockTranslation),
/// 36 referencePose (hkQsTransform[], 48 bytes each: translation, rotation xyzw, scale).
/// </summary>
public sealed record HavokSkeleton(string? Name, IReadOnlyList<string> BoneNames, short[] Parents, IReadOnlyList<HavokTransform> ReferencePose)
{
    public static HavokSkeleton Read(HavokPackfile pack, int offset)
    {
        string? name = pack.String(offset + 8);
        var (parentsAt, parentCount) = pack.Array(offset + 12, 2);
        var (bonesAt, boneCount) = pack.Array(offset + 24, 8);
        var (poseAt, poseCount) = pack.Array(offset + 36, 48);
        if (parentCount != boneCount || poseCount != boneCount)
            throw new InvalidDataException($"Skeleton '{name}': {boneCount} bones, {parentCount} parents, {poseCount} pose transforms.");

        var names = new string[boneCount];
        var parents = new short[boneCount];
        var pose = new HavokTransform[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            names[i] = pack.String(bonesAt!.Value + 8 * i) ?? $"bone{i}";
            parents[i] = pack.ReadInt16(parentsAt!.Value + 2 * i);
            if (parents[i] < -1 || parents[i] >= i) throw new InvalidDataException($"Skeleton '{name}': bone {i} has parent {parents[i]}.");
            pose[i] = HavokTransform.Read(pack, poseAt!.Value + 48 * i);
        }
        return new HavokSkeleton(name, names, parents, pose);
    }

    /// <summary>Model-space transforms of a local (parent-space) pose; parents precede children.</summary>
    public Matrix4x4[] ModelSpace(IReadOnlyList<HavokTransform> local)
    {
        var world = new Matrix4x4[Parents.Length];
        for (int i = 0; i < Parents.Length; i++)
            world[i] = Parents[i] < 0 ? local[i].ToMatrix() : local[i].ToMatrix() * world[Parents[i]];
        return world;
    }
}

/// <summary>hkQsTransform: translation, rotation, scale (row-vector convention: M = S·R·T).</summary>
public readonly record struct HavokTransform(Vector3 Translation, Quaternion Rotation, Vector3 Scale)
{
    public static readonly HavokTransform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    public static HavokTransform Read(HavokPackfile pack, int offset) => new(
        new Vector3(pack.ReadSingle(offset), pack.ReadSingle(offset + 4), pack.ReadSingle(offset + 8)),
        new Quaternion(pack.ReadSingle(offset + 16), pack.ReadSingle(offset + 20), pack.ReadSingle(offset + 24), pack.ReadSingle(offset + 28)),
        new Vector3(pack.ReadSingle(offset + 32), pack.ReadSingle(offset + 36), pack.ReadSingle(offset + 40)));

    public Matrix4x4 ToMatrix() =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);
}
