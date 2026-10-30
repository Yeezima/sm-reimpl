using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmSim;

namespace SmClient;

// ui/hud-fidelity (owner, 2026-10-29: "a true-replication pass on the HUD"): the kill feed, the reward lines, the score panel, the Seize Ground markers and the pick-up prompt
// laid out and drawn from the original movies (HudEventLog.gfx, Hud.gfx score_mc, HudText.gfx mp_score_mc and Prompt_mc, Decorator.gfx's point decorator): the pieces are the
// movies' own art rendered at runtime, the text fields' places, sizes, alignment and colours are the movies', the animations are the movies' frames at 30 fps. What the movies
// leave to their scripts (when a line goes, which state picks which frame) is measured on the reference footage or ours (spec "HUD from the movies (ui/hud-fidelity)").
public sealed partial class Hud
{
    /// <summary>The score fields' colours in <c>HudText.gfx</c> (<c>marine_kills</c> 255,204,0; <c>chaos_kills</c> 255,51,0): the viewer's side and the enemy.</summary>
    static readonly Color MovieGold = new(1f, 0.8f, 0f), MovieRed = new(1f, 0.2f, 0f);

    // ---------------------------------------------------------------- kill feed (HudEventLog.gfx)

    /// <summary>The feed movie's entries (<c>logentry0_mc</c> .. <c>logentry2_mc</c>, 37.45 stage pixels apart).</summary>
    const int MovieFeedRows = 3;
    /// <summary>
    /// Measured on the reference footage (ui/hud-fidelity, 0.1 s steps): a new line fades in on the top row over about 0.4 s and pushes the others down a row (within 0.1 s), the
    /// fourth drops off the bottom; a line stays about 4 s. Values: footage, rounded (ours).
    /// </summary>
    const double FeedFadeIn = 0.4, FeedLife = 4.0, FeedFadeOut = 0.5, FeedSlide = 0.1;

    /// <summary>The feed's names on the footage are 0.8 of the field's 18 pixels in our font (Bahnschrift's capitals stand taller than the original's): measured (ours).</summary>
    const float FeedFontScale = 0.8f;

    /// <summary>The feed from <c>HudEventLog.gfx</c>: killer right-aligned, the movie's weapon icon (added), victim left-aligned; the newest on top. False without the movie.</summary>
    bool DrawMovieKillFeed(Vector2 vs)
    {
        if (OldHud || _movieArt?.Movies?.EventLog is not { } log) return false;
        if (log.Find("logentry0_mc") is not { } row0 || log.Find("logentry1_mc") is not { } row1) return false;
        float step = (float)(row1.ToStage.Ty - row0.ToStage.Ty);
        var entry = row0.Placement.Character;
        var iconSprite = log.Find("logentry0_mc/weapon_icon_mc")?.Placement.Character ?? -1;
        var labels = iconSprite >= 0 ? log.Labels(iconSprite) : new Dictionary<string, int>();
        var stage = Stage(vs, 1);
        int n = 0;
        for (int i = _feed.Count - 1; i >= 0 && n < MovieFeedRows; i--, n++)
        {
            var (k, at) = _feed[i];
            double age = _time - at;
            if (age > FeedLife) continue;
            float alpha = (float)Math.Clamp(age / FeedFadeIn, 0.0, 1.0) * (float)Math.Clamp((FeedLife - age) / FeedFadeOut, 0.0, 1.0);
            // The row slides down from the one above when a newer line arrives.
            double since = i + 1 < _feed.Count ? _time - _feed[i + 1].At : double.MaxValue;
            float slide = n == 0 ? 0f : (float)Math.Clamp(since / FeedSlide, 0.0, 1.0);
            float y = step * (n - 1 + slide);
            var m = row0.ToStage; m = m with { Ty = m.Ty + (n == 0 ? 0 : y) };
            var root = stage * ToXf(m);
            string? label = _art?.Art?.FeedLabel(k.Wargear);
            int iconFrame = label is not null && labels.TryGetValue(label, out int lf) ? lf : -1;
            var killerColour = MovieNameColour(k.KillerRel); var victimColour = MovieNameColour(k.VictimRel);
            DrawWalk(log, entry, 0, root, (path, _) => path == "weapon_icon_mc" ? Math.Max(0, iconFrame) : null, ch => ch == iconSprite,
                path => path switch
                {
                    "killer_name_txt" => k.Killer,
                    "victim_name_txt" => k.Victim + k.Suffix,
                    _ => null,
                },
                path => path == "killer_name_txt" ? killerColour : path == "victim_name_txt" ? victimColour : null, alpha, fontScale: FeedFontScale);
            if (iconFrame < 0 && k.Weapon.Length > 0 && log.Find("logentry0_mc/weapon_icon_mc") is { } icon)
            {
                // No picture for this weapon: its name in the icon's place (ours).
                var c = root * new Vector2((float)(icon.ToStage.Tx - row0.ToStage.Tx) + 24f, (float)(icon.ToStage.Ty - row0.ToStage.Ty) + 24f);
                Txt(k.Weapon, c.X, c.Y + 6f, 16, new Color(0.92f, 0.92f, 0.92f, alpha), 0, 2);
            }
        }
        return true;
    }

    static Color MovieNameColour(Rel r) => r switch { Rel.Own => MovieGold, Rel.Enemy => MovieRed, _ => White };

    // ---------------------------------------------------------------- reward lines (Hud.gfx score_mc)

    /// <summary>
    /// The reward lines from <c>Hud.gfx</c>'s <c>score_mc</c>: <c>xp_event_1_mc</c> .. <c>_4_mc</c> (32.65 stage pixels apart), each the experience ("+50", right-aligned), the reward's
    /// icon (<c>icon_mc</c> frame by the reward's frame name: <c>mp_bonus_kill</c>, the skull) and its name, stamped in by the movie's own clips (the icon falls from 27 times its size,
    /// white, over 17 frames; the number and the name over 12; at 30 fps) and faded out by them. The newest on the top line (ours). The lines are the rewards of <see cref="RewardTracker"/>
    /// (the kill's own, headshot, revenge, multi kills, killstreaks ...: experience, icon frame and name from <see cref="RewardTable"/>, ui/hud-2). False without the movie.
    /// </summary>
    /// <summary>The reward lines' scale (ours, owner 2026-10-30: slightly smaller than the movie's).</summary>
    const float RewardScale = 0.82f;

    bool DrawMovieRewards(Vector2 vs)
    {
        if (OldHud || HudMovie is not { } hud) return false;
        if (hud.Find("score_mc/xp_event_1_mc") is not { } line1 || hud.Find("score_mc/xp_event_2_mc") is not { } line2) return false;
        if (hud.Find("score_mc/xp_event_1_mc/xp_icons_mc/xp_icon_1_mc/icon_mc") is not { } iconPlaced) return false;
        int iconSprite = iconPlaced.Placement.Character;
        var iconLabels = hud.Labels(iconSprite);
        int skull = iconLabels.TryGetValue(RewardTable.Kill.Frame, out int sk) ? sk : 0;
        // The icon's frame for a reward: its own label, else (a streak step the movie has no picture of) the killstreak 3 picture, else the skull.
        int IconFrame(RewardSpec spec) => iconLabels.TryGetValue(spec.Frame, out int f) ? f : iconLabels.TryGetValue("mp_bonus_killstreak_3", out int ks) && spec.Key.StartsWith("killstreak", StringComparison.Ordinal) ? ks : skull;
        float step = (float)(line2.ToStage.Ty - line1.ToStage.Ty);
        int eventSprite = line1.Placement.Character;
        var stage = Stage(vs, 0);
        // The clips' labels (fadein .. in .. fadeout).
        int iconClip = hud.Find("score_mc/xp_event_1_mc/xp_icons_mc/xp_icon_1_mc")?.Placement.Character ?? -1;
        int xpClip = hud.Find("score_mc/xp_event_1_mc/xp_mc")?.Placement.Character ?? -1;
        int nameClip = hud.Find("score_mc/xp_event_1_mc/name_offset_mc/name_mc")?.Placement.Character ?? -1;
        int n = 0;
        for (int i = _events.Count - 1; i >= 0 && n < 4; i--, n++)
        {
            var (text, at, line) = _events[i];
            var spec = line ?? RewardTable.Kill;
            double age = _time - at;
            var m = line1.ToStage; m = m with { Ty = m.Ty + step * n };
            // ui/hud-3 (owner 2026-10-30): the centre announcements a little smaller, about the screen's middle line and the first line's top (ours).
            var root = stage * new Transform2D(0f, new Vector2(RewardScale, RewardScale), 0f, new Vector2(640f * (1f - RewardScale), (float)line1.ToStage.Ty * (1f - RewardScale))) * ToXf(m);
            int Clip(int sprite, string path)
            {
                var l = hud.Labels(sprite);
                int fadeIn = l.GetValueOrDefault("fadein", 1), hold = l.GetValueOrDefault("in", fadeIn), fadeOut = l.GetValueOrDefault("fadeout", hold + 1);
                int count = hud.FrameCount(sprite);
                double outAt = EventSeconds - (count - fadeOut) / ReticleRules.FramesPerSecond;
                if (age >= outAt) return Math.Min(count - 1, fadeOut + (int)((age - outAt) * ReticleRules.FramesPerSecond));
                return Math.Min(hold, fadeIn + (int)(age * ReticleRules.FramesPerSecond));
            }
            int iconF = iconClip >= 0 ? Clip(iconClip, "icon") : 0, xpF = xpClip >= 0 ? Clip(xpClip, "xp") : 0, nameF = nameClip >= 0 ? Clip(nameClip, "name") : 0;
            string name = line is null ? text.TrimStart('+', ' ') : RewardName(spec);
            int icon = IconFrame(spec);
            DrawWalk(hud, eventSprite, 0, root, (path, _) => path switch
            {
                "xp_icons_mc/xp_icon_1_mc" => iconF,
                "xp_icons_mc/xp_icon_1_mc/icon_mc" => icon,
                "xp_mc" => xpF,
                "name_offset_mc/name_mc" => nameF,
                _ => null,   // xp_icon_2..4: their first frame ("none", hidden); the texts' "basic" frame
            }, ch => ch == iconSprite,
                path => path.EndsWith("xp_txt", StringComparison.Ordinal) ? $"+{spec.Experience}" : path.EndsWith("name_txt", StringComparison.Ordinal) ? name : null);
        }
        return true;
    }

    // ---------------------------------------------------------------- score panel (HudText.gfx mp_score_mc)

    /// <summary>
    /// The panel's slight tilt on the footage (its lines rise to the right, about 1.5 degrees; the movie draws it flat, the original projects it): measured (ours). The dark band behind
    /// it is not in the movie either (footage; M44's band, placed in the panel's frame).
    /// </summary>
    const float ScoreTilt = -1.5f, ScoreShift = 17f;

    /// <summary>The dark band's right end in the panel's frame (ui/hud-3: 330 before; side by side with the owner's frame the band ends at the bars' end, not 55 px past it; ours).</summary>
    const float BandRight = 292f;

    /// <summary>
    /// The team score panel from <c>HudText.gfx</c> <c>mp_score_mc</c>, anchored bottom left: the own score (gold) and the enemy's (red) right-aligned in their fields, the movie's bars
    /// (<c>sm_bar_mc</c> gold for the viewer's side, <c>chaos_bar_mc</c> red for the enemy: frame = score / limit x 100 of their 105-frame fill clips), the score limit, the time left, and
    /// in Seize Ground the points' markers (<c>objective_marker1</c>..<c>3</c>: the frame of the point's state label, <see cref="DecoratorRules.PointLabel"/>). The side emblem is the M44
    /// one (the movie's is a picture the renderer does not place). False without the movie.
    /// </summary>
    bool DrawMovieScorePanel(Vector2 vs)
    {
        var d = Data;
        if (OldHud || _movieArt?.Movies?.Text is not { } text || !d.Teams) return false;
        if (text.Find("mp_score_mc") is not { } panel) return false;
        // The footage's panel sits 17 stage pixels right of the movie's place (its emblem where the movie's would be): measured side by side (ours).
        var root = Stage(vs, -1) * new Transform2D(0f, new Vector2(ScoreShift, 0f)) * ToXf(panel.ToStage);
        root *= new Transform2D(Mathf.DegToRad(ScoreTilt), new Vector2(200f, 70f)) * new Transform2D(0f, new Vector2(-200f, -70f));
        var band = new Vector2[] { new(104f, 44f), new(BandRight, 44f), new(BandRight, 92f), new(92f, 92f) };   // ui/hud-3: ends where the bars' troughs end, as the owner's reference (ours)
        var bandV = new Vector2[4];
        for (int i = 0; i < 4; i++) bandV[i] = root * band[i];
        DrawPolygon(bandV, [new Color(0f, 0f, 0f, 0.3f), new Color(0f, 0f, 0f, 0.78f), new Color(0f, 0f, 0f, 0.78f), new Color(0f, 0f, 0f, 0.3f)]);
        var line = new Color(0.85f, 0.85f, 0.82f, 0.5f);
        DrawLine(bandV[0], bandV[1], line, 1.5f, true);
        DrawLine(bandV[3], bandV[2], line, 1.5f, true);
        SideEmblem(d.OwnTeam, root * new Vector2(72f - ScoreShift, 71f), 88f, Rel.Own);   // the footage's size (the aquila 74 stage pixels wide)
        float limit = MathF.Max(d.ScoreLimit, 1);
        int Fill(int score) => Math.Clamp((int)MathF.Round(Math.Clamp(score / limit, 0f, 1f) * 100f), 0, 100);
        int own = Fill(d.OwnScore), enemy = Fill(d.EnemyScore);
        int ownSprite = text.Find("mp_score_mc/sm_bar_mc")?.Placement.Character ?? -1, enemySprite = text.Find("mp_score_mc/chaos_bar_mc")?.Placement.Character ?? -1;
        int marker = text.Find("mp_score_mc/objective_marker1")?.Placement.Character ?? -1;
        var markerLabels = marker >= 0 ? text.Labels(marker) : new Dictionary<string, int>();
        DrawWalk(text, panel.Placement.Character, 0, root, (path, _) => path switch
        {
            "sm_bar_mc" => own,
            "chaos_bar_mc" => enemy,
            "objective_marker1" or "objective_marker2" or "objective_marker3" => d.Seize && PointIndex(path) is int i && i < d.Points.Count ? markerLabels.GetValueOrDefault(PointLabel(d.Points[i]), 0) : 0,
            _ => null,
        }, ch => ch == ownSprite || ch == enemySprite || ch == marker,
            path => path switch
            {
                "marine_kills" => d.OwnScore.ToString(),
                "chaos_kills" => d.EnemyScore.ToString(),
                "kill_number" => d.ScoreLimit > 0 ? d.ScoreLimit.ToString() : "-",
                "mp_timer_txt" => d.Clock,
                _ => null,
            }, path => path == "marine_kills" ? MovieGold : path == "chaos_kills" ? MovieRed : null,
            1f, skip: path => path is "team_1" or "team_2" || path == "objective_marker4" || (!d.Seize && path.StartsWith("objective_marker", StringComparison.Ordinal))
                || (path.StartsWith("objective_marker", StringComparison.Ordinal) && PointIndex(path) is int j && j >= d.Points.Count));
        return true;
    }

    static int? PointIndex(string path) => path.Length > 16 && int.TryParse(path.AsSpan(16), out int i) ? i - 1 : null;

    // ---------------------------------------------------------------- Seize Ground markers (Decorator.gfx point decorator)

    /// <summary>
    /// A control point's marker from <c>Decorator.gfx</c>'s point decorator: <c>anim_mc</c> at the frame of its state label (the pointer triangle, grey neutral, gold or red while a side
    /// captures it; the owner's eagle or Chaos star; added) at 0.5 and the point's letter in <c>number_txt</c> (black, 35 px at 0.5) over it, at the movie's own size (1 stage pixel per
    /// movie pixel, as the footage shows it: the triangle 40 stage pixels wide). False without the movie (the caller draws the M44 marker).
    /// </summary>
    bool DrawMoviePointMarker(Point p, Vector2 pos, float blink)
    {
        if (OldHud || _movieArt?.Movies?.Decorator is not { } deco) return false;
        if (!_pointDecoratorSearched)
        {
            // Once: the point decorator is the sprite that places "anim_mc" and "number_mc" (sprite 158 in the install).
            _pointDecoratorSearched = true;
            foreach (var (id, sprite) in deco.Movie.Sprites)
            {
                if (sprite.Frames.Count == 0) continue;
                int a = -1; bool hasNumber = false;
                foreach (var pl in sprite.Frames[0].Display.Values) { if (pl.Name == "anim_mc") a = pl.Character; if (pl.Name == "number_mc") hasNumber = true; }
                if (a >= 0 && hasNumber) { _pointDecorator = id; _pointAnim = a; break; }
            }
        }
        if (_pointDecorator < 0) return false;
        int anim = _pointAnim;
        int frame = deco.Labels(anim).GetValueOrDefault(PointLabel(p), 0);
        var root = new Transform2D(0f, new Vector2(StageToVirtual, StageToVirtual), 0f, pos);
        DrawWalk(deco, _pointDecorator, 0, root, (path, _) => path == "anim_mc" ? frame : null, ch => ch == anim,
            path => path.EndsWith("number_txt", StringComparison.Ordinal) ? p.Letter : null, null, blink,
            skip: path => path.StartsWith("bar_", StringComparison.Ordinal));
        return true;
    }

    int _pointDecorator = -1, _pointAnim = -1;
    bool _pointDecoratorSearched;

    // ---------------------------------------------------------------- pick-up prompt (HudText.gfx Prompt_mc)

    double _promptSince = -1;
    string _promptShown = "";

    /// <summary>
    /// The pick-up prompt from <c>HudText.gfx</c>'s <c>Prompt_mc</c>: its band and its centred text field (24 px) where the movie holds them (the clip slides them in from 40 stage
    /// pixels lower and fades them in over 19 frames), and the key above in a gold-framed box at the movie's <c>button_mc</c> (the button art comes from another movie: drawn by us,
    /// gold frame after the footage). False without the movie.
    /// </summary>
    bool DrawMoviePrompt(Vector2 vs)
    {
        var d = Data;
        if (d.Prompt.Length == 0) { _promptSince = -1; return !OldHud && _movieArt?.Movies?.Text is not null; }
        if (OldHud || _movieArt?.Movies?.Text is not { } text || text.Find("Prompt_mc") is not { } prompt) return false;
        if (_promptSince < 0 || _promptShown != d.Prompt) { _promptSince = _time; _promptShown = d.Prompt; }
        var labels = text.Labels(prompt.Placement.Character);
        int inAt = labels.GetValueOrDefault("in", 1), outAt = labels.GetValueOrDefault("out", 74);
        // Hold where the slide ends (the frame after which the placement stops changing: 19 in the install), before "out".
        int frame = Math.Max(0, Math.Min(Math.Min(outAt - 1, inAt + 18), inAt + (int)((_time - _promptSince) * ReticleRules.FramesPerSecond)));
        var root = Stage(vs, 0) * ToXf(prompt.ToStage);
        DrawWalk(text, prompt.Placement.Character, frame, root, null, _ => false,
            path => path.EndsWith("Objective_txt", StringComparison.Ordinal) ? d.Prompt : null, null, 1f,
            skip: path => path.EndsWith("icon_mc", StringComparison.Ordinal) || path.EndsWith("button_mc", StringComparison.Ordinal));
        // The key box at the movie's button (centred on its origin; 36 stage pixels, the footage's size).
        if (text.Find("Prompt_mc/Objective_mc/button_mc") is { } button && text.Find("Prompt_mc/Objective_mc") is { } holder
            && text.Movie.Sprites.TryGetValue(prompt.Placement.Character, out var clip) && clip.Frames.Count > frame)
        {
            float alpha = 1f;
            foreach (var pl in clip.Frames[frame].Display.Values) if (pl.Name == "Objective_mc") alpha = pl.Colour?.MulA ?? 1f;
            var inHolder = holder.ToStage.Inverse() is { } inv ? inv.After(button.ToStage) : button.ToStage;
            float holderY = 0f;
            foreach (var pl in clip.Frames[frame].Display.Values) if (pl.Name == "Objective_mc") holderY = (float)((pl.Matrix?.Ty ?? 0) / 20.0);
            var c = root * new Vector2((float)(holder.ToStage.Tx - prompt.ToStage.Tx + inHolder.Tx), holderY + (float)inHolder.Ty);
            float half = 13.5f * StageToVirtual;   // the footage's key box: 27 stage pixels
            var box = new Rect2(c.X - half, c.Y - half - 6f, half * 2f, half * 2f);
            DrawRect(box, new Color(0.02f, 0.02f, 0.02f, 0.75f * alpha));
            DrawRect(box, new Color(MovieGold, 0.95f * alpha), false, 3f);
            Txt(d.PromptKey, box.GetCenter().X, box.GetCenter().Y + 7f, 18, new Color(1f, 1f, 1f, alpha), 0, 0, 2);
        }
        return true;
    }

    // The armour and health bar stays M44's (Hud.DrawVitals, the original bitmaps): the movie's unifiedhealth_mc renders its armour lines and medallion gold, the
    // footage shows them cyan (the game tints them at runtime: not in the data), so the M44 drawing is the closer match (ui/hud-fidelity, side by side).

    // ---------------------------------------------------------------- weapon box (Hud.gfx weaponInfo_Slide)

    /// <summary>The box's turn on the footage (its edges fall to the right, about 2.5 degrees: the original projects it; the movie draws it flat): measured (M44, ours).</summary>
    const float BoxTilt = 2.5f;

    /// <summary>
    /// The box on the footage is 1.12 times the movie's own size (259 against 232 pixels of a 1280-wide frame, side by side; the original's projection brings it nearer): measured (ours).
    /// The weapon's picture in it shows at 0.8 of the holder's size and dimmed to 0.55 (the footage's dark grey gun): measured (ours).
    /// </summary>
    const float BoxScale = 1.12f, BoxPictureScale = 0.8f, BoxPictureShade = 0.55f;

    /// <summary><c>--hud-launcher-demo</c> (screenshot aid): the launcher display counts its rounds down and up (two a second) instead of showing the rounds left.</summary>
    static readonly bool LauncherDemo = Array.IndexOf(OS.GetCmdlineUserArgs(), "--hud-launcher-demo") >= 0;

    // The box's path sets, built once (not every frame).
    static readonly HashSet<string> OptionalPaths = new(WeaponBoxRules.Optional, StringComparer.Ordinal), NoPaths = new(StringComparer.Ordinal);
    static readonly Dictionary<IReadOnlyList<string>, HashSet<string>> ShowSets = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The weapon box from <c>Hud.gfx</c> (<c>weaponInfo_Slide</c> at the end of its slide-in, bottom right): the frame with the winged skull (with or without the grenade column),
    /// the grenades held (<c>grenade00</c> .. <c>04</c>, the equipment's picture), the weapon's picture where the movie loads it (<c>gun_mc</c>), the family's ammunition display
    /// (<see cref="WeaponBoxRules"/>) with its bar at the clip's or the heat's frame, and the count. False without the movie (the caller draws the M44 box).
    /// </summary>
    bool DrawMovieWeaponBox(Vector2 vs)
    {
        var d = Data;
        if (OldHud || HudMovie is not { } hud) return false;
        const string Box = "weaponInfo_Slide/weaponInfo_container/weaponInfo_mc";
        int slideSprite = hud.Find("weaponInfo_Slide")?.Placement.Character ?? -1;
        int slideEnd = slideSprite >= 0 ? hud.FrameCount(slideSprite) - 1 : 0;
        if (hud.Find(Box, id => id == slideSprite ? slideEnd : null) is not { } box) return false;
        bool chaos = d.OwnTeam == Team.Chaos;
        var spec = WeaponBoxRules.For(d.WeaponWargear, _art?.Art?.WeaponType(d.WeaponWargear), chaos);
        var root = Stage(vs, 1) * ToXf(box.ToStage);
        // Turned and enlarged about the frame's top-left corner (box pixels 28, 18): the footage's box is 1.12 times the movie's, its top-left corner where the movie puts it.
        root *= new Transform2D(Mathf.DegToRad(BoxTilt), new Vector2(BoxScale, BoxScale), 0f, new Vector2(28f, 18f)) * new Transform2D(0f, new Vector2(-28f, -18f));
        // The bar's frame: the clip's spent part or the heat (the bar drains as it heats).
        int barSprite = spec?.Bar is { } barPath ? hud.Find($"{Box}/{barPath}")?.Placement.Character ?? -1 : -1;
        int barFrame = 0;
        if (barSprite >= 0 && spec is not null)
        {
            float full = spec.Value switch
            {
                WeaponBoxBar.Heat => d.HasHeat ? 1f - Math.Clamp(d.Heat / MathF.Max(d.HeatMax, 1f), 0f, 1f) : 1f,
                WeaponBoxBar.Spent => d.ClipMax > 0 ? Math.Clamp(d.Clip / (float)d.ClipMax, 0f, 1f) : 1f,
                _ => 1f,
            };
            if (d.WeaponBarFull >= 0f) full = d.WeaponBarFull;   // ui/hud-3: the melta's progress between shots
            barFrame = WeaponBoxRules.BarFrame(full, hud.FrameCount(barSprite));
        }
        bool reserve = d.ReserveMax > 0 || d.Reserve > 0;
        int count = reserve ? d.Reserve : d.Clip;
        bool empty = count == 0 && d.Clip == 0;
        var show = spec is null ? NoPaths : ShowSets.TryGetValue(spec.Show, out var known) ? known : ShowSets[spec.Show] = new HashSet<string>(spec.Show, StringComparer.Ordinal);
        var optional = OptionalPaths;
        string equipment = d.GrenadeWargear.Contains("krak", StringComparison.OrdinalIgnoreCase) || d.GrenadeWargear.Contains("blind", StringComparison.OrdinalIgnoreCase) ? "blind_grenade_mc"
            : d.GrenadeWargear.Contains("stim", StringComparison.OrdinalIgnoreCase) ? "stimpack_mc" : "grenade_mc";
        int grenades = d.HasGrenade ? Math.Clamp(d.Grenades, 0, 5) : 0;
        int Last(string path) => hud.Find($"{Box}/{path}")?.Placement.Character is int c ? hud.FrameCount(c) - 1 : 0;
        int frameGren = Last("frame_gren_mc"), frameNoGren = Last("frame_nogren_mc");
        string? bar = spec?.Bar;
        DrawWalk(hud, box.Placement.Character, 0, root, (path, _) => path == "frame_gren_mc" ? frameGren : path == "frame_nogren_mc" ? frameNoGren
            : path == "loadout_mc/grenade_bg_mc" ? 0 : bar is not null && path == bar ? barFrame : null,
            ch => hud.HasClip(ch),
            path => path == spec?.Count ? count.ToString() : null,
            path => path == spec?.Count && empty ? MovieRed : null, 1f,
            skip: path =>
            {
                if (optional.Contains(path)) return !show.Contains(path);
                if (path == "frame_gren_mc") return true;   // its frames hold almost nothing as read; the box frame is frame_nogren_mc in both cases (ours)
                if (path == "frame_nogren_mc") return false;
                if (path.EndsWith("ammo_depleted", StringComparison.Ordinal) || path.EndsWith("weapon_txt", StringComparison.Ordinal)) return true;
                if (path.StartsWith("loadout_mc/grenade_bg_mc/grenade0", StringComparison.Ordinal))
                {
                    // grenade00 is the bottom one; the held ones show, each with the equipment's picture.
                    string rest = path["loadout_mc/grenade_bg_mc/grenade0".Length..];
                    if (rest.Length >= 1 && rest[0] - '0' >= grenades) return true;
                    int slash = rest.IndexOf('/');
                    return slash >= 0 && rest[(slash + 1)..] != equipment;
                }
                if (path.StartsWith(WeaponBoxRules.LauncherDisplay, StringComparison.Ordinal))
                {
                    // The launcher's rounds (ui/hud-2): the slots below the rounds left (clip and reserve); its red reload glow is not driven (hidden).
                    if (WeaponBoxRules.LauncherRoundIndex(path) is int slot) return !WeaponBoxRules.LauncherRoundLoaded(slot, LauncherDemo ? (int)(_time * 2.0) % 19 : d.Clip + d.Reserve);
                    return path.EndsWith("vengeance_reload", StringComparison.Ordinal);
                }
                if (path.StartsWith("Ammo_Stalker_mc/round", StringComparison.Ordinal) && path.Length > 21 && char.IsDigit(path[21]))
                    return !WeaponBoxRules.RoundLoaded(path[21] - '0', d.Clip, d.ClipMax);
                return false;
            }, fontScale: FeedFontScale);   // the count's digits on the footage: 0.8 of the field's size in our font, as the feed's names (measured)
        // The weapon's picture where the movie loads it (gun_mc: an empty holder the game fills; its registration point is the picture's top left).
        if (hud.Find($"{Box}/loadout_mc/gun_mc", id => id == slideSprite ? slideEnd : null) is { } gun && box.ToStage.Inverse() is { } inv
            && d.WeaponWargear.Length > 0 && _art?.WeaponIcon(d.WeaponWargear) is { } icon && _art.Texture(icon) is { } tex)
        {
            var xf = root * ToXf(inv.After(gun.ToStage));
            var size = tex.GetSize();
            DrawSetTransformMatrix(_base * _localXf * xf);
            DrawTextureRect(tex, new Rect2(size * (1f - BoxPictureScale) / 2f, size * BoxPictureScale), false, new Color(BoxPictureShade, BoxPictureShade, BoxPictureShade, 1f));
            DrawSetTransformMatrix(_base * _localXf);
        }
        return true;
    }
}
