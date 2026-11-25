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
    /// The key prefix of this brain's own background searches (cover, approach; <see cref="BotWork"/>): null (the default) gives every brain a lane of its own; the
    /// client's shadow brain is given its live brain's, so the two share each search. Map jobs (graph, jump links, watch, plan) are keyed by the map, not the lane.
    /// </summary>
    public string? WorkLane { get; init; }
    /// <summary>
    /// The brain the host plays (true, the default): it alone writes <c>--bot-trace</c>. The client's shadow brain (<c>--bot-shadow</c>) is not primary.
    /// </summary>
    public bool Primary { get; init; } = true;
    /// <summary>
    /// The seed of the Classic PvP preset draw (<see cref="ClassicBotBrain.BotPresetOnMap"/>); null: <see cref="System.Random.Shared"/>, as the client always drew it
    /// (one draw per match from the process's shared generator).
    /// </summary>
    public int? PresetSeed { get; init; }
}
