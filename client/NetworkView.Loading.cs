using Godot;
using SmClient.Customizer;
using SmNet;

namespace SmClient;

/// <summary>
/// The loading screen (the owner, 2026-11-07: "add a loading screen that replaces the 'connecting to IP address' when loading into musou mode or classic pvp mode. its just straight black
/// and says LOADING in the center."): LOADING in the middle, in the HUD's face (Bahnschrift, the closest system face to the original's), over everything while the view reads its files and
/// while the client waits for the server. No address and no other text. Gone once connected; an error (a refusal, no answer, a load error) shows the error as before.
/// Since 2026-11-10 (the owner: "i want the old main menu screen to be the new loading screen background") the picture behind it is the main menu's picture of before 2026-11-09
/// (<see cref="UiKit.LoadingBackgroundPath"/>), covering the window without stretching, under a dim and a vignette so the text stays readable; plain black without the file.
/// The change to the match is a blink (<see cref="ScreenBlink"/>): once connected the loading screen stays while the black closes over it, goes at full black, and the match
/// opens once its first frames have settled.
/// </summary>
public partial class NetworkView
{
    CanvasLayer? _loadingLayer;
    Label? _loadingLabel;
    Label? _loadingCaptionLabel;   // polish pass P2: the load's stage under LOADING (SetLoadingCaption, NetworkView.MapLoad.cs)
    TextureRect? _loadingPicture;
    bool _loadingBlinkAsked, _loadingBlinkClosed;
    double _loadingUpFor;   // seconds the loading screen has been up and clear (each frame counts at most 0.1 s)
    ulong _loadingWaitStartMs;   // when the handshake started waiting for the blink (0: not yet)

    /// <summary>The longest the handshake waits for the blink to open on the loading screen, in real seconds (a blink that never ends must not hold the join).</summary>
    public const double LoadingBlinkWaitSeconds = 2.5;

    /// <summary><c>--loading-linger &lt;s&gt;</c> (capture aid): once connected, the loading screen stays until it has been up and clear that long, as a slow server's join would (0: off).</summary>
    static readonly double LoadingLinger = double.TryParse(CmdArg("loading-linger"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var linger) && linger > 0 ? linger : 0;

    /// <summary>The text of the loading screen.</summary>
    public const string LoadingText = "LOADING";

    /// <summary>The loading screen's layer: above the HUD, the menus' layers (the loadout picker and Musou's modals, 60) and the join's back hint (50), under the blink.</summary>
    public const int LoadingLayer = 100;

    /// <summary>The dim over the picture: black at this opacity in the middle, rising to the edge value at the corners (ours, by eye).</summary>
    public const float LoadingDimCentre = 0.2f, LoadingDimEdge = 0.65f;

    /// <summary><c>--loading-shot</c> (screenshot aid): a screenshot is taken once the loading screen has been up 1.5 s (join an address with no server: it stays up for the 10 s connect timeout).</summary>
    static readonly bool LoadingShot = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--loading-shot") >= 0;
    bool LoadingWorthCapturing => LoadingShot && LoadingShown && _elapsed > 1.5;

    /// <summary>Whether the loading screen is up: no error, and the client not started yet or still connecting (or connected while the blink closes over it).</summary>
    public bool LoadingShown => _loadingLayer is { Visible: true };

    void AddLoadingScreen()
    {
        if (_bot) return;   // headless bots draw nothing
        _loadingLayer = new CanvasLayer { Name = "Loading", Layer = LoadingLayer };
        var black = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
        black.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _loadingLayer.AddChild(black);
        // The picture (not read for a headless run: nothing is drawn).
        if (DisplayServer.GetName() != "headless" && UiKit.LoadingBackgroundImage(GetViewport().GetVisibleRect().Size.Y) is { } picture)
        {
            _loadingPicture = new TextureRect
            {
                Name = "Picture", Texture = picture, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _loadingPicture.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _loadingLayer.AddChild(_loadingPicture);
            var shade = new TextureRect { Name = "Shade", Texture = LoadingShade(), StretchMode = TextureRect.StretchModeEnum.Scale, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = Control.MouseFilterEnum.Ignore };
            shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _loadingLayer.AddChild(shade);
        }
        var system = new SystemFont { FontNames = ["Bahnschrift", "DIN Alternate", "Arial Narrow"], Fallbacks = [ThemeDB.FallbackFont] };
        var ts = TextServerManager.GetPrimaryInterface();
        var font = new FontVariation { BaseFont = system, VariationOpentype = new Godot.Collections.Dictionary { { ts.NameToTag("wght"), 600 }, { ts.NameToTag("wdth"), 92 } }, SpacingGlyph = 6 };
        _loadingLabel = new Label
        {
            Name = "Text", Text = LoadingText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _loadingLabel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _loadingLabel.AddThemeFontOverride("font", font);
        _loadingLabel.AddThemeColorOverride("font_color", Colors.White);
        _loadingLabel.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.6f));   // keeps the white text apart from the picture's light haze
        _loadingLayer.AddChild(_loadingLabel);
        // polish pass P2 (ours): the load's stage in small capitals under LOADING (READING THE MAP, BUILDING COLLISION, DRAWING THE MAP, JOINING ...), fed by the worker.
        _loadingCaptionLabel = new Label
        {
            Name = "Caption", Text = "", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _loadingCaptionLabel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _loadingCaptionLabel.AddThemeFontOverride("font", font);
        _loadingCaptionLabel.AddThemeColorOverride("font_color", new Color(0.82f, 0.82f, 0.82f));
        _loadingCaptionLabel.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.6f));
        _loadingLayer.AddChild(_loadingCaptionLabel);
        AddChild(_loadingLayer);
        _loadingSizeFor = -1f;
        UpdateLoadingScreen();
    }

    /// <summary>The dim and vignette over the picture: a radial gradient, stretched to the window (so an ellipse), from <see cref="LoadingDimCentre"/> to <see cref="LoadingDimEdge"/>.</summary>
    static GradientTexture2D LoadingShade()
    {
        var gradient = new Gradient
        {
            Offsets = [0f, 0.55f, 1f],
            Colors = [new Color(0f, 0f, 0f, LoadingDimCentre), new Color(0f, 0f, 0f, (LoadingDimCentre + LoadingDimEdge) / 2f), new Color(0f, 0f, 0f, LoadingDimEdge)],
        };
        return new GradientTexture2D { Gradient = gradient, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 1f), Width = 256, Height = 256 };
    }

    float _loadingSizeFor;

    /// <summary>
    /// Per frame: shown while loading or connecting, hidden on an error at once; on connecting it stays while the blink closes over it and goes at full black (at once
    /// without the blink). The text scales with the window (40 px at 1080).
    /// </summary>
    void UpdateLoadingScreen()
    {
        if (_loadingLayer is null) return;
        bool loading = _error == "" && (_client is null || _client.Status == ClientStatus.Connecting);
        bool connected = _error == "" && _client is { Status: ClientStatus.Connected };
        if (_loadingLayer.Visible && ScreenBlink.Current is not { Busy: true }) _loadingUpFor += Mathf.Min(GetProcessDeltaTime(), 0.1);
        bool lingering = connected && !_loadingBlinkAsked && _loadingUpFor < LoadingLinger;   // --loading-linger
        if (connected && !_loadingBlinkAsked && !lingering)
        {
            _loadingBlinkAsked = true;
            if (_loadingLayer.Visible && ScreenBlink.Current is { } blink) blink.Run(() => _loadingBlinkClosed = true, LoadingMatchSettled);
            else _loadingBlinkClosed = true;
        }
        bool show = loading || lingering || (connected && !_loadingBlinkClosed && ScreenBlink.Current is { Busy: true });
        if (_loadingLayer.Visible != show)
        {
            _loadingLayer.Visible = show;
            if (!show && _loadingBlinkAsked && _loadingPicture is not null) { _loadingPicture.Texture = null; _loadingPicture = null; }   // never shown again after a connect: the picture's memory goes
        }
        if (!show) return;
        if (_loadingCaptionLabel is not null && _loadingCaptionLabel.Text != _loadingCaption) _loadingCaptionLabel.Text = _loadingCaption;   // P2: the worker's stage
        float height = GetViewport().GetVisibleRect().Size.Y;
        if (height == _loadingSizeFor || _loadingLabel is null) return;
        _loadingSizeFor = height;
        _loadingLabel.AddThemeFontSizeOverride("font_size", Mathf.Max(18, Mathf.RoundToInt(height / 1080f * 40f)));
        _loadingLabel.AddThemeConstantOverride("shadow_outline_size", Mathf.Max(4, Mathf.RoundToInt(height / 1080f * 10f)));
        _loadingLabel.AddThemeConstantOverride("shadow_offset_y", Mathf.Max(1, Mathf.RoundToInt(height / 1080f * 2f)));
        if (_loadingCaptionLabel is not null)
        {
            _loadingCaptionLabel.AddThemeFontSizeOverride("font_size", Mathf.Max(11, Mathf.RoundToInt(height / 1080f * 16f)));
            _loadingCaptionLabel.AddThemeConstantOverride("shadow_outline_size", Mathf.Max(2, Mathf.RoundToInt(height / 1080f * 6f)));
            _loadingCaptionLabel.OffsetTop = Mathf.RoundToInt(height / 1080f * 70f);   // under LOADING (both labels are centred in the whole window)
        }
    }

    /// <summary>
    /// True while the client should not start its handshake yet: the menu's blink has not opened on the loading screen. The match's map load is a long frame; started at once it
    /// fell inside the blink's black or open, so on a fast server the loading screen flickered in for a few frames and the blink turned back to black. Waiting, the loading screen
    /// is fully up before the handshake and stays while the map loads. Never for a replay, never longer than <see cref="LoadingBlinkWaitSeconds"/>.
    /// </summary>
    bool LoadingWaitsForBlink()
    {
        if (_loadingLayer is not { Visible: true } || _loadingBlinkAsked || DemoPlayPath is not null || _client is not { Status: ClientStatus.Connecting } || ScreenBlink.Current is not { Busy: true })
            return false;
        if (_loadingWaitStartMs == 0) _loadingWaitStartMs = Time.GetTicksMsec();
        return (Time.GetTicksMsec() - _loadingWaitStartMs) / 1000.0 < LoadingBlinkWaitSeconds;
    }

    /// <summary>
    /// The blink opens on the match when its last frame built every character it wanted (no first build waiting for the next frame's budget, <see cref="HeavyLeft"/>), or at once
    /// when the connection failed (the error must show).
    /// </summary>
    bool LoadingMatchSettled() => _error != "" || _client is not { Status: ClientStatus.Connected } || _heavyDeferred == 0;
}
