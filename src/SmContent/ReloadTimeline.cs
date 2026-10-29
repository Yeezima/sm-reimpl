using SmSim;

namespace SmContent;

/// <summary>
/// The clock, weight and magazine moments of one reload on screen (M35), without any engine type: <c>client/ReloadPlayer.cs</c> lays its
/// numbers on a character. The clip is played over exactly the reload time (<see cref="ReloadClip.PlaybackRate"/>), so its tags are fractions of the
/// reload; the layer blends in over the clip's blend-in time and out over its blend-out time (a cancelled reload fades over <see cref="CancelFadeSeconds"/>
/// from the weight it had, **ours**).
/// </summary>
public sealed class ReloadTimeline
{
    public const float CancelFadeSeconds = 0.1f;
    public const float DefaultBlendOut = 0.133f;

    public ReloadClip? Clip { get; private set; }
    /// <summary>The reload time (s) the clip is stretched to.</summary>
    public float Total { get; private set; }
    /// <summary>Seconds into the reload.</summary>
    public float Clock { get; private set; }
    public bool Active { get; private set; }
    /// <summary>Fading out after the reload finished or was cancelled.</summary>
    public bool Ending { get; private set; }
    public bool Shown => Active || Ending;
    /// <summary>Counts the reloads begun and reset (a scheduled callback checks it still belongs to its reload).</summary>
    public int Serial { get; private set; }
    public bool MagazineGone { get; private set; }
    public bool MagazineBack { get; private set; }
    /// <summary>The fraction of the clip at which the old magazine leaves: the remove tag, or later when the hand has not taken it yet (<see cref="Begin"/>); null: no magazine.</summary>
    public float? MagazineOutAt { get; private set; }
    /// <summary>The fraction at which the new magazine is in: the load tag, or later when the hand is not back yet; never before <see cref="MagazineOutAt"/>.</summary>
    public float? MagazineInAt { get; private set; }
    float _hold, _endAge, _endSeconds, _endFrom;

    /// <summary>
    /// Starts a reload of <paramref name="total"/> seconds, <paramref name="elapsed"/> of which have already run. <paramref name="handLeaves"/> and
    /// <paramref name="handArrives"/>: the fractions of the clip at which the hand has taken the old magazine away and is back with the new one
    /// (<c>ReloadHandProbe</c>), or null when not measured: the magazine leaves at the later of its remove tag and <paramref name="handLeaves"/>
    /// and returns at the later of its load tag and <paramref name="handArrives"/>.
    /// </summary>
    public void Begin(ReloadClip clip, float total, float elapsed, float? handLeaves = null, float? handArrives = null)
    {
        Clip = clip; Total = Math.Max(total, 0.05f); Clock = Math.Clamp(elapsed, 0f, Total);
        Active = true; Ending = false; MagazineGone = MagazineBack = false;
        MagazineOutAt = clip.MagazineOut is { } outTag ? Math.Max(outTag, handLeaves ?? 0f) : null;
        MagazineInAt = clip.MagazineIn is { } inTag ? Math.Max(Math.Max(inTag, handArrives ?? 0f), MagazineOutAt ?? 0f) : null;
        Serial++;
    }

    /// <summary>
    /// Runs the clock by one frame. With <paramref name="target"/> (the simulation's time into the reload, which moves in tick steps) the clock
    /// follows it: it keeps its own smooth pace while within one tick of the target and snaps to it beyond.
    /// </summary>
    public void Advance(float delta, float? target, float tick)
    {
        if (Ending) { _endAge += delta; if (_endAge >= _endSeconds) Ending = false; }
        if (!Active) return;
        Clock = Math.Min(Total, Clock + delta);
        if (target is { } t && (Clock > t + tick || Clock < t - tick)) Clock = Math.Clamp(t, 0f, Total);
    }

    /// <summary>Ends the reload: <paramref name="completed"/> blends out over the clip's time, a cancelled one fades quickly from the weight it had. The pose is held where it was while it fades.</summary>
    public void Finish(bool completed)
    {
        if (!Active) return;
        _endFrom = Weight;
        Active = false;
        _hold = completed ? Total : Clock;
        _endAge = 0f;
        _endSeconds = completed ? Clip?.BlendOut ?? DefaultBlendOut : CancelFadeSeconds;
        Ending = _endSeconds > 0f;
    }

    /// <summary>Drops everything at once (a new character, a respawn, a player leaving).</summary>
    public void Reset() { Active = Ending = false; MagazineGone = MagazineBack = false; Serial++; }

    /// <summary>The layer's weight: in over the blend-in time, out over the blend-out (from the weight it had when it ended).</summary>
    public float Weight
    {
        get
        {
            if (Active) { float blend = Math.Max(Clip?.BlendIn ?? 0.1f, 0.01f); return Math.Min(1f, Clock / blend); }
            return Ending ? Math.Max(0f, _endFrom * (1f - _endAge / Math.Max(_endSeconds, 1e-3f))) : 0f;
        }
    }

    /// <summary>The fraction of the clip the pose shows (the tags are fractions of it).</summary>
    public float ClipFraction
    {
        get
        {
            if (Clip is not { } c) return 0f;
            float seconds = (Active ? Clock : _hold) * c.PlaybackRate(Total);
            return c.ClipLength > 1e-3f ? Math.Clamp(seconds / c.ClipLength, 0f, 1f) : 0f;
        }
    }

    /// <summary>True once, when the reload has reached the moment the old magazine leaves.</summary>
    public bool TakeMagazineOut()
    {
        if (!Active || MagazineGone || MagazineOutAt is not { } at || ClipFraction < at) return false;
        return MagazineGone = true;
    }

    /// <summary>True once, after the magazine left, when the reload has reached the moment the new one is in.</summary>
    public bool TakeMagazineIn()
    {
        if (!Active || !MagazineGone || MagazineBack || MagazineInAt is not { } at || ClipFraction < at) return false;
        return MagazineBack = true;
    }
}

/// <summary>
/// Another player's reload, inferred from the shots the client sees (M35; ammunition and reload timers are not on the wire). Rounds fired are counted per
/// weapon against its clip size; the shot that empties the clip starts a reload of the weapon's reload time. **Interpretation**:
/// <list type="bullet">
/// <item>A full-auto weapon starts its reload one tick after the emptying shot (the simulation reloads on the next tick while the trigger is held); a
/// semi-auto weapon one shot cooldown after it (the earliest the next press, which starts the reload, can come).</item>
/// <item>A weapon whose clip and reserve are all spent since the player was last seen to spawn does not reload (nothing to load): counted from the first shot seen, so
/// a player seen late can get a phantom reload at the very end of its reserve.</item>
/// <item>A weapon's count goes back to empty when its last shot is older than its reload time + <see cref="IdleGraceSeconds"/> (the player probably reloaded by hand), and
/// every count when the player dies. A completed reload also refills.</item>
/// <item>A switch of weapon, a reaction, a melee attack, a throw or death cancels the reload (the clip stays empty).</item>
/// </list>
/// </summary>
public sealed class ReloadInference
{
    /// <summary>Seconds added to the reload time before an idle weapon counts as refilled.</summary>
    public const float IdleGraceSeconds = 1f;

    /// <summary>One shot seen: the weapon index, the rounds it used and its age in ticks.</summary>
    public readonly record struct Shot(int Weapon, int Cost, int Age);

    readonly Dictionary<int, int> _inClip = new();       // weapon -> rounds fired since it was last full
    readonly Dictionary<int, int> _fired = new();        // weapon -> rounds fired in all since the player spawned (seen)
    readonly Dictionary<int, double> _lastShotAt = new();
    int _weapon, _slot;
    float _elapsed, _total;

    /// <summary>A reload is under way (or waiting for its start).</summary>
    public bool Inferring { get; private set; }
    public int Weapon => _weapon;
    public float Total => _total;
    /// <summary>Seconds into the reload; negative while it waits for its start.</summary>
    public float Elapsed => _elapsed;
    public int Used(int weapon) => _inClip.GetValueOrDefault(weapon);

    /// <summary>
    /// One frame. <paramref name="weapons"/>: the configuration's weapons; <paramref name="reloads"/>: whether a weapon index has a reload to show;
    /// <paramref name="busy"/>: the player is in a reaction, melee attack or throw; <paramref name="alive"/>; <paramref name="slot"/>: its weapon slot (a change cancels);
    /// <paramref name="now"/> and <paramref name="delta"/>: seconds. Returns the reload that should be shown now (weapon, total, elapsed ≥ 0), or null.
    /// </summary>
    public (int Weapon, float Total, float Elapsed)? Update(IReadOnlyList<WeaponSpec> weapons, Func<int, bool> reloads, IEnumerable<Shot> shots, bool alive, bool busy, int slot, double now, float delta, float tick)
    {
        if (!alive) { _inClip.Clear(); _fired.Clear(); _lastShotAt.Clear(); Inferring = false; return null; }
        if (Inferring)
        {
            _elapsed += delta;
            if (busy || slot != _slot) { Inferring = false; return null; }   // cancelled: the clip stays empty
            if (_elapsed >= _total) { Inferring = false; _inClip[_weapon] = 0; return null; }
            return _elapsed >= 0f ? (_weapon, _total, _elapsed) : null;
        }
        foreach (var shot in shots)
        {
            if ((uint)shot.Weapon >= (uint)weapons.Count || !reloads(shot.Weapon)) continue;
            var weapon = weapons[shot.Weapon];
            // A weapon not fired for longer than its reload took was refilled by hand.
            if (_lastShotAt.TryGetValue(shot.Weapon, out double last) && now - last > weapon.ReloadTime + IdleGraceSeconds) _inClip[shot.Weapon] = 0;
            _lastShotAt[shot.Weapon] = now;
            int cost = Math.Max(1, shot.Cost);
            int used = Math.Min(weapon.ClipSize, _inClip.GetValueOrDefault(shot.Weapon) + cost);
            _inClip[shot.Weapon] = used;
            int fired = _fired.GetValueOrDefault(shot.Weapon) + cost;
            _fired[shot.Weapon] = fired;
            if (used < weapon.ClipSize || busy) continue;
            if (fired >= weapon.ClipSize + weapon.ReserveSize) continue;   // nothing left to load
            Inferring = true; _weapon = shot.Weapon; _slot = slot; _total = weapon.ReloadTime;
            float wait = weapon.Trigger == TriggerKind.SemiAuto ? Math.Max(weapon.ShotCooldown, tick) : tick;
            _elapsed = Math.Max(0, shot.Age) * tick + delta - wait;
            break;
        }
        return Inferring && _elapsed >= 0f ? (_weapon, _total, _elapsed) : null;
    }
}
