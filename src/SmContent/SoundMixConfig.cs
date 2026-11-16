using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmContent;

/// <summary>
/// sound/mix-expand: one sound's overrides in the owner's mix editor. A sound is an event name (the game's own, e.g. <c>play_sm_heavy_bolter_fire</c>; the stable id) heard from one side:
/// your own sounds or other players' (world sounds count as others'). All values ours; the defaults are "as the game plays it".
/// </summary>
/// <param name="VolumeDb">Added to the sound's level, dB (-40..+24).</param>
/// <param name="PitchSemitones">Pitch shift, semitones (-12..+12); the playback speed is scaled by 2^(n/12).</param>
/// <param name="DistanceScale">Hearing distance: the listener's distance is divided by this (2 = heard twice as far, 0.5 = half as far), 0.25..4.</param>
/// <param name="Mute">The sound is silent.</param>
/// <param name="Raw">This sound plays raw (<see cref="SoundMixEffect"/>: no filters, effects, mix passes or randomisation of ours or the data's).</param>
public sealed record SoundOverride(float VolumeDb = 0f, float PitchSemitones = 0f, float DistanceScale = 1f, bool Mute = false, bool Raw = false)
{
    public const float MinVolumeDb = -40f, MaxVolumeDb = 24f, MinPitch = -12f, MaxPitch = 12f, MinScale = 0.25f, MaxScale = 4f;

    public static readonly SoundOverride None = new();

    public bool IsDefault => VolumeDb == 0f && PitchSemitones == 0f && DistanceScale == 1f && !Mute && !Raw;

    public SoundOverride Clamped() => new(Math.Clamp(Finite(VolumeDb, 0f), MinVolumeDb, MaxVolumeDb), Math.Clamp(Finite(PitchSemitones, 0f), MinPitch, MaxPitch),
        Math.Clamp(Finite(DistanceScale, 1f), MinScale, MaxScale), Mute, Raw);

    static float Finite(float v, float fallback) => float.IsFinite(v) ? v : fallback;
}

/// <summary>
/// What a voice gets from the mix editor at this moment (resolved from <see cref="SoundMixConfig"/> when the voice starts and whenever the config changes), or null when nothing applies
/// (the default: the sound system then behaves exactly as before). Engine-free arithmetic so the rules can be tested.
/// </summary>
/// <remarks>
/// Raw (<see cref="Raw"/>, a sound's own or the global toggle): the client plays the sample as authored. It skips: the data's low-pass (the filter buses) and the on-hit high-pass; the map's
/// reverb send and the bus limiter (the voice goes to a bus of its own straight into Master); the data's random pitch and volume draws, the node and bus volumes, the RTPC curves, the
/// Set/Reset ducking, the loudness rule and every pass of ours (the gun and hit gains, the footstep level, the jump pack tail, the Classic soundscape's range and rear low-pass, the Musou
/// background duck, the autocannon's body boost, a group's own fall-off). Kept: the data's distance volume curve (a positioned sound still gets quieter with distance), the Options menu's
/// volumes, the group volumes of the Groups tab, the per-sound volume, pitch and hearing distance (they are the owner's own choices), the voice cap and instance limits (they decide which
/// sound plays, not how it sounds) and Master's own volume and safety limiter.
/// </remarks>
public sealed class SoundMixEffect
{
    /// <summary>The level of a silenced voice (dB): far under any threshold.</summary>
    public const float SilenceDb = -200f;

    public static readonly SoundMixEffect RawOnly = new(false, 0f, 0f, 1f, true);

    public SoundMixEffect(bool silenced, float volumeDb, float pitchSemitones, float distanceScale, bool raw)
    {
        Silenced = silenced; VolumeDb = volumeDb; PitchSemitones = pitchSemitones; DistanceScale = distanceScale; Raw = raw;
        PitchFactor = MathF.Pow(2f, pitchSemitones / 12f);
    }

    public bool Silenced { get; }
    public float VolumeDb { get; }
    public float PitchSemitones { get; }
    public float DistanceScale { get; }
    public bool Raw { get; }
    public float PitchFactor { get; }

    /// <summary>The level the editor adds, dB (<see cref="SilenceDb"/> when silenced).</summary>
    public float GainDb => Silenced ? SilenceDb : VolumeDb;

    public static float Gain(SoundMixEffect? fx) => fx?.GainDb ?? 0f;
    public static float Pitch(SoundMixEffect? fx, float pitchScale) => fx is null ? pitchScale : pitchScale * fx.PitchFactor;
    public static float Distance(SoundMixEffect? fx, float distance) => fx is null ? distance : distance / fx.DistanceScale;
}

/// <summary>
/// The mix editor's whole state: per-sound overrides (own and others' separately), a global raw switch and the solo set (solo is a listening aid, never saved or exported). Engine-free; the
/// client's <c>SoundSystem</c> reads <see cref="Live"/> and the F7 panel edits it. Empty means today's behaviour exactly (<see cref="Resolve"/> returns null, nothing else runs).
/// </summary>
public sealed class SoundMixConfig
{
    public static SoundMixConfig Live { get; set; } = new();

    readonly Dictionary<string, SoundOverride> _own = new(StringComparer.OrdinalIgnoreCase), _others = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _soloOwn = new(StringComparer.OrdinalIgnoreCase), _soloOthers = new(StringComparer.OrdinalIgnoreCase);
    bool _globalRaw;

    /// <summary>Raised on every change (a voice compares its cached version with this to know it must resolve again).</summary>
    public int Version { get; private set; }

    public bool GlobalRaw { get => _globalRaw; set { if (_globalRaw == value) return; _globalRaw = value; Version++; } }

    public int Count => _own.Count + _others.Count;
    public bool AnySolo => _soloOwn.Count + _soloOthers.Count > 0;

    /// <summary>True when the config changes nothing: no overrides, no global raw, no solo.</summary>
    public bool IsEmpty => Count == 0 && !GlobalRaw && !AnySolo;

    Dictionary<string, SoundOverride> Map(bool own) => own ? _own : _others;
    HashSet<string> Solo(bool own) => own ? _soloOwn : _soloOthers;

    public SoundOverride Get(string evt, bool own) => Map(own).GetValueOrDefault(evt) ?? SoundOverride.None;

    /// <summary>Sets a sound's override (clamped); a default override removes it.</summary>
    public void Set(string evt, bool own, SoundOverride value)
    {
        if (string.IsNullOrEmpty(evt)) return;
        value = value.Clamped();
        var map = Map(own);
        if (value.IsDefault) { if (!map.Remove(evt)) return; }
        else if (map.TryGetValue(evt, out var old) && old == value) return;
        else map[evt] = value;
        Version++;
    }

    public void Reset(string evt, bool own)
    {
        bool changed = Map(own).Remove(evt);
        changed |= Solo(own).Remove(evt);
        if (changed) Version++;
    }

    public bool IsSolo(string evt, bool own) => Solo(own).Contains(evt);

    public void SetSolo(string evt, bool own, bool on)
    {
        if (on ? Solo(own).Add(evt) : Solo(own).Remove(evt)) Version++;
    }

    /// <summary>Clears every override, the global raw switch and the solos.</summary>
    public void Clear()
    {
        if (IsEmpty) return;
        _own.Clear(); _others.Clear(); _soloOwn.Clear(); _soloOthers.Clear(); _globalRaw = false;
        Version++;
    }

    /// <summary>Copies another config's overrides and global raw (not its solos).</summary>
    public void CopyFrom(SoundMixConfig other)
    {
        _own.Clear(); _others.Clear(); _soloOwn.Clear(); _soloOthers.Clear();
        foreach (var (k, v) in other._own) _own[k] = v;
        foreach (var (k, v) in other._others) _others[k] = v;
        _globalRaw = other._globalRaw;
        Version++;
    }

    public SoundMixConfig Clone() { var c = new SoundMixConfig(); c.CopyFrom(this); return c; }

    public IEnumerable<(string Event, bool Own, SoundOverride Value)> Entries() =>
        _own.Select(kv => (kv.Key, true, kv.Value)).Concat(_others.Select(kv => (kv.Key, false, kv.Value)));

    /// <summary>
    /// What a voice of this event and side gets now: null when nothing applies (empty config: no work beyond the emptiness test, no allocation); the shared <see cref="SoundMixEffect.RawOnly"/> when
    /// only the global raw switch does; else a small object, made once per voice start and again only after the config changes.
    /// </summary>
    public SoundMixEffect? Resolve(string evt, bool own)
    {
        if (IsEmpty) return null;
        Map(own).TryGetValue(evt, out var ov);
        bool soloing = AnySolo;
        if (ov is null && !soloing) return _globalRaw ? SoundMixEffect.RawOnly : null;
        bool silenced = (ov?.Mute ?? false) || (soloing && !Solo(own).Contains(evt));
        return new SoundMixEffect(silenced, ov?.VolumeDb ?? 0f, ov?.PitchSemitones ?? 0f, ov?.DistanceScale ?? 1f, _globalRaw || (ov?.Raw ?? false));
    }

    /// <summary>The number of changes the config makes: one per sound override side, plus one when global raw is on.</summary>
    public int ChangeCount => Count + (GlobalRaw ? 1 : 0);
}

/// <summary>
/// The mix config's JSON file (sound-mix defaults and exports). Shape: <c>{ "version": 1, "globalRaw": false, "sounds": { "own/play_sm_heavy_bolter_fire": { "volumeDb": 3, "pitchSemitones": 0,
/// "distanceScale": 1, "mute": false, "raw": false } }, "tuning": { ...the Groups tab's file... } }</c>; only the fields and entries that differ from the defaults are written.
/// </summary>
public static class SoundMixFile
{
    public const int Version = 1;
    public const string DefaultsFileName = "sound-mix-defaults.json";
    public const string OverridesFileName = "sound-mix.json";
    public const string ExportFolder = "sound-configs";

    /// <summary>The folder of the saved overrides and the exports: <c>%LOCALAPPDATA%\sm-reimpl</c> (never the install, never the repo).</summary>
    public static string Directory() => SoundTuningFile.Directory();

    public static string OverridesPath() => Path.Combine(Directory(), OverridesFileName);

    public static string ExportDirectory() => Path.Combine(Directory(), ExportFolder);

    /// <summary>A new export's path: <c>sound-configs\yyyyMMdd-HHmmss.json</c> (UTC), made unique when one exists.</summary>
    public static string NewExportPath(DateTime utcNow)
    {
        string dir = ExportDirectory(), stem = utcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), path = Path.Combine(dir, stem + ".json");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{stem}-{i}.json");
        return path;
    }

    public static string Key(string evt, bool own) => (own ? "own/" : "other/") + evt;

    public static string ToJson(SoundMixConfig config, SoundTuning? tuning = null, SoundTuning? tuningBaseline = null)
    {
        var sounds = new JsonObject();
        foreach (var (evt, own, v) in config.Entries().OrderBy(e => e.Event, StringComparer.OrdinalIgnoreCase).ThenByDescending(e => e.Own))
        {
            var o = new JsonObject();
            if (v.VolumeDb != 0f) o["volumeDb"] = Math.Round(v.VolumeDb, 2);
            if (v.PitchSemitones != 0f) o["pitchSemitones"] = Math.Round(v.PitchSemitones, 2);
            if (v.DistanceScale != 1f) o["distanceScale"] = Math.Round(v.DistanceScale, 3);
            if (v.Mute) o["mute"] = true;
            if (v.Raw) o["raw"] = true;
            sounds[Key(evt, own)] = o;
        }
        var root = new JsonObject { ["version"] = Version };
        if (config.GlobalRaw) root["globalRaw"] = true;
        root["sounds"] = sounds;
        if (tuning is not null && JsonNode.Parse(SoundTuningFile.ToJson(tuning, tuningBaseline)) is JsonObject t
            && ((t["groups"] as JsonObject)?.Count > 0 || tuning.ReverbScale != (tuningBaseline?.ReverbScale ?? 1f)))
            root["tuning"] = t;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Applies a file's text on top of <paramref name="into"/> (bad entries and values are skipped; a sound not in the file keeps its value). False for text that is not a mix file.</summary>
    public static bool TryApply(string json, SoundMixConfig into, SoundTuning? tuningInto = null)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); } catch (JsonException) { return false; }
        if (node is not JsonObject root || (root["sounds"] is null && root["tuning"] is null && root["globalRaw"] is null)) return false;
        if (root["globalRaw"] is JsonValue g && g.TryGetValue<bool>(out var raw)) into.GlobalRaw = raw;
        if (root["sounds"] is JsonObject sounds)
            foreach (var (key, value) in sounds)
            {
                bool own;
                string evt;
                if (key.StartsWith("own/", StringComparison.OrdinalIgnoreCase)) { own = true; evt = key[4..]; }
                else if (key.StartsWith("other/", StringComparison.OrdinalIgnoreCase)) { own = false; evt = key[6..]; }
                else continue;
                if (evt.Length == 0 || value is not JsonObject o) continue;
                float F(string name, float fallback) => o[name] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? (float)d : fallback;
                bool B(string name) => o[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                into.Set(evt, own, new SoundOverride(F("volumeDb", 0f), F("pitchSemitones", 0f), F("distanceScale", 1f), B("mute"), B("raw")));
            }
        if (tuningInto is not null && root["tuning"] is JsonObject tuning) SoundTuningFile.TryApply(tuning.ToJsonString(), tuningInto);
        return true;
    }

    /// <summary>
    /// The shipped defaults (the first existing path of <paramref name="defaultsPaths"/>; its tuning section goes into <paramref name="tuningDefaults"/>) and the current mix: the owner's saved file when
    /// it exists and is valid (a complete snapshot, so a sound he reset stays reset even if the defaults set it), else the defaults. A missing or broken file is skipped; the note says what was read.
    /// </summary>
    public static (SoundMixConfig Defaults, SoundMixConfig Current, string Note) Load(IEnumerable<string> defaultsPaths, string? overridesPath, SoundTuning? tuningDefaults = null)
    {
        var defaults = new SoundMixConfig();
        string note = "";
        foreach (var path in defaultsPaths)
        {
            if (!File.Exists(path)) continue;
            try { note = TryApply(File.ReadAllText(path), defaults, tuningDefaults) ? "shipped sound mix" : "shipped sound mix unreadable, ignored"; }
            catch (IOException) { note = "shipped sound mix unreadable, ignored"; }
            break;
        }
        var current = defaults.Clone();
        if (overridesPath is not null && File.Exists(overridesPath))
        {
            try
            {
                var mine = new SoundMixConfig();
                if (TryApply(File.ReadAllText(overridesPath), mine)) { current = mine; note += (note.Length > 0 ? " + " : "") + "your sound mix"; }
                else note += "; your sound mix file is not valid (ignored)";
            }
            catch (IOException) { note += "; your sound mix file could not be read"; }
        }
        return (defaults, current, note);
    }

    /// <summary>Writes the config (and the Groups tab's tuning) to <paramref name="path"/>, creating its folder.</summary>
    public static void Write(string path, SoundMixConfig config, SoundTuning? tuning = null)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson(config, tuning));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// The changes from <paramref name="before"/> to <paramref name="after"/> in plain words, one line each (the lead shows the owner these before a file ships as the default). Empty when nothing differs.
    /// </summary>
    public static List<string> Describe(SoundMixConfig before, SoundTuning? beforeTuning, SoundMixConfig after, SoundTuning? afterTuning)
    {
        var lines = new List<string>();
        if (before.GlobalRaw != after.GlobalRaw) lines.Add(after.GlobalRaw ? "Raw mode on for every sound: no filters, effects or processing of ours (see the docs for exactly what it skips)." : "Raw mode off for every sound.");
        var keys = before.Entries().Concat(after.Entries()).Select(e => (e.Event, e.Own)).Distinct().OrderBy(k => SoundMixCatalog.Classify(k.Event).Group).ThenBy(k => SoundMixCatalog.Classify(k.Event).Label, StringComparer.OrdinalIgnoreCase).ThenByDescending(k => k.Own);
        foreach (var (evt, own) in keys)
        {
            var a = before.Get(evt, own);
            var b = after.Get(evt, own);
            if (a == b) continue;
            var info = SoundMixCatalog.Classify(evt);
            string who = own ? "your own" : "other players'";
            var parts = new List<string>();
            if (a.VolumeDb != b.VolumeDb) parts.Add($"volume {Signed(b.VolumeDb)} dB" + (a.VolumeDb != 0f ? $" (was {Signed(a.VolumeDb)})" : ""));
            if (a.PitchSemitones != b.PitchSemitones) parts.Add(b.PitchSemitones == 0f ? "pitch back to normal" : $"pitch {Signed(b.PitchSemitones)} semitones" + (a.PitchSemitones != 0f ? $" (was {Signed(a.PitchSemitones)})" : ""));
            if (a.DistanceScale != b.DistanceScale) parts.Add(b.DistanceScale == 1f ? "hearing distance back to the game's" : $"heard {b.DistanceScale.ToString("0.##", CultureInfo.InvariantCulture)} times as far (the game's distance x{b.DistanceScale.ToString("0.##", CultureInfo.InvariantCulture)})");
            if (a.Mute != b.Mute) parts.Add(b.Mute ? "muted" : "no longer muted");
            if (a.Raw != b.Raw) parts.Add(b.Raw ? "plays raw" : "no longer raw");
            lines.Add($"[{SoundMixCatalog.GroupLabel(info.Group)}] {info.Label}, {who} sound ({evt}): " + (b.IsDefault && !a.IsDefault ? "back to the game's own (" + string.Join(", ", parts) + ")" : string.Join(", ", parts)));
        }
        if (beforeTuning is not null && afterTuning is not null)
        {
            if (beforeTuning.ReverbScale != afterTuning.ReverbScale) lines.Add($"[Groups] Map reverb send x{afterTuning.ReverbScale.ToString("0.##", CultureInfo.InvariantCulture)} (was x{beforeTuning.ReverbScale.ToString("0.##", CultureInfo.InvariantCulture)}).");
            for (int i = 0; i < SoundTuningGroups.All.Count; i++)
            {
                var a = beforeTuning[i];
                var b = afterTuning[i];
                if (a == b) continue;
                var g = SoundTuningGroups.All[i];
                var parts = new List<string>();
                if (a.VolumeDb != b.VolumeDb) parts.Add($"volume {Signed(b.VolumeDb)} dB (was {Signed(a.VolumeDb)})");
                if (a.CustomFalloff != b.CustomFalloff || (b.CustomFalloff && (a.MinDistance != b.MinDistance || a.MaxDistance != b.MaxDistance || a.RolloffDb != b.RolloffDb)))
                    parts.Add(b.CustomFalloff ? $"own fall-off: full within {b.MinDistance:0.#} m, {b.RolloffDb:0.#} dB per doubling, silent beyond {b.MaxDistance:0.#} m" : "the game's own fall-off");
                lines.Add($"[Groups] {g.Section} / {g.Label}: " + string.Join(", ", parts) + ".");
            }
        }
        return lines;
    }

    static string Signed(float v) => v.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture);
}
