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

    /// <summary>Follow keeps the owner within this band (the design's 4 to 8 m).</summary>
    public const float FollowMin = 4f, FollowMax = 8f;
    /// <summary>Where Follow steers: it stands between these, inside the band, so a moving owner and a stopping puppet stay within it.</summary>
    public const float FollowStandNear = 4.5f, FollowStandFar = 6.5f;
    /// <summary>Follow shoots enemies within this of the puppet, without leaving the owner.</summary>
    public const float FollowEngage = 20f;
    /// <summary>Hold shoots enemies within this of the place it holds.</summary>
    public const float HoldRadius = 25f;
    /// <summary>How far from the held place a puppet stands.</summary>
    public const float HoldStand = 1f;
    /// <summary>Attack, and the lords, see enemies this far.</summary>
    public const float AttackSight = 40f;
    /// <summary>Within this of the active position a puppet has arrived and turns on the enemies.</summary>
    public const float ArriveRadius = 8f;
    /// <summary>A puppet with a primary closes on an enemy to this far; one without closes to strike.</summary>
    public const float GunStandOff = 12f, StrikeStandOff = 1.5f;
    /// <summary>A puppet without a primary leaves its way to strike an enemy within this.</summary>
    public const float StrikeChase = 10f;
    /// <summary>A puppet takes up a new way when its goal has moved this far from the last (a follower behind a walking owner).</summary>
    public const float WayRefresh = 5f;
    /// <summary>A puppet's target is the nearest of this many enemies in its sight.</summary>
    public const int SightCandidates = 4;
    /// <summary>A puppet sprints when it has this far to go and nobody to shoot.</summary>
    public const float SprintBeyond = 12f;

    /// <summary>The revive: the retainer steers to this far from the body, and within <see cref="ReviveRadius"/> holds it for <see cref="ReviveSeconds"/>.</summary>
    public const float ReviveApproach = 1.5f, ReviveRadius = 2f, ReviveSeconds = 3f;
    /// <summary>The window a dead player waits in with a retainer alive (TF4 opens it, <see cref="TfwRun.ReviveWindow"/>).</summary>
    public const float ReviveWindowSeconds = 25f;
    /// <summary>A revived player comes back with this share of its health and shield.</summary>
    public const float ReviveShare = 0.5f;
    /// <summary>A retainer dead in battle recovers for this many war seconds (3 war-minutes).</summary>
    public const float RetainerRecoverySeconds = 180f;
    /// <summary>The respawn timer a dead puppet is kept at: only this file brings one back.</summary>
    public const float DeadHold = 1f;

    /// <summary>The puppets a node battle and the final assault hold (the player's retainer, then a lord and his retainer per lane in play).</summary>
    public const int MaxInBattle = 3, MaxInFinal = 7;
}

/// <summary>A player's order to its retainers.</summary>
public enum MarineOrder : byte { Follow = 0, Hold = 1, Attack = 2 }

/// <summary>What a puppet is: a player's retainer, a lane's lord (autonomous), or a lord's retainer (his alone: no player orders it).</summary>
public enum MarineRole : byte { Retainer = 0, Lord = 1, LordRetainer = 2 }

/// <summary>One AI Marine (a real World player, <see cref="TfwContracts.PuppetIdFirst"/> up): what the brain remembers between ticks.</summary>
public sealed class Marine(int id, MarineRole role, int owner, TfwLane lane, string name)
{
    public int Id { get; } = id;
    public MarineRole Role { get; } = role;
    /// <summary>A retainer's owner (a human's id, or its lord's); a lord has none (0).</summary>
    public int Owner { get; } = owner;
    /// <summary>A lord's lane, and his retainer's.</summary>
    public TfwLane Lane { get; } = lane;
    /// <summary>The scoreboard name.</summary>
    public string Name { get; } = name;
    /// <summary>The place a Hold order holds.</summary>
    public Vector3 HoldAt { get; set; }
    /// <summary>Where its player's Attack sent it (null: the battle's active position).</summary>
    public Vector3? AttackAt { get; set; }
    /// <summary>The graph node and the place its way leads to (-1: none yet).</summary>
    public int WayGoal { get; set; } = -1;
    public Vector3 WayPoint { get; set; }
    /// <summary>The enemy it is after (0: none).</summary>
    public ushort Target { get; set; }
    /// <summary>Ticks in a row within reach of the body it revives.</summary>
    public int ReviveTicks { get; set; }
    /// <summary>Dead, and its death already dealt with.</summary>
    public bool Down { get; set; }
}

/// <summary>Where a puppet wants to be: within <see cref="Near"/> to <see cref="Far"/> of <see cref="Point"/>, flat (it stands inside the band, and backs off under it). <see cref="Via"/>: the next place on the way there (the walking graph's; none: straight).</summary>
public readonly record struct MarineGoal(Vector3 Point, float Near, float Far, bool Sprint, Vector3? Via = null);

/// <summary>What <see cref="MarineBrain.Plan"/> weighs: the order in force, the places, a body to revive, the enemy, and whether the puppet has no primary.</summary>
public readonly record struct MarineSituation(MarineOrder Order, Vector3 Self, Vector3? Owner, bool Revive, Vector3 HoldAt, Vector3? Objective, Vector3? Enemy, bool StrikesOnly);

/// <summary>
/// The AI Marines' decisions (design section E): pure and deterministic. <see cref="Plan"/> picks where to go, <see cref="Decide"/> turns that and the enemy into a tick's input, the same
/// input a client sends (no shortcut: the core moves it, fires it and limits it as anyone's). A revive comes before any order.
/// </summary>
public static class MarineBrain
{
    /// <summary>Where the puppet goes this tick; null: it stays.</summary>
    public static MarineGoal? Plan(in MarineSituation s)
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

/// <summary>A player's orders to its retainers (TF5): the last order per player, deduplicated by the command's sequence (a client resends until echoed).</summary>
public sealed class MarineOrders
{
    readonly Dictionary<int, (MarineOrder Order, ushort Sequence)> _orders = new();

    /// <summary>The order in force for <paramref name="player"/>'s retainers (Follow until told otherwise).</summary>
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

/// <summary>The AI Marines' part of the run (TF5). The run's own milestone (TF4) meets it here: it opens the revive window, names the active position, sets the war's time scale and hears of a fallen lord.</summary>
public sealed partial class TfwRun
{
    readonly List<Marine> _marines = new();
    readonly Dictionary<int, float> _recovery = new();
    readonly List<int> _recovered = new();

    /// <summary>The AI Marines in the world, in id order.</summary>
    public IReadOnlyList<Marine> Marines => _marines;

    /// <summary>The players' orders to their retainers.</summary>
    public MarineOrders Orders { get; } = new();

    /// <summary>Where the lords and an attacking retainer go: the battle's active position, then the commander (TF4 sets it; null: they fight what they see).</summary>
    public Vector3? ActivePosition { get; set; }

    /// <summary>The war's time scale, which a retainer's recovery runs on (TF4 sets it from <see cref="WarSim.TimeScale"/>).</summary>
    public float WarTimeScale { get; set; } = 1f;

    /// <summary>Whether a dead player's revive window is open (TF4: it holds the respawn and times the <see cref="MarineRules.ReviveWindowSeconds"/>). Null: never.</summary>
    public Func<int, bool>? ReviveWindow { get; set; }

    /// <summary>A lord fell in battle (TF4: <see cref="WarSim.WoundLord"/>).</summary>
    public Action<TfwLane>? LordFallen { get; set; }

    /// <summary>Whether <paramref name="player"/>, dead, waits for its retainer.</summary>
    public bool ReviveWanted(int player) => ReviveWindow?.Invoke(player) ?? false;

    /// <summary>How far the revive of <paramref name="player"/> has come, 0 to 1 (the HUD's prompt).</summary>
    public float ReviveProgress(int player, int tickRate)
    {
        foreach (var m in _marines)
            if (m.Role == MarineRole.Retainer && m.Owner == player) return Math.Clamp(m.ReviveTicks / (MarineRules.ReviveSeconds * Math.Max(1, tickRate)), 0f, 1f);
        return 0f;
    }

    /// <summary>The puppet of <paramref name="id"/>, if it is a registered AI Marine.</summary>
    public Marine? MarineOf(int id)
    {
        foreach (var m in _marines) if (m.Id == id) return m;
        return null;
    }

    /// <summary><paramref name="player"/>'s retainer in the world, alive or dead.</summary>
    public Marine? RetainerOf(int player)
    {
        foreach (var m in _marines) if (m.Role == MarineRole.Retainer && m.Owner == player) return m;
        return null;
    }

    /// <summary>The lord of <paramref name="lane"/> in the world (one lord per lane, the owner 2026-11-01).</summary>
    public Marine? LordOf(TfwLane lane)
    {
        foreach (var m in _marines) if (m.Role == MarineRole.Lord && m.Lane == lane) return m;
        return null;
    }

    /// <summary>War seconds until the retainer of <paramref name="owner"/> is back; 0: it is not recovering.</summary>
    public float RecoveryLeft(int owner) => _recovery.GetValueOrDefault(owner);

    internal void AddMarine(Marine marine)
    {
        int at = 0;
        while (at < _marines.Count && _marines[at].Id < marine.Id) at++;
        _marines.Insert(at, marine);
    }

    internal bool RemoveMarine(Marine marine) => _marines.Remove(marine);

    internal void StartRecovery(int owner) => _recovery[owner] = MarineRules.RetainerRecoverySeconds;

    internal void ForgetMarines()
    {
        _marines.Clear();
        _recovery.Clear();
        Orders.Clear();
    }

    /// <summary>One tick of the recoveries, on the war clock; they run whether or not a battle is on.</summary>
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
