using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmClient.ImpactRules;
using SmContent.Extras.Executions;
using SmSim;
using SmSim.Extras.Executions;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Executions sound and gore (owner brief 2026-11-27, card "Original events first"; docs/executions-cues.md): every execution's cues, driven by the shared execution clock. Each live run's cues
/// (<see cref="ExecutionClipLibrary.Cues"/>, the install's own bindings) fire once on every client, attacker, victim and observer alike (<see cref="ExecutionCueTimeline"/>), on three emitters per
/// run: the attacker (voice and body, at the chest), the victim (its proxy's chest: the original's victim entity) and the wound (impacts, grinds and cuts, ours). A sustained sound stops with the
/// data's stop event; an interrupted run (the executor killed, an invalid end, a disconnect, the match's end) stops all three emitters at once and drops its pending cues and effects. The power
/// sword and the knife on a chainsword move post their own swing and hit in place of the chainsword's motor (ours). Blood effects and the kill's streak only where the match's gore allows
/// (<see cref="ExecutionCueRules.BloodAllowed"/>). Reached only through the executions frame (<see cref="ContentExtras.Executions"/>): Classic Faithful and Musou never run it.
/// Test aids: <c>--log-execution-cues</c> (a line per fired cue and per end), <c>--no-execution-cues</c> (silent moves, the shipped behaviour), <c>--no-execution-gore</c> (no blood effect or streak).
/// </summary>
public partial class NetworkView
{
    static readonly bool ExecCueLog = CmdFlag("log-execution-cues") || ExecLog;
    static readonly bool NoExecCues = CmdFlag("no-execution-cues");
    static readonly bool NoExecGore = CmdFlag("no-execution-gore");

    /// <summary>The hit a power sword or knife posts along a borrowed grind repeats this often (seconds, ours: a held cut).</summary>
    const float ExecMotorRepeatSeconds = 0.4f;
    /// <summary>The wound sits this far from the victim's chest toward the attacker (metres, ours, as the melee hit's contact).</summary>
    const float ExecWoundOut = 0.3f;

    /// <summary>Exertion types the move asks for that a character may not have, and the one tried instead (ours): the roar as the big effort, a big hurt as a small one.</summary>
    static readonly Dictionary<string, string> ExecutionSpeechFallback = new(StringComparer.OrdinalIgnoreCase)
    {
        ["roar"] = "effort_big_pull", ["damage_big"] = "damage_small", ["damage_long"] = "damage_small",
    };

    sealed class ExecCueRun
    {
        public required ExecutionRun Run;
        public required ExecutionRow Row;
        public required IReadOnlyList<ExecutionCue> Cues;
        public required SoundEmitter Attacker, Victim, Wound;
        public string? Wargear;
        public readonly List<FxEffect> Effects = new();
        public bool Streaked;
    }

    readonly ExecutionCueTimeline _execCues = new();
    readonly Dictionary<ExecutionCueTimeline.RunKey, ExecCueRun> _execCueRuns = new();
    readonly List<ExecutionCueTimeline.Step> _execCueSteps = new();
    readonly List<ExecutionCueTimeline.RunKey> _execCueGone = new();
    Task<(Dictionary<string, CSystem> Systems, Dictionary<string, byte[]> Decals, List<string> Failures)>? _execCueFxLoad;
    bool _execCueFxReady;
    int _execCueFxNames;

    /// <summary>
    /// The connection is gone (the core's frame returns before the extras run from then on): every run's cues are interrupted at once, so no grind or loop keeps sounding (Fable review 6).
    /// Reached through <c>ExtrasConnectionGone</c> (NetworkView.Executions.cs).
    /// </summary>
    void ExecutionCuesInterruptAll()
    {
        _execCueSteps.Clear();
        _execCues.InterruptAll(_execCueSteps);
        foreach (var step in _execCueSteps) ExecCueApply(step, default, []);
        _execCueSteps.Clear();
    }

    /// <summary>Once a frame from the presenter (also for an observer): starts, advances, finishes and interrupts each run's cues.</summary>
    void ExecutionCuesFrame(PlayerState me, IReadOnlyList<PlayerState> others)
    {
        if (NoExecCues || _execLib.Catalogue is not { } catalogue) return;
        ExecCueFxLoad();
        _execCueSteps.Clear();
        foreach (var run in ExecutionsState.Live.Values)
        {
            if (catalogue.AtWire(run.Wire) is not { } row) continue;
            var key = new ExecutionCueTimeline.RunKey(run.Executor, run.StartTick);
            if (_execCues.IsClosed(key)) continue;   // a run ended for silence and revived by the live list stays closed: no second set of cues or streak (Fable review 5)
            if (!_execCueRuns.TryGetValue(key, out var cr))
            {
                var executor = FindPlayer(run.Executor, me, others);
                string? wargear = executor is { } ex ? MeleeWargear(ex) : null;
                var wield = ExecutionCueRules.WieldOf(row, wargear);
                _execCueRuns[key] = cr = new ExecCueRun
                {
                    Run = run, Row = row, Cues = _execLib.Cues.For(row.Id, wield), Wargear = wargear,
                    // One-shot emitters: the sound system forgets their container histories once their voices end (Fable review 3), a live grind keeps them until its stop.
                    Attacker = new SoundEmitter { Label = $"execution P{run.Executor} attacker", OneShot = true },
                    Victim = new SoundEmitter { Label = $"execution P{run.Executor} victim", OneShot = true },
                    Wound = new SoundEmitter { Label = $"execution P{run.Executor} wound", OneShot = true },
                };
                cr.Attacker.Follow = () => ExecAttackerAt(cr);
                cr.Victim.Follow = () => ExecVictimAt(cr, head: false);
                cr.Wound.Follow = () => ExecWoundAt(cr, head: false);
                if (ExecCueLog) GD.Print($"EXECCUES t {_connectedFor:F2} s: start P{run.Executor} {row.Id} wield {wield} ({wargear ?? "-"}): {cr.Cues.Count} cues");
            }
            _execCues.Advance(key, cr.Cues, ExecCueClock(cr), _execCueSteps);
            ExecCueStreak(cr, me);
        }
        foreach (var ended in ExecutionsState.Ended)
        {
            var key = new ExecutionCueTimeline.RunKey(ended.Run.Executor, ended.Run.StartTick);
            if (!_execCueRuns.TryGetValue(key, out var cr) || !_execCues.Has(key)) continue;
            if (ended.Reason == ExecutionEndReason.Done) { _execCues.Finish(key); _execCues.Advance(key, cr.Cues, ExecCueClock(cr), _execCueSteps); ExecCueStreak(cr, me); }
            else _execCues.Interrupt(key, _execCueSteps);
        }
        // A run neither live nor ended (cleared, or an ended entry already pruned while it was still finishing): finished runs play out, others stop.
        _execCueGone.Clear();
        foreach (var key in _execCues.Runs)
        {
            if (ExecutionsState.TryLive(key.Executor, out var live) && live.StartTick == key.StartTick) continue;
            bool ended = false;
            foreach (var e in ExecutionsState.Ended) if (e.Run.Executor == key.Executor && e.Run.StartTick == key.StartTick) { ended = true; break; }
            if (!ended) _execCueGone.Add(key);
        }
        foreach (var key in _execCueGone)
        {
            if (_execCueRuns.TryGetValue(key, out var cr) && ExecCueClock(cr) <= cr.Row.Seconds + 0.05f) _execCues.Interrupt(key, _execCueSteps);
            else if (cr is not null) { _execCues.Finish(key); _execCues.Advance(key, cr.Cues, ExecCueClock(cr), _execCueSteps); }
            else _execCues.Interrupt(key, _execCueSteps);
        }
        foreach (var step in _execCueSteps) ExecCueApply(step, me, others);
    }

    /// <summary>The run's clock (seconds of the move), as the presenter poses it: the executor's draw tick.</summary>
    float ExecCueClock(ExecCueRun cr) => ExecutionPresentMath.Pose(cr.Run, cr.Row, ExecDrawTick(cr.Run.Executor), ExecTickRate).ClipSeconds;

    static PlayerState? FindPlayer(int id, PlayerState me, IReadOnlyList<PlayerState> others)
    {
        if (me.PlayerId == id && me.PlayerId != 0) return me;
        foreach (var o in others) if (o.PlayerId == id) return o;
        return null;
    }

    CharacterViewer? ExecCharacter(int id) => _client is not null && id == _client.PlayerId ? _local : _remotes.GetValueOrDefault(id);

    CharacterViewer? ExecProxyViewer(ExecCueRun cr) =>
        _execProxies.TryGetValue((cr.Run.Executor, cr.Run.StartTick), out var p) && p.Viewer is { } v && IsInstanceValid(v) ? v : null;

    Vector3? ExecAttackerAt(ExecCueRun cr) =>
        ExecCharacter(cr.Run.Executor) is { } c && IsInstanceValid(c) ? c.GlobalPosition + Vector3.Up * 1.3f : ToGodot(cr.Run.Origin + new Num.Vector3(0f, 0f, 1.3f));

    Vector3? ExecVictimAt(ExecCueRun cr, bool head)
    {
        if (ExecProxyViewer(cr) is { } v)
        {
            float s = v.Scale.Y > 0f ? v.Scale.Y : 1f;
            if (v.Skeleton is { } sk && ExecBone(sk, head) is int bone and >= 0) return (sk.GlobalTransform * sk.GetBoneGlobalPose(bone)).Origin;
            return v.GlobalPosition + Vector3.Up * ((head ? 1.75f : 1.3f) * s);
        }
        return ToGodot(cr.Run.VictimAt + new Num.Vector3(0f, 0f, head ? 1.75f : 1.3f));
    }

    static readonly string[] ExecHeadBones = ["RIG_Head", "RIG_Neck", "Bip01 Head"], ExecChestBones = ["RIG_SpineB", "RIG_SpineA", "RIG_Spine1", "Bip01 Spine1"];

    static int ExecBone(Skeleton3D sk, bool head)
    {
        foreach (var n in head ? ExecHeadBones : ExecChestBones) { int b = sk.FindBone(n); if (b >= 0) return b; }
        return -1;
    }

    /// <summary>The wound (ours): the victim's chest (head for a head, neck or decap cue) brought <see cref="ExecWoundOut"/> toward the attacker.</summary>
    Vector3? ExecWoundAt(ExecCueRun cr, bool head)
    {
        if (ExecVictimAt(cr, head) is not { } v) return null;
        var a = ExecAttackerAt(cr) ?? v;
        var toward = a - v; toward.Y = 0f;
        return toward.LengthSquared() > 1e-4f ? v + toward.Normalized() * ExecWoundOut : v;
    }

    /// <summary>A bone by the data's marker name, case aside (the files write <c>rig_r_hand</c>, the rigs <c>RIG_R_Hand</c>); -1 when the skeleton has none.</summary>
    static int ExecFindBone(Skeleton3D sk, string marker)
    {
        int exact = sk.FindBone(marker);
        if (exact >= 0 || marker.Length == 0) return exact;
        for (int i = 0; i < sk.GetBoneCount(); i++)
            if (string.Equals(sk.GetBoneName(i), marker, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The move's first contact: its first sound or motor hit or grind at the wound (seconds), else the row's contact event.</summary>
    static float ExecFirstContact(ExecCueRun cr)
    {
        foreach (var c in cr.Cues)
            if (c.Place == ExecutionCuePlace.Wound && c.Kind is ExecutionCueKind.Sound or ExecutionCueKind.Motor && c.Motor != ExecutionMotorRole.Rev) return c.At;
        return cr.Row.At(ExecutionEventKind.Contact) ?? cr.Row.KillSeconds;
    }

    static bool ExecHeadCue(ExecutionCue c)
    {
        string t = (c.Tag + " " + c.Event).ToLowerInvariant();
        return t.Contains("head") || t.Contains("decap") || t.Contains("neck") || t.Contains("throat") || t.Contains("face");
    }

    void ExecCueApply(ExecutionCueTimeline.Step step, PlayerState me, IReadOnlyList<PlayerState> others)
    {
        if (!_execCueRuns.TryGetValue(step.Run, out var cr)) return;
        switch (step.Kind)
        {
            case ExecutionCueTimeline.StepKind.Fire when step.Cue is { } cue:
                ExecCueFire(cr, cue, me, others);
                break;
            case ExecutionCueTimeline.StepKind.Interrupt:
                ExecCueStop(cr, interrupted: true);
                _execCueRuns.Remove(step.Run);
                break;
            case ExecutionCueTimeline.StepKind.Finish:
                ExecCueStop(cr, interrupted: false);
                _execCueRuns.Remove(step.Run);
                break;
        }
    }

    /// <summary>
    /// The run's end: an interrupted run's three emitters stop at once (every sustained sound and every pending voice) and its effects end; a finished run's loops end (the data's instant
    /// tags' stop events wait for the state's end, interpretation), its one-shots play out.
    /// </summary>
    void ExecCueStop(ExecCueRun cr, bool interrupted)
    {
        if (_sound is not null)
            foreach (var e in new[] { cr.Attacker, cr.Victim, cr.Wound })
            {
                if (interrupted) _sound.StopEmitter(e);
                else _sound.StopLoops(e);
            }
        if (interrupted)
            foreach (var fx in cr.Effects) { fx.Repeat = false; fx.Life = Math.Min(fx.Life, Math.Max(0.0, fx.Age) + 0.05); }
        cr.Effects.Clear();
        if (ExecCueLog) GD.Print($"EXECCUES t {_connectedFor:F2} s: {(interrupted ? "interrupted" : "finished")} P{cr.Run.Executor} {cr.Row.Id}: {(interrupted ? "emitters stopped, pending cues dropped" : "loops ended")}");
    }

    SoundEmitter ExecPlace(ExecCueRun cr, ExecutionCuePlace place) => place switch { ExecutionCuePlace.Victim => cr.Victim, ExecutionCuePlace.Wound => cr.Wound, _ => cr.Attacker };

    void ExecCueFire(ExecCueRun cr, ExecutionCue cue, PlayerState me, IReadOnlyList<PlayerState> others)
    {
        string result = "";
        switch (cue.Kind)
        {
            case ExecutionCueKind.Sound:
                if (_sound is null || NoSound) { result = "no sound"; break; }
                result = _sound.Post(cue.Event, ExecPlace(cr, cue.Place)) is { Found: true } ? "posted" : "not in the banks";
                break;
            case ExecutionCueKind.Stop:
                if (_sound is null || NoSound) { result = "no sound"; break; }
                // A stop acts on the emitter that played its sound: posted on the cue's place and on the wound (a grind there).
                _sound.Post(cue.Event, ExecPlace(cr, cue.Place));
                if (cue.Place != ExecutionCuePlace.Wound) _sound.Post(cue.Event, cr.Wound);
                result = "stop posted";
                break;
            case ExecutionCueKind.Speech:
                result = ExecCueSpeech(cr, cue, me, others);
                break;
            case ExecutionCueKind.Motor:
                result = ExecCueMotor(cr, cue);
                break;
            case ExecutionCueKind.Effect:
                result = ExecCueEffect(cr, cue);
                break;
        }
        if (ExecCueLog) GD.Print($"EXECCUES t {_connectedFor:F2} s: P{cr.Run.Executor} {cr.Row.Id} {cue.At:F3} s {cue.Side} {cue.Kind} {cue.Place} {cue.Tag} {cue.Event} ({cue.File}): {result}");
    }

    string ExecCueSpeech(ExecCueRun cr, ExecutionCue cue, PlayerState me, IReadOnlyList<PlayerState> others)
    {
        if (_sound is null || NoSound || cue.Speech is not { } b) return "no sound";
        string? character;
        if (cue.NamedCharacter) character = b.Character;
        else if (cue.Place != ExecutionCuePlace.Victim) character = FindPlayer(cr.Run.Executor, me, others) is { } ex ? SpeechCharacterOf(ex) : null;
        else character = (cr.Run.TargetKind == ExecutionTargetKind.Player && FindPlayer(cr.Run.Target, me, others) is { } v ? SpeechCharacterOf(v) : null)
                         ?? ExecutionCueRules.VictimFiles(cr.Row.Target).Select(f => _execLib.Cues.Characters.GetValueOrDefault(f)).FirstOrDefault(c => c is not null);   // an enemy (or a player not in view): its blueprint's voice
        if (character is null) return "no speech character";
        var emitter = ExecPlace(cr, cue.Place);
        _sound.StopSpeech(emitter);   // the move's lines follow each other: a new line takes the place of the last (as the Ork's death yell does)
        var result = _sound.PostSpeech(b, character, emitter);
        // A line the character has no recording of (the MP marines have no "roar"): the nearest it has (ours, ExecutionSpeechFallback).
        if (result == SpeechResult.NoLine && ExecutionSpeechFallback.GetValueOrDefault(b.Type) is { } other)
            return $"speech {character}: {b.Type} none, {other} {_sound.PostSpeech(b with { Type = other }, character, emitter)}";
        return $"speech {character}: {result}";
    }

    /// <summary>A borrowed weapon's motor sound replaced (ours): the wielder's own swing for a swing, its hit for a hit and along a grind (repeated), nothing for a rev.</summary>
    string ExecCueMotor(ExecCueRun cr, ExecutionCue cue)
    {
        if (_sound is null || NoSound || _soundTriggers is null || cr.Wargear is not { } wargear) return "no sound";
        switch (cue.Motor)
        {
            case ExecutionMotorRole.Swing:
                var file = _soundTriggers.File(wargear);
                string? swing = file?.Tag("swing_x1").FirstOrDefault(s => s.StartEvent.Length > 0)?.StartEvent ?? file?.Tag("actn_snd_ck_swing").FirstOrDefault(s => s.StartEvent.Length > 0)?.StartEvent;
                if (swing is null) return "the wielder has no swing sound";
                _sound.Post(swing, cr.Attacker);
                return "wielder swing " + swing;
            case ExecutionMotorRole.Hit:
            case ExecutionMotorRole.Grind:
                var hits = ExecutionWeapons.FamilyOf(wargear) == ExecutionWeapons.CombatKnife
                    ? new List<string> { ExecutionCueRules.KnifeHit }   // the campaign knife's stab, not the MP light strike's fist hit (re-review N2)
                    : _soundTriggers.MeleeHitEvents(wargear, _soundTriggers.MeleeHitAbility(wargear, false, 1), false)
                        .Where(h => !h.Contains("bull_rush", StringComparison.OrdinalIgnoreCase)).ToList();   // the light strike's blade hit, not its charge's body smash
                if (hits.Count == 0) return "the wielder has no hit sound";
                int repeats = cue.Motor == ExecutionMotorRole.Grind ? Math.Max(1, (int)MathF.Ceiling((cue.Until - cue.At) / ExecMotorRepeatSeconds)) : 1;
                for (int i = 0; i < repeats; i++) foreach (var h in hits) _sound.Post(h, cr.Wound, extraDelay: i * ExecMotorRepeatSeconds);
                return $"wielder hit {string.Join("+", hits)} x{repeats}";
            default:
                return "rev dropped (no motor on this blade)";
        }
    }

    string ExecCueEffect(ExecCueRun cr, ExecutionCue cue)
    {
        if (cue.Fx is not { } p) return "no effect";
        if (cue.Blood && !ExecutionCueRules.BloodAllowed(MatchExtras, NoExecGore)) return "blood off";
        if (_fx is not { IsReady: true } fx || !_execCueFxReady) return "effects not ready";
        string marker = p.Marker;
        Func<(Vector3 Position, Basis Axes)?>? follow = null;
        if (cue.Side == ExecutionCueSide.Attacker && ExecCharacter(cr.Run.Executor) is { } c && IsInstanceValid(c))
        {
            if (c.Weapons is { } rig && rig.TryMeleeMarker(marker, out _))
                follow = () => ExecCharacter(cr.Run.Executor) is { Weapons: { } r } && r.TryMeleeMarker(marker, out var t) ? (t.Origin, t.Basis) : null;
            else if (c.Skeleton is { } sk && ExecFindBone(sk, marker) is int bone and >= 0)
                follow = () => IsInstanceValid(sk) && sk.IsInsideTree() ? ((sk.GlobalTransform * sk.GetBoneGlobalPose(bone)).Origin, Basis.Identity) : null;
        }
        else if (cue.Side == ExecutionCueSide.Victim && ExecProxyViewer(cr) is { Skeleton: { } vs } && ExecFindBone(vs, marker) is int vb and >= 0)
            follow = () => IsInstanceValid(vs) && vs.IsInsideTree() ? ((vs.GlobalTransform * vs.GetBoneGlobalPose(vb)).Origin, Basis.Identity) : null;
        // A blade-borne effect whose marker this blade lacks, before the move's first contact (O4's blood_pre_grind at 0.300 s on a knife or sword): dropped, not moved to the wound, where it
        // would show blood on the victim before anything touches him (re-review N4). From the first contact on (within 0.1 s), the wound is the right place for the cut's blood.
        if (follow is null && cue.Side == ExecutionCueSide.Attacker && marker.Length > 0 && cue.At < ExecFirstContact(cr) - 0.1f) return $"dropped (no {marker} on this blade or body, before the first contact)";
        bool head = ExecHeadCue(cue), atWound = follow is null;
        follow ??= () => ExecWoundAt(cr, head) is { } w ? (w, Basis.Identity) : null;   // a marker this body or blade lacks (the chainsword's teeth on a sword): at the wound
        if (follow() is not { } at) return "no place";
        float life = p.Lifetime > 0f ? p.Lifetime : cue.Until > cue.At ? cue.Until - cue.At + 0.2f : 0f;
        var e = fx.Play(cue.Event, at.Position, at.Axes, p.Scale <= 0f ? 1f : p.Scale, null, p.Attached ? follow : null, life, 0.0, priority: 1);
        if (e is null) return "effect not compiled";
        cr.Effects.Add(e);
        return atWound ? $"effect at the wound ({(marker.Length > 0 ? $"no {marker} on this {(cue.Side == ExecutionCueSide.Attacker ? "blade or body" : "body")}" : "no marker")})" : $"effect at {marker}";
    }

    /// <summary>The kill's streak (ours, the current gore streak reused): one at the wound when the clock passes the row's kill frame, where the streak and the gore draw.</summary>
    void ExecCueStreak(ExecCueRun cr, PlayerState me)
    {
        float clock = ExecCueClock(cr);
        if (cr.Streaked || clock < cr.Row.KillSeconds) return;
        cr.Streaked = true;
        if (clock - cr.Row.KillSeconds > ExecutionCueTimeline.LateSkipSeconds) return;   // first seen past the kill frame (a late join): no streak, as the cues' late skip (Fable review 10)
        if (!StreakDraws || !ExecutionCueRules.BloodAllowed(MatchExtras, NoExecGore) || ExecWoundAt(cr, false) is not { } wound || ExecAttackerAt(cr) is not { } a) return;
        var axis = wound - a; axis.Y = 0f;
        if (axis.LengthSquared() < 1e-4f) axis = Vector3.Forward;
        axis = (axis.Normalized() + Vector3.Down * 0.6f).Normalized();
        int seed = NextStreakSeed();
        bool heavy = string.Equals(cr.Row.Weapon, ExecutionWeapons.ThunderHammer, StringComparison.OrdinalIgnoreCase);
        SpawnStreak(wound, axis, StreakPlane(GoreStreakRules.Source.Sweep, axis, seed), 2f, 1f, 0f, 0f, lethal: true, heavy: heavy, seed: seed, GoreStreakRules.Source.Sweep,
            victim: cr.Run.TargetKind == ExecutionTargetKind.Player ? cr.Run.Target : 0, own: _client is not null && cr.Run.Target == _client.PlayerId && cr.Run.TargetKind == ExecutionTargetKind.Player);
        if (ExecCueLog) GD.Print($"EXECCUES t {_connectedFor:F2} s: P{cr.Run.Executor} {cr.Row.Id} kill streak at the wound");
    }

    /// <summary>The cues' effects are not in the weapons' catalogue: read and compiled once on a worker when the effect player is up (as Musou's guns).</summary>
    void ExecCueFxLoad()
    {
        if (_execCueFxReady || _fx is not { IsReady: true, Library: { } library } || !_execLib.Ready) return;
        var sheet = _execLib.Cues;
        _execCueFxLoad ??= Task.Run(() =>
        {
            // The names on the worker, once (Fable review 8: not every frame until the compile ends).
            var names = sheet.All.SelectMany(x => x.Cues).Where(c => c.Kind == ExecutionCueKind.Effect).Select(c => c.Event).Distinct(StringComparer.Ordinal).ToArray();
            _execCueFxNames = names.Length;
            var failures = new List<string>();
            var systems = CompileSystems(library, names, failures);
            var decals = library.Textures(FxPlayer.DecalNames(systems.Values));
            return (systems, decals, failures);
        });
        if (!_execCueFxLoad.IsCompleted) return;
        _execCueFxReady = true;
        if (_execCueFxLoad.IsFaulted) { GD.PrintErr("execution cues: effects: " + _execCueFxLoad.Exception?.GetBaseException().Message); return; }
        var (compiled, decalBytes, failed) = _execCueFxLoad.Result;
        _fx.Preload(compiled, decalBytes);
        GD.Print($"execution cues: {compiled.Count} of {_execCueFxNames} effects compiled{(failed.Count > 0 ? "; failed: " + string.Join(", ", failed.Take(6)) : "")}");
    }
}
