using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmFormats.Gfx;

namespace SmClient;

// ui/hud-fidelity: the pieces of the HUD drawn from the original movies (Hud.gfx, HudText.gfx, HudEventLog.gfx, Decorator.gfx) instead of the M44 shapes: the
// reticles with their spread, brace and charge clips, the hit marker, and (Hud.Fidelity.cs) the panels. Everything falls back to the M44 drawing while the movies
// are read, when the install lacks them, and with --old-hud (before / after).
public sealed partial class Hud
{
    /// <summary>Virtual units (the 1080-high space) per movie pixel (the movies' stages are 720 high).</summary>
    const float StageToVirtual = RefHeight / 720f;

    /// <summary>
    /// The reticle container's place on the stage is 0.8, 3.15 movie pixels up and left of the stage centre, and every reticle's centre is 0.8 to 1.0, 3.1 to 3.5 below and right of
    /// the container (measured: <c>smtool gfx-dump auto --path Reticule_Ingame_0_mc/idle_mc</c>): the reticles are centred on the stage centre, which the HUD puts on the screen
    /// centre (where the shots go).
    /// </summary>
    static readonly Vector2 ReticleContainer = new(-0.8f, -3.15f);

    HudMovieArt? _movieArt;
    HudAddLayer? _addLayer;
    readonly List<(Texture2D Texture, Transform2D Transform, Color Modulate)> _adds = new();
    readonly HashSet<string> _warmed = new();
    bool? _oldHud;

    /// <summary><c>--old-hud</c>: the M44 drawing everywhere (before / after aid).</summary>
    bool OldHud => _oldHud ??= Array.IndexOf(OS.GetCmdlineUserArgs(), "--old-hud") >= 0;

    void MovieReady()
    {
        if (OldHud) return;
        _movieArt = new HudMovieArt(GameDir);
        _addLayer = new HudAddLayer(this);
        AddChild(_addLayer);
    }

    void MovieProcess()
    {
        if (_movieArt is null) return;
        _movieArt.SetScale(Size.Y / 720.0);
        _movieArt.Process();
        _addLayer?.QueueRedraw();
    }

    public override void _ExitTree() => _movieArt?.Stop();

    HudMovie? HudMovie => _movieArt?.Movies?.Hud;

    /// <summary>Whether the HUD draws the interaction prompt itself (the movie is read and --old-hud is off).</summary>
    public bool DrawsPrompt => !OldHud && _movieArt?.Movies?.Text is not null;

    /// <summary>Draws a movie piece whose origin is at <paramref name="at"/> (virtual units) at <paramref name="scale"/> times the movie's size. False when the piece is not there.</summary>
    bool DrawPic(HudMovieArt.Pic? pic, Vector2 at, Color? modulate = null, float scale = 1f)
    {
        if (pic is null) return false;
        DrawAtom(pic, new Transform2D(0f, new Vector2(StageToVirtual * scale, StageToVirtual * scale), 0f, at), modulate ?? White, false);
        return true;
    }

    /// <summary>
    /// Draws a rendered piece under <paramref name="xf"/> (movie pixels to virtual units, inside the current local frame): normally, or into the HUD's add layer when the movie places
    /// it with the "add" blend mode (<paramref name="add"/>); its own added parts always go there.
    /// </summary>
    void DrawAtom(HudMovieArt.Pic pic, Transform2D xf, Color modulate, bool add)
    {
        float k = 1f / pic.RenderScale;
        var t = xf * new Transform2D(0f, new Vector2(k, k), 0f, -pic.Origin * k);   // picture pixels -> virtual units
        var screen = _base * _localXf * t;
        var addMod = new Color(modulate.R * modulate.A, modulate.G * modulate.A, modulate.B * modulate.A, 1f);
        if (add) _adds.Add((pic.Normal, screen, addMod));
        else
        {
            DrawSetTransformMatrix(screen);
            DrawTexture(pic.Normal, Vector2.Zero, modulate);
            DrawSetTransformMatrix(_base * _localXf);
        }
        if (pic.Add is { } extra) _adds.Add((extra, screen, addMod));
    }

    /// <summary>The local frame currently set by <see cref="BeginLocal"/> (identity outside), so every piece lands in it.</summary>
    Transform2D _localXf = Transform2D.Identity;

    internal void DrawAdditive(CanvasItem layer)
    {
        foreach (var (tex, xf, mod) in _adds)
        {
            layer.DrawSetTransformMatrix(xf);
            layer.DrawTexture(tex, Vector2.Zero, mod);
        }
        layer.DrawSetTransformMatrix(Transform2D.Identity);
    }

    /// <summary>Movie pixels to virtual units for a movie anchored left (-1), centred (0) or right (1) on the screen (the stage is 1280 x 720, scaled by height).</summary>
    static Transform2D Stage(Vector2 vs, int anchor) =>
        new(0f, new Vector2(StageToVirtual, StageToVirtual), 0f, new Vector2(anchor < 0 ? 0f : anchor == 0 ? vs.X / 2f - 640f * StageToVirtual : vs.X - 1280f * StageToVirtual, 0f));

    static Transform2D ToXf(GfxMatrix m) => new((float)m.A, (float)m.B, (float)m.C, (float)m.D, (float)m.Tx, (float)m.Ty);

    /// <summary>A colour through a movie colour transform.</summary>
    static Color Through(Color c, GfxColourTransform cx) => new(
        Math.Clamp(c.R * cx.MulR + cx.AddR, 0f, 1f), Math.Clamp(c.G * cx.MulG + cx.AddG, 0f, 1f), Math.Clamp(c.B * cx.MulB + cx.AddB, 0f, 1f), Math.Clamp(c.A * cx.MulA + cx.AddA, 0f, 1f));

    /// <summary>
    /// Text in a movie's text field: the field's rectangle (2-pixel gutter), font height, alignment and face (<c>$BourgeoisBoldAlt</c>: the bold Bahnschrift, else the regular) under
    /// <paramref name="xf"/> (field pixels to virtual units; no rotation), in <paramref name="colour"/> (the field's own when null) through the placements' colour transform.
    /// </summary>
    void FieldText(HudMovie movie, GfxEditText t, string s, Transform2D xf, GfxColourTransform cx, Color? colour = null, float alpha = 1f, int outline = 0, float fontScale = 1f)
    {
        if (s.Length == 0) return;
        var c0 = colour ?? new Color(t.Colour.R, t.Colour.G, t.Colour.B, t.Colour.A);
        var c = Through(c0, cx); c.A *= alpha;
        if (c.A <= 0.003f) return;
        float sy = xf.Y.Length();
        int size = Math.Max(1, (int)MathF.Round((float)t.FontHeight * sy * fontScale));
        int style = movie.Movie.FontNames.GetValueOrDefault(t.FontId, "").Contains("Bold", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        var tl = xf * new Vector2((float)t.X0 + 2f, (float)t.Y0 + 2f);
        var br = xf * new Vector2((float)t.X1 - 2f, (float)t.Y1 - 2f);
        float ascent = FontOf(style)?.GetAscent(size) ?? size * 0.8f;
        float x = t.Align == 1 ? br.X : t.Align == 2 ? (tl.X + br.X) / 2f : tl.X;
        Txt(s, x, tl.Y + ascent, size, c, t.Align == 1 ? 1 : t.Align == 2 ? 0 : -1, outline, style);
    }

    /// <summary>
    /// Draws what a walk of a movie character shows (<see cref="HudMovie.Walk"/>): pieces rendered once and drawn with their matrices and colours, text fields filled by
    /// <paramref name="textOf"/> (by the field's instance path; null: not drawn) in <paramref name="colourOf"/>'s colour (null: the field's own). <paramref name="root"/> takes the
    /// character's movie pixels to virtual units.
    /// </summary>
    void DrawWalk(HudMovie movie, int character, int frame, Transform2D root, Func<string, int, int?>? frameOf, Func<int, bool> atom,
        Func<string, string?> textOf, Func<string, Color?>? colourOf = null, float alpha = 1f, Func<string, bool>? skip = null, float fontScale = 1f)
    {
        if (_movieArt is null) return;
        string name = movie.Name;
        movie.Walk(character, frame, frameOf, atom, item =>
        {
            var xf = root * ToXf(item.Matrix);
            if (item.Text is { } text)
            {
                if (textOf(item.Path) is { } s) FieldText(movie, text, s, xf, item.Colour, colourOf?.Invoke(item.Path), alpha, 0, fontScale);
                return;
            }
            float a = Math.Clamp(item.Colour.MulA + item.Colour.AddA, 0f, 1f) * alpha;
            if (a <= 0.003f) return;
            int ch = item.Character, fr = item.Frame;
            // The colour transform's colour part is rendered into the piece (a tint such as the lascannon bar's red, the reward lines' white stamp); its alpha is drawn.
            var c = item.Colour;
            float mr = Q(c.MulR), mg = Q(c.MulG), mb = Q(c.MulB), ar = Q(c.AddR), ag = Q(c.AddG), ab = Q(c.AddB);
            bool plain = mr == 1f && mg == 1f && mb == 1f && ar == 0f && ag == 0f && ab == 0f;
            string key = plain ? $"{name}:{ch}:{fr}" : $"{name}:{ch}:{fr}:{mr},{mg},{mb},{ar},{ag},{ab}";
            var tint = new GfxColourTransform(mr, mg, mb, 1f, ar, ag, ab, 0f);
            var pic = _movieArt.Get(key, (m, s) => Of(m, name)?.Render(ch, fr, s, colour: plain ? null : tint));
            if (pic is null) return;
            DrawAtom(pic, xf, new Color(1f, 1f, 1f, a), item.Add);
        }, skip);
    }

    /// <summary>A colour transform value rounded to 1/20 (so a fading clip reuses a few renders).</summary>
    static float Q(float v) => MathF.Round(v * 20f) / 20f;

    static HudMovie? Of(HudMovies m, string name) => name switch { "Hud" => m.Hud, "HudText" => m.Text, "HudEventLog" => m.EventLog, "Decorator" => m.Decorator, _ => null };

    // ---------------------------------------------------------------- reticles

    /// <summary>The reticle's spread sprite inside a family's reticle ("crosshair"), its labels and frame count; null when it has none.</summary>
    (int Character, IReadOnlyDictionary<string, int> Labels, int Frames)? SpreadClip(HudMovie hud, string name)
    {
        if (hud.Find($"{ReticleRules.Container}/{name}/crosshair") is not { } f) return null;
        return (f.Placement.Character, hud.Labels(f.Placement.Character), hud.FrameCount(f.Placement.Character));
    }

    double _braceOnAt = double.NegativeInfinity, _braceOffAt = double.NegativeInfinity;
    bool _wasBraced;

    /// <summary>
    /// The original reticle of the weapon in hand at the screen centre <paramref name="c"/>: its family's idle reticle, the active one with an enemy under the crosshair; the spread
    /// families' ticks at the frame of the current scatter (<see cref="ReticleRules"/>), the plasma weapons' charge clip by the charge, the heavy bolter's brace clip at 30 fps.
    /// Nothing for the stalker bolter and the lascannon (owner). False when the movie is not there (the caller draws the M44 reticle).
    /// </summary>
    bool DrawMovieReticle(Vector2 c)
    {
        var d = Data;
        if (ScopeArt.KindOf(d.WeaponWargear) is not null) return true;   // owner, 2026-10-29: the scoped weapons show no reticle outside the scope
        if (_movieArt is null || _movieArt.Failed) return false;
        if (HudMovie is not { } hud) return !_movieArt.Failed && _movieArt.Movies is null;   // still reading: nothing for a moment
        var spec = ReticleRules.For(d.WeaponWargear, _art?.Art?.WeaponType(d.WeaponWargear)) ?? ReticleRules.All[0];
        string name = d.AimedAtEnemy && spec.Active is { } active ? active : spec.Idle;
        string path = $"{ReticleRules.Container}/{name}";
        int top = 0;
        Func<int, int>? nested = null;
        string key;
        if (spec.Spread && SpreadClip(hud, name) is { } clip)
        {
            var range = ReticleRules.SpreadRange(clip.Labels, clip.Frames, d.Aiming);
            int frame = ReticleRules.SpreadFrame(range, d.SpreadFraction);
            int cid = clip.Character;
            nested = ch => ch == cid ? frame : 0;
            key = $"ret:{name}:{frame}";
            WarmSpread(hud, spec);
            // Until this frame is built, the nearest built one.
            if (_movieArt.Get(key, (m, s) => m.Hud?.RenderAt(path, ReticleRules.Container, s, 0, nested)) is null)
                for (int step = 1; step < clip.Frames; step++)
                {
                    foreach (int f in new[] { frame - step, frame + step })
                        if (f >= 0 && f < clip.Frames && _movieArt.Known($"ret:{name}:{f}") && _movieArt.Get($"ret:{name}:{f}", (_, _) => null) is { } near)
                        { DrawReticlePic(near, c); return true; }
                }
        }
        else
        {
            var placed = hud.Find(path);
            if (placed is null) return false;
            int ch = placed.Value.Placement.Character;
            var labels = hud.Labels(ch);
            int count = hud.FrameCount(ch);
            top = spec.Family switch
            {
                "plasma_gun" or "plasma_pistol" or "plasma_cannon" => ChargeFrame(labels, count),
                "heavy_bolter" => BraceFrame(labels, count),
                _ => 0,
            };
            key = $"ret:{name}:{top}";
        }
        int topFrame = top;
        var pic = _movieArt.Get(key, (m, s) => m.Hud?.RenderAt(path, ReticleRules.Container, s, topFrame, nested));
        if (pic is not null) DrawReticlePic(pic, c);
        return true;
    }

    void DrawReticlePic(HudMovieArt.Pic pic, Vector2 c) => DrawPic(pic, c + ReticleContainer * StageToVirtual);

    /// <summary>Asks for every spread frame of a family's two reticles once (rendered in the background, so a bloom finds its frame built).</summary>
    void WarmSpread(HudMovie hud, ReticleSpec spec)
    {
        if (!_warmed.Add(spec.Family) || _movieArt is null) return;
        foreach (var name in new[] { spec.Idle, spec.Active })
        {
            if (name is null || SpreadClip(hud, name) is not { } clip) continue;
            string path = $"{ReticleRules.Container}/{name}";
            int cid = clip.Character;
            for (int f = 0; f < clip.Frames; f++)
            {
                int frame = f;
                _movieArt.Get($"ret:{name}:{frame}", (m, s) => m.Hud?.RenderAt(path, ReticleRules.Container, s, 0, ch => ch == cid ? frame : 0));
            }
        }
    }

    /// <summary>The plasma reticles' charge clip (<c>alt_fire_fade</c> .. <c>alt_fire</c>) at the charge (0 while not charging): <b>interpretation</b> of the labels.</summary>
    int ChargeFrame(IReadOnlyDictionary<string, int> labels, int count)
    {
        float charge = Data.Charge;
        if (charge < 0f || !labels.TryGetValue("alt_fire_fade", out int from) || !labels.TryGetValue("alt_fire", out int to)) return 0;
        return Math.Clamp((int)MathF.Round(from + (to - from) * Math.Clamp(charge, 0f, 1f)), 0, count - 1);
    }

    /// <summary>
    /// The heavy bolter reticle's brace clip: from the brace's start <c>alt_fire_fade</c> .. <c>alt_fire</c> played at 30 fps and held, from its end <c>normal_fade</c> to the clip's
    /// end, then the first frame (<b>interpretation</b> of the labels; the movie's own timeline rate).
    /// </summary>
    int BraceFrame(IReadOnlyDictionary<string, int> labels, int count)
    {
        bool braced = Data.Braced;
        if (braced && !_wasBraced) _braceOnAt = _time;
        if (!braced && _wasBraced) _braceOffAt = _time;
        _wasBraced = braced;
        if (!labels.TryGetValue("alt_fire_fade", out int on) || !labels.TryGetValue("alt_fire", out int held)) return 0;
        if (braced) return Math.Min(held, on + (int)((_time - _braceOnAt) * ReticleRules.FramesPerSecond));
        if (labels.TryGetValue("normal_fade", out int off))
        {
            int f = off + (int)((_time - _braceOffAt) * ReticleRules.FramesPerSecond);
            if (f < count) return f;
        }
        return 0;
    }

    /// <summary>
    /// The original hit marker (<c>hit_mc</c>: four gold diagonal ticks, added, fading over its clip's 10 frames at 30 fps: alpha 1, 0.77, 0.56 ... 0); every hit, a kill too (the movie
    /// has no kill marker). False when the movie is not there.
    /// </summary>
    bool DrawMovieHitMarker(Vector2 c, double since)
    {
        if (HudMovie is not { } hud || _movieArt is null) return false;
        if (hud.Find($"{ReticleRules.Container}/{ReticleRules.HitMarker}") is not { } placed || !hud.Movie.Sprites.TryGetValue(placed.Placement.Character, out var sprite)) return false;
        int frame = (int)(since * ReticleRules.FramesPerSecond);
        if (frame < 0 || frame >= sprite.Frames.Count) return true;
        // The clip only fades one shape: rendered once at full alpha (frame 1), drawn with each frame's alpha.
        float alpha = 0f;
        foreach (var p in sprite.Frames[frame].Display.Values) alpha = MathF.Max(alpha, p.Colour?.MulA ?? 1f);
        string path = $"{ReticleRules.Container}/{ReticleRules.HitMarker}";
        var pic = _movieArt.Get("hit", (m, s) => m.Hud?.RenderAt(path, ReticleRules.Container, s, 1));
        if (pic is null) return true;
        DrawPic(pic, c + ReticleContainer * StageToVirtual, new Color(1f, 1f, 1f, alpha));
        return true;
    }
}

/// <summary>The HUD's "add" blend layer (the movies' added glows and lines), drawn over the HUD in screen pixels.</summary>
public sealed partial class HudAddLayer : Control
{
    readonly Hud _owner;

    public HudAddLayer(Hud owner)
    {
        _owner = owner; Name = "HudAdd";
        MouseFilter = MouseFilterEnum.Ignore; SetAnchorsPreset(LayoutPreset.FullRect);
        Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
    }

    public override void _Draw() => _owner.DrawAdditive(this);
}
