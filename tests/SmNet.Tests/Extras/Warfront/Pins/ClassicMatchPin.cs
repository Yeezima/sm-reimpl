using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Pins;

/// <summary>
/// W0 pins (Warfront): the scripted 900-tick Classic match of <c>MusouIsolationTests</c> (<c>MusouIsolationTests.Replay</c>, reused, not rewritten; its fixture config is copied here, do not change it), with the
/// SHA-256 over every tick's snapshots, scores and kills, for Extras None and All. Captured on the untouched baseline (commit 5428594e). Classic must replay the same under Warfront.
/// </summary>
public class ClassicMatchPin(ITestOutputHelper output)
{
    static readonly WeaponSpec Rifle = new("iso_rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("iso_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static readonly MeleeStep Heavy = new(1.2f, 0.3f, 0f, 0f, 0.8f, 0.9f, 45f, 10f, 3.5f) { PrimaryReaction = new(0f, ReactionKind.Stun) };

    static Vector2[] Line(float metres, int samples) => [.. Enumerable.Range(0, samples + 1).Select(k => new Vector2(0f, metres * k / samples))];
    static readonly RushMove RushClip = new("iso_rush", Line(12f, 12), 1.2f, 1.0f, 0.6f, 0.5f, 1.0f);
    static readonly RushMove StopClip = new("iso_rush_stop", Line(0.5f, 10), 1.0f, 0.9f, 0.9f, 0.6f, 0.3f);
    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    static readonly RushSpec Charge = new("iso_bullrush", RushClip, StopClip, Plain with { Damage = 75f, ShieldDamage = 25f }, new ReactionEntry(0f, ReactionKind.Stun),
        Plain with { Damage = 30f }, new ReactionEntry(0f, ReactionKind.Interrupt), 4f, 1f) { ContactUntil = 0.6f, WallFrom = 0.3f, HyperArmourUntil = 0.3f };

    static readonly RigidSpec Rigid = new(65f, 0.3f, 0.4f, 0.05f, 0.05f, [new(0f, 1f), new(90f, 0.2f)], [new(0f, 1f), new(90f, 0.2f)], 0.1f, 0.1f);
    static readonly DistancePoint[] FragFallOff = [new(0f, 1f), new(2.5f, 1f), new(2.6f, 0.5f), new(5f, 0.5f), new(5.1f, 0.3f), new(7f, 0.3f), new(10f, 0.05f)];
    static readonly ProjectileSpec FragBall = new("ebps\\iso\\frag", 20f, 30f, "rigidbody", 0f, [new(5f, 0f), new(10f, 1f)], false, true, false, 1, 1f, 1.5f, 0, null,
        new SplashSpec(6f, new DamageSpec(120f, 0f, 1f, 1f, 1f, 1f, false), FragFallOff, true, false, "target_all")) { Rigid = Rigid, StopAtMaxBounce = true };
    static readonly ThrownSpec Frag = new("iso_frag", 3, 3, 1f, 10f, FragBall);

    static ClassLoadout Class(string name, Team side) =>
        new(name, 0, 1, [Light, Light, Light]) { PrimaryChoices = [0], PistolChoices = [1], EquipmentChoices = [0], MeleeHeavy = [Heavy], MeleeRush = Charge, Side = side, Role = "tactical" };

    static readonly SimConfig Fixture = new()
    {
        Weapons = [Rifle, Pistol],
        Thrown = [Frag],
        Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)],
        HitVolumes = [HitVolume.Sphere("Head", HitZone.Head, new(0f, 0f, 1.9f), 0.2f), HitVolume.Capsule("Spine", HitZone.Body, new(0f, 0f, 0.4f), new(0f, 0f, 1.5f), 0.35f)],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        HealthMax = 150f, ShieldMax = 50f, RespawnTime = 2f,
    };

    // Recaptured 2026-11-19 on main 1809fa31 (main moved after 5428594e); this tree and an untouched main give the same value.
    // None and All hash identically: the fixture has no weapon, class or rule that an extended flag changes (the Classic extras only add content that this fixture lacks).
    const string NoneReplay = "1A6C8AC48FDD26A34300BFDF27134041FD22476A9910DD52B7D2225CA89A18AD", AllReplay = "1A6C8AC48FDD26A34300BFDF27134041FD22476A9910DD52B7D2225CA89A18AD";

    [Fact]
    public void The_classic_replay_with_no_extras_is_as_before_Warfront()
    {
        var (hash, stats) = MusouIsolationTests.Replay(Fixture with { Extras = ContentExtras.None });
        output.WriteLine($"None {hash} {stats}");
        Assert.Equal(NoneReplay, hash);
    }

    [Fact]
    public void The_classic_replay_with_all_extras_is_as_before_Warfront()
    {
        var (hash, stats) = MusouIsolationTests.Replay(Fixture with { Extras = ContentExtras.All });
        output.WriteLine($"All {hash} {stats}");
        Assert.Equal(AllReplay, hash);
    }
}
