using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmNet;
using SmSim;
using V3 = System.Numerics.Vector3;

namespace SmClient;

/// <summary>
/// The owner's low-health rule (2026-11-08; test script, **ours**, not the original AI; engine-free parts in <see cref="BotRetreat"/> and <see cref="BotCover"/>): "they
/// should prioritize taking cover when their hp gets low to regenerate, then rejoin the fight." At 60 % health a Classic PvP bot walks (sprinting while unseen, else
/// firing back as it goes) to the nearest cell the nearby enemies cannot see, planned off the frame loop and planned again when an enemy gets sight of it; there it waits
/// for the sim's regeneration (firing only at an enemy that shows itself) and rejoins at 90 % health and armour, or after 35 s. An enemy within melee reach is fought
/// instead. Musou bots return earlier (<c>ExtrasBotInput</c>). <c>--bot-no-retreat</c> turns it off.
/// </summary>
public partial class NetworkView
{
    static readonly bool BotNoRetreat = CmdFlag("bot-no-retreat");
    /// <summary>Metres (flat) within which an enemy counts as a threat to hide from, and how close to a cover cell counts as in it (ours).</summary>
    const float RetreatThreatRange = 150f, CoverArrived = 1.5f;
    /// <summary>Seconds between the checks that the cover is still hidden (ours).</summary>
    const double CoverCheckEvery = 1.0;

    BotRetreat? _botRetreat;
    Task<V3?>? _coverTask;
    V3? _cover;
    bool _coverNone;
    double _coverAt = double.NegativeInfinity, _retreatSince;
    int _coverLogged;

    /// <summary>Whether the bot is retreating to cover now.</summary>
    bool BotRetreating => _botRetreat is { Active: true };

    /// <summary>The enemies near enough to hide from: alive, outside their spawn room, within <see cref="RetreatThreatRange"/>, nearest first (at most four).</summary>
    List<PlayerState> RetreatThreats(PlayerState me) => _lastOthers
        .Where(o => o.Alive && World.Hostile(me.Team, o.Team) && !InEnemyRoom(me, o)
                    && new Vector2(o.Position.X - me.Position.X, o.Position.Y - me.Position.Y).Length() <= RetreatThreatRange)
        .OrderBy(o => V3.DistanceSquared(o.Position, me.Position)).Take(4).ToList();

    /// <summary>
    /// Classic PvP, per bot tick after Musou's own bot: starts and ends the retreat (<see cref="BotRetreat.Step"/>) and, while it lasts, the walk to cover and the wait
    /// there. Null when not retreating, or when an enemy is within melee reach (the fight goes on).
    /// </summary>
    PlayerInput? BotRetreatInput(PlayerState me, uint tick)
    {
        if (BotNoRetreat || _config is null || _client?.Collision is not { } world || me.Team == Team.None || !me.Alive) return null;
        _botRetreat ??= new BotRetreat();
        var body = Loadouts.Body(_config, me);
        bool was = _botRetreat.Active;
        bool now = _botRetreat.Step(_connectedFor, me.Health / MathF.Max(body.HealthMax, 1f), me.Shield / MathF.Max(body.ShieldMax, 1f));
        if (now && !was)
        {
            _retreatSince = _connectedFor; _cover = null; _coverNone = false; _coverAt = double.NegativeInfinity;
            GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} retreat {_botRetreat.Retreats} to cover at health {me.Health:F0}/{body.HealthMax:F0}, armour {me.Shield:F0}");
        }
        else if (was && !now)
        {
            GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} rejoins after {_connectedFor - _retreatSince:F1} s (health {me.Health:F0}, armour {me.Shield:F0})");
            _cover = null;
        }
        if (!now || _nav is not { } graph) return null;

        var threats = RetreatThreats(me);
        var eyes = threats.Select(o => o.Position + new V3(0, 0, _config.EyeHeight)).ToList();
        var target = BotFightTarget(me, out float distance);
        if (target is not null && distance <= BotMeleeDistance + 1.5f) return null;   // in melee reach: no turning away

        if (_coverTask is { IsCompleted: true } task)
        {
            _coverTask = null;
            if (task.IsCompletedSuccessfully)
            {
                _cover = task.Result; _coverNone = _cover is null;
                if (_cover is { } far && new Vector2(far.X - me.Position.X, far.Y - me.Position.Y).Length() > BotCover.MaxDistance + 10f) { _cover = null; _coverNone = false; }   // planned from where the bot no longer is
                if (_coverLogged++ < 60)
                    GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} cover {(_cover is { } c ? $"at {c.X:F0},{c.Y:F0},{c.Z:F0}, {new Vector2(c.X - me.Position.X, c.Y - me.Position.Y).Length():F0} m" : "none in reach")} from {threats.Count} threat(s)");
            }
        }
        bool stale = _cover is null && !_coverNone
            || _connectedFor - _coverAt > CoverCheckEvery && (_coverNone || _cover is { } held && !BotCover.Hidden(world, eyes, held));
        if (stale && _coverTask is null)
        {
            _coverAt = _connectedFor;
            var from = me.Position; var cancel = _navCancel.Token; var plan = Plan;
            Func<V3, bool>? excluded = plan is null ? null : plan.InNoLoiterZone;
            _coverTask = Task.Run(() => BotCover.Find(graph, world, from, eyes, excluded, cancel), cancel);
        }
        if (_cover is not { } cover) return target is null ? new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f) : null;   // planning (standing), or none: fight

        float toCover = new Vector2(cover.X - me.Position.X, cover.Y - me.Position.Y).Length();
        if (target is { } enemy)
        {
            // Seen: fire back while going (no sprint: it would stop the fire), or standing in a cover that is no longer hidden until the next one is planned.
            float dx = enemy.Position.X - me.Position.X, dy = enemy.Position.Y - me.Position.Y;
            float dz = enemy.Position.Z + BotTargetHeight - (me.Position.Z + _config.EyeHeight);
            _yaw = MathF.Atan2(-dx, dy);
            _pitch = Math.Clamp(MathF.Atan2(dz, MathF.Sqrt(dx * dx + dy * dy)), -MaxPitchDown, MaxPitchUp);
            var weapon = BotWeaponButtons(me, tick, enemy);
            _botAimNow = true; _botAimTarget = enemy.PlayerId;
            _botNote = $"retreating, firing at P{enemy.PlayerId} ({toCover:F0} m to cover)";
            if (toCover <= CoverArrived) return new PlayerInput(tick, 0f, 0f, _yaw, weapon, _pitch);
            var (to, speed, _, _) = BotSteer(me, cover, "cover");
            return BotMoveInput(tick, to * speed, weapon | _navJump);
        }
        if (toCover <= CoverArrived)
        {
            if (threats.Count > 0)
            {
                var face = threats[0].Position;
                _yaw = MathF.Atan2(-(face.X - me.Position.X), face.Y - me.Position.Y);
            }
            _pitch = 0f;
            _botNote = $"in cover, regenerating (health {me.Health:F0})";
            return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);
        }
        _botNote = $"retreating to cover {toCover:F0} m";
        return BotWalkTo(me, tick, cover, "cover");
    }
}
