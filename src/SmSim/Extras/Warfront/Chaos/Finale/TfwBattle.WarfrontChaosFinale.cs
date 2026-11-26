using SmSim.Extras.Warfront.Fortress;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The Chaos fortress in the battle (Chaos Warfront C5; design "Finale"): the plaza's first human frees the Chosen (the four Boss-kind approach guards under the Chaos roster), not the Lord, who stays at
/// home (his leash held at aggro 0) until the duel; the Lord's health has the level's scaling and the bonus factor but not the per-human one; the bodyguard is down when every Chosen made is gone; and the
/// duel's hold stops every source and the trickle. Every member returns at once unless this is a Chaos fortress, so an Orks fortress runs as before.
/// </summary>
public sealed partial class TfwBattle
{
    /// <summary>Whether this is a Chaos fortress (the finale's battle).</summary>
    public bool ChaosFortress => _fortress is not null && ChaosArmy;

    /// <summary>The duel's hold (set by the World while the relay is on): no source sends, no trickle (C5).</summary>
    public bool ChaosDuelHold { get; internal set; }

    /// <summary>Whether the bodyguard is down: every Chosen the fortress stood is gone (dead or removed); with none made, once a human entered the plaza. False outside a Chaos fortress.</summary>
    public bool ChaosBodyguardDown => ChaosFortress && (FortressBossOrksMade > 0 ? ChaosChosenStanding == 0 : _fortress!.PlazaEntered);

    /// <summary>The Chosen still standing (tests and the log).</summary>
    public int ChaosChosenStanding { get { int n = 0; foreach (ushort id in _bossOrks) if (_enemies.TryGet(id, out var e) && e.Alive) n++; return n; } }

    /// <summary>The fortress's plan (null outside the fortress).</summary>
    public WarfrontFortressPlan? FortressPlan => _fortress?.Plan;

    /// <summary>The Lord's health at the spawn: the per-human factor (applied by the spawn) taken back out, so he is level-scaled only (design: "level-scaled, not per human").</summary>
    void WarfrontChaosLordHealth(ref float health)
    {
        if (!ChaosArmy || _enemies.Warfront is not { PlayerScaleNow: > 0f } rules) return;
        health /= rules.PlayerScaleNow;
    }

    /// <summary>The challenger's entry: <see cref="WarfrontDuelRules.EntryDistance"/> m from the Lord's spot toward the plaza's gate (at most the plaza's radius less 2 m), on the ground, facing the spot. Null outside the fortress.</summary>
    public (System.Numerics.Vector3 At, float Yaw)? ChaosDuelEntry
    {
        get
        {
            if (_fortress?.Plan is not { } plan) return null;
            var way = new System.Numerics.Vector2(plan.PlazaGate.X - plan.BossSpot.X, plan.PlazaGate.Y - plan.BossSpot.Y);
            var dir = way.LengthSquared() > 1e-6f ? System.Numerics.Vector2.Normalize(way) : -System.Numerics.Vector2.UnitY;
            float reach = Math.Clamp(SmSim.Extras.Warfront.Chaos.Finale.WarfrontDuelRules.EntryDistance, 1f, Math.Max(1f, plan.ArenaRadius - 2f));
            var at = plan.BossSpot + new System.Numerics.Vector3(dir * reach, 0f);
            at = Ground(at, at);
            var face = new System.Numerics.Vector2(plan.BossSpot.X - at.X, plan.BossSpot.Y - at.Y);
            return (at, face.LengthSquared() > 1e-6f ? MathF.Atan2(-face.X, face.Y) : 0f);
        }
    }

    /// <summary>The leash group the Lord is held in between two challengers (his own, as at the spawn).</summary>
    internal const int ChaosLordGroup = CommanderGroup;

    /// <summary>The plaza's first human in a Chaos fortress: the Chosen go for good (the Lord does not).</summary>
    void WarfrontChaosPlazaEntered()
    {
        foreach (ushort id in _bossOrks) _enemies.WarfrontFree(id);
    }
}
