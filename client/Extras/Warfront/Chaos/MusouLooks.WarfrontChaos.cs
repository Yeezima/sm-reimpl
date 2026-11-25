using SmContent.Extras.Warfront;
using SmFormats.Havok;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// The C0 looks seam's body (Chaos Warfront C3): in a Chaos battle each kind is drawn with the Chaos army's look (<see cref="WarfrontChaosLooks"/>); a marine kind whose own look is not built yet takes
/// Musou Mode's Chaos Marine (the PvP Chaos model), any other kind nothing. Never an Ork: <see cref="WarfrontOwnsLooks"/> keeps <see cref="For"/> from falling back to the Ork table.
/// </summary>
public sealed partial class MusouLooks
{
    /// <summary>The Chaos army's looks while the battle is a Chaos one (set each frame by the Warfront view); null otherwise.</summary>
    public WarfrontChaosLooks? WarfrontChaos;

    partial void WarfrontLookFor(MusouEnemyType type, ref Look? look)
    {
        if (WarfrontChaos is not { } chaos) return;
        look = chaos.For(type) ?? (WarfrontChaosLooksPlan.IsMarine(type) ? chaos.For(MusouEnemyType.ChaosMarine) ?? _looks[(int)MusouEnemyType.ChaosMarine] : null);
    }

    /// <summary>The marine rig's crowd clips (run, idle, attack, stagger, death) of a Chaos class look.</summary>
    internal static HavokBoundAnimation?[] WarfrontMarineClips(MarineAssets a) => MarineClips(a);

    /// <summary>A missing clip takes the idle's (or the run's) place (<see cref="Fill"/>).</summary>
    internal static void WarfrontFill(HavokBoundAnimation?[] clips) => Fill(clips);
}

/// <summary>The C0 flinch seam's body (Chaos Warfront C3): the armoured marine-rig Chaos units do not flinch at a shot (as Musou Mode's Chaos Marine draws none of the Orks' bone pose); men and daemons keep the kick.</summary>
public partial class MusouCrowd
{
    partial void WarfrontFlinchReacts(MusouEnemyType type, ref bool reacts)
    {
        if (_looks.WarfrontChaos is not null && WarfrontChaosLooksPlan.IsMarine(type)) reacts = false;
    }
}
