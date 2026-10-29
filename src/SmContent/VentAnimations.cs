using SmFormats.Havok;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>
/// The vent's clips (fidelity gap 7; the client): the graph <c>SM_Plasma_Vent</c>'s states "Low Heat Vent", "Medium Heat Vent", "High Heat Vent"
/// and "Overheat Vent" each blend the locomotion graph with one clip (<c>SM_RNG_Idle_Vent_Heat_Low</c>, <c>_Med</c>, <c>_High</c>, looping; the
/// overheat's <c>SM_RNG_OverHeat_Vent_02</c>, additive, played once). The pistol's <c>SM_PlasmaPistol_Vent</c> is not read (its five clips
/// have the same roles; interpretation: the pistol shows the rifle's vent clips).
/// </summary>
public sealed record VentAnimations(IReadOnlyDictionary<VentStage, (string Clip, bool Additive, bool Looping)> Clips)
{
    /// <summary>The vent graph.</summary>
    public const string Graph = "SM_Plasma_Vent";

    /// <summary>The state of each stage in <see cref="Graph"/>.</summary>
    public static readonly IReadOnlyDictionary<VentStage, string> States = new Dictionary<VentStage, string>
    {
        [VentStage.Low] = "Low Heat Vent", [VentStage.High] = "High Heat Vent", [VentStage.Overheat] = "Overheat Vent",
    };

    /// <summary>The clips from the character's graphs (the clip of each stage's blend), or null with a note.</summary>
    public static VentAnimations? Read(IReadOnlyList<BehaviorGraphDetail> graphs, List<string> notes)
    {
        if (graphs.FirstOrDefault(g => g.Name == Graph) is not { } g) { notes.Add($"vent: graph {Graph} not found"); return null; }
        var clips = new Dictionary<VentStage, (string, bool, bool)>();
        foreach (var (stage, name) in States)
        {
            if (g.AllStates.FirstOrDefault(x => x.State.Name == name).State is not { Generator: int at }) { notes.Add($"vent: state '{name}' not found"); continue; }
            var clip = g.ClipAt(at) ?? g.BlenderAt(at)?.Children.Select(c => c.Generator is int cg ? g.ClipAt(cg) : null).FirstOrDefault(c => c is not null);
            if (clip is null) { notes.Add($"vent: state '{name}' plays no clip"); continue; }
            clips[stage] = (clip.Animation, stage == VentStage.Overheat, clip.Mode == 1);
        }
        return clips.Count == 0 ? null : new VentAnimations(clips);
    }

    /// <summary>
    /// What the client shows of the brace and the vent, from the install's marine rig (read-only; one parse of its graphs): the brace's
    /// <see cref="BraceTuning.Timings"/> and the vent's clips, each null with a note when missing.
    /// </summary>
    public static (BraceTuning.Timings? Brace, VentAnimations? Vent) LoadPresentation(string gameDir, List<string> notes)
    {
        byte[]? rig = null;
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, CharacterModel.MarineRigArchive)))
            archive.ReadEntries(e => e.FileName == CharacterModel.MarineRigEntry && rig is null, (_, d) => rig = d.ToArray());
        if (rig is null) { notes.Add($"brace and vent: '{CharacterModel.MarineRigEntry}' not found"); return (null, null); }
        var character = new HavokCharacterFile(rig);
        var graphs = HavokBehaviorDetailReader.Read(character.Pack);
        return (BraceTuning.Read(character, graphs, notes), Read(graphs, notes));
    }
}
