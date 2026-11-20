using System;
using System.Collections.Generic;
using Godot;
using SmClient.Customizer;
using SmClient.Extras.Warfront.Progression;

namespace SmClient.Extras.Warfront.Coop;

/// <summary>
/// The join panel aboard the Thunderhawk (slice W12; ours). Two boxes, each the kit's recessed steel table (<see cref="WarfrontStyle"/>, the scoreboard's) with live labels inside in the concept's lettering and the table's two sizes:
/// the player's own (bottom centre; while it awaits its class: "YOU JOIN AT LEVEL 6" and the customizer line with the gate's clock; while it awaits its cards: "CHOOSE YOUR CARDS", "3 LEFT"), and the leader's awaited list
/// (top right, over the war map's header; "WAITING FOR" and one row per awaited player). No painted labels, no explainer text: where to open the customizer is the own box's hover hint (<see cref="HintText"/>). Fed by a <see cref="WarfrontCoopModel"/>.
/// </summary>
public sealed partial class WarfrontJoinPanel : Control
{
    public const float OwnWidth = 980f, OwnHeight = 128f, OwnTop = 888f, Pad = 28f;
    public const float ListLeft = 1240f, ListTop = 40f, ListWidth = 560f, ListRowPitch = 38f, ListRowsTop = 78f, ListReasonX = 330f, ListBottomPad = 24f;
    public const int ListRows = 4;
    /// <summary>The box is sized to its rows (the list never shows more than <see cref="ListRows"/>).</summary>
    public float ListHeight => ListRowsTop + Math.Max(1, _model.ShowAwaited ? _model.Section.Awaited.Length : 1) * ListRowPitch + ListBottomPad - 6f;
    public static float OwnLeft => (UiKit.VirtualWidth - OwnWidth) * 0.5f;

    readonly WarfrontCoopModel _model;
    readonly Label _heading, _body, _listHeading;
    readonly Control _ownHit;
    readonly Label[] _rows = new Label[ListRows], _reasons = new Label[ListRows];
    readonly Frames _frames;

    public Label HeadingLabel => _heading;
    public Label BodyLabel => _body;
    /// <summary>The own box's hover hint (how to reach the customizer; empty when the panel has no such step).</summary>
    public string HintText => _ownHit.TooltipText;
    public Label ListHeadingLabel => _listHeading;
    public Label RowLabel(int i) => _rows[i];
    public Label ReasonLabel(int i) => _reasons[i];
    public Rect2 OwnBox => new(OwnLeft, OwnTop, OwnWidth, OwnHeight);
    public Rect2 ListBox => new(ListLeft, ListTop, ListWidth, MathF.Round(ListHeight));

    public IEnumerable<Label> Labels()
    {
        foreach (var n in GetChildren()) if (n is Label l) yield return l;
    }

    sealed partial class Frames : Control
    {
        readonly WarfrontJoinPanel _p;
        public Frames(WarfrontJoinPanel p) { _p = p; Name = "WarfrontJoinFrames"; MouseFilter = MouseFilterEnum.Ignore; SetAnchorsPreset(LayoutPreset.FullRect); }

        public override void _Draw()
        {
            if (_p._model.ShowJoinPanel) WarfrontStyle.Table(this, _p.OwnBox);
            if (_p._model.ShowAwaited) WarfrontStyle.Table(this, _p.ListBox);
        }
    }

    public WarfrontJoinPanel(WarfrontCoopModel model)
    {
        Name = "WarfrontJoinPanel";
        _model = model;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _frames = new Frames(this);
        AddChild(_frames);
        _ownHit = new Control { Name = "WarfrontJoinOwnHit", MouseFilter = MouseFilterEnum.Pass, Position = OwnBox.Position, Size = OwnBox.Size };
        AddChild(_ownHit);
        _heading = Text("WarfrontJoinHeading", OwnLeft + Pad, OwnTop + 22f, WarfrontPickerPanel.HeadingSize, OwnWidth - 2f * Pad, WarfrontStyle.Gold);
        _body = Text("WarfrontJoinBody", OwnLeft + Pad, OwnTop + 72f, WarfrontPickerPanel.BodySize, OwnWidth - 2f * Pad);
        UiKit.TextKind(_body, "progress");   // the customizer line carries the gate's clock
        _listHeading = Text("WarfrontAwaitedHeading", ListLeft + Pad, ListTop + 22f, WarfrontPickerPanel.HeadingSize, ListWidth - 2f * Pad, WarfrontStyle.Gold);
        for (int i = 0; i < ListRows; i++)
        {
            _rows[i] = Text("WarfrontAwaited" + i, ListLeft + Pad, ListTop + ListRowsTop + i * ListRowPitch, WarfrontPickerPanel.BodySize, ListReasonX - Pad);
            _reasons[i] = Text("WarfrontAwaitedReason" + i, ListLeft + ListReasonX, ListTop + ListRowsTop + i * ListRowPitch, WarfrontPickerPanel.BodySize, ListWidth - ListReasonX - Pad);
        }
        model.Changed += Refresh;
        TreeExited += () => model.Changed -= Refresh;
        Refresh();
    }

    Label Text(string name, float x, float y, int size, float width, Color? ink = null)
    {
        var l = UiKit.MenuText("", null, clip: false);
        l.Name = name;
        WarfrontStyle.Skin(l, size, ink);
        l.Position = new Vector2(x, y);
        l.Size = new Vector2(width, size + 8f);
        AddChild(l);
        return l;
    }

    public void Refresh()
    {
        bool own = _model.ShowJoinPanel, list = _model.ShowAwaited;
        Set(_heading, own ? _model.JoinHeading : "");
        Set(_body, own ? _model.JoinBody : "");
        Set(_listHeading, list ? _model.AwaitedHeading : "");
        UiTooltip.Attach(_ownHit, own ? _model.JoinHint : "");
        _ownHit.Visible = own;
        var rows = _model.AwaitedRows();
        for (int i = 0; i < ListRows; i++)
        {
            Set(_rows[i], list && i < rows.Count ? rows[i].Name : "");
            Set(_reasons[i], list && i < rows.Count ? rows[i].Reason : "");
        }
        Visible = own || list;
        _frames.QueueRedraw();
    }

    static void Set(Label l, string text) { if (l.Text != text) l.Text = text; }
}
