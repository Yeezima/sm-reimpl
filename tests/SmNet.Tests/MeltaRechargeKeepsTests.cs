using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// fix/melta-cooldown-vfx (the owner's playtest of 2026-11-05: the melta's recharge "gets reset back to 0%" by a charge, a throw, a Devastator kick, a Tactical charge or
/// stomp, or grenade damage): the recharge between shots keeps its progress through every interruption (it keeps counting, as reloads do since sim/reload-keeps) and the
/// recharged round arrives on the same tick as with no interruption. Only a weapon switch (stored with the weapon) and death change it. Synthetic weapons shaped like the
/// install's melta (2.5 s a round); no game data.
/// </summary>
public class MeltaRechargeKeepsTests(ITestOutputHelper output)
{
    static readonly WeaponSpec Sidearm = new("test_sidearm", WeaponSlot.Pistol, TriggerKind.SemiAuto, 10f, 0.2f, 10, 40, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static readonly MeleeStep Knife = new(1.0f, 0.25f, 0.4f, 0.6f, 0.5f, 0.7f, 40f, 20f, 3.5f);
    static readonly MeleeStep Heavy = new(1.2f, 0.3f, 0f, 0f, 0.8f, 0.9f, 20f, 5f, 3.5f) { PrimaryReaction = new(0f, ReactionKind.Stun) };
    const int Tactical = 0, Assault = 1;

    static readonly SimConfig Config = new()
    {
        Weapons = [MeltaTests.Melta, Sidearm],
        Thrown = [GrenadeTests.Frag],
        Classes =
        [
            new ClassLoadout("test - tactical melta", 0, 1, [Knife]) { MeleeHeavy = [Heavy], MeleeRush = RushTests.Spec, Evade = 0, EquipmentChoices = [0], Side = Team.SpaceMarines },
            new ClassLoadout("test - assault melta", 0, 1, [Knife]) { MeleeHeavy = [Heavy], JumpPack = 0, Side = Team.SpaceMarines },
        ],
        JumpPacks = [JumpPackTests.Pack],
        Evades = [EvadeTests.Roll],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        HealthMax = 1000f, ShieldMax = 100f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
    };

    /// <summary>The tick (the shot's tick counting 0) the recharged round arrives, with <paramref name="interrupt"/> run from tick 20; asserts the recharge never went back up.</summary>
    int RoundArrives(int cls, Func<int, PlayerState, PlayerInput>? interrupt, out PlayerState s, Func<PlayerState, PlayerState>? at20 = null)
    {
        s = Combat.Respawned(Loadouts.Choose(Config, new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls }, -1, -1, -1, cls == Tactical ? 0 : -1), Config);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0, 0, 0, InputButtons.Fire), Config, out var e);
        Assert.True(e.Fired);
        Assert.Equal(0, s.Clip);
        float last = float.PositiveInfinity;
        for (int k = 1; k <= 200; k++)
        {
            if (k == 20 && at20 is not null) s = at20(s);
            var input = k >= 20 && interrupt is not null ? interrupt(k - 20, s) : new PlayerInput(0, 0, 0, 0, InputButtons.None);
            s = Combat.StepPlayer(s, input, Config, out _);
            if (s.Clip == 1) return k;
            if (k > 1) Assert.True(s.ReloadLeft < last, $"tick {k}: the recharge went from {last} back to {s.ReloadLeft}");
            last = s.ReloadLeft;
        }
        return -1;
    }

    static PlayerInput In(InputButtons b = InputButtons.None, float mx = 0f, float my = 0f) => new(0, mx, my, 0f, b);

    [Fact]
    public void Without_an_interruption_the_round_arrives_2_5_s_after_the_shot()
        => Assert.Equal(76, RoundArrives(Tactical, null, out _));

    [Fact]
    public void Starting_the_charge_keeps_the_recharge()
    {
        bool charged = false;
        Assert.Equal(76, RoundArrives(Tactical, (k, s) => { charged |= s.Rush != RushPhase.None; return k == 0 ? In(InputButtons.Sprint | InputButtons.MeleeHeavy, my: 1f) : In(); }, out _));
        Assert.True(charged);
    }

    [Fact]
    public void Throwing_a_grenade_keeps_the_recharge()
    {
        bool threw = false;
        Assert.Equal(76, RoundArrives(Tactical, (k, s) => { threw |= s.Throwing; return k == 0 ? In(InputButtons.Grenade) : In(); }, out _));
        Assert.True(threw);
    }

    [Theory]
    [InlineData(InputButtons.Melee)]
    [InlineData(InputButtons.MeleeHeavy)]
    public void A_melee_attack_or_the_stun_strike_keeps_the_recharge(InputButtons button)
    {
        bool struck = false;
        Assert.Equal(76, RoundArrives(Tactical, (k, s) => { struck |= s.MeleeStep > 0; return k == 0 ? In(button) : In(); }, out _));
        Assert.True(struck);
    }

    [Fact]
    public void A_roll_and_a_sprint_keep_the_recharge()
    {
        bool rolled = false;
        Assert.Equal(76, RoundArrives(Tactical, (k, s) => { rolled |= s.EvadeTicks > 0; return k == 0 ? In(InputButtons.Jump, my: 1f) : In(); }, out _));
        Assert.True(rolled);
        Assert.Equal(76, RoundArrives(Tactical, (k, _) => In(InputButtons.Sprint, my: 1f), out _));
    }

    [Theory]
    [InlineData(ReactionKind.Interrupt)]
    [InlineData(ReactionKind.Stun)]      // a Tactical charge or stun strike, a Devastator kick, a stomp's blast
    [InlineData(ReactionKind.Stagger)]   // a stomp's blast, a grenade
    public void A_hit_reaction_keeps_the_recharge(ReactionKind kind)
    {
        bool reacted = false;
        Assert.Equal(76, RoundArrives(Tactical, (_, s) => { reacted |= s.Reaction != ReactionKind.None; return In(); }, out _,
            s => Reaction.Start(s, Config, new ReactionEntry(0f, kind, Backward: 2f, Duration: 0.3f), ReactionDirection.Front, new Vector2(0f, -1f))));
        Assert.True(reacted);
    }

    [Fact]
    public void Damage_keeps_the_recharge()
        => Assert.Equal(76, RoundArrives(Tactical, null, out _, s => s with { Shield = 0f, Health = s.Health - 300f, SinceDamage = 0f }));

    [Fact]
    public void The_ground_pound_keeps_the_recharge()
    {
        bool pounded = false;
        Assert.Equal(76, RoundArrives(Assault, (k, s) => { pounded |= JumpPack.Pounding(s.Jump); return k < 12 ? In(InputButtons.Jump) : k == 14 ? In(InputButtons.Melee) : In(); }, out _));
        Assert.True(pounded);
    }

    [Fact]
    public void A_switch_keeps_the_recharge_with_the_holstered_melta_and_death_clears_it()
    {
        // The holstered melta's recharge runs on (sim/combat-pass item 10) and its round arrives on time.
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Tactical }, Config);
        s = Combat.StepPlayer(s, In(InputButtons.Fire), Config, out _);
        for (int k = 1; k < 20; k++) s = Combat.StepPlayer(s, In(), Config, out _);
        s = Combat.StepPlayer(s, In(InputButtons.Switch), Config, out _);
        Assert.Equal(WeaponSlot.Pistol, s.Slot);
        int k2 = 20;
        while (s.OtherClip == 0 && k2 < 200) { s = Combat.StepPlayer(s, In(), Config, out _); k2++; }
        Assert.Equal(76, k2);
        // Death clears it (a respawn refills the clip).
        var dead = Combat.Killed(s with { Slot = WeaponSlot.Ranged, ReloadLeft = 1f }, Config);
        Assert.Equal(0f, dead.ReloadLeft);
        output.WriteLine($"holstered round at tick {k2}");
    }
}
