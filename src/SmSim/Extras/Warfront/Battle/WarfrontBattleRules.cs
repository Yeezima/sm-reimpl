using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Campaign;

namespace SmSim.Extras.Warfront;

/// <summary>Where a fallen human of a Warfront node battle stands (<see cref="TfwRun.WarfrontDownState"/>).</summary>
public enum WarfrontDownPhase : byte { None = 0, Downed = 1, Spectator = 2 }

/// <summary>A fallen human's state and the seconds left of it (the revive window while Downed, the wait for the respawn while Spectator).</summary>
public readonly record struct WarfrontDown(WarfrontDownPhase Phase, float SecondsLeft);

/// <summary>
/// Warfront's battle rules (slice W3; spec "Added content: Warfront"): the numbers of a node battle and the seams the later slices plug into. One object per run, attached by the <c>WarfrontStarted</c> hook
/// only under <see cref="ContentExtras.Warfront"/> (null in every other run: then no Warfront hook changes anything). Every value is the owner's or ours (the spec rows say which).
/// </summary>
public sealed class WarfrontBattleRules
{
    public WarfrontBattleRules()
    {
        Progression = new WarfrontProgressionRuntime();
        OrkLevelScale = () => Progression.OrkLevelScale();   // the level part from the highest connected player's level (slice W3b); a test or W5 may replace it
    }

    /// <summary>The owner: "+20 % per human" on the Orks' health, from the second human on (1 + 0.20 x (humans - 1); four humans: x1.6).</summary>
    public const float HealthPerExtraHuman = 0.20f;
    /// <summary>The spectator time of a fallen human not revived in the window (owner: "respawn after 1 minute"), counted from the window's end (our reading; decided default).</summary>
    public const float SpectatorSeconds = 60f;
    /// <summary>The revive window (Musou's, kept): it always opens in Warfront, whoever stands.</summary>
    public const float ReviveWindowSeconds = TfwBattleRules.ReviveWindowSeconds;
    /// <summary>Boss Nobs in D's garrison, beside the commander's escort (ours, an estimate).</summary>
    public const int GarrisonBossNobs = 1;

    /// <summary>The Orks' health scale of <paramref name="humans"/> humans.</summary>
    public static float PlayerScale(int humans) => 1f + HealthPerExtraHuman * Math.Max(0, humans - 1);

    /// <summary>W9 seam: whether any retainer of any squad stands (default: none do; Warfront lands no puppet retainer).</summary>
    public Func<bool> AnyRetainerStanding { get; set; } = () => false;
    /// <summary>W9 seam: an order (Follow, Hold, Attack) as it comes in (default: dropped).</summary>
    public Action<int, TfwCommandRecord>? OrderSink { get; set; }
    /// <summary>The Orks' health and damage multipliers of the node's level, read at each spawn: the level part only (<see cref="WarfrontOrkScaling.LevelScale"/> of the highest connected player's level; 1, 1 at level 1). The humans' factor is <see cref="PlayerScale"/>.</summary>
    public Func<(float Health, float Damage)> OrkLevelScale { get; set; }
    /// <summary>W5 seam: the human who may deploy (default: <see cref="TfwRun.HostOf"/>).</summary>
    public Func<int>? Leader { get; set; }
    /// <summary>W5 seam: a node battle ended (lane, node, won); a wipe or a battle left is a loss.</summary>
    public Action<TfwLane, int, bool>? NodeResolved { get; set; }
    /// <summary>W5 seam: a node battle ended with its <see cref="TfwMapResult"/> (Won, Lost for a wipe, Left when every human went: W5 treats that as no result, back to the ship). Called with <see cref="NodeResolved"/>.</summary>
    public Action<TfwLane, int, TfwMapResult>? NodeEnded { get; set; }

    /// <summary>
    /// W7's progression in the run (slice W3b): XP and levels, the picker, the cards' bonuses. Until W5 supplies the real campaign it holds its own, with the player id as the key
    /// (<see cref="WarfrontProgressionRuntime.KeyOf"/>); W5 replaces it with one built on the run's campaign and keys.
    /// </summary>
    public WarfrontProgressionRuntime Progression { get; set; }

    /// <summary>The Warfront rule for the squad sim's ids (W9): the id range of the squad Marines as the Orks' targets and attackers' victims, -1000 to -1035 (clear of the Guardsmen's -2 and below).</summary>
    public const int SquadIdFirst = -1000, SquadIdLast = -1035, MaxSquadTargets = 36;

    /// <summary>W9 seam: writes the standing squad Marines into the Orks' target list (at most <see cref="MaxSquadTargets"/>; the hook makes room) and returns how many (default: none).</summary>
    public SquadTargetSource? SquadTargets { get; set; }
    /// <summary>W9 seam: an Ork's attack on a squad Marine: its id (-1000 to -1035), the damage (the Ork's remembered multiplier already applied) and where it came from (default: dropped; the Guardsmen never take it).</summary>
    public Action<int, float, Vector3>? SquadHit { get; set; }
    /// <summary>The tick the squad seam last ran from <c>WarfrontFought</c> (the World steps it from <c>WarfrontStep</c> when a tick had no fight).</summary>
    internal uint LastFought;
    /// <summary>W9b seam (notifying): one tick aboard the ship (the hub or the way down); the squad wiring follows its owners' loadouts there. Subscribe with <c>+=</c>.</summary>
    public Action? AboardStep { get; set; }
    /// <summary>W9b seam (notifying): the campaign key of a player who levelled up (called before the refill's own checks); the squad refills his standing Marines. Subscribe with <c>+=</c>.</summary>
    public Action<ulong>? OwnerLevelUp { get; set; }
    /// <summary>W9 seam: one squad tick, after the Orks moved and struck (default: none).</summary>
    public Action? SquadStep { get; set; }
    /// <summary>W9 seam: a node battle began (lane, node) / ended (its result); called from W3's creation and leave hooks (default: none).</summary>
    public Action<TfwLane, int>? BattleBegan { get; set; }
    public Action<TfwMapResult>? BattleEnded { get; set; }
    /// <summary>W9 seam: a position's capture was credited (its index 0 to 2 for A, B, C; 3 for D), once per position per battle (default: none).</summary>
    public Action<int>? ObjectiveSecured { get; set; }
    /// <summary>W12 seam: whether player <c>id</c> is held by the co-op rules (a first-time joiner spectating or in the customizer): his input is locked and he takes no damage while it returns true (default: none held).</summary>
    public Func<int, bool>? CoopHeld { get; set; }
    /// <summary>W12 seam: whether a late human is landed in the battle now (false: it waits in the Stormbird; default: allowed).</summary>
    public Func<int, bool>? LateJoinAllowed { get; set; }
    /// <summary>W2's stuck-unit log seam: a failsafe removed or relocated an Ork of a node battle (default: none; W5 connects <c>WarfrontStuckLog</c>).</summary>
    public Action<WarfrontStuckReport>? StuckSink { get; set; }

    /// <summary>W14b: the fortress deployment's setup (the arena's plan); null for every other battle. Set by the server before the landing; the plan hook then supplies the fortress layout and the final assault.</summary>
    public SmSim.Extras.Warfront.Fortress.WarfrontFortressSetup? Fortress { get; set; }

    /// <summary>The lane and node of the battle under way (set at its creation).</summary>
    public TfwLane Lane { get; internal set; }
    public int Node { get; internal set; }
    /// <summary>Whether a node battle is under way.</summary>
    public bool InNode { get; internal set; }
    /// <summary>Whether any Warfront battle is under way (a node, the HQ defence, the fortress).</summary>
    public bool InBattle => InNode;
    /// <summary>What the next battle is (W13b): set by the server before the landing; a Node battle until it says otherwise. The run's battle-plan hook reads it.</summary>
    public WarfrontTargetKind Target { get; set; } = WarfrontTargetKind.Node;

    /// <summary>The Orks' health scale of the battle under way (1 outside one).</summary>
    internal float PlayerScaleNow = 1f;
    internal float PendingDamage = 1f;
    internal readonly float[] DamageScale = CreateScales();

    /// <summary>The damage multiplier the Ork in slot <paramref name="id"/> took at its spawn (1 for an unknown id).</summary>
    public float DamageScaleOf(ushort id) => id < DamageScale.Length ? DamageScale[id] : 1f;

    static float[] CreateScales()
    {
        var scales = new float[MusouSim.Capacity + 1];
        Array.Fill(scales, 1f);
        return scales;
    }

    /// <summary>Resets what a battle sets.</summary>
    internal void EndBattle()
    {
        InNode = false;
        PlayerScaleNow = 1f;
        Progression.EndBattle();
    }
}

/// <summary>Writes the standing squad Marines into <paramref name="into"/> (W9) and returns how many it wrote.</summary>
public delegate int SquadTargetSource(Span<MusouTarget> into);

/// <summary>Which failsafe acted on a stuck Ork: <see cref="Cleared"/> removed it from a position being taken, <see cref="Relocated"/> and <see cref="Removed"/> are the stranded failsafe's two answers.</summary>
public enum WarfrontStuckWhy : byte { Cleared = 0, Relocated = 1, Removed = 2 }

/// <summary>One stuck-unit record of a node battle: where (lane, node), which Ork (pool id, type), where it stood and what was done.</summary>
public readonly record struct WarfrontStuckReport(TfwLane Lane, int Node, ushort Id, MusouEnemyType Type, Vector3 At, WarfrontStuckWhy Why);
