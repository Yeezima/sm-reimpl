using System;
using Godot;

namespace SmClient;

/// <summary>
/// ui/hud-3 (owner, 2026-10-30, his reference screenshot): the ground pound's target marker is four yellow double chevrons around the landing point (top, left, right, bottom of the view),
/// each pointing at the centre, sliding inward in a repeating cycle. Replaces the data's <c>mp_assault_land_reticule</c> effect, which the owner did not recognise as the original's
/// marker. The look (size, speed, glow) is **ours**, from the reference picture; the spec row "Pound target marker" says when it shows.
/// </summary>
public sealed partial class PoundMarker : Node3D
{
    /// <summary>Distance (m) from the point to a chevron at the start of the cycle and at its end, the chevron's size (m), and the cycle's length (s): measured by eye on the reference (ours).</summary>
    const float Far = 1.8f, Near = 0.7f, Size = 1.1f, Cycle = 0.9f;

    static ImageTexture? _texture;
    readonly MeshInstance3D[] _chevrons = new MeshInstance3D[4];
    readonly StandardMaterial3D[] _materials = new StandardMaterial3D[4];
    readonly Vector3[] _dirs = new Vector3[4];

    public PoundMarker()
    {
        _texture ??= MakeTexture();
        for (int i = 0; i < 4; i++)
        {
            _materials[i] = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoTexture = _texture,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add, CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = new Color(1f, 0.86f, 0.18f, 1f),
            };
            _chevrons[i] = new MeshInstance3D { Mesh = new QuadMesh { Orientation = PlaneMesh.OrientationEnum.Y, Size = new Vector2(Size, Size) }, MaterialOverride = _materials[i],
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_chevrons[i]);
        }
        Visible = false;
    }

    /// <summary>The cycle's progress (0 far .. 1 near) at a time.</summary>
    public static float Phase(double time) => (float)(time % Cycle / Cycle);

    /// <summary>Distance of a chevron from the point at a cycle progress.</summary>
    public static float Distance(float phase) => Far + (Near - Far) * phase;

    /// <summary>Fade over the cycle: in at the start, out at the end.</summary>
    public static float Alpha(float phase) => MathF.Min(MathF.Min(phase / 0.15f, (1f - phase) / 0.25f), 1f);

    /// <summary>Shows the marker at a ground point; <paramref name="forward"/> is the view's horizontal direction (the "top" chevron lies that way).</summary>
    public void Show(Vector3 point, Vector3 forward, double time)
    {
        forward.Y = 0f;
        forward = forward.LengthSquared() < 1e-6f ? Vector3.Forward : forward.Normalized();
        var right = forward.Cross(Vector3.Up);
        float phase = Phase(time), dist = Distance(phase), a = Math.Clamp(Alpha(phase), 0f, 1f);
        var dirs = _dirs;
        dirs[0] = forward; dirs[1] = -right; dirs[2] = right; dirs[3] = -forward;   // top, left, right, bottom of the view
        GlobalPosition = point;
        for (int i = 0; i < 4; i++)
        {
            // The texture's chevron points to +Z (down the picture): it points at the centre when -Z looks away from it.
            _chevrons[i].Transform = new Transform3D(Basis.LookingAt(dirs[i], Vector3.Up), dirs[i] * dist);
            _materials[i].AlbedoColor = new Color(1f, 0.86f, 0.18f, a);
        }
        Visible = true;
    }

    public void Hide2() => Visible = false;

    /// <summary>A double chevron pointing down the picture, yellow with a soft edge.</summary>
    static ImageTexture MakeTexture()
    {
        const int n = 128;
        var image = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = MathF.Abs(x - (n - 1) / 2f), best = 0f;
                // Two chevrons: the arm's distance from its line y = apex - dx * 0.8 (a V pointing down), thickness 9 px, 34 px apart.
                for (int k = 0; k < 2; k++)
                {
                    float apex = 78f - k * 34f;
                    float line = apex - dx * 0.8f;
                    float dist = MathF.Abs(y - line) * 0.78f;
                    float edge = Math.Clamp(1f - (dist - 3.5f) / 3f, 0f, 1f);
                    if (dx > 50f) edge = 0f;
                    best = MathF.Max(best, edge);
                }
                image.SetPixel(x, y, new Color(1f, 1f, 1f, best));
            }
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }
}
