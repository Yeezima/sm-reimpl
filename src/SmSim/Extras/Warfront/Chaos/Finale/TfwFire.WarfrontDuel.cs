namespace SmSim.Extras.Musou.Campaign;

/// <summary>The fire support in the duel (Chaos Warfront C5): no barrage lands while the relay is on (support is outside help, ours). The World calls it from its duel step only.</summary>
public sealed partial class TfwFire
{
    /// <summary>Every shell still to land is called off; the number dropped.</summary>
    internal int WarfrontDuelDropShells()
    {
        int n = _shells.Count;
        _shells.Clear();
        return n;
    }
}
