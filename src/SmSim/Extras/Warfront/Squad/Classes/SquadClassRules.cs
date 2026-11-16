namespace SmSim.Extras.Warfront.Squad;

/// <summary>The three squad classes the Librarian can set his whole squad to (owner, 2026-11-17): Assault/Raptor, Tactical, Devastator/Havoc.</summary>
public enum SquadClass : byte { Assault = 0, Tactical = 1, Devastator = 2 }

/// <summary>How the player's own class reads for the squad: Standard (a Classic class loadout, mirrored), Dreadnought (no active retainers) or Librarian (the squad's class is his option).</summary>
public enum SquadOwnerKind : byte { Standard = 0, Dreadnought = 1, Librarian = 2 }

/// <summary>
/// The numbers of Warfront's squad classes (slice W11; spec/behavior.md "Added content: Warfront: squad classes"). Every value is ours or an estimate unless a comment says it comes from the
/// class data; distances in metres, times in seconds.
/// </summary>
public static class SquadClassRules
{
    /// <summary>The class role (<c>ClassLoadout.Role</c>) of a squad class.</summary>
    public static string RoleOf(SquadClass c) => c switch { SquadClass.Assault => "assault", SquadClass.Devastator => "heavy", _ => "tactical" };

    /// <summary>The squad class of a class role; null for a role the Librarian cannot pick.</summary>
    public static SquadClass? OfRole(string role) => role.ToLowerInvariant() switch { "assault" => SquadClass.Assault, "tactical" => SquadClass.Tactical, "heavy" => SquadClass.Devastator, _ => null };

    // ---- the weapon profile read from the class data [ours/estimate]
    /// <summary>A ranged weapon's range is its data range, at most this (the squad does not snipe across the map).</summary>
    public const float RangeCap = 60f;
    /// <summary>Chance a round hits, by kind: full-auto, semi-auto, projectile or blast weapon, melee.</summary>
    public const float HitFullAuto = 0.65f, HitSemiAuto = 0.8f, HitProjectile = 0.75f, HitMelee = 0.9f;
    /// <summary>A projectile weapon's round does its direct damage plus this share of its blast damage.</summary>
    public const float SplashShare = 0.5f;
    /// <summary>The shortest time between rounds the profile allows.</summary>
    public const float MinInterval = 0.05f;
    /// <summary>A melee Marine's reach (the puppets' melee range).</summary>
    public const float MeleeReach = 2.5f;

    // ---- jump follow
    /// <summary>An Assault/Raptor Marine leaps to its place when its owner is farther than this, or airborne [plan section 6: "more than 12 m away"].</summary>
    public const float JumpFollowGap = 12f;
    /// <summary>A Marine considers a leap every this many ticks (staggered by index) [ours].</summary>
    public const int JumpCheckTicks = 10;
    /// <summary>Flight time: at least this, at most the pack's own airtime; plus the dive of a pound [ours/estimate].</summary>
    public const float MinFlight = 0.6f, PoundDive = 0.3f;
    /// <summary>A leap's cost is the full jump's power x its share of the pack's range, never below this share [ours/estimate].</summary>
    public const float MinCostShare = 0.35f;
    /// <summary>The owner counts as airborne when this high above the walking graph's floor, and as pounding when falling faster than the speed (m/s) [ours/estimate].</summary>
    public const float OwnerAirHeight = 1.5f, OwnerPoundSpeed = 12f;
    /// <summary>The view's Firing flag stays on this long after a round, so a 10 Hz wire sample does not miss it [ours].</summary>
    public const float FireHold = 0.3f;
    /// <summary>The apex of a flight under a roof (a tunnel or a cave), metres [ours/estimate].</summary>
    public const float RoofedApex = 2.5f;

    // ---- the jump-pound order
    /// <summary>Impacts land within this radius of the marked point [owner: "within a 5 m radius"].</summary>
    public const float PoundRadius = 5f;
    /// <summary>Two landings are at least this far apart (flat) [plan section 6: 1.2 m].</summary>
    public const float PoundSpacing = 1.2f;
    /// <summary>Impacts follow each other this many seconds apart [plan section 6: 0.15 s].</summary>
    public const float PoundStagger = 0.15f;
    /// <summary>A landing is valid when the walking graph's floor is within this of the wanted place (flat), and so on the graph [ours].</summary>
    public const float LandingGround = 1f;
    /// <summary>Landing places tried for one Marine before it walks instead [ours].</summary>
    public const int LandingAttempts = 24;
    /// <summary>A Marine with no valid landing walks to the mark for at most this long [ours].</summary>
    public const float PoundWalkSeconds = 8f;
    /// <summary>Enemies one impact reaches at most [ours].</summary>
    public const int BlastMax = 16;

    /// <summary>The ticks between impacts at <paramref name="tickRate"/>: the stagger rounded up, so never under 0.15 s.</summary>
    public static int StaggerTicks(int tickRate) => Math.Max(1, (int)MathF.Ceiling(PoundStagger * Math.Max(1, tickRate) - 1e-4f));
}
