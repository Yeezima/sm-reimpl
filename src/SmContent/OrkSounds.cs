using SmFormats.Baf;
using SmFormats.Havok;

namespace SmContent;

/// <summary>
/// The Ork Boy's sounds and barks (Ork O8; docs/ork-faction-design.md section 6). What the data binds is read like the players' files
/// (<see cref="SoundTriggers"/>): the Exterminatus boy's blueprint <see cref="Blueprint"/> (<c>ork_coop_boy</c>: footsteps on
/// <c>evt_left_foot</c> / <c>evt_right_foot</c>, the landing, the death action, the stun action, and the <c>actn_snd_vox_*</c> tags that the boy's clips
/// carry for his grunts, roars and yells), the Ork wargear (slugga, shoota, choppa, stikkbomb: fire, reload, swing, throw) and the boy rig's behaviour
/// graphs (the clip → tag table). The voice lines are character voices: <c>autodetect</c> reads the blueprint's <c>audio_ext/character</c>
/// (<see cref="SpeechCharacter"/>: <c>ork_boy_2</c> on the install; the Ork has no entry in <c>global</c>'s class table), resolved through the speech bank
/// like the marines' (M33). This file holds only what is not in the data: which tag stands for which game event when no clip carries it (the Ork's death, hit, stun,
/// hop landing, charge and kill-taunt barks), thresholds and spacings. Every such rule is **ours** (spec "Ork sounds (O8)"); the names and events are the data's.
/// </summary>
public static class OrkSounds
{
    /// <summary>The Ork Boy's blueprint (the file the sounds and the speech character come from).</summary>
    public const string Blueprint = OrkClass.Blueprint;

    // ---- the data's tags, used for the state-driven barks (a tag whose commands hold the line; nothing is invented, only when it is posted) ----
    /// <summary>The death yell (<c>2_exertion_type</c> = death); on the boy's death clips. Posted at the death when no clip carries it (ours).</summary>
    public const string DeathTag = "actn_snd_vox_death_yell_m_s";
    /// <summary>The grunts for taking damage (<c>damage_small</c> / <c>damage_big</c>), the sync-damage tags of the hit reactions. Posted at a hit (ours).</summary>
    public const string HitSmallTag = "actn_snd_vox_sync_damage_small", HitBigTag = "actn_snd_vox_sync_damage_big";
    /// <summary>The roar (<c>2_exertion_type</c> = roar) and the celebration (taunt) exertions; the short bark event <c>play_or_bo_2_barks</c> sits on <see cref="BarkTag"/>.</summary>
    public const string RoarTag = "sfx_or_roar", TauntTag = "speech_celebrate", BarkTag = "actn_snd_vox_taunt_m_s";
    /// <summary>The effort grunt on a landing (<c>effort_short_stab</c>); posted at the end of a dodge hop (ours).</summary>
    public const string LandingGruntTag = "actn_snd_jump_landing";
    /// <summary>The blueprint's stun action (<c>health_ext/stunned_action</c>: exertion <c>stunned</c>).</summary>
    public const string StunTrigger = "stunned_action";
    /// <summary>The blueprint's landing (<c>biped_locomotion_ext/finish_falling_actions</c>: <c>play_or_land</c>).</summary>
    public const string LandTrigger = "finish_falling_actions";

    // ---- when (ours) ----
    /// <summary>Damage taken (shield and health together) in one snapshot step at or above which the big grunt plays, below it the small one (ours).</summary>
    public const float BigHit = 25f;
    /// <summary>Damage taken below which no grunt plays (ours: rounding and the shield's regeneration are not hits).</summary>
    public const float MinHit = 1f;
    /// <summary>Seconds after a kill by a standing Ork at which the taunt starts (ours).</summary>
    public const float TauntDelay = 0.6f;
    /// <summary>An Ork taunts only when its speed is at most this (m/s; ours: the design's "standing still").</summary>
    public const float TauntMaxSpeed = 2f;
    /// <summary>Least seconds between one Ork's taunts, and between its sprint and charge roars (ours; the design's 6 s).</summary>
    public const double TauntSpacing = 4.0, RoarSpacing = 6.0;
    /// <summary>The same sound event posted twice for one emitter within this many seconds plays once (ours: a swing is bound at the ability's start and on the clip's tag).</summary>
    public const double DuplicateWindow = 0.45;

    /// <summary>The tag whose commands carry the grunt for taking <paramref name="damage"/> (shield and health together).</summary>
    public static string HitTagFor(float damage) => damage >= BigHit ? HitBigTag : HitSmallTag;

    /// <summary>
    /// The tag whose grunt a shot record's hit sets off (S1: another player's health is not on the wire, so a remote Ork's grunt comes from the shots that struck it): a body or head
    /// hit on a living Ork that is not the local player (its own damage is seen in its own record), sized by the weapon's damage per hit; null for anything else.
    /// </summary>
    public static string? ShotGruntTag(SmSim.ShotHit hit, bool victimIsOrk, bool victimAlive, bool victimIsOwn, float damagePerHit) =>
        hit is SmSim.ShotHit.Body or SmSim.ShotHit.Head && victimIsOrk && victimAlive && !victimIsOwn && IsHit(damagePerHit) ? HitTagFor(damagePerHit) : null;

    /// <summary>Whether a taking of damage is a hit that grunts.</summary>
    public static bool IsHit(float damage) => damage >= MinHit;

    /// <summary>Whether a kill taunts: the Ork is slow enough, and has not taunted within <see cref="TauntSpacing"/>.</summary>
    public static bool Taunts(float speed, double now, double lastTaunt) => speed <= TauntMaxSpeed && now - lastTaunt >= TauntSpacing;

    /// <summary>Whether a roar (sprint start, charge) plays: none within <see cref="RoarSpacing"/>.</summary>
    public static bool Roars(double now, double lastRoar) => now - lastRoar >= RoarSpacing;

    /// <summary>The speech character of the Ork's blueprint (<c>extensions/audio_ext/character</c>), or null.</summary>
    public static string? SpeechCharacter(BafDocument blueprint) =>
        blueprint.Root.Table("extensions/audio_ext")?.String("character") is { Length: > 0 } c ? c : null;

    /// <summary>The Ork rig's behaviour graphs (their clips' tags name the Ork's footsteps, grunts and swings). Reads the rig (a worker's job).</summary>
    public static IReadOnlyList<BehaviorGraph> ReadGraphs(string gameDir) => HavokBehavior.Read(RigProfiles.OrkBoy.LoadCharacter(gameDir).Pack);
}

/// <summary>
/// Ability-start sounds an emitter posted a moment ago (<see cref="OrkSounds.DuplicateWindow"/>): the choppa's swing is bound twice in the data, at the start of the
/// ability (<c>start_audio</c>) and on the swing clip's tag (<c>actn_fx_swing</c>); the tag's post is skipped when the start's was just made, so a swing sounds once.
/// </summary>
public sealed class RecentPosts
{
    readonly Dictionary<(ulong Emitter, string Event), double> _last = new();

    /// <summary>Records that the event was posted for the emitter at <paramref name="now"/>.</summary>
    public void Note(ulong emitter, string evt, double now)
    {
        _last[(emitter, evt)] = now;
        if (_last.Count > 256) foreach (var key in _last.Where(kv => now - kv.Value > 5.0).Select(kv => kv.Key).ToList()) _last.Remove(key);
    }

    /// <summary>Whether the event was noted for the emitter within <paramref name="window"/> seconds before <paramref name="now"/>.</summary>
    public bool Recent(ulong emitter, string evt, double now, double window) =>
        _last.TryGetValue((emitter, evt), out double at) && now >= at && now - at < window;
}
