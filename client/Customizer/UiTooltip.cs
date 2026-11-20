using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The one hover hint of the menus (owner, 2026-11-20: "hints and longer descriptions can be retained but hidden until a player mouses over an option for a short amount of time"): the explainer of an option is not on
/// the screen; it shows on a dark painted plate (the concept's own <c>opt-plate-blank</c>, the one lettering and the size table, the text inset) after <see cref="DelaySec"/> of the pointer resting on the option, or of the
/// keyboard or controller focus resting on it. <see cref="Attach"/> is the call every page uses (the Warfront pages too); <see cref="AddToTheme"/> gives Godot's own pointer tooltip the same plate. Ours (spec "Hover hints").
/// </summary>
public static class UiTooltip
{
    /// <summary>Seconds the pointer or the focus rests on an option before its hint shows.</summary>
    public const float DelaySec = 0.6f;

    /// <summary>
    /// Puts <paramref name="text"/> on <paramref name="c"/> as its hover hint (empty text removes it). Both the pointer's and the focus's hint are drawn by <see cref="FocusHint"/> (one plate, upper case, beside the option); while the
    /// pointer is on the option the engine's own tooltip is held back (its text is put back when the pointer leaves), so the engine's plate never shows a second time under the cursor.
    /// </summary>
    public static T Attach<T>(T c, string text) where T : Control
    {
        c.TooltipText = text;
        if (text.Length == 0 || c.HasMeta("ui_tooltip_focus")) return c;
        c.SetMeta("ui_tooltip_focus", true);
        c.FocusEntered += () => FocusHint.Start(c);
        c.FocusExited += () => FocusHint.Stop(c);
        c.MouseEntered += () =>
        {
            if (c.TooltipText.Length > 0) { c.SetMeta("ui_tooltip_text", c.TooltipText); c.TooltipText = ""; }
            FocusHint.Start(c);
        };
        c.MouseExited += () => { Restore(c); FocusHint.Stop(c); };
        c.TreeExiting += () => { Restore(c); FocusHint.Stop(c); };
        return c;
    }

    static void Restore(Control c)
    {
        if (c.GetMeta("ui_tooltip_text", "").AsString() is { Length: > 0 } held && c.TooltipText.Length == 0) c.TooltipText = held;
        if (c.HasMeta("ui_tooltip_text")) c.RemoveMeta("ui_tooltip_text");
    }

    static string HintText(Control c) => c.TooltipText.Length > 0 ? c.TooltipText : c.GetMeta("ui_tooltip_text", "").AsString();

    /// <summary>Shows the focus hint of <paramref name="c"/> now (screenshot aid: <c>--show-tooltip &lt;control name&gt;</c>).</summary>
    public static void ShowNow(Control c) => FocusHint.Show(c);

    /// <summary>The engine's tooltip (the pointer's) in the concept's look: the plate, the one face, the caption size; the delay is the project setting, set once here.</summary>
    public static void AddToTheme(Theme t)
    {
        t.SetStylebox("panel", "TooltipPanel", Plate());
        t.SetFont("font", "TooltipLabel", UiKit.ConceptFont());
        t.SetFontSize("font_size", "TooltipLabel", UiType.Px(UiType.Caption));
        t.SetColor("font_color", "TooltipLabel", new Color(0.88f, 0.87f, 0.83f));
        t.SetConstant("outline_size", "TooltipLabel", 0);
        try { ProjectSettings.SetSetting("gui/timers/tooltip_delay_sec", DelaySec); } catch (System.Exception) { /* the engine keeps its own delay */ }
    }

    static StyleBox Plate()
    {
        // The BACK plate's art (arm-back): chamfered ends cut clean, an opaque dark middle, no square shadow at the corners (owner, 2026-11-20); opt-plate-blank has a soft square shadow round its chamfers.
        if (ConceptArt.Get("arm-back") is { } tex)
        {
            var box = PaintedButtons.Box(tex, "arm-back", default, 1f, Colors.White, 2.45f);
            box.ContentMarginLeft = 66f; box.ContentMarginRight = 66f; box.ContentMarginTop = 30f; box.ContentMarginBottom = 30f;   // clear of the plate's chamfered ends and rim
            return box;
        }
        return new StyleBoxFlat { BgColor = new Color(0.055f, 0.042f, 0.022f), BorderColor = new Color(0.50f, 0.37f, 0.12f), BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1, ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 6, ContentMarginBottom = 6 };
    }

    /// <summary>Where the hint goes (owner, 2026-11-20): beside the option, to its right; where that has no room to its left, else above it (below only when there is no room above either), so it does not cover the options next to it.</summary>
    public static Vector2 Place(Rect2 at, Vector2 size, Vector2 view)
    {
        const float gap = 6f;
        float y = Mathf.Clamp(at.Position.Y + (at.Size.Y - size.Y) / 2f, 4f, Mathf.Max(4f, view.Y - size.Y - 4f));
        if (at.End.X + gap + size.X <= view.X - 4f) return new Vector2(at.End.X + gap, y);
        if (at.Position.X - gap - size.X >= 4f) return new Vector2(at.Position.X - gap - size.X, y);
        float x = Mathf.Clamp(at.Position.X, 4f, Mathf.Max(4f, view.X - size.X - 4f));
        return at.Position.Y - gap - size.Y >= 4f ? new Vector2(x, at.Position.Y - gap - size.Y) : new Vector2(x, Mathf.Min(at.End.Y + gap, Mathf.Max(4f, view.Y - size.Y - 4f)));
    }

    /// <summary>The hint of the focused option: starts a timer on focus, shows the plate under the option when it still has the focus after the delay.</summary>
    static class FocusHint
    {
        static CanvasLayer? _shown;
        static Control? _owner;
        static int _token;

        public static void Start(Control c)
        {
            int token = ++_token;
            if (c.GetTree() is not { } tree) return;
            tree.CreateTimer(DelaySec).Timeout += () =>
            {
                if (token != _token || !GodotObject.IsInstanceValid(c) || !c.IsInsideTree() || !c.IsVisibleInTree() || HintText(c).Length == 0) return;
                if (!c.HasFocus() && !c.GetGlobalRect().HasPoint(c.GetGlobalMousePosition())) return;   // neither the focus nor the pointer is on it any more
                Show(c);
            };
        }

        public static void Stop(Control c)
        {
            if (_owner != c) { if (_shown is null) _token++; return; }   // only the owner of the shown hint frees it
            _token++;
            if (_shown is not null && GodotObject.IsInstanceValid(_shown)) _shown.QueueFree();
            _shown = null; _owner = null;
        }

        public static void Show(Control c)
        {
            if (_shown is not null && GodotObject.IsInstanceValid(_shown)) _shown.QueueFree();
            var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 100 };
            panel.AddThemeStyleboxOverride("panel", Plate());
            string hint = HintText(c).ToUpperInvariant();   // the UI is all upper case
            float one = UiKit.ConceptFont().GetStringSize(hint, HorizontalAlignment.Left, -1, UiType.Px(UiType.Caption)).X;
            var label = new Label { Text = hint, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(Mathf.Min(480f, Mathf.Ceil(one) + 2f), 0f), MouseFilter = Control.MouseFilterEnum.Ignore };
            label.AddThemeFontOverride("font", UiKit.ConceptFont());
            label.AddThemeFontSizeOverride("font_size", UiType.Px(UiType.Caption));
            label.AddThemeColorOverride("font_color", new Color(0.88f, 0.87f, 0.83f));
            panel.AddChild(label);
            var layer = new CanvasLayer { Layer = 120 };   // over every menu layer
            layer.AddChild(panel);
            c.GetTree().Root.AddChild(layer);
            panel.Position = Place(c.GetGlobalRect(), panel.GetCombinedMinimumSize(), c.GetViewport().GetVisibleRect().Size);
            _shown = layer; _owner = c;
        }
    }
}
