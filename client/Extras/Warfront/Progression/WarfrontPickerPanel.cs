using Godot;
using SmClient.Customizer;
using SmSim.Extras.Warfront.Progression;

namespace SmClient.Extras.Warfront.Progression;

/// <summary>
/// The card picker (slice W8; brief "Behaviour" 3 to 5): up to six cards in a row (every stat card and +1 Retainer; owner 2026-11-18) over a dimmed field, shown while the model's offer stands and hidden when it closes. Each card: its slot number, name, one line of effect
/// and how many are already owned; a countdown bar from the wire's seconds left, "CARD 1 IS TAKEN WHEN THE TIME RUNS OUT" and "YOU CANNOT BE HARMED WHILE CHOOSING" (the protection is the server's; the client does not
/// block its own input, the server drops it). Input: keys 1 to 6 pick at once (the panel marks them handled, so they do not also switch weapons here); D-pad left and right move the highlight, D-pad down or A
/// takes it, D-pad up does nothing; a click on a card picks it. Text styles: heading 28 and body 20, every label live, boxes and bars drawn with crisp equal frames. Laid out in the 1920 by 1080 space, ours.
/// </summary>
public sealed partial class WarfrontPickerPanel : Control
{
    public const int HeadingSize = WarfrontStyle.Heading, BodySize = WarfrontStyle.Body;
    /// <summary>Card slots: all five stat cards and +1 Retainer (owner 2026-11-18); an offer without the Retainer leaves the sixth empty.</summary>
    public const int Cards = 6;
    public const float Left = 164f, CardWidth = 252f, CardHeight = 320f, CardGap = 16f, CardTop = 310f, HeaderTop = 250f;
    public const float BarTop = 640f, BarHeight = 16f, NotesTop = 676f, NoteStep = 34f;
    public static float CardLeft(int i) => Left + i * (CardWidth + CardGap);
    public static float RowWidth => Cards * CardWidth + (Cards - 1) * CardGap;

    readonly WarfrontProgressModel _model;
    readonly Label _header, _protected, _seconds;
    readonly Label[] _slot = new Label[Cards], _name = new Label[Cards], _effect = new Label[Cards], _owned = new Label[Cards];
    readonly Control[] _hit = new Control[Cards];
    readonly Draw _draw;

    public WarfrontProgressModel Model => _model;

    /// <summary>False keeps the panel hidden and deaf to input whatever the wire says (a live server, until W5c's PickerBusy hook keeps keys 1 to 4 from weapon switching).</summary>
    public bool Enabled { get; set; } = true;
    public Label HeaderLabel => _header;
    public Label SlotLabel(int i) => _slot[i];
    public Label NameLabel(int i) => _name[i];
    public Label EffectLabel(int i) => _effect[i];
    public Label OwnedLabel(int i) => _owned[i];
    public Control CardHit(int i) => _hit[i];
    /// <summary>What happens when the time runs out: the countdown's hover hint (not text on the screen).</summary>
    public string TimeoutHint => _seconds.TooltipText;
    public Label ProtectedLabel => _protected;

    public System.Collections.Generic.IEnumerable<Label> Labels()
    {
        foreach (var n in GetChildren()) if (n is Label l) yield return l;
    }

    sealed partial class Draw : Control
    {
        readonly WarfrontProgressModel _m;
        public Draw(WarfrontProgressModel m) { _m = m; Name = "WarfrontPickerDraw"; MouseFilter = MouseFilterEnum.Ignore; }

        public override void _Draw()
        {
            for (int i = 0; i < Cards; i++)
            {
                if (_m.Cards[i] == WarfrontCard.None) continue;   // no Retainer offered: the sixth slot is empty
                var box = new Rect2(CardLeft(i), CardTop, CardWidth, CardHeight);
                bool hi = _m.Highlight == i;
                bool sent = _m.Sent == i;
                WarfrontStyle.Card(this, box, hi || sent);
            }
            WarfrontProgressDraw.Bar(this, new Rect2(Left, BarTop, RowWidth, BarHeight), _m.TimeFraction, WarfrontProgressDraw.Gold);
        }
    }

    public WarfrontPickerPanel(WarfrontProgressModel model)
    {
        Name = "WarfrontPickerPanel";
        _model = model;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        var dim = new ColorRect { Name = "WarfrontPickerDim", Color = new Color(0f, 0f, 0f, 0.6f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        _draw = new Draw(model);
        _draw.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_draw);

        var white = WarfrontStyle.Ink;
        _header = WarfrontProgressDraw.Text(this, "WarfrontPickerHeader", Left, HeaderTop, 900f, HeadingSize, white);
        for (int i = 0; i < Cards; i++)
        {
            int slot = i;
            float x = CardLeft(i) + 20f, w = CardWidth - 40f;
            _slot[i] = WarfrontProgressDraw.Text(this, "WarfrontPickerSlot" + i, x, CardTop + 16f, w, HeadingSize, WarfrontProgressDraw.Gold);
            _name[i] = WarfrontProgressDraw.Text(this, "WarfrontPickerName" + i, x, CardTop + 64f, w, HeadingSize, white);
            _effect[i] = WarfrontProgressDraw.Text(this, "WarfrontPickerEffect" + i, x, CardTop + 124f, w, BodySize, white, lines: 4);
            UiKit.TextKind(_effect[i], "description");   // the card's own effect text
            _owned[i] = WarfrontProgressDraw.Text(this, "WarfrontPickerOwned" + i, x, CardTop + CardHeight - 44f, w, BodySize);
            var hit = new Control { Name = "WarfrontPickerHit" + i, MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand };
            hit.SetAnchorsPreset(LayoutPreset.TopLeft);
            hit.Position = new Vector2(CardLeft(i), CardTop);
            hit.Size = new Vector2(CardWidth, CardHeight);
            hit.MouseEntered += () => _model.SetHighlight(slot);
            hit.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) _model.Pick(slot); };
            AddChild(hit);
            _hit[i] = hit;
        }
        _protected = WarfrontProgressDraw.Text(this, "WarfrontPickerProtected", Left, NotesTop, 900f, BodySize, WarfrontStyle.Dim);
        _protected.Visible = false;   // not text on the screen: the protection rides in the cards' and the header's hover hint (ProtectedLabel keeps the words)
        _seconds = WarfrontProgressDraw.Text(this, "WarfrontPickerSeconds", Left + RowWidth - 140f, HeaderTop, 140f, HeadingSize, white, HorizontalAlignment.Right);
        _seconds.MouseFilter = MouseFilterEnum.Pass;
        UiTooltip.Attach(_seconds, WarfrontProgressModel.TimeoutNote);
        _protected.Text = WarfrontProgressModel.ProtectedNote;
        // The keys that pick are a hover hint, not text on the screen (owner, 2026-11-20): on the header and on every card.
        _header.MouseFilter = MouseFilterEnum.Pass;
        string hint = WarfrontProgressModel.HintNote + "\n" + WarfrontProgressModel.ProtectedNote;
        UiTooltip.Attach(_header, hint);
        foreach (var h in _hit) UiTooltip.Attach(h, hint);
        model.Changed += Refresh;
        TreeExited += () => model.Changed -= Refresh;
        Refresh();
    }

    public void Refresh()
    {
        var m = _model;
        bool show = m.OfferOpen && Enabled;
        if (Visible != show) Visible = show;
        if (!show) return;
        WarfrontProgressDraw.Set(_header, m.Header);
        for (int i = 0; i < Cards; i++)
        {
            var c = m.Cards[i];
            bool empty = c == WarfrontCard.None;
            _hit[i].Visible = !empty;
            WarfrontProgressDraw.Set(_slot[i], empty ? "" : (i + 1).ToString());
            WarfrontProgressDraw.Set(_name[i], WarfrontProgressModel.CardName(c));
            WarfrontProgressDraw.Set(_effect[i], WarfrontProgressModel.CardEffect(c) + '\n' + m.CardTotal(c));
            WarfrontProgressDraw.Set(_owned[i], empty ? "" : m.OwnedText(c));
        }
        _draw.QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _draw.QueueRedraw();
        WarfrontProgressDraw.Set(_seconds, Mathf.CeilToInt(_model.SecondsLeft) + " S");
    }

    /// <summary>Keys 1 to 6 and the D-pad while the offer stands; everything the panel uses is marked handled.</summary>
    public override void _Input(InputEvent e)
    {
        if (!Visible || !_model.OfferOpen) return;
        switch (e)
        {
            case InputEventKey { Pressed: true } k:
            {
                int slot = k.PhysicalKeycode switch { Key.Key1 or Key.Kp1 => 0, Key.Key2 or Key.Kp2 => 1, Key.Key3 or Key.Kp3 => 2, Key.Key4 or Key.Kp4 => 3, Key.Key5 or Key.Kp5 => 4, Key.Key6 or Key.Kp6 => 5, _ => -1 };
                if (slot < 0) return;
                if (!k.Echo) _model.Pick(slot);
                GetViewport().SetInputAsHandled();
                break;
            }
            case InputEventJoypadButton { Pressed: true } b:
                switch (b.ButtonIndex)
                {
                    case JoyButton.DpadLeft: _model.MoveHighlight(-1); break;
                    case JoyButton.DpadRight: _model.MoveHighlight(1); break;
                    case JoyButton.DpadDown: case JoyButton.A: _model.ConfirmHighlight(); break;
                    case JoyButton.DpadUp: break;
                    default: return;
                }
                GetViewport().SetInputAsHandled();
                break;
        }
    }
}
