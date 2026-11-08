using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The Scrap Meks of a battle (expansion S4, <see cref="MusouMeks"/>; Musou Mode war battles only, the 5-wave run and the skirmish never have a battle). The battle owns its Meks and their contraptions, so a
/// new battle starts with none; the World steps them (<c>World.TfwMeks.cs</c>) with what only it has (the targets' sight, the Guardsmen, the Ork blasts).
/// </summary>
public sealed partial class TfwBattle
{
    MusouMeks? _meks;
    readonly List<Vector3> _mekSpawns = new();

    /// <summary>Whether Scrap Meks come in this battle (ours: always; tests turn it off to count the other Orks alone).</summary>
    public bool MeksOn { get; set; } = true;

    /// <summary>This battle's Meks and contraptions (made at the first step that has them).</summary>
    public MusouMeks Meks => _meks ??= new MusouMeks(_enemies);

    /// <summary>The Meks' step (from the World, in play, before the battle is won): they come at the Ork spawn points (the positions the Orks hold, the HQ's source; any source when none is held).</summary>
    public void StepMeks(ReadOnlySpan<MusouTarget> targets, Func<Vector3, Vector3, bool>? blocked, Func<Vector3, float, bool>? guardsNear)
    {
        if (!MeksOn || Won) return;
        _mekSpawns.Clear();
        for (int k = 0; k < Positions; k++)
            if (_objectives[k].State != TfwPositionState.Secured) _mekSpawns.Add(_layout.Positions[k]);
        foreach (var source in _layout.Sources) _mekSpawns.Add(source);
        if (_mekSpawns.Count == 0) _mekSpawns.Add(_layout.Hq);
        Meks.Step(targets, _mekSpawns, blocked, guardsNear);
    }
}
