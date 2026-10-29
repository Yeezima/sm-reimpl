using SmNet;
using SmSim;

namespace SmServer;

/// <summary>
/// Dropped pickups on the wire (protocol 26, fidelity gap 1): the world's pickups and the recent collections and expiries go to every
/// client in a <see cref="PickupUpdate"/> outside the snapshots (which have no room left). The live list goes out in a tick where it changed
/// (or an event happened, or a client joined) and every <see cref="PickupListInterval"/> ticks while pickups lie or a change is recent; the
/// events every <see cref="PickupInterval"/> ticks while remembered (<see cref="PickupEventMemoryTicks"/>), without the list in between. Each
/// event thus goes out about ten times (redundant sends instead of acknowledgements; ours) and the list is the whole state.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>Ticks between event messages while events are remembered (3: 10 per second at 30 Hz; ours).</summary>
    public const int PickupInterval = 3;
    /// <summary>Ticks between repeats of an unchanged live list (30: once a second at 30 Hz; ours).</summary>
    public const int PickupListInterval = 30;
    /// <summary>Ticks an event stays in the messages (30: one second at 30 Hz; ours).</summary>
    public const int PickupEventMemoryTicks = 30;

    readonly List<(uint Tick, PickupEventView Event)> _pickupEvents = new();
    int _pickupSequence;
    uint _pickupChangedAt;
    bool _pickupChanged;

    /// <summary>Pickup messages sent (all clients count once per message).</summary>
    public int PickupMessagesSent { get; private set; }
    /// <summary>Bytes of pickup messages sent to one client (each message counted once), and the largest message.</summary>
    public long PickupBytesSent { get; private set; }
    public int LargestPickupMessage { get; private set; }
    int _pickupPeerCount;
    uint _pickupLogAt;

    /// <summary>After a world tick: remembers its pickup events, forgets old ones, logs the tick's drops and pickups.</summary>
    void NotePickups()
    {
        // New drops and events by the world's running totals (so those of SpawnPickup calls between ticks, e.g. --pickup-test, count too).
        var spawns = World.PickupSpawnLog.TakeLast((int)Math.Min(World.PickupSpawnLog.Count, World.PickupSpawnsTotal - _pickupSpawnsSeen)).ToList();
        _pickupSpawnsSeen = World.PickupSpawnsTotal;
        var events = World.PickupEventLog.TakeLast((int)Math.Min(World.PickupEventLog.Count, World.PickupEventsTotal - _pickupEventsSeen)).ToList();
        _pickupEventsSeen = World.PickupEventsTotal;
        foreach (var p in spawns)
            Log?.Invoke($"pickup #{p.Id} {TypeName(p.Type)} dropped by player {p.Dropper} at {p.Position.X:F1},{p.Position.Y:F1},{p.Position.Z:F1} (tick {World.Tick}; {World.LivePickups.Count} in the world)");
        foreach (var e in events)
        {
            _pickupEvents.Add((World.Tick, new PickupEventView(_pickupSequence, e.Id & 0xFFFF, e.Type, e.End, e.End == PickupEnd.Collected ? e.Collector & 0xFFFF : 0, e.Position)));
            _pickupSequence = (_pickupSequence + 1) & 0xFFFF;
            Log?.Invoke(e.End == PickupEnd.Collected
                ? $"pickup #{e.Id} {TypeName(e.Type)} collected by player {e.Collector} (+{e.Gain.Primary} primary, +{e.Gain.Pistol} pistol, +{e.Gain.Grenades} equipment; tick {World.Tick})"
                : $"pickup #{e.Id} {TypeName(e.Type)} expired (tick {World.Tick})");
        }
        _pickupEvents.RemoveAll(x => World.Tick - x.Tick > PickupEventMemoryTicks);
        if (events.Count > 0) { _pickupChanged = true; _pickupChangedAt = World.Tick; }
    }

    long _pickupSpawnsSeen, _pickupEventsSeen;

    /// <summary>Notes a change of the live list (also one without events: a new round clears the pickups silently, a test aid drops some).</summary>
    void NoteListChange()
    {
        ulong signature = 14695981039346656037UL;
        foreach (var p in World.LivePickups) signature = (signature ^ (ulong)p.Id) * 1099511628211UL;
        signature ^= (ulong)World.LivePickups.Count << 48;
        if (signature != _pickupSignature) { _pickupChanged = true; _pickupChangedAt = World.Tick; }
        _pickupSignature = signature;
    }

    ulong _pickupSignature = 14695981039346656037UL;

    string TypeName(int type) => World.Config.Pickups is { } s && type >= 0 && type < s.Types.Count ? s.Types[type].Blueprint : $"type {type}";

    /// <summary>The pickup message as of now (without the live list: <paramref name="withList"/> false).</summary>
    public PickupUpdate PickupsOf(bool withList = true) => new(World.Tick,
        withList ? World.LivePickups.Take(Protocol.MaxPickupRecords).Select(p => new PickupView(p.Id & 0xFFFF, p.Type, p.Position)).ToArray() : null,
        _pickupEvents.Select(x => x.Event).ToArray());

    /// <summary>Sends the pickup message to every client when it is due (see the class summary).</summary>
    void SendPickups()
    {
        if (World.Config.Pickups is null) return;
        NotePickups();   // again: drops and events of SpawnPickup calls after the tick (test aids) go out in this tick
        NoteListChange();
        if (_peers.Count > _pickupPeerCount) _pickupChanged = true;   // a newcomer gets the list at once
        _pickupPeerCount = _peers.Count;
        if (_peers.Count == 0) { _pickupChanged = false; return; }
        bool listDue = _pickupChanged
            || (World.Tick % PickupListInterval == 0 && (World.LivePickups.Count > 0 || World.Tick - _pickupChangedAt <= 2 * PickupListInterval));
        bool eventsDue = _pickupEvents.Count > 0 && World.Tick % PickupInterval == 0;
        if (!listDue && !eventsDue) return;
        _pickupChanged = false;
        var packet = Protocol.Write(PickupsOf(listDue));
        foreach (var connection in _peers.Keys) _transport.Send(connection, packet);
        PickupMessagesSent++;
        PickupBytesSent += packet.Length;
        LargestPickupMessage = Math.Max(LargestPickupMessage, packet.Length);
        if (World.Tick - _pickupLogAt >= 30u * (uint)World.Config.TickRate)
        {
            _pickupLogAt = World.Tick;
            Log?.Invoke($"pickup messages: {PickupMessagesSent} sent, {PickupBytesSent} bytes per client ({PickupBytesSent / Math.Max(1.0, World.Tick * World.Config.TickSeconds):F0} B/s average), largest {LargestPickupMessage} bytes; {World.LivePickups.Count} lying");
        }
    }
}
