using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Extras.Musou.Campaign;

/// <summary>One impostor of an army's aggregate: a stable key (its army and slot), what it is, where it stands and whether its army marches.</summary>
public readonly record struct TfwImpostor(int Key, TfwArmySide Side, MusouEnemyType Type, Vector3 Position, float Yaw, bool Moving);

/// <summary>A death the client draws where an aggregate's slot went (a far clash's losses: no real unit died).</summary>
public readonly record struct TfwImpostorDeath(int Key, TfwArmySide Side, MusouEnemyType Type, Vector3 Position, float Yaw);

/// <summary>The client's impostors' numbers (expansion S6; ours). Distances in metres, rates per second.</summary>
public static class TfwImpostorRules
{
    /// <summary>Most impostors drawn at once (nearest armies first): the crowd's vertex-animation tier carries them.</summary>
    public const int OrksMost = 900, GuardsMost = 400;
    /// <summary>How fast a drawn army closes on where the server says it is (a share a second), and how far off it is put there at once.</summary>
    public const float CatchUp = 3f, SnapMetres = 25f;
    /// <summary>Bodies drawn for an aggregate's losses, at most this many a section per army, before the corpse floor (2026-11-09: every slot that went; <see cref="MusouCorpseRules.ImpostorBodies"/>).</summary>
    public const int DeathsPerSection = MusouCorpseRules.OldImpostorDeathsPerSection;
}

/// <summary>
/// The battle flow's distant armies on a client (expansion S6; engine-free): tag 18's armies, each drawn between two sections (its anchor carried on at its last speed and eased to the newest), and their aggregate
/// parts written out as impostors in the server's own slots (<see cref="TfwArmyLayout"/>), so a promotion takes over an impostor where it stood and a demotion leaves one where the unit was. Losses of an aggregate
/// become deaths at the slots that went.
/// </summary>
public sealed class TfwArmyImpostors
{
    sealed class Drawn
    {
        public TfwArmyView View;
        public Vector3 Anchor, Velocity;
        public float Yaw;
        public uint Tick;
        public double HeardAt;
    }

    readonly Dictionary<byte, Drawn> _armies = new();
    readonly List<byte> _gone = new();
    readonly List<(float D, Drawn A)> _order = new();
    uint _tick;

    /// <summary>The aggregate losses heard since the last <see cref="TakeDeaths"/>.</summary>
    public List<TfwImpostorDeath> Deaths { get; } = new();
    /// <summary>The rules before the corpse floor (client <c>--musou-old-corpses</c>): at most <see cref="TfwImpostorRules.DeathsPerSection"/> bodies a section.</summary>
    public bool OldCorpses { get; init; }
    /// <summary>
    /// Whether the Ork armies' aggregates are drawn (the rules before 2026-11-09; client <c>--musou-old-ork-armies</c>, with the server's <c>SM_TFW_OLD_ORK_ARMIES=1</c>). Off: no Ork impostor and no Ork impostor
    /// death, whatever a section says (the owner: "completely remove those orks from musou mode"); the Guard armies' are drawn as before.
    /// </summary>
    public bool OrkAggregates { get; init; }
    public int Armies => _armies.Count;

    /// <summary>A stable key for slot <paramref name="slot"/> of army <paramref name="id"/>.</summary>
    public static int Key(byte id, int slot) => id << 16 | (slot & 0xFFFF);

    /// <summary>The newest section's armies (<paramref name="tick"/> its server tick, <paramref name="now"/> the client's clock in seconds).</summary>
    public void Apply(IReadOnlyList<TfwArmyView> views, uint tick, double now)
    {
        if (tick == _tick && _armies.Count > 0) return;
        _tick = tick;
        foreach (var id in _armies.Keys) _gone.Add(id);
        foreach (var v in views)
        {
            _gone.Remove(v.Id);
            if (!_armies.TryGetValue(v.Id, out var d) || d.View.Side != v.Side)
            {
                _armies[v.Id] = new Drawn { View = v, Anchor = v.Anchor, Yaw = v.Yaw, Tick = tick, HeardAt = now };
                continue;
            }
            double seconds = now - d.HeardAt;
            d.Velocity = seconds > 0.05 && v.Order is TfwArmyOrder.March or TfwArmyOrder.Retreat ? (v.Anchor - d.View.Anchor) / (float)seconds : Vector3.Zero;
            if (v.SlotBase == d.View.SlotBase && v.Far < d.View.Far && (v.Side == TfwArmySide.Guard || OrkAggregates))   // the aggregate lost men: bodies where its last slots stood
            {
                int lost = MusouCorpseRules.ImpostorBodies(d.View.Far - v.Far, OldCorpses);
                for (int k = 0; k < lost; k++)
                {
                    int slot = v.SlotBase + v.Far + k;
                    Deaths.Add(new TfwImpostorDeath(Key(v.Id, slot), v.Side, TypeOf(d.View, slot), TfwArmyLayout.Slot(v.Side, v.Width, d.Anchor, d.Yaw, slot), d.Yaw));
                }
            }
            d.View = v;
            d.Tick = tick;
            d.HeardAt = now;
            if (Vector3.Distance(d.Anchor, v.Anchor) > TfwImpostorRules.SnapMetres) { d.Anchor = v.Anchor; d.Yaw = v.Yaw; }
        }
        foreach (var id in _gone) _armies.Remove(id);
        _gone.Clear();
    }

    /// <summary>One frame: each army eases to its newest place carried on at its speed, and turns to its facing.</summary>
    public void Frame(float dt, double now)
    {
        float share = 1f - MathF.Exp(-TfwImpostorRules.CatchUp * MathF.Max(0f, dt));
        foreach (var d in _armies.Values)
        {
            var target = d.View.Anchor + d.Velocity * (float)Math.Min(1.0, now - d.HeardAt);
            d.Anchor += (target - d.Anchor) * share;
            float turn = MathF.IEEERemainder(d.View.Yaw - d.Yaw, MathF.Tau);
            d.Yaw += turn * share;
        }
    }

    static MusouEnemyType TypeOf(in TfwArmyView v, int slot) => v.Side == TfwArmySide.Ork ? TfwArmyLayout.OrkType(slot, v.EliteShare) : MusouEnemyType.Boy;

    /// <summary>
    /// The impostors to draw, nearest armies first, at most <paramref name="orksMost"/> Orks and <paramref name="guardsMost"/> Guardsmen: each army's aggregate slots <c>[SlotBase, SlotBase + Far)</c> from the front
    /// (the Orks' only with <see cref="OrkAggregates"/>).
    /// </summary>
    public void Write(Vector3 camera, List<TfwImpostor> into, int orksMost = TfwImpostorRules.OrksMost, int guardsMost = TfwImpostorRules.GuardsMost)
    {
        into.Clear();
        _order.Clear();
        foreach (var d in _armies.Values) if (d.View.Far > 0 && (d.View.Side == TfwArmySide.Guard || OrkAggregates)) _order.Add((Vector3.DistanceSquared(d.Anchor, camera), d));
        _order.Sort((x, y) => x.D.CompareTo(y.D));
        int orks = 0, guards = 0;
        foreach (var (_, d) in _order)
        {
            var v = d.View;
            bool moving = v.Order is TfwArmyOrder.March or TfwArmyOrder.Retreat;
            ref int used = ref v.Side == TfwArmySide.Ork ? ref orks : ref guards;
            int most = v.Side == TfwArmySide.Ork ? orksMost : guardsMost;
            for (int slot = v.SlotBase; slot < v.SlotBase + v.Far && used < most; slot++, used++)
                into.Add(new TfwImpostor(Key(v.Id, slot), v.Side, TypeOf(v, slot), TfwArmyLayout.Slot(v.Side, v.Width, d.Anchor, d.Yaw, slot), d.Yaw, moving));
        }
    }

    /// <summary>The drawn place and facing of army <paramref name="id"/> (tests and the barks).</summary>
    public (Vector3 Anchor, float Yaw)? Of(byte id) => _armies.TryGetValue(id, out var d) ? (d.Anchor, d.Yaw) : null;

    public void Clear() { _armies.Clear(); Deaths.Clear(); _tick = 0; }
}
