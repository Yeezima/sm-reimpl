using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SmClient.ImpactRules;
using SmSim;

namespace SmClient;

/// <summary>
/// fx/melee-hit-sound (the owner's requests of 2026-11-01; client presentation only: no simulation, wire or config-hash change; not under Extras: Faithful and Extended, classic PvP and Musou alike).
/// The on-hit sounds of two groups of melee weapons are **generated at run time** (<see cref="MeleeHitSynth"/>, no asset file; a provisional approximation; nobody
/// has heard it yet): sound A (a short deep impact) for the power axe and power sword; sound B (a heavier, lower impact) for the thunder hammer and the daemon maul (hotfix 2026-11-01: the chainsword, combat knife and every charge keep their originals).
/// The own player's hit reports and the others' inferred hits both come here. The hammer's and maul's blast layer
/// (<see cref="PowerFlash.BlastEvents"/>) is not played with sound B: a second boom under a roar of the same kind would fight it. <c>--old-melee-hit-sound</c> restores every original sound (and the blast layer).
/// Test aids: <c>--old-melee-hit-sound</c>, <c>--log-sound melee</c> (prints "procedural sound A/B" as the event).
/// </summary>
public partial class NetworkView
{
    bool OldMeleeHitSound => _oldMeleeHitSound ??= CmdFlag("old-melee-hit-sound");
    bool? _oldMeleeHitSound;
    readonly Dictionary<HitSound, AudioStreamWav[]> _hitStreams = new();
    uint _hitSoundCount;
    int _proceduralHits;

    /// <summary>The procedural sound that replaces a melee hit's original, or null (the original plays): by the attacker's weapon; a charge hit always keeps its original.</summary>
    HitSound? ProceduralHitSound(PlayerState attacker, bool charge, bool dash)
    {
        if (OldMeleeHitSound || _config is null) return null;
        if (charge) return null;   // every charge keeps its original sound (hotfix 2026-11-01)
        return MeleeHitSynth.Replaces(MeleeWargear(attacker));
    }

    /// <summary>Seconds after the own strike's sound during which the server's reports of that swing stay silent: past any round trip (ours).</summary>
    const double OwnStrikeWindow = 0.6;
    double _ownStrikeSoundAt = double.NegativeInfinity;

    /// <summary>Whether the own swing's hit sound has just been played at the strike.</summary>
    bool OwnStrikeSounded => _connectedFor - _ownStrikeSoundAt <= OwnStrikeWindow;

    /// <summary>An added mode's own melee targets (Musou's enemies): the nearest one this strike should reach, at its chest (game space).</summary>
    partial void ExtrasMeleeTarget(PlayerState me, MeleeStep step, ref System.Numerics.Vector3? at);

    /// <summary>
    /// The owner, 2026-11-02: the sound was late. A server's hit report comes a round trip after the blow, so the own swing's generated sound plays on the frame its hit time passes (the moment the
    /// damage is dealt, from <see cref="MeleeFxFor"/>) when an enemy stands in reach; the report then adds nothing. With nobody in reach the report still sounds, and a target that dodged in the last moment sounds a hit that dealt nothing (ours: rare, and the price of no delay).
    /// </summary>
    void OwnStrikeHitSound(PlayerState me, MeleeStep step, List<PlayerState> all)
    {
        if (NoMeleeSounds || _client is null || _config is null || ProceduralHitSound(me, charge: false, dash: false) is not { } synth) return;
        System.Numerics.Vector3? at = null;
        int victim = SmNet.MeleePresentation.Victim(me, all, _config, _client.MeleeLockTarget);
        if (victim > 0) foreach (var p in all) if (p.PlayerId == victim) { at = p.Position + new System.Numerics.Vector3(0f, 0f, 1.2f); break; }
        ExtrasMeleeTarget(me, step, ref at);   // EXTRAS-HOOK
        if (at is not { } spot) return;
        _ownStrikeSoundAt = _connectedFor;
        string played = PlayProceduralHit(synth, ToGodot(spot), "own strike");
        CountSound("melee hit");
        if (LogSound("melee") || LogSound("tag")) GD.Print($"MELEE HIT SOUND t {_connectedFor:F3} {MeleeWargear(me)} at the strike, event {played}");
    }

    AudioStreamWav[] HitStreams(HitSound sound)
    {
        if (_hitStreams.TryGetValue(sound, out var known)) return known;
        var renders = HitRender(sound).GetAwaiter().GetResult();   // built off the main thread when the sound system came up (PrewarmHitSounds): the first hit doesn't stall
        var set = new AudioStreamWav[renders.Length];
        for (int i = 0; i < set.Length; i++)
            set[i] = new AudioStreamWav { Data = renders[i], Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = MeleeHitSynth.SampleRate, Stereo = false };
        return _hitStreams[sound] = set;
    }

    static readonly Dictionary<HitSound, Task<byte[][]>> HitRenders = new();

    /// <summary>A sound's four renders as 16-bit bytes, computed once on a worker thread.</summary>
    static Task<byte[][]> HitRender(HitSound sound)
    {
        lock (HitRenders)
        {
            if (HitRenders.TryGetValue(sound, out var known)) return known;
            return HitRenders[sound] = Task.Run(() =>
            {
                var set = new byte[4][];
                for (int i = 0; i < set.Length; i++)
                {
                    var pcm = MeleeHitSynth.ToPcm16(MeleeHitSynth.Render(sound, 1000 * (int)sound + i + 1));
                    set[i] = new byte[pcm.Length * 2];
                    Buffer.BlockCopy(pcm, 0, set[i], 0, set[i].Length);
                }
                return set;
            });
        }
    }

    /// <summary>Starts both sounds' renders in the background, once the sound system is up.</summary>
    void PrewarmHitSounds()
    {
        if (OldMeleeHitSound) return;
        HitRender(HitSound.Sizzle);
        HitRender(HitSound.Roar);
    }

    /// <summary>Hotfix 2026-11-01: the generated hit sounds quieter (volume x 0.5, -6 dB; the owner: 40% quieter, then "slightly lowered" on 2026-11-02).</summary>
    static readonly float HitVolumeScaleDb = 20f * MathF.Log10(0.5f);

    /// <summary>Plays a procedural melee hit sound at <paramref name="at"/> (Godot space): one of four renders at a slightly different speed and level every time. Returns the text for the log.</summary>
    string PlayProceduralHit(HitSound sound, Vector3 at, string label)
    {
        var streams = HitStreams(sound);
        var (pitch, db) = MeleeHitSynth.Variation(_hitSoundCount * 7919u + (uint)sound);
        var stream = streams[(int)((_hitSoundCount + (uint)(db * 10f + 20f)) % (uint)streams.Length)];
        _hitSoundCount++; _proceduralHits++;
        var player = new AudioStreamPlayer3D
        {
            Stream = stream, Bus = SoundSystem.BusName, PitchScale = pitch, VolumeDb = db + SoundGainDb + HitVolumeScaleDb, GlobalPosition = at,
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance, UnitSize = 8f, MaxDistance = 90f,
        };
        player.Finished += player.QueueFree;
        AddChild(player);
        player.Play();
        return $"procedural sound {(sound == HitSound.Sizzle ? "A" : "B")} (render {Array.IndexOf(streams, stream)}, speed {pitch:0.000}, {db.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture)} dB) for {label}";
    }
}
