using System;
using Godot;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// The roaming Boss Orks on the Musou HUD (owner, 2026-11-06; design docs/musou-boss-orks-design.md): a narrower bar for each one alive under the boss bar (in its place while no
/// Warboss bar shows), named by its kind ("BOSS NOB"), and the call-outs "BOSS ORK INCOMING" and "BOSS ORK SLAIN". Layout and colours ours.
/// </summary>
public sealed partial class MusouHud
{
    /// <summary>A Boss Ork came (orange flash) or fell.</summary>
    public void BossOrkIncoming() => CallOut("BOSS ORK INCOMING", Orange, 2.6, new Color(1f, 0.4f, 0.02f, 0.22f));
    public void BossOrkSlain() => CallOut("BOSS ORK SLAIN", Gold, 2.2);

    void DrawBossOrkBars(Vector2 vs, MusouDirectorView d)
    {
        if (Data.BossOrks.Count == 0 || d.Phase is MusouPhase.Won or MusouPhase.Lost) return;
        bool main = d.BossId != 0 && d.BossMaxHealth > 0f && _bossShown >= 0f;
        if (!MusouHudLayout.Old)
        {
            // The corner: bottom left, stacked upwards over the commander's bar (in its place while none shows).
            const float cw = 300f, ch = 10f;
            float x0 = MusouHudLayout.Left, bottom = main ? CornerBossTop(vs) - 40f : vs.Y - MusouHudLayout.BottomMargin;
            foreach (var bar in Data.BossOrks)
            {
                float top = bottom - ch, f = Math.Clamp(bar.Health, 0f, 1f);
                Txt(bar.Name, x0, top - 6f, 16, Orange, -1, 3, true);
                Txt($"{(int)MathF.Ceiling(f * 100f)}%", x0 + cw, top - 6f, 15, White, 1, 3, true);
                DrawRect(new Rect2(x0 - 2f, top - 2f, cw + 4f, ch + 4f), PanelFill);
                DrawRect(new Rect2(x0, top, cw * f, ch), new Color(0.95f, 0.38f, 0.04f));
                DrawRect(new Rect2(x0 - 2f, top - 2f, cw + 4f, ch + 4f), FrameGold, false, 1.5f);
                bottom -= ch + 30f;
            }
            return;
        }
        float cx = vs.X / 2f, w = Math.Min(420f, vs.X - 900f), h = 12f;
        if (w < 160f) w = 160f;
        float y = main ? 52f + 22f + 34f : 52f;
        foreach (var bar in Data.BossOrks)
        {
            float x = cx - w / 2f, f = Math.Clamp(bar.Health, 0f, 1f);
            Txt(bar.Name, cx, y - 6f, 18, Orange, 0, 3, true);
            Txt($"{(int)MathF.Ceiling(f * 100f)}%", x, y - 6f, 16, White, -1, 3, true);
            DrawRect(new Rect2(x - 2f, y - 2f, w + 4f, h + 4f), PanelFill);
            DrawRect(new Rect2(x, y, w * f, h), new Color(0.95f, 0.38f, 0.04f));
            DrawRect(new Rect2(x - 2f, y - 2f, w + 4f, h + 4f), FrameGold, false, 1.5f);
            y += h + 30f;
        }
    }
}
