using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Extras.Musou.Campaign;

/// <summary>One elite as the bars' plan sees it: its id, where it stands and its health as 1 to 255 of its full health (<see cref="TfwBars.Quantise"/>).</summary>
public readonly record struct TfwElite(ushort Id, Vector3 Position, byte Health);

/// <summary>
/// The elites' health bars (Musou Mode pass 2, item 14; design section H; tag 13): which enemies have one, how a health becomes a byte, and which bars one client is sent. Engine-free and deterministic.
/// Every value is ours (kind: ours, confidence: low; spec "Elite health bars").
/// </summary>
public static class TfwBars
{
    /// <summary>
    /// The most bars one client gets. The owner doubled the enemies to 200, so up to 120 elites (60 % at the end of a run) may live at once; the design budgeted 60. A client is sent the
    /// <see cref="PerClient"/> nearest to its player and the rest have no bar until they come closer: 64 records are 193 bytes a message, so with <see cref="PerSecond"/> at most 2 KB/s a
    /// client, a quarter of the 8 KB/s the design gives all of pass 2's new tags. The ones left out stand beyond the nearest 64, in a swarm past about 30 to 40 m.
    /// </summary>
    public const int PerClient = 64;

    /// <summary>The most bar sections a client gets in a second; a section goes only when its content changed, or once a second as a keep-alive against a lost datagram.</summary>
    public const int PerSecond = 10;

    /// <summary>Seconds after which an unchanged list is sent again.</summary>
    public const float KeepAliveSeconds = 1f;

    /// <summary>Ticks between two sections of one client at <paramref name="tickRate"/> (at least 1).</summary>
    public static int PeriodTicks(int tickRate) => Math.Max(1, tickRate / PerSecond);

    /// <summary>The kinds that wear a bar: the 'Ard Boy and the Nob (the commander is a Nob), and the Warboss, who also has the boss bar (design section H).</summary>
    public static bool IsElite(MusouEnemyType type) => type is MusouEnemyType.ArdBoy or MusouEnemyType.Nob or MusouEnemyType.Warboss;

    /// <summary>Health as 1 to 255 of the full health (rounded up, so a living elite never shows empty). Zero for a dead one or a full health that is not above 0.</summary>
    public static byte Quantise(float health, float max)
    {
        if (!(health > 0f) || !(max > 0f)) return 0;
        return (byte)Math.Clamp((int)MathF.Ceiling(health / max * 255f), 1, 255);
    }

    /// <summary>
    /// The bars <paramref name="viewer"/> is sent: every elite when there are at most <see cref="PerClient"/>, else the nearest ones (flat distance, then the earlier in the list). With no viewer
    /// (nobody alive) the first ones. In the list's order (the pool's id order), so the same set is the same bytes.
    /// </summary>
    public static void Select(IReadOnlyList<TfwElite> elites, Vector3? viewer, List<TfwBar> into, List<(float Distance, int Index)>? scratch = null)
    {
        into.Clear();
        if (elites.Count <= PerClient)
        {
            for (int i = 0; i < elites.Count; i++) into.Add(new TfwBar(elites[i].Id, elites[i].Health));
            return;
        }
        scratch ??= new List<(float, int)>(elites.Count);
        scratch.Clear();
        for (int i = 0; i < elites.Count; i++)
        {
            float d = viewer is { } v ? Vector2.DistanceSquared(new Vector2(elites[i].Position.X, elites[i].Position.Y), new Vector2(v.X, v.Y)) : 0f;
            scratch.Add((d, i));
        }
        scratch.Sort(static (a, b) => { int c = a.Distance.CompareTo(b.Distance); return c != 0 ? c : a.Index.CompareTo(b.Index); });
        scratch.RemoveRange(PerClient, scratch.Count - PerClient);
        scratch.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        foreach (var (_, i) in scratch) into.Add(new TfwBar(elites[i].Id, elites[i].Health));
    }

    /// <summary>Whether a client's section is due: its list differs from the last sent and the period has passed, or the keep-alive second has.</summary>
    public static bool Due(bool changed, uint tick, uint lastTick, int tickRate) =>
        tick < lastTick || (changed ? tick - lastTick >= (uint)PeriodTicks(tickRate) : tick - lastTick >= (uint)Math.Max(1, (int)(KeepAliveSeconds * tickRate)));
}
