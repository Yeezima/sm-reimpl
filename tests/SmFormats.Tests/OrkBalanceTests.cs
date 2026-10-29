using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// Ork O4: the Ork Boy's balance (docs/ork-faction-design.md section 2) on synthetic data shaped like the install's (no original bytes):
/// Data mode keeps the data, Parity reaches the section 2 table; the class is appended after the six without touching them; the
/// raw readers of guns and the blueprint.
/// </summary>
public class OrkBalanceTests
{
    const float Dt = 1f / 30f;
    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);

    // ---- the Tactical reference (values as the install's) ----
    static readonly WeaponSpec Bolter = new("sm_mp_ranged_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 22f, 0.133f, 30, 270, 1.3f, 250f, 45f, 70f, 0.8f, 2.55f, 1f, 0.5f, 0.5f)
    { RaiseDelay = 0.2f };
    static readonly WeaponSpec BoltPistol = new("sm_mp_pistol_bolt_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 40f, 0.25f, 10, 80, 0.75f, 250f, 30f, 40f, 0.78f, 2.5f, 1f, 0.5f, 0.5f)
    { RaiseDelay = 0.1667f };
    static MeleeStep Step(float damage, float secondary, ReactionKind reaction) =>
        new(1f, 0.3f, 0.5f, 0.8f, 0.45f, 0.8f, damage, secondary, 3.5f, 1f, 0.8f) { PrimaryReaction = new(0f, reaction), SecondaryReaction = new(0f, ReactionKind.Interrupt) };
    static readonly RushMove BullRush = new("SM_BUL_Hit_M_5m", [Vector2.Zero, new(0f, 15f)], 1.2f, 1.0f, 0.667f, 0.5f, 1.0f);
    static readonly RushSpec Bull = new("ability_bullrush_01", BullRush, BullRush with { Animation = "stop" }, Plain with { Damage = 75f, ShieldDamage = 25f },
        new ReactionEntry(0f, ReactionKind.Stun), Plain with { Damage = 30f }, new ReactionEntry(0f, ReactionKind.Interrupt), 4f, 1f);
    static readonly MeleeCombo KnifeBase = new("sm_mp_melee_combat_knife", [Step(40f, 20f, ReactionKind.Interrupt), Step(40f, 20f, ReactionKind.Interrupt), Step(40f, 20f, ReactionKind.Interrupt)])
    { Heavy = [Step(35f, 10f, ReactionKind.Stun)], Rush = Bull };
    static readonly MeleeCombo Knife = KnifeBase with
    {
        Variants = [new(["serrated"], KnifeBase with { Steps = [Step(65f, 32f, ReactionKind.Interrupt), Step(65f, 32f, ReactionKind.Interrupt), Step(65f, 32f, ReactionKind.Interrupt)] })],
    };
    static readonly DistancePoint[] FragBands = [new(0f, 1f), new(2.5f, 1f), new(5f, 0.5f), new(10f, 0.05f)];
    static readonly SplashSpec FragBlast = new(10f, Plain with { Damage = 200f, Type = DamageType.Explosive }, FragBands, true, false, "target_all")
    { Reactions = [new(0.5f, ReactionKind.Stun)] };
    static ProjectileSpec Ball(string name, float speed, SplashSpec splash) =>
        new(name, speed, 30f, "rigidbody", 0f, [new(0f, 1f)], false, true, false, 1, 2f, 1.5f, 0, null, splash);
    static readonly ThrownSpec FragBase = new("sm_mp_eq_frag_grenade", 2, 1, 1f, 30f, Ball("ebps\\projectiles\\frag", 47.5f, FragBlast));
    static readonly ThrownSpec Frag = FragBase with { Variants = [new(["armory"], FragBase with { Count = 4, InitialCount = 2 })] };
    static EvadeMove Move(string clip, float duration, float x) => new(clip, [Vector2.Zero, new(x * 0.5f, 0f), new(x, 0f)], duration, 0.667f * duration / 1.1667f, 0.6f, 0.7f, 0.7f, duration);
    static readonly EvadeSpec Roll = new("sm_mp_core_abil_evade", Move("F", 1.1667f, 0f), Move("B", 1.1667f, 0f), Move("L", 1.1667f, -6f), Move("R", 1.1667f, 6f))
    { MinInterval = 0.8f, TargetableModeTime = 0.695f, CollisionModeTime = 0.546f, Diagonals = true, AnimationSet = "evade" };
    static readonly BodySpec Marine = new("marine", 6.5f, 6f, 4.5f, 10f, 3.6f, 3f, 3f, 2f, 100f, 5f, 5f, 100f, 7f, 33f);
    static readonly OrkReference Reference = new(Bolter, BoltPistol, Knife, Frag, Roll, Marine);

    // ---- the Ork's data (shaped like the install's AI tuning) ----
    static readonly WeaponSpec ShootaRead = new(OrkClass.Shoota, WeaponSlot.Ranged, TriggerKind.FullAuto, 2f, 0.11f, 40, 0, 2f, 100f, 100f, 100f, 1f, 1f, 1f, 1f, 1f);
    static readonly WeaponSpec SluggaRead = new(OrkClass.Slugga, WeaponSlot.Ranged, TriggerKind.FullAuto, 2f, 0.2f, 30, 0, 1.5f, 100f, 100f, 100f, 1f, 1f, 1f, 1f, 1f);
    static readonly RushSpec OrkRush = Bull with
    {
        Ability = OrkClass.RushAbility, Primary = Plain with { Damage = 15f, ShieldDamage = 15f }, PrimaryReaction = new(0f, ReactionKind.Interrupt),
        Secondary = Plain, SecondaryReaction = default, Range = 10f, MinInterval = 0f,
    };
    static readonly MeleeCombo ChoppaRead = new(OrkClass.Choppa, [Step(10f, 0f, ReactionKind.None), Step(10f, 0f, ReactionKind.None)])
    { Heavy = [Step(24f, 0f, ReactionKind.Interrupt)], Rush = OrkRush };
    static readonly ThrownSpec StikkRead = new(OrkClass.Stikkbomb, 5, -1, 0f, 30f,
        Ball("ebps\\projectiles\\pp_orkgrenade_stikk_mp", 30f, new SplashSpec(8f, Plain with { Damage = 30f, ShieldDamage = 70f }, [new(0f, 1f), new(8f, 0.2f)], true, false, "target_all")));
    static readonly EvadeSpec HopRead = new(OrkClass.EvadeWargear, Move("R", 1.511f, 3.7f), Move("L", 2.567f, -3.94f), Move("L", 2.567f, -3.94f), Move("R", 1.511f, 3.7f))
    { SideOnly = true, AnimationSet = EvadeTuning.OrkSideHop };
    static readonly OrkData Data = new(
        new OrkGunData(ShootaRead, 0.11f, null, 40, 0, true, 2f),
        new OrkGunData(SluggaRead, null, 0.5f, null, null, false, null),
        ChoppaRead, StikkRead,
        new OrkBodyData(85f, 4f, null, null, null, null, 6.5f, 6.5f, 6.5f, 10f, 3.6f, 6f, 4f, 4f),
        HopRead, [HitVolume.Sphere("Head", HitZone.Head, new(0f, 0f, 1.8f), 0.2f), HitVolume.Capsule("Spine", HitZone.Body, new(0f, 0f, 0.6f), new(0f, 0f, 1.5f), 0.35f)])
    { ChoppaAnimations = ["BOY_1HM_MEL_Cmbo_01_Atk01", "BOY_1HM_MEL_Cmbo_01_Atk02"] };

    static OrkBuild Parity => OrkBalance.Apply(Data, Reference, OrkBalanceMode.Parity, Dt);
    static OrkBuild DataMode => OrkBalance.Apply(Data, Reference, OrkBalanceMode.Data, Dt);

    [Fact]
    public void CooldownsRoundUpToWholeTicks()
    {
        Assert.Equal(4 * Dt, OrkBalance.EffectiveCooldown(0.133f, Dt), 5);
        Assert.Equal(4 * Dt, OrkBalance.EffectiveCooldown(0.11f, Dt), 5);
        Assert.Equal(8 * Dt, OrkBalance.EffectiveCooldown(0.25f, Dt), 5);
        Assert.Equal(15 * Dt, OrkBalance.EffectiveCooldown(0.5f, Dt), 5);
        Assert.Equal(Dt, OrkBalance.EffectiveCooldown(0f, Dt), 5);
    }

    [Fact]
    public void ParityReachesTheSectionTwoTable()
    {
        var b = Parity;
        // Body: 100 + 100 with the marine's regeneration; every movement speed the Tactical's (the owner's decision 2026-10-28).
        Assert.Equal((100f, 5f, 5f, 100f, 7f, 33f), (b.Body.HealthMax, b.Body.HealthRegenDelay, b.Body.HealthRegenRate, b.Body.ShieldMax, b.Body.ShieldRegenDelay, b.Body.ShieldRegenRate));
        Assert.Equal((Marine.RunSpeed, Marine.StrafeSpeed, Marine.BackSpeed, Marine.SprintSpeed, Marine.WalkSpeed), (b.Body.RunSpeed, b.Body.StrafeSpeed, b.Body.BackSpeed, b.Body.SprintSpeed, b.Body.WalkSpeed));
        Assert.Equal((Marine.AimRunSpeed, Marine.AimStrafeSpeed, Marine.AimBackSpeed), (b.Body.AimRunSpeed, b.Body.AimStrafeSpeed, b.Body.AimBackSpeed));
        Assert.Equal((6.5f, 6f, 4.5f, 10f, 3.6f, 3f, 3f, 2f), (b.Body.RunSpeed, b.Body.StrafeSpeed, b.Body.BackSpeed, b.Body.SprintSpeed, b.Body.WalkSpeed, b.Body.AimRunSpeed, b.Body.AimStrafeSpeed, b.Body.AimBackSpeed));
        Assert.Same(Data.HitVolumes, b.Body.HitVolumes);
        // Shoota: 22 every 4 ticks (165 per s), clip 40, reload 2, 260 in reserve, the bolter's range, fall-off and multipliers.
        Assert.Equal(22f, b.Shoota.Damage, 3);
        Assert.Equal((0.11f, 40, 2f, 260), (b.Shoota.ShotCooldown, b.Shoota.ClipSize, b.Shoota.ReloadTime, b.Shoota.ReserveSize));
        Assert.Equal((250f, 45f, 70f, 0.8f, 2.55f, 0.5f), (b.Shoota.MaxRange, b.Shoota.FallOffStart, b.Shoota.FallOffEnd, b.Shoota.FallOffEndMultiplier, b.Shoota.HeadMultiplier, b.Shoota.ShieldBodyMultiplier));
        Assert.Equal(0.2f, b.Shoota.RaiseDelay);
        // Slugga: 75 every 15 ticks (150 per s), the bolt pistol's damage per clip (5 rounds), its reload, 48 rounds a life, in the pistol slot.
        Assert.Equal(75f, b.Slugga.Damage, 3);
        Assert.Equal((WeaponSlot.Pistol, 0.5f, 5, 0.75f, 43), (b.Slugga.Slot, b.Slugga.ShotCooldown, b.Slugga.ClipSize, b.Slugga.ReloadTime, b.Slugga.ReserveSize));
        Assert.Equal((250f, 2.5f), (b.Slugga.MaxRange, b.Slugga.HeadMultiplier));
        // Both guns within ±10 % of their reference's sustained damage.
        foreach (var (gun, reference) in new[] { (b.Shoota, Bolter), (b.Slugga, BoltPistol) })
            Assert.InRange(OrkBalance.Sustained(gun, Dt) / OrkBalance.Sustained(reference, Dt), 1f - OrkBalance.SustainedTolerance, 1f + OrkBalance.SustainedTolerance);
        Assert.All(b.Rows.Where(r => r.Rule == BalanceRule.Check && r.Stat.Contains("sustained")), r => Assert.StartsWith("within", r.Note));
        // Choppa: the knife's hits, reactions and multipliers on the choppa's timing; the heavy is the knife's Y1 with its stun; the charge the bull rush's.
        Assert.All(b.Choppa.Steps, s => Assert.Equal((40f, 20f, ReactionKind.Interrupt, 0.8f), (s.Damage, s.SecondaryDamage, s.PrimaryReaction.Kind, s.ShieldMultiplier)));
        Assert.Equal(ChoppaRead.Steps[0].HitTime, b.Choppa.Steps[0].HitTime);
        Assert.Equal((35f, ReactionKind.Stun), (b.Choppa.Heavy[0].Damage, b.Choppa.Heavy[0].PrimaryReaction.Kind));
        Assert.Equal((75f, 25f, ReactionKind.Stun, 4f, 1f), (b.Choppa.Rush!.Primary.Damage, b.Choppa.Rush.Primary.ShieldDamage, b.Choppa.Rush.PrimaryReaction.Kind, b.Choppa.Rush.Range, b.Choppa.Rush.MinInterval));
        Assert.Same(OrkRush.Rush, b.Choppa.Rush.Rush);   // its own motion
        // Stikkbomb: the frag's count, spawn count, cooldown and blast; its own flight.
        var stikk = b.Stikk!;
        Assert.Equal((2, 1, 1f), (stikk.Count, stikk.SpawnCount, stikk.Cooldown));
        Assert.Same(FragBlast, stikk.Projectile.Splash);
        Assert.Equal((30f, 2f, "ebps\\projectiles\\pp_orkgrenade_stikk_mp"), (stikk.Projectile.Speed, stikk.Projectile.GroundLifetime, stikk.Projectile.Blueprint));
        // Factors as printed: health ×1.1765, shoota ×11, slugga ×37.5, stikk ×6.667 and ×0, back ×0.692.
        float F(string stat) => b.Rows.Single(r => r.Stat == stat).Factor;
        Assert.Equal(100f / 85f, F("Health max"), 3);
        Assert.Equal(11f, F("Shoota damage"), 2);
        Assert.Equal(37.5f, F("Slugga damage"), 2);
        Assert.Equal(200f / 30f, F("Stikk damage"), 3);
        Assert.Equal(0f, F("Stikk shield-only damage"));
        Assert.Equal(4.5f / 6.5f, F("Back speed"), 3);
        Assert.Equal(6f / 6.5f, F("Strafe speed"), 3);
        Assert.Equal(0.5f, F("Aim run speed"), 3);
    }

    [Fact]
    public void DataModeKeepsTheData()
    {
        var b = DataMode;
        Assert.Equal((85f, 4f, 0f, 0f, 0f, 0f), (b.Body.HealthMax, b.Body.HealthRegenDelay, b.Body.HealthRegenRate, b.Body.ShieldMax, b.Body.ShieldRegenDelay, b.Body.ShieldRegenRate));
        Assert.Equal((6.5f, 6.5f, 6.5f, 10f, 3.6f, 6f, 4f, 4f), (b.Body.RunSpeed, b.Body.StrafeSpeed, b.Body.BackSpeed, b.Body.SprintSpeed, b.Body.WalkSpeed, b.Body.AimRunSpeed, b.Body.AimStrafeSpeed, b.Body.AimBackSpeed));   // the data's
        Assert.Equal((2f, 0.11f, 40, OrkBalance.Unlimited, 2f, 100f), (b.Shoota.Damage, b.Shoota.ShotCooldown, b.Shoota.ClipSize, b.Shoota.ReserveSize, b.Shoota.ReloadTime, b.Shoota.MaxRange));
        Assert.Equal((1f, 1f), (b.Shoota.HeadMultiplier, b.Shoota.FallOffEndMultiplier));
        Assert.Equal((2f, 0.5f, OrkBalance.Unlimited, 0), (b.Slugga.Damage, b.Slugga.ShotCooldown, b.Slugga.ClipSize, b.Slugga.ReserveSize));
        Assert.Equal(ChoppaRead.Steps, b.Choppa.Steps);
        Assert.Equal(ChoppaRead.Heavy, b.Choppa.Heavy);
        Assert.Same(OrkRush, b.Choppa.Rush);
        Assert.Empty(b.Choppa.Variants);
        Assert.Equal((5, 5, 0f), (b.Stikk!.Count, b.Stikk.SpawnCount, b.Stikk.Cooldown));
        Assert.Same(StikkRead.Projectile, b.Stikk.Projectile);
        Assert.Empty(b.Stikk.Variants);
        Assert.Equal((2.567f, 1.511f), (b.Evade!.Left.Duration, b.Evade.Right.Duration));
        Assert.All(b.Rows.Where(r => r.Rule is not BalanceRule.Check and not BalanceRule.DataOrReference), r => Assert.Equal(1f, r.Factor));
    }

    [Fact]
    public void PerkVariantsAreMirrored()
    {
        var b = Parity;
        var serrated = Assert.Single(b.Choppa.Variants);
        Assert.Equal(["serrated"], serrated.Upgrades);
        Assert.All(serrated.Spec.Steps, s => Assert.Equal((65f, 32f), (s.Damage, s.SecondaryDamage)));
        Assert.Equal(b.Choppa.Steps.Count, serrated.Spec.Steps.Count);
        Assert.Empty(serrated.Spec.Variants);
        var armory = Assert.Single(b.Stikk!.Variants);
        Assert.Equal((4, 2), (armory.Spec.Count, armory.Spec.SpawnCount));
        Assert.Equal(30f, armory.Spec.Projectile.Speed);
        // A player with the perk gets the variant.
        Assert.Same(serrated.Spec, b.Choppa.With(new PerkUpgrades("serrated", null)));
    }

    [Fact]
    public void TheHopIsSideOnlyAndRetimedToTheRollInParity()
    {
        var hop = Parity.Evade!;
        Assert.True(hop.SideOnly);
        Assert.False(hop.Diagonals);
        Assert.Equal((Roll.Left.Duration, Roll.Left.MoveAt, Roll.Right.Duration), (hop.Left.Duration, hop.Left.MoveAt, hop.Right.Duration));
        Assert.Equal(HopRead.Left.Path, hop.Left.Path);   // the clip's own path
        Assert.Equal((Roll.MinInterval, Roll.HyperArmourTime), (hop.MinInterval, hop.HyperArmourTime));   // no hyper-armour window in the clip: the roll's
        Assert.Same(hop.Right, hop.Forward);
        Assert.Same(hop.Left, hop.Back);
        Assert.Equal(Roll.HyperArmourTime, DataMode.Evade!.HyperArmourTime);   // the fallback in Data mode too
    }

    [Fact]
    public void WithoutAChargeTheRowSaysSo()
    {
        var data = Data with { Choppa = ChoppaRead with { Rush = null } };
        var b = OrkBalance.Apply(data, Reference, OrkBalanceMode.Parity, Dt);
        Assert.Null(b.Choppa.Rush);
        Assert.Contains(b.Rows, r => r.Stat == "Choppa charge" && r.Note.Contains("no charge"));
    }

    // ---- the class appended ----

    static MpClasses Six()
    {
        var weapons = new[] { Bolter, BoltPistol };
        var loadouts = new[]
        {
            new ClassLoadout("sm - tactical", 0, 1, Knife.Steps) { PrimaryChoices = [0], PistolChoices = [1], MeleeChoices = [Knife], EquipmentChoices = [0], Evade = 0,
                Side = Team.SpaceMarines, Role = "tactical", PerkChoices = [0, 1], DefaultPerk = 1, MeleeHeavy = Knife.Heavy, MeleeRush = Knife.Rush },
            new ClassLoadout("csm - tactical", 0, 1, Knife.Steps) { PrimaryChoices = [0], PistolChoices = [1], MeleeChoices = [Knife], EquipmentChoices = [0], Evade = 0,
                Side = Team.Chaos, Role = "tactical", PerkChoices = [0, 1], DefaultPerk = 1 },
        };
        var classes = new[]
        {
            new MpClass("sm - tactical", "tactical", "space_marine", [], [], ["mp_perk_a", "mp_perk_b"], "mp_perk_b"),
            new MpClass("csm - tactical", "tactical", "chaos", [], [], ["mp_perk_a", "mp_perk_b"], "mp_perk_b"),
        };
        return new MpClasses(classes, weapons, loadouts, [[], []], [])
        {
            Thrown = [Frag], Evades = [Roll], Perks = [new PerkSpec("mp_perk_a", "a", PerkKind.Class, 1), new PerkSpec("mp_perk_b", "b", PerkKind.Class, 1)],
        };
    }

    [Fact]
    public void TheOrkIsAppendedAfterTheOthersWithTheTacticalsPerks()
    {
        var six = Six();
        var reference = OrkClass.ReferenceOf(six, Marine, []);
        Assert.NotNull(reference);
        Assert.Same(Bolter, reference!.Bolter);
        var build = OrkBalance.Apply(Data, reference, OrkBalanceMode.Parity, Dt);
        var all = OrkClass.Append(six, Data, build, []);
        Assert.Equal(3, all.Loadouts.Count);
        Assert.Equal(six.Loadouts, all.Loadouts.Take(2));   // the others unchanged, same objects
        Assert.Equal(six.Weapons, all.Weapons.Take(2));
        var ork = all.Loadouts[2];
        Assert.Equal((OrkClass.Key, Team.None, OrkClass.Role, 0), (ork.Name, ork.Side, ork.Role, ork.Body));
        Assert.Equal((2, 3, 1, 1), (ork.Primary, ork.Pistol, ork.EquipmentChoices.Single(), ork.Evade));
        Assert.Equal(six.Loadouts[0].PerkChoices, ork.PerkChoices);
        Assert.Equal(1, ork.DefaultPerk);
        Assert.Same(build.Body, Assert.Single(all.Bodies));
        Assert.Equal(OrkClass.Key, all.Classes[2].Key);
        Assert.Equal(Data.ChoppaAnimations, all.MeleeAnimationsByWeapon[OrkClass.Choppa]);

        // The config takes it: the Ork's body for its class; without the Ork the hash is the six classes' as before.
        var withoutOrk = GameplayTuning.WithClasses(new SimConfig(), six.Weapons, six.Loadouts, six.Thrown, six.JumpPacks, six.Evades, null, six.Perks, six.Bodies)!;
        var withOrk = GameplayTuning.WithClasses(new SimConfig(), all.Weapons, all.Loadouts, all.Thrown, all.JumpPacks, all.Evades, null, all.Perks, all.Bodies)!;
        Assert.NotNull(withOrk);
        var noBodiesArgument = GameplayTuning.WithClasses(new SimConfig(), six.Weapons, six.Loadouts, six.Thrown, six.JumpPacks, six.Evades, null, six.Perks)!;
        Assert.Equal(noBodiesArgument.Hash(), withoutOrk.Hash());
        Assert.NotEqual(withoutOrk.Hash(), withOrk.Hash());
        Assert.NotEqual(withOrk.Hash(), GameplayTuning.WithClasses(new SimConfig(), all.Weapons, all.Loadouts, all.Thrown, all.JumpPacks, all.Evades, null, all.Perks,
            OrkClass.Append(six, Data, OrkBalance.Apply(Data, reference, OrkBalanceMode.Data, Dt), []).Bodies)!.Hash());   // the mode is in the hash
        Assert.Same(withOrk, withOrk.For(0));
        Assert.Equal((100f, 100f, 4.5f), (withOrk.For(2).HealthMax, withOrk.For(2).ShieldMax, withOrk.For(2).BackSpeed));
        Assert.Equal(Data.HitVolumes, withOrk.For(2).HitVolumes);
    }

    /// <summary>
    /// Ork O5: the server and every client build the match config the same way (<see cref="GameplayTuning.MatchOrk"/>: Parity, Data only with
    /// --ork-data on both), so two independent builds hash alike; the Ork carries its race (<see cref="Race.Orks"/>, on no side) and the others none;
    /// a class with a side and a race of its own, or an unknown race, is refused.
    /// </summary>
    [Fact]
    public void TheMatchConfigHashesAlikeOnServerAndClientAndTheOrkHasItsRace()
    {
        Assert.Equal(OrkBalanceMode.Parity, GameplayTuning.MatchOrk());
        Assert.Equal(OrkBalanceMode.Data, GameplayTuning.MatchOrk(data: true));
        SimConfig Build(OrkBalanceMode mode)   // what GameplayTuning.Load does with the read classes, once per process
        {
            var six = Six();
            var all = OrkClass.Append(six, Data, OrkBalance.Apply(Data, OrkClass.ReferenceOf(six, Marine, [])!, mode, Dt), []);
            return GameplayTuning.WithClasses(new SimConfig(), all.Weapons, all.Loadouts, all.Thrown, all.JumpPacks, all.Evades, null, all.Perks, all.Bodies)!;
        }
        var server = Build(GameplayTuning.MatchOrk());
        var client = Build(GameplayTuning.MatchOrk());
        Assert.Equal(server.Hash(), client.Hash());
        Assert.NotEqual(server.Hash(), Build(GameplayTuning.MatchOrk(data: true)).Hash());   // a client without --ork-data is refused by an --ork-data server
        Assert.Equal((Race.None, Race.None, Race.Orks), (server.Classes[0].Race, server.Classes[1].Race, server.Classes[2].Race));
        Assert.Equal(Team.None, server.Classes[2].Side);
        Assert.Equal(Race.Orks, Races.Of(server, 2, Team.SpaceMarines));
        Assert.Equal(Race.Orks, Races.Of(server, 2, Team.Chaos));
        Assert.Equal(Race.Chaos, Races.Of(server, 1, Team.Chaos));
        // The race is hashed only for a class that has one (the marine and Chaos classes hash as before; checked in OrkSimTests too).
        var noRace = server with { Classes = server.Classes.Select(c => c with { Race = Race.None }).ToArray() };
        Assert.NotEqual(server.Hash(), noRace.Hash());
        var six = Six();
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), six.Weapons, [six.Loadouts[0] with { Race = Race.Orks }, six.Loadouts[1]], six.Thrown, six.JumpPacks, six.Evades,
            null, six.Perks, six.Bodies));   // a side class with a race of its own
        var all = OrkClass.Append(six, Data, Parity, []);
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), all.Weapons, [.. all.Loadouts.Take(2), all.Loadouts[2] with { Race = (Race)9 }], all.Thrown, all.JumpPacks,
            all.Evades, null, all.Perks, all.Bodies));   // an unknown race
    }

    /// <summary>
    /// O5 review: a failed Ork read (a missing chunk folder: <see cref="DirectoryNotFoundException"/>; a corrupt coop archive) means "no Ork, note added":
    /// the six classes survive unchanged and the config hashes as without the Ork. Synthetic archives only.
    /// </summary>
    [Fact]
    public void AFailedOrkReadKeepsTheSixClassesAndTheirHash()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-ork-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "data", "packages"));
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "data", "packages", "ebps.oppc"), SmFormats.Obpk.SyntheticObpkWriter.Build(
                [new("attr_pc", GameplayTuning.MarineBlueprint, SyntheticBafWriter.PlayerBlueprint(6.5f, 6f, 4.5f, 10f, 3.6f, 0.2f, 100f))]));
            File.WriteAllBytes(Path.Combine(dir, "data", "packages", "wargear.oppc"), SmFormats.Obpk.SyntheticObpkWriter.Build(
                [new("attr_pc", "unrelated_wargear", SyntheticBafWriter.PlayerBlueprint(1f, 1f, 1f, 1f, 1f, 0.2f, 1f))]));
            var six = Six();
            SimConfig ConfigOf(MpClasses c) =>
                GameplayTuning.WithClasses(new SimConfig(), c.Weapons, c.Loadouts, c.Thrown, c.JumpPacks, c.Evades, c.Reactions, c.Perks, c.Bodies)!;
            ulong withoutOrk = ConfigOf(six).Hash();

            // No data/chunks folder at all.
            var notes = new List<string>();
            var kept = MpClasses.WithOrk(six, dir, OrkBalanceMode.Parity, null, notes);
            Assert.Same(six, kept);
            Assert.Contains(notes, n => n.StartsWith("ork:", StringComparison.Ordinal) && n.EndsWith("no Ork", StringComparison.Ordinal));
            Assert.Equal(withoutOrk, ConfigOf(kept).Hash());

            // A corrupt coop archive.
            Directory.CreateDirectory(Path.Combine(dir, "data", "chunks"));
            File.WriteAllBytes(Path.Combine(dir, RigProfiles.OrkArchive), [.. Enumerable.Range(0, 300).Select(i => (byte)(i * 37))]);
            notes.Clear();
            kept = MpClasses.WithOrk(six, dir, OrkBalanceMode.Data, null, notes);
            Assert.Same(six, kept);
            Assert.Contains(notes, n => n.EndsWith("no Ork", StringComparison.Ordinal));
            Assert.Equal(withoutOrk, ConfigOf(kept).Hash());

            // The whole load on this install (no class data at all): the faithful match tuning is the plain one, so a server and a client still agree;
            Assert.Equal(GameplayTuning.Load(dir).Config.Hash(), GameplayTuning.LoadForMatch(dir, ContentExtras.None).Config.Hash());
            // with the Orks (whose read fails here) only SimConfig.Extras differs.
            var orks = GameplayTuning.LoadForMatch(dir, ContentExtras.Orks).Config;
            Assert.Equal(ContentExtras.Orks, orks.Extras);
            Assert.Equal(GameplayTuning.Load(dir).Config.Hash(), (orks with { Extras = ContentExtras.None }).Hash());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AnUnusableBodyIsRefused()
    {
        var six = Six();
        var all = OrkClass.Append(six, Data, Parity with { Body = Parity.Body with { HealthMax = 0f } }, []);
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), all.Weapons, all.Loadouts, all.Thrown, all.JumpPacks, all.Evades, null, all.Perks, all.Bodies));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), all.Weapons, all.Loadouts, all.Thrown, all.JumpPacks, all.Evades, null, all.Perks, []));   // Body index out of range
    }

    [Fact]
    public void WithoutTheTacticalThereIsNoReference()
    {
        var six = Six() with { Loadouts = [Six().Loadouts[1]], Classes = [Six().Classes[1]] };
        var notes = new List<string>();
        Assert.Null(OrkClass.ReferenceOf(six, Marine, notes));
        Assert.Contains(notes, n => n.Contains("no Ork"));
        Assert.Same(six, OrkClass.Append(six, Data, Parity, notes));
    }

    // ---- raw readers ----

    [Fact]
    public void GunDataReadsTheRawFields()
    {
        var doc = BafDocument.Read(SyntheticBafWriter.RangedWeapon(2f, 0.11f, 40, 0, 2f, 100f, [], 1f, 1f, 1f, 1f));
        var spec = WeaponTuning.Read(OrkClass.Shoota, doc, [])!;
        var g = OrkClass.GunData(spec, doc, 0.2f);
        Assert.Equal((0.11f, 40, 0, 2f, 0.2f), (g.ShotCooldown, g.Clip, g.Reserve, g.Reload, g.RaiseDelay));
        Assert.False(g.ReserveInfinite);
        Assert.Null(OrkClass.GunData(spec, doc, float.NaN).RaiseDelay);
    }

    [Fact]
    public void BodyDataReadsTheBlueprintAndNullWithout()
    {
        var b = OrkClass.BodyData(BafDocument.Read(SyntheticBafWriter.PlayerBlueprint(6.5f, 6.5f, 6.5f, 10f, 3.6f, 0.125f, 40f, aimRun: 6f, aimStrafe: 4f, aimBack: 4f)));
        Assert.Equal((100f, 5f, 5f, 100f, 7f, 33f), (b.Health, b.HealthRegenDelay, b.HealthRegenRate, b.Shield, b.ShieldRegenDelay, b.ShieldRegenRate));
        Assert.Equal((6.5f, 6.5f, 6.5f, 10f, 3.6f, 6f, 4f, 4f), (b.Run, b.Strafe, b.Back, b.Sprint, b.Walk, b.AimRun, b.AimStrafe, b.AimBack));
        var none = OrkClass.BodyData(null);
        Assert.Null(none.Health);
        Assert.Null(none.Shield);
        // Without any data, parity is the marine's body.
        var build = OrkBalance.Apply(Data with { Body = none }, Reference, OrkBalanceMode.Parity, Dt);
        Assert.Equal(Marine with { Name = build.Body.Name, HitVolumes = build.Body.HitVolumes }, build.Body);
    }
}
