using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The Big Shoota Nob's gun (expansion S3, docs/musou-expansion.md "Big Shoota Nob"; Musou Mode only; every value ours or an estimate, owner brief 2026-11-07, spec "Musou expansion: Big Shoota Nob").
/// Times in seconds, distances in metres, damage in the players' health and shield points. Hashed into the config only through <see cref="MusouSim.Version"/>.
/// </summary>
public static class MusouBigShoota
{
    /// <summary>A burst of sustained suppressing fire lasts this long (owner brief: 4 s) at this many rounds a second (plan: 12) ...</summary>
    public const float BurstSeconds = 4f, RoundsPerSecond = 12f;
    /// <summary>... each round this much (plan: 4) ...</summary>
    public const float RoundDamage = 4f;
    /// <summary>... hitting with this chance at <see cref="NearRange"/> and at <see cref="MaxRange"/>, linear between (ours: the spread; a miss is a tracer beside the target) ...</summary>
    public const float HitNear = 0.35f, HitFar = 0.15f;
    /// <summary>... at targets from this far up to this far (ours) ...</summary>
    public const float NearRange = 8f, MaxRange = 30f;   // his band's far edge (the type's RangedMax): beyond it he closes in
    /// <summary>... then the gun overheats or jams for this long (owner brief: 3.5 s): steam, no fire. The player's opening.</summary>
    public const float OverheatSeconds = 3.5f;
    /// <summary>A Big Shoota Nob holding a post stands within this of it (ours).</summary>
    public const float PostRadius = 1f;
    /// <summary>His cues (<see cref="MusouEventKind.Telegraph"/>'s value, after the Stormboy's): a burst begun (its target in the event's player), the gun overheated (steam; his chest in the vector), and vented (firing again).</summary>
    public const byte BurstCue = 35, OverheatCue = 36, VentCue = 37;
    /// <summary>His bullets never hurt Orks: they are rolled hits on his target (a player, an AI Marine or a Guard squad), with no projectile. Recorded for the friendly-fire table.</summary>
    public const bool BulletsHurtOrks = false;
}

/// <summary>
/// The Big Shoota Nob in the pool (expansion S3; <see cref="MusouBigShoota"/>). A posted Nob (<see cref="Post"/>, a war's) holds his spot and fires at whatever he can see in range; an unposted one
/// keeps the shooters' band. Either way his gun has its own cycle in place of the shooters' bursts: a sustained burst of <see cref="MusouBigShoota.BurstSeconds"/> at
/// <see cref="MusouBigShoota.RoundsPerSecond"/>, then <see cref="MusouEnemyState.Overheated"/> for <see cref="MusouBigShoota.OverheatSeconds"/>, then ready again. A stagger cuts a burst short, and the gun
/// overheats when he is up again.
/// </summary>
public sealed partial class MusouSim
{
    readonly bool[] _posted = new bool[Capacity];
    readonly Vector3[] _post = new Vector3[Capacity];
    readonly ushort[] _fireLeft = new ushort[Capacity];
    readonly float[] _fireDue = new float[Capacity];
    readonly Vector3[] _fireAt = new Vector3[Capacity];
    readonly bool[] _ventDue = new bool[Capacity];

    /// <summary>Bursts begun and overheats so far (for the report and tests).</summary>
    public int BigShootaBursts { get; private set; }
    public int BigShootaOverheats { get; private set; }

    void SpawnedBigShoota(int i) { _posted[i] = false; _fireLeft[i] = 0; _fireDue[i] = 0f; _ventDue[i] = false; }

    /// <summary>A Big Shoota Nob holds <paramref name="spot"/> from now on (he never walks off it). False for any other kind or a dead one.</summary>
    public bool Post(ushort id, Vector3 spot)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i] || _type[i] != MusouEnemyType.BigShootaNob || _state[i] == MusouEnemyState.Dying || !float.IsFinite(spot.X) || !float.IsFinite(spot.Y) || !float.IsFinite(spot.Z)) return false;
        _posted[i] = true; _post[i] = spot;
        return true;
    }

    /// <summary>The spot a Big Shoota Nob holds (null: none).</summary>
    public Vector3? PostOf(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] && _posted[id - 1] ? _post[id - 1] : null;

    /// <summary>Whether a Big Shoota Nob is firing a burst now.</summary>
    public bool Firing(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] && _fireLeft[id - 1] > 0;

    /// <summary>
    /// From <see cref="Step"/>, for a Big Shoota Nob not staggered: the overheat, the burst, and (posted) holding his spot. True when his tick is done; false when he goes on as any shooter (unposted,
    /// not firing: his band; the shooters' own bursts never start for him).
    /// </summary>
    bool StepBigShoota(int i, ReadOnlySpan<MusouTarget> targets, Span<int> near, MusouTypeSpec spec, bool fight, List<MusouAttack> attacks)
    {
        var state = _state[i];
        if (state is MusouEnemyState.Stagger or MusouEnemyState.Knocked)
        {
            if (_fireLeft[i] > 0) { _fireLeft[i] = 0; _fireDue[i] = 0f; _ventDue[i] = true; }   // a stagger cuts the burst short: the gun overheats when he is up again
            return false;
        }
        _headwayTick[i] = Tick; _anchor[i] = _position[i];
        if (_ventDue[i]) { _ventDue[i] = false; Overheat(i); state = MusouEnemyState.Overheated; }
        if (state == MusouEnemyState.Overheated)
        {
            if (_stateTicks[i] >= Ticks(MusouBigShoota.OverheatSeconds))
            {
                SetState(i, MusouEnemyState.Seek);
                AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, (ushort)(i + 1), 0, MusouBigShoota.VentCue, Chest(_position[i], spec.Height)));
            }
            else
            {
                int watched = IndexOf(targets, _target[i]);
                if (watched >= 0) Face(i, Toward(i, targets[watched].Position));
                Move(i, spec, Vector2.Zero, targets, near);
                return true;
            }
        }
        if (_fireLeft[i] > 0)
        {
            int at = IndexOf(targets, _target[i]);
            if (at >= 0 && !targets[at].Goal) _fireAt[i] = targets[at].Position;
            Face(i, Toward(i, _fireAt[i]));
            _fireDue[i] += MusouBigShoota.RoundsPerSecond * Dt;
            while (_fireDue[i] >= 1f) { _fireDue[i] -= 1f; FireBigShoota(i, at >= 0 && !targets[at].Goal ? targets[at] : null, fight, attacks); }
            if (--_fireLeft[i] == 0) Overheat(i);
            Move(i, spec, Vector2.Zero, targets, near);
            return true;
        }
        _cooldown[i] = Math.Max(_cooldown[i], (ushort)2);   // the shooters' own bursts never start for him (Behave)
        int t = Target(i, targets);
        bool aim = t >= 0 && !targets[t].Goal;
        if (aim)
        {
            if (_token[i] != ShooterToken) { _token[i] = ShooterToken; _losTick[i] = 0; }   // his gun is never left waiting for a token: he keeps one over the cap, so his line of sight is checked
            float distance = new Vector2(targets[t].Position.X - _position[i].X, targets[t].Position.Y - _position[i].Y).Length();
            bool fresh = _losTick[i] != 0 && Tick - _losTick[i] <= (uint)Rules.LosValidTicks;
            if (fight && fresh && _los[i] && distance <= MusouBigShoota.MaxRange)
            {
                _fireLeft[i] = Ticks(MusouBigShoota.BurstSeconds);
                _fireDue[i] = 1f;   // the first round at once
                _fireAt[i] = targets[t].Position;
                BigShootaBursts++;
                AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, (ushort)(i + 1), (ushort)Math.Clamp(targets[t].Id, 0, ushort.MaxValue), MusouBigShoota.BurstCue, _position[i]));
                Face(i, Toward(i, targets[t].Position));
                Move(i, spec, Vector2.Zero, targets, near);
                return true;
            }
        }
        if (!_posted[i]) return false;
        // Posted: back to his spot, facing whatever he aims at.
        var home = new Vector2(_post[i].X - _position[i].X, _post[i].Y - _position[i].Y);
        float off = home.Length();
        if (aim) Face(i, Toward(i, targets[t].Position));
        Move(i, spec, off > MusouBigShoota.PostRadius ? home / off * spec.Speed : Vector2.Zero, targets, near);
        if (aim) Face(i, Toward(i, targets[t].Position));
        return true;
    }

    Vector2 Toward(int i, Vector3 at)
    {
        var d = new Vector2(at.X - _position[i].X, at.Y - _position[i].Y);
        return d.LengthSquared() > 1e-8f ? Vector2.Normalize(d) : Forward(_yaw[i]);
    }

    void Overheat(int i)
    {
        _fireLeft[i] = 0; _fireDue[i] = 0f;
        BigShootaOverheats++;
        SetState(i, MusouEnemyState.Overheated);
        AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, (ushort)(i + 1), 0, MusouBigShoota.OverheatCue, Chest(_position[i], Rules.Of(_type[i]).Height)));
    }

    /// <summary>One round: a hit roll by distance on his target (none: a miss at where he aims), the tracer, the damage on a hit. No projectile: it can only ever strike his target, never an Ork.</summary>
    void FireBigShoota(int i, MusouTarget? target, bool fight, List<MusouAttack> attacks)
    {
        var aim = target?.Position ?? _fireAt[i];
        float distance = new Vector2(aim.X - _position[i].X, aim.Y - _position[i].Y).Length();
        float span = MusouBigShoota.MaxRange - MusouBigShoota.NearRange;
        float chance = MusouBigShoota.HitNear + (MusouBigShoota.HitFar - MusouBigShoota.HitNear) * Math.Clamp((distance - MusouBigShoota.NearRange) / span, 0f, 1f);
        uint roll = Mix(Tick, (uint)i * 0x85EBCA6Bu + (uint)_fireLeft[i] * 31u + (uint)(_fireDue[i] * 1000f));
        bool hit = fight && target is { } tgt && distance <= MusouBigShoota.MaxRange * 1.2f && Unit(roll) < chance;
        var chest = aim + new Vector3(0f, 0f, 1.2f);
        var end = chest;
        if (!hit)
        {
            var from = _position[i];
            var side = Vector2.Normalize(new Vector2(-(chest.Y - from.Y), chest.X - from.X) + new Vector2(1e-4f, 0f));
            float spread = (0.5f + 1.5f * Unit(Mix(roll, 1u))) * ((roll & 1u) == 0u ? 1f : -1f);
            end = chest + new Vector3(side * spread, (Unit(Mix(roll, 2u)) - 0.5f) * 1.2f);
        }
        else attacks.Add(new MusouAttack((ushort)(i + 1), target!.Value.Id, MusouBigShoota.RoundDamage, false, false, _position[i]));
        AddEvent(new MusouEvent(MusouEventKind.Tracer, Tick, (ushort)(i + 1), hit ? (ushort)Math.Clamp(target!.Value.Id, 0, ushort.MaxValue) : (ushort)0, 0, end));
    }
}
