using Godot;
using SmNet;

namespace SmClient;

/// <summary>
/// The loading screen (the owner, 2026-11-07: "add a loading screen that replaces the 'connecting to IP address' when loading into musou mode or classic pvp mode. its just straight black
/// and says LOADING in the center."): plain black over everything with LOADING in the middle, in the HUD's face (Bahnschrift, the closest system face to the original's), while the view
/// reads its files and while the client waits for the server. No address and no other text. Gone once connected; an error (a refusal, no answer, a load error) shows the error as before.
/// </summary>
public partial class NetworkView
{
    CanvasLayer? _loadingLayer;

    /// <summary>The text of the loading screen.</summary>
    public const string LoadingText = "LOADING";

    /// <summary><c>--loading-shot</c> (screenshot aid): a screenshot is taken once the loading screen has been up 1.5 s (join an address with no server: it stays up for the 10 s connect timeout).</summary>
    static readonly bool LoadingShot = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--loading-shot") >= 0;
    bool LoadingWorthCapturing => LoadingShot && LoadingShown && _elapsed > 1.5;

    /// <summary>Whether the loading screen is up: no error, and the client not started yet or still connecting.</summary>
    public bool LoadingShown => _loadingLayer is { Visible: true };

    void AddLoadingScreen()
    {
        if (_bot) return;   // headless bots draw nothing
        _loadingLayer = new CanvasLayer { Name = "Loading", Layer = 60 };   // above the HUD, the menus' layers and the join's back hint (50)
        var black = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
        black.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _loadingLayer.AddChild(black);
        var system = new SystemFont { FontNames = ["Bahnschrift", "DIN Alternate", "Arial Narrow"], Fallbacks = [ThemeDB.FallbackFont] };
        var ts = TextServerManager.GetPrimaryInterface();
        var font = new FontVariation { BaseFont = system, VariationOpentype = new Godot.Collections.Dictionary { { ts.NameToTag("wght"), 600 }, { ts.NameToTag("wdth"), 92 } }, SpacingGlyph = 6 };
        var label = new Label
        {
            Text = LoadingText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", Colors.White);
        _loadingLayer.AddChild(label);
        AddChild(_loadingLayer);
        _loadingSizeFor = -1f;
        UpdateLoadingScreen();
    }

    float _loadingSizeFor;

    /// <summary>Per frame: shown while loading or connecting, hidden on connect or on an error; the text scales with the window (40 px at 1080).</summary>
    void UpdateLoadingScreen()
    {
        if (_loadingLayer is null) return;
        bool show = _error == "" && (_client is null || _client.Status == ClientStatus.Connecting);
        if (_loadingLayer.Visible != show) _loadingLayer.Visible = show;
        if (!show) return;
        float height = GetViewport().GetVisibleRect().Size.Y;
        if (height == _loadingSizeFor || _loadingLayer.GetChildCount() < 2 || _loadingLayer.GetChild(1) is not Label label) return;
        _loadingSizeFor = height;
        label.AddThemeFontSizeOverride("font_size", Mathf.Max(18, Mathf.RoundToInt(height / 1080f * 40f)));
    }
}
