using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The war's HUD (design section G), over the match HUD as its child in the same 1080-high space and faces as <see cref="MusouHud"/>, whose run panel it replaces in the Campaign phase. In the middle: the
/// station's prompt, or what the run asks now (travel, pickup countdown, revive, extraction, the end) and the hold of P. The panels that stood top left went to the M map on 2026-11-02 (the owner's request):
/// the fronts, the HQ, the reserves and the clock are <see cref="WarMapPanel"/>'s own rows, the battle's tracker and the rest are <see cref="TfwBattlePanel"/>, fed from the same <see cref="Frame"/>. Every
/// figure comes from tags 7 and 8. Layout, sizes and colours are ours (kind: ours). <see cref="NetworkView"/> fills <see cref="Data"/> every frame and calls <see cref="Fed"/>; without that for two frames the HUD hides itself.
/// </summary>
public sealed partial class TfwHud : Control
{
    /// <summary>What the HUD shows this frame.</summary>
    public sealed class Frame
    {
        public TfwWarView? War;
        public IReadOnlyList<TfwObjective> Objectives = Array.Empty<TfwObjective>();
        /// <summary>The Orks the client knows standing in each position now (only a position being cleared is counted).</summary>
        public readonly int[] OrksOn = new int[WarRules.Positions + 1];
        public bool Alive = true;
        /// <summary>The station's prompt ("" away from one).</summary>
        public string Station = "";
        public float PickupHold;
        public int Kos;
        /// <summary>The map's points and the run's total (tag 12), and the line of keys 3 to 5.</summary>
        public uint MapPoints, RunTotal;
        public string Slots = "";
    }

    public Frame Data = new();

    const float RefHeight = 1080f;
    static readonly Color Gold = new(0.98f, 0.8f, 0.12f), RedText = new(0.9f, 0.08f, 0.07f), White = new(0.93f, 0.93f, 0.95f), PipOff = new(0.25f, 0.25f, 0.26f, 0.9f);

    FontVariation? _font, _fontBold;
    ulong _fedFrame;
    TfwWarView? _textFor;
    bool _aliveFor;
    string _prompt = "", _pickupText = "";

    /// <summary>The big line now (the tests read it).</summary>
    public string Prompt => _prompt;

    public TfwHud()
    {
        Name = "TfwHud";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        TextureFilter = TextureFilterEnum.Linear;
    }

    /// <summary>The HUD's two faces (the battle panel's too).</summary>
    internal static (FontVariation Normal, FontVariation Bold) MakeFonts()
    {
        var system = new SystemFont { FontNames = ["Bahnschrift", "DIN Alternate", "Arial Narrow"], Fallbacks = [ThemeDB.FallbackFont] };
        var ts = TextServerManager.GetPrimaryInterface();
        FontVariation Variant(int weight, int width) => new()
        {
            BaseFont = system,
            VariationOpentype = new Godot.Collections.Dictionary { { ts.NameToTag("wght"), weight }, { ts.NameToTag("wdth"), width } },
        };
        return (Variant(450, 92), Variant(600, 92));
    }

    public override void _Ready() => (_font, _fontBold) = MakeFonts();

    /// <summary>The view filled <see cref="Data"/> this frame: the texts follow, only when what they show changed.</summary>
    public void Fed()
    {
        _fedFrame = Engine.GetProcessFrames();
        if (Data.War is not { } war) return;
        if (_textFor is { } was && was == war && _aliveFor == Data.Alive) return;
        _textFor = war;
        _aliveFor = Data.Alive;
        _prompt = TfwHudModel.Prompt(war, Data.Alive);
        _pickupText = TfwHudModel.PickupKeyText(war);
    }

    public override void _Process(double delta)
    {
        if (Engine.GetProcessFrames() - _fedFrame > 2) { Visible = false; return; }
        QueueRedraw();
    }

    /// <summary>A line on <paramref name="on"/> (in its _Draw): align -1 from x, 0 centred on it, 1 ending at it.</summary>
    internal static void Text(CanvasItem on, Font? font, string s, float x, float y, int size, Color c, int align, int outline)
    {
        if (font is null || s.Length == 0) return;
        float width = font.GetStringSize(s, HorizontalAlignment.Left, -1, size).X;
        var pos = new Vector2(align < 0 ? x : align == 0 ? x - width / 2f : x - width, y);
        if (outline > 0) on.DrawStringOutline(font, pos, s, HorizontalAlignment.Left, -1, size, outline, new Color(0f, 0f, 0f, c.A * 0.8f));
        on.DrawString(font, pos, s, HorizontalAlignment.Left, -1, size, c);
    }

    void Txt(string s, float x, float y, int size, Color c, int align = -1, int outline = 3, bool bold = false) => Text(this, bold ? _fontBold : _font, s, x, y, size, c, align, outline);

    public override void _Draw()
    {
        if (_font is null || Data.War is null) return;
        float scale = Size.Y / RefHeight;
        if (scale <= 0f) return;
        DrawSetTransformMatrix(new Transform2D(0f, new Vector2(scale, scale), 0f, Vector2.Zero));
        DrawPrompts(Size / scale);
    }

    void DrawPrompts(Vector2 vs)
    {
        float cx = vs.X / 2f;
        if (!Data.Alive && Data.War is { } war && TfwHudModel.RunHoldsRespawn(war)) Txt("KILLED", cx, 330f, 52, RedText, 0, 6, true);
        if (Data.Station.Length > 0) Txt(Data.Station, cx, 640f, 30, Gold, 0, 4, true);
        else Txt(_prompt, cx, 640f, 34, _prompt.StartsWith("THE WAR IS LOST", StringComparison.Ordinal) ? RedText : Gold, 0, 5, true);
        if (Data.PickupHold <= 0f) return;
        const float w = 260f, y = 672f;
        Txt(_pickupText, cx, y - 6f, 18, White, 0, 3, true);
        DrawRect(new Rect2(cx - w / 2f, y, w, 8f), PipOff);
        DrawRect(new Rect2(cx - w / 2f, y, w * Math.Clamp(Data.PickupHold, 0f, 1f), 8f), Gold);
    }
}
