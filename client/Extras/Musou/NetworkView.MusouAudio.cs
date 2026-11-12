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

    /// <summary>
    /// Test aid <c>--musou-own-layer-db thump,deep,sub</c> (owner 2026-11-12, the crowded-fight sound tuning; presentation only): the own bolter's layer gains in dB, each a number or <c>off</c> (that layer not played),
    /// in place of <see cref="MusouAudioRules.OwnThumpGainDb"/>, <see cref="MusouAudioRules.OwnDeepGainDb"/> and <see cref="MusouAudioRules.OwnSubGainDb"/>; a field left empty or <c>-</c> keeps its default (no flag: today's values).
    /// </summary>
    static readonly (float? Thump, float? Deep, float? Sub) OwnLayerDb = ParseOwnLayerDb(CmdArg("musou-own-layer-db"));

    static (float? Thump, float? Deep, float? Sub) ParseOwnLayerDb(string? text)
    {
        float? thump = MusouAudioRules.OwnThumpGainDb, deep = MusouAudioRules.OwnDeepGainDb, sub = MusouAudioRules.OwnSubGainDb;
        if (text is null) return (thump, deep, sub);
        var parts = text.Split(',');
        if (parts.Length != 3 || !TryField(parts[0], ref thump) || !TryField(parts[1], ref deep) || !TryField(parts[2], ref sub))
        {
            GD.PrintErr($"--musou-own-layer-db: expected thump,deep,sub (each dB, 'off' or empty for the default), got '{text}'; the default gains stay");
            return (MusouAudioRules.OwnThumpGainDb, MusouAudioRules.OwnDeepGainDb, MusouAudioRules.OwnSubGainDb);
        }
        return (thump, deep, sub);

        static bool TryField(string field, ref float? value)
        {
            field = field.Trim();
            if (field.Length == 0 || field == "-") return true;
            if (field.Equals("off", StringComparison.OrdinalIgnoreCase)) { value = null; return true; }
            if (!float.TryParse(field, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float db) || !float.IsFinite(db)) return false;
            value = db;
            return true;
        }
    }

    void MusouTrackEmitter(SoundEmitter emitter, bool own)
    {
        if (own && MusouAudioOn) emitter.ProtectSpeech = true;
        if (MusouActive) emitter.VoiceDistanceScale = 1f / MusouVoiceReach;   // musou/npc-voices-only: every player's voice lines carry farther
    }

    /// <summary>A bolter rifle's shot: its fire sound again, deep, and the thump (voices: the copy's, one more).</summary>
    void MusouFireLayers(WeaponSpec spec, SoundEmitter e, bool own)
    {
        if (!MusouAudioOn || _sound is null || _soundTriggers is null || !MusouAudioRules.IsBolterRifle(spec.Name) || _soundTriggers.File(spec.Name) is not { } file) return;
        _musouBolterShots++;
        var fire = SoundMixPass.Fire(SoundSystem.MixClassic);
        if (!own)
        {
            if (MusouSoundIsOff(MusouSoundOff.AllyLayers)) return;   // musou/sound-ab: allylayers off
            // Others' bolters: the lighter deep copy and thump on the shooter's emitter (voices are shared with the crowd).
            var deep = new VoiceOffset(MusouAudioRules.DeepGainDb, MusouAudioRules.DeepPitchCents, MusouAudioRules.DeepLpf).Plus(fire);
            foreach (var b in file.Fire(null)) PostBinding(b, e, own, "fire other deep", extra: deep);
            _sound.Post(MusouAudioRules.ThumpEvent, e, offset: new VoiceOffset(MusouAudioRules.ThumpGainDb, MusouAudioRules.ThumpPitchCents).Plus(fire));
            CountSound("musou bolter thump");
            return;
        }
        if (MusouSoundIsOff(MusouSoundOff.OwnLayers)) return;   // musou/sound-ab: ownlayers off
        // The player's own gun (owner follow-up 2026-11-09: "deeper, more powerful"): a protected emitter of its own at the muzzle (never stolen, cut or ducked): a louder deep copy, a sub copy under it, a louder thump.
        var gun = new SoundEmitter { Position = e.Position, Follow = e.Follow, Switches = e.Switches, Own = true, OneShot = true, ProtectGun = true, Label = "musou bolter own layers" };
        var (thumpDb, deepDb, subDb) = OwnLayerDb;
        foreach (var b in file.Fire(null))
        {
            if (deepDb is { } d) PostBinding(b, gun, own, "fire own deep", extra: new VoiceOffset(d, MusouAudioRules.DeepPitchCents, MusouAudioRules.DeepLpf).Plus(fire));
            if (subDb is { } sb) PostBinding(b, gun, own, "fire own sub", extra: new VoiceOffset(sb, MusouAudioRules.OwnSubPitchCents, MusouAudioRules.OwnSubLpf).Plus(fire));
        }
        if (thumpDb is { } t) _sound.Post(MusouAudioRules.ThumpEvent, gun, offset: new VoiceOffset(t, MusouAudioRules.ThumpPitchCents).Plus(fire));
        CountSound("musou bolter thump");
        if (LogSound("musou-audio")) GD.Print($"MUSOU AUDIO t {_connectedFor:F3} {spec.Name}: own gun layers deep {OwnDb(deepDb)} {MusouAudioRules.DeepPitchCents:F0} c, sub {OwnDb(subDb)} {MusouAudioRules.OwnSubPitchCents:F0} c, thump {OwnDb(thumpDb)}");
    }

    static string OwnDb(float? db) => db is { } v ? $"{v:+0.#;-0.#;0} dB" : "off";

    /// <summary>The first Ork a bolter rifle's shot hits (<paramref name="at"/>, Godot space): the wet smack, then the explosion.</summary>
    void MusouBolterFirstHit(string weapon, Vector3 at)
    {
        if (!MusouAudioOn || _sound is null || !MusouAudioRules.IsBolterRifle(weapon)) return;
        if (_connectedFor - _musouBlastAt < MusouAudioRules.BlastDelaySeconds) return;   // a burst's hits cannot pile their explosions
        _musouBlastAt = _connectedFor;
        _musouBolterHits++;
        // sound/stalker-hit-confirm: the Stalker / Daemon Eye Bolter has its own confirm on the shooter (MusouStalkerConfirm) in place of the wet bolter smack; the explosion stays.
        bool stalker = !OldStalkerHitConfirm && StalkerHitConfirm.ReplacesMusouSmack(SoundNameOf(weapon));
        if (!stalker) MusouBolterSmack(at);
        var blast = new SoundEmitter { Position = at, OneShot = true, Label = "musou bolter blast" };
        if (!MusouSoundIsOff(MusouSoundOff.Blast))   // musou/sound-ab: blast off
            _sound.Post(MusouAudioRules.BlastEvent, blast, MusouAudioRules.BlastDelaySeconds, once: true, offset: new VoiceOffset(MusouAudioRules.BlastGainDb, TailDb: MusouAudioRules.BlastTailDb));
        CountSound("musou bolter hit");
        if (LogSound("musou-audio")) GD.Print($"MUSOU AUDIO t {_connectedFor:F3} {weapon}: {(stalker ? "blast (stalker: no smack)" : "smack + blast")} at {at.X:F1},{at.Y:F1},{at.Z:F1} (shots {_musouBolterShots}, hits {_musouBolterHits})");
    }

    /// <summary>The wet bolter smack at the Ork (<see cref="MusouAudioRules.SmackEvent"/>).</summary>
    void MusouBolterSmack(Vector3 at)
    {
        if (_sound is null) return;
        var smack = new SoundEmitter { Position = at, OneShot = true, Label = "musou bolter smack", DistanceScale = MusouAudioRules.SmackFalloffScale, Positioned = true, AttenuationOverride = _musouSmackCurve ??= _sound.AttenuationOf(MusouAudioRules.SmackCurveEvent) };
        smack.Switches.Set(AudioCatalog.MaterialType, Fnv.Hash32("flesh"));
        float mixDb = SoundSystem.MixClassic ? 0f : SoundMixPass.HitGainDb;   // the mix pass's on-hit gain, without its high-pass (the smack is a low body)
        _sound.Post(MusouAudioRules.SmackEvent, smack, offset: new VoiceOffset(MusouAudioRules.SmackGainDb + mixDb, MusouAudioRules.SmackPitchCents, MusouAudioRules.SmackLpf));
    }

    /// <summary>
    /// sound/stalker-hit-confirm (owner 2026-11-09): the own Stalker / Daemon Eye Bolter shot that hit an Ork plays the data's confirm (<see cref="StalkerHitConfirm.Event"/>) on the shooter, as
    /// Classic does for a hit on a player: 2D, the data's level, the Ork's material (<paramref name="armoured"/>: ceramite_armour, else flesh). Heard by the shooter only (an own emitter).
    /// </summary>
    void MusouStalkerConfirm(string weapon, bool armoured)
    {
        if (OldStalkerHitConfirm || !MusouActive || _client is null || _audio is null || _sound is null || HitConfirmArg == HitConfirmMode.Off || !StalkerHitConfirm.Is(SoundNameOf(weapon))) return;
        int ownId = _client.PlayerId;
        var at = new SoundEmitter { Own = true, Follow = () => PlayerSoundPoint(ownId), Label = "musou stalker confirm", OneShot = true };
        at.Switches.Set(AudioCatalog.PlayerGroup, AudioCatalog.LocalPlayerValue);
        at.Switches.Set(AudioCatalog.MaterialType, Fnv.Hash32(ImpactSounds.ConfirmMaterial(armoured)));
        uint? stateBefore = _audio.State(ImpactSounds.DeathStateGroup);
        _audio.SetState(ImpactSounds.DeathStateGroup, ImpactSounds.StateBeforeConfirm(HitConfirmMode.Data, stateBefore, kill: false, armoured));
        var play = _sound.Post(StalkerHitConfirm.Event, at, offset: StalkerHitConfirm.Offset);
        _audio.SetState(ImpactSounds.DeathStateGroup, ImpactSounds.StateAfterConfirm(HitConfirmMode.Data, stateBefore, _audio.State(ImpactSounds.DeathStateGroup)));
        CountSound("hit confirm");
        if (LogSound("hitconfirm") || LogSound("musou-audio"))
            GD.Print($"HIT CONFIRM t {_connectedFor:F3} musou {weapon} event {StalkerHitConfirm.Event} on an Ork, {(armoured ? "armour" : "flesh")} | {VoiceMixText(play, at, StalkerHitConfirm.Offset)}");
    }

    /// <summary>Another player's bolter shot that hit an Ork (the core <c>SoundShot</c> path): the same smack and blast at the shot's end.</summary>
    void MusouBolterOthersHit(ShotView shot, int victim)
    {
        if (!MusouAudioOn || _config is null || victim >= 0 || shot.Hit is not (ShotHit.Body or ShotHit.Head) || shot.Weapon < 0 || shot.Weapon >= _config.Weapons.Count) return;
        MusouBolterFirstHit(_config.Weapons[shot.Weapon].Name, ToGodot(shot.End));
    }
}
