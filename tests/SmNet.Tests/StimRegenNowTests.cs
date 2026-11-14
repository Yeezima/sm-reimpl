using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// sim/stim-regen-now (owner's recollection, 2026-11-14): "when using a combat stim that natural regeneration or boosted by Larraman's regen rate should kick in immediately even if there is still
/// time left before natural regeneration begins after damage". Using a stim ends the health regeneration delay at the stim's release; Classic PvP and Musou Mode share the step.
/// </summary>
public class StimRegenNowTests
{
    const string Larr = "u_larraman";
    const float Rate = 5f, Boosted = 45f, Dt = 1f / 30f;

    static SimConfig Make(ContentExtras extras)
    {
        var c = StimTests.Config;
        var cls = c.Classes[0] with { PerkChoices = [0, 1, 2] };
        return c with
        {
            Extras = extras, InputBufferWindow = 0.3f, HealthMax = 100f, HealthRegenDelay = 5f, HealthRegenRate = Rate,
            Perks = [.. c.Perks, new PerkSpec("Larraman", Larr, PerkKind.Class, 1)],
            PlayerUpgrades = [new PlayerUpgrade(Larr) { HealthRegenRate = Boosted - Rate }],
            Classes = [cls, c.Classes[1]],
        };
    }

    /// <summary>A damaged player (50 of 100 health, hit just now) with the stim pressed at tick 0; the health after each tick, up to <paramref name="ticks"/>, and the tick the stim released.</summary>
    static (float[] Health, int Release) Run(SimConfig c, bool larraman, bool press, int ticks, float sinceDamage = 0f)
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0, Perk1 = larraman ? 2 : 0 }, c) with { Health = 50f, SinceDamage = sinceDamage };
        var health = new float[ticks];
        int release = -1;
        for (int k = 0; k < ticks; k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, press && k == 0 ? InputButtons.Grenade : InputButtons.None), c, out _);
            health[k] = s.Health;
            if (release < 0 && s.StimLeft > 0f) release = k;
        }
        return (health, release);
    }

    [Theory]
    [InlineData(ContentExtras.None, false, Rate)]
    [InlineData(ContentExtras.None, true, Boosted)]
    [InlineData(ContentExtras.Musou, false, Rate)]
    [InlineData(ContentExtras.Musou, true, Boosted)]
    public void A_stim_used_during_the_delay_heals_on_the_next_tick(ContentExtras extras, bool larraman, float rate)
    {
        var c = Make(extras);
        var (h, release) = Run(c, larraman, press: true, ticks: 30);
        Assert.True(release > 0);
        for (int k = 0; k < release; k++) Assert.Equal(50f, h[k]);   // still in the delay before the release
        Assert.Equal(50f, h[release]);                                // the release tick itself heals nothing
        Assert.Equal(50f + rate * Dt, h[release + 1], 3);             // the next tick: the normal (or Larraman's) rate
        Assert.Equal(50f + 2 * rate * Dt, h[release + 2], 3);
    }

    [Theory]
    [InlineData(ContentExtras.None)]
    [InlineData(ContentExtras.Musou)]
    public void Without_a_stim_the_delay_still_holds(ContentExtras extras)
    {
        var (h, _) = Run(Make(extras), larraman: false, press: false, ticks: 30);
        Assert.All(h, v => Assert.Equal(50f, v));
    }

    [Theory]
    [InlineData(ContentExtras.None)]
    [InlineData(ContentExtras.Musou)]
    public void Damage_taken_after_the_stim_restarts_the_delay_as_normal(ContentExtras extras)
    {
        var c = Make(extras);
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c) with { Health = 50f, SinceDamage = 0f };
        for (int k = 0; k < 30; k++)
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, k == 0 ? InputButtons.Grenade : InputButtons.None), c, out _);
        Assert.True(s.StimLeft > 0f && s.Health > 50f);   // the stim released and regeneration is running
        var (hit, _, _) = Combat.ApplyDamage(s, 10f, 1f, 1f, c);
        Assert.Equal(0f, hit.SinceDamage);
        float after = hit.Health;
        for (int k = 0; k < 100; k++)   // 3.3 s, inside the 5 s delay: no regeneration
        {
            hit = Combat.StepPlayer(hit, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out _);
            Assert.Equal(after, hit.Health);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_stim_with_the_delay_already_over_changes_no_regeneration(bool larraman)
    {
        var c = Make(ContentExtras.None);
        var with = Run(c, larraman, press: true, ticks: 40, sinceDamage: 6f).Health;
        var without = Run(c, larraman, press: false, ticks: 40, sinceDamage: 6f).Health;
        Assert.Equal(without, with);
    }

    [Theory]
    [InlineData(ContentExtras.None)]
    [InlineData(ContentExtras.Musou)]
    public void A_stim_starts_health_at_once_but_armour_keeps_its_own_delay(ContentExtras extras)
    {
        var c = Make(extras) with { ShieldMax = 100f, ShieldRegenDelay = 7f, ShieldRegenRate = 33f };
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c) with { Health = 50f, Shield = 40f, SinceDamage = 0f };
        int armourStart = -1, healthStart = -1;
        for (int k = 0; k < 300; k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, k == 0 ? InputButtons.Grenade : InputButtons.None), c, out _);
            if (healthStart < 0 && s.Health > 50f) healthStart = k;
            if (armourStart < 0 && s.Shield > 40f) armourStart = k;
        }
        Assert.InRange(healthStart, 1, 12);                                  // within a few ticks of the stim's release
        Assert.InRange((armourStart + 1) * Dt, 6.9f, 7.2f);                        // armour: the usual 7 s from the damage, the stim changes nothing
    }

    [Fact]
    public void The_waiver_travels_in_the_snapshot_record_and_clears_on_damage()
    {
        var c = Make(ContentExtras.None);
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c) with { Health = 50f, HealthRegenWaived = true };
        Assert.True(s.HealthRegenWaived);
        var (hit, _, _) = Combat.ApplyDamage(s, 10f, 1f, 1f, c);
        Assert.False(hit.HealthRegenWaived);
    }

    [Fact]
    public void Respawning_clears_the_waiver()
    {
        var c = Make(ContentExtras.None);
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c);
        for (int k = 0; k < 20; k++)   // the stim releases at tick 9
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, k == 0 ? InputButtons.Grenade : InputButtons.None), c, out _);
        Assert.True(s.HealthRegenWaived);
        Assert.False(Combat.Respawned(Combat.Killed(s, c), c).HealthRegenWaived);
    }

    [Fact]
    public void A_stim_never_lengthens_the_delay_and_never_heals_past_the_maximum()
    {
        var c = Make(ContentExtras.None);
        var (h, release) = Run(c, larraman: false, press: true, ticks: 30, sinceDamage: 5f);   // the delay already over: regeneration runs from tick 0, the release changes nothing
        Assert.Equal(50f + Rate * Dt, h[0], 3);
        Assert.True(release > 0 && h[release] > h[release - 1]);
    }
}
