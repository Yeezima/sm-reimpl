using System;
using System.Collections.Generic;
using SmClient.Extras.Warfront;
using SmContent;
using SmSim;
using SmContent.Extras.Musou;

namespace SmClient.Customizer;

/// <summary>
/// The Librarian's squad-class rows (slice W5c, bodies of W11's option): on a Warfront profile the Librarian's SUPPORT ABILITIES step shows the three squad-class rows under the support rows, drawn as every
/// Librarian row is (<c>MusouList</c>), and a press stores the option in the profile's own Warfront extras entry (<see cref="WarfrontSquadClassOption"/>). SAVE keeps it. On any other profile nothing changes.
/// </summary>
public sealed partial class CreatorView
{
    bool WarfrontProfile => _services.Content.HasFlag(ContentExtras.Warfront);

    partial void WarfrontLibrarianRows(int step, ref IReadOnlyList<MusouSection> sections)
    {
        if (!WarfrontProfile || step != MusouCustomizer.SupportStep) return;
        var chosen = WarfrontSquadClassOption.SquadClassOf(_session.Working);
        var all = new List<MusouSection>(sections);
        all.AddRange(WarfrontSquadClassOption.Rows(chosen));
        sections = all;
    }

    partial void WarfrontRowAction(MusouRow row, ref bool handled)
    {
        if (!WarfrontProfile || WarfrontSquadClassOption.OfAction(row.Action) is not { } squad) return;
        handled = true;
        if (squad != WarfrontSquadClassOption.SquadClassOf(_session.Working)) _session.Edit(p => WarfrontSquadClassOption.WithSquadClass(p, squad));
        Say($"{row.Title}: your whole squad. SAVE keeps it.", 4.0);
        Refresh(focusList: false);
    }

    /// <summary>
    /// The default Armoury layout's slot cards for the Librarian (2026-11-25): a RETAINERS card after SUPPORT ABILITY that says what the old layout's fixed row says (TWO RETAINERS, RANDOM CLASSES), so both layouts show the
    /// retainers. A press opens the SUPPORT step with the RETAINERS row focused, so the item card shows its text.
    /// </summary>
    partial void WarfrontArmourySlots(List<ArmSlot> slots)
    {
        if (!WarfrontProfile) return;
        slots.Add(new ArmSlot(WarfrontSquadClassOption.Heading, WarfrontSquadClassOption.RolledTitle, null, () => WarfrontOpenRetainers(late: false)));
    }

    /// <summary>The Librarian's SUPPORT step with the RETAINERS row focused (the slot card's press, and <c>--step retainers</c> with <paramref name="late"/>).</summary>
    void WarfrontOpenRetainers(bool late)
    {
        _step = (Step)MusouCustomizer.SupportStep; _armPicking = true;
        Refresh(focusList: true);
        FocusArmouryRow(WarfrontSquadClassOption.RolledTitle, late);
    }

    partial void ExtrasOpenStep(string name, ref bool handled)
    {
        if (!WarfrontProfile || !MusouLayout || !WarfrontSquadClassOption.IsRetainersStep(name)) return;
        if (_musouEntry != MusouCustomizerEntry.Librarian) ChooseMusouEntry(MusouCustomizerEntry.Librarian);   // only his page has the row
        handled = true;
        WarfrontOpenRetainers(late: true);
    }

    /// <summary>Test aid (<c>--warfront-hooks-test</c>): the Librarian's page on its SUPPORT ABILITIES step.</summary>
    internal void WarfrontTestShowLibrarianSupport()
    {
        ChooseMusouEntry(MusouCustomizerEntry.Librarian);
        _step = (Step)MusouCustomizer.SupportStep;
        Refresh(focusList: false);
    }

    /// <summary>Test aid: presses the squad-class row of <paramref name="squad"/> and answers what the working profile holds after it.</summary>
    internal string WarfrontTestPress(SmSim.Extras.Warfront.Squad.SquadClass squad)
    {
        var row = WarfrontSquadClassOption.ChoiceRows(squad)[0].Rows[(int)squad];   // the retired choices (Rows shows one fixed row since 2026-11-23); ChoiceRows is in enum order
        LibrarianChoose(row);
        return WarfrontSquadClassOption.SquadClassOf(_session.Working).ToString();
    }
}
