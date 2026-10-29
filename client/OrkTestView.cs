using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Godot;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// Ork O3 test aid: the Ork Boy with its original meshes, textures and animations on a turntable, without a server (<see cref="OrkAssets"/> builds it,
/// <see cref="CharacterViewer"/> shows it). <c>--ork-test</c> with
/// <c>--look boy_ranged|boy_melee|boy_ranged_pro|boy_melee_pro|boy_ardboy|all</c> (default boy_ranged; "all" lines the looks up),
/// <c>--anim &lt;clip or alias&gt;</c> (default: the clip table's idle; aliases idle, run, sprint, reload, recoil, hop, hopr, choppa, heavy, toss, taunt, death, or any clip name; <c>--list-clips</c> prints them),
/// <c>--time &lt;s&gt;</c> (hold the clip there; the default with shots is 0.4 s), <c>--shots 0,180</c> (turntable angles, 0 = front) saved to <c>--out</c>
/// (default <c>%LOCALAPPDATA%\sm-reimpl\cache\ork-o3</c>; never inside the repository or the install) as <c>&lt;--name&gt;_&lt;angle&gt;.png</c>, then quit;
/// <c>--times t0,t1,..</c> steps through the clip (shot i = angle i, time i, cycling) saved as <c>&lt;name&gt;_a&lt;angle&gt;_t&lt;time&gt;.png</c>;
/// <c>--closeup head|torso|&lt;bone&gt;</c>, <c>--bones</c> (draw the skeleton), <c>--wounded</c> (only the dismemberment blocks: the two-layer vertex-blend material on a skinned mesh),
/// <c>--turn &lt;deg/s&gt;</c>, <c>--cam-target/--cam-dir x,y,z</c>, <c>--cam-dist m</c>, <c>--cam-fov deg</c>.
/// <c>--ragdoll</c> drops the Ork as a ragdoll (<see cref="RagdollModel"/> of the Ork rig, <see cref="Ragdoll"/>) after <c>--ragdoll-delay</c> s (default 1) with
/// <c>--kick x,y,z</c> m/s (game space, default 0,3,1), prints whether it rested without exploding and saves <c>&lt;name&gt;_fall.png</c> and <c>&lt;name&gt;_rest.png</c>.
/// <c>--weapons</c> (O6) gives the Ork its original weapons (<see cref="WeaponRig"/>, mounts of <see cref="OrkWeaponMounts"/>); <c>--weapon-state shoota|slugga|choppa|all</c>
/// (default all) picks what is in hand (the others are stowed); with <c>--shots a,b</c> every state is shot at every angle as <c>&lt;name&gt;_&lt;state&gt;_&lt;angle&gt;.png</c>
/// (default folder <c>ork-o6</c>); without shots the first state shows. <c>toss</c> shows the stikkbomb in the right hand part-way through the toss clip;
/// <c>--follow &lt;bone&gt;</c> keeps the camera on a bone (<c>--cam-dist</c>, <c>--cam-dir</c>); <c>--stow</c> tries other stow spots (see <see cref="StowOverride"/>).
/// **Placeholder demonstration** around original assets.
/// </summary>
public partial class OrkTestView : Node3D
{
    readonly string _gameDir;
    readonly Dictionary<string, string?> _args;
    readonly List<CharacterViewer> _characters = new();
    readonly List<(Skeleton3D Skeleton, ImmediateMesh Lines)> _bones = new();
    Camera3D? _camera;
    float[] _shots = [], _times = [];
    int _nextShot, _waitFrames, _shotCount;
    string _outDir = "", _name = "ork";
    float _turn;
    bool _quit;

    // --weapons (Ork O6)
    WeaponAssets? _weaponAssets;
    readonly List<WeaponRig> _weaponRigs = new();
    WeaponState[] _weaponStates = [];
    int _weaponState = -1;

    /// <summary>What the Ork carries in a screenshot: the slot in hand, whether the choppa is out, and the clip that suits it (1HM stance for the one-handed weapons).</summary>
    sealed record WeaponState(string Name, WeaponSlot Slot, bool MeleeOut, string Clip, bool Grenade = false, float? Time = null);

    // --ragdoll
    Node3D? _gameSpace;
    CharacterViewer? _source;
    RagdollDef? _ragdollDef;
    Ragdoll? _ragdoll;
    double _clock, _ragdollDelay = 1.0;
    Vector3 _kick = new(0, 3, 1);
    bool _fallShot, _reported;
    Vector3 _start;
    float _maxExcursion, _lowest = float.MaxValue;
    string _lowestBone = "";
    bool _nan;
    float[] _restLength = [];

    public OrkTestView(string gameDir, Dictionary<string, string?> args) { _gameDir = gameDir; _args = args; }
    public OrkTestView() : this("", new Dictionary<string, string?>()) { }

    static float F(string? text, float fallback) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    static Vector3? Triple(string? text) => text?.Split(',') is { Length: 3 } p && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
        && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z) ? new Vector3(x, y, z) : null;

    /// <summary>A comma list of numbers; an entry that is not a number is reported and left out.</summary>
    static float[] Floats(string? text, string what)
    {
        var list = new List<float>();
        foreach (var x in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v)) list.Add(v);
            else GD.PrintErr($"ork test: --{what}: '{x}' is not a number; left out");
        }
        return list.ToArray();
    }

    /// <summary>The clips the aliases name on the Ork rig: the clip table's (<see cref="RigClipTable.OrkBoy"/>) and the graphs' (reload, side hops, choppa combo, sprint, taunt).</summary>
    static IReadOnlyList<(string Alias, string Clip)> ClipAliases(RigClipTable table) =>
    [
        ("idle", table.IdleCandidates.FirstOrDefault() ?? ""),
        ("run", table.DefaultAnimation),
        ("sprint", "BOY_1HM_NAV_Sprint_F_01"),
        ("reload", "BOY_2HR_RNG_Reload_S_01"),
        ("recoil", table.RecoilClip ?? ""),
        ("hop", "BOY_2HR_NAV_Evade4m_L_01"),
        ("hopr", "BOY_2HR_NAV_Evade4m_R_01"),
        ("choppa", "BOY_1HM_MEL_Cmbo_01_Atk01_1m"),
        ("heavy", "BOY_1HM_MEL_Cmbo_01_Hvy01_1m"),
        ("sprint-attack", "BOY_1HM_MEL_Atk_Sprint_01"),
        ("toss", table.TossClip ?? ""),
        ("taunt", "BOY_1HM_MEL_Cmbo_01_Taunt_01"),
        ("death", table.DeathClips.FirstOrDefault() ?? ""),
    ];

    string ClipFor(RigAssets rig, string? wanted)
    {
        var table = RigProfiles.OrkBoy.Clips;
        string alias = (wanted ?? "idle").Trim();
        string? clip = ClipAliases(table).FirstOrDefault(a => a.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)).Clip;
        if (alias.Equals("idle", StringComparison.OrdinalIgnoreCase)) return rig.FindAnimation(table.IdleCandidates, "Idle") ?? table.IdleCandidates.First();
        return string.IsNullOrEmpty(clip) ? alias : clip;
    }

    public override void _Ready()
    {
        var notes = new List<string>();
        _name = _args.GetValueOrDefault("name") ?? "ork";
        if (_name.Length == 0 || _name.IndexOfAny(['/', '\\', ':']) >= 0 || _name.Contains("..", StringComparison.Ordinal) || _name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        { GD.PrintErr($"ork test: --name '{_name}' has a path separator, '..' or an invalid character; 'ork' is used"); _name = "ork"; }
        _outDir = _args.GetValueOrDefault("out") ?? System.IO.Path.Combine(GameInstall.DataRoot, "cache", _args.ContainsKey("weapons") ? "ork-o6" : "ork-o3");
        _turn = F(_args.GetValueOrDefault("turn"), 0f);
        _quit = _args.ContainsKey("quit");
        _shots = Floats(_args.GetValueOrDefault("shots"), "shots");
        _times = Floats(_args.GetValueOrDefault("times"), "times");
        _shotCount = Math.Max(_shots.Length, _times.Length);
        var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
        string fullOut = System.IO.Path.GetFullPath(_outDir);
        if (SmFormats.InstallPaths.IsWithin(fullOut, repoRoot) || SmFormats.InstallPaths.IsWithin(fullOut, _gameDir))
        { GD.PrintErr($"ork test: {_outDir} is inside the repository or the game folder; screenshots refused"); _shotCount = 0; }

        try
        {
            var total = Stopwatch.StartNew();
            var orks = OrkAssets.Shared(_gameDir, notes);
            GD.Print($"ork test: rig {RigProfiles.OrkBoy.Entry}: {orks.Rig.Rig.Skeleton.BoneNames.Count} bones, {orks.Rig.Rig.AnimationNames.Count} animations");
            string clip = ClipFor(orks.Rig, _args.GetValueOrDefault("anim"));
            if (_args.ContainsKey("list-clips")) ListClips(orks.Rig);
            bool wounded = _args.ContainsKey("wounded");
            string lookArg = _args.GetValueOrDefault("look") ?? "boy_ranged";
            var looks = lookArg.Equals("all", StringComparison.OrdinalIgnoreCase) ? OrkAssets.Looks : [OrkAssets.LookName(lookArg) ?? throw new ArgumentException($"unknown look '{lookArg}' (known: {string.Join(", ", OrkAssets.Looks)}, all)")];

            foreach (var look in looks) orks.Prefetch(look, wounded);   // the files of every look are read on workers while the first is built
            if (_args.ContainsKey("ragdoll")) { SetUpRagdoll(orks, looks[0], clip, notes); }
            else
            {
                float? hold = _args.ContainsKey("time") ? F(_args.GetValueOrDefault("time"), 0f) : _shotCount > 0 ? (_times.Length > 0 ? _times[0] : 0.4f) : null;
                for (int i = 0; i < looks.Count; i++)
                {
                    var watch = Stopwatch.StartNew();
                    var assets = orks.Build(looks[i], wounded);
                    GD.Print($"ork test: look '{looks[i]}'{(wounded ? " (wounded blocks only)" : "")}: {OrkAssets.Describe(assets)}; built in {watch.ElapsedMilliseconds} ms");
                    var viewer = new CharacterViewer(assets, clip, gameSpaceRoot: true) { Name = "Ork_" + looks[i], Position = new Vector3(((looks.Count - 1) / 2f - i) * 1.5f, 0, 0) };
                    AddChild(viewer);
                    if (hold is { } t) viewer.Pose(clip, t, 0f);
                    else viewer.Play(clip, 0f);
                    _characters.Add(viewer);
                    if (_args.ContainsKey("bones")) AddBones(viewer);
                    if (_args.ContainsKey("weapons")) AddWeapons(viewer, assets, notes);
                }
                if (_args.ContainsKey("weapons")) SetUpWeaponStates(orks.Rig, clip);
                GD.Print($"ork test: clip '{clip}' {(hold is { } h ? $"held at {h:F2} s" : "looping")}; {(_characters[0].ClipTime is { } ct ? $"length {ct.Duration:F2} s" : "clip not found")}");
            }
            AddEnvironment(orks.Rig, looks.Count);
            GD.Print($"ork test: ready in {total.ElapsedMilliseconds} ms; GPU memory: textures {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed) / 1048576.0:F1} MB");
        }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException))
        {
            GD.PrintErr("ork test: " + ex);
            GetTree().Quit(1);
            return;
        }
        foreach (var n in notes.Concat(OrkAssets.Shared(_gameDir).Notes).Distinct()) GD.Print("  note: " + n);
        _waitFrames = 12;
        if (_shotCount > 0 && _shots.Length > 0) foreach (var c in _characters) c.RotationDegrees = new Vector3(0, _shots[0], 0);
    }

    // ---- weapons (Ork O6) ----

    /// <summary>Gives the Ork its three weapons: the shoota (primary), the slugga (pistol) and the choppa (melee), mounted from <see cref="RigProfiles.OrkBoy"/>'s mounts.</summary>
    void AddWeapons(CharacterViewer viewer, MarineAssets assets, List<string> notes)
    {
        if (viewer.Skeleton is not { } skeleton) { GD.PrintErr("ork test: --weapons: the Ork has no skeleton"); return; }
        _weaponAssets ??= WeaponAssets.Load(_gameDir, assets, StowOverride(RigProfiles.OrkBoy), [OrkWeaponMounts.Slugga, OrkWeaponMounts.Shoota, OrkWeaponMounts.Choppa, OrkWeaponMounts.Stikkbomb], notes);
        var rig = new WeaponRig(skeleton, _weaponAssets);
        rig.Configure((6, 0, 0, OrkWeaponMounts.Choppa, null), OrkWeaponMounts.Shoota, OrkWeaponMounts.Slugga, OrkWeaponMounts.Choppa, [], alwaysMelee: false, grenade: OrkWeaponMounts.Stikkbomb);
        viewer.Weapons = rig;
        _weaponRigs.Add(rig);
        GD.Print($"ork test: weapons: {string.Join(", ", rig.Description)}; catalogue {_weaponAssets.CatalogueMilliseconds:F0} ms, models {_weaponAssets.LoadMilliseconds:F0} ms");
        foreach (var attachment in OrkWeaponMounts.Table.Keys.Select(k => _weaponAssets.Catalogue.Held(k)).OfType<WeaponAttachment>())
            GD.Print($"  {attachment.Wargear}: {attachment.VisualName}; hand {attachment.Wield?.HandleNode}->{attachment.Wield?.Bone}; stowed {(attachment.Stow is { } s ? $"{s.BodyNode} on {s.Bone}" : "hidden")}{(attachment.TwoHanded ? "; two-handed" : "")}");
    }

    /// <summary>
    /// Tuning aid: <c>--stow shoota=ox,oy,oz,lx,ly,lz,ux,uy,uz[,bone[,scale]];slugga=...</c> replaces a weapon's stow spot (<see cref="RigStowSpot"/>: the handle's offset in metres, then the
    /// directions of the weapon's length and up, in the bone's frame: on the spine and pelvis X up, Y forward, Z left) to find values by screenshot; the profile's own table is used otherwise.
    /// </summary>
    RigProfile StowOverride(RigProfile profile)
    {
        string? text = _args.GetValueOrDefault("stow");
        if (string.IsNullOrWhiteSpace(text)) return profile;
        var table = profile.WeaponMounts.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = entry.Split('=', 2);
            string key = kv[0].ToLowerInvariant() switch { "shoota" => OrkWeaponMounts.Shoota, "slugga" => OrkWeaponMounts.Slugga, "choppa" => OrkWeaponMounts.Choppa, var other => other };
            var f = (kv.Length > 1 ? kv[1] : "").Split(',', StringSplitOptions.TrimEntries);
            if (!table.TryGetValue(key, out var old) || f.Length < 9 || f.Take(9).Any(x => !float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            { GD.PrintErr($"ork test: --stow '{entry}' is not <weapon>=ox,oy,oz,lx,ly,lz,ux,uy,uz[,bone[,scale]]; ignored"); continue; }
            float[] n = f.Take(9).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            string bone = f.Length > 9 && f[9].Length > 0 ? f[9] : old.Stow?.Bone ?? "RIG_Spine3";
            float scale = f.Length > 10 && float.TryParse(f[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var sc) ? sc : 1f;
            table[key] = old with { Stow = new RigStowSpot(bone, new System.Numerics.Vector3(n[0], n[1], n[2]), new System.Numerics.Vector3(n[3], n[4], n[5]), new System.Numerics.Vector3(n[6], n[7], n[8]), scale, "test:" + key) };
        }
        return profile with { WeaponMounts = table };
    }

    void SetUpWeaponStates(RigAssets rig, string clip)
    {
        string wanted = _args.GetValueOrDefault("weapon-state") ?? "all";
        bool userClip = _args.ContainsKey("anim");
        string oneHanded = rig.FindAnimation(["BOY_1HM_MEL_Idle_Combat_03", "BOY_1HM_MEL_Idle_Combat_01"], "Idle_Combat") ?? clip;
        var all = new[]
        {
            new WeaponState("shoota", WeaponSlot.Ranged, false, clip),
            new WeaponState("slugga", WeaponSlot.Pistol, false, userClip ? clip : oneHanded),
            new WeaponState("choppa", WeaponSlot.Pistol, true, userClip ? clip : oneHanded),
            // The throw: the stikkbomb in the right hand part-way through the toss clip (the shoota in the other hand's place, as a throw ends the ready posture).
            new WeaponState("toss", WeaponSlot.Ranged, false, userClip ? clip : RigProfiles.OrkBoy.Clips.TossClip ?? clip, Grenade: true, Time: 0.75f),
        };
        _weaponStates = wanted.Equals("all", StringComparison.OrdinalIgnoreCase) ? all
            : wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => all.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                .Where(s => s is not null).Select(s => s!).ToArray();
        if (_weaponStates.Length == 0) { GD.PrintErr($"ork test: --weapon-state '{wanted}' names no state (shoota, slugga, choppa, toss, all); shoota is used"); _weaponStates = [all[0]]; }
        if (_shotCount > 0) _shotCount = _weaponStates.Length * Math.Max(1, _shots.Length);
        ApplyWeaponState(0);
    }

    /// <summary>Puts the Ork in a state: the slot in hand, the choppa out or away, and a stance that suits (the rigs apply it at once: no switch delay in a still).</summary>
    void ApplyWeaponState(int index)
    {
        if (_weaponStates.Length == 0 || index == _weaponState) return;
        _weaponState = index;
        var state = _weaponStates[index];
        float hold = _args.ContainsKey("time") ? F(_args.GetValueOrDefault("time"), 0f) : state.Time ?? 0.4f;
        foreach (var (rig, viewer) in _weaponRigs.Zip(_characters))
        {
            rig.SetGrenadeHeld(state.Grenade);
            rig.Update(state.Slot, state.MeleeOut, alive: true, delta: 1f, switchTime: 0f);
            viewer.Pose(state.Clip, hold, 0f);
        }
        GD.Print($"ork test: weapon state '{state.Name}' ({state.Clip}): {string.Join(", ", _weaponRigs.SelectMany(r => r.Description).Distinct())}");
    }

    void ListClips(RigAssets rig)
    {
        foreach (var (alias, clip) in ClipAliases(RigProfiles.OrkBoy.Clips))
        {
            var a = string.IsNullOrEmpty(clip) ? null : rig.Animation(clip);
            GD.Print($"  {alias,-14} {clip,-34} {(a is null ? "NOT IN THE RIG" : $"{a.Animation.Duration:F2} s")}");
        }
    }

    // ---- bones ----

    void AddBones(CharacterViewer viewer)
    {
        if (viewer.Skeleton is not { } skeleton) return;
        var lines = new ImmediateMesh();
        var material = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true, AlbedoColor = new Color(1f, 0.85f, 0.1f) };
        skeleton.AddChild(new MeshInstance3D { Name = "Bones", Mesh = lines, MaterialOverride = material });
        _bones.Add((skeleton, lines));
    }

    void DrawBones()
    {
        foreach (var (skeleton, lines) in _bones)
        {
            if (!IsInstanceValid(skeleton)) continue;
            lines.ClearSurfaces();
            lines.SurfaceBegin(Mesh.PrimitiveType.Lines);
            for (int i = 0; i < skeleton.GetBoneCount(); i++)
            {
                int parent = skeleton.GetBoneParent(i);
                if (parent < 0) continue;
                lines.SurfaceAddVertex(skeleton.GetBoneGlobalPose(parent).Origin);
                lines.SurfaceAddVertex(skeleton.GetBoneGlobalPose(i).Origin);
            }
            lines.SurfaceEnd();
        }
    }

    // ---- ragdoll ----

    static readonly Basis GameToGodot = new(new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0));

    void SetUpRagdoll(OrkAssets orks, string look, string clip, List<string> notes)
    {
        _ragdollDelay = F(_args.GetValueOrDefault("ragdoll-delay"), 1f);
        if (Triple(_args.GetValueOrDefault("kick")) is { } kick) _kick = kick;
        var assets = orks.Build(look);
        // The world the corpse lies on: the plane y = 0 on the ragdolls' world layer (as the networked view's flat floor).
        var floor = new StaticBody3D { Name = "RagdollFloor", CollisionLayer = Ragdoll.WorldLayer, CollisionMask = 0 };
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D { Plane = new Plane(Vector3.Up, 0f) } });
        AddChild(floor);
        AddChild(new MeshInstance3D { Name = "FloorSlab", Mesh = new BoxMesh { Size = new Vector3(14f, 0.04f, 14f) }, Position = new Vector3(0, -0.02f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.33f, 0.35f), Roughness = 0.9f } });
        _gameSpace = new Node3D { Name = "GameSpace", Basis = GameToGodot };
        AddChild(_gameSpace);
        _source = new CharacterViewer(assets, clip, gameSpaceRoot: false) { Name = "Alive" };
        _gameSpace.AddChild(_source);
        _source.Pose(clip, 0.4f, 0f);
        var model = RagdollModel.Load(_gameDir, RigProfiles.OrkBoy, notes) ?? throw new InvalidOperationException("the Ork ragdoll could not be read");
        _ragdollDef = RagdollDef.Build(model, assets, notes) ?? throw new InvalidOperationException("the Ork ragdoll could not be built: " + string.Join("; ", notes));
        GD.Print($"ork test: ragdoll {_ragdollDef.Description}; focus body '{_ragdollDef.Bodies[_ragdollDef.FocusBody].Name}'");
        if (_args.ContainsKey("rest-seconds") && F(_args.GetValueOrDefault("rest-seconds"), 3f) is var rs) Ragdoll.RestSeconds = rs;
    }

    void StartRagdoll()
    {
        _ragdoll = new Ragdoll { Name = "OrkRagdoll", PlayerId = 1 };
        _gameSpace!.AddChild(_ragdoll);
        _ragdoll.Start(_ragdollDef!, _source!, 0, Vector3.Zero, _gameSpace.Basis * _kick);
        _source!.Visible = false;
        _start = _ragdoll.FocusPoint;
        if (_ragdoll.Character?.Skeleton is { } sk)
        {
            _restLength = new float[sk.GetBoneCount()];
            for (int b = 0; b < _restLength.Length; b++)
                _restLength[b] = sk.GetBoneParent(b) < 0 ? 0f : (sk.GetBoneGlobalPose(b).Origin - sk.GetBoneGlobalPose(sk.GetBoneParent(b)).Origin).Length();   // the pose it died in
        }
        GD.Print($"ork test: ragdoll started at {_clock:F2} s, kick {_kick.X:F1},{_kick.Y:F1},{_kick.Z:F1} m/s (game space), mass {_ragdollDef!.TotalMass:F0} kg");
    }

    void WatchRagdoll(double delta)
    {
        if (_ragdoll is null) { if (_clock >= _ragdollDelay) StartRagdoll(); return; }
        // The corpse moves: the camera follows it (the side view's direction, 3.2 m).
        if (_camera is not null && !_args.ContainsKey("cam-target"))
        {
            var focus = _ragdoll.FocusPoint;
            _camera.Position = focus + new Vector3(1f, 0.28f, 0.15f).Normalized() * 3.2f;
            _camera.LookAt(focus, Vector3.Up);
        }
        if (_ragdoll.Character?.Skeleton is not { } skeleton) return;
        var origin = skeleton.GlobalTransform;
        for (int b = 0; b < skeleton.GetBoneCount(); b++)
        {
            var p = origin * skeleton.GetBoneGlobalPose(b).Origin;
            if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z) || float.IsInfinity(p.Length())) { _nan = true; continue; }
            _maxExcursion = MathF.Max(_maxExcursion, new Vector3(p.X - _start.X, 0, p.Z - _start.Z).Length());
            if (_ragdollDef!.DriveOfBone[b] is not null && p.Y < _lowest) { _lowest = p.Y; _lowestBone = skeleton.GetBoneName(b); }   // the bones the bodies carry (not the props and helpers)
        }
        if (!_fallShot && _ragdoll.Age >= 0.45 && _ragdoll.Simulating) { _fallShot = true; Save("fall"); }
        if (!_reported && !_ragdoll.Simulating) Report();
        else if (!_reported && _ragdoll.Age > Ragdoll.MaxSeconds + 1) Report();
    }

    void Report()
    {
        _reported = true;
        var skeleton = _ragdoll!.Character!.Skeleton!;
        float worstStretch = 0f; string worstBone = "";
        for (int b = 0; b < skeleton.GetBoneCount(); b++)
        {
            int parent = skeleton.GetBoneParent(b);
            string bone = skeleton.GetBoneName(b);
            if (parent < 0 || _restLength[b] < 0.12f || bone.Contains("Prop") || bone.Contains("Interaction") || bone.Contains("LookAt") || bone.Contains("Face")) continue;   // helpers and short twist bones are not what bursts
            float now = (skeleton.GetBoneGlobalPose(b).Origin - skeleton.GetBoneGlobalPose(parent).Origin).Length();
            float ratio = now / _restLength[b];
            if (ratio is < 0.8f or > 1.25f) GD.Print($"  bone {bone}: {_restLength[b]:F2} m -> {now:F2} m ({ratio:F2}x)");
            if (MathF.Abs(ratio - 1f) > MathF.Abs(worstStretch - 1f) || worstBone.Length == 0) { worstStretch = ratio; worstBone = skeleton.GetBoneName(b); }
        }
        var chest = _ragdoll.FocusPoint;
        bool exploded = _nan || _maxExcursion > 8f || _lowest < -0.2f || worstStretch > 2.5f || worstStretch < 0.4f;
        GD.Print($"ork test: ragdoll {(_ragdoll.Simulating ? "still simulating after " + _ragdoll.Age.ToString("F1") + " s" : $"rested after {_ragdoll.SettledAfter:F1} s")}; " +
                 $"furthest bone {_maxExcursion:F2} m from where it died, lowest driven bone {_lowest:F2} m ({_lowestBone}), worst bone length (over 0.12 m) vs the pose it died in {worstStretch:F2}x ({worstBone}), NaN {(_nan ? "YES" : "none")}, " +
                 $"focus at {chest.X:F2},{chest.Y:F2},{chest.Z:F2}: {(exploded ? "EXPLODED" : "ok")}");
        _waitFrames = 10;
        _pendingRestShot = true;
    }

    bool _pendingRestShot;

    // ---- view ----

    void AddEnvironment(RigAssets rig, int count)
    {
        // Heights from the rig's reference pose (game space, z up): the head and the spine, for the framing.
        var skeleton = rig.Rig.Skeleton;
        var model = skeleton.ModelSpace(skeleton.ReferencePose);
        Vector3 Bone(string name)
        {
            int i = skeleton.BoneNames.ToList().FindIndex(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? Vector3.Zero : new Vector3(model[i].Translation.X, model[i].Translation.Z, -model[i].Translation.Y);   // game (x, y, z) -> Godot (x, z, -y)
        }
        float height = Bone("RIG_Head").Y + 0.3f;
        string closeup = _args.GetValueOrDefault("closeup") ?? "";
        float fov = F(_args.GetValueOrDefault("cam-fov"), 35f);
        var front = new Vector3(0, 0.12f, -1f);
        Vector3 target = new(0, height * 0.5f, 0);
        float distance = height * 1.75f, aspect = GetViewport().GetVisibleRect().Size.X / Math.Max(1f, GetViewport().GetVisibleRect().Size.Y);
        distance = MathF.Max(distance, (count * 1.5f) / (2f * MathF.Tan(Mathf.DegToRad(fov / 2f)) * aspect) * 1.05f);
        var direction = front;
        if (_args.ContainsKey("ragdoll")) { target = new Vector3(0, 0.45f, -0.7f); distance = 4.2f; direction = new Vector3(1f, 0.28f, 0.15f); }
        if (closeup.Length > 0)
        {
            (string bone, float d, float f) = closeup switch { "head" => ("RIG_Head", 0.9f, 30f), "torso" => ("RIG_Spine3", 2.0f, 35f), _ => (closeup, 1.2f, 30f) };
            target = Bone(bone); distance = d; fov = f;
        }
        target = Triple(_args.GetValueOrDefault("cam-target")) ?? target;
        direction = Triple(_args.GetValueOrDefault("cam-dir")) ?? direction;
        distance = F(_args.GetValueOrDefault("cam-dist"), distance);
        _camera = new Camera3D { Fov = fov, Near = 0.05f, Far = 60f, Current = true };
        AddChild(_camera);
        _camera.Position = target + direction.Normalized() * distance;
        _camera.LookAt(target, Vector3.Up);
        AddChild(new DirectionalLight3D { LightEnergy = 1.1f, ShadowEnabled = true, RotationDegrees = new Vector3(-40f, 205f, 0f) });
        AddChild(new DirectionalLight3D { LightEnergy = 0.45f, RotationDegrees = new Vector3(-20f, 30f, 0f) });
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.18f, 0.2f, 0.23f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.57f, 0.6f), AmbientLightEnergy = 0.7f,
            },
        });
    }

    string ShotName(int index)
    {
        float angle = _shots.Length > 0 ? _shots[index % _shots.Length] : 0f;
        if (_weaponStates.Length > 0) return $"{_name}_{_weaponStates[Math.Min(index / Math.Max(1, _shots.Length), _weaponStates.Length - 1)].Name}_{angle:0}";
        return _times.Length > 0 ? $"{_name}_a{angle:0}_t{_times[index % _times.Length]:0.00}" : $"{_name}_{angle:0}";
    }

    void Save(string suffix)
    {
        string path = System.IO.Path.Combine(_outDir, $"{_name}_{suffix}.png");
        SavePng(path);
    }

    void SavePng(string path)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print(err == Error.Ok ? $"screenshot -> {path}" : $"screenshot failed: {err}");
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        if (_args.GetValueOrDefault("follow") is { Length: > 0 } follow && _camera is not null && _characters.Count > 0 && _characters[0].Skeleton is { } fs && fs.FindBone(follow) is var fb and >= 0)
        {
            // --follow <bone>: the camera looks at that bone of the first Ork, --cam-dist away along --cam-dir (a close-up of a hand in a moving clip).
            var at = fs.GlobalTransform * fs.GetBoneGlobalPose(fb).Origin;
            _camera.Position = at + (Triple(_args.GetValueOrDefault("cam-dir")) ?? new Vector3(0, 0.12f, -1f)).Normalized() * F(_args.GetValueOrDefault("cam-dist"), 1.5f);
            _camera.LookAt(at, Vector3.Up);
        }
        if (_turn != 0f) foreach (var c in _characters) c.RotationDegrees += new Vector3(0, _turn * (float)delta, 0);
        if (_bones.Count > 0) DrawBones();
        if (_ragdollDef is not null)
        {
            if (_waitFrames > 0) { _waitFrames--; if (_waitFrames == 0 && _pendingRestShot) { Save("rest"); GetTree().Quit(0); } return; }
            WatchRagdoll(delta);
            return;
        }
        if (_waitFrames > 0) { _waitFrames--; return; }
        if (_shotCount == 0) { if (_quit) GetTree().Quit(0); return; }
        if (_nextShot == 0) GD.Print($"ork test: GPU memory after the first frames: textures {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed) / 1048576.0:F1} MB, buffers {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed) / 1048576.0:F1} MB");
        SavePng(System.IO.Path.Combine(_outDir, ShotName(_nextShot) + ".png"));
        if (++_nextShot >= _shotCount) { _shotCount = 0; GetTree().Quit(0); return; }
        if (_shots.Length > 0) foreach (var c in _characters) c.RotationDegrees = new Vector3(0, _shots[_nextShot % _shots.Length], 0);
        if (_weaponStates.Length > 0) ApplyWeaponState(Math.Min(_nextShot / Math.Max(1, _shots.Length), _weaponStates.Length - 1));
        else if (_times.Length > 0)
        {
            string clip = _characters.Count > 0 ? _characters[0].Playing ?? "" : "";
            foreach (var c in _characters) c.Pose(clip, _times[_nextShot % _times.Length], 0f);
        }
        _waitFrames = 3;
    }
}
