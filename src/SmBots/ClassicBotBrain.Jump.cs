using System;
using SmContent;
using SmNet;
using SmSim;

namespace SmBots;

/// <summary>
/// M14: the bot's jump-pack script (test aid, not original behaviour; moved from the client's NetworkView.Jump.cs, whose jump animation, rings and log stay there):
/// at a target 8 to 30 m away it jumps, steers at it and fires in the air, and pounds from above.
/// </summary>
public sealed partial class ClassicBotBrain
{
    // Bot jump script (test aid, not original behaviour).
    const float BotJumpMin = 8f, BotJumpMax = 30f;   // metres (horizontal) to the target in which the bot jumps at it
    const float BotPoundDistance = 6f;               // metres (horizontal) within which the bot starts a pound in the air
    const float BotPoundMinHeight = 2.5f;            // metres above the target's feet the bot must be to pound
    const double BotJumpEvery = 2.0;                 // seconds between the bot's jump presses at least
    const double BotFreeJumpEvery = 5.0;            // --bot-jump-free: seconds between free jumps at least
    const float BotFreeJumpHold = 0.75f;             // --bot-jump-free: hold (s) of a free jump (about 13 m on the install; M45: was 0.25 s before a release cut the rise)
    const float BotJumpClearance = 8f;               // metres free above the head the bot wants before jumping (on a map)

    uint _botJumpHoldUntil;
    bool _botJumpHolding;
    double _botLastJump = double.NegativeInfinity;

    /// <summary>
    /// On a map (the client's collision world): a clear line from the bot's chest to the target's and
    /// <see cref="BotJumpClearance"/> m free above its head, so the bot does not jump into a wall or a ceiling. Always true
    /// on the flat ground.
    /// </summary>
    bool BotJumpClear(PlayerState me, PlayerState target)
    {
        if (_client?.Collision is not { } world) return true;
        var from = me.Position + new System.Numerics.Vector3(0, 0, BotTargetHeight);
        var to = target.Position + new System.Numerics.Vector3(0, 0, BotTargetHeight);
        var line = to - from;
        float length = line.Length();
        if (length > 1e-3f && world.Character.Raycast(from, line / length, length) is not null) return false;
        return world.Character.Raycast(me.Position + new System.Numerics.Vector3(0, 0, 2f), System.Numerics.Vector3.UnitZ, BotJumpClearance) is null;
    }

    /// <summary>
    /// The bot's jump-pack script, or null for its other behaviour (test aid, not the original AI). On the ground with a
    /// target <see cref="BotJumpMin"/>–<see cref="BotJumpMax"/> m away (horizontally), enough power for a jump, nothing
    /// else under way and its last jump press <see cref="BotJumpEvery"/> s ago or more, it presses Jump with the stick
    /// forward (it faces the target) and holds it for a time scaled by the distance (0 at 8 m to the pack's longest hold
    /// at 30 m; the simulation stops the hold where the power runs out). During the take-off it keeps the stick forward;
    /// in the air it steers at the target while farther than half <see cref="BotPoundDistance"/>, fires the weapon in
    /// hand, and within <see cref="BotPoundDistance"/> m horizontally and at least <see cref="BotPoundMinHeight"/> m above
    /// the target presses melee (the ground pound). During the dive and the recovery it does nothing.
    /// </summary>
    PlayerInput? BotJumpInput(PlayerState me, PlayerState target, uint tick)
    {
        var c = _config!;
        if (Loadouts.JumpPack(c, me) is not { } pack) return null;
        float dx = target.Position.X - me.Position.X, dy = target.Position.Y - me.Position.Y;
        float horizontal = MathF.Sqrt(dx * dx + dy * dy), above = me.Position.Z - target.Position.Z;
        bool hold = _botJumpHolding && tick <= _botJumpHoldUntil;
        if (!hold) _botJumpHolding = false;
        var jump = hold ? InputButtons.Jump : InputButtons.None;
        switch (me.Jump)
        {
            case JumpPhase.TakeOff:
                _botPoundMiss = null;
                return new PlayerInput(tick, 0f, 1f, _yaw, jump, _pitch);
            case JumpPhase.Ascent or JumpPhase.Hang or JumpPhase.Fall:
            {
                float stick = horizontal > BotPoundDistance * 0.5f ? 1f : 0f;
                // bot-grenade-safety (Classic PvP): over its own live grenade the bot steers out of the blast, a target inside it is not flown at or pounded (ClassicBotBrain.BotGrenades.cs).
                if (BotAirStick(me, target) is { } away)
                {
                    var keep = BotWeaponButtons(me, tick, target) & ~(InputButtons.Melee | InputButtons.Switch);
                    if (pack.PoundEnabled && horizontal <= BotPoundDistance && above >= BotPoundMinHeight) BotPoundIntoOwnBlast(target);   // counted and logged
                    if (!BotOldTactics) { _botAimNow = true; _botAimTarget = target.PlayerId; BotNoteEngagement(horizontal); }
                    return new PlayerInput(tick, away.X, away.Y, _yaw, jump | keep, _pitch);
                }
                if (pack.PoundEnabled && horizontal <= BotPoundDistance && above >= BotPoundMinHeight && !me.MeleeHeld && !BotPoundIntoOwnBlast(target))
                {
                    // Stream C: some pounds are deliberately aimed beside the target (ClassicBotBrain.BotTactics.cs).
                    var (poundYaw, poundPitch) = BotPoundPress(me, target);
                    if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} bot presses pound {above:F1} m above P{target.PlayerId}, {horizontal:F1} m away horizontally{(_botPoundMiss is null ? "" : " (a miss)")}");
                    return new PlayerInput(tick, 0f, stick, poundYaw, jump | InputButtons.Melee, poundPitch);
                }
                var weapon = BotWeaponButtons(me, tick, target) & ~(InputButtons.Melee | InputButtons.Switch);
                // Stream C (owner: "when jumping in the air they ALWAYS hit bolt pistol headshots"): shots in the air get the aim error, doubled in BotApplyAim (BotAirAimScale).
                if (!BotOldTactics) { _botAimNow = true; _botAimTarget = target.PlayerId; BotNoteEngagement(horizontal); }
                return new PlayerInput(tick, 0f, stick, _yaw, jump | weapon, _pitch);
            }
            case JumpPhase.Dive or JumpPhase.Recover:
            {
                var (diveYaw, divePitch) = BotPoundAim();   // a missed pound keeps its aim through the dive
                return new PlayerInput(tick, 0f, 0f, diveYaw, InputButtons.None, divePitch);
            }
            case JumpPhase.None:
            {
                if (!me.Grounded || me.JumpHeld || me.JumpPower + 1e-3f < pack.CostAt(0f)
                    || me.MeleeStep > 0 || me.Throwing || me.SwitchLeft > 0f || me.Charging || me.PendingShot > 0 || _connectedFor - _botLastJump < BotJumpEvery)
                    return null;
                // 2026-11-08: during a melee push the jump onto the target waits until 16 m (the approach stays low with dashes and hops; ClassicBotBrain.BotPush.cs).
                float jumpMax = _botPush is { Active: true } push && push.Target == target.PlayerId && _approachSafe ? BotHopper.JumpInRange : BotJumpMax;
                bool atTarget = horizontal >= BotJumpMin && horizontal <= jumpMax && BotJumpClear(me, target) && BotFloorBetween(me, target.Position);
                // --bot-jump-free (test aid): otherwise a jump toward the target every BotFreeJumpEvery s (on a map, over
                // whatever is in the way; a ceiling simply stops the rise), with BotFreeJumpHold s of hold.
                bool free = !atTarget && _botJumpFree && _connectedFor - _botLastJump >= BotFreeJumpEvery;
                if (!atTarget && !free) return null;
                if (BotJumpIntoOwnBlast(target)) return null;   // bot-grenade-safety (Classic PvP): not onto a target inside its own live grenade's blast (ClassicBotBrain.BotGrenades.cs)
                // M45: a release while rising cuts the rise, so the hold scales over the whole ascent (the full jump: JumpPack.FullRiseTicks).
                float longest = pack.ReleaseCut ? JumpPack.FullRiseTicks(pack, c) * c.TickSeconds : pack.MaxHold;
                float wanted = atTarget ? Math.Clamp((horizontal - BotJumpMin) / (BotJumpMax - BotJumpMin), 0f, 1f) * longest : BotFreeJumpHold;
                // A press released before the dash decision tick is a horizontal dash with the jump-pack branch (protocol 24): the hold is at least NavJumpFlight.ShortestJumpHold ticks (M44a).
                int holdTicks = Math.Max((int)MathF.Round(wanted / c.TickSeconds), NavJumpFlight.ShortestJumpHold(c, me.Class));
                float holdSeconds = holdTicks * c.TickSeconds;   // the hold the simulation counts (held ticks after the press tick)
                _botJumpHoldUntil = tick + (uint)holdTicks; _botJumpHolding = true;
                _botLastJump = _connectedFor;
                if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} bot presses jump{(free ? " (free jump)" : "")} at P{target.PlayerId} {horizontal:F1} m away: hold {holdTicks} ticks ({holdSeconds:F2} s, {(pack.HoldTiers ? (holdSeconds + 1e-4f >= pack.MaxHold ? "the full jump" : "the small jump") : pack.ReleaseCut ? "height set by the release" : $"height {pack.HeightAt(holdSeconds):F1} m")}, cost {pack.CostAt(holdSeconds):F0}); power {me.JumpPower:F0}");
                return new PlayerInput(tick, 0f, 1f, _yaw, InputButtons.Jump, _pitch);
            }
            default:
                return null;   // Landed: the usual behaviour (the simulation holds the movement until the move lock)
        }
    }
}
