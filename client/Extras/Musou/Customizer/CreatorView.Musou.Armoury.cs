using System;
using System.Collections.Generic;
using System.Linq;
using SmClient.Customizer.Model;
using SmContent.Customization;
using SmContent.Extras.Musou;

namespace SmClient.Customizer;

/// <summary>
/// Musou Mode on the Armoury's concept layout (phase 4d; docs/ui-concept-textures.md): the Librarian is the first row of the class list (the concept's panel 03 shows him selected), the marines' classes follow; on his page the
/// weapon row is his melee weapon and the three tiles his powers (a press opens the WEAPONS or POWERS step); the badge says MUSOU. Classic never reaches this: <see cref="CustomizerServices.MusouLayout"/> is off there.
/// </summary>
public sealed partial class CreatorView
{
    partial void ExtrasArmouryClasses(List<ArmClass> rows)
    {
        if (!MusouLayout) return;
        bool marines = _musouEntry == MusouCustomizerEntry.StandardMarines;
        for (int i = 0; i < rows.Count; i++)
        {
            var original = rows[i]; var press = original.Press;
            rows[i] = original with { Selected = original.Selected && marines, Press = () => { if (_musouEntry != MusouCustomizerEntry.StandardMarines) { SwitchMusouEntry(MusouCustomizerEntry.StandardMarines); SyncMusouButtons(); } press(); } };
        }
        rows.Insert(0, new ArmClass("LIBRARIAN", "arm-ico-librarian", _musouEntry == MusouCustomizerEntry.Librarian, () => ChooseMusouEntry(MusouCustomizerEntry.Librarian)));
    }

    partial void ExtrasArmouryDetail(ref ArmDetail? detail)
    {
        if (!MusouLayout || _musouEntry != MusouCustomizerEntry.Librarian) return;
        var picks = LibrarianPicks(_session.Working);
        string weapon = ((picks is null ? null : LibrarianData.MeleeTitle(picks.Chosen.Melee)) ?? LibrarianData.Melee ?? "NO MELEE").ToUpperInvariant();
        string? WeaponIcon(string w) => w.Contains("SWORD", StringComparison.Ordinal) ? "arm-wico-sword" : null;
        void Go(int step) { _step = (Step)step; Refresh(focusList: true); }
        var tiles = new List<ArmTile>();
        foreach (var p in LibrarianPowers.Present().Take(3))   // his powers, then the stress scale
        {
            string name = p.Title; string lower = name.ToLowerInvariant();
            string? icon = lower.Contains("lightning") ? "arm-tico-lightning" : lower.Contains("warp") ? "arm-tico-warp" : lower.Contains("shock") ? "arm-tico-shock" : lower.Contains("stress") ? "arm-ico-stress" : null;
            tiles.Add(new ArmTile(name, icon, _step == (Step)2 && tiles.Count == 0, () => Go(2)));
        }
        void Pick(int step) { _step = (Step)step; _armPicking = true; Refresh(focusList: true); }
        var slots = new List<ArmSlot>
        {
            new("MELEE WEAPON", weapon, weapon.Contains("SWORD", StringComparison.Ordinal) ? "arm-ico-sword" : null, () => Pick(1)),
            new("PSYCHIC POWERS", string.Join(", ", tiles.Select(t => t.Label.Split(" (")[0])), "arm-ico-lightning", () => Pick(2)),
            new("PERKS", "KEPT FROM YOUR CLASS", null, () => Pick(3)),
            new("LOOK", "YOUR ACTIVE LOADOUT", null, () => Pick(4)),
            new("SUPPORT ABILITY", "ARTILLERY OR DROP POD", null, () => Pick(5)),
        };
        detail = new ArmDetail(weapon, WeaponIcon(weapon), () => Go(1), tiles, slots);
    }

    partial void ExtrasArmouryBadge(ref string text)
    {
        if (MusouLayout) text = "WARFRONT";   // the Musou layout is Warfront's since 2026-11-19 (Musou Mode has no menu)
    }
}
