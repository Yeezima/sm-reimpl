using System.Numerics;

namespace SmSim;

/// <summary>A reaction the world started in the last tick (diagnostics, tests, client effects).</summary>
/// <param name="Source">The attacker or the blast's owner (0: none).</param>
public readonly record struct ReactionEvent(uint Tick, int Source, int Target, ReactionKind Kind, ReactionDirection Direction, int PushDistance);

/// <summary>A player a blind grenade's blast blinded in the last tick (M37; diagnostics, tests, server log).</summary>
/// <param name="Source">The grenade's owner.</param>
/// <param name="Distance">From the blast point to the player's eye, m.</param>
/// <param name="Ticks">The player's blindness after the blast (the longer of the old and the new), ticks.</param>
public readonly record struct BlindEvent(uint Tick, int Source, int Target, int Projectile, float Distance, int Ticks);

/// <summary>
/// Hit reactions in the world (M28; rules **interpretation**, spec/behavior.md "Reactions"): melee hits put their targets in
/// the step's reaction (<see cref="MeleeStep.PrimaryReaction"/>, <see cref="MeleeStep.SecondaryReaction"/>), blasts every
/// target they may harm in the entry for their intensity there (<see cref="SplashSpec.Reactions"/>; the ground pound's
/// <c>reaction_by_intensity</c>), after the hit's damage and only on a target still alive; blindness
/// (<see cref="SplashSpec.Blindness"/>) reaches everyone in the blast, allies included. Whether a reaction may start is
/// <see cref="Reaction.CanReact"/> (hyper armour, flight, a reaction or attack that cannot be interrupted yet).
/// </summary>
public sealed partial class World
{
    readonly List<ReactionEvent> _reactions = new();
    readonly List<BlindEvent> _blindings = new();

    /// <summary>Reactions started during the last <see cref="Step"/>.</summary>
    public IReadOnlyList<ReactionEvent> LastReactions => _reactions;

    /// <summary>Players blind grenades blinded during the last <see cref="Step"/> (M37).</summary>
    public IReadOnlyList<BlindEvent> LastBlindings => _blindings;

    /// <summary>
    /// Puts <paramref name="target"/> in <paramref name="entry"/>'s reaction when it may react: the clip by where
    /// <paramref name="from"/> lies, the knockback along <paramref name="push"/>. Returns whether it started.
    /// </summary>
    bool TryReact(int source, int target, ReactionEntry entry, Vector3 from, Vector2 push)
    {
        if (entry.Kind == ReactionKind.None || !_players.TryGetValue(target, out var victim)) return false;
        if (Reaction.SpecOf(Config, entry.Kind) is not { } spec || !Reaction.CanReact(victim, Config)) return false;
        var direction = Reaction.DirectionOf(victim.Position, victim.Yaw, from, spec.FourWay);
        var after = Reaction.Start(victim, Config, entry, direction, push);
        _players[target] = after;
        _reactions.Add(new ReactionEvent(Tick, source, target, entry.Kind, direction, after.PushDistance));
        return true;
    }

    /// <summary>A blast's reaction and blindness on one target inside it (after its damage; see the class summary).</summary>
    void BlastEffects(int owner, int target, SplashSpec splash, Vector3 point, float intensity)
    {
        if (!_players.TryGetValue(target, out var player) || !player.Alive) return;
        if (splash.Blindness > 0f) _players[target] = Reaction.Blind(player, Config, splash.Blindness);
        // Reactions only where the blast reaches with some intensity (past the last fall-off point it deals nothing; interpretation).
        if (splash.Reactions.Count == 0 || intensity <= 0f || !Harms(owner, target)) return;
        if (Reaction.Pick(splash.Reactions, intensity) is not { } entry) return;
        var away = _players[target].Position - point;
        TryReact(owner, target, entry, point, new Vector2(away.X, away.Y));
    }

    /// <summary>
    /// Whether <paramref name="point"/> lies within <paramref name="halfAngleDegrees"/> of the player's view (<see cref="Combat.AimDirection"/>
    /// of its yaw and pitch) seen from <paramref name="eye"/> (the blind grenade by facing; owner's decision 2026-10-28). A blast at the eye
    /// (closer than 1 cm) is faced; a half-angle of 180 or more (or not finite) faces every direction (SM1's distance-only rule).
    /// </summary>
    public static bool Faces(PlayerState player, Vector3 eye, Vector3 point, float halfAngleDegrees)
    {
        if (!(halfAngleDegrees < 180f)) return true;
        var toBlast = point - eye;
        float length = toBlast.Length();
        if (length < 0.01f) return true;
        var view = Combat.AimDirection(player.Yaw, player.Pitch);
        float cos = Vector3.Dot(view, toBlast / length);
        return cos >= MathF.Cos(MathF.Max(0f, halfAngleDegrees) * (MathF.PI / 180f)) - 1e-6f;
    }

    /// <summary>
    /// A blind grenade's blast (<see cref="SplashSpec.BlindOnly"/>, M37; owner's knowledge of the original, 2026-10-28): every
    /// living player (allies too, the owner unless <see cref="SplashSpec.IgnoreSelf"/>) whose eye (<see cref="Combat.EyePosition"/>)
    /// is within the blast's radius of <paramref name="point"/> and seen from it (weapon collision; always tested) is blinded
    /// for <see cref="SplashSpec.Blindness"/> (the longer of the old and the new). Nothing else: no damage, no hit, no
    /// reaction, no knockback. Neither the evade's hyper armour nor jump-pack flight protects (blindness is not a reaction;
    /// M28 rule kept). <b>Facing (owner's decision 2026-10-28, a deliberate change from the original)</b>: only a player whose view
    /// has the blast within <see cref="SimConfig.BlindViewHalfAngle"/> is blinded (<see cref="Faces"/>); the original blinds by
    /// distance alone (the data has no facing term), which a half-angle of 180 restores.
    /// </summary>
    void BlindBlast(int owner, int projectile, Vector3 point, SplashSpec splash)
    {
        if (splash.Blindness <= 0f) return;
        foreach (var target in _players.Keys.ToList())
        {
            var player = _players[target];
            if (!player.Alive || (splash.IgnoreSelf && target == owner)) continue;
            var eye = Combat.EyePosition(player, Config);
            float distance = Vector3.Distance(eye, point);
            if (distance > splash.Radius || !Faces(player, eye, point, Config.BlindViewHalfAngle) || Blocked(point, eye)) continue;
            var after = Reaction.Blind(player, Config, splash.Blindness);
            _players[target] = after;
            _blindings.Add(new BlindEvent(Tick, owner, target, projectile, distance, after.BlindTicks));
        }
    }
}
