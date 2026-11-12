namespace SmContent;

/// <summary>What a post of another player's sound is, for the Classic soundscape (<see cref="ClassicSoundscape"/>); carried on <see cref="VoiceOffset.Scape"/>. None: as before.</summary>
public enum ScapeClass : byte
{
    None,
    /// <summary>Footsteps, rolls, charges, landings, hops' apex and dive, grunts and barks: heard to <see cref="ClassicSoundscape.MovementReachM"/>, never cut off.</summary>
    Movement,
    /// <summary>Jump-pack take-offs, dashes and ground pounds: movement actions (never cut off) that also carry the whole map faintly.</summary>
    MovementFar,
    /// <summary>Weapons fire of all kinds and explosions: the whole map faintly (not protected).</summary>
    Gunfire,
    /// <summary>Another player's reload: heard to <see cref="ClassicSoundscape.ReloadReachM"/>, quieter than steps.</summary>
    Reload,
}

/// <summary>How the Classic soundscape is set (client <c>--old-classic-mix</c> = Old, <c>--sound-natural-position</c> = Natural, default Clear).</summary>
public enum ScapeMode
{
    /// <summary>The default: the ranges, levels and protection below, and clearer left/right/behind cues.</summary>
    Clear,
    /// <summary>The ranges, levels and protection below with Godot's own softer positional panning (no stronger pan, no behind cue).</summary>
    Natural,
    /// <summary>The whole old mix: nothing of this class applies.</summary>
    Old,
}

/// <summary>
/// The Classic PvP soundscape (owner's interview, 2026-11-13: "its difficult to hear other player's footsteps and actions (charges, rolls, assault marine/raptor jumps and hops, etc.) ... these sounds
/// are important to keep track of players around the map"; recorded as a **deliberate change from the original**, docs/changes-from-original.md). Client presentation only, Annihilation and Seize
/// Ground (not Musou Mode). Engine-free so it is tested without Godot: <see cref="Apply"/> turns a voice's data mix (the loudness rule's, at the listener's distance) and its mix at 0 m into the
/// mix heard. All numbers are **ours** (estimates, refine in a playtest): footsteps and movement of others are lifted so they read at 50 m with a steady fall-off, then fade out by about 70 m;
/// reloads to 20 m; weapons fire, explosions, take-offs, dashes and pounds keep a faint floor over the whole map; movement is protected from stealing, cutting and culling by other sounds up to a bounded number of voices.
/// </summary>
public static class ClassicSoundscape
{
    /// <summary>Movement sounds of others are heard to here (m; the owner: 50 m).</summary>
    public const float MovementReachM = 50f;
    /// <summary>Others' reloads are heard to here (m; the owner: about 20 m).</summary>
    public const float ReloadReachM = 20f;

    /// <summary>Movement: the most a quiet voice is lifted over its own level at 0 m (dB; ours).</summary>
    public const float MovementLiftDb = 8f;
    /// <summary>Movement: the level at 0 m (dB at the voice, master bus counted) a lifted voice is brought up to; a voice already at or above it is not lifted, so a loud ground pound stays as it is (ours).</summary>
    public const float MovementReadableDb = -19f;
    /// <summary>Movement: steady fall-off (dB per m) from 2 m out to <see cref="MovementReachM"/> (ours): 14 dB down at 50 m.</summary>
    public const float MovementSlopeDbPerM = 0.3f;
    /// <summary>Reload: level relative to the voice's 0 m level (dB; ours: quieter than steps).</summary>
    public const float ReloadLiftDb = -4f;
    /// <summary>Reload: fall-off (dB per m) from 2 m out to <see cref="ReloadReachM"/> (ours).</summary>
    public const float ReloadSlopeDbPerM = 0.5f;
    /// <summary>Past the reach the level falls this fast (dB per m), so the sound is gone (under the -60 dB cut) a little further out (ours).</summary>
    public const float PastReachDbPerM = 1.5f;
    /// <summary>Whole-map floor: the level falls this many dB per m from 0 m, to at most <see cref="FarMaxDropDb"/> under the voice's 0 m level (ours; the loudness rule's own floor is 14 dB, so this lifts the far floor 2 dB).</summary>
    public const float FarSlopeDbPerM = 0.24f;
    public const float FarMaxDropDb = 12f;

    /// <summary>Low-pass cap per class (Wwise 0..100 units, higher = duller): the data dulls a footstep to 1.4 kHz at 50 m; clear movement keeps it near 3.6 kHz (ours).</summary>
    public static float MaxLpf(ScapeClass c) => c switch { ScapeClass.Movement => 30f, ScapeClass.MovementFar => 30f, ScapeClass.Reload => 40f, ScapeClass.Gunfire => 45f, _ => 100f };

    /// <summary>The most voices held by the protection at once (of the 48 positioned): the footsteps and actions of about eight others at a time; past it they are ordinary voices (ours).</summary>
    public const int ProtectedVoiceCap = 20;

    /// <summary>Movement sounds are never stolen, cut by another source's instance limit or culled by the pool (up to <see cref="ProtectedVoiceCap"/>).</summary>
    public static bool IsProtected(ScapeClass c) => c is ScapeClass.Movement or ScapeClass.MovementFar;

    /// <summary>Godot's <c>AudioStreamPlayer3D.PanningStrength</c> for another player's voice: 1 is Godot's own; clear direction spreads the stereo image wider (ours).</summary>
    public const float ClearPanningStrength = 1.8f;
    public static float PanningStrength(ScapeMode mode, bool own) => mode == ScapeMode.Clear && !own ? ClearPanningStrength : 1f;

    /// <summary>The behind cue (clear direction): a sound straight behind the listener gets this much more low-pass (units; the front none), a cosine blend (ours).</summary>
    public const float RearLpf = 12f;

    /// <summary>The extra low-pass for a source at <paramref name="frontDot"/> (the dot of the listener's facing and the direction to the source: 1 ahead, -1 behind).</summary>
    public static float RearLpfFor(ScapeMode mode, bool own, float frontDot) => mode == ScapeMode.Clear && !own ? RearLpf * Math.Clamp((1f - frontDot) / 2f, 0f, 1f) : 0f;

    /// <summary>The class of a binding's post by the client's kind label (what <c>PostBinding</c> was told it is), for another player; None when this change does not touch it.</summary>
    public static ScapeClass ClassOfKind(string kind) => kind switch
    {
        "footstep" or "tag" or "charge" or "charge end" or "jump apex" or "jump thrust cut" or "jump dive" or "jump land" or "land" or "slash land" or "roll" or "bark" => ScapeClass.Movement,
        "jump launch" or "dash" or "ground pound" => ScapeClass.MovementFar,
        "fire other" or "detonation" or "throw" => ScapeClass.Gunfire,
        "reload other" => ScapeClass.Reload,
        _ => ScapeClass.None,
    };

    /// <summary>The lift a movement voice gets over its 0 m level <paramref name="at0Db"/>: up to <see cref="MovementReadableDb"/>, at most <see cref="MovementLiftDb"/>, never negative.</summary>
    public static float MovementLift(float at0Db) => Math.Clamp(MovementReadableDb - at0Db, 0f, MovementLiftDb);

    static float MovementTarget(float d, float lift) => d <= MovementReachM
        ? lift - MovementSlopeDbPerM * Math.Max(0f, d - 2f)
        : lift - MovementSlopeDbPerM * (MovementReachM - 2f) - PastReachDbPerM * (d - MovementReachM);

    static float ReloadTarget(float d) => d <= ReloadReachM
        ? ReloadLiftDb - ReloadSlopeDbPerM * Math.Max(0f, d - 2f)
        : ReloadLiftDb - ReloadSlopeDbPerM * (ReloadReachM - 2f) - PastReachDbPerM * (d - ReloadReachM);

    static float FarTarget(float d) => -Math.Min(FarMaxDropDb, FarSlopeDbPerM * Math.Max(0f, d));

    /// <summary>
    /// The mix heard: <paramref name="atD"/> (the loudness rule's mix at the listener's distance <paramref name="d"/>, m) raised to the class's target over <paramref name="at0"/> (the same voice's mix at 0 m),
    /// never lowered inside the reach; past a movement or reload reach the target itself (it falls fast). The low-pass is capped (<see cref="MaxLpf"/>).
    /// </summary>
    public static VoiceMix Apply(ScapeClass c, VoiceMix atD, VoiceMix at0, float d)
    {
        if (c == ScapeClass.None) return atD;
        float db = atD.VolumeDb;
        switch (c)
        {
            case ScapeClass.Movement:
                db = d <= MovementReachM ? Math.Max(db, at0.VolumeDb + MovementTarget(d, MovementLift(at0.VolumeDb))) : at0.VolumeDb + MovementTarget(d, MovementLift(at0.VolumeDb));
                break;
            case ScapeClass.MovementFar:
                db = Math.Max(Math.Max(db, at0.VolumeDb + MovementTarget(d, MovementLift(at0.VolumeDb))), at0.VolumeDb + FarTarget(d));
                break;
            case ScapeClass.Gunfire:
                db = Math.Max(db, at0.VolumeDb + FarTarget(d));
                break;
            case ScapeClass.Reload:
                db = d <= ReloadReachM ? Math.Max(db, at0.VolumeDb + ReloadTarget(d)) : at0.VolumeDb + ReloadTarget(d);
                break;
        }
        return atD with { VolumeDb = Math.Max(db, -96f), Lpf = Math.Min(atD.Lpf, MaxLpf(c)) };
    }
}
