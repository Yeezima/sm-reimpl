using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The Three-Front War (Musou's war, owner 2026-11-01: "the three-front war mode is Musou mode"): the rules' version. Hashed into the config inside Musou's block
/// (<c>SimConfig.Extras.cs</c>), so a war-only change raises this and touches neither <see cref="MusouSim.Version"/> nor <see cref="ContentExtras.WireVersion"/>.
/// The war's own numbers (kind ours; design sections C to F) join this class with the milestones that use them.
/// </summary>
public static class TfwRules
{
    public const uint Version = 1;
}

/// <summary>The contract the war's milestones share (TF0, frozen): ids and how the war's actors are named across the simulation, the server and the client.</summary>
public static class TfwContracts
{
    /// <summary>The server-side AI Marines' player ids (real World players without a peer; design section E): 8 ids at the top of the 16-bit range.</summary>
    public const int PuppetIdFirst = 65520, PuppetIdLast = 65527;

    /// <summary>Whether <paramref name="id"/> is one of the AI Marines' ids. Means nothing outside a war (<see cref="World.Tfw"/> null): a human may hold such an id there.</summary>
    public static bool IsPuppet(int id) => id is >= PuppetIdFirst and <= PuppetIdLast;

    /// <summary>The <see cref="MusouTarget.Id"/> of Guard squad <paramref name="squad"/> (0-based): -2 and down, so it never meets a player's id or <see cref="FlowField"/>'s -1 for an unreached node.</summary>
    public static int GuardTargetId(int squad) => -(squad + 2);

    /// <summary>Whether a target or attack names a Guard squad (<see cref="MusouAttack.Player"/> below 0; the World routes it to <c>TfwGuardHit</c>).</summary>
    public static bool IsGuardTarget(int id) => id <= -2;

    /// <summary>The squad of a Guard target id (the inverse of <see cref="GuardTargetId"/>).</summary>
    public static int SquadOf(int targetId) => -targetId - 2;
}

/// <summary>The three fronts.</summary>
public enum TfwLane : byte { A = 0, B = 1, C = 2 }

/// <summary>Where the run stands (design sections C, D and F).</summary>
public enum TfwRunPhase : byte { Hub = 0, Travel = 1, Battle = 2, Pickup = 3, FinalAssault = 4, Won = 5, Lost = 6 }

/// <summary>A capture position in a battle (design section D): the chain goes Locked, Held, Clearing, Securing, Secured.</summary>
public enum TfwPositionState : byte { Locked = 0, Held = 1, Clearing = 2, Securing = 3, Secured = 4 }

/// <summary>A client's command (message kind 17, tag <c>TfwWire.CommandTag</c>; design section H). Deploy's argument is the lane.</summary>
public enum TfwCommand : byte { Deploy = 1, RequestPickup = 2, CancelPickup = 3, Follow = 4, Hold = 5, Attack = 6 }

/// <summary>One decoded command: its sequence (resent until tag 7 echoes it, idempotent by it), what it asks and its argument.</summary>
public readonly record struct TfwCommandRecord(ushort Sequence, TfwCommand Command, byte Argument);

/// <summary>One front as the simulation holds it and tag 7 carries it (design section C): front -1 (HQ besieged) to 3 (stronghold), progress -1 to 1, the strengths, each position's owner bit, the gather time left, the lord's state.</summary>
public readonly record struct TfwLaneState(sbyte Front, float Progress, float Imperial, float Ork, byte OwnedMask, float GatherLeft, byte LordState);

/// <summary>The war as tag 7 carries it: the phase, the battle's lane, the war clock (seconds), the time scale, the reserves, the newest command sequence applied per the sender (filled per client), and the three fronts.</summary>
public readonly record struct TfwWarView(TfwRunPhase Phase, TfwLane BattleLane, float Clock, float TimeScale, byte Transport, byte Supplies, ushort CommandEcho,
    TfwLaneState A, TfwLaneState B, TfwLaneState C);

/// <summary>One capture position as tag 8 carries it (positions quantised over the Musou box).</summary>
public readonly record struct TfwObjective(byte Index, TfwPositionState State, float Progress, Vector3 Position);

/// <summary>One Guardsman as tag 9 carries it (10-byte records, as the enemies').</summary>
public readonly record struct TfwGuard(ushort Id, Vector3 Position, float Yaw, byte State);

/// <summary>
/// The war of one match (<see cref="World.Tfw"/>; set only by <see cref="World.StartTfw"/>). TF0 holds the contract alone: the command queue, the AI Marines' input source and
/// their names. The war itself (TF2's WarSim, TF3's Guardsmen, TF4's battle and run, TF5's Marines) grows here and in <c>World.Tfw.cs</c>.
/// </summary>
public sealed partial class TfwRun
{
    /// <summary>Commands waiting for the next tick (a client's flood is cut by the server's rate, this by the queue's size).</summary>
    public const int MaxQueuedCommands = 64;

    readonly Queue<(int Player, TfwCommandRecord Command)> _commands = new();

    /// <summary>Queues a command that arrived between ticks; false when the queue is full.</summary>
    public bool Enqueue(int player, TfwCommandRecord command)
    {
        if (_commands.Count >= MaxQueuedCommands) return false;
        _commands.Enqueue((player, command));
        return true;
    }

    /// <summary>The oldest queued command, if any.</summary>
    public bool TryDequeue(out int player, out TfwCommandRecord command)
    {
        bool any = _commands.TryDequeue(out var next);
        (player, command) = next;
        return any;
    }

    /// <summary>Commands waiting.</summary>
    public int QueuedCommands => _commands.Count;

    /// <summary>The AI Marines' inputs (TF5's MarineBrain): the input for a puppet this tick; null: it stands still.</summary>
    public Func<int, PlayerState, PlayerInput>? PuppetBrain { get; set; }

    /// <summary>An AI Marine's scoreboard name ("" for any other id; TF5's roster, MarineBrain.cs).</summary>
    public string PuppetName(int id) => TfwContracts.IsPuppet(id) ? MarineName(id) : "";
}
