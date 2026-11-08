using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// The AI Marines in the World (TF5; design section E; the owner's pair, 2026-11-08): two real players without a peer, who act only by the input <see cref="MarineBrain"/> makes
/// (<see cref="TfwMarineInput"/>, through <see cref="TfwRun.PuppetBrain"/>). They land at the landing zone and roam together: the leader goes to the pair's goal (<see cref="TfwBattle.AllyGoal"/>),
/// reviving a fallen human on the way, and the follower keeps a few metres behind him. No player orders them. A dead one is back at the landing zone <see cref="AllyRules.RespawnSeconds"/> later.
/// </summary>
public sealed partial class World
{
    /// <summary>The lords' places by lane and their placeholder names (open question 7, labelled so): the war map still names a lane's lord; no lord fights in a battle since the owner's pair.</summary>
    static readonly (string Role, string Name)[] TfwLords = [("assault", "Aurex"), ("tactical", "Tolmar"), ("heavy", "Severin")];

    readonly List<Marine> _tfwMarineWork = new();

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
        if (_tfw is not { } run || run.Marines.Count >= AllyRules.Count) return null;
        int slot = 0;
        while (slot < AllyRules.Roster.Length && run.Marines.Any(m => m.Slot == slot)) slot++;
        var (role, name) = AllyRules.Roster[slot % AllyRules.Roster.Length];
        if (ground && Musou?.Nav is { } nav && nav.Nearest(at, 1.5f, 2.5f) is var node and >= 0) at = nav.Position(node);
        for (int id = TfwContracts.PuppetIdFirst; id <= TfwContracts.PuppetIdLast; id++)
        {
            if (_players.ContainsKey(id)) continue;
            int playerClass = TfwMarineClass(role);
            var state = Spawn(id, playerClass, primaryChoice: Config.Classes[playerClass].PrimaryChoices.Count > 0 ? 0 : -1, team: Team.SpaceMarines);   // a prepared config has no default primary
            _players[id] = state with { Position = at, Yaw = yaw };
            var marine = new Marine(id, slot, name) { Leads = run.Marines.Count == 0 };
            run.AddMarine(marine);
            run.PuppetBrain ??= TfwMarineInput;
            run.Battle?.SetAllies(run.Marines.Select(m => m.Id));
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

    /// <summary>Takes a puppet out of the world (a battle ends).</summary>
    public bool TfwDismissMarine(int id)
    {
        if (_tfw?.MarineOf(id) is not { } marine) return false;
        _tfw.RemoveMarine(marine);
        Remove(id);
        if (_tfw.Marines.Count == 1) _tfw.Marines[0].Leads = true;
        return true;
    }

    /// <summary>Takes every puppet out (a battle ends).</summary>
    public void TfwDismissMarines()
    {
        if (_tfw is not { } run) return;
        _tfwMarineWork.Clear();
        _tfwMarineWork.AddRange(run.Marines);
        foreach (var m in _tfwMarineWork) TfwDismissMarine(m.Id);
    }

    /// <summary>A new run: no puppet.</summary>
    public void TfwResetMarines()
    {
        TfwDismissMarines();
        _tfw?.ForgetMarines();
    }

    /// <summary>Whether a Marine of the pair stands (a fallen human's revive window stays open while one does; <see cref="TfwRun.AllyUp"/>).</summary>
    internal bool TfwAllyUp()
    {
        if (_tfw is not { } run) return false;
        foreach (var m in run.Marines) if (_players.TryGetValue(m.Id, out var p) && p.Alive) return true;
        return false;
    }

    /// <summary>Seconds until the first dead Marine of the pair is back (0: one stands, or none is waiting).</summary>
    internal float TfwAllyBackIn()
    {
        if (_tfw is not { } run || TfwAllyUp()) return 0f;
        int least = int.MaxValue;
        foreach (var m in run.Marines) if (m.Down) least = Math.Min(least, m.RespawnTicks);
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

    /// <summary>The puppets' tick, after the fight (<c>TfwGuardsStep</c>, in <see cref="TfwStep"/>): the pair's goal, each Marine's death and return, who leads, and the revive (its ammunition: <see cref="TfwEndlessAmmo"/>, every player's since 2026-11-06).</summary>
    void TfwPuppetsStep()
    {
        var run = _tfw!;
        if (run.Marines.Count == 0) return;
        _tfwMarineWork.Clear();
        _tfwMarineWork.AddRange(run.Marines);
        foreach (var m in _tfwMarineWork)
        {
            if (!_players.TryGetValue(m.Id, out var p)) { run.RemoveMarine(m); continue; }
            if (!p.Alive) TfwMarineDown(run, m);
            else m.Down = false;
        }
        TfwAllyLeader(run);
        TfwAllyGoal(run);
        foreach (var m in run.Marines)
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

    /// <summary>Exactly one living Marine of the pair leads: a lone survivor leads, and with both up the one that led goes on leading.</summary>
    void TfwAllyLeader(TfwRun run)
    {
        Marine? leader = null, alive = null;
        foreach (var m in run.Marines)
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
        foreach (var m in run.Marines) if (m.Leads && _players.TryGetValue(m.Id, out var p)) from = p.Position;
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

    /// <summary>A puppet's input this tick (<see cref="TfwRun.PuppetBrain"/>): its part in the pair, the enemy it looks at every <see cref="MarineRules.RetargetTicks"/> ticks, then <see cref="MarineBrain"/>.</summary>
    PlayerInput TfwMarineInput(int id, PlayerState self)
    {
        var standing = new PlayerInput(0, 0f, 0f, self.Yaw, InputButtons.None);
        var run = _tfw!;
        if (!self.Alive || run.MarineOf(id) is not { } m) return standing;

        Vector3? partnerAt = null;
        if (run.PartnerOf(m) is { } partner && _players.TryGetValue(partner.Id, out var pp) && pp.Alive) partnerAt = pp.Position;
        bool follows = !m.Leads && partnerAt is not null;

        var musou = _musou!;
        bool struck = m.Target != 0 && musou.TryGet(m.Target, out var held) && held.State != MusouEnemyState.Dying;
        if (!struck || (Tick + (uint)id) % MarineRules.RetargetTicks == 0)
        {
            Span<ushort> nearest = stackalloc ushort[MarineRules.SightCandidates];
            int seen = musou.Query(self.Position, follows ? MarineRules.FollowEngage : MarineRules.AttackSight, nearest);
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
        Vector3? revive = m.ReviveOf != 0 && _players.TryGetValue(m.ReviveOf, out var fallen) && !fallen.Alive ? fallen.Position : null;
        // The leader waits for a follower left behind (he fights where he stands), and sprints only alone, so the pair keeps together.
        bool waits = !follows && partnerAt is { } behind && MarineBrain.Flat(self.Position, behind) > AllyRules.LeaderWait;
        var situation = new MarineSituation(self.Position, follows ? partnerAt : null, waits ? null : revive, waits ? null : run.AllyGoal, enemyAt, strikesOnly, MaySprint: partnerAt is null);
        var goal = MarineBrain.Plan(situation);
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
