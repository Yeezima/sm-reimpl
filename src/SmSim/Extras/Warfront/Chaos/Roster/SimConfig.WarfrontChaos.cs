using SmSim.Extras.Warfront.Faction;

namespace SmSim;

/// <summary>
/// Warfront's army in the match's config (Chaos Warfront C2): the stage's faction, fixed before its World exists (a stage process runs one army; a deployment of the other restages). Orks (0) in every
/// other config. Chaos goes with the Chaos type table in <see cref="MusouRules"/> (both set together by <c>SmContent.Extras.Warfront.WarfrontChaosTuning.Apply</c>). Not in the config hash: the
/// enemies are the server's alone and the clients learn the army from the Hub's faction byte; the rules it changes are covered by <c>WarfrontRules.Version</c>.
/// </summary>
public sealed partial record SimConfig
{
    /// <summary>The army this stage's enemy sim fields (Orks: unchanged; Chaos: the Chaos roster, <c>Extras/Warfront/Chaos/Roster/</c>).</summary>
    public WarfrontFaction WarfrontFaction { get; init; }
}
