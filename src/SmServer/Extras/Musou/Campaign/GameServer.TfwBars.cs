using System.Numerics;
using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// The elites' health bars on the wire (Musou Mode pass 2, item 14; design section H; tag 13). The owner: "a health bar above each elite's head, always visible". Elites are the 'Ard Boy, the Nob
/// (the commander) and the Warboss (<see cref="TfwBars.IsElite"/>). Ten times a second the living elites are listed with their health as a byte of their full health (the pool's per-type
/// maximum; the battle's boss has its own); each client gets the <see cref="TfwBars.PerClient"/> nearest to its player, as one section, when the list changed since it last got one, or once a
/// second as a keep-alive. Fewer bytes than that never go: 64 records are 193 bytes, so at most about 2 KB/s a client (budget: 8 KB/s for all of pass 2's new tags).
/// </summary>
public sealed partial class GameServer
{
    sealed class TfwBarClient
    {
        public byte[]? Last;
        public uint SentTick;
    }

    readonly MusouEnemy[] _tfwBarPool = new MusouEnemy[MusouSim.Capacity];
    readonly List<TfwElite> _tfwElites = new();
    readonly List<TfwBar> _tfwBarsNow = new();
    readonly List<(float, int)> _tfwBarScratch = new();
    readonly Dictionary<int, TfwBarClient> _tfwBarClients = new();
    uint _tfwBarsChecked;

    /// <summary>Bar sections sent, and the largest payload (tests and the log).</summary>
    public int TfwBarSections { get; private set; }
    public int TfwBarLargest { get; private set; }

    /// <summary>Once a tick from <c>SendTfw</c>; works every <see cref="TfwBars.PeriodTicks"/> ticks.</summary>
    void SendTfwBars(uint tick, int tickRate)
    {
        if (World.Tfw is not { } run || World.Musou is not { } sim) return;
        if (tick - _tfwBarsChecked < (uint)TfwBars.PeriodTicks(tickRate) && tick >= _tfwBarsChecked) return;
        _tfwBarsChecked = tick;

        _tfwElites.Clear();
        int n = sim.CopyTo(_tfwBarPool);
        ushort boss = run.Battle?.BossId ?? 0;
        float bossMax = run.Battle?.BossMaxHealth ?? 0f;
        for (int i = 0; i < n; i++)
        {
            ref readonly var e = ref _tfwBarPool[i];
            if (!e.Alive || !TfwBars.IsElite(e.Type)) continue;
            float max = e.Id == boss && bossMax > 0f ? bossMax : sim.Rules.Of(e.Type).Health;
            _tfwElites.Add(new TfwElite(e.Id, e.Position, TfwBars.Quantise(e.Health, max)));
        }

        Vector3? anyone = null;
        foreach (var p in World.Players.Values) if (p.Alive) { anyone = p.Position; break; }
        foreach (var peer in _peers.Values)
        {
            if (!_tfwBarClients.TryGetValue(peer.PlayerId, out var client)) _tfwBarClients[peer.PlayerId] = client = new TfwBarClient();
            Vector3? viewer = World.Players.TryGetValue(peer.PlayerId, out var me) && me.Alive ? me.Position : anyone;
            TfwBars.Select(_tfwElites, viewer, _tfwBarsNow, _tfwBarScratch);
            var payload = TfwWire.WriteBars(_tfwBarsNow);
            bool changed = client.Last is null ? _tfwBarsNow.Count > 0 : !client.Last.AsSpan().SequenceEqual(payload);
            if ((!changed && _tfwBarsNow.Count == 0) || !TfwBars.Due(changed, tick, client.SentTick, tickRate)) continue;   // an empty list is sent once, when it replaces a list
            client.Last = payload;
            client.SentTick = tick;
            SendExtras(new ExtrasUpdate(tick, [(TfwWire.BarsTag, payload)]), peer.PlayerId);
            TfwBarSections++;
            TfwBarLargest = Math.Max(TfwBarLargest, payload.Length);
        }
        foreach (int id in _tfwBarClients.Keys.ToList()) if (!_peers.Values.Any(p => p.PlayerId == id)) _tfwBarClients.Remove(id);
    }
}
