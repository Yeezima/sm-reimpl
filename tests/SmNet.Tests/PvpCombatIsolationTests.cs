using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// The owner's item 28: Musou Mode's frag, cleave and piercing must not touch Annihilation or Seize Ground at all. Faithful and extended worlds on this synthetic fixture: one throw is one projectile
/// with the item's own spec and blast; a shot stops on the first enemy; a melee step deals what it always dealt, with the outcome pinned against values captured on a tree whose core files are main's
/// (M5 changed no core file; its rules sit on Musou-only state). Core folder, so it also runs in <c>faithful-build.cmd</c>. <c>MusouIsolationTests</c> pins the 900-tick replay and the config hashes.
/// </summary>
public class PvpCombatIsolationTests(ITestOutputHelper output)
{
    static readonly WeaponSpec Rifle = new("sm_mp_ranged_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static readonly RigidSpec Rigid = new(65f, 0.3f, 0.4f, 0.05f, 0.05f, [new(0f, 1f), new(90f, 0.2f)], [new(0f, 1f), new(90f, 0.2f)], 0.1f, 0.1f);
    static readonly SplashSpec FragBlast = new(10f, new DamageSpec(200f, 0f, 1f, 1f, 1f, 1f, false), [new(0f, 1f), new(2.5f, 1f), new(5f, 0.5f), new(7f, 0.3f), new(10f, 0.05f)], false, false, "target_all");
    static readonly ProjectileSpec FragBall = new("ebps\\iso\\frag", 20f, 30f, "rigidbody", 0f, [new(5f, 0f), new(10f, 1f)], false, true, false, 1, 1f, 1.5f, 0, null, FragBlast) { Rigid = Rigid, StopAtMaxBounce = true };
    static readonly ThrownSpec Frag = new("sm_mp_eq_frag_grenade", 3, 3, 1f, 10f, FragBall);

    static ClassLoadout Class(string name, Team side) => new(name, 0, -1, [Light, Light, Light]) { PrimaryChoices = [0], EquipmentChoices = [0], Side = side, Role = "tactical" };

    static readonly SimConfig Faithful = new()
    {
        Weapons = [Rifle], Thrown = [Frag], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)],
        HitVolumes = [HitVolume.Sphere("Head", HitZone.Head, new(0f, 0f, 1.9f), 0.2f), HitVolume.Capsule("Spine", HitZone.Body, new(0f, 0f, 0.4f), new(0f, 0f, 1.5f), 0.35f)],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f, HealthMax = 150f, ShieldMax = 50f, RespawnTime = 2f,
    };

    public static TheoryData<bool> Profiles => new() { false, true };

    /// <summary>One Space Marine at the origin facing +y and Chaos players in a line in front of him.</summary>
    static World Line(bool extended, params float[] ahead)
    {
        var spawns = new List<SpawnPoint> { new(Vector3.Zero, 0f) };
        foreach (float y in ahead) spawns.Add(new SpawnPoint(new Vector3(0f, y, 0f), MathF.PI));
        var w = new World(extended ? Faithful with { Extras = ContentExtras.All } : Faithful, null, spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f } };
        w.Spawn(1, 0);
        for (int k = 0; k < ahead.Length; k++) w.Spawn(2 + k, 1);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, InputButtons.None));
        Assert.Equal(MatchState.Playing, w.State);
        return w;
    }

    static void Tick(World w, InputButtons buttons = InputButtons.None)
    {
        uint view = Math.Max(1u, w.Tick), body = w.Tick > 1 ? w.Tick - 1 : 0;
        w.Step(id => new PlayerInput(w.Tick, 0f, 0f, 0f, id == 1 ? buttons : InputButtons.None, ViewTick: view, BodyTick: body));
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void One_throw_is_one_frag_with_the_data_blast(bool extended)
    {
        var w = Line(extended, 30f);
        int carried = w.Players[1].Grenades;
        Tick(w, InputButtons.Grenade);
        int mostFlying = 0;
        DetonationEvent? blast = null;
        for (int k = 0; k < 300 && blast is null; k++)
        {
            Tick(w);
            mostFlying = Math.Max(mostFlying, w.Projectiles.Count);
            if (w.Projectiles.Count == 1) Assert.Same(FragBall, w.Projectiles[0].Spec);
            if (w.LastDetonations.Count > 0) blast = w.LastDetonations[0];
        }
        Assert.Equal(1, mostFlying);
        Assert.Equal(carried - 1, w.Players[1].Grenades);
        Assert.NotNull(blast);
        Assert.Equal(FragBlast.Radius, blast!.Value.Radius);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void A_shot_stops_on_the_first_enemy(bool extended)
    {
        var w = Line(extended, 8f, 11f, 14f);
        // Where each stands is the spawn rule's: the shooter faces along the line, down the side with two or more enemies.
        var me = w.Players[1].Position;
        float Left(int id) => w.Players[id].Health + w.Players[id].Shield;
        var behind = w.Players.Values.Where(p => p.PlayerId != 1 && p.Position.Y > me.Y).ToArray();
        var before = w.Players.Values.Where(p => p.PlayerId != 1 && p.Position.Y < me.Y).ToArray();
        float yaw = behind.Length >= before.Length ? 0f : MathF.PI;
        var ahead = (behind.Length >= before.Length ? behind : before).OrderBy(p => MathF.Abs(p.Position.Y - me.Y)).Select(p => p.PlayerId).ToArray();
        Assert.True(ahead.Length >= 2);
        float full = Left(ahead[0]);
        for (int k = 0; k < 90 && Left(ahead[0]) == full; k++)
        {
            uint view = Math.Max(1u, w.Tick), body = w.Tick > 1 ? w.Tick - 1 : 0;
            w.Step(id => new PlayerInput(w.Tick, 0f, 0f, id == 1 ? yaw : w.Players[id].Yaw, id == 1 ? InputButtons.Fire : InputButtons.None, ViewTick: view, BodyTick: body));
        }
        Assert.True(Left(ahead[0]) < full);
        foreach (int id in ahead.Skip(1)) Assert.Equal(full, Left(id));
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void A_melee_step_strikes_as_it_always_did(bool extended)
    {
        var w = Line(extended, 1.5f, 1.5f, 1.5f, 1.5f);
        var hits = new List<HitEvent>();
        for (int k = 0; k < 30; k++)
        {
            Tick(w, k < 3 ? InputButtons.Melee : InputButtons.None);
            hits.AddRange(w.LastHits.Where(h => h.Melee));
        }
        string outcome = string.Join(" ", hits.Select(h => $"{h.Target}:{h.ShieldDamage + h.HealthDamage:F1}"));
        output.WriteLine($"melee: {outcome}");
        Assert.Equal(PinnedMelee, outcome);
    }

    /// <summary>The outcome on the core as main has it (M5 has no diff in any core file).</summary>
    const string PinnedMelee = "3:25.0 5:10.0 4:10.0 2:10.0";
}
