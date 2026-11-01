using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using SmClient.Customizer;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>What the score panels and the cards share: full screen in the menus' style (<c>MenuKit.cs</c>), in the 1920 by 1080 space from the top left, as <see cref="WarMapPanel"/>. Layout ours.</summary>
public abstract partial class TfwPagePanel : Control
{
    protected const float Left = UiKit.MenuLeft;

    protected TfwPagePanel(string name, string title)
    {
        Name = name;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var dim = new ColorRect { Name = name + "Dim", Color = new Color(0f, 0f, 0f, 0.88f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        var head = new Label { Name = name + "Title", Text = title, MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(Left - 3.5f, 52f) };
        head.AddThemeFontOverride("font", UiKit.TitleFont);
        head.AddThemeFontSizeOverride("font_size", UiKit.MenuTitleSize);
        head.AddThemeColorOverride("font_color", UiKit.White);
        AddChild(head);
    }

    protected static void Place(Control c, float x, float y, float w, float h)
    {
        c.SetAnchorsPreset(LayoutPreset.TopLeft);
        c.Position = new Vector2(x, y);
        c.Size = new Vector2(w, h);
    }

    protected Label Line(string name, string text, float x, float y, int size, float width, Color? colour = null)
    {
        var l = UiKit.MenuText(text, colour, clip: false);
        l.Name = name;
        l.AddThemeFontSizeOverride("font_size", size);
        Place(l, x, y, width, size + 8f);
        AddChild(l);
        return l;
    }

    protected Button Row(string text, Action act, float y, int size = 22, float x = Left, float width = 900f)
    {
        var b = UiKit.MenuButton(text, act);
        b.AddThemeFontSizeOverride("font_size", size);
        Place(b, x, y, width, size + 16f);
        AddChild(b);
        return b;
    }

    protected static string Number(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    protected static string MapName(in TfwMapScore m) => $"MANUFACTORUM, FRONT {m.Lane}, NODE {m.Node}";
}

/// <summary>
/// The end of a map (M6b): the map, its KOs, best chain and points and the run's total, every figure from tag 12. Enter, or CONTINUE, goes on to the cards when there are any; shown in the Stormbird before them.
/// </summary>
public sealed partial class TfwScorePanel : TfwPagePanel
{
    readonly Action _close;
    readonly Label _total;
    readonly Button _continue;

    /// <summary>The figures shown, as text (the tests hold them against the server's).</summary>
    public string MapPoints { get; }
    public string RunTotal => _total.Text;
    public Button ContinueButton => _continue;

    public TfwScorePanel(in TfwMapScore map, uint runTotal, Action close) : base("TfwScorePanel", TfwBest.ResultText(map.Result) == "WON" ? "MAP CLEARED" : "MAP " + TfwBest.ResultText(map.Result))
    {
        _close = close;
        MapPoints = Number(map.Points);
        Line("ScoreMap", MapName(map), Left, 146f, 16, 1200f);
        Line("ScoreKosLabel", "KOS", Left, 220f, 18, 300f);
        Line("ScoreKos", Number(map.Kos), Left + 320f, 212f, 30, 400f);
        Line("ScoreChainLabel", "BEST CHAIN", Left, 276f, 18, 300f);
        Line("ScoreChain", Number(map.BestChain), Left + 320f, 268f, 30, 400f);
        Line("ScorePointsLabel", "MAP POINTS", Left, 332f, 18, 300f);
        Line("ScorePoints", MapPoints, Left + 320f, 324f, 30, 400f);
        Line("ScoreTotalLabel", "RUN TOTAL", Left, 400f, 18, 300f);
        _total = Line("ScoreTotal", Number(runTotal), Left + 320f, 388f, 40, 500f, new Color(0.98f, 0.8f, 0.12f));
        _continue = Row("Continue", close, 480f);
    }

    public void Focus() => _continue.FocusLater();

    public override void _Input(InputEvent e)
    {
        if (!IsVisibleInTree() || !PadHost.IsBack(e)) return;
        GetViewport().SetInputAsHandled();
        _close();
    }
}

/// <summary>The reward cards after a won map (owner's item 11): a weapon, an equipment and a perk, each a menu row with what it gives. A pick is final; it goes out with the offer's number (<see cref="TfwCommandRecord.Offer"/>).</summary>
public sealed partial class TfwRewardPanel : TfwPagePanel
{
    readonly Button?[] _cards = new Button?[TfwWire.RewardCards];

    /// <summary>The row of card <paramref name="index"/>, null for an empty card.</summary>
    public Button? Card(int index) => _cards[index];

    public TfwRewardPanel(in TfwRewardOffer offer, Func<TfwRewardCard, string> name, Action<byte> pick) : base("TfwRewardPanel", "CHOOSE A REWARD")
    {
        Line("RewardNote", "ONE FOR THE REST OF THIS RUN. A WEAPON GOES IN KEY 3, 4 OR 5 AND NEVER REPLACES YOUR PRIMARY.", Left, 146f, 16, 1400f);
        ReadOnlySpan<TfwRewardCard> cards = [offer.First, offer.Second, offer.Third];
        for (int k = 0; k < cards.Length; k++)
        {
            byte card = (byte)k;
            float y = 240f + k * 120f;
            var kind = cards[k].Kind;
            Line("RewardKind" + k, kind switch { TfwRewardKind.Weapon => "WEAPON", TfwRewardKind.Equipment => "EQUIPMENT", TfwRewardKind.Perk => "PERK", _ => "" }, Left, y, 16, 400f);
            if (kind != TfwRewardKind.None) _cards[k] = Row(name(cards[k]), () => pick(card), y + 28f, 30);
        }
    }

    public void Focus()
    {
        foreach (var b in _cards) if (b is not null) { b.FocusLater(); return; }
    }

    /// <summary>A card is a choice to make: Esc and B do nothing here, and open nothing either.</summary>
    public override void _Input(InputEvent e)
    {
        if (IsVisibleInTree() && PadHost.IsBack(e)) GetViewport().SetInputAsHandled();
    }
}

/// <summary>
/// The end of a run (M6b): one row per map, the total, the outcome, the humans ranked and the local best, then COPY RESULT. It stays until closed: the server's restart delay (15 s by default) may run out behind it and a new
/// run waits in the Stormbird. Won or lost, the total shows.
/// </summary>
public sealed partial class TfwRunPanel : TfwPagePanel
{
    readonly Action _close;
    readonly Button _closeButton;

    public string Total { get; }
    public IReadOnlyList<string> Rows { get; }
    public Button CopyButton { get; }
    public Button CloseButton => _closeButton;
    /// <summary>The line the panel copies.</summary>
    public string Result { get; }

    public TfwRunPanel(bool won, IReadOnlyList<TfwMapScore> maps, uint total, IReadOnlyList<(string Name, uint Total)> ranked, bool newBest, TfwBestRecord? best, string result, Action copy, Action close)
        : base("TfwRunPanel", won ? "THE WAR IS WON" : "THE WAR IS LOST")
    {
        _close = close;
        Total = Number(total);
        Result = result;
        var rows = new List<string>(maps.Count);
        float y = 150f;
        for (int k = 0; k < maps.Count; k++, y += 28f)
        {
            var m = maps[k];
            rows.Add($"{MapName(m)}   {TfwBest.ResultText(m.Result)}   KOS {Number(m.Kos)}   CHAIN {Number(m.BestChain)}   {Number(m.Points)}");
            Line("RunMap" + k, rows[k], Left, y, 16, 1500f);
        }
        Rows = rows;
        y += 12f;
        Line("RunTotalLabel", "TOTAL", Left, y + 8f, 22, 240f);
        Line("RunTotal", Total, Left + 240f, y, 40, 500f, new Color(0.98f, 0.8f, 0.12f));
        if (newBest) Line("RunNewBest", "NEW BEST", Left + 620f, y + 8f, 22, 300f, new Color(0.35f, 0.85f, 0.4f));
        y += 64f;
        if (best is not null) { Line("RunBest", $"BEST ON THIS PC  {Number(best.Total)}  ({best.Date})", Left, y, 16, 900f); y += 30f; }
        if (ranked.Count > 1)
        {
            for (int k = 0; k < ranked.Count; k++, y += 26f) Line("RunRank" + k, $"{k + 1}.  {ranked[k].Name}   {Number(ranked[k].Total)}", Left, y, 16, 900f);
            y += 8f;
        }
        CopyButton = Row("Copy result", copy, y + 12f);
        _closeButton = Row("Close", close, y + 56f);
    }

    public void Focus() => CopyButton.FocusLater();

    public override void _Input(InputEvent e)
    {
        if (!IsVisibleInTree() || !PadHost.IsBack(e)) return;
        GetViewport().SetInputAsHandled();
        _close();
    }
}
