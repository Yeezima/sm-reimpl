namespace SmBots;

/// <summary>
/// A Classic bot's settings that the Godot client took from its constructor and init properties (the rest of the test aids are command-line flags,
/// <see cref="BotArgs"/>). Defaults: an ordinary bot.
/// </summary>
public sealed record ClassicBotOptions
{
    /// <summary>The game install (<c>--bot-trace</c> refuses a file inside it); empty: no check.</summary>
    public string GameDir { get; init; } = "";
    /// <summary><c>--bot-melee</c>: runs at the target without firing, so melee is exercised every time.</summary>
    public bool Melee { get; init; }
    /// <summary><c>--bot-fire</c>: fires while no target is visible too (on flat ground only; on a map with <see cref="FireWalls"/>).</summary>
    public bool Fire { get; init; }
    /// <summary><c>--bot-fire-walls</c> (with <c>--bot-fire</c>): on a map also fires with no enemy in sight, and stays in its room when alone.</summary>
    public bool FireWalls { get; init; }
    /// <summary>Metres (horizontal) within which the bot throws: 20, or <c>--bot-throw-max</c>.</summary>
    public float ThrowMax { get; init; } = 20f;
    /// <summary><c>--bot-jump-free</c>: a jump-pack bot also jumps toward its target every 5 s over whatever is in the way.</summary>
    public bool JumpFree { get; init; }
    /// <summary><c>--bot-goal</c>: one point the bot walks to and holds (see the route test aid, ClassicBotBrain.Nav.cs).</summary>
    public string? Goal { get; init; }
    /// <summary><c>--bot-route</c>: points the bot walks through in order, then again from the first.</summary>
    public string? Route { get; init; }
    /// <summary>The client's lean bot (perf/lean-bots): the jump-link build's thread rule for a small machine (<see cref="SmNet.LeanBot.JumpBuildThreadsFor"/>).</summary>
    public bool Lean { get; init; }
    /// <summary>
    /// Plan paths inside the steering call instead of on the thread pool (<see cref="SmSim.NavAgent.Background"/> off): deterministic for the same inputs. The shadow
    /// comparison's <c>--bot-shadow-sync</c> (both brains); off for every ordinary bot.
    /// </summary>
    public bool SyncPlans { get; init; }
    /// <summary>
    /// Run the cover and approach searches inside the brain's tick instead of on the thread pool (Warfront retainers, review F1: with a graph their results landed at a wall-clock-dependent poll).
    /// Off for every Classic bot and the shadow comparison.
    /// </summary>
    public bool InlineSearches { get; init; }
    /// <summary>
    /// The key prefix of this brain's background jobs (<see cref="BotWork"/>): one per bot in a process; the shadow brain uses its live brain's, so the two share
    /// each job.
    /// </summary>
    public string WorkLane { get; init; } = "bot";
    /// <summary>
    /// The seed of the Classic PvP preset draw (<see cref="ClassicBotBrain.BotPresetOnMap"/>); null: <see cref="System.Random.Shared"/>, as the client always drew it
    /// (one draw per match from the process's shared generator).
    /// </summary>
    public int? PresetSeed { get; init; }
}
