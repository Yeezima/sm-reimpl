using System;
using SmSim;

namespace SmBots;

/// <summary>M29: the bot's charge (test script, not original behaviour; moved from the client's NetworkView.Reactions.cs, whose reaction and charge poses stay there).</summary>
public sealed partial class ClassicBotBrain
{
    double _botRushNext = 6.0;

    // feel/charge-directions test aids (the client's NetworkView.ChargeDirections.cs keeps their flags, tables and measurements; IBotHooks.ChargeDirs/DashDirs).
    int _botChargeDir, _botDashDir;
    double _botChargeRunSince = -1;

    /// <summary>
    /// The bot's charge (test script, M29): every ~8 s, with a target 4 to 9 m away and a charge available, it runs at the target
    /// (W, sprint) and presses F (the heavy button) once the charge can start (protocol 49: Shift and W held); then it lets the rush play without input.
    /// <c>--bot-charge-dirs</c> (the client's NetworkView.ChargeDirections.cs): W only for its run-up, turned away from the target, then F with W, W + A, W + D in turn.
    /// </summary>
    PlayerInput? BotRushInput(PlayerState me, uint tick, float distanceSquared)
    {
        if (Loadouts.Rush(_config!, me) is not { } spec || !me.Alive) return null;
        if (me.Rush != RushPhase.None) { _botChargeRunSince = -1; return new PlayerInput(tick, 0f, 0f, BotRushYaw(me, false), InputButtons.None, _pitch); }   // stream C: a missed charge keeps its turn
        bool dirs = BotChargeDirs;
        // --bot-charge-dirs runs away from the target during its run-up, so it starts at 3 to 30 m (it needs no target in reach).
        if (_connectedFor < _botRushNext || (dirs ? distanceSquared is < 9f or > 900f : distanceSquared is < 16f or > 81f) || me.RushLock > 0) { _botChargeRunSince = -1; return null; }
        if (dirs) _yaw += MathF.PI;   // away from the target: no melee lock steers the charge (its rule: a locked charge starts along the view)
        var run = new PlayerInput(tick, 0f, 1f, _yaw, dirs ? InputButtons.None : InputButtons.Sprint, _pitch);
        if (dirs && _botChargeRunSince < 0 && me.Grounded && me.MeleeStep == 0) _botChargeRunSince = _connectedFor;
        bool runUp = dirs && (_botChargeRunSince < 0 || _connectedFor - _botChargeRunSince < ChargeDirsRunUp);
        // protocol 49: the press needs Sprint held as well as W (--bot-charge-dirs runs up without it, then holds Shift for the press).
        var press = run with { Buttons = run.Buttons | InputButtons.Sprint };
        if (runUp || !Rush.CanStart(me, press, spec, _config!)) return me.Grounded && me.MeleeStep == 0 ? run : null;
        _botRushNext = _connectedFor + 8.0;
        _botChargeRunSince = -1;
        float forward = me.Velocity.X * -MathF.Sin(_yaw) + me.Velocity.Y * MathF.Cos(_yaw);
        var (x, keys) = dirs ? ChargeDirs[_botChargeDir++ % ChargeDirs.Length] : (0f, "W+Shift");
        NotePress(_yaw, keys, forward);
        GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} presses F with {keys} at {forward:F2} m/s forward (protocol 49: Shift + W; F without Shift is the stun strike)");
        // Stream C: some charges are deliberately started off the target (ClassicBotBrain.BotTactics.cs); not with --bot-charge-dirs (it measures the directions).
        return press with { MoveX = x, Yaw = dirs ? _yaw : BotRushYaw(me, true), Buttons = press.Buttons | (me.MeleeHeavyHeld ? InputButtons.None : InputButtons.MeleeHeavy) };
    }
}
