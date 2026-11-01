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
    public const int DefaultVisible = 240, MaxSkinned = 40, MaxVat = 200, FallbackSkinned = 80;
    public const float NearMetres = 25f, HalfRateMetres = 12f, SinkAfter = 3f, SinkSeconds = 1.5f, SinkDepth = 1.2f;
    /// <summary>Bodies kept at once (ours); the oldest go first.</summary>
    public const int MaxBodies = 60;

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

    /// <summary>A death to show: the body plays its death clip where the enemy was last seen.</summary>
    public void AddBody(in MusouDeathShown death) =>
        AddBody(new Vector3(death.Position.X, death.Position.Y, death.Position.Z), death.Yaw, (int)death.Type, MusouLooks.Death);

    /// <summary>The frame: <paramref name="shown"/> at the render tick, the camera in game space.</summary>
    public void Frame(IReadOnlyList<MusouShown> shown, Vector3 camera, float delta)
    {
        UpkeepLooks("musou");
        Items.Clear();
        _seen.Clear();
        foreach (var e in shown)
        {
            var p = new Vector3(e.Position.X, e.Position.Y, e.Position.Z);
            int clip = ClipOf(e.State);
            _seen.Add(e.Id);
            float time;
            if (MusouLooks.Loops(clip)) { _clocks.TryGetValue(e.Id, out time); time += delta; _clocks[e.Id] = time; }
            else time = e.State == MusouEnemyState.Knocked ? KnockedTime(e) : e.StateSeconds;
            Items.Add(new Item { Key = e.Id, Type = (int)e.Type, Position = p, Yaw = e.Yaw, Time = time, Clip = clip, Distance = p.DistanceTo(camera) });
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
        MusouEnemyState.WindUp or MusouEnemyState.Strike or MusouEnemyState.Recover => MusouLooks.Attack,
        MusouEnemyState.Stagger => MusouLooks.Stagger,
        MusouEnemyState.Dying or MusouEnemyState.Knocked => MusouLooks.Death,
        _ => MusouLooks.Idle,
    };
}
