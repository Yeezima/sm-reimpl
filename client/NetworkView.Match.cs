using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// M15 (protocol 10): teams and Annihilation in the networked view. <c>--side marines|chaos</c> asks for a side
/// (<see cref="ClientOptions.RequestedSide"/>; the server may balance the player onto the other side with that side's
/// equivalent class, printed once the first scoreboard names the player). Nameplates are coloured by relation (own green,
/// ally blue, enemy red; white without teams), every marine gets a ring at its feet in its side's colour (Space Marines
/// blue, Chaos orange); Chaos players wear the original Chaos armour (M17; a red tint only if it fails to load). The match banner (top right) shows warm-up, the time left (counted down locally from
/// the snapshots' ticks left), the end with the winner and the restart countdown, and both team scores with the limit;
/// the kill feed (under it) keeps the last <see cref="KillFeedEntries"/> deaths for <see cref="KillFeedSeconds"/> s;
/// the scoreboard (Tab held, or shown while the match has ended, or always with <c>--show-scoreboard</c>) lists each
/// side's players by kills. All of it is a **placeholder demonstration** (the original HUD, scoreboard and colours are
/// not reproduced); the rules and numbers behind it come from the server.
/// </summary>
public partial class NetworkView
{
    static readonly Color OwnColour = new(0.6f, 1f, 0.6f);
    static readonly Color AllyColour = new(0.45f, 0.75f, 1f);
    static readonly Color EnemyColour = new(1f, 0.38f, 0.32f);
    static readonly Color MarineSideColour = new(0.2f, 0.45f, 1f);
    static readonly Color ChaosSideColour = new(1f, 0.55f, 0.1f);

    /// <summary>The side asked for with <c>--side</c> (<see cref="Team.None"/>: the class's side).</summary>
    public Team RequestedSide { get; init; }
    /// <summary><c>--show-scoreboard</c> (test aid): the scoreboard is always shown.</summary>
    public bool ShowScoreboard { get; init; }
    /// <summary><c>--wait-match-end</c> (test aid): the screenshot waits until a match has ended (and the board is up).</summary>
    public bool WaitMatchEnd { get; init; }

    PanelContainer? _scoreboardPanel;
    RichTextLabel? _scoreboardText;
    MatchView _lastMatch;
    bool _matchSeen;
    double _matchSeenAt;
    double? _endedSince;
    bool _classReported;
    int _killsSeen;
    StandardMaterial3D? _chaosTint;

    /// <summary>Parses <c>--side</c>: "marines", "sm", "space marines" or "chaos", "csm"; null for anything else.</summary>
    public static Team? ParseSide(string? text)
    {
        string s = string.Concat((text ?? "").ToLowerInvariant().Where(char.IsLetter));
        return s switch
        {
            "marines" or "marine" or "sm" or "spacemarines" or "spacemarine" or "1" => Team.SpaceMarines,
            "chaos" or "csm" or "chaosspacemarines" or "2" => Team.Chaos,
            "any" or "" => Team.None,
            _ => null,
        };
    }

    static string SideName(Team t) => t switch { Team.SpaceMarines => "Space Marines", Team.Chaos => "Chaos", _ => "no side" };

    /// <summary>The Godot font for the scoreboard's columns (a system monospace font).</summary>
    static readonly SystemFont MonoFont = new() { FontNames = ["Consolas", "Cascadia Mono", "Courier New", "monospace"] };

    void AddMatchUi(CanvasLayer layer)
    {
        // Centre: the scoreboard.
        _scoreboardPanel = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            OffsetLeft = -526, OffsetRight = 526, OffsetTop = -40, OffsetBottom = 40,   // grows with the text (names/bot-lore: 31-character name column)
            GrowHorizontal = Control.GrowDirection.Both, GrowVertical = Control.GrowDirection.Both,
            Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _scoreboardPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.03f, 0.03f, 0.03f, 0.86f), BorderColor = new Color(1f, 0.8f, 0f, 0.9f),
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 12, ContentMarginBottom = 12,
        });
        _scoreboardText = new RichTextLabel { MouseFilter = Control.MouseFilterEnum.Ignore, BbcodeEnabled = true, FitContent = true, ScrollActive = false };
        _scoreboardText.AddThemeFontOverride("normal_font", MonoFont);
        _scoreboardText.AddThemeFontSizeOverride("normal_font_size", 20);
        _scoreboardPanel.AddChild(_scoreboardText);
        layer.AddChild(_scoreboardPanel);
        AddMatchFlowUi(layer);   // match flow: the countdown, the splash and the next-map timer, above the HUD and the scoreboard
    }

    /// <summary>The player's side as last seen: own predicted state, a remote's interpolated one, else the scoreboard's.</summary>
    Team TeamOf(int playerId)
    {
        if (_client is null) return Team.None;
        if (playerId == _client.PlayerId) return _client.Predicted.Team;
        foreach (var o in _lastOthers) if (o.PlayerId == playerId) return o.Team;
        if (_client.Scoreboard is { } b) foreach (var e in b.Players) if (e.PlayerId == playerId) return e.Team;
        return Team.None;
    }

    int? ClassOf(int playerId)
    {
        if (_client is null) return null;
        if (playerId == _client.PlayerId) return _client.Predicted.Class;
        foreach (var o in _lastOthers) if (o.PlayerId == playerId) return o.Class;
        if (_client.Scoreboard is { } b) foreach (var e in b.Players) if (e.PlayerId == playerId) return e.Class;
        return null;
    }

    /// <summary>Colour of a player's name for this viewer: own green; with teams ally blue, enemy red; without teams white.</summary>
    Color RelationColour(int playerId, Team team)
    {
        if (_client is not null && playerId == _client.PlayerId) return OwnColour;
        var mine = _client?.Predicted.Team ?? Team.None;
        if (mine == Team.None || team == Team.None) return Colors.White;
        return World.Hostile(mine, team) ? EnemyColour : AllyColour;
    }

    /// <summary>
    /// The nameplate colour, the side ring and the Chaos tint of one marine (placeholders), changed only when its side or
    /// the viewer's side changes.
    /// </summary>
    void ApplyTeamLook(CharacterViewer character, int playerId, Team team)
    {
        var mine = _client?.Predicted.Team ?? Team.None;
        string name = HudName(playerId);   // names/bot-lore: the nameplate follows the scoreboard's name
        string key = $"{team}/{mine}/{name}/{ZoneMarkers}";   // observer mode: H (HUD off) changes it, so the tags and rings are set again
        if (character.HasMeta("team_look") && character.GetMeta("team_look").AsString() == key) return;
        character.SetMeta("team_look", key);
        // Fidelity gap 10: the original's world decorators (the HUD, FillDecorators) replace the placeholder name tags and side rings; those stay
        // as test aids with --show-markers (and --no-markers still hides them).
        if (character.GetNodeOrNull<Label3D>("Nameplate") is { } plate)
        {
            plate.Modulate = RelationColour(playerId, team);
            plate.Text = _client is not null && playerId == _client.PlayerId ? $"{name} (you)" : name;
            plate.Visible = ZoneMarkers && _client is not null && playerId != _client.PlayerId && mine != Team.None && team == mine;
        }
        if (character.GetNodeOrNull<MeshInstance3D>("SideRing") is { } ring)
        {
            ring.Visible = team != Team.None && ZoneMarkers;
            if (team != Team.None) ring.MaterialOverride = GlowMaterial(team == Team.Chaos ? ChaosSideColour : MarineSideColour, 1.5f);
        }
        _chaosTint ??= new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(0.75f, 0.06f, 0.04f, 0.38f),
        };
        // The tint is only the fallback for a Chaos player drawn with the Space Marine model (the Chaos model failed to load).
        character.SetOverlay(team == Team.Chaos && ReferenceEquals(character.Assets, _assets) ? _chaosTint : null);
    }

    /// <summary>A ring lying at the marine's feet (game space, Z up), hidden until the player's side is known.</summary>
    static MeshInstance3D NewSideRing() => new()
    {
        Name = "SideRing",
        Mesh = new TorusMesh { InnerRadius = 0.6f, OuterRadius = 0.72f, Rings = 32, RingSegments = 8 },
        // The torus's axis (local +Y) along game +Z.
        Transform = new Transform3D(new Basis(new Vector3(1, 0, 0), MathF.PI / 2f), new Vector3(0, 0, 0.06f)),
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        Visible = false,
    };

    /// <summary>
    /// ui/hud-3 (owner, 2026-10-30): the world decorator is only the aimed player's: the name over the living player the crosshair is on (red for an enemy, yellow with the race icon for a
    /// friend), nothing otherwise and nothing for the rest of the players. Replaces the earlier friends-in-range list (fidelity gap 10) and the "no names over enemies" rule.
    /// Visibility by <see cref="DecoratorRules.PlayerVisible"/>; the position is the shown (interpolated) one, 2.5 m over the feet.
    /// </summary>
    void FillDecorators(Hud.Frame f, PlayerState me, PlayerState? aimed)
    {
        // Shown only while playing as a living player: not spectating, dead, or in the countdown, warm-up and match-over phases.
        if (aimed is null || _camera is null || _gameSpace is null || _client is null || me.Team == Team.None || !me.Alive || Spectate is not null
            || _client.Match.State != MatchState.Playing) return;
        var other = aimed.Value;
        if (!other.Alive || other.PlayerId == _client.PlayerId || other.Team == Team.None) return;
        var relation = World.Hostile(me.Team, other.Team) ? DecoratorRelation.Enemy : DecoratorRelation.Ally;
        float distance = System.Numerics.Vector3.Distance(other.Position, me.Position);
        if (!DecoratorRules.PlayerVisible(relation, distance, true)) return;   // the crosshair's ray is the line of sight
        var anchor = _gameSpace.Transform * new Vector3(other.Position.X, other.Position.Y, other.Position.Z + 2.5f);
        if (_camera.IsPositionBehind(anchor)) return;
        f.Decorators.Add(new Hud.Decorator
        {
            Name = HudName(other.PlayerId), Rel = relation == DecoratorRelation.Enemy ? Hud.Rel.Enemy : Hud.Rel.Own, Team = other.Team,
            Screen = _camera.UnprojectPosition(anchor), Distance = distance,
        });
        if (_bot && (int)(_connectedFor / 4.0) != _decoratorLogSlot)
        {
            _decoratorLogSlot = (int)(_connectedFor / 4.0);
            GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} aimed decorator: {(relation == DecoratorRelation.Enemy ? "enemy" : "ally")}");
        }
    }

    int _decoratorLogSlot = -1;

    /// <summary>Hook for added content (docs/added-content.md): each kill-feed entry, once, as the feed takes it. Implemented in an <c>Extras/</c> folder.</summary>
    partial void ExtrasKill(KillView k);

    /// <summary>Per frame: the class report, the kill feed, the match banner and the scoreboard.</summary>
    void UpdateMatchUi()
    {
        if (_client is null || _config is null) return;
        bool connected = _client.Status == ClientStatus.Connected;
        if (!connected)
        {
            if (_scoreboardPanel is not null) _scoreboardPanel.Visible = false;
            _flowView?.SetFrame(MatchFlowFrame.None);   // fix/connect-timeout: no match-end UI before (or after) a connection
            return;
        }

        ReportClass();
        var match = _client.Match;
        if (!_matchSeen || match != _lastMatch) { _lastMatch = match; _matchSeen = true; _matchSeenAt = _connectedFor; }
        if (match.State == MatchState.Ended) _endedSince ??= _connectedFor; else _endedSince = null;

        RoundStatsGate(match.State);   // fidelity gap 4: the battle record's statistics start afresh with each round
        foreach (var k in _client.TakeKills())
        {
            NoteKillForStats(k, match.State);
            string text = KillText(k);
            NoteDeathCause(k);   // M23: the ragdoll's push
            OrkKillSound(k);     // Ork O8: an Ork standing still taunts after a kill (silly option)
            ExtrasKill(k);       // EXTRAS-HOOK: added content's kill reactions (loyalist barks: the killer's bark)
            _hudView?.AddKill(HudKill(k));
            _killsSeen++;
            // A kill by the own player: a reward-style line (the hit marker comes from the hit reports, UpdateHits) (no experience points exist here).
            if (k.Killer == _client.PlayerId && k.Victim != k.Killer)
            {
                _hudView?.AddEvent($"+ Kill {HudName(k.Victim)}");
            }
            if (_bot)
                GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} kill feed #{k.Sequence}: {StripBbcode(text)} " +
                         $"(killer P{k.Killer} {SideName(TeamOf(k.Killer))}, victim P{k.Victim} {SideName(TeamOf(k.Victim))}; cause {k.Cause} {k.Index}; " +
                         $"score marines {match.MarineScore} chaos {match.ChaosScore}; kills seen {_killsSeen})");
        }

        NoteCapturesForStats(match.State);
        UpdateMatchFlow(match);   // before the board: the splash, the count-up and the battle record hold the scoreboard back (MatchEndTimeline)
        bool board = ShowScoreboard || (match.State == MatchState.Ended && EndBoardUp) || (!_bot && Input.IsPhysicalKeyPressed(Key.Tab)) || PadScoreboardHeld;
        if (_scoreboardPanel is not null && _scoreboardText is not null)
        {
            _scoreboardPanel.Visible = board;
            if (board) _scoreboardText.Text = ScoreboardText();
        }

        if (_bot && (int)(_connectedFor / 5.0) != (int)((_connectedFor - GetProcessDeltaTime()) / 5.0))
            GD.Print($"t {_connectedFor:F0} s: id {_client.PlayerId} side {SideName(_client.Predicted.Team)} match: {MatchText(match).Replace('\n', ';')}" +
                     (SeizeGround ? $"; points {PointsLogText()}; bot {_botSeizeNote}" : "") +
                     (_client.Scoreboard is { } sb ? $"; board: {string.Join(", ", sb.Players.Select(e => $"P{e.PlayerId}{(e.Name.Length > 0 ? $" \"{e.Name}\"" : "")} {SideName(e.Team)} '{ClassLabel(e.Class)}' {e.Kills}/{e.Deaths}"))}" : ""));
    }

    /// <summary>Once the scoreboard names this player: the class and side actually given (the server may balance them).</summary>
    void ReportClass()
    {
        if (_classReported || _client?.Scoreboard is not { } board) return;
        foreach (var e in board.Players)
        {
            if (e.PlayerId != _client.PlayerId) continue;
            _classReported = true;
            bool askedClass = _requestedClass != Protocol.ServerDefaultClass;
            bool classChanged = askedClass && e.Class != _requestedClass;
            var classSide = askedClass && _config is not null && _requestedClass < _config.Classes.Count ? _config.Classes[_requestedClass].Side : Team.None;
            var wanted = RequestedSide != Team.None ? RequestedSide : classSide;
            string why = wanted != Team.None && e.Team != Team.None && e.Team != wanted
                ? "; the server balanced this player onto the other side with that side's equivalent class"
                : classChanged ? "; the requested side's equivalent class" : "";
            GD.Print($"playing class {e.Class} '{ClassLabel(e.Class)}' on the {SideName(e.Team)} side " +
                     $"(requested class {(askedClass ? $"{_requestedClass} '{ClassLabel(_requestedClass)}'" : "server default")}, side {(RequestedSide == Team.None ? "the class's" : SideName(RequestedSide))})" + why);
            return;
        }
    }

    /// <summary>"Brother Aurex [bolter] Skarne the Unbroken" (names/bot-lore; "P3" for a player without a name), with suicide, team kill and fall wording; names coloured by relation (BBCode).</summary>
    string KillText(KillView k)
    {
        string Name(int id) => $"[color=#{RelationColour(id, TeamOf(id)).ToHtml(false)}]{Esc(HudName(id))}{(_client is not null && id == _client.PlayerId ? " (you)" : "")}[/color]";
        if (k.Cause == KillCause.World && (k.Killer == 0 || k.Killer == k.Victim)) return $"{Name(k.Victim)} fell out of the level";
        string weapon = Esc($"[{CauseLabel(k)}]");
        if (k.Killer == 0) return $"{weapon} {Name(k.Victim)}";
        if (k.Killer == k.Victim) return $"{Name(k.Victim)} {weapon} suicide";
        var killerTeam = TeamOf(k.Killer);
        bool teamKill = killerTeam != Team.None && killerTeam == TeamOf(k.Victim);
        return $"{Name(k.Killer)} {weapon} {Name(k.Victim)}{(teamKill ? " (team kill)" : "")}";
    }

    /// <summary>A kill as the HUD's feed shows it: killer, weapon tag, victim (own side gold, enemy red).</summary>
    Hud.Kill HudKill(KillView k)
    {
        string weapon = k.Cause == KillCause.World ? "fall" : CauseLabel(k);
        string wargear = CauseWargear(k);   // M44: the weapon's picture in the feed
        if (k.Cause == KillCause.World && (k.Killer == 0 || k.Killer == k.Victim)) return new Hud.Kill("", Hud.Rel.None, "", HudName(k.Victim), HudRelation(k.Victim), " fell");
        if (k.Killer == 0) return new Hud.Kill("", Hud.Rel.None, weapon, HudName(k.Victim), HudRelation(k.Victim), "", wargear);
        if (k.Killer == k.Victim) return new Hud.Kill("", Hud.Rel.None, weapon, HudName(k.Victim), HudRelation(k.Victim), " (suicide)", wargear);
        var killerTeam = TeamOf(k.Killer);
        bool teamKill = killerTeam != Team.None && killerTeam == TeamOf(k.Victim);
        return new Hud.Kill(HudName(k.Killer), HudRelation(k.Killer), weapon, HudName(k.Victim), HudRelation(k.Victim), teamKill ? " (team kill)" : "", wargear);
    }

    /// <summary>The wargear file of a kill's weapon (for its picture), or empty: weapons, thrown items, the killer's melee choice, the jump pack.</summary>
    string CauseWargear(KillView k)
    {
        var c = _config!;
        switch (k.Cause)
        {
            case KillCause.Weapon: return k.Index >= 0 && k.Index < c.Weapons.Count ? c.Weapons[k.Index].Name : "";
            case KillCause.Thrown: return k.Index >= 0 && k.Index < c.Thrown.Count ? c.Thrown[k.Index].Name : "";
            case KillCause.Melee:
                if (ClassOf(k.Killer) is { } cls && Loadouts.ClassOf(c, cls) is { } loadout && loadout.MeleeChoices.Count > 0)
                    return loadout.MeleeChoices[k.Index >= 0 && k.Index < loadout.MeleeChoices.Count ? k.Index : 0].Wargear;
                return "";
            case KillCause.JumpPack: return k.Index >= 0 && k.Index < c.JumpPacks.Count ? c.JumpPacks[k.Index].Name : "";
            case KillCause.Perk: return k.Index >= 0 && k.Index < c.Perks.Count ? c.Perks[k.Index].Name : "";   // fidelity gap 10: the perk's blast file names its feed picture (killed_by_weapon_type)
            default: return "";
        }
    }

    /// <summary>The weapon name of a kill: <see cref="SimConfig.Weapons"/>, <see cref="SimConfig.Thrown"/>, the killer's melee choice, the jump pack, or the level.</summary>
    string CauseLabel(KillView k)
    {
        var c = _config!;
        switch (k.Cause)
        {
            case KillCause.Weapon: return k.Index >= 0 && k.Index < c.Weapons.Count ? WeaponLabel(c.Weapons[k.Index]) : "weapon";
            case KillCause.Thrown: return ThrownLabel(k.Index);
            case KillCause.Melee:
                if (ClassOf(k.Killer) is { } cls && Loadouts.ClassOf(c, cls) is { } loadout && loadout.MeleeChoices.Count > 0)
                {
                    int choice = k.Index >= 0 && k.Index < loadout.MeleeChoices.Count ? k.Index : 0;   // -1: the class default (the first)
                    return WeaponLabel(loadout.MeleeChoices[choice].Wargear);
                }
                return "melee";
            case KillCause.JumpPack: return k.Index >= 0 && k.Index < c.JumpPacks.Count ? "jump pack (" + WeaponLabel(c.JumpPacks[k.Index].Name) + ")" : "jump pack";
            case KillCause.Perk: return k.Index >= 0 && k.Index < c.Perks.Count ? Perks.NameOf(c.Perks[k.Index]) : "perk";   // M43: Final Vengeance, Reactive Blast, Blast Off
            default: return "fall";
        }
    }

    static string Esc(string s) => string.Concat(s.Select(ch => ch == '[' ? "[lb]" : ch == ']' ? "[rb]" : ch.ToString()));

    static string StripBbcode(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '[') { sb.Append(s[i]); continue; }
            int end = s.IndexOf(']', i);
            if (end < 0) { sb.Append(s[i..]); break; }
            string tag = s[(i + 1)..end];
            sb.Append(tag == "lb" ? "[" : tag == "rb" ? "]" : "");
            i = end;
        }
        return sb.ToString();
    }

    /// <summary>Seconds left in the current phase, counted down locally from the newest snapshot's ticks left.</summary>
    double SecondsLeft(MatchView match) =>
        Math.Max(0.0, match.TicksLeft / (double)_config!.TickRate - (_connectedFor - _matchSeenAt));

    static string Clock(double seconds)
    {
        int s = (int)Math.Ceiling(Math.Clamp(seconds, 0.0, 359_999.0));   // at most 99:59:59 shown as minutes
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>The banner: state line, then the team scores with the limit.</summary>
    string MatchText(MatchView match)
    {
        int? limit = _client?.Scoreboard is { Teams: true } b ? b.ScoreLimit : null;
        bool seize = match.Mode == GameMode.SeizeGround;
        string mode = seize ? "Seize Ground" : "Annihilation";
        // Seize Ground: the points each side holds after its score (M16).
        string held(Team t) => seize && _client is not null ? $" [{_client.Points.Count(p => p.Owner == t)}]" : "";
        string scores = $"Space Marines {match.MarineScore}{held(Team.SpaceMarines)} – {match.ChaosScore}{held(Team.Chaos)} Chaos  (to {(limit is { } l ? l.ToString() : "?")})";
        return match.State switch
        {
            MatchState.None => "Free-for-all (no teams)",
            MatchState.Warmup when match.IsCountdown() => $"{mode}: MATCH BEGINS {MatchFlow.DisplaySeconds(SecondsLeft(match))}\n" + scores,
            MatchState.Warmup => $"{mode}: WARMUP — waiting for both sides\n" + scores,
            MatchState.Playing => (match.TicksLeft == uint.MaxValue ? $"{mode} — no time limit" : $"{mode}   {Clock(SecondsLeft(match))}") + "\n" + scores,
            _ => $"MATCH OVER — {(match.Winner == Team.None ? "draw" : SideName(match.Winner) + " win")}   next match in {Math.Ceiling(SecondsLeft(match)):F0} s\n" + scores,
        };
    }

    /// <summary>The scoreboard's name column (names/bot-lore: <see cref="PlayerNames.MaxLength"/>, " (you)" and a space: <see cref="PlayerNames.ScoreboardColumn"/>).</summary>
    const int NameColumn = PlayerNames.ScoreboardColumn;

    const string GoldTag = "[color=#ffcc00]", RedTag = "[color=#e01414]", GreyTag = "[color=#9a9a9a]", EndTag = "[/color]";

    /// <summary>
    /// The scoreboard as BBCode (dark panel, gold header; own side gold, enemy red): per side (or one list without
    /// teams), players by kills (then fewer deaths, then id).
    /// </summary>
    string ScoreboardText()
    {
        var board = _client?.Scoreboard;
        var match = _client!.Match;
        var sb = new StringBuilder();
        if (board is null) return GoldTag + "SCOREBOARD" + EndTag + "\n\n(waiting for the server's scoreboard)";
        string limits = $"score limit {board.ScoreLimit}   time limit {(board.TimeLimit > 0f ? Clock(board.TimeLimit) : "none")}   friendly fire {(board.FriendlyFire ? "on" : "off")}";
        sb.AppendLine(GoldTag + (board.Seize is { } seize
            ? $"SEIZE GROUND   {limits}{EndTag}\n{GreyTag}capture {seize.CaptureTime:0.#} s   neutralise {seize.NeutraliseTime:0.#} s   {seize.PointScore} per point every {seize.ScoreInterval:0.#} s\npoints: {PointsLogText()}{EndTag}"
            : board.Teams ? $"ANNIHILATION   {limits}{EndTag}" : $"FREE-FOR-ALL{EndTag}"));
        var sides = board.Teams ? new[] { Team.SpaceMarines, Team.Chaos } : new[] { Team.None };
        var mine = _client.Predicted.Team;
        foreach (var side in sides)
        {
            sb.AppendLine();
            var rows = board.Players.Where(e => !board.Teams || e.Team == side)
                .OrderByDescending(e => e.Kills).ThenBy(e => e.Deaths).ThenBy(e => e.PlayerId).ToList();
            int score = side == Team.SpaceMarines ? match.MarineScore : side == Team.Chaos ? match.ChaosScore : 0;
            string tag = !board.Teams || mine == Team.None ? GoldTag : side == mine ? GoldTag : RedTag;
            sb.AppendLine(tag + (board.Teams ? $"{SideName(side).ToUpperInvariant()}   {score}{(match.State == MatchState.Ended && match.Winner == side ? "   WINNER" : "")}" : "PLAYERS") + EndTag);
            // The original's columns (SummaryScreen.gfx player_header_mc: tdm LEVEL, PLAYER, XP, KILLS, DEATHS, RATIO, ASSISTS; domination ... SEIZED, DEFENDED),
            // headed with the install's words; level, score, assists, captures and defends are not on the wire (a dash), the ratio is computed.
            var t = _summaryText;
            bool seizeMode = board.Seize is not null;
            string Cell(string text, int width) => text.Length >= width ? text[..(width - 1)] + " " : text.PadLeft(width);
            string extraHead = seizeMode ? Cell(t.Seized, 9) + Cell(t.Defended, 10) : Cell(t.Ratio, 8) + Cell(t.Assists, 9);
            sb.AppendLine($"{GreyTag}    {PlayerNames.Cell(t.Player, NameColumn)}{"CLASS",-20}{Cell(t.Kills, 7)}{Cell(t.Deaths, 8)}{extraHead}{EndTag}");
            foreach (var e in rows)
            {
                bool me = e.PlayerId == _client.PlayerId;
                string extra = seizeMode ? Cell("—", 9) + Cell("—", 10)
                    : Cell(BattleRecord.Ratio(e.Kills, e.Deaths).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), 8) + Cell("—", 9);
                // names/bot-lore: a name of up to 24 characters, " (you)" and a space fit the name column (31; PlayerNameTests)
                string line = $"{(me ? " >  " : "    ")}{Esc(PlayerNames.Cell(HudName(e.PlayerId) + (me ? " (you)" : ""), NameColumn))}{Esc(Trim(ClassLabel(e.Class), 19)),-20}{e.Kills,7}{e.Deaths,8}{extra}";
                sb.AppendLine(me ? GoldTag + line + EndTag : line);
            }
            if (rows.Count == 0) sb.AppendLine("    (nobody)");
        }
        if (board.Spectators is { Length: > 0 } watching)
            sb.AppendLine("\n" + GreyTag + "SPECTATORS: " + string.Join(", ", watching.Select(s => Esc(s.Name.Length > 0 ? s.Name : $"Spectator {s.PlayerId}"))) + EndTag);   // protocol 46
        sb.Append(GreyTag + "\n(Tab: scoreboard; columns after the original's; level, score, assists, captures and defends are not tracked yet: a dash)" + EndTag);
        return sb.ToString();

        static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "...";
    }

    /// <summary>
    /// <c>--wait-match-end</c>: true once a match has been over for 2.5 s (the splash word is in; the scoreboard follows after
    /// <see cref="MatchFlow.SplashSeconds"/>); used by <see cref="ReadyForScreenshot"/>.
    /// </summary>
    bool MatchEndWorthCapturing => _endedSince is { } since && _connectedFor - since >= 2.5;
}
