using System;
using System.Linq;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// The owner's posts (2026-11-08; test script, **ours**; engine-free parts in <see cref="BotPosts"/>): "make one boss play at the top of the side ramps, one devastator on
/// top of the balcony next to the capture points A and C. the rest play around the center of the map and the side lanes." Annihilation on a map with anchors
/// (Manufactorum): the side's lowest-id Devastator or Havoc holds the balcony on its half and its lowest-id Tactical (else another non-heavy) the ramp top on its half;
/// every player of the side counts, humans too, since a bot cannot tell them apart (a human Devastator with a lower id takes the bot's place). A post holder walks to its
/// post at once (no lane leg), holds it facing the enemy's side, fights what it sees from there (no evade roll, no advance; past 2.5 m from it, it steps back while firing),
/// does not chase or push, and goes back after a retreat.
/// The others keep the centre holds, lanes and roaming. <c>--bot-no-posts</c> turns it off.
/// </summary>
public partial class NetworkView
{
    static readonly bool BotNoPosts = CmdFlag("bot-no-posts");
    /// <summary>Metres from the post at which the holder counts as on it, and at which it walks back (ours).</summary>
    const float PostArrived = 1.5f, PostStray = 2.5f;

    BotPosts.Duty _postLogged = BotPosts.Duty.None;
    bool _postLoggedOnce;

    /// <summary>The bot's duty now (none in Seize Ground, without a plan, or on a map without posts).</summary>
    BotPosts.Duty BotPostDuty(PlayerState me)
    {
        if (BotNoPosts || !BotRecentAi || SeizeGround || _config is not { } c || me.Team == Team.None || Plan is not { Posts.Count: > 0 } plan) return BotPosts.Duty.None;
        var team = _lastOthers.Where(o => o.Team == me.Team && o.PlayerId > 0 && o.PlayerId != me.PlayerId).Select(o => (o.PlayerId, Loadouts.ClassOf(c, o.Class)?.Role ?? ""))
            .Append((me.PlayerId, Loadouts.ClassOf(c, me.Class)?.Role ?? ""));
        var duty = BotPosts.DutyOf(me.PlayerId, team);
        duty = plan.Posts.ContainsKey(duty) ? duty : BotPosts.Duty.None;
        if (duty != _postLogged || !_postLoggedOnce)
        {
            _postLogged = duty; _postLoggedOnce = true;
            GD.Print($"t {_connectedFor:F1} s: id {me.PlayerId} duty {duty}{(plan.Posts.TryGetValue(duty, out var p) ? $": {p.Name} {p.Spot.X:F0},{p.Spot.Y:F0},{p.Spot.Z:F1}" : "")}");
        }
        return duty;
    }

    /// <summary>The post the bot holds, or null.</summary>
    BotPost? BotPostOf(PlayerState me) => BotPostDuty(me) is var duty and not BotPosts.Duty.None && Plan is { } plan && plan.Posts.TryGetValue(duty, out var post) ? post : null;

    /// <summary>
    /// While fighting: the post to step back onto when the holder has strafed or fallen more than <see cref="PostStray"/> m from it (it fires as it goes); null on the
    /// post or without one (the holder then strafes there as usual, but does not roll or advance).
    /// </summary>
    System.Numerics.Vector3? BotPostReturn(PlayerState me) =>
        BotPostOf(me) is { } post && (new Vector2(post.Spot.X - me.Position.X, post.Spot.Y - me.Position.Y).Length() > PostStray || MathF.Abs(post.Spot.Z - me.Position.Z) > 2f) ? post.Spot : null;

    /// <summary>With nothing in sight (from <see cref="BotChaseInput"/>): walk to the post or hold it facing the enemy's side. Null without a post.</summary>
    PlayerInput? BotPostInput(PlayerState me, uint tick)
    {
        if (!me.Alive || BotPostOf(me) is not { } post) return null;
        float d = new Vector2(post.Spot.X - me.Position.X, post.Spot.Y - me.Position.Y).Length();
        if (d > PostArrived || MathF.Abs(post.Spot.Z - me.Position.Z) > 2f)
        {
            _botNote = $"to post: {post.Name} {d:F0} m";
            NoteIdle($"to post {post.Name} {post.Spot.X:F0},{post.Spot.Y:F0},{post.Spot.Z:F0}");
            return BotWalkTo(me, tick, post.Spot, "post");
        }
        _yaw = MathF.Atan2(-(post.Face.X - me.Position.X), post.Face.Y - me.Position.Y);
        _pitch = 0f;
        _botNote = $"holding post: {post.Name}";
        NoteIdle($"holding post {post.Name} {post.Spot.X:F0},{post.Spot.Y:F0},{post.Spot.Z:F0}");
        return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);
    }
}
