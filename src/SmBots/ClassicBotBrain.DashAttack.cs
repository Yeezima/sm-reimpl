using System;
using SmSim;

namespace SmBots;

/// <summary>Protocol 30: the <c>--bot-dash-attack</c> test aid in the fight (moved from the client's NetworkView.DashAttack.cs, whose clips, effects and other test aids stay there).</summary>
public sealed partial class ClassicBotBrain
{
    double _botDashNext = 4.0;

    /// <summary>
    /// <c>--bot-dash-attack</c>: on the ground with a target 5 to 14 m away, free and the dash attack's spacing over, the bot taps Jump with the stick forward
    /// (the dash) every few seconds; during the dash's flight it presses melee as soon as the simulation's window allows (<see cref="Rush.CanDashAttack"/>) and then
    /// lets the attack play without input. Null otherwise (the bot's other behaviour).
    /// </summary>
    PlayerInput? BotDashAttackInput(PlayerState me, PlayerState target, uint tick)
    {
        if (!BotDashAttack || Loadouts.JumpPack(_config!, me)?.DashAttack is null || !me.Alive) return null;
        if (Rush.IsDash(me.Rush)) return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, _pitch);
        if (Evade.Evading(me) && !me.EvadeLanded)
        {
            bool press = Rush.CanDashAttack(me, _config!, _client!.Collision) && me.RushLock == 0 && !me.MeleeHeld;
            // feel/charge-directions: --bot-dash-dirs holds the stick in each of the eight directions in turn at the press (the lunge goes that way).
            var (sx, sy, keys) = press && BotDashDirs ? DashDirs[_botDashDir++ % DashDirs.Length] : (0f, 0f, "none");
            if (press) NotePress(_yaw, keys, me.Velocity.X * -MathF.Sin(_yaw) + me.Velocity.Y * MathF.Cos(_yaw));
            return new PlayerInput(tick, sx, sy, _yaw, press ? InputButtons.Melee : InputButtons.None, _pitch);
        }
        if (me.Jump == JumpPhase.TakeOff) return new PlayerInput(tick, 0f, 1f, _yaw, InputButtons.None, _pitch);   // released: the tap is the dash
        float dx = target.Position.X - me.Position.X, dy = target.Position.Y - me.Position.Y, distance = MathF.Sqrt(dx * dx + dy * dy);
        if (_connectedFor < _botDashNext || !me.Grounded || me.Jump != JumpPhase.None || Evade.Evading(me) || me.EvadeLock > 0 || me.RushLock > 0
            || me.MeleeStep > 0 || me.Throwing || me.JumpHeld || distance is < 5f or > 14f) return null;
        _botDashNext = _connectedFor + 3.0;
        if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} bot dashes at P{target.PlayerId} {distance:F1} m away to dash attack");
        return new PlayerInput(tick, 0f, 1f, _yaw, InputButtons.Jump, _pitch);
    }
}
