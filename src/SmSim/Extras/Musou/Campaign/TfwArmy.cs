using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The battle flow's numbers (expansion S6, docs/musou-expansion.md "S6 design"; the brief's where it gives one, every other value ours, an estimate; spec "Musou Mode: the battle flow"). A change here
/// raises <see cref="TfwRules.Version"/>. Distances in metres (flat), times in real seconds, speeds in m/s, counts in men.
/// </summary>
public static class TfwArmyRules
{
    // Levels: the brief's real within 90 m of any human, aggregate again past 120 m; since 2026-11-09 (owner: "increase the distance for active ork behavior and rendering - 300m") real within 300 m,
    // aggregate past 330 m (ours: the brief's 30 m margin). The server's SM_TFW_OLD_ARMY_RANGE=1 restores 90 / 120 (test aid, the before of a measurement).
    public static readonly bool OldRange = Environment.GetEnvironmentVariable("SM_TFW_OLD_ARMY_RANGE") == "1";
    public static float NearMetres => OldRange ? CoreMetres : 300f;
    public static float FarMetres => OldRange ? CoreFarMetres : 330f;
    /// <summary>
    /// The brief's levels (90 / 120 m), kept for two things: real units nearer any human than <see cref="CoreFarMetres"/> are never folded back to make room for a nearer army (<see cref="NearOrksMost"/> is
    /// unchanged, so at 300 m the cap is full more often: the nearest armies get the real units), and the client leaves a far death's body only within <see cref="CoreMetres"/> (the owner, 2026-11-08).
    /// </summary>
    public const float CoreMetres = 90f, CoreFarMetres = 120f;
    /// <summary>A farther army gives up real units to a nearer one only when it is this much farther (ours: no swap to and fro between armies at about the same distance).</summary>
    public const float RoomMargin = 30f;
    /// <summary>Real units made from the aggregates a tick, over all armies (Orks; Guard squads), and folded back a tick: no frame spawns or removes hundreds.</summary>
    public const int PromotePerTick = 16, PromoteSquadsPerTick = 1, DemotePerTick = 32, DemoteSquadsPerTick = 2;
    /// <summary>
    /// Most real Orks and Guard squads the armies hold at once (the pool's new 512 slots less a margin; GuardSim's 40 squads less the field's 12). The Orks' 480 is 384 since 2026-11-09 (the owner: "reduce the
    /// total amount of orks on the map to 80% of current"; with no aggregate it is also the most Orks all the armies have together).
    /// </summary>
    public const int NearOrksMost = 384, NearSquadsMost = 28;
    /// <summary>
    /// The owner, 2026-11-09: "there are currently on occasion large rectangular formations of orks that move from their points to imperial captured points. they walk through objects, don't interact with the
    /// world or fight. completely remove those orks from musou mode." Those were the Ork armies' aggregates drawn as impostors (900 at most on a client). Since then an Ork army is made of real Orks alone: a
    /// wave musters real Orks at its stronghold up to <see cref="NearOrksMost"/> over all armies (the rest of its dice is never made), they march, fight and die as every Ork does, at any distance, and none is
    /// folded back. The Guard armies keep their aggregates. The server's <c>SM_TFW_OLD_ORK_ARMIES=1</c> (with the client's <c>--musou-old-ork-armies</c> to draw them) restores the aggregates (test aid).
    /// </summary>
    public static readonly bool OldOrkArmies = Environment.GetEnvironmentVariable("SM_TFW_OLD_ORK_ARMIES") == "1";
    /// <summary>Most armies at once.</summary>
    public const int MaxArmies = 40;

    /// <summary>A marching army's real Orks wake when a target comes this near their places (they march past what is not on their way); an attacking army's at the point's own aggro.</summary>
    public const float MarchAggro = 15f, AttackAggro = 30f;
    /// <summary>Aggregate march speeds (under an Ork's run and a Guardsman's walk, so real members keep up).</summary>
    public const float OrkSpeed = 3f, GuardSpeed = 3.6f;
    /// <summary>A marching army waits while the middle of its real Orks is this far behind their places, at most <see cref="WaitMostSeconds"/> at a time; then it creeps on at <see cref="CreepShare"/> of its pace, so a wave whose front is fighting never stalls short of its point (the patrols' rule).</summary>
    public const float WaitBehind = 5f, WaitMostSeconds = 2.5f, CreepShare = 0.6f;
    /// <summary>The routes between the battle's places are searched off the tick from the start and taken this long after it (deterministic, as the patrols').</summary>
    public const float RouteSeconds = 5f;
    public const int RouteExpansions = 200_000;

    // Waves (the brief): peaks every 3-5 min, lulls of 90-150 s; 200 (70 %), 500 (25 %), 1,000 or more (5 %). Since 2026-11-09 80 % of each (the owner: "reduce the total amount of orks on the map to
    // 80% of current"): 160, 400, 800 and up to 160 more; and with no aggregate a wave is what the armies' real cap leaves room for.
    public const float LullMin = 90f, LullMax = 150f, PeakMin = 180f, PeakMax = 300f;
    /// <summary>A wave is seen gathering at its stronghold this long before it leaves (its aggregate grows to its size).</summary>
    public const float MusterSeconds = 30f;
    /// <summary>With no Imperial position to attack a mustered wave waits, looking again this often.</summary>
    public const float WaitRecheck = 5f;
    public const int SmallWave = 160, MediumWave = 400, LargeWave = 800, LargeExtraMost = 160;   // 200, 500, 1000, 200 before 2026-11-09
    public const float SmallShare = 0.70f, MediumShare = 0.25f;
    /// <summary>The two farthest Imperial positions are drawn between (the brief: "random among the two farthest").</summary>
    public const int FarthestDraw = 2;

    // Guardsmen (the brief): from the base every 60 s, split by need; 200 stationed a position at most.
    public const float ReinforceSeconds = 60f;
    public const int ReinforceMen = 60, GarrisonCap = 200;

    // Retreats and rallies (the brief).
    /// <summary>Orks retreat at this share of losses or more with no leader near; Guardsmen at more than it.</summary>
    public const float RetreatLoss = 0.5f;
    public const float LeaderRadius = 100f;
    public const float RallyRadius = 15f, RallyLockSeconds = 60f, NoRetreatSeconds = 30f;

    /// <summary>An army is at a point (it clashes there and counts on it) within its radius and this margin.</summary>
    public const float AtPointMargin = 10f;
    /// <summary>The base's radius for a wave attacking it (the owner's "base is a target", 2026-11-08; ours: the landing zone's hold radius, doubled for the squads round it).</summary>
    public const float BaseRadius = 20f;

    /// <summary>The share of a wave's head count that is elite: the battle's (run progress) share.</summary>
    public static int WaveSize(float roll, float extraRoll) =>
        roll < SmallShare ? SmallWave : roll < SmallShare + MediumShare ? MediumWave : LargeWave + (int)(Math.Clamp(extraRoll, 0f, 1f) * LargeExtraMost);
}

/// <summary>Whose an army is.</summary>
public enum TfwArmySide : byte { Ork = 0, Guard = 1 }

/// <summary>What an army is doing: gathering (an Ork wave before it leaves), on its way, attacking its point, holding a point, or going home.</summary>
public enum TfwArmyOrder : byte { Muster = 0, March = 1, Attack = 2, Hold = 3, Retreat = 4 }

/// <summary>
/// An army's slots (shared by the server and the client, so a promotion spawns where the impostor stood): the anchor is the middle of the front rank, ranks go back along the facing. Orks stand in rows of
/// the army's width; Guardsmen in blocks of two rows of five, one block a squad, laid out as <see cref="GuardSim"/> lays out a squad.
/// </summary>
public static class TfwArmyLayout
{
    public const float OrkRow = 1.6f, OrkFile = 1.4f, GuardSpacing = 1.5f;
    public const int GuardBlock = 5;

    /// <summary>An army's width (files) for <paramref name="planned"/> men: fixed for its life so its slots never shift.</summary>
    public static int Width(TfwArmySide side, int planned)
    {
        int w = (int)MathF.Round(MathF.Sqrt(Math.Max(1, planned)) * 1.2f);
        return side == TfwArmySide.Ork ? Math.Clamp(w, 6, 36) : Math.Clamp((w + 2) / GuardBlock * GuardBlock, GuardBlock, 30);
    }

    /// <summary>Slot <paramref name="slot"/>'s place: metres right of the anchor, and metres behind it.</summary>
    public static Vector2 Offset(TfwArmySide side, int width, int slot)
    {
        width = Math.Max(1, width);
        if (side == TfwArmySide.Ork) return new Vector2((slot % width - (width - 1) * 0.5f) * OrkFile, slot / width * OrkRow);
        int blocks = Math.Max(1, width / GuardBlock), squad = slot / GuardSim.SquadSize, man = slot % GuardSim.SquadSize;
        int bx = squad % blocks, by = squad / blocks;
        return new Vector2((bx * GuardBlock + man % GuardBlock - (blocks * GuardBlock - 1) * 0.5f) * GuardSpacing, (by * 2 + man / GuardBlock) * GuardSpacing);
    }

    /// <summary>The forward of a yaw (the game's: yaw 0 looks along +Y).</summary>
    public static Vector2 Forward(float yaw) => new(-MathF.Sin(yaw), MathF.Cos(yaw));

    /// <summary>Slot <paramref name="slot"/> in the world, for an army anchored at <paramref name="anchor"/> facing <paramref name="yaw"/> (the anchor's height).</summary>
    public static Vector3 Slot(TfwArmySide side, int width, Vector3 anchor, float yaw, int slot)
    {
        var o = Offset(side, width, slot);
        var forward = Forward(yaw);
        var right = new Vector2(forward.Y, -forward.X);
        return anchor + new Vector3(right * o.X - forward * o.Y, 0f);
    }

    /// <summary>The centre of Guard squad block <paramref name="squad"/> (its men's middle, as <see cref="GuardSim.SpawnSquad"/> takes it).</summary>
    public static Vector3 SquadCentre(int width, Vector3 anchor, float yaw, int squad)
    {
        var a = Slot(TfwArmySide.Guard, width, anchor, yaw, squad * GuardSim.SquadSize);
        var b = Slot(TfwArmySide.Guard, width, anchor, yaw, squad * GuardSim.SquadSize + GuardSim.SquadSize - 1);
        return (a + b) * 0.5f;
    }

    /// <summary>Metres from the front rank to the rank of slot <paramref name="slots"/> − 1.</summary>
    public static float Depth(TfwArmySide side, int width, int slots) => slots <= 0 ? 0f : Offset(side, width, slots - 1).Y;

    /// <summary>The middle of an army of <paramref name="slots"/> slots.</summary>
    public static Vector3 Middle(TfwArmySide side, int width, Vector3 anchor, float yaw, int slots) => anchor - new Vector3(Forward(yaw) * (Depth(side, width, slots) * 0.5f), 0f);

    /// <summary>The army's footprint: the radius round its middle its slots fill.</summary>
    public static float Radius(TfwArmySide side, int width, int slots)
    {
        float across = (Math.Min(width, Math.Max(1, slots)) - 1) * 0.5f * (side == TfwArmySide.Ork ? OrkFile : GuardSpacing);
        float along = Depth(side, width, slots) * 0.5f;
        return MathF.Sqrt(across * across + along * along) + 1f;
    }

    /// <summary>Whether Ork slot <paramref name="slot"/> is an elite's at elite share <paramref name="share"/> (a low-discrepancy sequence: every share of the slots, everywhere in the army).</summary>
    public static bool EliteSlot(int slot, float share) => share > 0f && (slot * 0.6180339887498949 + 0.5) % 1.0 < share;

    /// <summary>What stands in Ork slot <paramref name="slot"/>: an elite ('Ard Boy three times in four, else a Nob) or fodder (Boyz and Shootas by the battle's shares).</summary>
    public static MusouEnemyType OrkType(int slot, float eliteShare)
    {
        if (EliteSlot(slot, eliteShare)) return slot * 0.7548776662466927 % 1.0 < TfwBattleRules.EliteArds ? MusouEnemyType.ArdBoy : MusouEnemyType.Nob;
        return slot * 0.7548776662466927 % 1.0 < TfwBattleRules.FodderBoyz ? MusouEnemyType.Boy : MusouEnemyType.ShootaBoy;
    }

    /// <summary>Elite slots in <c>[from, from + count)</c>.</summary>
    public static int EliteSlots(int from, int count, float share)
    {
        int n = 0;
        for (int s = from; s < from + count; s++) if (EliteSlot(s, share)) n++;
        return n;
    }
}

/// <summary>A way an army walks: points with their running length (flat), sampled by distance along it.</summary>
public sealed class TfwWay
{
    readonly List<Vector3> _points = new();
    readonly List<float> _length = new();

    public TfwWay() { }
    public TfwWay(IEnumerable<Vector3> points) { foreach (var p in points) Add(p); }

    public int Count => _points.Count;
    public float Length => _length.Count == 0 ? 0f : _length[^1];
    public Vector3 First => _points[0];
    public Vector3 Last => _points[^1];
    public IReadOnlyList<Vector3> Points => _points;

    public void Add(Vector3 p)
    {
        if (_points.Count > 0 && Vector3.DistanceSquared(_points[^1], p) < 1e-6f) return;
        _length.Add(_points.Count == 0 ? 0f : _length[^1] + Vector2.Distance(new Vector2(_points[^1].X, _points[^1].Y), new Vector2(p.X, p.Y)));
        _points.Add(p);
    }

    /// <summary>The point <paramref name="s"/> metres along (clamped to the ends) and the way's flat direction there (unit; +Y for a single point).</summary>
    public (Vector3 At, Vector2 Way) Sample(float s)
    {
        if (_points.Count == 0) return (Vector3.Zero, Vector2.UnitY);
        if (_points.Count == 1) return (_points[0], Vector2.UnitY);
        s = Math.Clamp(s, 0f, Length);
        int hi = _length.BinarySearch(s);
        if (hi < 0) hi = ~hi;
        hi = Math.Clamp(hi, 1, _points.Count - 1);
        int lo = hi - 1;
        float span = _length[hi] - _length[lo], t = span > 1e-5f ? (s - _length[lo]) / span : 0f;
        var way = new Vector2(_points[hi].X - _points[lo].X, _points[hi].Y - _points[lo].Y);
        float w = way.Length();
        return (Vector3.Lerp(_points[lo], _points[hi], t), w > 1e-5f ? way / w : Vector2.UnitY);
    }

    /// <summary>The part of this way from <paramref name="from"/> to <paramref name="to"/> metres along (backwards when <paramref name="to"/> is less).</summary>
    public TfwWay Slice(float from, float to)
    {
        from = Math.Clamp(from, 0f, Length); to = Math.Clamp(to, 0f, Length);
        var part = new TfwWay();
        part.Add(Sample(from).At);
        if (to >= from) { for (int k = 0; k < _points.Count; k++) if (_length[k] > from && _length[k] < to) part.Add(_points[k]); }
        else for (int k = _points.Count - 1; k >= 0; k--) if (_length[k] < from && _length[k] > to) part.Add(_points[k]);
        part.Add(Sample(to).At);
        return part;
    }

    /// <summary>This way, then <paramref name="next"/> (joined at its first point).</summary>
    public TfwWay Then(TfwWay next)
    {
        var joined = new TfwWay(_points);
        foreach (var p in next._points) joined.Add(p);
        return joined;
    }

    public TfwWay Reversed()
    {
        var back = new TfwWay();
        for (int k = _points.Count - 1; k >= 0; k--) back.Add(_points[k]);
        return back;
    }
}

/// <summary>
/// One army of the battle flow (expansion S6): a formation counted as an aggregate (<see cref="Far"/>, the men no client sees as real units) and its real members, which are units of the pools
/// (Orks of <see cref="MusouSim"/>, Guard squads of <see cref="GuardSim"/>). Its count, start, elites, goal, timers and place belong to it, so a promotion or a demotion moves men between the two parts
/// and nothing else. Slots: the real members fill <c>[0, SlotBase)</c> (holes where they died), the aggregate stands in <c>[SlotBase, SlotBase + round(Far))</c>.
/// </summary>
public sealed class TfwArmy
{
    public TfwArmy(byte id, TfwArmySide side, TfwArmyOrder order, int width, float eliteShare)
    {
        Id = id; Side = side; Order = order; Width = width; EliteShare = side == TfwArmySide.Ork ? Math.Clamp(eliteShare, 0f, 1f) : 0f;
    }

    public byte Id { get; }
    public TfwArmySide Side { get; }
    public TfwArmyOrder Order { get; set; }
    /// <summary>Files abreast (fixed: the slots never shift).</summary>
    public int Width { get; }
    /// <summary>The share of its slots that were elites at its muster (Orks).</summary>
    public float EliteShare { get; }
    /// <summary>The aggregate's share of elites now: what stands in its far slots (the client draws the same).</summary>
    public float FarEliteShare => FarCount > 0 ? Math.Clamp(FarElites / FarCount, 0f, 1f) : 0f;
    /// <summary>Whether it still has a leader of its own, real or in the aggregate.</summary>
    public bool Led => RealElites > 0 || FarElites >= 0.5f;
    /// <summary>Its goal: a position (0 to 2), <see cref="TfwBattle.LandingGoal"/> for the base, or an Ork stronghold (<see cref="TfwBattle.StrongholdGoal"/>).</summary>
    public int Goal { get; set; } = -1;
    /// <summary>Where it came from (a place index of the battle's routes).</summary>
    public int From { get; set; } = -1;
    /// <summary>The men counted as an aggregate (fractional: the far fight takes fractions).</summary>
    public float Far { get; set; }
    /// <summary>The first slot of the aggregate part.</summary>
    public int SlotBase { get; set; }
    /// <summary>Its strength at its start (a wave's departure, a reinforcement's leaving, a garrison's last fill or rally): losses are counted of it.</summary>
    public int Start { get; set; }
    public float Cohesion { get; set; } = 1f;
    /// <summary>Whether any human is near it (the hysteresis' state).</summary>
    public bool Near { get; set; }
    public Vector3 Anchor { get; set; }
    public float Yaw { get; set; }
    public TfwWay? Way { get; set; }
    public float Along { get; set; }
    /// <summary>The leash group its real Orks stand under (a new one after each release).</summary>
    public int Group { get; set; }
    /// <summary>Its real Orks (leashed or let loose), and its Guard squads.</summary>
    public List<ushort> Orks { get; } = new();
    /// <summary>The slot of each of <see cref="Orks"/> (same order).</summary>
    public List<int> OrkSlots { get; } = new();
    public List<int> Squads { get; } = new();
    /// <summary>The block of each of <see cref="Squads"/> and its living men when last seen (same order).</summary>
    public List<int> SquadBlocks { get; } = new();
    public List<int> SquadLiving { get; } = new();
    /// <summary>The aggregate's elites (Orks; fractional as <see cref="Far"/>).</summary>
    public float FarElites { get; set; }
    /// <summary>Real elites alive now (kept by the battle each tick).</summary>
    public int RealElites { get; set; }
    /// <summary>The distance along <see cref="Way"/> at which it passes <see cref="From"/> (a redirect turns back to it).</summary>
    public float FromAlong { get; set; }
    /// <summary>The place its way ends at (a route index of the battle's places).</summary>
    public int To { get; set; } = -1;
    /// <summary>The point it last defended or went to (a Guard army: a rally sends it back there when it is still Imperial).</summary>
    public int Post { get; set; } = -1;
    /// <summary>An attacking wave closing on its point: metres it still walks on past the way's end, so its middle stands on the point.</summary>
    public float Closing { get; set; }
    /// <summary>The battle's tick from which it may be rallied again, and from which it may retreat again.</summary>
    public int RallyReady { get; set; }
    public int RetreatReady { get; set; }
    /// <summary>Times it was rallied (tests).</summary>
    public int Rallies { get; set; }
    /// <summary>Ticks it has waited for its real ranks in a row.</summary>
    public int WaitTicks { get; set; }
    /// <summary>A wave's departure tick (Muster: when it leaves).</summary>
    public int Leaves { get; set; }
    /// <summary>The wave's planned size while it musters.</summary>
    public int Planned { get; set; }
    /// <summary>Men the muster has brought so far (real Ork armies: what was made real or could not be, so a full cap ends the muster's growth instead of counting it again).</summary>
    public float Mustered { get; set; }
    /// <summary>Men lost so far (aggregate and real).</summary>
    public int Lost { get; set; }
    /// <summary>Real men alive now (kept by the battle each tick).</summary>
    public int Real { get; set; }
    /// <summary>The aggregate's men, whole.</summary>
    public int FarCount => (int)MathF.Round(Math.Max(0f, Far));
    public int Count => FarCount + Real;
    /// <summary>Slots it fills (real high water, then the aggregate).</summary>
    public int Slots => SlotBase + FarCount;
    public Vector3 Middle => TfwArmyLayout.Middle(Side, Width, Anchor, Yaw, Math.Max(1, Slots));
    public float Radius => TfwArmyLayout.Radius(Side, Width, Math.Max(1, Slots));
    public float LossShare => Start > 0 ? Math.Clamp((Start - Count) / (float)Start, 0f, 1f) : 0f;
}
