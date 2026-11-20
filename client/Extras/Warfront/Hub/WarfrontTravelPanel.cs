using Godot;
using SmClient.Customizer;

namespace SmClient.Extras.Warfront.Hub;

/// <summary>
/// The travel screen (plan section 8): shown from the leader's deploy until the next battlefield is loaded and the players have rejoined. Full screen in the concept's style (<see cref="WarfrontStyle"/>), no input: the title, where the
/// Thunderhawk is going, and what the load is waiting for (<see cref="WarfrontHubModel.TravelStatus"/>, fed by <see cref="WarfrontHubModel.SetTravelProgress"/>). The elapsed time counts up (a first visit to a battlefield can take two to three minutes); One title, one heading line and one body line, all live labels. Laid out in the 1920 by 1080 space from the top left, ours.
/// </summary>
public sealed partial class WarfrontTravelPanel : Control
{
    readonly WarfrontHubModel _model;
    readonly WarfrontFrame _frame;
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
        _frame = new WarfrontFrame("WarfrontTravelFrame", "Travelling", 0.85f);   // the concept frame of OPTIONS and the WARFRONT page
        AddChild(_frame);
        _destination = Text("WarfrontTravelDestination", 190f, WarfrontHubPanel.HeadingSize, WarfrontFrame.StageW, WarfrontStyle.Gold);
        _status = Text("WarfrontTravelStatus", 250f, WarfrontHubPanel.BodySize, WarfrontFrame.StageW);
        _elapsed = Text("WarfrontTravelElapsed", 300f, WarfrontHubPanel.BodySize, WarfrontFrame.StageW);
        UiKit.TextKind(_destination, "progress"); UiKit.TextKind(_status, "progress");   // where the ship goes and what the load waits for
        model.Changed += Refresh;
        TreeExited += () => model.Changed -= Refresh;
        VisibilityChanged += () => { if (Visible) SetElapsed(0); };
        Refresh();
    }

    Label Text(string name, float y, int size, float width, Color? ink = null)
    {
        var l = UiKit.MenuText("", null, clip: false);
        l.Name = name;
        WarfrontStyle.Skin(l, size, ink);
        l.SetAnchorsPreset(LayoutPreset.TopLeft);
        l.HorizontalAlignment = HorizontalAlignment.Center;
        l.Position = new Vector2(0f, y);
        l.Size = new Vector2(width, size + 14f);
        _frame.Stage.AddChild(l);
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
