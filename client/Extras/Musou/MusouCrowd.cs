using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;


namespace SmClient;

/// <summary>
/// The Musou crowd as drawn (design section D): each frame the enemies shown (<see cref="MusouClientState"/>) and the bodies of the dead go to <see cref="CrowdView"/>, which draws the nearest
/// <see cref="CrowdView.Visible"/>, the nearest within <see cref="NearMetres"/>, at most <see cref="MaxSkinned"/>, skinned, the rest by each look's vertex-animation tier (at most <see cref="MaxVat"/>
/// per look); skinned only, at most <see cref="FallbackSkinned"/>, without a baked look. A dead enemy plays its death clip where it fell and sinks after <see cref="SinkAfter"/> s (10 s since the corpse floor, 2026-11-09).
/// Every number here is ours (design section E's budgets).
/// </summary>
public partial class MusouCrowd : CrowdView
{
    public const int DefaultVisible = 1800, MaxSkinned = 40, MaxVat = 1000, FallbackSkinned = 80;   // 900 and 500 before the owner's third doubling (2026-11-06)
    public const float NearMetres = 25f, HalfRateMetres = 12f, SinkSeconds = 1.5f, SinkDepth = 1.2f;
    /// <summary>Seconds a body lies whole before it sinks: the corpse floor's 10 s (the owner, 2026-11-09; <see cref="SmNet.Extras.Musou.MusouCorpseRules"/>), 3 s before it (<c>--musou-old-corpses</c>).</summary>
    public static float SinkAfter => SmNet.Extras.Musou.MusouCorpseRules.LieSeconds(OldCorpses);
    /// <summary>Bodies kept at once (ours): past this the oldest past the corpse floor go (before it: the oldest), and with none past it the pool grows up to <see cref="HardBodies"/>.</summary>
    public const int MaxBodies = 100, HardBodies = SmNet.Extras.Musou.MusouCorpseRules.OrkHardBodies;

    static readonly int[] TypeIds = Enum.GetValues<MusouEnemyType>().Select(t => (int)t).ToArray();

    readonly MusouLooks _looks;
    readonly Dictionary<int, float> _clocks = new();   // per id: the looping clip's time (shared by both tiers, so a switch of tier does not jump)
    readonly HashSet<int> _seen = new();
    readonly List<int> _gone = new();

    public MusouCrowd(MusouLooks looks) : base(new CrowdLimits(MaxSkinned, MaxVat, FallbackSkinned, NearMetres, HalfRateMetres, SinkAfter, SinkSeconds, SinkDepth, MaxBodies, HardBodies), DefaultVisible)
    {
        _looks = looks; Name = "MusouCrowd";
    }

    protected override IReadOnlyList<int> Types => TypeIds;
    protected override MusouLooks.Look? LookOf(int type) => _looks.For((MusouEnemyType)type);
    protected override bool Loops(int clip) => MusouLooks.Loops(clip);

    /// <summary>A death to show: the body plays its death clip where the enemy was last seen, <paramref name="age"/> s into it (the time it waited to be handed out).</summary>
    public void AddBody(in MusouDeathShown death, float age = 0f, bool keepSkeleton = false) =>
        AddBody(new Vector3(death.Position.X, death.Position.Y, death.Position.Z), death.Yaw, (int)death.Type, MusouLooks.Death, age, keepSkeleton ? death.Id : 0);

    /// <summary>Deaths the view has not handed out yet (night batch 2026-11-06, <see cref="MusouDeathRules"/>) and the seconds since each arrived: drawn where they fell, playing their death clip.</summary>
    public readonly List<(MusouDeathShown Death, float Age)> Dying = new();

    /// <summary>A blast death with no ragdoll to spare, or far off: the body flies one arc along <paramref name="velocity"/>, tipping over about one axis (<see cref="SmNet.Extras.Musou.MusouFling.Tumble"/>; the owner, 2026-11-08: no corkscrew), and falls where it lands (<see cref="MusouRagdolls"/> throws the near ones).</summary>
    public void AddFlung(in MusouDeathShown d, Vector3 velocity, bool keepSkeleton = false)
    {
        var t = SmNet.Extras.Musou.MusouFling.Tumble(new System.Numerics.Vector3(velocity.X, velocity.Y, velocity.Z));
        AddFlungBody(new Vector3(d.Position.X, d.Position.Y, d.Position.Z), d.Yaw, (int)d.Type, MusouLooks.Death, velocity, new Vector3(t.X, t.Y, t.Z), keepSkeleton ? d.Id : 0);
    }

    /// <summary>Seconds a shot Ork's flinch plays, how soon a new shot restarts it, and how far it rocks him back (m). The owner, 2026-11-02: "add hit reactions to the orks getting shot". Drawn only: the simulation's Ork does not stop. Ours.</summary>
    public const float FlinchSeconds = 0.4f, FlinchAgain = 0.2f, FlinchBack = 0.22f;
    readonly Dictionary<int, float> _flinch = new();   // per id: seconds into its flinch
    readonly List<int> _flinchOver = new();

    /// <summary>Combat feel (2026-11-09): per id, the push back from a blow (seconds into it, the flat direction away from the attacker, metres); drawn only, the clip and facing kept.</summary>
    readonly Dictionary<int, (float Age, Vector3 Away, float Metres)> _recoil = new();
    readonly List<int> _recoilOver = new();

    /// <summary>A blow from the direction opposite <paramref name="away"/> struck this Ork: it is drawn pushed back by up to <paramref name="metres"/> (<see cref="SmNet.Extras.Musou.MusouContactRules.RecoilAt"/>).</summary>
    public void Recoil(int id, Vector3 away, float metres) => _recoil[id] = (0f, away, metres);

    /// <summary>
    /// Ork hit reactions (owner 2026-11-13, reference v3; <see cref="MusouOrkFlinchRules"/>): per id, the flinch's track. A skeletal Ork poses it (torso, pelvis, knees, feet planted), a baked one leans and jolts,
    /// a very far one shows none. Drawn only; the clip, facing and path are the Ork's own. <c>--no-musou-ork-flinch</c> restores the no-reaction crowd of 2026-11-06.
    /// </summary>
    public static bool NoOrkFlinch { get; set; } = Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-musou-ork-flinch") >= 0;
    readonly Dictionary<int, MusouOrkFlinchTrack> _kick = new();
    readonly List<int> _kickOver = new();
    /// <summary>Flinches started and Orks drawn mid-flinch (the profile's and the tests').</summary>
    public int Kicks { get; private set; }
    public int Flinching => _kick.Count;
    /// <summary>The ids that took a blast's kick and when (render seconds): the server's hit reports of the same blast follow about 0.2 s later and add no second kick.</summary>
    readonly Dictionary<int, float> _blastKicked = new();
    float _clock;

    /// <summary>A hit: the Ork <paramref name="id"/> flinches away from <paramref name="away"/> (a unit direction in game space), <paramref name="strength"/> by the weapon (<see cref="MusouOrkFlinchRules.ShotStrength"/> ...); <paramref name="type"/> sizes it.</summary>
    public void Hit(int id, System.Numerics.Vector2 away, float strength, MusouEnemyType type)
    {
        if (NoOrkFlinch || !MusouOrkFlinchRules.Reacts(type)) return;
        float size = MusouOrkFlinchRules.Amplitude(strength, type);
        if (!(size > 0f)) return;
        if (_blastKicked.TryGetValue(id, out float at) && _clock - at < 0.6f) return;   // the blast's own kick covers the server's reports of it
        _kick.TryGetValue(id, out var track);
        track.Kick(away, size);
        _kick[id] = track;
        Kicks++;
    }

    /// <summary>A blast: the Ork <paramref name="id"/> takes the explosive's kick (<see cref="MusouOrkFlinchRules.BlastStrength"/> by its distance from the centre), away from it.</summary>
    public void Blast(int id, System.Numerics.Vector2 away, float distance, float radius, MusouEnemyType type)
    {
        if (NoOrkFlinch || !MusouOrkFlinchRules.Reacts(type)) return;
        float size = MusouOrkFlinchRules.Amplitude(MusouOrkFlinchRules.BlastStrength(distance, radius), type);
        if (!(size > 0f)) return;
        _kick.TryGetValue(id, out var track);
        track.Kick(away, size);
        _kick[id] = track;
        _blastKicked[id] = _clock;
        Kicks++;
    }

    /// <summary>Test aid (the old <c>--musou-ork-flinch</c>): a shot struck this Ork.</summary>
    public void Flinch(int id)
    {
        if (!_flinch.TryGetValue(id, out float age) || age >= FlinchAgain) _flinch[id] = 0f;
    }

    /// <summary>The frame: <paramref name="shown"/> at the render tick, the camera in game space.</summary>
    public void Frame(IReadOnlyList<MusouShown> shown, Vector3 camera, float delta)
    {
        UpkeepLooks("musou");
        Items.Clear();
        _seen.Clear();
        foreach (var e in shown)
        {
            var p = new Vector3(e.Position.X, e.Position.Y, e.Position.Z);
            int clip = GunClip(e, ClipOf(e.State));   // MusouCrowd.Guns.cs: a firing Shoota Boy aims
            _seen.Add(e.Id);
            float time, rate = 1f;
            if (clip == MusouLooks.Run && !OldGait) (clip, rate) = Gait(e, p, delta);   // 2026-11-09: the run follows his drawn speed, the idle when he stands (MusouGaitRules)
            if (MusouLooks.Loops(clip)) { _clocks.TryGetValue(e.Id, out time); time += delta * rate; _clocks[e.Id] = time; }
            else time = e.State == MusouEnemyState.Knocked ? KnockedTime(e) : e.StateSeconds;
            if (_flinch.TryGetValue(e.Id, out float flinch) && e.Type != MusouEnemyType.Warboss && !MusouBossOrks.Is(e.Type) && e.State is MusouEnemyState.Seek or MusouEnemyState.WindUp or MusouEnemyState.Recover)
            {
                clip = MusouLooks.Stagger; time = flinch;
                p -= new Vector3(-MathF.Sin(e.Yaw), MathF.Cos(e.Yaw), 0f) * (FlinchBack * MathF.Sin(MathF.PI * MathF.Min(1f, flinch / FlinchSeconds)));
            }
            if (_recoil.TryGetValue(e.Id, out var push)) p += push.Away * SmNet.Extras.Musou.MusouContactRules.RecoilAt(push.Age, push.Metres);
            float distance = p.DistanceTo(camera);
            var kick = Vector3.Zero;
            if (_kick.TryGetValue(e.Id, out var track) && MusouOrkFlinchRules.DistanceScale(distance) > 0f && e.State is not (MusouEnemyState.Dying or MusouEnemyState.Knocked))
            {
                var c = track.Current();
                kick = new Vector3(c.X, c.Y, 0f);
            }
            Items.Add(new Item { Key = e.Id, Type = (int)e.Type, Position = p, Yaw = e.Yaw, Time = time, Clip = clip, Distance = distance, Kick = kick });
        }
        _clock += delta;
        if (_kick.Count > 0)
        {
            _kickOver.Clear();
            foreach (var id in new List<int>(_kick.Keys))
            {
                var t = _kick[id];
                if (!t.Step(delta) || !_seen.Contains(id)) _kickOver.Add(id); else _kick[id] = t;
            }
            foreach (var id in _kickOver) _kick.Remove(id);
        }
        if (_blastKicked.Count > 64) { _kickOver.Clear(); foreach (var (id, at) in _blastKicked) if (_clock - at > 1f) _kickOver.Add(id); foreach (var id in _kickOver) _blastKicked.Remove(id); }
        _recoilOver.Clear();
        foreach (var (id, push) in _recoil) if (push.Age + delta >= SmNet.Extras.Musou.MusouContactRules.RecoilSeconds || !_seen.Contains(id)) _recoilOver.Add(id);
        foreach (var id in _recoilOver) _recoil.Remove(id);
        foreach (var id in new List<int>(_recoil.Keys)) { var r = _recoil[id]; _recoil[id] = (r.Age + delta, r.Away, r.Metres); }
        _flinchOver.Clear();
        foreach (var (id, age) in _flinch) if (age + delta >= FlinchSeconds || !_seen.Contains(id)) _flinchOver.Add(id);
        foreach (var id in _flinchOver) _flinch.Remove(id);
        foreach (var id in new List<int>(_flinch.Keys)) _flinch[id] += delta;
        foreach (var (d, age) in Dying)
        {
            if (_seen.Contains(d.Id)) continue;   // the id is in play again
            var p = new Vector3(d.Position.X, d.Position.Y, d.Position.Z);
            Items.Add(new Item { Key = d.Id, Type = (int)d.Type, Position = p, Yaw = d.Yaw, Time = age, Clip = MusouLooks.Death, Distance = p.DistanceTo(camera) });
        }
        _gone.Clear();
        foreach (var id in _clocks.Keys) if (!_seen.Contains(id)) _gone.Add(id);
        foreach (var id in _gone) _clocks.Remove(id);
        _gone.Clear();
        foreach (var id in _gait.Keys) if (!_seen.Contains(id)) _gone.Add(id);
        foreach (var id in _gone) _gait.Remove(id);
        AddBodyItems(camera, delta);
        Draw(delta);
    }

    /// <summary>A knocked Ork plays his death clip, lies at its last frame, and plays it backwards to stand (the crowd has no get-up clip baked).</summary>
    float KnockedTime(in MusouShown e) =>
        SmSim.Extras.Musou.Campaign.TfwCombatRules.Default.FallClipTime(e.StateSeconds, _looks.For(e.Type)?.Clips[MusouLooks.Death]?.Animation.Duration ?? 1f);

    /// <summary>Test aid (<c>--musou-old-big-orks</c>): the big Orks as before 2026-11-09's deaths and gait (torn apart by a blast, the Warboss's blast reaction for a death, the full scale's stride; <see cref="SmNet.Extras.Musou.MusouBigOrkRules"/>).</summary>
    public static bool OldBigOrks { get; set; } = Array.IndexOf(OS.GetCmdlineUserArgs(), "--musou-old-big-orks") >= 0;
    /// <summary>The power of a look's scale its run's stride grows by (<see cref="SmNet.Extras.Musou.MusouBigOrkRules.StrideExponent"/>; 1 with <c>--musou-old-big-orks</c>; <c>--musou-stride-exponent f</c> to try another).</summary>
    public static float StrideExponent { get; set; } = OldBigOrks ? 1f : CmdFloat("--musou-stride-exponent") ?? SmNet.Extras.Musou.MusouBigOrkRules.StrideExponent;
    static float? CmdFloat(string flag)
    {
        var a = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(a, flag);
        return i >= 0 && i + 1 < a.Length && float.TryParse(a[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }
    /// <summary>Test aid (<c>--musou-old-gait</c>): every seeking Ork plays the run at its own rate, as before 2026-11-09.</summary>
    public static bool OldGait { get; set; } = Array.IndexOf(OS.GetCmdlineUserArgs(), "--musou-old-gait") >= 0;
    readonly Dictionary<int, MusouGaitTrack> _gait = new();
    readonly Dictionary<(MusouLooks.Look, int), float> _clipSpeed = new();
    /// <summary>Test aid (<c>--musou-gait-log</c>): each look's measured run speed once, printed as MUSOUGAIT lines.</summary>
    static readonly bool GaitLog = Array.IndexOf(OS.GetCmdlineUserArgs(), "--musou-gait-log") >= 0;

    /// <summary>A seeking Ork's clip and run rate from the speed he is drawn at (<see cref="MusouGaitRules"/>): the run at his stride's rate, or the idle when he stands.</summary>
    (int Clip, float Rate) Gait(in MusouShown e, Vector3 drawn, float delta)
    {
        _gait.TryGetValue(e.Id, out var track);
        bool running = track.Step(new System.Numerics.Vector2(drawn.X, drawn.Y), delta);
        _gait[e.Id] = track;
        if (!running) { track.Sprinting = false; _gait[e.Id] = track; return (MusouLooks.Idle, 1f); }
        var look = _looks.For(e.Type);
        // The charge (2026-11-09): a melee Ork drawn near his sprint speed plays his own sprint clip at its stride's rate (no byte on the wire: the speed tells)
        if (look is not null && SmSim.Extras.Musou.MusouChargeRules.Charges(e.Type) && !ReferenceEquals(look.Clips[MusouLooks.Sprint], look.Clips[MusouLooks.Run]))
        {
            float speed = (int)e.Type < Rules.Types.Count ? Rules.Of(e.Type).Speed : 5f;
            track.Sprinting = MusouGaitRules.Sprinting(track.Speed, speed * SmSim.Extras.Musou.MusouChargeRules.PaceScale, SmSim.Extras.Musou.MusouChargeRules.SprintSpeed(speed), track.Sprinting);
            _gait[e.Id] = track;
            if (track.Sprinting) return (MusouLooks.Sprint, MusouGaitRules.Rate(track.Speed, ClipSpeed(look, MusouLooks.Sprint)));
        }
        return (MusouLooks.Run, MusouGaitRules.Rate(track.Speed, ClipSpeed(look)));
    }
    static readonly SmSim.Extras.Musou.MusouRules Rules = new();

    /// <summary>A look's run clip's planted-foot speed at its scale (m/s; measured once, <c>ClipGait</c> on the Ork boy's feet, else the Marine's; 0: not measured).</summary>
    float ClipSpeed(MusouLooks.Look? look, int clip = MusouLooks.Run)
    {
        if (look is null) return 0f;
        if (_clipSpeed.TryGetValue((look, clip), out float known)) return known;
        float speed = 0f;
        try
        {
            if (look.Clips[clip] is { } run)
            {
                var skeleton = look.Assets.Model.Rig.Skeleton;
                var gait = SmContent.ClipGait.Analyze(skeleton, run, feet: SmContent.FootBones.OrkBoy) ?? SmContent.ClipGait.Analyze(skeleton, run, feet: SmContent.FootBones.Warboss) ?? SmContent.ClipGait.Analyze(skeleton, run, feet: SmContent.FootBones.Marine);
                // musou-big-ork-deaths (2026-11-09): the stride grows with the scale to StrideExponent's power (the square root), not the whole scale, so a big body does not step in slow motion
                float stride = SmNet.Extras.Musou.MusouBigOrkRules.StrideScale(look.Scale, StrideExponent);
                if (gait is { } g && g.Speed > 0.5f && g.Speed < 20f) speed = g.Speed * stride;
                if (GaitLog) GD.Print($"MUSOUGAIT {look.Name}: {(clip == MusouLooks.Sprint ? "sprint" : "run")} {run.Name} {run.Animation.Duration:F3} s, stride {(gait is { } h ? h.Stride.Length() : 0f):F2} m, speed {(gait?.Speed ?? 0f):F2} m/s at scale 1, {speed:F2} m/s matched at {look.Scale:F2} (stride x{stride:F2})");
            }
        }
        catch (Exception ex) { GD.PushWarning($"MUSOUGAIT {look.Name}: no stride ({ex.Message})"); }
        _clipSpeed[(look, clip)] = speed;
        return speed;
    }

    static int ClipOf(MusouEnemyState state) => state switch
    {
        MusouEnemyState.Seek => MusouLooks.Run,
        MusouEnemyState.WindUp or MusouEnemyState.Strike or MusouEnemyState.Recover or MusouEnemyState.Casting => MusouLooks.Attack,   // Casting: the Weirdboy's summons
        MusouEnemyState.Stagger or MusouEnemyState.Stunned => MusouLooks.Stagger,   // Stunned: the Weirdboy's broken shield
        MusouEnemyState.Dying or MusouEnemyState.Knocked => MusouLooks.Death,
        MusouEnemyState.Airborne => MusouLooks.Idle,   // expansion S3: a Stormboy in his hop (the crowd has no flight clip)
        _ => MusouLooks.Idle,
    };
}
