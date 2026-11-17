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
        var row = WarfrontSquadClassOption.Rows(squad)[0].Rows[(int)squad == 0 ? 0 : (int)squad];
        LibrarianChoose(row);
        return WarfrontSquadClassOption.SquadClassOf(_session.Working).ToString();
    }
}
