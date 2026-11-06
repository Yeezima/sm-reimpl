using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The Weirdboy's own rules (Musou Mode only; spec "Musou Mode: the Weirdboy"). From the data (<c>ork_coop_weirdboy</c>, <c>weirdboy_frazzle_coop</c>, <c>ork_coop_weirdboy_staff</c>) where it
/// has them, else ours (kind: ours). Times in seconds, distances in metres. Hashed into the config only through <see cref="MusouSim.Version"/>.
/// </summary>
public static class MusouWeirdboy
{
    /// <summary>A broken shield stuns him this long (data: <c>armourshield_broken</c> applies 1001 stun damage over a threshold of 1000, <c>stun_default_length</c> 8) ...</summary>
    public const float StunSeconds = 8f;
    /// <summary>... and comes back with this many points when the stun ends (data: the same action's <c>delayed_action</c>, 8 s, <c>do_heal armourshield</c> 75).</summary>
    public const float ShieldRestore = 75f;
    /// <summary>He teleports away from a target that comes within this (ours) ...</summary>
    public const float TeleportTrigger = 6f;
    /// <summary>... at most this often (ours) ...</summary>
    public const float TeleportCooldown = 6f;
    /// <summary>... fading for this long first (ours; the clip <c>WIB_1HM_ACT_Telaport_Out_01</c>) ...</summary>
    public const float VanishSeconds = 0.6f;
    /// <summary>... to a place this far from that target (ours: inside his band, the data's ideal 20 to 50 m).</summary>
    public const float TeleportMin = 25f, TeleportMax = 35f;
    /// <summary>He calls <see cref="SummonCount"/> Boyz this long after he comes (ours), then every <see cref="SummonCooldown"/> (ours), casting for <see cref="SummonSeconds"/> (ours; the clips
    /// <c>WIB_1HM_ACT_Summon_orks_start/loop/release_01</c>); they stand on a ring of <see cref="SummonRingMin"/> to <see cref="SummonRingMax"/> round him (ours).</summary>
    public const float SummonFirst = 8f, SummonCooldown = 20f, SummonSeconds = 2.5f;
    public const int SummonCount = 4;
    public const float SummonRingMin = 2f, SummonRingMax = 4f;
    /// <summary>A summons needs this many free slots in the pool beyond its Boyz (ours; the battle's own reserve).</summary>
    public const int SummonReserve = 80;
    /// <summary>A bolt's burst reaches this far up and down (ours).</summary>
    public const float BoltVerticalReach = 2.5f;
    /// <summary>When his shield breaks, every Ork within this (flat; ours, the owner 2026-11-07: "ragdoll all orks within 20m") but the Warboss, the Boss Orks and other Weirdboyz is thrown down
    /// (<see cref="MusouEnemyState.Knocked"/>, the fall Musou Mode's shove uses) <see cref="BreakKnockback"/> away from him (ours: the charge's push).</summary>
    public const float BreakRadius = 20f, BreakKnockback = 4f;
    /// <summary>When he dies he bursts: every Ork within this (flat; the owner 2026-11-07: "killing all orks around him ... within 20m") but the Warboss and the Boss Orks ("Spare Boss Orks") dies, credited to his killer, as a blast.</summary>
    public const float BurstRadius = 20f;
    /// <summary>Both reach this far up and down (ours).</summary>
    public const float PsychicVerticalReach = 4f;
    /// <summary>The hit marker of a hit the shield took (<see cref="MusouEventKind.Hit"/>'s value; 1 body, 2 head, 3 melee).</summary>
    public const byte ShieldMarker = 4;

    /// <summary>The Weirdboy's cues (<see cref="MusouEventKind.Telegraph"/>'s value; above the Warboss's abilities): a bolt thrown (its target in the event's player, where it bursts in the vector),
    /// the shield broken (the Orks round him thrown down), the shield back, a teleport (where he goes in the vector), a summons begun, his death's burst (where, in the vector).</summary>
    public const byte BoltCue = 16, ShieldBreakCue = 17, ShieldBackCue = 18, TeleportCue = 19, SummonCue = 20, BurstCue = 21;
}

/// <summary>
/// The Weirdboy in the pool (Musou Mode only; <see cref="MusouWeirdboy"/>). Between his own acts he is an ordinary hybrid: the staff within 5 m, else his band and his bolts. His acts:
/// <list type="bullet">
/// <item>The shield (<see cref="MusouTypeSpec.ShieldMax"/>): a shot or a strafe's round does nothing; melee, blasts and the barrage wear it down; at 0 it breaks and he is
/// <see cref="MusouEnemyState.Stunned"/> for <see cref="MusouWeirdboy.StunSeconds"/>, his health open to everything, then it comes back with <see cref="MusouWeirdboy.ShieldRestore"/>.</item>
/// <item>The bolt (<see cref="MusouTypeSpec.RangedBlastRadius"/>): aimed at the target's feet, it bursts there after its flight.</item>
/// <item>The teleport: a target within <see cref="MusouWeirdboy.TeleportTrigger"/>, and he fades (<see cref="MusouEnemyState.Vanishing"/>) and appears 25 to 35 m from it.</item>
/// <item>The summons: he casts (<see cref="MusouEnemyState.Casting"/>) and Boyz come round him.</item>
/// </list>
/// A stagger cuts a cast or a fade short; it never ends the stun.
/// </summary>
public sealed partial class MusouSim
{
    readonly float[] _shield = new float[Capacity];
    readonly ushort[] _teleportCd = new ushort[Capacity];
    readonly ushort[] _summonCd = new ushort[Capacity];
    readonly Vector3[] _blink = new Vector3[Capacity];
    readonly int[] _blinkNode = new int[Capacity];
    readonly List<Bolt> _bolts = new();

    /// <summary>A bolt in flight: who threw it, where and when it bursts, its damage and radius.</summary>
    readonly record struct Bolt(ushort Enemy, Vector3 From, Vector3 Point, uint Lands, float Damage, float Radius);

    /// <summary>The bolts in flight now.</summary>
    public int BoltsInFlight => _bolts.Count;

    /// <summary>An enemy's energy shield now (0: none, broken, or an unknown enemy).</summary>
    public float ShieldOf(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] ? _shield[id - 1] : 0f;

    void SpawnedWeirdboy(int i)
    {
        var spec = Rules.Of(_type[i]);
        _shield[i] = spec.ShieldMax > 0f ? spec.ShieldMax : 0f;
        _teleportCd[i] = 0;
        _summonCd[i] = _type[i] == MusouEnemyType.Weirdboy ? Ticks(MusouWeirdboy.SummonFirst) : (ushort)0;
        _blinkNode[i] = -1;
    }

    /// <summary>
    /// The shield's share of a hit (from <see cref="Damage"/>): true when the shield took it all (nothing reaches the health). A shot or a strafe's round is stopped whole; anything else wears the
    /// shield down, and the hit that empties it breaks it (what is left of that hit is lost). The failsafe's removal (<see cref="MusouDeathKind.World"/>) goes through.
    /// </summary>
    bool ShieldTakes(int i, int attacker, float amount, MusouDeathKind cause)
    {
        if (_shield[i] <= 0f || cause == MusouDeathKind.World || _state[i] == MusouEnemyState.Stunned) return false;
        if (_group[i] != 0) ReleaseGroup(_group[i]);
        if (cause is not (MusouDeathKind.Shot or MusouDeathKind.Strafe)) _shield[i] = MathF.Max(0f, _shield[i] - amount);
        AddEvent(new MusouEvent(MusouEventKind.Hit, Tick, (ushort)(i + 1), (ushort)Math.Clamp(attacker, 0, ushort.MaxValue), MusouWeirdboy.ShieldMarker, _position[i]));
        if (_shield[i] <= 0f) BreakShield(i);
        return true;
    }

    void BreakShield(int i)
    {
        _shield[i] = 0f;
        SetState(i, MusouEnemyState.Stunned);
        _token[i] = 0; _burst[i] = 0; _scripted[i] = false;
        AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, (ushort)(i + 1), 0, MusouWeirdboy.ShieldBreakCue, _position[i]));
        ThrowDownAround(i);
    }

    /// <summary>Whether a type counts as an Ork for his break and his burst (every type but the Chaos Marine).</summary>
    static bool Ork(MusouEnemyType type) => type != MusouEnemyType.ChaosMarine;

    /// <summary>His shield's break throws every Ork within <see cref="MusouWeirdboy.BreakRadius"/> down, away from him (outside a war, where nobody falls: a stagger).</summary>
    void ThrowDownAround(int i)
    {
        var near = new ushort[Capacity];   // its own list: the burst calls itself again through Damage
        int n = Query(_position[i], MusouWeirdboy.BreakRadius, near, MusouWeirdboy.PsychicVerticalReach);
        for (int k = 0; k < n; k++)
        {
            int j = near[k] - 1;
            var type = _type[j];
            if (j == i || !Ork(type) || type is MusouEnemyType.Warboss or MusouEnemyType.Weirdboy || MusouBossOrks.Is(type)) continue;
            if (_state[j] is MusouEnemyState.Knocked or MusouEnemyState.Spawning) continue;
            var away = new Vector2(_position[j].X - _position[i].X, _position[j].Y - _position[i].Y);
            away = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : Forward(_yaw[j] + MathF.PI);
            if (War is null) { Stagger(near[k], away, MusouWeirdboy.BreakKnockback); continue; }
            if (_group[j] != 0) ReleaseGroup(_group[j]);
            _scripted[j] = false;
            SetState(j, MusouEnemyState.Knocked);
            _token[j] = 0; _burst[j] = 0;
            Knock(j, away, MusouWeirdboy.BreakKnockback);
        }
    }

    /// <summary>His death's burst (from <see cref="Damage"/>): every Ork within <see cref="MusouWeirdboy.BurstRadius"/> but the Warboss and the Boss Orks dies as a blast, the KO <paramref name="attacker"/>'s (another
    /// Weirdboy's shield does not stop it, so he bursts in turn).</summary>
    void PsychicBurst(int i, int attacker)
    {
        AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, (ushort)(i + 1), (ushort)Math.Clamp(attacker, 0, ushort.MaxValue), MusouWeirdboy.BurstCue, _position[i]));
        var near = new ushort[Capacity];   // its own list: the burst calls itself again through Damage
        int n = Query(_position[i], MusouWeirdboy.BurstRadius, near, MusouWeirdboy.PsychicVerticalReach);
        for (int k = 0; k < n; k++)
        {
            int j = near[k] - 1;
            if (j == i || !_used[j] || _state[j] == MusouEnemyState.Dying || !Ork(_type[j]) || _type[j] == MusouEnemyType.Warboss || MusouBossOrks.Is(_type[j])) continue;   // the owner, 2026-11-07: spare the Boss Orks
            var away = new Vector2(_position[j].X - _position[i].X, _position[j].Y - _position[i].Y);
            away = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : Forward(_yaw[j]);
            _shield[j] = 0f;
            if (_state[j] == MusouEnemyState.Stunned) SetState(j, MusouEnemyState.Seek);
            Damage(near[k], attacker, _health[j], new Vector3(away, 0.6f), out _, marker: 0, cause: MusouDeathKind.Blast);
        }
    }

    /// <summary>
    /// One tick of a Weirdboy's own states (from <see cref="Step"/>, before its target and moves): the cooldowns, the stun, the cast and the fade. True when it was in one of his own states
    /// (it stood this tick); false for anything else, which goes on as any enemy.
    /// </summary>
    bool StepWeirdboy(int i, ReadOnlySpan<MusouTarget> targets, Span<int> near, bool fight)
    {
        if (_teleportCd[i] > 0) _teleportCd[i]--;
        if (_summonCd[i] > 0) _summonCd[i]--;
        var state = _state[i];
        if (state is not (MusouEnemyState.Stunned or MusouEnemyState.Casting or MusouEnemyState.Vanishing)) return false;
        var spec = Rules.Of(_type[i]);
        _headwayTick[i] = Tick; _anchor[i] = _position[i];
        _token[i] = 0; _burst[i] = 0;
        switch (state)
        {
            case MusouEnemyState.Stunned:
                if (_stateTicks[i] >= Ticks(MusouWeirdboy.StunSeconds))
                {
                    _shield[i] = MathF.Min(spec.ShieldMax, MusouWeirdboy.ShieldRestore);
                    SetState(i, MusouEnemyState.Seek);
                    AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, (ushort)(i + 1), 0, MusouWeirdboy.ShieldBackCue, _position[i]));
                }
                break;
            case MusouEnemyState.Casting:
                if (_stateTicks[i] >= Ticks(MusouWeirdboy.SummonSeconds))
                {
                    if (fight) Summon(i);
                    SetState(i, MusouEnemyState.Seek);
                }
                break;
            case MusouEnemyState.Vanishing:
                if (_stateTicks[i] >= Ticks(MusouWeirdboy.VanishSeconds))
                {
                    _position[i] = _blink[i]; _node[i] = _blinkNode[i]; _velocity[i] = Vector2.Zero;
                    _transit[i] = -1; _aim[i] = -1; _flowTicks[i] = 0; _blocked[i] = 0; _drive[i] = Vector2.Zero; _driveTicks[i] = 0;
                    Array.Fill(_trail, _position[i], i * TrailLength, TrailLength);
                    _anchor[i] = _position[i]; _best[i] = float.PositiveInfinity;
                    SetState(i, MusouEnemyState.Spawning);   // he appears (the client plays it from the state)
                    return true;
                }
                break;
        }
        Move(i, spec, Vector2.Zero, targets, near);
        return true;
    }

    /// <summary>
    /// A Weirdboy's choice in <see cref="MusouEnemyState.Seek"/> with a target (from <see cref="Behave"/>): away from a target that came too near, else a summons when it is due. True when he
    /// started one (he stands this tick).
    /// </summary>
    bool WeirdboyActs(int i, MusouTarget target, float distance, Vector2 toward)
    {
        if (target.Goal) return false;
        if (_teleportCd[i] == 0 && distance <= MusouWeirdboy.TeleportTrigger + target.Radius && BlinkSpot(i, target, out var at, out int node))
        {
            _blink[i] = at; _blinkNode[i] = node;
            _teleportCd[i] = Ticks(MusouWeirdboy.TeleportCooldown);
            _token[i] = 0; _burst[i] = 0;
            SetState(i, MusouEnemyState.Vanishing);
            Face(i, toward);
            AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, (ushort)(i + 1), (ushort)Math.Clamp(target.Id, 0, ushort.MaxValue), MusouWeirdboy.TeleportCue, at));
            return true;
        }
        if (_summonCd[i] == 0 && distance <= Rules.Of(_type[i]).RangedMax && _free.Count > MusouWeirdboy.SummonReserve + MusouWeirdboy.SummonCount)
        {
            _summonCd[i] = Ticks(MusouWeirdboy.SummonCooldown);
            _token[i] = 0; _burst[i] = 0;
            SetState(i, MusouEnemyState.Casting);
            Face(i, toward);
            AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, (ushort)(i + 1), 0, MusouWeirdboy.SummonCue, _position[i]));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Where a teleport takes him: <see cref="MusouWeirdboy.TeleportMin"/> to <see cref="MusouWeirdboy.TeleportMax"/> from <paramref name="target"/>, on the graph, tried first on his side of it and
    /// then turning out in eight steps; false when no try lands on the graph (he stays). Without a graph: the first try, on the target's level.
    /// </summary>
    bool BlinkSpot(int i, MusouTarget target, out Vector3 at, out int node)
    {
        var away = new Vector2(_position[i].X - target.Position.X, _position[i].Y - target.Position.Y);
        away = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : Forward(_yaw[i]);
        float r = MusouWeirdboy.TeleportMin + (MusouWeirdboy.TeleportMax - MusouWeirdboy.TeleportMin) * Unit(Mix(Tick, (uint)i * 0x27D4EB2Du + 0x5EEDu));
        ReadOnlySpan<float> turns = [0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f];
        foreach (float degrees in turns)
        {
            float a = degrees * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
            var dir = new Vector2(away.X * c - away.Y * s, away.X * s + away.Y * c);
            var spot = target.Position + new Vector3(dir * r, 0f);
            if (Walk is null) { at = spot; node = -1; return true; }
            int n = Walk.Nearest(spot, 3f, 6f);
            if (n < 0 || !Reachable(n)) continue;
            at = Walk.Position(n); node = n;
            return true;
        }
        at = default; node = -1;
        return false;
    }

    /// <summary>Whether players can be walked to from <paramref name="node"/> (the flow field's distance is finite; true while the field is not ready): a teleport never strands him.</summary>
    public bool Reachable(int node) => Flow is not { Ready: true } || !float.IsPositiveInfinity(Flow.Distance(node));

    /// <summary>The summons' Boyz, on a ring round him on the graph (a spot off it: his own feet).</summary>
    void Summon(int i)
    {
        var from = _position[i];
        float health = Rules.Of(MusouEnemyType.Boy).Health;
        for (int k = 0; k < MusouWeirdboy.SummonCount; k++)
        {
            if (_free.Count <= MusouWeirdboy.SummonReserve) break;
            float angle = _yaw[i] + k * 2.399963f;
            float radius = MusouWeirdboy.SummonRingMin + (MusouWeirdboy.SummonRingMax - MusouWeirdboy.SummonRingMin) * Unit(Mix((uint)(i * 8 + k), Tick));
            var spot = from + new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f);
            int node = -1;
            if (Walk is not null)
            {
                node = Walk.Nearest(spot, 1.5f, 2.5f);
                spot = node >= 0 ? Walk.Position(node) : from;
                if (node < 0) node = _node[i];
            }
            Spawn(MusouEnemyType.Boy, spot, _yaw[i], health, node);
        }
    }

    /// <summary>A bolt thrown (from <see cref="Fire"/>, a type with <see cref="MusouTypeSpec.RangedBlastRadius"/>): it bursts at the target's feet after its flight (none outside play).</summary>
    void ThrowBolt(int i, MusouTarget target, MusouTypeSpec spec, bool fight)
    {
        if (!fight) return;
        var point = target.Position;
        var muzzle = _position[i] + new Vector3(0f, 0f, 1.5f);
        float flight = Vector3.Distance(muzzle, point) / MathF.Max(1f, spec.RangedBoltSpeed);
        uint lands = Tick + (uint)Math.Max(1, (int)MathF.Round(flight * TickRate));
        _bolts.Add(new Bolt((ushort)(i + 1), muzzle, point, lands, spec.RangedDamage, spec.RangedBlastRadius));
        AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, (ushort)(i + 1), (ushort)Math.Clamp(target.Id, 0, ushort.MaxValue), MusouWeirdboy.BoltCue, point));
    }

    /// <summary>The bolts that land this tick (from <see cref="Step"/>): each hurts every living target within its radius (a squad by its spread too); outside play they fizzle.</summary>
    void LandBolts(ReadOnlySpan<MusouTarget> targets, bool fight, List<MusouAttack> attacks)
    {
        for (int b = _bolts.Count - 1; b >= 0; b--)
        {
            var bolt = _bolts[b];
            if (bolt.Lands > Tick) continue;
            _bolts.RemoveAt(b);
            if (!fight) continue;
            foreach (var t in targets)
            {
                if (!t.Alive || t.Goal) continue;
                var d = t.Position - bolt.Point;
                if (MathF.Abs(d.Z) > MusouWeirdboy.BoltVerticalReach) continue;
                float reach = bolt.Radius + MathF.Max(0f, t.Radius);
                if (d.X * d.X + d.Y * d.Y > reach * reach) continue;
                attacks.Add(new MusouAttack(bolt.Enemy, t.Id, bolt.Damage, false, false, bolt.From));   // from where it was thrown: the hit's direction and distance
            }
        }
    }
}
