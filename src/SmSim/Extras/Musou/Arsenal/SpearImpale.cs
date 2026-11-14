using System.Numerics;

namespace SmSim.Extras.Musou.Arsenal;

/// <summary>
/// Musou's power spear (W3; docs/new-weapons-design.md section 6; the owner approved the design 2026-11-10: "slower than hammer but much wider reach", "impaling dive plus wide sweeping swings"). Added content,
/// Musou only. Every number is ours (kind: estimate or ours, confidence low), to check in a playtest; they are in the refine register.
/// <para>The spear is the thunder hammer's combo slowed to <see cref="Tempo"/> (<see cref="FromHammer"/>): every timing of a step divided by it, the damage of the swings times <see cref="DamageScale"/>, the slam
/// and the charge as the hammer's. Its sweeps are a wide, long flat cone (<see cref="ReachMetres"/>, <see cref="HalfAngleDegrees"/> each side, at most <see cref="MostOrks"/> Orks, through <c>World.ArsenalCleave</c>), and its
/// jump-pack dive ends in an impale (<see cref="ImpaleDamage"/> to the enemy nearest the landing within <see cref="ImpaleReach"/>, <c>World.ArsenalJump</c>) on top of the pack's own blast. It takes the hammer's
/// jump-pack rules (<see cref="ArsenalRules.WieldsHammer"/>).</para>
/// </summary>
public static class SpearRules
{
    /// <summary>The hammer's clips at this rate (D14: slower than the hammer; 0.85 an estimate): every step's timing is the hammer's divided by it. Also the client's clip rate (<c>WeaponMap.ClipSpeed</c>).</summary>
    public const float Tempo = 0.85f;

    /// <summary>Reach of a sweep, metres from the feet (D14: much wider reach; the owner 2026-11-10: "Wider and longer", 15 m, was 12; the hammer's war cleave is about 8.8 m).</summary>
    public const float ReachMetres = 15f;

    /// <summary>A sweep covers this many degrees either side of the facing (300 in all, nearly a full circle; owner 2026-11-10: "Wider and longer", was 120 each side; the hammer's war cleave is 120 each side).</summary>
    public const float HalfAngleDegrees = 150f;

    /// <summary>The most Orks one sweep strikes, nearest first (the hammer's is the war's 40; the spear's is a crowd cap of its own, estimate).</summary>
    public const int MostOrks = MusouSim.Capacity;   // no cap (owner, 2026-11-14: "All orks within the cleave range should always be hit no matter how many"); was 12

    /// <summary>The swings' damage as a share of the hammer's (150 against 200 for a light swing; the heavy sweeps the same share; the slam is the hammer's): an estimate.</summary>
    public const float DamageScale = 0.75f;

    /// <summary>The impale: damage to the enemy nearest the landing (a Nob's whole health; an estimate, D18) and how near it must be (metres).</summary>
    public const float ImpaleDamage = 600f, ImpaleReach = 2f;

    /// <summary>The cone's least cosine (<see cref="HalfAngleDegrees"/>).</summary>
    public static float MinCos => MathF.Cos(HalfAngleDegrees * MathF.PI / 180f);

    // ---- the impale as the clients learn of it (owner via the lead, 2026-11-11: the point-down impale pose only when the landing actually impaled an Ork) ----
    // No new message: the server's impale is a melee hit on the Ork (a Musou Hit event with the melee marker, its player the spear's) at the landing's tick, and a kill also a melee Death; the
    // pack's own blast marks its hits as a body hit (marker 1) and its kills as a blast, and nothing else of that player's is a melee hit within a pound's landing (no strike can run from the dive
    // to the end of the recovery), so either event within ImpaleWindowTicks of the landing is the impale. The client predicts its own landing (an Ork alive within ImpaleReach as it sees them)
    // until the server's events for the landing's ticks must have come, then follows them (Musou Mode only; presentation, never a rule).

    /// <summary>The marker of the impale's Hit event: the melee hit's (<c>MusouEvent</c>: 1 body, 2 head, 3 melee).</summary>
    public const byte ImpaleMarker = 3;
    /// <summary>An impale event this many server ticks either side of the landing's tick is the landing's (ours: the own landing is predicted and may land a tick or two from the server's; a strike before the jump is 15 ticks and more away).</summary>
    public const uint ImpaleWindowTicks = 6;
    /// <summary>Once the client has the server's snapshot this many ticks past the landing's, the landing's events have come or never will (ours: an event goes out with its own tick's snapshot and twice after).</summary>
    public const uint ImpaleConfirmTicks = 4;

    /// <summary>Whether <paramref name="e"/> shows <paramref name="player"/>'s spear impaling an Ork: his melee-marker hit, or a melee kill of his (the impale's 600 kills most Orks).</summary>
    public static bool ImpaleEvidence(in MusouEvent e, int player) =>
        player > 0 && e.Player == player && (e.Kind == MusouEventKind.Hit && e.Value == ImpaleMarker || e.Kind == MusouEventKind.Death && e.Value == (byte)MusouDeathKind.Melee);

    /// <summary>Whether an event of server tick <paramref name="eventTick"/> belongs to the landing at <paramref name="landingTick"/> (within <see cref="ImpaleWindowTicks"/> either side).</summary>
    public static bool NearLanding(uint eventTick, uint landingTick) => eventTick + ImpaleWindowTicks >= landingTick && eventTick <= landingTick + ImpaleWindowTicks;

    /// <summary>
    /// Whether the landing at <paramref name="landingTick"/> shows the impale: the server's evidence <paramref name="seen"/>; without it, the client's <paramref name="predicted"/> until the
    /// snapshots have reached <see cref="ImpaleConfirmTicks"/> past the landing (<paramref name="newestTick"/>), then not.
    /// </summary>
    public static bool ShowsImpale(bool seen, bool predicted, uint newestTick, uint landingTick) => seen || predicted && newestTick < landingTick + ImpaleConfirmTicks;

    /// <summary>The client's guess at its own landing: an enemy alive within <see cref="ImpaleReach"/> of <paramref name="landing"/> (flat) and <paramref name="verticalReach"/> above or below, as the server's impale looks (before the pack's blast, which the client cannot foresee).</summary>
    public static bool ImpaleLikely(Vector3 landing, IEnumerable<Vector3> enemies, float verticalReach)
    {
        foreach (var e in enemies)
        {
            var d = e - landing;
            if (MathF.Abs(d.Z) <= verticalReach && d.X * d.X + d.Y * d.Y <= ImpaleReach * ImpaleReach) return true;
        }
        return false;
    }

    /// <summary>Whether <paramref name="state"/> has the power spear chosen in its melee slot (<paramref name="config"/>'s class lists carry it only in a Musou match).</summary>
    public static bool Wields(SimConfig config, PlayerState state) => IsSpear(MeleeWargearOf(config, state));

    /// <summary>Whether <paramref name="wargear"/> names the power spear (case ignored; an arsenal name of ours, which no install file carries).</summary>
    public static bool IsSpear(string? wargear) => wargear is not null && ArsenalRules.PowerSpear.Equals(wargear, StringComparison.OrdinalIgnoreCase);

    /// <summary>The wargear name of the melee weapon <paramref name="state"/> has chosen (null: a class with no melee choices, or a combo with no name).</summary>
    public static string? MeleeWargearOf(SimConfig config, PlayerState state)
    {
        int i = Loadouts.MeleeChoiceOf(config, state);
        return i >= 0 && Loadouts.ClassOf(config, state.Class) is { } loadout ? loadout.MeleeChoices[i].Wargear : null;
    }

    /// <summary>
    /// The sweep's volume for a melee weapon named <paramref name="wargear"/>: for the spear the whole reach <see cref="ReachMetres"/> (from the feet; the war cleave's scale is not applied on top),
    /// <see cref="HalfAngleDegrees"/> each side and at most <see cref="MostOrks"/> Orks, and true; any other weapon leaves the three as they are and gives false. The one function both the server's
    /// cleave (<c>World.ArsenalCleave</c>) and the client's drawn swing arc (<c>MusouCleaveShape.ArsenalShape</c>, owner 2026-11-10: the line matches the hitbox) read, so they cannot drift apart.
    /// </summary>
    public static bool Sweep(string? wargear, ref float reach, ref float halfAngleDegrees, ref int most)
    {
        if (!IsSpear(wargear)) return false;
        reach = ReachMetres;
        halfAngleDegrees = HalfAngleDegrees;
        most = MostOrks;
        return true;
    }

    /// <summary>The spear's combo (named <paramref name="wargear"/>) from the hammer's: slowed to <see cref="Tempo"/>, the swings' damage times <see cref="DamageScale"/>. Perk variants follow.</summary>
    public static MeleeCombo FromHammer(MeleeCombo hammer, string wargear) => Convert(hammer) with { Wargear = wargear };

    static MeleeCombo Convert(MeleeCombo c) => c with
    {
        Steps = c.Steps.Select(s => Slow(s, light: true)).ToArray(),
        Heavy = c.Heavy.Select(s => Slow(s, light: false)).ToArray(),
        Variants = c.Variants.Select(v => v with { Spec = Convert(v.Spec) }).ToArray(),
    };

    /// <summary>One step of the hammer at the spear's tempo (every time in seconds divided by <see cref="Tempo"/>; a slam keeps the hammer's damage and blast).</summary>
    public static MeleeStep Slow(MeleeStep s, bool light)
    {
        static float T(float t) => float.IsFinite(t) ? t / Tempo : t;
        float scale = s.Stomp is not null ? 1f : DamageScale;   // a step that sets off a blast is the slam: the hammer's
        var slow = s with
        {
            Duration = T(s.Duration), HitTime = T(s.HitTime), ComboOpen = T(s.ComboOpen), ComboClose = T(s.ComboClose), RangedAt = T(s.RangedAt), MoveAt = T(s.MoveAt),
            Damage = s.Damage * scale, SecondaryDamage = s.SecondaryDamage * scale, ShieldDamage = s.ShieldDamage * scale, SecondaryShieldDamage = s.SecondaryShieldDamage * scale,
            NoReactFrom = T(s.NoReactFrom), NoReactTo = T(s.NoReactTo),
            YComboOpen = T(s.YComboOpen), YComboClose = T(s.YComboClose),
            StringResetAt = T(s.StringResetAt), EvadeAt = T(s.EvadeAt),
        };
        if (s.Steers)
            // The turn is the same 185 degrees over the longer window: the rate slows with the clip.
            slow = slow with { SteerFrom = T(s.SteerFrom), SteerTo = T(s.SteerTo), SteerRate = s.SteerRate * Tempo };
        if (s.Reach is { } r)
            slow = slow with { Reach = r with { AlignFrom = T(r.AlignFrom), AlignTo = T(r.AlignTo), DistanceFrom = T(r.DistanceFrom), DistanceTo = T(r.DistanceTo) } };
        return slow;
    }
}

/// <summary>W3's part of the arsenal's name rules.</summary>
public static partial class ArsenalRules
{
    static partial void SpearWieldsHammer(string wargear, ref bool hammer)
    {
        if (PowerSpear.Equals(wargear, StringComparison.OrdinalIgnoreCase)) hammer = true;
    }
}
