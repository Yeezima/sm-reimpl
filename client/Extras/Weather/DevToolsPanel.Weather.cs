using System.Collections.Generic;
using Godot;
using SmClient.Extras.Weather;
using SmNet.Extras.Weather;
using SmSim.Extras.Weather;

namespace SmClient;

/// <summary>
/// The F6 panel's Weather section (weather W1): pick a preset (it starts at its opening phase), NONE (removes the added weather at once) or FOLLOW MATCH (the match's own weather again, at its current phase). Everything is
/// the server's: the buttons send a weather command (<c>WeatherWire.CommandTag</c>), the server obeys only under the developer-tools gate (enabled, loopback bind, loopback client) and the status line shows what it reports.
/// The section appears only on a server that carries weather. The override is never saved. Script words (<c>--dev-script</c>): <c>weather:clear</c>, <c>weather:low_fog</c>, <c>weather:rain</c>, <c>weather:none</c>, <c>weather:follow</c>.
/// </summary>
public partial class DevToolsPanel
{
    VBoxContainer? _wxBox;
    Label? _wxStatus;
    readonly Dictionary<WeatherId, Button> _wxButtons = new();

    partial void ExtrasPanelBuild(VBoxContainer column)
    {
        _wxBox = new VBoxContainer { Name = "Weather", Visible = false };
        _wxBox.AddThemeConstantOverride("separation", 4);
        column.AddChild(_wxBox);
        _wxBox.AddChild(new Label { Text = "WEATHER (presentation only; not saved)", Modulate = new Color(0.55f, 0.8f, 1f) });
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        _wxBox.AddChild(row);
        row.AddChild(WeatherButton("NONE", "Removes the added weather at once (the map's default look).", () => WeatherSend(new WeatherCommand(WeatherCommandKind.Set, WeatherId.None))));
        row.AddChild(WeatherButton("FOLLOW MATCH", "The match's own weather again, at its current phase.", () => WeatherSend(new WeatherCommand(WeatherCommandKind.Follow))));
        var presets = new HBoxContainer();
        presets.AddThemeConstantOverride("separation", 6);
        _wxBox.AddChild(presets);
        foreach (var def in WeatherCatalog.All)
        {
            if (def.Id == WeatherId.None) continue;
            var id = def.Id;
            var b = WeatherButton(def.Short, $"{def.Name}: starts at its opening phase{(def.Real ? "" : " (not built yet: draws the default look)")}.", () => WeatherSend(new WeatherCommand(WeatherCommandKind.Set, id)));
            if (!def.Real) b.Modulate = new Color(1f, 1f, 1f, 0.55f);
            presets.AddChild(b);
            _wxButtons[id] = b;
        }
        _wxStatus = new Label { Text = "", Modulate = new Color(0.85f, 0.85f, 0.85f), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _wxBox.AddChild(_wxStatus);
    }

    static Button WeatherButton(string text, string tip, System.Action pressed)
    {
        var b = new Button { Text = text, TooltipText = tip, CustomMinimumSize = new Vector2(0, 0) };
        b.Pressed += pressed;
        return b;
    }

    static void WeatherSend(WeatherCommand command) => WeatherClientLink.Send?.Invoke(WeatherWire.Encode(command));

    partial void ExtrasPanelPress(string name, string arg, ref bool handled)
    {
        if (name != "weather" || !WeatherClientLink.Available) return;
        if (arg == "follow") { WeatherSend(new WeatherCommand(WeatherCommandKind.Follow)); handled = true; return; }
        if (WeatherCatalog.ByKey(arg) is { } def) { WeatherSend(new WeatherCommand(WeatherCommandKind.Set, def.Id)); handled = true; }
    }

    partial void ExtrasPanelSync()
    {
        if (_wxBox is null) return;
        bool on = WeatherClientLink.Available;
        _wxBox.Visible = on;
        if (!on || _wxStatus is null || WeatherClientLink.Model is not { } model) return;
        var s = model.State;
        var def = WeatherCatalog.Of(s.Id);
        _wxStatus.Text = !model.Has ? "Waiting for the server's weather."
            : model.Overridden ? $"Showing {def.Name}, {s.Phase.ToString().ToLowerInvariant()} (set here; Follow Match returns to the match's own)."
            : $"Following the match: {def.Name}, {s.Phase.ToString().ToLowerInvariant()}.";
        foreach (var (id, b) in _wxButtons)
            b.AddThemeColorOverride("font_color", model.Overridden && s.Id == id ? new Color(0.55f, 1f, 0.55f) : new Color(0.85f, 0.85f, 0.85f));
    }
}
