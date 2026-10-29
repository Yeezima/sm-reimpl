using SmSim;

namespace SmContent;

/// <summary>
/// Pure rules of the client's hit feedback (M38), kept out of the Godot client so that they can be tested: when the own player's state change is a damage flash, and what the client
/// really knows of a victim's armour. Client presentation only; nothing here is simulated.
/// </summary>
public static class HitFeedbackRules
{
    /// <summary>A shield or health drop smaller than this is not damage (float noise of the snapshots' 1/255 quantisation and of reconciliation).</summary>
    public const float DamageEpsilon = 0.01f;

    /// <summary>
    /// The damage the own player took between two states, for the screen flash: (shield lost, health lost); (0, 0) when there is none to show. A flash needs a living player in both
    /// states (a respawn restores shield and health and a death has the death vignette instead), and only a fall counts (regeneration and healing raise the values).
    /// </summary>
    public static (float Shield, float Health) FlashDamage(bool wasAlive, float oldShield, float oldHealth, bool alive, float shield, float health)
    {
        if (!wasAlive || !alive) return (0f, 0f);
        float lostShield = oldShield - shield, lostHealth = oldHealth - health;
        return (lostShield > DamageEpsilon ? lostShield : 0f, lostHealth > DamageEpsilon ? lostHealth : 0f);
    }

    /// <summary>The own shield above this is a raised armour shield.</summary>
    public const float ShieldUp = 0.05f;

    /// <summary>
    /// Whether the victim's armour shield was up when a player hit landed, where the client really knows; null: unknown (the effects then use the armour variant). No guessing.
    /// <paramref name="forced"/>: the <c>--hit-armour</c> test aid; <paramref name="victimIsOwn"/>: the own shield <paramref name="ownShieldBefore"/> a little before the hit decides
    /// (the hit that breaks the shield counts as armour); <paramref name="armoured"/>: the protocol bit (<c>ShotView.Armoured</c> / <c>HitReport.LastArmoured</c>) once wired.
    /// Not a player hit: null.
    /// </summary>
    public static bool? ArmourUp(ShotHit hit, bool? forced, bool victimIsOwn, float ownShieldBefore, bool? armoured)
    {
        if (hit is not (ShotHit.Body or ShotHit.Head)) return null;
        if (forced is { } f) return f;
        if (victimIsOwn) return ownShieldBefore > ShieldUp;
        return armoured;
    }
}
