using System;
using System.Globalization;
using Godot;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// The own powerups on the Musou HUD (owner, 2026-11-10; the lead: "a minimal black-and-white buff indicator: icon and timer, near your own bar at the bottom left"): one chip per active buff in a row
/// just right of the own armour and health bar (<see cref="MusouHudLayout.VitalsCorner"/>; bottom left with <c>--musou-old-hud</c>, where that bar is top centre): a dark square with a thin white frame,
/// the powerup's white icon (<see cref="MusouPowerupIcons"/>), a white bar along its bottom draining with the time, and the seconds left under it. The last 3 s blink. Sizes ours.
/// </summary>
public sealed partial class MusouHud
{
    const float ChipSize = 40f, ChipGap = 10f;
    readonly string[] _buffSeconds = new string[5];
    readonly int[] _buffSecondsFor = [-1, -1, -1, -1, -1];

    void DrawBuffs(Vector2 vs)
    {
        if (Data.Buffs.Count == 0) return;
        // Right of the own bar's box (its left edge at VitalsCorner.X, about 385 wide at its scale), its bottom on the bar's bottom.
        float x = MusouHudLayout.Old ? MusouHudLayout.Left : MusouHudLayout.VitalsCorner.X + 395f * 1.5f * MusouHudLayout.VitalsCorner.Z + 18f;
        float bottom = vs.Y - (MusouHudLayout.Old ? MusouHudLayout.BottomMargin : MusouHudLayout.VitalsCorner.Y) - 4f;
        foreach (var (kind, left, total) in Data.Buffs)
        {
            float y = bottom - ChipSize;
            bool blink = left < 3f && ((int)(_time * 4.0) & 1) == 0;
            float a = blink ? 0.45f : 1f;
            DrawRect(new Rect2(x, y, ChipSize, ChipSize), new Color(0.02f, 0.02f, 0.02f, 0.8f));
            DrawRect(new Rect2(x, y, ChipSize, ChipSize), new Color(0.93f, 0.93f, 0.93f, 0.9f * a), false, 1.5f);
            var icon = MusouPowerupIcons.Of(kind);
            const float pad = 5f;
            DrawTextureRect(icon, new Rect2(x + pad, y + pad, ChipSize - 2f * pad, ChipSize - 2f * pad - 3f), false, new Color(1f, 1f, 1f, a));
            float f = total > 0f ? Math.Clamp(left / total, 0f, 1f) : 0f;
            DrawRect(new Rect2(x + 2f, y + ChipSize - 5f, (ChipSize - 4f) * f, 3f), new Color(1f, 1f, 1f, 0.95f * a));
            int k = (int)kind, secs = (int)MathF.Ceiling(left);
            if (k >= 0 && k < _buffSeconds.Length && _buffSecondsFor[k] != secs) { _buffSecondsFor[k] = secs; _buffSeconds[k] = secs.ToString(CultureInfo.InvariantCulture); }
            Txt(k >= 0 && k < _buffSeconds.Length ? _buffSeconds[k] : "", x + ChipSize / 2f, bottom + 18f, 15, new Color(0.93f, 0.93f, 0.93f, a), 0, 3, true);
            x += ChipSize + ChipGap;
        }
    }
}
