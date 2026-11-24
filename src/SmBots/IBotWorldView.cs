using SmContent;
using SmNet;
using SmSim;

namespace SmBots;

/// <summary>
/// What a bot brain reads of the world, and the view (yaw and pitch) it turns. The Godot client's adapter (client/NetworkView.BotBrain.cs) answers from its
/// <c>GameClient</c> and NetworkView state, as the brain read them before the move; a server (Warfront retainers, T4) answers from its World. The brain calls this
/// view under the name <c>_client</c>, so its moved code kept its lines (<see cref="ClassicBotBrain"/>).
/// </summary>
public interface IBotWorldView
{
    /// <summary>The bot's own player id (-1 before the server assigned one).</summary>
    int PlayerId { get; }
    /// <summary>The bot's own state now (the client: its predicted state).</summary>
    PlayerState Predicted { get; }
    /// <summary>Every other player as the bot sees it now (the client: the interpolated states it draws, which lag compensation rewinds to).</summary>
    IReadOnlyList<PlayerState> Others { get; }
    /// <summary>Seconds since the bot joined (the brain's clock: timers, cooldowns, logs).</summary>
    double ConnectedFor { get; }
    SimConfig? Config { get; }
    /// <summary>The map's collision (null on the flat ground without a map).</summary>
    CollisionWorld? Collision { get; }
    MatchView Match { get; }
    IReadOnlyList<ControlPointView> Points { get; }
    /// <summary>The Seize Ground layout (null in Annihilation and before it is known).</summary>
    SeizeGroundInfo? SeizeLayout { get; }
    /// <summary>Whether the match is Seize Ground.</summary>
    bool SeizeGround { get; }
    /// <summary>The map's name (<c>mp_*</c> for Classic PvP).</summary>
    string MapName { get; }
    /// <summary>How far behind the newest server state the others are shown (the client's interpolation delay; zero on a server).</summary>
    TimeSpan CurrentInterpolationDelay { get; }
    /// <summary>The newest projectiles in flight, not interpolated (detonated ones left out).</summary>
    IEnumerable<ProjectileView> NewestProjectiles();
    /// <summary>The projectiles as the bot sees them now (the client: interpolated like the others).</summary>
    IReadOnlyList<ProjectileView> Projectiles();
    /// <summary>The bot's view: yaw (radians; direction (-sin yaw, cos yaw)) and pitch, which the brain reads and sets.</summary>
    float Yaw { get; set; }
    float Pitch { get; set; }
    /// <summary>Seconds the bot has had grenades out (projectiles of its own in flight; 0 when none).</summary>
    float GrenadesOutFor { get; }
    /// <summary>The bot's own shots fired so far (the client: its hip-fire events), for the aim statistics.</summary>
    int OwnShotsFired { get; }
    /// <summary>The match's added content (none for a faithful match).</summary>
    ContentExtras MatchExtras { get; }
    /// <summary>The player's equipment when it is a stim, else null (unknown for another player: null).</summary>
    StimSpec? StimOf(PlayerState s);
    /// <summary>
    /// Seam (T1, ours): metres above <paramref name="target"/>'s feet the bot aims at. Classic: <see cref="ClassicBotBrain.BotTargetHeight"/> (the chest, 1.2 m) for
    /// everyone; a Warfront view gives an Ork's body its own chest height.
    /// </summary>
    float AimHeightOf(PlayerState target) => ClassicBotBrain.BotTargetHeight;
    /// <summary>A class's name for the log (the client: its HUD label).</summary>
    string ClassLabel(int playerClass) => $"class {playerClass}";
    /// <summary>The background work the brain's jobs run on (<see cref="BotWork"/>; one per process or bot host).</summary>
    BotWork Work { get; }
}
