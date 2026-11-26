using System.Collections.Generic;
using Godot;
using SmClient.Customizer;
using SmSim.Extras.Warfront.Faction;

namespace SmClient.FrontEnd;

/// <summary>
/// The Host panel's FACTION row (Chaos Warfront C1, owner brief 2026-11-24 rev 1; added content, the Warfront profile only): ALTERNATE (the default: Orks and Chaos take turns with each new campaign, Orks first), ORKS or
/// CHAOS. It sets the enemy of the hosted server's next NEW campaign only; a campaign in progress keeps its own. The choice reaches the server launch through <c>HostOptions.WarfrontFaction</c>
/// (<c>ServerLaunch.Arguments</c>: <c>--warfront-faction orks|chaos</c>); ALTERNATE sends nothing (the server's default), so the launch of every earlier build is unchanged. Wired as the weather row is
/// (HostPanel.Weather.cs). Test aid: <c>--host-warfront-faction orks|chaos|alternate</c> starts the row on that choice (not saved).
/// </summary>
public sealed partial class HostPanel
{
    UiKit.MenuChoice? _factionRow;
    WarfrontFactionSetting _faction = WarfrontFactionSetting.Alternate;

    /// <summary>The row's words, in setting order.</summary>
    public static readonly string[] FactionChoices = ["ALTERNATE", "ORKS", "CHAOS"];

    public const string FactionTooltip = "The enemy of the next new campaign. ALTERNATE (the default) switches between the Orks and Chaos with each new campaign, starting with the Orks. A campaign keeps its enemy to the end: changing this affects only the next new campaign.";

    /// <summary>The FACTION row, behind the C0 seam (<c>ExtrasHostFactionRow</c> -> <c>WarfrontHostFactionRow</c>, client/Extras/HostPanel.Extras.cs).</summary>
    partial void WarfrontHostFactionRow(ref Control? row)
    {
        if (!WarfrontFactionPolicy.TryParse(_args.GetValueOrDefault("host-warfront-faction"), out _faction)) _faction = WarfrontFactionSetting.Alternate;
        _factionRow = new UiKit.MenuChoice("Faction", FactionChoices, (int)_faction, i => { _faction = (WarfrontFactionSetting)i; RefreshNote(); }, FactionTooltip) { Name = "ChoiceWarfrontFaction" };
        row = _factionRow;
    }

    partial void ExtrasHostFaction(ref string? faction)
    {
        if (Warfront && _faction != WarfrontFactionSetting.Alternate) faction = WarfrontFactionPolicy.Key(_faction);
    }

    /// <summary>Hides the FACTION row from every profile but Warfront's (dispatched from <c>ExtrasHostRowShown</c>).</summary>
    partial void WarfrontFactionRowShown(Control row, ref bool shown)
    {
        if (row == _factionRow && !Warfront) shown = false;
    }

    /// <summary>Self-test: the row exists, applies (the Warfront profile), the setting's key and the words on it.</summary>
    public (bool Exists, bool Applies, string Key, string Value) FactionRowState => (_factionRow is not null, _factionRow is not null && Warfront, WarfrontFactionPolicy.Key(_faction), _factionRow?.Value ?? "");

    /// <summary>Self-test: chooses a setting as a click on the row would (an unknown key is ignored).</summary>
    public void ChooseFactionForTest(string key)
    {
        if (_factionRow is null || !WarfrontFactionPolicy.TryParse(key, out var s)) return;
        _factionRow.SetIndex((int)s);
        _faction = s;
        RefreshNote();
    }

    /// <summary>The <c>--warfront-faction</c> the next hosted campaign would be given (null: alternate, or a profile without Warfront); what <c>StartHosting</c> puts in <c>HostOptions.WarfrontFaction</c>.</summary>
    public string? FactionChosen { get { string? f = null; ExtrasHostFaction(ref f); return f; } }
}
