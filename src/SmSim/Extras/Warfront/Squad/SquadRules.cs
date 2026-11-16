using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Warfront.Squad;

/// <summary>
/// The numbers of Warfront's lightweight squad (slice W9; spec/behavior.md "Added content: Warfront: squads"). Counts, the 20 s downed window and "out for that battle only" are the owner's
/// (2026-11-17); every other value is ours or an estimate, tagged in the spec and so in the refine register. Distances in metres, times in seconds. Movement reuses
/// <see cref="MarineRules"/> follow/hold/attack bands (the puppet retainers' numbers) so the orders read the same as in Musou Mode.
/// </summary>
public static class SquadRules
{
    /// <summary>Retainers per player at most, players with a roster at most, and so the whole squad system's most [owner].</summary>
    public const int MaxPerPlayer = 9, MaxOwners = 4, MaxTotal = MaxPerPlayer * MaxOwners;
    /// <summary>A new roster starts with this many retainers [owner].</summary>
    public const int StartRetainers = 1;
    /// <summary>A downed Marine can be revived this long; then it is out for the battle (and back in the next) [owner].</summary>
    public const float DownedSeconds = 20f;
    /// <summary>A player holding Use within <see cref="PlayerReviveReach"/> of a downed Marine revives it after <see cref="PlayerReviveSeconds"/> [ours/estimate, plan section 6].</summary>
    public const float PlayerReviveReach = 2f, PlayerReviveSeconds = 2f;
    /// <summary>A Marine revives a downed squad-mate or a fallen human when no Ork is within <see cref="SafeRadius"/> of the body, standing within <see cref="MarineReviveReach"/> for <see cref="MarineReviveSeconds"/> [ours/estimate].</summary>
    public const float SafeRadius = 6f, MarineReviveReach = 2f, MarineReviveSeconds = 2f, ReviveSearch = 30f;
    /// <summary>A Marine that damaged an Ork shares its kill as an assist when the kill follows within this [ours/estimate].</summary>
    public const float AssistSeconds = 5f;
    /// <summary>A Marine looks for a target every this many ticks (staggered by slot) [ours/estimate].</summary>
    public const int RetargetTicks = 3;
    /// <summary>Walking and sprinting pace, m/s (sprint when far from its place with nobody to shoot) [ours/estimate].</summary>
    public const float Speed = 5.5f, SprintSpeed = 8f;
    /// <summary>Two Marines closer than this push apart [ours/estimate].</summary>
    public const float Separation = 0.9f;
    /// <summary>Landing ring: Marine k of a roster lands at this radius plus k x step from its owner's landing point [ours/estimate].</summary>
    public const float LandRadius = 1.5f, LandRadiusStep = 0.35f;
    /// <summary>The default body when no loadout is mirrored yet (W11 fills the real values): health, armour [placeholder estimate].</summary>
    public const float DefaultHealth = 200f, DefaultArmour = 100f;
    /// <summary>The default ranged weapon when no loadout is mirrored yet: range m, damage per round, seconds between rounds, hit chance [placeholder estimate].</summary>
    public const float DefaultRange = 35f, DefaultDamage = 18f, DefaultInterval = 0.25f, DefaultHitChance = 0.7f;
    /// <summary>A Marine's body radius as the Orks target it (metres) [ours/estimate].</summary>
    public const float BodyRadius = 0.5f;
    /// <summary>Squad targets are <see cref="TargetBase"/> + index, negated: the Guard squads use -2 to -41, so this range (-1000 to -1035) is its own.</summary>
    public const int TargetBase = 1000;
    /// <summary>The share of an Ork hit a Marine takes: Musou's puppet Marines take a tenth (<c>TfwSupplyRules.BotDamageTaken</c>, owner 2026-11-06) [estimate: reused as the starting value]. The per-Ork level multiplier is applied by W3b's hook, not here.</summary>
    public const float DamageTaken = 0.10f;
    /// <summary>A shared path is rebuilt at most this many times a second per squad, and only when its target moved this far (metres) [ours/estimate].</summary>
    public const int PathRefreshPerSecond = 2;
    public const float PathMoved = 2f;
    /// <summary>A Marine goes straight for a goal this near when the graph says the line is walkable; farther, it follows the path [ours/estimate].</summary>
    public const float DirectRange = 12f;
    /// <summary>Rosters kept for a run (every key that ever had one) and Marines that fight at once: the 4 players' squads.</summary>
    public const int MaxKeptRosters = 64;
    /// <summary>The longest name a Marine can be given.</summary>
    public const int NameMax = 24;

    /// <summary>The Orks' target id of squad Marine <paramref name="index"/> (the owner's roster slot x <see cref="MaxPerPlayer"/> + the Marine's slot).</summary>
    public static int TargetId(int index) => -(TargetBase + index);

    /// <summary>The Marine index of a squad target id; -1 for any other id.</summary>
    public static int IndexOfTarget(int targetId) => targetId <= -TargetBase && targetId > -(TargetBase + MaxTotal) ? -targetId - TargetBase : -1;
}
