using System.Numerics;
using SmNet;
using SmNet.Extras.Warfront;
using SmSim;
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
/// The squad's sections (slice W10, <see cref="SquadWire"/>): SquadState at 10 Hz to everyone, a roster section per owner by signature (on change, to a client that joins, and one owner every 2 s in turn), and the events
/// from the Marines' state changes. Pure: it reads <see cref="WarfrontSquadSim"/> and <see cref="SquadRosters"/> and returns what to send; W5a's <c>WarfrontSendSquad</c> hands the result to <c>SendExtras</c>
/// (the exact two lines are in the W10 report). Fallen and out Marines stay in the roster section; only Standing and Downed ones are in SquadState; in the hub nothing is.
/// </summary>
public sealed class WarfrontSquadSender
{
    /// <summary>The state rate (Hz) and the roster rotation (seconds between two owners) [ours/estimate].</summary>
    public const int StateHz = 10;
    public const double RotationSeconds = 2.0;

    readonly WarfrontSquadSim _sim;
    readonly SimConfig _config;
    readonly IWarfrontSquadHost _host;
    readonly SquadState[] _last = new SquadState[SquadRules.MaxTotal];
    readonly Dictionary<ulong, byte[]> _signature = [];   // per (owner key): the roster bytes last sent with the owner's own handle
    readonly HashSet<int> _known = [];
    readonly List<SquadEventWire> _events = [];
    byte _phase;
    uint _lastRosterTick;
    int _rotation;

    /// <summary>Sections sent as halves, and as whole lists (the report's "how often the halves were used").</summary>
    public int HalvesSent { get; private set; }
    public int WholesSent { get; private set; }

    public WarfrontSquadSender(WarfrontSquadSim sim, SimConfig config, IWarfrontSquadHost host) { _sim = sim; _config = config; _host = host; }

    /// <summary>Bytes of the extras message another section may take this tick (the lead's budget for the squad); the default is the whole of one section.</summary>
    public int StateRoom { get; set; } = Protocol.MaxExtrasPayloadBytes;

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

    /// <summary>Adapter for W11's second-pass <c>SquadMarineView.Airborne</c> (not in the view yet: false until it is, then this one line reads it).</summary>
    static bool AirborneOf(in SquadMarineView v) => false;

    /// <summary>The Marines in a battle as the wire shows them (Standing and Downed only).</summary>
    public List<SquadMarineWire> Marines()
    {
        var list = new List<SquadMarineWire>();
        if (!_host.InBattle) return list;
        Span<SquadMarineView> views = stackalloc SquadMarineView[SquadRules.MaxTotal];
        int n = _sim.CopyTo(views);
        for (int i = 0; i < n; i++)
        {
            var v = views[i];
            if (v.State is not (SquadState.Standing or SquadState.Downed)) continue;
            bool melee = _sim.Rosters.Marine(v.Key, v.Slot)?.Loadout.Weapon.Melee ?? false;
            list.Add(SquadWire.Make(v.Index, (byte)v.State, v.Firing, melee, v.State == SquadState.Downed && v.ReviveShare > 0f, (byte)(_host.PlayerOf(v.Key) is var owner and >= 0 ? _sim.OrderOf(owner) : SquadOrder.Follow), AirborneOf(v), v.Position, v.Yaw, v.HealthShare, v.DownedLeft, v.ReviveShare, v.Target));
        }
        return list;
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
            var marines = Marines();
            if (marines.Count > 0 || _known.Count > 0)
            {
                var payload = SquadWire.EncodeStateFitting(_phase++, marines, StateRoom, out bool half);
                if (half) HalvesSent++; else WholesSent++;
                outgoing.Add(new(-1, SquadWire.StateTag, payload));
            }
            _known.Clear();
            foreach (var m in marines) _known.Add(m.Index);
            RosterSections(tick, outgoing);
        }
        return outgoing;
    }

    /// <summary>A client that joined (or came back): every roster again, at the next call of <see cref="Step"/>.</summary>
    public void ClientJoined(int player) { foreach (var key in _signature.Keys.ToList()) _signature[key] = []; _ = player; }

    readonly List<int> _pendingRevived = [];

    void NoteEvents()
    {
        if (!_host.InBattle) { Array.Fill(_last, SquadState.Free); _pendingRevived.Clear(); return; }
        // A revive outside a step is reported by the sim's next step: the "by" of last call's revives is read now.
        foreach (int i in _pendingRevived) _events.Add(new SquadEventWire(SquadEventKind.Revived, (byte)i, ReviverOf(i)));
        _pendingRevived.Clear();
        for (int i = 0; i < SquadRules.MaxTotal; i++)
        {
            var now = _sim.StateOf(i);
            var was = _last[i];
            if (now == was) continue;
            _last[i] = now;
            if (now == SquadState.Downed && was != SquadState.Downed) _events.Add(new SquadEventWire(SquadEventKind.Downed, (byte)i, 255));
            else if (was == SquadState.Downed && now == SquadState.Standing) _pendingRevived.Add(i);
            else if (was == SquadState.Downed && now == SquadState.Out) _events.Add(new SquadEventWire(SquadEventKind.Out, (byte)i, 255));
        }
    }

    byte ReviverOf(int index)
    {
        foreach (var r in _sim.Revives)
            if (_sim.IndexOf(_host.PlayerOf(r.Key), r.Slot) == index) return r.ByHuman >= 0 ? (byte)Math.Min(r.ByHuman, 199) : r.ByMarine >= 0 ? (byte)Math.Min(200 + r.ByMarine, 254) : (byte)255;
        return 255;
    }

    /// <summary>A recruit (a card pick): W3b calls this with the owner and the new Marine's slot.</summary>
    public void NoteRecruited(int slotIndex) => _events.Add(new SquadEventWire(SquadEventKind.Recruited, (byte)Math.Clamp(slotIndex, 0, SquadWire.MaxMarines - 1), 255));

    void RosterSections(uint tick, List<WarfrontSquadOutgoing> outgoing)
    {
        var save = _sim.Rosters.Export();
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
            int index = player >= 0 ? _sim.IndexOf(player, slot) : -1;
            byte inBattle = index >= 0 && _host.InBattle && _sim.StateOf(index) != SquadState.Free ? (byte)index : SquadRosterWire.NotInBattle;
            marines.Add(new SquadRosterWire((byte)slot, inBattle, m.Name, cls, p, s, melee, OwnPieces(m.Loadout), RecordOf(m.Record)));
        }
        int owning = _host.PlayerOf(owner.Key);
        return SquadWire.EncodeRoster(new SquadRosterSection(handle, owning is >= 0 and < 255 ? (byte)owning : SquadRosterSection.NoPlayer, owner.Held, marines));
    }
}
