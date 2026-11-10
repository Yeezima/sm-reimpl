using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>One drawn link of the chain lightning (from the Librarian's hand, or from the last enemy, to the next).</summary>
public readonly record struct LibrarianLink(Vector3 From, Vector3 To);

/// <summary>Where the Librarian stands and looks this tick (the sim's view of the player).</summary>
public readonly record struct LibrarianCaster(int PlayerId, Vector3 Feet, Vector3 Eye, float Yaw, float Pitch);

/// <summary>What a Librarian pressed this tick (<see cref="LibrarianRules"/> names the binding).</summary>
/// <summary>What a Librarian pressed this tick; <paramref name="Pull"/> only with the restore switch <c>pull</c> (<see cref="LibrarianVariant"/>).</summary>
public readonly record struct LibrarianPress(bool Lightning, bool Shockwave, bool Pull = false);

/// <summary>
/// One Librarian's psychic powers and Psychic Stress (Musou Mode only; <see cref="LibrarianRules"/>), server-authoritative. It acts on a <see cref="MusouSim"/> through its public damage, stagger, knock-down and
/// drive calls, so it knows nothing of the World: <see cref="World"/> feeds it the player's input each tick (<c>World.Librarian.cs</c>) and tests drive it on a bare pool.
/// <list type="bullet">
/// <item>Stress 0 to 100, shared by both powers. At 100 everything locks until it is back at 0.</item>
/// <item>Chain lightning adds 5 a second while held, a Shockwave 50; it cools 12.5 a second once a second has passed without a cast; a melee swing that strikes takes 6 off.</item>
/// <item>The Warp Pull was removed (the owner, 2026-11-10): the Grenade key throws his equipment, as any marine's.</item>
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
    /// <summary>Seconds left on the Shockwave's cooldown (and the Warp Pull's, with the restore switch <c>pull</c> only; else 0).</summary>
    public float ShockwaveCooldown { get; private set; }
    public float PullCooldown { get; private set; }
    /// <summary>Shockwaves and Warp Pulls cast (wrapping bytes: the clients play the effect when one changes; Pulls only with the restore switch).</summary>
    public byte ShockwaveCount { get; private set; }
    public byte PullCount { get; private set; }
    /// <summary>Whether lightning was cast this tick, and the links of it (at most <see cref="LibrarianRules.MaxDrawnLinks"/>; empty when not casting).</summary>
    public bool Casting { get; private set; }
    public IReadOnlyList<LibrarianLink> Links => _links;
    /// <summary>How many enemies the last tick's lightning struck (at most <see cref="LibrarianRules.MaxTargets"/>).</summary>
    public int LastStruck { get; private set; }

    readonly List<LibrarianLink> _links = new();
    readonly ushort[] _scan = new ushort[MusouSim.Capacity];   // a 60 m Shockwave may take the whole pool; no larger than Query's scratch (MusouSim._queryDistance), or Query allocates
    // The chain lightning's buffers (musou-balance 2026-11-11: the chain is LibrarianChain's, on the caster's sight or the pool now): no allocation per tick.
    readonly LibrarianCandidate[] _present = new LibrarianCandidate[MusouSim.Capacity];
    readonly bool[] _chained = new bool[MusouSim.Capacity];
    readonly int[] _chain = new int[LibrarianRules.MaxTargets];
    readonly List<ushort> _chainIds = new(LibrarianRules.MaxTargets);
    readonly LibrarianChain.Scratch _chainScratch = new();

    /// <summary>The enemies of this tick's chain lightning in chain order (the primary first; empty when not casting or nothing was in the sights), alive now or not: the tests and the logs.</summary>
    public IReadOnlyList<ushort> Chained => _chainIds;
    bool _prevShockwave, _prevPull;

    /// <summary>A new life (or a leave): everything back to rest.</summary>
    public void Reset()
    {
        Stress = 0f; Locked = false; SinceCast = 0f; ShockwaveCooldown = PullCooldown = 0f;
        Casting = false; _links.Clear(); _chainIds.Clear(); LastStruck = 0; _prevShockwave = _prevPull = false;
    }

    /// <summary>A melee swing struck at least one enemy: stress falls by <see cref="LibrarianRules.MeleeRelief"/> (an unlock at 0 follows).</summary>
    public void MeleeHit()
    {
        Stress = MathF.Max(0f, Stress - LibrarianRules.MeleeRelief);
        if (Locked && Stress <= 0f) Locked = false;
    }

    /// <summary>
    /// One tick: <paramref name="dt"/> seconds, the presses (a Shockwave acts on the press edge), the damage credited to <paramref name="caster"/>. <paramref name="blocked"/> says whether level geometry lies between two
    /// points (null: nothing blocks). The order of a tick: the cooldown runs, the powers cast (Shockwave, lightning), stress rises or cools, the lock sets or clears.
    /// <paramref name="sight"/>: the enemies as the caster's client drew them (the World's rewind to his input's view tick, <c>World.Librarian.cs</c>); the chain lightning is picked on them and strikes each
    /// of them alive now. Null: on the pool as it is now.
    /// </summary>
    public void Step(MusouSim sim, in LibrarianCaster caster, LibrarianPress press, float dt, Func<Vector3, Vector3, bool>? blocked = null, LibrarianVariant variant = default, LibrarianSight? sight = null)
    {
        ShockwaveCooldown = MathF.Max(0f, ShockwaveCooldown - dt);
        PullCooldown = MathF.Max(0f, PullCooldown - dt);
        _links.Clear();
        _chainIds.Clear();
        Casting = false;
        LastStruck = 0;
        bool shockEdge = press.Shockwave && !_prevShockwave, pullEdge = variant.WarpPull && press.Pull && !_prevPull;
        _prevShockwave = press.Shockwave; _prevPull = press.Pull;
        bool cast = false;

        if (!Locked && pullEdge && PullCooldown <= 0f)   // the restore switch pull only (pass 2)
        {
            Pull(sim, caster);
            PullCooldown = LibrarianRules.Pass2.PullCooldown;
            PullCount++;
            Stress += LibrarianRules.Pass2.PullStress;
            cast = true;
        }
        if (!Locked && !(cast && Stress >= LibrarianRules.StressMax - LockEpsilon) && shockEdge && ShockwaveCooldown <= 0f)
        {
            if (variant.OldShockwave) ShockwavePass2(sim, caster); else Shockwave(sim, caster);
            ShockwaveCooldown = LibrarianRules.ShockwaveCooldown;
            ShockwaveCount++;
            Stress += LibrarianRules.ShockwaveStress;
            cast = true;
        }
        if (!Locked && press.Lightning && Stress < LibrarianRules.StressMax - LockEpsilon)
        {
            Lightning(sim, caster, dt, blocked, sight);
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

    /// <summary>
    /// The chain (<see cref="LibrarianChain"/>, the client's foresight's own rule) on <paramref name="sight"/>, the enemies as the caster's client drew them, or on the pool now; then each enemy of the chain that is
    /// alive now takes its share (the first <see cref="LibrarianRules.PrimaryDps"/> a second, each jump <see cref="LibrarianRules.JumpFalloff"/> of the one before). One the caster saw alive that has died since
    /// stays a link of the chain (the jump goes on from it, as drawn) and takes nothing. The links are where the caster saw them.
    /// </summary>
    void Lightning(MusouSim sim, in LibrarianCaster c, float dt, Func<Vector3, Vector3, bool>? blocked, LibrarianSight? sight)
    {
        ReadOnlySpan<LibrarianCandidate> candidates = sight is not null ? sight.Candidates : Present(sim);
        int count = LibrarianChain.Run(c.Feet, c.Eye, c.Yaw, c.Pitch, candidates, blocked, _links, _chained, _chain, _chainScratch);   // nothing in the sights: the one link that crackles out
        float damage = LibrarianRules.PrimaryDps * dt;
        for (int k = 0; k < count; k++) _chainIds.Add(candidates[_chain[k]].Id);
        for (int k = 0; k < count; k++, damage *= LibrarianRules.JumpFalloff)
        {
            ushort id = _chainIds[k];
            if (!sim.AliveNow(id) || (sight is not null && !sight.Same(_chain[k], sim))) continue;   // died since he saw it (or its slot is another enemy's now): a link, no damage
            var push = new Vector3(Flat(sim.FeetNow(id) - c.Feet), 0.2f);
            if (sim.Damage(id, c.PlayerId, damage, push, out _, marker: 1, cause: MusouDeathKind.Blast)) LastStruck++;   // a blast so the Weirdboy's shield wears down (a shot would be stopped whole)
        }
    }

    /// <summary>Every enemy alive in the pool now, as chain candidates (into the psyker's own buffer).</summary>
    ReadOnlySpan<LibrarianCandidate> Present(MusouSim sim)
    {
        int n = 0;
        for (int i = 0; i < MusouSim.Capacity; i++)
        {
            ushort id = (ushort)(i + 1);
            if (!sim.AliveNow(id) || !sim.TryGet(id, out var e)) continue;
            _present[n++] = new LibrarianCandidate(id, e.Position, sim.Body(e.Type).Height);
        }
        return _present.AsSpan(0, n);
    }

    // ------------------------------------------------------------------ psychic shockwave

    void Shockwave(MusouSim sim, in LibrarianCaster c)
    {
        int n = sim.Query(c.Feet, LibrarianRules.ShockwaveRadius, _scan, LibrarianRules.ShockwaveVerticalReach);
        int ticks = Math.Max(1, (int)MathF.Round(LibrarianRules.ThrowSeconds * sim.TickRate));
        float seconds = ticks / (float)Math.Max(1, sim.TickRate);
        for (int k = 0; k < n; k++)
        {
            ushort id = _scan[k];
            if (!sim.TryGet(id, out var e) || !e.Alive) continue;
            var flat = new Vector2(e.Position.X - c.Feet.X, e.Position.Y - c.Feet.Y);
            var away = flat.LengthSquared() > 1e-6f ? Vector2.Normalize(flat) : new Vector2(-MathF.Sin(c.Yaw), MathF.Cos(c.Yaw));
            sim.Damage(id, c.PlayerId, LibrarianRules.ShockwaveDamage, new Vector3(away, 0.5f), out bool killed, marker: 1, cause: MusouDeathKind.Blast);
            if (killed) continue;
            var tier = LibrarianRules.TierOf(e.Type);
            if (tier == LibrarianRules.Tier.Boss) { sim.Stagger(id, away, 0f); continue; }   // staggered where he stands (false for the stagger-immune Warboss and Boss Orks)
            float roll = Roll(sim.Tick, id);
            float distance = tier == LibrarianRules.Tier.Elite
                ? LibrarianRules.EliteThrowMin + (LibrarianRules.EliteThrowMax - LibrarianRules.EliteThrowMin) * roll
                : LibrarianRules.ThrowMin + (LibrarianRules.ThrowMax - LibrarianRules.ThrowMin) * roll;
            if (tier == LibrarianRules.Tier.Elite || !sim.KnockDown(id, away, 0.1f, c.PlayerId, 0f)) sim.Stagger(id, away, 0f);   // a war's fodder falls; any other one staggers
            sim.Drive(id, away * (distance / seconds), ticks);
        }
    }

    /// <summary>The pass 2 Shockwave (restore switch <c>shockwave</c>): 9 m, 40 damage, ordinary Orks thrown 20 to 25 m over 1.0 s, elites and bosses not moved.</summary>
    void ShockwavePass2(MusouSim sim, in LibrarianCaster c)
    {
        int n = sim.Query(c.Feet, LibrarianRules.Pass2.ShockwaveRadius, _scan, LibrarianRules.Pass2.ShockwaveVerticalReach);
        int ticks = Math.Max(1, (int)MathF.Round(LibrarianRules.Pass2.ThrowSeconds * sim.TickRate));
        float seconds = ticks / (float)Math.Max(1, sim.TickRate);
        for (int k = 0; k < n; k++)
        {
            ushort id = _scan[k];
            if (!sim.TryGet(id, out var e) || !e.Alive) continue;
            var flat = new Vector2(e.Position.X - c.Feet.X, e.Position.Y - c.Feet.Y);
            var away = flat.LengthSquared() > 1e-6f ? Vector2.Normalize(flat) : new Vector2(-MathF.Sin(c.Yaw), MathF.Cos(c.Yaw));
            sim.Damage(id, c.PlayerId, LibrarianRules.Pass2.ShockwaveDamage, new Vector3(away, 0.5f), out bool killed, marker: 1, cause: MusouDeathKind.Blast);
            if (killed || LibrarianRules.TierOf(e.Type) != LibrarianRules.Tier.Ordinary) continue;
            float distance = LibrarianRules.Pass2.ThrowMin + (LibrarianRules.Pass2.ThrowMax - LibrarianRules.Pass2.ThrowMin) * Roll(sim.Tick, id);
            if (!sim.KnockDown(id, away, 0.1f, c.PlayerId, 0f)) sim.Stagger(id, away, 0f);
            sim.Drive(id, away * (distance / seconds), ticks);
        }
    }

    // ------------------------------------------------------------------ warp pull (restore switch pull: pass 2)

    void Pull(MusouSim sim, in LibrarianCaster c)
    {
        var forward = new Vector2(-MathF.Sin(c.Yaw), MathF.Cos(c.Yaw));
        float minCos = MathF.Cos(LibrarianRules.Pass2.PullConeDegrees * MathF.PI / 180f);
        int n = sim.Query(c.Feet, LibrarianRules.Pass2.PullRange, _scan, LibrarianRules.VerticalReach);
        int ticks = Math.Max(1, (int)MathF.Round(LibrarianRules.Pass2.PullSeconds * sim.TickRate));
        float seconds = ticks / (float)Math.Max(1, sim.TickRate);
        for (int k = 0; k < n; k++)
        {
            if (!sim.TryGet(_scan[k], out var e) || !e.Alive) continue;
            var tier = LibrarianRules.TierOf(e.Type);
            if (tier == LibrarianRules.Tier.Boss) continue;
            var flat = new Vector2(e.Position.X - c.Feet.X, e.Position.Y - c.Feet.Y);
            float distance = flat.Length();
            if (distance > 1e-4f && Vector2.Dot(flat / distance, forward) < minCos) continue;
            float travel = tier == LibrarianRules.Tier.Elite ? distance * LibrarianRules.Pass2.ElitePullShare : distance - LibrarianRules.Pass2.PullStopDistance;
            if (!(travel > 0.05f) || distance < 1e-4f) continue;   // already close: not pushed back out
            var toward = -flat / distance;
            sim.Stagger(e.Id, toward, 0f);   // dragged off his feet; a stagger with no push of its own
            sim.Drive(e.Id, toward * (travel / seconds), ticks);
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

    static Vector2 Flat(Vector3 v)
    {
        var f = new Vector2(v.X, v.Y);
        return f.LengthSquared() > 1e-6f ? Vector2.Normalize(f) : Vector2.Zero;
    }
}
