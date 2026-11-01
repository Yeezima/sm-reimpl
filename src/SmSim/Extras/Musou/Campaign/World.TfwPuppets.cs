using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// The AI Marines in the World (TF5; design section E): real players without a peer, who act only by the input <see cref="MarineBrain"/> makes (<see cref="TfwMarineInput"/>, through
/// <see cref="TfwRun.PuppetBrain"/>). A player's retainer takes that player's orders and revives it; a lord goes by himself to <see cref="TfwRun.ActivePosition"/> with a retainer of
/// his own, and no player orders either. A dead puppet stays dead (its respawn timer held) until its recovery ends (a retainer) or for good (a lord: the war wounds him).
/// </summary>
public sealed partial class World
{
    /// <summary>The lords' places by lane (classes by role, the design's order: Assault, Tactical, Devastator) and their placeholder names (open question 7, labelled so).</summary>
    static readonly (string Role, string Name)[] TfwLords = [("assault", "Aurex"), ("tactical", "Tolmar"), ("heavy", "Severin")];

    readonly List<Marine> _tfwMarineWork = new();

    /// <summary>
    /// Gives <paramref name="owner"/> its retainer (a Tactical, default loadout) at <paramref name="at"/>: one per player. Null, and nothing changed, outside a war, for an unknown player, or
    /// with one in the world, one recovering, or no free puppet id.
    /// </summary>
    public int? TfwSpawnRetainer(int owner, Vector3 at, float yaw = 0f)
    {
        if (_tfw is not { } run || !_players.ContainsKey(owner) || run.RetainerOf(owner) is not null || run.RecoveryLeft(owner) > 0f) return null;
        return TfwSpawnMarine(MarineRole.Retainer, owner, default, "tactical", "Retainer", at, yaw)?.Id;
    }

    /// <summary>
    /// Brings the lord of <paramref name="lane"/> and his Tactical retainer to <paramref name="at"/> (one lord per lane). His id; null, and nothing changed, outside a war, with him already
    /// here, or without two free puppet ids.
    /// </summary>
    public int? TfwSpawnLord(TfwLane lane, Vector3 at, float yaw = 0f)
    {
        if (_tfw is not { } run || run.LordOf(lane) is not null) return null;
        int free = 0;
        for (int id = TfwContracts.PuppetIdFirst; id <= TfwContracts.PuppetIdLast; id++) if (!_players.ContainsKey(id)) free++;
        if (free < 2) return null;
        var (role, name) = TfwLords[(int)lane];
        var lord = TfwSpawnMarine(MarineRole.Lord, 0, lane, role, $"Brother-Captain {name}", at, yaw)!;
        TfwSpawnMarine(MarineRole.LordRetainer, lord.Id, lane, "tactical", $"Retainer of {name}", at + new Vector3(1.5f, -1.5f, 0f), yaw);
        return lord.Id;
    }

    Marine? TfwSpawnMarine(MarineRole role, int owner, TfwLane lane, string classRole, string name, Vector3 at, float yaw)
    {
        var run = _tfw!;
        for (int id = TfwContracts.PuppetIdFirst; id <= TfwContracts.PuppetIdLast; id++)
        {
            if (_players.ContainsKey(id)) continue;
            int playerClass = TfwMarineClass(classRole);
            var state = Spawn(id, playerClass, primaryChoice: Config.Classes[playerClass].PrimaryChoices.Count > 0 ? 0 : -1, team: Team.SpaceMarines);   // a prepared config has no default primary
            _players[id] = state with { Position = at, Yaw = yaw };
            var marine = new Marine(id, role, owner, lane, name) { HoldAt = at };
            run.AddMarine(marine);
            run.PuppetBrain ??= TfwMarineInput;
            return marine;
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

    /// <summary>Takes a puppet out of the world (a battle ends; its recovery, if any, runs on).</summary>
    public bool TfwDismissMarine(int id)
    {
        if (_tfw?.MarineOf(id) is not { } marine) return false;
        _tfw.RemoveMarine(marine);
        Remove(id);
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
    /// Applies a player's order to its retainers now: orders reach only the retainers that player owns. False for a resend, an old sequence or a command that is no order. A Hold holds where
    /// the retainer stands at that moment.
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

    /// <summary>The puppets' tick, after the fight (<c>TfwGuardsStep</c>, in <see cref="TfwStep"/>): the orders that came in, the recoveries, each puppet's death, ammunition and revive.</summary>
    void TfwPuppetsStep()
    {
        var run = _tfw!;
        TfwTakeOrders(run);
        float dt = 1f / Math.Max(1, Config.TickRate);
        run.StepRecovery(dt);
        if (run.Marines.Count == 0) return;
        _tfwMarineWork.Clear();
        _tfwMarineWork.AddRange(run.Marines);
        foreach (var m in _tfwMarineWork)
        {
            if (!_players.TryGetValue(m.Id, out var p)) { run.RemoveMarine(m); continue; }
            bool ownerHere = _players.TryGetValue(m.Owner, out var owner);
            if (m.Role == MarineRole.Retainer && !ownerHere) { TfwDismissMarine(m.Id); continue; }   // its player left
            if (!p.Alive) { TfwMarineDown(run, m, p); continue; }
            m.Down = false;
            TfwMarineAmmunition(p);
            if (m.Role != MarineRole.Retainer || owner.Alive || !run.ReviveWanted(m.Owner)
                || MarineBrain.Flat(_players[m.Id].Position, owner.Position) > MarineRules.ReviveRadius) { m.ReviveTicks = 0; continue; }
            if (++m.ReviveTicks < MarineRules.ReviveSeconds * Config.TickRate) continue;
            m.ReviveTicks = 0;
            TfwRevive(m.Owner);
        }
    }

    /// <summary>Follow, Hold and Attack out of the command queue; the run's other commands stay in it, in their order, for TF4.</summary>
    void TfwTakeOrders(TfwRun run)
    {
        for (int left = run.QueuedCommands; left > 0 && run.TryDequeue(out int player, out var command); left--)
        {
            if (command.Command is TfwCommand.Follow or TfwCommand.Hold or TfwCommand.Attack) TfwOrderMarines(player, command);
            else run.Enqueue(player, command);
        }
    }

    /// <summary>A dead puppet: its fall once (a lord's goes to the war, a retainer starts recovering), its respawn timer held, and a recovered retainer back at the landing zone.</summary>
    void TfwMarineDown(TfwRun run, Marine m, in PlayerState p)
    {
        m.ReviveTicks = 0;
        m.Target = 0;
        int recovery = m.Role == MarineRole.Lord ? 0 : m.Owner;
        if (!m.Down)
        {
            m.Down = true;
            if (m.Role == MarineRole.Lord) run.LordFallen?.Invoke(m.Lane);
            else run.StartRecovery(recovery);
        }
        else if (m.Role != MarineRole.Lord && run.RecoveryLeft(recovery) <= 0f)
        {
            var landing = run.Battle?.Layout.LandingZone ?? run.Field.MarineStart;
            TfwRespawnAt(m.Id, landing, 0f);
            m.HoldAt = landing;   // its post fell with it
            m.Down = false;
            return;
        }
        _players[m.Id] = _players[m.Id] with { RespawnLeft = MarineRules.DeadHold };
    }

    /// <summary>A puppet's ammunition does not run out (ours: it has no pickups to look for): an empty reserve fills, and a weapon that cannot reload (a brace weapon) its clip.</summary>
    void TfwMarineAmmunition(in PlayerState p)
    {
        if (Loadouts.InHand(Config, p) is not { } weapon) return;
        if (p.Reserve <= 0) _players[p.PlayerId] = p with { Reserve = weapon.ReserveSize };
        if (p.Clip <= 0 && weapon.Brace is not null) _players[p.PlayerId] = _players[p.PlayerId] with { Clip = weapon.ClipSize };
    }

    /// <summary>A puppet's input this tick (<see cref="TfwRun.PuppetBrain"/>): the order in force, the enemy it looks at every <see cref="MarineRules.RetargetTicks"/> ticks, then <see cref="MarineBrain"/>.</summary>
    PlayerInput TfwMarineInput(int id, PlayerState self)
    {
        var standing = new PlayerInput(0, 0f, 0f, self.Yaw, InputButtons.None);
        var run = _tfw!;
        if (!self.Alive || run.MarineOf(id) is not { } m) return standing;

        Vector3? ownerAt = null;
        bool ownerAlive = false;
        if (_players.TryGetValue(m.Owner, out var owner)) { ownerAt = owner.Position; ownerAlive = owner.Alive; }
        var order = m.Role switch
        {
            MarineRole.Lord => MarineOrder.Attack,
            MarineRole.LordRetainer => ownerAlive ? MarineOrder.Follow : MarineOrder.Attack,
            _ => run.Orders.Of(m.Owner),
        };
        bool revive = m.Role == MarineRole.Retainer && ownerAt is not null && !ownerAlive && run.ReviveWanted(m.Owner);

        var musou = _musou!;
        bool struck = m.Target != 0 && musou.TryGet(m.Target, out var held) && held.State != MusouEnemyState.Dying;
        if (!struck || (Tick + (uint)id) % MarineRules.RetargetTicks == 0)
        {
            var (centre, radius) = order switch
            {
                MarineOrder.Follow => (self.Position, MarineRules.FollowEngage),
                MarineOrder.Hold => (m.HoldAt, MarineRules.HoldRadius),
                _ => (self.Position, MarineRules.AttackSight),
            };
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
        var objective = m.AttackAt ?? run.Battle?.GoalFrom(self.Position) ?? run.ActivePosition;
        var goal = MarineBrain.Plan(new MarineSituation(order, self.Position, ownerAt, revive, m.HoldAt, objective, enemyAt, strikesOnly));
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
