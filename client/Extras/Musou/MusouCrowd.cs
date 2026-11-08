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
/// per look); skinned only, at most <see cref="FallbackSkinned"/>, without a baked look. A dead enemy plays its death clip where it fell and sinks after <see cref="SinkAfter"/> s.
/// Every number here is ours (design section E's budgets).
/// </summary>
public partial class MusouCrowd : CrowdView
{
    public const int DefaultVisible = 1800, MaxSkinned = 40, MaxVat = 1000, FallbackSkinned = 80;   // 900 and 500 before the owner's third doubling (2026-11-06)
    public const float NearMetres = 25f, HalfRateMetres = 12f, SinkAfter = 3f, SinkSeconds = 1.5f, SinkDepth = 1.2f;
    /// <summary>Bodies kept at once (ours); the oldest go first.</summary>
    public const int MaxBodies = 100;

    static readonly int[] TypeIds = Enum.GetValues<MusouEnemyType>().Select(t => (int)t).ToArray();

    readonly MusouLooks _looks;
    readonly Dictionary<int, float> _clocks = new();   // per id: the looping clip's time (shared by both tiers, so a switch of tier does not jump)
    readonly HashSet<int> _seen = new();
    readonly List<int> _gone = new();

    public MusouCrowd(MusouLooks looks) : base(new CrowdLimits(MaxSkinned, MaxVat, FallbackSkinned, NearMetres, HalfRateMetres, SinkAfter, SinkSeconds, SinkDepth, MaxBodies), DefaultVisible)
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

    /// <summary>How fast a thrown body turns end over end at most (rad/s, ours).</summary>
    public const float FlungSpin = 9f;

    /// <summary>A blast death with no ragdoll to spare, or far off: the body tumbles along <paramref name="velocity"/> and falls where it lands (<see cref="MusouRagdolls"/> throws the near ones).</summary>
    public void AddFlung(in MusouDeathShown d, Vector3 velocity, bool keepSkeleton = false)
    {
        var (id, tick) = (d.Id, d.Tick);
        float Spin(uint k) => (SmNet.Extras.Gore.GoreRules.Roll(id, tick + k) * 2f - 1f) * FlungSpin;
        AddFlungBody(new Vector3(d.Position.X, d.Position.Y, d.Position.Z), d.Yaw, (int)d.Type, MusouLooks.Death, velocity, new Vector3(Spin(11u), Spin(12u), Spin(13u)), keepSkeleton ? d.Id : 0);
    }

    /// <summary>Seconds a shot Ork's flinch plays, how soon a new shot restarts it, and how far it rocks him back (m). The owner, 2026-11-02: "add hit reactions to the orks getting shot". Drawn only: the simulation's Ork does not stop. Ours.</summary>
    public const float FlinchSeconds = 0.4f, FlinchAgain = 0.2f, FlinchBack = 0.22f;
    readonly Dictionary<int, float> _flinch = new();   // per id: seconds into its flinch
    readonly List<int> _flinchOver = new();

    /// <summary>A shot struck this Ork.</summary>
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
            float time;
            if (MusouLooks.Loops(clip)) { _clocks.TryGetValue(e.Id, out time); time += delta; _clocks[e.Id] = time; }
            else time = e.State == MusouEnemyState.Knocked ? KnockedTime(e) : e.StateSeconds;
            if (_flinch.TryGetValue(e.Id, out float flinch) && e.Type != MusouEnemyType.Warboss && !MusouBossOrks.Is(e.Type) && e.State is MusouEnemyState.Seek or MusouEnemyState.WindUp or MusouEnemyState.Recover)
            {
                clip = MusouLooks.Stagger; time = flinch;
                p -= new Vector3(-MathF.Sin(e.Yaw), MathF.Cos(e.Yaw), 0f) * (FlinchBack * MathF.Sin(MathF.PI * MathF.Min(1f, flinch / FlinchSeconds)));
            }
            Items.Add(new Item { Key = e.Id, Type = (int)e.Type, Position = p, Yaw = e.Yaw, Time = time, Clip = clip, Distance = p.DistanceTo(camera) });
        }
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
        AddBodyItems(camera, delta);
        Draw(delta);
    }

    /// <summary>A knocked Ork plays his death clip, lies at its last frame, and plays it backwards to stand (the crowd has no get-up clip baked).</summary>
    float KnockedTime(in MusouShown e) =>
        SmSim.Extras.Musou.Campaign.TfwCombatRules.Default.FallClipTime(e.StateSeconds, _looks.For(e.Type)?.Clips[MusouLooks.Death]?.Animation.Duration ?? 1f);

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
