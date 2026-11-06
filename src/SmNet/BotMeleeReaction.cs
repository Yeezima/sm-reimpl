namespace SmNet;

/// <summary>
/// The test bots' melee reaction time (night batch 2026-11-06, the owner: "add a 'reaction time' in melee that means they wait a moment the way a human would to react and
/// perform a melee action ... always a slight delay"; **ours, an estimate**, a test aid, not the original AI). A bot that gets an enemy into its melee (or charge) reach waits
/// a seeded uniform <see cref="ReactionMin"/> to <see cref="ReactionMax"/> s before its first melee action (200-350 ms: a little under the ranged 250-450 ms, since the enemy
/// is already close and in view; a human's simple reaction is about 200-250 ms). A new threat (an enemy near it starting a melee attack) re-arms the wait, so the bot answers
/// an incoming attack after a moment, never on the tick it starts. An enemy in reach again after <see cref="ForgetTime"/> s outside it counts as new. Pure and seeded, one per bot.
/// </summary>
public sealed class BotMeleeReaction
{
    public const float ReactionMin = 0.20f, ReactionMax = 0.35f;
    /// <summary>Seconds without an enemy in reach after which the next one counts as new.</summary>
    public const float ForgetTime = 0.4f;

    readonly Random _random;
    int _target = int.MinValue;
    float _outside = float.MaxValue;
    float _wait;

    public BotMeleeReaction(int seed) { _random = new Random(seed); }

    float Draw() => ReactionMin + (ReactionMax - ReactionMin) * (float)_random.NextDouble();

    /// <summary>
    /// One input tick of <paramref name="dt"/> s. <paramref name="targetId"/>: the enemy in melee reach, or null when none. <paramref name="threatBegan"/>: an enemy near the bot
    /// started a melee attack this tick. Returns whether the bot may start a melee action now.
    /// </summary>
    public bool Step(float dt, int? targetId, bool threatBegan = false)
    {
        if (targetId is not { } id)
        {
            _outside = _outside >= float.MaxValue / 2 ? _outside : _outside + dt;
            return false;
        }
        if (id != _target || _outside >= ForgetTime) { _target = id; _wait = Draw(); }
        _outside = 0f;
        if (threatBegan) _wait = Math.Max(_wait, Draw());
        _wait -= dt;
        return _wait <= 0f;
    }
}
