using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// The sprint's connecting clips (animation pass 3; the owner, 2026-10-29: "a 'slowdown' when you stop sprinting where an animation plays of the character model slowing down from the
/// sprint back to a stop, if movement keys are let go of entirely", "we're missing 'connecting' animations"). Read from the sprint graphs (<see cref="SprintTransitions"/>,
/// <c>smtool graph-dump --sprint-edges</c>); the same for the own marine and remote ones. The decisions are engine-free and tested (<see cref="SprintStopTracker"/>,
/// <see cref="SprintStartTracker"/>); this file feeds them and poses the clips.
/// <list type="bullet">
/// <item><b>Sprint stop</b>. The graph leaves the loop for "Sprint to Idle" when the sprint is let go with the stick at rest, over '8 Frame Blend' 0.267 s: <c>SM_NAV_Bullrush2Idle_F_01</c>
/// (rifles), <c>SM_1HR_NAV_Sprint_to_Idle</c> (pistols), <c>SM_NAV_TB_Sprint_to_Idle</c> (heavy bolter), <c>SM_NAV_LC_Sprint_to_Idle</c> (lascannon, plasma cannon). It starts with the
/// simulation's own stop (<c>PlayerState.SprintStop</c>, every movement key let go after a sprint: the body slows over <c>SimConfig.SprintStopTime</c> 0.833 s), in every record so for seen
/// marines too; without the flag (a config with the stop off) a marine slowing hard below the run speed soon after a sprint is read as stopping. The clip's root motion is in place
/// (extracted), so its pose follows the body's speed (<see cref="SprintEdgeClip.TimeAtSlowingSpeed"/>); from rest it plays on to its branch or the end of its root motion, then the
/// navigation takes over with the base graph's blend at that branch (rifle and plasma cannon 0.333 s, pistol and heavy bolter 0.1 s). It ends early when an action takes the body (it does
/// not resume later), the weapon comes up (a shot), the marine aims or leaves the ground, or the simulation's stop is cancelled with the body still moving.</item>
/// <item><b>Sprint start</b> (the rifle and pistol graphs; the heavy graphs start straight into the loop). The entry state plays <c>SM_NAV_Bullrush_Start_F_01</c> (0.933 s, 4.5 m of root
/// motion) up to <c>BRANCH_Idle2SprintLoop</c> (its end) and blends into the loop over 0.1 s. Here: a sprint shown soon after standing shows the start clip driven by the distance the body has
/// gone since, then the loop from the foot the clip ends on.</item>
/// <item><b>Leaving the sprint</b> with the stick held: the base graph's blend out of the sprint state (rifle and plasma cannon 'Standard Blend' 0.333 s, pistol and heavy bolter 0.1 s).</item>
/// <item><b>The pistol's relaxed idle</b> (the owner, 2026-10-29: "the assault marine and raptor have a weird idle animation"): with the bolt pistol in hand and the weapon down, <c>SM_Pistol_NAV</c>'s
/// "Idle Relaxed" plays <c>SM_NAV_IdleL_CS_01</c>; until now every marine without the rifle's turn data stood in the rifle's relaxed idle (<c>SM_NAV_IdleL_S_01</c>).</item>
/// </list>
/// <c>--no-sprint-stop</c>, <c>--no-sprint-start</c> (test aids) and <c>--no-p3</c> switch them off.
/// </summary>
public partial class NetworkView
{
    static readonly bool NoSprintStop = CharacterViewer.NoP3 || OS.GetCmdlineUserArgs().Contains("--no-sprint-stop");
    static readonly bool NoSprintStart = CharacterViewer.NoP3 || OS.GetCmdlineUserArgs().Contains("--no-sprint-start");
    static readonly bool LogEdges = OS.GetCmdlineUserArgs().Contains("--log-sprint-edges");

    Task<SprintTransitions>? _edgeLoad;
    SprintTransitions? _edgeData;
    string _edgeNote = "not loaded";
    readonly Dictionary<int, SprintEdgeTrack> _edges = new();
    readonly HashSet<string> _edgeClipsInRig = new(StringComparer.OrdinalIgnoreCase);   // the edge clips the rig has, resolved once (review S4)
    string? _pistolIdle;                                                                // the pistol's relaxed idle when the rig has it, resolved once
    int _sprintStops, _sprintStopsDone, _sprintStarts;
    long _frameNo = -1;   // this frame's number, read once per frame in _Process (the staleness of the sprint stop and start)

    sealed class SprintEdgeTrack
    {
        public readonly SprintStopTracker Stop = new();
        public readonly SprintStartTracker Start = new();
        public SprintEdgeClip? StopClip;
        public bool SprintLast;                              // the last placement showed the sprint loop (or its start)
        public SprintFamily Family;
        public float LeaveFade;                              // the blend the next placement's stance change gets (the stop's end, an action's end)
        public float LastX, LastY; public bool HavePos; public double PosAt;
        public float Decel, SpeedThen; public double SpeedAt = double.NegativeInfinity;   // the drawn deceleration over the last ~0.1 s
    }

    /// <summary>Starts reading the sprint edges on a worker thread (the behaviour graphs; slow).</summary>
    void SetUpSprintEdges()
    {
        if (CharacterViewer.NoP3 || Lean) { _edgeNote = Lean ? "not loaded (lean bot)" : "switched off (--no-p3)"; return; }   // perf/lean-bots: poses only
        string gameDir = _gameDir;
        _edgeNote = "loading";
        _edgeLoad = Task.Run(() => SprintTransitions.Load(gameDir));
    }

    bool SprintEdgesLoading => _edgeLoad is not null && _edgeData is null && !_edgeLoad.IsCompleted;

    SprintTransitions? EdgeData()
    {
        if (_edgeData is not null) return _edgeData;
        if (_edgeLoad is not { IsCompleted: true } load) return null;
        _edgeLoad = null;
        if (load.IsFaulted || load.IsCanceled)
        {
            _edgeNote = "not available: " + (load.Exception?.GetBaseException().Message ?? "cancelled");
            GD.PrintErr("sprint edges: " + _edgeNote);
            return null;
        }
        _edgeData = load.Result;
        // Which clips the rig has, once (RigHasClip scans the rig's names).
        foreach (var set in _edgeData.Sets)
            foreach (var c in new[] { set.Start, set.Stop })
                if (c is not null && RigHasClip(c.Clip)) _edgeClipsInRig.Add(c.Clip);
        _pistolIdle = _edgeData.PistolRelaxedIdle is { } idle && RigHasClip(idle) ? idle : null;
        _edgeNote = string.Join(", ", _edgeData.Sets.Select(s => $"{s.Family}{(s.OneHanded ? " 1HM" : "")}: start {s.Start?.Clip ?? "none"}, stop {s.Stop?.Clip ?? "none"}, out {s.RunOnBlend:0.###}/{s.StopExitBlend:0.###} s"))
            + $"; pistol relaxed idle {_pistolIdle ?? "none"}";
        GD.Print("sprint edges: " + _edgeNote);
        foreach (var n in _edgeData.Notes) GD.Print("  sprint edges note: " + n);
        return _edgeData;
    }

    SprintEdgeTrack EdgeOf(int id)
    {
        if (!_edges.TryGetValue(id, out var e))
        {
            // Review S-8: players who left are forgotten with their remote (LocomotionForget); this also drops any other entry (an own id from before a reconnect) once the table outgrows the players.
            if (_edges.Count > _remotes.Count + 2)
                foreach (var gone in _edges.Keys.Where(k => k != id && k != _client?.PlayerId && !_remotes.ContainsKey(k)).ToList()) _edges.Remove(gone);
            _edges[id] = e = new SprintEdgeTrack();
        }
        return e;
    }

    /// <summary>The relaxed idle clip of a family: the pistol's own (<c>SM_Pistol_NAV</c> "Idle Relaxed"), else null (the caller's default, the rifle's).</summary>
    string? RelaxedIdleClip(HipFireWeaponClass family) =>
        family == HipFireWeaponClass.Pistol && !CharacterViewer.NoP3 && EdgeData() is not null ? _pistolIdle : null;

    /// <summary>The idle clip of the plain idle stance (no turn data for the posture): the pistol's relaxed idle with the pistol in hand and the weapon down, else the rig's idle.</summary>
    string? IdleClipOf(PlayerState s) =>
        (HammerIdleOf(s) ?? (!HipReady(s) && HipFamilyOf(s) is { } family ? RelaxedIdleClip(family) : null)) ?? _idleAnimation;   // anim/melee-flow: the hammer's own idle first

    /// <summary>The sprint edges of a marine's sprint family and hand (the pistol's loop in the rifle graph has its own stop).</summary>
    SprintEdges? EdgesOf(PlayerState s, SprintFamily family) =>
        EdgeData()?.For(family, family == SprintFamily.Rifle && HipFamilyOf(s) == HipFireWeaponClass.Pistol);

    /// <summary>A family's stop clip when the data is in and the rig has it (else null: the stop is not played and the sprint loop is not ended early for it).</summary>
    SprintEdgeClip? StopClipOf(PlayerState s, SprintFamily family) =>
        EdgesOf(s, family)?.Stop is { } stop && _edgeClipsInRig.Contains(stop.Clip) ? stop : null;

    /// <summary>
    /// Whether the sprint loop gives way to the simulation's stop at once (review S5: only when the stop will actually play: not switched off, the data in and the clip in the rig; else the loop
    /// keeps showing until the body slows below the sprint, as without the stop).
    /// </summary>
    bool SprintLoopEndsForStop(PlayerState s, SprintFamily family) =>
        !NoSprintStop && SimSprintStopping(s) && StopClipOf(s, family) is not null;

    /// <summary>
    /// The locomotion placement (called for a marine not in an action clip): the sprint stop when one plays, else the locomotion (<see cref="PlaceLocomotionCore"/>) with the blend of leaving
    /// the sprint, the stop or an action clip when the last placement showed them.
    /// </summary>
    void PlaceLocomotion(CharacterViewer character, PlayerState s, bool own)
    {
        if (CharacterViewer.NoP3 || (own && HipBlendActive)) { PlaceLocomotionCore(character, s, own); return; }
        var e = EdgeOf(s.PlayerId);
        TrackTravel(s, e);
        // The clip the last frame showed (an action clip ends here): the graphs' blend out of it, and a charge that ends arms the stop as a sprint does.
        if (character.StanceKey is null && character.Playing is { } left && !e.Stop.Active)
        {
            if (e.LeaveFade <= 0f) e.LeaveFade = MeleeExitBlendOf(s, left) ?? ExitBlendOf(left);   // anim/melee-flow: a melee strike's way out of its own graph (NetworkView.MeleeFlow.cs)
            if ((left.StartsWith("SM_BUL_", StringComparison.OrdinalIgnoreCase) || left.StartsWith("SM_TH_BUL_", StringComparison.OrdinalIgnoreCase)) && _config is not null)
            {
                e.Stop.ChargeEnded(_connectedFor);
                e.Family = Loadouts.InHand(_config, s)?.Name is { } weapon ? SprintFamilyFor(weapon) : SprintFamily.Rifle;
            }
        }
        if (SprintStopPlace(character, s, e)) { e.SprintLast = false; return; }
        float saved = _fadeOverride;
        bool wasSprint = e.SprintLast;
        e.SprintLast = false;
        if (saved <= 0f)
        {
            if (e.LeaveFade > 0f) _fadeOverride = e.LeaveFade;
            else if (wasSprint && EdgesOf(s, e.Family) is { } edges) _fadeOverride = edges.RunOnBlend;
        }
        try { PlaceLocomotionCore(character, s, own); }
        finally { _fadeOverride = saved; e.LeaveFade = 0f; }
    }

    /// <summary>
    /// The simulation's sprint stop (<c>SmSim.SprintStop</c>, <c>PlayerState.SprintStop</c>: set in every record, own and remote, while the body slows from a sprint with every movement
    /// key let go, over <c>SimConfig.SprintStopTime</c> 0.833 s). The stop pose has one owner: this file (the simulation's <c>SprintStop.ClipTime</c> is not used by the client).
    /// </summary>
    static bool SimSprintStopping(PlayerState s) => s.SprintStop;

    /// <summary>The sprint family of a weapon as shown (the shared rifle sprint with <c>--shared-sprint</c>).</summary>
    static SprintFamily SprintFamilyFor(string weapon) => SharedSprint ? SprintFamily.Rifle : LocomotionAnimations.SprintFamilyOf(weapon);

    /// <summary>
    /// The blend from an action clip back into the locomotion, by the clip's kind: an evade's 'Evade to Idle' / 'Evade to Run' 0.333 s (<c>SM_Base_MP</c>), the charge's
    /// 'BRANCH_RushAttack2Sprint' '10 Frame Blend' 0.333 s (the heavy sprint graphs; the rifle's charge leaves through its stop), the charge stops' the stop's 0.333 s; a melee strike's and a
    /// reaction's way out of their graphs carries no blend of its own (conduit states): <see cref="ActionExitBlend"/> (ours). 0 (the stance's own) for any other clip.
    /// </summary>
    static float ExitBlendOf(string clip)
    {
        if (clip.StartsWith("SM_EVD_", StringComparison.OrdinalIgnoreCase)) return EvadeExitBlend;
        if (clip.StartsWith("SM_BUL_", StringComparison.OrdinalIgnoreCase) || clip.StartsWith("SM_TH_BUL_", StringComparison.OrdinalIgnoreCase) || clip.Contains("Bullrush2Idle", StringComparison.OrdinalIgnoreCase)) return EvadeExitBlend;
        if (clip.StartsWith("SM_MEL_", StringComparison.OrdinalIgnoreCase) || clip.StartsWith("SM_REC_", StringComparison.OrdinalIgnoreCase)) return ActionExitBlend;
        return 0f;
    }

    /// <summary>'Evade to Idle' / 'Evade to Run' / '10 Frame Blend' / 'Standard Blend': 10 frames at 30 fps.</summary>
    const float EvadeExitBlend = 10f / 30f;
    /// <summary>A melee strike's or a reaction's blend back into the locomotion (s; ours: the graphs' conduit states carry none).</summary>
    const float ActionExitBlend = 0.2f;

    /// <summary>Called by <see cref="SprintPlace"/> each frame it shows the sprint loop: the stop's arming and the family it will play.</summary>
    void SprintShown(PlayerState s, SprintFamily family)
    {
        var e = EdgeOf(s.PlayerId);
        e.Stop.SprintShown(_connectedFor); e.SprintLast = true; e.Family = family;
    }

    /// <summary>The body's motion for the trackers: the distance gone forward (the drawn position along the facing), the deceleration over the last ~0.1 s.</summary>
    void TrackTravel(PlayerState s, SprintEdgeTrack e)
    {
        float speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
        float yaw = BodyYawOf(s);
        float fx = -MathF.Sin(yaw), fy = MathF.Cos(yaw);
        bool fresh = e.HavePos && _connectedFor - e.PosAt < 0.25;
        float moved = fresh ? (s.Position.X - e.LastX) * fx + (s.Position.Y - e.LastY) * fy : 0f;
        if (MathF.Abs(moved) > 2f) moved = 0f;   // a respawn or a correction
        e.LastX = s.Position.X; e.LastY = s.Position.Y; e.HavePos = true; e.PosAt = _connectedFor;
        double span = _connectedFor - e.SpeedAt;
        if (span >= 0.1 || span < 0) { e.Decel = span > 0 && span < 0.5 ? (float)((e.SpeedThen - speed) / span) : 0f; e.SpeedThen = speed; e.SpeedAt = _connectedFor; }
        e.Start.Track(_connectedFor, speed, s.Grounded, moved, _frameNo);
    }

    bool SprintStopPlace(CharacterViewer character, PlayerState s, SprintEdgeTrack e)
    {
        if (NoSprintStop || _config is null) return false;
        // Review S-1: no stop under way and none can start (the sprint was not shown within the arming time): nothing to do (no lookups for the many marines that are not stopping).
        if (!e.Stop.Active && !e.Stop.CanArm(_connectedFor)) return false;
        float speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
        float yaw = BodyYawOf(s);
        float forward = s.Velocity.X * -MathF.Sin(yaw) + s.Velocity.Y * MathF.Cos(yaw);
        bool eligible = s.Alive && s.Grounded && !s.Aiming && !TurnInAction(s, null) && !(_loco.TryGetValue(s.PlayerId, out var lt) && lt.Fall.Falling);
        bool wasActive = e.Stop.Active;
        if (!wasActive) e.StopClip = StopClipOf(s, e.Family);
        var frame = new StopFrame(_connectedFor, eligible, HipReady(s), SimSprintStopping(s), speed, forward, e.Decel, _frameNo);
        var ended = e.Stop.Update(frame, e.StopClip, _config.RunSpeed);
        if (ended != StopEnd.None)
        {
            if (ended == StopEnd.Done) _sprintStopsDone++;
            var edges = EdgesOf(s, e.Family);
            e.LeaveFade = ended == StopEnd.Done ? edges?.StopExitBlend ?? ClipBlends.Default : edges?.RunOnBlend ?? ClipBlends.Default;
            EdgeLog(s, $"stop ended: {ended} (speed {speed:F1} m/s)");
            return false;
        }
        if (!e.Stop.Active || e.StopClip is not { } clip) return false;
        if (!wasActive) { _sprintStops++; EdgeLog(s, $"stop begins: {clip.Clip} at {e.Stop.Time:F2} s (speed {speed:F1} m/s{(frame.SimStopping ? ", the simulation's stop" : "")})"); }
        if (!character.Pose(clip.Clip, e.Stop.Time, clip.BlendIn)) { e.Stop.Drop(); return false; }
        var hip = HipOf(s.PlayerId);
        hip.Kind = PoseKind.Normal;
        character.CancelSettle();   // the clip has the feet
        return true;
    }

    /// <summary>
    /// Called by <see cref="SprintPlace"/> before it shows the loop: the start clip, driven by the distance the body has gone, when the sprint began from standing. True when the start shows.
    /// <paramref name="seedFraction"/> is set when the start has just ended: the loop's phase at the foot the start ended on.
    /// </summary>
    bool SprintStartPlace(CharacterViewer character, PlayerState s, SprintFamily family, out float? seedFraction)
    {
        seedFraction = null;
        var e = EdgeOf(s.PlayerId);
        if (NoSprintStart) return false;
        bool was = e.Start.Active;
        var clip = EdgesOf(s, family)?.Start is { } start && _edgeClipsInRig.Contains(start.Clip) ? start : null;
        if (!e.Start.Place(_connectedFor, clip, out seedFraction, _frameNo)) { if (was) EdgeLog(s, "start done: the loop takes over"); return false; }
        if (!was) { _sprintStarts++; EdgeLog(s, $"start begins: {clip!.Clip} at {e.Start.Time:F2} s (travel {e.Start.Travel:F2} m)"); }
        if (!character.Pose(clip!.Clip, e.Start.Time, clip.BlendIn)) { e.Start.Drop(); return false; }
        e.Stop.SprintShown(_connectedFor); e.SprintLast = true; e.Family = family;
        return true;
    }

    /// <summary>Whether a sprint start is under way for the player (it holds the sprint while the body is still at the run's start).</summary>
    bool SprintStartActive(int playerId) => _edges.TryGetValue(playerId, out var e) && e.Start.Active;

    void EdgeLog(PlayerState s, string what)
    {
        if (LogEdges || (_bot && s.PlayerId == _client?.PlayerId)) GD.Print($"t {_connectedFor:F2} s: P{s.PlayerId} SPRINT EDGE {what}");
    }

    /// <summary>The bot log's counters (printed with the other bot status lines).</summary>
    string SprintEdgeStats() => $"sprint edges: starts {_sprintStarts}, stops {_sprintStops} ({_sprintStopsDone} played to the end)";
}
