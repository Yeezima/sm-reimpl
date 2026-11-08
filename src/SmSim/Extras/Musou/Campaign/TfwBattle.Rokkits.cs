namespace SmSim.Extras.Musou.Campaign;

public sealed partial class TfwBattle
{
    /// <summary>Whether Rokkit Boyz join this battle's crowds (expansion S2; ours: always; tests turn it off to count the Boyz and Shootas alone).</summary>
    public bool RokkitsOn { get; set; } = true;

    /// <summary>
    /// The kind of a crowd's next Ork (expansion S2): a Rokkit Boy by <see cref="MusouRokkitRules.CrowdShare"/> (a low-discrepancy sequence of its own, no dice), else a Boy or a Shoota as before
    /// (<see cref="Fodder"/>). Garrisons and the trickle keep their Boyz and Shootas.
    /// </summary>
    MusouEnemyType CrowdKind(int n) =>
        RokkitsOn && n * 0.5698402909980532 % 1.0 < MusouRokkitRules.CrowdShare ? MusouEnemyType.RokkitBoy : Fodder(n);
}
