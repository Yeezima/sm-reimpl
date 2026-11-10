using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// musou/boss-collision (<see cref="MusouSolids"/>): the World's side. Each tick's end keeps the solid big Orks (with the players' bodies record, <c>WorldBodies.cs</c>); a
/// player's step meets them as its input's body tick had them, quantised as the wire carried them, and its melee lock may take them as the input's view tick had them.
/// Returns at once outside a Musou match (Classic PvP never reaches here).
/// </summary>
public sealed partial class World
{
    readonly Dictionary<uint, MusouSolid[]> _musouSolids = new();
    readonly List<MusouSolid> _musouSolidScratch = new();

    partial void ExtrasRecordBodies()
    {
        if (_musou is null) return;
        _musou.Solids(_musouSolidScratch);
        _musouSolids[Tick] = _musouSolidScratch.Count == 0 ? [] : _musouSolidScratch.ToArray();
        if (Tick >= BodyHistoryTicks) _musouSolids.Remove(Tick - BodyHistoryTicks);
        LibrarianRecord();   // Librarian/World.Librarian.cs: the pool's places of this tick, for the chain lightning's rewind (musou-balance 2026-11-11)
    }

    /// <summary>The solid big Orks at the end of tick <paramref name="tick"/> as the wire carried them (quantised over <see cref="MusouSim.WireBox"/>; raw before the first send); empty when not kept.</summary>
    public IReadOnlyList<MusouSolid> MusouSolidsAt(uint tick)
    {
        if (_musou is null) return [];
        // Within a step the view of this tick (not recorded yet): the bodies as the last tick left them (the Orks move after the players).
        if (!_musouSolids.TryGetValue(tick, out var raw) && !(tick >= Tick && Tick > 0 && _musouSolids.TryGetValue(Tick - 1, out raw))) return [];
        if (raw.Length == 0) return [];
        if (_musou.WireBox is not { } box) return raw;
        var quantised = new MusouSolid[raw.Length];
        for (int k = 0; k < raw.Length; k++) quantised[k] = raw[k] with { Position = MusouSolids.Quantised(raw[k].Position, box) };
        return quantised;
    }

    partial void ExtrasBodies(uint tick, ref BodyView? view)
    {
        if (_musou is null) return;
        MusouSolids.AddBodies(ref view, MusouSolidsAt(tick), Config.MusouRules ?? MusouRules.Default);
    }

    partial void ExtrasLockView(uint tick, in PlayerState self, ref IReadOnlyDictionary<int, PlayerState> view)
    {
        if (_musou is null) return;
        view = MusouSolids.WithLockTargets(view, MusouSolidsAt(tick), self, Config.MusouRules ?? MusouRules.Default, Config);
    }
}
