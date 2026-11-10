using System;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Musou;

/// <summary>
/// The shape of a player's melee cleave on Musou's enemies, the same as <c>World.MusouCleave</c> picks its targets: a flat reach from the attacker's feet, a half angle off the heading and
/// the most enemies one strike takes (nearest first).
/// <para>The reach is <see cref="MusouCombatRules.CleaveRange"/> (the core's melee range, the cleave's extra range, the track-to-hit bonus; times the cleave scale in the war). The half angle is the
/// core's melee cone times <see cref="MusouCombatRules.CleaveConeScale"/>, at most 180 degrees; the count <see cref="MusouCombatRules.CleaveTargets"/>. In Musou Mode's war (<see cref="TfwCombatRules"/>)
/// the reach is also times <see cref="TfwCombatRules.CleaveReachScale"/>, the half angle is <see cref="TfwCombatRules.CleaveHalfAngleDegrees"/> and the count <see cref="TfwCombatRules.CleaveTargets"/>
/// for every melee weapon. A weapon of Musou's arsenal may set its own (W3, the spear's sweeps: <see cref="ArsenalShape"/>, in <c>Arsenal/MusouCleaveShape.Arsenal.cs</c>, from the same function as the
/// server's <c>World.ArsenalCleave</c>).</para>
/// <para>The client draws each swing's slash arc from this (owner, 2026-11-10: one flat curved line that matches the swing's cleave) and picks the own strike's predicted targets with it, so the arc,
/// the prediction and the hitbox use one formula. <c>MusouSlashArcTests</c> checks it against the server's own cleave in a real World. Presentation helper only: the server's cleave does not call it,
/// so nothing here changes a rule, Musou's version or a config hash.</para>
/// </summary>
public static partial class MusouCleaveShape
{
    /// <summary>A cleave's reach (metres, flat, from the attacker's feet), half angle (degrees to each side of the heading) and the most enemies it strikes.</summary>
    public readonly record struct Shape(float Reach, float HalfAngleDegrees, int Most)
    {
        /// <summary>The whole width of the cleave (degrees).</summary>
        public float SpanDegrees => 2f * HalfAngleDegrees;

        /// <summary>The least cosine between the heading and an enemy's direction that the cleave strikes (the server's <c>minCos</c>).</summary>
        public float MinCos => MathF.Cos(HalfAngleDegrees * MathF.PI / 180f);
    }

    /// <summary>The cleave of <paramref name="step"/> by a player wielding <paramref name="wargear"/>, in a war (<paramref name="war"/>) or in the 5-wave run.</summary>
    public static Shape Of(SimConfig config, MeleeStep step, bool war, string? wargear = null)
    {
        var rules = (config.MusouRules ?? MusouRules.Default).Combat;
        float reach = rules.CleaveRange(config.MeleeRange, step.TrackToHit, war);
        float half = MathF.Min(180f, config.MeleeConeDegrees * rules.CleaveConeScale);
        int most = rules.CleaveTargets;
        if (war)
        {
            var volume = TfwCombatRules.Default;   // World.Tfw: a war's cleave volume is always the default rules
            reach *= volume.CleaveReachScale;
            half = MathF.Min(180f, volume.CleaveHalfAngleDegrees);
            most = volume.CleaveTargets;
        }
        if (config.Extras.HasFlag(ContentExtras.Musou)) ArsenalShape(wargear, ref reach, ref half, ref most);
        return new Shape(reach, half, most);
    }

    /// <summary>
    /// Musou's arsenal: a weapon's own reach (metres), half angle (degrees) and count, from the same function the server's <c>World.ArsenalCleave</c> reads (W3, the spear's sweeps:
    /// <c>Arsenal/MusouCleaveShape.Arsenal.cs</c>), so the drawn arc keeps matching the hitbox.
    /// </summary>
    static partial void ArsenalShape(string? wargear, ref float reach, ref float halfAngleDegrees, ref int most);
}
