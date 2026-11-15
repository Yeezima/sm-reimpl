using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Overcharge, the Dreadnought power-up's addition (owner 2026-11-15; Musou only): while the buff runs (30 s: the data's 10 s under Musou's stim scale) the cannon builds no heat, and a melee swing that strikes releases one 3 m
/// shockwave at the hit (knockback and interrupt, no damage of its own). Heat built before cools as usual; the data's effects (reaction immunity) stay. Classic is untouched.
/// </summary>
public class DreadnoughtOverchargeTests
{
    static readonly WeaponSpec Cannon = new(DreadnoughtRules.CannonWargear, WeaponSlot.Ranged, TriggerKind.FullAuto, 30f, 0.1f, 140, 2_000_000, 2.5f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f)
    {
        AlwaysReady = true, Heat = new HeatSpec(1f, 15f, 9f, 1f, 4.5f, 0.41f, 0.41f),
    };
    static readonly ThrownSpec Stims = new("combat_stims", 3, 3, 1f, 0f, ThrownSpec.NoProjectile) { Stim = new StimSpec(5f, 0.2f, 0.6f) };
    static readonly MusouRules Still = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] };
    static readonly SimConfig Config = MusouLoadout.Prepare(TfwRig.Config with { Weapons = [.. TfwRig.Config.Weapons, Cannon], Thrown = [Stims], MusouRules = Still });
    const int Human = TfwRig.Human;

    /// <summary>The same config with the power-up's Overcharge flag off: the data's effects and the stim's numbers alone (the control of the shockwave test).</summary>
    static readonly SimConfig NoOvercharge = Config with { Thrown = [.. Config.Thrown.Select(t => t.Name == DreadnoughtRules.BuffName ? t with { Stim = t.Stim! with { NoHeat = false } } : t)] };

    static World Landed(SimConfig? config = null)
    {
        var Config = config ?? DreadnoughtOverchargeTests.Config;
        int dread = Config.Classes.ToList().FindIndex(k => k.Name == DreadnoughtRules.ClassName);
        var w = TfwRig.War(null, Config);
        Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = dread }));
        TfwRig.Command(w, TfwCommand.ChangeClass);
        TfwRig.Steps(w, 3);
        TfwRig.Deploy(w);
        TfwRig.Seconds(w, DreadnoughtRules.EntranceSeconds + 0.3f);
        w.Musou!.Frozen = true;
        return w;
    }

    static PlayerState Me(World w) => w.Players[Human];
    static void Step(World w, InputButtons b = InputButtons.None) => w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, b));
    static void Steps(World w, int n, InputButtons b = InputButtons.None) { for (int k = 0; k < n; k++) Step(w, b); }
    static ushort Boy(World w, Vector3 at) { ushort id = w.Musou!.Spawn(MusouEnemyType.Boy, at, 0f, 100000f); Assert.NotEqual(0, id); return id; }
    static void Buff(World w) { Step(w, InputButtons.Grenade); Steps(w, 36); Assert.True(Me(w).StimLeft > 0f); }

    [Fact]
    public void The_power_up_lasts_thirty_seconds_one_constant_times_musous_stim_scale()
    {
        Assert.Equal(30f, DreadnoughtRules.BuffLifetime * MusouCombatRules.StimLifetimeScale);
        var w = Landed();
        Buff(w);
        Assert.InRange(Me(w).StimLeft, 28.5f, 30f);
        Steps(w, 30 * 20);   // well past the item's cooldown and the supply refill: the power-up still has its time
        Assert.InRange(Me(w).StimLeft, 8f, 10.5f);
    }

    [Fact]
    public void The_cannon_builds_no_heat_while_the_power_up_runs_and_heat_before_it_cools_and_after_it_builds_again()
    {
        var w = Landed();
        Steps(w, 30, InputButtons.Fire);
        float before = Me(w).Heat;
        Assert.True(before > 0f, "the cannon heats without the power-up");
        Buff(w);   // a use of the power-up cools the gun on its own clock; the heat from before is not raised
        float start = Me(w).Heat, peak = start;
        for (int k = 0; k < 90; k++) { Step(w, InputButtons.Fire); peak = MathF.Max(peak, Me(w).Heat); }
        Assert.True(peak <= start + 1e-4f, $"heat rose from {start} to {peak} under the power-up");
        Assert.False(Me(w).Overheated);
        Assert.True(Me(w).Cooldown >= 0f);
        // it does fire the whole time (0.1 s a shot, no ammunition spent)
        int clip = Me(w).Clip;
        Steps(w, 30, InputButtons.Fire);
        Assert.Equal(clip, Me(w).Clip);
        // after the 30 s: heat again
        Steps(w, 30 * 32);
        Assert.Equal(0f, Me(w).StimLeft);
        Steps(w, 30, InputButtons.Fire);
        Assert.True(Me(w).Heat > 1f, $"heat {Me(w).Heat} after the power-up ended");
    }

    static Dictionary<ushort, Vector3> Moved(World w, bool overcharge, out int waves, out Vector3 hit)
    {
        if (overcharge) Buff(w);
        var at = Me(w).Position;
        hit = at + new Vector3(0f, 1.8f, 0f);
        var ids = new Dictionary<ushort, Vector3> { [Boy(w, hit)] = hit };
        foreach (float dz in new[] { 2.2f, 5.0f })   // above the swing's vertical reach, at 2.2 m and 5 m from the hit
        {
            var p = hit + new Vector3(0f, 0f, dz);
            ids[Boy(w, p)] = p;
        }
        int before = w.DreadnoughtShockwaves;
        var moved = new Dictionary<ushort, Vector3>();
        for (int k = 0; k < 46; k++)
        {
            Step(w, k == 0 ? InputButtons.Melee : InputButtons.None);
            foreach (var (id, p0) in ids)
                if (w.Musou!.TryGet(id, out var e) && e.State is not (MusouEnemyState.Spawning or MusouEnemyState.Seek or MusouEnemyState.WindUp or MusouEnemyState.Strike or MusouEnemyState.Recover)) moved[id] = p0;
        }
        waves = w.DreadnoughtShockwaves - before;
        return moved;
    }

    [Fact]
    public void One_shockwave_per_swing_knocks_enemies_within_three_metres_of_the_hit_and_not_those_at_five()
    {
        var control = Moved(Landed(NoOvercharge), true, out int none, out var hit0);
        Assert.Equal(0, none);   // the same power-up without Overcharge: no shockwave
        var w = Landed();
        var over = Moved(w, true, out int waves, out var hit);
        Assert.Equal(1, waves);   // one swing, one wave, however many it struck
        foreach (var (id, p0) in over)
            if (!control.ContainsKey(id)) Assert.True(Vector3.Distance(p0, hit) <= DreadnoughtMelee.OverchargeRadius + 0.01f, $"Ork at {Vector3.Distance(p0, hit):0.0} m from the hit was moved by the wave");
        Assert.Equal(3f, DreadnoughtMelee.OverchargeRadius);
        Assert.Equal(0f, DreadnoughtMelee.Overcharge.Damage.Damage);   // knockback and interrupt only
        Assert.True(over.Count >= control.Count, $"overcharge moved {over.Count}, control {control.Count}");
        Assert.True(over.Keys.Except(control.Keys).Any(), $"the wave moved nobody beyond the swing (control {control.Count}, overcharge {over.Count})");
    }

    [Fact]
    public void Classic_has_no_overcharge_a_stim_with_no_flag_builds_heat()
    {
        Assert.False(new StimSpec(5f, 0.2f, 0.6f).NoHeat);
        Assert.False(Stims.Stim!.NoHeat);
        var item = Config.Thrown.First(t => t.Name == DreadnoughtRules.BuffName).Stim!;
        Assert.True(item.NoHeat);
        Assert.DoesNotContain(Config.Thrown.Where(t => t.Name != DreadnoughtRules.BuffName), t => t.Stim!.NoHeat);
    }

    [Fact]
    public void The_clients_carry_keeps_the_power_up_past_the_items_cooldown_when_the_prediction_has_it()
    {
        var w = Landed();
        Buff(w);
        Steps(w, 30 * 6);
        var item = Config.Thrown.First(t => t.Name == DreadnoughtRules.BuffName);
        // run on until the item's cooldown is over: the record then has no cooldown left and only the prediction knows the time
        int guard = 0;
        while (Me(w).GrenadeCooldown > 0f && guard++ < 30 * 90) Step(w);
        var record = Me(w);
        Assert.Equal(0f, record.GrenadeCooldown);
        Assert.True(record.StimLeft > 0f, $"the server's power-up ended with its cooldown ({item.Cooldown} s)");
        var carried = SmSim.Stims.Carry(w.Config, record with { StimLeft = 0f }, record);
        Assert.Equal(MathF.Min(record.StimLeft, MathF.Max(0f, DreadnoughtRules.BuffLifetime * MusouCombatRules.StimLifetimeScale - item.Cooldown)), carried.StimLeft, 3);
        Assert.True(carried.StimLeft > 0f, $"carried {carried.StimLeft} of {record.StimLeft} (cooldown {item.Cooldown})");
        // the war's supply refills the item while the power-up still works (the prediction has not): the record with one more is no disagreement
        var refilled = SmSim.Stims.Carry(w.Config, record with { StimLeft = 0f, Grenades = record.Grenades + 1 }, record);
        Assert.Equal(carried.StimLeft, refilled.StimLeft, 3);
    }

    [Fact]
    public void The_wave_moves_an_ork_2_8_m_from_the_hit_and_not_one_3_4_m_away_sideways_too()
    {
        // above the swing's vertical reach (2.2 m up) so only the wave can reach them; 3-D distances 2.8 m and 3.4 m from the hit, one to each side
        float Run(float distance, World w)
        {
            var hit = Me(w).Position + new Vector3(0f, 1.8f, 0f);
            float flat = MathF.Sqrt(distance * distance - 2.2f * 2.2f);
            var struck = Boy(w, hit);
            var near = Boy(w, hit + new Vector3(flat, 0f, 2.2f));
            bool moved = false;
            for (int k = 0; k < 46; k++)
            {
                Step(w, k == 0 ? InputButtons.Melee : InputButtons.None);
                if (w.Musou!.TryGet(near, out var e) && e.State is not (MusouEnemyState.Spawning or MusouEnemyState.Seek or MusouEnemyState.WindUp or MusouEnemyState.Strike or MusouEnemyState.Recover)) moved = true;
            }
            _ = struck;
            return moved ? 1f : 0f;
        }
        var a = Landed(); Buff(a);
        var b = Landed(); Buff(b);
        Assert.Equal(1f, Run(2.8f, a));
        Assert.Equal(0f, Run(3.4f, b));
    }

    [Fact]
    public void A_swing_that_strikes_nobody_releases_no_wave_and_leaves_no_point_for_the_next()
    {
        var w = Landed(); Buff(w);
        var boy = Boy(w, Me(w).Position + new Vector3(0f, 1.8f, 0f));
        int before = w.DreadnoughtShockwaves;
        Steps(w, 1, InputButtons.Melee); Steps(w, 60);
        Assert.Equal(before + 1, w.DreadnoughtShockwaves);   // the struck swing: one
        w.Musou!.Damage(boy, Human, 1_000_000f, Vector3.Zero, out _);
        Steps(w, 30);
        int mid = w.DreadnoughtShockwaves;
        Steps(w, 1, InputButtons.Melee); Steps(w, 60);
        Assert.Equal(mid, w.DreadnoughtShockwaves);   // an empty swing: none, not the last one's point again
    }

    [Fact]
    public void Classic_carry_gives_the_stim_no_time_past_the_cooldown_without_a_prediction_and_after_a_respawn()
    {
        var w = Landed(); Buff(w);
        int guard = 0;
        while (Me(w).GrenadeCooldown > 0f && guard++ < 30 * 90) Step(w);
        var record = Me(w);
        Assert.Equal(0f, SmSim.Stims.Carry(w.Config, record with { StimLeft = 7f }, null).StimLeft);   // no prediction offered (a rested or respawned round): the stim is over
        Assert.Equal(0f, SmSim.Stims.Carry(w.Config, record with { StimLeft = 7f, Alive = false }, record).StimLeft);   // a dead record has none
    }
}
