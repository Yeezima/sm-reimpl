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
/// The fall pose and the landing in the networked view (animation pass 1; the owner's playtest of 2026-10-28: "in sm1 when a player walks off a ledge he drops ... with a falling animation").
/// Until now a marine that left the ground kept its run or idle pose for the whole fall and snapped on landing. Now, for every player without a jump pack in use (the pack has its own
/// poses, <c>NetworkView.Jump.cs</c>), read from the navigation graphs (<see cref="FallAnimations"/>, <c>smtool graph-dump --falls</c>):
/// <list type="bullet">
/// <item><b>Falling</b> (not supported by the ground for <see cref="FallRules.DebounceSeconds"/>; ours: the graph reacts at once but a crest of stairs or a ramp is unsupported for a tick or two): the
/// base pose is the standing aim-offset idle (the horizontal velocity is not shown: the graph's "Dropping" state has no direction or speed input; M40-T5's idle path) and the legs of
/// <c>SM_ACT_B_button_drop_fall_01</c> are laid over it on the 14 leg bones (<see cref="PoseSlots.Fall"/>), faded in over the graph's 'To Fall' 0.167 s. Whatever horizontal speed the simulation keeps in the air, the
/// pose does not show it.</item>
/// <item><b>Landing</b>: a fall that reached 20 m/s downward (<see cref="FallSet.HardFallSpeed"/>, the graph's <c>FLT_Fall_Speed</c> test) plays <c>SM_ACT_B_button_drop_land_01</c> on the whole body
/// (<see cref="PoseSlots.Land"/>, 0.1 s blend in) until the graph's <c>BRANCH_To_Run_MP</c> (0.167 s into the clip; every class has <c>mp_fall_protection</c>, so the 0.635 s exit does not occur) and out over 0.267 s; a softer
/// fall lets the legs go over 0.1 s. A marine that moves while the crouch plays keeps less of it (<see cref="FallRules.MoveKeep"/>, ours). The heavy bolter has its own pair, whole-body: <c>SM_NAV_TB_Dropdown</c>
/// from 1 m above the ground and <c>SM_NAV_TB_Landing</c> from 0.5 m, at any speed.</item>
/// <item><b>Feel</b> (the own player): the player blueprint's <c>finish_falling_actions</c>: a camera shake (<see cref="CameraShakeSpec"/>) and the <c>jump_land</c> effect at scale 0.5, for a fall of
/// at least <see cref="FallRules.LandingFeelSeconds"/> in the air (the sound's threshold). The landing sound is the sound system's (<c>NetworkView.Sound.cs</c>), unchanged.</item>
/// </list>
/// Which weapon family's fall a marine gets (the rifle's, the heavy bolter's; the pistol and the melee weapons have no fall of their own in the data, the rifle's is used) is read from the weapon in hand when the fall
/// starts. All of it is presentation: the simulation is not touched. <c>--no-fall-pose</c> (test aid) switches it off, <c>--log-fall</c> prints the events, <c>--fall-hard-speed &lt;m/s&gt;</c> lowers the hard-landing speed for tests.
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--no-fall-pose</c> (test aid): the old behaviour, the run or idle pose for the whole fall.</summary>
    static readonly bool NoFallPose = OS.GetCmdlineUserArgs().Contains("--no-fall-pose");
    /// <summary><c>--log-fall</c> (test aid, bots log it anyway): prints the fall and landing events of every player seen.</summary>
    static readonly bool LogFall = OS.GetCmdlineUserArgs().Contains("--log-fall");
    /// <summary><c>--fall-hard-speed &lt;m/s&gt;</c> (test aid): the downward speed from which a rifle fall plays the landing clip, instead of the graph's 20 (the simulation's gravity reaches 20 m/s after a 10 m drop).</summary>
    static readonly float? FallHardSpeedOverride = FloatArg("fall-hard-speed");

    /// <summary><c>--wait-fall</c> / <c>--wait-fall-land</c> (screenshot aids, with <c>--bot-gait --bot-ledge</c>): the screenshot waits for the own marine to be 0.35 s into a fall / 0.1 s into the hard landing clip.</summary>
    static readonly bool WaitFall = OS.GetCmdlineUserArgs().Contains("--wait-fall"), WaitFallLand = OS.GetCmdlineUserArgs().Contains("--wait-fall-land");

    bool FallWorthCapturing
    {
        get
        {
            if (!WaitFall && !WaitFallLand) return true;
            if (_client is null || !_loco.TryGetValue(_client.PlayerId, out var t)) return false;
            return WaitFallLand ? t.Fall.LandHard && _connectedFor - t.Fall.LandAt is >= 0.1 and <= 0.25 : t.Fall.Falling && _connectedFor - t.Fall.FallSince >= 0.35;
        }
    }

    Task<FallAnimations>? _fallLoad;
    FallAnimations? _fallData;
    string _fallNote = "not loaded";
    CameraShakeSpec? _landShake;
    double _landShakeAt;
    int _fallsBegun, _landingsSoft, _landingsHard, _landingFeels;
    readonly Dictionary<int, bool> _fallProtected = new();

    /// <summary>Starts reading the fall data on a worker thread (the behaviour graphs: slow, never on the main thread).</summary>
    void SetUpFall()
    {
        if (NoFallPose || Lean) { _fallNote = Lean ? "not loaded (lean bot)" : "switched off (--no-fall-pose)"; return; }   // perf/lean-bots: poses only
        string gameDir = _gameDir;
        _fallNote = "loading";
        _fallLoad = Task.Run(() => FallAnimations.Load(gameDir));
    }

    FallAnimations? FallData()
    {
        if (_fallData is not null) return _fallData;
        if (_fallLoad is not { IsCompleted: true } load) return null;
        _fallLoad = null;
        if (load.IsFaulted || load.IsCanceled)
        {
            _fallNote = "not available: " + (load.Exception?.GetBaseException().Message ?? "cancelled");
            GD.PrintErr("fall: " + _fallNote);
            return null;
        }
        _fallData = load.Result;
        _fallNote = string.Join(", ", _fallData.Sets.Select(s => $"{s.Family} {(s.Legs ?? s.Drop)?.Clip} / {s.Land.Clip} (hard from {(s.HardFallSpeed > 0f ? $"{s.HardFallSpeed:0.#} m/s" : "any fall")}, exit {s.ProtectedExit.At:0.###} s protected)"))
            + $"; fall protection: {string.Join(", ", _fallData.ProtectedWargear.Order(StringComparer.Ordinal))}";
        GD.Print("fall: " + _fallNote);
        foreach (var n in _fallData.Notes) GD.Print("  fall note: " + n);
        return _fallData;
    }

    /// <summary>The fall of the family the marine's weapon belongs to (the heavy bolter's, else the rifle's).</summary>
    FallSet? FallSetOf(PlayerState s)
    {
        if (FallData() is not { } data) return null;
        var family = HipFamilyOf(s) == HipFireWeaponClass.Heavy ? FallFamily.Heavy : FallFamily.Rifle;
        return data.For(family) ?? data.For(FallFamily.Rifle);
    }

    /// <summary>Whether the marine's class has <c>mp_fall_protection</c> (the wargear whose lifetime actions apply it: the three core evade abilities, so all of them) and so leaves the landing at the short exit.</summary>
    bool FallProtectedClass(PlayerState s)
    {
        if (_fallData is not { } data || _classes is not { } classes || s.Class < 0 || s.Class >= classes.Classes.Count) return false;
        if (_fallProtected.TryGetValue(s.Class, out var known)) return known;
        var mp = classes.Classes[s.Class];
        bool protectedClass = mp.BaseWargear.Concat(mp.StartupWargear).Any(data.ProtectedWargear.Contains);
        _fallProtected[s.Class] = protectedClass;
        return protectedClass;
    }

    /// <summary>Metres from the marine's feet to the level under it (a ray down from 0.6 m above them), 100 when there is nothing below within 100 m or no map.</summary>
    float HeightAboveGround(PlayerState s)
    {
        if (_client?.Collision is not { } world) return 0f;
        return world.Character.Raycast(s.Position + new System.Numerics.Vector3(0f, 0f, 0.6f), -System.Numerics.Vector3.UnitZ, 100f) is { } hit ? Math.Max(0f, hit.Distance - 0.6f) : 100f;
    }

    /// <summary>
    /// Whether the marine's fall layers are still on it: the hard landing's clip, or the fall layer fading out after the touchdown. Its body is not planted and no foot is pinned meanwhile
    /// (<see cref="TurnInAction"/>): a settle begun now would pin the toes where the fall legs dangle them, below the ground.
    /// </summary>
    bool FallLanding(int playerId) =>
        _loco.TryGetValue(playerId, out var t) && (t.LegsWeight > 1e-3f || (t.FallSet is { } set && t.Fall.LandingActive(_connectedFor, t.LandProtected ? set.ProtectedExit : set.Exit)));

    /// <summary>
    /// Once per placed frame, before the body is turned: feeds the marine's <see cref="FallTracker"/> (the rules are in it: debounce, peak speed, landing, gap reset) and acts on what it reports.
    /// Eligible: alive, no jump-pack phase or client jump view, not rolling, charging or reacting.
    /// </summary>
    void FallObserve(PlayerState s, LocoTrack t, bool own)
    {
        double now = _connectedFor;
        if (NoFallPose || _config is null || FallData() is null) { t.Fall.Observe(now, false, true, 0f); return; }
        // sim/combat-pass (item 21): a charge held in the air keeps its clip until it has run FallRules.ChargeFallDelay (0.3 s off the ground), then the fall pose.
        bool heldInAir = ChargeHeldInAir(s) || EvadeFallingInAir(s);   // sim/movement-fixes: a roll or hop carried off a drop too
        if (!heldInAir) { t.ChargeAirSince = double.NegativeInfinity; t.ChargeFall = false; }
        else
        {
            if (double.IsNegativeInfinity(t.ChargeAirSince)) t.ChargeAirSince = now;
            t.ChargeFall = t.ChargeFall || FallRules.ChargeShowsFall(now - t.ChargeAirSince);
        }
        bool eligible = s.Alive && s.Jump == JumpPhase.None && (s.Rush == RushPhase.None || t.ChargeFall) && s.Reaction == ReactionKind.None && (!Evade.Evading(s) || t.ChargeFall)
                        && !(_jumpTracks.TryGetValue(s.PlayerId, out var jump) && jump.View != JumpView.None);
        var fall = t.Fall;
        // Animation pass 3: a charge held in the air off a ledge is a fall from its first frame (no debounce: a crest of stairs does not hold a charge), so its clip hands over to the fall pose at once.
        var seen = fall.Observe(now, eligible, s.Grounded, s.Velocity.Z, immediate: !CharacterViewer.NoP3 && t.ChargeFall);
        if (fall.WasReset) t.LegsWeight = 0f;
        switch (seen)
        {
            case FallEvent.Began: BeginFall(s, t, own); break;
            case FallEvent.Landed: Landed(s, t, own); break;
            case FallEvent.Aborted: t.LegsWeight = 0f; break;   // died, or the pack / a roll / a reaction took over: nothing to fade
        }
        if (fall.Falling && t.FallSet is { Family: FallFamily.Heavy } heavy) HeavyDropStep(s, t, heavy, own);
    }

    void BeginFall(PlayerState s, LocoTrack t, bool own)
    {
        t.FallSet = FallSetOf(s);
        t.LandKeep = 1f;
        _fallsBegun++;
        if (_bot || LogFall) GD.Print($"t {_connectedFor:F2} s: P{s.PlayerId} FALL begins ({t.FallSet?.Family.ToString() ?? "no data"}; unsupported {(_connectedFor - t.Fall.AirSince) * 1000:F0} ms, vertical {s.Velocity.Z:F1} m/s, horizontal {MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y):F1} m/s, {(own ? "own" : "seen")}{(t.ChargeFall ? $"; a charge held in the air ({s.Rush}) for {FallRules.ChargeFallDelay:0.#} s: the fall pose, not its clip" : "")})");
    }

    /// <summary>The heavy bolter's states (<see cref="FallTracker.HeavyStep"/>): the drop clip from <see cref="FallSet.EnterHeight"/> above the ground, the landing clip from <see cref="FallSet.LandHeight"/> (reached before touchdown) or at touchdown.</summary>
    void HeavyDropStep(PlayerState s, LocoTrack t, FallSet set, bool own)
    {
        if (t.Fall.HeavyLanded) return;
        float height = HeightAboveGround(s);
        if (t.Fall.HeavyStep(_connectedFor, height, set.EnterHeight, set.LandHeight)) BeginLanding(s, t, set, hard: true, own, height);
    }

    void Landed(PlayerState s, LocoTrack t, bool own)
    {
        var set = t.FallSet;
        var fall = t.Fall;
        double airTime = fall.LastAirTime;
        float threshold = set is null ? 0f : FallHardSpeedOverride is { } over && set.Family == FallFamily.Rifle ? over : set.HardFallSpeed;
        // The heavy bolter's landing needs the drop (1 m above the ground) and begins at 0.5 m, usually before touchdown; the rifle's needs the speed.
        bool hard = set is not null && (set.Family == FallFamily.Heavy ? fall.DropStarted : FallRules.IsHard(fall.PeakDown, threshold));
        if (set is not null && !fall.HeavyLanded) BeginLanding(s, t, set, hard, own, 0f);
        // The same air-time threshold as the landing sound (NetworkView.Sound.cs): FallRules.LandingFeelSeconds.
        if (own && airTime >= FallRules.LandingFeelSeconds) LandingFeel(s);
        if (!hard) _landingsSoft++;
        if (_bot || LogFall)
            GD.Print($"t {_connectedFor:F2} s: P{s.PlayerId} LAND after {airTime:F2} s in the air, peak {fall.PeakDown:F1} m/s down (hard from {threshold:0.#}): {(hard ? "hard landing clip" : "soft: legs fade out")}{(t.LandProtected ? ", fall protection" : "")} ({(own ? "own" : "seen")})");
    }

    void BeginLanding(PlayerState s, LocoTrack t, FallSet set, bool hard, bool own, float height)
    {
        t.Fall.BeginLanding(_connectedFor, hard);
        t.LandProtected = FallProtectedClass(s);
        if (set.Family == FallFamily.Heavy) t.Fall.MarkHeavyDone();
        if (hard) _landingsHard++;
        if (set.Family == FallFamily.Heavy && (_bot || LogFall))
            GD.Print($"t {_connectedFor:F2} s: P{s.PlayerId} heavy LANDING clip begins at {height:F2} m above the ground ({(own ? "own" : "seen")})");
    }

    /// <summary>The own player's landing feel from the blueprint's <c>finish_falling_actions</c>: the camera shake and the effect at its feet (the sound is the sound system's).</summary>
    void LandingFeel(PlayerState s)
    {
        if (_catalogue?.FinishFalling is not { } landing) return;
        _landingFeels++;
        if (landing.Shake is { } shake) { _landShake = shake; _landShakeAt = _connectedFor; }
        if (landing.Plays.Count > 0 && FxActive && _fx is not null)
        {
            var (point, normal, surface) = GroundUnder(s.Position);
            int started = GroundPoundFx.Play(_fx, landing.Plays, point, normal, 0f, null, _catalogue!.SurfaceName(surface), 0).Count;
            if (_bot || LogFall) GD.Print($"t {_connectedFor:F2} s: landing feel: {started} effect(s) ({string.Join(", ", landing.Plays.Select(p => $"{p.Name} x{p.Scale:0.##}"))}), shake {(landing.Shake is null ? "none" : $"{landing.Shake.Total:0.##} s")}");
        }
    }

    /// <summary>
    /// The state the locomotion poses a falling marine from: no horizontal velocity, so the standing pose (the aim-offset idle, the aim stance, the ready posture) shows whatever speed the simulation keeps in the air.
    /// </summary>
    PlayerState FallBaseState(PlayerState s, LocoTrack t) =>
        t.Fall.Falling ? s with { Velocity = new System.Numerics.Vector3(0f, 0f, s.Velocity.Z) } : s;

    /// <summary>
    /// After the locomotion pose is placed: lays the fall's layers over it: the legs (or the heavy bolter's drop clip) while falling, fading out after the touchdown, and the landing clip with its envelope.
    /// </summary>
    void FallLayers(CharacterViewer character, PlayerState s, LocoTrack t)
    {
        if (NoFallPose || _fallData is null) return;
        double now = _connectedFor;
        float dt = double.IsFinite(t.FallLayerAt) ? (float)Math.Clamp(now - t.FallLayerAt, 0.0, 0.1) : 0f;
        // anim/melee-flow: not placed here for a while (an action clip drove the pose: a charge going off a ledge falls from its first unsupported frame while its clip still shows):
        // the legs ramp from now instead of taking the whole gap at once (they came in at full weight in one frame: the root turned 90 degrees, 168 cm per frame per frame).
        if (!NoMeleeFlow && double.IsFinite(t.FallLayerAt) && now - t.FallLayerAt > 0.05) dt = 0f;
        t.FallLayerAt = now;
        if (t.FallSet is not { } set || !s.Alive) { t.LegsWeight = 0f; return; }
        // The falling pose: the legs on the leg mask, or the heavy bolter's whole-body drop; they go out over the blend of whatever follows (the landing's, or the soft exit's).
        bool on = t.Fall.Falling && (set.Legs is not null || (t.Fall.DropStarted && !t.Fall.HeavyLanded));
        float blendIn = Math.Max(0.02f, set.EnterBlend), blendOut = Math.Max(0.02f, t.Fall.LandHard ? set.LandBlend : set.SoftExitBlend);
        t.LegsWeight = Mathf.MoveToward(t.LegsWeight, on ? 1f : 0f, dt / (on ? blendIn : blendOut));
        if (t.LegsWeight > 1e-3f)
        {
            // Animation pass 3: the weight on the smooth blend curve (CharacterViewer.Smooth), so the legs ease in and out instead of starting and stopping at full rate.
            if (set.Legs is { } legs) character.SetLayer(PoseSlots.Fall, legs.Clip, (float)((now - t.Fall.FallSince) % Math.Max(legs.Length, 1e-3f)), CharacterViewer.Smooth(t.LegsWeight), false, legs.Mask);
            else if (set.Drop is { } drop) character.SetLayer(PoseSlots.Fall, drop.Clip, (float)(now - t.Fall.DropSince), CharacterViewer.Smooth(t.LegsWeight), false, null);
        }
        // The landing clip: blends in, holds to the exit branch, blends out; a marine on the move keeps less of it.
        if (t.Fall.LandHard)
        {
            var exit = t.LandProtected ? set.ProtectedExit : set.Exit;
            float since = (float)(now - t.Fall.LandAt);
            if (FallRules.LandOver(since, exit)) { t.Fall.EndLanding(); return; }
            float speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
            t.LandKeep = Mathf.MoveToward(t.LandKeep, FallRules.MoveKeep(speed, _config?.MoveSpeedScale ?? 1f), dt / LandKeepSeconds);
            float weight = FallRules.LandWeight(since, set.LandBlend, exit) * t.LandKeep;
            if (weight > 1e-3f) character.SetLayer(PoseSlots.Land, set.Land.Clip, since, CharacterViewer.Smooth(weight), false, null);   // animation pass 3: the smooth curve
        }
        if ((LogFall || _bot) && character.ActiveLayers > 0 && now - t.FallLogAt >= 0.1)
        {
            t.FallLogAt = now;
            var legsState = character.LayerState(PoseSlots.Fall);
            var landState = character.LayerState(PoseSlots.Land);
            GD.Print($"t {now:F2} s: P{s.PlayerId} FALLPOSE falling {t.Fall.Falling}; fall layer {(legsState is { } l ? $"{l.Clip} t={l.Time:F2} w={l.Weight:F2}" : "-")}; land layer {(landState is { } d ? $"{d.Clip} t={d.Time:F2} w={d.Weight:F2}" : "-")}; stance {character.StanceKey ?? "-"}; v {s.Velocity.X:F1},{s.Velocity.Y:F1},{s.Velocity.Z:F1}");
        }
    }

    /// <summary>How fast the landing clip's share follows the marine's speed (seconds for a full change; ours).</summary>
    const float LandKeepSeconds = 0.15f;

    /// <summary>The camera offset of the landing's shake this frame (right, up; metres), or zero.</summary>
    Vector3 LandShakeOffset()
    {
        if (_landShake is not { } shake) return Vector3.Zero;
        float age = (float)(_connectedFor - _landShakeAt);
        if (age > shake.Total) { _landShake = null; return Vector3.Zero; }
        var (h, v) = shake.OffsetAt(age);
        return new Vector3(h, v, 0f);
    }

    /// <summary>
    /// feel/jump-pack (the owner, 2026-10-29: "use the falling animation for when a player has charged off a ledge and is 'accelerating/gliding'"): a bull rush or a dash
    /// attack's lunge held in the air (<see cref="Rush.Held"/>: off a ledge, its clip time waiting until it lands) shows the fall pose instead of its clip frozen at the time it
    /// left the ground; on landing the clip resumes as before. The simulation is not touched. <c>--no-fall-pose</c> keeps the old frozen clip.
    /// </summary>
    bool ChargeHeldInAir(PlayerState s) =>
        !NoFallPose && s.Alive && !s.Grounded && s.Rush is RushPhase.Rush or RushPhase.DashAttack && _config is not null && Rush.MoveOf(_config, s) is { } move && Rush.Held(s, move, _config);

    /// <summary>
    /// sim/movement-fixes (the owner, 2026-11-01: the hop off a ledge "should go down while still going forward"): a roll or heavy hop carried off a drop goes on in the
    /// air with its clip's travel (the simulation) and falls; past its own lift (<see cref="EvadeMove.HeightAt"/> 0) and on the way down it shows the fall pose once it has been
    /// off the ground <see cref="FallRules.ChargeFallDelay"/> (as a charge held in the air), so a long drop is not its clip's standing frames in mid-air; on landing the
    /// clip resumes. Not the jump-pack dash (its flight clip is its own fall). <c>--no-fall-pose</c> keeps the clip.
    /// </summary>
    bool EvadeFallingInAir(PlayerState s) =>
        !NoFallPose && s.Alive && !s.Grounded && s.Velocity.Z < 0f && _config is not null && Evade.Move(s, _config) is { Flight: false } move
        && !(move.HeightAt(Evade.Time(s, _config)) > 0f);

    /// <summary>The bot log's fall counters.</summary>
    string FallStats() => $"falls: {_fallsBegun} begun, landings soft {_landingsSoft} hard {_landingsHard}, own landing feel {_landingFeels}";

    /// <summary>The F1 line about the fall.</summary>
    string FallInfoLine() =>
        NoFallPose ? "Fall pose: off (--no-fall-pose)."
        : "Fall pose (animation pass 1): a marine unsupported for 2 ticks falls: the standing aim-offset idle with the legs of the graph's fall clip over it (SM_ACT_B_button_drop_fall_01), the hard-landing clip from 20 m/s down (heavy bolter: its own drop and landing), " +
          $"the blueprint's landing shake and effect for the own player; the horizontal speed is not shown in the air; debounce, the moving marine's share of the crouch and the pistol's use of the rifle fall: ours. Loaded: {_fallNote}.";
}
