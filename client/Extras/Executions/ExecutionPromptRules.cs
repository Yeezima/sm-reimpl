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
    /// Chaos runs (Warfront's renegade roster aliases Ork type bytes) refuse every execution until X4b maps their kinds (plan "Chaos runs refuse executions until X4b"): false, so the prompt never shows
    /// in a Chaos run. X4b turns it on together with the server.
    /// </summary>
    public const bool ChaosKindsIn = false;

    /// <summary>The prompt's text: <c>[Q] EXECUTE</c> with the bound key's display name, or <c>EXECUTE (unbound: Controls)</c> when the device in use has no binding (lead 39: the pad's default).</summary>
    public static string Text(string? boundKey) =>
        string.IsNullOrWhiteSpace(boundKey) || boundKey is "?" or "-" ? UnboundText : $"[{boundKey.Trim().ToUpperInvariant()}] {Label}";

    public const string Label = "EXECUTE";
    public const string UnboundText = "EXECUTE (unbound: Controls)";

    /// <summary>The key and the label of a prompt text (<see cref="Text"/>): the key boxed, the label beside it; a text without a key gives a null key.</summary>
    public static (string? Key, string Label) Split(string text)
    {
        if (text.Length > 2 && text[0] == '[' && text.IndexOf("] ", StringComparison.Ordinal) is var close and > 1)
            return (text[1..close], text[(close + 2)..]);
        return (null, text);
    }

    /// <summary>The melee wargear the executor carries (the X3 rule: the class's melee choice), or null.</summary>
    public static string? MeleeKey(SimConfig c, in PlayerState s)
    {
        if (Loadouts.ClassOf(c, s.Class) is not { } loadout) return null;
        int choice = Loadouts.MeleeChoiceOf(c, s);
        return choice >= 0 && choice < loadout.MeleeChoices.Count ? loadout.MeleeChoices[choice].Wargear : null;
    }

    /// <summary>The refusals every mode shares, cheapest first (X3's <c>ExecutionsRefusal</c>): alive, a human, not executing, grounded, not in a reaction or flinch, not charging, evading or in a jump, a melee family, not an Ork, rows.</summary>
    public static ExecutionPromptHidden Common(SimConfig c, in PlayerState me, bool spectating, bool executing, IReadOnlyList<ExecutionRow> rows)
    {
        if (!me.Alive) return ExecutionPromptHidden.NotAlive;
        if (spectating) return ExecutionPromptHidden.Spectating;
        if (executing) return ExecutionPromptHidden.Executing;
        if (TfwContracts.IsPuppet(me.PlayerId)) return ExecutionPromptHidden.Puppet;   // retainers and AI Marines never execute (owner)
        if (!me.Grounded || me.Jump != JumpPhase.None) return ExecutionPromptHidden.Airborne;
        if (me.Reaction != ReactionKind.None || me.ReactionTicks > 0 || me.FlinchTicks > 0) return ExecutionPromptHidden.Reaction;   // X3 refuses a press in a reaction or a flinch
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
    /// The slide's bystanders (X4's horde rule, plan step 2; until X4's <c>ExecutionsBystanders</c> lands this is its copy, to be replaced by a call): the other living players within reach,
    /// and only the player-solid crowd enemies (<see cref="MusouSolids.Blocks"/>: the Warboss and the Boss Orks). Regular Orks never block the slide.
    /// </summary>
    public static void Bystanders(SimConfig c, in PlayerState me, IReadOnlyList<PlayerState> others, IReadOnlyList<MusouShown> shown, List<Vector3> into)
    {
        into.Clear();
        float reach = ExecutionAlign.MaxSlide + 2f * c.CapsuleRadius + 7f, r2 = reach * reach;
        foreach (var o in others)
        {
            if (!o.Alive || o.PlayerId == me.PlayerId) continue;
            float dx = o.Position.X - me.Position.X, dy = o.Position.Y - me.Position.Y;
            if (dx * dx + dy * dy <= r2) into.Add(o.Position);
        }
        foreach (var e in shown)
        {
            if (!MusouSolids.Blocks(e.Type) || e.State == MusouEnemyState.Dying) continue;
            float dx = e.Position.X - me.Position.X, dy = e.Position.Y - me.Position.Y;
            if (dx * dx + dy * dy <= r2) into.Add(e.Position);
        }
    }

    /// <summary>
    /// The Ork the press would take (0: none fits): <see cref="ExecutionChoice.ChooseAlignedOrk"/> in its prompt mode (no seed: the first fitting move; the Ork chosen is the press's) on the
    /// stand-ins of <see cref="Gather"/>, the reserved Orks (being executed by others) skipped. <paramref name="choices"/> counts the calls (the tests' early-out check).
    /// </summary>
    public static ushort WarfrontPick(in PlayerState me, float facingYaw, SimConfig c, IReadOnlyList<ExecutionRow> rows, CollisionWorld? world, IReadOnlyList<MusouEnemy> near,
        IReadOnlyList<Vector3> bystanders, IReadOnlySet<ushort>? reserved, ref int choices)
    {
        if (near.Count == 0 || Loadouts.ClassOf(c, me.Class) is not { } loadout || MeleeKey(c, me) is not { } weapon) return 0;
        choices++;
        return ExecutionChoice.ChooseAlignedOrk(me, facingYaw, loadout.Name, weapon, near, rows, c, world, reserved, seedFor: null, bystanders: bystanders).Id;
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
