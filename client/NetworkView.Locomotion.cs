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
/// Movement poses of a marine whose weapon is lowered, in the networked view (M36; the owner's playtest 2026-10-28: "sprint has its own
/// animation, separate from the normal run", "there is a backpedal animation").
/// <para>
/// Until M36 every moving marine outside aim mode and the ready posture showed the one forward run clip (<c>SM_NAV_Run_F_01</c>), so a
/// sprint, a backpedal and a strafe all looked like running forward. Now (read from the behaviour graphs, <see cref="LocomotionAnimations"/>;
/// <c>smtool graph-dump --locomotion</c>):
/// </para>
/// <list type="bullet">
/// <item><b>Sprint</b>: the sprint graphs' loop, a blend over the movement angle of three looping clips: strafe left, forward and strafe right at
/// -45 / 0 / +45 degrees (<c>FLT_Nav_Motion_Angle_SPRINT_DAMP</c>), so a sprint is forward only. Rifles, pistols and the other weapons play
/// <c>SM_MP_1HR_NAV_Sprint_*</c> (<c>MP_SM_Bolter_NuSprint</c>), the heavy bolter <c>SM_NAV_TB_Sprint_*</c> (<c>SM_Heavy_Bolter_NuSprint</c>),
/// the lascannon and plasma cannon <c>SM_NAV_LC_Sprint_*</c> (<c>SM_Lascannon_Plasma_NuSprint</c>). It is entered over the base graph's
/// navigation-to-sprint blend (0.1 s). Which players sprint: the simulation's sprint is the Sprint bit applied with the stick forward on the
/// ground (<see cref="Combat.SprintApplies"/>, <c>Combat.StepPlayer</c>). Walking and running on the ground never exceed
/// <see cref="SimConfig.RunSpeed"/> (the speeds are the original's: run 6.5, sprint 10), so a <b>grounded</b> marine that is not rolling, charging,
/// reacting or in a jump-pack phase, and was not in one of those for <see cref="SprintLockout"/> s (their momentum is over run speed too), shows the
/// sprint while it moves forward faster than <see cref="SprintEnterFraction"/> of the way from the run to the sprint speed (down to
/// <see cref="SprintLeaveFraction"/>, a hysteresis). The same rule for the own predicted state and for remote players (velocity against facing,
/// the state's flags); it needs no input and so holds for both. Rules in <see cref="ViewSmoothing"/> (engine-free, tested).</item>
/// <item><b>Relaxed run</b>: the navigation graphs' "Run Relaxed" state (<c>SM_Bolter_NAV</c> for rifles and the heavy weapons, <c>SM_Pistol_NAV</c> for
/// pistols): eight directions with their own walk and run clips, back included (<c>SM_NAV_Run_B_01</c>, <c>SM_1HR_NAV_Run_B_01</c>), chosen by
/// the direction of the velocity against the facing (nearest of the eight, with hysteresis: <see cref="ViewSmoothing.NearestDirection"/>) and the speed band. The graph blends between
/// directions and speeds continuously; showing the nearest discrete clip is an <b>interpretation</b> (as in the ready posture, M27). The heavy bolter
/// has no relaxed graph state (always ready); when it is lowered (a sprint, a recent switch) it shows the rifle's relaxed clips.</item>
/// <item>The ready posture (M27, <c>NetworkView.HipFire.cs</c>) and aim mode (<c>NetworkView.Aim.cs</c>) already pick backward clips and are unchanged.</item>
/// </list>
/// Playback rates match the clips' planted-foot speed to the movement speed (<see cref="RunPlanted"/>, <see cref="WalkPlanted"/>,
/// <see cref="SprintPlanted"/>; measured with <c>--clip-stats</c>; the graphs bind the playback speed to variables the engine feeds, so the
/// rate is an <b>interpretation</b>). The clip choice, the speed bands and the sprint thresholds are interpretation; the clips are original.
/// </summary>
public partial class NetworkView
{
    /// <summary>Fractions of the way from <see cref="SimConfig.RunSpeed"/> to <see cref="SimConfig.SprintSpeed"/> above which a marine shows the sprint (and below which it stops; ours, see the class comment). 0.25 was the charge's margin (M31; the charge needs only W since protocol 39), now <c>SprintStop.SprintMargin</c>.</summary>
    const float SprintEnterFraction = 0.25f, SprintLeaveFraction = 0.1f;
    /// <summary>Seconds after a roll, charge, reaction or jump phase ended during which the sprint is not shown (the body still carries that speed; ours).</summary>
    const double SprintLockout = 0.15;
    /// <summary>Planted-foot ground speeds (m/s) of the relaxed run clips, by <see cref="Dirs8"/> (F, FR, R, BR, B, BL, L, FL), the walk clips and the sprint clips (<c>--clip-stats</c>, rifle clips; the pistol's agree except two noisy back diagonals).</summary>
    static readonly float[] RunPlanted = [6.3f, 6.0f, 5.55f, 4.6f, 4.5f, 4.5f, 5.65f, 6.25f];
    const float WalkPlanted = 1.75f, SprintPlanted = 7.8f;
    /// <summary>
    /// Animation pass 3: the planted-foot speed of each sprint family's forward clip (m/s; <c>--clip-stats</c>, mean of the two feet where both are read): <c>SM_MP_1HR_NAV_Sprint_F</c> 7.84,
    /// <c>SM_NAV_TB_Sprint_F</c> 6.79 / 7.84 (7.3), <c>SM_NAV_LC_Sprint_F</c> 7.71. The heavy bolter's clip is slower and longer (0.7 s), so at the same body speed it plays a little faster. <c>--no-p3</c>: the rifle's for all.
    /// </summary>
    static float SprintPlantedOf(SprintFamily family) => CharacterViewer.NoP3 ? SprintPlanted : family switch { SprintFamily.Heavy => 7.3f, SprintFamily.Cannon => 7.7f, _ => SprintPlanted };
    /// <summary>Animation pass 3: the time constant (s; ours) with which the sprint's strafe blend follows the movement angle (the graphs damp it, <c>FLT_Nav_Motion_Angle_SPRINT_DAMP</c>, with an accel / decel modifier whose values are not read): a remote marine's angle steps with every snapshot.</summary>
    const float SprintAngleSeconds = 0.12f;

    /// <summary><c>--no-locomotion</c> (test aid): the old single forward run clip for every movement outside aim mode and the ready posture (no sprint or backpedal clips).</summary>
    public bool NoLocomotion { get; init; }

    Task<LocomotionBundle>? _locoLoad;
    LocomotionAnimations? _locoAnims;
    string _locoNote = "not loaded";
    readonly Dictionary<int, LocoTrack> _loco = new();
    readonly Dictionary<(HipFireWeaponClass, int, bool), LocoStanceDef?> _locoStances = new();
    readonly Dictionary<SprintFamily, SprintStanceDef?> _locoSprints = new();
    readonly HashSet<string> _locoNoted = new();
    readonly Dictionary<HipFireWeaponClass, RelaxedMoveSet?> _locoRelaxed = new();   // the family's relaxed set, resolved once
    readonly Dictionary<HipFireWeaponClass, float[]> _locoAngles = new();            // its directions' angles, for the nearest-direction search
    string _locoNowText = "";

    sealed class LocoTrack
    {
        public bool Sprint;          // hysteresis: in the sprint
        public double SprintBlockedUntil;   // no sprint before this (_connectedFor): a roll, charge, reaction or jump just ended
        public bool Walk;            // hysteresis between the walk and run bands
        public int DirFamily = -1, Dir;   // the direction last shown (hysteresis against flicker at the sector borders)
        public readonly VelocityDamper Damper = new();   // M40-T4: the ground velocity the blend space follows (NetworkView.GaitBlend.cs)
        public double GaitAt = double.NegativeInfinity;   // _connectedFor at the last blend update
        // The action-clip hand-off (NetworkView.Handoff.cs): the locomotion has the legs under the attack / the throw in progress.
        public bool AttackHand, HandHeavy, ThrowHand;
        public int HandStep;
        public float HandTime, HandThrowTime;
        public bool GaitMoving, HipGaitMoving;   // hysteresis of the moving / standing choice (GaitLeaveSpeed): relaxed run, ready run
        // M40-T5 (NetworkView.TurnInPlace.cs): the shown body heading, the aim it follows and the standing posture it is planted in this frame.
        public readonly BodyYaw Body = new();
        public double BodyAt = double.NegativeInfinity;
        public float AimYaw;
        public TurnPosture? Stand;
        public TurnRig? StandRig;
        public bool WasStanding, PrevStanding;   // this frame's and the last frame's standing flags, for the stop's foot pins and the run start
        public readonly RunStartState Start = new();   // NetworkView.RunStart.cs
        public float GaitSpeedPrev;   // the damped ground speed of the last blend update, for the stop's deceleration test
        // Animation pass 1 (NetworkView.Fall.cs): the fall and the landing.
        public readonly FallTracker Fall = new();  // the engine-free fall state machine (SmContent.FallTracker): debounce, peak speed, landing, the heavy bolter's states
        public double FallLayerAt = double.NegativeInfinity;
        public FallSet? FallSet;                  // the family's fall, fixed when the fall begins
        public bool LandProtected;                // the class has fall protection (short exit)
        public float LegsWeight, LandKeep = 1f;   // the fall layer's weight; the share of the landing clip kept by a marine that moves
        public double FallLogAt = double.NegativeInfinity;   // test log throttle
        // Animation pass 2 (NetworkView.UpperBody.cs): the weapon switch, the flinch and what the death clip needs.
        public WeaponSlot SwSlot;
        public bool SwSeen;
        public double SwitchAt = double.NegativeInfinity, SwitchTraceAt = double.NegativeInfinity;
        public float SwitchDuration;
        public SwitchClip? Switch;
        public FlinchClip? Flinch;
        public double FlinchAt = double.NegativeInfinity, FlinchLast = double.NegativeInfinity, HeadHitAt = double.NegativeInfinity;
        public bool WasSprint, WasCharging, WasAirborne;
        public float SprintBlend; public double SprintBlendAt = double.NegativeInfinity;   // animation pass 3: the damped strafe blend of the sprint
        public string? WeaponName;
    }

    /// <summary>One resolved relaxed run stance: a single looping clip.</summary>
    sealed record LocoStanceDef(string Key, string Clip, float Planted);

    /// <summary>One resolved sprint: the three clips (null: not in the rig) and the blend in.</summary>
    sealed record SprintStanceDef(string Key, string? Left, string Mid, string? Right, float EnterBlend);

    /// <summary>Starts reading the sprint and relaxed-run data on a worker thread (the behaviour graphs: slow, never on the main thread).</summary>
    void SetUpLocomotion()
    {
        if (NoLocomotion || Lean) { _locoNote = Lean ? "not loaded (lean bot)" : "switched off (--no-locomotion)"; return; }   // perf/lean-bots: poses only (the turns too)
        string gameDir = _gameDir;
        _locoNote = "loading";
        _locoLoad = Task.Run(() => GaitLibrary.Load(gameDir));   // M40-T4: also measures the clips' gaits
        SetUpTurns();   // M40-T5: the turn-in-place, aim-offset idle and run-start data (NetworkView.TurnInPlace.cs)
        SetUpSprintEdges();   // animation pass 3: the sprint's start and stop clips, the pistol's relaxed idle (NetworkView.SprintEdges.cs)
    }

    LocomotionAnimations? LocoData()
    {
        if (_locoAnims is not null) return _locoAnims;
        if (_locoLoad is not { IsCompleted: true } load) return null;
        _locoLoad = null;
        if (load.IsFaulted || load.IsCanceled)
        {
            _locoNote = "not available: " + (load.Exception?.GetBaseException().Message ?? "cancelled");
            GD.PrintErr("locomotion: " + _locoNote);
            return null;
        }
        _locoAnims = load.Result.Locomotion;
        GaitTake(load.Result);
        _locoNote = string.Join(", ", _locoAnims.Relaxed.Select(s => $"relaxed {s.Class} {s.Directions.Count} directions ({s.Directions.FirstOrDefault(d => d.AngleDegrees is 180f or -180f)?.Run?.Clip ?? "no back clip"})"))
            + ", " + string.Join(", ", _locoAnims.Sprints.Select(s => $"sprint {s.Family} {(s.Clips.FirstOrDefault(c => Math.Abs(c.AngleDegrees) < 1f)?.Clip ?? "none")} blend in {s.EnterBlend:F2} s"));
        GD.Print("locomotion: " + _locoNote);
        foreach (var n in _locoAnims.Notes) GD.Print("  locomotion note: " + n);
        return _locoAnims;
    }

    LocoTrack LocoOf(int id)
    {
        if (!_loco.TryGetValue(id, out var t)) _loco[id] = t = new LocoTrack();
        return t;
    }

    void LocomotionForget(int id) { _loco.Remove(id); _edges.Remove(id); }

    bool RigHasClip(string clip) => _assets!.Model.Rig.AnimationNames.Contains(clip, StringComparer.OrdinalIgnoreCase) && _assets.Animation(clip) is not null;

    /// <summary>
    /// The sprint clips of the weapon in hand, as the base graph picks its sprint behaviour (animation pass 3; the owner, 2026-10-29: "devastator/havoc needs to have a different sprint
    /// animation", superseding the note of 2026-10-28 that every class shares one): the heavy bolter's state references <c>SM_Heavy_Bolter_NuSprint</c> (<c>SM_NAV_TB_Sprint_*</c>), the
    /// lascannon's and plasma cannon's <c>SM_Lascannon_Plasma_NuSprint</c> (<c>SM_NAV_LC_Sprint_*</c>), every other weapon (the bolt pistol in a heavy class's hand too: its "BoltPistol Sprint"
    /// state) <c>MP_SM_Bolter_NuSprint</c> (<c>SM_MP_1HR_NAV_Sprint_*</c>). <c>--shared-sprint</c> (test aid) restores the one shared sprint; <c>--per-weapon-sprint</c> is now the default and kept as a no-op.
    /// </summary>
    SprintFamily SprintFamilyOf(PlayerState s) =>
        !SharedSprint && _config is not null && Loadouts.InHand(_config, s)?.Name is { } name ? LocomotionAnimations.SprintFamilyOf(name) : SprintFamily.Rifle;

    /// <summary><c>--per-weapon-sprint</c> (test aid of M36): the graphs' per-weapon sprint clips; the default since animation pass 3.</summary>
    public bool PerWeaponSprint { get; init; }

    /// <summary><c>--shared-sprint</c> (test aid, animation pass 3): every class sprints on the rifle's clips, as before the owner's decision of 2026-10-29.</summary>
    static readonly bool SharedSprint = OS.GetCmdlineUserArgs().Contains("--shared-sprint") || OS.GetCmdlineUserArgs().Contains("--no-p3");

    /// <summary>Whether a marine moving like this is in the sprint (see the class comment); updates the hysteresis.</summary>
    bool LocoSprinting(PlayerState s, LocoTrack t, float speed, float forward)
    {
        var c = _config!;
        // Speeds above the run speed that are not the sprint: a roll, the charge, a reaction's knockback, the jump pack, the air.
        bool other = !s.Grounded || s.Rush != RushPhase.None || s.Reaction != ReactionKind.None || Evade.Evading(s) || s.Jump != JumpPhase.None;
        if (other) t.SprintBlockedUntil = _connectedFor + SprintLockout;
        // Animation pass 3: the simulation's sprint stop (NetworkView.SprintEdges.cs) ends the loop at once, when the stop clip will play (review S5).
        if (!CharacterViewer.NoP3 && _config is not null && SprintLoopEndsForStop(s, SprintFamilyOf(s))) { t.Sprint = false; return false; }
        t.Sprint = s.Alive && ViewSmoothing.Sprinting(t.Sprint, !other && _connectedFor >= t.SprintBlockedUntil, speed, forward, c.RunSpeed, c.SprintSpeed, SprintEnterFraction, SprintLeaveFraction);
        return t.Sprint;
    }

    /// <summary>
    /// The movement pose outside aim mode and the ready posture: the sprint loop, or the relaxed run of the movement direction and speed.
    /// False when the marine is (nearly) standing - the caller shows the idle clip - or when the data or a clip is missing (the old
    /// single run clip then shows). Sets <see cref="_locoNowText"/> for the own player's F1 line.
    /// </summary>
    bool LocoPlace(CharacterViewer character, PlayerState s, HipTrack hip, bool own)
    {
        if (own) _locoNowText = "";
        if (NoLocomotion || _config is null || LocoData() is not { } data) return false;
        var t = LocoOf(s.PlayerId);
        float speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
        float fx = -MathF.Sin(s.Yaw), fy = MathF.Cos(s.Yaw);
        float forward = s.Velocity.X * fx + s.Velocity.Y * fy, right = s.Velocity.X * fy - s.Velocity.Y * fx;
        float angle = MathF.Atan2(right, forward) * 180f / MathF.PI;
        // Animation pass 3: a sprint start under way holds the sprint while the body is still going forward faster than a run's start (its speed dips around the threshold).
        bool startHolds = !CharacterViewer.NoP3 && SprintStartActive(s.PlayerId) && s.Grounded && forward > RunThreshold && !SimSprintStopping(s);
        if ((LocoSprinting(s, t, speed, forward) || startHolds) && ((GaitSprint && GaitSprintPlace(character, s, hip, data, t, own)) || SprintPlace(character, s, hip, data, angle, speed, own))) return true;
        // A marine enters the movement poses above RunThreshold (raw simulation speed). With the blend space (it carries the idle pose as a share) it stays in
        // them while its damped speed (GaitPlace) is above GaitLeaveSpeed, so a reversal through zero speed, or a speed hovering at the threshold (a remote
        // marine's velocity changes with every snapshot), does not swap the blend for the idle stance and back.
        if (GaitUsable && t.GaitMoving)
        {
            if (GaitRelaxedPlace(character, s, hip, data, t, own)) return true;
            t.GaitMoving = false;
            StandAfterGait(s, t);   // qc/reference-pass: the aim-offset idle from this frame on (no one-frame plain idle)
        }
        if (speed <= RunThreshold) return false;
        if (GaitRelaxedPlace(character, s, hip, data, t, own)) { t.GaitMoving = true; return true; }
        return RelaxedPlace(character, s, hip, data, t, angle, speed, own);
    }

    SprintStanceDef? SprintStance(LocomotionAnimations data, SprintFamily family)
    {
        if (_locoSprints.TryGetValue(family, out var cached)) return cached;
        SprintStanceDef? def = null;
        if ((data.SprintFor(family) ?? data.SprintFor(SprintFamily.Rifle)) is { Clips.Count: > 0 } set)
        {
            var clips = set.Clips.OrderBy(c => c.AngleDegrees).ToList();
            var mid = clips.MinBy(c => MathF.Abs(c.AngleDegrees))!;
            string? left = clips[0].AngleDegrees < -1f && RigHasClip(clips[0].Clip) ? clips[0].Clip : null;
            string? rightClip = clips[^1].AngleDegrees > 1f && RigHasClip(clips[^1].Clip) ? clips[^1].Clip : null;
            if (RigHasClip(mid.Clip)) def = new SprintStanceDef("sprint:" + set.Family, left, mid.Clip, rightClip, set.EnterBlend);
            else _notes.Add($"sprint clip {mid.Clip} not in the rig; {family} shows the relaxed run while sprinting");
        }
        else if (_locoNoted.Add("sprint:" + family)) _notes.Add($"no sprint data for {family}; it shows the relaxed run while sprinting");
        _locoSprints[family] = def;
        return def;
    }

    bool SprintPlace(CharacterViewer character, PlayerState s, HipTrack hip, LocomotionAnimations data, float angle, float speed, bool own)
    {
        var family = SprintFamilyOf(s);
        if (SprintStance(data, family) is not { } def) return false;
        // Animation pass 3 (NetworkView.SprintEdges.cs): a sprint from standing shows the graph's start clip first.
        float? seed = null;
        if (!CharacterViewer.NoP3 && SprintStartPlace(character, s, family, out seed)) { hip.Kind = PoseKind.Normal; return true; }
        // The blend runs over -45 .. +45 degrees: left strafe clip, forward, right strafe clip.
        float t = Math.Clamp(angle / 45f, -1f, 1f);
        if (!CharacterViewer.NoP3)
        {
            var lt = LocoOf(s.PlayerId);
            double since = _connectedFor - lt.SprintBlendAt;
            lt.SprintBlend = since > 0.25 ? t : lt.SprintBlend + (t - lt.SprintBlend) * (1f - MathF.Exp(-(float)since / SprintAngleSeconds));
            lt.SprintBlendAt = _connectedFor;
            t = lt.SprintBlend;
        }
        float rate = Math.Clamp(speed / SprintPlantedOf(family), 0.7f, 1.4f);
        // Review S2: an action clip handing over into the sprint (the charge's 0.333 s, an evade's, a melee strike's) keeps its blend.
        float enter = !CharacterViewer.NoP3 && _fadeOverride > 0f ? _fadeOverride : def.EnterBlend;
        if (!character.Stance(def.Key, def.Left, def.Mid, def.Right, t, rate, enter, seed ?? -1f)) return false;
        if (!CharacterViewer.NoP3) SprintShown(s, family);
        hip.Kind = PoseKind.Normal;
        if (own && _showInfo) _locoNowText = $"sprint ({family}: {def.Mid}), angle {angle:F0} deg, speed {speed:F1} m/s, clip rate {rate:F2}";
        return true;
    }

    LocoStanceDef? RelaxedStance(RelaxedMoveSet set, float angleDegrees, bool walk, LocoTrack t)
    {
        // The direction last shown is kept until another is closer by more than HipDirectionHold degrees.
        if (!_locoAngles.TryGetValue(set.Class, out var angles)) _locoAngles[set.Class] = angles = set.Directions.Select(d => d.AngleDegrees).ToArray();
        int best = ViewSmoothing.NearestDirection(angles, angleDegrees, t.DirFamily == (int)set.Class ? t.Dir : -1, HipDirectionHold);
        if (best < 0) return null;
        t.DirFamily = (int)set.Class; t.Dir = best;
        var key = (set.Class, best, walk);
        if (_locoStances.TryGetValue(key, out var cached)) return cached;
        var direction = set.Directions[best];
        var band = walk ? direction.Walk ?? direction.Run : direction.Run;
        LocoStanceDef? def = null;
        if (band is not null && RigHasClip(band.Clip))
        {
            int sector = Array.IndexOf(Dirs8, direction.Label);
            bool isWalk = walk && direction.Walk is not null;
            def = new LocoStanceDef($"loco:{set.Class}:{direction.Label}:{(isWalk ? "walk" : "run")}", band.Clip, isWalk ? WalkPlanted : sector >= 0 ? RunPlanted[sector] : RunPlanted[0]);
        }
        else if (_locoNoted.Add($"relaxed:{key}")) _notes.Add($"relaxed run: no usable clip for {set.Class} {direction.Label} {(walk ? "walk" : "run")}; the forward run clip shows there");
        _locoStances[key] = def;
        return def;
    }

    bool RelaxedPlace(CharacterViewer character, PlayerState s, HipTrack hip, LocomotionAnimations data, LocoTrack t, float angle, float speed, bool own)
    {
        // The heavy bolter has no relaxed graph state: the rifle's.
        var family = HipFamilyOf(s) == HipFireWeaponClass.Pistol ? HipFireWeaponClass.Pistol : HipFireWeaponClass.Rifle;
        if (!_locoRelaxed.TryGetValue(family, out var set)) _locoRelaxed[family] = set = data.RelaxedFor(family) ?? data.RelaxedFor(HipFireWeaponClass.Rifle);
        if (set is null || set.Directions.Count == 0) return false;
        bool walk = speed < HipWalkBelow + (t.Walk ? HipBandHold : -HipBandHold);
        t.Walk = walk;
        if (RelaxedStance(set, angle, walk, t) is not { } def) return false;
        float rate = walk ? Math.Clamp(speed / def.Planted, 0.6f, 1.5f) : Math.Clamp(speed / def.Planted, 0.7f, 1.4f);
        if (!character.Stance(def.Key, null, def.Clip, null, 0f, rate, HipRelaxFade(s, hip))) return false;
        hip.Kind = PoseKind.Normal;
        if (own && _showInfo) _locoNowText = $"relaxed run {def.Clip}, angle {angle:F0} deg, speed {speed:F1} m/s, clip rate {rate:F2}";
        return true;
    }

    /// <summary><c>--bot-gait</c> (M36 test aid, with <c>--bot</c>): the bot stays near where it spawned and walks every gait in turn at a fixed view, a 10 s cycle: forward 1 s, back 1.4 s, left 1 s, right 1 s, sprint 1 s, back 2 s (about as far as the sprint went), forward-left 1 s, back-right 1 s, then 0.6 s walking back to the start point.</summary>
    public bool BotGait { get; init; }

    System.Numerics.Vector2? _gaitHome;
    int _gaitPhase = -1;

    /// <summary><c>--gait-sweep</c> (M40-T1 test aid, with <c>--bot-gait</c>): instead of the 10 s cycle, a ~80 s script for the animation probe (<c>--anim-probe</c>): out-and-back
    /// legs at every 22.5 degree heading at a run, then at a walk, a sprint with the stick turned, then quick reversals (strafe, forward-back, diagonal): the view stays fixed.</summary>
    static readonly bool GaitSweep = OS.GetCmdlineUserArgs().Contains("--gait-sweep");

    // The sweep script: (stick heading degrees from forward, + right; stick length; buttons; seconds of the out leg; "back" = the return leg runs the opposite stick until home).
    sealed record SweepLeg(float Heading, float Stick, InputButtons Buttons, float Seconds, bool Back);

    static readonly List<SweepLeg> SweepScript = BuildSweepScript();

    static List<SweepLeg> BuildSweepScript()
    {
        var legs = new List<SweepLeg>();
        // Run: eight out-and-back pairs cover all sixteen headings (the back leg is the opposite heading).
        for (int i = 0; i < 8; i++) { legs.Add(new SweepLeg(i * 22.5f, 1f, InputButtons.None, 1.3f, false)); legs.Add(new SweepLeg(i * 22.5f + 180f, 1f, InputButtons.None, 3f, true)); }
        // Walk: a short stick.
        for (int i = 0; i < 8; i++) { legs.Add(new SweepLeg(i * 22.5f, 0.4f, InputButtons.None, 1.6f, false)); legs.Add(new SweepLeg(i * 22.5f + 180f, 0.4f, InputButtons.None, 3f, true)); }
        // Sprint, straight and with the stick turned (the sprint blend runs over -45 .. +45 degrees).
        foreach (float h in new[] { 0f, -30f, 30f }) { legs.Add(new SweepLeg(h, 1f, InputButtons.Sprint, 1.4f, false)); legs.Add(new SweepLeg(180f + h, 1f, InputButtons.None, 3f, true)); }
        // Reversals: left-right (strafe), forward-back, diagonal; 0.4 s legs.
        for (int i = 0; i < 8; i++) legs.Add(new SweepLeg(i % 2 == 0 ? -90f : 90f, 1f, InputButtons.None, 0.4f, false));
        for (int i = 0; i < 6; i++) legs.Add(new SweepLeg(i % 2 == 0 ? 0f : 180f, 1f, InputButtons.None, 0.5f, false));
        for (int i = 0; i < 6; i++) legs.Add(new SweepLeg(i % 2 == 0 ? 45f : -135f, 1f, InputButtons.None, 0.5f, false));
        legs.Add(new SweepLeg(0f, 0f, InputButtons.None, 1.5f, false));
        return legs;
    }

    /// <summary><c>--gait-sweep-fire</c> (with <c>--gait-sweep</c>): also taps the trigger for 0.1 s every 3 s outside the sprint legs, so the weapon is up (the ready run, M27) through the sweep.</summary>
    static readonly bool GaitSweepFire = OS.GetCmdlineUserArgs().Contains("--gait-sweep-fire");

    int _sweepLeg; uint _sweepLegTick; bool _sweepStarted;

    PlayerInput BotSweepInput(uint tick)
    {
        var me = _client!.Predicted;
        if (_gaitHome is null && me.Alive)
        {
            _gaitHome = new System.Numerics.Vector2(me.Position.X, me.Position.Y);
            GD.Print($"gait sweep home {me.Position.X:F2},{me.Position.Y:F2} facing yaw {me.Yaw:F2}");
        }
        if (!_sweepStarted) { _sweepStarted = true; _sweepLegTick = tick; }
        var leg = SweepScript[_sweepLeg % SweepScript.Count];
        double inLeg = (tick - _sweepLegTick) * (double)_config!.TickSeconds;
        bool done = inLeg >= leg.Seconds;
        if (leg.Back && _gaitHome is { } home)
        {
            // Back until home: the position along the way out is behind the start point (or the leg has run too long).
            float h = (leg.Heading + 180f) * MathF.PI / 180f;
            var forward = new System.Numerics.Vector2(-MathF.Sin(_yaw), MathF.Cos(_yaw));
            var rightV = new System.Numerics.Vector2(forward.Y, -forward.X);
            var outDir = rightV * MathF.Sin(h) + forward * MathF.Cos(h);   // the direction the out leg went
            var offset = new System.Numerics.Vector2(me.Position.X, me.Position.Y) - home;
            done = (inLeg > 0.3 && System.Numerics.Vector2.Dot(offset, outDir) <= 0.25f) || inLeg >= leg.Seconds;
        }
        if (done)
        {
            _sweepLeg++; _sweepLegTick = tick;
            leg = SweepScript[_sweepLeg % SweepScript.Count];
            if (_sweepLeg % SweepScript.Count == 0) GD.Print($"SWEEP cycle {_sweepLeg / SweepScript.Count} done at {DateTime.Now:HH:mm:ss.fff}");
        }
        float a = leg.Heading * MathF.PI / 180f;
        var buttons = leg.Buttons;
        if (GaitSweepFire && (buttons & InputButtons.Sprint) == 0 && tick * (double)_config.TickSeconds % 3.0 < 0.1) buttons |= InputButtons.Fire;
        if (BotSlot is { Length: > 0 } && _connectedFor is >= 3.0 and < 3.2)
            buttons |= BotSlot.StartsWith("pi", StringComparison.OrdinalIgnoreCase) ? InputButtons.SlotPistol : InputButtons.SlotPrimary;
        return new PlayerInput(tick, MathF.Sin(a) * leg.Stick, MathF.Cos(a) * leg.Stick, _yaw, buttons, 0f);
    }

    PlayerInput BotGaitInput(uint tick)
    {
        if (TurnSweepOn) return BotTurnSweepInput(tick);   // M40-T5 (NetworkView.TurnSweep.cs)
        if (LedgeWalk) return BotLedgeInput(tick);         // animation pass 1 (NetworkView.Ledge.cs): walks off ledges
        if (TransitionSweep) return BotTransitionInput(tick);   // animation pass 3 (NetworkView.TransitionSweep.cs): state switches for the transition probe
        if (GaitSweep) return BotSweepInput(tick);
        if (ActionSweep) return BotActionInput(tick);
        var me = _client!.Predicted;
        if (_gaitHome is null && me.Alive)
        {
            _gaitHome = new System.Numerics.Vector2(me.Position.X, me.Position.Y);
            GD.Print($"gait home {me.Position.X:F2},{me.Position.Y:F2} facing yaw {me.Yaw:F2}");
        }
        double t = tick * (double)_config!.TickSeconds % 10.0;
        int phase = t switch { < 1.0 => 0, < 2.4 => 1, < 3.4 => 2, < 4.4 => 3, < 5.4 => 4, < 7.4 => 5, < 8.4 => 6, < 9.4 => 7, _ => 8 };
        if (phase != _gaitPhase)
        {
            _gaitPhase = phase;
            GD.Print($"GAIT {new[] { "forward", "back", "left", "right", "sprint", "back-after-sprint", "forward-left", "back-right", "home" }[phase]} at {DateTime.Now:HH:mm:ss.fff}");
        }
        // Every 0.5 s: the pose the own character shows (clip of the stance, weapon family in hand), for the log.
        if (tick % 15 == 0 && _local is not null)
            GD.Print($"GAITPOSE phase {phase} speed {new Vector2(me.Velocity.X, me.Velocity.Y).Length():F1} m/s weapon {(Loadouts.InHand(_config, me)?.Name ?? "none")} clip {_local.Playing}");
        (float x, float y, InputButtons b) = t switch
        {
            < 1.0 => (0f, 1f, InputButtons.None),
            < 2.4 => (0f, -1f, InputButtons.None),
            < 3.4 => (-1f, 0f, InputButtons.None),
            < 4.4 => (1f, 0f, InputButtons.None),
            < 5.4 => (0f, 1f, InputButtons.Sprint),
            < 7.4 => (0f, -1f, InputButtons.None),
            < 8.4 => (-0.7f, 0.7f, InputButtons.None),
            < 9.4 => (0.7f, -0.7f, InputButtons.None),
            _ => (0f, 0f, InputButtons.None),
        };
        // The last 0.6 s of every cycle: back toward the start point (so a fixed camera keeps the bot in view).
        if (t >= 9.4 && _gaitHome is { } home)
        {
            float dx = home.X - me.Position.X, dy = home.Y - me.Position.Y;
            if (dx * dx + dy * dy > 0.04f) { float f = 1f / MathF.Max(1f, MathF.Max(MathF.Abs(dx), MathF.Abs(dy))); (x, y) = (dx * f, dy * f); }
        }
        // --bot-slot pistol|primary: one press of that slot key a few seconds after joining (as the other bot scripts).
        if (BotSlot is { Length: > 0 } && _connectedFor is >= 3.0 and < 3.2)
            b |= BotSlot.StartsWith("pi", StringComparison.OrdinalIgnoreCase) ? InputButtons.SlotPistol : InputButtons.SlotPrimary;
        return new PlayerInput(tick, x, y, _yaw, b, 0f);
    }

    /// <summary>The F1 info line on the sprint and relaxed-run poses.</summary>
    string LocomotionInfoLine() =>
        "Sprint and backpedal: original clips from the navigation and sprint graphs (sprint: MP_SM_Bolter_NuSprint / SM_Heavy_Bolter_NuSprint / SM_Lascannon_Plasma_NuSprint; relaxed run: SM_Bolter_NAV / SM_Pistol_NAV, eight directions, back included); " +
        $"which clip, the speed bands and the sprint threshold ({SprintEnterFraction:P0} of the way from run to sprint speed): interpretation. Loaded: {_locoNote}." +
        (_locoNowText.Length > 0 ? " Now: " + _locoNowText : "") + " " + GaitInfoLine() + " " + TurnInfoLine();
}
