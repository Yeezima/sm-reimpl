using System;
using System.Collections.Generic;

namespace SmClient.Customizer.Model;

/// <summary>
/// Pure matching for the customizer's test aids (no Godot): which step <c>--step &lt;name&gt;</c> means, and which row of the step's list <c>--armoury-focus &lt;row&gt;</c> focuses (the item card follows the focused row).
/// Test aids, ours.
/// </summary>
public static class ArmouryFocus
{
    /// <summary>
    /// The index in <paramref name="steps"/> that <paramref name="wanted"/> names: the whole name (any case), else its first word (<c>support</c> is SUPPORT ABILITIES); -1 when none does.
    /// <paramref name="shown"/> filters out a step the page does not show.
    /// </summary>
    public static int StepIndex(IReadOnlyList<string> steps, string? wanted, Func<int, bool>? shown = null)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return -1;
        string w = wanted.Trim().Replace('-', ' ').Replace('_', ' ');
        for (int i = 0; i < steps.Count; i++) if (steps[i].Equals(w, StringComparison.OrdinalIgnoreCase) && (shown?.Invoke(i) ?? true)) return i;
        for (int i = 0; i < steps.Count; i++)
        {
            int space = steps[i].IndexOf(' ');
            if (space > 0 && steps[i][..space].Equals(w, StringComparison.OrdinalIgnoreCase) && (shown?.Invoke(i) ?? true)) return i;
        }
        return -1;
    }

    /// <summary>
    /// The row of <paramref name="titles"/> that <paramref name="wanted"/> names: the whole title (any case, hyphens and underscores read as spaces), else the first title that contains it, else a 1-based row number; -1 when none.
    /// </summary>
    public static int RowIndex(IReadOnlyList<string> titles, string? wanted)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return -1;
        string w = wanted.Trim().Replace('-', ' ').Replace('_', ' ');
        for (int i = 0; i < titles.Count; i++) if (titles[i].Equals(w, StringComparison.OrdinalIgnoreCase)) return i;
        for (int i = 0; i < titles.Count; i++) if (titles[i].Contains(w, StringComparison.OrdinalIgnoreCase)) return i;
        if (int.TryParse(w, out int n) && n >= 1 && n <= titles.Count) return n - 1;
        return -1;
    }
}
