using System;
using System.Globalization;
using Godot;
using SmClient.Customizer;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The war map (design section G): full screen in the menus' style (<c>MenuKit.cs</c>: DejaVu Sans Bold, transparent text buttons, a brightness change on hover and focus): the three fronts with their
/// pips, progress bars, strengths, gather time and lords, the HQ, the war's clock and scale, the reserves, and a DEPLOY row per front. With a <c>deploy</c> action it is the Destination station's screen:
/// keyboard, mouse and controller, Esc or B closes it. Without one it is the M overlay: no buttons, it takes no input, and in a battle the <see cref="TfwBattlePanel"/> stands in the DEPLOY column. Fed by <see cref="Show"/>; a text is set only when it changes. Laid out in the
/// 1920 by 1080 space from the top left, ours.
/// </summary>
public sealed partial class WarMapPanel : Control
{
    const float Left = UiKit.MenuLeft, Top = 232f, LaneHeight = 204f, BarWidth = 860f, BarHeight = 40f, DeployColumn = Left + BarWidth + 56f;
    static readonly Color HereGold = new(0.98f, 0.8f, 0.12f);

    /// <summary>A front's pips (-1 to 3) and its progress, from the middle: to the right the Imperials are winning, to the left the Orks.</summary>
    sealed partial class LaneBar : Control
    {
        public int Pips;
        public float Progress;

        public override void _Draw()
        {
            const float pip = 22f, gap = 6f;
            int count = WarRules.FrontStronghold - WarRules.FrontBesieged;
            for (int i = 0; i < count; i++)
            {
                var box = new Rect2(i * (pip + gap), 0f, pip, Size.Y);
                // steel pips: a lit gold lamp or a dark recess with a bronze rim
                DrawRect(box, i < Pips ? new Color(0.95f, 0.78f, 0.22f) : new Color(0.05f, 0.045f, 0.04f));
                DrawRect(box, new Color(0.45f, 0.38f, 0.26f, 0.9f), false, 1.5f);
            }
            float x = count * (pip + gap) + 18f, w = Size.X - x, mid = x + w / 2f, h = Size.Y;
            DrawRect(new Rect2(x, 0f, w, h), new Color(0.05f, 0.045f, 0.04f));
            float fill = Math.Clamp(Progress, -1f, 1f) * w / 2f;
            if (fill >= 0f) DrawRect(new Rect2(mid, 2f, fill, h - 4f), new Color(0.95f, 0.8f, 0.2f));
            else DrawRect(new Rect2(mid + fill, 2f, -fill, h - 4f), new Color(0.78f, 0.12f, 0.09f));
            DrawRect(new Rect2(x, 0f, w, h), new Color(0.45f, 0.38f, 0.26f, 0.9f), false, 2f);
            DrawRect(new Rect2(mid - 1f, -4f, 2f, h + 8f), UiKit.White);
        }
    }

    sealed record Row(Label Name, Label Lord, LaneBar Bar, Label Status, Label Strengths, Button? Deploy);

    readonly Action<TfwLane>? _deploy;
    readonly Action? _close;
    readonly Row[] _rows = new Row[3];
    readonly Label _clock, _reserves, _note;
    readonly Button? _closeButton;
    readonly TfwBattlePanel? _battle;
    TfwWarView? _shown;
    bool _shownPlaying;

    /// <summary>Whether this is the station's screen (false: the M overlay).</summary>
    public bool Interactive => _deploy is not null;
    /// <summary>The CLOSE row (null on the overlay).</summary>
    public Button? CloseButton => _closeButton;
    public Button? DeployButton(TfwLane lane) => _rows[(int)lane].Deploy;
    /// <summary>The battle's panel (null on the station's screen, which only opens in the hub).</summary>
    public TfwBattlePanel? Battle => _battle;
    /// <summary>What the map says under the rows (why a Deploy cannot be asked for).</summary>
    public string Note => _note.Text;

    public WarMapPanel(Action<TfwLane>? deploy, Action? close)
    {
        Name = "WarMapPanel";
        _deploy = deploy;
        _close = close;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = deploy is null ? MouseFilterEnum.Ignore : MouseFilterEnum.Stop;
        var title = new Label { Name = "WarMapTitle", Text = "WAR MAP", MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(Left - 3.5f, 52f) };
        if (deploy is not null)
        {
            // The station's screen (a menu) wears the redesigned steel board and plate; the M overlay in a battle keeps its plain dim (it is part of the play view).
            title.Position = new Vector2(Left + 30f, 70f);
            SteelBackdrop.StyleTitle(title);
            _steel = true;
            AddChild(new SteelBackdrop("WarMapDim", title, 0.7f) { FitContent = true });
        }
        else
        {
            var dim = new ColorRect { Name = "WarMapDim", Color = new Color(0f, 0f, 0f, 0.62f), MouseFilter = MouseFilterEnum.Ignore };
            dim.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(dim);
            title.AddThemeFontOverride("font", UiKit.TitleFont);
            title.AddThemeFontSizeOverride("font_size", UiType.Px(UiType.Title));
            title.AddThemeColorOverride("font_color", UiKit.White);
        }
        AddChild(title);
        _clock = Text("WarMapClock", Left, 172f, 22, 1400f);
        _reserves = Text("WarMapReserves", Left, 204f, 22, 1400f);
        for (int l = 0; l < 3; l++) _rows[l] = MakeRow((TfwLane)l, Top + 30f + l * LaneHeight);
        _note = Text("WarMapNote", Left, Top + 30f + 3 * LaneHeight, 22, 1400f);
        if (deploy is null)
        {
            _battle = new TfwBattlePanel { Name = "TfwBattlePanel", Visible = false };
            Place(_battle, DeployColumn, Top + 30f, TfwBattlePanel.PanelWidth, TfwBattlePanel.PanelMaxHeight);
            AddChild(_battle);
            return;
        }
        _closeButton = UiKit.MenuButton("Close", () => _close?.Invoke(), "Back to the Stormbird (Esc, or B on a controller)");
        Place(_closeButton, Left, Top + 76f + 3 * LaneHeight, 240f, 30f);
        _closeButton.AddThemeFontSizeOverride("font_size", UiType.Px(UiType.Plate));
        _closeButton.Size = new Vector2(300f, 56f);
        SteelBackdrop.StylePlate(_closeButton);
        AddChild(_closeButton);
    }

    static void Place(Control c, float x, float y, float w, float h)
    {
        c.SetAnchorsPreset(LayoutPreset.TopLeft);
        c.Position = new Vector2(x, y);
        c.Size = new Vector2(w, h);
    }

    Label Text(string name, float x, float y, int size, float width)
    {
        var l = UiKit.MenuText("", null, clip: false);
        l.Name = name;
        l.AddThemeFontSizeOverride("font_size", UiType.Px(UiType.Level(size)));
        if (_steel) SteelBackdrop.StyleLine(l);
        Place(l, x, y, width, size + 8f);
        AddChild(l);
        return l;
    }

    bool _steel;

    Row MakeRow(TfwLane lane, float y)
    {
        var name = Text("Lane" + lane, Left, y, 32, 300f);
        var lord = Text("Lord" + lane, Left + 300f, y + 10f, 22, 560f);
        var bar = new LaneBar { Name = "Bar" + lane, MouseFilter = MouseFilterEnum.Ignore };
        Place(bar, Left, y + 52f, BarWidth, BarHeight);
        AddChild(bar);
        var status = Text("Status" + lane, Left, y + 104f, 22, BarWidth);
        var strengths = Text("Strengths" + lane, Left, y + 138f, 22, BarWidth);
        Button? deploy = null;
        if (_deploy is { } act)
        {
            deploy = UiKit.MenuButton("Deploy " + lane, () => act(lane));
            deploy.AddThemeFontSizeOverride("font_size", UiType.Px(UiType.Plate)); SteelBackdrop.StylePlate(deploy);
            Place(deploy, DeployColumn, y + 46f, 420f, 56f);
            AddChild(deploy);
        }
        return new Row(name, lord, bar, status, strengths, deploy);
    }

    static void Set(Label l, string text) { if (l.Text != text) l.Text = text; }

    /// <summary>The focus on the first DEPLOY that can be pressed, else CLOSE.</summary>
    public void Focus()
    {
        foreach (var r in _rows) if (r.Deploy is { Disabled: false } b) { b.FocusLater(); return; }
        _closeButton?.FocusLater();
    }

    /// <summary>The war as it stands; nothing moves unless the record changed.</summary>
    public void Show(in TfwWarView war, bool playing)
    {
        if (_shown is { } was && was == war && _shownPlaying == playing) return;
        _shown = war;
        _shownPlaying = playing;
        var inv = CultureInfo.InvariantCulture;
        Set(_clock, string.Create(inv, $"WAR {TfwHudModel.ClockText(war.Clock)}    {TfwHudModel.ScaleText(war.TimeScale)}    {TfwHudModel.HqText(war)}"));
        Set(_reserves, $"TRANSPORT {war.Transport}    SUPPLIES {war.Supplies}");
        bool can = TfwHudModel.CanDeploy(war, playing);
        Set(_note, TfwHudModel.DeployNote(war, playing));
        for (int l = 0; l < 3; l++)
        {
            var row = _rows[l];
            var state = TfwHudModel.Lane(war, (TfwLane)l);
            Set(row.Name, $"FRONT {(TfwLane)l}");
            if (TfwHudModel.InBattle(war.Phase) && war.BattleLane == (TfwLane)l) row.Name.AddThemeColorOverride("font_color", HereGold);   // the front of this battle
            else row.Name.RemoveThemeColorOverride("font_color");
            Set(row.Lord, TfwHudModel.LordText((TfwLane)l, state.LordState));
            int pips = TfwHudModel.Pips(state.Front);
            if (row.Bar.Pips != pips || row.Bar.Progress != state.Progress) { row.Bar.Pips = pips; row.Bar.Progress = state.Progress; row.Bar.QueueRedraw(); }
            string gather = state.GatherLeft > 0f ? string.Create(inv, $"    GATHERING {MathF.Ceiling(state.GatherLeft):0} S") : "";
            Set(row.Status, TfwHudModel.FrontText(state.Front) + gather);
            Set(row.Strengths, string.Create(inv, $"IMPERIAL {state.Imperial:0}    ORK {state.Ork:0}"));
            if (row.Deploy is { } b)
            {
                b.Text = TfwHudModel.IsFinal(state) ? "FINAL ASSAULT " + (TfwLane)l : "DEPLOY " + (TfwLane)l;
                b.Disabled = !can;
            }
        }
    }

    public override void _Input(InputEvent e)
    {
        if (_close is null || !IsVisibleInTree() || !SmClient.PadHost.IsBack(e)) return;
        GetViewport().SetInputAsHandled();
        _close();
    }
}
