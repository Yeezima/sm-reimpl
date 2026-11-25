using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Retainers;

namespace SmServer;

/// <summary>
/// Warfront Phase B T5 follow-up: the look a retainer (a World player, <see cref="MarineRole.Squad"/>) shows others. Musou's <c>TfwPuppetAppearance</c> reads the owner fixed at the spawn
/// (<c>TfwMarine.Owner</c>); a Warfront squad follows its owner when he returns under a new id (<see cref="WarfrontRetainers.Rebind"/>), so the wearer is the registry's <see cref="WarfrontRetainer.Owner"/>.
/// A Librarian's squad wears the class's own pieces in the owner's colours (<see cref="RetainerLook"/>; the old squad crowd's rule, lost when the retainers became World players). No-op outside Warfront
/// (no squad wiring). Spec "Added content: Warfront: retainers as World players" (look).
/// </summary>
public sealed partial class GameServer
{
    partial void WarfrontPuppetWearer(int playerId, ref int wearer, ref bool ownPieces)
    {
        if (_wfWiring is not { } w || World.Tfw?.MarineOf(playerId) is not { Role: MarineRole.Squad }) return;
        if (RetainerLook.Of(w.Retainers, w.Rosters, playerId) is not { } look) return;
        wearer = look.Owner;
        ownPieces = look.OwnPieces;
    }

    partial void WarfrontColoursOnly(ref AppearanceCode code) => code = RetainerLook.CodeOf(code, ownPieces: false);
}
