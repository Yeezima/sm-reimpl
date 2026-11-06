using System.Numerics;
using SmContent.Extras.Musou;
using SmFormats.Dcm;

namespace SmFormats.Tests.Extras.Musou;

/// <summary>
/// The Musou Warboss's head (<see cref="WarbossRig.HeadBone"/>, <see cref="WarbossRig.BindHead"/>): the cutscene head's face-rig bones bound to the gameplay rig's head, jaw, neck and chest,
/// each with its target's reference inverse bind. A synthetic block (names shaped like the install's, positions and matrices invented).
/// </summary>
public class WarbossHeadTests
{
    static readonly string[] Rig = ["RIG_Root", "RIG_Chest", "RIG_Neck", "RIG_Head", "RIG_Brow", "RIG_Jaw"];
    static readonly HashSet<string> RigSet = new(Rig, StringComparer.Ordinal);

    [Theory]
    [InlineData("RIG_headX", "RIG_Head")]
    [InlineData("RIG_NeckA", "RIG_Neck")]
    [InlineData("RIG_SpineD", "RIG_Chest")]
    [InlineData("RIG_jawX", "RIG_Jaw")]
    [InlineData("RIG_chin", "RIG_Jaw")]
    [InlineData("RIG_l_lowerLip", "RIG_Jaw")]
    [InlineData("RIG_lowerLip", "RIG_Jaw")]
    [InlineData("RIG_tongue_B", "RIG_Jaw")]
    [InlineData("RIG_l_brow_A", "RIG_Head")]
    [InlineData("RIG_r_upperEyeLid", "RIG_Head")]
    [InlineData("RIG_upperLip", "RIG_Head")]
    [InlineData("RIG_l_bobble_e1", "RIG_Head")]
    [InlineData("RIG_Brow", "RIG_Brow")]   // a bone the rig has keeps its name
    public void Each_face_bone_follows_the_nearest_gameplay_bone(string face, string expected) => Assert.Equal(expected, WarbossRig.HeadBone(face, RigSet));

    [Fact]
    public void Bound_head_takes_its_targets_reference_inverse_binds_and_keeps_its_vertices_and_weights()
    {
        var reference = Rig.Select((_, i) => Matrix4x4.CreateRotationZ(0.1f * i) * Matrix4x4.CreateTranslation(0f, 0.1f * i, 2.5f + 0.2f * i)).ToArray();
        var faceBind = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        var skin = new DcmSkin(0, [new DcmBone("RIG_headX", faceBind, 0), new DcmBone("RIG_jawX", faceBind, 1), new DcmBone("RIG_Head", faceBind, 2)], [0, 1, 2],
            [new Vector4(0.5f, 0.5f, 0f, 0f)], [0, 1, 0, 0]);
        Vector3[] positions = [new(0f, 0.2f, 3.2f)];
        var block = new DcmBlock(0, Vector3.Zero, Vector3.One, [], [0, 0, 0], positions, [Vector3.UnitZ], [Vector4.UnitX], [Vector2.Zero], [Vector2.Zero], [0u], skin);
        var unskinned = block with { Skin = null };
        var mesh = DcmMesh.FromBlocks([block, unskinned]);

        var bound = WarbossRig.BindHead(mesh, Rig, reference, out int rebound);

        Assert.Equal(2, rebound);   // headX and jawX; RIG_Head is the rig's own and is left as it was
        var bones = bound.Blocks[0].Skin!.Bones;
        Assert.Equal(["RIG_Head", "RIG_Jaw", "RIG_Head"], bones.Select(b => b.Name));
        Matrix4x4.Invert(reference[3], out var headInverse);
        Matrix4x4.Invert(reference[5], out var jawInverse);
        Assert.Equal(headInverse, bones[0].InverseBind);
        Assert.Equal(jawInverse, bones[1].InverseBind);
        Assert.Equal(faceBind, bones[2].InverseBind);
        Assert.Same(positions, bound.Blocks[0].Positions);
        Assert.Equal(skin.BoneIndices, bound.Blocks[0].Skin!.BoneIndices);
        Assert.Same(unskinned, bound.Blocks[1]);
        Assert.Equal("RIG_headX", mesh.Blocks[0].Skin!.Bones[0].Name);   // the read mesh is untouched
    }

    [Fact]
    public void A_target_the_rig_lacks_leaves_the_face_bone_as_it_was()
    {
        string[] rig = ["RIG_Root", "RIG_Head"];   // no jaw
        var reference = new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(0f, 0f, 3f) };
        var skin = new DcmSkin(0, [new DcmBone("RIG_jawX", Matrix4x4.Identity, 0)], [0], [Vector4.UnitX], [0, 0, 0, 0]);
        var block = new DcmBlock(0, Vector3.Zero, Vector3.One, [], [0, 0, 0], [Vector3.Zero], [Vector3.UnitZ], [Vector4.UnitX], [Vector2.Zero], [Vector2.Zero], [0u], skin);

        var bound = WarbossRig.BindHead(DcmMesh.FromBlocks([block]), rig, reference, out int rebound);

        Assert.Equal(0, rebound);
        Assert.Equal("RIG_jawX", bound.Blocks[0].Skin!.Bones[0].Name);
    }
}
