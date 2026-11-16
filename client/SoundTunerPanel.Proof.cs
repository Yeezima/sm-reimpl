using System;
using System.Linq;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// sound/mix-expand test aid (<c>--sound-mix-proof &lt;png prefix&gt;</c>, run by <c>NetworkView.RunSoundMixProof</c>): the panel's steps without a mouse, through the same methods the buttons and lists call,
/// so a windowed run on the private desktop can show them in screenshots. Never used by the game.
/// </summary>
public partial class SoundTunerPanel
{
    /// <summary>Picks a sound as a click in its group's list would (side, group, clear search, select).</summary>
    public void ProofSelect(string evt, bool own, string search = "")
    {
        if (_groupBox is null || _search is null) return;
        _own = own;
        _ownBtn?.SetPressedNoSignal(own);
        _othersBtn?.SetPressedNoSignal(!own);
        _sel = evt;
        _groupBox.Select(Array.IndexOf(SoundMixCatalog.Groups.ToArray(), SoundMixCatalog.Classify(evt).Group));
        _search.Text = search;
        RebuildList();
    }

    /// <summary>Sets the selected sound's volume and pitch as its sliders do.</summary>
    public void ProofEdit(float volumeDb, float pitchSemitones, float distanceScale = 1f) { Edit(o => o with { VolumeDb = volumeDb, PitchSemitones = pitchSemitones, DistanceScale = distanceScale }); UpdateEditor(); }

    /// <summary>Marks the selected sound raw, as its checkbox does.</summary>
    public void ProofRaw(bool on) { Edit(o => o with { Raw = on }); UpdateEditor(); }

    public void ProofPlay() { if (_sel.Length > 0) _preview?.Invoke(_sel, _own); }

    /// <summary>Clicks the live list's row of this event (as <see cref="SelectFromLive"/> does); false when it is not listed.</summary>
    public bool ProofClickLive(string evt)
    {
        RebuildLive();
        int i = _liveRows.FindIndex(r => string.Equals(r.Event, evt, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        _live?.Select(i);
        SelectFromLive(_liveRows[i]);
        return true;
    }

    public void ProofExport() => Export();

    public string StatusText => _status?.Text ?? "";

    public string LiveText => string.Join(" | ", Enumerable.Range(0, _live?.ItemCount ?? 0).Select(i => _live!.GetItemText(i)));
}
