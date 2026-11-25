using System.Numerics;

namespace SmSim.Extras.Executions;

/// <summary>What an execution's target is: a player (Classic PvP's corpse) or one of Warfront's AI enemies (an Ork, by its enemy id). The wire (<c>ExecutionWire</c>, tag 50) carries it as a byte.</summary>
public enum ExecutionTargetKind : byte { Player = 0, Enemy = 1 }

/// <summary>Why an execution ended (the client's clean-up and the executor's camera: a death goes to the death view). The wire carries it as a byte.</summary>
public enum ExecutionEndReason : byte { Done = 0, Killed = 1, Invalid = 2, Disconnect = 3, MatchEnd = 4 }

/// <summary>
/// One execution in progress (executions X3; design state/design-executions-1125.md "Simulation"). The server's state machine (<c>World.Executions.cs</c>) keeps one per executor; the client
/// rebuilds it from the wire's Start (<c>ExecutionWire</c>) for the drawn pose (X5) and the own prediction pin (X6). <see cref="ExecutionTrack"/> gives the executor's feet and yaw at any tick
/// from these fields alone, so the server pin, the prediction pin and the drawn pose cannot drift apart.
/// <list type="bullet">
/// <item><see cref="Executor"/>: the executing player's id. <see cref="TargetKind"/> and <see cref="Target"/>: the victim (a player id or an enemy id).</item>
/// <item><see cref="Wire"/>: the row's wire index (<see cref="ExecutionRowSet.WireIndexOf"/>, its place in <see cref="ExecutionRows.All"/>), never its place in a filtered list or in
/// <see cref="ExecutionRowSet.Rows"/>; the receiver resolves it with <see cref="ExecutionRowSet.AtWire"/> (null: no execution is drawn or predicted).</item>
/// <item><see cref="Distance"/>: the forward distance the attacker's blend plays at (metres; <see cref="ExecutionRow.Blend"/>, <see cref="ExecutionRow.AttackerRootAt"/>).</item>
/// <item><see cref="SlideFrom"/> to <see cref="Origin"/>: the slide (linear by tick over <see cref="SlideTicks"/>), then the clip from <see cref="Origin"/> at <see cref="Yaw"/> (the aligned attacker's
/// feet and yaw, <see cref="ExecutionAlignResult.Attacker"/>).</item>
/// <item><see cref="StartTick"/>: the tick the run was started (the state machine's step of that tick); the first pinned tick is <c>StartTick + 1</c> (the step runs after that tick's pin).</item>
/// <item><see cref="RaceSwapped"/>: Classic's Chaos Marine rows on a loyalist (<see cref="ExecutionAlignResult.RaceSwapped"/>). <see cref="KillDone"/>: the kill event has passed.</item>
/// <item>The victim at the start: <see cref="VictimAt"/> (feet, never moved), <see cref="VictimYaw"/> (the pair's), <see cref="VictimClass"/> (-1 for an enemy) and <see cref="VictimTeam"/>.</item>
/// </list>
/// The server quantizes <see cref="Distance"/>, <see cref="Yaw"/> and <see cref="SlideFrom"/> to the wire's precision when it starts a run (<see cref="ExecutionTrack.Quantize"/>), so the client's
/// run decoded from the Start is the server's, value for value.
/// </summary>
public readonly record struct ExecutionRun(int Executor, ExecutionTargetKind TargetKind, int Target, int Wire, float Distance, Vector3 SlideFrom, Vector3 Origin, float Yaw,
    int SlideTicks, uint StartTick, bool RaceSwapped)
{
    public bool KillDone { get; init; }
    public Vector3 VictimAt { get; init; }
    public float VictimYaw { get; init; }
    public int VictimClass { get; init; } = -1;
    public Team VictimTeam { get; init; }
}

/// <summary>
/// Classic PvP Extended (X3): the kill of <see cref="Victim"/> by <see cref="Killer"/> at <see cref="KillTick"/> that the killer may finish during the window
/// (<see cref="ExecutionRules.InWindow"/>). <see cref="Corpse"/>: the victim's state at death (its feet, yaw, class and team; <c>Alive</c> set for the alignment checks only).
/// </summary>
public readonly record struct ExecutionClassicOffer(int Killer, int Victim, uint KillTick, PlayerState Corpse);

/// <summary>An execution that ended at <see cref="Tick"/>, and why (X3; the server sends an End for each).</summary>
public readonly record struct ExecutionEnded(ExecutionRun Run, ExecutionEndReason Reason, uint Tick);

/// <summary>What a press chose (X3/X4): the target, the alignment result (its row, distance, slide and the aligned attacker) and the victim at the start.</summary>
public readonly record struct ExecutionPick(ExecutionTargetKind Kind, int Target, ExecutionAlignResult Result, Vector3 VictimAt, float VictimYaw, int VictimClass, Team VictimTeam);
