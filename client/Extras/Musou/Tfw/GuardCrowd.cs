using System;
using System.Collections.Generic;
using Godot;
using SmContent.Extras.Musou;
using SmFormats.Havok;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The Guardsmen as drawn (TF7a, design section E; the same tiers as <see cref="MusouCrowd"/>, <see cref="CrowdView"/>): 20 skinned near, the rest vertex-animated, 120 visible (60 on Low).
/// Tag 9's records give the place, facing and state; a man's speed and direction come from how his interpolated place moved. Tag 10's events: a shot sets the man's recoil and aim pitch and keeps him
/// in the aim set for <see cref="EngagedSeconds"/>, a hurt man plays the light reaction when he stands, a death leaves a body playing a death clip where he stood (no ragdoll yet, the owner's order).
/// **Skinned tier**: the four-way aimed run blended by the direction of travel, the aim triple blended by the pitch of the last shot, the recoil laid over either, a short fade between clips.
/// **Far tier**: one baked clip at a time (the run clip nearest the direction, the level aim), no recoil. Which clip plays when and every number here are ours.
/// </summary>
public partial class GuardCrowd : CrowdView
{
    public const int DefaultVisible = 120, LowVisible = 60, SkinnedNear = 20, MaxBodyCount = 40;
    public const float NearDistance = 25f, HalfRateDistance = 12f;
    /// <summary>Seconds after his last shot a man stays in the aim set.</summary>
    public const float EngagedSeconds = 3f;
    /// <summary>Below this speed (m/s) a man counts as standing.</summary>
    public const float StandingSpeed = 0.4f;
    /// <summary>Seconds of the fade between two skinned clips.</summary>
    public const float FadeSeconds = 0.15f;
    /// <summary>How fast (per second) the drawn facing and the direction of travel follow the records.</summary>
    const float TurnRate = 12f, TravelRate = 10f;

    static readonly int[] Only = [0];

    sealed class Man
    {
        public Vector3 Position, Last;
        public Vector2 Travel;      // m/s, on the man (X his right, Y his front)
        public float Yaw, Pitch, Speed, Clock, Down;
        public float SinceShot = float.PositiveInfinity, SinceHurt = float.PositiveInfinity, Fade;
        public int Clip = -1;
        public bool Seen;
    }

    readonly GuardLooks _looks;
    readonly Dictionary<ushort, Man> _men = new();
    readonly GuardDeaths _deaths = new();
    readonly List<ushort> _gone = new();
    Quaternion[] _quats = [];
    Vector3[] _spots = [];
    bool[] _touched = [];
    static HavokTransform[] _sample = [], _reference = [];

    public GuardCrowd(GuardLooks looks, int visible = DefaultVisible)
        : base(new CrowdLimits(SkinnedNear, MusouCrowd.MaxVat, MusouCrowd.FallbackSkinned / 2, NearDistance, HalfRateDistance, MusouCrowd.SinkAfter, MusouCrowd.SinkSeconds, MusouCrowd.SinkDepth, MaxBodyCount), visible)
    {
        _looks = looks; Name = "GuardCrowd";
    }

    protected override IReadOnlyList<int> Types => Only;
    protected override MusouLooks.Look? LookOf(int type) => _looks.Look;
    protected override bool Loops(int clip) => clip < GuardRig.Hurt;

    /// <summary>An event of tag 10. Call before <see cref="Frame"/>: a death needs the man's last place.</summary>
    public void Event(in TfwGuardEvent e)
    {
        _men.TryGetValue(e.Guard, out var man);
        switch (e.Kind)
        {
            case TfwGuardEventKind.Shot:
                if (man is null) return;
                man.SinceShot = 0f;
                var d = new Vector3(e.Vector.X, e.Vector.Y, e.Vector.Z) - (man.Position + new Vector3(0f, 0f, _looks.Muzzle.Z));
                float flat = new Vector2(d.X, d.Y).Length();
                if (flat > 0.1f) man.Pitch = MathF.Atan2(d.Z, flat);
                break;
            case TfwGuardEventKind.Hurt:
                if (man is not null && man.SinceHurt >= HurtSeconds) man.SinceHurt = 0f;
                break;
            case TfwGuardEventKind.Death:
                Fall(e.Guard, e.Tick, man?.Position ?? new Vector3(e.Vector.X, e.Vector.Y, e.Vector.Z), man?.Yaw ?? 0f, e.Tick);
                break;
        }
    }

    float HurtSeconds => _looks.Look?.Clips[GuardRig.Hurt]?.Animation.Duration ?? 2f;

    /// <summary>One body a death, whichever report comes first (<see cref="GuardDeaths"/>).</summary>
    void Fall(ushort id, uint tick, Vector3 at, float yaw, uint clipTick)
    {
        if (_deaths.Died(id, tick)) AddBody(at, yaw, 0, GuardRig.DeathClip(id, clipTick));
    }

    /// <summary>Where a man's muzzle is now (game space); null for a man not shown.</summary>
    public Vector3? MuzzleOf(ushort id)
    {
        if (!_men.TryGetValue(id, out var man)) return null;
        if (SkinnedOf(id) is { } s && _looks.MuzzleBone >= 0)
            return (s.Root.Transform * s.Skeleton.GetBoneGlobalPose(_looks.MuzzleBone) * _looks.MuzzleOnBone).Origin;
        return man.Position + new Basis(new Vector3(0, 0, 1), man.Yaw) * _looks.Muzzle;
    }

    /// <summary>The frame: <paramref name="shown"/> at the render tick, the camera in game space.</summary>
    public void Frame(IReadOnlyList<TfwGuardShown> shown, uint tick, Vector3 camera, float delta)
    {
        UpkeepLooks("guards");
        _deaths.Forget(tick);
        Items.Clear();
        foreach (var man in _men.Values) man.Seen = false;
        foreach (var g in shown)
        {
            var p = new Vector3(g.Position.X, g.Position.Y, g.Position.Z);
            bool isNew = !_men.TryGetValue(g.Id, out var man);
            if (isNew) _men[g.Id] = man = new Man { Position = p, Last = p, Yaw = g.Yaw };
            man!.Seen = true;
            if (g.State == (byte)GuardState.Dying)
            {
                Fall(g.Id, tick, man.Position, man.Yaw, tick);   // the event was lost, or came first: one body either way
                continue;
            }
            if (_deaths.IsGone(g.Id, tick)) continue;   // a record behind his death: the body is drawn, not him
            Step(man, p, g.Yaw, delta);
            if (g.State == (byte)GuardState.Knocked)
            {
                // Down (a Marine's shove): a death clip, held, then backwards to stand, as the Orks' (MusouCrowd.KnockedTime).
                man.Down += delta;
                float length = _looks.Look?.Clips[GuardRig.FirstDeath]?.Animation.Duration ?? 1f;
                Items.Add(new Item { Key = g.Id, Position = p, Yaw = man.Yaw, Time = TfwCombatRules.Default.FallClipTime(man.Down, length), Clip = GuardRig.FirstDeath, Distance = p.DistanceTo(camera) });
                continue;
            }
            man.Down = 0f;
            bool moving = g.State == (byte)GuardState.Moving && man.Speed > StandingSpeed;
            int clip = ClipOf(man, moving);
            if (clip != man.Clip)
            {
                if (man.Clip >= 0) man.Fade = FadeSeconds;
                if (clip == GuardRig.Hurt || man.Clip == GuardRig.Hurt) man.Clock = 0f;
                man.Clip = clip;
            }
            man.Clock += delta * (moving ? GuardRig.RunRate(man.Speed) : 1f);
            float time = clip == GuardRig.Hurt ? man.SinceHurt : man.Clock;
            Items.Add(new Item { Key = g.Id, Position = p, Yaw = man.Yaw, Time = time, Clip = clip, Distance = p.DistanceTo(camera) });
        }
        _gone.Clear();
        foreach (var (id, man) in _men) if (!man.Seen) _gone.Add(id);
        foreach (var id in _gone) _men.Remove(id);
        AddBodyItems(camera, delta);
        Draw(delta);
        foreach (var man in _men.Values) man.Fade = MathF.Max(0f, man.Fade - delta);
    }

    static void Step(Man man, Vector3 p, float yaw, float delta)
    {
        float turn = MathF.IEEERemainder(yaw - man.Yaw, MathF.Tau);
        man.Yaw += turn * Math.Clamp(TurnRate * delta, 0f, 1f);
        man.Position = p;
        if (delta > 1e-5f)
        {
            var moved = new Vector2(p.X - man.Last.X, p.Y - man.Last.Y) / delta;
            float c = MathF.Cos(man.Yaw), s = MathF.Sin(man.Yaw);
            man.Travel = man.Travel.Lerp(new Vector2(moved.X * c + moved.Y * s, -moved.X * s + moved.Y * c), Math.Clamp(TravelRate * delta, 0f, 1f));
            man.Speed = man.Travel.Length();
        }
        man.Last = p;
        man.SinceShot += delta; man.SinceHurt += delta;
    }

    int ClipOf(Man man, bool moving)
    {
        if (!moving && man.SinceHurt < HurtSeconds) return GuardRig.Hurt;
        bool engaged = man.SinceShot < EngagedSeconds;
        if (!moving) return engaged ? GuardRig.AimIdle : GuardRig.Idle;
        if (!engaged) return GuardRig.Run;
        var t = man.Travel;
        return MathF.Abs(t.Y) >= MathF.Abs(t.X) ? (t.Y >= 0f ? GuardRig.RunF : GuardRig.RunB) : t.X >= 0f ? GuardRig.RunR : GuardRig.RunL;
    }

    // ---------------------------------------------------------------- the skinned pose

    protected override void PoseSkinned(Skinned s, in Item it)
    {
        if (it.Key <= 0 || !_men.TryGetValue((ushort)it.Key, out var man)) { base.PoseSkinned(s, it); return; }   // a body
        int bones = s.Rest.Length;
        Prepare(s, bones);
        var clips = s.Look.Clips;
        if (it.Clip == GuardRig.AimIdle)
        {
            var (down, level, up) = GuardRig.AimWeights(man.Pitch);
            float weight = 0f;
            Layer(clips[GuardRig.AimIdle], it.Time, level, ref weight);
            Layer(_looks.AimDown ?? clips[GuardRig.AimIdle], it.Time, down, ref weight);
            Layer(_looks.AimUp ?? clips[GuardRig.AimIdle], it.Time, up, ref weight);
        }
        else if (it.Clip is GuardRig.RunF or GuardRig.RunB or GuardRig.RunL or GuardRig.RunR)
        {
            var (f, b, l, r) = GuardRig.RunWeights(new System.Numerics.Vector2(man.Travel.X, man.Travel.Y));
            float weight = 0f;
            Layer(clips[GuardRig.RunF], it.Time, f, ref weight);
            Layer(clips[GuardRig.RunB], it.Time, b, ref weight);
            Layer(clips[GuardRig.RunL], it.Time, l, ref weight);
            Layer(clips[GuardRig.RunR], it.Time, r, ref weight);
        }
        else
        {
            float weight = 0f;
            Layer(clips[it.Clip], it.Clip == GuardRig.Hurt ? MathF.Min(it.Time, HurtSeconds) : it.Time, 1f, ref weight);
        }
        if (_looks.Recoil is { } recoil && man.SinceShot <= recoil.Animation.Duration) Recoil(recoil, man.SinceShot);
        Finish(s, man);
    }

    void Prepare(Skinned s, int bones)
    {
        if (_quats.Length != bones)
        {
            _quats = new Quaternion[bones]; _spots = new Vector3[bones]; _touched = new bool[bones];
        }
        for (int b = 0; b < bones; b++)
        {
            _spots[b] = s.Rest[b].Origin;
            _quats[b] = s.Rest[b].Basis.GetRotationQuaternion();
            _touched[b] = false;
        }
    }

    /// <summary>Blends a clip's pose into the pose so far, by its share of the weight so far.</summary>
    void Layer(HavokBoundAnimation? clip, float time, float weight, ref float sum)
    {
        if (clip is null || weight <= 1e-3f) return;
        if (_sample.Length != clip.Animation.TrackCount) _sample = new HavokTransform[clip.Animation.TrackCount];
        float length = MathF.Max(clip.Animation.Duration, 1e-3f);
        clip.Animation.SampleInto(time >= length ? time % length : time, _sample);
        float share = weight / (sum + weight);
        for (int k = 0; k < _sample.Length; k++)
        {
            int bone = clip.TrackToBone[k];
            if ((uint)bone >= (uint)_quats.Length) continue;
            var q = new Quaternion(_sample[k].Rotation.X, _sample[k].Rotation.Y, _sample[k].Rotation.Z, _sample[k].Rotation.W).Normalized();
            var o = new Vector3(_sample[k].Translation.X, _sample[k].Translation.Y, _sample[k].Translation.Z);
            if (!_touched[bone] || share >= 1f) { _quats[bone] = q; _spots[bone] = o; _touched[bone] = true; continue; }
            _quats[bone] = _quats[bone].Slerp(q, share).Normalized();
            _spots[bone] = _spots[bone].Lerp(o, share);
        }
        sum += weight;
    }

    /// <summary>The recoil over the pose: its offsets, or (a whole-pose clip) its difference from its first frame.</summary>
    void Recoil(HavokBoundAnimation recoil, float time)
    {
        int tracks = recoil.Animation.TrackCount;
        if (_sample.Length != tracks) _sample = new HavokTransform[tracks];
        recoil.Animation.SampleInto(MathF.Min(time, recoil.Animation.Duration), _sample);
        if (!_looks.RecoilAdditive)
        {
            if (_reference.Length != tracks) _reference = new HavokTransform[tracks];
            recoil.Animation.SampleInto(0f, _reference);
        }
        for (int k = 0; k < tracks; k++)
        {
            int bone = recoil.TrackToBone[k];
            if ((uint)bone >= (uint)_quats.Length) continue;
            var q = new Quaternion(_sample[k].Rotation.X, _sample[k].Rotation.Y, _sample[k].Rotation.Z, _sample[k].Rotation.W).Normalized();
            var o = new Vector3(_sample[k].Translation.X, _sample[k].Translation.Y, _sample[k].Translation.Z);
            if (!_looks.RecoilAdditive)
            {
                q = (q * new Quaternion(_reference[k].Rotation.X, _reference[k].Rotation.Y, _reference[k].Rotation.Z, _reference[k].Rotation.W).Normalized().Inverse()).Normalized();
                o -= new Vector3(_reference[k].Translation.X, _reference[k].Translation.Y, _reference[k].Translation.Z);
            }
            _quats[bone] = (q * _quats[bone]).Normalized();
            _spots[bone] += o;
        }
    }

    /// <summary>The root stays over the man's place (as <see cref="MusouVat.LockRoot"/>), a running fade blends from what is posed now, and the pose goes on the skeleton.</summary>
    void Finish(Skinned s, Man man)
    {
        var root = s.Rest[0].Origin;
        _spots[0] = new Vector3(root.X, root.Y, _spots[0].Z);
        float keep = man.Fade > 0f ? man.Fade / FadeSeconds : 0f;
        for (int b = 0; b < _quats.Length; b++)
        {
            var q = _quats[b];
            var o = _spots[b];
            if (keep > 0f)
            {
                q = q.Slerp(s.Skeleton.GetBonePoseRotation(b), keep).Normalized();
                o = o.Lerp(s.Skeleton.GetBonePosePosition(b), keep);
            }
            s.Skeleton.SetBonePosePosition(b, o);
            s.Skeleton.SetBonePoseRotation(b, q);
        }
    }
}
