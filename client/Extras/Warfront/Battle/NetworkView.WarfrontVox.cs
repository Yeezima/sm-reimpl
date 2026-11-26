using Godot;
using SmContent;
using SmFormats.Audio;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront;

namespace SmClient;

/// <summary>
/// Guardsmen G2: the Guardsmen's vox lines (envelope tag 52, <see cref="WarfrontVoxWire"/>). Each new sequence is shown once on Musou's existing callout line (3 s; no new HUD element). Owner 2026-11-25 ("Add our
/// barks"): Flank plays the co-op Guardsman's 'danger' bark and Rallying his 'celebration' cheer from the player's own speech bank at runtime (ours: neither says the line's words; the other three are text only).
/// Log line <c>warfront: vox &lt;seq&gt; &lt;text&gt;</c>.
/// </summary>
public partial class NetworkView
{
    ushort _wfVoxShown;
    double _wfVoxAt = -100;
    int _wfVoxSounds;

    static readonly Color WfVoxColour = new(0.86f, 0.82f, 0.62f);

    /// <summary>The co-op Guardsman voices (speech_01: ig_guardsman_3 and _4) and their lines for the two barks (state/guardsmen-vox-search.md).</summary>
    static readonly string[] WfVoxVoices = ["ig_guardsman_3", "ig_guardsman_4"];
    const uint WfVoxDangerEvent = 0x33c89dfa;   // the unnamed chatter event whose set "danger" the co-op Guardsmen have

    internal int WarfrontVoxShown => _wfVoxShown;

    void WarfrontVoxReceive(System.ReadOnlySpan<byte> payload)
    {
        if (!WarfrontVoxWire.TryDecode(payload, out var line)) { WarfrontDropped++; return; }
        double now = Time.GetTicksMsec() / 1000.0;
        bool fresh = line.Sequence != _wfVoxShown || now - _wfVoxAt > WarfrontVoxWire.RepeatSeconds + 1.5;   // a repeat of the shown line is not shown again; the same number long after is a new battle's
        _wfVoxAt = now;
        if (!fresh) return;
        _wfVoxShown = line.Sequence;
        string text = WarfrontVox.Text(line.Kind, line.From, line.To);
        _musouHud?.CallOut(text, WfVoxColour, 3.0);
        GD.Print($"warfront: vox {line.Sequence} {text}");
        if (line.Kind is WarfrontVoxKind.Flank or WarfrontVoxKind.Rallying) WarfrontVoxBark(line.Kind, line.Sequence);
    }

    void WarfrontVoxBark(WarfrontVoxKind kind, ushort sequence)
    {
        if (_sound is null || _audio is null || !_audio.HasSpeech || _client is null) return;
        uint voice = Fnv.Hash32(WfVoxVoices[sequence % WfVoxVoices.Length]);
        var leaves = kind == WarfrontVoxKind.Flank
            ? _audio.DialogueLeaves(WfVoxDangerEvent, [voice, Fnv.Hash32("danger")])
            : _audio.DialogueLeaves(AudioCatalog.SpeechExertionEvent, [voice, Fnv.Hash32("celebration")]);
        if (leaves.Count == 0) return;
        var at = new SoundEmitter { Position = ToGodot(_client.Predicted.Position) + new Vector3(0, 1.6f, 0), Own = true, Label = "warfront vox bark", OneShot = true };
        var result = _sound.PostLine(leaves[sequence % leaves.Count], $"warfront vox/{kind}", at);
        if (result == SpeechResult.Started) _wfVoxSounds++;
        GD.Print($"warfront: vox bark {kind} {result} (played {_wfVoxSounds})");
    }
}
