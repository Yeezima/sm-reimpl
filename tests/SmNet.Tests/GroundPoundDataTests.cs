using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M37: the ground pound's damage equals the data. A synthetic jump-pack file with the install's <c>actn_gp_hit</c> values
/// (default entry: 25 explosive + 50 armour shield, multipliers 1, r 7, fall-off 0 → 1, 4.5 → 1, 7 → 0.05, line of sight,
/// <c>ignore_self</c>; the perk entry's 75 not taken) is read by <see cref="JumpPackTuning"/> and the pound is simulated: each
/// victim loses 50 × i from the shield first, then 25 × i through the shield-then-health rule, i the linear fall-off at its
/// distance; nothing beyond 7 m; the pounder nothing. No original bytes.
/// </summary>
public class GroundPoundDataTests(ITestOutputHelper output)
{
    const int Assault = 0, Target = 1;

    static readonly (float, float)[] Heights = [(0.1f, 4f), (0.1f, 4f), (0.25f, 13f), (0.4f, 18f), (0.41f, 0f), (0f, 0f)];
    static readonly (float, float)[] Costs = [(0.1f, 35f), (0.25f, 35f), (0.4f, 55f)];

    static JumpPackSpec PackFromData()
    {
        var notes = new List<string>();
        var doc = BafDocument.Read(SyntheticBafWriter.JumpPack(Heights, Costs, 100f, 10f, 20f, 12f, 0.5f, 30f, 16f, 0.02f, 0.01f,
            poundDamage: (25f, 50f), landings: [(15f, 4f, 15f), (0f, 4f, 5f)]));
        return JumpPackTuning.Read("sm_mp_abil_jumppack", doc, notes)!;
    }

    static SimConfig Config(float shieldMax) => new()
    {
        ShieldMax = shieldMax,
        Weapons = [new WeaponSpec("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.2f, 10, 50, 1.3f, 100f, 100f, 100f, 1f, 2.5f, 1f, 0.5f, 0.5f)],
        Classes = [new ClassLoadout("assault", -1, 0, SimConfig.PlaceholderMeleeSteps) { JumpPack = 0 }, new ClassLoadout("target", -1, 0, SimConfig.PlaceholderMeleeSteps)],
        JumpPacks = [PackFromData()],
    };

    /// <summary>Player 1 jumps (tap) and pounds straight down at the hang; returns the impact and the blast's hits.</summary>
    static (JumpEvent Impact, List<HitEvent> Hits) Pound(World world)
    {
        var hits = new List<HitEvent>();
        bool pounded = false;
        for (int k = 0; k < 300; k++)
        {
            var p = world.Players[1];
            var b = k == 0 ? InputButtons.Jump : !pounded && p.Jump == JumpPhase.Hang ? InputButtons.Melee : InputButtons.None;
            if (b == InputButtons.Melee) pounded = true;
            world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, b, -1.4f) : new PlayerInput(0, 0, 0, 0f, InputButtons.None));
            hits.AddRange(world.LastHits.Where(h => h.Landing));
            if (world.LastJumps.FirstOrDefault(j => j.Kind == JumpEventKind.Impact) is { Player: 1 } impact) return (impact, hits);
        }
        throw new Xunit.Sdk.XunitException("no impact");
    }

    [Fact]
    public void The_pound_read_from_the_data_deals_50_shield_then_25_by_the_linear_fall_off_and_nothing_past_7_m()
    {
        var pack = PackFromData();
        var blast = pack.Pound!;
        Assert.Equal((7f, 25f, 50f, 1f, 1f, 1f, 1f), (blast.Radius, blast.Damage.Damage, blast.Damage.ShieldDamage,
            blast.Damage.BodyMultiplier, blast.Damage.HeadMultiplier, blast.Damage.ShieldBodyMultiplier, blast.Damage.ShieldHeadMultiplier));
        Assert.Equal([new(0f, 1f), new(4.5f, 1f), new DistancePoint(7f, 0.05f)], blast.FallOff);
        Assert.Equal((true, true, "target_all"), (blast.LineOfSight, blast.IgnoreSelf, blast.TargetFilter));
        // The fall-off between the data's points: full to 4.5 m, 0.525 at 5.75 m, 0.05 at 7 m, nothing beyond.
        Assert.Equal(1f, Combat.SplashIntensity(blast.FallOff, 4.5f));
        Assert.Equal(0.525f, Combat.SplashIntensity(blast.FallOff, 5.75f), 4);
        Assert.Equal(0.05f, Combat.SplashIntensity(blast.FallOff, 7f), 4);
        Assert.Equal(0f, Combat.SplashIntensity(blast.FallOff, 7.01f));

        var config = Config(100f);
        var spots = new[] { Vector3.Zero, new Vector3(3f, 0, 0), new Vector3(0, 6f, 0), new Vector3(-6.9f, 0, 0), new Vector3(0, -7.2f, 0) };
        var world = new World(config, null, spots.Select(p => new SpawnPoint(p, 0f)).ToArray());
        world.Spawn(1, Assault);
        for (int i = 1; i < spots.Length; i++) world.Spawn(i + 1, Target);
        var (impact, hits) = Pound(world);
        output.WriteLine($"impact at {impact.Position}, drop {impact.Drop:F2}: {impact.Victims} hit, {impact.ShieldDamage:F2} shield + {impact.HealthDamage:F2} health");
        Assert.DoesNotContain(hits, h => h.Target == 1);   // ignore_self
        Assert.DoesNotContain(hits, h => h.Target == 5);   // 7.2 m: past the radius
        Assert.Equal([2, 3, 4], hits.Select(h => h.Target).Order());
        foreach (var h in hits)
        {
            float i = Combat.SplashIntensity(blast.FallOff, h.Distance);
            output.WriteLine($"  player {h.Target} at {h.Distance:F3} m: intensity {i:F4}: shield {h.ShieldDamage:F3}, health {h.HealthDamage:F3}");
            Assert.Equal(75f * i, h.ShieldDamage, 3);   // 100 of shield absorbs 50 × i and then 25 × i
            Assert.Equal(0f, h.HealthDamage);
            Assert.Equal(config.ShieldMax - 75f * i, world.Players[h.Target].Shield, 3);
        }
        Assert.Equal(75f, hits.Single(h => h.Target == 2).ShieldDamage);   // 3 m: full intensity
        // The impact drifts a little from the take-off spot (here under 1 m), so the others are checked by their measured distance above;
        // both lie on the linear part of the fall-off (between 4.5 and 7 m).
        Assert.All(hits.Where(h => h.Target is 3 or 4), h => Assert.InRange(h.Distance, 4.5f, 7f));
        Assert.All(hits.Where(h => h.Target is 3 or 4), h => Assert.InRange(h.ShieldDamage, 75f * 0.05f, 75f - 1f));
        Assert.Equal((config.HealthMax, config.ShieldMax), (world.Players[1].Health, world.Players[1].Shield));
    }

    [Fact]
    public void A_shield_too_weak_for_the_pound_passes_the_rest_of_the_damage_to_health()
    {
        // Shield 60: the shield part 50 leaves 10, which absorbs 10 of the 25; the other 15 reaches health (multiplier 1).
        var config = Config(60f);
        var world = new World(config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(2f, 0, 0), 0f)]);
        world.Spawn(1, Assault);
        world.Spawn(2, Target);
        var (_, hits) = Pound(world);
        var h = Assert.Single(hits);
        Assert.Equal((60f, 15f), (h.ShieldDamage, h.HealthDamage));
        Assert.Equal((0f, config.HealthMax - 15f), (world.Players[2].Shield, world.Players[2].Health));
    }

    [Fact]
    public void A_pound_on_a_full_armour_target_lowers_the_armour_first_and_leaves_the_health_alone_even_a_few_ticks_later()
    {
        // sim/pound-armour-first (owner, 2026-10-31): 100 armour at 2 m (intensity 1): 75 comes off the armour, health stays 100 (the stun the pound adds does not undo it).
        var config = Config(100f) with { JumpPacks = [PackFromData() with { Pound = PackFromData().Pound! with { Reactions = [new ReactionEntry(0f, ReactionKind.Stun)] } }] };
        var world = new World(config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(2f, 0, 0), 0f)]);
        world.Spawn(1, Assault);
        world.Spawn(2, Target);
        var (_, hits) = Pound(world);
        var h = Assert.Single(hits);
        Assert.Equal((75f, 0f), (h.ShieldDamage, h.HealthDamage));
        for (int k = 0; k < 10; k++)
        {
            Assert.Equal((25f, config.HealthMax), (world.Players[2].Shield, world.Players[2].Health));
            world.Step(_ => new PlayerInput(0, 0, 0, 0f, InputButtons.None));
        }
    }

    [Fact]
    public void A_pound_on_a_target_with_no_armour_takes_all_of_it_as_health()
    {
        // No armour: the shield part (50) has nothing to come off, the 25 goes to health (multiplier 1).
        var config = Config(0f);
        var world = new World(config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(2f, 0, 0), 0f)]);
        world.Spawn(1, Assault);
        world.Spawn(2, Target);
        var (_, hits) = Pound(world);
        var h = Assert.Single(hits);
        Assert.Equal((0f, 25f), (h.ShieldDamage, h.HealthDamage));
        Assert.Equal((0f, config.HealthMax - 25f), (world.Players[2].Shield, world.Players[2].Health));
    }

    [Fact]
    public void The_pounded_clients_own_state_shows_the_armour_drop_and_not_a_health_drop()
    {
        // sim/pound-armour-first (owner, 2026-10-31): through GameServer and two GameClients, the victim's predicted (own HUD) armour and health after a pound.
        var config = Config(100f) with { JumpPacks = [PackFromData() with { Pound = PackFromData().Pound! with { Reactions = [new ReactionEntry(0f, ReactionKind.Stun)] } }] };
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(20), TimeSpan.Zero, 0), 3, 2, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        h.Run(TimeSpan.FromSeconds(1.5));
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        int pounder = h.Server.World.Players.Keys.Min(), victim = h.Clients.FindIndex(c => c.PlayerId != pounder);
        int pi = 1 - victim;
        output.WriteLine($"pounder P{pounder} victim P{h.Clients[victim].PlayerId} distance {Vector3.Distance(h.Server.World.Players[h.Clients[pi].PlayerId].Position, h.Server.World.Players[h.Clients[victim].PlayerId].Position):F2}");
        bool pounded = false, impact = false;
        var samples = new List<string>();
        float minShield = 100f, maxHealthLoss = 0f;
        for (int ms = 0; ms < 12000; ms++)
        {
            h.Step((i, t) =>
            {
                if (i != pi) return new PlayerInput(t, 0, 0, 0f, InputButtons.None);
                var p = h.Server.World.Players[h.Clients[pi].PlayerId];
                var b = ms < 500 ? InputButtons.None : ms < 960 ? InputButtons.Jump : !pounded && p.Position.Z > 9f && p.Jump is JumpPhase.Hang or JumpPhase.Fall ? InputButtons.Melee : InputButtons.None;
                if (b == InputButtons.Melee) pounded = true;
                return new PlayerInput(t, 0, 0, -2.68f, b, -1.4f);   // faces the victim, so the dive leans toward it
            });
            if (!impact && h.Server.World.LastJumps.Any(j => j.Kind == JumpEventKind.Impact)) { impact = true; var ij = h.Server.World.LastJumps.First(j => j.Kind == JumpEventKind.Impact); output.WriteLine($"impact at ms {ms}: player {ij.Player} at {ij.Position}, {ij.Victims} hit; victim at {h.Server.World.Players[h.Clients[victim].PlayerId].Position}"); }
            if (impact)
            {
                var v = h.Clients[victim].Predicted; var truth = h.Server.World.Players[h.Clients[victim].PlayerId];
                minShield = Math.Min(minShield, v.Shield); maxHealthLoss = Math.Max(maxHealthLoss, config.HealthMax - v.Health);
                if (ms % 100 == 0) samples.Add($"ms {ms}: client shield {v.Shield:F1} health {v.Health:F1} | server shield {truth.Shield:F1} health {truth.Health:F1}");
                if (samples.Count > 3) break;
            }
        }
        samples.ForEach(output.WriteLine);
        Assert.True(impact);
        Assert.True(minShield <= 25.5f, $"the victim's own armour only fell to {minShield}");
        Assert.Equal(0f, maxHealthLoss);
    }
}
