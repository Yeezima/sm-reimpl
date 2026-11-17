using System.Numerics;
using SmContent;
using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Grenade effects pass (owner 2026-11-17; protocol 83): a thrown item launched with its owner's Master-Crafted blueprint is marked on the wire (<see cref="ProjectileFlags.Upgraded"/>) so every
/// client draws and sounds the upgraded blast; the camera effect and smoke rules (<see cref="GrenadeFx"/>) are the data's.
/// </summary>
public class GrenadeEffectsTests
{
    const string Master = "mp_perk_equipment_effectiveness";

    static readonly ProjectileSpec UpgradedBall = GrenadeTests.FragBall with { Blueprint = "ebps\\test\\frag_upgraded" };
    static readonly ThrownSpec MasterFrag = GrenadeTests.Frag with { Variants = [new([Master], GrenadeTests.Frag with { Projectile = UpgradedBall })] };

    static readonly SimConfig Config = GrenadeTests.Config with
    {
        Thrown = [MasterFrag, GrenadeTests.Krak, GrenadeTests.Bomb],
        Perks = [new PerkSpec(Master, Master, PerkKind.Class, 1)],
        Classes = [GrenadeTests.Config.Classes[0] with { PerkChoices = [0] }, .. GrenadeTests.Config.Classes.Skip(1)],
    };

    static (List<ProjectileState> Flying, List<DetonationEvent> Blasts) Throw(int perk)
    {
        var world = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f)]);
        world.Spawn(1, 0, equipmentChoice: 0, perk1Choice: perk);
        var flying = new List<ProjectileState>();
        var blasts = new List<DetonationEvent>();
        for (int k = 0; k < 300 && blasts.Count == 0; k++)
        {
            world.Step(id => new PlayerInput(0, 0, 0, 0f, k == 0 ? InputButtons.Grenade : InputButtons.None, -0.5f));
            flying.AddRange(world.Projectiles);
            blasts.AddRange(world.LastDetonations);
        }
        return (flying, blasts);
    }

    [Fact]
    public void A_grenade_of_a_Master_Crafted_owner_is_marked_upgraded_from_launch_to_blast()
    {
        var (flying, blasts) = Throw(perk: 0);
        Assert.NotEmpty(flying);
        Assert.All(flying, p => Assert.True(p.Upgraded));
        var blast = Assert.Single(blasts);
        Assert.True(blast.Thrown && blast.Upgraded);
        var view = Assert.Single(GameServer.ProjectilesFor([flying[0]], [], Vector3.Zero));
        Assert.True(view.Thrown && view.Upgraded);
        var detonated = Assert.Single(GameServer.ProjectilesFor([], [blast], Vector3.Zero));
        Assert.True(detonated.Flags.HasFlag(ProjectileFlags.Detonated | ProjectileFlags.Thrown | ProjectileFlags.Upgraded));
    }

    [Fact]
    public void A_plain_grenade_is_not_marked()
    {
        var (flying, blasts) = Throw(perk: -1);
        Assert.NotEmpty(flying);
        Assert.All(flying, p => Assert.False(p.Upgraded));
        Assert.False(Assert.Single(blasts).Upgraded);
        Assert.All(GameServer.ProjectilesFor(flying, blasts, Vector3.Zero), v => Assert.False(v.Upgraded));
    }

    [Fact]
    public void The_flag_round_trips_and_only_a_thrown_item_may_carry_it()
    {
        ProjectileView[] views =
        [
            new(1, 0, 0, new Vector3(1, 2, 3), ProjectileFlags.Thrown | ProjectileFlags.Upgraded),
            new(2, 0, 0, new Vector3(4, 5, 6), ProjectileFlags.Thrown | ProjectileFlags.Detonated | ProjectileFlags.Upgraded),
        ];
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(100, 0, 2, [], Projectiles: views))));
        Assert.Equal(2, back.Projectiles!.Length);
        Assert.All(back.Projectiles, v => Assert.True(v.Upgraded));
        Assert.Null(Protocol.Read(Protocol.Write(new Snapshot(101, 0, 2, [], Projectiles: [new ProjectileView(3, 0, 0, Vector3.One, ProjectileFlags.Upgraded)]))));   // an upgraded shot of a weapon: refused
    }

    [Fact]
    public void The_config_names_the_upgraded_blueprint_for_the_client()
    {
        Assert.Equal(UpgradedBall.Blueprint, MasterFrag.UpgradedProjectile!.Blueprint);
        Assert.Null(GrenadeTests.Krak.UpgradedProjectile);
        Assert.Equal(UpgradedBall.Blueprint, Shots.ProjectileOf(Config, 0, 0, thrown: true, upgraded: true)!.Blueprint);
        Assert.Equal(GrenadeTests.FragBall.Blueprint, Shots.ProjectileOf(Config, 0, 0, thrown: true, upgraded: false)!.Blueprint);
        Assert.Equal(GrenadeTests.Krak.Projectile.Blueprint, Shots.ProjectileOf(Config, 1, 0, thrown: true, upgraded: true)!.Blueprint);   // no variant: the base
    }

    [Fact]
    public void The_protocol_is_83_and_the_flag_needs_no_hash_change()
    {
        Assert.Equal(83, Protocol.Version);
        Assert.Equal(32, (int)ProjectileFlags.Upgraded);
    }
}

/// <summary>The camera effect and smoke rule of the frag blasts (<see cref="GrenadeFx"/>).</summary>
public class GrenadeFxTests
{
    [Theory]
    [InlineData(0f, 1f)] [InlineData(5f, 1f)] [InlineData(7.5f, 0.5f)] [InlineData(10f, 0f)] [InlineData(40f, 0f)]
    public void The_shake_is_full_within_5_m_and_gone_from_10_m(float distance, float strength) => Assert.Equal(strength, GrenadeFx.Strength(distance), 4);

    [Fact]
    public void The_shake_lasts_0_07_s_at_12_Hz_with_the_data_amplitudes()
    {
        Assert.Equal((0f, 0f), GrenadeFx.Offset(0f, 1f));
        Assert.Equal((0f, 0f), GrenadeFx.Offset(GrenadeFx.Duration, 1f));
        Assert.Equal((0f, 0f), GrenadeFx.Offset(0.01f, 0f));
        var (r, u) = GrenadeFx.Offset(1f / 48f, 1f);   // a quarter of the 12 Hz period: the crest
        Assert.Equal(GrenadeFx.Horizontal, r, 3);
        Assert.Equal(GrenadeFx.Vertical, u, 3);
        var (r2, u2) = GrenadeFx.Offset(1f / 48f, 0.5f);
        Assert.Equal(r / 2f, r2, 4);
        Assert.Equal(u / 2f, u2, 4);
    }

    [Fact]
    public void Only_the_frag_blasts_keep_their_smoke()
    {
        Assert.True(GrenadeFx.KeepsSmoke("frag_grenade_explosion"));
        Assert.True(GrenadeFx.KeepsSmoke("chaos_frag_grenade_explosion"));
        Assert.False(GrenadeFx.KeepsSmoke("krak_grenade_explosion"));
        Assert.False(GrenadeFx.KeepsSmoke("bolter_hit"));
        Assert.True(GrenadeFx.IsFrag("csm_mp_eq_frag_grenade"));
        Assert.False(GrenadeFx.IsFrag("sm_mp_eq_krak_grenade"));
    }
}
