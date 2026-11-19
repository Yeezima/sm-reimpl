using System;
using Godot;
using SmClient.Customizer;
using SmSim.Extras.Warfront.Campaign;

namespace SmClient.Extras.Warfront.Hub;

/// <summary>
/// The Warfront war map aboard the Thunderhawk (plan section 8), full screen in the menus' style (<c>MenuKit.cs</c>, as the Musou <c>WarMapPanel</c>: DejaVu Sans Bold, transparent text buttons that
/// brighten on hover and focus, Esc or B closes): three lanes of three nodes between the Imperial HQ and the Ork fortress, the front of each lane marked, the last deployment's results beside it, the
/// party with its leader, and one DEPLOY row per lane plus the HQ. Only the leader's rows can be pressed; with a mandatory HQ defense the HQ row is the only one. Everything is fed by a
/// <see cref="WarfrontHubModel"/> and redrawn only when it changes. Text styles: title, one heading size, one body size; every label is a live label, only boxes and lines are drawn. Laid out in the
/// 1920 by 1080 space from the top left, ours.
/// </summary>
public sealed partial class WarfrontHubPanel : Control
{
    public const int HeadingSize = 24, BodySize = 16;
    public const float Left = UiKit.MenuLeft, MapTop = 220f, LanePitch = 150f, BoxTop = 36f, BoxHeight = 84f;
    public const float HqWidth = 190f, NodeLeft = 340f, NodeWidth = 220f, NodeStep = 260f, FortressLeft = 1120f, FortressWidth = 190f, TextColumn = 1340f, TextWidth = 460f;
    public const float RowsTop = 700f, RowPitch = 40f;
    public const float StageInset = 14f, StageHeight = 56f;
    public const int PartyRows = 6;
    public static float StageTop(int i) => MapTop + BoxTop + 110f + 80f * i;
    public static float WarbossTop => MapTop + BoxTop + 190f;
    public static readonly Color HereGold = new(0.98f, 0.8f, 0.12f), OrkRed = new(0.86f, 0.1f, 0.08f);

    readonly WarfrontHubModel _model;
    readonly Action? _close;
    readonly Label _header, _note, _partyHeading;
    readonly Label[] _laneNames = new Label[3], _status = new Label[3], _last = new Label[3], _floor = new Label[3], _party = new Label[PartyRows];
    readonly Label[,] _nodes = new Label[3, 3];
    readonly Label _hq, _fortress;
    readonly Button[] _rows = new Button[WarfrontHubModel.Rows];
    readonly Label[] _stages = new Label[3];
    readonly Label _warboss;
    readonly Button? _closeButton;
    readonly MapDraw _map;

    public WarfrontHubModel Model => _model;
    public Button DeployRow(int row) => _rows[row];
    public Button? CloseButton => _closeButton;
    public Label NodeLabelOf(int lane, int node) => _nodes[lane, node - 1];
    public Label HqLabel => _hq;
    public Label StageLabel(int i) => _stages[i];
    public Label WarbossLabel => _warboss;
    public Label FortressLabel => _fortress;
    public Label NoteLabel => _note;
    public Label HeaderLabel => _header;
    public Label StatusLabel(int lane) => _status[lane];
    public Label LastLabel(int lane) => _last[lane];
    public Label FloorLabel(int lane) => _floor[lane];
    public Label PartyLabel(int i) => _party[i];

    /// <summary>Every live label of the panel (the self-test's fit check).</summary>
    public System.Collections.Generic.IEnumerable<Label> Labels()
    {
        foreach (var n in GetChildren()) if (n is Label l) yield return l;
    }

    /// <summary>The tracks, the node boxes and the marker (shapes only).</summary>
    sealed partial class MapDraw : Control
    {
        readonly WarfrontHubModel _m;
        public MapDraw(WarfrontHubModel m) { _m = m; Name = "WarfrontMap"; MouseFilter = MouseFilterEnum.Ignore; }

        /// <summary>A frame of equal width on all four sides, on whole pixels: four rectangles, never an outline stroke.</summary>
        public static void Frame(CanvasItem c, Rect2 r, float w, Color colour)
        {
            c.DrawRect(new Rect2(r.Position.X, r.Position.Y, r.Size.X, w), colour);
            c.DrawRect(new Rect2(r.Position.X, r.End.Y - w, r.Size.X, w), colour);
            c.DrawRect(new Rect2(r.Position.X, r.Position.Y + w, w, r.Size.Y - 2f * w), colour);
            c.DrawRect(new Rect2(r.End.X - w, r.Position.Y + w, w, r.Size.Y - 2f * w), colour);
        }

        public override void _Draw()
        {
            var v = _m.View;
            var line = new Color(1f, 1f, 1f, 0.35f);
            var dim = new Color(1f, 1f, 1f, 0.3f);
            float hqTop = MapTop + BoxTop, hqBottom = MapTop + 2 * LanePitch + BoxTop + BoxHeight;
            // One frame width (2 px) everywhere: colour and fill carry the state.
            bool hqDanger = v.HqMandatory || v.Phase == WarfrontPhase.Lost;
            var hqColour = hqDanger ? OrkRed : new Color(1f, 1f, 1f, 0.8f);
            Frame(this, new Rect2(Left, hqTop, HqWidth, hqBottom - hqTop), 2f, hqColour);
            for (int i = 0; i < 3; i++) Frame(this, new Rect2(Left + StageInset, StageTop(i), HqWidth - 2f * StageInset, StageHeight), 2f, hqDanger ? new Color(OrkRed, 0.75f) : new Color(1f, 1f, 1f, 0.5f));
            bool fortressOpen = v.FortressUnlocked || v.Phase == WarfrontPhase.Won;
            var fortColour = fortressOpen ? HereGold : dim;
            Frame(this, new Rect2(FortressLeft, hqTop, FortressWidth, hqBottom - hqTop), 2f, fortColour);
            Frame(this, new Rect2(FortressLeft + StageInset, WarbossTop, FortressWidth - 2f * StageInset, StageHeight), 2f, fortColour);
            for (int lane = 0; lane < 3; lane++)
            {
                float y = MapTop + lane * LanePitch + BoxTop, mid = y + BoxHeight / 2f;
                int front = v.Fronts[lane];
                // The track runs between the boxes only (a translucent box must not show a line through its text).
                float gapStart = Left + HqWidth;
                for (int node = 1; node <= 3; node++)
                {
                    float boxLeft = NodeLeft + (node - 1) * NodeStep;
                    DrawRect(new Rect2(gapStart, mid - 1f, boxLeft - gapStart, 2f), line);
                    gapStart = boxLeft + NodeWidth;
                }
                DrawRect(new Rect2(gapStart, mid - 1f, FortressLeft - gapStart, 2f), line);
                for (int node = 1; node <= 3; node++)
                {
                    var box = new Rect2(NodeLeft + (node - 1) * NodeStep, y, NodeWidth, BoxHeight);
                    if (node == front) { DrawRect(box, new Color(HereGold, 0.28f)); Frame(this, box, 2f, HereGold); }
                    else if (node < front) { DrawRect(box, new Color(1f, 1f, 1f, 0.10f)); Frame(this, box, 2f, new Color(1f, 1f, 1f, 0.8f)); }
                    else Frame(this, box, 2f, new Color(OrkRed, 0.75f));
                }
                // The marker only for the two states off the nodes: broken through (at the HQ) and cleared (at the fortress).
                if (front == 0) DrawRect(new Rect2(Left + HqWidth + 8f, mid - 8f, 16f, 16f), OrkRed);
                else if (front == 4) DrawRect(new Rect2(FortressLeft - 24f, mid - 8f, 16f, 16f), HereGold);
            }
        }
    }

    public WarfrontHubPanel(WarfrontHubModel model, Action? close = null)
    {
        Name = "WarfrontHubPanel";
        _model = model;
        _close = close;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var dim = new ColorRect { Name = "WarfrontHubDim", Color = new Color(0f, 0f, 0f, 0.88f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        var title = new Label { Name = "WarfrontHubTitle", Text = "WAR MAP", MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(Left - 3.5f, 52f) };
        title.AddThemeFontOverride("font", UiKit.TitleFont);
        title.AddThemeFontSizeOverride("font_size", UiKit.MenuTitleSize);
        title.AddThemeColorOverride("font_color", UiKit.White);
        AddChild(title);

        _header = Text("WarfrontHeader", Left, 150f, HeadingSize, 1200f);
        _map = new MapDraw(model);
        Place(_map, 0f, 0f, UiKit.VirtualWidth, UiKit.VirtualHeight);
        AddChild(_map);

        _hq = Text("WarfrontHq", Left + 14f, MapTop + BoxTop + 12f, BodySize, HqWidth - 20f, lines: 2);
        _fortress = Text("WarfrontFortress", FortressLeft + 14f, MapTop + BoxTop + 12f, BodySize, FortressWidth - 20f, lines: 2);
        string[] stages = { "WALL", "KEEP", "CAVES" };
        for (int i = 0; i < 3; i++) { _stages[i] = Text("WarfrontStage" + i, Left + StageInset + 14f, StageTop(i) + 16f, BodySize, HqWidth - 2f * StageInset - 20f); _stages[i].Text = stages[i]; }
        _warboss = Text("WarfrontWarboss", FortressLeft + StageInset + 14f, WarbossTop + 16f, BodySize, FortressWidth - 2f * StageInset - 20f);
        _warboss.Text = "WARBOSS";
        for (int lane = 0; lane < 3; lane++)
        {
            float y = MapTop + lane * LanePitch;
            _laneNames[lane] = Text("WarfrontLane" + lane, NodeLeft, y - 2f, HeadingSize, 400f);
            for (int node = 1; node <= 3; node++)
                _nodes[lane, node - 1] = Text($"WarfrontNode{lane}{node}", NodeLeft + (node - 1) * NodeStep + 14f, y + BoxTop + 12f, BodySize, NodeWidth - 20f, lines: 2);
            _status[lane] = Text("WarfrontStatus" + lane, TextColumn, y + BoxTop + 4f, BodySize, TextWidth);
            _last[lane] = Text("WarfrontLast" + lane, TextColumn, y + BoxTop + 30f, BodySize, TextWidth);
            _floor[lane] = Text("WarfrontFloor" + lane, TextColumn, y + BoxTop + 56f, BodySize, TextWidth);
            _laneNames[lane].Text = WarfrontHubModel.LaneName(lane);
        }
        for (int row = 0; row < WarfrontHubModel.Rows; row++)
        {
            int r = row;
            var b = UiKit.MenuButton("Deploy", () => OnRow(r));
            b.Name = "WarfrontDeploy" + row;
            b.AddThemeFontSizeOverride("font_size", HeadingSize);
            Place(b, Left, RowsTop + row * RowPitch, 1000f, RowPitch - 2f);
            AddChild(b);
            _rows[row] = b;
        }
        _note = Text("WarfrontNote", Left, RowsTop + WarfrontHubModel.Rows * RowPitch + 20f, BodySize, 1200f);
        _partyHeading = Text("WarfrontPartyHeading", TextColumn, RowsTop, HeadingSize, TextWidth);
        _partyHeading.Text = "PARTY";
        for (int i = 0; i < PartyRows; i++) _party[i] = Text("WarfrontParty" + i, TextColumn, RowsTop + 40f + i * 28f, BodySize, TextWidth);
        if (close is not null)
        {
            _closeButton = UiKit.MenuButton("Close", () => _close?.Invoke(), "Back to the Thunderhawk (Esc, or B on a controller)");
            _closeButton.AddThemeFontSizeOverride("font_size", HeadingSize);
            Place(_closeButton, Left, RowsTop + WarfrontHubModel.Rows * RowPitch + 60f, 240f, RowPitch - 2f);
            AddChild(_closeButton);
        }
        model.Changed += Refresh;
        TreeExited += () => model.Changed -= Refresh;
        Refresh();
    }

    static void Place(Control c, float x, float y, float w, float h)
    {
        c.SetAnchorsPreset(LayoutPreset.TopLeft);
        c.Position = new Vector2(x, y);
        c.Size = new Vector2(w, h);
    }

    Label Text(string name, float x, float y, int size, float width, int lines = 1)
    {
        var l = UiKit.MenuText("", null, clip: false);
        l.Name = name;
        l.AddThemeFontSizeOverride("font_size", size);
        Place(l, x, y, width, lines * (size + 8f));
        AddChild(l);
        return l;
    }

    static void Set(Label l, string text) { if (l.Text != text) l.Text = text; }

    void OnRow(int row)
    {
        if (_model.RowTarget(row) is { } t) _model.RequestDeploy(t);
    }

    /// <summary>The focus on the first deployment row that can be pressed, else CLOSE.</summary>
    public void Focus()
    {
        foreach (var b in _rows) if (b.Visible && !b.Disabled) { b.FocusLater(); return; }
        _closeButton?.FocusLater();
    }

    /// <summary>Redraws from the model; a text is set only when it changed.</summary>
    public void Refresh()
    {
        var m = _model;
        Set(_header, m.Header);
        Set(_hq, "IMPERIAL HQ\n" + m.HqState);
        Set(_fortress, "ORK FORTRESS\n" + m.FortressState);
        for (int lane = 0; lane < 3; lane++)
        {
            Set(_status[lane], m.LaneStatus(lane));
            Set(_last[lane], m.LastText(lane));
            Set(_floor[lane], m.FloorText(lane));
            for (int node = 1; node <= 3; node++) Set(_nodes[lane, node - 1], m.NodeLabel(lane, node));
        }
        for (int row = 0; row < WarfrontHubModel.Rows; row++)
        {
            var b = _rows[row];
            string text = m.DeployRowText(row).ToUpperInvariant();
            if (b.Text != text) b.Text = text;
            bool visible = m.RowVisible(row);
            if (b.Visible != visible) b.Visible = visible;
            bool can = m.RowTarget(row) is { } t && m.CanDeploy(t);
            if (b.Disabled == can) b.Disabled = !can;
            // An open target a non-leader cannot press keeps the normal colour; an unavailable one (and the locked fortress) is greyed.
            if (m.RowOpen(row) && !can) b.AddThemeColorOverride("font_disabled_color", UiKit.MenuIdle);
            else b.RemoveThemeColorOverride("font_disabled_color");
        }
        WireFocus();
        Set(_note, m.Note);
        for (int i = 0; i < PartyRows; i++) Set(_party[i], m.PartyLine(i));
        _map.QueueRedraw();
    }

    public override void _Ready() => Refresh();

    /// <summary>Up and down (keys, D-pad, stick) walk the rows that can be pressed, then CLOSE; they wrap.</summary>
    void WireFocus()
    {
        if (!IsInsideTree()) return;
        var chain = new System.Collections.Generic.List<Button>();
        foreach (var b in _rows) if (b.Visible && !b.Disabled) chain.Add(b);
        if (_closeButton is not null) chain.Add(_closeButton);
        for (int i = 0; i < chain.Count; i++)
        {
            var next = chain[(i + 1) % chain.Count];
            var prev = chain[(i + chain.Count - 1) % chain.Count];
            chain[i].FocusNeighborBottom = chain[i].GetPathTo(next);
            chain[i].FocusNeighborTop = chain[i].GetPathTo(prev);
        }
    }

    public override void _Input(InputEvent e)
    {
        if (_close is null || !IsVisibleInTree() || !SmClient.PadHost.IsBack(e)) return;
        GetViewport().SetInputAsHandled();
        _close();
    }
}
