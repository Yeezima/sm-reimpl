using System;
using Godot;

namespace SmClient.Customizer;

public static partial class UiKit
{
    /// <summary>A steel button (ui/concept-redesign): a dark-metal horizontal button, gold while it has the focus or is selected. <paramref name="name"/> keeps a node name the tests and test aids know.</summary>
    public static Button SteelButton(string text, Action? onPress, string? tooltip = null, string? name = null, bool tab = false, int minHeight = 0)
    {
        var b = new Button
        {
            Text = text.ToUpperInvariant(), ThemeTypeVariation = tab ? Steel.TabType : Steel.ButtonType, Alignment = HorizontalAlignment.Center, FocusMode = Control.FocusModeEnum.All,
            TooltipText = tooltip ?? "", Name = name ?? "Steel" + text.Replace(" ", "").Replace("'", ""), CustomMinimumSize = new Vector2(0f, minHeight), ClipText = true,
        };
        if (onPress is not null) b.Pressed += onPress;
        return b;
    }

    /// <summary>The engraved line under the main menu's frame: a dark plate with tracked small capitals (the concept's nameplate).</summary>
    public static Label Nameplate(string text)
    {
        var l = new Label { Name = "Nameplate", Text = text.ToUpperInvariant(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore, ClipText = true };
        l.AddThemeFontOverride("font", new FontVariation { BaseFont = BundledFont(), SpacingGlyph = 2 });
        l.AddThemeFontSizeOverride("font_size", Steel.SmallSize);
        l.AddThemeColorOverride("font_color", new Color(0.78f, 0.76f, 0.70f));
        if (ConceptArt.Patch("nameplate", new Vector2(20f, 12f), new Vector2(14f, 4f)) is { } art)
        {
            l.AddThemeStyleboxOverride("normal", art);
            l.AddThemeColorOverride("font_color", Colors.Transparent);   // the words are in the painted plate (fixed text)
        }
        else l.AddThemeStyleboxOverride("normal", new Steel.SteelStyle(Steel.Kind.Plate, 5f, 14f, 4f));
        return l;
    }
}
