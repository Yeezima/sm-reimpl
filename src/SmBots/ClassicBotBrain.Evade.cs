using System;
using SmSim;

namespace SmBots;

/// <summary>M21: the bot's evade roll (test script, not original behaviour; moved from the client's NetworkView.Evade.cs, whose roll pose stays there).</summary>
public sealed partial class ClassicBotBrain
{
    /// <summary>Bot test script: seconds between rolls (plus up to <see cref="BotEvadeJitter"/>); not original behaviour.</summary>
    const double BotEvadeEvery = 6.0, BotEvadeJitter = 2.0;

    double _botEvadeNext = 3.0;
    Random? _botRandom;

    /// <summary>
    /// The bot's roll (test script, only for classes with an evade): in a fight, grounded and free, a Jump press with the
    /// stick sideways (left or right at random) every <see cref="BotEvadeEvery"/> s or so; during a roll no stick (it would
    /// end the roll at the clip's run branch).
    /// </summary>
    PlayerInput? BotEvadeInput(PlayerState me, uint tick)
    {
        if (Loadouts.Evade(_config!, me) is null) return null;
        if (Evade.Evading(me)) return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, _pitch);
        if (_connectedFor < _botEvadeNext || !me.Grounded || me.EvadeLock > 0 || me.MeleeStep > 0 || me.Throwing || me.SwitchLeft > 0f || me.PendingShot > 0) return null;
        _botRandom ??= new Random(Math.Max(_client?.PlayerId ?? 0, 0) * 7919 + 1);
        _botEvadeNext = _connectedFor + BotEvadeEvery + _botRandom.NextDouble() * BotEvadeJitter;
        float side = _botRandom.Next(2) == 0 ? -1f : 1f;
        // Roll only onto walkable ground (navigation graph, ClassicBotBrain.Nav.cs): the other side, or no roll this time.
        if (!BotRollClear(me, side)) side = -side;
        if (!BotRollClear(me, side)) return null;
        return new PlayerInput(tick, side, 0f, _yaw, InputButtons.Jump, _pitch);
    }
}
