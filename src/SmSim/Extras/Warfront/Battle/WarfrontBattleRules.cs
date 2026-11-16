using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

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
    /// <summary>W7 seam: the Orks' health and damage multipliers of the node's level, read at each spawn (default 1, 1).</summary>
    public Func<(float Health, float Damage)> OrkLevelScale { get; set; } = () => (1f, 1f);
    /// <summary>W5 seam: the human who may deploy (default: <see cref="TfwRun.HostOf"/>).</summary>
    public Func<int>? Leader { get; set; }
    /// <summary>W5 seam: a node battle ended (lane, node, won); a wipe or a battle left is a loss.</summary>
    public Action<TfwLane, int, bool>? NodeResolved { get; set; }
    /// <summary>W5 seam: a node battle ended with its <see cref="TfwMapResult"/> (Won, Lost for a wipe, Left when every human went: W5 treats that as no result, back to the ship). Called with <see cref="NodeResolved"/>.</summary>
    public Action<TfwLane, int, TfwMapResult>? NodeEnded { get; set; }

    /// <summary>The lane and node of the battle under way (set at its creation).</summary>
    public TfwLane Lane { get; internal set; }
    public int Node { get; internal set; }
    /// <summary>Whether a node battle is under way.</summary>
    public bool InNode { get; internal set; }

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
    }
}
