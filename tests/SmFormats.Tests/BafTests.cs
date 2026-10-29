using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim;

namespace SmFormats.Tests;

public class BafTests
{
    static byte[] Marine() => SyntheticBafWriter.PlayerBlueprint(6.5f, 6f, 4.5f, 10f, 3.6f, 0.2f, 100f);
    static byte[] Bolter() => SyntheticBafWriter.RangedWeapon(22f, 0.133f, 30, 270, 1.3f, 250f,
        [(70f, 0.8f), (0f, 1f), (45f, 1f)], head: 2.55f, body: 1f, shieldHead: 0.5f, shieldBody: 0.505f);

    [Fact]
    public void Reads_tables_types_and_paths()
    {
        var doc = BafDocument.Read(Marine());
        Assert.Equal(100u, doc.Version);
        var standing = doc.Root.Table("extensions/biped_locomotion_ext/speed_standing");
        Assert.NotNull(standing);
        Assert.Equal(6.5f, standing!.Value.Float("run_speed"));
        Assert.Equal("_base_templates\\speed_tuning", standing.Value.String("$ref"));
        var locomotion = doc.Root.Table("extensions/biped_locomotion_ext")!.Value;
        Assert.False(locomotion["left_stick_turns_camera"]!.Value.AsBool());
        Assert.Equal(3, doc.Root.Table("extensions/health_ext")!.Value["level"]!.Value.Raw);
        Assert.Equal(BafType.Int, doc.Root["max_recycle_pool_size"]!.Value.Type);
        Assert.Null(doc.Root.Table("extensions/missing"));
    }

    [Fact]
    public void Empty_tables_use_minus_one_as_first_entry()
    {
        var doc = BafDocument.Read(SyntheticBafWriter.Build(("marker_target", Array.Empty<(string, object)>()), ("x", 1f)));
        Assert.Empty(doc.Root.Table("marker_target")!.Value.Entries);
        Assert.Equal(1f, doc.Root.Float("x"));
    }

    [Fact]
    public void Corrupt_files_are_rejected()
    {
        var bytes = Marine();
        Assert.Throws<InvalidDataException>(() => BafDocument.Read(bytes.AsSpan(0, 20)));
        var badType = (byte[])bytes.Clone();
        int entriesAt = BitConverter.ToInt32(badType, 25);
        BitConverter.GetBytes(9).CopyTo(badType, entriesAt);
        Assert.Throws<InvalidDataException>(() => BafDocument.Read(badType));
        var badTable = (byte[])bytes.Clone();
        BitConverter.GetBytes(1_000_000).CopyTo(badTable, BitConverter.ToInt32(bytes, 17) + 4);   // root table's first entry
        Assert.Throws<InvalidDataException>(() => BafDocument.Read(badTable));
        for (int len = 0; len < bytes.Length; len += 5)
            Assert.Throws<InvalidDataException>(() => BafDocument.Read(bytes.AsSpan(0, len)));
    }

    [Fact]
    public void A_table_referenced_twice_is_rejected()
    {
        var bytes = Marine();
        int entriesAt = BitConverter.ToInt32(bytes, 25);
        // Point the root's second entry (an int) at table 1, which "extensions" already owns.
        BitConverter.GetBytes(4).CopyTo(bytes, entriesAt + 12);
        BitConverter.GetBytes(1).CopyTo(bytes, entriesAt + 12 + 8);
        Assert.Throws<InvalidDataException>(() => BafDocument.Read(bytes));
    }

    [Fact]
    public void Tuning_maps_the_player_blueprint_onto_the_simulation_config()
    {
        var notes = new List<string>();
        var config = GameplayTuning.FromBlueprint(BafDocument.Read(Marine()), notes, out var placeholders);
        Assert.Equal((6.5f, 6f, 4.5f, 10f, 3.6f), (config.RunSpeed, config.StrafeSpeed, config.BackSpeed, config.SprintSpeed, config.WalkSpeed));
        Assert.Equal((0.2f, 100f, 60f), (config.GroundTimeToMaxSpeed, config.MaxAcceleration, config.MaxWalkableSlope));
        Assert.Equal((100f, 5f, 5f), (config.HealthMax, config.HealthRegenDelay, config.HealthRegenRate));
        Assert.Equal((100f, 7f, 33f), (config.ShieldMax, config.ShieldRegenDelay, config.ShieldRegenRate));   // default entry, not the 150 upgrade
        Assert.Equal((3f, 3f, 2f), (config.AimRunSpeed, config.AimStrafeSpeed, config.AimBackSpeed));   // speed_aiming (protocol 12)
        Assert.Equal(["TickRate", "Gravity", "RespawnTime", "ShotConeDegrees", "EyeHeight", "HitVolumes", "MaxRewindTicks", "CapsuleRadius", "CapsuleHeight", "StepHeight"], placeholders);
        Assert.Empty(notes);
    }

    [Fact]
    public void Tuning_maps_the_weapon_default_entries_onto_the_simulation_config()
    {
        var notes = new List<string>();
        var placeholders = new List<string>();
        var c = GameplayTuning.WithWeapon(new SimConfig(), BafDocument.Read(Bolter()), notes, placeholders);
        Assert.Equal((22f, 0.133f, 30, 270, 1.3f, 250f), (c.Damage, c.ShotCooldown, c.ClipSize, c.ReserveSize, c.ReloadTime, c.MaxRange));
        Assert.Equal((45f, 70f, 0.8f), (c.FallOffStart, c.FallOffEnd, c.FallOffEndMultiplier));
        Assert.Equal((2.55f, 1f, 0.5f, 0.505f), (c.HeadMultiplier, c.BodyMultiplier, c.ShieldHeadMultiplier, c.ShieldBodyMultiplier));
        Assert.Empty(placeholders);
        Assert.Empty(notes);
    }

    [Fact]
    public void Fall_off_shapes_other_than_the_bolter_are_mapped_or_reported()
    {
        static SimConfig With((float, float)[] points, List<string> notes, List<string> placeholders) => GameplayTuning.WithWeapon(new SimConfig(),
            BafDocument.Read(SyntheticBafWriter.RangedWeapon(22f, 0.1f, 30, 270, 1f, 250f, points, 2f, 1f, 0.5f, 0.5f)), notes, placeholders);
        List<string> notes = [], placeholders = [];
        var flat = With([(0f, 1f), (100f, 1f)], notes, placeholders);
        Assert.Equal(1f, Combat.FallOff(200f, flat));
        Assert.Empty(placeholders);
        With([(0f, 0.9f), (50f, 0.5f)], notes, placeholders);
        Assert.Contains("FallOff", placeholders);
        With([(0f, 1f), (10f, 0.5f), (20f, 0.15f)], notes, placeholders = []);   // melta-like: the middle point is dropped
        Assert.Contains(notes, n => n.Contains("fall-off"));
        Assert.Empty(placeholders);
    }

    [Fact]
    public void Zero_multipliers_are_values_not_placeholders()
    {
        var placeholders = new List<string>();
        var c = GameplayTuning.WithWeapon(new SimConfig(), BafDocument.Read(SyntheticBafWriter.RangedWeapon(22f, 0.1f, 30, 270, 1f, 250f,
            [(0f, 1f), (45f, 1f), (70f, 0.8f)], head: 2f, body: 1f, shieldHead: 0f, shieldBody: 0f)), [], placeholders);
        Assert.Equal((0f, 0f), (c.ShieldHeadMultiplier, c.ShieldBodyMultiplier));
        Assert.Empty(placeholders);
    }

    [Fact]
    public void Missing_weapon_values_keep_placeholders()
    {
        var placeholders = new List<string>();
        var c = GameplayTuning.WithWeapon(new SimConfig(), BafDocument.Read(SyntheticBafWriter.Build(("ability_data", 1))), [], placeholders);
        Assert.Equal(new SimConfig().Damage, c.Damage);
        Assert.Contains("Damage", placeholders);
        Assert.Contains("ClipSize", placeholders);
        Assert.Contains("FallOff", placeholders);
    }

    [Fact]
    public void Tuning_loads_from_an_install_and_cross_checks_the_chaos_side()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-tuning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "data", "packages"));
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "data", "packages", "ebps.oppc"), SyntheticObpkWriter.Build(
            [
                new("attr_pc", GameplayTuning.MarineBlueprint, Marine()),
                new("attr_pc", GameplayTuning.ChaosBlueprint, SyntheticBafWriter.PlayerBlueprint(7f, 6f, 4.5f, 10f, 3.6f, 0.2f, 100f)),
            ]));
            var tuning = GameplayTuning.Load(dir);
            // feel/buffer-speed: the owner's movement × 0.9 and the input buffer apply by default; ownerFeel: false keeps the data's speeds.
            Assert.Equal(6.5f * MovementScale.OwnerFactor, tuning.Config.RunSpeed);
            Assert.Equal((MovementScale.OwnerFactor, GameplayTuning.OwnerInputBuffer), (tuning.Config.MoveSpeedScale, tuning.Config.InputBufferWindow));
            var data = GameplayTuning.Load(dir, ownerFeel: false).Config;
            Assert.Equal((6.5f, 1f, 0f), (data.RunSpeed, data.MoveSpeedScale, data.InputBufferWindow));
            // feel/locks-sprint-stop: the lock scale and the sprint stop (no rig here: the stop clip's placeholder time); off with ownerFeel: false.
            Assert.Equal((GameplayTuning.OwnerLockScale, SprintStopTuning.FallbackTime), (tuning.Config.ActionLockScale, tuning.Config.SprintStopTime));
            Assert.Equal((1f, 0f), (data.ActionLockScale, data.SprintStopTime));
            Assert.NotEqual(data.Hash(), tuning.Config.Hash());
            Assert.Contains(tuning.Notes, n => n.Contains("differ"));
            Assert.Contains(tuning.Notes, n => n.Contains(GameplayTuning.Bolter));   // no wargear package: weapon placeholders

            File.WriteAllBytes(Path.Combine(dir, "data", "packages", "wargear.oppc"), SyntheticObpkWriter.Build([new("attr_pc", GameplayTuning.Bolter, Bolter())]));
            tuning = GameplayTuning.Load(dir);
            Assert.Equal((22f, 0.133f, 1.3f), (tuning.Config.Damage, tuning.Config.ShotCooldown, tuning.Config.ReloadTime));
            Assert.DoesNotContain(tuning.Notes, n => n.Contains(GameplayTuning.Bolter));
            Assert.DoesNotContain("Damage", tuning.Placeholders);

            // Identical blueprints with a weapon loaded: no false "locomotion differ" note.
            File.WriteAllBytes(Path.Combine(dir, "data", "packages", "ebps.oppc"), SyntheticObpkWriter.Build(
                [new("attr_pc", GameplayTuning.MarineBlueprint, Marine()), new("attr_pc", GameplayTuning.ChaosBlueprint, Marine())]));
            Assert.DoesNotContain(GameplayTuning.Load(dir).Notes, n => n.Contains("differ"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Movement_uses_directional_speeds()
    {
        var c = GameplayTuning.FromBlueprint(BafDocument.Read(Marine()), [], out _);
        float TopSpeed(float x, float y, InputButtons buttons)
        {
            var s = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0, true);
            for (int i = 0; i < 60; i++) s = Movement.Step(s, new PlayerInput((uint)i, x, y, 0, buttons), c);
            return new Vector2(s.Velocity.X, s.Velocity.Y).Length();
        }
        Assert.Equal(6.5f, TopSpeed(0, 1, InputButtons.None), 3);
        Assert.Equal(10f, TopSpeed(0, 1, InputButtons.Sprint), 3);
        Assert.Equal(6f, TopSpeed(1, 0, InputButtons.None), 3);
        Assert.Equal(4.5f, TopSpeed(0, -1, InputButtons.None), 3);

        // Full speed from standing in GroundTimeToMaxSpeed (0.2 s = 6 ticks), not sooner.
        var s = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0, true);
        for (int i = 0; i < 5; i++) s = Movement.Step(s, new PlayerInput((uint)i, 0, 1, 0, InputButtons.None), c);
        Assert.True(s.Velocity.Length() < 6.5f);
        s = Movement.Step(s, new PlayerInput(5, 0, 1, 0, InputButtons.None), c);
        Assert.Equal(6.5f, s.Velocity.Length(), 3);
    }

    [Fact]
    public void Stopping_takes_the_ground_time_and_ends_at_rest()
    {
        var c = GameplayTuning.FromBlueprint(BafDocument.Read(Marine()), [], out _);
        var s = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0, true);
        for (int i = 0; i < 30; i++) s = Movement.Step(s, new PlayerInput((uint)i, 0, 1, 0, InputButtons.None), c);
        for (int i = 0; i < 5; i++) s = Movement.Step(s, new PlayerInput((uint)(30 + i), 0, 0, 0, InputButtons.None), c);
        Assert.True(s.Velocity.Length() > 0.5f, "not stopped before the ground time");
        s = Movement.Step(s, new PlayerInput(35, 0, 0, 0, InputButtons.None), c);
        Assert.Equal(Vector3.Zero, s.Velocity);
    }

    [Fact]
    public void Reversing_and_diagonals_stay_within_the_directional_speeds()
    {
        var c = GameplayTuning.FromBlueprint(BafDocument.Read(Marine()), [], out _);
        var s = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0, true);
        for (int i = 0; i < 30; i++) s = Movement.Step(s, new PlayerInput((uint)i, 0, 1, 0, InputButtons.Sprint), c);
        int ticks = 0;
        while (s.Velocity.Y > -4.5f + 1e-3f && ticks < 100) { s = Movement.Step(s, new PlayerInput((uint)(30 + ticks), 0, -1, 0, InputButtons.None), c); ticks++; }
        Assert.InRange(ticks, 6, 15);   // from +10 to −4.5 at the transition rate
        foreach (var (x, y) in new[] { (0.7f, 0.7f), (-1f, 1f), (1f, -1f), (0.3f, -0.9f) })
        {
            var d = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0.4f, true);
            for (int i = 0; i < 60; i++)
            {
                d = Movement.Step(d, new PlayerInput((uint)i, x, y, 0.4f, InputButtons.Sprint), c);
                Assert.True(d.Velocity.Length() <= c.SprintSpeed + 1e-3f);
            }
        }
    }

    [Fact]
    public void Missing_blueprint_package_or_entry_is_reported()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-tuning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "data", "packages"));
        try
        {
            Assert.ThrowsAny<IOException>(() => GameplayTuning.Load(dir));
            File.WriteAllBytes(Path.Combine(dir, "data", "packages", "ebps.oppc"), SyntheticObpkWriter.Build([new("attr_pc", "other", Marine())]));
            var ex = Assert.Throws<FileNotFoundException>(() => GameplayTuning.Load(dir));
            Assert.Contains(GameplayTuning.MarineBlueprint, ex.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Zero_timing_values_fall_back_to_placeholders()
    {
        var notes = new List<string>();
        var c = GameplayTuning.FromBlueprint(BafDocument.Read(SyntheticBafWriter.PlayerBlueprint(6.5f, 6f, 4.5f, 10f, 3.6f, 0f, 0f)), notes, out var placeholders);
        Assert.Contains("GroundTimeToMaxSpeed", placeholders);
        Assert.Contains("MaxAcceleration", placeholders);
        Assert.True(c.MaxAcceleration > 0 && c.GroundTimeToMaxSpeed > 0);
    }

    [Fact]
    public void Jumping_is_off_unless_configured()
    {
        var s = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0, true);
        s = Movement.Step(s, new PlayerInput(1, 0, 0, 0, InputButtons.Jump), new SimConfig());
        Assert.True(s.Grounded);
        Assert.Equal(0f, s.Position.Z);
    }
}
