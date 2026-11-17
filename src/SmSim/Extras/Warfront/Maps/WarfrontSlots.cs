using System.Globalization;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Warfront.Maps;

/// <summary>
/// One Warfront lane battlefield (plan section 2, slots 1 to 9): the map name, the lane and node it stands for, the pilot's band profile it is built from (node 1 wasteland, 2 outskirts, 3 urban core, on all three lanes) and the
/// layout seed pinned in <see cref="WarfrontSlots"/>. Hub plus ONE band: the world of the name is <c>AshfallFront.Plan(Band, Seed)</c> through <c>AshfallDraft5Pass.Run</c>, composed with the hub by <c>TfwMap.Compose</c>.
/// </summary>
public sealed record WarfrontSlot(int Number, string Name, TfwLane Lane, int Node, int Band, uint Seed)
{
    /// <summary>The pilot band layout the slot's level carries (<c>AshfallLayout.FrontWasteland</c>, <c>FrontOutskirts</c>, <c>FrontCore</c>).</summary>
    public AshfallLayout Layout => AshfallFront.LayoutOf(Band);

    /// <summary>The band profile's name ("wasteland", "outskirts", "urban core"), the pilot's object unchanged.</summary>
    public string Profile => AshfallBandProfile.Front[Band].Name;

    /// <summary>The seed the slot started its walk from (<see cref="WarfrontSlots.CandidateSeed"/>).</summary>
    public uint Candidate => WarfrontSlots.CandidateSeed(Lane, Node);
}

/// <summary>A slot's build: the plan (gate A passed on its frame) and the level with its dressing (gate B passed on its final collision); a gate failure throws, so a build is a pass.</summary>
public sealed record WarfrontBuild(WarfrontSlot Slot, AshfallFieldPlan Plan, AshfallDraft5 Draft)
{
    public AshfallLevel Level => Draft.Level;
    public AshfallDressing Dressing => Draft.Dressing;
    /// <summary>Gate B's rules as the pass logged them ("front gate B rule [pass] ..."), in order.</summary>
    public IReadOnlyList<string> GateB => [.. Draft.Dressing.Flags.Where(f => f.StartsWith("front gate B rule ", StringComparison.Ordinal)).Select(f => f["front gate B rule ".Length..])];
}

/// <summary>
/// The nine lane slots of the Warfront campaign map set (plan section 2; slice W2): a pinned table (map name, lane, node, band, seed), not a formula at run time. The candidate seed of a slot is
/// <c>16 + 100*lane + 10*(node - 1)</c>; when the candidate fails gate A or B (after the pilot's own eight deterministic retries) the walk goes +1 until a seed passes, and the result is pinned in <see cref="All"/> (<see cref="Walk"/> reproduces
/// it; a test asserts the table equals the walk). The generator is the pilot's, unchanged (<c>AshfallFront.Plan</c>, <c>AshfallDraft5Pass.Run</c>, the three band profiles): nothing is rewritten and no older draft is used. Slots 1 to 3 are lane A.
/// </summary>
public static class WarfrontSlots
{
    /// <summary>Raised with any change of the map set (a name, a seed, the node-to-band rule); the lane slots' own version (plan section 1, "Versions"). Starts at 1.</summary>
    public const uint Version = 1;

    /// <summary>The map names' prefix; the HQ and fortress slots (W13, W14) share it.</summary>
    public const string Prefix = "wf_";

    /// <summary>The most seeds the walk tries from the candidate before it gives up (ours).</summary>
    public const int WalkLimit = 64;

    /// <summary>The candidate seed of a slot (plan section 2: lane A 16/26/36, B 116/126/136, C 216/226/236).</summary>
    public static uint CandidateSeed(TfwLane lane, int node) => (uint)(16 + 100 * (int)lane + 10 * (node - 1));

    /// <summary>The map name of a lane and node: <c>wf_a1</c> to <c>wf_c3</c>.</summary>
    public static string NameOf(TfwLane lane, int node) => string.Create(CultureInfo.InvariantCulture, $"{Prefix}{char.ToLowerInvariant(lane.ToString()[0])}{node}");

    /// <summary>The band of a node on every lane: node 1 is the wasteland (band 0), 2 the outskirts, 3 the urban core.</summary>
    public static int BandOfNode(int node) => node - 1;

    /// <summary>The pinned table: nine slots in the plan's order (lane A nodes 1 to 3, then B, then C). Seeds are the walk's results.</summary>
    public static IReadOnlyList<WarfrontSlot> All { get; } =
    [
        Slot(1, TfwLane.A, 1, 16), Slot(2, TfwLane.A, 2, 26), Slot(3, TfwLane.A, 3, 36),
        Slot(4, TfwLane.B, 1, 116), Slot(5, TfwLane.B, 2, 126), Slot(6, TfwLane.B, 3, 136),
        Slot(7, TfwLane.C, 1, 216), Slot(8, TfwLane.C, 2, 226), Slot(9, TfwLane.C, 3, 236),
    ];

    static WarfrontSlot Slot(int number, TfwLane lane, int node, uint seed) => new(number, NameOf(lane, node), lane, node, BandOfNode(node), seed);

    static readonly Lazy<global::SmSim.Extras.Warfront.Fortress.IWarfrontArena> FortressArena = new(() => global::SmSim.Extras.Warfront.Fortress.WarfrontFortressMap.Build());

    /// <summary>The arena of slot 11, the Ork fortress (W14b): the one line to change when a handmade map (a Mapmaker export that supplies <see cref="global::SmSim.Extras.Warfront.Fortress.IWarfrontArena"/>) replaces the generated one. Built once, deterministic.</summary>
    public static global::SmSim.Extras.Warfront.Fortress.IWarfrontArena Fortress => FortressArena.Value;

    /// <summary>The slot of a map name, or null (the HQ and the fortress are not lane slots).</summary>
    public static WarfrontSlot? ByName(string? name) => name is null ? null : All.FirstOrDefault(s => s.Name == name);

    /// <summary>Whether <paramref name="name"/> is one of the nine lane maps.</summary>
    public static bool IsLaneMap(string? name) => ByName(name) is not null;

    /// <summary>The slot of a lane's node (1 to 3).</summary>
    public static WarfrontSlot Of(TfwLane lane, int node) => All.First(s => s.Lane == lane && s.Node == node);

    static readonly WarfrontBuild?[] Memo = new WarfrontBuild?[9];
    static readonly object MemoGate = new();

    /// <summary>The slot's build, once per process until <see cref="Release"/> (a tool run over all nine would otherwise hold nine levels).</summary>
    public static WarfrontBuild Build(WarfrontSlot slot)
    {
        lock (MemoGate)
            return Memo[slot.Number - 1] ??= BuildFresh(slot.Band, slot.Seed, slot);
    }

    /// <summary>Drops the process's copy of a slot's build (the next <see cref="Build"/> generates again).</summary>
    public static void Release(WarfrontSlot slot) { lock (MemoGate) Memo[slot.Number - 1] = null; }

    /// <summary>An uncached build of a band and seed (the walk's step): gate A inside <c>AshfallFront.Plan</c>, then the kit pass with gate B; <see cref="AshfallGateException"/> on a failure of either.</summary>
    public static WarfrontBuild BuildFresh(int band, uint seed, WarfrontSlot? slot = null)
    {
        var plan = AshfallFront.Plan(band, seed);
        var draft = AshfallMap.BuildFrontFrom(plan);
        return new WarfrontBuild(slot ?? new WarfrontSlot(0, $"band{band}-seed{seed}", TfwLane.A, band + 1, band, seed), plan, draft);
    }

    /// <summary>
    /// The seed walk of a lane and node: from the candidate, +1 on a gate failure, up to <see cref="WalkLimit"/> steps. Returns the first seed that passes both gates; <paramref name="failures"/> lists each failed seed and why.
    /// </summary>
    public static uint Walk(TfwLane lane, int node, out List<string> failures)
    {
        failures = [];
        uint start = CandidateSeed(lane, node);
        for (uint seed = start; seed < start + WalkLimit; seed++)
        {
            try { BuildFresh(BandOfNode(node), seed); return seed; }
            catch (AshfallGateException e) { failures.Add($"seed {seed}: {e.Message}"); }
        }
        throw new InvalidOperationException($"{NameOf(lane, node)}: no seed from {start} to {start + WalkLimit - 1} passes gates A and B");
    }
}
