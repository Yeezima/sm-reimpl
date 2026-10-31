using System.Collections.Generic;
using Godot;
using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The Three-Front War on the client. The map is <c>NetworkView.TfwMap.cs</c> (TF1). TF7b: the war's own sections (tags 7 and 8) go into <see cref="_tfwWar"/>, the commands waiting there go out under
/// <see cref="TfwWire.CommandTag"/>, and the frame runs the stations (<c>TfwStations.cs</c>), the orders (<c>TfwOrders.cs</c>), the HUD (<see cref="TfwHud"/>) and the markers (<see cref="TfwMarkers"/>).
/// All of it waits for the first tag 7, which only a war's server sends.
/// </summary>
public partial class NetworkView
{
    readonly TfwClientState _tfwWar = new();
    TfwHud? _tfwHud;
    TfwMarkers? _tfwMarkers;

    /// <summary>Once a frame, with this frame's sections of tags 7 to 10 (the dispatcher <c>NetworkView.Extras.cs</c>). Runs after <see cref="MusouFrame"/>, so the director's box is this frame's.</summary>
    void TfwFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta, List<ExtrasSection> sections)
    {
        if (!MusouActive || _client is not { } client) return;
        double now = Time.GetTicksUsec() / 1e6;
        var box = _musouState.Director?.Box.Sane();
        foreach (var section in sections)
            if (section.Tag is TfwWire.WarTag or TfwWire.ObjectivesTag) _tfwWar.Apply(section, box, now);
        if (_tfwWar.War is not { } war) return;
        if (_tfwWar.Commands.Due(now) is { } payload) client.SendExtrasCommand(TfwWire.CommandTag, payload);
        TfwOrderKeysFree();
        TfwOrdersFrame(war, me, (float)delta);
        TfwStationsFrame(war, me);
        TfwHudFrame(war, me);
        TfwMarkersFrame(war);
        TfwBotFrame(war, me);
        TfwTestFrame();
    }

    /// <summary>The HUD's frame (a client that draws, or the scripted test: a lean bot has no HUD). <see cref="MusouHud"/> keeps the boss bar: here it learns whose it is.</summary>
    void TfwHudFrame(in TfwWarView war, PlayerState me)
    {
        if (Lean) return;
        if (_tfwHud is null && _hudView is not null)
        {
            _tfwHud = new TfwHud();
            _hudView.AddChild(_tfwHud);
        }
        if (_tfwHud is null) return;
        var f = _tfwHud.Data;
        f.War = war;
        f.Objectives = _tfwWar.Objectives;
        f.Alive = me.Alive;
        f.Station = _tfwAt == TfwStation.None ? "" : TfwHudModel.StationPrompt(_tfwAt, ControlsInput.Prompt(GameAction.Use));
        f.PickupHold = _tfwKeys.PickupHold;
        f.Kos = _musouScore.Kos;
        _tfwHud.Fed();
        _tfwHud.Visible = _endPhase == MatchEndPhase.Splash && !_tfwMapHeld;
        // The match HUD's "RESPAWN IN n" would lie: a fallen player waits for its retainer or the extraction, held by the run. TfwHud says KILLED and what happens instead.
        if (!me.Alive && TfwHudModel.InBattle(war.Phase) && _hudView is { } hud) hud.Data.Alive = true;
        if (_musouHud is { } musou) musou.Data.BossName = war.Phase == TfwRunPhase.FinalAssault || _tfwWar.Objectives.Count == 0 ? "ORK WARBOSS" : "ORK COMMANDER";
    }

    /// <summary>The rings, the pillars and the beacon (a client with a game space and a look at it: never lean or headless).</summary>
    void TfwMarkersFrame(in TfwWarView war)
    {
        if (Lean || _gameSpace is null || DisplayServer.GetName() == "headless") return;
        if (_tfwMarkers is null)
        {
            _tfwMarkers = new TfwMarkers { Name = "TfwMarkers" };
            _gameSpace.AddChild(_tfwMarkers);
        }
        _tfwMarkers.Show(war, _tfwWar.Objectives);
    }
}
