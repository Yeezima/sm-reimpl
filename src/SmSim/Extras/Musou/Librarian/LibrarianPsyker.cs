using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>One drawn link of the chain lightning (from the Librarian's hand, or from the last enemy, to the next).</summary>
public readonly record struct LibrarianLink(Vector3 From, Vector3 To);

/// <summary>Where the Librarian stands and looks this tick (the sim's view of the player).</summary>
public readonly record struct LibrarianCaster(int PlayerId, Vector3 Feet, Vector3 Eye, float Yaw, float Pitch);

/// <summary>What a Librarian pressed this tick (<see cref="LibrarianRules"/> names the binding).</summary>
public readonly record struct LibrarianPress(bool Lightning, bool Pull, bool Shockwave);

/// <summary>
/// One Librarian's psychic powers and Psychic Stress (Musou Mode only; <see cref="LibrarianRules"/>), server-authoritative. It acts on a <see cref="MusouSim"/> through its public damage, stagger, knock-down and
/// drive calls, so it knows nothing of the World: <see cref="World"/> feeds it the player's input each tick (<c>World.Librarian.cs</c>) and tests drive it on a bare pool.
/// <list type="bullet">
/// <item>Stress 0 to 100, shared by all three powers. At 100 everything locks until it is back at 0.</item>
/// <item>Chain lightning adds 5 a second while held, a Pull and a Shockwave 50 each; it cools 12.5 a second once a second has passed without a cast; a melee swing that strikes takes 6 off.</item>
/// </list>
/// </summary>
public sealed class LibrarianPsyker
{
    const float LockEpsilon = 0.01f;

    /// <summary>Psychic Stress now, 0 to <see cref="LibrarianRules.StressMax"/>.</summary>
    public float Stress { get; private set; }
    /// <summary>Whether every power is locked (stress reached the top and has not yet fallen to 0).</summary>
    public bool Locked { get; private set; }
    /// <summary>Seconds without a cast.</summary>
    public float SinceCast { get; private set; }
    /// <summary>Seconds left on each cooldown.</summary>
    public float PullCooldown { get; private set; }
    public float ShockwaveCooldown { get; private set; }
    /// <summary>Counts of Pulls and Shockwaves cast (wrapping bytes: the clients play an effect when they change).</summary>
    public byte PullCount { get; private set; }
    public byte ShockwaveCount { get; private set; }
    /// <summary>Whether lightning was cast this tick, and the links of it (at most <see cref="LibrarianRules.MaxDrawnLinks"/>; empty when not casting).</summary>
    public bool Casting { get; private set; }
    public IReadOnlyList<LibrarianLink> Links => _links;
    /// <summary>How many enemies the last tick's lightning struck (at most <see cref="LibrarianRules.MaxTargets"/>).</summary>
    public int LastStruck { get; private set; }

    readonly List<LibrarianLink> _links = new();
    readonly ushort[] _near = new ushort[96];
    readonly HashSet<ushort> _struck = new();
    readonly ushort[] _scan = new ushort[2048];
    bool _prevPull, _prevShockwave;

    /// <summary>A new life (or a leave): everything back to rest.</summary>
    public void Reset()
    {
        Stress = 0f; Locked = false; SinceCast = 0f; PullCooldown = ShockwaveCooldown = 0f;
        Casting = false; _links.Clear(); LastStruck = 0; _prevPull = _prevShockwave = false;
    }

    /// <summary>A melee swing struck at least one enemy: stress falls by <see cref="LibrarianRules.MeleeRelief"/> (an unlock at 0 follows).</summary>
    public void MeleeHit()
    {
        Stress = MathF.Max(0f, Stress - LibrarianRules.MeleeRelief);
        if (Locked && Stress <= 0f) Locked = false;
    }

    /// <summary>
    /// One tick: <paramref name="dt"/> seconds, the presses (a Pull and a Shockwave act on the press edge), the damage credited to <paramref name="caster"/>. <paramref name="blocked"/> says whether level geometry lies between two
    /// points (null: nothing blocks). The order of a tick: cooldowns run, the powers cast (Pull, Shockwave, lightning), stress rises or cools, the lock sets or clears.
    /// </summary>
    public void Step(MusouSim sim, in LibrarianCaster caster, LibrarianPress press, float dt, Func<Vector3, Vector3, bool>? blocked = null)
    {
        PullCooldown = MathF.Max(0f, PullCooldown - dt);
        ShockwaveCooldown = MathF.Max(0f, ShockwaveCooldown - dt);
        _links.Clear();
        Casting = false;
        LastStruck = 0;
        bool pullEdge = press.Pull && !_prevPull, shockEdge = press.Shockwave && !_prevShockwave;
        _prevPull = press.Pull; _prevShockwave = press.Shockwave;
        bool cast = false;

        if (!Locked && pullEdge && PullCooldown <= 0f)
        {
            Pull(sim, caster);
            PullCooldown = LibrarianRules.PullCooldown;
            PullCount++;
            Stress += LibrarianRules.PullStress;
            cast = true;
        }
        if (!Locked && !(cast && Stress >= LibrarianRules.StressMax - LockEpsilon) && shockEdge && ShockwaveCooldown <= 0f)
        {
            Shockwave(sim, caster);
            ShockwaveCooldown = LibrarianRules.ShockwaveCooldown;
            ShockwaveCount++;
            Stress += LibrarianRules.ShockwaveStress;
            cast = true;
        }
        if (!Locked && press.Lightning && Stress < LibrarianRules.StressMax - LockEpsilon)
        {
            Lightning(sim, caster, dt, blocked);
            Casting = true;
            Stress += LibrarianRules.LightningStressPerSecond * dt;
            cast = true;
        }

        if (cast) SinceCast = 0f;
        else
        {
            SinceCast += dt;
            if (SinceCast >= LibrarianRules.CoolDelay) Stress -= LibrarianRules.CoolPerSecond * dt;
        }
        if (Stress >= LibrarianRules.StressMax - LockEpsilon) { Stress = LibrarianRules.StressMax; Locked = true; }
        else if (Stress <= 0f) { Stress = 0f; Locked = false; }
    }

    // ------------------------------------------------------------------ chain lightning

    void Lightning(MusouSim sim, in LibrarianCaster c, float dt, Func<Vector3, Vector3, bool>? blocked)
    {
        var aim = Aim(c.Yaw, c.Pitch);
        _struck.Clear();
        int primary = PickPrimary(sim, c, aim, blocked);
        if (primary < 0)
        {
            _links.Add(new LibrarianLink(Hand(c), c.Eye + aim * LibrarianRules.LightningRange));   // nothing in the sights: the bolt crackles out and ends
            return;
        }
        ushort id = (ushort)primary;
        var from = Hand(c);
        float damage = LibrarianRules.PrimaryDps * dt;
        for (int k = 0; k < LibrarianRules.MaxTargets; k++)
        {
            if (!sim.TryGet(id, out var e)) break;
            var chest = MusouSim.Chest(e.Position, sim.Body(e.Type).Height);
            if (_links.Count < LibrarianRules.MaxDrawnLinks) _links.Add(new LibrarianLink(from, chest));
            _struck.Add(id);
            var push = new Vector3(Flat(e.Position - c.Feet), 0.2f);
            sim.Damage(id, c.PlayerId, damage, push, out _, marker: 1, cause: MusouDeathKind.Blast);   // a blast so the Weirdboy's shield wears down (a shot would be stopped whole)
            LastStruck++;
            damage *= LibrarianRules.JumpFalloff;
            from = chest;
            int next = NextJump(sim, e.Position);
            if (next < 0) break;
            id = (ushort)next;
        }
    }

    /// <summary>The enemy best lined up with the aim within the aim cone and range, in sight; -1 for none.</summary>
    int PickPrimary(MusouSim sim, in LibrarianCaster c, Vector3 aim, Func<Vector3, Vector3, bool>? blocked)
    {
        int n = sim.Query(c.Feet, LibrarianRules.LightningRange, _scan, 8f);
        float minCos = MathF.Cos(LibrarianRules.AimConeDegrees * MathF.PI / 180f), best = minCos;
        int pick = -1;
        for (int k = 0; k < n; k++)
        {
            if (!sim.TryGet(_scan[k], out var e) || !e.Alive) continue;
            var chest = MusouSim.Chest(e.Position, sim.Body(e.Type).Height);
            var to = chest - c.Eye;
            float len = to.Length();
            if (len > LibrarianRules.LightningRange + 1f) continue;
            float cos = len < 1e-3f ? 1f : Vector3.Dot(to / len, aim);
            if (pick < 0 ? cos <= best : cos <= best + 0.0005f) continue;   // the nearest of the well-lined-up ones, unless another is clearly truer
            if (blocked is not null && blocked(c.Eye, chest)) continue;
            best = cos; pick = e.Id;
        }
        return pick;
    }

    /// <summary>The nearest enemy not yet struck within the jump radius of <paramref name="at"/>; -1 for none.</summary>
    int NextJump(MusouSim sim, Vector3 at)
    {
        int n = sim.Query(at, LibrarianRules.JumpRadius, _near, LibrarianRules.VerticalReach);
        for (int k = 0; k < n; k++)
            if (!_struck.Contains(_near[k]) && sim.TryGet(_near[k], out var e) && e.Alive) return _near[k];
        return -1;
    }

    // ------------------------------------------------------------------ warp pull

    void Pull(MusouSim sim, in LibrarianCaster c)
    {
        var forward = new Vector2(-MathF.Sin(c.Yaw), MathF.Cos(c.Yaw));
        float minCos = MathF.Cos(LibrarianRules.PullConeDegrees * MathF.PI / 180f);
        int n = sim.Query(c.Feet, LibrarianRules.PullRange, _scan, LibrarianRules.VerticalReach);
        int ticks = Math.Max(1, (int)MathF.Round(LibrarianRules.PullSeconds * sim.TickRate));
        float seconds = ticks / (float)Math.Max(1, sim.TickRate);
        for (int k = 0; k < n; k++)
        {
            if (!sim.TryGet(_scan[k], out var e) || !e.Alive) continue;
            var tier = LibrarianRules.TierOf(e.Type);
            if (tier == LibrarianRules.Tier.Boss) continue;
            var flat = new Vector2(e.Position.X - c.Feet.X, e.Position.Y - c.Feet.Y);
            float distance = flat.Length();
            if (distance > 1e-4f && Vector2.Dot(flat / distance, forward) < minCos) continue;
            float travel = tier == LibrarianRules.Tier.Elite ? distance * LibrarianRules.ElitePullShare : distance - LibrarianRules.PullStopDistance;
            if (!(travel > 0.05f) || distance < 1e-4f) continue;   // already close: not pushed back out
            var toward = -flat / distance;
            sim.Stagger(e.Id, toward, 0f);   // dragged off his feet; a stagger with no push of its own
            sim.Drive(e.Id, toward * (travel / seconds), ticks);
        }
    }

    // ------------------------------------------------------------------ psychic shockwave

    void Shockwave(MusouSim sim, in LibrarianCaster c)
    {
        int n = sim.Query(c.Feet, LibrarianRules.ShockwaveRadius, _scan, LibrarianRules.VerticalReach);
        int ticks = Math.Max(1, (int)MathF.Round(LibrarianRules.ThrowSeconds * sim.TickRate));
        float seconds = ticks / (float)Math.Max(1, sim.TickRate);
        for (int k = 0; k < n; k++)
        {
            ushort id = _scan[k];
            if (!sim.TryGet(id, out var e) || !e.Alive) continue;
            var flat = new Vector2(e.Position.X - c.Feet.X, e.Position.Y - c.Feet.Y);
            var away = flat.LengthSquared() > 1e-6f ? Vector2.Normalize(flat) : new Vector2(-MathF.Sin(c.Yaw), MathF.Cos(c.Yaw));
            bool killed = false;
            sim.Damage(id, c.PlayerId, LibrarianRules.ShockwaveDamage, new Vector3(away, 0.5f), out killed, marker: 1, cause: MusouDeathKind.Blast);
            if (killed || LibrarianRules.TierOf(e.Type) != LibrarianRules.Tier.Ordinary) continue;
            float distance = LibrarianRules.ThrowMin + (LibrarianRules.ThrowMax - LibrarianRules.ThrowMin) * Roll(sim.Tick, id);
            if (!sim.KnockDown(id, away, 0.1f, c.PlayerId, 0f)) sim.Stagger(id, away, 0f);   // a war's fodder falls; any other ordinary one staggers
            sim.Drive(id, away * (distance / seconds), ticks);
        }
    }

    /// <summary>A repeatable roll in [0, 1) from the tick and the enemy.</summary>
    static float Roll(uint tick, ushort id)
    {
        uint x = tick * 0x9E3779B1u ^ (uint)id * 0x85EBCA6Bu;
        x ^= x >> 15; x *= 0x2C1B3C6Du; x ^= x >> 12; x *= 0x297A2D39u; x ^= x >> 15;
        return (x & 0xFFFFFFu) / 16777216f;
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>The view direction (yaw about Z, 0 looking along +Y, positive turning left as the cleave's; pitch up positive).</summary>
    public static Vector3 Aim(float yaw, float pitch)
    {
        float cp = MathF.Cos(pitch);
        return new Vector3(-MathF.Sin(yaw) * cp, MathF.Cos(yaw) * cp, MathF.Sin(pitch));
    }

    /// <summary>Where the lightning leaves: a hand's height below the eye, a little ahead (ours, cosmetic).</summary>
    static Vector3 Hand(in LibrarianCaster c) => c.Eye + Aim(c.Yaw, 0f) * 0.5f - new Vector3(0f, 0f, 0.35f);

    static Vector2 Flat(Vector3 v)
    {
        var f = new Vector2(v.X, v.Y);
        return f.LengthSquared() > 1e-6f ? Vector2.Normalize(f) : Vector2.Zero;
    }
}
