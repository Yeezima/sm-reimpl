using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmFormats.Audio;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Ork O8 (docs/ork-faction-design.md section 6): the Ork Boy's own sounds and barks. A player whose class has the Ork race makes the Ork's sounds instead of a marine's:
/// the blueprint is <c>ork_coop_boy</c> (<see cref="BlueprintName"/>), so its spawn and death actions, footsteps (<c>evt_left_foot</c> / <c>evt_right_foot</c> on the boy's
/// clips: <c>play_or_boy_footstep</c> on the surface under the feet), the landing and every <c>actn_snd_vox_*</c> tag a clip crosses are the boy's; the Ork wargear's
/// fire, reload, swing and throw sounds come with the weapons (the same <see cref="PlayerFiles"/> path as the marines'); and the voice lines are the speech bank's for the
/// character the blueprint names (<c>ork_boy_2</c>, <see cref="OrkSounds.SpeechCharacter"/>). The boy's clip tags join the marine rig's in the trigger table
/// (<see cref="OrkGraphsOrNull"/>), so a clip the Ork shows fires whatever its tags bind, as for a marine.
/// <para>
/// What the data does not say is when the boy grunts in a match, because his animations are not all shown yet (and the hits, deaths and kills are the simulation's):
/// <see cref="SoundOrk"/> posts the data's lines for the simulation's events (**ours**; rules in <see cref="OrkSounds"/>, tested): the death yell at the death
/// (<c>actn_snd_vox_death_yell_m_s</c>), a small or big grunt for damage taken (<c>actn_snd_vox_sync_damage_*</c>), the stun line (<c>health_ext/stunned_action</c>) at a stun or
/// stagger, the landing and its effort at the end of a dodge hop, the choppa's swing sound at the start of a light attack (its <c>start_audio</c>; the clip's swing tag is
/// skipped within <see cref="OrkSounds.DuplicateWindow"/> s), and, as the "silly" options (<c>--no-ork-silly</c> switches them off), a roar at a charge and at the start of a sprint
/// (<c>sfx_or_roar</c>, at most every <see cref="OrkSounds.RoarSpacing"/> s) and a taunt (<c>speech_celebrate</c>) <see cref="OrkSounds.TauntDelay"/> s after a kill by an Ork standing
/// still (<see cref="OrkKillSound"/>, called from the kill feed). A line posted while the character is still speaking is dropped (M33), which also stops a clip's tag and
/// the state-driven post from sounding twice. <c>--log-sound ork</c> prints every Ork post with the voices it resolved to.
/// </para>
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--no-ork-silly</c>: the Ork's roars and kill taunts are off (they are ours, not the data's triggers).</summary>
    bool OrkSillySounds => _orkSilly ??= !CmdFlag("no-ork-silly");
    bool? _orkSilly;

    readonly RecentPosts _recentPosts = new();
    bool _orkPrefetched;
    /// <summary>The emitters of players who currently speak and sound as Orks (for the log).</summary>
    readonly HashSet<ulong> _orkEmitterIds = new();
    readonly Dictionary<int, OrkSoundState> _orkSoundStates = new();

    sealed class OrkSoundState
    {
        public double LastTaunt = double.NegativeInfinity, LastRoar = double.NegativeInfinity;
        public bool WasSprinting;
    }

    OrkSoundState OrkSoundOf(int id)
    {
        if (!_orkSoundStates.TryGetValue(id, out var st)) _orkSoundStates[id] = st = new OrkSoundState();
        return st;
    }

    /// <summary>Whether a player sounds as an Ork (its class's race; independent of whether the Ork model is drawn).</summary>
    bool IsOrkVoice(PlayerState s) => _config is not null && Races.Of(_config, s.Class, s.Team) == Race.Orks;

    /// <summary>The Ork rig's behaviour graphs for the trigger table, read on the sound worker; null (noted) when they cannot be read: the Ork then has the blueprint's state sounds but no clip tags.</summary>
    static IReadOnlyList<SmFormats.Havok.BehaviorGraph>? OrkGraphsOrNull(string gameDir)
    {
        try { return OrkSounds.ReadGraphs(gameDir); }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or NotSupportedException or KeyNotFoundException or ArgumentException)
        {
            GD.PrintErr($"sound: the Ork rig's clip tags could not be read ({ex.Message}); the Ork has no footsteps or clip barks");
            return null;
        }
    }

    /// <summary>A tag's commands for a player: its sound events and its voice lines.</summary>
    void OrkTag(SoundFile file, string tag, SoundTrack t, PlayerState s, bool own, string kind)
    {
        foreach (var b in file.Tag(tag)) PostBinding(b, t.Emitter, own, kind);
        foreach (var b in file.SpeechTag(tag)) Bark(b, t, s, kind);
    }

    /// <summary>The Ork's state-driven sounds for one player after this frame's state (see the class comment).</summary>
    void SoundOrk(SoundTrack t, PlayerState s, PlayerState p, bool own)
    {
        _orkEmitterIds.Add(t.Emitter.Id);
        if (!_orkPrefetched) { _orkPrefetched = true; PrefetchOrkSounds(); }   // marine-only matches never pay for the Ork's lines
        if (_soundTriggers is null || _config is null || BlueprintFile(s) is not { } file) return;
        double now = _connectedFor;
        if (!s.Alive)
        {
            if (p.Alive)
            {
                _sound?.StopSpeech(t.Emitter);   // the death yell takes the place of a grunt still playing
                OrkTag(file, OrkSounds.DeathTag, t, s, own, "ork death");
            }
            return;
        }
        if (!p.Alive) return;
        var st = OrkSoundOf(s.PlayerId);

        // Taking damage (shield and health together, one step): the stun line for a stun or stagger, else a grunt of the hit's size.
        float lost = p.Health + p.Shield - (s.Health + s.Shield);
        if ((s.Reaction is ReactionKind.Stun or ReactionKind.Stagger) && p.Reaction != s.Reaction)
            foreach (var b in file.SpeechOf(OrkSounds.StunTrigger)) Bark(b, t, s, "ork stun");
        else if (OrkSounds.IsHit(lost)) OrkTag(file, OrkSounds.HitTagFor(lost), t, s, own, "ork hit");

        // The choppa's light swing: the ability's start_audio sound (its voice line is SoundMeleeBarks'); the swing clip's tag would post the same event a moment later.
        bool swing = s.MeleeStep != 0 && (p.MeleeStep != s.MeleeStep || s.MeleeTime + 1e-4f < p.MeleeTime || p.MeleeHeavy != s.MeleeHeavy);
        if (swing && !s.MeleeHeavy && MeleeWargear(s) is { } melee && _soundTriggers.File(melee) is { } meleeFile)
            foreach (var b in meleeFile.Of("start_audio").Where(b => b.StartEvent.Length > 0 && b.Ability.Equals("ability_melee_light", StringComparison.OrdinalIgnoreCase)))
            {
                PostBinding(b, t.Emitter, own, "ork swing");
                _recentPosts.Note(t.Emitter.Id, b.StartEvent, now);
            }

        // The end of a dodge hop: the boy's landing and its effort grunt (the hop is a short jump).
        if (p.EvadeTicks > 0 && s.EvadeTicks == 0 && s.Grounded)
        {
            SurfaceUnder(t, s);
            PostAll(file.Of(OrkSounds.LandTrigger), t.Emitter, own, "ork hop land");
            foreach (var b in file.SpeechTag(OrkSounds.LandingGruntTag)) Bark(b, t, s, "ork hop land");
        }

        // The silly options: a roar at a charge and at the start of a sprint.
        var c = _config.For(s.Class);
        float run = c.RunSpeed, sprint = c.SprintSpeed;
        float horizontal = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
        bool sprinting = s.Grounded && horizontal > run + 0.25f * Math.Max(0f, sprint - run);
        bool sprintStart = sprinting && !st.WasSprinting;
        st.WasSprinting = sprinting;
        bool charge = s.Rush == RushPhase.Rush && p.Rush != RushPhase.Rush;
        if ((charge || sprintStart) && OrkSillySounds && OrkSounds.Roars(now, st.LastRoar))
        {
            st.LastRoar = now;
            OrkTag(file, OrkSounds.RoarTag, t, s, own, charge ? "ork charge roar" : "ork sprint roar");
        }
    }

    /// <summary>
    /// A shot that struck <paramref name="victimId"/> (the victim found by where the shot ended, <c>VictimAt</c>; the shot's hit zone; the weapon's index in the configuration): a remote Ork
    /// grunts (health is not on the wire for others, so the state difference <see cref="SoundOrk"/> uses for the own Ork is not there). Rays and cones only: melee and blast hits on a remote Ork carry no
    /// shot record and are silent (a gap). The rule is <see cref="OrkSounds.ShotGruntTag"/>.
    /// </summary>
    void OrkShotHit(int victimId, ShotHit hit, int weaponIndex)
    {
        if (victimId < 0 || _config is null || _client is null || weaponIndex < 0 || weaponIndex >= _config.Weapons.Count) return;
        if (!_soundTracks.TryGetValue(victimId, out var t) || !t.Has) return;
        var s = t.Prev;
        if (OrkSounds.ShotGruntTag(hit, IsOrkVoice(s), s.Alive, victimId == _client.PlayerId, _config.Weapons[weaponIndex].Damage) is not { } tag || BlueprintFile(s) is not { } file) return;
        OrkTag(file, tag, t, s, false, "ork hit (shot)");
    }

    /// <summary>
    /// A kill from the kill feed: an Ork killer standing still taunts (<c>speech_celebrate</c>) a moment later (ours; <c>--no-ork-silly</c> switches it off). The Ork must be alive and
    /// slow enough when the taunt would start, and not have taunted within <see cref="OrkSounds.TauntSpacing"/> s.
    /// </summary>
    void OrkKillSound(KillView k)
    {
        if (!OrkSillySounds || _sound is null || _soundTriggers is null || k.Killer < 0 || k.Killer == k.Victim) return;
        if (!_soundTracks.TryGetValue(k.Killer, out var t) || !t.Has || !IsOrkVoice(t.Prev) || !t.Prev.Alive) return;
        int killer = k.Killer;
        double due = _connectedFor + OrkSounds.TauntDelay;
        _soundSchedule.Add((due, () =>
        {
            if (!_soundTracks.TryGetValue(killer, out var track) || !track.Has || !track.Prev.Alive || !IsOrkVoice(track.Prev) || BlueprintFile(track.Prev) is not { } file) return;
            var s = track.Prev;
            var st = OrkSoundOf(killer);
            float speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
            if (!OrkSounds.Taunts(speed, _connectedFor, st.LastTaunt)) return;
            st.LastTaunt = _connectedFor;
            OrkTag(file, OrkSounds.TauntTag, track, s, killer == _client?.PlayerId, "ork taunt");
        }));
    }

    /// <summary>Decodes ahead the Ork's lines and the events its triggers use, so the first grunt is not late.</summary>
    void PrefetchOrkSounds()
    {
        if (_audio is null || _soundTriggers is null || _sound is null || _config is null) return;
        if (!Enumerable.Range(0, _config.Classes.Count).Any(i => Races.Of(_config, i, Team.None) == Race.Orks)) return;
        if (_soundTriggers.File(OrkSounds.Blueprint) is not { } file) return;
        var events = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var speech = new List<SpeechBinding>();
        foreach (var tag in new[] { "evt_left_foot", OrkSounds.DeathTag, OrkSounds.HitSmallTag, OrkSounds.HitBigTag, OrkSounds.RoarTag, OrkSounds.TauntTag, OrkSounds.BarkTag, OrkSounds.LandingGruntTag })
        {
            foreach (var b in file.Tag(tag)) events.Add(b.StartEvent);
            speech.AddRange(file.SpeechTag(tag));
        }
        foreach (var b in file.Of("death_action").Concat(file.Of(OrkSounds.LandTrigger))) events.Add(b.StartEvent);
        speech.AddRange(file.SpeechOf(OrkSounds.StunTrigger));
        foreach (var name in OrkClass.Wargear)
            if (_soundTriggers.File(name) is { } wf)
            {
                foreach (var b in wf.Fire(null).Concat(wf.Of("start_audio")).Concat(wf.Tag("actn_fx_swing"))) events.Add(b.StartEvent);
                speech.AddRange(wf.SpeechOf("start_audio"));
            }
        var wems = events.Where(e => e.Length > 0).SelectMany(_audio.WemsOf).ToHashSet();
        if (_soundTriggers.SpeechCharacter(OrkClass.Key) is { } character)
            foreach (var s in speech)
            {
                var play = _audio.PostSpeech(s.Kind, character, s.Type, s.State, s.Threat.Length == 0 || s.Threat.Equals("autodetect", StringComparison.OrdinalIgnoreCase) ? "high" : s.Threat, new SoundSwitches());
                foreach (var v in play.Voices) wems.Add(v.WemId);
            }
        _sound.Prefetch(wems);
        GD.Print($"sound: Ork prefetch {wems.Count} wems of {events.Count} events and {speech.Count} voice lines (character {_soundTriggers.SpeechCharacter(OrkClass.Key) ?? "none"})");
    }

    /// <summary>The voices a post resolved to, for the logs: "n voices: wem id length 2D/3D".</summary>
    string VoicesText(SoundPlay? play) =>
        play is null ? "not posted" : !play.Found ? "event not in the banks" :
        $"{play.Voices.Count} voices" + (play.Voices.Count == 0 ? "" : ": " + string.Join("; ", play.Voices.Select(v => $"wem {v.WemId} {_audio?.Duration(v.WemId) ?? 0f:F2}s {(v.Is3D ? "3D" : "2D")}")));

    /// <summary><c>--log-sound ork</c>: every post of an Ork's emitter with what it resolved to.</summary>
    void LogBindingPost(SoundBinding b, SoundEmitter e, string kind, SoundPlay? play)
    {
        // feel/melee-charge-2 test aid: --log-sound tag prints every behaviour-tag post (the melee swings' events, which file posted them is in the trigger path).
        if (kind == "tag" && LogSound("tag")) GD.Print($"TAG SOUND t {_connectedFor:F3} {e.Label} [{b.Trigger}] {b.Path} event {b.StartEvent} stop '{b.StopEvent}' | {VoicesText(play)}");
        // fix/camera-zoom-misc test aid: --log-sound reload prints every reload-tag post (own: "reload", others: "reload other") with the event and what it resolved to, to count the posts of one reload.
        if (kind.StartsWith("reload", StringComparison.Ordinal) && LogSound("reload")) GD.Print($"RELOAD SOUND t {_connectedFor:F3} {e.Label} {kind} [{b.Trigger}] event {b.StartEvent} | {VoicesText(play)}");
        if (!LogSound("ork") || !_orkEmitterIds.Contains(e.Id)) return;
        GD.Print($"ORK SOUND t {_connectedFor:F3} {e.Label} {kind} [{b.Trigger}] event {b.StartEvent} | {VoicesText(play)}");
    }

    /// <summary><c>--log-sound ork</c> / <c>bark</c>: a voice line's result for a player.</summary>
    void LogBark(SpeechBinding b, SoundTrack t, PlayerState s, string kind, string character, SpeechResult result)
    {
        bool ork = _orkEmitterIds.Contains(t.Emitter.Id);
        if (!((ork && LogSound("ork")) || LogSound("bark"))) return;
        GD.Print($"{(ork ? "ORK " : "")}BARK t {_connectedFor:F3} {t.Emitter.Label} {kind} [{b.Trigger}] {b.Kind} {(b.Type.Length > 0 ? b.Type : b.State)} as {character} | {result}");
    }
}
