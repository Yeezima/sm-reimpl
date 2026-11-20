using System;
using System.Collections.Generic;
using Godot;
using SmClient.FrontEnd;

namespace SmClient.Customizer;

/// <summary>
/// BACK with unsaved changes in the Armoury (owner, 2026-11-20: "give it the same treatment we gave to the other menu windows"): a small menu window over a dimmed Armoury, built like the Options and Warfront windows, with their
/// painted frame (<c>overlay-panel</c>, the title plate and the stone), scaled down; the title UNSAVED CHANGES on the plate; two plates of the menus' own art, side by side, equal and centred: KEEP CHANGES (the gold plate, saves
/// then leaves) and DISCARD (the dark plate, reverts then leaves). Esc or B closes it and stays. Ours (spec "Armoury buttons").
/// </summary>
public sealed partial class CreatorView
{
    /// <summary>The window's size against the full menu window (a factor of its fit to the screen), and the two plates in the frame's own v2 pixels: size, gap, and the row's top.</summary>
    const float ArmPromptScale = 0.5f, ArmPromptBtnW = 216f, ArmPromptBtnH = 38f, ArmPromptGap = 24f, ArmPromptRowY = 174f;

    sealed partial class ArmBackPrompt : Control
    {
        public Action? Cancel;
        public override void _UnhandledKeyInput(InputEvent e)
        {
            if (e is InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape }) { GetViewport().SetInputAsHandled(); Cancel?.Invoke(); }
        }
        public override void _Input(InputEvent e)
        {
            if (PadHost.IsBack(e)) { GetViewport().SetInputAsHandled(); Cancel?.Invoke(); }
        }
    }

    ArmBackPrompt? _armPromptNow;
    Control? _armPromptFocus;

    /// <summary>Whether the unsaved-changes prompt is open (test aid).</summary>
    public bool BackPromptOpen => _armPromptNow is not null && IsInstanceValid(_armPromptNow) && !_armPromptNow.IsQueuedForDeletion();

    /// <summary>Screenshot aid (<c>--arm-back-prompt</c>): makes an unsaved edit of the open custom loadout, then asks as BACK does.</summary>
    public void ShowBackPromptDemo()
    {
        if (OpenClass is { } c && !_session.Dirty) { var r = Model.LoadoutEdits.Rename(_session.Working, _open.Slot, c.Name + " 2"); if (r.Changed) _session.Edit(_ => r.Profile); }
        Back();
    }

    void OpenBackPrompt()
    {
        if (BackPromptOpen || _armCanvas is null) return;
        _armPromptFocus = GetViewport()?.GuiGetFocusOwner();
        var prompt = new ArmBackPrompt { Name = "ArmBackPrompt", MouseFilter = MouseFilterEnum.Stop };
        prompt.SetAnchorsPreset(LayoutPreset.FullRect);
        prompt.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.65f), MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });

        // The window: the shared frame at half the size a full menu window takes, centred; its own 736 x 342 v2 pixels.
        const float fw = 736f, fh = 342f;
        float sv = ArmPromptScale * Mathf.Max(0.2f, Mathf.Min(Size.Y * 0.95f / fh, Size.X * 0.96f / fw));
        var o = new Vector2((Size.X - fw * sv) / 2f, (Size.Y - fh * sv) / 2f);
        Rect2 R(Rect2 r) => new(o + r.Position * sv, r.Size * sv);
        var frame = new TextureRect { Name = "ArmBackPromptFrame", Texture = PanelFrame.Texture("overlay-panel", 0), MouseFilter = MouseFilterEnum.Stop, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, TextureFilter = TextureFilterEnum.LinearWithMipmaps, Position = o, Size = new Vector2(fw, fh) * sv };
        prompt.AddChild(frame);
        var plate = new Label { Name = "ArmBackPromptTitle" };
        ConceptPanelPage.StyleTitle(plate, "UNSAVED CHANGES");
        var t = R(ConceptPanelPage.TitleRectV2);
        plate.Position = t.Position; plate.Size = t.Size;
        ConceptPanelPage.FitTitle(plate, t.Size.X * 0.92f);
        prompt.AddChild(plate);

        // Two plates in the rows area, equal and symmetric about the frame's middle line, the menus' own art: gold (like APPLY) and dark (like REVERT).
        float s = sv * 15f / 14f;
        var fit = new List<(Control, string, float)>();
        Button Plate(string name, string text, float x, bool gold, Action press)
        {
            var r = R(new Rect2(x, ArmPromptRowY, ArmPromptBtnW, ArmPromptBtnH));
            var b = new Button { Name = name, Text = text, FocusMode = FocusModeEnum.All, TopLevel = false, Position = r.Position, Size = r.Size, CustomMinimumSize = Vector2.Zero };
            b.Pressed += () => { press(); };
            prompt.AddChild(b);
            var pad = new PaintedButtons.Pad(3, 4, 3, 4);
            PaintedButtons.Apply(b, "opt-apply-dark", "opt-apply-gold", pad, new PaintedButtons.Pad(3, 4, 3, 5), s, true, paintedText: false, goldRest: gold, noGlow: true);
            if (gold) b.AddThemeStyleboxOverride("focus", new StyleBoxFlat { BgColor = Colors.Transparent, DrawCenter = false, BorderColor = new Color(1f, 0.86f, 0.4f), BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2, CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3, ExpandMarginLeft = 0.5f * s, ExpandMarginRight = 0.5f * s, ExpandMarginTop = 1f * s, ExpandMarginBottom = 1f * s });   // the gold ring on the plate's own edge
            ConceptPanelPage.DockText(b, s, gold, goldOnFocus: !gold);
            fit.Add((b, text, (r.Size.X - b.GetThemeStylebox("normal").GetMinimumSize().X) * 0.92f));
            return b;
        }
        const float mid = fw / 2f;
        var keep = Plate("ArmKeepChanges", "KEEP CHANGES", mid - ArmPromptGap / 2f - ArmPromptBtnW, true, () =>
        {
            string? error = _session.Save();
            ClosePrompt();
            if (error is null) _onBack(); else Say("Could not save: " + error);
        });
        var discard = Plate("ArmDiscard", "DISCARD", mid + ArmPromptGap / 2f, false, () => { ClosePrompt(); _session.Revert(); ReturnFromRevertedCopy(); _onBack(); });
        PaintedButtons.FitGroup(fit, UiType.Plate);   // one size for both
        keep.FocusNeighborRight = keep.GetPathTo(discard); discard.FocusNeighborLeft = discard.GetPathTo(keep);
        keep.FocusNeighborLeft = keep.GetPathTo(keep); discard.FocusNeighborRight = discard.GetPathTo(discard);
        keep.FocusNeighborTop = keep.GetPathTo(keep); keep.FocusNeighborBottom = keep.GetPathTo(keep); discard.FocusNeighborTop = discard.GetPathTo(discard); discard.FocusNeighborBottom = discard.GetPathTo(discard);
        prompt.Cancel = ClosePrompt;   // Esc or B: stay on the page
        _armCanvas.FocusBehaviorRecursive = FocusBehaviorRecursiveEnum.Disabled;
        AddChild(prompt); _armPromptNow = prompt;
        keep.FocusLater();
    }

    void ClosePrompt()
    {
        if (_armPromptNow is not { } p) return;
        _armPromptNow = null;
        if (_armCanvas is not null) _armCanvas.FocusBehaviorRecursive = FocusBehaviorRecursiveEnum.Inherited;
        p.QueueFree();
        if (_armPromptFocus is { } f && IsInstanceValid(f) && f.IsVisibleInTree()) f.FocusLater();
    }
}
