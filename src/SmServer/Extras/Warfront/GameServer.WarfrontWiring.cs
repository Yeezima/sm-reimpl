using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmNet;
using SmNet.Extras.Warfront;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Coop;
using SmSim.Extras.Warfront.Squad;

namespace SmServer;

/// <summary>
/// Warfront's squad system, co-op rules and their wire on the server (slice W5a second pass): the one <see cref="WarfrontSquadWiring"/> per process (built after the real progression runtime and never replaced; its
/// notifying seams are subscribed with +=), W10's sender and command handler, W12's co-op rules, and the glue between them and the campaign. <c>squads.json</c> is written BEFORE <c>run.json</c> at every save
/// point, so a kill between the two offers a pick again (at worst one Marine too many) instead of a paid card with no Marine.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>The file beside run.json that holds the squads (rosters, class picks): never read by a new run.</summary>
    public const string WarfrontSquadsFile = "squads.json";

    static readonly JsonSerializerOptions WarfrontSquadJson = new() { Converters = { new JsonStringEnumConverter() }, IncludeFields = true };

    WarfrontSquadWiring? _wfWiring;
    internal WarfrontSquadWiring? WarfrontWiringForTests => _wfWiring;   // a test aid (W14b): the real wipe knocks the retainers out first
    WarfrontSquadSender? _wfSender;
    WarfrontSquadSim? _wfSenderSim;
    WarfrontSquadCommands? _wfSquadCommands;
    NavGraph? _wfNav;
    WfHost? _wfHost;
    byte[]? _wfCoopLast;
    readonly Dictionary<int, byte[]> _wfCoopSent = new();
    readonly Dictionary<int, uint> _wfCoopSentTick = new();

    /// <summary>One object that is every host the squad and co-op code asks questions of: this server's state, read at each call.</summary>
    sealed class WfHost(GameServer server) : IWarfrontSquadHost, IWarfrontSquadCommandHost, IWarfrontCoopHost
    {
        WarfrontServerState S => server._wf!;
        public int Rate => server.WarfrontRate;
        public bool InBattle => server.World.Tfw?.Battle is not null;
        public IReadOnlyList<(int Player, ulong Key)> Clients => S.KeyOf.Select(p => (p.Key, p.Value)).ToList();
        public ulong Handle(ulong viewerKey, ulong ownerKey) => ownerKey == viewerKey ? ownerKey : WarfrontWire.Pseudonym(ownerKey, S.Campaign.RunSeed);
        public int PlayerOf(ulong key) => server.WarfrontPlayerOf(key) ?? -1;
        public ulong? KeyOf(int player) => S.KeyOf.TryGetValue(player, out ulong k) ? k : null;
        public bool Aboard(int player) => server.World.Tfw is { Battle: null } && server.World.Players.TryGetValue(player, out var p) && p.Alive;
        public bool FightingAlive(int player) => server.World.Tfw?.Battle is not null && server.World.Players.TryGetValue(player, out var p) && p.Alive;
        public bool HasJumpPack(int player) => server.World.Players.TryGetValue(player, out var p) && p.Class >= 0 && p.Class < server.World.Config.Classes.Count && server.World.Config.Classes[p.Class].JumpPack >= 0;
        public Vector3 PositionOf(int player) => server.World.Players.TryGetValue(player, out var p) ? p.Position : Vector3.Zero;
        public bool OnGraph(Vector3 point) => server._wfNav is { } nav && nav.Nearest(point) >= 0;
        public double Now => server.World.Tick / (double)server.WarfrontRate;
        public bool IsLibrarian(int player) => server.World.Players.TryGetValue(player, out var p) && SmSim.Extras.Musou.LibrarianRules.IsLibrarian(server.World.Config, p.Class);
        public bool OrderJumpPound(int player, Vector3 mark) => server._wfWiring?.OrderJumpPound(player, mark) ?? false;
        public void SetSquadClass(ulong key, byte option) => server._wfWiring?.SetSquadClass(key, (SquadClass)Math.Min(option, (byte)2));
        // co-op
        public bool HasRoster(ulong key) => server._wfWiring?.HasRoster(key) ?? false;
        public void AddStartingRetainer(ulong key) => server._wfWiring?.AddStartingRetainer(key);
        public void HoldSquad(ulong key) => server._wfWiring?.HoldSquad(key);
        public void RebindSquad(ulong key, int player) => server._wfWiring?.RebindSquad(key, player);
        public void Save() => server.WfSave(S);
    }

    /// <summary>Builds the wiring, the sender, the commands and the co-op rules once the run's battle rules (and the real progression runtime) exist; reads squads.json.</summary>
    void WarfrontBuildSquads(WarfrontServerState s, WarfrontBattleRules rules)
    {
        _wfHost = new WfHost(this);
        var wiring = new WarfrontSquadWiring(World, rules, s.Campaign, s.Campaign.RunSeed)
        {
            LookOf = id => AppearanceOf(id)?.Code,
            StuckLog = _wfStuck,
            SlotName = () => Map?.Name ?? "",
            SaveCallback = () => WfSave(s),
        };
        _wfWiring = wiring;
        try
        {
            string path = Path.Combine(s.Dir, WarfrontSquadsFile);
            if (File.Exists(path)) wiring.Import(JsonSerializer.Deserialize<WarfrontSquadSave>(File.ReadAllText(path), WarfrontSquadJson));
            else wiring.Import(null);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            WfLog($"squads.json is unreadable ({e.Message}); the squads start empty");
            wiring.Import(null);
        }
        _wfSquadCommands = new WarfrontSquadCommands(wiring.Rosters, _wfHost);

        var coop = new WarfrontCoop(s.Campaign, _wfHost) { HumanCap = s.MaxHumans };
        WarfrontCoopRules = coop;
        var eligible = coop.AttachTo(rules);   // CoopHeld, LateJoinAllowed and the XP predicate
        var coopLate = rules.LateJoinAllowed;
        rules.LateJoinAllowed = id => s.KeyOf.ContainsKey(id) && (coopLate?.Invoke(id) ?? true);   // identified AND the co-op rules allow it
        rules.Progression.Xp.Eligible = eligible;
        rules.Progression.Picked += r => coop.PicksChanged(r.Key);   // += : the wiring and others listen too
        WfLog($"squads: wiring built ({wiring.Rosters.Export().Owners.Count} roster(s) loaded); co-op rules on, cap {s.MaxHumans}");
    }

    /// <summary>The squads on disk, written before run.json (see the class summary).</summary>
    void WarfrontSaveSquads(WarfrontServerState s)
    {
        if (_wfWiring is not { } wiring) return;
        try
        {
            Directory.CreateDirectory(s.Dir);
            string path = Path.Combine(s.Dir, WarfrontSquadsFile), tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(wiring.Export(), WarfrontSquadJson));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { WfLog($"squads.json could not be written: {e.Message}"); }
    }

    // ---- the tick, the sections, the commands ---------------------------------------------------------------------------------------------------

    /// <summary>The co-op clock, once a tick.</summary>
    void WarfrontCoopTick(WarfrontServerState s) => WarfrontCoopRules?.Tick(1.0 / WarfrontRate);

    partial void WarfrontSendSquad()
    {
        if (_wf is not { } s || _wfWiring is not { } wiring || _wfHost is null) return;
        if (_wfSender is null || !ReferenceEquals(_wfSenderSim, wiring.Sim))
        {
            _wfSender = new WarfrontSquadSender(wiring.Sim, World.Config, _wfHost);   // a new sim per battle: read wiring.Sim, never keep it past its battle
            _wfSenderSim = wiring.Sim;
        }
        foreach (var o in _wfSender.Step(World.Tick))
            SendExtras(new ExtrasUpdate(World.Tick, [(o.Tag, o.Payload)]), o.Player < 0 ? null : o.Player);
    }

    partial void WarfrontSendCoop()
    {
        if (_wf is not { } s || WarfrontCoopRules is null) return;
        uint tick = World.Tick;
        foreach (var (id, key) in s.KeyOf.ToList())
        {
            if (WarfrontCoopSectionFor(key) is not { } bytes) continue;
            bool changed = !_wfCoopSent.TryGetValue(id, out var last) || !last.AsSpan().SequenceEqual(bytes);
            if (!changed && tick - _wfCoopSentTick.GetValueOrDefault(id) < (uint)(2 * WarfrontRate)) continue;   // on change and every 2 s
            _wfCoopSent[id] = bytes; _wfCoopSentTick[id] = tick;
            SendExtras(new ExtrasUpdate(tick, [(WarfrontCoopWire.EnvelopeTag, bytes)]), id);
        }
    }

    partial void WarfrontSquadCommand(int player, byte tag, ReadOnlySpan<byte> payload, ref bool handled)
    {
        if (_wf is not { } s || _wfSquadCommands is null) return;
        handled = true;
        if (_wfSquadCommands.Handle(player, payload)) s.HubDirty = true;
    }

    partial void WarfrontCoopCommand(int player, byte tag, ReadOnlySpan<byte> payload, ref bool handled)
    {
        if (_wf is not { } s || !s.KeyOf.TryGetValue(player, out ulong key)) return;
        handled = true;
        WarfrontCoopCommand(key, payload, WarfrontWatchCandidates());
    }

    /// <summary>The watchable targets in order: living humans, then standing Marines.</summary>
    List<WarfrontWatch> WarfrontWatchCandidates()
    {
        var list = new List<WarfrontWatch>();
        if (_wf is not { } s) return list;
        foreach (int id in s.KeyOf.Keys.OrderBy(i => i))
            if (World.Players.TryGetValue(id, out var p) && p.Alive && WarfrontCoopRules?.RoleOf(s.KeyOf[id]) == WarfrontCoopRole.Player) list.Add(new WarfrontWatch(WarfrontWatchKind.Player, id));
        if (_wfWiring is { } w)
            for (int i = 0; i < SquadRules.MaxTotal; i++) if (w.Sim.View(i) is { State: SquadState.Standing }) list.Add(new WarfrontWatch(WarfrontWatchKind.Marine, i));
        return list;
    }

    /// <summary>A spectating client's crowd data is centred on the Marine or player it watches.</summary>
    partial void WarfrontViewCentre(int player, ref Vector3? centre)
    {
        if (_wf is not { } s || WarfrontCoopRules is not { } coop || !s.KeyOf.TryGetValue(player, out ulong key)) return;
        if (coop.RoleOf(key) != WarfrontCoopRole.SpectatingJoiner && s.Campaign.Phase != WarfrontPhase.Battle) return;
        var watch = coop.WatchOf(key);
        if (watch.Kind == WarfrontWatchKind.Player && World.Players.TryGetValue(watch.Id, out var target)) centre = target.Position;
        else if (watch.Kind == WarfrontWatchKind.Marine && _wfWiring is { } w && w.Sim.View(watch.Id) is { } marine) centre = marine.Position;
    }
}
