using SmContent.Extras.Warfront;
using SmSim;
using SmSim.Extras.Warfront.Faction;

namespace SmServer;

/// <summary>
/// A stage's army in its config (Chaos Warfront C2): Program.cs, after <see cref="WarfrontRunFiles.RecordStageFaction"/> and before the World exists, gives the stage's config the run's army
/// (<see cref="WarfrontChaosTuning.Apply"/>: a Chaos stage gets the Chaos type table and the faction; an Orks stage's config is returned unchanged).
/// </summary>
static class WarfrontStageArmy
{
    /// <summary>The stage's config for <see cref="WarfrontRunFiles.StageFaction"/>, and the log line (null for Orks: nothing changed).</summary>
    internal static SimConfig Apply(SimConfig config, out string? note)
    {
        note = null;
        if (WarfrontRunFiles.StageFaction != WarfrontFaction.Chaos) return config;
        var chaos = WarfrontChaosTuning.Apply(config, WarfrontFaction.Chaos);
        note = WarfrontChaosTuning.Note(chaos.MusouRules!);
        return chaos;
    }
}
