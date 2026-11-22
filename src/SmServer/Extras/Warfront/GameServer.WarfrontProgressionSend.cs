using SmNet;
using SmSim;
using SmNet.Extras.Warfront;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;

namespace SmServer;

/// <summary>What the sender needs to know of one player at one moment (the server fills it from the campaign, the picker and the run phase; tests fill it by hand).</summary>
public readonly record struct WarfrontProgressionSnapshot(
    int Level, long Xp, long XpAtLevel, long XpAtNextLevel, int PendingPicks, IReadOnlyList<int> Cards, int Retainers, float AttackSpeedBonus,
    bool OfferOpen, int OfferPickIndex, IReadOnlyList<WarfrontCard> OfferCards, double OfferSecondsLeft, bool Aboard);

/// <summary>
/// The per-player send state of Warfront's progression sections (slice W8s; tags 44 and 45, <see cref="WarfrontProgressionWire"/>). Pure: no sockets, no World. <see cref="Step"/> is called once per send for each
/// identified player and returns the sections to send now: Progress when its bytes changed or <see cref="ProgressRepeatSeconds"/> have passed; Offer on every call while an offer stands (it repeats, the datagram is unreliable);
/// the close record <see cref="CloseRepeats"/> times in consecutive calls once it ends (review of W8, finding 1). One instance per server, state per player id.
/// </summary>
public sealed class WarfrontProgressionSender
{
    /// <summary>The Progress repeat while nothing changes (the W8 brief: every 2 s) [ours].</summary>
    public const double ProgressRepeatSeconds = 2.0;
    /// <summary>The close record is sent in this many consecutive sends (review of W8) [ours].</summary>
    public const int CloseRepeats = 3;

    sealed class State
    {
        public byte[]? LastProgress;
        public uint LastProgressTick;
        public bool WasOpen;
        public int LastPickIndex;
        public int CloseLeft;
    }

    readonly Dictionary<int, State> _players = new();

    /// <summary>The player left (or became unidentified): its state goes.</summary>
    public void Forget(int id) => _players.Remove(id);

    /// <summary>Drops the state of every id not in <paramref name="live"/>.</summary>
    public void Keep(IReadOnlyCollection<int> live)
    {
        foreach (int id in _players.Keys.Where(k => !live.Contains(k)).ToList()) _players.Remove(id);
    }

    public static byte[] ProgressBytes(in WarfrontProgressionSnapshot s)
    {
        var counts = new byte[WarfrontProgressionWire.StatCardTypes];
        foreach (int c in s.Cards)
            if (c >= (int)WarfrontCard.AttackSpeed && c <= (int)WarfrontCard.MaxArmor && counts[c - 1] < byte.MaxValue) counts[c - 1]++;
        long atLevel = Math.Min(s.XpAtLevel, s.Xp);
        return WarfrontProgressionWire.Encode(new WarfrontProgressWire(
            s.Level, (uint)Math.Clamp(s.Xp, 0, uint.MaxValue), (uint)Math.Clamp(atLevel, 0, uint.MaxValue), (uint)Math.Clamp(Math.Max(s.XpAtNextLevel, atLevel), 0, uint.MaxValue),
            s.PendingPicks, counts, s.Retainers, (int)MathF.Round(s.AttackSpeedBonus * 1000f)));
    }

    public static byte[] OfferBytes(in WarfrontProgressionSnapshot s)
    {
        var cards = new WarfrontCard[WarfrontProgressionWire.OfferSize];
        for (int i = 0; i < cards.Length && i < s.OfferCards.Count; i++) cards[i] = s.OfferCards[i];
        return WarfrontProgressionWire.Encode(new WarfrontOfferWire(true, s.OfferPickIndex & 0xFF, cards, (int)Math.Ceiling(Math.Max(0, s.OfferSecondsLeft) * 10), s.Aboard));
    }

    /// <summary>The sections to send to player <paramref name="id"/> now (possibly none). <paramref name="tick"/> and <paramref name="rate"/> are the World's.</summary>
    public List<(byte Tag, byte[] Payload)> Step(int id, in WarfrontProgressionSnapshot snap, uint tick, int rate)
    {
        var sections = new List<(byte, byte[])>(2);
        if (!_players.TryGetValue(id, out var st)) _players[id] = st = new State();
        byte[] progress = ProgressBytes(snap);
        if (st.LastProgress is null || !st.LastProgress.AsSpan().SequenceEqual(progress) || tick - st.LastProgressTick >= (uint)Math.Max(1, (int)(ProgressRepeatSeconds * rate)))
        {
            sections.Add((WarfrontProgressionWire.ProgressTag, progress));
            st.LastProgress = progress;
            st.LastProgressTick = tick;
        }
        if (snap.OfferOpen)
        {
            sections.Add((WarfrontProgressionWire.OfferTag, OfferBytes(snap)));
            st.WasOpen = true;
            st.LastPickIndex = snap.OfferPickIndex & 0xFF;
            st.CloseLeft = 0;
        }
        else
        {
            if (st.WasOpen) { st.WasOpen = false; st.CloseLeft = CloseRepeats; }
            if (st.CloseLeft > 0)
            {
                sections.Add((WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.EncodeClosed(st.LastPickIndex)));
                st.CloseLeft--;
            }
        }
        return sections;
    }
}

/// <summary>Warfront's progression sections on the server (slice W8s): fills <see cref="WarfrontProgressionSnapshot"/> per identified human from the campaign, the picker and the run phase and sends what <see cref="WarfrontProgressionSender"/> returns.</summary>
public sealed partial class GameServer
{
    /// <summary>A Pick command's decision (W5a's handler calls it): the wire carries the pick index modulo 256 and answers the standing offer only; a refusal (no offer, stale index, slot out of range) changes nothing.</summary>
    internal static bool WarfrontPickFromWire(World world, int player, int slot, int wirePick) =>
        world.WarfrontOffer(player) is { } offer && (offer.PickIndex & 0xFF) == wirePick && world.WarfrontPick(player, slot, offer.PickIndex);

    readonly WarfrontProgressionSender _wfProgression = new();
    readonly HashSet<ulong> _wfXpAidGiven = new();
    readonly Dictionary<int, (int Index, double Left, int Taken)> _wfOfferLog = new();

    /// <summary>Test aid <c>SM_WF_XP=&lt;n&gt;</c>: every identified player gets n XP once per process (a scripted run levels up without fighting). Logged when used.</summary>
    void WarfrontXpAid(WarfrontServerState s, ulong key)
    {
        if (Environment.GetEnvironmentVariable("SM_WF_XP") is not { Length: > 0 }) return;
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "--bind");
        if (at < 0 || at + 1 >= args.Length || !System.Net.IPAddress.TryParse(args[at + 1], out var bound) || !System.Net.IPAddress.IsLoopback(bound))
        {
            if (_wfXpAidGiven.Add(0)) WfLog("test aid SM_WF_XP refused: the server is not bound to 127.0.0.1 (--bind 127.0.0.1)");
            return;
        }
        if (Environment.GetEnvironmentVariable("SM_WF_XP") is not { Length: > 0 } v || !long.TryParse(v, out long xp) || xp <= 0 || !_wfXpAidGiven.Add(key)) return;
        s.Campaign.AddXp(key, xp);
        WfLog($"test aid SM_WF_XP: +{xp} XP for P{s.KeyOf.First(k => k.Value == key).Key}, now level {s.Campaign.Player(key)?.Level}");
    }

    partial void WarfrontSendProgression()
    {
        if (_wf is not { } s) return;
        int rate = WarfrontRate;
        uint tick = World.Tick;
        bool aboard = World.Tfw is { } run && run.Phase is TfwRunPhase.Hub or TfwRunPhase.Travel;
        double now = tick / (double)Math.Max(1, World.Config.TickRate);   // the picker's own clock (World.WarfrontStep: Tick / Config.TickRate)
        var ids = s.KeyOf.ToList();
        var live = ids.Select(p => p.Key).ToList();
        _wfProgression.Keep(live);
        foreach (int gone in _wfOfferLog.Keys.Where(k => !live.Contains(k)).ToList()) _wfOfferLog.Remove(gone);
        foreach (var (id, key) in ids)
        {
            WarfrontXpAid(s, key);
            if (s.Campaign.Player(key) is not { } p) continue;
            var bonuses = WarfrontStatBonuses.For(p.Cards);
            var offer = World.WarfrontOffer(id);
            var snap = new WarfrontProgressionSnapshot(
                p.Level, p.Xp, WarfrontCampaign.XpForLevel(p.Level), WarfrontCampaign.XpForLevel(p.Level + 1), p.PendingPicks, p.Cards, bonuses.Retainers, bonuses.AttackSpeed - 1f,
                offer is not null, offer?.PickIndex ?? 0, offer?.Cards ?? [], offer is null ? 0 : WarfrontProgressionRules.PickTimeoutSeconds - (now - offer.OpenedAt), aboard);
            WarfrontLogOffer(id, p, snap);
            var sections = _wfProgression.Step(id, snap, tick, rate);
            if (sections.Count > 0) SendExtras(new ExtrasUpdate(tick, sections.ToArray()), id);
            WarfrontSendProgressSave(s, id, key, tick, rate);
        }
    }

    /// <summary>One log line when an offer opens and one when it ends (by a pick or by the 20 s timeout), for the scripted runs.</summary>
    void WarfrontLogOffer(int id, WarfrontPlayerState p, in WarfrontProgressionSnapshot snap)
    {
        bool had = _wfOfferLog.TryGetValue(id, out var last);
        if (had && (!snap.OfferOpen || last.Index != snap.OfferPickIndex))
        {
            WfLog($"P{id} offer {last.Index} closed: {(p.PicksTaken > last.Taken ? $"pick taken ({(last.Left <= 0.5 ? "by the timeout" : "by the player")}; card {(p.Cards.Count > 0 ? (WarfrontCard)p.Cards[^1] : WarfrontCard.None)})" : "no pick taken")}; protection ended; {p.PendingPicks} pending");
            had = false;
        }
        if (snap.OfferOpen && !had) WfLog($"P{id} offer {snap.OfferPickIndex} opened ({(snap.Aboard ? "aboard" : "at a lull")}): {string.Join(", ", snap.OfferCards)}; {p.PendingPicks} pending; protected until a pick or the timeout");
        if (snap.OfferOpen) _wfOfferLog[id] = (snap.OfferPickIndex, snap.OfferSecondsLeft, p.PicksTaken);
        else _wfOfferLog.Remove(id);
    }
}

/// <summary>
/// Persistent progression on the host (codebase fix 1, owner 2026-11-22): the Progress command (a player's own file, chunked), the merge into the run, the ProgressSave section (envelope tag 49) that sends the run's copy back, and the roster glue
/// (the extra retainers with their names). A player's progression lives on his own PC; the run's copy (run.json cards and XP, squads.json rosters) is the host's working copy and its mirror.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>Seconds between ProgressSave sends while only XP moved (a pick, a level change and a retainer gain send at once) [ours].</summary>
    const int WarfrontProgressSaveSeconds = 10;

    /// <summary>One chunk of a client's Progress record (untrusted). When the record is whole it is merged now (Ship phase) or queued to the next ship stop. A malformed or too early chunk is dropped; the client repeats until it hears ProgressSave.</summary>
    void WarfrontProgressCommand(int player, byte[] chunk)
    {
        if (_wf is not { } s) return;
        if (!s.KeyOf.TryGetValue(player, out ulong key)) { WarfrontCommandsDropped++; return; }   // before the Identify: the client repeats
        if (!s.ProgressChunks.TryGetValue(player, out var reassembler)) s.ProgressChunks[player] = reassembler = new SmNet.Extras.Warfront.WarfrontProgressSaveWire.Reassembler();
        var record = reassembler.Add(chunk);
        if (record is null) return;
        WarfrontApplyQueuedProgress(s);   // an older queued record of this player must not outlive a newer one
        s.ProgressSeen.Add(player);
        s.ProgressAckDue.Add(player);
        bool ship = WarfrontShipPhase(s);
        if (!ship)
        {
            if (!s.ProgressQueueBaseXp.ContainsKey(key) && WarfrontProgressMirror(s, key) is { } baseline) s.ProgressQueueBaseXp[key] = baseline.Xp;
            s.ProgressQueue[key] = record;
            WfLog($"P{player} progress record queued to the next ship stop ({s.Campaign.Phase})");
            return;
        }
        WarfrontMergeProgress(s, player, key, record);
    }

    static bool WarfrontShipPhase(WarfrontServerState s) => s.Campaign.Phase is WarfrontPhase.Ship or WarfrontPhase.Lobby;

    /// <summary>At the ship, every queued record is merged (called each tick, before a Deploy and before a class confirmation, so the gates always see the progression).</summary>
    void WarfrontApplyQueuedProgress(WarfrontServerState s)
    {
        if (s.ProgressQueue.Count == 0 || !WarfrontShipPhase(s)) return;
        foreach (var (key, record) in s.ProgressQueue.ToList())
        {
            if (s.Campaign.Player(key) is null) continue;
            s.ProgressQueue.Remove(key);
            int player = WarfrontPlayerOf(key) ?? -1;
            WarfrontMergeProgress(s, player, key, record);
        }
    }

    /// <summary>The merge (Ship phase): the Reset flag zeroes the progression and keeps the stats; absent keeps the run's copy; otherwise the better of the run's copy and the player's, whole (cards and retainers follow the winner).</summary>
    void WarfrontMergeProgress(WarfrontServerState s, int player, ulong key, SmNet.Extras.Warfront.WarfrontProgressRecord record)
    {
        var c = s.Campaign;
        bool wasQueued = s.ProgressQueueBaseXp.Remove(key, out long queuedAtXp);   // false when the record was merged at once
        if (record.Reset)
        {
            c.ApplyProgress(key, new SmSim.Extras.Warfront.Progression.WarfrontProgressFile());
            WarfrontRosterSync(s, key, []);
            s.PendingExtras.Remove(key);
            WfLog($"P{player} (key {KeyTail(key)}) reset the progression: level 1, no cards, one retainer (the stats stay)");
            WfSave(s);
            return;
        }
        var run = WarfrontProgressMirror(s, key);
        if (run is null) return;
        if (record.Absent || record.State is null)
        {
            WfLog($"P{player} (key {KeyTail(key)}) has no progress file: the run's copy (level {run.Level}) is kept and goes back to it");
            return;
        }
        var notes = new List<string>();
        var theirs = SmSim.Extras.Warfront.Progression.WarfrontProgressFile.Sanitize(record.State, notes);
        if (notes.Count > 0) WfLog($"P{player} progress record repaired: {string.Join("; ", notes)}");
        var winner = SmSim.Extras.Warfront.Progression.WarfrontProgressFile.Better(run, theirs);
        if (ReferenceEquals(winner, theirs))
        {
            // the record waited for the ship (the player joined mid-battle): the XP the run's copy earned meanwhile is not lost; level-ups from it give their normal picks
            long gained = wasQueued ? Math.Max(0, run.Xp - queuedAtXp) : 0;
            if (gained > 0) { theirs.Xp += gained; theirs = SmSim.Extras.Warfront.Progression.WarfrontProgressFile.Sanitize(theirs); WfLog($"P{player} (key {KeyTail(key)}) earned {gained} XP in the battle on the run's copy: added to the player's file"); }
            c.ApplyProgress(key, theirs);
            WarfrontRosterSync(s, key, theirs.ExtraRetainers);
            WfLog($"P{player} (key {KeyTail(key)}) progress: the player's file (level {theirs.Level}, {theirs.PicksTaken} picks, {theirs.ExtraRetainers.Count} extra retainer(s)) is better than the run's (level {run.Level}) and is applied");
            WfSave(s);
        }
        else WfLog($"P{player} (key {KeyTail(key)}) progress: the run's copy (level {run.Level}) is kept (the file's is level {theirs.Level})");
    }

    /// <summary>The run's copy of a key's progression: the campaign's XP and cards, the extra retainers from the roster (names included) once it exists. Sanitized. Null for an unknown key.</summary>
    SmSim.Extras.Warfront.Progression.WarfrontProgressFile? WarfrontProgressMirror(WarfrontServerState s, ulong key)
    {
        var file = s.Campaign.ProgressOf(key);
        if (file is null) return null;
        if (WarfrontRosterExtras(key) is { } extras) file.ExtraRetainers = extras;
        else if (s.PendingExtras.TryGetValue(key, out var pending)) file.ExtraRetainers = pending;
        return SmSim.Extras.Warfront.Progression.WarfrontProgressFile.Sanitize(file);
    }

    /// <summary>
    /// The copy tag 49 carries to a client: the run's copy, except for a client whose reset is queued (it reset on the menu and joined a battle): that one gets the level-1 copy the reset will make, so the acknowledgement never sends
    /// the old level back to the file the player just reset.
    /// </summary>
    SmSim.Extras.Warfront.Progression.WarfrontProgressFile? WarfrontProgressCopyFor(WarfrontServerState s, ulong key)
    {
        if (s.ProgressQueue.TryGetValue(key, out var queued) && queued.Reset && s.Campaign.Player(key) is not null)
            return SmSim.Extras.Warfront.Progression.WarfrontProgressFile.Sanitize(new SmSim.Extras.Warfront.Progression.WarfrontProgressFile());
        return WarfrontProgressMirror(s, key);
    }

    /// <summary>The ProgressSave sections: to each player whose client sent a record, on its first send (the acknowledgement), at once on a pick, a level change or a retainer gain, and every <see cref="WarfrontProgressSaveSeconds"/> s while the XP moved.</summary>
    void WarfrontSendProgressSave(WarfrontServerState s, int id, ulong key, uint tick, int rate)
    {
        if (!s.ProgressSeen.Contains(id)) return;
        bool ack = s.ProgressAckDue.Contains(id);
        if (!ack && tick % (uint)Math.Max(1, rate / 4) != 0) return;
        var file = WarfrontProgressCopyFor(s, key);
        if (file is null) return;
        var bytes = SmNet.Extras.Warfront.WarfrontProgressSaveWire.Encode(file);
        bool have = s.SaveSent.TryGetValue(id, out var last);
        bool changed = !have || !last.Bytes.AsSpan().SequenceEqual(bytes);
        bool structural = !have || last.Level != file.Level || last.Picks != file.PicksTaken || last.Extras != file.ExtraRetainers.Count;
        bool xpDue = changed && have && tick - last.Tick >= (uint)(WarfrontProgressSaveSeconds * rate);
        if (!ack && !(changed && structural) && !xpDue) return;
        s.ProgressAckDue.Remove(id);
        s.SaveSent[id] = (bytes, file.Level, file.PicksTaken, file.ExtraRetainers.Count, tick);
        SendExtras(new ExtrasUpdate(tick, [(SmNet.Extras.Warfront.WarfrontProgressSaveWire.EnvelopeTag, bytes)]), id);
    }

    /// <summary>
    /// The final send (the end of the run, a stage change): every client that sent a record gets the run's copy once more now, whatever the 10 s rule says, so its file holds the last XP before the process leaves.
    /// (A player who has already left cannot be reached; the 10 s sends keep that window short.) Returns how many were sent.
    /// </summary>
    int WarfrontSendProgressSaveFinal(WarfrontServerState s)
    {
        int sent = 0;
        foreach (var (id, key) in s.KeyOf.ToList())
        {
            if (!s.ProgressSeen.Contains(id)) continue;
            var file = WarfrontProgressCopyFor(s, key);
            if (file is null) continue;
            var bytes = SmNet.Extras.Warfront.WarfrontProgressSaveWire.Encode(file);
            s.ProgressAckDue.Remove(id);
            s.SaveSent[id] = (bytes, file.Level, file.PicksTaken, file.ExtraRetainers.Count, World.Tick);
            SendExtras(new ExtrasUpdate(World.Tick, [(SmNet.Extras.Warfront.WarfrontProgressSaveWire.EnvelopeTag, bytes)]), id);
            sent++;
        }
        return sent;
    }

    internal int WarfrontSendProgressSaveFinalForTests() => _wf is { } s ? WarfrontSendProgressSaveFinal(s) : 0;

    /// <summary>Once a tick: queued records at the ship, extras waiting for a roster.</summary>
    void WarfrontProgressStep(WarfrontServerState s)
    {
        WarfrontApplyQueuedProgress(s);
        if (s.PendingExtras.Count == 0 || _wfWiring is not { } w) return;
        foreach (var (key, extras) in s.PendingExtras.ToList())
            if (w.Rosters.Has(key)) { WarfrontRosterSync(s, key, extras); s.PendingExtras.Remove(key); }
    }
}
