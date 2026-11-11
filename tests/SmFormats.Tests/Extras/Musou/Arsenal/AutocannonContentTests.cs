using SmContent;
using SmContent.Extras.Musou.Arsenal;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmFormats.Tests;

/// <summary>
/// W1, the Musou autocannon's content side (docs/new-weapons-design.md section 4 and the customizer's config, section 11): the twin is appended to the Space Marine Devastator's primaries only, a Join
/// resolves its name to that index in Musou's configuration (the data's configuration has no such name, which is why the customizer needs Musou's own config), and the data's configuration, the
/// faithful profile's and the Orks' never gain it.
/// </summary>
public class AutocannonContentTests
{
    static WeaponSpec Gun(string name, WeaponSlot slot = WeaponSlot.Ranged) => new(name, slot, TriggerKind.FullAuto, 35f, 0.2f, 100, 0, 1f, 100f, 40f, 60f, 0.8f, 1f, 1f, 1f, 1f);

    static SimConfig Data() => new()
    {
        Weapons = [Gun("sm_mp_exotic_heavy_bolter"), Gun("sm_mp_exotic_las_cannon"), Gun("csm_mp_exotic_heavy_bolter"), Gun("sm_mp_ranged_bolter")],
        Classes =
        [
            new ClassLoadout("sm - devastator", 0, -1, []) { Side = Team.SpaceMarines, Role = "heavy", PrimaryChoices = [0, 1] },
            new ClassLoadout("csm - havoc", 2, -1, []) { Side = Team.Chaos, Role = "heavy", PrimaryChoices = [2] },
            new ClassLoadout("sm - tactical", 3, -1, []) { Side = Team.SpaceMarines, Role = "tactical", PrimaryChoices = [3] },
        ],
    };

    static readonly WeaponSpec Source = Gun("csm_mp_exotic_auto_cannon") with { Damage = 50f, ShotCooldown = 0.4f, Trigger = TriggerKind.SemiAuto, BurstLength = 2, BurstInterval = 0.2f };

    [Fact]
    public void A_join_names_the_autocannon_by_its_index_in_Musous_configuration_and_not_at_all_in_the_datas()
    {
        var data = Data();
        var musou = Arsenal.WithAutocannon(data with { Extras = ContentExtras.Musou }, Source, []);
        int devastator = LoadoutIndices.Class(musou, "sm - devastator");
        Assert.Equal(2, LoadoutIndices.Choice(musou, devastator, "primary", "mp_exotic_auto_cannon"));   // the stored (side-neutral) name of the custom class
        Assert.Equal(2, LoadoutIndices.Choice(musou, devastator, "primary", "sm_mp_exotic_auto_cannon"));
        Assert.Equal(0, LoadoutIndices.Choice(musou, devastator, "primary", "mp_exotic_heavy_bolter"));    // no existing index moved
        Assert.Equal(1, LoadoutIndices.Choice(musou, devastator, "primary", "mp_exotic_las_cannon"));
        // The data's configuration (what the customizer used for every page before): the name is unknown, the Join would send the default (-1).
        Assert.Equal(-1, LoadoutIndices.Choice(data, LoadoutIndices.Class(data, "sm - devastator"), "primary", "mp_exotic_auto_cannon"));
        // Not the Chaos Havoc's, not the Tactical's.
        Assert.Equal(-1, LoadoutIndices.Choice(musou, LoadoutIndices.Class(musou, "csm - havoc"), "primary", "mp_exotic_auto_cannon"));
        Assert.Equal(-1, LoadoutIndices.Choice(musou, LoadoutIndices.Class(musou, "sm - tactical"), "primary", "mp_exotic_auto_cannon"));
    }

    [Fact]
    public void The_autocannons_sounds_are_the_Stalker_Bolters_and_its_effects_stay_the_twins()
    {
        // The owner (2026-11-10): "make the autocannon use stalker bolter sounds". Sounds: the Stalker's files (the hit confirm rule keys on their names); effects, reticle and ammo box: the twin's (W0b).
        Assert.Equal(["sm_mp_ranged_sniper_rifle", "csm_mp_ranged_sniper_rifle"], Arsenal.SoundSources(ArsenalRules.Autocannon));
        Assert.All(Arsenal.SoundSources(ArsenalRules.Autocannon)!, f => Assert.True(StalkerHitConfirm.Is(f)));
        Assert.Equal([Arsenal.AutocannonTwinFile, Arsenal.HeavyBolterFile], Arsenal.PresentationSources(ArsenalRules.Autocannon));
        Assert.Null(Arsenal.SoundSources(ArsenalRules.LightningClaw));   // the others' sounds are their effects' sources
        Assert.Null(Arsenal.SoundSources("sm_mp_exotic_heavy_bolter"));
    }

    [Fact]
    public void The_autocannon_shows_the_loyalist_muzzle_flash_and_tracer_in_place_of_the_twins_purple_ones()
    {
        // The owner (2026-11-10, after W1b's clip): "Loyalist effects": the twin's chaos_autocannon_muzzle / chaos_autocannon_tracer become the effects package's autocannon_muzzle / heavy_bolter_tracer.
        var names = Arsenal.EffectNames(ArsenalRules.Autocannon.ToUpperInvariant())!;
        Assert.Equal(3, names.Count);
        Assert.Equal("autocannon_muzzle", names["chaos_autocannon_muzzle"]);
        Assert.Equal("heavy_bolter_tracer", names["CHAOS_AUTOCANNON_TRACER"]);
        // And "Loyalist impact" (the owner, the same day): the twin container's world hit becomes the Space Marine autocannon container's (in the autocannon's own copy of the container).
        Assert.Equal("autocannon_dmg", names["chaos_autocannon_dmg"]);
        Assert.All(names.Values, to => Assert.DoesNotContain("chaos", to, StringComparison.OrdinalIgnoreCase));
        // The twin stays the effects' source (its markers, scales and impacts), the Stalker the sounds' (reticle and ammo box: ArsenalPresentationTests).
        Assert.Equal([Arsenal.AutocannonTwinFile, Arsenal.HeavyBolterFile], Arsenal.PresentationSources(ArsenalRules.Autocannon));
        Assert.Equal(["sm_mp_ranged_sniper_rifle", "csm_mp_ranged_sniper_rifle"], Arsenal.SoundSources(ArsenalRules.Autocannon));
        foreach (var other in ArsenalRules.Names.Where(n => n != ArsenalRules.Autocannon).Concat([Arsenal.AutocannonTwinFile, Arsenal.HeavyBolterFile, "sm_mp_ranged_bolter"]))
            Assert.Null(Arsenal.EffectNames(other));   // nothing else is renamed, the install's names least of all
    }

    [Fact]
    public void The_barrel_is_shortened_to_1_6_m_ahead_of_the_receiver_and_nothing_behind_it_moves()
    {
        // The owner (2026-11-10): "shorten it to 1.6m": the muzzle's distance from the model's root along its length (W1b's "2.6 m", the install's 2.621 m).
        Assert.Equal(1.6f, AutocannonBarrel.Muzzle);
        var k = AutocannonBarrel.Knots();
        Assert.Equal(1.6f, AutocannonBarrel.Along(AutocannonBarrel.InstallMuzzle, k), 4);
        // The stock, the grip (the chest prop the mounts use, 0.533 m), the drum and the receiver's front stay.
        foreach (float y in new[] { -0.167f, 0f, 0.533f, 0.74f, 1.069f, AutocannonBarrel.ReceiverFront }) Assert.Equal(y, AutocannonBarrel.Along(y, k));
        // The clamp with the rod's end and the ribbed front keep their lengths: the plain tube gives up the metre.
        float clamp = AutocannonBarrel.Along(AutocannonBarrel.ClampEnd, k) - AutocannonBarrel.Along(AutocannonBarrel.ClampStart, k);
        float jacket = AutocannonBarrel.Along(AutocannonBarrel.InstallMuzzle, k) - AutocannonBarrel.Along(AutocannonBarrel.JacketStart, k);
        Assert.Equal(AutocannonBarrel.ClampEnd - AutocannonBarrel.ClampStart, clamp, 4);
        Assert.Equal(AutocannonBarrel.InstallMuzzle - AutocannonBarrel.JacketStart, jacket, 4);
        Assert.InRange(AutocannonBarrel.Along(AutocannonBarrel.ClampStart, k), AutocannonBarrel.ReceiverFront, AutocannonBarrel.ReceiverFront + 0.01f);   // the clamp just past the receiver
        // Increasing everywhere (no part folds over another), and a point past the muzzle moves with it.
        float last = float.NegativeInfinity;
        for (float y = -0.3f; y <= 2.8f; y += 0.001f) { float m = AutocannonBarrel.Along(y, k); Assert.True(m >= last, $"not increasing at {y}"); last = m; }
        Assert.Equal(1.7f, AutocannonBarrel.Along(AutocannonBarrel.InstallMuzzle + 0.1f, k), 4);
        // The install's muzzle is the identity; a muzzle too close for the kept parts squeezes them evenly, never past the receiver.
        var same = AutocannonBarrel.Knots(AutocannonBarrel.InstallMuzzle);
        foreach (float y in new[] { 1.2f, 1.4f, 2f, 2.5f }) Assert.Equal(y, AutocannonBarrel.Along(y, same), 4);
        var tight = AutocannonBarrel.Knots(0.5f);
        Assert.Equal(AutocannonBarrel.Shortest, AutocannonBarrel.Along(AutocannonBarrel.InstallMuzzle, tight), 4);
        Assert.Equal(AutocannonBarrel.Along(AutocannonBarrel.ClampStart, tight), AutocannonBarrel.ReceiverFront, 4);
    }

    [Fact]
    public void The_kick_is_a_small_move_straight_back_that_starts_and_ends_at_rest()
    {
        // Owner 2026-11-11: "only slightly move straight back"; the size and times are ours (provisional approximation).
        Assert.Equal(0f, AutocannonKick.Metres(0f));
        Assert.Equal(0f, AutocannonKick.Metres(-0.1f));
        Assert.Equal(0f, AutocannonKick.Metres(AutocannonKick.TotalSeconds));
        Assert.Equal(0f, AutocannonKick.Metres(float.NaN));
        Assert.Equal(AutocannonKick.PeakMetres, AutocannonKick.Metres(AutocannonKick.RiseSeconds), 5);
        Assert.InRange(AutocannonKick.PeakMetres, 0.01f, 0.08f);   // a few centimetres, not a swing
        Assert.True(AutocannonKick.TotalSeconds < 0.4f);   // gone before the next round (0.4 s)
        for (float age = 0f; age < AutocannonKick.TotalSeconds; age += 0.005f) Assert.InRange(AutocannonKick.Metres(age), 0f, AutocannonKick.PeakMetres + 1e-6f);
    }

    // ---- the player's install (skipped without it; nothing of it is kept)

    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (needs the player's install; never in CI)";
        }
    }

    [InstallFact]
    public void The_barrels_cut_runs_through_plain_tube_only_on_the_install()
    {
        // turret_aa as read: the muzzle where AutocannonBarrel says, and no vertex inside the two stretches the shortening scales (the plain tube on either side of the clamp), so nothing is
        // distorted: every vertex ahead of the receiver is the clamp, the rod's end or the ribbed front, which only move back.
        var model = WeaponModel.Load(Path.Combine(Game.Value!, "data", "packages", "turret_aa.oppc"), "turret_aa", null, []);
        var muzzle = model.Muzzle!;
        Assert.Equal(AutocannonBarrel.InstallMuzzle, muzzle.Position.Y, 2);
        const float slack = 0.002f;
        int ahead = 0;
        foreach (var part in model.Parts.Where(p => p.Visible))
            foreach (var p in model.Model.Mesh.Blocks[part.BlockIndex].Positions.Select(v => System.Numerics.Vector3.Transform(v, part.ToRoot)))
            {
                if (p.Y <= AutocannonBarrel.ReceiverFront + slack) continue;
                ahead++;
                bool clampOrRod = p.Y >= AutocannonBarrel.ClampStart - slack && p.Y <= AutocannonBarrel.ClampEnd + slack;
                bool jacket = p.Y >= AutocannonBarrel.JacketStart - slack && p.Y <= AutocannonBarrel.InstallMuzzle + slack;
                Assert.True(clampOrRod || jacket, $"{part.Node}: a vertex at {p.Y:F4} m lies in the plain tube the shortening scales");
                if (!part.Node.Equals("Recoiling_Barrel", StringComparison.OrdinalIgnoreCase)) Assert.True(clampOrRod, $"{part.Node}: a vertex at {p.Y:F4} m ahead of the receiver that is not the rod's end");
            }
        Assert.True(ahead > 500, $"only {ahead} vertices ahead of the receiver");
    }

    [Fact]
    public void Nothing_but_the_Space_Marine_Devastators_list_and_the_weapon_list_change()
    {
        var data = Data();
        var musou = Arsenal.WithAutocannon(data with { Extras = ContentExtras.Musou }, Source, []);
        Assert.Equal(data.Weapons.Count + 1, musou.Weapons.Count);
        Assert.Equal(data.Weapons, musou.Weapons.Take(data.Weapons.Count));
        Assert.Equal(ArsenalRules.Autocannon, musou.Weapons[^1].Name);
        Assert.Equal([0, 1, data.Weapons.Count], musou.Classes[0].PrimaryChoices);
        Assert.Equal(data.Classes[1].PrimaryChoices, musou.Classes[1].PrimaryChoices);
        Assert.Equal(data.Classes[2].PrimaryChoices, musou.Classes[2].PrimaryChoices);
    }
}
