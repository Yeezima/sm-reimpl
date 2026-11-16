using System;
using System.Linq;
using Godot;
using SmNet;

namespace SmClient;

/// <summary>
/// dev-tools (owner, 2026-11-17): the developer panel (<see cref="DevToolsPanel"/>), F6. Built when the server first announces the tools (<see cref="GameClient.Dev"/>; a server without
/// <c>--dev-tools</c> never does, so nothing is built and nothing runs but one null check a frame). Test aids: <c>--dev-panel</c> opens the panel at once, <c>--dev-script "inv:on,add:chaos,..."</c>
/// presses the controls in turn (<c>--dev-script-step</c> seconds apart, default 2; the words are <see cref="DevToolsPanel.ControlWords"/>, with <c>:on</c> / <c>:off</c>), and every change of the
/// server's state is logged as <c>devtools: ...</c>.
/// </summary>
public partial class NetworkView
{
    static readonly string[] DevArgs = OS.GetCmdlineUserArgs();
    DevToolsPanel? _devPanel;
    string[]? _devScript;
    int _devScriptAt;
    double _devScriptNext;
    string _devLogged = "";

    static string? DevArg(string name)
    {
        int i = Array.IndexOf(DevArgs, name);
        return i >= 0 && i + 1 < DevArgs.Length && !DevArgs[i + 1].StartsWith("--") ? DevArgs[i + 1] : null;
    }

    bool DevPanelOpen => _devPanel?.IsOpen == true;

    /// <summary>Once a frame, after the network update: builds the panel when the tools are announced, runs the test script, keeps an open panel in step with the server.</summary>
    void DevToolsFrame()
    {
        if (_client is null) return;
        var state = _client.Dev;
        if (state is null && _devPanel is null) return;   // a normal server: nothing
        if (_devPanel is null && !_bot)
        {
            AddChild(_devPanel = new DevToolsPanel(() => _client?.Dev, (kind, argument, value) => _client?.SendDevCommand(kind, argument, value) == true));
            if (DevArgs.Contains(DevToolsPanel.PanelArg)) _devPanel.SetOpen(true);
            if (DevArg(DevToolsPanel.ScriptArg) is { } script) { _devScript = script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); _devScriptNext = _elapsed + 2.0; }
        }
        if (state is not null)
        {
            string line = $"flags={(int)state.Flags} ({state.Flags}) loyalists={state.PlayersLoyalists} (tool bots {state.ToolBotsLoyalists}) chaos={state.PlayersChaos} (tool bots {state.ToolBotsChaos}) note={state.Note}#{state.NoteSerial}";
            if (line != _devLogged) { _devLogged = line; GD.Print("devtools: state " + line); }
        }
        if (_devPanel is null) return;
        if (_devPanel.IsOpen) _devPanel.Sync();
        if (_devScript is { } words && _devScriptAt < words.Length && state is not null && _elapsed >= _devScriptNext)
        {
            string word = words[_devScriptAt++];
            bool known = _devPanel.Press(word);
            GD.Print($"devtools: script pressed '{word}'{(known ? "" : " (unknown word)")}");
            double step = double.TryParse(DevArg(DevToolsPanel.ScriptStepArg), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && s > 0 ? s : 2.0;
            _devScriptNext = _elapsed + step;
        }
    }

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
                if (_devPanel is null && _client is not null) AddChild(_devPanel = new DevToolsPanel(() => _client?.Dev, (kind, argument, value) => _client?.SendDevCommand(kind, argument, value) == true));
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
