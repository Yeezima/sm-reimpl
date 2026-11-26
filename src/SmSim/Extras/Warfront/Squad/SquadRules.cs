using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Warfront.Squad;

/// <summary>
/// The numbers of Warfront's lightweight squad (slice W9; spec/behavior.md "Added content: Warfront: squads"). Counts, the 20 s downed window and "out for that battle only" are the owner's
/// (2026-11-17); every other value is ours or an estimate, tagged in the spec and so in the refine register. Distances in metres, times in seconds. Movement reuses
/// <see cref="MarineRules"/> follow/hold/attack bands (the puppet retainers' numbers) so the orders read the same as in Musou Mode.
/// </summary>
public static class SquadRules
{
    /// <summary>
    /// The roster's capacity (owner 2026-11-17: 9 per player, 4 players, 36 in all): what the squad sim, its target ids and the squad wire are sized for, and what a bench may fill. A run never grows a roster past
    /// <see cref="RetainersPerPlayer"/> (owner 2026-11-23, card 2).
    /// </summary>
    public const int MaxPerPlayer = 9, MaxOwners = 4, MaxTotal = MaxPerPlayer * MaxOwners;
    /// <summary>Every player always has exactly this many retainers (owner 2026-11-23, card 2: "make it so each player always has 2 retainers except the dreadnought"); a Dreadnought's roster is held, so he has none in the field [owner].</summary>
    public const int RetainersPerPlayer = 2;
    /// <summary>A new roster starts with <see cref="RetainersPerPlayer"/> retainers (was 1 and grew by +1 Retainer cards until 2026-11-23) [owner].</summary>
    public const int StartRetainers = RetainersPerPlayer;
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
    /// <summary>The squad's damage dealt, a multiplier on every Marine's weapon and pound damage: the one knob for squad strength (W11 review 1; ruling of 2026-11-17: 1 to 0.40, one Marine deals 0.08 to 0.25 of a bolter player) [ours, estimate, low].</summary>
    public const float DamageDealt = 0.40f;
    /// <summary>A Marine's body radius as the Orks target it (metres) [ours/estimate].</summary>
    public const float BodyRadius = 0.5f;
    /// <summary>Squad targets are <see cref="TargetBase"/> + index, negated: the Guard squads use -2 to -41, so this range (-1000 to -1035) is its own.</summary>
    public const int TargetBase = 1000;
    /// <summary>The share of an Ork hit a Marine takes (ruling of 2026-11-17: 0.10, Musou's puppet Marines' share, to 0.35, so a busy wave drops Marines in sequence) [ours, estimate, low]. The per-Ork level multiplier is applied by W3b's hook, not here.</summary>
    public const float DamageTaken = 0.35f;
    /// <summary>A partial path (the goal not reached) is still followed when its end is at least this much nearer the goal than the squad's centre (metres); only no progress is "no way" [ours, estimate, low] (W9b review 1).</summary>
    public const float PartialProgress = 10f;
    /// <summary>The share of an Ork's level damage multiplier a Marine takes (tuning ruling T1: 0, the squad's durability advances with the level as the Orks' damage does) [ours, estimate, low].</summary>
    public const float OrkLevelDamageShare = 0f;
    /// <summary>A place is out of a human's sight when it is behind him: more than this angle (radians, 100 degrees) from where he faces, or hidden by a wall [ours, estimate].</summary>
    public const float ViewHalfAngle = 1.745f;
    /// <summary>A search for a path or a route expands at most this many nodes; farther is "no way" for a follow [ours, estimate].</summary>
    public const int SearchNodes = 8_000;
    /// <summary>A failed search is not repeated for this long, or until the owner has moved <see cref="FailedMoved"/> m, whichever comes first (seconds, metres) [ours, estimate].</summary>
    public const float FailedRetrySeconds = 3f, FailedMoved = 10f;
    /// <summary>The relocate failsafe: a squad whose owner is unreachable for <see cref="StrandedSeconds"/> with the owner farther than <see cref="StrandedDistance"/> is put <see cref="RelocateNear"/> to <see cref="RelocateFar"/> m from the owner; one squad a second at most [ours, estimate].</summary>
    public const float StrandedSeconds = 10f, StrandedDistance = 40f, RelocateNear = 8f, RelocateFar = 12f, RelocateGapSeconds = 1f;
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
