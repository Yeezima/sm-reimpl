using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The owner's Musou Mode rules of the night batch 2026-11-06: "ammo should be infinite in musou mode"; "grenades should refill every 30 seconds automatically"; "bots in musou mode should retain all
/// player attributes and statistics - except make them take 90% reduced damage". Classic PvP keeps the original's rules.
/// </summary>
public class TfwSupplyTests
{
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static ClassLoadout Class(string name, Team side) => new(name, -1, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], EquipmentChoices = [0], Side = side, Role = "tactical" };

    static readonly SimConfig Musou = TfwRig.Config with
    {
        Weapons = [Rifle, Pistol], Thrown = [GrenadeTests.Frag],
        Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)],
    };
    static readonly SimConfig Classic = Musou with { Extras = ContentExtras.None };
    const int Human = TfwRig.Human;

    static void SetPlayer(World w, int id, PlayerState state)
    {
        var field = typeof(World).GetField("_players", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((Dictionary<int, PlayerState>)field.GetValue(w)!)[id] = state;
    }

    /// <summary>A Classic match: the same weapons and grenade, no added content, in play.</summary>
    static World ClassicMatch()
    {
        var map = TfwTestArena.Build();
        var w = new World(Classic, map.Collision, TfwTestArena.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        w.Spawn(1, 0);
        w.Spawn(2, 1);
        TfwRig.Steps(w, 30);
        Assert.Equal(MatchState.Playing, w.State);
        return w;
    }

    // ---------------------------------------------------------------- endless ammunition

    [Fact]
    public void The_grenade_refill_period_is_the_owners_15_seconds() => Assert.Equal(15f, TfwSupplyRules.GrenadeRefillSeconds);

    [Fact]
    public void Every_weapon_keeps_a_full_reserve_and_the_clip_still_needs_its_reload()
    {
        var w = TfwRig.War(config: Musou);
        SetPlayer(w, Human, w.Players[Human] with { Clip = 0, Reserve = 0, OtherReserve = 0 });
        TfwRig.Steps(w, 1);
        var p = w.Players[Human];
        var hand = Loadouts.InHand(w.Config, p)!;
        Assert.Equal(hand.ReserveSize, p.Reserve);
        if (Loadouts.Weapon(w.Config, p, Loadouts.Other(p.Slot)) is { } other) Assert.Equal(other.ReserveSize, p.OtherReserve);
        Assert.True(p.Clip < hand.ClipSize, $"clip {p.Clip}: the clip is not filled for free");
        for (int k = 0; k < 30 * 5 && w.Players[Human].Clip < hand.ClipSize; k++)
            w.Step(id => new PlayerInput(0, 0f, 0f, w.Players[id].Yaw, id == Human ? InputButtons.Reload : InputButtons.None));
        Assert.Equal(hand.ClipSize, w.Players[Human].Clip);   // the reload fills it from the reserve
        Assert.Equal(hand.ReserveSize, w.Players[Human].Reserve);   // which stays full
    }

    [Fact]
    public void An_ai_marine_keeps_a_full_reserve_in_every_slot()
    {
        var w = TfwRig.War(config: Musou);
        int r = w.TfwSpawnAlly(TfwTestArena.LandingZone)!.Value;
        SetPlayer(w, r, w.Players[r] with { Reserve = 0, OtherReserve = 0 });
        TfwRig.Steps(w, 1);
        var p = w.Players[r];
        Assert.Equal(Loadouts.InHand(w.Config, p)!.ReserveSize, p.Reserve);
        if (Loadouts.Weapon(w.Config, p, Loadouts.Other(p.Slot)) is { } other) Assert.Equal(other.ReserveSize, p.OtherReserve);
    }

    [Fact]
    public void Classic_pvp_still_runs_out_of_ammunition_and_grenades()
    {
        var w = ClassicMatch();
        SetPlayer(w, 1, w.Players[1] with { Reserve = 0, OtherReserve = 0, Grenades = 0 });
        TfwRig.Seconds(w, TfwSupplyRules.GrenadeRefillSeconds + 1f);
        Assert.Equal(0, w.Players[1].Reserve);
        Assert.Equal(0, w.Players[1].OtherReserve);
        Assert.Equal(0, w.Players[1].Grenades);
        Assert.Equal(1f, w.DamageTakenScale(1));
        Assert.Equal(1f, w.DamageTakenScale(TfwContracts.PuppetIdFirst));
    }

    // ---------------------------------------------------------------- the grenade refill

    [Fact]
    public void A_grenade_comes_back_every_30_s_while_short_up_to_the_most_carried()
    {
        var w = TfwRig.War(config: Musou);
        int most = GrenadeTests.Frag.Count;
        SetPlayer(w, Human, w.Players[Human] with { Grenades = most });
        int period = (int)MathF.Round(TfwSupplyRules.GrenadeRefillSeconds * Musou.TickRate);
        TfwRig.Steps(w, period + 5);
        Assert.Equal(most, w.Players[Human].Grenades);
        Assert.Equal(0, w.TfwGrenadeWait(Human));   // no time counts at the most

        SetPlayer(w, Human, w.Players[Human] with { Grenades = 0 });
        TfwRig.Steps(w, period - 1);
        Assert.Equal(0, w.Players[Human].Grenades);
        TfwRig.Steps(w, 1);
        Assert.Equal(1, w.Players[Human].Grenades);
        TfwRig.Steps(w, period);
        Assert.Equal(2, w.Players[Human].Grenades);
        TfwRig.Steps(w, period * 2);
        Assert.Equal(most, w.Players[Human].Grenades);   // never past the most
    }

    [Fact]
    public void The_refill_waits_its_whole_time_again_after_the_count_was_full()
    {
        var w = TfwRig.War(config: Musou);
        int period = (int)MathF.Round(TfwSupplyRules.GrenadeRefillSeconds * Musou.TickRate);
        SetPlayer(w, Human, w.Players[Human] with { Grenades = GrenadeTests.Frag.Count - 1 });
        TfwRig.Steps(w, period / 2);
        SetPlayer(w, Human, w.Players[Human] with { Grenades = GrenadeTests.Frag.Count });   // a pickup filled it half way
        TfwRig.Steps(w, 1);
        SetPlayer(w, Human, w.Players[Human] with { Grenades = GrenadeTests.Frag.Count - 1 });   // one thrown
        TfwRig.Steps(w, period - 1);
        Assert.Equal(GrenadeTests.Frag.Count - 1, w.Players[Human].Grenades);
        TfwRig.Steps(w, 1);
        Assert.Equal(GrenadeTests.Frag.Count, w.Players[Human].Grenades);
    }

    // ---------------------------------------------------------------- the stimulant refill (owner, 2026-11-09: "stimulants need to regenerate the same way grenades do")

    static readonly ThrownSpec StimItem = new("sm_mp_eq_stimpack", 3, -1, 6f, 0f, ThrownSpec.NoProjectile) { Stim = new StimSpec(5f, 0.3f, 0.9f) };
    static SimConfig WithStims(SimConfig c) => c with { Thrown = [StimItem], Classes = [.. c.Classes.Select(k => k with { EquipmentChoices = [0] })] };

    [Fact]
    public void A_stim_charge_comes_back_on_the_grenade_period_up_to_the_most_in_musou()
    {
        var w = TfwRig.War(config: WithStims(Musou));
        Assert.NotNull(Loadouts.Equipment(w.Config, w.Players[Human])!.Stim);
        int period = (int)MathF.Round(TfwSupplyRules.GrenadeRefillSeconds * Musou.TickRate);
        SetPlayer(w, Human, w.Players[Human] with { Grenades = 0 });
        TfwRig.Steps(w, period - 1);
        Assert.Equal(0, w.Players[Human].Grenades);
        TfwRig.Steps(w, 1);
        Assert.Equal(1, w.Players[Human].Grenades);
        TfwRig.Steps(w, period * 5);
        Assert.Equal(StimItem.Count, w.Players[Human].Grenades);   // never past the most
        Assert.Equal(0, w.TfwGrenadeWait(Human));
    }

    [Fact]
    public void A_revive_clears_the_stims_health_regen_waiver()
    {
        var w = TfwRig.War(config: WithStims(Musou));
        SetPlayer(w, Human, w.Players[Human] with { HealthRegenWaived = true });   // a stim used before the fall
        Assert.True(w.TfwKillForTests(Human));
        Assert.True(w.TfwRevive(Human));
        Assert.True(w.Players[Human].Alive);
        Assert.False(w.Players[Human].HealthRegenWaived);
    }

    [Fact]
    public void Classic_pvp_never_gives_a_stim_charge_back()
    {
        var map = TfwTestArena.Build();
        var cfg = WithStims(Classic);
        var w = new World(cfg, map.Collision, TfwTestArena.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        w.Spawn(1, 0);
        TfwRig.Steps(w, 30);
        SetPlayer(w, 1, w.Players[1] with { Grenades = 0 });
        TfwRig.Seconds(w, TfwSupplyRules.GrenadeRefillSeconds + 1f);
        Assert.Equal(0, w.Players[1].Grenades);
    }

    // ---------------------------------------------------------------- the AI Marines' damage

    [Fact]
    public void An_ai_marine_takes_a_tenth_of_a_hit_and_a_human_all_of_it()
    {
        var w = TfwRig.War(config: Musou);
        int r = w.TfwSpawnAlly(TfwTestArena.LandingZone)!.Value;
        w.Tfw!.PuppetBrain = (_, p) => new PlayerInput(0, 0f, 0f, p.Yaw, InputButtons.None);   // it stands and takes the blows: this test is of the damage alone
        Assert.Equal(TfwSupplyRules.BotDamageTaken, w.DamageTakenScale(r));
        Assert.Equal(1f, w.DamageTakenScale(Human));

        // On the field, before any deploy (nothing else spawns): the human and its retainer held 60 m apart, a Boy beside each that nobody can kill.
        var a = new Vector3(-30f, 0f, 0f);
        var b = new Vector3(30f, 0f, 0f);
        TfwRig.Teleport(w, a);
        Assert.True(w.TfwRespawnAt(r, b, 0f));
        const float boy = 1e6f;   // nobody kills it first
        Assert.NotEqual((ushort)0, w.Musou!.Spawn(MusouEnemyType.Boy, a + new Vector3(-1.2f, 0f, 0f), MathF.PI, boy));
        Assert.NotEqual((ushort)0, w.Musou.Spawn(MusouEnemyType.Boy, b + new Vector3(1.2f, 0f, 0f), MathF.PI, boy));
        var human = new List<float>();
        var retainer = new List<float>();
        for (int t = 0; t < 30 * 20 && (human.Count < 2 || retainer.Count < 2); t++)
        {
            SetPlayer(w, Human, w.Players[Human] with { Position = a });   // both stand where they are
            SetPlayer(w, r, w.Players[r] with { Position = b });
            w.Step(id => new PlayerInput(0, 0f, 0f, w.Players[id].Yaw, InputButtons.None));
            foreach (var hit in w.LastHits)
            {
                if (hit.Shooter != 0) continue;   // an enemy's
                if (hit.Target == Human) human.Add(hit.ShieldDamage + hit.HealthDamage);
                else if (hit.Target == r) retainer.Add(hit.ShieldDamage + hit.HealthDamage);
            }
        }
        Assert.True(human.Count >= 2 && retainer.Count >= 2, $"{human.Count} and {retainer.Count} strikes; retainer {w.Players[r].Alive} at {w.Players[r].Position}, boy targets {w.Musou.TargetOf(1)} {w.Musou.TargetOf(2)}");
        Assert.All(retainer, d => Assert.Equal(human[0] * TfwSupplyRules.BotDamageTaken, d, 3));
        Assert.All(human, d => Assert.Equal(human[0], d, 3));
    }

    [Fact]
    public void A_bot_client_takes_a_tenth_in_musou_and_all_of_it_in_classic_pvp()
    {
        var w = TfwRig.War(config: Musou);
        w.SetBotClient(Human, true);
        Assert.Equal(TfwSupplyRules.BotDamageTaken, w.DamageTakenScale(Human));
        w.SetBotClient(Human, false);
        Assert.Equal(1f, w.DamageTakenScale(Human));
        w.SetBotClient(Human, true);
        w.Remove(Human);
        w.Spawn(Human, w.AssignClass(0));
        Assert.Equal(1f, w.DamageTakenScale(Human));                        // a new join of the id is no bot until the server says so

        var classic = ClassicMatch();
        classic.SetBotClient(1, true);
        Assert.True(classic.IsBotClient(1));
        Assert.Equal(1f, classic.DamageTakenScale(1));                      // Classic PvP: unchanged
    }

    [Fact]
    public void Classic_pvp_never_gives_a_grenade_back_short_or_empty()
    {
        var w = ClassicMatch();
        SetPlayer(w, 1, w.Players[1] with { Grenades = 0 });
        int carried = w.Players[2].Grenades;
        Assert.True(carried > 0);
        SetPlayer(w, 2, w.Players[2] with { Grenades = carried - 1 });
        TfwRig.Seconds(w, 3 * TfwSupplyRules.GrenadeRefillSeconds + 1f);
        Assert.Equal((0, carried - 1), (w.Players[1].Grenades, w.Players[2].Grenades));
        Assert.Equal((0, 0), (w.TfwGrenadeWait(1), w.TfwGrenadeWait(2)));     // no refill counts at all
    }
}
