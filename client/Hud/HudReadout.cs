using System;
using System.Globalization;
using Godot;
using SmClient.Customizer.Model;
using SmNet;

namespace SmClient;

/// <summary>
/// polish pass P1 (2026-11-10, ours; both modes, off by default): the match's performance readout for testers' reports, a few lines of small text in the top right corner
/// under the HUD's own elements. F3 cycles it (ModelViewer.CycleReadout; no Options row: the owner keeps the original's options), remembered as the profile's
/// <c>performanceReadout</c>: OFF, FPS (frames a second over the last second, the mean and worst frame of that second in ms)
/// or FPS + NETWORK (plus the ping from the snapshot echoes, the snapshot loss over 3 s, the remote players' interpolation delay and the input slack the server reports,
/// <see cref="GameClient"/>'s own numbers: nothing is measured twice). Test aid <c>--readout fps|network|off</c> sets it for one run. Reads only; nothing of the game
/// state is touched. Headless clients draw nothing.
/// </summary>
public sealed partial class HudReadout : Label
{
    /// <summary>The saved choice (<see cref="FrontEnd.OptionsHost"/> sets it from the profile, F3 cycles it); <see cref="Override"/> wins when the command line names one.</summary>
    public static ReadoutChoice Choice { get; set; } = ReadoutChoice.Off;

    /// <summary>The command line's <c>--readout</c> (null: none).</summary>
    public static readonly ReadoutChoice? Override = ParseOverride();

    public static ReadoutChoice Effective => Override ?? Choice;

    static ReadoutChoice? ParseOverride()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--readout");
        if (i < 0 || i + 1 >= args.Length) return null;
        return args[i + 1].ToLowerInvariant() switch { "fps" => ReadoutChoice.Fps, "network" or "net" => ReadoutChoice.Network, "off" => ReadoutChoice.Off, _ => null };
    }

    /// <summary>How often the text is rebuilt (seconds): the window the frame numbers are taken over.</summary>
    public const double WindowSeconds = 1.0;
    /// <summary>The margin from the window's top right corner (pixels at the HUD's scale).</summary>
    public const float Margin = 14f;

    double _window;
    int _frames;
    double _frameSum, _frameWorst;

    public HudReadout()
    {
        Name = "Readout";
        MouseFilter = MouseFilterEnum.Ignore;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        SetAnchorsPreset(LayoutPreset.TopRight);
        GrowHorizontal = GrowDirection.Begin;
        GrowVertical = GrowDirection.End;
        OffsetRight = -Margin; OffsetTop = Margin;
        Visible = false;
    }

    public override void _Ready()
    {
        // The HUD's face (Bahnschrift, the closest system face to the original's), light and small so the readout never competes with the HUD.
        var system = new SystemFont { FontNames = ["Bahnschrift", "DIN Alternate", "Arial Narrow"], Fallbacks = [ThemeDB.FallbackFont] };
        var ts = TextServerManager.GetPrimaryInterface();
        var font = new FontVariation { BaseFont = system, VariationOpentype = new Godot.Collections.Dictionary { { ts.NameToTag("wght"), 450 }, { ts.NameToTag("wdth"), 92 } } };
        AddThemeFontOverride("font", font);
        AddThemeFontSizeOverride("font_size", 15);
        AddThemeColorOverride("font_color", new Color(0.92f, 0.92f, 0.92f, 0.9f));
        AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.7f));
        AddThemeConstantOverride("shadow_offset_x", 1);
        AddThemeConstantOverride("shadow_offset_y", 1);
    }

    /// <summary>Each frame: counts the frame; once a second rebuilds the text from the frame numbers and <paramref name="client"/>'s network state (null: not connected).</summary>
    public void Step(double delta, GameClient? client, bool inMatch)
    {
        var mode = Effective;
        bool show = mode != ReadoutChoice.Off && inMatch;
        if (Visible != show) Visible = show;
        if (!show) { _window = 0; _frames = 0; _frameSum = 0; _frameWorst = 0; return; }
        double ms = delta * 1000.0;
        _frames++; _frameSum += ms; if (ms > _frameWorst) _frameWorst = ms;
        _window += delta;
        if (_window < WindowSeconds) return;
        Text = Compose(mode, _frames, _frameSum, _frameWorst, _window, client);
        _window = 0; _frames = 0; _frameSum = 0; _frameWorst = 0;
    }

    /// <summary>The readout's lines (engine-free so a test can read them): fps and frame ms; with <see cref="ReadoutChoice.Network"/> the ping, loss, interpolation and slack.</summary>
    public static string Compose(ReadoutChoice mode, int frames, double frameSumMs, double frameWorstMs, double windowSeconds, GameClient? client)
    {
        if (frames <= 0 || windowSeconds <= 0) return "";
        var inv = CultureInfo.InvariantCulture;
        string text = string.Create(inv, $"{frames / windowSeconds:F0} FPS  {frameSumMs / frames:F1} ms  worst {frameWorstMs:F0}");
        if (mode != ReadoutChoice.Network) return text;
        if (client is null || client.Status != ClientStatus.Connected) return text + "\nNOT CONNECTED";
        string ping = client.EchoRoundTripMs > 0 ? string.Create(inv, $"{client.EchoRoundTripMs} ms") : "-";
        string slack = double.IsFinite(client.FilteredInputSlack) ? string.Create(inv, $"{client.FilteredInputSlack:F0} ms") : "-";
        return text + string.Create(inv, $"\nPING {ping}  LOSS {client.MeasuredLoss * 100:F1}%\nINTERP {client.CurrentInterpolationDelay.TotalMilliseconds:F0} ms  SLACK {slack}");
    }
}
