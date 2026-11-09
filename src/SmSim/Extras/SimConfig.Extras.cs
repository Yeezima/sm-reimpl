using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// The config hash's added-content block (docs/added-content.md): the one implementation of <see cref="ExtrasHash"/>, a dispatcher that writes each added
/// content's own block only under its own flag. Nothing is written for <see cref="ContentExtras.Orks"/>, <see cref="ContentExtras.Gore"/> or
/// <see cref="ContentExtras.LoyalistBarks"/> (they ride on <see cref="ContentExtras.WireVersion"/>), so the extended profile hashes as before Musou existed
/// (pinned in <c>MusouIsolationTests</c>).
/// </summary>
public sealed partial record SimConfig
{
    partial void ExtrasHash(BinaryWriter w)
    {
        // Musou: its own version (simulation and wire sections), so a Musou-only change never touches ContentExtras.WireVersion or the extended hash.
        // Musou Mode's war rules: their version sits in the same block.
        if (Extras.HasFlag(ContentExtras.Musou)) { w.Write("musou"); w.Write(MusouSim.Version); w.Write(SmSim.Extras.Musou.Campaign.TfwRules.Version); }
    }

    // sim/air-jumps (owner, 2026-11-06): only Musou Mode keeps the jump-pack classes' full jumps in the air (feel/melee-cancels); Classic PvP gets the hop alone.
    partial void ExtrasAirJumps(ref bool allowed)
    {
        if (Extras.HasFlag(ContentExtras.Musou)) allowed = true;
    }

    // musou/no-spread (owner, 2026-11-06): Musou Mode's shots have no spread at all.
    partial void ExtrasNoBulletSpread(ref bool off)
    {
        if (Extras.HasFlag(ContentExtras.Musou)) off = true;
    }

    // night batch 2026-11-06 (owner): Musou Mode keeps the owner's jump-pack capacity (300); Classic PvP has the data's.
    partial void ExtrasJumpPackOwnerCapacity(ref bool owner)
    {
        if (Extras.HasFlag(ContentExtras.Musou)) owner = true;
    }

    // musou/combat-rules (owner, 2026-11-09): Musou Mode's combat stims last 3x, hit 2x, swing and fire 20 % faster, and spend no ammunition; its frag blasts reach 2x.
    partial void ExtrasStimBoost(ref StimBoost boost)
    {
        if (Extras.HasFlag(ContentExtras.Musou)) boost = new StimBoost(MusouCombatRules.StimLifetimeScale, MusouCombatRules.StimMeleeDamage, MusouCombatRules.StimAttackSpeed, true);
    }

    partial void ExtrasThrownBlastScale(ThrownSpec? item, ref float scale)
    {
        if (Extras.HasFlag(ContentExtras.Musou) && item is not null && SmSim.Extras.Musou.Campaign.TfwCombatRules.IsFrag(item)) scale = MusouCombatRules.FragReachScale;
    }
}
