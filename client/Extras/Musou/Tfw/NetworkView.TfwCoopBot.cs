using System;
using System.Linq;
using Godot;
using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// Musou Mode co-op's test bot (2026-11-06; test script, not an AI of the original): <c>--bot</c> on a Musou Mode server (without <c>--tfw-bot</c> or <c>--tfw-test</c>) plays with the team.
/// In the Stormbird it stands, and <c>--coop-deploy-after s</c> (default 20) seconds after it joined it sends a Deploy to front B (<c>--coop-lane a|b|c</c>; any one of the team's Deploys
/// takes everyone, the others are dropped by the server). In a battle it fights the nearest Ork it is shown within <see cref="CoopSight"/> m (150): melee within <see cref="CoopMelee"/> m,
/// fire (pressed and released every other tick, so semi-automatic and charged weapons fire too) within <see cref="CoopFire"/> m while strafing, and walks to it beyond (straight, with
/// detours when stuck; the navigation graph's path with <c>--coop-nav</c>; sprinting far off); with no Ork shown it walks to the nearest position not secured. Every 5 s a <c>COOPBOT</c> line: the phase, its life, its target, shots, swings,
/// Orks it killed (the deaths shown with its id as the killer), and the other humans and AI Marines it sees. <c>--coop-bot-off</c> gives the plain bot back.
/// </summary>
public partial class NetworkView
{
    const float CoopSight = 150f, CoopFire = 35f, CoopMelee = 2.6f, CoopSprint = 20f, CoopChest = 1.0f;
    static readonly bool CoopBotOff = CmdFlag("coop-bot-off"), CoopNav = CmdFlag("coop-nav");
    double _coopJoinedAt = -1, _coopDeploySent = double.NegativeInfinity, _coopLogAt;
    int _coopShots, _coopSwings, _coopKills, _coopDeploys;
    string _coopNote = "";
    double? _coopDeployAfter;
    TfwLane? _coopLane;

    bool CoopBotOn => _bot && MusouActive && !TfwBotOn && !TfwTestOn && !CoopBotOff && _client is not null;

    double CoopDeployAfter => _coopDeployAfter ??= double.TryParse(CmdArg("coop-deploy-after"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && s >= 0 ? s : 20.0;
    TfwLane CoopLane => _coopLane ??= CmdArg("coop-lane")?.ToLowerInvariant() switch { "a" => TfwLane.A, "c" => TfwLane.C, _ => TfwLane.B };

    partial void ExtrasBotInput(PlayerState me, uint tick, ref PlayerInput? input)
    {
        if (!CoopBotOn || _tfwState.War is not { } war) return;
        var still = new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, _pitch);
        if (war.Phase is not (TfwRunPhase.Battle or TfwRunPhase.FinalAssault or TfwRunPhase.Pickup)) { _coopNote = $"waiting in {war.Phase}"; _navAgent?.Reset(); input = still; return; }
        if (!me.Alive) { input = still; return; }
        var here = new Vector2(me.Position.X, me.Position.Y);
        if (_musouState.NearestTo(me.Position, CoopSight) is { } ork)
        {
            float dx = ork.X - me.Position.X, dy = ork.Y - me.Position.Y, flat = MathF.Sqrt(dx * dx + dy * dy);
            float dz = ork.Z + CoopChest - (me.Position.Z + _config!.EyeHeight);
            if (flat <= CoopFire)
            {
                _yaw = MathF.Atan2(-dx, dy);
                _pitch = Math.Clamp(MathF.Atan2(dz, MathF.Max(flat, 0.1f)), -MaxPitchDown, MaxPitchUp);
                if (CoopLibrarianInput(me, tick, flat) is { } psychic) { input = psychic; return; }   // Librarian/NetworkView.LibrarianLook.cs: his powers
                if (flat <= CoopMelee && Loadouts.Melee(_config, me).Count > 0)
                {
                    bool press = !me.MeleeHeld && !me.MeleeHeavyHeld && !me.MeleeQueued;
                    if (press) _coopSwings++;
                    _coopNote = $"melee an Ork at {flat:F1} m";
                    input = new PlayerInput(tick, 0f, 0f, _yaw, press ? InputButtons.Melee : InputButtons.None, _pitch);
                    return;
                }
                bool fire = (tick & 1) == 0;
                if (fire) _coopShots++;
                int id = Math.Max(_client!.PlayerId, 0);
                float side = (int)(_connectedFor / 3.0) % 2 == id % 2 ? 1f : -1f;
                float strafe = CoopNav ? BotWalkableStrafe(me, side) : side;
                _coopNote = $"firing at an Ork {flat:F0} m";
                input = new PlayerInput(tick, strafe, 0f, _yaw, fire ? InputButtons.Fire : InputButtons.None, _pitch);
                return;
            }
            input = CoopWalk(me, tick, ork, $"Ork {flat:F0} m");
            return;
        }
        var open = _tfwState.Objectives.Where(o => o.State != TfwPositionState.Secured).OrderBy(o => new Vector2(o.Position.X, o.Position.Y).DistanceSquaredTo(here)).Select(o => (System.Numerics.Vector3?)o.Position).FirstOrDefault();
        input = open is { } goal ? CoopWalk(me, tick, goal, "the nearest open position") : still;
        if (open is null) _coopNote = "no Ork, no open position";
    }

    PlayerInput CoopWalk(PlayerState me, uint tick, System.Numerics.Vector3 goal, string purpose)
    {
        // The straight walk with detours (BotWalkDirection): the bots' graph of Musou Mode's whole map is built on the first walk and stalled every client of a 4-bot run for 30 to
        // 130 s on a loaded PC (2026-11-06), so it is opt-in (--coop-nav).
        var (to, speed, _, dropAhead) = CoopNav ? BotSteer(me, BotPathGoal(goal), "coop: " + purpose) : (BotWalkDirection(me, new Vector2(goal.X, goal.Y)), 1f, false, false);
        float distance = new Vector2(me.Position.X, me.Position.Y).DistanceTo(new Vector2(goal.X, goal.Y));
        _coopNote = $"walking to {purpose}";
        if (to.LengthSquared() < 1e-6f) return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);
        _yaw = MathF.Atan2(-to.X, to.Y);
        _pitch = 0f;
        return BotMoveInput(tick, to * speed, (distance > CoopSprint && !dropAhead && _navAgent is not { OnJumpLink: true } ? InputButtons.Sprint : InputButtons.None) | (CoopNav ? _navJump : InputButtons.None));
    }

    /// <summary>Once a frame (the war's frame): the Deploy from the Stormbird and the log line.</summary>
    void TfwCoopBotFrame(in TfwWarView war, PlayerState me)
    {
        if (!CoopBotOn || _client is not { } client) return;
        if (_coopJoinedAt < 0) { _coopJoinedAt = _connectedFor; GD.Print($"t {_connectedFor:F1} s: COOPBOT id {client.PlayerId} joined in {war.Phase}; deploys to front {CoopLane} after {CoopDeployAfter:0.#} s in the Stormbird"); }
        if (war.Phase == TfwRunPhase.Hub && client.Match.State == MatchState.Playing && me.Alive && _connectedFor - _coopJoinedAt >= CoopDeployAfter && _connectedFor - _coopDeploySent > 3.0)
        {
            _coopDeploySent = _connectedFor;
            _coopDeploys++;
            _tfwState.Commands.Issue(TfwCommand.Deploy, (byte)CoopLane);
            GD.Print($"t {_connectedFor:F1} s: COOPBOT id {client.PlayerId} sends Deploy to front {CoopLane}");
        }
        if (_connectedFor - _coopLogAt < 5.0) return;
        _coopLogAt = _connectedFor;
        int humans = _lastOthers.Count(o => !TfwContracts.IsPuppet(o.PlayerId)), marines = _lastOthers.Count(o => TfwContracts.IsPuppet(o.PlayerId));
        string seen = string.Join(" ", _lastOthers.Where(o => !TfwContracts.IsPuppet(o.PlayerId)).Select(o => $"P{o.PlayerId} {o.Position.X:F0},{o.Position.Y:F0} {(o.Alive ? "alive" : "down")}"));
        GD.Print($"t {_connectedFor:F0} s: COOPBOT id {client.PlayerId} phase {war.Phase} {(me.Alive ? "alive" : "down")} hp {me.Health:F0} at {me.Position.X:F0},{me.Position.Y:F0},{me.Position.Z:F0} [{_coopNote}] " +
                 $"shots {_coopShots} swings {_coopSwings} kills {_coopKills} deploys sent {_coopDeploys}; Orks shown {_musouState.Count}; other humans {humans} [{seen}], AI Marines {marines}");
    }

    /// <summary>The deaths shown this frame that name this client as the killer (before the crowd state forgets them).</summary>
    void TfwCoopBotDeaths()
    {
        if (!CoopBotOn || _client is not { } client) return;
        foreach (var d in _musouState.NewDeaths) if (d.Killer == client.PlayerId) _coopKills++;
    }
}
