namespace SmNet.Extras.Musou;

/// <summary>
/// The Musou crowd's run cycle as drawn (owner, 2026-11-09: "storm boys do a lot of walking in place while on the ground ... the same is true for all orks"; every value ours, spec "Musou Mode: Ork movement
/// feel"). Before, every Ork the server called Seek played the run clip at its own rate whatever he did: one standing at his place, waiting in a slot or pressed in the crowd ran on the spot, and one moving
/// slower or faster than the clip's stride slid his feet. Now the clip follows the speed he is drawn at: below <see cref="StandBelow"/> he plays the idle, from <see cref="RunFrom"/> the run (between, as he
/// was), and the run plays at his speed over the clip's own planted-foot speed (<c>ClipGait</c>, measured from the clip on load; <see cref="FallbackClipSpeed"/> when it cannot be), clamped to
/// <see cref="RateMin"/> to <see cref="RateMax"/>. Client only; <c>--musou-old-gait</c> restores the old clip choice.
/// </summary>
public static class MusouGaitRules
{
    /// <summary>The drawn speed's hysteresis (m/s): the run starts from <see cref="RunFrom"/> and ends below <see cref="StandBelow"/>.</summary>
    public const float RunFrom = 0.9f, StandBelow = 0.5f;
    /// <summary>The run's playback rate bounds (a 4.5x Warboss's stride is long: his rate is low).</summary>
    public const float RateMin = 0.15f, RateMax = 1.6f;
    /// <summary>The drawn speed's easing time constant (s): one frame's jump in the drawn place does not flip the clip.</summary>
    public const float SpeedSeconds = 0.08f;
    /// <summary>A step longer than this in one frame (m) is a jump of the drawn place (a snap, a new track), not a speed.</summary>
    public const float JumpMetres = 3f;
    /// <summary>The run clip's planted-foot speed at scale 1 when it cannot be measured (m/s, ours: about the Marine run's 6.3).</summary>
    public const float FallbackClipSpeed = 6f;

    /// <summary>The drawn speed eased toward <paramref name="now"/> over <paramref name="delta"/> s.</summary>
    public static float Ease(float was, float now, float delta) =>
        delta <= 0f ? was : was + (now - was) * (1f - MathF.Exp(-delta / SpeedSeconds));

    /// <summary>Whether he plays the run at <paramref name="speed"/> m/s, having played it the frame before (<paramref name="was"/>).</summary>
    public static bool Running(float speed, bool was) => was ? speed >= StandBelow : speed >= RunFrom;

    /// <summary>The run's playback rate at <paramref name="speed"/> for a clip whose feet go <paramref name="clipSpeed"/> m/s at the look's scale (0 or less: <see cref="FallbackClipSpeed"/>).</summary>
    public static float Rate(float speed, float clipSpeed)
    {
        float clip = clipSpeed > 0.1f ? clipSpeed : FallbackClipSpeed;
        return Math.Clamp(speed / clip, RateMin, RateMax);
    }

    /// <summary>
    /// The charge's sprint as drawn (the owner, 2026-11-09: "sprint to 'charge' the enemy"; client only, no byte on the wire): a charging Ork plays the sprint clip once his drawn speed passes
    /// <see cref="SprintEnter"/> of the way from his paced run to his sprint, and back to the run below <see cref="SprintLeave"/> of it.
    /// </summary>
    public const float SprintEnter = 0.5f, SprintLeave = 0.3f;

    /// <summary>Whether he plays the sprint at <paramref name="speed"/> m/s between his <paramref name="run"/> and <paramref name="sprint"/> speeds, having played it the frame before (<paramref name="was"/>).</summary>
    public static bool Sprinting(float speed, float run, float sprint, bool was) =>
        sprint > run + 0.1f && speed >= run + (was ? SprintLeave : SprintEnter) * (sprint - run);

    /// <summary>The foot slide this leaves (m/s): the speed the feet are drawn at, against the body's.</summary>
    public static float Slide(float speed, float clipSpeed, bool running) => running ? MathF.Abs(speed - Rate(speed, clipSpeed) * (clipSpeed > 0.1f ? clipSpeed : FallbackClipSpeed)) : speed;
}

/// <summary>One drawn crowd member's gait between frames (<see cref="MusouGaitRules"/>): his last drawn place, his eased speed and whether he runs.</summary>
public struct MusouGaitTrack
{
    public System.Numerics.Vector2 Last;
    public float Speed;
    public bool Running, Seen, Sprinting;

    /// <summary>A frame: the member drawn at <paramref name="at"/> (flat), <paramref name="delta"/> s after the last; whether he runs now.</summary>
    public bool Step(System.Numerics.Vector2 at, float delta)
    {
        if (!Seen || delta <= 0f) { Last = at; Seen = true; return Running; }
        float step = System.Numerics.Vector2.Distance(at, Last);
        Last = at;
        if (step > MusouGaitRules.JumpMetres) return Running;
        Speed = MusouGaitRules.Ease(Speed, step / delta, delta);
        Running = MusouGaitRules.Running(Speed, Running);
        return Running;
    }
}
