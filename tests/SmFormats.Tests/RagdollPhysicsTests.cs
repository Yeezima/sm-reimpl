using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// M23: the ragdoll's physics (<see cref="HavokRagdoll.ReadPhysics"/>: masses, inertia, material, damping, filter words, the
/// ragdoll skeleton, ragdoll and limited-hinge constraints, the skeleton mapper), the content model built from it
/// (<see cref="RagdollModel"/>) and the death values (<see cref="DeathTuning"/>). Synthetic files only.
/// </summary>
public class RagdollPhysicsTests
{
    static readonly Quaternion XUp = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2);
    static readonly Quaternion Turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f);
    const float Deg = MathF.PI / 180f;

    static RagdollBodyFixture[] Bodies() =>
    [
        new("Ragdoll_RIG_Pelvis", new Vector3(0, 0, 1.2f), XUp, new SphereFixture(0.2f),
            Physics: new RagdollPhysicsFixture(120f, new Vector3(33f, 25f, 26f), new Vector3(0.05f, 0f, 0f), FilterInfo: 0x00010020)),
        new("Ragdoll_RIG_Chest", new Vector3(0, 0, 1.55f), XUp, new TranslateFixture(new Vector3(0.2f, 0, 0), new SphereFixture(0.3f)),
            Physics: new RagdollPhysicsFixture(150f, new Vector3(56f, 87f, 70f), new Vector3(0.4f, -0.3f, 0f), Friction: 0.6f, Restitution: 0.4f, FilterInfo: 0x000104A0), Parent: 0),
        new("Ragdoll_RIG_L_Calf", new Vector3(-0.2f, 0, 0.75f), Quaternion.Identity, new CapsuleFixture(Vector3.Zero, new Vector3(0, 0, -0.4f), 0.2f),
            Physics: new RagdollPhysicsFixture(60f, new Vector3(1.4f, 2.4f, 2.4f), new Vector3(0, 0, -0.2f), Motion: 2, FilterInfo: 0x000108E0), Parent: 0),
        new("Ragdoll_RIG_L_Foot", new Vector3(-0.25f, 0, 0.2f), Quaternion.Identity, new BoxFixture(new Vector3(0.1f, 0.2f, 0.05f), 0.01f),
            Physics: new RagdollPhysicsFixture(10f, new Vector3(0.33f, 0.34f, 0.47f), Vector3.Zero, LinearDamping: 0.5f, GravityFactor: 2f), Parent: 2),
    ];

    static RagdollJointFixture[] Joints() =>
    [
        new("Ragdoll_RIG_Chest", 1, 0, Vector3.Zero, Quaternion.Identity, new Vector3(0.35f, 0, 0), Turn, -5 * Deg, 5 * Deg, Cone: 5 * Deg, PlaneMin: -30 * Deg, PlaneMax: 30 * Deg),
        new("Ragdoll_RIG_L_Calf", 2, 0, Vector3.Zero, Quaternion.Identity, new Vector3(0.45f, 0, 0.2f), Quaternion.Identity, -10 * Deg, 10 * Deg, Cone: 63.4f * Deg,
            PlaneMin: -9 * Deg, PlaneMax: 6.4f * Deg, Friction: 10f),
        new("Ragdoll_RIG_L_Foot", 3, 2, Vector3.Zero, Turn, new Vector3(0, 0, -0.55f), Quaternion.Identity, -5 * Deg, 20 * Deg, Friction: 0f, Tau: 1f, Hinge: true),
    ];

    static RagdollMapperFixture Mapper() => new(
        [new("RIG_Root", -1, new Vector3(0, 0, 1.2f), Quaternion.Identity), new("RIG_Pelvis", 0, Vector3.Zero, Quaternion.Identity),
         new("RIG_SpineD", 1, new Vector3(0, 0, 0.35f), Quaternion.Identity), new("RIG_L_Calf", 1, new Vector3(-0.2f, 0, -0.45f), Quaternion.Identity),
         new("RIG_L_Foot", 3, new Vector3(-0.05f, 0, -0.55f), Quaternion.Identity), new("RIG_L_Toe", 4, new Vector3(0, 0.2f, 0), Quaternion.Identity)],
        [(0, 1, Vector3.Zero, Quaternion.Identity), (0, 0, new Vector3(0, 0.01f, 0), Turn), (1, 2, Vector3.Zero, Quaternion.Identity),
         (2, 3, Vector3.Zero, Quaternion.Identity), (3, 4, new Vector3(0.001f, 0, 0), Quaternion.Identity)],
        Chains: [(0, 1, 0, 2)], Unmapped: [5]);

    static HavokPackfile Pack(RagdollJointFixture[]? joints = null, RagdollMapperFixture? mapper = null, RagdollBodyFixture[]? bodies = null) =>
        new(BuildRagdoll(bodies ?? Bodies(), joints ?? Joints(), mapper ?? Mapper()));

    static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");
    static void Near(float expected, float actual, float tolerance = 1e-4f) =>
        Assert.True(MathF.Abs(expected - actual) < tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void Body_physics_are_read_with_inverted_mass_and_inertia_half_float_damping_and_the_filter_word()
    {
        var physics = HavokRagdoll.ReadPhysics(Pack())!;
        Assert.Equal(["Ragdoll_RIG_Pelvis", "Ragdoll_RIG_Chest", "Ragdoll_RIG_L_Calf", "Ragdoll_RIG_L_Foot"], physics.Bodies.Select(b => b.Name));
        var pelvis = physics.Bodies[0];
        Near(120f, pelvis.Mass, 1e-3f); Near(new Vector3(33f, 25f, 26f), pelvis.Inertia, 1e-3f); Near(new Vector3(0.05f, 0, 0), pelvis.CenterOfMass);
        Assert.Equal(RagdollMotionKind.BoxInertia, pelvis.Motion);
        Assert.Equal((0.8f, 0.8f, 0f), (pelvis.Friction, pelvis.Restitution, pelvis.LinearDamping));
        Near(0.05f, pelvis.AngularDamping, 5e-4f);   // a half float: 0.0498
        Assert.Equal(1f, pelvis.GravityFactor);
        Assert.Equal((0, 1, 0, 1), (pelvis.Layer, pelvis.SubSystemId, pelvis.NoCollideWith, pelvis.SystemGroup));
        var chest = physics.Bodies[1];
        Assert.Equal((0.6f, 0.4f), (chest.Friction, chest.Restitution));
        Assert.Equal((5, 1, 1), (chest.SubSystemId, chest.NoCollideWith, chest.SystemGroup));   // does not collide with the pelvis
        Assert.Equal(RagdollMotionKind.SphereInertia, physics.Bodies[2].Motion);
        Assert.Equal((0.5f, 2f), (physics.Bodies[3].LinearDamping, physics.Bodies[3].GravityFactor));
        Assert.Equal(["Ragdoll_RIG_Pelvis", "Ragdoll_RIG_Chest", "Ragdoll_RIG_L_Calf", "Ragdoll_RIG_L_Foot"], physics.SkeletonBones);
        Assert.Equal(new short[] { -1, 0, 0, 2 }, physics.SkeletonParents);
        Assert.Equal([0, 1, 2, 3], physics.BoneToBody);
    }

    [Fact]
    public void Ragdoll_and_hinge_constraints_are_read_with_their_frames_and_limits()
    {
        var notes = new List<string>();
        var joints = HavokRagdoll.ReadPhysics(Pack(), notes)!.Joints;
        Assert.Empty(notes);
        Assert.Equal(3, joints.Count);
        var chest = joints[0];
        Assert.Equal(("Ragdoll_RIG_Chest", RagdollJointKind.Ragdoll, 1, 0), (chest.Name, chest.Kind, chest.BodyA, chest.BodyB));
        Near(Vector3.Zero, chest.PivotA); Near(new Vector3(0.35f, 0, 0), chest.PivotB);
        Near(Vector3.UnitX, chest.AxisA); Near(Vector3.Transform(Vector3.UnitX, Turn), chest.AxisB);
        Near(Vector3.Transform(Vector3.UnitY, Turn), chest.PlaneAxisB);
        Near(-5 * Deg, chest.MinAngle); Near(5 * Deg, chest.MaxAngle); Near(5 * Deg, chest.ConeAngle);
        Near(-30 * Deg, chest.PlaneMin); Near(30 * Deg, chest.PlaneMax);
        Assert.Equal((20f, 0.8f), (chest.MaxFrictionTorque, chest.LimitTau));
        Assert.Equal(10f, joints[1].MaxFrictionTorque);
        var foot = joints[2];
        Assert.Equal((RagdollJointKind.LimitedHinge, 3, 2), (foot.Kind, foot.BodyA, foot.BodyB));
        Near(Vector3.Transform(Vector3.UnitX, Turn), foot.AxisA); Near(new Vector3(0, 0, -0.55f), foot.PivotB);
        Near(-5 * Deg, foot.MinAngle); Near(20 * Deg, foot.MaxAngle);
        Assert.Equal((0f, 0f, 0f, 0f, 1f), (foot.ConeAngle, foot.PlaneMin, foot.PlaneMax, foot.MaxFrictionTorque, foot.LimitTau));
    }

    [Fact]
    public void The_mapper_from_the_ragdoll_skeleton_gives_animation_bones_chains_and_unmapped_bones()
    {
        var physics = HavokRagdoll.ReadPhysics(Pack())!;
        Assert.Equal("RIG_Root", physics.AnimationSkeleton);
        Assert.Equal([(0, "RIG_Pelvis"), (0, "RIG_Root"), (1, "RIG_SpineD"), (2, "RIG_L_Calf"), (3, "RIG_L_Foot")], physics.ToAnimation.Select(m => (m.RagdollBone, m.AnimationBone)));
        Near(new Vector3(0, 0.01f, 0), physics.ToAnimation[1].Translation);
        Assert.True(Quaternion.Dot(Turn, physics.ToAnimation[1].Rotation) > 0.9999f);
        var chain = Assert.Single(physics.Chains);
        Assert.Equal((0, 1, "RIG_Root", "RIG_SpineD"), (chain.RagdollStart, chain.RagdollEnd, chain.AnimationStart, chain.AnimationEnd));
        Assert.Equal(["RIG_L_Toe"], physics.UnmappedAnimationBones);
    }

    [Fact]
    public void Unknown_constraint_classes_are_skipped_and_misread_atoms_or_missing_masses_are_refused()
    {
        var notes = new List<string>();
        var other = new RagdollJointFixture("Ragdoll_RIG_Chest", 1, 0, Vector3.Zero, Quaternion.Identity, Vector3.Zero, Quaternion.Identity, 0, 0, ClassName: "hkpBallAndSocketConstraintData");
        var physics = HavokRagdoll.ReadPhysics(Pack([other, .. Joints().Skip(1)]), notes)!;
        Assert.Equal(2, physics.Joints.Count);
        Assert.Contains(notes, n => n.Contains("hkpBallAndSocketConstraintData"));

        var bad = Joints()[0] with { BadAtom = 3 };
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.ReadPhysics(Pack([bad])));
        var inverted = Joints()[0] with { Min = 0.2f, Max = -0.2f };
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.ReadPhysics(Pack([inverted])));
        var massless = Bodies().Select((b, i) => i == 2 ? b with { Physics = null } : b).ToArray();
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.ReadPhysics(Pack(bodies: massless)));

        Assert.Null(HavokRagdoll.ReadPhysics(new HavokPackfile(Build([new Bone("Root", -1, Vector3.Zero, Quaternion.Identity)], []))));
        var unmappedNotes = new List<string>();
        var noMapper = HavokRagdoll.ReadPhysics(new HavokPackfile(BuildRagdoll(Bodies(), Joints())), unmappedNotes)!;
        Assert.Empty(noMapper.ToAnimation);
        Assert.Contains(unmappedNotes, n => n.Contains("mapper"));
    }

    [Fact]
    public void Out_of_range_bone_to_body_entries_mapper_bones_and_chain_ends_are_refused()
    {
        HavokPackfile With(RagdollMapperFixture? mapper = null, int[]? boneToBody = null) => new(BuildRagdoll(Bodies(), Joints(), mapper ?? Mapper(), boneToBody));
        // Control: the map may name "no body" (-1).
        Assert.Equal([0, -1, 2, 3], HavokRagdoll.ReadPhysics(With(boneToBody: [0, -1, 2, 3]))!.BoneToBody);
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.ReadPhysics(With(boneToBody: [0, 1, 2, 4])));    // body 4 of 4
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.ReadPhysics(With(boneToBody: [0, 1, -2, 3])));
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.ReadPhysics(With(boneToBody: [0, 1, 2])));       // fewer entries than bones

        var m = Mapper();
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.ReadPhysics(With(m with { Mappings = [(4, 1, Vector3.Zero, Quaternion.Identity)] })));   // ragdoll bone 4 of 4
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.ReadPhysics(With(m with { Mappings = [(-1, 1, Vector3.Zero, Quaternion.Identity)] })));
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.ReadPhysics(With(m with { Mappings = [(0, 6, Vector3.Zero, Quaternion.Identity)] })));   // animation bone 6 of 6
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.ReadPhysics(With(m with { Mappings = [(0, 1, Vector3.Zero, new Quaternion(0, 0, 0, 2))] })));   // not a rotation
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.ReadPhysics(With(m with { Chains = [(0, 4, 0, 2)] })));      // ragdoll end out of range
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.ReadPhysics(With(m with { Chains = [(-1, 1, 0, 2)] })));
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.ReadPhysics(With(m with { Chains = [(0, 1, 0, 6)] })));      // animation end out of range
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.ReadPhysics(With(m with { Chains = [(0, 1, -1, 2)] })));
        Assert.Throws<InvalidDataException>(() => HavokRagdoll.ReadPhysics(With(m with { Unmapped = [6] })));
        Assert.NotNull(HavokRagdoll.ReadPhysics(With(m with { Chains = [(0, 3, 0, 5)], Unmapped = [0] })));                 // control: the last valid indices
    }

    [Fact]
    public void The_content_model_puts_shapes_in_body_frames_and_names_each_body_s_bone_and_parent()
    {
        var model = RagdollModel.FromPackfile(Pack())!;
        Assert.Equal(4, model.Bodies.Count);
        Near(340f, model.TotalMass, 1e-2f);
        Assert.Equal([-1, 0, 0, 2], model.Bodies.Select(b => b.Parent));
        Assert.Equal(["RIG_Pelvis", "RIG_SpineD", "RIG_L_Calf", "RIG_L_Foot"], model.Bodies.Select(b => b.Bone));   // the name match, not the first mapping
        var chest = model.Bodies[model.IndexOf("ragdoll_rig_chest")];
        Near(new Vector3(0, 0, 1.55f), chest.RestPosition);
        var sphere = Assert.Single(chest.Shapes);
        Near(new Vector3(0.2f, 0, 0), sphere.A);   // the translate shape's offset, in the body frame (in model space it is up)
        Assert.True(Quaternion.Dot(Quaternion.Identity, sphere.Rotation) > 0.9999f);
        var calf = model.Bodies[2];
        Near(new Vector3(0, 0, -0.4f), calf.Shapes[0].B);
        Assert.Equal(150f, chest.Mass, 3);
        Assert.Equal(3, model.Joints.Count);
        Assert.Equal(-1, model.IndexOf("Ragdoll_RIG_Head"));
    }

    [Fact]
    public void A_hull_turned_into_the_body_frame_keeps_its_planes_consistent_with_its_vertices()
    {
        float half = 0.2f, radius = 0.01f, core = half - radius;
        var vertices = Enumerable.Range(0, 8).Select(i => new Vector3((i & 1) == 0 ? -core : core, (i & 2) == 0 ? -core : core, (i & 4) == 0 ? -core : core)).ToArray();
        Vector4[] planes = [new(1, 0, 0, -half), new(-1, 0, 0, -half), new(0, 1, 0, -half), new(0, -1, 0, -half), new(0, 0, 1, -half), new(0, 0, -1, -half)];
        var bodies = Bodies();
        bodies[0] = bodies[0] with { Shape = new TranslateFixture(new Vector3(0.1f, 0, 0), new HullFixture(new Vector3(core), Vector3.Zero, radius, vertices, planes)) };
        var pelvis = RagdollModel.FromPackfile(Pack(bodies: bodies))!.Bodies[0];
        var hull = Assert.Single(pelvis.Shapes);
        Assert.Equal(RagdollShapeKind.Hull, hull.Kind);
        Near(new Vector3(0.1f + core, core, core), hull.Vertices!.OrderByDescending(v => v.X + v.Y + v.Z).First());   // body frame: shifted along local X only
        foreach (var v in hull.Vertices!)
            Near(-radius, hull.Planes!.Max(p => p.X * v.X + p.Y * v.Y + p.Z * v.Z + p.W), 1e-4f);   // every vertex sits the radius inside its nearest plane
    }

    [Fact]
    public void Death_values_are_read_from_the_blueprint_and_the_global_force_levels()
    {
        var blueprint = BafDocument.Read(SyntheticBafWriter.Build(("extensions", new (string, object)[]
        {
            ("health_ext", new (string, object)[] { ("ragdoll_death", true), ("death_time", 5f), ("remove_from_world_on_death", true) }),
            ("biped_locomotion_ext", new (string, object)[] { ("ragdoll_force_multiplier", 1.2f), ("force_required_to_lift_off_feet", 2000) }),
        })));
        var global = BafDocument.Read(SyntheticBafWriter.Build(("extensions", new (string, object)[]
        {
            ("weight_and_strength", new (string, object)[]
            {
                ("weights", new (string, object)[] { ("spacemarine", 500f) }),
                ("forces", new (string, object)[] { ("medium", 2500f), ("strong", 4000) }),
            }),
        })));
        var death = DeathTuning.From(blueprint, global)!;
        Assert.Equal((true, 5f, true, 1.2f, 2000f, 500f), (death.RagdollDeath, death.DeathTime, death.RemoveFromWorldOnDeath, death.RagdollForceMultiplier, death.ForceToLiftOffFeet, death.SpaceMarineWeight));
        Assert.Equal(2500f, death.Forces["Medium"]);
        Assert.Equal(4000f, death.Forces["strong"]);

        var notes = new List<string>();
        var empty = BafDocument.Read(SyntheticBafWriter.Build(("extensions", new (string, object)[] { ("health_ext", new (string, object)[] { ("death_time", 5f) }) })));
        Assert.Null(DeathTuning.From(empty, global, notes));
        Assert.NotEmpty(notes);
    }
}
