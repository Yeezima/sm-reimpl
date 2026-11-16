using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Squad;

namespace SmSim;

/// <summary>The few reads the squad wiring (slice W9b, <see cref="WarfrontSquadWiring"/>) needs of the World: the Use bit and a human's loadout choice. Nothing here changes state.</summary>
public sealed partial class World
{
    /// <summary>Whether player <paramref name="id"/> holds Use (the Interact bit of the latest sampled input), which a human reviving a downed Marine holds; the same bit the pickups read.</summary>
    internal bool WarfrontUseHeld(int id) => _interactHeld.Contains(id);

    /// <summary>The loadout choice player <paramref name="id"/> plays now (class and the indices of his weapons, melee, equipment and perks), for the squad's mirror; null for an unknown player.</summary>
    internal LoadoutChoice? WarfrontChoiceOf(int id) =>
        _players.TryGetValue(id, out var p) ? new LoadoutChoice(p.Class, p.Primary, p.Pistol, p.MeleeChoice, p.Equipment, p.Perk1, p.Perk2, p.Second) : null;
}
