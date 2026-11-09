using System;
using Godot;
using SmContent;
using SmFormats.Audio;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// musou/audio-pass (owner, 2026-11-09; Musou Mode only, client presentation only: no simulation, wire or config-hash change; every number in <see cref="MusouAudioRules"/> is ours). The hooks
/// of the shared sound code (<c>ExtrasTrackEmitter</c>, <c>ExtrasFireLayers</c>, <c>ExtrasShotMaterial</c>) land here, and each asks <see cref="MusouActive"/> first, so
/// Annihilation and Seize Ground run none of it:
/// <list type="bullet">
/// <item>The own character's barks are protected (<see cref="SoundEmitter.ProtectSpeech"/>): no ducking by an explosion's Set/Reset actions, no voice stealing, no instance limit, full level, own bus.</item>
/// <item>The bolter rifles (bolter, storm bolter, stalker bolter, heavy bolter): a deep copy of the fire sound plus a thump under every shot, and on the first Ork a shot hits the wet smack, then the explosion.
/// Piercing and explosions of the simulation are untouched.</item>
/// </list>
/// Test aid: <c>--no-musou-audio-pass</c> (all of it off: the 2026-11-08 sound), <c>--log-sound musou-audio</c> (a line per bolter hit).
/// </summary>
public partial class NetworkView
{
    static readonly bool NoMusouAudioPass = CmdFlag("no-musou-audio-pass");
    SmFormats.Audio.WwiseAttenuation? _musouSmackCurve;
    double _musouBlastAt = double.NegativeInfinity;
    int _musouBolterShots, _musouBolterHits;

    bool MusouAudioOn => MusouActive && !NoMusouAudioPass && MusouAudioRules.Applies(_config!.Extras);

    void MusouTrackEmitter(SoundEmitter emitter, bool own)
    {
        if (own && MusouAudioOn) emitter.ProtectSpeech = true;
    }

    /// <summary>A bolter rifle's shot: its fire sound again, deep, and the thump (voices: the copy's, one more).</summary>
    void MusouFireLayers(WeaponSpec spec, SoundEmitter e, bool own)
    {
        if (!MusouAudioOn || _sound is null || _soundTriggers is null || !MusouAudioRules.IsBolterRifle(spec.Name) || _soundTriggers.File(spec.Name) is not { } file) return;
        _musouBolterShots++;
        var fire = SoundMixPass.Fire(SoundSystem.MixClassic);
        if (!own)
        {
            // Others' bolters: the lighter deep copy and thump on the shooter's emitter (voices are shared with the crowd).
            var deep = new VoiceOffset(MusouAudioRules.DeepGainDb, MusouAudioRules.DeepPitchCents, MusouAudioRules.DeepLpf).Plus(fire);
            foreach (var b in file.Fire(null)) PostBinding(b, e, own, "fire other deep", extra: deep);
            _sound.Post(MusouAudioRules.ThumpEvent, e, offset: new VoiceOffset(MusouAudioRules.ThumpGainDb, MusouAudioRules.ThumpPitchCents).Plus(fire));
            CountSound("musou bolter thump");
            return;
        }
        // The player's own gun (owner follow-up 2026-11-09: "deeper, more powerful"): a protected emitter of its own at the muzzle (never stolen, cut or ducked): a louder deep copy, a sub copy under it, a louder thump.
        var gun = new SoundEmitter { Position = e.Position, Follow = e.Follow, Switches = e.Switches, Own = true, OneShot = true, ProtectGun = true, Label = "musou bolter own layers" };
        var body = new VoiceOffset(MusouAudioRules.OwnDeepGainDb, MusouAudioRules.DeepPitchCents, MusouAudioRules.DeepLpf).Plus(fire);
        var sub = new VoiceOffset(MusouAudioRules.OwnSubGainDb, MusouAudioRules.OwnSubPitchCents, MusouAudioRules.OwnSubLpf).Plus(fire);
        foreach (var b in file.Fire(null))
        {
            PostBinding(b, gun, own, "fire own deep", extra: body);
            PostBinding(b, gun, own, "fire own sub", extra: sub);
        }
        _sound.Post(MusouAudioRules.ThumpEvent, gun, offset: new VoiceOffset(MusouAudioRules.OwnThumpGainDb, MusouAudioRules.ThumpPitchCents).Plus(fire));
        CountSound("musou bolter thump");
        if (LogSound("musou-audio")) GD.Print($"MUSOU AUDIO t {_connectedFor:F3} {spec.Name}: own gun layers deep {MusouAudioRules.OwnDeepGainDb:+0;-0} dB {MusouAudioRules.DeepPitchCents:F0} c, sub {MusouAudioRules.OwnSubGainDb:+0;-0} dB {MusouAudioRules.OwnSubPitchCents:F0} c, thump {MusouAudioRules.OwnThumpGainDb:+0;-0} dB");
    }

    /// <summary>The first Ork a bolter rifle's shot hits (<paramref name="at"/>, Godot space): the wet smack, then the explosion.</summary>
    void MusouBolterFirstHit(string weapon, Vector3 at)
    {
        if (!MusouAudioOn || _sound is null || !MusouAudioRules.IsBolterRifle(weapon)) return;
        if (_connectedFor - _musouBlastAt < MusouAudioRules.BlastDelaySeconds) return;   // a burst's hits cannot pile their explosions
        _musouBlastAt = _connectedFor;
        _musouBolterHits++;
        var smack = new SoundEmitter { Position = at, OneShot = true, Label = "musou bolter smack", DistanceScale = MusouAudioRules.SmackFalloffScale, Positioned = true, AttenuationOverride = _musouSmackCurve ??= _sound.AttenuationOf(MusouAudioRules.SmackCurveEvent) };
        smack.Switches.Set(AudioCatalog.MaterialType, Fnv.Hash32("flesh"));
        float mixDb = SoundSystem.MixClassic ? 0f : SoundMixPass.HitGainDb;   // the mix pass's on-hit gain, without its high-pass (the smack is a low body)
        _sound.Post(MusouAudioRules.SmackEvent, smack, offset: new VoiceOffset(MusouAudioRules.SmackGainDb + mixDb, MusouAudioRules.SmackPitchCents, MusouAudioRules.SmackLpf));
        var blast = new SoundEmitter { Position = at, OneShot = true, Label = "musou bolter blast" };
        if (!MusouSoundIsOff(MusouSoundOff.Blast))   // musou/sound-ab: blast off
            _sound.Post(MusouAudioRules.BlastEvent, blast, MusouAudioRules.BlastDelaySeconds, once: true, offset: new VoiceOffset(MusouAudioRules.BlastGainDb, TailDb: MusouAudioRules.BlastTailDb));
        CountSound("musou bolter hit");
        if (LogSound("musou-audio")) GD.Print($"MUSOU AUDIO t {_connectedFor:F3} {weapon}: smack + blast at {at.X:F1},{at.Y:F1},{at.Z:F1} (shots {_musouBolterShots}, hits {_musouBolterHits})");
    }

    /// <summary>Another player's bolter shot that hit an Ork (the core <c>SoundShot</c> path): the same smack and blast at the shot's end.</summary>
    void MusouBolterOthersHit(ShotView shot, int victim)
    {
        if (!MusouAudioOn || _config is null || victim >= 0 || shot.Hit is not (ShotHit.Body or ShotHit.Head) || shot.Weapon < 0 || shot.Weapon >= _config.Weapons.Count) return;
        MusouBolterFirstHit(_config.Weapons[shot.Weapon].Name, ToGodot(shot.End));
    }
}
