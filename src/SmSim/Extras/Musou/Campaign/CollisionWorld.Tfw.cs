using System.Numerics;

namespace SmSim;

/// <summary>Musou Mode's world (design section A): a level with solids beside it.</summary>
public sealed partial class CollisionWorld
{
    /// <summary>
    /// This level with the triangles of <paramref name="corners"/> (three a triangle) added to both meshes, its kill volumes and props kept and no spawn-room blockers. Only the meshes are
    /// made again; the level's triangles keep their numbers and materials, the added ones come before the props, as <see cref="PropOfShotTriangle"/> wants.
    /// </summary>
    internal CollisionWorld WithSolids(Vector3[] corners)
    {
        int[] indices = Enumerable.Range(0, corners.Length).ToArray();
        return new CollisionWorld(_levelCharacter.With(corners, indices), _levelWeapons.With(corners, indices), KillVolumes, null, Props);
    }
}
