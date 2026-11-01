using System;
using Godot;

namespace SmClient;

/// <summary>
/// The smoke cloud of a class change in the Stormbird (Musou Mode pass 2, item 2; the owner: "the player model [...] completely covered in a cloud of smoke that fades away after 2 seconds").
/// A pile of soft billboard puffs in a body-sized ellipsoid round the player's feet: fully opaque for <see cref="OpaqueSeconds"/> (the swap of the model lands under it), then fading to nothing by
/// <see cref="TotalSeconds"/> while the puffs swell and drift up. Placeholder demonstration (the texture and the numbers are ours; spec "Class change smoke"). Lives in game space (Z up, metres),
/// placed by the tag 11 record; the same record places the same puffs on every client (the seed is the player and the tick).
/// </summary>
public partial class TfwSmoke : Node3D
{
    /// <summary>The cloud hides the model fully until here, then fades to nothing at <see cref="TotalSeconds"/> (ours).</summary>
    public const float OpaqueSeconds = 0.5f, TotalSeconds = 2f;

    const int Puffs = 40;
    /// <summary>Half-extent of the ellipsoid the puffs sit in: wider and taller than a Space Marine (about 0.5 m across, 2.1 m tall), so he is covered from every side.</summary>
    static readonly Vector3 Reach = new(0.75f, 0.75f, 1.2f);

    static Texture2D? _puff;

    /// <summary>A soft round puff: opaque in the middle, a smooth edge (radial gradient, built once).</summary>
    static Texture2D PuffTexture()
    {
        if (_puff is not null) return _puff;
        var gradient = new Gradient { Offsets = [0f, 0.45f, 0.8f, 1f], Colors = [new Color(1f, 1f, 1f, 1f), new Color(1f, 1f, 1f, 0.95f), new Color(1f, 1f, 1f, 0.35f), new Color(1f, 1f, 1f, 0f)] };
        return _puff = new GradientTexture2D { Gradient = gradient, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(0.5f, 0f), Width = 128, Height = 128 };
    }

    readonly StandardMaterial3D[] _paint = new StandardMaterial3D[Puffs];
    readonly MeshInstance3D[] _puffs = new MeshInstance3D[Puffs];
    readonly Vector3[] _from = new Vector3[Puffs], _drift = new Vector3[Puffs];
    readonly float[] _size = new float[Puffs], _tone = new float[Puffs];
    float _age;

    /// <summary>The cloud for a record (<paramref name="seed"/>: the player and the tick), <paramref name="age"/> seconds in already (a record that arrived late).</summary>
    public TfwSmoke(uint seed, float age = 0f)
    {
        Name = "TfwSmoke";
        _age = age;
        uint state = seed * 2654435761u + 0x9E3779B9u;
        float Next() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return (state & 0xFFFFFF) / (float)0x1000000; }
        var mesh = new QuadMesh { Size = Vector2.One };
        for (int i = 0; i < Puffs; i++)
        {
            // Fibonacci-like spread over the ellipsoid's volume, with a jitter: even cover, a different cloud each time.
            float t = (i + 0.5f) / Puffs, theta = MathF.Acos(1f - 2f * t), phi = i * 2.399963f + Next();
            float r = MathF.Cbrt(0.35f + 0.65f * Next());
            _from[i] = new Vector3(MathF.Sin(theta) * MathF.Cos(phi) * Reach.X, MathF.Sin(theta) * MathF.Sin(phi) * Reach.Y, Reach.Z + MathF.Cos(theta) * Reach.Z) * new Vector3(r, r, 1f) ;
            _from[i].Z = Math.Clamp(_from[i].Z, 0.15f, 2.2f);
            _drift[i] = new Vector3((Next() - 0.5f) * 0.5f, (Next() - 0.5f) * 0.5f, 0.25f + Next() * 0.4f);
            _size[i] = 1.7f + Next() * 1.0f;
            _tone[i] = 0.58f + Next() * 0.2f;
            _paint[i] = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, BillboardKeepScale = true, AlbedoTexture = PuffTexture(), AlbedoColor = new Color(_tone[i], _tone[i], _tone[i] * 0.98f, 1f),
                DisableReceiveShadows = true, NoDepthTest = true,   // drawn over the model and the room: nothing of the body shows through while it is opaque
            };
            _puffs[i] = new MeshInstance3D { Mesh = mesh, MaterialOverride = _paint[i], CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_puffs[i]);
        }
        Apply();
    }

    /// <summary>The cloud's opacity at <paramref name="age"/> seconds: 1 until <see cref="OpaqueSeconds"/>, then down to 0 at <see cref="TotalSeconds"/> (smooth).</summary>
    public static float OpacityAt(float age)
    {
        if (age <= OpaqueSeconds) return 1f;
        if (age >= TotalSeconds) return 0f;
        float x = (age - OpaqueSeconds) / (TotalSeconds - OpaqueSeconds);
        return 1f - x * x * (3f - 2f * x);
    }

    /// <summary>Puffs piled over each other along a line of sight through the middle of the cloud (about 12 of the 40): one puff's alpha is worked out from that so the whole cloud fades evenly, not all at the end.</summary>
    const float Overlap = 12f;

    /// <summary>The alpha of one puff when the cloud as a whole should be <paramref name="opacity"/> opaque (<c>1 - (1 - a)^Overlap = opacity</c>).</summary>
    public static float PuffAlpha(float opacity) => opacity >= 1f ? 1f : opacity <= 0f ? 0f : 1f - MathF.Pow(1f - opacity, 1f / Overlap);

    /// <summary>Seconds of life so far (tests).</summary>
    public float Age => _age;

    public override void _Process(double delta)
    {
        _age += (float)delta;
        if (_age >= TotalSeconds) { QueueFree(); return; }
        Apply();
    }

    void Apply()
    {
        float alpha = PuffAlpha(OpacityAt(_age)), swell = 1f + 0.22f * Math.Min(_age / TotalSeconds, 1f);
        for (int i = 0; i < Puffs; i++)
        {
            _puffs[i].Position = _from[i] + _drift[i] * _age;
            _puffs[i].Scale = new Vector3(_size[i] * swell, _size[i] * swell, 1f);
            var c = _paint[i].AlbedoColor;
            _paint[i].AlbedoColor = new Color(c.R, c.G, c.B, alpha);
        }
    }
}
