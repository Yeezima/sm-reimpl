using Godot;
using SmClient.Customizer;
using SmSim;

namespace SmClient.FrontEnd;

/// <summary>
/// Warfront's RESET PROGRESSION in OPTIONS, INTERFACE (owner 2026-11-25: "remove the reset progression button from the warfront menu and put it in the options, interface section. below screen flashes.").
/// The row is an option row like its neighbours (the label column, then a pill reading RESET; the same lettering and size); pressing it opens the same confirmation as the Warfront page's plate did (Yes / No,
/// No is the default) and Yes resets only this PC's Warfront progress file (<see cref="WarfrontProgressReset"/>). The row is always listed in a Warfront build and greyed, with the reason on hover, when it
/// cannot be used: no Warfront key on this PC yet (nothing to reset), a server of this game running, a progress file of a newer build or one that cannot be read, or the in-match menu's OPTIONS.
/// The outcome shows on the page's own chip (live text). Only in a build with added content and Warfront.
/// </summary>
public sealed partial class OptionsView
{
    Control? _wfResetDialog;

    /// <summary>Self-test: the reset row on the open INTERFACE tab, or null.</summary>
    internal UiKit.MenuChoice? WarfrontResetRowForTest { get; private set; }

    /// <summary>Self-test: the confirmation is up.</summary>
    internal bool WarfrontResetConfirmOpen => _wfResetDialog is { } d && GodotObject.IsInstanceValid(d) && d.IsInsideTree() && !d.IsQueuedForDeletion();

    partial void ExtrasInterfaceRows()
    {
        WarfrontResetRowForTest = null;
        if (!ContentExtras.Compiled || !ContentExtras.WarfrontCompiled) return;
        string? blocked = WarfrontProgressReset.Blocked(InMatch, WarfrontProgressReset.ServerRunning, InMatch ? null : WarfrontProgressReset.OpenStore(WarfrontProgressReset.KeyFileFromCommandLine()));
        var row = new UiKit.MenuChoice(WarfrontProgressReset.Row, [WarfrontProgressReset.Value], 0, null, blocked ?? WarfrontProgressReset.Hint) { Name = "WarfrontResetRow" };
        row.Pressed += WarfrontResetPress;   // one value: the row's own step does nothing, the press asks
        row.SetDisabled(blocked is not null);
        if (_screenFlashesRow is { } flashes) AddUnder(row, flashes); else Add(row);   // directly under SCREEN FLASHES in one column and in two
        WarfrontResetRowForTest = row;
    }

    /// <summary>Self-test: places the under-rows as for <paramref name="columns"/> columns (the one-column order cannot be had at every window size).</summary>
    internal void PlaceUnderForTest(int columns) => PlaceUnder(columns);

    /// <summary>The row's press: the confirmation card naming what is cleared (No is the default answer).</summary>
    internal void WarfrontResetPress()
    {
        if (InMatch || WarfrontResetConfirmOpen || WarfrontProgressReset.ServerRunning) return;
        if (GetParent() is not Control host) return;
        _wfResetDialog = UiKit.Dialog.Open(this, host, WarfrontProgressReset.Title, WarfrontProgressReset.Body, ["Yes", "No"], i => { if (i == 0) WarfrontResetYes(); }, cancelIndex: 1);
        WarfrontProgressReset.FocusNo(_wfResetDialog);
    }

    /// <summary>The player said Yes: the safety copy, the level-1 file with its reset time; the next connection tells the host (flag 2), which honours it in the Ship phase. Returns whether it was reset.</summary>
    internal bool WarfrontResetYes()
    {
        string? problem = WarfrontProgressReset.ResetNow(WarfrontProgressReset.KeyFileFromCommandLine());
        if (Tab == Interface) ShowTab(Interface);   // the row follows the new state
        WarfrontResetRowForTest?.FocusLater();
        ShowToast(problem is null ? WarfrontProgressReset.Done : problem.ToUpperInvariant());
        return problem is null;
    }
}
