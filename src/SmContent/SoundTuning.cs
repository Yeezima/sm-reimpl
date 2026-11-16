using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmContent;

/// <summary>
/// sound/tuning-tool (owner, 2026-11-14: "create a tool for me that will allow me to adjust sound levels? separated logically"): the owner's mixer. Every voice the client plays belongs to one
/// <see cref="SoundTuningGroup"/> (<see cref="SoundTuningGroups.Resolve"/>: by the event's name, whose emitter it is and the kind of post); a group holds a volume (dB) and optionally its own
/// distance fall-off (<see cref="GroupTuning"/>), and the whole mixer a reverb scale. Engine-free: the client's <c>SoundSystem</c> reads <see cref="Live"/>, the in-client panel
/// (<c>SoundTunerPanel</c>) edits it, and <see cref="SoundTuningFile"/> keeps the owner's changes in a JSON file outside the repo and the install. All defaults are 0 dB and "the data's curve"
/// (nothing changes until the owner moves a slider); every number here is **ours** (spec "Sound tuning tool").
/// </summary>
public readonly record struct SoundTuningGroup(string Id, string Section, string Label, string Help, bool MusouOnly = false);

/// <summary>The groups, in the panel's order, and the rule that puts a voice in one.</summary>
public static class SoundTuningGroups
{
    public const string Master = "master";
    public const string OwnGun = "own_gun", OwnMelee = "own_melee", OwnJump = "own_jump", OwnSteps = "own_steps", OwnVoice = "own_voice", OwnOther = "own_other";
    public const string GunBolter = "gun_bolter", GunHeavy = "gun_heavy", GunPlasma = "gun_plasma", GunMelta = "gun_melta", GunLas = "gun_las", GunOther = "gun_other";
    public const string StepsOthers = "steps_others", MeleeOthers = "melee_others", JumpOthers = "jump_others", VoiceOthers = "voice_others";
    public const string MeleeImpacts = "melee_impacts", Explosions = "explosions", Grenades = "grenades", Ambience = "ambience", Ui = "ui", Other = "other";
    public const string MusouWar = "musou_war", MusouThreat = "musou_threat";

    public static readonly IReadOnlyList<SoundTuningGroup> All =
    [
        new(Master, "Master", "Everything", "Every sound the match plays (on top of the Options menu's volumes)."),
        new(OwnGun, "Your actions", "Your gun", "The shots you fire (all weapons), first person."),
        new(OwnMelee, "Your actions", "Your melee", "Your swings, charges and rolls."),
        new(OwnJump, "Your actions", "Your jump pack", "Your jump pack's take-off, hover and landing."),
        new(OwnSteps, "Your actions", "Your footsteps", "Your own steps and landings."),
        new(OwnVoice, "Your actions", "Your voice", "Your barks and efforts."),
        new(OwnOther, "Your actions", "Your other sounds", "Reloads, gear, weapon swaps, shield and armour sounds."),
        new(GunBolter, "Other players' guns", "Bolters and pistols", "Bolter, Stalker, storm bolter and the pistols."),
        new(GunHeavy, "Other players' guns", "Heavy bolter and autocannon", "The heavy weapons' fire."),
        new(GunPlasma, "Other players' guns", "Plasma", "Plasma guns and cannons."),
        new(GunMelta, "Other players' guns", "Melta", "Melta guns."),
        new(GunLas, "Other players' guns", "Lascannon", "Lascannon fire."),
        new(GunOther, "Other players' guns", "Other weapons", "Any other weapon's fire."),
        new(StepsOthers, "Other players' movement", "Footsteps", "Other players' steps, landings and rolls."),
        new(JumpOthers, "Other players' movement", "Jump packs", "Other players' jump pack sounds."),
        new(MeleeOthers, "Other players' movement", "Melee swings and charges", "Other players' swings and charges."),
        new(VoiceOthers, "Other players' movement", "Voices", "Other players' barks and efforts."),
        new(MeleeImpacts, "Impacts and blasts", "Melee and bullet impacts", "Hit sounds: melee hits, bullet impacts and hit confirms."),
        new(Explosions, "Impacts and blasts", "Explosions", "Blasts of every kind (not grenade throws)."),
        new(Grenades, "Impacts and blasts", "Grenades", "Grenade throws and bounces."),
        new(Ambience, "World", "Ambience and map", "The map's own sounds and the environment."),
        new(Ui, "World", "Interface", "Menu, countdown and match-result sounds."),
        new(Other, "World", "Everything else", "Sounds no other group claims."),
        new(MusouWar, "Musou Mode", "The far war", "Musou Mode: the battle's background.", MusouOnly: true),
        new(MusouThreat, "Musou Mode", "Close threat cues", "Musou Mode: the close-threat warnings.", MusouOnly: true),
    ];

    static readonly Dictionary<string, int> Indices = All.Select((g, i) => (g.Id, i)).ToDictionary(t => t.Id, t => t.i, StringComparer.Ordinal);

    public static int IndexOf(string id) => Indices.GetValueOrDefault(id, -1);

    /// <summary>
    /// The group of a voice: the post's event, whether it is the own player's emitter, whether it is a footstep post, the emitter's Musou flags and its label.
    /// Interpretation (ours): the event names are the install's (<c>play_sm_heavy_bolter_fire</c>, <c>play_ch_frag_grenade_explode</c>, ...); anything unrecognised falls in "Everything else".
    /// </summary>
    public static string Resolve(string evt, bool own, bool footstep, bool background = false, bool threat = false, string label = "")
    {
        if (threat) return MusouThreat;
        if (background) return MusouWar;
        string e = evt ?? "";
        bool speech = e.StartsWith("speech/", StringComparison.Ordinal);
        if (speech) return own ? OwnVoice : VoiceOthers;
        if (e.StartsWith("play_menu", StringComparison.OrdinalIgnoreCase) || e.StartsWith("play_mp_", StringComparison.OrdinalIgnoreCase) || e.StartsWith("play_ui", StringComparison.OrdinalIgnoreCase)) return Ui;
        if (footstep) return own ? OwnSteps : StepsOthers;
        bool Has(string s) => e.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("footstep") || Has("footfall") || Has("_step")) return own ? OwnSteps : StepsOthers;
        if (Has("explo") || Has("blast")) return Explosions;
        if (Has("jump_pack") || Has("jumppack")) return own ? OwnJump : JumpOthers;
        if (Has("_fire")) return own ? OwnGun : Has("lascannon") ? GunLas : Has("heavy_bolter") || Has("autocannon") || Has("auto_cannon") ? GunHeavy : Has("plasma") ? GunPlasma : Has("melta") ? GunMelta
            : Has("bolter") || Has("bolt_pistol") || Has("pistol") ? GunBolter : GunOther;
        if (Has("grenade")) return Grenades;
        if (Has("_hit") || Has("smack") || Has("impact") || Has("ricochet")) return MeleeImpacts;
        if (Has("swing") || Has("slam") || Has("charge") || Has("bullrush") || Has("evade") || Has("roll") || Has("kick") || Has("dash")) return own ? OwnMelee : MeleeOthers;
        if (own) return OwnOther;
        if (label.Equals("world", StringComparison.OrdinalIgnoreCase) || label.StartsWith("amb", StringComparison.OrdinalIgnoreCase) || Has("ambience") || Has("amb_") || Has("wind")) return Ambience;
        return Other;
    }
}

/// <summary>One group's settings: a volume and a fall-off.</summary>
/// <param name="VolumeDb">Added to every voice of the group (0 = as now), dB, -40..+24.</param>
/// <param name="CustomFalloff">false: the data's distance curve (with the match's loudness rule) as now; true: the group's own curve below.</param>
/// <param name="MinDistance">Custom: metres inside which the voice is at its full level (0 m level).</param>
/// <param name="MaxDistance">Custom: metres beyond which the voice is silent (it fades out over its last 10 %).</param>
/// <param name="RolloffDb">Custom: decibels lost per doubling of the distance beyond <see cref="MinDistance"/> (6 = the real inverse-distance law; 0 = no fall-off).</param>
public readonly record struct GroupTuning(float VolumeDb = 0f, bool CustomFalloff = false, float MinDistance = 3f, float MaxDistance = 80f, float RolloffDb = 6f)
{
    public const float MinVolumeDb = -40f, MaxVolumeDb = 24f;
    public static GroupTuning Default => new();

    public GroupTuning Clamped() => new(Math.Clamp(Finite(VolumeDb), MinVolumeDb, MaxVolumeDb), CustomFalloff, Math.Clamp(Finite(MinDistance, 3f), 0.5f, 200f),
        Math.Clamp(Finite(MaxDistance, 80f), 1f, 500f), Math.Clamp(Finite(RolloffDb, 6f), 0f, 24f));

    static float Finite(float v, float fallback = 0f) => float.IsFinite(v) ? v : fallback;

    /// <summary>The custom curve's level change at <paramref name="distance"/> m, dB (0 within the minimum; -80 beyond the maximum; a 10 % fade into silence).</summary>
    public float FalloffDb(float distance)
    {
        if (!CustomFalloff) return 0f;
        float min = Math.Max(0.5f, MinDistance), max = Math.Max(min + 0.5f, MaxDistance);
        float db = distance <= min ? 0f : -RolloffDb * MathF.Log2(distance / min);
        if (distance >= max) return -80f;
        float fade = max * 0.9f;
        if (distance > fade) db += -60f * (distance - fade) / (max - fade);
        return Math.Max(db, -80f);
    }

    public bool IsDefault => this == Default;
}

/// <summary>All groups' settings and the reverb scale: the tuner's whole state.</summary>
public sealed class SoundTuning
{
    /// <summary>The running client's tuning (the panel edits it, the mixer reads it; replaced as a whole, so a reader never sees half of a change).</summary>
    public static SoundTuning Live { get; set; } = new();

    readonly GroupTuning[] _groups = new GroupTuning[SoundTuningGroups.All.Count];

    /// <summary>Scale on the map's reverb send (1 = as now, 0 = none, up to 3).</summary>
    public float ReverbScale { get; set; } = 1f;

    public GroupTuning this[int index] { get => index >= 0 && index < _groups.Length ? _groups[index] : default; set { if (index >= 0 && index < _groups.Length) _groups[index] = value.Clamped(); } }
    public GroupTuning Of(string id) => this[SoundTuningGroups.IndexOf(id)];
    public void Set(string id, GroupTuning t) => this[SoundTuningGroups.IndexOf(id)] = t;

    /// <summary>The volume of a group plus the master's, dB (what a voice of that group gets).</summary>
    public float GainDb(int group) => group < 0 ? this[0].VolumeDb : group == 0 ? this[0].VolumeDb : this[0].VolumeDb + this[group].VolumeDb;

    public bool IsDefault => ReverbScale == 1f && _groups.All(g => g.IsDefault);

    public SoundTuning Clone() { var c = new SoundTuning { ReverbScale = ReverbScale }; Array.Copy(_groups, c._groups, _groups.Length); return c; }

    public void CopyFrom(SoundTuning other) { ReverbScale = other.ReverbScale; Array.Copy(other._groups, _groups, _groups.Length); }

    public static float ClampReverb(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 3f) : 1f;
}

/// <summary>The tuning's JSON files: the owner's overrides (<c>%LOCALAPPDATA%\sm-reimpl\sound-tuning.json</c>) and the shipped defaults they sit on (<c>config/sound-tuning-defaults.json</c> in the repo).</summary>
public static class SoundTuningFile
{
    public const int Version = 1;
    public const string OverridesFileName = "sound-tuning.json";
    public const string DefaultsFileName = "sound-tuning-defaults.json";

    /// <summary>The overrides file's folder: <c>%LOCALAPPDATA%\sm-reimpl</c> (never the install, never the repo).</summary>
    public static string Directory() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sm-reimpl");

    public static string OverridesPath() => Path.Combine(Directory(), OverridesFileName);

    /// <summary>The JSON of a tuning: only the groups that differ from <paramref name="baseline"/> (or every group with <paramref name="all"/>), and the reverb scale when it does.</summary>
    public static string ToJson(SoundTuning t, SoundTuning? baseline = null, bool all = false)
    {
        baseline ??= new SoundTuning();
        var groups = new JsonObject();
        for (int i = 0; i < SoundTuningGroups.All.Count; i++)
        {
            var g = t[i];
            if (!all && g == baseline[i]) continue;
            var o = new JsonObject { ["volumeDb"] = Math.Round(g.VolumeDb, 2) };
            if (g.CustomFalloff || all) { o["customFalloff"] = g.CustomFalloff; o["minDistance"] = Math.Round(g.MinDistance, 2); o["maxDistance"] = Math.Round(g.MaxDistance, 2); o["rolloffDb"] = Math.Round(g.RolloffDb, 2); }
            groups[SoundTuningGroups.All[i].Id] = o;
        }
        var root = new JsonObject { ["version"] = Version, ["reverbScale"] = Math.Round(t.ReverbScale, 3), ["groups"] = groups };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Applies a file's text on top of <paramref name="into"/> (unknown groups and bad values are skipped; a group not in the file keeps its value). Returns false for text that is not a tuning file.</summary>
    public static bool TryApply(string json, SoundTuning into)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); } catch (JsonException) { return false; }
        if (node is not JsonObject root) return false;
        if (root["reverbScale"] is JsonValue rv && rv.TryGetValue<double>(out var r)) into.ReverbScale = SoundTuning.ClampReverb((float)r);
        if (root["groups"] is JsonObject groups)
            foreach (var (id, value) in groups)
            {
                int index = SoundTuningGroups.IndexOf(id);
                if (index < 0 || value is not JsonObject o) continue;
                var g = into[index];
                float F(string name, float fallback) => o[name] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? (float)d : fallback;
                bool custom = o["customFalloff"] is JsonValue cv && cv.TryGetValue<bool>(out var cb) ? cb : g.CustomFalloff;
                into[index] = new GroupTuning(F("volumeDb", g.VolumeDb), custom, F("minDistance", g.MinDistance), F("maxDistance", g.MaxDistance), F("rolloffDb", g.RolloffDb));
            }
        return true;
    }

    /// <summary>Loads the shipped defaults (when the file exists), then the owner's overrides on top; a missing or broken file is skipped (the result says what was read).</summary>
    public static (SoundTuning Defaults, SoundTuning Current, string Note) Load(string? defaultsPath, string? overridesPath)
    {
        var defaults = new SoundTuning();
        string note = "built-in defaults";
        if (defaultsPath is not null && File.Exists(defaultsPath))
        {
            try { note = TryApply(File.ReadAllText(defaultsPath), defaults) ? "shipped defaults" : "shipped defaults unreadable, built-in used"; }
            catch (IOException) { note = "shipped defaults unreadable, built-in used"; }
        }
        var current = defaults.Clone();
        if (overridesPath is not null && File.Exists(overridesPath))
        {
            try { note += TryApply(File.ReadAllText(overridesPath), current) ? " + your overrides" : "; your overrides file is not valid JSON (ignored)"; }
            catch (IOException) { note += "; your overrides file could not be read"; }
        }
        return (defaults, current, note);
    }

    /// <summary>Writes the overrides (the groups that differ from <paramref name="defaults"/>) to <paramref name="path"/>, creating its folder.</summary>
    public static void SaveOverrides(string path, SoundTuning current, SoundTuning defaults) => Write(path, ToJson(current, defaults));

    /// <summary>Writes every group, as a defaults file the lead can commit as <c>config/sound-tuning-defaults.json</c>.</summary>
    public static void ExportDefaults(string path, SoundTuning current) => Write(path, ToJson(current, null, all: true));

    static void Write(string path, string text)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
