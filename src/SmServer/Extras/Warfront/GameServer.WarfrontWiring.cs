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
        _wfWriter.Flush();
        if (WarfrontSquadsJson() is not { } json) return;
        try
        {
            Directory.CreateDirectory(s.Dir);
            string path = Path.Combine(s.Dir, WarfrontSquadsFile), tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { WfLog($"squads.json could not be written: {e.Message}"); }
    }

    /// <summary>The squads' save text, null when there is no wiring or it could not be serialised. Runs on the tick thread, so the snapshot is consistent.</summary>
    string? WarfrontSquadsJson()
    {
        if (_wfWiring is not { } wiring) return null;
        try { return JsonSerializer.Serialize(wiring.Export(), WarfrontSquadJson); }
        catch (Exception e) when (e is JsonException or NotSupportedException) { WfLog($"squads.json could not be written: {e.Message}"); return null; }
    }

    // ---- the extra retainers of a persistent progression (codebase fix 1) ---------------------------------------------------------------------

    /// <summary>The retainers beyond the first of a key's roster as progress records (class and name), or null when the roster does not exist.</summary>
    List<SmSim.Extras.Warfront.Progression.WarfrontExtraRetainer>? WarfrontRosterExtras(ulong key)
    {
        if (_wfWiring is not { } w || !w.Rosters.Has(key)) return null;
        var list = new List<SmSim.Extras.Warfront.Progression.WarfrontExtraRetainer>();
        for (int slot = 1; slot < w.Rosters.Count(key) && list.Count < SmSim.Extras.Warfront.Progression.WarfrontProgressFile.MaxExtras; slot++)
            if (w.Rosters.Marine(key, slot) is { } m)
                list.Add(new SmSim.Extras.Warfront.Progression.WarfrontExtraRetainer { ClassName = m.Loadout.ClassId, Name = m.Name });
        return list;
    }

    /// <summary>
    /// Makes a key's roster hold the run's two retainers (<see cref="SquadRules.RetainersPerPlayer"/>, owner 2026-11-23 card 2) through <see cref="SquadRosters.Conform(ulong, int)"/> when a progress record is applied at the ship;
    /// an old record's <paramref name="extras"/> (retired +1 Retainer cards) only lend their names to the Marines after the first. Service records of the kept Marines stay. No roster yet: the names wait (<see cref="WarfrontServerState.PendingExtras"/>) for it.
    /// </summary>
    void WarfrontRosterSync(WarfrontServerState s, ulong key, IReadOnlyList<SmSim.Extras.Warfront.Progression.WarfrontExtraRetainer> extras)
    {
        if (_wfWiring is not { } w || !w.Rosters.Has(key))
        {
            if (extras.Count > 0) s.PendingExtras[key] = extras.Select(e => new SmSim.Extras.Warfront.Progression.WarfrontExtraRetainer { ClassName = e.ClassName, Name = e.Name }).ToList();
            else s.PendingExtras.Remove(key);
            return;
        }
        w.Rosters.Conform(key, SquadRules.RetainersPerPlayer);
        for (int slot = 1; slot < w.Rosters.Count(key) && slot - 1 < extras.Count; slot++)
            if (!string.IsNullOrWhiteSpace(extras[slot - 1].Name) && w.Rosters.Marine(key, slot) is { } m) m.Name = extras[slot - 1].Name;
    }

    // ---- the tick, the sections, the commands ---------------------------------------------------------------------------------------------------

    /// <summary>The co-op clock, once a tick.</summary>
    void WarfrontCoopTick(WarfrontServerState s)
    {
        if (WarfrontCoopRules is not { } coop) return;
        coop.Tick(1.0 / WarfrontRate);
        WarfrontDefaultWatches(s, coop);
    }

    /// <summary>A spectator (a held joiner, or a fallen player past his revive window) has a watch target from the first tick, not from the first F.</summary>
    readonly Dictionary<int, int> _wfWatchMoves = new();

    void WarfrontDefaultWatches(WarfrontServerState s, WarfrontCoop coop)
    {
        if (s.KeyOf.Count == 0) return;
        List<WarfrontWatch>? cands = null;
        foreach (var (id, key) in s.KeyOf)
        {
            bool spectating = coop.RoleOf(key) == WarfrontCoopRole.SpectatingJoiner
                || (s.Campaign.Phase == WarfrontPhase.Battle && World.Tfw?.WarfrontDownState(id).Phase == WarfrontDownPhase.Spectator);
            if (!spectating && coop.WatchOf(key).Kind == WarfrontWatchKind.None) continue;
            var before = coop.WatchOf(key);
            bool changed = coop.DefaultWatch(key, spectating, spectating ? cands ??= WarfrontWatchCandidates() : []);
            if (!changed) continue;
            var now = coop.WatchOf(key);
            if (spectating && before.Kind == WarfrontWatchKind.None)
            {
                Vector3? centre = null;
                WarfrontViewCentre(id, ref centre);
                _wfWatchMoves[id] = 0;
                WfLog($"P{id} spectates: default watch {now.Kind} {now.Id}; crowd data centred on {(centre is { } c ? $"({c.X:0},{c.Y:0},{c.Z:0})" : "the own body")}");
            }
            else if (spectating) _wfWatchMoves[id] = _wfWatchMoves.GetValueOrDefault(id) + 1;   // a watched body fell: counted, logged with the drop
            else if (World.Players.TryGetValue(id, out var back))
            {
                WfLog($"P{id} is back in the fight at ({back.Position.X:0},{back.Position.Y:0},{back.Position.Z:0}), alive {back.Alive}; the watch is dropped ({_wfWatchMoves.GetValueOrDefault(id)} later change(s) while spectating)");
                _wfWatchMoves.Remove(id);
            }
        }
    }

    partial void WarfrontSendSquad()
    {
        if (_wf is not { } s || _wfWiring is not { } wiring || _wfHost is null) return;
        _wfSender ??= new WarfrontSquadSender(wiring.Retainers, wiring.Rosters, World.Config, _wfHost);   // Phase B T2: one registry for the whole run
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
        WarfrontApplyQueuedProgress(s);   // a progress record that waited for the ship is applied before the class confirmation
        WarfrontCoopCommand(key, payload, WarfrontWatchCandidates());
    }

    /// <summary>The watchable targets in order: living humans, then standing Marines.</summary>
    List<WarfrontWatch> WarfrontWatchCandidates()
    {
        if (_wf is not { } s) return [];
        var humans = s.KeyOf.Where(kv => World.Players.TryGetValue(kv.Key, out var p) && p.Alive && WarfrontCoopRules?.RoleOf(kv.Value) == WarfrontCoopRole.Player).Select(kv => kv.Key);
        var marines = new List<int>();
        if (_wfWiring is { } w)
            foreach (var r in w.Retainers.All) if (r.State == SquadState.Standing) marines.Add(r.Index);
        return WarfrontCoop.Candidates(humans, marines);
    }

    /// <summary>A spectating client's crowd data is centred on the Marine or player it watches.</summary>
    partial void WarfrontViewCentre(int player, ref Vector3? centre)
    {
        if (_wf is not { } s || WarfrontCoopRules is not { } coop || !s.KeyOf.TryGetValue(player, out ulong key)) return;
        if (coop.RoleOf(key) != WarfrontCoopRole.SpectatingJoiner && s.Campaign.Phase != WarfrontPhase.Battle) return;
        var watch = coop.WatchOf(key);
        if (watch.Kind == WarfrontWatchKind.Player && World.Players.TryGetValue(watch.Id, out var target)) centre = target.Position;
        else if (watch.Kind == WarfrontWatchKind.Marine && _wfWiring is { } w && w.Retainers.View(watch.Id) is { } marine && w.Retainers.ByIndex(watch.Id) is { WorldId: > 0 }) centre = marine.Position;
    }
}
