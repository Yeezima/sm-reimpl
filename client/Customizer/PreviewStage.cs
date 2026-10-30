using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer.Model;
using SmContent;
using SmContent.Customization;
using SmSim;

namespace SmClient.Customizer;

/// <summary>
/// A camera framing of the preview (docs/customizer-design.md section 3): where to look, how far, from which side. Target and distance are fractions of the marine's
/// height (measured from the rig's bones, so the framing fits whatever the units are), x to the marine's right.
/// </summary>
public sealed record Framing(string Name, float X, float Y, float Distance, float AzimuthDeg, float ElevationDeg, bool Spin)
{
    // Azimuth 0 is the front of the marine (the camera on the marine's facing side), positive turns towards the marine's right.
    public static readonly Framing Body = new("Full body", 0f, 0.47f, 2.15f, 28f, 6f, true);
    public static readonly Framing Helmet = new("Helmet", -0.015f, 0.95f, 0.5f, 0f, 2f, false);
    public static readonly Framing Torso = new("Torso", 0f, 0.7f, 0.9f, 0f, 3f, false);
    public static readonly Framing LeftArm = new("Left arm", -0.15f, 0.66f, 0.8f, -55f, 2f, false);
    public static readonly Framing RightArm = new("Right arm", 0.15f, 0.66f, 0.8f, 55f, 2f, false);
    public static readonly Framing Legs = new("Legs", 0f, 0.28f, 1.0f, 0f, 3f, false);
    public static readonly Framing LeftPad = new("Left shoulder", -0.15f, 0.76f, 0.55f, -60f, 4f, false);
    public static readonly Framing RightPad = new("Right shoulder", 0.15f, 0.76f, 0.55f, 60f, 4f, false);
    public static readonly Framing Back = new("Backpack", 0f, 0.7f, 1.0f, 180f, 4f, false);

    /// <summary>The framing for an armour slot of the customizer.</summary>
    public static Framing ForSlot(string? slot) => slot switch
    {
        "head" => Helmet, "torso" => Torso, "leftshoulder" => LeftPad, "rightshoulder" => RightPad, "leftarm" => LeftArm, "rightarm" => RightArm,
        "leftleg" or "rightleg" => Legs, "backpack" => Back, _ => Body,
    };
}

/// <summary>
/// The preview's assets: the process-wide <see cref="AppearanceAssets"/> (T6: the shared rig, a piece cache and per-surface painted materials, so a new look costs a few
/// materials) and the weapon catalogue. The preview asks for a model through <see cref="AppearanceAssets.TryBuild"/> each frame until it is ready and never waits for a file.
/// </summary>
public sealed class PreviewAssets
{
    readonly CustomizerServices _services;
    AppearanceAssets? _appearance;
    WeaponAssets? _weapons;
    ResolvedAppearance? _warm;
    bool _warmDone;

    public PreviewAssets(CustomizerServices services) => _services = services;

    /// <summary>The shared appearance assets (created on first use on the main thread); custom emblems come from the profile's emblem files.</summary>
    public AppearanceAssets Appearance
    {
        get
        {
            if (_appearance is null)
            {
                var notes = new List<string>();
                _appearance = AppearanceAssets.Shared(_services.GameDir, notes);
                lock (_services.Notes) _services.Notes.AddRange(notes);
            }
            _appearance.EmblemSource = hash => _services.Store.LoadEmblem(hash);
            return _appearance;
        }
    }

    /// <summary>Drops the preview's models and weapons when the match starts (the match's own look cache takes over; the emblem source is left to it).</summary>
    public void Clear()
    {
        _weapons = null;
        _orkWeapons = null;
        _orkCatalogue = null;
        _warm = null;
        PreviewEyeGlow.Forget();
        if (_appearance is { } assets) { assets.EmblemSource = null; assets.Clear(); }
        _appearance = null;
    }

    /// <summary>
    /// Builds the default look of the first class while the menu is up, a piece or two per call (the first build pays the shader and material warm-up, about 50 ms,
    /// here instead of at the first preview). Returns true when finished; call once per frame.
    /// </summary>
    public bool WarmStep()
    {
        if (_warmDone || _services.Catalog is not { } catalog) return true;
        try
        {
            if (_warm is null)
            {
                string role = ProfileStore.FirstRole(catalog);
                _warm = AppearanceEdits.Resolve(catalog, AppearanceProfile.Empty("", CustomizationCatalog.SpaceMarine, "default"), role, null, Appearance.ResolveOptions().PadTakesDecal);
                if (_warm is null) { _warmDone = true; return true; }
                Appearance.Prefetch(_warm);
            }
            if (Appearance.TryBuild(_warm) is not null) _warmDone = true;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            lock (_services.Notes) _services.Notes.Add("preview warm-up: " + ex.Message);
            _warmDone = true;
        }
        return _warmDone;
    }

    /// <summary>The weapon models (every weapon, melee choice and jump pack of the configuration), created once.</summary>
    public WeaponAssets? Weapons(MarineAssets marine)
    {
        if (_weapons is not null || _services.Config is not { } config) return _weapons;
        try
        {
            var names = config.Weapons.Select(w => w.Name).Concat(config.Classes.SelectMany(c => c.MeleeChoices.Select(m => m.Wargear))).Concat(config.JumpPacks.Select(j => j.Name))
                .Where(n => !OrkClass.Wargear.Contains(n, StringComparer.OrdinalIgnoreCase));   // the Ork's are on his own rig (OrkWeapons)
            var notes = new List<string>();
            _weapons = WeaponAssets.Load(_services.GameDir, marine, names, notes);
            lock (_services.Notes) _services.Notes.AddRange(notes);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            lock (_services.Notes) _services.Notes.Add("preview weapons: " + ex.Message);
        }
        return _weapons;
    }

    // ---- the Ork Boy (ui/customizer-ork) ----

    OrkAssets? _orks;
    bool _orkAsked, _orkFailed, _orkWeaponsFailed;
    WeaponAssets? _orkWeapons;
    Task<(WeaponModels Catalogue, List<string> Notes, double Milliseconds)>? _orkCatalogue;

    /// <summary>Why the Ork preview could not be drawn, or null.</summary>
    public string? OrkError { get; private set; }

    /// <summary>The Ork's weapons cannot be read (or his model cannot): the preview shows him without them.</summary>
    public bool OrkWeaponsFailed => _orkWeaponsFailed || _orkFailed;

    /// <summary>
    /// The Ork Boy's model in a look (<see cref="OrkAssets"/>, the process-wide caches the match uses too), or null while it is not ready: the first call only asks (the caller
    /// shows "Loading" for a frame), the next reads the boy's rig on the main thread (once per run), then the look's files are read on a worker (<see cref="OrkAssets.Prefetch"/>)
    /// and the model is built when they are in. Null for good, with <see cref="OrkError"/>, when the install has no Ork.
    /// </summary>
    public MarineAssets? TryBuildOrk(int look)
    {
        if (_orkFailed) return null;
        if (!_orkAsked) { _orkAsked = true; return null; }
        try
        {
            if (_orks is null)
            {
                var notes = new List<string>();
                _orks = OrkAssets.Shared(_services.GameDir, notes);
                lock (_services.Notes) _services.Notes.AddRange(notes);
            }
            string name = OrkLookRules.Names[Math.Clamp(look, 0, OrkLookRules.Names.Count - 1)];
            _orks.Prefetch(name);
            if (_orkCatalogue is null)
            {
                string dir = _services.GameDir;
                var bones = _orks.Rig.Rig.Skeleton.BoneNames;
                _orkCatalogue = Task.Run(() =>
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var notes = new List<string>();
                    var catalogue = WeaponModels.Load(dir, RigProfiles.OrkBoy, notes, RigProfiles.OrkBoy.DefaultWargear, bones);
                    return (catalogue, notes, watch.Elapsed.TotalMilliseconds);
                });
            }
            return _orks.IsReady(name) ? _orks.Build(name) : null;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            _orkFailed = true;
            OrkError = ex.Message;
            lock (_services.Notes) _services.Notes.Add("preview ork: " + ex.Message);
            return null;
        }
    }

    /// <summary>The Ork's weapon models on his own bones once the worker has read their catalogue; null before (the preview tries again each frame) or when they cannot be read.</summary>
    public WeaponAssets? OrkWeapons()
    {
        if (_orkWeapons is not null || _orkWeaponsFailed || _orks is null || _orkCatalogue is not { IsCompleted: true } load) return _orkWeapons;
        try
        {
            var (catalogue, notes, ms) = load.GetAwaiter().GetResult();
            _orkWeapons = WeaponAssets.FromCatalogue(_services.GameDir, _orks.Rig.Rig.Skeleton.BoneNames, catalogue, notes, ms);
            lock (_services.Notes) _services.Notes.AddRange(notes);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            _orkWeaponsFailed = true;   // the Ork is shown without weapons
            lock (_services.Notes) _services.Notes.Add("preview ork weapons: " + (ex is AggregateException a ? a.GetBaseException().Message : ex.Message));
        }
        return _orkWeapons;
    }
}

/// <summary>What the preview shows besides the framing: a class on a race with its chosen weapons, and a look.</summary>
/// <param name="OrkLook">The Ork Boy's look (role <see cref="OrkLoadout.Role"/>; ui/customizer-ork): an index of <see cref="OrkLookRules.Names"/>, null = automatic (drawn as the first look).</param>
public sealed record PreviewSubject(string Race, string Role, CustomClass? Custom, AppearanceProfile? Look, int? OrkLook = null)
{
    public bool IsOrk => OrkLoadout.IsOrk(Role);
}

/// <summary>
/// The 3D preview (docs/customizer-design.md section 3): a SubViewport of its own with the shared marine rig, the class's weapons in hand, the full-body framing
/// and the data-independent close framings of the customizer. Today it draws the chapter paint of the look (the preset or predefined set); the
/// per-part armour, colours, badges and lens are the appearance renderer's (T6) and plug in where <see cref="PreviewAssets.Marine"/> is called. Mouse drag
/// rotates, the wheel zooms, Left/Right/Up/Down do the same from the keyboard, Home resets; the right stick rotates. "Reduce motion" cuts the camera moves.
/// <para>ui/customizer-ork (the owner's requests of 2026-10-29): every class stands in the bolt pistol's relaxed idle (<see cref="RigClipTable.PistolRelaxedIdle"/>;
/// the class's pistol in the hand, the primary stowed: <see cref="HoldsPistol"/>), the figure never turns by itself (the turntable's 18 degrees a second are gone: "spins automatically ... when
/// choosing colors, which can be annoying"; only the player's drag, keys or stick turn it), and the Ork Boy is drawn on his own rig in his own idle with his
/// shoota in hand (<see cref="PreviewAssets.TryBuildOrk"/>).</para>
/// </summary>
public sealed partial class PreviewStage : SubViewportContainer
{
    readonly CustomizerServices _services;
    readonly SubViewport _viewport;
    readonly Node3D _turntable = new() { Name = "Turntable" };
    readonly Camera3D _camera = new() { Fov = 32f, Near = 0.05f, Far = 60f };
    readonly Label _status;
    CharacterViewer? _character;
    WeaponRig? _rig;
    PreviewSubject? _subject;
    Framing _framing = Framing.Body;
    string _shownKey = "";
    string _weaponKey = "";
    bool _dirty = true;
    float _yaw, _zoom = 1f;
    // Smoothed camera.
    Vector3 _target = new(0f, 1.1f, 0f);
    float _distance = 4.5f, _azimuth = Framing.Body.AzimuthDeg, _elevation = Framing.Body.ElevationDeg;
    float _height = 2.3f;   // the marine's height in the rig's units (measured when the character is shown)
    bool _snapCamera = true;
    double _lastUserRotation = -10;
    double _time;

    /// <summary>Seconds since the player last turned the figure (drag, keys, stick; test aid): nothing else turns it.</summary>
    public double SinceUserTurn => _time - _lastUserRotation;

    /// <summary>Shown in the log and screenshot name: what the preview draws now.</summary>
    public string Description { get; private set; } = "";
    /// <summary>A note shown on the preview (what is not drawn yet).</summary>
    public string Caption { get; set; } = "";
    /// <summary>The preview has its assets and has drawn the subject.</summary>
    public bool Loaded => _character is not null && !_dirty && !_orkWeaponsPending && (_subject?.Race != CustomizationCatalog.Chaos || _services.InMatch || _eye is { Settled: true });

    /// <summary>The preview shows the Ork Boy (ui/customizer-ork; test aid).</summary>
    public bool ShowsOrk => _subject?.IsOrk == true && _shownKey.StartsWith("ork|", StringComparison.Ordinal);

    /// <summary>The figure's turn in degrees (test aid: it must stay put unless the player turns it).</summary>
    public float Yaw => _yaw;

    public PreviewStage(CustomizerServices services)
    {
        _services = services;
        Stretch = true;
        FocusMode = FocusModeEnum.All;
        CustomMinimumSize = new Vector2(400, 300);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        _viewport = new SubViewport { OwnWorld3D = true, TransparentBg = false, HandleInputLocally = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        RenderQuality.Apply(_viewport);   // a SubViewport does not inherit the project's anti-aliasing
        AddChild(_viewport);
        _status = UiKit.Text("", UiKit.BodySize, UiKit.White, HorizontalAlignment.Center);
        _status.SetAnchorsPreset(LayoutPreset.Center);
        _status.GrowHorizontal = GrowDirection.Both;
        _status.GrowVertical = GrowDirection.Both;
        AddChild(_status);
        var focus = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = UiKit.White, DrawCenter = false };
        focus.SetBorderWidthAll(3);
        AddThemeStyleboxOverride("focus", focus);
        BuildScene();
    }

    void BuildScene()
    {
        _viewport.AddChild(_turntable);
        _viewport.AddChild(_camera);
        var sun = new DirectionalLight3D { LightEnergy = 1.25f, ShadowEnabled = true, RotationDegrees = new Vector3(-42f, -28f, 0f) };
        _viewport.AddChild(sun);
        var fill = new DirectionalLight3D { LightEnergy = 0.35f, RotationDegrees = new Vector3(-15f, 150f, 0f), LightColor = new Color(0.75f, 0.82f, 1f) };
        _viewport.AddChild(fill);
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.045f, 0.05f, 0.06f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.5f, 0.52f, 0.58f),
            AmbientLightEnergy = 0.55f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
        };
        _viewport.AddChild(new WorldEnvironment { Environment = environment });
        // A low dark plinth the marine stands on (grounds the figure; it is not part of the turntable).
        var plinth = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.85f, BottomRadius = 0.9f, Height = 0.06f, RadialSegments = 48 },
            Position = new Vector3(0f, -0.03f, 0f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.1f, 0.11f), Roughness = 0.8f, Metallic = 0.2f },
        };
        _viewport.AddChild(plinth);
    }

    // ---- what to show ----

    public void SetSubject(PreviewSubject subject)
    {
        _subject = subject;
        _dirty = true;
    }

    /// <summary><c>--preview-frame x,y,distance,azimuth,elevation</c> (test aid): every framing is this one (fractions of the marine's height).</summary>
    static readonly Framing? ForcedFraming = ParseForced();

    /// <summary>
    /// The turn that makes the idle pose face the camera (the idle clips turn the body towards the aiming side): <see cref="PoseYaw"/> of the shown pose;
    /// <c>--preview-base-yaw</c> overrides it (test aid).
    /// </summary>
    float BaseYaw => ForcedBaseYaw ?? _poseYaw;
    static readonly float? ForcedBaseYaw = ParseBaseYaw();
    float _poseYaw;

    /// <summary>
    /// The bolt pistol's relaxed idle stands turned about 35 degrees to the marine's left (measured on screenshots, ui/customizer-ork; ours): the figure is turned back by that so the
    /// full-body framing shows him from the front quarter as the old relaxed idle did. The Ork's idle needs none.
    /// </summary>
    public const float PistolIdleYaw = 35f;

    static float? ParseBaseYaw()
    {
        var args = OS.GetCmdlineUserArgs();
        int at = Array.FindIndex(args, a => a == "--preview-base-yaw");
        return at >= 0 && at + 1 < args.Length && float.TryParse(args[at + 1], System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    static Framing? ParseForced()
    {
        var args = OS.GetCmdlineUserArgs();
        int at = Array.FindIndex(args, a => a == "--preview-frame");
        if (at < 0 || at + 1 >= args.Length) return null;
        var v = args[at + 1].Split(',').Select(t => float.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f).ToArray();
        return v.Length == 5 ? new Framing("forced", v[0], v[1], v[2], v[3], v[4], false) : null;
    }

    public void SetFraming(Framing framing)
    {
        framing = ForcedFraming ?? framing;
        if (_framing == framing) return;
        _framing = framing;
        _zoom = 1f;
        if (!framing.Spin) _yaw = 0f;   // close framings look at the part from the named side, not from wherever the turntable stopped
        if (UiKit.ReduceMotion) _snapCamera = true;
    }

    public Framing CurrentFraming => _framing;

    public void ResetView()
    {
        _yaw = 0f; _zoom = 1f; _lastUserRotation = -10;
        if (UiKit.ReduceMotion) _snapCamera = true;
    }

    /// <summary>Turns the camera at once (screenshots and tests).</summary>
    public void SnapCamera() => _snapCamera = true;

    // ---- the frame ----

    public override void _Ready()
    {
        VisibilityChanged += ApplyVisibility;
        ApplyVisibility();
    }

    /// <summary>A hidden stage (the other screen's preview) neither renders nor loads anything.</summary>
    void ApplyVisibility() => _viewport.RenderTargetUpdateMode = IsVisibleInTree() ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;

    public override void _EnterTree() => ApplyVisibility();

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree()) return;
        _time += delta;
        if (_services.Ready) Rebuild();
        else _status.Text = _services.Failed ? "The preview needs the game install: " + _services.Error : "Loading...";
        if (_orkWeaponsPending && !_dirty) ConfigureOrkWeapons();
        UpdateRig((float)delta);
        UpdateCamera((float)delta);
        UpdateEyeGlow(delta);
    }

    // ---- the Chaos eye glow (owner's request 2026-10-28: it follows the lens colour; PreviewEyeGlow) ----

    PreviewEyeGlow? _eye;
    string? _deployed;
    (byte R, byte G, byte B)? _lens;

    /// <summary>
    /// The wargear the preview holds in hand: the melee weapon for the classes that always hold it, else the pistol when the pose is the bolt pistol's idle
    /// (ui/customizer-ork) or there is no primary, else the primary.
    /// </summary>
    string? DeployedWargear(string? primary, string? pistol, string? melee) => _rig is { AlwaysMelee: true } ? melee : _holdPistol ? pistol ?? primary : primary ?? pistol;

    /// <summary>
    /// ui/customizer-ork (the lead's review): in the bolt pistol's idle the class's pistol (the bolt pistol, or the plasma pistol of a class that carries it) is in the hand
    /// and the primary stowed on its stow marker, as after a switch to the pistol; backpack-mounted weapons and jump packs stay as they are.
    /// </summary>
    bool HoldsPistol(string? primary, string? pistol) => primary is null || (pistol is not null && IdleClip == RigClipTable.PistolRelaxedIdle);

    /// <summary>The slot the preview's rig holds (test aid): Pistol or Ranged.</summary>
    public WeaponSlot? HeldSlot => _rig is null ? null : _holdPistol ? WeaponSlot.Pistol : WeaponSlot.Ranged;

    /// <summary>The wargear in the preview's hand (test aid).</summary>
    public string? HeldWargear => _deployed;

    void UpdateEyeGlow(double delta)
    {
        if (_subject is null || _character is null || !_services.Ready) return;
        // A Space Marine preview, or the in-match picker (the match has its own effects): no glow, and none left on the stage from the last frame.
        if (_subject.Race != CustomizationCatalog.Chaos || _services.InMatch) { _eye?.Clear(delta, _camera); return; }
        _eye ??= new PreviewEyeGlow(_viewport);
        _eye.Update(delta, _camera, _character, _services, _deployed, EyeGlow.TintOf(_lens));
    }

    /// <summary>The worn jump pack's primary colour as drawn now (test aid: the shader value of its first painted surface), or null without a painted pack.</summary>
    public Color? PackPrimaryColour => _rig?.PackPrimaryColour;
    /// <summary>The pack paint the preview applies now (the backpack's of the look shown), or null.</summary>
    public SmContent.Customization.ResolvedPaint? PackPaint => _rig?.PackPaint;

    /// <summary>The glow's state (test aid): the effects alive and the state they were started in.</summary>
    public int EyeGlowQuads => _eye?.Quads ?? 0;
    public string EyeGlowState => _eye is null ? "none" : $"{_eye.Live} effects ({(_eye.Key.Length == 0 ? "-" : _eye.Key)})";

    void Rebuild()
    {
        if (!_dirty || _subject is null) return;
        var s = _subject;
        var catalog = _services.Catalog!;
        var options = LoadoutRows.Options(catalog, s.Role, s.Race);
        if (options is null) { _status.Text = "No class data for this preview."; _dirty = false; return; }
        if (!(s.IsOrk ? CreateOrk(s, options) : CreateModel(s, options))) return;   // not ready yet: the old model stays and "Loading" is shown; tried again next frame
        if (s.IsOrk) ConfigureOrkWeapons(); else ConfigureWeapons(options, s);
        _status.Visible = false;
        Description = $"{options.DisplayName} ({_shownKey})";
        _dirty = false;
    }

    /// <summary>
    /// The one place the preview's character is created: the subject's look resolved for its class (<see cref="AppearanceEdits.Resolve"/>, with the pad check that never waits
    /// for a file) and built by <see cref="AppearanceAssets.TryBuild"/>, which spreads the work over frames. The cache key is the look's wire code with the class and the carried
    /// weapons and perks, so a changed colour, piece, badge, emblem, lens or perk-dependent class piece builds again (the materials are shared, so it is cheap) and an unchanged one does not. Returns false while the model is not ready
    /// (the previous one stays on stage); false and no more tries when the armour cannot be built.
    /// </summary>
    bool CreateModel(PreviewSubject subject, ClassOptions options)
    {
        var catalog = _services.Catalog!;
        var assets = _services.Previews.Appearance;
        var look = subject.Look is { } own && own.Race == subject.Race ? own : AppearanceProfile.Empty("", subject.Race, "Default look");
        var c = subject.Custom;   // carried weapons and perks pick class pieces too (e.g. the Havoc's Iron Halo), so they are part of the key
        string key = options.Key + "|" + ProfileWire.ToCode(look, catalog) + "|" + string.Join(",", c?.Primary, c?.Pistol, c?.Perk(0), c?.Perk(1));
        if (key == _shownKey && _character is not null) return true;
        var resolved = AppearanceEdits.Resolve(catalog, look, subject.Role, subject.Custom, assets.ResolveOptions().PadTakesDecal);
        if (resolved is null) { _status.Text = "No class data for this preview."; _dirty = false; return false; }
        assets.Prefetch(resolved);
        var model = assets.TryBuild(resolved);
        if (model is null)
        {
            _status.Text = $"Loading {options.DisplayName}...";
            _status.SetAnchorsPreset(_character is null ? LayoutPreset.Center : LayoutPreset.CenterTop);   // over the old model: out of its way
            _status.Visible = true;
            return false;
        }
        ShowCharacter(model, RigProfiles.Marine.Clips.PreviewIdleCandidates);
        _lens = resolved.Lens;
        _shownKey = key;
        return true;
    }

    /// <summary>
    /// The Ork Boy (ui/customizer-ork): his look's model on his own rig (<see cref="PreviewAssets.TryBuildOrk"/>), in his own idle (<see cref="RigClipTable.OrkBoy"/>'s preview idle).
    /// He has no armour, paint or lens. Returns false while the model is not ready (the previous one stays and "Loading" is shown).
    /// </summary>
    bool CreateOrk(PreviewSubject subject, ClassOptions options)
    {
        int look = OrkLoadout.PreviewLook(subject.OrkLook);
        string key = "ork|" + look;
        if (key == _shownKey && _character is not null) return true;
        var model = _services.Previews.TryBuildOrk(look);
        if (model is null)
        {
            _status.Text = _services.Previews.OrkError is { } error ? "The Ork Boy cannot be drawn: " + error : $"Loading {options.DisplayName}...";
            _status.SetAnchorsPreset(_character is null ? LayoutPreset.Center : LayoutPreset.CenterTop);
            _status.Visible = true;
            if (_services.Previews.OrkError is not null) _dirty = false;
            return false;
        }
        ShowCharacter(model, RigProfiles.OrkBoy.Clips.PreviewIdleCandidates);
        _lens = null;
        _shownKey = key;
        return true;
    }

    // The Ork's weapons wait for their catalogue (read on a worker): tried each frame until they are on.
    bool _orkWeaponsPending;

    /// <summary>The Ork's shoota in hand, the slugga and choppa stowed (his idle is the two-handed gun's), from <see cref="PreviewAssets.OrkWeapons"/> once they are read.</summary>
    void ConfigureOrkWeapons()
    {
        _orkWeaponsPending = !_services.Previews.OrkWeaponsFailed;   // when they cannot be read the Ork stands without them
        if (_character?.Skeleton is null || _services.Previews.OrkWeapons() is not { } weapons) return;
        _orkWeaponsPending = false;
        _rig ??= _character.Weapons ??= new WeaponRig(_character.Skeleton, weapons);
        if (_rig.Faulted) return;
        try
        {
            string key = "ork|" + OrkWeaponMounts.Shoota;
            if (key == _weaponKey) return;
            _weaponKey = key;
            _rig.Configure((-1, 0, 0, OrkWeaponMounts.Choppa, null), OrkWeaponMounts.Shoota, OrkWeaponMounts.Slugga, OrkWeaponMounts.Choppa, [], alwaysMelee: false);
            _holdPistol = false;
            _deployed = OrkWeaponMounts.Shoota;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            _rig.Fault();
            lock (_services.Notes) _services.Notes.Add($"preview ork weapons: {ex.GetType().Name}: {ex.Message}");
        }
    }

    void ShowCharacter(MarineAssets assets, IReadOnlyList<string> idleCandidates)
    {
        if (_character is not null) { _turntable.RemoveChild(_character); _character.QueueFree(); }
        _rig = null;
        _deployed = null;
        _weaponKey = "";
        _orkWeaponsPending = false;
        _height = MeasureHeight(assets);
        string? idle = assets.FindAnimation(idleCandidates, "Idle");
        IdleClip = idle;
        _poseYaw = idle == RigClipTable.PistolRelaxedIdle ? PistolIdleYaw : 0f;
        _character = new CharacterViewer(assets, idle, gameSpaceRoot: true) { Name = "Preview" };
        _turntable.AddChild(_character);
        _turntable.Rotation = new Vector3(0f, Mathf.DegToRad(_yaw + BaseYaw), 0f);
    }

    /// <summary>The marine's height from the rig's reference pose (the highest bone plus the head's size).</summary>
    static float MeasureHeight(MarineAssets assets)
    {
        var skeleton = assets.Model.Rig.Skeleton;
        float max = 0f;
        foreach (var m in skeleton.ModelSpace(skeleton.ReferencePose)) max = Mathf.Max(max, m.M43);
        return max > 0.5f && max < 6f ? max * 1.08f : 2.3f;
    }

    void ConfigureWeapons(ClassOptions options, PreviewSubject s)
    {
        if (_character?.Skeleton is null || _services.Config is not { } config || _services.Catalog is not { } catalog) return;
        var assets = _character.Assets;
        if (assets is null || _services.Previews.Weapons(assets) is not { } weaponAssets) return;
        _rig ??= _character.Weapons ??= new WeaponRig(_character.Skeleton, weaponAssets);
        if (_rig.Faulted) return;
        try
        {
            var team = ProfileWire.SideOf(s.Race);
            var choice = s.Custom is null ? ProfileWire.DefaultLoadout(s.Role, config, team) : ProfileWire.ToLoadout(s.Custom, config, team);
            int cls = choice.Class;
            if (cls < 0) return;
            var loadout = Loadouts.ClassOf(config, cls);
            string? Weapon(int index, WeaponSlot slot, int fallback) =>
                index >= 0 && index < Loadouts.Choices(config, cls, slot).Count ? config.Weapons[Loadouts.Choices(config, cls, slot)[index]].Name
                : fallback >= 0 && fallback < config.Weapons.Count ? config.Weapons[fallback].Name : null;
            string? primary = Weapon(choice.Primary, WeaponSlot.Ranged, loadout?.Primary ?? -1);
            string? pistol = Weapon(choice.Pistol, WeaponSlot.Pistol, loadout?.Pistol ?? -1);
            string? melee = loadout is null || loadout.MeleeChoices.Count == 0 ? null
                : choice.Melee >= 0 && choice.Melee < loadout.MeleeChoices.Count ? loadout.MeleeChoices[choice.Melee].Wargear : loadout.MeleeChoices[0].Wargear;
            var jump = Loadouts.JumpPack(config, cls);
            string key = $"{cls}|{primary}|{pistol}|{melee}|{jump?.Name}";
            if (key == _weaponKey) return;
            _weaponKey = key;
            _rig.Configure((cls, primary?.GetHashCode() ?? 0, pistol?.GetHashCode() ?? 0, melee, key), primary, pistol, melee,
                jump is null ? Array.Empty<string?>() : new string?[] { jump.Name }, jump is not null);
            _holdPistol = HoldsPistol(primary, pistol);
            _deployed = DeployedWargear(primary, pistol, melee);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            _rig.Fault();
            lock (_services.Notes) _services.Notes.Add($"preview weapons: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The idle clip the shown character plays (test aid and log): the bolt pistol's relaxed idle for every marine class, the boy's own idle for the Ork.</summary>
    public string? IdleClip { get; private set; }

    bool _holdPistol;

    void UpdateRig(float delta)
    {
        if (_rig is null || _rig.Faulted) return;
        try
        {
            _rig.SetPackPaint(_character?.Assets?.PackPaint);   // a colour change builds a new character for the new look; the jump pack takes its backpack paint (WornPackPaint)
            _rig.Update(_holdPistol ? WeaponSlot.Pistol : WeaponSlot.Ranged, false, true, delta, 0.5f);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { _rig.Fault(); }
    }

    void UpdateCamera(float delta)
    {
        // No turntable (ui/customizer-ork, the owner's request: the figure turning by itself while colours are chosen was annoying): it turns only when the player turns it.
        float stick = Input.GetJoyAxis(0, JoyAxis.RightX);
        if (Mathf.Abs(stick) > 0.25f && IsVisibleInTree()) { _yaw += stick * 120f * delta; _lastUserRotation = _time; }
        _turntable.Rotation = new Vector3(0f, Mathf.DegToRad(_yaw + BaseYaw), 0f);

        float distance = _framing.Distance * _height * _zoom;
        var wanted = new Vector3(_framing.X * _height, _framing.Y * _height, 0f);
        if (_snapCamera || UiKit.ReduceMotion)
        {
            _target = wanted; _distance = distance; _azimuth = _framing.AzimuthDeg; _elevation = _framing.ElevationDeg;
            _snapCamera = false;
        }
        else
        {
            // Ease-out over about 0.35 s.
            float k = 1f - Mathf.Exp(-delta * 9f);
            _target = _target.Lerp(wanted, k);
            _distance = Mathf.Lerp(_distance, distance, k);
            _azimuth = Mathf.Lerp(_azimuth, _framing.AzimuthDeg, k);
            _elevation = Mathf.Lerp(_elevation, _framing.ElevationDeg, k);
        }
        float az = Mathf.DegToRad(_azimuth), el = Mathf.DegToRad(_elevation);
        // The marine faces -Z: azimuth 0 puts the camera at -Z; positive azimuth moves it towards +X (the marine's right).
        var offset = new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), -Mathf.Cos(az) * Mathf.Cos(el)) * _distance;
        _camera.Position = _target + offset;
        _camera.LookAt(_target, Vector3.Up);
    }

    // ---- input ----

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion { ButtonMask: var mask } motion && (mask & MouseButtonMask.Left) != 0)
        {
            _yaw += motion.Relative.X * 0.5f;
            _lastUserRotation = _time;
            AcceptEvent();
        }
        else if (e is InputEventMouseButton { Pressed: true } button)
        {
            if (button.ButtonIndex == MouseButton.WheelUp) { Zoom(0.9f); AcceptEvent(); }
            else if (button.ButtonIndex == MouseButton.WheelDown) { Zoom(1.1f); AcceptEvent(); }
            else if (button.ButtonIndex == MouseButton.Left) GrabFocus();
        }
        else if (e is InputEventKey { Pressed: true } key && HasFocus())
        {
            switch (key.PhysicalKeycode)
            {
                case Key.Left: _yaw -= 12f; _lastUserRotation = _time; AcceptEvent(); break;
                case Key.Right: _yaw += 12f; _lastUserRotation = _time; AcceptEvent(); break;
                case Key.Up: Zoom(0.8f); AcceptEvent(); break;
                case Key.Down: Zoom(1.25f); AcceptEvent(); break;
                case Key.Home: ResetView(); AcceptEvent(); break;
            }
        }
    }

    void Zoom(float factor) => _zoom = Mathf.Clamp(_zoom * factor, 0.55f, 1.6f);
}
