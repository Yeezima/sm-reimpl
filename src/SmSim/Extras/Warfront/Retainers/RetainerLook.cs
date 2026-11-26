using SmSim.Extras.Warfront.Squad;

namespace SmSim.Extras.Warfront.Retainers;

/// <summary>
/// The look a Warfront retainer (a World player, <see cref="SmSim.Extras.Musou.Campaign.MarineRole.Squad"/>) wears (Phase B T5 follow-up; the old squad crowd's rule, W11): its owner's own pieces and colours when
/// its loadout carries the owner's pieces (a standard retainer), or the class's own armour in the owner's colours (a Librarian's squad: pieces left to the class's default look, the palettes, pattern, decals,
/// lens and emblem the owner's). The owner is the player its squad fights under now (<see cref="WarfrontRetainer.Owner"/>, re-bound when he returns). Pure; the server's look path and the client's model read it.
/// Spec "Added content: Warfront: retainers as World players" (look).
/// </summary>
public static class RetainerLook
{
    /// <summary>A loadout wears its owner's own armour pieces when its armour list holds more than the class tag (<c>SquadLoadoutBuilder</c>; the same test as the roster wire's <c>OwnPieces</c>).</summary>
    public static bool OwnPieces(SquadLoadout loadout) => loadout.Armour.Count > 1;

    /// <summary>The wearer and the piece rule of the retainer in World id <paramref name="worldId"/>; null when that id is no retainer (a missing roster Marine keeps the owner's pieces).</summary>
    public static (int Owner, bool OwnPieces)? Of(WarfrontRetainers retainers, SquadRosters rosters, int worldId) =>
        retainers.ByWorldId(worldId) is { } r ? (r.Owner, rosters.Marine(r.Key, r.Slot) is not { } m || OwnPieces(m.Loadout)) : null;

    /// <summary>The appearance code worn: <paramref name="owner"/> itself with <paramref name="ownPieces"/>, else its colours, pattern, decals, lens and emblem on the default pieces.</summary>
    public static AppearanceCode CodeOf(AppearanceCode owner, bool ownPieces)
    {
        if (ownPieces) return owner;
        var code = AppearanceCode.Default;
        foreach (var slot in Enum.GetValues<ArmourSlot>())
        {
            code = code.WithPalette(slot, owner.Palette(slot, 0), owner.Palette(slot, 1), owner.Palette(slot, 2));
            code = code.WithPattern(slot, owner.PatternOn(slot));
        }
        code = code.WithDecals(owner.LeftDecal, owner.RightDecal, owner.FallbackBadge);
        if (owner.HasLens) code = code.WithLens(owner.Lens);
        if (owner.UsesEmblem) code = code.WithEmblem(owner.Emblem);
        return code;
    }
}
