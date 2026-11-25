namespace SmSim.Extras.Warfront.Chaos;

/// <summary>Which kind of Warfront battle the breaches are placed for (<see cref="WarfrontBreachRules.Count"/>).</summary>
public enum WarfrontBreachBattle : byte { Lane = 0, Hq = 1, Fortress = 2 }

/// <summary>
/// The warp breaches of a Chaos Warfront battle (Chaos Warfront C4; design "Warp breaches"; added content). Every number is ours unless its line says otherwise (spec "Added content: Warfront Chaos:
/// warp breaches (C4)"); distances in metres, times in real seconds turned into sim ticks at the battle's tick rate. A change here raises <see cref="Version"/> (and <see cref="WarfrontRules.Version"/>).
/// </summary>
public static class WarfrontBreachRules
{
    /// <summary>1: the breaches (C4).</summary>
    public const uint Version = 1;

    /// <summary>A breach opens this long after the landing (the install's <c>chaos_portal</c> "delay to open", 1 s, applied to a spawner: an interpretation).</summary>
    public const float OpenDelaySeconds = 1f;
    /// <summary>Its first squad comes this long after it opens (the install's 5 s build-up core, applied to a spawner: an interpretation).</summary>
    public const float BuildUpSeconds = 5f;
    /// <summary>A squad every this long per open breach (design; ours).</summary>
    public const float SquadSeconds = 20f;
    /// <summary>Daemons in a squad (design; ours).</summary>
    public const int SquadSize = 5;
    /// <summary>A squad comes out on a ring this far from the breach's foot (ours).</summary>
    public const float RingRadius = 2f;
    /// <summary>At most this many of one breach's Daemons alive (design; ours).</summary>
    public const int BreachAliveCap = 10;
    /// <summary>At most this many breach Daemons alive over all the battle's breaches (design; ours). They also count against the battle's own alive cap and the pool's room.</summary>
    public const int AllAliveCap = 30;

    /// <summary>A qualifying detonation closes a breach when it is within this far of the breach's centre horizontally (design: 3 m; the blast's own radius is not added).</summary>
    public const float CloseRadius = 3f;
    /// <summary>... and within this far of it vertically (ours).</summary>
    public const float CloseVertical = 3f;
    /// <summary>The breach's centre stands this high over its foot on the walking graph (ours): the centre the close rule measures from and the sphere's centre.</summary>
    public const float CentreHeight = 1.5f;
    /// <summary>
    /// The breach's body (lead 2026-11-25, open question 1: "a rocket or plasma shot flying through a breach closes it"): a sphere of this radius round the centre that a qualifying projectile of a
    /// human detonates on (ours). Its surface is within <see cref="CloseRadius"/> of the centre, so the detonation closes it.
    /// </summary>
    public const float SphereRadius = 1.5f;

    /// <summary>Placement (design: 40 m from the landing zone, 25 m from every capture point; 30 m apart ours).</summary>
    public const float MinFromLanding = 40f, MinFromPoints = 25f, MinApart = 30f;
    /// <summary>A breach stands on an enemy source, or when the source itself is too near something, on a ring round it this far out (ours: 10 m, then 20 m, 8 spots each).</summary>
    public const float RingStep = 10f;
    public const int Rings = 2, RingSpots = 8;
    /// <summary>A ring spot (Fable review 3; ours): on the source's floor, within this height of its foot; reached on foot from the source within this many times the ring's radius; the walk's search is bounded by this many nodes.</summary>
    public const float RingVertical = 1.5f, RingDetour = 2.5f;
    public const int RingPathExpandedMost = 20_000;
    /// <summary>The most breaches a battle has (the wire's limit, <c>WarfrontChaosWire.MaxBreaches</c>).</summary>
    public const int MaxBreaches = 8;
    /// <summary>The salt of the placement rolls (the battle's own <c>TfwBattle.Roll</c> mixing).</summary>
    public const uint PlacementSalt = 0xB4EA_C000u;

    /// <summary>
    /// How many breaches a battle wants (lead 2026-11-25 on the planner's open question 2; ours): lane node 1, 2, 3 (the run's node numbers) give 1, 2, 3, escalating along the lane; the fortress
    /// approach 2 (none in the plaza); the HQ defence none (its waves are the pressure).
    /// </summary>
    public static int Count(WarfrontBreachBattle battle, int node) => battle switch
    {
        WarfrontBreachBattle.Lane => Math.Clamp(node, 1, 3),
        WarfrontBreachBattle.Fortress => 2,
        _ => 0,
    };

    /// <summary>Ticks of <paramref name="seconds"/> at <paramref name="rate"/> (at least one).</summary>
    public static int Ticks(float seconds, int rate) => Math.Max(1, (int)MathF.Round(seconds * Math.Max(1, rate)));
}
