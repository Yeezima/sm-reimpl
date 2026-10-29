using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// The locomotion blend space in the networked view (M40-T4; the M40 audit's D4). Until now a moving marine showed one discrete clip, the nearest of the
/// eight directions in one speed band, which slides the feet by up to half the speed between two directions' headings and flickers when the heading
/// reverses. Now the relaxed run, the ready (hip-fire) run and the sprint are a blend of the neighbouring clips (<see cref="GaitSpace"/>, engine-free and
/// tested): the two neighbouring directions of the movement heading times the two neighbouring speed bands, with weights from the heading and speed of the
/// (damped, <see cref="VelocityDamper"/>) ground velocity, and the cadence of the shared foot-synchronised phase (<see cref="GaitPhase"/>) set so that the
/// blend's planted feet move back exactly as fast as the body moves forward (the clips' measured strides: <see cref="ClipGait"/>, <see cref="GaitLibrary"/>).
/// Below <see cref="IdleBlendSpeed"/> the idle pose takes the rest of the weight (<see cref="GaitBlend.MovingWeight"/>), so a reversal passes through the idle
/// pose instead of flipping from one direction's clip to the other's. The same for the own marine and for remote ones; the ready run's down / level / up
/// pitch clips are weighted by the aim pitch as before.
/// <para>
/// The graphs blend these clips continuously too (<c>FLT_Nav_Motion_Angle_DAMPED</c>, <c>FLT_Input_Nav_Speed_DAMPED</c>); how the weights follow from the
/// velocity (planted-foot strides), the foot-plant phase sync (the original's SYNC_L1 / SYNC_R1 are declared in the graphs but carried by no sequencer or
/// clip in the install), the damping and the idle share are <b>interpretation</b> (ours). <c>--no-gait-blend</c> (test aid) restores the nearest-clip poses
/// for before / after runs on the same demo.
/// </para>
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--no-gait-blend</c> (test aid, M40-T4): the old nearest-clip locomotion poses.</summary>
    static readonly bool NoGaitBlend = OS.GetCmdlineUserArgs().Contains("--no-gait-blend");

    /// <summary>
    /// <c>--gait-sprint</c> (test aid): the sprint through the blend space too. Off by default: on the sweep demos its standing foot slid more than the old three-way blend
    /// (0.37 against 0.17 to 0.28 m/s), so the sprint keeps the M36 blend until it measures no worse (spec "Locomotion blend space (M40-T4)").
    /// </summary>
    static readonly bool GaitSprint = OS.GetCmdlineUserArgs().Contains("--gait-sprint");

    /// <summary>Ground speed (m/s) from which the moving clips have the whole weight; below it the idle pose takes the rest (ours; the walk clip's pace is 1.9 m/s).</summary>
    const float IdleBlendSpeed = 1.0f;
    /// <summary>
    /// Animation pass 1: with the walk-on-the-spot clip as the blend's zero-speed band (it steps in place: the graph's speed blend runs 0 = walk on the spot, 2 = walk, 4.5 / 6.5 = run), the idle pose is only
    /// needed right at the stop, so the idle share ends at this speed (m/s; ours) instead of <see cref="IdleBlendSpeed"/>: a marine between a stand and a walk steps on the spot instead of shuffling the idle pose.
    /// The stop's foot pins (M40-T5) start where the idle share starts, so they begin this close to rest. Above <see cref="GaitLeaveSpeed"/> so the cross-fade to the idle stance has a share to finish.
    /// </summary>
    const float WosIdleBlendSpeed = 0.35f;
    /// <summary><c>--no-wos</c> (test aid): the relaxed blend without the walk-on-the-spot band (the idle share below 1 m/s, as before animation pass 1).</summary>
    static readonly bool NoWos = OS.GetCmdlineUserArgs().Contains("--no-wos");
    /// <summary>A marine already in the blend goes back to the idle stance only below this speed (m/s; ours; <see cref="NetworkView.RunThreshold"/> enters).</summary>
    const float GaitLeaveSpeed = 0.2f;

    GaitLibrary? _gaits;
    readonly Dictionary<string, GaitFamily?> _gaitFamilies = new();
    readonly HashSet<string> _gaitNoted = new();

    /// <summary>One blend space with its clips as a <see cref="CharacterViewer.BlendSet"/>: clip index = (direction x bands + band) x slots + slot, the idle clips after them.</summary>
    sealed class GaitFamily
    {
        public required CharacterViewer.BlendSet Set;
        public required GaitSpace Space;
        public int Slots = 1;                // clips per direction and band: 1, or 3 (aim down, level, up)
        public int IdleStart = -1, IdleSlots;    // the idle clips' indices (-1: none)
        public float IdleSpeed = IdleBlendSpeed; // ground speed (m/s) from which the moving clips have the whole weight; below it the idle pose takes the rest
        public float[] Weights = [];
        public readonly GaitWeight[] Solution = new GaitWeight[GaitSpace.MaxWeights];
        public (float LowDegrees, float HighDegrees, bool HasLow, bool HasHigh)[] Pitch = [];   // per direction x band (Slots = 3)
    }

    /// <summary>Keeps the gaits of the loaded bundle and notes what is missing (once).</summary>
    void GaitTake(LocomotionBundle bundle)
    {
        _gaits = bundle.Gaits;
        // The worker already decoded these clips: hand them to the rig's cache instead of decoding them again on the first moving frame.
        if (_assets is not null) foreach (var (name, animation) in bundle.Gaits.Animations) _assets.Rig.Preload(bundle.Gaits.RigKey, name, animation);
        foreach (var n in bundle.Gaits.Notes) GD.Print("  gait note: " + n);
        GD.Print($"gait blend: {bundle.Gaits.Clips.Count} clip gaits measured (planted-foot strides and sync times){(NoGaitBlend ? "; OFF (--no-gait-blend)" : "")}");
    }

    bool GaitUsable => !NoGaitBlend && _gaits is not null && _assets is not null;

    ClipGait? GaitOf(string clip) => _gaits?.Get(clip);

    /// <summary>The blend space of the relaxed run of a weapon family (null, noted once, when a clip or gait is missing).</summary>
    GaitFamily? RelaxedGaitFamily(LocomotionAnimations data, HipFireWeaponClass family)
    {
        string key = "relaxed:" + family;
        if (_gaitFamilies.TryGetValue(key, out var cached)) return cached;
        if (family == HipFireWeaponClass.Pistol && SprintEdgesLoading) return null;   // animation pass 3: the pistol's idle clip is read with the sprint edges; not cached before they are in
        GaitFamily? result = null;
        if ((data.RelaxedFor(family) ?? data.RelaxedFor(HipFireWeaponClass.Rifle)) is { Directions.Count: > 0 } set)
        {
            var directions = new List<(float, IReadOnlyList<ClipGait>)>();
            var clips = new List<CharacterViewer.BlendClip>();
            bool ok = true;
            // Animation pass 1: the graph's speed blend starts with the walk-on-the-spot clip at speed 0 (one more band, when every direction has it and its gait was measured).
            bool spot = !NoWos && set.Directions.All(d => d.WalkOnSpot is { } wos && GaitOf(wos.Clip) is not null && RigHasClip(wos.Clip));
            foreach (var d in set.Directions)
            {
                var run = d.Run; var walk = d.Walk ?? d.Run;
                var runGait = run is null ? null : GaitOf(run.Clip); var walkGait = walk is null ? null : GaitOf(walk.Clip);
                if (run is null || walk is null || runGait is null || walkGait is null || !RigHasClip(run.Clip) || !RigHasClip(walk.Clip)) { ok = false; break; }
                if (spot)
                {
                    var wosGait = GaitOf(d.WalkOnSpot!.Clip)!;
                    directions.Add((d.AngleDegrees, [wosGait, walkGait, runGait]));
                    clips.Add(new CharacterViewer.BlendClip(d.WalkOnSpot.Clip, wosGait));
                }
                else directions.Add((d.AngleDegrees, [walkGait, runGait]));
                clips.Add(new CharacterViewer.BlendClip(walk.Clip, walkGait));
                clips.Add(new CharacterViewer.BlendClip(run.Clip, runGait));
            }
            if (ok)
            {
                int idle = clips.Count;
                string idleClip = RelaxedIdleClip(family) ?? _idleAnimation ?? CharacterViewer.DefaultAnimation;   // animation pass 3: the pistol's own relaxed idle (NetworkView.SprintEdges.cs)
                if (RigHasClip(idleClip)) clips.Add(new CharacterViewer.BlendClip(idleClip, null));
                result = new GaitFamily
                {
                    Set = new CharacterViewer.BlendSet("gait:" + key, clips.ToArray()), Space = new GaitSpace(directions, cyclic: true),
                    IdleStart = clips.Count > idle ? idle : -1, IdleSlots = clips.Count > idle ? 1 : 0, Weights = new float[clips.Count],
                    IdleSpeed = spot ? WosIdleBlendSpeed : IdleBlendSpeed,
                };
            }
        }
        if (result is null && _gaitNoted.Add(key)) { _notes.Add($"gait blend: no usable relaxed-run clips or gaits for {family}; the nearest-clip pose shows"); GD.Print($"gait blend: no usable relaxed-run clips or gaits for {family}; the nearest-clip pose shows"); }
        _gaitFamilies[key] = result;
        return result;
    }

    /// <summary>The blend space of a sprint family: the three loops at -45 / 0 / +45 degrees as one band.</summary>
    GaitFamily? SprintGaitFamily(LocomotionAnimations data, SprintFamily family)
    {
        string key = "sprint:" + family;
        if (_gaitFamilies.TryGetValue(key, out var cached)) return cached;
        GaitFamily? result = null;
        if ((data.SprintFor(family) ?? data.SprintFor(SprintFamily.Rifle)) is { Clips.Count: > 0 } set)
        {
            var directions = new List<(float, IReadOnlyList<ClipGait>)>();
            var clips = new List<CharacterViewer.BlendClip>();
            bool ok = true;
            foreach (var c in set.Clips)
            {
                var gait = GaitOf(c.Clip);
                if (gait is null || !RigHasClip(c.Clip)) { ok = false; break; }
                directions.Add((c.AngleDegrees, [gait]));
                clips.Add(new CharacterViewer.BlendClip(c.Clip, gait));
            }
            if (ok) result = new GaitFamily { Set = new CharacterViewer.BlendSet("gait:" + key, clips.ToArray()), Space = new GaitSpace(directions, cyclic: false), Weights = new float[clips.Count] };
        }
        if (result is null && _gaitNoted.Add(key)) { _notes.Add($"gait blend: no usable sprint clips or gaits for {family}; the three-way blend shows"); GD.Print($"gait blend: no usable sprint clips or gaits for {family}; the three-way blend shows"); }
        _gaitFamilies[key] = result;
        return result;
    }

    /// <summary>The blend space of the ready run of a weapon family: its directions' walk and run bands, each a down / level / up pitch triple, and the standing aim triple as the idle pose.</summary>
    GaitFamily? HipGaitFamily(HipFireSet set)
    {
        string key = "hip:" + set.Class;
        if (_gaitFamilies.TryGetValue(key, out var cached)) return cached;
        GaitFamily? result = null;
        var standKey = AimKeys[(int)set.Class][0];   // HipFireWeaponClass and AimClass share their order: rifle, pistol, heavy
        if (_aimStances.TryGetValue(standKey, out var stand))
        {
            var directions = new List<(float, IReadOnlyList<ClipGait>)>();
            var clips = new List<CharacterViewer.BlendClip>();
            var pitch = new List<(float, float, bool, bool)>();
            bool ok = true;
            foreach (var d in set.Directions)
            {
                var bands = new List<ClipGait>();
                foreach (var band in new[] { d.Walk ?? d.Run, d.Run })
                {
                    if (band is not { Clips.Count: > 0 }) { ok = false; break; }
                    var ordered = band.Clips.OrderBy(c => c.PitchDegrees).ToList();
                    var low = ordered[0]; var high = ordered[^1]; var mid = ordered.MinBy(c => MathF.Abs(c.PitchDegrees))!;
                    var gait = GaitOf(mid.Clip);
                    if (gait is null || !RigHasClip(mid.Clip)) { ok = false; break; }
                    bool hasLow = low.PitchDegrees < -1f && RigHasClip(low.Clip), hasHigh = high.PitchDegrees > 1f && RigHasClip(high.Clip);
                    bands.Add(gait);
                    clips.Add(new CharacterViewer.BlendClip(hasLow ? low.Clip : null, hasLow ? gait : null));
                    clips.Add(new CharacterViewer.BlendClip(mid.Clip, gait));
                    clips.Add(new CharacterViewer.BlendClip(hasHigh ? high.Clip : null, hasHigh ? gait : null));
                    pitch.Add((hasLow ? low.PitchDegrees : -80f, hasHigh ? high.PitchDegrees : 80f, hasLow, hasHigh));
                }
                if (!ok) break;
                directions.Add((d.AngleDegrees, bands));
            }
            if (ok && directions.Count > 0)
            {
                int idle = clips.Count;
                clips.Add(new CharacterViewer.BlendClip(stand.Down, null));
                clips.Add(new CharacterViewer.BlendClip(stand.Level, null));
                clips.Add(new CharacterViewer.BlendClip(stand.Up, null));
                result = new GaitFamily
                {
                    Set = new CharacterViewer.BlendSet("gait:" + key, clips.ToArray()), Space = new GaitSpace(directions, cyclic: true), Slots = 3,
                    IdleStart = idle, IdleSlots = 3, Weights = new float[clips.Count], Pitch = pitch.ToArray(),
                };
            }
        }
        if (result is null && _gaitNoted.Add(key)) { _notes.Add($"gait blend: no usable ready-run clips or gaits for {set.Class}; the nearest-clip pose shows"); GD.Print($"gait blend: no usable ready-run clips or gaits for {set.Class}; the nearest-clip pose shows"); }
        _gaitFamilies[key] = result;
        return result;
    }

    /// <summary>
    /// Poses a marine from a blend space: damps its ground velocity, solves the weights and the cadence for it, adds the idle share below
    /// <see cref="IdleBlendSpeed"/>, weights the pitch triples (<paramref name="pitchRadians"/>, ready run only) and hands them to the character.
    /// False (nothing changes) when the velocity is (nearly) zero or a clip cannot be loaded. <paramref name="detail"/> is for the F1 line.
    /// </summary>
    bool GaitPlace(CharacterViewer character, PlayerState s, LocoTrack t, GaitFamily fam, float pitchRadians, float fadeSeconds, bool holding, bool own, out string detail)
    {
        detail = "";
        float dt = double.IsFinite(t.GaitAt) ? (float)Math.Max(0.0, _connectedFor - t.GaitAt) : float.PositiveInfinity;
        t.GaitAt = _connectedFor;
        var v = t.Damper.Step(new System.Numerics.Vector2(s.Velocity.X, s.Velocity.Y), dt);
        float fx = -MathF.Sin(s.Yaw), fy = MathF.Cos(s.Yaw);
        var body = new System.Numerics.Vector2(v.X * fy - v.Y * fx, v.X * fx + v.Y * fy);   // X right, Y forward
        if (body.Length() < (holding ? GaitLeaveSpeed : 0f)) return false;   // standing: the caller shows the idle stance
        int n = fam.Space.Solve(body, fam.Solution, out float cadence, out float ratio, out bool exact);
        if (n == 0) return false;
        float moving = GaitBlend.MovingWeight(body.Length(), fam.IdleSpeed);
        // The pins keep their M40-T5 trigger, the damped speed falling through IdleBlendSpeed (1 m/s), whatever the family's own idle share: with the walk-on-the-spot band the share ends at 0.35 m/s,
        // where the damper's tail (speed / 0.06 s = about 6 m/s squared) is below the deceleration gate and the pins would never start (animation pass 1, review).
        StopPinsFor(character, s, t, body.Length(), GaitBlend.MovingWeight(body.Length(), IdleBlendSpeed), dt);   // M40-T5: the feet that are down when the marine runs out of speed stay down (NetworkView.TurnInPlace.cs)
        var w = fam.Weights;
        Array.Clear(w);
        int bands = fam.Space.BandCount;
        for (int i = 0; i < n; i++)
        {
            var g = fam.Solution[i];
            int cell = g.Direction * bands + g.Band;
            if (fam.Slots == 1) { w[cell] += g.Weight * moving; continue; }
            var p = fam.Pitch[cell];
            float pitchDegrees = pitchRadians * 180f / MathF.PI;
            float pitchT = pitchDegrees >= 0f ? (p.HasHigh ? pitchDegrees / p.HighDegrees : 0f) : (p.HasLow ? pitchDegrees / p.LowDegrees * -1f : 0f);
            pitchT = Math.Clamp(pitchT, -1f, 1f);
            float low = p.HasLow ? MathF.Max(0f, -pitchT) : 0f, high = p.HasHigh ? MathF.Max(0f, pitchT) : 0f;
            w[cell * 3] += g.Weight * moving * low;
            w[cell * 3 + 1] += g.Weight * moving * (1f - low - high);
            w[cell * 3 + 2] += g.Weight * moving * high;
        }
        if (fam.IdleStart >= 0 && moving < 1f)
        {
            if (fam.IdleSlots == 1) w[fam.IdleStart] = 1f - moving;
            else
            {
                float t3 = Math.Clamp(pitchRadians / AimPitchRange, -1f, 1f);
                float low = fam.Set.Clips[fam.IdleStart].Clip is null ? 0f : MathF.Max(0f, -t3), high = fam.Set.Clips[fam.IdleStart + 2].Clip is null ? 0f : MathF.Max(0f, t3);
                w[fam.IdleStart] = (1f - moving) * low; w[fam.IdleStart + 1] = (1f - moving) * (1f - low - high); w[fam.IdleStart + 2] = (1f - moving) * high;
            }
        }
        if (!character.BlendStance(fam.Set, w, cadence, fadeSeconds)) return false;
        if (own && _showInfo) detail = $"gait blend: velocity {body.X:F1} right {body.Y:F1} forward m/s, {n} clips, cadence {cadence:F2} cycles/s (x{ratio:F2}{(exact ? "" : ", clamped")}), moving weight {moving:F2}";
        return true;
    }

    /// <summary>The sprint through the blend space; false when the gait data is missing (the old three-way blend then shows).</summary>
    bool GaitSprintPlace(CharacterViewer character, PlayerState s, HipTrack hip, LocomotionAnimations data, LocoTrack t, bool own)
    {
        if (!GaitUsable) return false;
        var family = SprintFamilyOf(s);
        if (SprintStance(data, family) is not { } def || SprintGaitFamily(data, family) is not { } fam) return false;
        if (!GaitPlace(character, s, t, fam, 0f, def.EnterBlend, false, own, out var detail)) return false;
        hip.Kind = PoseKind.Normal;
        if (own && _showInfo) _locoNowText = $"sprint ({family}) {detail}";
        return true;
    }

    /// <summary>The relaxed run through the blend space; false when the gait data is missing (the nearest clip then shows).</summary>
    bool GaitRelaxedPlace(CharacterViewer character, PlayerState s, HipTrack hip, LocomotionAnimations data, LocoTrack t, bool own)
    {
        if (!GaitUsable) return false;
        var family = HipFamilyOf(s) == HipFireWeaponClass.Pistol ? HipFireWeaponClass.Pistol : HipFireWeaponClass.Rifle;
        if (RelaxedGaitFamily(data, family) is not { } fam) return false;
        if (!GaitPlace(character, s, t, fam, 0f, HipRelaxFade(s, hip), t.GaitMoving, own, out var detail)) return false;
        hip.Kind = PoseKind.Normal;
        if (own && _showInfo) _locoNowText = $"relaxed run {family} {detail}";
        return true;
    }

    /// <summary>The ready run through the blend space (the caller has decided the marine is moving); false when the gait data is missing (the nearest clip then shows).</summary>
    bool GaitHipPlace(CharacterViewer character, PlayerState s, HipFireSet set, float fadeSeconds)
    {
        if (_gaits is null) LocoData();   // the bundle is polled by the first user: a marine that is always ready (the heavy bolter) never reaches the relaxed run
        if (!GaitUsable || _locoAnims is null || HipGaitFamily(set) is not { } fam) return false;
        var t = LocoOf(s.PlayerId);
        return GaitPlace(character, s, t, fam, s.Pitch, fadeSeconds, t.HipGaitMoving, false, out _);
    }

    /// <summary>The F1 line about the blend space.</summary>
    string GaitInfoLine() =>
        NoGaitBlend ? "Locomotion blend: off (--no-gait-blend)."
        : "Locomotion blend (M40-T4): the relaxed, ready and sprint runs blend the two neighbouring directions and speed bands by the damped ground velocity, phase-synced by the feet's plant times, " +
          $"cadence from the clips' measured strides (feet do not slide); " +
          (NoWos ? $"idle share below {IdleBlendSpeed:F1} m/s (--no-wos)" : $"the walk-on-the-spot clip is the relaxed run's zero-speed band (animation pass 1), the idle share ends at {WosIdleBlendSpeed:F2} m/s") + "; weights, damping and sync: interpretation.";
}
