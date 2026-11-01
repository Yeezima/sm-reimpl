using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// Musou Mode (the war; <c>Tfw</c> in code means Musou Mode, docs/added-content.md): the rules' version. Hashed into the config inside Musou's block
/// (<c>SimConfig.Extras.cs</c>), so a war-only change raises this and touches neither <see cref="MusouSim.Version"/> nor <see cref="ContentExtras.WireVersion"/>.
/// The war's own numbers (kind ours; design sections C to F) join this class with the milestones that use them.
/// </summary>
public static class TfwRules
{
    public const uint Version = 16;   // 2: TF6, the war's sections on the wire and the commands with a place; 3: TF4, the war runs (the battle, the run, the extraction); 4: TF7b, tag 7 carries the own block (TfwOwnView); 5: the own block's deployed flag, and an extraction after a won battle never ends the run; 6: pass 2's contracts (tags 11 to 14, the commands ChangeClass and ChooseReward, Knocked, the deaths Barrage and Strafe); 7: battles on Manufactorum (M1: the map's points, drawn sites, tag 8 carries each radius and the sites); 8: M7 and M11 (the class change in the Stormbird: the command ChangeClass and its smoke on tag 11; the elites' bars on tag 13; a retainer wears its owner's look; 9: the hub is the Thunderhawk's interior, map version 4); 10: taken by the rewards (M6); 11: allied navigation and line of sight (M2: the Guardsmen and AI Marines go by the walking graph, shots need a sight, squads go for the nearest position not secured, all three live from the landing) and the Orks (M3: garrisons, the tide from each point, retaking, elites by the run's wins, the crowd; the owner's changes: spawning stops only with a full capture, 200 alive, no Guardsmen for now); 12: taken by the rewards; 13: M5, the shove and the fall (Knocked), three big frags, the 20-enemy cleave, piercing shots; 14: rewards, slots and scores merged with 13; 15: fire support (M8: tag 11 carries the shells, the commands FireTest and Orbital, the own block's cooldown); 16: orbital strikes have no cooldown (every B press calls one; the own block's byte stays, always 0); 17: the spawn points' crowds (the Orks of a point pool there until 50, then swarm; no lone tide, no garrison rebuilding; a stuck Ork in a point being cleared is removed)
}

/// <summary>The contract the war's milestones share (TF0, frozen): ids and how the war's actors are named across the simulation, the server and the client.</summary>
public static class TfwContracts
{
    /// <summary>The server-side AI Marines' player ids (real World players without a peer; design section E): 8 ids at the top of the 16-bit range.</summary>
    public const int PuppetIdFirst = 65520, PuppetIdLast = 65527;

    /// <summary>Whether <paramref name="id"/> is one of the AI Marines' ids. Means nothing outside a war (<see cref="World.Tfw"/> null): a human may hold such an id there.</summary>
    public static bool IsPuppet(int id) => id is >= PuppetIdFirst and <= PuppetIdLast;

    /// <summary>The <see cref="MusouTarget.Id"/> of a secured position as the place the Orks go to retake (<see cref="MusouTarget.Goal"/>): over the AI Marines' ids, under 16 bits.</summary>
    public static int PointTargetId(int position) => PuppetIdLast + 3 + position;

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

/// <summary>A client's command (message kind 17, tag <c>TfwWire.CommandTag</c>; design section H). Deploy's argument is the lane; Hold and Attack carry a place (<see cref="TfwCommandRecord.Point"/>); ChooseReward's argument is the card (0 to <c>TfwWire.RewardCards</c> - 1) and <see cref="TfwCommandRecord.Offer"/> the offer it answers; SelectSlot's argument is the weapon key (1 to 5).</summary>
public enum TfwCommand : byte { Deploy = 1, RequestPickup = 2, CancelPickup = 3, Follow = 4, Hold = 5, Attack = 6, ChangeClass = 7, ChooseReward = 8, SelectSlot = 9, Orbital = 10, FireTest = 11 }

/// <summary>One decoded command: its sequence (resent until tag 7 echoes it, idempotent by it), what it asks, its argument and, for Hold and Attack, the point of the sender's crosshair (metres; untrusted, within about 3 km on each axis). <see cref="Offer"/>: ChooseReward's offer sequence, so a delayed pick never lands on a newer offer.</summary>
public readonly record struct TfwCommandRecord(ushort Sequence, TfwCommand Command, byte Argument, Vector3 Point = default, ushort Offer = 0);

/// <summary>One front as the simulation holds it and tag 7 carries it (design section C): front -1 (HQ besieged) to 3 (stronghold), progress -1 to 1, the strengths, each position's owner bit, the gather time left, the lord's state.</summary>
public readonly record struct TfwLaneState(sbyte Front, float Progress, float Imperial, float Ork, byte OwnedMask, float GatherLeft, byte LordState);

/// <summary>The war as tag 7 carries it: the phase, the battle's lane, the war clock (seconds), the time scale, the reserves, the newest command sequence applied per the sender (filled per client), the three fronts, and what only this client's HUD needs (<see cref="TfwOwnView"/>, per client as the echo).</summary>
public readonly record struct TfwWarView(TfwRunPhase Phase, TfwLane BattleLane, float Clock, float TimeScale, byte Transport, byte Supplies, ushort CommandEcho,
    TfwLaneState A, TfwLaneState B, TfwLaneState C, TfwOwnView Own = default);

/// <summary>A player's retainer as the HUD shows it (TF7b): none yet (in the Stormbird), up, reviving its player, or down and recovering.</summary>
public enum TfwRetainerState : byte { None = 0, Ready = 1, Reviving = 2, Recovering = 3 }

/// <summary>
/// What one client's HUD needs of the run beyond the fronts (TF7b, the end of tag 7): the seconds left, rounded up (so a countdown changes the record once a second) of the travel, the pickup, the
/// own revive window and the extraction's prompt; the revive's progress in tenths; whether the battle is won (a pickup is then free); the own retainer, its order and its recovery (war seconds, rounded
/// up); the battle's Guardsmen alive, squads in the field, squads in reserve and positions secured; and whether this player is in the run's field (<see cref="TfwRun.InField"/>: one who joined after the
/// landing is not, so the run does not hold its respawn); and a spare byte, always 0 since orbital strikes lost their cooldown (the wire keeps it).
/// </summary>
public readonly record struct TfwOwnView(byte TravelSeconds, byte PickupSeconds, byte ReviveSeconds, byte ExtractionSeconds, byte ReviveTenths, bool BattleWon,
    TfwRetainerState Retainer, MarineOrder Order, ushort RecoverySeconds, byte Guards, byte Squads, byte Reserves, byte Secured, bool Deployed = false, byte OrbitalSeconds = 0);

/// <summary>One capture position as tag 8 carries it (positions quantised over the Musou box).</summary>
public readonly record struct TfwObjective(byte Index, TfwPositionState State, float Progress, Vector3 Position, float Radius = 0f);

/// <summary>One Guardsman as tag 9 carries it (10-byte records, as the enemies').</summary>
public readonly record struct TfwGuard(ushort Id, Vector3 Position, float Yaw, byte State);

/// <summary>What a fire record reports (tag 11): a shell's landing, a Thunderhawk run, one round of its guns, the smoke of a class change, a frag's big blast.</summary>
public enum TfwFireKind : byte { Shell = 1, Run = 2, Round = 3, Smoke = 4, Frag = 5 }

/// <summary>
/// One record of tag 11; a client plays each (<see cref="Kind"/>, <see cref="Tick"/>, <see cref="Subject"/>) once. Shell: <see cref="Tick"/> the landing tick, <see cref="Subject"/> its number in the barrage,
/// <see cref="At"/> the impact. Run: the first tick, the run's number, the path from <see cref="At"/> to <see cref="To"/>. Round: the firing tick, a running count, muzzle to impact. Smoke: the first tick, the player,
/// <see cref="At"/> his feet. Frag: the tick it went off, the projectile, <see cref="At"/> the blast, <see cref="To"/> X the thrown item and Y the blast's radius.
/// </summary>
public readonly record struct TfwFireEvent(TfwFireKind Kind, uint Tick, ushort Subject, Vector3 At, Vector3 To = default);

/// <summary>One elite's health as tag 13 carries it: the enemy's id, 0 to 255 of its full health.</summary>
public readonly record struct TfwBar(ushort Enemy, byte Health);

/// <summary>How a map ended for the score (tag 12's history).</summary>
public enum TfwMapResult : byte { Won = 1, Left = 2, Lost = 3, Extracted = 4 }

/// <summary>This client's own score (tag 12): the map's points, the run's total, the map's KOs and its best chain.</summary>
public readonly record struct TfwScoreOwn(uint MapPoints, uint RunTotal, ushort Kos, ushort BestChain);

/// <summary>Another human's run total (tag 12).</summary>
public readonly record struct TfwScorePlayer(ushort Player, uint RunTotal);

/// <summary>One finished map of the run (tag 12's history).</summary>
public readonly record struct TfwMapScore(TfwLane Lane, byte Node, TfwMapResult Result, ushort Kos, ushort BestChain, uint Points);

/// <summary>One tag 12 section: the parts it carries (null: not in this section; a client keeps what it has). Each part has its own rate.</summary>
public readonly record struct TfwScoreUpdate(TfwScoreOwn? Own, IReadOnlyList<TfwScorePlayer>? Others = null, IReadOnlyList<TfwMapScore>? History = null);

/// <summary>What a reward card gives (tag 14); None: a card left empty when the class has too few choices.</summary>
public enum TfwRewardKind : byte { None = 0, Weapon = 1, Equipment = 2, Perk = 3 }

/// <summary>One reward card: its kind and the item (a <see cref="MusouDropItem"/>, an equipment or a perk index, by kind).</summary>
public readonly record struct TfwRewardCard(TfwRewardKind Kind, ushort Item);

/// <summary>The cards offered to one client after a win (tag 14); the newest <see cref="Sequence"/> stands.</summary>
public readonly record struct TfwRewardOffer(ushort Sequence, TfwRewardCard First, TfwRewardCard Second, TfwRewardCard Third);

/// <summary>
/// One client's extra primary slots (tag 15; owner's item 26): the weapon index in keys 3, 4 and 5 (-1: empty), and which key holds the weapon now in the player's primary and pistol fields
/// (0: its own, never an extra). The HUD draws the slots; the client needs the two so that keys 2 and 1 bring its own weapon back.
/// </summary>
public readonly record struct TfwArsenalView(short Slot3, short Slot4, short Slot5, byte LoadedPrimary, byte LoadedPistol)
{
    /// <summary>The empty slots.</summary>
    public static readonly TfwArsenalView Empty = new(-1, -1, -1, 0, 0);
    /// <summary>The weapon in key <paramref name="key"/> (3 to 5), or -1.</summary>
    public int Weapon(int key) => key switch { 3 => Slot3, 4 => Slot4, 5 => Slot5, _ => -1 };
}

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
