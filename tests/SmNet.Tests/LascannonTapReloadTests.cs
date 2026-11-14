using System.Numerics;
using SmNet;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/lascannon-tap-reload (protocol 77; owner 2026-11-15, experimental, Classic PvP only): a reload of the lascannon loads one round and takes 0.4 s, with no shot while it runs; a full clip or an
/// empty reserve starts nothing; Musou Mode and other weapons keep their full reload. Plus sim/lascannon-steady-zoom's pure part: the scoped look is the plain view whatever the camera's place.
/// </summary>
public class LascannonTapReloadTests(ITestOutputHelper output)
{
    static readonly WeaponSpec Lascannon = new("sm_mp_exotic_las_cannon", WeaponSlot.Ranged, TriggerKind.SemiAuto, 150f, 1f, 4, 20, 2.25f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f) { AlwaysReady = true };

    static readonly SimConfig Config = new()
    {
        Weapons = [Lascannon, StompUninterruptibleTests.BoltPistol],
        Thrown = [GrenadeTests.Frag],
        Classes = [new ClassLoadout("devastator", 0, 1, [StompUninterruptibleTests.Kick]) { Evade = 0, EquipmentChoices = [0], Side = Team.SpaceMarines }],
        Evades = [EvadeTests.Hop],
        HealthMax = 1e6f,
    };
    static readonly SimConfig MusouConfig = Config with { Extras = ContentExtras.Musou };

    static PlayerState Fresh(SimConfig c, int clip, int reserve) =>
        Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Team = Team.SpaceMarines, Class = 0 }, c) with { Clip = clip, Reserve = reserve, ReadyTicks = 300 };

    static (PlayerState S, SelfEvents E) Step(SimConfig c, PlayerState s, InputButtons b)
    {
        var next = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, b), c, out var e);
        return (next, e);
    }

    /// <summary>Ticks of a one-tick Reload press then no buttons until the reload ends; returns the ticks it ran and the end state.</summary>
    static (int Ticks, PlayerState S) Tap(SimConfig c, PlayerState s)
    {
        s = Step(c, s, InputButtons.Reload).S;
        int ticks = 1;
        while (s.ReloadLeft > 0f && ticks < 200) { s = Step(c, s, InputButtons.None).S; ticks++; }
        return (ticks, s);
    }

    [Fact]
    public void A_tap_loads_one_round_in_0_4_s()
    {
        var (ticks, s) = Tap(Config, Fresh(Config, 1, 20));
        output.WriteLine($"{ticks} ticks, clip {s.Clip}, reserve {s.Reserve}");
        Assert.Equal((2, 19), (s.Clip, s.Reserve));
        Assert.InRange(ticks, 12, 13);   // 0.4 s at 30 Hz
    }

    [Fact]
    public void Fire_is_blocked_for_the_reload_and_allowed_after()
    {
        var c = Config;
        var s = Step(c, Fresh(c, 1, 20), InputButtons.Reload).S;
        for (int i = 0; i < 10; i++)
        {
            var (n, e) = Step(c, s, InputButtons.Fire);
            Assert.False(e.Fired, $"fired at tick {i + 1} of the reload");
            s = n with { FireHeld = false };
        }
        s = Tap(c, Fresh(c, 1, 20)).S;
        Assert.True(Step(c, s with { Cooldown = 0f }, InputButtons.Fire).E.Fired);
    }

    [Fact]
    public void A_full_clip_or_an_empty_reserve_starts_no_reload()
    {
        Assert.Equal(0f, Step(Config, Fresh(Config, 4, 20), InputButtons.Reload).S.ReloadLeft);
        Assert.Equal(0f, Step(Config, Fresh(Config, 1, 0), InputButtons.Reload).S.ReloadLeft);
    }

    [Fact]
    public void Held_reload_goes_on_round_by_round_to_a_full_clip()
    {
        var c = Config;
        var s = Fresh(c, 0, 20);
        for (int i = 0; i < 100; i++) s = Step(c, s, InputButtons.Reload).S;
        Assert.Equal((4, 16), (s.Clip, s.Reserve));
    }

    [Fact]
    public void Musou_Mode_and_the_restore_switch_keep_the_full_reload()
    {
        var (_, musou) = Tap(MusouConfig, Fresh(MusouConfig, 1, 20));
        Assert.Equal((4, 17), (musou.Clip, musou.Reserve));
        var old = Config with { LascannonTapReload = false };
        var (ticks, s) = Tap(old, Fresh(old, 1, 20));
        Assert.Equal((4, 17), (s.Clip, s.Reserve));
        Assert.True(ticks > 60);
        Assert.NotEqual(Config.Hash(), old.Hash());
    }

    [Fact]
    public void Other_weapons_keep_their_reload()
    {
        var c = Config;
        var s = Fresh(c, 1, 20) with { Slot = WeaponSlot.Pistol };
        s = Step(c, s, InputButtons.Reload).S;
        Assert.InRange(s.ReloadLeft, 0.7f, 0.76f);
    }

    [Fact]
    public void The_scoped_look_is_the_plain_view_whatever_the_camera_place()
    {
        var view = Vector3.Normalize(new Vector3(0.2f, 1f, 0.1f));
        // Walking moves the camera and the wall casts re-place it: the focus turn differs frame to frame.
        foreach (var turn in new[] { new Vector3(0.3f, 1f, 0f), new Vector3(-0.2f, 1f, 0.05f), new Vector3(0f, 1f, -0.1f) })
            Assert.Equal(view, CameraBoom.ScopedLook(Vector3.Normalize(turn), view, 1f));
        Assert.Equal(Vector3.Normalize(new Vector3(0.3f, 1f, 0f)), CameraBoom.ScopedLook(Vector3.Normalize(new Vector3(0.3f, 1f, 0f)), view, 0f));
        var half = CameraBoom.ScopedLook(Vector3.Normalize(new Vector3(0.3f, 1f, 0f)), view, 0.5f);
        Assert.InRange(half.Length(), 0.999f, 1.001f);
    }

    [Fact]
    public void A_weapon_switch_mid_reload_keeps_the_one_round_reload()
    {
        var c = Config;
        var s = Step(c, Fresh(c, 1, 20), InputButtons.Reload).S;
        s = Step(c, s, InputButtons.Switch).S;
        for (int i = 0; i < 40 && s.SwitchLeft > 0f; i++) s = Step(c, s, InputButtons.None).S;
        for (int i = 0; i < 30; i++) s = Step(c, s, InputButtons.None).S;   // the holstered reload finishes while the pistol is out
        s = Step(c, s, InputButtons.Switch).S;
        for (int i = 0; i < 40; i++) s = Step(c, s, InputButtons.None).S;
        Assert.Equal(WeaponSlot.Ranged, s.Slot);
        Assert.Equal((2, 19), (s.Clip, s.Reserve));   // one round, not a full clip
    }

    [Fact]
    public void A_respawn_clears_the_reload()
    {
        var c = Config;
        var s = Step(c, Fresh(c, 1, 20), InputButtons.Reload).S;
        Assert.True(s.ReloadLeft > 0f);
        var again = Combat.Respawned(s with { Alive = false }, c);
        Assert.Equal(0f, again.ReloadLeft);
    }

    [Fact]
    public void The_empty_clip_auto_reload_is_one_round()
    {
        var c = Config;
        var s = Step(c, Fresh(c, 0, 20), InputButtons.Fire).S;
        Assert.InRange(s.ReloadLeft, 0.3f, 0.4f);
        for (int i = 0; i < 20 && s.ReloadLeft > 0f; i++) s = Step(c, s, InputButtons.None).S;
        Assert.Equal((1, 19), (s.Clip, s.Reserve));
    }

    [Fact]
    public void The_Chaos_lascannon_is_the_same()
    {
        var chaos = Lascannon with { Name = "csm_mp_exotic_las_cannon" };
        var c = Config with { Weapons = [chaos, StompUninterruptibleTests.BoltPistol] };
        var (ticks, s) = Tap(c, Fresh(c, 1, 20));
        Assert.Equal((2, 19), (s.Clip, s.Reserve));
        Assert.InRange(ticks, 12, 13);
    }

    [Fact]
    public void A_remote_lascannon_reload_is_inferred_at_0_4_s_and_loads_one_round()
    {
        var r = new SmContent.ReloadInference();
        var weapons = new[] { Lascannon };
        var shots = Enumerable.Range(0, 4).Select(_ => new SmContent.ReloadInference.Shot(0, 1, 40)).ToList();   // old enough to be past the shot's cooldown
        var got = r.Update(weapons, _ => true, shots, true, false, 0, 10.0, 1f / 60, 1f / 30, w => LascannonTapTuning.Time(Config, w));
        Assert.NotNull(got);
        Assert.Equal(LascannonTapTuning.ReloadTime, got!.Value.Total);
        Assert.Equal(4, r.Used(0));
        r.Update(weapons, _ => true, [], true, false, 0, 10.5, 0.5f, 1f / 30, w => LascannonTapTuning.Time(Config, w));
        Assert.Equal(3, r.Used(0));   // one round loaded, not the clip
    }

    [Fact]
    public void Scoped_walking_makes_no_footfall_camera_impulse()
    {
        Assert.True(CameraBoom.ScopeSteady(1f));
        Assert.True(CameraBoom.ScopeSteady(0.5f));
        Assert.False(CameraBoom.ScopeSteady(0f));
        Assert.False(CameraBoom.ScopeSteady(0.49f));
    }
}
