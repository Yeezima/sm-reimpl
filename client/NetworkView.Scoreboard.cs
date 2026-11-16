using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmContent;
using SmContent.Customization;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// The Tab scoreboard's content (the owner, 2026-11-07; drawn by <see cref="ScoreboardView"/>): per side the team score and a row per player with only the numbers a player wants:
/// Classic PvP KILLS, DEATHS, RATIO, ASSISTS, SCORE and PING (the install's words where it has them); Musou Mode one column with KILLS, DEATHS, SCORE (the run's points) and PING
/// (<see cref="ExtrasScoreboard"/>). Rows by score, then kills, fewer deaths, id. The own side is the left column (Space Marines for a spectator). Each player's icon is its
/// shoulder emblem as it joined (<see cref="JoinIcons{TIcon}"/>), drawn in grey.
/// </summary>
public partial class NetworkView
{
    readonly JoinIcons<ResolvedDecal> _joinIcons = new();
    Scoreboard? _iconBoard;
    readonly Dictionary<Texture2D, ImageTexture> _greyIcons = new();

    /// <summary>Added content's scoreboard (docs/added-content.md): one column, its own score per player, its own columns. Implemented in an <c>Extras/</c> folder.</summary>
    partial void ExtrasScoreboard(ref bool single, ref Func<ScoreEntry, int>? score, ref bool assists);

    /// <summary>On every new board, and while a listed player has no icon yet: the icons of players who joined since, the leavers forgotten.</summary>
    void ObserveJoinIcons()
    {
        if (_client?.Scoreboard is not { } board) return;
        if (ReferenceEquals(board, _iconBoard) && _joinIcons.Count >= board.Players.Length) return;
        _iconBoard = board;
        var taken = _joinIcons.Observe(board.Players.Select(e => e.PlayerId), CurrentIcon);
        if (_bot && taken.Count > 0) GD.Print($"t {_connectedFor:F1} s: scoreboard icons taken for {string.Join(", ", taken.Select(id => "P" + id))}");
    }

    /// <summary>The player's shoulder emblem now: the pad decal that is an emblem, else the left pad's badge, else the right's; null while its look is not resolved.</summary>
    ResolvedDecal? CurrentIcon(int playerId)
    {
        if (!_playerLooks.TryGetValue(playerId, out var look)) return null;
        var l = look.Entry.Look;
        if (l.LeftDecal.Kind == DecalKind.Emblem && !l.LeftDecal.Emblem.IsNone) return l.LeftDecal;
        if (l.RightDecal.Kind == DecalKind.Emblem && !l.RightDecal.Emblem.IsNone) return l.RightDecal;
        return l.LeftDecal.Kind != DecalKind.None ? l.LeftDecal : l.RightDecal.Kind != DecalKind.None ? l.RightDecal : null;
    }

    /// <summary>The icon's picture in grey (only black and white on the board), made once per texture.</summary>
    Texture2D? IconTexture(int playerId)
    {
        if (_looks is null || !_joinIcons.TryGet(playerId, out var decal) || _looks.DecalIcon(decal) is not { } texture) return null;
        if (_greyIcons.TryGetValue(texture, out var grey)) return grey;
        try
        {
            var image = texture.GetImage();
            if (image is null) return null;
            image = (Image)image.Duplicate();
            if (image.IsCompressed()) image.Decompress();
            image.Convert(Image.Format.Rgba8);
            if (image.GetWidth() > 128) image.Resize(128, 128 * image.GetHeight() / Math.Max(1, image.GetWidth()), Image.Interpolation.Bilinear);
            for (int y = 0; y < image.GetHeight(); y++)
                for (int x = 0; x < image.GetWidth(); x++)
                {
                    var c = image.GetPixel(x, y);
                    float v = 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
                    image.SetPixel(x, y, new Color(v, v, v, c.A));
                }
            grey = ImageTexture.CreateFromImage(image);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            GD.PrintErr($"scoreboard: P{playerId}'s icon could not be made grey ({ex.Message})");
            return null;
        }
        if (_greyIcons.Count > 64) _greyIcons.Clear();
        _greyIcons[texture] = grey;
        return grey;
    }

    ScoreboardView.ScoreboardFrame? BuildScoreboardFrame()
    {
        if (_client?.Scoreboard is not { } board) return null;
        bool single = !board.Teams;
        Func<ScoreEntry, int>? scoreOf = null;
        bool assists = true;
        ExtrasScoreboard(ref single, ref scoreOf, ref assists);   // EXTRAS-HOOK: Musou Mode's one column and its points
        scoreOf ??= e => e.Score;
        var t = _summaryText;
        string score = _scoreWord;
        var frame = new ScoreboardView.ScoreboardFrame
        {
            Single = single, SingleScore = board.Teams,   // free-for-all: no side score; Musou Mode: the column's total
            Headers = assists ? [t.Kills, t.Deaths, t.Ratio, t.Assists, score, "PING"] : [t.Kills, t.Deaths, score, "PING"],
        };
        var mine = _client.Predicted.Team;
        IEnumerable<Team> sides = single ? [Team.None] : mine == Team.Chaos ? [Team.Chaos, Team.SpaceMarines] : [Team.SpaceMarines, Team.Chaos];
        var match = _client.Match;
        foreach (var side in sides)
        {
            var entries = board.Players.Where(e => side == Team.None || e.Team == side)
                .OrderByDescending(scoreOf).ThenByDescending(e => e.Kills).ThenBy(e => e.Deaths).ThenBy(e => e.PlayerId).ToList();
            var column = new ScoreboardView.Side
            {
                Score = side == Team.SpaceMarines ? match.MarineScore : side == Team.Chaos ? match.ChaosScore : entries.Sum(scoreOf),
            };
            foreach (var e in entries)
            {
                string ratio = BattleRecord.Ratio(e.Kills, e.Deaths).ToString("0.0", CultureInfo.InvariantCulture);
                string ping = RttEstimator.Display(e.PingMs);
                column.Rows.Add(new ScoreboardView.Row
                {
                    Name = HudName(e.PlayerId), Icon = IconTexture(e.PlayerId), Me = e.PlayerId == _client.PlayerId,
                    Cells = assists
                        ? [e.Kills.ToString(CultureInfo.InvariantCulture), e.Deaths.ToString(CultureInfo.InvariantCulture), ratio, e.Assists.ToString(CultureInfo.InvariantCulture), scoreOf(e).ToString(CultureInfo.InvariantCulture), ping]
                        : [e.Kills.ToString(CultureInfo.InvariantCulture), e.Deaths.ToString(CultureInfo.InvariantCulture), scoreOf(e).ToString(CultureInfo.InvariantCulture), ping],
                });
            }
            frame.Sides.Add(column);
        }
        return frame;
    }

    /// <summary>The SCORE header: the install's word (locale 106096), read once with the summary's words; "SCORE" without it.</summary>
    string _scoreWord => _scoreWordRead ??= ReadScoreWord();
    string? _scoreWordRead;

    string ReadScoreWord()
    {
        try
        {
            string path = System.IO.Path.Combine(_gameDir, "data", "locale", "english", "ucs.pc");
            if (System.IO.File.Exists(path) && SmFormats.Locale.LocaleTable.Open(path).Get(106096) is { Length: > 0 } word) return word;
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException) { GD.PrintErr("scoreboard: the SCORE word was not read (" + ex.Message + ")"); }
        return "SCORE";
    }
}
