using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Expansion S4 on the client (Musou Mode only): the Scrap Meks and their contraptions (tag 17, <see cref="MekWire"/>; cues <see cref="MusouMekRules.BuildCue"/> to <see cref="MusouMekRules.TurretFireCue"/>).
/// <list type="bullet">
/// <item>*Placeholder demonstration* (no Mek model, no contraption models in the install): the Mek is the Boy's body (the crowd's fallback look) with a drawn wrench at his right hand; each contraption is
/// built from simple shapes in scrap colours (the shield generator a drum with a mast, the beacon a pole with a blinking lamp, the turret a box with a launch tube that turns to its aim, the trap a
/// rusty plate ringed with spikes and a pulsing red light); a build in progress rises out of the ground with its progress; the shield's dome a see-through green sphere of
/// <see cref="MusouMekRules.ShieldRadius"/>.</item>
/// <item>*Original asset loaded* (master_fx and the sound triggers, read at run time): <c>amb_sparks_frequent</c> while a Mek builds, <c>weirdboy_shield_pulse</c> when a dome comes up,
/// <c>ork_shield_break</c> when it is destroyed, <c>rokkit_dmg</c> and the frag grenade's detonation for a contraption destroyed and a trap's blast, <c>droppod_land_close</c> and
/// <c>play_droppod_incoming</c> for a beacon's drop; the turret's rocket is the Rokkit Boy's (its fire, trail, burst and warning, <c>NetworkView.MusouSquigRokkit.cs</c>).</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    /// <summary>The wrench's place at a Mek's right hand (his own space: right, forward, up; m; ours).</summary>
    const float MekToolRight = 0.45f, MekToolForward = 0.25f, MekToolUp = 0.95f;

    sealed class ShownContraption
    {
        public Node3D Root = null!;
        public MeshInstance3D Body = null!;
        public Node3D? Turn;                 // the turret's head
        public OmniLight3D? Light;           // the trap's red pulse, the beacon's lamp
        public MeshInstance3D? Dome;
        public FxEffect? Sparks;
        public MusouContraption Last;
    }

    readonly Dictionary<byte, ShownContraption> _contraptions = new();
    readonly Dictionary<ushort, MeshInstance3D> _mekTools = new();
    readonly HashSet<byte> _contraptionSeen = new();
    readonly HashSet<ushort> _mekSeen = new();
    Mesh? _mekToolMesh;
    readonly Dictionary<MusouContraptionKind, Mesh> _contraptionMeshes = new();
    Mesh? _turretHeadMesh;
    ShaderMaterial? _domeMaterial;
    int _mekBuilds, _mekBuilt, _mekDestroyed, _mekTraps, _mekDrops;

    static readonly string[] MekEffects = ["amb_sparks_frequent", "droppod_land_close", "ork_shield_break", "weirdboy_shield_pulse", "rokkit_dmg"];
    System.Threading.Tasks.Task<(Dictionary<string, CSystem> Systems, Dictionary<string, byte[]> Decals, List<string> Failures)>? _mekFxLoad;
    bool _mekFxReady;

    /// <summary>A cue of expansion S4: its effects and sounds. False for any other value.</summary>
    bool MusouMekCue(in MusouEvent e, IReadOnlyList<MusouShown> shown)
    {
        if (e.Value is < MusouMekRules.BuildCue or > MusouMekRules.TurretFireCue) return false;
        var at = ToGodot(e.Vector);
        bool fx = _fx is not null && FxActive;
        byte which = (byte)e.Player;
        switch (e.Value)
        {
            case MusouMekRules.BuildCue:
                _mekBuilds++;
                break;
            case MusouMekRules.BuiltCue:
                _mekBuilt++;
                if (fx && _tfwState.Contraptions.FirstOrDefault(c => c.Id == which) is { Kind: MusouContraptionKind.ShieldGenerator })
                    _fx!.Play("weirdboy_shield_pulse", at + Vector3.Up * 1f, Basis.Identity, 2.5f, priority: 1);
                break;
            case MusouMekRules.DestroyedCue:
            {
                _mekDestroyed++;
                bool shield = _contraptions.TryGetValue((byte)e.Player, out var gone) && gone.Last.Kind == MusouContraptionKind.ShieldGenerator;
                if (fx) _fx!.Play(shield ? "ork_shield_break" : "rokkit_dmg", at + Vector3.Up * 0.5f, Basis.Identity, shield ? 2f : 1f, priority: 2);
                FragBurst(e.Vector, effects: false);
                RemoveContraption((byte)e.Player);
                break;
            }
            case MusouMekRules.TrapCue:
                _mekTraps++;
                if (fx) _fx!.Play("rokkit_dmg", at + Vector3.Up * 0.2f, Basis.Identity, 0.8f, priority: 2);
                S2Post(SquigKickSound, at, 0f);
                RemoveContraption((byte)e.Player);
                break;
            case MusouMekRules.TrapBlastCue:
                FragBurst(e.Vector);
                if (fx) _fx!.Play("squig_explosion", at, Basis.Identity, 1f, priority: 2);
                break;
            case MusouMekRules.DropCue:
                S2Post(SquigWhistle, at + Vector3.Up * 12f, -2f);
                break;
            case MusouMekRules.DropLandCue:
                _mekDrops++;
                if (fx) _fx!.Play("droppod_land_close", at + Vector3.Up * 0.05f, Basis.Identity, 0.8f, priority: 2);
                FragBurst(e.Vector, effects: false);
                break;
            case MusouMekRules.TurretFireCue:
            {
                // The turret's rocket: from its muzzle as tag 17 last placed it (the event's enemy is the rocket's serial, its player the turret).
                var turret = _tfwState.Contraptions.FirstOrDefault(c => c.Id == which);
                var foot = turret.Id != 0 ? turret.Position : e.Vector;
                var flat = new Num.Vector2(e.Vector.X - foot.X, e.Vector.Y - foot.Y);
                var forward = flat.LengthSquared() > 1e-6f ? Num.Vector2.Normalize(flat) : Num.Vector2.UnitY;
                var muzzle = foot + new Num.Vector3(forward * MusouMekRules.TurretMuzzleForward, MusouMekRules.TurretMuzzleHeight);
                StartRocket(e.Enemy, muzzle, e.Vector);
                break;
            }
        }
        return true;
    }

    /// <summary>Each frame: every Mek's wrench, every contraption as tag 17 has it (its build rising, the turret's aim, the trap's pulse, the beacon's lamp, the dome), the sparks of a build.</summary>
    void MusouMekFrame(IReadOnlyList<MusouShown> shown, double delta)
    {
        MekFxLoad();
        if (_gameSpace is null) return;
        double now = _connectedFor;
        _mekSeen.Clear();
        foreach (var s in shown)
        {
            if (s.Type != MusouEnemyType.ScrapMek) continue;
            _mekSeen.Add(s.Id);
            if (!_mekTools.TryGetValue(s.Id, out var tool))
            {
                tool = new MeshInstance3D { Name = "MekWrench", Mesh = _mekToolMesh ??= MekToolMesh() };
                _gameSpace.AddChild(tool);
                _mekTools[s.Id] = tool;
            }
            tool.Visible = s.State != MusouEnemyState.Dying;
            var right = new Num.Vector3(MathF.Cos(s.Yaw), MathF.Sin(s.Yaw), 0f);
            var ahead = new Num.Vector3(-MathF.Sin(s.Yaw), MathF.Cos(s.Yaw), 0f);
            var hand = s.Position + right * MekToolRight + ahead * MekToolForward + new Num.Vector3(0f, 0f, MekToolUp);
            // At work the wrench swings (ours: 3 swings a second); else it hangs at his side.
            float swing = s.State == MusouEnemyState.Building ? 0.6f * MathF.Sin((float)now * 3f * MathF.Tau) : 0.25f;
            tool.Transform = new Transform3D(new Basis(Vector3.Back, s.Yaw) * new Basis(Vector3.Right, swing), new Vector3(hand.X, hand.Y, hand.Z));
        }
        foreach (var id in _mekTools.Keys.Where(k => !_mekSeen.Contains(k)).ToList()) if (_mekTools.Remove(id, out var t)) t.QueueFree();

        _contraptionSeen.Clear();
        foreach (var c in _tfwState.Contraptions)
        {
            _contraptionSeen.Add(c.Id);
            if (!_contraptions.TryGetValue(c.Id, out var shownC)) _contraptions[c.Id] = shownC = MakeContraption(c);
            shownC.Last = c;
            var p = c.Position;
            float rise = c.State == MusouContraptionState.Building ? 0.15f + 0.85f * c.Progress : 1f;
            shownC.Root.Transform = new Transform3D(Basis.Identity, new Vector3(p.X, p.Y, p.Z));
            shownC.Body.Scale = new Vector3(1f, 1f, rise);
            if (shownC.Turn is { } head) { head.Visible = c.State == MusouContraptionState.Active; head.Rotation = new Vector3(0f, 0f, c.Aim); }
            if (shownC.Light is { } light)
            {
                bool active = c.State == MusouContraptionState.Active;
                light.Visible = active || c.Kind == MusouContraptionKind.Trap;
                light.LightEnergy = c.Kind switch
                {
                    MusouContraptionKind.Trap => (active ? 1.5f : 0.4f) + (active ? 2.5f : 0.3f) * (0.5f + 0.5f * MathF.Sin((float)now * MathF.Tau * 1.5f)),   // ours: a slow 1.5 Hz pulse
                    _ => (c.DropInFlight ? 6f : 2f) * (((int)(now * (c.DropInFlight ? 6 : 1.5))) % 2 == 0 ? 1f : 0.15f),   // the beacon's lamp blinks, faster with a drop on its way
                };
            }
            if (shownC.Dome is { } dome) dome.Visible = c.State == MusouContraptionState.Active;
            // A build in progress throws sparks (the data's ambient sparks).
            bool building = c.State == MusouContraptionState.Building;
            if (building && shownC.Sparks is null && _fx is not null && FxActive && _mekFxReady)
                shownC.Sparks = _fx.Play("amb_sparks_frequent", ToGodot(p + new Num.Vector3(0f, 0f, 0.6f)), Basis.Identity, 0.7f, repeat: true, priority: 1);
            if (!building && shownC.Sparks is { } sparks) { sparks.Repeat = false; sparks.Life = Math.Min(sparks.Life, sparks.Age + 0.3); shownC.Sparks = null; }
        }
        foreach (var id in _contraptions.Keys.Where(k => !_contraptionSeen.Contains(k)).ToList()) RemoveContraption(id);
    }

    void RemoveContraption(byte id)
    {
        if (!_contraptions.Remove(id, out var c)) return;
        if (c.Sparks is { } sparks) { sparks.Repeat = false; sparks.Life = Math.Min(sparks.Life, sparks.Age + 0.2); }
        c.Root.QueueFree();
    }

    ShownContraption MakeContraption(in MusouContraption c)
    {
        var root = new Node3D { Name = $"Contraption{c.Id}" };
        _gameSpace!.AddChild(root);
        var body = new MeshInstance3D { Name = "Body", Mesh = ContraptionMesh(c.Kind) };
        root.AddChild(body);
        var shown = new ShownContraption { Root = root, Body = body, Last = c };
        switch (c.Kind)
        {
            case MusouContraptionKind.Turret:
                var head = new Node3D { Name = "Head", Position = new Vector3(0f, 0f, MusouMekRules.TurretMuzzleHeight) };
                head.AddChild(new MeshInstance3D { Mesh = _turretHeadMesh ??= TurretHeadMesh() });
                root.AddChild(head);
                shown.Turn = head;
                break;
            case MusouContraptionKind.Trap:
                shown.Light = new OmniLight3D { LightColor = new Color(1f, 0.08f, 0.04f), OmniRange = MusouMekRules.TrapRadius + 1.5f, ShadowEnabled = false, Position = new Vector3(0f, 0f, 0.6f) };
                root.AddChild(shown.Light);
                break;
            case MusouContraptionKind.Beacon:
                shown.Light = new OmniLight3D { LightColor = new Color(1f, 0.55f, 0.1f), OmniRange = 8f, ShadowEnabled = false, Position = new Vector3(0f, 0f, 3.4f) };
                root.AddChild(shown.Light);
                break;
            case MusouContraptionKind.ShieldGenerator:
                shown.Dome = new MeshInstance3D
                {
                    Name = "Dome",
                    Mesh = new SphereMesh { Radius = MusouMekRules.ShieldRadius, Height = MusouMekRules.ShieldRadius * 2f, RadialSegments = 48, Rings = 24 },
                    MaterialOverride = _domeMaterial ??= DomeMaterial(),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                root.AddChild(shown.Dome);
                break;
        }
        return shown;
    }

    /// <summary>The dome (ours): a see-through Ork-green shell, brighter at its rim, with slow bands running up it. *Placeholder demonstration*.</summary>
    static ShaderMaterial DomeMaterial() => new()
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode blend_add, depth_draw_never, cull_disabled, unshaded;
                varying vec3 local;
                void vertex() { local = VERTEX; }
                void fragment() {
                    float rim = pow(1.0 - abs(dot(normalize(NORMAL), normalize(VIEW))), 2.5);
                    float bands = 0.5 + 0.5 * sin(local.y * 3.0 - TIME * 2.0);
                    vec3 green = vec3(0.25, 0.95, 0.35);
                    ALBEDO = green * (0.05 + 0.55 * rim + 0.06 * bands);
                }
                """,
        },
    };

    /// <summary>A contraption's body in game axes (Z up), as tall as its shot cylinder (<see cref="MusouMekRules.Body"/>). *Placeholder demonstration*: scrap iron, rust and Ork yellow.</summary>
    Mesh ContraptionMesh(MusouContraptionKind kind)
    {
        if (_contraptionMeshes.TryGetValue(kind, out var cached)) return cached;
        var mesh = new ArrayMesh();
        var iron = new StandardMaterial3D { AlbedoColor = new Color(0.24f, 0.23f, 0.21f), Metallic = 0.6f, Roughness = 0.6f };
        var rust = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.18f, 0.08f), Metallic = 0.4f, Roughness = 0.7f };
        var yellow = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.62f, 0.1f), Metallic = 0.3f, Roughness = 0.6f };
        var red = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.08f, 0.04f), EmissionEnabled = true, Emission = new Color(1f, 0.05f, 0.02f), EmissionEnergyMultiplier = 2f };
        var green = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.9f, 0.3f), EmissionEnabled = true, Emission = new Color(0.2f, 1f, 0.3f), EmissionEnergyMultiplier = 1.5f };
        var upright = new Basis(Vector3.Right, MathF.PI / 2f);   // Godot's cylinders stand along Y: a quarter turn stands them along the game's Z
        Transform3D Up(float x, float y, float z) => new(upright, new Vector3(x, y, z));
        switch (kind)
        {
            case MusouContraptionKind.ShieldGenerator:
                AddPart(mesh, new CylinderMesh { TopRadius = 0.8f, BottomRadius = 0.9f, Height = 1.2f }, Up(0f, 0f, 0.6f), iron);
                AddPart(mesh, new CylinderMesh { TopRadius = 0.95f, BottomRadius = 0.95f, Height = 0.15f }, Up(0f, 0f, 0.9f), yellow);
                AddPart(mesh, new CylinderMesh { TopRadius = 0.08f, BottomRadius = 0.12f, Height = 1.2f }, Up(0f, 0f, 1.8f), rust);
                AddPart(mesh, new SphereMesh { Radius = 0.25f, Height = 0.5f }, new Transform3D(Basis.Identity, new Vector3(0f, 0f, 2.45f)), green);
                break;
            case MusouContraptionKind.Beacon:
                AddPart(mesh, new BoxMesh { Size = new Vector3(1.0f, 1.0f, 0.5f) }, new Transform3D(Basis.Identity, new Vector3(0f, 0f, 0.25f)), iron);
                AddPart(mesh, new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.12f, Height = 2.8f }, Up(0f, 0f, 1.9f), rust);
                AddPart(mesh, new BoxMesh { Size = new Vector3(0.6f, 0.05f, 0.4f) }, new Transform3D(Basis.Identity, new Vector3(0.3f, 0f, 2.7f)), yellow);   // the glyph banner
                AddPart(mesh, new SphereMesh { Radius = 0.2f, Height = 0.4f }, new Transform3D(Basis.Identity, new Vector3(0f, 0f, 3.35f)), yellow);
                break;
            case MusouContraptionKind.Turret:
                AddPart(mesh, new CylinderMesh { TopRadius = 0.7f, BottomRadius = 0.9f, Height = 0.5f }, Up(0f, 0f, 0.25f), iron);
                AddPart(mesh, new BoxMesh { Size = new Vector3(0.6f, 0.6f, 0.8f) }, new Transform3D(Basis.Identity, new Vector3(0f, 0f, 0.9f)), rust);
                break;
            default:   // the trap: a rusty plate ringed with spikes, a red eye in the middle
                AddPart(mesh, new CylinderMesh { TopRadius = 1.1f, BottomRadius = 1.2f, Height = 0.12f }, Up(0f, 0f, 0.06f), rust);
                for (int k = 0; k < 12; k++)
                {
                    float a = k * MathF.Tau / 12f;
                    AddPart(mesh, new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.09f, Height = 0.5f }, Up(MathF.Cos(a) * 0.95f, MathF.Sin(a) * 0.95f, 0.35f), iron);
                }
                for (int k = 0; k < 6; k++)
                {
                    float a = (k + 0.5f) * MathF.Tau / 6f;
                    AddPart(mesh, new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.07f, Height = 0.35f }, Up(MathF.Cos(a) * 0.5f, MathF.Sin(a) * 0.5f, 0.28f), iron);
                }
                AddPart(mesh, new SphereMesh { Radius = 0.15f, Height = 0.3f }, new Transform3D(Basis.Identity, new Vector3(0f, 0f, 0.18f)), red);
                break;
        }
        _contraptionMeshes[kind] = mesh;
        return mesh;
    }

    /// <summary>The turret's head in game axes, facing +Y at yaw 0: the launch tube with a rusty ring. *Placeholder demonstration*.</summary>
    static Mesh TurretHeadMesh()
    {
        var mesh = new ArrayMesh();
        var iron = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.2f, 0.19f), Metallic = 0.7f, Roughness = 0.5f };
        var yellow = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.62f, 0.1f), Metallic = 0.3f, Roughness = 0.6f };
        AddPart(mesh, new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0.18f, Height = 1.4f }, Transform3D.Identity, iron);   // Godot's cylinder along Y = the game's forward
        AddPart(mesh, new CylinderMesh { TopRadius = 0.22f, BottomRadius = 0.22f, Height = 0.15f }, new Transform3D(Basis.Identity, new Vector3(0f, 0.55f, 0f)), yellow);
        return mesh;
    }

    /// <summary>The Mek's wrench in game axes (his hand at the origin, the head up): a handle and a jaw. *Placeholder demonstration*.</summary>
    static Mesh MekToolMesh()
    {
        var mesh = new ArrayMesh();
        var iron = new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.3f, 0.28f), Metallic = 0.8f, Roughness = 0.4f };
        var rust = new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.2f, 0.08f), Metallic = 0.4f, Roughness = 0.7f };
        AddPart(mesh, new BoxMesh { Size = new Vector3(0.07f, 0.07f, 0.7f) }, new Transform3D(Basis.Identity, new Vector3(0f, 0f, 0.2f)), iron);
        AddPart(mesh, new BoxMesh { Size = new Vector3(0.12f, 0.28f, 0.12f) }, new Transform3D(Basis.Identity, new Vector3(0f, 0.06f, 0.6f)), rust);
        AddPart(mesh, new BoxMesh { Size = new Vector3(0.12f, 0.08f, 0.2f) }, new Transform3D(Basis.Identity, new Vector3(0f, 0.17f, 0.72f)), rust);
        return mesh;
    }

    static void AddPart(ArrayMesh mesh, PrimitiveMesh p, Transform3D t, Material m)
    {
        var arrays = p.GetMeshArrays();
        var v = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
        var n = (Vector3[])arrays[(int)Mesh.ArrayType.Normal];
        for (int k = 0; k < v.Length; k++) { v[k] = t * v[k]; n[k] = (t.Basis * n[k]).Normalized(); }
        arrays[(int)Mesh.ArrayType.Vertex] = v; arrays[(int)Mesh.ArrayType.Normal] = n;
        arrays[(int)Mesh.ArrayType.Tangent] = default(Variant);
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, m);
    }

    /// <summary>S4's effects (no catalogue the client reads names them): compiled once on a worker from master_fx when the effect player is up.</summary>
    void MekFxLoad()
    {
        if (_mekFxReady || _fx is not { IsReady: true, Library: { } library }) return;
        _mekFxLoad ??= System.Threading.Tasks.Task.Run(() =>
        {
            var failures = new List<string>();
            var systems = CompileSystems(library, MekEffects, failures);
            return (new Dictionary<string, CSystem>(systems), new Dictionary<string, byte[]>(library.Textures(FxPlayer.DecalNames(systems.Values))), failures);
        });
        if (!_mekFxLoad.IsCompleted) return;
        _mekFxReady = true;
        if (_mekFxLoad.IsFaulted) { GD.PrintErr("musou s4: effects: " + _mekFxLoad.Exception?.GetBaseException().Message); return; }
        var (systems, decals, failed) = _mekFxLoad.Result;
        _fx.Preload(systems, decals);
        GD.Print($"musou s4: {systems.Count} effects compiled ({string.Join(", ", systems.Keys)}){(failed.Count > 0 ? "; failed: " + string.Join(", ", failed) : "")}");
    }

    /// <summary>Capture aid (<c>--tfw-fight-meks</c>): the middle of the contraptions within 60 m of <paramref name="me"/> (null: none).</summary>
    Num.Vector3? MekLook(Num.Vector3 me)
    {
        var sum = Num.Vector3.Zero; int n = 0;
        foreach (var c in _tfwState.Contraptions) if (Num.Vector3.Distance(c.Position, me) < 60f) { sum += c.Position; n++; }
        return n > 0 ? sum / n : null;
    }

    /// <summary>For the 10 s log line.</summary>
    string MusouMekNote() => $"meks: contraptions shown {_contraptions.Count} (builds {_mekBuilds}, built {_mekBuilt}, destroyed {_mekDestroyed}, traps sprung {_mekTraps}, drops {_mekDrops}), wrenches {_mekTools.Count}";
}
