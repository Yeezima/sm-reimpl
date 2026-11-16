using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// musou/hotkey-lines (owner, 2026-11-10; Musou Mode only, client presentation only: no simulation, wire, cooldown or config-hash change). The FOR THE EMPEROR key (T, the loyalist barks' key,
/// NetworkView.LoyalistBarks.cs) says <see cref="MusouKeyLines.Pool"/> in turn: slot 0 is the voice pack's FOR THE EMPEROR take as before, the other six slots are the campaign lines the owner kept,
/// posted from the speaker's own voice emitter (the own player's is protected from ducking and stealing by musou/audio-pass, <see cref="SoundEmitter.ProtectSpeech"/>; others' are heard in 3D
/// from their characters). The slot comes from the shout sequence the server hands every client, so all hear the same line. A line that cannot play (no speech bank) falls back to the take.
/// The six lines play their own <see cref="MusouKeyLines.KeyLine.GainDb"/> over the data's level (ours: level with the take).
/// Classic PvP: <see cref="MusouActive"/> is false and T plays the takes exactly as before. Test aids: <c>--no-musou-key-lines</c> (FOR THE EMPEROR only), <c>--musou-key-gain &lt;dB&gt;</c>,
/// <c>--musou-key-data-level</c>, <c>--log-sound musou-key</c>.
/// </summary>
public partial class NetworkView
{
    static readonly bool NoMusouKeyLines = CmdFlag(MusouKeyLines.EmperorOnlyFlag[2..]);
    /// <summary><c>--musou-key-gain &lt;dB&gt;</c>: added to the six campaign lines' own level offsets (<see cref="MusouKeyLines.KeyLine.GainDb"/>); default 0.</summary>
    static readonly float MusouKeyGainDb = float.TryParse(CmdArg("musou-key-gain"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float kg) ? kg : 0f;
    /// <summary><c>--musou-key-data-level</c>: the six lines at the data's level (no offsets).</summary>
    static readonly bool MusouKeyDataLevel = CmdFlag(MusouKeyLines.DataLevelFlag[2..]);
    int _musouKeyLinesSaid;

    /// <summary>
    /// A key shout of player <paramref name="id"/> with shout number <paramref name="sequence"/>, after the key has cut the speaker's lines: true when a campaign line of the rotation started
    /// (the take is not played), false for slot 0, outside Musou Mode, with <c>--no-musou-key-lines</c>, or when the line cannot play (the caller plays the FOR THE EMPEROR take).
    /// </summary>
    bool MusouKeyLine(int id, byte sequence, bool own, string who)
    {
        if (!MusouActive || NoMusouKeyLines || _sound is null) return false;
        int slot = MusouKeyLines.SlotOf(sequence);
        var line = MusouKeyLines.Pool[slot];
        if (line.Emperor)
        {
            if (LogSound("musou-key") || _bot) GD.Print($"MUSOU KEY t {_connectedFor:F2} {who} shout {sequence}: slot {slot + 1}/{MusouKeyLines.Pool.Count} FOR THE EMPEROR (the voice pack's take)");
            return false;
        }
        float gain = (MusouKeyDataLevel ? 0f : line.GainDb) + MusouKeyGainDb;   // level-matched to the take (ours)
        var result = _sound.PostLine(line.Node, $"musou key/{line.Character}", Track(id, own).Emitter, new VoiceOffset(gain));
        if (result == SpeechResult.Started) _musouKeyLinesSaid++;
        BarkCount(result == SpeechResult.Started ? "Key said (musou line)" : $"Key musou line {result}, the take instead");
        if (LogSound("musou-key") || LogSound("loyalist") || _bot)
            GD.Print($"MUSOU KEY t {_connectedFor:F2} {who} shout {sequence}: slot {slot + 1}/{MusouKeyLines.Pool.Count} {line.Character} {line.Node:x8} \"{line.Text}\" {gain:+0.#;-0.#} dB: {result}" +
                     (result == SpeechResult.Started ? $" (lines said {_musouKeyLinesSaid})" : ", FOR THE EMPEROR instead"));
        return result == SpeechResult.Started;
    }
}
