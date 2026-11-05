namespace SmContent;

/// <summary>
/// fix/plasma-visuals (owner, 2026-11-06: "make the crosshair heat tracker, cross hair ... match exactly the original game"): the plasma weapons' gauges at the crosshair, drawn from the
/// HUD movie's reticle container (<c>Hud.gfx</c> <c>Reticule_Ingame_0_mc</c>, read at runtime; nothing copied):
/// <list type="bullet">
/// <item><b>Heat</b>: <c>heat_mc</c> (labels <c>normal</c>, <c>heavy</c>, <c>plasmacannon</c>), an arc under the reticle: <c>meter_mc</c> (60 frames, yellow to orange, the movie's
/// <c>vent_txt</c> showing on its last frame) and <c>overheat_mc</c> (labels <c>normal </c> hidden, <c>danger</c> an orange-white flash on frames 1 .. 11).</item>
/// <item><b>Charge</b>: <c>PlasmaCharge_mc</c> (plasma gun and pistol) or <c>PlasmaCannonCharge_mc</c> (the same clip, larger), a cyan arc over the reticle that fills from its ends
/// to its spike over 60 frames and glows on the last.</item>
/// </list>
/// Which weapon picks which label and arc, and which value drives which clip, is an <b>interpretation</b> of the names (the script that drives them is in the movie's ActionScript,
/// not read); the frame rate is the movie's 30 fps.
/// </summary>
public static class PlasmaGaugeRules
{
    /// <summary>The heat arc's instance under the reticle container.</summary>
    public const string Heat = "heat_mc";
    /// <summary>The heat arc's fill clip and its overheat flash, under <see cref="Heat"/>.</summary>
    public const string Meter = "meter_mc", Overheat = "overheat_mc";
    /// <summary>The overheat flash's label for the overheated state (its first frame, <c>normal </c>, draws nothing).</summary>
    public const string DangerLabel = "danger";

    /// <summary>Whether a reticle family (<see cref="ReticleSpec.Family"/>) is a plasma weapon's (the three that show the gauges).</summary>
    public static bool IsPlasma(string family) => family is "plasma_gun" or "plasma_pistol" or "plasma_cannon";

    /// <summary>The heat arc's label for a family: the plasma cannon's larger arc, else the normal one (<c>heavy</c> is the heavy bolter's, not drawn here).</summary>
    public static string HeatLabel(string family) => family == "plasma_cannon" ? "plasmacannon" : "normal";

    /// <summary>The charge arc's instance for a family (the cannon's is the same clip placed larger).</summary>
    public static string ChargeArc(string family) => family == "plasma_cannon" ? "PlasmaCannonCharge_mc" : "PlasmaCharge_mc";

    /// <summary>The heat arc's frame for the heat (0 cold .. <paramref name="frames"/> - 1 at the overheat amount, the frame that shows <c>vent_txt</c>).</summary>
    public static int MeterFrame(float heat, float overheat, int frames)
    {
        int last = Math.Max(0, frames - 1);
        if (!(overheat > 0f) || !float.IsFinite(heat)) return 0;
        return (int)MathF.Round(Math.Clamp(heat / overheat, 0f, 1f) * last);
    }

    /// <summary>
    /// The charge arc's frame: 0 (the dim empty arc) when not charging (<paramref name="charge"/> negative), else the charge toward the last firing stage over the clip
    /// (its last frame, the glow, once that stage is reached). <b>Interpretation</b>: the first frames hold the empty arc, the data's own lead-in.
    /// </summary>
    public static int ChargeFrame(float charge, int frames)
    {
        int last = Math.Max(0, frames - 1);
        if (!float.IsFinite(charge) || charge < 0f) return 0;
        return (int)MathF.Round(Math.Clamp(charge, 0f, 1f) * last);
    }

    /// <summary>
    /// The overheat flash's frame: its first (nothing) unless <paramref name="overheated"/>, then the <c>danger</c> frames looped at 30 fps from the overheat's start
    /// (<paramref name="since"/> seconds ago). <paramref name="danger"/> is the label's frame (1 on the install), <paramref name="frames"/> the clip's length.
    /// </summary>
    public static int OverheatFrame(bool overheated, double since, int danger, int frames)
    {
        if (!overheated || frames <= 1) return 0;
        danger = Math.Clamp(danger, 0, frames - 1);
        int span = Math.Max(1, frames - danger);
        int step = double.IsFinite(since) && since > 0 ? (int)(since * ReticleRules.FramesPerSecond) : 0;
        return danger + step % span;
    }

    /// <summary>A nested clip's frame on a 30 fps clock (<paramref name="since"/> seconds), looped over its <paramref name="frames"/>.</summary>
    public static int Looped(double since, int frames) => frames <= 1 || !double.IsFinite(since) || since <= 0 ? 0 : (int)(since * ReticleRules.FramesPerSecond) % frames;
}
