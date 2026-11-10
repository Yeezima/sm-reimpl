using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>Musou melee feel (owner, 2026-11-08): the cleave reaches 1.5x as far, the slash is red for chain weapons and the knife and blue for power weapons, the own-hit shake is small and capped.</summary>
public class MusouMeleeFeelTests
{
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 1f, 10f, 3.5f);

    static World War(float scale)
    {
        var rifle = new WeaponSpec("sm_mp_ranged_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.12f, 30, 2000, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
        var c = new ClassLoadout("sm - tactical", 0, -1, [Light]) { PrimaryChoices = [0], EquipmentChoices = [0], Side = Team.SpaceMarines, Role = "tactical" };
        var rules = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })], Combat = MusouCombatRules.Default with { CleaveRadiusScale = scale } };
        var config = MusouSimTests.Base with { Weapons = [rifle], Classes = [c, c with { Name = "csm - tactical", Side = Team.Chaos }], Extras = ContentExtras.Musou, MusouRules = rules, RespawnTime = 60f };
        var map = TfwTestArena.Build();
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        w.Spawn(1, 0);
        Tick(w);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick(w);
        w.TfwRespawnAt(1, new Vector3(0f, -60f, 0f), 0f);
        Tick(w);
        return w;
    }

    static void Tick(World w, InputButtons buttons = InputButtons.None) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, buttons));

    /// <summary>Whether a swing reaches an Ork <paramref name="metres"/> straight ahead (it falls when struck).</summary>
    static bool Reaches(float scale, float metres)
    {
        var w = War(scale);
        w.Musou!.Frozen = true;
        ushort id = w.Musou.Spawn(MusouEnemyType.Boy, w.Players[1].Position + new Vector3(0f, metres, 0f), 0f, 1000f);
        for (int t = 0; t < 30; t++) Tick(w, t < 3 ? InputButtons.Melee : InputButtons.None);
        Assert.True(w.Musou.TryGet(id, out var e));
        return e.State == MusouEnemyState.Knocked;
    }

    [Fact]
    public void The_cleave_reach_is_one_and_a_half_times_the_old_reach()
    {
        var c = MusouCombatRules.Default;
        Assert.Equal(1.5f, c.CleaveRadiusScale);
        Assert.Equal((2.0f + c.CleaveExtraRange) * 1.5f, c.CleaveRange(2.0f), 4);
        Assert.Equal((2.0f + c.CleaveExtraRange + MusouCombatRules.CleaveTrackRange) * 1.5f, c.CleaveRange(2.0f, true), 4);
        Assert.Equal(2.0f + c.CleaveExtraRange, (c with { CleaveRadiusScale = 1f }).CleaveRange(2.0f), 4);
    }

    [Fact]
    public void A_swing_now_strikes_an_Ork_past_the_old_reach_and_the_old_scale_does_not()
    {
        float oldReach = War(1f).Config.MeleeRange + MusouCombatRules.Default.CleaveExtraRange;
        float d = oldReach * 1.1f;   // beyond the scale-1 reach (the war volume is x1.3 since 2026-11-09, and x0.6 since 2026-11-11: 0.78 of it), inside the 1.5 scale (1.17 of it)
        Assert.False(Reaches(1f, d));
        Assert.True(Reaches(1.5f, d));
    }

    [Theory]
    [InlineData("sm_mp_melee_chainsword", MusouSlashColour.Red)]
    [InlineData("sm_mp_melee_chainsword_gold", MusouSlashColour.Red)]
    [InlineData("csm_mp_melee_chainsword", MusouSlashColour.Red)]
    [InlineData("csm_mp_melee_chain_axe", MusouSlashColour.Red)]
    [InlineData("sm_mp_melee_combat_knife", MusouSlashColour.Red)]
    [InlineData("csm_mp_melee_combat_knife", MusouSlashColour.Red)]
    [InlineData("sm_mp_melee_powersword", MusouSlashColour.Blue)]
    [InlineData("csm_mp_melee_powersword", MusouSlashColour.Blue)]
    [InlineData("sm_mp_melee_power_axe", MusouSlashColour.Blue)]
    [InlineData("sm_mp_melee_2hand_thunder_hammer", MusouSlashColour.Blue)]
    [InlineData("csm_mp_melee_2hand_power_mace", MusouSlashColour.Blue)]
    [InlineData(null, MusouSlashColour.Red)]
    public void The_slash_is_red_for_chain_weapons_and_the_knife_and_blue_for_power_weapons(string? wargear, MusouSlashColour colour) =>
        Assert.Equal(colour, MusouSlashRules.ColourOf(wargear));

    /// <summary>The owner, 2026-11-10, Musou only: "make it so that blue and red slashes don't appear beyond 30m" (was 60 m).</summary>
    [Fact]
    public void No_red_or_blue_slash_is_drawn_beyond_30_metres() => Assert.Equal(30f, MusouSlashRules.Metres);

    [Fact]
    public void The_own_hit_shake_is_small_and_a_swing_of_many_hits_is_capped()
    {
        float sum = 0f;
        for (int hit = 0; hit < 20; hit++) sum += MusouSlashRules.Shake(hit, sum, 1f);
        Assert.Equal(MusouSlashRules.ShakeCap, sum, 5);
        Assert.True(MusouSlashRules.ShakeCap < 0.03f);   // well under the pound's 0.03 rad
        Assert.Equal(MusouSlashRules.ShakeFirst, MusouSlashRules.Shake(0, 0f, 1f));
        Assert.Equal(0f, MusouSlashRules.Shake(0, 0f, 0f));   // --musou-melee-shake 0
    }
}
