using System;
using System.Collections.Generic;
using System.Numerics;
using SmSim;

namespace SmClient.ImpactRules;

/// <summary>
/// classic-hit-effects (brief 2026-11-23-series-36-classic-hit-effects rev 1; owner answer 2026-11-23, question card: "Extended only"; client presentation only, no wire, protocol or
/// server change). The engine-free rules of the directional hit look on players in Classic PvP (Annihilation and Seize Ground; never Musou Mode or Warfront):
/// <list type="bullet">
/// <item>What a confirmed damage shows (<see cref="Classify"/>): armour only (the last armour point broken without health loss included): yellow/white sparks; health lost with armour left
/// (a weapon whose armour multiplier is 0): blood and sparks; health lost on bare armour, or armour broken and health hit by the same blow: blood.</item>
/// <item>Who draws what (<see cref="LookFor"/>): the directional melee sparks in both Classic profiles; the red directional blood spray (and its heavier and lethal amounts) and the fatal
/// gunfire headshot wall decal only with the gore content (<see cref="ContentExtras.Gore"/>, part of the Extended profile). Faithful keeps today's red health-hit sparks and glow.</item>
/// <item>The spray's direction comes from the real weapon motion through the hit (<see cref="WeaponDirection"/>) or the incoming shot (<see cref="ShotDirection"/>), never the camera.</item>
/// <item>A remote victim's melee damage carries no attacker on the wire: <see cref="PickAttacker"/> pairs the confirmed damage mark with the enemy whose melee attack just struck in reach.</item>
/// </list>
/// Every number here is ours (kind: ours; spec/behavior.md "Classic hit effects"). <c>--old-classic-hit-dir</c> restores the look of d090566a.
/// </summary>
public static class ClassicHitDirRules
{
    /// <summary>The client flag that restores the look before the directional hit effects (no directional sparks, spray or wall decal).</summary>
    public const string OldLookFlag = "old-classic-hit-dir";

    /// <summary>The visual class of one confirmed damage.</summary>
    public enum HitVisual { None, Sparks, Mixed, Blood }

    /// <summary>The parts of the directional look a match draws.</summary>
    [Flags]
    public enum DirLook { None = 0, MeleeSparks = 1, BloodSpray = 2, HeadshotDecal = 4 }

    /// <summary>
    /// What a match draws (owner 2026-11-23, question card "Extended only"): nothing outside Classic PvP or with <paramref name="oldLook"/>; the directional melee sparks in both Classic profiles;
    /// the blood spray and the headshot decal only with <see cref="ContentExtras.Gore"/> (the Extended profile's blood).
    /// </summary>
    public static DirLook LookFor(ContentExtras extras, bool oldLook)
    {
        if (oldLook || !PlayerHitFxRules.Classic(extras)) return DirLook.None;
        var look = DirLook.MeleeSparks;
        if (extras.HasFlag(ContentExtras.Gore)) look |= DirLook.BloodSpray | DirLook.HeadshotDecal;
        return look;
    }

    /// <summary>
    /// The class of a confirmed damage from what it took: <paramref name="shieldTaken"/>, <paramref name="healthTaken"/>, and whether armour was left after it
    /// (<paramref name="shieldLeftAfter"/>). No damage: none.
    /// </summary>
    public static HitVisual Classify(bool shieldTaken, bool healthTaken, bool shieldLeftAfter)
    {
        if (!healthTaken) return shieldTaken ? HitVisual.Sparks : HitVisual.None;
        return shieldLeftAfter ? HitVisual.Mixed : HitVisual.Blood;
    }

    /// <summary>
    /// The armour left after a damage mark's tick, where the record carries no shield (another player): <c>Combat.ApplyDamage</c> spills into health only once the shield is spent, so shield
    /// and health together is a break; health alone leaves the shield as it was before (<paramref name="armourUpBefore"/>: the shot's armour bit, the own hit report, else the client's armour
    /// tracker, approximate for a remote melee hit); shield alone does not matter (sparks either way).
    /// </summary>
    public static bool ShieldLeftAfterMark(bool shield, bool health, bool armourUpBefore) => !(shield && health) && health && armourUpBefore;

    /// <summary>Whether a class draws blood, sparks.</summary>
    public static bool HasBlood(HitVisual v) => v is HitVisual.Mixed or HitVisual.Blood;
    public static bool HasSparks(HitVisual v) => v is HitVisual.Sparks or HitVisual.Mixed;

    /// <summary>The blood amount: 1 normal, heavier for a heavy attack, most for a lethal hit (ours).</summary>
    public const float HeavyAmount = 1.5f, LethalAmount = 2.1f;
    public static float BloodAmount(bool heavy, bool lethal) => lethal ? LethalAmount : heavy ? HeavyAmount : 1f;

    /// <summary>Streaks in one spray (the mesh's quads) and how many a normal hit shows.</summary>
    public const int MaxStreaks = 64, NormalStreaks = 26, SparkStreaks = 22;
    public static int StreakCount(float amount) => Math.Clamp((int)MathF.Round(NormalStreaks * amount), 1, MaxStreaks);

    /// <summary>The spray's life (s): the emission window behind the weapon, then each streak's flight and fade. The reference clip's plume is mostly gone about 0.8 s after contact.</summary>
    public const float EmitSeconds = 0.12f, StreakSeconds = 0.85f, SparkSeconds = 0.32f;
    public static float SprayLife(bool sparks) => EmitSeconds + (sparks ? SparkSeconds : StreakSeconds);

    /// <summary>The sideways spread (radians at the end of the emission) of a pure sweep and of a pure thrust: thrusts stay narrow, sweeps bloom wider.</summary>
    public const float SweepSpread = 0.42f, ThrustSpread = 0.14f;

    /// <summary>The spread for a motion: <paramref name="thrustness"/> 1 when the weapon moves along the attacker's facing (a stab), 0 across it (a sweep).</summary>
    public static float SpreadFor(float thrustness) => SweepSpread + (ThrustSpread - SweepSpread) * Math.Clamp(thrustness, 0f, 1f);

    /// <summary>How much a weapon motion is a thrust: the share of its direction along the attacker's horizontal facing (yaw, radians, game space: facing (-sin, cos)).</summary>
    public static float Thrustness(Vector3 motion, float attackerYaw)
    {
        if (motion.LengthSquared() < 1e-8f) return 0f;
        var f = new Vector3(-MathF.Sin(attackerYaw), MathF.Cos(attackerYaw), 0f);
        return MathF.Abs(Vector3.Dot(Vector3.Normalize(motion), f));
    }

    /// <summary>A timed point of a weapon's path (game or world space, as the caller keeps it).</summary>
    public readonly record struct PathPoint(double Time, Vector3 Position);

    /// <summary>The fastest a weapon point is read to move (m/s); a faster jump is a pose pop or a teleport, not a swing (ours).</summary>
    public const float MaxWeaponSpeed = 40f;

    /// <summary>
    /// The weapon's direction of motion at <paramref name="time"/> from its recorded path (oldest first): the difference of the samples around it, over at least <paramref name="span"/> seconds;
    /// null when the path is too short, the weapon did not move or moved implausibly fast.
    /// </summary>
    public static Vector3? WeaponDirection(IReadOnlyList<PathPoint> path, double time, double span = 0.03)
    {
        if (path.Count < 2) return null;
        int i = 1;
        while (i < path.Count - 1 && path[i].Time < time) i++;
        int a = i - 1, b = i;
        while (a > 0 && path[b].Time - path[a].Time < span) a--;
        while (b < path.Count - 1 && path[b].Time - path[a].Time < span) b++;
        double dt = path[b].Time - path[a].Time;
        if (dt <= 1e-6) return null;
        var d = path[b].Position - path[a].Position;
        float speed = d.Length() / (float)dt;
        if (speed < 0.3f || speed > MaxWeaponSpeed) return null;
        return d / d.Length();
    }

    /// <summary>The closest point of a recorded path to a body axis (the segment <paramref name="low"/> to <paramref name="high"/>): its index and distance; (-1, inf) for an empty path.</summary>
    public static (int Index, float Distance) ClosestApproach(IReadOnlyList<PathPoint> path, Vector3 low, Vector3 high)
    {
        int best = -1; float bestD = float.PositiveInfinity;
        for (int i = 0; i < path.Count; i++)
        {
            float d = Vector3.Distance(path[i].Position, ClosestOnSegment(path[i].Position, low, high));
            if (d < bestD) { bestD = d; best = i; }
        }
        return (best, bestD);
    }

    public static Vector3 ClosestOnSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float l = ab.LengthSquared();
        if (l < 1e-10f) return a;
        return a + ab * Math.Clamp(Vector3.Dot(p - a, ab) / l, 0f, 1f);
    }

    /// <summary>The body's surface radius (m) the contact sits on (ours: a marine's torso).</summary>
    public const float BodySurface = 0.35f;

    /// <summary>The contact on the body's surface toward a weapon point (game space, Z up): the axis point nearest it pushed out to <see cref="BodySurface"/>.</summary>
    public static Vector3 ContactOnBody(Vector3 weapon, Vector3 low, Vector3 high)
    {
        var axis = ClosestOnSegment(weapon, low, high);
        var o = weapon - axis;
        if (o.LengthSquared() < 1e-8f) return axis;
        return axis + Vector3.Normalize(o) * MathF.Min(BodySurface, o.Length());
    }

    /// <summary>
    /// Whether the remote spray waits: the attacker as drawn has not yet reached the closest approach (the newest sample is the nearest so far and still farther than
    /// <paramref name="touch"/>) and the wait has not run out (<paramref name="waited"/> under <see cref="MaxWaitSeconds"/>).
    /// </summary>
    public static bool WaitForContact(int closestIndex, int count, float closestDistance, double waited, float touch = 0.55f) =>
        waited < MaxWaitSeconds && closestIndex == count - 1 && closestDistance > touch;

    /// <summary>The longest a confirmed melee damage waits for the drawn weapon to reach the victim (s; remote models are drawn a little behind the server).</summary>
    public const double MaxWaitSeconds = 0.3;

    /// <summary>The most a late spray is started already aged (s): a confirmation that arrives after the drawn contact starts the spray as if it had begun there, up to this much.</summary>
    public const double MaxPreAge = 0.08;
    public static double PreAge(double now, double contactAt) => Math.Clamp(now - contactAt, 0.0, MaxPreAge);

    /// <summary>The incoming shot's direction from the shot origin to its end (null for a zero-length shot).</summary>
    public static Vector3? ShotDirection(Vector3 origin, Vector3 end)
    {
        var d = end - origin;
        return d.LengthSquared() < 1e-8f ? null : Vector3.Normalize(d);
    }

    // ---- remote melee: the attacker of a confirmed damage mark ----

    /// <summary>A player who may have struck: id, side, feet, facing, seconds since his weapon melee attack (not a stomp or blast) was last seen active, and whether it was heavy.</summary>
    public readonly record struct MeleeCandidate(int Id, Team Team, Vector3 Feet, float Yaw, double SinceMelee, bool Alive, bool Heavy = false, bool Own = false, bool Slash = false);

    /// <summary>Melee reach for the pairing (m, horizontal, the lunge included; ours) and the recent-attack windows (s): another player's record arrives with the damage, the own prediction a round trip earlier.</summary>
    public const float PairReach = 3.6f;
    /// <summary>The air slash's pairing reach (m, horizontal): its ability's 4 m <c>max_range</c> and a margin for the drawn position; no facing test (the slash's cone follows the pack's heading in the air, not the body's drawn yaw). Ours (live check 2026-11-24: slash hits paired with nothing at 2.5 to 4.3 m).</summary>
    public const float SlashPairReach = 4.6f;

    /// <summary>
    /// Whether a player's current attack is a weapon melee for the directional look: a melee step that is not a stomp, or the jump pack's air slash (owner 2026-11-24, question card
    /// "Yes, spray it": the slash's blade hits are weapon melee, sprayed along the blade's path like any other). A bull rush or dash attack contact is neither (not asked).
    /// </summary>
    public static bool IsWeaponMelee(bool alive, int meleeStep, bool stomp, bool airSlash) => alive && (airSlash || (meleeStep > 0 && !stomp));

    /// <summary>
    /// The attack key of a contact (one spray per victim and key, <see cref="ContactLedger"/>): a melee step's from its start tick, step and weight; an air slash's from the attacker's
    /// count of slashes (negative, so it never equals a step's).
    /// </summary>
    public static long AttackKey(long startTick, int step, bool heavy) => startTick * 64 + step * 2 + (heavy ? 1 : 0);
    public static long SlashKey(int slashCount) => -1L - slashCount;

    /// <summary>A bull-rush charge's attack key, from the charger's count of rushes (below every slash key, never a melee step's).</summary>
    public static long ChargeKey(int rushCount) => -1_000_000_000L - rushCount;

    /// <summary>The charge phases a connect happens in (owner 2026-11-24, question card "Yes, spray charges too"): the rush itself and its collision stop; not the jump pack's dash attack.</summary>
    public static bool IsChargeContact(RushPhase phase) => phase is RushPhase.Rush or RushPhase.Collision;

    /// <summary>How far (m, horizontal) a charger in his rush or collision stop may be from the victim of a damage mark and be taken as its source (ours; the connect stands them 0.9 m apart).</summary>
    public const float ChargeReach = 2.5f;

    /// <summary>The charge spray's sideways spread (rad; ours: a body slam, between a thrust's and a sweep's).</summary>
    public const float ChargeSpread = 0.3f;

    /// <summary>
    /// The direction of a charge contact (game space, level): the charger's velocity during the rush when he was moving (over 1 m/s), else the line from him to the victim, else his facing.
    /// </summary>
    public static Vector3 ChargeDirection(Vector3 rushVelocity, Vector3 chargerToVictim, float chargerYaw)
    {
        var v = new Vector3(rushVelocity.X, rushVelocity.Y, 0f);
        if (v.Length() > 1f) return Vector3.Normalize(v);
        var b = new Vector3(chargerToVictim.X, chargerToVictim.Y, 0f);
        if (b.Length() > 1e-3f) return Vector3.Normalize(b);
        return new Vector3(-MathF.Sin(chargerYaw), MathF.Cos(chargerYaw), 0f);
    }
    /// <summary>How far from a victim's drawn chest a server body or head hit this view resolved to nobody (or to someone else) may end and still be taken as his (m; ours, live check 2026-11-24: such hits ended up to 2.5 m away).</summary>
    public const float LooseShotReach = 3.5f;
    /// <summary>The most the loose-shot reach grows to for a fast victim (m; ours, lead decision 2026-11-24).</summary>
    public const float LooseShotReachMax = 6f;

    /// <summary>
    /// The loose-shot reach for a victim drawn moving at <paramref name="drawnSpeed"/> (m/s, level) with the remote players drawn <paramref name="drawLag"/> s behind (the client's
    /// interpolation delay in use): <see cref="LooseShotReach"/> plus how far he moves in that lag, at most <see cref="LooseShotReachMax"/> (ours, lead decision 2026-11-24).
    /// </summary>
    public static float LooseReach(float drawnSpeed, float drawLag) =>
        Math.Min(LooseShotReachMax, LooseShotReach + Math.Max(0f, drawnSpeed) * Math.Clamp(drawLag, 0f, 0.3f));

    /// <summary>A server-reported body or head hit offered to a mark: where it ended and the shot's direction (game space).</summary>
    public readonly record struct LooseHit(Vector3 End, Vector3 Dir);

    /// <summary>
    /// The loose hit a mark on a victim with his chest at <paramref name="chest"/> takes: among those ending within <paramref name="reach"/>, the one whose shot line (through its end,
    /// along its direction) passes closest to him; equal lines, the nearer end. -1: none.
    /// </summary>
    public static int PickLooseHit(Vector3 chest, float reach, IReadOnlyList<LooseHit> hits)
    {
        int best = -1; float bestLine = float.MaxValue, bestEnd = float.MaxValue;
        for (int i = 0; i < hits.Count; i++)
        {
            float end = Vector3.Distance(hits[i].End, chest);
            if (end > reach) continue;
            float line = LineDistance(chest, hits[i].End, hits[i].Dir);
            if (line < bestLine - 1e-4f || (MathF.Abs(line - bestLine) <= 1e-4f && end < bestEnd)) { best = i; bestLine = line; bestEnd = end; }
        }
        return best;
    }

    /// <summary>The distance from <paramref name="p"/> to the line through <paramref name="a"/> along <paramref name="dir"/>.</summary>
    public static float LineDistance(Vector3 p, Vector3 a, Vector3 dir)
    {
        if (dir.LengthSquared() < 1e-8f) return Vector3.Distance(p, a);
        var u = Vector3.Normalize(dir);
        var ap = p - a;
        return (ap - u * Vector3.Dot(ap, u)).Length();
    }
    public const double RemoteWindow = 0.35, OwnWindow = 0.8;

    /// <summary>
    /// The attacker of a confirmed melee damage on <paramref name="victimTeam"/>'s player at <paramref name="victimFeet"/>: among living enemies whose weapon melee attack was active within
    /// the window, in reach and facing within 100 degrees of him, the best (nearest, facing more squarely; equal scores: the lower id). 0: nobody (a blast, a stomp, a shot or a fall: no melee spray).
    /// Two attackers striking him in the same tick raise his mark once, so one spray is drawn for the better placed.
    /// </summary>
    public static int PickAttacker(int victimId, Team victimTeam, Vector3 victimFeet, IReadOnlyList<MeleeCandidate> candidates)
    {
        int best = 0; float bestScore = float.PositiveInfinity;
        foreach (var c in candidates)
        {
            if (c.Id == victimId || !c.Alive || c.Team == victimTeam) continue;
            if (c.SinceMelee < 0 || c.SinceMelee > (c.Own ? OwnWindow : RemoteWindow)) continue;
            var d = victimFeet - c.Feet; d.Z = 0f;
            float dist = d.Length();
            if (dist > (c.Slash ? SlashPairReach : PairReach)) continue;
            float cos = dist < 1e-3f ? 1f : Vector3.Dot(d / dist, new Vector3(-MathF.Sin(c.Yaw), MathF.Cos(c.Yaw), 0f));
            if (c.Slash) cos = MathF.Max(cos, 0f);
            else if (cos < -0.17f) continue;   // behind him (beyond 100 degrees)
            float score = dist + 1.5f * (1f - cos);
            if (score < bestScore - 1e-4f || (MathF.Abs(score - bestScore) <= 1e-4f && c.Id < best)) { best = c.Id; bestScore = score; }
        }
        return best;
    }

    /// <summary>One spray per victim and contact: a contact (victim, attacker, attack key) is drawn once; entries age out after <see cref="Seconds"/>.</summary>
    public sealed class ContactLedger
    {
        public const double Seconds = 1.0;
        readonly Dictionary<(int Victim, int Attacker, long Key), double> _seen = new();
        readonly List<(int, int, long)> _old = new();

        /// <summary>True the first time a contact is offered within <see cref="Seconds"/>; false for a repeat.</summary>
        public bool First(int victim, int attacker, long key, double now)
        {
            if (_seen.Count > 32)
            {
                _old.Clear();
                foreach (var (k, at) in _seen) if (now - at > Seconds) _old.Add(k);
                foreach (var k in _old) _seen.Remove(k);
            }
            if (_seen.TryGetValue((victim, attacker, key), out double was) && now - was <= Seconds) return false;
            _seen[(victim, attacker, key)] = now;
            return true;
        }

        public int Count => _seen.Count;
    }
}
