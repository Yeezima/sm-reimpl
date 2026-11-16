using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer.Model;
using SmContent;
using SmContent.Customization;

namespace SmClient.Customizer;

// ui/appearance-slots (the owner's request 2026-11-06): MY APPEARANCES, the ten saved appearances of the side shown (AppearanceSlots). The list shows each slot with the loadouts
// that wear it; Enter on an empty slot saves this loadout's look there, Enter on a filled one opens its page: wear it here, save this loadout's look over it, apply it to every
// loadout or to single loadouts (one row per loadout, Enter puts it on or takes it off), rename, delete. B / Esc on a page goes back to the list. Every change is undoable.
public sealed partial class AppearanceView
{
    /// <summary>The look in a slot of the side shown (1..10), or null.</summary>
    AppearanceProfile? SlotLook(int slot) => slot is >= 1 and <= AppearanceSlots.Count ? AppearanceSlots.Layout(_session.Working, _race)[slot - 1] : null;

    /// <summary>MY APPEARANCES with a slot's page open (selftest and screenshots).</summary>
    public void OpenSlot(int slot)
    {
        _section = Section.Slots;
        _slotPage = SlotLook(slot) is null ? 0 : slot;
        Rebuild(true);
    }

    void CloseSlotPage()
    {
        _slotPage = 0;
        Rebuild(true);
    }

    /// <summary>
    /// The header's slot note: "  -  slot 2, shared with 3" when other loadouts wear this loadout's look (an edit reaches them too); the tooltip names them ("also worn by
    /// loadouts 1, 2 and 6"). Empty for the default look.
    /// </summary>
    (string Short, string Long) SharedNote()
    {
        if (Class?.Appearance.For(_race) is not { } id) return ("", "");
        var others = AppearanceSlots.WornBy(_session.Working, _race, id).Where(s => s != _slot).ToList();
        string slot = $"  -  slot {AppearanceSlots.SlotOf(_session.Working, _race, id)}";
        return others.Count == 0 ? (slot, slot) : ($"{slot}, shared with {others.Count}", $"{slot}, also worn by {AppearanceSlots.LoadoutList(others)}");
    }

    /// <summary>A profile-wide edit (wear, save into, apply, rename, delete), remembered for Undo.</summary>
    void ApplyProfile(Func<CustomizationProfile, CustomizationProfile> edit, string? message = null)
    {
        var before = _session.Working;
        _session.Edit(edit);
        if (!ReferenceEquals(before, _session.Working)) { _undo.Push(before); TrimUndo(); }
        if (message is not null) Say(message);
    }

    /// <summary>Keeps the newest 50 of at most 60 undo steps.</summary>
    void TrimUndo()
    {
        if (_undo.Count <= 60) return;
        var keep = _undo.ToArray().Take(50).Reverse().ToArray();
        _undo.Clear();
        foreach (var k in keep) _undo.Push(k);
    }

    /// <summary>Shows a look on a loadout's class in the preview, without changing anything (focus preview).</summary>
    void PreviewOn(CustomClass? who, AppearanceProfile look)
    {
        if (_clock < _noPreviewUntil) return;
        _previewEndAt = -1;
        _previewing = true;
        _preview.SetSubject(new PreviewSubject(_race, who?.Role ?? Role, who ?? Class, look));
    }

    string SideWord => _race == CustomizationCatalog.Chaos ? "Chaos" : "Space Marine";

    void BuildSlots(AppearanceProfile look, List<Control> focusables)
    {
        if (_slotPage > 0 && SlotLook(_slotPage) is { } open) { BuildSlotPage(_slotPage, open, look, focusables); return; }
        _slotPage = 0;
        var profile = _session.Working;
        var layout = AppearanceSlots.Layout(profile, _race);
        string? mine = Class?.Appearance.For(_race);
        _panel.AddChild(Note($"Ten saved {SideWord} appearances. Any loadout can wear one; a change to it shows on every loadout that wears it. Enter on an empty slot saves this loadout's look there."));
        _panel.AddChild(Heading($"{SideWord.ToUpperInvariant()} APPEARANCES"));
        for (int i = 0; i < layout.Length; i++)
        {
            int slot = i + 1;
            var saved = layout[i];
            UiKit.Row row;
            if (saved is null)
            {
                row = new UiKit.Row($"{slot}.  (empty)", [], false, true)
                {
                    Focused = () => { ShowTextCard($"Slot {slot}", "Empty.", "Enter saves this loadout's look here; this loadout then wears it."); PreviewEnd(); },
                };
                row.Pressed += () => SaveHere(slot, look, null);
            }
            else
            {
                var wearers = AppearanceSlots.WornBy(profile, _race, saved.Id);
                bool worn = saved.Id == mine;
                var tags = new List<string>();
                if (worn) tags.Add("THIS LOADOUT");
                int others = wearers.Count(s => s != _slot);
                if (others > 0) tags.Add(worn ? $"+{others} MORE" : $"{others} LOADOUT{(others == 1 ? "" : "S")}");
                var a = saved;
                row = new UiKit.Row($"{slot}.  {saved.Name}", tags, worn, true, worn ? UiKit.Glyph.Kind.Check : null)
                {
                    Focused = () => { ShowSlotCard(slot, a); PreviewOn(null, a); },
                    Unfocused = PreviewEnd,
                };
                row.Pressed += () => { _slotPage = slot; Rebuild(true); };
            }
            _panel.AddChild(row);
            focusables.Add(row);
        }
    }

    void ShowSlotCard(int slot, AppearanceProfile saved)
    {
        var wearers = AppearanceSlots.WornBy(_session.Working, _race, saved.Id);
        string worn = wearers.Count == 0 ? "Worn by no loadout." : $"Worn by {AppearanceSlots.LoadoutList(wearers)}.";
        string set = saved.PredefinedSet is { } s ? $"\nArmour set: {Race?.Set(s)?.DisplayName ?? s}" : "";
        string chapter = saved.PresetApplied is { } p ? $"\nPaint from: {Race?.Presets.FirstOrDefault(x => x.Name == p)?.DisplayName ?? p}" : "";
        ShowTextCard($"Slot {slot}: {saved.Name}", worn + set + chapter, "Enter opens the slot: wear it, apply it to loadouts, rename or delete it.");
    }

    void SaveHere(int slot, AppearanceProfile look, AppearanceProfile? over)
    {
        if (Class is null) return;
        void Save() => ApplyProfile(p => AppearanceSlots.SaveInto(p, _race, slot, look, _slot).Profile, over is null ? $"This loadout's look is saved in slot {slot} and worn here." : $"Saved over slot {slot}.");
        if (over is null) { Save(); return; }
        var others = AppearanceSlots.WornBy(_session.Working, _race, over.Id).Where(s => s != _slot).ToList();
        string reach = others.Count > 0 ? $" {char.ToUpperInvariant(AppearanceSlots.LoadoutList(others)[0])}{AppearanceSlots.LoadoutList(others)[1..]} wear{(others.Count == 1 ? "s" : "")} it and change{(others.Count == 1 ? "s" : "")} too." : "";
        UiKit.Dialog.Open(this, Host, $"Save over slot {slot}?", $"\"{over.Name}\" takes this loadout's look; this loadout then wears slot {slot}.{reach}", ["Save over", "Cancel"], i => { if (i == 0) Save(); });
    }

    void BuildSlotPage(int slot, AppearanceProfile saved, AppearanceProfile look, List<Control> focusables)
    {
        var profile = _session.Working;
        string id = saved.Id;
        bool wornHere = Class?.Appearance.For(_race) == id;
        _panel.AddChild(Heading($"SLOT {slot}: {saved.Name.ToUpperInvariant()}"));
        _panel.AddChild(Note("B / Esc: back to the ten slots."));

        void Add(UiKit.Row row) { _panel.AddChild(row); focusables.Add(row); }

        var wear = new UiKit.Row(wornHere ? "Worn on this loadout" : "Wear it on this loadout", wornHere ? ["WORN"] : [], wornHere, Class is not null, wornHere ? UiKit.Glyph.Kind.Check : null)
        {
            Focused = () => { ShowTextCard("Wear it", $"Loadout {_slot} wears slot {slot}.", "Its own look stays in its slot."); PreviewOn(null, saved); },
            Unfocused = PreviewEnd,
        };
        wear.Pressed += () => { if (!wornHere) ApplyProfile(p => AppearanceEdits.Assign(p, _slot, _race, id), $"Loadout {_slot} wears slot {slot}."); };
        Add(wear);

        var over = new UiKit.Row("Save this loadout's look here", [], false, Class is not null && !wornHere)
        {
            Focused = () => { ShowTextCard("Save this loadout's look here", wornHere ? "This loadout already wears this slot: its edits are saved in it as you make them." : $"Slot {slot} takes the look of loadout {_slot}.", "Every loadout that wears the slot changes with it."); if (!wornHere) PreviewOn(null, look); },
            Unfocused = PreviewEnd,
        };
        over.Pressed += () => { if (wornHere) Say("This loadout already wears this slot."); else SaveHere(slot, look, saved); };
        Add(over);

        var all = new UiKit.Row("Apply to all loadouts", [], false, profile.Classes.Count > 0)
        {
            Focused = () => { ShowTextCard("Apply to all loadouts", $"Every custom loadout wears slot {slot} on the {SideWord} side.", "Their other side's look stays."); PreviewOn(null, saved); },
            Unfocused = PreviewEnd,
        };
        all.Pressed += () => ApplyProfile(p => AppearanceSlots.ApplyToAll(p, _race, id), $"All {profile.Classes.Count} loadouts wear slot {slot}.");
        Add(all);

        _panel.AddChild(Heading("LOADOUTS (Enter puts it on or takes it off)"));
        foreach (var c in profile.Classes.OrderBy(c => c.Slot))
        {
            var who = c;
            bool wears = c.Appearance.For(_race) == id;
            var row = new UiKit.Row($"Loadout {c.Slot}  \"{c.Name}\"  -  {LoadoutRows.ClassName(_catalog, c.Role, _race)}", wears ? ["WEARS IT"] : [], wears, true, wears ? UiKit.Glyph.Kind.Check : null)
            {
                Focused = () => { ShowTextCard($"Loadout {who.Slot}: {who.Name}", wears ? $"Wears slot {slot}. Enter takes it off (the default look)." : $"Enter puts slot {slot} on it.", ""); PreviewOn(who, saved); },
                Unfocused = PreviewEnd,
            };
            row.Pressed += () => ApplyProfile(p => AppearanceSlots.SetWorn(p, who.Slot, _race, id, !wears), wears ? $"Loadout {who.Slot} wears the default look." : $"Loadout {who.Slot} wears slot {slot}.");
            Add(row);
        }

        _panel.AddChild(Heading("THIS SLOT"));
        var rename = new UiKit.Row("Rename", [], false, true) { Focused = () => ShowTextCard("Rename", $"Up to {LoadoutEdits.MaxNameLength} characters.") };
        rename.Pressed += () => UiKit.Dialog.Prompt(this, Host, $"Rename slot {slot}", $"Up to {LoadoutEdits.MaxNameLength} characters.", saved.Name, ["Rename", "Cancel"], (i, text) =>
        {
            if (i == 0) ApplyProfile(p => p.Appearance(id) is { } a ? AppearanceEdits.ReplaceAppearance(p, AppearanceEdits.Rename(a, text)) : p, "Renamed.");
        });
        Add(rename);
        var delete = new UiKit.Row("Delete", [], false, true) { Focused = () => ShowTextCard("Delete", "Empties the slot. Loadouts that wear it go back to the default look.") };
        delete.Pressed += () => UiKit.Dialog.Open(this, Host, $"Delete slot {slot}?", $"\"{saved.Name}\" is removed. Loadouts that wear it go back to the default look.", ["Delete", "Cancel"], i =>
        {
            if (i != 0) return;
            _slotPage = 0;
            ApplyProfile(p => AppearanceEdits.RemoveAppearance(p, id), $"Slot {slot} is empty.");
        }, titleColour: UiKit.LightRed);
        Add(delete);
    }
}
