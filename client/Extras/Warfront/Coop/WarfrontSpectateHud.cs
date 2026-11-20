using System;
using Godot;
using SmClient.Customizer;
using SmClient.Extras.Warfront.Progression;

namespace SmClient.Extras.Warfront.Coop;

/// <summary>
/// The spectator HUD of a fallen player past its revive window and of a joiner held aboard (slice W12; ours): "WATCHING NAME" and one line under it (the seconds to the respawn, or "YOU JOIN AT THE NEXT
/// THUNDERHAWK STOP"), plus the leader-change line. Live labels in the table's two sizes (heading and body), all in the concept's lettering, centred in the 1920 by 1080 space; the kit's dark steel strip behind them.
/// Fed by a <see cref="WarfrontCoopModel"/>; shown only while <see cref="WarfrontCoopModel.Spectating"/> (the leader-change line shows whenever there is one).
/// </summary>
public sealed partial class WarfrontSpectateHud : Control
{
    public const float StripTop = 70f, StripHeight = 136f;
    /// <summary>The strip's top while the Battle HUD's line and clock are shown: under the clock's label (ClockTop 138 + heading 28 + 12 of room + a 12 gap), in the 1920 by 1080 space (ours, fallen-watch fix).</summary>
    public const float StripTopUnderBattle = 190f;
    /// <summary>Asked each frame: whether the Battle HUD's line is on screen (set by the networked view). Null: never.</summary>
    public Func<bool>? BattleLineShown { get; set; }
    float _top = StripTop;
    /// <summary>The strip's current top in the design space (for the self-test and the stills).</summary>
    public float Top => _top;
    readonly WarfrontCoopModel _model;
    readonly Label _watching, _sub, _keys, _announce;
    readonly StripDraw _strip;

    /// <summary>The dark steel strip of the kit (the scoreboard's header strip) across the screen.</summary>
    sealed partial class StripDraw : Control
    {
        public StripDraw() { Name = "WarfrontSpectateStrip"; MouseFilter = MouseFilterEnum.Ignore; }
        public override void _Draw() => WarfrontStyle.Strip(this, new Rect2(Vector2.Zero, Size));
    }

    public Label WatchingLabel => _watching;
    public Label SubLabel => _sub;
    public Label KeysLabel => _keys;
    public Label AnnounceLabel => _announce;

    public WarfrontSpectateHud(WarfrontCoopModel model)
    {
        Name = "WarfrontSpectateHud";
        _model = model;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _strip = new StripDraw { Position = new Vector2(0f, StripTop), Size = new Vector2(UiKit.VirtualWidth, StripHeight) };
        AddChild(_strip);
        _watching = Line("WarfrontWatching", StripTop + 8f, WarfrontPickerPanel.HeadingSize, WarfrontStyle.Gold);
        _sub = Line("WarfrontSpectateSub", StripTop + 56f, WarfrontPickerPanel.BodySize);
        _keys = Line("WarfrontSpectateKeys", StripTop + 94f, WarfrontPickerPanel.BodySize);
        _announce = Line("WarfrontLeaderLine", StripTop + StripHeight + 14f, WarfrontPickerPanel.BodySize);
        model.Changed += Refresh;
        TreeExited += () => model.Changed -= Refresh;
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (Visible) Layout();
    }

    void Layout()
    {
        float top = BattleLineShown?.Invoke() == true ? StripTopUnderBattle : StripTop;
        if (top == _top) return;
        _top = top;
        _strip.Position = new Vector2(0f, top);
        _watching.Position = new Vector2(0f, top + 8f);
        _sub.Position = new Vector2(0f, top + 56f);
        _keys.Position = new Vector2(0f, top + 94f);
        _announce.Position = new Vector2(0f, top + StripHeight + 14f);
    }

    Label Line(string name, float y, int size, Color? ink = null)
    {
        var l = UiKit.MenuText("", null, clip: false);
        l.Name = name;
        WarfrontStyle.Skin(l, size, ink, outline: true);
        l.HorizontalAlignment = HorizontalAlignment.Center;
        l.Position = new Vector2(0f, y);
        l.Size = new Vector2(UiKit.VirtualWidth, size + 12f);
        AddChild(l);
        return l;
    }

    public void Refresh()
    {
        bool spectating = _model.Spectating;
        _strip.Visible = spectating;
        Set(_watching, _model.WatchingText);
        Set(_sub, _model.SpectateSubText);
        Set(_keys, _model.WatchKeysText);
        Set(_announce, _model.AnnouncementText);
        _watching.Visible = _sub.Visible = _keys.Visible = spectating;
        Visible = spectating || _model.AnnouncementText.Length > 0;
        Layout();
    }

    static void Set(Label l, string text) { if (l.Text != text) l.Text = text; }
}
