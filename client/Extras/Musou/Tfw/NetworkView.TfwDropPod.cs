using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmContent;
using SmContent.Extras.LoyalistBarks;
using SmContent.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The drop pod assault on the client (Musou Mode's second support ability; owner, 2026-11-12; docs/drop-pod-assault.md): tag 11's <see cref="TfwFireKind.Pod"/> record becomes the pod's fall (the
/// original <c>DropPod</c> mesh of the singleplayer chunk <c>part_01__s_004_r350_airlock</c>: a campaign-content dependency; a plain capsule, placeholder, when it does not load), the install's own
/// <c>drop_pod_trail</c> fire and smoke behind it, the incoming whistle, the touchdown (<c>droppod_land_close</c>, <c>drop_pod_landing_destruction</c>, the dust, the crater, the install's landing sounds,
/// a hard screen shake) and the five doors dropping open (our approximation: the mesh's door pieces are turned outward about their feet; see <see cref="PodDoorTurn"/>). The sim decides everything but
/// the picture: the Marines are ordinary players on the wire, and the wreck goes on the server's second Pod record. Every number is ours (spec "Added content: Musou: drop pod assault").
/// </summary>
public sealed partial class NetworkView
{
    /// <summary>The pod's mesh and where it lives.</summary>
    const string PodChunk = "part_01__s_004_r350_airlock.oppc", PodMesh = "DropPod";
    /// <summary>The install's sounds (bank <c>vehicles</c>).</summary>
    const string PodIncoming = "play_droppod_incoming", PodLand = "play_droppod_land", PodDoorOpen = "play_droppod_door_open", PodDoorLand = "play_droppod_door_land";
    /// <summary>The whistle starts this long before touchdown (s; ours), and the camera shake at 0 m, its reach (radians, m; ours: the artillery's is 0.05 and 110 m, "hard" is about 2.5 times that).</summary>
    const float PodIncomingLead = 2.4f, PodShakeAtZero = 0.12f, PodShakeReach = 90f;
    /// <summary>The doors open to this angle (degrees) over <see cref="TfwDropPodRules.DoorSeconds"/>.</summary>
    const float PodDoorOpenDegrees = 100f;
    /// <summary>The door's hinge sits this high above the ground (m; the shut door's lower edge in the mesh, measured on the closed pose: about 0.6).</summary>
    const float PodDoorFoot = 0.6f;

    sealed class TfwPodShow
    {
        public required TfwFireEvent Record;
        public Node3D? Root;
        public PodDoor[] Doors = [];
        public bool Started, Landed, Whistled;
        public double LandedAt;
        public double GoneTick = double.PositiveInfinity;
        public FxEffect? Trail;
        /// <summary>The Marines' staggered "For the Emperor": their ids and the tick each begins (null until they have stepped out).</summary>
        public List<(int Id, double Tick, bool Done)>? Shouts;
    }

    /// <summary>A door's node, its closed pose (the node's own transform), where it hinges and which way it falls (Godot space).</summary>
    sealed record PodDoor(MeshInstance3D Node, Transform3D Closed, Vector3 Hinge, Vector3 Axis);

    sealed record PodAssets(StaticModel? Model, IReadOnlyList<DropPodDoor> Doors);

    readonly List<TfwPodShow> _tfwPods = new();
    Task<PodAssets>? _tfwPodLoad;
    ArrayMesh?[]? _tfwPodMeshes;
    IReadOnlyList<DropPodDoor> _tfwPodRig = [];
    bool _tfwPodMeshesBuilt;
    int _tfwPodsPlayed;

    /// <summary>The model's read starts with the first frame of a battle so the first call does not wait for it (a thread; the Godot objects are made on this one).</summary>
    void TfwPodPrewarm()
    {
        if (_tfwPodLoad is not null) return;
        string dir = _gameDir;
        _tfwPodLoad = Task.Run(() =>
        {
            string chunk = Path.Combine(dir, "data", "chunks", PodChunk);
            StaticModel? model = null;
            IReadOnlyList<DropPodDoor> doors = [];
            try { model = StaticModel.Load(chunk, PodMesh, new List<string>()); doors = DropPodRig.Read(chunk); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException) { GD.PrintErr("tfw pod: the model does not load: " + ex.Message); }
            return new PodAssets(model, doors);
        });
    }

    /// <summary>One block of the pod mesh as a one-surface mesh with the original material (null for an unreadable one).</summary>
    void TfwPodBuildMeshes()
    {
        if (_tfwPodMeshesBuilt || _tfwPodLoad is not { IsCompleted: true }) return;
        _tfwPodMeshesBuilt = true;
        if (_tfwPodLoad.IsFaulted || _tfwPodLoad.Result is not { Model: { } model } assets) return;
        _tfwPodRig = assets.Doors;
        foreach (var d in assets.Doors) GD.Print($"tfw pod: door block {d.Block}: hinge {d.Hinge}, outward {d.Outward}, closed origin {d.Closed.Translation}");
        var textures = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        var meshes = new ArrayMesh?[model.Mesh.Blocks.Count];
        foreach (var surface in model.Surfaces)
        {
            if (model.Mesh.Blocks[surface.BlockIndex] is not { } block || block.Positions.Length == 0 || surface.Range.IndexCount < 3) continue;
            var arrays = ModelViewer.BlockArrays(block);
            var indices = new int[surface.Range.IndexCount - surface.Range.IndexCount % 3];
            for (int i = 0; i < indices.Length; i++) indices[i] = block.Indices[surface.Range.StartIndex + i];
            arrays[(int)Mesh.ArrayType.Index] = indices;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(0, ModelViewer.MaterialFor(surface, textures, notes));
            meshes[surface.BlockIndex] = mesh;
        }
        model.ReleaseTextures();
        _tfwPodMeshes = meshes;
        GD.Print($"tfw pod: the model {model.MeshName} of {model.Archive}: {meshes.Count(m => m is not null)} of {meshes.Length} blocks");
        foreach (string note in notes.Take(4)) GD.Print("  tfw pod note: " + note);
    }

    /// <summary>The pod's node: five door pieces (blocks 0 to 4) and the hull and interior (blocks 5 and 6), placed as the mesh has them; a capsule when the model did not load (placeholder).</summary>
    (Node3D Root, PodDoor[] Doors) TfwPodBuild()
    {
        var root = new Node3D { Name = "TfwDropPod", Visible = false };
        var doors = new List<PodDoor>();
        if (_tfwPodMeshes is { } meshes)
        {
            for (int i = 0; i < meshes.Length; i++)
            {
                if (meshes[i] is not { } mesh) continue;
                var node = new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.On, Name = i < 5 ? "Door" + i : i == 5 ? "Hull" : "Inner" };
                root.AddChild(node);
                foreach (var rig in _tfwPodRig)
                {
                    if (rig.Block != i) continue;
                    var closed = PodGodot(rig.Closed);
                    node.Transform = closed;
                    var axis = new Vector3(0f, 1f, 0f).Cross(PodGodot(rig.Outward));   // up x outward (right-handed): turning about it lays the door's top outward
                    doors.Add(new PodDoor(node, closed, PodGodot(rig.Hinge) + Vector3.Up * PodDoorFoot, axis.Normalized()));
                }
            }
        }
        else
        {
            root.AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 3f, Height = 10f }, Position = new Vector3(0f, 5f, 0f), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.37f, 0.4f) }, Name = "Hull" });
        }
        AddChild(root);
        return (root, doors.ToArray());
    }

    /// <summary>A game-space point or direction in Godot's axes (x, z, -y: <see cref="ModelViewer.BlockArrays"/>'s).</summary>
    static Vector3 PodGodot(System.Numerics.Vector3 v) => new(v.X, v.Z, -v.Y);

    /// <summary>A pod-space transform (row vectors, game axes) as the Godot transform of a mesh whose vertices were converted with <see cref="PodGodot"/>: P M P^-1.</summary>
    static Transform3D PodGodot(System.Numerics.Matrix4x4 m)
    {
        Vector3 Axis(System.Numerics.Vector3 game) => PodGodot(System.Numerics.Vector3.TransformNormal(game, m));
        // Godot's x is the game's x, Godot's y the game's z, Godot's z the game's -y.
        return new Transform3D(new Basis(Axis(System.Numerics.Vector3.UnitX), Axis(System.Numerics.Vector3.UnitZ), Axis(-System.Numerics.Vector3.UnitY)), PodGodot(m.Translation));
    }

    /// <summary>A Pod record: a call (the touchdown tick, the caller, the place) or the wreck's end (<see cref="TfwFireEvent.To"/> X set).</summary>
    void TfwPodRecord(in TfwFireEvent f)
    {
        if (f.To.X > 0.5f)
        {
            foreach (var pod in _tfwPods) if (pod.Record.Subject == f.Subject && pod.GoneTick == double.PositiveInfinity && pod.Record.Tick <= f.Tick) { pod.GoneTick = f.Tick; break; }
            return;
        }
        foreach (var pod in _tfwPods) if (pod.Record.Subject == f.Subject && pod.Record.Tick == f.Tick) return;
        _tfwPods.Add(new TfwPodShow { Record = f });
    }

    /// <summary>The pods' frame: the fall, the touchdown, the doors, the end.</summary>
    void TfwPodsFrame(double tick, int rate, bool inField)
    {
        TfwPodPrewarm();
        TfwPodShotFrame();
        if (_tfwPods.Count == 0) return;
        TfwPodBuildMeshes();
        for (int k = _tfwPods.Count - 1; k >= 0; k--)
        {
            var pod = _tfwPods[k];
            double land = pod.Record.Tick, fallStart = land - TfwDropPodRules.FallSeconds * rate;
            var at = ToGodot(pod.Record.At);
            bool over = tick >= pod.GoneTick || (pod.Landed && _connectedFor - pod.LandedAt > 120.0) || (!inField && pod.Landed);
            if (over || tick > land + 200 * rate)
            {
                pod.Root?.QueueFree();
                if (pod.Trail is not null) pod.Trail.Life = 0.0;
                _tfwPods.RemoveAt(k);
                continue;
            }
            if (!pod.Whistled && tick >= land - PodIncomingLead * rate)
            {
                pod.Whistled = true;
                if (CameraDistance(at) < 400f) TfwPostAt(PodIncoming, at + Vector3.Up * 40f);
            }
            if (tick < fallStart) continue;
            if (!_tfwPodMeshesBuilt && _tfwPodLoad is { IsCompleted: false } && tick < land) continue;   // the model is still being read: the pod waits (a late one lands as it is)
            if (pod.Root is null)
            {
                var (root, doors) = TfwPodBuild();
                pod.Root = root;
                pod.Doors = doors;
            }
            if (!pod.Started)
            {
                pod.Started = true;
                _tfwPodsPlayed++;
                if (_fx is { } fx && tick < land) pod.Trail = fx.Play("drop_pod_trail", at + Vector3.Up * TfwDropPodRules.FallHeight, Basis.Identity, 1f, follow: () => pod.Root is { Visible: true } r ? (r.GlobalPosition + Vector3.Up * 10f, Basis.Identity) : null, repeat: true, priority: 3);
            }
            var node = pod.Root!;
            if (tick < land)
            {
                float u = (float)Math.Clamp((tick - fallStart) / (land - fallStart), 0.0, 1.0);
                node.Position = at + Vector3.Up * (TfwDropPodRules.FallHeight * (1f - u * u));   // falls faster as it goes: about 120 m/s at the ground
                node.Visible = true;
                continue;
            }
            node.Position = at;
            node.Visible = true;
            if (!pod.Landed) TfwPodLands(pod, at);
            float open = TfwPodDoorForced ?? Math.Clamp((float)((_connectedFor - pod.LandedAt) / TfwDropPodRules.DoorSeconds), 0f, 1f);
            foreach (var door in pod.Doors) PodDoorTurn(door, open);
            TfwPodShouts(pod, tick, rate);
        }
    }

    /// <summary>
    /// The pod Marines' battle cry (owner, 2026-11-12): once they have stepped out each says a FOR THE EMPEROR take of the voice pack from his own body, one after the other (<see cref="TfwDropPodRules.ShoutDelay"/>, 0.15 to 0.35 s apart,
    /// ours), through the loyalist bark path (its take choice, cooldown and voice emitters; never the Musou key rotation). Which puppets are the pod's: the Space Marine puppets standing within 8 m of the pod when they appear.
    /// </summary>
    void TfwPodShouts(TfwPodShow pod, double tick, int rate)
    {
        double spawn = pod.Record.Tick + (TfwDropPodRules.DoorSeconds + TfwDropPodRules.StepOutSeconds) * rate;
        if (tick < spawn) return;
        if (pod.Shouts is null)
        {
            var ids = new List<int>();
            foreach (var (id, s) in _barkPlayers)
                if (TfwContracts.IsPuppet(id) && s.Alive && System.Numerics.Vector3.Distance(s.Position with { Z = pod.Record.At.Z }, pod.Record.At) <= 8f) ids.Add(id);
            ids.Sort();
            if (ids.Count == 0) { if (tick > spawn + 2 * rate) pod.Shouts = []; return; }   // none seen within 2 s: no cry
            pod.Shouts = new();
            for (int k = 0; k < ids.Count && k < TfwDropPodRules.Marines; k++) pod.Shouts.Add((ids[k], spawn + TfwDropPodRules.ShoutDelay((uint)pod.Record.Tick, k) * rate, false));
        }
        for (int k = 0; k < pod.Shouts.Count; k++)
        {
            var (id, at, done) = pod.Shouts[k];
            if (done || tick < at) continue;
            pod.Shouts[k] = (id, at, true);
            if (_barkPlayers.TryGetValue(id, out var marine) && marine.Alive) BarkSay(id, marine, BarkMoment.Key, (long)pod.Record.Tick * 8 + k, own: false, emperorOnly: true);
        }
    }

    /// <summary>The touchdown: the trail ends, the install's landing fire and the dust, a crater, the install's sounds, the shake.</summary>
    void TfwPodLands(TfwPodShow pod, Vector3 at)
    {
        pod.Landed = true;
        pod.LandedAt = _connectedFor;
        if (pod.Trail is not null) { pod.Trail.Life = 0.0; pod.Trail = null; }
        float distance = CameraDistance(at);
        _tfwCloud?.Burst(at, distance < 120f ? 1f : 0.5f);
        if (_fx is { } fx)
        {
            fx.Play("droppod_land_close", at + Vector3.Up * 0.2f, Basis.Identity, 1f, priority: 3);
            fx.Play("drop_pod_landing_destruction", at + Vector3.Up * 0.2f, Basis.Identity, 1f, priority: 3);
            fx.Play("droppod_engine_cooling_down", at + Vector3.Up * 1f, Basis.Identity, 1f, lifetime: 25f, priority: 1);
            fx.PlaceDataDecal("metal_crater_dif", at + Vector3.Up * 0.1f, Basis.Identity, 12f, 12f, 3f, 60f, PoundRing.Fade);
        }
        float late = distance / SoundSpeed;
        if (distance < 300f)
        {
            TfwPostAt(PodLand, at, late, new VoiceOffset(GainDb: 6f));
            TfwPostAt(PodDoorOpen, at, late + 0.15f);
            TfwPostAt(PodDoorLand, at, late + TfwDropPodRules.DoorSeconds);
        }
        TfwPodShake(distance);
    }

    /// <summary>The landing's shake: 0.12 at the pod falling to nothing at <see cref="PodShakeReach"/>; reduce motion: none.</summary>
    void TfwPodShake(float distance)
    {
        if (UiKit.ReduceMotion || !FxActive || _camera is null) return;
        float k = Math.Clamp(1f - distance / PodShakeReach, 0f, 1f);
        if (k <= 0f) return;
        float amount = PodShakeAtZero * k * MathF.Sqrt(k);
        FxShake(amount);
        _tfwShakeTotal += amount;
    }

    static readonly bool TfwPodTestOn = CmdFlag("tfw-pod-test");
    int _tfwPodStage, _tfwPodFailed, _tfwPodBase;
    double _tfwPodAt = -1, _tfwPodCalled = -1, _tfwPodLandedSeen = -1;
    Num.Vector3 _tfwPodChosen;

    /// <summary><c>--tfw-pod-shots &lt;dir&gt;</c> (a windowed capture aid with <c>--tfw-pod-test --tfw-fire-cam</c>): from the call to 14 s after it the viewport is saved about every 0.05 s as <c>f&lt;ms&gt;.png</c> in that folder, with the camera standing 50 m from the pod's point looking at it.</summary>
    static readonly string? TfwPodShotsDir = CmdArg("tfw-pod-shots");
    /// <summary>Capture aids: <c>--tfw-pod-door 0..1</c> holds the doors at that opening, <c>--tfw-pod-cam d,h</c> puts the camera d metres from the point and h metres up, <c>--tfw-pod-quick</c> quits when the shots end.</summary>
    static readonly float? TfwPodDoorForced = float.TryParse(CmdArg("tfw-pod-door"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float forcedDoor) ? forcedDoor : null;
    static readonly bool TfwPodQuick = CmdFlag("tfw-pod-quick");
    double _tfwPodShotNext = -1, _tfwPodShotEnd;
    bool _tfwPodShotBusy;

    async Task TfwPodShot()
    {
        _tfwPodShotBusy = true;
        try
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            string path = Path.Combine(TfwPodShotsDir!, $"f{(long)(_connectedFor * 1000.0):D8}.png");
            GetViewport().GetTexture().GetImage().SavePng(path);
        }
        finally { _tfwPodShotBusy = false; }
    }

    void TfwPodShotFrame()
    {
        if (TfwPodQuick && _tfwPodShotNext >= 0 && _connectedFor > _tfwPodShotEnd + 0.5) { GetTree().Quit(0); return; }
        if (TfwPodShotsDir is null || _tfwPodShotNext < 0 || _tfwPodShotBusy || DisplayServer.GetName() == "headless" || _connectedFor > _tfwPodShotEnd || _connectedFor < _tfwPodShotNext) return;
        _tfwPodShotNext = _connectedFor + 0.05;
        _ = TfwPodShot();
    }

    void TfwPodCheck(string what, bool passed)
    {
        GD.Print($"TFWTEST {(passed ? "PASS" : "FAIL")}: drop pod: {what}");
        if (!passed) _tfwPodFailed++;
    }

    /// <summary>The AI Marines now in the world as the client sees them (ids from <see cref="TfwContracts.PuppetIdFirst"/>): the allied pair, retainers and a pod's Marines.</summary>
    int TfwPodPuppets() => _client is null ? 0 : _client.RemotePlayers().Players.Count(p => TfwContracts.IsPuppet(p.PlayerId));

    /// <summary>
    /// <c>--tfw-pod-test</c> (a scripted player, not <c>--bot</c>, with <c>--tfw-deploy b --tfw-support droppod</c>): looks ahead, presses the real key B, and checks the call end to end: the pod's record, its
    /// touchdown about <see cref="TfwDropPodRules.LandSeconds"/> later on the crosshair's point, five more AI Marines after the doors, a second call refused (one pod each, and the cooldown), the cooldown on the
    /// HUD, and every Marine and the pod gone after the fight and the retreat. TFWTEST lines; quits 0, or 1 when any failed.
    /// </summary>
    void TfwPodTestFrame(in TfwWarView war)
    {
        if (!TfwPodTestOn || _client is null || !TfwHudModel.InBattle(war.Phase)) return;
        var me = _client.Predicted;
        if (_tfwPodAt < 0) { _tfwPodAt = _connectedFor + (double.TryParse(CmdArg("tfw-pod-delay"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double delay) ? delay : 6.0); return; }
        if (_connectedFor < _tfwPodAt) { if (_tfwPodStage == 0) _pitch = -0.1f; return; }
        switch (_tfwPodStage)
        {
            case 0:
                _tfwPodBase = TfwPodPuppets();
                _tfwPodChosen = TfwCrosshairPoint(me);
                GD.Print($"t {_connectedFor:F1} s: TFWPOD pressing B, crosshair on ({_tfwPodChosen.X:F1}, {_tfwPodChosen.Y:F1}, {_tfwPodChosen.Z:F1}), player at ({me.Position.X:F1}, {me.Position.Y:F1}), {_tfwPodBase} Marines already");
                if (TfwPodShotsDir is not null)
                {
                    Directory.CreateDirectory(TfwPodShotsDir);
                    var away = new Num.Vector3(me.Position.X - _tfwPodChosen.X, me.Position.Y - _tfwPodChosen.Y, 0f);
                    away = away.LengthSquared() > 1f ? Num.Vector3.Normalize(away) : Num.Vector3.UnitY;
                    var cam = (CmdArg("tfw-pod-cam") ?? "46,9").Split(',');
                    float camDistance = float.Parse(cam[0], System.Globalization.CultureInfo.InvariantCulture), camHeight = cam.Length > 1 ? float.Parse(cam[1], System.Globalization.CultureInfo.InvariantCulture) : 9f;
                    _tfwCamEye = ToGodot(_tfwPodChosen + away * camDistance + new Num.Vector3(0f, 0f, camHeight));
                    _tfwCamLook = ToGodot(_tfwPodChosen + new Num.Vector3(0f, 0f, 4f));
                    _tfwPodShotNext = _connectedFor; _tfwPodShotEnd = _connectedFor + (CmdArg("tfw-pod-seconds") is { } secs ? double.Parse(secs, System.Globalization.CultureInfo.InvariantCulture) : 16.0);
                }
                TfwKey(Key.B, true);
                _tfwPodCalled = _connectedFor;
                _tfwPodStage = 1; _tfwPodAt = _connectedFor + 0.3;
                break;
            case 1:
                TfwKey(Key.B, false);
                _tfwPodStage = 2; _tfwPodAt = _connectedFor + 1.2;
                break;
            case 2:
                TfwPodCheck($"the call made a pod: {_tfwPods.Count} on the way", _tfwPods.Count == 1);
                if (_tfwPods.Count == 1)
                {
                    float off = new Num.Vector2(_tfwPods[0].Record.At.X - _tfwPodChosen.X, _tfwPods[0].Record.At.Y - _tfwPodChosen.Y).Length();
                    TfwPodCheck($"its touchdown is {off:F1} m from the crosshair's point", off < 3f);
                }
                TfwPodCheck($"the cooldown shows: {war.Own.OrbitalSeconds} s", war.Own.OrbitalSeconds is > 40 and <= 60);
                _tfwState.Commands.Issue(TfwCommand.DropPod, 0, _tfwPodChosen);   // a second call: refused (one pod each, and the cooldown)
                _tfwPodStage = 3; _tfwPodAt = _connectedFor + 3.0;
                break;
            case 3:
                TfwPodCheck($"the second call was refused: {_tfwPods.Count} pod", _tfwPods.Count == 1);
                TfwPodCheck($"the pod has landed {_connectedFor - _tfwPodCalled:F1} s after the press", _tfwPods.Count == 1 && _tfwPods[0].Landed && _connectedFor - _tfwPodCalled is > 2.6 and < 5.5);
                _tfwPodStage = 4; _tfwPodAt = _connectedFor + 4.0;
                break;
            case 4:
                int now = TfwPodPuppets();
                TfwPodCheck($"the Marines stepped out: {now - _tfwPodBase} more ({now} in all)", now - _tfwPodBase == TfwDropPodRules.Marines);
                _tfwPodStage = 5; _tfwPodAt = _connectedFor + TfwDropPodRules.FightSeconds - 2.0;
                break;
            case 5:
                // A live battle may kill them first (the Orks come to the landing zone): reported, not a failure; the sim's timeline is TfwDropPodTests'.
                int fighting = TfwPodPuppets() - _tfwPodBase;
                GD.Print($"TFWTEST note: drop pod: {fighting} of the Marines still fighting near the end of the 30 s");
                TfwPodCheck($"no more than the squad's five: {fighting}", fighting <= TfwDropPodRules.Marines);
                _tfwPodStage = 6; _tfwPodAt = _connectedFor + TfwDropPodRules.RetreatMaxSeconds + TfwDropPodRules.WreckSeconds + 8.0;
                break;
            case 6:
                TfwPodCheck($"the Marines are gone: {TfwPodPuppets() - _tfwPodBase} more", TfwPodPuppets() - _tfwPodBase == 0);
                TfwPodCheck($"the wreck is gone: {_tfwPods.Count} pods", _tfwPods.Count == 0);
                GD.Print($"TFWTEST drop pod done: {_tfwPodFailed} failed; posted: {string.Join(", ", _tfwFirePosted.Select(kv => $"{kv.Key} {kv.Value}"))}");
                GetTree().Quit(_tfwPodFailed == 0 ? 0 : 1);
                _tfwPodStage = 7;
                break;
        }
    }

    /// <summary>
    /// A door dropped open by <paramref name="open"/> (0 shut, 1 open). The install's pose of each door when shut is read from the pod's object file (<see cref="DropPodRig"/>); the fall is our approximation
    /// (provisional: the original's door animation is Havok data this does not read): each door turns about its foot on the ground ring, outward, to <see cref="PodDoorOpenDegrees"/>, easing out
    /// as it drops (a late, quick fall, a short bounce at the end).
    /// </summary>
    static void PodDoorTurn(PodDoor door, float open)
    {
        float t = open * open;   // a door falls faster as it goes
        float angle = Mathf.DegToRad(PodDoorOpenDegrees) * (t < 1f ? t : 1f);
        if (open >= 1f) angle = Mathf.DegToRad(PodDoorOpenDegrees);
        var turn = new Basis(door.Axis, angle);
        door.Node.Transform = new Transform3D(turn * door.Closed.Basis, door.Hinge + turn * (door.Closed.Origin - door.Hinge));
    }
}
