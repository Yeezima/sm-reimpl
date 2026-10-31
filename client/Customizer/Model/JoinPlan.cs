using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmContent;
using SmContent.Customization;
using SmSim;

namespace SmClient.Customizer.Model;

/// <summary>The side the Join screen asks for.</summary>
public enum SideChoice { Auto, SpaceMarines, Chaos }

/// <summary>
/// What the front end remembers between runs (<c>frontend.json</c> next to the profile): the server address, the requested side, two accessibility /
/// privacy options and the content profile (added content E2: <c>faithful</c>, <c>extended</c> or a flag list; default <see cref="ContentProfiles.DefaultName"/>,
/// the owner's decision of 2026-10-29). Engine-free; a missing or damaged file gives the defaults.
/// </summary>
/// <remarks>fix/connect-timeout: the Host panel's bots' difficulty (default Easy). fix/hosting-bots-logs (the owner's decision of 2026-10-29): the number of bots is
/// never remembered (hosting starts none unless the player picks a number); the old <c>hostBotsLocal</c> / <c>hostBotsLan</c> fills of earlier builds are ignored
/// when read and dropped at the next save, so they cannot bring back the 7 bots.</remarks>
public sealed record FrontEndSettings(string Host, SideChoice Side, bool ReduceMotion, bool ShowOthersEmblems, string Content = ContentProfiles.DefaultName,
    BotDifficulty BotDifficulty = BotDifficulty.Easy, float FovDegrees = 0f, float MouseSensitivity = SmClient.Customizer.Model.MouseSensitivity.Default, GameOptions? SavedOptions = null)
{
    /// <summary>The Options menu's settings (ui/menu-rebuild).</summary>
    public GameOptions Options { get => SavedOptions ?? GameOptions.Default; init => SavedOptions = value == GameOptions.Default ? null : value; }

    public const string FileName = "frontend.json", DefaultHost = "127.0.0.1:27015";
    public static FrontEndSettings Default => new(DefaultHost, SideChoice.Auto, false, true);

    /// <summary>
    /// The added content the saved profile name means in this build: its flags when the name parses (a build without extras refuses any but
    /// <c>faithful</c>), else <see cref="ContentProfiles.Default"/> (none in a build without extras).
    /// </summary>
    public ContentExtras ContentExtras => ContentProfiles.TryParse(Content, out var extras, out _) ? extras : ContentProfiles.Default;

    /// <summary>A settings file larger than this is not read (it is a few hundred bytes).</summary>
    public const long MaxFileBytes = 1_048_576;

    public static FrontEndSettings Load(string directory)
    {
        try
        {
            string path = Path.Combine(directory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes) return Default;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return Default;
            string host = o["host"] is JsonValue h && h.TryGetValue<string>(out var hs) && ValidateHost(hs) is null ? hs.Trim() : DefaultHost;
            var side = o["side"] is JsonValue s && s.TryGetValue<string>(out var ss) && Enum.TryParse<SideChoice>(ss, true, out var parsed) ? parsed : SideChoice.Auto;
            bool Flag(string name, bool fallback) => o[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
            // A name of letters and commas (faithful, extended, orks,gore); anything else is the default. What it means in this build is ContentExtras.
            string content = o["content"] is JsonValue c && c.TryGetValue<string>(out var cs) && cs.Trim().ToLowerInvariant() is { Length: > 0 and <= 40 } name && name.All(ch => ch is >= 'a' and <= 'z' or ',')
                ? name : ContentProfiles.DefaultName;
            var difficulty = o["hostBotDifficulty"] is JsonValue d && d.TryGetValue<string>(out var ds) && HostBots.ParseDifficulty(ds) is { } dv ? dv : BotDifficulty.Easy;
            // ui/fov-slider: the horizontal field of view (0 = the data's look); a wrong value is the default.
            float fov = o["fovDegrees"] is JsonValue fv && fv.TryGetValue<double>(out var fd) && double.IsFinite(fd) && fd is > 0 and <= FovMath.Maximum ? (float)fd : 0f;
            // ui/mouse-sensitivity: the factor on the mouse look (1 = the original feel); a wrong value is the default.
            float sens = o["mouseSensitivity"] is JsonValue mv && mv.TryGetValue<double>(out var md) && double.IsFinite(md) && md is >= SmClient.Customizer.Model.MouseSensitivity.Minimum - 1e-6 and <= SmClient.Customizer.Model.MouseSensitivity.Maximum + 1e-6 ? SmClient.Customizer.Model.MouseSensitivity.Clamp((float)md) : SmClient.Customizer.Model.MouseSensitivity.Default;
            return new FrontEndSettings(host, side, Flag("reduceMotion", false), Flag("showOthersEmblems", true), content, difficulty, fov, sens) { Options = GameOptions.Read(o["options"]) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) { return Default; }
    }

    /// <summary>Writes the settings (temp file, then rename); returns an error text or null.</summary>
    public string? Save(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var o = new JsonObject { ["host"] = Host, ["side"] = Side.ToString(), ["reduceMotion"] = ReduceMotion, ["showOthersEmblems"] = ShowOthersEmblems, ["content"] = Content,
                ["hostBotDifficulty"] = BotDifficulty.ToString(), ["fovDegrees"] = Math.Round(FovDegrees, 1), ["mouseSensitivity"] = Math.Round(MouseSensitivity, 2), ["options"] = Options.Write() };
            string path = Path.Combine(directory, FileName), temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(System.Text.Encoding.UTF8.GetBytes(o.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
                    stream.Flush(flushToDisk: true);   // on disk before the rename
                }
                File.Move(temp, path, true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
    }

    /// <summary>A message when <paramref name="text"/> is not <c>host</c>, <c>host:port</c> or <c>[v6]:port</c>, else null. No lookup is made.</summary>
    public static string? ValidateHost(string? text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0) return "Enter the server address (host:port).";
        if (text.Any(char.IsWhiteSpace) || text.Length > 253) return "The address must not contain spaces.";
        string? portText = null;
        if (text.StartsWith('[')) { int close = text.IndexOf(']'); if (close < 2) return "The address is not valid."; portText = close + 1 < text.Length ? (text[close + 1] == ':' ? text[(close + 2)..] : "x") : null; }
        else if (System.Net.IPAddress.TryParse(text, out _) && text.Count(c => c == ':') > 1) portText = null;   // a bare IPv6 address
        else if (text.Contains(':')) { int colon = text.LastIndexOf(':'); if (text.IndexOf(':') != colon || colon == 0) return "The address is not valid."; portText = text[(colon + 1)..]; }
        if (portText is not null && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)) return "The port must be 1 to 65535.";
        return null;
    }
}

/// <summary>
/// Everything a Join sends for the active loadout, worked out from the profile (docs/customizer-architecture.md sections 3 and 4): the class and the item
/// choices as wire indices for the side asked for, the two perks, and the wire look for each race. The Join screen turns it into the existing connect path's
/// arguments (<see cref="ToArgs"/>: class and item indices); the networked view (T7) reads the rest to fill <c>ClientOptions</c>
/// (see <c>JoinPlanOptions.ApplyTo</c> in the client).
/// </summary>
/// <param name="OrkLook">The Hello's Ork look (ui/customizer-ork): the profile's <see cref="CustomizationProfile.OrkLook"/> as the wire byte, <see cref="Races.OrkLookAutomatic"/> when automatic
/// or when the profile has no Orks.</param>
public sealed record JoinPlan(Selection Selection, LoadoutChoice Choice, Team Side, string? ClassName, string Summary,
    AppearanceCode SpaceMarineLook, AppearanceCode ChaosLook, bool UsesEmblem, byte OrkLook = Races.OrkLookAutomatic)
{
    /// <summary>
    /// The plan for <paramref name="selection"/>. <paramref name="side"/> Auto asks for the Space Marine class and leaves the side to the class (the server may
    /// balance the player onto the other one); an empty or missing custom slot reads as the selected role's DEFAULTS or slot 1 via <see cref="LoadoutEdits.Active"/>.
    /// <paramref name="extras"/> (added content E2; default the config's own flags) is the content profile: a custom class the profile does not have
    /// (<see cref="LoadoutAvailability.Unavailable"/>, the Ork Boy without the Orks) is kept in the profile but not played: the plan is the first role's
    /// DEFAULTS on the side, and the summary says so; the plan never holds a class the profile does not offer (<see cref="ContentGate.ForJoin"/>).
    /// </summary>
    public static JoinPlan Build(CustomizationProfile profile, Selection selection, SimConfig config, CustomizationCatalog catalog, SideChoice side, ContentExtras? extras = null)
    {
        var content = extras ?? config.Extras;
        if (!LoadoutEdits.IsValid(profile, selection)) selection = LoadoutEdits.Active(profile, catalog, out _);
        var team = side switch { SideChoice.Chaos => Team.Chaos, SideChoice.SpaceMarines => Team.SpaceMarines, _ => Team.None };
        var classSide = team == Team.None ? Team.SpaceMarines : team;
        string race = ProfileWire.RaceOf(classSide) ?? CustomizationCatalog.SpaceMarine;
        string summary;
        LoadoutChoice choice;
        AppearanceCode sm = AppearanceCode.Default, chaos = AppearanceCode.Default;
        string fallbackRole = ProfileStore.FirstRole(catalog);
        if (LoadoutAvailability.Unavailable(profile, selection, content))
        {
            // A saved Ork loadout (custom or the Ork DEFAULTS) in a profile without the Orks: kept, not played.
            string name = profile.ClassAt(selection.Slot) is { } unavailable && selection.Kind == SelectionKind.Custom ? unavailable.Name : $"{OrkLoadout.ClassName} defaults";
            choice = ProfileWire.DefaultLoadout(fallbackRole, config, classSide);
            summary = $"{name}: {LoadoutAvailability.Reason(content)} Playing the {LoadoutRows.ClassName(catalog, fallbackRole, race)} defaults instead. {LoadoutRows.DefaultSummary(catalog, fallbackRole, race)}";
        }
        else if (selection.Kind == SelectionKind.Custom && profile.ClassAt(selection.Slot) is { } custom)
        {
            choice = ProfileWire.ToLoadout(custom, config, classSide);
            summary = $"{custom.Name}: {LoadoutRows.Summary(catalog, custom, race)}";
            var both = new CustomizationProfile(profile.Schema, selection, profile.Classes, profile.Appearances);
            sm = ProfileWire.SelectedAppearance(both, CustomizationCatalog.SpaceMarine, catalog);
            chaos = ProfileWire.SelectedAppearance(both, CustomizationCatalog.Chaos, catalog);
        }
        else
        {
            string role = selection.Role ?? ProfileStore.FirstRole(catalog);
            choice = ProfileWire.DefaultLoadout(role, config, classSide);
            summary = $"Defaults: {LoadoutRows.DefaultSummary(catalog, role, race)}";
        }
        choice = ContentGate.ForJoin(choice, config, content, ProfileWire.DefaultLoadout(fallbackRole, config, classSide));
        string? className = choice.Class >= 0 && choice.Class < config.Classes.Count ? config.Classes[choice.Class].Name : null;
        byte orkLook = ContentGate.OrkLook(ProfileWire.OrkLookByte(profile.OrkLook), content);
        return new JoinPlan(selection, choice, team, className, summary, sm, chaos, sm.UsesEmblem || chaos.UsesEmblem, orkLook);
    }

    /// <summary>
    /// The arguments the existing connect path understands (<c>--class</c> by the class's name, <c>--primary</c> ... <c>--equipment</c> by list index,
    /// <c>--side</c>). Perks and looks are not among them; they travel in <c>ClientOptions</c>.
    /// </summary>
    public IReadOnlyList<(string Key, string Value)> ToArgs()
    {
        var args = new List<(string, string)>();
        if (ClassName is not null) args.Add(("class", ClassName));
        void Add(string key, int index) { if (index >= 0) args.Add((key, index.ToString(CultureInfo.InvariantCulture))); }
        Add("primary", Choice.Primary); Add("pistol", Choice.Pistol); Add("melee", Choice.Melee); Add("equipment", Choice.Equipment);
        if (Side != Team.None) args.Add(("side", Side == Team.Chaos ? "chaos" : "marines"));
        return args;
    }
}

/// <summary>
/// Which saved loadouts a content profile can play (added content E2). A loadout of the Ork Boy (a custom class of role <see cref="OrkClass.Role"/>, saved by the
/// customizer's faction step since ui/customizer-ork, or the Ork DEFAULTS selected) is not played in a profile without the Orks: it stays in the profile, untouched,
/// shown as unavailable and joined as the side's default class.
/// </summary>
public static class LoadoutAvailability
{
    /// <summary>Whether <paramref name="custom"/> is of a race the profile does not have.</summary>
    public static bool Unavailable(CustomClass custom, ContentExtras extras) =>
        OrkLoadout.IsOrk(custom.Role) && !ContentGate.OffersOrks(extras);

    /// <summary>Whether a selection (a custom slot, or a DEFAULTS role) is of a race the profile does not have.</summary>
    public static bool Unavailable(CustomizationProfile profile, Selection selection, ContentExtras extras) =>
        !ContentGate.OffersOrks(extras) && (selection.Kind == SelectionKind.Custom ? profile.ClassAt(selection.Slot) is { } c && Unavailable(c, extras) : OrkLoadout.IsOrk(selection.Role));

    /// <summary>Why a loadout is unavailable (one sentence for the strip's tooltip and the Join summary); empty when it is available.</summary>
    public static string Reason(CustomClass custom, ContentExtras extras) => Unavailable(custom, extras) ? Reason(extras) : "";

    /// <summary>Why the Orks' loadouts are not played in <paramref name="extras"/>.</summary>
    public static string Reason(ContentExtras extras) => $"The Orks are added content and this profile is {ContentProfiles.Name(extras)}.";
}

/// <summary>The launch flags of the front end (<c>--loadout</c>), parsed without Godot.</summary>
public static class LaunchOptions
{
    /// <summary><c>default:&lt;role&gt;</c> (a DEFAULTS loadout, role as in the catalogue or a class word) or <c>1..10</c> (a custom slot); null when it is neither.</summary>
    public static Selection? ParseLoadout(string? text, CustomizationCatalog catalog)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int slot))
            return slot is >= 1 and <= CustomizationProfile.MaxCustomClasses ? Selection.CustomSlot(slot) : null;
        const string prefix = "default:";
        string word = (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text[prefix.Length..] : text).Trim().ToLowerInvariant();
        string? role = word switch
        {
            "tactical" or "sm - tactical" => "tactical",
            "heavy" or "devastator" or "havoc" => "heavy",
            "assault" or "raptor" => "assault",
            "ork" or "ork boy" or "ork - boy" or "orks" => OrkLoadout.Role,   // ui/customizer-ork: the Ork DEFAULTS (played only with the Orks; the Join says so otherwise)
            _ => null,
        };
        if (OrkLoadout.IsOrk(role)) return Selection.DefaultFor(OrkLoadout.Role);
        if (role is null || !ProfileStore.Roles(catalog).Contains(role, StringComparer.OrdinalIgnoreCase)) return null;
        return Selection.DefaultFor(ProfileStore.Roles(catalog).First(r => r.Equals(role, StringComparison.OrdinalIgnoreCase)));
    }
}
