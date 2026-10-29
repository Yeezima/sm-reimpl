using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmFormats.Havok;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The static description of the marine's death ragdoll (M23), built once from the original ragdoll
/// (<see cref="RagdollModel"/>: 17 rigid bodies with mass, inertia, centre of mass, material and shapes in their own frames, 16
/// joints with the original limits, and the ragdoll-to-animation skeleton mapping). Each body drives the animation bones the mapper
/// names (the pose of an animation bone in its body's frame is taken from the rest pose, <see cref="Drive.Local"/>); the spine and
/// neck bones the mapper's chains span follow a blend of the two bodies at the chain's ends. Shapes, masses, inertia, limits, material and
/// the mapping are **supported by evidence** (original data); how Godot's physics maps them (joint limits as a 6-DOF's Euler limits,
/// Havok's joint friction as extra angular damping) is an approximation. Godot shape resources are shared by every corpse.
/// </summary>
public sealed class RagdollDef
{
    /// <summary>One primitive of a body: the Godot shape and its transform in the body's frame.</summary>
    public sealed record ShapeDef(Shape3D Shape, Transform3D Local);

    public sealed class Body
    {
        public string Name = "";
        public int Parent = -1;                  // index into Bodies, or -1 for the root body
        public int Bone = -1;                    // the animation bone it mainly drives
        public Transform3D BoneLocal;            // that bone's rest pose in the body's frame
        public List<ShapeDef> Shapes = new();
        public float Mass;
        public Vector3 Inertia, CenterOfMass;    // diagonal inertia and centre of mass in the body's frame
        public float Friction, Restitution, LinearDamp, AngularDamp, GravityScale = 1f;
    }

    /// <summary>A joint: <see cref="Child"/> is Havok's body A, <see cref="Parent"/> body B; frames (pivot and axes, X = twist or hinge axis, Y = plane axis) in each body's frame; angles in radians.</summary>
    public sealed class Joint
    {
        public string Name = "";
        public int Child, Parent;
        public Transform3D FrameChild, FrameParent;
        public bool Hinge;
        public float TwistMin, TwistMax, Cone, PlaneMin, PlaneMax;
    }

    /// <summary>An animation bone driven by a body: its pose in that body's frame at rest.</summary>
    public sealed record Drive(int Body, Transform3D Local);

    /// <summary>Animation bones <see cref="Bones"/> (in order from <see cref="BodyA"/>'s end to <see cref="BodyB"/>'s) follow a blend of the two bodies' motion, by <see cref="Fraction"/> toward B.</summary>
    public sealed record Chain(int[] Bones, float[] Fraction, int BodyA, int BodyB);

    public Body[] Bodies { get; private set; } = [];
    public Joint[] Joints { get; private set; } = [];
    /// <summary>Per animation bone: what drives it, or null.</summary>
    public Drive?[] DriveOfBone { get; private set; } = [];
    public Chain[] Chains { get; private set; } = [];
    public int FocusBody { get; private set; }
    public float TotalMass { get; private set; }
    public string Description { get; private set; } = "";

    public static RagdollDef? Build(RagdollModel model, MarineAssets assets, List<string> notes)
    {
        try { return BuildCore(model, assets, notes); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException)
        {
            notes.Add($"ragdoll: not built ({ex.Message}); dead players disappear instead");
            return null;
        }
    }

    static RagdollDef? BuildCore(RagdollModel model, MarineAssets assets, List<string> notes)
    {
        var skeleton = assets.Model.Rig.Skeleton;
        int boneCount = skeleton.BoneNames.Count;
        var restModel = skeleton.ModelSpace(skeleton.ReferencePose).Select(MarineAssets.ToGodot).ToArray();
        int BoneIndex(string name)
        {
            for (int i = 0; i < boneCount; i++) if (string.Equals(skeleton.BoneNames[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
        var def = new RagdollDef { DriveOfBone = new Drive?[boneCount] };

        // Bodies.
        var rests = new Transform3D[model.Bodies.Count];
        def.Bodies = new Body[model.Bodies.Count];
        for (int i = 0; i < model.Bodies.Count; i++)
        {
            var mb = model.Bodies[i];
            var p = mb.Physics;
            rests[i] = new Transform3D(new Basis(new Quaternion(mb.RestRotation.X, mb.RestRotation.Y, mb.RestRotation.Z, mb.RestRotation.W).Normalized()), G(mb.RestPosition));
            var body = new Body
            {
                Name = mb.Name.StartsWith("Ragdoll_RIG_", StringComparison.OrdinalIgnoreCase) ? mb.Name["Ragdoll_RIG_".Length..] : mb.Name,
                Parent = mb.Parent, Mass = Math.Max(p.Mass, 0.1f), Inertia = G(p.Inertia), CenterOfMass = G(p.CenterOfMass),
                Friction = p.Friction, Restitution = p.Restitution, LinearDamp = p.LinearDamping, AngularDamp = p.AngularDamping, GravityScale = p.GravityFactor,
            };
            foreach (var s in mb.Shapes) if (MakeShape(s) is { } shape) body.Shapes.Add(shape);
            if (body.Shapes.Count == 0) { notes.Add($"ragdoll body '{mb.Name}': no usable shape"); return null; }
            def.Bodies[i] = body;
        }
        def.TotalMass = def.Bodies.Sum(b => b.Mass);

        // Which animation bones each body drives (the mapper), and the pose of each in its body's frame at rest.
        int BodyOfRagdollBone(int ragdollBone) => ragdollBone >= 0 && ragdollBone < model.SkeletonBones.Count ? model.IndexOf(model.SkeletonBones[ragdollBone]) : -1;
        float worstMapping = 0f;
        foreach (var m in model.ToAnimation)
        {
            int body = BodyOfRagdollBone(m.RagdollBone), bone = BoneIndex(m.AnimationBone);
            if (body < 0 || bone < 0) { notes.Add($"ragdoll mapping {m.AnimationBone}: body or bone not found"); continue; }
            var local = rests[body].AffineInverse() * restModel[bone];
            // Cross-check with the mapper's own transform (the animation bone's translation in the body's frame).
            worstMapping = MathF.Max(worstMapping, local.Origin.DistanceTo(G(m.Translation)));
            def.DriveOfBone[bone] = new Drive(body, local);
        }
        if (worstMapping > 0.01f) notes.Add($"ragdoll: the mapper's translations differ from the rest poses' by up to {worstMapping:F3} m (the rest poses are used)");
        for (int i = 0; i < def.Bodies.Length; i++)
        {
            int bone = model.Bodies[i].Bone is { } name ? BoneIndex(name) : -1;
            if (bone < 0 || def.DriveOfBone[bone]?.Body != i) bone = Array.FindIndex(def.DriveOfBone, d => d?.Body == i);
            if (bone < 0) { notes.Add($"ragdoll body '{def.Bodies[i].Name}' drives no animation bone"); return null; }
            def.Bodies[i].Bone = bone;
            def.Bodies[i].BoneLocal = def.DriveOfBone[bone]!.Local;
        }

        // Joints.
        def.Joints = model.Joints.Select(j => new Joint
        {
            Name = def.Bodies[j.BodyA].Name, Child = j.BodyA, Parent = j.BodyB, Hinge = j.Kind == RagdollJointKind.LimitedHinge,
            FrameChild = Frame(j.FrameA, j.PivotA), FrameParent = Frame(j.FrameB, j.PivotB),
            TwistMin = j.MinAngle, TwistMax = j.MaxAngle, Cone = j.ConeAngle, PlaneMin = j.PlaneMin, PlaneMax = j.PlaneMax,
        }).ToArray();

        // Chains: the animation bones strictly between the chain's two ends that nothing drives.
        var chains = new List<Chain>();
        foreach (var c in model.Chains)
        {
            int a = BodyOfRagdollBone(c.RagdollStart), b = BodyOfRagdollBone(c.RagdollEnd), top = BoneIndex(c.AnimationStart), bottom = BoneIndex(c.AnimationEnd);
            if (a < 0 || b < 0 || top < 0 || bottom < 0) { notes.Add($"ragdoll chain {c.AnimationStart}..{c.AnimationEnd}: not found"); continue; }
            var between = new List<int>();
            for (int bone = skeleton.Parents[bottom]; bone >= 0 && bone != top; bone = skeleton.Parents[bone]) between.Add(bone);
            between.Reverse();
            var free = between.Where(x => def.DriveOfBone[x] is null).ToArray();
            if (free.Length > 0)
                chains.Add(new Chain(free, free.Select(x => (between.IndexOf(x) + 1f) / (between.Count + 1f)).ToArray(), a, b));
        }
        def.Chains = chains.ToArray();
        def.FocusBody = Math.Max(0, Array.FindIndex(def.Bodies, b => b.Name.Equals("Chest", StringComparison.OrdinalIgnoreCase)));
        def.Description = $"{def.Bodies.Length} bodies, {def.Bodies.Sum(b => b.Shapes.Count)} shapes, {def.Joints.Length} joints ({def.Joints.Count(j => j.Hinge)} hinges), {def.TotalMass:F0} kg, " +
                          $"{def.DriveOfBone.Count(d => d is not null)} bones driven, spine/neck bones between: {string.Join("+", def.Chains.Select(c => c.Bones.Length))}";
        return def;
    }

    static Vector3 G(Num.Vector3 v) => new(v.X, v.Y, v.Z);

    static Transform3D Frame(Num.Quaternion rotation, Num.Vector3 pivot) =>
        new(new Basis(new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W).Normalized()), G(pivot));

    /// <summary>A ragdoll primitive (in the body's frame) as a Godot shape; hulls are inflated by their convex radius, boxes already include theirs. Null for a degenerate shape.</summary>
    static ShapeDef? MakeShape(RagdollShape s)
    {
        switch (s.Kind)
        {
            case RagdollShapeKind.Sphere:
                return new ShapeDef(new SphereShape3D { Radius = s.Radius }, new Transform3D(Basis.Identity, G(s.A)));
            case RagdollShapeKind.Capsule:
            {
                var a = G(s.A); var b = G(s.B);
                float length = a.DistanceTo(b);
                if (length < 1e-4f) return new ShapeDef(new SphereShape3D { Radius = s.Radius }, new Transform3D(Basis.Identity, a));
                var basis = new Basis(new Quaternion(Vector3.Up, (b - a) / length));
                return new ShapeDef(new CapsuleShape3D { Radius = s.Radius, Height = length + 2f * s.Radius }, new Transform3D(basis, (a + b) * 0.5f));
            }
            case RagdollShapeKind.Box:
            {
                var half = G(s.B);
                if (half.X <= 0f || half.Y <= 0f || half.Z <= 0f) return null;
                var q = new Quaternion(s.Rotation.X, s.Rotation.Y, s.Rotation.Z, s.Rotation.W).Normalized();
                return new ShapeDef(new BoxShape3D { Size = half * 2f }, new Transform3D(new Basis(q), G(s.A)));
            }
            default:
            {
                if (s.Vertices is not { Count: >= 4 } vertices) return null;
                var points = vertices.Select(G).ToArray();
                var centre = points.Aggregate(Vector3.Zero, (x, y) => x + y) / points.Length;
                var inflated = points.Select(p => { var d = p - centre; return d.LengthSquared() > 1e-8f ? p + d.Normalized() * s.Radius : p; }).ToArray();
                return new ShapeDef(new ConvexPolygonShape3D { Points = inflated }, Transform3D.Identity);
            }
        }
    }
}

/// <summary>
/// A dead player's body (M23): the character's last animated pose turned into a physics ragdoll. Each original ragdoll body
/// becomes a <see cref="RigidBody3D"/> (its original mass, inertia, centre of mass, friction and restitution) at the pose its bone had,
/// joined to its parent body by a <see cref="Generic6DofJoint3D"/> with the original limits (position locked; twist about the joint's X axis,
/// swing by the cone and plane angles; a limited hinge turns about X only); every frame the bodies' transforms are written back to the
/// skeleton (bones between the two ends of a chain blend between the end bodies, unmapped bones keep their animated local pose).
/// Bodies collide with the map's static collision and with their own ragdoll's other bodies (not their parent, as in the original's
/// collision filter), on a collision layer of their own per slot, so ragdolls do not collide with each other. After it has been still
/// for <see cref="RestSeconds"/> seconds (or after <see cref="MaxSeconds"/>) the pose is kept and all physics nodes are freed.
/// **Placeholder demonstration** around original ragdoll data.
/// </summary>
public sealed partial class Ragdoll : Node3D
{
    public static float RestSeconds = 3f;          // still for this long: bake the pose and free the physics (user's request: ~3 s)
    public const float MaxSeconds = 12f;           // bake after this long whatever happens
    const float RestLinear = 0.3f, RestAngular = 2f;   // m/s and rad/s below which a body counts as still
    /// <summary>Joint friction (Havok's max friction torque on every axis, 10 to 20 N·m) is not available on Godot's joints: this much angular damping stands in for it (provisional).</summary>
    const float JointFrictionDamping = 1.0f;
    public const uint WorldLayer = 1;              // physics layer of the map's static body
    public const int MaxSlots = 16;
    public static bool DebugLog;
    /// <summary>The time of each step of the last <see cref="Start"/> (M40-T9 diagnostics, logged by the view when a death is slow).</summary>
    public static string LastSplits = "";
    /// <summary>Godot-space height below which a body counts as fallen out of the world (set from the map's lowest collision; 50 m below it).</summary>
    public static float LostBelowY = -300f;
    /// <summary>The body fell out of the world (hidden; <see cref="FocusPoint"/> stays where it died).</summary>
    public bool Lost { get; private set; }

    /// <summary>Whose body it is (the player id).</summary>
    public int PlayerId { get; init; }
    public CharacterViewer? Character { get; private set; }
    public bool Simulating { get; private set; }
    public double Age { get; private set; }
    public int Slot { get; private set; } = -1;
    /// <summary>Seconds simulated before the pose was baked (-1 while simulating).</summary>
    public double SettledAfter { get; private set; } = -1;
    public bool Own { get; init; }

    RagdollDef? _def;
    Skeleton3D? _skeleton;
    RigidBody3D?[] _bodies = [];
    Generic6DofJoint3D?[] _joints = [];
    Transform3D[] _deathGlobal = [];   // every bone's skeleton-space pose when it died
    Transform3D[] _deathBody = [];     // every body's skeleton-space frame when it died
    double _stillFor;
    Vector3 _deathFocus;

    /// <summary>Builds the corpse from a living character's pose and starts the simulation.</summary>
    /// <param name="velocity">The body's velocity at death (Godot world space, m/s), given to every body.</param>
    /// <param name="kick">The push on top of it (Godot world space, m/s), given to every body; the chest gets half as much again.</param>
    public void Start(RagdollDef def, CharacterViewer source, int slot, Vector3 velocity, Vector3 kick)
    {
        _def = def;
        Slot = slot;
        var split = System.Diagnostics.Stopwatch.StartNew();
        double tCharacter = 0, tPose = 0, tWeapons = 0, tBodies = 0;
        var character = new CharacterViewer(source.Assets!, null, gameSpaceRoot: false) { Name = "Corpse", PoseLocked = true };
        AddChild(character);
        character.Transform = source.Transform;
        tCharacter = split.Elapsed.TotalMilliseconds; split.Restart();
        character.CopyPoseFrom(source);
        tPose = split.Elapsed.TotalMilliseconds; split.Restart();
        // M24: the corpse keeps the weapons where they were (held or stowed) and the worn models; they follow the baked bones.
        try { if (source.Weapons is { Faulted: false } weapons && character.Skeleton is { } corpseSkeleton) character.Weapons = weapons.CloneTo(corpseSkeleton); }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException)) { GD.PrintErr($"corpse weapons: {ex.GetType().Name}: {ex.Message}"); }
        foreach (var suffix in source.HiddenPieces) character.SetPieceHidden(suffix, true);
        tWeapons = split.Elapsed.TotalMilliseconds; split.Restart();
        Character = character;
        _skeleton = character.Skeleton;
        if (_skeleton is null) return;
        int bones = _skeleton.GetBoneCount();
        _deathGlobal = new Transform3D[bones];
        for (int b = 0; b < bones; b++) _deathGlobal[b] = _skeleton.GetBoneGlobalPose(b);

        uint layer = 1u << (slot + 1);
        int n = def.Bodies.Length;
        _bodies = new RigidBody3D?[n];
        _joints = new Generic6DofJoint3D?[def.Joints.Length];
        _deathBody = new Transform3D[n];
        var rng = new RandomNumberGenerator { Seed = (ulong)(PlayerId * 7919 + 17) };
        var skeletonToWorld = _skeleton.GlobalTransform;
        for (int i = 0; i < n; i++)
        {
            var b = def.Bodies[i];
            _deathBody[i] = _deathGlobal[b.Bone] * b.BoneLocal.AffineInverse();
            var body = new RigidBody3D
            {
                Name = b.Name, Mass = b.Mass, CollisionLayer = layer, CollisionMask = WorldLayer | layer,
                CenterOfMassMode = RigidBody3D.CenterOfMassModeEnum.Custom, CenterOfMass = b.CenterOfMass,
                LinearDamp = b.LinearDamp, AngularDamp = b.AngularDamp + JointFrictionDamping, GravityScale = b.GravityScale,
                PhysicsMaterialOverride = new PhysicsMaterial { Friction = b.Friction, Bounce = b.Restitution }, CanSleep = true, ContinuousCd = true,
            };
            if (b.Inertia.X > 0f && b.Inertia.Y > 0f && b.Inertia.Z > 0f) body.Inertia = b.Inertia;
            AddChild(body);
            foreach (var s in b.Shapes) body.AddChild(new CollisionShape3D { Shape = s.Shape, Transform = s.Local });
            body.GlobalTransform = skeletonToWorld * _deathBody[i];
            float scale = i == def.FocusBody ? 1.5f : 1f;
            body.LinearVelocity = velocity + kick * scale;
            body.AngularVelocity = new Vector3(rng.RandfRange(-1f, 1f), rng.RandfRange(-1f, 1f), rng.RandfRange(-1f, 1f)) * (kick.Length() > 0.1f ? 1.5f : 0.3f);
            _bodies[i] = body;
        }
        for (int i = 0; i < n; i++)
            if (def.Bodies[i].Parent >= 0 && _bodies[def.Bodies[i].Parent] is { } parent) _bodies[i]!.AddCollisionExceptionWith(parent);   // the original's filter: not with the parent body
        tBodies = split.Elapsed.TotalMilliseconds; split.Restart();

        for (int k = 0; k < def.Joints.Length; k++)
        {
            var j = def.Joints[k];
            var parentWorld = skeletonToWorld * _deathBody[j.Parent];
            var frameParent = parentWorld * j.FrameParent;
            var frameChild = skeletonToWorld * _deathBody[j.Child] * j.FrameChild;
            // The joint's zero is the pose it is created in (the animated pose), so the original limits (about the rest pose) are shifted by
            // the angles the child's frame already has relative to the parent's, and widened to include the start.
            var delta = (frameParent.Basis.Orthonormalized().Inverse() * frameChild.Basis.Orthonormalized()).GetEuler(EulerOrder.Xyz);
            var joint = new Generic6DofJoint3D { Name = "Joint_" + j.Name };
            AddChild(joint);
            joint.GlobalTransform = new Transform3D(frameParent.Basis.Orthonormalized(), frameParent.Origin);
            joint.NodeA = joint.GetPathTo(_bodies[j.Parent]);
            joint.NodeB = joint.GetPathTo(_bodies[j.Child]);
            for (int axis = 0; axis < 3; axis++)
            {
                SetFlag(joint, axis, Generic6DofJoint3D.Flag.EnableLinearLimit, true);
                SetParam(joint, axis, Generic6DofJoint3D.Param.LinearLowerLimit, 0f);
                SetParam(joint, axis, Generic6DofJoint3D.Param.LinearUpperLimit, 0f);
                SetFlag(joint, axis, Generic6DofJoint3D.Flag.EnableAngularLimit, true);
            }
            if (j.Hinge)
            {
                SetAngular(joint, 0, j.TwistMin, j.TwistMax, delta.X);
                SetAngular(joint, 1, 0f, 0f, delta.Y);
                SetAngular(joint, 2, 0f, 0f, delta.Z);
            }
            else
            {
                // Twist about X; the swing: the cone limits the tilt of the twist axis toward Y and Z, the plane limit the tilt toward Y.
                SetAngular(joint, 0, j.TwistMin, j.TwistMax, delta.X);
                SetAngular(joint, 1, -j.Cone, j.Cone, delta.Y);
                SetAngular(joint, 2, Math.Max(j.PlaneMin, -j.Cone), Math.Min(j.PlaneMax, j.Cone), delta.Z);
            }
            _joints[k] = joint;
        }
        LastSplits = $"corpse character {tCharacter:F1} ms, pose copy {tPose:F1}, weapons and hidden pieces {tWeapons:F1}, bodies {tBodies:F1}, joints {split.Elapsed.TotalMilliseconds:F1}";
        Simulating = true;
        _deathFocus = (skeletonToWorld * _deathGlobal[def.Bodies[def.FocusBody].Bone]).Origin;
        Sync();
    }

    /// <summary>Limits (radians) shifted by the starting angle and widened to include it (the joint's zero is the pose at creation).</summary>
    static void SetAngular(Generic6DofJoint3D joint, int axis, float min, float max, float start)
    {
        float lo = Math.Min(min - start, 0f), hi = Math.Max(max - start, 0f);
        SetParam(joint, axis, Generic6DofJoint3D.Param.AngularLowerLimit, Math.Max(lo, -MathF.PI + 0.01f));
        SetParam(joint, axis, Generic6DofJoint3D.Param.AngularUpperLimit, Math.Min(hi, MathF.PI - 0.01f));
    }

    static void SetParam(Generic6DofJoint3D j, int axis, Generic6DofJoint3D.Param p, float v)
    {
        if (axis == 0) j.SetParamX(p, v); else if (axis == 1) j.SetParamY(p, v); else j.SetParamZ(p, v);
    }

    static void SetFlag(Generic6DofJoint3D j, int axis, Generic6DofJoint3D.Flag f, bool v)
    {
        if (axis == 0) j.SetFlagX(f, v); else if (axis == 1) j.SetFlagY(f, v); else j.SetFlagZ(f, v);
    }

    /// <summary>The corpse's chest in Godot world space (the camera's focus).</summary>
    public Vector3 FocusPoint
    {
        get
        {
            if (Lost) return _deathFocus;
            if (_skeleton is null || _def is null) return GlobalPosition;
            return (_skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(_def.Bodies[_def.FocusBody].Bone)).Origin;
        }
    }

    public override void _Process(double delta)
    {
        if (!Simulating || _skeleton is null || _def is null) return;
        Age += delta;
        Sync();
        float linear = 0f, angular = 0f;
        foreach (var b in _bodies)
        {
            if (b is null) continue;
            linear = MathF.Max(linear, b.LinearVelocity.Length());
            angular = MathF.Max(angular, b.AngularVelocity.Length());
        }
        // Still for RestSeconds, or too long: bake. Also bake when the body has fallen out of the world.
        _stillFor = linear < RestLinear && angular < RestAngular ? _stillFor + delta : 0.0;
        if (DebugLog && (int)(Age * 2) != (int)((Age - delta) * 2)) GD.Print($"  ragdoll P{PlayerId} age {Age:F1} s: max linear {linear:F2} m/s, max angular {angular:F2} rad/s, still for {_stillFor:F1} s");
        bool lost = _bodies[_def.FocusBody] is { } chest && chest.GlobalPosition.Y < LostBelowY;
        if (lost) { Lost = true; if (Character is not null) Character.Visible = false; }   // fell out of the world: hide the body, the camera stays at the death place
        if (_stillFor >= RestSeconds || Age >= MaxSeconds || lost) Bake();
    }

    /// <summary>Writes the bodies' transforms into the skeleton: driven bones from their body, chain bones blended, the rest keep their local pose.</summary>
    void Sync()
    {
        var def = _def!; var skeleton = _skeleton!;
        var toSkeleton = skeleton.GlobalTransform.AffineInverse();
        int n = def.Bodies.Length;
        var now = new Transform3D[n];
        for (int i = 0; i < n; i++) if (_bodies[i] is { } body) now[i] = toSkeleton * body.GlobalTransform;
        int bones = skeleton.GetBoneCount();
        for (int b = 0; b < bones; b++)
        {
            if (def.DriveOfBone[b] is { } drive)
            {
                if (_bodies[drive.Body] is not null) skeleton.SetBoneGlobalPose(b, now[drive.Body] * drive.Local);
                continue;
            }
            foreach (var chain in def.Chains)
            {
                int k = Array.IndexOf(chain.Bones, b);
                if (k < 0) continue;
                // As each end body carries the bone rigidly since death, blended toward the far end.
                var fromA = now[chain.BodyA] * _deathBody[chain.BodyA].AffineInverse() * _deathGlobal[b];
                var fromB = now[chain.BodyB] * _deathBody[chain.BodyB].AffineInverse() * _deathGlobal[b];
                float t = chain.Fraction[k];
                var q = fromA.Basis.GetRotationQuaternion().Slerp(fromB.Basis.GetRotationQuaternion(), t);
                skeleton.SetBoneGlobalPose(b, new Transform3D(new Basis(q), fromA.Origin.Lerp(fromB.Origin, t)));
                break;
            }
        }
    }

    /// <summary>Keeps the final pose and frees every physics node.</summary>
    public void Bake()
    {
        if (!Simulating) return;
        Sync();
        SettledAfter = Age;
        Simulating = false;
        foreach (var j in _joints) j?.QueueFree();
        foreach (var b in _bodies) b?.QueueFree();
        _joints = []; _bodies = [];
    }
}
