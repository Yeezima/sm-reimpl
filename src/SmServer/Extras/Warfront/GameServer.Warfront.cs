using System.Security.Cryptography;
using SmContent.Extras.Warfront;
using SmContent.Extras.Warfront.Maps;
using SmNet;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Maps;
using SmSim.Extras.Warfront.Coop;

namespace SmServer;

/// <summary>
/// Warfront on the server (slice W5a; docs/added-content.md section 11): one stage process per deployment (the hub and ONE battlefield), the campaign in <c>run.json</c>, players identified by campaign key,
/// the leader's deployment, the stage change and the resume. Every hook returns at once unless the match has the Warfront flag (<see cref="_wf"/> is set only then).
/// </summary>
public sealed partial class GameServer
{
    /// <summary>Seconds a player waits for its Identify before the server makes a key for the connection (plan section 4; bots without a key file).</summary>
    const int WarfrontIdentifySeconds = 5;
    /// <summary>Seconds a new stage waits for the players of the deployment to rejoin before it lands without them (ours).</summary>
    const int WarfrontArrivalSeconds = 20;
    /// <summary>Seconds a stage keeps answering after it said Go, so every client hears it (ours).</summary>
    const int WarfrontGoLingerSeconds = 2;

    sealed class WarfrontServerState
    {
        public required WarfrontCampaign Campaign;
        public readonly HashSet<int> DownSent = new();
        public required string Dir;
        public required string Map;
        public int MaxHumans = 4;
        public DateTime StartedUtc = DateTime.UtcNow;
        /// <summary>Identified players: player id to campaign key (never logged in full, never sent to another client).</summary>
        public readonly Dictionary<int, ulong> KeyOf = new();
        /// <summary>Connected, not yet identified: the tick of the join.</summary>
        public readonly Dictionary<int, uint> Waiting = new();
        public readonly HashSet<int> Known = new();
        public readonly Dictionary<int, byte[]> LastHub = new();
        public bool HubDirty;
        /// <summary>A short text for one player (a refused Identify), shown as Stage None status until the tick.</summary>
        public readonly Dictionary<int, (string Text, uint Until)> Notices = new();
        /// <summary>The exit code of the restart-due path: the next-stage code, or 0 when the supervisor is gone.</summary>
        public int ExitCode = ServerCommandLine.WarfrontStageExit;
        /// <summary>The run is over and its end was handled: no later save writes the finished campaign back.</summary>
        public bool Finished;
        /// <summary>This process is about to leave on a rewritten run (a new run, a deployment that could not land): nothing may write the old campaign back.</summary>
        public bool Leaving;
        public int? SupervisorPid;
        public uint NextAliveCheck, NextPlaySave;
        public uint LandingStartTick, LandingLastLog, LandingEnqueued;
        public Prepare? TestPrep;
        // arrival (a stage that loaded a save in Travel)
        public bool Arriving;
        public List<ulong> Expected = new();
        public uint? FirstIdentifiedTick;
        public uint ArrivedUntilTick;
        // landing and battle
        public bool Landing, Landed;
        public WarfrontTarget? LandingTarget;
        public uint LandingNoticeTick;
        public string? GateRefusal;
        public uint GateRefusalTick;
        public int PrepPercent, ArrivedCount;
        public ushort CommandSeq;
        public bool? ForcedWin;
        public uint? ResolveAtTick;
        public bool ResolveAid;
        public bool ResolveWins;
        // stage change
        public Prepare? Prep;
        public GoState? Go;
        public bool RestartDue;
        // end of run
        public byte[]? ResultsBytes;
        public byte ResultsTransfer;
        public uint NextResultsTick;
    }

    sealed class Prepare
    {
        public required WarfrontTarget Target;
        public required string Map;
        public required DateTime Started;
        public volatile int Percent;
        public volatile string Status = "starting";
        public volatile bool Done;
        public string? Error;
        public string? Note;
        public DateTime LastLog = DateTime.UtcNow;
        public int GraphStartedAtPercent = 30;
        public DateTime GraphStarted;
        public int DelaySeconds;
    }

    sealed class GoState
    {
        public required string Map;
        public required WarfrontTarget? Target;
        public required uint Tick;
    }

    WarfrontServerState? _wf;

    /// <summary>Builds a map's caches (the travel wait's work); tests replace it with a stub.</summary>
    internal static Func<string, SimConfig, Action<int, string>?, string> WarfrontPrebuild = WarfrontMaps.Prebuild;

    internal SmSim.Extras.Warfront.Campaign.WarfrontCampaign? WarfrontCampaignForTests => _wf?.Campaign;
    internal bool WarfrontPreparingForTests => _wf?.Prep is not null;
    internal int? WarfrontPlayerOfForTests(ulong key) => WarfrontPlayerOf(key);

    /// <summary>Whether this server runs the Warfront campaign.</summary>
    public bool WarfrontActive => _wf is not null;

    /// <summary>Test aid and log helper: one line, UTC-stamped, never a full key.</summary>
    static void WfLog(string text) => Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}] warfront: {text}");

    /// <summary>The last four hex digits of a key: enough to tell two players apart in a log.</summary>
    static string KeyTail(ulong key) => "..." + (key & 0xFFFF).ToString("x4");

    // ---- map and set-up hooks ----------------------------------------------------------------------------------------------------------------------

    /// <summary>A wf_* map under the Warfront flag (the first line of TfwGeneratedMap): the lane slot's hub world, or the HQ's or fortress's collision; refused without the flag.</summary>
    static partial void WarfrontGeneratedMap(ContentExtras extras, ref string? name, ref GameMap? map, ref string? error, ref bool handled)
    {
        bool flag = extras.HasFlag(ContentExtras.Warfront);
        if (name is not null && name.StartsWith(WarfrontSlots.Prefix, StringComparison.Ordinal))
        {
            handled = true;
            if (!flag) { error = $"--map {name} is a Warfront map: it needs --content warfront"; return; }
        }
        else if (flag && name is null) { name = WarfrontStagePlan.FirstMap; handled = true; }
        else return;
        if (!WarfrontMaps.IsWarfrontMap(name)) { error = $"--map {name}: not a Warfront map (the maps are {string.Join(", ", WarfrontMaps.AllNames)})"; return; }
        var loaded = WarfrontMaps.Load(name!, out string? problem);
        if (loaded is null) { error = problem; return; }
        map = loaded.Tfw is { } tfw
            ? TfwGameMap(tfw, name!)
            : new GameMap(name!, loaded.Collision, loaded.Spawns, loaded.Hash, StartAreas: loaded.StartAreas);
        if (SmContent.Extras.Musou.AshfallLayoutCache.LastNote is { } cacheNote) Console.WriteLine($"ashfall: {cacheNote}");
        if (WarfrontLayoutCache.LastNote is { } layoutNote) Console.WriteLine($"warfront: {layoutNote}");
    }

    /// <summary>The walking graph of a lane slot, through its own cache file (<c>wf_xN-musou-&lt;key&gt;.nav</c>).</summary>
    partial void WarfrontNav(TfwBattlefield field, ref NavGraph? nav, ref string note, ref bool handled)
    {
        if (Map is null || WarfrontSlots.ByName(Map.Name) is not { } slot || !World.Config.Extras.HasFlag(ContentExtras.Warfront)) return;
        handled = true;
        try
        {
            var data = new TfwMapData(Map.Collision, Map.Spawns, Map.StartAreas ?? [], Map.Hash, field);
            nav = WarfrontWorld.Nav(slot, data, World.Config, out string built, WarfrontRunFiles.NavCacheOverride);
            _wfNav = nav;
            note = $"{built}, {nav.NodeCount} nodes";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            nav = null;
            note = $"nav graph failed ({e.GetType().Name}: {e.Message})";
        }
    }

    partial void WarfrontMapNote(ref string note)
    {
        if (!World.Config.Extras.HasFlag(ContentExtras.Warfront) || Map is null || WarfrontSlots.ByName(Map.Name) is not { } slot) return;
        note += $"; warfront: slot {slot.Number} {slot.Name} (lane {slot.Lane}, node {slot.Node}, band {slot.Band} {slot.Profile}, seed {slot.Seed})";
    }

    partial void WarfrontPlayerCap(ref int cap)
    {
        if (_wf is { } s) cap = Math.Min(cap, s.MaxHumans);
    }

    partial void WarfrontRestartDue(ref bool due, ref int code)
    {
        if (_wf is { RestartDue: true } s) { due = true; code = s.ExitCode; }
    }

    readonly WarfrontStuckLog _wfStuck = new(Console.WriteLine);

    /// <summary>The progression runtime on the run's own campaign, with this server's key map (a human is a campaign player only once identified).</summary>
    void WarfrontBindProgression(WarfrontServerState s)
    {
        if (World.Tfw?.Warfront is not { } rules) return;
        rules.Progression = new WarfrontProgressionRuntime(s.Campaign) { KeyOf = id => s.KeyOf.TryGetValue(id, out ulong k) ? k : null };   // built once per process, never replaced
    }

    /// <summary>Writes run.json unless the run was finished (the archive removed it; a finished campaign must never be written back).</summary>
    void WfSave(WarfrontServerState s)
    {
        if (s.Finished || s.Leaving) return;
        WarfrontSaveSquads(s);   // the squads first, then the run: killed between the two, a pick is offered again instead of a paid card with no Marine
        try { WarfrontSave.Write(s.Dir, s.Campaign); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { WfLog($"run.json could not be written: {e.Message}"); }
    }

    /// <summary>At the end of the constructor, after the war's set-up: the campaign (loaded or new), the seams, the stage's arrival.</summary>
    partial void WarfrontCreated()
    {
        if (!World.Config.Extras.HasFlag(ContentExtras.Warfront) || Map is null) return;
        string dir = WarfrontRunFiles.Dir;
        int maxHumans = Math.Clamp(WarfrontRunFiles.MaxHumans, 1, 4);
        WarfrontCampaign campaign;
        string how;
        try
        {
            string before = WarfrontSave.Exists(dir) ? "" : "none";
            var loaded = WarfrontSave.Load(dir, WarfrontRunFiles.Rolls());
            if (loaded is null) { campaign = WarfrontRunFiles.NewRun(dir); how = $"a new run (seed {campaign.RunSeed:X}); " + before; }
            else { campaign = loaded; how = $"run loaded: {campaign.Phase}, deployment {campaign.Deployments}, state {campaign.StateHash()[..8]}"; WarfrontSave.Write(dir, campaign); }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            WfLog($"run.json is unreadable ({e.Message}); starting a new run");
            campaign = WarfrontRunFiles.NewRun(dir);
            how = "a new run after an unreadable save";
        }
        var state = new WarfrontServerState { Campaign = campaign, Dir = dir, Map = Map.Name, MaxHumans = maxHumans };
        if (int.TryParse(Environment.GetEnvironmentVariable(WarfrontRunFiles.SupervisorPidVariable), out int supervisor) && supervisor > 0) state.SupervisorPid = supervisor;
        _wf = state;
        if (WarfrontStagePlan.HasShip(Map.Name) && campaign.Phase != WarfrontPhase.Travel) WarfrontRunFiles.WriteStage(dir, new(Map.Name, [], Map.Name));
        WfLog($"{how}; map {Map.Name}; at most {maxHumans} human(s)");
        foreach (var aid in new[] { "SM_WF_ROLLS", "SM_WF_START", "SM_WF_RESOLVE", "SM_WF_PREBUILD_DELAY", "SM_WF_PREPARE_TEST", "SM_WF_DIR", "SM_WF_SEED" })
            if (Environment.GetEnvironmentVariable(aid) is { Length: > 0 } v) WfLog($"test aid {aid}={(aid == "SM_WF_DIR" ? "(set)" : v)}");
        string? resolve = Environment.GetEnvironmentVariable("SM_WF_RESOLVE");
        if (resolve is "win" or "wipe") { state.ResolveAid = true; state.ResolveWins = resolve == "win"; }

        if (World.Tfw?.Warfront is { } rules)
        {
            rules.Leader = () => WarfrontLeaderPlayer() ?? -1;
            rules.NodeEnded = (lane, node, result) => WarfrontNodeEnded(lane, node, result);
            rules.LateJoinAllowed = id => state.KeyOf.ContainsKey(id);   // a human not yet identified is not flown into a battle (W12 adds its own condition on top)
            rules.StuckSink = r => _wfStuck.Record(Map.Name, r.Type.ToString(), r.At, r.Why == WarfrontStuckWhy.Removed ? WarfrontStuckAction.Removed : WarfrontStuckAction.Relocated, r.Why == WarfrontStuckWhy.Cleared ? "position-clearing" : "stranded");
            WarfrontBindProgression(state);
            WarfrontBuildSquads(state, rules);
        }
        else WfLog($"{Map.Name} has no hub and no battle rules yet (W13b and W14b): a deployment here runs under SM_WF_RESOLVE only");

        if (campaign.Phase == WarfrontPhase.Travel && campaign.CurrentTarget is { } target)
        {
            state.Arriving = true;
            state.Expected = WarfrontRunFiles.ReadStage(dir)?.Expected ?? [];
            WfLog($"arrival: travelling to {target.Kind}{(target.Lane >= 0 ? " lane " + (char)('A' + target.Lane) : "")} on {Map.Name}; waiting for {state.Expected.Count} player(s) to rejoin (at most {WarfrontArrivalSeconds} s after the first)");
        }
        if (campaign.IsOver) { WfLog("the loaded run is over: its end was not archived; finishing it now"); WarfrontFinishRun(state); }
        Stepped += _ => WarfrontStep();
    }

    // ---- the tick -----------------------------------------------------------------------------------------------------------------------------------

    int WarfrontRate => Math.Max(1, World.Config.TickRate);

    void WarfrontStep()
    {
        if (_wf is not { } s) return;
        try
        {
            WarfrontSync(s);
            WarfrontClocks(s);
            WarfrontCoopTick(s);
            WarfrontIdentityTimeouts(s);
            WarfrontArrivalStep(s);
            WarfrontBattleWatch(s);
            WarfrontResolveAid(s);
            WarfrontPrepareWatch(s);
            WarfrontGoWatch(s);
            WarfrontMeasureStep(s);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            WfLog($"tick error: {e}");
        }
    }

    /// <summary>Joins and leaves, by comparing the connected players with the ones known.</summary>
    void WarfrontSync(WarfrontServerState s)
    {
        var now = new HashSet<int>(Players);
        foreach (int id in now)
            if (s.Known.Add(id))
            {
                s.Waiting[id] = World.Tick;
                WfLog($"P{id} joined; held on the ship until it identifies (at most {WarfrontIdentifySeconds} s)");
            }
        foreach (int id in s.Known.Where(k => !now.Contains(k)).ToList())
        {
            s.Known.Remove(id); s.Waiting.Remove(id); s.LastHub.Remove(id);
            if (s.KeyOf.Remove(id, out ulong key))
            {
                var down = World.Tfw?.WarfrontDownState(id) ?? default;
                var state = s.Campaign.Phase != WarfrontPhase.Battle ? WarfrontLeaveState.NotInBattle
                    : down.Phase == WarfrontDownPhase.Spectator ? WarfrontLeaveState.Spectating
                    : down.Phase == WarfrontDownPhase.Downed ? WarfrontLeaveState.Downed : WarfrontLeaveState.Alive;
                if (WarfrontCoopRules is { } coop) coop.Left(key, state, down.SecondsLeft);
                else s.Campaign.Leave(key);
                WfLog($"P{id} (key {KeyTail(key)}) left; leader is now {(s.Campaign.Leader is { } l ? KeyTail(l) : "nobody")}");
                WfSave(s);
                s.HubDirty = true;
            }
        }
    }

    void WarfrontIdentityTimeouts(WarfrontServerState s)
    {
        uint limit = (uint)(WarfrontIdentifySeconds * WarfrontRate);
        foreach (var (id, tick) in s.Waiting.ToList())
            if (World.Tick - tick >= limit && _peers.Values.Any(p => p.PlayerId == id && p.Bot))   // a human stays held until its Identify arrives (the client resends); only a bot gets a made key
            {
                ulong made = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) | 1UL;
                WfLog($"P{id} (a bot) sent no Identify in {WarfrontIdentifySeconds} s: treated as a new key made for this connection only");
                WarfrontIdentify(id, made, "");
            }
    }

    /// <summary>The player id of a campaign key, or null.</summary>
    int? WarfrontPlayerOf(ulong key)
    {
        if (_wf is not { } s) return null;
        foreach (var (id, k) in s.KeyOf) if (k == key) return id;
        return null;
    }

    /// <summary>The player id of the campaign's leader (null: nobody identified).</summary>
    int? WarfrontLeaderPlayer() => _wf?.Campaign.Leader is { } k ? WarfrontPlayerOf(k) : null;

    /// <summary>An Identify (or the server's own key after the wait): the player becomes a campaign player.</summary>
    void WarfrontIdentify(int player, ulong key, string name)
    {
        if (_wf is not { } s || !s.Known.Contains(player)) return;
        if (s.KeyOf.TryGetValue(player, out ulong have)) { if (have != key) WfLog($"P{player} sent a second Identify with another key: ignored"); return; }
        if (s.KeyOf.ContainsValue(key)) { WfLog($"P{player} identified with a key already connected ({KeyTail(key)}): refused"); WarfrontNotice(s, player, "that campaign key is already connected"); return; }
        string shown = string.IsNullOrWhiteSpace(name) ? $"P{player}" : name.Trim();
        WarfrontJoinKind kind;
        if (WarfrontCoopRules is { } coop)
        {
            var joined = coop.Connect(key, shown, player);   // in place of Campaign.Join: the roles, the hold, the grants
            if (!joined.Admitted) { WfLog($"P{player} was refused: {joined.Refusal}"); WarfrontNotice(s, player, joined.Refusal ?? "the campaign is full"); return; }
            kind = joined.Kind;
            if (joined.SpectatorSeconds > 0) WfLog($"P{player} returns as a fallen player's spectator for {joined.SpectatorSeconds:0} s");
        }
        else if (!s.Campaign.Join(key, shown, out kind)) { WfLog($"P{player} could not join the campaign: it is full"); WarfrontNotice(s, player, "the campaign is full"); return; }
        s.KeyOf[player] = key;
        _wfSender?.ClientJoined(player);
        s.Waiting.Remove(player);
        s.FirstIdentifiedTick ??= World.Tick;
        WfLog($"P{player} identified as a {(kind == WarfrontJoinKind.New ? "new" : "returning")} player (key {KeyTail(key)}, \"{shown}\"); leader {(s.Campaign.Leader is { } l ? KeyTail(l) : "nobody")}");
        WfSave(s);
        s.HubDirty = true;
        s.LastHub.Remove(player);
    }

    // ---- seams for the later slices (plain partial voids, no behaviour; W8 progression, W10 squad, W12 coop implement them in their own files) ---------------------------------

    /// <summary>W12: while a client spectates, the position its crowd data is centred on (default: untouched). Declared in GameServer.Musou.cs, called where Musou, the war and the elite bars choose each client's centre.</summary>
    /// <summary>W8, W10, W12: each sends its own envelope sections from the Warfront send.</summary>
    partial void WarfrontSendProgression();
    partial void WarfrontSendSquad();
    partial void WarfrontSendCoop();
    /// <summary>W10 (tag 41) and W12 (tag 42): a command of another slice's tag.</summary>
    partial void WarfrontSquadCommand(int player, byte tag, ReadOnlySpan<byte> payload, ref bool handled);
    partial void WarfrontCoopCommand(int player, byte tag, ReadOnlySpan<byte> payload, ref bool handled);

    /// <summary>A short text for one player, shown as the Stage status for 5 s.</summary>
    void WarfrontNotice(WarfrontServerState s, int player, string text) => s.Notices[player] = (text, World.Tick + (uint)(5 * WarfrontRate));

    /// <summary>Once a tick: the run clock (time with someone connected; saved every 30 s), the supervisor's life.</summary>
    void WarfrontClocks(WarfrontServerState s)
    {
        int rate = WarfrontRate;
        s.Campaign.AddPlaySeconds(1.0 / rate);
        if (World.Tick >= s.NextPlaySave) { s.NextPlaySave = World.Tick + (uint)(30 * rate); if (s.Campaign.PlaySeconds > 0) WfSave(s); }
        if (s.SupervisorPid is { } pid && World.Tick >= s.NextAliveCheck)
        {
            s.NextAliveCheck = World.Tick + (uint)rate;
            bool alive;
            try { alive = !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { alive = false; }
            if (!alive && !s.RestartDue)
            {
                WfSave(s);
                s.ExitCode = 0; s.RestartDue = true;
                WfLog($"the supervisor (pid {pid}) is gone: the run is saved, this stage leaves with code 0");
            }
        }
    }

    /// <summary>The humans connected and identified.</summary>
    int WarfrontIdentified => _wf?.KeyOf.Count ?? 0;
}
