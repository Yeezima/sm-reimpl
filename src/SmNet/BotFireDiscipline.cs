namespace SmNet;

/// <summary>
/// The test bots' trigger discipline (owner's request 2026-10-28, "make them less efficient at dealing ranged damage"; **ours, a test aid**, not the
/// original AI): a bot that acquires a new target waits <see cref="ReactionMin"/> to <see cref="ReactionMax"/> s before its first shot, and then fires in bursts of
/// <see cref="BurstTime"/> s with <see cref="PauseTime"/> s pauses (each varied by ±<see cref="Jitter"/> from a seeded generator, so bots do not
/// pulse in step). A target counts as new when its id changes or when no target was aimed at for <see cref="ForgetTime"/> s (so a target re-entering view or range starts a new reaction). While the
/// discipline holds, the bot's Fire button is released (a charge already being held is left alone by the caller). Perfect aim
/// (<c>--bot-accuracy 1</c> and the probe flags) switches it off.
/// </summary>
public sealed class BotFireDiscipline
{
    /// <summary>
    /// Seconds from acquiring a target to the first shot: a seeded uniform draw per acquisition between <see cref="ReactionMin"/> and <see cref="ReactionMax"/> (night batch 2026-11-06,
    /// the owner: "they should never be instant when they acquire a target. always a slight delay"; **estimate**: a human's simple visual reaction time is about 200-250 ms and
    /// picking one target out of a scene and lining up a shot takes a little longer, so 250-450 ms; was a flat 0.6 s with +-30 %, 0.42-0.78 s).
    /// </summary>
    public const float ReactionMin = 0.25f, ReactionMax = 0.45f;
    /// <summary>Seconds of fire per burst (ours).</summary>
    public const float BurstTime = 0.8f;
    /// <summary>Seconds of pause between bursts (ours).</summary>
    public const float PauseTime = 0.9f;
    /// <summary>Each wait and burst is scaled by a factor in [1 - Jitter, 1 + Jitter].</summary>
    public const float Jitter = 0.3f;
    /// <summary>Seconds without an aimed target after which the next one counts as new.</summary>
    public const float ForgetTime = 0.4f;

    readonly Random _random;
    int _target = int.MinValue;
    float _sinceAimed = float.MaxValue;
    float _phaseLeft;
    bool _firing;

    public BotFireDiscipline(int seed) { _random = new Random(seed); }

    /// <summary>Whether the discipline currently lets the trigger be held.</summary>
    public bool Firing => _firing;

    /// <summary>
    /// One input tick of <paramref name="dt"/> s. <paramref name="targetId"/>: the enemy aimed at this tick, or null when the bot is not aiming a
    /// ranged shot at anyone. Returns whether Fire may be held this tick.
    /// </summary>
    public bool Step(float dt, int? targetId)
    {
        if (targetId is not { } id)
        {
            _sinceAimed = _sinceAimed >= float.MaxValue / 2 ? _sinceAimed : _sinceAimed + dt;
            return false;
        }
        if (id != _target || _sinceAimed >= ForgetTime)
        {
            _target = id;
            _firing = false;
            _phaseLeft = ReactionMin + (ReactionMax - ReactionMin) * (float)_random.NextDouble();
        }
        _sinceAimed = 0f;
        _phaseLeft -= dt;
        if (_phaseLeft <= 0f)
        {
            _firing = !_firing;
            _phaseLeft += Vary(_firing ? BurstTime : PauseTime);
        }
        return _firing;
    }

    float Vary(float seconds) => seconds * (1f - Jitter + 2f * Jitter * (float)_random.NextDouble());
}
