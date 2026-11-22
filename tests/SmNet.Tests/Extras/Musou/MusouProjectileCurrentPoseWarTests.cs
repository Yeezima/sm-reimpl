using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Codebase fix 6, through a real war (World, MusouProjectiles, TfwPierceProjectile; not MusouSim alone): the player's charged plasma ball tests an Ork's CURRENT pose, so it misses one that has stepped
/// sideways out of its path, while a bolter shot (hit-scan, TfwPierceShot) fired at the same moment still hits the same Ork through the 0.2 s trail. And a scoring guard for the run pin: a human's
/// projectile kill of a stationary Ork in a battle still scores, so the pin's "0" cannot hide broken scoring.
/// </summary>
public class MusouProjectileCurrentPoseWarTests
{
    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    static readonly WeaponSpec Gun = new("sm_mp_ranged_plasma_gun", WeaponSlot.Ranged, TriggerKind.FullAuto, 20f, 0.12f, 30, 2000, 1.2f, 250f, 45f, 70f, 0.8f, 1f, 1f, 0.5f, 0.5f);
    static readonly ProjectileSpec Ball = new("ebps_test_plasma", 40f, 5f, "raycast", 0f, [new(0f, 0f)], true, true, false, 0, 0f, 0f, 0, null,
        new SplashSpec(1.5f, Plain with { Damage = 1f }, [new(0f, 1f), new(1.5f, 1f)], false, false, "target_all"));
    static readonly WeaponSpec BallGun = Gun with { Name = "sm_mp_exotic_plasma_cannon", Delivery = DeliveryKind.Projectile, Projectile = Ball, ShotCooldown = 1f };
    static readonly WeaponSpec Bolter = Gun with { Name = "sm_mp_ranged_bolter" };
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 1f, 10f, 3.5f);
    static readonly MusouRules Still = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] };

    const int Human = 1;

    static World War(int primary, Vector3 at)
    {
        var c = new ClassLoadout("sm - tactical", primary, -1, [Light]) { PrimaryChoices = [primary], EquipmentChoices = [], Side = Team.SpaceMarines, Role = "tactical" };
        var config = MusouSimTests.Base with
        {
            Weapons = [Gun, BallGun, Bolter], Thrown = [], Classes = [c, c with { Name = "csm - tactical", Side = Team.Chaos }], Extras = ContentExtras.Musou, MusouRules = Still, RespawnTime = 60f,
        };
        var map = TfwTestArena.Build();
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        w.Spawn(Human, 0);
        Tick(w);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.TfwRespawnAt(Human, at, 0f);
        Tick(w);
        w.Musou!.Frozen = true;
        return w;
    }

    static void Tick(World w, InputButtons buttons = InputButtons.None) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, buttons));

    static void Shoot(World w)
    {
        Tick(w, InputButtons.Fire);
        for (int k = 0; k < 20 && w.LastShots.Count == 0 && w.Projectiles.Count == 0; k++) Tick(w, InputButtons.Fire);
        Assert.True(w.LastShots.Count > 0 || w.Projectiles.Count > 0);
    }

    static float HealthOf(World w, ushort id) => w.Musou!.TryGet(id, out var e) ? e.Health : 0f;

    /// <summary>A Boy 4 m ahead that has stepped sideways out of the line a tick ago (the trail still holds the old pose, the current body is clear of the shot).</summary>
    static (ushort Boy, Vector3 Eye) Sidestepped(World w)
    {
        var me = w.Players[Human].Position;
        ushort boy = w.Musou!.Spawn(MusouEnemyType.Boy, me + new Vector3(0f, 4f, 0f), 0f, 1000f);
        Assert.NotEqual(0, boy);
        Tick(w);   // the trail stamps the Ork on the line
        float side = w.Musou.ShotBody(MusouEnemyType.Boy).Radius + 0.3f;   // outside the current body, inside the trail's reach
        Assert.True(w.Musou.Relocate(boy, me + new Vector3(side, 4f, 0f)));
        Tick(w);
        var eye = new Vector3(w.Players[Human].Position.X, w.Players[Human].Position.Y, w.Players[Human].Position.Z + 1f);
        // The premise: an instant shot down this line would still find him on the trail, a flying projectile would not.
        Assert.Equal(boy, w.Musou.Raycast(eye, Vector3.UnitY, 30f, out _, out _));
        Assert.Equal((ushort)0, w.Musou.RaycastCurrent(eye, Vector3.UnitY, 30f, out _, out _));
        return (boy, eye);
    }

    [Fact]
    public void A_plasma_ball_misses_an_ork_that_stepped_aside()
    {
        var w = War(1, new Vector3(0f, -60f, 0f));
        var (boy, _) = Sidestepped(w);
        Shoot(w);
        for (int k = 0; k < 40 && w.Projectiles.Count > 0; k++) Tick(w);
        Assert.Equal(1000f, HealthOf(w, boy));   // no direct hit, no burn
        Assert.Equal(0, w.TfwBurnsSet);
    }

    [Fact]
    public void A_bolter_shot_at_the_same_moment_still_hits_him_through_the_trail()
    {
        var w = War(2, new Vector3(0f, -60f, 0f));
        var (boy, _) = Sidestepped(w);
        Shoot(w);
        Assert.True(HealthOf(w, boy) < 1000f);
    }

    [Fact]
    public void A_plasma_ball_still_hits_an_ork_standing_in_its_path_and_the_kill_scores()
    {
        // A battle (TfwRig): the human kills a stationary Ork with a plasma ball and the run's score rises, so the MusouRunPin's "0" cannot hide broken scoring.
        var c = TfwRigClass();
        var config = TfwRig.Config with { Weapons = [BallGun, TfwRig.Config.Weapons[1]], Classes = [c, c with { Name = "csm - tactical", Side = Team.Chaos }] };
        var w = TfwRig.War(null, config);
        var battle = TfwRig.Deploy(w);
        Assert.NotNull(battle);
        TfwRig.KillAll(w);
        w.Musou!.Frozen = true;
        TfwRig.Teleport(w, new Vector3(0f, -60f, 0f));
        TfwRig.Steps(w, 2);
        TfwRig.KillAll(w);
        uint before = w.Tfw!.Score.TotalOf(TfwRig.Human);
        var me = w.Players[TfwRig.Human].Position;
        ushort boy = w.Musou.Spawn(MusouEnemyType.Boy, me + new Vector3(0f, 4f, 0f), 0f, 5f);
        Assert.NotEqual(0, boy);
        for (int k = 0; k < 40 && w.Musou.TryGet(boy, out var e) && e.Alive; k++) w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, k == 0 ? InputButtons.Fire : InputButtons.None));
        Assert.False(w.Musou.TryGet(boy, out var dead) && dead.Alive);
        Assert.True(w.Tfw.Score.TotalOf(TfwRig.Human) > before);
    }

    static ClassLoadout TfwRigClass() => new("sm - tactical", 0, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], Side = Team.SpaceMarines, Role = "tactical" };
}
