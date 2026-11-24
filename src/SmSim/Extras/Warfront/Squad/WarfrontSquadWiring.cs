using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Maps;
using SmSim.Extras.Warfront.Progression;
using SmSim.Extras.Warfront.Results;
using SmSim.Extras.Warfront.Retainers;

namespace SmSim.Extras.Warfront.Squad;

/// <summary>A campaign key's chosen squad class (the Librarian's option), part of <see cref="WarfrontSquadSave"/>.</summary>
public sealed record SquadClassPick(ulong Key, SquadClass Class);

/// <summary>
/// The squad system's whole save value (slice W9b): the rosters, the class book and the Librarian's chosen squad classes. Keys are campaign keys: this is live state like run.json (W5a writes it beside it as
/// <c>squads.json</c> at its save points, passes it back at start, deletes it at the run's end) and is never archived.
/// </summary>
public sealed record WarfrontSquadSave(SquadRosterSave Rosters, SquadClassSave Classes, IReadOnlyList<SquadClassPick> Picks)
{
    public static WarfrontSquadSave Empty { get; } = new(new SquadRosterSave(0, []), new SquadClassSave([]), []);
}

/// <summary>
/// Connects what already exists (slice W9b): the squad sim (W9, W11) to W3's and W3b's battle seams, the squads' kills to their owners' XP (W7), the cards to the rosters, and every countable event to the stats ledger (W4b).
/// One object per Warfront server, built once the run's <see cref="WarfrontBattleRules"/> exist (and after its <see cref="WarfrontBattleRules.Progression"/> is set: it hangs on that instance's callbacks); the
/// server slice (W5a) only constructs it, calls <see cref="Import"/> at start, <see cref="Export"/> at its save points, <see cref="RecordsOf"/> at the run's end, <see cref="Reset"/> on a new run and
/// <see cref="StepAboard"/> once a tick. Pure and deterministic: players and keys in sorted order, no wall clock, no <c>System.Random</c>. Spec "Added content: Warfront: the squad wiring".
/// </summary>
public sealed class WarfrontSquadWiring
{
    readonly World _world;
    readonly WarfrontBattleRules _rules;
    readonly WarfrontCampaign _campaign;
    readonly ulong _runSeed;
    readonly WarfrontLedgerFeed _feed;
    readonly Dictionary<ulong, SquadClass> _squadClass = [];
    readonly Dictionary<ulong, (LoadoutChoice Choice, int LookHash, SquadOwnerKind Kind, SquadClass Squad)> _seen = [];
    readonly Dictionary<ulong, int> _playerOfKey = [];       // the player id each key was last fighting under (a leaver's squad is held through it)
    readonly Dictionary<ulong, (int Cards, int Level)> _scaleCards = [];        // the card count and level a key's scale was last set for
    readonly HashSet<int> _tried = [];                       // player ids already offered a landing this battle
    readonly HashSet<int> _wasDowned = [];
    readonly HashSet<ulong> _held = [];                      // keys whose squad was put on Hold because the player left
    readonly List<int> _field = [];
    readonly List<ulong> _gone = [];
    SquadHuman[] _humans = new SquadHuman[8];
    ushort _holdSequence = 60_000;
    bool _began;

    public WarfrontSquadWiring(World world, WarfrontBattleRules rules, WarfrontCampaign campaign, ulong runSeed)
    {
        _world = world; _rules = rules; _campaign = campaign; _runSeed = runSeed;
        _feed = new WarfrontLedgerFeed(campaign.Stats, () => campaign.Deployments + 1);
        Rosters = new SquadRosters();
        ClassBook = new SquadClassBook(world.Config, Rosters, runSeed);
        Retainers = new WarfrontRetainers(world, Rosters) { Log = line => Log?.Invoke(line) };
        Connect();
    }

    /// <summary>The rosters (one per campaign key).</summary>
    public SquadRosters Rosters { get; }
    /// <summary>The class book (who the retainers copy).</summary>
    public SquadClassBook ClassBook { get; }
    /// <summary>
    /// The retainers as World players (Phase B T2): every roster Marine lands with its owner as a real player in a puppet id, so it moves, fights, animates and sounds as a player does. The squad sim's movement,
    /// combat and targets are retired (<see cref="WarfrontSquadSim"/> is no longer stepped); rosters, names, records, the ledger and the results stay here.
    /// </summary>
    public WarfrontRetainers Retainers { get; }
    /// <summary>The ledger feed (tests and W5a's end-of-run read the ledger through the campaign).</summary>
    public WarfrontLedgerFeed Feed => _feed;

    /// <summary>The server's source of a player's look (his appearance code), for the colours his retainers wear; null: the default look.</summary>
    public Func<int, AppearanceCode?>? LookOf { get; set; }
    /// <summary>The stuck log the squad's stranded failsafe wrote to (W5a sets it; null: none). Retired with the squad sim's movement: nothing writes it until the retainers' brain (Phase B T4) has a failsafe of its own.</summary>
    public WarfrontStuckLog? StuckLog { get; set; }
    /// <summary>The name of the map slot for the stuck log (W5a sets it; default: lane and node).</summary>
    public Func<string>? SlotName { get; set; }
    /// <summary>W12's <c>Save()</c>: the server's save callback (<see cref="Save"/> invokes it).</summary>
    public Action? SaveCallback { get; set; }

    // ---- the save ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>The whole squad state as one value.</summary>
    public WarfrontSquadSave Export() => new(Rosters.Export(), ClassBook.Export(), [.. _squadClass.OrderBy(p => p.Key).Select(p => new SquadClassPick(p.Key, p.Value))]);

    /// <summary>Rebuilds the squad state from a save; null (a missing file) is an empty value.</summary>
    public void Import(WarfrontSquadSave? save)
    {
        save ??= WarfrontSquadSave.Empty;
        Rosters.Import(OfferedMelee(_world.Config, save.Rosters));
        ClassBook.Import(save.Classes);
        _squadClass.Clear();
        foreach (var p in save.Picks) _squadClass[p.Key] = p.Class;
        foreach (var k in save.Classes.Keys) if (k.Last is { Kind: SquadOwnerKind.Librarian } l) _squadClass.TryAdd(k.Key, l.Librarian);
        _seen.Clear(); _scaleCards.Clear();
        Retainers.BeginBattle();
    }

    /// <summary>
    /// The rosters of a save with every Marine's removed melee weapon (the lightning claw or the power spear the config no longer offers; owner 2026-11-23) put back to the class default
    /// (<see cref="SquadLoadoutBuilder.WithOfferedMelee"/>); the same value when none holds one.
    /// </summary>
    public static SquadRosterSave OfferedMelee(SimConfig config, SquadRosterSave save)
    {
        if (!save.Owners.Any(o => o.Marines.Any(m => SmSim.Extras.Musou.Arsenal.ArsenalRules.IsSpearOrClaw(m.Loadout.Melee)))) return save;
        return save with
        {
            Owners = [.. save.Owners.Select(o => o with { Marines = [.. o.Marines.Select(m => m with { Loadout = SquadLoadoutBuilder.WithOfferedMelee(config, m.Loadout) })] })],
        };
    }

    /// <summary>A new run: rosters, class book and sim cleared.</summary>
    public void Reset()
    {
        Import(WarfrontSquadSave.Empty);
        _playerOfKey.Clear(); _held.Clear(); _tried.Clear(); _wasDowned.Clear(); _began = false;
    }

    /// <summary>One key's Marines as the end-of-run record wants them (a squad as it stands, the fallen included).</summary>
    public IReadOnlyList<WarfrontRetainerStats> RecordsOf(ulong key)
    {
        var list = new List<WarfrontRetainerStats>();
        for (int slot = 0; slot < Rosters.Count(key); slot++)
        {
            var m = Rosters.Marine(key, slot)!; var r = m.Record;
            list.Add(new WarfrontRetainerStats(m.Name, m.Loadout.ClassId, slot, r.Kills, r.EliteKills, r.BossKills, r.Assists, r.TimesDowned, r.Revives, r.Objectives, r.BattlesSurvived));
        }
        return list;
    }

    // ---- W12's host (the same five members, ahead of the interface) ------------------------------------------------------------------------

    /// <summary>Whether the key has a roster.</summary>
    public bool HasRoster(ulong key) => Rosters.Has(key);

    /// <summary>
    /// The first roster of a key, through the class book (a Dreadnought-first player then holds none and gets the free one on his switch): one retainer, once. Nothing when a roster exists. False when the key's player is not in the
    /// World (the first tick aboard makes it) or the kept rosters are full.
    /// </summary>
    public bool AddStartingRetainer(ulong key)
    {
        if (Rosters.Has(key)) return true;
        if (_rules.Progression.TryId(key, out int id) && _world.Players.ContainsKey(id)) Remember(id, key);
        return Rosters.Has(key);
    }

    /// <summary>A leaver's squad holds where it stands and keeps counting as standing (order Hold); it is not removed.</summary>
    public bool HoldSquad(ulong key)
    {
        if (!_playerOfKey.TryGetValue(key, out int player)) return false;
        for (int k = 0; k < 2; k++)   // a sequence equal to the squad's last is refused: try the next
            if (Retainers.Order(player, new TfwCommandRecord(++_holdSequence, TfwCommand.Hold, 0))) return true;
        return false;
    }

    /// <summary>A returning player's new id for the squad already fighting.</summary>
    public bool RebindSquad(ulong key, int player)
    {
        if (!Retainers.Rebind(key, player)) return false;
        _playerOfKey[key] = player; _tried.Add(player);
        return true;
    }

    /// <summary>Asks the server to save now.</summary>
    public void Save() => SaveCallback?.Invoke();

    // ---- W10's commands ---------------------------------------------------------------------------------------------------------------------

    /// <summary>W10's command (tag 41, kind SquadClass): the Librarian's squad class. Stored for the run; a Librarian's squad is rerolled once, the same class again changes nothing.</summary>
    public void SetSquadClass(ulong key, SquadClass squad)
    {
        _squadClass[key] = squad;
        _seen.Remove(key);   // the next look at the ship re-applies it
        if (_world.Tfw?.Phase == TfwRunPhase.Hub && ClassBook.LastOf(key) is { Kind: SquadOwnerKind.Librarian } && _rules.Progression.TryId(key, out int id) && _world.Players.ContainsKey(id))
            Remember(id, key);   // a Librarian aboard: his squad is rerolled now (the same class again changes nothing)
    }

    /// <summary>W10's command: the Assault squad's jump-pound at a marked point. Retired with the squad sim (Phase B T2): false until the retainers' brain (T4) takes the order; the command is still read and refused.</summary>
    public bool OrderJumpPound(int player, Vector3 mark) => false;

    // ---- the connections --------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The seam rule (W9b review 1): the notifying seams (<c>BattleBegan</c>, <c>BattleEnded</c>, <c>ObjectiveSecured</c>, <c>AboardStep</c>, <c>OwnerLevelUp</c>, <c>NodeEnded</c>, <c>NodeResolved</c> and the runtime's <c>Assisted</c>,
    /// <c>HumanKill</c>, <c>Picked</c>) are subscribed with <c>+=</c>, so every slice's handler stays; the answering seams (<c>SquadTargets</c>, <c>SquadHit</c>, <c>SquadStep</c>, <c>OrderSink</c>, <c>AnyRetainerStanding</c>,
    /// <c>RosterSize</c>) are assigned by the wiring alone and by nobody else.
    /// </summary>
    void Connect()
    {
        var rules = _rules;
        rules.BattleBegan += (_, _) => OnBattleBegan();
        rules.BattleEnded += _ => OnBattleEnded();
        rules.ObjectiveSecured += OnObjective;
        rules.AboardStep += OnAboardTick;
        rules.OwnerLevelUp += key => Retainers.Refill(key);   // the owner's standing Marines refill with him
        rules.Retainers = Retainers;   // Phase B T2: the World's retainer hooks (input, damage, kills, the owner's answers) read it
        rules.SquadTargets = null;   // retired: the Orks see the retainers as the World players they are
        rules.SquadHit = null;
        rules.SquadStep = OnSquadStep;
        rules.OrderSink = (player, record) => Retainers.Order(player, record);
        rules.AnyRetainerStanding = () => Retainers.AnyStanding;
        Retainers.KillSink = k => { if (PaySquadKill(k, (uint)_world.Tick)) rules.Progression.Refill?.Invoke(); };
        var rt = rules.Progression;
        rt.RosterSize = key => Rosters.Count(key);
        rt.Assisted += (helper, killer, type) => { if (rt.TryKey(helper, out ulong key)) _feed.Assist(key); };
        rt.HumanKill += OnHumanKill;
        rt.Picked += pick => { if (pick.Card == WarfrontCard.Retainer) ClassBook.Recruit(pick.Key); };   // one pick, one recruit: never from the card count
    }

    void OnBattleBegan()
    {
        Retainers.BeginBattle();   // the humans are placed after this hook: their Marines land at the first step, at their own landing place
        _tried.Clear(); _wasDowned.Clear(); _playerOfKey.Clear(); _held.Clear(); _scaleCards.Clear();
        _began = true;
    }

    void OnBattleEnded()
    {
        if (!_began) return;
        Retainers.EndBattle();   // the standing Marines survived it; nothing is paid for it
        _began = false;
        _tried.Clear(); _wasDowned.Clear();
    }

    void OnHumanKill(WarfrontHumanKill kill)
    {
        _feed.Kill(kill.Key, kill.Type, kill.Enemy, kill.Tick);
    }

    void OnObjective(int index)
    {
        if (_world.Tfw?.Battle is not { } battle || index < 0 || index >= battle.Objectives.Count) return;
        var o = battle.Objectives[index];
        Retainers.CreditObjective(o.Position, o.Radius);
        _field.Clear(); _field.AddRange(_world.Tfw.InField); _field.Sort();
        foreach (int id in _field)
        {
            if (!_rules.Progression.TryKey(id, out ulong key) || !_world.Players.TryGetValue(id, out var p)) continue;
            var down = _world.Tfw.WarfrontDownState(id).Phase;
            if (!p.Alive && down != WarfrontDownPhase.Downed) continue;   // standing or downed inside the radius; a spectator is not on the point
            if (new Vector2(p.Position.X - o.Position.X, p.Position.Y - o.Position.Y).Length() <= o.Radius) _feed.Capture(key, index);
        }
    }

    // ---- aboard ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>The aboard seam's handler: every <see cref="AboardEvery"/> ticks (a compare of a few numbers per player is cheap, but it need not run at 30 Hz).</summary>
    void OnAboardTick()
    {
        if (_world.Tick % AboardEvery == 0) StepAboard();
    }

    /// <summary>Ticks between two looks at the ship's loadouts [ours].</summary>
    public const int AboardEvery = 15;

    readonly List<int> _ids = [];

    /// <summary>
    /// Aboard the ship (the hub and the way down; <see cref="WarfrontBattleRules.AboardStep"/> calls it every <see cref="AboardEvery"/> ticks; public for tests): every human with a key whose class, loadout choices, appearance or squad class changed
    /// since last seen is told to the class book. This creates a first roster of one, holds a Dreadnought's roster, gives the free retainer once and makes the squad follow its owner's loadout change. A player W12 holds (a joiner who has not
    /// confirmed his class) is skipped, so the first kind the book sees is the confirmed one.
    /// </summary>
    public void StepAboard()
    {
        if (_world.Tfw is not { } run || run.Phase is not (TfwRunPhase.Hub or TfwRunPhase.Travel)) return;
        var rt = _rules.Progression;
        _ids.Clear(); _ids.AddRange(rt.Ids); _ids.Sort();
        foreach (int id in _ids)
        {
            if (!rt.TryKey(id, out ulong key) || !_world.Players.ContainsKey(id) || _rules.CoopHeld?.Invoke(id) == true) continue;
            Remember(id, key);
        }
    }

    void Remember(int id, ulong key)
    {
        if (_world.WarfrontChoiceOf(id) is not { } choice || !_world.Players.TryGetValue(id, out var p)) return;
        var look = LookOf?.Invoke(id) ?? AppearanceCode.Default;
        int lookHash = look.ToArray().Aggregate(17, (h, b) => h * 31 + b);
        // aboard, a Dreadnought pick plays as the Tactical stand-in: the pick is what counts
        var kind = DreadnoughtRules.IsDreadnought(_world.Config, p.Class) || _world.DreadnoughtPicked(id) ? SquadOwnerKind.Dreadnought  : LibrarianRules.IsLibrarian(_world.Config, p.Class) ? SquadOwnerKind.Librarian : SquadOwnerKind.Standard;
        var squad = _squadClass.GetValueOrDefault(key, SquadClass.Tactical);
        var now = (choice, lookHash, kind, squad);
        if (_seen.TryGetValue(key, out var was) && was == now && Rosters.Has(key)) return;
        _seen[key] = now;
        var side = p.Class >= 0 && p.Class < _world.Config.Classes.Count && _world.Config.Classes[p.Class].Side == Team.Chaos ? Team.Chaos : Team.SpaceMarines;
        var owner = kind switch
        {
            SquadOwnerKind.Dreadnought => SquadOwnerLoadout.Dreadnought(),
            SquadOwnerKind.Librarian => SquadOwnerLoadout.ForLibrarian(squad, look, side),
            _ => SquadOwnerLoadout.Standard(choice, look),
        };
        ClassBook.OnOwnerLoadout(key, owner);
    }

    // ---- the battle step ---------------------------------------------------------------------------------------------------------------------

    void OnSquadStep()
    {
        if (_world.Tfw is not { Battle: not null } run || !_began) return;
        var rt = _rules.Progression;
        uint tick = (uint)_world.Tick;
        _field.Clear(); _field.AddRange(run.InField); _field.Sort();
        if (_humans.Length < _field.Count) _humans = new SquadHuman[_field.Count * 2];
        int n = 0;
        foreach (int id in _field)
        {
            if (!rt.TryKey(id, out ulong key) || !_world.Players.TryGetValue(id, out var p)) continue;
            LandIfNew(id, key, p);
            var phase = run.WarfrontDownState(id).Phase;
            bool fallen = !p.Alive && phase == WarfrontDownPhase.Downed;
            if (fallen) { if (_wasDowned.Add(id)) _feed.Death(key, id, tick); }   // each fall counts [interpretation]
            else _wasDowned.Remove(id);
            _humans[n++] = new SquadHuman(id, p.Position, p.Alive, fallen, -1, p.Yaw);
        }
        ReleaseLeavers();
        RefreshScales(rt);
        Retainers.Step();   // the squad's rules over the World players: downed, revived, out
        Revives(run, n, tick);
    }

    /// <summary>A human in the field with no fighting squad: a returning key re-binds, a new one lands at the human's own place; once per player id and battle.</summary>
    void LandIfNew(int id, ulong key, in PlayerState p)
    {
        bool returned = _held.Remove(key);
        _playerOfKey[key] = id;
        if (!_tried.Add(id)) return;
        _feed.Battle(key);
        if (ClassBook.LastOf(key) is null) Remember(id, key);   // no class-book entry yet (a first-time joiner landed late): a first roster
        ApplyScale(_rules.Progression, key);
        if (Retainers.Rebind(key, id))
        {
            if (returned) for (int k = 0; k < 2 && !Retainers.Order(id, new TfwCommandRecord(++_holdSequence, TfwCommand.Follow, 0)); k++) { }   // a squad put on Hold when he left follows again when he is back
        }
        else if (Retainers.Land(key, id, p.Position) == 0 && Rosters.Has(key) && !Rosters.IsHeld(key))
            Log?.Invoke($"warfront: no fighting slot or World id for key {key}'s squad; it joins at the next battle");
    }

    /// <summary>The server's log line sink (W5a sets it; null: none).</summary>
    public Action<string>? Log { get; set; }

    readonly Dictionary<int, (int Target, float Progress)> _revive = [];   // reviver's player id -> (target: a human's id, or -(retainer index + 1)), progress in seconds
    readonly Dictionary<int, float> _share = [];
    readonly HashSet<int> _reviving = [];
    readonly List<(int Id, Vector3 At, int Marine)> _revivers = [];

    /// <summary>
    /// Revived by anyone (Phase B T2; the squad's rule): a human in the field or a standing retainer holding Use (the Interact bit) with a downed retainer or a fallen human (phase Downed) within
    /// <see cref="SquadRules.PlayerReviveReach"/> revives the nearer (a tie goes to the human) after <see cref="SquadRules.PlayerReviveSeconds"/>: a human through W3's <c>WarfrontRevive</c>, a retainer back where it fell.
    /// A human reviver's revive counts in his ledger, a retainer's in its record. Progress is per reviver and is dropped when Use is released, the target changes or either leaves reach.
    /// </summary>
    void Revives(TfwRun run, int n, uint tick)
    {
        float dt = 1f / Math.Max(1, _world.Config.TickRate);
        _share.Clear(); _reviving.Clear(); _revivers.Clear();
        for (int i = 0; i < n; i++) if (_humans[i].Alive && _world.WarfrontUseHeld(_humans[i].Id)) _revivers.Add((_humans[i].Id, _humans[i].Position, -1));
        foreach (var r in Retainers.All)
            if (r.State == SquadState.Standing && _world.Players.TryGetValue(r.WorldId, out var rp) && rp.Alive && _world.WarfrontUseHeld(r.WorldId)) _revivers.Add((r.WorldId, rp.Position, r.Index));
        foreach (var r in Retainers.All) if (r.State == SquadState.Downed) r.ReviveShare = 0f;
        foreach (var (id, at, marine) in _revivers)
        {
            int target = 0; float best = SquadRules.PlayerReviveReach + 1e-4f;
            for (int j = 0; j < n; j++)
            {
                var t = _humans[j];
                if (t.Id == id || !t.Fallen) continue;
                float d = Flat(t.Position, at);
                if (d < best) { best = d; target = t.Id; }
            }
            foreach (var r in Retainers.All)
            {
                if (r.State != SquadState.Downed || r.WorldId == id || !_world.Players.TryGetValue(r.WorldId, out var dp)) continue;
                float d = Flat(dp.Position, at);
                if (d < best) { best = d; target = -(r.Index + 1); }
            }
            if (target == 0) continue;
            _reviving.Add(id);
            float progress = _revive.TryGetValue(id, out var was) && was.Target == target ? was.Progress + dt : dt;
            if (progress < SquadRules.PlayerReviveSeconds - 1e-4f)
            {
                _revive[id] = (target, progress);
                float share = progress / SquadRules.PlayerReviveSeconds;
                if (target > 0) _share[target] = Math.Max(_share.GetValueOrDefault(target), share);
                else if (Retainers.ByIndex(-target - 1) is { } downed) downed.ReviveShare = Math.Max(downed.ReviveShare, share);
                continue;
            }
            _revive.Remove(id);
            bool done = target > 0 ? run.WarfrontRevive(_world, target) : Retainers.ByIndex(-target - 1) is { } r2 && Retainers.Revive(r2, marine < 0 ? id : -1, marine);
            if (!done) continue;
            if (marine < 0 && _rules.Progression.TryKey(id, out ulong key)) _feed.Revive(key, id, tick);
            else if (marine >= 0 && target > 0) Retainers.RevivedHuman(marine, target);
        }
        foreach (int id in _revive.Keys.ToList()) if (!_reviving.Contains(id)) _revive.Remove(id);   // Use released, target gone: the hold starts again
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

    /// <summary>The share (0 to 1) of the hold that is reviving fallen human <paramref name="target"/> now, for the client's prompt; 0 when nobody is.</summary>
    public float HumanReviveShare(int target) => _share.GetValueOrDefault(target);

    /// <summary>A key whose player left the World is held (order Hold, not removed): the squad keeps counting as standing and is re-bound on his return.</summary>
    void ReleaseLeavers()
    {
        _gone.Clear();
        foreach (var (key, id) in _playerOfKey) if (!_world.Players.ContainsKey(id) && !_held.Contains(key)) _gone.Add(key);
        _gone.Sort();
        foreach (ulong key in _gone) { HoldSquad(key); _held.Add(key); }
    }

    /// <summary>The cards' retainer third: damage now, health for Marines landed after (the sim's <c>SetScale</c>); set when the key's card count changed.</summary>
    void RefreshScales(WarfrontProgressionRuntime rt)
    {
        foreach (var (key, id) in _playerOfKey) if (_world.Players.ContainsKey(id)) ApplyScale(rt, key, onlyIfChanged: true);
    }

    void ApplyScale(WarfrontProgressionRuntime rt, ulong key, bool onlyIfChanged = false)
    {
        var player = _campaign.Player(key);
        int cards = player?.Cards.Count ?? 0, level = Math.Max(1, player?.Level ?? 1);
        if (onlyIfChanged && _scaleCards.TryGetValue(key, out var was) && was == (cards, level)) return;
        _scaleCards[key] = (cards, level);
        var b = WarfrontStatBonuses.ForRetainer(player?.Cards ?? []);
        var (levelHealth, levelDamage) = SquadLevelScale(level);
        Retainers.SetScale(key, b.Damage * b.AttackSpeed * levelDamage, b.MaxHp * levelHealth, b.MaxArmor);   // a third of each card's magnitude (the attack speed folded into the damage, as before); the cooldown share has nothing to act on
    }

    /// <summary>
    /// The level part of a retainer's strength (owner 2026-11-19): health x (1 + 0.08 (L-1)) and damage x (1 + 0.05 (L-1)), the Orks' own per-level curve (<see cref="WarfrontProgressionRules.OrkHealthPerLevel"/> and
    /// <see cref="WarfrontProgressionRules.OrkDamagePerLevel"/>; without the Orks' base knobs), on top of the cards' third. Level 1: exactly 1 and 1. Health applies to Marines landed after (a level-up refills the standing ones), damage at once.
    /// </summary>
    public static (float Health, float Damage) SquadLevelScale(int level)
    {
        int l = Math.Max(1, level);
        return (1f + WarfrontProgressionRules.OrkHealthPerLevel * (l - 1), 1f + WarfrontProgressionRules.OrkDamagePerLevel * (l - 1));
    }

    /// <summary>
    /// Pays one squad kill to its owner's key (the owner's rule): an ordinary kill pays the owner (when the server's <c>Eligible</c> allows him), an elite or boss kill every connected player once. The same death
    /// (the World tick and the pool id) never pays twice. Squad kills never reach the player's ledger kills (the Marine's service record holds them). True when anybody was paid.
    /// </summary>
    public bool PaySquadKill(in SquadKill k, uint tick)
    {
        var rt = _rules.Progression;
        var type = k.Type ?? (k.Class == SquadEnemyClass.Boss ? MusouEnemyType.BossNob : k.Class == SquadEnemyClass.Elite ? MusouEnemyType.Nob : MusouEnemyType.Boy);
        int points = _world.Musou?.Rules.Of(type).Points ?? 0;
        var cls = WarfrontXpLedger.Classify(type);
        if (cls == WarfrontKillClass.Ordinary && (rt.Eligible?.Invoke(k.Key) == false || !_campaign.IsConnected(k.Key))) return false;   // a spectating joiner and an absent owner get none
        return rt.Xp.Kill(k.Key, points, cls, ((long)tick << 16) | k.Enemy).Count > 0;
    }
}
