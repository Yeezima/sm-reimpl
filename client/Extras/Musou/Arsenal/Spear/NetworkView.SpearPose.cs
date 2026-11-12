using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Arsenal;

namespace SmClient;

/// <summary>
/// The power spear's approved animations in a Musou match (docs/claw-spear-animations.md section 4; the owner approved the sixth pass, 2026-11-10). W3 plays the thunder hammer's clips for
/// the spear's strikes (its combo is the hammer's at 0.85, so they stay posed: their sound and footstep tags, the hand-over to the legs and every timing are the hammer's). Over them each
/// spear player's character shows the spear's own keyframed clip (<see cref="ClawSpearClips"/>) through <see cref="CharacterViewer.WeaponPose"/>:
/// <list type="bullet">
/// <item>a strike: the clip of its step (<see cref="ClawSpearClips.SpearStrike"/>) at the strike's drawn time (<see cref="ExtrasAttackTime"/>: the simulation's attack time, predicted for the own
/// player and from the snapshots for the others, after the Musou weight pass's retime), laid over the clip it was authored on (the hammer's idle; <see cref="WeaponPoseState.Base"/>), so it
/// shows exactly as on the preview stage; the clip foot lock is off for it (the clip's own leg solve plants the feet);</item>
/// <item>the jump pack's pound: the point-down dive over the hammer's dive loop and the impale over its landing, while the pound's clips are posed;</item>
/// <item>standing, walking, running and sprinting with the spear (the hammer's stance: the pistol not up, nothing else shown): the one-handed carry over the stance;</item>
/// <item>anything else (the pistol up, a reaction, a roll, a jump, a charge, an air slash): nothing, faded out over <see cref="SpearPoseFade"/>, the spear back on W3's mount.</item>
/// </list>
/// While a clip shows, the spear's model is put on the hands as the stage puts its stand-in (<see cref="WeaponRig.PlaceSpearOnHands"/>: the clip's grip tracks, the hand at the clip's
/// grip point in metres from the butt). Musou Mode only, the spear's players only; the claw keeps the power sword's clips. Restore: <c>--musou-spear-stand-in</c> (the hammer's clips
/// and W3's mounts alone, as before).
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--musou-spear-stand-in</c>: the spear's strikes and carry with the thunder hammer's clips and W3's mounts alone (before the approved clips were wired in a match).</summary>
    static readonly bool MusouSpearStandIn = CmdFlag("musou-spear-stand-in");

    /// <summary>How long the spear's pose takes to come in and to go out where it starts or stops showing (s; ours, about the stances' cross-fade).</summary>
    const float SpearPoseFade = 0.15f;
    /// <summary>The planar speed (m/s) from which the carry's fist solve is fully laid (ours: about a walk; WeaponPoseState.FistWeight).</summary>
    const float SpearFistFullAt = 1.5f;

    sealed class SpearPoseTrack
    {
        public readonly WeaponPoseState State = new() { Weight = 0f };
        /// <summary>The process frame a strike was last posed in (from the attack pose).</summary>
        public ulong StrikeFrame = ulong.MaxValue;
        /// <summary>The loop clock of the hammer's idle under a strike and of the carry (s).</summary>
        public float IdleClock;
        public CharacterViewer? Character;
        /// <summary>The pound's landing shown now (the impact clip posed), its server tick (own: the predicted tick; others: the render tick), the client's guess, and the choice once locked.</summary>
        public bool Landing, Predicted, Locked, Impaled;
        public uint LandingTick;
        public float LandingAt;
    }

    readonly Dictionary<int, SpearPoseTrack> _spearPoses = new();
    readonly List<int> _spearGone = new();
    /// <summary>Each player's newest impale evidence from the server (the server tick of his melee-marker hit or melee kill; <see cref="SpearRules.ImpaleEvidence"/>).</summary>
    readonly Dictionary<int, uint> _spearImpaleSeen = new();

    /// <summary>
    /// After this long into the landing clip the impale or the plain landing stays as chosen (s; ours): a late answer from the server would otherwise drive the spear back down, or lift it
    /// out of the Ork, in the middle of the landing. The two share their touch-down to <see cref="ClawSpearClips.ImpaleDrive"/>, so a change of mind before then does not show; the own
    /// landing shows the client's guess until the server's events for its ticks are in (<see cref="SpearRules.ShowsImpale"/>), about the round trip and 4 ticks after it lands.
    /// </summary>
    const float SpearLandingLock = 0.25f;

    /// <summary>From the Musou frame, before its events are taken: notes every player's impale evidence (Musou Mode only).</summary>
    void SpearImpaleEvents(IReadOnlyList<MusouEvent> events)
    {
        if (!SpearPosesOn) return;
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (!SpearRules.ImpaleEvidence(e, e.Player)) continue;
            if (!_spearImpaleSeen.TryGetValue(e.Player, out uint seen) || e.Tick > seen) _spearImpaleSeen[e.Player] = e.Tick;
        }
    }

    /// <summary>
    /// The pound's landing clip for <paramref name="s"/> at <paramref name="at"/> s into it: the impale when the server's events show this landing impaled an Ork (or, until they can have come,
    /// when the client sees one within the impale's reach), else the plain landing (owner via the lead, 2026-11-11).
    /// </summary>
    WeaponPoseClip SpearLandingOf(PlayerState s, SpearPoseTrack track, float at)
    {
        var client = _client!;
        if (!track.Landing || at + 1e-3f < track.LandingAt)
        {
            bool own = ReferenceEquals(track.Character, _local);
            track.Landing = true; track.Locked = false;
            track.LandingTick = own ? client.Tick : (uint)Math.Max(0.0, Math.Floor(client.RemotePlayers().Tick));
            track.Predicted = SpearRules.ImpaleLikely(s.Position, SpearEnemiesShown(), _config!.MeleeVerticalReach);
        }
        track.LandingAt = at;
        if (!track.Locked)
        {
            bool seen = _spearImpaleSeen.TryGetValue(s.PlayerId, out uint tick) && SpearRules.NearLanding(tick, track.LandingTick);
            track.Impaled = SpearRules.ShowsImpale(seen, track.Predicted, client.LatestSnapshotTick, track.LandingTick);
            if (at >= SpearLandingLock)
            {
                track.Locked = true;
                if (SpearCaptureLog) GD.Print($"SPEARLAND P{s.PlayerId} landing tick {track.LandingTick}: {(track.Impaled ? "impale" : "plain landing")} (server evidence {(seen ? $"at tick {tick}" : "none")}, client guess {(track.Predicted ? "an Ork in reach" : "none in reach")}, newest snapshot {client.LatestSnapshotTick}); frame {Engine.GetFramesDrawn()}");
            }
        }
        return ClawSpearClips.SpearLanding(track.Impaled);
    }

    /// <summary>The places of the enemies alive as this client draws them (the client's guess at an impale).</summary>
    IEnumerable<System.Numerics.Vector3> SpearEnemiesShown()
    {
        for (int i = 0; i < _tfwShown.Count; i++) if (_tfwShown[i].State != MusouEnemyState.Dying) yield return _tfwShown[i].Position;
    }

    /// <summary>
    /// The spear's clips are shown in this match: Musou's content, and neither <c>--musou-spear-stand-in</c> nor <c>--no-weapon-pose</c> (review, 2026-11-10: with the overlay off, the strike's
    /// foot-lock override and the spear's placement on the hands acted still; now nothing of it does and W3's mounts and the hammer's clips show alone).
    /// </summary>
    /// <summary><c>--old-spear-hew</c> (musou/spear-authored): set once when the view class first loads, before any clip is shown (<see cref="SpearHewSwitch"/>).</summary>
    static readonly bool OldSpearHew = SpearHewSwitch.Apply();

    bool SpearPosesOn => !MusouSpearStandIn && !CharacterViewer.NoWeaponPose && _config is { } c && c.Extras.HasFlag(ContentExtras.Musou);

    /// <summary>
    /// From the attack pose, before its clip is posed (<see cref="ExtrasAttackTime"/>): a spear player's strike shows the spear's clip of that step at <paramref name="drawnTime"/>, so the overlay is
    /// laid in the same pose call as the hammer's clip under it.
    /// </summary>
    void SpearStrikeShown(PlayerState s, bool own, float drawnTime)
    {
        if (!SpearPosesOn || s.MeleeStep <= 0 || !SpearRules.Wields(_config!, s) || ClawSpearClips.SpearStrike(s.MeleeHeavy, s.MeleeStep) is not { } clip
            || Combat.MeleeStepOf(_config!, s) is not { } step) return;
        var character = own ? _local : _remotes.GetValueOrDefault(s.PlayerId);
        if (character is null) return;
        var track = SpearTrackOf(s.PlayerId, character);
        track.State.Show(clip, clip.TimeAt(drawnTime, step.HitTime));
        track.State.PinFeet = true;   // planted feet stay on their spots in the world while the lunge moves the body (FootPin, 2026-11-11)
        track.State.Base = clip.BaseClip;
        track.State.BaseTime = track.IdleClock % ClawSpearClips.SpearIdleLength;
        track.StrikeFrame = Engine.GetProcessFrames();
        character.ClipFootLock = false;   // the clip's own leg solve has the feet (the lock would pin the hammer clip's)
    }

    SpearPoseTrack SpearTrackOf(int playerId, CharacterViewer character)
    {
        if (!_spearPoses.TryGetValue(playerId, out var track)) _spearPoses[playerId] = track = new SpearPoseTrack();
        if (!ReferenceEquals(track.Character, character)) { track.Character = character; track.State.Weight = 0f; }
        if (!ReferenceEquals(character.WeaponPose, track.State)) character.WeaponPose = track.State;
        return track;
    }

    /// <summary>Once a frame after every character is posed and its weapons placed (<see cref="ExtrasFrame"/>): each spear player's pose between strikes, the fades, and the spear on the hands.</summary>
    void SpearPoseFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta)
    {
        if (_spearPoses.Count == 0 && !SpearPosesOn) return;
        if (_client is not null && _local is not null) SpearPoseOf(me, _local, (float)delta);
        for (int i = 0; i < others.Count; i++)   // by index: no enumerator over the interface each frame
            if (_remotes.GetValueOrDefault(others[i].PlayerId) is { } character) SpearPoseOf(others[i], character, (float)delta);
        // Players who left (or a character no longer drawn) keep no track.
        _spearGone.Clear();
        foreach (var (id, track) in _spearPoses)
            if (track.Character is not { } c || !GodotObject.IsInstanceValid(c) || (c != _local && _remotes.GetValueOrDefault(id) != c)) _spearGone.Add(id);
        foreach (int id in _spearGone) _spearPoses.Remove(id);
        // Nor any impale evidence (review of 2026-11-11): a player no longer drawn keeps none.
        _spearGone.Clear();
        foreach (int id in _spearImpaleSeen.Keys)
            if (id != me.PlayerId && !_remotes.ContainsKey(id)) _spearGone.Add(id);
        foreach (int id in _spearGone) _spearImpaleSeen.Remove(id);
    }

    void SpearPoseOf(PlayerState s, CharacterViewer character, float delta)
    {
        bool wields = SpearPosesOn && s.Alive && _config is not null && SpearRules.Wields(_config, s);
        if (!wields && !_spearPoses.ContainsKey(s.PlayerId)) return;
        var track = SpearTrackOf(s.PlayerId, character);
        var state = track.State;
        track.IdleClock += delta;
        bool show = false;
        if (wields)
        {
            if (track.StrikeFrame == Engine.GetProcessFrames()) show = true;   // the attack pose showed the strike (SpearStrikeShown)
            else
            {
                state.PinFeet = false;
                string? posed = character.PosedClip;
                float at = character.PosedClipTime;
                state.Base = null;
                bool landing = posed is not null && _jmpImpact is not null && string.Equals(posed, _jmpImpact, StringComparison.OrdinalIgnoreCase);
                if (!landing) track.Landing = false;
                if (posed is not null && _jmpDive is not null && string.Equals(posed, _jmpDive, StringComparison.OrdinalIgnoreCase))
                { state.Show(ClawSpearClips.SpearDive, at); state.Base = ClawSpearClips.SpearDiveLoop; state.BaseTime = at % ClawSpearClips.SpearDiveLoopLength; show = true; }
                else if (landing)
                { state.Show(SpearLandingOf(s, track, at), at); state.Base = ClawSpearClips.SpearDiveImpact; state.BaseTime = at; show = true; }   // the impale only on a hit (2026-11-11)
                else if (posed is null && s.MeleeStep == 0 && s.Rush == RushPhase.None && HammerStance(s) && character.PoseLayerClip is null)
                { state.Show(ClawSpearClips.SpearRest, track.IdleClock % ClawSpearClips.SpearIdleLength); show = true; }
            }
        }
        if (!show) { state.Base = null; state.PinFeet = false; }
        // The carry's fist solve (WeaponPoseClip.FistTarget) comes in with the speed: off standing (the keys hold the fist there), full from a walk on (ours: SpearFistFullAt, eased).
        float speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
        float fistWant = Math.Clamp(speed / SpearFistFullAt, 0f, 1f);
        state.FistWeight += Math.Clamp(fistWant - state.FistWeight, -delta / SpearPoseFade, delta / SpearPoseFade);
        // The fades: in where a clip starts showing, out (the last clip held at its last time) where none does; the track goes once it is out and the spear is not wielded.
        float step = SpearPoseFade > 1e-4f ? delta / SpearPoseFade : 1f;
        state.Weight = show ? MathF.Min(1f, state.Weight + step) : MathF.Max(0f, state.Weight - step);
        if (!show && state.Weight <= 0f) { state.Show(null, 0f); state.From = null; }
        state.Advance(delta);
        var rig = character.Weapons;
        bool onHands = false;
        if (state.Weight > 1e-3f && state.Clip is not null && rig is not null && character.WeaponPoseHands(out var right, out var left))
        {
            // The frame the carry leans in and the strikes' edge faces: the stance's, from the hips, as on the stage (owner's review of 2026-11-10: the carried blade "facing away from the
            // character model", the spear "held out away from the body"; W3b leaned the carry toward the skeleton's +Y, the way the player faces, which across the hammer idle's side-on
            // stance, its hips 71 degrees right of it, put the shaft and the edge across the body).
            float grip = SpearGripOf(state, out float point);
            onHands = rig.PlaceSpearOnHands(right, left, grip, point, character.WeaponPoseSpearFront(), state.Weight, state.FistWeight, character.WeaponPoseSpine());   // the carry's: the facing (2026-11-11)
        }
        if (rig is not null)
        {
            rig.GripSlideDriven = onHands;   // W3's own slide waits while the clip holds the hand, and goes on from where the clip left it
            if (onHands) rig.GripSlide = SpearHold.SlideAt(SpearGripPointOf(state));
        }
        if (!wields && state.Weight <= 0f) { if (ReferenceEquals(character.WeaponPose, state)) character.WeaponPose = null; _spearPoses.Remove(s.PlayerId); }
    }

    /// <summary>The left hand's hold on the shaft (0 to 1) of the shown clip, faded from the clip before it like the pose; <paramref name="point"/> the right hand's grip point (m from the butt).</summary>
    static float SpearGripOf(WeaponPoseState state, out float point)
    {
        point = SpearGripPointOf(state);
        float grip = state.Clip?.GripAt(state.Time) ?? 1f;
        if (state.From is { } from && state.FromShare is var share and > 0f) grip += (from.GripAt(state.FromTime) - grip) * share;
        return grip;
    }

    static float SpearGripPointOf(WeaponPoseState state)
    {
        float point = state.Clip?.GripPointAt(state.Time) ?? WeaponPoseClip.LowGrip;
        if (state.From is { } from && state.FromShare is var share and > 0f) point += (from.GripPointAt(state.FromTime) - point) * share;
        return point;
    }
}
