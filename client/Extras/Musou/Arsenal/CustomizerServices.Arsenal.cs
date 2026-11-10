using System;
using System.Threading.Tasks;
using SmContent;
using SmSim;

namespace SmClient.Customizer;

/// <summary>
/// Musou's arsenal in the customizer (W1; docs/new-weapons-design.md section 11, "First, the customizer's config"): the front end's customizer reads one data configuration (no added content), so a Join
/// planned from Musou's page would look the autocannon up in a list that does not have it and send the default primary. Under a Musou content profile <see cref="CustomizerServices.Config"/> is the
/// configuration the match tuning builds for that profile (<see cref="GameplayTuning.LoadForMatch"/>, which appends the arsenal): read once per profile on a worker the first time something asks, and null
/// until it is ready, so <see cref="CustomizerServices.Ready"/> (and so PLAY and JOIN) wait for it; if the read fails the data's configuration is used and the failure noted. The in-match picker (it has the
/// match's own configuration) and every profile without Musou are untouched.
/// </summary>
public sealed partial class CustomizerServices
{
    readonly object _musouConfigLock = new();
    (ContentExtras Content, Task<SimConfig?> Task)? _musouConfig;

    partial void ExtrasConfig(ref SimConfig? config)
    {
        var content = Content;
        if (config is null || InMatch || !content.HasFlag(ContentExtras.Musou)) return;
        Task<SimConfig?> task;
        lock (_musouConfigLock)
        {
            if (_musouConfig is not { } known || known.Content != content) _musouConfig = known = (content, Task.Run(() => ReadMusouConfig(content)));
            task = known.Task;
        }
        config = task.IsCompleted ? task.Result ?? config : null;
    }

    SimConfig? ReadMusouConfig(ContentExtras content)
    {
        try
        {
            var tuning = GameplayTuning.LoadForMatch(GameDir, content);
            lock (Notes) Notes.AddRange(tuning.Notes);
            return tuning.Config;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            lock (Notes) Notes.Add("musou customizer config: " + ex.Message + "; the data's configuration is used");
            return null;
        }
    }
}
