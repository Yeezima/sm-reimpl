using System;
using System.Collections.Generic;
using Godot;
using SmSim;
using SmSim.Extras.Executions;

namespace SmClient;

/// <summary>
/// Executions X5: the paired animation (docs/added-content.md section 13). Every execution the server announces (<see cref="ExecutionsState"/>) is drawn:
/// <list type="bullet">
/// <item>The executor's own character (<see cref="ExecutionsPlace"/>, from the core's <c>Place</c>): the row's attacker clip, the two distance variants either side of the run's
/// distance blended by <see cref="ExecutionRow.Blend"/> (<see cref="ExecutionPresentMath.AttackerClips"/>), at the row's clock from the server's tick clock (clip time 0 at start +
/// slide ticks; the interpolated render tick for others, the drawn predicted tick for the own player), feet and yaw from X3's <see cref="ExecutionTrack"/> (the server pin's function).
/// A clip with extracted motion plays in place and the node follows the track; a clip whose root bone travels is placed at the aligned origin and moves itself. Locomotion,
/// upper-body and turn layers are skipped (<c>handled</c>).</item>
/// <item>The victim: a separate proxy character (<see cref="ExecutionProxy"/>) at its place, playing the row's victim clip on the same clock.</item>
/// </list>
/// A Start whose wire index this install's catalogue lacks (<c>AtWire</c> null) draws nothing: the normal kill stands. Results: <b>original asset loaded</b> where the install's clips
/// play; a labelled stand-in (<see cref="ExecutionStandIns"/>, <b>placeholder demonstration</b>) otherwise.
/// </summary>
public partial class NetworkView
{
    readonly ExecutionClipLibrary _execLib = new();
    bool _execLibNoted;
    /// <summary><c>--execution-log</c>: a line per placed executor every second, per proxy and per end (test aid).</summary>
    static readonly bool ExecLog = CmdFlag("execution-log") || CmdFlag("execution-selftest") || CmdArg("execution-demo") is not null;
    readonly Dictionary<int, double> _execPlaceLogged = new();

    int ExecTickRate => _config?.TickRate ?? 30;

    /// <summary>The server tick the own player is drawn at (between the two last predicted ticks).</summary>
    double OwnDrawTick() => _client is null ? 0 : Math.Max(0, (double)_client.Tick - 1 + _client.TickFraction);

    /// <summary>The tick an executor is drawn at: the own player's predicted clock, else the remote players' render tick.</summary>
    double ExecDrawTick(int executor) => _client is not null && executor == _client.PlayerId ? OwnDrawTick() : _othersTick;

    partial void ExecutionsPlace(CharacterViewer character, PlayerState state, bool own, ref bool handled)
    {
        if (!state.Alive || !_execLib.Ready || _execLib.Catalogue is not { } catalogue || character.Assets is not { } assets) return;
        if (!ExecutionsState.TryLive(state.PlayerId, out var run) || catalogue.AtWire(run.Wire) is not { } row) return;
        double tick = ExecDrawTick(state.PlayerId);
        var pose = ExecutionPresentMath.Pose(run, row, tick, ExecTickRate);
        float distance = ExecutionPresentMath.DistanceOf(run, row);
        var (near, far, weight) = ExecutionPresentMath.AttackerClips(row, distance);
        var nearClip = _execLib.Attacker(near);
        var farClip = far is null ? null : _execLib.Attacker(far);
        character.ClearPoseLayers();
        character.ClearTurnPose();
        character.ClipFootLock = false;
        character.ClipStrideScale = 1f;
        bool inPlace = nearClip?.InPlace ?? true;
        if (_execLib.Prepare(assets.Rig, nearClip, near) && (farClip is null || _execLib.Prepare(assets.Rig, farClip, far!)))
        {
            float nearTime = ExecutionPresentMath.RawTime(pose.ClipSeconds, row.AttackerSeconds, nearClip!.Animation.Animation.Duration);
            character.Pose(ExecutionClipLibrary.Alias(near), nearTime, ClipBlends.Default);
            if (farClip is not null && weight > 0f)
                character.SetPoseLayer(ExecutionClipLibrary.Alias(far!), ExecutionPresentMath.RawTime(pose.ClipSeconds, row.AttackerSeconds, farClip.Animation.Animation.Duration), weight, false);
        }
        else
        {
            inPlace = true;
            ExecutionStandIns.Attacker(character, row, pose.ClipSeconds, _notes);
        }
        // In place: the node carries the root path (the track). A travelling root bone: the node stays at the aligned origin (at the slide's place while it slides).
        var feet = inPlace || pose.InSlide ? pose.Feet : run.Origin;
        float yaw = inPlace || pose.InSlide ? pose.Yaw : run.Yaw;
        character.Transform = new Transform3D(new Basis(new Vector3(0, 0, 1), yaw), new Vector3(feet.X, feet.Y, feet.Z));
        if (character.GetNodeOrNull<Node3D>("ChargeGlow") is { Visible: true } glow) glow.Visible = false;   // X5 review X5-6: the core's charge-glow update is skipped below this hook; no glow left on from a charge
        handled = true;
        if (ExecLog && (!_execPlaceLogged.TryGetValue(state.PlayerId, out double at) || _connectedFor - at >= 1.0))
        {
            _execPlaceLogged[state.PlayerId] = _connectedFor;
            GD.Print($"EXECPRESENT t {_connectedFor:F2} s: P{state.PlayerId} {row.Id} tick {tick:F2} clip {pose.ClipSeconds:F3}/{row.Seconds:F3} s {(pose.InSlide ? "slide " : "")}" +
                     $"{near}{(far is null ? "" : $" + {far} x{weight:F2}")} feet {feet.X:F2},{feet.Y:F2},{feet.Z:F2} yaw {yaw:F3} (track) drawn-state {state.Position.X:F2},{state.Position.Y:F2} {(own ? "own" : "remote")}");
        }
    }

    partial void ExecutionsPresentFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta)
    {
        _execLib.Start(_gameDir);
        if (!_execLib.Ready) return;
        if (!_execLibNoted)
        {
            _execLibNoted = true;
            foreach (var n in _execLib.Notes) GD.Print("  " + n);
        }
        ExecutionSelfTestFrame(me, others);   // --execution-selftest / --execution-demo (ExecutionSelfTest.cs)
        // An executor who left the match ends its move here (Disconnect).
        if (ExecutionsState.Live.Count > 0)
        {
            List<int>? gone = null;
            foreach (var id in ExecutionsState.Live.Keys)
            {
                if (_client is not null && id == _client.PlayerId) continue;
                bool seen = false;
                foreach (var o in others) if (o.PlayerId == id) { seen = true; break; }
                if (!seen) (gone ??= new()).Add(id);
            }
            if (gone is not null) foreach (var id in gone) ExecutionsState.Forget(id, _connectedFor);
        }
        ExecutionProxiesFrame(me, others, delta);
    }
}
