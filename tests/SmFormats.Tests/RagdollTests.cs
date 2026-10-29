using System.Numerics;
using SmContent;
using SmFormats.Havok;
using SmSim;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// M20: the ragdoll reader (<see cref="HavokRagdoll"/>: rigid bodies, rest transforms, sphere / capsule / box / hull shapes
/// through translate, list and MOPP wrappers) and the hit volumes built from it (<see cref="HitVolumeTuning"/>). Synthetic
/// packfiles only.
/// </summary>
public class RagdollTests
{
    /// <summary>Local X turned to point up (−90° about Y), as the game's spine and head bodies are.</summary>
    static readonly Quaternion XUp = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2);

    /// <summary>A cube hull of half size <paramref name="half"/> (planes) whose core vertices sit <paramref name="radius"/> inside.</summary>
    static HullFixture Cube(float half, float radius)
    {
        float core = half - radius;
        var vertices = Enumerable.Range(0, 8).Select(i => new Vector3((i & 1) == 0 ? -core : core, (i & 2) == 0 ? -core : core, (i & 4) == 0 ? -core : core)).ToArray();
        Vector4[] planes = [new(1, 0, 0, -half), new(-1, 0, 0, -half), new(0, 1, 0, -half), new(0, -1, 0, -half), new(0, 0, 1, -half), new(0, 0, -1, -half)];
        return new HullFixture(new Vector3(core), Vector3.Zero, radius, vertices, planes);
    }

    static RagdollBodyFixture[] Marine() =>
    [
        new("Ragdoll_RIG_Pelvis", new Vector3(0, 0, 1.2f), Quaternion.Identity, Cube(0.2f, 0.01f)),
        new("Ragdoll_RIG_Chest", new Vector3(0, 0, 1.6f), XUp, new MoppFixture(new ListFixture([Cube(0.25f, 0.02f), new CapsuleFixture(new Vector3(0.3f, -0.4f, 0), new Vector3(0.6f, -0.4f, 0), 0.1f)]))),
        new("Ragdoll_RIG_Head", new Vector3(0, -0.08f, 2.0f), XUp, new TranslateFixture(new Vector3(0.1f, 0.03f, 0), new SphereFixture(0.18f))),
        new("Ragdoll_RIG_L_Calf", new Vector3(-0.2f, 0, 0.9f), Quaternion.Identity, new CapsuleFixture(Vector3.Zero, new Vector3(0, 0, -0.4f), 0.15f)),
        new("Ragdoll_RIG_L_Foot", new Vector3(-0.25f, 0, 0.1f), Quaternion.Identity, new TranslateFixture(new Vector3(0, 0.1f, 0), new BoxFixture(new Vector3(0.1f, 0.2f, 0.05f), 0.01f))),
        new("Ragdoll_RIG_L_Hand", new Vector3(-0.5f, 0, 1.1f), Quaternion.Identity, new OtherShapeFixture("hkpCylinderShape")),
    ];

    static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-5f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void Bodies_and_their_shapes_are_read_in_model_space_at_the_rest_pose()
    {
        var notes = new List<string>();
        var bodies = HavokRagdoll.Read(new HavokPackfile(BuildRagdoll(Marine())), notes);
        Assert.Equal(["Ragdoll_RIG_Pelvis", "Ragdoll_RIG_Chest", "Ragdoll_RIG_Head", "Ragdoll_RIG_L_Calf", "Ragdoll_RIG_L_Foot", "Ragdoll_RIG_L_Hand"], bodies.Select(b => b.Name));

        var head = Assert.Single(bodies[2].Shapes);
        Assert.Equal(RagdollShapeKind.Sphere, head.Kind);
        Assert.Equal(0.18f, head.Radius);
        Near(new Vector3(0f, -0.05f, 2.1f), head.A);   // the translation (0.1, 0.03, 0) turned with local X up: 2.0 + 0.1

        var pelvis = Assert.Single(bodies[0].Shapes);
        Assert.Equal(RagdollShapeKind.Hull, pelvis.Kind);
        Assert.Equal(6, pelvis.Planes!.Count);
        Assert.Equal(8, pelvis.Vertices!.Count);
        Assert.Contains(pelvis.Vertices, v => Vector3.Distance(v, new Vector3(0.19f, 0.19f, 1.39f)) < 1e-5f);   // core corner, a radius inside the planes
        Assert.Contains(pelvis.Planes, p => Vector4.Distance(p, new Vector4(0, 0, 1, -1.4f)) < 1e-5f);   // top face at z 1.2 + 0.2

        // The chest: a MOPP tree over a list of a hull and a capsule, turned with local X up.
        Assert.Equal([RagdollShapeKind.Hull, RagdollShapeKind.Capsule], bodies[1].Shapes.Select(s => s.Kind));
        var capsule = bodies[1].Shapes[1];
        Near(new Vector3(0f, -0.4f, 1.9f), capsule.A);
        Near(new Vector3(0f, -0.4f, 2.2f), capsule.B);
        var chestHull = bodies[1].Shapes[0];
        Assert.Contains(chestHull.Planes!, p => Vector4.Distance(p, new Vector4(0, 0, 1, -1.85f)) < 1e-5f);   // local +X face, now facing up

        var calf = Assert.Single(bodies[3].Shapes);
        Assert.Equal((RagdollShapeKind.Capsule, 0.15f), (calf.Kind, calf.Radius));
        Near(new Vector3(-0.2f, 0, 0.5f), calf.B);

        var foot = Assert.Single(bodies[4].Shapes);
        Assert.Equal(RagdollShapeKind.Box, foot.Kind);
        Near(new Vector3(-0.25f, 0.1f, 0.1f), foot.A);
        Near(new Vector3(0.11f, 0.21f, 0.06f), foot.B);   // half extents plus the convex radius

        Assert.Empty(bodies[5].Shapes);
        Assert.Contains(notes, n => n.Contains("hkpCylinderShape"));
    }

    [Fact]
    public void A_rest_transform_that_is_not_a_rotation_is_refused_and_a_file_without_a_ragdoll_has_none()
    {
        var bad = Marine();
        bad[0] = bad[0] with { Columns = [new Vector3(2, 0, 0), Vector3.UnitY, Vector3.UnitZ] };
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.Read(new HavokPackfile(BuildRagdoll(bad))));
        var mirrored = Marine();
        mirrored[0] = mirrored[0] with { Columns = [-Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ] };
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.Read(new HavokPackfile(BuildRagdoll(mirrored))));
        var rig = SyntheticHavokWriter.Build([new("RIG_Root", -1, Vector3.Zero, Quaternion.Identity)], []);
        Assert.Empty(HavokRagdoll.Read(new HavokPackfile(rig)));
    }

    [Fact]
    public void Hit_volumes_are_built_head_first_with_one_volume_per_shape()
    {
        var notes = new List<string>();
        var volumes = HitVolumeTuning.FromRagdoll(HavokRagdoll.Read(new HavokPackfile(BuildRagdoll(Marine())), notes), notes)!;
        Assert.Equal(["Head", "Pelvis", "Chest", "Chest #2", "L_Calf", "L_Foot"], volumes.Select(v => v.Name));
        Assert.Equal([HitZone.Head, HitZone.Body, HitZone.Body, HitZone.Body, HitZone.Body, HitZone.Body], volumes.Select(v => v.Zone));
        Assert.Equal([HitVolumeKind.Sphere, HitVolumeKind.Hull, HitVolumeKind.Hull, HitVolumeKind.Capsule, HitVolumeKind.Capsule, HitVolumeKind.Box], volumes.Select(v => v.Kind));
        Assert.All(volumes, v => Assert.True(v.IsValid));

        // In use: a level ray at the head's height from the front hits the head; one through the pelvis cube hits the body
        // at its front face (0.2 m before its centre).
        var config = new SimConfig { HitVolumes = volumes };
        var head = Combat.HitTest(new Vector3(0, 10, 2.1f), -Vector3.UnitY, Vector3.Zero, config, 100f);
        Assert.Equal(HitZone.Head, head.Zone);
        Assert.Equal(10f + 0.05f - 0.18f, head.Distance, 1e-4f);
        var pelvis = Combat.HitTest(new Vector3(0, 10, 1.2f), -Vector3.UnitY, Vector3.Zero, config, 100f);
        Assert.Equal((HitZone.Body, 9.8f), (pelvis.Zone, MathF.Round(pelvis.Distance, 4)));

        // No head body: unusable (the placeholders stay).
        var headless = HavokRagdoll.Read(new HavokPackfile(BuildRagdoll(Marine().Where(b => !b.Name.Contains("Head")).ToArray())));
        Assert.Null(HitVolumeTuning.FromRagdoll(headless, notes));
        Assert.Contains(notes, n => n.Contains("0 head"));
    }
}
