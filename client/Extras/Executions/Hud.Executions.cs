using System;
using Godot;
using SmSim.Extras.Executions;

namespace SmClient;

/// <summary>
/// Executions X6 on the HUD (forwards in <c>Hud.ExecutionsHooks.cs</c>, only with <see cref="SmSim.ContentExtras.Executions"/>):
/// <list type="bullet">
/// <item><b>The Execute prompt</b> (<see cref="ExecutionsHud"/>): the networked view hands it over in <see cref="ExecutionPrompt"/> (text and place in virtual units) only while the server's offer or
/// alignment says an execution is possible. It is drawn in the pick-up prompt's style (owner 2026-11-15, one text style per level): the key in the pick-up prompt's box (dark fill, the movie's gold
/// border of 3 units, the key at 18 bold), the label beside it at 22 bold with the HUD's outline; every edge snapped to whole screen pixels so the border stays crisp and symmetric. Live labels only,
/// nothing painted. A device without a binding shows the label alone, <c>EXECUTE (unbound: Controls)</c>.</item>
/// <item><b>The armour tint</b> (<see cref="ExecutionsArmourTint"/>): while the own armour is above its bar (Warfront's overshield), the armour bar is modulated toward <see cref="OvershieldTint"/>,
/// the more so the fuller the overshield (<see cref="ArmourTintFor"/>), so it fades back as the overshield goes. White (the movie's own colours) otherwise, and always in Classic PvP (no overshield there).</item>
/// </list>
/// </summary>
public sealed partial class Hud
{
    /// <summary>The armour bar's modulate at a full overshield (RGB; ours, the owner's "deeper blue armour HUD"; spec/behavior.md "camera, input, HUD and shield (X6)").</summary>
    public static readonly Color OvershieldTint = new(0.36f, 0.56f, 1f);
    /// <summary>The share of <see cref="OvershieldTint"/> at the smallest overshield (the rest grows with the overshield's fraction; ours).</summary>
    public const float OvershieldTintFloor = 0.45f;

    /// <summary>Prompts drawn (the self-test's counter) and the last text drawn.</summary>
    public int ExecutionPromptsDrawn { get; private set; }
    public string ExecutionPromptLast { get; private set; } = "";
    /// <summary>The armour tint applied this frame (White: none; the self-test reads it).</summary>
    public Color ExecutionArmourTintLast { get; private set; } = White;

    /// <summary>Screen pixels to the HUD's virtual units (the networked view places the prompt with the camera's projection).</summary>
    public Vector2 ScreenToVirtual(Vector2 screen)
    {
        float scale = Size.Y / RefHeight;
        return scale > 0f ? screen / scale : screen;
    }

    /// <summary>The armour bar's modulate for <paramref name="shield"/> of <paramref name="shieldMax"/>: White at or under the bar, else <see cref="OvershieldTint"/> by the overshield's fraction.</summary>
    public static Color ArmourTintFor(float shield, float shieldMax)
    {
        float cap = ExecutionRules.OvershieldCap(shieldMax);
        if (!(cap > 0f) || !(shield > shieldMax)) return White;
        float f = Math.Clamp(ExecutionRules.Overshield(shield, shieldMax) / cap, 0f, 1f);
        return White.Lerp(OvershieldTint, OvershieldTintFloor + (1f - OvershieldTintFloor) * f);
    }

    /// <summary>The X6 test aids' overshield (a fraction of the cap; null: the own record's), for the tint only.</summary>
    public static float? ExecutionsTestOvershield { get; set; }
    /// <summary>The armour bar's tint hook has run (the movie's bar is drawn in this run).</summary>
    public bool ExecutionsTintCalled { get; private set; }

    partial void ExecutionsArmourTint(ref Color tint)
    {
        var d = Data;
        float shield = ExecutionsTestOvershield is { } f ? d.ShieldMax * (1f + Math.Clamp(f, 0f, 1f) * ExecutionRules.OvershieldBars) : d.Shield;
        tint = d.Alive ? ArmourTintFor(shield, d.ShieldMax) : White;
        ExecutionArmourTintLast = tint;
        ExecutionsTintCalled = true;
    }

    partial void ExecutionsHud(Vector2 vs)
    {
        var d = Data;
        if (!d.Alive || d.Spectating || ExecutionPrompt is not { } prompt || prompt.Text.Length == 0) return;
        var (key, label) = ExecutionPromptRules.Split(prompt.Text);
        const int keySize = 18, labelSize = 22;
        float px = 1f / MathF.Max(_scale, 1e-3f);   // one screen pixel in virtual units
        float Snap(float v) => MathF.Round(v / px) * px;
        float labelW = TextWidth(label, labelSize, 2);
        float half = 13.5f * StageToVirtual;   // the pick-up prompt's key box (27 stage pixels)
        float gap = 10f;
        float width = key is null ? labelW : half * 2f + gap + labelW;
        float x = Snap(Math.Clamp(prompt.At.X - width / 2f, 8f, MathF.Max(8f, vs.X - width - 8f)));
        float y = Snap(Math.Clamp(prompt.At.Y, 40f, vs.Y - 40f));   // the label's baseline sits on the target's screen point
        if (key is not null)
        {
            var box = new Rect2(x, Snap(y - half * 2f + 6f), Snap(half * 2f), Snap(half * 2f));
            DrawRect(box, new Color(0.02f, 0.02f, 0.02f, 0.75f));
            DrawRect(box, new Color(MovieGold, 0.95f), false, 3f);
            Txt(key, box.GetCenter().X, box.GetCenter().Y + 7f, keySize, White, 0, 0, 2);
            x += box.Size.X + gap;
        }
        Txt(label, x, y, labelSize, White, -1, 4, 2);
        ExecutionPromptsDrawn++;
        ExecutionPromptLast = prompt.Text;
    }
}
