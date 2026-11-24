using System.Numerics;
using SmSim.Extras.Musou;

namespace SmSim.Extras.Warfront;

/// <summary>
/// Warfront's walk-over pickups (owner 2026-11-25: "make a change so that pickups are picked up just by walking over them"; on the cards: "Warfront only" and "Weapons stay on E"). A living human takes his own
/// drops that are not weapons (the powerups, the crates) and any ammunition or equipment box by standing over one: within <see cref="WalkOverMetres"/> of his feet on the flat and <see cref="WalkOverHeight"/>
/// up or down (kind ours). No press and no hold. A weapon or melee drop still needs the interact press, as in Musou Mode. A drop that cannot be given now stays and is taken when it can: a box that would add
/// nothing (every weapon and the equipment full) stays on the floor, a crate whose weapon cannot be given now stays. The AI Marines (the retainers among them) take nothing. Musou Mode and Classic are unchanged.
/// </summary>
public static class WarfrontPickupRules
{
    /// <summary>The walk-over radius on the flat, metres from the feet (ours: a Marine stands on it; Musou's press reach is 1.5 m, a Classic box's 4 m).</summary>
    public const float WalkOverMetres = 1.0f;

    /// <summary>How far up or down a pickup may lie from the feet and still be walked over, metres (ours: a step or a slope, not the floor above or below).</summary>
    public const float WalkOverHeight = 1.0f;

    /// <summary>
    /// Whether a crate is walked over (the lead's brief of 2026-11-25 lists crates with the walk-over pickups). A Musou Mode crate holds a weapon roll: false puts crates back on the press with the weapons.
    /// Crates are not placed today (<see cref="MusouDropRules.WeaponDrops"/> is off), so this changes nothing in play until the weapon drops come back.
    /// </summary>
    public const bool CratesWalkOver = true;

    /// <summary>Whether <paramref name="drop"/> is taken by walking over it: a powerup or a crate; a weapon or melee drop is not.</summary>
    public static bool WalksOver(in MusouDrop drop) => WalksOver(drop.Item, drop.Crate);

    /// <summary>Whether a drop of <paramref name="item"/> (a crate when <paramref name="crate"/>) is taken by walking over it (the client's records carry the two).</summary>
    public static bool WalksOver(ushort item, bool crate) => crate ? CratesWalkOver : MusouPowerupRules.IsPowerup(item);

    /// <summary>Whether feet at <paramref name="feet"/> stand over a pickup at <paramref name="at"/>.</summary>
    public static bool Over(Vector3 at, Vector3 feet)
    {
        float dx = at.X - feet.X, dy = at.Y - feet.Y;
        return dx * dx + dy * dy <= WalkOverMetres * WalkOverMetres && MathF.Abs(at.Z - feet.Z) <= WalkOverHeight;
    }
}
