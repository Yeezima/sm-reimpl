using System;
using System.Collections.Generic;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The Armoury's regions for <c>--ui-compare-panel ... --screen armoury</c> (phase 4d): every painted frame, plate, tile and card of panel 03 with the live content (words, icons, the weapon in the tile) left out. A region is the
/// piece's own alpha (read from client/assets/ui) at its place in the concept's screen, minus the zones the live parts fill. Coordinates: panel 03's own (screen + (10, 26)).
/// </summary>
public static partial class UiComparePanel
{
    static readonly Dictionary<string, Image> AlphaCache = new();

    /// <summary>The pixels where the texture <paramref name="art"/> (client/assets/ui/*.png at 4x) is opaque, placed with its top left at the concept screen point (x, y).</summary>
    static bool[] ArtMask(string art, float x, float y)
    {
        if (!AlphaCache.TryGetValue(art, out var img))
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "assets", "ui"));
            img = Image.LoadFromFile(System.IO.Path.Combine(dir, art + ".png")); img.Convert(Image.Format.Rgba8); AlphaCache[art] = img;
        }
        var m = new bool[W * H];
        for (int py = 0; py < H; py++) for (int px = 0; px < W; px++)
            {
                int tx = (int)((px - x + 0.5f) * 4f), ty = (int)((py - y + 0.5f) * 4f);
                m[py * W + px] = tx >= 0 && ty >= 0 && tx < img.GetWidth() && ty < img.GetHeight() && img.GetPixel(tx, ty).A > 0.9f;
            }
        return m;
    }

    static bool[] SRect(float x, float y, float w, float h) => Rect(x + 10, y + 26, x + w + 10, y + h + 26);   // a rectangle in the concept's screen coordinates

    static void ArmouryRegions(List<(string, bool[], bool)> regions)
    {
        void Add(string name, bool[] mask) => regions.Add((name, mask, false));
        var a = UiTextureBuilder.ArmTitle; Add("title plate + wordmark", ArtMask("arm-title", a.Position.X, a.Position.Y));
        var rail = UiTextureBuilder.ArmRail;
        Add("rail frame", Minus(ArtMask("overlay-armoury", rail.Position.X, rail.Position.Y), SRect(34, 97, 244, 254), SRect(0, 0, 780, 80)));
        // the rows: each plate minus the icon and the name
        string[] names = ["row LIBRARIAN (gold)", "row TACTICAL", "row ASSAULT", "row DEVASTATOR"];
        for (int i = 0; i < 4; i++)
        {
            var r = UiTextureBuilder.ArmRows[i];
            Add(names[i], Minus(SRect(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y), SRect(r.Position.X + 14, r.Position.Y, 100, r.Size.Y), SRect(r.Position.X + 74, r.Position.Y, 150, r.Size.Y)));
        }
        var wi = UiTextureBuilder.ArmWeaponIcon; var wn = UiTextureBuilder.ArmWeaponName;
        Add("weapon icon frame", Minus(SRect(wi.Position.X, wi.Position.Y, wi.Size.X, wi.Size.Y), SRect(wi.Position.X + 5, wi.Position.Y + 5, wi.Size.X - 10, wi.Size.Y - 10)));
        Add("weapon name plate", Minus(SRect(wn.Position.X, wn.Position.Y, wn.Size.X, wn.Size.Y), SRect(wn.Position.X + 8, wn.Position.Y + 2, 100, wn.Size.Y - 4)));
        string[] tn = ["tile 1 (gold)", "tile 2", "tile 3"];
        for (int i = 0; i < 3; i++)
        {
            var t = UiTextureBuilder.ArmTiles[i];
            Add(tn[i], Minus(SRect(t.Position.X, t.Position.Y, t.Size.X, t.Size.Y), SRect(t.Position.X + 6, t.Position.Y + 6, t.Size.X - 12, t.Size.Y - 12)));
        }
        for (int i = 0; i < 3; i++)
        {
            float y = UiTextureBuilder.ArmCard.Position.Y + i * UiTextureBuilder.ArmCardPitch - (i == 2 ? 0f : 0f);
            Add("card " + (i + 1), Minus(ArtMask("arm-card", UiTextureBuilder.ArmCard.Position.X, y), SRect(561, y + 5, 60, 52), SRect(626, y + 2, 126, 60)));
        }
        var c = UiTextureBuilder.ArmConfirm; Add("CONFIRM plate", Minus(ArtMask("arm-confirm", c.Position.X, c.Position.Y), SRect(c.Position.X + 40, c.Position.Y + 8, c.Size.X - 80, c.Size.Y - 14)));
        var w = UiTextureBuilder.ArmWings; Add("wings + skull", ArtMask("arm-wings", w.Position.X, w.Position.Y));
        var b = UiTextureBuilder.ArmBadge; Add("mode badge", Minus(ArtMask("arm-badge", b.Position.X, b.Position.Y), SRect(b.Position.X + 38, b.Position.Y + 3, b.Size.X - 76, b.Size.Y - 6)));
        var k = UiTextureBuilder.ArmBack; Add("BACK plate", Minus(ArtMask("arm-back", k.Position.X, k.Position.Y), SRect(k.Position.X + 20, k.Position.Y + 8, k.Size.X - 40, k.Size.Y - 16)));
    }
}
