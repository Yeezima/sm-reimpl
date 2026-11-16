using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Headshot kills (the owner, 2026-11-07: "No headshot scream audio. No headshot medal onscreen after the kill."; protocol 64). The server marks a kill whose killing hit was a
/// weapon's shot on the head (<see cref="KillView.Headshot"/>, <see cref="ScoreRules.IsHeadshotKill"/>); the client no longer guesses it from the own hit reports and the crosshair.
/// <list type="bullet">
/// <item>The killer's HUD shows the headshot medal line (<see cref="Hud.AddOwnKill"/>: the data's <c>mp_bonus_headshot</c> icon, name 105720 and description 105721 from the locale).</item>
/// <item>Everyone near hears the victim's death scream: the speech bank's <c>speech_exertion</c> line for the victim class's character and the exertion type <c>death</c> (the MP
/// characters have lines for it; the MP data binds no command that plays them: which moment the original used them for is not in the data, so playing them on a headshot kill is ours).</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    /// <summary>The exertion command a headshot kill plays for the victim (character <c>autodetect</c>: the victim class's <c>speech_arg_character</c>).</summary>
    static readonly SpeechBinding HeadshotScreamBinding = new("headshot kill (ours)", "headshot", "", [], SpeechKind.Exertion, "autodetect", HeadshotScreamType, "", "");

    /// <summary>The speech bank's exertion type of the scream (<c>speech_exertion</c> argument <c>exertion_type</c>, docs/discovery.md "Dialogue events").</summary>
    public const string HeadshotScreamType = "death";

    int _headshotScreams, _headshotScreamsSilent;

    /// <summary>A kill in the feed: a headshot plays the victim's death scream where the victim fell.</summary>
    void HeadshotKillSound(KillView k)
    {
        if (!k.Headshot || _sound is null || _client is null) return;
        PlayerState? victim = k.Victim == _client.PlayerId ? _client.Predicted
            : _soundTracks.TryGetValue(k.Victim, out var t) && t.Has ? t.Prev : null;
        if (victim is not { } s || SpeechCharacterOf(s) is not { } character) { _headshotScreamsSilent++; return; }
        bool own = k.Victim == _client.PlayerId;
        var at = new SoundEmitter { Position = ToGodot(s.Position) + new Vector3(0, 1.6f, 0), Own = own, Label = $"P{k.Victim} headshot scream", OneShot = true };
        var result = _sound.PostSpeech(HeadshotScreamBinding, character, at);
        if (result == SpeechResult.Started) _headshotScreams++; else _headshotScreamsSilent++;
        if (_bot || LogSoundKinds.Count > 0)
            GD.Print($"t {_connectedFor:F1} s: headshot kill #{k.Sequence} P{k.Killer} -> P{k.Victim}: scream {character}/{HeadshotScreamType} {result} (played {_headshotScreams}, silent {_headshotScreamsSilent})");
    }
}
