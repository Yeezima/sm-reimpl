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
    DreadnoughtLooks? _dreadLooks;
    readonly Dictionary<int, DreadnoughtTrack> _dreadTracks = new();

    sealed class DreadnoughtTrack
    {
        public bool WasAlive, EngineOn, Seen, ClassDread, HasPos, Corpse, StepDust, Placed;
        public string Label = "";
        public Vector3 PrevPos;
        public int PrevClip;
        public double Born = double.NegativeInfinity, Died = double.NegativeInfinity;
        public SoundEmitter Emitter = new();
        public Vector3 Last;
        public float Stride;
        public int Strike;
        public float ReloadLeft;
    }

    /// <summary>The Dreadnought's class index in this match's config, or -1 (Classic and every config without him), found once: every per-frame hook returns at once when it is -1.</summary>
    int _dreadIndex = -2;
    int DreadIndex => _dreadIndex != -2 ? _dreadIndex : _config is null ? -1 : _dreadIndex = DreadnoughtClassIndex();
    bool IsDreadnoughtClass(int playerClass) => DreadIndex >= 0 && playerClass == DreadIndex;

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
        _dreadLooks ??= new DreadnoughtLooks(_gameDir);
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
                if (state.MeleeStep <= list.Length && character.Pose(list[state.MeleeStep - 1], MathF.Max(0f, state.MeleeTime), 0.12f)) return;
            }
        }
        DreadnoughtLocomotion(character, state);
    }

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
        if (t.WasAlive) { t.Died = _connectedFor; t.WasAlive = false; t.EngineOn = false; _sound?.StopEmitter(t.Emitter); }
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

    // ---- effects (D7): the install's effects through the effect player ----
    const string DreadMuzzleEffect = "autocannon_muzzle", DreadDustEffect = "jump_land";
    /// <summary>The muzzle flash's scale (data: the cannon's effect binding 1.75) and the footfall dust's (ours, the size of his foot).</summary>
    const float DreadMuzzleScale = 1.75f, DreadDustScale = 2.5f;

    /// <summary>A muzzle flash at the cannon for each frame the clip lost a round, and the footfall dust where the stride lands. Nothing for a lean bot.</summary>
    void DreadnoughtEffects(DreadnoughtTrack t, PlayerState s, bool entering)
    {
        if (_fx is null || Lean) { t.PrevClip = s.Clip; t.StepDust = false; return; }
        bool cannon = s.Slot == WeaponSlot.Ranged;
        if (!entering && cannon && s.Clip < t.PrevClip && s.ReloadLeft <= 0f)
        {
            float cp = MathF.Cos(s.Pitch);
            var fwd = new Vector3(-MathF.Sin(s.Yaw) * cp, MathF.Cos(s.Yaw) * cp, MathF.Sin(s.Pitch));
            var right = new Vector3(MathF.Cos(s.Yaw), MathF.Sin(s.Yaw), 0f);
            var muzzle = new Vector3(s.Position.X, s.Position.Y, s.Position.Z + 2.3f) + fwd * 2.2f + right * 0.8f;   // ours: the cannon's barrels, ahead of his right shoulder
            _fx.Play(DreadMuzzleEffect, new Vector3(muzzle.X, muzzle.Z, -muzzle.Y), AxesFromY(new Vector3(fwd.X, fwd.Z, -fwd.Y)), DreadMuzzleScale);   // game space (X, Y, Z up) to Godot's (X, Z, -Y)
        }
        t.PrevClip = s.Clip;
        if (t.StepDust)
        {
            t.StepDust = false;
            _fx.Play(DreadDustEffect, ToGodot(s.Position), Basis.Identity, DreadDustScale);
        }
    }

    // ---- sounds (D7): the install's own events from bank Weapon_Dreadnought through the sound system (the data's mix); none for a lean bot ----
    const string DreadEngineEvent = "play_engine_sm_dreadnought", DreadFootEvent = "play_dreadnought_footstep", DreadSprintFootEvent = "play_dreadnought_sprint_footstep", DreadReloadEvent = "play_dreadnought_sm_weapon_reload";
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
        em.Parameters[VoiceMixer.Speed] = speed;
        if (!s.Alive) { if (t.EngineOn) { _sound.StopEmitter(em); t.EngineOn = false; } t.Last = Vector3.Zero; return; }
        if (!entering && !t.EngineOn) { _sound.Post(DreadEngineEvent, em); t.EngineOn = true; }
        var here = ToGodot(s.Position);
        if (t.Last != Vector3.Zero && s.Grounded && !entering)
        {
            float moved = new Vector2(here.X - t.Last.X, here.Z - t.Last.Z).Length();   // Godot space: X, Z are the ground
            if (moved < 3f) t.Stride += moved;
            if (t.Stride >= DreadnoughtRules.FootfallStride) { t.Stride -= DreadnoughtRules.FootfallStride; t.StepDust = true; _sound.Post(speed > DreadnoughtRules.RunSpeed + 1.5f ? DreadSprintFootEvent : DreadFootEvent, em, footstep: true); }
        }
        t.Last = here;
        if (s.MeleeStep > 0 && t.Strike == 0)
        {
            var list = s.MeleeHeavy ? DreadHeavySwing : DreadLightSwing;
            if (s.MeleeStep <= list.Length) _sound.Post(list[s.MeleeStep - 1], em);
        }
        t.Strike = s.MeleeStep;
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
                character.Stance("dn:sprint", null, DreadnoughtRig.Sprint, null, 0f, Math.Clamp(speed / DreadnoughtRules.SprintSpeed, 0.6f, 1.4f), 0.25f);
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

    /// <summary>A Dreadnought's death is his clip, held where it ends; no ragdoll (design section 3.2: "no ragdoll").</summary>
    partial void ExtrasDeath(CharacterViewer character, ref bool handled)
    {
        if (IsInstanceValid(character) && IsDreadnoughtCharacter(character)) handled = true;
    }
}
