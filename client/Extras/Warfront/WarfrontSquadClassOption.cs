using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using SmContent.Customization;
using SmContent.Extras.Musou;
using SmSim.Extras.Warfront.Squad;

namespace SmClient.Extras.Warfront;

/// <summary>
/// The Librarian's squad-class option for Warfront (slice W11; owner, 2026-11-17: "a loadout customizer option, like the support slot, that sets the whole squad to Assault/Raptor, Tactical, or
/// Devastator/Havoc"). It is stored as the support slot is stored: a name in the profile's own extras entry (<see cref="ExtrasKey"/>, field <c>squadClass</c>), the default (Tactical) removing it; the rows
/// are the same <see cref="MusouSection"/> rows the support step shows (a <see cref="MusouRowKind.Choice"/> row whose action is <c>squad:&lt;name&gt;</c>), so the customizer lists them the way it lists
/// the support abilities. Pure model: no Godot. The customizer's own files are not touched here; the line that shows these rows is the hook listed in the W11 report.
/// </summary>
public static class WarfrontSquadClassOption
{
    /// <summary>The profile extras entry of Warfront's own data (<see cref="CustomizationProfile.Extras"/>): <c>{ "squadClass": "assault" }</c>.</summary>
    public const string ExtrasKey = "warfront";

    /// <summary>The action prefix of a row.</summary>
    public const string ActionPrefix = "squad:";

    /// <summary>The class a Librarian's squad has when nothing is stored.</summary>
    public const SquadClass Default = SquadClass.Tactical;

    /// <summary>The names stored.</summary>
    public static string NameOf(SquadClass c) => c switch { SquadClass.Assault => "assault", SquadClass.Devastator => "devastator", _ => "tactical" };

    /// <summary>The class a stored name means; null for an unknown one.</summary>
    public static SquadClass? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "assault" or "raptor" => SquadClass.Assault, "tactical" => SquadClass.Tactical, "devastator" or "havoc" => SquadClass.Devastator, _ => null,
    };

    /// <summary>The squad class stored in <paramref name="profile"/>: <see cref="Default"/> when nothing, or nothing readable, is stored.</summary>
    public static SquadClass SquadClassOf(CustomizationProfile profile)
    {
        if (!profile.Extras.TryGetValue(ExtrasKey, out var text)) return Default;
        try
        {
            if (JsonNode.Parse(text) is JsonObject root && root["squadClass"] is JsonValue v && v.TryGetValue<string>(out var name)) return Parse(name) ?? Default;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException or ArgumentException) { }
        return Default;
    }

    /// <summary>The profile with the squad class <paramref name="squad"/> (the default removes the entry; everything else in the Warfront extras entry is kept).</summary>
    public static CustomizationProfile WithSquadClass(CustomizationProfile profile, SquadClass squad)
    {
        JsonObject root;
        try
        {
            root = profile.Extras.TryGetValue(ExtrasKey, out var text) && JsonNode.Parse(text) is JsonObject o ? o : new JsonObject();
            _ = root.Count;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException) { root = new JsonObject(); }
        root.Remove("squadClass");
        if (squad != Default) root["squadClass"] = NameOf(squad);
        string? json = root.Count == 0 ? null : root.ToJsonString();
        if (profile.Extras.TryGetValue(ExtrasKey, out var old) ? old == json : json is null) return profile;
        return profile.WithExtra(ExtrasKey, json);
    }

    /// <summary>The squad class a row's action names (<c>squad:assault</c>); null for any other action.</summary>
    public static SquadClass? OfAction(string? action) => action is not null && action.StartsWith(ActionPrefix, StringComparison.Ordinal) ? Parse(action[ActionPrefix.Length..]) : null;

    /// <summary>The Librarian's squad rows, <paramref name="chosen"/> marked: one section, three choices, whole squad (no mixed squads).</summary>
    public static IReadOnlyList<MusouSection> Rows(SquadClass chosen) =>
    [
        new("SQUAD CLASS  (your whole squad)",
        [
            Row(SquadClass.Assault, "Assault / Raptor", "Every retainer is an Assault Marine (a Raptor for Chaos) with a jump pack: they follow by jump and ground-pound beside you. A power sword, chainsword or hammer with a pistol, as the bot loadouts have them.", chosen),
            Row(SquadClass.Tactical, "Tactical", "Every retainer is a Tactical Marine with a bolter, plasma gun, storm bolter or sniper rifle and the class's perks and grenades.", chosen),
            Row(SquadClass.Devastator, "Devastator / Havoc", "Every retainer is a Devastator (a Havoc for Chaos) with a lascannon or plasma cannon: slow, heavy fire from a distance.", chosen),
        ]),
    ];

    static MusouRow Row(SquadClass c, string title, string text, SquadClass chosen) =>
        new(title, text + " Each Marine rolls its own valid loadout, kept while this choice stands, in your colours.", MusouRowKind.Choice, ActionPrefix + NameOf(c), c == chosen);
}
