using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// sim/sword-ff-stab (owner, 2026-11-14), the simulation's side: a heavy press, then a second inside the strike's Y window, starts the finisher strike; a finisher built as the stab
/// (no blast, 100 + 50 shield, stagger) hits only the target in front with that damage and reaction, while the old stomp finisher hit through its blast. Synthetic timings.
/// </summary>
public class SwordFfStabWorldTests
{
    static float T(float ticks) => ticks / 30f;

    static readonly ReactionEntry Stagger = new(0f, ReactionKind.Stagger, Backward: 1f, Duration: 0.2f);
    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);

    /// <summary>The first F: hit at tick 10, a Y window at ticks 15..25, chains to heavy strike 2.</summary>
    static MeleeStep Quick() => new MeleeStep(T(40.5f), T(9.5f), 0f, 0f, T(25.5f), T(29.5f), 0f, 0f, 3.5f)
        { YNext = 2, YComboOpen = T(14.5f), YComboClose = T(24.5f) };

    /// <summary>The finisher as the stab: the same clip and hit time, 100 + 50 shield and a stagger on the primary target, nothing for the others, no blast.</summary>
    static MeleeStep Stab() => new MeleeStep(T(40.5f), T(9.5f), 0f, 0f, T(25.5f), T(29.5f), 100f, 0f, 3.5f)
        { PrimaryReaction = Stagger, ShieldDamage = 50f };

    static SimConfig Config(MeleeStep finisher) => new()
    {
        Classes =
        [
            new ClassLoadout("sword", -1, -1, [new MeleeStep(T(30.5f), T(6.5f), T(12.5f), T(18.5f), T(15.5f), T(21.5f), 65f, 30f, 3.5f)]) { MeleeHeavy = [Quick(), finisher], Side = Team.SpaceMarines },
            new ClassLoadout("target", -1, -1, [new MeleeStep(T(30.5f), T(6.5f), T(12.5f), T(18.5f), T(15.5f), T(21.5f), 10f, 5f, 3.5f)]) { Side = Team.Chaos },
        ],
        MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f,
        HealthMax = 200f, ShieldMax = 300f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
    };

    static (List<HitEvent> Hits, World World) FF(MeleeStep finisher, Vector3 bystander)
    {
        var w = new World(Config(finisher), null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 2, 0), 0f), new SpawnPoint(bystander, 0f)]);
        w.Spawn(1, 0); w.Spawn(2, 1); w.Spawn(3, 1);
        var hits = new List<HitEvent>();
        // F at tick 0 (the first strike does no damage and no reaction), F again at tick 20, inside the Y window.
        for (int k = 0; k < 60; k++)
        {
            int tick = k;
            w.Step(id => new PlayerInput(0, 0f, 0f, id == 1 ? 0f : MathF.PI, id == 1 && (tick == 0 || tick == 20) ? InputButtons.MeleeHeavy : InputButtons.None));
            hits.AddRange(w.LastHits.Where(h => h.Shooter == 1));
        }
        return (hits, w);
    }

    [Fact]
    public void The_second_F_deals_100_on_the_body_after_50_off_the_shield_and_staggers_only_the_target_in_front()
    {
        var (hits, w) = FF(Stab(), new Vector3(0, -2, 0));   // the bystander is behind the attacker
        var hit = Assert.Single(hits, h => h.Melee);
        Assert.Equal((2, 150f, 0f), (hit.Target, hit.ShieldDamage, hit.HealthDamage));   // 50 off the shield alone, then 100 on what is left of it (250)
        Assert.Equal((300f - 150f, 200f), (w.Players[2].Shield, w.Players[2].Health));
        Assert.Equal((300f, 200f), (w.Players[3].Shield, w.Players[3].Health));
    }

    [Fact]
    public void The_stab_has_no_blast_so_a_second_target_beside_the_first_is_not_hurt()
    {
        var (_, w) = FF(Stab(), new Vector3(1, 2.5f, 0));
        Assert.Equal((300f, 200f), (w.Players[3].Shield, w.Players[3].Health));
    }
}
