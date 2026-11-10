using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Musou Mode's swing arc (owner, 2026-11-10; <see cref="MusouSlashArcRules"/>): one thin glowing line along each melee swing's cleave, a circle segment round the swinger, level, at the chest height
/// of the enemies struck. It is built every frame as a ribbon that faces the camera along the curve (the line stays visible from any view while its path stays flat). The line sweeps from one end
/// to the other in the swing's direction, then fades. Like the strokes it replaces (<see cref="MusouSlashView"/>) it has no depth test and draws over every model except the striking player's own
/// (the stencil twins of <see cref="MusouSlashMark"/>; Forward+ and Mobile only). Positions are Godot world space. Client only.
/// </summary>
public partial class MusouSlashArcView : Node3D
{
    const string ShaderCode = """
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, depth_test_disabled, shadows_disabled;
//STENCIL
const float ColourRange = 4.0;
void fragment() {
    // UV.x along the arc (0 the swing's start .. 1 its end), UV.y across it (-1 .. 1); UV2.x the sweep's head, UV2.y the fade.
    float x = UV.x;
    float head = UV2.x;
    float taper = max(pow(sin(3.14159 * clamp(x, 0.0, 1.0)), 0.55), 0.03);   // a tear: thickest in the middle, pointed at both ends
    float d = abs(UV.y) / taper;
    float body = smoothstep(1.0, 0.3, d);
    float core = smoothstep(0.4, 0.0, d);
    float lit = smoothstep(head, head - 0.035, x);                           // only the part the sweep has passed
    float glow = smoothstep(0.3, 0.0, head - x) * lit;                       // brighter near the head
    float a = (body * 0.7 + core * 0.9) * (0.6 + 0.4 * glow) * lit * UV2.y;
    ALBEDO = COLOR.rgb * ColourRange * (0.7 + core * 0.8 + glow * 0.5);   // the colour comes a quarter strength (vertex colours may be 8 bits: no value over 1)
    ALPHA = clamp(a, 0.0, 1.0);
}
""";

    /// <summary>The vertex colour is the arc's colour divided by this; the shader multiplies it back (colours over 1 survive an 8-bit vertex format).</summary>
    const float ColourRange = 4f;

    /// <summary>One arc: its centre at the line's height (the swinger's feet raised), the level forward and left axes of the swing, the cleave's shape and the line's look.</summary>
    struct Arc
    {
        public bool Live;
        public Vector3 Centre, Forward, Left;
        public MusouCleaveShape.Shape Shape;
        public float Radius, Width, Age;
        public int Direction;
        public Color Colour;
    }

    /// <summary>The arcs of one attacker's stencil reference (0: no model hides them): their pool, material and mesh.</summary>
    sealed class Group
    {
        public readonly Arc[] Arcs = new Arc[MusouSlashArcRules.Pool];
        public readonly ImmediateMesh Mesh = new();
        public readonly ShaderMaterial Material;
        public int Next;

        public Group(Node3D parent, int reference)
        {
            string code = ShaderCode.Replace("//STENCIL", reference > 0 ? $"stencil_mode read, compare_not_equal, {reference};" : "");
            Material = new ShaderMaterial { Shader = new Shader { Code = code }, RenderPriority = 5 };
            parent.AddChild(new MeshInstance3D
            {
                Name = "Arcs" + reference, Mesh = Mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
                CustomAabb = new Aabb(new Vector3(-4000f, -4000f, -4000f), new Vector3(8000f, 8000f, 8000f)),
            });
        }
    }

    readonly Dictionary<int, Group> _groups = new();
    readonly Dictionary<int, (Node3D Model, double Next)> _marked = new();

    /// <summary>Arcs started since the view was made, and those alive now.</summary>
    public int Started, Live;

    public MusouSlashArcView()
    {
        Name = "MusouSlashArcs";
        GroupOf(0);
    }

    Group GroupOf(int reference)
    {
        if (!_groups.TryGetValue(reference, out var group)) _groups[reference] = group = new Group(this, reference);
        return group;
    }

    /// <summary>
    /// Starts an arc round <paramref name="centre"/> (the swinger's feet raised to the line's height) along the level unit <paramref name="forward"/> (the swing's heading) with
    /// <paramref name="left"/> to its left, for a cleave of <paramref name="shape"/> drawn at <paramref name="radius"/>. <paramref name="attackerModel"/> (with the attacker's id) hides the
    /// arc where it covers that model (null: nothing hides it).
    /// </summary>
    public void Add(Vector3 centre, Vector3 forward, Vector3 left, MusouCleaveShape.Shape shape, float radius, float width, int direction, MusouSlashColour colour, float brightness,
        int attacker = 0, Node3D? attackerModel = null)
    {
        var (r, g, b) = MusouSlashRules.Rgb(colour);
        float bright = brightness > 0f ? brightness : 1f;
        int reference = attackerModel is not null && GodotObject.IsInstanceValid(attackerModel) ? MusouSlashMark.ReferenceOf(attacker) : 0;
        if (reference > 0)
        {
            MusouSlashMark.Mark(attackerModel!, reference);
            _marked[reference] = (attackerModel!, Time.GetTicksMsec() / 1000.0 + MusouSlashMark.RefreshSeconds);
        }
        var group = GroupOf(reference);
        group.Arcs[group.Next] = new Arc
        {
            Live = true, Centre = centre, Forward = forward, Left = left, Shape = shape, Radius = radius, Width = width, Direction = direction,
            Colour = new Color(r * bright / ColourRange, g * bright / ColourRange, b * bright / ColourRange, 1f),
        };
        group.Next = (group.Next + 1) % group.Arcs.Length;
        Started++;
    }

    public override void _Process(double delta)
    {
        var camera = GetViewport()?.GetCamera3D();
        var eye = camera?.GlobalPosition ?? Vector3.Zero;
        double now = Time.GetTicksMsec() / 1000.0;
        int total = 0;
        foreach (var (reference, group) in _groups)
        {
            group.Mesh.ClearSurfaces();
            bool open = false;
            int n = 0;
            for (int i = 0; i < group.Arcs.Length; i++)
            {
                ref var a = ref group.Arcs[i];
                if (!a.Live) continue;
                a.Age += (float)delta;
                if (a.Age >= MusouSlashArcRules.Lifetime) { a.Live = false; continue; }
                if (!open) { group.Mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, group.Material); open = true; }
                Emit(group.Mesh, a, eye);
                n++;
            }
            if (open) group.Mesh.SurfaceEnd();
            total += n;
            // The attacker's model gains and loses meshes (a weapon drawn, a look change): keep its stencil twins current while its arcs live.
            if (reference > 0 && n > 0 && _marked.TryGetValue(reference, out var m) && now >= m.Next)
            {
                if (GodotObject.IsInstanceValid(m.Model)) MusouSlashMark.Mark(m.Model, reference);
                _marked[reference] = (m.Model, now + MusouSlashMark.RefreshSeconds);
            }
        }
        Live = total;
    }

    /// <summary>The arc's ribbon: two vertices a segment, the width across the curve and the view so the line faces the camera.</summary>
    static void Emit(ImmediateMesh mesh, in Arc a, Vector3 eye)
    {
        int segments = MusouSlashArcRules.Segments(a.Shape);
        float head = MusouSlashArcRules.Head(a.Age), fade = MusouSlashArcRules.Fade(a.Age), half = a.Width * 0.5f;
        Vector3 prevOuter = default, prevInner = default;
        float prevAlong = 0f;
        for (int k = 0; k <= segments; k++)
        {
            float along = k / (float)segments;
            float angle = MusouSlashArcRules.AngleAt(along, a.Shape, a.Direction);
            var radial = a.Forward * MathF.Cos(angle) + a.Left * MathF.Sin(angle);
            var point = a.Centre + radial * a.Radius;
            var tangent = -a.Forward * MathF.Sin(angle) + a.Left * MathF.Cos(angle);
            var side = tangent.Cross(eye - point);
            side = side.LengthSquared() < 1e-8f ? Vector3.Up : side.Normalized();
            var outer = point + side * half;
            var inner = point - side * half;
            if (k > 0)
            {
                Vertex(mesh, prevOuter, prevAlong, 1f, head, fade, a.Colour);
                Vertex(mesh, prevInner, prevAlong, -1f, head, fade, a.Colour);
                Vertex(mesh, outer, along, 1f, head, fade, a.Colour);
                Vertex(mesh, prevInner, prevAlong, -1f, head, fade, a.Colour);
                Vertex(mesh, inner, along, -1f, head, fade, a.Colour);
                Vertex(mesh, outer, along, 1f, head, fade, a.Colour);
            }
            prevOuter = outer; prevInner = inner; prevAlong = along;
        }
    }

    static void Vertex(ImmediateMesh mesh, Vector3 at, float along, float across, float head, float fade, Color colour)
    {
        mesh.SurfaceSetColor(colour);
        mesh.SurfaceSetUV(new Vector2(along, across));
        mesh.SurfaceSetUV2(new Vector2(head, fade));
        mesh.SurfaceAddVertex(at);
    }
}
