using System.Globalization;
using SmContent;
using SmContent.Extras.Musou;

namespace SmTool;

/// <summary>
/// <c>smtool dreadnought-dump [&lt;game-dir|auto&gt;]</c> (Musou Dreadnought D0, docs/musou-dreadnought-design.md): every number of the loyalist Dreadnought's body, weapons, arm strikes, areas, sprint, buff and
/// entrance as <see cref="DreadnoughtData"/> reads them from the install, each with the entry it comes from. Read-only; nothing is cached or written.
/// </summary>
static class DreadnoughtDumpCommand
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(string[] args)
    {
        string gameDir = args.Length > 0 && !args[0].StartsWith("--") && args[0] != "auto" ? args[0] : GameInstall.Locate();
        var notes = new List<string>();
        var d = DreadnoughtData.Read(gameDir, notes);
        string bp = $"ebps.oppc:{DreadnoughtData.Blueprint}";
        void Row(string name, object? value, string source) => Console.WriteLine($"  {name,-34} {Format(value),-14} {source}");

        Console.WriteLine("== body");
        var b = d.Body;
        Row("health_max", b.HealthMax, bp + " health_ext");
        Row("health_regen_delay (s)", b.HealthRegenDelay, bp + " health_ext");
        Row("health_regen_rate (/s)", b.HealthRegenRate, bp + " health_ext");
        Row("armourshield_max", b.ShieldMax, bp + " health_ext/armourshield_info");
        Row("take_damage melee/ranged/expl/world", $"{Format(b.ReceivedMelee)}/{Format(b.ReceivedRanged)}/{Format(b.ReceivedExplosive)}/{Format(b.ReceivedWorld)}", bp + " health_ext/take_damage_multiplier");
        Row("death_time (s)", b.DeathTime, bp + " health_ext");
        Row("speed standing run/strafe/back", $"{Format(b.RunSpeed)}/{Format(b.StrafeSpeed)}/{Format(b.BackSpeed)}", bp + " biped_locomotion_ext/speed_standing");
        Row("speed standing sprint/walk", $"{Format(b.SprintSpeed)}/{Format(b.WalkSpeed)}", bp + " biped_locomotion_ext/speed_standing");
        Row("speed aiming run/strafe/back", $"{Format(b.AimRunSpeed)}/{Format(b.AimStrafeSpeed)}/{Format(b.AimBackSpeed)}", bp + " biped_locomotion_ext/speed_aiming");
        Row("speed crouching run/strafe/back", $"{Format(b.CrouchRunSpeed)}/{Format(b.CrouchStrafeSpeed)}/{Format(b.CrouchBackSpeed)}", bp + " biped_locomotion_ext/speed_crouching");
        Row("rotation_max (deg/s)", b.RotationMaxDegreesPerSecond, bp + " biped_locomotion_ext");
        Row("ground_time_till_max_speed (s)", b.GroundTimeTillMaxSpeed, bp + " biped_locomotion_ext");
        Row("max_walkable_slope (deg)", b.MaxWalkableSlope, bp + " biped_locomotion_ext");
        Row("aim_horizontal/vertical (deg/s)", $"{Format(b.AimHorizontalSpeed)}/{Format(b.AimVerticalSpeed)}", bp + " weapon_ext");
        Row("hit_phantom radius/length (m)", $"{Format(b.HitPhantomRadius)}/{Format(b.HitPhantomLength)}", bp + " animated_character_ext/animation");
        Row("unit_size", b.UnitSize, bp + " entity_ext");

        Console.WriteLine("== entrance");
        Row("invulnerable (s)", d.Entrance.InvulnerableSeconds, bp + " evt_spawn_in/apply_modifiers lifetime");
        Row("received damage multiplier", d.Entrance.ReceivedMultiplier, bp + " evt_spawn_in/apply_modifiers");
        Row($"{DreadnoughtData.EntranceClip} (s)", d.Entrance.ClipSeconds, DreadnoughtData.RigEntry);
        Row("control point heal", d.Entrance.ControlPointHeal, bp + " evt_dread_cap/do_heal");

        Gun("assault cannon", DreadnoughtData.AssaultCannon, d.Cannon);
        Gun("melta", DreadnoughtData.MeltaGun, d.Melta);
        if (d.MeltaExtras is { } m)
        {
            Row("coop damage / armour", $"{Format(m.CoopDamage)}/{Format(m.CoopArmourDamage)}", "wargear.oppc:dread_melta actions_by_upgrade is_in_coop");
            Row("shot_cooldown in fury_aim_mode (s)", m.FuryShotCooldown, "wargear.oppc:dread_melta basic_weapon_tuning");
        }

        Console.WriteLine($"== arm ({DreadnoughtData.Arm})");
        foreach (var s in d.Strikes)
        {
            string at = $"wargear.oppc:{DreadnoughtData.Arm} {s.Ability}";
            Console.WriteLine($"  {s.Ability,-17} range {Format(s.Range)} m, clip {s.Clip} {Format(s.ClipSeconds)} s");
            Hit("primary", s.Primary, at);
            Hit("secondary", s.Secondary, at);
            if (s.Tags.Count > 0) Console.WriteLine("      tags: " + string.Join(", ", s.Tags.Select(t => $"{t.Kind}:{t.Name ?? "-"}@{t.Seconds.ToString("0.000", Inv)}")));
        }
        Console.WriteLine("== areas (centre damage / armour / stun damage, area radius, debris-force radius)");
        foreach (var a in d.Areas)
            Console.WriteLine($"  {a.Action,-20} {Format(a.Damage)} / {Format(a.ArmourDamage)} / {Format(a.StunDamage)}  area {Format(a.Radius)} m  debris {Format(a.DebrisRadius)} m");

        Console.WriteLine("== sprint");
        if (d.Sprint is { } sp)
        {
            string at = $"wargear.oppc:{DreadnoughtData.SprintAbility}";
            Row("max time (s)", sp.MaxSeconds, at); Row("gap (s)", sp.GapSeconds, at); Row("max turn rate (deg/s)", sp.MaxTurnRate, at); Row("wall hit angle (deg)", sp.WallHitAngle, at);
        }
        Console.WriteLine("== buff");
        if (d.Buff is { } bf)
        {
            string at = $"wargear.oppc:{DreadnoughtData.BuffGear}";
            Row("duration (s)", bf.Seconds, at); Row("cooldown (s)", bf.CooldownSeconds, at); Row("regen multiplier", bf.RegenMultiplier, at);
            Row("upgrades", string.Join(" + ", bf.Upgrades), at); Row("clip / reserve", $"{Format(bf.Clip)}/{Format(bf.Reserve)}", at);
        }
        foreach (var n in d.Notes) Console.WriteLine("note: " + n);
        return 0;

        void Gun(string label, string file, DreadnoughtGun? g)
        {
            Console.WriteLine($"== {label} ({file})");
            if (g is null) { Console.WriteLine("  (not found)"); return; }
            string at = $"wargear.oppc:{file}";
            Row("damage / armour damage", $"{Format(g.Damage)}/{Format(g.ArmourDamage)}", at);
            Row("shot_cooldown / time_between (s)", $"{Format(g.ShotCooldown)}/{Format(g.TimeBetweenShots)}", at);
            Row("range (m)", g.Range, at);
            Row("clip / reserve (infinite)", $"{Format(g.ClipSize)}/{Format(g.Reserve)} ({Format(g.ReserveInfinite)})", at);
            Row("low ammunition", g.LowAmmo, at);
            Row("recharge delay (s) / per second", $"{Format(g.RechargeDelay)}/{Format(g.RechargePerSecond)}", at);
            Row("reload / auto reload (s)", $"{Format(g.ReloadTime)}/{Format(g.AutoReloadTime)}", at);
            if (g.HeatPerShot is not null || g.HeatName is not null) Row("heat per shot", $"{Format(g.HeatPerShot)} {g.HeatName}", at);
        }

        void Hit(string label, DreadnoughtHit? h, string source)
        {
            if (h is not null) Console.WriteLine($"      {label,-10} damage {Format(h.Damage)}, armour {Format(h.ArmourDamage)}, stun damage {Format(h.StunDamage)}   {source}");
        }
    }

    static string Format(object? v) => v switch
    {
        null => "-",
        float f => f.ToString("0.###", Inv),
        double f => f.ToString("0.###", Inv),
        bool x => x ? "True" : "False",
        _ => v.ToString() ?? "-",
    };
}
