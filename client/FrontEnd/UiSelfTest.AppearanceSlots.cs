using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmClient.Customizer.Model;
using SmContent;
using SmContent.Customization;

namespace SmClient.FrontEnd;

// ui/appearance-slots (the owner's request 2026-11-06): MY APPEARANCES in the appearance editor, ten saved looks per side that any loadout can wear. Driven through the screen's own
// rows, dialogs and the pad (LB to the section, B out of a slot's page). Leaves the profile as it found it (in memory; nothing is saved). --appearance-slots-shot <file.png>
// (windowed run, screenshot aid) saves <file>_list.png, <file>_page.png and <file>_chaos.png.
public sealed partial class UiSelfTest
{
    static readonly Regex SlotRowText = new(@"^(\d+)\.  ", RegexOptions.CultureInvariant);

    async Task SlotShot(string? prefix, string name)
    {
        if (prefix is null) return;
        await Frames(90);   // the preview's model and paint load
        string file = System.IO.Path.ChangeExtension(prefix, null) + "_" + name + ".png";
        GetViewport().GetTexture().GetImage().SavePng(file);
        GD.Print("appearance slots screenshot -> " + file);
    }

    async Task AppearanceSlotChecks()
    {
        var session = _services.Session!;
        var catalog = _services.Catalog!;
        var kept = session.Working;
        string? shot = ShotArgument("--appearance-slots-shot");
        const string sm = CustomizationCatalog.SpaceMarine;
        try
        {
            // Three more loadouts to apply looks to, beside slot 4 (whose look the steps above made).
            string? own = session.Working.ClassAt(4)?.Appearance.SpaceMarine;
            Check("slots: loadout 4 wears a look of its own", own is not null && session.Working.Appearance(own) is not null);
            if (own is null) return;
            session.Edit(p => LoadoutEdits.Create(p, 1, "tactical", catalog).Profile);
            session.Edit(p => LoadoutEdits.Create(p, 2, "heavy", catalog).Profile);
            session.Edit(p => LoadoutEdits.Create(p, 6, "assault", catalog).Profile);
            var creator = _front.Creator!;
            _front.ShowCreator();
            creator.OpenSelection(Selection.CustomSlot(4));
            await Frames(3);
            _front.ShowAppearance();
            await Frames(6);
            var appearance = _front.Appearance!;
            appearance.OpenSection("chapter");
            await Frames(3);

            // The pad: LB from CHAPTER goes to MY APPEARANCES (the first section).
            await SendPad(JoyButton.LeftShoulder);
            await Frames(3);
            var rows = Rows(appearance);
            Check("slots: LB from CHAPTER opens MY APPEARANCES with ten slot rows", rows.Count == AppearanceSlots.Count, $"{rows.Count} rows");
            int ownSlot = AppearanceSlots.SlotOf(session.Working, sm, own);
            var ownRow = rows.FirstOrDefault(r => SlotNumber(r) == ownSlot);
            Check("slots: the loadout's own look is in its slot, marked as worn", ownSlot >= 1 && ownRow is { IsSelected: true }, $"slot {ownSlot}");
            Check("slots: the focus starts on the worn slot", GetViewport().GuiGetFocusOwner() == ownRow);
            Check("slots: the header names the slot", HeaderText(appearance).Contains($"slot {ownSlot}"));

            // Enter on an empty slot saves this loadout's look there and wears it.
            var empty = rows.First(r => r.Text.EndsWith("(empty)", StringComparison.Ordinal));
            int saved = SlotNumber(empty);
            empty.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(4);
            var copyId = session.Working.ClassAt(4)?.Appearance.SpaceMarine;
            var copy = session.Working.Appearance(copyId);
            var original = session.Working.Appearance(own);
            Check("slots: an empty slot takes a copy of the loadout's look and the loadout wears it", copy is not null && copyId != own && AppearanceSlots.SlotOf(session.Working, sm, copyId) == saved
                && original is not null && copy.Paint.Count == original.Paint.Count && copy.PresetApplied == original.PresetApplied, $"slot {saved}, id {copyId}");
            session.Edit(p => AppearanceEdits.ReplaceAppearance(p, AppearanceEdits.Rename(p.Appearance(copyId)!, "Ultramarine squad")));
            await Frames(2);

            // The slot's page: apply to one loadout, to all, take it off, undo.
            appearance.OpenSlot(saved);
            await Frames(4);
            var row1 = FindButton(appearance, b => b is UiKit.Row && b.Text.StartsWith("Loadout 1 ", StringComparison.Ordinal));
            Check("slots: the page lists the loadouts", row1 is not null && FindButton(appearance, b => b is UiKit.Row && b.Text.StartsWith("Loadout 6 ", StringComparison.Ordinal)) is not null);
            row1?.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(3);
            Check("slots: Enter on a loadout row puts the slot on it", session.Working.ClassAt(1)?.Appearance.SpaceMarine == copyId);
            await SlotShot(shot, "page");
            Click(b => b is UiKit.Row && b.Text == "Apply to all loadouts", appearance);
            await Frames(3);
            Check("slots: Apply to all loadouts dresses every loadout and leaves the Chaos looks", session.Working.Classes.All(c => c.Appearance.SpaceMarine == copyId)
                && session.Working.Classes.All(c => c.Appearance.Chaos == kept.ClassAt(c.Slot)?.Appearance.Chaos));
            Click(b => b is UiKit.Row && b.Text.StartsWith("Loadout 6 ", StringComparison.Ordinal), appearance);
            await Frames(3);
            Check("slots: Enter again takes it off (the default look)", session.Working.ClassAt(6)?.Appearance.SpaceMarine is null);
            Click(b => b.Text == "Undo", appearance);
            await Frames(3);
            Check("slots: Undo puts it back", session.Working.ClassAt(6)?.Appearance.SpaceMarine == copyId);
            Check("slots: the header says the look is shared, its tooltip by which loadouts", HeaderText(appearance).Contains($"slot {saved}, shared with 3") && HeaderTip(appearance).Contains("also worn by loadouts 1, 2 and 6"), HeaderText(appearance) + " | " + HeaderTip(appearance));

            // B leaves the page for the list, not the editor.
            await SendPad(JoyButton.B);
            Check("slots: B on a slot's page goes back to the ten slots", appearance.Visible && Rows(appearance).Count == AppearanceSlots.Count && FindButton(appearance, b => b.Text == "Apply to all loadouts") is null);
            await SlotShot(shot, "list");

            // Save over a filled slot: asks, then the old slot holds this look and the loadout wears it.
            appearance.OpenSlot(ownSlot);
            await Frames(3);
            session.Edit(p => AppearanceEdits.ReplaceAppearance(p, AppearanceEdits.ResetPaint(p.Appearance(copyId)!)));   // make the two differ
            await Frames(3);
            Click(b => b is UiKit.Row && b.Text == "Save this loadout's look here", appearance);
            await Frames(3);
            Check("slots: saving over a filled slot asks first", OpenDialog() is not null);
            ClickDialog("Save over");
            await Frames(3);
            Check("slots: Save over copies the look in and the loadout wears that slot", session.Working.ClassAt(4)?.Appearance.SpaceMarine == own && session.Working.Appearance(own)?.Paint.Count == 0
                && session.Working.Appearance(own)?.Name == original!.Name);

            // Rename and delete through the page's dialogs.
            appearance.OpenSlot(saved);
            await Frames(3);
            Click(b => b is UiKit.Row && b.Text == "Rename", appearance);
            await Frames(3);
            if (OpenDialog() is { } prompt && FindEdit(prompt) is { } edit) edit.Text = "Second Company";
            ClickDialog("Rename");
            await Frames(3);
            Check("slots: Rename names the slot", session.Working.Appearance(copyId)?.Name == "Second Company");
            Click(b => b is UiKit.Row && b.Text == "Delete", appearance);
            await Frames(3);
            ClickDialog("Delete");
            await Frames(3);
            Check("slots: Delete empties the slot; its loadouts wear the default look", session.Working.Appearance(copyId) is null && session.Working.ClassAt(1)?.Appearance.SpaceMarine is null
                && Rows(appearance).Count == AppearanceSlots.Count && Rows(appearance).Any(r => SlotNumber(r) == saved && r.Text.EndsWith("(empty)", StringComparison.Ordinal)));

            // Each side has its own ten.
            if (catalog.Race(CustomizationCatalog.Chaos) is not null)
            {
                appearance.Open(4, CustomizationCatalog.Chaos, "slots");
                await Frames(4);
                var chaosRows = Rows(appearance);
                Check("slots: the Chaos side has ten slots of its own", chaosRows.Count == AppearanceSlots.Count && AppearanceSlots.Layout(session.Working, CustomizationCatalog.Chaos).All(a => a is null || a.Race == CustomizationCatalog.Chaos)
                    && AppearanceSlots.Layout(session.Working, CustomizationCatalog.Chaos).All(a => a?.Id != copyId && a?.Id != own));
                chaosRows.First(r => r.Text.EndsWith("(empty)", StringComparison.Ordinal)).EmitSignal(BaseButton.SignalName.Pressed);
                await Frames(3);
                var chaosId = session.Working.ClassAt(4)?.Appearance.Chaos;
                Check("slots: a Chaos slot saves a Chaos look", session.Working.Appearance(chaosId)?.Race == CustomizationCatalog.Chaos && session.Working.ClassAt(4)?.Appearance.SpaceMarine == own);
                await SlotShot(shot, "chaos");
            }
        }
        finally
        {
            session.Seed(kept);
            _front.Appearance?.Open(4, sm, "chapter");
            _front.ShowCreator();
            await Frames(3);
        }
    }

    static int SlotNumber(Button row) => SlotRowText.Match(row.Text) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;

    static System.Collections.Generic.List<UiKit.Row> Rows(Node root)
    {
        var found = new System.Collections.Generic.List<UiKit.Row>();
        void Walk(Node n)
        {
            if (n is UiKit.Row r && r.IsVisibleInTree() && SlotRowText.IsMatch(r.Text)) found.Add(r);
            foreach (var c in n.GetChildren()) Walk(c);
        }
        Walk(root);
        return found;
    }

    static string HeaderText(Node root) => HeaderLabel(root)?.Text ?? "";
    static string HeaderTip(Node root) => HeaderLabel(root)?.TooltipText ?? "";

    /// <summary>The appearance editor's loadout line (right-aligned, cut at the edge, "Loadout N ...").</summary>
    static Label? HeaderLabel(Node root)
    {
        foreach (var c in root.GetChildren())
        {
            if (c is Label { ClipText: true, HorizontalAlignment: HorizontalAlignment.Right } l && l.Text.StartsWith("Loadout ", StringComparison.Ordinal)) return l;
            if (HeaderLabel(c) is { } found) return found;
        }
        return null;
    }
}
