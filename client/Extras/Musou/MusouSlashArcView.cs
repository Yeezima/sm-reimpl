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
/// (the stencil twins of <see cref="MusouSlashMark"/>; Forward+ and Mobile only). The Compatibility renderer has no stencil: there no twins are made, another player's arc draws over every model
/// (his own too), and the own player's arcs draw depth tested with every vertex pulled <see cref="MusouSlashArcRules.CompatPullMetres"/> toward the eye along its sight line (the same place on
/// the screen): the own model in front hides them, the Orks they run through do not, a wall or model farther than that in front of the line does. Positions are Godot world space. Client only.
/// </summary>
public partial class MusouSlashArcView : Node3D
{
    const string ShaderCode = """
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, //DEPTH shadows_disabled;
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

    /// <summary>Whether the renderer has a stencil buffer (not the Compatibility renderer): the striker's model hides its arcs through stencil twins.</summary>
    static readonly bool Stencil = !SafeMode.Compatibility;

    /// <summary>The group of the own player's arcs in the Compatibility renderer: depth tested, pulled toward the eye (no stencil there).</summary>
    const int OwnDepthTested = -1;

    /// <summary>One arc: its centre at the line's height (the swinger's feet raised), the level forward and left axes of the swing, the cleave's shape and the line's look.</summary>
    struct Arc
    {
        public bool Live;
        public Vector3 Centre, Forward, Left;
        public MusouCleaveShape.Shape Shape;
        public float Radius, Width, Age;
        public int Direction;
        public Color Colour;
        /// <summary>The line's points as offsets from <see cref="Centre"/> (the weapon tip's path scaled to the cleave radius, <see cref="MusouSlashArcPath"/>), or null: the level circle segment.</summary>
        public Vector3[]? Offsets;
    }

    /// <summary>
    /// The arcs of one attacker's stencil reference (0: no model hides them; <see cref="OwnDepthTested"/>: the own player's in the Compatibility renderer): their pool, material and mesh, how many
    /// are alive and whether the mesh holds a surface from the last frame.
    /// </summary>
    sealed class Group
    {
        public readonly Arc[] Arcs = new Arc[MusouSlashArcRules.Pool];
        public readonly ImmediateMesh Mesh = new();
        public readonly ShaderMaterial Material;
        public readonly bool DepthTested;
        public int Next, Alive;
        public bool Drew;

        public Group(Node3D parent, int reference)
        {
            DepthTested = reference == OwnDepthTested;
            string code = ShaderCode
                .Replace("//STENCIL", reference > 0 ? $"stencil_mode read, compare_not_equal, {reference};" : "")
                .Replace("//DEPTH", DepthTested ? "" : "depth_test_disabled,");
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
    /// arc where it covers that model (null: nothing hides it). <paramref name="own"/>: the own player's swing (the Compatibility renderer's depth-tested group).
    /// </summary>
    public void Add(Vector3 centre, Vector3 forward, Vector3 left, MusouCleaveShape.Shape shape, float radius, float width, int direction, MusouSlashColour colour, float brightness,
        int attacker = 0, Node3D? attackerModel = null, bool own = false, MusouSlashArcPath.Path? path = null)
    {
        Vector3[]? offsets = null;
        if (path is not null)
        {
            // The weapon tip's path (X forward, Y left, Z up) at the cleave radius, in Godot world space round the centre: the heading and left are level, up is the world's.
            var scaled = MusouSlashArcPath.Scale(path, radius);
            offsets = new Vector3[scaled.Length];
            for (int i = 0; i < scaled.Length; i++) offsets[i] = forward * scaled[i].X + left * scaled[i].Y + Vector3.Up * scaled[i].Z;
        }
        var (r, g, b) = MusouSlashRules.Rgb(colour);
        float bright = brightness > 0f ? brightness : 1f;
        int reference = 0;
        if (!Stencil) reference = own ? OwnDepthTested : 0;   // review: no stencil, so no twins (they would cost a draw of every mesh of the model for nothing)
        else if (attackerModel is not null && GodotObject.IsInstanceValid(attackerModel))
        {
            reference = MusouSlashMark.ReferenceOf(attacker);
            MusouSlashMark.Mark(attackerModel, reference);
            _marked[reference] = (attackerModel, Time.GetTicksMsec() / 1000.0 + MusouSlashMark.RefreshSeconds);
        }
        var group = GroupOf(reference);
        if (!group.Arcs[group.Next].Live) group.Alive++;
        group.Arcs[group.Next] = new Arc
        {
            Live = true, Centre = centre, Forward = forward, Left = left, Shape = shape, Radius = radius, Width = width, Direction = direction, Offsets = offsets,
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
            if (group.Alive == 0 && !group.Drew) continue;   // review: nothing to clear and nothing to draw (most groups, most frames)
            if (group.Drew) group.Mesh.ClearSurfaces();
            bool open = false;
            int n = 0;
            float pull = group.DepthTested ? MusouSlashArcRules.CompatPullMetres : 0f;
            for (int i = 0; i < group.Arcs.Length; i++)
            {
                ref var a = ref group.Arcs[i];
                if (!a.Live) continue;
                a.Age += (float)delta;
                if (a.Age >= MusouSlashArcRules.Lifetime) { a.Live = false; group.Alive--; continue; }
                if (!open) { group.Mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, group.Material); open = true; }
                Emit(group.Mesh, a, eye, pull);
                n++;
            }
            if (open) group.Mesh.SurfaceEnd();
            group.Drew = open;
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

    /// <summary>
    /// The arc's ribbon: two vertices a segment, the width across the curve and the view so the line faces the camera. <paramref name="pull"/> above 0 (the Compatibility renderer's own arcs):
    /// every vertex moved that many metres toward the eye along its sight line, which keeps its place on the screen and only brings its depth nearer.
    /// </summary>
    static void Emit(ImmediateMesh mesh, in Arc a, Vector3 eye, float pull)
    {
        var offsets = a.Offsets;
        int segments = offsets is null ? MusouSlashArcRules.Segments(a.Shape) : offsets.Length - 1;
        float head = MusouSlashArcRules.Head(a.Age), fade = MusouSlashArcRules.Fade(a.Age), half = a.Width * 0.5f;
        Vector3 prevOuter = default, prevInner = default;
        float prevAlong = 0f;
        for (int k = 0; k <= segments; k++)
        {
            float along = k / (float)segments;
            Vector3 point, tangent;
            if (offsets is null)
            {
                float angle = MusouSlashArcRules.AngleAt(along, a.Shape, a.Direction);
                var radial = a.Forward * MathF.Cos(angle) + a.Left * MathF.Sin(angle);
                point = a.Centre + radial * a.Radius;
                tangent = -a.Forward * MathF.Sin(angle) + a.Left * MathF.Cos(angle);
            }
            else
            {
                point = a.Centre + offsets[k];
                tangent = offsets[Math.Min(k + 1, segments)] - offsets[Math.Max(k - 1, 0)];
            }
            var side = tangent.Cross(eye - point);
            side = side.LengthSquared() < 1e-8f ? Vector3.Up : side.Normalized();
            var outer = point + side * half;
            var inner = point - side * half;
            if (pull > 0f) { outer = Toward(outer, eye, pull); inner = Toward(inner, eye, pull); }
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

    /// <summary><paramref name="at"/> moved <paramref name="metres"/> toward <paramref name="eye"/> (never nearer to it than 0.1 m).</summary>
    static Vector3 Toward(Vector3 at, Vector3 eye, float metres)
    {
        var sight = at - eye;
        float distance = sight.Length();
        return distance > 1e-4f ? eye + sight * (MathF.Max(distance - metres, 0.1f) / distance) : at;
    }

    static void Vertex(ImmediateMesh mesh, Vector3 at, float along, float across, float head, float fade, Color colour)
    {
        mesh.SurfaceSetColor(colour);
        mesh.SurfaceSetUV(new Vector2(along, across));
        mesh.SurfaceSetUV2(new Vector2(head, fade));
        mesh.SurfaceAddVertex(at);
    }
}
