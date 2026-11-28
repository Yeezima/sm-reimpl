using Godot;

namespace SmClient;

/// <summary>
/// The F6 panel's hooks for added content (docs/added-content.md): a C# partial method has one implementation, so each hook is a dispatcher here that calls every feature in a fixed order: the
/// Mapmaker's CUSTOM MAP row (client/Extras/Mapmaker/DevToolsPanel.Mapmaker.cs, devtools-god-custommap) and weather's section (client/Extras/Weather/DevToolsPanel.Weather.cs). Each shows nothing
/// unless the server it is connected to offers it.
/// </summary>
public partial class DevToolsPanel
{
    partial void ExtrasPanelBuild(VBoxContainer column) => WeatherPanelBuild(column);

    partial void ExtrasPanelMapRows(VBoxContainer column) => MapmakerPanelRows(column);

    partial void ExtrasPanelOpened() => MapmakerPanelOpened();

    partial void ExtrasPanelPress(string name, string arg, ref bool handled)
    {
        MapmakerPanelPress(name, arg, ref handled);
        if (!handled) WeatherPanelPress(name, arg, ref handled);
    }

    partial void ExtrasPanelSync()
    {
        MapmakerPanelSync();
        WeatherPanelSync();
    }
}
