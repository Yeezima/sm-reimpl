using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmNet;
using SmSim;
using V2 = System.Numerics.Vector2;

namespace SmClient;

/// <summary>
/// Stream C (owner's batch 2026-11-06 late; Classic PvP test bots, **ours**, not the original AI; values in <see cref="BotTactics"/>, spec/behavior.md
/// "Bot tactics"):
/// <list type="bullet">
/// <item>Melee misses: <see cref="BotTactics.MeleeMissChance"/> of the swings (each press that starts an attack or a combo step), charges and ground pounds
/// are deliberately thrown off the target (a swing's view turned out of the lock and hit cones, a charge's start turned out of its cone, a pound aimed
/// <see cref="BotTactics.PoundMissOffset"/> m beside the target).</item>
/// <item>Shots in the air get the same aim error as on the ground (before, the jump-pack branch fired with the exact aim: "ALWAYS hit bolt pistol headshots").</item>
/// <item>Gunfights: the bot strafes left and right (legs of 0.7 to 2 s; it no longer stands 2 s of every 10), and when an ally already engages its enemy
/// it strafes around the enemy away from that ally until it is <see cref="BotTactics.FlankDegrees"/> around (a flank); a bot that cannot see the enemy yet
/// heads for a flank point instead of the enemy.</item>
/// <item>No straight runs at an enemy beyond <see cref="BotTactics.StraightRunRange"/> m: the advance while firing (beyond 40 m) and the melta's close-in run
/// weave <see cref="BotTactics.WeaveDegrees"/> degrees to alternating sides where the ground allows.</item>
/// </list>
/// <c>--bot-old-tactics</c> restores the old behaviour (for before/after measurements). Musou Mode's bot (the Extras hook) is unchanged.
/// A "tactics:" line every 10 s gives the measurements (shots and hits on the ground and in the air, per weapon, melee swings and hits, strafing share).
/// </summary>
public partial class NetworkView
{
    BotTactics? _botTacticsModel;
    bool? _botOldTactics;
    /// <summary><c>--bot-old-tactics</c>: the behaviour before stream C (no melee misses, exact aim in the air, the old strafe, straight advances).</summary>
    bool BotOldTactics => _botOldTactics ??= CmdFlag("bot-old-tactics");
    BotTactics Tactics => _botTacticsModel ??= new BotTactics(Math.Max(_client?.PlayerId ?? 0, 0) * 86028121 + 3);
    /// <summary>Whether the misses apply: not with the old tactics, a perfect aim (the probe flags) or <c>--bot-no-discipline</c> (test aids that need every hit).</summary>
    bool BotMisses => !BotOldTactics && BotAccuracy < BotAimModel.Perfect && !CmdFlag("bot-no-discipline");

    /// <summary>The current swing's view turn (radians, 0: aimed), kept while that attack runs.</summary>
    float _botSwingTurn;
    /// <summary>The current charge's view turn (radians), kept while the charge runs.</summary>
    float _botRushTurn;
    /// <summary>A missed pound's aim (yaw, pitch), kept through the dive; null when the pound is aimed.</summary>
    (float Yaw, float Pitch)? _botPoundMiss;

    // ---- melee misses ----

    /// <summary>
    /// The yaw of a melee input at an enemy <paramref name="horizontal"/> m away: a press that starts an attack draws whether it misses; the turn is kept
    /// while that attack runs and cleared when the bot is idle again.
    /// </summary>
    float BotSwingYaw(PlayerState me, bool press, float horizontal)
    {
        if (press) _botSwings++;
        if (!BotMisses) return _yaw;
        if (press)
        {
            bool miss = Tactics.RollMeleeMiss();
            if (miss) _botSwingMisses++;
            _botSwingTurn = miss ? Tactics.RollSide() * BotTactics.MissTurn(horizontal) * MathF.PI / 180f : 0f;
        }
        else if (me.MeleeStep == 0 && !me.MeleeQueued) _botSwingTurn = 0f;
        return _yaw + _botSwingTurn;
    }

    /// <summary>A charge press (<paramref name="pressing"/>) draws whether it misses (its start turned 60 degrees: out of the charge's 45 degree lock and contact cones); the turn is kept while it runs.</summary>
    float BotRushYaw(PlayerState me, bool pressing)
    {
        if (pressing) _botCharges++;
        if (!BotMisses) return _yaw;
        if (pressing)
        {
            bool miss = Tactics.RollMeleeMiss();
            if (miss) _botChargeMisses++;
            _botRushTurn = miss ? Tactics.RollSide() * BotChargeMissTurn * MathF.PI / 180f : 0f;
        }
        else if (me.Rush == RushPhase.None) _botRushTurn = 0f;
        return _yaw + _botRushTurn;
    }

    /// <summary>Degrees a missed charge's start is turned off the target (ours: past the charge's 45 degree lock and contact cones).</summary>
    const float BotChargeMissTurn = 60f;

    /// <summary>
    /// The aim of a pound pressed at <paramref name="target"/>: the target itself, or for a miss (drawn here) a point <see cref="BotTactics.PoundMissOffset"/> m
    /// beside it on the ground (the dive goes along the aim). Kept for the dive by <see cref="BotPoundAim"/>.
    /// </summary>
    (float Yaw, float Pitch) BotPoundPress(PlayerState me, PlayerState target)
    {
        _botPoundMiss = null;
        _botPounds++;
        if (!BotMisses) return (_yaw, _pitch);
        if (!Tactics.RollMeleeMiss()) return (_yaw, _pitch);
        _botPoundMisses++;
        var toTarget = new V2(target.Position.X - me.Position.X, target.Position.Y - me.Position.Y);
        var side = toTarget.LengthSquared() > 1e-4f ? V2.Normalize(new V2(toTarget.Y, -toTarget.X)) : new V2(1, 0);
        var point = new V2(target.Position.X, target.Position.Y) + side * (Tactics.RollSide() * BotTactics.PoundMissOffset);
        float dx = point.X - me.Position.X, dy = point.Y - me.Position.Y, dz = target.Position.Z - (me.Position.Z + _config!.EyeHeight);
        float yaw = MathF.Atan2(-dx, dy), pitch = Math.Clamp(MathF.Atan2(dz, MathF.Sqrt(dx * dx + dy * dy)), -MaxPitchDown, MaxPitchUp);
        _botPoundMiss = (yaw, pitch);
        return (yaw, pitch);
    }

    /// <summary>The aim during a dive: the missed pound's aim, else the current one.</summary>
    (float Yaw, float Pitch) BotPoundAim() => _botPoundMiss ?? (_yaw, _pitch);

    /// <summary>Metres from which a bot aims a primary weapon in a gunfight (ours: closer, and with a pistol, it fires from the hip and strafes at full speed).</summary>
    const float BotAimFrom = 10f;

    /// <summary>
    /// Aim mode (held) for a gunfight on the ground with a primary weapon at <see cref="BotAimFrom"/> m or more: the data's aiming spread sets are tighter than the
    /// hip's. Up close and with a pistol a person fires from the hip (a first measurement with aim mode everywhere: two jump-pack bots' pistol duel went from 53 to
    /// 74 % hits, both strafing at the slow aiming speed). None with <c>--bot-old-tactics</c>.
    /// </summary>
    InputButtons BotAimButton(PlayerState me, float horizontal) =>
        BotOldTactics || horizontal < BotAimFrom || _config is null || Loadouts.InHand(_config, me) is not { Slot: not WeaponSlot.Pistol } ? InputButtons.None : InputButtons.Aim;

    // ---- strafing, flanking, weaving ----

    /// <summary>
    /// The fight's strafe (-1 left, 1 right, 0 none) against <paramref name="target"/>: toward the flank while an ally engages it from elsewhere
    /// (<see cref="BotFlankSide"/>), else left and right in legs (<see cref="BotTactics.StrafeSide"/>); only onto walkable ground (the other side, or none).
    /// </summary>
    float BotStrafe(PlayerState me, PlayerState target)
    {
        int flank = BotFlankSide(me, target);
        int wanted = flank != 0 ? flank : Tactics.StrafeSide(_connectedFor);
        if (flank != 0) _botFlankTicks++;
        if (Nav is not { } nav) return wanted;
        var forward = new V2(-MathF.Sin(_yaw), MathF.Cos(_yaw));
        var right = new V2(forward.Y, -forward.X);
        if (nav.CanWalk(me.Position, right * (wanted * BotStrafeCheck))) return wanted;
        if (nav.CanWalk(me.Position, right * (-wanted * BotStrafeCheck))) { if (flank == 0) Tactics.FlipStrafe(_connectedFor); return -wanted; }
        return 0f;
    }

    /// <summary>The nearest living ally (not this bot) that engages <paramref name="enemy"/> (<see cref="BotTactics.Engages"/>), or null.</summary>
    PlayerState? BotEngagingAlly(PlayerState me, PlayerState enemy)
    {
        var at = new V2(enemy.Position.X, enemy.Position.Y);
        PlayerState? best = null;
        float bestDistance = float.MaxValue;
        foreach (var o in _lastOthers)
        {
            if (!o.Alive || o.PlayerId == enemy.PlayerId || me.Team == Team.None || o.Team != me.Team) continue;
            var p = new V2(o.Position.X, o.Position.Y);
            if (!BotTactics.Engages(p, o.Yaw, at)) continue;
            float d = V2.Distance(p, at);
            if (d < bestDistance) { bestDistance = d; best = o; }
        }
        return best;
    }

    /// <summary>The strafe side that flanks <paramref name="enemy"/> away from an engaging ally (<see cref="BotTactics.FlankSide"/>); 0 without one or once around.</summary>
    int BotFlankSide(PlayerState me, PlayerState enemy)
    {
        if (BotEngagingAlly(me, enemy) is not { } ally) return 0;
        return BotTactics.FlankSide(new V2(enemy.Position.X, enemy.Position.Y), new V2(me.Position.X, me.Position.Y), new V2(ally.Position.X, ally.Position.Y));
    }

    /// <summary>
    /// The chase goal for <paramref name="enemy"/>: a flank point (<see cref="BotTactics.FlankPoint"/>) when an ally engages it and the point lies on the
    /// navigation graph, else null (the enemy itself).
    /// </summary>
    System.Numerics.Vector3? BotFlankGoal(PlayerState me, PlayerState enemy)
    {
        if (BotOldTactics || _nav is not { } graph || BotEngagingAlly(me, enemy) is not { } ally) return null;
        var p = BotTactics.FlankPoint(new V2(enemy.Position.X, enemy.Position.Y), new V2(me.Position.X, me.Position.Y), new V2(ally.Position.X, ally.Position.Y));
        var goal = new System.Numerics.Vector3(p.X, p.Y, enemy.Position.Z);
        return graph.Nearest(goal, 3f, 3f) >= 0 ? goal : null;
    }

    /// <summary>
    /// <paramref name="to"/> (unit, ground plane) turned by the weave angle when the bot is more than <see cref="BotTactics.StraightRunRange"/> m from the enemy it
    /// closes on and the turned way is walkable for <see cref="BotStrafeCheck"/> m; else unchanged.
    /// </summary>
    Vector2 BotWeave(PlayerState me, Vector2 to, float enemyDistance)
    {
        if (BotOldTactics || enemyDistance <= BotTactics.StraightRunRange || to.LengthSquared() < 1e-6f) return to;
        var turned = to.Rotated(Mathf.DegToRad(Tactics.WeaveAngle(_connectedFor)));
        if (Nav is { } nav && !nav.CanWalk(me.Position, new V2(turned.X, turned.Y) * BotStrafeCheck)) return to;
        _botWeaveTicks++;
        return turned;
    }

    // ---- measurements ----

    int _botSwings, _botSwingMisses, _botMeleeHits, _botCharges, _botChargeMisses, _botPounds, _botPoundMisses;
    int _botFightTicks, _botStrafeTicks, _botFlankTicks, _botWeaveTicks, _botHeadOnTicks, _botCloseFightTicks;
    int _botAirShots, _botAirHits, _botGroundShots, _botGroundHits;
    readonly Dictionary<string, (int Shots, int Hits)> _botWeaponShots = new();
    string _botLastShotWeapon = "";
    bool _botLastShotAir;
    /// <summary>The enemy this tick's fight input is against (set by the fight code, read by <see cref="BotNoteMove"/>).</summary>
    PlayerState? _botTickEnemy;

    /// <summary>Per input tick of a fight (before the aim error): the strafing share and runs straight at the enemy (stick at least 0.7 within 20 degrees of it, beyond <see cref="BotTactics.StraightRunRange"/> m).</summary>
    void BotNoteMove(PlayerInput input)
    {
        if (_botTickEnemy is not { } enemy || _client is null) return;
        _botTickEnemy = null;
        var me = _client.Predicted;
        if (!me.Alive || me.Jump != JumpPhase.None) return;
        _botFightTicks++;
        if (MathF.Abs(input.MoveX) >= 0.3f) _botStrafeTicks++;
        var to = new Vector2(enemy.Position.X - me.Position.X, enemy.Position.Y - me.Position.Y);
        float d = to.Length();
        if (d <= BotTactics.StraightRunRange) { _botCloseFightTicks++; return; }
        var forward = new Vector2(-MathF.Sin(input.Yaw), MathF.Cos(input.Yaw));
        var right = new Vector2(forward.Y, -forward.X);
        var move = right * input.MoveX + forward * input.MoveY;
        if (move.Length() >= 0.7f && move.Normalized().Dot(to / d) >= MathF.Cos(Mathf.DegToRad(20f))) _botHeadOnTicks++;
    }

    /// <summary>A new own shot (counted by the aim log): in the air or on the ground, and with which weapon.</summary>
    void BotNoteShot(PlayerState me)
    {
        _botLastShotAir = !me.Grounded || me.Jump != JumpPhase.None;
        _botLastShotWeapon = _config is null ? "?" : WeaponLabel(Loadouts.InHand(_config, me));
        if (_botLastShotAir) _botAirShots++; else _botGroundShots++;
        var w = _botWeaponShots.GetValueOrDefault(_botLastShotWeapon);
        _botWeaponShots[_botLastShotWeapon] = (w.Shots + 1, w.Hits);
    }

    /// <summary>A hit report of the bot's own attacks: melee hits, and ranged hits by the last shot's place (air or ground) and weapon.</summary>
    void BotNoteTacticsHits(HitReport hits)
    {
        if (hits.Hits <= 0) return;
        if (hits.LastMelee) { _botMeleeHits++; return; }
        if (hits.LastSplash) return;
        if (_botLastShotAir) _botAirHits += hits.Hits; else _botGroundHits += hits.Hits;
        var w = _botWeaponShots.GetValueOrDefault(_botLastShotWeapon);
        _botWeaponShots[_botLastShotWeapon] = (w.Shots, w.Hits + hits.Hits);
    }

    static string Pct(int part, int whole) => whole > 0 ? $"{100f * part / whole:F1}%" : "-";

    /// <summary>The "tactics:" line (with the aim line, every 10 s).</summary>
    void LogBotTactics()
    {
        string weapons = string.Join("; ", _botWeaponShots.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value.Hits}/{k.Value.Shots}"));
        GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} tactics{(BotOldTactics ? " (old)" : "")}: ground {_botGroundHits}/{_botGroundShots} ({Pct(_botGroundHits, _botGroundShots)}), " +
                 $"air {_botAirHits}/{_botAirShots} ({Pct(_botAirHits, _botAirShots)}); weapons {weapons}; melee swings {_botSwings} (missed on purpose {_botSwingMisses}), melee hits {_botMeleeHits}, " +
                 $"charges {_botCharges} (missed {_botChargeMisses}), pounds {_botPounds} (missed {_botPoundMisses}); fight ticks {_botFightTicks}, strafing {_botStrafeTicks} ({Pct(_botStrafeTicks, _botFightTicks)}), " +
                 $"flanking {_botFlankTicks}, close {_botCloseFightTicks}, head-on runs {_botHeadOnTicks} ({Pct(_botHeadOnTicks, _botFightTicks - _botCloseFightTicks)}), weave {_botWeaveTicks}");
    }
}
