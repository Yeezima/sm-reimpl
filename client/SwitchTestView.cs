using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// fix/stow-right test aid (<c>--switch-test</c>): a marine on a plain floor switches primary → pistol → primary with the rules the networked view uses: the switch's clip pieces
/// (<see cref="SwitchPlan"/>) laid in <see cref="PoseSlots.Switch"/> / <see cref="PoseSlots.SwitchNext"/> with the same fades, stretched over the switch time (0.6 s), and
/// <see cref="WeaponRig"/> moving the models between hand and stow marker at the plan's release and take. Under the switch the upper body holds the pose the last switch ended in (the
/// stow clip's first frame before the first switch): the relaxed idles move the legs only. <c>--switch-primary w</c> / <c>--switch-pistol w</c> / <c>--switch-melee w</c> /
/// <c>--switch-pack w</c> pick the wargear (default the Tactical's bolter and bolt pistol), <c>--switch-shots n</c> saves n frames spread over each switch to <c>--switch-out</c> (default
/// <c>%LOCALAPPDATA%\sm-reimpl\screenshots\stow-right</c>, never inside the repository or the install; names start with <c>--switch-label</c>, default "before" with <c>--old-stow</c>, else
/// "after") and quits, <c>--switch-cam x,y,z</c> / <c>--switch-look x,y,z</c> place the camera (Godot space, the marine faces -Z), <c>--switch-probe</c> prints per sample of each switch
/// where the hand props, the chest prop and each drawn weapon's handle are in RIG_SpineA's frame (right, forward, up) and how far a weapon in hand is from its hand prop, and quits
/// (<c>--switch-clips</c> also tabulates the two swap clips alone). <c>--old-stow</c> gives the behaviour before the fix.
/// </summary>
public partial class SwitchTestView : Node3D
{
    readonly string _gameDir;
    readonly Dictionary<string, string?> _args;
    CharacterViewer? _character;
    WeaponRig? _rig;
    UpperBodyBundle? _upper;
    string _primary = "", _pistol = "", _label = "";
    string? _melee, _pack;
    string _outDir = "";
    int _shotsPerSwitch;
    const float SwitchTime = 0.6f;   // SimConfig.WeaponSwitchTime (spec "Weapon switch time")
    const float BlendIn = 0.1f, BlendOut = 0.1f, JointBlend = 0.08f;   // NetworkView.UpperBody.cs SwitchBlendIn / SwitchBlendOut / SwitchJointBlend
    // The script: the primary in hand, switch to the pistol at 0.5 s, hold, switch back at 1.9 s, hold to 3.0 s.
    static readonly (float At, WeaponSlot Slot)[] Script = [(0f, WeaponSlot.Ranged), (0.5f, WeaponSlot.Pistol), (1.9f, WeaponSlot.Ranged)];
    const float ScriptEnd = 3.0f;
    float _clock = -0.3f;
    WeaponSlot _slot = WeaponSlot.Ranged;
    float _switchAt = -10f;
    SwitchPlan? _plan;
    (string Clip, float Time, IReadOnlyDictionary<string, float> Mask)? _hold;
    readonly List<(float At, string Name)> _shots = new();
    int _nextShot;
    string? _rifleIdle, _pistolIdle;

    public SwitchTestView(string gameDir, Dictionary<string, string?> args) { _gameDir = gameDir; _args = args; }
    public SwitchTestView() : this("", new Dictionary<string, string?>()) { }

    static float F(string? text, float fallback) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    static Vector3 V3(string? text, Vector3 fallback)
    {
        var p = text?.Split(',');
        return p is { Length: 3 } ? new Vector3(F(p[0], fallback.X), F(p[1], fallback.Y), F(p[2], fallback.Z)) : fallback;
    }

    public override void _Ready()
    {
        _primary = _args.GetValueOrDefault("switch-primary") ?? "sm_mp_ranged_bolter";
        _pistol = _args.GetValueOrDefault("switch-pistol") ?? "sm_mp_pistol_bolt_pistol";
        _melee = _args.GetValueOrDefault("switch-melee");
        _pack = _args.GetValueOrDefault("switch-pack");
        _label = _args.GetValueOrDefault("switch-label") ?? (WeaponRig.OldStow ? "before" : "after");
        _shotsPerSwitch = (int)F(_args.GetValueOrDefault("switch-shots"), 0f);
        _outDir = _args.GetValueOrDefault("switch-out") ?? System.IO.Path.Combine(GameInstall.DataRoot, "screenshots", "stow-right");
        var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
        if (SmFormats.InstallPaths.IsWithin(System.IO.Path.GetFullPath(_outDir), repoRoot) || SmFormats.InstallPaths.IsWithin(System.IO.Path.GetFullPath(_outDir), _gameDir))
        { GD.PrintErr($"switch test: {_outDir} is inside the repository or the game folder; screenshots refused"); _shotsPerSwitch = 0; }
        for (int s = 1; s < Script.Length; s++)
            for (int k = 0; k < _shotsPerSwitch; k++)
            {
                // n frames from the switch's start to just past its end (k / (n - 1) of 1.1 x the switch time).
                float f = _shotsPerSwitch == 1 ? 0.5f : k / (float)(_shotsPerSwitch - 1);
                float at = Script[s].At + f * SwitchTime * 1.1f;
                _shots.Add((at, $"{_label}_{(Script[s].Slot == WeaponSlot.Pistol ? "1_to_pistol" : "2_to_primary")}_{k:00}_{f * SwitchTime * 1.1f:0.000}s"));
            }

        var checker = Image.CreateEmpty(2, 2, false, Image.Format.Rgb8);
        checker.SetPixel(0, 0, new Color(0.34f, 0.35f, 0.37f)); checker.SetPixel(1, 1, new Color(0.34f, 0.35f, 0.37f));
        checker.SetPixel(1, 0, new Color(0.22f, 0.23f, 0.25f)); checker.SetPixel(0, 1, new Color(0.22f, 0.23f, 0.25f));
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(20, 20) },
            MaterialOverride = new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(checker), TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Uv1Scale = new Vector3(20, 20, 1) },
        });
        AddChild(new DirectionalLight3D { LightEnergy = 1.2f, ShadowEnabled = true, RotationDegrees = new Vector3(-45, 25, 0) });
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
        // Default: behind the marine and a little to his right, about where the follow camera sits (the marine faces -Z, his right is +X).
        camera.Position = V3(_args.GetValueOrDefault("switch-cam"), new Vector3(0.6f, 2.2f, 3.4f));
        camera.LookAt(V3(_args.GetValueOrDefault("switch-look"), new Vector3(0, 1.2f, 0)), Vector3.Up);

        var space = new Node3D { Name = "GameSpace", Basis = new Basis(new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0)) };
        AddChild(space);
        var notes = new List<string>();
        try
        {
            var assets = MarineAssets.Load(_gameDir, notes);
            _rifleIdle = assets.FindAnimation(["SM_NAV_IdleL_S_01", "SM_NAV_Idle_S_01", "SM_NAV_IdleR_S_01"], "Idle");
            _pistolIdle = assets.Animation("SM_NAV_IdleL_CS_01") is not null ? "SM_NAV_IdleL_CS_01" : null;
            _character = new CharacterViewer(assets, IdleFor(WeaponSlot.Ranged), gameSpaceRoot: false) { Name = "Marine" };
            space.AddChild(_character);
            var wargear = new[] { _primary, _pistol, _melee, _pack }.Where(w => w is not null).Cast<string>().ToArray();
            var weaponAssets = WeaponAssets.Load(_gameDir, assets, wargear, notes);
            _rig = new WeaponRig(_character.Skeleton ?? throw new InvalidOperationException("no skeleton"), weaponAssets);
            _rig.Configure((0, 1, 1, _melee, _pack), _primary, _pistol, _melee, _pack is null ? [] : [_pack], alwaysMelee: _pack is not null);
            _rig.Update(WeaponSlot.Ranged, false, true, 0f, SwitchTime);
            _upper = UpperBodyBundle.Load(_gameDir);
            if (_upper.Switch.ToRifle is { } first) _hold = (first.Clip, first.Length, first.Mask);   // before the first switch: the rifle in hand as its draw ends
            foreach (var w in wargear)
                if (weaponAssets.Catalogue.Held(w) is { } a)
                    GD.Print($"switch test: {w}: in hand on {a.Wield?.BodyNode} ({a.Wield?.Bone}), stowed on {a.Stow?.BodyNode ?? "-"} ({a.Stow?.Bone ?? "-"}){(a.HideOnStow ? ", hidden when stowed" : "")}");
            foreach (var (from, to) in new[] { (WeaponSlot.Ranged, WeaponSlot.Pistol), (WeaponSlot.Pistol, WeaponSlot.Ranged) })
                if (SwitchPlan.For(_upper.Switch, from, to, WeaponRig.OldStow) is { } p)
                    GD.Print($"switch test: {from} -> {to}: " + string.Join(" then ", p.Segments.Select(g => $"{g.Clip} {g.From:F3}->{g.To:F3} s over {g.Start:F3}..{g.End:F3}")) +
                             $"; old weapon stowed at {p.Release:F3}, new in hand at {p.Take:F3} of the switch{(WeaponRig.OldStow ? " (--old-stow)" : "")}");
        }
        catch (Exception ex) { GD.PrintErr("switch test: " + ex); }
        foreach (var n in notes) GD.Print("  note: " + n);
    }

    /// <summary>The relaxed idle of the weapon in hand (legs; the networked view's choice: <c>NetworkView.IdleCandidates</c>, the pistol's <c>SM_NAV_IdleL_CS_01</c>).</summary>
    string IdleFor(WeaponSlot slot) => (slot == WeaponSlot.Pistol ? _pistolIdle : null) ?? _rifleIdle ?? CharacterViewer.DefaultAnimation;

    /// <summary>Poses the marine at script time <paramref name="t"/> as the networked view would (NetworkView.UpperBody.cs SwitchLayer: the same plan, fades and joint).</summary>
    void PoseAt(float t, float delta)
    {
        var slot = WeaponSlot.Ranged;
        float switchAt = -10f;
        foreach (var (at, s) in Script) if (t >= at) { slot = s; switchAt = at; }
        if (slot != _slot)
        {
            // Under the switch, the new weapon's stance from its first frame (the networked view's base pose follows the simulation's slot, which changes at the press): its draw's last
            // pose stands in for it.
            _plan = SwitchPlan.For(_upper!.Switch, _slot, slot, WeaponRig.OldStow);
            if (_plan is { } next) _hold = (next.Segments[^1].Clip, next.Segments[^1].To, next.Segments[^1].Mask);
            _slot = slot; _switchAt = switchAt;
        }
        float elapsed = t - _switchAt;
        _character!.Pose(IdleFor(_slot), 0.4f, 0f);
        _character.ClearPoseLayers();
        if (_hold is { } hold) _character.SetLayer(PoseSlots.Action, hold.Clip, hold.Time, 1f, false, hold.Mask);
        if (_plan is { } plan && elapsed >= 0f && elapsed < SwitchTime)
        {
            float weight = CharacterViewer.Smooth(Math.Clamp(Math.Min(elapsed / BlendIn, (SwitchTime - elapsed) / BlendOut), 0f, 1f));
            float fraction = elapsed / SwitchTime;
            var segment = plan.SegmentAt(fraction);
            if (plan.Crossfade(fraction, JointBlend / SwitchTime) is { } joint)
            {
                _character.SetLayer(PoseSlots.Switch, joint.Previous.Clip, joint.Previous.To, weight, false, joint.Previous.Mask);
                _character.SetLayer(PoseSlots.SwitchNext, segment.Clip, segment.ClipTime(fraction), weight * CharacterViewer.Smooth(joint.Progress), false, segment.Mask);
            }
            else _character.SetLayer(PoseSlots.Switch, segment.Clip, segment.ClipTime(fraction), weight, false, segment.Mask);
        }
        _character.ForcePose();
        _rig!.Update(_slot, false, true, delta, SwitchTime, _plan?.Release ?? 0.5f, _plan?.Take ?? -1f);
    }

    /// <summary><c>--switch-probe</c>: samples each switch every 1/20 s and prints the hand props, the chest prop and the drawn weapons in RIG_SpineA's frame.</summary>
    void Probe()
    {
        var skeleton = _character!.Skeleton!;
        int B(string n) => skeleton.FindBone(n);
        int propR = B("RIG_Prop_RHand"), propL = B("RIG_Prop_LHand"), chest = B("RIG_Prop_M_Chest"), spine = B("RIG_SpineA"), handR = B("RIG_R_Hand"), handL = B("RIG_L_Hand");
        Vector3 P(int b) => skeleton.GetBoneGlobalPose(b).Origin;
        // RIG_SpineA's own frame (the bone the bolter's stow marker hangs on): X up the body, Y forward, Z to the left (RigStowSpot's note; checked: the left thigh is at -16 cm right, the right at +15).
        string Where(Vector3 p)
        {
            var basis = skeleton.GetBoneGlobalPose(spine).Basis.Orthonormalized();
            Vector3 up = basis.Column0, fwd = basis.Column1, right = -basis.Column2;
            var d = p - P(spine);
            return $"({d.Dot(right) * 100f:+0;-0} right, {d.Dot(fwd) * 100f:+0;-0} fwd, {d.Dot(up) * 100f:+0;-0} up)";
        }
        if (_args.ContainsKey("switch-clips"))
            foreach (var clip in new[] { _upper!.Switch.ToRifle, _upper.Switch.ToPistol })
            {
                if (clip is null) continue;
                for (float ct = 0f; ct <= clip.Length + 1e-4f; ct += 1f / 30f)
                {
                    _character.ClearPoseLayers();
                    _character.SetLayer(PoseSlots.Switch, clip.Clip, ct, 1f, false, clip.Mask);
                    _character.ForcePose();
                    GD.Print($"clip {clip.Clip} {ct:F3}: R prop {Where(P(propR))} L prop {Where(P(propL))} chest prop {Where(P(chest))}, chest-R {P(chest).DistanceTo(P(propR)) * 100f:F0} cm");
                }
            }
        _rig!.Update(WeaponSlot.Ranged, false, true, 0f, SwitchTime);
        float dt = 1f / 60f;
        float worstFree = 0f;
        string worstAt = "-";
        float worstPistol = 0f;
        var last = new Dictionary<HandRole, (string Mode, Vector3 At)>();
        for (float t = 0f; t <= ScriptEnd; t += dt)
        {
            PoseAt(t, dt);
            float since = t - _switchAt;
            if (since < 0f || since > SwitchTime * 1.05f) continue;
            // The primary in hand rides the chest prop: how far that is from the nearer hand (the free flight the owner saw), while the switch clips pose the arms at full
            // weight (the fades blend with the stand-in stance underneath, which is not the networked view's).
            if (since >= BlendIn && since <= SwitchTime - BlendOut)
                foreach (var (role, mode, _) in _rig.Placements())
                    if (mode == "hand" && role == HandRole.Primary && Math.Min(P(chest).DistanceTo(P(handR)), P(chest).DistanceTo(P(handL))) is var free && free > worstFree)
                    { worstFree = free; worstAt = $"{(_slot == WeaponSlot.Pistol ? "to pistol" : "to primary")} at {since:F3} s"; }
                    else if (mode == "hand" && role == HandRole.Pistol) worstPistol = Math.Max(worstPistol, P(propL).DistanceTo(P(handL)));
            // Where a weapon changes between hand and marker: how far its handle jumps in that frame.
            foreach (var (role, mode, origin) in _rig.Placements())
            {
                if (last.TryGetValue(role, out var was) && was.Mode != mode)
                    GD.Print($"probe: {(_slot == WeaponSlot.Pistol ? "to pistol" : "to primary")} at {since:F3} s the {role} goes {was.Mode} -> {mode}: its handle jumps {was.At.DistanceTo(origin) * 100f:F0} cm");
                last[role] = (mode, origin);
            }
            if ((int)MathF.Round(since / dt) % 3 != 0) continue;
            var line = $"probe {(_slot == WeaponSlot.Pistol ? "to pistol " : "to primary")} {since:F3} s: R prop {Where(P(propR))}, L prop {Where(P(propL))}, chest prop {Where(P(chest))} ({P(chest).DistanceTo(P(handR)) * 100f:F0} / {P(chest).DistanceTo(P(handL)) * 100f:F0} cm from the R / L hand)";
            foreach (var (role, mode, origin) in _rig.Placements()) line += $"; {role} {mode} {Where(origin)}";
            GD.Print(line);
        }
        GD.Print($"probe: the primary in hand was at most {worstFree * 100f:F0} cm from the nearer hand during the switches ({worstAt}), the pistol in hand at most {worstPistol * 100f:F0} cm from the left hand{(WeaponRig.OldStow ? " (--old-stow)" : "")}");
    }

    public override void _Process(double delta)
    {
        if (_character is null || _rig is null || _upper is null) return;
        if (_args.ContainsKey("switch-probe")) { Probe(); GetTree().Quit(0); _upper = null; return; }
        float dt = (float)delta;
        _clock += dt;
        if (_clock < 0f) { PoseAt(0f, 0f); return; }
        PoseAt(_clock, dt);
        if (_nextShot < _shots.Count && _clock >= _shots[_nextShot].At - 1e-4f)
        {
            System.IO.Directory.CreateDirectory(_outDir);
            string path = System.IO.Path.Combine(_outDir, _shots[_nextShot].Name + ".png");
            var err = GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print(err == Error.Ok ? $"screenshot -> {path} (clock {_clock:F3} s; {string.Join(", ", _rig.Description)})" : $"screenshot failed: {err}");
            _nextShot++;
        }
        if (_shots.Count > 0 && _nextShot >= _shots.Count) { GetTree().Quit(0); _upper = null; }
        if (_shots.Count == 0 && _clock > ScriptEnd + 0.5f) { _clock = -0.3f; _slot = WeaponSlot.Ranged; _plan = null; }
    }
}
