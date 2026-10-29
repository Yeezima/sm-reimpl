using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmSim;

namespace SmClient;

// Fidelity gap 10: the original's HUD indicators that live in the world or in the kill feed. Art and state names come from the install's own
// movies (Decorator.gfx, HudEventLog.gfx; SmContent.DecoratorSprites / FeedIcons / DecoratorRules); the placement on screen, the sizes and the
// visibility rules are ours where the movies do not say (spec "World decorators (fidelity gap 10)", refine register).
public sealed partial class Hud
{
    /// <summary>One player's decorator this frame: the viewer's relation, the head's screen position (viewport pixels) and the distance.</summary>
    public sealed class Decorator
    {
        public string Name = "";
        public Rel Rel;
        public Team Team;
        public Vector2 Screen;
        public float Distance;
    }

    // ---------------------------------------------------------------- kill feed pictures

    /// <summary>
    /// The kill feed's picture for a weapon (the feed movie's white silhouette with blood splatter), 48 virtual units square at scale 1,
    /// centred on <paramref name="centre"/>; false when the art or a label for the weapon is not there (the feed then falls back to the HUD's
    /// weapon silhouette, then to the weapon's name).
    /// </summary>
    bool FeedPicture(string wargear, Vector2 centre, float scale, Color modulate)
    {
        if (wargear.Length == 0 || _art?.Art is not { } art) return false;
        // The label lookup allocates; the answer never changes once the art is loaded, so it is kept per wargear file.
        if (!_feedIcons.TryGetValue(wargear, out var icon))
            _feedIcons[wargear] = icon = art.FeedLabel(wargear) is { } label ? FeedIcons.Find(label) : null;
        if (icon is null || _art.Texture(FeedIcons.Texture) is null) return false;
        var size = new Vector2(FeedIcons.Size, FeedIcons.Size) * scale;
        return SpriteFlipped(icon.Sprite, new Rect2(centre - size / 2f, size), icon.FlipX, icon.FlipY, modulate);
    }

    readonly Dictionary<string, FeedIcon?> _feedIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An art sprite into <paramref name="dest"/>, mirrored horizontally and/or vertically; false when the art is not there.</summary>
    bool SpriteFlipped(UiSprite s, Rect2 dest, bool flipX, bool flipY, Color modulate)
    {
        if (!flipX && !flipY) return Sprite(s, dest, modulate);
        if (_art?.Texture(s.Texture) is not { } tex) return false;
        BeginLocal(new Transform2D(new Vector2(flipX ? -1f : 1f, 0f), new Vector2(0f, flipY ? -1f : 1f), dest.GetCenter()));
        DrawTextureRectRegion(tex, new Rect2(-dest.Size / 2f, dest.Size), new Rect2(s.X, s.Y, s.Width, s.Height), modulate);
        EndLocal();
        return true;
    }

    // ---------------------------------------------------------------- point states

    readonly Dictionary<string, (float Last, double ChangedAt, float Delta)> _pointTrack = new();
    const double WorkingHold = 0.6;   // seconds a point still counts as "being worked on" after its progress last changed (ours)

    /// <summary>Per frame: remembers when and which way each control point's progress last moved (the wire carries the progress only).</summary>
    void TrackPoints()
    {
        foreach (var p in Data.Points)
        {
            if (!_pointTrack.TryGetValue(p.Letter, out var t)) { _pointTrack[p.Letter] = (p.Progress, double.NegativeInfinity, 0f); continue; }
            float delta = p.Progress - t.Last;
            _pointTrack[p.Letter] = MathF.Abs(delta) > 1e-4f ? (p.Progress, _time, delta) : t with { Last = p.Progress };
        }
    }

    /// <summary>
    /// Whether the point's progress is being worked on (interpretation of the movie's flash states): a neutral point whose capture
    /// moved just now; an owned point whose hold is falling (the other side is uncapping it).
    /// </summary>
    bool PointWorking(Point p)
    {
        if (!_pointTrack.TryGetValue(p.Letter, out var t) || _time - t.ChangedAt > WorkingHold) return false;
        return p.OwnerTeam == Team.None ? p.Progress > 0f || t.Delta < 0f : t.Delta < 0f;
    }

    static DecoratorRelation Relation(Rel r) => r switch { Rel.Own => DecoratorRelation.Ally, Rel.Enemy => DecoratorRelation.Enemy, _ => DecoratorRelation.None };

    /// <summary>The point decorator's state label of the original's movie for a point (<see cref="DecoratorRules.PointLabel"/>).</summary>
    string PointLabel(Point p) =>
        DecoratorRules.PointLabel(Relation(p.Owner), p.OwnerTeam == Team.Chaos, p.OwnerTeam == Team.None ? Relation(p.Side) : DecoratorRelation.None, PointWorking(p), p.Contested);

    /// <summary>
    /// The icon's opacity for a point (1 when steady): in a flash state the movie's flash loop (30 frames, 10 for the combat flash) fades an
    /// overlay of the icon from opaque to 10 % over the icon's half-opacity base, so the icon blinks between full and 55 %.
    /// </summary>
    float PointBlink(Point p)
    {
        string label = PointLabel(p);
        if (!DecoratorRules.IsFlash(label)) return 1f;
        float overlay = DecoratorRules.FlashAlpha(_time, DecoratorRules.IsCombat(label));
        return 1f - (1f - DecoratorRules.IdleIconAlpha) * (1f - overlay);
    }

    /// <summary>
    /// The capture bar of a point decorator (the movie's <c>bar_capping_mc</c> / <c>bar_uncapping_mc</c>: a 100-frame clip whose fill slides in;
    /// no bar art survives in the data, so the look is ours): a neutral point shows the capturing side's progress, an owned point whose hold has
    /// been eroded shows how far its owner has lost it. Centred on <paramref name="c"/>; gold when the viewer's side is working, red when the enemy is.
    /// </summary>
    void DrawCaptureBar(Point p, Vector2 c)
    {
        bool neutral = p.OwnerTeam == Team.None;
        float progress = neutral ? p.Progress : 1f - p.Progress;
        if (progress <= 0.004f) return;
        float fill = DecoratorRules.CaptureBarFill(DecoratorRules.CaptureBarFrame(progress));
        // Who is working on it: the capturing side of a neutral point, the opponent of an owned point's owner.
        var worker = neutral ? p.Side : p.Owner == Rel.Own ? Rel.Enemy : Rel.Own;
        var col = worker == Rel.Enemy ? RedText : worker == Rel.Own ? Gold : NeutralGrey;
        const float w = 62f, h = 9f;
        var box = new Rect2(c.X - w / 2f, c.Y - h / 2f, w, h);
        DrawRect(box.Grow(2f), new Color(0.02f, 0.02f, 0.02f, 0.8f));
        DrawRect(new Rect2(box.Position, new Vector2(w * fill, h)), col);
        DrawRect(box, new Color(1f, 1f, 1f, 0.55f), false, 1.2f);
    }

    // ---------------------------------------------------------------- player decorators

    /// <summary>
    /// The decorators over players (Decorator.gfx's <c>PlayerDecorator</c>: the race icon and the name; the movie's health and armour bars
    /// need the other players' health on the wire, which it does not carry). A friend: the gold race icon (aquila or Chaos star, the movie's
    /// <c>icon_mc</c> frames <c>imperial</c> / <c>chaos</c>) over the name in gold; a foe: the name in red (the footage). Size follows the distance (ours).
    /// </summary>
    void DrawPlayerDecorators(Vector2 vs)
    {
        if (Data.Decorators.Count == 0) return;
        // Far ones first so the near ones are on top.
        var list = new List<Decorator>(Data.Decorators);
        list.Sort((a, b) => b.Distance.CompareTo(a.Distance));
        foreach (var dec in list)
        {
            var relation = Relation(dec.Rel);
            var pos = dec.Screen / _scale;
            if (pos.X < -100f || pos.X > vs.X + 100f || pos.Y < -100f || pos.Y > vs.Y + 100f) continue;
            float s = DecoratorRules.PlayerScale(dec.Distance);
            float a = DecoratorRules.PlayerAlpha(relation, dec.Distance);
            if (a <= 0.01f) continue;
            bool ally = relation == DecoratorRelation.Ally;
            var col = ally ? FeedGold : RedText;
            col.A = a;
            int size = Math.Max(10, (int)MathF.Round(24f * s));
            Txt(dec.Name, pos.X, pos.Y, size, col, 0, 3, 2);
            if (!ally) continue;
            // The race icon above the name (131 x 70 art pixels, 0.4 virtual units each at full size).
            var icon = DecoratorSprites.Icon(dec.Team == Team.Chaos, enemy: false);
            float k = 0.4f * s;
            var centre = new Vector2(pos.X, pos.Y - size - 4f * s - icon.Height * k / 2f);
            if (!SpriteAt(icon, centre, k, new Color(1f, 1f, 1f, 0.9f * a))) SideEmblem(dec.Team, centre, 26f * s, Rel.Own, a);
        }
    }
}
