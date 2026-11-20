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

    /// <summary>Puts <paramref name="text"/> on <paramref name="c"/> as its hover hint (empty text removes it). The pointer's hint is the engine's tooltip in the theme's plate; the focus's hint is drawn by <see cref="FocusHint"/>.</summary>
    public static T Attach<T>(T c, string text) where T : Control
    {
        c.TooltipText = text;
        if (text.Length == 0 || c.HasMeta("ui_tooltip_focus")) return c;
        c.SetMeta("ui_tooltip_focus", true);
        c.FocusEntered += () => FocusHint.Start(c);
        c.FocusExited += () => FocusHint.Stop(c);
        c.TreeExiting += () => FocusHint.Stop(c);
        return c;
    }

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
        if (ConceptArt.Get("opt-plate-blank") is { } tex)
        {
            // Under a plate's own text inset (4 x 2 concept pixels) plus the bevel; the plate is small, so its caps are the table's.
            var box = PaintedButtons.Box(tex, "opt-plate-blank", default, 1f, Colors.White, 2.45f);
            return box;
        }
        return new StyleBoxFlat { BgColor = new Color(0.055f, 0.042f, 0.022f), BorderColor = new Color(0.50f, 0.37f, 0.12f), BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1, ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 6, ContentMarginBottom = 6 };
    }

    /// <summary>The hint of the focused option: starts a timer on focus, shows the plate under the option when it still has the focus after the delay.</summary>
    static class FocusHint
    {
        static PanelContainer? _shown;
        static Control? _owner;
        static int _token;

        public static void Start(Control c)
        {
            int token = ++_token;
            if (c.GetTree() is not { } tree) return;
            tree.CreateTimer(DelaySec).Timeout += () =>
            {
                if (token != _token || !GodotObject.IsInstanceValid(c) || !c.IsInsideTree() || !c.HasFocus() || c.TooltipText.Length == 0) return;
                Show(c);
            };
        }

        public static void Stop(Control c)
        {
            if (_owner != c && _shown is null) { _token++; return; }
            _token++;
            if (_shown is not null && GodotObject.IsInstanceValid(_shown)) _shown.QueueFree();
            _shown = null; _owner = null;
        }

        static void Show(Control c)
        {
            if (_shown is not null && GodotObject.IsInstanceValid(_shown)) _shown.QueueFree();
            var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 100 };
            panel.AddThemeStyleboxOverride("panel", Plate());
            var label = new Label { Text = c.TooltipText, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(360f, 0f), MouseFilter = Control.MouseFilterEnum.Ignore };
            label.AddThemeFontOverride("font", UiKit.ConceptFont());
            label.AddThemeFontSizeOverride("font_size", UiType.Px(UiType.Caption));
            label.AddThemeColorOverride("font_color", new Color(0.88f, 0.87f, 0.83f));
            panel.AddChild(label);
            c.GetTree().Root.AddChild(panel);
            var view = c.GetViewport().GetVisibleRect().Size;
            var at = c.GetGlobalRect(); var pos = new Vector2(at.Position.X, at.End.Y + 4f);
            var size = panel.GetCombinedMinimumSize();
            if (pos.Y + size.Y > view.Y) pos.Y = at.Position.Y - size.Y - 4f;
            pos.X = Mathf.Clamp(pos.X, 4f, Mathf.Max(4f, view.X - size.X - 4f));
            panel.Position = pos;
            _shown = panel; _owner = c;
        }
    }
}
