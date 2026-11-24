using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Retainers;
using SmSim.Extras.Warfront.Squad;

namespace SmSim;

/// <summary>
/// Warfront's retainers as real World players in the World (Phase B, T2; spec "Added content: Warfront: retainers as World players"): their spawn in a puppet id with <see cref="MarineRole.Squad"/>, their input
/// (<see cref="WarfrontRetainers.Brain"/>), their tick in the puppets' step, their damage taken (the squad's 0.35, not the puppets' 0.10, and none of the Orks' level damage), and the switches of the owner's answers
/// (<see cref="WarfrontRetainerRules"/>). Each hook returns at once unless the run's Warfront object and its retainers are set, so Musou Mode and Classic PvP never see a change.
/// </summary>
public sealed partial class World
{
    /// <summary>The run's retainers (null outside Warfront, or before the squad wiring is built).</summary>
    WarfrontRetainers? WarfrontRetainersNow => _tfw?.Warfront?.Retainers;

    /// <summary>Whether <paramref name="id"/> is a Warfront retainer's World player (a puppet of <see cref="MarineRole.Squad"/>).</summary>
    public bool WarfrontIsRetainer(int id) => _tfw is { Warfront: not null } run && TfwContracts.IsPuppet(id) && run.MarineOf(id) is { Role: MarineRole.Squad };

    /// <summary>
    /// A retainer of <paramref name="owner"/> as a World player at <paramref name="at"/> (snapped to the walking graph), with <paramref name="choice"/>'s loadout and the scoreboard name <paramref name="name"/>; it faces the
    /// map's centre in a battle. Its look is its owner's (<c>GameServer.TfwLook</c>: every role but the pair's wears its owner's). Null, and nothing changed, outside Warfront, for an unknown owner or with no free puppet id.
    /// </summary>
    internal int? WarfrontSpawnRetainer(int owner, LoadoutChoice choice, string name, Vector3 at)
    {
        if (_tfw is not { Warfront: not null } run || !_players.ContainsKey(owner) || TfwContracts.IsPuppet(owner)) return null;
        if (Musou?.Nav is { } nav)
        {
            if (nav.Nearest(at, 1.5f, 2.5f) is var node and >= 0) at = nav.Position(node);
            else at = _players[owner].Position;   // the ring place is off the walking graph (a wall, a ledge): land on the owner, as the human landing falls back (review fix 2026-11-25)
        }
        int? id = TfwSpawnPuppet(run, "tactical", at, 0f, id => new Marine(id, -1, string.IsNullOrWhiteSpace(name) ? "Retainer" : name, MarineRole.Squad, owner) { HoldAt = at });
        if (id is not { } made) return null;
        run.Battle?.WarfrontRetainerIn(made);   // never captures, weighs as a Marine on the HQ hold
        var p = _players[made];
        var dressed = Loadouts.Choose(Config, p with { Class = Math.Clamp(choice.Class, 0, Math.Max(0, Config.Classes.Count - 1)) }, choice.Primary, choice.Pistol, choice.Melee, choice.Equipment, choice.Perk1, choice.Perk2, choice.Second);
        _players[made] = Combat.Respawned(dressed, Config) with { Position = p.Position, Yaw = p.Yaw };
        _musouArmed.Remove(made);   // the war's arsenal never arms a puppet (TfwArmAll and TfwGiveFrags skip puppet ids): the retainer keeps the Classic respawn kit of its loadout
        return made;
    }

    /// <summary>A living player back at full health and armour; false for an unknown or dead one.</summary>
    internal bool WarfrontRefillPlayer(int id)
    {
        if (!_players.TryGetValue(id, out var p) || !p.Alive) return false;
        var body = Loadouts.Body(Config, p);
        _players[id] = p with { Health = body.HealthMax, Shield = body.ShieldMax };
        return true;
    }

    /// <summary>A retainer's input: its brain's, else it stands still keeping its heading (Phase B T4 plugs the Classic bot brain in here, ahead of Musou's <see cref="TfwRun.PuppetBrain"/>).</summary>
    partial void WarfrontPuppetInput(int id, in PlayerState self, ref PlayerInput input, ref bool handled)
    {
        if (WarfrontRetainersNow is not { } retainers || !WarfrontIsRetainer(id)) return;
        input = (self.Alive ? retainers.Brain?.Invoke(id, self) : null) ?? new PlayerInput(0, 0f, 0f, self.Yaw, InputButtons.None);
        handled = true;
    }

    /// <summary>A retainer's tick in the puppets' step: a fallen one waits for its revive (its respawn held; the downed window and the "out" are the registry's, <see cref="WarfrontRetainers.Step"/>).</summary>
    partial void WarfrontSquadMarineStep(Marine m, in PlayerState p)
    {
        if (WarfrontRetainersNow?.TakeOut(m.Id) == true) return;   // out for the battle: it leaves the World here, outside the fight's id lists
        m.Down = !p.Alive;
        if (!p.Alive) _players[m.Id] = p with { RespawnLeft = MarineRules.DeadHold };
    }

    /// <summary>A retainer skips the puppets' tenth of every hit (it takes the squad's share in <see cref="WarfrontDamageTaken"/>).</summary>
    partial void WarfrontSquadDamage(int target, ref bool own)
    {
        if (WarfrontRetainersNow is not null && WarfrontIsRetainer(target)) own = true;
    }

    /// <summary>The retainer's share of a hit: 0.35, through the cards' third and the level's health (1 / health while unshielded, 1 / armour while shielded).</summary>
    void WarfrontRetainerDamageTaken(WarfrontRetainers retainers, int target, ref float scale)
    {
        scale *= WarfrontRetainerRules.DamageTaken;
        if (retainers.ByWorldId(target) is not { } r || !_players.TryGetValue(target, out var p)) return;
        var (_, health, armour) = retainers.ScaleOf(r.Key);
        float s = p.Shield > 0f ? armour : health;
        if (s > 0f && s != 1f) scale /= s;
    }

    /// <summary>Owner answer (a): the war's human-only scales for a retainer only when <see cref="WarfrontRetainerRules.HumanOnlyScales"/> (today: never; base player damage only).</summary>
    partial void WarfrontScalesPlayer(int id, ref bool scales)
    {
        if (WarfrontRetainerRules.HumanOnlyScales && !scales && WarfrontIsRetainer(id)) scales = true;
    }

    /// <summary>Owner (2026-11-25, T2 review): a retainer's melee reach is a human's exactly, so it takes the human cleave trim (<see cref="WarfrontRetainerRules.HumanMeleeReach"/>).</summary>
    partial void WarfrontCleaveTrim(int attacker, ref bool trim)
    {
        if (WarfrontRetainerRules.HumanMeleeReach && !trim && WarfrontIsRetainer(attacker)) trim = true;
    }

    /// <summary>Owner answer (b): a scrap trap hurts a retainer as a human (<see cref="WarfrontRetainerRules.TrapsHurtLikeHumans"/>) instead of killing it outright.</summary>
    partial void WarfrontTrapAiMarine(int id, ref bool aiMarine)
    {
        if (WarfrontRetainerRules.TrapsHurtLikeHumans && aiMarine && WarfrontIsRetainer(id)) aiMarine = false;
    }

    /// <summary>The retainers' damage scales on the Orks (their key's card third and level curve), right after the clear.</summary>
    void WarfrontRetainerScales(MusouSim musou)
    {
        if (WarfrontRetainersNow is not { } retainers) return;
        foreach (var r in retainers.All)
        {
            if (r.WorldId == 0) continue;
            float d = retainers.ScaleOf(r.Key).Damage;
            if (d != 1f) musou.SetAttackerScale(r.WorldId, d, d);
        }
    }

    /// <summary>A kill of the war's tick for the retainers: a retainer's pays its owner (the wiring's sink), every retainer that hit it shares an assist.</summary>
    void WarfrontRetainerKill(in MusouKill kill)
    {
        if (WarfrontRetainersNow is not { } retainers) return;
        if (retainers.Killed(kill.Enemy, kill.Type, kill.Player, _musou!.Tick) is { } squadKill) retainers.KillSink?.Invoke(squadKill);
    }
}
