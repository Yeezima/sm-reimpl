using System;
using System.Collections.Generic;
using Godot;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// The Musou powerups' icons (owner, 2026-11-10: "much more minimalistic and match the black and white theme"): white line glyphs on transparency, drawn once at 96 x 96 with soft edges, shared by the
/// world's drops and the HUD's buff chips. Double Damage "x2", Fear a skull, Supercharged Melee a blade with a bolt, Rapid Fire three chevrons. Ours.
/// </summary>
public static class MusouPowerupIcons
{
    const int Size = 96;
    static readonly Dictionary<MusouPowerup, ImageTexture> Cache = new();

    /// <summary>The powerup's name in the menus' capitals.</summary>
    public static string Name(MusouPowerup kind) => kind switch
    {
        MusouPowerup.DoubleDamage => "DOUBLE DAMAGE",
        MusouPowerup.Fear => "FEAR",
        MusouPowerup.Supercharged => "SUPERCHARGED MELEE",
        MusouPowerup.RapidFire => "RAPID FIRE",
        _ => "POWERUP",
    };

    public static ImageTexture Of(MusouPowerup kind)
    {
        if (Cache.TryGetValue(kind, out var t)) return t;
        var segs = new List<(Vector2 A, Vector2 B, float W)>();
        var rings = new List<(Vector2 C, float R, float W)>();
        var discs = new List<(Vector2 C, float R)>();
        void Line(params float[] p) { for (int i = 0; i + 3 < p.Length; i += 2) segs.Add((new(p[i], p[i + 1]), new(p[i + 2], p[i + 3]), 4.5f)); }
        switch (kind)
        {
            case MusouPowerup.DoubleDamage:   // x2
                Line(14, 36, 40, 66); Line(40, 36, 14, 66);
                Line(50, 40, 56, 32, 68, 30, 78, 36, 78, 46, 52, 66, 82, 66);
                break;
            case MusouPowerup.Fear:   // a skull
                rings.Add((new(48, 40), 24f, 4.5f));
                discs.Add((new(38, 40), 6.5f)); discs.Add((new(58, 40), 6.5f));
                Line(34, 62, 34, 76, 62, 76, 62, 62); Line(43, 64, 43, 76); Line(53, 64, 53, 76);
                break;
            case MusouPowerup.Supercharged:   // a blade and a bolt
                segs.Add((new(18, 78), new(62, 22), 6f));
                Line(16, 58, 38, 80);
                Line(70, 44, 62, 58, 74, 58, 64, 76);
                break;
            case MusouPowerup.RapidFire:   // >>>
                foreach (float x in new[] { 16f, 38f, 60f }) Line(x, 28, x + 18, 48, x, 68);
                break;
        }
        var img = Image.CreateEmpty(Size, Size, false, Image.Format.La8);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float a = 0f;
                foreach (var (A, B, W) in segs) a = MathF.Max(a, Math.Clamp(W / 2f - DistanceToSegment(p, A, B) + 0.5f, 0f, 1f));
                foreach (var (C, R, W) in rings) a = MathF.Max(a, Math.Clamp(W / 2f - MathF.Abs(p.DistanceTo(C) - R) + 0.5f, 0f, 1f));
                foreach (var (C, R) in discs) a = MathF.Max(a, Math.Clamp(R - p.DistanceTo(C) + 0.5f, 0f, 1f));
                img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        img.GenerateMipmaps();
        return Cache[kind] = ImageTexture.CreateFromImage(img);
    }

    static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-6f ? 0f : Math.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }
}
