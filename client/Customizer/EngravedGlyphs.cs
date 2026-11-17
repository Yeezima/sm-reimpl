using System;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The Armoury's icon wells when the game has no picture for an item (ui/overhaul, Opus review 1: no empty black wells): a faint engraved glyph of the item's type, drawn in code (nothing from the install or the concept is
/// used). Registered under the names <c>glyph-gun</c>, <c>glyph-pistol</c>, <c>glyph-blade</c>, <c>glyph-grenade</c>, <c>glyph-perk</c> and <c>glyph-ork</c> in <see cref="ConceptArt"/>, so every place that takes an icon name takes them.
/// </summary>
public static class EngravedGlyphs
{
    public static readonly string[] Names = ["glyph-gun", "glyph-pistol", "glyph-blade", "glyph-grenade", "glyph-perk", "glyph-ork"];

    /// <summary>Makes the glyphs once and puts them in the art cache.</summary>
    public static void Register()
    {
        foreach (var n in Names) if (!ConceptArt.Has(n)) ConceptArt.Put(n, Make(n));
    }

    /// <summary>The glyph name of a slot or step word: weapon words to the gun, and so on; the perk star otherwise.</summary>
    public static string For(string words)
    {
        string w = words.ToLowerInvariant();
        return w.Contains("primary") || w.Contains("weapon") && !w.Contains("melee") ? "glyph-gun" : w.Contains("side") || w.Contains("pistol") ? "glyph-pistol" : w.Contains("melee") || w.Contains("sword") || w.Contains("knife") || w.Contains("axe") || w.Contains("hammer") ? "glyph-blade"
            : w.Contains("equip") || w.Contains("grenade") ? "glyph-grenade" : "glyph-perk";
    }

    const int N = 96;

    static ImageTexture Make(string name)
    {
        var img = Image.CreateEmpty(N, N, false, Image.Format.Rgba8);
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float lit = Cover(name, x + 0.5f, y + 0.5f), shade = Cover(name, x - 1.5f, y - 1.5f);   // the engraving: the shape, and its edge cut a little darker toward the light
                if (lit <= 0f) { img.SetPixel(x, y, new Color(0f, 0f, 0f, 0f)); continue; }
                float edge = Mathf.Clamp(1f - shade * 0.7f, 0f, 1f);
                var c = new Color(0.62f - 0.2f * edge, 0.55f - 0.18f * edge, 0.40f - 0.14f * edge, 0.34f);
                img.SetPixel(x, y, c);
            }
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    static float Box(float x, float y, float x0, float y0, float x1, float y1) => x >= x0 && x <= x1 && y >= y0 && y <= y1 ? 1f : 0f;
    static float Disc(float x, float y, float cx, float cy, float r) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r ? 1f : 0f;

    /// <summary>1 inside the glyph at that pixel, else 0 (the shapes are simple unions of boxes and discs on a 96 px grid).</summary>
    static float Cover(string name, float x, float y)
    {
        switch (name)
        {
            case "glyph-gun":
                return Math.Max(Math.Max(Box(x, y, 10, 38, 78, 50), Box(x, y, 54, 50, 66, 74)), Math.Max(Box(x, y, 8, 34, 30, 60), Math.Max(Box(x, y, 78, 41, 90, 47), Box(x, y, 30, 50, 48, 58))));
            case "glyph-pistol":
                return Math.Max(Math.Max(Box(x, y, 22, 36, 76, 50), Box(x, y, 22, 50, 38, 74)), Box(x, y, 76, 40, 86, 46));
            case "glyph-blade":
            {
                // a sword on the diagonal: the blade, the guard, the grip, the pommel
                float u = (x + y) / 1.4142f - 48f * 1.4142f, v = (y - x) / 1.4142f;   // along the blade and across it
                float blade = u > -14f && u < 40f && Mathf.Abs(v) < 5f ? 1f : 0f, guard = u > -20f && u < -14f && Mathf.Abs(v) < 17f ? 1f : 0f, grip = u > -40f && u <= -20f && Mathf.Abs(v) < 3.5f ? 1f : 0f;
                float pommel = Disc(u, v, -44f, 0f, 5.5f);
                return Math.Max(Math.Max(blade, guard), Math.Max(grip, pommel));
            }
            case "glyph-grenade":
                return Math.Max(Math.Max(Disc(x, y, 48, 54, 22), Box(x, y, 40, 22, 56, 34)), Box(x, y, 56, 24, 70, 28));
            case "glyph-perk":
            {
                // a four-point star
                float ax = Mathf.Abs(x - 48f), ay = Mathf.Abs(y - 48f);
                return Mathf.Sqrt(ax) + Mathf.Sqrt(ay) < 5.4f ? 1f : 0f;
            }
            case "glyph-ork":
            {
                // a head: the skull's disc with two eye holes and two tusks
                float head = Disc(x, y, 48, 44, 26) + Box(x, y, 30, 52, 66, 72) > 0f ? 1f : 0f;
                float eyes = Math.Max(Disc(x, y, 38, 42, 5), Disc(x, y, 58, 42, 5));
                float tusks = Math.Max(Box(x, y, 32, 72, 38, 84), Box(x, y, 58, 72, 64, 84));
                return Math.Max(head * (1f - eyes), tusks);
            }
        }
        return 0f;
    }
}
