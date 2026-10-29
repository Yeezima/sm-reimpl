namespace SmNet;

/// <summary>
/// Dropped pickups on the client (protocol 26, fidelity gap 1): the newest <see cref="PickupUpdate"/> live list is the state shown, minus the
/// pickups that collections and expiries received since have removed; the events are handed out once each (<see cref="TakePickupEvents"/>).
/// Nothing is predicted: a collection shows when the server's message says so, and the own ammunition with the next snapshot.
/// The events of the first message accepted (a late joiner's view of the last second) are marked seen without being handed out, and at most
/// <see cref="MaxQueuedPickupEvents"/> wait (the oldest go first), so a view that does not take them cannot make the queue grow.
/// </summary>
public sealed partial class GameClient
{
    PickupView[] _pickupList = [];
    PickupView[] _pickups = [];
    uint _pickupListTick, _pickupTick;
    bool _pickupSeen, _pickupListSeen;
    readonly List<PickupEventView> _pickupEvents = new();
    readonly HashSet<int> _seenPickupEvents = new();
    readonly Queue<int> _seenPickupEventOrder = new();
    /// <summary>Pickups removed by events, with the server tick of the message that said so (a list older than that does not bring them back).</summary>
    readonly Dictionary<int, uint> _removedPickups = new();
    /// <summary>Sequences remembered to drop repeats (far more than one message's events; a sequence wraps after 65536).</summary>
    const int PickupEventMemory = 1024;
    /// <summary>Most events waiting for <see cref="TakePickupEvents"/>; one more drops the oldest.</summary>
    public const int MaxQueuedPickupEvents = 256;

    /// <summary>The pickups in the world as of the newest pickup messages (empty before the first list).</summary>
    public IReadOnlyList<PickupView> Pickups => _pickups;
    /// <summary>The server tick of the newest pickup message (0 before the first).</summary>
    public uint PickupTick => _pickupTick;
    /// <summary>Pickup messages accepted.</summary>
    public int PickupMessages { get; private set; }

    void OnPickups(PickupUpdate u)
    {
        bool first = !_pickupSeen;
        _pickupSeen = true;
        if (u.ServerTick > _pickupTick) _pickupTick = u.ServerTick;
        PickupMessages++;
        foreach (var e in u.Events)
        {
            if (!_seenPickupEvents.Add(e.Sequence)) continue;
            _seenPickupEventOrder.Enqueue(e.Sequence);
            while (_seenPickupEventOrder.Count > PickupEventMemory) _seenPickupEvents.Remove(_seenPickupEventOrder.Dequeue());
            _removedPickups[e.Id] = Math.Max(_removedPickups.GetValueOrDefault(e.Id), u.ServerTick);
            if (first) continue;   // a late joiner does not replay the last second
            _pickupEvents.Add(e);
            if (_pickupEvents.Count > MaxQueuedPickupEvents) _pickupEvents.RemoveRange(0, _pickupEvents.Count - MaxQueuedPickupEvents);
        }
        if (u.Live is { } list && (!_pickupListSeen || u.ServerTick >= _pickupListTick))
        {
            _pickupListSeen = true;
            _pickupListTick = u.ServerTick;
            _pickupList = list;
            // A removal said by a message newer than this list stays; older removals are settled by the list itself.
            foreach (var id in _removedPickups.Where(kv => kv.Value <= u.ServerTick).Select(kv => kv.Key).ToList()) _removedPickups.Remove(id);
        }
        _pickups = _removedPickups.Count == 0 ? _pickupList : _pickupList.Where(p => !_removedPickups.ContainsKey(p.Id)).ToArray();
    }

    /// <summary>Collections and expiries not handed out yet (each once), oldest first.</summary>
    public IReadOnlyList<PickupEventView> TakePickupEvents()
    {
        if (_pickupEvents.Count == 0) return [];
        var list = _pickupEvents.ToArray();
        _pickupEvents.Clear();
        return list;
    }
}
