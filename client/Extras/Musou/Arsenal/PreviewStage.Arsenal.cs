using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmClient.Customizer;

/// <summary>
/// The arsenal in the customizer's viewer (owner 2026-11-12: the new weapons could not be seen, stowed by the relaxed pistol idle): while the primary or melee weapon on show is an added one the page carries
/// (<see cref="ArsenalRules.Shown"/>: Musou's claw, spear and autocannon, and the autocannon under Classic PvP's extended profile), the figure stands in the match's ready stance with the weapon in the hands (the rig's own
/// rules for the held slot, the claw always on its hand, the spear in its carry). Every original weapon keeps the relaxed pistol idle.
/// </summary>
public sealed partial class PreviewStage
{
    partial void ExtrasReadyPose(string? primary, string? melee, ref bool ready) =>
        ready = ArsenalRules.Shown(_services.Content, primary) || ArsenalRules.Shown(_services.Content, melee);
}
