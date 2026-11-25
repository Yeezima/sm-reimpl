using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmContent;
using SmContent.Extras.Executions;
using SmFormats.Havok;
using SmSim.Extras.Executions;

namespace SmClient;

/// <summary>
/// Executions X7 test aid: one execution row's attacker and victim clips (<see cref="ExecutionRows.All"/>) read from the install and played together in our
/// renderer, without a server, for the side-by-side "original asset loaded" baseline (Fable review). The attacker is the MP Space Marine (or, with
/// <c>--race-swap</c> on a Chaos Marine row, the MP Chaos Marine) playing the campaign player rig's clip (the same skeleton, inventory 1.4) with the row's
/// weapon in hand; the victim is the co-op Ork Boy (Ork Boy rows, the campaign <c>havok_boy</c> clip on the same skeleton) or the MP Chaos Marine (Chaos
/// Marine rows; the loyalist with <c>--race-swap</c>), standing at the row's authored offset and facing the attacker. Both clips run on one clock from 0;
/// a clip whose root path is extracted motion moves its character's node (a root-bone path moves the skeleton itself). Nothing is aligned, blended or
/// checked against terrain here: these are the raw reference clips (the 3.5 m variant; the arm break's 4 m).
/// Arguments: <c>--execution-test &lt;row id&gt;</c> (default <c>O3.chainsword</c>; <c>--list</c> prints the rows), <c>--race-swap</c>, <c>--look &lt;ork look&gt;</c>
/// (default boy_melee), <c>--view side|front|back|three</c> (default side), <c>--times t0,t1,..</c> (stills at those seconds saved to <c>--out</c>, default
/// <c>%LOCALAPPDATA%\sm-reimpl\cache\executions</c>, never inside the repository or the install, as <c>&lt;--name&gt;_t&lt;time&gt;.png</c>, then quit; a capture needs
/// <c>godot.cmd --windowed</c>), <c>--rate f</c> (playback speed), <c>--quit</c> (after one pass). Prints <c>EXECTEST</c> lines (clips, lengths, root sources,
/// kill time). Gretchin, Bloodletter and renegade rows have no model in this view yet. **Original asset loaded** (clips, meshes, textures) on a
/// **placeholder demonstration** stage.
/// </summary>
public partial class ExecutionTestView : Node3D
{
    readonly string _gameDir;
    readonly Dictionary<string, string?> _args;
    ExecutionRow? _row;
    CharacterViewer? _attacker, _victim;
    ExecutionClip? _attackerClip, _victimClip;
    Vector3 _victimStart;   // Godot space
    float _time, _rate = 1f, _length;
    float[] _times = [];
    int _nextShot, _waitFrames;
    bool _shooting, _quit, _passed;
    string _outDir = "", _name = "execution";

    public ExecutionTestView(string gameDir, Dictionary<string, string?> args) { _gameDir = gameDir; _args = args; }
    public ExecutionTestView() : this("", new Dictionary<string, string?>()) { }

    /// <summary>The MP wargear drawn for a row's family (the Chaos Marine's own when the attacker is Chaos).</summary>
    static string Wargear(string family, bool chaos) => family switch
    {
        ExecutionWeapons.PowerAxe => chaos ? "csm_mp_melee_chain_axe" : "sm_mp_melee_power_axe",
        ExecutionWeapons.ThunderHammer => chaos ? "csm_mp_melee_2hand_power_mace" : "sm_mp_melee_2hand_thunder_hammer",
        ExecutionWeapons.CombatKnife => chaos ? "csm_mp_melee_combat_knife" : "sm_mp_melee_combat_knife",
        _ => chaos ? "csm_mp_melee_chainsword" : "sm_mp_melee_chainsword",
    };

    /// <summary>Game space (X right, Y forward, Z up) to Godot (Y up): x, z, −y.</summary>
    static Vector3 ToGodot(float x, float y, float z) => new(x, z, -y);

    public override void _Ready()
    {
        var notes = new List<string>();
        if (_args.ContainsKey("list"))
            foreach (var r in ExecutionRows.All) GD.Print($"EXECTEST row {r.Id,-20} {r.Target,-13} {r.AttackerClip} / {r.VictimClip}");
        string id = _args.GetValueOrDefault("execution-test") is { Length: > 0 } given ? given : "O3.chainsword";
        _row = ExecutionRows.All.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        if (_row is null) { Fail($"no row '{id}' (known: {string.Join(", ", ExecutionRows.All.Select(r => r.Id))})"); return; }
        _name = _args.GetValueOrDefault("name") is { Length: > 0 } n && n.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 && !n.Contains("..", StringComparison.Ordinal) ? n : _row.Id.Replace('.', '_');
        _outDir = _args.GetValueOrDefault("out") ?? System.IO.Path.Combine(GameInstall.DataRoot, "cache", "executions");
        _rate = float.TryParse(_args.GetValueOrDefault("rate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate > 0f ? rate : 1f;
        _quit = _args.ContainsKey("quit");
        foreach (var x in (_args.GetValueOrDefault("times") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0f) _times = [.. _times, v];
        bool swap = _args.ContainsKey("race-swap");
        if (swap && _row.Target != ExecutionTargets.ChaosMarine) { notes.Add("--race-swap applies to the Chaos Marine rows only; ignored"); swap = false; }

        try
        {
            var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
            string fullOut = System.IO.Path.GetFullPath(_outDir);
            if (SmFormats.InstallPaths.IsWithin(fullOut, repoRoot) || SmFormats.InstallPaths.IsWithin(fullOut, _gameDir))
            { GD.PrintErr($"execution test: {_outDir} is inside the repository or the game folder; stills refused"); _times = []; }

            // The clips: the campaign player rig (attacker) and the victim kind's rig, read-only.
            var campaign = ExecutionCatalogueLoader.AttackerProfile.LoadCharacter(_gameDir);
            var victimProfile = ExecutionCatalogueLoader.VictimProfile(_row.Target) ?? throw new InvalidOperationException($"no victim rig for '{_row.Target}'");
            var victimRig = victimProfile.LoadCharacter(_gameDir);
            _attackerClip = ExecutionClipReader.Read(campaign, _row.AttackerClip, []);
            _victimClip = ExecutionClipReader.Read(victimRig, _row.VictimClip, []);
            _length = MathF.Max(_attackerClip.Seconds, _victimClip.Seconds);

            // The characters: our MP models (and the co-op Ork), the original clips preloaded into their rigs (the same skeletons, no retargeting).
            var attackerAssets = MarineAssets.Load(_gameDir, notes, swap ? "basic" : null);
            Preload(attackerAssets.Rig, campaign, _row.AttackerClip);
            MarineAssets victimAssets = _row.Target switch
            {
                ExecutionTargets.OrkBoy => OrkAssets.Shared(_gameDir, notes).Build(OrkAssets.LookName(_args.GetValueOrDefault("look")) ?? "boy_melee"),
                ExecutionTargets.ChaosMarine => MarineAssets.Load(_gameDir, notes, swap ? null : "basic"),
                _ => throw new NotSupportedException($"'{_row.Target}' has no model in this view yet (Ork Boy and Chaos Marine rows only)"),
            };
            Preload(victimAssets.Rig, victimRig, _row.VictimClip);

            _attacker = new CharacterViewer(attackerAssets, _row.AttackerClip, gameSpaceRoot: true) { Name = "Attacker" };
            AddChild(_attacker);
            AddWeapon(_attacker, attackerAssets, Wargear(_row.Weapon, swap), notes);
            _victimStart = ToGodot(_row.Offset.X, _row.Offset.Y, _row.Offset.Z);
            _victim = new CharacterViewer(victimAssets, _row.VictimClip, gameSpaceRoot: true) { Name = "Victim", Position = _victimStart, Rotation = new Vector3(0f, _row.RelativeYaw, 0f) };
            AddChild(_victim);

            GD.Print($"EXECTEST row {_row.Id}: attacker {_row.AttackerClip} {_attackerClip.Seconds:F3} s root {_attackerClip.RootSource} ({_attackerClip.Root.Count}); " +
                     $"victim {_row.VictimClip} {_victimClip.Seconds:F3} s root {_victimClip.RootSource} ({_victimClip.Root.Count}) on {victimProfile.Entry}; " +
                     $"offset ({_row.Offset.X:F2}, {_row.Offset.Y:F2}) yaw {_row.RelativeYaw * 180f / MathF.PI:F0}; kill {_row.KillSeconds:F3} s, end {_row.Seconds:F3} s{(swap ? "; races swapped (ours)" : "")}");
            AddStage();
        }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException))
        {
            Fail(ex.ToString());
            return;
        }
        foreach (var note in notes.Distinct()) GD.Print("  note: " + note);
        _shooting = _times.Length > 0;
        _waitFrames = 12;
        Place(0f);
    }

    void Fail(string message)
    {
        GD.PrintErr("execution test: " + message);
        GetTree().Quit(1);
    }

    /// <summary>
    /// Decodes <paramref name="clip"/> from <paramref name="source"/> (the campaign rig) into <paramref name="rig"/>'s cache (the same skeleton by bone order).
    /// The cache keeps the first animation of a name, and the model's own rig can carry a clip of the same name (the MP rig's knife heavy
    /// <c>SM_MEL_CK_XATK_HVY_3.5m</c>, O8's attacker clip). So a refused preload fails the view unless the cached animation is the campaign one; it never
    /// plays the model rig's clip under the row's name.
    /// </summary>
    static void Preload(RigAssets rig, HavokCharacterFile source, string clip)
    {
        if (source.Skeleton.BoneNames.Count != rig.Rig.Skeleton.BoneNames.Count || !source.Skeleton.BoneNames.SequenceEqual(rig.Rig.Skeleton.BoneNames))
            throw new InvalidOperationException($"{clip}: the source skeleton ({source.Skeleton.BoneNames.Count} bones) is not the model's ({rig.Rig.Skeleton.BoneNames.Count})");
        var decoded = source.LoadAnimation(clip);
        if (!rig.Preload(rig.RigKey, clip, decoded) && !ReferenceEquals(rig.Animation(clip), decoded))
            throw new InvalidOperationException($"{clip}: the model's rig already holds a clip of that name; the campaign rig's could not be loaded in its place");
    }

    void AddWeapon(CharacterViewer viewer, MarineAssets assets, string wargear, List<string> notes)
    {
        if (viewer.Skeleton is null) return;
        var weaponNotes = new List<string>();
        var weaponAssets = WeaponAssets.Load(_gameDir, assets, [wargear], weaponNotes);
        var rig = new WeaponRig(viewer.Skeleton, weaponAssets);
        rig.Configure((0, 0, 0, "execution-test", null), null, null, wargear, [], true);
        rig.Update(SmSim.WeaponSlot.Ranged, true, true, 0f, 0.6f);
        viewer.Weapons = rig;
        notes.AddRange(weaponNotes);
        notes.Add($"weapon {wargear}: {string.Join(", ", rig.Description)}");
    }

    /// <summary>Both clips at <paramref name="t"/> seconds, and the nodes on their extracted motion.</summary>
    void Place(float t)
    {
        if (_row is null || _attacker is null || _victim is null || _attackerClip is null || _victimClip is null) return;
        _attacker.Pose(_row.AttackerClip, t, 0f);
        _victim.Pose(_row.VictimClip, t, 0f);
        if (_attackerClip.RootSource == "extracted motion")
        {
            var r = ExecutionRow.RootAt(_attackerClip.Root, t / _attackerClip.Seconds);
            _attacker.Position = ToGodot(r.X, r.Y, r.Z);
            _attacker.Rotation = new Vector3(0f, r.W, 0f);
        }
        if (_victimClip.RootSource == "extracted motion")
        {
            var r = ExecutionRow.RootAt(_victimClip.Root, t / _victimClip.Seconds);
            float c = MathF.Cos(_row.RelativeYaw), s = MathF.Sin(_row.RelativeYaw);
            var world = new Vector2(r.X * c - r.Y * s, r.X * s + r.Y * c);   // the victim's frame turned by its yaw (game space)
            _victim.Position = _victimStart + ToGodot(world.X, world.Y, r.Z);
            _victim.Rotation = new Vector3(0f, _row.RelativeYaw + r.W, 0f);
        }
    }

    void AddStage()
    {
        // Frame the whole move: both starts and both root paths (extracted motion; the victim's in its turned frame), game space.
        var points = new List<Vector2> { Vector2.Zero, new(_row!.Offset.X, _row.Offset.Y) };
        float c = MathF.Cos(_row.RelativeYaw), s = MathF.Sin(_row.RelativeYaw);
        if (_attackerClip!.RootSource == "extracted motion") points.AddRange(_attackerClip.Root.Select(r => new Vector2(r.X, r.Y)));
        if (_victimClip!.RootSource == "extracted motion") points.AddRange(_victimClip.Root.Select(r => new Vector2(_row.Offset.X + r.X * c - r.Y * s, _row.Offset.Y + r.X * s + r.Y * c)));
        float minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        var mid = ToGodot((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, 0.9f);
        var dir = (_args.GetValueOrDefault("view") ?? "side") switch
        {
            "front" => new Vector3(0f, 0.15f, -1f), "back" => new Vector3(0f, 0.2f, 1f), "three" => new Vector3(1f, 0.35f, -0.5f),
            _ => new Vector3(1f, 0.15f, 0f),
        };
        float span = MathF.Max(maxX - minX, maxY - minY) + 2f;
        float distance = MathF.Max(4.5f, span * 0.5f / MathF.Tan(22.5f * MathF.PI / 180f) * 1.1f);
        var camera = new Camera3D { Fov = 45f, Near = 0.05f, Far = 100f, Current = true };
        AddChild(camera);
        camera.Position = mid + dir.Normalized() * distance;
        camera.LookAt(mid, Vector3.Up);
        AddChild(new DirectionalLight3D { LightEnergy = 1.1f, ShadowEnabled = true, RotationDegrees = new Vector3(-45f, 35f, 0f) });
        AddChild(new DirectionalLight3D { LightEnergy = 0.4f, RotationDegrees = new Vector3(-20f, 215f, 0f) });
        var floor = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(30f, 30f) }, Position = new Vector3(mid.X, 0f, mid.Z) };
        floor.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.33f, 0.35f) };
        AddChild(floor);
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.18f, 0.2f, 0.23f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.57f, 0.6f), AmbientLightEnergy = 0.7f,
            },
        });
    }

    public override void _Process(double delta)
    {
        if (_row is null) return;
        if (_waitFrames > 0) { _waitFrames--; return; }
        if (_shooting)
        {
            // Pose, let the frame draw, then save; one still a time.
            if (_nextShot >= _times.Length) { GetTree().Quit(0); _shooting = false; return; }
            float t = _times[_nextShot];
            if (!_posedForShot) { Place(t); _posedForShot = true; _waitFrames = 4; return; }
            string path = System.IO.Path.Combine(_outDir, $"{_name}_t{t.ToString("0.000", CultureInfo.InvariantCulture)}.png");
            System.IO.Directory.CreateDirectory(_outDir);
            var err = GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print(err == Error.Ok ? $"EXECTEST still {t:F3} s -> {path}" : $"EXECTEST still failed: {err}");
            _posedForShot = false;
            _nextShot++;
            return;
        }
        _time += (float)delta * _rate;
        if (_time > _length + 1f)
        {
            if (!_passed) GD.Print($"EXECTEST played {_row.Id} once ({_length:F3} s)");
            _passed = true;
            if (_quit) { GetTree().Quit(0); return; }
            _time = 0f;
        }
        Place(MathF.Min(_time, _length));
    }

    bool _posedForShot;
}
