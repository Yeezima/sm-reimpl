using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmSim;

namespace SmContent.Customization;

/// <summary>One thing <see cref="ProfileStore.Load"/> changed or noted. <see cref="Path"/> is a JSON-style location ("classes[2].primary").</summary>
/// <param name="Informational">True for a note that changed nothing (an unsimulated item kept, a perk the pistol needs); false for a repair of the data.</param>
public sealed record ProfileRepair(string Path, string Message, bool Informational = false);

/// <summary>How the file on disk was.</summary>
public enum ProfileLoadStatus
{
    /// <summary>Read (possibly with repairs).</summary>
    Loaded,
    /// <summary>No file: the default profile.</summary>
    Missing,
    /// <summary>Not valid JSON or no usable schema: renamed to <c>customization.json.bad-&lt;utc&gt;</c>, the default profile returned.</summary>
    Corrupt,
    /// <summary>A schema newer than <see cref="CustomizationProfile.CurrentSchema"/>: read as far as understood, <see cref="CustomizationProfile.ReadOnly"/>, never overwritten.</summary>
    NewerSchema,
    /// <summary>The file exists but could not be read: the default profile, read-only (the file is left alone).</summary>
    Unreadable,
}

/// <summary>The result of <see cref="ProfileStore.Load"/>; deconstructs to <c>(profile, repairs)</c>.</summary>
public sealed record ProfileLoadResult(CustomizationProfile Profile, IReadOnlyList<ProfileRepair> Repairs, ProfileLoadStatus Status)
{
    /// <summary>Whether the data was repaired (the caller may save the repaired profile; informational notes do not count).</summary>
    public bool Repaired => Repairs.Any(r => !r.Informational);

    public void Deconstruct(out CustomizationProfile profile, out IReadOnlyList<ProfileRepair> repairs) { profile = Profile; repairs = Repairs; }
}

/// <summary>
/// The profile on disk (docs/customizer-architecture.md section 3): <c>customization.json</c> (schema 1, atomic temp + rename) and the custom emblems
/// <c>emblems/&lt;hex&gt;.bc3</c> (exactly <see cref="EmblemHash.BlobBytes"/> bytes of BC3, named by their <see cref="EmblemHash"/>). The directory is the
/// caller's (the client passes its user directory, tests a temp directory); the store writes nowhere else, never in the repo or the game install.
/// <see cref="Load"/> never throws for bad data: it repairs field by field against the catalogue and reports each repair.
/// </summary>
public sealed class ProfileStore
{
    public const string FileName = "customization.json", EmblemDirectory = "emblems", EmblemExtension = ".bc3";

    /// <summary>A profile file larger than this is not read (a full profile is a few tens of KB): it is treated as unreadable and left alone.</summary>
    public const long MaxFileBytes = 1_048_576;

    readonly Func<DateTime> _utcNow;

    /// <param name="directory">The profile's directory (<see cref="FileName"/> and the emblems in it).</param>
    /// <param name="profileFileName">Another file name for the profile in that directory (<see cref="ForFile"/>); null: <see cref="FileName"/>.</param>
    public ProfileStore(string directory, Func<DateTime>? utcNow = null, string? profileFileName = null)
    {
        Directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _profileFileName = string.IsNullOrEmpty(profileFileName) ? FileName : Path.GetFileName(profileFileName);
    }

    /// <summary>
    /// A store for a profile file given by path (the client's <c>--profile &lt;json&gt;</c>): the profile is that file, its emblems are in <see cref="EmblemDirectory"/>
    /// next to it.
    /// </summary>
    public static ProfileStore ForFile(string profilePath, Func<DateTime>? utcNow = null)
    {
        string full = Path.GetFullPath(profilePath);
        return new ProfileStore(Path.GetDirectoryName(full) ?? ".", utcNow, Path.GetFileName(full));
    }

    readonly string _profileFileName;
    public string Directory { get; }
    public string ProfilePath => Path.Combine(Directory, _profileFileName);
    string EmblemDir => Path.Combine(Directory, EmblemDirectory);

    /// <summary>The file of an emblem (named by the hash's hex digits only).</summary>
    public string EmblemPath(EmblemHash hash) => Path.Combine(EmblemDir, hash.ToHex() + EmblemExtension);

    // ---- emblems ----

    /// <summary>Stores an emblem (exactly <see cref="EmblemHash.BlobBytes"/> bytes of BC3) and returns its hash; an identical file already there is kept.</summary>
    public EmblemHash SaveEmblem(ReadOnlySpan<byte> bc3)
    {
        if (bc3.Length != EmblemHash.BlobBytes) throw new ArgumentException($"An emblem is {EmblemHash.BlobBytes} bytes of BC3, not {bc3.Length}.", nameof(bc3));
        var hash = EmblemHash.Of(bc3);
        if (HasEmblem(hash)) return hash;
        System.IO.Directory.CreateDirectory(EmblemDir);
        WriteAtomically(EmblemPath(hash), bc3);
        return hash;
    }

    /// <summary>The emblem's BC3 bytes, or null when the file is missing, the wrong size or does not hash to its name.</summary>
    public byte[]? LoadEmblem(EmblemHash hash)
    {
        if (hash.IsNone) return null;
        try
        {
            string path = EmblemPath(hash);
            if (!File.Exists(path) || new FileInfo(path).Length != EmblemHash.BlobBytes) return null;
            byte[] bytes = File.ReadAllBytes(path);
            return EmblemHash.Of(bytes) == hash ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public bool HasEmblem(EmblemHash hash) => LoadEmblem(hash) is not null;

    // ---- the profile ----

    /// <summary>
    /// Reads and repairs the profile against <paramref name="catalog"/>. Repair rules: unknown, wrong-race or not-offered names become the default (null) with a note;
    /// a duplicate perk becomes null; more than <see cref="CustomizationProfile.MaxCustomClasses"/> classes or <see cref="CustomizationProfile.MaxAppearancesPerRace"/>
    /// appearances per race are trimmed; names are trimmed (control characters removed, at most <see cref="CustomizationProfile.MaxNameLength"/> characters);
    /// a custom emblem without its file shows the fallback badge; items the simulation does not run (equipment without a thrown delivery or stim effect; stims run since perks/inert-2) are kept and noted;
    /// a newer schema gives a read-only profile; unparseable JSON is renamed to <c>customization.json.bad-&lt;utc&gt;</c> and the default profile returned.
    /// </summary>
    public ProfileLoadResult Load(CustomizationCatalog catalog)
    {
        var repairs = new List<ProfileRepair>();
        if (!File.Exists(ProfilePath)) return new ProfileLoadResult(CustomizationProfile.Default(catalog), repairs, ProfileLoadStatus.Missing);
        string text;
        try
        {
            if (new FileInfo(ProfilePath).Length > MaxFileBytes)
            {
                repairs.Add(new ProfileRepair("", $"{FileName} is larger than {MaxFileBytes / 1024 / 1024} MB and could not be read; defaults are used and the file is left alone"));
                return new ProfileLoadResult(CustomizationProfile.Default(catalog) with { ReadOnly = true }, repairs, ProfileLoadStatus.Unreadable);
            }
            text = File.ReadAllText(ProfilePath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            repairs.Add(new ProfileRepair("", $"{FileName} could not be read ({ex.Message}); defaults are used and the file is left alone"));
            return new ProfileLoadResult(CustomizationProfile.Default(catalog) with { ReadOnly = true }, repairs, ProfileLoadStatus.Unreadable);
        }

        JsonObject? root = null;
        int schema = 0;
        try
        {
            root = JsonNode.Parse(text, null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
            if (root is not null && !(TryInt(root["schema"], out schema) && schema >= 1)) root = null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { root = null; }   // ArgumentException: a duplicate key

        if (root is null)
        {
            string bad = MoveAside(repairs);
            repairs.Add(new ProfileRepair("", $"{FileName} is not a usable profile (invalid JSON or no schema); {(bad.Length > 0 ? $"renamed to {bad}" : "it could not be renamed")}, defaults are used"));
            return new ProfileLoadResult(CustomizationProfile.Default(catalog), repairs, ProfileLoadStatus.Corrupt);
        }

        bool newer = schema > CustomizationProfile.CurrentSchema;
        if (newer) repairs.Add(new ProfileRepair("schema", $"{FileName} has schema {schema}, newer than {CustomizationProfile.CurrentSchema}; it is read as far as understood and never overwritten", true));
        CustomizationProfile profile;
        try { profile = new Reader(catalog, repairs, HasEmblem).Read(root, schema); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or InvalidCastException)
        {
            repairs.Add(new ProfileRepair("", $"{FileName} could not be understood ({ex.Message}); defaults are used"));
            if (!newer) { MoveAside(repairs); return new ProfileLoadResult(CustomizationProfile.Default(catalog), repairs, ProfileLoadStatus.Corrupt); }
            profile = CustomizationProfile.Default(catalog);
        }
        return newer
            ? new ProfileLoadResult(profile with { ReadOnly = true }, repairs, ProfileLoadStatus.NewerSchema)
            : new ProfileLoadResult(profile, repairs, ProfileLoadStatus.Loaded);
    }

    /// <summary>
    /// Writes the profile (temp file, then rename over <c>customization.json</c>). Refuses a <see cref="CustomizationProfile.ReadOnly"/> profile and a file on disk with a newer schema.
    /// </summary>
    /// <exception cref="InvalidOperationException">The profile is read-only or the file on disk is of a newer schema.</exception>
    public void Save(CustomizationProfile profile)
    {
        if (profile.ReadOnly) throw new InvalidOperationException($"The profile was read from a newer schema and is read-only; {FileName} is not overwritten.");
        if (OnDiskSchema() is { } existing && existing > CustomizationProfile.CurrentSchema)
            throw new InvalidOperationException($"{FileName} on disk has schema {existing}, newer than {CustomizationProfile.CurrentSchema}; it is not overwritten.");
        System.IO.Directory.CreateDirectory(Directory);
        WriteAtomically(ProfilePath, Encoding.UTF8.GetBytes(Serialize(profile)));
    }

    int? OnDiskSchema()
    {
        try
        {
            if (!File.Exists(ProfilePath)) return null;
            return JsonNode.Parse(File.ReadAllText(ProfilePath, Encoding.UTF8)) is JsonObject o && TryInt(o["schema"], out int s) ? s : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    static void WriteAtomically(string path, ReadOnlySpan<byte> bytes)
    {
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);   // on disk before the rename, so a crash never leaves a renamed but empty file
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    string MoveAside(List<ProfileRepair> repairs)
    {
        string stamp = _utcNow().ToUniversalTime().ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        for (int n = 0; n < 100; n++)
        {
            string target = ProfilePath + ".bad-" + stamp + (n == 0 ? "" : "-" + n);
            if (File.Exists(target)) continue;
            try { File.Move(ProfilePath, target); return Path.GetFileName(target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                repairs.Add(new ProfileRepair("", $"could not rename {FileName} ({ex.Message})"));
                return "";
            }
        }
        return "";
    }

    // ---- shared helpers ----

    /// <summary>The roles the catalogue's classes have ("tactical", "heavy", "assault"), tactical first, the rest in the data's order.</summary>
    public static IReadOnlyList<string> Roles(CustomizationCatalog catalog)
    {
        var roles = catalog.Perks.Classes.Select(c => c.Role).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int tactical = roles.FindIndex(r => r.Equals("tactical", StringComparison.OrdinalIgnoreCase));
        if (tactical > 0) { string t = roles[tactical]; roles.RemoveAt(tactical); roles.Insert(0, t); }
        return roles;
    }

    /// <summary>The role of the initial DEFAULTS selection (tactical when the catalogue has it).</summary>
    public static string FirstRole(CustomizationCatalog catalog) => Roles(catalog).FirstOrDefault() ?? "tactical";

    /// <summary>The items a custom class keeps that the simulation does not run yet, as (slot, neutral name): they are shown unavailable and sent as the default.</summary>
    public static IReadOnlyList<(string Slot, string Name)> UnsimulatedItems(CustomClass custom, CustomizationCatalog catalog)
    {
        var result = new List<(string, string)>();
        foreach (var (slot, name) in new[] { ("primary", custom.Primary), ("pistol", custom.Pistol), ("melee", custom.Melee), ("equipment", custom.Equipment) })
            if (name is not null && Offered(catalog, custom.Role, slot).FirstOrDefault(i => ProfileWire.Neutral(i.Name).Equals(name, StringComparison.OrdinalIgnoreCase)) is { Simulated: false })
                result.Add((slot, name));
        return result;
    }

    internal static IEnumerable<LoadoutItem> Offered(CustomizationCatalog catalog, string role, string slot) =>
        catalog.Perks.Classes.Where(c => c.Role.Equals(role, StringComparison.OrdinalIgnoreCase)).SelectMany(c => c.For(slot).Items);

    internal static bool TryInt(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue v && v.TryGetValue(out value);
    }

    // ---- writing ----

    /// <summary>The profile as the JSON text <see cref="Save"/> writes (schema 1; indented; properties in a fixed order).</summary>
    public static string Serialize(CustomizationProfile profile)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", profile.Schema);
            w.WriteStartObject("selected");
            if (profile.Selected.Kind == SelectionKind.Custom) { w.WriteString("kind", "custom"); w.WriteNumber("slot", profile.Selected.Slot); }
            else { w.WriteString("kind", "default"); w.WriteString("role", profile.Selected.Role); }
            w.WriteEndObject();
            w.WriteStartArray("classes");
            foreach (var c in profile.Classes.OrderBy(c => c.Slot)) WriteClass(w, c);
            w.WriteEndArray();
            w.WriteStartArray("appearances");
            foreach (var a in profile.Appearances) WriteAppearance(w, a);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static void WriteClass(Utf8JsonWriter w, CustomClass c)
    {
        w.WriteStartObject();
        w.WriteNumber("slot", c.Slot);
        w.WriteString("name", c.Name);
        w.WriteString("role", c.Role);
        w.WriteString("primary", c.Primary);
        w.WriteString("pistol", c.Pistol);
        w.WriteString("melee", c.Melee);
        w.WriteString("equipment", c.Equipment);
        w.WriteStartArray("perks");
        foreach (var p in c.Perks) w.WriteStringValue(p);
        w.WriteEndArray();
        w.WriteStartObject("appearance");
        w.WriteString(CustomizationCatalog.SpaceMarine, c.Appearance.SpaceMarine);
        w.WriteString(CustomizationCatalog.Chaos, c.Appearance.Chaos);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    static IEnumerable<KeyValuePair<string, T>> InSlotOrder<T>(IReadOnlyDictionary<string, T> map) =>
        map.OrderBy(kv => { int i = CustomizationCatalog.ArmourSlots.ToList().IndexOf(kv.Key); return i < 0 ? int.MaxValue : i; }).ThenBy(kv => kv.Key, StringComparer.Ordinal);

    static void WriteAppearance(Utf8JsonWriter w, AppearanceProfile a)
    {
        w.WriteStartObject();
        w.WriteString("id", a.Id);
        w.WriteString("race", a.Race);
        w.WriteString("name", a.Name);
        w.WriteString("predefinedSet", a.PredefinedSet);
        w.WriteStartObject("pieces");
        foreach (var (slot, piece) in InSlotOrder(a.Pieces)) w.WriteString(slot, piece);
        w.WriteEndObject();
        w.WriteStartObject("paint");
        foreach (var (slot, p) in InSlotOrder(a.Paint))
        {
            w.WriteStartObject(slot);
            w.WriteString("primary", p.Primary);
            w.WriteString("pattern", p.Pattern);
            w.WriteString("detail", p.Detail);
            w.WriteBoolean("patternOn", p.PatternOn);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WriteString("presetApplied", a.PresetApplied);
        w.WriteStartObject("decals");
        foreach (var (pad, d) in InSlotOrder(a.Decals))
        {
            w.WriteStartObject(pad);
            if (d.None) w.WriteBoolean("none", true);
            else if (d.Emblem is not null) { w.WriteString("emblem", d.Emblem); w.WriteString("fallback", d.Fallback); }
            else w.WriteString("badge", d.Badge);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        if (a.Lens is { } lens) { w.WriteStartArray("lens"); w.WriteNumberValue(lens.R); w.WriteNumberValue(lens.G); w.WriteNumberValue(lens.B); w.WriteEndArray(); }
        else w.WriteNull("lens");
        w.WriteEndObject();
    }

    // ---- reading and repairing ----

    sealed class Reader(CustomizationCatalog catalog, List<ProfileRepair> repairs, Func<EmblemHash, bool> emblemExists)
    {
        static readonly string[] Pads = ["leftshoulder", "rightshoulder"];
        readonly IReadOnlyList<string> _roles = Roles(catalog);

        void Fix(string path, string message) => repairs.Add(new ProfileRepair(path, message));
        void Note(string path, string message) => repairs.Add(new ProfileRepair(path, message, true));

        static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        /// <summary>Control characters removed, trimmed, cut to <paramref name="max"/> characters (never inside a surrogate pair); the fallback when empty.</summary>
        static string Clean(string? text, int max, string fallback)
        {
            var sb = new StringBuilder();
            foreach (char ch in text ?? "") if (!char.IsControl(ch)) sb.Append(ch);
            string s = sb.ToString().Trim();
            if (s.Length > max) { s = s[..max]; if (char.IsHighSurrogate(s[^1])) s = s[..^1]; s = s.TrimEnd(); }
            return s.Length == 0 ? fallback : s;
        }

        public CustomizationProfile Read(JsonObject root, int schema)
        {
            var appearances = ReadAppearances(root["appearances"] as JsonArray);
            var classes = ReadClasses(root["classes"] as JsonArray, appearances);
            var selected = ReadSelection(root["selected"] as JsonObject, classes);
            return new CustomizationProfile(schema > CustomizationProfile.CurrentSchema ? schema : CustomizationProfile.CurrentSchema, selected, classes, appearances);
        }

        // ---- selection ----

        Selection ReadSelection(JsonObject? node, List<CustomClass> classes)
        {
            var fallback = Selection.DefaultFor(FirstRole(catalog));
            if (node is null) return fallback;
            string? kind = Str(node["kind"])?.Trim().ToLowerInvariant();
            if (kind == "custom" && TryInt(node["slot"], out int slot))
            {
                if (classes.Any(c => c.Slot == slot)) return Selection.CustomSlot(slot);
                Fix("selected", $"custom slot {slot} has no class; the default loadout is selected");
                return fallback;
            }
            if (kind == "default" && Str(node["role"])?.Trim() is { Length: > 0 } role)
            {
                if (_roles.FirstOrDefault(r => r.Equals(role, StringComparison.OrdinalIgnoreCase)) is { } known) return Selection.DefaultFor(known);
                Fix("selected.role", $"unknown role '{role}'; the default loadout is selected");
                return fallback;
            }
            Fix("selected", "the selection is not understood; the default loadout is selected");
            return fallback;
        }

        // ---- custom classes ----

        List<CustomClass> ReadClasses(JsonArray? array, IReadOnlyList<AppearanceProfile> appearances)
        {
            var result = new List<CustomClass>();
            if (array is null) return result;
            var slots = new HashSet<int>();
            for (int i = 0; i < array.Count; i++)
            {
                string path = $"classes[{i}]";
                if (array[i] is not JsonObject node) { Fix(path, "not an object; dropped"); continue; }
                if (!TryInt(node["slot"], out int slot) || slot < 1 || slot > CustomizationProfile.MaxCustomClasses)
                { Fix(path, $"slot must be 1..{CustomizationProfile.MaxCustomClasses}; dropped"); continue; }
                if (!slots.Add(slot)) { Fix(path, $"slot {slot} is used twice; this one is dropped"); continue; }
                string? roleName = Str(node["role"])?.Trim();
                string? role = _roles.FirstOrDefault(r => r.Equals(roleName, StringComparison.OrdinalIgnoreCase));
                if (role is null) { Fix(path, $"unknown role '{roleName}'; dropped"); slots.Remove(slot); continue; }

                string name = Clean(Str(node["name"]), CustomizationProfile.MaxNameLength, $"CUSTOM LOADOUT {slot}");
                string? primary = Item(node["primary"], $"{path}.primary", role, "primary"), pistol = Item(node["pistol"], $"{path}.pistol", role, "pistol"),
                    melee = Item(node["melee"], $"{path}.melee", role, "melee"), equipment = Item(node["equipment"], $"{path}.equipment", role, "equipment");
                var perks = ReadPerks(node["perks"], $"{path}.perks", role);
                var refs = ReadRefs(node["appearance"] as JsonObject, $"{path}.appearance", appearances);
                var custom = new CustomClass(slot, name, role, primary, pistol, melee, equipment, perks, refs);
                foreach (var (slotName, item) in UnsimulatedItems(custom, catalog))
                    Note($"{path}.{slotName}", $"{item} is not simulated yet; it is kept and sent as the class default");
                if (pistol is not null && Offered(catalog, role, "pistol").FirstOrDefault(p => ProfileWire.Neutral(p.Name).Equals(pistol, StringComparison.OrdinalIgnoreCase)) is { RequiredPerk: { } needed }
                    && !perks.Contains(needed, StringComparer.OrdinalIgnoreCase))
                    Note($"{path}.pistol", $"{pistol} needs the perk {needed}; the class default pistol is used until it is chosen");
                result.Add(custom);
            }
            result = result.OrderBy(c => c.Slot).ToList();
            return result;
        }

        string? Item(JsonNode? node, string path, string role, string slot)
        {
            if (node is null) return null;
            if (Str(node) is not { } raw) { Fix(path, "not a name; the class default is used"); return null; }
            raw = raw.Trim();
            if (raw.Length == 0) return null;
            var items = Offered(catalog, role, slot).ToList();
            if (!catalog.Perks.Classes.Any(c => c.Role.Equals(role, StringComparison.OrdinalIgnoreCase) && c.For(slot).Selectable))
            { Fix(path, $"the {role} class has no choice for this slot; '{raw}' dropped (the class default is used)"); return null; }
            string neutral = ProfileWire.Neutral(raw);
            var match = items.FirstOrDefault(i => ProfileWire.Neutral(i.Name).Equals(neutral, StringComparison.OrdinalIgnoreCase));
            if (match is null) { Fix(path, $"'{raw}' is not offered to the {role} class; the class default is used"); return null; }
            return ProfileWire.Neutral(match.Name);
        }

        List<string?> ReadPerks(JsonNode? node, string path, string role)
        {
            var result = new List<string?> { null, null };
            if (node is null) return result;
            if (node is not JsonArray array) { Fix(path, "not a list; no perks"); return result; }
            var offered = catalog.Perks.Classes.Where(c => c.Role.Equals(role, StringComparison.OrdinalIgnoreCase))
                .SelectMany(c => c.Perks.Concat(c.DefaultPerk is { } d ? [d] : [])).Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (array.Count > 2) Fix(path, $"{array.Count} perks listed; only the first two are kept");
            for (int i = 0; i < Math.Min(2, array.Count); i++)
            {
                if (array[i] is null) continue;
                if (Str(array[i])?.Trim() is not { Length: > 0 } raw) { Fix($"{path}[{i}]", "not a name; no perk"); continue; }
                string? match = offered.FirstOrDefault(p => p.Equals(raw, StringComparison.OrdinalIgnoreCase));
                if (match is null) { Fix($"{path}[{i}]", $"'{raw}' is not a perk of the {role} class; dropped"); continue; }
                if (result.Contains(match, StringComparer.OrdinalIgnoreCase)) { Fix($"{path}[{i}]", $"'{match}' is chosen twice; the second is dropped"); continue; }
                result[i] = match;
            }
            return result;
        }

        AppearanceRefs ReadRefs(JsonObject? node, string path, IReadOnlyList<AppearanceProfile> appearances)
        {
            if (node is null) return AppearanceRefs.None;
            string? Ref(string race)
            {
                if (Str(node[race])?.Trim() is not { Length: > 0 } id) return null;
                var look = appearances.FirstOrDefault(a => a.Id == id);
                if (look is null) { Fix($"{path}.{race}", $"appearance '{id}' does not exist; the default look is used"); return null; }
                if (look.Race != race) { Fix($"{path}.{race}", $"appearance '{id}' is for {look.Race}; the default look is used"); return null; }
                return id;
            }
            return new AppearanceRefs(Ref(CustomizationCatalog.SpaceMarine), Ref(CustomizationCatalog.Chaos));
        }

        // ---- appearances ----

        List<AppearanceProfile> ReadAppearances(JsonArray? array)
        {
            var result = new List<AppearanceProfile>();
            if (array is null) return result;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var perRace = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < array.Count; i++)
            {
                string path = $"appearances[{i}]";
                if (array[i] is not JsonObject node) { Fix(path, "not an object; dropped"); continue; }
                string? race = Str(node["race"])?.Trim();
                if (race is null || catalog.Race(race) is not { } rc) { Fix(path, $"unknown race '{race}'; dropped"); continue; }
                int count = perRace.GetValueOrDefault(race);
                if (count >= CustomizationProfile.MaxAppearancesPerRace)
                { Fix(path, $"more than {CustomizationProfile.MaxAppearancesPerRace} {race} appearances; this one is dropped"); continue; }
                perRace[race] = count + 1;

                string? id = Str(node["id"])?.Trim();
                string prefix = race == CustomizationCatalog.Chaos ? "csm-" : "sm-";
                if (id is null || id.Length == 0 || id.Length > CustomizationProfile.MaxIdLength || !id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') || !ids.Add(id))
                {
                    string old = id ?? "";
                    int n = 1;
                    while (!ids.Add(id = prefix + n)) n++;
                    Fix($"{path}.id", old.Length == 0 ? $"no usable id; '{id}' assigned" : $"id '{old}' is not usable or used twice; '{id}' assigned (class references to it are lost)");
                }
                string name = Clean(Str(node["name"]), CustomizationProfile.MaxNameLength, $"Appearance {count + 1}");

                string? set = null;
                if (Str(node["predefinedSet"])?.Trim() is { Length: > 0 } setName)
                {
                    set = rc.PredefinedSets.FirstOrDefault(s => s.Name.Equals(setName, StringComparison.OrdinalIgnoreCase))?.Name;
                    if (set is null) Fix($"{path}.predefinedSet", $"'{setName}' is not a predefined {race} set; ignored");
                }
                string? preset = null;
                if (Str(node["presetApplied"])?.Trim() is { Length: > 0 } presetName)
                {
                    preset = rc.Presets.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase))?.Name;
                    if (preset is null) Fix($"{path}.presetApplied", $"'{presetName}' is not a {race} preset; ignored");
                }
                result.Add(new AppearanceProfile(id, race, name, set, ReadPieces(node["pieces"] as JsonObject, $"{path}.pieces", rc),
                    ReadPaint(node["paint"] as JsonObject, $"{path}.paint"), preset, ReadDecals(node["decals"] as JsonObject, $"{path}.decals", rc), ReadLens(node["lens"], $"{path}.lens")));
            }
            return result;
        }

        Dictionary<string, string> ReadPieces(JsonObject? node, string path, RaceCatalog rc)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (node is null) return result;
            foreach (var (key, value) in node)
            {
                string? slot = CustomizationCatalog.ArmourSlots.FirstOrDefault(s => s.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (slot is null) { Fix($"{path}.{key}", "not an armour slot; dropped"); continue; }
                if (value is null) continue;
                if (Str(value)?.Trim() is not { Length: > 0 } piece) { Fix($"{path}.{key}", "not a piece name; the default piece is used"); continue; }
                var match = rc.PiecesFor(slot).FirstOrDefault(p => p.Name.Equals(piece, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    string other = rc.Race == CustomizationCatalog.Chaos ? CustomizationCatalog.SpaceMarine : CustomizationCatalog.Chaos;
                    bool wrongRace = catalog.Race(other)?.PiecesFor(slot).Any(p => p.Name.Equals(piece, StringComparison.OrdinalIgnoreCase)) == true;
                    Fix($"{path}.{key}", wrongRace ? $"'{piece}' belongs to the other race; the default piece is used" : $"'{piece}' is not an offered {rc.Race} {slot}; the default piece is used");
                    continue;
                }
                result[slot] = match.Name;
            }
            return result;
        }

        Dictionary<string, SlotPaint> ReadPaint(JsonObject? node, string path)
        {
            var result = new Dictionary<string, SlotPaint>(StringComparer.Ordinal);
            if (node is null) return result;
            foreach (var (key, value) in node)
            {
                string? slot = CustomizationCatalog.ArmourSlots.FirstOrDefault(s => s.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (slot is null) { Fix($"{path}.{key}", "not an armour slot; dropped"); continue; }
                if (value is not JsonObject paint) { if (value is not null) Fix($"{path}.{key}", "not an object; the scheme's paint is used"); continue; }
                string? Colour(string field)
                {
                    if (paint[field] is null) return null;
                    if (Str(paint[field])?.Trim() is not { Length: > 0 } c) { Fix($"{path}.{key}.{field}", "not a colour name; the scheme's colour is used"); return null; }
                    var match = catalog.Palette.FirstOrDefault(p => p.Name.Equals(c, StringComparison.OrdinalIgnoreCase));
                    if (match is null) Fix($"{path}.{key}.{field}", $"'{c}' is not a visible palette colour; the scheme's colour is used");
                    return match?.Name;
                }
                bool patternOn = paint["patternOn"] is JsonValue v && v.TryGetValue<bool>(out var on) && on;
                var slotPaint = new SlotPaint(Colour("primary"), Colour("pattern"), Colour("detail"), patternOn);
                if (!slotPaint.IsEmpty) result[slot] = slotPaint;
            }
            return result;
        }

        Dictionary<string, DecalChoice> ReadDecals(JsonObject? node, string path, RaceCatalog rc)
        {
            var result = new Dictionary<string, DecalChoice>(StringComparer.Ordinal);
            if (node is null) return result;
            EmblemHash shown = EmblemHash.None;
            foreach (var pad in Pads)
            {
                var key = node.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(pad, StringComparison.OrdinalIgnoreCase));
                if (key is null || node[key] is not JsonObject d) { if (key is not null && node[key] is not null) Fix($"{path}.{key}", "not an object; the scheme's decal is used"); continue; }
                string at = $"{path}.{key}";
                string? Badge(JsonNode? value, string field)
                {
                    if (Str(value)?.Trim() is not { Length: > 0 } badge) return null;
                    var match = rc.Decals.FirstOrDefault(b => b.Name.Equals(badge, StringComparison.OrdinalIgnoreCase));
                    if (match is null) Fix($"{at}.{field}", $"'{badge}' is not an offered {rc.Race} badge; ignored");
                    return match?.Name;
                }
                if (d["none"] is JsonValue none && none.TryGetValue<bool>(out var isNone) && isNone) { result[pad] = DecalChoice.NoDecal; continue; }
                string? fallback = Badge(d["fallback"], "fallback");
                if (d["emblem"] is not null)
                {
                    if (!EmblemHash.TryParseHex(Str(d["emblem"])?.Trim(), out var hash) || hash.IsNone)
                    { Fix($"{at}.emblem", "not a 32-digit emblem hash; the fallback badge is used"); AddBadge(result, pad, fallback); continue; }
                    if (!shown.IsNone && shown != hash)
                    { Fix($"{at}.emblem", "a look carries one custom emblem; this pad shows its fallback badge"); AddBadge(result, pad, fallback); continue; }
                    if (!emblemExists(hash))
                    { Fix($"{at}.emblem", $"emblem file {hash.ToHex()}{EmblemExtension} is missing or damaged; the fallback badge is used"); AddBadge(result, pad, fallback); continue; }
                    shown = hash;
                    result[pad] = DecalChoice.ForEmblem(hash.ToHex(), fallback);
                    continue;
                }
                AddBadge(result, pad, Badge(d["badge"], "badge"));
            }
            foreach (var key in node.Select(kv => kv.Key).Where(k => !Pads.Contains(k, StringComparer.OrdinalIgnoreCase)))
                Fix($"{path}.{key}", "not a shoulder pad; dropped");
            return result;
        }

        static void AddBadge(Dictionary<string, DecalChoice> decals, string pad, string? badge)
        {
            if (badge is not null) decals[pad] = DecalChoice.ForBadge(badge);
        }

        LensColour? ReadLens(JsonNode? node, string path)
        {
            if (node is null) return null;
            if (node is JsonArray a && a.Count == 3 && TryInt(a[0], out int r) && TryInt(a[1], out int g) && TryInt(a[2], out int b) && r is >= 0 and <= 255 && g is >= 0 and <= 255 && b is >= 0 and <= 255)
                return new LensColour((byte)r, (byte)g, (byte)b);
            Fix(path, "not [red, green, blue] of 0..255; the original look is used");
            return null;
        }
    }
}
