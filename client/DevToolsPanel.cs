using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// dev-tools (owner, 2026-11-17): the in-match developer panel, styled and opened like the sound tuner (<see cref="SoundTunerPanel"/>): F6 shows or hides it, Esc closes it. It shows only
/// what the server reports (<see cref="DevState"/>) and sends requests (<see cref="DevCommand"/>); nothing is applied here. Left column: toggles (invincible, freeze bots, infinite ammunition,
/// infinite jump pack fuel, indefinite match, instant respawn), the match buttons (end now, restart, skip), kill me and kill bots. Right column: add and remove bots, teleport, the bots'
/// difficulty sliders and the time scale. A server that does not announce the tools (no <c>--dev-tools</c>, or not bound to loopback only) leaves F6 with a short "dev tools off" note. Test
/// aids: <c>--dev-panel</c> opens it as soon as the server announces the tools, <c>--dev-script</c> presses controls in turn (<see cref="NetworkView"/>, NetworkView.DevTools.cs). Client only; ours.
/// </summary>
public partial class DevToolsPanel : CanvasLayer
{
    public const string PanelArg = "--dev-panel", ScriptArg = "--dev-script", ScriptStepArg = "--dev-script-step";

    /// <summary>The controls, by the word a <c>--dev-script</c> uses (a bare flag word toggles, <c>:on</c> / <c>:off</c> sets; see <see cref="Press"/>).</summary>
    public static readonly string[] ControlWords =
    [
        "inv", "freeze", "ammo", "jump", "indef", "respawn", "end", "restart", "skip", "map:N|NAME", "killme", "kill:loyal", "kill:chaos", "kill:all",
        "add:loyal", "add:chaos", "rem:loyal", "rem:chaos", "tp:spawn-loyal", "tp:spawn-chaos", "tp:point:N", "tp:player:N",
        "acc:PERCENT|default", "dmg:PERCENT", "range:METRES|default", "scale:0..4",
    ];

    /// <summary>One teleport choice: its label, kind and index (side, point or player id).</summary>
    public readonly record struct TeleportChoice(string Label, DevTeleportKind Kind, int Index);

    /// <summary>One map the picker offers: the archive name and the display name (devtools-map-pick, protocol 86); its position in the list is what the wire carries.</summary>
    public readonly record struct MapChoice(string Name, string Label);

    readonly Func<DevState?> _state;
    readonly Func<DevCommandKind, byte, bool, int, bool> _send;
    readonly Func<List<TeleportChoice>> _targets;
    readonly Func<IReadOnlyList<MapChoice>> _maps;
    readonly Func<string> _currentMapOf;
    readonly Dictionary<DevToolsFlags, Godot.Button> _toggles = new();
    readonly List<Godot.Button> _scaleButtons = new();
    PanelContainer? _panel;
    Label? _bots, _status, _off, _accValue, _dmgValue, _rangeValue;
    HSlider? _acc, _dmg, _range;
    OptionButton? _teleport, _mapPick;
    VBoxContainer? _mapBox;
    List<MapChoice> _mapChoices = new();
    string _mapSignature = "";
    List<TeleportChoice> _choices = new();
    string _choiceSignature = "";
    double _offUntil;
    Input.MouseModeEnum _previousMouse = Input.MouseModeEnum.Captured;
    byte _lastSerial;
    bool _serialSeen, _syncing;
    readonly Dictionary<HSlider, (double Due, Action Send)> _pending = new();

    public override void _Process(double delta)
    {
        if (_pending.Count == 0) return;
        double now = Time.GetTicksMsec() / 1000.0;
        foreach (var (slider, (due, send)) in _pending.ToList())
            if (now >= due) { _pending.Remove(slider); send(); }
    }

    public DevToolsPanel(Func<DevState?> state, Func<DevCommandKind, byte, bool, int, bool> send, Func<List<TeleportChoice>> targets, Func<IReadOnlyList<MapChoice>>? maps = null, Func<string>? currentMap = null)
    {
        _state = state; _send = send; _targets = targets; _maps = maps ?? (() => []); _currentMapOf = currentMap ?? (() => "");
        Layer = 95;
        Name = "DevTools";
    }

    public bool IsOpen => _panel?.Visible == true;

    static readonly Color Blue = new(0.55f, 0.8f, 1f);

    public override void _Ready()
    {
        _off = new Label { Text = "DEV TOOLS OFF - this server was not started with --dev-tools on 127.0.0.1", Visible = false, Position = new Vector2(12, 12), Modulate = new Color(1f, 0.55f, 0.35f) };
        _off.AddThemeColorOverride("font_outline_color", Colors.Black);
        _off.AddThemeConstantOverride("outline_size", 4);
        AddChild(_off);
        var panel = new PanelContainer { Name = "Panel", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.04f, 0.045f, 0.06f, 0.92f), BorderColor = new Color(0.85f, 0.7f, 0.25f), BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2, ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 10, ContentMarginBottom = 10 });
        panel.Position = new Vector2(12, 12);
        panel.CustomMinimumSize = new Vector2(1000, 0);
        AddChild(panel);
        _panel = panel;
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 6);
        panel.AddChild(outer);

        outer.AddChild(new Label { Text = "DEV TOOLS   (F6 or Esc closes)  -  local test server only", Modulate = new Color(0.95f, 0.8f, 0.3f) });
        outer.AddChild(new Label { Text = "Every effect is applied by the server; the controls show what it reports.", Modulate = new Color(0.7f, 0.7f, 0.7f), AutowrapMode = TextServer.AutowrapMode.WordSmart });

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 24);
        outer.AddChild(columns);
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0) };
        left.AddThemeConstantOverride("separation", 4);
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(480, 0) };
        right.AddThemeConstantOverride("separation", 4);
        columns.AddChild(left); columns.AddChild(right);

        // ---- left: toggles, match, players ----
        Header(left, "TOGGLES");
        Toggle(left, DevToolsFlags.Invincible, "All players and bots invincible", "Nobody takes damage (shots, blasts, melee, charges); hit reactions still play.");
        Toggle(left, DevToolsFlags.FreezeBots, "Freeze bots", "Bots stand still, do not aim and do not fire.");
        Toggle(left, DevToolsFlags.InfiniteAmmo, "Infinite ammunition", "Clips, reserves and grenades never run out; no weapon heats up or overheats.");
        Toggle(left, DevToolsFlags.InfiniteJump, "Infinite jump pack fuel", "The jump pack's power is always full.");
        Toggle(left, DevToolsFlags.Indefinite, "Indefinite match (this match only)", "The match clock stands and the score limit does not end it; the next match is normal again.");
        Toggle(left, DevToolsFlags.InstantRespawn, "Instant respawn", "Nobody waits out a death timer.");
        Header(left, "MATCH");
        var match = Row(left);
        Button(match, "END MATCH NOW", () => Send(DevCommandKind.EndMatch, 0, true), "Ends the match as the time limit would (the end screen, then the next match or map).");
        Button(match, "RESTART MATCH", () => Send(DevCommandKind.RestartMatch, 0, true), "Same map, fresh scores, everyone respawns.");
        Button(match, "SKIP TO NEXT", () => Send(DevCommandKind.SkipMatch, 0, true), "Ends the match and hurries to the next one (the next map in a --maps rotation).");
        // devtools-map-pick (protocol 86): Classic PvP only (the list is empty in Musou Mode and the whole block is hidden)
        _mapBox = new VBoxContainer { Name = "MapPick" };
        _mapBox.AddThemeConstantOverride("separation", 4);
        left.AddChild(_mapBox);
        Header(_mapBox, "MAP (ends this match now, starts a new one)");
        var pick = new HBoxContainer { Name = "MapRow" };
        pick.AddThemeConstantOverride("separation", 8);
        _mapBox.AddChild(pick);
        _mapPick = new OptionButton { Name = "MapList", CustomMinimumSize = new Vector2(300, 0), TooltipText = "Every Classic PvP map this server can play (the --maps all rotation's list)." };
        pick.AddChild(_mapPick);
        Button(pick, "GO", PickSelectedMap, "Ends the current match at once; everyone rejoins on the chosen map in a few seconds.");
        _mapBox.Visible = false;
        Header(left, "KILL");
        var kill = Row(left);
        Button(kill, "KILL ME", () => Send(DevCommandKind.KillMe, 0, true), "A suicide: a death on the scoreboard; you respawn at once.");
        kill.AddChild(new Label { Text = "  kill bots:" });
        Button(kill, "LOYALIST", () => Send(DevCommandKind.KillBots, 0, true), "Kills every Loyalist bot.");
        Button(kill, "CHAOS", () => Send(DevCommandKind.KillBots, 1, true), "Kills every Chaos bot.");
        Button(kill, "BOTH", () => Send(DevCommandKind.KillBots, 2, true), "Kills every bot.");

        // ---- right: bots, teleport, difficulty, time ----
        Header(right, "BOTS");
        _bots = new Label { Text = "", Modulate = new Color(0.85f, 0.85f, 0.85f) };
        right.AddChild(_bots);
        var add = Row(right);
        add.AddChild(new Label { Text = "Add bot", CustomMinimumSize = new Vector2(110, 0) });
        Button(add, "LOYALIST", () => Send(DevCommandKind.AddBot, DevSides.Loyalists, true), "Starts a test bot on the Space Marines (a headless client of this PC).");
        Button(add, "CHAOS", () => Send(DevCommandKind.AddBot, DevSides.Chaos, true), "Starts a test bot on Chaos.");
        var remove = Row(right);
        remove.AddChild(new Label { Text = "Remove bot", CustomMinimumSize = new Vector2(110, 0) });
        Button(remove, "LOYALIST", () => Send(DevCommandKind.RemoveBot, DevSides.Loyalists, true), "Stops the newest bot these tools started on the Space Marines.");
        Button(remove, "CHAOS", () => Send(DevCommandKind.RemoveBot, DevSides.Chaos, true), "Stops the newest bot these tools started on Chaos.");

        Header(right, "TELEPORT (you only)");
        var tp = Row(right);
        _teleport = new OptionButton { CustomMinimumSize = new Vector2(300, 0), TooltipText = "Spawn rooms, control points and the other living players." };
        tp.AddChild(_teleport);
        Button(tp, "GO", () =>
        {
            int i = _teleport.Selected;
            if (i >= 0 && i < _choices.Count) Send(DevCommandKind.Teleport, (byte)_choices[i].Kind, true, _choices[i].Index);
        }, "Moves you to a free spot there.");

        Header(right, "BOT DIFFICULTY (all bots, live)");
        _acc = Slider(right, "Accuracy", 0, 100, 1, out _accValue, v => $"{v:0} %", v => Send(DevCommandKind.SetBotAccuracy, 0, true, (int)Math.Round(v * 10)));
        _dmg = Slider(right, "Damage dealt", 0, 100, 1, out _dmgValue, v => $"{v:0} %", v => Send(DevCommandKind.SetBotDamage, 0, true, (int)Math.Round(v * 10)));
        _range = Slider(right, "Fire range", 10, 300, 5, out _rangeValue, v => $"{v:0} m", v => Send(DevCommandKind.SetBotRange, 0, true, (int)Math.Round(v)));
        var reset = Row(right);
        Button(reset, "BOTS' OWN ACCURACY AND RANGE", () => { Send(DevCommandKind.SetBotAccuracy, 0, true, Protocol.DevNoValue); Send(DevCommandKind.SetBotRange, 0, true, 0); }, "Back to what each bot was started with (damage stays as set).");

        Header(right, "TIME");
        var time = Row(right);
        string[] names = ["1x", "1/2", "1/4", "1/10", "PAUSE"];
        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            var b = new Godot.Button { Text = names[i], ToggleMode = false, CustomMinimumSize = new Vector2(70, 0), TooltipText = "The server's clock and every client's run at this fraction of real time." };
            b.Pressed += () => Send(DevCommandKind.SetTimeScale, (byte)index, true);
            time.AddChild(b);
            _scaleButtons.Add(b);
        }
        ExtrasPanelBuild(right);   // EXTRAS-HOOK: added content's own section (weather: the preset buttons)

        _status = new Label { Text = "", Modulate = new Color(0.75f, 0.75f, 0.75f), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        outer.AddChild(_status);
        var close = new HBoxContainer();
        Button(close, "CLOSE", () => SetOpen(false), null);
        outer.AddChild(close);
        Sync();
    }

    /// <summary>Added content may add a section to the right column (weather). Unimplemented, it compiles away.</summary>
    partial void ExtrasPanelBuild(VBoxContainer column);
    /// <summary>Added content's script words (<c>--dev-script</c>): set <paramref name="handled"/> when the word was its own.</summary>
    partial void ExtrasPanelPress(string name, string arg, ref bool handled);
    /// <summary>Once per sync: added content redraws its section.</summary>
    partial void ExtrasPanelSync();

    void PickSelectedMap()
    {
        int i = _mapPick?.Selected ?? -1;
        if (i >= 0 && i < _mapChoices.Count) Send(DevCommandKind.PickMap, (byte)i, true);
    }

    static void Header(VBoxContainer into, string text) => into.AddChild(new Label { Text = text, Modulate = Blue });

    static HBoxContainer Row(VBoxContainer into)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        into.AddChild(row);
        return row;
    }

    HSlider Slider(VBoxContainer into, string label, double min, double max, double step, out Label value, Func<double, string> format, Action<double> send)
    {
        var row = Row(into);
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(110, 0) });
        var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = min, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(220, 20) };
        var text = new Label { Text = "", CustomMinimumSize = new Vector2(70, 0), HorizontalAlignment = HorizontalAlignment.Right };
        slider.ValueChanged += v => text.Text = format(v);
        slider.DragEnded += changed => { _pending.Remove(slider); if (changed && !_syncing) send(slider.Value); };   // sent when the drag is let go
        slider.ValueChanged += _ => { if (!_syncing && slider.HasFocus()) _pending[slider] = (Time.GetTicksMsec() / 1000.0 + 0.4, () => send(slider.Value)); };   // a keyboard change: sent after a short pause
        row.AddChild(slider); row.AddChild(text);
        value = text;
        return slider;
    }

    void Toggle(VBoxContainer into, DevToolsFlags flag, string label, string tip)
    {
        var b = new Godot.Button { Text = label, TooltipText = tip, Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(0, 28), Name = "Toggle" + flag };
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

    void Send(DevCommandKind kind, byte argument, bool value, int extra = 0)
    {
        if (!_send(kind, argument, value, extra)) SetStatus("Not connected as a player.");
    }

    /// <summary>
    /// The words of a <c>--dev-script</c> value: split on commas, each trimmed of spaces and of stray quote characters. A nested quoting (<c>cmd /c "client.cmd --dev-script \"a,b\""</c>, a PowerShell string with quotes in it)
    /// can hand the client the quotes themselves, which used to turn the first and the last word into unknown ones (<c>"a</c>, <c>b"</c>); now they are dropped.
    /// </summary>
    public static string[] SplitScript(string script)
        => script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.Trim('"', '\'', ' ')).Where(w => w.Length > 0).ToArray();

    /// <summary>
    /// Presses a control by its <see cref="ControlWords"/> word (the <c>--dev-script</c> test aid): <c>inv:on</c> sets a flag, <c>inv</c> toggles it; <c>tp:spawn-loyal</c>, <c>tp:point:1</c>,
    /// <c>tp:player:2</c>; <c>acc:50</c> (percent), <c>dmg:34</c>, <c>range:80</c>, <c>acc:default</c>, <c>range:default</c>; <c>scale:0</c> to <c>scale:4</c>. False for an unknown word.
    /// </summary>
    public bool Press(string word)
    {
        var parts = word.Split(':');
        string name = parts[0], arg = parts.Length > 1 ? parts[1] : "";
        string value = arg is "on" or "off" ? arg : "";
        var state = _state();
        bool Wanted(DevToolsFlags flag) => value.Length > 0 ? value == "on" : !(state is not null && (state.Flags & flag) != 0);
        bool Number(string text, out int n) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
        switch (name)
        {
            case "inv": Send(DevCommandKind.SetFlag, (byte)DevToolsFlags.Invincible, Wanted(DevToolsFlags.Invincible)); return true;
            case "freeze": Send(DevCommandKind.SetFlag, (byte)DevToolsFlags.FreezeBots, Wanted(DevToolsFlags.FreezeBots)); return true;
            case "ammo": Send(DevCommandKind.SetFlag, (byte)DevToolsFlags.InfiniteAmmo, Wanted(DevToolsFlags.InfiniteAmmo)); return true;
            case "jump": Send(DevCommandKind.SetFlag, (byte)DevToolsFlags.InfiniteJump, Wanted(DevToolsFlags.InfiniteJump)); return true;
            case "indef": Send(DevCommandKind.SetFlag, (byte)DevToolsFlags.Indefinite, Wanted(DevToolsFlags.Indefinite)); return true;
            case "respawn": Send(DevCommandKind.SetFlag, (byte)DevToolsFlags.InstantRespawn, Wanted(DevToolsFlags.InstantRespawn)); return true;
            case "end": Send(DevCommandKind.EndMatch, 0, true); return true;
            case "restart": Send(DevCommandKind.RestartMatch, 0, true); return true;
            case "skip": Send(DevCommandKind.SkipMatch, 0, true); return true;
            case "map" when Number(arg, out int mapIndex): Send(DevCommandKind.PickMap, (byte)mapIndex, true); return true;   // an index into the picker's list
            case "map" when arg.Length > 0:
            {
                int at = _maps().ToList().FindIndex(m => m.Name.Equals(arg, StringComparison.OrdinalIgnoreCase));   // or the archive name (map:mp_tdm_17)
                if (at < 0) return false;
                Send(DevCommandKind.PickMap, (byte)at, true); return true;
            }
            case "killme": Send(DevCommandKind.KillMe, 0, true); return true;
            case "kill" when arg is "loyal" or "chaos" or "all": Send(DevCommandKind.KillBots, (byte)(arg == "loyal" ? 0 : arg == "chaos" ? 1 : 2), true); return true;
            case "add" when arg is "loyal" or "chaos": Send(DevCommandKind.AddBot, arg == "chaos" ? DevSides.Chaos : DevSides.Loyalists, true); return true;
            case "rem" when arg is "loyal" or "chaos": Send(DevCommandKind.RemoveBot, arg == "chaos" ? DevSides.Chaos : DevSides.Loyalists, true); return true;
            case "tp" when arg is "spawn-loyal" or "spawn-chaos": Send(DevCommandKind.Teleport, (byte)DevTeleportKind.SpawnRoom, true, arg == "spawn-chaos" ? 1 : 0); return true;
            case "tp" when arg == "point" && parts.Length > 2 && Number(parts[2], out int point): Send(DevCommandKind.Teleport, (byte)DevTeleportKind.ControlPoint, true, point); return true;
            case "tp" when arg == "player" && parts.Length > 2 && Number(parts[2], out int player): Send(DevCommandKind.Teleport, (byte)DevTeleportKind.Player, true, player); return true;
            case "acc" when arg == "default": Send(DevCommandKind.SetBotAccuracy, 0, true, Protocol.DevNoValue); return true;
            case "acc" when Number(arg, out int acc): Send(DevCommandKind.SetBotAccuracy, 0, true, acc * 10); return true;
            case "dmg" when Number(arg, out int dmg): Send(DevCommandKind.SetBotDamage, 0, true, dmg * 10); return true;
            case "range" when arg == "default": Send(DevCommandKind.SetBotRange, 0, true, 0); return true;
            case "range" when Number(arg, out int range): Send(DevCommandKind.SetBotRange, 0, true, range); return true;
            case "scale" when Number(arg, out int scale): Send(DevCommandKind.SetTimeScale, (byte)scale, true); return true;
            default:
            {
                bool handled = false;
                ExtrasPanelPress(name, parts.Length > 1 ? string.Join(':', parts, 1, parts.Length - 1) : "", ref handled);   // EXTRAS-HOOK: added content's script words (weather:rain, weather:none, weather:follow; wxslide:20:3 keeps every part after the name)
                return handled;
            }
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
        DevNote.NotPlaying => "That needs a match that is being played (or you are dead).",
        DevNote.TeleportBlocked => "No free spot there, or that target is gone.",
        DevNote.BadValue => "The server refused that value.",
        DevNote.NoMapChange => "This server cannot change its map (start it with server.cmd --dev-tools).",
        _ => "",
    };

    /// <summary>Rebuilds the teleport list when the points or players changed, keeping the choice.</summary>
    void RefreshTargets()
    {
        if (_teleport is null) return;
        var choices = _targets();
        string signature = string.Join("|", choices.Select(c => $"{c.Kind}:{c.Index}:{c.Label}"));
        if (signature == _choiceSignature) return;
        var kept = _teleport.Selected >= 0 && _teleport.Selected < _choices.Count ? _choices[_teleport.Selected] : (TeleportChoice?)null;
        _choiceSignature = signature;
        _choices = choices;
        _teleport.Clear();
        int select = 0;
        for (int i = 0; i < choices.Count; i++)
        {
            _teleport.AddItem(choices[i].Label);
            if (kept is { } k && k.Kind == choices[i].Kind && k.Index == choices[i].Index) select = i;
        }
        if (choices.Count > 0) _teleport.Select(select);
    }

    /// <summary>Rebuilds the map list when it changed (a new mode or map), keeping the choice; hides the block when there is no list (Musou Mode).</summary>
    void RefreshMaps()
    {
        if (_mapPick is null || _mapBox is null) return;
        var choices = _maps();
        string current = _currentMapOf();
        string signature = string.Join("|", choices.Select(c => c.Name)) + "@" + current;
        _mapBox.Visible = choices.Count > 0;
        if (signature == _mapSignature) return;
        _mapSignature = signature;
        _mapChoices = choices.ToList();
        _mapPick.Clear();
        int select = 0;
        for (int i = 0; i < _mapChoices.Count; i++)
        {
            bool now = _mapChoices[i].Name.Equals(current, StringComparison.OrdinalIgnoreCase);
            _mapPick.AddItem(_mapChoices[i].Label + (now ? "  (playing now)" : ""));
            if (now) select = i;
        }
        if (_mapChoices.Count > 0) _mapPick.Select(select);
    }

    /// <summary>Redraws the controls from the server's state (a closed panel does not bother).</summary>
    public void Sync()
    {
        var s = _state();
        _syncing = true;
        foreach (var (flag, button) in _toggles)
        {
            bool on = s is not null && (s.Flags & flag) != 0;
            string label = button.Text.StartsWith("[ ON  ] ") || button.Text.StartsWith("[ OFF ] ") ? button.Text[8..] : button.Text;
            button.Text = (on ? "[ ON  ] " : "[ OFF ] ") + label;
            button.AddThemeColorOverride("font_color", on ? new Color(0.55f, 1f, 0.55f) : new Color(0.85f, 0.85f, 0.85f));
        }
        if (s is not null)
        {
            if (_bots is not null) _bots.Text = $"Loyalists: {s.PlayersLoyalists} players ({s.ToolBotsLoyalists} added here)    Chaos: {s.PlayersChaos} ({s.ToolBotsChaos} added here)    cap {Protocol.MaxPlayers}";
            for (int i = 0; i < _scaleButtons.Count; i++)
                _scaleButtons[i].AddThemeColorOverride("font_color", i == s.TimeScale ? new Color(0.55f, 1f, 0.55f) : new Color(0.85f, 0.85f, 0.85f));
            // Sliders show what the server holds, unless one is being dragged (its own value is then the user's).
            if (_acc is not null && _accValue is not null && !_acc.HasFocus()) { double v = s.BotAccuracy == Protocol.DevNoValue ? NetworkView.DefaultBotAccuracy * 100.0 : s.BotAccuracy / 10.0; _acc.SetValueNoSignal(v); _accValue.Text = s.BotAccuracy == Protocol.DevNoValue ? "bots' own" : $"{v:0} %"; }
            if (_dmg is not null && _dmgValue is not null && !_dmg.HasFocus()) { double v = s.BotDamage / 10.0; _dmg.SetValueNoSignal(v); _dmgValue.Text = $"{v:0} %"; }
            if (_range is not null && _rangeValue is not null && !_range.HasFocus()) { double v = s.BotRange == 0 ? 100 : s.BotRange; _range.SetValueNoSignal(v); _rangeValue.Text = s.BotRange == 0 ? "bots' own" : $"{v:0} m"; }
        }
        _syncing = false;
        ExtrasPanelSync();   // EXTRAS-HOOK: added content's section follows the server
        if (s is not null && (!_serialSeen || s.NoteSerial != _lastSerial))
        {
            _serialSeen = true; _lastSerial = s.NoteSerial;
            if (s.Note != DevNote.None) SetStatus(NoteText(s.Note));
        }
        if (IsOpen) { RefreshTargets(); RefreshMaps(); }
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
