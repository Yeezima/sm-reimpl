using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The owner, 2026-11-09: "not all of the scrap mek's contraptions can currently be destroyed. ensure they have hitboxes and have a low health value to be destroyed at zero." Each kind is struck by a
/// player's shots, one melee blow, a frag's splash and a projectile until zero, and is then gone from the sim with its effect stopped.
/// </summary>
public class MusouContraptionDestructibleTests
{
    const int Human = 1;
    const int Rate = 30;
    static readonly Vector3 Spot = new(40f, -70f, 0f);

    static (World W, MusouMeks Meks) Battle(SimConfig? config = null)
    {
        var w = TfwRig.War(config: config);
        w.TfwMeksOn = true;
        w.Tfw!.Guardsmen = false;
        w.TfwSquigDropsOn = false;
        w.Tfw.PuppetBrain = (_, p) => new PlayerInput(w.Tick, 0f, 0f, p.Yaw, InputButtons.None);   // the AI Marines shoot nothing
        var battle = TfwRig.Deploy(w);
        battle.WeirdboyzOn = false;
        TfwRig.Steps(w, 3);
        var meks = Assert.IsType<MusouMeks>(w.TfwMeks);
        meks.Builds = false;
        w.Musou!.Frozen = true;
        return (w, meks);
    }

    static List<MusouEvent> Cues(World w, byte cue) => w.Musou!.Events.Where(e => e.Kind == MusouEventKind.Telegraph && e.Value == cue).ToList();

    /// <summary>The effect of a kind is gone with it: no dome, no drop, no rocket, no trap going off.</summary>
    static void AssertInactive(World w, MusouMeks meks, MusouContraptionKind kind, byte id)
    {
        Assert.False(meks.TryGet(id, out _), $"the {kind} is gone from the sim");
        Assert.DoesNotContain(All(meks), c => c.Id == id);
        if (kind == MusouContraptionKind.ShieldGenerator) Assert.False(meks.AnyDome, "its dome is down");
        var fire = new List<MusouEvent>();
        for (int k = 0; k < 5 * Rate; k++)
        {
            w.Step(pid => new PlayerInput(w.Tick, 0f, 0f, w.Players[pid].Yaw, InputButtons.None));
            fire.AddRange(Cues(w, MusouMekRules.TurretFireCue).Where(e => e.Player == id));
            fire.AddRange(Cues(w, MusouMekRules.DropCue).Where(e => e.Player == id));
            fire.AddRange(Cues(w, MusouMekRules.TrapCue).Where(e => e.Player == id));
        }
        Assert.Empty(fire);
    }

    static List<MusouContraption> All(MusouMeks meks) { var list = new List<MusouContraption>(); meks.CopyTo(list); return list; }

    public static TheoryData<MusouContraptionKind> Kinds => new() { MusouContraptionKind.ShieldGenerator, MusouContraptionKind.Beacon, MusouContraptionKind.Turret, MusouContraptionKind.Trap };

    [Fact]
    public void Every_kind_has_the_low_health()
    {
        foreach (var kind in Enum.GetValues<MusouContraptionKind>())
        {
            Assert.Equal(MusouMekRules.ContraptionHealth, MusouMekRules.Health(kind));
            Assert.True(MusouMekRules.Body(kind).Radius > 0f && MusouMekRules.Body(kind).Height > 0f);
        }
        Assert.InRange(MusouMekRules.ContraptionHealth, 25.4f * 2f, 25.4f * 3f);   // three standard bolter rounds
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_players_rounds_destroy_it_at_zero(MusouContraptionKind kind)
    {
        var (w, meks) = Battle();
        var built = meks.PlaceBuilt(kind == MusouContraptionKind.ShieldGenerator ? 3 : 0, kind, Spot)!.Value;
        // From outside the shield's dome too (16 m): the dome lets fire through to its own generator.
        float range = kind == MusouContraptionKind.ShieldGenerator ? 16f : 12f;
        Assert.True(w.TfwRespawnAt(Human, Spot + new Vector3(0f, -range, 0f), 0f));
        w.Step(pid => new PlayerInput(w.Tick, 0f, 0f, 0f, InputButtons.None));
        float pitch = -MathF.Atan2(w.Config.EyeHeight - MusouMekRules.Body(kind).Height * 0.5f, range);
        bool destroyed = false;
        for (int k = 0; k < 10 * Rate && !destroyed; k++)
        {
            var trigger = k % 2 == 0 ? InputButtons.Fire : InputButtons.None;   // the class's semi-automatic: a press a shot
            w.Step(pid => new PlayerInput(w.Tick, 0f, 0f, 0f, pid == Human ? trigger : InputButtons.None, Pitch: pitch));
            destroyed = Cues(w, MusouMekRules.DestroyedCue).Any(e => e.Player == built.Id);
            if (!destroyed && kind == MusouContraptionKind.Trap) destroyed = Cues(w, MusouMekRules.TrapBlastCue).Any(e => e.Player == built.Id);
        }
        Assert.True(destroyed, $"the {kind} was shot to zero");
        AssertInactive(w, meks, kind, built.Id);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void One_melee_blow_destroys_it(MusouContraptionKind kind)
    {
        var (w, meks) = Battle();
        var built = meks.PlaceBuilt(kind == MusouContraptionKind.ShieldGenerator ? 3 : 0, kind, Spot)!.Value;
        Assert.True(w.TfwRespawnAt(Human, Spot + new Vector3(0f, -(MusouMekRules.Body(kind).Radius + 0.8f), 0f), 0f));
        w.Step(pid => new PlayerInput(w.Tick, 0f, 0f, 0f, InputButtons.None));
        bool gone = false;
        for (int k = 0; k < 2 * Rate && !gone; k++)
        {
            var buttons = k < 2 ? InputButtons.Melee : InputButtons.None;
            w.Step(pid => new PlayerInput(w.Tick, 0f, 0f, 0f, pid == Human ? buttons : InputButtons.None));
            gone = !meks.TryGet(built.Id, out _);
        }
        Assert.True(gone, $"one blow destroyed the {kind}");
        AssertInactive(w, meks, kind, built.Id);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_frags_splash_destroys_it(MusouContraptionKind kind)
    {
        var frag = new ThrownSpec("test_frag", 2, 2, 1f, 10f, GrenadeTests.FragBall);
        var config = TfwRig.Config with { Thrown = [frag], Classes = TfwRig.Config.Classes.Select(c => c with { EquipmentChoices = [0] }).ToArray() };
        var (w, meks) = Battle(config);
        var built = meks.PlaceBuilt(kind == MusouContraptionKind.ShieldGenerator ? 3 : 0, kind, Spot)!.Value;
        // Thrown straight down 2 m from its edge: it goes off beside it (inside the dome for the generator, as a player who walked in would).
        Assert.True(w.TfwRespawnAt(Human, Spot + new Vector3(0f, -(MusouMekRules.Body(kind).Radius + 2f), 0f), 0f));
        w.Step(pid => new PlayerInput(w.Tick, 0f, 0f, 0f, InputButtons.None));
        bool gone = false;
        for (int k = 0; k < 6 * Rate && !gone; k++)
        {
            var buttons = k < 2 ? InputButtons.Grenade : InputButtons.None;
            w.Step(pid => new PlayerInput(w.Tick, 0f, 0f, 0f, pid == Human ? buttons : InputButtons.None, Pitch: -1.4f));
            gone = !meks.TryGet(built.Id, out _);
        }
        Assert.True(gone, $"the frag's blast destroyed the {kind}");
        Assert.False(meks.TryGet(built.Id, out _));
    }

    [Fact]
    public void A_projectile_strikes_a_contraption_and_detonates_on_it()
    {
        var frag = new ThrownSpec("test_frag", 2, 2, 1f, 10f, GrenadeTests.FragBall);
        var config = TfwRig.Config with { Thrown = [frag], Classes = TfwRig.Config.Classes.Select(c => c with { EquipmentChoices = [0] }).ToArray() };
        var (w, meks) = Battle(config);
        var turret = meks.PlaceBuilt(0, MusouContraptionKind.Turret, Spot)!.Value;
        Assert.True(w.TfwRespawnAt(Human, Spot + new Vector3(0f, -5f, 0f), 0f));
        w.Step(pid => new PlayerInput(w.Tick, 0f, 0f, 0f, InputButtons.None));
        float nearest = float.PositiveInfinity;
        for (int k = 0; k < 4 * Rate; k++)
        {
            var buttons = k < 2 ? InputButtons.Grenade : InputButtons.None;
            w.Step(pid => new PlayerInput(w.Tick, 0f, 0f, 0f, pid == Human ? buttons : InputButtons.None, Pitch: -0.2f));   // at the turret's body, below its top
            foreach (var p in w.Projectiles.Where(p => p.Owner == Human)) nearest = MathF.Min(nearest, new Vector2(p.Position.X - Spot.X, p.Position.Y - Spot.Y).Length());
        }
        Assert.True(nearest >= MusouMekRules.Body(MusouContraptionKind.Turret).Radius - 0.2f, $"it never passed into the turret's body (nearest {nearest:F2} m) ");
        Assert.False(meks.TryGet(turret.Id, out _), "its blast against the turret destroyed it");
    }
    /// <summary>The class's pistol slot as a projectile launcher (a straight dart at 30 m/s, 30 direct damage, bursting on contact).</summary>
    static SimConfig Launcher()
    {
        var dart = new ProjectileSpec("ebps/test/dart", 30f, 0f, "raycast", 0f, [new(0f, 0f)], true, true, false, 0, 0f, 0f, 0, null,
            new SplashSpec(1f, new DamageSpec(1f, 0f, 1f, 1f, 0f, 0f, false), [new(0f, 1f)], true, false, "target_all"));
        var launcher = new WeaponSpec("test_launcher", WeaponSlot.Pistol, TriggerKind.SemiAuto, 30f, 0.2f, 10, 20, 0.6f, 150f, 150f, 150f, 1f, 1f, 1f, 0f, 0f)
            { Delivery = DeliveryKind.Projectile, Projectile = dart };
        var weapons = TfwRig.Config.Weapons.ToArray();
        weapons[1] = launcher;
        return TfwRig.Config with { Weapons = weapons };
    }

    [Fact]
    public void A_projectile_fired_from_outside_the_dome_reaches_its_generator()
    {
        var (w, meks) = Battle(Launcher());
        var shield = meks.PlaceBuilt(3, MusouContraptionKind.ShieldGenerator, Spot)!.Value;
        const float range = 16f;   // 6 m outside the 10 m dome
        Assert.True(w.TfwRespawnAt(Human, Spot + new Vector3(0f, -range, 0f), 0f));
        w.Step(pid => new PlayerInput(w.Tick, 0f, 0f, 0f, InputButtons.None));
        float pitch = -MathF.Atan2(w.Config.EyeHeight - MusouMekRules.Body(MusouContraptionKind.ShieldGenerator).Height * 0.5f, range);
        bool destroyed = false;
        for (int k = 0; k < 6 * Rate && !destroyed; k++)
        {
            var trigger = k % 8 == 0 ? InputButtons.Fire : InputButtons.None;
            w.Step(pid => new PlayerInput(w.Tick, 0f, 0f, 0f, pid == Human ? trigger : InputButtons.None, Pitch: pitch));
            destroyed = Cues(w, MusouMekRules.DestroyedCue).Any(e => e.Player == shield.Id);
        }
        Assert.True(destroyed, "the darts passed the dome's shell and destroyed its generator");
        AssertInactive(w, meks, MusouContraptionKind.ShieldGenerator, shield.Id);
    }

    [Fact]
    public void A_projectile_fired_from_inside_a_contraptions_body_does_not_go_off_at_the_muzzle()
    {
        var (w, meks) = Battle(Launcher());
        var beacon = meks.PlaceBuilt(0, MusouContraptionKind.Beacon, Spot)!.Value;
        Assert.Equal(0, meks.Raycast(Spot + new Vector3(0.1f, 0f, 1.5f), Vector3.UnitY, 50f, out _));   // a ray starting inside a body does not strike it
        Assert.True(w.TfwRespawnAt(Human, Spot, 0f));   // standing in the beacon's cylinder
        w.Step(pid => new PlayerInput(w.Tick, 0f, 0f, 0f, InputButtons.None));
        float farthest = 0f;
        for (int k = 0; k < Rate; k++)
        {
            var trigger = k == 0 ? InputButtons.Fire : InputButtons.None;
            w.Step(pid => new PlayerInput(w.Tick, 0f, 0f, 0f, pid == Human ? trigger : InputButtons.None));
            foreach (var p in w.Projectiles.Where(p => p.Owner == Human)) farthest = MathF.Max(farthest, Vector3.Distance(p.Position, Spot));
        }
        Assert.True(farthest > 5f, $"the dart flew on (farthest {farthest:F1} m)");
        Assert.True(meks.TryGet(beacon.Id, out var after) && after.Health == MusouMekRules.BeaconHealth, "the beacon he stands in is not struck");
    }
}
