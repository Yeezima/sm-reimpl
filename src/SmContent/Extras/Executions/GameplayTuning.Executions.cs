using SmContent.Extras.Executions;
using SmSim;

namespace SmContent;

/// <summary>
/// Executions X3: the catalogue on the match config (<see cref="SimConfig.Executions"/>), from the player's own install (<see cref="ExecutionCatalogueLoader.Load"/>, read-only). Only under
/// <see cref="ContentExtras.Executions"/> (the dispatcher in <c>GameplayTuning.Extras.cs</c>). An install without the player set loads nothing (no execution can be played). The rows are not in
/// the config hash: the server's are authoritative (lead 39, 2026-11-25).
/// </summary>
public sealed partial record GameplayTuning
{
    static partial void ExecutionsLoad(ref SimConfig config, string gameDir, List<string> notes)
    {
        if (string.IsNullOrEmpty(gameDir))
        {
            notes.Add("executions: no install; no rows loaded (nothing can be executed)");
            return;
        }
        var catalogue = ExecutionCatalogueLoader.Load(gameDir, notes);
        config = config with { Executions = catalogue.Set };
        notes.Add($"executions: {catalogue.Rows.Count} rows of {catalogue.WireIds.Count} loaded; kinds {string.Join(", ", catalogue.Kinds)}");
    }
}
