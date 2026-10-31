using SmFormats.Baf;
using SmFormats.Locale;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>
/// A perk as the creator offers it (a <c>mp_perk_*</c> wargear file, slot "perk").
/// </summary>
/// <param name="Name">The wargear file name, e.g. "mp_perk_weap_bolter_accuracy" (the entries of <see cref="MpClass.Perks"/>).</param>
/// <param name="UpgradeString">The string the perk applies to its owner (<c>action_data/lifetime_actions/commands/apply_upgrade_string/strings/entry</c>);
/// null when it applies none (e.g. Weapon Versatility). What the simulation checks for.</param>
/// <param name="Kind">Weapon when the name starts with "mp_perk_weap_", else Class (interpretation).</param>
/// <param name="Level">The file's <c>unlock_info/level_requirement/level_required</c> (weapon perks are 1 there: they unlock by a challenge, <paramref name="ChallengeText"/>).</param>
/// <param name="ChallengeText">"Group: description" of the weapon-mastery challenge that unlocks the perk (e.g. "Bolter: Kill 50 enemies"), or null.</param>
/// <param name="DisplayName">The English display name; the file name when the text is missing.</param>
/// <param name="Description">The English description; empty when missing.</param>
public sealed record PerkInfo(string Name, string? UpgradeString, PerkKind Kind, int Level, string? ChallengeText, string DisplayName, string Description)
{
    /// <summary>The visual packages of the perk's own <c>visual_data/attachments</c> (Iron Halo: <c>sm_acc_iron_halo</c>); empty for a perk that shows nothing on the body.</summary>
    public IReadOnlyList<string> Packages { get; init; } = [];
}

/// <summary>
/// One wargear choice (weapon or equipment) of a class.
/// </summary>
/// <param name="Name">The wargear file name, e.g. "sm_mp_ranged_bolter" (Chaos: "csm_mp_...").</param>
/// <param name="Slot">The data's slot: "ranged" (primary and heavy weapons), "pistol", "melee" or "equipment".</param>
/// <param name="Level">The file's unlock level (player level).</param>
/// <param name="Simulated">False for items the simulation drops (equipment without a thrown delivery or stim effect, melee weapons without a combo): the UI shows them
/// unavailable and the client sends the default instead.</param>
/// <param name="Note">Why it is not simulated; null otherwise.</param>
/// <param name="RequiredPerk">A perk the item needs to be offered (the pistol-slot Bolters need True Grit: **interpretation**, the data only implies it); null otherwise.</param>
public sealed record LoadoutItem(string Name, string Slot, string DisplayName, string Description, int Level, bool Simulated, string? Note, string? RequiredPerk);

/// <summary>
/// The items of one slot of a class, in the data's order (the class's <c>base_wargear</c> first, then its <c>startup_wargear</c> that the customizer shows).
/// </summary>
/// <param name="Selectable">The player chooses (the class lists startup items of this slot); false when the class carries one fixed item (the Tactical
/// Marine's bolt pistol and knife) or none (the Assault Marine's primary).</param>
public sealed record SlotChoices(string Slot, IReadOnlyList<LoadoutItem> Items, bool Selectable)
{
    /// <summary>The default: the first simulated item, else the first item, else null.</summary>
    public LoadoutItem? Default => Items.FirstOrDefault(i => i.Simulated) ?? Items.FirstOrDefault();
}

/// <summary>What the creator offers for one class (<see cref="MpClass"/> key e.g. "sm - tactical").</summary>
/// <param name="Perks">The class's selectable perks in the data's order (<see cref="MpClass.Perks"/>).</param>
/// <param name="DefaultPerk">The class's <see cref="MpClass.DefaultPerk"/>, or null.</param>
public sealed record ClassOptions(string Key, string Role, string Race, string DisplayName, string Description,
    SlotChoices Primary, SlotChoices Pistol, SlotChoices Melee, SlotChoices Equipment,
    IReadOnlyList<PerkInfo> Perks, PerkInfo? DefaultPerk)
{
    public SlotChoices For(string slot) => slot switch
    {
        "ranged" or "primary" => Primary,
        "pistol" => Pistol,
        "melee" => Melee,
        "equipment" => Equipment,
        _ => throw new ArgumentException($"Unknown slot '{slot}'.", nameof(slot)),
    };
}

/// <summary>
/// The weapon-mastery and armour challenges of <c>global</c> (<c>multiplayer_settings/challenge_groups</c>) by the wargear file each one unlocks,
/// as text: "Group: description" with the target filled in (e.g. "Bolter: Kill 50 enemies"). Needs the locale; without it there is no text.
/// </summary>
public sealed class UnlockChallenges
{
    readonly Dictionary<string, string> _text = new(StringComparer.OrdinalIgnoreCase);
    public static readonly UnlockChallenges None = new();

    /// <summary>The challenge text for a wargear file name, or null.</summary>
    public string? For(string wargearName) => _text.TryGetValue(wargearName, out var t) ? t : null;
    public int Count => _text.Count;

    public static UnlockChallenges Read(BafDocument global, LocaleTable? locale)
    {
        var result = new UnlockChallenges();
        if (locale is null || global.Root.Table("extensions/multiplayer_settings/challenge_groups") is not { } groups) return result;
        foreach (var g in groups.Entries.Where(e => e.Type == BafType.Table))
        {
            var group = g.AsTable();
            string? groupName = CatalogData.Text(locale, CatalogData.IntOf(group, "display_name"));
            if (group.Table("challenge_sets") is not { } sets) continue;
            foreach (var s in sets.Entries.Where(e => e.Type == BafType.Table))
                if (s.AsTable().Table("challenges") is { } challenges)
                    foreach (var c in challenges.Entries.Where(e => e.Type == BafType.Table))
                    {
                        var challenge = c.AsTable();
                        if (challenge.Table("wargear") is not { } unlocks) continue;
                        string? description = CatalogData.Text(locale, CatalogData.IntOf(challenge, "display_description"));
                        if (description is null) continue;
                        description = description.Replace("%1TARGET%", CatalogData.IntOf(challenge, "target").ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
                        string text = groupName is null ? description : $"{groupName}: {description}";
                        foreach (var w in unlocks.Entries.Where(e => e.Type == BafType.String))
                            result._text.TryAdd(CatalogData.Leaf(w.AsString()), text);
                    }
        }
        return result;
    }
}

/// <summary>
/// The creator's lists per class: weapons, equipment and perks, with display text from the locale (**Verified-local** sources: <c>global</c>
/// classes, the wargear files they name). Engine-free; read once. The simulation owner defines its own spec types from this: <see cref="PerkInfo"/> is
/// the stable interface (names, upgrade strings, kinds, levels).
/// </summary>
public sealed class PerkCatalog
{
    /// <summary>
    /// The melee weapons the simulation has combos for today (wargear name suffixes; the one list, <see cref="MpClasses.SimulatedMeleeSuffixes"/>).
    /// Other melee weapons are listed but flagged not simulated.
    /// </summary>
    public static IReadOnlyList<string> SimulatedMeleeSuffixes => MpClasses.SimulatedMeleeSuffixes;

    /// <summary>The perk the pistol-slot Bolters need (**interpretation**: "True Grit" replaces the bolt pistol with a one-handed Bolter; the simulation's <see cref="Loadouts.TrueGritPerk"/>).</summary>
    public const string BolterPistolPerk = Loadouts.TrueGritPerk;

    PerkCatalog(IReadOnlyList<ClassOptions> classes, IReadOnlyList<PerkInfo> perks, IReadOnlyList<string> notes)
    {
        Classes = classes; All = perks; Notes = notes;
        _perks = perks.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    readonly Dictionary<string, PerkInfo> _perks;

    /// <summary>The classes in the data's order (assault, tactical, devastator, raptor, chaos tactical, havoc).</summary>
    public IReadOnlyList<ClassOptions> Classes { get; }
    /// <summary>Every perk some class lists, ordinal by name.</summary>
    public IReadOnlyList<PerkInfo> All { get; }
    public IReadOnlyList<string> Notes { get; }

    public ClassOptions? Class(string key) => Classes.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    /// <summary>The class of a role ("tactical", "heavy", "assault") on a race ("space_marine", "chaos"), or null.</summary>
    public ClassOptions? ForRole(string role, string race) =>
        Classes.FirstOrDefault(c => c.Role.Equals(role, StringComparison.OrdinalIgnoreCase) && c.Race.Equals(race, StringComparison.OrdinalIgnoreCase));
    public PerkInfo? Perk(string name) => _perks.GetValueOrDefault(name);

    /// <summary>
    /// The item of <paramref name="options"/> (one side's class) that a custom class's stored name <paramref name="storedName"/> means for <paramref name="slot"/>, or null
    /// (the class default). A custom class serves both sides and stores one side's name without its prefix ("mp_melee_power_axe", "mp_melee_chain_axe"): the name is found in
    /// this side's list first, else the item at the same position (over the simulated items, as the simulation's lists) of the other side's class of the same role is taken (**interpretation**: Chaos has its own melee and
    /// heavy weapon names; <c>ProfileWire.ToLoadout</c> applies the same rule to the simulation's lists, so what the creator shows is what the join sends).
    /// </summary>
    public LoadoutItem? Resolve(ClassOptions options, string slot, string? storedName)
    {
        if (storedName is null) return null;
        string wanted = LoadoutIndices.Neutral(storedName);
        bool Matches(LoadoutItem i) => LoadoutIndices.Neutral(i.Name).Equals(wanted, StringComparison.OrdinalIgnoreCase);
        var items = options.For(slot).Items;
        if (items.FirstOrDefault(Matches) is { } found) return found;
        if (Classes.FirstOrDefault(c => c.Role.Equals(options.Role, StringComparison.OrdinalIgnoreCase) && !c.Race.Equals(options.Race, StringComparison.OrdinalIgnoreCase)) is not { } other) return null;
        // Positions count over the simulated items only: they are the simulation's lists (items the simulation drops), which ProfileWire.ToLoadout counts its fallback over.
        var own = items.Where(i => i.Simulated).ToList();
        var theirs = other.For(slot).Items.Where(i => i.Simulated).ToList();
        for (int i = 0; i < theirs.Count && i < own.Count; i++) if (Matches(theirs[i])) return own[i];
        return null;
    }

    /// <summary>
    /// The item a custom class's stored name means on any side of <paramref name="role"/>: the item itself and whether the role offers a choice for the slot (a class
    /// of either side lists startup items of it). Null when no class of the role lists the name.
    /// </summary>
    public (LoadoutItem Item, bool Selectable)? Offered(string role, string slot, string neutralName)
    {
        var classes = Classes.Where(c => c.Role.Equals(role, StringComparison.OrdinalIgnoreCase)).ToList();
        var item = classes.SelectMany(c => c.For(slot).Items).FirstOrDefault(i => LoadoutIndices.Neutral(i.Name).Equals(neutralName, StringComparison.OrdinalIgnoreCase));
        return item is null ? null : (item, classes.Any(c => c.For(slot).Selectable));
    }

    /// <summary>Reads <c>global</c>, the wargear files the classes name and the locale. Never throws for a missing locale or wargear file (notes); throws when <c>global</c> is unreadable.</summary>
    public static PerkCatalog Load(string gameDir, List<string>? notes = null, string language = "english")
    {
        notes ??= [];
        var global = CatalogData.ReadGlobal(gameDir);
        var classes = MpClasses.ReadClasses(global);
        var names = classes.SelectMany(c => c.BaseWargear.Concat(c.StartupWargear).Concat(c.Perks)).Concat(classes.Select(c => c.DefaultPerk).OfType<string>().Where(d => d.Length > 0)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var docs = CatalogData.ReadWargear(gameDir, names, notes);
        var locale = CatalogData.ReadLocale(gameDir, language, notes);
        return Build(global, docs.GetValueOrDefault, locale, notes);
    }

    static IReadOnlyList<string> PackagesOf(BafDocument doc) => doc.Root.Table("visual_data/attachments") is { } attachments
        ? attachments.Entries.Where(e => e.Type == BafType.Table).Select(e => e.AsTable().String("visual_package")).OfType<string>().Where(p => p.Length > 0).ToList()
        : [];

    /// <summary>The catalogue from already-read data (testable without an install). <paramref name="wargear"/> returns a wargear file by name or null.</summary>
    public static PerkCatalog Build(BafDocument global, Func<string, BafDocument?> wargear, LocaleTable? locale, List<string> notes)
    {
        var classes = MpClasses.ReadClasses(global);
        var challenges = UnlockChallenges.Read(global, locale);
        var classTable = global.Root.Table("extensions/multiplayer_settings/ingame_progression/classes");
        var perks = new Dictionary<string, PerkInfo>(StringComparer.OrdinalIgnoreCase);
        PerkInfo Perk(string name)
        {
            if (perks.TryGetValue(name, out var known)) return known;
            if (wargear(name) is not { } doc)
            {
                notes.Add($"perk {name}: wargear file not found; listed with its file name");
                return perks[name] = new PerkInfo(name, null, MpClasses.KindOf(name), MpClasses.MissingLevel, null, name, "");
            }
            var meta = CatalogData.Meta(doc);
            var spec = MpClasses.ReadPerk(name, doc);   // the one perk reader (the simulation's too)
            return perks[name] = new PerkInfo(name, spec.UpgradeString, spec.Kind, spec.Level, challenges.For(name),
                CatalogData.Text(locale, meta.NameId) ?? name, CatalogData.Text(locale, meta.DescriptionId) ?? "") { Packages = PackagesOf(doc) };
        }
        var options = new List<ClassOptions>();
        foreach (var c in classes)
        {
            foreach (var missing in c.StartupWargear.Where(n => wargear(n) is null).Distinct(StringComparer.OrdinalIgnoreCase))
                notes.Add($"{c.Key}: wargear file {missing} not found; not offered");
            var primary = Slot(c, "ranged", wargear, locale, notes);
            var pistol = Slot(c, "pistol", wargear, locale, notes);
            var melee = Slot(c, "melee", wargear, locale, notes);
            var equipment = Slot(c, "equipment", wargear, locale, notes);
            if (c.Perks.Contains(BolterPistolPerk, StringComparer.OrdinalIgnoreCase))
                pistol = pistol with { Items = pistol.Items.Select(i => Loadouts.IsPistolBolterName(i.Name) ? i with { RequiredPerk = BolterPistolPerk } : i).ToList() };
            var classPerks = c.Perks.Distinct(StringComparer.OrdinalIgnoreCase).Select(Perk).ToList();
            var defaultPerk = c.DefaultPerk is { Length: > 0 } d ? Perk(d) : null;
            var table = classTable?.Table(c.Key);
            options.Add(new ClassOptions(c.Key, c.Role, c.Race,
                CatalogData.Text(locale, table is { } t ? CatalogData.IntOf(t, "display_name") : 0) ?? c.Key,
                CatalogData.Text(locale, table is { } t2 ? CatalogData.IntOf(t2, "display_description") : 0) ?? "",
                primary, pistol, melee, equipment, classPerks, defaultPerk));
        }
        return new PerkCatalog(options, perks.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList(), notes);
    }

    static SlotChoices Slot(MpClass c, string slot, Func<string, BafDocument?> wargear, LocaleTable? locale, List<string> notes)
    {
        var items = new List<LoadoutItem>();
        bool selectable = false;
        foreach (var (name, startup) in c.BaseWargear.Select(n => (n, false)).Concat(c.StartupWargear.Select(n => (n, true))))
        {
            if (wargear(name) is not { } doc) continue;   // noted once per class by Build
            var meta = CatalogData.Meta(doc);
            if (meta.Slot != slot) continue;
            if (startup && !meta.Visible) continue;   // hidden from the customizer (the melta bomb)
            if (items.Any(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            selectable |= startup;
            bool simulated = true;
            string? note = null;
            if (slot == "melee" && !SimulatedMeleeSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            { simulated = false; note = "melee weapon not simulated yet (no combo)"; }
            else if (slot == "equipment" && doc.Root.Table("ability_data/ability_definitions/ability_utility/delivery") is null && !StimTuning.IsStim(doc))
            { simulated = false; note = "equipment without a thrown delivery not simulated"; }   // perks/inert-2: stims are simulated
            items.Add(new LoadoutItem(name, slot, CatalogData.Text(locale, meta.NameId) ?? name, CatalogData.Text(locale, meta.DescriptionId) ?? "", meta.Level, simulated, note, null));
        }
        return new SlotChoices(slot, items, selectable);
    }

    /// <summary>The first <c>action_data/lifetime_actions/commands/apply_upgrade_string/strings</c> entry, or null (<see cref="MpClasses.ReadPerk"/>).</summary>
    public static string? UpgradeString(BafDocument perk) => MpClasses.ReadPerk("", perk).UpgradeString;
}

/// <summary>Reading helpers shared by the catalogues.</summary>
static class CatalogData
{
    public static string Leaf(string path) => path.Split('\\', '/')[^1];

    public static int IntOf(BafTable table, string name) => table[name] is { Type: BafType.Int } e ? e.Raw : 0;

    public static float NumberOf(BafTable table, string name) =>
        table[name] is { } e ? e.Type switch { BafType.Float => e.AsFloat(), BafType.Int => e.Raw, _ => 0f } : 0f;

    /// <summary>The locale text of <paramref name="id"/>, or null when there is none or it is empty or whitespace (callers fall back to file names).</summary>
    public static string? Text(LocaleTable? locale, int id) => id > 0 && locale?.Get(id) is { } t && !string.IsNullOrWhiteSpace(t) ? t : null;

    public readonly record struct Info(string Slot, int Level, bool Visible, int NameId, int DescriptionId, string HelmetOverride);

    /// <summary>A wargear file's slot, unlock level (1 when absent), customizer visibility (true when absent) and text ids.</summary>
    public static Info Meta(BafDocument doc)
    {
        var common = doc.Root.Table("common_data");
        var ui = doc.Root.Table("ui_data");
        int level = common?.Table("unlock_info/level_requirement") is { } l && l["level_required"] is { Type: BafType.Int } e ? e.Raw : MpClasses.MissingLevel;
        bool visible = ui?["visible_in_customizer"] is not { Type: BafType.Bool } v || v.AsBool();
        return new Info(common?.String("slot") ?? "", level, visible, ui is { } u ? IntOf(u, "name") : 0, ui is { } u2 ? IntOf(u2, "description") : 0, common?.String("helmet_override") ?? "");
    }

    public static BafDocument ReadGlobal(string gameDir)
    {
        string path = Path.Combine(gameDir, MpClasses.GlobalPackage);
        BafDocument? global = null;
        using var archive = ObpkArchive.Open(path);
        archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName == "global", (_, d) => global = BafDocument.Read(d));
        return global ?? throw new FileNotFoundException($"global not found in {MpClasses.GlobalPackage}.");
    }

    public static Dictionary<string, BafDocument> ReadWargear(string gameDir, ISet<string> names, List<string> notes)
    {
        var docs = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(gameDir, GameplayTuning.WargearPackage);
        if (!File.Exists(path)) { notes.Add($"{GameplayTuning.WargearPackage} not found; no wargear text or slots"); return docs; }
        using var archive = ObpkArchive.Open(path);
        archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && names.Contains(e.FileName), (e, d) =>
        {
            if (docs.ContainsKey(e.FileName)) notes.Add($"wargear: several files named {e.FileName}; the last is used");   // as MpClasses' blueprints
            try { docs[e.FileName] = BafDocument.Read(d); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { notes.Add($"wargear {e.FileName} unreadable ({ex.Message})"); }
        });
        return docs;
    }

    public static LocaleTable? ReadLocale(string gameDir, string language, List<string> notes)
    {
        string path = Path.Combine(gameDir, "data", "locale", language, "ucs.pc");
        if (!File.Exists(path)) { notes.Add($"locale {language} not found; names fall back to file names"); return null; }
        try { return LocaleTable.Open(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { notes.Add($"locale {language} unreadable ({ex.Message}); names fall back to file names"); return null; }
    }
}
