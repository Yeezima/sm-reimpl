using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmContent.Extras.Musou.Arsenal;

/// <summary>
/// W2, the lightning claw (docs/new-weapons-design.md section 5; owner 2026-11-10: one claw on the right hand plus the pistol, "about 25% faster", no extra edge). Musou Mode only, added content:
/// an Assault Marine melee choice with no wargear file in the install (ours), the power sword's moves at <see cref="Speed"/> times the clip speed and every hit times <see cref="DamageScale"/>
/// (the same damage a second as the sword). Its combo is the sword's own combo, read from the install by the data's loader, with every step timing divided by the speed and every damage scaled, so
/// the claw reads nothing the sword does not; its map is the sword's with <see cref="MeleeTuning.WeaponMap.ClipSpeed"/> set (the client poses the clips at the same rate), and its animations are the
/// sword's through the alias. The model, the always-worn rule and the strike crackle are the client's (<c>client/Extras/Musou/Arsenal/Claw/</c>).
/// </summary>
public static partial class Arsenal
{
    /// <summary>The claw's tempo against the power sword's (owner: "about 25 % faster"; the factor ours, an estimate).</summary>
    public const float ClawSpeed = 1.25f;

    /// <summary>Every hit of the claw against the sword's (ours: 0.8 = 1 / 1.25, so the damage a second is the sword's).</summary>
    public const float ClawDamageScale = 0.8f;

    /// <summary>The sword whose combo, map and animations the claw takes (the Space Marines' wargear; the Chaos twin is not offered the claw).</summary>
    public const string ClawBase = "sm_mp_melee_powersword";

    /// <summary>The claw's English name and description in the customizer (ours; no text in the install).</summary>
    public const string ClawTitle = "Lightning Claw", ClawText = "Four crackling talons on a piston-driven gauntlet. Strikes as the power sword does, about a quarter faster, each blow a little lighter.";

    static partial void AppendLightningClaw(ref SimConfig config, string gameDir, List<string> notes)
    {
        var classes = config.Classes.ToList();
        bool changed = false;
        for (int i = 0; i < classes.Count; i++)
        {
            var c = classes[i];
            if (c.Side != Team.SpaceMarines || !string.Equals(c.Role, "assault", StringComparison.OrdinalIgnoreCase)) continue;
            if (c.MeleeChoices.Any(m => m.Wargear.Equals(ArsenalRules.LightningClaw, StringComparison.OrdinalIgnoreCase))) continue;
            var sword = c.MeleeChoices.FirstOrDefault(m => m.Wargear.Equals(ClawBase, StringComparison.OrdinalIgnoreCase));
            if (sword is null) { notes.Add($"musou arsenal: {c.Name} has no {ClawBase}; the lightning claw is not offered"); continue; }
            classes[i] = c with { MeleeChoices = [.. c.MeleeChoices, ClawCombo(sword)] };
            changed = true;
        }
        if (changed) config = config with { Classes = classes };
    }

    static partial void LightningClawMap(string wargear, ref MeleeTuning.WeaponMap? map)
    {
        if (!wargear.Equals(ArsenalRules.LightningClaw, StringComparison.OrdinalIgnoreCase)) return;
        if (MpClasses.MeleeMapFor(ClawBase) is { } sword) map = sword with { Wargear = wargear, ClipSpeed = ClawSpeed };
    }

    static partial void LightningClawAlias(ref string wargear)
    {
        if (wargear.Equals(ArsenalRules.LightningClaw, StringComparison.OrdinalIgnoreCase)) wargear = ClawBase;
    }

    static partial void LightningClawItems(ref PerkCatalog catalog)
    {
        catalog = catalog.WithClasses(c =>
        {
            if (!string.Equals(c.Role, "assault", StringComparison.OrdinalIgnoreCase) || c.Melee.Items.Any(i => i.Name.Equals(ArsenalRules.LightningClaw, StringComparison.OrdinalIgnoreCase))) return c;
            if (!c.Race.Equals("space_marine", StringComparison.OrdinalIgnoreCase)) return c;
            var sword = c.Melee.Items.FirstOrDefault(i => i.Name.Equals(ClawBase, StringComparison.OrdinalIgnoreCase));
            if (sword is null) return c;
            var claw = new LoadoutItem(ArsenalRules.LightningClaw, "melee", ClawTitle, ClawText, sword.Level, true, null, null);
            return c with { Melee = c.Melee with { Items = [.. c.Melee.Items, claw] } };
        });
    }

    /// <summary>
    /// The claw's combo from the sword's: every step's times (the attack, the hit, the windows, the Y chain, the string reset, the evade, the alignment and the steered turn) divided by
    /// <see cref="ClawSpeed"/> (a steered turn's rate times it, so it turns as far in the shorter time), every damage times <see cref="ClawDamageScale"/>; the charge likewise. The sword's perk
    /// variants are dropped (none of the sword's perks count for the claw at first). Distances, reactions and the root motion's shape stay the sword's.
    /// </summary>
    public static MeleeCombo ClawCombo(MeleeCombo sword) => sword with
    {
        Wargear = ArsenalRules.LightningClaw,
        Steps = [.. sword.Steps.Select(ClawStep)],
        Heavy = [.. sword.Heavy.Select(ClawStep)],
        Rush = sword.Rush is { } rush ? ClawRush(rush) : null,
        Variants = [],
    };

    static float T(float seconds) => seconds / ClawSpeed;

    static MeleeStep ClawStep(MeleeStep m) => m with
    {
        Duration = T(m.Duration), HitTime = T(m.HitTime), ComboOpen = T(m.ComboOpen), ComboClose = T(m.ComboClose), RangedAt = T(m.RangedAt), MoveAt = T(m.MoveAt),
        Damage = m.Damage * ClawDamageScale, SecondaryDamage = m.SecondaryDamage * ClawDamageScale,
        ShieldDamage = m.ShieldDamage * ClawDamageScale, SecondaryShieldDamage = m.SecondaryShieldDamage * ClawDamageScale,
        NoReactFrom = T(m.NoReactFrom), NoReactTo = T(m.NoReactTo),
        YComboOpen = T(m.YComboOpen), YComboClose = T(m.YComboClose),
        StringResetAt = T(m.StringResetAt), EvadeAt = T(m.EvadeAt),
        SteerFrom = T(m.SteerFrom), SteerTo = T(m.SteerTo), SteerRate = m.SteerRate * ClawSpeed,
        Stomp = m.Stomp is { } stomp ? stomp with { Damage = ClawDamage(stomp.Damage) } : null,
        Reach = m.Reach is { } reach ? reach with { AlignFrom = T(reach.AlignFrom), AlignTo = T(reach.AlignTo), DistanceFrom = T(reach.DistanceFrom), DistanceTo = T(reach.DistanceTo) } : null,
    };

    static DamageSpec ClawDamage(DamageSpec d) => d with { Damage = d.Damage * ClawDamageScale, ShieldDamage = d.ShieldDamage * ClawDamageScale };

    static RushMove ClawMove(RushMove r) => r with { ClipLength = T(r.ClipLength), End = T(r.End), MoveAt = T(r.MoveAt), EvadeAt = T(r.EvadeAt), MeleeAt = T(r.MeleeAt) };

    static RushSpec ClawRush(RushSpec r) => r with
    {
        Rush = ClawMove(r.Rush), Collision = ClawMove(r.Collision), Primary = ClawDamage(r.Primary), Secondary = ClawDamage(r.Secondary),
        ContactUntil = T(r.ContactUntil), WallFrom = T(r.WallFrom), HyperArmourUntil = T(r.HyperArmourUntil),
    };
}
