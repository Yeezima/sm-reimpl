using System;
using System.Collections.Generic;
using Godot;
using SmClient.Looks;
using SmContent.Extras.Musou;
using SmContent;
using SmNet;
using SmSim;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// The Musou Dreadnought on screen (D2; docs/musou-dreadnought-design.md section 6; added content, Musou only): a player of the Dreadnought class wears the install's model on its own rig
/// (<see cref="DreadnoughtLooks"/>; until it is built, or when the install cannot give it, the Tactical stands in) and is posed from the snapshot alone: the entrance clip when a life begins (the client counts
/// the same 4.833 s the sim's input lock does), the eight-way walk and run clips by speed with the aim pitch laid over them, the sprint clip, the idle, and the death clip held. Which clip plays is **ours**
/// (provisional approximation; the behaviour graph is not evaluated). Strikes, fire and reload poses are added by the later milestones.
/// </summary>
public partial class NetworkView
{
    const string DreadnoughtKey = "dreadnought";
    /// <summary>Test aid <c>--chaos-dreadnought</c>: every Dreadnought wears the Chaos model and fires the reaper cannon's own sounds and effects (the sim's numbers are the same). Not in the customizer (Musou players are Imperial).</summary>
    static readonly bool DreadChaos = CmdFlag("chaos-dreadnought");
    DreadnoughtLooks? _dreadLooks;
    readonly Dictionary<int, DreadnoughtTrack> _dreadTracks = new();

    sealed class DreadnoughtTrack
    {
        public bool WasAlive, EngineOn, Seen, ClassDread, HasPos, Corpse, StepDust, Placed;
        /// <summary>The character wearing his model (the markers' skeleton), set each frame he is posed.</summary>
        public CharacterViewer? Viewer;
        public float PrevCooldown, PrevShotCd, PrevMeltaCd;
        public string Label = "";
        public Vector3 PrevPos;
        public int PrevClip;
        public double Born = double.NegativeInfinity, Died = double.NegativeInfinity;
        public SoundEmitter Emitter = new();
        public Vector3 Last;
        public float Stride;
        public int Strike;
        public float ReloadLeft;
        /// <summary>The frame-rate clip clocks of the arm's strike (own player: <see cref="OwnClipClock"/>, others: <see cref="ClipClock"/>).</summary>
        public readonly OwnClipClock OwnStrike = new();
        public readonly ClipClock OtherStrike = new();
        /// <summary>Every bone's rotation last frame (<see cref="DreadnoughtArmCap"/>).</summary>
        public Quaternion[] Cap = new Quaternion[0]; public bool CapHave; public double CapAt; public ulong CapFrame;
    }

    /// <summary>The Dreadnought's class index in this match's config, or -1 (Classic and every config without him), found once: every per-frame hook returns at once when it is -1.</summary>
    int _dreadIndex = -2;
    int DreadIndex => _dreadIndex != -2 ? _dreadIndex : _config is null ? -1 : _dreadIndex = DreadnoughtClassIndex();
    bool IsDreadnoughtClass(int playerClass) => DreadIndex >= 0 && playerClass == DreadIndex;

    /// <summary>Whether player <paramref name="id"/> (the own or one seen) is a Dreadnought now: his swing's cleave and drawn arc are 1.5 times as long (<see cref="DreadnoughtRules.CleaveReachScale"/>).</summary>
    bool DreadnoughtPlayer(int id)
    {
        if (DreadIndex < 0 || _client is null) return false;
        if (id == _client.PlayerId) return IsDreadnoughtClass(_client.Predicted.Class);
        foreach (var o in _lastOthers) if (o.PlayerId == id) return IsDreadnoughtClass(o.Class);
        return false;
    }

    /// <summary>The characters wearing his model (a set: no per-frame string work; empty without a Dreadnought in the match).</summary>
    readonly HashSet<CharacterViewer> _dreadCharacters = new();
    bool IsDreadnoughtCharacter(CharacterViewer c) => _dreadCharacters.Count > 0 && _dreadCharacters.Contains(c);

    DreadnoughtTrack DreadTrack(int id) { if (!_dreadTracks.TryGetValue(id, out var t)) _dreadTracks[id] = t = new DreadnoughtTrack(); return t; }

    partial void ExtrasEnsureModel(CharacterViewer? current, int playerId, bool local, PlayerState state, ref CharacterViewer? result)
    {
        if (DreadIndex < 0) return;   // Classic and every config without him: nothing here
        bool want = IsDreadnoughtClass(state.Class);
        var track = DreadTrack(playerId);
        // His entrance starts when a player seen as something else (the hub's Tactical stand-in) becomes him: the landing. A first sight as the Dreadnought (a join in a battle) does not replay it.
        if (want && !track.ClassDread && track.Seen) track.Born = _connectedFor;
        track.Seen = true; track.ClassDread = want;
        if (!want)
        {
            // A Dreadnought turned back into a marine (the hub's Tactical stand-in): the rig is another one, so it is dropped for the placeholder and the usual look swap follows.
            if (current is not null && IsDreadnoughtCharacter(current) && _assets is not null) { _dreadCharacters.Remove(current); current.QueueFree(); result = Spawn(playerId, local, _assets, LookKeys.Placeholder); }
            return;
        }
        if (current is not null && IsDreadnoughtCharacter(current)) { result = current; return; }
        if (_fx is not null && _fx.LightCaps.Count == 0) DreadRegisterLightCaps();   // per view (review): the caps go with this view's effect player
        _dreadLooks ??= new DreadnoughtLooks(_gameDir, DreadChaos);
        bool wasDone = _dreadLooks.Done;
        _dreadLooks.Step();
        if (!wasDone && _dreadLooks.Done)
        {
            foreach (var n in _dreadLooks.Notes) { _notes.Add(n); GD.Print("  note: " + n); }
            _dreadLooks.Notes.Clear();
        }
        if (_dreadLooks.Assets is { } assets)
        {
            if (current is not null) { _dreadCharacters.Remove(current); current.QueueFree(); }
            result = Spawn(playerId, local, assets, DreadnoughtKey);
            _dreadCharacters.Add(result);
            if (result.GetNodeOrNull<Label3D>("Nameplate") is { } plate) plate.Position = new Vector3(0, 0, _dreadLooks.Height + 0.3f);
        }
        // not built yet (or failed): result stays null and the usual path shows the Tactical stand-in
    }

    partial void ExtrasPlace(CharacterViewer character, PlayerState state, bool own, ref bool handled)
    {
        if (!IsDreadnoughtCharacter(character)) return;
        handled = true;
        var t = DreadTrack(state.PlayerId);
        bool first = !t.Placed;   // the first time this character is posed (a join in a battle, or the model finishing its load): no edge to read yet, so no entrance replays
        t.Placed = true;
        t.Viewer = character;
        // A new life (the dead to alive edge) or a jump of the body while alive (a war redeploy, an extraction or a call-back: the server re-locks the entrance at each) starts the entrance clip.
        var pos = new Vector3(state.Position.X, state.Position.Y, state.Position.Z);
        bool jumped = t.HasPos && new Vector2(pos.X - t.PrevPos.X, pos.Y - t.PrevPos.Y).Length() > DreadnoughtTeleportMetres;
        if (state.Alive && !first && (!t.WasAlive || jumped)) t.Born = _connectedFor;
        t.PrevPos = pos; t.HasPos = true;
        t.WasAlive = true; t.Corpse = false;
        character.ClearPoseLayers();
        character.ClearTurnPose();
        float yaw = own ? DisplayYaw(state) : state.Yaw;
        character.Transform = new Transform3D(new Basis(new Vector3(0, 0, 1), yaw), new Vector3(state.Position.X, state.Position.Y, state.Position.Z));
        if (character.GetNodeOrNull<Node3D>("ChargeGlow") is { } glow) glow.Visible = false;
        character.Visible = true;
        double since = _connectedFor - t.Born;
        DreadnoughtSounds(t, state, own, since < DreadnoughtRig.EntranceSeconds);
        DreadnoughtEffects(t, state, since < DreadnoughtRig.EntranceSeconds);
        DreadnoughtFeelStep(t, state, own, since < DreadnoughtRig.EntranceSeconds, since);
        if (since < DreadnoughtRig.EntranceSeconds) { character.Pose(DreadnoughtRig.Entrance, (float)since, 0.1f); return; }
        if (_config is { } cfg)
        {
            // the charge (Sprint + Melee) and its collision stop, then the arm's strike at the simulation's time
            if (state.Rush is RushPhase.Rush or RushPhase.Collision)
            {
                float rt = SmSim.Rush.Time(state, cfg);
                if (character.Pose(state.Rush == RushPhase.Rush ? DreadnoughtRig.RushAttack : DreadnoughtRig.Collision, rt, 0.12f)) return;
            }
            if (state.MeleeStep > 0)
            {
                var list = state.MeleeHeavy ? DreadnoughtRig.HeavyStrikes : DreadnoughtRig.LightStrikes;
                if (state.MeleeStep <= list.Length && character.Pose(list[state.MeleeStep - 1], DreadnoughtStrikeClipTime(t, state, own), DreadnoughtStrikeBlend)) { DreadnoughtArmCap(character, t); return; }
            }
            else { t.OwnStrike.Stop(); t.OtherStrike.Stop(); t.CapHave = false; }
        }
        DreadnoughtLocomotion(character, state);
    }

    /// <summary>
    /// The seconds into the strike's clip to show this frame (owner, 2026-11-15: his melee "very choppy and poppy": "sync, smooth, blend, and transition"). The strike's time is the simulation's, which stands still between two
    /// ticks (the own player's, predicted) or two snapshots (the others', interpolated), so the clip was posed at a stepped time and froze and jumped at 30 Hz or less; it now runs on the frame-rate clocks every other
    /// strike uses (<see cref="OwnClipClock"/> for the own player, <see cref="ClipClock"/> for the others), then is multiplied by <see cref="DreadnoughtRules.MeleeSpeed"/>: the sim runs the strike 1.3 times as fast as the data's
    /// clip, so the clip plays 1.3 times as fast and its hit frame is at the sim's hit time.
    /// </summary>
    float DreadnoughtStrikeClipTime(DreadnoughtTrack t, PlayerState s, bool own)
    {
        long key = ClipClock.Key(3, s.MeleeStep, s.MeleeHeavy ? 1 : 0, s.MeleeChoice);
        float simTime = MathF.Max(0f, s.MeleeTime);
        if (DreadRawStrike) return simTime * DreadnoughtRules.MeleeSpeed;   // test aid: the strike's time as the tick or snapshot carries it (the look before the smoothing)
        float shown = own && _client is not null && _config is not null
            ? t.OwnStrike.Step(key, simTime, _client.ClipFraction, _config.TickSeconds, float.MaxValue, _client.ClipLead)
            : t.OtherStrike.Step(key, simTime, (float)GetProcessDeltaTime(), float.MaxValue);
        return shown * DreadnoughtRules.MeleeSpeed;
    }

    /// <summary>
    /// The turn rate (degrees a second; **ours (estimate)**) no bone of his may exceed while a strike is posed, as a limit per frame of at most 1/60 s of it (60 degrees). `DN_MEL_XATK_02_3.5m` twists `RIG_LHand` about 29 degrees per 1/60 s of clip
    /// (the sampler is smooth: checked with `HavokSplineAnimation.SampleRaw`, no flipped control points), which at his 1.3 rate is 2260 degrees a second, 38 a frame at 60 frames a second: the authored twist passes. What the cap stops is a frame that runs
    /// long (a hitch, a slow machine, a movie of a live run) taking the clip on by that much more, or a chained swing's first cross-fade frame, and turning a hand, hose or belt up to 180 degrees in one frame. It runs after the viewer has applied the frame
    /// (<see cref="DreadLateNode"/>), so the entry cross-fade, which the viewer advances in its own _Process, cannot undo it.
    /// </summary>
    const float DreadnoughtArmTurnRate = 3600f;

    /// <summary>A node that runs after every character has been posed this frame (process priority 900, before the pose-pop probe's 1000).</summary>
    sealed partial class DreadLateNode : Node
    {
        public Action? Tick;
        public override void _Process(double delta) => Tick?.Invoke();
    }
    DreadLateNode? _dreadLate;

    void DreadnoughtLateStart()
    {
        if (_dreadLate is not null) return;
        _dreadLate = new DreadLateNode { Name = "DreadLate", ProcessPriority = 900, Tick = DreadnoughtLateCaps };
        AddChild(_dreadLate);
    }

    /// <summary>Marks the strike pose of this frame for the late cap (<see cref="DreadnoughtLateCaps"/>).</summary>
    void DreadnoughtArmCap(CharacterViewer character, DreadnoughtTrack t)
    {
        DreadnoughtLateStart();
        t.CapFrame = Engine.GetProcessFrames();
    }

    void DreadnoughtLateCaps()
    {
        ulong frame = Engine.GetProcessFrames();
        foreach (var t in _dreadTracks.Values)
        {
            if (t.CapFrame != frame || t.Viewer is not { } v || !IsInstanceValid(v) || v.Skeleton is not { } sk) { t.CapHave = false; continue; }
            int n = sk.GetBoneCount();
            if (t.Cap.Length != n) { t.Cap = new Quaternion[n]; t.CapHave = false; }
            float dt = (float)(_connectedFor - t.CapAt);
            t.CapAt = _connectedFor;
            bool use = t.CapHave && dt > 0f && dt < 0.25f;
            float limit = Mathf.DegToRad(DreadnoughtArmTurnRate) * Math.Min(dt, 1f / 60f);
            for (int i = 0; i < n; i++)
            {
                var q = sk.GetBonePoseRotation(i);
                if (use)
                {
                    var last = t.Cap[i];
                    if (q.Dot(last) < 0f) q = -q;
                    float angle = 2f * MathF.Acos(Math.Min(1f, MathF.Abs(q.Dot(last))));
                    if (angle > limit && angle > 1e-4f) { q = last.Slerp(q, limit / angle).Normalized(); sk.SetBonePoseRotation(i, q); }
                }
                t.Cap[i] = q;
            }
            t.CapHave = true;
        }
    }

    /// <summary>Test aid <c>--dn-raw-strike</c>: the strike clip at the simulation's stepped time, not the frame-rate clock (the before of the smoothing's before-and-after clip).</summary>
    static readonly bool DreadRawStrike = CmdFlag("dn-raw-strike");

    /// <summary>The cross-fade into a strike (s): the data's 0.1 s entry and 0.167 s restart, taken at the clip rate; the viewer lengthens a fade to its floor (0.125 s) so no bone turns more than a frame's tolerance.</summary>
    const float DreadnoughtStrikeBlend = 0.15f;

    /// <summary>A body that moves this far in one frame while alive was moved by the server (a redeploy, an extraction, a call-back): a new entrance (ours, a heuristic: the server's lock has no wire signal).</summary>
    const float DreadnoughtTeleportMetres = 6f;
    /// <summary>How long a dead Dreadnought's body is shown (data: death_time 10 s).</summary>
    const double DreadnoughtCorpseSeconds = 10.0;

    /// <summary>
    /// A dead Dreadnought (called by TrackDeath, which does not place the dead): the death clip at its time, held on its last frame, shown for <see cref="DreadnoughtCorpseSeconds"/>; no ragdoll.
    /// </summary>
    partial void ExtrasDeadPlace(CharacterViewer character, PlayerState state, bool own, ref bool keep)
    {
        if (!IsDreadnoughtCharacter(character)) return;
        var t = DreadTrack(state.PlayerId);
        if (t.WasAlive) { t.Died = _connectedFor; t.WasAlive = false; t.EngineOn = false; _sound?.StopEmitter(t.Emitter); DreadnoughtFeelDeath(t, state, own); }
        double dead = _connectedFor - t.Died;
        if (dead > DreadnoughtCorpseSeconds || double.IsInfinity(dead)) { character.Visible = false; return; }
        if (!t.Corpse)
        {
            t.Corpse = true;
            character.ClearPoseLayers(); character.ClearTurnPose();
            if (character.GetNodeOrNull<Node3D>("ChargeGlow") is { } glow) glow.Visible = false;
        }
        float yaw = own ? DisplayYaw(state) : state.Yaw;
        character.Transform = new Transform3D(new Basis(new Vector3(0, 0, 1), yaw), new Vector3(state.Position.X, state.Position.Y, state.Position.Z));
        character.Pose(DreadnoughtRig.Death, (float)Math.Min(dead, DreadnoughtRig.DeathSeconds), 0.2f);
        character.Visible = true;
        keep = true;
    }

    /// <summary>The Dreadnought's wargear (cannon, melta, and the Chaos reaper cannon under the test aid) read into the effect catalogue: no class carries them, which is why the catalogue had no bindings for them before.</summary>
    partial void ExtrasEffectWargear(List<string> names, List<string> effects)
    {
        if (_config is null || !_config.Extras.HasFlag(ContentExtras.Musou)) return;
        effects.AddRange(DreadnoughtArm.AllEffectNames());
        names.Add(DreadnoughtRules.CannonWargear); names.Add(DreadnoughtRules.MeltaWargear);
        if (DreadChaos) names.Add(DreadnoughtRig.ChaosCannonWargear);
    }

    // ---- effects (D7): the install's effects through the effect player ----
    const string DreadDustEffect = "jump_land";
    /// <summary>The footfall dust's scale (ours, the size of his foot).</summary>
    const float DreadDustScale = 2.5f;

    /// <summary>
    /// The barrel for the shot path's effect markers (<see cref="ExtrasFxPlace"/>): ahead of his right shoulder for the cannon, his left for the melta (**ours**, as the flash had been placed), with the aim's axes; nothing for any
    /// other shooter. The state is the own prediction or the last snapshot's.
    /// </summary>
    partial void ExtrasFxPlace(int shooter, string marker, ref (Vector3, Basis)? place)
    {
        if (DreadIndex < 0 || _client is null) return;
        PlayerState s = default; bool found = false;
        if (shooter == _client.PlayerId) { s = _client.Predicted; found = true; }
        else foreach (var o in _lastOthers) if (o.PlayerId == shooter) { s = o; found = true; break; }
        if (!found || !IsDreadnoughtClass(s.Class)) return;
        float cp = MathF.Cos(s.Pitch);
        var fwd = new Vector3(-MathF.Sin(s.Yaw) * cp, MathF.Cos(s.Yaw) * cp, MathF.Sin(s.Pitch));
        var right = new Vector3(MathF.Cos(s.Yaw), MathF.Sin(s.Yaw), 0f);
        float side = s.Slot == WeaponSlot.Pistol ? -0.8f : 0.8f;
        var muzzle = new Vector3(s.Position.X, s.Position.Y, s.Position.Z + 2.3f) + fwd * 2.2f + right * side;
        place = (new Vector3(muzzle.X, muzzle.Z, -muzzle.Y), AxesFromY(new Vector3(fwd.X, fwd.Z, -fwd.Y)));   // game space (X, Y, Z up) to Godot's (X, Z, -Y)
        if (_dreadTracks.TryGetValue(shooter, out var tr) && place is { } pl)
        {
            // The data names the cannon's flash on a list of markers (fx_muzzle_autocannon_top, then fx_muzzle_assault_cannon: the first the model has is used); the loyalist model has the second only, and the estimate above sat 2 m ahead of the barrel.
            var real = DreadMarkerWorld(tr, marker) ?? (DreadnoughtFeel.MarkerFallback.TryGetValue(marker, out var alt) ? DreadMarkerWorld(tr, alt) : null);
            if (real is { } at) place = (at, pl.Item2);
            // The model's marker sits 1.4 m AHEAD of the barrel's tip (the film of 2026-11-15, measured: the tracer, which starts at the marker, began 1.5 m past the barrel), so every muzzle point is first brought back to the tip
            // (DreadnoughtFeel.MarkerToTip, **ours**); and the flash effect is authored with its bloom ring, beams and light 0.7 to 1.4 m ahead of its own origin (x1.75: a core 1.4 m ahead), so its origin goes back that much further and its
            // visible core sits at the tip (DreadnoughtFeel.FlashPullback). The tracer, casings and the melta keep their markers (the melta's fx_Muzzle is on the fist).
            if (place is { } tip && DreadnoughtFeel.TipShift(marker, DreadChaos) is > 0f and var back) place = (tip.Item1 - tip.Item2.Y * back, tip.Item2);
            if (DreadMuzzleLog) DreadMuzzleProbe(tr, s, marker, place!.Value.Item1);
        }   // the model's own muzzle marker when it has one (the estimate above is the fallback)
    }

    static readonly bool DreadMuzzleLog = CmdFlag("dn-muzzle-log");
    /// <summary>Test aid <c>--dn-muzzle-log</c>: where the shot's effect marker sits in the Dreadnought's frame (forward, right, up from his feet) and how far the drawn model reaches forward and right, so a flash that starts in the air shows.</summary>
    void DreadMuzzleProbe(DreadnoughtTrack tr, PlayerState s, string marker, Vector3 at)
    {
        if (tr.Viewer is not { } v || !IsInstanceValid(v)) return;
        var gs = _gameSpace; if (gs is null) return;
        var inv = (gs.GlobalTransform * new Transform3D(new Basis(new Vector3(0, 0, 1), s.Yaw), new Vector3(s.Position.X, s.Position.Y, s.Position.Z))).AffineInverse();
        Vector3 Rel(Vector3 w) => inv * w;
        float maxF = -99, maxR = -99, minR = 99; var stack = new Stack<Node>(); stack.Push(v);
        while (stack.Count > 0) { var n = stack.Pop(); foreach (var c in n.GetChildren()) stack.Push(c);
            if (n is MeshInstance3D mi && mi.Mesh is not null) { var a = mi.GetAabb(); for (int k = 0; k < 8; k++) { var cr = Rel(mi.GlobalTransform * (a.Position + new Vector3((k & 1) * a.Size.X, (k >> 1 & 1) * a.Size.Y, (k >> 2 & 1) * a.Size.Z))); maxF = MathF.Max(maxF, cr.Y); maxR = MathF.Max(maxR, cr.X); minR = MathF.Min(minR, cr.X); } } }
        var m = Rel(at);
        GD.Print($"DNMUZZLE {marker}: marker right {m.X:F2} forward {m.Y:F2} up {m.Z:F2}; model reaches forward {maxF:F2}, right {minR:F2}..{maxR:F2} (inverse frame: x right, y forward)");
    }

    /// <summary>A muzzle flash at the cannon for each frame the clip lost a round, and the footfall dust where the stride lands. Nothing for a lean bot.</summary>
    void DreadnoughtEffects(DreadnoughtTrack t, PlayerState s, bool entering)
    {
        if (_fx is null || Lean) { t.PrevClip = s.Clip; t.StepDust = false; return; }
        // No flash of his own any more (the buff, owner 2026-11-15): the shot path plays the autocannon's whole effect list (flash at its scale 1.5, tracer, casings) and asks ExtrasFxPlace for the barrel, so there is exactly one flash per shot.
        t.PrevClip = s.Clip;
        // The cannon's and the melta's own effects (flash, casings, tracer, impacts) are the wargear's bindings through the normal shot path, now that the catalogue reads their files (ExtrasEffectWargear); no per-shot copy here.
        t.PrevMeltaCd = s.Cooldown;
        t.PrevShotCd = s.Cooldown;
        if (t.StepDust)
        {
            t.StepDust = false;
        }
    }

    // ---- sounds (D7): the install's own events from bank Weapon_Dreadnought through the sound system (the data's mix); none for a lean bot ----
    /// <summary>The ground one footfall covers in the clips (**ours**, from the clips' cycles: walk 1.333 s for two steps at 3 m/s, run 0.8 s at 6.5, sprint 0.8 s at 11; the clips play faster with his 1.3x speeds, so the distance holds): the feel's footfalls (sound, dust, shake) follow the feet, not the sim's blast stride.</summary>
    const float DreadWalkStride = 2.0f, DreadRunStride = 2.6f, DreadSprintStride = 4.4f;
    const string DreadEngineEvent = DreadnoughtFeel.EngineEvent, DreadFootEvent = "play_dreadnought_footstep", DreadSprintFootEvent = "play_dreadnought_sprint_footstep", DreadReloadEvent = "play_dreadnought_sm_weapon_reload";
    static readonly string[] DreadLightSwing = ["play_dreadnought_melee_swing_x1", "play_dreadnought_melee_swing_x2"], DreadHeavySwing = ["play_sm_stomp_displace", "play_dreadnought_melee_swing_y1"];

    /// <summary>
    /// Posts a Dreadnought's sounds for this frame: his engine loop from the end of his entrance until his death, a footstep every <see cref="DreadnoughtRules.FootfallStride"/> metres walked (the sim's footfall stride: the
    /// sprint's footstep when sprinting), the arm's swing at each strike's start and the cannon's reload. The data's events (docs/discovery.md "Dreadnought (Dreadnought Assault)", section "Sounds"); the cannon's own fire
    /// sound is the wargear's, played by the shot path.
    /// </summary>
    void DreadnoughtSounds(DreadnoughtTrack t, PlayerState s, bool own, bool entering)
    {
        if (_sound is null || Lean) return;
        var em = t.Emitter;
        em.Own = own; if (t.Label.Length == 0) t.Label = $"dreadnought {s.PlayerId}"; em.Label = t.Label;
        em.Position = ToGodot(s.Position) + Vector3.Up * 1.5f;
        float speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
        em.Parameters[VoiceMixer.Speed] = speed / DreadnoughtRules.MoveSpeed;   // the engine's speed RTPC is anchored at the data's 6.5 m/s run; his run is 1.3 times that now
        if (!s.Alive) { if (t.EngineOn) { _sound.StopEmitter(em); t.EngineOn = false; } t.Last = Vector3.Zero; return; }
        if (!entering && !t.EngineOn) { _sound.Post(DreadEngineEvent, em); t.EngineOn = true; }
        var here = ToGodot(s.Position);
        if (t.Last != Vector3.Zero && s.Grounded && !entering)
        {
            float moved = new Vector2(here.X - t.Last.X, here.Z - t.Last.Z).Length();   // Godot space: X, Z are the ground
            if (moved < 3f) t.Stride += moved;
            float strideNeeded = speed > DreadnoughtRules.RunSpeed + 1.5f ? DreadSprintStride : speed > DreadnoughtRig.RunFrom ? DreadRunStride : DreadWalkStride;
            if (t.Stride >= strideNeeded) { t.Stride -= strideNeeded; var foot = FeelOf(s.PlayerId); foot.Left = !foot.Left; bool sprint = speed > DreadnoughtRules.RunSpeed + 1.5f;
                DreadAct(sprint ? (foot.Left ? DreadAction.SprintFootLeft : DreadAction.SprintFootRight) : (foot.Left ? DreadAction.FootLeft : DreadAction.FootRight), s, t, own); }
        }
        t.Last = here;
        // the arm's sounds are DreadnoughtArm.Cues' now (NetworkView.DreadnoughtFeel.cs: start, antic, swing and hit beats)
        t.Strike = s.MeleeStep;
        // the melta's pump follows each shot (wargear pump state; the 0.45 s lag is ours)
        if (s.Slot == WeaponSlot.Pistol && s.Cooldown > t.PrevCooldown + 0.05f)
        {
            var pumpEm = em; GetTree().CreateTimer(DreadnoughtFeel.MeltaPumpDelay).Timeout += () => { if (LogDreadFeel) GD.Print($"DNFEEL t {_connectedFor:F2} MeltaPump {DreadnoughtFeel.MeltaPumpEvent}"); _sound?.Post(DreadnoughtFeel.MeltaPumpEvent, pumpEm); };
        }
        t.PrevCooldown = s.Cooldown;
        if (s.ReloadLeft > 0f && t.ReloadLeft <= 0f) _sound.Post(DreadReloadEvent, em);
        t.ReloadLeft = s.ReloadLeft;
    }

    /// <summary>The walk, run, sprint or idle clip for the state (**ours**: eight sectors off the facing, walk below <see cref="DreadnoughtRig.RunFrom"/>, rate from the speed, the aim pitch across the down / level / up clips).</summary>
    void DreadnoughtLocomotion(CharacterViewer character, PlayerState s)
    {
        float vx = s.Velocity.X, vy = s.Velocity.Y;
        float speed = MathF.Sqrt(vx * vx + vy * vy);
        float fx = -MathF.Sin(s.Yaw), fy = MathF.Cos(s.Yaw);
        float forward = vx * fx + vy * fy, right = vx * fy - vy * fx;
        bool melta = s.Slot == WeaponSlot.Pistol;
        float pitchT = Math.Clamp(s.Pitch / 0.9f, -1f, 1f);   // radians; ~50 degrees is the full clip
        if (speed > 0.4f)
        {
            if (speed > DreadnoughtRules.RunSpeed + 1.5f && forward > 0f)
            {
                character.Stance("dn:sprint", null, DreadnoughtRig.Sprint, null, 0f, Math.Clamp(speed / DreadnoughtRules.DataSprintSpeed, 0.6f, 1.6f), 0.25f);
                return;
            }
            int dir = DreadnoughtRig.DirectionIndex(MathF.Atan2(right, forward) * 180f / MathF.PI);
            bool run = speed > DreadnoughtRig.RunFrom;
            character.Stance(DreadMoveKey(run, dir, melta), DreadnoughtRig.MoveClip(run, dir, melta, -1), DreadnoughtRig.MoveClip(run, dir, melta, 0), DreadnoughtRig.MoveClip(run, dir, melta, 1), pitchT,
                speed / (run ? DreadnoughtRig.RunCycleSpeed : DreadnoughtRig.WalkCycleSpeed), 0.2f);
            return;
        }
        character.Stance(melta ? "dn:idle:l" : "dn:idle:a", DreadnoughtRig.Stand(melta, -1), DreadnoughtRig.Stand(melta, 0), DreadnoughtRig.Stand(melta, 1), pitchT, 1f, 0.25f);
    }

    /// <summary>The stance keys, built once (the pose asks every frame).</summary>
    static readonly string[] DreadMoveKeys = BuildMoveKeys();
    static string[] BuildMoveKeys()
    {
        var keys = new string[2 * 8 * 2];
        for (int r = 0; r < 2; r++) for (int d = 0; d < 8; d++) for (int m = 0; m < 2; m++) keys[(r * 8 + d) * 2 + m] = $"dn:{(r == 1 ? "run" : "walk")}:{d}:{(m == 1 ? "l" : "a")}";
        return keys;
    }
    static string DreadMoveKey(bool run, int direction, bool melta) => DreadMoveKeys[((run ? 8 : 0) + direction) * 2 + (melta ? 1 : 0)];

    readonly DreadnoughtSprintGate _dnSprint = new();
    float _dnSprintFov;

    /// <summary>The sprint's 6 s and 1 s gap on the own input before it is sent and predicted (the server applies the same <see cref="DreadnoughtSprintGate"/>).</summary>
    partial void ExtrasInputGate(ref PlayerInput input)
    {
        if (_client is { } c && _config is not null && c.Predicted.Alive && IsDreadnoughtClass(c.Predicted.Class)) _dnSprint.Apply(ref input, 1f / Math.Max(1, _config.TickRate));
        else _dnSprint.Reset();
    }

    /// <summary>His camera scale (**estimate**: the data's camera offset curves `dn_*` were not found; the marine rig's offsets x the model's size ratio, 4.2 m against a marine's 1.9 m).</summary>
    const float DreadnoughtCameraScale = 2.2f;
    /// <summary>His fields of view (data: 42 exploring, 20 aimed, 55 sprinting, blended in 0.5 s) against the rifle set's 40 and 20 the rig starts from.</summary>
    const float DreadnoughtExploreFov = 42f, DreadnoughtSprintFov = 55f, RifleExploreFov = 40f;

    partial void ExtrasRigCamera(PlayerState me, float aimBlend, float delta, ref System.Numerics.Vector3 cam, ref System.Numerics.Vector3 pivot, ref float fov, ref float fovRef)
    {
        if (!IsDreadnoughtClass(me.Class) || !me.Alive) { _dnSprintFov = 0f; return; }
        cam *= DreadnoughtCameraScale;
        pivot *= DreadnoughtCameraScale;
        float speed = MathF.Sqrt(me.Velocity.X * me.Velocity.X + me.Velocity.Y * me.Velocity.Y);
        _dnSprintFov = Mathf.MoveToward(_dnSprintFov, speed > DreadnoughtRules.RunSpeed + 1.5f ? 1f : 0f, delta / 0.5f);
        float hip = Mathf.Lerp(DreadnoughtExploreFov, DreadnoughtSprintFov, _dnSprintFov) / RifleExploreFov;   // the rig's hip field of view is the rifle's 40
        float scale = Mathf.Lerp(hip, 1f, aimBlend);
        fov *= scale; fovRef *= scale;
    }

    /// <summary>camera/melee-fov-widen (owner 2026-11-15: "No"): the Dreadnought keeps his normal field of view in melee.</summary>
    partial void ExtrasMeleeFovExcluded(PlayerState me, ref bool excluded) => excluded = IsDreadnoughtClass(me.Class);

    /// <summary>A Dreadnought's death is his clip, held where it ends; no ragdoll (design section 3.2: "no ragdoll").</summary>
    partial void ExtrasDeath(CharacterViewer character, ref bool handled)
    {
        if (IsInstanceValid(character) && IsDreadnoughtCharacter(character)) handled = true;
    }
}
