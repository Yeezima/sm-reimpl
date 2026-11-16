using System;
using System.Linq;
using Godot;
using SmNet;

namespace SmClient;

/// <summary>
/// cancel-editor (owner, 2026-11-17): the cancel matrix panel (<see cref="CancelEditorPanel"/>), F8, built when a server announces the developer tools (the same gate as F6: <c>--dev-tools</c>, loopback only), so a
/// normal server never builds it and it costs a null check a frame. Test aids: <c>--cancel-panel</c> opens it, <c>--cancel-script</c> edits cells in turn (<c>--cancel-script-step</c> seconds apart, default 1.5).
/// </summary>
public partial class NetworkView
{
    CancelEditorPanel? _cancelPanel;
    string[]? _cancelScript;
    int _cancelScriptAt;
    double _cancelScriptNextReal = double.PositiveInfinity;

    bool CancelPanelOpen => _cancelPanel?.IsOpen == true;

    void CancelEditorFrame()
    {
        if (_client is null || _bot || MusouActive) return;   // Classic PvP only (and Musou's own F8 is the Ashfall look)
        if (_cancelPanel is null)
        {
            if (_client.Dev is null) return;
            AddChild(_cancelPanel = new CancelEditorPanel(() => _client));
            if (DevArgs.Contains(CancelEditorPanel.PanelArg)) _cancelPanel.SetOpen(true);
            if (DevArg(CancelEditorPanel.ScriptArg) is { } script) { _cancelScript = script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); _cancelScriptNextReal = Time.GetTicksMsec() / 1000.0 + 2.0; }
        }
        if (_cancelPanel.IsOpen) _cancelPanel.Sync();
        if (_cancelScript is { } words && _cancelScriptAt < words.Length && _client.Dev is not null && Time.GetTicksMsec() / 1000.0 >= _cancelScriptNextReal)
        {
            string word = words[_cancelScriptAt++];
            GD.Print($"cancel: script pressed '{word}'{(_cancelPanel.Press(word) ? "" : " (unknown word)")}");
            double step = double.TryParse(DevArg("--cancel-script-step"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && s > 0 ? s : 1.5;
            _cancelScriptNextReal = Time.GetTicksMsec() / 1000.0 + step;
        }
    }

    /// <summary>F8 shows or hides the panel when the server has the tools (F6 says so when it has not); Esc closes it. While open it swallows every key and click no control took.</summary>
    bool CancelEditorInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F8 })
        {
            if (_cancelPanel is null || _client?.Dev is null) return false;
            if (!_cancelPanel.IsOpen && (PickerBusy)) return true;
            if (!_cancelPanel.IsOpen && DevPanelOpen) return true;
            _cancelPanel.Toggle();
            return true;
        }
        if (!CancelPanelOpen) return false;
        if (e is InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape }) _cancelPanel!.SetOpen(false);
        return e is InputEventKey or InputEventMouse or InputEventJoypadButton or InputEventJoypadMotion;
    }
}
