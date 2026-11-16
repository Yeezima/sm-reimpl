using System;
using System.Collections.Generic;
using Godot;
using SmClient.Customizer;
using SmClient.Extras.Warfront.Progression;

namespace SmClient.Extras.Warfront.Coop;

/// <summary>
/// The join panel aboard the Thunderhawk (slice W12; ours). Two boxes, each a crisp frame of equal width on all four sides drawn as four rectangles (never an outline stroke) with live labels inside, in the hub's two sizes:
/// the player's own (bottom centre; while it awaits its class: "YOU JOIN AT LEVEL 6" and the customizer line with the gate's clock; while it awaits its cards: "CHOOSE YOUR CARDS", "3 LEFT"), and the leader's awaited list
/// (top right, over the war map's header; "WAITING FOR" and one row per awaited player). No painted labels. Fed by a <see cref="WarfrontCoopModel"/>.
/// </summary>
public sealed partial class WarfrontJoinPanel : Control
{
    public const float OwnWidth = 980f, OwnHeight = 176f, OwnTop = 840f, Pad = 28f;
    public const float ListLeft = 1240f, ListTop = 40f, ListWidth = 560f, ListRowPitch = 34f, ListRowsTop = 76f, ListReasonX = 330f, ListBottomPad = 24f;
    public const int ListRows = 4;
    /// <summary>The box is sized to its rows (the list never shows more than <see cref="ListRows"/>).</summary>
    public float ListHeight => ListRowsTop + Math.Max(1, _model.ShowAwaited ? _model.Section.Awaited.Length : 1) * ListRowPitch + ListBottomPad - 6f;
    public static float OwnLeft => (UiKit.VirtualWidth - OwnWidth) * 0.5f;

    readonly WarfrontCoopModel _model;
    readonly Label _heading, _body, _hint, _listHeading;
    readonly Label[] _rows = new Label[ListRows], _reasons = new Label[ListRows];
    readonly Frames _frames;

    public Label HeadingLabel => _heading;
    public Label BodyLabel => _body;
    public Label HintLabel => _hint;
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

        /// <summary>A frame of equal width on all four sides, on whole pixels: four rectangles, never an outline stroke.</summary>
        static void Frame(CanvasItem c, Rect2 r, float w, Color colour)
        {
            c.DrawRect(new Rect2(r.Position.X, r.Position.Y, r.Size.X, w), colour);
            c.DrawRect(new Rect2(r.Position.X, r.End.Y - w, r.Size.X, w), colour);
            c.DrawRect(new Rect2(r.Position.X, r.Position.Y + w, w, r.Size.Y - 2f * w), colour);
            c.DrawRect(new Rect2(r.End.X - w, r.Position.Y + w, w, r.Size.Y - 2f * w), colour);
        }

        public override void _Draw()
        {
            var fill = new Color(0f, 0f, 0f, 0.78f);
            var line = new Color(1f, 1f, 1f, 0.8f);
            if (_p._model.ShowJoinPanel) { DrawRect(_p.OwnBox, fill); Frame(this, _p.OwnBox, 2f, line); }
            if (_p._model.ShowAwaited) { DrawRect(_p.ListBox, fill); Frame(this, _p.ListBox, 2f, line); }
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
        _heading = Text("WarfrontJoinHeading", OwnLeft + Pad, OwnTop + 24f, WarfrontPickerPanel.HeadingSize, OwnWidth - 2f * Pad);
        _body = Text("WarfrontJoinBody", OwnLeft + Pad, OwnTop + 72f, WarfrontPickerPanel.BodySize, OwnWidth - 2f * Pad);
        _hint = Text("WarfrontJoinHint", OwnLeft + Pad, OwnTop + 112f, WarfrontPickerPanel.BodySize, OwnWidth - 2f * Pad);
        _listHeading = Text("WarfrontAwaitedHeading", ListLeft + Pad, ListTop + 22f, WarfrontPickerPanel.HeadingSize, ListWidth - 2f * Pad);
        for (int i = 0; i < ListRows; i++)
        {
            _rows[i] = Text("WarfrontAwaited" + i, ListLeft + Pad, ListTop + ListRowsTop + i * ListRowPitch, WarfrontPickerPanel.BodySize, ListReasonX - Pad);
            _reasons[i] = Text("WarfrontAwaitedReason" + i, ListLeft + ListReasonX, ListTop + ListRowsTop + i * ListRowPitch, WarfrontPickerPanel.BodySize, ListWidth - ListReasonX - Pad);
        }
        model.Changed += Refresh;
        TreeExited += () => model.Changed -= Refresh;
        Refresh();
    }

    Label Text(string name, float x, float y, int size, float width)
    {
        var l = UiKit.MenuText("", null, clip: false);
        l.Name = name;
        l.AddThemeFontSizeOverride("font_size", size);
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
        Set(_hint, own ? _model.JoinHint : "");
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
