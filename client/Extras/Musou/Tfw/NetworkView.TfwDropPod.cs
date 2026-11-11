using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmContent;
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

    sealed class TfwPodShow
    {
        public required TfwFireEvent Record;
        public Node3D? Root;
        public MeshInstance3D[] Doors = [];
        public bool Started, Landed, Whistled;
        public double LandedAt;
        public double GoneTick = double.PositiveInfinity;
        public FxEffect? Trail;
    }

    readonly List<TfwPodShow> _tfwPods = new();
    Task<StaticModel?>? _tfwPodLoad;
    ArrayMesh?[]? _tfwPodMeshes;
    bool _tfwPodMeshesBuilt;
    int _tfwPodsPlayed;

    /// <summary>The model's read starts with the first frame of a battle so the first call does not wait for it (a thread; the Godot objects are made on this one).</summary>
    void TfwPodPrewarm()
    {
        if (_tfwPodLoad is not null) return;
        string dir = _gameDir;
        _tfwPodLoad = Task.Run(() =>
        {
            try { return (StaticModel?)StaticModel.Load(Path.Combine(dir, "data", "chunks", PodChunk), PodMesh, new List<string>()); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException) { GD.PrintErr("tfw pod: the model does not load: " + ex.Message); return null; }
        });
    }

    /// <summary>One block of the pod mesh as a one-surface mesh with the original material (null for an unreadable one).</summary>
    void TfwPodBuildMeshes()
    {
        if (_tfwPodMeshesBuilt || _tfwPodLoad is not { IsCompleted: true }) return;
        _tfwPodMeshesBuilt = true;
        if (_tfwPodLoad.IsFaulted || _tfwPodLoad.Result is not { } model) return;
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
    (Node3D Root, MeshInstance3D[] Doors) TfwPodBuild()
    {
        var root = new Node3D { Name = "TfwDropPod", Visible = false };
        var doors = new List<MeshInstance3D>();
        if (_tfwPodMeshes is { } meshes)
        {
            for (int i = 0; i < meshes.Length; i++)
            {
                if (meshes[i] is not { } mesh) continue;
                var node = new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.On, Name = i < 5 ? "Door" + i : i == 5 ? "Hull" : "Inner" };
                root.AddChild(node);
                if (i < 5) doors.Add(node);
            }
        }
        else
        {
            root.AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 3f, Height = 10f }, Position = new Vector3(0f, 5f, 0f), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.37f, 0.4f) }, Name = "Hull" });
        }
        AddChild(root);
        return (root, doors.ToArray());
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
            float open = Math.Clamp((float)((_connectedFor - pod.LandedAt) / TfwDropPodRules.DoorSeconds), 0f, 1f);
            for (int d = 0; d < pod.Doors.Length; d++) PodDoorTurn(pod.Doors[d], d, open);
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

    void TfwPodCheck(string what, bool passed)
    {
        GD.Print($"TFWTEST {(passed ? "PASS" : "FAIL")}: drop pod: {what}");
        if (!passed) _tfwPodFailed++;
    }

    /// <summary>The AI Marines now in the world as the client sees them (ids from <see cref="TfwContracts.PuppetIdFirst"/>): the allied pair, retainers and a pod's Marines.</summary>
    int TfwPodPuppets() => _client is null ? 0 : _client.RemotePlayers().Players.Count(p => TfwContracts.IsPuppet(p.PlayerId));

    /// <summary>
    /// <c>--tfw-pod-test</c> (a scripted player, not <c>--bot</c>, with <c>--tfw-deploy b --tfw-support droppod</c>): looks ahead, presses the real key B, and checks the call end to end: the pod's record, its
    /// touchdown about <see cref="TfwDropPodRules.LandSeconds"/> later on the crosshair's point, four more AI Marines after the doors, a second call refused (one pod each, and the cooldown), the cooldown on the
    /// HUD, and every Marine and the pod gone after the fight and the retreat. TFWTEST lines; quits 0, or 1 when any failed.
    /// </summary>
    void TfwPodTestFrame(in TfwWarView war)
    {
        if (!TfwPodTestOn || _client is null || !TfwHudModel.InBattle(war.Phase)) return;
        var me = _client.Predicted;
        if (_tfwPodAt < 0) { _tfwPodAt = _connectedFor + 6.0; return; }
        if (_connectedFor < _tfwPodAt) { if (_tfwPodStage == 0) _pitch = -0.1f; return; }
        switch (_tfwPodStage)
        {
            case 0:
                _tfwPodBase = TfwPodPuppets();
                _tfwPodChosen = TfwCrosshairPoint(me);
                GD.Print($"t {_connectedFor:F1} s: TFWPOD pressing B, crosshair on ({_tfwPodChosen.X:F1}, {_tfwPodChosen.Y:F1}, {_tfwPodChosen.Z:F1}), player at ({me.Position.X:F1}, {me.Position.Y:F1}), {_tfwPodBase} Marines already");
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
                TfwPodCheck($"still fighting near the end of the 30 s: {TfwPodPuppets() - _tfwPodBase} more", TfwPodPuppets() - _tfwPodBase >= 1);
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
    /// Door <paramref name="index"/> dropped open by <paramref name="open"/> (0 shut, 1 open). Provisional approximation: the mesh holds the five doors as pieces (block 0 apart, blocks 1 to 4 where they
    /// are modelled), with no hinge data read, so each is turned outward about its own foot at the hull's skirt.
    /// </summary>
    static void PodDoorTurn(MeshInstance3D door, int index, float open)
    {
        // Filled in once the door pieces' placement is known (see docs/drop-pod-assault.md): until then the doors stay as modelled.
        _ = door; _ = index; _ = open;
    }
}
