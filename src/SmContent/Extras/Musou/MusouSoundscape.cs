using SmSim.Extras.Musou;

namespace SmContent;

/// <summary>
/// musou/soundscape-tuning (owner interview, lead 25 chat, 2026-11-12; Musou Mode only, client presentation only: no sim, wire, protocol or hash change). The owner's answers: "Balanced battle" (nothing
/// drowns anything), problems "Too loud / clipping" and "Can't hear threats"; the crowd's voice count and levels "Keep as it is". The rules of what that asked for, each with a restore switch
/// (<c>--musou-sound-off clipguard,threat,softduck,bgduck,bed,waaagh</c>, <see cref="MusouSoundAb"/>). Every number is **ours** unless noted (spec "Musou soundscape tuning", refine register); the owner's
/// answers are the sources, the figures the lead picked and measured.
/// </summary>
public static class MusouSoundscape
{
    // ---- 1. clipping only ("Only fix what clips": levels and boosts stay) ----

    /// <summary>
    /// The Master bus's hard limiter sits before Master's +10 % volume (Godot applies a bus's volume after its effects), so its -0.3 dB ceiling came out at +0.53 dBFS at full master volume (measured
    /// in musou/sound-ab). In Musou Mode the ceiling is lowered by that boost, so the peak leaves at <see cref="SoundMixPass.MasterCeilingDb"/> (-0.3 dBFS), and a soft limiter ahead of it
    /// (<see cref="SoftThresholdDb"/>: it only acts on the few peaks above it) leaves the hard one little to do. Levels and the 10 % boost are untouched. Ours.
    /// </summary>
    public static readonly float GuardMasterCeilingDb = SoundMixPass.MasterCeilingDb - SoundMixPass.MasterBoostDb;
    /// <summary>The soft limiter's threshold, ceiling and soft-clip knee on Master (dB, dB, dB; ratio). Ours, chosen by the before/after measurement.</summary>
    public const float SoftThresholdDb = -4f, SoftCeilingDb = -1.2f, SoftClipDb = 2f, SoftClipRatio = 6f;

    // ---- 2. threat cues: close ones only, clear but natural ----

    /// <summary>The lift of a threat cue (dB; the owner: "about +3 dB when close or aimed at him"), and how many a second may post at most (a token bucket: a mob winding up behind him is not a wall of cues). Ours: the rate.</summary>
    public const float ThreatLiftDb = 3f;
    public const float ThreatPostsPerSecond = 6f, ThreatBurst = 4f;

    /// <summary>The kinds of threat the owner named: Boss and Nob attacks, rokkits and Big Shootas, Bomb Squigs, melee Orks behind him.</summary>
    public enum Threat : byte { BossAttack, NobSwing, MeleeBehind, Rokkit, BigShoota, SquigFuse }

    /// <summary>
    /// "Only close ones (aimed at the player or within a few metres)": metres. A boss's attack is a threat within <see cref="BossRadius"/> (his reach is long), or aimed at him within
    /// <see cref="BossAimedRadius"/>; a Nob's swing within <see cref="NobRadius"/> when he faces the player (or within <see cref="CloseRadius"/>); a melee Ork's wind-up when he faces the player within
    /// <see cref="CloseRadius"/> and stands outside the view (more than <see cref="OutsideViewDegrees"/> off the camera's forward: the owner's "from behind (outside the view)"); a rokkit when its aim point is within
    /// <see cref="RokkitAimRadius"/> of the player; a Big Shoota's burst when it is aimed at the player within <see cref="BigShootaRadius"/>; a Bomb Squig's fuse within <see cref="SquigRadius"/>. A facing counts as
    /// aimed within <see cref="AimedDegrees"/>. Ours: the metres and degrees (the owner said "a few metres").
    /// </summary>
    public const float CloseRadius = 6f, NobRadius = 10f, BossRadius = 15f, BossAimedRadius = 25f, RokkitAimRadius = 6f, BigShootaRadius = 40f, SquigRadius = 8f;
    public const float AimedDegrees = 60f, OutsideViewDegrees = 70f;

    /// <summary>Whether a cue of this kind at <paramref name="distance"/> m (for a rokkit: its aim point's distance from the player) is a close threat.</summary>
    public static bool Qualifies(Threat kind, float distance, bool aimed, bool outsideView) => kind switch
    {
        Threat.BossAttack => distance <= BossRadius || aimed && distance <= BossAimedRadius,
        Threat.NobSwing => distance <= CloseRadius || aimed && distance <= NobRadius,
        Threat.MeleeBehind => outsideView && aimed && distance <= CloseRadius,
        Threat.Rokkit => distance <= RokkitAimRadius,
        Threat.BigShoota => aimed && distance <= BigShootaRadius,
        Threat.SquigFuse => distance <= SquigRadius,
        _ => false,
    };

    /// <summary>Whether an Ork at <paramref name="from"/> facing <paramref name="yaw"/> (game yaw: forward is (-sin, cos)) is aimed at <paramref name="target"/>: within <see cref="AimedDegrees"/> of the line to it.</summary>
    public static bool IsAimedAt(System.Numerics.Vector3 from, float yaw, System.Numerics.Vector3 target)
    {
        float dx = target.X - from.X, dy = target.Y - from.Y, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f) return true;
        float dot = (-MathF.Sin(yaw) * dx + MathF.Cos(yaw) * dy) / len;
        return dot >= MathF.Cos(AimedDegrees * MathF.PI / 180f);
    }

    /// <summary>Whether a point lies outside the view: more than <see cref="OutsideViewDegrees"/> off the camera's forward (a flat direction, game space), seen from the camera.</summary>
    public static bool IsOutsideView(System.Numerics.Vector3 camera, System.Numerics.Vector2 forward, System.Numerics.Vector3 point)
    {
        float dx = point.X - camera.X, dy = point.Y - camera.Y, len = MathF.Sqrt(dx * dx + dy * dy), fl = forward.Length();
        if (len < 1e-3f || fl < 1e-3f) return false;
        return (forward.X * dx + forward.Y * dy) / (len * fl) < MathF.Cos(OutsideViewDegrees * MathF.PI / 180f);
    }

    // ---- 3. the deafening cut from big explosions: softer, shorter, close ones only ----

    /// <summary>
    /// The data's explosions put a Set/Reset volume and low-pass on three buses (<c>play_big_damage_med_explosion</c>: -6 dB and low-pass 60 for 1.5 s, measured in musou/sound-ab). In Musou Mode the cut
    /// happens only for a blast within <see cref="DuckRadius"/> m of the listener, at <see cref="DuckDepth"/> of its depth (so -3 dB and low-pass 30) and <see cref="DuckTime"/> of its time (delays and fades;
    /// so 0.75 s). Ours (the owner: "softer, shorter", close big blasts only).
    /// </summary>
    public const float DuckRadius = 12f, DuckDepth = 0.5f, DuckTime = 0.5f;

    /// <summary>The softened action, or null when the blast is too far for any cut. Only volume and low-pass Set values are made shallower (a Reset carries no value); every delay and fade is shortened.</summary>
    public static SoundPropertyAction? ShapeDuck(SoundPropertyAction a, float distance)
    {
        if (distance > DuckRadius) return null;
        float value = !a.Reset && (a.Property is SoundProperty.Volume or SoundProperty.Lpf) ? a.Value * DuckDepth : a.Value;
        return a with { Value = value, Delay = a.Delay * DuckTime, Fade = a.Fade * DuckTime };
    }

    // ---- 4. the far war ducks under close combat ----

    /// <summary>
    /// "Duck under close combat": the far battle (musou/battle-ambience) and the older war bed go <see cref="BackgroundDuckDb"/> dB down while an Ork engages the player (a wind-up or strike within
    /// <see cref="EngageRadius"/> m, or any Ork within <see cref="TouchRadius"/> m), reaching it in <see cref="BackgroundAttack"/> s, and come back over <see cref="BackgroundRelease"/> s once nothing has
    /// engaged him for <see cref="BackgroundHold"/> s. Ours.
    /// </summary>
    public const float BackgroundDuckDb = -4f, BackgroundAttack = 0.4f, BackgroundRelease = 2.5f, BackgroundHold = 1f;
    public const float EngageRadius = 8f, TouchRadius = 3f;

    // ---- 5. the Waaagh! war cries back ----

    /// <summary>Test aid: the armies' Waaagh! war cries are off again (since 2026-11-12 the interview, they are on by default; the far mob's yells stay off unless <see cref="MusouVoiceRules.AmbientVoicesFlag"/>).</summary>
    public const string NoWaaaghFlag = "--no-musou-waaagh";

    public static bool Waaagh(IReadOnlyCollection<string> args) => !args.Contains(NoWaaaghFlag);

    // ---- 6. Ashfall's wind and fires: a quiet bed under everything ----

    /// <summary>
    /// The install's own sounds: the world fire loop <c>play_world_fire_03</c> (in_game_default: a continuous container of four short crackles and a flame sound, 3D curves) on <see cref="BedEmitters"/>
    /// emitters on a ring of <see cref="BedRing"/> m round the listener, and the wind loop <c>play_wind_open</c> (in_game_default holds the event; its endless wind sound is in the campaign's region bank
    /// <see cref="WindPackage"/>, a singleplayer-content dependency loaded when the battle starts) on one emitter at the listener. Each at its gain over the event's own level (<see cref="FireGainDb"/>,
    /// <see cref="WindGainDb"/>). Ours: the gains, "low level".
    /// </summary>
    public const string FireEvent = "play_world_fire_03", WindEvent = "play_wind_open", WindStopEvent = "stop_wind_open", WindPackage = "rgn_onthetrail_r100_player_01";
    public const int BedEmitters = 2;
    public const float BedRing = 4f, FireGainDb = 0f, WindGainDb = -12f;

    /// <summary>The Ork a frame's engagement counts from: a melee wind-up or strike near enough, or anyone in touch.</summary>
    public static bool Engages(MusouEnemyState state, float distance) => distance <= TouchRadius || (state is MusouEnemyState.WindUp or MusouEnemyState.Strike) && distance <= EngageRadius;
}

/// <summary>A token bucket of the threat cues: <see cref="Refill"/> each frame, <see cref="Take"/> one for each cue (ours).</summary>
public sealed class MusouThreatBudget
{
    float _tokens = MusouSoundscape.ThreatBurst;
    public void Refill(float seconds) => _tokens = MathF.Min(MusouSoundscape.ThreatBurst, _tokens + MusouSoundscape.ThreatPostsPerSecond * seconds);
    public bool Take()
    {
        if (_tokens < 1f) return false;
        _tokens -= 1f;
        return true;
    }
}

/// <summary>The far war's duck (<see cref="MusouSoundscape.BackgroundDuckDb"/>): engaged holds it, then it releases; <see cref="Db"/> is the gain to add to the far war's voices now (0 or less).</summary>
public sealed class MusouBackgroundDucker
{
    double _heldUntil = double.NegativeInfinity, _now;

    /// <summary>The current duck (dB, 0 or negative).</summary>
    public float Db { get; private set; }

    public void Reset() { Db = 0f; _heldUntil = double.NegativeInfinity; }

    public void Step(float seconds, bool engaged)
    {
        _now += seconds;
        if (engaged) _heldUntil = _now + MusouSoundscape.BackgroundHold;
        float depth = -MusouSoundscape.BackgroundDuckDb;
        if (_now <= _heldUntil) Db = MathF.Max(MusouSoundscape.BackgroundDuckDb, Db - depth / MusouSoundscape.BackgroundAttack * seconds);
        else Db = MathF.Min(0f, Db + depth / MusouSoundscape.BackgroundRelease * seconds);
    }
}
