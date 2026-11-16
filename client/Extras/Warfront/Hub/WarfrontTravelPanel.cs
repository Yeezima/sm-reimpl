using Godot;
using SmClient.Customizer;

namespace SmClient.Extras.Warfront.Hub;

/// <summary>
/// The travel screen (plan section 8): shown from the leader's deploy until the next battlefield is loaded and the players have rejoined. Full screen in the menus' style, no input: the title, where the
/// Thunderhawk is going, and what the load is waiting for (<see cref="WarfrontHubModel.TravelStatus"/>, fed by <see cref="WarfrontHubModel.SetTravelProgress"/>). The elapsed time counts up (a first visit to a battlefield can take two to three minutes); One title, one heading line and one body line, all live labels. Laid out in the 1920 by 1080 space from the top left, ours.
/// </summary>
public sealed partial class WarfrontTravelPanel : Control
{
    const float Left = UiKit.MenuLeft;
    readonly WarfrontHubModel _model;
    readonly Label _destination, _status;
    readonly Label _elapsed;
    double _clock;

    public Label DestinationLabel => _destination;
    public Label StatusLabel => _status;
    public Label ElapsedLabel => _elapsed;
    /// <summary>Seconds since the screen was shown.</summary>
    public double Elapsed => _clock;

    public WarfrontTravelPanel(WarfrontHubModel model)
    {
        Name = "WarfrontTravelPanel";
        _model = model;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var dim = new ColorRect { Name = "WarfrontTravelDim", Color = new Color(0f, 0f, 0f, 0.96f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        var title = new Label { Name = "WarfrontTravelTitle", Text = "TRAVELLING", MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(Left - 3.5f, 52f) };
        title.AddThemeFontOverride("font", UiKit.TitleFont);
        title.AddThemeFontSizeOverride("font_size", UiKit.MenuTitleSize);
        title.AddThemeColorOverride("font_color", UiKit.White);
        AddChild(title);
        _destination = Text("WarfrontTravelDestination", 300f, WarfrontHubPanel.HeadingSize, 1400f);
        _status = Text("WarfrontTravelStatus", 360f, WarfrontHubPanel.BodySize, 1400f);
        _elapsed = Text("WarfrontTravelElapsed", 396f, WarfrontHubPanel.BodySize, 1400f);
        model.Changed += Refresh;
        TreeExited += () => model.Changed -= Refresh;
        VisibilityChanged += () => { if (Visible) SetElapsed(0); };
        Refresh();
    }

    Label Text(string name, float y, int size, float width)
    {
        var l = UiKit.MenuText("", null, clip: false);
        l.Name = name;
        l.AddThemeFontSizeOverride("font_size", size);
        l.SetAnchorsPreset(LayoutPreset.TopLeft);
        l.Position = new Vector2(Left, y);
        l.Size = new Vector2(width, size + 8f);
        AddChild(l);
        return l;
    }

    public void Refresh()
    {
        if (_destination.Text != _model.TravelDestination) _destination.Text = _model.TravelDestination;
        if (_status.Text != _model.TravelStatus) _status.Text = _model.TravelStatus;
        SetElapsed(_clock);
    }

    /// <summary>Sets the clock (a test aid, and the reset when the screen is shown).</summary>
    public void SetElapsed(double seconds)
    {
        _clock = seconds;
        string text = WarfrontHubModel.ElapsedText(seconds);
        if (_elapsed.Text != text) _elapsed.Text = text;
    }

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree()) return;
        SetElapsed(_clock + delta);
    }
}
