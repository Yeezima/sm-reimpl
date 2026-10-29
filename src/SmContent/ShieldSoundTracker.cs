namespace SmContent;

/// <summary>The armour shield's sound triggers in one frame (the player blueprint's <c>health_ext/armourshield_info</c> actions).</summary>
[Flags]
public enum ShieldSoundEvents
{
    None = 0,
    /// <summary><c>armourshield_broken</c>: the shield reached 0 (<c>play_armor_break</c>: a crack and, for the local player, the warning loop).</summary>
    Broken = 1,
    /// <summary><c>armourshield_hit</c>: the shield dropped but is not broken.</summary>
    Hit = 2,
    /// <summary><c>armourshield_regen</c> start: regeneration began (<c>play_armor_restore</c>, which also stops the warning loop).</summary>
    RegenStart = 4,
    /// <summary><c>armourshield_regen</c> end before the shield was full: damage interrupted it (the binding's stop event <c>stop_armor_restore_early</c>).</summary>
    RegenInterrupted = 8,
    /// <summary><c>armourshield_restored</c>: the shield is full again.</summary>
    Restored = 16,
    /// <summary>The player died or (re)spawned: its looping sounds end with that life.</summary>
    LifeChanged = 32,
}

/// <summary>
/// Turns the own player's shield values, frame by frame, into the blueprint's shield triggers (sound/shield-regen). Regeneration is a span, not a value: it
/// begins on the first rise of the shield (after the data's delay, 7 s, at any level below the maximum: interpretation of <c>armourshield_regen</c>), and ends when
/// the shield is full or drops again (damage). A frame in which the shown shield does not change (the frame rate is higher than the 30 Hz tick) neither starts nor
/// ends it, so the start sound plays once per regeneration. Before this rule the client posted the start only when the shield rose from 0, and nothing ended the
/// broken-shield warning loop when the player died with the shield down.
/// </summary>
public sealed class ShieldSoundTracker
{
    const float Epsilon = 1e-4f, FullMargin = 0.01f;
    bool _regenerating;

    /// <summary>Whether a regeneration is under way (started and not yet full or interrupted).</summary>
    public bool Regenerating => _regenerating;

    /// <summary>
    /// One frame: the shield before and now, its maximum, whether the player was alive before and is now. A death or a spawn reports only
    /// <see cref="ShieldSoundEvents.LifeChanged"/> (the spawn's full shield is not a regeneration).
    /// </summary>
    public ShieldSoundEvents Step(float before, float now, float max, bool aliveBefore, bool aliveNow)
    {
        if (aliveBefore != aliveNow) { _regenerating = false; return ShieldSoundEvents.LifeChanged; }
        if (!aliveNow) return ShieldSoundEvents.None;
        var e = ShieldSoundEvents.None;
        if (before > 0f && now <= 0f) e |= ShieldSoundEvents.Broken;
        else if (now < before - FullMargin) e |= ShieldSoundEvents.Hit;
        if (now < before - Epsilon)
        {
            if (_regenerating) e |= ShieldSoundEvents.RegenInterrupted;
            _regenerating = false;
        }
        else if (now > before + Epsilon && !_regenerating && before < max - FullMargin)
        {
            _regenerating = true;
            e |= ShieldSoundEvents.RegenStart;
        }
        if (now >= max - FullMargin && before < max - FullMargin)
        {
            e |= ShieldSoundEvents.Restored;
            _regenerating = false;
        }
        return e;
    }
}
