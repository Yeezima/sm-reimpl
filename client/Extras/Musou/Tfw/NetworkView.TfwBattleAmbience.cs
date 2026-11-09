using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// musou/battle-ambience (owner, 2026-11-08): the far battle round a Musou battlefield: Ork mobs yelling, bolter and lasgun fire, artillery and explosions, scheduled by
/// <see cref="MusouAmbienceScheduler"/> and played through the sound system (original events of the default banks; the loudness rule applies). Each artillery boom shakes the camera a tiny bit
/// (<see cref="MusouAmbienceRules.ArtilleryShake"/>, at most one shake every <see cref="AmbienceShakeSpacing"/> s, none with Reduce motion), as do the older war bed's artillery booms
/// (<c>TfwFireView</c>). Only in a battle (not the Stormbird, travel or the Firing Range's hub), never on a lean bot. Test aids: <c>--no-musou-battle-ambience</c>, <c>--musou-ambience-gain &lt;dB&gt;</c>,
/// <c>--log-sound musou-ambience</c> (a line per sound and every 10 s the counts).
/// </summary>
public partial class NetworkView
{
    /// <summary>The far battle's own voice group (its voices steal only from each other, never a player's or the crowd's), and the most it holds at once (of the 48 positioned players).</summary>
    const string AmbienceGroup = "musou battle";
    const int AmbienceVoicesMost = 14;
    /// <summary>Seconds between two artillery shakes, whichever layer's boom it was (ours: "semi often").</summary>
    const float AmbienceShakeSpacing = 4f;

    static readonly bool NoBattleAmbience = CmdFlag("no-musou-battle-ambience");
    static readonly float AmbienceGainArg = float.TryParse(CmdArg("musou-ambience-gain"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g) && float.IsFinite(g) ? g : 0f;

    readonly MusouAmbienceScheduler _tfwAmbience = new();
    readonly List<MusouAmbienceSound> _tfwAmbienceDue = new();
    readonly int[] _tfwAmbiencePlayed = new int[MusouAmbienceRules.Specs.Count];
    bool _tfwAmbienceOn;
    int _tfwAmbienceHeld, _tfwAmbienceShakes;
    double _tfwAmbienceShakeAt = double.NegativeInfinity, _tfwAmbienceLogAt;

    /// <summary>The frame (from <c>TfwFireFrame</c>).</summary>
    void TfwBattleAmbienceFrame(in TfwWarView war)
    {
        if (NoBattleAmbience || MusouSoundIsOff(MusouSoundOff.Ambience) || Lean || _sound is null || _camera is not { } camera) return;   // musou/sound-ab: ambience off
        bool battle = war.Phase is TfwRunPhase.Battle or TfwRunPhase.FinalAssault;
        if (!battle) { _tfwAmbienceOn = false; _tfwAmbienceDue.Clear(); return; }
        if (!_tfwAmbienceOn) { _tfwAmbienceOn = true; _tfwAmbience.Reset(_connectedFor); }
        _tfwAmbienceDue.AddRange(_tfwAmbience.Step(_connectedFor));
        var listener = camera.GlobalPosition;
        for (int i = _tfwAmbienceDue.Count - 1; i >= 0; i--)
        {
            var s = _tfwAmbienceDue[i];
            if (_connectedFor < s.At) continue;
            _tfwAmbienceDue.RemoveAt(i);
            TfwAmbiencePlay(s, listener);
        }
        if (LogSound("musou-ambience") && _connectedFor >= _tfwAmbienceLogAt)
        {
            _tfwAmbienceLogAt = _connectedFor + 10.0;
            GD.Print($"musou ambience: t {_connectedFor:F0} s: played {string.Join(", ", MusouAmbienceRules.Specs.Select((sp, k) => $"{sp.Kind} {_tfwAmbiencePlayed[k]}"))}, held (voice cap) {_tfwAmbienceHeld}, shakes {_tfwAmbienceShakes}");
        }
    }

    void TfwAmbiencePlay(in MusouAmbienceSound s, Vector3 listener)
    {
        if (_sound!.GroupVoices(AmbienceGroup).Voices >= AmbienceVoicesMost) { _tfwAmbienceHeld++; return; }
        var at = listener + new Vector3(MathF.Cos(s.Bearing) * s.Distance, 4f, MathF.Sin(s.Bearing) * s.Distance);
        var emitter = new SoundEmitter { Position = at, OneShot = true, Label = $"musou battle {s.Kind}", Group = AmbienceGroup };
        var offset = new VoiceOffset(GainDb: s.GainDb + AmbienceGainArg, PitchCents: s.Kind == MusouAmbienceKind.OrkMob ? 0f : (Random.Shared.NextSingle() - 0.5f) * 120f, Lpf: s.Lpf);
        bool played;
        if (s.Event.StartsWith("speech:", StringComparison.Ordinal) && s.Event.Split(':') is [_, var character, var type])
            played = _sound.PostSpeech(new SpeechBinding("musou battle ambience (ours)", "ambience", "", [], SpeechKind.Exertion, character, type, "", ""), character, emitter, offset: offset) == SpeechResult.Started;
        else played = _sound.Post(s.Event, emitter, offset: offset, once: true, limited: true) is { Found: true, Voices.Count: > 0 };
        if (played) _tfwAmbiencePlayed[(int)s.Kind]++;
        if (LogSound("musou-ambience")) GD.Print($"MUSOU AMBIENCE t {_connectedFor:F2} {s.Kind} {s.Event} at {s.Distance:F0} m bearing {s.Bearing:F2}{(played ? "" : " (silent)")}{(s.Shake > 0f ? $" shake {s.Shake:F3}" : "")}");
        if (s.Shake > 0f) TfwArtilleryShake(s.Shake);
    }

    /// <summary>An artillery boom's tiny shake (spaced <see cref="AmbienceShakeSpacing"/> apart; <c>TfwShake</c> keeps Reduce motion and the cap).</summary>
    void TfwArtilleryShake(float amount)
    {
        if (_connectedFor - _tfwAmbienceShakeAt < AmbienceShakeSpacing) return;
        _tfwAmbienceShakeAt = _connectedFor;
        _tfwAmbienceShakes++;
        TfwShake(amount);
    }
}
