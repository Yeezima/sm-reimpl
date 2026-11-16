using SmSim.Extras.Musou.Arsenal;

namespace SmSim.Extras.Musou;

/// <summary>
/// The arsenal's part of the cleave's shape (the swing arc, owner 2026-11-10): a weapon of Musou's arsenal that sets its own cleave sets it here through the same function the server's
/// <c>World.ArsenalCleave</c> calls, so the drawn line and the hitbox cannot drift apart. W3, the power spear: <see cref="SpearRules.Sweep"/> (15 m from the feet, 150 degrees each side, 12 Orks).
/// </summary>
public static partial class MusouCleaveShape
{
    static partial void ArsenalShape(string? wargear, ref float reach, ref float halfAngleDegrees, ref int most) => SpearRules.Sweep(wargear, ref reach, ref halfAngleDegrees, ref most);
}
