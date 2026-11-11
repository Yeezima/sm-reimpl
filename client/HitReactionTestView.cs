using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Hit reactions test aid (<c>--hit-reaction-test --hrt-case &lt;case&gt;</c>, 2026-11-11): a marine on a plain floor plays one case with the rules and data the networked view uses
/// (<see cref="FlinchRules"/>, <see cref="DeathRules"/> on the install's graphs, the flinch layer's weight curve and strength, the death clip held to its hand-over, then <see cref="Ragdoll"/> started with
/// the clip's motion and the killing blow's push), with a caption, then quits: for Movie Maker clips (<c>godot.cmd --windowed --write-movie f.avi --fixed-fps 30 -- --hit-reaction-test --hrt-case ...</c>).
/// Cases: <c>flinch-gunfire</c> (bolter hits: front left high, front right low, head front right), <c>flinch-light-melee</c> (three of the graph's random flinches), <c>flinch-heavy</c> (a stun strike's
/// reaction, the full-body clip, for comparison), <c>death-front</c> (a bolter shot from the front left), <c>death-side</c> (from the right), <c>death-back</c> (from behind), <c>death-head</c>,
/// <c>death-explosion</c> (a grenade in front), <c>death-melee</c>, <c>death-lascannon</c>, <c>death-sprint</c>, <c>death-jump</c> (the jump-pack death in mid-air, 2026-11-11: a bolter kill 9 m up in a
/// jump-pack flight, the hit clip, the fall loop to the floor, the floor clip and the ragdoll at its tag, with the jump death's effects at their times, <see cref="JumpDeathRun"/>; the camera follows the
/// body unless <c>--hrt-cam</c> is given). <c>--old-hit-reactions</c> gives the earlier rules (no clip motion at the hand-over).
/// <c>--hrt-cam x,y,z</c> / <c>--hrt-look x,y,z</c> place the camera (game space: x right of the marine, y ahead of it, z up).
/// </summary>
public partial class HitReactionTestView : Node3D
{
    readonly string _gameDir;
    readonly Dictionary<string, string?> _args;
    CharacterViewer? _character;
    UpperBodyBundle? _upper;
    RagdollDef? _ragdollDef;
    DeathTuning? _tuning;
    Node3D? _space;
    Label? _caption;
    string _case = "death-front";
    string? _idle;
    float _clock = -0.4f;
    const float HitAt = 0.5f;
    const float HoldCap = 1.5f;   // NetworkView.DeathHoldCap

    // Flinch cases: (when, clip, additive, peak, what).
    readonly List<(float At, FlinchClip Clip, float Peak, string What)> _flinches = new();
    (float At, string Clip, float Length, string What)? _reaction;
    // Death case.
    DeathClip? _death;
    MarineDeathKind _deathKind;
    Vector3 _blow;   // game space, unit
    float _push;
    string _deathWhat = "";
    Ragdoll? _ragdoll;
    Transform3D[]? _posePrev, _poseLast;
    float _posePrevAt = float.NaN, _poseLastAt = float.NaN, _end = 4f;
    // The jump-pack death in mid-air (death-jump): where and how fast the marine flies when the shot kills it (game space), the flight clip before, the run, the effects and the following camera.
    static readonly Vector3 JumpAt = new(0f, 0f, 9f), JumpVelocity = new(0f, 4f, 3f);
    const float JumpGravity = 20f, JumpMaxFall = 60f;   // SimConfig.Gravity, MaxFallSpeed (the networked view passes the match's)
    JumpDeathRun? _jump;
    string? _flight;
    FxPlayer? _fx;
    DeathSide? _deathSide;
    readonly List<(float Due, FxPlay Play)> _fxSteps = new();
    Camera3D? _camera;
    bool _followCamera;
    Vector3 _cameraFocus;
    string _jumpNotes = "";
    float _probeAt = float.NegativeInfinity;
    int[]? _bodyBones;

    public HitReactionTestView(string gameDir, Dictionary<string, string?> args) { _gameDir = gameDir; _args = args; }
    public HitReactionTestView() : this("", new Dictionary<string, string?>()) { }

    static float F(string? text, float fallback) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    static Vector3 V3(string? text, Vector3 fallback)
    {
        var p = text?.Split(',');
        return p is { Length: 3 } ? new Vector3(F(p[0], fallback.X), F(p[1], fallback.Y), F(p[2], fallback.Z)) : fallback;
    }
    static Vector3 ToGodot(Vector3 game) => new(game.X, game.Z, -game.Y);

    public override void _Ready()
    {
        _case = _args.GetValueOrDefault("hrt-case") ?? "death-front";
        var checker = Image.CreateEmpty(2, 2, false, Image.Format.Rgb8);
        checker.SetPixel(0, 0, new Color(0.34f, 0.35f, 0.37f)); checker.SetPixel(1, 1, new Color(0.34f, 0.35f, 0.37f));
        checker.SetPixel(1, 0, new Color(0.22f, 0.23f, 0.25f)); checker.SetPixel(0, 1, new Color(0.22f, 0.23f, 0.25f));
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(30, 30) },
            MaterialOverride = new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(checker), TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Uv1Scale = new Vector3(30, 30, 1) },
        });
        var floor = new StaticBody3D { Name = "RagdollFloor", CollisionLayer = Ragdoll.WorldLayer, CollisionMask = 0 };
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D { Plane = new Plane(Vector3.Up, 0f) } });
        AddChild(floor);
        AddChild(new DirectionalLight3D { LightEnergy = 1.2f, ShadowEnabled = true, RotationDegrees = new Vector3(-50, 30, 0) });
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.12f, 0.12f, 0.14f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.55f, 0.58f), AmbientLightEnergy = 0.9f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        });
        var camera = new Camera3D { Current = true, Fov = 50f, Near = 0.05f, Far = 100f };
        AddChild(camera);
        _camera = camera;
        _followCamera = _case == "death-jump" && !_args.ContainsKey("hrt-cam");
        bool death = _case.StartsWith("death", StringComparison.Ordinal);
        // Default: in front of the marine and to his right, a little above (deaths: further back, the body falls 1.5 m).
        // The blast death throws the body several metres back: a wider view.
        bool wide = _case == "death-explosion";
        camera.Position = ToGodot(V3(_args.GetValueOrDefault("hrt-cam"), wide ? new Vector3(6.5f, 1.5f, 2.6f) : death ? new Vector3(3.4f, 3.2f, 1.7f) : new Vector3(1.9f, 2.4f, 1.6f)));
        camera.LookAt(ToGodot(V3(_args.GetValueOrDefault("hrt-look"), wide ? new Vector3(0f, -2.5f, 0.6f) : death ? new Vector3(0f, 0.4f, 0.7f) : new Vector3(0f, 0f, 1.25f))), Vector3.Up);
        var ui = new CanvasLayer();
        AddChild(ui);
        _caption = new Label { Position = new Vector2(16, 12), Size = new Vector2(1200, 120), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _caption.AddThemeFontSizeOverride("font_size", 20);
        _caption.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
        _caption.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
        _caption.AddThemeConstantOverride("outline_size", 6);
        ui.AddChild(_caption);

        _space = new Node3D { Name = "GameSpace", Basis = new Basis(new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0)) };
        AddChild(_space);
        var notes = new List<string>();
        try
        {
            var assets = MarineAssets.Load(_gameDir, notes);
            _idle = assets.FindAnimation(["SM_NAV_IdleL_S_01", "SM_NAV_Idle_S_01", "SM_NAV_IdleR_S_01"], "Idle");
            _character = new CharacterViewer(assets, _idle ?? CharacterViewer.DefaultAnimation, gameSpaceRoot: false) { Name = "Marine" };
            _space.AddChild(_character);
            var weaponAssets = WeaponAssets.Load(_gameDir, assets, ["sm_mp_ranged_bolter", "sm_mp_pistol_bolt_pistol"], notes);
            var rig = new WeaponRig(_character.Skeleton ?? throw new InvalidOperationException("no skeleton"), weaponAssets);
            rig.Configure((0, 1, 1, null, null), "sm_mp_ranged_bolter", "sm_mp_pistol_bolt_pistol", null, [], alwaysMelee: false);
            rig.Update(WeaponSlot.Ranged, false, true, 0f, 0.6f);
            _character.Weapons = rig;
            _upper = UpperBodyBundle.Load(_gameDir);
            if (RagdollModel.Load(_gameDir, notes) is { } model) _ragdollDef = RagdollDef.Build(model, assets, notes);
            _tuning = DeathTuning.Load(_gameDir, notes);
            if (_case == "death-jump")
            {
                // The flight before the shot: the high set's forward clip aimed forward (SM_JMP_Zero_*F_Aim_F), posed by the motion's vertical angle as the networked view does (JumpFlightPose).
                var pattern = new System.Text.RegularExpressions.Regex(@"^SM_JMP_Zero_.*?F_Aim_F(_\d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                _flight = assets.Model.Rig.AnimationNames.Where(n => pattern.IsMatch(n) && !n.Contains("_TH_", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Length).FirstOrDefault(n => assets.Animation(n) is not null);
                LoadJumpDeathFx(notes);
            }
            Plan(_upper);
        }
        catch (Exception ex) { GD.PrintErr("hit reaction test: " + ex); }
        foreach (var n in notes) GD.Print("  note: " + n);
    }

    /// <summary>The case's script, with the networked view's rules.</summary>
    void Plan(UpperBodyBundle data)
    {
        FlinchSet Set(string weapon, out string name)
        {
            name = data.Flinch.SetOfReaction(data.ReactionOf(weapon)) ?? FlinchRules.SetFor(weapon, false);
            return data.Flinch.For(name) ?? data.Flinch.Sets.Values.First();
        }
        void Shot(float at, string weapon, bool head, bool behind, bool left, bool low, string what)
        {
            var set = Set(weapon, out var name);
            int slot = FlinchRules.Choose(set, head, behind, left, low);
            if (set.Slot(slot) is { } clip) _flinches.Add((at, clip, WeightFeel.FlinchRangedPeak, $"{what}: {name} slot {slot}, {clip.Clip} ({clip.Length:0.###} s, additive)"));
        }
        switch (_case)
        {
            case "flinch-gunfire":
                Shot(0.6f, "sm_mp_ranged_bolter", false, false, true, false, "bolter hit, front left, high");
                Shot(1.8f, "sm_mp_ranged_bolter", false, false, false, true, "bolter hit, front right, low");
                Shot(3.0f, "sm_mp_ranged_bolter", true, false, false, false, "bolter head hit, front right");
                _end = 4.2f;
                return;
            case "flinch-light-melee":
                foreach (var (at, seed) in new[] { (0.6f, 1u), (1.8f, 5u), (3.0f, 9u) })
                {
                    int pick = FlinchRules.PickRandom(data.Flinch.Random, seed);
                    if (pick >= 0) _flinches.Add((at, data.Flinch.Random[pick].Clip, WeightFeel.FlinchMeleePeak, $"light melee hit: Random Flinches {pick + 1} of {data.Flinch.Random.Count}, {data.Flinch.Random[pick].Clip.Clip} (additive; no stagger, no interrupt)"));
                }
                _end = 4.2f;
                return;
            case "flinch-heavy":
            {
                string clip = ReactionTuning.Sets.First(s => s.Kind == ReactionKind.Stun).Clips[0];
                float length = _character?.Assets?.Animation(clip)?.Animation.Duration ?? 1f;
                _reaction = (0.6f, clip, length, $"stun strike (heavy hit): the full-body stun reaction {clip} ({length:0.###} s), for comparison with a flinch");
                _end = 0.6f + length + 1.2f;
                return;
            }
        }
        if (_case == "death-jump") { PlanJumpDeath(data); return; }
        // Deaths: the context the networked view builds for the kill.
        string weaponName = _case == "death-lascannon" ? "sm_mp_exotic_las_cannon" : "sm_mp_ranged_bolter";
        bool head = _case == "death-head", blast = _case == "death-explosion", melee = _case == "death-melee", sprint = _case == "death-sprint";
        // The blow (game space; the marine faces +y): from the front left, the right, behind or the front.
        _blow = _case switch
        {
            "death-side" => new Vector3(-1f, 0f, 0f),        // shot from its right: travelling to its left
            "death-back" => new Vector3(0f, 1f, 0f),          // from behind: travelling the way it faces
            "death-front" => new Vector3(0.6f, -0.8f, 0f),    // from the front left
            _ => new Vector3(0f, -1f, 0f),                    // from the front
        };
        float along = _blow.Y, right = _blow.X;
        IReadOnlyList<string> branches = [];
        string detail = melee ? "melee kill: no flinch branch" : blast ? "grenade kill: blast" : "";
        if (!melee && !blast)
        {
            var set = Set(weaponName, out var setName);
            int slot = FlinchRules.Choose(set, head, along > 0f, right > 0f);
            branches = set.Slot(slot)?.DeathBranches ?? [];
            detail = $"{weaponName} kill, its flinch {setName} slot {slot}{(head ? " (head)" : "")}: {(branches.Count > 0 ? string.Join(" ", branches) : "no branch")}";
        }
        var ctx = new DeathContext(false, sprint, false, blast, branches, true, along, right);
        if (DeathRules.Choose(ctx, data.Death) is not { } kind || data.Death.Get(kind) is not { } chosen) { GD.PrintErr($"hit reaction test: no death clip for {_case}"); return; }
        _death = chosen; _deathKind = kind;
        string level = blast ? "strong" : melee ? "medium" : "light";
        float force = _tuning is { } t && t.Forces.TryGetValue(level, out var f) ? f : level == "strong" ? 4000f : level == "medium" ? 2500f : 1000f;
        _push = force * (_tuning?.RagdollForceMultiplier ?? 1f) / (_ragdollDef?.TotalMass ?? 780f);
        _deathWhat = $"{_case}: {detail} -> {kind}: {chosen.Clip} ({chosen.Length:0.###} s), blend in {chosen.Blend:0.###} s, the ragdoll at {DeathRules.HoldSeconds(chosen, HoldCap):0.###} s ({level} push {_push:0.0} m/s{(NetworkView.OldHitReactions ? "; --old-hit-reactions: no clip motion" : "; the clip's motion carried on")})";
        _end = HitAt + DeathRules.HoldSeconds(chosen, HoldCap) + 3.0f;
        GD.Print("hit reaction test: " + _deathWhat);
    }

    public override void _Process(double delta)
    {
        if (_character is null || _upper is null) return;
        float dt = (float)delta;
        _clock += dt;
        float t = MathF.Max(_clock, 0f);
        string caption = $"{_case}   t {t:0.00} s";
        if (_death is not null) caption += "\n" + _deathWhat + (_ragdoll is not null ? "\nRAGDOLL" : t >= HitAt ? "\nDEATH CLIP" : "");
        if (_case == "death-jump") caption += JumpFrame(dt);
        else if (_ragdoll is null)
        {
            float idleLength = _idle is not null && _character.Assets?.Animation(_idle) is { } ia ? ia.Animation.Duration : 1f;
            if (_death is { } d && t >= HitAt)
            {
                float age = t - HitAt;
                if (age >= DeathRules.HoldSeconds(d, HoldCap)) StartRagdoll();
                else
                {
                    float rate = d.Length > 1e-3f && _character.Assets?.Animation(d.Clip) is { } da ? da.Animation.Duration / d.Length : 1f;
                    _character.ClearPoseLayers();
                    _character.Pose(d.Clip, age * rate, NetworkView.OldHitReactions ? 0.167f : d.Blend);
                    NotePose();
                }
            }
            else if (_reaction is { } r && t >= r.At && t < r.At + r.Length)
            {
                _character.ClearPoseLayers();
                _character.Pose(r.Clip, t - r.At, 0.1f);
                caption += "\n" + r.What;
            }
            else
            {
                _character.Pose(_idle ?? CharacterViewer.DefaultAnimation, t % idleLength, 0.2f);
                _character.ClearPoseLayers();
                foreach (var (at, clip, peak, what) in _flinches)
                {
                    float age = t - at;
                    if (age < 0f || age >= clip.Length + 0.333f) continue;
                    _character.SetLayer(PoseSlots.Flinch, clip.Clip, MathF.Min(age, clip.Length), NetworkView.FlinchWeight(age, clip.Length) * (clip.Additive ? peak : 1f), clip.Additive, null);
                    caption += "\n" + what;
                }
                if (_reaction is { } past && t >= past.At) caption += "\n(back to the stance after the reaction)";
            }
            _character.ForcePose();
        }
        if (_caption is not null) _caption.Text = caption;
        if (_clock >= _end) { GD.Print($"hit reaction test: {_case} done at {_clock:F2} s; ragdoll bodies started with the clip's motion {Ragdoll.ClipMotionBodies}"); GetTree().Quit(0); _upper = null; }
    }

    void NotePose()
    {
        if (_character?.Skeleton is not { } skeleton) return;
        int n = skeleton.GetBoneCount();
        var spare = _posePrev is { } p && p.Length == n ? p : new Transform3D[n];
        _posePrev = _poseLast; _posePrevAt = _poseLastAt;
        for (int b = 0; b < n; b++) spare[b] = skeleton.GetBoneGlobalPose(b);
        _poseLast = spare; _poseLastAt = _clock;
    }

    void StartRagdoll(Vector3 velocity = default, bool push = true)
    {
        if (_ragdollDef is null || _character is null || _space is null) return;
        _ragdoll = new Ragdoll { Name = "Corpse", PlayerId = 1, StartedAt = _clock };
        _space.AddChild(_ragdoll);
        var kick = push ? _blow.Normalized() * _push : Vector3.Zero;
        float seconds = _poseLastAt - _posePrevAt;
        bool motion = !NetworkView.OldHitReactions && _posePrev is not null && seconds is > 1e-3f and < 0.25f;
        _ragdoll.Start(_ragdollDef, _character, 0, _space.Basis * velocity, _space.Basis * kick, motion ? _posePrev : null, motion ? seconds : 0f);
        _character.Visible = false;
        GD.Print($"hit reaction test: ragdoll at {_clock - HitAt:F3} s into the death ({_deathKind}), push {(push ? _push : 0f):F2} m/s along {_blow}, body velocity {velocity}, clip motion {(motion ? $"over {seconds * 1000f:F0} ms" : "none")}");
    }

    // ---- the jump-pack death in mid-air (death-jump) ----

    /// <summary>The jump death's clips and the kill's route, with the networked view's rules (a bolter shot from the front left while the marine flies: <c>BOOL_JumpIsActive</c> comes first).</summary>
    void PlanJumpDeath(UpperBodyBundle data)
    {
        _blow = new Vector3(0.6f, -0.8f, 0f);
        var ctx = new DeathContext(true, false, false, false, ["BRANCH_Death_Ranged_Left"], true, _blow.Y, _blow.X);
        var kind = DeathRules.Choose(ctx, data.Death);
        if (kind != MarineDeathKind.JumpStart || !JumpDeathRules.Applies(true, false, data.Death)) { GD.PrintErr($"hit reaction test: no jump death ({kind}; {string.Join("; ", data.Death.Notes)})"); return; }
        var hit = data.Death.Get(MarineDeathKind.JumpStart)!; var fall = data.Death.Get(MarineDeathKind.JumpFall)!; var impact = data.Death.Get(MarineDeathKind.JumpImpact)!;
        _deathKind = MarineDeathKind.JumpStart;
        _deathWhat = $"death-jump: a bolter kill in jump-pack flight ({JumpAt.Z:0} m up, {JumpVelocity.Y:0} m/s ahead, {JumpVelocity.Z:0} m/s up) -> {kind} (BOOL_JumpIsActive, priority 10): {hit.Clip} to {hit.RagdollAt:0.###} s, " +
                     $"{fall.Clip} looping ({fall.Length:0.###} s at speed 0.65) to the floor, {impact.Clip} to the ragdoll at {impact.RagdollAt:0.###} s (StartAction {JumpDeathRules.ImpactActionAt(impact):0.###} s); no push";
        _jump = new JumpDeathRun(hit, fall, impact, new System.Numerics.Vector3(JumpAt.X, JumpAt.Y, JumpAt.Z), new System.Numerics.Vector3(JumpVelocity.X, JumpVelocity.Y, JumpVelocity.Z), JumpGravity, JumpMaxFall);
        _end = HitAt + JumpDeathRules.FallCap + 3.5f;   // shortened when the ragdoll starts
        // actn_jumpdeath_start from the death (+0.02 s, DeathEffectRules.StartOf); its steps' own delays on top.
        if (_deathSide?.JumpStart is { } start)
            foreach (var step in start.Effects) _fxSteps.Add((HitAt + DeathEffectRules.StartOf(DeathKind.Jump) + step.Delay, step.Fx!));
        GD.Print("hit reaction test: " + _deathWhat + $"; effects {(_fx is { IsReady: true } ? $"{_fxSteps.Count} start steps" : "not loaded")}");
    }

    /// <summary>The jump death's effects (the marine side's <c>actn_jumpdeath_start</c> and <c>actn_jumpdeath_impact</c>), compiled as the networked view does (a duration of 0 lasting the play's length).</summary>
    void LoadJumpDeathFx(List<string> notes)
    {
        try
        {
            var death = DeathEffects.Load(_gameDir, notes);
            _deathSide = death.Marines;
            var library = FxLibrary.Load(_gameDir);
            _fx = new FxPlayer { Name = "Effects" };
            AddChild(_fx);
            if (!_fx.Initialize(library, _gameDir)) { notes.Add("jump death effects: " + _fx.Status); return; }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in new[] { _deathSide.JumpStart, _deathSide.JumpImpact }) if (a is not null) foreach (var n in a.EffectNames) names.Add(n);
            var failures = new List<string>();
            var compiled = NetworkView.CompileSystems(library, names, failures, death.EffectLengths);
            var collide = (_deathSide.JumpImpact?.EffectNames ?? []).ToHashSet(StringComparer.Ordinal);
            _fx.Preload(compiled, library.Textures(FxPlayer.DecalNames(compiled.Values, collide)), collide);
            notes.Add($"jump death effects: {compiled.Count} of {names.Count} compiled ({string.Join(", ", compiled.Keys)}){(failures.Count > 0 ? "; not: " + string.Join(", ", failures) : "")}");
        }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException)) { notes.Add("jump death effects: not loaded: " + ex.Message); _fx = null; }
    }

    /// <summary>One frame of the death-jump case: the flight, then the jump death (the networked view's run), its effects, the ragdoll, and the following camera. Returns the caption's lines.</summary>
    string JumpFrame(float dt)
    {
        if (_character is null || _space is null || _jump is not { } run) return "\n(no jump death)";
        string lines = "\n" + _deathWhat;
        if (_ragdoll is null && _clock < HitAt)
        {
            // The flight: straight along its velocity (the stage has no thrust model), posed by the motion's vertical angle.
            var at = JumpAt + JumpVelocity * (_clock - HitAt);
            _character.Position = at;
            if (_flight is not null && _character.Assets?.Animation(_flight) is { } fa)
            {
                float fraction = JumpFlightPose.Target(JumpVelocity.Z, new Vector2(JumpVelocity.X, JumpVelocity.Y).Length());
                _character.Pose(_flight, JumpFlightPose.Time(fraction, fa.Animation.Duration), 0.2f);
            }
            else _character.Pose(_idle ?? CharacterViewer.DefaultAnimation, 0f, 0.2f);
            _character.ClearPoseLayers();
            _character.ForcePose();
            lines += $"\nFLIGHT ({_flight ?? "no flight clip: idle"})";
        }
        else if (_ragdoll is null)
        {
            float step = MathF.Min(dt, _clock - HitAt);
            var events = run.Step(step, FloorRay, NetworkView.BodyBottom(_character, ref _bodyBones));
            if ((events & JumpDeathEvents.Fall) != 0) _jumpNotes += $"\n  fall loop from {run.PhaseStart:0.000} s ({run.Dropped:0.00} m fallen)";
            if ((events & JumpDeathEvents.FloorHit) != 0 && (events & JumpDeathEvents.Landed) == 0) _jumpNotes += $"\n  the body meets the floor at {run.FloorHitAt:0.000} s, its feet {run.Feet.Z:0.00} m up";
            if ((events & JumpDeathEvents.Landed) != 0) _jumpNotes += $"\n  on the floor at {run.LandedAt:0.000} s after {run.Dropped:0.00} m ({run.FallSeconds:0.00} s of the fall loop)";
            if ((events & JumpDeathEvents.ImpactAction) != 0) { _jumpNotes += $"\n  impact effects at {run.Age:0.000} s ({run.PhaseAge:0.000} s into the floor clip)"; PlayJumpImpact(run); }
            if (events != JumpDeathEvents.None) GD.Print($"hit reaction test: jump death at {run.Age:F3} s: {events}; phase {run.Phase}, feet {run.Feet.X:F2},{run.Feet.Y:F2},{run.Feet.Z:F2}, velocity z {run.Velocity.Z:F1} m/s");
            if (run.Phase == JumpDeathPhase.Ragdoll)
            {
                _jumpNotes += $"\n  ragdoll at {run.Age:0.000} s ({run.End}{(run.End == JumpDeathEnd.Floor ? $", {run.PhaseAge:0.000} s into the floor clip" : "")})";
                StartRagdoll(new Vector3(run.Velocity.X, run.Velocity.Y, run.Velocity.Z), push: false);
                _end = MathF.Min(_end, _clock + 2.5f);
            }
            else
            {
                _character.Position = new Vector3(run.Feet.X, run.Feet.Y, run.Feet.Z);
                var (clip, playback, entered) = run.Shown();
                float rate = clip.Length > 1e-3f && _character.Assets?.Animation(clip.Clip) is { } ca ? ca.Animation.Duration / clip.Length : 1f;
                _character.ClearPoseLayers();
                _character.Pose(clip.Clip, playback * rate, entered ? clip.Blend : 0f);
                _character.ForcePose();
                NotePose();
                lines += $"\n{run.Phase.ToString().ToUpperInvariant()} {clip.Clip} at {playback * rate:0.00} s";
                if (_character.Skeleton is { } sk && _clock - _probeAt >= 0.05f)
                {
                    _probeAt = _clock;
                    float low = float.MaxValue; string lowName = "";
                    for (int b = 0; b < sk.GetBoneCount(); b++)
                    {
                        float z = (sk.GlobalTransform * sk.GetBoneGlobalPose(b)).Origin.Y - _character.GlobalPosition.Y;   // world up, from the node's origin (the feet)
                        if (z < low) { low = z; lowName = sk.GetBoneName(b); }
                    }
                    int pelvis = sk.FindBone("RIG_Pelvis");
                    float pz = pelvis >= 0 ? (sk.GlobalTransform * sk.GetBoneGlobalPose(pelvis)).Origin.Y - _character.GlobalPosition.Y : float.NaN;
                    GD.Print($"hit reaction test: probe {run.Phase} {playback * rate:F2} s: lowest bone {lowName} {low:F2} m above the origin, pelvis {pz:F2} m, feet z {run.Feet.Z:F2}");
                }
            }
        }
        if (_ragdoll is not null) lines += "\nRAGDOLL";
        lines += _jumpNotes;
        // The death effects due (they follow their bone: the falling body's, then the corpse's).
        for (int i = _fxSteps.Count - 1; i >= 0; i--)
        {
            if (_clock < _fxSteps[i].Due || _fx is not { IsReady: true }) continue;
            var p = _fxSteps[i].Play;
            _fxSteps.RemoveAt(i);
            string marker = p.Marker;
            Func<(Vector3, Basis)?>? follow = p.Attached ? () => (MarkerWorld(marker), Basis.Identity) : null;
            _fx.Play(p.Name, MarkerWorld(marker), Basis.Identity, p.Scale <= 0f ? 1f : p.Scale, null, follow, p.Lifetime, 0.0, false, 2);
            GD.Print($"hit reaction test: jump death effect {p.Name} at {marker} ({_clock - HitAt:F3} s after the death)");
        }
        FollowCamera(dt);
        if (_fx is { IsReady: true } && _camera is not null) _fx.Update(dt, _camera);   // the effects advance with the stage (FxTest's pattern)
        return lines;
    }

    /// <summary>The stage's level for the falling body: the floor plane at z = 0.</summary>
    static float? FloorRay(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, float max) =>
        direction.Z < -1e-6f && origin.Z >= 0f && origin.Z / -direction.Z <= max ? origin.Z / -direction.Z : null;

    /// <summary>A marker of the death data (a rig bone, ignoring case and underscores) on the body shown now (the falling one, then the corpse), Godot world; the chest without one.</summary>
    Vector3 MarkerWorld(string marker)
    {
        var body = _ragdoll?.Character ?? _character;
        if (body?.Skeleton is { } skeleton)
            for (int b = 0; b < skeleton.GetBoneCount(); b++)
                if (DeathEffectRules.SameBone(skeleton.GetBoneName(b), marker)) return (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(b)).Origin;
        return _ragdoll is not null ? _ragdoll.FocusPoint : (body?.GlobalPosition ?? Vector3.Zero) + Vector3.Up * 1.2f;
    }

    /// <summary><c>actn_jumpdeath_impact</c>: its ground effects (crater, smoke, debris) under the body on the floor.</summary>
    void PlayJumpImpact(JumpDeathRun run)
    {
        if (_fx is not { IsReady: true } || _deathSide?.JumpImpact is not { } impact || _space is null) return;
        var point = _space.GlobalTransform * new Vector3(run.Feet.X, run.Feet.Y, 0f);
        var started = GroundPoundFx.Play(_fx, impact.Effects.Select(s => s.Fx!).ToList(), point, Vector3.Up, 0f, null, "", 17);
        GD.Print($"hit reaction test: jump death impact: {string.Join(", ", started.Select(e => e.Name))} at {run.PhaseAge:F3} s into the floor clip");
    }

    /// <summary>The death-jump camera: to the marine's right and a little behind and above, following its chest (then the corpse's) smoothly.</summary>
    void FollowCamera(float dt)
    {
        if (!_followCamera || _camera is null || _character is null) return;
        var focus = _ragdoll is not null ? _ragdoll.FocusPoint : _character.GlobalPosition + Vector3.Up * 1.1f;
        _cameraFocus = _cameraFocus == Vector3.Zero ? focus : _cameraFocus.Lerp(focus, 1f - MathF.Exp(-12f * MathF.Max(dt, 0f)));
        _camera.GlobalPosition = _cameraFocus + ToGodot(new Vector3(7.5f, -3f, 1.4f));
        _camera.LookAt(_cameraFocus, Vector3.Up);
    }
}
