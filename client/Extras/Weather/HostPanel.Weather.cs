using System.Collections.Generic;
using System.Linq;
using SmClient.Customizer;
using Godot;
using SmSim.Extras.Weather;

namespace SmClient.FrontEnd;

/// <summary>
/// The Host panel's WEATHER row (weather W3; added content, the extended profile only): None (the default), the six presets, or Random (a preset per map load). The choice reaches the real server launch through
/// <c>HostOptions.Weather</c> (<c>ServerLaunch.Arguments</c>: <c>--weather &lt;key&gt;</c>); None sends nothing, so the launch of every earlier build is unchanged. The row is shown only while the lobby's content profile carries
/// weather (<see cref="WeatherClassic.Carries"/>: <c>extended</c>, never faithful, a partial list, Musou or Warfront). Test aid: <c>--host-weather rain|random|...</c> starts the row on that choice (not saved).
/// </summary>
public sealed partial class HostPanel
{
    UiKit.MenuChoice? _weatherRow;
    string _weatherKey = "none";

    partial void ExtrasHostWeatherRow(ref Control? row)
    {
        _weatherKey = WeatherClassic.NormalizeKey(_args.GetValueOrDefault("host-weather")) ?? "none";
        var choices = WeatherClassic.HostChoices;
        int index = System.Math.Max(0, choices.ToList().FindIndex(c => c.Key == _weatherKey));
        _weatherRow = new UiKit.MenuChoice("Weather", choices.Select(c => c.Label).ToArray(), index, i => { _weatherKey = choices[i].Key; },
            "Added weather for the whole match, lobby and every map of a rotation: it builds up, peaks mid-match, eases before the end and starts again with each map and round. NONE (the default) is the map as it is. RANDOM picks a different preset for each map. Looks and sounds only: damage, movement, weapons and the bots are not affected.")
        { Name = "ChoiceWeather" };
        row = _weatherRow;
    }

    partial void ExtrasHostWeather(ref string? weather)
    {
        if (WeatherCarried && _weatherKey != "none") weather = _weatherKey;
    }

    /// <summary>The lobby's profile carries Classic weather.</summary>
    bool WeatherCarried => WeatherClassic.Carries(_content());

    /// <summary>Hides the weather row from every profile that has no Classic weather (called from <c>ExtrasHostRowShown</c>).</summary>
    void WeatherRowShown(Control row, ref bool shown)
    {
        if (row == _weatherRow && !WeatherCarried) shown = false;
    }

    /// <summary>Self-test: the weather row's state, shown (it is in the tree and applies to the profile), the choice's key and the words on it.</summary>
    public (bool Exists, bool Applies, string Key, string Value) WeatherRowState => (_weatherRow is not null, _weatherRow is not null && WeatherCarried, _weatherKey, _weatherRow?.Value ?? "");

    /// <summary>Self-test: chooses a weather as a click on the row would (an unknown key is ignored).</summary>
    public void ChooseWeatherForTest(string key)
    {
        var choices = WeatherClassic.HostChoices;
        int i = choices.ToList().FindIndex(c => c.Key == WeatherClassic.NormalizeKey(key));
        if (_weatherRow is null || i < 0) return;
        _weatherRow.SetIndex(i);
        _weatherKey = choices[i].Key;
    }

    /// <summary>The weather the next hosted match would be given (null: None, or a profile without weather); what <c>StartHosting</c> puts in <c>HostOptions.Weather</c>.</summary>
    public string? WeatherChosen { get { string? w = null; ExtrasHostWeather(ref w); return w; } }
}
