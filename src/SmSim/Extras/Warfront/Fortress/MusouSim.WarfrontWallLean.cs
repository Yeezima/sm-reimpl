namespace SmSim.Extras.Musou;

/// <summary>W14d: the fortress Warboss leans off walls as a Boss Ork does (his 3.4 m body was pinned on the plaza openings' walls). No-op without the Warfront object; Musou's own Warboss is untouched.</summary>
public sealed partial class MusouSim
{
    partial void WarfrontWallLean(MusouEnemyType type, ref bool lean)
    {
        if (Warfront is not null && type == MusouEnemyType.Warboss) lean = true;
    }
}
