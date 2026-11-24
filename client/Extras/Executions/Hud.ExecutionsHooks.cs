using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// Executions X5a: the HUD's executions seams (<c>ExtrasArmourTint</c> in Hud.Vitals.cs, <c>ExtrasHudDraw</c> in Hud.cs), forwarding to X6's partials
/// (<c>Hud.Executions.cs</c>: the overshield's deeper blue, the Execute prompt) only when the match's content (<see cref="Hud.Content"/>) carries
/// <see cref="ContentExtras.Executions"/>. What X6 draws it is handed by the networked view through <see cref="ExecutionPrompt"/>.
/// </summary>
public sealed partial class Hud
{
    /// <summary>The prompt X6 draws this frame (its text and its place in virtual units), or null; set by the networked view each frame.</summary>
    public (string Text, Vector2 At)? ExecutionPrompt { get; set; }

    bool ExecutionsOn => Content.HasFlag(ContentExtras.Executions);

    partial void ExtrasArmourTint(ref Color tint)
    {
        if (ExecutionsOn) ExecutionsArmourTint(ref tint);
    }

    partial void ExtrasHudDraw(Vector2 vs)
    {
        if (ExecutionsOn) ExecutionsHud(vs);
    }

    /// <summary>X6: the armour bar's modulate while an overshield lasts (left White otherwise).</summary>
    partial void ExecutionsArmourTint(ref Color tint);
    /// <summary>X6: the Execute prompt.</summary>
    partial void ExecutionsHud(Vector2 vs);
}
