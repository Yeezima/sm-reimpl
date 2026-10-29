using SmFormats.Baf;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>The two speech commands of the multiplayer files (M33): an effort or reaction grunt, or a spoken action line.</summary>
public enum SpeechKind { Exertion, Action }

/// <summary>
/// A <c>post_speech_exertion_event</c> or <c>post_speech_action_event</c> command (M33; the character-voice "barks") in a
/// blueprint or wargear file. Fields as the data has them: <see cref="Character"/> (<c>1_character</c>, always <c>autodetect</c>
/// in the multiplayer files: the class's <c>speech_arg_character</c>, see <see cref="SoundTriggers.SpeechCharacters"/>), and
/// <see cref="Type"/> (<c>2_exertion_type</c> such as <c>effort_short_stab</c>, or <c>2_action_type</c>, empty in all multiplayer
/// action commands), <see cref="State"/> (<c>3_action_state</c>) and <see cref="Threat"/> (<c>4_threat_level</c>) for actions.
/// Where they sit and what triggers them is read like <see cref="SoundBinding"/>: the nearest enclosing non-structural table
/// (a behaviour tag under <c>triggered_actions</c>, or <c>start_audio</c> of an ability) and its ability.
/// The command resolves through the dialogue events of the speech bank: <c>speech_exertion</c> (character, exertion type) and
/// <c>speech_action</c> (character, action type, state, threat); the names are found by hashing (docs/discovery.md).
/// </summary>
public sealed record SpeechBinding(string Path, string Trigger, string Ability, IReadOnlyList<string> RequiredUpgrades, SpeechKind Kind,
    string Character, string Type, string State, string Threat)
{
    public bool IsDefault => RequiredUpgrades.Count == 0;
    public bool UnderTriggeredActions => Path.Contains("/triggered_actions/", StringComparison.Ordinal);

    /// <summary>Every speech command in a file, in file order.</summary>
    public static IReadOnlyList<SpeechBinding> Collect(BafTable root)
    {
        var found = new List<SpeechBinding>();
        SoundBinding.WalkCommands(root, (child, kind, path, upgrades, _) =>
        {
            SpeechKind speech;
            if (kind.EndsWith("post_speech_exertion_event", StringComparison.OrdinalIgnoreCase)) speech = SpeechKind.Exertion;
            else if (kind.EndsWith("post_speech_action_event", StringComparison.OrdinalIgnoreCase)) speech = SpeechKind.Action;
            else return false;
            int trigger = SoundBinding.TriggerIndex(path);
            found.Add(new SpeechBinding(string.Join("/", path), trigger >= 0 ? path[trigger] : "", SoundBinding.AbilityOf(path), upgrades.Distinct().ToList(), speech,
                child.String("1_character") ?? "", child.String(speech == SpeechKind.Exertion ? "2_exertion_type" : "2_action_type") ?? "",
                child.String("3_action_state") ?? "", child.String("4_threat_level") ?? ""));
            return true;
        });
        return found;
    }
}

public sealed partial class SoundFile
{
    /// <summary>The file's speech commands (M33).</summary>
    public IReadOnlyList<SpeechBinding> Speech { get; init; } = [];

    /// <summary>The default (no upgrade) speech commands of a trigger (<c>start_audio</c>, a table name, or a behaviour tag).</summary>
    public IReadOnlyList<SpeechBinding> SpeechOf(string trigger) =>
        Speech.Where(b => b.IsDefault && string.Equals(b.Trigger, trigger, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>A behaviour tag's speech commands (only those under <c>triggered_actions</c>).</summary>
    public IReadOnlyList<SpeechBinding> SpeechTag(string tag) =>
        Speech.Where(b => b.IsDefault && b.UnderTriggeredActions && string.Equals(b.Trigger, tag, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The default speech command at the start of a melee ability (<c>start_audio</c> under <c>start_actions</c> of the ability
    /// named <paramref name="ability"/>, e.g. <c>ability_melee_x2</c>), or null.
    /// </summary>
    public SpeechBinding? AbilityStart(string ability) =>
        Speech.FirstOrDefault(b => b.IsDefault && b.Kind == SpeechKind.Exertion && b.Trigger.Equals("start_audio", StringComparison.OrdinalIgnoreCase)
                                   && b.Ability.Equals(ability, StringComparison.OrdinalIgnoreCase));
}

public sealed partial class SoundTriggers
{
    /// <summary>
    /// Class name (the key under <c>multiplayer_settings/ingame_progression/classes</c> of <c>global</c>, e.g. <c>sm - tactical</c>) →
    /// the speech character (<c>speech_arg_character</c>: <c>tactical_marine</c>, <c>devastator</c>, <c>chaos_havoc</c>, ...) that
    /// <c>autodetect</c> resolves to for a player of that class (interpretation: the data names the field, the engine's lookup is
    /// not visible).
    /// </summary>
    public IReadOnlyDictionary<string, string> SpeechCharacters { get; init; } = new Dictionary<string, string>();

    /// <summary>The speech character of a class name, or null.</summary>
    public string? SpeechCharacter(string className) => SpeechCharacters.GetValueOrDefault(className);

    /// <summary>Reads <c>speech_arg_character</c> of every multiplayer class from the <c>global</c> document.</summary>
    public static IReadOnlyDictionary<string, string> ReadSpeechCharacters(BafDocument global)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (global.Root.Table("extensions/multiplayer_settings/ingame_progression/classes") is not { } classes) return map;
        foreach (var c in classes.Entries)
            if (c.Type == BafType.Table && c.AsTable().String("speech_arg_character") is { Length: > 0 } character) map[c.Name] = character;
        return map;
    }

    static IReadOnlyDictionary<string, string> LoadSpeechCharacters(string gameDir, List<string> notes)
    {
        try
        {
            BafDocument? global = null;
            using var archive = ObpkArchive.Open(Path.Combine(gameDir, MpClasses.GlobalPackage));
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName == "global", (_, d) => global = BafDocument.Read(d));
            if (global is not null) return ReadSpeechCharacters(global);
            notes.Add("global not found; no speech characters");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            notes.Add($"speech characters not read: {ex.Message}");
        }
        return new Dictionary<string, string>();
    }
}
