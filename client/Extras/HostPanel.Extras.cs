using Godot;

namespace SmClient.FrontEnd;

/// <summary>
/// The Host panel's hooks of added content (docs/added-content.md): the one implementation of <see cref="ExtrasHostRowShown"/>, a dispatcher. Warfront hides the Classic rows from its profile
/// (<c>WarfrontHostRowShown</c>, compiled out with <c>-p:SmWarfront=false</c>: the partial then has no implementation and the call goes); the Classic weather row (weather W3) shows only with the extended profile.
/// </summary>
public sealed partial class HostPanel
{
    partial void ExtrasHostRowShown(Control row, ref bool shown)
    {
        WarfrontHostRowShown(row, ref shown);   // WARFRONT-HOOK
        WeatherRowShown(row, ref shown);        // weather (client/Extras/Weather/HostPanel.Weather.cs)
        WarfrontFactionRowShown(row, ref shown);   // WARFRONT-HOOK: the FACTION row only on the Warfront profile (Chaos Warfront C1)
    }

    partial void WarfrontHostRowShown(Control row, ref bool shown);
    partial void WarfrontFactionRowShown(Control row, ref bool shown);

    /// <summary>The faction row (Chaos Warfront C0 seam): Warfront's alone, so the dispatcher hands it straight on; null without its implementation (client/Extras/Warfront/Chaos/).</summary>
    partial void ExtrasHostFactionRow(ref Control? row) => WarfrontHostFactionRow(ref row);   // WARFRONT-HOOK

    partial void WarfrontHostFactionRow(ref Control? row);
}
