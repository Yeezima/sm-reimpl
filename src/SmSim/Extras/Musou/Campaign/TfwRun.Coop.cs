using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>Musou Mode co-op (<see cref="TfwCoop"/>; the owner, 2026-11-06): a human who joins while a battle is under way is flown in to it (ours).</summary>
public sealed partial class TfwRun
{
    readonly List<int> _late = new();

    /// <summary>Humans flown in to a battle under way since the run began (tests and the server's log).</summary>
    public int LateJoins { get; private set; }

    /// <summary>
    /// A living human who is not in the field while a battle or the final assault is under way (it joined after the landing: it spawned in the Stormbird) lands at the landing zone at the
    /// next free place of <see cref="TfwCoop.LandingOffset"/>, facing the HQ, with its retainer, and is one of the team from then on (its scores, its revive window, the extraction).
    /// Not during a pickup (the team is leaving: it waits in the Stormbird), not after the run's end.
    /// </summary>
    void JoinLate(World w)
    {
        if (Phase is not (TfwRunPhase.Battle or TfwRunPhase.FinalAssault) || Battle is not { } battle) return;
        _late.Clear();
        foreach (var (id, p) in w.Players)
            if (!TfwContracts.IsPuppet(id) && p.Alive && !_deployed.Contains(id)) _late.Add(id);
        if (_late.Count == 0) return;
        _late.Sort();
        var layout = battle.Layout;
        float yaw = layout.FaceCentre(layout.LandingZone);   // owner 2026-11-09: a spawn faces the map's centre
        foreach (int id in _late)
        {
            var at = Ashore(w, layout, TfwCoop.LandingOffset(_deployed.Count));
            _landingSlots[id] = _deployed.Count;   // TfwRun.Flow.cs: a redeploy comes back here
            _deployed.Add(id);
            w.TfwRespawnAt(id, at, yaw);
            w.TfwSpawnRetainer(id, Ashore(w, layout, at - layout.LandingZone + new Vector3(0f, -2f, 0f)), yaw);
            LateJoins++;
        }
        _deployed.Sort();
    }
}
