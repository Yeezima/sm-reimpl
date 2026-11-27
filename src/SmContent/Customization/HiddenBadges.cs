namespace SmContent.Customization;

/// <summary>One shoulder badge the original hides (<c>custom_paint_decal</c> with <c>visible_in_customizer</c> false) that armour Series B offers.</summary>
/// <param name="Name">The decal entry's name.</param>
/// <param name="Race">The entry's <c>race</c> (the only scoping the data has: no entry names a class).</param>
/// <param name="DisplayName">Ours, for entries whose <c>name</c> text id is 0; an entry with a locale text keeps that text.</param>
public sealed record HiddenBadgeSpec(string Name, string Race, string DisplayName);

/// <summary>
/// armour Series B (owner's pick of 2026-11-27, "Hidden shoulder badges"; ours): the badges of <c>custom_paint_decal</c> the original hides from the customizer
/// become pad choices, appended after the race's visible badges in <see cref="Offered"/>'s order (so every existing badge index of an appearance code keeps its
/// meaning; only ever append). Of the 16 hidden entries, 5 are offered and 11 left out (each viewed in a still) (<see cref="LeftOut"/>, with the reason). Behind the piece library's
/// restore switches (<see cref="CustomizationCatalog.ExtraPiecesOffered"/>): <c>--no-extra-pieces</c> restores the original badge list.
/// </summary>
public static class HiddenBadges
{
    const string Sm = CustomizationCatalog.SpaceMarine;

    /// <summary>The offered hidden badges, in the order they are appended (all Space Marine: the four Chaos entries are blanks).</summary>
    public static readonly IReadOnlyList<HiddenBadgeSpec> Offered =
    [
        new("sm_commandsquad", Sm, "Command Squad"),
        new("sm_rank_sargeredskull", Sm, "Sergeant Red Skull Rank"),
        new("sm_identifier_tactical", Sm, "Tactical Identifier"),
        new("sm_identifier_assault", Sm, "Assault Identifier"),
        new("sm_identifier_devastator", Sm, "Devastator Identifier"),
    ];

    /// <summary>The hidden entries left out, with the reason (docs/customizer-and-perks-data.md "Hidden shoulder badges").</summary>
    public static readonly IReadOnlyDictionary<string, string> LeftOut = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["csm_black_templars_orig"] = "placeholder: Chaos entry with a blank texture",
        ["csm_brazen_claws_black"] = "placeholder: Chaos entry with a blank texture",
        ["csm_crimson_fists_orig"] = "placeholder: Chaos entry with a blank texture",
        ["csm_sm_angels_of_redemption_inverted"] = "placeholder: Chaos entry with a blank texture",
        ["sm_ultramarines"] = "duplicate: the same texture as the visible sm_ultramarines_orig",
        ["ai_space_marine_shldrl_01"] = "duplicate: the same texture as the visible sm_ultramarines_orig",
        ["sm_bloodravens"] = "duplicate: the same texture as the visible sm_blood_ravens_orig",
        ["sm_darkangels"] = "duplicate: the same texture as the visible sm_dark_angels_orig",
        ["sm_darkangel3rdtactical"] = "broken: its texture package sm_darkangel_3rd_tactical_dif is not in the install (draws nothing)",
        ["sm_rank_tactical"] = "broken: its texture package sm_rank_tactical_dif is not in the install (draws nothing)",
        ["ai_space_marine_shldrr_01"] = "broken: its texture package sm_rank_tactical_white_iv_dif is not in the install (draws nothing)",
    };
}
