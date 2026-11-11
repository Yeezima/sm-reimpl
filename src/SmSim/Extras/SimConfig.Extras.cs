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

    // librarian zoom (owner, 2026-11-12): the Librarian carries no gun, but the Aim key zooms him like every class (his cast keys are Fire, Grenade and Reload).
    partial void ExtrasAimWithoutWeapon(int classIndex, ref bool aims)
    {
        if (SmSim.Extras.Musou.LibrarianRules.IsLibrarian(this, classIndex)) aims = true;
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
        if (Extras.HasFlag(ContentExtras.Musou)) boost = new StimBoost(MusouCombatRules.StimLifetimeScale, MusouCombatRules.StimMeleeDamage, MusouCombatRules.StimAttackSpeed, true, Reliable: true);   // + owner 2026-11-12: a reliable use (a 0.5 s kept press, no hit cancel before the release)
    }

    partial void ExtrasFireBoost(ref float rate, ref float cap)
    {
        if (!Extras.HasFlag(ContentExtras.Musou)) return;
        rate = SmSim.Extras.Musou.MusouPowerupRules.FireRate;   // the Rapid Fire powerup (owner 2026-11-10)
        cap = SmSim.Extras.Musou.MusouPowerupRules.Cap;         // with a stim's speed never above x2 (owner 2026-11-10: "Cap at x2")
    }

    // the autocannon pass 2 (owner, 2026-11-11: "No clip in Musou mode - just infinite shooting"): Musou's autocannon spends no round, so its clip never moves and nothing reloads it.
    // Classic PvP's autocannon (ContentExtras.Autocannon, never with Musou) spends its clip of 120.
    partial void ExtrasEndlessClip(WeaponSpec weapon, ref bool endless)
    {
        if (Extras.HasFlag(ContentExtras.Musou) && AutocannonRules.EndlessClip(weapon)) endless = true;
    }

    partial void ExtrasThrownBlastScale(ThrownSpec? item, ref float scale)
    {
        if (Extras.HasFlag(ContentExtras.Musou) && item is not null && SmSim.Extras.Musou.Campaign.TfwCombatRules.IsFrag(item)) scale = MusouCombatRules.FragReachScale;
        else if (Extras.HasFlag(ContentExtras.Musou) && item is not null && item.Name.Equals(SmSim.Extras.Musou.Arsenal.ArsenalRules.RemoteDetonators, StringComparison.OrdinalIgnoreCase))
            scale = SmSim.Extras.Musou.Arsenal.DemolitionChargeRules.CentreStretch;   // a "blast reach" contract: the charge has no splash today; one added later would be scaled x1.6 on the server too   // W4: the client's one centre effect at the 16 m blast (x1.6 of the data's 10 m); the item has no splash, so the server's blasts are untouched
    }
}
