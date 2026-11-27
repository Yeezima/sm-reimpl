namespace SmContent.Customization;

/// <summary>
/// One further armour piece the customizer offers beyond the armour sets (armour-pieces-1125 Series A, owner's pick of 2026-11-25, ours): either a wargear file
/// of the install by name (<see cref="Packages"/> null: slot, packages, text and helmet flag come from the file, as for a set's piece) or, for the campaign
/// armour whose wargear files all share generic names ("arm_left", "torso", ...), the visual packages themselves (<see cref="Name"/> is then the first
/// package's name, unique in the install, and the text is ours).
/// </summary>
/// <param name="Group">The owner's group: "mp" (multiplayer and co-op extras), "librarian", "campaign", "barehead" (armour Series B).</param>
/// <param name="SetLabel">For package pieces: the display name of the campaign armour it belongs to (ours) and its <see cref="ArmourPiece.SetNames"/> entry.</param>
/// <param name="Hidden">Round 5 (owner, 2026-11-26, card "Berserker": "Hide the 2 pieces"): the piece keeps its place in the slot's list (a reserved entry, so
/// later appearance-code indices keep their meaning) but is never offered, as a piece the install lacks.</param>
public sealed record ExtraPieceSpec(string Group, string Race, string Slot, string Name, IReadOnlyList<string>? Packages = null, string? SetLabel = null,
    string HelmetOverride = "no_override", bool Hidden = false);

/// <summary>
/// The pieces armour-pieces-1125 Series A adds to the per-slot libraries, in the order they are appended (after the customizable sets' pieces and the
/// predefined sets' pieces; a slot's list keeps the table's order, so a later addition can only be appended and existing appearance-code indices keep their
/// meaning). Owner's pick (agent-coordination decisions/2026-11-25-dlc-armour-pieces.md): (1) the Blood Ravens DLC arms, the Chosen raptor head and legs for
/// every Chaos class, the co-op elite Khorne Berserker helmet and backpack; (2) the Librarian's torso and belt, right pad and backpack in every mode
/// (cosmetic; Musou's Librarian is unchanged); (3) the campaign Space Marine and Chaos armour (singleplayer-content dependency, docs/discovery.md
/// "Campaign armour as customizer pieces").
/// </summary>
public static class ExtraArmourPieces
{
    const string Sm = CustomizationCatalog.SpaceMarine, Csm = CustomizationCatalog.Chaos;

    static readonly (string Slot, string Suffix)[] Parts =
    [
        ("head", "helmet"), ("torso", "torso"), ("leftshoulder", "shldrl"), ("rightshoulder", "shldrr"),
        ("leftarm", "arml"), ("rightarm", "armr"), ("leftleg", "legl"), ("rightleg", "legr"), ("backpack", "backpack"),
    ];

    /// <summary>
    /// Campaign packages left out: Titus's shoulder pads (<c>sm_armour_basic_upres_shldrl/_shldrr</c>) use a static mesh block layout our mesh reader does
    /// not parse (stride 19 and 100 bytes, docs/discovery.md "Campaign armour as customizer pieces"); offering them would draw an empty slot. (Sidonus has no
    /// helmet package in the install; the package check leaves it out.)
    /// </summary>
    static readonly HashSet<string> Unreadable = new(StringComparer.OrdinalIgnoreCase) { "sm_armour_basic_upres_shldrl", "sm_armour_basic_upres_shldrr" };

    static IEnumerable<ExtraPieceSpec> Campaign(string race, string stem, string label, string headOverride)
    {
        foreach (var (slot, suffix) in Parts)
        {
            string package = $"{stem}_{suffix}";
            if (Unreadable.Contains(package)) continue;
            yield return new ExtraPieceSpec("campaign", race, slot, package, [package], label, slot == "head" ? headOverride : "no_override");
        }
    }

    /// <summary>
    /// armour Series B: the bare campaign heads offered, in table order (package, label). The <c>ingame_*</c> packages, not their higher-detail
    /// <c>cinematic_*</c> twins (those warn about missing face bones on the MP rig).
    /// Declared before <see cref="All"/>: static fields initialise in text order.
    /// </summary>
    public static readonly IReadOnlyList<(string Package, string Label)> BareHeads =
        [("ingame_titus", "Titus"), ("ingame_leandros", "Leandros"), ("ingame_sidonus", "Sidonus")];

    /// <summary>armour Series B: bare heads looked at and left out, with the reason.</summary>
    public static readonly IReadOnlyDictionary<string, string> BareHeadsLeftOut = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ingame_drogan"] = "does not look right on a marine: the human-scale head sinks into the collar (and its hair cards, like Titus's, need the cut-out below)",
    };

    /// <summary>
    /// Whether a package is one of the bare heads (offered or left out): the client draws their hair (shader <c>hair_test2</c>, whose silhouette is in the diffuse
    /// alpha) as a two-sided alpha cut-out (review finding 1; provisional), and no other piece.
    /// </summary>
    public static bool IsBareHead(string package) =>
        BareHeads.Any(b => b.Package.Equals(package, StringComparison.OrdinalIgnoreCase)) || BareHeadsLeftOut.ContainsKey(package.ToLowerInvariant());

    /// <summary>Every Series A and Series B piece in table order (the per-slot order is this order filtered by slot).</summary>
    public static readonly IReadOnlyList<ExtraPieceSpec> All = BuildAll();

    static List<ExtraPieceSpec> BuildAll()
    {
        var list = new List<ExtraPieceSpec>
        {
            // (1) Multiplayer and co-op extras (wargear files of the install).
            new("mp", Sm, "leftarm", "sm_dlc_bloodravens_arm_left"),
            new("mp", Sm, "rightarm", "sm_dlc_bloodravens_arm_right"),
            new("mp", Csm, "head", "csm_armour_mp_raptor_01_head"),
            new("mp", Csm, "leftleg", "csm_armour_mp_raptor_01_leg_left"),
            new("mp", Csm, "rightleg", "csm_armour_mp_raptor_01_leg_right"),
            // The co-op elite Berserker's helmet and backpack: hidden since round 5 (owner, 2026-11-26: unfinished developer art, block-out meshes on a misnamed
            // basic-helmet diffuse and a flat grey; out of the piece library and the 92 % gate). The rest of that armour is unaffected (it has no full-set quick pick).
            new("mp", Csm, "head", "csm_berserker_helmet", Hidden: true),
            new("mp", Csm, "backpack", "csm_berserker_backpack", Hidden: true),
            // (2) The Librarian's pieces (the very ones Musou's LibrarianLook puts on).
            new("librarian", Sm, "torso", "sm_librarian_torso"),
            new("librarian", Sm, "rightshoulder", "sm_librarian_shoulder_right"),
            new("librarian", Sm, "backpack", "sm_librarian_backpack"),
        };
        // (3) Campaign armour by package (the owner's list, in its order).
        list.AddRange(Campaign(Sm, "sm_armour_basic_upres", "Titus", "helmet"));
        list.AddRange(Campaign(Sm, "sm_armour_sidonus", "Sidonus", "helmet"));
        list.AddRange(Campaign(Sm, "sm_armour_leandros", "Leandros", "helmet"));
        list.AddRange(Campaign(Sm, "sm_armour_dark_angels", "Dark Angels (campaign)", "helmet"));
        list.AddRange(Campaign(Sm, "sm_armour_black_temp", "Black Templars (campaign)", "helmet"));
        list.AddRange(Campaign(Sm, "sm_armour_blood_ravens", "Blood Ravens (campaign)", "helmet"));
        list.Add(new("campaign", Sm, "head", "sm_armour_corvus_helmet", ["sm_armour_corvus_helmet"], "Corvus", "helmet"));
        list.Add(new("campaign", Sm, "rightshoulder", "sm_armour_corvus_shldrr", ["sm_armour_corvus_shldrr"], "Corvus"));
        list.AddRange(Campaign(Sm, "sm_armour_basic_001", "Ultramarines (campaign)", "helmet"));
        list.AddRange(Campaign(Csm, "csm_armour_basic", "Chaos Space Marine (campaign)", "no_override"));
        list.AddRange(Campaign(Csm, "csm_armour_berserker", "Khorne Berserker (campaign)", "no_override"));
        list.AddRange(Campaign(Csm, "csm_armour_havoc", "Havoc (campaign)", "no_override"));
        list.AddRange(Campaign(Csm, "csm_armour_raptor", "Raptor (campaign)", "no_override"));
        // (4) armour Series B (owner's pick of 2026-11-27, "Bare campaign heads"; ours): the campaign heroes' bare heads as helmetless head picks for the
        // Space Marine classes (singleplayer-content dependency, docs/discovery.md "Bare campaign heads and hidden shoulder badges"). Skin and hair: no paint mask, none built. The MP rig
        // has no face bones, so the face holds its bind pose (static).
        foreach (var (package, label) in BareHeads) list.Add(new("barehead", Sm, "head", package, [package], label, "no_helmet"));
        return list;
    }

    /// <summary>The wargear files the table names (the catalogue reads them with the sets' items).</summary>
    public static IEnumerable<string> WargearNames => All.Where(s => s.Packages is null).Select(s => s.Name);

    /// <summary>Display names of the wargear pieces whose own text is a developer label or the whole set's name (ours).</summary>
    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["sm_dlc_bloodravens_arm_left"] = "Blood Ravens Left Arm",
        ["sm_dlc_bloodravens_arm_right"] = "Blood Ravens Right Arm",
        ["csm_armour_mp_raptor_01_head"] = "Chosen Raptor Helmet",
        ["csm_armour_mp_raptor_01_leg_left"] = "Chosen Raptor Left Leg",
        ["csm_armour_mp_raptor_01_leg_right"] = "Chosen Raptor Right Leg",
        ["csm_berserker_helmet"] = "Khorne Berserker Elite Helmet",
        ["csm_berserker_backpack"] = "Khorne Berserker Elite Backpack",
        ["sm_librarian_torso"] = "Librarian Torso",
        ["sm_librarian_shoulder_right"] = "Librarian Right Shoulder",
        ["sm_librarian_backpack"] = "Librarian Psychic Hood",
    };

    static readonly IReadOnlyDictionary<string, string> PartNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["head"] = "Helmet", ["torso"] = "Torso", ["leftshoulder"] = "Left Shoulder", ["rightshoulder"] = "Right Shoulder", ["leftarm"] = "Left Arm",
        ["rightarm"] = "Right Arm", ["leftleg"] = "Left Leg", ["rightleg"] = "Right Leg", ["backpack"] = "Backpack",
    };

    /// <summary>The display name of a package piece (ours): its campaign armour's label and the slot's part.</summary>
    public static string DisplayName(ExtraPieceSpec spec) =>
        spec.Group == "barehead" ? $"{spec.SetLabel} (Bare Head)" : $"{spec.SetLabel} {PartNames.GetValueOrDefault(spec.Slot, spec.Slot)}";

    /// <summary>
    /// The pieces whose textures carry no paint mask and so get one built (<see cref="BakedArmourPaint"/>): every Series A piece except the Librarian's and the
    /// Chosen raptor's (their materials have the game's masks; leaving them unregistered keeps their load, and Musou's Librarian, exactly as before).
    /// </summary>
    /// <remarks>armour Series B: the bare heads neither (skin and hair, no paint).</remarks>
    public static bool NeedsBuiltMasks(ExtraPieceSpec spec) => spec.Group is not ("librarian" or "barehead") && !spec.Name.StartsWith("csm_armour_mp_raptor_01_", StringComparison.Ordinal);
}
