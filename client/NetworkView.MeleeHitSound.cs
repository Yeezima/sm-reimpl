using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SmClient.ImpactRules;
using SmSim;

namespace SmClient;

/// <summary>
/// fx/melee-hit-sound (the owner's requests of 2026-11-01; client presentation only: no simulation, wire or config-hash change; not under Extras: Faithful and Extended, classic PvP and Musou alike).
/// The on-hit sounds of two groups of melee weapons are **generated at run time** (<see cref="MeleeHitSynth"/>, no asset file; a provisional approximation measured from two reference clips; nobody
/// has heard it yet): sound A (a pink sizzle) for the power axe, power sword and combat knife; sound B (an instant roar with a crackling tail) for the chainsword, the thunder hammer, the daemon maul and
/// the charge hit (key F, the bull rush) of the Tactical and Assault marines and their Chaos mirrors. The own player's hit reports and the others' inferred hits both come here. The hammer's and maul's blast layer
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

    /// <summary>The procedural sound that replaces a melee hit's original, or null (the original plays): by the attacker's weapon, or for a charge hit (bull rush, not the dash attack) by its class.</summary>
    HitSound? ProceduralHitSound(PlayerState attacker, bool charge, bool dash)
    {
        if (OldMeleeHitSound || _config is null) return null;
        if (charge) return null;   // every charge keeps its original sound (hotfix 2026-11-01)
        return MeleeHitSynth.Replaces(MeleeWargear(attacker));
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

    /// <summary>Plays a procedural melee hit sound at <paramref name="at"/> (Godot space): one of four renders at a slightly different speed and level every time. Returns the text for the log.</summary>
    /// <summary>Hotfix 2026-11-01: the generated hit sounds 40% quieter (volume x 0.6, about -4.4 dB).</summary>
    static readonly float HitVolumeScaleDb = 20f * MathF.Log10(0.6f);

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
