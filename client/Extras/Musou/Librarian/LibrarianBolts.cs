using System;
using System.Collections.Generic;
using Godot;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// The Librarian's chain lightning, drawn procedurally (**ours**; no third-party file or audio is used, and no original effect either): per link a jagged polyline from the caster's hand to the primary enemy and on to
/// each chained one, re-rolled every <see cref="CrackleSeconds"/> so it crackles, with a short side fork, drawn as two crossed ribbons in two layers (a near-white core, an electric-blue <c>#5aa0ff</c> outer glow) in
/// an additive unshaded material. The first links are thicker and brighter and the width falls along the chain as the damage does (x0.85 a link). A bright spark flares on every struck chest, a soft blue glow sits at the
/// hand, and everything fades over <see cref="FadeSeconds"/> after the last section that fed it. Pooled: at most <see cref="MaxLinks"/> meshes, built once, rebuilt in place on each re-roll (no node or material per frame).
/// The positions are Godot world space (the caller converts).
/// </summary>
public sealed partial class LibrarianBolts : Node3D
{
    public const int MaxLinks = LibrarianRules.MaxDrawnLinks;
    public const double CrackleSeconds = 0.06, FadeSeconds = 0.15;
    const int MaxSegments = 32;
    static readonly Color Core = new(1f, 1f, 1f, 1f), Glow = new(0x5a / 255f, 0xa0 / 255f, 1f, 0.55f);

    readonly MeshInstance3D[] _bolts = new MeshInstance3D[MaxLinks];
    readonly ImmediateMesh[] _meshes = new ImmediateMesh[MaxLinks];
    readonly MeshInstance3D[] _sparks = new MeshInstance3D[MaxLinks];
    readonly MeshInstance3D _hand;
    readonly StandardMaterial3D _material;
    readonly StandardMaterial3D _sparkMaterial;
    readonly Vector3[] _from = new Vector3[MaxLinks], _to = new Vector3[MaxLinks];
    readonly Vector3[] _points = new Vector3[MaxSegments + 1];
    readonly Vector3[] _fork = new Vector3[4];
    readonly Random _roll = new(11);
    int _count;
    double _fedAt = double.NegativeInfinity, _builtAt = double.NegativeInfinity;

    public LibrarianBolts()
    {
        Name = "LibrarianBolts";
        _material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            VertexColorUseAsAlbedo = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _sparkMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            AlbedoColor = new Color(0.8f, 0.92f, 1f, 0.9f),
        };
        for (int i = 0; i < MaxLinks; i++)
        {
            _meshes[i] = new ImmediateMesh();
            _bolts[i] = new MeshInstance3D { Mesh = _meshes[i], MaterialOverride = _material, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _sparks[i] = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.3f, Height = 0.6f, RadialSegments = 8, Rings = 4 }, MaterialOverride = _sparkMaterial, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_bolts[i]);
            AddChild(_sparks[i]);
        }
        _hand = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.22f, Height = 0.44f, RadialSegments = 10, Rings = 5 }, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                AlbedoColor = new Color(0x5a / 255f, 0xa0 / 255f, 1f, 0.55f),
            },
        };
        AddChild(_hand);
    }

    /// <summary>A section's links (Godot space, at most <see cref="MaxLinks"/>): they replace the last and the fade starts over.</summary>
    public void Feed(ReadOnlySpan<(Vector3 From, Vector3 To)> links, double now)
    {
        _count = Math.Min(links.Length, MaxLinks);
        for (int i = 0; i < _count; i++) { _from[i] = links[i].From; _to[i] = links[i].To; }
        _fedAt = now;
        _builtAt = double.NegativeInfinity;   // rebuild at once
    }

    public override void _Process(double delta)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        float alive = (float)Math.Clamp(1.0 - (now - _fedAt) / FadeSeconds, 0.0, 1.0);
        if (alive <= 0f || _count == 0)
        {
            if (_hand.Visible) { _hand.Visible = false; for (int i = 0; i < MaxLinks; i++) { _bolts[i].Visible = false; _sparks[i].Visible = false; } }
            return;
        }
        _material.AlbedoColor = new Color(1f, 1f, 1f, alive);
        _sparkMaterial.AlbedoColor = new Color(0.8f, 0.92f, 1f, 0.9f * alive);
        if (now - _builtAt >= CrackleSeconds) { Rebuild(); _builtAt = now; }
        _hand.Visible = true;
    }

    void Rebuild()
    {
        _hand.Position = _from[0];
        _hand.Scale = Vector3.One * (0.8f + 0.5f * (float)_roll.NextDouble());
        for (int i = 0; i < MaxLinks; i++)
        {
            bool on = i < _count;
            _bolts[i].Visible = on;
            _sparks[i].Visible = on;
            if (!on) continue;
            float fall = MathF.Pow(0.85f, i);   // thick and bright near the caster, thinner down the chain, as the damage falls
            float width = 0.018f + 0.04f * fall;   // pass 2: thinner, with soft edges (Ribbons), so the bolts read as arcs, not bars
            _current = _meshes[i];
            _meshes[i].ClearSurfaces();
            _meshes[i].SurfaceBegin(Mesh.PrimitiveType.Triangles);
            float length = _from[i].DistanceTo(_to[i]);
            Jag(_from[i], _to[i], MaxSegments, Math.Clamp(0.2f + 0.035f * length, 0.2f, 0.75f));
            Ribbons(_points, 0, Count(_from[i], _to[i]) + 1, width * 5f, new Color(Glow.R, Glow.G, Glow.B, Glow.A * (0.45f + 0.45f * fall)));
            Ribbons(_points, 0, Count(_from[i], _to[i]) + 1, width, Core);
            // A short side fork from a middle point, thin and blue.
            int segments = Count(_from[i], _to[i]);
            int at = 1 + _roll.Next(Math.Max(1, segments - 1));
            var start = _points[Math.Min(at, segments)];
            var dir = (_to[i] - _from[i]).Normalized();
            var side = Perp(dir, _roll.NextSingle() * MathF.Tau) * (0.8f + (float)_roll.NextDouble()) + dir * 0.6f;
            _fork[0] = start;
            for (int k = 1; k < _fork.Length; k++) _fork[k] = start + side * (k / 3f) + Perp(dir, _roll.NextSingle() * MathF.Tau) * 0.15f;
            Ribbons(_fork, 0, _fork.Length, width * 1.5f, new Color(0.75f, 0.88f, 1f, 0.8f));
            if (segments > 6)   // pass 2: a second fork on longer links, the other way
            {
                var start2 = _points[Math.Clamp(segments - at, 1, segments)];
                var side2 = Perp(dir, _roll.NextSingle() * MathF.Tau) * (0.5f + 0.8f * (float)_roll.NextDouble()) - dir * 0.3f;
                _fork[0] = start2;
                for (int k = 1; k < _fork.Length; k++) _fork[k] = start2 + side2 * (k / 3f) + Perp(dir, _roll.NextSingle() * MathF.Tau) * 0.12f;
                Ribbons(_fork, 0, _fork.Length, width * 1.2f, new Color(0.75f, 0.88f, 1f, 0.65f));
            }
            _meshes[i].SurfaceEnd();
            _sparks[i].Position = _to[i];
            _sparks[i].Scale = Vector3.One * (0.6f + 0.9f * fall * (0.6f + 0.4f * (float)_roll.NextDouble()));
        }
    }

    static int Count(Vector3 from, Vector3 to) => Math.Clamp((int)(from.DistanceTo(to) / 0.55f), 5, MaxSegments);

    /// <summary>The jagged line from <paramref name="a"/> to <paramref name="b"/> into <see cref="_points"/>: end points fixed, the others pushed sideways at random.</summary>
    void Jag(Vector3 a, Vector3 b, int most, float amount)
    {
        int n = Math.Min(Count(a, b), most);
        var dir = (b - a);
        float length = dir.Length();
        dir = length > 1e-4f ? dir / length : Vector3.Up;
        _points[0] = a;
        for (int k = 1; k < n; k++)
        {
            float t = k / (float)n;
            float envelope = MathF.Sin(t * MathF.PI);   // still at both ends
            _points[k] = a.Lerp(b, t) + Perp(dir, _roll.NextSingle() * MathF.Tau) * (amount * envelope * (0.3f + (float)_roll.NextDouble()));
        }
        _points[n] = b;
    }

    static Vector3 Perp(Vector3 dir, float angle)
    {
        var u = MathF.Abs(dir.Y) > 0.95f ? Vector3.Right : Vector3.Up;
        var s = dir.Cross(u).Normalized();
        var t = dir.Cross(s).Normalized();
        return s * MathF.Cos(angle) + t * MathF.Sin(angle);
    }

    /// <summary>Two crossed ribbons of half-width <paramref name="w"/> along a polyline (a bolt reads from any side without facing the camera); pass 2: each ribbon is bright on its spine and fades to nothing at its edges.</summary>
    void Ribbons(Vector3[] pts, int from, int count, float w, Color colour)
    {
        for (int k = from; k + 1 < from + count; k++)
        {
            var a = pts[k]; var b = pts[k + 1];
            var d = b - a;
            if (d.LengthSquared() < 1e-8f) continue;
            d = d.Normalized();
            var s = Perp(d, 0f) * w; var t = Perp(d, MathF.PI / 2f) * w;
            var clear = new Color(colour.R, colour.G, colour.B, 0f);
            Soft(a, b, s, colour, clear);
            Soft(a, b, -s, colour, clear);
            Soft(a, b, t, colour, clear);
            Soft(a, b, -t, colour, clear);
        }
    }

    ImmediateMesh _current = null!;

    /// <summary>Half a ribbon: the spine a-b in <paramref name="spine"/>, the edge pushed by <paramref name="off"/> in <paramref name="edge"/>.</summary>
    void Soft(Vector3 a, Vector3 b, Vector3 off, Color spine, Color edge)
    {
        var im = _current;
        im.SurfaceSetColor(spine); im.SurfaceAddVertex(a); im.SurfaceSetColor(edge); im.SurfaceAddVertex(a + off); im.SurfaceSetColor(edge); im.SurfaceAddVertex(b + off);
        im.SurfaceSetColor(spine); im.SurfaceAddVertex(a); im.SurfaceSetColor(edge); im.SurfaceAddVertex(b + off); im.SurfaceSetColor(spine); im.SurfaceAddVertex(b);
    }
}
