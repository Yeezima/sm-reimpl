using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;

namespace SmClient;

/// <summary>
/// Musou Mode's melee slash (owner, 2026-11-08: "bring back the slashing visual effect that would appear over enemies that were hit by player melee attacks. make the effect red for chain
/// weapons and the combat knife, blue for power weapons"). A glowing curved stroke drawn across the enemy that was hit, facing the camera, sweeping in over the first 0.12 s and fading by the
/// end. It is its own view (one <see cref="MultiMeshInstance3D"/> per attacker group, no texture, no dependence on the gore, so the original Ork blood or <c>--no-musou-gore</c> do not hide it); the dark gore
/// it replaces drew the same stroke as black ink. Positions are Godot world space. All sizes and colours are ours (<see cref="MusouSlashRules"/>).
/// <para>Depth (owner, 2026-11-09: "the on hit blue or red slash effect should appear over any model in the game except the player model that initiated it"): the strokes have no depth test, so they draw over
/// every model and over the level (an Ork's body, a Guardsman, an ally, a corpse), never inside or behind one. The striking player's own model still hides them: its meshes write a stencil mark
/// (<see cref="MusouSlashMark"/>, an invisible twin of each mesh) and that attacker's strokes (one <see cref="Group"/> per attacker) are not drawn where the mark is. Stencil is Forward+ and Mobile
/// only (experimental in Godot): in the Compatibility renderer the strokes also draw over the attacker's own model.</para>
/// </summary>
public partial class MusouSlashView : Node3D
{
    const string ShaderCode = """
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, depth_test_disabled;
//STENCIL
varying vec4 cd;
void vertex() { cd = INSTANCE_CUSTOM; }
void fragment() {
    // x along the stroke (0 start .. 1 end), y across it; a crescent: thick in the middle, pointed at both ends, bowed a little.
    float x = UV.x;
    float bow = 0.5 + 0.12 * sin(3.14159 * x) * cd.z;
    float half_w = pow(sin(3.14159 * clamp(x, 0.0, 1.0)), 0.7) * 0.3;
    float d = abs(UV.y - bow);
    float body = smoothstep(half_w, half_w * 0.25, d);
    float core = smoothstep(half_w * 0.45, 0.0, d);
    float reveal = smoothstep(cd.x, cd.x - 0.12, x);
    // The bright head only inside the crescent (it once lit the whole quad: the grey gradient boxes with stair-stepped edges, owner 2026-11-09), fading out before the quad edge.
    float head = smoothstep(0.25, 0.0, cd.x - x) * smoothstep(cd.x, cd.x - 0.06, x) * smoothstep(half_w * 1.1, half_w * 0.3, d);
    float edge_fade = smoothstep(0.0, 0.08, x) * smoothstep(1.0, 0.92, x) * smoothstep(0.5, 0.46, abs(UV.y - 0.5));
    float a = (body * 0.8 + core * 0.9 + head * 0.6) * reveal * cd.y * edge_fade;
    ALBEDO = COLOR.rgb * (0.65 + core * 0.9 + head * 0.6);
    ALPHA = clamp(a, 0.0, 1.0);
}
""";

    struct Slash
    {
        public bool Live;
        public Vector3 Pos, Axis, Up, Normal;
        public float Length, Age, Bow, Seconds, Base, Width;
        public Color Colour;
    }

    /// <summary>The strokes of one attacker (reference 0: no model hides them): its own pool, material (with the stencil test) and multimesh.</summary>
    sealed class Group
    {
        public readonly Slash[] Slashes = new Slash[MusouSlashRules.Pool];
        public readonly MultiMesh Mesh;
        public int Next;

        public Group(Node3D parent, int reference)
        {
            string code = ShaderCode.Replace("//STENCIL", reference > 0 ? $"stencil_mode read, compare_not_equal, {reference};" : "");
            var material = new ShaderMaterial { Shader = new Shader { Code = code }, RenderPriority = 5 };
            Mesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = new QuadMesh { Size = Vector2.One, Material = material }, InstanceCount = MusouSlashRules.Pool };
            Mesh.VisibleInstanceCount = 0;
            parent.AddChild(new MultiMeshInstance3D
            {
                Name = "Quads" + reference, Multimesh = Mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
                CustomAabb = new Aabb(new Vector3(-4000f, -4000f, -4000f), new Vector3(8000f, 8000f, 8000f)),
            });
        }
    }

    readonly Dictionary<int, Group> _groups = new();
    readonly Dictionary<int, (Node3D Model, double Next)> _marked = new();
    /// <summary>Slashes started since the view was made, and those live now.</summary>
    public int Started, Live;

    public MusouSlashView()
    {
        Name = "MusouSlashes";
        GroupOf(0);
    }

    Group GroupOf(int reference)
    {
        if (!_groups.TryGetValue(reference, out var group)) _groups[reference] = group = new Group(this, reference);
        return group;
    }

    /// <summary>Starts a slash across the enemy whose chest is at <paramref name="at"/>, struck from <paramref name="from"/>, seen from <paramref name="camera"/> (all world space).</summary>
    public void Add(Vector3 at, Vector3 from, Vector3 camera, MusouSlashColour colour, RandomNumberGenerator rng) => Add(at, from, camera, colour, rng, MusouContactRules.DefaultSlash);

    /// <summary>
    /// The same with the weapon's stroke style (combat feel, 2026-11-09: the power sword's faster, thinner, brighter stroke; <see cref="MusouContactRules.SlashOf"/>). <paramref name="attackerModel"/>
    /// (with the attacker's id) is the striking player's model, whose own meshes hide the stroke (null: nothing hides it).
    /// </summary>
    public void Add(Vector3 at, Vector3 from, Vector3 camera, MusouSlashColour colour, RandomNumberGenerator rng, MusouSlashStyle style, int attacker = 0, Node3D? attackerModel = null)
    {
        var view = (camera - at).Normalized();
        if (view.LengthSquared() < 0.5f) view = Vector3.Back;
        var right = Vector3.Up.Cross(view);
        right = right.LengthSquared() < 1e-6f ? Vector3.Right : right.Normalized();
        var up = view.Cross(right).Normalized();
        // A diagonal stroke from the side the blow came from, 25 to 60 degrees off the horizontal, falling toward the far side.
        float side = (at - from).Dot(right) >= 0f ? 1f : -1f;
        float angle = rng.RandfRange(0.45f, 1.05f) * (rng.Randf() < 0.5f ? 1f : -1f);
        var axis = (right * MathF.Cos(angle) * side + up * MathF.Sin(angle)).Normalized();
        var (r, g, b) = MusouSlashRules.Rgb(colour);
        float bright = style.Brightness > 0f ? style.Brightness : 1f;
        r *= bright; g *= bright; b *= bright;
        int reference = attackerModel is not null && GodotObject.IsInstanceValid(attackerModel) ? MusouSlashMark.ReferenceOf(attacker) : 0;
        if (reference > 0)
        {
            MusouSlashMark.Mark(attackerModel!, reference);
            _marked[reference] = (attackerModel!, Time.GetTicksMsec() / 1000.0 + MusouSlashMark.RefreshSeconds);
        }
        var group = GroupOf(reference);
        float life = style.Seconds > 0f ? style.Seconds : MusouSlashRules.Seconds;
        group.Slashes[group.Next] = new Slash
        {
            Live = true, Pos = at + view * 0.45f, Axis = axis, Up = view.Cross(axis).Normalized(), Normal = view,
            Length = rng.RandfRange(style.LengthMin, style.LengthMax), Base = life, Seconds = MusouSlashRules.Lifetime(life), Width = style.Width > 0f ? style.Width : MusouSlashRules.Width,
            Bow = rng.Randf() < 0.5f ? 1f : -1f, Colour = new Color(r, g, b, 1f),
        };
        group.Next = (group.Next + 1) % group.Slashes.Length;
        Started++;
    }

    public override void _Process(double delta)
    {
        int total = 0;
        double now = Time.GetTicksMsec() / 1000.0;
        foreach (var (reference, group) in _groups)
        {
            int n = 0;
            for (int i = 0; i < group.Slashes.Length; i++)
            {
                ref var s = ref group.Slashes[i];
                if (!s.Live) continue;
                s.Age += (float)delta;
                if (s.Age >= s.Seconds) { s.Live = false; continue; }
                float reveal = MusouSlashRules.Reveal(s.Age, s.Base);   // the head runs a little past the end, so the whole stroke is lit before it fades
                float fade = MusouSlashRules.Fade(s.Age, s.Base);       // owner 2026-11-09: one second longer, the tail eased out
                var basis = new Basis(s.Axis * s.Length, s.Up * s.Width * (s.Length / 1.8f) * 2.2f, s.Normal);
                group.Mesh.SetInstanceTransform(n, new Transform3D(basis, s.Pos));
                group.Mesh.SetInstanceColor(n, s.Colour);
                group.Mesh.SetInstanceCustomData(n, new Color(reveal, fade, s.Bow, 0f));
                n++;
            }
            if (group.Mesh.VisibleInstanceCount != n) group.Mesh.VisibleInstanceCount = n;
            total += n;
            // The attacker's model gains and loses meshes (a weapon drawn, a look change): keep its stencil twins current while its strokes live.
            if (reference > 0 && n > 0 && _marked.TryGetValue(reference, out var m) && now >= m.Next)
            {
                if (GodotObject.IsInstanceValid(m.Model)) MusouSlashMark.Mark(m.Model, reference);
                _marked[reference] = (m.Model, now + MusouSlashMark.RefreshSeconds);
            }
        }
        Live = total;
    }
}
