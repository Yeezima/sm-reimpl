using System;
using System.Collections.Generic;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Coop;

namespace SmClient.Extras.Warfront.Coop;

/// <summary>
/// The co-op view-model (slice W12; no Godot, no sockets): holds the last Coop section (envelope tag 46), the fallen player's Down state (W5a's Down section, fed through <see cref="ApplyDown"/>), the names the client
/// resolves (the watch target's, the leader's) and answers every text the spectator HUD and the join panel show. The feed calls <see cref="Apply"/> with each section; the panels redraw on <see cref="Changed"/>; presses go
/// out through <see cref="ConfirmRequested"/> and <see cref="WatchRequested"/> (the view sends command tag 42). All text is capitals, as the hub's (ours).
/// </summary>
public sealed class WarfrontCoopModel
{
    WarfrontCoopWire.Section _s = new(WarfrontCoopRole.Player, WarfrontWatchKind.None, 0, 0, 0, Array.Empty<WarfrontAwaited>());
    WarfrontDownPhase _down;
    float _downSeconds;
    string _watchName = "", _leaderName = "";
    int _grantedLevel = 1;
    bool _customizerOpened;
    string _announcement = "";
    double _announcedAt = double.NegativeInfinity, _clock;

    public WarfrontCoopWire.Section Section => _s;
    public WarfrontCoopRole Role => _s.Role;
    public int GrantedLevel => _grantedLevel;
    /// <summary>The viewer's own name as the scoreboard shows it: its row in the awaited list reads YOU.</summary>
    public string OwnName { get; set; } = "";
    public WarfrontDownPhase DownPhase => _down;

    /// <summary>Something drawn changed.</summary>
    public event Action? Changed;
    /// <summary>The player closed the customizer while awaiting its class: send ConfirmClass.</summary>
    public event Action? ConfirmRequested;
    /// <summary>The player pressed the spectate keys: send Watch with the step (-1 previous, +1 next).</summary>
    public event Action<int>? WatchRequested;

    /// <summary>Feed: a Coop section arrived. The "leader changed" flag turning on raises one announcement line.</summary>
    public void Apply(WarfrontCoopWire.Section s, string leaderName, string watchName, int level, int picksLeft = 0)
    {
        bool announce = s.LeaderChanged && !_s.LeaderChanged;
        bool changed = !SameSection(_s, s) || leaderName != _leaderName || watchName != _watchName || level != _grantedLevel || picksLeft != PicksLeft;
        if (_s.Role != WarfrontCoopRole.AwaitClass && s.Role == WarfrontCoopRole.AwaitClass) _customizerOpened = false;
        _s = s; _leaderName = leaderName; _watchName = watchName; _grantedLevel = level; PicksLeft = picksLeft;
        if (announce)
        {
            _announcement = s.YouLead ? "YOU LEAD THE SQUAD" : $"{(leaderName.Length > 0 ? leaderName.ToUpperInvariant() : "A PLAYER")} NOW LEADS THE SQUAD";
            _announcedAt = _clock;
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    static bool SameSection(in WarfrontCoopWire.Section a, in WarfrontCoopWire.Section b)
    {
        if (a.Role != b.Role || a.WatchKind != b.WatchKind || a.WatchId != b.WatchId || a.Flags != b.Flags || a.GateSecondsLeft != b.GateSecondsLeft || a.Awaited.Length != b.Awaited.Length) return false;
        for (int i = 0; i < a.Awaited.Length; i++) if (a.Awaited[i] != b.Awaited[i]) return false;
        return true;
    }

    /// <summary>Feed: W5a's Down section (the fallen player's phase and the seconds left of it).</summary>
    public void ApplyDown(WarfrontDownPhase phase, float seconds)
    {
        if (phase == _down && Math.Abs(seconds - _downSeconds) < 0.5f) return;
        _down = phase; _downSeconds = seconds;
        Changed?.Invoke();
    }

    /// <summary>Advances the announcement's life (the line stays <see cref="WarfrontCoop.LeaderChangedSeconds"/> seconds).</summary>
    public void Tick(double dt)
    {
        _clock += dt;
        if (_announcement.Length > 0 && _clock - _announcedAt >= WarfrontCoop.LeaderChangedSeconds) { _announcement = ""; Changed?.Invoke(); }
    }

    // ---- the customizer (the hook the lead routes: WarfrontCustomizerClosed) ----------------------------------------------------------------

    /// <summary>The player opened the customizer on the ship.</summary>
    public void CustomizerOpened() { if (Role == WarfrontCoopRole.AwaitClass) _customizerOpened = true; }

    /// <summary>The place the hub customizer closes calls this: in AwaitClass, after it was opened, the class is confirmed.</summary>
    public void CustomizerClosed()
    {
        if (Role != WarfrontCoopRole.AwaitClass || !_customizerOpened) return;
        _customizerOpened = false;
        ConfirmRequested?.Invoke();
    }

    public void StepWatch(int step) { if (Spectating) WatchRequested?.Invoke(step < 0 ? -1 : 1); }

    // ---- the spectator HUD -------------------------------------------------------------------------------------------------------------------

    /// <summary>A fallen player past its revive window, or a joiner held aboard: the spectator camera is up.</summary>
    public bool Spectating => Role == WarfrontCoopRole.SpectatingJoiner || _down == WarfrontDownPhase.Spectator;

    public bool JoinerSpectating => Role == WarfrontCoopRole.SpectatingJoiner;

    public string WatchingText => !Spectating ? "" : _watchName.Length > 0 ? "WATCHING " + _watchName.ToUpperInvariant() : "WATCHING THE SQUAD";

    public string SpectateSubText
    {
        get
        {
            if (!Spectating) return "";
            return JoinerSpectating ? "YOU JOIN AT THE NEXT THUNDERHAWK STOP" : "BACK IN THE FIGHT IN " + Clock(_downSeconds);
        }
    }

    /// <summary>The leader-change line (empty after a few seconds).</summary>
    public string AnnouncementText => _announcement;

    /// <summary>The two keys that change the watched body (Classic's observer keys: F next, Shift+F previous; the mouse buttons do the same).</summary>
    public string WatchKeysText => Spectating ? "F: NEXT VIEW    SHIFT+F: PREVIOUS VIEW" : "";

    public static string Clock(double seconds)
    {
        int s = Math.Max(0, (int)Math.Ceiling(seconds));
        return $"{s / 60}:{s % 60:00}";
    }

    // ---- the join panel ---------------------------------------------------------------------------------------------------------------------

    /// <summary>How to reach the customizer from the ship (the armoury station; the hub's own key hint replaces this when W11 names one).</summary>
    public string JoinHint => Role == WarfrontCoopRole.AwaitClass ? "OPEN IT AT THE ARMOURY STATION ON THE SHIP" : "";

    public bool ShowJoinPanel => Role is WarfrontCoopRole.AwaitClass or WarfrontCoopRole.AwaitPicks;

    public string JoinHeading => Role switch
    {
        WarfrontCoopRole.AwaitClass => _grantedLevel > 1 ? $"YOU JOIN AT LEVEL {_grantedLevel}" : "YOU JOIN THE SQUAD",
        WarfrontCoopRole.AwaitPicks => "CHOOSE YOUR CARDS",
        _ => "",
    };

    public string JoinBody => Role switch
    {
        WarfrontCoopRole.AwaitClass => $"CONFIRM YOUR CLASS IN THE CUSTOMIZER ({Clock(_s.GateSecondsLeft)})",
        WarfrontCoopRole.AwaitPicks => $"{PicksLeft} LEFT",
        _ => "",
    };

    /// <summary>The cards the viewer still has to take (the feed reads it from the viewer's own awaited row, or the progression state).</summary>
    public int PicksLeft { get; private set; }

    /// <summary>The viewer's own picks left in a section: the awaited row of its name with the Picks reason, else 0.</summary>
    public static int PicksLeftFor(in WarfrontCoopWire.Section s, string ownName)
    {
        foreach (var a in s.Awaited) if (a.Reason == WarfrontAwaitReason.Picks && string.Equals(a.Name, ownName, StringComparison.Ordinal)) return a.PicksLeft;
        return 0;
    }

    // ---- the leader's awaited list ----------------------------------------------------------------------------------------------------------

    public bool ShowAwaited => _s.YouLead && _s.Awaited.Length > 0;

    public string AwaitedHeading => "WAITING FOR";

    /// <summary>One (name, reason) pair per awaited player; the panel puts the reasons in one column. The viewer's own row reads YOU.</summary>
    public IReadOnlyList<(string Name, string Reason)> AwaitedRows()
    {
        var list = new List<(string, string)>();
        foreach (var a in _s.Awaited)
        {
            string name = (a.Name.Length > 0 ? a.Name : "PLAYER").ToUpperInvariant();
            if (OwnName.Length > 0 && string.Equals(a.Name, OwnName, StringComparison.Ordinal)) name = "YOU";
            list.Add((name, a.Reason == WarfrontAwaitReason.Class ? "CLASS" : $"{a.PicksLeft} {(a.PicksLeft == 1 ? "CARD" : "CARDS")}"));
        }
        return list;
    }
}
