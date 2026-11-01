using System;
using Godot;
using SmClient.Customizer;

namespace SmClient;

/// <summary>The Stormbird's Firing Range button, confirmed: a dimmed screen, one line and two buttons (placeholder demonstration of a station's confirm). Esc, or B on a controller, cancels.</summary>
public sealed partial class TfwRangeConfirm : Control
{
    readonly Action _cancel;
    readonly Button _go;

    public TfwRangeConfirm(Action go, Action cancel)
    {
        Name = "TfwRangeConfirm";
        _cancel = cancel;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var dim = new ColorRect { Name = "RangeDim", Color = new Color(0f, 0f, 0f, 0.8f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        var title = new Label { Name = "RangeTitle", Text = "FIRING RANGE", MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(560f, 340f) };
        title.AddThemeFontOverride("font", UiKit.TitleFont);
        title.AddThemeFontSizeOverride("font_size", UiKit.MenuTitleSize);
        title.AddThemeColorOverride("font_color", UiKit.White);
        AddChild(title);
        var text = new Label { Name = "RangeText", Text = "A 100 m square test ground with 200 Orks, for the artillery strike and the new features.\nTeleport there from the Stormbird?", MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(560f, 420f) };
        text.AddThemeColorOverride("font_color", UiKit.White);
        text.AddThemeFontSizeOverride("font_size", 18);
        AddChild(text);
        _go = UiKit.MenuButton("Teleport", () => go(), "Go to the Firing Range");
        _go.Position = new Vector2(560f, 500f); _go.Size = new Vector2(240f, 30f);
        AddChild(_go);
        var back = UiKit.MenuButton("Cancel", () => cancel(), "Stay in the Stormbird (Esc, or B on a controller)");
        back.Position = new Vector2(560f, 540f); back.Size = new Vector2(240f, 30f);
        AddChild(back);
    }

    public Button GoButton => _go;
    public void Focus() => _go.FocusLater();

    public override void _Input(InputEvent e)
    {
        if (!IsVisibleInTree() || !SmClient.PadHost.IsBack(e)) return;
        GetViewport().SetInputAsHandled();
        _cancel();
    }
}
