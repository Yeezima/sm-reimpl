using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Chaos.Finale;
using SmSim.Extras.Warfront.Results;

namespace SmSim;

/// <summary>
/// The Chaos finale in the World (Chaos Warfront C5; brief 2026-11-24 rev 1 and design "Finale"). Runs from C4's <c>WarfrontChaosDuelStep</c> (the end of each Chaos battle's Warfront step), only in a
/// Chaos fortress; an Orks stage, Musou Mode and Classic never reach it.
/// <list type="bullet">
/// <item><b>Bodyguard:</b> the Lord is shielded and kept at home; the Chosen fight the full squad, retainers included; a wipe is the normal fortress-wipe rule.</item>
/// <item><b>Snapshot:</b> the tick the last Chosen is gone, the living (alive, not downed), connected, deployed humans are ranked (<see cref="WarfrontDuelRanking"/>); nobody living: no snapshot, the wipe rule decides.</item>
/// <item><b>Isolation</b>, at the start and every tick of the relay: every other human aboard and held (no input, no damage); retainers benched; Guardsmen withdrawn; the sources and trickle stopped;
/// the breaches closed silently; every other enemy removed without credit; every projectile, lingering area and pending blast not the duelist's removed, and every burn not his; barrages and drop pods
/// called off (support is outside help); drops and buffs cleared; every open card offer withdrawn (its picks wait); executions aborted (the X3 seam); only the duelist's hits, staggers and throws land on the Lord.</item>
/// <item><b>Entry:</b> a fresh life at the plaza entry facing the Lord (full health, armour, ammunition and charges; the current class and loadout), the Lord freed and unshielded.</item>
/// <item><b>Handoff:</b> a defeat or a disconnect; the Lord's health never written; his burns, stun, script and cooldowns cleared; put back on his spot (no heal) and held there; a 3 s pause; the next present challenger.</item>
/// <item><b>End:</b> his death by the duelist is the fortress's win (the battle sees it); the queue exhausted is <see cref="WarfrontFinaleLost"/>, the run's duel hook leaves the battle as lost.</item>
/// </list>
/// </summary>
public sealed partial class World
{
    WarfrontDuel? _duel;
    TfwBattle? _duelBattle;
    int _duelist;
    uint _handoffEnds;
    Vector3 _duelEntry;
    float _duelEntryYaw;
    bool _duelEmptyLogged, _heldWrapped, _squadWrapped;
    MusouEnemy[]? _duelBuffer;

    /// <summary>The finale of the Chaos fortress under way (null: none yet, or not a Chaos fortress). Kept after the battle ends until the next one, so the server reads its end.</summary>
    public WarfrontDuel? WarfrontChaosDuel => _duel;
    /// <summary>The current challenger's player id (0: none).</summary>
    public int WarfrontDuelist => _duelist;
    /// <summary>The tick the handoff's pause ends (tests).</summary>
    public uint WarfrontDuelHandoffEnds => _handoffEnds;
    /// <summary>Whether the relay ran out of challengers with the Lord alive (the server maps the battle's loss to <c>WarfrontOutcome.FinaleLost</c>).</summary>
    public bool WarfrontFinaleLost => _duel?.Phase == WarfrontDuelPhase.Lost;
    /// <summary>Whether <paramref name="battle"/> is the one the finale state belongs to.</summary>
    public bool WarfrontDuelOwns(TfwBattle? battle) => battle is not null && ReferenceEquals(battle, _duelBattle);

    /// <summary>The server's: benches every retainer for the rest of the battle (the squad sim's end of battle; default none).</summary>
    public Action? WarfrontDuelBenchRetainers { get; set; }
    /// <summary>The server's: a key's retainers' service records, for the snapshot's squad score (default none).</summary>
    public Func<ulong, IReadOnlyList<WarfrontRetainerStats>>? WarfrontDuelSquadOf { get; set; }

    /// <summary>Whether the relay holds the battle under way now.</summary>
    bool DuelHolding => _duel is { Active: true } && _tfw?.Battle is { } b && ReferenceEquals(b, _duelBattle);

    /// <summary>The Executions X3 seam (abort every execution under way, and at an entry the overshield goes): a no-op until executions land.</summary>
    partial void WarfrontDuelExecutions(int duelist);

    /// <summary>
    /// The picker held while the relay is on (its protection would make the duelist unharmed): any offer open now is withdrawn, not taken (<see cref="SmSim.Extras.Warfront.Progression.WarfrontPicker.Leave"/>;
    /// an offer is not saved and reopens with the same cards for the same pick index), so the pending picks wait for after the duel. This runs at the end of the Warfront step (inside C4's
    /// <c>WarfrontChaosStep</c>), after the offers' loop of the same step and before the fight, so an offer opened this tick never protects anyone and never reaches the wire.
    /// </summary>
    void DuelHoldPicker(WarfrontBattleRules rules)
    {
        var rt = rules.Progression;
        foreach (int id in rt.Ids)
            if (rt.TryKey(id, out ulong key) && rt.Picker.Protected(key)) rt.Picker.Leave(key);
    }

    void DuelLog(string line) => _chaosLog?.Invoke("chaos finale: " + line);

    partial void WarfrontChaosDuelStep()
    {
        if (_tfw is not { Warfront: { ChaosRoster: { } roster } rules, Battle: { } battle } run || !battle.ChaosFortress) return;
        var musou = _musou!;
        if (!ReferenceEquals(battle, _duelBattle))
        {
            _duel = new WarfrontDuel();
            _duelBattle = battle;
            _duelist = 0;
            _duelEmptyLogged = false;
            musou.WarfrontLordReset();
            DuelLog($"rules {WarfrontDuelRules.Version}: the bodyguard first, then the duel relay");
        }
        var duel = _duel!;
        ushort lord = battle.BossId;
        switch (duel.Phase)
        {
            case WarfrontDuelPhase.Bodyguard:
            {
                if (lord == 0) return;
                roster.Shield(lord, true);
                musou.WarfrontHoldHome(lord);
                if (!battle.ChaosBodyguardDown) return;
                var queue = WarfrontDuelRanking.Queue(rules.Progression.Campaign, key => DuelLiving(rules, run, key), WarfrontDuelSquadOf);
                if (!duel.Snapshot(queue, lord))
                {
                    if (!_duelEmptyLogged) DuelLog("the last Chosen fell with nobody living: no duel yet, the fortress-wipe rule decides");
                    _duelEmptyLogged = true;
                    return;
                }
                DuelLog($"snapshot: the last Chosen fell; queue {string.Join(", ", queue.Select(k => DuelName(rules, k)))}; the Lord at {LordHealthText(lord)}");
                DuelIsolate(rules, run, battle, start: true);
                DuelBegin(rules, run, battle, roster);
                return;
            }
            case WarfrontDuelPhase.Duel:
            {
                if (lord == 0 || !musou.TryGet(lord, out var l) || !l.Alive)
                {
                    duel.Win();
                    musou.LordDuelist = 0;
                    DuelLog($"WON: {DuelName(rules, duel.Current)} killed the Chaos Lord");
                    return;
                }
                DuelIsolate(rules, run, battle, start: false);
                if (!_players.TryGetValue(_duelist, out var p)) { DuelEnd(rules, run, battle, roster, WarfrontDuelEnd.Disconnected); return; }
                if (!p.Alive) { DuelEnd(rules, run, battle, roster, WarfrontDuelEnd.Defeated); return; }
                DuelBounds(battle, lord, p);
                foreach (var b in musou.LordBolts)
                    if (b.Player == _duelist && _players.TryGetValue(b.Player, out var target) && target.Alive) MusouHit(new MusouAttack(b.Lord, b.Player, b.Damage, Melee: false, Staggers: false, b.From));
                musou.LordBolts.Clear();
                return;
            }
            case WarfrontDuelPhase.Handoff:
                DuelIsolate(rules, run, battle, start: false);
                if (Tick >= _handoffEnds) DuelBegin(rules, run, battle, roster);
                return;
            case WarfrontDuelPhase.Snapshot:
                DuelBegin(rules, run, battle, roster);
                return;
        }
    }

    /// <summary>The snapshot's "living": a human of the battle, alive (not downed), connected, not held by the co-op rules.</summary>
    bool DuelLiving(WarfrontBattleRules rules, TfwRun run, ulong key) =>
        rules.Progression.TryId(key, out int id) && !TfwContracts.IsPuppet(id) && _players.TryGetValue(id, out var p) && p.Alive && run.InField.Contains(id)
        && rules.Progression.Campaign.IsConnected(key) && rules.CoopHeld?.Invoke(id) != true;

    /// <summary>A challenger's turn may begin: connected now (a dead one is fine: he gets a fresh life).</summary>
    bool DuelPresent(WarfrontBattleRules rules, ulong key) =>
        rules.Progression.TryId(key, out int id) && _players.ContainsKey(id) && rules.Progression.Campaign.IsConnected(key);

    string DuelName(WarfrontBattleRules rules, ulong? key)
    {
        if (key is not { } k) return "nobody";
        string name = rules.Progression.Campaign.Player(k)?.Name ?? "";
        string id = rules.Progression.TryId(k, out int pid) ? $"P{pid}" : "away";
        return string.IsNullOrWhiteSpace(name) ? id : $"{WarfrontResults.Clean(name)} ({id})";
    }

    string LordHealthText(ushort lord)
    {
        if (!_musou!.TryGet(lord, out var l)) return "?";
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        float max = _tfw?.Battle?.BossMaxHealth ?? 0f;
        return $"{l.Health.ToString("0.###", ci)} hp ({(max > 0f ? (l.Health / max * 100f).ToString("0.#", ci) : "?")} %, bits 0x{BitConverter.SingleToInt32Bits(l.Health):X8})";
    }

    /// <summary>The next challenger enters, or the relay is lost.</summary>
    void DuelBegin(WarfrontBattleRules rules, TfwRun run, TfwBattle battle, SmSim.Extras.Warfront.Chaos.WarfrontChaosRoster roster)
    {
        var duel = _duel!;
        var musou = _musou!;
        var skipped = duel.Record.Count;
        ulong? key = duel.Begin(k => DuelPresent(rules, k));
        for (int k = skipped; k < duel.Record.Count; k++) DuelLog($"{DuelName(rules, duel.Record[k].Key)} is not here at his turn: skipped");
        ushort lord = duel.LordId;
        if (key is not { } next || !rules.Progression.TryId(next, out int id))
        {
            _duelist = 0;
            musou.LordDuelist = 0;
            DuelLog($"LOST: no challenger left; the Chaos Lord stands at {LordHealthText(lord)}");
            return;
        }
        _duelist = id;
        DuelIsolate(rules, run, battle, start: false);
        if (battle.ChaosDuelEntry is { } entry) { _duelEntry = entry.At; _duelEntryYaw = entry.Yaw; }
        TfwRespawnAt(id, _duelEntry, _duelEntryYaw, faceCentre: false);
        if (_players.TryGetValue(id, out var p) && p.FireBoostLeft > 0f) _players[id] = p with { FireBoostLeft = 0f };
        WarfrontDuelExecutions(id);
        roster.Shield(lord, false);
        roster.AcceptOnly(lord, id);
        musou.WarfrontFree(lord);
        musou.LordDuelId = lord;
        musou.LordDuelist = id;
        musou.LordBolts.Clear();
        DuelLog($"entry: {DuelName(rules, next)} (attempt {duel.AttemptIndex + 1}, {duel.Remaining} after him) against the Lord at {LordHealthText(lord)}");
    }

    /// <summary>The current attempt is over: the handoff (or the loss).</summary>
    void DuelEnd(WarfrontBattleRules rules, TfwRun run, TfwBattle battle, SmSim.Extras.Warfront.Chaos.WarfrontChaosRoster roster, WarfrontDuelEnd end)
    {
        var duel = _duel!;
        var musou = _musou!;
        ulong? was = duel.Current;
        ushort lord = duel.LordId;
        duel.Advance(end);
        _duelist = 0;
        musou.LordDuelist = 0;
        musou.LordBolts.Clear();
        roster.Shield(lord, true);
        roster.AcceptOnly(lord, 0);
        _tfwBurns.Remove(lord);
        var spot = battle.FortressPlan?.BossSpot ?? battle.Layout.BossSpot;
        musou.Relocate(lord, spot);
        musou.WarfrontLordSlate(lord);
        if (musou.TryGet(lord, out var l)) musou.WarfrontLeashHome(lord, l.Position, TfwBattle.ChaosLordGroup);
        if (_musouBoss is { } boss && boss.Id == lord) boss.WarfrontLordSlate();
        DuelIsolate(rules, run, battle, start: false);
        _handoffEnds = Tick + (uint)Math.Max(1, (int)MathF.Round(WarfrontDuelRules.HandoffSeconds * Math.Max(1, Config.TickRate)));
        DuelLog($"{DuelName(rules, was)} {(end == WarfrontDuelEnd.Disconnected ? "disconnected" : "was defeated")}; the Lord at {LordHealthText(lord)}; "
            + (duel.Phase == WarfrontDuelPhase.Lost ? "LOST: no challenger left" : $"handoff, the next in {WarfrontDuelRules.HandoffSeconds:0} s"));
    }

    /// <summary>The duelist past the plaza is put back on the entry (no heal, never a loss); the Lord past it, back on his spot.</summary>
    void DuelBounds(TfwBattle battle, ushort lord, PlayerState p)
    {
        if (battle.FortressPlan is not { } plan) return;
        static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();
        if (Flat(p.Position, plan.BossSpot) > plan.ArenaRadius + WarfrontDuelRules.PlazaPullBack)
        {
            _players[_duelist] = p with { Position = _duelEntry, Velocity = Vector3.Zero };
            DuelLog($"P{_duelist} left the plaza: put back on the entry");
        }
        if (_musou!.TryGet(lord, out var l) && Flat(l.Position, plan.BossSpot) > plan.ArenaRadius + WarfrontDuelRules.LordPullBack) _musou.Relocate(lord, plan.BossSpot);
    }

    /// <summary>The relay's isolation (see the class summary); <paramref name="start"/> also clears the drops and buffs once and logs.</summary>
    void DuelIsolate(WarfrontBattleRules rules, TfwRun run, TfwBattle battle, bool start)
    {
        var musou = _musou!;
        battle.ChaosDuelHold = true;
        battle.Trickle = false;
        if (!_heldWrapped)
        {
            _heldWrapped = true;
            var before = rules.CoopHeld;
            rules.CoopHeld = id => (before?.Invoke(id) ?? false) || (DuelHolding && id != _duelist && !TfwContracts.IsPuppet(id) && id > 0);
        }
        if (!_squadWrapped)
        {
            _squadWrapped = true;
            var step = rules.SquadStep;
            var targets = rules.SquadTargets;
            rules.SquadStep = () => { if (!DuelHolding) step?.Invoke(); };
            rules.SquadTargets = into => DuelHolding ? 0 : targets?.Invoke(into) ?? 0;
        }
        if (rules.AnyRetainerStanding()) { WarfrontDuelBenchRetainers?.Invoke(); if (start) DuelLog("retainers benched"); }
        if (TfwGuards is { Count: > 0 } guards) { guards.Clear(); if (start) DuelLog("Guardsmen withdrawn"); }
        if (WarfrontChaosBreaches is { AnyStanding: true } breaches) breaches.CloseAll(silent: true, Tick);
        ushort lord = _duel!.LordId;
        if (musou.Count > 1)
        {
            _duelBuffer ??= new MusouEnemy[MusouSim.Capacity];
            int n = musou.CopyTo(_duelBuffer);
            int removed = 0;
            for (int k = 0; k < n; k++) if (_duelBuffer[k].Id != lord && musou.Remove(_duelBuffer[k].Id)) removed++;
            if (start && removed > 0) DuelLog($"{removed} other enemies removed (no credit)");
        }
        int duelist = _duelist;
        _projectiles.RemoveAll(q => duelist == 0 || q.Owner != duelist);
        _areas.RemoveAll(a => duelist == 0 || a.Owner != duelist);
        _perkBlasts.RemoveAll(b => duelist == 0 || b.Owner != duelist);
        if (_tfwBurns.Count > 0)
            foreach (ushort e in _tfwBurns.Where(kv => duelist == 0 || kv.Value.Owner != duelist).Select(kv => kv.Key).ToList()) _tfwBurns.Remove(e);
        run.Fire.WarfrontDuelDropShells();
        if (run.Pods.Count > 0 || run.Marines.Count > 0) TfwDismissMarines();
        if (start || _musouDrops.Count > 0) MusouClearDrops();   // the drops and every buff (World.MusouPowerups.cs)
        foreach (var (id, p) in _players.ToList())
        {
            if (id == duelist || TfwContracts.IsPuppet(id) || !p.Alive || run.Field.Hub.Contains(p.Position)) continue;
            var spawns = run.Field.Hub.Spawns;
            var spawn = spawns[Math.Abs(id) % Math.Max(1, spawns.Count)];
            TfwRespawnAt(id, spawn.Position, spawn.Yaw, faceCentre: false);   // aboard, held: no input, no damage (the CoopHeld wrap above)
            if (start) DuelLog($"P{id} aboard to watch");
        }
        DuelHoldPicker(rules);
        WarfrontDuelExecutions(0);
    }
}
