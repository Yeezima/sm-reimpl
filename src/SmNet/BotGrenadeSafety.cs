using System.Numerics;
using SmSim;

namespace SmNet;

/// <summary>
/// bot-grenade-safety (owner, 2026-11-11: "i want to set a rule that includes a minimum distance at which they will throw grenades. they should also not (as a raptor/assault
/// marine) jump in near where the grenade landed until it has exploded" and "the end result is no more or very few bot suicides to their own grenades"; the rules are **ours**,
/// test bots are not the original's AI). Classic PvP bots only (Annihilation and Seize Ground; Musou Mode's co-op bots keep their own script); engine-free so it can be tested.
/// <list type="bullet">
/// <item>The throw: a damaging item (its projectile has a blast that hurts, the frag) is thrown only when the target's chest and the predicted landing point (the target's feet
/// moved by its horizontal velocity over the flight, or the first level contact along the aimed line) are both at least <see cref="SafetyDistance(ThrownSpec?, float)"/> from the
/// bot's chest (3D): the item's own largest damaging blast radius from the data plus <see cref="Margin"/>; never at a target in the air (<see cref="Airborne"/>). The aim goes to that landing point (the owner's 2026-10-29 bounce rule
/// keeps a grenade within about a metre of where it lands, so the old aim <see cref="OldThrowShort"/> m short of the target put it at the bot's feet). Stims and the blind grenade
/// keep the old rule (<see cref="OldThrowMin"/> m horizontally; stims at any distance).</item>
/// <item>The hold: while the bot's own live grenade (or launcher round) is out, a <see cref="Hazard"/> at it; on the ground the bot does not move, jump, dash, hop or charge into
/// one (it holds), and inside one it backs straight out (<see cref="Hold"/>); a jump-pack bot does not jump at, pound onto or steer at a target inside one.</item>
/// <item>Remote detonation: never while the bot's chest is inside its own waiting rounds' blast reach (<see cref="MayDetonate"/>).</item>
/// </list>
/// <c>--old-bot-throws</c> (<see cref="RestoreFlag"/>) brings the bots' throws, aim and holds back as they were.
/// </summary>
public static class BotGrenadeSafety
{
    /// <summary>The client flag that restores the old throws (6 m horizontal minimum, the aim 8 m short of the target, no holds, detonation at any distance).</summary>
    public const string RestoreFlag = "old-bot-throws";
    /// <summary>Metres added to a blast's radius for the bot's safety distance (ours: chest-height and quantization slack, a step or two of the bot's own move).</summary>
    public const float Margin = 1.5f;
    /// <summary>The old minimum throw distance (metres, horizontal; the old test script).</summary>
    public const float OldThrowMin = 6f;
    /// <summary>The old aim: metres short of the target the throw was aimed at the ground (the old test script, from before the 2026-10-29 bounce rule).</summary>
    public const float OldThrowShort = 8f;
    /// <summary>Seconds of the bot's own ground move tested against a hazard (ours): a move whose end after this long would be inside one is held.</summary>
    public const float LookAhead = 0.6f;
    /// <summary>Metres a second the hold assumes for a full stick (a marine's run, rounded up; ours).</summary>
    public const float MoveSpeed = 6f;
    /// <summary>Seconds after the release an own throw stays a hazard at its predicted landing point when its grenade is never seen: this + 1.5 s (ours).</summary>
    public const float UnseenSeconds = 0.6f;
    /// <summary>Seconds an own grenade is a hazard at most (a lost detonation; the frag's lifetime is 30 s, its ground fuse 1 s; ours).</summary>
    public const float MaxHazardSeconds = 6f;
    /// <summary>Seconds after the own release within which a new thrown projectile may be claimed as the bot's own (ours).</summary>
    public const float ClaimSeconds = 1f;
    /// <summary>Metres of slack on how far the own grenade can be from the release point (the hand above the feet, a snapshot's lead; ours).</summary>
    public const float ClaimSlack = 3f;

    /// <summary>
    /// Whether a new thrown projectile <paramref name="distance"/> metres from the bot's feet at its release, <paramref name="age"/> s after the release, can be the bot's own: within
    /// <see cref="ClaimSeconds"/> and no farther than the throw's <paramref name="speed"/> could have carried it plus <see cref="ClaimSlack"/> (snapshots carry no owner; an enemy's
    /// grenade arriving beside the bot is farther from the hand than the bot's own could be yet).
    /// </summary>
    public static bool Claimable(float distance, float age, float speed) => age >= 0f && age <= ClaimSeconds && distance <= MathF.Max(speed, 0f) * age + ClaimSlack;

    /// <summary>Whether the rules run: Classic PvP (any match that is not Musou Mode) and no <see cref="RestoreFlag"/>.</summary>
    public static bool Applies(ContentExtras extras, bool restore) => !restore && !extras.HasFlag(ContentExtras.Musou);

    /// <summary>A projectile's largest damaging blast radius (metres; the blind grenade's blind-only blasts do not count), 0 when it has none.</summary>
    public static float DamagingRadius(ProjectileSpec? projectile)
    {
        if (projectile is null) return 0f;
        float radius = 0f;
        foreach (var s in projectile.AllSplashes)
            if (!s.BlindOnly && s.Radius > radius && (s.Damage.Damage > 0f || s.Damage.ShieldDamage > 0f)) radius = s.Radius;
        return radius;
    }

    /// <summary>
    /// The bot's safety distance for a thrown item (metres): its largest damaging blast radius × <paramref name="reach"/> (<see cref="SimConfig.ThrownBlastScale"/>, 1 in Classic
    /// PvP) + <see cref="Margin"/>; 0 (the old rule) for stims, the blind grenade and anything without a damaging blast.
    /// </summary>
    public static float SafetyDistance(ThrownSpec? item, float reach = 1f) =>
        item is null || item.Stim is not null ? 0f : DamagingRadius(item.Projectile) is var r and > 0f ? r * MathF.Max(reach, 0f) + Margin : 0f;

    /// <summary>The safety distance of a projectile (a launcher round, a plasma ball): its damaging blast radius + <see cref="Margin"/>, 0 without one.</summary>
    public static float SafetyDistance(ProjectileSpec? projectile) => DamagingRadius(projectile) is var r and > 0f ? r + Margin : 0f;

    /// <summary>Seconds a throw takes to cover <paramref name="distance"/> metres at <paramref name="speed"/> m/s (0 when the speed is not positive).</summary>
    public static float FlightSeconds(float distance, float speed) => speed > 0f ? MathF.Max(distance, 0f) / speed : 0f;

    /// <summary>The predicted landing point: the target's feet moved by its horizontal velocity over <paramref name="flightSeconds"/> (a jump's rise does not lift the point).</summary>
    public static Vector3 PredictLanding(Vector3 targetFeet, Vector3 targetVelocity, float flightSeconds) =>
        targetFeet + new Vector3(targetVelocity.X, targetVelocity.Y, 0f) * MathF.Max(flightSeconds, 0f);

    /// <summary>The verdict on a throw.</summary>
    public enum ThrowVerdict
    {
        /// <summary>Throw.</summary>
        Throw,
        /// <summary>The target's chest is within the safety distance of the bot's chest.</summary>
        TargetTooClose,
        /// <summary>The predicted landing point (or the first level contact on the way) is within the safety distance.</summary>
        LandsTooClose,
        /// <summary>The target is farther than the bot's longest throw.</summary>
        OutOfRange,
        /// <summary>The target is in the air (a jump, a fall): it will not be where the grenade lands, and a grenade thrown up at it glances off or flies on.</summary>
        TargetInAir,
        /// <summary>The bot itself is in the air: it moves on between the press and the release, so the landing is not where it was checked.</summary>
        OwnInAir,
    }

    /// <summary>Metres a second of vertical speed beyond which a target counts as in the air whatever its jump phase (a plain jump, a fall; ours).</summary>
    public const float AirborneVerticalSpeed = 3f;

    /// <summary>Whether a target is in the air for the throw rule: in a jump-pack jump or pound (any phase but none and the landing), or moving up or down faster than <see cref="AirborneVerticalSpeed"/>.</summary>
    public static bool Airborne(JumpPhase jump, Vector3 velocity) => jump is not (JumpPhase.None or JumpPhase.Landed) || MathF.Abs(velocity.Z) > AirborneVerticalSpeed;

    /// <summary>
    /// The throw rule for a damaging item: <see cref="ThrowVerdict.Throw"/> when the target's chest and <paramref name="landing"/> are both at least <paramref name="safety"/> from
    /// <paramref name="ownChest"/> (3D), the target is within <paramref name="maxHorizontal"/> metres horizontally and neither it (<paramref name="targetAirborne"/>) nor the bot
    /// (<paramref name="ownAirborne"/>) is in the air (<see cref="Airborne"/>).
    /// </summary>
    public static ThrowVerdict CheckThrow(Vector3 ownChest, Vector3 targetChest, Vector3 landing, float safety, float maxHorizontal, bool targetAirborne = false, bool ownAirborne = false)
    {
        if (Vector3.Distance(ownChest, targetChest) < safety) return ThrowVerdict.TargetTooClose;
        if (Vector3.Distance(ownChest, landing) < safety) return ThrowVerdict.LandsTooClose;
        var flat = new Vector2(targetChest.X - ownChest.X, targetChest.Y - ownChest.Y);
        if (flat.Length() > maxHorizontal) return ThrowVerdict.OutOfRange;
        return targetAirborne ? ThrowVerdict.TargetInAir : ownAirborne ? ThrowVerdict.OwnInAir : ThrowVerdict.Throw;
    }

    /// <summary>The old rule (and the rule for stims and the blind grenade): <paramref name="horizontal"/> metres within [<see cref="OldThrowMin"/>, max]; stims at any distance up to max.</summary>
    public static bool OldThrowAllowed(float horizontal, bool stim, float maxHorizontal) => (stim || horizontal >= OldThrowMin) && horizontal <= maxHorizontal;

    /// <summary>The ground distance the old aim pointed at: <see cref="OldThrowShort"/> m short of the target, at least 2 m.</summary>
    public static float OldAimDistance(float horizontal) => MathF.Max(2f, horizontal - OldThrowShort);

    /// <summary>A live own explosive: its centre (the grenade's place once it is down, the throw's predicted landing point while it flies or is unseen) and its reach (blast radius + margin), metres.</summary>
    public readonly record struct Hazard(Vector3 Centre, float Reach);

    /// <summary>Whether <paramref name="point"/> is within the reach of any hazard (3D).</summary>
    public static bool Inside(IReadOnlyList<Hazard> hazards, Vector3 point)
    {
        foreach (var h in hazards) if (Vector3.DistanceSquared(h.Centre, point) < h.Reach * h.Reach) return true;
        return false;
    }

    /// <summary>Whether the straight segment from <paramref name="from"/> to <paramref name="to"/> passes within the reach of any hazard (3D).</summary>
    public static bool PathCrosses(IReadOnlyList<Hazard> hazards, Vector3 from, Vector3 to)
    {
        var d = to - from;
        float lengthSquared = d.LengthSquared();
        foreach (var h in hazards)
        {
            float t = lengthSquared > 1e-8f ? Math.Clamp(Vector3.Dot(h.Centre - from, d) / lengthSquared, 0f, 1f) : 0f;
            if (Vector3.DistanceSquared(from + d * t, h.Centre) < h.Reach * h.Reach) return true;
        }
        return false;
    }

    /// <summary>The unit horizontal direction straight away from the nearest hazard whose reach holds <paramref name="at"/> (zero when none; straight back along <paramref name="fallback"/> on top of it).</summary>
    public static Vector2 Away(IReadOnlyList<Hazard> hazards, Vector3 at, Vector2 fallback)
    {
        float best = float.MaxValue; Vector2 away = Vector2.Zero;
        foreach (var h in hazards)
        {
            float d = Vector3.Distance(h.Centre, at);
            if (d >= h.Reach || d >= best) continue;
            best = d;
            var flat = new Vector2(at.X - h.Centre.X, at.Y - h.Centre.Y);
            away = flat.LengthSquared() > 1e-6f ? Vector2.Normalize(flat) : fallback.LengthSquared() > 1e-6f ? Vector2.Normalize(fallback) : new Vector2(0f, -1f);
        }
        return away;
    }

    /// <summary>A view's forward direction on the ground (game space: yaw 0 looks along +Y).</summary>
    public static Vector2 Forward(float yaw) => new(-MathF.Sin(yaw), MathF.Cos(yaw));

    /// <summary>A view's right direction on the ground.</summary>
    public static Vector2 Right(float yaw) => new(MathF.Cos(yaw), MathF.Sin(yaw));

    /// <summary>The input's stick as a ground direction in game space (length as the stick's, at most about 1.4 on a diagonal).</summary>
    public static Vector2 WorldMove(in PlayerInput input) => Right(input.Yaw) * input.MoveX + Forward(input.Yaw) * input.MoveY;

    /// <summary>What <see cref="Hold"/> did.</summary>
    public enum HoldKind
    {
        /// <summary>Nothing: no hazard in the way.</summary>
        None,
        /// <summary>The move (or the jump, dash, hop or charge) would have gone into a hazard: the bot stands (it keeps aiming and firing).</summary>
        Held,
        /// <summary>The bot was inside a hazard: it backs straight out, firing allowed, no jump, charge or melee.</summary>
        BackedOut,
    }

    /// <summary>
    /// The hold on a grounded bot's input (<paramref name="feet"/> and <paramref name="chest"/> its position): inside a hazard it moves straight away from it (the view and fire kept;
    /// sprint, jump, melee and the charge dropped); else a move whose point <see cref="LookAhead"/> s on (at <see cref="MoveSpeed"/> m/s for a full stick, half again sprinting) would
    /// be inside one, or that crosses one on the way, stops (the stick centred, jump, sprint and the heavy strike dropped). With no hazard (or none in the way) the input is unchanged.
    /// </summary>
    public static PlayerInput Hold(in PlayerInput input, Vector3 feet, Vector3 chest, IReadOnlyList<Hazard> hazards, out HoldKind kind)
    {
        kind = HoldKind.None;
        if (hazards.Count == 0) return input;
        var forward = Forward(input.Yaw);
        if (Inside(hazards, chest))
        {
            var away = Away(hazards, chest, -forward);
            kind = HoldKind.BackedOut;
            var keep = input.Buttons & ~(InputButtons.Jump | InputButtons.Sprint | InputButtons.Melee | InputButtons.MeleeHeavy);
            return input with { MoveX = Vector2.Dot(away, Right(input.Yaw)), MoveY = Vector2.Dot(away, forward), Buttons = keep };
        }
        var move = WorldMove(input);
        bool jumps = (input.Buttons & InputButtons.Jump) != 0;
        bool charges = (input.Buttons & InputButtons.MeleeHeavy) != 0 && (input.Buttons & InputButtons.Sprint) != 0;
        if (move.LengthSquared() < 1e-4f && !jumps && !charges) return input;
        float speed = MoveSpeed * ((input.Buttons & InputButtons.Sprint) != 0 || jumps ? 1.5f : 1f);
        var heading = move.LengthSquared() >= 1e-4f ? move : forward;   // a jump or charge without a stick goes the way the bot faces
        var step = new Vector3(heading.X, heading.Y, 0f) * (speed * LookAhead);
        if (!PathCrosses(hazards, chest, chest + step)) return input;
        kind = HoldKind.Held;
        return input with { MoveX = 0f, MoveY = 0f, Buttons = input.Buttons & ~(InputButtons.Jump | InputButtons.Sprint | InputButtons.MeleeHeavy) };
    }

    /// <summary>
    /// Self-splash shots (the plasma cannon, a plasma charge, the launcher): whether a shot whose <paramref name="projectile"/> bursts at <paramref name="impact"/> (the first level
    /// contact on the line to the target, else the target's chest) would catch the bot: within its damaging blast radius + <see cref="Margin"/> of <paramref name="ownChest"/>.
    /// False for a shot without a damaging blast (hit-scan, a plain bolt).
    /// </summary>
    public static bool BurstsTooClose(ProjectileSpec? projectile, Vector3 ownChest, Vector3 impact) =>
        SafetyDistance(projectile) is var safety and > 0f && Vector3.Distance(ownChest, impact) < safety;

    /// <summary>
    /// Remote detonation (the grenade launcher's waiting rounds): false while the bot's chest is within <paramref name="safety"/> (the round's blast radius + margin) of any of
    /// <paramref name="ownRounds"/>.
    /// </summary>
    public static bool MayDetonate(IEnumerable<Vector3> ownRounds, Vector3 ownChest, float safety)
    {
        foreach (var r in ownRounds) if (Vector3.DistanceSquared(r, ownChest) < safety * safety) return false;
        return true;
    }
}
