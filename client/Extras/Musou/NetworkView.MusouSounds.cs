using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmFormats.Audio;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Added content "Musou": the enemies' own sounds, original events only, through <see cref="SoundSystem"/> so the data's mix applies (no gain, unless <c>--musou-ork-gain</c> asks). What each type sounds is
/// read from its files (<see cref="MusouSoundSet"/>: the Boy, Shoota Boy, 'Ard Boy, Gretchin, Nob and Warboss each have their own steps, weapons and voice); when is the client's reading of the crowd:
/// <list type="bullet">
/// <item>The nearest enemies (<see cref="MusouSoundRules.Slots"/>) hold an emitter that follows them; their state changes give the wind-up, swing, stun, knock-down and landing, the ground they cover gives
/// footsteps (surface from the map, speed from their motion), and timers the running grunts and barks. The crowd beyond them is silent.</item>
/// <item>The events give the rest: a tracer the shot and its impact, a strike the hit on the target, a hit the grunt, a death the yell and the fall, the boss's telegraphs his abilities.</item>
/// <item>Everything passes <see cref="MusouSoundBudget"/>, and every post is <c>limited</c>, so the data's instance limits hold too. Nothing else stops the crowd's sum from reaching the voice pool.</item>
/// </list>
/// Test aids: <c>--no-musou-ork-sounds</c>; <c>--musou-ork-gain &lt;dB&gt;</c> (a listening aid, default 0); <c>--log-sound musou</c> (a MUSOU SOUND line per post). Every 10 s a <c>musou sounds:</c> line gives the
/// voices (live and peak) and each group's posts and holds. An event of the data no bank holds is printed once at start-up and stays silent.
/// </summary>
public partial class NetworkView
{
    /// <summary><see cref="SoundEmitter.Group"/> of the crowd's emitters.</summary>
    const string MusouSoundEmitters = "musou enemies";

    /// <summary>One emitter bound to a near enemy.</summary>
    sealed class MusouSoundSlot
    {
        public readonly SoundEmitter Emitter = new() { Group = MusouSoundEmitters };
        public ushort Enemy;
        public MusouSoundSet Set = null!;
        public MusouEnemyState State;
        public Num.Vector3 Last;
        public float Walked;
        public bool LeftFoot;
        public double NextGrunt, NextBark, NextSurface, LastHurt, Unwanted;
        public bool Bound => Enemy != 0;
    }

    readonly MusouSoundSlot[] _musouSlots = Enumerable.Range(0, MusouSoundRules.Slots).Select(i => new MusouSoundSlot { Emitter = { Label = $"musou slot {i}" } }).ToArray();
    readonly MusouSoundBudget _musouBudget = new();
    readonly Random _musouSoundRoll = new(7);
    readonly List<(float Distance, int Index)> _musouNear = new();
    readonly Dictionary<ushort, int> _musouRank = new();
    Task<SoundTriggers>? _musouSoundLoad;
    MusouSoundSet?[]? _musouSets;
    bool _musouSoundFailed;
    double _musouChooseAt, _musouSoundLogAt;
    int _musouLiveVoices, _musouLiveSpeech, _musouPeakVoices, _musouPeakSpeech, _musouAllPeakStart;
    bool _musouOwnAlive = true;

    bool MusouOrkSoundsOff => _musouOrkSoundsOff ??= CmdFlag("no-musou-ork-sounds");
    bool? _musouOrkSoundsOff;
    VoiceOffset MusouOrkOffset => _musouOrkOffset ??= new VoiceOffset(float.TryParse(CmdArg("musou-ork-gain"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float db) && float.IsFinite(db) ? db : 0f);
    VoiceOffset? _musouOrkOffset;

    /// <summary>Reads the enemies' files on a worker the first time, then builds their sets and decodes ahead. False until they are ready.</summary>
    bool MusouSoundsReady()
    {
        if (_musouSets is not null) return true;
        if (_musouSoundFailed || _audio is null || _sound is null) return false;
        string gameDir = _gameDir;
        _musouSoundLoad ??= Task.Run(() => SoundTriggers.Load(gameDir, MusouSoundSet.Wargear, MusouSoundSet.Blueprints, (IReadOnlyList<SmFormats.Havok.BehaviorGraph>?)null));
        if (!_musouSoundLoad.IsCompleted) return false;
        if (_musouSoundLoad.IsFaulted)
        {
            _musouSoundFailed = true;
            GD.PrintErr($"musou sounds: the enemies' files could not be read ({_musouSoundLoad.Exception?.GetBaseException().Message}); they stay silent");
            return false;
        }
        var triggers = _musouSoundLoad.Result;
        foreach (var n in triggers.Notes) GD.Print("musou sounds: note: " + n);
        var sets = new MusouSoundSet?[(int)MusouEnemyType.Warboss + 1];
        var events = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var files in MusouSoundSet.Files)
        {
            var set = sets[(int)files.Type] = MusouSoundSet.Build(files, triggers, _audio.HasEvent);
            foreach (var (_, cue) in set.Cues) foreach (var o in cue.Options) foreach (var b in o.Events) events.Add(b.StartEvent);
            string missing = set.Missing.Count == 0 ? "" : $"; not in the banks: {string.Join(", ", set.Missing)}";
            GD.Print($"musou sounds: {files.Type}: character {set.Character ?? "none"}, {set.Cues.Count(c => !c.Cue.Empty)} cues; no cue for {string.Join(", ", set.Cues.Where(c => c.Cue.Empty).Select(c => c.Name))}{missing}");
        }
        var wems = events.SelectMany(_audio.WemsOf).ToHashSet();
        foreach (var set in sets.OfType<MusouSoundSet>())
            if (set.Character is { } character)
                foreach (var (_, cue) in set.Cues)
                    foreach (var o in cue.Options)
                        foreach (var l in o.Lines)
                            for (int k = 0; k < 4; k++)
                                foreach (var v in _audio.PostSpeech(l.Kind, character, l.Type, l.State, l.Threat.Length == 0 || l.Threat.Equals("autodetect", StringComparison.OrdinalIgnoreCase) ? "high" : l.Threat, new SoundSwitches()).Voices)
                                    wems.Add(v.WemId);
        _sound.Prefetch(wems);
        GD.Print($"musou sounds: ready, prefetch {wems.Count} wems of {events.Count} events");
        _musouAllPeakStart = _sound.PeakActive;
        _musouSets = sets;
        return true;
    }

    /// <summary>This frame's enemy sounds (a client that draws, before the crowd state's new events are taken).</summary>
    void MusouSoundsFrame(PlayerState me, IReadOnlyList<MusouShown> shown, float delta)
    {
        if (MusouOrkSoundsOff || _camera is not { } cam || !MusouSoundsReady() || _sound is null) return;
        double now = _connectedFor;
        var camera = new Num.Vector3(cam.GlobalPosition.X, -cam.GlobalPosition.Z, cam.GlobalPosition.Y);   // Godot to game space, as MusouPresent
        _musouBudget.Refill(delta);
        (_musouLiveVoices, _musouLiveSpeech) = _sound.GroupVoices(MusouSoundEmitters);
        _musouPeakVoices = Math.Max(_musouPeakVoices, _musouLiveVoices);
        _musouPeakSpeech = Math.Max(_musouPeakSpeech, _musouLiveSpeech);
        if (now >= _musouChooseAt) { _musouChooseAt = now + MusouSoundRules.ChooseEvery; MusouChooseSlots(shown, camera, now); }
        foreach (var slot in _musouSlots) if (slot.Bound) MusouSlotFrame(slot, shown, camera, now);

        var events = _musouState.NewEvents;
        for (int i = 0; i < events.Count; i++) MusouSoundEvent(events[i], shown, camera, now);
        var deaths = _musouState.NewDeaths;
        for (int i = 0; i < deaths.Count; i++) MusouSoundDeath(deaths[i], camera);
        // A Marine down: the nearest Ork with something to say about it.
        if (_musouOwnAlive && !me.Alive && _musouNear.Count > 0 && shown.Count > _musouNear[0].Index && MusouSetOf(shown[_musouNear[0].Index].Type) is { } nearest
            && MusouSlotOf(shown[_musouNear[0].Index].Id) is { } speaker) MusouCue(nearest.Celebrate, nearest, speaker.Emitter, MusouSoundGroup.Celebrate, MathF.Sqrt(_musouNear[0].Distance), "celebrate");
        _musouOwnAlive = me.Alive;
        if (now >= _musouSoundLogAt) { _musouSoundLogAt = now + 10.0; MusouSoundLog(shown.Count); }
    }

    MusouSoundSet? MusouSetOf(MusouEnemyType type) => (int)type < _musouSets!.Length ? _musouSets[(int)type] : null;

    MusouSoundSlot? MusouSlotOf(ushort enemy)
    {
        foreach (var s in _musouSlots) if (s.Enemy == enemy) return s;
        return null;
    }

    /// <summary>Gives the free emitters to the nearest enemies (the Warboss first); one that left the nearest keeps its emitter a moment and while it speaks.</summary>
    void MusouChooseSlots(IReadOnlyList<MusouShown> shown, Num.Vector3 camera, double now)
    {
        _musouNear.Clear();
        float range = MusouSoundRules.SlotRange * MusouSoundRules.SlotRange;
        for (int i = 0; i < shown.Count; i++)
        {
            var e = shown[i];
            if (e.State == MusouEnemyState.Dying) continue;
            float d = Num.Vector3.DistanceSquared(e.Position, camera);
            if (e.Type == MusouEnemyType.Warboss) d = 0f;
            else if (d > range) continue;
            _musouNear.Add((d, i));
        }
        _musouNear.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        _musouRank.Clear();
        for (int k = 0; k < _musouNear.Count && k < MusouSoundRules.Slots; k++) _musouRank[shown[_musouNear[k].Index].Id] = k;
        foreach (var slot in _musouSlots)
        {
            if (!slot.Bound) continue;
            if (_musouRank.ContainsKey(slot.Enemy)) slot.Unwanted = now;
            else if (now - slot.Unwanted >= MusouSoundRules.SlotHold && !_sound!.SpeechBusy(slot.Emitter)) slot.Enemy = 0;
        }
        for (int k = 0; k < _musouNear.Count && k < MusouSoundRules.Slots; k++)
        {
            var e = shown[_musouNear[k].Index];
            if (MusouSlotOf(e.Id) is not null || MusouSetOf(e.Type) is not { } set) continue;
            var slot = Array.Find(_musouSlots, s => !s.Bound);
            if (slot is null) break;
            slot.Enemy = e.Id; slot.Set = set; slot.State = e.State; slot.Last = e.Position; slot.Walked = 0f; slot.Unwanted = now; slot.LastHurt = double.NegativeInfinity;
            slot.NextGrunt = now + _musouSoundRoll.NextDouble() * MusouSoundRules.GruntEveryMax;
            slot.NextBark = now + _musouSoundRoll.NextDouble() * MusouSoundRules.BarkEveryMax;
            slot.NextSurface = now;
            slot.Emitter.Position = ToGodot(e.Position) + Vector3.Up * 1.2f;
        }
    }

    double MusouNext(float min, float max) => min + _musouSoundRoll.NextSingle() * (max - min);

    /// <summary>One bound enemy's frame: the emitter follows it; its state and the ground it covers make its sounds.</summary>
    void MusouSlotFrame(MusouSoundSlot slot, IReadOnlyList<MusouShown> shown, Num.Vector3 camera, double now)
    {
        int i = MusouFind(shown, slot.Enemy);
        if (i < 0) { slot.Enemy = 0; return; }
        var e = shown[i];
        var set = slot.Set;
        var emitter = slot.Emitter;
        float stepped = new Num.Vector2(e.Position.X - slot.Last.X, e.Position.Y - slot.Last.Y).Length();
        if (stepped > 3f) stepped = 0f;   // a jump in the records is not a walk
        slot.Last = e.Position;
        emitter.Position = ToGodot(e.Position) + Vector3.Up * 1.2f;
        emitter.Parameters[VoiceMixer.Speed] = (_config?.MusouRules ?? MusouRules.Default).Types[(int)e.Type].Speed;
        float distance = Num.Vector3.Distance(e.Position, camera);

        if (e.State != slot.State)
        {
            var was = slot.State;
            slot.State = e.State;
            switch (e.State)
            {
                case MusouEnemyState.WindUp:
                    MusouCue(set.WindUp, set, emitter, MusouSoundGroup.Swing, distance, "wind-up");
                    MusouCue(set.Effort, set, emitter, MusouSoundGroup.Effort, distance, "effort");
                    break;
                case MusouEnemyState.Strike:
                    MusouCue(set.Swing, set, emitter, MusouSoundGroup.Swing, distance, "swing");
                    break;
                case MusouEnemyState.Stagger:
                    MusouCue(set.Stun, set, emitter, MusouSoundGroup.Stun, distance, "stun");
                    break;
                case MusouEnemyState.Knocked:
                    MusouCue(set.Knocked, set, emitter, MusouSoundGroup.Bodyfall, distance, "knocked");
                    break;
                case MusouEnemyState.Seek when was == MusouEnemyState.Spawning:
                    MusouCue(set.Land, set, emitter, MusouSoundGroup.Bodyfall, distance, "land");
                    MusouCue(set.Roar, set, emitter, MusouSoundGroup.Bark, distance, "spawn roar");
                    break;
            }
        }
        if (e.State != MusouEnemyState.Seek) return;
        slot.Walked += stepped;
        if (slot.Walked >= MusouSoundRules.StepMetres(e.Type))
        {
            slot.Walked = 0f;
            slot.LeftFoot = !slot.LeftFoot;
            if (MusouSoundRules.HeavyStep(e.Type) || _musouRank.GetValueOrDefault(e.Id, int.MaxValue) < MusouSoundRules.Stepping)
            {
                if (now >= slot.NextSurface) { slot.NextSurface = now + 1.0; MusouSurface(emitter, e.Position); }
                MusouCue(slot.LeftFoot ? set.StepLeft : set.StepRight, set, emitter, MusouSoundGroup.Footstep, distance, "step");
            }
        }
        if (stepped > 0f && now >= slot.NextGrunt)
        {
            slot.NextGrunt = now + MusouNext(MusouSoundRules.GruntEveryMin, MusouSoundRules.GruntEveryMax);
            MusouCue(set.Grunt, set, emitter, MusouSoundGroup.Grunt, distance, "grunt");
        }
        if (now >= slot.NextBark)
        {
            slot.NextBark = now + MusouNext(MusouSoundRules.BarkEveryMin, MusouSoundRules.BarkEveryMax);
            MusouCue(set.Bark, set, emitter, MusouSoundGroup.Bark, distance, "bark");
        }
    }

    /// <summary>The surface under an enemy, for its footsteps' switch.</summary>
    void MusouSurface(SoundEmitter emitter, Num.Vector3 at)
    {
        string name = MaterialEffectTable.DefaultKey;
        if (_client?.Collision is { } world && world.Weapons.Raycast(at + new Num.Vector3(0, 0, 0.5f), new Num.Vector3(0, 0, -1), 2.5f) is { } hit)
            name = Surfaces.NameOf(world.Weapons.MaterialOf(hit.Triangle));
        emitter.Switches.Set(AudioCatalog.MaterialType, Fnv.Hash32(name));
    }

    /// <summary>A sound at one place (a shot, a hit, a fall), on an emitter of its own; none is made for what the budget would refuse by distance.</summary>
    void MusouCueAt(MusouSoundCue cue, MusouSoundSet set, Num.Vector3 at, MusouSoundGroup group, float distance, string what, string surface = MaterialEffectTable.DefaultKey)
    {
        if (cue.Empty || distance > MusouSoundRules.LimitOf(group).Range) return;
        var emitter = new SoundEmitter { Position = ToGodot(at) + Vector3.Up * 1.2f, OneShot = true, Label = "musou " + what, Group = MusouSoundEmitters };
        emitter.Switches.Set(AudioCatalog.MaterialType, Fnv.Hash32(surface));
        MusouCue(cue, set, emitter, group, distance, what);
    }

    /// <summary>What the crowd's events sound like.</summary>
    void MusouSoundEvent(in MusouEvent e, IReadOnlyList<MusouShown> shown, Num.Vector3 camera, double now)
    {
        int i = MusouFind(shown, e.Enemy);
        if (i < 0 || MusouSetOf(shown[i].Type) is not { } set) return;
        var enemy = shown[i];
        float distance = Num.Vector3.Distance(enemy.Position, camera);
        switch (e.Kind)
        {
            case MusouEventKind.Tracer:
                if (MusouSlotOf(enemy.Id) is { } shooter) MusouCue(set.Fire, set, shooter.Emitter, MusouSoundGroup.Gun, distance, "fire");
                else MusouCueAt(set.Fire, set, enemy.Position, MusouSoundGroup.Gun, distance, "fire");
                MusouCueAt(set.GunImpact, set, e.Vector, MusouSoundGroup.GunImpact, Num.Vector3.Distance(e.Vector, camera), "gun impact", e.Player != 0 ? "armour" : MaterialEffectTable.DefaultKey);
                break;
            case MusouEventKind.Strike:
                var cue = e.Value switch { (byte)MusouBossAbility.GroundSlam => set.SlamHit, (byte)MusouBossAbility.Charge => set.ChargeHit, _ => set.MeleeHit };
                MusouCueAt(cue, set, e.Vector, e.Value != 0 ? MusouSoundGroup.Boss : MusouSoundGroup.MeleeHit, Num.Vector3.Distance(e.Vector, camera), "strike");
                break;
            case MusouEventKind.Hit:
                if (MusouSlotOf(enemy.Id) is not { } hurt || now - hurt.LastHurt < MusouSoundRules.HurtSpacing) break;
                hurt.LastHurt = now;
                MusouCue(e.Value == 3 ? set.HurtBig : set.HurtSmall, set, hurt.Emitter, MusouSoundGroup.Hurt, distance, "hurt");
                break;
            case MusouEventKind.Telegraph when e.Value != MusouSim.MeleeWindUpTelegraph:
                var emitter = MusouSlotOf(enemy.Id)?.Emitter;
                if (emitter is null) break;
                _sound!.StopSpeech(emitter);   // the boss's roar takes the place of what he was saying
                MusouCue(set.Roar, set, emitter, MusouSoundGroup.Boss, distance, "boss roar");
                if (e.Value == (byte)MusouBossAbility.GroundSlam) MusouCue(set.SlamWindUp, set, emitter, MusouSoundGroup.Boss, distance, "slam wind-up");
                else if (e.Value == (byte)MusouBossAbility.Charge) MusouCue(set.ChargeStart, set, emitter, MusouSoundGroup.Boss, distance, "charge start");
                break;
        }
    }

    /// <summary>A death: the yell (it cuts what the enemy was saying), then the body's own fall.</summary>
    void MusouSoundDeath(in MusouDeathShown d, Num.Vector3 camera)
    {
        if (MusouSetOf(d.Type) is not { } set) return;
        float distance = Num.Vector3.Distance(d.Position, camera);
        var group = d.Type == MusouEnemyType.Warboss ? MusouSoundGroup.Boss : MusouSoundGroup.Death;
        if (MusouSlotOf(d.Id) is { } slot)
        {
            _sound!.StopSpeech(slot.Emitter);
            MusouCue(set.DeathYell, set, slot.Emitter, group, distance, "death");
            MusouCue(set.Fall, set, slot.Emitter, d.Type == MusouEnemyType.Warboss ? group : MusouSoundGroup.Bodyfall, distance, "fall");
            slot.Enemy = 0;
            return;
        }
        MusouCueAt(set.DeathYell, set, d.Position, group, distance, "death");
        MusouCueAt(set.Fall, set, d.Position, d.Type == MusouEnemyType.Warboss ? group : MusouSoundGroup.Bodyfall, distance, "fall");
    }

    /// <summary>Plays one option of a cue if the budget lets it; true when something posted. A line for an enemy still speaking is dropped before it costs the budget.</summary>
    bool MusouCue(MusouSoundCue cue, MusouSoundSet set, SoundEmitter emitter, MusouSoundGroup group, float distance, string what)
    {
        if (cue.Empty || _sound is null) return false;
        var option = cue.Pick(_musouSoundRoll);
        if (option.Events.Count == 0 && _sound.SpeechBusy(emitter)) return false;
        if (_musouBudget.Take(group, distance, _musouLiveVoices, _musouLiveSpeech) != MusouSoundVerdict.Post) return false;
        var offset = MusouOrkOffset;
        int voices = 0, lines = 0;
        string events = "";
        foreach (var b in option.Events)
        {
            if (b.UseTerrainMaterial is false && b.IsFootstep) emitter.Switches.Set(AudioCatalog.MaterialType, Fnv.Hash32(MaterialEffectTable.DefaultKey));
            var step = FootstepLevel.IsStep(b) ? offset.Plus(new VoiceOffset(FootstepLevel.GainDb(b))) : offset;
            var play = _sound.Post(b.StartEvent, emitter, b.DelaySeconds, footstep: b.IsFootstep, offset: step, once: true, limited: true);
            voices += play?.Voices.Count ?? 0;
            events += " " + b.StartEvent;
            if (play is not null && play.Voices.Exists(v => v.LoopCount == 0)) _sound.StopLoops(emitter);   // nothing here would ever end a loop
        }
        foreach (var l in option.Lines)
        {
            if (set.Character is not { } character) break;
            var result = _sound.PostSpeech(l, character, emitter, 0f, offset, limited: true);
            if (result != SpeechResult.Started) continue;
            lines++;
            events += $" speech/{(l.Type.Length > 0 ? l.Type : l.State)}";
        }
        _musouLiveVoices += voices + lines;
        _musouLiveSpeech += lines;
        CountSound("musou " + group);
        if (LogSound("musou")) GD.Print($"MUSOU SOUND t {_connectedFor:F3} {group} {what} {set.Type} {emitter.Label} d {distance:F1} m voices {voices}+{lines}{events}");
        return voices + lines > 0;
    }

    void MusouSoundLog(int known)
    {
        if (_sound is null) return;
        var parts = Enum.GetValues<MusouSoundGroup>().Select(g => (g, posted: _musouBudget.Count(g, MusouSoundVerdict.Post), rate: _musouBudget.Count(g, MusouSoundVerdict.OverRate), cap: _musouBudget.Count(g, MusouSoundVerdict.VoiceCap) + _musouBudget.Count(g, MusouSoundVerdict.SpeechCap), far: _musouBudget.Count(g, MusouSoundVerdict.TooFar)))
            .Where(p => p.posted + p.rate + p.cap + p.far > 0).Select(p => $"{p.g} {p.posted} (rate {p.rate}, cap {p.cap}, far {p.far})");
        GD.Print($"musou sounds: t {_connectedFor:F0} s: enemies {known}, slots {_musouSlots.Count(s => s.Bound)}, crowd voices now {_musouLiveVoices} peak {_musouPeakVoices} of {MusouSoundRules.MaxVoices} (lines peak {_musouPeakSpeech} of {MusouSoundRules.MaxSpeech}), " +
                 $"all voices now {_sound.ActiveCount} peak {_sound.PeakActive} of {SoundSystem.Max3D}+{SoundSystem.Max2D}, steals {_sound.Steals}, late {_sound.Late}; {string.Join(", ", parts)}");
    }
}
