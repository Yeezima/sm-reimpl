using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmContent.Extras.Musou;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// musou/soundscape-tuning (owner interview 2026-11-12; Musou Mode only, client only: <see cref="MusouSoundscape"/> holds the rules and numbers). The glue of the changes that need the crowd's state:
/// <list type="bullet">
/// <item>The threat cues: Boss and Nob attacks, a melee Ork winding up at the player from outside the view, a rokkit aimed at him, a Big Shoota's burst at him, a lit Bomb Squig near him play about +3 dB and
/// on an emitter outside the crowd's group, never stolen, cut or ducked (<c>MusouCue</c>'s <c>threat</c>). <c>--log-sound musou</c> prints a MUSOU THREAT line for each.</item>
/// <item>The far war (battle ambience and war bed) ducks while an Ork engages the player.</item>
/// <item>A quiet fire bed round the listener in a battle.</item>
/// </list>
/// The clip guard and the softer duck are in the sound system (<see cref="SoundSystem.SetClipGuard"/>, <see cref="SoundSystem.ShapeBusActions"/>), switched from <c>MusouSoundAbFrame</c>.
/// Each has its restore switch in <c>--musou-sound-off</c> (clipguard, threat, softduck, bgduck, bed, waaagh).
/// </summary>
public partial class NetworkView
{
    readonly MusouThreatBudget _musouThreatBudget = new();
    readonly MusouBackgroundDucker _musouBgDucker = new();
    static readonly Func<SoundPropertyAction, float, SoundPropertyAction?> MusouShapeDuck = MusouSoundscape.ShapeDuck;
    Num.Vector3 _musouMePos, _musouCamPos;
    Num.Vector2 _musouCamForward;
    int _musouMeId;
    bool _musouMeAlive, _musouThreatOn;
    int _musouThreatPosted, _musouThreatTokens;
    SoundEmitter[]? _musouBed;
    readonly SoundEmitter _musouWind = new() { Label = "musou wind", Group = "musou bed", Follow = null };
    bool _musouBedOn, _musouWindTried, _musouWindBank;

    /// <summary>Once a frame, before the crowd's sounds: the player, the camera, the far war's duck.</summary>
    void MusouSoundscapeFrame(PlayerState me, IReadOnlyList<MusouShown> shown, Camera3D cam, float delta)
    {
        _musouThreatBudget.Refill(delta);
        _musouMePos = me.Position;
        _musouMeId = me.PlayerId;
        _musouMeAlive = me.Alive;
        _musouCamPos = new Num.Vector3(cam.GlobalPosition.X, -cam.GlobalPosition.Z, cam.GlobalPosition.Y);
        var forward = -cam.GlobalTransform.Basis.Z;
        _musouCamForward = new Num.Vector2(forward.X, -forward.Z);
        _musouThreatOn = !MusouSoundIsOff(MusouSoundOff.Threat);
        bool engaged = false;
        if (_musouMeAlive)
            for (int i = 0; i < shown.Count && !engaged; i++)
            {
                var e = shown[i];
                if (e.State != MusouEnemyState.Dying && MusouSoundscape.Engages(e.State, Num.Vector3.Distance(e.Position, _musouMePos))) engaged = true;
            }
        _musouBgDucker.Step(delta, engaged);
        if (_sound is { } sound) sound.BackgroundDb = MusouSoundIsOff(MusouSoundOff.BgDuck) ? 0f : _musouBgDucker.Db;
    }

    /// <summary>Whether a threat of this kind by an enemy at <paramref name="at"/> facing <paramref name="yaw"/> is close to or aimed at the player now.</summary>
    bool MusouThreatClose(MusouSoundscape.Threat kind, Num.Vector3 at, float yaw)
    {
        if (!_musouThreatOn || !_musouMeAlive) return false;
        float d = Num.Vector3.Distance(at, _musouMePos);
        bool aimed = MusouSoundscape.IsAimedAt(at, yaw, _musouMePos);
        bool outside = MusouSoundscape.IsOutsideView(_musouCamPos, _musouCamForward, at);
        return MusouSoundscape.Qualifies(kind, d, aimed, outside);
    }

    /// <summary>The wind-up or swing of a melee enemy as a threat: the boss's attack, the Nob's heavy swing, any other melee Ork's from behind. Gunners are not melee threats.</summary>
    bool MusouThreatOf(in MusouShown e)
    {
        var kind = e.Type == MusouEnemyType.Warboss || MusouBossOrks.Is(e.Type) ? MusouSoundscape.Threat.BossAttack
            : e.Type == MusouEnemyType.Nob ? MusouSoundscape.Threat.NobSwing
            : MusouOrkGunRules.WargearOf(e.Type) is null ? MusouSoundscape.Threat.MeleeBehind : (MusouSoundscape.Threat?)null;
        return kind is { } k && MusouThreatClose(k, e.Position, e.Yaw);
    }

    /// <summary>Posts a threat cue's option on its own emitter: the option's events and lines, +3 dB, no instance limit.</summary>
    bool MusouThreatPost(MusouSoundOption option, MusouSoundSet set, SoundEmitter emitter, string what, MusouSoundGroup group, float distance)
    {
        var offset = MusouOrkOffset.Plus(new VoiceOffset(MusouSoundscape.ThreatLiftDb));
        if (group == MusouSoundGroup.MeleeHit) offset = offset.Plus(LoudnessArg.MeleeHit);
        int voices = 0, lines = 0;
        string events = "";
        foreach (var b in option.Events)
        {
            var play = _sound!.Post(b.StartEvent, emitter, b.DelaySeconds, footstep: false, offset: offset, once: true, limited: false);
            voices += play?.Voices.Count ?? 0;
            events += " " + b.StartEvent;
        }
        foreach (var l in option.Lines)
        {
            if (set.Character is not { } character) break;
            if (_sound!.PostSpeech(l, character, emitter, 0f, offset, limited: false) != SpeechResult.Started) continue;
            lines++;
            events += $" speech/{(l.Type.Length > 0 ? l.Type : l.State)}";
        }
        _musouThreatPosted++;
        CountSound("musou threat");
        if (LogSound("musou")) GD.Print($"MUSOU THREAT t {_connectedFor:F3} {group} {what} {set.Type} d {distance:F1} m from the listener voices {voices}+{lines}{events}");
        return voices + lines > 0;
    }

    /// <summary>The rokkit fired, or the Squig's cue: whether it is a close threat (the rocket's aim point or the Squig's feet near the player), and the sound's emitter for it.</summary>
    bool MusouThreatNear(MusouSoundscape.Threat kind, Num.Vector3 at) =>
        _musouThreatOn && _musouMeAlive && MusouSoundscape.Qualifies(kind, Num.Vector3.Distance(at, _musouMePos), false, false);

    /// <summary>A Big Shoota Nob's burst announced at the player: his gun's ready cue, lifted (the data has no spin-up sound for his gun).</summary>
    void MusouBigShootaThreat(in MusouEvent e, IReadOnlyList<MusouShown> shown)
    {
        if (!_musouThreatOn || !_musouMeAlive || e.Player != _musouMeId) return;
        int i = MusouFind(shown, e.Enemy);
        if (i < 0) return;
        var s = shown[i];
        if (s.Type != MusouEnemyType.BigShootaNob || MusouSetOf(s.Type) is not { } set || set.GunReady.Empty) return;
        float d = Num.Vector3.Distance(s.Position, _musouMePos);
        if (!MusouSoundscape.Qualifies(MusouSoundscape.Threat.BigShoota, d, true, false)) return;
        var emitter = MusouSlotOf(s.Id)?.Emitter ?? new SoundEmitter { Position = ToGodot(s.Position) + Vector3.Up * 1.2f, OneShot = true, Label = "musou big shoota" };
        MusouCue(set.GunReady, set, emitter, MusouSoundGroup.Effort, d, "spin-up", threat: true);
    }

    // ---- the quiet wind and fire bed (Ashfall's battle) ----

    /// <summary>
    /// A fire bed in a battle: the install's world fire loop on <see cref="MusouSoundscape.BedEmitters"/> emitters on a ring round the listener (they follow the camera), at the bed's gain. Starts with the
    /// battle, ends with it. Nothing in the hub or the Stormbird.
    /// </summary>
    void MusouBedFrame(bool battle)
    {
        bool want = battle && !Lean && _sound is not null && _camera is not null && !MusouSoundIsOff(MusouSoundOff.Bed) && _audio is { } audio && audio.HasEvent(MusouSoundscape.FireEvent);
        if (!battle) { _musouBgDucker.Reset(); if (_sound is { } s) s.BackgroundDb = 0f; }   // no duck sticks after a battle
        if (!want)
        {
            if (_musouBedOn && _musouBed is { } old) { foreach (var e in old) _sound?.StopEmitter(e); _sound?.StopEmitter(_musouWind); }
            _musouBedOn = false;
            return;
        }
        if (_musouBedOn) return;
        _musouBedOn = true;
        _musouBed ??= BuildBed();
        _musouWind.Follow = () => _camera?.GlobalPosition;
        foreach (var e in _musouBed) _sound!.Post(MusouSoundscape.FireEvent, e, offset: new VoiceOffset(MusouSoundscape.FireGainDb));
        if (!_musouWindTried) { _musouWindTried = true; _musouWindBank = _audio.AddPackage(_gameDir, MusouSoundscape.WindPackage); }   // once per client: AddPackage re-opens and re-parses
        bool windBank = _musouWindBank;
        var wind = _sound!.Post(MusouSoundscape.WindEvent, _musouWind, offset: new VoiceOffset(MusouSoundscape.WindGainDb));
        if (LogSound("musou-ambience")) GD.Print($"musou bed: {MusouSoundscape.FireEvent} on {_musouBed.Length} emitters at +{MusouSoundscape.FireGainDb:0} dB; {MusouSoundscape.WindEvent} (bank {MusouSoundscape.WindPackage} {(windBank ? "loaded" : "missing")}) voices {wind?.Voices.Count ?? 0} 3D {wind?.Voices.Exists(v => v.Is3D)}");
    }

    SoundEmitter[] BuildBed()
    {
        var bed = new SoundEmitter[MusouSoundscape.BedEmitters];
        for (int k = 0; k < bed.Length; k++)
        {
            float angle = MathF.Tau * k / bed.Length + 0.7f;
            var offset = new Vector3(MathF.Cos(angle), 0.3f, MathF.Sin(angle)) * MusouSoundscape.BedRing;
            bed[k] = new SoundEmitter { Label = $"musou bed {k}", Group = "musou bed", Background = false, Follow = () => _camera is { } c ? c.GlobalPosition + offset : null };
        }
        return bed;
    }
}
