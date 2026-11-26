using System;
using System.Collections.Generic;
using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>Why the Execute prompt is not shown (the HUD log and the tests); <see cref="None"/>: it may show.</summary>
public enum ExecutionPromptHidden : byte
{
    None, NotAlive, Spectating, Executing, Puppet, Airborne, Busy, NoFamily, OrkPlayer, NoRows,
    NotInBattle, ChaosRun, Cooldown, NoEnemyNear, NoFit, NoOffer, Throttled, Reaction, NotInPlay,
    /// <summary>X4b: the Chaos finale's relay holds the battle and the own player is not the duelist with the Lord under the duel's gate (the server refuses such a press).</summary>
    Duel,
}

/// <summary>
/// Executions X6: the HUD prompt's rules (engine-free, tested in <c>ExecutionPromptRuleTests</c>). The prompt only says what the server would do: Warfront's aligned Ork
/// (<see cref="ExecutionChoice.ChooseAlignedOrk"/> in its prompt mode, the same Ork the press takes) or Classic's corpse while the own offer's window is open (the server's offer, tag 50). The cheap
/// refusals go first (<see cref="Common"/>, <see cref="Warfront"/>), the choice runs at most <see cref="PromptHz"/> times a second (<see cref="PromptThrottle"/>; review note 6), so a frame without a
/// candidate never aligns anything. The press is the server's: the client's crowd view lags it by the interpolation delay, so at the edge of range the prompted Ork can differ (accepted, documented).
/// </summary>
public static class ExecutionPromptRules
{
    /// <summary>How often the Warfront pick is recomputed (Hz; ours, review note 6).</summary>
    public const double PromptHz = 10.0;
    /// <summary>The crowd's candidates are gathered this far from the executor (metres; X2's range plus X4's 0.5 m margin).</summary>
    public const float GatherRange = ExecutionChoice.WarfrontRange + 0.5f;
    /// <summary>
    /// Whether a Chaos run (Warfront's Chaos roster on the Ork type bytes) shows the prompt: true since X4b (owner 2026-11-26), together with the server, its kinds by <see cref="ExecutionKindMap.Chaos"/> of
    /// Warfront's roster keys (the client's <c>WarfrontChaosUnitShown</c> hook). False was the series-37 candidate's: no prompt in a Chaos run.
    /// </summary>
    public const bool ChaosKindsIn = true;

    /// <summary>The dot's clearance over a Chaos body's top (metres; ours: the Ork dot's 2.45 m over the Boy's drawn 2.15 m).</summary>
    public const float MarkerOverTop = 0.3f;

    /// <summary>
    /// The dot's height over a Chaos enemy's feet (metres; X4b): its body's top as the shots see it (<see cref="MusouSim.ShotBodyOf"/> on the client's latched Chaos rules: the drawn Chaos models' heights,
    /// Cultist and Traitor Guardsman 2.0 m, the marines 2.3 m, the Aspiring Champion 2.53 m, the Chosen 2.76 m, the Daemon 2.6 m, the Lord a marine's 2.3 m x1.35) plus <see cref="MarkerOverTop"/>.
    /// </summary>
    public static float ChaosMarkerHeight(MusouRules? rules, MusouEnemyType type) => MusouSim.ShotBodyOf(rules ?? MusouRules.Default, type).Height + MarkerOverTop;

    /// <summary>The melee wargear the executor carries (the X3 rule: the class's melee choice), or null.</summary>
    public static string? MeleeKey(SimConfig c, in PlayerState s)
    {
        if (Loadouts.ClassOf(c, s.Class) is not { } loadout) return null;
        int choice = Loadouts.MeleeChoiceOf(c, s);
        return choice >= 0 && choice < loadout.MeleeChoices.Count ? loadout.MeleeChoices[choice].Wargear : null;
    }

    /// <summary>The refusals every mode shares, cheapest first (X3's <c>ExecutionsRefusal</c>): alive, a human, not executing, grounded, not in an interrupt, stun or stagger (a flinch does not count), not charging, evading or in a jump, a melee family, not an Ork, rows.</summary>
    public static ExecutionPromptHidden Common(SimConfig c, in PlayerState me, bool spectating, bool executing, IReadOnlyList<ExecutionRow> rows)
    {
        if (!me.Alive) return ExecutionPromptHidden.NotAlive;
        if (spectating) return ExecutionPromptHidden.Spectating;
        if (executing) return ExecutionPromptHidden.Executing;
        if (TfwContracts.IsPuppet(me.PlayerId)) return ExecutionPromptHidden.Puppet;   // retainers and AI Marines never execute (owner)
        if (!me.Grounded || me.Jump != JumpPhase.None) return ExecutionPromptHidden.Airborne;
        if (me.Reaction != ReactionKind.None) return ExecutionPromptHidden.Reaction;   // X3 refuses a press in an interrupt, stun or stagger; a light hit's flinch does not refuse (lead 40)
        if (me.Rush != RushPhase.None || me.EvadeTicks > 0) return ExecutionPromptHidden.Busy;
        if (ExecutionWeapons.FamilyOf(MeleeKey(c, me)) is null) return ExecutionPromptHidden.NoFamily;
        if (!ExecutionTargets.CanExecute(Races.Of(c, me.Class, me.Team))) return ExecutionPromptHidden.OrkPlayer;
        if (rows.Count == 0) return ExecutionPromptHidden.NoRows;
        return ExecutionPromptHidden.None;
    }

    /// <summary>Whether a Warfront executor is on its cooldown at <paramref name="tick"/>: <see cref="ExecutionRules.CooldownTicks"/> from its last completed execution (<paramref name="lastDoneTick"/>).</summary>
    public static bool CoolingDown(SimConfig c, uint? lastDoneTick, uint tick) =>
        lastDoneTick is { } done && tick < done + (uint)Math.Max(0, ExecutionRules.CooldownTicks(c));

    /// <summary>Warfront's own refusals after <see cref="Common"/>: in a battle, not a Chaos run (until X4b), not cooling down, a crowd Ork within <see cref="GatherRange"/>.</summary>
    public static ExecutionPromptHidden Warfront(SimConfig c, in PlayerState me, bool inBattle, bool chaosRun, uint? lastDoneTick, uint tick, IReadOnlyList<MusouShown> shown)
    {
        if (!inBattle) return ExecutionPromptHidden.NotInBattle;
        if (chaosRun && !ChaosKindsIn) return ExecutionPromptHidden.ChaosRun;
        if (CoolingDown(c, lastDoneTick, tick)) return ExecutionPromptHidden.Cooldown;
        float r2 = GatherRange * GatherRange;
        for (int i = 0; i < shown.Count; i++)
        {
            var e = shown[i];
            float dx = e.Position.X - me.Position.X, dy = e.Position.Y - me.Position.Y;
            if (dx * dx + dy * dy <= r2 && e.State is not (MusouEnemyState.Dying or MusouEnemyState.Spawning or MusouEnemyState.Airborne)) return ExecutionPromptHidden.None;
        }
        return ExecutionPromptHidden.NoEnemyNear;
    }

    /// <summary>
    /// The crowd Orks near <paramref name="me"/> as X2's <see cref="MusouEnemy"/> stand-ins (id, type, state, place, yaw; the crowd view carries no health, so a living one counts as 1: executions take
    /// any health), into <paramref name="into"/> (cleared first).
    /// </summary>
    public static void Gather(in PlayerState me, IReadOnlyList<MusouShown> shown, List<MusouEnemy> into)
    {
        into.Clear();
        float r2 = GatherRange * GatherRange;
        for (int i = 0; i < shown.Count; i++)
        {
            var e = shown[i];
            float dx = e.Position.X - me.Position.X, dy = e.Position.Y - me.Position.Y;
            if (dx * dx + dy * dy > r2) continue;
            float health = e.State == MusouEnemyState.Dying ? 0f : 1f;
            into.Add(new MusouEnemy(e.Id, e.Type, e.State, e.Position, Vector2.Zero, e.Yaw, health, 0, 0, 0, 0));
        }
    }

    /// <summary>
    /// The bystander reach round the executor (metres): the server's <c>World.ExecutionsBystanderReach</c> (the longest slide, two capsule radii and 7 m), the same formula on the match's config
    /// (that property reads only <see cref="SimConfig.CapsuleRadius"/> and the X2/X3 constants).
    /// </summary>
    public static float BystanderReach(SimConfig c) => MathF.Max(ExecutionRules.ClassicMaxSlide, ExecutionAlign.MaxBackSlide) + 2f * MathF.Max(c.CapsuleRadius, 0.05f) + 7f;

    /// <summary>
    /// The slide's bystanders by X4's horde rule itself (<see cref="ExecutionBystanders.Warfront"/>, the call the server's <c>ExecutionsWarfrontBystanders</c> makes; X6 review X6-12): the other
    /// living players within <see cref="BystanderReach"/> (3D) of the executor, then the player-solid crowd enemies (<see cref="ExecutionBystanders.Counts"/>: the Warboss and the Boss Orks) as the
    /// crowd is drawn (a dying one is not a body any more). Regular Orks never block the slide.
    /// </summary>
    public static void Bystanders(SimConfig c, in PlayerState me, IReadOnlyList<PlayerState> others, IReadOnlyList<MusouShown> shown, List<Vector3> into)
    {
        into.Clear();
        var players = _scratchPlayers ??= new();
        var enemies = _scratchEnemies ??= new();
        players.Clear(); enemies.Clear();   // reused: no per-pick allocation but the rule's own result list
        players.Add((me.PlayerId, me));
        foreach (var o in others) if (o.PlayerId != me.PlayerId) players.Add((o.PlayerId, o));
        players.Sort(ById);   // the server's order (by id)
        foreach (var e in shown) if (e.State != MusouEnemyState.Dying && ExecutionBystanders.Counts(e.Type)) enemies.Add((e.Type, e.Position));
        into.AddRange(ExecutionBystanders.Warfront(me.PlayerId, players, enemies, me.Position, BystanderReach(c)));
    }

    [ThreadStatic] static List<(int, PlayerState)>? _scratchPlayers;
    [ThreadStatic] static List<(MusouEnemyType, Vector3)>? _scratchEnemies;
    static readonly Comparison<(int, PlayerState)> ById = (a, b) => a.Item1.CompareTo(b.Item1);

    /// <summary>
    /// Whether the own Execute press counter's change spends the Classic offer (X6 review X6-10, re-review 1 and 4): <paramref name="seen"/> is synced to <paramref name="presses"/> every frame,
    /// and a new press spends it only when the dot showed on the previous frame (<paramref name="shownLastFrame"/>). A press made while something else hid the dot (rolling, staggered, airborne,
    /// no offer yet, during a move, before the window opened at the view tick) is refused by the server before the offer, so it spends nothing here either.
    /// </summary>
    public static bool PressSpendsOffer(int presses, ref int seen, bool shownLastFrame)
    {
        bool pressed = presses != seen;
        seen = presses;
        return pressed && shownLastFrame;
    }

    /// <summary>
    /// The Ork the press would take (0: none fits): <see cref="ExecutionChoice.ChooseAlignedOrk"/> in its prompt mode (no seed: the first fitting move; the Ork chosen is the press's) on the
    /// stand-ins of <see cref="Gather"/>, the reserved Orks (being executed by others) skipped. <paramref name="choices"/> counts the calls (the tests' early-out check).
    /// </summary>
    public static ushort PickOrkVictim(in PlayerState me, float facingYaw, SimConfig c, IReadOnlyList<ExecutionRow> rows, CollisionWorld? world, IReadOnlyList<MusouEnemy> near,
        IReadOnlyList<Vector3> bystanders, IReadOnlySet<ushort>? reserved, ref int choices, ExecutionKindMap? kinds = null)
    {
        if (near.Count == 0 || Loadouts.ClassOf(c, me.Class) is not { } loadout || MeleeKey(c, me) is not { } weapon) return 0;
        choices++;
        return ExecutionChoice.ChooseAlignedOrk(me, facingYaw, loadout.Name, weapon, near, rows, c, world, reserved, seedFor: null, bystanders: bystanders, kinds: kinds).Id;
    }

    /// <summary>Classic: the own offer's window is open at the client's view of the server tick (<paramref name="renderTick"/>: the press counts at its view tick on the server).</summary>
    public static bool ClassicOpen(ExecutionOfferView? offer, double renderTick) => offer is { } o && o.OpenAt(renderTick);
}

/// <summary>At most <see cref="ExecutionPromptRules.PromptHz"/> recomputations a second (render time, seconds).</summary>
public sealed class PromptThrottle
{
    double _last = double.NegativeInfinity;

    /// <summary>True (and the time noted) when a recomputation is due at <paramref name="now"/>.</summary>
    public bool Due(double now)
    {
        if (now - _last < 1.0 / ExecutionPromptRules.PromptHz - 1e-9) return false;
        _last = now;
        return true;
    }

    /// <summary>The next call is due at once (the candidate set changed sharply: a new run, a death).</summary>
    public void Reset() => _last = double.NegativeInfinity;
}
