using System;
using System.Collections.Generic;
using Godot;
using SmFormats.Havok;
using SmNet.Extras.Gore;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Real ragdolls for the Orks a blast kills in Musou Mode (the owner, 2026-11-02: "the bodies should ragdoll as they fly"). A pool of <see cref="Dolls"/> physics rigs is made at load, one a frame, from
/// the Exterminatus boy's original ragdoll (<see cref="RagdollDef"/>: shapes, masses, inertia, joint limits), so a barrage's many deaths build nothing: a free doll is moved to the Ork, woken and thrown,
/// and wears that look's meshes. The rigs start from one pose (a frame of the run clip), since a joint's zero is the pose it is created in. Unlike a player's <see cref="Ragdoll"/> a doll's parts do not
/// collide with each other (only with the map's static world), which keeps a pool of them affordable. Only the Orks on the boy's rig are thrown here, and the Warboss is not; a Gretchin is the same
/// physics drawn smaller round the chest. Past the pool, or far from the camera, the crowd throws a tumbling body instead (<see cref="MusouCrowd.AddFlung"/>). Every number here is ours (spec "Flung corpses").
/// </summary>
public sealed partial class MusouRagdolls : Node3D
{
    /// <summary>Dolls in the pool, the distance from the camera within which one is used, and the seconds of physics, stillness and lying (ours).</summary>
    public const int Dolls = 20;
    public const float Metres = 45f, MaxPhysicsSeconds = 6f, StillSeconds = 0.6f, LieSeconds = 3f;
    const float StillLinear = 0.4f, StillAngular = 2.5f, JointFrictionDamping = 1f, LimbScatter = 1.5f, LimbSpin = 8f;

    sealed class Doll
    {
        public required Node3D Root;
        public required Skeleton3D Skeleton;
        public required RigidBody3D[] Bodies;
        public readonly Dictionary<MusouLooks.Look, Node3D> Worn = new(ReferenceEqualityComparer.Instance);
        public Node3D? Showing;
        public bool InUse, Frozen;
        public float Age, Still, Lying, Scale, StartZ;
        public int Serial;
    }

    readonly RagdollDef _def;
    readonly HavokBoundAnimation _startClip;
    readonly MarineAssets _rig;
    readonly List<Doll> _dolls = new();
    readonly Transform3D[] _now;
    Transform3D[] _startBody = [], _startBone = [];
    int _serial;

    /// <summary>Dolls thrown so far, the highest a doll's chest has risen above its start (m), and the dolls in use now.</summary>
    public int Thrown { get; private set; }
    public float Highest { get; private set; }
    public int Built => _dolls.Count;
    public int Active { get { int n = 0; foreach (var d in _dolls) if (d.InUse) n++; return n; } }

    public MusouRagdolls(RagdollDef def, MarineAssets rig, HavokBoundAnimation startClip)
    {
        Name = "MusouRagdolls";
        _def = def; _rig = rig; _startClip = startClip;
        _now = new Transform3D[def.Bodies.Length];
    }

    /// <summary>Whether this kind of enemy is thrown as a doll (the boy's rig, and not the boss).</summary>
    public static bool Fits(MusouEnemyType type) => type is MusouEnemyType.Boy or MusouEnemyType.ShootaBoy or MusouEnemyType.ArdBoy or MusouEnemyType.Gretchin or MusouEnemyType.Nob;

    /// <summary>The next doll of the pool, if any is still to be made (one a frame, so the load shows no hitch).</summary>
    public void BuildStep()
    {
        if (_dolls.Count < Dolls) _dolls.Add(Build());
    }

    Doll Build()
    {
        var skeleton = _rig.Model.Rig.Skeleton;
        var root = new Node3D { Name = "Doll" + _dolls.Count, Visible = false };
        var sk = new Skeleton3D { Name = "Skeleton" };
        root.AddChild(sk);
        var rest = new Transform3D[skeleton.BoneNames.Count];
        for (int i = 0; i < rest.Length; i++)
        {
            sk.AddBone(skeleton.BoneNames[i]);
            if (skeleton.Parents[i] >= 0) sk.SetBoneParent(i, skeleton.Parents[i]);
            rest[i] = MarineAssets.ToGodot(skeleton.ReferencePose[i]);
            sk.SetBoneRest(i, rest[i]);
        }
        sk.ResetBonePoses();
        AddChild(root);
        if (_startBone.Length == 0) CapturePose(sk, rest);

        int n = _def.Bodies.Length;
        var bodies = new RigidBody3D[n];
        for (int i = 0; i < n; i++)
        {
            var b = _def.Bodies[i];
            var body = new RigidBody3D
            {
                Name = b.Name, Mass = b.Mass, CollisionLayer = 0, CollisionMask = 0, Freeze = true, FreezeMode = RigidBody3D.FreezeModeEnum.Static,
                CenterOfMassMode = RigidBody3D.CenterOfMassModeEnum.Custom, CenterOfMass = b.CenterOfMass,
                LinearDamp = b.LinearDamp, AngularDamp = b.AngularDamp + JointFrictionDamping, GravityScale = b.GravityScale,
                PhysicsMaterialOverride = new PhysicsMaterial { Friction = b.Friction, Bounce = b.Restitution }, ContinuousCd = true,
            };
            if (b.Inertia.X > 0f && b.Inertia.Y > 0f && b.Inertia.Z > 0f) body.Inertia = b.Inertia;
            AddChild(body);
            foreach (var s in b.Shapes) body.AddChild(new CollisionShape3D { Shape = s.Shape, Transform = s.Local });
            body.Transform = _startBody[i];
            bodies[i] = body;
        }
        foreach (var j in _def.Joints) AddJoint(j, bodies);
        return new Doll { Root = root, Skeleton = sk, Bodies = bodies };
    }

    /// <summary>The pose every doll starts from: the start clip at a fixed time, in skeleton space, for the bones and for the bodies that drive them.</summary>
    void CapturePose(Skeleton3D sk, Transform3D[] rest)
    {
        var sample = new HavokTransform[_startClip.Animation.TrackCount];
        _startClip.Animation.SampleInto(MathF.Min(0.15f, _startClip.Animation.Duration), sample);
        var pose = (Transform3D[])rest.Clone();
        for (int k = 0; k < sample.Length; k++)
        {
            int bone = _startClip.TrackToBone[k];
            if ((uint)bone < (uint)pose.Length) pose[bone] = MarineAssets.ToGodot(sample[k]);
        }
        MusouVat.LockRoot(pose, rest);
        for (int b = 0; b < pose.Length; b++)
        {
            sk.SetBonePosePosition(b, pose[b].Origin);
            sk.SetBonePoseRotation(b, pose[b].Basis.GetRotationQuaternion());
        }
        _startBone = new Transform3D[pose.Length];
        for (int b = 0; b < pose.Length; b++) _startBone[b] = sk.GetBoneGlobalPose(b);
        _startBody = new Transform3D[_def.Bodies.Length];
        for (int i = 0; i < _startBody.Length; i++)
            _startBody[i] = (_startBone[_def.Bodies[i].Bone] * _def.Bodies[i].BoneLocal.AffineInverse()).Orthonormalized();
    }

    /// <summary>A joint as <see cref="Ragdoll"/> makes it: position locked, the original limits shifted by the start pose and widened to include it.</summary>
    void AddJoint(RagdollDef.Joint j, RigidBody3D[] bodies)
    {
        var frameParent = _startBody[j.Parent] * j.FrameParent;
        var frameChild = _startBody[j.Child] * j.FrameChild;
        var delta = (frameParent.Basis.Orthonormalized().Inverse() * frameChild.Basis.Orthonormalized()).GetEuler(EulerOrder.Xyz);
        var joint = new Generic6DofJoint3D { Name = "Joint_" + j.Name };
        AddChild(joint);
        joint.Transform = new Transform3D(frameParent.Basis.Orthonormalized(), frameParent.Origin);
        joint.NodeA = joint.GetPathTo(bodies[j.Parent]);
        joint.NodeB = joint.GetPathTo(bodies[j.Child]);
        for (int axis = 0; axis < 3; axis++)
        {
            Flag(joint, axis, Generic6DofJoint3D.Flag.EnableLinearLimit);
            Limit(joint, axis, Generic6DofJoint3D.Param.LinearLowerLimit, 0f);
            Limit(joint, axis, Generic6DofJoint3D.Param.LinearUpperLimit, 0f);
            Flag(joint, axis, Generic6DofJoint3D.Flag.EnableAngularLimit);
        }
        Angular(joint, 0, j.TwistMin, j.TwistMax, delta.X);
        if (j.Hinge) { Angular(joint, 1, 0f, 0f, delta.Y); Angular(joint, 2, 0f, 0f, delta.Z); }
        else { Angular(joint, 1, -j.Cone, j.Cone, delta.Y); Angular(joint, 2, Math.Max(j.PlaneMin, -j.Cone), Math.Min(j.PlaneMax, j.Cone), delta.Z); }
    }

    static void Angular(Generic6DofJoint3D joint, int axis, float min, float max, float start)
    {
        Limit(joint, axis, Generic6DofJoint3D.Param.AngularLowerLimit, Math.Max(Math.Min(min - start, 0f), -MathF.PI + 0.01f));
        Limit(joint, axis, Generic6DofJoint3D.Param.AngularUpperLimit, Math.Min(Math.Max(max - start, 0f), MathF.PI - 0.01f));
    }

    static void Limit(Generic6DofJoint3D j, int axis, Generic6DofJoint3D.Param p, float v)
    {
        if (axis == 0) j.SetParamX(p, v); else if (axis == 1) j.SetParamY(p, v); else j.SetParamZ(p, v);
    }

    static void Flag(Generic6DofJoint3D j, int axis, Generic6DofJoint3D.Flag f)
    {
        if (axis == 0) j.SetFlagX(f, true); else if (axis == 1) j.SetFlagY(f, true); else j.SetFlagZ(f, true);
    }

    /// <summary>Throws a doll wearing <paramref name="look"/> from the Ork's place at <paramref name="velocity"/> (game space, m/s). False with none free: the crowd throws a tumbling body.</summary>
    public bool Throw(in MusouDeathShown d, MusouLooks.Look look, Vector3 velocity)
    {
        if (look.Assets.Model.Rig.Skeleton.BoneNames.Count != _startBone.Length) return false;
        Doll? doll = null;
        foreach (var c in _dolls)
        {
            if (!c.InUse) { doll = c; break; }
            if (c.Frozen && (doll is null || c.Serial < doll.Serial)) doll = c;   // all busy: the one lying longest makes way
        }
        if (doll is null) return false;

        var place = new Transform3D(new Basis(new Vector3(0f, 0f, 1f), d.Yaw), new Vector3(d.Position.X, d.Position.Y, d.Position.Z));
        var toWorld = GlobalBasis;
        for (int i = 0; i < doll.Bodies.Length; i++)
        {
            var body = doll.Bodies[i];
            body.Transform = place * _startBody[i];
            body.CollisionMask = Ragdoll.WorldLayer;
            body.Freeze = false;
            body.Sleeping = false;
            uint seed = d.Tick + (uint)i * 7u + 1u;
            var scatter = new Vector3(Signed(d.Id, seed), Signed(d.Id, seed + 1u), Signed(d.Id, seed + 2u));
            body.LinearVelocity = toWorld * (velocity + scatter * LimbScatter);
            body.AngularVelocity = toWorld * (new Vector3(Signed(d.Id, seed + 3u), Signed(d.Id, seed + 4u), Signed(d.Id, seed + 5u)) * LimbSpin);
        }
        Wear(doll, look);
        doll.InUse = true; doll.Frozen = false; doll.Age = 0f; doll.Still = 0f; doll.Lying = 0f; doll.Scale = look.Scale; doll.Serial = ++_serial;
        doll.StartZ = d.Position.Z;
        doll.Root.Visible = true;
        Sync(doll, 0f);
        Thrown++;
        return true;
    }

    static float Signed(ushort id, uint tick) => GoreRules.Roll(id, tick) * 2f - 1f;

    static void Wear(Doll doll, MusouLooks.Look look)
    {
        if (!doll.Worn.TryGetValue(look, out var meshes))
        {
            meshes = new Node3D { Name = look.Name };
            foreach (var piece in look.Assets.Pieces)
            {
                var mesh = new MeshInstance3D { Name = piece.Name, Mesh = piece.Mesh, Skin = piece.Skin, Skeleton = new NodePath("../.."), ExtraCullMargin = 2f };
                for (int i = 0; i < piece.Overrides.Count; i++) if (piece.Overrides[i] is { } m) mesh.SetSurfaceOverrideMaterial(i, m);
                meshes.AddChild(mesh);
            }
            doll.Skeleton.AddChild(meshes);
            doll.Worn[look] = meshes;
        }
        if (doll.Showing is not null) doll.Showing.Visible = false;
        meshes.Visible = true;
        doll.Showing = meshes;
    }

    /// <summary>Puts the dolls in use on the screen and ends those that have lain long enough.</summary>
    public void Frame(float delta, float sinkSeconds, float sinkDepth)
    {
        foreach (var doll in _dolls)
        {
            if (!doll.InUse) continue;
            doll.Age += delta;
            if (doll.Frozen)
            {
                doll.Lying += delta;
                if (doll.Lying >= LieSeconds + sinkSeconds) { Release(doll); continue; }
                if (doll.Lying > LieSeconds) Place(doll, (doll.Lying - LieSeconds) / sinkSeconds * sinkDepth);
                continue;
            }
            Sync(doll, 0f);
            float linear = 0f, angular = 0f;
            foreach (var b in doll.Bodies)
            {
                linear = MathF.Max(linear, b.LinearVelocity.Length());
                angular = MathF.Max(angular, b.AngularVelocity.Length());
            }
            doll.Still = linear < StillLinear && angular < StillAngular ? doll.Still + delta : 0f;
            var chest = doll.Bodies[_def.FocusBody];
            Highest = MathF.Max(Highest, chest.Transform.Origin.Z - doll.StartZ);
            if (chest.GlobalPosition.Y < Ragdoll.LostBelowY) { Release(doll); continue; }
            if (doll.Still >= StillSeconds || doll.Age >= MaxPhysicsSeconds) Settle(doll);
        }
    }

    static void Settle(Doll doll)
    {
        doll.Frozen = true;
        foreach (var b in doll.Bodies) { b.CollisionMask = 0; b.Freeze = true; }
    }

    static void Release(Doll doll)
    {
        Settle(doll);
        doll.InUse = false;
        doll.Root.Visible = false;
    }

    void Place(Doll doll, float sink)
    {
        var chest = doll.Bodies[_def.FocusBody].Transform.Origin;
        doll.Root.Transform = new Transform3D(Basis.FromScale(Vector3.One * doll.Scale), chest - new Vector3(0f, 0f, sink));
    }

    /// <summary>Writes the bodies' transforms into the skeleton, as <see cref="Ragdoll"/> does; the skeleton's origin is the chest, so a look's scale shrinks or grows the Ork round it.</summary>
    void Sync(Doll doll, float sink)
    {
        Place(doll, sink);
        var toSkeleton = new Transform3D(Basis.Identity, -doll.Bodies[_def.FocusBody].Transform.Origin);
        for (int i = 0; i < _now.Length; i++) _now[i] = toSkeleton * doll.Bodies[i].Transform;
        var skeleton = doll.Skeleton;
        int bones = skeleton.GetBoneCount();
        for (int b = 0; b < bones; b++)
        {
            if (_def.DriveOfBone[b] is { } drive) { skeleton.SetBoneGlobalPose(b, _now[drive.Body] * drive.Local); continue; }
            foreach (var chain in _def.Chains)
            {
                int k = Array.IndexOf(chain.Bones, b);
                if (k < 0) continue;
                var fromA = _now[chain.BodyA] * _startBody[chain.BodyA].AffineInverse() * _startBone[b];
                var fromB = _now[chain.BodyB] * _startBody[chain.BodyB].AffineInverse() * _startBone[b];
                var q = fromA.Basis.GetRotationQuaternion().Slerp(fromB.Basis.GetRotationQuaternion(), chain.Fraction[k]);
                skeleton.SetBoneGlobalPose(b, new Transform3D(new Basis(q), fromA.Origin.Lerp(fromB.Origin, chain.Fraction[k])));
                break;
            }
        }
    }
}
