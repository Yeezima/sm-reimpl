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
        var title = new Label { Name = "RangeTitle", Text = "FIRING RANGE", MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(580f, 330f) };
        SteelBackdrop.StyleTitle(title, 44);
        AddChild(new SteelBackdrop("RangeDim", title, 0.78f, new Rect2(500f, 290f, 920f, 350f)));   // ui/overhaul: the steel board and plate
        AddChild(title);
        var text = new Label { Name = "RangeText", Text = "A 100 m square test ground with 200 Orks, for the artillery strike and the new features.\nTeleport there from the Stormbird?".ToUpperInvariant(), MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(560f, 420f) };
        text.AddThemeColorOverride("font_color", UiKit.White);
        text.AddThemeFontSizeOverride("font_size", 24); SteelBackdrop.StyleLine(text); text.AddThemeFontOverride("font", UiKit.NoteFont());
        AddChild(text);
        _go = UiKit.MenuButton("Teleport", () => go(), "Go to the Firing Range");
        _go.Position = new Vector2(560f, 520f); _go.Size = new Vector2(380f, 56f); _go.AddThemeFontSizeOverride("font_size", 26); SteelBackdrop.StylePlate(_go);
        AddChild(_go);
        var back = UiKit.MenuButton("Cancel", () => cancel(), "Stay in the Stormbird (Esc, or B on a controller)");
        back.Position = new Vector2(980f, 520f); back.Size = new Vector2(380f, 56f); back.AddThemeFontSizeOverride("font_size", 26); SteelBackdrop.StylePlate(back);
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
