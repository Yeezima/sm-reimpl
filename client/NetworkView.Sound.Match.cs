using System;
using System.Linq;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Fidelity gap 9: the match sounds the original's scripts post and the first version missed. The Seize Ground ones (<see cref="SeizeSounds"/>, tested): the capture tick
/// <c>play_mp_point_being_captured</c> (and its stop) while the local player stands on a point that is moving, <c>Play_Objective_Complete</c> when a point passes to the local
/// side, <c>Play_MP_Points_Total</c> when the local side's score rises; the events are the data's (<c>In_Game_Default</c>), the moments are ours (spec "Match sounds
/// (fidelity gap 9)"). The menus' sounds (<see cref="UiSounds"/>) are handed to this view when a match starts, so the in-match customizer's buttons sound through the match's
/// sound system. All of them play on one non-positioned emitter. <c>--log-sound match</c> prints each post with the voices it resolved to.
/// </summary>
public partial class NetworkView
{
    readonly SeizeSounds _seizeSounds = new();
    SoundEmitter? _uiEmitter;
    bool _uiConnected;
    double _voiceLogAt;

    /// <summary>Once the sound system exists: the menus' buttons post through it (see <see cref="UiSounds.Redirect"/>).</summary>
    void ConnectUiSounds()
    {
        if (_uiConnected || _sound is null) return;
        _uiConnected = true;
        // A match started without the front end (--connect) has no menu node yet: make one to carry the picker's sounds (not for a bot: it has no menu).
        var ui = UiSounds.Current ?? (_bot ? null : UiSounds.Ensure(GetParent() ?? this, _gameDir, NoSound));
        ui?.Redirect(e => PostUiEvent(e, "ui"), this);
        if (CmdFlag("ui-sound-probe")) ui?.Play(UiSoundRules.Select);   // test aid: one menu sound through the redirect, for --log-sound ui
    }

    /// <summary>Posts an event on the non-positioned UI emitter; counted as <paramref name="kind"/>, logged with <c>--log-sound match</c> (or <c>ui</c>).</summary>
    void PostUiEvent(string eventName, string kind, bool once = false)
    {
        if (_sound is null) return;
        _uiEmitter ??= new SoundEmitter { Label = "ui" };
        var play = _sound.Post(eventName, _uiEmitter, once: once);
        CountSound(kind);
        if (LogSound("match") || (kind == "ui" && LogSound("ui")))
            GD.Print($"MATCH SOUND t {_connectedFor:F3} {kind} event {eventName} | {VoicesText(play)}");
    }

    /// <summary>Per frame in Seize Ground: the capture tick, the objective noise and the score tick.</summary>
    void SoundSeize(PlayerState me)
    {
        if (_sound is null || _client is not { Status: ClientStatus.Connected } client || !SeizeGround || client.Scoreboard?.Seize is not { } layout)
        {
            _seizeSounds.Reset();
            return;
        }
        var points = client.Points;
        int inside = -1;
        if (me.Alive)
            for (int i = 0; i < layout.Points.Length; i++)
            {
                ref readonly var p = ref layout.Points[i];   // the cylinder test of SmSim.ControlPoint.Contains, without a record per frame
                float dx = me.Position.X - p.Center.X, dy = me.Position.Y - p.Center.Y;
                if (dx * dx + dy * dy <= p.Radius * p.Radius && MathF.Abs(me.Position.Z - p.Center.Z) <= p.HalfHeight) { inside = i; break; }
            }
        var match = client.Match;
        int score = me.Team == Team.Chaos ? match.ChaosScore : me.Team == Team.SpaceMarines ? match.MarineScore : 0;
        if (LogSound("match") && _connectedFor >= _voiceLogAt)
        {
            _voiceLogAt = _connectedFor + 5.0;
            GD.Print($"MATCH VOICES t {_connectedFor:F1} {_sound.Summary(6)}");   // test aid: the live voice count must stay flat while nothing is posted
        }
        var events = _seizeSounds.Step(points, inside, me.Team, score, _connectedFor);   // a buffer reused by the next step
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            PostUiEvent(SeizeSounds.EventOf(ev.Kind), ev.Kind switch
            {
                SeizeSoundKind.CaptureTick => "seize capture tick",
                SeizeSoundKind.CaptureStop => "seize capture stop",
                SeizeSoundKind.ObjectiveComplete => "seize objective complete",
                _ => "seize score tick",
            }, SeizeSounds.SingleShot(ev.Kind));
        }
    }

    /// <summary>Decodes ahead the Seize Ground events (a tick must not be late).</summary>
    void PrefetchMatchSounds()
    {
        if (_audio is null || _sound is null) return;
        var wems = new[] { SeizeSounds.CaptureTickEvent, SeizeSounds.ObjectiveCompleteEvent, SeizeSounds.PointsTotalEvent }.SelectMany(_audio.WemsOf).Distinct().ToList();
        _sound.Prefetch(wems);
    }
}
