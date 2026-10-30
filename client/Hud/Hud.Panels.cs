using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmSim;

namespace SmClient;

// M44: the corner panels, the weapon-switch cards and the objective markers. Positions in the 1080-high virtual space, measured on the
// owner's footage (800 x 450 captures of a 1600 x 900 view: 1 frame pixel = 2.4 virtual units); spec "In-game HUD (M44)".
public sealed partial class Hud
{
    /// <summary>
    /// The team score panel, bottom left: the own side's emblem, the scores (own gold over enemy red) left of their bars, a dark panel
    /// tilted up to the right behind the bars, the score limit in large white digits at its right end, the time left above it in gold,
    /// and in Seize Ground the points' emblems above the bars (owner's emblem; gold own, red enemy, grey neutral).
    /// </summary>
    void DrawScorePanel(Vector2 vs)
    {
        var d = Data;
        if (!d.Teams) return;
        if (DrawMovieScorePanel(vs)) return;   // ui/hud-fidelity: the HudText movie's panel (Hud.Fidelity.cs)
        float b = vs.Y;   // bottom edge
        // The panel: a dark band whose top and bottom lines rise slightly to the right (footage: from (231, b-199) to (537, b-209) and
        // (216, b-135) to (534, b-145)); darker at the right, fading out at the left.
        var tl = new Vector2(231f, b - 199f); var tr = new Vector2(537f, b - 209f);
        var bl = new Vector2(216f, b - 135f); var br = new Vector2(534f, b - 145f);
        DrawPolygon([tl, tr, br, bl], [new Color(0f, 0f, 0f, 0.35f), new Color(0f, 0f, 0f, 0.8f), new Color(0f, 0f, 0f, 0.8f), new Color(0f, 0f, 0f, 0.35f)]);
        var line = new Color(0.85f, 0.85f, 0.82f, 0.55f);
        DrawLine(tl, tr, line, 2f, true);
        DrawLine(bl, br, line, 2f, true);
        // Own side's emblem.
        bool chaos = d.OwnTeam == Team.Chaos;
        SideEmblem(d.OwnTeam, new Vector2(chaos ? 159f : 138f, b - 166f), chaos ? 84f : 86f, Rel.Own);
        // Scores and bars (bars from x 276 to 497, own centred 183 above the bottom, enemy 161).
        Txt(d.OwnScore.ToString(), 269f, b - 175f, 24, Gold, 1, 3, 2);
        Txt(d.EnemyScore.ToString(), 269f, b - 151f, 24, RedText, 1, 3, 2);
        float limit = MathF.Max(d.ScoreLimit, 1);
        const float bx = 276f, bw = 221f;
        for (int i = 0; i < 2; i++)
        {
            float cy = b - (i == 0 ? 183f : 161f);
            float f = Math.Clamp((i == 0 ? d.OwnScore : d.EnemyScore) / limit, 0f, 1f);
            if (f <= 0f) continue;
            var tint = i == 0 ? new Color(1f, 0.86f, 0.12f) : new Color(0.95f, 0.08f, 0.06f);
            // The bar's picture is cut at the score; at least its pointed end shows.
            float shown = MathF.Max(f, 0.06f);
            if (!Sprite(HudSprites.ScoreBar, new Rect2(bx, cy - 13f, bw, 26f), tint, shown))
                DrawColoredPolygon(Slanted(bx, cy - 7f, bw * shown, 14f, 4f), tint);
            // The bright cap at the bar's end (footage).
            DrawLine(new Vector2(bx + bw * shown - 3f, cy - 6f), new Vector2(bx + bw * shown - 3f, cy + 6f), new Color(1f, 1f, 0.9f, 0.9f), 3f);
        }
        // Score limit and the time left.
        Txt(d.ScoreLimit > 0 ? d.ScoreLimit.ToString() : "-", 540f, b - 174f, 40, DigitWhite, -1, 4, 2);
        if (d.Clock.Length > 0) Txt(d.Clock, 534f, b - 226f, 26, ClockGold, -1, 3, 1);
        // Seize Ground: the points' emblems above the bars (x 346, 414, 482; centre 235 above the bottom).
        if (d.Seize)
            for (int i = 0; i < d.Points.Count; i++)
            {
                var p = d.Points[i];
                var c = new Vector2(346f + i * 68f, b - 235f);
                PointEmblem(p, c, 40f);
                if (p.Progress > 0.01f && p.Progress < 0.999f)
                    DrawArc(c, 25f, -MathF.PI / 2f, -MathF.PI / 2f + MathF.Tau * Math.Clamp(p.Progress, 0f, 1f), 24, PointColour(p.Side), 3f, true);
            }
    }

    /// <summary>A point's emblem: its owner's (gold own, red enemy, flashing while contested), the grey diamond while neutral.</summary>
    void PointEmblem(Point p, Vector2 c, float size)
    {
        float flash = PointBlink(p);   // fidelity gap 10: the movie's flash states (a contested point: the short combat flash)
        if (p.OwnerTeam == Team.None)
        {
            if (!SpriteAt(HudSprites.NeutralDiamond, c, size * 0.8f / HudSprites.NeutralDiamond.Height, new Color(NeutralTint, flash)))
                Poly([c + new Vector2(0, -size * 0.4f), c + new Vector2(size * 0.4f, 0), c + new Vector2(0, size * 0.4f), c + new Vector2(-size * 0.4f, 0)], new Color(0.05f, 0.05f, 0.05f, 0.7f), NeutralGrey, 2.5f);
            return;
        }
        SideEmblem(p.OwnerTeam, c, size, p.Owner == Rel.None ? Rel.Own : p.Owner, flash);
    }

    // ---------------------------------------------------------------- weapon box

    /// <summary>The weapon box's size in its own frame (virtual units).</summary>
    const float BoxW = 388f, BoxH = 122f;

    /// <summary>
    /// The weapon box, bottom right, and what hangs off it. Footage: the box's top-left corner 514 virtual units left of the right edge and
    /// 238 above the bottom, 388 x 122, turned 2.5 degrees clockwise (its top edge falls to the right; the original's is a slight perspective).
    /// Inside: the reserve in large thin digits top left, the clip as slanted ticks bottom left (charge weapons: the charge mark), the
    /// weapon's picture at the right, the heat bar along the bottom for heat weapons; the winged skull on the top edge. Left of it the
    /// grenade box; under it the jump pack meter (owner's request, M44: moved there from above the box) and the grenade launcher's line.
    /// </summary>
    void DrawWeaponBox(Vector2 vs)
    {
        var d = Data;
        if (!d.Alive) return;
        bool movie = d.HasWeapon && DrawMovieWeaponBox(vs);   // ui/hud-fidelity: the movie's box (Hud.Fidelity.cs); the jump meter and the lines under it stay
        BeginLocal(Placed(new Vector2(vs.X - 514f, vs.Y - 238f), 2.5f));
        if (movie)
        {
            string state = d.Overheated ? (d.Venting ? "OVERHEATED - VENTING" : "OVERHEATED") : d.WeaponState;
            if (state.Length > 0) Txt(state, BoxW - 14f, BoxH + 2f, 17, new Color(RedText.Lerp(Gold, 0.45f), 0.65f + 0.35f * MathF.Sin((float)_time * 10f)), 1, 3, 2);
        }
        if (d.HasWeapon && !movie) DrawBoxContents();
        if (d.HasGrenade && !movie) DrawGrenadeBox();
        float below = BoxH + (movie ? 26f : 10f);   // ui/hud-fidelity: the movie's box stands 1.12 times larger
        if (d.HasJump) { DrawJumpMeter(below); below += 34f; }
        if (d.GrenadesOutText.Length > 0) Txt(d.GrenadesOutText, BoxW - 6f, below + 18f, 17, new Color(0.9f, 0.9f, 0.9f), 1, 3);
        EndLocal();
    }

    void DrawBoxContents()
    {
        var d = Data;
        // Frame (ours: the atlas' frame pieces are not placed without the movie): dark fill, thin gold line, two chamfered corners.
        var pts = Chamfered(0f, 0f, BoxW, BoxH, 11f);
        DrawPolygon(pts, [PanelFill, PanelFill, new Color(0f, 0f, 0f, 0.86f), new Color(0f, 0f, 0f, 0.9f), new Color(0f, 0f, 0f, 0.9f), PanelFill]);
        Poly(pts, new Color(0f, 0f, 0f, 0f), FrameGold, 2f);
        // The winged skull sits on the top edge, a little right of the middle (skull centre at x 187).
        if (!Sprite(HudSprites.BoxOrnament, new Rect2(110f, -13f, HudSprites.BoxOrnament.Width, HudSprites.BoxOrnament.Height)))
            Skull(new Vector2(187f, -6f), 7f, new Color(1f, 0.95f, 0.6f));

        // Reserve (large, thin); without a reserve the clip, without either the endless sign.
        bool reserve = d.ReserveMax > 0 || d.Reserve > 0;
        if (reserve || d.ClipMax > 0)
        {
            int shown = reserve ? d.Reserve : d.Clip;
            bool empty = shown == 0 && d.Clip == 0;
            Txt(shown.ToString(), 16f, 46f, 42, empty ? RedText : DigitWhite, -1, 3, 1);
        }
        else if (!SpriteAt(HudSprites.Endless, new Vector2(40f, 30f), 1.3f)) Txt("-", 16f, 46f, 42, DigitWhite, -1, 3, 1);

        // Charge weapons show the charge mark under the digits (gold while a release fires the charged shot).
        if (d.HasCharge)
        {
            var mark = d.ChargeReady ? new Color(1f, 0.85f, 0.3f) : White;
            if (!SpriteAt(HudSprites.ChargeMark, new Vector2(34f, 64f), 1.3f, mark)) DrawCircle(new Vector2(34f, 64f), 14f, mark);
        }

        // Bottom row (footage t105, t150): heat weapons the heat; charge weapons without heat (the lascannon) the clip as the long red bar
        // (4 of 4 full, 3 of 4 shorter); while charging, the charge as a pale overlay on that bar, gold once a release fires the charged
        // shot (the overlay and its colour: ours). Other weapons: the clip ticks.
        var barRect = new Rect2(12f, 84f, 342f, 40f);
        bool bar = d.HasHeat || d.HasCharge;
        if (bar)
        {
            float f;
            Color tint = White;
            if (d.HasHeat)
            {
                f = Math.Clamp(d.Heat / MathF.Max(d.HeatMax, 1f), 0f, 1f);
                float blink = d.Overheated ? 0.55f + 0.45f * MathF.Sin((float)_time * 14f) : 1f;
                if (d.Overheated) tint = new Color(1f, blink, blink);
                // Fidelity gap 7: venting pales the bar with a slow pulse (ours; the original's cue is not known).
                if (d.Venting) tint = tint.Lerp(new Color(0.85f, 0.95f, 1f), 0.45f + 0.25f * MathF.Sin((float)_time * 6f));
            }
            else f = d.ClipMax > 0 ? Math.Clamp(d.Clip / (float)d.ClipMax, 0f, 1f) : 0f;
            DrawRect(new Rect2(16f, 99f, 334f, 10f), new Color(0.25f, 0.02f, 0.02f, 0.55f));   // the empty track (ours)
            if (!Sprite(HudSprites.HeatBar, barRect, tint, f)) DrawRect(new Rect2(16f, 98f, 334f * f, 12f), RedFill);
            if (d.HasCharge && d.Charge >= 0f)
            {
                var over = d.ChargeReady ? new Color(1f, 0.86f, 0.25f) : new Color(1f, 1f, 1f, 0.85f);
                float c = Math.Clamp(d.Charge, 0.04f, 1f);
                if (!Sprite(HudSprites.ScoreBar, new Rect2(16f, 94f, 330f, 20f), over, c)) DrawRect(new Rect2(18f, 101f, 326f * c, 6f), over);
            }
        }
        else DrawTicks();

        // The weapon's picture at the right (smaller and higher above a bar, so the bar runs under it as in the footage).
        if (!WeaponPicture(d.WeaponWargear, bar ? new Vector2(311f, 48f) : new Vector2(311f, 60f), bar ? 1.15f : 1.42f, White))
            Txt(d.WeaponName, BoxW - 18f, 66f, 24, new Color(0.9f, 0.9f, 0.88f), 1, 3);

        // Reloading, switching, recharging, overheated: small blinking words at the bottom right (ours; the original's cue is not known).
        string state = d.Overheated ? (d.Venting ? "OVERHEATED - VENTING" : "OVERHEATED") : d.WeaponState;
        if (state.Length > 0)
        {
            float blink = 0.65f + 0.35f * MathF.Sin((float)_time * 10f);
            Txt(state, BoxW - 14f, BoxH - 10f, 17, new Color(RedText.Lerp(Gold, 0.45f), blink), 1, 3, 2);
        }
    }

    /// <summary>The clip as slanted ticks from the original strip (loaded white, spent dark): 9.8 virtual units apart, 22 tall (footage).</summary>
    void DrawTicks()
    {
        var d = Data;
        var (ticks, loaded) = HudRules.Ticks(d.Clip, d.ClipMax);
        const float x0 = 16f, y0 = 88f, step = 9.8f, h = 22f;
        for (int i = 0; i < ticks; i++)
        {
            var dest = new Rect2(x0 + i * step, y0, step, h);
            bool on = i < loaded;
            if (!Sprite(on ? HudSprites.TickLoaded : HudSprites.TickSpent, dest, on ? null : new Color(1f, 1f, 1f, 0.9f)))
                DrawColoredPolygon(Slanted(dest.Position.X, y0, step - 3f, h, -4f), on ? White : new Color(0.1f, 0.1f, 0.1f, 0.9f));
        }
    }

    /// <summary>The grenade box left of the weapon box (footage: 82 x 122, flush with it): one picture per grenade held, the cooldown under them.</summary>
    void DrawGrenadeBox()
    {
        var d = Data;
        const float gx = -86f, gw = 80f;
        var pts = Chamfered(gx, 0f, gw, BoxH, 9f);
        DrawPolygon(pts, [PanelFill, PanelFill, new Color(0f, 0f, 0f, 0.86f), new Color(0f, 0f, 0f, 0.9f), new Color(0f, 0f, 0f, 0.9f), PanelFill]);
        Poly(pts, new Color(0f, 0f, 0f, 0f), d.Grenades > 0 ? FrameGold : new Color(0.45f, 0.4f, 0.2f, 0.9f), 2f);
        int n = Math.Min(d.Grenades, 4);
        for (int i = 0; i < n; i++)
        {
            var c = new Vector2(gx + 22f + (i % 2) * 34f, BoxH - 24f - (i / 2) * 40f);
            if (!SpriteAt(HudSprites.Grenade, c, 1.1f)) DrawCircle(c, 11f, new Color(0.55f, 0.65f, 0.25f));
        }
        if (d.Grenades > 4 || d.Grenades == 0) Txt(d.Grenades.ToString(), gx + gw / 2f, 46f, 32, d.Grenades > 0 ? DigitWhite : RedText, 0, 3, 1);
        if (d.StimLeft > 0f) Txt($"STIM {d.StimLeft:F1}", gx + gw / 2f, 30f, 15, Gold, 0, 3);   // perks/inert-2 (ours)
        else if (d.GrenadeCooldown > 0f) Txt($"{d.GrenadeCooldown:F1}", gx + gw / 2f, 30f, 18, Gold, 0, 3);
        else if (d.Throwing) Txt("THROW", gx + gw / 2f, 30f, 15, Gold, 0, 3);
    }

    /// <summary>
    /// The jump pack meter, tucked under the weapon box (owner's request, M44): the pack's original picture and a thin bar of the power
    /// left; pale blue, orange when less than a take-off is left; the jump's phase in small letters at its end (ours).
    /// </summary>
    void DrawJumpMeter(float y)
    {
        var d = Data;
        float f = Math.Clamp(d.JumpPower / MathF.Max(d.JumpMax, 1f), 0f, 1f);
        float x = 56f, w = 250f;
        bool icon = WeaponPicture(d.JumpWargear.Length > 0 ? d.JumpWargear : "sm_mp_abil_jumppack", new Vector2(30f, y + 12f), 0.5f, new Color(1f, 1f, 1f, 0.95f));
        if (!icon) x = 8f;
        DrawRect(new Rect2(x, y + 7f, w, 10f), new Color(0f, 0f, 0f, 0.6f));
        var tint = d.JumpLow ? new Color(1f, 0.5f, 0.15f) : new Color(0.55f, 0.82f, 1f);
        if (f > 0f && !Sprite(HudSprites.ScoreBar, new Rect2(x - 2f, y + 1f, w + 4f, 22f), tint, f))
            DrawRect(new Rect2(x, y + 8f, w * f, 8f), tint);
        if (d.JumpPhase.Length > 0) Txt(d.JumpPhase.ToUpperInvariant(), x + w + 10f, y + 18f, 15, new Color(0.85f, 0.9f, 1f, 0.85f), -1, 2, 2);
    }

    // ---------------------------------------------------------------- weapon-switch cards

    /// <summary>
    /// The weapon-switch cards (footage): the pistol's card above the crosshair (x -140..151, y -212..-100 from the centre), the primary's at
    /// its right (x 108..420, y -58..58); the weapon in hand in the original's orange frame with a gold chevron pointing at it ("^" under
    /// the pistol card, ">" left of the primary card), the other dim with a white chevron. Each card: "clip/reserve", the name, the picture.
    /// </summary>
    void DrawSwitchCards(Vector2 c)
    {
        var d = Data;
        double age = _time - _popupAt;
        if (age > PopupSeconds || !d.HasWeapon || !d.HasOther) return;
        float a = (float)Math.Clamp(PopupSeconds - age, 0.0, 0.4) / 0.4f;
        if (d.HasThird)
        {
            // Protocol 44: three weapons (Weapon Versatility). The cards by slot: pistol above the crosshair, the primary at its right, the second primary under it.
            var slot = (WeaponSlot)d.SlotKey;
            var bySlot = new (string Ammo, string Name, string Wargear)[3];
            bySlot[(int)slot] = (HudRules.CardAmmo(d.Clip, d.ClipMax, d.Reserve), d.WeaponName, d.WeaponWargear);
            bySlot[(int)Loadouts.Other(slot)] = (HudRules.CardAmmo(d.OtherClip, d.OtherClipMax, d.OtherReserve), d.OtherName, d.OtherWargear);
            bySlot[(int)Loadouts.Third(slot)] = (HudRules.CardAmmo(d.ThirdClip, d.ThirdClipMax, d.ThirdReserve), d.ThirdName, d.ThirdWargear);
            var pistolCard = bySlot[(int)WeaponSlot.Pistol]; var primaryCard = bySlot[(int)WeaponSlot.Ranged]; var secondCard = bySlot[(int)WeaponSlot.Second];
            Card(new Rect2(c.X - 140f, c.Y - 212f, 291f, 112f), pistolCard.Ammo, pistolCard.Name, pistolCard.Wargear, slot == WeaponSlot.Pistol, a);
            Card(new Rect2(c.X + 108f, c.Y - 120f, 312f, 116f), primaryCard.Ammo, primaryCard.Name, primaryCard.Wargear, slot == WeaponSlot.Ranged, a);
            Card(new Rect2(c.X + 108f, c.Y + 4f, 312f, 116f), secondCard.Ammo, secondCard.Name, secondCard.Wargear, slot == WeaponSlot.Second, a);
            Chevron(new Vector2(c.X, c.Y - 82f), 0f, slot == WeaponSlot.Pistol, a);
            Chevron(new Vector2(c.X + 86f, c.Y - 62f), MathF.PI / 2f, slot == WeaponSlot.Ranged, a);
            Chevron(new Vector2(c.X + 86f, c.Y + 62f), MathF.PI / 2f, slot == WeaponSlot.Second, a);
            return;
        }
        bool pistolInHand = d.SlotKey == (int)WeaponSlot.Pistol;
        (string Ammo, string Name, string Wargear) inHand = (HudRules.CardAmmo(d.Clip, d.ClipMax, d.Reserve), d.WeaponName, d.WeaponWargear);
        (string Ammo, string Name, string Wargear) holstered = (HudRules.CardAmmo(d.OtherClip, d.OtherClipMax, d.OtherReserve), d.OtherName, d.OtherWargear);
        var pistol = pistolInHand ? inHand : holstered;
        var primary = pistolInHand ? holstered : inHand;
        Card(new Rect2(c.X - 140f, c.Y - 212f, 291f, 112f), pistol.Ammo, pistol.Name, pistol.Wargear, pistolInHand, a);
        Card(new Rect2(c.X + 108f, c.Y - 58f, 312f, 116f), primary.Ammo, primary.Name, primary.Wargear, !pistolInHand, a);
        // Chevrons: "^" under the pistol card, ">" left of the primary card.
        Chevron(new Vector2(c.X, c.Y - 82f), 0f, pistolInHand, a);
        Chevron(new Vector2(c.X + 86f, c.Y), MathF.PI / 2f, !pistolInHand, a);
    }

    void Card(Rect2 r, string ammo, string name, string wargear, bool active, float a)
    {
        float k = active ? a : a * 0.55f;
        DrawRect(r.Grow(-3f), new Color(0.01f, 0.01f, 0.012f, (active ? 0.82f : 0.6f) * a));
        if (active && _art?.NinePatch(HudSprites.CardFrame, 12f) is { } frame)
        {
            frame.ModulateColor = new Color(1f, 1f, 1f, a);
            DrawStyleBox(frame, r.Grow(7f));
        }
        else if (active) DrawRect(r, new Color(1f, 0.62f, 0.05f, a), false, 3f);
        else DrawRect(r, new Color(0.62f, 0.62f, 0.62f, 0.6f * a), false, 1.5f);
        Txt(ammo, r.Position.X + 16f, r.Position.Y + 42f, 32, new Color(DigitWhite, k), -1, 3, 1);
        // The name shrinks (down to 13) to stay left of the picture.
        string label = _art?.Art?.WeaponName(wargear) ?? name;
        int size = 19;
        while (size > 13 && TextWidth(label, size) > r.Size.X - 130f) size--;
        Txt(label, r.Position.X + 16f, r.Position.Y + r.Size.Y - 18f, size, new Color(0.9f, 0.9f, 0.88f, k), -1, 3);
        WeaponPicture(wargear, new Vector2(r.End.X - 60f, r.Position.Y + 46f), 0.78f, new Color(1f, 1f, 1f, k));
    }

    void Chevron(Vector2 c, float rotation, bool active, float a)
    {
        BeginLocal(new Transform2D(rotation, c));
        bool drawn = active
            ? SpriteAt(HudSprites.ChevronGold, Vector2.Zero, 0.8f, new Color(1f, 1f, 1f, a))
            : SpriteAt(HudSprites.ChevronWhite, Vector2.Zero, 0.7f, new Color(1f, 1f, 1f, 0.7f * a));
        if (!drawn) DrawPolyline([new(-12, 6), new(0, -6), new(12, 6)], active ? new Color(Gold, a) : new Color(1f, 1f, 1f, 0.6f * a), 3f);
        EndLocal();
    }

    // ---------------------------------------------------------------- objective markers

    /// <summary>
    /// Seize Ground objective markers (footage): the owner's emblem with the point's letter on it, "DEFEND" or "CAPTURE" in small gold
    /// capitals above; a neutral point: its letter on a dark plate. Clamped to the screen edges when off-screen (ours), distance under it (ours).
    /// </summary>
    void DrawObjectives(Vector2 vs)
    {
        var d = Data;
        if (!d.Seize || !d.Alive) return;
        var c = vs / 2f;
        const float mx = 80f, myTop = 215f, myBottom = 290f;   // the margins keep clamped markers off the bar and the corner panels
        var placed = new List<Vector2>();
        foreach (var p in d.Points)
        {
            var pos = p.Screen / _scale;
            bool inside = !p.Behind && pos.X > mx && pos.X < vs.X - mx && pos.Y > myTop && pos.Y < vs.Y - myBottom;
            if (!inside)
            {
                var dir = pos - c;
                if (p.Behind) dir = -dir;
                if (dir.LengthSquared() < 1f) dir = new Vector2(0f, 1f);
                float sx = dir.X != 0f ? (vs.X / 2f - mx) / MathF.Abs(dir.X) : float.MaxValue;
                float sy = dir.Y != 0f ? ((dir.Y > 0 ? vs.Y / 2f - myBottom : vs.Y / 2f - myTop)) / MathF.Abs(dir.Y) : float.MaxValue;
                pos = c + dir * MathF.Min(sx, sy);
            }
            // Markers that would sit on top of one another (e.g. two points in the same direction) are spread sideways.
            for (int tries = 0; tries < 4 && placed.Exists(q => MathF.Abs(q.X - pos.X) < 60f && MathF.Abs(q.Y - pos.Y) < 70f); tries++) pos.X += 64f;
            placed.Add(pos);
            bool defend = p.Owner == Rel.Own && p.Progress >= 0.999f && !p.Contested;
            var textCol = p.Contested ? White.Lerp(RedText, 0.5f + 0.5f * MathF.Sin((float)_time * 12f)) : ClockGold;
            // ui/hud-fidelity: an owned or contested point and a point being captured show the decorator movie's art (footage t 8 s: the gold triangle, the gold eagle); a quiet
            // neutral point keeps the M44 dark plate (footage t 20 s shows a dark plate, not the movie's grey triangle).
            if (PointLabel(p) != "neutral" && DrawMoviePointMarker(p, pos, PointBlink(p)))
            {
                // ui/hud-fidelity: the decorator movie's marker; the word above it (footage), the capture bar; no distance (the footage shows none).
                Txt(defend ? "DEFEND" : "CAPTURE", pos.X, pos.Y - 44f, 16, textCol, 0, 3, 2);
                DrawCaptureBar(p, new Vector2(pos.X, pos.Y + 41f));
                continue;
            }
            if (p.OwnerTeam == Team.None)
            {
                float blink = PointBlink(p);
                Poly(Chamfered(pos.X - 15f, pos.Y - 17f, 30f, 34f, 5f), new Color(0.03f, 0.03f, 0.03f, 0.8f * blink), new Color(0.75f, 0.75f, 0.75f, 0.7f * blink), 1.5f);
                Txt(p.Letter, pos.X, pos.Y + 9f, 26, new Color(1f, 1f, 1f, blink), 0, 2, 2);
            }
            else
            {
                PointEmblem(p, pos, 46f);
                Txt(p.Letter, pos.X, pos.Y + 9f, 24, p.Owner == Rel.Enemy ? new Color(0.25f, 0f, 0f) : new Color(0.3f, 0.2f, 0f), 0, 0, 2);
            }
            Txt(defend ? "DEFEND" : "CAPTURE", pos.X, pos.Y - 30f, 16, textCol, 0, 3, 2);
            DrawCaptureBar(p, new Vector2(pos.X, pos.Y + 41f));   // fidelity gap 10: the point decorator's capture bar
            Txt($"{p.Distance:F0} m", pos.X, pos.Y + 62f, 14, new Color(0.85f, 0.85f, 0.85f, 0.85f), 0, 3);
        }
    }
}
