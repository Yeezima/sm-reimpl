using System.Collections.Generic;
using System.Linq;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// sim/player-collision (protocol 43) in the networked view: the remote players are drawn moved apart from each other and from the own player
/// (<see cref="DrawnBodies"/>, ours: interpolated bodies in the past and the predicted own body in the present can overlap although the simulation
/// keeps them apart), and a bot logs every 10 s how close the drawn bodies came and how large its prediction corrections were.
/// Test aids: <c>--no-player-collision</c> (as a server started with it: players pass through each other; config hash), <c>--no-body-separation</c>
/// (draw the interpolated places as they are).
/// </summary>
public partial class NetworkView
{
    static readonly bool NoPlayerCollision = OS.GetCmdlineUserArgs().Contains("--no-player-collision");
    static readonly bool NoBodySeparation = OS.GetCmdlineUserArgs().Contains("--no-body-separation");

    readonly DrawnBodies _drawnBodies = new();   // its buffers are reused every frame
    readonly OverlapProbe _drawnOverlaps = new();
    readonly List<System.Numerics.Vector3> _drawnFeet = new();
    double _bodiesLoggedAt;
    int _correctionsLogged;

    /// <summary>The config as the server's: player collision off with <c>--no-player-collision</c> (the config hashes then agree).</summary>
    static SimConfig WithPlayerCollisionArg(SimConfig c) => WithChargeCancelArgs((((NoPlayerCollision ? c with { PlayerBodies = false } : c) is var b && NoChargeLockArg ? b with { ChargeLock = false } : b) is var d && OldBodyContactArg ? d with { SolidBodies = false } : d) is var e && NoChargeClashArg ? e with { ChargeClash = false } : e);
    /// <summary>
    /// charge-cancel / charge-ally-bump restore switches <c>--old-charge-cancel</c> (a Grenade press ends the charge, S does not) and <c>--old-ally-bump</c> (the collision stop on an
    /// ally), the protocol 65 rules (the server needs the same: config hash).
    /// </summary>
    static SimConfig WithChargeCancelArgs(SimConfig c0) { var c = WithChargeCancelArgs0(c0); if (OldChargeMeleeChainArg) c = c with { ChargeMeleeChain = false }; if (MusouMeleeCooldownArg) c = c with { MusouMeleeCooldown = true }; if (MusouUncappedMeleeHealArg) c = c with { MusouUncappedMeleeHeal = true }; return c; }
    static SimConfig WithChargeCancelArgs0(SimConfig c) => ((((OldChargeCancelArg ? c with { ChargeBackCancel = false } : c) is var d && OldAllyBumpArg ? d with { ChargeAllyBump = false } : d) is var f && OldStimUseArg ? f with { StimReliable = false } : f) is var g && OldChargeAfterMeleeArg ? g with { ChargeAfterMelee = false } : g) is var h && OldChargeCancelDelayArg ? h with { ChargeInstantCancel = false } : h;
    static readonly bool OldStimUseArg = OS.GetCmdlineUserArgs().Contains("--old-stim-use");
    static readonly bool OldChargeCancelArg = OS.GetCmdlineUserArgs().Contains("--old-charge-cancel");
    /// <summary>charge-responsive restore switches <c>--old-charge-after-melee</c> and <c>--old-charge-cancel-delay</c> (the server needs the same: config hash).</summary>
    static readonly bool OldChargeAfterMeleeArg = OS.GetCmdlineUserArgs().Contains("--old-charge-after-melee");
    static readonly bool OldChargeCancelDelayArg = OS.GetCmdlineUserArgs().Contains("--old-charge-cancel-delay");
    /// <summary>charge-melee-chain (protocol 70) restore switches <c>--old-charge-melee-chain</c> and <c>--musou-melee-cooldown</c> (the server needs the same: config hash).</summary>
    static readonly bool OldChargeMeleeChainArg = OS.GetCmdlineUserArgs().Contains("--old-charge-melee-chain");
    static readonly bool MusouMeleeCooldownArg = OS.GetCmdlineUserArgs().Contains("--musou-melee-cooldown");
    /// <summary>musou/melee-heal-cap restore switch <c>--musou-uncapped-melee-heal</c> (the server needs the same: config hash).</summary>
    static readonly bool MusouUncappedMeleeHealArg = OS.GetCmdlineUserArgs().Contains("--musou-uncapped-melee-heal");
    static readonly bool OldAllyBumpArg = OS.GetCmdlineUserArgs().Contains("--old-ally-bump");
    /// <summary>fix/charge-player-collision test aid <c>--old-body-contact</c> (the server needs it too: config hash): the protocol 56 body and charge contact rules.</summary>
    /// <summary>feel/charge-clash test aid <c>--no-charge-clash</c> (the server needs it too: config hash): the protocol 58 stop, no clash.</summary>
    static readonly bool NoChargeClashArg = OS.GetCmdlineUserArgs().Contains("--no-charge-clash");
    static readonly bool OldBodyContactArg = OS.GetCmdlineUserArgs().Contains("--old-body-contact");
    /// <summary>anim/melee-interaction test aid <c>--no-charge-lock</c> (the server needs it too: config hash): the M29 charge contact and stop.</summary>
    static readonly bool NoChargeLockArg = OS.GetCmdlineUserArgs().Contains("--no-charge-lock");

    /// <summary>The remote players where they are drawn this frame (moved apart unless <c>--no-body-separation</c>), and the bot's overlap sample.</summary>
    IReadOnlyList<PlayerState> DrawnRemotes(IReadOnlyList<PlayerState> others, PlayerState me, PlayerState shown)
    {
        // anim/melee-interaction: the own charge's victim at the lock spot (NetworkView.MeleeInteraction.cs). feel/charge-clash: placed before the separation, so a spot the own
        // body (or another player) has since walked into is moved out like any other overlap (a bot run found the held spot drawn up to 0.36 m inside the own body).
        var locked = LockDrawn(others, me, shown);
        var drawn = NoBodySeparation ? locked : _drawnBodies.Separate(locked, me.Alive ? shown.Position : null, me.PlayerId, _config!);
        if (_bot)
        {
            _drawnFeet.Clear();
            foreach (var p in drawn) if (p.Alive) _drawnFeet.Add(p.Position);
            if (me.Alive) _drawnFeet.Add(shown.Position);
            _drawnOverlaps.Add(_drawnFeet, _config!);
            LogDrawnOverlaps(drawn, me, shown);
            if (_connectedFor - _bodiesLoggedAt >= 10.0)
            {
                _bodiesLoggedAt = _connectedFor;
                var errors = _client!.CorrectionErrors;
                var recent = errors.Skip(System.Math.Min(_correctionsLogged, errors.Count)).ToList();
                _correctionsLogged = errors.Count;
                var sorted = errors.OrderBy(e => e).ToList();
                string p99 = sorted.Count == 0 ? "-" : sorted[System.Math.Clamp((int)System.Math.Ceiling(0.99 * sorted.Count) - 1, 0, sorted.Count - 1)].ToString("F3");
                GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} bodies: drawn {_drawnOverlaps.Summary()}; prediction corrections {errors.Count} sampled, " +
                         $"{errors.Count(e => e > 0.001f)} above 1 mm, {errors.Count(e => e > 0.05f)} above 5 cm, p99 {p99} m, max {(errors.Count == 0 ? 0f : errors.Max()):F3} m " +
                         $"(last 10 s: {recent.Count(e => e > 0.001f)} above 1 mm{(recent.Count > 0 ? $", max {recent.Max():F3} m" : "")}); collision {(_config!.PlayerBodies ? "on" : "OFF")}, " +
                         $"separation {(NoBodySeparation ? "off" : "on")}");
            }
        }
        return drawn;
    }

    double _drawnOverlapLoggedAt = double.NegativeInfinity;

    /// <summary>
    /// feel/charge-clash (a bot's diagnostics): once a second at most, a DRAWNOVERLAP line for the deepest pair of drawn bodies more than 5 cm inside each other: who (the own body
    /// marked), sides, each one's charge phase, reaction and jump, and whether the own charge's victim is drawn at the lock spot, so an overlap can be told apart (allies, a charge, a jump).
    /// </summary>
    void LogDrawnOverlaps(IReadOnlyList<PlayerState> drawn, PlayerState me, PlayerState shown)
    {
        if (_connectedFor - _drawnOverlapLoggedAt < 1.0 || _config is null) return;
        var all = new List<(PlayerState P, bool Own)>();
        foreach (var p in drawn) if (p.Alive) all.Add((p, false));
        if (me.Alive) all.Add((shown with { PlayerId = me.PlayerId, Team = me.Team, Rush = me.Rush, Reaction = me.Reaction, Jump = me.Jump }, true));
        float reach = 2f * _config.CapsuleRadius, height = System.Math.Max(_config.CapsuleHeight, reach), deepest = 0.05f;
        (PlayerState P, bool Own) x = default, y = default;
        for (int i = 0; i < all.Count; i++)
            for (int j = i + 1; j < all.Count; j++)
            {
                var a = all[i].P.Position; var b = all[j].P.Position;
                if (PlayerCollision.Relate(a.Z, b.Z, height) != PlayerCollision.Relation.Side) continue;
                float depth = reach - new System.Numerics.Vector2(a.X - b.X, a.Y - b.Y).Length();
                if (depth > deepest) { deepest = depth; x = all[i]; y = all[j]; }
            }
        if (deepest <= 0.05f) return;
        _drawnOverlapLoggedAt = _connectedFor;
        string Who((PlayerState P, bool Own) w) => $"P{w.P.PlayerId}{(w.Own ? " (own)" : "")} {w.P.Team} rush {w.P.Rush}{w.P.RushTicks} reaction {w.P.Reaction} jump {w.P.Jump}";
        GD.Print($"t {_connectedFor:F2} s: DRAWNOVERLAP {deepest:F3} m: {Who(x)} / {Who(y)}; {(World.Hostile(x.P.Team, y.P.Team) ? "opponents" : "allies")}; lock draw P{_lockDraw.Victim}");
    }
}
