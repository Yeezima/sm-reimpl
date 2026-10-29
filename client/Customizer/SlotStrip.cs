using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer.Model;
using SmContent;
using SmContent.Customization;

namespace SmClient.Customizer;

/// <summary>
/// The loadout chips (docs/customizer-design.md section 3): the three DEFAULTS loadouts and CUSTOM 1 to 10, each with its name and class and the words
/// ACTIVE / UNSAVED / EMPTY (the ACTIVE one also a star). Used by the Join screen and the creator. The strip only reports clicks; the screen decides what a click does.
/// </summary>
public sealed partial class SlotStrip : VBoxContainer
{
    readonly CustomizerServices _services;
    readonly bool _showDefaults;
    readonly List<(Selection Selection, Button Chip)> _chips = new();
    readonly int _chipWidth;

    /// <summary>A chip was chosen (click, Enter or a digit key).</summary>
    public Action<Selection>? Picked;
    /// <summary>The selection the screen has open (marked with a gold frame); null: none.</summary>
    public Selection? Open { get; set; }

    readonly bool _compact;
    /// <summary>Compact (creator): one row, one DEFAULTS chip for the role of <see cref="DefaultsRole"/> (the creator's tabs choose it) followed by CUSTOM 1 to 10.</summary>
    public string? DefaultsRole { get; set; }

    public SlotStrip(CustomizerServices services, bool showDefaults, int chipWidth = 160, bool compact = false)
    {
        _services = services; _showDefaults = showDefaults; _chipWidth = chipWidth; _compact = compact;
        AddThemeConstantOverride("separation", 8);
    }

    public IReadOnlyList<Button> Chips => _chips.Select(c => c.Chip).ToList();
    public Button? ChipOf(Selection selection) => _chips.FirstOrDefault(c => c.Selection == selection).Chip;

    /// <summary>Rebuilds the chips from the session's working profile.</summary>
    public void Refresh()
    {
        var session = _services.Session;
        var catalog = _services.Catalog;
        if (session is null || catalog is null) return;
        Selection? focusedSelection = _chips.FirstOrDefault(c => c.Chip.HasFocus()).Selection;
        bool hadFocus = _chips.Any(c => c.Chip.HasFocus());
        foreach (var child in GetChildren()) { RemoveChild(child); child.QueueFree(); }
        _chips.Clear();
        var profile = session.Working;
        var active = LoadoutEdits.Active(profile, catalog, out _);
        var saved = session.Saved;

        HBoxContainer? single = null;
        if (_compact)
        {
            single = UiKit.HBox(8);
            // Only a role the profile offers (ui/customizer-ork: no Ork DEFAULTS chip in Faithful).
            string role = DefaultsRole is { } wanted && _services.Roles().Contains(wanted, StringComparer.OrdinalIgnoreCase) ? wanted : ProfileStore.FirstRole(catalog);
            var sel = Selection.DefaultFor(role);
            bool activeDefaults = active.Kind == SelectionKind.Default && active.Role?.Equals(role, StringComparison.OrdinalIgnoreCase) == true;
            single.AddChild(MakeChip(sel, "DEFAULTS", LoadoutRows.ClassName(catalog, role, CustomizationCatalog.SpaceMarine), activeDefaults, false, null, _chipWidth));
        }
        else if (_showDefaults)
        {
            var row = UiKit.HBox(8);
            row.AddChild(Caption("DEFAULTS"));
            var roles = _services.Roles();   // the Ork Boy's DEFAULTS only in a profile with the Orks (ui/customizer-ork)
            int width = roles.Count > 3 ? 320 : 430;   // four chips share the row's 1290 pixels
            foreach (var role in roles)
            {
                var sel = Selection.DefaultFor(role);
                string name = OrkLoadout.IsOrk(role) ? $"{OrkLoadout.ClassName} (either side)"
                    : LoadoutRows.ClassName(catalog, role, CustomizationCatalog.SpaceMarine) + " / " + LoadoutRows.ClassName(catalog, role, CustomizationCatalog.Chaos);
                var defaultsChip = MakeChip(sel, "DEFAULTS", name, active == sel, false, null, width);
                defaultsChip.TooltipText = defaultsChip.TooltipText.Length > 0 ? $"{name} - {defaultsChip.TooltipText}" : name;
                row.AddChild(defaultsChip);
            }
            AddChild(row);
        }
        var custom = single ?? UiKit.HBox(8);
        if (single is null) custom.AddChild(Caption("CUSTOM"));
        for (int slot = 1; slot <= CustomizationProfile.MaxCustomClasses; slot++)
        {
            var sel = Selection.CustomSlot(slot);
            var c = profile.ClassAt(slot);
            bool unsaved = ProfileSignature(c) != ProfileSignature(saved.ClassAt(slot)) || (c is not null && c.Appearance != (saved.ClassAt(slot)?.Appearance ?? c.Appearance)) || (c is not null && AppearanceChanged(profile, saved, c));
            string name = c is null ? "Empty" : c.Name;
            bool unavailable = c is not null && LoadoutAvailability.Unavailable(c, _services.Content);   // added content E2: kept in the profile, not played in this profile
            string detail = c is null ? "" : unavailable ? "unavailable here" : LoadoutRows.ClassName(catalog, c.Role, CustomizationCatalog.SpaceMarine);
            var slotChip = MakeChip(sel, $"{slot}", name, active == sel, unsaved, c is null ? null : detail, _chipWidth);
            if (unavailable) slotChip.TooltipText = LoadoutAvailability.Reason(c!, _services.Content) + " It is kept; you join with the side's default class.";
            custom.AddChild(slotChip);
        }
        AddChild(custom);
        if (hadFocus && focusedSelection is { } fs && ChipOf(fs) is { } chip) chip.FocusLater();
        ApplyOpen();
    }

    static Label Caption(string text)
    {
        var l = UiKit.Text(text, UiKit.CaptionSize, UiKit.Grey);
        l.CustomMinimumSize = new Vector2(104, 0);
        l.VerticalAlignment = VerticalAlignment.Center;
        return l;
    }

    static string ProfileSignature(CustomClass? c) => c is null ? "" : $"{c.Name}|{c.Role}|{c.Primary}|{c.Pistol}|{c.Melee}|{c.Equipment}|{string.Join(",", c.Perks)}";

    static bool AppearanceChanged(CustomizationProfile now, CustomizationProfile saved, CustomClass c)
    {
        foreach (var race in new[] { CustomizationCatalog.SpaceMarine, CustomizationCatalog.Chaos })
        {
            var a = now.Appearance(c.Appearance.For(race));
            var b = saved.Appearance(c.Appearance.For(race));
            if ((a is null) != (b is null)) return true;
            if (a is not null && b is not null && ProfileStore.Serialize(now with { Appearances = [a], Classes = [] }) != ProfileStore.Serialize(saved with { Appearances = [b], Classes = [] })) return true;
        }
        return false;
    }

    Button MakeChip(Selection selection, string number, string name, bool active, bool unsaved, string? detail, int width)
    {
        var tags = new List<string>();
        if (active) tags.Add("ACTIVE");
        if (unsaved) tags.Add("UNSAVED");
        if (detail is null && selection.Kind == SelectionKind.Custom) tags.Add("EMPTY");
        string head = selection.Kind == SelectionKind.Custom ? $"CUSTOM {number}" : number;
        var chip = new Button
        {
            Text = $"{head}\n{Trim(width > 200 ? name : Short(name), width > 200 ? 46 : 15)}" + (detail is null ? "" : $"\n{Trim(width > 200 ? detail : Short(detail), width > 200 ? 46 : 15)}"),
            Alignment = HorizontalAlignment.Left,
            FocusMode = Control.FocusModeEnum.All,
            CustomMinimumSize = new Vector2(width, _compact ? 84 : 96),
            ToggleMode = false,
            TooltipText = tags.Count > 0 ? string.Join(" - ", tags) : "",
            ClipText = true,
        };
        chip.AddThemeFontSizeOverride("font_size", _compact ? 17 : UiKit.CaptionSize);
        var border = active ? UiKit.Gold : detail is null && selection.Kind == SelectionKind.Custom ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.55f, 0.45f, 0.1f);
        var normal = UiKit.Box(new Color(0.06f, 0.06f, 0.06f, 0.92f), border, active ? 3 : 2, 8);
        normal.ContentMarginTop = 28;
        chip.AddThemeStyleboxOverride("normal", normal);
        var hover = UiKit.Box(UiKit.Hover, UiKit.Gold, 2, 8);
        hover.ContentMarginTop = 28;
        chip.AddThemeStyleboxOverride("hover", hover);
        var pressed = UiKit.Box(UiKit.Hover, UiKit.Gold, 3, 8);
        pressed.ContentMarginTop = 28;
        chip.AddThemeStyleboxOverride("pressed", pressed);
        // The state words sit on the first line at the right; the star marks the active chip.
        if (tags.Count > 0)
        {
            var label = UiKit.Text(string.Join("  ", tags), 16, active ? UiKit.Gold : unsaved ? UiKit.LightRed : UiKit.Grey);
            label.SetAnchorsPreset(Control.LayoutPreset.TopWide);
            label.OffsetLeft = active ? 34 : 10;
            label.OffsetRight = -6;
            label.OffsetTop = 3;
            label.ClipText = true;   // "ACTIVE  UNSAVED" on a narrow chip stays inside it (the full words are in the chip's tooltip)
            chip.AddChild(label);
        }
        if (active) chip.AddChild(new UiKit.Glyph(UiKit.Glyph.Kind.Star, UiKit.Gold) { Position = new Vector2(8, 4), Size = new Vector2(20, 20), CustomMinimumSize = new Vector2(20, 20) });
        chip.Pressed += () => Picked?.Invoke(selection);
        _chips.Add((selection, chip));
        return chip;
    }

    static string Short(string text) => text.Replace(" Marine", "");

    static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + ".";

    /// <summary>Marks the open selection's chip with a heavier gold frame.</summary>
    void ApplyOpen()
    {
        foreach (var (selection, chip) in _chips)
        {
            bool open = Open is { } o && o == selection;
            var box = (StyleBoxFlat)chip.GetThemeStylebox("normal");
            var copy = (StyleBoxFlat)box.Duplicate();
            if (open)
            {
                copy.BorderColor = UiKit.White;
                copy.SetBorderWidthAll(4);
                copy.BgColor = new Color(0.14f, 0.12f, 0.04f, 0.95f);
            }
            chip.AddThemeStyleboxOverride("normal", copy);
        }
    }

    public void SetOpen(Selection? selection)
    {
        Open = selection;
        ApplyOpen();
    }

    /// <summary>Digits 1 to 9 and 0 (slot 10) jump to a slot when the strip has the focus.</summary>
    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } key || !_chips.Any(c => c.Chip.HasFocus())) return;
        int slot = key.PhysicalKeycode switch
        {
            >= Key.Key1 and <= Key.Key9 => (int)key.PhysicalKeycode - (int)Key.Key0,
            Key.Key0 => 10,
            _ => 0,
        };
        if (slot == 0 || ChipOf(Selection.CustomSlot(slot)) is not { } chip) return;
        chip.GrabFocus();
        GetViewport().SetInputAsHandled();
    }
}
