using SmFormats.Baf;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>
/// One camera state (<c>data/packages/camera_curve_settings.oppc</c>, e.g. <c>exploration_default_mp</c>,
/// <c>aim_sm_rifle_mp</c>). Values in the files' units: <see cref="FieldOfView"/> in degrees (vertical or horizontal is not
/// stated; vertical is the interpretation, spec/behavior.md); <see cref="AnimationCurve"/> names a <see cref="CameraRig"/>
/// animation; <see cref="PitchMinimum"/>/<see cref="PitchMaximum"/> in the rig's degrees (0 down, 90 level, 180 up);
/// <see cref="BlendInTime"/>, <see cref="RelaxDelay"/> and <see cref="TargetLockOnSeconds"/> in seconds;
/// <see cref="RelaxSpeed"/>, <see cref="AimBlendSpeedScaler"/> and <see cref="BlendInDistance"/> in original units (meaning not established).
/// </summary>
public sealed record CameraSet(string Name, float FieldOfView, string AnimationCurve, float PitchMinimum, float PitchMaximum, float BlendInTime,
    float RelaxDelay, float RelaxSpeed, float AimBlendSpeedScaler, float BlendInDistance, float TargetLockOnSeconds);

/// <summary>
/// Look-speed tuning (<c>data/packages/camera_input_tuning.oppc</c>): <see cref="SpeedMouse"/>, <see cref="SpeedHorizontal"/>
/// and <see cref="SpeedVertical"/> in original units (not established; the stick speeds are probably degrees per second at
/// full deflection, the mouse speed a relative scale). Controller (gamepad feature): <see cref="StickMapping"/> is the file's <c>input_mapping</c>
/// (raw stick 0..1 to the mapped request 0..1, linear between the points; the Xbox table, not <c>input_mapping_ps3</c>) and <see cref="StickDamping"/> the
/// <c>request_damping</c> (time, to cap), null when the file has none.
/// </summary>
public sealed record InputTuning(string Name, float SpeedMouse, float SpeedHorizontal, float SpeedVertical,
    IReadOnlyList<(float From, float To)>? StickMapping = null, IReadOnlyList<(float From, float To)>? StickDamping = null);

/// <summary>
/// What a weapon's wargear says about the camera (<c>camera_data/entry/camera_data</c> of the default entry): the exploration
/// and aim camera sets, and the input tuning used with each, through the weapon's aim-assist sets (<c>auto_aim</c> for
/// exploration, <c>auto_aim_zoom</c> for aim mode; each names a <c>camera_input_tuning_set</c>). <see cref="Evidence"/> says
/// where the values came from ("wargear ..." or "fallback: ...").
/// <para>
/// <see cref="ZoomFovs"/> (degrees, same convention as <see cref="CameraSet.FieldOfView"/>): the wargear's <c>zoom_levels</c> (<c>fov_1</c> .. <c>fov_&lt;num_zoom_levels&gt;</c>) of the
/// default entry, only when the aim camera set has <c>aim_use_zoom_levels</c> true (stalker bolter, lascannon); the first is the field of view a scoped player has on pressing aim, the
/// next ones are the levels the zoom-cycle binding steps through. Null: the aim set's own <c>field_of_view</c> is the zoom.
/// </para>
/// </summary>
public sealed record WeaponCamera(string Weapon, CameraSet Exploration, CameraSet Aim, InputTuning ExplorationInput, InputTuning AimInput,
    string? AutoAim, string? AutoAimZoom, string Evidence, IReadOnlyList<float>? ZoomFovs = null);

/// <summary>
/// Camera and look tuning read from the install (read-only): every camera state in <c>camera_curve_settings</c>, every
/// set in <c>camera_input_tuning</c>, the <c>camera_input_tuning_set</c> of every aim-assist set in <c>auto_aim_info</c>
/// (aim assist itself is recorded in spec/behavior.md, not implemented), and the camera data of the multiplayer weapons
/// (<c>sm_mp_*</c>, <c>csm_mp_*</c> in <c>wargear.oppc</c>). <see cref="For"/> answers per weapon.
/// </summary>
public sealed class CameraTuning
{
    public const string CurvePackage = "data/packages/camera_curve_settings.oppc";
    public const string InputPackage = "data/packages/camera_input_tuning.oppc";
    public const string AutoAimPackage = "data/packages/auto_aim_info.oppc";
    public const string WargearPackage = "data/packages/wargear.oppc";
    /// <summary>The fallback for a weapon without camera data: the bolter's sets (sm_mp_ranged_bolter on the install).</summary>
    public const string DefaultExploration = "exploration_default_mp", DefaultAim = "aim_sm_rifle_mp", DefaultInput = "default", DefaultAimInput = "aim";

    readonly Dictionary<string, CameraSet> _sets;
    readonly Dictionary<string, InputTuning> _inputs;
    readonly Dictionary<string, WeaponCamera> _weapons;
    readonly List<string> _notes;

    public IReadOnlyDictionary<string, CameraSet> Sets => _sets;
    public IReadOnlyDictionary<string, InputTuning> Inputs => _inputs;
    /// <summary>The multiplayer weapons' camera data, by wargear name.</summary>
    public IReadOnlyDictionary<string, WeaponCamera> Weapons => _weapons;
    public IReadOnlyList<string> Notes => _notes;

    CameraTuning(Dictionary<string, CameraSet> sets, Dictionary<string, InputTuning> inputs, Dictionary<string, WeaponCamera> weapons, List<string> notes)
    {
        _sets = sets; _inputs = inputs; _weapons = weapons; _notes = notes;
    }

    /// <summary>A table of <c>mapping</c> entries with two float fields as points (null when absent or empty); the order of the file is kept.</summary>
    static IReadOnlyList<(float From, float To)>? ReadCurve(BafTable? table, string from, string to)
    {
        if (table is not { } t) return null;
        var points = new List<(float, float)>();
        foreach (var e in t.Entries)
            if (e.Type == BafType.Table && e.Name == "mapping" && e.AsTable() is var m && m.Float(from) is { } a && m.Float(to) is { } b && float.IsFinite(a) && float.IsFinite(b))
                points.Add((a, b));
        return points.Count > 0 ? points : null;
    }

    /// <summary>Reads everything from the install (read-only).</summary>
    public static CameraTuning Load(string gameDir)
    {
        static Dictionary<string, BafDocument> ReadAll(string path, Func<string, bool> want)
        {
            var docs = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
            using var archive = ObpkArchive.Open(path);
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && want(e.FileName), (e, d) => docs[e.FileName] = BafDocument.Read(d));
            return docs;
        }
        var curves = ReadAll(Path.Combine(gameDir, CurvePackage), _ => true);
        var inputs = ReadAll(Path.Combine(gameDir, InputPackage), _ => true);
        var autoAim = ReadAll(Path.Combine(gameDir, AutoAimPackage), _ => true);
        var wargear = ReadAll(Path.Combine(gameDir, WargearPackage), IsMultiplayerWargear);
        return Read(curves, inputs, autoAim, wargear);
    }

    /// <summary>Multiplayer wargear names (<c>sm_mp_</c>, <c>csm_mp_</c>).</summary>
    public static bool IsMultiplayerWargear(string name) =>
        name.StartsWith("sm_mp_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("csm_mp_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the tuning from attribute files by name: camera states, input tuning sets, aim-assist sets (names as in their
    /// packages, e.g. <c>multiplayer\sm_auto_aim_bolter_mp</c> or <c>sm_auto_aim_bolter_mp</c>), and wargear.
    /// </summary>
    public static CameraTuning Read(IReadOnlyDictionary<string, BafDocument> curves, IReadOnlyDictionary<string, BafDocument> inputs,
        IReadOnlyDictionary<string, BafDocument> autoAim, IReadOnlyDictionary<string, BafDocument> wargear)
    {
        var notes = new List<string>();
        var sets = new Dictionary<string, CameraSet>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, doc) in curves)
            if (ReadSet(name, doc, notes) is { } set) sets[name] = set;
        var tunings = new Dictionary<string, InputTuning>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, doc) in inputs)
        {
            var r = doc.Root;
            float? mouse = r.Float("speed_mouse"), horizontal = r.Float("speed_horizontal"), vertical = r.Float("speed_vertical");
            if (mouse is { } m && float.IsFinite(m) && m > 0f)
                tunings[name] = new InputTuning(name, m, horizontal is { } h && float.IsFinite(h) ? h : float.NaN, vertical is { } v && float.IsFinite(v) ? v : float.NaN,
                    ReadCurve(r.Table("input_mapping"), "from (raw stick)", "to (mapped request)"), ReadCurve(r.Table("request_damping"), "from (time)", "to (cap)"));
            else notes.Add($"input tuning {name}: no positive speed_mouse; skipped");
        }
        // Aim-assist set → input tuning set (by the aim-assist file name, without folders).
        var assistInput = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, doc) in autoAim)
            if (doc.Root.String("camera_input_tuning_set") is { Length: > 0 } set && Leaf(name) is { } key && Leaf(set) is { } value) assistInput[key] = value;

        var weapons = new Dictionary<string, WeaponCamera>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, doc) in wargear.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var entries = doc.Root.Table("camera_data")?.Entries.Where(e => e.Type == BafType.Table && e.Name == "entry").Select(e => e.AsTable()).ToList();
            if (entries is not { Count: > 0 }) continue;
            var entry = entries.Cast<BafTable?>().FirstOrDefault(t => t!.Value.Table("required_upgrade_name/required_list") is not { } list || !list.Entries.Any());
            if (entry?.Table("camera_data") is not { } data)
            {
                notes.Add($"{name}: every camera_data entry needs an upgrade; not read");
                continue;
            }
            if (entries.Count > 1) notes.Add($"{name}: {entries.Count - 1} upgrade variant(s) of camera_data not read (e.g. the snap upgrade)");
            string? exploration = Leaf(data.String("exploration_camera")), aim = Leaf(data.String("aim_camera"));
            string? assist = Leaf(data.String("auto_aim")), assistZoom = Leaf(data.String("auto_aim_zoom"));
            if (exploration is null || aim is null || !sets.TryGetValue(exploration, out var e1) || !sets.TryGetValue(aim, out var a1))
            {
                notes.Add($"{name}: camera sets '{exploration}'/'{aim}' not found; not read");
                continue;
            }
            var (input, inputFrom) = Tuning(assist, DefaultInput);
            var (aimInput, aimInputFrom) = Tuning(assistZoom, DefaultAimInput);
            var zoom = curves.TryGetValue(aim, out var aimDoc) && aimDoc.Root["aim_use_zoom_levels"] is { Type: BafType.Bool } flag && flag.AsBool() ? ZoomLevels(data) : null;
            weapons[name] = new WeaponCamera(name, e1, a1, input, aimInput, assist, assistZoom,
                $"wargear {name} camera_data: {exploration}, {aim}; input {inputFrom}, aim input {aimInputFrom}"
                + (zoom is null ? "" : $"; zoom_levels {string.Join(" / ", zoom.Select(z => z.ToString(System.Globalization.CultureInfo.InvariantCulture)))}"), zoom);
        }
        return new CameraTuning(sets, tunings, weapons, notes);

        (InputTuning Tuning, string From) Tuning(string? assist, string fallback)
        {
            if (assist is not null && assistInput.TryGetValue(assist, out var set) && tunings.TryGetValue(set, out var t)) return (t, $"{set} (via {assist})");
            var f = tunings.TryGetValue(fallback, out var d) ? d : new InputTuning(fallback, float.NaN, float.NaN, float.NaN);
            return (f, $"{fallback} (fallback: {(assist is null or "" ? "no aim-assist set" : $"'{assist}' names no input tuning")})");
        }
    }

    /// <summary>
    /// The wargear's <c>zoom_levels</c> of the entry without a required upgrade: <c>fov_1</c> .. <c>fov_n</c>, n = <c>num_zoom_levels</c> (1 to 3; the table is a template instance
    /// named <c>_base_templates\zoom_levels</c>). Null when absent, implausible or the count is missing.
    /// </summary>
    static List<float>? ZoomLevels(BafTable data)
    {
        if (data.Table("zoom_levels") is not { } holder) return null;
        foreach (var e in holder.Entries.Where(e => e.Type == BafType.Table))
        {
            var t = e.AsTable();
            if (t.Table("required_upgrade_name/required_list") is { } list && list.Entries.Any()) continue;
            if (t["num_zoom_levels"] is not { Type: BafType.Int } n || n.Raw is < 1 or > 3) continue;
            var levels = new List<float>();
            for (int i = 1; i <= n.Raw; i++)
                if (t.Float($"fov_{i}") is { } f && float.IsFinite(f) && f > 0f && f < 180f) levels.Add(f); else return null;
            return levels;
        }
        return null;
    }

    static CameraSet? ReadSet(string name, BafDocument doc, List<string> notes)
    {
        var r = doc.Root;
        float F(string key) => r.Float(key) is { } v && float.IsFinite(v) ? v : float.NaN;
        string? curve = r.String("animation_curve");
        float fov = F("field_of_view"), min = F("pitch_minimum"), max = F("pitch_maximum");
        if (curve is null || !(fov > 0f && fov < 180f) || !(min >= 0f && max <= 180f && min < max))
        {
            notes.Add($"camera set {name}: missing or implausible field_of_view, animation_curve or pitch range; skipped");
            return null;
        }
        return new CameraSet(name, fov, curve, min, max, F("blend_in_time"), F("relax_delay"), F("relax_speed"), F("aim_blend_speed_scaler"),
            F("blend_in_distance"), F("target_lock_on_seconds"));
    }

    /// <summary>The last part of a reference like <c>camera_curve_settings\aim_sm_rifle_mp</c> (null or empty: null).</summary>
    static string? Leaf(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        int cut = reference.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? reference : reference[(cut + 1)..];
    }

    /// <summary>
    /// The camera for a weapon (wargear name, e.g. <c>sm_mp_ranged_bolter</c>). A weapon without camera data (or an unknown
    /// name) gets the bolter's sets (<see cref="DefaultExploration"/>, <see cref="DefaultAim"/>, inputs <see cref="DefaultInput"/>
    /// and <see cref="DefaultAimInput"/>; provisional fallback, said in <see cref="WeaponCamera.Evidence"/>); null only when even
    /// those sets are missing.
    /// </summary>
    public WeaponCamera? For(string weaponName)
    {
        if (_weapons.TryGetValue(weaponName, out var w)) return w;
        if (!_sets.TryGetValue(DefaultExploration, out var exploration) || !_sets.TryGetValue(DefaultAim, out var aim)) return null;
        var input = _inputs.TryGetValue(DefaultInput, out var i1) ? i1 : new InputTuning(DefaultInput, float.NaN, float.NaN, float.NaN);
        var aimInput = _inputs.TryGetValue(DefaultAimInput, out var i2) ? i2 : new InputTuning(DefaultAimInput, float.NaN, float.NaN, float.NaN);
        return new WeaponCamera(weaponName, exploration, aim, input, aimInput, null, null,
            $"fallback: '{weaponName}' has no camera data; the bolter's sets ({DefaultExploration}, {DefaultAim}, {DefaultInput}, {DefaultAimInput})");
    }
}
