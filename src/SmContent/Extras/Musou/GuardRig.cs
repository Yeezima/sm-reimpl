using System.Numerics;

namespace SmContent.Extras.Musou;

/// <summary>
/// Musou Mode's Guardsman on his own rig (TF7a; docs/discovery.md "Guardsman"): the Exterminatus <c>ig_coop_guardsman</c>, read from the co-op set. The rig, meshes, clips and
/// lasgun are original assets of the player's install. Which clip plays when is **ours** (the graphs of <c>havok_guardsman_a_coop</c> are not read): the names are the clips the discovery
/// lists for each job, as <see cref="RigClipTable.OrkBoy"/> names the boy's.
/// </summary>
public static class GuardRig
{
    public const string Archive = "data/chunks/ebp_sets__mp_coop_set_sm.oppc", Entry = "havok_guardsman_a_coop__hkxx", BodyVisual = "guardsman_a", Head = "guardsman_head_basic_001",
        Wargear = "ig_coop_lasgun";

    /// <summary>The effects of a shot, as the wargear names them (<c>fx_name</c> of <c>hit_fx</c> and <c>fire_fx</c>), and the muzzle's scale there (0.35).</summary>
    public const string TracerEffect = "lasgun_tracer", MuzzleEffect = "lasgun_muzzle";
    public const float MuzzleEffectScale = 0.35f;

    /// <summary>What the crowd's looping and one-shot clips are, by index into <see cref="ClipNames"/>; the vertex-animation tier bakes these.</summary>
    public const int Idle = 0, AimIdle = 1, Run = 2, RunF = 3, RunB = 4, RunL = 5, RunR = 6, Hurt = 7, FirstDeath = 8, Deaths = 5, ClipCount = FirstDeath + Deaths;

    /// <summary>Clip names by index (ours: the relaxed idle and run out of a fight, the aim set in one; the light front reaction; the five deaths under 3 s).</summary>
    public static readonly IReadOnlyList<string> ClipNames =
    [
        "IG_NAV_Idle_01", "IG_NAV_Idle_AimForward", "IG_NAV_Run_F_01",
        "IG_NAV_Run_F_AimForward", "IG_NAV_Run_B_AimForward", "IG_NAV_Run_L_AimForward", "IG_NAV_Run_R_AimForward",
        "IG_REC_Light_Front_Hi_Left",
        "IG_DIE_Basic_01", "IG_DIE_Basic_03", "IG_DIE_FallForward_01", "IG_DIE_Headshot_01", "IG_REC_Death_02",
    ];

    /// <summary>The aim pitch's whole poses, the recoil laid over them and the reload (the skinned tier only; the far tier stays level).</summary>
    public const string AimDown = "IG_NAV_Idle_AimDown", AimUp = "IG_NAV_Idle_AimUp", Recoil = "IG_RNG_Fire_S_01", Reload = "IG_RNG_Reload_01";

    /// <summary>The clips the client picks by name.</summary>
    public static readonly RigClipTable Clips = new()
    {
        IdleCandidates = ["IG_NAV_Idle_01", "IG_NAV_Idle_03", "IG_NAV_Idle_Tense_01"],
        PreviewIdleCandidates = ["IG_NAV_Idle_01"],
        DefaultAnimation = "IG_NAV_Run_F_01",
        TossClip = "IG_RNG_GrenadeToss",
        RecoilClip = Recoil,
        AimStances = new Dictionary<string, AimClipTriple>(StringComparer.Ordinal)
        {
            ["rifle:wos"] = new([AimDown], ["IG_NAV_Idle_AimForward"], [AimUp]),
            ["rifle:walk:F"] = new(["IG_NAV_Walk_F_AimDown"], ["IG_NAV_Walk_F_AimForward"], ["IG_NAV_Walk_F_AimUp"]),
            ["rifle:walk:B"] = new(["IG_NAV_Walk_B_AimDown"], ["IG_NAV_Walk_B_AimForward"], ["IG_NAV_Walk_B_AimUp"]),
            ["rifle:walk:L"] = new(["IG_NAV_Walk_L_AimDown"], ["IG_NAV_Walk_L_AimForward"], ["IG_NAV_Walk_L_AimUp"]),
            ["rifle:walk:R"] = new(["IG_NAV_Walk_R_AimDown"], ["IG_NAV_Walk_R_AimForward"], ["IG_NAV_Walk_R_AimUp"]),
        },
        DeathClips = ["IG_DIE_Basic_01", "IG_DIE_Basic_03", "IG_DIE_FallForward_01", "IG_DIE_Headshot_01", "IG_REC_Death_02", "IG_DIE_Explode_01"],
        Unfilled =
        [
            "pistol:*, heavy:*: the Guardsman carries the lasgun alone",
            "grenade-launcher recoil: a co-op variant's, not read",
            "the six additive flinches: not bound (one track more than bones)",
            "the graphs of havok_guardsman_a_coop: not read; every name here is picked by hand",
        ],
    };

    /// <summary>The rig as a profile: only the data sits here, no marine or Ork graph map fits it, so nothing may read those through this profile.</summary>
    public static readonly RigProfile Profile = new("guardsman", Archive, Entry, BodyVisual)
    {
        Clips = Clips,
        DefaultWargear = [Wargear],
    };

    /// <summary>Metres a second of the run clips (the blueprint's run speed, <c>ig_coop_guardsman</c>: 6.25; **interpretation**: that the clips were authored at it).</summary>
    public const float RunClipSpeed = 6.25f;
    /// <summary>The pitch at which the aim blend is all down or all up (ours: the clips' range is not in the data read).</summary>
    public const float MaxAimPitch = 0.8f;

    /// <summary>The weights of the four run clips for a direction of travel on the man (X his right, Y his front); they sum to 1; all forward for no direction.</summary>
    public static (float F, float B, float L, float R) RunWeights(Vector2 local)
    {
        float x = float.IsFinite(local.X) ? local.X : 0f, y = float.IsFinite(local.Y) ? local.Y : 0f;
        float sum = MathF.Abs(x) + MathF.Abs(y);
        if (sum < 1e-4f) return (1f, 0f, 0f, 0f);
        return (MathF.Max(y, 0f) / sum, MathF.Max(-y, 0f) / sum, MathF.Max(-x, 0f) / sum, MathF.Max(x, 0f) / sum);
    }

    /// <summary>The weights of the aim triple for a pitch (radians, up positive); they sum to 1.</summary>
    public static (float Down, float Level, float Up) AimWeights(float pitch)
    {
        float t = float.IsFinite(pitch) ? Math.Clamp(pitch / MaxAimPitch, -1f, 1f) : 0f;
        return (MathF.Max(-t, 0f), 1f - MathF.Abs(t), MathF.Max(t, 0f));
    }

    /// <summary>The run's playback rate at a speed (clips play at their own rate at <see cref="RunClipSpeed"/>).</summary>
    public static float RunRate(float speed) => float.IsFinite(speed) ? Math.Clamp(speed / RunClipSpeed, 0.3f, 1.5f) : 1f;

    /// <summary>A man's death clip: one of the five by his id and the tick he fell, so every client plays the same.</summary>
    public static int DeathClip(ushort guard, uint tick) => FirstDeath + (int)((guard * 2654435761u + tick * 40503u) >> 16) % Deaths;
}

/// <summary>
/// Which Guardsmen have died, by id and tick, apart from what is drawn of them, so one death gives one body: the Death event and the Dying records (tag 9) can come in either order, either can be lost,
/// and live records can still arrive after the event. A man stays gone for <see cref="HoldTicks"/>, longer than <c>GuardRules.DyingSeconds</c> (60 ticks), after which his slot may hold a new man. Ticks
/// are compared as a signed difference, as an event's tick is usually ahead of the render tick; one far outside the hold (a restarted server) has expired.
/// </summary>
public sealed class GuardDeaths
{
    /// <summary>Ticks a death is remembered (ours: 3 s at 30 Hz).</summary>
    public const int HoldTicks = 90;

    readonly Dictionary<ushort, uint> _died = new();
    readonly List<ushort> _expired = new();

    static bool Within(uint died, uint tick) => Math.Abs((long)(int)(tick - died)) < HoldTicks;

    /// <summary>A report of a death at <paramref name="tick"/>; true for the first of this death: it gets the body.</summary>
    public bool Died(ushort guard, uint tick)
    {
        if (_died.TryGetValue(guard, out uint known) && Within(known, tick)) return false;
        _died[guard] = tick;
        return true;
    }

    /// <summary>Whether a live record of <paramref name="guard"/> at <paramref name="tick"/> is behind his death (he is not drawn).</summary>
    public bool IsGone(ushort guard, uint tick) => _died.TryGetValue(guard, out uint died) && Within(died, tick);

    /// <summary>Drops the deaths whose hold is over at <paramref name="tick"/>.</summary>
    public void Forget(uint tick)
    {
        _expired.Clear();
        foreach (var (guard, died) in _died) if (!Within(died, tick)) _expired.Add(guard);
        foreach (ushort guard in _expired) _died.Remove(guard);
    }
}

/// <summary>
/// The lasgun in the hands, read from the data as the marine's weapons are (<see cref="WeaponModels"/>): <c>ig_coop_lasgun</c>'s wield entry puts the model's <c>rig_prop_m_chest</c> node on the
/// rig bone of that name (**supported by evidence**: the wargear names both nodes, the model and the rig both have them).
/// </summary>
/// <param name="Bone">The bone it follows.</param>
/// <param name="Rigid">Per drawn block, its place on the bone (<see cref="CharacterPiece.Rigid"/>).</param>
/// <param name="Hidden">The blocks not drawn.</param>
/// <param name="MuzzleToBone">The <c>fx_muzzle</c> locator in the bone's space; null when the model has none.</param>
public sealed record GuardLasgun(WeaponModel Model, string Bone, IReadOnlyDictionary<int, RigidBlock> Rigid, IReadOnlySet<int> Hidden, Matrix4x4? MuzzleToBone)
{
    /// <exception cref="InvalidDataException">The wargear has no wield mount on a bone.</exception>
    public static GuardLasgun Load(string gameDir, IReadOnlyList<string> bones, List<string> notes)
    {
        var catalogue = WeaponModels.Load(gameDir, GuardRig.Profile, notes, [GuardRig.Wargear], bones);
        var attachment = catalogue.Held(GuardRig.Wargear) ?? throw new InvalidDataException($"{GuardRig.Wargear}: no wield entry");
        return From(WeaponModel.Load(gameDir, attachment, notes), attachment.Wield!, notes);
    }

    /// <summary>The placement of a model on a mount (testable without an install).</summary>
    /// <exception cref="InvalidDataException">The mount names no bone.</exception>
    public static GuardLasgun From(WeaponModel model, WeaponMount wield, List<string> notes)
    {
        string bone = wield.Bone ?? throw new InvalidDataException($"{model.Package}: wield node '{wield.BodyNode}' is on no bone");
        var toBone = model.RootToBone(wield, notes);
        var rigid = new Dictionary<int, RigidBlock>();
        foreach (var part in model.Parts) if (part.Visible && !part.Skinned) rigid[part.BlockIndex] = new RigidBlock(bone, part.ToRoot * toBone);
        var hidden = Enumerable.Range(0, model.Model.Mesh.Blocks.Count).Where(b => !rigid.ContainsKey(b)).ToHashSet();
        return new GuardLasgun(model, bone, rigid, hidden, model.Muzzle is { } muzzle ? muzzle.ToRoot * toBone : null);
    }
}
