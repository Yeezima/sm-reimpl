using SmSim.Extras.Warfront;

namespace SmSim;

/// <summary>
/// Warfront without Squigs (owner 2026-11-25: "temporarily remove squigs from warfront mode"; ours, a temporary switch): while <see cref="WarfrontRules.NoSquigs"/> holds, a Warfront run
/// spawns no Bomb Squig (the drop director and its capture aid in <c>World.TfwSquigs.cs</c> are the only squig sources). Musou Mode, which has no Warfront object, keeps its Squigs.
/// </summary>
public sealed partial class World
{
    partial void WarfrontSquigs(ref bool on)
    {
        if (WarfrontRules.NoSquigs && _tfw?.Warfront is not null) on = false;
    }
}
