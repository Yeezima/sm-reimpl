using System.Net;
using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit;

namespace SmNet.Tests;

/// <summary>
/// devtools-god-custommap (protocol 95; the owner 2026-11-28: "make only the player invincible (god mode)"): the F6 panel's god mode protects only the player who pressed it, exactly as Invincible
/// protects everyone (no damage from shots, blasts, its own grenade included, melee or charges; KILL ME still kills), while bots and other players still take damage. It is the player's own: kept
/// across respawns and restarts, dropped when the player leaves or the id joins anew. Also the protocol 95 DevState and the two new commands on the wire. Synthetic data only.
/// </summary>
public sealed class DevGodModeTests
{
    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(2), TimeSpan.Zero, 0);

    // ---- the wire ----

    [Fact]
    public void Protocol_95_carries_the_own_god_mode_the_file_pick_flag_and_two_short_texts()
    {
        Assert.Equal(95, Protocol.Version);
        var plain = new DevState(DevToolsFlags.Invincible, 1, 2, 3, 4, DevNote.Applied, 7);
        Assert.Equal(plain, Protocol.Read(Protocol.Write(plain)));
        Assert.Equal(Protocol.DevStateBytes, Protocol.Write(plain).Length);
        Assert.Equal(18, Protocol.DevStateBytes);
        var full = plain with { God = true, CanPickFiles = true, Note = DevNote.MapModeFallback, Text = "Starting arena.smmap in Annihilation: ü", NextMapFile = "arena.smmap" };
        Assert.Equal(full, Protocol.Read(Protocol.Write(full)));
        foreach (var kind in new[] { DevCommandKind.SetGod, DevCommandKind.PickMapFile })
        {
            var cmd = new DevCommand(5u, kind, 3, true, 0xBEEF);
            Assert.Equal(cmd, Protocol.Read(Protocol.Write(cmd)));
        }
        // a long text is cut at a character boundary, a long file name is not sent at all, unknown self bits are refused
        var longText = Assert.IsType<DevState>(Protocol.Read(Protocol.Write(plain with { Text = new string('é', 150) })));
        Assert.Equal(Protocol.MaxDevText / 2, longText.Text.Length);
        Assert.Equal("", Assert.IsType<DevState>(Protocol.Read(Protocol.Write(plain with { NextMapFile = new string('a', 130) + ".smmap" }))).NextMapFile);
        var badSelf = Protocol.Write(plain); badSelf[Protocol.DevStateBytes - 3] = 4;
        Assert.Null(Protocol.Read(badSelf));
        var badKind = Protocol.Write(new DevCommand(5u, DevCommandKind.PickMapFile, 0, true)); badKind[5] = 18;
        Assert.Null(Protocol.Read(badKind));
    }

    // ---- the world ----

    static readonly SimConfig Bolter = new()
    {
        Damage = 22f, ShotCooldown = 0.133f, ClipSize = 30, ReserveSize = 270, ReloadTime = 1.3f, MaxRange = 250f,
        FallOffStart = 45f, FallOffEnd = 70f, FallOffEndMultiplier = 0.8f,
        HeadMultiplier = 2.55f, BodyMultiplier = 1f, ShieldHeadMultiplier = 0.5f, ShieldBodyMultiplier = 0.505f, MaxRewindTicks = 8,
    };

    /// <summary><paramref name="shooter"/> shoots <paramref name="target"/> once in a two-player world; the target's shield and health afterwards.</summary>
    static (float Shield, float Health) Shot(World world, int shooter, int target)
    {
        PlayerInput Idle(int id) => new(0, 0, 0, 0, InputButtons.None);
        world.Step(Idle);
        var eye = world.Players[shooter].Position + new Vector3(0, 0, Bolter.EyeHeight);
        var aim = Vector3.Normalize(world.Players[target].Position + new Vector3(0, 0, 1.0f) - eye);
        float yaw = MathF.Atan2(-aim.X, aim.Y), pitch = MathF.Asin(aim.Z);
        world.Step(id => id == shooter ? new PlayerInput(0, 0, 0, yaw, InputButtons.Fire, pitch, world.Tick) : Idle(id));
        for (int i = 0; i < 10; i++) world.Step(Idle);   // past the shot cooldown
        return (world.Players[target].Shield, world.Players[target].Health);
    }

    static World Duel()
    {
        var world = new World(Bolter);
        world.Spawn(1); world.Spawn(2);
        return world;
    }

    [Fact]
    public void God_mode_protects_only_its_owner_and_off_it_hurts_again()
    {
        var world = Duel();
        Assert.True(world.SetDevGod(1, true));
        Assert.False(world.SetDevGod(1, true));   // already on: nothing changed
        Assert.Equal(0f, world.DamageTakenScale(1));
        Assert.Equal(1f, world.DamageTakenScale(2));
        Assert.Equal((Bolter.ShieldMax, Bolter.HealthMax), Shot(world, 2, 1));   // the owner takes nothing
        var other = Shot(world, 1, 2);
        Assert.True(other.Shield < Bolter.ShieldMax, "the other player still takes damage");
        Assert.Equal(DevToolsFlags.None, world.DevFlags);   // the all-players switch is untouched
        world.SetDevGod(1, false);
        Assert.False(world.IsDevGod(1));
        Assert.True(Shot(world, 2, 1).Shield < Bolter.ShieldMax, "off: the owner takes damage again");
    }

    [Fact]
    public void God_mode_holds_against_the_owners_own_grenade_while_a_player_beside_it_is_hurt()
    {
        var config = GrenadeTests.Config;
        var world = new World(config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 3, 0), MathF.PI)]);
        world.Spawn(1, 0, equipmentChoice: 0);   // a frag
        world.Spawn(2, 1);
        world.SetDevGod(1, true);
        var hits = new List<HitEvent>();
        PlayerInput Thrower(int id, InputButtons b) => new(0, 0, 0, id == 1 ? 0f : MathF.PI, id == 1 ? b : InputButtons.None, id == 1 ? -1.3f : 0f);   // steeply at its own feet
        world.Step(id => Thrower(id, InputButtons.Grenade));
        for (int k = 0; k < 400 && !hits.Any(h => h.Splash); k++) { world.Step(id => Thrower(id, InputButtons.None)); hits.AddRange(world.LastHits); }
        Assert.Contains(hits, h => h.Splash && h.Target == 2 && h.ShieldDamage + h.HealthDamage > 0f);
        Assert.DoesNotContain(hits, h => h.Target == 1 && h.ShieldDamage + h.HealthDamage > 0f);
        Assert.Equal((config.ShieldMax, config.HealthMax), (world.Players[1].Shield, world.Players[1].Health));
    }

    [Fact]
    public void Kill_me_still_kills_a_god_and_god_mode_survives_the_respawn_but_not_a_new_join_or_leaving()
    {
        var world = Duel();
        world.SetDevGod(1, true);
        Assert.True(world.DevKill(1));
        PlayerInput Idle(int id) => new(0, 0, 0, 0, InputButtons.None);
        world.Step(Idle);
        Assert.False(world.Players[1].Alive);
        for (int i = 0; i < 30 * 30 && !world.Players[1].Alive; i++) world.Step(Idle);
        Assert.True(world.Players[1].Alive, "respawned");
        Assert.True(world.IsDevGod(1), "kept across the respawn");
        Assert.Equal((Bolter.ShieldMax, Bolter.HealthMax), Shot(world, 2, 1));
        world.Spawn(1);   // the id joins anew (a new player)
        Assert.False(world.IsDevGod(1));
        world.SetDevGod(2, true);
        world.Remove(2);
        Assert.Empty(world.DevGodPlayers);
    }

    // ---- the server ----

    static (Harness H, List<string> Log) Make(DevToolsSetup? setup)
    {
        var h = new Harness(Link, seed: 5, 2, serverOptions: new ServerOptions { DevTools = setup });
        var log = new List<string>();
        h.Server.Log += log.Add;
        h.Run(TimeSpan.FromSeconds(2));
        return (h, log);
    }

    [Fact]
    public void The_panel_sets_god_mode_for_its_own_player_only_and_each_client_is_told_its_own()
    {
        var (h, log) = Make(new DevToolsSetup(IPAddress.Loopback, null));
        var (me, other) = (h.Clients[0], h.Clients[1]);
        Assert.True(me.DevActive);
        me.SendDevCommand(DevCommandKind.SetGod, 0, true);
        h.Run(TimeSpan.FromSeconds(0.3));
        Assert.True(h.Server.World.IsDevGod(me.PlayerId));
        Assert.False(h.Server.World.IsDevGod(other.PlayerId));
        Assert.Equal(DevToolsFlags.None, h.Server.World.DevFlags);
        Assert.True(me.Dev!.God);
        Assert.False(other.Dev!.God);
        Assert.Equal(DevNote.Applied, me.Dev.Note);
        Assert.Contains(log, l => l == $"dev tools: god mode ON for player {me.PlayerId} only");
        Assert.Equal(0f, h.Server.World.DamageTakenScale(me.PlayerId));
        Assert.Equal(1f, h.Server.World.DamageTakenScale(other.PlayerId));
        me.SendDevCommand(DevCommandKind.SetGod, 0, false);
        h.Run(TimeSpan.FromSeconds(0.3));
        Assert.False(h.Server.World.IsDevGod(me.PlayerId));
        Assert.False(me.Dev!.God);
    }

    [Fact]
    public void A_bot_cannot_take_god_mode_and_without_the_tools_nothing_happens()
    {
        var (h, _) = Make(new DevToolsSetup(IPAddress.Loopback, null));
        var bot = h.AddClient(nonce: 77, options: new ClientOptions { Bot = true });
        h.Run(TimeSpan.FromSeconds(1));
        bot.SendDevCommand(DevCommandKind.SetGod, 0, true);
        h.Run(TimeSpan.FromSeconds(0.3));
        Assert.Empty(h.Server.World.DevGodPlayers);

        var (off, log) = Make(null);
        off.Clients[0].SendDevCommand(DevCommandKind.SetGod, 0, true);
        off.Run(TimeSpan.FromSeconds(0.3));
        Assert.Empty(off.Server.World.DevGodPlayers);
        Assert.Contains(log, l => l.StartsWith("dev tools request ignored"));
    }
}
