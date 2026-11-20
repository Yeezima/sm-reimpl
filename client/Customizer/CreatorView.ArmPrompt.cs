using System;
using System.Collections.Generic;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// BACK with unsaved changes in the Armoury (owner, 2026-11-20): a small prompt on the page itself, in the page's own plates (the dark card, the gold and dark plate buttons, the one lettering), asking KEEP CHANGES
/// (saves, then leaves) or DISCARD (reverts, then leaves). Esc or B closes it and stays. No operating-system dialog and no steel dialog: those do not match the Armoury. Ours (spec "Armoury buttons").
/// </summary>
public sealed partial class CreatorView
{
    /// <summary>Concept pixels of the prompt: its card, and the two plates inside it.</summary>
    static readonly Rect2 ArmPromptCard = new(283, 164, 214, 92), ArmPromptKeep = new(10, 52, 94, 24), ArmPromptDiscard = new(110, 52, 94, 24);

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
        var (s, o) = ArmMap();
        _armPromptFocus = GetViewport()?.GuiGetFocusOwner();
        var prompt = new ArmBackPrompt { Name = "ArmBackPrompt", MouseFilter = MouseFilterEnum.Stop };
        prompt.SetAnchorsPreset(LayoutPreset.FullRect);
        prompt.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.65f), MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });
        var panel = new Panel { Name = "ArmBackPromptCard", Position = o + ArmPromptCard.Position * s, Size = ArmPromptCard.Size * s, MouseFilter = MouseFilterEnum.Stop };
        if (ConceptArt.Get("arm-back") is { } plate)   // the BACK plate's art: a clean chamfer and an opaque dark middle
            panel.AddThemeStyleboxOverride("panel", PaintedButtons.Box(plate, "arm-back", default, s, Colors.White, s));
        prompt.AddChild(panel);
        var title = new Label { Text = "UNSAVED CHANGES", MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Position = new Vector2(10f, 16f) * s, Size = new Vector2(ArmPromptCard.Size.X - 20f, 22f) * s };
        ArmLabel(title, ArmTileSize, s, new Color(0.97f, 0.82f, 0.4f)); panel.AddChild(title);
        var fit = new List<(Control, string, float)>();
        Button Plate(string name, string text, Rect2 r, bool gold, Action press)
        {
            var b = new Button { Name = name, Text = text, FocusMode = FocusModeEnum.All, Position = r.Position * s, Size = r.Size * s };
            b.Pressed += () => { press(); };
            panel.AddChild(b);
            PaintedButtons.Apply(b, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(1, 1, 1, 1), new PaintedButtons.Pad(1, 1, 1, 1), s * 0.45f, true, paintedText: false, goldRest: gold, noGlow: true, cpx: s);
            ArmText(b, ArmPlateSize, s, gold ? ArmDark : ArmLight, ArmDark);
            fit.Add((b, text, r.Size.X * s - b.GetThemeStylebox("normal").GetMinimumSize().X));
            return b;
        }
        var keep = Plate("ArmKeepChanges", "KEEP CHANGES", ArmPromptKeep, true, () =>
        {
            string? error = _session.Save();
            ClosePrompt();
            if (error is null) _onBack(); else Say("Could not save: " + error);
        });
        var discard = Plate("ArmDiscard", "DISCARD", ArmPromptDiscard, false, () => { ClosePrompt(); _session.Revert(); ReturnFromRevertedCopy(); _onBack(); });
        PaintedButtons.FitGroup(fit, ArmUnit(ArmPlateSize));   // the two plates share one size
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
