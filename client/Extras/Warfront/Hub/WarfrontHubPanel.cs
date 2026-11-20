using System;
using Godot;
using SmClient.Customizer;
using SmSim.Extras.Warfront.Campaign;

namespace SmClient.Extras.Warfront.Hub;

/// <summary>
/// The Warfront war map aboard the Thunderhawk (plan section 8), full screen in the concept's style (<see cref="WarfrontStyle"/>: the concept lettering, the kit's recessed steel boxes with a gold rim on the front, the concept's plates for the
/// deployment rows, Esc or B closes): three lanes of three nodes between the Imperial HQ and the Ork fortress, the front of each lane marked, the last deployment's results beside it, the
/// party with its leader, and one DEPLOY row per lane plus the HQ. Only the leader's rows can be pressed; with a mandatory HQ defense the HQ row is the only one. Everything is fed by a
/// <see cref="WarfrontHubModel"/> and redrawn only when it changes. Text styles: title, one heading size, one body size of the one table (caption and small); every label is a live label, only boxes and lines are drawn. Laid out in the
/// 1920 by 1080 space from the top left, ours.
/// </summary>
public sealed partial class WarfrontHubPanel : Control
{
    public const int HeadingSize = UiType.Caption, BodySize = UiType.Small;
    // The stage of the concept frame (WarfrontFrame.StageW by StageH design pixels): the map across the top, the deploy rows, the party and CLOSE under it.
    public const float Left = 0f, MapTop = 40f, LanePitch = 100f, BoxTop = 0f, BoxHeight = 76f;
    public const float HqWidth = 150f, NodeLeft = 190f, NodeWidth = 190f, NodeStep = 215f, FortressLeft = 835f, FortressWidth = 150f, TextColumn = 1010f, TextWidth = 390f;
    public const float RowsTop = 328f, RowPitch = 44f, RowsWidth = 660f, SideLeft = 700f, SideWidth = 300f;
    public const float StageInset = 10f, StageHeight = 48f;
    public const int PartyRows = 6;
    public static float StageTop(int i) => MapTop + BoxTop + 70f + 62f * i;
    public static float WarbossTop => MapTop + BoxTop + 130f;
    /// <summary>The leader's plain line is not text on the screen: the open rows say it.</summary>
    const string YouLeadLine = "YOU LEAD: CHOOSE THE DEPLOYMENT";
    public static readonly Color HereGold = WarfrontStyle.GoldFill, OrkRed = WarfrontStyle.Alert;

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
    readonly WarfrontFrame _frame;
    readonly Control _stage;

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
        foreach (var n in _stage.GetChildren()) if (n is Label l) yield return l;
    }

    /// <summary>The tracks, the node boxes and the marker (shapes only).</summary>
    sealed partial class MapDraw : Control
    {
        readonly WarfrontHubModel _m;
        public MapDraw(WarfrontHubModel m) { _m = m; Name = "WarfrontMap"; MouseFilter = MouseFilterEnum.Ignore; }

        /// <summary>A box of the kit: the recessed steel panel, gold rimmed when it is the front, the dark off plate when it is still ahead.</summary>
        void BoxOf(Rect2 r, Steel.Kind kind) => WarfrontStyle.Board.Box(this, kind, r, 6f, 1f);

        public override void _Draw()
        {
            var v = _m.View;
            var line = new Color(Steel.SteelMid, 0.8f);
            float hqTop = MapTop + BoxTop, hqBottom = MapTop + 2 * LanePitch + BoxTop + BoxHeight;
            // The kit's boxes carry the state: the recess is a held place, the gold rim the front, the off plate what is still ahead; red marks the HQ in danger.
            bool hqDanger = v.HqMandatory || v.Phase == WarfrontPhase.Lost;
            BoxOf(new Rect2(Left, hqTop, HqWidth, hqBottom - hqTop), Steel.Kind.Recess);
            for (int i = 0; i < 3; i++) BoxOf(new Rect2(Left + StageInset, StageTop(i), HqWidth - 2f * StageInset, StageHeight), hqDanger ? Steel.Kind.Hot : Steel.Kind.Dark);
            bool fortressOpen = v.FortressUnlocked || v.Phase == WarfrontPhase.Won;
            BoxOf(new Rect2(FortressLeft, hqTop, FortressWidth, hqBottom - hqTop), fortressOpen ? Steel.Kind.CardGold : Steel.Kind.Off);
            BoxOf(new Rect2(FortressLeft + StageInset, WarbossTop, FortressWidth - 2f * StageInset, StageHeight), fortressOpen ? Steel.Kind.Hot : Steel.Kind.Off);
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
                    BoxOf(box, node == front ? Steel.Kind.CardGold : node < front ? Steel.Kind.Recess : Steel.Kind.Off);
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
        // The concept frame of OPTIONS and the WARFRONT page, dimmed over the scene; everything else lives in its stage.
        _frame = new WarfrontFrame("WarfrontHubFrame", "War map");
        AddChild(_frame);
        _stage = _frame.Stage;

        _header = Text("WarfrontHeader", Left, 0f, HeadingSize, 1200f, WarfrontStyle.Gold);
        _map = new MapDraw(model);
        Place(_map, 0f, 0f, WarfrontFrame.StageW, WarfrontFrame.StageH);
        _stage.AddChild(_map);

        _hq = Text("WarfrontHq", Left + 12f, MapTop + BoxTop + 8f, BodySize, HqWidth - 20f, null, 2);
        _fortress = Text("WarfrontFortress", FortressLeft + 12f, MapTop + BoxTop + 8f, BodySize, FortressWidth - 20f, lines: 2);
        string[] stages = { "WALL", "KEEP", "CAVES" };
        for (int i = 0; i < 3; i++) { _stages[i] = Text("WarfrontStage" + i, Left + StageInset + 12f, StageTop(i) + 10f, BodySize, HqWidth - 2f * StageInset - 20f); _stages[i].Text = stages[i]; }
        _warboss = Text("WarfrontWarboss", FortressLeft + StageInset + 12f, WarbossTop + 10f, BodySize, FortressWidth - 2f * StageInset - 20f);
        _warboss.Text = "WARBOSS";
        for (int lane = 0; lane < 3; lane++)
        {
            float y = MapTop + lane * LanePitch;
            _laneNames[lane] = Text("WarfrontLane" + lane, TextColumn, y - 2f, HeadingSize, TextWidth, WarfrontStyle.Gold);
            for (int node = 1; node <= 3; node++)
                _nodes[lane, node - 1] = Text($"WarfrontNode{lane}{node}", NodeLeft + (node - 1) * NodeStep + 12f, y + BoxTop + 10f, BodySize, NodeWidth - 20f, null, 2);
            _status[lane] = Text("WarfrontStatus" + lane, TextColumn, y + 36f, BodySize, TextWidth);
            _last[lane] = Text("WarfrontLast" + lane, TextColumn, y + 64f, BodySize, TextWidth);
            _status[lane].MouseFilter = MouseFilterEnum.Pass;   // the floor rule is this lane''s hover hint, not a line (FloorLabel keeps its text, hidden)
            _floor[lane] = Text("WarfrontFloor" + lane, TextColumn, y + 64f, BodySize, TextWidth);
            _floor[lane].Visible = false;
            _laneNames[lane].Text = WarfrontHubModel.LaneName(lane);
        }
        for (int row = 0; row < WarfrontHubModel.Rows; row++)
        {
            int r = row;
            var b = UiKit.MenuButton("Deploy", () => OnRow(r));
            b.Name = "WarfrontDeploy" + row;
            UiKit.TextKind(b, "description");   // the target the row deploys to (lane, node, place)
            Place(b, Left, RowsTop + row * RowPitch, RowsWidth, RowPitch - 2f);
            WarfrontStyle.Plate(b, HeadingSize, RowsWidth);   // centred, inset from the bevel (R1), like the WARFRONT page''s plates
            _stage.AddChild(b);
            _rows[row] = b;
        }
        _note = Text("WarfrontNote", SideLeft + SideWidth + 10f, RowsTop, BodySize, WarfrontFrame.StageW - SideLeft - SideWidth - 10f, null, 2);
        _note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        UiKit.TextKind(_note, "progress");   // who is choosing, what the leader does next
        _partyHeading = Text("WarfrontPartyHeading", SideLeft, RowsTop, HeadingSize, SideWidth, WarfrontStyle.Gold);
        _partyHeading.Text = "PARTY";
        for (int i = 0; i < PartyRows; i++) _party[i] = Text("WarfrontParty" + i, SideLeft, RowsTop + 40f + i * 28f, BodySize, SideWidth);
        if (close is not null)
        {
            _closeButton = UiKit.MenuButton("Close", () => _close?.Invoke());
            UiTooltip.Attach(_closeButton, "Back to the Thunderhawk (Esc, or B on a controller)");
            Place(_closeButton, WarfrontFrame.StageW - 300f, WarfrontFrame.StageH - RowPitch, 300f, RowPitch - 2f);
            WarfrontStyle.Plate(_closeButton, HeadingSize, 300f);
            _stage.AddChild(_closeButton);
        }
        model.Changed += Refresh;
        TreeExited += () => model.Changed -= Refresh;
        Refresh();
    }

    /// <summary>Hides the line under the rows (the read-only look hides it as the leader''s line is not true there).</summary>
    public bool NoteSuppressed { get; set; }
    static void Place(Control c, float x, float y, float w, float h)
    {
        c.SetAnchorsPreset(LayoutPreset.TopLeft);
        c.Position = new Vector2(x, y);
        c.Size = new Vector2(w, h);
    }

    Label Text(string name, float x, float y, int size, float width, Color? ink = null, int lines = 1)
    {
        var l = UiKit.MenuText("", null, clip: false);
        l.Name = name;
        WarfrontStyle.Skin(l, size, ink);
        Place(l, x, y, width, lines * (size + 12f));
        _stage.AddChild(l);
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
            _status[lane].TooltipText = m.FloorText(lane);
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
            if (m.RowOpen(row) && !can) b.AddThemeColorOverride("font_disabled_color", WarfrontStyle.Ink);
            else b.RemoveThemeColorOverride("font_disabled_color");
        }
        WireFocus();
        Set(_note, m.Note);
        _note.Visible = !NoteSuppressed && m.Note != YouLeadLine;
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
