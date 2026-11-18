using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// ui/text-scale (owner, 2026-11-18): lists every piece of visible live text of a screen with its theme font size and the size it has on the window's pixels (font size times the window's stretch scale),
/// so one level of text can be compared across screens and resolutions. A screenshot aid (<c>--text-audit &lt;file&gt;</c>, written when the screenshot is taken) and the data of the self-test that checks
/// one size per level. Menus only; never reads or draws anything of the in-game HUD.
/// </summary>
public static class UiTextAudit
{
    public readonly record struct Item(string Kind, string Path, string Text, int FontSize, float WindowPx, string Face, bool Fractional);

    /// <summary>The window's stretch scale (window pixels per virtual pixel) of the viewport holding <paramref name="node"/>.</summary>
    public static float WindowScale(Node node) => node.GetViewport()?.GetFinalTransform().Scale.X ?? 1f;

    public static List<Item> Collect(Node root)
    {
        var list = new List<Item>();
        Walk(root, root, WindowScale(root), list);
        return list;
    }

    static void Walk(Node root, Node n, float scale, List<Item> into)
    {
        if (n is CanvasItem { Visible: false }) return;
        if (n is Control c)
        {
            string? text = null; Color ink = Colors.White;
            switch (c)
            {
                case Button b: text = b.Text; ink = b.GetThemeColor("font_color"); break;
                case Label l: text = l.Text; ink = l.GetThemeColor("font_color"); break;
                case LineEdit le: text = le.Text.Length > 0 ? le.Text : le.PlaceholderText; ink = le.GetThemeColor("font_color"); break;
                case RichTextLabel rt: text = rt.GetParsedText(); ink = rt.GetThemeColor("default_color"); break;
            }
            if (!string.IsNullOrWhiteSpace(text) && ink.A > 0.05f && c.IsVisibleInTree())
            {
                bool rich = c is RichTextLabel;
                int size = c.GetThemeFontSize(rich ? "normal_font_size" : "font_size");
                var font = c.GetThemeFont(rich ? "normal_font" : "font");
                float px = size * scale;
                into.Add(new Item(n.GetClass(), root.GetPathTo(n).ToString(), text!.Replace("\n", " / "), size, px, font?.GetFontName() ?? "?", Math.Abs(px - MathF.Round(px)) > 0.02f));
            }
        }
        foreach (var child in n.GetChildren()) Walk(root, child, scale, into);
    }

    public static string Dump(Node root)
    {
        var sb = new StringBuilder();
        var rect = root.GetViewport().GetVisibleRect().Size;
        sb.Append(CultureInfo.InvariantCulture, $"scale|{WindowScale(root):0.####}|{rect.X:0}x{rect.Y:0}\n");
        foreach (var i in Collect(root))
            sb.Append(CultureInfo.InvariantCulture, $"{i.Kind}|{i.FontSize}|{i.WindowPx:0.##}|{(i.Fractional ? "FRAC" : "int")}|{i.Face}|{i.Text}|{i.Path}\n");
        return sb.ToString();
    }
}
