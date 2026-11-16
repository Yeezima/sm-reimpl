namespace SmSim.Extras.Musou;

/// <summary>
/// The Dreadnought's sprint limit (D6; data <c>dread_sprint</c>: <see cref="DreadnoughtRules.SprintSeconds"/> 6 s at most, then a gap of <see cref="DreadnoughtRules.SprintGapSeconds"/> 1 s). The same rule runs in the server (it takes the Sprint
/// bit off an input that sprints too long: <c>World.DreadnoughtFilterInput</c>) and in the client before it sends its input, so what the client predicts is what the server steps. Time held counts while Sprint is held and
/// resets when it is let go (**estimate**: the data names no reset); the data's turn cap of 50 degrees a second while sprinting is not applied (the view is the mouse's: spec "Dreadnought: the sprint").
/// </summary>
public sealed class DreadnoughtSprintGate
{
    float _held, _gap;

    /// <summary>Sprint time used in the run now, seconds.</summary>
    public float Held => _held;
    /// <summary>The gap left before sprinting is allowed again, seconds (0: allowed).</summary>
    public float Gap => _gap;

    /// <summary>Applies the limit to <paramref name="input"/> for one tick of <paramref name="dt"/> seconds: Sprint is dropped during the gap and once the run reaches its maximum.</summary>
    public void Apply(ref PlayerInput input, float dt)
    {
        if (_gap > 0f) { _gap = MathF.Max(0f, _gap - dt); _held = 0f; input = input with { Buttons = input.Buttons & ~InputButtons.Sprint }; return; }
        if ((input.Buttons & InputButtons.Sprint) == 0)
        {
            if (_held > 0f) _gap = DreadnoughtRules.SprintGapSeconds;   // any sprint is followed by the data's gap: tapping the key does not reset the limit
            _held = 0f;
            return;
        }
        _held += dt;
        if (_held > DreadnoughtRules.SprintSeconds + 1e-4f)
        {
            _gap = DreadnoughtRules.SprintGapSeconds; _held = 0f;
            input = input with { Buttons = input.Buttons & ~InputButtons.Sprint };
        }
    }

    public void Reset() { _held = 0f; _gap = 0f; }
}
