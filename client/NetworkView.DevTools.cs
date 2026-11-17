using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// dev-tools (owner, 2026-11-17): the developer panel (<see cref="DevToolsPanel"/>), F6. Built when the server first announces the tools (<see cref="GameClient.Dev"/>; a server without
/// <c>--dev-tools</c> never does, so nothing is built and nothing runs but a few comparisons a frame). The state also drives two things on every client of such a server, bots included:
/// the time scale (the client's clock and Godot's <c>Engine.TimeScale</c> run at the server's fraction, so prediction, interpolation, timers, animation and effects follow the scaled
/// server together; sound is not slowed) and, on a bot, the live accuracy and range the panel sets (the bot's own aim model is rebuilt). Test aids: <c>--dev-panel</c> opens the panel at
/// once, <c>--dev-script "inv:on,add:chaos,..."</c> presses the controls in turn (<c>--dev-script-step</c> seconds apart, default 2; the words are <see cref="DevToolsPanel.ControlWords"/>),
/// and every change of the server's state is logged as <c>devtools: ...</c>.
/// </summary>
public partial class NetworkView
{
    static readonly string[] DevArgs = OS.GetCmdlineUserArgs();
    DevToolsPanel? _devPanel;
    string[]? _devScript;
    int _devScriptAt;
    string _devLogged = "";
    ScalableClock? _devClock;
    double _devScaleApplied = 1.0;
    bool _devBotCaptured;
    float? _devOwnAccuracy, _devOwnRange;

    static string? DevArg(string name)
    {
        int i = Array.IndexOf(DevArgs, name);
        return i >= 0 && i + 1 < DevArgs.Length && !DevArgs[i + 1].StartsWith("--") ? DevArgs[i + 1] : null;
    }

    bool DevPanelOpen => _devPanel?.IsOpen == true;

    DevToolsPanel NewDevPanel() => new(() => _client?.Dev, (kind, argument, value, extra) => _client?.SendDevCommand(kind, argument, value, extra) == true, DevTeleportTargets, DevMapChoices, () => _client?.MapName ?? "");

    IReadOnlyList<MapEntry>? _devMapCatalog;
    GameMode? _devMapMode;
    List<DevToolsPanel.MapChoice> _devMaps = [];

    /// <summary>
    /// devtools-map-pick (protocol 86): the maps the picker lists, by display name, in the order the server counts them (<see cref="DevMapPicks"/>: the install's playable competitive maps that offer
    /// the server's mode, Manufactorum first). Empty in Musou Mode (its dev tools stay as they were) or when the install lists none.
    /// </summary>
    IReadOnlyList<DevToolsPanel.MapChoice> DevMapChoices()
    {
        if (_client is null || _config is null || _config.Extras.HasFlag(ContentExtras.Musou)) return [];
        var mode = _client.Match.Mode;
        if (_devMapCatalog is null)
        {
            try { _devMapCatalog = MapCatalog.Read(_gameDir); } catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or NotSupportedException) { _devMapCatalog = []; }
        }
        if (mode != _devMapMode)
        {
            _devMapMode = mode;
            _devMaps = DevMapPicks.For(_devMapCatalog, mode).Select(m => new DevToolsPanel.MapChoice(m.Name, m.DisplayName)).ToList();
        }
        return _devMaps;
    }

    /// <summary>The teleport list: both spawn rooms, the control points and the other players the scoreboard knows.</summary>
    List<DevToolsPanel.TeleportChoice> DevTeleportTargets()
    {
        var list = new List<DevToolsPanel.TeleportChoice>
        {
            new("Spawn room: Loyalists", DevTeleportKind.SpawnRoom, 0), new("Spawn room: Chaos", DevTeleportKind.SpawnRoom, 1),
        };
        if (_client is null) return list;
        for (int i = 0; i < _client.Points.Count; i++) list.Add(new($"Control point {(char)('A' + i)}", DevTeleportKind.ControlPoint, i));
        if (_client.Scoreboard is { } board)
            foreach (var p in board.Players.OrderBy(p => p.PlayerId))
                if (p.PlayerId != _client.PlayerId) list.Add(new($"Player {(p.Name.Length > 0 ? p.Name : "P" + p.PlayerId)} ({p.Team})", DevTeleportKind.Player, p.PlayerId));
        return list;
    }

    /// <summary>The server's time scale on this client (its clock and the engine's time scale), and on a bot the live aim values; nothing to do on a server without the tools.</summary>
    void DevTimeAndBots(DevState? state)
    {
        // (the state's own freshness is judged on real time, GameClient.UnscaledClock: a dead server's state expires even while the clock stands still)
        double scale = state is null ? 1.0 : DevTools.TimeScales[Math.Clamp(state.TimeScale, 0, DevTools.TimeScales.Length - 1)];
        if (scale != _devScaleApplied)
        {
            _devScaleApplied = scale;
            if (_devClock is not null) _devClock.Scale = scale;
            Engine.TimeScale = Math.Max(scale, 0.001);   // animation, effects and every delta-driven part follow (a pause is a near stop; Godot does not take 0)
            GD.Print($"devtools: time scale {scale:0.##} (client clock and engine)");
        }
        if (_bot && state is not null)
        {
            ReadBotAimFlags();
            if (!_devBotCaptured) { _devBotCaptured = true; _devOwnAccuracy = _botAccuracyFlag; _devOwnRange = _botRangeFlag; }
            float? accuracy = state.BotAccuracy == Protocol.DevNoValue ? _devOwnAccuracy : state.BotAccuracy / 1000f;
            if (accuracy != _botAccuracyFlag) { _botAccuracyFlag = accuracy; _botAim = null; GD.Print($"devtools: bot accuracy {(accuracy is { } a ? a.ToString("0.###") : "own")}"); }   // the aim model is rebuilt from it
            float? range = state.BotRange == 0 ? _devOwnRange : state.BotRange;
            if (range != _botRangeFlag) { _botRangeFlag = range; GD.Print($"devtools: bot range {(range is { } r ? r.ToString("0") + " m" : "own")}"); }
        }
    }

    /// <summary>Once a frame, after the network update: builds the panel when the tools are announced, runs the test script, keeps an open panel in step with the server.</summary>
    void DevToolsFrame()
    {
        if (_client is null) { if (_devScaleApplied != 1.0) DevTimeAndBots(null); return; }   // no connection: back to normal time at once
        var state = _client.Status == ClientStatus.Connected ? _client.Dev : null;   // a dropped connection (a rotation rejoin included) is back to normal time too
        if (state is null && _devPanel is null && _devScaleApplied == 1.0) return;   // a normal server: nothing
        DevTimeAndBots(state);
        if (_devPanel is null && !_bot && state is not null)
        {
            AddChild(_devPanel = NewDevPanel());
            if (DevArgs.Contains(DevToolsPanel.PanelArg)) _devPanel.SetOpen(true);
            if (DevArg(DevToolsPanel.ScriptArg) is { } script) { _devScript = script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); _devScriptNextReal = Time.GetTicksMsec() / 1000.0 + 2.0; }
        }
        if (state is not null)
        {
            string line = $"flags={(int)state.Flags} ({state.Flags}) loyalists={state.PlayersLoyalists} (tool bots {state.ToolBotsLoyalists}) chaos={state.PlayersChaos} (tool bots {state.ToolBotsChaos}) note={state.Note}#{state.NoteSerial} scale={state.TimeScale} acc={state.BotAccuracy} dmg={state.BotDamage} range={state.BotRange}";
            if (!_bot && line != _devLogged) { _devLogged = line; GD.Print("devtools: state " + line); }
        }
        if (_devPanel is null) return;
        if (_devPanel.IsOpen) _devPanel.Sync();
        if (_devScript is { } words && _devScriptAt < words.Length && state is not null && Time.GetTicksMsec() / 1000.0 >= _devScriptNextReal)
        {
            string word = words[_devScriptAt++];
            bool known = _devPanel.Press(word);
            GD.Print($"devtools: script pressed '{word}'{(known ? "" : " (unknown word)")}");
            double step = double.TryParse(DevArg(DevToolsPanel.ScriptStepArg), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && s > 0 ? s : 2.0;
            _devScriptNextReal = Time.GetTicksMsec() / 1000.0 + step;   // real seconds: the script goes on while the game is slowed or paused
        }
    }

    double _devScriptNextReal = double.PositiveInfinity;

    /// <summary>
    /// F6 shows or hides the panel (or, when the server has no developer tools, says so for a moment); Esc closes it. It does not open over the Esc menu, the customizer, the spawn choice, a Musou modal
    /// or the sound tuner (<see cref="PickerBusy"/>). While it is open every key and click that no control took is swallowed here, so a click beside the panel cannot recapture the mouse.
    /// </summary>
    bool DevToolsInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F6 })
        {
            if (_client?.Dev is null)
            {
                if (_devPanel is null && _client is not null) AddChild(_devPanel = NewDevPanel());
                _devPanel?.ShowOff();
                return true;
            }
            if (_devPanel is null) return true;   // announced a moment ago: the frame builds the panel
            if (!_devPanel.IsOpen && PickerBusy) return true;
            _devPanel.Toggle();
            return true;
        }
        if (!DevPanelOpen) return false;
        if (e is InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape }) _devPanel!.SetOpen(false);
        return e is InputEventKey or InputEventMouse or InputEventJoypadButton or InputEventJoypadMotion;
    }
}
