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
/// Turn in place and the aim-offset idle in the networked view (M40-T5; the M40 audit's D8: "turning in place: the body spins rigidly; the idle ignores the aim
/// pitch"). Until now a marine's body took the view's yaw at once and its idle was one clip. Now a standing marine in a posture with the graph's turn data
/// (<see cref="TurnAnimations"/>: the rifle relaxed and ready, the pistol and the heavy bolter ready) keeps its lower body planted while the aim moves: the
/// upper body follows it through the graph's aim-offset idle (left / ahead / right clips at -90 / 0 / +90 degrees times the pitch clips, <see cref="AimOffsetGrid"/>;
/// the pistol and the heavy bolter have the pitch clips and a twist modifier, done here as a spine twist), and when the aim is more than the threshold off the body
/// (90 degrees; 75 for the pistol and the heavy bolter) <c>SM_NAV_AimTurn_Left</c> / <c>_Right</c> plays on the pelvis and legs while the body turns with the
/// clip's root motion (<see cref="BodyYaw"/>, 135 degrees over 0.833 s, ended by <c>BRANCH_Turn_End</c> at 0.92). A marine that moves, attacks, aims (Left Ctrl),
/// jumps or reacts has its body follow the aim at most <see cref="BodyFollowRate"/> radians a second (ours: the audit asked for at most 15 degrees of body yaw per frame).
/// The same for the own marine and for remote ones; the simulation, the hit volumes, the shots and the camera keep the view's yaw (owner's decision pending: the
/// shown body may lag the aim). <c>--no-turns</c> (test aid) restores the old rigid body and single idle for before / after runs.
/// <para>
/// <b>Not applied.</b> The graphs also send a marine into a rigid "Idle Spin" while the requested facing change is 100 degrees a second or more (150 for the heavy bolter;
/// <c>FLT_Facing_Change_Request_DegSec</c>, not decoded); here the lag and turn rules apply at every turning speed. The slope idles (<c>SM_NAV_IdleL_S_Slope*</c>) need the ground
/// slope and are not used.
/// </para>
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--no-turns</c> (test aid, M40-T5): the body takes the view's yaw at once and the idle is one clip, as before.</summary>
    static readonly bool NoTurns = OS.GetCmdlineUserArgs().Contains("--no-turns");

    /// <summary>The largest angle between the aim and a standing body (radians; ours): a view that outruns the turn drags the body. The turn clip's own root rotation is 135 degrees.</summary>
    const float BodyMaxTwist = 135f * MathF.PI / 180f;
    /// <summary>The fastest a body follows the aim when it is not planted (radians per second; ours): 720 degrees a second is 12 degrees a frame at 60 fps, under the audit's 15.</summary>
    const float BodyFollowRate = 720f * MathF.PI / 180f;
    /// <summary>The aim-offset blend's horizontal range (degrees, the graph's <c>FLT_Aim_Horizontal_Deg_CLAMPED</c> reaches the same): beyond it the upper body keeps the end pose.</summary>
    const float AimOffsetRange = 90f;
    /// <summary>The foot pins' hold in aim mode, seconds: the aim graph's walk-to-idle blend (<c>CustomTransitionEffect15</c>, 0.1 s).</summary>
    const float AimStopBlend = 0.1f;
    /// <summary>How long a pinned foot takes to step to the idle pose, seconds (ours).</summary>
    const float SettleStepSeconds = 0.2f;

    Task<TurnAnimations>? _turnLoad;
    TurnAnimations? _turnData;
    string _turnNote = "not loaded";
    readonly Dictionary<TurnPosture, TurnRig?> _turnRigs = new();

    /// <summary>A posture's resolved clips: the blend set of the idle grid, the grid itself and the turn clips with their masks and profiles.</summary>
    sealed class TurnRig
    {
        public required TurnPosture Posture;
        public required CharacterViewer.BlendSet Set;
        public required AimOffsetGrid Grid;
        public required float[] Weights;
        public TurnProfile? LeftProfile, RightProfile;
        public TurnClip? Left, Right;
        public float EnterRadians;
    }

    void SetUpTurns()
    {
        if (NoTurns) { _turnNote = "switched off (--no-turns)"; return; }
        string gameDir = _gameDir;
        _turnNote = "loading";
        _turnLoad = Task.Run(() => TurnAnimations.Load(gameDir));
    }

    TurnAnimations? TurnData()
    {
        if (_turnData is not null) return _turnData;
        if (_turnLoad is not { IsCompleted: true } load) return null;
        _turnLoad = null;
        if (load.IsFaulted || load.IsCanceled)
        {
            _turnNote = "not available: " + (load.Exception?.GetBaseException().Message ?? "cancelled");
            GD.PrintErr("turns: " + _turnNote);
            return null;
        }
        _turnData = load.Result;
        _turnNote = string.Join(", ", _turnData.Sets.Select(set => $"{set.Class} " + string.Join("/", new[] { set.Relaxed, set.Ready }.OfType<TurnPosture>().Select(p => $"{p.Name} {p.EnterDegrees:F0} deg")) + (set.RelaxedStart is not null || set.ReadyStart is not null ? " + run start" : "")));
        GD.Print("turns: " + _turnNote);
        foreach (var n in _turnData.Notes) GD.Print("  turns note: " + n);
        return _turnData;
    }

    /// <summary>The posture whose turn data applies to this marine now, or null (the body then follows the aim and the old idle shows): not aiming, a ranged weapon in hand, and the family has the data.</summary>
    TurnPosture? TurnPostureOf(PlayerState s, TurnAnimations data)
    {
        if (!s.Alive || s.Aiming || HipFamilyOf(s) is not { } family || data.For(family) is not { } set) return null;
        return HipReady(s) ? set.Ready : set.Relaxed ?? (PistolTwistIdle && family == HipFireWeaponClass.Pistol ? set.Ready : null);
    }

    /// <summary>
    /// <c>--pistol-twist-idle</c> (test aid for the owner, qc/reference-pass; off by default, an <b>interpretation</b> to compare against the video): the pistol posture at rest stands in the
    /// ready posture's "Twist Idle" (pitch clips, upper-body twist toward the aim, the turn clips) instead of the graph's "Idle Relaxed" (<c>SM_NAV_IdleL_CS_01</c>, no twist, no turns). The
    /// data does not do this: in <c>SM_Pistol_NAV</c> "Idle Relaxed" holds no twist modifier and the Nav state's modifier list twists only the right-hand prop for the combat knife
    /// (<c>smtool graph-dump --twists</c>).
    /// </summary>
    static readonly bool PistolTwistIdle = OS.GetCmdlineUserArgs().Contains("--pistol-twist-idle");

    TurnRig? TurnRigOf(TurnPosture posture)
    {
        if (_turnRigs.TryGetValue(posture, out var cached)) return cached;
        TurnRig? rig = null;
        var columns = posture.Idle.Columns;
        bool usable = columns.Count > 0 && columns.All(c => c.Pitches.Count > 0 && c.Pitches.All(p => RigHasClip(p.Clip)));
        if (usable)
        {
            var clips = columns.SelectMany(c => c.Pitches).Select(p => new CharacterViewer.BlendClip(p.Clip, null)).ToArray();
            var grid = new AimOffsetGrid(columns.Select(c => c.HorizontalDegrees).ToArray(), columns.Select(c => (IReadOnlyList<float>)c.Pitches.Select(p => p.PitchDegrees).ToArray()).ToArray());
            rig = new TurnRig
            {
                Posture = posture, Set = new CharacterViewer.BlendSet($"turn:{posture.Graph}:{columns[0].Pitches[0].Clip}", clips), Grid = grid, Weights = new float[clips.Length],
                EnterRadians = posture.EnterDegrees * MathF.PI / 180f,
            };
            if (posture.Left is { } l && posture.Right is { } r && RigHasClip(l.Clip) && RigHasClip(r.Clip))
            {
                rig.Left = l; rig.Right = r;
                rig.LeftProfile = BodyYaw.Profile(l.RootYawDegrees, l.RootDuration, l.Length, l.EndFraction);
                rig.RightProfile = BodyYaw.Profile(r.RootYawDegrees, r.RootDuration, r.Length, r.EndFraction);
            }
            else _notes.Add($"turn in place: the turn clips of the {posture.Name} {posture.Graph} posture are not in the rig; the body follows the aim there");
        }
        else _notes.Add($"turn in place: aim-offset idle clips of the {posture.Name} {posture.Graph} posture are not all in the rig; the old idle shows");
        _turnRigs[posture] = rig;
        return rig;
    }

    /// <summary>Whether the marine is in something that is not a ground pose of the locomotion: dead, in the air, a jump phase, a reaction, the charge, a roll, a melee step or a throw (their own clips drive the body).</summary>
    bool TurnInAction(PlayerState s, float? throwTime) =>
        !s.Alive || !s.Grounded || s.Jump != JumpPhase.None || s.Reaction != ReactionKind.None || s.Rush != RushPhase.None || Evade.Evading(s) || s.MeleeStep != 0 || s.Throwing || throwTime is not null
        // Review S3: the client-timed jump views (landing, ground-pound impact, get-up) outlast the simulation's jump phase and play their own whole-body clips.
        || (_jumpTracks.TryGetValue(s.PlayerId, out var jump) && jump.View != JumpView.None)
        // Animation pass 1: the hard landing's whole-body clip (NetworkView.Fall.cs) owns the body and the feet while it plays.
        || FallLanding(s.PlayerId);

    /// <summary>Whether this marine stands in a way that keeps its body planted: on the ground, not moving or blending out of a move, no action clip or jump pose.</summary>
    bool TurnStanding(PlayerState s, LocoTrack t, float? throwTime)
    {
        if (TurnInAction(s, throwTime)) return false;
        // Only the blend the marine is drawn in counts: the relaxed run's flag stays set while the weapon is up (the ready run has its own), and the sprint needs no flag (its speed is far above standing).
        if (HipReady(s) ? t.HipGaitMoving : t.GaitMoving) return false;
        return s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y <= RunThreshold * RunThreshold;
    }

    /// <summary>
    /// Steps the marine's body heading for this frame (called by <c>Place</c> before it poses the character) and returns the yaw to draw it at: the aim's when turns are off,
    /// the data is not in or the marine is dead, else <see cref="BodyYaw.Yaw"/>.
    /// </summary>
    float BodyYawFor(CharacterViewer character, PlayerState s, bool own, float aimYaw, float? throwTime)
    {
        var t = LocoOf(s.PlayerId);
        double now = _connectedFor;
        float dt = double.IsFinite(t.BodyAt) ? (float)(now - t.BodyAt) : float.PositiveInfinity;
        t.BodyAt = now;
        t.AimYaw = aimYaw;
        _bodyThrowTime = throwTime;
        if (NoTurns || TurnData() is not { } data || !s.Alive || (own && OwnDead)) { t.Body.Snap(aimYaw); t.Stand = null; t.StandRig = null; t.PrevStanding = t.WasStanding = false; character.CancelSettle(); return aimYaw; }
        // feel/charge-directions (protocol 39): through a charge (the bull rush turned by A / D, the dash attack's lunge any of eight ways) the body is the
        // simulation's heading at once: the forward clips play along the travel, never turned toward it at the follow rate (a backward lunge spun 0.25 s).
        if (s.Rush != RushPhase.None) { t.Body.Snap(aimYaw); t.Stand = null; t.StandRig = null; t.PrevStanding = t.WasStanding = false; character.CancelSettle(); return aimYaw; }
        var posture = TurnPostureOf(s, data);
        var rig = posture is null ? null : TurnRigOf(posture);
        bool standing = TurnStanding(s, t, throwTime);
        bool canTwist = rig is not null && standing;
        // M40-T5 (CharacterViewer.FootPin.cs): the moment a marine comes to rest the feet on the ground are pinned while the pose cross-fades to the idle, then step to it.
        if (standing && !t.WasStanding && !character.Settling) character.BeginSettle(posture?.StopBlend ?? (s.Aiming ? AimStopBlend : TurnAnimations.DefaultStopBlend), SettleStepSeconds);
        else if (!standing && character.Settling && TurnInAction(s, throwTime)) character.CancelSettle();
        t.PrevStanding = t.WasStanding; t.WasStanding = standing;
        t.Body.Step(aimYaw, dt, canTwist, rig?.EnterRadians ?? 0f, rig?.LeftProfile, rig?.RightProfile, posture?.TurnBlend ?? TurnAnimations.DefaultBlend, BodyMaxTwist, BodyFollowRate);
        // Animation pass 1: a falling marine stands on the aim-offset idle (the graph's Dropping state) without being planted: the body follows the aim (above), the idle follows the twist.
        // Animation pass 3: also while the fall's legs fade out after the touchdown or its landing plays (FallLanding): the idle stayed the aim-offset idle in the air and is it again
        // once standing, so the plain idle in between flipped the stance twice in a few frames (the transition probe: the hand 46 cm per frame per frame at every soft landing).
        bool fallStand = !canTwist && rig is not null && (t.Fall.Falling || (!CharacterViewer.NoP3 && s.Alive && s.Grounded && FallLanding(s.PlayerId)));
        t.Stand = canTwist || fallStand ? posture : null;
        t.StandRig = canTwist || fallStand ? rig : null;
        return t.Body.Yaw;
    }

    /// <summary>A damped ground speed falling faster than this (m/s squared) while the idle share is rising is a stop, not a slowing walk (ours; the simulation brakes at 40).</summary>
    const float StopDecelerationThreshold = 8f;
    /// <summary>A pinned marine that speeds up again past this damped speed (m/s) drops its pins (ours).</summary>
    const float StopCancelSpeed = 2f;
    /// <summary>Animation pass 3: how long held feet take to slide onto the moving pose when the marine moves on (s; ours).</summary>
    const float PinReleaseSeconds = 0.15f;
    /// <summary>The pins hold a little longer than the posture's stop blend, because they start while the blend into the idle has not begun (the damped speed is still above <see cref="GaitLeaveSpeed"/>; ours).</summary>
    const float StopHoldMargin = 0.06f;

    /// <summary>
    /// Called by the blend space each frame it poses a moving marine: when its damped speed is falling fast and the moving clips no longer have the whole weight (the idle pose is
    /// coming in at the speed of the stop, 1 m/s), pins the feet that are on the ground (<see cref="CharacterViewer.BeginSettle"/>): the cross-fade to the idle would slide them 20 to 70 cm in 0.1 s. A
    /// marine that speeds up again drops them.
    /// </summary>
    void StopPinsFor(CharacterViewer character, PlayerState s, LocoTrack t, float dampedSpeed, float moving, float dt)
    {
        float previous = t.GaitSpeedPrev;
        t.GaitSpeedPrev = dampedSpeed;
        if (NoTurns || CharacterViewer.NoFootPins || !float.IsFinite(dt) || dt <= 0f || dt > 0.25f) return;
        if (character.Settling) { if (dampedSpeed > StopCancelSpeed) character.ReleaseSettle(PinReleaseSeconds); return; }   // animation pass 3: the held feet slide onto the run instead of snapping (CharacterViewer.ReleaseSettle)
        if (moving >= 1f || (previous - dampedSpeed) / dt < StopDecelerationThreshold || !s.Alive || !s.Grounded) return;
        var posture = TurnData() is { } data ? TurnPostureOf(s, data) : null;
        character.BeginSettle((posture?.StopBlend ?? (s.Aiming ? AimStopBlend : TurnAnimations.DefaultStopBlend)) + StopHoldMargin, SettleStepSeconds);
    }

    /// <summary><c>--old-stand</c> (test aid): the one-frame plain idle at the end of a run comes back (before / after runs of qc/reference-pass).</summary>
    static readonly bool OldStand = CharacterViewer.NoP3 || OS.GetCmdlineUserArgs().Contains("--old-stand");
    float? _bodyThrowTime;   // the throw time BodyYawFor was given for the character being placed

    /// <summary>
    /// qc/reference-pass (the transition probe: "move -> idle-plain -> idle" at every stop of a rifle marine): <see cref="BodyYawFor"/> decides at the start of the placement whether the
    /// marine stands, while the run's blend space still has it (<c>GaitMoving</c>); when the blend lets go later in the same placement (its damped speed fell below the leave speed), the
    /// stand was not set, so for that one frame the plain idle clip (or the plain standing aim pose) was shown and the aim-offset idle took over the next frame: two cross-fades from two
    /// different poses in two frames (the right hand's fingers 25 to 33 cm per frame per frame each). The stand is now decided again the moment the blend lets go, with the same rule, so
    /// the run blends straight into the final idle. <c>--old-stand</c> restores the old frame.
    /// </summary>
    void StandAfterGait(PlayerState s, LocoTrack t)
    {
        if (OldStand || NoTurns || t.Stand is not null || !s.Alive || TurnData() is not { } data) return;
        var posture = TurnPostureOf(s, data);
        var rig = posture is null ? null : TurnRigOf(posture);
        if (rig is null || !TurnStanding(s, t, _bodyThrowTime)) return;
        t.Stand = posture; t.StandRig = rig;
        _standFade = posture!.StopBlend;
    }

    /// <summary>
    /// The cross-fade of the stance change <see cref="StandAfterGait"/> starts this placement: the posture's stop blend (the graphs' run-to-idle 'Nav to Nav' 0.333 s relaxed, 0.15 s
    /// ready; the pins of M40-T5 already hold the feet that long), 0 when none. Before, the stance fade (0.15 s) from the plain idle.
    /// </summary>
    float _standFade;

    /// <summary>The yaw the locomotion compares the velocity with: the shown body's (valid after <see cref="BodyYawFor"/>), else the state's.</summary>
    float BodyYawOf(PlayerState s) => !NoTurns && _loco.TryGetValue(s.PlayerId, out var t) && t.Body.Valid ? t.Body.Yaw : s.Yaw;

    /// <summary>
    /// The standing pose of a marine whose body is planted (<see cref="BodyYawFor"/> found it standing in a posture with turn data): the aim-offset idle for the angle
    /// between the aim and the body and the aim pitch, the turn clip on the legs while a turn is made, the spine twist for the families that have one. False when the marine is
    /// not in such a pose (the caller shows the old idle).
    /// </summary>
    bool TurnStandPlace(CharacterViewer character, PlayerState s, HipTrack hip, bool ready, float fadeSeconds)
    {
        var t = LocoOf(s.PlayerId);
        if (t.Stand is not { } posture || t.StandRig is not { } rig) return false;
        return IdlePlace(character, s, hip, ready, fadeSeconds, posture, rig);
    }

    /// <summary>The aim-offset idle of <paramref name="rig"/> (and the turn layer when a turn is made); <see cref="TurnStandPlace"/> and the run start's wait for its clip use it.</summary>
    bool IdlePlace(CharacterViewer character, PlayerState s, HipTrack hip, bool ready, float fadeSeconds, TurnPosture posture, TurnRig rig)
    {
        var t = LocoOf(s.PlayerId);
        float twist = t.Body.Twist(t.AimYaw);   // positive: the aim is to the left of the body
        float horizontal = Math.Clamp(-twist * 180f / MathF.PI, -AimOffsetRange, AimOffsetRange);   // the graph's angle: positive right
        rig.Grid.Weights(posture.Idle.ProceduralTwist ? 0f : horizontal, s.Pitch * 180f / MathF.PI, rig.Weights);
        if (!character.BlendStance(rig.Set, rig.Weights, 0f, fadeSeconds)) return false;
        // Review S4: the grid's clips end at 90 degrees, so a body that lags the aim by more (a flick before the turn clip has swung it round) keeps the end pose and the spine twists the rest,
        // up to BodyMaxTwist (135 degrees, kept: the turn clip's own root rotation, and a 100 degree cap would let the body drag at the follow rate more often).
        character.SetSpineTwist(FootPinRules.SpineTwist(twist, posture.Idle.ProceduralTwist, AimOffsetRange * MathF.PI / 180f, BodyMaxTwist));
        var body = t.Body;
        if (body.LayerSide != TurnSide.None && body.LayerWeight > 0f && (body.LayerSide == TurnSide.Left ? rig.Left : rig.Right) is { } clip)
            character.SetTurnLayer(clip.Clip, body.TurnTime, body.LayerWeight, clip.LegMask);
        hip.Kind = ready ? PoseKind.Hip : PoseKind.Normal;
        return true;
    }

    /// <summary>The F1 line about the turn in place.</summary>
    string TurnInfoLine() =>
        NoTurns ? "Turn in place: off (--no-turns)."
        : $"Turn in place (M40-T5): a standing marine keeps its lower body planted; the upper body follows the aim through the graph's aim-offset idle, and past the threshold the turn clips play (data: {_turnNote}); the body follows at most {BodyFollowRate * 180f / MathF.PI:F0} deg/s otherwise; follow rate, twist cap {BodyMaxTwist * 180f / MathF.PI:F0} deg: ours.";
}
