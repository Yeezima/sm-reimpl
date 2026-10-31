using System.Numerics;
using SmContent;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// sim/stormbolter-ammo (protocol 51 batch; the owner, 2026-11-01: "the storm bolter for tactical is currently not using a bullet every shot - resulting in firing
/// for twice as long on full automatic before requiring a reload"). The storm bolters' wargear (<c>sm_</c> and <c>csm_mp_ranged_storm_bolter</c>) spends a round
/// through <c>ammo_used</c> and one more through a <c>fire_sim</c> <c>delayed_action</c> <c>consume_ammo</c> (the second barrel, 0.05 s later): 2 rounds a 0.1 s
/// shot, so the clip of 100 empties in 50 shots, 5 s on full auto, then a reload is needed. The reader used to count only <c>ammo_used</c> (100 shots, 10 s).
/// Every other firearm spends 1 round a shot. The client predicts with the same <see cref="Combat.StepPlayer"/> and config (its hash includes the rounds per shot).
/// </summary>
public class StormBolterAmmoTests
{
    static WeaponSpec Gun(string name, float cooldown, int clip, int reserve, float reload, int ammoPerShot) =>
        new WeaponSpec(name, WeaponSlot.Ranged, TriggerKind.FullAuto, 22f, cooldown, clip, reserve, reload, 250f, 250f, 250f, 1f, 1f, 1f, 0.625f, 0.625f)
        { AmmoPerShot = ammoPerShot };

    // As the install gives them (spec "Multiplayer weapons"): the storm bolter 0.1 s, 100 / 500, reload 2 s, 2 rounds a shot; the bolter 0.133 s, 30 / 270, 1.3 s.
    static readonly WeaponSpec StormBolter = Gun("sm_mp_ranged_storm_bolter", 0.1f, 100, 500, 2f, 2);
    static readonly WeaponSpec Bolter = Gun("sm_mp_ranged_bolter", 0.133f, 30, 270, 1.3f, 1);

    static SimConfig ConfigFor(WeaponSpec w) => new()
    {
        Weapons = [w],
        Classes = [new ClassLoadout("tactical", 0, -1, []) { Evade = -1 }],
        HealthMax = 1e6f,
    };

    sealed record Emptied(int Shots, int FirstShotTick, int LastShotTick, int ReloadStartTick, int ReloadEndTick, int NextShotTick, List<int> ClipAfterShot, PlayerState End);

    /// <summary>Holds Fire from a fresh spawn until the clip is empty, reloaded and the first shot of the new clip leaves (at most 30 s).</summary>
    static Emptied HoldFire(SimConfig c)
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c);
        int shots = 0, first = -1, last = -1, reloadStart = -1, reloadEnd = -1;
        var clips = new List<int>();
        for (int k = 0; k < 30 * c.TickRate; k++)
        {
            int clipBefore = s.Clip;
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), c, out var e);
            if (e.Fired)
            {
                if (reloadEnd >= 0) return new(shots, first, last, reloadStart, reloadEnd, k, clips, s);
                shots++; clips.Add(s.Clip);
                if (first < 0) first = k;
                last = k;
                Assert.True(s.Clip < clipBefore, "every shot spends rounds");
            }
            else Assert.True(s.Clip == clipBefore || s.ReloadLeft <= 0f, "no round goes without a shot, except a reload's refill");
            if (reloadStart < 0 && s.ReloadLeft > 0f) reloadStart = k;
            if (reloadStart >= 0 && reloadEnd < 0 && s.Clip > 0) reloadEnd = k;
        }
        throw new Xunit.Sdk.XunitException("the weapon never fired again after its reload");
    }

    [Fact]
    public void A_full_storm_bolter_clip_on_full_auto_is_50_shots_in_5_seconds_then_a_reload()
    {
        var c = ConfigFor(StormBolter);
        var r = HoldFire(c);
        float dt = c.TickSeconds;
        Assert.Equal(StormBolter.ClipSize / StormBolter.AmmoPerShot, r.Shots);                 // 50 shots: 2 rounds each
        Assert.Equal(Enumerable.Range(1, 50).Select(i => 100 - 2 * i), r.ClipAfterShot);         // the clip (and the HUD) steps down 2 at every shot
        // Time to empty = clip / rounds per second = 100 / (2 / 0.1 s) = 5 s: the last shot leaves 49 cooldowns after the first.
        float cooldown = OrkBalance.EffectiveCooldown(StormBolter.ShotCooldown, dt);
        Assert.Equal(0.1f, cooldown, 1e-4f);
        Assert.Equal(5f, (r.LastShotTick - r.FirstShotTick) * dt + cooldown, 1e-3f);
        Assert.Equal(StormBolter.ClipSize / (StormBolter.AmmoPerShot / StormBolter.ShotCooldown), (r.LastShotTick - r.FirstShotTick + 3) * dt, 1e-3f);
        // Then the held trigger starts a reload; nothing leaves until it ends, then the new clip fires.
        Assert.True(r.ReloadStartTick > r.LastShotTick);
        Assert.InRange(r.ReloadStartTick - r.LastShotTick, 1, 4);
        Assert.Equal(StormBolter.ReloadTime, (r.ReloadEndTick - r.ReloadStartTick + 1) * dt, 0.04f);
        Assert.True(r.NextShotTick >= r.ReloadEndTick);
        Assert.Equal((98, 400), (r.End.Clip, r.End.Reserve));
    }

    [Fact]
    public void The_bolter_still_spends_one_round_a_shot()
    {
        var c = ConfigFor(Bolter);
        var r = HoldFire(c);
        float dt = c.TickSeconds, cooldown = OrkBalance.EffectiveCooldown(Bolter.ShotCooldown, dt);
        Assert.Equal(30, r.Shots);
        Assert.Equal(Enumerable.Range(1, 30).Select(i => 30 - i), r.ClipAfterShot);
        // 30 shots of 4 ticks (0.133 s rounded up to whole ticks): 4 s; the data's 30 × 0.133 = 3.99 s within a tick per shot.
        float empty = (r.LastShotTick - r.FirstShotTick) * dt + cooldown;
        Assert.Equal(30 * cooldown, empty, 1e-3f);
        Assert.Equal(Bolter.ClipSize * Bolter.ShotCooldown, empty, 30 * dt);
        Assert.Equal((29, 240), (r.End.Clip, r.End.Reserve));
    }

    [Fact]
    public void The_last_round_of_an_odd_clip_still_fires_and_spends_what_is_left()
    {
        // Ours: the original spends the second round 0.05 s after the shot and finds none; a shot needs one round, as before (Combat.StepPlayer).
        var c = ConfigFor(StormBolter with { ClipSize = 5, ReserveSize = 5 });
        var r = HoldFire(c);
        Assert.Equal(3, r.Shots);
        Assert.Equal([3, 1, 0], r.ClipAfterShot);
    }

    [Fact]
    public void The_rounds_per_shot_are_in_the_config_hash()
    {
        // A client that reads the storm bolter differently from its server is refused (the clip it predicts would drift from the server's).
        Assert.NotEqual(ConfigFor(StormBolter).Hash(), ConfigFor(StormBolter with { AmmoPerShot = 1 }).Hash());
    }

    // ---------------------------------------------------------------- the player's install (skipped without it; never in CI)

    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (the pin needs the player's install; never in CI)";
        }
    }

    [InstallFact]
    public void On_the_install_both_storm_bolters_spend_two_rounds_a_shot_and_every_other_firearm_one()
    {
        var c = GameplayTuning.Load(Game.Value!).Config;
        var storm = c.Weapons.Where(w => w.Name.EndsWith("_mp_ranged_storm_bolter", StringComparison.Ordinal)).ToList();
        Assert.Equal(["csm_mp_ranged_storm_bolter", "sm_mp_ranged_storm_bolter"], storm.Select(w => w.Name).Order(StringComparer.Ordinal));
        foreach (var w in storm)
        {
            Assert.Equal((2, 100, 500, 0.1f), (w.AmmoPerShot, w.ClipSize, w.ReserveSize, w.ShotCooldown));
            Assert.All(w.Variants, v => Assert.Equal(2, v.Spec.AmmoPerShot));   // Rapid-Fire Reload, Improved Twin-Link
        }
        // The basic shots; the charged shots keep their ammo_used amounts (the plasma pistol's heavy charge 10: no consume_ammo in any charge stage).
        foreach (var w in c.Weapons.Except(storm)) Assert.True(w.AmmoPerShot == 1, $"{w.Name}: {w.AmmoPerShot} rounds per shot");
        // The install's Tactical storm bolter on full auto: 50 shots in 5 s, then a reload.
        var sm = storm.Single(w => w.Name.StartsWith("sm_", StringComparison.Ordinal)) with { RaiseDelay = 0f, AlwaysReady = true };
        var r = HoldFire(ConfigFor(sm));
        Assert.Equal(50, r.Shots);
        Assert.Equal(5f, (r.LastShotTick - r.FirstShotTick + 3) / 30f, 1e-3f);
    }
}
