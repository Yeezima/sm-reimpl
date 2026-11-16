using System;
using System.Collections.Generic;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// dev-tools (owner, 2026-11-17): the in-match developer panel, styled and opened like the sound tuner (<see cref="SoundTunerPanel"/>): F6 shows or hides it, Esc closes it. It shows only
/// what the server reports (<see cref="DevState"/>) and sends requests (<see cref="DevCommand"/>); nothing is applied here. A server that does not announce the tools (no <c>--dev-tools</c>,
/// or not bound to loopback only) leaves F6 with a short "dev tools off" note. Test aids: <c>--dev-panel</c> opens it as soon as the server announces the tools, <c>--dev-script</c> presses
/// controls in turn (<see cref="NetworkView"/>, NetworkView.DevTools.cs). Client only; ours.
/// </summary>
public partial class DevToolsPanel : CanvasLayer
{
    public const string PanelArg = "--dev-panel", ScriptArg = "--dev-script", ScriptStepArg = "--dev-script-step";

    /// <summary>The controls, by the word a <c>--dev-script</c> uses; <c>:on</c> / <c>:off</c> sets, none toggles (inv, freeze, ammo, jump, add:loyal, add:chaos, rem:loyal, rem:chaos).</summary>
    public static readonly string[] ControlWords = ["inv", "freeze", "ammo", "jump", "add:loyal", "add:chaos", "rem:loyal", "rem:chaos"];

    readonly Func<DevState?> _state;
    readonly Func<DevCommandKind, byte, bool, bool> _send;
    readonly Dictionary<DevToolsFlags, Godot.Button> _toggles = new();
    PanelContainer? _panel;
    Label? _bots, _status, _off;
    double _offUntil;
    Input.MouseModeEnum _previousMouse = Input.MouseModeEnum.Captured;
    byte _lastSerial;
    bool _serialSeen;

    public DevToolsPanel(Func<DevState?> state, Func<DevCommandKind, byte, bool, bool> send)
    {
        _state = state; _send = send;
        Layer = 95;
        Name = "DevTools";
    }

    public bool IsOpen => _panel?.Visible == true;

    public override void _Ready()
    {
        _off = new Label { Text = "DEV TOOLS OFF - this server was not started with --dev-tools on 127.0.0.1", Visible = false, Position = new Vector2(12, 12), Modulate = new Color(1f, 0.55f, 0.35f) };
        _off.AddThemeColorOverride("font_outline_color", Colors.Black);
        _off.AddThemeConstantOverride("outline_size", 4);
        AddChild(_off);
        var panel = new PanelContainer { Name = "Panel", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.04f, 0.045f, 0.06f, 0.92f), BorderColor = new Color(0.85f, 0.7f, 0.25f), BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2, ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 10, ContentMarginBottom = 10 });
        panel.Position = new Vector2(12, 12);
        panel.CustomMinimumSize = new Vector2(500, 0);
        AddChild(panel);
        _panel = panel;
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 6);
        panel.AddChild(outer);

        outer.AddChild(new Label { Text = "DEV TOOLS   (F6 or Esc closes)  -  local test server only", Modulate = new Color(0.95f, 0.8f, 0.3f) });
        outer.AddChild(new Label { Text = "Every effect is applied by the server; the buttons show what it reports.", Modulate = new Color(0.7f, 0.7f, 0.7f), AutowrapMode = TextServer.AutowrapMode.WordSmart });

        outer.AddChild(new Label { Text = "TOGGLES", Modulate = new Color(0.55f, 0.8f, 1f) });
        Toggle(outer, DevToolsFlags.Invincible, "All players and bots invincible", "Nobody takes damage (shots, blasts, melee, charges).");
        Toggle(outer, DevToolsFlags.FreezeBots, "Freeze bots", "Bots stand still, do not aim and do not fire.");
        Toggle(outer, DevToolsFlags.InfiniteAmmo, "Infinite ammunition", "Clips, reserves and grenades never run out; no weapon heats up or overheats.");
        Toggle(outer, DevToolsFlags.InfiniteJump, "Infinite jump pack fuel", "The jump pack's power is always full.");

        outer.AddChild(new Label { Text = "BOTS", Modulate = new Color(0.55f, 0.8f, 1f) });
        _bots = new Label { Text = "", Modulate = new Color(0.85f, 0.85f, 0.85f) };
        outer.AddChild(_bots);
        var add = new HBoxContainer(); add.AddThemeConstantOverride("separation", 8);
        add.AddChild(new Label { Text = "Add bot", CustomMinimumSize = new Vector2(110, 0) });
        Button(add, "LOYALIST", () => Send(DevCommandKind.AddBot, DevSides.Loyalists, true), "Starts a test bot on the Space Marines (a headless client of this PC).");
        Button(add, "CHAOS", () => Send(DevCommandKind.AddBot, DevSides.Chaos, true), "Starts a test bot on Chaos.");
        outer.AddChild(add);
        var remove = new HBoxContainer(); remove.AddThemeConstantOverride("separation", 8);
        remove.AddChild(new Label { Text = "Remove bot", CustomMinimumSize = new Vector2(110, 0) });
        Button(remove, "LOYALIST", () => Send(DevCommandKind.RemoveBot, DevSides.Loyalists, true), "Stops the newest bot these tools started on the Space Marines.");
        Button(remove, "CHAOS", () => Send(DevCommandKind.RemoveBot, DevSides.Chaos, true), "Stops the newest bot these tools started on Chaos.");
        outer.AddChild(remove);

        _status = new Label { Text = "", Modulate = new Color(0.75f, 0.75f, 0.75f), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        outer.AddChild(_status);
        var close = new HBoxContainer();
        Button(close, "CLOSE", () => SetOpen(false), null);
        outer.AddChild(close);
        Sync();
    }

    void Toggle(VBoxContainer into, DevToolsFlags flag, string label, string tip)
    {
        var b = new Godot.Button { Text = label, TooltipText = tip, Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(0, 30), Name = "Toggle" + flag };
        b.Pressed += () =>
        {
            var state = _state();
            bool now = state is not null && (state.Flags & flag) != 0;
            Send(DevCommandKind.SetFlag, (byte)flag, !now);   // the button shows the server's state, so it flips when the server's answer arrives
        };
        into.AddChild(b);
        _toggles[flag] = b;
    }

    static void Button(Container into, string text, Action pressed, string? tip)
    {
        var b = new Godot.Button { Text = text, TooltipText = tip ?? "" };
        b.Pressed += pressed;
        into.AddChild(b);
    }

    void Send(DevCommandKind kind, byte argument, bool value)
    {
        if (!_send(kind, argument, value)) SetStatus("Not connected as a player.");
    }

    /// <summary>Presses a control by its <see cref="ControlWords"/> word (the <c>--dev-script</c> test aid); <c>inv:on</c> sets, <c>inv</c> toggles. False for an unknown word.</summary>
    public bool Press(string word)
    {
        string name = word, value = "";
        int colon = word.LastIndexOf(':');
        if (colon > 0 && word[(colon + 1)..] is "on" or "off") { name = word[..colon]; value = word[(colon + 1)..]; }
        var state = _state();
        bool Wanted(DevToolsFlags flag) => value.Length > 0 ? value == "on" : !(state is not null && (state.Flags & flag) != 0);
        switch (name)
        {
            case "inv": Send(DevCommandKind.SetFlag, (byte)DevToolsFlags.Invincible, Wanted(DevToolsFlags.Invincible)); return true;
            case "freeze": Send(DevCommandKind.SetFlag, (byte)DevToolsFlags.FreezeBots, Wanted(DevToolsFlags.FreezeBots)); return true;
            case "ammo": Send(DevCommandKind.SetFlag, (byte)DevToolsFlags.InfiniteAmmo, Wanted(DevToolsFlags.InfiniteAmmo)); return true;
            case "jump": Send(DevCommandKind.SetFlag, (byte)DevToolsFlags.InfiniteJump, Wanted(DevToolsFlags.InfiniteJump)); return true;
            case "add:loyal": Send(DevCommandKind.AddBot, DevSides.Loyalists, true); return true;
            case "add:chaos": Send(DevCommandKind.AddBot, DevSides.Chaos, true); return true;
            case "rem:loyal": Send(DevCommandKind.RemoveBot, DevSides.Loyalists, true); return true;
            case "rem:chaos": Send(DevCommandKind.RemoveBot, DevSides.Chaos, true); return true;
            default: return false;
        }
    }

    /// <summary>The answer to the last request, in words.</summary>
    public static string NoteText(DevNote note) => note switch
    {
        DevNote.Applied => "Done.",
        DevNote.BotStarting => "Bot starting (it joins in a few seconds).",
        DevNote.BotRemoved => "Bot removed.",
        DevNote.NoBotToRemove => "No bot started by these tools on that side.",
        DevNote.SideFull => "That side is full.",
        DevNote.ServerFull => "The server is full (16 players).",
        DevNote.LauncherMissing => "No Godot executable found for the bots (start the server with --dev-bot-exe).",
        DevNote.LaunchFailed => "The bot could not be started (see the server log).",
        DevNote.NotInThisMode => "Bots cannot be added in this mode.",
        DevNote.TooManyStarting => "Three bots are still starting; wait a moment.",
        DevNote.TooFast => "Too many requests; wait a moment.",
        _ => "",
    };

    /// <summary>Redraws the controls from the server's state (a closed panel does not bother).</summary>
    public void Sync()
    {
        var s = _state();
        foreach (var (flag, button) in _toggles)
        {
            bool on = s is not null && (s.Flags & flag) != 0;
            string label = button.Text.StartsWith("[ ON  ] ") || button.Text.StartsWith("[ OFF ] ") ? button.Text[8..] : button.Text;
            button.Text = (on ? "[ ON  ] " : "[ OFF ] ") + label;
            button.AddThemeColorOverride("font_color", on ? new Color(0.55f, 1f, 0.55f) : new Color(0.85f, 0.85f, 0.85f));
        }
        if (_bots is not null && s is not null)
            _bots.Text = $"Loyalists: {s.PlayersLoyalists} players ({s.ToolBotsLoyalists} added here)    Chaos: {s.PlayersChaos} players ({s.ToolBotsChaos} added here)    cap {Protocol.MaxPlayers}";
        if (s is not null && (!_serialSeen || s.NoteSerial != _lastSerial))
        {
            _serialSeen = true; _lastSerial = s.NoteSerial;
            if (s.Note != DevNote.None) SetStatus(NoteText(s.Note));
        }
        if (_off is not null && _off.Visible && Time.GetTicksMsec() / 1000.0 > _offUntil) _off.Visible = false;
    }

    void SetStatus(string text) { if (_status is not null) _status.Text = text; }

    /// <summary>The note F6 shows when the server does not announce the tools.</summary>
    public void ShowOff()
    {
        if (_off is null) return;
        _off.Visible = true;
        _offUntil = Time.GetTicksMsec() / 1000.0 + 3.5;
    }

    /// <summary>Shows or hides the panel; the mouse is free while it is open and captured again when it closes.</summary>
    public void SetOpen(bool open)
    {
        if (_panel is null || open == _panel.Visible) return;
        _panel.Visible = open;
        if (open)
        {
            Sync();
            _previousMouse = Input.MouseMode;
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        else Input.MouseMode = _previousMouse;
    }

    public void Toggle() => SetOpen(!IsOpen);
}
