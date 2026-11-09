using System;
using Godot;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// The Psychic Shockwave's reach, drawn (librarian-lightning, owner 2026-11-10: the Shockwave now reaches 60 m; **ours**): a ring of psychic light racing out along the ground from the Librarian to
/// <see cref="LibrarianRules.ShockwaveRadius"/> in <see cref="Seconds"/>, with a low shimmering wall rising off it and a flash at its heart, fading as it goes. Two meshes built once (a flat annulus and an
/// open cylinder, both of unit radius) scaled each frame; additive, unlit, no texture, the same in Forward+ and Compatibility. It plays over the original blast the caster still shows at his feet.
/// Positions are the game space of the parent (Z up).
/// </summary>
public sealed partial class LibrarianShockwaveRing : Node3D
{
    /// <summary>The ring's run out to the full radius, and the wall's height (ours).</summary>
    public const float Seconds = 0.55f, WallHeight = 4f;

    const string Code = """
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled;
uniform float fade = 1.0;
uniform float band = 0.08;
uniform vec3 colour = vec3(0.45, 0.65, 1.0);
uniform bool wall = false;
void fragment() {
    // UV.x: 0 inner .. 1 outer edge of the ring (the wall: 0 foot .. 1 top); UV.y around.
    float a;
    if (wall) {
        a = pow(1.0 - UV.x, 2.0) * (0.75 + 0.25 * sin(UV.y * 260.0 + TIME * 30.0));
    } else {
        float edge = smoothstep(1.0, 1.0 - band, UV.x) * smoothstep(1.0 - band * 6.0, 1.0 - band, UV.x);
        float trail = smoothstep(0.0, 1.0, UV.x) * 0.18;
        a = edge + trail * (0.7 + 0.3 * sin(UV.y * 180.0 - TIME * 20.0));
    }
    ALBEDO = mix(colour, vec3(1.0), clamp(a - 0.4, 0.0, 1.0)) * 2.2;
    ALPHA = clamp(a * fade, 0.0, 1.0);
}
""";

    readonly MeshInstance3D _ring, _wall;
    readonly ShaderMaterial _ringMat, _wallMat;
    readonly OmniLight3D _flash;
    double _startedAt = double.NegativeInfinity;

    public LibrarianShockwaveRing()
    {
        Name = "LibrarianShockwaveRing";
        var shader = new Shader { Code = Code };
        _ringMat = new ShaderMaterial { Shader = shader, RenderPriority = 3 };
        _wallMat = new ShaderMaterial { Shader = shader, RenderPriority = 3 };
        _wallMat.SetShaderParameter("wall", true);
        var big = new Aabb(new Vector3(-100f, -100f, -10f), new Vector3(200f, 200f, 30f));
        _ring = new MeshInstance3D { Name = "Ring", Mesh = Annulus(96), MaterialOverride = _ringMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false, CustomAabb = big };
        _wall = new MeshInstance3D { Name = "Wall", Mesh = Cylinder(96), MaterialOverride = _wallMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false, CustomAabb = big };
        _flash = new OmniLight3D { Name = "Flash", LightColor = new Color(0.55f, 0.72f, 1f), OmniRange = 18f, OmniAttenuation = 1.2f, ShadowEnabled = false, Visible = false };
        AddChild(_ring); AddChild(_wall); AddChild(_flash);
    }

    int _warm;

    /// <summary>Draws the ring invisibly for a few frames in front of the camera, so its material is compiled before the first Shockwave.</summary>
    public void WarmUp() => _warm = 3;

    /// <summary>Starts the ring at <paramref name="feet"/> (game space).</summary>
    public void Play(Vector3 feet)
    {
        Position = feet + new Vector3(0f, 0f, 0.15f);
        _startedAt = LibrarianClock.Now;
        _ring.Visible = _wall.Visible = _flash.Visible = true;
    }

    public override void _Process(double delta)
    {
        if (_warm > 0 && GetViewport()?.GetCamera3D() is { } camera && GetParent() is Node3D parent)
        {
            _warm--;
            Position = parent.ToLocal(camera.GlobalPosition - camera.GlobalBasis.Z * 4f);
            Scale = Vector3.One;
            _ringMat.SetShaderParameter("fade", 0f); _wallMat.SetShaderParameter("fade", 0f);
            _ring.Visible = _wall.Visible = _warm > 0;
            _ring.Scale = _wall.Scale = Vector3.One * 0.5f;
            return;
        }
        if (!_ring.Visible) return;
        float t = (float)(LibrarianClock.Now - _startedAt);
        float u = Math.Clamp(t / Seconds, 0f, 1f);
        if (t > Seconds + 0.25f) { _ring.Visible = _wall.Visible = _flash.Visible = false; return; }
        float eased = 1f - MathF.Pow(1f - u, 2.2f);   // fast out of the hands, slowing at the edge
        float radius = MathF.Max(0.5f, LibrarianRules.ShockwaveRadius * eased);
        float fade = u < 1f ? 1f - 0.5f * u : MathF.Max(0f, 0.5f - (t - Seconds) / 0.25f * 0.5f);
        _ring.Scale = new Vector3(radius, radius, 1f);
        _wall.Scale = new Vector3(radius, radius, WallHeight * (1f - 0.6f * u));
        _ringMat.SetShaderParameter("fade", fade);
        _ringMat.SetShaderParameter("band", Math.Clamp(0.6f / radius, 0.02f, 0.3f));   // the bright edge stays about 0.6 m deep
        _wallMat.SetShaderParameter("fade", fade * 0.6f);
        _flash.LightEnergy = 9f * MathF.Max(0f, 1f - t / 0.3f);
        _flash.Visible = _flash.LightEnergy > 0.05f;
    }

    /// <summary>A flat ring from radius 0 to 1 in the XY plane (game space), UV.x the radius, UV.y the angle.</summary>
    static ArrayMesh Annulus(int segments)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < segments; i++)
        {
            float a0 = MathF.Tau * i / segments, a1 = MathF.Tau * (i + 1) / segments;
            var o0 = new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0f); var o1 = new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0f);
            float v0 = i / (float)segments, v1 = (i + 1) / (float)segments;
            Tri(st, Vector3.Zero, new Vector2(0f, v0), o0, new Vector2(1f, v0), o1, new Vector2(1f, v1));
        }
        return st.Commit();
    }

    /// <summary>An open cylinder of radius 1 from height 0 to 1 (game space), UV.x the height, UV.y the angle.</summary>
    static ArrayMesh Cylinder(int segments)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < segments; i++)
        {
            float a0 = MathF.Tau * i / segments, a1 = MathF.Tau * (i + 1) / segments;
            var b0 = new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0f); var b1 = new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0f);
            var t0 = b0 + new Vector3(0f, 0f, 1f); var t1 = b1 + new Vector3(0f, 0f, 1f);
            float v0 = i / (float)segments, v1 = (i + 1) / (float)segments;
            Tri(st, b0, new Vector2(0f, v0), t0, new Vector2(1f, v0), t1, new Vector2(1f, v1));
            Tri(st, b0, new Vector2(0f, v0), t1, new Vector2(1f, v1), b1, new Vector2(0f, v1));
        }
        return st.Commit();
    }

    static void Tri(SurfaceTool st, Vector3 a, Vector2 ua, Vector3 b, Vector2 ub, Vector3 c, Vector2 uc)
    {
        st.SetUV(ua); st.AddVertex(a);
        st.SetUV(ub); st.AddVertex(b);
        st.SetUV(uc); st.AddVertex(c);
    }
}
