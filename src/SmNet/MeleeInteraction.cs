using System.Numerics;
using SmSim;

namespace SmNet;

/// <summary>
/// anim/melee-interaction, client presentation rules (engine-free, tested; the simulation's side is <see cref="ChargeLock"/>). The owner (2026-10-31): "it's still very floaty and
/// disconnected ... improve the violence and effects around the charge. it needs to feel much weightier/meatier/crunchier/satisfying ... there should be some interaction between
/// melee participants". Every value here is **ours** (spec/behavior.md "Melee interaction pass": the presentation rows, in the refine register).
/// <list type="bullet">
/// <item><b>Charge hit-stop</b>: on a charge connect the charger's collision-stop clip and the victim's stun clip hold for <see cref="ChargeStopSeconds"/>, then catch up over
/// <see cref="ChargeCatchUpSeconds"/> (<see cref="ChargeHold"/>; the simulation never waits). Longer than the melee hit-stop (opt-in, 0.07 s): a body slam.</item>
/// <item><b>Camera punch</b>: the charger's and the victim's own cameras take a quick directional kick (<see cref="PunchAt"/>: up in <see cref="PunchRise"/>, back with
/// <see cref="PunchTau"/>) plus a short shake; an observer within <see cref="ObserverRange"/> a faint shake.</item>
/// <item><b>Victims turned to the attacker</b>: a reacting body turns so the reaction clip's side points exactly at the attacker (<see cref="FacingFor"/>; the clip the simulation
/// chose by the victim's view stays), eased (<see cref="Ease"/>).</item>
/// </list>
/// </summary>
public static class MeleeInteraction
{
    /// <summary>Ours: how long a charge connect holds the two clips (seconds; six frames at 60 fps).</summary>
    public const float ChargeStopSeconds = 0.1f;
    /// <summary>Ours: how long the held clips take to catch up afterwards (seconds).</summary>
    public const float ChargeCatchUpSeconds = 0.15f;
    /// <summary>Ours: the camera kick's peak for the charger, the victim (degrees of pitch) and the shake added to each (degrees).</summary>
    public const float ChargerPunchDegrees = 2.2f, VictimPunchDegrees = 3.5f, ChargerShakeDegrees = 0.9f, VictimShakeDegrees = 1.3f;
    /// <summary>Ours: an observer's shake at the impact (degrees) and the range it fades out over (metres).</summary>
    public const float ObserverShakeDegrees = 0.35f, ObserverRange = 12f;
    /// <summary>Ours: the kick reaches its peak in this long and returns with this time constant (seconds).</summary>
    public const float PunchRise = 0.03f, PunchTau = 0.09f;
    /// <summary>Ours: how fast a reacting body turns to line its clip up with the attacker (1/s, exponential).</summary>
    public const float FacingRate = 25f;
    /// <summary>Ours: how far from the attacker a reaction's source is looked for (metres; the bull rush's secondary reach is 4).</summary>
    public const float SourceRange = 4.5f;

    /// <summary>Ours: the own charge's victim is drawn at the lock spot until its reaction shows, then the override fades over this long (seconds); at most this long in all.</summary>
    public const float LockDrawFade = 0.2f, LockDrawMax = 0.8f;

    /// <summary>
    /// The weight of the lock-spot override on the own charge's victim <paramref name="since"/> seconds after the predicted contact, <paramref name="sinceReacted"/> seconds after its drawn
    /// reaction began (negative: not yet): 1, then a smooth fade over <see cref="LockDrawFade"/> once it reacts or as <see cref="LockDrawMax"/> runs out.
    /// </summary>
    public static float LockDrawWeight(float since, float sinceReacted)
    {
        if (!(since >= 0f)) return 0f;
        float fade = MathF.Max(sinceReacted >= 0f ? sinceReacted : 0f, since - (LockDrawMax - LockDrawFade));
        if (fade <= 0f) return 1f;
        if (fade >= LockDrawFade) return 0f;
        float u = 1f - fade / LockDrawFade;
        return u * u * (3f - 2f * u);
    }

    /// <summary>Seconds a clip is held back <paramref name="since"/> seconds after a charge connect: frozen for <see cref="ChargeStopSeconds"/>, then fading to 0.</summary>
    public static float ChargeHold(float since)
    {
        if (!(since >= 0f)) return 0f;
        if (since <= ChargeStopSeconds) return since;
        return MathF.Max(0f, ChargeStopSeconds * (1f - (since - ChargeStopSeconds) / ChargeCatchUpSeconds));
    }

    /// <summary>Whether a charge hit-stop that began <paramref name="since"/> seconds ago still changes anything.</summary>
    public static bool ChargeHoldActive(float since) => since >= 0f && since < ChargeStopSeconds + ChargeCatchUpSeconds;

    /// <summary>The camera kick <paramref name="since"/> seconds after it began, as a share of its peak (0..1): a smooth rise over <see cref="PunchRise"/>, then exponential return.</summary>
    public static float PunchAt(float since)
    {
        if (!(since >= 0f)) return 0f;
        if (since < PunchRise) { float u = since / PunchRise; return u * u * (3f - 2f * u); }
        float k = MathF.Exp(-(since - PunchRise) / PunchTau);
        return k < 0.01f ? 0f : k;
    }

    /// <summary>An observer's shake at <paramref name="distance"/> metres from the impact (radians): full within 3 m, fading to 0 at <see cref="ObserverRange"/>.</summary>
    public static float ObserverShake(float distance)
    {
        if (!float.IsFinite(distance) || distance >= ObserverRange) return 0f;
        float f = distance <= 3f ? 1f : 1f - (distance - 3f) / (ObserverRange - 3f);
        return ObserverShakeDegrees * MathF.PI / 180f * f;
    }

    /// <summary>
    /// The player a charger in its stop met: with the lock (<see cref="SimConfig.ChargeLock"/>) the living player standing nearest the lock spot (<see cref="ChargeLock.LockDistance"/> in front
    /// of the charger's heading) within 0.75 m of it; without one (or none there), the melee reach rule (<see cref="MeleePresentation.Victim"/>). 0: none (a wall stop).
    /// </summary>
    public static int ChargeVictim(in PlayerState charger, IEnumerable<PlayerState> others, SimConfig c)
    {
        var forward = new Vector2(-MathF.Sin(charger.Yaw), MathF.Cos(charger.Yaw));
        var spot = new Vector2(charger.Position.X, charger.Position.Y) + forward * ChargeLock.LockDistance(c);
        int best = 0; float bestD = 0.75f;
        var list = others as IReadOnlyCollection<PlayerState> ?? others.ToList();
        if (c.ChargeLock)
            foreach (var o in list)
            {
                if (o.PlayerId == charger.PlayerId || !o.Alive || MathF.Abs(o.Position.Z - charger.Position.Z) > c.MeleeVerticalReach) continue;
                float d = Vector2.Distance(new Vector2(o.Position.X, o.Position.Y), spot);
                if (d < bestD || (d == bestD && o.PlayerId < best)) { bestD = d; best = o.PlayerId; }
            }
        return best > 0 ? best : MeleePresentation.Victim(charger, list, c);
    }

    /// <summary>
    /// Who put <paramref name="victim"/> in its reaction, as far as the client can tell (presentation guess): the nearest other living player within <see cref="SourceRange"/> on another side
    /// (or no side) that is attacking now (a melee step, or a charge or its stop); 0 when none.
    /// </summary>
    public static int ReactionSource(in PlayerState victim, IEnumerable<PlayerState> others)
    {
        int best = 0; float bestD = SourceRange;
        foreach (var o in others)
        {
            if (o.PlayerId == victim.PlayerId || !o.Alive || (o.Team == victim.Team && o.Team != Team.None)) continue;
            if (o.MeleeStep <= 0 && o.Rush == RushPhase.None) continue;
            float d = Vector2.Distance(new Vector2(o.Position.X, o.Position.Y), new Vector2(victim.Position.X, victim.Position.Y));
            if (d < bestD) { bestD = d; best = o.PlayerId; }
        }
        return best;
    }

    /// <summary>The yaw (radians; yaw 0 faces +Y, growing to the left) from <paramref name="from"/> toward <paramref name="to"/>.</summary>
    public static float Bearing(Vector3 from, Vector3 to) => MathF.Atan2(-(to.X - from.X), to.Y - from.Y);

    /// <summary>
    /// The body yaw that puts the attacker at <paramref name="bearing"/> on the side the reaction clip was chosen for: in front (the front clip), behind, on the right or on the left
    /// (<see cref="Reaction.DirectionOf"/>'s sectors).
    /// </summary>
    public static float FacingFor(ReactionDirection direction, float bearing) => direction switch
    {
        ReactionDirection.Back => bearing + MathF.PI,
        ReactionDirection.Right => bearing + MathF.PI / 2f,
        ReactionDirection.Left => bearing - MathF.PI / 2f,
        _ => bearing,
    };

    /// <summary>The angle from <paramref name="from"/> to <paramref name="to"/> in (−π, π].</summary>
    public static float Wrap(float a)
    {
        a -= MathF.Round(a / (2f * MathF.PI)) * 2f * MathF.PI;
        return a;
    }

    /// <summary><paramref name="current"/> turned toward <paramref name="target"/> the short way, closing the gap at <see cref="FacingRate"/> (exponential) over <paramref name="dt"/>.</summary>
    public static float Ease(float current, float target, float dt)
    {
        if (!float.IsFinite(current)) return target;
        float gap = Wrap(target - current);
        if (!(dt > 0f) || !float.IsFinite(dt)) return current;
        return current + gap * (1f - MathF.Exp(-FacingRate * MathF.Min(dt, 0.25f)));
    }
}
