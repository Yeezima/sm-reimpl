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

    /// <summary><c>--hud-vitals-demo &lt;prefix&gt;</c> (screenshot aid, ui/armour-bar-fix): in a match, steps the own bars through six states (full, half armour, no armour, half health, low health, both half), saving the window as <c>&lt;prefix&gt;_N.png</c> for each, then quits.</summary>
    static readonly string? VitalsDemo = ReadVitalsDemo();
    double _vitalsDemoAt;
    int _vitalsDemoStep;
    static readonly (float Health, float Shield)[] VitalsDemoStates = [(1f, 1f), (1f, 0.5f), (1f, 0f), (0.5f, 1f), (0.2f, 1f), (0.5f, 0.5f)];

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

    // ui/hud-3: no bar flash any more (owner, 2026-10-30): the own damage is a full-screen vignette (DrawDamageVignette).
    float _vitalsHealth = -1f, _vitalsShield = -1f;
    bool _vitalsAlive;
    double _vignetteAt = double.NegativeInfinity;
    bool _vignetteHealth;
    GradientTexture2D? _vignetteTex;

    /// <summary>The plain armour bar's yellow (ours, owner 2026-10-30; the original's frame is yellow-gold, a flat colour in place of the Iron Halo clip's cyan and white).</summary>
    static readonly GfxColourTransform ArmourYellow = new(0f, 0f, 0f, 1f, 1f, 0.8f, 0.05f, 0f);

    /// <summary>Starburst medallion of the armour clip (<c>c540</c>): the Iron Halo's bar keeps it, the reference's plain bar leaves it out.</summary>
    static readonly HashSet<int> NoMedallion = [540];

    // The vignette's colours: the armour bar's cyan; the health bar's red (Iron Halo's tinted bar) or the movie's own orange-red (the reference bar).
    static readonly Color ArmourVignette = new(0.35f, 0.95f, 0.9f), HealthVignetteRed = new(0.91f, 0.02f, 0.1f), HealthVignetteOrange = new(1f, 0.28f, 0.1f), ArmourVignetteYellow = new(1f, 0.8f, 0.05f);
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
        var col = _vignetteHealth ? (Data.IronHalo ? HealthVignetteRed : HealthVignetteOrange) : (Data.IronHalo ? ArmourVignette : ArmourVignetteYellow);
        float k = 1f - (float)(age / VignetteSeconds);
        col.A = VignetteAlpha * k * k;
        DrawTextureRect(_vignetteTex, new Rect2(Vector2.Zero, vs), false, col);
    }

    /// <summary>
    /// The bar from <c>unifiedhealth_mc</c>: the dark band, the cyan armour bar cut by the armour, the health bar cut by the health (added, as the movie places it). With the Iron Halo perk the
    /// armour bar has its starburst medallion and the health bar its measured red; without it (ui/hud-3, the owner's reference) the bar has no medallion spikes, a plain yellow armour bar (never blue or white) and the movie's own orange-red health. False without the movie (the caller draws the M44 bar).
    /// </summary>
    /// <summary>Armour bar layers the last movie bar drew (ui-selftest: one, or none for an empty plain bar; -1 until the movie bar has been drawn).</summary>
    public int VitalsArmourLayers { get; private set; } = -1;

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
        int hframe = VitalsRules.HealthFrame(hf, d.Alive), aframe = VitalsRules.ArmourFrame(sf, d.Alive);
        string haloPath = $"{Vitals}/ironhalo_mc";
        string v = halo ? "h" : "p";

        var bg = _movieArt.Get("vit:bg", (m, s) => m.Hud?.RenderAt($"{Vitals}/background_mc", Vitals, s));
        var arm = _movieArt.Get($"vit:a{v}:{aframe}", (m, s) =>
        {
            GfxRaster.Hidden = halo ? null : NoMedallion;
            try
            {
                if (halo || m.Hud is not { } movie || movie.Find(haloPath) is not { } found || unified.ToStage.Inverse() is not { } invU) return m.Hud?.RenderAt(haloPath, Vitals, s, aframe);
                // ui/hud-r2 (owner, 2026-10-30): everyone but the Iron Halo's carrier has the plain yellow armour bar: the clip's own cyan and white replaced by the yellow (ours).
                return movie.Render(found.Placement.Character, aframe, s, invU.After(found.ToStage), null, colour: ArmourYellow, add: found.Placement.Blend == 8);
            }
            finally { GfxRaster.Hidden = null; }
        }, "vit:a", 24);
        var healthLocal = unified.ToStage.Inverse() is { } invUnified ? invUnified.After(health.ToStage) : health.ToStage;
        var hp = _movieArt.Get($"vit:h{v}:{hframe}", (m, s) =>
            m.Hud?.Render(health.Placement.Character, hframe, s, healthLocal, null, colour: halo ? HealthTint : null, add: health.Placement.Blend == 8), "vit:h", 24);
        if (bg is not null) DrawAtom(bg, xf, White, false);
        // ui/armour-bar-fix (owner 2026-10-31): the gold lightdamagebar_mc frame (a whole second armour bar, gold, full length) is no longer drawn under the yellow bar: one armour bar only.
        // Frame 99 of the clip is the starburst alone: it belongs to the Iron Halo perk, so everyone else draws nothing for an empty armour (ui/armour-bar-fix).
        VitalsArmourLayers = 0;
        if (arm is not null && (halo || aframe != VitalsRules.ArmourEmpty)) { DrawAtom(arm, xf, White, false); VitalsArmourLayers++; }
        if (hp is not null) DrawAtom(hp, xf, White, false);
        return true;
    }
}
