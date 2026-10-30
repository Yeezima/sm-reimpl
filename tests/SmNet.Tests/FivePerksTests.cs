using System.Numerics;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Protocol 44, the five perks that had no effect (owner's decisions of 2026-10-30): Rapid Deployment (3 s respawn), Weapon Versatility (a third weapon slot, key 3,
/// Tactical only, validated by the server), Teleport Homer (allies respawn next to a living carrier), Tactical Readout and Signum Link (enemy health and armour bars;
/// nothing over enemies without them). Synthetic classes and weapons only.
/// </summary>
public class FivePerksTests(ITestOutputHelper output)
{
    const int SmTac = 0, SmAssault = 1, CsmTac = 2;
    const int Rifle = 0, Sidearm = 1, RifleB = 2;
    // Perk indices in the config.
    const int Rapid = 0, Second = 1, Homer = 2, Readout = 3, Signum = 4, Plain = 5;

    static WeaponSpec Gun(string name, WeaponSlot slot, int clip) => new(name, slot, TriggerKind.SemiAuto, 20f, 0.2f, clip, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);

    static ClassLoadout Tactical(string name, Team side) => new(name, Rifle, Sidearm, SimConfig.PlaceholderMeleeSteps)
    { PrimaryChoices = [Rifle, RifleB], PistolChoices = [Sidearm], Side = side, Role = "tactical", PerkChoices = [Plain, Rapid, Second, Homer, Readout, Signum], DefaultPerk = Plain };
    static ClassLoadout Assault(string name, Team side) => new(name, -1, Sidearm, SimConfig.PlaceholderMeleeSteps)
    { PistolChoices = [Sidearm], Side = side, Role = "assault", PerkChoices = [Plain, Rapid, Second, Homer, Readout, Signum], DefaultPerk = Plain };

    static readonly SimConfig Config = new()
    {
        Weapons = [Gun("test_rifle", WeaponSlot.Ranged, 30), Gun("test_sidearm", WeaponSlot.Pistol, 10), Gun("test_rifle_b", WeaponSlot.Ranged, 20)],
        Classes = [Tactical("sm - tactical", Team.SpaceMarines), Assault("sm - assault marine", Team.SpaceMarines), Tactical("csm - tactical", Team.Chaos)],
        Perks =
        [
            new PerkSpec(Loadouts.RapidDeploymentPerk, null, PerkKind.Class, 1), new PerkSpec(Loadouts.SecondWeaponPerk, null, PerkKind.Class, 13),
            new PerkSpec(Loadouts.TeleportHomerPerk, null, PerkKind.Class, 1), new PerkSpec(Loadouts.TacticalReadoutPerk, null, PerkKind.Class, 1),
            new PerkSpec(Loadouts.SignumLinkPerk, null, PerkKind.Class, 1), new PerkSpec("mp_perk_plain", null, PerkKind.Class, 1),
        ],
        HealthMax = 40f, ShieldMax = 0f, HealthRegenDelay = 1e6f, RespawnTime = 5f, MaxRewindTicks = 40,
    };
    static readonly MatchRules Rules = new() { TimeLimit = 0f };

    // Choice indices into the class's PerkChoices list [Plain, Rapid, Second, Homer, Readout, Signum].
    const int ChPlain = 0, ChRapid = 1, ChSecond = 2, ChHomer = 3, ChReadout = 4, ChSignum = 5;

    static PlayerInput Idle(int id) => new(0, 0, 0, MathF.PI, InputButtons.None);
    static Func<int, PlayerInput> Only1(InputButtons buttons, float yaw = 0f) =>
        id => id == 1 ? new PlayerInput(0, 0, 0, yaw, buttons) : Idle(id);

    /// <summary>Player 1 fires at <paramref name="target"/> (turned toward it) until it dies.</summary>
    static void ShootDead(World w, int target)
    {
        var d = w.Players[target].Position - w.Players[1].Position;
        float yaw = MathF.Atan2(-d.X, d.Y);
        for (int k = 0; k < 400 && w.Players[target].Alive; k++) w.Step(Only1(k % 2 == 0 ? InputButtons.Fire : InputButtons.None, yaw));
        Assert.False(w.Players[target].Alive);
    }

    // ---------------------------------------------------------------- Rapid Deployment

    [Fact]
    public void Rapid_Deployment_shortens_the_respawn_to_three_seconds_and_the_base_stays()
    {
        SpawnPoint[] points = [new(new Vector3(0, 0, 0), 0f), new(new Vector3(0, 5, 0), 0f), new(new Vector3(60, 60, 0), 0f), new(new Vector3(-60, 60, 0), 0f)];
        var w = new World(Config, null, points) { Rules = Rules };
        w.Spawn(1, SmTac);
        w.Spawn(2, CsmTac, perk1Choice: ChRapid);
        w.Spawn(3, CsmTac);
        w.Step(Idle);
        Assert.Equal(3f, Loadouts.RespawnTime(Config, w.Players[2]));
        Assert.Equal(5f, Loadouts.RespawnTime(Config, w.Players[3]));
        Assert.Equal(5f, Config.RespawnTime);   // the base is unchanged
        // A Rapid Deployment player alone at the front dies and waits 3 s.
        var shot = new World(Config, null, [new(new Vector3(0, 0, 0), 0f), new(new Vector3(0, 5, 0), 0f)]) { Rules = Rules };
        shot.Spawn(1, SmTac); shot.Spawn(2, CsmTac, perk1Choice: ChRapid);
        shot.Step(Idle);
        ShootDead(shot, 2);
        Assert.Equal(3f, shot.Players[2].RespawnLeft, 3);
        int ticks = 0;
        while (!shot.Players[2].Alive && ticks < 400) { shot.Step(Idle); ticks++; }
        output.WriteLine($"respawned after {ticks} ticks");
        Assert.InRange(ticks, 3 * Config.TickRate - 1, 3 * Config.TickRate + 2);
    }

    // ---------------------------------------------------------------- Weapon Versatility

    [Fact]
    public void The_server_keeps_a_second_primary_only_for_a_Tactical_with_Weapon_Versatility()
    {
        var withPerk = new LoadoutChoice(SmTac, Perk1: ChSecond, Second: 1);
        Assert.Equal(1, Loadouts.Resolve(Config, SmTac, withPerk).Second);
        Assert.Equal(1, Loadouts.Resolve(Config, SmTac, withPerk with { Perk1 = -1, Perk2 = ChSecond }).Second);   // either perk slot
        Assert.Equal(-1, Loadouts.Resolve(Config, SmTac, withPerk with { Perk1 = ChRapid }).Second);                // another perk: none
        Assert.Equal(-1, Loadouts.Resolve(Config, SmTac, withPerk with { Perk1 = -1 }).Second);                      // no perk
        Assert.Equal(-1, Loadouts.Resolve(Config, SmTac, withPerk with { Second = 99 }).Second);                     // not one of the class's primaries
        Assert.Equal(-1, Loadouts.Resolve(Config, SmAssault, withPerk with { Class = SmAssault }).Second);           // not a Tactical, even with the perk
        // The world's spawn applies the same rule.
        var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f)]) { Rules = Rules };
        Assert.Equal(RifleB, w.Spawn(1, SmTac, perk1Choice: ChSecond, secondChoice: 1).Second);
        Assert.Equal(-1, w.Spawn(2, SmTac, secondChoice: 1).Second);
        Assert.Equal(-1, w.Spawn(3, SmAssault, perk1Choice: ChSecond, secondChoice: 1).Second);
        // A change request resolves at the next respawn.
        w.SetNextLoadout(2, new LoadoutChoice(SmTac, Perk1: ChSecond, Second: 0));
        Assert.Equal(Rifle, w.NextLoadout(2)!.Value.Second >= 0 ? Loadouts.Choices(Config, SmTac, WeaponSlot.Second)[w.NextLoadout(2)!.Value.Second] : -1);
    }

    [Fact]
    public void Key_3_puts_the_second_primary_in_hand_and_every_weapon_keeps_its_ammunition()
    {
        var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f)]) { Rules = Rules };
        var s = w.Spawn(1, SmTac, perk1Choice: ChSecond, secondChoice: 1);
        Assert.Equal((WeaponSlot.Ranged, 30, 10, 20), (s.Slot, s.Clip, s.OtherClip, s.ThirdClip));   // hand: primary; first holstered: pistol; second holstered: the second primary
        void Press(InputButtons b) { w.Step(Only1(b)); for (int k = 0; k < 60; k++) w.Step(Only1(InputButtons.None)); }
        Press(InputButtons.SlotSecond);
        s = w.Players[1];
        Assert.Equal(WeaponSlot.Second, s.Slot);
        Assert.Equal(RifleB, Loadouts.InHand(Config, s)!.Name == "test_rifle_b" ? RifleB : -1);
        Assert.Equal((20, 10, 30), (s.Clip, s.OtherClip, s.ThirdClip));
        Press(InputButtons.SlotPistol);
        s = w.Players[1];
        Assert.Equal((WeaponSlot.Pistol, 10, 30, 20), (s.Slot, s.Clip, s.OtherClip, s.ThirdClip));
        Press(InputButtons.SlotPrimary);
        s = w.Players[1];
        Assert.Equal((WeaponSlot.Ranged, 30, 10, 20), (s.Slot, s.Clip, s.OtherClip, s.ThirdClip));
        // The toggle cycles pistol, primary, second primary.
        Press(InputButtons.SlotSecond);
        Press(InputButtons.Switch);
        Assert.Equal(WeaponSlot.Pistol, w.Players[1].Slot);
        Press(InputButtons.Switch); Assert.Equal(WeaponSlot.Ranged, w.Players[1].Slot);
        Press(InputButtons.Switch); Assert.Equal(WeaponSlot.Second, w.Players[1].Slot);
        // A shot spends the clip of the weapon in hand only.
        Press(InputButtons.SlotSecond);
        for (int k = 0; k < 4; k++) { w.Step(Only1(InputButtons.Fire)); w.Step(Only1(InputButtons.None)); }
        s = w.Players[1];
        Assert.True(s.Clip < 20 && s.OtherClip == 10 && s.ThirdClip == 30, $"{s.Clip}/{s.OtherClip}/{s.ThirdClip}");
    }

    [Fact]
    public void Key_3_does_nothing_without_a_second_primary()
    {
        var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f)]) { Rules = Rules };
        w.Spawn(1, SmTac);
        w.Step(Only1(InputButtons.SlotSecond));
        for (int k = 0; k < 40; k++) w.Step(Only1(InputButtons.None));
        Assert.Equal(WeaponSlot.Ranged, w.Players[1].Slot);
    }

    [Fact]
    public void The_snapshot_carries_the_third_weapon_and_stays_within_1200_bytes()
    {
        var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f)]) { Rules = Rules };
        w.Spawn(1, SmTac, perk1Choice: ChSecond, secondChoice: 1);
        for (int slot = 0; slot < 3; slot++)
        {
            var key = slot == 0 ? InputButtons.SlotPrimary : slot == 1 ? InputButtons.SlotPistol : InputButtons.SlotSecond;
            w.Step(Only1(key)); for (int k = 0; k < 60; k++) w.Step(Only1(InputButtons.None));
            var me = w.Players[1] with { ThirdHeat = 4f, ThirdSinceHeat = 1.5f, ThirdOverheated = true, SlotSecondHeld = true };
            var bytes = Protocol.Write(new Snapshot(5, 4, 1, [me], Self: 1));
            var back = Assert.IsType<Snapshot>(Protocol.Read(bytes)).Players[0];
            Assert.Equal((me.Slot, me.Primary, me.Pistol, me.Second), (back.Slot, back.Primary, back.Pistol, back.Second));
            Assert.Equal((me.Clip, me.OtherClip, me.ThirdClip, me.ThirdReserve), (back.Clip, back.OtherClip, back.ThirdClip, back.ThirdReserve));
            Assert.Equal((4f, 1.5f, true, true), (back.ThirdHeat, back.ThirdSinceHeat, back.ThirdOverheated, back.SlotSecondHeld));
        }
        Assert.True(Protocol.MaxSnapshotBytes <= Protocol.SnapshotBudget);
        var hello = new Hello(Protocol.Version, 1, 2, 3, RequestedClass: 0, RequestedSecond: 1);
        Assert.Equal(1, Assert.IsType<Hello>(Protocol.Read(Protocol.Write(hello))).Loadout.Second);
        var input = new PlayerInput(3, 0, 0, 0, InputButtons.SlotSecond);
        Assert.Equal(InputButtons.SlotSecond, Assert.IsType<InputBatch>(Protocol.Read(Protocol.Write(new InputBatch([input], 1, 2)))).Inputs[0].Buttons);
    }

    // ---------------------------------------------------------------- Teleport Homer

    /// <summary>A world whose players are placed by the rotation over <paramref name="points"/> (no rules yet), then the match rules on and play begun.</summary>
    static World Arena(SimConfig config, SpawnPoint[] points, params (int Id, int Class, int Perk)[] players)
    {
        var w = new World(config, null, points) { Rules = Rules };
        foreach (var (id, cls, perk) in players) w.Spawn(id, cls, perk1Choice: perk);
        for (int k = 0; k < 400 && w.State != MatchState.Playing; k++) w.Step(Idle);
        Assert.Equal(MatchState.Playing, w.State);
        return w;
    }

    [Fact]
    public void An_ally_respawns_next_to_a_living_Teleport_Homer_carrier_and_normally_when_none_lives()
    {
        var fast = Config with { RespawnTime = 0.5f };
        SpawnPoint[] points = [new(new Vector3(0, 0, 0), 0f), new(new Vector3(0, 5, 0), 0f), new(new Vector3(100, 100, 0), 1.25f), new(new Vector3(-100, 100, 0), 0f)];
        var w = Arena(fast, points, (1, SmTac, -1), (2, CsmTac, -1), (3, CsmTac, ChHomer));
        ShootDead(w, 2);
        for (int k = 0; k < 40 && !w.Players[2].Alive; k++) w.Step(Idle);
        var back = w.Players[2];
        Assert.True(back.Alive);
        float d = Vector3.Distance(back.Position, w.Players[3].Position);
        output.WriteLine($"carrier at {w.Players[3].Position}, respawned at {back.Position}, {d:0.0} m");
        Assert.InRange(d, 1.0f, 3.2f);
        Assert.Equal(w.Players[3].Yaw, back.Yaw, 4);
        // A Space Marine respawning does not go to a Chaos carrier: shoot nothing, kill the shooter's side by the other way round is not needed; the rule is by side.
        // No perk on the Chaos ally: the victim respawns at a map spawn point, as without the perk.
        var w2 = Arena(fast, points, (1, SmTac, -1), (2, CsmTac, -1), (3, CsmTac, -1));
        ShootDead(w2, 2);
        for (int k = 0; k < 200 && !w2.Players[2].Alive; k++) w2.Step(Idle);
        Assert.True(w2.Players[2].Alive);
        Assert.Contains(points, p => p.Position == w2.Players[2].Position);
    }

    [Fact]
    public void A_dead_carrier_is_no_spawn_point()
    {
        var cfg = Config with { RespawnTime = 5f };   // the carrier (no Rapid Deployment) stays dead for 5 s; the victim, with it, returns after 3 s
        SpawnPoint[] points = [new(new Vector3(0, 0, 0), 0f), new(new Vector3(0, 5, 0), 0f), new(new Vector3(0, 9, 0), 0f), new(new Vector3(200, 0, 0), 0f), new(new Vector3(-200, 0, 0), 0f)];
        var w = Arena(cfg, points, (1, SmTac, -1), (3, CsmTac, ChHomer), (2, CsmTac, ChRapid));
        ShootDead(w, 3);                                                   // the carrier dies first
        ShootDead(w, 2);                                                   // then the victim
        for (int k = 0; k < 6 * cfg.TickRate && !w.Players[2].Alive; k++) w.Step(Idle);
        Assert.True(w.Players[2].Alive);
        Assert.False(w.Players[3].Alive);                                  // the carrier still waits
        Assert.Contains(points, p => p.Position == w.Players[2].Position); // so a spawn point, not next to the corpse
    }

    // ---------------------------------------------------------------- Tactical Readout and Signum Link

    [Fact]
    public void Enemy_bars_travel_only_to_players_with_Tactical_Readout_or_Signum_Link_and_never_for_allies()
    {
        var options = new ServerOptions { Match = new MatchRules { TimeLimit = 0f, ScoreLimit = 1000 } };
        var h = new Harness(LinkConditions.Perfect, seed: 3, clients: 0, Config, options);
        var readout = h.AddClient(options: new ClientOptions { RequestedClass = SmTac, RequestedPerk1 = ChReadout }, nonce: 1);
        var plain = h.AddClient(options: new ClientOptions { RequestedClass = SmTac }, nonce: 3);
        var signum = h.AddClient(options: new ClientOptions { RequestedClass = CsmTac, RequestedPerk1 = ChSignum }, nonce: 2);
        var enemy = h.AddClient(options: new ClientOptions { RequestedClass = CsmTac }, nonce: 4);
        Assert.True(CustomFixture.RunUntil(h, () => h.Clients.All(c => c.Status == ClientStatus.Connected), TimeSpan.FromSeconds(5)));
        h.Run(TimeSpan.FromSeconds(1));
        // The server balances the sides by join order: the expectation follows the teams as they are.
        var team = (GameClient c) => h.Server.World.Players[c.PlayerId].Team;
        int[] EnemiesOf(GameClient me) => h.Clients.Where(c => team(c) != team(me)).Select(c => c.PlayerId).Order().ToArray();
        int[] AlliesOf(GameClient me) => h.Clients.Where(c => c != me && team(c) == team(me)).Select(c => c.PlayerId).Order().ToArray();
        var bars = h.Server.BarsFor(readout.PlayerId);
        Assert.NotNull(bars);
        Assert.NotEmpty(EnemiesOf(readout));
        Assert.Equal(EnemiesOf(readout), bars!.Entries.Select(e => e.PlayerId).Order());     // the enemies only: no allies
        Assert.All(bars.Entries, e => Assert.Equal(255, e.Health));                          // full health
        Assert.Equal(EnemiesOf(signum), h.Server.BarsFor(signum.PlayerId)!.Entries.Select(e => e.PlayerId).Order());   // Signum Link: the same for now
        Assert.Null(h.Server.BarsFor(plain.PlayerId));                                       // without either perk: nothing is sent
        Assert.Null(h.Server.BarsFor(enemy.PlayerId));
        Assert.True(readout.HasEnemyBars && signum.HasEnemyBars);
        foreach (int id in EnemiesOf(readout)) Assert.NotNull(readout.EnemyBarFor(id));
        foreach (int id in AlliesOf(readout)) Assert.Null(readout.EnemyBarFor(id));          // an ally has no bar
        Assert.False(plain.HasEnemyBars);                                                    // nothing shows over enemies
        foreach (int id in EnemiesOf(plain)) Assert.Null(plain.EnemyBarFor(id));
        Assert.Null(enemy.EnemyBarFor(readout.PlayerId));
        // The wire round trip, and a duplicate or null id is refused.
        var message = new EnemyBars(9, [new EnemyBarView(4, 128, 7), new EnemyBarView(5, 0, 255)]);
        Assert.Equal(message.Entries, Assert.IsType<EnemyBars>(Protocol.Read(Protocol.Write(message))).Entries);
        var bytes = Protocol.Write(message);
        bytes[^4] = 4; bytes[^3] = 0;   // the second record names player 4 again
        Assert.Null(Protocol.Read(bytes));
    }

    [Fact]
    public void An_enemy_bar_is_sent_only_for_an_enemy_in_range_and_not_behind_a_wall()
    {
        var v = new[] { new Vector3(10f, -20, -1), new Vector3(10f, 20, -1), new Vector3(10f, 20, 10), new Vector3(10f, -20, 10) };
        var wall = new StaticMesh(v, [0, 1, 2, 0, 2, 3]);
        var open = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f)]);
        var walled = new World(Config, new CollisionWorld(wall, wall), [new SpawnPoint(Vector3.Zero, 0f)]);
        PlayerState At(float x) => new PlayerState(2, new Vector3(x, 0, 0), Vector3.Zero, 0f, true) { Alive = true };
        var me = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Alive = true };
        Assert.True(GameServer.BarVisible(open, me, At(40f)));
        Assert.False(GameServer.BarVisible(open, me, At(80f)));        // beyond 75 m
        Assert.True(GameServer.BarVisible(walled, me, At(5f)));        // before the wall
        Assert.False(GameServer.BarVisible(walled, me, At(20f)));      // behind it
    }
}
