using System;

namespace SmClient.Customizer.Model;

/// <summary>
/// camera/melee-fov-widen (owner 2026-11-15; a deliberate change from the original in Classic PvP): any melee action widens the own player's configured horizontal field of
/// view by <see cref="Widen"/> degrees (capped at <see cref="FovMath.Maximum"/>: a player at 120 sees no change, one at 115 goes to 120) until the first ranged shot, aiming down
/// sights, a grenade throw, death or respawn; both ways blend over <see cref="BlendSeconds"/> (smoothstep). Never widened while <see cref="Inputs.Excluded"/> (added content's
/// Dreadnought). Camera only: nothing on the wire. Engine-free; the caller feeds it the predicted own player's state once per frame.
/// </summary>
public sealed class MeleeFov
{
    /// <summary>Owner 2026-11-15: horizontal degrees added over the configured field of view.</summary>
    public const float Widen = 10f;
    /// <summary>Owner 2026-11-15 ("Smooth 0.3 s"): seconds for the blend either way.</summary>
    public const float BlendSeconds = 0.3f;

    /// <summary>What the player's predicted state says this frame.</summary>
    /// <param name="Alive">The player is alive (dead players and the frame of a respawn end the widening).</param>
    /// <param name="Melee">Any melee action is in progress: a swing or heavy strike, the charge, a lunge, a ground pound.</param>
    /// <param name="Fire">The fire (LMB) bit is held.</param>
    /// <param name="Aiming">Aiming down sights.</param>
    /// <param name="Throwing">A grenade throw is in progress.</param>
    /// <param name="Excluded">Added content's body that keeps its own field of view (Musou: the Dreadnought).</param>
    public readonly record struct Inputs(bool Alive, bool Melee, bool Fire, bool Aiming, bool Throwing, bool Excluded);

    public enum Change { None, Widened, Shot, Aimed, Threw, Died, Excluded }

    bool _wasMelee, _wasFire, _wasAlive;

    /// <summary>The widening is on (the blend is moving toward, or sits at, the wide view).</summary>
    public bool Wide { get; private set; }
    /// <summary>Linear 0 to 1 progress of the blend; <see cref="Blend"/> is its smoothstep.</summary>
    public float Progress { get; private set; }
    public float Blend => Progress * Progress * (3f - 2f * Progress);

    /// <summary>Advances one frame; what ended or began the widening, if anything.</summary>
    public Change Step(Inputs i, float delta)
    {
        var change = Change.None;
        bool meleeStart = i.Melee && !_wasMelee;
        bool fireEdge = i.Fire && !_wasFire;
        bool respawn = i.Alive && !_wasAlive;
        if (i.Excluded) { if (Wide) change = Change.Excluded; Wide = false; }
        else if (!i.Alive) { if (Wide) change = Change.Died; Wide = false; }
        else if (respawn) Wide = false;
        else if (i.Aiming) { if (Wide) change = Change.Aimed; Wide = false; }
        else if (i.Throwing) { if (Wide) change = Change.Threw; Wide = false; }
        else if (meleeStart) { if (!Wide) change = Change.Widened; Wide = true; }   // a melee start beats a fire press on the same frame (review)
        else if (fireEdge || (i.Fire && !i.Melee)) { if (Wide) change = Change.Shot; Wide = false; }
        _wasMelee = i.Melee; _wasFire = i.Fire; _wasAlive = i.Alive;
        float step = BlendSeconds > 0f && delta > 0f ? delta / BlendSeconds : (delta > 0f ? 1f : 0f);
        Progress = Math.Clamp(Progress + (Wide ? step : -step), 0f, 1f);
        return change;
    }

    /// <summary>The widest horizontal field of view for this configured one: +<see cref="Widen"/> over it, never above <see cref="FovMath.Maximum"/> (a configured 120 stays 120; 0, unset, is the data's look).</summary>
    public static float WideTarget(float configuredHorizontal, float aspect) =>
        MathF.Min(FovMath.Maximum, FovMath.Clamp(configuredHorizontal, aspect) + Widen);

    /// <summary>The horizontal field of view to show now: the configured one (<see cref="FovMath.Clamp"/>) blended toward <see cref="WideTarget"/>.</summary>
    public float Horizontal(float configuredHorizontal, float aspect)
    {
        float basis = FovMath.Clamp(configuredHorizontal, aspect);
        return basis + (WideTarget(configuredHorizontal, aspect) - basis) * Blend;
    }

    /// <summary>The factor for <see cref="FovMath.Apply"/>: <see cref="FovMath.Scale"/> of <see cref="Horizontal"/>; equals the configured scale when not widened.</summary>
    public float Scale(float configuredHorizontal, float aspect) =>
        Progress <= 0f ? FovMath.Scale(configuredHorizontal, aspect) : FovMath.Scale(Horizontal(configuredHorizontal, aspect), aspect);

}
