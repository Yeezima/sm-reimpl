using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Warfront.Squad;

/// <summary>
/// Squad classes in the squad sim (slice W11; a partial of <see cref="WarfrontSquadSim"/> so it reaches the agents it steers): the retainers' armour and health regeneration from the class numbers
/// (W9 review note 8), the Assault/Raptor's jump-follow (a leap to its place when its owner is far or airborne, by a route's jump link when the walk ends at one, and a ground pound when the owner pounds),
/// and the jump-pound order (<see cref="OrderJumpPound"/>: max jump, then a ground pound round the marked point, impacts staggered, each landing validated on the walking graph, the pack's power
/// limiting every jump). Deterministic like the rest of the sim.
/// </summary>
public sealed partial class WarfrontSquadSim
{
    sealed partial class Agent
    {
        /// <summary>Jump-pack power, and seconds since the last hit (armour and health regenerate after the class's delay).</summary>
        public float Fuel, SinceHit;
        /// <summary>In flight: <see cref="From"/> to <see cref="To"/> over <see cref="FlightTotal"/> ticks, <see cref="FlightTicks"/> so far, an arc of <see cref="Apex"/> m; <see cref="Pound"/>: lands as a ground pound.</summary>
        public bool Air, Pound, PoundOnLand;
        public Vector3 From, To;
        public int FlightTicks, FlightTotal, RecoverLeft, PlanTicks;
        public float Apex, PlanApex, PlanCost;
        /// <summary>A planned pound: waits standing until <see cref="LaunchTick"/>, then launches.</summary>
        public bool Pending;
        public uint LaunchTick, WalkUntil, FiredAt;
        /// <summary>Whether the last round was a blow (the wire's melee flag): a Marine with a pistol and a blade does both.</summary>
        public bool ShotMelee;
        /// <summary>A Marine with no valid landing walks here instead.</summary>
        public Vector3? Walk;
    }

    readonly float[] _ownerZ = new float[SquadRules.MaxOwners];
    readonly bool[] _ownerSeen = new bool[SquadRules.MaxOwners], _ownerAir = new bool[SquadRules.MaxOwners], _ownerPound = new bool[SquadRules.MaxOwners];
    readonly List<SquadPoundImpact> _impacts = [];
    readonly ushort[] _blast = new ushort[SquadClassRules.BlastMax];
    // one route search per squad per second at most, its answer (the first jump link's far side, or none) kept until the owner has moved PathMoved
    readonly uint[] _routeTick = new uint[SquadRules.MaxOwners];
    readonly bool[] _routeKept = new bool[SquadRules.MaxOwners], _routeFailed = new bool[SquadRules.MaxOwners];
    readonly Vector3[] _routeOwner = new Vector3[SquadRules.MaxOwners];
    readonly Vector3?[] _routeFar = new Vector3?[SquadRules.MaxOwners];

    /// <summary>Route searches made so far (the bench and the tests count them).</summary>
    public int RouteSearches { get; private set; }

    /// <summary>The route search with jump links (optional): lets an Assault/Raptor follow by a jump link the walking path stops at.</summary>
    public ISquadJumpArena? JumpArena { get; set; }

    /// <summary>The ground pounds that went off this tick (cleared by each <see cref="Step"/>).</summary>
    public IReadOnlyList<SquadPoundImpact> PoundImpacts => _impacts;

    /// <summary>Whether Marine <paramref name="index"/> is in flight (a leap or a pound's jump).</summary>
    public bool IsAirborne(int index) => index is >= 0 and < Max && _a[index].Air;

    /// <summary>Whether Marine <paramref name="index"/>'s latest round was a blow; before any, whether its main weapon is the blade.</summary>
    public bool FiringMelee(int index) => index is >= 0 and < Max && (_a[index].FiredAt != 0 ? _a[index].ShotMelee : _a[index].Marine?.Loadout.Weapon.Melee == true);

    /// <summary>The jump-pack power Marine <paramref name="index"/> has left (0 for a Marine without a pack).</summary>
    public float FuelOf(int index) => index is >= 0 and < Max ? _a[index].Fuel : 0f;

    /// <summary>Marine <paramref name="index"/>'s armour and health now (for the wire's bars and the tests).</summary>
    public (float Health, float Armour) BodyOf(int index) => index is >= 0 and < Max ? (_a[index].Health, _a[index].Armour) : (0f, 0f);

    // ---- hooks called by the squad sim

    void ClassLand(Agent a)
    {
        a.Air = a.Pending = a.Pound = a.PoundOnLand = false; a.Walk = null; a.RecoverLeft = 0; a.SinceHit = 1e6f; a.FiredAt = 0;
        a.Fuel = a.Marine.Loadout.Jump?.MaxPower ?? 0f;
        _routeKept[a.OwnerSlot] = false; _failed[a.OwnerSlot] = _stranded[a.OwnerSlot] = false;
    }

    void ClassReset()
    {
        foreach (var a in _a) { a.Air = a.Pending = a.Pound = a.PoundOnLand = false; a.Walk = null; a.RecoverLeft = 0; }
        Array.Clear(_ownerSeen); Array.Clear(_ownerAir); Array.Clear(_ownerPound); Array.Clear(_routeKept); Array.Clear(_routeFailed); Array.Clear(_failed); Array.Clear(_stranded);
        _impacts.Clear();
    }

    /// <summary>A Marine knocked down in flight falls where it was going: the Marine lies on the floor, not in the air.</summary>
    void ClassDown(Agent a)
    {
        if (a.Air) a.Pos = a.To;
        a.Air = a.Pending = a.Pound = a.PoundOnLand = false; a.Walk = null; a.RecoverLeft = 0;
    }

    /// <summary>Once per tick: clears the impact list and reads each owner's flight (airborne, falling fast = pounding) for the follow.</summary>
    void ClassTick(ReadOnlySpan<SquadHuman> humans)
    {
        _impacts.Clear();
        float dt = Dt;
        for (int os = 0; os < SquadRules.MaxOwners; os++)
        {
            _ownerAir[os] = _ownerPound[os] = false;
            if (!_used[os] || !OwnerAt(humans, _player[os], out var p)) { _ownerSeen[os] = false; continue; }
            bool air = _arena.Ground(p, out var floor) && p.Z - floor.Z > SquadClassRules.OwnerAirHeight;
            float vz = _ownerSeen[os] ? (p.Z - _ownerZ[os]) / dt : 0f;
            _ownerAir[os] = air;
            _ownerPound[os] = air && vz < -SquadClassRules.OwnerPoundSpeed;
            _ownerZ[os] = p.Z; _ownerSeen[os] = true;
        }
    }

    static bool OwnerAt(ReadOnlySpan<SquadHuman> humans, int player, out Vector3 at)
    {
        foreach (var h in humans) if (h.Id == player && h.Alive) { at = h.Position; return true; }
        at = default; return false;
    }

    /// <summary>
    /// A Marine's class step, before its fight and walk: regeneration for every class, then (jump-pack classes) the pack's power, a flight in progress, the stillness after a pound, a planned pound's wait, the walk
    /// of a Marine with no landing, and the decision to leap after the owner. True when the Marine's tick is taken (it neither fights nor walks).
    /// </summary>
    bool ClassStep(int i, ReadOnlySpan<SquadHuman> humans, float dt)
    {
        var a = _a[i];
        var l = a.Marine.Loadout;
        a.SinceHit += dt;
        if (a.Armour < a.ArmourMax && l.ArmourRegenRate > 0f && a.SinceHit >= l.ArmourRegenDelay) a.Armour = MathF.Min(a.ArmourMax, a.Armour + l.ArmourRegenRate * dt);
        if (SquadClassRules.HealthRegen && a.Health < a.HealthMax && l.HealthRegenRate > 0f && a.SinceHit >= l.HealthRegenDelay) a.Health = MathF.Min(a.HealthMax, a.Health + l.HealthRegenRate * dt);
        if (l.Jump is not { } j) return false;
        a.Fuel = MathF.Min(j.MaxPower, a.Fuel + j.Recharge * dt);
        if (a.Air) { StepFlight(i, j); return true; }
        if (a.RecoverLeft > 0) { a.RecoverLeft--; return true; }
        if (a.Pending)
        {
            if (_tick >= a.LaunchTick) Launch(i, a.To, a.PlanApex, a.PlanTicks, a.PlanCost, true);
            return true;
        }
        if (a.Walk is { } walk)
        {
            if (Flat(walk - a.Pos) <= 1.5f || _tick > a.WalkUntil) a.Walk = null;
            else { MoveToward(a, walk, 1f, SquadRules.SprintSpeed, dt); return true; }
        }
        return TryFollowLeap(i, humans, j);
    }

    /// <summary>The flat ticks of a flight of <paramref name="distance"/> m: the time at the pack's speed, between the shortest flight and the pack's airtime, and the pound's dive on top.</summary>
    int FlightTicksOf(SquadJump j, float distance, bool pound)
    {
        float t = Math.Clamp(distance / Math.Max(1f, j.ForwardSpeed), SquadClassRules.MinFlight, Math.Max(SquadClassRules.MinFlight, j.Duration)) + (pound ? SquadClassRules.PoundDive : 0f);
        return Math.Max(1, (int)MathF.Round(t * _arena.TickRate));
    }

    void Launch(int i, Vector3 to, float apex, int ticks, float cost, bool pound)
    {
        var a = _a[i];
        a.Air = true; a.Pending = false; a.Pound = pound; a.From = a.Pos; a.To = to; a.FlightTotal = Math.Max(1, ticks); a.FlightTicks = 0; a.Apex = apex;
        a.Fuel = MathF.Max(0f, a.Fuel - cost);
        var d = to - a.Pos;
        if (Flat(d) > 0.01f) a.Yaw = MathF.Atan2(d.Y, d.X);
        // a roof over the middle of the flight (the HQ's tunnels are 8 m clear): a low arc instead of the full height
        var mid = Vector3.Lerp(a.Pos, to, 0.5f);
        if (a.Apex > SquadClassRules.RoofedApex && !_arena.HasSight(mid, mid + new Vector3(0f, 0f, a.Apex))) a.Apex = SquadClassRules.RoofedApex;
    }

    /// <summary>The ticks the view's <c>Firing</c> flag is held after a round (<see cref="SquadClassRules.FireHold"/>).</summary>
    int FireHoldTicks => Math.Max(1, (int)MathF.Ceiling(SquadClassRules.FireHold * _arena.TickRate - 1e-4f));

    void StepFlight(int i, SquadJump j)
    {
        var a = _a[i];
        if (_ownerPound[a.OwnerSlot]) a.PoundOnLand = true;   // the owner is diving: this Marine's landing is a pound too
        a.FlightTicks++;
        float u = Math.Clamp((float)a.FlightTicks / a.FlightTotal, 0f, 1f);
        if (a.FlightTicks < a.FlightTotal)
        {
            var flat = Vector3.Lerp(a.From, a.To, u);
            a.Pos = new Vector3(flat.X, flat.Y, flat.Z + 4f * a.Apex * u * (1f - u));
            return;
        }
        a.Pos = a.To; a.Air = false; a.HoldAt = a.Pos;
        if ((a.Pound || a.PoundOnLand) && j.PoundEnabled) Impact(i, j);
        a.Pound = a.PoundOnLand = false;
    }

    void Impact(int i, SquadJump j)
    {
        var a = _a[i];
        int hits = 0;
        if (j.PoundRadius > 0f)
        {
            int n = _arena.QueryEnemies(a.Pos, j.PoundRadius, _blast);
            for (int k = 0; k < n; k++)
            {
                ushort id = _blast[k];
                if (!_arena.TryEnemy(id, out var pos, out var kind)) continue;
                var away = new Vector3(pos.X - a.Pos.X, pos.Y - a.Pos.Y, 0f);
                var push = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : Vector3.Zero;
                MusouEnemyType? type = _arena.TryEnemyType(id, out var et) ? et : null;
                if (!_arena.DamageEnemy(id, j.PoundDamage * _damageScale[a.OwnerSlot] * SquadRules.DamageDealt, push, out bool killed)) continue;
                hits++;
                if (killed) Killed(i, id, kind, type); else Note(id, i);
            }
        }
        _impacts.Add(new SquadPoundImpact(a.Key, a.Slot, i, a.Pos, _tick, hits));
        a.RecoverLeft = Math.Max(0, (int)MathF.Ceiling(j.PoundRecovery * _arena.TickRate));
    }

    // ---- jump-follow

    bool TryFollowLeap(int i, ReadOnlySpan<SquadHuman> humans, SquadJump j)
    {
        var a = _a[i];
        int os = a.OwnerSlot;
        if (_order[os] != SquadOrder.Follow || a.DutyMate >= 0 || a.DutyHuman != int.MinValue) return false;
        if ((_tick + (uint)i) % SquadClassRules.JumpCheckTicks != 0) return false;
        if (j.Range <= 0f || !OwnerAt(humans, a.Owner, out var ownerPos)) return false;
        float far = Flat(ownerPos - a.Pos);
        if (!(far > SquadClassRules.JumpFollowGap || _ownerAir[os] && far > MarineRules.FollowMax)) return false;
        Vector3 floor = Snap(ownerPos);
        var home = floor + Ring(a.Slot, MarineRules.FollowStandNear + a.Slot % 3, 0f);
        // the place it wants: its own spot by the owner, else as far as the pack flies toward it
        bool found = TryLanding(i, a.Pos, home, j, out var land);
        if (!found)
        {
            var toward = home - a.Pos; float len = Flat(toward);
            found = len > 0.01f && TryLanding(i, a.Pos, a.Pos + new Vector3(toward.X, toward.Y, 0f) / len * (j.Range * 0.9f), j, out land);
        }
        // the walk stops at a jump link: leap to the first one's far side (one search a second per squad, kept until the owner moves)
        if (!found && RouteFar(os, a.Pos, ownerPos) is { } linkFar) found = TryLanding(i, a.Pos, linkFar, j, out land);
        if (!found) return false;
        float d = Flat(land - a.Pos);
        float share = Math.Clamp(d / j.Range, SquadClassRules.MinCostShare, 1f);
        float cost = j.FullCost * share;
        if (a.Fuel < cost - 1e-3f) return false;
        Launch(i, land, j.Height * share, FlightTicksOf(j, d, false), cost, false);
        return true;
    }

    /// <summary>
    /// The far side of the first jump or drop link on the route from the squad's walk to the owner, or null (no link, no route, or no <see cref="JumpArena"/>). A squad searches at most once a second, and
    /// keeps the answer (a failed search too) until the owner has moved <see cref="SquadRules.PathMoved"/> m, the rule the walking path uses.
    /// </summary>
    Vector3? RouteFar(int os, Vector3 from, Vector3 ownerPos)
    {
        if (JumpArena is null) return null;
        bool due, moved;
        if (!_routeKept[os]) { due = moved = true; }
        else if (_routeFailed[os])
        {
            // a failed search waits 3 s, or until the owner has moved 10 m (W9b)
            moved = true;
            due = (int)(_tick - _routeTick[os]) >= (int)MathF.Round(SquadRules.FailedRetrySeconds * _arena.TickRate) || Flat(ownerPos - _routeOwner[os]) >= SquadRules.FailedMoved;
        }
        else
        {
            moved = Flat(ownerPos - _routeOwner[os]) > SquadRules.PathMoved;
            due = (int)(_tick - _routeTick[os]) >= _arena.TickRate;
        }
        if (moved && due && ClaimSearch())   // one search a tick for the whole system: when this tick's is taken the answer waits for the next
        {
            _routeKept[os] = true; _routeTick[os] = _tick; _routeOwner[os] = ownerPos; _routeFar[os] = null; _routeFailed[os] = false;
            RouteSearches++;
            if (JumpArena.FindRoute(from, ownerPos) is { } route)
            {
                foreach (var step in route) if (step.Jump) { _routeFar[os] = step.Position; break; }
            }
            else _routeFailed[os] = true;
        }
        return _routeFar[os];
    }

    /// <summary>
    /// A landing for Marine <paramref name="i"/> at <paramref name="wanted"/>: the walking graph's floor there within <see cref="SquadClassRules.LandingGround"/> (so it is on the graph), in the pack's range, the
    /// line of flight clear, and not on another Marine's landing. A few nearby places are tried when the first is taken.
    /// </summary>
    bool TryLanding(int i, Vector3 from, Vector3 wanted, SquadJump j, out Vector3 landing)
    {
        for (int k = 0; k < 6; k++)
        {
            var cand = k == 0 ? wanted : wanted + new Vector3(MathF.Cos(k * 1.05f), MathF.Sin(k * 1.05f), 0f) * (k * 0.6f);
            if (!_arena.Ground(cand, out var g) || Flat(g - cand) > SquadClassRules.LandingGround) continue;
            if (Flat(g - from) > j.Range || Flat(g - from) < 1f || Taken(g, i) || !_arena.HasSight(from, g)) continue;
            landing = g;
            return true;
        }
        landing = default;
        return false;
    }

    /// <summary>Whether another Marine is flying to, or waits to pound at, a place within the landing spacing of <paramref name="g"/>.</summary>
    bool Taken(Vector3 g, int except)
    {
        for (int k = 0; k < Max; k++)
        {
            var o = _a[k];
            if (k == except || o.State != SquadState.Standing || !(o.Air || o.Pending)) continue;
            if (Flat(o.To - g) < SquadClassRules.PoundSpacing) return true;
        }
        return false;
    }

    // ---- the jump-pound order

    /// <summary>
    /// The Assault/Raptor player's order (plan section 6): every standing jump-pack Marine of <paramref name="player"/>'s squad jumps as high as it can and ground-pounds round <paramref name="mark"/>.
    /// Each Marine gets its own landing within <see cref="SquadClassRules.PoundRadius"/> of the marked point's floor, at least <see cref="SquadClassRules.PoundSpacing"/> from every other, on the walking graph,
    /// in range of its pack, with a clear line of flight, and with the pack's power for a full jump; impacts follow each other <see cref="SquadClassRules.PoundStagger"/> s apart (rounded up to ticks).
    /// A Marine without such a landing, or without the power, walks to the mark instead. Null when the player has no squad in this battle, no jump-pack Marine, or the mark is off the walking graph.
    /// </summary>
    public SquadPoundPlan? OrderJumpPound(int player, Vector3 mark)
    {
        int os = SlotOfPlayer(player);
        if (os < 0 || !_arena.Ground(mark, out var mg)) return null;
        var jumpers = new List<int>();
        for (int slot = 0; slot < SquadRules.MaxPerPlayer; slot++)
        {
            int idx = os * SquadRules.MaxPerPlayer + slot;
            var a = _a[idx];
            if (a.State == SquadState.Standing && !a.Air && a.Marine.Loadout.Jump is { PoundEnabled: true }) jumpers.Add(idx);
        }
        if (jumpers.Count == 0) return null;
        var landings = new List<Vector3>();
        var leap = new Dictionary<int, (Vector3 Land, int Ticks)>();
        for (int k = 0; k < jumpers.Count; k++)
        {
            int idx = jumpers[k];
            var a = _a[idx];
            var j = a.Marine.Loadout.Jump!;
            if (a.Fuel < j.FullCost - 1e-3f) continue;
            for (int att = 0; att < SquadClassRules.LandingAttempts; att++)
            {
                float u = ((k + 0.5f) / jumpers.Count + att * 0.37f) % 1f;
                float r = SquadClassRules.PoundRadius * MathF.Sqrt(u) * 0.95f, ang = k * 2.39996f + att * 1.1f;
                var cand = mg + new Vector3(MathF.Cos(ang) * r, MathF.Sin(ang) * r, 0f);
                if (!_arena.Ground(cand, out var g) || Flat(g - cand) > SquadClassRules.LandingGround) continue;
                if (Flat(g - mg) > SquadClassRules.PoundRadius || Flat(g - a.Pos) > j.Range || Taken(g, idx)) continue;
                bool crowded = false;
                foreach (var p in landings) if (Flat(p - g) < SquadClassRules.PoundSpacing) { crowded = true; break; }
                if (crowded || !_arena.HasSight(a.Pos, g)) continue;
                landings.Add(g);
                leap[idx] = (g, FlightTicksOf(j, Flat(g - a.Pos), true));
                break;
            }
        }
        int longest = 0;
        foreach (var (_, v) in leap) longest = Math.Max(longest, v.Ticks);
        int stagger = SquadClassRules.StaggerTicks(_arena.TickRate);
        var entries = new List<SquadPoundEntry>();
        int rank = 0, walkers = 0;
        foreach (int idx in jumpers)
        {
            var a = _a[idx];
            if (leap.TryGetValue(idx, out var v))
            {
                var j = a.Marine.Loadout.Jump!;
                uint impact = _tick + 1u + (uint)longest + (uint)(rank++ * stagger);
                a.Pending = true; a.To = v.Land; a.PlanTicks = v.Ticks; a.PlanApex = j.Height; a.PlanCost = j.FullCost; a.LaunchTick = impact - (uint)v.Ticks; a.Walk = null;
                entries.Add(new SquadPoundEntry(idx, a.Slot, v.Land, impact, true));
            }
            else
            {
                var to = mg + Ring(walkers++, 2f, 0.3f);
                a.Walk = _arena.Ground(to, out var wg) ? wg : mg; a.WalkUntil = _tick + (uint)(SquadClassRules.PoundWalkSeconds * _arena.TickRate);
                entries.Add(new SquadPoundEntry(idx, a.Slot, a.Walk.Value, 0, false));
            }
        }
        return new SquadPoundPlan(mg, entries);
    }
}
