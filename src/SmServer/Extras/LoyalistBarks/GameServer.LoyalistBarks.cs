using SmNet;
using SmNet.Extras.LoyalistBarks;
using SmSim;

namespace SmServer;

/// <summary>
/// Added content "loyalist barks" on the server (docs/added-content.md section 7): the FOR THE EMPEROR key. Once a tick (the envelope hook
/// <see cref="ExtrasSend"/>, through its dispatcher), only when the match runs <see cref="ContentExtras.LoyalistBarks"/>: a press edge of <see cref="InputButtons.ExtraAction"/>
/// in the applied input of a living loyalist Space Marine (not Chaos, not an Ork), at most once per <see cref="LoyalistShoutWire.CooldownSeconds"/>,
/// becomes a shout (with the player's shout sequence) every client is told about in the envelope's section (<see cref="LoyalistShoutWire"/>), repeated for
/// <see cref="LoyalistShoutWire.ResendTicks"/> ticks. The simulation never reads the key: no action is interrupted and no animation plays. The server
/// plays nothing; which take is heard is the clients' (seeded by the shout's tick).
/// </summary>
public sealed partial class GameServer
{
    readonly Dictionary<int, bool> _shoutHeld = new();
    readonly HashSet<int> _shoutPresent = new();
    readonly List<int> _shoutGone = new();
    readonly Dictionary<int, uint> _shoutLast = new();
    readonly Dictionary<int, byte> _shoutSequence = new();
    readonly List<LoyalistShout> _shoutRecent = new();

    /// <summary>FOR THE EMPEROR shouts accepted.</summary>
    public int LoyalistShouts { get; private set; }
    /// <summary>Key presses refused: dead, not a loyalist Space Marine, or within the cooldown.</summary>
    public int LoyalistShoutsRefused { get; private set; }

    /// <summary>This feature's part of the envelope hook (called by the dispatcher <c>GameServer.Extras.cs</c>).</summary>
    void SendLoyalistBarks()
    {
        if (!World.Config.Extras.HasFlag(ContentExtras.LoyalistBarks)) return;
        uint tick = World.Tick;
        var present = _shoutPresent;
        present.Clear();
        foreach (var peer in _peers.Values)
        {
            int id = peer.PlayerId;
            present.Add(id);
            bool held = (peer.LastApplied.Buttons & InputButtons.ExtraAction) != 0;
            bool was = _shoutHeld.GetValueOrDefault(id);
            _shoutHeld[id] = held;
            if (!held || was) continue;
            bool loyalist = World.Players.TryGetValue(id, out var p) && p.Alive && Races.Of(World.Config, p.Class, p.Team) == Race.SpaceMarines;
            if (!loyalist || !LoyalistShoutWire.CooledDown(_shoutLast.TryGetValue(id, out uint last) ? last : null, tick, World.Config.TickRate))
            {
                LoyalistShoutsRefused++;
                continue;
            }
            _shoutLast[id] = tick;
            byte sequence = unchecked((byte)(_shoutSequence.GetValueOrDefault(id) + 1));
            _shoutSequence[id] = sequence;
            _shoutRecent.Add(new LoyalistShout(id, tick, LoyalistShoutKind.BattleCry, sequence));
            LoyalistShouts++;
            Log?.Invoke($"loyalist barks: P{id} FOR THE EMPEROR at tick {tick} ({LoyalistShouts} accepted, {LoyalistShoutsRefused} refused)");
        }
        _shoutGone.Clear();
        foreach (int k in _shoutHeld.Keys) if (!present.Contains(k)) _shoutGone.Add(k);
        foreach (int gone in _shoutGone) { _shoutHeld.Remove(gone); _shoutLast.Remove(gone); _shoutSequence.Remove(gone); }
        _shoutRecent.RemoveAll(s => tick - s.Tick >= LoyalistShoutWire.ResendTicks);
        if (_shoutRecent.Count > 0) SendExtras(new ExtrasUpdate(tick, [(LoyalistShoutWire.Tag, LoyalistShoutWire.Write(_shoutRecent))]));
    }
}
