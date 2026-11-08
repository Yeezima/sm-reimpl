using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SmClient.ImpactRules;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// sound/original-hits (the owner's request of 2026-11-04; client presentation only: no simulation, wire or config-hash change; not under Extras: classic PvP and Musou alike).
/// <b>The melee hit sounds are the original's.</b> A melee ability's hit sound is the <c>sfx</c> event of the container its <c>primary_target_actions</c> (the first target) or
/// <c>secondary_target_actions</c> (further targets of the same swing) name (<see cref="SoundTriggers.MeleeHitEvents"/>, read per wargear and ability from the install). The client maps the
/// swing to the data ability from the simulation's strike (<see cref="MeleeTuning.WeaponMap.AbilityOf"/>: light or heavy, step number) and posts the event when the hit resolves: the own
/// player's at the strike (the frame its hit time passes with an enemy in reach, <see cref="OwnStrikeHitSound"/>, every weapon; the server's report of that swing stays silent for that
/// victim), the others' as inferred from the snapshots (<see cref="SoundMeleeOthers"/>). Nothing of ours is layered on: a charge plays the body smash only, the hammer and the maul no displace blast.
/// Replaces the generated sounds of fx/melee-hit-sound (2026-11-01/02: <see cref="MeleeHitSynth"/>, a provisional approximation), kept behind a test switch.
/// Test aids: <c>--generated-melee-hit-sound</c> (the synth for the power axe, power sword, thunder hammer and daemon maul, and Musou's no-bass), <c>--charge-hit-layer</c> (a charge also plays the
/// weapon's own hit sound), <c>--hammer-displace-layer</c> (the hammer's and maul's blast events with a hit), <c>--old-melee-hit-sound</c> (both layers, no synth),
/// <c>--log-sound melee</c> (ability, rank and event of every post).
/// </summary>
public partial class NetworkView
{
    bool OldMeleeHitSound => _oldMeleeHitSound ??= CmdFlag("old-melee-hit-sound");
    bool? _oldMeleeHitSound;
    /// <summary><c>--generated-melee-hit-sound</c>: the run-time synth replaces the data's hit sound of the power axe, power sword, thunder hammer and daemon maul again (fx/melee-hit-sound).</summary>
    bool GeneratedMeleeHitSound => _generatedMeleeHitSound ??= CmdFlag("generated-melee-hit-sound");
    bool? _generatedMeleeHitSound;
    /// <summary><c>--charge-hit-layer</c> (or <c>--old-melee-hit-sound</c>): a charge hit also plays the charging weapon's own hit sound under the body smash (anim/melee-interaction, ours).</summary>
    bool ChargeHitLayer => _chargeHitLayer ??= CmdFlag("charge-hit-layer") || OldMeleeHitSound;
    bool? _chargeHitLayer;
    /// <summary><c>--hammer-displace-layer</c> (or <c>--old-melee-hit-sound</c>): the hammer's and maul's blast events (<see cref="PowerFlash.BlastEvents"/>) play with a hit (ours).</summary>
    bool HammerDisplaceLayer => _hammerDisplaceLayer ??= CmdFlag("hammer-displace-layer") || OldMeleeHitSound;
    bool? _hammerDisplaceLayer;
    readonly Dictionary<(HitSound, bool), AudioStreamWav[]> _hitStreams = new();
    uint _hitSoundCount;
    int _proceduralHits;

    // ---- the data's hit sound of a swing ----

    /// <summary>The own swings and the victims each has sounded for: which server hit reports are already sounded at the strike (<see cref="OwnSwingLedger"/>, matched by swing, not by clock).</summary>
    readonly OwnSwingLedger _ownSwings = new();

    /// <summary>
    /// The data's hit events of a strike: the attacker's wargear, the ability its strike maps to (<see cref="MeleeTuning.WeaponMap.AbilityOf"/>) and that ability's primary or secondary
    /// target events. A strike unknown here (step 0: a report without a swing in memory) takes the first light attack's ability (ours). Empty when the data has none.
    /// </summary>
    (string? Wargear, string Ability, IReadOnlyList<string> Events) MeleeHitData(PlayerState attacker, bool heavy, int step, bool secondary)
    {
        string? wargear = MeleeWargear(attacker);
        if (wargear is null || _soundTriggers is null) return (wargear, "", []);
        string ability = _soundTriggers.MeleeHitAbility(wargear, heavy, step);
        return (wargear, ability, _soundTriggers.MeleeHitEvents(wargear, ability, secondary));
    }

    /// <summary>
    /// Posts a swing's data hit events at <paramref name="at"/> (Godot space; the armour and flesh layers together, <see cref="PostPlayerHitSound"/>). Returns the events joined, or null when the data
    /// has none for the strike. The log line says the ability, the strike and the rank.
    /// </summary>
    string? PostDataMeleeHit(PlayerState attacker, bool heavy, int step, bool secondary, string target, Vector3 at, string source, bool? armoured = null)
    {
        if (_sound is null) return null;
        var (wargear, ability, events) = MeleeHitData(attacker, heavy, step, secondary);
        string rank = secondary ? "secondary" : "primary", strike = $"{(heavy ? "heavy" : "light")} {step}";
        if (events.Count == 0)
        {
            if (LogSound("melee")) GD.Print($"MELEE HIT SOUND t {_connectedFor:F3} P{attacker.PlayerId} melee on {target} ({source}) wargear {wargear ?? "-"} strike {strike} ability {(ability.Length > 0 ? ability : "-")} rank {rank}: no hit sound in the data");
            return null;
        }
        foreach (var evt in events) PostPlayerHitSound(evt, new SoundEmitter { Position = at, Label = $"melee hit on {target}", OneShot = true }, LoudnessArg.MeleeHit, armoured);   // sound/louder-farther: melee hits x2
        CountSound(secondary ? "melee hit secondary" : "melee hit");
        _meleeHitPosts++;
        string joined = string.Join("+", events);
        if (LogSound("melee")) GD.Print($"MELEE HIT SOUND t {_connectedFor:F3} P{attacker.PlayerId} melee on {target} ({source}) wargear {wargear} strike {strike} ability {ability} rank {rank} event {joined}");
        return joined;
    }

    /// <summary>The procedural sound that replaces a melee hit's original, or null (the original plays; the default since sound/original-hits): only with <c>--generated-melee-hit-sound</c>, by the attacker's weapon; a charge hit never.</summary>
    HitSound? ProceduralHitSound(PlayerState attacker, bool charge, bool dash)
    {
        if (OldMeleeHitSound || !GeneratedMeleeHitSound || _config is null) return null;
        if (charge) return null;   // every charge keeps its original sound (hotfix 2026-11-01)
        return MeleeHitSynth.Replaces(MeleeWargear(attacker));
    }

    /// <summary>Seconds after the own strike's sound during which the server's reports of that swing stay silent: past a normal round trip, short of the next swing's hit (ours).</summary>
    const double OwnStrikeWindow = 0.35;
    double _ownStrikeSoundAt = double.NegativeInfinity;

    /// <summary>Whether the own swing's hit sound has just been played at the strike.</summary>
    bool OwnStrikeSounded => _connectedFor - _ownStrikeSoundAt <= OwnStrikeWindow;

    /// <summary>An added mode's own melee targets (Musou's enemies): the nearest one this strike should reach, at its chest (game space).</summary>
    partial void ExtrasMeleeTarget(PlayerState me, MeleeStep step, ref System.Numerics.Vector3? at, ref bool? armoured);

    /// <summary>
    /// The owner, 2026-11-02: the sound was late. A server's hit report comes a round trip after the blow, so the own swing's hit sound plays on the frame its hit time passes (the moment the
    /// damage is dealt, from <see cref="MeleeFxFor"/>) when an enemy stands in reach; the report then adds nothing for that victim. With nobody in reach the report still sounds (as the swing's
    /// first target), a further victim the report names sounds as a secondary target (a round trip late: ours), and a target that dodged in the last moment sounds a hit that dealt nothing
    /// (ours: rare, and the price of no delay). Since sound/original-hits this holds for every weapon, with the data's event for the swing's ability (primary rank).
    /// </summary>
    void OwnStrikeHitSound(PlayerState me, MeleeStep step, List<PlayerState> all)
    {
        if (NoMeleeSounds || NoSound || Lean || _sound is null || _client is null || _config is null) { _ownSwings.Strike(_connectedFor, me.MeleeHeavy, me.MeleeStep, sounded: false, 0); return; }
        var synth = ProceduralHitSound(me, charge: false, dash: false);
        if (synth is null && _soundTriggers is null) { _ownSwings.Strike(_connectedFor, me.MeleeHeavy, me.MeleeStep, sounded: false, 0); return; }
        System.Numerics.Vector3? at = null;
        int victim = SmNet.MeleePresentation.Victim(me, all, _config, _client.MeleeLockTarget);
        if (victim > 0) foreach (var p in all) if (p.PlayerId == victim) { at = p.Position + new System.Numerics.Vector3(0f, 0f, 1.2f); break; }
        bool? armoured = null;
        ExtrasMeleeTarget(me, step, ref at, ref armoured);   // EXTRAS-HOOK: an added mode names the material of its own target (Musou Mode: flesh for an unarmoured Ork)
        _ownSwings.Strike(_connectedFor, me.MeleeHeavy, me.MeleeStep, sounded: at is not null, victim);
        if (at is not { } spot) return;
        _ownStrikeSoundAt = _connectedFor;
        string? played = synth is { } s ? PlayProceduralHit(s, ToGodot(spot), "own strike") : PostDataMeleeHit(me, me.MeleeHeavy, me.MeleeStep, secondary: false, victim > 0 ? $"P{victim}" : "an enemy", ToGodot(spot), "at the strike", armoured);
        if (synth is not null) { CountSound("melee hit"); if (LogSound("melee") || LogSound("tag")) GD.Print($"MELEE HIT SOUND t {_connectedFor:F3} {MeleeWargear(me)} at the strike, event {played}"); }
    }

    AudioStreamWav[] HitStreams(HitSound sound)
    {
        bool noBass = HitNoBass;
        if (_hitStreams.TryGetValue((sound, noBass), out var known)) return known;
        var renders = HitRender(sound, noBass).GetAwaiter().GetResult();   // built off the main thread when the sound system came up (PrewarmHitSounds): the first hit doesn't stall
        var set = new AudioStreamWav[renders.Length];
        for (int i = 0; i < set.Length; i++)
            set[i] = new AudioStreamWav { Data = renders[i], Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = MeleeHitSynth.SampleRate, Stereo = false };
        return _hitStreams[(sound, noBass)] = set;
    }

    /// <summary>Musou Mode's hit sounds have no bass (the owner, 2026-11-02); classic PvP keeps it, a question left to the owner.</summary>
    bool HitNoBass => _config?.Extras.HasFlag(SmSim.ContentExtras.Musou) == true;

    static readonly Dictionary<(HitSound, bool), Task<byte[][]>> HitRenders = new();

    /// <summary>A sound's four renders as 16-bit bytes, computed once on a worker thread.</summary>
    static Task<byte[][]> HitRender(HitSound sound, bool noBass)
    {
        lock (HitRenders)
        {
            if (HitRenders.TryGetValue((sound, noBass), out var known)) return known;
            return HitRenders[(sound, noBass)] = Task.Run(() =>
            {
                var set = new byte[4][];
                for (int i = 0; i < set.Length; i++)
                {
                    var pcm = MeleeHitSynth.ToPcm16(MeleeHitSynth.Render(sound, 1000 * (int)sound + i + 1, noBass));
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
        if (OldMeleeHitSound || !GeneratedMeleeHitSound) return;
        HitRender(HitSound.Sizzle, HitNoBass);
        HitRender(HitSound.Roar, HitNoBass);
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
            Stream = stream, Bus = SoundSystem.MixClassic ? SoundSystem.BusName : SoundSystem.HitBusName, PitchScale = pitch, GlobalPosition = at,
            // sound/louder-farther: the loudness rule and the melee hit gain; the distance (inverse from 8 m times the range scale, never under the floor) set once at the start, Godot's model off.
            VolumeDb = db + SoundGainDb + HitVolumeScaleDb + SoundMixPass.Hit(SoundSystem.MixClassic).GainDb + LoudnessArg.GainDb + LoudnessArg.MeleeHitGainDb
                       + LoudnessArg.InverseDistanceDb(GetViewport()?.GetCamera3D() is { } cam ? cam.GlobalPosition.DistanceTo(at) : 0f, 8f),
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled, MaxDistance = 0f,
        };
        player.Finished += player.QueueFree;
        AddChild(player);
        player.Play();
        return $"procedural sound {(sound == HitSound.Sizzle ? "A" : "B")} (render {Array.IndexOf(streams, stream)}, speed {pitch:0.000}, {db.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture)} dB) for {label}";
    }
}
