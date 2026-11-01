using SmSim.Extras.Musou;

namespace SmContent;

/// <summary>The kinds of sound Musou's enemies make; each has a budget of its own (<see cref="MusouSoundRules"/>).</summary>
public enum MusouSoundGroup : byte { Footstep, Grunt, Bark, Effort, Swing, MeleeHit, Gun, GunImpact, Hurt, Stun, Death, Bodyfall, Boss, Celebrate }

/// <summary>Why a sound was or was not posted.</summary>
public enum MusouSoundVerdict : byte { Post, TooFar, OverRate, VoiceCap, SpeechCap }

/// <summary>A group's limits: farthest from the listener (m), posts a second and burst of the token bucket, whether it is a character's voice line (<see cref="MusouSoundRules.MaxSpeech"/>).</summary>
public readonly record struct MusouSoundLimit(float Range, float PerSecond, float Burst, bool Speech = false);

/// <summary>
/// How much of a crowd is heard: Musou Mode has hundreds of enemies and one player's 48 positioned voices, so only the nearest enemies speak and step, each kind of sound has a range and a rate, and
/// the whole crowd holds at most <see cref="MaxVoices"/> voices at once. Every value here is **ours** (spec "Musou enemy sounds", refine register); the ranges follow the data's attenuation: an Ork
/// line is 26 dB down at 12 m, a shoota 13 dB down at 20 m. The data's own instance limits apply on top (<c>limited</c> posts).
/// </summary>
public static class MusouSoundRules
{
    /// <summary>Emitters bound to the nearest enemies (the Warboss always has one), and how many of the nearest step aloud (Nobs and the Warboss always do).</summary>
    public const int Slots = 16, Stepping = 6;
    /// <summary>The farthest enemy that gets an emitter (m).</summary>
    public const float SlotRange = 30f;
    /// <summary>Most voices of the crowd at once (of <c>SoundSystem.Max3D</c>, 48: more than half stays the players'), and most character lines among them (design section D's 6).</summary>
    public const int MaxVoices = 20, MaxSpeech = 6;
    /// <summary>Seconds between one enemy's running grunts (the data's clips carry one about every 1.2 s; thinned for a crowd) and between its barks (no such tag in the data's timing: a clip's).</summary>
    public const float GruntEveryMin = 2.5f, GruntEveryMax = 5f, BarkEveryMin = 6f, BarkEveryMax = 14f;
    /// <summary>Seconds between one enemy's grunts for being hit.</summary>
    public const double HurtSpacing = 1.0;
    /// <summary>Seconds an emitter stays with an enemy that left the nearest ones, so a line is not cut.</summary>
    public const double SlotHold = 1.5;
    /// <summary>Seconds between the nearest choices of emitters.</summary>
    public const double ChooseEvery = 0.1;

    static readonly MusouSoundLimit[] Limits =
    [
        new(14f, 14f, 6f),                // Footstep
        new(12f, 2.5f, 3f, Speech: true), // Grunt
        new(14f, 0.8f, 2f, Speech: true), // Bark
        new(12f, 3f, 3f, Speech: true),   // Effort
        new(18f, 5f, 3f),                 // Swing
        new(20f, 6f, 4f),                 // MeleeHit
        new(50f, 6f, 4f),                 // Gun
        new(15f, 6f, 3f),                 // GunImpact
        new(12f, 4f, 3f, Speech: true),   // Hurt
        new(12f, 1f, 1f, Speech: true),   // Stun
        new(20f, 4f, 3f, Speech: true),   // Death
        new(20f, 5f, 3f),                 // Bodyfall
        new(80f, 20f, 8f),                // Boss: never held by the caps
        new(25f, 0.5f, 1f, Speech: true), // Celebrate
    ];

    public static MusouSoundLimit LimitOf(MusouSoundGroup g) => Limits[(int)g];

    /// <summary>Metres between two footfalls (ours: a run of about 2.5 steps a second at the type's speed; the crowd's clips are not read for this).</summary>
    public static float StepMetres(MusouEnemyType t) => t switch
    {
        MusouEnemyType.Gretchin => 1.4f, MusouEnemyType.Nob => 2.4f, MusouEnemyType.ChaosMarine => 2.2f, MusouEnemyType.Warboss => 3f, _ => 2f,
    };

    /// <summary>Whether a type always steps aloud with an emitter, however many are nearer.</summary>
    public static bool HeavyStep(MusouEnemyType t) => t is MusouEnemyType.Nob or MusouEnemyType.Warboss;
}

/// <summary>The crowd's budget: <see cref="Refill"/> each frame, <see cref="Take"/> for each sound. Counts what it passed and held, by group.</summary>
public sealed class MusouSoundBudget
{
    const int Groups = (int)MusouSoundGroup.Celebrate + 1, Verdicts = (int)MusouSoundVerdict.SpeechCap + 1;
    readonly float[] _tokens = new float[Groups];
    readonly long[,] _count = new long[Groups, Verdicts];

    public MusouSoundBudget() { for (int g = 0; g < Groups; g++) _tokens[g] = MusouSoundRules.LimitOf((MusouSoundGroup)g).Burst; }

    public void Refill(float seconds)
    {
        for (int g = 0; g < Groups; g++)
        {
            var l = MusouSoundRules.LimitOf((MusouSoundGroup)g);
            _tokens[g] = MathF.Min(l.Burst, _tokens[g] + l.PerSecond * seconds);
        }
    }

    /// <summary>Whether a sound of <paramref name="group"/> at <paramref name="distance"/> m may post with this many of the crowd's voices (and lines) live.</summary>
    public MusouSoundVerdict Take(MusouSoundGroup group, float distance, int voices, int speech)
    {
        var l = MusouSoundRules.LimitOf(group);
        var verdict = distance > l.Range ? MusouSoundVerdict.TooFar
            : group != MusouSoundGroup.Boss && voices >= MusouSoundRules.MaxVoices ? MusouSoundVerdict.VoiceCap
            : group != MusouSoundGroup.Boss && l.Speech && speech >= MusouSoundRules.MaxSpeech ? MusouSoundVerdict.SpeechCap
            : _tokens[(int)group] < 1f ? MusouSoundVerdict.OverRate : MusouSoundVerdict.Post;
        if (verdict == MusouSoundVerdict.Post) _tokens[(int)group] -= 1f;
        _count[(int)group, (int)verdict]++;
        return verdict;
    }

    public long Count(MusouSoundGroup group, MusouSoundVerdict verdict) => _count[(int)group, (int)verdict];
}

/// <summary>One thing an enemy can sound: sound events and voice lines played together.</summary>
public sealed record MusouSoundOption(IReadOnlyList<SoundBinding> Events, IReadOnlyList<SpeechBinding> Lines);

/// <summary>A cue: its options, one picked at random for each post (none: the data has nothing, or nothing in the banks).</summary>
public sealed class MusouSoundCue
{
    public List<MusouSoundOption> Options { get; } = new();
    public bool Empty => Options.Count == 0;
    public MusouSoundOption Pick(Random roll) => Options[roll.Next(Options.Count)];
}

/// <summary>
/// The files a Musou enemy type sounds from. <see cref="VoiceBlueprint"/>: the blueprint its lines come from when that is not <see cref="Blueprint"/>. <see cref="VoiceClass"/>: for a blueprint that names no
/// character and binds no lines (the Chaos player's), the class whose character says the plain exertions.
/// </summary>
public sealed record MusouSoundFiles(MusouEnemyType Type, string Blueprint, string Melee, string? Gun = null, string? Charge = null, string? VoiceBlueprint = null, string? VoiceClass = null);

/// <summary>
/// What a Musou enemy type can sound, read from the data (the same tags and tables the Ork class's sounds use, <see cref="OrkSounds"/>): which trigger stands for which moment is ours, the commands are
/// the data's. <see cref="Build"/> keeps only commands the banks can play; the rest go to <see cref="Missing"/>, reported and never replaced.
/// </summary>
public sealed class MusouSoundSet
{
    /// <summary>
    /// The types' files. The Warboss has the boss's own steps, klaw and body, and a Nob's voice: his own character <c>warboss</c> has no exertion lines in the speech bank (Musou's Warboss is "a scaled
    /// Nob"). The Gretchin has the grot's own; the Chaos Marine the Exterminatus bolter and chain axe and the player blueprint.
    /// </summary>
    public static readonly IReadOnlyList<MusouSoundFiles> Files =
    [
        new(MusouEnemyType.Boy, OrkSounds.Blueprint, "ork_coop_boy_choppa"),
        new(MusouEnemyType.ShootaBoy, "ork_coop_boy_2hand_shoota", "ork_coop_boy_choppa", Gun: "ork_coop_boy_2hand_shoota_rapid"),
        new(MusouEnemyType.ArdBoy, "ork_coop_boy_pro", "ork_coop_boy_choppa_pro"),
        new(MusouEnemyType.Gretchin, "ork_coop_gretchin", "ork_coop_grot_choppa"),
        new(MusouEnemyType.Nob, "ork_coop_nob", "ork_coop_nob_uge_axe", Charge: "ork_coop_nob_bullrush"),
        new(MusouEnemyType.ChaosMarine, GameplayTuning.ChaosBlueprint, "chaos_coop_chainaxe", Gun: "chaos_coop_bolter", VoiceClass: "csm - tactical"),
        new(MusouEnemyType.Warboss, "ork_coop_warboss", "ork_coop_warboss_power_klaw", Charge: "ork_coop_warboss_bullrush", VoiceBlueprint: "ork_coop_nob_pro"),
    ];

    /// <summary>Every file <see cref="Files"/> names, for <see cref="SoundTriggers.Load(string, IEnumerable{string}, IEnumerable{string}, IReadOnlyList{SmFormats.Havok.BehaviorGraph}?, IReadOnlyList{SmFormats.Havok.BehaviorGraph}?)"/>.</summary>
    public static IReadOnlyList<string> Wargear => Files.SelectMany(f => new[] { f.Melee, f.Gun, f.Charge }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public static IReadOnlyList<string> Blueprints => Files.SelectMany(f => new[] { f.Blueprint, f.VoiceBlueprint }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public required MusouEnemyType Type { get; init; }
    /// <summary>The speech character of the lines (null: none, they are not played).</summary>
    public string? Character { get; init; }
    public MusouSoundCue Grunt { get; } = new();
    public MusouSoundCue Bark { get; } = new();
    public MusouSoundCue Roar { get; } = new();
    public MusouSoundCue StepLeft { get; } = new();
    public MusouSoundCue StepRight { get; } = new();
    public MusouSoundCue WindUp { get; } = new();
    public MusouSoundCue Effort { get; } = new();
    public MusouSoundCue Swing { get; } = new();
    public MusouSoundCue MeleeHit { get; } = new();
    public MusouSoundCue Fire { get; } = new();
    public MusouSoundCue GunImpact { get; } = new();
    public MusouSoundCue HurtSmall { get; } = new();
    public MusouSoundCue HurtBig { get; } = new();
    public MusouSoundCue Stun { get; } = new();
    public MusouSoundCue DeathYell { get; } = new();
    public MusouSoundCue Fall { get; } = new();
    public MusouSoundCue Knocked { get; } = new();
    public MusouSoundCue Land { get; } = new();
    public MusouSoundCue Celebrate { get; } = new();
    public MusouSoundCue SlamWindUp { get; } = new();
    public MusouSoundCue SlamHit { get; } = new();
    public MusouSoundCue ChargeStart { get; } = new();
    public MusouSoundCue ChargeHit { get; } = new();
    /// <summary>Commands of the data this type would play that no loaded bank holds (reported at start-up and by <c>smtool musou-sounds</c>).</summary>
    public SortedSet<string> Missing { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every cue with its name, for the report and the prefetch.</summary>
    public IEnumerable<(string Name, MusouSoundCue Cue)> Cues => new (string, MusouSoundCue)[]
    {
        ("grunt", Grunt), ("bark", Bark), ("roar", Roar), ("step left", StepLeft), ("step right", StepRight), ("wind-up", WindUp), ("effort", Effort), ("swing", Swing), ("melee hit", MeleeHit), ("fire", Fire),
        ("gun impact", GunImpact), ("hurt small", HurtSmall), ("hurt big", HurtBig), ("stun", Stun), ("death yell", DeathYell), ("fall", Fall), ("knocked", Knocked), ("land", Land), ("celebrate", Celebrate),
        ("slam wind-up", SlamWindUp), ("slam hit", SlamHit), ("charge start", ChargeStart), ("charge hit", ChargeHit),
    };

    /// <summary>The set of <paramref name="files"/>; <paramref name="inBanks"/> says whether an event can be posted.</summary>
    public static MusouSoundSet Build(MusouSoundFiles files, SoundTriggers triggers, Func<string, bool> inBanks)
    {
        var body = triggers.File(files.Blueprint);
        var voice = files.VoiceBlueprint is { } v ? triggers.File(v) : body;
        var melee = triggers.File(files.Melee);
        var gun = files.Gun is { } g ? triggers.File(g) : null;
        var charge = files.Charge is { } c ? triggers.File(c) : null;
        string? character = files.VoiceClass is { } voiceClass ? triggers.SpeechCharacter(voiceClass)
            : (files.VoiceBlueprint ?? files.Blueprint) is var key && triggers.BlueprintCharacters.TryGetValue(key, out var named) ? named : null;
        var set = new MusouSoundSet { Type = files.Type, Character = character };

        void AddOption(MusouSoundCue cue, IEnumerable<SoundBinding> events, IEnumerable<SpeechBinding> lines)
        {
            var kept = new List<SoundBinding>();
            foreach (var b in events)
            {
                if (b.StartEvent.Length == 0 || b.OnlyIfOnPlayer) continue;
                if (inBanks(b.StartEvent)) kept.Add(b); else set.Missing.Add(b.StartEvent);
            }
            var said = character is null ? [] : lines.ToList();
            if (kept.Count + said.Count > 0) cue.Options.Add(new MusouSoundOption(kept, said));
        }
        // Each trigger one option (a body's command with the line the same trigger binds).
        void Each(MusouSoundCue cue, SoundFile? file, params string[] names)
        {
            if (file is null) return;
            foreach (var n in names) AddOption(cue, file.Of(n), file.SpeechOf(n));
        }
        // The first trigger with something to play.
        void First(MusouSoundCue cue, SoundFile? file, params string[] names)
        {
            if (file is null) return;
            foreach (var n in names)
            {
                Each(cue, file, n);
                if (!cue.Empty) return;
            }
        }
        void Line(MusouSoundCue cue, SoundFile? file, params string[] names)
        {
            if (file is null) return;
            foreach (var n in names) foreach (var line in file.SpeechOf(n)) AddOption(cue, [], [line]);
        }
        void Exertion(MusouSoundCue cue, string type) => AddOption(cue, [], [new SpeechBinding("", type, "", [], SpeechKind.Exertion, "autodetect", type, "", "")]);
        void Sound(MusouSoundCue cue, string? evt) { if (evt is { Length: > 0 }) AddOption(cue, [new SoundBinding("", "hit", "", "", [], evt, "", false, false, false, 0f, "", new Dictionary<string, float>())], []); }

        Each(set.Grunt, body, "actn_snd_vox_nav", "actn_snd_vox_grunt_m_m", "actn_snd_vox_grunt_l_m", "actn_snd_gear_movement");
        Each(set.Grunt, voice != body ? voice : null, "actn_snd_vox_grunt_l_m");
        Each(set.Bark, body, "actn_snd_vox_taunt_m_s", "actn_snd_vox_taunt_h_m", "actn_snd_vox_growl_m_m", "actn_snd_vox_snort_l_s", "actn_snd_vox_laugh_m_l");
        if (files.Type == MusouEnemyType.Gretchin) Each(set.Bark, body, "lifetime_action");   // his ambient chatter
        Each(set.Bark, voice, "actn_snd_vox_taunt_h_l", "actn_snd_vox_taunt_h_m");
        Each(set.Roar, voice, "sfx_or_roar", "sfx_or_roar_long", "actn_snd_vox_roar_h_m", "actn_snd_vox_roar_h_l", "actn_snd_vox_roar_m_l", "actn_snd_vox_roar_l_m");
        foreach (var (cue, foot) in new[] { (set.StepLeft, "evt_left_foot"), (set.StepRight, "evt_right_foot") }) Each(cue, body, foot);
        First(set.WindUp, melee, "weaponflare", "actn_fx_warboss_swing_antic");
        Line(set.Effort, melee, "start_audio");
        First(set.Swing, melee, "actn_fx_swing", "actn_huge_swing", "actn_fx_warboss_swing_span");
        if (set.Swing.Empty && melee is not null) AddOption(set.Swing, melee.Of("start_audio"), []);
        if (melee is not null) Sound(set.MeleeHit, triggers.MeleeHitSound(melee.Name));
        if (melee is not null && set.MeleeHit.Empty)   // the plain attack's, where the heavy one binds its swing too
            AddOption(set.MeleeHit, melee.Of("hit_audio").Where(b => b.Ability.Equals("ability_melee_light", StringComparison.OrdinalIgnoreCase)).Concat(melee.Of("actn_smash")), []);
        if (set.MeleeHit.Empty) Each(set.MeleeHit, body, "actn_snd_choppa_hit_ground");
        if (gun is not null) { AddOption(set.Fire, gun.Fire(null), []); Sound(set.GunImpact, triggers.ShotHitSound(gun.Name)); }
        First(set.HurtSmall, voice, "actn_snd_vox_sync_damage_small", "actn_snd_vox_pain_m_s");
        First(set.HurtBig, voice, "actn_snd_vox_sync_damage_big", "actn_snd_vox_pain_l_l", "actn_snd_vox_pain_m_m");
        First(set.Stun, voice, OrkSounds.StunTrigger, "stunned");
        First(set.DeathYell, voice, OrkSounds.DeathTag, "actn_fx_death_ground_hit", "actn_snd_vox_sync_damage_big", "actn_snd_vox_pain_m_m");
        if (body is not null) AddOption(set.Fall, body.Of("death_action"), []);
        First(set.Knocked, body, "actn_snd_impact_surface", "actn_fx_hit_ground", "actn_fx_knee_ground_hit");
        First(set.Land, body, OrkSounds.LandTrigger);
        First(set.Celebrate, voice, OrkSounds.TauntTag);
        if (files.VoiceClass is not null)
        {
            Exertion(set.HurtSmall, "damage_small"); Exertion(set.HurtBig, "damage_big"); Exertion(set.DeathYell, "death"); Exertion(set.Stun, "stunned"); Exertion(set.Effort, "effort_short_stab");
        }
        // A weapon that whooshes at the flare and at the swing (the Nob's) sounds once, at the swing.
        var swung = set.Swing.Options.SelectMany(o => o.Events).Select(b => b.StartEvent).ToHashSet(StringComparer.OrdinalIgnoreCase);
        set.WindUp.Options.RemoveAll(o => o.Events.All(b => swung.Contains(b.StartEvent)));
        First(set.SlamWindUp, melee, "actn_fx_warboss_swing_antic");
        First(set.SlamHit, body, "actn_fx_warboss_s18_jump_hit_1");
        if (melee is not null) AddOption(set.SlamHit, melee.Of("actn_smash"), []);
        First(set.ChargeStart, charge, "weaponflare");
        First(set.ChargeHit, charge, "actn_bullrush_warboss", "actn_huge_swing");
        return set;
    }
}
