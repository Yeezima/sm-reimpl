using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmFormats.Gfx;

namespace SmClient;

// ui/hud-2: the armour and health bar drawn from the HUD movie (Hud.gfx `unifiedhealth_mc`) like the other panels. The movie holds three bar clips: `healthbar_mc` (100 frames: the red
// fill, frame 0 full, frame 99 empty; its low-health frames carry the hit flash clips), `ironhalo_mc` (100 frames, **cyan**: frames 0 to 14 a flash with the medallion's starburst
// shrinking in, frame 15 the whole bar, frames 16 to 99 the bar cut shorter with its white end cap, frame 99 only the starburst) and `lightdamagebar_mc` (the same armour bar in
// **gold**, 110 frames). The footage's armour bar is the cyan one in 22 of the 24 samples that show armour lines (one every 4 s), so it is drawn from `ironhalo_mc` as the movie has it (no tint needed); the gold clip is not drawn
// (which state of the original shows it is script, not data: spec "HUD from the movies"). Place and scale are the movie's own (they match the footage within 2 stage pixels).
public sealed partial class Hud
{
    const string Vitals = "unifiedhealth_mc";

    /// <summary>
    /// The footage's health bar is a pure red (232, 0, 20 to 36; its medallion a red diamond) where the movie's is an orange-red gradient with an orange medallion (255, 48 to 145, 40):
    /// the green channel is taken out and the red and blue brought down (a runtime tint the data does not carry): measured on the reference frames (ours).
    /// </summary>
    static readonly GfxColourTransform HealthTint = new(0.9f, 0f, 0.7f, 1f, 0f, 0f, 0f, 0f);

    /// <summary>The flash's frames (<c>ironhalo_mc</c>'s first 15: <see cref="VitalsRules.FlashLast"/>).</summary>
    const int BurstFrames = VitalsRules.FlashLast;

    /// <summary>The placement depth of the starburst (<c>c540</c>, added) in <c>ironhalo_mc</c>'s flash frames.</summary>
    const int BurstDepth = 16;

    double _vitalsHitAt = double.NegativeInfinity;
    float _vitalsHealth = -1f, _vitalsShield = -1f;
    bool _vitalsAlive;

    // The characters of the health bar's two hit-flash clips (named bar_mc and health_flash_mc in its low-health frames), by frame.
    readonly Dictionary<int, (int Bar, int Flash)> _healthFlashChars = new();

    /// <summary>
    /// The bar from <c>unifiedhealth_mc</c>: the dark band, the cyan armour bar cut by the armour, the red health bar cut by the health (added, as the movie places it), and a flash when the
    /// own player takes damage (the starburst of <c>ironhalo_mc</c>'s first frames shrinking in over 0.5 s at 30 fps; the health bar's flash clips at low health). False without the movie
    /// (the caller draws the M44 bar).
    /// </summary>
    bool DrawMovieVitals(Vector2 vs)
    {
        if (OldHud || HudMovie is not { } hud || _movieArt is null) return false;
        if (hud.Find(Vitals) is not { } unified || hud.Find($"{Vitals}/healthbar_mc") is not { } health || hud.Find($"{Vitals}/ironhalo_mc") is not { } halo || hud.Find($"{Vitals}/background_mc") is null) return false;
        var d = Data;
        float hf = Math.Clamp(d.Health / MathF.Max(d.HealthMax, 1f), 0f, 1f);
        float sf = Math.Clamp(d.Shield / MathF.Max(d.ShieldMax, 1f), 0f, 1f);
        // Damage to the own player (health or armour going down while alive): the flash. A death, a respawn or a first frame starts nothing.
        if (d.Alive && _vitalsAlive && (d.Health < _vitalsHealth - 0.01f || d.Shield < _vitalsShield - 0.01f)) _vitalsHitAt = _time;
        if (!d.Alive) _vitalsHitAt = double.NegativeInfinity;
        _vitalsHealth = d.Health; _vitalsShield = d.Shield; _vitalsAlive = d.Alive;
        int since = (int)Math.Min(1000.0, Math.Max(0.0, (_time - _vitalsHitAt) * ReticleRules.FramesPerSecond));

        // The movie's own place (463.05, 65.75, 0.9 turned upside down: its y axis is flipped, as its band's) is the footage's: cyan lines 434 to 814 and 78 to 114 of the 1280 x 720 stage.
        var xf = Stage(vs, 0) * ToXf(unified.ToStage);
        int hframe = VitalsRules.HealthFrame(hf, d.Alive), aframe = VitalsRules.ArmourFrame(sf, d.Alive);
        // The health bar's flash clips exist only in its frames from 50 on: they play from the hit and rest at their last frame.
        int flash = hframe >= 50 ? Math.Min(9, since) : 0;
        var (barChar, flashChar) = HealthFlashChars(hud, health.Placement.Character, hframe);
        string haloPath = $"{Vitals}/ironhalo_mc";

        var bg = _movieArt.Get("vit:bg", (m, s) => m.Hud?.RenderAt($"{Vitals}/background_mc", Vitals, s));
        var arm = _movieArt.Get($"vit:a:{aframe}", (m, s) => m.Hud?.RenderAt(haloPath, Vitals, s, aframe), "vit:a", 24);
        var healthLocal = unified.ToStage.Inverse() is { } invUnified ? invUnified.After(health.ToStage) : health.ToStage;
        var hp = _movieArt.Get($"vit:h:{hframe}:{flash}", (m, s) =>
            m.Hud?.Render(health.Placement.Character, hframe, s, healthLocal, ch => (ch == barChar || ch == flashChar) ? flash : 0, colour: HealthTint, add: health.Placement.Blend == 8), "vit:h", 24);
        if (bg is not null) DrawAtom(bg, xf, White, false);
        if (arm is not null) DrawAtom(arm, xf, White, false);
        if (hp is not null) DrawAtom(hp, xf, White, false);
        // The starburst flash over the armour bar's medallion (every other frame of the clip's 15: a picture each is as big as the burst).
        if (d.Alive && since <= BurstFrames)
        {
            int k = since & ~1;
            var rel = unified.ToStage.Inverse() is { } inv ? inv.After(halo.ToStage) : halo.ToStage;
            GfxPlacement? placed = null;
            if (hud.Movie.Sprites.TryGetValue(halo.Placement.Character, out var clip) && k < clip.Frames.Count) clip.Frames[k].Display.TryGetValue(BurstDepth, out placed);
            if (placed is { Character: >= 0 } burst)
            {
                GfxColourTransform? tint = null;
                float alpha = 1f;
                if (burst.Colour is { } cx)
                {
                    tint = new GfxColourTransform(Q(cx.MulR), Q(cx.MulG), Q(cx.MulB), 1f, Q(cx.AddR), Q(cx.AddG), Q(cx.AddB), 0f);
                    alpha = Math.Clamp(cx.MulA + cx.AddA, 0f, 1f);
                }
                var local = rel.After(HudMovie.PixelMatrix(burst.Matrix));
                bool add = burst.Blend == 8;
                var pic = _movieArt.Get($"vit:b:{k}", (m, s) => m.Hud?.Render(burst.Character, 0, s, local, null, colour: tint, add: add), "vit:b", 8);
                if (pic is not null) DrawAtom(pic, xf, new Color(1f, 1f, 1f, alpha), false);
            }
        }
        return true;
    }

    /// <summary>The characters of the health bar's <c>bar_mc</c> and <c>health_flash_mc</c> in its frame <paramref name="frame"/> (-1: it has none there).</summary>
    (int Bar, int Flash) HealthFlashChars(HudMovie hud, int healthSprite, int frame)
    {
        if (_healthFlashChars.TryGetValue(frame, out var known)) return known;
        int bar = -1, flash = -1;
        if (hud.Movie.Sprites.TryGetValue(healthSprite, out var sprite) && frame < sprite.Frames.Count)
            foreach (var p in sprite.Frames[frame].Display.Values)
            {
                if (p.Name == "bar_mc") bar = p.Character;
                else if (p.Name == "health_flash_mc") flash = p.Character;
            }
        // Only the multi-frame clips take the flash frame (a one-frame shape would be asked for its tenth).
        if (bar >= 0 && hud.FrameCount(bar) < 10) bar = -1;
        if (flash >= 0 && hud.FrameCount(flash) < 10) flash = -1;
        return _healthFlashChars[frame] = (bar, flash);
    }
}
