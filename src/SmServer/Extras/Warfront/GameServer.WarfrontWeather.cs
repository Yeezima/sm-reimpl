using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Maps;
using SmSim.Extras.Warfront.Weather;
using SmSim.Extras.Weather;

namespace SmServer;

/// <summary>
/// Warfront's weather (weather W1): the campaign's forecast for this stage's lane slot is the match's baseline while a battle is on; the ship (the hub), the travel, the HQ and the fortress keep the default look
/// (None), so the Thunderhawk is never drawn in rain. The forecast is in the save and is read, never rolled, here: a wipe that returns to the same slot, a reconnect and a restarted stage all read the same
/// forecast. Presentation only. The developer-tools override sits on the director (GameServer.Weather.cs) and is cleared whenever the baseline is rebased.
/// </summary>
public sealed partial class GameServer
{
    (WarfrontPhase Phase, int Deployments) _wfWeatherAt;

    WeatherId WarfrontWeatherBaseline(WarfrontServerState s)
    {
        if (s.Campaign.Phase != WarfrontPhase.Battle || Map is null) return WeatherId.None;
        return WarfrontSlots.ByName(Map.Name) is { } slot ? WarfrontForecast.ForSlot(s.Campaign.Forecast, slot.Number) : WeatherId.None;
    }

    void WarfrontWeatherBind(WarfrontServerState s)
    {
        WeatherBind(WarfrontWeatherBaseline(s));
        _wfWeatherAt = (s.Campaign.Phase, s.Campaign.Deployments);
    }

    /// <summary>Once a tick: a new phase or a new deployment of the run is a new battle, so its weather starts again from the saved forecast and any developer-tools override is gone.</summary>
    void WarfrontWeatherStep(WarfrontServerState s)
    {
        var at = (s.Campaign.Phase, s.Campaign.Deployments);
        if (at == _wfWeatherAt) return;
        _wfWeatherAt = at;
        WeatherRebase(WarfrontWeatherBaseline(s));
    }
}
