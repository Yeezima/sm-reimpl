using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The customizer screens in a match (M44): while the player is alive the match stays visible behind them ("Call of Duty style"), so the screens' own background is replaced by
/// a translucent black dim (<see cref="DimAlpha"/>) and the content sits on one opaque panel inset from the edges; on the death screen and at the match's end the
/// screens keep their own opaque background. Works on any full-screen customizer view whose first child is its background (<see cref="UiKit.Background"/>).
/// </summary>
public static class MatchOverlay
{
    public const float DimAlpha = 0.55f;
    const string DimName = "MatchDim", FrameName = "MatchFrame";

    /// <summary>Switches the view between the translucent look (alive in a match) and its normal opaque background.</summary>
    public static void Apply(Control view, bool translucent)
    {
        TextureRect? background = null;
        foreach (var child in view.GetChildren()) if (child is TextureRect t) { background = t; break; }
        if (background is null) return;
        if (translucent && view.GetNodeOrNull(DimName) is null)
        {
            var dim = new ColorRect { Name = DimName, Color = new Color(0f, 0f, 0f, DimAlpha), MouseFilter = Control.MouseFilterEnum.Stop };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            var frame = new Panel { Name = FrameName, MouseFilter = Control.MouseFilterEnum.Ignore };
            frame.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            frame.OffsetLeft = 60; frame.OffsetRight = -60; frame.OffsetTop = 6; frame.OffsetBottom = -6;
            frame.AddThemeStyleboxOverride("panel", UiKit.Box(UiKit.PanelSolid, UiKit.PanelSolid, 0, 0));
            view.AddChild(dim);
            view.AddChild(frame);
            view.MoveChild(dim, 0);
            view.MoveChild(frame, 1);
        }
        if (view.GetNodeOrNull<Control>(DimName) is { } d) d.Visible = translucent;
        if (view.GetNodeOrNull<Control>(FrameName) is { } f) f.Visible = translucent;
        background.Visible = !translucent;
    }
}
