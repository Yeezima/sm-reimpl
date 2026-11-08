using System;
using Godot;
using SmNet.Extras.Musou;

namespace SmClient;

/// <summary>
/// Musou Mode's melee slash (owner, 2026-11-08: "bring back the slashing visual effect that would appear over enemies that were hit by player melee attacks. make the effect red for chain
/// weapons and the combat knife, blue for power weapons"). A glowing curved stroke drawn across the enemy that was hit, facing the camera, sweeping in over the first 0.12 s and fading by the
/// end. It is its own view (one <see cref="MultiMeshInstance3D"/>, no texture, no dependence on the gore, so the original Ork blood or <c>--no-musou-gore</c> do not hide it); the dark gore it replaces
/// drew the same stroke as black ink. Positions are Godot world space. All sizes and colours are ours (<see cref="MusouSlashRules"/>).
/// </summary>
public partial class MusouSlashView : Node3D
{
    const string ShaderCode = """
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never;
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
    float head = smoothstep(0.25, 0.0, cd.x - x) * step(x, cd.x);
    float a = (body * 0.8 + core * 0.9 + head * 0.6) * reveal * cd.y;
    ALBEDO = COLOR.rgb * (0.65 + core * 0.9 + head * 0.6);
    ALPHA = clamp(a, 0.0, 1.0);
}
""";

    struct Slash
    {
        public bool Live;
        public Vector3 Pos, Axis, Up, Normal;
        public float Length, Age, Bow;
        public Color Colour;
    }

    readonly Slash[] _slashes = new Slash[MusouSlashRules.Pool];
    readonly MultiMesh _mm;
    int _next;
    /// <summary>Slashes started since the view was made, and those live now.</summary>
    public int Started, Live;

    public MusouSlashView()
    {
        Name = "MusouSlashes";
        var material = new ShaderMaterial { Shader = new Shader { Code = ShaderCode }, RenderPriority = 5 };
        _mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = new QuadMesh { Size = Vector2.One, Material = material }, InstanceCount = MusouSlashRules.Pool };
        _mm.VisibleInstanceCount = 0;
        AddChild(new MultiMeshInstance3D
        {
            Name = "Quads", Multimesh = _mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            CustomAabb = new Aabb(new Vector3(-4000f, -4000f, -4000f), new Vector3(8000f, 8000f, 8000f)),
        });
    }

    /// <summary>Starts a slash across the enemy whose chest is at <paramref name="at"/>, struck from <paramref name="from"/>, seen from <paramref name="camera"/> (all world space).</summary>
    public void Add(Vector3 at, Vector3 from, Vector3 camera, MusouSlashColour colour, RandomNumberGenerator rng)
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
        _slashes[_next] = new Slash
        {
            Live = true, Pos = at + view * 0.45f, Axis = axis, Up = view.Cross(axis).Normalized(), Normal = view,
            Length = rng.RandfRange(MusouSlashRules.LengthMin, MusouSlashRules.LengthMax), Bow = rng.Randf() < 0.5f ? 1f : -1f, Colour = new Color(r, g, b, 1f),
        };
        _next = (_next + 1) % _slashes.Length;
        Started++;
    }

    public override void _Process(double delta)
    {
        int n = 0;
        for (int i = 0; i < _slashes.Length; i++)
        {
            ref var s = ref _slashes[i];
            if (!s.Live) continue;
            s.Age += (float)delta;
            float t = s.Age / MusouSlashRules.Seconds;
            if (t >= 1f) { s.Live = false; continue; }
            float reveal = Math.Clamp(t / 0.4f, 0.05f, 1f) * 1.15f;   // the head runs a little past the end, so the whole stroke is lit before it fades
            float fade = 1f - Math.Clamp((t - 0.45f) / 0.55f, 0f, 1f);
            var basis = new Basis(s.Axis * s.Length, s.Up * MusouSlashRules.Width * (s.Length / 1.8f) * 2.2f, s.Normal);
            _mm.SetInstanceTransform(n, new Transform3D(basis, s.Pos));
            _mm.SetInstanceColor(n, s.Colour);
            _mm.SetInstanceCustomData(n, new Color(reveal, fade, s.Bow, 0f));
            n++;
        }
        Live = n;
        if (_mm.VisibleInstanceCount != n) _mm.VisibleInstanceCount = n;
    }
}
