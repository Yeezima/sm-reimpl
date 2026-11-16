using System.Net;
using SmContent;
using SmNet;
using SmSim;

namespace SmServer;

/// <summary>The developer tools' setup (protocol 80): the address the server listens on (the gate needs loopback), the launcher of the tools' bots (null: bots cannot be added) and the scalable clock the server runs on (null: the time scale is recorded and sent but changes nothing).</summary>
public sealed record DevToolsSetup(IPAddress Bind, IDevBotLauncher? Launcher, ScalableClock? Clock = null);

public sealed partial record ServerOptions
{
    /// <summary>
    /// Protocol 80 (dev-tools; the owner's request of 2026-11-17): null (the default, and every public or dedicated server) keeps the tools off. Set from <c>--dev-tools</c>; the server
    /// still refuses them unless it listens on loopback alone (<see cref="DevToolsGate"/>).
    /// </summary>
    public DevToolsSetup? DevTools { get; init; }
}

/// <summary>
/// The rule that decides whether a developer-tools request is obeyed (docs/commands.md "Developer tools"): the server was started with <c>--dev-tools</c> AND listens on a loopback
/// address only (127.0.0.1 or ::1, never every interface), AND the request comes from a loopback address. A request from a connection the transport has no address for (an in-process
/// test link) counts as local: only the real UDP transport carries a network, and it always has one.
/// </summary>
public static class DevToolsGate
{
    /// <summary>The tools are on: the switch was given and the bind address is loopback.</summary>
    public static bool Enabled(DevToolsSetup? setup) => setup is not null && IPAddress.IsLoopback(setup.Bind);

    /// <summary>The request's address is loopback (or the transport has none).</summary>
    public static bool FromLoopback(IPEndPoint? remote) => remote is null || IPAddress.IsLoopback(remote.Address);

    /// <summary>Both rules.</summary>
    public static bool Allows(DevToolsSetup? setup, IPEndPoint? remote) => Enabled(setup) && FromLoopback(remote);
}

/// <summary>
/// Developer tools (protocol 80; owner's request, 2026-11-17; ours, test aids, never on a public server): invincibility, frozen bots, infinite ammunition, infinite jump-pack power and
/// adding or removing test bots, asked by the in-game panel (F6) and applied here, on the server, only when <see cref="DevToolsGate"/> says so. With <c>--dev-tools</c> off nothing here
/// runs: no state is sent, no request is read beyond a one-time log line, and the tick loop pays one null check.
/// </summary>
public sealed partial class GameServer
{
    sealed class ToolBot(IDevBotProcess process, Team team, uint token, TimeSpan startedAt)
    {
        /// <summary>The nonce this bot's Hello carries (its own, random): the player with it is this process, whatever its side.</summary>
        public uint Token { get; } = token;
        public TimeSpan StartedAt { get; } = startedAt;
        public IDevBotProcess Process { get; } = process;
        public Team Team { get; } = team;
        /// <summary>The connection of the player this process became (null while it is still joining).</summary>
        public int? Connection;
    }

    readonly List<ToolBot> _toolBots = new();
    readonly HashSet<string> _devRefusals = new();
    readonly Dictionary<Team, int> _toolBotsStarted = new();
    bool _devDirty;
    int _devTimeScale, _devAccuracy = Protocol.DevNoValue, _devDamage = -1, _devRange;
    bool _devDamageSet;
    DevToolsFlags _lastDevFlags;
    byte _devNoteSerial;
    DevNote _devNote;
    TimeSpan _nextDevState;
    (int Loyalists, int Chaos, int ToolLoyalists, int ToolChaos) _devCounts;

    /// <summary>Bots still joining that the tools may have waiting at once (each is a whole game client, 0.65 GB).</summary>
    public const int MaxStartingToolBots = 3;
    /// <summary>A tool bot that has not joined after this long is stopped and its slot freed.</summary>
    public static readonly TimeSpan ToolBotJoinTimeout = TimeSpan.FromSeconds(60);
    /// <summary>Developer-tools requests taken per peer per second.</summary>
    const double DevRequestRate = 6;

    /// <summary>Whether the tools are on for this server (the switch given and the bind address loopback).</summary>
    public bool DevToolsOn => DevToolsGate.Enabled(_options.DevTools);

    /// <summary>The bots the tools started and that are still running.</summary>
    public int ToolBotCount => _toolBots.Count(b => b.Process.Running);

    void NoteDevRefused(string why)
    {
        if (_devRefusals.Add(why)) Log?.Invoke($"dev tools request ignored: {why} (logged once)");
    }

    void Answer(DevNote note) { _devNote = note; _devNoteSerial++; _devDirty = true; }

    void OnDevCommand(int connection, Peer peer, DevCommand cmd)
    {
        peer.LastHeard = _clock.Now;
        if (!DevToolsOn) { NoteDevRefused("this server was not started with --dev-tools on a loopback address"); return; }
        var remote = _transport.EndPointOf(connection);
        if (!DevToolsGate.FromLoopback(remote)) { NoteDevRefused($"{remote?.Address} is not a loopback address"); return; }
        if (peer.Spectator || peer.Bot) return;   // a bot (or a spectator) never commands the tools
        // The rate limit counts real time: the server's clock stands still when paused, and a bucket on it would never refill (a pause that locks itself in). Changing the time scale is never limited.
        var real = _options.DevTools?.Clock?.Real.Now ?? _clock.Now;
        peer.DevRequests ??= new TokenBucket(DevRequestRate, DevRequestRate);
        if (cmd.Kind != DevCommandKind.SetTimeScale && !peer.DevRequests.TryTake(real)) { Answer(DevNote.TooFast); SendDevStates(); return; }
        switch (cmd.Kind)
        {
            case DevCommandKind.SetFlag: SetDevFlag(peer, (DevToolsFlags)cmd.Argument, cmd.Value); break;
            case DevCommandKind.AddBot when cmd.Argument <= DevSides.Chaos: AddToolBot(peer, cmd.Argument == DevSides.Chaos ? Team.Chaos : Team.SpaceMarines); break;
            case DevCommandKind.RemoveBot when cmd.Argument <= DevSides.Chaos: RemoveToolBot(peer, cmd.Argument == DevSides.Chaos ? Team.Chaos : Team.SpaceMarines); break;
            case DevCommandKind.EndMatch:
                if (World.DevEndMatch()) { Log?.Invoke($"dev tools: end match now (player {peer.PlayerId}): {World.State}, winner {World.Winner}"); Answer(DevNote.Applied); }
                else Answer(DevNote.NotPlaying);
                break;
            case DevCommandKind.SkipMatch:
                if (World.DevSkipMatch(MatchFlowWire.RotationRejoinTicks)) { Log?.Invoke($"dev tools: skip to the next match{(World.Rules is { RotatesAfterMatch: true } ? " (the next map in the rotation)" : "")} (player {peer.PlayerId})"); Answer(DevNote.Applied); }
                else Answer(DevNote.NotPlaying);
                break;
            case DevCommandKind.RestartMatch:
                bool wasIndefinite = (World.DevFlags & DevToolsFlags.Indefinite) != 0;
                if (World.DevRestartMatch())
                {
                    Log?.Invoke($"dev tools: restart match, same map, fresh scores (player {peer.PlayerId}): {World.State}");
                    if (wasIndefinite) Log?.Invoke("dev tools: indefinite match OFF (the match was restarted: the new match is normal)");
                    Answer(DevNote.Applied);
                }
                else Answer(DevNote.NotPlaying);
                break;
            case DevCommandKind.KillMe:
                if (World.DevKill(peer.PlayerId)) { Log?.Invoke($"dev tools: kill me (player {peer.PlayerId})"); Answer(DevNote.Applied); }
                else Answer(DevNote.NotPlaying);
                break;
            case DevCommandKind.KillBots when cmd.Argument <= 2: KillBots(peer, cmd.Argument); break;
            case DevCommandKind.Teleport: TeleportRequester(peer, cmd); break;
            case DevCommandKind.SetTimeScale: SetTimeScale(peer, cmd.Argument); break;
            case DevCommandKind.CancelSync: SendCancelSync(connection); break;
            case DevCommandKind.SetBotAccuracy or DevCommandKind.SetBotDamage or DevCommandKind.SetBotRange: SetBotTuning(peer, cmd); break;
            default: Answer(DevNote.BadValue); break;
        }
        _devDirty = true;
        if (DevToolsOn) SendDevStates();   // at once, not at the next tick: a paused server runs no ticks
    }

    void KillBots(Peer peer, byte which)
    {
        int killed = 0;
        foreach (var p in _peers.Values)
        {
            if (!p.Bot || p.Spectator || !World.Players.TryGetValue(p.PlayerId, out var s)) continue;
            if (which == 0 && s.Team != Team.SpaceMarines || which == 1 && s.Team != Team.Chaos) continue;
            if (World.DevKill(p.PlayerId)) killed++;
        }
        Log?.Invoke($"dev tools: kill bots ({(which == 0 ? "loyalists" : which == 1 ? "chaos" : "both sides")}): {killed} killed (player {peer.PlayerId})");
        Answer(DevNote.Applied);
    }

    void TeleportRequester(Peer peer, DevCommand cmd)
    {
        if (cmd.Argument > (byte)DevTeleportKind.Player) { Answer(DevNote.BadValue); return; }
        var kind = (DevTeleportKind)cmd.Argument;
        string target = kind switch { DevTeleportKind.SpawnRoom => $"spawn room ({(cmd.Extra == 1 ? "chaos" : "loyalists")})", DevTeleportKind.ControlPoint => $"control point {cmd.Extra}", _ => $"player {cmd.Extra}" };
        if (World.DevTeleport(peer.PlayerId, kind, cmd.Extra, out string why) && World.Players.TryGetValue(peer.PlayerId, out var now))
        {
            Log?.Invoke($"dev tools: teleport player {peer.PlayerId} to {target}: {now.Position.X:0.#},{now.Position.Y:0.#},{now.Position.Z:0.##}");
            Answer(DevNote.Applied);
        }
        else { Log?.Invoke($"dev tools: teleport player {peer.PlayerId} to {target} refused: {why}"); Answer(DevNote.TeleportBlocked); }
    }

    void SetTimeScale(Peer peer, int index)
    {
        if (index < 0 || index >= DevTools.TimeScales.Length) { Answer(DevNote.BadValue); return; }
        _devTimeScale = index;
        float scale = DevTools.TimeScales[index];
        if (_options.DevTools?.Clock is { } clock) clock.Scale = scale;
        Log?.Invoke($"dev tools: time scale {(scale == 0f ? "0 (paused)" : scale.ToString("0.##"))} (player {peer.PlayerId})");
        Answer(DevNote.Applied);
    }

    void SetBotTuning(Peer peer, DevCommand cmd)
    {
        int v = cmd.Extra;
        switch (cmd.Kind)
        {
            case DevCommandKind.SetBotAccuracy:
                if (v != Protocol.DevNoValue && v > 1000) { Answer(DevNote.BadValue); return; }
                _devAccuracy = v;
                Log?.Invoke(v == Protocol.DevNoValue ? $"dev tools: bot accuracy back to each bot's own (player {peer.PlayerId})" : $"dev tools: bot accuracy {v / 1000f:0.###} (player {peer.PlayerId})");
                break;
            case DevCommandKind.SetBotDamage:
                if (v > 1000) { Answer(DevNote.BadValue); return; }
                _devDamage = v; _devDamageSet = true;
                ApplyBotDamage();
                Log?.Invoke($"dev tools: bot damage x {v / 1000f:0.###} (player {peer.PlayerId})");
                break;
            default:
                if (v != 0 && (v < 5 || v > 1000)) { Answer(DevNote.BadValue); return; }
                _devRange = v;
                Log?.Invoke(v == 0 ? $"dev tools: bot range back to each weapon's own (player {peer.PlayerId})" : $"dev tools: bot range {v} m (player {peer.PlayerId})");
                break;
        }
        Answer(DevNote.Applied);
    }

    /// <summary>The damage every bot deals, as the tools set it (the sim only ever lowers it: 1000 is full damage and removes the server's <c>--bot-damage-scale</c> too).</summary>
    void ApplyBotDamage()
    {
        if (!_devDamageSet) return;
        foreach (var p in _peers.Values)
            if (p.Bot && !p.Spectator) World.SetDamageScale(p.PlayerId, _devDamage / 1000f);
    }

    static string FlagName(DevToolsFlags flag) => flag switch
    {
        DevToolsFlags.Invincible => "invincible", DevToolsFlags.FreezeBots => "freeze bots", DevToolsFlags.InfiniteAmmo => "infinite ammunition", DevToolsFlags.InfiniteJump => "infinite jump pack", DevToolsFlags.Indefinite => "indefinite match", DevToolsFlags.InstantRespawn => "instant respawn", _ => flag.ToString(),
    };

    void SetDevFlag(Peer peer, DevToolsFlags flag, bool on)
    {
        if (!DevTools.IsSingleFlag(flag)) return;
        bool was = (World.DevFlags & flag) != 0;
        World.DevFlags = on ? World.DevFlags | flag : World.DevFlags & ~flag;
        _lastDevFlags = World.DevFlags;
        if (was != on) Log?.Invoke($"dev tools: {FlagName(flag)} {(on ? "ON" : "OFF")} (player {peer.PlayerId})");
        Answer(DevNote.Applied);
    }

    void AddToolBot(Peer peer, Team team)
    {
        string sideName = team == Team.Chaos ? "chaos" : "loyalists";
        if (World.Config.Extras.HasFlag(ContentExtras.Musou)) { Log?.Invoke($"dev tools: add bot ({sideName}) refused: not in Musou Mode"); Answer(DevNote.NotInThisMode); return; }
        if (_options.DevTools?.Launcher is not { } launcher) { Log?.Invoke($"dev tools: add bot ({sideName}) refused: no Godot executable was found for the bots (--dev-bot-exe)"); Answer(DevNote.LauncherMissing); return; }
        BindToolBots();   // bind first: a bot that joined just before the join timeout is kept
        PruneToolBots();
        int starting = _toolBots.Count(b => b.Connection is null);
        int onSide = _peers.Values.Count(p => !p.Spectator && World.Players.TryGetValue(p.PlayerId, out var s) && s.Team == team) + _toolBots.Count(b => b.Connection is null && b.Team == team);
        int total = _peers.Values.Count(p => !p.Spectator) + starting;
        if (total >= PlayerCap) { Log?.Invoke($"dev tools: add bot ({sideName}) refused: {total} of {PlayerCap} players"); Answer(DevNote.ServerFull); return; }
        if (World.Rules is { } rules && onSide >= rules.MaxPerSide) { Log?.Invoke($"dev tools: add bot ({sideName}) refused: the side holds {onSide} of {rules.MaxPerSide}"); Answer(DevNote.SideFull); return; }
        if (starting >= MaxStartingToolBots) { Log?.Invoke($"dev tools: add bot ({sideName}) refused: {starting} bots are still starting"); Answer(DevNote.TooManyStarting); return; }
        string side = team == Team.Chaos ? HostBots.Chaos : HostBots.Marines;
        int index = _toolBotsStarted.GetValueOrDefault(team);
        var slot = new BotSlot(side, HostBots.ClassFor(side, index));
        uint token = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var process = launcher.Start(slot, token, out string error);
        if (process is null) { Log?.Invoke($"dev tools: add bot ({sideName}) failed: {error}"); Answer(DevNote.LaunchFailed); return; }
        _toolBotsStarted[team] = index + 1;
        _toolBots.Add(new ToolBot(process, team, token, _clock.Now));
        Log?.Invoke($"dev tools: add bot ({sideName}, class '{slot.Class}') started, pid {process.Id} (player {peer.PlayerId} asked)");
        Answer(DevNote.BotStarting);
    }

    void RemoveToolBot(Peer peer, Team team)
    {
        string sideName = team == Team.Chaos ? "chaos" : "loyalists";
        BindToolBots();   // bind first: a bot that joined just before the join timeout is kept
        PruneToolBots();
        var bot = _toolBots.LastOrDefault(b => b.Team == team);
        if (bot is null) { Log?.Invoke($"dev tools: remove bot ({sideName}): none of the tools' bots on that side"); Answer(DevNote.NoBotToRemove); return; }
        _toolBots.Remove(bot);
        int pid = bot.Process.Id;
        bot.Process.Stop();
        int removedPlayer = 0;
        if (bot.Connection is { } connection && _peers.TryGetValue(connection, out var botPeer))
        {
            removedPlayer = botPeer.PlayerId;
            RemovePeer(connection, botPeer, reason: "dev tools: bot removed");
        }
        Log?.Invoke($"dev tools: remove bot ({sideName}) stopped pid {pid}{(removedPlayer > 0 ? $", player {removedPlayer} removed" : " (it had not joined yet)")} (player {peer.PlayerId} asked)");
        Answer(DevNote.BotRemoved);
    }

    /// <summary>Forgets bots whose process ended and stops those whose player is gone (kicked or timed out): the tools keep no stray process.</summary>
    void PruneToolBots()
    {
        for (int i = _toolBots.Count - 1; i >= 0; i--)
        {
            var bot = _toolBots[i];
            bool ended = !bot.Process.Running;
            bool playerGone = bot.Connection is { } c && !_peers.ContainsKey(c);
            bool neverJoined = bot.Connection is null && _clock.Now - bot.StartedAt > ToolBotJoinTimeout;
            if (neverJoined) Log?.Invoke($"dev tools: bot pid {bot.Process.Id} did not join within {ToolBotJoinTimeout.TotalSeconds:0} s; stopped");
            if (!ended && !playerGone && !neverJoined) continue;
            if (!ended) bot.Process.Stop();
            _toolBots.RemoveAt(i);
            _devDirty = true;
        }
    }

    /// <summary>Ties each tool bot to the player whose Hello nonce is its token (so a bot from <c>bots.cmd</c> or one the balance moved to another side is never taken for it).</summary>
    void BindToolBots()
    {
        if (_toolBots.All(b => b.Connection is not null)) return;
        foreach (var (connection, peer) in _peers)
        {
            if (!peer.Bot || peer.Spectator || _toolBots.Any(b => b.Connection == connection)) continue;
            var owner = _toolBots.FirstOrDefault(b => b.Connection is null && b.Token == peer.Nonce);
            if (owner is not null) { owner.Connection = connection; _devDirty = true; }
        }
    }

    /// <summary>Stops every bot the tools started (the server's exit; safe to call twice).</summary>
    public void StopToolBots()
    {
        foreach (var bot in _toolBots.ToList()) bot.Process.Stop();
        _toolBots.Clear();
    }

    /// <summary>Test seam: the state a player on a loopback address is sent now.</summary>
    internal DevState DevStateNow() => new(World.DevFlags, _devCounts.ToolLoyalists, _devCounts.ToolChaos, _devCounts.Loyalists, _devCounts.Chaos, _devNote, _devNoteSerial,
        _devTimeScale, _devAccuracy, _devDamage >= 0 ? _devDamage : (int)Math.Round(Math.Clamp(_options.BotDamageScale, 0f, 1f) * 1000f), _devRange);

    /// <summary>Once a tick (one null check with the tools off): the bookkeeping of the tools' bots and the state to the panels, once a second and on every change.</summary>
    void DevToolsTick()
    {
        if (_options.DevTools is null || !DevToolsOn) return;
        if (World.Tick % 10 == 0)
        {
            BindToolBots();
            PruneToolBots();
            int loyalists = 0, chaos = 0;
            foreach (var p in _peers.Values)
                if (!p.Spectator && World.Players.TryGetValue(p.PlayerId, out var s)) { if (s.Team == Team.Chaos) chaos++; else if (s.Team == Team.SpaceMarines) loyalists++; }
            var counts = (loyalists, chaos, _toolBots.Count(b => b.Team == Team.SpaceMarines), _toolBots.Count(b => b.Team == Team.Chaos));
            if (counts != _devCounts) { _devCounts = counts; _devDirty = true; }
            ApplyBotDamage();   // a bot that joined after the tools set the damage gets it
        }
        CancelBeaconTick();
        var flags = World.DevFlags;
        if (flags != _lastDevFlags)
        {
            // The world itself cleared a flag: the indefinite match ends with the match (the next one is not indefinite).
            if ((_lastDevFlags & DevToolsFlags.Indefinite) != 0 && (flags & DevToolsFlags.Indefinite) == 0)
                Log?.Invoke("dev tools: indefinite match OFF (the next match started)");
            _lastDevFlags = flags; _devDirty = true;
        }
        if (!_devDirty && _clock.Now < _nextDevState) return;
        SendDevStates();
    }

    TimeSpan _nextDevKeep;

    /// <summary>
    /// Called every <see cref="Update"/>, outside the tick loop (a paused server runs no ticks): while the time scale is not 1 the state goes out once a real second, so clients can tell a paused
    /// or slowed server from a dead one (their copy of the state expires after 3.5 real seconds and they return to normal time).
    /// </summary>
    void DevKeepAlive()
    {
        if (_devTimeScale == 0 || !DevToolsOn) return;
        var real = _options.DevTools?.Clock?.Real.Now ?? _clock.Now;
        if (real < _nextDevKeep) return;
        _nextDevKeep = real + TimeSpan.FromSeconds(1);
        SendDevStates();
    }

    /// <summary>Sends the state to every player and bot on a loopback address (a bot takes the bot values and the time scale from it).</summary>
    void SendDevStates()
    {
        _devDirty = false;
        _nextDevState = _clock.Now + TimeSpan.FromSeconds(1);
        _lastDevFlags = World.DevFlags;
        var packet = Protocol.Write(DevStateNow());
        foreach (var (connection, peer) in _peers)
            if (!peer.Spectator && DevToolsGate.FromLoopback(_transport.EndPointOf(connection))) _transport.Send(connection, packet);
    }
}
