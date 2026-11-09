using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// The boss column (owner, 2026-11-10: "put your player armor and health in the bottom left, then the boss health bars above that when the bosses do spawn. prioritize showing the health of bosses being
/// actively damaged by the local player viewing their own ui"): over the own armour and health bar (<see cref="MusouHudLayout.VitalsCorner"/>), bottom left, one bar per living boss (the commander, the Boss
/// Orks) in <see cref="MusouBossBarOrder"/>'s order, the first nearest the own bar, at most <see cref="MusouBossBarOrder.Shown"/> and a "+N MORE" line for the rest. A boss the own player is damaging has a
/// bright frame and a white name. The commander's bar is red (pulsing when enraged, its Waaagh! ticks), a Boss Ork's orange. Nothing shows while no boss lives. Sizes and colours ours. Not drawn
/// with <c>--musou-old-hud</c> (the top-centre bars instead).
/// </summary>
public sealed partial class MusouHud
{
    const float ColumnW = 340f, ColumnBarH = 12f, ColumnStep = 42f;
    /// <summary>The first bar's bottom edge above the screen's bottom: over the own bar's box (its bottom at <see cref="MusouHudLayout.VitalsCorner"/>.Y, about 39 tall at its scale) with a gap.</summary>
    const float ColumnBase = 40f + 39f + 14f;

    readonly MusouBossBarOrder _bossOrder = new();
    readonly List<MusouBossBar> _bossRows = new();
    Func<ushort, double>? _hitOf;
    Func<ushort, float>? _farOf;
    string _moreText = "";
    int _moreFor = -1;

    /// <summary>This frame's living bosses and their order (called from _Process; the commander's health is the eased one).</summary>
    void OrderBosses(MusouDirectorView? d)
    {
        _bossRows.Clear();
        if (d is { } dir && dir.Phase is not (MusouPhase.Won or MusouPhase.Lost))
        {
            if (dir.BossId != 0 && dir.BossMaxHealth > 0f && _bossShown >= 0f) _bossRows.Add(new MusouBossBar(dir.BossId, Data.BossName, _bossShown, true));
            foreach (var b in Data.BossOrks) if (b.Id != dir.BossId) _bossRows.Add(new MusouBossBar(b.Id, b.Name, Math.Clamp(b.Health, 0f, 1f), false));
        }
        _hitOf ??= id => Data.HitAt.TryGetValue(id, out double t) ? t : double.NegativeInfinity;
        _farOf ??= id => Data.Distance.TryGetValue(id, out float m) ? m : float.MaxValue;
        _bossOrder.Update(_bossRows, _hitOf, _farOf, Data.Now);
        if (Data.HitAt.Count > 64) PruneHits();
    }

    readonly List<ushort> _stale = new();
    void PruneHits()
    {
        _stale.Clear();
        foreach (var (id, at) in Data.HitAt) if (Data.Now - at > 30.0) _stale.Add(id);
        foreach (ushort id in _stale) Data.HitAt.Remove(id);
    }

    void DrawBossColumn(Vector2 vs, MusouDirectorView d)
    {
        var order = _bossOrder.Order;
        if (order.Count == 0) return;
        float x = MusouHudLayout.Left, bottom = vs.Y - ColumnBase;
        int drawn = 0;
        foreach (ushort id in order)
        {
            if (drawn >= MusouBossBarOrder.Shown) break;
            MusouBossBar? row = null;
            foreach (var r in _bossRows) if (r.Id == id) { row = r; break; }
            if (row is not { } b) continue;
            DrawColumnBar(x, bottom - ColumnBarH, b, MusouBossBarOrder.Damaged(_hitOf!(id), Data.Now));
            bottom -= ColumnStep;
            drawn++;
        }
        int hidden = _bossOrder.Hidden;
        if (hidden <= 0) return;
        if (hidden != _moreFor) { _moreFor = hidden; _moreText = hidden == 1 ? "+1 MORE BOSS" : $"+{hidden} MORE BOSSES"; }
        Txt(_moreText, x, bottom - 2f, 16, Grey, -1, 3, true);
    }

    void DrawColumnBar(float x, float y, in MusouBossBar b, bool damaged)
    {
        float w = ColumnW, h = ColumnBarH, f = Math.Clamp(b.Health, 0f, 1f);
        bool rage = b.Commander && Data.BossEnraged;
        float pulse = rage ? 0.75f + 0.25f * MathF.Sin((float)_time * 8f) : 1f;
        var nameColour = damaged ? White : b.Commander ? (rage ? RedText : Gold) : Orange;
        Txt(b.Name, x, y - 6f, b.Commander ? 18 : 16, nameColour, -1, 3, true);
        if (rage) Txt("ENRAGED", x + w - 52f, y - 6f, 15, new Color(RedText, pulse), 1, 3, true);
        Txt(b.Commander ? _bossPct.Of((long)MathF.Ceiling(f * 100f), static k => $"{k}%") : $"{(int)MathF.Ceiling(f * 100f)}%", x + w, y - 6f, 15, White, 1, 3, true);
        DrawRect(new Rect2(x - 2f, y - 2f, w + 4f, h + 4f), PanelFill);
        if (b.Commander && _bossTrail > f) DrawRect(new Rect2(x + w * f, y, w * (_bossTrail - f), h), new Color(1f, 0.85f, 0.6f, 0.85f));
        var fill = b.Commander ? (rage ? new Color(1f * pulse, 0.12f, 0.04f) : RedFill) : new Color(0.95f, 0.38f, 0.04f);
        DrawRect(new Rect2(x, y, w * f, h), fill);
        if (b.Commander)
            foreach (float mark in BossMarks) DrawLine(new Vector2(x + w * mark, y - 1f), new Vector2(x + w * mark, y + h + 1f), new Color(0f, 0f, 0f, 0.85f), 2f);
        DrawRect(new Rect2(x - 2f, y - 2f, w + 4f, h + 4f), damaged ? Gold : FrameGold, false, damaged ? 2.5f : 1.5f);
    }
}
