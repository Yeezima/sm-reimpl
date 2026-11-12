namespace SmSim;

/// <summary>
/// musou/no-aerial-tackle (owner, 2026-11-12, temporary): the aerial (flying) tackle is the jump-pack dash attack, a melee press in a hop's window
/// (<see cref="Rush.CanDashAttack"/>). Added content may turn it off for a config (<see cref="AerialTackleOff"/>, hook <c>ExtrasAerialTackleOff</c>): a melee press in a hop is then
/// always the light attack, started at once with no wait after it (<see cref="MeleeCancel.Apply"/>). False in the faithful game and Classic PvP, where the hook does nothing.
/// </summary>
public sealed partial record SimConfig
{
    /// <summary>Whether the aerial tackle (the dash attack) never starts and a melee press in a hop swings at once, with no cancel wait. Not hashed itself: added content's block covers it.</summary>
    public bool AerialTackleOff
    {
        get
        {
            bool off = false;
            ExtrasAerialTackleOff(ref off);   // EXTRAS-HOOK
            return off;
        }
    }

    /// <summary>Restore switch (<c>--musou-aerial-tackle</c> on the server and every client; config hash): with it the added content leaves the aerial tackle on. Hashed by added content's block only when on.</summary>
    public bool AerialTackleRestored { get; init; }

    partial void ExtrasAerialTackleOff(ref bool off);
}
