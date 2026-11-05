namespace SmSim.Extras.Musou.Ashfall;

public static partial class AshfallMap
{
    /// <summary>The dressing pass's version (raised with any change of the items).</summary>
    public const uint DressVersion = 0;

    /// <summary>
    /// The level with its kit dressing (deterministic; the same items in the same order on every machine). Server and client both call it, so the Prop items'
    /// collision proxies are part of the level and its hash. Stub: no items yet (the dressing pass replaces it).
    /// </summary>
    public static (AshfallLevel Level, AshfallDressing Dressing) BuildWithDressing()
    {
        var level = Build();
        return (level, new AshfallDressing([], [], DressVersion));
    }
}
