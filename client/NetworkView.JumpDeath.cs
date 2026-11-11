using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmNet;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The jump-pack death in mid-air (the owner, 2026-11-11: play it, "Yes"; client presentation only, the marine view of every mode; no simulation, wire, protocol or config-hash change): a marine
/// killed in a jump-pack jump (the graph's <c>BOOL_JumpIsActive</c> at its last living frame, ROOT's priority-10 route into "Jumppack Death") plays the graph's three states before the ragdoll:
/// <c>SM_REC_MP_MidAirDeath_Hit_01</c> to its branch at 0.565 s, <c>SM_REC_MP_MidAirDeath_Fall_01</c> looping at speed 0.65 until the body is on a floor, then <c>SM_REC_MP_MidAirImpact_01</c>,
/// and the ragdoll takes the body at its tag (0.367 s) with the clip's motion (the hit reactions' hand-over). The client places the dead body meanwhile (<see cref="JumpDeathRun"/>: the last
/// velocity, the world gravity, the level's walls, ceilings and floors; ours); no floor within <see cref="JumpDeathRules.FallCap"/> (a pit, a kill volume) and the ragdoll takes it where it is.
/// The jump death's effects (the pack's <c>actn_jumpdeath_start</c>, Final Vengeance's) play on the falling body from the death and move on to its ragdoll (<see cref="DeathBody"/>); the impact
/// (<c>actn_jumpdeath_impact</c>: crater, smoke, sound) at the floor clip's <c>StartAction</c>. The respawn, the kill credit, the body's collision and the server's simulation are untouched.
/// Test aids: <c>--no-jump-death</c> (the ragdoll at once, as before), <c>--log-upper</c> (the phases), <c>--hit-reaction-test --hrt-case death-jump</c> (the stage).
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--no-jump-death</c> (test aid): a jump-pack death in mid-air goes to the ragdoll at once, its effects on the ragdoll (before 2026-11-11).</summary>
    static readonly bool NoJumpDeath = OS.GetCmdlineUserArgs().Contains("--no-jump-death");

    int _jumpDeaths, _jumpDeathsFloor, _jumpDeathsCapped, _jumpDeathsCancelled;

    /// <summary>
    /// The first frame of a marine's death (<see cref="MarineDeathHold"/>): when the victim was in a jump-pack jump and the jump death applies (<see cref="JumpDeathRules.Applies"/>; not a fall out of
    /// the level, the clips read and decoded, not <c>--no-jump-death</c> or <c>--old-hit-reactions</c>), starts it (the run, its effects) and returns true.
    /// </summary>
    bool BeginJumpDeath(int id, CharacterViewer character, PlayerState state, bool own, MarineDeath entry, UpperBodyBundle data, KillView? kill, LocoTrack t)
    {
        if (NoJumpDeath || OldHitReactions || !t.WasJumpActive || _config is null) return false;
        if (!JumpDeathRules.Applies(true, kill is { Cause: KillCause.World }, data.Death)) return false;
        var hit = data.Death.Get(MarineDeathKind.JumpStart)!;
        var fall = data.Death.Get(MarineDeathKind.JumpFall)!;
        var impact = data.Death.Get(MarineDeathKind.JumpImpact)!;
        if (_assets?.Animation(hit.Clip) is null || _assets.Animation(fall.Clip) is null || _assets.Animation(impact.Clip) is null) return false;
        var feet = character.Position;   // game space (the characters live under the game space node)
        var v = _motion.TryGetValue(id, out var m) ? m.Velocity : Vector3.Zero;   // the last living frame's motion (smoothed for the others)
        if (v.Length() > MaxRagdollSpeed) v = v.Normalized() * MaxRagdollSpeed;
        var run = new JumpDeathRun(hit, fall, impact, new Num.Vector3(feet.X, feet.Y, feet.Z), new Num.Vector3(v.X, v.Y, v.Z), _config.Gravity, _config.MaxFallSpeed);
        entry.Jump = run; entry.Ray = FallRay(state.Team); entry.Body = character;
        entry.Clip = hit; entry.Kind = MarineDeathKind.JumpStart; entry.Start = entry.Last = _connectedFor;
        character.ClearPoseLayers();   // the flight's layers (wobble, recoil ...) are not part of the death
        _deathsHeld++; _jumpDeaths++;
        _deathKinds[MarineDeathKind.JumpStart] = _deathKinds.GetValueOrDefault(MarineDeathKind.JumpStart) + 1;
        if (UpperLog || own)
            GD.Print($"t {_connectedFor:F2} s: P{id} dies in a jump-pack jump: {hit.Clip} to {hit.RagdollAt:F3} s, {fall.Clip} looping ({fall.Length:F3} s) to the floor, {impact.Clip} to the ragdoll at {impact.RagdollAt:F3} s " +
                     $"(from {feet.X:F1},{feet.Y:F1},{feet.Z:F1} at {v.X:F1},{v.Y:F1},{v.Z:F1} m/s, gravity {_config.Gravity:F0} m/s², cap {JumpDeathRules.FallCap:F0} s; {(kill is { } k ? k.Cause.ToString() : "cause not yet known")}{(own ? ", own" : "")})");
        try { ScheduleDeathFx(id, character, state, own, kill, null, entry); }   // the jump death's effects from the death, on the falling body
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException)) { if (_fxNoted.Add("deathfx-fault")) GD.PrintErr($"death effects failed: {ex}"); }
        return true;
    }

    /// <summary>
    /// Each frame of a jump death: advances the run (the fall against the level), places and poses the body, fires the floor clip's impact for the effects, and returns false once the ragdoll is due
    /// (at the floor clip's tag, at the cap, or at once when the kill turns out to be a fall out of the level), so <see cref="ProcessDeaths"/> builds it from the pose shown.
    /// </summary>
    bool JumpDeathFrame(int id, CharacterViewer character, bool own, MarineDeath entry)
    {
        var run = entry.Jump!;
        float dt = (float)(_connectedFor - entry.Last);
        entry.Last = _connectedFor;
        if (run.Phase != JumpDeathPhase.Ragdoll && run.Age < 1f && _deathCauses.TryGetValue(id, out var c) && c.Kill.Cause == KillCause.World && c.At >= entry.Seen - 0.5)
        {
            run.Cancel();   // the cause came late and was a fall out of the level: no jump death (as such a death on the ground: the ragdoll at once)
            _jumpDeathsCancelled++;
        }
        var events = run.Step(dt, entry.Ray, BodyBottom(character, ref entry.BodyBones));   // the floor hit by the posed body's lowest point (the clips hang the hands below the feet)
        if ((events & JumpDeathEvents.ImpactAction) != 0 && entry.ImpactWatch is { } watch) watch.FloorReached = true;
        if (UpperLog || own)
        {
            if ((events & JumpDeathEvents.Fall) != 0) GD.Print($"t {_connectedFor:F2} s: P{id} jump death: the fall loop from {run.PhaseStart:F3} s ({run.Dropped:F2} m fallen, {run.Velocity.Z:F1} m/s down)");
            if ((events & JumpDeathEvents.FloorHit) != 0 && (events & JumpDeathEvents.Landed) == 0) GD.Print($"t {_connectedFor:F2} s: P{id} jump death: the body meets the floor at {run.FloorHitAt:F3} s, its feet {run.Feet.Z:F2} m up");
            if ((events & JumpDeathEvents.Landed) != 0) GD.Print($"t {_connectedFor:F2} s: P{id} jump death: on the floor at {run.LandedAt:F3} s after {run.Dropped:F2} m ({run.FallSeconds:F2} s of the fall loop)");
            if ((events & JumpDeathEvents.Impact) != 0) GD.Print($"t {_connectedFor:F2} s: P{id} jump death: {run.Impact.Clip} from {run.ImpactStartedAt:F3} s");
            if ((events & JumpDeathEvents.ImpactAction) != 0) GD.Print($"t {_connectedFor:F2} s: P{id} jump death: the floor clip's StartAction at {run.PhaseAge:F3} s into it (the impact's effects)");
        }
        if (run.Phase == JumpDeathPhase.Ragdoll)
        {
            if (run.End != JumpDeathEnd.Floor && entry.ImpactWatch is { } none) none.NoFloor = true;
            if (run.End == JumpDeathEnd.Floor) _jumpDeathsFloor++;
            else if (run.End == JumpDeathEnd.Cap) _jumpDeathsCapped++;
            if (UpperLog || own)
                GD.Print($"t {_connectedFor:F2} s: P{id} jump death: the ragdoll at {run.Age:F3} s ({run.End switch { JumpDeathEnd.Floor => $"the floor clip's tag, {run.PhaseAge:F3} s into it", JumpDeathEnd.Cap => $"no floor within {JumpDeathRules.FallCap:F0} s: the cap, {run.Dropped:F1} m fallen", _ => "the kill was a fall out of the level" }}; " +
                         $"body velocity {run.Velocity.X:F1},{run.Velocity.Y:F1},{run.Velocity.Z:F1} m/s{(own ? ", own" : "")})");
            return false;
        }
        character.Position = new Vector3(run.Feet.X, run.Feet.Y, run.Feet.Z);
        character.Visible = !own || run.Age > 0.12f;
        var (clip, playback, entered) = run.Shown();
        float rate = clip.Length > 1e-3f ? ClipLength(clip.Clip, clip.Length) / clip.Length : 1f;
        character.Pose(clip.Clip, playback * rate, entered ? DeathBlend(clip) : 0f);   // the phase's own blend on entry; the fall loop's wrap is not a new entry
        NoteDeathPose(entry, character);
        _deathHolds++;
        if (LogUpper && _connectedFor - _deathTraceAt >= 0.1)
        {
            _deathTraceAt = _connectedFor;
            GD.Print($"t {_connectedFor:F2} s: jump death trace P{id} {run.Phase} {run.PhaseAge:F2} s ({clip.Clip} at {playback * rate:F2} s), feet {run.Feet.X:F2},{run.Feet.Y:F2},{run.Feet.Z:F2}, velocity {run.Velocity.Z:F1} m/s up: {BoneTrace(character, "RIG_Head", "RIG_Pelvis")}");
        }
        return true;
    }

    /// <summary>Whether a player's death under way is a jump death (its ragdoll gets no push at the hand-over: the blow came long before; ours).</summary>
    bool JumpDeathHeld(int id) => _marineDeaths.TryGetValue(id, out var d) && d.Jump is not null;

    /// <summary>When the ragdoll of a held jump death starts: its effects (queued at the death) move on to the ragdoll; true when there were such (the ragdoll then schedules none).</summary>
    bool JumpDeathHandOver(int id, Ragdoll rd)
    {
        if (!_marineDeaths.TryGetValue(id, out var d) || d.FxBody is not { } body) return false;
        body.Ragdoll = rd;
        body.Held = null;
        // claude/hit-fx-answers: the kill may not have been known when the death began; the melta's burn is decided again here (once per death: MeltaKillBurn's guard).
        try { MeltaKillBurn(id, _deathCauses.TryGetValue(id, out var c) && _connectedFor - c.At < 8.0 ? c.Kill : null, rd); }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException or OutOfMemoryException)) { if (_fxNoted.Add("meltakill-fault")) GD.PrintErr($"melta kill burn failed: {ex.Message}"); }
        return true;
    }

    /// <summary>The own death camera's focus while the own body falls in its jump death (the chest; Godot world), else null.</summary>
    Vector3? JumpDeathFocus(int id) =>
        _marineDeaths.TryGetValue(id, out var d) && d.Jump is not null && d.Body is { } body && IsInstanceValid(body) ? CharacterChest(body) : null;

    /// <summary>A living character's chest (Godot world): the ragdoll's focus bone (its chest body's), else 1.2 m above the feet.</summary>
    Vector3 CharacterChest(CharacterViewer character)
    {
        if (character.Skeleton is { } skeleton && _ragdollDef is { } def && def.FocusBody < def.Bodies.Length && def.Bodies[def.FocusBody].Bone is int bone && bone >= 0 && bone < skeleton.GetBoneCount())
            return (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone)).Origin;
        return character.GlobalPosition + Vector3.Up * 1.2f;
    }

    /// <summary>
    /// The shown pose's lowest bone relative to the character's feet (m, world up; negative below them), the placement helpers (<c>*Root*</c>) left out; 0 without a skeleton. <paramref name="bones"/>
    /// caches the bones that count (once per death).
    /// </summary>
    internal static float BodyBottom(CharacterViewer character, ref int[]? bones)
    {
        if (character.Skeleton is not { } skeleton || !IsInstanceValid(skeleton)) return 0f;
        int count = skeleton.GetBoneCount();
        if (bones is null || bones.Length > count)
        {
            var list = new List<int>(count);
            for (int b = 0; b < count; b++) if (!skeleton.GetBoneName(b).Contains("Root", StringComparison.OrdinalIgnoreCase)) list.Add(b);
            bones = list.ToArray();
        }
        var toWorld = skeleton.GlobalTransform;
        float feet = character.GlobalPosition.Y, low = float.PositiveInfinity;
        foreach (int b in bones) low = MathF.Min(low, (toWorld * skeleton.GetBoneGlobalPose(b)).Origin.Y - feet);
        return float.IsFinite(low) ? low : 0f;
    }

    /// <summary>The level a falling body meets (game space): the character collision of its side (as the held slide), or the flat floor at z = 0 the ragdolls lie on without a map.</summary>
    JumpDeathRay FallRay(Team team)
    {
        if (_client?.Collision is { } world)
        {
            var level = world.CharacterFor(team);
            return (origin, direction, max) => level.Raycast(origin, direction, max)?.Distance;
        }
        return (origin, direction, max) => direction.Z < -1e-6f && origin.Z >= 0f && origin.Z / -direction.Z <= max ? origin.Z / -direction.Z : null;
    }

    /// <summary>For the bot log's upper-body line.</summary>
    string JumpDeathStats() =>
        $"jump deaths {_jumpDeaths} ({_jumpDeathsFloor} to the floor, {_jumpDeathsCapped} capped, {_jumpDeathsCancelled} cancelled{(NoJumpDeath ? "; --no-jump-death" : "")})";
}
