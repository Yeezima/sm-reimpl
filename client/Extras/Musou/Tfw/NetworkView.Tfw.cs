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

    /// <summary>Once a frame, with this frame's sections of tags 7 to 15 (the dispatcher <c>NetworkView.Extras.cs</c>). Runs after <see cref="MusouFrame"/>, which reads the director these need.</summary>
    void TfwFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta, List<ExtrasSection> sections)
    {
        if (!MusouActive || _client is null) return;
        TfwGuardsFrame(sections, delta);   // the sections, the commands and the Guardsmen (TF7a)
        Ph("tfw: orders, stations, hud, markers");   // musou/perf-pass: --frame-profile's phases
        if (_tfwState.War is not { } war) { TfwLibrarianFrame(me, others, delta); return; }   // the 5-wave run: no war, but a Librarian's tag 16 still comes
        TfwOrderKeysFree();
        TfwOrdersFrame(war, me, (float)delta);
        TfwRedeployFrame(war, me);   // NetworkView.TfwFlow.cs: a fallen player's deploy for a supply
        Ph("tfw: stations");   // polish pass P3: the landing's first frame named by its part
        TfwStationsFrame(war, me);
        Ph("tfw: hud");
        TfwHudFrame(war, me);
        TfwBossOrksFrame(war);   // the roaming Boss Orks' bars and call-outs (NetworkView.TfwBossOrks.cs)
        Ph("tfw: markers");
        TfwMarkersFrame(war);
        Ph("tfw: fire, clouds");
        TfwFireFrame(war, delta);
        TfwFireCamFrame();
        AutocannonCamFrame();   // Arsenal/Autocannon/NetworkView.AutocannonCapture.cs: the capture's third-person frames
        SpearCamFrame();   // Arsenal/Spear/NetworkView.SpearCapture.cs: the same for the spear's stills
        ClawCamFrame();   // Arsenal/Claw/NetworkView.ClawCapture.cs: the lightning claw's capture views
        TfwSwarmCamFrame();
        TfwCloudsFrame();
        Ph("tfw: class, librarian, score, tests");
        TfwClassFrame(war, me);
        TfwLibrarianFrame(me, others, delta);   // Librarian/NetworkView.TfwLibrarian.cs: tag 16
        TfwScoreFrame(war, me);   // NetworkView.TfwRewards.cs
        TfwRewardFrame(war, me);
        TfwBotFrame(war, me);
        TfwCoopBotFrame(war, me);   // NetworkView.TfwCoopBot.cs: co-op's --bot (deploy, log)
        TfwTestFrame();
        TfwRangeTestFrame();   // the Firing Range script (NetworkView.TfwRangeTest.cs); off without --tfw-test-range
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
        f.MapPoints = _tfwState.ScoreOwn?.MapPoints ?? 0;   // tag 12: the server's, never the client's tracker
        f.RunTotal = _tfwState.ScoreOwn?.RunTotal ?? 0;
        f.Slots = TfwSlotsLine(me);
        f.OnPosition = me.Alive ? TfwMarkerRules.OnPosition(_tfwState.Objectives, me.Position) : -1;   // the capture tracker's wide bar (TfwHud.Capture.cs)
        if (MusouHudDemoCampaign && TfwHudModel.InBattle(war.Phase))   // --musou-hud-demo campaign: the on-point bar, a pickup hold and the drop prompt on (the edge layout's screenshots)
        {
            if (f.OnPosition < 0 && f.Objectives.Count > 0) f.OnPosition = 0;
            f.PickupHold = 0.4f;
            MusouDropPromptShow("[E] PICK UP PLASMA PISTOL");
        }
        _tfwHud.Fed();
        if (_tfwMapHeld) _tfwPeek?.Battle?.Feed(f);
        _tfwHud.Visible = _endPhase == MatchEndPhase.Splash && !_tfwMapHeld;
        // The match HUD's "RESPAWN IN n" would lie: a fallen player in the field waits for its retainer or the extraction, held by the run. TfwHud says KILLED and what happens instead. One who
        // joined after the landing is not in the field: the core's respawn runs and its line is true.
        if (!f.Alive && TfwHudModel.RunHoldsRespawn(war) && _hudView is { } hud) hud.Data.Alive = true;
        if (_musouHud is { } musou)
        {
            musou.Data.BossName = TfwHudModel.BossName(war, _tfwState.Objectives);
            // The support call's cell (owner 2026-11-12; MusouHud.Cells.cs): B's artillery strike or drop pod and its cooldown, in a battle.
            if (_supportAt < _connectedFor - 1.0 || _supportAt > _connectedFor) { _supportAt = _connectedFor; _supportShown = TfwSupportCommand(me); }
            bool drop = _supportShown == TfwCommand.DropPod;
            float wait = MusouHudDemoCampaign ? 37f : war.Own.OrbitalSeconds;
            musou.Data.Cells[2] = new AbilityCell
            {
                Shown = TfwHudModel.InBattle(war.Phase) && me.Alive, Kind = drop ? AbilityKind.DropPod : AbilityKind.Artillery,
                Key = PadHost.Active ? "D-LEFT" : "B", Left = wait, Total = SmSim.Extras.Musou.Campaign.TfwFireRules.OrbitalCooldownSeconds,
            };
            musou.Data.JumpMeterAbove = _hudView?.Data.HasJump ?? false;
        }
    }

    double _supportAt = double.NegativeInfinity;
    TfwCommand _supportShown = TfwCommand.Orbital;

    /// <summary>The Orks this client knows standing in position <paramref name="o"/> (its radius, <see cref="TfwHudModel.ClearingReach"/> up and down).</summary>
    int TfwOrksOn(TfwObjective o) => _musouState.CountWithin(new System.Numerics.Vector3(o.Position.X, o.Position.Y, o.Position.Z), o.Radius, TfwHudModel.ClearingReach);

    /// <summary>The rings, the pillars and the beacon (a client with a game space and a look at it: never lean or headless).</summary>
    void TfwMarkersFrame(in TfwWarView war)
    {
        if (Lean || _gameSpace is null || DisplayServer.GetName() == "headless") return;
        if (_tfwMarkers is null)
        {
            _tfwMarkers = new TfwMarkers { Name = "TfwMarkers", GameDir = _gameDir };   // the capture banners read their models from the install (TfwBanners.cs)
            _gameSpace.AddChild(_tfwMarkers);
        }
        _tfwMarkers.Show(war, _tfwState.Objectives, _tfwState.Sites, _client?.Predicted.Position ?? default);
        Ph("tfw: hub");   // polish pass P3: the hub apart from the markers in the frame profile
        TfwHubFrame(war);
    }
}
