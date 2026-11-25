using System;
using Godot;
using SmClient.Extras.Warfront.Chaos.Finale;
using SmNet;
using SmSim;
using SmNet.Extras.Warfront;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Chaos.Finale;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The Chaos finale on the client (Chaos Warfront C5c; tag 51 kinds 16 and 17, <see cref="WarfrontChaosDuelWire"/>). C4c's tag 51 forward hands kinds 16 to 31 to <see cref="WarfrontChaosDuelSection"/>, which
/// puts them into a <see cref="ChaosDuelModel"/>: the duel HUD (<see cref="DuelHud"/>, everyone), the Lord's leap and bolt cues (<see cref="LordTelegraphFx"/>), and the spectator camera on the champion for everyone
/// else (<see cref="WarfrontChaosDuelSpectate"/>, forwarded from Warfront's spectator view). The first duel state of a battle sets <see cref="SmClient.Extras.Warfront.Chaos.Breach.ChaosBreachModel.SilentCloses"/>
/// (C4c's contract), so the server's silent close of every breach at the duel start shows no closure (the server sends that state before the list). Only a Chaos stage sends these kinds, past its fortress's
/// bodyguard: nothing here runs in Classic, Faithful, Musou Mode or an Orks Warfront run. A bot or a headless client keeps the model and the log lines and draws nothing. The frame runs on its own node under the
/// game space (a <see cref="ChaosBreachDriver"/>), so no other file carries a frame line.
/// </summary>
public partial class NetworkView
{
    readonly ChaosDuelModel _wfDuel = new();
    ChaosBreachDriver? _wfDuelDriver;
    LordTelegraphFx? _wfDuelFx;
    DuelHud? _wfDuelHud;

    /// <summary>C5c's seam at the top of Warfront's spectator view (<c>NetworkView.WarfrontClientHooks.cs</c>, pinned by <c>DuelHudModelTests.SpectateForwardIsPinned</c>).</summary>
    partial void WarfrontChaosDuelSpectate(PlayerState me, ref (Vector3 Eye, Vector3 Target)? view, ref bool taken);

    double _wfFortressSeenAt = -100;
    ushort _wfDuelWatching;
    bool _wfDuelSeenThisBattle, _wfLordShieldedShown;

    /// <summary>The Battle section's kind (the line in <c>NetworkView.WarfrontBattle.cs</c>, pinned): a fortress battle, on the Battle HUD's clock (real seconds).</summary>
    void WarfrontDuelBattleSeen(bool fortress, double at)
    {
        if (fortress) _wfFortressSeenAt = at;
    }

    /// <summary>Whether this is a fortress battle (its Battle section within the Battle HUD's 3 s).</summary>
    bool WarfrontFortressBattle => Time.GetTicksMsec() / 1000.0 - _wfFortressSeenAt <= 3.0;

    /// <summary>
    /// Every frame of a Chaos battle (the line in C3's <c>WarfrontChaosFrame</c>): in a Chaos fortress battle the breach model's silent closes are on for the whole battle (lead 40: a kind 2 closure still
    /// shows; only the list's lost-burst fallback is off, so the duel's silent close never plays), and until the relay starts the Lord's bar reads <c>CHAOS LORD: SHIELDED</c>.
    /// </summary>
    void WarfrontChaosDuelFrame()
    {
        double now = _connectedFor;
        // lead 41: Musou's slam ring fills over the client's boss rules' wind-up; in a Chaos battle the only boss is the Lord, whose slam winds up x1.5 (the server's WarfrontLordRules), so the client's
        // latched Chaos config carries his wind-up (Musou's own code and timing untouched: the Orks' and Musou Mode's configs never get it; C3's latch undo restores the Orks' config)
        if (_config?.MusouRules is { } rules)
        {
            float lordSlam = (_wfOrksConfig?.MusouRules ?? MusouRules.Default).Boss.SlamWindUp * WarfrontDuelRules.SlamWindUpScale;
            if (rules.Boss.SlamWindUp != lordSlam)
            {
                _config = _config with { MusouRules = rules with { Boss = rules.Boss with { SlamWindUp = lordSlam } } };
                _musouRulesOver = _config.MusouRules;
                GD.Print($"t {now:F1} s: warfront chaos: the slam telegraph fills over the Lord's {lordSlam:0.##} s wind-up");
            }
        }
        bool fortress = WarfrontFortressBattle;
        if (!fortress) _wfDuelSeenThisBattle = false;
        if (_wfDuel.State is not null) _wfDuelSeenThisBattle = true;
        bool silent = fortress || _wfDuel.Running(now);
        if (silent != _wfBreach.SilentCloses)
        {
            _wfBreach.SilentCloses = silent;
            GD.Print($"t {now:F1} s: warfront chaos: breach closures without a closure section are {(silent ? "silent (a Chaos fortress battle)" : "shown again")}");
        }
        bool shielded = fortress && !_wfDuelSeenThisBattle;
        if (shielded != _wfLordShieldedShown) { _wfLordShieldedShown = shielded; GD.Print($"t {now:F1} s: warfront chaos: the Lord's bar {(shielded ? "reads SHIELDED" : "plain")}"); }
        if (_musouHud is { } hud)
        {
            bool hide = _wfDuel.Running(now) || _wfDuel.HudShown(now);   // lead 41: the duel strip carries the Lord's bar; the boss column's commander bar steps aside (no lines built here)
            if (hide != hud.WarfrontHideCommander) { hud.WarfrontHideCommander = hide; GD.Print($"t {now:F1} s: warfront chaos: the boss column's Lord bar {(hide ? "hidden (the duel strip has it)" : "back")}"); }
        }
    }

    static readonly Func<string, string> s_chaosRelabel = SmContent.Extras.Warfront.WarfrontChaosLooksPlan.HudLabel, s_shieldedRelabel = ChaosDuelLook.ShieldedRelabel;

    /// <summary>The Musou HUD's Chaos words (C3's line chooses it once a frame, Fable review): the shielded Lord's in a Chaos fortress before the relay, else C3's; one cached delegate each, so the HUD's word cache is kept until the choice changes.</summary>
    Func<string, string> WarfrontChaosRelabel => _wfLordShieldedShown ? s_shieldedRelabel : s_chaosRelabel;

    /// <summary>The duel model, for the tests and the capture aid.</summary>
    internal ChaosDuelModel WarfrontDuelModel => _wfDuel;

    /// <summary>The finale's sections (C4c's forward): kinds 16 and 17 into the model; anything else of 16 to 31 is refused.</summary>
    partial void WarfrontChaosDuelSection(byte tag, ReadOnlySpan<byte> payload, ref bool handled)
    {
        if (tag != WarfrontChaosWire.Tag) return;
        handled = true;
        WarfrontDuelApply(payload, _connectedFor);
    }

    void WarfrontDuelApply(ReadOnlySpan<byte> payload, double now)
    {
        if (!_wfDuel.Apply(payload, now)) { WarfrontDroppedChaos++; return; }
        if (_wfDuel.State is { Phase: not WarfrontDuelPhase.Bodyguard } && !_wfBreach.SilentCloses)
        {
            _wfBreach.SilentCloses = true;   // C4c's contract: the duel's CloseAll is silent
            GD.Print($"t {now:F1} s: warfront chaos: the finale's relay is on; breach closures from now are the duel's silent close");
        }
        if (_wfDuel.PhaseChanged && _wfDuel.State is { } s)
            GD.Print($"t {now:F1} s: warfront chaos: duel {s.Phase}: champion P{s.Champion}, next P{s.Next}, {s.Remaining} waiting, attempt {(s.Attempt == 255 ? "-" : (s.Attempt + 1).ToString())}, "
                     + $"the Lord {s.Permille / 10.0:0.#} % ({s.Health:0.###} hp, bits 0x{BitConverter.SingleToInt32Bits(s.Health):X8})");
        foreach (var t in _wfDuel.NewTelegraphs)
        {
            GD.Print($"t {now:F1} s: warfront chaos: the Lord's {AbilityName(t.Told.Ability)} at {t.Told.Position.X:F1},{t.Told.Position.Y:F1},{t.Told.Position.Z:F1} in {t.Told.Seconds:0.0} s (tick {t.Told.Tick})");
            if (WarfrontDuelDraws) { WarfrontDuelEnsureDriver(); _wfDuelFx?.Add(t); }
        }
        WarfrontDuelEnsureDriver();
    }

    static string AbilityName(byte ability) => ability switch
    {
        WarfrontLordTelegraph.Slam => "slam",
        WarfrontLordTelegraph.Charge => "charge",
        WarfrontLordTelegraph.Bolt => "bolt",
        WarfrontLordTelegraph.Leap => "leap",
        _ => "?",
    };

    bool WarfrontDuelDraws => !Lean && !_bot && _gameSpace is not null && DisplayServer.GetName() != "headless";

    /// <summary>The frame node under the game space (made with the first section of a map; it goes with the map), the telegraphs' node under it, the HUD in Warfront's 1920 by 1080 root.</summary>
    void WarfrontDuelEnsureDriver()
    {
        if (_gameSpace is null) return;
        if (_wfDuelDriver is not { } d || !IsInstanceValid(d) || !d.IsInsideTree())
        {
            _wfDuelDriver = new ChaosBreachDriver { Name = "WarfrontChaosDuel", Step = WarfrontDuelFrame, Gone = WarfrontDuelGone };
            _gameSpace.AddChild(_wfDuelDriver);
            _wfDuelFx = null;
        }
        if (!WarfrontDuelDraws) return;
        if (_wfDuelFx is null || !IsInstanceValid(_wfDuelFx))
        {
            _wfDuelFx = new LordTelegraphFx { Name = "ChaosLordTelegraphs", Now = () => _connectedFor };
            _wfDuelDriver.AddChild(_wfDuelFx);
        }
        if ((_wfDuelHud is null || !IsInstanceValid(_wfDuelHud)) && _hudView is not null)
        {
            _wfDuelHud = new DuelHud();
            WarfrontFitRoot(_hudView).AddChild(_wfDuelHud);
        }
    }

    /// <summary>Once a frame while a duel section has come this map: the model's clock, the HUD's lines, the spectator HUD kept out of the strip's way.</summary>
    void WarfrontDuelFrame(double dt)
    {
        double now = _connectedFor;
        if (_wfDuel.Tick(now)) WarfrontDuelEnded(now, "no duel state for 3 s");
        if (_wfDuelHud is not { } hud || !IsInstanceValid(hud)) return;
        ushort own = _client is { PlayerId: > 0 } c ? (ushort)Math.Clamp(c.PlayerId, 0, ushort.MaxValue) : (ushort)0;
        var lines = _wfDuel.Lines(own, id => HudName(id), now);
        hud.Show(lines, _wfBattle is { Visible: true });
        if (_wfSpectateHud is { } spectate && IsInstanceValid(spectate)) spectate.Modulate = lines.Shown ? new Color(1f, 1f, 1f, 0f) : Colors.White;   // the duel strip stands where its strip would
    }

    void WarfrontDuelEnded(double now, string why)
    {
        GD.Print($"t {now:F1} s: warfront chaos: the duel HUD cleared ({why})");
        _wfBreach.SilentCloses = false;
        if (_musouHud is { } musouHud) musouHud.WarfrontHideCommander = false;
        _wfDuelFx?.ClearAll();
        if (_wfDuelHud is { } hud && IsInstanceValid(hud)) hud.Show(default, false);
        if (_wfSpectateHud is { } spectate && IsInstanceValid(spectate)) spectate.Modulate = Colors.White;
    }

    /// <summary>The frame node went with its map: the finale is forgotten.</summary>
    void WarfrontDuelGone()
    {
        if (_wfDuel.State is not null) WarfrontDuelEnded(_connectedFor, "the map went");
        _wfDuel.Clear();
        _wfDuelFx = null;
    }

    /// <summary>
    /// The spectator camera during a duel (the forward at the top of Warfront's spectator view): everyone but the champion watches him from behind and above (<see cref="WarfrontSpectatePose"/>), when his body is in
    /// the snapshot; otherwise the normal views. <paramref name="taken"/> says the duel owns the view.
    /// </summary>
    partial void WarfrontChaosDuelSpectate(PlayerState me, ref (Vector3 Eye, Vector3 Target)? view, ref bool taken)
    {
        WarfrontDuelDemoFrame();
        if (_client is not { } c || _gameSpace is null || c.PlayerId <= 0) return;
        double now = _connectedFor;
        if (!_wfDuel.Watching((ushort)c.PlayerId, now) || _wfDuel.State is not { } s) return;
        foreach (var p in c.RemotePlayers().Players)
            if (p.PlayerId == s.Champion && p.Alive)
            {
                view = WarfrontSpectatePose(SpectateG(p.Position), p.Yaw);
                taken = true;
                if (_wfDuelWatching != s.Champion) { _wfDuelWatching = s.Champion; GD.Print($"t {now:F1} s: warfront chaos: spectating the champion P{s.Champion} (the duel camera)"); }
                return;
            }
    }
}
