using System.Numerics;
using SmNet;
using SmNet.Extras.Warfront;
using SmSim;
using SmSim.Extras.Warfront.Retainers;
using SmSim.Extras.Warfront.Squad;

namespace SmServer;

/// <summary>What the squad sender needs from the Warfront server (W5a's state and W3b's battle seams implement it; tests use a fake).</summary>
public interface IWarfrontSquadHost
{
    /// <summary>The server's tick rate (ticks a second).</summary>
    int Rate { get; }
    /// <summary>Whether a battle is running (in the hub nothing is in SquadState).</summary>
    bool InBattle { get; }
    /// <summary>Every client that is identified: its player id and its campaign key.</summary>
    IReadOnlyList<(int Player, ulong Key)> Clients { get; }
    /// <summary>The handle client <paramref name="viewerKey"/> sees for owner <paramref name="ownerKey"/>: the key itself for the viewer's own roster, <c>WarfrontWire.Pseudonym</c> for any other.</summary>
    ulong Handle(ulong viewerKey, ulong ownerKey);
    /// <summary>The player id now playing campaign key <paramref name="key"/>; -1 when not connected.</summary>
    int PlayerOf(ulong key);
}

/// <summary>One thing to send: to one player, or (Player -1) to every client.</summary>
public readonly record struct WarfrontSquadOutgoing(int Player, byte Tag, byte[] Payload);

/// <summary>
/// The squad's sections (slice W10, <see cref="SquadWire"/>; Phase B T2): a roster section per owner by signature (on change, to a client that joins, and one owner every 2 s in turn), each Marine with the World id
/// it fights as, and the events from the Marines' state changes. SquadState (tag 32) is retired: the retainers are World players in every snapshot. Pure: it reads <see cref="WarfrontRetainers"/> and
/// <see cref="SquadRosters"/> and returns what to send; W5a's <c>WarfrontSendSquad</c> hands the result to <c>SendExtras</c>. Fallen and out Marines stay in the roster section.
/// </summary>
public sealed class WarfrontSquadSender
{
    /// <summary>The state rate (Hz) and the roster rotation (seconds between two owners) [ours/estimate].</summary>
    public const int StateHz = 10;
    public const double RotationSeconds = 2.0;
    /// <summary>Roster changes are looked for this often (a second), not on every state send (export and encode cost) [ours/estimate]; a recruit or a joining client forces the next check.</summary>
    public const int RosterChecksPerSecond = 2;

    readonly WarfrontRetainers _sim;
    readonly SquadRosters _rosters;
    readonly SimConfig _config;
    readonly IWarfrontSquadHost _host;
    readonly SquadState[] _last = new SquadState[SquadRules.MaxTotal];
    readonly Dictionary<ulong, byte[]> _signature = [];   // per (owner key): the roster bytes last sent with the owner's own handle
    readonly List<SquadEventWire> _events = [];
    bool _force = true;
    uint _lastCheck;
    uint _lastRosterTick;
    int _rotation;

    public WarfrontSquadSender(WarfrontRetainers retainers, SquadRosters rosters, SimConfig config, IWarfrontSquadHost host) { _sim = retainers; _rosters = rosters; _config = config; _host = host; }

    /// <summary>The class, weapon and melee ids of a loadout in the Classic loadout's numbering (the index in the config's lists; 0xFFFF for a name the config does not have).</summary>
    public (byte Class, ushort Primary, ushort Secondary, ushort Melee) IdsOf(SquadLoadout l)
    {
        int cls = -1;
        for (int i = 0; i < _config.Classes.Count; i++) if (_config.Classes[i].Name == l.ClassId) { cls = i; break; }
        ushort W(string name)
        {
            if (name.Length == 0) return 0xFFFF;
            for (int i = 0; i < _config.Weapons.Count; i++) if (_config.Weapons[i].Name == name) return (ushort)i;
            return 0xFFFF;
        }
        ushort melee = 0xFFFF;
        if (cls >= 0 && l.Melee.Length > 0)
        {
            var choices = _config.Classes[cls].MeleeChoices;
            for (int i = 0; i < choices.Count; i++) if (choices[i].Wargear == l.Melee) { melee = (ushort)i; break; }
        }
        return (cls >= 0 ? (byte)cls : (byte)255, W(l.Primary), W(l.Secondary), melee);
    }

    /// <summary>A loadout wears its owner's own armour pieces when its armour list holds more than the class tag (<c>SquadLoadoutBuilder</c>).</summary>
    public static bool OwnPieces(SquadLoadout l) => l.Armour.Count > 1;

    /// <summary>The service record as the roster section carries it (clamped to 16 bits).</summary>
    public static ushort[] RecordOf(ServiceRecord r)
    {
        static ushort C(int v) => (ushort)Math.Clamp(v, 0, ushort.MaxValue);
        return [C(r.Kills), C(r.EliteKills), C(r.BossKills), C(r.Assists), C(r.Revives), C(r.TimesDowned), C(r.Objectives), C(r.BattlesSurvived)];
    }

    /// <summary>Called every tick with the tick number: the events are noted every tick, the state goes at <see cref="StateHz"/>, rosters when they change or are due.</summary>
    public List<WarfrontSquadOutgoing> Step(uint tick)
    {
        var outgoing = new List<WarfrontSquadOutgoing>();
        NoteEvents();
        int perState = Math.Max(1, _host.Rate / StateHz);
        if (tick % (uint)perState == 0)
        {
            if (_events.Count > 0) { outgoing.Add(new(-1, SquadWire.EventsTag, SquadWire.EncodeEvents(_events))); _events.Clear(); }
            if (_force || tick - _lastCheck >= (uint)Math.Max(1, _host.Rate / RosterChecksPerSecond)) { _force = false; _lastCheck = tick; RosterSections(tick, outgoing); }
        }
        return outgoing;
    }

    /// <summary>A client that joined (or came back): every roster again, at the next call of <see cref="Step"/>.</summary>
    public void ClientJoined(int player) { _force = true; foreach (var key in _signature.Keys.ToList()) _signature[key] = []; _ = player; }


    void NoteEvents()
    {
        if (!_host.InBattle) { Array.Fill(_last, SquadState.Free); return; }
        for (int i = 0; i < SquadRules.MaxTotal; i++)
        {
            var now = _sim.ByIndex(i)?.State ?? SquadState.Free;
            var was = _last[i];
            if (now == was) continue;
            _last[i] = now;
            if (now == SquadState.Downed && was != SquadState.Downed) _events.Add(new SquadEventWire(SquadEventKind.Downed, (byte)i, 255));
            else if (was == SquadState.Downed && now == SquadState.Standing) _events.Add(new SquadEventWire(SquadEventKind.Revived, (byte)i, ReviverOf(i)));   // the revive is in this tick's list (the registry flips the state as it revives)
            else if (was == SquadState.Downed && now == SquadState.Out) _events.Add(new SquadEventWire(SquadEventKind.Out, (byte)i, 255));
        }
    }

    byte ReviverOf(int index)
    {
        foreach (var r in _sim.Revives)
            if (r.Slot >= 0 && _sim.IndexOf(_host.PlayerOf(r.Key), r.Slot) == index) return r.ByHuman >= 0 ? (byte)Math.Min(r.ByHuman, 199) : r.ByMarine >= 0 ? (byte)Math.Min(200 + r.ByMarine, 254) : (byte)255;
        return 255;
    }

    /// <summary>A recruit (a card pick): W3b calls this with the owner and the new Marine's slot.</summary>
    public void NoteRecruited(int slotIndex) { _force = true; _events.Add(new SquadEventWire(SquadEventKind.Recruited, (byte)Math.Clamp(slotIndex, 0, SquadWire.MaxMarines - 1), 255)); }

    void RosterSections(uint tick, List<WarfrontSquadOutgoing> outgoing)
    {
        var save = _rosters.Export();
        bool rotate = tick - _lastRosterTick >= (uint)(RotationSeconds * _host.Rate);
        if (rotate) _lastRosterTick = tick;
        int ownerNo = 0;
        foreach (var owner in save.Owners)
        {
            if (owner.Marines.Count == 0) { ownerNo++; continue; }
            var own = Build(owner, owner.Key);   // signature: the roster as its owner sees it (no handle in the comparison)
            bool changed = !_signature.TryGetValue(owner.Key, out var old) || !old.AsSpan().SequenceEqual(own);
            bool due = rotate && ownerNo == _rotation % Math.Max(1, save.Owners.Count);
            _signature[owner.Key] = own;
            ownerNo++;
            if (!changed && !due) continue;
            foreach (var (player, key) in _host.Clients)
                outgoing.Add(new(player, SquadWire.RosterTag, Build(owner, _host.Handle(key, owner.Key))));
        }
        if (rotate) _rotation++;
    }

    byte[] Build(SquadOwnerSave owner, ulong handle)
    {
        var marines = new List<SquadRosterWire>();
        for (int slot = 0; slot < owner.Marines.Count && slot < SquadWire.MaxRosterMarines; slot++)
        {
            var m = owner.Marines[slot];
            var (cls, p, s, melee) = IdsOf(m.Loadout);
            int player = _host.PlayerOf(owner.Key);
            var landed = player >= 0 && _host.InBattle ? _sim.Of(player, slot) : null;
            byte inBattle = landed is { State: not SquadState.Free } ? (byte)landed.Index : SquadRosterWire.NotInBattle;
            ushort worldId = landed is { WorldId: > 0 and <= ushort.MaxValue, State: SquadState.Standing or SquadState.Downed } ? (ushort)landed.WorldId : SquadRosterWire.NoWorldId;
            marines.Add(new SquadRosterWire((byte)slot, inBattle, m.Name, cls, p, s, melee, OwnPieces(m.Loadout), RecordOf(m.Record), worldId));
        }
        int owning = _host.PlayerOf(owner.Key);
        return SquadWire.EncodeRoster(new SquadRosterSection(handle, owning is >= 0 and < 255 ? (byte)owning : SquadRosterSection.NoPlayer, owner.Held, marines));
    }
}
