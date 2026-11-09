using SmFormats.Audio;

namespace SmContent;

/// <summary>The far battle's kinds of sound (musou/battle-ambience).</summary>
public enum MusouAmbienceKind : byte { OrkMob, Bolter, Lasgun, Artillery, Explosion }

/// <summary>
/// One kind of the far battle: its events (a <c>speech:character:exertion</c> entry is a character's exertion line), the seconds between two of its sounds, how far round the listener they
/// are (m), how many shots (or voices) one sound has and the seconds between them, its gain (dB) and added low-pass (LPF units), and the shake (radians) one of them gives the camera.
/// </summary>
public sealed record MusouAmbienceSpec(MusouAmbienceKind Kind, IReadOnlyList<string> Events, float EveryMin, float EveryMax, float Near, float Far, int ShotsMin, int ShotsMax,
    float GapMin, float GapMax, float GainDb, float Lpf, float ShakeRadians = 0f);

/// <summary>One sound of the far battle, due at <see cref="At"/> (connection seconds): which event, from which bearing (radians) and distance (m), and whether it shakes the camera.</summary>
public readonly record struct MusouAmbienceSound(MusouAmbienceKind Kind, string Event, float Bearing, float Distance, double At, float Shake, float GainDb, float Lpf);

/// <summary>
/// musou/battle-ambience (owner, 2026-11-08: "add looping barks and voice lines for orks, bolter fire, lasgun fire, artillery, explosions. any time artillery explosion sounds play the screen
/// shakes a tiny bit - semi often in musou mode, to make the map feel more alive"). Musou Mode's battles only; the client plays them (<c>NetworkView.TfwBattleAmbience.cs</c>) through the sound
/// system, so the loudness rule applies. Every event is the install's (default banks); every number is **ours** (spec "Musou battle ambience", refine register).
/// </summary>
public static class MusouAmbienceRules
{
    /// <summary>Speed of sound (m/s): an artillery shell's boom and its shake arrive this late after it lands (as the orbital strike's, <c>TfwFireView</c>).</summary>
    public const float SoundSpeed = 340f;

    public static readonly IReadOnlyList<MusouAmbienceSpec> Specs =
    [
        new(MusouAmbienceKind.OrkMob, ["play_or_bo_1_growls", "play_or_bo_2_barks", "play_or_bo_2_roar", "play_or_bo_1_roar", "play_or_bo_2_snorts",
                "speech:ork_boy:celebration", "speech:ork_boy_2:roar", "speech:ork_nob:roar", "speech:ork_nob:celebration", "speech:ork_boy:effort_build"],
            // musou/audio-pass (owner 2026-11-09: "ensure ork ambient voices sound like they are from far away"): well beyond the crowd's near Orks (they stay as they were), quieter and dull (LPF 40 = about 2.7 kHz).
            EveryMin: 1.2f, EveryMax: 3.5f, Near: 60f, Far: 130f, ShotsMin: 2, ShotsMax: 4, GapMin: 0.1f, GapMax: 0.5f, GainDb: -8f, Lpf: 40f),
        new(MusouAmbienceKind.Bolter, ["play_sm_bolter_fire"], EveryMin: 2f, EveryMax: 5f, Near: 40f, Far: 100f, ShotsMin: 3, ShotsMax: 7, GapMin: 0.12f, GapMax: 0.17f, GainDb: -4f, Lpf: 10f),
        new(MusouAmbienceKind.Lasgun, ["play_ig_lasgun_fire"], EveryMin: 1.5f, EveryMax: 4f, Near: 40f, Far: 100f, ShotsMin: 2, ShotsMax: 6, GapMin: 0.18f, GapMax: 0.35f, GainDb: -2f, Lpf: 10f),
        new(MusouAmbienceKind.Artillery, ["play_ork_rokk_distant_explosion", "play_sm_missile_explosion", "play_powersource_explode_distant"],
            EveryMin: 7f, EveryMax: 15f, Near: 60f, Far: 140f, ShotsMin: 1, ShotsMax: 2, GapMin: 0.6f, GapMax: 1.5f, GainDb: 0f, Lpf: 5f, ShakeRadians: ArtilleryShake),
        new(MusouAmbienceKind.Explosion, ["play_impact_distant", "play_big_damage_med_explosion", "play_sm_grenade_explode_mp", "play_plasma_cannon_explosion"],
            EveryMin: 3f, EveryMax: 8f, Near: 40f, Far: 110f, ShotsMin: 1, ShotsMax: 1, GapMin: 0f, GapMax: 0f, GainDb: -3f, Lpf: 10f),
    ];

    /// <summary>An artillery boom's shake (radians; ours: "a tiny bit", a fifth of the orbital strike's 0.05 at 0 m). Also given to the older war bed's artillery booms.</summary>
    public const float ArtilleryShake = 0.01f;

    /// <summary>Whether an event is an artillery explosion (it shakes the camera a tiny bit wherever it plays in the far battle).</summary>
    public static bool IsArtillery(string evt) => Specs[(int)MusouAmbienceKind.Artillery].Events.Contains(evt);

    public static MusouAmbienceSpec SpecOf(MusouAmbienceKind kind) => Specs[(int)kind];
}

/// <summary>The far battle's clock: each kind keeps its own next time; <see cref="Step"/> hands out what is due (all shots of a burst at once, each with its own time).</summary>
public sealed class MusouAmbienceScheduler(int seed = 1108)
{
    readonly Random _roll = new(seed);
    readonly double[] _next = new double[MusouAmbienceRules.Specs.Count];
    bool _started;

    float Range(float a, float b) => a + _roll.NextSingle() * (b - a);

    /// <summary>Starts over (a battle begins): each kind's first sound comes within its own interval.</summary>
    public void Reset(double now)
    {
        _started = true;
        for (int k = 0; k < _next.Length; k++) _next[k] = now + Range(0.3f, MusouAmbienceRules.Specs[k].EveryMax);
    }

    /// <summary>The sounds that start between the last call and <paramref name="now"/> (their <see cref="MusouAmbienceSound.At"/> may lie a little ahead: the shots of a burst, a shell's late boom).</summary>
    public List<MusouAmbienceSound> Step(double now)
    {
        var due = new List<MusouAmbienceSound>();
        if (!_started) Reset(now);
        for (int k = 0; k < _next.Length; k++)
        {
            var s = MusouAmbienceRules.Specs[k];
            while (_next[k] <= now)
            {
                double at = _next[k];
                _next[k] = at + Range(s.EveryMin, s.EveryMax);
                float bearing = Range(0f, MathF.Tau), distance = Range(s.Near, s.Far);
                int shots = s.ShotsMin + _roll.Next(s.ShotsMax - s.ShotsMin + 1);
                double t = at + (s.Kind == MusouAmbienceKind.Artillery ? distance / MusouAmbienceRules.SoundSpeed : 0.0);
                string evt = s.Events[_roll.Next(s.Events.Count)];
                for (int n = 0; n < shots; n++)
                {
                    // A mob's voices are different Orks (each its own event, a little apart); a burst's shots one gun (the same event).
                    if (s.Kind == MusouAmbienceKind.OrkMob && n > 0) { evt = s.Events[_roll.Next(s.Events.Count)]; bearing += Range(-0.15f, 0.15f); }
                    float shake = s.ShakeRadians * Math.Clamp(1.4f - distance / s.Far, 0.4f, 1f);   // nearer booms shake a little more
                    due.Add(new MusouAmbienceSound(s.Kind, evt, bearing, distance, t, s.ShakeRadians > 0f ? shake : 0f, s.GainDb, s.Lpf));
                    t += Range(s.GapMin, s.GapMax);
                }
            }
        }
        return due;
    }
}

/// <summary>
/// musou/battle-ambience (owner, 2026-11-08: "add random barks for space marines, the player, allied space marines, retainers in musou mode. use intense voice lines from the campaign for this
/// purpose"). The player speaks as Titus, every other Marine (the allied pair and other players) as Sidonus or Leandros (by id), from the campaign's lines in the speech bank (<c>speech_01</c>;
/// a singleplayer-content dependency, noted in docs/discovery.md). Which lines are "intense" is **ours**: the dialogue events and keys below have no names in the data read; they were chosen as the
/// campaign squad's combat sets by their shape (smtool audio-speech). Timing all ours.
/// </summary>
public static class MusouMarineBarkRules
{
    public const string PlayerCharacter = "titus";
    static readonly string[] AllyCharacters = ["sidonus", "leandros"];

    /// <summary>The character a Marine speaks as.</summary>
    public static string CharacterOf(bool own, int playerId) => own ? PlayerCharacter : AllyCharacters[(playerId & int.MaxValue) % AllyCharacters.Length];

    /// <summary>The two unnamed dialogue events whose campaign-squad sets are used (hashes; arguments: character, two unnamed, threat level) and their sets' second keys.</summary>
    public const uint ChatterEvent = 0x33c89dfa, ChatterEvent2 = 0x1930d1dd;
    public const uint SetBig = 0xfce76c70, SetCall = 0x3486a710, SetShort = 0x8e781e2c;
    /// <summary><c>speech_status</c> (name found by hashing) and its unnamed set that Titus has two lines of.</summary>
    public static readonly uint StatusEvent = Fnv.Hash32("speech_status");
    public const uint SetStatus = 0x2a767f17;

    /// <summary>The line sets of a character: (dialogue event, key path). Sets a character lacks give no lines (the client keeps the ones that do). Exertions: the battle cries.</summary>
    public static IReadOnlyList<(uint Event, uint[] Prefix)> LineSets(string character)
    {
        uint c = Fnv.Hash32(character);
        return
        [
            (ChatterEvent, [c, SetBig]), (ChatterEvent, [c, SetCall]), (ChatterEvent, [c, SetShort]), (ChatterEvent2, [c, SetShort]),
            (AudioCatalog.SpeechExertionEvent, [c, Fnv.Hash32("celebration")]), (AudioCatalog.SpeechExertionEvent, [c, Fnv.Hash32("roar")]),
            (StatusEvent, [c, SetStatus]),   // speech_status: Titus's two lines of this set
        ];
    }

    /// <summary>A Marine with an Ork this near is fighting (m).</summary>
    public const float CombatRange = 30f;
    /// <summary>Seconds between one Marine's barks: fighting, and not.</summary>
    public const float CombatEveryMin = 6f, CombatEveryMax = 14f, CalmEveryMin = 20f, CalmEveryMax = 40f;
    /// <summary>Seconds between any two barks (the squad does not talk over itself), and the farthest Marine heard barking (m).</summary>
    public const float Spacing = 2f, HearRange = 60f;
}

/// <summary>When each Marine barks next (<see cref="MusouMarineBarkRules"/>).</summary>
public sealed class MusouBarkClock(int seed = 4108)
{
    readonly Random _roll = new(seed);
    readonly Dictionary<int, double> _next = new();
    double _lastAny = double.NegativeInfinity;

    public Random Roll => _roll;

    /// <summary>Whether Marine <paramref name="id"/> barks now; when it does (or first comes up), its next time is drawn.</summary>
    public bool Due(int id, double now, bool fighting)
    {
        float min = fighting ? MusouMarineBarkRules.CombatEveryMin : MusouMarineBarkRules.CalmEveryMin, max = fighting ? MusouMarineBarkRules.CombatEveryMax : MusouMarineBarkRules.CalmEveryMax;
        if (!_next.TryGetValue(id, out double next)) { _next[id] = now + min * 0.5 + _roll.NextDouble() * (max - min); return false; }
        if (fighting && next - now > MusouMarineBarkRules.CombatEveryMax) _next[id] = next = now + MusouMarineBarkRules.CombatEveryMin * _roll.NextDouble();   // a calm wait cut short by a fight
        if (now < next || now - _lastAny < MusouMarineBarkRules.Spacing) return false;
        _lastAny = now;
        _next[id] = now + min + _roll.NextDouble() * (max - min);
        return true;
    }

    public void Forget(int id) => _next.Remove(id);
}
