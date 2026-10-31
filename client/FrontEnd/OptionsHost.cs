using System;
using System.Linq;
using Godot;
using SmClient.Customizer.Model;

namespace SmClient.FrontEnd;

/// <summary>
/// Applies the Options menu's settings to the engine (ui/menu-rebuild, ours, client only). Only what differs from the last applied state is touched, and the starting state is
/// <see cref="GameOptions.Default"/>, so a player who changed nothing (and every test run on a fresh profile) leaves the engine as the command line and the project set it. Bots apply nothing
/// (<c>--bot</c>: their 30 fps cap stays); a <c>--test-window</c> or headless run never changes the window (hard rule: test runs do not disturb the desktop). One node for the app, on the root:
/// the brightness pass (a full-screen shader, hidden at the default, so it costs nothing then) and the mute when the window loses focus.
/// </summary>
public sealed partial class OptionsHost : CanvasLayer
{
    public const int BrightnessLayer = 120;
    const string BrightnessShader = """
        shader_type canvas_item;
        uniform sampler2D screen : hint_screen_texture, filter_nearest, repeat_disable;
        uniform float gamma = 1.0;
        void fragment() { COLOR = vec4(pow(texture(screen, SCREEN_UV).rgb, vec3(1.0 / gamma)), 1.0); }
        """;

    static OptionsHost? _current;
    static GameOptions _applied = GameOptions.Default;
    static readonly bool Bot = OS.GetCmdlineUserArgs().Contains("--bot");
    static readonly bool WindowLocked = DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase) || OS.GetCmdlineUserArgs().Contains("--test-window");

    readonly ColorRect _brightness = new() { Name = "Brightness", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    readonly ShaderMaterial _material = new() { Shader = new Shader { Code = BrightnessShader } };
    bool _unfocused;

    OptionsHost()
    {
        Name = "OptionsHost";
        Layer = BrightnessLayer;
        ProcessMode = ProcessModeEnum.Always;
        _brightness.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _brightness.Material = _material;
        AddChild(_brightness);
    }

    static SceneTree? Tree => Engine.GetMainLoop() as SceneTree;

    /// <summary>The brightness pass is drawn (test read).</summary>
    public static bool BrightnessActive => _current?._brightness.Visible == true;

    /// <summary>Applies what changed since the last call; safe to call before the tree is ready.</summary>
    public static void Apply(GameOptions options)
    {
        if (Bot) return;
        var before = _applied;
        _applied = options;
        var root = Tree?.Root;
        if (root is null) return;
        if (options.FpsCap != before.FpsCap) Engine.MaxFps = options.FpsCap;
        if (options.RenderScale != before.RenderScale) root.Scaling3DScale = options.RenderScale;
        if (options.Aa != before.Aa && RenderQuality.Choose(options.Aa)) GD.Print(RenderQuality.Apply(root));
        if (options.Shadows != before.Shadows) MapLightingView.ApplyShadowChoice(options.Shadows);
        if (options.MasterVolume != before.MasterVolume) AudioServer.SetBusVolumeDb(0, GameOptions.VolumeDb(options.MasterVolume));
        if (options.Brightness != 1f && _current is null) Ensure(root);
        _current?.ShowBrightness(options.Brightness);
        if (options.MuteUnfocused) Ensure(root);
        _current?.ApplyMute();
        if (!WindowLocked) ApplyWindow(root, options, before);
    }

    static void Ensure(Window root)
    {
        if (_current is not null && IsInstanceValid(_current)) return;
        _current = new OptionsHost();
        root.CallDeferred(Node.MethodName.AddChild, _current);
    }

    void ShowBrightness(float brightness)
    {
        _material.SetShaderParameter("gamma", brightness);
        _brightness.Visible = !Mathf.IsEqualApprox(brightness, 1f);
    }

    static void ApplyWindow(Window root, GameOptions now, GameOptions before)
    {
        if (now.Vsync != before.Vsync) DisplayServer.WindowSetVsyncMode(now.Vsync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        if (now.Window != before.Window)
            root.Mode = now.Window switch
            {
                WindowChoice.Fullscreen => Window.ModeEnum.ExclusiveFullscreen,
                WindowChoice.Borderless => Window.ModeEnum.Fullscreen,   // Godot's "fullscreen" is a borderless window over the desktop
                _ => Window.ModeEnum.Windowed,
            };
        bool sizeChanged = now.WindowWidth != before.WindowWidth || now.WindowHeight != before.WindowHeight || now.Window != before.Window;
        if (sizeChanged && now.Window == WindowChoice.Windowed && now.WindowWidth > 0 && now.WindowHeight > 0)
        {
            root.Size = new Vector2I(now.WindowWidth, now.WindowHeight);
            root.MoveToCenter();
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMWindowFocusOut) { _unfocused = true; ApplyMute(); }
        else if (what == NotificationWMWindowFocusIn) { _unfocused = false; ApplyMute(); }
    }

    void ApplyMute() => AudioServer.SetBusMute(0, _unfocused && _applied.MuteUnfocused);
}
