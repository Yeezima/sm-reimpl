using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The Musou Dreadnought in the war (owner, 2026-11-14: "the player appears as a placeholder ultramarine tactical in the thunderhawk troop bay ... he appears in the dreadnaught once he loads into the ashfall citadel map by
/// deploying. there should never be a dreadnaught in the thunderhawk troop bay"): in the hub a Dreadnought pick plays as the Tactical (its body, capsule, health and kit); the landing makes him the Dreadnought with his
/// entrance (5 s of no damage, the first 4.833 s without move or fire), and every war respawn, a death's respawn and a new round start the entrance again.
/// </summary>
public class DreadnoughtWarTests
{
    static readonly SimConfig Config = MusouLoadout.Prepare(TfwRig.Config);
    static int Dread(SimConfig c) => c.Classes.ToList().FindIndex(k => k.Name == DreadnoughtRules.ClassName);
    const int Human = TfwRig.Human;

    /// <summary>A war with the human in the hub, having picked the Dreadnought (the Setup and the hub's ChangeClass command).</summary>
    static World HubWithPick()
    {
        var w = TfwRig.War(null, Config);
        Assert.Equal(TfwRunPhase.Hub, w.Tfw!.Phase);
        Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = Dread(w.Config) }));
        TfwRig.Command(w, TfwCommand.ChangeClass);
        TfwRig.Steps(w, 3);
        return w;
    }

    static PlayerState Me(World w) => w.Players[Human];

    static void SetPlayer(World w, PlayerState state)
    {
        var field = typeof(World).GetField("_players", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((Dictionary<int, PlayerState>)field.GetValue(w)!)[Human] = state;
    }

    static float Walked(World w, int ticks)
    {
        var from = Me(w).Position;
        for (int k = 0; k < ticks; k++) w.Step(_ => new PlayerInput(0, 0f, 1f, 0f, InputButtons.None));
        return Vector3.Distance(from, Me(w).Position);
    }

    [Fact]
    public void In_the_hub_a_dreadnought_pick_plays_as_the_tactical_with_its_body_capsule_health_and_kit()
    {
        var w = HubWithPick();
        var p = Me(w);
        Assert.False(DreadnoughtRules.IsDreadnought(w.Config, p.Class));
        Assert.Equal(w.Config.Classes[0].Name, w.Config.Classes[p.Class].Name);   // the Tactical
        var view = w.Config.For(p.Class);
        Assert.Equal((w.Config.CapsuleRadius, w.Config.CapsuleHeight, w.Config.HealthMax), (view.CapsuleRadius, view.CapsuleHeight, view.HealthMax));
        Assert.Equal(w.Config.HealthMax, p.Health);
        Assert.Equal((0f, 1f), (w.DreadnoughtEntryLeft(Human), w.DamageTakenScale(Human)));
        Assert.True(Walked(w, 15) > 0.5f);   // not locked
        TfwRig.Seconds(w, 3);
        Assert.False(DreadnoughtRules.IsDreadnought(w.Config, Me(w).Class));   // and it stays so while he is in the bay
    }

    [Fact]
    public void A_direct_join_as_the_dreadnought_in_the_hub_is_the_tactical_too()
    {
        var w = TfwRig.War(null, Config);
        w.Spawn(7, Dread(w.Config));
        w.MusouJoined(7);
        Assert.False(DreadnoughtRules.IsDreadnought(w.Config, w.Players[7].Class));
        Assert.Equal(w.Config.HealthMax, w.Players[7].Health);
        TfwRig.Steps(w, 2);
        Assert.False(DreadnoughtRules.IsDreadnought(w.Config, w.Players[7].Class));
    }

    [Fact]
    public void The_landing_makes_him_the_dreadnought_with_his_body_and_an_entrance_that_locks_move_and_fire_for_the_clip()
    {
        var w = HubWithPick();
        TfwRig.Deploy(w);
        TfwRig.Steps(w, 2);
        var p = Me(w);
        Assert.True(DreadnoughtRules.IsDreadnought(w.Config, p.Class));
        var view = w.Config.For(p.Class);
        Assert.Equal((DreadnoughtRules.CapsuleRadius, DreadnoughtRules.CapsuleHeight), (view.CapsuleRadius, view.CapsuleHeight));
        Assert.Equal(DreadnoughtRules.Health, p.Health);
        Assert.True(w.DreadnoughtEntryLeft(Human) > 4.5f);
        Assert.Equal(0f, w.DamageTakenScale(Human));
        Assert.True(Walked(w, 30 * 3) < 0.05f, "he moved during the entrance clip");
        // After the clip (4.833 s) he moves again, still unhurt until the 5 s are out.
        TfwRig.Seconds(w, 2f);
        Assert.True(Walked(w, 30) > 0.5f);
        Assert.Equal(1f, w.DamageTakenScale(Human));
    }

    [Fact]
    public void Every_war_respawn_starts_the_entrance_again()
    {
        var w = HubWithPick();
        TfwRig.Deploy(w);
        TfwRig.Seconds(w, 6f);
        Assert.Equal((0f, 1f), (w.DreadnoughtEntryLeft(Human), w.DamageTakenScale(Human)));
        TfwRig.Teleport(w, Me(w).Position + new Vector3(2f, 0f, 0f));   // the war's own respawn (extraction, redeploy, a call back to the line)
        Assert.True(DreadnoughtRules.IsDreadnought(w.Config, Me(w).Class));
        Assert.Equal(0f, w.DamageTakenScale(Human));
        TfwRig.Steps(w, 2);
        Assert.True(w.DreadnoughtEntryLeft(Human) > 4.8f);
        Assert.Equal(DreadnoughtRules.Health, Me(w).Health);
    }

    [Fact]
    public void A_new_round_rearms_everyone_and_starts_the_entrance_again()
    {
        var w = HubWithPick();
        TfwRig.Deploy(w);
        TfwRig.Seconds(w, 6f);
        Assert.Equal(0f, w.DreadnoughtEntryLeft(Human));
        typeof(World).GetMethod("MusouRearm", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(w, null);
        Assert.Equal(0f, w.DamageTakenScale(Human));   // no entry yet: he is entering
        TfwRig.Steps(w, 2);
        Assert.True(w.DreadnoughtEntryLeft(Human) > 4.8f);
    }

    [Fact]
    public void Picking_another_class_in_the_hub_forgets_the_pick()
    {
        var w = HubWithPick();
        Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = 0 }));
        TfwRig.Command(w, TfwCommand.ChangeClass);
        TfwRig.Steps(w, 3);
        TfwRig.Deploy(w);
        TfwRig.Steps(w, 2);
        Assert.False(DreadnoughtRules.IsDreadnought(w.Config, Me(w).Class));
    }

    [Fact]
    public void The_big_body_is_solid_to_the_other_players_with_its_own_radius_and_a_classic_config_gives_every_body_the_shared_capsule()
    {
        var w = TfwRig.War(null, Config);
        w.Spawn(2, Dread(w.Config));
        TfwRig.Steps(w, 2);   // the hub: he is a Tactical there
        var dreadnought = Me(w) with { PlayerId = 2, Class = Dread(w.Config) };
        var marine = Me(w) with { PlayerId = 1, Position = Me(w).Position + new Vector3(3f, 0f, 0f) };
        var view = PlayerCollision.See([dreadnought, marine], 1, 10, 10, w.Config)!;
        var other = Assert.Single(view.Bodies);
        Assert.Equal((DreadnoughtRules.CapsuleRadius, DreadnoughtRules.CapsuleHeight), (other.Radius, other.Height));
        var seenByHim = PlayerCollision.See([dreadnought, marine], 2, 10, 10, w.Config)!;
        Assert.Equal((w.Config.CapsuleRadius, MathF.Max(w.Config.CapsuleHeight, 2f * w.Config.CapsuleRadius)), (seenByHim.Bodies[0].Radius, seenByHim.Bodies[0].Height));   // the marine's own radius, so the two add
        // The guard's distance between a marine and him: the larger radius; between two marines: as before.
        Assert.Equal(DreadnoughtRules.CapsuleRadius, SolidBodies.GuardDistance(w.Config, false, 0, Dread(w.Config)));
        Assert.Equal(SolidBodies.GuardDistance(w.Config, false), SolidBodies.GuardDistance(w.Config, false, 0, 0));
        // A config without any capsule of its own: no body carries a radius (the shared capsule, as ever).
        var classic = TfwRig.Config with { Extras = ContentExtras.None };
        var plain = PlayerCollision.See([marine, marine with { PlayerId = 2 }], 1, 10, 10, classic)!;
        Assert.All(plain.Bodies, b => Assert.Equal((0f, 0f), (b.Radius, b.Height)));
        Assert.Equal(SolidBodies.GuardDistance(classic, true), SolidBodies.GuardDistance(classic, true, 0, 0));
    }

    [Fact]
    public void The_landing_puts_the_first_dreadnought_at_the_base_centre_and_spreads_several_while_other_classes_keep_their_places()
    {
        // owner 2026-11-15: "spawn in the middle of the imperial base HQ ... Spread them out a bit if multiple players picked dreadnaught class".
        Assert.Equal(Vector3.Zero, DreadnoughtRules.LandingOffset(0));
        Assert.Equal([0f, 4f, -4f, 8f], Enumerable.Range(0, 4).Select(j => DreadnoughtRules.LandingOffset(j).X).ToArray());
        var w = HubWithPick();                      // player 1 (Human) picked the Dreadnought
        w.Spawn(2, Dread(w.Config)); w.MusouJoined(2);   // a second pick
        w.Spawn(3, 0); w.MusouJoined(3);                  // a Tactical
        TfwRig.Steps(w, 2);
        TfwRig.Deploy(w);
        TfwRig.Steps(w, 2);
        var zone = TfwTestArena.LandingZone;
        Assert.True(DreadnoughtRules.IsDreadnought(w.Config, w.Players[1].Class) && DreadnoughtRules.IsDreadnought(w.Config, w.Players[2].Class));
        Assert.False(DreadnoughtRules.IsDreadnought(w.Config, w.Players[3].Class));
        float Flat(int id) => Vector3.Distance(w.Players[id].Position with { Z = 0f }, zone);
        Assert.True(Flat(1) < 0.75f, $"the first Dreadnought at the base's centre: {w.Players[1].Position}");
        float apart = Vector3.Distance(w.Players[1].Position with { Z = 0f }, w.Players[2].Position with { Z = 0f });
        Assert.InRange(apart, 3f, 5f);
        Assert.True(Flat(3) < 6f, "the Tactical stays by the base in its own slot");
    }
}
