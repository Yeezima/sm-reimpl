using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmNet;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// F-D1 (fidelity; client presentation only, no simulation or protocol change): the death effects the original multiplayer data binds to marine and Chaos player deaths,
/// played at the start of the ragdoll (<see cref="DeathEffects"/>, <see cref="DeathEffectRules"/>; evidence in docs/discovery.md "Death effects").
/// <list type="bullet">
/// <item>A kill by a plasma weapon plays <c>actn_plasma_death</c> of the victim's player blueprint (<c>plasma_limb_vanish</c> at the hands, a calf, a thigh, the spine and the head, again 1.7 s
/// later, and <c>plasma_dmg_burning_body</c> on the spine); a kill by the melta gun plays <c>actn_melta_death</c> (<c>meltagun_dmg_vaporize_limb</c> at seven body markers). A Chaos
/// plasma weapon recolours them purple like its other plasma effects (<see cref="PlasmaColour"/>, the owner's rule).</item>
/// <item>A marine killed in a jump (flight or dive, with a jump pack) plays the pack's <c>actn_jumpdeath_start</c> (<c>jumppack_death_mp</c> at the spine, <c>jumppack_dmg_burn_limb</c>
/// on both upper arms, a calf and a thigh, the explosion sound) and, when the body reaches the ground, <c>actn_jumpdeath_impact</c> (<c>jumppack_land_death_mp</c> with its crater and the impact sound).</item>
/// <item>Final Vengeance (the perk whose upgrade string the player blueprint's <c>death_action</c> keys on): the own death plays <c>jumppack_predeath</c> at once and the explosion's effects 2.5 s later;
/// for other players the perk is not on the wire, so the explosion's effects play when a kill by that perk names the dead player as the killer.</item>
/// </list>
/// Nothing is severed or removed: the data's <c>remove_wargear</c> step of the plasma death (the corpse's weapon, 1.2 s) is not drawn, and the limb effects only play at their bones.
/// What fires these actions in the original is the death state of the behaviour graph (see <see cref="DeathEffects"/>); binding it to the kill's cause is interpretation, and so is
/// resolving the data's marker names that the rig does not have (<see cref="DeathBone"/>). <c>--no-death-fx</c> plays none of it; <c>--log-death-fx</c> logs what each kill played.
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--no-death-fx</c> (test aid): none of the death effects (frame cost baseline, and the behaviour before F-D1).</summary>
    bool NoDeathFx => _noDeathFx ??= CmdFlag("no-death-fx");
    bool? _noDeathFx;
    /// <summary><c>--log-death-fx</c> (test aid): logs the death effects each kill played (bots log them always).</summary>
    bool LogDeathFx => _logDeathFx ??= CmdFlag("log-death-fx");
    bool? _logDeathFx;

    /// <summary>Priority of these effects in the player's cull (a death is rarer than a shot or a hit and bigger).</summary>
    const int DeathFxPriority = 2;
    /// <summary>Most steps waiting at once (a death queues up to 17; a cap so that a long fight in a faulty state cannot grow the list).</summary>
    const int MaxDeathFxJobs = 400;

    DeathEffects? _deathEffects;
    readonly Dictionary<int, bool> _airborneAlive = new();
    readonly List<DeathFxJob> _deathFxJobs = new();
    readonly List<JumpImpactWatch> _jumpImpacts = new();
    readonly Dictionary<int, double> _vengeanceShown = new();
    readonly Dictionary<string, int> _deathFxKinds = new();
    int _deathFxEffects, _deathFxSounds, _deathFxMissing, _deathFxRemovals, _deathFxKills;
    /// <summary>Milliseconds spent choosing and queueing in the death frame (worst), and playing the due steps in one frame (worst); for the log and the F1 line.</summary>
    double _deathFxQueueMs, _deathFxPlayMs;
    /// <summary>Worst milliseconds of one call: starting an effect, finding the ground, posting a sound, finding a bone (to say where a slow frame went).</summary>
    double _dfxEffectMs, _dfxGroundMs, _dfxSoundMs, _dfxBoneMs;
    bool _deathFxNoted;
    /// <summary>Bone index (or -1: the rig has none) of each marker name, with the bone count of the skeleton it was found on (the marine rig is shared by every corpse).</summary>
    readonly Dictionary<string, (int Bone, int Count)> _deathBones = new(StringComparer.Ordinal);
    HashSet<string>? _groundFxNames;

    static double Since(long began) => System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;

    /// <summary>One death's presentation: what was chosen and what has played (for the log).</summary>
    sealed class DeathFxRun
    {
        public int PlayerId;
        public bool Own, Chaos;
        public string Label = "", Wargear = "";
        public int Pending, Effects, Sounds, Missing, Removals, NoGround;
        public readonly Dictionary<string, int> Names = new();
    }

    /// <summary>One step of a death action waiting for its time; the ragdoll it plays on is the corpse's (a respawn removes it and with it the step).</summary>
    sealed class DeathFxJob
    {
        public double Due;
        public required Ragdoll Ragdoll;
        public required DeathStep Step;
        public required DeathFxRun Run;
        public Vector3? NegativeHit;
        public string Wargear = "";
    }

    sealed record JumpImpactWatch(Ragdoll Ragdoll, DeathAction? Action, double StartAt, DeathFxRun Run, Vector3? NegativeHit);

    /// <summary>While a player is alive: whether it is in a jump the original's behaviour graph counts as active (<c>BOOL_JumpIsActive</c>: flight or dive; the take-off still stands on the ground).</summary>
    void NoteAirborne(int id, PlayerState state) =>
        _airborneAlive[id] = state.Jump != JumpPhase.TakeOff && (JumpPack.Jumping(state.Jump) || JumpPack.Pounding(state.Jump)) && !state.Grounded;

    // ---- choosing and scheduling ----

    /// <summary>
    /// Called when a ragdoll starts: picks the death action (<see cref="DeathEffectRules.Choose"/>) and queues its steps. Never throws; false when nothing is played.
    /// </summary>
    void ScheduleDeathFx(int id, CharacterViewer character, PlayerState state, bool own, KillView? kill, Ragdoll rd)
    {
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        double jit = Jit(), gc = GC.CollectionCount(0);
        ScheduleDeathFxCore(id, character, state, own, kill, rd);
        double ms = Since(began);
        if (ms > _deathFxQueueMs) { _deathFxQueueMs = ms; _deathFxQueueNote = $"jit {Jit() - jit:F2} ms, {GC.CollectionCount(0) - gc} gen0 GC"; }
    }

    /// <summary>Milliseconds this thread has spent compiling (JIT) so far; the difference over a timed window says whether a slow frame was the JIT.</summary>
    static double Jit() => System.Runtime.JitInfo.GetCompilationTime(currentThread: true).TotalMilliseconds;
    string _deathFxQueueNote = "", _deathFxPlayNote = "";
    readonly List<string> _deathFxLogs = new();

    void ScheduleDeathFxCore(int id, CharacterViewer character, PlayerState state, bool own, KillView? kill, Ragdoll rd)
    {
        if (NoDeathFx || _deathEffects is not { } effects || _config is null || _fx is null || !FxActive) return;
        if (IsOrkCharacter(character)) return;
        bool chaos = state.Team == Team.Chaos;
        var side = effects.Side(chaos);
        bool hasPack = Loadouts.JumpPack(_config, state) is not null;
        bool jumping = _airborneAlive.GetValueOrDefault(id);
        string wargear = "";
        var family = DeathFamily.None;
        if (kill is { Cause: KillCause.Weapon } k && k.Index >= 0 && k.Index < _config.Weapons.Count)
        {
            wargear = _config.Weapons[k.Index].Name;
            family = effects.FamilyOf(wargear);
        }
        var kind = DeathEffectRules.Choose(jumping, hasPack, family);
        var hit = kill is { HasDirection: true } d ? -DirToGodot(new Num.Vector3(d.Direction.X, d.Direction.Y, d.Direction.Z)).Normalized() : (Vector3?)null;
        _deathFxKills++;
        var run = new DeathFxRun { PlayerId = id, Own = own, Chaos = chaos, Label = kind == DeathKind.None ? "" : kind.ToString().ToLowerInvariant(), Wargear = wargear };
        double now = DeathSeenAt(id, _connectedFor);   // pass 2: a held death's ragdoll exists later; the steps count from when the death was seen (those already due play at once)
        if ((_bot || LogDeathFx) && _connectedFor - now > 0.05) GD.Print($"t {_connectedFor:F2} s: P{id} death effects count from the death seen {_connectedFor - now:F2} s ago (steps already due play at once)");
        if (DeathEffectRules.ActionOf(side, kind) is { } action)
        {
            double start = now + DeathEffectRules.StartOf(kind);
            Enqueue(run, rd, action, start, hit, kind == DeathKind.Jump ? "" : wargear);
            if (kind == DeathKind.Jump) _jumpImpacts.Add(new JumpImpactWatch(rd, side.JumpImpact, start, run, hit));
        }
        // Final Vengeance: the own perks are known (the others' are not on the wire; their explosion shows when it kills, NoteDeathCause).
        if (own && Loadouts.Upgrades(_config, state).Has(DeathEffectRules.FinalVengeanceUpgrade)
            && side.Perks.FirstOrDefault(p => p.Upgrade.Equals(DeathEffectRules.FinalVengeanceUpgrade, StringComparison.OrdinalIgnoreCase)) is { } perk)
        {
            _vengeanceShown[id] = now;
            run.Label += run.Label.Length == 0 ? "final vengeance" : " + final vengeance";
            Enqueue(run, rd, perk.Action, now, null, "");
        }
        if (run.Pending == 0 && !_jumpImpacts.Any(w => w.Run == run)) return;
        CountKind(run.Label);
    }

    void CountKind(string label) => _deathFxKinds[label] = _deathFxKinds.GetValueOrDefault(label) + 1;

    void Enqueue(DeathFxRun run, Ragdoll rd, DeathAction action, double start, Vector3? negativeHit, string wargear, double minDelay = 0.0)
    {
        foreach (var step in action.Steps)
        {
            if (step.Kind == DeathStepKind.Effect && step.Fx is not { Name.Length: > 0 }) continue;
            if (step.Delay < minDelay) continue;
            if (_deathFxJobs.Count >= MaxDeathFxJobs) { var dropped = _deathFxJobs[0]; _deathFxJobs.RemoveAt(0); dropped.Run.Pending--; }   // the oldest goes; its death no longer waits for it
            _deathFxJobs.Add(new DeathFxJob { Due = start + step.Delay - minDelay, Ragdoll = rd, Step = step, Run = run, NegativeHit = negativeHit, Wargear = wargear });
            run.Pending++;
        }
    }

    /// <summary>
    /// A kill by Final Vengeance's blast (<see cref="KillCause.Perk"/>) names the dead exploder as the killer: its explosion (the steps due after the delay) plays on its corpse if the own
    /// death has not already shown it. Called for each kill-feed entry.
    /// </summary>
    void NoteVengeanceKill(KillView kill)
    {
        if (NoDeathFx || _deathEffects is not { } effects || _config is null || kill.Cause != KillCause.Perk || kill.Killer <= 0 || kill.Killer == kill.Victim) return;
        if (kill.Index < 0 || kill.Index >= _config.Perks.Count || !string.Equals(_config.Perks[kill.Index].UpgradeString, DeathEffectRules.FinalVengeanceUpgrade, StringComparison.OrdinalIgnoreCase)) return;
        if (_vengeanceShown.TryGetValue(kill.Killer, out var at) && _connectedFor - at < 8.0) return;   // already shown (the own death, or an earlier victim of the same blast)
        var rd = _ragdolls.LastOrDefault(r => r.PlayerId == kill.Killer && IsInstanceValid(r));
        if (rd is null || _fx is null || !FxActive) return;
        _vengeanceShown[kill.Killer] = _connectedFor;
        var side = effects.Side(TeamOf(kill.Killer) == Team.Chaos);
        if (side.Perks.FirstOrDefault(p => p.Upgrade.Equals(DeathEffectRules.FinalVengeanceUpgrade, StringComparison.OrdinalIgnoreCase)) is not { } perk) return;
        var run = new DeathFxRun { PlayerId = kill.Killer, Own = kill.Killer == _client?.PlayerId, Chaos = side == effects.Chaos, Label = "final vengeance (seen from its kill)" };
        _deathFxKills++;
        CountKind(run.Label);
        double blast = perk.Action.Steps.Select(s => (double)s.Delay).DefaultIfEmpty(0).Max();
        Enqueue(run, rd, perk.Action, _connectedFor, null, "", minDelay: (float)blast);
    }

    // ---- per frame ----

    /// <summary>Called every frame from <see cref="UpdateFx"/>: plays the steps that are due and watches the jump deaths for the ground.</summary>
    bool _deathSoundsPrefetched;

    /// <summary>Once, when both the effects data and the sound system are there: decodes the death sounds' media in the background (a first post would decode on the main thread, tens of milliseconds).</summary>
    void PrefetchDeathSounds()
    {
        if (_deathSoundsPrefetched || _deathEffects is not { } effects || _sound is null || _audio is null) return;
        _deathSoundsPrefetched = true;
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        var wems = effects.Marines.All.Concat(effects.Chaos.All).SelectMany(a => a.Steps).Where(s => s.Kind == DeathStepKind.Sound).Select(s => s.Sound).Distinct(StringComparer.Ordinal)
            .SelectMany(e => _audio.WemsOf(e)).Distinct().ToList();
        if (wems.Count > 0) _sound.Prefetch(wems);
        if (_bot || LogDeathFx) GD.Print($"death sounds: {wems.Count} wems prefetched in {System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds:F1} ms");
    }

    void FxDeaths()
    {
        PrefetchDeathSounds();
        if (_deathFxJobs.Count == 0 && _jumpImpacts.Count == 0) return;
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        double jit = Jit(), gc = GC.CollectionCount(0);
        FxDeathsCore();
        double ms = Since(began);
        if (ms > _deathFxPlayMs) { _deathFxPlayMs = ms; _deathFxPlayNote = $"jit {Jit() - jit:F2} ms, {GC.CollectionCount(0) - gc} gen0 GC"; }
        if (_deathFxLogs.Count > 0) { foreach (var line in _deathFxLogs) GD.Print(line); _deathFxLogs.Clear(); }   // printed outside the timed window
    }

    void FxDeathsCore()
    {
        double now = _connectedFor;
        for (int i = _deathFxJobs.Count - 1; i >= 0; i--)
        {
            var job = _deathFxJobs[i];
            if (!CorpseThere(job.Ragdoll)) { _deathFxJobs.RemoveAt(i); Finish(job.Run, job.Run.Pending - 1); continue; }
            if (now < job.Due) continue;
            _deathFxJobs.RemoveAt(i);
            PlayDeathStep(job);
            Finish(job.Run, job.Run.Pending - 1);
        }
        for (int i = _jumpImpacts.Count - 1; i >= 0; i--)
        {
            var w = _jumpImpacts[i];
            if (!CorpseThere(w.Ragdoll)) { _jumpImpacts.RemoveAt(i); Finish(w.Run, w.Run.Pending); continue; }
            if (now < w.StartAt) continue;
            if (!DeathEffectRules.ImpactDue((float)(now - w.StartAt), HeightAbove(w.Ragdoll.FocusPoint))) continue;
            _jumpImpacts.RemoveAt(i);
            if (w.Action is { } impact) PlayJumpImpact(w, impact);
        }
    }

    bool CorpseThere(Ragdoll rd) => IsInstanceValid(rd) && !rd.IsQueuedForDeletion() && rd.IsInsideTree();

    void Finish(DeathFxRun run, int pending)
    {
        run.Pending = pending;
        if (pending > 0 || _jumpImpacts.Any(w => w.Run == run)) return;
        if (_bot || LogDeathFx || FrameProfileOn || run.Own)
            _deathFxLogs.Add($"t {_connectedFor:F1} s: death fx P{run.PlayerId}{(run.Own ? " (own)" : "")} {(run.Chaos ? "chaos" : "marine")} {run.Label}{(run.Wargear.Length > 0 ? " by " + run.Wargear : "")}: " +
                     $"{run.Effects} effect(s) ({string.Join(", ", run.Names.Select(kv => $"{kv.Key} x{kv.Value}"))}), {run.Sounds} sound(s){(run.Missing > 0 ? $", {run.Missing} not available" : "")}{(run.Removals > 0 ? $", {run.Removals} weapon removal(s) not drawn" : "")}{(run.NoGround > 0 ? ", no ground below: the impact is skipped" : "")}; " +
                     $"worst so far: queued in {_deathFxQueueMs:F2} ms ({_deathFxQueueNote}), played in {_deathFxPlayMs:F2} ms in one frame ({_deathFxPlayNote}; one effect {_dfxEffectMs:F2}, ground {_dfxGroundMs:F2}, sound {_dfxSoundMs:F2}, bone {_dfxBoneMs:F2})");
    }

    void PlayDeathStep(DeathFxJob job)
    {
        var run = job.Run;
        var rd = job.Ragdoll;
        switch (job.Step.Kind)
        {
            case DeathStepKind.Effect when job.Step.Fx is { } ground && _fx is not null && IsGroundEffect(ground.Name):
                PlayGroundPlays(run, rd, [ground]);   // Final Vengeance's land effect: a ground effect with decals, like the jump death's impact
                break;
            case DeathStepKind.Effect when job.Step.Fx is { } p && _fx is not null:
            {
                var (position, bone, skeleton) = DeathBone(rd, p.Marker);
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                var axes = AxesForDeath(p, job.NegativeHit);
                Func<(Vector3 Position, Basis Axes)?>? follow = null;
                if (p.Attached)
                    follow = () => IsInstanceValid(rd) && skeleton is not null && IsInstanceValid(skeleton) && bone >= 0
                        ? ((skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone)).Origin, axes) : IsInstanceValid(rd) ? (rd.FocusPoint, axes) : null;
                var tint = job.Wargear.Length > 0 ? RaceTint(job.Wargear, p.Name) : null;
                var e = _fx.Play(p.Name, position, axes, p.Scale <= 0f ? 1f : p.Scale, null, follow, p.Lifetime, 0.0, false, DeathFxPriority, tint: tint);
                _dfxEffectMs = Math.Max(_dfxEffectMs, Since(began));
                if (e is null) { run.Missing++; _deathFxMissing++; if (_fxNoted.Add("deathfx:" + p.Name)) GD.Print($"  effects: death effect '{p.Name}' is not available"); }
                else { run.Effects++; _deathFxEffects++; run.Names[p.Name] = run.Names.GetValueOrDefault(p.Name) + 1; _fxLastShotAt = _connectedFor; _fxLastEnd = position; _fxLastFrom = position; }
                break;
            }
            case DeathStepKind.Sound when job.Step.Sound.Length > 0:
            {
                if (PostDeathSound(job.Step.Sound, DeathBone(rd, "rig_pelvis").Position, run.Own)) { run.Sounds++; _deathFxSounds++; }
                break;
            }
            case DeathStepKind.RemoveWargear:
                run.Removals++; _deathFxRemovals++;   // the corpse's weapon vanishing is not drawn (WeaponRig has no hide); counted for the log
                break;
        }
    }

    /// <summary>The effect's axis (+Y): the data's <c>negative_hit_direction</c> is back along the killing blow; "world" and "up" are straight up.</summary>
    static Basis AxesForDeath(FxPlay p, Vector3? negativeHit) =>
        p.RotationSource != "world" && p.Direction == "negative_hit_direction" && negativeHit is { } v && v.LengthSquared() > 1e-6f ? AxesFromY(v) : Basis.Identity;

    /// <summary>
    /// A marker of the data (a bone of the marine rig) on a corpse: its position (Godot world), the bone and the skeleton. Names are matched ignoring case and underscores
    /// (<see cref="DeathEffectRules.SameBone"/>: the melta death names <c>rig_lhand</c>, <c>rig_rthigh</c>, <c>rig_lcalf</c> where the rig has <c>RIG_L_Hand</c>, <c>RIG_R_Thigh</c>,
    /// <c>RIG_L_Calf</c>); a name the rig does not have at all (<c>rig_chest</c>, <c>rig_spine1</c>) takes the corpse's chest (ours).
    /// </summary>
    (Vector3 Position, int Bone, Skeleton3D? Skeleton) DeathBone(Ragdoll rd, string marker)
    {
        var skeleton = rd.Character?.Skeleton;
        if (skeleton is not null && IsInstanceValid(skeleton) && marker.Length > 0)
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            int bone = BoneOf(skeleton, marker);
            _dfxBoneMs = Math.Max(_dfxBoneMs, Since(began));
            if (bone >= 0) return ((skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone)).Origin, bone, skeleton);
        }
        return (rd.FocusPoint, -1, skeleton);
    }

    /// <summary>
    /// The bone of a marker name, from a cache (the marine rig is the same on every corpse: a name is scanned for once; a hit is confirmed with one bone name, and an entry only counts for a skeleton
    /// with the bone count it was made on). -1: the rig has no such bone.
    /// </summary>
    int BoneOf(Skeleton3D skeleton, string marker)
    {
        int count = skeleton.GetBoneCount();
        if (_deathBones.TryGetValue(marker, out var known) && known.Count == count && (known.Bone < 0 || DeathEffectRules.SameBone(skeleton.GetBoneName(known.Bone), marker))) return known.Bone;
        int found = -1;
        for (int bone = 0; bone < count && found < 0; bone++)
            if (DeathEffectRules.SameBone(skeleton.GetBoneName(bone), marker)) found = bone;
        _deathBones[marker] = (found, count);
        return found;
    }

    /// <summary>The effects the jump death's impact plays: ground effects with collision content (crater, cracks, smoke), also named by Final Vengeance's explosion.</summary>
    bool IsGroundEffect(string name)
    {
        _groundFxNames ??= _deathEffects is { } e ? (e.Marines.JumpImpact?.EffectNames ?? []).Concat(e.Chaos.JumpImpact?.EffectNames ?? []).ToHashSet(StringComparer.Ordinal) : [];
        return _groundFxNames.Contains(name);
    }

    // ---- the jump death's impact ----

    /// <summary>Metres from a Godot-space point down to the level (the collision world the client predicts on); null with no level within 4 m or no map.</summary>
    float? HeightAbove(Vector3 godotPoint)
    {
        if (_client?.Collision is not { } world || _gameSpace is null) return null;
        var g = _gameSpace.GlobalTransform.AffineInverse() * godotPoint;
        return world.Weapons.Raycast(new Num.Vector3(g.X, g.Y, g.Z), -Num.Vector3.UnitZ, 4f) is { } hit ? hit.Distance : null;
    }

    void PlayJumpImpact(JumpImpactWatch w, DeathAction impact)
    {
        PlayGroundPlays(w.Run, w.Ragdoll, impact.Effects.Select(s => s.Fx!).ToList(), impact.Steps.Where(s => s.Kind == DeathStepKind.Sound).Select(s => s.Sound));
        Finish(w.Run, w.Run.Pending);
    }

    /// <summary>
    /// Plays ground effects (with their collision content: crater, cracks, smoke) and their sounds on the level under a corpse's root bone. With a level but none within reach below (a pit,
    /// a kill region) nothing is played: no decals into the void, no impact sound (counted for the log). Without a map (flat ground) the root's place stands in.
    /// </summary>
    void PlayGroundPlays(DeathFxRun run, Ragdoll rd, IReadOnlyList<FxPlay> plays, IEnumerable<string>? sounds = null)
    {
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        // The probe starts at the corpse's chest (as the impact's timing does): a ragdoll's root bone can lie at or under the floor, where a ray down finds nothing.
        var root = rd.FocusPoint;
        var g = _gameSpace is null ? default : _gameSpace.GlobalTransform.AffineInverse() * root;
        var (point, normal, surface) = _gameSpace is null ? (root, Vector3.Up, -1) : GroundUnder(new Num.Vector3(g.X, g.Y, g.Z));
        _dfxGroundMs = Math.Max(_dfxGroundMs, Since(began));
        if (_client?.Collision is not null && surface < 0) { run.NoGround++; return; }
        if (_fx is not null && plays.Count > 0)
        {
            began = System.Diagnostics.Stopwatch.GetTimestamp();
            var started = GroundPoundFx.Play(_fx, plays, point, normal, 0f, null, _catalogue?.SurfaceName(surface) ?? "", run.PlayerId * 17 + _deathFxKills);
            _dfxEffectMs = Math.Max(_dfxEffectMs, Since(began));
            run.Effects += started.Count; _deathFxEffects += started.Count;
            foreach (var e in started) run.Names[e.Name] = run.Names.GetValueOrDefault(e.Name) + 1;
            run.Missing += plays.Count - started.Count; _deathFxMissing += plays.Count - started.Count;
            _fxLastShotAt = _connectedFor; _fxLastEnd = point; _fxLastFrom = point;
        }
        if (sounds is not null)
            foreach (var s in sounds)
                if (PostDeathSound(s, point, run.Own)) { run.Sounds++; _deathFxSounds++; }
    }

    /// <summary>Posts one of the data's death sounds at a place (Godot space); whether the sound bank has the event.</summary>
    bool PostDeathSound(string eventName, Vector3 at, bool own)
    {
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        bool found = _sound?.Post(eventName, new SoundEmitter { Position = at, Label = "death fx", OneShot = true, Own = own }) is { Found: true };
        _dfxSoundMs = Math.Max(_dfxSoundMs, Since(began));
        return found;
    }

    /// <summary>The F1 line about the death effects.</summary>
    string DeathFxInfoLine() =>
        NoDeathFx ? "Death effects: off (--no-death-fx)."
        : _deathEffects is null ? "Death effects: not loaded."
        : $"Death effects (the data's actn_plasma_death, actn_melta_death, actn_jumpdeath_*, Final Vengeance; trigger by the kill's cause is interpretation): {_deathFxKills} death(s) seen, {_deathFxEffects} effect(s) and {_deathFxSounds} sound(s) played, {_deathFxMissing} not available; worst frame: queued in {_deathFxQueueMs:F2} ms, played in {_deathFxPlayMs:F2} ms; " +
          (_deathFxKinds.Count == 0 ? "none yet" : string.Join(", ", _deathFxKinds.Select(kv => $"{kv.Key} {kv.Value}"))) + ".";
}
