using System.Collections.Generic;
using Godot;
using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// Musou Mode on the client. The map is <c>NetworkView.TfwMap.cs</c> (TF1). <see cref="TfwGuardsFrame"/> (TF7a) reads the sections into <see cref="_tfwState"/> and sends its commands; then the frame runs the stations (<c>TfwStations.cs</c>), the orders (<c>TfwOrders.cs</c>), the HUD (<see cref="TfwHud"/>) and the markers (<see cref="TfwMarkers"/>).
/// All of it waits for the first tag 7, which only a war's server sends.
/// </summary>
public partial class NetworkView
{
    TfwHud? _tfwHud;
    TfwMarkers? _tfwMarkers;

    /// <summary>Once a frame, with this frame's sections of tags 7 to 14 (the dispatcher <c>NetworkView.Extras.cs</c>). Runs after <see cref="MusouFrame"/>, which reads the director these need.</summary>
    void TfwFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta, List<ExtrasSection> sections)
    {
        if (!MusouActive || _client is null) return;
        TfwGuardsFrame(sections, delta);   // the sections, the commands and the Guardsmen (TF7a)
        if (_tfwState.War is not { } war) return;
        TfwOrderKeysFree();
        TfwOrdersFrame(war, me, (float)delta);
        TfwStationsFrame(war, me);
        TfwHudFrame(war, me);
        TfwMarkersFrame(war);
        TfwFireFrame(war, delta);
        TfwBarsFrame();
        TfwClassFrame(war, me);
        TfwScoreFrame(war);
        TfwRewardFrame(war);
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
        f.Objectives = _tfwState.Objectives;
        for (int k = 0; k < f.OrksOn.Length; k++) f.OrksOn[k] = k < _tfwState.Objectives.Count && _tfwState.Objectives[k].State == TfwPositionState.Clearing ? TfwOrksOn(_tfwState.Objectives[k]) : 0;
        f.Alive = me.Alive || Spectate is not null;   // a spectator was never killed
        f.Station = _tfwAt == TfwStation.None ? "" : TfwHudModel.StationPrompt(_tfwAt, ControlsInput.Prompt(GameAction.Use));
        f.PickupHold = _tfwKeys.PickupHold;
        f.Kos = _musouScore.Kos;
        _tfwHud.Fed();
        _tfwHud.Visible = _endPhase == MatchEndPhase.Splash && !_tfwMapHeld;
        // The match HUD's "RESPAWN IN n" would lie: a fallen player in the field waits for its retainer or the extraction, held by the run. TfwHud says KILLED and what happens instead. One who
        // joined after the landing is not in the field: the core's respawn runs and its line is true.
        if (!f.Alive && TfwHudModel.RunHoldsRespawn(war) && _hudView is { } hud) hud.Data.Alive = true;
        if (_musouHud is { } musou) musou.Data.BossName = TfwHudModel.BossName(war, _tfwState.Objectives);
    }

    /// <summary>The Orks this client knows standing in position <paramref name="o"/> (its radius, <see cref="TfwHudModel.ClearingReach"/> up and down).</summary>
    int TfwOrksOn(TfwObjective o) => _musouState.CountWithin(new System.Numerics.Vector3(o.Position.X, o.Position.Y, o.Position.Z), o.Radius, TfwHudModel.ClearingReach);

    /// <summary>The rings, the pillars and the beacon (a client with a game space and a look at it: never lean or headless).</summary>
    void TfwMarkersFrame(in TfwWarView war)
    {
        if (Lean || _gameSpace is null || DisplayServer.GetName() == "headless") return;
        if (_tfwMarkers is null)
        {
            _tfwMarkers = new TfwMarkers { Name = "TfwMarkers" };
            _gameSpace.AddChild(_tfwMarkers);
        }
        _tfwMarkers.Show(war, _tfwState.Objectives, _tfwState.Sites, _client?.Predicted.Position ?? default, TfwOrksOn);
        TfwHubFrame(war);
    }
}
