using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Hip fire in the networked view (M27): shooting without the Aim button, on the move, with the animations and effects that
/// go with it. The simulation side: a sprint blocks the trigger (protocol 51; before it a shot ended the sprint), and (M31) the weapon is raised or lowered (<see cref="PlayerState.ReadyTicks"/>,
/// <see cref="PlayerState.RaiseTicks"/>: the first shot from a lowered weapon waits for its raise; <c>Combat.StepPlayer</c>,
/// spec/behavior.md "Hip fire"); accuracy is the same as aimed fire (shots converge on the crosshair in both modes).
/// <para>
/// In the original (behaviour graphs in <c>havok_sm_base_mp__hkxx</c>, read by <see cref="HipFireAnimations"/>) a shot keeps the
/// navigation graph running in its <b>ready</b> posture (<c>Run Ready</c>: a blend over the movement direction, the speed and
/// the aim pitch of the <c>*_Aim*</c> run / walk clips) and adds a whole-body additive recoil clip per shot. Here:
/// </para>
/// <list type="bullet">
/// <item>Ready posture: not aiming, alive, ranged weapon in hand, no melee / throw / switch, and the simulation's weapon up (M31: the
/// own player's predicted ready or raise ticks, others' "weapon up" bit; held <see cref="SimConfig.ReadyHold"/> after a shot, always
/// for the always-ready weapons). The family's clips are chosen by the direction of the velocity against the facing (nearest of
/// the graph's directions), the speed band (walk below 3.25 m/s, else run; on the spot: the standing aim pose) and blended by
/// the aim pitch through the stance machinery (<see cref="CharacterViewer.Stance"/>); the fades are the graphs' relaxed to ready and
/// ready to relaxed times. Which of the graph's discrete directions and bands show (instead of its continuous blends) and the
/// +40 degree diagonal clips left out are <b>interpretation</b>; the hold time is the simulation's (<b>ours</b>: the original sets it in code).</item>
/// <item>Recoil: each shot restarts the family's recoil clip as an additive whole-body pose layer
/// (<see cref="CharacterViewer.SetPoseLayer"/>); the grenade launcher's clip is not additive in the original and is laid over the
/// upper body at partial weight (<b>provisional approximation</b>). Also shown in aim mode (the same fire state).</item>
/// <item>Families: rifle (bolter, plasma gun, melta, grenade launcher, storm bolter, stalker, and lascannon and plasma cannon, whose
/// ready posture the rifle graph uses), pistol (bolt pistol, plasma pistol: <b>interpretation</b>), heavy (heavy bolter).</item>
/// <item>Triggers: the own player's predicted shot events, others' ray and cone shots, and projectiles that appear next to a player
/// holding that weapon; a charging player stays ready.</item>
/// </list>
/// The effects (muzzle flash, tracer) read the posed muzzle, so they follow the recoil and the ready posture with no change.
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--no-hip-fire</c>: the plain movement and aim poses only (no ready posture, no recoil).</summary>
    public bool NoHipFire { get; init; }
    /// <summary><c>--hip-probe</c> (test aid, with a bot): once the weapon is in hand, poses every ready clip of its family and logs where the barrel points against the facing.</summary>
    public bool HipProbe { get; init; }
    readonly HashSet<HipFireWeaponClass> _hipProbed = new();
    // The blend probe (--hip-probe): drives the own character through the ready stances at a range of aim pitches and logs the barrel error.
    List<(HipStanceDef Def, float PitchDegrees, string Label)>? _hipBlendSteps;
    int _hipBlendIndex, _hipBlendFrames;
    bool HipBlendActive => _hipBlendSteps is not null && _hipBlendIndex < _hipBlendSteps.Count;

    /// <summary>The walk band is shown below this horizontal speed (m/s), the run band above (the midpoint of the graph's walk 2 and run 4.5).</summary>
    const float HipWalkBelow = 3.25f;
    /// <summary>Hysteresis (ours) on the walk/run split (m/s each side) and on the direction sector borders (degrees), against stance flicker.</summary>
    const float HipBandHold = 0.25f, HipDirectionHold = 8f;
    /// <summary>Largest distance (m) between a new projectile and the muzzle area of the player it is attributed to.</summary>
    const float HipProjectileReach = 12f;   // fx/barrel-origin: 8 m before; the charged plasma pistol ball is first seen 11 m from its shooter (measured), the others 3 to 9 m
    /// <summary>The grenade launcher's recoil clip (not additive in the original) and the weight it is laid over the upper body at (ours).</summary>
    const string GrenadeRecoilClip = "SM_RNG_GL_FireIdle_01";
    const float GrenadeRecoilWeight = 0.5f;

    enum PoseKind : byte { Normal, Hip, Aim }

    sealed class HipTrack
    {
        public double ShotAt = double.NegativeInfinity;    // the last shot (or charge) seen
        public double RecoilAt = double.NegativeInfinity;  // the last shot, for the recoil clip
        public bool Moving;                                // hysteresis between the standing and the moving ready pose
        public double HipSince = double.NegativeInfinity;  // when the ready posture was entered (the raise blend runs from here)
        public PoseKind Kind;                              // the pose last placed (for the blend times)
        public int Shots;
        public bool Walk;                                  // hysteresis between the walk and run bands
        public int DirFamily = -1, Dir;                    // the direction last shown (hysteresis against flicker at the sector borders)
    }

    /// <summary>One resolved moving ready stance: <see cref="CharacterViewer.Stance"/> arguments.</summary>
    sealed record HipStanceDef(string Key, string? Low, string Mid, string? High, float LowDegrees, float HighDegrees, float BandSpeed);

    Task<HipFireAnimations>? _hipLoad;
    HipFireAnimations? _hipAnims;
    string _hipNote = "not loaded";
    readonly Dictionary<int, HipTrack> _hip = new();
    readonly Dictionary<(HipFireWeaponClass, int, bool), HipStanceDef?> _hipStances = new();
    readonly Dictionary<HipFireWeaponClass, (string Clip, float Length, bool Additive)?> _hipRecoils = new();
    (string Clip, float Length, bool Additive)? _hipGrenadeRecoil;
    bool _hipGrenadeResolved;
    IReadOnlyList<ProjectileView> _frameProjectiles = [];   // this frame's GameClient.Projectiles(), shared with UpdateProjectiles
    readonly HashSet<int> _hipProjectilesSeen = new();
    bool _hipProjectilesInit;
    IReadOnlyList<(uint Tick, SelfEvents Events)> _frameOwnEvents = [];   // this frame's own predicted events (GameClient.TakeOwnEvents), read by sound and hip fire
    int _hipOwnShots, _hipRemoteShots, _hipProjectileShots, _hipNoSet;
    readonly HashSet<string> _hipNoted = new();
    // Muzzle check (bot log): the barrel against the aim while the own player shoots, by pose.
    sealed class HipAngleStat { public int N; public double Sum, Yaw, Pitch, AimPitch; public float Max; }
    readonly Dictionary<string, HipAngleStat> _hipAngles = new();
    double _hipLogAt, _hipMeasureSkipUntil;
    float _hipMeasureYaw;
    int _hipAnglesSkipped;
    /// <summary>Measurement filters (test aid): a view turn above this in one frame is a snap, skipped for <see cref="HipSnapSkip"/> s; a switch is skipped while it runs and <see cref="HipSwitchSkip"/> s after.</summary>
    const float HipSnapRadians = 0.1f;
    const double HipSnapSkip = 0.25, HipSwitchSkip = 0.5;
    /// <summary>The first seconds of the ready posture are the relaxed to ready blend (the shot is not delayed for the raise, see the spec): measured apart.</summary>
    const double HipRaiseSeconds = 0.3;

    /// <summary>Starts reading the hip-fire animation data on a worker thread (the behaviour graphs: slow, never on the main thread).</summary>
    void SetUpHipFire()
    {
        if (NoHipFire || Lean) { _hipNote = Lean ? "not loaded (lean bot)" : "switched off (--no-hip-fire)"; return; }   // perf/lean-bots: poses only
        string gameDir = _gameDir;
        _hipNote = "loading";
        _hipLoad = Task.Run(() => HipFireAnimations.Load(gameDir));
    }

    HipFireAnimations? HipData()
    {
        if (_hipAnims is not null) return _hipAnims;
        if (_hipLoad is not { IsCompleted: true } load) return null;
        _hipLoad = null;
        if (load.IsFaulted || load.IsCanceled)
        {
            _hipNote = "not available: " + (load.Exception?.GetBaseException().Message ?? "cancelled");
            GD.PrintErr("hip fire: " + _hipNote);
            return null;
        }
        _hipAnims = load.Result;
        _hipNote = string.Join(", ", _hipAnims.Sets.Select(s => $"{s.Class} {s.Directions.Count} directions, recoil {s.Recoil?.Clip ?? "none"}"));
        GD.Print("hip fire: " + _hipNote + $"; hold {_config?.ReadyHold:F2} s (simulation; M32: the owner's rough estimate, unverified); ready blends rifle {_hipAnims.For(HipFireWeaponClass.Rifle)?.RelaxedToReadyBlend:F3} / {_hipAnims.For(HipFireWeaponClass.Rifle)?.ReadyToRelaxedBlend:F3} s");
        foreach (var set in _hipAnims.Sets)
            foreach (var n in set.Notes) GD.Print($"  hip fire note ({set.Class}): {n}");
        return _hipAnims;
    }

    HipTrack HipOf(int id)
    {
        if (!_hip.TryGetValue(id, out var t)) _hip[id] = t = new HipTrack();
        return t;
    }

    /// <summary>
    /// The hip-fire family of the weapon in hand (null without a ranged weapon; <see cref="HipFireAnimations.FamilyOf"/>, the mapping the
    /// simulation's raise delays use). Lascannon and plasma cannon use the rifle graph's ready posture; plasma pistol: pistol (interpretation).
    /// </summary>
    HipFireWeaponClass? HipFamilyOf(PlayerState s) =>
        _config is not null && Loadouts.InHand(_config, s)?.Name is { } name ? HipFireAnimations.FamilyOf(name) : null;

    // ------------------------------------------------------------------ shots seen

    /// <summary>
    /// Once per frame before the characters are placed: takes the own predicted events and the others' shots out of the client
    /// (the one drain of both; the sound and effect code read <see cref="_frameOwnEvents"/> and <see cref="_frameShots"/>) and notes
    /// who fired.
    /// </summary>
    void HipFireBegin(PlayerState me)
    {
        _frameShots = _client!.TakeShots();
        _frameOwnEvents = _client.TakeOwnEvents();
        double now = _connectedFor;
        var own = HipOf(_client.PlayerId);
        if (!me.Alive || me.SwitchLeft > 0f || me.MeleeStep > 0 || me.Reaction != ReactionKind.None) { own.ShotAt = double.NegativeInfinity; own.RecoilAt = double.NegativeInfinity; }
        else
        {
            foreach (var (_, ev) in _frameOwnEvents)
                if (ev.Fired)
                {
                    own.ShotAt = own.RecoilAt = now; own.Shots++; _hipOwnShots++;
                    if (ev.ShotStage > 0) _ownChargedShots++; else _ownBasicShots++;
                }
            if (me.Charging) own.ShotAt = now;
        }
        foreach (var shot in _frameShots)
        {
            var t = HipOf(shot.Shooter);
            t.ShotAt = t.RecoilAt = now; t.Shots++; _hipRemoteShots++;
        }
    }

    /// <summary>After the others are known: a projectile that appears next to a player holding that weapon is that player's shot; a charging player stays ready.</summary>
    void HipFireOthers(PlayerState me, IReadOnlyList<PlayerState> others, bool snapshotSeen)
    {
        if (_client is null || _config is null) return;
        _frameProjectiles = _client.Projectiles();   // once per frame; UpdateProjectiles reads it too
        double now = _connectedFor;
        foreach (var o in others)
        {
            var t = HipOf(o.PlayerId);
            if (!o.Alive || o.Reaction != ReactionKind.None) { t.ShotAt = double.NegativeInfinity; t.RecoilAt = double.NegativeInfinity; }   // a reaction (M28) ends the ready posture and the recoil
            else if (o.Charging) t.ShotAt = now;
        }
        foreach (var q in _frameProjectiles)
        {
            if (q.Thrown || !_hipProjectilesSeen.Add(q.Id)) continue;
            if (!_hipProjectilesInit) continue;   // already out when this view began: no shooter to find
            PlayerState? best = null;
            float bestDistance = HipProjectileReach * HipProjectileReach;
            var at = new System.Numerics.Vector3(q.Position.X, q.Position.Y, q.Position.Z);
            foreach (var o in others.Append(me))
            {
                if (!o.Alive || Loadouts.WeaponIndex(_config, o, o.Slot) != q.Weapon) continue;
                float d = System.Numerics.Vector3.DistanceSquared(o.Position + new System.Numerics.Vector3(0, 0, 1.4f), at);
                if (d < bestDistance) { bestDistance = d; best = o; }
            }
            if (best is not { } shooter || shooter.PlayerId == me.PlayerId) continue;   // the own shots come from the prediction
            var track = HipOf(shooter.PlayerId);
            track.ShotAt = track.RecoilAt = now; track.Shots++; _hipProjectileShots++;
            FxProjectileLaunch(shooter, q);   // fx/barrel-origin: the muzzle effects of another player's projectile shot (no shot record is sent for it)
        }
        // Projectiles already out when the first snapshot arrived have no shooter to find; later ones do.
        if (snapshotSeen) _hipProjectilesInit = true;
        if (_hipProjectilesSeen.Count > 4096)
        {
            // Ids wrap at 65536: forget only those no longer live (a live one would be taken for a new shot).
            var live = new HashSet<int>(_frameProjectiles.Select(p => p.Id));
            _hipProjectilesSeen.IntersectWith(live);
        }
    }

    void HipFireForget(int id) => _hip.Remove(id);

    // ------------------------------------------------------------------ poses

    /// <summary>
    /// Whether the ready posture shows for this player now (see the class comment): the simulation's weapon up (M31: ready or raising;
    /// a charge or a pending charged shot too), so the pose and the shot line up.
    /// </summary>
    static bool HipReady(PlayerState s) =>
        s.Alive && !s.Aiming && s.Reaction == ReactionKind.None && s.MeleeStep == 0 && !s.Throwing && s.SwitchLeft <= 0f
        && (s.ReadyTicks > 0 || s.RaiseTicks > 0 || s.Charging || s.PendingShot > 0);

    /// <summary>
    /// The moving ready stance of a family for a movement direction (degrees from the facing, 0 forward, +90 right) and speed band:
    /// the nearest of the graph's directions, its band's clips as a down / level / up triple, resolved once and checked against the
    /// rig; null when a clip is missing (the pose then falls back to the normal one, noted once).
    /// </summary>
    HipStanceDef? HipStanceFor(HipFireSet set, float angleDegrees, bool walk, HipTrack t)
    {
        int best = 0;
        float bestDiff = float.MaxValue;
        for (int i = 0; i < set.Directions.Count; i++)
        {
            float diff = MathF.Abs(((set.Directions[i].AngleDegrees - angleDegrees) % 360f + 540f) % 360f - 180f);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        // The direction last shown is kept until another is closer by more than HipDirectionHold degrees.
        if (t.DirFamily == (int)set.Class && t.Dir < set.Directions.Count && t.Dir != best)
        {
            float previous = MathF.Abs(((set.Directions[t.Dir].AngleDegrees - angleDegrees) % 360f + 540f) % 360f - 180f);
            if (previous <= bestDiff + HipDirectionHold) best = t.Dir;
        }
        t.DirFamily = (int)set.Class; t.Dir = best;
        var key = (set.Class, best, walk);
        if (_hipStances.TryGetValue(key, out var cached)) return cached;
        HipStanceDef? def = null;
        var direction = set.Directions[best];
        var band = walk ? direction.Walk ?? direction.Run : direction.Run;
        if (band is { Clips.Count: > 0 })
        {
            var clips = band.Clips.OrderBy(c => c.PitchDegrees).ToList();
            var low = clips[0];
            var high = clips[^1];
            var mid = clips.MinBy(c => MathF.Abs(c.PitchDegrees))!;
            bool Ok(string clip) => _assets!.Model.Rig.AnimationNames.Contains(clip, StringComparer.OrdinalIgnoreCase) && _assets.Animation(clip) is not null;
            if (!Ok(mid.Clip)) _hipNoted.Add($"hip clip {mid.Clip} not in the rig");
            else
            {
                string? lowClip = low.PitchDegrees < -1f && Ok(low.Clip) ? low.Clip : null;
                string? highClip = high.PitchDegrees > 1f && Ok(high.Clip) ? high.Clip : null;
                def = new HipStanceDef($"hip:{set.Class}:{direction.Label}:{(walk && direction.Walk is not null ? "walk" : "run")}", lowClip, mid.Clip, highClip,
                    lowClip is null ? -80f : low.PitchDegrees, highClip is null ? 80f : high.PitchDegrees, band.Speed);
            }
        }
        if (def is null && _hipNoted.Add($"stance:{key}")) _notes.Add($"hip fire: no usable clips for {set.Class} {direction.Label} {(walk ? "walk" : "run")}; the normal pose shows there");
        _hipStances[key] = def;
        return def;
    }

    /// <summary>The ready posture: true when it was placed (false: the caller shows the normal pose).</summary>
    bool HipStancePlace(CharacterViewer character, PlayerState s, HipTrack t)
    {
        if (_hipAnims is null || HipFamilyOf(s) is not { } family || _hipAnims.For(family) is not { } set) { if (_hipAnims is not null) _hipNoSet++; return false; }
        float speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
        t.Moving = t.Moving ? speed > AimStandBelow : speed > AimWalkAbove;
        // Entering the ready posture from the relaxed one takes the graph's blend; hand-over from aim or other ready poses the default.
        // Also after a clip pose (jump, roll, melee, throw, reaction dropped the stances): the weapon is brought up again from that pose.
        bool entering = t.Kind != PoseKind.Hip || character.StanceKey is null;
        float fade = _fadeOverride > 0f ? _fadeOverride : entering && t.Kind != PoseKind.Aim ? set.RelaxedToReadyBlend ?? set.NavToFireBlend ?? CharacterViewer.StanceFadeSeconds : CharacterViewer.StanceFadeSeconds;
        float pitchDegrees = s.Pitch * 180f / MathF.PI;
        // M40-T4: a marine in the ready run's blend stays in it until its damped speed is below GaitLeaveSpeed (the blend carries the standing aim pose as a share),
        // so a reversal through zero speed does not swap the blend for the standing stance and back.
        var gaitTrack = LocoOf(s.PlayerId);
        if (!t.Moving && gaitTrack.HipGaitMoving && GaitHipPlace(character, s, set, fade))
        {
            if (entering) t.HipSince = _connectedFor;
            t.Kind = PoseKind.Hip;
            return true;
        }
        if (!t.Moving)
        {
            gaitTrack.HipGaitMoving = false;
            StandAfterGait(s, gaitTrack);   // qc/reference-pass: the twist idle from this frame on (no one-frame plain aim pose)
            if (_standFade > 0f && _fadeOverride <= 0f) fade = _standFade;
            _standFade = 0f;
            // M40-T5 (NetworkView.TurnInPlace.cs): the graph's standing state: the aim-offset idle, planted, with the turn clips; else the idle aim pose below.
            if (TurnStandPlace(character, s, t, ready: true, fade)) { if (entering) t.HipSince = _connectedFor; return true; }
            // Standing still: the idle aim pose of the family (feet planted; the graph's on-the-spot clips step in place), see NetworkView.Aim.cs.
            string standKey = AimKeys[(int)family][0];
            if (!_aimStances.TryGetValue(standKey, out var triple)) return false;
            if (!character.Stance(standKey, triple.Down, triple.Level, triple.Up, Math.Clamp(s.Pitch / AimPitchRange, -1f, 1f), 1f, fade)) return false;
            if (entering) t.HipSince = _connectedFor;
            t.Kind = PoseKind.Hip;
            return true;
        }
        float fx = -MathF.Sin(s.Yaw), fy = MathF.Cos(s.Yaw);
        float forward = s.Velocity.X * fx + s.Velocity.Y * fy, right = s.Velocity.X * fy - s.Velocity.Y * fx;
        float angle = MathF.Atan2(right, forward) * 180f / MathF.PI;
        bool walk = speed < HipWalkBelow + (t.Walk ? HipBandHold : -HipBandHold);
        t.Walk = walk;
        // M40-T4 (NetworkView.GaitBlend.cs): the blend of the neighbouring directions and speed bands; the nearest clip below is the fallback.
        if (GaitHipPlace(character, s, set, fade))
        {
            gaitTrack.HipGaitMoving = true;
            if (entering) t.HipSince = _connectedFor;
            t.Kind = PoseKind.Hip;
            return true;
        }
        if (HipStanceFor(set, angle, walk, t) is not { } def) return false;
        float pitchT = pitchDegrees >= 0f ? (def.High is null ? 0f : pitchDegrees / def.HighDegrees) : (def.Low is null ? 0f : pitchDegrees / def.LowDegrees * -1f);
        float rate = def.BandSpeed > 0.1f ? Math.Clamp(speed / def.BandSpeed, walk ? 0.6f : 0.8f, walk ? 1.5f : 1.25f) : 1f;
        if (!character.Stance(def.Key, def.Low, def.Mid, def.High, pitchT, rate, fade)) return false;
        if (entering) t.HipSince = _connectedFor;
        t.Kind = PoseKind.Hip;
        return true;
    }

    /// <summary>The blend time to use when the pose goes back from the ready posture to the relaxed one.</summary>
    float HipRelaxFade(PlayerState s, HipTrack t)
    {
        if (_fadeOverride > 0f) return _fadeOverride;   // an action clip handing the legs over (NetworkView.Handoff.cs)
        float fade = CharacterViewer.StanceFadeSeconds;
        if (t.Kind == PoseKind.Hip && _hipAnims is not null && HipFamilyOf(s) is { } f && _hipAnims.For(f) is { } set) fade = set.ReadyToRelaxedBlend ?? set.FireToNavBlend ?? fade;
        return fade;
    }

    /// <summary>The recoil clip of a family (the rig's clip and its length), null when the data or the clip is missing.</summary>
    (string Clip, float Length, bool Additive)? HipRecoilOf(PlayerState s, HipFireWeaponClass family)
    {
        if (_config is not null && Loadouts.InHand(_config, s)?.Name.Contains("grenade_launcher", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (!_hipGrenadeResolved)
            {
                _hipGrenadeResolved = true;
                if (_assets!.Animation(GrenadeRecoilClip) is { } gl) _hipGrenadeRecoil = (GrenadeRecoilClip, gl.Animation.Duration, false);
            }
            if (_hipGrenadeRecoil is not null) return _hipGrenadeRecoil;
        }
        if (_hipRecoils.TryGetValue(family, out var cached)) return cached;
        (string, float, bool)? result = null;
        if (_hipAnims?.For(family)?.Recoil is { } r && _assets!.Animation(r.Clip) is { } clip) result = (r.Clip, clip.Animation.Duration, r.Additive);
        else if (_hipAnims is not null && _hipNoted.Add("recoil:" + family)) _notes.Add($"hip fire: no recoil clip for {family}; shots show no recoil");
        _hipRecoils[family] = result;
        return result;
    }

    /// <summary>Lays the recoil of the last shot over the pose (or removes the layer when none runs).</summary>
    void HipRecoil(CharacterViewer character, PlayerState s, HipTrack t)
    {
        if (_hipAnims is null || !s.Alive || HipFamilyOf(s) is not { } family || HipRecoilOf(s, family) is not { } recoil) { character.ClearPoseLayer(); return; }
        float age = (float)(_connectedFor - t.RecoilAt);
        if (age < 0f || age >= recoil.Length) { character.ClearPoseLayer(); return; }
        if (recoil.Additive) character.SetPoseLayer(recoil.Clip, age, 1f, additive: true);
        else
        {
            // A whole pose, not a difference: laid over the arms and spine only, easing in and out, so the legs keep moving.
            float envelope = MathF.Min(1f, MathF.Min(age / 0.1f, (recoil.Length - age) / 0.15f));
            character.SetPoseLayer(recoil.Clip, age, GrenadeRecoilWeight * envelope, additive: false, _hipAnims.For(HipFireWeaponClass.Rifle)?.UpperBodyMask);
        }
    }


    // ------------------------------------------------------------------ check and info

    /// <summary>
    /// The barrel against the aim while the own player shoots: counted by pose (hip fire moving / standing, aim mode) over the
    /// frames in which a shot was seen in the last 0.25 s (so the recoil is in play). Called after the frame is placed; logged by
    /// <see cref="HipFireLog"/>.
    /// </summary>
    void HipFireMeasure(PlayerState me)
    {
        if (!_bot || _client is null || _gameSpace is null || !me.Alive) return;   // a test aid: nothing is computed otherwise
        // Frames that say nothing about the pose: a switch under way or just done (the weapon travels between the hand and its
        // stow marker) and a snap of the view (the test bot turns to its target in one frame; the body follows within a few frames).
        float yawStep = MathF.Abs(MathF.IEEERemainder(me.Yaw - _hipMeasureYaw, 2f * MathF.PI));
        _hipMeasureYaw = me.Yaw;
        if (yawStep > HipSnapRadians) _hipMeasureSkipUntil = _connectedFor + HipSnapSkip;
        if (me.SwitchLeft > 0f) _hipMeasureSkipUntil = Math.Max(_hipMeasureSkipUntil, _connectedFor + HipSwitchSkip);
        var t = HipOf(_client.PlayerId);
        // Aim mode: the frames within 0.25 s of a shot. Hip fire: every frame of the ready posture (the pose error without the recoil is
        // the stance's own), split into the frames the recoil clip is playing ("+recoil": the original's kick) and the rest.
        bool recent = _connectedFor - t.RecoilAt <= 0.25 || _connectedFor - t.ShotAt <= 0.25;
        if (!(recent || (!me.Aiming && HipReady(me)))) return;
        if (_connectedFor < _hipMeasureSkipUntil) { _hipAnglesSkipped++; return; }
        if (_local?.StanceKey is null || MuzzleWorld(_client.PlayerId) is not { } m) return;   // a clip (reload, switch, melee) drives the pose: not a ready or aim pose
        float speed = MathF.Sqrt(me.Velocity.X * me.Velocity.X + me.Velocity.Y * me.Velocity.Y);
        float recoilLength = HipFamilyOf(me) is { } family && HipRecoilOf(me, family) is { } recoil ? recoil.Length : 0.5f;
        string withRecoil = _connectedFor - t.RecoilAt < recoilLength ? " +recoil" : "";
        string kind = me.Aiming ? (speed > 0.5f ? "aim moving" : "aim standing") + withRecoil
            : (me.RaiseTicks > 0 || _connectedFor - t.HipSince < HipRaiseSeconds ? "hip raising (the raise, or the first 0.3 s)" : (speed > 0.5f ? (_local.StanceKey.StartsWith("gait:hip:", StringComparison.Ordinal) ? "hip blend " + _local.StanceKey["gait:hip:".Length..] : "hip " + _local.StanceKey.Replace("hip:", "")) : "hip standing") + withRecoil);
        var aim = Combat.AimDirection(me.Yaw, me.Pitch);
        var aimWorld = (_gameSpace.Basis * new Vector3(aim.X, aim.Y, aim.Z)).Normalized();
        float angle = Mathf.RadToDeg(m.Direction.AngleTo(aimWorld));
        if (!_hipAngles.TryGetValue(kind, out var stat)) _hipAngles[kind] = stat = new HipAngleStat();
        // Where the barrel is off, in the body's frame (the facing is the aim's heading): yaw error positive to the right, pitch error positive up.
        var local = _local.GlobalTransform.Basis.Inverse() * m.Direction;
        stat.N++; stat.Sum += angle; stat.Max = MathF.Max(stat.Max, angle);
        stat.Yaw += Mathf.RadToDeg(MathF.Atan2(local.X, local.Y));
        stat.Pitch += Mathf.RadToDeg(MathF.Asin(Math.Clamp(local.Z / MathF.Max(local.Length(), 1e-6f), -1f, 1f))) - Mathf.RadToDeg(me.Pitch);
        stat.AimPitch += MathF.Abs(Mathf.RadToDeg(me.Pitch));
    }

    /// <summary>
    /// Test aid (<c>--hip-probe</c>): for every ready clip of the weapon family in hand (all directions, the walk and run bands, the
    /// down / level / up clips), poses the character in it at four times across the clip and logs the mean direction of the barrel
    /// relative to the body's facing: yaw (degrees, positive to the right) and pitch (positive up). A clip that aims straight ahead
    /// reads 0 / 0. Runs once per weapon family.
    /// </summary>
    void HipClipProbe(PlayerState me)
    {
        if (HipBlendActive) { HipBlendStep(); return; }
        if (!HipProbe || !_bot || _local is null || _hipAnims is null || !me.Alive || me.SwitchLeft > 0f || _connectedFor < 8.0) return;
        if (HipFamilyOf(me) is not { } family || _hipAnims.For(family) is not { } set || MuzzleWorld(_client!.PlayerId) is null) return;
        if (!_hipProbed.Add(family)) return;
        BuildBlendSteps(set);
        var weapon = Loadouts.InHand(_config!, me)?.Name;
        GD.Print($"hip probe: {weapon} ({family}); barrel yaw/pitch against the facing, degrees, mean over 4 times of each clip");
        foreach (var direction in set.Directions)
            foreach (var band in direction.Bands)
                foreach (var clip in band.Clips)
                {
                    if (_assets!.Animation(clip.Clip) is not { } anim) continue;
                    float sumYaw = 0f, sumPitch = 0f, sumOff = 0f, maxOff = 0f, minYaw = 999f, maxYaw = -999f;
                    int n = 0;
                    for (int k = 0; k < 16; k++)
                    {
                        _local.Pose(clip.Clip, anim.Animation.Duration * k / 16f, 0f);   // no cross-fade: the probe reads the clip itself
                        if (MuzzleWorld(_client.PlayerId) is not { } m) continue;
                        var local = _local.GlobalTransform.Basis.Inverse() * m.Direction;   // game space of the character: +Y forward, +Z up
                        float clipPitch = Mathf.DegToRad(clip.PitchDegrees);
                        float off = Mathf.RadToDeg(local.Normalized().AngleTo(new Vector3(0f, MathF.Cos(clipPitch), MathF.Sin(clipPitch))));   // against the aim the clip is meant for
                        sumOff += off; maxOff = MathF.Max(maxOff, off);
                        float yawNow = Mathf.RadToDeg(MathF.Atan2(local.X, local.Y));
                        minYaw = MathF.Min(minYaw, yawNow); maxYaw = MathF.Max(maxYaw, yawNow);
                        sumYaw += yawNow;
                        sumPitch += Mathf.RadToDeg(MathF.Asin(Math.Clamp(local.Z / MathF.Max(local.Length(), 1e-6f), -1f, 1f)));
                        n++;
                    }
                    if (n > 0) GD.Print($"  hip probe {direction.Label,-3} speed {band.Speed,4:F1} clip pitch {clip.PitchDegrees,5:F0} {clip.Clip,-40} yaw {sumYaw / n,6:F1} pitch {sumPitch / n,6:F1} off the clip's own aim {sumOff / n,5:F1} (max {maxOff,5:F1}; yaw range {minYaw:F0}..{maxYaw:F0})");
                }
    }

    void BuildBlendSteps(HipFireSet set)
    {
        var steps = new List<(HipStanceDef, float, string)>();
        foreach (var direction in set.Directions)
            foreach (bool walk in new[] { true, false })
                if (HipStanceFor(set, direction.AngleDegrees, walk, new HipTrack()) is { } def)
                    foreach (float pitch in new[] { -60f, -45f, -30f, -15f, 0f, 15f, 30f, 45f, 60f })
                        steps.Add((def, pitch, $"{direction.Label} {(walk ? "walk" : "run")}"));
        _hipBlendSteps = steps; _hipBlendIndex = 0; _hipBlendFrames = 0;
        GD.Print($"hip blend probe: {steps.Count} steps ({set.Class}); barrel against the aim at that pitch, degrees: off (angle), yaw error (+ right), pitch error (+ up)");
    }

    /// <summary>One frame of the blend probe: holds the stance at the step's pitch, reads the barrel after the pose has followed, then moves on.</summary>
    void HipBlendStep()
    {
        var (def, pitch, label) = _hipBlendSteps![_hipBlendIndex];
        bool first = _hipBlendIndex == 0 || _hipBlendSteps[_hipBlendIndex - 1].Def != def;
        float pitchT = pitch >= 0f ? (def.High is null ? 0f : pitch / def.HighDegrees) : (def.Low is null ? 0f : pitch / def.LowDegrees * -1f);
        _local!.Stance(def.Key, def.Low, def.Mid, def.High, pitchT, 1f, 0.05f);
        _hipBlendFrames++;
        if (_hipBlendFrames < (first ? 40 : 4)) return;
        _hipBlendFrames = 0;
        if (MuzzleWorld(_client!.PlayerId) is { } m)
        {
            var local = _local.GlobalTransform.Basis.Inverse() * m.Direction;
            float p = Mathf.DegToRad(pitch);
            float off = Mathf.RadToDeg(local.Normalized().AngleTo(new Vector3(0f, MathF.Cos(p), MathF.Sin(p))));
            float yaw = Mathf.RadToDeg(MathF.Atan2(local.X, local.Y));
            float pe = Mathf.RadToDeg(MathF.Asin(Math.Clamp(local.Z / MathF.Max(local.Length(), 1e-6f), -1f, 1f))) - pitch;
            GD.Print($"  hip blend {label,-8} aim pitch {pitch,4:F0} off {off,5:F1} yaw {yaw,6:+0.0;-0.0} pitch {pe,6:+0.0;-0.0}");
        }
        _hipBlendIndex++;
    }

    /// <summary>Bot log line: shots seen, the muzzle angles by pose.</summary>
    void HipFireLog()
    {
        if (!_bot || _connectedFor - _hipLogAt < 10.0) return;
        _hipLogAt = _connectedFor;
        GD.Print($"t {_connectedFor:F0} s: hip fire: {_hipNote}; shots seen own {_hipOwnShots} remote rays {_hipRemoteShots} projectiles {_hipProjectileShots}; hold {_config?.ReadyHold:F2} s (simulation); " +
                 $"barrel off the aim in deg (mean/max (frames): aim mode within 0.25 s of a shot, hip fire over the ready posture; {_hipAnglesSkipped} frames skipped for a switch or a view snap): " +
                 (_hipAngles.Count == 0 ? "none yet" : string.Join(", ", _hipAngles.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value.Sum / kv.Value.N:F1}/{kv.Value.Max:F1} ({kv.Value.N}; yaw {kv.Value.Yaw / kv.Value.N:+0.0;-0.0}, pitch {kv.Value.Pitch / kv.Value.N:+0.0;-0.0}, |aim pitch| {kv.Value.AimPitch / kv.Value.N:F0})"))));
    }

    /// <summary>The F1 line about hip fire.</summary>
    string HipInfoLine()
    {
        var own = _client is not null ? HipOf(_client.PlayerId) : null;
        return $"Hip fire (M27, M31): ready posture {(_hipAnims is null ? _hipNote : "from the behaviour graphs")} while the simulation holds the weapon up ({_config?.ReadyHold:F2} s after a shot, the owner's rough estimate, unverified; " +
               "the first shot from a lowered weapon waits for its raise: rifle 0.2, pistol 0.167 s, from the graphs; heavy bolter, grenade launcher, melta, lascannon, plasma cannon always up), " +
               "movement clips blended between the neighbouring directions and speed bands by the damped velocity (M40-T4; the nearest clip when the gait data is missing or with --no-gait-blend), and by pitch; recoil = the original additive clip per shot (grenade launcher: partial overlay, approximation); " +
               "shots as accurate as aimed fire; a sprint lowers the weapon and blocks the trigger, so does the Tactical roll (protocol 51). " +
               (own is null ? "" : $"Now: {(HipReady(_client!.Predicted) ? (_client.Predicted.RaiseTicks > 0 ? "raising" : "ready") : "relaxed")}, {own.Shots} own shots, last {(_connectedFor - own.ShotAt < 1e6 ? $"{_connectedFor - own.ShotAt:F1} s ago" : "never")}.");
    }
}
