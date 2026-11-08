using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>The AI Marines' numbers (design section E; every value ours or an estimate, in spec/behavior.md and the refine register). Distances in metres, flat.</summary>
public static class MarineRules
{
    /// <summary>A puppet looks for its target every this many ticks.</summary>
    public const int RetargetTicks = 3;
    /// <summary>The aim's error, each way, on yaw and on pitch (degrees).</summary>
    public const float AimErrorDegrees = 2f;
    /// <summary>An enemy this near is struck, not shot.</summary>
    public const float MeleeRange = 2.5f;

    /// <summary>A retainer's Follow keeps its owner within this band (the design's 4 to 8 m).</summary>
    public const float FollowMin = 4f, FollowMax = 8f;
    /// <summary>Where a retainer's Follow steers: it stands between these, inside the band, so a moving owner and a stopping puppet stay within it.</summary>
    public const float FollowStandNear = 4.5f, FollowStandFar = 6.5f;
    /// <summary>The pair's follower and a following retainer shoot enemies within this of themselves, without leaving whom they follow.</summary>
    public const float FollowEngage = 20f;
    /// <summary>A retainer's Hold shoots enemies within this of the place it holds.</summary>
    public const float HoldRadius = 25f;
    /// <summary>How far from the held place a retainer stands.</summary>
    public const float HoldStand = 1f;
    /// <summary>The pair's leader and an attacking retainer see enemies this far.</summary>
    public const float AttackSight = 40f;
    /// <summary>Within this of its goal the leader has arrived and turns on the enemies.</summary>
    public const float ArriveRadius = 8f;
    /// <summary>A puppet with a primary closes on an enemy to this far; one without closes to strike.</summary>
    public const float GunStandOff = 12f, StrikeStandOff = 1.5f;
    /// <summary>A puppet without a primary leaves its way to strike an enemy within this.</summary>
    public const float StrikeChase = 10f;
    /// <summary>A puppet takes up a new way when its goal has moved this far from the last (a follower behind a walking leader).</summary>
    public const float WayRefresh = 5f;
    /// <summary>A puppet's target is the nearest of this many enemies in its sight.</summary>
    public const int SightCandidates = 4;
    /// <summary>A puppet sprints when it has this far to go and nobody to shoot.</summary>
    public const float SprintBeyond = 12f;

    /// <summary>The revive: a retainer (its owner) or the pair's leader (the nearest fallen human) steers to this far from the body, and within <see cref="ReviveRadius"/> holds it for <see cref="ReviveSeconds"/>.</summary>
    public const float ReviveApproach = 1.5f, ReviveRadius = 2f, ReviveSeconds = 3f;
    /// <summary>A revived player comes back with this share of its health and shield.</summary>
    public const float ReviveShare = 0.5f;
    /// <summary>A retainer dead in battle recovers for this many war seconds (3 war-minutes).</summary>
    public const float RetainerRecoverySeconds = 180f;
    /// <summary>The respawn timer a dead puppet is kept at: only its own rule (a retainer's recovery, the pair's respawn) brings one back.</summary>
    public const float DeadHold = 1f;
}

/// <summary>
/// The allied AI Marines of a Musou battle (owner 2026-11-08: "nerf all non-player allied space marines. reduce their total count to 2, and they roam the map together attacking and defending"):
/// two, whatever the number of players, landed at the landing zone, a leader and a follower. The count is the owner's and the 30 s respawn expansion S6's Defender rule, kept; every other value is
/// ours (refine register). They replace the lane lords and their retainers and expansion S6's six Defenders; each human's own retainer is back beside them (owner 2026-11-08: "return the player
/// retainer bots with orders as well"). A change here raises <see cref="TfwRules.Version"/>.
/// </summary>
public static class AllyRules
{
    /// <summary>The allied Marines a battle lands (the owner), and the seconds a dead one waits before he is back at the landing zone.</summary>
    public const int Count = 2;
    public const float RespawnSeconds = 30f;
    /// <summary>The co-op's humans at most (SmContent's <c>MusouLocal.MaxPlayers</c>): a retainer each and the pair are six puppets, well inside the World's 16 players and the puppet id range.</summary>
    public const int HumansMost = 4;
    /// <summary>The pair's goal is looked at this often.</summary>
    public const float GoalSeconds = 1f;
    /// <summary>A secured position's threat counts the Orks this near it (up to <see cref="ThreatMost"/>) and every man of an Ork army on its way to it or attacking it.</summary>
    public const float ThreatRadius = 40f;
    public const int ThreatMost = 64;
    /// <summary>A secured position with this threat is defended (the pair leaves its attack for it); the one it defends is kept while the threat stays at half of this.</summary>
    public const float DefendAt = 8f;
    /// <summary>The follower stands this far from its leader (the owner's "together": within a few metres), and sprints after him beyond <see cref="FollowSprint"/>.</summary>
    public const float FollowNear = 2f, FollowFar = 4f, FollowSprint = 6f;
    /// <summary>The leader waits (fighting where he stands) while his follower is farther than this, and never sprints while his follower lives.</summary>
    public const float LeaderWait = 8f;
    /// <summary>The pair's classes and placeholder names, in landing order (the leader first; labelled so, as the lords' were).</summary>
    public static readonly (string Role, string Name)[] Roster = [("tactical", "Brother Varro"), ("heavy", "Brother Calix")];
}

/// <summary>A player's order to its retainer (the allied pair takes none).</summary>
public enum MarineOrder : byte { Follow = 0, Hold = 1, Attack = 2 }

/// <summary>What a puppet is: a player's retainer (takes that player's orders, revives it, recovers 180 war seconds after a death), or a Marine of the allied pair (autonomous, back in 30 s).</summary>
public enum MarineRole : byte { Retainer = 0, Ally = 1 }

/// <summary>One AI Marine (a real World player, <see cref="TfwContracts.PuppetIdFirst"/> up): what the brain remembers between ticks.</summary>
public sealed class Marine(int id, int slot, string name, MarineRole role = MarineRole.Ally, int owner = 0)
{
    public int Id { get; } = id;
    /// <summary>A Marine of the pair or a retainer.</summary>
    public MarineRole Role { get; } = role;
    /// <summary>A retainer's owner (a human's id); 0 for the pair.</summary>
    public int Owner { get; } = owner;
    /// <summary>Its place in the pair's roster (0 or 1; -1 a retainer).</summary>
    public int Slot { get; } = slot;
    /// <summary>The place a retainer's Hold holds.</summary>
    public Vector3 HoldAt { get; set; }
    /// <summary>Where its player's Attack sent a retainer (null: the battle's goal from where it stands).</summary>
    public Vector3? AttackAt { get; set; }
    /// <summary>The scoreboard name.</summary>
    public string Name { get; } = name;
    /// <summary>Whether it leads the pair (the other follows it): a lone survivor leads, and one back from the dead follows a living partner.</summary>
    public bool Leads { get; set; }
    /// <summary>The graph node and the place its way leads to (-1: none yet).</summary>
    public int WayGoal { get; set; } = -1;
    public Vector3 WayPoint { get; set; }
    /// <summary>The enemy it is after (0: none).</summary>
    public ushort Target { get; set; }
    /// <summary>The fallen human it goes to revive (0: none), and its ticks in a row within reach of the body.</summary>
    public int ReviveOf { get; set; }
    public int ReviveTicks { get; set; }
    /// <summary>Dead, and its death already dealt with; ticks until it is back.</summary>
    public bool Down { get; set; }
    public int RespawnTicks { get; set; }
}

/// <summary>Where a puppet wants to be: within <see cref="Near"/> to <see cref="Far"/> of <see cref="Point"/>, flat (it stands inside the band, and backs off under it). <see cref="Via"/>: the next place on the way there (the walking graph's; none: straight).</summary>
public readonly record struct MarineGoal(Vector3 Point, float Near, float Far, bool Sprint, Vector3? Via = null);

/// <summary>
/// What <see cref="MarineBrain.Plan"/> weighs: the leader to keep to (a follower; null: it leads), a body to revive, the pair's goal, the enemy, whether the puppet has no primary, and whether a leader
/// may sprint (only alone).
/// </summary>
public readonly record struct MarineSituation(Vector3 Self, Vector3? Leader, Vector3? Revive, Vector3? Objective, Vector3? Enemy, bool StrikesOnly, bool MaySprint = true);

/// <summary>What <see cref="MarineBrain.PlanRetainer"/> weighs (a retainer, as before the pair): the order in force, the places, whether to revive its owner, the enemy, and whether it has no primary.</summary>
public readonly record struct RetainerSituation(MarineOrder Order, Vector3 Self, Vector3? Owner, bool Revive, Vector3 HoldAt, Vector3? Objective, Vector3? Enemy, bool StrikesOnly);

/// <summary>
/// The AI Marines' decisions (design section E): pure and deterministic. <see cref="Plan"/> picks where to go, <see cref="Decide"/> turns that and the enemy into a tick's input, the same
/// input a client sends (no shortcut: the core moves it, fires it and limits it as anyone's). A follower keeps to its leader; a leader revives first, then goes to the pair's goal.
/// </summary>
public static class MarineBrain
{
    /// <summary>Where the puppet goes this tick; null: it stays.</summary>
    public static MarineGoal? Plan(in MarineSituation s)
    {
        if (s.Leader is { } leader)
            return new MarineGoal(leader, AllyRules.FollowNear, AllyRules.FollowFar, s.Enemy is null && Flat(s.Self, leader) > AllyRules.FollowSprint);
        if (s.Revive is { } body)
            return new MarineGoal(body, 0f, MarineRules.ReviveApproach, s.MaySprint && Flat(s.Self, body) > MarineRules.SprintBeyond);
        if (s.StrikesOnly && s.Enemy is { } near && Flat(s.Self, near) <= MarineRules.StrikeChase) return Engage(near, true);
        if (s.Objective is { } goal && Flat(s.Self, goal) > MarineRules.ArriveRadius)
            return new MarineGoal(goal, 0f, MarineRules.ArriveRadius * 0.75f, s.MaySprint && s.Enemy is null && Flat(s.Self, goal) > MarineRules.SprintBeyond);
        return s.Enemy is { } enemy ? Engage(enemy, s.StrikesOnly) : null;
    }

    /// <summary>Where a retainer goes this tick (its owner's order; a revive of its owner comes before any order); null: it stays.</summary>
    public static MarineGoal? PlanRetainer(in RetainerSituation s)
    {
        if (s.Revive && s.Owner is { } body)
            return new MarineGoal(body, 0f, MarineRules.ReviveApproach, Flat(s.Self, body) > MarineRules.SprintBeyond);
        switch (s.Order)
        {
            case MarineOrder.Follow when s.Owner is { } owner:
                return new MarineGoal(owner, MarineRules.FollowStandNear, MarineRules.FollowStandFar, s.Enemy is null && Flat(s.Self, owner) > MarineRules.SprintBeyond);
            case MarineOrder.Hold:
                return new MarineGoal(s.HoldAt, 0f, MarineRules.HoldStand, s.Enemy is null && Flat(s.Self, s.HoldAt) > MarineRules.SprintBeyond);
            case MarineOrder.Attack:
                if (s.StrikesOnly && s.Enemy is { } near && Flat(s.Self, near) <= MarineRules.StrikeChase) return Engage(near, true);
                if (s.Objective is { } goal && Flat(s.Self, goal) > MarineRules.ArriveRadius)
                    return new MarineGoal(goal, 0f, MarineRules.ArriveRadius * 0.75f, s.Enemy is null && Flat(s.Self, goal) > MarineRules.SprintBeyond);
                return s.Enemy is { } enemy ? Engage(enemy, s.StrikesOnly) : null;
            default:
                return null;
        }
    }

    static MarineGoal Engage(Vector3 enemy, bool strikesOnly) => new(enemy, 0f, strikesOnly ? MarineRules.StrikeStandOff : MarineRules.GunStandOff, false);

    /// <summary>
    /// The tick's input. <paramref name="aimAt"/> is the enemy's chest and <paramref name="enemyDistance"/> its flat distance: within <see cref="MarineRules.MeleeRange"/> it strikes, else
    /// it holds the trigger while the clip lasts (a semi-automatic or charged weapon by repeated presses), then reloads; a sprint fires nothing, so a sprinting puppet does not aim.
    /// A weapon with a brace never presses Reload (there it changes stance); an overheated one waits.
    /// </summary>
    public static PlayerInput Decide(in PlayerState self, WeaponSpec? weapon, MarineGoal? goal, Vector3? aimAt, float enemyDistance, float eyeHeight, uint tick)
    {
        Vector2 move = Vector2.Zero;
        bool sprint = false;
        if (goal is { } g)
        {
            var to = new Vector2(g.Point.X - self.Position.X, g.Point.Y - self.Position.Y);
            float d = to.Length();
            if (d > g.Far && d > 1e-4f)
            {
                var way = g.Via is { } via ? new Vector2(via.X - self.Position.X, via.Y - self.Position.Y) : to;
                move = way.LengthSquared() > 1e-6f ? Vector2.Normalize(way) : to / d;
                sprint = g.Sprint;
            }
            else if (d < g.Near && d > 1e-4f) move = -to / d;
        }

        float yaw = self.Yaw, pitch = 0f;
        var buttons = InputButtons.None;
        if (aimAt is { } aim && !sprint)
        {
            var eye = self.Position + new Vector3(0f, 0f, eyeHeight);
            var to = aim - eye;
            float flat = new Vector2(to.X, to.Y).Length();
            yaw = YawOf(to.X, to.Y) + AimError(self.PlayerId, tick, 0u);
            pitch = MathF.Atan2(to.Z, flat) + AimError(self.PlayerId, tick, 1u);
            if (enemyDistance <= MarineRules.MeleeRange) { if (!self.MeleeHeld) buttons |= InputButtons.Melee; }
            else if (weapon is not null) buttons |= Trigger(self, weapon, tick);
        }
        else if (move != Vector2.Zero) yaw = YawOf(move.X, move.Y);

        if (sprint) buttons |= InputButtons.Sprint;
        float sin = MathF.Sin(yaw), cos = MathF.Cos(yaw);
        float forward = move.X * -sin + move.Y * cos, right = move.X * cos + move.Y * sin;
        return new PlayerInput(tick, right, forward, yaw, buttons, pitch);
    }

    static InputButtons Trigger(in PlayerState self, WeaponSpec weapon, uint tick)
    {
        if (self.ReloadLeft > 0f || self.Overheated) return InputButtons.None;
        if (self.Clip <= 0) return weapon.Brace is null && (tick & 1u) == 0u ? InputButtons.Reload : InputButtons.None;   // a press edge every other tick
        bool held = weapon.Trigger == TriggerKind.FullAuto && weapon.Charge is null;
        return held || !self.FireHeld ? InputButtons.Fire : InputButtons.None;
    }

    /// <summary>The yaw that looks along a flat direction (the core's: forward is (-sin yaw, cos yaw)).</summary>
    public static float YawOf(float x, float y) => MathF.Atan2(-x, y);

    /// <summary>A deterministic aim error for a puppet and tick, within ±<see cref="MarineRules.AimErrorDegrees"/> (radians).</summary>
    public static float AimError(int id, uint tick, uint axis)
    {
        uint h = unchecked(((uint)id * 2654435761u) ^ (tick * 2246822519u) ^ (axis * 3266489917u));
        h ^= h >> 15; h = unchecked(h * 2246822519u); h ^= h >> 13; h = unchecked(h * 3266489917u); h ^= h >> 16;
        return ((h >> 8) / 8388607.5f - 1f) * MarineRules.AimErrorDegrees * MathF.PI / 180f;
    }

    /// <summary>The flat distance of two places.</summary>
    public static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();
}

/// <summary>A player's orders to its retainer (TF5): the last order per player, deduplicated by the command's sequence (a client resends until echoed).</summary>
public sealed class MarineOrders
{
    readonly Dictionary<int, (MarineOrder Order, ushort Sequence)> _orders = new();

    /// <summary>The order in force for <paramref name="player"/>'s retainer (Follow until told otherwise).</summary>
    public MarineOrder Of(int player) => _orders.TryGetValue(player, out var o) ? o.Order : MarineOrder.Follow;

    /// <summary>The newest order sequence applied for <paramref name="player"/>, 0 before any.</summary>
    public ushort Echo(int player) => _orders.TryGetValue(player, out var o) ? o.Sequence : (ushort)0;

    /// <summary>Applies an order; false for a command that is no order, or a resend or older sequence (wrapping 16-bit).</summary>
    public bool Apply(int player, TfwCommandRecord command)
    {
        MarineOrder? order = command.Command switch
        {
            TfwCommand.Follow => MarineOrder.Follow,
            TfwCommand.Hold => MarineOrder.Hold,
            TfwCommand.Attack => MarineOrder.Attack,
            _ => null,
        };
        if (order is not { } next) return false;
        if (_orders.TryGetValue(player, out var was) && (short)(command.Sequence - was.Sequence) <= 0) return false;
        _orders[player] = (next, command.Sequence);
        return true;
    }

    /// <summary>Forgets every order (a new run).</summary>
    public void Clear() => _orders.Clear();
}

/// <summary>The AI Marines' part of the run (TF5). The run's own milestone (TF4) meets it here: it opens the revive window, names the active position and sets the war's time scale.</summary>
public sealed partial class TfwRun
{
    readonly List<Marine> _marines = new();
    readonly List<Marine> _allies = new();
    readonly Dictionary<int, float> _recovery = new();
    readonly List<int> _recovered = new();

    /// <summary>The AI Marines in the world (the players' retainers and the allied pair), in id order.</summary>
    public IReadOnlyList<Marine> Marines => _marines;

    /// <summary>The allied pair alone (<see cref="MarineRole.Ally"/>), in id order.</summary>
    public IReadOnlyList<Marine> Pair => _allies;

    /// <summary>The players' orders to their retainers.</summary>
    public MarineOrders Orders { get; } = new();

    /// <summary>The war's time scale, which a retainer's recovery runs on (TF4 sets it from <see cref="WarSim.TimeScale"/>).</summary>
    public float WarTimeScale { get; set; } = 1f;

    /// <summary>The battle's active position (TF4 sets it): where the pair goes outside a battle's own choice (tests; null: they fight what they see).</summary>
    public Vector3? ActivePosition { get; set; }

    /// <summary>The pair's goal now (the World looks at it every <see cref="AllyRules.GoalSeconds"/>): a battle's position, its HQ or landing zone, else <see cref="ActivePosition"/>.</summary>
    public Vector3? AllyGoal { get; internal set; }
    /// <summary>The battle's index of that goal (<see cref="TfwBattle.AllyGoal"/>; -1 the landing zone, -2 none yet).</summary>
    public int AllyGoalIndex { get; internal set; } = -2;

    /// <summary>Whether a dead player's revive window is open (TF4: it holds the respawn and times the window). Null: never.</summary>
    public Func<int, bool>? ReviveWindow { get; set; }

    /// <summary>Whether <paramref name="player"/>, dead, waits for the pair.</summary>
    public bool ReviveWanted(int player) => ReviveWindow?.Invoke(player) ?? false;

    /// <summary>How far the revive of <paramref name="player"/> has come, 0 to 1 (the HUD's prompt).</summary>
    public float ReviveProgress(int player, int tickRate)
    {
        float best = 0f;
        foreach (var m in _marines)
            if (m.ReviveOf == player) best = MathF.Max(best, Math.Clamp(m.ReviveTicks / (MarineRules.ReviveSeconds * Math.Max(1, tickRate)), 0f, 1f));
        return best;
    }

    /// <summary>The puppet of <paramref name="id"/>, if it is a registered AI Marine.</summary>
    public Marine? MarineOf(int id)
    {
        foreach (var m in _marines) if (m.Id == id) return m;
        return null;
    }

    /// <summary>The other Marine of the pair (null for a retainer).</summary>
    public Marine? PartnerOf(Marine marine)
    {
        if (marine.Role != MarineRole.Ally) return null;
        foreach (var m in _allies) if (!ReferenceEquals(m, marine)) return m;
        return null;
    }

    /// <summary><paramref name="player"/>'s retainer in the world, alive or dead.</summary>
    public Marine? RetainerOf(int player)
    {
        foreach (var m in _marines) if (m.Role == MarineRole.Retainer && m.Owner == player) return m;
        return null;
    }

    /// <summary>War seconds until the retainer of <paramref name="owner"/> is back; 0: it is not recovering.</summary>
    public float RecoveryLeft(int owner) => _recovery.GetValueOrDefault(owner);

    internal void AddMarine(Marine marine)
    {
        Insert(_marines, marine);
        if (marine.Role == MarineRole.Ally) Insert(_allies, marine);
    }

    static void Insert(List<Marine> into, Marine marine)
    {
        int at = 0;
        while (at < into.Count && into[at].Id < marine.Id) at++;
        into.Insert(at, marine);
    }

    internal bool RemoveMarine(Marine marine)
    {
        _allies.Remove(marine);
        return _marines.Remove(marine);
    }

    internal void StartRecovery(int owner) => _recovery[owner] = MarineRules.RetainerRecoverySeconds;

    internal void ForgetMarines()
    {
        _marines.Clear();
        _allies.Clear();
        _recovery.Clear();
        Orders.Clear();
        AllyGoal = null;
        AllyGoalIndex = -2;
    }

    /// <summary>One tick of the retainers' recoveries, on the war clock; they run whether or not a battle is on.</summary>
    internal void StepRecovery(float dt)
    {
        if (_recovery.Count == 0) return;
        float war = dt * WarTimeScale;
        _recovered.Clear();
        foreach (int owner in _recovery.Keys) _recovered.Add(owner);
        foreach (int owner in _recovered)
        {
            float left = _recovery[owner] - war;
            if (left > 0f) _recovery[owner] = left;
            else _recovery.Remove(owner);
        }
    }

    /// <summary>An AI Marine's name, a placeholder number for an id nobody registered.</summary>
    string MarineName(int id) => MarineOf(id)?.Name ?? $"Marine {id - TfwContracts.PuppetIdFirst + 1}";
}
