using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.ImpactRules;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// fx/melee-presentation (the owner's reports of 2026-10-30; client presentation only, no simulation or protocol change):
/// <list type="bullet">
/// <item><b>Others' melee hits</b> were silent: only the own player's hit report posted the weapon's hit sound. A hit between two other players, or on the local player, is now
/// <b>inferred</b> (interpretation) from what the snapshots show, by the gate the stomp and sword effects use (<see cref="MeleeHitGate"/>, <c>NetworkView.MeleeFx.cs</c>): the attacker's
/// melee step crosses its hit time with an enemy in reach, and the victim <b>reacts</b> in the same window (health plus shield falls, or it dies: <see cref="MeleeHitConfirm"/>); then the
/// attacker's weapon's hit sound (<see cref="SoundTriggers.MeleeHitSound"/>) is posted at the victim, as for the own player's hits. The gate fires once per attack and never on a
/// reconcile rewind; an evaded or missed swing gets none.</item>
/// <item><b>Charges.</b> A bull rush or a dash attack that connects plays the data's own charge impact (<see cref="SoundTriggers.ChargeHitSound"/>: the <c>body_smash</c> container's
/// <c>play_sm_bull_rush_smash</c>; the wargear's other command there, <c>play_sm_sk01_bodyslam</c>, is in no bank of this install) instead of the weapon's hit sound: the own player's from its
/// hit report while the prediction shows a charge (or just did), the others' from the phase change into the collision / hit state (<see cref="ChargeConnect"/>) with the same reaction check.</item>
/// <item><b>Idle hum.</b> A melee weapon in hand hums with the wargear's own idle events (<see cref="MeleeIdleSounds"/>): the thunder hammer, the daemon maul and the power axe; the power sword
/// quietly with the axe's hum (the Space Marine sword's own <c>on_deploy_actions</c> names it; the Chaos sword uses it as a substitute, <b>ours</b>); the chain axe's only hum is the missing
/// <c>play_sm_power_sword_idle</c> (not reproducible). Started when the weapon comes into hand, stopped when it leaves, the player dies or leaves.</item>
/// </list>
/// Test aids: <c>--no-melee-sounds</c> (before / after), <c>--log-sound melee</c> (every post).
/// </summary>
public partial class NetworkView
{
    bool NoMeleeSounds => _noMeleeSounds ??= CmdFlag("no-melee-sounds");
    bool? _noMeleeSounds;

    /// <summary>The own player's charge, remembered for the hit report that arrives a few frames after the connection: the last time the prediction showed one and its phase.</summary>
    double _ownRushAt = double.NegativeInfinity;
    RushPhase _ownRushPhase;
    /// <summary>A hit report within this many seconds after the prediction last showed a charge is the charge's (ours; the collision clip itself lasts about this long).</summary>
    const double OwnChargeMemory = 0.6;

    int _meleeHitPosts, _chargeHitPosts, _humsStarted;
    readonly Dictionary<string, MeleeIdleSounds.Hum?> _humCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per frame for the own player (from <see cref="SoundPlayer"/>): remembers a charge in progress.</summary>
    void SoundNoteOwnCharge(PlayerState s)
    {
        if (s.Rush == RushPhase.None) return;
        _ownRushAt = _connectedFor;
        _ownRushPhase = s.Rush;
    }

    /// <summary>Whether the own player's hit report is a charge's: the prediction shows a charge, or showed one a moment ago.</summary>
    bool OwnHitIsCharge(PlayerState me, out bool dash)
    {
        dash = SmSim.Rush.IsDash(me.Rush != RushPhase.None ? me.Rush : _ownRushPhase);
        return me.Rush != RushPhase.None || _connectedFor - _ownRushAt <= OwnChargeMemory;
    }

    /// <summary>The wargear file whose container a charge hit's sound comes from: the jump pack's for the dash attack, else the melee weapon's.</summary>
    string? ChargeWargear(PlayerState s, bool dash) => dash ? Loadouts.JumpPack(_config!, s)?.Name : MeleeWargear(s);

    /// <summary>
    /// A charge hit's sound event: the data's charge impact container of the charging wargear, else (a file without one) its weapon's hit sound; null when there is none.
    /// </summary>
    string? ChargeHitEvent(PlayerState attacker, bool dash)
    {
        if (_soundTriggers is null) return null;
        return _soundTriggers.ChargeHitSound(ChargeWargear(attacker, dash)) ?? (MeleeWargear(attacker) is { } m ? _soundTriggers.MeleeHitSound(m) : null);
    }

    /// <summary>
    /// Posts a melee or charge hit sound at a victim (the armour and flesh layers together, as for a bullet: <see cref="PostPlayerHitSound"/>). Returns the event posted or null.
    /// </summary>
    string? PostMeleeHit(PlayerState attacker, int victimId, System.Numerics.Vector3 victimPosition, bool charge, bool dash, string source)
    {
        if (_soundTriggers is null || _sound is null || _config is null || NoMeleeSounds) return null;
        string? evt = charge ? ChargeHitEvent(attacker, dash) : MeleeWargear(attacker) is { } m ? _soundTriggers.MeleeHitSound(m) : null;
        if (evt is null) return null;
        var where = victimPosition + new System.Numerics.Vector3(0f, 0f, 1.2f);
        PostPlayerHitSound(evt, new SoundEmitter { Position = ToGodot(where), Label = $"{(charge ? "charge" : "melee")} hit on P{victimId}", OneShot = true });
        CountSound(charge ? "charge hit" : "melee hit");
        if (charge) _chargeHitPosts++; else _meleeHitPosts++;
        if (LogSound("melee")) GD.Print($"MELEE HIT SOUND t {_connectedFor:F3} P{attacker.PlayerId} {(charge ? (dash ? "dash attack" : "charge") : "melee")} on P{victimId} ({source}) wargear {(charge ? ChargeWargear(attacker, dash) : MeleeWargear(attacker)) ?? "-"} event {evt}");
        return evt;
    }

    // ---- others' hits (inferred) ----

    /// <summary>The same once-per-attack gate the stomp and sword effects use (<c>NetworkView.MeleeFx.cs</c>), kept apart so the sounds work with the effects off.</summary>
    readonly MeleeHitGate _meleeSoundGate = new();
    readonly MeleeHitConfirm _meleeConfirm = new();
    readonly List<MeleeHitConfirm.Hit> _meleeConfirmed = new();
    readonly List<PlayerState> _meleeSoundAll = new(), _meleeSoundPool = new();
    readonly HashSet<int> _meleeSoundPresent = new();
    readonly Dictionary<int, RushPhase> _meleeRushPrev = new();
    /// <summary>The last living state of each player and when it died (-1: alive): a victim killed by the blow that crosses the attacker's hit time is already dead in the same snapshot.</summary>
    readonly Dictionary<int, (PlayerState State, double DiedAt)> _meleeLastAlive = new();
    /// <summary>A player who died this long ago still counts as in reach for a hit candidate (ours; the candidate window is <see cref="MeleeHitConfirm.After"/>).</summary>
    const double RecentDeath = 0.4;

    /// <summary>
    /// Per frame (from <see cref="UpdateSound"/>): the melee and charge hits of the players other than the local one, and on it, inferred from the snapshots (see the class summary). Candidates:
    /// an attacker whose melee step passes its hit time (<see cref="MeleeHitGate"/>) or whose charge changes into its connected phase (<see cref="ChargeConnect"/>) with an enemy in reach
    /// (<see cref="MeleePresentation.Victim"/>, the rule the sword's flare uses); a candidate is posted when its victim reacts (<see cref="MeleeHitConfirm"/>).
    /// </summary>
    void SoundMeleeOthers(PlayerState me, IReadOnlyList<PlayerState> others)
    {
        if (_client is null || _config is null || _soundTriggers is null || _sound is null || NoMeleeSounds) return;
        double now = _connectedFor;
        int ownId = _client.PlayerId;
        var all = _meleeSoundAll;
        all.Clear();
        all.Add(me);
        for (int i = 0; i < others.Count; i++) all.Add(others[i]);
        _meleeSoundPresent.Clear();
        var pool = _meleeSoundPool;
        pool.Clear();
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            _meleeSoundPresent.Add(p.PlayerId);
            _meleeConfirm.Observe(p.PlayerId, p.Alive, p.Health + p.Shield, now);
            if (p.Alive) { _meleeLastAlive[p.PlayerId] = (p, -1); pool.Add(p); }
            else if (_meleeLastAlive.TryGetValue(p.PlayerId, out var last))
            {
                double diedAt = last.DiedAt < 0 ? now : last.DiedAt;
                _meleeLastAlive[p.PlayerId] = (last.State, diedAt);
                if (now - diedAt <= RecentDeath) pool.Add(last.State);
            }
        }
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p.PlayerId == ownId) continue;   // the own player's hits come from its hit reports (SoundHits)
            if (p.Alive && p.MeleeStep > 0 && Combat.MeleeStepOf(_config, p) is { } step)
            {
                long start = MeleeHitGate.StartTick(_client.LatestSnapshotTick, p.MeleeTime, _config.TickSeconds);
                if (_meleeSoundGate.Passed(p.PlayerId, p.MeleeStep, p.MeleeHeavy, p.MeleeTime, step.HitTime, start)
                    && MeleePresentation.Victim(p, pool, _config) is var victim && victim > 0)
                    _meleeConfirm.Add(p.PlayerId, victim, charge: false, dash: false, now);
            }
            else _meleeSoundGate.Idle(p.PlayerId);
            _meleeRushPrev.TryGetValue(p.PlayerId, out var before);
            _meleeRushPrev[p.PlayerId] = p.Rush;
            if (p.Alive && ChargeConnect.Connected(before, p.Rush) && MeleePresentation.Victim(p, pool, _config) is var struck && struck > 0)
                _meleeConfirm.Add(p.PlayerId, struck, charge: true, dash: p.Rush == RushPhase.DashHit, now);
        }
        _meleeConfirmed.Clear();
        _meleeConfirm.Drain(now, _meleeConfirmed);
        foreach (var hit in _meleeConfirmed)
        {
            int a = all.FindIndex(x => x.PlayerId == hit.Attacker);
            int v = pool.FindIndex(x => x.PlayerId == hit.Victim);
            if (a >= 0 && v >= 0) PostMeleeHit(all[a], hit.Victim, pool[v].Position, hit.Charge, hit.Dash, "inferred from the snapshots");
        }
        _meleeSoundGate.Prune(_meleeSoundPresent.Contains);
        _meleeConfirm.Prune(_meleeSoundPresent.Contains);
        _meleeRushGone.Clear();
        foreach (var id in _meleeRushPrev.Keys) if (!_meleeSoundPresent.Contains(id)) _meleeRushGone.Add(id);
        foreach (var id in _meleeRushGone) { _meleeRushPrev.Remove(id); _meleeLastAlive.Remove(id); }
    }
    readonly List<int> _meleeRushGone = new();

    /// <summary>The hum a melee wargear gives (cached; null when it has none that resolves).</summary>
    MeleeIdleSounds.Hum? HumOf(string? wargear)
    {
        if (wargear is null || _soundTriggers is null || _audio is null) return null;
        if (_humCache.TryGetValue(wargear, out var known)) return known;
        var hum = MeleeIdleSounds.For(wargear, _soundTriggers.File, _audio.HasEvent);
        _humCache[wargear] = hum;
        if (LogSound("melee"))
            GD.Print($"MELEE HUM {wargear}: {(hum is null ? "none (the file names no idle event that is in a bank: not reproducible)" : $"{hum.Start} / stop '{hum.Stop}', {hum.GainDb:+0.#;-0.#;0} dB{(hum.Substitute ? ", substitute (ours)" : "")}")}");
        return hum;
    }

    /// <summary>
    /// Per frame per player (from <see cref="SoundPlayer"/>): the melee weapon's idle hum while the weapon is in hand and the player alive; started once, stopped when the weapon leaves
    /// the hand (stowed, another weapon), the player dies or the melee choice changes. A player who leaves is silenced with its emitter (<see cref="SoundSystem.StopEmitter"/>).
    /// </summary>
    void SoundMeleeIdle(SoundTrack t, CharacterViewer character, PlayerState s)
    {
        if (_sound is null || _soundTriggers is null) return;
        string? wargear = s.Alive && !NoMeleeSounds && character.Weapons?.MeleeMeshInHand is not null ? MeleeWargear(s) : null;
        var want = HumOf(wargear);
        if (t.HumFile is not null && (want is null || !string.Equals(wargear, t.HumFile, StringComparison.OrdinalIgnoreCase))) StopMeleeHum(t);
        if (want is null || t.HumFile is not null) return;
        _sound.Post(want.Start, t.Emitter, offset: new VoiceOffset(want.GainDb));
        t.HumFile = wargear; t.HumStop = want.Stop;
        _humsStarted++;
        CountSound("melee hum");
        if (LogSound("melee")) GD.Print($"MELEE HUM t {_connectedFor:F3} P{s.PlayerId} start {want.Start} ({wargear}{(want.Substitute ? ", substitute" : "")}, {want.GainDb:+0.#;-0.#;0} dB)");
    }

    void StopMeleeHum(SoundTrack t)
    {
        if (t.HumFile is null) return;
        if (t.HumStop is { Length: > 0 } stop && _sound is not null) _sound.Post(stop, t.Emitter);
        if (LogSound("melee")) GD.Print($"MELEE HUM t {_connectedFor:F3} {t.Emitter.Label} stop {t.HumStop ?? "-"} ({t.HumFile})");
        t.HumFile = null; t.HumStop = null;
    }

    /// <summary>The sound system's summary for the log line.</summary>
    string MeleeSoundLog() => $"melee sounds: hits {_meleeHitPosts}, charge hits {_chargeHitPosts}, hums started {_humsStarted}";
}
