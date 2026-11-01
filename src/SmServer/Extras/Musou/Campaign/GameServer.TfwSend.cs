using System.Numerics;
using SmNet;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// The war's sections (TF6; <c>TfwWire</c> tags 7 to 10), only in a war (<see cref="World.Tfw"/>), so a faithful, extended or 5-wave server never sends one. The run leaves what it shows in
/// <see cref="TfwFeed"/>; tags 8 to 10 are quantised over the director's box, so they wait for the run's first director (<see cref="MusouSim.Director"/>).
/// </summary>
public sealed partial class GameServer
{
    readonly Dictionary<int, TfwGuardInterest> _tfwInterest = new();
    readonly TfwEventWindow _tfwEvents = new();
    readonly List<int> _tfwGone = new();
    long _tfwRecordsGone;

    /// <summary>What the war shows this tick: the run fills it, <see cref="SendTfw"/> reads it (and takes the events).</summary>
    public TfwFeed TfwFeed { get; } = new();

    /// <summary>Guardsman records sent (all clients).</summary>
    public long TfwRecordsSent => _tfwInterest.Values.Sum(i => i.RecordsSent) + _tfwRecordsGone;

    /// <summary>
    /// This feature's part of the envelope hook (the dispatcher <c>GameServer.Extras.cs</c>): per client, the war, the objectives, the events and the Guardsmen due. It runs after
    /// <see cref="SendMusou"/>, whose spending this tick it reads, and within the bytes that leaves.
    /// </summary>
    void SendTfw()
    {
        if (World.Tfw is null || World.Musou is not { } sim) return;
        TfwDownTest();   // GameServer.TfwDownTest.cs: a test aid, off without its variable
        TfwTestAids();   // GameServer.TfwWinTest.cs: the same
        FeedTfw();   // GameServer.TfwFeedRun.cs
        var feed = TfwFeed;
        uint tick = World.Tick;
        int tickRate = World.Config.TickRate;
        bool hasBox = sim.Director != default;
        var box = hasBox ? MusouSend.EffectiveBox(sim.Director.Box, _musouNavBox) : default;
        byte[]? objectives = hasBox ? TfwWire.WriteObjectives(feed.Objectives, box, feed.Sites) : null;
        IReadOnlyList<TfwGuardEvent> events = hasBox ? _tfwEvents.Step(tick, feed.Events, TfwSendRules.EventsPerTick(tickRate)) : [];
        var guards = new ReadOnlySpan<TfwGuard>(feed.Guards, 0, hasBox ? Math.Clamp(feed.GuardCount, 0, feed.Guards.Length) : 0);
        Vector3? anyone = null;
        foreach (var p in World.Players.Values) if (p.Alive) { anyone = p.Position; break; }

        foreach (var peer in _peers.Values)
        {
            int id = peer.PlayerId;
            if (!_tfwInterest.TryGetValue(id, out var interest)) _tfwInterest[id] = interest = new TfwGuardInterest();
            Vector3? viewer = World.Players.TryGetValue(id, out var me) ? me.Position : anyone;
            byte[]? war = feed.War is { } front ? TfwWire.WriteWar(front.View(feed.Transport, feed.Supplies, _tfwCommandEcho.GetValueOrDefault(id)) with { Own = World.TfwOwn(id) }) : null;
            float musouBytes = interest.MusouBytes(_musouInterest.TryGetValue(id, out var musou) ? musou.Tokens : null, tickRate);
            foreach (var sections in interest.Plan(tick, tickRate, war, objectives, guards, viewer, box, events, musouBytes))
                SendExtras(new ExtrasUpdate(tick, sections), id);
        }
        if (hasBox) feed.ClearEvents();

        _tfwGone.Clear();
        foreach (int id in _tfwInterest.Keys) if (!_peers.Values.Any(p => p.PlayerId == id)) _tfwGone.Add(id);
        foreach (int id in _tfwGone)
        {
            _tfwRecordsGone += _tfwInterest[id].RecordsSent;
            _tfwInterest.Remove(id);
            _tfwCommandEcho.Remove(id);
        }
    }
}
