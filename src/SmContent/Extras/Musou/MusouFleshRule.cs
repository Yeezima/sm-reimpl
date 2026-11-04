using SmSim.Extras.Musou;

namespace SmContent;

/// <summary>
/// Musou Mode (added content): which Ork enemy types a hit always sounds as the flesh case of the hit sound (owner, 2026-11-04: "when killing orks in musou mode, specifically ones that have no armor,
/// the flesh hit sounds should always be played. armored orks will follow the same default rules as usual"). The Musou enemy rules have no armour value (a type is health, speed and size,
/// <see cref="MusouTypeSpec"/>); the Ork Boy's blueprint has no armour shield either (<c>OrkBalance</c>: "none in the data"). So the type decides, **interpretation**: the Boy, the Shoota Boy and the
/// Gretchin are unarmoured (flesh only); the 'Ard Boy, the Nob and the Warboss count as armoured and play armour and flesh together (owner 2026-11-04, "Armour and flesh"; the name and the heavier build), and the Chaos Space Marine is no Ork and keeps the usual rule.
/// </summary>
public static class MusouFleshRule
{
    public static bool AlwaysFlesh(MusouEnemyType type) => type is MusouEnemyType.Boy or MusouEnemyType.ShootaBoy or MusouEnemyType.Gretchin;

    /// <summary>
    /// The hit sound's armour value for a hit on this type, one rule for every view (own strike, cleave, own and others' shots; owner 2026-11-04, "Armour and flesh"): <c>false</c> (flesh only) for an
    /// unarmoured type; <c>null</c> (unknown: the armour and the flesh layers together, <c>HitSoundRules.Of(null)</c>) for an armoured one and the Chaos Marine.
    /// </summary>
    public static bool? Material(MusouEnemyType type) => AlwaysFlesh(type) ? false : null;
}
