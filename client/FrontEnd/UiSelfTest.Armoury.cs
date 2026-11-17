using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmClient.Customizer.Model;
using SmContent.Customization;

namespace SmClient.FrontEnd;

// ui/concept-redesign phase 4d: the Armoury (panel 03). The concept layout is on, the class rows are the plate buttons (focus by key and pad), the weapon row and tiles open their steps,
// the step's choices are the right column's cards, CONFIRM LOADOUT saves and sets the open loadout active, BACK / Esc / B leave.
public sealed partial class UiSelfTest
{
    async Task ArmouryChecks()
    {
        if (_services.Session is not { } session || _services.Catalog is not { } catalog) return;
        var creator = _front.Creator!;
        _front.ShowCreator();
        await Frames(6);
        Check("armoury: the concept layout is on", creator.ArmouryOn);
        if (!creator.ArmouryOn) return;
        creator.OpenSelection(Selection.DefaultFor(ProfileStore.FirstRole(catalog)));
        await Frames(6);
        FindButton(creator, b => b.Text == "CLASS" && b.GetParent()?.Name == "ArmouryPills")?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
        var rows = Enumerable.Range(0, 4).Select(i => FindButton(creator, b => b.Name == "ArmClass" + i)).Where(b => b is not null).ToList();
        Check("armoury: the class list has plate rows (one per class)", rows.Count >= 3, rows.Count.ToString());
        Check("armoury: the open class is the pressed (gold) row", rows.Count(b => b!.ButtonPressed) == 1, rows.Count(b => b!.ButtonPressed).ToString());
        Check("armoury: the CONFIRM LOADOUT, BACK and tab strip exist", FindButton(creator, b => b.Name == "ConfirmLoadout") is not null && creator.FindChild("ArmouryPills", true, false) is { } pills && pills.GetChildCount() >= 3);
        // Keyboard: the focus walks the class rows.
        rows[0]!.GrabFocus(); await Frames(2);
        await SendKey(Key.Down);
        Check("armoury: Down moves the focus to the next class row", GetViewport().GuiGetFocusOwner() is Button { Name: var n } && n == rows[1]!.Name, GetViewport().GuiGetFocusOwner()?.Name ?? "none");
        await SendPad(JoyButton.DpadDown);
        Check("armoury: the pad's Down moves it on", GetViewport().GuiGetFocusOwner() is Button { Name: var n2 } && n2 == rows[2]!.Name, GetViewport().GuiGetFocusOwner()?.Name ?? "none");
        // The weapon row opens the WEAPONS step, a tile its step.
        var tile = FindButton(creator, b => b.Name == "ArmTile0");
        tile?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(4);
        Check("armoury: a tile press opens its step", creator.CurrentStep is "EQUIPMENT" or "POWERS" or "PERKS", creator.CurrentStep);
        FindButton(creator, b => b.Name == "ArmouryWeaponRow")?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(4);
        Check("armoury: the weapon row opens the WEAPONS step", creator.CurrentStep == "WEAPONS", creator.CurrentStep);
        creator.OpenStep("weapons");
        await Frames(4);
        FindButton(creator, b => b.Name == "ArmouryPickBack")?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(4);
        var slotCard = FindButton(creator, b => b.Name == "ArmSlot0");
        Check("armoury: the right column shows one card per loadout slot", slotCard is not null);
        slotCard?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(4);
        Check("armoury: a slot card opens that slot's choices", FindButton(creator, b => b is UiKit.Row) is not null && FindButton(creator, b => b.Name == "ArmouryPickBack") is not null);
        Check("armoury: the step's choices are cards (rows with the card plate) in the right column", FindButton(creator, b => b is UiKit.Row) is { } card && card.GetThemeStylebox("normal") is StyleBoxTexture { Texture: not null });
        // CONFIRM LOADOUT: saves and sets the open loadout active.
        creator.OpenSelection(Selection.DefaultFor(ProfileStore.FirstRole(catalog)));
        await Frames(4);
        FindButton(creator, b => b.Name == "ConfirmLoadout")?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(6);
        var active = LoadoutEdits.Active(session.Working, catalog, out _);
        Check("armoury: CONFIRM LOADOUT sets the open loadout active and saves it", active == Selection.DefaultFor(ProfileStore.FirstRole(catalog)) && !session.Dirty, $"{active} dirty={session.Dirty}");
        // Back: B leaves like the BACK button (nothing unsaved after the confirm).
        await SendPad(JoyButton.B);
        await Frames(4);
        Check("armoury: B leaves the Armoury", !creator.Visible);
        _front.ShowCreator();
        await Frames(4);
    }
}
