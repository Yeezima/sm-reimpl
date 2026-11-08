using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// The AI Marines in the World (TF5; design section E): real players without a peer, who act only by the input <see cref="MarineBrain"/> makes (<see cref="TfwMarineInput"/>, through
/// <see cref="TfwRun.PuppetBrain"/>). Two kinds stand side by side (owner 2026-11-08):
/// <list type="bullet">
/// <item>each human's retainer (<see cref="MarineRole.Retainer"/>): takes that player's orders (Follow, Hold, Attack), revives it, and after a death recovers for
/// <see cref="MarineRules.RetainerRecoverySeconds"/> war seconds before it is back at the landing zone ("return the player retainer bots with orders as well");</item>
/// <item>the allied pair (<see cref="MarineRole.Ally"/>, <see cref="AllyRules"/>): two whatever the number of players, landed at the landing zone, roaming together: the leader goes to the pair's goal
/// (<see cref="TfwBattle.AllyGoal"/>), reviving a fallen human on the way, the follower keeps a few metres behind him. No player orders them; a dead one is back at the landing zone
/// <see cref="AllyRules.RespawnSeconds"/> later.</item>
/// </list>
/// The lane lords, their retainers and expansion S6's Defenders stay gone.
/// </summary>
public sealed partial class World
{
    /// <summary>The lords' places by lane and their placeholder names (open question 7, labelled so): the war map still names a lane's lord; no lord fights in a battle since the owner's pair.</summary>
    static readonly (string Role, string Name)[] TfwLords = [("assault", "Aurex"), ("tactical", "Tolmar"), ("heavy", "Severin")];

    readonly List<Marine> _tfwMarineWork = new();

    /// <summary>
    /// Gives <paramref name="owner"/> its retainer (a Tactical, default loadout) at <paramref name="at"/>: one per player. Null, and nothing changed, outside a war, for an unknown player, or
    /// with one in the world, one recovering, or no free puppet id.
    /// </summary>
    public int? TfwSpawnRetainer(int owner, Vector3 at, float yaw = 0f)
    {
        if (_tfw is not { } run || !_players.ContainsKey(owner) || TfwContracts.IsPuppet(owner) || run.RetainerOf(owner) is not null || run.RecoveryLeft(owner) > 0f) return null;
        return TfwSpawnPuppet(run, "tactical", at, yaw, id => new Marine(id, -1, "Retainer", MarineRole.Retainer, owner) { HoldAt = at });
    }

    /// <summary>
    /// Lands the pair (<see cref="AllyRules.Count"/>) round <paramref name="at"/>, each in a free puppet id; the battle, if any, is told who they are, so they never capture. How many came (fewer with a
    /// Marine of the pair already here, or no free id).
    /// </summary>
    public int TfwSpawnAllies(Vector3 at, float yaw = 0f)
    {
        int n = 0;
        while (TfwSpawnAlly(at + new Vector3((n % 2) * 2f - 1f, -4f, 0f), yaw, ground: true) is not null) n++;
        return n;
    }

    /// <summary>
    /// One Marine of the pair at <paramref name="at"/> (the next of <see cref="AllyRules.Roster"/>); its id. Null, and nothing changed, outside a war, with the pair complete, or with no free puppet id.
    /// The first leads; the second follows him.
    /// </summary>
    public int? TfwSpawnAlly(Vector3 at, float yaw = 0f) => TfwSpawnAlly(at, yaw, ground: false);

    int? TfwSpawnAlly(Vector3 at, float yaw, bool ground)
    {
        if (_tfw is not { } run || run.Pair.Count >= AllyRules.Count) return null;
        int slot = 0;
        while (slot < AllyRules.Roster.Length && run.Pair.Any(m => m.Slot == slot)) slot++;
        var (role, name) = AllyRules.Roster[slot % AllyRules.Roster.Length];
        if (ground && Musou?.Nav is { } nav && nav.Nearest(at, 1.5f, 2.5f) is var node and >= 0) at = nav.Position(node);
        bool leads = run.Pair.Count == 0;
        int? id = TfwSpawnPuppet(run, role, at, yaw, id => new Marine(id, slot, name) { Leads = leads });
        if (id is not null) run.Battle?.SetAllies(run.Pair.Select(m => m.Id));
        return id;
    }

    /// <summary>A puppet of class <paramref name="role"/> in the first free puppet id, registered with the run; its id, null with no free id.</summary>
    int? TfwSpawnPuppet(TfwRun run, string role, Vector3 at, float yaw, Func<int, Marine> make)
    {
        for (int id = TfwContracts.PuppetIdFirst; id <= TfwContracts.PuppetIdLast; id++)
        {
            if (_players.ContainsKey(id)) continue;
            int playerClass = TfwMarineClass(role);
            var state = Spawn(id, playerClass, primaryChoice: Config.Classes[playerClass].PrimaryChoices.Count > 0 ? 0 : -1, team: Team.SpaceMarines);   // a prepared config has no default primary
            _players[id] = state with { Position = at, Yaw = yaw };
            run.AddMarine(make(id));
            run.PuppetBrain ??= TfwMarineInput;
            return id;
        }
        return null;
    }

    /// <summary>The Space Marines' class of a role; the first of theirs, else the first, when the config has no such class.</summary>
    int TfwMarineClass(string role)
    {
        int first = -1;
        for (int k = 0; k < Config.Classes.Count; k++)
        {
            var c = Config.Classes[k];
            if (c.Side != Team.SpaceMarines) continue;
            if (c.Role == role) return k;
            if (first < 0) first = k;
        }
        return Math.Max(first, 0);
    }

    /// <summary>Takes a puppet out of the world (a battle ends; a retainer's recovery, if any, runs on).</summary>
    public bool TfwDismissMarine(int id)
    {
        if (_tfw?.MarineOf(id) is not { } marine) return false;
        _tfw.RemoveMarine(marine);
        Remove(id);
        if (marine.Role == MarineRole.Ally && _tfw.Pair.Count == 1) _tfw.Pair[0].Leads = true;
        return true;
    }

    /// <summary>Takes every puppet out, keeping the recoveries and the orders (a battle ends).</summary>
    public void TfwDismissMarines()
    {
        if (_tfw is not { } run) return;
        _tfwMarineWork.Clear();
        _tfwMarineWork.AddRange(run.Marines);
        foreach (var m in _tfwMarineWork) TfwDismissMarine(m.Id);
    }

    /// <summary>A new run: no puppet, no recovery, no order.</summary>
    public void TfwResetMarines()
    {
        TfwDismissMarines();
        _tfw?.ForgetMarines();
    }

    /// <summary>
    /// Applies a player's order to its retainer now: orders reach only the retainer that player owns (never the allied pair). False for a resend, an old sequence or a command that is no order.
    /// A Hold holds the point the order names (the sender's crosshair), else where the retainer stands at that moment.
    /// </summary>
    public bool TfwOrderMarines(int player, TfwCommandRecord command)
    {
        if (_tfw is not { } run || !run.Orders.Apply(player, command)) return false;
        var order = run.Orders.Of(player);
        foreach (var m in run.Marines)
        {
            if (m.Role != MarineRole.Retainer || m.Owner != player || !_players.TryGetValue(m.Id, out var p)) continue;
            bool place = command.Point != default;   // the sender's crosshair, clamped by the run; none: where the retainer stands
            if (order == MarineOrder.Hold) m.HoldAt = place ? command.Point : p.Position;
            m.AttackAt = order == MarineOrder.Attack && place ? command.Point : null;
        }
        return true;
    }

    /// <summary>Whether <paramref name="id"/>'s retainer stands and can revive it.</summary>
    internal bool TfwRetainerUp(int id) => _tfw is { } run && run.RetainerOf(id) is { } m && _players.TryGetValue(m.Id, out var p) && p.Alive;

    /// <summary>Whether a Marine of the pair stands (a fallen human's revive window stays open while one does, or while its own retainer does; <see cref="TfwRun.AllyUp"/>).</summary>
    internal bool TfwAllyUp()
    {
        if (_tfw is not { } run) return false;
        foreach (var m in run.Pair) if (_players.TryGetValue(m.Id, out var p) && p.Alive) return true;
        return false;
    }

    /// <summary>Seconds until the first dead Marine of the pair is back (0: one stands, or none is waiting).</summary>
    internal float TfwAllyBackIn()
    {
        if (_tfw is not { } run || TfwAllyUp()) return 0f;
        int least = int.MaxValue;
        foreach (var m in run.Pair) if (m.Down) least = Math.Min(least, m.RespawnTicks);
        return least == int.MaxValue ? 0f : least / (float)Math.Max(1, Config.TickRate);
    }

    /// <summary>
    /// Brings player <paramref name="id"/> back where it fell with <see cref="MarineRules.ReviveShare"/> of its health and shield, keeping what it carries (no new life: no fresh ammunition,
    /// no pending loadout, no strip to the pistol). False, and nothing changed, outside a war, for an unknown player or one alive.
    /// </summary>
    public bool TfwRevive(int id)
    {
        if (_tfw is null || !_players.TryGetValue(id, out var p) || p.Alive) return false;
        var body = Loadouts.Body(Config, p);
        _players[id] = p with { Alive = true, Health = body.HealthMax * MarineRules.ReviveShare, Shield = body.ShieldMax * MarineRules.ReviveShare, RespawnLeft = 0f, SinceDamage = 0f, Grounded = true };
        _musouArmed.Add(id);
        return true;
    }

    /// <summary>
    /// The puppets' tick, after the fight (<c>TfwGuardsStep</c>, in <see cref="TfwStep"/>): the orders that came in, the recoveries, each retainer's death and its owner's revive, then the pair's
    /// goal, each Marine's death and return, who leads, and its revive (their ammunition: <see cref="TfwEndlessAmmo"/>, every player's since 2026-11-06).
    /// </summary>
    void TfwPuppetsStep()
    {
        var run = _tfw!;
        TfwTakeOrders(run);
        run.StepRecovery(1f / Math.Max(1, Config.TickRate));
        if (run.Marines.Count == 0) return;
        _tfwMarineWork.Clear();
        _tfwMarineWork.AddRange(run.Marines);
        foreach (var m in _tfwMarineWork)
        {
            if (!_players.TryGetValue(m.Id, out var p)) { run.RemoveMarine(m); continue; }
            if (m.Role == MarineRole.Retainer) { TfwRetainerStep(run, m, p); continue; }
            if (!p.Alive) TfwMarineDown(run, m);
            else m.Down = false;
        }
        if (run.Pair.Count == 0) return;
        TfwAllyLeader(run);
        TfwAllyGoal(run);
        foreach (var m in run.Pair)
        {
            if (!_players.TryGetValue(m.Id, out var p) || !p.Alive || !m.Leads) { m.ReviveOf = 0; m.ReviveTicks = 0; continue; }
            int body = TfwFallenToRevive(p.Position);
            if (body != m.ReviveOf) { m.ReviveOf = body; m.ReviveTicks = 0; }
            if (body == 0 || MarineBrain.Flat(p.Position, _players[body].Position) > MarineRules.ReviveRadius) { m.ReviveTicks = 0; continue; }
            if (++m.ReviveTicks < MarineRules.ReviveSeconds * Config.TickRate) continue;
            m.ReviveTicks = 0;
            m.ReviveOf = 0;
            TfwRevive(body);
        }
    }

    /// <summary>A retainer's tick: gone with its player, its death (the recovery), and the revive of its fallen owner while the owner's window is open.</summary>
    void TfwRetainerStep(TfwRun run, Marine m, in PlayerState p)
    {
        if (!_players.TryGetValue(m.Owner, out var owner)) { TfwDismissMarine(m.Id); return; }   // its player left
        if (!p.Alive) { TfwRetainerDown(run, m); return; }
        m.Down = false;
        if (owner.Alive || !run.ReviveWanted(m.Owner)) { m.ReviveOf = 0; m.ReviveTicks = 0; return; }
        m.ReviveOf = m.Owner;
        if (MarineBrain.Flat(p.Position, owner.Position) > MarineRules.ReviveRadius) { m.ReviveTicks = 0; return; }
        if (++m.ReviveTicks < MarineRules.ReviveSeconds * Config.TickRate) return;
        m.ReviveTicks = 0;
        m.ReviveOf = 0;
        TfwRevive(m.Owner);
    }

    /// <summary>A dead retainer: its fall once (the recovery starts), its respawn timer held, and once recovered back at the landing zone.</summary>
    void TfwRetainerDown(TfwRun run, Marine m)
    {
        m.ReviveTicks = 0;
        m.ReviveOf = 0;
        m.Target = 0;
        if (!m.Down)
        {
            m.Down = true;
            run.StartRecovery(m.Owner);
        }
        else if (run.RecoveryLeft(m.Owner) <= 0f)
        {
            var landing = run.Battle?.Layout.LandingZone ?? run.Field.MarineStart;
            TfwRespawnAt(m.Id, landing, 0f);
            m.HoldAt = landing;   // its post fell with it
            m.Down = false;
            m.WayGoal = -1;
            return;
        }
        _players[m.Id] = _players[m.Id] with { RespawnLeft = MarineRules.DeadHold };
    }

    /// <summary>Follow, Hold and Attack out of the command queue (to the sender's retainer); the run's other commands stay in it, in their order, for TF4.</summary>
    void TfwTakeOrders(TfwRun run)
    {
        for (int left = run.QueuedCommands; left > 0 && run.TryDequeue(out int player, out var command); left--)
        {
            if (command.Command is TfwCommand.Follow or TfwCommand.Hold or TfwCommand.Attack) TfwOrderMarines(player, command);
            else run.Enqueue(player, command);
        }
    }

    /// <summary>Exactly one living Marine of the pair leads: a lone survivor leads, and with both up the one that led goes on leading.</summary>
    void TfwAllyLeader(TfwRun run)
    {
        Marine? leader = null, alive = null;
        foreach (var m in run.Pair)
        {
            if (!_players.TryGetValue(m.Id, out var p) || !p.Alive) { m.Leads = false; continue; }
            alive ??= m;
            if (m.Leads && leader is null) leader = m;
            else m.Leads = false;
        }
        if (leader is null && alive is not null) alive.Leads = true;
    }

    /// <summary>Once every <see cref="AllyRules.GoalSeconds"/> the pair's goal: the battle's (<see cref="TfwBattle.AllyGoal"/>) from the leader's feet, else the run's active position.</summary>
    void TfwAllyGoal(TfwRun run)
    {
        if (run.Battle is not { } battle) { run.AllyGoal = run.ActivePosition; run.AllyGoalIndex = -2; return; }
        if (run.AllyGoal is not null && Tick % (uint)Math.Max(1, (int)(AllyRules.GoalSeconds * Config.TickRate)) != 0) return;
        Vector3 from = battle.Layout.LandingZone;
        foreach (var m in run.Pair) if (m.Leads && _players.TryGetValue(m.Id, out var p)) from = p.Position;
        run.AllyGoalIndex = battle.AllyGoal(from, run.AllyGoalIndex);
        run.AllyGoal = battle.AllyPlace(run.AllyGoalIndex);
    }

    /// <summary>The fallen human nearest to <paramref name="from"/> whose revive window is open (0: none).</summary>
    int TfwFallenToRevive(Vector3 from)
    {
        var run = _tfw!;
        int best = 0;
        float bestD = float.MaxValue;
        foreach (var (id, p) in _players)
        {
            if (TfwContracts.IsPuppet(id) || p.Alive || !run.ReviveWanted(id)) continue;
            float d = MarineBrain.Flat(from, p.Position);
            if (d < bestD) { bestD = d; best = id; }
        }
        return best;
    }

    /// <summary>A dead Marine of the pair: back at the landing zone <see cref="AllyRules.RespawnSeconds"/> (real seconds) after its death, with a fresh life; its respawn timer is held till then.</summary>
    void TfwMarineDown(TfwRun run, Marine m)
    {
        m.ReviveTicks = 0;
        m.ReviveOf = 0;
        m.Target = 0;
        m.Leads = false;
        if (!m.Down) { m.Down = true; m.RespawnTicks = Math.Max(1, (int)MathF.Round(AllyRules.RespawnSeconds * Math.Max(1, Config.TickRate))); }
        else if (--m.RespawnTicks <= 0)
        {
            var back = run.Battle?.Layout.LandingZone ?? run.Field.MarineStart;
            TfwRespawnAt(m.Id, back, 0f);
            m.Down = false;
            m.WayGoal = -1;
            return;
        }
        _players[m.Id] = _players[m.Id] with { RespawnLeft = MarineRules.DeadHold };
    }

    /// <summary>A puppet's input this tick (<see cref="TfwRun.PuppetBrain"/>): a retainer's order or its part in the pair, the enemy it looks at every <see cref="MarineRules.RetargetTicks"/> ticks, then <see cref="MarineBrain"/>.</summary>
    PlayerInput TfwMarineInput(int id, PlayerState self)
    {
        var standing = new PlayerInput(0, 0f, 0f, self.Yaw, InputButtons.None);
        var run = _tfw!;
        if (!self.Alive || run.MarineOf(id) is not { } m) return standing;

        // A retainer: its owner's order. The pair: who leads and who follows.
        bool retainer = m.Role == MarineRole.Retainer;
        Vector3? ownerAt = null;
        bool ownerAlive = false;
        if (retainer && _players.TryGetValue(m.Owner, out var owner)) { ownerAt = owner.Position; ownerAlive = owner.Alive; }
        var order = retainer ? run.Orders.Of(m.Owner) : MarineOrder.Attack;
        Vector3? partnerAt = null;
        if (!retainer && run.PartnerOf(m) is { } partner && _players.TryGetValue(partner.Id, out var pp) && pp.Alive) partnerAt = pp.Position;
        bool follows = !retainer && !m.Leads && partnerAt is not null;

        var musou = _musou!;
        bool struck = m.Target != 0 && musou.TryGet(m.Target, out var held) && held.State != MusouEnemyState.Dying;
        if (!struck || (Tick + (uint)id) % MarineRules.RetargetTicks == 0)
        {
            var (centre, radius) = retainer
                ? order switch
                {
                    MarineOrder.Follow => (self.Position, MarineRules.FollowEngage),
                    MarineOrder.Hold => (m.HoldAt, MarineRules.HoldRadius),
                    _ => (self.Position, MarineRules.AttackSight),
                }
                : (self.Position, follows ? MarineRules.FollowEngage : MarineRules.AttackSight);
            Span<ushort> nearest = stackalloc ushort[MarineRules.SightCandidates];
            int seen = musou.Query(centre, radius, nearest);
            m.Target = 0;
            for (int k = 0; k < seen && m.Target == 0; k++)
                if (TfwMarineSees(self, nearest[k])) m.Target = nearest[k];
        }

        Vector3? enemyAt = null, chest = null;
        float distance = float.MaxValue;
        if (m.Target != 0 && musou.TryGet(m.Target, out var enemy) && enemy.State != MusouEnemyState.Dying)
        {
            enemyAt = enemy.Position;
            chest = MusouSim.Chest(enemy.Position, musou.Body(enemy.Type).Height);
            distance = MarineBrain.Flat(self.Position, enemy.Position);
        }

        bool strikesOnly = Loadouts.Weapon(Config, self, WeaponSlot.Ranged) is null;
        MarineGoal? goal;
        if (retainer)
        {
            bool revive = ownerAt is not null && !ownerAlive && run.ReviveWanted(m.Owner);
            var objective = m.AttackAt ?? run.Battle?.GoalFrom(self.Position) ?? run.ActivePosition;
            goal = MarineBrain.PlanRetainer(new RetainerSituation(order, self.Position, ownerAt, revive, m.HoldAt, objective, enemyAt, strikesOnly));
        }
        else
        {
            Vector3? revive = m.ReviveOf != 0 && _players.TryGetValue(m.ReviveOf, out var fallen) && !fallen.Alive ? fallen.Position : null;
            // The leader waits for a follower left behind (he fights where he stands), and sprints only alone, so the pair keeps together.
            bool waits = !follows && partnerAt is { } behind && MarineBrain.Flat(self.Position, behind) > AllyRules.LeaderWait;
            goal = MarineBrain.Plan(new MarineSituation(self.Position, follows ? partnerAt : null, waits ? null : revive, waits ? null : run.AllyGoal, enemyAt, strikesOnly, MaySprint: partnerAt is null));
        }
        if (goal is { } g && g.Point != enemyAt && MarineBrain.Flat(self.Position, g.Point) > g.Far && TfwGuards?.Ways is { } ways)
        {
            if (m.WayGoal < 0 || MarineBrain.Flat(m.WayPoint, g.Point) > MarineRules.WayRefresh) { m.WayPoint = g.Point; m.WayGoal = ways.GoalNode(g.Point); }
            if (ways.Carrot(self.Position, m.WayGoal) is { } next) goal = g with { Via = next };
        }
        return MarineBrain.Decide(self, Loadouts.InHand(Config, self), goal, chest, distance, Config.EyeHeight, Tick);
    }

    /// <summary>Whether the enemy's chest is in sight of the puppet's eyes: the weapons mesh, as its shot will meet it.</summary>
    bool TfwMarineSees(in PlayerState self, ushort enemy)
    {
        var musou = _musou!;
        if (!musou.TryGet(enemy, out var e)) return false;
        var eye = self.Position + new Vector3(0f, 0f, Config.EyeHeight);
        var ray = MusouSim.Chest(e.Position, musou.Body(e.Type).Height) - eye;
        float length = ray.Length();
        return length < 1e-3f || Collision is not { } world || !(world.Weapons.Raycast(eye, ray / length, length) is { } wall && wall.Distance < length - 0.1f);
    }
}
