using System;
using Godot;

namespace SmClient;

/// <summary>
/// Stand-in models for the lightning claw and the power spear (added content, Musou Mode; **placeholder demonstration** built from primitives, no install asset): the claw follows the
/// right forearm and hand, the spear the line through both hands, so the stand-ins sit in the hands whatever pose the strike puts them in. Each is a child of the character's
/// skeleton and is placed in skeleton space every frame (<see cref="Follow"/>). Sizes from the owner's reference images as the design describes them (docs/new-weapons-design.md
/// sections 5 and 6): the talons about 1.2 times the forearm, the shaft about 1.3 times the marine's height with a blade about a third of it.
/// </summary>
public abstract partial class WeaponStandIn : Node3D
{
    /// <summary>0 to 1: the power field's glow (the view raises it round the hit).</summary>
    public float Glow { get; set; }

    public abstract void Follow(Skeleton3D s);

    protected static StandardMaterial3D Metal(Color c, float metallic = 0.9f, float roughness = 0.35f) => new() { AlbedoColor = c, Metallic = metallic, Roughness = roughness };

    protected static MeshInstance3D Box(Node3D parent, Vector3 size, Vector3 at, Material m)
    {
        var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, Position = at, MaterialOverride = m };
        parent.AddChild(mi);
        return mi;
    }

    /// <summary>A cylinder along +Y of the parent, its base at <paramref name="from"/>.</summary>
    protected static MeshInstance3D Rod(Node3D parent, float radiusBottom, float radiusTop, float length, Vector3 from, Material m)
    {
        var mi = new MeshInstance3D { Mesh = new CylinderMesh { BottomRadius = radiusBottom, TopRadius = radiusTop, Height = length, RadialSegments = 12 }, Position = from + new Vector3(0f, length * 0.5f, 0f), MaterialOverride = m };
        parent.AddChild(mi);
        return mi;
    }

    /// <summary>A frame whose Y axis is <paramref name="along"/> (unit), with <paramref name="up"/> as the Z axis's hint.</summary>
    protected static Basis Frame(Vector3 along, Vector3 up)
    {
        var z = (up - along * up.Dot(along));
        z = z.LengthSquared() > 1e-8f ? z.Normalized() : (MathF.Abs(along.Z) < 0.9f ? new Vector3(0f, 0f, 1f) : new Vector3(1f, 0f, 0f)).Cross(along).Normalized();
        var x = along.Cross(z).Normalized();
        return new Basis(x, along, z);
    }
}

/// <summary>
/// The claw: a bulky piston-backed gauntlet over the right forearm and hand with brass bands, and four long curved steel talons from the knuckles, each about 1.2 times the forearm,
/// curving a little toward the palm (the owner's reference). A faint blue power field on the talons while <see cref="WeaponStandIn.Glow"/> is up (ours: the references show none).
/// </summary>
public partial class ClawStandIn : WeaponStandIn
{
    int _fore = -1, _hand = -1, _f1 = -1, _f2 = -1, _f11 = -1, _f21 = -1;
    Vector3 _palmAxis, _fingerAxis;
    bool _resolved, _built;
    StandardMaterial3D? _talon;
    readonly Node3D _root = new() { Name = "ClawRoot" };

    public override void _Ready() { AddChild(_root); }

    public override void Follow(Skeleton3D s)
    {
        if (!_resolved)
        {
            _resolved = true;
            _fore = s.FindBone("RIG_R_Forearm"); _hand = s.FindBone("RIG_R_Hand");
            _f1 = s.FindBone("RIG_R_Finger1"); _f2 = s.FindBone("RIG_R_Finger2"); _f11 = s.FindBone("RIG_R_Finger11"); _f21 = s.FindBone("RIG_R_Finger21");
            if (_hand >= 0 && _f1 >= 0 && _f2 >= 0)
            {
                // The hand's frame from the bind pose (as the Librarian's cast pose reads it): the fingers along the mean of the index and ring roots; the palm on the side the tips curl toward.
                var i1 = s.GetBoneRest(_f1).Origin; var i2 = s.GetBoneRest(_f2).Origin;
                _fingerAxis = ((i1 + i2) * 0.5f).Normalized();
                var across = i1 - i2; across = (across - _fingerAxis * across.Dot(_fingerAxis)).Normalized();
                _palmAxis = _fingerAxis.Cross(across).Normalized();
                var curl = Vector3.Zero;
                foreach (var (root, tip) in new[] { (_f1, _f11), (_f2, _f21) })
                {
                    if (tip < 0) continue;
                    var rootRest = s.GetBoneRest(root);
                    var run = rootRest.Origin.Normalized();
                    var d = (rootRest.Basis * s.GetBoneRest(tip).Origin).Normalized();
                    curl += d - run * d.Dot(run);
                }
                if (curl.LengthSquared() > 1e-6f && _palmAxis.Dot(curl) < 0f) _palmAxis = -_palmAxis;
            }
            else { _fingerAxis = new Vector3(1f, 0f, 0f); _palmAxis = new Vector3(0f, 0f, -1f); }
        }
        if (_hand < 0 || _fore < 0) return;
        var hand = s.GetBoneGlobalPose(_hand); var fore = s.GetBoneGlobalPose(_fore);
        float forearm = MathF.Max(0.2f, fore.Origin.DistanceTo(hand.Origin));
        if (!_built) { _built = true; Build(forearm); }
        var along = (hand.Basis * _fingerAxis).Normalized();
        var palm = (hand.Basis * _palmAxis).Normalized();
        _root.Transform = new Transform3D(Frame(along, -palm), hand.Origin);   // +Y along the fingers, +Z the back of the hand
        if (_talon is not null) { _talon.EmissionEnabled = Glow > 0.02f; _talon.EmissionEnergyMultiplier = 2.5f * Glow; }
    }

    void Build(float forearm)
    {
        var steel = Metal(new Color(0.42f, 0.44f, 0.47f));
        var dark = Metal(new Color(0.16f, 0.17f, 0.19f), 0.7f, 0.5f);
        var brass = Metal(new Color(0.72f, 0.52f, 0.2f), 0.95f, 0.3f);
        _talon = Metal(new Color(0.78f, 0.8f, 0.84f), 1f, 0.2f);
        _talon.Emission = new Color(0.3f, 0.55f, 1f);
        // The gauntlet: a bulky block over the forearm and the back of the hand, its long axis along the fingers (+Y), the wrist end behind the hand's origin.
        Box(_root, new Vector3(0.17f, forearm * 0.95f, 0.15f), new Vector3(0f, -forearm * 0.33f, 0.035f), dark);
        Box(_root, new Vector3(0.19f, 0.07f, 0.17f), new Vector3(0f, 0.02f, 0.035f), steel);   // the knuckle plate
        Box(_root, new Vector3(0.2f, 0.035f, 0.18f), new Vector3(0f, -forearm * 0.12f, 0.03f), brass);   // brass band at the wrist
        Box(_root, new Vector3(0.2f, 0.035f, 0.18f), new Vector3(0f, -forearm * 0.62f, 0.03f), brass);   // brass band at the elbow end
        for (int i = -1; i <= 1; i++)   // three pistons on the back of the gauntlet
            Rod(_root, 0.014f, 0.014f, forearm * 0.55f, new Vector3(i * 0.05f, -forearm * 0.68f, 0.115f), steel);
        // Four talons from the knuckles, 1.2 forearm long, tapering, bent toward the palm (-Z) in two segments.
        float talon = forearm * 1.2f;
        float[] xs = [-0.066f, -0.022f, 0.022f, 0.066f];
        foreach (float x in xs)
        {
            var seg1 = new Node3D { Position = new Vector3(x, 0.05f, 0.0f), Rotation = new Vector3(Mathf.DegToRad(-6f), 0f, 0f) };
            _root.AddChild(seg1);
            Rod(seg1, 0.019f, 0.012f, talon * 0.55f, Vector3.Zero, _talon);
            var seg2 = new Node3D { Position = new Vector3(0f, talon * 0.55f, 0f), Rotation = new Vector3(Mathf.DegToRad(-14f), 0f, 0f) };
            seg1.AddChild(seg2);
            Rod(seg2, 0.012f, 0.002f, talon * 0.45f, Vector3.Zero, _talon);
        }
    }
}

/// <summary>
/// The spear: a long black shaft (about 1.3 times the marine's height, 2.8 m), an ornate bronze collar with a small crossguard and a ring near the top, and a long single-edged
/// blade (about 0.9 m) with an angled chisel tip glowing blue, deeper at the base (the owner's reference). The shaft runs through both hands: the right hand is the rear hand near the
/// butt, the left the forward hand, and the blade is beyond it (a polearm's grip; ours).
/// </summary>
public partial class SpearStandIn : WeaponStandIn
{
    int _lHand = -1, _rHand = -1;
    bool _resolved, _built;
    StandardMaterial3D? _field, _fieldBase;
    readonly Node3D _root = new() { Name = "SpearRoot" };
    /// <summary>The shaft's and the blade's lengths (m) and where the rear (right) hand sits from the butt; the forward (left) hand is wherever the pose puts it along the shaft.</summary>
    public const float Shaft = 1.9f, Blade = 0.9f, RearHandFromButt = 0.5f;
    /// <summary>How far the left hand is on the shaft (0 to 1; <see cref="SmContent.Extras.Musou.WeaponPoseClip.GripAt"/>): at 0 the spear is carried in the right hand alone, at 1 it lies on the line through both hands.</summary>
    public float LeftGrip { get; set; } = 1f;
    /// <summary>
    /// Where the right hand holds the shaft (m from the butt; <see cref="SmContent.Extras.Musou.WeaponPoseClip.GripPointAt"/>): the low melee grip, or the high carry toward the blade. A model built for W3 needs the
    /// same two parameters on its mount: the grip point along the shaft and, for the one-handed carry, the shaft's tilt (<see cref="CarryTilt"/>), both driven by the animation.
    /// </summary>
    public float RightHandFromButt { get; set; } = RearHandFromButt;
    /// <summary>
    /// The one-handed carry's tilt: at the low grip the shaft stands upright from the hand; at the carry grip (0.95 m from the butt) it leans forward to this elevation (degrees above the horizontal),
    /// so with the hand hanging about 0.97 m up the butt rides a few centimetres off the ground behind the hand and the blade forward-up over the shoulder (ours, from the owner's rule).
    /// </summary>
    public const float CarryTilt = 70f;

    public override void _Ready() { AddChild(_root); }

    public override void Follow(Skeleton3D s)
    {
        if (!_resolved) { _resolved = true; _lHand = s.FindBone("RIG_L_Hand"); _rHand = s.FindBone("RIG_R_Hand"); }
        if (_lHand < 0 || _rHand < 0) return;
        if (!_built) { _built = true; Build(); }
        var l = s.GetBoneGlobalPose(_lHand).Origin; var r = s.GetBoneGlobalPose(_rHand).Origin;
        var d = l - r;   // the shaft from the rear (right) hand toward the forward (left) hand and the blade
        if (d.LengthSquared() < 1e-4f) d = new Vector3(1f, 0f, 0f);
        d = d.Normalized();
        float grip = Math.Clamp(LeftGrip, 0f, 1f);
        if (grip < 1f)
        {
            // Carried in the right hand alone: upright at the low grip, tilted forward-up at the high carry grip (the body's front read from the hips); the left hand's hold brings it onto the two-hand line.
            float high = Math.Clamp((RightHandFromButt - RearHandFromButt) / (SmContent.Extras.Musou.WeaponPoseClip.CarryGrip - RearHandFromButt), 0f, 1f);
            float el = Mathf.DegToRad(Mathf.Lerp(90f, CarryTilt, high));
            var front = CarryFront(s);
            var carry = (front * MathF.Cos(el) + new Vector3(0f, 0f, 1f) * MathF.Sin(el)).Normalized();
            d = carry.Lerp(d, grip).Normalized();
        }
        var butt = r - d * RightHandFromButt;
        _root.Transform = new Transform3D(Frame(d, new Vector3(0f, 0f, 1f)), butt);
        if (_field is not null)
        {
            float e = 0.8f + 2.2f * Glow;
            _field.EmissionEnergyMultiplier = e; if (_fieldBase is not null) _fieldBase.EmissionEnergyMultiplier = e * 1.3f;
        }
    }

    int _lThigh = -2, _rThigh = -2;

    /// <summary>The body's flat front from the hips (skeleton space), for the carry's tilt.</summary>
    Vector3 CarryFront(Skeleton3D s)
    {
        if (_lThigh == -2) { _lThigh = s.FindBone("RIG_L_Thigh"); _rThigh = s.FindBone("RIG_R_Thigh"); }
        if (_lThigh < 0 || _rThigh < 0) return new Vector3(1f, 0f, 0f);
        var across = s.GetBoneGlobalPose(_rThigh).Origin - s.GetBoneGlobalPose(_lThigh).Origin;
        across.Z = 0f;
        return across.LengthSquared() > 1e-6f ? new Vector3(0f, 0f, 1f).Cross(across.Normalized()).Normalized() : new Vector3(1f, 0f, 0f);
    }

    void Build()
    {
        var black = Metal(new Color(0.06f, 0.06f, 0.07f), 0.3f, 0.55f);
        var bronze = Metal(new Color(0.66f, 0.45f, 0.18f), 0.95f, 0.3f);
        _field = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.72f, 1f, 0.8f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, EmissionEnabled = true, Emission = new Color(0.25f, 0.55f, 1f), EmissionEnergyMultiplier = 0.8f, Roughness = 0.2f };
        _fieldBase = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.4f, 1f, 0.9f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, EmissionEnabled = true, Emission = new Color(0.1f, 0.3f, 1f), EmissionEnergyMultiplier = 1.0f, Roughness = 0.2f };
        Rod(_root, 0.032f, 0.032f, Shaft, Vector3.Zero, black);
        Rod(_root, 0.055f, 0.04f, 0.1f, new Vector3(0f, 0f, 0f), bronze);   // the butt cap
        // The fitting near the top: the collar, a small crossguard and a ring.
        Rod(_root, 0.06f, 0.05f, 0.18f, new Vector3(0f, Shaft - 0.22f, 0f), bronze);
        Box(_root, new Vector3(0.26f, 0.035f, 0.05f), new Vector3(0f, Shaft - 0.04f, 0f), bronze);
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.04f, OuterRadius = 0.06f, Rings = 24, RingSegments = 10 }, Position = new Vector3(0.13f, Shaft - 0.12f, 0f), MaterialOverride = bronze };
        ring.RotationDegrees = new Vector3(0f, 0f, 90f);
        _root.AddChild(ring);
        // The blade: a long single-edged flat, a deeper core along its base third, and an angled chisel tip; the edge on +X.
        float straight = Blade * 0.82f;
        Box(_root, new Vector3(0.14f, straight, 0.03f), new Vector3(0.025f, Shaft + straight * 0.5f, 0f), _field);
        Box(_root, new Vector3(0.06f, Blade * 0.4f, 0.036f), new Vector3(-0.025f, Shaft + Blade * 0.2f, 0f), _fieldBase);
        var tip = new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(0.14f, Blade - straight, 0.03f), LeftToRight = 1f }, Position = new Vector3(0.025f, Shaft + straight + (Blade - straight) * 0.5f, 0f), MaterialOverride = _field };
        _root.AddChild(tip);
    }
}
