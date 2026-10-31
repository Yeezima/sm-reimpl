using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmFormats.Gfx;

namespace SmClient;

// ui/hud-2: the armour and health bar drawn from the HUD movie (Hud.gfx `unifiedhealth_mc`) like the other panels. The movie holds three bar clips: `healthbar_mc` (100 frames: the red
// fill, frame 0 full, frame 99 empty; its low-health frames carry the hit flash clips), `ironhalo_mc` (100 frames, **cyan**: frames 0 to 14 a flash with the medallion's starburst
// shrinking in, frame 15 the whole bar, frames 16 to 99 the bar cut shorter with its white end cap, frame 99 only the starburst) and `lightdamagebar_mc` (the same armour bar in
// **cream** lines with a plain diamond outline, 110 frames, cut by a mask). ui/hud-bar-colours: the Iron Halo perk's carrier gets `ironhalo_mc`, everyone else `lightdamagebar_mc`, each in the movie's own
// colours (the owner's reference frames of an Assault Marine show the cream bar: spec "Armour bar: the plain clip"). Place and scale are the movie's own (they match the footage within 2 stage pixels).
public sealed partial class Hud
{
    const string Vitals = "unifiedhealth_mc";

    /// <summary><c>--hud-vitals-demo &lt;prefix&gt;</c> (screenshot aid, ui/armour-bar-fix): in a match, steps the own bars through nine states (full; half armour; no armour with half health; no armour
    /// with low health; no armour; half health; low health; both half; low armour), saving the window as <c>&lt;prefix&gt;_N.png</c> for each, then quits.</summary>
    static readonly string? VitalsDemo = ReadVitalsDemo();
    double _vitalsDemoAt;
    int _vitalsDemoStep;
    static readonly (float Health, float Shield)[] VitalsDemoStates = [(1f, 1f), (1f, 0.5f), (0.5f, 0f), (0.2f, 0f), (1f, 0f), (0.5f, 1f), (0.2f, 1f), (0.5f, 0.5f), (1f, 0.2f)];

    static string? ReadVitalsDemo()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--hud-vitals-demo");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    void RunVitalsDemo()
    {
        if (VitalsDemo is null || !Data.InGame) return;
        var d = Data;
        _vitalsDemoAt += GetProcessDeltaTime();
        if (_vitalsDemoStep >= VitalsDemoStates.Length) { GetTree().Quit(); return; }
        var (h, s) = VitalsDemoStates[_vitalsDemoStep];
        d.Alive = true; d.Health = d.HealthMax * h; d.Shield = d.ShieldMax * s;
        if (_vitalsDemoAt < 3.0) return;
        GD.Print(GetViewport().GetTexture().GetImage().SavePng($"{VitalsDemo}_{_vitalsDemoStep}.png") == Error.Ok ? $"vitals shot {_vitalsDemoStep} (halo {d.IronHalo}) -> {VitalsDemo}_{_vitalsDemoStep}.png" : "vitals shot failed");
        _vitalsDemoStep++; _vitalsDemoAt = 2.0;
    }

    /// <summary>
    /// The footage's health bar is a pure red (232, 0, 20 to 36; its medallion a red diamond) where the movie's is an orange-red gradient with an orange medallion (255, 48 to 145, 40):
    /// the green channel is taken out and the red and blue brought down (a runtime tint the data does not carry): measured on the reference frames (ours).
    /// </summary>
    static readonly GfxColourTransform HealthTint = new(0.9f, 0f, 0.7f, 1f, 0f, 0f, 0f, 0f);

    /// <summary>
    /// ui/hud-bar-colours: the plain bar's health fill on the owner's three reference frames of the original (an Assault Marine without Iron Halo) is a warmer orange-red than the movie's own: the
    /// core about F7 36 11 (FF491B, FA3203, F03115 sampled) and the edges about EE 79 2E, where the movie draws E7 08 00 and EC 58 03: the green lifted (most in the core) and the blue brought down,
    /// fitted on our own window shots (measured, ours; the Iron Halo's bar keeps <see cref="HealthTint"/>).
    /// </summary>
    static readonly GfxColourTransform PlainHealthTint = new(1f, 0.73f, 0.3f, 1f, 0.03f, 0.13f, 0f, 0f);

    // ui/hud-3: no bar flash any more (owner, 2026-10-30): the own damage is a full-screen vignette (DrawDamageVignette).
    float _vitalsHealth = -1f, _vitalsShield = -1f;
    bool _vitalsAlive;
    double _vignetteAt = double.NegativeInfinity;
    bool _vignetteHealth;
    GradientTexture2D? _vignetteTex;

    /// <summary>
    /// ui/hud-bar-colours (owner 2026-10-31, third pass): the bar without the Iron Halo perk is the movie's own plain armour clip <c>lightdamagebar_mc</c>, cut by the armour like the cyan one: cream lines
    /// (251, 252, 174 in the data; the original's footage samples FFF2C5, F9F5BB, FAEFB1 on the owner's three reference frames), a cream diamond outline with no spikes, placed added. Before, everyone
    /// without the perk got the Iron Halo clip flat-tinted saturated yellow (FDCA0D), which the original never shows.
    /// </summary>
    const string PlainArmourClip = "lightdamagebar_mc";

    /// <summary>The health clip's low-health flash pieces (frames 50 to 99): the additive white bar <c>c558</c> is drawn as the normal red one <c>c550</c> and the flash clip <c>health_flash_mc</c> <c>c559</c> as the normal diamond <c>c553</c> (the frames below 50 have them); the white end cap <c>c557</c> is left out (it turned magenta under the Halo's tint).</summary>
    static readonly HashSet<int> HealthFlash = [557];
    static readonly Dictionary<int, int> HealthBarSwap = new() { [558] = 550, [559] = 553 };

    // The vignette's colours: the armour bar's cyan (Iron Halo) or the plain clip's cream (251, 252, 174: the data's line colour); the health bar's red (Iron Halo's tinted bar) or the reference's orange-red
    // (FF461A: the fill's core sampled on the owner's reference frame of the plain bar).
    static readonly Color ArmourVignette = new(0.35f, 0.95f, 0.9f), HealthVignetteRed = new(0.91f, 0.02f, 0.1f), HealthVignetteOrange = new(1f, 0.275f, 0.1f), ArmourVignetteCream = new(0.984f, 0.988f, 0.682f);
    const double VignetteSeconds = 0.55;
    const float VignetteAlpha = 0.6f;

    /// <summary>
    /// The own damage as a flash of the screen's edges (ours, owner 2026-10-30): the armour bar's colour when only the armour went down, the health bar's when the health did.
    /// A death, a respawn or a first frame starts nothing. Fades out over <see cref="VignetteSeconds"/>.
    /// </summary>
    void DrawDamageVignette(Vector2 vs)
    {
        var d = Data;
        if (d.Alive && _vitalsAlive && (d.Health < _vitalsHealth - 0.01f || d.Shield < _vitalsShield - 0.01f))
        {
            _vignetteAt = _time;
            _vignetteHealth = d.Health < _vitalsHealth - 0.01f;
        }
        if (!d.Alive) _vignetteAt = double.NegativeInfinity;
        _vitalsHealth = d.Health; _vitalsShield = d.Shield; _vitalsAlive = d.Alive;
        double age = _time - _vignetteAt;
        if (age < 0.0 || age >= VignetteSeconds) return;
        _vignetteTex ??= new GradientTexture2D
        {
            Width = 256, Height = 256, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1.05f, 0.5f),
            Gradient = new Gradient { Offsets = [0f, 0.45f, 1f], Colors = [new Color(1f, 1f, 1f, 0f), new Color(1f, 1f, 1f, 0.18f), new Color(1f, 1f, 1f, 1f)] },
        };
        var col = _vignetteHealth ? (Data.IronHalo ? HealthVignetteRed : HealthVignetteOrange) : (Data.IronHalo ? ArmourVignette : ArmourVignetteCream);
        float k = 1f - (float)(age / VignetteSeconds);
        col.A = VignetteAlpha * k * k;
        DrawTextureRect(_vignetteTex, new Rect2(Vector2.Zero, vs), false, col);
    }

    /// <summary>
    /// The bar from <c>unifiedhealth_mc</c>: the dark band, the cyan armour bar cut by the armour, the health bar cut by the health (added, as the movie places it). With the Iron Halo perk the
    /// armour bar has its starburst medallion and the health bar its measured red; without it (ui/hud-bar-colours, the owner's reference) the armour bar is the movie's plain cream clip
    /// <c>lightdamagebar_mc</c> (no spikes, never blue) and the health bar the reference's orange-red (<see cref="PlainHealthTint"/>). False without the movie (the caller draws the M44 bar).
    /// </summary>
    /// <summary>Armour bar layers the last movie bar drew (ui-selftest: one, or none for an empty plain bar; -1 until the movie bar has been drawn).</summary>
    public int VitalsArmourLayers { get; private set; } = -1;

    /// <summary>Share of the health clip's drawn pixels that are white or blue-ish (ui-selftest: none at any health level, with or without Iron Halo; -1 until drawn).</summary>
    public float VitalsHealthWhiteShare { get; private set; } = -1f;
    HudMovieArt.Pic? _healthShareFor;

    void MeasureHealthShare(HudMovieArt.Pic pic)
    {
        if (ReferenceEquals(pic, _healthShareFor)) return;
        _healthShareFor = pic;
        var img = pic.Normal.GetImage();
        int drawn = 0, light = 0;
        for (int y = 0; y < img.GetHeight(); y++)
            for (int x = 0; x < img.GetWidth(); x++)
            {
                var c = img.GetPixel(x, y);
                if (c.A < 0.5f) continue;
                drawn++;
                if ((c.R > 0.75f && c.G > 0.75f && c.B > 0.75f) || c.B > c.R) light++;
            }
        if (pic.Add is { } add)   // what the movie places additively (the flash's white) is a second picture
        {
            var ai = add.GetImage();
            for (int y = 0; y < ai.GetHeight(); y++)
                for (int x = 0; x < ai.GetWidth(); x++)
                {
                    var c = ai.GetPixel(x, y);
                    if (c.R > 0.75f && c.G > 0.75f && c.B > 0.75f) { drawn++; light++; }
                }
        }
        VitalsHealthWhiteShare = drawn == 0 ? 0f : light / (float)drawn;
    }

    /// <summary>
    /// The armour bar picture last drawn (ui-selftest, ui/hud-bar-colours): the share of its bright pixels that are cyan (blue over red), saturated yellow (blue under 40 % of red: the old flat tint
    /// made every pixel so; the cream clip's own glow has some) and cream or white (red and green over 85 %, blue over 55 %), over both layers; -1 until drawn.
    /// </summary>
    public float VitalsArmourCyanShare { get; private set; } = -1f;
    public float VitalsArmourYellowShare { get; private set; } = -1f;
    public float VitalsArmourCreamShare { get; private set; } = -1f;
    HudMovieArt.Pic? _armourShareFor;

    void MeasureArmourShare(HudMovieArt.Pic pic)
    {
        if (ReferenceEquals(pic, _armourShareFor)) return;
        _armourShareFor = pic;
        int bright = 0, cyan = 0, yellow = 0, cream = 0;
        foreach (var tex in new[] { pic.Normal, pic.Add })
        {
            if (tex is null) continue;
            var img = tex.GetImage();
            for (int y = 0; y < img.GetHeight(); y++)
                for (int x = 0; x < img.GetWidth(); x++)
                {
                    var c = img.GetPixel(x, y);
                    if (tex == pic.Normal && c.A < 0.5f) continue;
                    if (MathF.Max(c.R, MathF.Max(c.G, c.B)) < 0.6f) continue;   // the lines and the medallion, not their faint glow
                    bright++;
                    if (c.B > c.R + 0.1f) cyan++;
                    else if (c.R > 0.6f && c.G > 0.5f && c.B < 0.4f * c.R) yellow++;
                    if (c.R > 0.85f && c.G > 0.85f && c.B > 0.55f && c.B <= c.R + 0.1f) cream++;
                }
        }
        VitalsArmourCyanShare = bright == 0 ? 0f : cyan / (float)bright;
        VitalsArmourYellowShare = bright == 0 ? 0f : yellow / (float)bright;
        VitalsArmourCreamShare = bright == 0 ? 0f : cream / (float)bright;
    }

    bool DrawMovieVitals(Vector2 vs)
    {
        if (OldHud || HudMovie is not { } hud || _movieArt is null) return false;
        if (hud.Find(Vitals) is not { } unified || hud.Find($"{Vitals}/healthbar_mc") is not { } health || hud.Find($"{Vitals}/ironhalo_mc") is null || hud.Find($"{Vitals}/background_mc") is null) return false;
        var d = Data;
        bool halo = d.IronHalo;
        float hf = Math.Clamp(d.Health / MathF.Max(d.HealthMax, 1f), 0f, 1f);
        float sf = Math.Clamp(d.Shield / MathF.Max(d.ShieldMax, 1f), 0f, 1f);
        // The movie's own place (463.05, 65.75, 0.9 turned upside down: its y axis is flipped, as its band's) is the footage's: cyan lines 434 to 814 and 78 to 114 of the 1280 x 720 stage.
        var xf = Stage(vs, 0) * ToXf(unified.ToStage);
        int hframe = VitalsRules.HealthFrame(hf, d.Alive);
        // Iron Halo: the cyan ironhalo_mc with its starburst; everyone else: the movie's own plain clip lightdamagebar_mc in its own colours (ui/hud-bar-colours).
        int aframe = halo ? VitalsRules.ArmourFrame(sf, d.Alive) : VitalsRules.PlainArmourFrame(sf, d.Alive);
        bool armourEmpty = !halo && aframe == VitalsRules.PlainEmpty;
        string armourPath = $"{Vitals}/{(halo ? "ironhalo_mc" : PlainArmourClip)}";
        string v = halo ? "h" : "p";

        var bg = _movieArt.Get("vit:bg", (m, s) => m.Hud?.RenderAt($"{Vitals}/background_mc", Vitals, s));
        var arm = armourEmpty ? null : _movieArt.Get($"vit:a{v}:{aframe}", (m, s) => m.Hud?.RenderAt(armourPath, Vitals, s, aframe), "vit:a", 24);
        if (arm is not null) MeasureArmourShare(arm);
        var healthLocal = unified.ToStage.Inverse() is { } invUnified ? invUnified.After(health.ToStage) : health.ToStage;
        var hp = _movieArt.Get($"vit:h{v}:{hframe}", (m, s) =>
        {
            // ui/armour-bar-fix (owner 2026-10-31): from frame 50 on the health clip swaps its bar for an additive white flash version (c558) and adds the flash clip (c559): drawn still they turn
            // the fill white (pink with the Halo's tint). They are the movie's low-health flash, which the own-damage vignette replaces (ui/hud-3), so the fill stays its normal red.
            GfxRaster.Hidden = HealthFlash; GfxRaster.Swap = HealthBarSwap;
            try { return m.Hud?.Render(health.Placement.Character, hframe, s, healthLocal, null, colour: halo ? HealthTint : PlainHealthTint, add: health.Placement.Blend == 8); }
            finally { GfxRaster.Hidden = null; GfxRaster.Swap = null; }
        }, "vit:h", 24);
        if (bg is not null) DrawAtom(bg, xf, White, false);
        // One armour bar layer only (ui/armour-bar-fix): the Iron Halo's cyan clip or the plain cream clip, never both. The plain clip's empty frame (99) is its bare unmasked lines: nothing is drawn
        // at no armour (the Iron Halo's frame 99 is its starburst alone and stays).
        VitalsArmourLayers = 0;
        if (arm is not null) { DrawAtom(arm, xf, White, false); VitalsArmourLayers++; }
        if (hp is not null) { MeasureHealthShare(hp); DrawAtom(hp, xf, White, false); }
        return true;
    }
}
