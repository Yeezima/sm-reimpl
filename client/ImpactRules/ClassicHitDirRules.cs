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

    /// <summary>Streaks in one spark spray (the mesh's quads) and how many it shows.</summary>
    public const int MaxStreaks = 64, SparkStreaks = 22;

    /// <summary>The spark spray's life (s): the emission window behind the weapon, then each streak's flight and fade. (The Extended blood has its own timing since the third design: Extras/Gore/ClassicBloodRules.)</summary>
    public const float EmitSeconds = 0.2f, SparkSeconds = 0.32f;
    public static float SprayLife(bool sparks) => EmitSeconds + SparkSeconds;

    /// <summary>The sideways spread (radians at the end of the emission) of a pure sweep and of a pure thrust: thrusts stay narrow, sweeps bloom wider.</summary>
    public const float SweepSpread = 0.42f, ThrustSpread = 0.14f;

    /// <summary>The spread for a motion: <paramref name="thrustness"/> 1 when the weapon moves along the attacker's facing (a stab), 0 across it (a sweep).</summary>
    /// <summary>
    /// How much of the weapon head's own motion (the attacker's body motion taken out) drives straight out from his shoulder (<paramref name="headFromShoulder"/>: the head's place
    /// from it): 1 a thrust, 0 a sweep around him (or a pull back). Ours, from recorded strikes (tester round 2, 2026-11-24): measured along his facing, a sweep's arc around
    /// the body still runs 0.6 to 0.8 forward at contact and every strike read as a thrust; measured outward from the shoulder, sweeps read 0 to 0.4 and lunging thrusts 0.75 to 0.9.
    /// </summary>
    public static float ThrustOf(Vector3 ownMotion, Vector3 headFromShoulder)
    {
        if (ownMotion.LengthSquared() < 1e-8f || headFromShoulder.LengthSquared() < 1e-8f) return 0f;
        return Math.Clamp(Vector3.Dot(Vector3.Normalize(ownMotion), Vector3.Normalize(headFromShoulder)), 0f, 1f);
    }

    /// <summary>The shoulder height above the feet the thrust measure takes the weapon head's reach from (m; ours).</summary>
    public const float ShoulderHeight = 1.4f;

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

    /// <summary>The fastest a weapon point is read to move (m/s); a faster jump is a pose pop or a teleport, not a swing (ours; 40 before tester round 2, 2026-11-24: a recorded real sweep's head moved 50 to 55 m/s at 1.8 m out and was thrown away; pops jump about 1.8 m in one frame, over 100 m/s).</summary>
    public const float MaxWeaponSpeed = 80f;

    /// <summary>
    /// The weapon's direction of motion at <paramref name="time"/> from its recorded path (oldest first): the difference of the samples around it, over at least <paramref name="span"/> seconds;
    /// null when the path is too short, the weapon did not move or moved implausibly fast.
    /// </summary>
    public static Vector3? WeaponDirection(IReadOnlyList<PathPoint> path, double time, double span = 0.015)
    {
        // the sum of the path's segments within span of the time (a fast arc turns a lot in a few hundredths: a longer chord read a sweep as a thrust), each moving between 0.3 m/s and MaxWeaponSpeed: a single pose pop (an animation
        // change, a snap of the drawn remote) is dropped instead of spoiling the whole tangent (tester round 2, 2026-11-24: real strikes often had one in the window)
        if (path.Count < 2) return null;
        Vector3 sum = Vector3.Zero; int used = 0;
        Vector3 nearest = Vector3.Zero; double nearestGap = double.MaxValue;
        for (int k = 1; k < path.Count; k++)
        {
            double dt = path[k].Time - path[k - 1].Time;
            if (dt <= 1e-6) continue;
            var d = path[k].Position - path[k - 1].Position;
            float speed = d.Length() / (float)dt;
            if (speed < 0.3f || speed > MaxWeaponSpeed) continue;
            double mid = (path[k].Time + path[k - 1].Time) * 0.5;
            if (path[k - 1].Time >= time - span - 1e-9 && path[k].Time <= time + span + 1e-9) { sum += d; used++; }
            double gap = Math.Abs(mid - time);
            if (gap < nearestGap) { nearestGap = gap; nearest = d; }
        }
        if (used == 0)
        {
            if (nearestGap > 4 * span) return null;   // nothing valid near the time
            sum = nearest;
        }
        return sum.LengthSquared() < 1e-10f ? null : Vector3.Normalize(sum);
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

    /// <summary>
    /// How close (m) the drawn weapon head must come to the victim's body axis for its path to give the contact and direction (ours; 1.6 before tester round 2, 2026-11-24, when 7 of
    /// 12 real strikes in a headless 2v2 passed 2.2 to 3.6 m from the drawn victim, who is drawn a little behind, and fell back to the attacker's facing). The contact itself stays on
    /// the body's surface toward the head.
    /// </summary>
    public const float ContactReach = 4f;

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

    /// <summary>
    /// One weapon melee attack of a player as this view recorded it (review of 2b89de15, findings 1 and 2): its key, the server tick it started at, the last tick it was seen active, and
    /// the first tick its blow can land (a step: its start plus the step's hit time; an air slash: its landing (grounded after <c>Air</c>, airborne) plus the slash's hit time, unknown (max) until it lands). Ticks are the
    /// server's numbering: the own attack from the prediction's tick, another player's from the tick he is drawn at (the same tick the victim's damage mark is drawn at).
    /// </summary>
    public record struct AttackSpan(long Key, long StartTick, long LastTick, long HitFromTick, bool Slash, bool Air = false);

    /// <summary>How many ticks past an attack's last active tick a damage mark is still taken as its (the mark shows on the snapshot after the damage: up to a snapshot interval late; ours).</summary>
    public const int SpanSlackTicks = 4;

    /// <summary>
    /// The attack a damage mark drawn at server tick <paramref name="markTick"/> belongs to, among a player's recorded <paramref name="spans"/>: one that had started by then (a tick of
    /// slack) and was active up to <see cref="SpanSlackTicks"/> before it; an air slash only once its blow can land (its landing blast, a tick or more earlier, is not the slash's:
    /// finding 2); of several, the latest whose blow can land, else the latest. Null: none (the mark is not his weapon melee's).
    /// <para>The own attack is predicted ahead of the marks, so "his current attack" is often the next combo step already (finding 1: step N's mark was filed under step N+1's key and
    /// the next hit drew nothing); the tick says which step it was.</para>
    /// </summary>
    public static AttackSpan? SpanForMark(long markTick, IReadOnlyList<AttackSpan> spans)
    {
        AttackSpan? best = null; bool bestReady = false;
        foreach (var sp in spans)
        {
            if (sp.StartTick - 1 > markTick || markTick > sp.LastTick + SpanSlackTicks) continue;
            bool ready = markTick >= sp.HitFromTick - 1;
            if (sp.Slash && !ready) continue;
            if (best is null || (ready && !bestReady) || (ready == bestReady && sp.StartTick > best.Value.StartTick)) { best = sp; bestReady = ready; }
        }
        return best;
    }

    /// <summary>
    /// Whether a recorded attack continues the previous record's: the same step and weight with a start tick within <see cref="MeleeHitGate.StartTolerance"/> (another player's start
    /// estimate jitters with the snapshots; a fresh key there split one swing's path and let a second damage of it pass the ledger).
    /// </summary>
    /// <summary>Whether a melee step's span is a heavy attack (the weight is the key's low bit, <see cref="AttackKey"/>).</summary>
    public static bool SpanHeavy(AttackSpan span) => !span.Slash && (span.Key & 1) == 1;

    public static bool SameAttack(long startA, int stepA, bool heavyA, long startB, int stepB, bool heavyB) =>
        stepA == stepB && heavyA == heavyB && Math.Abs(startA - startB) <= MeleeHitGate.StartTolerance;

    /// <summary>A bull-rush charge's attack key, from the charger's count of rushes (below every slash key, never a melee step's).</summary>
    public static long ChargeKey(int rushCount) => -1_000_000_000L - rushCount;

    /// <summary>The charge phases a connect happens in (owner 2026-11-24, question card "Yes, spray charges too"): the rush itself and its collision stop; not the jump pack's dash attack.</summary>
    public static bool IsChargeContact(RushPhase phase) => phase is RushPhase.Collision;   // review 2026-11-24: only on contact (the collision stop), not the run-up

    /// <summary>How far (m, horizontal) a charger in his rush or collision stop may be from the victim of a damage mark and be taken as its source (ours; the connect stands them 0.9 m apart).</summary>
    public const float ChargeReach = 2.5f;

    // ---- the glue decisions as rules (review of fcd76a6f, 2026-11-24): each one a finding of the review, tested on its own ----

    /// <summary>
    /// The victim's armour before a mark, when the client's tracker may not know it (a first sight, or the death reset that clears it before a lethal mark is read): unknown reads
    /// as down, so a health-only mark is blood, not mixed, and Faithful never gains sparks on a bare-armour kill (review finding 2).
    /// </summary>
    public static bool ArmourBeforeForMark(bool? tracked) => tracked ?? false;

    /// <summary>How old the own hit report may be and still give a melee contact its exact armour: the own report arrives the remote draw lag ahead of the interpolated mark (finding 1).</summary>
    public static double OwnReportWindow(double drawLag) => Math.Clamp(drawLag, 0.0, 0.3) + 0.15;

    /// <summary>
    /// Whether the own melee hit report draws the directional look (and leaves out the old fan) at once: the same pairing test the interpolated mark uses later, with the own
    /// attacker only (finding 1 of the review of fcd76a6f: the report came first and drew the old fan, then the mark drew the spray), and only when the mark will draw it: the report's
    /// attack is known (<paramref name="hasKey"/>) and is its first report on this victim (<paramref name="reportsBefore"/> 0; a second damage under the same key is not drawn by the mark,
    /// so it keeps the old fan: review of 2b89de15, finding 1, no hit with less than base).
    /// </summary>
    public static bool OwnReportDirectional(DirLook look, bool lastMelee, int victimId, Team victimTeam, Vector3 victimFeet, MeleeCandidate own, bool hasKey = true, int reportsBefore = 0) =>
        look.HasFlag(DirLook.MeleeSparks) && lastMelee && own.Own && hasKey && reportsBefore == 0 && PickAttacker(victimId, victimTeam, victimFeet, new[] { own }) == own.Id;

    /// <summary>
    /// The confirmed-class sparks of a shot, drawn when its damage mark arrives (review of 2b89de15, finding 6; the brief's armour rule): the yellow-white armour sparks for armour only
    /// and for health lost with armour left; none for a bare-armour health hit or a break with health damage. In a profile without the blood spray (Faithful), a health hit keeps
    /// today's red sparks: drawn here only when the shot's impact did not (its armour bit said up, <paramref name="armourUpAtImpact"/>: a break with health damage).
    /// </summary>
    public static (bool Yellow, bool Red) GunfireMarkSparks(DirLook look, HitVisual visual, bool armourUpAtImpact) =>
        (HasSparks(visual), !look.HasFlag(DirLook.BloodSpray) && visual == HitVisual.Blood && armourUpAtImpact);

    /// <summary>
    /// Whether a shot's impact leaves its sparks to its damage mark (finding 6): with the directional look, on another player this view resolved the hit to; the own body and an
    /// unresolved hit draw at the impact as before.
    /// </summary>
    public static bool SparksWaitForMark(DirLook look, int victim, int ownPlayer) => look != DirLook.None && victim > 0 && victim != ownPlayer;

    /// <summary>What a charge connect's data impact effects do (finding 3).</summary>
    public enum ChargeFx { PlayNow, Hold, Skip }

    /// <summary>
    /// <see cref="ChargeFx.PlayNow"/> without the directional look, on the own body, when the mark already drew nothing new, or in a profile without blood when the victim's armour
    /// is known down (nothing new will draw); <see cref="ChargeFx.Skip"/> when the directional look already drew this contact; else <see cref="ChargeFx.Hold"/> for the mark.
    /// </summary>
    public static ChargeFx ChargeImpactDecision(DirLook look, bool ownVictim, bool alreadyDrawn, bool markDrewNothing, bool? victimArmourUp)
    {
        if (look == DirLook.None || ownVictim) return ChargeFx.PlayNow;
        if (alreadyDrawn) return ChargeFx.Skip;
        if (markDrewNothing) return ChargeFx.PlayNow;
        if (!look.HasFlag(DirLook.BloodSpray) && victimArmourUp == false) return ChargeFx.PlayNow;
        return ChargeFx.Hold;
    }

    /// <summary>Whether a health hit on bare armour keeps today's red sparks: always on the own body, which draws no directional look (finding 4).</summary>
    public static bool KeepRedSparks(bool bloodReplacesRed, bool victimIsLocal) => !bloodReplacesRed || victimIsLocal;

    /// <summary>How close (m) a hit's end must be to the living player it was resolved to for it to stay his (finding 5).</summary>
    public const float ResolvedBodyReach = 1.3f;

    /// <summary>
    /// Whether a server body or head hit may be taken by another victim's mark: never an own predicted hit (unconfirmed), and only one resolved to nobody or ending farther than
    /// <see cref="ResolvedBodyReach"/> from the living player it was resolved to (finding 5: a loose hit could be taken from its correctly resolved victim).
    /// </summary>
    public static bool LooseHitEligible(bool own, int resolvedTo, float resolvedDistance) => !own && (resolvedTo <= 0 || resolvedDistance > ResolvedBodyReach);

    /// <summary>The speed scale of a spray's throw (sparks fly faster); set before its velocities are filled (finding 6: a pooled spray kept the last one's).</summary>
    public static float SpeedScaleFor(bool sparks) => sparks ? 1.6f : 1f;

    /// <summary>The thrust measure from which a melee spray starts on the far (exit) side of the body, as gunfire and charges do (lead decision 7; ours).</summary>
    public const float ExitThrust = 0.6f;

    /// <summary>Whether a spray starts on the exit side: gunfire, and melee thrusts at <see cref="ExitThrust"/> or more (lead decision 7).</summary>
    public static bool StartsAtExit(bool gunfire, float thrust) => gunfire || thrust >= ExitThrust;

    /// <summary>The exit point on the body's surface (game space, Z up): the body axis at the struck height pushed out along the level part of the direction (lead decision 7).</summary>
    public static Vector3 ExitContact(Vector3 axisPoint, Vector3 dir)
    {
        var level = new Vector3(dir.X, dir.Y, 0f);
        return level.LengthSquared() < 1e-8f ? axisPoint : axisPoint + Vector3.Normalize(level) * BodySurface;
    }

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
        /// <summary>Whether a contact was already drawn within <see cref="Seconds"/> (no record made).</summary>
        public bool Seen(int victim, int attacker, long key, double now) => _seen.TryGetValue((victim, attacker, key), out double at) && now - at <= Seconds;

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
