using Godot;
using SmClient.Customizer;
using SmContent.Extras.Warfront;
using SmSim.Extras.Warfront.Faction;

namespace SmClient.FrontEnd;

/// <summary>
/// The local WARFRONT page's FACTION plate (Chaos Warfront C3; owner 2026-11-25: "Add a fraction row"): one plate of the page's column, in the page's own plate style (no painted label, no second
/// text style), that steps ALTERNATE -> ORKS -> CHAOS on each press. It sets the enemy of the next new campaign this PC's supervisor builds (<c>--warfront-faction</c> through
/// <see cref="WarfrontLocal.Options"/> and <c>ExtraArgs</c>; ALTERNATE sends nothing, the server's default); a campaign in progress keeps its own army. Shown while no server runs. Kept for the
/// session (not saved), as the Host panel's row. Test aid: <c>--warfront-faction-plate alternate|orks|chaos</c> starts it on that choice.
/// </summary>
public sealed partial class FrontEndView
{
    static WarfrontFactionSetting? s_wfLocalFaction;

    /// <summary>The setting the local page passes to the next supervisor it starts.</summary>
    WarfrontFactionSetting WarfrontLocalFaction
    {
        get
        {
            if (s_wfLocalFaction is { } known) return known;
            s_wfLocalFaction = WarfrontFactionPolicy.TryParse((_args.TryGetValue("warfront-faction-plate", out var arg) ? arg : null), out var s) ? s : WarfrontFactionSetting.Alternate;
            return s_wfLocalFaction.Value;
        }
        set => s_wfLocalFaction = value;
    }

    /// <summary>The plate, with the current choice in its text.</summary>
    Button WarfrontFactionPlate()
    {
        Button? plate = null;
        plate = UiKit.MenuButton(WarfrontLocal.FactionPlateTexts[(int)WarfrontLocalFaction], () =>
        {
            WarfrontLocalFaction = (WarfrontFactionSetting)(((int)WarfrontLocalFaction + 1) % WarfrontLocal.FactionPlateTexts.Length);
            plate!.Text = WarfrontLocal.FactionPlateTexts[(int)WarfrontLocalFaction];
            GD.Print($"warfront: the local page's faction is {WarfrontFactionPolicy.Key(WarfrontLocalFaction)} (the next new campaign)");
        });
        plate.Name = "WarfrontFactionPlate";
        UiTooltip.Attach(plate, WarfrontLocal.FactionPlateTooltip);
        return plate;
    }
}
